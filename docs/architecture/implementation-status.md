# Crescendarr engineering checkpoint (2026-11-22, post-independent review)

**Production readiness: NO-GO.** Source-only project. No installable or write-enabled Crescendarr release.

## Verified on source or executable CI
- Pure .NET 8 planner: **29/29** fake-profile synthetic specifications passed. This is not native Lidarr quality-profile or filesystem safety evidence.
- Disposable Compose configuration: original **16/16** synthetic tests passed; extra digest/loopback tests are under review in the current PR. Static source-pattern scan is **advisory, not a security audit**.
- Official, checksum-pinned Lidarr 3.1.6.5079 Linux binaries: experimental adapter compiled.
- Ephemeral Lidarr host: test-only plugin selected by DryIoc; plugin's own startup handler invoked its synthetic, non-delegating `ProcessPath`.
- Independent ephemeral starts: stock host without plugin, negative guard (missing opt-in) and positive synthetic interception all passed.
- GitHub PRs #1, #4, #5 merged. No production systems accessed.

## Source-verified critical blockers
- `ImportApprovedTracks.Import(decisions, true,...)` does album-wide deletion, even when quality rules reject an incoming individual track.
- `UpgradeMediaFileService` deletes/recycles an old file before moving the replacement and rows are added after the loop.
- `ExtraFileService` removes registered extras on track-file upgrade; actual `.lrc` registration and handling remain unverified.
- Lidarr can retry without plugins after some startup failures, creating a fail-open risk.
- `IDownloadedTracksImportService` misses manual-import paths and cannot control `replaceExisting` or host completion semantics.
- The first interception experiment was a direct plugin-generated call, **not** actual tracked-download orchestration.

## Revised architecture
- **Do not** ship a write-enabled `IDownloadedTracksImportService` wrapper.
- First characterize upstream Lidarr's current behavior in tests and pursue a small supported core-owned selective-import and settlement contract, coordinated with still-open PR #6166.
- Treat any `IImportApprovedTracks` experimental decorator as unapproved until tested and until fallback/file/sidecar risks have an acceptable solution.
- Keep Crescendarr's pure planner, adapt native track identities and real quality models only after core import semantics are understood.

## Active blockers
- [#6](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6) album-wide delete, per-track behavior, failure-injection characterization
- [#7](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7) truthful queue settlement, lyrics, SMB/CIFS recovery
- [#3](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/3) upstream PR #6166 overlap
- [#8](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/8) tooling evidence quality
- [Independent-review triage](../reviews/2026-11-22-independent-review-triage.md)

**Operator boundary:** Production Lidarr, Tubifarry, NAS, SABnzbd, MusicBrainz edition and `.lrc` files remain untouched. Do not deploy any test probe on production.
