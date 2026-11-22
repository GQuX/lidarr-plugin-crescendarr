# Native import-history and missing-path scan characterization

**Date:** 2026-11-22. **Lidarr source:** v3.1.6.5079 pinned to commit da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd.
**Executable evidence:** [GitHub Actions #45029118148](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45029118148): **21/21 native NUnit tests passed** (previous 16 plus 5 new).
**Status:** native code with mocked external effects; no real files, SQLite rows, downloader or production NAS were changed.

## Imported-history predicate: three new tests

The fixture [CrescendarrHistoryPredicateCharacterizationFixture.cs](../../tests/LidarrNative/CrescendarrHistoryPredicateCharacterizationFixture.cs) invokes the actual stock TrackedDownloadAlreadyImported.IsImported, not an ITrackedDownloadAlreadyImported mock:

1. **One TrackFileImported history row for an album with 12 expected tracks -> true.** This does not verify 12 track identities. A 1-of-12 partial import can therefore satisfy the history predicate if the event is the most recent per-album event.
2. A more recent AlbumImportIncomplete event followed in time by an older TrackFileImported row -> false (caller supplies newest-first history).
3. Empty history -> false.

Relevant [predicate source](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/Download/TrackedDownloads/TrackedDownloadAlreadyImported.cs#L25-L57).

**Evidence correction:** The earlier completion fixture that reported 11 improved + 1 intentionally kept as ImportFailed supplied a **mocked false history result**. The actual importer publishes TrackImportedEvent after AddMany/SetFileIds; EntityHistoryService subscribes and records TrackFileImported, so an ordinary 11+1 download may settle as Imported. The exact integrated event/DB sequence still requires a non-mocked history integration test. Genuine partial failures may be incorrectly declared complete if successful per-track events have been stored. See [native lyrics and settlement document](native-lyrics-completion-characterization.md).

## MissingFromDisk and lyrics: two new tests

[CrescendarrRescanCleanupCharacterizationFixture.cs](../../tests/LidarrNative/CrescendarrRescanCleanupCharacterizationFixture.cs) invokes actual MediaFileTableCleanupService.Clean with an existing TrackFile path absent from synthetic scanned inventory. It verifies:

- IMediaFileService.DeleteMany is called with that TrackFile and DeleteMediaFileReason.MissingFromDisk.
- Its linked Track has TrackFileId reset to zero and is submitted to ITrackService.SetFileIds.

[CrescendarrLyricSidecarCharacterizationFixture.cs](../../tests/LidarrNative/CrescendarrLyricSidecarCharacterizationFixture.cs) now also invokes the actual LyricFileService.Handle with TrackFileDeletedEvent(..., MissingFromDisk), verifying the registered .lrc is recycled through IRecycleBinProvider and its extra-file repository record removed, in that order.

Together with stock [MediaFileService.DeleteMany event emission](https://github.com/Lidarr/Lidarr/blob/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd/src/NzbDrone.Core/MediaFiles/MediaFileService.cs#L89-L97), these classes form a plausible real rescan cascade: old audio moved to a recovery path -> scan finds original path missing -> track/row unlinked -> old registered lyric deleted/recycled. **We have not executed the chain end-to-end**, nor physically deleted a lyric.

## Engineering implication

**Scan exclusion is a precondition** for any design that renames an original aside before candidate publication. A startup reconciliation command queued after a rescan is not a proven solution. A host-owned per-album scan skip or synchronous reconciliation before scan dispatch must be designed and tested before a write-enabled executor.

Current [incremental architecture](../architecture/host-selective-import-incremental-v0.2.md) explicitly covers this dependency, careful SMB non-atomicity and truthful per-track settlement.

Remaining release blockers: live synthetic bytes + SQLite tests, history event ordering and coverage without mocks, protected .lrc copying/relinking, per-file staging/rollback/replay, release identity, multi-track files, and plugin absence/ABI fallback. **Do not install experimental Crescendarr on production Lidarr.**
