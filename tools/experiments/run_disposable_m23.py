#!/usr/bin/env python3
"""M23 (issue #58): Crescendarr on a REAL Linux kernel CIFS (SMB 3.1.1) library, disposable only.

The disposable root's library folder is a real `cifs` mount (cifs_fixture.py: loopback smbd, random account) checked
with findmnt before anything runs. Lidarr's database, Crescendarr's journal, album-lyric backups and staging stay on
LOCAL runner storage (a different failure domain from the library); the stock recycle bin is local too, so stock
"moves" between staging/library/recycle are cross-filesystem copies. Simulated crashes here are process SIGKILL and
SMB server SIGSTOP/SIGKILL/unmount: they are NOT physical power loss.

Lanes
  cifs-three          baseline 3-track selective import on CIFS, rescan idempotency, restart replay
  cifs-twelve         baseline 12-track selective import on CIFS
  cifs-sigkill-rescan SIGKILL inside stock Import after the move onto CIFS, before the row; restart; real stock
                      rescan (cascade) before recovery; recovery
  cifs-server-loss    SMB server frozen (SIGSTOP) at the post-move boundary; the operation must not report Completed;
                      server back; recovery reconciles only hash/identity-proven state
  cifs-unmounted      SIGKILL after the native upgrade, share UNMOUNTED (mountpoint becomes an empty local directory),
                      recovery must write nothing there and must not end sticky; remount; recovery completes
  cifs-readonly       share remounted read-only before an import: refused before any destructive step, zero change

Usage: run_disposable_m23.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <lane>
"""
from __future__ import annotations

import json
import os
import re
import signal
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
from cifs_fixture import CifsShare  # noqa: E402
from run_disposable_m17 import Failure, check, files_state, library_view, note, print_view, sha, status  # noqa: E402
from run_disposable_m18 import (Lane, check_cascade, crash_at_post_move, import_body, prepare, ready_plan, recover,  # noqa: E402
                                replay_and_restart, restart_after_kill, stock_scan, verify_consistent)

LANES = ("cifs-three", "cifs-twelve", "cifs-sigkill-rescan", "cifs-server-loss", "cifs-unmounted", "cifs-readonly")
SHARE: CifsShare | None = None


def fstype(path: Path) -> str:
    import subprocess
    return subprocess.run(["findmnt", "-n", "-o", "FSTYPE", "--target", str(path)], capture_output=True, text=True).stdout.strip()


def storage_layout(lane: Lane) -> None:
    layout = {name: fstype(path) for name, path in {
        "library (artist root)": lane.artist_path, "download source": lane.watch, "staging + journal + lyric backups": lane.base / "crescendarr",
        "recycle bin": lane.base / "recycle", "lidarr database": lane.data}.items()}
    note(f"STORAGE_LAYOUT {json.dumps(layout)}")
    check(layout["library (artist root)"] == "cifs", "the library is on a REAL kernel CIFS mount")
    check(all(v != "cifs" for k, v in layout.items() if k != "library (artist root)"), "journal, backups, staging, recycle bin and database are on local storage")


def lane_baseline(lane: Lane, upgrades: list[int], superior: int, added: list[int]) -> None:
    host = lane.boot()
    try:
        storage_layout(lane)
        plan = ready_plan(lane, host)
        result = host.command(import_body(plan))
        check(status(result) == "completed" and "outcome=Completed" in host.text(), "the selective import over CIFS completed")
        final = verify_consistent(lane, "after-cifs-import", upgrades, superior, added)
        recycled = [p.name for p in (lane.base / "recycle").rglob("*") if p.is_file()]
        check(not any(x.endswith(".lrc") for x in recycled), "no lyric was recycled")
        scan = host.command({"name": "RescanFolders"})
        check(status(scan) == "completed" and lane.view("after-rescan") == final, "stock rescan over CIFS changed nothing")
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_sigkill_rescan(lane: Lane) -> None:
    host = lane.boot()
    try:
        storage_layout(lane)
        plan = ready_plan(lane, host)
        before = lane.state("before")
        crash_at_post_move(lane, host, plan, 1)
    except BaseException:
        host.stop()
        raise
    host = restart_after_kill(lane)
    try:
        scanned = stock_scan(lane, host, "scan-before-recovery")
        check_cascade(lane, before, scanned, [2])
        state, text = recover(host)
        check(state == "completed" and "outcome=Completed" in text, "recovery over CIFS completed after the crash and the stock cascade")
        final = verify_consistent(lane, "after-recovery", [1], 2, [3])
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_server_loss(lane: Lane) -> None:
    host = lane.boot()
    tid = lane.track_id(1)
    try:
        storage_layout(lane)
        plan = ready_plan(lane, host)
        before_lyrics = {n: lane.track(n)["lyricSha"] for n in (1, 2)}
        (lane.base / "faults" / f"pause-post-move-{tid}").write_text("armed\n")
        start = len(host.text())
        host.push(import_body(plan))
        host.wait_log(rf"FAULT_PAUSED post-move-{tid}", deadline=180, start=start)
        SHARE.signal_server(signal.SIGSTOP)
        note("SMB_SERVER_FROZEN (SIGSTOP) at the post-move/pre-row boundary")
        (lane.base / "faults" / f"release-post-move-{tid}").write_text("go\n")
        outcome = host.wait_log(r"IMPORT_OUTCOME[^\n]*", start=start, deadline=420).group(0)
        note("OUTCOME_WHILE_UNREACHABLE " + outcome)
        check("outcome=Completed" not in outcome, "with the SMB server unreachable the operation did NOT report Completed")
        frozen_commands = [c for c in lane.state("while-unreachable")["commands"] if c[1] == "CrescendarrImport"]
        note(f"IMPORT_COMMAND {frozen_commands}")
        SHARE.signal_server(signal.SIGCONT)
        note("SMB_SERVER_RESUMED (SIGCONT)")
        time.sleep(5)
        state, text = recover(host)
        note("RECOVERY " + (re.findall(r"IMPORT_OUTCOME[^\n]*", text) or ["(none)"])[-1])
        check(state == "completed" and "outcome=Completed" in text, "after the server came back, recovery reconciled hash/identity-proven state to Completed")
        final = verify_consistent(lane, "after-reconnect-recovery", [1], 2, [3])
        check(all(sha(lane.lyric(n)) == s for n, s in before_lyrics.items()), "every album lyric still has its original bytes")
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        SHARE.signal_server(signal.SIGCONT)
        host.stop()


def lane_unmounted(lane: Lane) -> None:
    host = lane.boot()
    try:
        storage_layout(lane)
        plan = ready_plan(lane, host)
        (lane.base / "faults" / "pause-after-native-1").write_text("armed\n")
        start = len(host.text())
        host.push(import_body(plan))
        host.wait_log(r"FAULT_PAUSED after-native-1", deadline=180, start=start)
        host.kill()
    except BaseException:
        host.stop()
        raise
    SHARE.umount(lazy=True)
    library = lane.base / "library"
    check(fstype(library) != "cifs" and not any(library.iterdir()), "share unmounted: the library path is now an EMPTY local directory")
    host = lane.boot()
    try:
        state, text = recover(host)
        note("RECOVERY_UNMOUNTED " + (re.findall(r"IMPORT_OUTCOME[^\n]*", text) or ["(none)"])[-1])
        written = sorted(str(p.relative_to(library)) for p in library.rglob("*"))
        check(not written, f"nothing was written into the unmounted mountpoint directory ({written})")
        check("outcome=Completed" not in text, "recovery did not report Completed while the library was unmounted")
        op = lane.op_root()
        check(not (op / "outcome.json").exists(), "the operation was NOT made terminal (no sticky review) by a storage outage")
        check(any("StorageUnavailable" in l or "storage unavailable" in l.lower() for l in text.splitlines()), "the reason names the storage outage")
        host.stop()
        SHARE.mount()
        check(fstype(library) == "cifs", "share remounted")
        host = lane.boot()
        state, text = recover(host)
        check(state == "completed" and "outcome=Completed" in text, "after remount, recovery completed the operation")
        final = verify_consistent(lane, "after-remount-recovery", [1], 2, [3])
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_readonly(lane: Lane) -> None:
    host = lane.boot()
    try:
        storage_layout(lane)
        plan = ready_plan(lane, host)
        before, before_files = lane.view("before"), files_state(lane.artist_path)
        SHARE.umount(lazy=True)
        SHARE.mount(read_only=True)
        note("SHARE_REMOUNTED_READ_ONLY " + json.dumps(SHARE.evidence()["proc_mounts"]))
        start = len(host.text())
        result = host.command(import_body(plan))
        text = host.text()[start:]
        note("READONLY_RESULT " + status(result) + " " + (re.findall(r"CRESCENDARR (?:IMPORT_REFUSED|IMPORT_OUTCOME)[^\n]*", text) or ["(none)"])[-1][:600])
        check("outcome=Completed" not in text, "a read-only library never yields Completed")
        check("IMPORT_REFUSED" in text and "NATIVE_IMPORT" not in text, "refused BEFORE any destructive step (no native import)")
        check(lane.view("after") == before and files_state(lane.artist_path) == before_files, "zero row, link, lyric or byte change")
        check(not list((lane.base / "recycle").rglob("*.*")), "nothing was recycled")
    finally:
        host.stop()


def run(bin_dir: Path, plugin: Path, seeder: Path, name: str, base: Path, export: Path, state: Path) -> None:
    global SHARE
    SHARE = CifsShare(export, base / "library", state)
    SHARE.configure()
    SHARE.start_server()
    SHARE.mount()
    note(f"CIFS_EVIDENCE {json.dumps(SHARE.evidence())}")
    lane = prepare(bin_dir, plugin, seeder, base, "twelve" if name == "cifs-twelve" else "three")
    {"cifs-three": lambda: lane_baseline(lane, [1], 2, [3]), "cifs-twelve": lambda: lane_baseline(lane, list(range(1, 12)), 12, []),
     "cifs-sigkill-rescan": lambda: lane_sigkill_rescan(lane), "cifs-server-loss": lambda: lane_server_loss(lane),
     "cifs-unmounted": lambda: lane_unmounted(lane), "cifs-readonly": lambda: lane_readonly(lane)}[name]()
    note(f"M23_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    name = sys.argv[4]
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m23-{name}-", dir=runner)).resolve()
    export = Path(tempfile.mkdtemp(prefix="crescendarr-m23-export-", dir=runner)).resolve()
    state = Path(tempfile.mkdtemp(prefix="crescendarr-m23-samba-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, name, base, export, state)
        return 0
    except Failure as failure:
        print(f"M23_FAILED: {failure}", file=sys.stderr)
        note(f"M23_FAILED: {failure}")
        return 1
    finally:
        if SHARE:
            SHARE.teardown()
        for log in sorted(base.glob("boot*.log"), key=lambda p: int(re.sub(r"\D", "", p.name) or 0)):
            print(f"=== {log.name} (Crescendarr and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]", "RecycleBin")):
                    print(line[:2000])
        (runner / f"m23-{name}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M23 {name}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
