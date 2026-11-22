# M14: stock DiskScanService after the sixth-file failure and before recovery

**Status: DRAFT. First scanner-only case **PASSED** on Linux native CI [45058139237](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45058139237): 58/58 native tests plus both separate-process stages. New real-event-during-import variant is NOT YET VERIFIED.** [Issue #38](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/38). Source pinned to unmodified Lidarr v3.1.6.5079 (`da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`). No live host, audio library, NAS/SMB or download client.

## Safety question

M12 reproduced a precise post-recycle, post-TrackFile-delete sixth incoming-transfer failure against stock `TrackFileMovingService`, `RecycleBinProvider`, `DiskTransferService` (except a single injected `IOException`) and migrated SQLite. Track six remains linked to its *deleted* old TrackFile ID while the original audio destination is missing. Can unmodified Lidarr's scanner run before a plugin can recover, and what database/file/lyric changes does it make?

## Initial runnable characterization (this PR)

In `CrescendarrSqliteSyntheticMediaImportFixture.native_scan_before_recovering_sixth_transfer_failure_preserves_recovery_evidence`:

1. Build the existing 12-track stock SQLite fixture, 11 native approved upgrades, one superior FLAC rejected; write independent original backups and preserve physical `.lrc` files.
2. Inject the failure in the **actual sixth incoming `IDiskTransferService.TransferFile` boundary**, after stock recycle and old TrackFile row deletion.
3. **Before any restore**, invoke the actual `DiskScanService.Scan` with the real disk provider, real `MediaFileTableCleanupService`, migrated native media/track services, and stock `EventAggregator` configured with stock synchronous track/lyric deletion handlers.
4. Observe the exact Track, TrackFile, LyricFile, AlbumRelease, staging and physical `.lrc` state. The current test expects a scan with no new import decisions to leave the orphaned sixth row/reference and verification evidence untouched. The initial scanner-only variant was observed to leave these state elements unchanged in native CI. Unexpected changes in the second variant must fail the test rather than being declared safe.
5. Continue the already characterized **test-only** recovery: restore verified sixth audio with no overwrite, insert a fresh TrackFile ID, relink the track and reinsert the missing lyric association. Assert all 12 links and physical lyric bytes; protect the superior FLAC and monitored release.

## Follow-up variant: real stock deletion-event fan-out BEFORE the failure (pending CI)

The initial M12 sixth-transfer fault uses a mocked `IEventAggregator`, leaving a nonzero dangling `Track.TrackFileId` after the old TrackFile row is deleted. That may not match a running stock Lidarr instance: the real synchronous `TrackService.Handle(TrackFileDeletedEvent)` calls `TrackRepository.DetachTrackFile`.

The second test, `native_delete_event_fanout_before_scan_then_recovers_sixth_transfer_failure`, registers the real `EventAggregator` and stock `TrackService` / `LyricFileService` handlers *before* the native importer begins. It performs the identical sixth physical transfer failure, asserts the observed deletion-handler result (hypothesis: sixth `TrackFileId` becomes zero, rather than a dangling nonzero ID), then invokes the same real stock scan with empty decisions before recovery. The repair still uses a fresh TrackFile ID and must preserve the protected superior track, selected release, staging and every `.lrc` byte.

This new case is **not yet CI-verified**. If it fails, capture the actual event order and data changes rather than relaxing the safety requirement. These variants intentionally compare the mocked-event M12 failure with the real synchronous event-dispatch path, which is a fundamental difference for automatic recovery.

### Critical boundary and what this DOES NOT prove

This first increment **does not run native audio tag identification or full scan import decision-making**: the fixture's MP3/FLAC-named payloads are intentionally *opaque, non-playable* bytes. Its `IMakeImportDecision.GetImportDecisions` seam returns an empty set, allowing the **real stock scanner/cleanup/file enumeration/event plumbing** to run without fabricated media identification. `ImportApprovedTracks` itself is not replaced, but sees no decisions during scan.

Consequently, even green CI establishes only how the stock scan/cleanup path treats this already-orphaned SQLite state under an **empty-decision** scan. It does **not** establish what a normal populated rescan will do when tag readers identify files, invoke native importing, rename or retag files, or observe concurrent scan events. The real event handlers are registered, but this case may publish **no TrackFileDeletedEvent**, because the sixth TrackFile DB row was already deleted by the failed importer. Existing separate M12 event-dispatch tests prove handler behavior on a deletion that actually publishes an event. Neither evidence source is a full server boot.

Follow-up under #38 must exercise actual discovered decisions with valid tagged synthetic audio or explain why that cannot be done. It must separately test a scanner with real event fan-out and a post-orphan perturbation, including interleaved rescans. A green empty-decision test must **not** close #38 or lift the production write-enabled NO-GO.

Everything here is disposable runner-local testing. The test does not exercise power loss, SMB/CIFS semantics, a real Lidarr daemon restart, or a working plugin.
