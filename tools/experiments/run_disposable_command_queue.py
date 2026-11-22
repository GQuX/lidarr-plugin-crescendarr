#!/usr/bin/env python3
"""M16 (issue #44): stock Lidarr command-queue disk-access serialization with ONE additive test plugin.

CI-only, Linux. Starts the SHA-256-pinned official Lidarr v3.1.6.5079 binary with a fresh, empty data
folder under RUNNER_TEMP, a disposable synthetic root folder holding a single text file (no audio, so no
metadata lookups), and the test-only queue probe plugin. Everything is driven through the stock HTTP API
and the stock command queue. The probe only blocks on test-owned barrier files and logs.

Modes:
  queue          registration, forward/reverse serialization, direct-scan bypass, dedupe, failure path,
                 SIGKILL restart with persisted queued/started plugin commands.
  guard-negative plugin installed WITHOUT opt-in: it must refuse to run.

Every wait has a hard deadline and fails with the evidence collected so far.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
from datetime import datetime
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

PORT = 18787
STATUS = {0: "queued", 1: "started", 2: "completed", 3: "failed", 4: "aborted", 5: "cancelled", 6: "orphaned"}
EVIDENCE: list[str] = []


class Failure(Exception):
    pass


def note(line: str) -> None:
    EVIDENCE.append(line)
    print(line, flush=True)


def check(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)
    note(f"CHECK_OK {message}")


def require_ci() -> Path:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable host experiment requires a GitHub Actions CI runner.")
    runner_temp = os.environ.get("RUNNER_TEMP", "")
    if not runner_temp or not Path(runner_temp).is_dir():
        raise RuntimeError("RUNNER_TEMP must exist.")
    return Path(runner_temp).resolve()


class Host:
    def __init__(self, bin_dir: Path, data: Path, env: dict[str, str], log: Path):
        self.bin_dir, self.data, self.env, self.log = bin_dir, data, env, log
        self.process: subprocess.Popen | None = None
        self.api_key = ""

    def start(self) -> None:
        handle = self.log.open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            [str(self.bin_dir / "Lidarr"), f"-data={self.data}", "-nobrowser", "-exitimmediately"],
            cwd=self.bin_dir, stdout=handle, stderr=subprocess.STDOUT, env=self.env, start_new_session=True,
        )

    def text(self) -> str:
        return self.log.read_text(encoding="utf-8", errors="replace")

    def host_log(self) -> str:
        """Stock NLog file target; stock messages are not written to stdout on this runner."""
        path = self.data / "logs" / "lidarr.txt"
        return path.read_text(encoding="utf-8", errors="replace") if path.exists() else ""

    def wait_ready(self, marker: str, deadline: float = 120) -> None:
        limit = time.monotonic() + deadline
        while time.monotonic() < limit:
            if self.process and self.process.poll() is not None:
                raise Failure(f"host exited early rc={self.process.returncode}")
            if marker in self.text() and self.ping():
                key = re.search(r"<ApiKey>([0-9a-f]+)</ApiKey>", (self.data / "config.xml").read_text())
                if key:
                    self.api_key = key.group(1)
                    return
            time.sleep(0.5)
        raise Failure(f"host not ready with marker {marker!r} within {deadline}s")

    def ping(self) -> bool:
        try:
            with urlopen(f"http://127.0.0.1:{PORT}/ping", timeout=1.5) as response:
                return response.status == 200
        except (HTTPError, URLError, TimeoutError, ConnectionError):
            return False

    def api(self, method: str, path: str, body: dict | None = None):
        data = None if body is None else json.dumps(body).encode()
        request = Request(f"http://127.0.0.1:{PORT}/api/v1/{path}", data=data, method=method,
                          headers={"X-Api-Key": self.api_key, "Content-Type": "application/json"})
        try:
            with urlopen(request, timeout=15) as response:
                raw = response.read()
                return json.loads(raw) if raw else None
        except HTTPError as error:
            raise Failure(f"{method} {path} -> HTTP {error.code}: {error.read()[:400]!r}") from error

    def command(self, body: dict) -> dict:
        resource = self.api("POST", "command", body)
        note(f"PUSH name={body['name']} label={body.get('label', '')} -> id={resource['id']} status={status(resource)}")
        return resource

    def get(self, command_id: int) -> dict:
        return self.api("GET", f"command/{command_id}")

    def wait_status(self, command_id: int, wanted: set[str], deadline: float = 60) -> dict:
        limit = time.monotonic() + deadline
        last = None
        while time.monotonic() < limit:
            last = self.get(command_id)
            if status(last) in wanted:
                return last
            time.sleep(0.2)
        raise Failure(f"command {command_id} never reached {wanted}; last={last and status(last)}")

    def wait_log(self, pattern: str, deadline: float = 60) -> str:
        limit = time.monotonic() + deadline
        while time.monotonic() < limit:
            match = re.search(pattern, self.text())
            if match:
                return match.group(0)
            time.sleep(0.2)
        raise Failure(f"log pattern {pattern!r} not seen within {deadline}s")

    def kill(self) -> None:
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL)
            self.process.wait(timeout=15)
        note(f"HOST_SIGKILL rc={self.process.returncode if self.process else None}")

    def stop(self) -> None:
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=10)
        note(f"HOST_STOPPED rc={self.process.returncode if self.process else None}")


def status(resource: dict) -> str:
    value = resource.get("status")
    return STATUS.get(value, str(value).lower()) if isinstance(value, int) else str(value).lower()


def release(barriers: Path, name: str) -> None:
    (barriers / name).write_text("released\n", encoding="utf-8")
    note(f"BARRIER {name}")


def stays(host: Host, command_id: int, wanted: str, seconds: float = 4) -> None:
    """Observe a state for a window; the decisive ordering proof is the timestamp check afterwards."""
    limit = time.monotonic() + seconds
    while time.monotonic() < limit:
        current = status(host.get(command_id))
        if current != wanted:
            raise Failure(f"command {command_id} left {wanted!r} early: {current}")
        time.sleep(0.25)
    note(f"STAYED id={command_id} status={wanted} window={seconds}s")


def ts(value: str | None) -> datetime | None:
    if not value:
        return None
    value = value.rstrip("Z")
    if "." in value:
        head, frac = value.split(".", 1)
        value = f"{head}.{frac[:6]}"
    return datetime.fromisoformat(value)


def snapshot_db(data: Path, destination: Path) -> list[dict]:
    """Copy lidarr.db (+WAL) and read the Commands table from the COPY, never the live file."""
    destination.mkdir(parents=True, exist_ok=True)
    for suffix in ("", "-wal"):
        source = data / f"lidarr.db{suffix}"
        if source.exists():
            shutil.copyfile(source, destination / f"lidarr.db{suffix}")
    connection = sqlite3.connect(destination / "lidarr.db")
    try:
        rows = connection.execute(
            'SELECT "Id","Name","Status","QueuedAt","StartedAt","EndedAt","Body" FROM "Commands" ORDER BY "Id"').fetchall()
    finally:
        connection.close()
    result = []
    for row in rows:
        body = json.loads(row[6]) if row[6] else {}
        result.append({"id": row[0], "name": row[1], "status": STATUS.get(row[2], row[2]), "queued": row[3],
                       "started": row[4], "ended": row[5], "label": body.get("label"),
                       "disk": body.get("requiresDiskAccess"), "bodyName": body.get("name")})
    for item in result:
        note(f"DB {destination.name} id={item['id']} name={item['name']} label={item['label']} status={item['status']} "
             f"disk={item['disk']} started={item['started']} ended={item['ended']}")
    return result


def no_disk_overlap(rows: list[dict]) -> None:
    disk = [r for r in rows if r["disk"] and r["started"] and r["ended"]]
    for i, a in enumerate(disk):
        for b in disk[i + 1:]:
            overlap = ts(a["started"]) < ts(b["ended"]) and ts(b["started"]) < ts(a["ended"])
            check(not overlap, f"no overlap of disk-access commands #{a['id']} {a['name']}/{a['label']} and #{b['id']} {b['name']}/{b['label']}")


def run_queue(bin_dir: Path, plugin_dll: Path, base: Path) -> None:
    data, home, barriers, library = base / "config", base / "home", base / "barriers", base / "synthetic-root"
    for directory in (data, home, barriers, library):
        directory.mkdir()
    (base / ".crescendarr-disposable-host").write_text("CRESCENDARR_DISPOSABLE_HOST_V1\n", encoding="utf-8")
    (library / "synthetic-note.txt").write_text("synthetic, not audio\n", encoding="utf-8")
    install(plugin_dll, data)
    env = environment(base, home, opt_in=True)
    release(barriers, "push-on-start")
    release(barriers, "release-plugin-pushed")

    # ------------------------------------------------------------------ boot 1
    host = Host(bin_dir, data, env, base / "boot1.log")
    host.start()
    try:
        host.wait_ready("CRESCENDARR_QUEUE")
        registration = host.wait_log(r"CRESCENDARR_QUEUE \S+ thread=\d+ REGISTRATION [^\n]*")
        note("BOOT1 " + registration)
        check("knownCommandTypes=[CrescendarrDirectScanProbeCommand,CrescendarrQueueProbeCommand]" in registration,
              "stock KnownTypes includes both plugin Command types")
        check("executor=NzbDrone.Core.Plugins.CrescendarrQueueProbe.CrescendarrQueueProbeExecutor" in registration,
              "stock IServiceFactory resolves the plugin IExecute<T>")
        pushed = host.wait_log(r"PLUGIN_PUSHED id=\d+")
        pushed_id = int(pushed.split("=")[1])
        check(status(host.wait_status(pushed_id, {"completed"})) == "completed",
              "command pushed by the plugin through IManageCommandQueue ran on the stock executor")
        time.sleep(2)
        starts = re.findall(r"thread=\d+ START label=plugin-pushed [^\n]*", host.text())
        note(f"OBSERVED plugin-pushed executions={len(starts)} pushedId={pushed_id} "
             "(startup push races the stock ApplicationStartedEvent requeue)")
        for line in starts:
            note("OBSERVED " + line[:400])

        quality = host.api("GET", "qualityprofile")[0]["id"]
        metadata = host.api("GET", "metadataprofile")[0]["id"]
        host.api("POST", "rootfolder", {"name": "synthetic", "path": str(library), "defaultQualityProfileId": quality,
                                        "defaultMetadataProfileId": metadata, "defaultMonitorOption": "none",
                                        "defaultNewItemMonitorOption": "none", "defaultTags": []})
        note(f"ROOTFOLDER {library}")

        # Forward: plugin disk-access command started first.
        hold = host.command({"name": "CrescendarrQueueProbe", "label": "hold-forward"})
        host.wait_status(hold["id"], {"started"})
        host.wait_log(r"START label=hold-forward otherDiskAccessStarted=\[\]")
        scan = host.command({"name": "RescanFolders"})
        stays(host, scan["id"], "queued")
        duplicate_scan = host.command({"name": "RescanFolders"})
        check(duplicate_scan["id"] == scan["id"], "stock de-duplicates an identical queued RescanFolders")
        release(barriers, "release-dup")
        dup_a = host.command({"name": "CrescendarrQueueProbe", "label": "dup"})
        dup_b = host.command({"name": "CrescendarrQueueProbe", "label": "dup"})
        check(dup_a["id"] == dup_b["id"], "stock de-duplicates an identical queued plugin command")
        check(status(host.get(dup_a["id"])) == "queued", "second disk-access plugin command waits behind the first")

        # Negative control: a non-disk command calling IDiskScanService.Scan directly is NOT serialized.
        direct = host.command({"name": "CrescendarrDirectScanProbe", "folder": str(library)})
        host.wait_status(direct["id"], {"completed"})
        direct_end = host.wait_log(r"DIRECT_SCAN_END [^\n]*")
        check(f"CrescendarrQueueProbe#{hold['id']}:Started:disk=True" in direct_end,
              "direct IDiskScanService.Scan completed WHILE the plugin disk-access command was Started")
        check(f"RescanFolders#{scan['id']}:Queued:disk=True" in direct_end,
              "...and while the queued stock RescanFolders was still waiting")
        scan_seen = False
        for _ in range(100):
            scan_seen = re.search(r"Scanning " + re.escape(str(library)), host.host_log()) is not None
            if scan_seen:
                break
            time.sleep(0.1)
        check(scan_seen,
              "the stock scanner actually ran its folder scan for the direct call")
        check(status(host.get(scan["id"])) == "queued", "queued stock scan still waiting after the direct scan")

        release(barriers, "release-hold-forward")
        host.wait_status(hold["id"], {"completed"})
        host.wait_status(scan["id"], {"completed"})
        host.wait_status(dup_a["id"], {"completed"})
        forward_hold, forward_scan = host.get(hold["id"]), host.get(scan["id"])
        check(ts(forward_scan["started"]) >= ts(forward_hold["ended"]),
              f"forward: stock RescanFolders started {forward_scan['started']} only after plugin command ended {forward_hold['ended']}")

        # Reverse: stock RescanFolders started first (held in its Started state by a test-owned barrier).
        release(barriers, "arm-stock-hold")
        release(barriers, "release-after-stock")
        stock = host.command({"name": "RescanFolders"})
        host.wait_status(stock["id"], {"started"})
        host.wait_log(rf"STOCK_HOLD_BEGIN id={stock['id']}")
        after = host.command({"name": "CrescendarrQueueProbe", "label": "after-stock"})
        stays(host, after["id"], "queued")
        release(barriers, "release-stock-hold")
        host.wait_status(stock["id"], {"completed"})
        host.wait_status(after["id"], {"completed"})
        reverse_stock, reverse_after = host.get(stock["id"]), host.get(after["id"])
        check(ts(reverse_after["started"]) >= ts(reverse_stock["ended"]),
              f"reverse: plugin command started {reverse_after['started']} only after stock scan ended {reverse_stock['ended']}")
        host.wait_log(r"START label=after-stock otherDiskAccessStarted=\[\]")

        # Failure path.
        failing = host.command({"name": "CrescendarrQueueProbe", "label": "fail-path"})
        check(status(host.wait_status(failing["id"], {"failed", "completed"})) == "failed", "deliberate plugin failure reported as Failed")

        # Restart: one plugin command Started, one plugin and one stock disk-access command Queued, then SIGKILL.
        held = host.command({"name": "CrescendarrQueueProbe", "label": "hold-restart"})
        host.wait_status(held["id"], {"started"})
        queued_plugin = host.command({"name": "CrescendarrQueueProbe", "label": "queued-restart"})
        queued_scan = host.command({"name": "RescanFolders"})
        check(status(host.get(queued_plugin["id"])) == "queued" and status(host.get(queued_scan["id"])) == "queued",
              "before kill: plugin and stock disk-access commands queued behind the held plugin command")
    finally:
        host.kill()

    boot1 = snapshot_db(data, base / "db-after-sigkill")
    by_id = {row["id"]: row for row in boot1}
    check(by_id[held["id"]]["status"] == "started", "persisted: held plugin command is Started at SIGKILL")
    check(by_id[queued_plugin["id"]]["status"] == "queued" and by_id[queued_plugin["id"]]["label"] == "queued-restart",
          "persisted: queued plugin command and its label are in the Commands table")
    no_disk_overlap([row for row in boot1 if row["id"] != held["id"]])

    # ------------------------------------------------------------------ boot 2 (same data, plugin still installed)
    for label in ("hold-restart", "queued-restart"):
        release(barriers, f"release-{label}")
    (barriers / "push-on-start").unlink()
    host2 = Host(bin_dir, data, env, base / "boot2.log")
    host2.start()
    try:
        host2.wait_ready("CRESCENDARR_QUEUE")
        note("BOOT2 " + host2.wait_log(r"REGISTRATION [^\n]*"))
        requeued_plugin = host2.wait_status(queued_plugin["id"], {"completed", "failed"})
        requeued_scan = host2.wait_status(queued_scan["id"], {"completed", "failed"})
        note(f"BOOT2_REQUEUED plugin id={queued_plugin['id']} status={status(requeued_plugin)} message={requeued_plugin.get('message')} "
             f"body={json.dumps(requeued_plugin.get('body'))[:300]}")
        note(f"BOOT2_REQUEUED stock id={queued_scan['id']} status={status(requeued_scan)}")
        time.sleep(1)
        boot2_text = host2.text()
    finally:
        host2.stop()

    boot2 = snapshot_db(data, base / "db-after-restart")
    after_restart = {row["id"]: row for row in boot2}
    check(after_restart[held["id"]]["status"] == "orphaned", "restart: the Started plugin command was Orphaned, not resumed")
    check("START label=hold-restart" not in boot2_text, "restart: orphaned plugin command never re-executed")
    check("START label=queued-restart" not in boot2_text,
          "restart: queued plugin command was NOT executed by the plugin executor after rehydration")
    check(after_restart[queued_plugin["id"]]["status"] == "completed",
          "restart: queued plugin command row ended Completed (rehydrated as stock UnknownCommand and skipped)")
    check(requeued_plugin.get("body", {}).get("requiresDiskAccess") is False,
          "restart: rehydrated plugin command no longer claims disk access")
    check(after_restart[queued_scan["id"]]["status"] == "completed", "restart: queued stock RescanFolders was requeued and ran")
    note("QUEUE_EXPERIMENT_COMPLETE")


def run_guard_negative(bin_dir: Path, plugin_dll: Path, base: Path) -> None:
    data, home, barriers = base / "config", base / "home", base / "barriers"
    for directory in (data, home, barriers):
        directory.mkdir()
    install(plugin_dll, data)
    host = Host(bin_dir, data, environment(base, home, opt_in=False), base / "guard.log")
    host.start()
    try:
        host.wait_ready("CRESCENDARR_QUEUE")
        check("DISABLED no disposable opt-in" in host.text(), "probe disabled without opt-in")
        refused = host.command({"name": "CrescendarrQueueProbe", "label": "guard"})
        check(status(host.wait_status(refused["id"], {"failed", "completed"})) == "failed", "unopted probe command refused (Failed)")
        check("START label=guard" not in host.text(), "unopted probe never reached its body")
    finally:
        host.stop()
    note("GUARD_NEGATIVE_COMPLETE")


def install(plugin_dll: Path, data: Path) -> None:
    if plugin_dll.name != "Lidarr.Plugin.Crescendarr.QueueProbe.dll":
        raise RuntimeError("Unexpected experimental plugin filename.")
    plugin_dir = data / "plugins" / "GQuX" / "CrescendarrQueueProbe"
    plugin_dir.mkdir(parents=True)
    shutil.copyfile(plugin_dll, plugin_dir / plugin_dll.name)


def environment(base: Path, home: Path, opt_in: bool) -> dict[str, str]:
    env = {
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(home), "XDG_CONFIG_HOME": str(home),
        "LANG": "C.UTF-8", "TMPDIR": str(base), "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", ""),
        "Lidarr__Server__BindAddress": "127.0.0.1", "Lidarr__Server__Port": str(PORT),
        "CRESCENDARR_QUEUE_PROBE_ROOT": str(base),
    }
    if opt_in:
        env["CRESCENDARR_QUEUE_PROBE_OPT_IN"] = "DISPOSABLE_SYNTHETIC_ONLY"
    return env


def main() -> int:
    root = require_ci()
    if len(sys.argv) != 4 or sys.argv[3] not in ("queue", "guard-negative"):
        raise ValueError("Usage: run_disposable_command_queue.py <pinned-lidarr-bin> <plugin-dll> <queue|guard-negative>")
    bin_dir = Path(sys.argv[1]).resolve(strict=True)
    plugin_dll = Path(sys.argv[2]).resolve(strict=True)
    base = Path(tempfile.mkdtemp(prefix="crescendarr-queue-", dir=root)).resolve()
    try:
        (run_queue if sys.argv[3] == "queue" else run_guard_negative)(bin_dir, plugin_dll, base)
        return 0
    except Failure as failure:
        print(f"M16_QUEUE_EXPERIMENT_FAILED: {failure}", file=sys.stderr)
        return 1
    finally:
        for log in sorted(list(base.glob("*.log")) + list(base.glob("config/logs/lidarr*.txt"))):
            print(f"=== {log.name}: CRESCENDARR / command / scan lines ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if any(k in line for k in ("CRESCENDARR", "Scanning", "Exception", "Error", "Unknown command", "Orphan", "plugin")):
                    print(line[:600])
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M16 {sys.argv[3]}\n\n```\n" + "\n".join(EVIDENCE[-80:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
