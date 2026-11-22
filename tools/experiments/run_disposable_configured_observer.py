#!/usr/bin/env python3
"""Disposable stock Lidarr: configured synthetic blackhole with CDH OFF.

CI-only, no production API keys, no real SABnzbd, no external music or NAS mounts.
Creates a built-in Usenet Blackhole provider using stock Lidarr's local API.
Crescendarr's test plugin observes its ProviderAdded/ProviderUpdated events and
READS GetItems(), never imports audio, removes the job or modifies any library.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from xml.etree import ElementTree

PORT = 18688
NAME = "Crescendarr Synthetic Blackhole"
MARKER = "CRESCENDARR_DISPOSABLE_HOST_V1"
EXPECTED = "CRESCENDARR_SYNTHETIC_CLIENT_ITEM phase=updated CDH_ENABLED=False STATUS=Completed ID_MATCH=True CAN_REMOVE=False"


def get_key(config: Path) -> str:
    config_file = config / "config.xml"
    if not config_file.is_file():
        raise RuntimeError("Fresh Lidarr config.xml not yet generated.")
    key = ElementTree.parse(config_file).getroot().findtext("ApiKey")
    if not key:
        raise RuntimeError("Local disposable API key missing.")
    return key


def request(api_key: str, path: str, method: str = "GET", data: dict | None = None):
    url = f"http://127.0.0.1:{PORT}/api/v1/{path}"
    body = json.dumps(data).encode("utf-8") if data is not None else None
    headers = {"X-Api-Key": api_key, "Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    try:
        with urlopen(Request(url, data=body, headers=headers, method=method), timeout=10) as resp:
            raw = resp.read(300000).decode("utf-8")
            return json.loads(raw) if raw.strip() else None
    except HTTPError as exc:
        # Do not log API key; response is server-generated and contains no credentials.
        error = exc.read(1200).decode("utf-8", "replace")
        raise RuntimeError(f"Local synthetic API {method} {path} returned HTTP {exc.code}: {error}") from exc


def wait_for_server(port: int, proc: subprocess.Popen, seconds: int = 90):
    limit = time.monotonic() + seconds
    while time.monotonic() < limit:
        if proc.poll() is not None:
            raise RuntimeError(f"Disposable host terminated early with code {proc.returncode}")
        try:
            with urlopen(f"http://127.0.0.1:{port}/ping", timeout=1.5):
                return
        except HTTPError as exc:
            if exc.code in (401, 403, 404):
                return
        except (URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(1.5)
    raise RuntimeError("Disposable host did not respond to loopback HTTP.")


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Refusing execution outside explicitly opted-in GitHub Actions.")
    if len(sys.argv) != 3:
        raise ValueError("Usage: run_disposable_configured_observer.py <stock-bin> <observer-dll>")
    root = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
    bin_dir = Path(sys.argv[1]).resolve(strict=True)
    plugin_dll = Path(sys.argv[2]).resolve(strict=True)
    if plugin_dll.name != "Lidarr.Plugin.Crescendarr.ObserverProbe.dll":
        raise RuntimeError("Unexpected plugin binary.")
    if not (bin_dir / "Lidarr").is_file():
        raise RuntimeError("Missing official pinned Lidarr binary.")

    with tempfile.TemporaryDirectory(prefix="crescendarr-cdh-blackhole-", dir=root) as tmp:
        base = Path(tmp)
        data = base / "fresh-config"
        synthetic = base / "synthetic-only"
        watch = synthetic / "watch"
        incoming = synthetic / "nzb"
        album = watch / "Crescendarr-Synthetic-Album"
        home = base / "home"
        for folder in (data, synthetic, watch, incoming, album, home):
            folder.mkdir(parents=True, exist_ok=True)
        (synthetic / ".crescendarr-disposable-host").write_text(MARKER + "\n", encoding="utf-8")
        lyric = album / "01-fake-lyrics.lrc"
        lyric.write_text("[00:00.00] SYNTHETIC TEST ONLY\n", encoding="utf-8")
        original_bytes = lyric.read_bytes()

        plugin_dir = data / "plugins" / "GQuX" / "CrescendarrObserver"
        plugin_dir.mkdir(parents=True)
        shutil.copy2(plugin_dll, plugin_dir / plugin_dll.name)
        deps = plugin_dll.with_suffix(".deps.json")
        if deps.is_file():
            shutil.copy2(deps, plugin_dir / deps.name)

        env = {
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
            "HOME": str(home),
            "XDG_CONFIG_HOME": str(home),
            "LANG": "C.UTF-8",
            "TMPDIR": str(base),
            "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", ""),
            "Lidarr__Server__BindAddress": "127.0.0.1",
            "Lidarr__Server__Port": str(PORT),
            "CI": "true",
            "GITHUB_ACTIONS": "true",
            "CRESCENDARR_OBSERVER_OPT_IN": "DISPOSABLE_SYNTHETIC_ONLY",
            "CRESCENDARR_OBSERVER_SYNTHETIC_ROOT": str(synthetic),
        }
        logfile = base / "lidarr-configured-observer.log"
        proc = None
        try:
            with logfile.open("w", encoding="utf-8") as output:
                proc = subprocess.Popen(
                    [str(bin_dir / "Lidarr"), f"-data={data}", "-nobrowser", "-exitimmediately"],
                    cwd=bin_dir, stdout=output, stderr=subprocess.STDOUT,
                    env=env, start_new_session=True,
                )
                wait_for_server(PORT, proc)
                api_key = get_key(data)
                settings = request(api_key, "config/downloadclient")
                assert settings["enableCompletedDownloadHandling"] is True, "Fresh host should start with native CDH ON"
                settings["enableCompletedDownloadHandling"] = False
                request(api_key, f"config/downloadclient/{settings['id']}", "PUT", settings)
                persisted = request(api_key, "config/downloadclient")
                assert persisted["enableCompletedDownloadHandling"] is False, "CDH OFF must persist through native API"

                templates = request(api_key, "downloadclient/schema")
                candidates = [p for p in templates if p.get("implementation") == "UsenetBlackhole"]
                assert len(candidates) == 1, "Official UsenetBlackhole provider schema missing or ambiguous"
                blackhole = candidates[0]
                blackhole.update({
                    "name": NAME,
                    "enable": True,
                    "priority": 1,
                    "removeCompletedDownloads": False,
                    "removeFailedDownloads": False,
                })
                matched = set()
                for field in blackhole["fields"]:
                    if field["name"].lower() == "nzbfolder":
                        field["value"] = str(incoming)
                        matched.add("NzbFolder")
                    elif field["name"].lower() == "watchfolder":
                        field["value"] = str(watch)
                        matched.add("WatchFolder")
                assert matched == {"NzbFolder", "WatchFolder"}, f"Unexpected blackhole schema: {matched}"
                request(api_key, "downloadclient?forceSave=true", "POST", blackhole)

                created = request(api_key, "downloadclient")
                matching = [x for x in created if x["name"] == NAME]
                assert len(matching) == 1, "Disposable blackhole provider was not persisted"
                client = matching[0]

                # Blackhole's documented 30-second grace period expires after
                # the first GetItems() invocation triggered by ProviderAdded.
                # A changed provider priority triggers ProviderUpdated and a new
                # read-only GetItems() invocation, now reporting Completed.
                time.sleep(34)
                client["priority"] = 2
                request(api_key, f"downloadclient/{client['id']}?forceSave=true", "PUT", client)

                limit = time.monotonic() + 30
                while time.monotonic() < limit:
                    log = logfile.read_text(encoding="utf-8", errors="replace")
                    if EXPECTED in log:
                        break
                    if proc.poll() is not None:
                        break
                    time.sleep(1)

                log = logfile.read_text(encoding="utf-8", errors="replace")
                assert EXPECTED in log, "Observer did not see synthetic completed job under CDH OFF."
                assert request(api_key, "config/downloadclient")["enableCompletedDownloadHandling"] is False
                assert lyric.read_bytes() == original_bytes, "Synthetic lyric was modified."
                assert sorted(p.name for p in album.iterdir()) == [lyric.name], "Synthetic album folder changed."
                assert (synthetic / ".crescendarr-disposable-host").read_text().strip() == MARKER
                assert "CRESCENDARR_PROBE ProcessPath intercepted" not in log
                print("DISPOSABLE_CDH_OFF_COMPLETED_OBSERVER_OK")
                print("Native completed-download handling remained OFF in the ephemeral config.")
                print("Built-in blackhole client returned a synthetic completed item to read-only plugin.")
                print("No importer service overrides; original synthetic lyric content unchanged.")
                for line in log.splitlines()[-350:]:
                    if any(s in line for s in ("CRESCENDARR_OBSERVER", "CRESCENDARR_SYNTHETIC_CLIENT", "Error", "Exception")):
                        print(line[:650])
        finally:
            if proc is not None and proc.poll() is None:
                try:
                    os.killpg(proc.pid, signal.SIGTERM)
                    proc.wait(timeout=12)
                except (ProcessLookupError, subprocess.TimeoutExpired):
                    os.killpg(proc.pid, signal.SIGKILL)
                    proc.wait(timeout=8)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"DISPOSABLE_CDH_OFF_OBSERVER_FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
