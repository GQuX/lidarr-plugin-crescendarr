#!/usr/bin/env python3
"""M25 (issue #62): the AUDIT-ONLY BETA build on the official SHA-256-pinned stock Lidarr v3.1.6.5079 daemon.

The audit build runs WITHOUT any Crescendarr CI opt-in variable or disposable marker (as on an ordinary host); only the
synthetic fixture seeding uses the separate test seeder in its own setup boot. The download client has an ordinary name
and is selected explicitly in Settings > Connect. Library state is proven unchanged with BEFORE/AFTER baselines: every
file byte and name under the library root, TrackFile/Track/LyricFile associations, monitored editions, root folders,
stock settings and the download sources.

Lanes
  audit-readonly     default Disabled observes nothing; then Audit only previews Upgrade/Keep/Add per track and the review
                     reason; injected writer commands, a database-injected GuardedAuto mode, restarts, a profile change, a
                     disallowed path, an unreadable artist folder, a disabled client and an unselected client all cause
                     ZERO library mutation; diagnostics stay under Lidarr's AppData folder and the shareable copy is redacted
  package-lifecycle  install from the candidate zip, upgrade to a second build, roll back, uninstall: settings and state
                     survive upgrade/rollback, uninstall leaves stock Lidarr clean, the library never changes

Usage: run_disposable_m25.py <lidarr-bin> <audit-dll-or-zip-dir> <seeder-dll> <lane>
  For package-lifecycle the second argument is a folder holding crescendarr-audit-beta-*.zip (two versions).
"""
from __future__ import annotations

import json
import os
import re
import shutil
import sqlite3
import stat
import sys
import tempfile
import time
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
from run_disposable_m17 import Failure, Host, check, files_state, note, sha, status  # noqa: E402
from run_disposable_m18 import GOOD, MP3_256, Lane, prepare  # noqa: E402
from run_disposable_m20 import WRONG  # noqa: E402

LANES = ("audit-readonly", "package-lifecycle")
WRITER_COMMANDS = ("CrescendarrImport", "CrescendarrRecover", "CrescendarrApprove", "CrescendarrPropose", "CrescendarrCycle",
                   "CrescendarrPlan", "CrescendarrOperatorStatus", "CrescendarrSeedFixture")
NORMAL_CLIENT = "Usenet Blackhole"


def audit_env(lane: Lane) -> dict[str, str]:
    """An ordinary host: no CI markers, no Crescendarr opt-in, no disposable root variable."""
    return {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(lane.home), "XDG_CONFIG_HOME": str(lane.home), "LANG": "C.UTF-8",
            "TMPDIR": str(lane.base), "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", ""),
            "Lidarr__Server__BindAddress": "127.0.0.1", "Lidarr__Server__Port": str(m17.PORT)}


def boot(lane: Lane, marker: str = "CRESCENDARR AUDIT_BUILD_READY") -> Host:
    lane.boots += 1
    host = Host(lane.bin_dir, lane.data, audit_env(lane), lane.base / f"boot{lane.boots}.log")
    host.start(marker)
    return host


def state_dir(lane: Lane) -> Path:
    return lane.data / "crescendarr-audit"


def tree(root: Path) -> dict[str, str]:
    """Every file AND directory name under root, with file bytes: catches new probe files, folders and renames."""
    out = {}
    for p in sorted(root.rglob("*")):
        rel = str(p.relative_to(root))
        out[rel] = "<dir>" if p.is_dir() else sha(p)
    return out


def stock_settings(host: Host) -> dict:
    snap = {
        "mediamanagement": host.api("GET", "config/mediamanagement"),
        "downloadclientconfig": host.api("GET", "config/downloadclient"),
        "downloadclients": host.api("GET", "downloadclient"),
        "qualityprofiles": host.api("GET", "qualityprofile"),
        "rootfolders": [{k: r.get(k) for k in ("id", "path", "defaultQualityProfileId")} for r in host.api("GET", "rootfolder")],
        "naming": host.api("GET", "config/naming"),
        "otherNotifications": [n for n in host.api("GET", "notification") if n.get("implementation") != "CrescendarrAuditConnect"],
    }
    return json.loads(json.dumps(snap, sort_keys=True))


def library_rows(lane: Lane, label: str) -> dict:
    state = lane.state(label)
    return {k: state[k] for k in ("tracks", "files", "lyrics", "releases")}


class Baseline:
    """Immutable BEFORE picture; compare() lists every difference."""

    def __init__(self, lane: Lane, host: Host, label: str):
        self.lane = lane
        self.library = tree(lane.base / "library")
        self.rows = library_rows(lane, label)
        self.settings = stock_settings(host)
        self.downloads = tree(lane.base / "downloads")
        note(f"BASELINE {label} libraryEntries={len(self.library)} trackFiles={len(self.rows['files'])} lyrics={len(self.rows['lyrics'])}")

    def compare(self, host: Host, label: str, settings: bool = True) -> None:
        library = tree(self.lane.base / "library")
        added = sorted(set(library) - set(self.library))
        removed = sorted(set(self.library) - set(library))
        changed = sorted(k for k in set(library) & set(self.library) if library[k] != self.library[k])
        check(not added and not removed and not changed, f"{label}: library files and folders unchanged (added={added[:5]} removed={removed[:5]} changed={changed[:5]})")
        check(library_rows(self.lane, label) == self.rows, f"{label}: TrackFile, Track link, LyricFile and monitored-edition rows unchanged")
        check(tree(self.lane.base / "downloads") == self.downloads, f"{label}: download sources unchanged and retained")
        if settings:
            now = stock_settings(host)
            diff = [k for k in now if now[k] != self.settings[k]]
            check(not diff, f"{label}: stock settings unchanged (differs: {diff})")
        check(not (self.lane.base / "crescendarr").exists(), f"{label}: no writer state folder anywhere in the disposable root")


def connect(host: Host, mode: int, clients: list[int], allowed: str, cycle: int = 1) -> dict:
    existing = [n for n in host.api("GET", "notification") if n.get("implementation") == "CrescendarrAuditConnect"]
    resource = existing[0] if existing else [s for s in host.api("GET", "notification/schema") if s.get("implementation") == "CrescendarrAuditConnect"][0]
    values = {"mode": mode, "observedClients": clients, "allowedDownloadFolders": allowed, "cycleMinutes": cycle}
    for f in resource["fields"]:
        if f["name"] in values:
            f["value"] = values[f["name"]]
    resource.update({"name": "Crescendarr audit", "onHealthIssue": True, "tags": resource.get("tags") or []})
    saved = host.api("PUT", f"notification/{resource['id']}?forceSave=true", resource) if existing else host.api("POST", "notification?forceSave=true", resource)
    note(f"CONNECT mode={mode} clients={clients} allowed={allowed!r} id={saved.get('id')}")
    return saved


def summaries(host: Host, start: int = 0) -> list[str]:
    return re.findall(r"CRESCENDARR AUDIT_CYCLE_SUMMARY [^\n]*", host.text()[start:])


def run_cycle(host: Host) -> str:
    """A manual audit cycle through the stock command API (the audit build's only command)."""
    start = len(host.text())
    result = host.command({"name": "CrescendarrAuditCycle", "reason": "test"})
    check(status(result) == "completed", "the audit cycle command completed")
    text = host.text()[start:]
    found = re.findall(r"CRESCENDARR AUDIT_CYCLE_(?:SUMMARY|SKIPPED) [^\n]*", text)
    check(found, "the audit cycle logged a summary")
    note("CYCLE " + found[-1])
    return found[-1]


def report(lane: Lane) -> dict:
    data = json.loads((state_dir(lane) / "report.json").read_text())
    by_folder = {d["folder"]: d for d in data["downloads"]}
    note("REPORT " + json.dumps({k: {"state": v["state"], "reason": v.get("reason"), "tracks": [(t["Track"], t["Action"], t["Current"], t["Incoming"]) for t in v["tracks"]]}
                                  for k, v in by_folder.items()})[:3000])
    return by_folder


def health(host: Host) -> list[dict]:
    host.command({"name": "CheckHealth"})
    items = [h for h in host.api("GET", "health") if "Crescendarr" in (h.get("message") or "") or "Crescendarr" in (h.get("source") or "")]
    note(f"HEALTH {items}")
    return items


def actions(entry: dict) -> dict[int, str]:
    return {t["Track"]: t["Action"] for t in entry["tracks"]}


def inject(host: Host, lane: Lane) -> None:
    """Writer commands through the stock API: none may exist or run in this build."""
    for name in WRITER_COMMANDS:
        body = {"name": name, "downloadId": "x", "planId": "0" * 64, "operationId": "00000000-0000-4000-8000-000000000000", "action": "resolve", "token": "x"}
        try:
            result = host.command(body, deadline=60)
            outcome = status(result)
        except Failure as refused:
            outcome = "refused: " + str(refused)[:160]
        note(f"INJECTED {name} -> {outcome}")
        check(outcome != "completed", f"injected writer command {name} did not run")
    names = {c[1] for c in lane.state("after-injection")["commands"]}
    check(not names & set(WRITER_COMMANDS), f"no writer command was ever recorded by stock ({sorted(names & set(WRITER_COMMANDS))})")


def set_connect_settings_in_db(lane: Lane, mutate) -> None:
    connection = sqlite3.connect(lane.data / "lidarr.db", timeout=30)
    try:
        rows = connection.execute('SELECT "Id","Settings" FROM "Notifications" WHERE "Implementation" = ?', ("CrescendarrAuditConnect",)).fetchall()
        check(len(rows) == 1, "exactly one Crescendarr Connect row")
        settings = json.loads(rows[0][1])
        mutate(settings)
        connection.execute('UPDATE "Notifications" SET "Settings" = ? WHERE "Id" = ?', (json.dumps(settings), rows[0][0]))
        connection.commit()
        note(f"DB_INJECTED_SETTINGS {settings}")
    finally:
        connection.close()


# ------------------------------------------------------------------ lanes

def lane_readonly(lane: Lane) -> None:
    host = boot(lane)
    try:
        check("CrescendarrAuditConnect" in {s.get("implementation") for s in host.api("GET", "notification/schema")}, "the audit Connect provider is offered")
        check("CrescendarrConnect" not in {s.get("implementation") for s in host.api("GET", "notification/schema")}, "the writer Connect provider does not exist")
        ready = re.search(r"CRESCENDARR AUDIT_BUILD_READY [^\n]*", host.text()).group(0)
        note(ready)
        check("libraryWrites=impossible" in ready, "the build announces it cannot write the library")
        client = host.api("GET", "downloadclient")[0]
        client["name"] = NORMAL_CLIENT
        host.api("PUT", f"downloadclient/{client['id']}?forceSave=true", client)
        note(f"CLIENT renamed to an ordinary name: {NORMAL_CLIENT!r} id={client['id']}")
        watch = str(lane.watch)
        base = Baseline(lane, host, "before")

        # 1. Installed and even configured with defaults: nothing is observed.
        connect(host, 0, [], "")
        for name in (GOOD, WRONG):
            shutil.move(str(lane.base / "prepared" / name), str(lane.watch / name))
        base = Baseline(lane, host, "with-downloads")
        mark = len(host.text())
        time.sleep(75)
        check("AUDIT_CYCLE_START" not in host.text()[mark:], "default Disabled: no audit cycle ran for 75 s")
        check(not (state_dir(lane) / "report.json").exists(), "default Disabled: nothing observed or recorded")

        # 2. Explicit opt-in: Audit only, one selected client, one allowed folder.
        connect(host, 1, [client["id"]], watch)
        deadline = time.monotonic() + 240
        entries = {}
        while time.monotonic() < deadline:
            run_cycle(host)
            entries = report(lane)
            if entries.get(GOOD, {}).get("state") == "WouldImport" and WRONG in entries:
                break
            time.sleep(10)
        good, wrong = entries.get(GOOD), entries.get(WRONG)
        check(good is not None and good["state"] == "WouldImport", "the good download is previewed as a would-be library change")
        check(actions(good) == {1: "Upgrade", 2: "Keep", 3: "Add"}, f"per-track preview: upgrade track 1, KEEP the superior FLAC track 2, add missing track 3 ({actions(good)})")
        track2 = [t for t in good["tracks"] if t["Track"] == 2][0]
        check("FLAC" in (track2["Current"] or ""), f"the kept track shows its better current quality ({track2['Current']})")
        check(wrong is not None and wrong["state"] in ("NeedsReview", "Blocked") and wrong.get("reason"), "the wrong edition says why it needs review")
        markdown = (state_dir(lane) / "report.md").read_text()
        check("AUDIT ONLY" in markdown and "| 2 |" in markdown and "Keep" in markdown, "the readable report lists the tracks and says AUDIT ONLY")
        redacted = (state_dir(lane) / "diagnostics-redacted.json").read_text()
        check("Synthetic" not in redacted and str(lane.base) not in redacted, "the shareable diagnostics contain no names or paths")
        warnings = health(host)
        check(any("AUDIT ONLY" in (h.get("message") or "") and "WOULD change" in (h.get("message") or "") for h in warnings), "System > Status shows the would-be change in a warning")
        base.compare(host, "after-first-audit", settings=False)

        # 3. Writer commands injected through the stock API.
        inject(host, lane)
        base.compare(host, "after-injection", settings=False)

        # 4. Settings edited outside the UI to GuardedAuto (value 2) directly in the database, then restart.
        host.stop()
        set_connect_settings_in_db(lane, lambda s: s.update({"mode": 2}))
        host = boot(lane)
        skipped = run_cycle(host)
        check("AUDIT_CYCLE_SKIPPED" in skipped and "invalid" in skipped, "an injected GuardedAuto mode is invalid in this build: nothing observed")
        check(any(str(h.get("type")).lower() == "error" and "not observing" in (h.get("message") or "") for h in health(host)), "health: the invalid configuration is an error")
        inject(host, lane)
        base.compare(host, "after-guardedauto-injection", settings=False)
        host.stop()
        set_connect_settings_in_db(lane, lambda s: s.update({"mode": 1}))
        host = boot(lane)

        # 5. Restart and a quality-profile change: the preview updates, the library does not.
        run_cycle(host)
        check(report(lane)[GOOD]["state"] == "WouldImport", "after restart the preview is unchanged")
        original = host.api("GET", f"qualityprofile/{host.api('GET', 'artist')[0]['qualityProfileId']}")
        profile = json.loads(json.dumps(original))
        items = profile["items"]
        index = next(i for i, it in enumerate(items) if (it.get("quality") or {}).get("id") == MP3_256 or any((s.get("quality") or {}).get("id") == MP3_256 for s in it.get("items") or []))
        item = items.pop(index)
        item["allowed"] = True
        items.append(item)
        profile["cutoff"] = item["quality"]["id"] if item.get("quality") else item["id"]
        host.api("PUT", f"qualityprofile/{profile['id']}", profile)
        run_cycle(host)
        after_profile = report(lane)[GOOD]
        note(f"PROFILE_PREVIEW {actions(after_profile)}")
        check(actions(after_profile).get(2) != "Keep" or after_profile["state"] != "WouldImport", "the preview follows the changed profile (track 2 no longer kept)")
        host.api("PUT", f"qualityprofile/{original['id']}", original)
        run_cycle(host)
        check(actions(report(lane)[GOOD]) == {1: "Upgrade", 2: "Keep", 3: "Add"}, "restoring the profile restores the preview")
        base.compare(host, "after-profile-change", settings=False)

        # 6. Disallowed path (simulates a wrong remote path mapping): refused and visible.
        connect(host, 1, [client["id"]], str(lane.base / "downloads" / "nzb"))
        run_cycle(host)
        check(report(lane)[GOOD]["state"] == "RefusedUnsafePath", "a completed folder outside the allowed folders is refused, visibly")
        connect(host, 1, [client["id"]], watch)

        # 7. Unreadable artist folder (no permission; the same signal as an unmounted or stale share).
        artist = lane.artist_path
        mode = artist.stat().st_mode
        os.chmod(artist, 0)
        try:
            run_cycle(host)
            entry = report(lane)[GOOD]
            check(entry["state"] == "LibraryUnavailable" and not any(t["Action"] == "Add" for t in entry["tracks"]),
                  "an unreadable library is never previewed as missing tracks to add")
        finally:
            os.chmod(artist, stat.S_IMODE(mode))
        run_cycle(host)

        # 8. The selected client disabled (downloader outage as Lidarr sees it): visible, nothing changes.
        client = host.api("GET", f"downloadclient/{client['id']}")
        client["enable"] = False
        host.api("PUT", f"downloadclient/{client['id']}?forceSave=true", client)
        run_cycle(host)
        status_file = json.loads((state_dir(lane) / "status.json").read_text())
        check(status_file["clientProblems"], f"a disabled selected client is reported ({status_file['clientProblems']})")
        client["enable"] = True
        host.api("PUT", f"downloadclient/{client['id']}?forceSave=true", client)

        # 9. An UNSELECTED client with its own completed download is never read.
        other_watch = lane.base / "downloads" / "other-watch"
        other_watch.mkdir()
        shutil.copytree(lane.watch / GOOD, other_watch / "Unselected Completed Album")
        other = json.loads(json.dumps(host.api("GET", f"downloadclient/{client['id']}")))
        other.pop("id")
        other["name"] = "Other Blackhole"
        for f in other["fields"]:
            if f["name"].lower() == "watchfolder":
                f["value"] = str(other_watch)
        other = host.api("POST", "downloadclient?forceSave=true", other)
        base = Baseline(lane, host, "with-unselected-client")
        run_cycle(host)
        time.sleep(35)
        run_cycle(host)
        check("Unselected Completed Album" not in report(lane), "a completed download of an unselected client is never observed")

        # 10. Final: restart once more, then the complete BEFORE/AFTER comparison including stock settings.
        host.stop()
        host = boot(lane)
        run_cycle(host)
        base.compare(host, "final")
        written = sorted(str(p.relative_to(lane.data)) for p in state_dir(lane).rglob("*") if p.is_file())
        note(f"AUDIT_STATE_FILES {written}")
        check(written and all(w.startswith("crescendarr-audit/") for w in written), "every audit file lives in Lidarr's AppData crescendarr-audit folder")
        check(not any(lane.data.glob("plugins/**/*.probe")) and not any((lane.base / "library").rglob("*probe*")), "no probe file was ever written")
    finally:
        host.stop()


def lane_lifecycle(lane: Lane, packages: Path) -> None:
    zips = sorted(packages.glob("crescendarr-audit-beta-*.zip"))
    check(len(zips) == 2, f"two candidate packages to install and upgrade ({[z.name for z in zips]})")
    first, second = zips
    plugin_dir = lane.data / "plugins" / "GQuX" / "Crescendarr"
    shutil.rmtree(plugin_dir)

    host = boot(lane, marker="")  # stock only
    try:
        stock_before = stock_settings(host)
        library_before = tree(lane.base / "library")
        rows_before = library_rows(lane, "stock-only")
    finally:
        host.stop()

    def install(package: Path) -> str:
        if plugin_dir.exists():
            shutil.rmtree(plugin_dir)
        plugin_dir.mkdir(parents=True)
        with zipfile.ZipFile(package) as archive:
            names = archive.namelist()
            check("Lidarr.Plugin.Crescendarr.dll" in names, f"{package.name} contains the plugin DLL")
            archive.extract("Lidarr.Plugin.Crescendarr.dll", plugin_dir)
            sums = archive.read("SHA256SUMS").decode()
        dll_sha = sha(plugin_dir / "Lidarr.Plugin.Crescendarr.dll")
        check(dll_sha in sums, f"{package.name}: the installed DLL matches the package's SHA256SUMS")
        note(f"INSTALLED {package.name} dll_sha256={dll_sha}")
        return dll_sha

    def version(host: Host) -> str:
        return re.search(r"AUDIT_BUILD_READY version=(\S+)", host.text()).group(1)

    install(first)
    host = boot(lane)
    try:
        v1 = version(host)
        client = host.api("GET", "downloadclient")[0]
        connect(host, 1, [client["id"]], str(lane.watch))
        shutil.move(str(lane.base / "prepared" / GOOD), str(lane.watch / GOOD))
        time.sleep(35)
        run_cycle(host)
        check(report(lane)[GOOD]["state"] == "WouldImport", f"{v1}: previews the download")
        records = sorted(p.name for p in (state_dir(lane) / "downloads").glob("*.json"))
    finally:
        host.stop()

    install(second)
    host = boot(lane)
    try:
        v2 = version(host)
        check(v2 != v1, f"upgraded build identity is visible ({v1} -> {v2})")
        check(any(n["implementation"] == "CrescendarrAuditConnect" and next(f["value"] for f in n["fields"] if f["name"] == "mode") == 1
                  for n in host.api("GET", "notification")), "upgrade: settings preserved")
        run_cycle(host)
        check(sorted(p.name for p in (state_dir(lane) / "downloads").glob("*.json")) == records, "upgrade: registry state reused, not duplicated")
    finally:
        host.stop()

    install(first)
    host = boot(lane)
    try:
        check(version(host) == v1, "rollback: the previous build runs again")
        run_cycle(host)
        check(report(lane)[GOOD]["state"] == "WouldImport", "rollback: the preview still works on the newer state")
        connect_id = [n for n in host.api("GET", "notification") if n["implementation"] == "CrescendarrAuditConnect"][0]["id"]
        host.api("DELETE", f"notification/{connect_id}")
        note("UNINSTALL step 1: Connect definition removed through the stock API")
    finally:
        host.stop()

    shutil.rmtree(plugin_dir)
    note("UNINSTALL step 2: plugin folder removed")
    host = boot(lane, marker="")
    try:
        check("CRESCENDARR" not in host.text(), "uninstalled: no Crescendarr code runs")
        check("CrescendarrAuditConnect" not in {s.get("implementation") for s in host.api("GET", "notification/schema")}, "uninstalled: provider gone")
        after = stock_settings(host)
        changed = [k for k in after if k != "downloadclients" and after[k] != stock_before[k]]
        check(not changed, f"uninstalled: stock settings equal to before install ({changed})")
        check(tree(lane.base / "library") == library_before and library_rows(lane, "uninstalled") == rows_before, "library unchanged through install, upgrade, rollback and uninstall")
        note(f"LEFTOVER_STATE {state_dir(lane)} exists={state_dir(lane).exists()} (documented: delete it to remove all audit data)")
    finally:
        host.stop()


def run(bin_dir: Path, artifact: Path, seeder: Path, name: str, base: Path) -> None:
    if name == "package-lifecycle":
        dll = base.parent / "lifecycle-seed-plugin.dll"
        with zipfile.ZipFile(sorted(artifact.glob("crescendarr-audit-beta-*.zip"))[0]) as archive:
            dll.write_bytes(archive.read("Lidarr.Plugin.Crescendarr.dll"))
        lane = prepare(bin_dir, dll, seeder, base, "three")
        lane_lifecycle(lane, artifact)
    else:
        lane = prepare(bin_dir, artifact, seeder, base, "three")
        lane_readonly(lane)
    note(f"M25_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required (the harness, not the audit build, needs it).")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, artifact, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m25-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, artifact, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M25_FAILED: {failure}", file=sys.stderr)
        note(f"M25_FAILED: {failure}")
        return 1
    finally:
        for log in sorted(base.glob("boot*.log"), key=lambda p: int(re.sub(r"\D", "", p.name) or 0)):
            print(f"=== {log.name} (Crescendarr and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]")):
                    print(line[:2000])
        (runner / f"m25-{sys.argv[4]}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M25 {sys.argv[4]}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
