#!/usr/bin/env python3
"""Run the non-delegating Crescendarr probe against a fresh disposable Lidarr host.

CI-only, Linux: NO existing configs, databases, NAS mounts or external downloads.
Expected result: host boots, plugin's startup handler resolves the service, and a
synthetic path is intercepted by our non-delegating probe.
"""
from __future__ import annotations

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


def require_ci() -> Path:
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("CI") != "true":
        raise RuntimeError("Disposable host probe requires a GitHub Actions CI runner.")
    runner_temp = os.environ.get("RUNNER_TEMP", "")
    if not runner_temp or not Path(runner_temp).is_dir():
        raise RuntimeError("RUNNER_TEMP must exist.")
    return Path(runner_temp).resolve()


def healthy_local_server(port: int) -> bool:
    try:
        with urlopen(f"http://127.0.0.1:{port}/ping", timeout=1.5) as response:
            return response.status in (200, 204, 301, 302, 401, 403, 404)
    except HTTPError as exc:
        return exc.code in (401, 403, 404)
    except (URLError, TimeoutError, ConnectionError):
        return False


def main() -> int:
    root = require_ci()
    if len(sys.argv) != 3:
        raise ValueError("Usage: run_disposable_lidarr.py <pinned-lidarr-bin> <compiled-probe-dll>")
    bin_dir = Path(sys.argv[1]).resolve(strict=True)
    plugin_dll = Path(sys.argv[2]).resolve(strict=True)

    lidarr_executable = bin_dir / "Lidarr"
    if not lidarr_executable.is_file():
        raise RuntimeError("Expected official Linux binary: <bin>/Lidarr")
    if plugin_dll.name != "Lidarr.Plugin.Crescendarr.Probe.dll":
        raise RuntimeError("Unexpected experimental probe assembly filename.")

    with tempfile.TemporaryDirectory(prefix="crescendarr-disposable-", dir=root) as folder:
        base = Path(folder).resolve()
        data = base / "empty-config"
        synthetic = base / "synthetic-fixtures"
        home = base / "home"
        for d in (data, synthetic, home):
            d.mkdir()
        (synthetic / ".crescendarr-disposable-host").write_text(
            "CRESCENDARR_DISPOSABLE_HOST_V1\n", encoding="utf-8"
        )

        # Confirm the auto-discovery pattern from Lidarr v3.1.6.5079's PathExtensions:
        # data/plugins/<owner>/<name>/Lidarr.Plugin.*.dll
        plugin_dir = data / "plugins" / "GQuX" / "CrescendarrProbe"
        plugin_dir.mkdir(parents=True)
        shutil.copyfile(plugin_dll, plugin_dir / plugin_dll.name)
        deps = plugin_dll.with_suffix(".deps.json")
        if deps.is_file():
            shutil.copyfile(deps, plugin_dir / deps.name)

        environment = {
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
            "HOME": str(home),
            "XDG_CONFIG_HOME": str(home),
            "LANG": "C.UTF-8",
            "TMPDIR": str(base),
            "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", ""),
            "Lidarr__Server__BindAddress": "127.0.0.1",
            "Lidarr__Server__Port": "18686",
            "CRESCENDARR_PROBE_OPT_IN": "DISPOSABLE_SYNTHETIC_ONLY",
            "CRESCENDARR_PROBE_SYNTHETIC_ROOT": str(synthetic),
        }
        logfile = base / "host-output.log"
        seen_intercept = False
        seen_service = False
        running_http = False
        process = None
        try:
            with logfile.open("w", encoding="utf-8") as output:
                process = subprocess.Popen(
                    [str(lidarr_executable), f"-data={data}", "-nobrowser", "-exitimmediately"],
                    cwd=bin_dir,
                    stdout=output,
                    stderr=subprocess.STDOUT,
                    env=environment,
                    start_new_session=True,
                )
                limit = time.monotonic() + 90
                while time.monotonic() < limit:
                    text = logfile.read_text(encoding="utf-8", errors="replace")
                    seen_service = "CRESCENDARR_RUNTIME SERVICE_SELECTED=" in text
                    seen_intercept = "CRESCENDARR_RUNTIME INTERCEPTION_OK" in text
                    if seen_intercept:
                        running_http = healthy_local_server(18686)
                    if seen_service and seen_intercept and running_http:
                        break
                    if process.poll() is not None:
                        break
                    time.sleep(1.5)
        finally:
            if process is not None and process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                    process.wait(timeout=12)
                except (ProcessLookupError, subprocess.TimeoutExpired):
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=8)

        final = logfile.read_text(encoding="utf-8", errors="replace")
        # Don't print an entire application log. No credentials exist, but bound output.
        print("=== DISPOSABLE HOST DI/INTERCEPTION EVIDENCE ===")
        print(f"host_process_exit={process.returncode if process else 'not-started'}")
        print(f"selected_service_logged={seen_service}")
        print(f"synthetic_interception_logged={seen_intercept}")
        print(f"localhost_http_responded={running_http}")
        print(f"source_marker_still_present={(synthetic / '.crescendarr-disposable-host').exists()}")
        print("=== RELEVANT LOG LINES ===")
        for line in final.splitlines()[-350:]:
            if any(word in line for word in ("CRESCENDARR", "Error", "Warn", "Exception", "Migration", "Startup", "Unable to")):
                print(line[:800])

        assert (synthetic / ".crescendarr-disposable-host").exists(), "Fixture marker was unexpectedly removed"
        assert len(list(synthetic.iterdir())) == 1, "Probe modified the synthetic fixture directory"
        if not (seen_service and seen_intercept and running_http):
            print("DISPOSABLE_RUNTIME_PROBE_INCONCLUSIVE: inspect host log and adapter registration", file=sys.stderr)
            return 1
        print("DISPOSABLE_RUNTIME_PROBE_OK: DI selected Crescendarr and synthetic-only method was intercepted")
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"DISPOSABLE_RUNTIME_PROBE_ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
