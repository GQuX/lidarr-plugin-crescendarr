# Crescendarr

**Selective, quality-aware track merging for Lidarr.**

> **DEVELOPMENT SOURCE ONLY. NOT INSTALLABLE. NOT SAFE FOR PRODUCTION.**
> The plugin integration seam, import lifecycle, recovery and `.lrc` preservation have **not** been proven in a running Lidarr host.

## What we are building

A single Lidarr plugin that plans missing-track imports and strictly preferred individual upgrades without replacing entire album releases. It will preserve the monitored MusicBrainz edition, unrelated/better originals, and both existing and staged `.lrc` lyrics. It will not depend on Tubifarry, LRCGET, SABnzbd APIs, a daemon, or any other application besides compatible Lidarr. SABnzbd remains managed by Lidarr.

The current source is **only**:

- `src/Crescendarr.Core/`: pure deterministic merge planner. No Lidarr references, no disk/database/network access, and no file-write operations.
- `tests/Crescendarr.Core.Spec/`: standalone executable specifications using synthetic identities and a **fake** quality policy. They do not validate Lidarr's real quality profiles.
- `src/Crescendarr.Lidarr.Probe/`: deliberately non-delegating, test-only registration experiment. It has **no installer, no release workflow** and must not be put into production Lidarr.
- `deploy/portainer/disposable-lidarr-stable.compose.yml`: no-production-mount disposable host example (separate `/config`, port 8687); this does **not** install the probe.
- `tools/check_disposable_compose.py` and `tools/tests/`: development-only Compose lint plus 13 regression checks for unsafe configuration.
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

No NuGet test framework dependency is needed. **These tests have not been run in the authoring environment**, because no .NET SDK is installed there. Do not treat the checked-in expected assertions as verified passing tests until your CI/SDK actually runs them.

## The critical host experiment

Read [disposable host protocol](docs/safety/disposable-host.md). The test-only probe requires exact disposable-host opt-in, a sentinel file and an explicit synthetic-root allowlist. It intentionally returns empty import decisions, which may cause *Lidarr's own* queue/import state to change. Use it only in an isolated host with a fresh disposable DB and no real downloads.

The source-pinned Lidarr import interface at commit `a754943cbd85cf9257e984506e0143ede18135fe` is not an endorsed plugin interception API. We must prove type selection, startup, original concrete resolution and unload before designing a write-enabled plugin. A DI wrapper alone would **not** solve completion/edition/lyrics safety.

## Documentation

- [Host contract and uncertain integration seam](docs/compatibility/host-contract.md)
- [Disposable-host probe protocol](docs/safety/disposable-host.md)
- [Proposed upstream API if needed](docs/architecture/upstream-interface-proposal.md)
- [Project architecture blueprint](docs/architecture/blueprint.md)

## Project status

- [x] Name and dependency boundary agreed
- [x] Initial pure domain/planner and synthetic specs authored
- [x] Experimental host-probe source authored
- [x] Disposable Portainer example, lint and 16 Python safety regression tests (passed in authoring environment)
- [ ] .NET 8 compile and spec run
- [ ] Disposable Lidarr host and DI comparison
- [ ] Supported pre-import contract with truthful zero/partial/full outcomes
- [ ] Verified `.lrc` preservation, edition lock and recoverable SMB-safe upgrades
- [ ] Build a real installable plugin

GitHub development repository: https://github.com/GQuX/lidarr-plugin-crescendarr. No license has been selected yet; do not assume open-source reuse rights until a license is added.

## Portainer lab deployment (v0.1.1)

Use **a new Portainer stack**, not the production multi-service stack. The lab Compose includes an explicit Watchtower exclusion and a fresh `/config` volume. See `docs/portainer/lidarr-stable-lab.md`. Never install the probe in production.

## Stable-target decision (2026-11-22, v0.2.0)

Lidarr stable v3.1.6.5079 has plugin loading in its tagged source and targets net8.0.
This source now targets .NET 8. Production Lidarr is a **compatibility reference only**.
The operator confirmed an independent backup and Watchtower exclusion; **no probe may be installed**
on that live container. Current source checks are static only; the C# code has not been compiled.
Full input snapshot hashing is now included in merge plan identifiers, but these fingerprints
are advisory: only a host-owned revalidation and recoverable import executor can authorize writes.
