#!/usr/bin/env python3
"""Static manifest/safety checks only. DOES NOT compile C# or run a Lidarr plugin."""
from __future__ import annotations
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []
def require(ok: bool, message: str) -> None:
    if not ok: errors.append(message)

for csproj in ROOT.rglob('*.csproj'):
    try: ET.parse(csproj)
    except ET.ParseError as e: errors.append(f'Invalid XML {csproj.relative_to(ROOT)}: {e}')
for path in [ROOT / 'global.json', ROOT / 'docs/compatibility/m1-probe-report.template.json']:
    try: json.loads(path.read_text())
    except (OSError, ValueError) as e: errors.append(f'Invalid JSON {path.name}: {e}')

core_dir = ROOT / 'src/Crescendarr.Core'
core_code = '\n'.join(p.read_text() for p in core_dir.rglob('*.cs'))
for forbidden in [r'\bFile\.(Delete|Move|Copy|Write)', r'\bDirectory\.(Delete|Move)',
                  r'\bSqliteConnection\b', r'\bHttpClient\b', r'\bProcess\.Start']:
    require(re.search(forbidden, core_code) is None, f'Unexpected side-effect capability in Core: {forbidden}')

probe_code = (ROOT / 'src/Crescendarr.Lidarr.Probe/ProbeImportService.cs').read_text()
require('IDownloadedTracksImportService' in probe_code, 'No interface probe')
require('RequireDisposableEnvironment()' in probe_code, 'No disposable environment guard')
require('NO DELEGATION' in probe_code, 'Probe no-delegation marker missing')
require('original.Process' not in probe_code and 'original.ShouldDelete' not in probe_code,
        'Probe must not delegate to core import')
require('public bool ShouldDeleteFolder' in probe_code and 'return false;' in probe_code,
        'Probe might permit stage cleanup')

spec_text = (ROOT / 'tests/Crescendarr.Core.Spec/Program.cs').read_text()
header = spec_text.split('int failures = 0;')[0]
count = len(re.findall(r'^\s*\("[^\"]+",\s*[A-Za-z_][A-Za-z0-9_]*\),?$', header, re.M))
require(count >= 20, f'Expected >=20 executable C# specs, found {count}')
require('CrescendarrDisposableHostBuild' in (ROOT / 'src/Crescendarr.Lidarr.Probe/Crescendarr.Lidarr.Probe.csproj').read_text(),
        'Probe build guard missing')
require('<TargetFramework>net8.0</TargetFramework>' in (ROOT / 'Directory.Build.props').read_text(),
        'Stable v3.1.6 host requires net8.0')
require('"version": "8.0.100"' in (ROOT / 'global.json').read_text(),
        'net8 SDK pin missing')
require('Include COMPLETE incoming and existing snapshots' in (ROOT / 'src/Crescendarr.Core/Planning/MergePlanner.cs').read_text(),
        'Plan fingerprint does not include complete inventory')
require('SafeUpgrade' in (ROOT / 'docs/architecture/blueprint.md').read_text(),
        'Architecture blueprint missing')
require(not any(ROOT.glob('**/*install*.sh')), 'Unexpected install script')

if errors:
    for error in errors: print('FAIL', error, file=sys.stderr)
    sys.exit(1)
print(f'STATIC_STRUCTURE_OK: {count} C# specs authored; XML/JSON parse; no forbidden Core write APIs.')
print('NOT_RUN: .NET compile/spec execution, host DI probe, download lifecycle, filesystem/SMB safety.')
