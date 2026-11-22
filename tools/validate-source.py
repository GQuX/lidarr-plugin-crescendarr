#!/usr/bin/env python3
"""Advisory source-structure smoke checks only. NOT a security or import safety proof.

These syntax/substring/regex checks cannot detect all side-effecting calls,
indirect invocation, plugin startup fallback, filesystem races or DB writes.
"""
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
# Trivial catch for obvious accidents, NOT a complete side-effect analysis.
for forbidden in [r'\bFile\.(Delete|Move|Copy|Write|Replace|Create|Open)',
                  r'\bFileInfo\b', r'\bFileStream\b', r'\bDirectory\.(Delete|Move|Create)',
                  r'\bSqliteConnection\b', r'\bHttpClient\b', r'\bProcess\.Start',
                  r'\bIDiskProvider\b', r'\bIFileSystem\b']:
    require(re.search(forbidden, core_code) is None, f'Unexpected side-effect capability in Core: {forbidden}')

probe_code = (ROOT / 'src/Crescendarr.Lidarr.Probe/ProbeImportService.cs').read_text()
require('IDownloadedTracksImportService' in probe_code, 'No interface probe')
require('RequireDisposableEnvironment()' in probe_code, 'No disposable environment guard')
require('NO DELEGATION' in probe_code, 'Probe no-delegation marker missing')
require('original.Process' not in probe_code and 'original.ShouldDelete' not in probe_code,
        'Probe must not delegate to core import')
require('public bool ShouldDeleteFolder' in probe_code and 'return false;' in probe_code,
        'Expected marker missing: probe cleanup stub (NOT validated by a parser)')

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
print(f'ADVISORY_STRUCTURE_SMOKE_OK: {count} C# specs declared, XML/JSON parse, obvious risky source patterns not found.')
print('NOT_PROVEN_BY_THIS_SCRIPT: C# semantics, plugin non-delegation, host behavior, real imports, lyrics, concurrency, recovery, or production safety.')
print('CI separately runs C# planner specs and a disposable-host DI probe; those tests are NOT executed by this script.')
