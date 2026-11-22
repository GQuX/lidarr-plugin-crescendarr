# Independent review of ADR-024 v0.1: triage and corrected design direction

**Status:** independent review received 2026-11-22, source findings checked against Lidarr `v3.1.6.5079` (commit `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`). This is a technical triage, **not** proof of a safe implementation.

The independent reviewer recommended replacing the overlarge proposed transaction manager with **small native Lidarr changes, one at a time**, and evaluating a purely additive `IImportDecisionEngineSpecification` plugin after baseline host behavior is made safe.

## Corrections confirmed by source inspection

**E1: History is under-constrained, not reliably ImportFailed after 11+1.** Our [native completion fixture](../../tests/LidarrNative/CrescendarrCompletedDownloadCharacterizationFixture.cs) explicitly mocked `ITrackedDownloadAlreadyImported.IsImported=false` for the failing mixed case and `true` for the completing case. The fixture establishes both conditional branches **but not what a real import history would contain**. Stock `ImportApprovedTracks` publishes successful `TrackImportedEvent` events after database insertion and before returning ([lines 317-326](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs#L317-L326)); `EntityHistoryService` handles those events and records `TrackFileImported` per track. Stock [`TrackedDownloadAlreadyImported.IsImported`](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/Download/TrackedDownloads/TrackedDownloadAlreadyImported.cs#L25-L57) only checks the **latest history event type per album**, not all expected track IDs. Thus 11+1 can settle as Imported after at least one successful track event, and an 11+1 combination with a *genuine* failed import can be incorrectly treated as complete. Exact live event ordering and history write reliability still require integration tests.

**E2: Failure in a single track is often caught.** Stock `ImportApprovedTracks` catches exceptions per track ([lines 282-311](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs#L282-L311)), then adds successful rows in a batch and updates track links. The earlier claim that all successfully moved files always lack DB rows after one later failed move was **overstated**. A process crash or failure during batched DB insert/link update is different and still unsafe.

**E3: A rescan can trigger lyric cleanup.** [`MediaFileTableCleanupService.Clean`](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/MediaFiles/MediaFileTableCleanupService.cs#L29-L47) calls `DeleteMany(..., MissingFromDisk)` for database paths absent from the scanned inventory. [`MediaFileService.DeleteMany`](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/MediaFiles/MediaFileService.cs#L89-L97) publishes `TrackFileDeletedEvent` for mapped files. [`ExtraFileService.Handle`](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/Extras/Files/ExtraFileService.cs#L103-L129) recycles/deletes linked extra files for **every reason except NoLinkedEpisodes**, including MissingFromDisk. If original audio is temporarily renamed aside when a scan runs, the DB row and registered lyrics can be removed. New native verification tests are tracked in [PR #14](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/14).

**E4: Existing command serialization is partial.** [`CommandQueue` lines 169-174](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/Messaging/Commands/CommandQueue.cs#L169-L174) avoid concurrently dispatching disk-access commands. This is an existing primitive, not proof of exclusion for API deletes, background event handlers, external NAS writers or recovery startup ordering. Reuse it for command-driven imports and explicitly protect journaled albums from cleanup regardless of dispatch order.

## Design review decisions

| Finding | Decision |
|---|---|
| v0.1 required host changes but described a monolithic coordinator | **Accept.** Split upstream work into small characterized patches and treat an optional policy plugin as a later addon |
| Scan-vs-recovery race | **Accept, release blocking.** Before renaming originals aside, host scan/cleanup must skip journal-open albums or startup reconciliation must run before any scan/queue dispatch; test both |
| SMB hashes/durability guarantees | **Accept.** No promise of durable-at-commit; read-back may hit client caches. Verification after reconnect/restart still cannot prove NAS power-loss durability |
| Extra copy of originals before same-share rename | **Conditionally accept.** A same-storage rename-aside can reduce I/O but is not an independent copy and is susceptible to storage loss or external writes. Whether one or multiple backups are required is a configurable/recovery-policy decision to test, not a universally safe shortcut |
| Tagging after publication | **Accept.** All planned tag edits must run while candidate is staged; verify after final mutation, not before |
| Journal location and DB atomicity | **Accept.** If journal is needed, host-owned DB on local persistent config is preferred. SQLite and SMB operations are not one transaction; reconciliation must be idempotent and may hold for review rather than always redo or rollback |
| Existing command lock | **Accept, qualified.** Use existing `RequiresDiskAccess` serialization and cover API/external processes/startup scans outside it |
| Album edition switch | **Accept.** Same-monitored-release is a prerequisite for a selective upgrade; a different release stays blocked/requires explicit handling, never silently retained with orphaned links |
| Multi-track shared audio | **Accept.** Guard all old physical file associations before deleting even in the per-track upgrader |
| Typed keep vs imported | **Accept.** Stock latest-event history may incorrectly settle genuine partial failures, and zero-improvement needs explicit kept-satisfied state |
| Additive plugin specification registration | **Investigate only.** No proof yet that a plugin specification is present in the decision-maker's `IEnumerable` collection in a real host |
| Incremental fix makes nothing worse than stock | **Not established.** Each step needs explicit negative tests and regressions, especially release-switch and rescans. A smaller patch is safer to review but is not automatically safe |
| "Rollback is never the plan" | **Overstated.** Prefer deterministic reconciliation and retain originals. A verified restore can be appropriate before a committed transaction; don't promise an automatic rollback where DB/storage diverge |

## Revised priorities

1. **P0, native red/green test:** Same monitored release: approved 11 upgrades never pre-delete the 12th better original; different release must not silently orphan its existing track files. Test exact source-pinned Lidarr implementation.
2. **P0, safe publication experiment:** Stage and apply tag modifications first, verify candidate, rename original aside, promote candidate without deleting original; **scan protection or pre-scan reconciliation must be designed/tested before production or kill-point testing**.
3. **P0, scan/lyric race:** Trigger scan with old filename temporarily absent and prove MissingFromDisk + `.lrc` deletion; add protection in host, test before/after process kill.
4. **P0, truthful settlement:** Add a native history-backed test of 0 kept, 11+1 kept, and 11+1 *genuinely failed*, without mocking `ITrackedDownloadAlreadyImported`. A typed ExistingFileIsPreferred rejection may count toward satisfied, but errors must not.
5. **P1, physical/DB fidelity:** Use real synthetic files and disposable local SQLite, then staged file-replacement fault injection with real process kills and startup rechecks. Only after local recovery works should a dedicated synthetic SMB share be considered.
6. **P1, plugin discovery:** Test an additive `IImportDecisionEngineSpecification<LocalTrack>` registration in a fresh disposable host; do not override outer/import approval services.

## Product reality

The aspiration remains one **Crescendarr plugin** with no daemon, no Tubifarry/LRCGET dependency and no direct SABnzbd API. The guarantees in this review require **new Lidarr core behavior**, so on unmodified stable Lidarr there is no production-safe plugin architecture today. The realistic route is small Lidarr fixes plus an optional one-package policy plugin, subject to a supported extension path. No write-enabled release, upstream outreach or production changes are authorized by this document.

**Previous ADR-024 v0.1 is superseded as an implementation target.** See [incremental design v0.2](../architecture/host-selective-import-incremental-v0.2.md).
