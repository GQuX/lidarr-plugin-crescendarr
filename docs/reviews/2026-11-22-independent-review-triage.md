# 2026-11-22 independent-review triage: critical architecture correction

**Status:** Reviewed against **Lidarr `v3.1.6.5079` tagged source**. This report summarizes an external code review supplied by the project owner and subsequent source checks. This is **not** a verified fix, an upstream patch, or permission to run any import on production Lidarr.

**Decision (ADR-023):** Do **not** develop `IDownloadedTracksImportService` as Crescendarr's write-enabled production wrapper. The currently merged probe remains useful **only** as a disposable-host DI characterization fixture. Prioritize upstream characterization and a minimal, supported host import-planning/settlement change. An `IImportApprovedTracks` decorator remains an **experimental alternative to measure**, not an approved stopgap for general users.

## Critical findings reproduced from source

| Finding | Stable Lidarr evidence | Consequence |
|---|---|---|
| Album-wide pre-deletion | [`ImportApprovedTracks.Import` lines 120-128](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs#L120-L128) calls `RemoveExistingTrackFiles(artist, album)` when `replaceExisting=true`, then updates monitored release | A better untouched track may be removed even when its incoming candidate was rejected |
| Default auto import requests replacement | [`DownloadedTracksImportService` line 223](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs#L223) calls `Import(decisions, true, ...)` | Core album-wide deletion is on the normal completed-download path |
| Existing quality logic already compares tracks | [`UpgradeSpecification` lines 27-64](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/Specifications/UpgradeSpecification.cs#L27-L64) rejects lower *quality*, and sometimes lower revision at equal quality | Some planner functionality is redundant; equal-quality sidegrades and custom-format preferences are not proved by this check |
| Destructive upgrade order | [`UpgradeMediaFileService` lines 57-86](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/UpgradeMediaFileService.cs#L57-L86) recycles/deletes old track and DB mapping **before** moving/copying replacement | The host cannot currently be treated as an automatically recoverable executor |
| Deferred new DB rows | [`ImportApprovedTracks` lines 315-320](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs#L315-L320) writes new TrackFile rows after the loop | Import interruption can leave partial disk/DB state; fault injection needed |
| Extra-file cleanup | [`ExtraFileService` lines 103-129](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Extras/Files/ExtraFileService.cs#L103-L129) recycles/deletes linked extra files when TrackFile is deleted, except reason `NoLinkedEpisodes` | Existing linked lyrics may be lost on upgrades; verify exactly how Lidarr registers `.lrc` |
| Unconfigured recycle bin | [`RecycleBinProvider` lines 69-85](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/RecycleBinProvider.cs#L69-L85) calls permanent `DeleteFile` if unset | Recycle bin is a mitigation, not a transaction or sidecar guarantee |
| Plugin startup failure fallback | [`Bootstrap` lines 131-141](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Host/Bootstrap.cs#L131-L141) retries without plugins after failed plugin-enabled host startup when unload succeeds | A plugin cannot guarantee it will always intercept stock imports, especially across ABI drift |
| Manual imports bypass outer wrapper | [`ManualImportService` lines 418-423](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/Manual/ManualImportService.cs#L418-L423) directly calls `IImportApprovedTracks.Import` | Current `IDownloadedTracksImportService` seam has incomplete import coverage |
| No truthful no-op/partial settlement API | [`CompletedDownloadService` lines 126-180](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/CompletedDownloadService.cs#L126-L180) and [190-245](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/CompletedDownloadService.cs#L190-L245) | Empty results remain pending, non-imported can mark failure, previous history may settle; needs test matrix |

### Unverified or conditional claims

- **`replaceExisting=false` fixes everything**: unsupported. It may avoid the *album-wide* deletion but `UpgradeTrackFile` still removes per-track originals before replacement; `SetMonitored` still runs for approved decisions. Characterize with host tests.
- **All `.lrc` are deleted**: not established. The event deletes *registered linked extras*; file registration, unlinked sidecars, staging cleanup and default config require targeted tests.
- **Any ABI mismatch always silently fails open**: conditional. The startup failure path retries without plugins when unload succeeds. Other failures can be swallowed or leave a partially running host, rather than the same fallback. Test exact failure modes.
- **A direct startup handler proves a real importer call was intercepted**: false. `ProbeStartupCheck` directly calls the selected service. Real `DownloadedAlbumsScan` and download tracking callers remain untested.
- **`IImportApprovedTracks` decorator is safe**: not demonstrated. It captures more callers, but stock `ImportApprovedTracks.Import` still invokes unsafe file replacement and edition switching. Failing closed within the plugin cannot cover *plugin absence* or fallback.
- **Planner fingerprints establish file integrity**: false. They hash caller-supplied metadata; no authoritative file hashes, paths, stat snapshots, native `Track.Id` mapping, or synchronized plan execution exist.
- **Quality logic equals configured Lidarr semantics**: not proved. Pure tests use fake quality tokens; real `QualityModelComparer`, revisions, custom formats, allowed qualities and cutoff must be characterized.
- **0BSD licensing guarantees all future plugin binaries may use 0BSD**: not a legal conclusion. The project license covers original code; distribution/linking with GPL-licensed Lidarr components needs separate compliance review, and no upstream copyrighted source should be copied into 0BSD files.

## Prioritized engineering path

1. **P0 [#6](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6):** write *Lidarr-native* characterization tests covering existing album wipe, `replaceExisting=false`, release changes, upgrade error handling and database/FS recovery. Use a disposable test fork and only synthetic data.
2. **P0 [#7](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7):** characterize `CompletedDownloadService` states, registered/staged lyrics behavior, multi-track files and SMB/CIFS crash recovery. Define truthful settlement first.
3. **P0 [#3](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/3):** coordinate with upstream [PR #6166](https://github.com/Lidarr/Lidarr/pull/6166) while it is open. Its selective replacement is conditional on target recording IDs / Song Mode, not a demonstrated general album upgrade fix.
4. **P1 [#8](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/8):** retire stale manifest, revise weak smoke-check claims, permit image digests in lab lint, and fix overstated test names/README.
5. Only after host tests and upstream feasibility review, run a **non-delegating test-only `IImportApprovedTracks` probe** in a disposable runtime as a comparison; do not ship it merely because DryIoc selects it.

## Explicit safety gates

**No write-enabled Crescendarr plugin** until supported interposition, no fail-open destructive path, target/edition identity locks, real quality-profile interpretation, sidecar guarantees, truthful queue state, recovery after interrupted disk/DB operations, concurrent import/rescan handling, and synthetic fault tests are established.

This document changes our architecture decision but **does not authorize** changing production Lidarr, NAS, SABnzbd or any upstream Lidarr repository. Any upstream collaboration should be coordinated with the project owner first.
