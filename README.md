# Crescendarr

**Selective, quality-aware track merging for Lidarr.**

> **DEVELOPMENT SOURCE ONLY. NOT INSTALLABLE. NOT SAFE FOR PRODUCTION.**
> The plugin integration seam, import lifecycle, recovery and `.lrc` preservation have **not** been proven in a running Lidarr host.

## Architecture safety decision (2026-11-22)

An independent review, followed by [Lidarr source verification](docs/reviews/2026-11-22-independent-review-triage.md), found a destructive mismatch: stock Lidarr can delete an entire album before selective imports, recycle/delete linked extras, and retry without plugins after some startup failures.

**Do not pursue IDownloadedTracksImportService as a write-enabled release mechanism.** The current probe characterizes disposable-host dependency injection only. Priority is [native import characterization (#6)](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6), [completion/lyrics/recovery (#7)](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7), and upstream compatibility with [PR #6166](https://github.com/Lidarr/Lidarr/pull/6166). The single-plugin goal is conditional on a safe host integration point. Our [independently reviewed incremental import proposal v0.2](docs/architecture/host-selective-import-incremental-v0.2.md) replaces the overlarge [v0.1 draft](docs/architecture/host-recoverable-import-protocol-v0.1.md). Both are **unimplemented** and require upstream Lidarr changes; safe standalone production operation remains unavailable.

## What we are building

A single Lidarr plugin that plans missing-track imports and strictly preferred individual upgrades without replacing entire album releases. It will preserve the monitored MusicBrainz edition, unrelated/better originals, and both existing and staged `.lrc` lyrics. It will not depend on Tubifarry, LRCGET, SABnzbd APIs, a daemon, or any other application besides compatible Lidarr. SABnzbd remains managed by Lidarr.

The current source is **only**:

- `src/Crescendarr.Core/`: pure deterministic merge planner. No Lidarr references, no disk/database/network access, and no file-write operations.
- `tests/Crescendarr.Core.Spec/`: standalone executable specifications using synthetic identities and a **fake** quality policy. They do not validate Lidarr's real quality profiles.
- `src/Crescendarr.Lidarr.Probe/`: deliberately non-delegating, test-only registration experiment. It has **no installer, no release workflow** and must not be put into production Lidarr. CI has compiled and loaded this probe in a disposable host; the probe has never processed a real download. The outer import-service override is not a release design.
- `deploy/portainer/disposable-lidarr-stable.compose.yml`: no-production-mount disposable host example (separate `/config`, port 8687); this does **not** install the probe.
- `tools/check_disposable_compose.py` and `tools/tests/`: development-only Compose lint with synthetic safety regression checks, including image-digest support and a loopback-only WebUI. These cannot prove network isolation.
- `docs/`: pinned compatibility hypotheses, safety boundaries, Portainer guide, experiment protocol and report template.

No import execution, queue completion, or recovery implementation exists yet.

## Running the disposable Portainer stack lint

This is a development configuration check only. Requires Python and PyYAML, never connects to Docker.

```sh
python -m pip install pyyaml
python tools/check_disposable_compose.py
python -m unittest discover -s tools/tests -v
```

For deployment instructions, read [Portainer disposable stable lab](docs/portainer/lidarr-stable-lab.md). **Do not copy production settings, downloads, database, or media mounts.**

## Running the pure planner specs

Requires the **.NET 8 SDK**:

```sh
dotnet run --project tests/Crescendarr.Core.Spec/Crescendarr.Core.Spec.csproj -c Release
```

No NuGet test framework dependency is needed. **GitHub Actions compiled and executed all 29 core specifications successfully on 2026-11-22.** The authoring environment has no local .NET SDK; the CI result applies to the pure planner, not to the test-only Lidarr probe or production imports.

## The critical host experiment

Read [disposable host protocol](docs/safety/disposable-host.md). The test-only probe requires exact disposable-host opt-in, a sentinel file and an explicit synthetic-root allowlist. It intentionally returns empty import decisions, which may cause *Lidarr's own* queue/import state to change. Use it only in an isolated host with a fresh disposable DB and no real downloads.

The [first disposable runtime test](docs/compatibility/disposable-runtime-evidence.md) showed the test-only service selected and its own startup handler directly calling a synthetic `ProcessPath`. **That is not a real Lidarr download/import caller.** **This is not a supported API nor proof of safe automatic imports.** The source-pinned Lidarr import interface at commit `a754943cbd85cf9257e984506e0143ede18135fe` is not an endorsed plugin interception API. We must prove type selection, startup, original concrete resolution and unload before designing a write-enabled plugin. A DI wrapper alone would **not** solve completion/edition/lyrics safety.

## Documentation

- [Host contract and uncertain integration seam](docs/compatibility/host-contract.md)
- [Disposable-host probe protocol](docs/safety/disposable-host.md)
- [Proposed upstream API if needed](docs/architecture/upstream-interface-proposal.md)
- [Project architecture blueprint](docs/architecture/blueprint.md)

## Project status

- [x] Name and dependency boundary agreed
- [x] Initial pure domain/planner and synthetic specs authored
- [x] Experimental host-probe source authored
- [x] Disposable Portainer example and digest-aware, loopback-only configuration lint (synthetic checks, not host isolation)
- [x] .NET 8 pure-core compile and 29/29 synthetic specs passed in [GitHub Actions](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45018712104)
- [x] Test-only adapter compiled against checksum-pinned Lidarr 3.1.6.5079 Linux binaries ([compile-only CI](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45019459665))
- [x] Disposable Lidarr v3.1.6.5079 host selected the test-only importer and intercepted a synthetic call ([CI evidence](docs/compatibility/disposable-runtime-evidence.md))
- [x] Isolated baseline, missing opt-in, and positive synthetic-interception tests ([CI #45020330835](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45020330835))
- [x] Lidarr-native 12-track characterization ([3/3 NUnit tests](docs/compatibility/native-12-track-characterization.md)): stock `replaceExisting=true` pre-deletes even the better track; `false` skips only album-wide pre-wipe but still calls `SetMonitored`
- [x] Native fault injection ([6/6 NUnit tests total](docs/compatibility/native-upgrade-failure-characterization.md)): original recycle and DB deletion are invoked before a failing replacement move **or copy**; real files and rollback remain untested
- [x] Native lyrics and download-completion characterization ([16/16 NUnit tests total](docs/compatibility/native-lyrics-completion-characterization.md)): linked `.lrc` is recycled during upgrade; lyric-only staging directory passes cleanup check; intentional 11+1 upgrade is failed without qualifying history. **No physical file handling or recovery was tested.**
- [ ] Real import caller, no-improvement and partial-upgrade settlement, clean unload/fallback, unsupported-version and multi-plugin behavior
- [ ] Supported pre-import contract with truthful zero/partial/full outcomes
- [ ] Verified `.lrc` preservation, edition lock and recoverable SMB-safe upgrades
- [ ] Build a real installable plugin

GitHub development repository: https://github.com/GQuX/lidarr-plugin-crescendarr. Licensed under [0BSD](LICENSE): reuse, modification, and redistribution are permitted without attribution requirements.

## Portainer lab deployment (v0.1.1)

Use **a new Portainer stack**, not the production multi-service stack. The lab Compose includes an explicit Watchtower exclusion and a fresh `/config` volume. See `docs/portainer/lidarr-stable-lab.md`. Never install the probe in production.

## Stable-target decision (2026-11-22, v0.2.0)

Lidarr stable v3.1.6.5079 has plugin loading in its tagged source and targets net8.0.
This source now targets .NET 8. Production Lidarr is a **compatibility reference only**.
The operator confirmed an independent backup and Watchtower exclusion; **no probe may be installed**
on that live container. The pure .NET 8 planner compiled and passed 29/29 synthetic specifications in GitHub Actions. The test-only host probe compiled and loaded in a clean disposable Lidarr runtime; **real tracked download processing and all file/queue/lyrics/recovery safety remain untested**.
Full input snapshot hashing is now included in merge plan identifiers, but these fingerprints
are advisory: only a host-owned revalidation and recoverable import executor can authorize writes.

## Upstream overlap

[Lidarr PR #6166](https://github.com/Lidarr/Lidarr/pull/6166) (open and unmerged when checked on 2026-11-22) proposes song selection, selective imports, and queue-aware recording intent. That work may supply a safer host-owned seam or make some Crescendarr functionality redundant. We are tracking its changes before considering any upstream submission. The PR does **not** on its own verify our automatic album-download upgrade rules, edition-lock guarantees, `.lrc` handling, crash recovery, or SMB-safe replacement semantics. See [upstream overlap notes](docs/architecture/upstream-overlap-pr6166.md).

**Host ABI check:** `.github/workflows/probe-abi.yml` downloads the pinned `v3.1.6.5079` Linux x64 release, checks SHA-256, discovers `Lidarr.Core.dll` and `Lidarr.Common.dll`, and compiles the non-installable adapter. It never executes Lidarr or installs the probe. A green result is **not** permission to test on a real library.

## Disposable runtime milestone (2026-11-22)

[GitHub Actions #45020067092](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45020067092) started stock Lidarr v3.1.6.5079 with a new empty database and our guarded probe, confirmed DryIoc chose the Crescendarr service, resolved Lidarr's concrete original without recursion, and intercepted a synthetic method call made directly by the plugin's own startup handler, not by Lidarr's real download pipeline. **No real download-client job, import, replacement, .lrc sidecar, or crash recovery was tested.** [Evidence and limitations](docs/compatibility/disposable-runtime-evidence.md).
