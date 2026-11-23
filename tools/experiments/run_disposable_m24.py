#!/usr/bin/env python3
"""M24 (issue #61): stale download-registry decisions, in the official SHA-256-pinned stock Lidarr v3.1.6.5079 daemon.

Same disposable host as M20 (synthetic library, stock Usenet Blackhole client, CDH OFF, configuration ONLY through the
stock Connect provider API). Library changes are made by ANOTHER ACTOR through the stock API (DELETE trackfile, quality
profile PUT), never by Crescendarr.

Lanes
  reeval-library-change  GuardedAuto: the good download is imported; a byte-identical second download is NoImprovement;
                         another actor deletes track 3 through the stock API; the second download is re-evaluated and
                         imports track 3 ONCE, while the already-imported first download is never replayed
  reeval-profile-audit   AuditOnly: after an approved import, a second download is NoImprovement; a quality-profile change
                         makes it a would-be upgrade (plan and status updated, ZERO library writes); reverting the profile
                         makes it NoImprovement again; the stale approval is refused; the first download stays Imported
  completed-idempotent   GuardedAuto: a completed import survives an unrelated library change, a restart and a quiet
                         steady state; changed bytes in its folder go to IdentityConflict (never re-imported); a corrupt
                         registry record fails closed to RegistryUnreadable with the evidence preserved
  ambiguous-source       GuardedAuto: two stock clients report the same completed folder; nothing is imported until the
                         ambiguity is gone, then it imports once

Usage: run_disposable_m24.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <lane>
"""
from __future__ import annotations

import copy
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
from run_disposable_m17 import Failure, Host, check, files_state, note, sha, status  # noqa: E402
from run_disposable_m18 import GOOD, MP3_256, Lane, prepare, verify_consistent  # noqa: E402
from run_disposable_m20 import WRONG, configure, health, place, wait_cycles, wait_summary  # noqa: E402

LANES = ("reeval-library-change", "reeval-profile-audit", "completed-idempotent", "ambiguous-source")
COPY = "Crescendarr Synthetic Album [good copy]"


def registry(lane: Lane) -> dict[str, dict]:
    """Every registry record by folder name. An unparseable record is reported, never silently skipped."""
    folder = lane.base / "crescendarr" / "downloads"
    entries: dict[str, dict] = {}
    for p in sorted(folder.glob("*.json")) if folder.exists() else []:
        try:
            entry = json.loads(p.read_text())
        except ValueError:
            entries[f"<unparseable {p.name}>"] = {"state": "<unparseable>", "file": p.name}
            continue
        entries[Path(entry["folder"]).name] = entry
    note("REGISTRY " + json.dumps({k: {x: v.get(x) for x in ("state", "planId", "disposition", "blocked", "reason", "evaluation", "completion")}
                                    for k, v in entries.items()})[:3000])
    return entries


def wait_registry(lane: Lane, name: str, predicate, what: str, deadline: float = 360) -> dict:
    limit = time.monotonic() + deadline
    last = None
    while time.monotonic() < limit:
        last = registry(lane).get(name)
        if last is not None and predicate(last):
            return last
        time.sleep(10)
    raise Failure(f"{what} (last registry record for {name}: {json.dumps(last)[:800] if last else None})")


def make_copy(lane: Lane) -> dict[str, str]:
    """A second, byte-identical completed download in its own folder (a re-grab of the same release)."""
    source = lane.base / "prepared-pristine" / GOOD
    shutil.copytree(source, lane.watch / COPY)
    return {p.name: sha(p) for p in (lane.watch / COPY).iterdir()}


def keep_pristine(lane: Lane) -> None:
    shutil.copytree(lane.base / "prepared" / GOOD, lane.base / "prepared-pristine" / GOOD)


def natives(host: Host, start: int = 0) -> int:
    return host.text()[start:].count("NATIVE_IMPORT")


def library(lane: Lane, label: str) -> tuple[dict, dict]:
    return lane.view(label), files_state(lane.artist_path)


def artist_profile(host: Host) -> dict:
    artist = host.api("GET", "artist")[0]
    return host.api("GET", f"qualityprofile/{artist['qualityProfileId']}")


def contains(item: dict, quality: int) -> bool:
    return (item.get("quality") or {}).get("id") == quality or any((s.get("quality") or {}).get("id") == quality for s in item.get("items") or [])


def prefer_quality_last(host: Host, quality: int) -> dict:
    """Another actor edits the artist's stock quality profile so `quality` ranks highest (and is the cutoff)."""
    original = artist_profile(host)
    profile = copy.deepcopy(original)
    items = profile["items"]
    index = next(i for i, it in enumerate(items) if contains(it, quality))
    item = items.pop(index)
    item["allowed"] = True
    for sub in item.get("items") or []:
        sub["allowed"] = True
    items.append(item)
    profile["cutoff"] = item["quality"]["id"] if item.get("quality") else item["id"]
    profile["upgradeAllowed"] = True
    host.api("PUT", f"qualityprofile/{profile['id']}", profile)
    note(f"PROFILE_CHANGED id={profile['id']} highest={quality} cutoff={profile['cutoff']}")
    return original


def delete_trackfile(host: Host, lane: Lane, n: int) -> None:
    view = lane.view(f"before-delete-{n}")
    file_id = view[n]["trackFileId"]
    check(file_id > 0, f"track {n} has a file to delete")
    host.api("DELETE", f"trackfile/{file_id}")
    note(f"ANOTHER_ACTOR deleted trackfile {file_id} (track {n}) through the stock API")
    check(lane.view(f"after-delete-{n}")[n]["trackFileId"] == 0, f"track {n} is missing after the stock delete")


def registry_file(lane: Lane, name: str) -> Path:
    for p in (lane.base / "crescendarr" / "downloads").glob("*.json"):
        try:
            if Path(json.loads(p.read_text())["folder"]).name == name:
                return p
        except ValueError:
            continue
    raise Failure(f"no registry record for {name}")


# ------------------------------------------------------------------ lanes

def lane_library_change(lane: Lane) -> None:
    keep_pristine(lane)
    host = lane.boot()
    try:
        configure(host, "GuardedAuto")
        place(lane, GOOD)
        start = len(host.text())
        wait_summary(host, r"imported=1", start)
        verify_consistent(lane, "after-first-import", [1], 2, [3])
        copy_bytes = make_copy(lane)
        wait_registry(lane, COPY, lambda e: e["state"] == "NoImprovement", "the byte-identical second download is recorded NoImprovement")
        check(registry(lane)[GOOD]["state"] == "Imported", "the first download is recorded Imported")
        before_natives = natives(host)
        delete_trackfile(host, lane, 3)
        mark = len(host.text())
        reg = wait_registry(lane, COPY, lambda e: e["state"] not in ("NoImprovement",),
                            "after another actor removed track 3, the NoImprovement download is RE-EVALUATED (not stale)")
        check(reg["state"] == "Imported", f"the re-evaluated download imported the missing track (state {reg['state']})")
        check(natives(host) == before_natives + 1, "exactly one further native import (the re-evaluated download only, never the completed one)")
        verify = lane.view("after-reevaluated-import")
        check(verify[3]["trackFileId"] > 0 and verify[3]["sha"] == copy_bytes[lane.audio(3).name], "track 3 restored from the second download's bytes")
        verify_consistent(lane, "after-reevaluated-import", [1], 2, [3])
        reg = registry(lane)
        check(reg[GOOD]["state"] == "Imported" and reg[GOOD].get("completion"), "the first download stays Imported with its completion evidence")
        check(reg[COPY].get("completion") and reg[COPY]["completion"]["operationId"] != reg[GOOD]["completion"]["operationId"],
              "each import is bound to its own operation")
        steady = len(host.text())
        later = wait_cycles(host, steady, 2)
        check(all("imported=0" in s for s in later) and natives(host, steady) == 0, "two further cycles: nothing imported again")
        check("CYCLE_DOWNLOAD " not in host.text()[steady:], "steady state is quiet (no per-download log lines)")
        check(not [h for h in health(host)], "health: clean")
        for name, expected in ((GOOD, lane.incoming), (COPY, copy_bytes)):
            check({p.name: sha(p) for p in (lane.watch / name).iterdir()} == expected, f"download source retained byte-for-byte: {name}")
    finally:
        host.stop()


def lane_profile_audit(lane: Lane) -> None:
    keep_pristine(lane)
    host = lane.boot()
    try:
        configure(host, "AuditOnly")
        place(lane, GOOD)
        start = len(host.text())
        wait_summary(host, r"awaitingApproval=1", start)
        good = registry(lane)[GOOD]
        result = host.command({"name": "CrescendarrImport", "downloadId": good["downloadId"], "planId": good["planId"]})
        check(status(result) == "completed", "explicit operator approval imported the first download")
        verify_consistent(lane, "after-approval", [1], 2, [3])
        copy_bytes = make_copy(lane)
        copy_record = wait_registry(lane, COPY, lambda e: e["state"] == "NoImprovement", "the second download is NoImprovement")
        frozen, frozen_files = library(lane, "before-profile-change")
        before_natives = natives(host)
        original = prefer_quality_last(host, MP3_256)
        changed = wait_registry(lane, COPY, lambda e: e["state"] != "NoImprovement",
                                "after the quality profile changed, the NoImprovement download is RE-EVALUATED (not stale)")
        note(f"REEVALUATED_AFTER_PROFILE state={changed['state']} disposition={changed.get('disposition')} plan={changed.get('planId')} blocked={changed.get('blocked')}")
        check(changed["state"] in ("AwaitingApproval", "Blocked", "NeedsReview") and changed["planId"] != copy_record["planId"],
              "audit only: a fresh plan, explanation and status are recorded")
        check(natives(host) == before_natives, "audit only: no native import after the profile change")
        check(library(lane, "after-profile-change") == (frozen, frozen_files), "audit only: zero row, link, lyric or byte change")
        stale_plan = changed["planId"]
        host.api("PUT", f"qualityprofile/{original['id']}", original)
        note("PROFILE_RESTORED")
        wait_registry(lane, COPY, lambda e: e["state"] == "NoImprovement", "after the profile is restored, the download is NoImprovement again")
        if stale_plan:
            mark = len(host.text())
            refused = host.command({"name": "CrescendarrImport", "downloadId": changed["downloadId"], "planId": stale_plan})
            refusal = re.findall(r"IMPORT_REFUSED[^\n]*", host.text()[mark:])[:1]
            note(f"STALE_APPROVAL status={status(refused)} {refusal}")
            check("plan no longer current" in host.text()[mark:] or "disposition" in host.text()[mark:], "an approval of the superseded plan is refused")
        check(natives(host) == before_natives and library(lane, "after-stale-approval") == (frozen, frozen_files), "still zero library change")
        reg = registry(lane)
        check(reg[GOOD]["state"] == "Imported" and reg[GOOD].get("completion"),
              "the approved import is recorded as Imported (bound to its completed operation), not as a reproducible NoImprovement")
        check({p.name: sha(p) for p in (lane.watch / COPY).iterdir()} == copy_bytes, "second download retained")
    finally:
        host.stop()


def lane_completed(lane: Lane) -> None:
    host = lane.boot()
    try:
        configure(host, "GuardedAuto")
        place(lane, GOOD)
        start = len(host.text())
        wait_summary(host, r"imported=1", start)
        verify_consistent(lane, "after-import", [1], 2, [3])
        imported = natives(host)
        delete_trackfile(host, lane, 3)
        mark = len(host.text())
        wait_cycles(host, mark, 2)
        check(natives(host) == imported, "an unrelated library change never replays a completed import (its plan would be Ready again)")
        check(registry(lane)[GOOD]["state"] == "Imported", "still Imported")
        after_change = library(lane, "after-unrelated-change")
        host.stop()
        host = lane.boot()
        host.wait_log(r"CYCLE_SCHEDULED reason=startup", start=0)
        wait_summary(host, r"alreadyHandled=1", 0)
        steady = len(host.text())
        wait_cycles(host, steady, 2)
        check(natives(host) == 0 and library(lane, "after-restart") == after_change, "after a restart: nothing replayed, library unchanged")
        check("CYCLE_DOWNLOAD " not in host.text(), "a durably handled download produces no per-download log lines")
        # The same folder now holds different bytes (a re-download or tampering): never treated as the handled download.
        good_track = lane.watch / GOOD / lane.audio(3).name
        wrong_track = lane.base / "prepared" / WRONG / lane.audio(3).name
        check(wrong_track.exists(), "the wrong-edition track exists in the prepared fixtures")
        shutil.copyfile(wrong_track, good_track)
        note("CHANGED_BYTES track 3 in the completed folder replaced")
        conflict = wait_registry(lane, GOOD, lambda e: e["state"] != "Imported", "changed bytes in a completed download are noticed")
        check(conflict["state"] == "IdentityConflict", f"changed bytes after import go to IdentityConflict review (got {conflict['state']})")
        check(conflict.get("completion"), "the original completion evidence is preserved in the review record")
        check(natives(host) == 0 and library(lane, "after-changed-bytes") == after_change, "zero library change on the conflict")
        check(any("download registry" in (h.get("message") or "") for h in health(host)), "health: the conflict is visible to the operator")
        record = registry_file(lane, GOOD)
        record.write_text("{ this is not json", encoding="utf-8")
        note(f"CORRUPTED_REGISTRY {record.name}")
        unreadable = wait_registry(lane, GOOD, lambda e: e["state"] == "RegistryUnreadable", "a corrupt registry record fails closed")
        preserved = list(record.parent.glob(record.name + ".unreadable-*"))
        check(len(preserved) == 1 and preserved[0].read_text() == "{ this is not json", "the corrupt record is preserved as evidence")
        check(natives(host) == 0 and library(lane, "after-corrupt-registry") == after_change, "zero library change on a corrupt registry")
        note(f"UNREADABLE {json.dumps(unreadable)[:600]}")
    finally:
        host.stop()


def lane_ambiguous(lane: Lane) -> None:
    host = lane.boot()
    try:
        clients = host.api("GET", "downloadclient")
        mirror = copy.deepcopy(clients[0])
        mirror.pop("id", None)
        mirror["name"] = clients[0]["name"] + " Mirror"
        mirror = host.api("POST", "downloadclient?forceSave=true", mirror)
        note(f"MIRROR_CLIENT id={mirror['id']} name={mirror['name']} (same watch folder)")
        before, before_files = library(lane, "before")
        configure(host, "GuardedAuto")
        place(lane, GOOD)
        record = wait_registry(lane, GOOD, lambda e: True, "the completed folder is observed")
        mark = len(host.text())
        wait_cycles(host, mark, 2)
        record = registry(lane)[GOOD]
        check(natives(host) == 0, "no import while two stock clients report the same completed folder")
        check(record["state"] == "AmbiguousSource", f"the folder is recorded AmbiguousSource (got {record['state']})")
        check(library(lane, "while-ambiguous") == (before, before_files), "zero library change")
        check(any("download registry" in (h.get("message") or "") for h in health(host)), "health: the ambiguity is visible")
        host.api("DELETE", f"downloadclient/{mirror['id']}")
        mark = len(host.text())
        wait_summary(host, r"imported=1", mark)
        verify_consistent(lane, "after-ambiguity-resolved", [1], 2, [3])
        check(registry(lane)[GOOD]["state"] == "Imported" and natives(host) == 1, "imported exactly once after the ambiguity was resolved")
    finally:
        host.stop()


def run(bin_dir: Path, plugin: Path, seeder: Path, name: str, base: Path) -> None:
    lane = prepare(bin_dir, plugin, seeder, base, "three")
    {"reeval-library-change": lane_library_change, "reeval-profile-audit": lane_profile_audit,
     "completed-idempotent": lane_completed, "ambiguous-source": lane_ambiguous}[name](lane)
    note(f"M24_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m24-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M24_FAILED: {failure}", file=sys.stderr)
        note(f"M24_FAILED: {failure}")
        return 1
    finally:
        for log in sorted(base.glob("boot*.log"), key=lambda p: int(re.sub(r"\D", "", p.name) or 0)):
            print(f"=== {log.name} (Crescendarr and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]")):
                    print(line[:2000])
        (runner / f"m24-{sys.argv[4]}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M24 {sys.argv[4]}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
