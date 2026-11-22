# M12: sixth-file failure at the native transfer boundary

**Date:** 2026-11-22. **Stock Lidarr source:** v3.1.6.5079 / `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **PR:** [#34](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/34). **CI:** [native run 45049365277](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45049365277): 46/46 passed, then separate-process prepare/recover 1/1 + 1/1 passed.

## Exact executed boundary

The source-pinned existing 12-track migrated-SQLite importer fixture now has a `failAtSix=true, useStockDiskPipeline=true` case. It runs actual native `ImportApprovedTracks.Import(replaceExisting:false)`, `UpgradeMediaFileService`, `TrackFileMovingService`, `RecycleBinProvider`, native `MediaFileService` / `TrackService` / `ReleaseService`, real SQLite, actual Linux disk provider, mount-provider and symlink resolver. The only synthetic boundary is a test-controlled **wrapper implementing IDiskTransferService**: when the source is the incoming sixth staged audio and destination is the original sixth library path, it throws `IOException`; every other file-transfer call delegates to **stock DiskTransferService**. The sixth old file was already moved by stock recycler and old TrackFile row deleted by native media service before the injected transfer exception.

Native importer continues and records exactly 10 Imported, 1 Skipped and 1 Rejected. Before test recovery the sixth library path is absent, incoming sixth source still staged, old byte-identical file is in native recycle, and the Track row still points at a deleted old TrackFileId (the **orphaned nonzero** failure mode). Physical .lrc bytes remain because the fixture separately DB-unlinked the original lyric row before starting. The test then copies the pre-verified independent original backup back without overwriting, adds a **fresh** native TrackFile row, calls native `ITrackService.SetFileIds`, and registers a new lyric row with Id=0. The final 12 tracks link to real TrackFile rows; physical lyrics, protected superior twelfth original, recycle copies and independent backups are verified.

## What is and is not proven

- This increment proves native delete-before-move and recovery invariants with the **actual stock mover and recycler** participating in the same SQLite importer test. Earlier sixth-file faults used a mocked IMoveTrackFiles and did not test that transfer boundary.
- Fault injection happens in a *test wrapper around* IDiskTransferService, not an unexpected real OS-level partial write, power loss, disk disconnection, CIFS mount drop, or process kill.
- Recovery is still **test-authored** and **in-process** for this new case; no deployed journal discovers the operation, writes durable checkpoints, or automatically reconciles. Earlier cross-process SQL snapshot tests remain separate and green.
- The incoming and existing files in this combined importer remain **opaque non-playable synthetic bytes**, and the actual audio tag reader, correct MusicBrainz edition identification and full host event subscriber graph are not exercised here.
- In the native fixture, the lyric DB association was deliberately removed **before** native delete; no full plugin-driven automatic sidecar preservation workflow or full DiskScanService.Scan is shown.

## Implementation implications

Do not call stock UpgradeMediaFileService until Crescendarr has a durable write-ahead intent and independently verified original audio + lyrics backup. If the old TrackFile row is deleted, never re-use the old file ID; fail closed until fresh SQLite and actual filesystem evidence agree. Under any edition mismatch (see [two-edition native characterization](native-two-edition-sqlite-import.md)), refuse to invoke native import at all. A separate [M13 recovery journal assignment](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/35) is being implemented in parallel with strict local disposable tests and independent review.

**Shipping status NO-GO**: tests on disposable GitHub runner, unmodified stock Lidarr, no production data, SMB or downloader was accessed.