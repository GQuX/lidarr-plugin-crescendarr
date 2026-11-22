#!/usr/bin/env python3
"""Run read-only Crescendarr observer on an ephemeral, blank stock Lidarr instance.

Never touches the user's instance or downloads. The probe has no importer
override and enumerates configured provider instances without polling GetItems.
"""
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import urlopen


def healthy(port: int) -> bool:
    try:
        with urlopen(f"http://127.0.0.1:{port}/ping", timeout=1.5) as response:
            return response.status in (200, 204, 301, 302, 401, 403, 404)
    except HTTPError as exc:
        return exc.code in (401, 403, 404)
    except (URLError, TimeoutError, ConnectionError):
        return False


def main() -> int:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("CI-only experimental host; refusing to run locally or on production.")
    runner_temp = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
    if len(sys.argv) != 3:
        raise ValueError("Usage: run_disposable_observer.py <pinned-official-bin-dir> <observer-probe-dll>")

    host_bin = Path(sys.argv[1]).resolve(strict=True)
    plugin_dll = Path(sys.argv[2]).resolve(strict=True)
    if not (host_bin / "Lidarr").is_file():
        raise RuntimeError("Expected official Lidarr executable not found.")
    if plugin_dll.name != "Lidarr.Plugin.Crescendarr.ObserverProbe.dll":
        raise RuntimeError("Unexpected observer assembly name.")

    with tempfile.TemporaryDirectory(prefix="crescendarr-observer-ci-", dir=runner_temp) as temp:
        root = Path(temp)
        config = root / "empty-config"
        home = root / "home"
        synthetic = root / "no-media-no-downloader"
        for folder in (config, home, synthetic):
            folder.mkdir()
        marker = synthetic / ".crescendarr-disposable-host"
        marker.write_text("CRESCENDARR_DISPOSABLE_HOST_V1\n", encoding="utf-8")

        plugin_dir = config / "plugins" / "GQuX" / "CrescendarrObserver"
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
            "TMPDIR": str(root),
            "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", ""),
            "Lidarr__Server__BindAddress": "127.0.0.1",
            "Lidarr__Server__Port": "18687",
            "CI": "true",
            "GITHUB_ACTIONS": "true",
            "CRESCENDARR_OBSERVER_OPT_IN": "DISPOSABLE_SYNTHETIC_ONLY",
            "CRESCENDARR_OBSERVER_SYNTHETIC_ROOT": str(synthetic),
        }

        logfile = root / "observer-host.log"
        proc = None
        ready = False
        http = False
        try:
            with logfile.open("w", encoding="utf-8") as output:
                proc = subprocess.Popen(
                    [str(host_bin / "Lidarr"), f"-data={config}", "-nobrowser", "-exitimmediately"],
                    cwd=host_bin, stdout=output, stderr=subprocess.STDOUT,
                    env=env, start_new_session=True,
                )
                until = time.monotonic() + 90
                while time.monotonic() < until:
                    log = logfile.read_text(encoding="utf-8", errors="replace")
                    ready = (
                        "CRESCENDARR_OBSERVER_READY CDH_ENABLED=True CLIENT_COUNT=0 IMPORT_OVERRIDE=NONE"
                        in log
                    )
                    http = healthy(18687)
                    if ready and http:
                        break
                    if proc.poll() is not None:
                        break
                    time.sleep(1.5)
        finally:
            if proc is not None and proc.poll() is None:
                try:
                    os.killpg(proc.pid, signal.SIGTERM)
                    proc.wait(timeout=12)
                except (ProcessLookupError, subprocess.TimeoutExpired):
                    os.killpg(proc.pid, signal.SIGKILL)
                    proc.wait(timeout=8)

        log = logfile.read_text(encoding="utf-8", errors="replace")
        print("=== READ-ONLY OBSERVER HOST RESULT ===")
        print("host_started_http=", http)
        print("observer_read_config_and_factory=", ready)
        print("probe_has_no_import_override=True")
        print("no_clients_or_user_media_configured=True")
        print("sentinel_preserved=", marker.is_file())
        for line in log.splitlines()[-250:]:
            if any(word in line for word in (
                "CRESCENDARR_OBSERVER", "Error", "Exception", "Unable to", "Failed",
            )):
                print(line[:800])
        if not ready or not http:
            raise AssertionError("Observer did not resolve stock client factory and read default CDH state.")
        if "CRESCENDARR_PROBE ProcessPath intercepted" in log:
            raise AssertionError("An import override unexpectedly ran in read-only observer test.")
        if not marker.is_file() or len(list(synthetic.iterdir())) != 1:
            raise AssertionError("Synthetic-only directory changed.")
        print("DISPOSABLE_OBSERVER_OK: stock Lidarr loaded observer; factory callable; no configured clients")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"DISPOSABLE_OBSERVER_FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
