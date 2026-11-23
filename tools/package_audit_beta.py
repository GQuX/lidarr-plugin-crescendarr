#!/usr/bin/env python3
"""M25: build a REPRODUCIBLE audit-only beta candidate zip from an already-built audit DLL.

Deterministic: fixed member order, fixed timestamps (1980-01-01, the zip epoch), fixed permissions, no extra fields,
fixed compression. The same DLL and documents always produce a byte-identical zip. Writes:
  <out>/crescendarr-audit-beta-<version>.zip     (DLL, docs, license, change log, SHA256SUMS inside)
  <out>/crescendarr-audit-beta-<version>.zip.sha256

Usage: package_audit_beta.py <Lidarr.Plugin.Crescendarr.dll> <version-label> <out-dir>
This only packages. It never publishes a release.
"""
from __future__ import annotations

import hashlib
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = [
    ("README-AUDIT-BETA.md", ROOT / "docs" / "audit-beta" / "README-AUDIT-BETA.md"),
    ("CHANGELOG.md", ROOT / "docs" / "audit-beta" / "CHANGELOG.md"),
    ("LICENSE", ROOT / "LICENSE"),
    ("THIRD-PARTY-NOTICES.md", ROOT / "docs" / "audit-beta" / "THIRD-PARTY-NOTICES.md"),
]
EPOCH = (1980, 1, 1, 0, 0, 0)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    dll, label, out = Path(sys.argv[1]).resolve(strict=True), sys.argv[2], Path(sys.argv[3]).resolve()
    if not label.replace(".", "").replace("-", "").isalnum() or "audit" not in label:
        raise SystemExit(f"version label must name the audit build, e.g. 0.25.0-audit-beta.1 (got {label!r})")
    out.mkdir(parents=True, exist_ok=True)
    members = [("Lidarr.Plugin.Crescendarr.dll", dll.read_bytes())] + [(name, path.read_bytes()) for name, path in DOCS]
    sums = "".join(f"{sha256(data)}  {name}\n" for name, data in members).encode()
    members.append(("SHA256SUMS", sums))
    target = out / f"crescendarr-audit-beta-{label}.zip"
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(members, key=lambda m: m[0]):
            info = zipfile.ZipInfo(name, date_time=EPOCH)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o644 & 0xFFFF) << 16
            info.create_system = 3
            archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    digest = sha256(target.read_bytes())
    (out / (target.name + ".sha256")).write_text(f"{digest}  {target.name}\n", encoding="ascii")
    print(f"PACKAGE {target.name} sha256={digest}")
    for name, data in members:
        print(f"  {sha256(data)}  {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
