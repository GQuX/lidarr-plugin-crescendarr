# Native Lidarr lyrics and download-settlement characterization

**Date:** 2026-11-22  
**Source under test:** [Lidarr `v3.1.6.5079` commit `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`](https://github.com/Lidarr/Lidarr/tree/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd)  
**Executable evidence:** [GitHub Actions run #45025608674](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45025608674): **16/16 native NUnit specifications passed** (6 prior + 10 new).  
**PR:** [#12](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/12)

These are original Crescendarr tests run against **real Lidarr service implementations** in an ephemeral source checkout. All filesystem operations, database repositories, download results, and download history are mocked. **No real files, NAS share, downloader, or live Lidarr instance were accessed.**

## 1. Existing, registered lyrics

The [native `LyricFileService` event-handler fixture](../../tests/LidarrNative/CrescendarrLyricSidecarCharacterizationFixture.cs) verifies:

- `.lrc` is a supported extension in `LyricFileExtensions`.
- When a `TrackFileDeletedEvent` has reason `Upgrade`, and a linked registered `LyricFile` exists, `IRecycleBinProvider.DeleteFile` is invoked **before** `IExtraFileRepository<LyricFile>.DeleteForTrackFile`. These call ordering assertions passed.
- For `NoLinkedEpisodes` cleanup, the DB relation is deleted **without** recycling the actual extra file.
- If a linked lyric record exists but its physical path is missing, the record is still deleted, without a recycle call.

Relevant Lidarr source: [`ExtraFileService.Handle` lines 103-129](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Extras/Files/ExtraFileService.cs#L103-L129), [`ExistingLyricImporter` lines 31-87](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Extras/Lyrics/ExistingLyricImporter.cs#L31-L87). The latter shows how matching existing lyrics are associated with a `TrackFileId`, but our tests **do not** prove that every arbitrary lyric path in a real library gets registered.

A configured Lidarr recycle bin may preserve old bytes outside the library. The tests **do not** prove recovery, pairing with new audio, or retention without a recycle bin. The [stock `RecycleBinProvider`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/RecycleBinProvider.cs#L69-L89) permanently deletes if unconfigured.

## 2. Download-staging lyrics

The [native staging-folder fixture](../../tests/LidarrNative/CrescendarrStagedLyricCleanupCharacterizationFixture.cs) passes `ShouldDeleteFolder` an entirely synthetic inventory consisting of a single `01-title.lrc` and no audio or RAR archive. The result is **true**.

Stock [`DownloadedTracksImportService.ProcessFolder` lines 225-243](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs#L225-L243) then recursively deletes the folder **if** import mode resolves to Move, at least one import result is Imported, and `ShouldDeleteFolder` is true. This conditional branch is source-verified; our fixture **only** invokes `ShouldDeleteFolder`, not recursive deletion.

There is an additional independent risk because [`ConfigService.ImportExtraFiles`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Configuration/ConfigService.cs#L233-L241) defaults to `false` and the default extra extensions string is `srt`. Thus ordinary extra-file handling cannot be assumed to copy `.lrc` from staging before cleanup. We have not yet executed a full import with a physical staged `.lrc`.

## 3. Completion and history mapping

The [native `CompletedDownloadService` fixture](../../tests/LidarrNative/CrescendarrCompletedDownloadCharacterizationFixture.cs) invokes the real stock service with synthetic `ImportResult` sequences and fake history/download dependencies.

| Results returned by mocked importer | Stock observed state | Test expectation |
|---|---|---|
| 0 results | `ImportPending`; no completion event | Passed |
| 12 intentional keep/rejected results, empty history | `ImportFailed`, `AlbumImportIncompleteEvent`, no completion | Passed |
| 11 imported + 1 intentionally kept/rejected, empty history | `ImportFailed`, incomplete event | Passed |
| Same 11+1 results, `ITrackedDownloadAlreadyImported.IsImported` mocked true | `Imported`, completion event | Passed |
| 12 imported results | `Imported`, completion event | Passed |

The history=true case **does not independently establish history correctness**. It deliberately tests Lidarr's trust in the existing history abstraction. Stock `ImportResultType` lacks a representation of `KeptExistingBecauseBetter`; intentional rejection is not a semantically distinct success.

Source: [`CompletedDownloadService.Import` and `VerifyImport` lines 126-250](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/CompletedDownloadService.cs#L126-L250). These tests are service-level, **not** a real SABnzbd job or an end-to-end cleanup test.

## Implications and remaining release blockers

1. Any safe Crescendarr workflow needs an explicit **lyrics retention or relinking contract** for registered existing lyrics and downloaded staging sidecars. A standalone protected `.lrc` path must not be treated as disposable just because the audio finished importing.
2. Correct partial upgrade completion requires a **host-owned, track-aware settlement outcome**, not pretending a rejected track was imported, forging history, or relying on a single happy-path enum.
3. Recovery must preserve originals, sidecars, and database consistency across failed storage/database operations. [Earlier native failure-order tests](native-upgrade-failure-characterization.md) showed existing audio and DB unlink requests occurring before failed transfer.
4. The production design remains **NO-GO** until these issues and safe fallback/version changes are resolved. The current outer importer test probe is a disposable diagnostic, not a release plugin.

Continue [#7](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7) and [#6](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6). We have **not** modified upstream Lidarr, production Lidarr, Tubifarry, NAS, SABnzbd, or user files.
