# Native Lidarr importer characterization: 11 upgrades + 1 better existing track

**Date:** 2026-11-22  
**Pin:** [Lidarr v3.1.6.5079 source, commit `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`](https://github.com/Lidarr/Lidarr/tree/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd)  
**Evidence:** [Native GitHub Actions run #45023293982](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45023293982)  
**Fixture:** [`tests/LidarrNative/CrescendarrAlbumUpgradeCharacterizationFixture.cs`](../../tests/LidarrNative/CrescendarrAlbumUpgradeCharacterizationFixture.cs)  
**Result:** **3/3 NUnit tests passed** against the *actual stock import and quality-specification classes*, with all filesystem/DB operations mocked.

## Exact synthetic scenario

One Lidarr artist, album and release; 12 distinct existing track-file records. Tracks 1–11 are recorded as MP3_192, track 12 as FLAC. A downloaded album supplies MP3_256 files for every track.

The test explicitly evaluates all 12 candidates via native `UpgradeSpecification.IsSatisfiedBy` against a real `QualityModelComparer` using Lidarr's default test quality profile, producing **11 approved and 1 rejected** decisions. The 12th incoming candidate is inferior to the existing FLAC.

Those decisions are passed directly to native `ImportApprovedTracks.Import` in two modes. This is a unit-level characterization, **not** a completed download, live disk import, end-to-end integration, or plugin validation.

| Native call | Observed mock interactions | Result |
|---|---|---|
| `Import(decisions, replaceExisting: true)` | `IRecycleBinProvider.DeleteFile` called for **all 12 existing paths**, including the better FLAC; `IMediaFileService.Delete` for all 12; **11** approved files delegated to the mocked upgrader | **Destructive pre-wipe confirmed** |
| `Import(decisions, replaceExisting: false)` | **0** album-wide recycle/delete calls; **11** approved files delegated to mocked upgrader; 1 rejected result | Album-wide pre-wipe avoided at this layer |
| `Import(decisions, replaceExisting: false)` | `IReleaseService.SetMonitored` still called once | Edition state is **not** protected by the false flag |

### Upstream source explaining the results

- [`DownloadedTracksImportService.cs:223`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs#L223) calls importer with `replaceExisting=true` for automatic download import.
- [`ImportApprovedTracks.cs:120-128`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs#L120-L128) pre-deletes all album files when true and calls `SetMonitored` regardless of the replacement flag.
- [`UpgradeSpecification.cs:27-64`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/TrackImport/Specifications/UpgradeSpecification.cs#L27-L64) rejects the inferior incoming track.
- [`UpgradeMediaFileService.cs:57-86`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/UpgradeMediaFileService.cs#L57-L86) has a separate delete-before-move path even when the album-wide pre-wipe is skipped.
- [`ReleaseRepository.cs:74-80`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Music/Repositories/ReleaseRepository.cs#L74-L80) sets exactly one monitored album release and persists the changes.

## Explicit limitations

1. **The per-track upgrader was mocked.** These tests cannot prove physical files, lyrics, database links, or downloads survive the real upgrade sequence, let alone NAS failures.
2. The `IReleaseService` is mocked; test #3 only verifies `SetMonitored` **would be invoked**, not that a specific alternative edition was switched in a database.
3. `UpgradeSpecification` is invoked by the test directly rather than via Lidarr's complete scan/download decision engine; actual matching, profiles, exceptions and concurrency remain untested.
4. No user-owned music, production Lidarr, downloader, NAS or SMB mount was used.
5. This is evidence of **stock source behavior**, not a deployed fix or an endorsement of `replaceExisting=false` as a complete solution.

## Reproducibility / CI

The workflow [`.github/workflows/native-lidarr-characterization.yml`](../../.github/workflows/native-lidarr-characterization.yml) checks out the exact upstream commit and copies the original characterization test into a throwaway GitHub Actions runner. The temporary test project adds a reference to Lidarr's native Linux platform assembly required by its `AutoMoqer`.

For the historical test checkout only, `NuGetAudit=false` bypasses restore failures caused by security advisories for the **pinned upstream ImageSharp dependency**. This neither changes that package nor signifies that it is safe for distribution. Do not copy this suppression into any production build.

## Next engineering gates

- [Issue #6](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6): independently characterize real `UpgradeMediaFileService` failure points (delete-before-move, per-track DB relations) and a proposed upstream selective-removal correction.
- [Issue #7](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7): preserve library and staged `.lrc` sidecars; track no-improvement, partial and recovery states.
- [Issue #3](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/3): coordinate with open upstream Lidarr PR #6166 when ready for external discussion.
- Do **not** turn the test-only `IDownloadedTracksImportService` adapter into a production importer. No write-enabled Crescendarr is authorized.

**Status:** first native characterization milestone complete; no production-ready plugin.
