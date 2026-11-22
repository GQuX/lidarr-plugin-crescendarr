# Native SQLite scan cleanup and lyric-event effects

**Date:** 2026-11-22. **Status:** passed in source-pinned disposable CI; not a full Lidarr server scan. [PR #28](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/28), [native run #45040268264](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45040268264): **38/38 native tests + 1/1 restart prepare + 1/1 separate-process recovery**. **Pinned host:** Lidarr stable v3.1.6.5079 / `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`.

## Scenario

New `tests/LidarrNative/CrescendarrNativeSqliteScanLyricFixture.cs` has two NUnit cases against migrated disposable SQLite and physical synthetic `.lrc` bytes. Both create real stock Artist/Album/AlbumRelease/Track/TrackFile/LyricFile records. The original music path is temporarily moved to a verified separate backup folder, simulating a path missing during an interrupted replacement. Real stock `MediaFileTableCleanupService.Clean` is called with an empty file inventory; its native `MediaFileService` and `TrackService` use real SQLite repositories.

The event aggregator is **mocked**, as in the earlier M12 fixtures. To characterize native sidecar effects without claiming actual dispatcher coverage, the test explicitly invokes the real `LyricFileService.Handle(new TrackFileDeletedEvent(..., MissingFromDisk))`. The low-level recycler interface uses a test-controlled callback that genuinely moves ephemeral lyric bytes into a disposable recycle folder.

| Before cleanup | Actual native SQLite track state | Native lyric handler effect | Physical lyric |
|---|---|---|---|
| Lyric remains registered to missing TrackFile | Old TrackFile row deleted, Track.TrackFileId set to **0** by stock cleanup | Handler finds old lyric row and recycles it, then deletes the row | Original path absent; bytes recovered only from test recycle |
| Lyric DB-unlinked first via `LyricFileService.Delete(id)` | Old TrackFile row deleted, Track.TrackFileId set to **0** by stock cleanup | Handler sees no registered lyric to recycle | Adjacent `.lrc` bytes remain at original path |

Both paths preserve independently moved original audio bytes. Each fixture verifies the real SQLite state and physical lyrics after cleanup. These tests complement [cross-process recovery](native-cross-process-sqlite-recovery.md): they show why a real-recovery implementation must handle a **TrackFileId=0** detached track and protected lyrics, not only a stale orphaned TrackFileId observed with a mocked event aggregator.

## What is not proven

- Real `IEventAggregator.PublishEvent` dispatch and ordering are **not** run, only the native handler explicitly invoked.
- `DiskScanService.Scan` with true audio discovery, empty-directory guard, manual imports, file collisions, concurrent processes, and startup/event scheduling are not exercised.
- The stock `RecycleBinProvider`, native disk transfer implementation and actual SMB mount are **not** run. Test callbacks move only ephemeral bytes.
- No automatic operation journal, path-fingerprint validation, idempotent recovery after a daemon crash, quality-profile/edition revalidation or real tagged audio is implemented.
- The test compares a *pre-unlinked row* with a registered row. Unlinking only protects physical .lrc bytes from this particular cleanup path; it does not preserve lyric DB associations without later verified repair, and it cannot prevent other software from deleting or renaming files.

## Engineering implications

1. Before any candidate write, record immutable original Track/TrackFile/release IDs, paths and strong byte checksums of original audio **and** registered/unregistered/staged sidecars in an operation-owned journal.
2. A recovery reconciler must tolerate both a dangling old file ID and a **detached ID=0**, but only after re-reading the actual stock DB and disk. Never restore based only on a stale in-memory LocalTrack instance.
3. Explicitly test scan scheduling before audio relocation, between unlink and native delete, between delete and new file insert, and after a process crash. Unexpected changes require `RequiresReview` or `RecoveryRequired`, not blind replay.
4. Add real event fan-out and stock disk-mover tests on valid tagged disposable audio, then a separate synthetic SMB lab. Do not infer an atomic operation from these local tests.

**Shipping decision remains NO-GO.** No production Lidarr settings, server, downloader, NAS, music or lyrics were accessed.
