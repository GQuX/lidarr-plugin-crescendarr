#!/usr/bin/env python3
"""M19 (issue #50): multi-track crash recovery, protective lyric salvage, operator resolution and finer native
boundaries, on the official SHA-256-pinned stock Lidarr v3.1.6.5079 daemon with exactly ONE Crescendarr plugin.

Every lane runs on the REAL host (real daemon, real stock importer, real stock RescanFolders, real SQLite).
Checks read DB rows from a copy of lidarr.db (+WAL) and file bytes from disk.

Lanes
  twelve-postmove-recover    SIGKILL inside stock Import after moving track 6 of 12, before its row; real stock rescan
                             (album-wide cascade, every TrackFile ID changes) BEFORE recovery; recovery must COMPLETE:
                             every lyric present with its original SHA-256 and registered to the verified CURRENT row
  twelve-postmove-ambiguous  same, plus a foreign album track aliasing an unstarted track's rescanned row: sticky review,
                             no further audio import, protective salvage of every unambiguous lyric, then an explicit
                             operator workflow (stale token rejected, salvage, resolve) that releases the album block
                             WITHOUT deleting the operation or its backups; a new import then finishes the album
  three-postrow              SIGKILL after stock inserted the TrackFile row but before it linked the track
                             (TrackFileAddedEvent seam); rescan; operator-approved link; completion
  three-lyric-crash          post-move crash + rescan, then SIGKILL after restoring a lyric and again after binding it;
                             each restart + rescan + recovery is idempotent

Usage: run_disposable_m19.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <lane>
"""
from __future__ import annotations

import json
import os
import re
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
import run_disposable_m18 as m18  # noqa: E402
from run_disposable_m17 import Failure, check, field, library_view, note, plan_until_completed, print_view, sha, status  # noqa: E402
from run_disposable_m18 import (GOOD, Lane, approve, crash_at_post_move, import_body, prepare, propose, ready_plan,  # noqa: E402
                                recover, replay_and_restart, restart_after_kill, review_summary, stock_scan, verify_consistent)

LANES = ("twelve-postmove-recover", "twelve-postmove-ambiguous", "three-postrow", "three-lyric-crash")


def album_lyrics_registered(lane: Lane, label: str, tracks: list[int], skip: tuple[int, ...] = ()) -> dict:
    """Every originally registered lyric: physically present with the ORIGINAL SHA-256 and registered once to the
    track's verified CURRENT TrackFile (a row at the right path holding the right bytes)."""
    view = library_view(lane.state(label), lane.seed, lane.release_id)
    for n in tracks:
        if n in skip:
            continue
        lyric, v = lane.lyric(n), view[n]
        check(lyric.exists() and sha(lyric) == lane.track(n)["lyricSha"], f"track {n}: lyric physically present with its original SHA-256")
        check(v["trackFileId"] > 0 and v["lyrics"] == [(v["lyrics"][0][0], lyric.name)] if v["lyrics"] else False,
              f"track {n}: lyric registered exactly once to the CURRENT TrackFile {v['trackFileId']}")
    return view


def crash_and_scan(lane: Lane, n: int) -> object:
    host = lane.boot()
    try:
        plan = ready_plan(lane, host)
        before = lane.state("before")
        crash_at_post_move(lane, host, plan, n)
    except BaseException:
        host.stop()
        raise
    host = restart_after_kill(lane)
    scanned = stock_scan(lane, host, "scan-before-recovery")
    b, a = (library_view(s, lane.seed, lane.release_id) for s in (before, scanned))
    rotated = [k for k in a if b[k]["trackFileId"] and a[k]["trackFileId"] not in (0, b[k]["trackFileId"])]
    note(f"CASCADE_ROTATED tracks={rotated}")
    check(len(rotated) >= 2, "the real stock rescan re-imported the album with fresh TrackFile rows (destructive cascade exercised)")
    return host


def lane_twelve_recover(lane: Lane) -> None:
    host = crash_and_scan(lane, 6)
    try:
        state, text = recover(host)
        note("RECOVERY " + host.wait_log(r"IMPORT_OUTCOME[^\n]*").group(0))
        check(len(re.findall(r"REBASE_ORIGINAL", text)) >= 5, "the rescanned rows of the 5 unstarted upgrades were recorded as durable observation evidence")
        check(state == "completed" and "outcome=Completed" in text, "the 12-track crash/rescan recovered to a COMPLETED operation")
        final = verify_consistent(lane, "after-recovery", list(range(1, 12)), 12, [])
        album_lyrics_registered(lane, "after-recovery-lyrics", list(range(1, 13)))
        journal = json.loads((lane.op_root() / "journal.json").read_text())["payload"]
        rebased = [p for p in journal["progress"] if p.get("observedOriginalTrackFileId")]
        note(f"JOURNAL_REBASES {[(p['trackId'], p['observedOriginalTrackFileId']) for p in rebased]}")
        original_ids = {t["trackId"]: t["originalTrackFileId"] for t in journal["binding"]["tracks"]}
        check(all(original_ids[p["trackId"]] == lane.track(next(t["n"] for t in lane.seed["tracks"] if t["trackId"] == p["trackId"]))["trackFileId"] for p in rebased),
              "the immutable original binding still holds the seeded original IDs (rebase is evidence, not a mutation)")
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_twelve_ambiguous(lane: Lane) -> None:
    host = crash_and_scan(lane, 6)
    foreign = lane.seed["foreign"]
    f3 = [t for t in foreign["tracks"] if t["n"] == 3][0]["trackId"]
    try:
        scanned = library_view(lane.state("before-alias"), lane.seed, lane.release_id)
        aliased_row = scanned[8]["trackFileId"]
        lane.sql('UPDATE "Tracks" SET "TrackFileId" = ? WHERE "Id" = ?', (aliased_row, f3))
        before_recovery = library_view(lane.state("before-recovery"), lane.seed, lane.release_id)
        state, text = recover(host)
        check(state == "failed" and "outcome=RequiresReview" in text, "an ambiguous rescanned row (foreign alias) forces sticky RequiresReview")
        check("NATIVE_IMPORT" not in text, "no further native audio import ran once the album became ambiguous")
        check(re.search(r"SALVAGE op=\S+ [^\n]*restored=", text) is not None, "protective lyric salvage ran on entering review")
        after = album_lyrics_registered(lane, "after-salvage", list(range(1, 13)), skip=(8,))
        check(lane.lyric(8).exists() and sha(lane.lyric(8)) == lane.track(8)["lyricSha"], "track 8: lyric bytes restored (unregistered: its current row is ambiguous)")
        check(after[8]["lyrics"] == [], "track 8: lyric NOT registered to an ambiguous (foreign-aliased) row")
        for n in range(7, 12):
            check(after[n]["sha"] == lane.track(n)["sha"] and after[n]["quality"] == 1, f"unstarted track {n}: original MP3-192 audio untouched")
        check(all(after[n]["trackFileId"] == before_recovery[n]["trackFileId"] for n in range(1, 13)), "salvage changed no audio link")
        summary = review_summary(lane, "RequiresReview")
        check("8" in json.dumps(summary.get("reasons")) or "linked to another track" in json.dumps(summary.get("reasons")), "the summary explains why the remaining tracks were not imported")
        state, text = recover(host)
        check("RECOVERY_SKIP" in text and "NATIVE_IMPORT" not in text, "the review is sticky")

        op = lane.op_root().name
        allowed, token, line = propose(host, op, "resolve")
        check(not allowed and token is None, "resolve is refused while a lyric is unregistered and a row is aliased")
        lane.sql('UPDATE "Tracks" SET "TrackFileId" = 0 WHERE "Id" = ?', (f3,))
        allowed, salvage_token, _ = propose(host, op, "salvage-lyrics")
        check(allowed and salvage_token, "after the alias is removed, a salvage proposal is allowed")
        allowed, resolve_token, _ = propose(host, op, "resolve")
        check(not allowed, "resolve is still refused until lyric 8 is registered")
        result, text = approve(host, op, "salvage-lyrics", salvage_token)
        check(result == "applied", "the operator-approved salvage was applied")
        album_lyrics_registered(lane, "after-operator-salvage", list(range(1, 13)))
        result, _ = approve(host, op, "salvage-lyrics", salvage_token)
        check(result == "rejected", "the same token cannot be replayed (the snapshot changed)")
        allowed, resolve_token, _ = propose(host, op, "resolve")
        check(allowed and resolve_token, "resolve is allowed once every bound audio file and album lyric is verified and linked")
        result, text = approve(host, op, "resolve", resolve_token)
        check(result == "applied", "the operator resolution was applied with the fresh token")
        resolution = json.loads((lane.op_root() / "resolution.json").read_text())
        note(f"RESOLUTION {json.dumps(resolution)[:1500]}")
        check(resolution.get("importCompleted") is False and resolution.get("downloadSettled") is False,
              "resolution is NOT import completion and NOT download settlement")
        check((lane.op_root() / "journal.json").exists() and any((lane.op_root() / "album-lyrics").iterdir()),
              "the journal and the verified album backups were kept")
        outcome = json.loads((lane.op_root() / "outcome.json").read_text())
        check(outcome["outcome"] == "RequiresReview", "the reviewed outcome is preserved (never rewritten as Completed)")
        # The album block is released: a NEW operation finishes the remaining upgrades through the normal journal.
        plan = plan_until_completed(host, GOOD)
        check(field(plan, "disposition") == "Ready", "a fresh plan for the remaining unstarted upgrades is Ready")
        result = host.command(import_body(plan))
        check(status(result) == "completed", "after resolution a new journaled import completed the album")
        final = verify_consistent(lane, "after-second-operation", list(range(1, 12)), 12, [])
        album_lyrics_registered(lane, "after-second-operation-lyrics", list(range(1, 13)))
        check(sum(1 for p in (lane.base / "crescendarr" / "ops").iterdir() if p.is_dir()) == 2, "both operations (and their evidence) are retained")
    finally:
        host.stop()


def lane_postrow(lane: Lane) -> None:
    host = lane.boot()
    tid = lane.track_id(1)
    try:
        plan = ready_plan(lane, host)
        (lane.base / "faults" / f"pause-post-row-{tid}").write_text("armed\n")
        start = len(host.text())
        host.push(import_body(plan))
        host.wait_log(rf"FAULT_PAUSED post-row-{tid}", deadline=180, start=start)
        boundary = host.wait_log(rf"NATIVE_BOUNDARY post-row pre-link kind=upgrade track={tid} rowId=(\d+)[^\n]*", start=start)
        note("BOUNDARY " + boundary.group(0))
        at_kill = lane.state("at-sigkill")
        row = int(boundary.group(1))
        check(any(r[0] == row and r[1] == str(lane.audio(1)) for r in at_kill["files"]), "post-row: stock already inserted the TrackFile row at the destination")
        check([r[3] for r in at_kill["tracks"] if r[0] == tid] == [0], "pre-link: the track is NOT yet linked to that row")
        host.kill()
    except BaseException:
        host.stop()
        raise
    host = restart_after_kill(lane)
    try:
        scanned = stock_scan(lane, host, "scan-before-recovery")
        note(f"POSTROW_AFTER_SCAN track1={[r[3] for r in scanned['tracks'] if r[0] == tid]}")
        frozen = lane.view("before-recovery")
        state, text = recover(host)
        check(state == "failed" and "outcome=OperatorApprovalRequired" in text, "a row inserted but not linked needs operator approval (never auto-linked)")
        check(lane.view("after-unapproved") == frozen, "unapproved recovery changed nothing")
        op = lane.op_root().name
        allowed, token, _ = propose(host, op, "link-placed-row")
        check(allowed and token, "dry-run proposal to link the placed row is allowed")
        result, text = approve(host, op, "link-placed-row", token)
        check(result == "applied" and "LINK_PLACED_ROW" in text, "approved link applied")
        note("RECOVERY " + host.wait_log(r"IMPORT_OUTCOME[^\n]*outcome=Completed[^\n]*").group(0))
        final = verify_consistent(lane, "after-recovery", [1], 2, [3])
        album_lyrics_registered(lane, "after-recovery-lyrics", [1, 2])
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_lyric_crash(lane: Lane) -> None:
    host = crash_and_scan(lane, 1)
    t2 = lane.track_id(2)
    try:
        for point in (f"after-restore-lyric-{t2}", f"after-bind-lyric-{t2}"):
            (lane.base / "faults" / f"pause-{point}").write_text("armed\n")
            start = len(host.text())
            host.push({"name": "CrescendarrRecover"})
            host.wait_log(rf"FAULT_PAUSED {point}", deadline=180, start=start)
            print_view(f"AT_{point.upper()}", library_view(lane.state(f"at-{point}"), lane.seed, lane.release_id))
            host.kill()
            host = restart_after_kill_any(lane)
            stock_scan(lane, host, f"scan-after-{point}")
        state, text = recover(host)
        note("RECOVERY " + host.wait_log(r"IMPORT_OUTCOME[^\n]*").group(0))
        check(state == "completed" and "outcome=Completed" in text, "after two crashes inside lyric restore/bind, recovery completed")
        final = verify_consistent(lane, "after-recovery", [1], 2, [3])
        album_lyrics_registered(lane, "after-recovery-lyrics", [1, 2])
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def restart_after_kill_any(lane: Lane):
    host = lane.boot()
    recovers = [c for c in lane.state(f"after-restart-{lane.boots}")["commands"] if c[1] == "CrescendarrRecover"]
    check(any(m17.STATUS.get(c[2]) == "orphaned" for c in recovers), "stock Orphaned the interrupted plugin recovery command")
    return host


def run(bin_dir: Path, plugin: Path, seeder: Path, name: str, base: Path) -> None:
    lane = prepare(bin_dir, plugin, seeder, base, "twelve" if name.startswith("twelve") else "three")
    {"twelve-postmove-recover": lane_twelve_recover, "twelve-postmove-ambiguous": lane_twelve_ambiguous,
     "three-postrow": lane_postrow, "three-lyric-crash": lane_lyric_crash}[name](lane)
    note(f"M19_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m19-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M19_FAILED: {failure}", file=sys.stderr)
        note(f"M19_FAILED: {failure}")
        return 1
    finally:
        for log in sorted(base.glob("boot*.log"), key=lambda p: int(re.sub(r"\D", "", p.name) or 0)):
            print(f"=== {log.name} (Crescendarr, import, scan and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]", "ImportApprovedTracks", "DiskScanService")):
                    print(line[:2000])
        (runner / f"m19-{sys.argv[4]}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M19 {sys.argv[4]}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
