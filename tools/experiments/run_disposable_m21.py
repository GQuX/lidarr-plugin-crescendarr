#!/usr/bin/env python3
"""M21 (issue #52): realistic compatibility lanes and disposable install procedure on the official SHA-256-pinned stock
Lidarr v3.1.6.5079 daemon with exactly ONE Crescendarr plugin.

Lanes
  sab-client          Lidarr's OWN SABnzbd client integration talks to a CI-only mock SABnzbd API (a test dependency
                      inside this harness process, never a runtime component). The plugin observes the item only through
                      Lidarr's client factory, waits while the job is still extracting, imports once Completed (guarded
                      auto), never asks the client to delete anything and keeps the source.
  realistic-rename    Real MusicBrainz identities from a captured CC0 fixture (2 discs x 14 tracks, CD and Digital Media
                      editions sharing recordings), renaming ON with stock multi-disc naming, extras in the album folder.
                      Other-edition and unknown-audio downloads must be refused with zero changes; the good one imports
                      27 upgrades/additions with the superior FLAC, every lyric and the cover untouched.
  realistic-repeated  Derived variant: one recording MBID on two release tracks. Must FAIL CLOSED (no import).
  install-cycle       Disposable install / restart / upgrade / uninstall of the packaged plugin: inert without opt-in,
                      no Lidarr setting changed, nothing written, clean removal.

Usage: run_disposable_m21.py <lidarr-bin> <crescendarr-dll-or-zip> <seeder-dll> <lane>
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import threading
import time
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
from run_disposable_m17 import CLIENT, Failure, Host, check, db_state, environment, field, files_state, install, library_view, note, plan_until_completed, print_view, sha, status  # noqa: E402
from run_disposable_m18 import GOOD, Lane, prepare, verify_consistent  # noqa: E402
from run_disposable_m20 import configure, registry, wait_summary  # noqa: E402

LANES = ("sab-client", "realistic-rename", "realistic-repeated", "install-cycle")
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "musicbrainz"
SAB_PORT = 18090


# ------------------------------------------------------------------ CI-only SABnzbd API mock (test dependency)

class SabMock:
    def __init__(self, storage: Path, title: str):
        self.storage, self.title = storage, title
        self.status = "Extracting"
        self.requests: list[dict] = []
        mock = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):  # noqa: D401 - silence
                pass

            def do_GET(self):  # noqa: N802
                query = {k: v[0] for k, v in parse_qs(urlparse(self.path).query).items()}
                mock.requests.append(query)
                body = mock.respond(query)
                data = json.dumps(body).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_POST = do_GET

        self.server = ThreadingHTTPServer(("127.0.0.1", SAB_PORT), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def respond(self, q: dict) -> dict:
        mode = q.get("mode")
        if mode == "version":
            return {"version": "4.3.3"}
        if mode == "get_config":
            return {"config": {"misc": {"complete_dir": str(self.storage.parent), "pre_check": False, "history_retention": "", "history_retention_option": "all",
                                        "enable_tv_sorting": False, "tv_categories": [], "enable_movie_sorting": False, "movie_categories": [],
                                        "enable_date_sorting": False, "date_categories": []},
                               "categories": [{"name": "music", "pp": "", "script": "Default", "dir": "", "priority": 0}], "sorters": []}}
        if mode == "fullstatus":
            return {"status": {"completedir": str(self.storage.parent)}}
        if mode == "queue":
            return {"queue": {"my_home": "", "paused": False, "slots": []}}
        if mode == "history":
            return {"history": {"paused": False, "slots": [{
                "nzo_id": "SABnzbd_nzo_crescendarr_m21", "name": self.title, "category": "music", "bytes": sum(p.stat().st_size for p in self.storage.iterdir()),
                "storage": str(self.storage), "status": self.status, "fail_message": "", "nzb_name": self.title + ".nzb", "download_time": 7}]}}
        return {"status": True}

    def destructive_requests(self) -> list[dict]:
        return [r for r in self.requests if r.get("name") in ("delete", "delete_files") or r.get("mode") in ("delete",) or "del_files" in r]

    def close(self) -> None:
        self.server.shutdown()


def lane_sab(lane: Lane) -> None:
    host = lane.boot()
    mock = None
    try:
        for client in host.api("GET", "downloadclient"):
            if client["name"] == CLIENT:
                host.api("DELETE", f"downloadclient/{client['id']}")
        shutil.move(str(lane.base / "prepared" / GOOD), str(lane.watch / GOOD))
        lane.incoming = {p.name: sha(p) for p in (lane.watch / GOOD).iterdir()}
        mock = SabMock(lane.watch / GOOD, GOOD)
        schema = [s for s in host.api("GET", "downloadclient/schema") if s.get("implementation") == "Sabnzbd"][0]
        values = {"host": "127.0.0.1", "port": SAB_PORT, "apiKey": "disposable-ci-mock", "musicCategory": "music", "useSsl": False, "urlBase": ""}
        for f in schema["fields"]:
            if f["name"] in values:
                f["value"] = values[f["name"]]
        schema.update({"name": "Crescendarr Synthetic SABnzbd", "enable": True, "priority": 1, "removeCompletedDownloads": False, "removeFailedDownloads": False})
        host.api("POST", "downloadclient?forceSave=true", schema)
        configure(host, "GuardedAuto")
        before = lane.view("before")
        start = len(host.text())
        host.wait_log(r"CYCLE_SUMMARY [^\n]*observed=0", start=start, deadline=300)
        check(lane.view("while-extracting") == before and "NATIVE_IMPORT" not in host.text(), "while SABnzbd still reports Extracting, nothing is planned or imported")
        check(any(r.get("mode") == "history" for r in mock.requests), "Lidarr's own SABnzbd client polled the (mock) SABnzbd history API")
        mock.status = "Completed"
        start = len(host.text())
        wait_summary(host, r"imported=1", start)
        verify_consistent(lane, "after-sab-import", [1], 2, [3])
        reg = registry(lane)
        check(reg[GOOD]["state"] == "Imported" and not reg[GOOD]["downloaderRemoved"] and reg[GOOD]["sourceRetained"], "registry: handled, source retained, not removed from the downloader")
        check(re.search(r'CYCLE_DOWNLOAD download="SABnzbd_nzo_crescendarr_m21"', host.text()) is not None, "the item came from Lidarr's SABnzbd client (SAB nzo id)")
        note(f"SAB_MOCK_REQUESTS modes={sorted({r.get('mode') for r in mock.requests})} count={len(mock.requests)}")
        check(not mock.destructive_requests(), "no delete/remove request ever reached the (mock) downloader")
        check({p.name: sha(p) for p in (lane.watch / GOOD).iterdir()} == lane.incoming, "SABnzbd storage folder bytes unchanged")
        mark = len(host.text())
        wait_summary(host, r"alreadyHandled=1", mark)
        check(host.text()[mark:].count("NATIVE_IMPORT") == 0, "the same SAB job is never imported twice")
    finally:
        host.stop()
        if mock:
            mock.close()


# ------------------------------------------------------------------ realistic MusicBrainz fixture, rename ON

def prepare_realistic(bin_dir: Path, plugin: Path, seeder: Path, base: Path, scenario: str) -> Lane:
    data, home = base / "config", base / "home"
    for d in (data, home, base / "library", base / "downloads" / "nzb", base / "downloads" / "watch", base / "recycle", base / "faults"):
        d.mkdir(parents=True)
    (base / ".crescendarr-disposable-host").write_text("CRESCENDARR_DISPOSABLE_HOST_V1\n", encoding="utf-8")
    manifest = json.loads((FIXTURES / "MANIFEST.json").read_text())
    for name, digest in manifest["files"].items():
        check(hashlib.sha256((FIXTURES / name).read_bytes()).hexdigest() == digest, f"captured fixture {name} matches its pinned SHA-256")
    shutil.copytree(FIXTURES, base / "fixtures")
    seeder_dir = install(seeder, data, "CrescendarrFixtureSeeder")
    env = environment(base, home, crescendarr=False, seeder=True)
    env["CRESCENDARR_M21_FIXTURES"] = str(base / "fixtures")
    boot0 = Host(bin_dir, data, env, base / "boot0.log")
    boot0.start("")
    try:
        naming = boot0.api("GET", "config/naming")
        naming["renameTracks"] = True
        boot0.api("PUT", f"config/naming/{naming['id']}", naming)
        check(boot0.api("GET", "config/naming")["renameTracks"] is True, "stock track renaming is ON (standard + multi-disc formats)")
        note(f"NAMING standard={naming.get('standardTrackFormat')!r} multiDisc={naming.get('multiDiscTrackFormat')!r}")
        seed = m17.configure_and_seed(boot0, base, scenario)
    finally:
        boot0.stop()
    shutil.rmtree(seeder_dir)
    install(plugin, data, "Crescendarr")
    note(f"SEED {json.dumps(seed)[:1500]}")
    return Lane(bin_dir, plugin, base, seed, data, home)


def realistic_view(lane: Lane, label: str) -> dict[int, dict]:
    state = lane.state(label)
    files = {r[0]: r for r in state["files"]}
    view = {}
    for t in lane.seed["tracks"]:
        row = [r for r in state["tracks"] if r[0] == t["trackId"]][0]
        f = files.get(row[3])
        view[t["n"]] = {"trackFileId": row[3], "path": f[1] if f else None, "quality": json.loads(f[2])["quality"] if f else None,
                        "sha": sha(Path(f[1])) if f and Path(f[1]).exists() else None,
                        "lyrics": sorted((l[0], l[2]) for l in state["lyrics"] if l[1] == row[3] and row[3] > 0)}
    return view


def place_named(lane: Lane, name: str) -> dict[str, str]:
    shutil.move(str(lane.base / "prepared" / name), str(lane.watch / name))
    return {p.name: sha(p) for p in (lane.watch / name).iterdir()}


def refused_without_change(lane: Lane, host: Host, name: str, expect: tuple[str, ...]) -> None:
    before, before_files = realistic_view(lane, f"before-{name[:20]}"), files_state(lane.artist_path)
    place_named(lane, name)
    plan = plan_until_completed(host, name)
    check(field(plan, "disposition") in expect, f"{name}: plan disposition {field(plan, 'disposition')} is a refusal")
    start = len(host.text())
    host.command({"name": "CrescendarrImport", "downloadId": field(plan, "download"), "planId": field(plan, "planId") or "none"})
    check("IMPORT_REFUSED" in host.text()[start:] and "NATIVE_IMPORT" not in host.text()[start:], f"{name}: import refused, no native call")
    check(realistic_view(lane, f"after-{name[:20]}") == before and files_state(lane.artist_path) == before_files, f"{name}: zero row, link, lyric or byte change")
    shutil.move(str(lane.watch / name), str(lane.base / "prepared" / name))


def lane_realistic(lane: Lane, repeated: bool) -> None:
    host = lane.boot()
    try:
        tracks = {t["n"]: t for t in lane.seed["tracks"]}
        before = realistic_view(lane, "before")
        note(f"OWNED_PATH_SAMPLE {[tracks[n].get('path') for n in (1, 15) if tracks[n].get('owned')]}")
        if repeated:
            name = "Realistic Fixture [good]"
            place_named(lane, name)
            plan = plan_until_completed(host, name)
            note("REPEATED_PLAN " + plan[:1200])
            check(field(plan, "disposition") != "Ready", "repeated recording MBIDs: the plan is NOT Ready (fail closed)")
            start = len(host.text())
            host.command({"name": "CrescendarrImport", "downloadId": field(plan, "download"), "planId": field(plan, "planId") or "none"})
            check("IMPORT_REFUSED" in host.text()[start:] and "NATIVE_IMPORT" not in host.text()[start:], "repeated recordings: import refused")
            check(realistic_view(lane, "after-repeated") == before, "repeated recordings: zero library change")
            return
        refused_without_change(lane, host, "Realistic Fixture [other edition]", ("NeedsReview", "Blocked"))
        refused_without_change(lane, host, "Realistic Fixture [unknown audio]", ("NeedsReview", "Blocked"))
        name = "Realistic Fixture [good]"
        incoming = place_named(lane, name)
        plan = plan_until_completed(host, name)
        note("GOOD_PLAN " + plan[:2000])
        check(field(plan, "disposition") == "Ready", "real-identity 2-disc download produced a Ready plan")
        result = host.command({"name": "CrescendarrImport", "downloadId": field(plan, "download"), "planId": field(plan, "planId")})
        check(status(result) == "completed" and "outcome=Completed" in host.text(), "the 2-disc rename-enabled import completed")
        after = realistic_view(lane, "after")
        for n, t in tracks.items():
            src = f"d{t['medium']}t{t['position']:02d}.mp3"
            v = after[n]
            if t["medium"] == 1 and t["position"] == 3:
                check(v == before[n] and v["sha"] == t["sha"], f"disc 1 track 3 (superior FLAC): row, bytes and lyric untouched")
                continue
            check(v["trackFileId"] > 0 and v["sha"] == incoming[src] and v["quality"] == 3, f"disc {t['medium']} track {t['position']}: incoming MP3-256 bytes linked")
            check(Path(v["path"]).name != src and Path(v["path"]).is_relative_to(lane.artist_path), f"disc {t['medium']} track {t['position']}: stored at its stock-renamed path, not the download name")
            if t.get("owned"):
                check(v["path"] == t["path"], f"disc {t['medium']} track {t['position']}: replaced in place at its existing stock-named path")
                check(len(v["lyrics"]) == 1 and sha(Path(t["lyricPath"])) == t["lyricSha"], f"disc {t['medium']} track {t['position']}: lyric bytes kept and registered to the new row")
        check(sha(Path(lane.seed["cover"])) == lane.seed["coverSha"], "album folder extra (cover.jpg) untouched")
        recycled = [p.name for p in (lane.base / "recycle").rglob("*") if p.is_file()]
        note(f"RECYCLE {len(recycled)} files; lrc={sum(1 for x in recycled if x.endswith('.lrc'))}")
        check(not any(x.endswith(".lrc") for x in recycled), "no lyric recycled")
        scan = host.command({"name": "RescanFolders"})
        check(status(scan) == "completed" and realistic_view(lane, "after-rescan") == after, "stock rescan changed nothing (no cascade)")
    finally:
        host.stop()


# ------------------------------------------------------------------ disposable install / upgrade / uninstall

def lane_install(bin_dir: Path, package: Path, base: Path) -> None:
    data, home = base / "config", base / "home"
    for d in (data, home):
        d.mkdir(parents=True)
    host = Host(bin_dir, data, environment(base, home, crescendarr=False, seeder=False), base / "boot0.log")
    host.start("")
    settings = lambda h: {k: h.api("GET", k) for k in ("config/mediamanagement", "config/downloadclient", "config/naming", "notification", "downloadclient", "rootfolder")}
    try:
        pristine = settings(host)
    finally:
        host.stop()
    with zipfile.ZipFile(package) as z:
        names = z.namelist()
        sums = dict(line.split("  ")[::-1] for line in z.read("SHA256SUMS").decode().splitlines() if line.strip())
        check(set(names) == {"Lidarr.Plugin.Crescendarr.dll", "SHA256SUMS", "INSTALL.txt"}, f"package contains exactly the plugin, checksums and notes ({names})")
        check(hashlib.sha256(z.read("Lidarr.Plugin.Crescendarr.dll")).hexdigest() == sums["Lidarr.Plugin.Crescendarr.dll"], "plugin checksum verified before install")
        folder = data / "plugins" / "GQuX" / "Crescendarr"
        folder.mkdir(parents=True)
        (folder / "Lidarr.Plugin.Crescendarr.dll").write_bytes(z.read("Lidarr.Plugin.Crescendarr.dll"))
    for label in ("install", "upgrade"):
        host = Host(bin_dir, data, environment(base, home, crescendarr=False, seeder=False), base / f"boot-{label}.log")
        host.start("CRESCENDARR INERT")
        try:
            check(settings(host) == pristine, f"{label}: no Lidarr setting changed by installing the plugin")
            check("CRESCENDARR READY" not in host.text() and not (base / "crescendarr").exists(), f"{label}: inert without the disposable opt-in; nothing written")
            check(any(p.get("implementation") == "CrescendarrConnect" for p in host.api("GET", "notification/schema")), f"{label}: the Connect settings provider is offered by stock Lidarr")
        finally:
            host.stop()
        (data / "plugins" / "GQuX" / "Crescendarr" / "Lidarr.Plugin.Crescendarr.dll").write_bytes(package.parent.joinpath("Lidarr.Plugin.Crescendarr.dll").read_bytes())
    shutil.rmtree(data / "plugins" / "GQuX" / "Crescendarr")
    host = Host(bin_dir, data, environment(base, home, crescendarr=False, seeder=False), base / "boot-uninstalled.log")
    host.start("")
    try:
        check("CRESCENDARR" not in host.text(), "after uninstall the plugin is gone")
        check(settings(host) == pristine, "after uninstall every Lidarr setting is unchanged")
        check(not any(p.get("implementation") == "CrescendarrConnect" for p in host.api("GET", "notification/schema")), "the Connect provider disappeared with the plugin")
    finally:
        host.stop()


def run(bin_dir: Path, plugin: Path, seeder: Path, name: str, base: Path) -> None:
    if name == "install-cycle":
        lane_install(bin_dir, plugin, base)
    elif name == "sab-client":
        lane_sab(prepare(bin_dir, plugin, seeder, base, "three"))
    else:
        lane_realistic(prepare_realistic(bin_dir, plugin, seeder, base, "realistic-repeated" if name == "realistic-repeated" else "realistic"), name == "realistic-repeated")
    note(f"M21_LANE_COMPLETE {name}")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 5 or sys.argv[4] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder = (Path(a).resolve(strict=True) for a in sys.argv[1:4])
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m21-{sys.argv[4]}-", dir=runner)).resolve()
    try:
        run(bin_dir, plugin, seeder, sys.argv[4], base)
        return 0
    except Failure as failure:
        print(f"M21_FAILED: {failure}", file=sys.stderr)
        note(f"M21_FAILED: {failure}")
        return 1
    finally:
        for log in sorted(base.glob("boot*.log")):
            print(f"=== {log.name} (Crescendarr and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]")):
                    print(line[:2000])
        (runner / f"m21-{sys.argv[4]}-evidence.txt").write_text("\n".join(m17.EVIDENCE) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M21 {sys.argv[4]}\n\n```\n" + "\n".join(m17.EVIDENCE[-150:]) + "\n```\n")


if __name__ == "__main__":
    sys.exit(main())
