#!/usr/bin/env python3
"""M17 (issue #46): real stock-Lidarr selective-import vertical slice in a DISPOSABLE CI daemon.

CI-only, Linux. Official SHA-256-pinned Lidarr v3.1.6.5079, fresh data folder under RUNNER_TEMP, synthetic
tagged audio only, stock built-in Usenet Blackhole client with Completed Download Handling OFF.

  boot 0  test-harness fixture seeder only (separate assembly): catalog rows + owned library + candidate folders
  boot 1  ONLY the Crescendarr plugin: wrong-edition refusal, real observation -> plan -> guarded native import,
          before/after files+hashes+SQLite rows, stock rescan, repeat dispatch, (crash lane: SIGKILL mid-import)
  boot 2  crash lane: restart, stock rescan BEFORE recovery, recovery command, final verification
  boot 3  plugin installed without opt-in must be inert

Usage: run_disposable_m17.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <three|twelve|three-crash>
"""
from __future__ import annotations

import hashlib
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
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

PORT = 18989
STATUS = {0: "queued", 1: "started", 2: "completed", 3: "failed", 4: "aborted", 5: "cancelled", 6: "orphaned"}
EVIDENCE: list[str] = []
CLIENT = "Crescendarr Synthetic Blackhole"


class Failure(Exception):
    pass


def note(line: str) -> None:
    EVIDENCE.append(line)
    print(line, flush=True)


def check(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)
    note(f"CHECK_OK {message}")


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Host:
    def __init__(self, bin_dir: Path, data: Path, env: dict[str, str], log: Path):
        self.bin_dir, self.data, self.env, self.log = bin_dir, data, env, log
        self.process: subprocess.Popen | None = None
        self.key = ""

    def start(self, marker: str) -> None:
        handle = self.log.open("w", encoding="utf-8")
        self.process = subprocess.Popen([str(self.bin_dir / "Lidarr"), f"-data={self.data}", "-nobrowser", "-exitimmediately"],
                                        cwd=self.bin_dir, stdout=handle, stderr=subprocess.STDOUT, env=self.env, start_new_session=True)
        limit = time.monotonic() + 150
        while time.monotonic() < limit:
            if self.process.poll() is not None:
                raise Failure(f"host exited early rc={self.process.returncode}")
            if marker in self.text() and self.ping():
                match = re.search(r"<ApiKey>([0-9a-f]+)</ApiKey>", (self.data / "config.xml").read_text())
                if match:
                    self.key = match.group(1)
                    note(f"HOST_READY log={self.log.name} marker={marker!r}")
                    return
            time.sleep(0.5)
        raise Failure(f"host not ready ({marker!r})")

    def text(self) -> str:
        return self.log.read_text(encoding="utf-8", errors="replace") if self.log.exists() else ""

    def ping(self) -> bool:
        try:
            with urlopen(f"http://127.0.0.1:{PORT}/ping", timeout=1.5) as response:
                return response.status == 200
        except (HTTPError, URLError, TimeoutError, ConnectionError):
            return False

    def api(self, method: str, path: str, body=None):
        data = None if body is None else json.dumps(body).encode()
        request = Request(f"http://127.0.0.1:{PORT}/api/v1/{path}", data=data, method=method,
                          headers={"X-Api-Key": self.key, "Content-Type": "application/json"})
        try:
            with urlopen(request, timeout=30) as response:
                raw = response.read()
                return json.loads(raw) if raw.strip() else None
        except HTTPError as error:
            raise Failure(f"{method} {path} -> HTTP {error.code}: {error.read()[:600]!r}") from error

    def command(self, body: dict, wanted=("completed", "failed"), deadline: float = 240) -> dict:
        created = self.api("POST", "command", body)
        limit = time.monotonic() + deadline
        while time.monotonic() < limit:
            current = self.api("GET", f"command/{created['id']}")
            state = status(current)
            if state in wanted:
                note(f"COMMAND {body['name']} id={created['id']} status={state} message={current.get('message')!r}")
                return current
            time.sleep(0.3)
        raise Failure(f"command {body['name']} id={created['id']} did not reach {wanted}")

    def push(self, body: dict) -> dict:
        created = self.api("POST", "command", body)
        note(f"PUSHED {body['name']} id={created['id']}")
        return created

    def wait_log(self, pattern: str, deadline: float = 120, start: int = 0) -> re.Match:
        limit = time.monotonic() + deadline
        while time.monotonic() < limit:
            match = re.compile(pattern).search(self.text(), start)
            if match:
                return match
            time.sleep(0.3)
        raise Failure(f"log pattern {pattern!r} not seen")

    def stop(self) -> None:
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=10)
        note(f"HOST_STOPPED log={self.log.name} rc={self.process.returncode if self.process else None}")

    def kill(self) -> None:
        os.killpg(self.process.pid, signal.SIGKILL)
        self.process.wait(timeout=15)
        note(f"HOST_SIGKILL log={self.log.name} rc={self.process.returncode}")


def status(resource: dict) -> str:
    value = resource.get("status")
    return STATUS.get(value, str(value).lower()) if isinstance(value, int) else str(value).lower()


# ------------------------------------------------------------------ evidence

def db_state(data: Path, label: str, base: Path) -> dict:
    """Copy lidarr.db (+WAL) and read the relevant rows from the COPY only."""
    target = base / "db" / label
    target.mkdir(parents=True, exist_ok=True)
    for suffix in ("", "-wal"):
        if (data / f"lidarr.db{suffix}").exists():
            shutil.copyfile(data / f"lidarr.db{suffix}", target / f"lidarr.db{suffix}")
    connection = sqlite3.connect(target / "lidarr.db")
    try:
        q = lambda sql: connection.execute(sql).fetchall()
        state = {
            "tracks": q('SELECT "Id","AbsoluteTrackNumber","AlbumReleaseId","TrackFileId","ForeignRecordingId" FROM "Tracks" ORDER BY "Id"'),
            "files": q('SELECT "Id","Path","Quality","AlbumId","Size" FROM "TrackFiles" ORDER BY "Id"'),
            "lyrics": q('SELECT "Id","TrackFileId","RelativePath","AlbumId" FROM "LyricFiles" ORDER BY "Id"'),
            "releases": q('SELECT "Id","ForeignReleaseId","Monitored" FROM "AlbumReleases" ORDER BY "Id"'),
            "commands": q('SELECT "Id","Name","Status","StartedAt","EndedAt" FROM "Commands" ORDER BY "Id"'),
        }
    finally:
        connection.close()
    return state


def files_state(root: Path) -> dict[str, str]:
    return {str(p.relative_to(root)): sha(p) for p in sorted(root.rglob("*")) if p.is_file()}


def library_view(state: dict, seed: dict, release_id: int) -> dict[int, dict]:
    files = {row[0]: row for row in state["files"]}
    lyrics = state["lyrics"]
    view = {}
    for row in state["tracks"]:
        if row[2] != release_id:
            continue
        file = files.get(row[3])
        view[row[1]] = {
            "trackId": row[0], "trackFileId": row[3], "path": file[1] if file else None,
            "quality": json.loads(file[2])["quality"] if file else None,
            "sha": sha(Path(file[1])) if file and Path(file[1]).exists() else None,
            "lyrics": [(l[0], l[2]) for l in lyrics if l[1] == row[3] and row[3] > 0],
        }
    return view


def print_view(label: str, view: dict[int, dict]) -> None:
    for n in sorted(view):
        v = view[n]
        note(f"ALBUM {label} track={n} trackFileId={v['trackFileId']} quality={v['quality']} file={Path(v['path']).name if v['path'] else None} "
             f"sha={(v['sha'] or '')[:12]} lyrics={v['lyrics']}")


# ------------------------------------------------------------------ setup

def environment(base: Path, home: Path, crescendarr: bool, seeder: bool) -> dict[str, str]:
    env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(home), "XDG_CONFIG_HOME": str(home), "LANG": "C.UTF-8",
           "TMPDIR": str(base), "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", ""), "CI": "true", "GITHUB_ACTIONS": "true",
           "Lidarr__Server__BindAddress": "127.0.0.1", "Lidarr__Server__Port": str(PORT), "CRESCENDARR_M17_ROOT": str(base)}
    if crescendarr:
        env["CRESCENDARR_M17_OPT_IN"] = "DISPOSABLE_SYNTHETIC_ONLY"
    if seeder:
        env["CRESCENDARR_M17_SEEDER_OPT_IN"] = "DISPOSABLE_SYNTHETIC_ONLY"
    return env


def install(dll: Path, data: Path, name: str) -> Path:
    folder = data / "plugins" / "GQuX" / name
    folder.mkdir(parents=True)
    shutil.copyfile(dll, folder / dll.name)
    return folder


def configure_and_seed(host: Host, base: Path, scenario: str) -> dict:
    settings = host.api("GET", "config/downloadclient")
    settings["enableCompletedDownloadHandling"] = False
    host.api("PUT", f"config/downloadclient/{settings['id']}", settings)
    check(host.api("GET", "config/downloadclient")["enableCompletedDownloadHandling"] is False, "stock CDH is OFF")
    media = host.api("GET", "config/mediamanagement")
    media["recycleBin"] = str(base / "recycle")
    host.api("PUT", f"config/mediamanagement/{media['id']}", media)
    quality = host.api("GET", "qualityprofile")[0]["id"]
    metadata = host.api("GET", "metadataprofile")[0]["id"]
    host.api("POST", "rootfolder", {"name": "synthetic", "path": str(base / "library"), "defaultQualityProfileId": quality,
                                    "defaultMetadataProfileId": metadata, "defaultMonitorOption": "none",
                                    "defaultNewItemMonitorOption": "none", "defaultTags": []})
    schema = [p for p in host.api("GET", "downloadclient/schema") if p.get("implementation") == "UsenetBlackhole"][0]
    schema.update({"name": CLIENT, "enable": True, "priority": 1, "removeCompletedDownloads": False, "removeFailedDownloads": False})
    for field in schema["fields"]:
        if field["name"].lower() == "nzbfolder":
            field["value"] = str(base / "downloads" / "nzb")
        elif field["name"].lower() == "watchfolder":
            field["value"] = str(base / "downloads" / "watch")
    host.api("POST", "downloadclient?forceSave=true", schema)
    seeded = host.command({"name": "CrescendarrSeedFixture", "scenario": scenario})
    check(status(seeded) == "completed", "fixture seeded through stock repositories in the setup boot")
    return json.loads((base / "seed.json").read_text())


def plan_until_completed(host: Host, folder_name: str) -> str:
    """Stock blackhole reports a folder Completed only after its 30 s grace period from first observation."""
    limit = time.monotonic() + 150
    while time.monotonic() < limit:
        start = len(host.text())
        host.command({"name": "CrescendarrPlan"})
        text = host.text()[start:]
        for line in text.splitlines():
            if line.startswith("CRESCENDARR PLAN ") and folder_name in line:
                note("PLAN_LINE " + line[:1500])
                return line
        time.sleep(5)
    raise Failure(f"no completed plan for {folder_name}")


def field(line: str, name: str) -> str:
    match = re.search(name + r'=("([^"]*)"|\S+)', line)
    if not match:
        raise Failure(f"{name} missing in {line[:300]}")
    return match.group(2) if match.group(2) is not None else match.group(1)


# ------------------------------------------------------------------ scenario

def run(bin_dir: Path, plugin: Path, seeder: Path, scenario: str, base: Path) -> None:
    fixture = "twelve" if scenario == "twelve" else "three"
    total = 12 if fixture == "twelve" else 3
    data, home = base / "config", base / "home"
    for d in (data, home, base / "library", base / "downloads" / "nzb", base / "downloads" / "watch", base / "recycle", base / "faults"):
        d.mkdir(parents=True)
    (base / ".crescendarr-disposable-host").write_text("CRESCENDARR_DISPOSABLE_HOST_V1\n", encoding="utf-8")

    # boot 0: fixture seeder only (test harness), never together with Crescendarr.
    seeder_dir = install(seeder, data, "CrescendarrFixtureSeeder")
    boot0 = Host(bin_dir, data, environment(base, home, crescendarr=False, seeder=True), base / "boot0.log")
    boot0.start("")
    try:
        seed = configure_and_seed(boot0, base, fixture)
    finally:
        boot0.stop()
    shutil.rmtree(seeder_dir)
    install(plugin, data, "Crescendarr")
    release_id, alternate_id = seed["releaseId"], seed["alternateReleaseId"]
    artist_path = Path(seed["artistPath"])
    note(f"SEED {json.dumps(seed)[:1500]}")

    boot1 = Host(bin_dir, data, environment(base, home, crescendarr=True, seeder=False), base / "boot1.log")
    boot1.start("CRESCENDARR READY")
    note("BOOT1 " + boot1.wait_log(r"CRESCENDARR READY[^\n]*").group(0))
    check(not (data / "plugins" / "GQuX" / "CrescendarrFixtureSeeder").exists(), "only the Crescendarr plugin is installed for the operation boots")
    watch = base / "downloads" / "watch"
    try:
        before = db_state(data, "before", base)
        before_view = library_view(before, seed, release_id)
        print_view("BEFORE", before_view)
        library_before = files_state(artist_path)

        # Conflicting edition: tagged with the album's OTHER (unmonitored) release. Must refuse with zero mutations.
        wrong_name = "Crescendarr Synthetic Album [wrong edition]"
        shutil.move(str(base / "prepared" / wrong_name), str(watch / wrong_name))
        wrong = plan_until_completed(boot1, wrong_name)
        check(field(wrong, "disposition") in ("NeedsReview", "Blocked"), f"wrong-edition download is not importable (disposition={field(wrong, 'disposition')})")
        check("EDITION_MISMATCH" in wrong or "Album release not requested" in wrong, "wrong-edition reason comes from edition identity")
        boot1.command({"name": "CrescendarrImport", "downloadId": field(wrong, "download"), "planId": field(wrong, "planId") or "none"})
        boot1.wait_log(r"IMPORT_REFUSED[^\n]*wrong edition")
        check(library_view(db_state(data, "after-wrong-edition", base), seed, release_id) == before_view, "wrong edition: no DB link/row/lyric change")
        check(files_state(artist_path) == library_before, "wrong edition: no library byte change")
        shutil.move(str(watch / wrong_name), str(base / "prepared" / wrong_name))

        # Golden path: real observation -> plan -> import.
        good_name = "Crescendarr Synthetic Album [good]"
        shutil.move(str(base / "prepared" / good_name), str(watch / good_name))
        incoming = {p.name: sha(p) for p in (watch / good_name).iterdir()}
        good = plan_until_completed(boot1, good_name)
        check(field(good, "disposition") == "Ready", "real observed download produced a Ready plan")
        expected_upgrades = [1] if fixture == "three" else list(range(1, 12))
        superior = 2 if fixture == "three" else 12
        for n in expected_upgrades:
            check(re.search(rf"track={n}:Upgrade:STRICT_HOST_UPGRADE", good) is not None, f"plan upgrades track {n}")
        check(re.search(rf"track={superior}:KeepExisting:EXISTING_EQUAL_OR_PREFERRED", good) is not None, f"plan keeps superior FLAC track {superior}")
        if fixture == "three":
            check(re.search(r"track=3:AddMissing:VERIFIED_MISSING_RECORDING", good) is not None, "plan adds missing track 3")

        crash = scenario == "three-crash"
        if crash:
            (base / "faults" / "pause-after-native-1").write_text("armed\n")
            boot1.push({"name": "CrescendarrImport", "downloadId": field(good, "download"), "planId": field(good, "planId")})
            boot1.wait_log(r"FAULT_PAUSED after-native-1", deadline=180)
            paused = db_state(data, "at-sigkill", base)
            print_view("AT_SIGKILL", library_view(paused, seed, release_id))
            boot1.kill()
            boot1 = None
        else:
            result = boot1.command({"name": "CrescendarrImport", "downloadId": field(good, "download"), "planId": field(good, "planId")})
            check(status(result) == "completed", "import command completed on the stock executor")
            note("OUTCOME " + boot1.wait_log(r"IMPORT_OUTCOME[^\n]*").group(0))
            check("outcome=Completed" in boot1.text(), "operation outcome Completed")
    finally:
        if boot1 is not None:
            boot1.stop()

    if scenario == "three-crash":
        db_state(data, "after-sigkill", base)
        boot2 = Host(bin_dir, data, environment(base, home, crescendarr=True, seeder=False), base / "boot2.log")
        boot2.start("CRESCENDARR READY")
        try:
            restarted = db_state(data, "after-restart", base)
            imports = [c for c in restarted["commands"] if c[1] == "CrescendarrImport"]
            note(f"RESTART_COMMANDS {imports}")
            check(any(STATUS.get(c[2]) == "orphaned" for c in imports), "the interrupted plugin import command is Orphaned by stock, never resumed")
            check("NATIVE_IMPORT" not in boot2.text(), "no plugin import resumed from the stock Commands table")
            scan = boot2.command({"name": "RescanFolders"})
            check(status(scan) == "completed", "a stock rescan ran BEFORE recovery")
            print_view("AFTER_SCAN_BEFORE_RECOVERY", library_view(db_state(data, "scan-before-recovery", base), seed, release_id))
            recovered = boot2.command({"name": "CrescendarrRecover"})
            note("RECOVERY " + boot2.wait_log(r"IMPORT_OUTCOME[^\n]*").group(0))
            check(status(recovered) == "completed" and "outcome=Completed" in boot2.text(), "recovery command completed the journaled operation")
            check(re.search(r"JOURNAL gen=\d+ verdict=Proceed next=RecordStepDone:\d+:Automatic \"native upgrade observed complete\"", boot2.text()) is not None,
                  "M13 reconciler recognised the completed native step from fresh observation")
            verify_final(boot2, data, base, seed, release_id, alternate_id, before_view, incoming, artist_path, watch / good_name, [1], 2, fixture)
        finally:
            boot2.stop()
    else:
        boot2 = Host(bin_dir, data, environment(base, home, crescendarr=True, seeder=False), base / "boot2.log")
        boot2.start("CRESCENDARR READY")
        try:
            verify_final(boot2, data, base, seed, release_id, alternate_id, before_view, incoming, artist_path, watch / good_name,
                         expected_upgrades, superior, fixture)
        finally:
            boot2.stop()

    # Guard negative: same data, plugin installed, no opt-in -> inert.
    boot3 = Host(bin_dir, data, environment(base, home, crescendarr=False, seeder=False), base / "boot3.log")
    boot3.start("CRESCENDARR INERT")
    try:
        frozen = db_state(data, "guard-before", base)
        refused = boot3.command({"name": "CrescendarrPlan"})
        check(status(refused) == "failed", "without opt-in the installed plugin refuses to run")
        check(db_state(data, "guard-after", base)["files"] == frozen["files"], "inert plugin changed nothing")
    finally:
        boot3.stop()
    note(f"M17_SCENARIO_COMPLETE {scenario}")


def verify_final(host: Host, data: Path, base: Path, seed: dict, release_id: int, alternate_id: int, before: dict, incoming: dict,
                 artist_path: Path, download: Path, upgrades: list[int], superior: int, fixture: str) -> None:
    after_state = db_state(data, "after", base)
    after = library_view(after_state, seed, release_id)
    print_view("AFTER", after)
    seeded = {t["n"]: t for t in seed["tracks"]}
    note(f"LIBRARY_FILES {sorted(p.name for p in artist_path.iterdir())}")
    for n in upgrades:
        name = f"{n:02d} - Synthetic Track {n}.mp3"
        check(after[n]["trackFileId"] not in (0, before[n]["trackFileId"]), f"track {n}: linked to a FRESH TrackFile row")
        check(after[n]["quality"] == 3 and after[n]["sha"] == incoming[name], f"track {n}: library holds the incoming MP3-256 bytes (stock quality id 3)")
        check(after[n]["lyrics"] == [(after[n]["lyrics"][0][0], Path(seeded[n]["lyricPath"]).name)] and len(after[n]["lyrics"]) == 1,
              f"track {n}: original lyric re-registered to the new TrackFile")
        check(sha(Path(seeded[n]["lyricPath"])) == seeded[n]["lyricSha"], f"track {n}: lyric bytes unchanged")
    check(after[superior] == before[superior], f"superior track {superior}: TrackFile row, quality, bytes and lyric row untouched")
    check(sha(Path(seeded[superior]["path"])) == seeded[superior]["sha"], f"superior track {superior}: FLAC bytes unchanged")
    if fixture == "three":
        name = "03 - Synthetic Track 3.mp3"
        check(after[3]["trackFileId"] > 0 and after[3]["sha"] == incoming[name] and Path(after[3]["path"]).parent == artist_path,
              "missing track 3 added from the incoming file")
    releases = {r[0]: r for r in after_state["releases"]}
    check(releases[release_id][2] == 1 and releases[alternate_id][2] == 0, "monitored MusicBrainz edition unchanged; alternate stays unmonitored")
    check({p.name: sha(p) for p in download.iterdir()} == incoming, "completed download folder left intact (no early source deletion)")
    recycled = sorted(p.name for p in (base / "recycle").rglob("*") if p.is_file())
    note(f"RECYCLE {recycled}")
    check(not any(x.endswith(".lrc") for x in recycled), "no lyric was recycled")

    # Subsequent stock rescan must be a no-op on the fully linked album.
    scan = host.command({"name": "RescanFolders"})
    check(status(scan) == "completed", "stock RescanFolders completed")
    rescanned = library_view(db_state(data, "after-rescan", base), seed, release_id)
    check(rescanned == after, "stock rescan changed no TrackFile, link or lyric row (no cascade)")

    # Repeat dispatch: the same approved operation must not import twice.
    start = len(host.text())
    host.command({"name": "CrescendarrPlan"})
    replan = [l for l in host.text()[start:].splitlines() if l.startswith("CRESCENDARR PLAN ") and download.name in l]
    check(replan and field(replan[-1], "disposition") == "NoImprovement", "re-observing the same download yields NoImprovement")
    old_plan = re.search(r"planId=(\w+)", [l for l in EVIDENCE if l.startswith("PLAN_LINE") and download.name in l][-1]).group(1)
    host.command({"name": "CrescendarrImport", "downloadId": field(replan[-1], "download"), "planId": old_plan})
    host.wait_log(r"IMPORT_REFUSED[^\n]*plan no longer current", start=start)
    check(library_view(db_state(data, "after-repeat", base), seed, release_id) == after, "repeat dispatch imported nothing")
    queue = host.api("GET", "queue?includeUnknownArtistItems=true")
    note(f"STOCK_QUEUE totalRecords={queue.get('totalRecords')} items={[(r.get('title'), r.get('status'), r.get('trackedDownloadState')) for r in queue.get('records', [])]}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in ("three", "twelve", "three-crash"):
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m17-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M17_FAILED: {failure}", file=sys.stderr)
        return 1
    finally:
        for log in sorted(base.glob("boot*.log")):
            print(f"=== {log.name} (Crescendarr, import, scan and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]", "ImportApprovedTracks", "UpgradeMediaFileService", "RecycleBin", "DiskScanService")):
                    print(line[:2000])
        (runner / f"m17-{sys.argv[4]}-evidence.txt").write_text("\n".join(EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M17 {sys.argv[4]}\n\n```\n" + "\n".join(EVIDENCE[-120:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
