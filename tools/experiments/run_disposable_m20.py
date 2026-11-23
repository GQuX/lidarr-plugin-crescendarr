#!/usr/bin/env python3
"""M20 (issue #51): unattended Crescendarr in the official SHA-256-pinned stock Lidarr v3.1.6.5079 daemon.

The harness configures Crescendarr ONLY through Lidarr's own Settings > Connect provider API and never pushes a
Crescendarr command in the automatic lanes: every plan, import and recovery is started by the plugin's scheduler
(stock RefreshMonitoredDownloads -> CommandExecutedEvent -> queued CrescendarrCycle) or by the startup cycle.

Lanes
  auto-guarded        GuardedAuto: a good download and a wrong-edition download appear; the plugin imports only the
                      Ready one, records both in its registry, never re-imports across cycles or a restart, keeps the
                      sources, claims no stock settlement; Disabled stops cycles; without opt-in it is inert
  auto-audit          AuditOnly (default): zero library writes, Ready plan recorded as awaiting approval, health warning,
                      explicit approval (CrescendarrImport with the registry plan ID) imports, the next cycle records it handled
  auto-crash-restart  GuardedAuto import SIGKILLed after the native upgrade; the startup cycle recovers FIRST and completes
  auto-busy-album     a fresh-row alias forces review during an automatic import; the health check reports the
                      operation, later cycles never import into the blocked album, the registry stays terminal

Usage: run_disposable_m20.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <lane>
"""
from __future__ import annotations

import json
import os
import re
import shutil
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
from run_disposable_m17 import Failure, Host, check, environment, files_state, library_view, note, sha, status  # noqa: E402
from run_disposable_m18 import GOOD, Lane, prepare, verify_consistent  # noqa: E402

LANES = ("auto-guarded", "auto-audit", "auto-crash-restart", "auto-busy-album")
WRONG = "Crescendarr Synthetic Album [wrong edition]"
MODES = {"Disabled": 0, "AuditOnly": 1, "GuardedAuto": 2}


def configure(host: Host, mode: str, cycle: int = 1, max_imports: int = 1) -> dict:
    """Settings > Connect > Crescendarr, through the stock provider API only."""
    existing = [n for n in host.api("GET", "notification") if n.get("implementation") == "CrescendarrConnect"]
    resource = existing[0] if existing else [s for s in host.api("GET", "notification/schema") if s.get("implementation") == "CrescendarrConnect"][0]
    values = {"mode": MODES[mode], "cycleMinutes": cycle, "maxImportsPerCycle": max_imports, "qualityPolicy": 0, "retention": 0}
    for f in resource["fields"]:
        if f["name"] in values:
            f["value"] = values[f["name"]]
    resource.update({"name": "Crescendarr", "onHealthIssue": True, "tags": resource.get("tags") or []})
    saved = host.api("PUT", f"notification/{resource['id']}?forceSave=true", resource) if existing else host.api("POST", "notification?forceSave=true", resource)
    note(f"CONFIGURED mode={mode} cycleMinutes={cycle} maxImportsPerCycle={max_imports} id={saved.get('id')}")
    return saved


def place(lane: Lane, name: str) -> None:
    if not (lane.watch / name).exists():
        shutil.move(str(lane.base / "prepared" / name), str(lane.watch / name))
    if name == GOOD:
        lane.incoming = {p.name: sha(p) for p in (lane.watch / GOOD).iterdir()}


def wait_summary(host: Host, pattern: str, start: int, deadline: float = 420) -> re.Match:
    m = host.wait_log(r"CRESCENDARR CYCLE_SUMMARY [^\n]*" + pattern + r"[^\n]*", deadline=deadline, start=start)
    note("SUMMARY " + m.group(0))
    return m


def summaries(host: Host, start: int = 0) -> list[str]:
    return re.findall(r"CRESCENDARR CYCLE_SUMMARY [^\n]*", host.text()[start:])


def wait_cycles(host: Host, mark: int, count: int, deadline: float = 400) -> list[str]:
    limit = time.monotonic() + deadline
    while len(summaries(host, mark)) < count and time.monotonic() < limit:
        time.sleep(5)
    found = summaries(host, mark)
    check(len(found) >= count, f"{count} further automatic cycles ran")
    return found


def registry(lane: Lane) -> dict[str, dict]:
    folder = lane.base / "crescendarr" / "downloads"
    entries = {}
    for p in folder.glob("*.json") if folder.exists() else []:
        entry = json.loads(p.read_text())
        entries[Path(entry["folder"]).name] = entry
    note(f"REGISTRY {json.dumps({k: {x: v.get(x) for x in ('state', 'pluginHandled', 'lidarrImported', 'downloaderRemoved', 'sourceRetained', 'planId', 'reason', 'blocked')} for k, v in entries.items()})}")
    return entries


def health(host: Host) -> list[dict]:
    host.command({"name": "CheckHealth"})
    items = [h for h in host.api("GET", "health") if "Crescendarr" in (h.get("source") or "") or "Crescendarr" in (h.get("message") or "")]
    note(f"HEALTH {items}")
    return items


def no_manual_crescendarr_commands(lane: Lane) -> None:
    names = {c[1] for c in lane.state("commands")["commands"]}
    note(f"COMMAND_NAMES {sorted(names)}")
    check("CrescendarrCycle" in names, "automatic CrescendarrCycle commands ran on the stock queue")
    check(not names & {"CrescendarrImport", "CrescendarrPlan", "CrescendarrRecover"}, "no Crescendarr command was pushed by the harness")


def downloads_intact(lane: Lane, names: list[str]) -> None:
    for name in names:
        check((lane.watch / name).is_dir() and len(list((lane.watch / name).iterdir())) == 3, f"download source retained: {name}")
    check({p.name: sha(p) for p in (lane.watch / GOOD).iterdir()} == lane.incoming, "good download bytes unchanged")


# ------------------------------------------------------------------ lanes

def lane_guarded(lane: Lane) -> None:
    host = lane.boot()
    try:
        before = lane.view("before")
        configure(host, "GuardedAuto")
        place(lane, GOOD)
        place(lane, WRONG)
        start = len(host.text())
        wait_summary(host, r"imported=1", start)
        check(len(re.findall(r"CYCLE_SCHEDULED reason=schedule", host.text()[start:])) >= 1, "the stock-driven scheduler pushed the cycle")
        verify_consistent(lane, "after-auto-import", [1], 2, [3])
        reg = registry(lane)
        check(reg[GOOD]["state"] == "Imported" and reg[GOOD]["pluginHandled"] and not reg[GOOD]["lidarrImported"] and not reg[GOOD]["downloaderRemoved"],
              "registry: handled by Crescendarr, NOT reported as Lidarr-imported or downloader-removed")
        check(reg[WRONG]["state"] in ("NeedsReview", "Blocked"), "the wrong-edition download is recorded for review and never imported")
        downloads_intact(lane, [GOOD, WRONG])
        natives = host.text().count("NATIVE_IMPORT")
        mark = len(host.text())
        later = wait_cycles(host, mark, 2)
        check(all("imported=0" in s and "alreadyHandled=2" in s for s in later), "two further cycles: duplicates suppressed, nothing re-imported")
        check(host.text().count("NATIVE_IMPORT") == natives, "no further native import")
        queue = host.api("GET", "queue?includeUnknownArtistItems=true")
        note(f"STOCK_QUEUE {queue.get('totalRecords')} {[(r.get('title'), r.get('trackedDownloadState')) for r in queue.get('records', [])]}")
        check(not any(r.get("trackedDownloadState") == "imported" for r in queue.get("records", [])), "no stock queue item is falsely marked imported")
        check(not health(host), "health: no Crescendarr issue after a clean automatic import")
        final = lane.view("before-restart")
        host.stop()
        host = lane.boot()
        start = len(host.text())
        host.wait_log(r"CYCLE_SCHEDULED reason=startup", start=0)
        wait_summary(host, r"alreadyHandled=2", 0)
        check(lane.view("after-restart-cycles") == final, "after a restart the startup cycle changed nothing (no duplicate import)")
        configure(host, "Disabled")
        mark = len(host.text())
        time.sleep(150)
        check("CYCLE_START" not in host.text()[mark:], "Disabled: no cycle ran for 150 s (two stock refresh ticks)")
        no_manual_crescendarr_commands(lane)
        host.api("DELETE", f"notification/{[n for n in host.api('GET', 'notification') if n['implementation'] == 'CrescendarrConnect'][0]['id']}")
        host.stop()
        host = lane.boot(crescendarr=False, marker="CRESCENDARR INERT")
        time.sleep(75)
        check("CYCLE_" not in host.text(), "without opt-in and configuration the installed plugin is inert")
        check(lane.view("inert") == final, "inert host: library unchanged")
        downloads_intact(lane, [GOOD, WRONG])
    finally:
        host.stop()


def lane_audit(lane: Lane) -> None:
    host = lane.boot()
    try:
        before, before_files = lane.view("before"), files_state(lane.artist_path)
        configure(host, "AuditOnly")
        place(lane, GOOD)
        start = len(host.text())
        wait_summary(host, r"awaitingApproval=1", start)
        check(lane.view("after-audit") == before and files_state(lane.artist_path) == before_files, "audit only: zero library row or byte changes")
        check("NATIVE_IMPORT" not in host.text(), "audit only: no native import")
        reg = registry(lane)
        check(reg[GOOD]["state"] == "AwaitingApproval" and reg[GOOD]["planId"], "registry: Ready plan awaiting approval, with its plan ID")
        warnings = health(host)
        check(any("awaiting approval" in (h.get("message") or "") for h in warnings), "health: native warning lists the download awaiting approval")
        result = host.command({"name": "CrescendarrImport", "downloadId": reg[GOOD]["downloadId"], "planId": reg[GOOD]["planId"]})
        check(status(result) == "completed", "explicit operator approval (the registry plan ID) imported the download")
        verify_consistent(lane, "after-approval", [1], 2, [3])
        mark = len(host.text())
        wait_summary(host, r"noImprovement=1", mark)
        check(registry(lane)[GOOD]["state"] == "NoImprovement" and host.text()[mark:].count("NATIVE_IMPORT") == 0, "the next audit cycle records it handled; nothing imported")
        check(not any("awaiting approval" in (h.get("message") or "") for h in health(host)), "health warning cleared")
        downloads_intact(lane, [GOOD])
    finally:
        host.stop()


def lane_crash_restart(lane: Lane) -> None:
    host = lane.boot()
    try:
        configure(host, "GuardedAuto")
        (lane.base / "faults" / "pause-after-native-1").write_text("armed\n")
        place(lane, GOOD)
        host.wait_log(r"FAULT_PAUSED after-native-1", deadline=420)
        host.kill()
    except BaseException:
        host.stop()
        raise
    host = lane.boot()
    try:
        cycles = [c for c in lane.state("after-restart")["commands"] if c[1] == "CrescendarrCycle"]
        note(f"RESTART_COMMANDS {cycles}")
        check(any(m17.STATUS.get(c[2]) == "orphaned" for c in cycles), "the interrupted automatic cycle was Orphaned by stock, never resumed")
        host.wait_log(r"CYCLE_SCHEDULED reason=startup", start=0)
        recovered = host.wait_log(r"CYCLE_RECOVERY op=\S+ state=(\w+)", start=0, deadline=300)
        note("RECOVERY " + recovered.group(0))
        check(recovered.group(1) == "Completed", "the startup cycle ran recovery FIRST and completed the interrupted operation")
        summary = wait_summary(host, r"recovered=1", 0)
        check("imported=0" in summary.group(0), "no new import ran in the recovering cycle (the download is handled by the recovered operation)")
        verify_consistent(lane, "after-auto-recovery", [1], 2, [3])
        check(not health(host), "health: clean after automatic recovery")
        no_manual_crescendarr_commands(lane)
        downloads_intact(lane, [GOOD])
    finally:
        host.stop()


def lane_busy_album(lane: Lane) -> None:
    foreign = lane.seed["foreign"]
    f3 = [t for t in foreign["tracks"] if t["n"] == 3][0]["trackId"]
    host = lane.boot()
    try:
        configure(host, "GuardedAuto")
        (lane.base / "faults" / "pause-after-native-1").write_text("armed\n")
        place(lane, GOOD)
        host.wait_log(r"FAULT_PAUSED after-native-1", deadline=420)
        fresh = [r[3] for r in lane.state("paused")["tracks"] if r[0] == lane.track_id(1)][0]
        lane.sql('UPDATE "Tracks" SET "TrackFileId" = ? WHERE "Id" = ?', (fresh, f3))
        (lane.base / "faults" / "release-after-native-1").write_text("go\n")
        host.wait_log(r"IMPORT_OUTCOME[^\n]*outcome=RequiresReview", deadline=120)
        frozen = lane.view("after-review")
        issues = health(host)
        check(any(str(h.get("type")).lower() == "error" and "need an operator" in (h.get("message") or "") for h in issues), "health: native ERROR names the blocked operation")
        mark = len(host.text())
        later = wait_cycles(host, mark, 2)
        check(all("imported=0" in s for s in later) and host.text()[mark:].count("NATIVE_IMPORT") == 0, "later cycles never import into the blocked album")
        check(registry(lane)[GOOD]["state"] == "RequiresReview", "registry keeps the download terminal (RequiresReview), never 'handled successfully'")
        check(lane.view("after-cycles") == frozen, "no row, link or lyric change after review")
        host.command({"name": "CrescendarrOperatorStatus"})
        status_file = json.loads((lane.base / "crescendarr" / "status.json").read_text())
        note(f"OPERATOR_STATUS_FILE {json.dumps(status_file)[:1500]}")
        check(any(o["state"] == "RequiresReview" and o["blocksAlbum"] for o in status_file["operations"]), "operator status lists the blocking review")
        downloads_intact(lane, [GOOD])
    finally:
        host.stop()


def run(bin_dir: Path, plugin: Path, seeder: Path, name: str, base: Path) -> None:
    lane = prepare(bin_dir, plugin, seeder, base, "three")
    {"auto-guarded": lane_guarded, "auto-audit": lane_audit, "auto-crash-restart": lane_crash_restart, "auto-busy-album": lane_busy_album}[name](lane)
    note(f"M20_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m20-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M20_FAILED: {failure}", file=sys.stderr)
        note(f"M20_FAILED: {failure}")
        return 1
    finally:
        for log in sorted(base.glob("boot*.log"), key=lambda p: int(re.sub(r"\D", "", p.name) or 0)):
            print(f"=== {log.name} (Crescendarr and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]")):
                    print(line[:2000])
        (runner / f"m20-{sys.argv[4]}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M20 {sys.argv[4]}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
