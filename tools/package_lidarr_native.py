#!/usr/bin/env python3
"""Package ONLY the M25 audit-only DLL for stock Lidarr's GitHub URL installer (M26).

Lidarr v3.1.6.5079 PluginService looks for a published non-draft GitHub release on
the default tree (target_commitish main/master), with a release asset containing
"net8.0.zip". InstallPluginService extracts the archive under
<AppData>/plugins/<GitHub owner>/<GitHub repository>/; it does not require a
plugin.json or manifest.json. See docs/release-gates/m26-native-installer.md.

This script deliberately does not publish releases/tags, read other assemblies,
or package the write-enabled plugin. The caller MUST first run tools/audit-scan
against the same exact DLL. The zip contains only the audit plugin DLL.

Usage: package_lidarr_native.py <AUDIT-DLL> <version e.g. 0.25.0-audit-beta.1> <output-dir>
"""
from __future__ import annotations

import hashlib
import re
import sys
import zipfile
from pathlib import Path

ZIP_EPOCH = (1980, 1, 1, 0, 0, 0)
DLL = "Lidarr.Plugin.Crescendarr.dll"


def make_package(dll: Path, version: str, dest: Path) -> Path:
    if not re.fullmatch(r"\d+\.\d+\.\d+-audit-beta\.\d+", version):
        raise ValueError("refusing a non-audit beta version label")
    if dll.name != DLL or not dll.is_file():
        raise ValueError(f"expected exact pre-scanned audit DLL named {DLL}")
    data = dll.read_bytes()
    if not data.startswith(b"MZ"):
        raise ValueError("expected a compiled .NET assembly, not source or a different artifact")
    # Defense in depth, NOT a replacement for tools/audit-scan's full metadata/IL verifier:
    # wrong-flavor writer assemblies must never become a GitHub installer asset by accident.
    if b"CrescendarrAuditCycleCommand" not in data or b"AuditBarrier" not in data:
        raise ValueError("audit-only build markers are missing; refusing to package")
    if any(name in data for name in (b"CrescendarrImportCommand", b"CrescendarrRecoverCommand", b"CrescendarrApproveCommand")):
        raise ValueError("writer command detected in DLL; refusing to package")
    dest.mkdir(parents=True, exist_ok=True)
    path = dest / f"Crescendarr-v{version}.net8.0.zip"
    with zipfile.ZipFile(path, "w") as archive:
        info = zipfile.ZipInfo(DLL, ZIP_EPOCH)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.create_system = 3
        info.external_attr = 0o644 << 16
        archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    (dest / (path.name + ".sha256")).write_text(f"{digest}  {path.name}\n", encoding="ascii")
    print(f"NATIVE_INSTALLER_ZIP {path.name} sha256={digest} dll_sha256={hashlib.sha256(data).hexdigest()}")
    return path


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    make_package(Path(sys.argv[1]).resolve(strict=True), sys.argv[2], Path(sys.argv[3]).resolve())
    return 0


if __name__ == "__main__":
    sys.exit(main())
