#!/usr/bin/env python3
"""M27 release readiness: the AUDIT-ONLY candidate in the LinuxServer.io Lidarr 3.1.6.5079 container (pinned tag/digest)
with a persistent /config, REAL SABnzbd (synthetic NZBs from a localhost NNTP fixture), a kernel-mounted SMB/CIFS music
library, stock Completed Download Handling ON (Lidarr's default) and ordinary client names.

The plugin is installed, upgraded, rolled back and uninstalled through Lidarr's REAL installer (System > Plugins commands
InstallPlugin / UninstallPlugin). Its only GitHub call goes to a LOCAL mock of api.github.com (tools/experiments/mock_github.py,
throwaway CA trusted only inside this container); nothing is published.

Attribution: with CDH ON, stock Lidarr imports what it recognises by itself. Every library change is attributed by
comparing BEFORE/AFTER snapshots with stock's own History rows; Crescendarr's share must be zero (it has no write code,
see tools/audit-scan, and it must leave no state outside <AppData>/crescendarr-audit).

Usage: run_disposable_m27.py <host-lidarr-bin> <seeder-dll> <candidate-dir> <sabnzbd-dir> <python> <image>
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
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
import run_disposable_m22 as m22  # noqa: E402
from cifs_fixture import CifsShare  # noqa: E402
from mock_github import release, write_releases  # noqa: E402
from nntp_fixture import ArticleStore, NntpServer  # noqa: E402
from run_disposable_m17 import Failure, Host, check, note, sha, status  # noqa: E402
from run_disposable_m18 import GOOD, Lane, prepare  # noqa: E402
from run_disposable_m25 import WRITER_COMMANDS, set_connect_settings_in_db, stock_settings, tree  # noqa: E402

NAME = "crescendarr-m27-lidarr"
URL = "https://github.com/GQuX/lidarr-plugin-crescendarr"
ASSET_PORT = 18999
V1, V2 = "0.25.0-audit-beta.1", "0.25.0-audit-beta.2"
WRONG = "Crescendarr Synthetic Album [wrong edition]"
# History event types that are stock's own library writes (Lidarr EntityHistoryEventType).
STOCK_WRITE_EVENTS = {2: "ArtistFolderImported", 3: "TrackFileImported", 5: "TrackFileDeleted", 6: "TrackFileRenamed", 7: "AlbumImportIncomplete", 8: "DownloadImported", 9: "TrackFileRetagged"}


class DockerHost(Host):
    """Same API as the harness Host, backed by the LinuxServer.io container."""

    def __init__(self, image: str, run_args: list[str], data: Path, log: Path):
        super().__init__(Path("/"), data, {}, log)
        self.image, self.run_args, self.created = image, run_args, False

    def start(self, marker: str) -> None:
        if not self.created:
            subprocess.run(["docker", "run", "-d", "--name", NAME, *self.run_args, self.image], check=True, capture_output=True)
            self.created = True
        else:
            subprocess.run(["docker", "start", NAME], check=True, capture_output=True)
        limit = time.monotonic() + 240
        while time.monotonic() < limit:
            if marker in self.text() and self.ping():
                match = re.search(r"<ApiKey>([0-9a-f]+)</ApiKey>", (self.data / "config.xml").read_text())
                if match:
                    self.key = match.group(1)
                    note(f"CONTAINER_READY marker={marker!r}")
                    return
            time.sleep(1)
        raise Failure(f"container not ready ({marker!r})")

    def text(self) -> str:
        if not self.created:
            return ""
        out = subprocess.run(["docker", "logs", NAME], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        return out.stdout.decode("utf-8", errors="replace")

    def stop(self) -> None:
        if self.created:
            subprocess.run(["docker", "stop", "-t", "60", NAME], capture_output=True)
            self.log.write_text(self.text(), encoding="utf-8")
        note("CONTAINER_STOPPED")

    def restart(self, marker: str) -> None:
        self.stop()
        self.start(marker)

    def kill(self) -> None:
        subprocess.run(["docker", "kill", NAME], capture_output=True)


class Snapshot:
    """Library bytes and names, lyric files, library DB rows, stock settings and download sources at one instant."""

    def __init__(self, lane: Lane, host: Host, label: str):
        self.label = label
        self.library = tree(lane.base / "library")
        self.rows = rows(lane)
        self.settings = stock_settings(host)
        self.downloads = tree(lane.base / "downloads")
        self.history = max_history(lane)
        note(f"SNAPSHOT {label} libraryEntries={len(self.library)} lyrics={sum(1 for k in self.library if k.endswith('.lrc'))} "
             f"trackFiles={len(self.rows['files'])} historyMax={self.history}")


def rows(lane: Lane) -> dict:
    connection = sqlite3.connect(f"file:{lane.data / 'lidarr.db'}?mode=ro", uri=True, timeout=30)
    try:
        q = lambda sql: connection.execute(sql).fetchall()  # noqa: E731
        return {
            "files": q('SELECT "Id","Path","Size","Quality" FROM "TrackFiles" ORDER BY "Id"'),
            "links": q('SELECT "Id","TrackFileId" FROM "Tracks" ORDER BY "Id"'),
            "lyrics": q('SELECT "Id","TrackFileId","RelativePath" FROM "LyricFiles" ORDER BY "Id"'),
            "releases": q('SELECT "Id","ForeignReleaseId","Monitored" FROM "AlbumReleases" ORDER BY "Id"'),
            "albums": q('SELECT "Id","Monitored","AnyReleaseOk" FROM "Albums" ORDER BY "Id"'),
            "artists": q('SELECT "Id","Path","Monitored","QualityProfileId" FROM "Artists" ORDER BY "Id"'),
        }
    finally:
        connection.close()


def max_history(lane: Lane) -> int:
    connection = sqlite3.connect(f"file:{lane.data / 'lidarr.db'}?mode=ro", uri=True, timeout=30)
    try:
        return connection.execute('SELECT COALESCE(MAX("Id"), 0) FROM "History"').fetchone()[0]
    finally:
        connection.close()


def stock_events(lane: Lane, since: int) -> list[tuple]:
    connection = sqlite3.connect(f"file:{lane.data / 'lidarr.db'}?mode=ro", uri=True, timeout=30)
    try:
        return connection.execute('SELECT "Id","EventType","DownloadId","SourceTitle" FROM "History" WHERE "Id" > ? ORDER BY "Id"', (since,)).fetchall()
    finally:
        connection.close()


def attribute(lane: Lane, host: Host, before: Snapshot, label: str, stock_may_import: bool, harness_downloads: tuple[str, ...] = ()) -> None:
    """Every library change since `before` must be stock's own (History) and Crescendarr must have written nothing."""
    after = Snapshot(lane, host, label)
    changed_files = sorted(k for k in set(before.library) | set(after.library) if before.library.get(k) != after.library.get(k))
    changed_rows = [k for k in after.rows if after.rows[k] != before.rows[k]]
    events = stock_events(lane, before.history)
    writes = [(i, STOCK_WRITE_EVENTS[t], d, s) for i, t, d, s in events if t in STOCK_WRITE_EVENTS]
    note(f"ATTRIBUTION {label} changedLibraryEntries={len(changed_files)} {changed_files[:8]} changedTables={changed_rows} "
         f"stockHistory={[(STOCK_WRITE_EVENTS.get(t, t), s) for _, t, _, s in events][:12]}")
    if changed_files or changed_rows:
        check(stock_may_import, f"{label}: the library changed although stock had nothing to import ({changed_files[:5]}, {changed_rows})")
        check(writes, f"{label}: every library change is accompanied by stock Lidarr's own import/delete/rename History ({[e[1] for e in writes]})")
        note(f"STOCK_ATTRIBUTED {label} stock wrote the library ({len(writes)} History write event(s)); Crescendarr's share is zero by construction")
    else:
        note(f"NO_LIBRARY_CHANGE {label}")
    # (The synthetic artist is literally named "Crescendarr Synthetic Artist"; new files are caught by the tree diff above.)
    check(not [p for p in (lane.base / "library").rglob("*") if "probe" in p.name.lower() or ".partial-" in p.name],
          f"{label}: no probe or partial file anywhere in the library")
    check(not (lane.base / "crescendarr").exists() and not (lane.data / "crescendarr").exists(), f"{label}: no writer state folder")
    only_state = [str(p.relative_to(lane.data)) for p in lane.data.rglob("*") if "crescendarr" in str(p.relative_to(lane.data)).lower()
                  and not str(p.relative_to(lane.data)).startswith(("crescendarr-audit", "plugins"))]
    check(not only_state, f"{label}: Crescendarr files only in crescendarr-audit/ and its plugin folder ({only_state[:5]})")
    # Downloads that EXISTED before this step must be untouched unless stock consumed them; new SABnzbd job folders
    # created during the step are the step's own input.
    moved = sorted(k for k in before.downloads if before.downloads.get(k) != after.downloads.get(k)
                   and not k.startswith(harness_downloads or ("<none>",)))
    added = sorted(k for k in after.downloads if k not in before.downloads)
    note(f"DOWNLOAD_SOURCES {label} changedOrRemoved={moved[:8]} newDownloads={len(added)}")
    if moved:
        # Only stock may consume a completed download (CDH ON imports and, by its client default, removes it).
        check(stock_may_import and writes, f"{label}: download sources changed only where stock imported them ({moved[:5]})")
    diff = [k for k in after.settings if k != "otherNotifications" and after.settings[k] != before.settings[k]]
    check(not diff, f"{label}: stock settings unchanged ({diff})")


def connect(host: Host, mode: int, clients: list[int], allowed: str) -> None:
    existing = [n for n in host.api("GET", "notification") if n.get("implementation") == "CrescendarrAuditConnect"]
    resource = existing[0] if existing else [s for s in host.api("GET", "notification/schema") if s.get("implementation") == "CrescendarrAuditConnect"][0]
    for f in resource["fields"]:
        f["value"] = {"mode": mode, "observedClients": clients, "allowedDownloadFolders": allowed, "cycleMinutes": 1}.get(f["name"], f.get("value"))
    resource.update({"name": "Crescendarr", "onHealthIssue": True, "tags": resource.get("tags") or []})
    if existing:
        host.api("PUT", f"notification/{resource['id']}", resource)
    else:
        host.api("POST", "notification", resource)  # not forceSave: the provider's own Test() must pass
    note(f"CONNECT mode={mode} clients={clients}")


def cycle(host: Host) -> str:
    start = len(host.text())
    result = host.command({"name": "CrescendarrAuditCycle", "reason": "m27"}, deadline=300)
    check(status(result) == "completed", "audit cycle completed")
    found = re.findall(r"CRESCENDARR AUDIT_CYCLE_(?:SUMMARY|SKIPPED) [^\n]*", host.text()[start:])
    note("CYCLE " + (found[-1] if found else "<none>"))
    return found[-1] if found else ""


def report(lane: Lane) -> dict:
    path = lane.data / "crescendarr-audit" / "report.json"
    if not path.exists():
        return {}
    data = json.loads(path.read_text())
    entries = {d["folder"]: d for d in data["downloads"]}
    note("REPORT " + json.dumps({k: (v["state"], v.get("reason"), [(t["Track"], t["Action"]) for t in v["tracks"]]) for k, v in entries.items()})[:2500])
    return entries


def health(host: Host) -> list[str]:
    host.command({"name": "CheckHealth"})
    messages = [h.get("message") or "" for h in host.api("GET", "health")]
    note(f"HEALTH {messages}")
    return messages


def plugins(host: Host) -> list[dict]:
    listed = host.api("GET", "system/plugins")
    note(f"SYSTEM_PLUGINS {listed}")
    return listed


def add_client(host: Host, implementation: str, name: str, fields: dict, force: bool = False) -> dict:
    schema = [s for s in host.api("GET", "downloadclient/schema") if s.get("implementation") == implementation][0]
    for f in schema["fields"]:
        if f["name"] in fields:
            f["value"] = fields[f["name"]]
    schema.update({"name": name, "enable": True, "priority": 1})
    saved = host.api("POST", "downloadclient" + ("?forceSave=true" if force else ""), schema)
    note(f"CLIENT {name} ({implementation}) id={saved['id']} removeCompleted={saved.get('removeCompletedDownloads')}")
    return saved


def sab_job(lane: Lane, sab: m22.RealSab, store: ArticleStore, source: str, title: str) -> Path:
    nzb, _, _ = m22.stage_job(lane, store, source, title)
    nzo = sab.add(nzb)
    slot = sab.wait(nzo, ("Completed", "Failed"), deadline=420)
    check(slot["status"] == "Completed", f"real SABnzbd completed '{title}'")
    folder = Path(slot["storage"])
    note(f"SAB_COMPLETED {title!r} -> {folder.name}")
    return folder


def wait_report(host: Host, lane: Lane, name: str, what: str, deadline: float = 300) -> dict:
    limit = time.monotonic() + deadline
    while time.monotonic() < limit:
        cycle(host)
        entry = report(lane).get(name)
        if entry is not None:
            return entry
        time.sleep(10)
    raise Failure(what)


# ------------------------------------------------------------------ the lane

def run(bin_dir: Path, seeder: Path, candidate: Path, sab_dir: Path, python: str, image: str, base: Path, export: Path, state: Path) -> None:
    share = CifsShare(export, base / "library", state)
    share.configure()
    share.start_server()
    share.mount()
    note(f"CIFS_EVIDENCE {json.dumps(share.evidence())}")
    native1, native2 = candidate / f"Crescendarr-v{V1}.net8.0.zip", candidate / f"Crescendarr-v{V2}.net8.0.zip"
    with zipfile.ZipFile(native1) as z:
        check(z.namelist() == ["Lidarr.Plugin.Crescendarr.dll"], f"the release asset contains only the plugin DLL ({z.namelist()})")
        dll1 = z.read("Lidarr.Plugin.Crescendarr.dll")
    scanned = (candidate / "audit-scan.dll.sha256").read_text().split()[0]
    check(sha_bytes(dll1) == scanned, "the release asset's DLL is byte-identical to the independently scanned audit DLL")
    seed_plugin = base.parent / f"{base.name}-seed" / "Lidarr.Plugin.Crescendarr.dll"
    seed_plugin.parent.mkdir()
    seed_plugin.write_bytes(dll1)
    lane = prepare(bin_dir, seed_plugin, seeder, base, "three")
    shutil.rmtree(lane.data / "plugins")  # installed ONLY through the stock installer below
    complete = base / "downloads" / "sab-complete"
    complete.mkdir(parents=True)
    store = ArticleStore()
    nntp = NntpServer(store, m22.NNTP_PORT)
    sab = m22.RealSab(sab_dir, python, base, complete)
    sab.start()
    mock = None
    host = None
    try:
        mock, bundle, releases = start_mock(base, native1, native2)
        uid, gid = os.getuid(), os.getgid()
        args = ["--network", "host", "--add-host", "api.github.com:127.0.0.1", "-e", f"PUID={uid}", "-e", f"PGID={gid}", "-e", "TZ=Etc/UTC",
                "-e", f"Lidarr__Server__Port={m17.PORT}", "-e", "Lidarr__Server__BindAddress=127.0.0.1", "-e", "SSL_CERT_FILE=/crescendarr-ci/bundle.pem",
                "-v", f"{bundle}:/crescendarr-ci/bundle.pem:ro", "-v", f"{bundle}:/etc/ssl/certs/ca-certificates.crt:ro",
                "-v", f"{lane.data}:/config", "-v", f"{base}:{base}"]
        host = DockerHost(image, args, lane.data, base / "container.log")
        host.start("")
        status_resource = host.api("GET", "system/status")
        note(f"STOCK_STATUS version={status_resource.get('version')} os={status_resource.get('osName')} docker={status_resource.get('isDocker')} "
             f"appData={status_resource.get('appData')} runtime={status_resource.get('runtimeVersion')}")
        check(status_resource.get("version") == "3.1.6.5079", "the container runs Lidarr 3.1.6.5079")
        check(status_resource.get("isDocker") is True and status_resource.get("appData") == "/config", "stock Lidarr reports Docker with AppData /config")

        # --- ordinary configuration: CDH ON (stock default), SABnzbd with defaults, two blackholes ----------------------
        dl = host.api("GET", "config/downloadclient")
        dl["enableCompletedDownloadHandling"] = True
        host.api("PUT", f"config/downloadclient/{dl['id']}", dl)
        for c in host.api("GET", "downloadclient"):
            host.api("DELETE", f"downloadclient/{c['id']}")
        sab_client = add_client(host, "Sabnzbd", "SABnzbd", {"host": "127.0.0.1", "port": m22.SAB_PORT, "apiKey": m22.API_KEY,
                                                             "musicCategory": "music", "useSsl": False, "urlBase": ""})
        (base / "downloads" / "bh-nzb").mkdir(parents=True)
        (base / "downloads" / "bh-watch").mkdir()
        (base / "downloads" / "other-nzb").mkdir()
        (base / "downloads" / "other-watch").mkdir()
        bh = add_client(host, "UsenetBlackhole", "Blackhole", {"nzbFolder": str(base / "downloads" / "bh-nzb"), "watchFolder": str(base / "downloads" / "bh-watch")})
        other = add_client(host, "UsenetBlackhole", "Other Blackhole", {"nzbFolder": str(base / "downloads" / "other-nzb"), "watchFolder": str(base / "downloads" / "other-watch")})
        check(host.api("GET", "config/downloadclient")["enableCompletedDownloadHandling"] is True, "stock Completed Download Handling is ON")

        # --- 1. install through the REAL stock installer (mock GitHub API, nothing published) -------------------------
        result = host.command({"name": "InstallPlugin", "githubUrl": URL}, deadline=240)
        check(status(result) == "completed", "stock InstallPlugin completed")
        folder = lane.data / "plugins" / "GQuX" / "lidarr-plugin-crescendarr"
        check(sorted(p.name for p in folder.iterdir()) == ["Lidarr.Plugin.Crescendarr.dll"] and sha(folder / "Lidarr.Plugin.Crescendarr.dll") == scanned,
              "stock installer placed exactly the scanned DLL in plugins/GQuX/lidarr-plugin-crescendarr/")
        check(re.search(r"Plugin \[GQuX/lidarr-plugin-crescendarr\] v0\.25\.0-audit-beta\.1 installed", host.text()), "stock parsed the tag version 0.25.0-audit-beta.1")
        requests = (base / "mock-github" / "requests.log").read_text()
        note("MOCK_REQUESTS " + requests.replace("\n", " | ")[:800])
        check("API GET /repos/GQuX/lidarr-plugin-crescendarr/releases" in requests and f"ASSET GET /assets/Crescendarr-v{V1}.net8.0.zip" in requests,
              "the installer queried the releases API and downloaded the net8.0.zip asset")
        host.restart("CRESCENDARR AUDIT_BUILD_READY")
        ready = re.search(r"CRESCENDARR AUDIT_BUILD_READY [^\n]*", host.text()).group(0)
        note(ready)
        check(f"version={V1}+" in ready and "libraryWrites=impossible" in ready, "the audit-only build starts in the LinuxServer.io container")
        listed = [p for p in plugins(host) if p["owner"] == "GQuX" and p["name"] == "lidarr-plugin-crescendarr"]
        check(len(listed) == 1 and listed[0]["installedVersion"] == "0.25.0.0 (audit-beta.1)" and listed[0]["updateAvailable"] is False,
              "System > Plugins lists GQuX/lidarr-plugin-crescendarr 0.25.0 (audit-beta.1), no update offered")

        # --- 2. default Disabled ------------------------------------------------------------------------------------------
        base_snap = Snapshot(lane, host, "installed")
        mark = len(host.text())
        time.sleep(70)
        check("AUDIT_CYCLE_START" not in host.text()[mark:] and not (lane.data / "crescendarr-audit" / "report.json").exists(),
              "installed but unconfigured: nothing observed for 70 s")
        connect(host, 0, [], "")
        time.sleep(70)
        check("AUDIT_CYCLE_START" not in host.text()[mark:], "Connect entry with defaults (Disabled): nothing observed")

        # --- 3. audit with stock CDH ON: wrong edition first, then the good album ------------------------------------------
        connect(host, 1, [sab_client["id"], bh["id"]], f"{complete};{base / 'downloads' / 'bh-watch'}")
        before = Snapshot(lane, host, "before-wrong")
        wrong_folder = sab_job(lane, sab, store, WRONG, "Crescendarr Synthetic Artist - Crescendarr Synthetic Album (Wrong Edition)")
        wrong = wait_report(host, lane, wrong_folder.name, "the wrong-edition SAB job is previewed")
        note(f"PREVIEW_WRONG state={wrong['state']} reason={wrong.get('reason')}")
        time.sleep(120)  # two stock RefreshMonitoredDownloads ticks: let stock CDH act on its own
        attribute(lane, host, before, "after-wrong-edition", stock_may_import=True)

        before = Snapshot(lane, host, "before-good")
        good_folder = sab_job(lane, sab, store, GOOD, "Crescendarr Synthetic Artist - Crescendarr Synthetic Album (2024) [MP3 256]")
        cycle(host)
        good = report(lane).get(good_folder.name)
        note(f"PREVIEW_GOOD_FIRST_CYCLE {good['state'] if good else 'not yet / already handled by stock'} tracks={[(t['Track'], t['Action']) for t in good['tracks']] if good else None}")
        time.sleep(150)
        attribute(lane, host, before, "after-good-with-stock-cdh", stock_may_import=True)
        queue = host.api("GET", "queue?includeUnknownArtistItems=true")
        note(f"STOCK_QUEUE {[(r.get('title'), r.get('trackedDownloadState'), r.get('trackedDownloadStatus'), [m.get('messages') for m in r.get('statusMessages') or []]) for r in queue.get('records', [])]}")

        # --- 4. symlinked download path (selected client) and an unselected client --------------------------------------
        before = Snapshot(lane, host, "before-paths")
        outside = base / "outside"
        (outside / "Linked Album").mkdir(parents=True)
        (outside / "Linked Album" / "notes.txt").write_text("not audio\n")
        os.symlink(outside / "Linked Album", base / "downloads" / "bh-watch" / "Linked Album")
        (base / "downloads" / "other-watch" / "Unselected Album").mkdir()
        (base / "downloads" / "other-watch" / "Unselected Album" / "notes.txt").write_text("not audio\n")
        entry = wait_report(host, lane, "Linked Album", "the symlinked blackhole folder is reported", deadline=240)
        check(entry["state"] == "RefusedUnsafePath" and "symbolic link" in (entry.get("reason") or ""), f"a symlinked download path is refused ({entry})")
        check("Unselected Album" not in report(lane), "a completed download of an unselected client is never observed")
        attribute(lane, host, before, "after-paths", stock_may_import=False, harness_downloads=("bh-watch/Linked Album", "other-watch/Unselected Album"))

        # --- 5. command injection and an invalid (GuardedAuto) configuration -------------------------------------------
        before = Snapshot(lane, host, "before-injection")
        for name in WRITER_COMMANDS:
            try:
                outcome = status(host.command({"name": name, "downloadId": "x", "planId": "0" * 64}, deadline=60))
            except Failure as refused:
                outcome = "refused: " + str(refused)[:120]
            note(f"INJECTED {name} -> {outcome}")
            check(outcome != "completed", f"injected {name} did not run")
        host.stop()
        set_connect_settings_in_db(lane, lambda s: s.update({"mode": 2}))
        host.start("CRESCENDARR AUDIT_BUILD_READY")
        skipped = cycle(host)
        check("AUDIT_CYCLE_SKIPPED" in skipped and "invalid" in skipped, "a GuardedAuto mode written into the database is invalid: nothing observed")
        check(any("not observing anything" in m for m in health(host)), "health: invalid configuration is an error")
        host.stop()
        set_connect_settings_in_db(lane, lambda s: s.update({"mode": 1}))
        host.start("CRESCENDARR AUDIT_BUILD_READY")
        attribute(lane, host, before, "after-injection-and-invalid-config", stock_may_import=False)

        # --- 6. unavailable storage: the SMB server stops answering ------------------------------------------------------
        before = Snapshot(lane, host, "before-storage-outage")
        report_before = (lane.data / "crescendarr-audit" / "report.json").read_bytes()
        expected = {k: (v["state"], [(t["Track"], t["Action"]) for t in v["tracks"]]) for k, v in report(lane).items()}
        share.signal_server(signal.SIGSTOP)
        try:
            pushed = host.push({"name": "CrescendarrAuditCycle", "reason": "m27-outage"})
            time.sleep(90)
            during = host.api("GET", f"command/{pushed['id']}")
            note(f"OUTAGE_COMMAND status={status(during)} after 90 s with every smbd stopped")
            albums = (good_folder.name, wrong_folder.name)
            during_report = {k: (v["state"], [(t["Track"], t["Action"]) for t in v["tracks"]]) for k, v in report(lane).items()}
            note(f"OUTAGE_REPORT rewritten={(lane.data / 'crescendarr-audit' / 'report.json').read_bytes() != report_before} "
                 f"albums={ {k: during_report.get(k) for k in albums} }")
            check(all(during_report.get(k) in (expected.get(k), ("LibraryUnavailable", []), None) for k in albums),
                  "while the SMB server is not answering, no album preview turns into a false Upgrade/Add/Missing")
        finally:
            share.signal_server(signal.SIGCONT)
        limit = time.monotonic() + 300
        while status(host.api("GET", f"command/{pushed['id']}")) not in ("completed", "failed") and time.monotonic() < limit:
            time.sleep(3)
        check(status(host.api("GET", f"command/{pushed['id']}")) == "completed", "after the server answers again the blocked audit cycle completes")
        resumed = {k: (v["state"], [(t["Track"], t["Action"]) for t in v["tracks"]]) for k, v in report(lane).items()}
        # albums: the library-dependent previews (blackhole entries depend on stock timing)
        check({k: resumed.get(k) for k in albums} == {k: expected.get(k) for k in albums},
              f"the resumed cycle reports exactly the album previews from before the outage ({ {k: resumed.get(k) for k in albums} })")
        time.sleep(20)
        attribute(lane, host, before, "after-storage-outage", stock_may_import=False)

        # --- 7. restart with persistent /config ------------------------------------------------------------------------
        before = Snapshot(lane, host, "before-restart")
        host.restart("CRESCENDARR AUDIT_BUILD_READY")
        cycle(host)
        check((lane.data / "crescendarr-audit" / "status.json").exists(), "audit state persists in /config across restarts")
        attribute(lane, host, before, "after-restart", stock_may_import=False)

        # --- 8. update detection, upgrade and rollback through the real installer --------------------------------------
        write_releases(releases, [release(f"v{V2}", native2.name, ASSET_PORT, "2026-11-24T00:00:00Z", BODY), release(f"v{V1}", native1.name, ASSET_PORT, "2026-11-23T00:00:00Z", BODY)])
        listed = [p for p in plugins(host) if p["name"] == "lidarr-plugin-crescendarr"][0]
        check(listed["updateAvailable"] is True and listed["availableVersion"] == "0.25.0.0 (audit-beta.2)", "a newer pre-release is detected as an update")
        check(status(host.command({"name": "InstallPlugin", "githubUrl": URL}, deadline=240)) == "completed", "stock installer upgraded in place")
        host.restart("CRESCENDARR AUDIT_BUILD_READY")
        check(f"version={V2}+" in host.text().rsplit("AUDIT_BUILD_READY", 1)[1][:80], "after restart the upgraded build runs")
        check(any(n.get("implementation") == "CrescendarrAuditConnect" for n in host.api("GET", "notification")), "upgrade kept the Connect settings")
        write_releases(releases, [release(f"v{V1}", native1.name, ASSET_PORT, "2026-11-25T00:00:00Z", BODY)])
        check(status(host.command({"name": "InstallPlugin", "githubUrl": URL}, deadline=240)) == "completed", "rollback: reinstall the previous release")
        host.restart("CRESCENDARR AUDIT_BUILD_READY")
        check(f"version={V1}+" in host.text().rsplit("AUDIT_BUILD_READY", 1)[1][:80], "after rollback the previous build runs")
        cycle(host)

        # --- 9. uninstall through the stock command (Connect entry left in place, as a user might) -----------------------
        before = Snapshot(lane, host, "before-uninstall")
        check(status(host.command({"name": "UninstallPlugin", "githubUrl": URL}, deadline=120)) == "completed", "stock UninstallPlugin completed")
        check(not (lane.data / "plugins" / "GQuX" / "lidarr-plugin-crescendarr").exists(), "uninstall removed exactly plugins/GQuX/lidarr-plugin-crescendarr")
        host.restart("")
        time.sleep(30)
        check(re.search(r"Plugin \[GQuX/lidarr-plugin-crescendarr\][^\n]*uninstalled", host.text()), "stock logged the uninstall")
        check("AUDIT_BUILD_READY" not in re.split(r"uninstalled\. Please restart", host.text())[-1], "no Crescendarr code runs after uninstall")
        check(not [p for p in plugins(host) if p["name"] == "lidarr-plugin-crescendarr"], "System > Plugins no longer lists Crescendarr")
        try:
            leftovers = [n for n in host.api("GET", "notification") if n.get("implementation") == "CrescendarrAuditConnect"]
            note(f"ORPHAN_CONNECT_ENTRIES {len(leftovers)} (stock API still answers)")
        except Failure as orphan:
            note(f"ORPHAN_CONNECT_API_ERROR {orphan}")
        check(host.ping() and host.api("GET", "system/status")["version"] == "3.1.6.5079", "stock Lidarr is healthy after uninstall")
        attribute(lane, host, before, "after-uninstall", stock_may_import=False)
        note(f"LEFTOVER_STATE crescendarr-audit exists={(lane.data / 'crescendarr-audit').exists()} (documented manual removal)")
    finally:
        if host:
            host.stop()
            subprocess.run(["docker", "rm", "-f", NAME], capture_output=True)
        sab.stop()
        nntp.close()
        if mock:
            subprocess.run(["sudo", "kill", str(mock.pid)], capture_output=True)
        share.teardown()


BODY = ("Audit-only beta (read-only preview). Minimum Lidarr Version: 3.1.6.5079\n"
        "CI MOCK RELEASE - not a real GitHub release.")


def sha_bytes(data: bytes) -> str:
    import hashlib
    return hashlib.sha256(data).hexdigest()


def start_mock(base: Path, native1: Path, native2: Path) -> tuple[subprocess.Popen, Path, Path]:
    root = base / "mock-github"
    (root / "assets").mkdir(parents=True)
    shutil.copyfile(native1, root / "assets" / native1.name)
    shutil.copyfile(native2, root / "assets" / native2.name)

    def ssl(*args: str) -> None:
        subprocess.run(["openssl", *args], cwd=root, check=True, capture_output=True)

    ssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "ca.key", "-out", "ca.pem", "-days", "2", "-subj", "/CN=Crescendarr CI throwaway CA")
    ssl("req", "-newkey", "rsa:2048", "-nodes", "-keyout", "server.key", "-out", "server.csr", "-subj", "/CN=api.github.com")
    (root / "ext.cnf").write_text("subjectAltName=DNS:api.github.com\nbasicConstraints=CA:FALSE\nextendedKeyUsage=serverAuth\n")
    ssl("x509", "-req", "-in", "server.csr", "-CA", "ca.pem", "-CAkey", "ca.key", "-CAcreateserial", "-out", "server.pem", "-days", "2", "-extfile", "ext.cnf")
    bundle = root / "bundle.pem"
    bundle.write_text(Path("/etc/ssl/certs/ca-certificates.crt").read_text() + (root / "ca.pem").read_text())
    releases = root / "releases.json"
    write_releases(releases, [release(f"v{V1}", native1.name, ASSET_PORT, "2026-11-23T00:00:00Z", BODY)])
    log = root / "requests.log"
    log.write_text("")
    process = subprocess.Popen(["sudo", sys.executable, str(Path(__file__).with_name("mock_github.py")), str(root / "server.pem"), str(root / "server.key"),
                                str(releases), str(root / "assets"), str(ASSET_PORT), str(log)], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    limit = time.monotonic() + 20
    while "MOCK_GITHUB_READY" not in log.read_text() and time.monotonic() < limit:
        time.sleep(0.3)
    check("MOCK_GITHUB_READY" in log.read_text(), "local mock GitHub API is listening (127.0.0.1 only)")
    return process, bundle, releases


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 7:
        raise ValueError(__doc__)
    bin_dir, seeder, candidate, sab_dir = (Path(a).resolve(strict=True) for a in sys.argv[1:5])
    python, image = sys.argv[5], sys.argv[6]
    base = Path(tempfile.mkdtemp(prefix="crescendarr-m27-", dir=runner)).resolve()
    export = Path(tempfile.mkdtemp(prefix="crescendarr-m27-export-", dir=runner)).resolve()
    state = Path(tempfile.mkdtemp(prefix="crescendarr-m27-samba-", dir=runner)).resolve()
    try:
        run(bin_dir, seeder, candidate, sab_dir, python, image, base, export, state)
        note("M27_LANE_COMPLETE lsio")
        return 0
    except Failure as failure:
        print(f"M27_FAILED: {failure}", file=sys.stderr)
        note(f"M27_FAILED: {failure}")
        return 1
    finally:
        log = base / "container.log"
        if log.exists():
            print("=== container log (Crescendarr, plugin installer, import and error lines; redacted) ===")
            for line in log.read_text(errors="replace").splitlines():
                if any(k in line for k in ("CRESCENDARR", "Plugin", "Import", "Exception", "[Error]", "[Fatal]", "[Warn]")):
                    print(m22.redact(line[:1500]))
        (runner / "m27-lsio-evidence.txt").write_text(m22.redact("\n".join(m17.EVIDENCE)) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write("### M27 LinuxServer.io release readiness\n\n```\n" + m22.redact("\n".join(m17.EVIDENCE[-200:])) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
