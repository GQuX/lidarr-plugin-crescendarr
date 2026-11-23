#!/usr/bin/env python3
"""M22 (issue #56): REAL disposable SABnzbd -> unmodified stock Lidarr (its own Sabnzbd client) -> ONE Crescendarr plugin.

A real, version- and digest-pinned SABnzbd process downloads synthetic NZBs from a CI-only localhost NNTP fixture
(tools/experiments/nntp_fixture.py): SABnzbd does the queueing, article download, yEnc decoding, assembly,
post-processing and history itself. Lidarr is configured with its stock Sabnzbd implementation, CDH OFF, completed/failed
removal OFF. Crescendarr only sees SABnzbd through Lidarr's IDownloadClientFactory. Nothing here talks to a real
Usenet provider, a user downloader, SMB or a NAS. The ephemeral SAB API key is random per run and redacted from output.

Lanes
  sab-real-happy         real job -> automatic guarded import; bytes/rows/lyrics/edition; restart SAB + Lidarr; no duplicate
  sab-real-failed-retry  a genuinely missing article -> real SAB Failed history -> no Crescendarr action; article provided,
                         SAB's own retry API -> Completed -> imported exactly once
  sab-real-restarts      SAB SIGKILLed mid-download and restarted (resumes from its own queue); Lidarr down when the job
                         completes; startup cycle imports once; SAB unavailable -> visible client error, no settlement
  sab-real-paths         a second SAB category completing OUTSIDE the disposable root plus a same-name job with different
                         content: refused with an actionable reason, the good job still imports

Usage: run_disposable_m22.py <lidarr-bin> <crescendarr-dll> <seeder-dll> <sabnzbd-dir> <python> <lane>
"""
from __future__ import annotations

import json
import os
import re
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import urlopen

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_disposable_m17 as m17  # noqa: E402
from nntp_fixture import ArticleStore, NntpServer, write_nzb  # noqa: E402
from run_disposable_m17 import CLIENT, Failure, Host, check, library_view, note, sha  # noqa: E402
from run_disposable_m18 import GOOD, Lane, prepare, verify_consistent  # noqa: E402
from run_disposable_m20 import configure, registry, summaries, wait_cycles, wait_summary  # noqa: E402

LANES = ("sab-real-happy", "sab-real-failed-retry", "sab-real-restarts", "sab-real-paths")
SAB_PORT, NNTP_PORT = 18085, 11119
WRONG = "Crescendarr Synthetic Album [wrong edition]"
API_KEY = secrets.token_hex(16)
NZB_KEY = secrets.token_hex(16)


def redact(text: str) -> str:
    return text.replace(API_KEY, "<redacted-api-key>").replace(NZB_KEY, "<redacted-nzb-key>")


class RealSab:
    """A real SABnzbd process (pinned source release) with a disposable config under the runner temp root."""

    def __init__(self, sab_dir: Path, python: str, base: Path, complete: Path, extra_categories: dict[str, str] | None = None):
        self.sab_dir, self.python, self.base = sab_dir, python, base
        self.home = base / "sabnzbd"
        self.home.mkdir(exist_ok=True)
        self.ini = self.home / "sabnzbd.ini"
        self.log = base / "sabnzbd.log"
        self.process: subprocess.Popen | None = None
        self.states: list[tuple[str, str, str]] = []
        categories = {"music": ""}
        categories.update(extra_categories or {})
        cats = "\n".join(f"[[{name}]]\nname = {name}\norder = 0\npp = 3\nscript = None\ndir = {folder}\nnewzbin = \"\"\npriority = -100" for name, folder in categories.items())
        self.ini.write_text(f"""__version__ = 19
__encoding__ = utf-8
[misc]
api_key = {API_KEY}
nzb_key = {NZB_KEY}
host = 127.0.0.1
port = {SAB_PORT}
https_port = ""
enable_https = 0
inet_exposure = 0
download_dir = {base / 'sab-incomplete'}
complete_dir = {complete}
admin_dir = {self.home / 'admin'}
log_dir = {self.home / 'logs'}
dirscan_dir = ""
notified_new_skin = 2
check_new_rel = 0
auto_browser = 0
enable_unrar = 0
enable_7zip = 0
direct_unpack = 0
pre_check = 0
fail_hopeless_jobs = 1
allow_incomplete_nzb = 0
history_retention_option = all
ignore_samples = 0
deobfuscate_final_filenames = 0
enable_meta = 1
[servers]
[[127.0.0.1]]
name = 127.0.0.1
displayname = crescendarr-ci-nntp
host = 127.0.0.1
port = {NNTP_PORT}
timeout = 30
username = ""
password = ""
connections = 2
ssl = 0
ssl_verify = 0
enable = 1
required = 0
optional = 0
retention = 0
priority = 0
expire_date = ""
quota = ""
[categories]
[[*]]
name = *
order = 0
pp = 3
script = None
dir = ""
newzbin = ""
priority = 0
{cats}
""", encoding="utf-8")

    def start(self) -> None:
        handle = self.log.open("a", encoding="utf-8")
        self.process = subprocess.Popen([self.python, "-OO", str(self.sab_dir / "SABnzbd.py"), "-f", str(self.ini), "-s", f"127.0.0.1:{SAB_PORT}",
                                         "-b", "0", "-l", "1", "--disable-file-log"], cwd=self.sab_dir, stdout=handle, stderr=subprocess.STDOUT, start_new_session=True)
        limit = time.monotonic() + 120
        while time.monotonic() < limit:
            try:
                version = self.api("version")["version"]
                note(f"SAB_READY version={version} pid={self.process.pid}")
                return
            except Exception:  # noqa: BLE001 - not up yet
                if self.process.poll() is not None:
                    raise Failure("SABnzbd exited early:\n" + redact(self.log.read_text(errors="replace")[-3000:]))
                time.sleep(0.5)
        raise Failure("SABnzbd not ready")

    def api(self, mode: str, **params) -> dict:
        query = urlencode({"mode": mode, "output": "json", "apikey": API_KEY, **params})
        with urlopen(f"http://127.0.0.1:{SAB_PORT}/api?{query}", timeout=15) as response:
            return json.loads(response.read())

    def add(self, nzb: Path, category: str = "music") -> str:
        result = self.api("addlocalfile", name=str(nzb), cat=category)
        check(result.get("status") is True and result.get("nzo_ids"), f"real SABnzbd accepted {nzb.name}")
        return result["nzo_ids"][0]

    def job(self, nzo: str) -> tuple[str, dict | None]:
        queue = self.api("queue")["queue"]["slots"]
        for slot in queue:
            if slot["nzo_id"] == nzo:
                return slot["status"], slot
        for slot in self.api("history", limit=50)["history"]["slots"]:
            if slot["nzo_id"] == nzo:
                return slot["status"], slot
        return "Absent", None

    def wait(self, nzo: str, wanted: tuple[str, ...], deadline: float = 300) -> dict:
        limit, last = time.monotonic() + deadline, None
        while time.monotonic() < limit:
            state, slot = self.job(nzo)
            if state != last:
                self.states.append((time.strftime("%H:%M:%S"), nzo, state))
                note(f"SAB_STATE nzo={nzo} state={state}")
                last = state
            if state in wanted:
                return slot
            time.sleep(0.4)
        raise Failure(f"SAB job {nzo} never reached {wanted} (last {last})")

    def stop(self, kill: bool = False) -> None:
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL if kill else signal.SIGTERM)
            try:
                self.process.wait(timeout=60)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=15)
        note(f"SAB_STOPPED kill={kill}")


def connect_lidarr(host: Host, name: str = "Crescendarr Synthetic SABnzbd", category: str = "music") -> None:
    for client in host.api("GET", "downloadclient"):
        if client["name"] == CLIENT:
            host.api("DELETE", f"downloadclient/{client['id']}")
    schema = [s for s in host.api("GET", "downloadclient/schema") if s.get("implementation") == "Sabnzbd"][0]
    values = {"host": "127.0.0.1", "port": SAB_PORT, "apiKey": API_KEY, "musicCategory": category, "useSsl": False, "urlBase": ""}
    for f in schema["fields"]:
        if f["name"] in values:
            f["value"] = values[f["name"]]
    schema.update({"name": name, "enable": True, "priority": 1, "removeCompletedDownloads": False, "removeFailedDownloads": False})
    saved = host.api("POST", "downloadclient", schema)  # NOT forceSave: Lidarr's own connection test must pass against real SAB
    check(saved.get("implementation") == "Sabnzbd", f"stock Lidarr Sabnzbd client '{name}' saved after Lidarr's own connection test against real SABnzbd")
    check(host.api("GET", "config/downloadclient")["enableCompletedDownloadHandling"] is False, "stock CDH stays OFF")


def stage_job(lane: Lane, store: ArticleStore, source: str, job: str) -> tuple[Path, dict[str, str]]:
    folder = lane.base / "prepared" / source
    files = sorted(p for p in folder.iterdir() if p.is_file())
    nzb = lane.base / "nzbs" / f"{job}.nzb"
    nzb.parent.mkdir(exist_ok=True)
    write_nzb(store, files, nzb, job)
    return nzb, {p.name: sha(p) for p in files}


def no_delete_and_intact(lane: Lane, sab: RealSab, nzo: str, folder: Path, incoming: dict[str, str]) -> None:
    state, slot = sab.job(nzo)
    check(state == "Completed" and slot, f"the job is still in real SABnzbd history as Completed (never deleted): {nzo}")
    check(Path(slot["storage"]) == folder, "SABnzbd's reported storage is exactly the folder Lidarr/Crescendarr used")
    check({p.name: sha(p) for p in folder.iterdir() if p.is_file()} == incoming, "the completed download is intact (same files, same bytes)")
    reg = registry(lane)
    entry = next(e for e in reg.values() if Path(e["folder"]) == folder)
    check(not entry["downloaderRemoved"] and not entry["lidarrImported"], "registry never claims downloader removal or stock import")


def boot_with_sab(lane: Lane, sab: RealSab, mode: str = "GuardedAuto") -> Host:
    host = lane.boot()
    connect_lidarr(host)
    configure(host, mode)
    return host


# ------------------------------------------------------------------ lanes

def lane_happy(lane: Lane, sab: RealSab, store: ArticleStore) -> None:
    nzb, incoming = stage_job(lane, store, GOOD, GOOD)
    host = boot_with_sab(lane, sab)
    try:
        before = lane.view("before")
        nzo = sab.add(nzb)
        start = len(host.text())
        slot = sab.wait(nzo, ("Completed", "Failed"))
        check(slot["status"] == "Completed", "real SABnzbd downloaded, assembled and post-processed the job (history Completed)")
        note(f"SAB_HISTORY_SLOT {redact(json.dumps({k: slot.get(k) for k in ('nzo_id', 'name', 'status', 'storage', 'bytes', 'download_time', 'postproc_time', 'stage_log')}))[:1500]}")
        check(len(store.served) >= len(incoming), f"real on-wire NNTP: SABnzbd fetched {len(store.served)} articles from the localhost server")
        states = {s for _, n, s in sab.states if n == nzo}
        check(len(states) >= 2, f"real per-job state changes observed: {sorted(states)}")
        folder = Path(slot["storage"])
        check(folder == lane.watch / GOOD, "SABnzbd completed into the disposable downloads folder Lidarr and Crescendarr see (no remote mapping)")
        lane.incoming = incoming
        wait_summary(host, r"imported=1", start)
        check(lane.view("before-check") != before, "the library changed only through the automatic cycle")
        verify_consistent(lane, "after-real-sab-import", [1], 2, [3])
        no_delete_and_intact(lane, sab, nzo, folder, incoming)
        final = lane.view("final")
        host.stop()
        sab.stop()
        sab.start()
        host = lane.boot()
        wait_summary(host, r"alreadyHandled=1", 0)
        check(lane.view("after-double-restart") == final and "NATIVE_IMPORT" not in host.text(), "SAB + Lidarr restart: no duplicate import, no status drift")
        no_delete_and_intact(lane, sab, nzo, folder, incoming)
    finally:
        host.stop()


def lane_failed_retry(lane: Lane, sab: RealSab, store: ArticleStore) -> None:
    nzb, incoming = stage_job(lane, store, GOOD, GOOD)
    withheld = sorted(store.articles)[len(store.articles) // 2]
    store.withhold(withheld)
    host = boot_with_sab(lane, sab)
    try:
        before, before_files = lane.view("before"), m17.files_state(lane.artist_path)
        nzo = sab.add(nzb)
        slot = sab.wait(nzo, ("Completed", "Failed"))
        note(f"SAB_FAILED_SLOT {redact(json.dumps({k: slot.get(k) for k in ('status', 'fail_message', 'storage')}))}")
        check(slot["status"] == "Failed" and withheld in store.missing, "a genuinely missing article made REAL SABnzbd fail the job (history Failed)")
        mark = len(host.text())
        wait_cycles(host, mark, 2)
        check("NATIVE_IMPORT" not in host.text() and lane.view("after-failed") == before and m17.files_state(lane.artist_path) == before_files,
              "no Crescendarr mutation for a Failed SABnzbd job across two cycles")
        check(not registry(lane), "a Failed job is never recorded as handled (not observed as Completed)")
        store.release_withheld()
        retried = sab.api("retry", value=nzo)
        note(f"SAB_RETRY {retried}")
        new = retried.get("nzo_id") or nzo
        start = len(host.text())
        slot = sab.wait(new, ("Completed", "Failed"))
        check(slot["status"] == "Completed", "SABnzbd's own retry downloaded the now-available article and completed the job")
        lane.incoming = incoming
        wait_summary(host, r"imported=1", start)
        verify_consistent(lane, "after-retry-import", [1], 2, [3])
        no_delete_and_intact(lane, sab, new, Path(slot["storage"]), incoming)
        mark = len(host.text())
        wait_cycles(host, mark, 1)
        check(host.text()[mark:].count("NATIVE_IMPORT") == 0, "imported exactly once")
    finally:
        host.stop()


def lane_restarts(lane: Lane, sab: RealSab, store: ArticleStore) -> None:
    nzb, incoming = stage_job(lane, store, GOOD, GOOD)
    store.delay = 1.5
    host = boot_with_sab(lane, sab)
    try:
        before = lane.view("before")
        nzo = sab.add(nzb)
        sab.wait(nzo, ("Downloading", "Fetching"), deadline=120)
        while len(store.served) < 2:
            time.sleep(0.2)
        sab.stop(kill=True)
        check(sab.process.poll() is not None, "real SABnzbd SIGKILLed mid-download")
        mark = len(host.text())
        wait_cycles(host, mark, 1)
        text = host.text()[mark:]
        note("UNAVAILABLE_CYCLE " + (re.findall(r"CRESCENDARR (?:CYCLE_CLIENT_ERROR|CYCLE_SUMMARY)[^\n]*", text) or ["none"])[-1])
        check("CYCLE_CLIENT_ERROR" in text, "SABnzbd unavailable: the cycle reports a visible client error")
        check(lane.view("while-sab-down") == before and not registry(lane), "SABnzbd unavailable: no mutation and nothing recorded as handled or settled")
        host.stop()
        store.delay = 0
        sab.start()
        slot = sab.wait(nzo, ("Completed", "Failed"))
        check(slot["status"] == "Completed", "after the restart real SABnzbd resumed the job from its own queue and completed it (Lidarr was down)")
        lane.incoming = incoming
        host = lane.boot()
        wait_summary(host, r"imported=1", 0)
        verify_consistent(lane, "after-startup-import", [1], 2, [3])
        final = lane.view("final")
        host.stop()
        host = lane.boot()
        wait_summary(host, r"alreadyHandled=1", 0)
        check(lane.view("after-restart") == final and "NATIVE_IMPORT" not in host.text(), "restart after import: no duplicate")
        no_delete_and_intact(lane, sab, nzo, Path(slot["storage"]), incoming)
    finally:
        host.stop()


def lane_paths(lane: Lane, sab: RealSab, store: ArticleStore, outside: Path) -> None:
    good_nzb, incoming = stage_job(lane, store, GOOD, GOOD)
    outside_nzb, _ = stage_job(lane, store, GOOD, GOOD + " outside")
    host = lane.boot()
    try:
        connect_lidarr(host, "Crescendarr Synthetic SABnzbd outside", "music-outside")
        connect_lidarr(host)
        configure(host, "GuardedAuto")
        before = lane.view("before")
        outside_nzo = sab.add(outside_nzb, "music-outside")
        slot = sab.wait(outside_nzo, ("Completed", "Failed"))
        check(slot["status"] == "Completed" and not Path(slot["storage"]).is_relative_to(lane.base), "real SABnzbd completed a job OUTSIDE the disposable root")
        good_nzo = sab.add(good_nzb)
        good = sab.wait(good_nzo, ("Completed", "Failed"))
        lane.incoming = incoming
        start = len(host.text())
        wait_summary(host, r"imported=1", start)
        text = host.text()
        check(re.search(r"CYCLE_REFUSED_ITEM [^\n]*outside", text) is not None, "the out-of-root job is refused with an actionable reason")
        verify_consistent(lane, "after-paths", [1], 2, [3])
        check({p.name: sha(p) for p in Path(slot["storage"]).iterdir()} == incoming, "the out-of-root download was not touched")
        refused = [e for e in registry(lane).values() if e["state"] == "RefusedUnsafePath"]
        check(refused and refused[0]["folder"] == slot["storage"], "the refusal is recorded durably with the offending path")
        # Same job NAME, different content (the other edition's files): a different SAB job and folder, never confused.
        same_nzb, _ = stage_job(lane, store, WRONG, GOOD)
        same_nzo = sab.add(same_nzb)
        same = sab.wait(same_nzo, ("Completed", "Failed"))
        check(same_nzo != good_nzo and Path(same["storage"]) != Path(good["storage"]), f"same-name job got its own SAB id and folder ({Path(same['storage']).name})")
        after = lane.view("before-same-name")
        mark = len(host.text())
        wait_cycles(host, mark, 2)
        check(lane.view("after-same-name") == after and host.text()[mark:].count("NATIVE_IMPORT") == 0, "same-name/different-content job: no import, library untouched")
        states = {Path(e["folder"]).name: e["state"] for e in registry(lane).values()}
        check(states.get(Path(same["storage"]).name) in ("NeedsReview", "Blocked") and states.get(GOOD) == "Imported",
              f"registry identity is folder + content, not display name: {states}")
        check(lane.view("final") != before, "the good job was imported")
        no_delete_and_intact(lane, sab, good_nzo, Path(good["storage"]), incoming)
    finally:
        host.stop()


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable CI runner required.")
    runner = Path(os.environ["RUNNER_TEMP"]).resolve()
    if len(sys.argv) != 7 or sys.argv[6] not in LANES:
        raise ValueError(__doc__)
    bin_dir, plugin, seeder, sab_dir = (Path(a).resolve(strict=True) for a in sys.argv[1:5])
    python, name = sys.argv[5], sys.argv[6]
    base = Path(tempfile.mkdtemp(prefix=f"crescendarr-m22-{name}-", dir=runner)).resolve()
    outside = Path(tempfile.mkdtemp(prefix="crescendarr-m22-outside-", dir=runner)).resolve()
    store = ArticleStore()
    nntp = NntpServer(store, NNTP_PORT)
    sab = None
    try:
        lane = prepare(bin_dir, plugin, seeder, base, "three")
        sab = RealSab(sab_dir, python, base, lane.watch, {"music-outside": str(outside)})
        sab.start()
        {"sab-real-happy": lambda: lane_happy(lane, sab, store), "sab-real-failed-retry": lambda: lane_failed_retry(lane, sab, store),
         "sab-real-restarts": lambda: lane_restarts(lane, sab, store), "sab-real-paths": lambda: lane_paths(lane, sab, store, outside)}[name]()
        note(f"SAB_STATES {sab.states}")
        note(f"NNTP served={len(store.served)} missing={len(store.missing)}")
        note(f"M22_LANE_COMPLETE {name}")
        return 0
    except Failure as failure:
        print(f"M22_FAILED: {failure}", file=sys.stderr)
        note(f"M22_FAILED: {failure}")
        return 1
    finally:
        if sab:
            sab.stop()
        nntp.close()
        for log in sorted(base.glob("boot*.log")):
            print(f"=== {log.name} (Crescendarr, download client and error lines) ===")
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if line.startswith("CRESCENDARR") or any(k in line for k in ("Exception", "[Error]", "[Fatal]", "Sabnzbd")):
                    print(redact(line[:2000]))
        if (base / "sabnzbd.log").exists():
            print("=== sabnzbd.log (tail, redacted) ===")
            print(redact((base / "sabnzbd.log").read_text(errors="replace")[-6000:]))
        for p in list(base.rglob("*.ini")) + list(base.rglob("*.log")):
            try:
                p.write_text(redact(p.read_text(errors="replace")))
            except OSError:
                pass
        (runner / f"m22-{name}-evidence.txt").write_text(redact("\n".join(m17.EVIDENCE)) + "\n", encoding="utf-8")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write(f"### M22 {name}\n\n```\n" + redact("\n".join(m17.EVIDENCE[-150:])) + "\n```\n")
        shutil.rmtree(outside, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
