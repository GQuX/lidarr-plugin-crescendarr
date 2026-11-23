#!/usr/bin/env python3
"""Reproducible build of the ONE Crescendarr plugin for a DISPOSABLE stock Lidarr host (M21, issue #52).

NOT a production release. The plugin stays inert unless the disposable CI opt-in, CI markers and a marked root exist.

  build_plugin.py <lidarr-bin-dir> <out-dir> [--version 0.0.0-m21]

Produces in <out-dir>:
  Lidarr.Plugin.Crescendarr.dll
  crescendarr-plugin-<version>-lidarr-3.1.6.5079.zip   (dll + SHA256SUMS + INSTALL.txt, fixed timestamps, sorted entries)
  crescendarr-plugin-<version>-lidarr-3.1.6.5079.zip.sha256
"""
from __future__ import annotations

import argparse
import hashlib
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "Crescendarr.Lidarr.Plugin" / "Crescendarr.Lidarr.Plugin.csproj"
HOST = "3.1.6.5079"
FIXED = (1980, 1, 1, 0, 0, 0)

INSTALL = """Crescendarr EXPERIMENTAL plugin for stock Lidarr {host} -- DISPOSABLE HOSTS ONLY. NOT FOR PRODUCTION.

Verify:     sha256sum -c SHA256SUMS
Install:    stop Lidarr; copy Lidarr.Plugin.Crescendarr.dll to <lidarr-data>/plugins/GQuX/Crescendarr/; start Lidarr.
            The plugin changes no Lidarr setting and stays INERT unless CRESCENDARR_M17_OPT_IN=DISPOSABLE_SYNTHETIC_ONLY,
            CI=true, GITHUB_ACTIONS=true and a marked CRESCENDARR_M17_ROOT are all present (disposable CI hosts only).
Configure:  Settings > Connect > Crescendarr (Mode defaults to Audit only).
Upgrade:    stop Lidarr; replace the dll (verify its checksum); start Lidarr. Journals, backups and the download registry
            under the disposable root are kept; recovery runs first on the next start.
Rollback:   stop Lidarr; put back the previous dll; start Lidarr.
Uninstall:  stop Lidarr; delete <lidarr-data>/plugins/GQuX/Crescendarr/; start Lidarr. Remove the Connect entry first if
            one was added. Never delete crescendarr/ops: it holds the only independent lyric backups.
"""


def build(bin_dir: Path, out: Path, version: str) -> Path:
    with tempfile.TemporaryDirectory(prefix="crescendarr-build-") as tmp:
        subprocess.run(["dotnet", "build", str(PROJECT), "-c", "Release", "--nologo", "-v", "q", "--output", tmp,
                        "-p:CrescendarrDisposableHostBuild=true", f"-p:LidarrBinDir={bin_dir}", "-p:Deterministic=true",
                        "-p:ContinuousIntegrationBuild=true", f"-p:PathMap={ROOT}=/crescendarr", f"-p:InformationalVersion={version}",
                        "-p:IncludeSourceRevisionInInformationalVersion=false"], check=True)
        dll = (Path(tmp) / "Lidarr.Plugin.Crescendarr.dll").read_bytes()
    out.mkdir(parents=True, exist_ok=True)
    (out / "Lidarr.Plugin.Crescendarr.dll").write_bytes(dll)
    sums = f"{hashlib.sha256(dll).hexdigest()}  Lidarr.Plugin.Crescendarr.dll\n"
    package = out / f"crescendarr-plugin-{version}-lidarr-{HOST}.zip"
    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in sorted({"INSTALL.txt": INSTALL.format(host=HOST).encode(), "Lidarr.Plugin.Crescendarr.dll": dll, "SHA256SUMS": sums.encode()}.items()):
            info = zipfile.ZipInfo(name, FIXED)
            info.external_attr = 0o644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    (out / (package.name + ".sha256")).write_text(f"{digest}  {package.name}\n")
    print(f"PACKAGE {package.name} sha256={digest} dll={hashlib.sha256(dll).hexdigest()}")
    return package


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("bin_dir", type=Path)
    parser.add_argument("out_dir", type=Path)
    parser.add_argument("--version", default="0.0.0-m21-disposable")
    args = parser.parse_args()
    build(args.bin_dir.resolve(), args.out_dir.resolve(), args.version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
