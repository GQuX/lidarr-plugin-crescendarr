# Real native event dispatch with migrated SQLite and physical lyrics

**Date:** 2026-11-22. **Pinned source:** stock Lidarr 3.1.6.5079, commit da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd. **CI:** [run 45041825078](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45041825078), 40/40 native tests and both separate-process recovery tests passed.

## Experiment

Two new variants extend the native SQLite + synthetic physical .lrc fixture in [PR #29](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/29). Unlike the previous direct-handler tests, these variants use the **actual stock EventAggregator**. The test explicitly registers **real stock TrackService and LyricFileService** as synchronous TrackFileDeletedEvent subscribers with Lidarr's IServiceFactory, then invokes **stock MediaFileTableCleanupService.Clean**. That calls real MediaFileService.DeleteMany, publishes the deletion event, and lets the unmodified EventAggregator execute both native handlers. No explicit Handle invocation occurs in these variants.

Against real migrated disposable SQLite, both cases detach the missing file from its track (TrackFileId=0). When the old LyricFile row remains registered, the native event handler recycles the physical lyric into the test recycle folder and removes its SQLite association. When the LyricFile DB row was explicitly deleted first, its adjacent physical .lrc survives. Test-controlled disk callbacks perform the ephemeral recycle move.

## Boundaries and next steps

- Only the two native subscribers are explicitly wired. This does not test the full host dependency injection graph, other third-party subscribers, a full DiskScanService.Scan, startup order or concurrent writes.
- No real stock RecycleBinProvider or TrackFileMovingService in this test, no valid tagged audio and no SMB/NAS.
- The end-to-end cross-process SQLite recovery still uses a test-authored restoration procedure, not a plugin journal or actual Lidarr daemon restart.
- Next: native local filesystem transfer/recycle testing, full host rescan and event fan-out, real tag/quality identification, and journal-backed idempotent recovery.

**Production release remains NO-GO.** No user production system was modified.