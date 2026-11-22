# Upstream overlap: Lidarr PR #6166

**Inspected 2026-11-22.** [Lidarr PR #6166](https://github.com/Lidarr/Lidarr/pull/6166), **Add song selection, per-track search, and selective import**, remains **open and unmerged**. These notes are source-review observations, not evidence of a running system or a settled upstream design. Do not deploy either experimental build to a production library.

## What the PR already addresses

- A Songs UI, selected recording IDs, one-time song searches, and ongoing per-artist song-monitoring mode.
- Propagating selected recording IDs through search, download history, pending downloads, and completed-download handling.
- Subset-aware replacement. In `ImportApprovedTracks.Import`, if the download or current release is selective, the PR calls `RemoveExistingTrackFiles(artist, album, tracksToReplace)` instead of wiping all album files. Existing files are selected for removal only if all associated tracks match the selected IDs or recording IDs.
- Completed-download verification for targeted recordings. `CompletedDownloadService.VerifyImport` requires all requested recording IDs among imported results.
- These are meaningful upstream advances toward the selective-import feature Crescendarr wants. Do not reimplement those features blindly.

## Differences from Crescendarr's goals to verify

| Requirement | PR #6166 source observation | Not yet proved |
|---|---|---|
| Automatic, per-track upgrade of ordinary album downloads | PR uses target-recording IDs from explicit song requests / Song Mode; selective replacement is conditional | Does stock automatic album upgrades compare and preserve a better individual existing track? |
| Protect better unrelated tracks | Adds `tracksToReplace` filtering during selective imports | All existing track associations, edition mappings and unchanged files survive |
| Edition lock | PR's shown `ImportApprovedTracks` diff changes deletion filtering but not the surrounding `SetMonitored(newRelease)` behavior | Never changes the monitored MusicBrainz edition on a partial import |
| Completed download | Requires all explicitly targeted recording IDs | Truthful `NoImprovement`, `PartiallyApplied`, `RequiresReview`, and recovery outcomes, not merely success/fail |
| Source staging / lyrics | `DownloadedTracksImportService.DiscardUntargetedFiles` can call `_diskProvider.DeleteFile(track.Path)` for rejected untargeted source audio after all targets imported | No loss of downloaded or existing `.lrc` sidecars from deletion or folder cleanup |
| SMB/CIFS and crash recovery | No corresponding durable recoverability guarantee established by this inspection | Backups, atomic-as-possible writes, restart recovery, DB consistency |
| Supported plugin API | PR is a core change, not a defined external pre-import hook | Safe extension point without overriding internal DryIoc service binding |

The reviewed patches are in [PR files](https://github.com/Lidarr/Lidarr/pull/6166/files): `src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs`, `src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs`, and `src/NzbDrone.Core/Download/CompletedDownloadService.cs`. The deletion path has guard conditions; its existence alone does **not** establish a bug.

## Strategy

1. Preserve the pure Crescendarr planner as a standalone and rigorously tested component.
2. First test whether stable Lidarr v3.1.6.5079 can compile and load a test-only probe in a disposable host.
3. Track PR #6166 for integration design, overlap, and upstream review progress; never claim it is merged.
4. If its eventual implementation satisfies the user-facing requirement, favor small targeted upstream contributions rather than duplicating it in a brittle plugin.
5. If a plugin still solves missing use cases, retain the strict Lidarr-only dependency model. Any upstream proposal should include test fixtures for 11 upgrades + 1 better kept, edition preservation, `.lrc` sidecars, no-improvement settlement, recovery after SMB failure.

No external issue or PR comments have been posted on the Lidarr project.
