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

## Second executable case: sixth-file failure and in-process repair

[Draft PR #26](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/26) adds a second parameterized case in the **same real disposable-file/SQLite fixture**. [Native run 45038820816](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45038820816) passed **36/36** tests (35 prior + 1 additional scenario).

After the sixth target has passed the native `UpgradeMediaFileService`'s recycle step and native `MediaFileService.Delete` has removed its old SQLite `TrackFile` row, the test's low-level transfer callback raises a synthetic `IOException` **before** the incoming file is moved. The stock `ImportApprovedTracks` catches the exception, returns **10 Imported, 1 Skipped, 1 Rejected**, and continues with later upgrades.

The test observes a real intermediate integrity problem: the sixth original destination is absent on disk; its old `TrackFile` row is missing; the `Track` row still refers to that old ID because the test aggregator is mocked; its former lyric association is unlinked, although the physical `.lrc` survives. The staged sixth candidate and independent verified backup remain intact. This is a **test with intentionally mocked event dispatch**, not proof that a real host's native event handlers leave precisely the same orphan reference.

Test-authored **in-process** repair copies the original media bytes back from the independently verified backup; uses **real native** `IMediaFileService.Add` to register a fresh `TrackFile`, `ITrackService.SetFileIds` to link the original track to its new file ID, and `LyricFileService.Upsert` with `Id=0` to restore the physical lyric's association. It then validates 12 valid `Track`→`TrackFile` references, all 12 lyric associations, and retained bytes for the other ten improvements and the better original on track twelve.

**Scope boundary:** the test's recovery operations are embedded in the NUnit test, not implemented in a plugin. No restart, transaction journal, real native event fan-out, scan, file collision, SMB disconnect or durable persistence guarantees were exercised. Replaying this sequence blindly after a crash would be unsafe without an operation journal and fresh identity/path verification.

## Important exclusions, not production evidence

1. The files named `.mp3`/`.flac` contain **opaque synthetic bytes, not valid playable audio**. The native audio tag reader/writer is mocked. Thus no ID3/FLAC parsing, metadata write, checksum after actual host-tagging, audio quality decoding, custom format scoring, or MusicBrainz track identification is validated.
2. **Native `TrackFileMovingService`, `RecycleBinProvider` and filesystem transfer service are not exercised.** Their interfaces use controlled callbacks that really move the temporary bytes. This deliberately isolates DB and importer effects before testing the stock storage implementation.
3. **The full `IMakeImportDecision` / `IdentificationOverrides.AlbumRelease` chain is not exercised.** Decisions are constructed from a real stock `UpgradeSpecification` result; no ambiguous/multi-edition identification is present. The inserted release stays monitored but competing releases are not tested.
4. **The event aggregator is mocked.** Real `TrackFileDeletedEvent` fan-out, rescan cleanup, sidecar detection and concurrent handlers are not proven. Lyric unlink/relink is a test-controlled step.
5. **The injected sixth-file failure is recovered inside the same test process only.** There is no restart, scan, WAL-crash test or SMB/CIFS operation. The recovery copies were verified but crash-safe recovery has *not* been demonstrated. No output may be treated as atomic.
6. This is **not an installable plugin**. Stock manual imports, completed-job settlement, CDH OFF recovery, plugin-uninstall fallback, and downloaded staging retention still require separate tests.

## Follow-up gates

- **Completed at in-process test scope:** sixth-file fault after native deletion, actual SQLite/byte orphan inspection, and fresh native-record repair from verified copies. **Still required:** implement journaling and reproduce process restart + rescan recovery, with real event handlers and collision checks.
- Replace the test-controlled low-level mover/recycler with stock `TrackFileMovingService`/`RecycleBinProvider` using disposable actual filesystem transfer and valid tiny tagged audio samples.
- Prove the full native decision maker and exact release override against *two* competing release identities, including unmonitored release preservation and equal-quality ambiguity.
- Test registered **and unregistered**, staged and adjacent `.lrc` files; real synchronous event handlers and out-of-band disk scanning.
- Verify idempotent CDH-OFF download observation, correct no-improvement/partial settlement and clean plugin unload without touching a real download client.

**Shipping decision: NO-GO.** No upstream Lidarr patch, production import, user NAS, SMB mount, SABnzbd account or live library was used.
