#!/usr/bin/env python3
"""M18 (issue #48): real stock-daemon crash/data-integrity lanes for the ONE Crescendarr plugin.

CI-only, Linux. Same disposable host as M17: official SHA-256-pinned Lidarr v3.1.6.5079, fresh data folder under
RUNNER_TEMP, synthetic tagged audio, stock Usenet Blackhole client with Completed Download Handling OFF. Reuses the
M17 harness primitives (separate seeder boot, then exactly one Crescendarr plugin). Every check reads DB rows from a
COPY of lidarr.db (+WAL) and file bytes from disk; log lines are used only to locate events.

Lanes
  three-postmove-upgrade  SIGKILL inside stock Import after the upgrade's file move, before its TrackFile row insert;
                          restart; real stock RescanFolders (album-wide cascade) BEFORE recovery; recover
  three-postmove-add      same boundary during the missing-track ADD
  three-postmove-noscan   same boundary (upgrade); recovery WITHOUT a scan must stop for operator approval with
                          zero mutations; an approved, targeted stock registration then completes with no cascade
  three-alias             a foreign album's track is linked to the operation's FRESH TrackFile row mid-operation
  three-lyric-negatives   from the post-scan state: conflicting bytes, corrupt/missing backup, dangling own link,
                          stale lyric row (each sticky RequiresReview, zero mutations), then a clean control recovery,
                          replay and restart replay
  three-guards            dangling symlinks, file/directory links and out-of-root aliases refused before any write
  twelve-postmove         the boundary mid-album (track 6 of 12): characterizes the remaining fail-closed review

Usage: run_disposable_m18.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <lane>
"""
from __future__ import annotations

import json
import os
import re
import shutil
import sqlite3
import sys
import tempfile
from dataclasses import dataclass, field as dc_field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
from run_disposable_m17 import (Failure, Host, check, configure_and_seed, db_state, environment, field, files_state,  # noqa: E402
                                install, library_view, note, plan_until_completed, print_view, sha, status, STATUS)

LANES = ("three-postmove-upgrade", "three-postmove-add", "three-postmove-noscan", "three-alias", "three-lyric-negatives",
         "three-guards", "twelve-postmove")
GOOD = "Crescendarr Synthetic Album [good]"
MP3_256, FLAC = 3, 6


@dataclass
class Lane:
    bin_dir: Path
    plugin: Path
    base: Path
    seed: dict
    data: Path
    home: Path
    incoming: dict = dc_field(default_factory=dict)
    boots: int = 0

    @property
    def release_id(self) -> int:
        return self.seed["releaseId"]

    @property
    def artist_path(self) -> Path:
        return Path(self.seed["artistPath"])

    @property
    def watch(self) -> Path:
        return self.base / "downloads" / "watch"

    def track(self, n: int) -> dict:
        return [t for t in self.seed["tracks"] if t["n"] == n][0]

    def track_id(self, n: int) -> int:
        return self.track(n)["trackId"]

    def audio(self, n: int, ext: str = ".mp3") -> Path:
        return self.artist_path / f"{n:02d} - Synthetic Track {n}{ext}"

    def lyric(self, n: int) -> Path:
        return self.artist_path / f"{n:02d} - Synthetic Track {n}.lrc"

    def state(self, label: str) -> dict:
        return db_state(self.data, label, self.base)

    def view(self, label: str) -> dict:
        return library_view(self.state(label), self.seed, self.release_id)

    def boot(self, crescendarr: bool = True, marker: str = "CRESCENDARR READY") -> Host:
        self.boots += 1
        host = Host(self.bin_dir, self.data, environment(self.base, self.home, crescendarr=crescendarr, seeder=False),
                    self.base / f"boot{self.boots}.log")
        host.start(marker)
        return host

    def op_root(self) -> Path:
        ops = sorted((self.base / "crescendarr" / "ops").glob("*/"))
        check(len(ops) == 1, f"exactly one operation directory exists ({[p.name for p in ops]})")
        return ops[0]

    def sql(self, statement: str, params: tuple = ()) -> None:
        connection = sqlite3.connect(self.data / "lidarr.db", timeout=30)
        try:
            connection.execute(statement, params)
            connection.commit()
        finally:
            connection.close()
        note(f"TEST_SQL {statement} {params}")


def prepare(bin_dir: Path, plugin: Path, seeder: Path, base: Path, fixture: str) -> Lane:
    data, home = base / "config", base / "home"
    for d in (data, home, base / "library", base / "downloads" / "nzb", base / "downloads" / "watch", base / "recycle", base / "faults"):
        d.mkdir(parents=True)
    (base / ".crescendarr-disposable-host").write_text("CRESCENDARR_DISPOSABLE_HOST_V1\n", encoding="utf-8")
    seeder_dir = install(seeder, data, "CrescendarrFixtureSeeder")
    boot0 = Host(bin_dir, data, environment(base, home, crescendarr=False, seeder=True), base / "boot0.log")
    boot0.start("")
    try:
        seed = configure_and_seed(boot0, base, fixture)
    finally:
        boot0.stop()
    shutil.rmtree(seeder_dir)
    install(plugin, data, "Crescendarr")
    note(f"SEED {json.dumps(seed)[:2000]}")
    return Lane(bin_dir, plugin, base, seed, data, home)


def ready_plan(lane: Lane, host: Host) -> str:
    if not (lane.watch / GOOD).exists():
        shutil.move(str(lane.base / "prepared" / GOOD), str(lane.watch / GOOD))
        lane.incoming = {p.name: sha(p) for p in (lane.watch / GOOD).iterdir()}
    plan = plan_until_completed(host, GOOD)
    check(field(plan, "disposition") == "Ready", "real observed download produced a Ready plan")
    return plan


def import_body(plan: str) -> dict:
    return {"name": "CrescendarrImport", "downloadId": field(plan, "download"), "planId": field(plan, "planId")}


def recover(host: Host, approve: str | None = None) -> tuple[str, str]:
    start = len(host.text())
    body = {"name": "CrescendarrRecover"}
    if approve:
        body["approve"] = approve
    result = host.command(body)
    return status(result), host.text()[start:]


def propose(host: Host, op: str, action: str) -> tuple[bool, str | None, str]:
    """M19 dry run: returns (allowed, token, PROPOSAL line). Nothing is changed."""
    start = len(host.text())
    host.command({"name": "CrescendarrPropose", "operationId": op, "action": action})
    line = host.wait_log(rf"CRESCENDARR PROPOSAL op={op} action={re.escape(action)} [^\n]*", start=start).group(0)
    note("PROPOSAL_LINE " + line[:1500])
    token = re.search(r"token=(\w+)", line).group(1)
    return "allowed=True" in line, (None if token == "none" else token), line


def approve(host: Host, op: str, action: str, token: str) -> tuple[str, str]:
    """M19 typed approval: returns ('applied' | 'rejected', log text since the command)."""
    start = len(host.text())
    host.command({"name": "CrescendarrApprove", "operationId": op, "action": action, "token": token})
    text = host.text()[start:]
    if f"APPROVAL_APPLIED op={op} action={action}" in text:
        return "applied", text
    check(f"APPROVAL_REJECTED op={op} action={action}" in text, f"approval of {action} reports applied or rejected")
    return "rejected", text


def foreign_rows(lane: Lane, state: dict) -> list:
    foreign = lane.seed.get("foreign")
    if not foreign:
        return []
    ids = {t["trackId"] for t in foreign["tracks"]}
    return [r for r in state["tracks"] if r[0] in ids]


# ------------------------------------------------------------------ the native post-move / pre-row boundary

def crash_at_post_move(lane: Lane, host: Host, plan: str, n: int) -> dict:
    """Pause INSIDE stock ImportApprovedTracks.Import after the file move, before AddMany, then SIGKILL the daemon."""
    tid = lane.track_id(n)
    (lane.base / "faults" / f"pause-post-move-{tid}").write_text("armed\n")
    start = len(host.text())
    host.push(import_body(plan))
    host.wait_log(rf"FAULT_PAUSED post-move-{tid}", deadline=180, start=start)
    boundary = host.wait_log(rf"NATIVE_BOUNDARY post-move pre-row kind=(\w+) track={tid} rowId=(\d+) placed=(\w+)[^\n]*", start=start)
    note("BOUNDARY " + boundary.group(0))
    check(boundary.group(2) == "0" and boundary.group(3) == "True",
          f"track {n}: stock passed the post-move boundary with the file in place and NO row id yet (CreateAfterTrackImport)")
    at_kill = lane.state("at-sigkill")
    dest = lane.audio(n)
    print_view("AT_SIGKILL", library_view(at_kill, lane.seed, lane.release_id))
    check(dest.exists() and sha(dest) == lane.incoming[dest.name], f"track {n}: incoming bytes already moved to the library destination")
    check(not any(r[1] == str(dest) for r in at_kill["files"]), f"track {n}: no TrackFile row exists for the moved file (pre-row)")
    check([r[3] for r in at_kill["tracks"] if r[0] == tid] == [0], f"track {n}: the track is not linked to any TrackFile")
    original = lane.track(n)
    if original.get("owned"):
        check(not any(r[0] == original["trackFileId"] for r in at_kill["files"]), f"track {n}: stock already deleted the original TrackFile row")
        recycled = [p for p in (lane.base / "recycle").rglob(dest.name)]
        check(any(sha(p) == original["sha"] for p in recycled), f"track {n}: stock already recycled the original audio bytes")
    host.kill()
    return at_kill


def restart_after_kill(lane: Lane) -> Host:
    host = lane.boot()
    imports = [c for c in lane.state("after-restart")["commands"] if c[1] == "CrescendarrImport"]
    note(f"RESTART_COMMANDS {imports}")
    check(any(STATUS.get(c[2]) == "orphaned" for c in imports), "stock Orphaned the interrupted plugin import; it never resumes")
    check("NATIVE_IMPORT" not in host.text() and "NATIVE_BOUNDARY" not in host.text(), "nothing resumed from the stock Commands table")
    return host


def stock_scan(lane: Lane, host: Host, label: str) -> dict:
    scan = host.command({"name": "RescanFolders"})
    check(status(scan) == "completed", "a real queued stock RescanFolders ran BEFORE the recovery command")
    state = lane.state(label)
    print_view(label.upper(), library_view(state, lane.seed, lane.release_id))
    return state


def check_cascade(lane: Lane, before: dict, after: dict, recycled_lyrics: list[int]) -> None:
    """The scan with an unregistered audio file re-imports the album: fresh rows, registered lyrics recycled."""
    b = library_view(before, lane.seed, lane.release_id)
    a = library_view(after, lane.seed, lane.release_id)
    flac = lane.track(2)
    check(a[2]["trackFileId"] not in (0, b[2]["trackFileId"]), "destructive rescan: the UNSELECTED superior FLAC got a fresh TrackFile row")
    check(a[2]["sha"] == flac["sha"] and Path(a[2]["path"]) == Path(flac["path"]), "destructive rescan: superior FLAC audio bytes survived in place")
    for n in recycled_lyrics:
        lyric = lane.lyric(n)
        recycled = [p for p in (lane.base / "recycle").rglob(lyric.name)]
        check(not lyric.exists() and any(sha(p) == lane.track(n)["lyricSha"] for p in recycled),
              f"destructive rescan: stock physically RECYCLED track {n}'s registered lyric")
    note(f"CASCADE_OBSERVED rows_before={[(k, v['trackFileId']) for k, v in sorted(b.items())]} rows_after={[(k, v['trackFileId']) for k, v in sorted(a.items())]}")


def verify_consistent(lane: Lane, label: str, upgrades: list[int], superior: int, added: list[int]) -> dict:
    """Rotation-tolerant end state: identity, path, bytes, link and lyric registration (never stored row IDs)."""
    state = lane.state(label)
    view = library_view(state, lane.seed, lane.release_id)
    print_view(label.upper(), view)
    for n in upgrades:
        v, name = view[n], lane.audio(n).name
        check(v["trackFileId"] > 0 and Path(v["path"]) == lane.audio(n) and v["quality"] == MP3_256 and v["sha"] == lane.incoming[name],
              f"track {n}: linked to a row at its destination holding the incoming MP3-256 bytes")
        check(v["lyrics"] == [(v["lyrics"][0][0], lane.lyric(n).name)] if v["lyrics"] else False, f"track {n}: exactly its lyric is registered to its CURRENT TrackFile")
        check(sha(lane.lyric(n)) == lane.track(n)["lyricSha"], f"track {n}: lyric bytes are the original bytes")
    s, flac = view[superior], lane.track(superior)
    check(s["trackFileId"] > 0 and Path(s["path"]) == Path(flac["path"]) and s["sha"] == flac["sha"] and s["quality"] == FLAC,
          f"superior track {superior}: original FLAC bytes linked at their original path")
    check(s["lyrics"] == [(s["lyrics"][0][0], lane.lyric(superior).name)] if s["lyrics"] else False,
          f"superior track {superior}: its lyric is registered to its CURRENT TrackFile")
    check(sha(lane.lyric(superior)) == flac["lyricSha"], f"superior track {superior}: lyric bytes are the original bytes")
    for n in added:
        v = view[n]
        check(v["trackFileId"] > 0 and Path(v["path"]) == lane.audio(n) and v["sha"] == lane.incoming[lane.audio(n).name],
              f"missing track {n}: added from the incoming bytes")
    releases = {r[0]: r for r in state["releases"]}
    check(releases[lane.release_id][2] == 1 and releases[lane.seed["alternateReleaseId"]][2] == 0, "monitored edition unchanged")
    check({p.name: sha(p) for p in (lane.watch / GOOD).iterdir()} == lane.incoming, "completed download left intact")
    check(not any("/crescendarr/" in r[1] for r in state["files"]), "no TrackFile row points into operation-owned staging")
    ids = [v["trackFileId"] for v in view.values() if v["trackFileId"] > 0]
    check(len(ids) == len(set(ids)), "no two album tracks share a TrackFile")
    return view


def replay_and_restart(lane: Lane, host: Host, expected: dict, label: str) -> Host:
    state, text = recover(host)
    check("RECOVERY_SKIP" in text and "NATIVE_IMPORT" not in text, "identical replay: a second recovery does nothing")
    host.stop()
    host = lane.boot()
    state, text = recover(host)
    check("RECOVERY_SKIP" in text and "NATIVE_IMPORT" not in text, "restart replay: recovery after another restart does nothing")
    scan = host.command({"name": "RescanFolders"})
    check(status(scan) == "completed", "stock rescan after recovery completed")
    check(lane.view(label) == expected, "stock rescan after recovery changed no row, link or lyric (no cascade left)")
    return host


def review_summary(lane: Lane, status_text: str) -> dict:
    path = lane.op_root() / "review.json"
    check(path.exists(), "an inspectable operator summary (review.json) was written")
    summary = json.loads(path.read_text())
    note(f"REVIEW_SUMMARY {json.dumps(summary)[:3000]}")
    check(summary.get("status") == status_text and summary.get("settled") is False and summary.get("success") is False,
          f"summary: status {status_text}, download NOT settled, NOT success")
    check(all(k in summary for k in ("reasons", "tracks", "albumLyrics")), "summary lists reasons, per-track identity/rows/hashes and album lyric linkage")
    return summary


# ------------------------------------------------------------------ lanes

def lane_postmove(lane: Lane, n: int, recycled: list[int]) -> None:
    host = lane.boot()
    try:
        plan = ready_plan(lane, host)
        before = lane.state("before")
        crash_at_post_move(lane, host, plan, n)
    except BaseException:
        host.stop()
        raise
    host = restart_after_kill(lane)
    try:
        scanned = stock_scan(lane, host, "scan-before-recovery")
        check_cascade(lane, before, scanned, recycled)
        state, text = recover(host)
        note("RECOVERY " + host.wait_log(r"IMPORT_OUTCOME[^\n]*").group(0))
        check(state == "completed" and "outcome=Completed" in text, "recovery completed the WHOLE operation from the journal and fresh observation")
        for r in recycled:
            check(re.search(rf"RESTORE_ALBUM_LYRIC[^\n]*{re.escape(lane.lyric(r).name)}", text) is not None, f"track {r}'s recycled lyric restored from its verified backup")
        final = verify_consistent(lane, "after-recovery", [1], 2, [3])
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_noscan(lane: Lane) -> None:
    host = lane.boot()
    try:
        plan = ready_plan(lane, host)
        crash_at_post_move(lane, host, plan, 1)
    except BaseException:
        host.stop()
        raise
    host = restart_after_kill(lane)
    try:
        frozen, frozen_files = lane.view("before-unapproved"), files_state(lane.artist_path)
        state, text = recover(host)
        check(state == "failed" and "outcome=OperatorApprovalRequired" in text, "without a scan, recovery stops for OPERATOR APPROVAL (placed file has no row)")
        check(lane.view("after-unapproved") == frozen and files_state(lane.artist_path) == frozen_files, "unapproved recovery changed no row, link, lyric or byte")
        summary = review_summary(lane, "OperatorApprovalRequired")
        check("InsertFreshTrackFileForIncoming" in json.dumps(summary.get("proposal")), "the proposal names the exact host action")
        # A NEW import for the same album must not start while this operation is unfinished.
        fresh = plan_until_completed(host, GOOD)
        start = len(host.text())
        host.command({"name": "CrescendarrImport", "downloadId": field(fresh, "download"), "planId": field(fresh, "planId") or "none"})
        check("IMPORT_REFUSED" in host.text()[start:], f"a second import for the blocked album is refused (fresh disposition={field(fresh, 'disposition')})")
        check(lane.view("after-second-import") == frozen, "the refused second import changed nothing")
        op = lane.op_root().name
        # M19: approval is a typed token bound to the operation, journal generation, action and FRESH snapshot.
        check(approve(host, op, "register-placed", "0" * 32)[0] == "rejected", "a forged/stale approval token is rejected")
        check(lane.view("after-forged-approval") == frozen, "the rejected approval changed nothing")
        allowed, token, _ = propose(host, op, "register-placed")
        check(allowed and token, "dry-run proposal for register-placed is allowed and yields a token")
        result, text = approve(host, op, "register-placed", token)
        note("RECOVERY " + host.wait_log(r"IMPORT_OUTCOME[^\n]*outcome=Completed[^\n]*").group(0))
        check(result == "applied" and "REGISTER_PLACED" in text, "approved targeted stock registration + recovery completed the operation")
        final = verify_consistent(lane, "after-recovery", [1], 2, [3])
        check(final[2]["trackFileId"] == lane.track(2)["trackFileId"], "no cascade: the superior FLAC kept its ORIGINAL TrackFile row")
        check(not any(p.name.endswith(".lrc") for p in (lane.base / "recycle").rglob("*")), "no lyric was ever recycled on this path")
        host = replay_and_restart(lane, host, final, "after-replay")
    finally:
        host.stop()


def lane_alias(lane: Lane) -> None:
    foreign = lane.seed["foreign"]
    f3 = [t for t in foreign["tracks"] if t["n"] == 3][0]["trackId"]
    host = lane.boot()
    try:
        plan = ready_plan(lane, host)
        (lane.base / "faults" / "pause-after-native-1").write_text("armed\n")
        start = len(host.text())
        host.push(import_body(plan))
        host.wait_log(r"FAULT_PAUSED after-native-1", deadline=180, start=start)
        paused = lane.state("paused-after-upgrade")
        fresh = [r[3] for r in paused["tracks"] if r[0] == lane.track_id(1)][0]
        check(fresh not in (0, lane.track(1)["trackFileId"]), "track 1 linked to a fresh TrackFile row by the native upgrade")
        lane.sql('UPDATE "Tracks" SET "TrackFileId" = ? WHERE "Id" = ?', (fresh, f3))
        (lane.base / "faults" / "release-after-native-1").write_text("go\n")
        outcome = host.wait_log(r"IMPORT_OUTCOME[^\n]*", start=start).group(0)
        note("OUTCOME " + outcome)
        text = host.text()[start:]
        check("outcome=RequiresReview" in outcome, "a foreign album track aliasing the FRESH row forces RequiresReview")
        check("created by this operation is linked to another track" in text, "the M13 reconciler names the fresh-row alias")
        after = lane.state("after-alias-review")
        view = library_view(after, lane.seed, lane.release_id)
        check(view[3]["trackFileId"] == 0 and not lane.audio(3).exists(), "the pending missing-track add was NOT imported")
        check(view[1]["lyrics"] == [] and sha(lane.lyric(1)) == lane.track(1)["lyricSha"], "track 1's detached lyric was NOT relinked; its bytes are intact")
        check(view[2]["trackFileId"] == lane.track(2)["trackFileId"] and view[2]["sha"] == lane.track(2)["sha"], "superior FLAC untouched")
        check([r[3] for r in after["tracks"] if r[0] == f3] == [fresh], "the foreign alias was not 'repaired' by the plugin")
        rows = {r[0]: r[3] for r in foreign_rows(lane, after)}
        shared = foreign["sharedTrackFileId"]
        check([rows[t["trackId"]] for t in foreign["tracks"] if t["n"] in (1, 2)] == [shared, shared], "unrelated foreign sharing untouched")
        review_summary(lane, "RequiresReview")
        state, text = recover(host)
        check("RECOVERY_SKIP" in text and "NATIVE_IMPORT" not in text, "RequiresReview is sticky")
    finally:
        host.stop()


def lane_negatives(lane: Lane) -> None:
    host = lane.boot()
    try:
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
    finally:
        host.stop()
    snapshot = lane.base.with_name(lane.base.name + "-snapshot")
    shutil.copytree(lane.base, snapshot, symlinks=True, ignore=shutil.ignore_patterns("db", "boot*.log", "logs", "clr-debug-pipe-*", "dotnet-diagnostic-*"))
    lyrics = json.loads((lane.op_root() / "album-lyrics.json").read_text())
    backup2 = Path([l for l in lyrics if l["RelativePath"] == lane.lyric(2).name][0]["backup"])
    original_lyric_bytes = (lane.base / "recycle").rglob(lane.lyric(2).name).__next__().read_bytes()

    def restore() -> None:
        for child in list(lane.base.iterdir()):
            if child.name == "db" or child.name.startswith(("boot", "clr-debug-pipe-", "dotnet-diagnostic-")):
                continue
            shutil.rmtree(child) if child.is_dir() and not child.is_symlink() else child.unlink()
        for child in snapshot.iterdir():
            target = lane.base / child.name
            shutil.copytree(child, target, symlinks=True) if child.is_dir() else shutil.copy2(child, target)

    cases = [
        ("conflicting bytes at the recycled lyric path", lambda: lane.lyric(2).write_bytes(b"[00:00.00] SOMEONE ELSE'S LYRIC\n"),
         r"unrelated bytes occupy the lyric path"),
        ("corrupt album-lyric backup", lambda: backup2.write_bytes(b"corrupt"), r"lyric missing and backup unverifiable"),
        ("missing album-lyric backup", lambda: backup2.unlink(), r"lyric missing and backup unverifiable"),
        ("dangling nonzero link on the pending add", lambda: lane.sql('UPDATE "Tracks" SET "TrackFileId" = 987654 WHERE "Id" = ?', (lane.track_id(3),)),
         r"dangling"),
        ("stale lyric row pointing at a deleted TrackFile", lambda: (lane.lyric(2).write_bytes(original_lyric_bytes), lane.sql(
            'INSERT INTO "LyricFiles" ("ArtistId","AlbumId","TrackFileId","RelativePath","Extension","Added","LastUpdated") VALUES (?,?,?,?,?,?,?)',
            (lane.seed["artistId"], lane.seed["albumId"], lane.track(2)["trackFileId"], lane.lyric(2).name, ".lrc", "2026-01-01 00:00:00Z", "2026-01-01 00:00:00Z"))),
         r"stale lyric row"),
    ]
    for name, mutate, reason in cases:
        restore()
        mutate()
        host = lane.boot()
        try:
            frozen = lane.state(f"neg-before-{lane.boots}")
            frozen_files, frozen_lyric = files_state(lane.artist_path), (lane.lyric(2).read_bytes() if lane.lyric(2).exists() else None)
            state, text = recover(host)
            check(state == "failed" and "outcome=RequiresReview" in text, f"{name}: sticky RequiresReview")
            check(re.search(reason, text, re.IGNORECASE) is not None, f"{name}: reason names the hazard ({reason})")
            after = lane.state(f"neg-after-{lane.boots}")
            check(after["tracks"] == frozen["tracks"] and after["files"] == frozen["files"], f"{name}: zero TrackFile or link changes")
            # M19: entering review runs the protective lyric-only salvage for OTHER, unambiguous lyrics. The hazard itself
            # is never touched, nothing is overwritten, and every change must be one the salvage logged.
            salvage = re.search(r"SALVAGE op=\S+ restored=\[([^\]]*)\] bound=\[([^\]]*)\]", text)
            restored = salvage.group(1) if salvage else ""
            bound = salvage.group(2) if salvage else ""
            note(f"NEGATIVE_SALVAGE case={name!r} restored=[{restored}] bound=[{bound}]")
            check(set(frozen["lyrics"]) <= set(after["lyrics"]), f"{name}: no lyric row removed")
            check(all(row[2] in bound for row in set(after["lyrics"]) - set(frozen["lyrics"])), f"{name}: every new lyric row is a logged salvage bind")
            changed = {k for k in set(files_state(lane.artist_path)) | set(frozen_files) if files_state(lane.artist_path).get(k) != frozen_files.get(k)}
            check(all(k in restored and k not in frozen_files for k in changed), f"{name}: the only library byte changes are logged restores of MISSING lyrics (nothing overwritten)")
            hazard_on_lyric2 = "dangling" not in reason
            if hazard_on_lyric2:
                check((lane.lyric(2).read_bytes() if lane.lyric(2).exists() else None) == frozen_lyric and lane.lyric(2).name not in restored + bound,
                      f"{name}: the hazardous lyric was neither overwritten, restored nor registered")
            review_summary(lane, "RequiresReview")
            state, text = recover(host)
            check("RECOVERY_SKIP" in text, f"{name}: a second recovery does nothing (sticky)")
        finally:
            host.stop()

    restore()
    host = lane.boot()
    try:
        state, text = recover(host)
        check(state == "completed" and "outcome=Completed" in text, "control: the unmodified post-scan state recovers to Completed")
        final = verify_consistent(lane, "control-after-recovery", [1], 2, [3])
        host = replay_and_restart(lane, host, final, "control-after-replay")
    finally:
        host.stop()


def lane_guards(lane: Lane) -> None:
    outside = Path(tempfile.mkdtemp(prefix="crescendarr-m18-outside-", dir=lane.base.parent))
    hold = lane.base / "hold"
    hold.mkdir()
    host = lane.boot()
    try:
        ready_plan(lane, host)
        base_view, base_files = lane.view("guards-before"), files_state(lane.artist_path)
        ops = lane.base / "crescendarr" / "ops"

        def case(name: str, setup, undo, hazard: Path | None) -> None:
            setup()
            outside_before = files_state(outside)
            ops_before = sorted(p.name for p in ops.glob("*")) if ops.exists() else []
            plan = plan_until_completed(host, GOOD)
            start = len(host.text())
            result = host.command({"name": "CrescendarrImport", "downloadId": field(plan, "download"), "planId": field(plan, "planId") or "none"})
            text = host.text()[start:]
            note(f"GUARD_CASE {name} status={status(result)} refused={'IMPORT_REFUSED' in text}")
            check("IMPORT_REFUSED" in text or status(result) == "failed", f"{name}: import refused")
            check("NATIVE_IMPORT" not in text and "NATIVE_BOUNDARY" not in text, f"{name}: no native import was attempted")
            check(lane.view(f"guard-{name[:12]}") == base_view, f"{name}: no DB row, link or lyric change")
            now = {k: v for k, v in files_state(lane.artist_path).items() if hazard is None or k != hazard.name}
            expected = {k: v for k, v in base_files.items() if hazard is None or k != hazard.name}
            check(now == expected, f"{name}: no library byte change")
            check(files_state(outside) == outside_before, f"{name}: nothing was written outside the disposable root")
            check((sorted(p.name for p in ops.glob("*")) if ops.exists() else []) == ops_before, f"{name}: no operation was published")
            undo()

        lyric2 = lane.lyric(2)
        case("dangling symlink at a registered lyric",
             lambda: (shutil.move(str(lyric2), str(hold / lyric2.name)), os.symlink("/nonexistent-crescendarr-m18/lyric.lrc", lyric2)),
             lambda: (lyric2.unlink(), shutil.move(str(hold / lyric2.name), str(lyric2))), lyric2)
        candidate = lane.watch / GOOD / "02 - Synthetic Track 2.mp3"
        case("download candidate is a file link to outside the root",
             lambda: (shutil.move(str(candidate), str(outside / candidate.name)), os.symlink(outside / candidate.name, candidate)),
             lambda: (candidate.unlink(), shutil.move(str(outside / candidate.name), str(candidate))), None)
        artist = lane.artist_path
        case("artist folder aliased to a copy outside the root",
             lambda: (shutil.move(str(artist), str(hold / "artist")), shutil.copytree(hold / "artist", outside / "artist-copy"), os.symlink(outside / "artist-copy", artist)),
             lambda: (artist.unlink(), shutil.rmtree(outside / "artist-copy"), shutil.move(str(hold / "artist"), str(artist))), None)
        staging = lane.base / "crescendarr" / "staging"
        staging.parent.mkdir(parents=True, exist_ok=True)
        (outside / "staging-target").mkdir()
        case("operation staging directory is a link to outside the root",
             lambda: os.symlink(outside / "staging-target", staging), lambda: staging.unlink(), None)
        dangling = lane.audio(3)
        case("dangling symlink at the add-missing destination",
             lambda: os.symlink("/nonexistent-crescendarr-m18/03.mp3", dangling), lambda: dangling.unlink(), dangling)

        plan = plan_until_completed(host, GOOD)
        result = host.command(import_body(plan))
        check(status(result) == "completed" and "outcome=Completed" in host.text(), "control: with every hazard removed the import completes")
        verify_consistent(lane, "guards-control", [1], 2, [3])
    finally:
        host.stop()
        shutil.rmtree(outside, ignore_errors=True)


def lane_twelve_postmove(lane: Lane) -> None:
    host = lane.boot()
    try:
        plan = ready_plan(lane, host)
        crash_at_post_move(lane, host, plan, 6)
    except BaseException:
        host.stop()
        raise
    host = restart_after_kill(lane)
    try:
        stock_scan(lane, host, "scan-before-recovery")
        state, text = recover(host)
        outcome = host.wait_log(r"IMPORT_OUTCOME[^\n]*").group(0)
        note("RECOVERY " + outcome)
        after = lane.state("after-recovery")
        view = library_view(after, lane.seed, lane.release_id)
        lyrics = json.loads((lane.op_root() / "album-lyrics.json").read_text())
        for n in range(1, 13):
            path, backup = lane.lyric(n), Path([l for l in lyrics if l["RelativePath"] == lane.lyric(n).name][0]["backup"])
            seeded = lane.track(n)["lyricSha"]
            check((path.exists() and sha(path) == seeded) or (not path.exists() and sha(backup) == seeded),
                  f"track {n}: lyric present with original bytes, or absent with a hash-verified album backup (never overwritten)")
        check(view[12]["sha"] == lane.track(12)["sha"], "superior FLAC 12 audio bytes survived")
        check(not any("/crescendarr/" in r[1] for r in after["files"]), "no TrackFile row points into staging")
        if "outcome=Completed" in outcome:
            verify_consistent(lane, "twelve-final", list(range(1, 12)), 12, [])
            note("TWELVE_POSTMOVE_RESULT Completed")
        else:
            check("outcome=RequiresReview" in outcome, "mid-album cascade ends Completed or sticky RequiresReview (never false success)")
            review_summary(lane, "RequiresReview")
            state, text = recover(host)
            check("RECOVERY_SKIP" in text, "RequiresReview is sticky")
            note("TWELVE_POSTMOVE_RESULT RequiresReview (documented residual)")
    finally:
        host.stop()


def run(bin_dir: Path, plugin: Path, seeder: Path, name: str, base: Path) -> None:
    lane = prepare(bin_dir, plugin, seeder, base, "twelve" if name.startswith("twelve") else "three")
    {
        "three-postmove-upgrade": lambda: lane_postmove(lane, 1, [2]),
        "three-postmove-add": lambda: lane_postmove(lane, 3, [1, 2]),
        "three-postmove-noscan": lambda: lane_noscan(lane),
        "three-alias": lambda: lane_alias(lane),
        "three-lyric-negatives": lambda: lane_negatives(lane),
        "three-guards": lambda: lane_guards(lane),
        "twelve-postmove": lambda: lane_twelve_postmove(lane),
    }[name]()
    note(f"M18_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m18-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M18_FAILED: {failure}", file=sys.stderr)
        note(f"M18_FAILED: {failure}")
        return 1
    finally:
        for log in sorted(base.glob("boot*.log"), key=lambda p: int(re.sub(r"\D", "", p.name) or 0)):
            print(f"=== {log.name} (Crescendarr, import, scan and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]", "ImportApprovedTracks", "UpgradeMediaFileService",
                                                                              "RecycleBin", "DiskScanService", "ExistingExtraFile", "LyricService")):
                    print(line[:2000])
        (runner / f"m18-{sys.argv[4]}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M18 {sys.argv[4]}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
