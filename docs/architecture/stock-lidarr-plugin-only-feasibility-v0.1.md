# Stock-Lidarr-only integration feasibility (constraint adopted 2026-11-22)

**Status:** feasibility study, **not** proof of a safe or complete plugin. **Hard requirement:** no Lidarr source patches, forks, custom binaries, modified upstream releases, companion daemon or separately managed downloader. One self-contained plugin at most. User has *not* approved changing production settings or enabling writes.

**Pinned stock host for characterization:** LinuxServer Lidarr stable v3.1.6.5079, .NET 8. Its supported plugin ABI is not guaranteed across releases.

## What the current source does

1. [DownloadedTracksImportService](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs#L223-L245) always invokes native IImportApprovedTracks with replaceExisting **true** for automatic downloaded album imports. Stock ImportApprovedTracks then pre-deletes all album files, even a better one it decided not to import. A plugin that merely adds import specifications cannot change this.
2. [UpgradeMediaFileService](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/UpgradeMediaFileService.cs#L57-L87) deletes/recycles a target original and its TrackFile association before moving/copying the replacement. Lyrics linked to the old record can be removed by ExtraFileService. Disabling album-wide deletion alone is insufficient.
3. [DownloadProcessingService.Execute](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/DownloadProcessingService.cs#L47-L75) gates stock ICompletedDownloadService.Import on IConfigService.EnableCompletedDownloadHandling. When this setting remains **false**, stock automatic import does not execute on pending items. However RemoveCompletedDownloads is still called and can publish DownloadCanBeRemovedEvent for previously Imported removable items.
4. [DownloadMonitoringService.Refresh](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/TrackedDownloads/DownloadMonitoringService.cs#L60-L80) continues to query Lidarr-configured download clients and updates ITrackedDownloadService. [DownloadIsTrackable](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/TrackedDownloads/DownloadMonitoringService.cs#L136-L152) filters complete jobs from the normal tracked UI when CDH is off. A plugin might inspect the internal tracked cache, call configured IDownloadClientFactory providers, or handle a host event, but these methods and their usability are **not yet proven** in a plugin.
5. Manual/interactive imports directly call [IImportApprovedTracks](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/Manual/ManualImportService.cs#L418-L423). They are **not** prevented by disabling Completed Download Handling. Disk scanning uses IImportApprovedTracks with replaceExisting=false, but cleanup of MissingFromDisk files can still remove linked lyrics.
6. [Bootstrap](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Host/Bootstrap.cs#L131-L141) can fall back to a host without plugins after plugin-enabled startup failure. CDH persistently OFF could prevent **automatic** stock replacement under that fallback, but only if config remains disabled and the exact failure behavior is verified. A plugin cannot enforce manual-import behavior when it is absent.

## Plugin-only options compared

| Path | Stock Lidarr compatibility | Safety blocker | Decision |
|---|---|---|---|
| Additive import decision specification only | Possible if plugin types join ImportDecisionMaker's enumeration | Cannot change automatic replaceExisting=true, stock side effects still destructive | **Insufficient by itself**; retain as optional safety policy probe |
| Intercept IImportApprovedTracks and pass replaceExisting=false | DI selection plausible; reaches auto, manual and rescan calls | Core per-file delete-before-move, lyrics, SetMonitored, startup fail-open | **Test-only candidate** with upfront backup + guarded validation, not safe today |
| Intercept outer IDownloadedTracksImportService | Already probed under one pinned host | Must reimplement private ProcessFolder and misses manual imports; stock fallback | **NO-GO** as automatic write strategy |
| Native CDH **persistently off**; single plugin manages completed queue internally | Stock config option, and download client APIs remain queryable | Finished items hidden from normal queue, plugin owns scheduling/status/retry/settlement and must preserve file/lyric DB correctness | **Preferred feasibility experiment**, not a selected production design |
| Entire custom plugin FS/SQLite importer | Does not patch Lidarr binaries | Unsupported DB mutability, events, crashes, rescans and lyrics, high maintenance | **NO-GO** without a convincing testable model |

## Candidate single-plugin lifecycle (hypothesis)

**1. Installation / operator gate:** user explicitly opts into Crescendarr-managed downloads and manually sets Lidarr's native Completed Download Handling to OFF. The plugin must **never briefly turn it ON** and must refuse to apply if the setting is not OFF. Need to test that stock CDH OFF is persisted and the host does not override it on restart. No production change during this experiment.

**2. Observe:** an in-process plugin component obtains completed downloads from Lidarr's configured IDownloadClientFactory / ITrackedDownloadService, or from a proven event hook. No direct SABnzbd HTTP API, no separately running app. Must not drop jobs merely because stock queue hides them, and must dedupe against existing imported history.

**3. Plan:** compare exact native release and Track IDs, inspect quality profile, reject equal/ambiguous/multi-track and edition-switch cases. Freeze file inventories, including registered and adjacent .lrc. Defaults to read-only and NeedsReview.

**4. Prepare recovery:** stage candidates and create verified, operation-owned backups of existing audio and lyrics. On a missing NAS mount or conflicting paths, do nothing. Do not depend solely on Lidarr's optional recycle bin.

**5. Controlled apply:** investigate whether a guarded IImportApprovedTracks wrapper or official manual-import interface with replaceExisting=false can use native DB/event machinery without album pre-wipe. The stock upgrader still removes originals before transfer, so every target must have a verified recoverable original. Unload/crash and manual-import bypass must be tested; if a crash leaves unsatisfied records, held backups and a deterministic reconciliation process are mandatory.

**6. Settle:** plugin-owned queue state must represent NoImprovement / AppliedPartial / NeedsReview / RecoveryRequired truthfully. Avoid forging TrackFileImported events. Cleanup only operation-owned download staging after audio and all .lrc are verified; use Lidarr's configured download client abstraction for any removal where supported and explicitly opted in.

**7. Plugin missing:** native CDH remains OFF. No Crescendarr auto import occurs; completed jobs remain with the download client (subject to its own retention settings). Native manual import or other paths can still modify library; do not promise a comprehensive global safety guarantee.

## Critical tests before choosing this route

- [ ] Native unit test: CDH OFF skips automatic pending imports; ON invokes them; OFF can still emit cleanup for already imported removable jobs (PR #17).
- [ ] Disposable host: with CDH OFF, prove a plugin can discover completed SAB-style jobs via Lidarr's configured client/factory **without** connection to production SAB.
- [ ] Disposable host: plugin unload/startup failure with CDH persistently off; prove no automatic import and no native removal/cleanup for an unimported completed synthetic job.
- [ ] Verify native queue UI loses completed items; decide what plugin status/reporting will look like.
- [ ] Call a guarded importer with replaceExisting=false in a synthetic host; prove no album-wide deletion, no edition switch, and preservation of a better FLAC and its lyrics.
- [ ] Real local synthetic files + disposable Lidarr DB; first 11-of-12 and no improvement, then failure after original deletion and restart recovery.
- [ ] Simulate manual import when plugin absent and document protected/unprotected scope; check future stock version compatibility.
- [ ] Only after local and synthetic SMB tests pass, consider operator-facing opt-in. No live NAS tests are authorized.

## Honest delivery contract

The objective is one plugin that works on stock Lidarr, but **no full safety guarantee is technically demonstrated yet**. Stock manual imports, rescans and third-party changes remain outside an absent plugin's control. If the plugin-only route cannot meet original-track and lyric preservation with reliable restart recovery, ship read-only planning and deliberate review before any write-enabled release.

Tracked in [#16](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/16). The previous upstream-dependent [ADR v0.2](../architecture/host-selective-import-incremental-v0.2.md) is now **archived as research, not an implementation plan**, because the project owner has rejected modifying Lidarr.
