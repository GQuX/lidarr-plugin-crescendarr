# M12: native selective importer, real SQLite and stock filesystem mover in one fixture

**Date:** 2026-11-22. **Lidarr pin:** stock stable v3.1.6.5079 / `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **PR:** [#31](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/31). **CI:** [run #45042718808](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45042718808), **43/43 native tests**, plus **1/1 separate-process checkpoint preparation and 1/1 recovery**, all passed. Pure planner CI passed.

## Combined experiment

The source-pinned `CrescendarrSqliteSyntheticMediaImportFixture` now includes a third scenario; the previous two cases remain unchanged for in-process failure injection and independent process-boundary recovery. The new case constructs 12 actual on-disk opaque synthetic music files, 12 staged candidates, 12 physical .lrc sidecars and real registered lyric rows in a disposable per-test folder. It uses **migrated local stock SQLite** with real artist/album/release/track/TrackFile records and a stock quality profile. Stock `UpgradeSpecification` approves exactly 11 replacements and rejects a candidate inferior to existing better track 12.

An independently written verified byte copy of every original is created. Eleven old lyric DB associations are removed through native `LyricFileService.Delete(id)` before import, without deleting physical .lrc bytes. The fixture then invokes stock `ImportApprovedTracks.Import(decisions, replaceExisting:false, importMode:Move)`, native `UpgradeMediaFileService`, native `MediaFileService`/`TrackService`/`ReleaseService`, **native `TrackFileMovingService`, native `RecycleBinProvider`, native `DiskTransferService`, actual platform disk service, actual `ProcMountProvider` and actual `SymbolicLinkResolver`**. This time, **no mocked mover or recycler callback is used in the 11-track success path**.

The test re-reads SQLite and actual file bytes and proves:

- Exactly **11 Imported** and **1 Rejected** based on native quality comparison; the physical new audio bytes replace only those eleven destinations and all eleven old audio files survive in their native recycle subfolders.
- The better twelfth original retains its original file bytes, TrackFileId and registered .lrc row; its inferior staged candidate remains untouched.
- New native TrackFile records are associated with the correct track rows, 12 tracks refer to actual TrackFile records, all 12 .lrc physical bytes survive, and eleven new native LyricFile rows can be inserted with fresh Id=0 to refer to the new TrackFile IDs.
- The single monitored album-release row remains monitored, and independent backups of all original audio bytes remain intact.

## What the green test does NOT prove

1. **The files named .mp3/.flac are not playable audio.** They hold opaque synthetic bytes. Real audio metadata reading, ID3/FLAC tag writing, checksum changes caused by tagging and authentic MusicBrainz track identification are still mocked/unexercised. A separate [actual stock local disk test](native-real-local-file-transfer.md) used valid PCM WAV; it was not the importer in this combined case.
2. Native mover/recycler, disk and SQLite ARE exercised together, but `IBuildFileNames`, root-folder selection, script-import policy, permissions/attribute changes and event aggregator remain test-controlled. A realistic full host DI container, competing background event handlers and full `DiskScanService.Scan` are not proven.
3. This is a **success** case only for native physical moves. The sixth-file interruption and cross-process recovery still use test-controlled transfer callbacks to inject failures. There is no automatic durable operation journal, real server kill/restart, full process crash or CIFS/SMB disconnection handling.
4. The inserted album has just **one monitored release**. The test checks monitoring stayed true but does not prove that native `SetMonitored` preserves a user's chosen edition when multiple MusicBrainz releases are present.
5. There is no completed-download queue inventory, actual SABnzbd item, real downloader cleanup, album settlement, user library or installable plugin. Manual import and startup races remain separate hazards.

## Next gates

1. Generate **valid tagged** MP3/FLAC fixtures (or supported authentic native audio format), exercise stock `IAudioTagService` and full `IMakeImportDecision` with explicit `IdentificationOverrides.AlbumRelease` against **two competing release IDs**.
2. Exercise actual event subscriber graph and real `DiskScanService.Scan` in the native 12-track path, including a sixth-file native transfer fault and a scan between DB unlink and recovery.
3. Implement and independently review a conservative per-operation journal and idempotent state reconciler; persist source/target identity and checksums before writes, reject collisions/edition changes and verify before queue settlement.
4. Build read-only CDH-OFF durable client inventory (#21). Only later consider a write-capable **disposable-host** plugin prototype with explicit user approval for any production settings.

**Shipping status NO-GO.** All tests run only against pinned unmodified stock source inside GitHub CI with local disposable files and DB. No production paths or user NAS were accessed.