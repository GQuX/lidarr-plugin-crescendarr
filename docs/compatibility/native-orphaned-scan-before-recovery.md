# M14: stock DiskScanService after the sixth-file failure and before recovery

**Status: DRAFT.**

- **Empty-decision controls PASSED.** Both empty-decision scans passed on Linux native CI: [45058139237](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45058139237) for the scanner-only case, and [45058579402](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45058579402) for the real-event-during-import variant, as confirmed by the independent review.
- **Real tagged-audio scan characterized; independent controls verified.** Native CI [45065051393](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45065051393) on `2c71891` passed **65/65** native tests plus **1/1 + 1/1** independent-process stages. Planner CI [45065051407](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45065051407) also passed. This uses real tag reading, identification and import decisions; see the new section below. [Issue #38](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/38). Source pinned to unmodified Lidarr v3.1.6.5079 (`da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`). No live host, audio library, NAS/SMB or download client.

## Safety question

M12 reproduced a precise post-recycle, post-TrackFile-delete sixth incoming-transfer failure against stock `TrackFileMovingService`, `RecycleBinProvider`, `DiskTransferService` (except a single injected `IOException`) and migrated SQLite. Track six remains linked to its *deleted* old TrackFile ID while the original audio destination is missing. Can unmodified Lidarr's scanner run before a plugin can recover, and what database/file/lyric changes does it make?

## Initial runnable characterization (this PR)

In `CrescendarrSqliteSyntheticMediaImportFixture.native_scan_before_recovering_sixth_transfer_failure_preserves_recovery_evidence`:

1. Build the existing 12-track stock SQLite fixture, 11 native approved upgrades, one superior FLAC rejected; write independent original backups and preserve physical `.lrc` files.
2. Inject the failure in the **actual sixth incoming `IDiskTransferService.TransferFile` boundary**, after stock recycle and old TrackFile row deletion.
3. **Before any restore**, invoke the actual `DiskScanService.Scan` with the real disk provider, real `MediaFileTableCleanupService`, migrated native media/track services, and stock `EventAggregator` configured with stock synchronous track/lyric deletion handlers.
4. Observe the exact Track, TrackFile, LyricFile, AlbumRelease, staging and physical `.lrc` state. The current test expects a scan with no new import decisions to leave the orphaned sixth row/reference and verification evidence untouched. The initial scanner-only variant was observed to leave these state elements unchanged in native CI. Unexpected changes in the second variant must fail the test rather than being declared safe.
5. Continue the already characterized **test-only** recovery: restore verified sixth audio with no overwrite, insert a fresh TrackFile ID, relink the track and reinsert the missing lyric association. Assert all 12 links and physical lyric bytes; protect the superior FLAC and monitored release.

## Follow-up variant: real stock deletion-event fan-out BEFORE the failure (PASSED, run 45058579402)

The initial M12 sixth-transfer fault uses a mocked `IEventAggregator`, leaving a nonzero dangling `Track.TrackFileId` after the old TrackFile row is deleted. That may not match a running stock Lidarr instance: the real synchronous `TrackService.Handle(TrackFileDeletedEvent)` calls `TrackRepository.DetachTrackFile`.

The second test, `native_delete_event_fanout_before_scan_then_recovers_sixth_transfer_failure`, registers the real `EventAggregator` and stock `TrackService` / `LyricFileService` handlers *before* the native importer begins. It performs the identical sixth physical transfer failure, asserts the observed deletion-handler result (hypothesis: sixth `TrackFileId` becomes zero, rather than a dangling nonzero ID), then invokes the same real stock scan with empty decisions before recovery. The repair still uses a fresh TrackFile ID and must preserve the protected superior track, selected release, staging and every `.lrc` byte.

**Verified in native CI run 45058579402.** With the real synchronous handlers active during the importer, track six's `TrackFileId` becomes **0** (`M14_REAL_EVENT_FAULT_OBSERVED`). The older mocked-event variant leaves a dangling positive ID instead. A production adapter and reconciler must handle both shapes. These variants intentionally compare the mocked-event M12 failure with the real synchronous event-dispatch path, which is a fundamental difference for automatic recovery.

## Real tagged-audio scan: actual identification and import decisions (characterized)

`tests/LidarrNative/CrescendarrTaggedAudioRescanFixture.cs` replaces the empty-decision stub with the **stock decision pipeline**. The existing empty-decision controls above are unchanged and still pass.

**Synthetic audio, generated at runtime (no committed binaries).**
- Tracks 1–11 and the incoming files are silent MPEG-1 Layer III files: owned 192 kbps, incoming 256 kbps.
- Protected track 12 is a FLAC stream: a real STREAMINFO block with an exact sample count.
- Everything is tagged with Lidarr's own `AudioTag.Write`, carrying a consistent MusicBrainz release, release-group, artist, recording and release-track IDs, track numbers and titles. Database durations match the files.
- Before use, the stock `AudioTag` reader must return `IsValid`, the expected IDs, and the qualities `MP3-192`, `MP3-256` and `FLAC`.

**Stock components exercised.**
- **Scan and import:** `DiskScanService`, `MediaFileTableCleanupService`, and `ImportDecisionMaker` with **all 12** stock `IImportDecisionEngineSpecification` implementations.
- **Identification:** `IdentificationService`, `CandidateService`, `TrackGroupingService`, `AugmentingService` with the stock aggregators, and `AudioTagService`.
- **Writes and events:** `ImportApprovedTracks`, `UpgradeMediaFileService`, `TrackFileMovingService`, `RecycleBinProvider` and `DiskTransferService` on the real Linux disk provider, plus the real `EventAggregator` with the stock `TrackService` and `LyricFileService` deletion handlers. On `ArtistScannedEvent` it runs the stock `ExistingExtraFileService` with `ExistingLyricImporter`.
- **Storage:** migrated SQLite.
- **Test-controlled inputs:** naming, root folder, the history mock, and the import decisions for the *download* step, which is not the subject here.

A recording decorator logs every real decision (`M14_TAGGED_DECISION`).

Native CI [45063053067](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45063053067) passed all four scenarios. The final run is linked in PR #39.

| Scenario | What the real scan did |
|---|---|
| **Control**: track six has a valid file on disk but no `TrackFile` row; nothing else is wrong | 12 approved decisions (11 arrive as `AdditionalFile`). **Every** existing track relinked to a **new** row, including the protected FLAC. **All 11 registered lyrics moved to the recycle bin**, and none re-registered. Track six linked to its file (`MP3-192`). |
| **Restored original before a fresh row**: the sixth transfer fails after stock delete (real events, so `TrackFileId=0`), then the verified original is restored (M13's automatic `RestoreOriginalBytes`) | The scan **links track six to the restored original by itself** (`MP3-192`, bytes unchanged), and relinks every other track. The protected track's still-registered lyric is **recycled**. The 11 lyrics already DB-detached by the Crescendarr procedure survive in place. |
| **Incoming transferred but unlinked**: the sixth transfer completes, then the importer fails before the row insert | The scan links track six to the incoming file (`MP3-256`), relinks everything else, and **recycles the protected track's lyric**. |
| **Scan forced inside the importer** (after tracks 1–5 moved, before the batch insert; test-forced, see below) | 11 decisions; the scan creates rows for every path. The importer's `AddMany` then **throws** `UNIQUE constraint failed: TrackFiles.Path`, outside its per-track `try/catch`, so the whole import fails. End state: tracks 7–11 linked to scan-created rows that still describe the replaced 192k originals, although their paths now hold the incoming bytes; track six unlinked with its incoming file in place; no lyric rows left. |

**Held in every scenario:**
- the protected FLAC's **bytes**;
- the staged rejected candidate;
- every independent backup;
- the single monitored MusicBrainz release;
- unique `TrackFile` paths, which the stock schema enforces.

**Root cause, from source.**
1. `DiskScanService` calls the decision maker with `IncludeExisting = true`.
2. `IdentificationService` adds every existing album file as an `AdditionalFile` local track.
3. `ImportDecisionMaker` emits a decision for each one. `AdditionalFile` is consumed only by Manual Import.
4. `ImportApprovedTracks`' existing-file branch deletes the row at each path with `DeleteMediaFileReason.ManualOverride` and re-adds it.
5. `ExtraFileService.Handle(TrackFileDeletedEvent)` recycles linked extras for every reason except `NoLinkedEpisodes`.
6. `ExistingLyricImporter` cannot map a `.lrc` back to tracks from its file name ("Cannot find related tracks"), so nothing is re-registered.

**Two fixture artifacts were found and fixed before these results were accepted.** Earlier evidence runs showed lyrics surviving; that was false.
1. The lyric handler was built before the real disk provider was set, so its mocked `FileExists` returned false.
2. AutoMoqer caches resolved concrete types, so re-resolving returned that same mocked instance. The handler is now constructed explicitly.

The older empty-decision M12/M14 cases never reach a lyric-bearing deletion, so their conclusions are unaffected.

### What this changes for Crescendarr (implications; none are implemented here)

- **In this source-pinned, synthetic tagged-audio fixture, a populated stock rescan with an unknown file recycled every still-registered album lyric.** This does not establish the behavior of every ordinary rescan or all daemon configurations. Three states that can expose this risk in the fixture are:
  - a partial import that leaves a placed-but-unlinked file;
  - M13's own automatic `RestoreOriginalBytes`;
  - a user dropping a file into the folder.
- **M13's automatic restore is therefore not safe while other lyrics in the album remain registered and a stock scan can run.** Before any restore, an adapter must either keep every lyric in the album DB-detached and track them in the journal, or restore under a scan-excluded name and promote it only together with the fresh row. That is an executor design decision for M13/M15, not something proven here.
- **A stock scan can complete M13's two operator-approval repairs by itself.** It inserts a fresh row for a restored original, or for a placed incoming file. The adapter's reconciler must treat "a fresh row appeared that we did not insert" as a legitimate observation, not an inconsistency. It must still re-verify bytes and edition, and accept that every other track also received a new row ID.
- **Row identity is not stable across a stock rescan.** Every bound `TrackFile` ID can change even when no bytes changed. A journal must never treat a changed ID alone as data loss, nor rely on IDs across a scan without re-reading paths and bytes.
- **The stock schema rejects identical duplicate paths** with `UNIQUE(TrackFiles.Path)`. M13's same-path duplicate checks remain defense in depth, and case-variant paths are still possible.

### Independent review controls (verified in run 45065051393)

Following independent review, two additional source-pinned tagged-audio scenarios were run on the same migrated SQLite, disk provider, scanner, event subscribers and stock decision maker. The original four scenarios and both empty-decision controls remained unchanged.

1. **Fully linked, no-unknown-file baseline: PASSED.** All 12 tagged audio files already had `TrackFile` rows; all 12 physical `.lrc` files were registered. The real scanner returned **zero import decisions**, changed **no track links, TrackFile row IDs, lyric DB links or physical lyric bytes**, and passed every preserved-state assertion. Log: `M14_TAGGED_CLEAN_BASELINE decisions=0 approved=0 linksChanged=False rowIdsChanged=False lyricRowsChanged=False`. This **supports unknown-file-triggered cascade causality within this fixture**. It does not imply rescans are safe with other host configurations or file states.
2. **Conventional protected lyric filename: PASSED as an adverse characterization.** With the sixth audio file unknown, protected track 12's physical lyric and its **actual registered host-relative path** were both `12 - Synthetic Track 12.lrc`. The fixture now validates that `Artist.Path + LyricFile.RelativePath` is exactly the physical lyric path before any scan, preventing a decoy-path false result. Stock scanning still re-imported 11 previously linked tracks, physically recycled **all 11 still-registered lyrics including the protected one**, and ended with **zero lyric DB rows**. Log: `M14_TAGGED_CASCADE scenario=ConventionalProtectedLyricName relinked=11 lyricsRecycled=11 lyricRowsAfter=0`.
   - **Parsing limitation:** the stock parser returned an object but extracted **no track number** from either `ParseMusicPath` or `ParseMusicTitle` for this filename in this fixture. Thus this proves conventional-looking naming alone did not prevent physical recycling; it **does not** rule out different valid naming and identification settings recovering a sidecar.
   - **Rejected preliminary fixture result:** an earlier red naming test left the registered `RelativePath` pointing to the old short filename, while changing only the physical path. That did **not** test the actual registered lyric and was corrected before the successful run.
3. **Honest labels:** `CollectInvariants` now reports each actual scenario, not `ControlUnlinkedValidFile` for all cases. Test code does not treat an intentionally characterized lyric-preservation violation as a safe outcome.

The original tagged-audio scenarios pass **because their expected destructive stock behavior was reproduced and asserted**. Only the fully linked baseline asserts preservation. This is a serious unsolved plugin safety requirement, not approval for unattended writes.

### Still not proven

- **The interleaved case is test-forced.** In stock Lidarr, scans and imports run as `RequiresDiskAccess` commands and are serialized by the command queue. A production race would need a code path outside that lock.
- **Not exercised here:** the full host DI graph, other event subscribers, `ExistingMetadataImporter` / `ExistingOtherExtraImporter`, real naming and tag *writing*, fingerprinting, and a real Lidarr daemon or restart.
- **The audio is real enough for the stock tag reader, but not decoded.** Silent zero-payload MPEG frames and a FLAC STREAMINFO without a decoded frame are sufficient for TagLib's properties, not for a decoder.
- **No SMB/CIFS, NAS, downloader or user library** was involved.

Issue #38 should stay open, and the **production write-enabled gate stays NO-GO**.

### Evidence boundaries: empty-decision versus tagged-audio tests

The **older two empty-decision M14 variants** use opaque synthetic media and explicitly return no scanner import decisions. Those variants prove only native enumeration, cleanup and event handling under that artificial decision outcome.

The **new tagged-audio variants**, in contrast, really execute Lidarr's own tag reader, identification, 12 import specifications, populated decisions, importer and synchronous deletion handlers on migrated runner-local SQLite. They establish the observed synthetic full-album re-import and lyric-recycle hazard, but **not** normal-daemon frequency, other event subscribers, actual audio decoding, a production race, or SMB/CIFS durability. The additional independent-review controls above are pending execution, so whether the unknown audio file is a necessary condition remains open.

This is **not** proof that Crescendarr can preserve sidecars during a live rescan, and it provides no authorization for a production write-enabled plugin.

Everything here remains disposable runner-local testing; no user files, NAS, downloader or real Lidarr instance were accessed.
