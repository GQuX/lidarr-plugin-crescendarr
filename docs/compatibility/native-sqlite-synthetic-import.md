# Disposable native 12-track SQLite + synthetic-byte import (M12 increment 1)

**Date:** 2026-11-22. **Host:** stock Lidarr v3.1.6.5079 source pinned at `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **CI:** [native run 45038419588](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45038419588), **35/35 passed**. **Issue:** [#23](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/23).

## What this genuinely exercised

The new `tests/LidarrNative/CrescendarrSqliteSyntheticMediaImportFixture.cs` runs unmodified stock:

- `UpgradeSpecification.IsSatisfiedBy` with a profile also inserted in disposable SQLite; exactly 11 MP3-192 → MP3-256 candidates are approved and an existing FLAC is rejected.
- `ImportApprovedTracks.Import(decisions, replaceExisting:false, importMode:Move)`.
- **Real** `UpgradeMediaFileService`, `MediaFileService`, `TrackService`, `ReleaseService` and their native SQLite repositories against the framework's **migrated, disposable SQLite**.
- Twelve real owned byte files under the test runner's temp root, twelve real staged incoming byte files, twelve real `.lrc` files and twelve registered lyric rows. An independently written and verified recovery copy is made for each owned file before import.
- Eleven per-file old-byte relocations and incoming-byte moves, performed using **test-controlled callbacks** on `IRecycleBinProvider` and `IMoveTrackFiles`. The original twelfth, higher-quality FLAC is neither relocated nor overwritten.
- Native lyric `Delete(id)` unlinks 11 old rows **before** import, preserving physical lyric bytes; fresh `Id=0` rows link to the actual new native `TrackFileId` values **after** import. The twelfth lyric's original row and ID remain untouched.
- Fresh queries of real `Tracks`, `TrackFiles`, `LyricFiles` and the monitored `AlbumRelease`, with byte-for-byte checks of all surviving lyrics, incoming replacements, recovery copies and the protected original.

**Result:** the fixture passed as part of 35/35 native tests. This is the first Crescendarr stock-importer test combining actual temporary-file bytes, migrated SQLite, native per-file upgrader and native TrackFile/Track relinking. The upstream source was copied into an ephemeral CI checkout; no production paths or files were present.

## Important exclusions, not production evidence

1. The files named `.mp3`/`.flac` contain **opaque synthetic bytes, not valid playable audio**. The native audio tag reader/writer is mocked. Thus no ID3/FLAC parsing, metadata write, checksum after actual host-tagging, audio quality decoding, custom format scoring, or MusicBrainz track identification is validated.
2. **Native `TrackFileMovingService`, `RecycleBinProvider` and filesystem transfer service are not exercised.** Their interfaces use controlled callbacks that really move the temporary bytes. This deliberately isolates DB and importer effects before testing the stock storage implementation.
3. **The full `IMakeImportDecision` / `IdentificationOverrides.AlbumRelease` chain is not exercised.** Decisions are constructed from a real stock `UpgradeSpecification` result; no ambiguous/multi-edition identification is present. The inserted release stays monitored but competing releases are not tested.
4. **The event aggregator is mocked.** Real `TrackFileDeletedEvent` fan-out, rescan cleanup, sidecar detection and concurrent handlers are not proven. Lyric unlink/relink is a test-controlled step.
5. **There is no injected failure, process restart, scan, WAL-crash test or SMB/CIFS operation.** The recovery copies were verified but their crash-safe use was *not* demonstrated. No output may be treated as atomic.
6. This is **not an installable plugin**. Stock manual imports, completed-job settlement, CDH OFF recovery, plugin-uninstall fallback, and downloaded staging retention still require separate tests.

## Follow-up gates

- Expand the same isolated test into a fault at upgrade six **after** a native deletion and inspect bytes and real SQLite immediately; explicitly exercise restoration from verified copies, and later after process restart with rescan.
- Replace the test-controlled low-level mover/recycler with stock `TrackFileMovingService`/`RecycleBinProvider` using disposable actual filesystem transfer and valid tiny tagged audio samples.
- Prove the full native decision maker and exact release override against *two* competing release identities, including unmonitored release preservation and equal-quality ambiguity.
- Test registered **and unregistered**, staged and adjacent `.lrc` files; real synchronous event handlers and out-of-band disk scanning.
- Verify idempotent CDH-OFF download observation, correct no-improvement/partial settlement and clean plugin unload without touching a real download client.

**Shipping decision: NO-GO.** No upstream Lidarr patch, production import, user NAS, SMB mount, SABnzbd account or live library was used.
