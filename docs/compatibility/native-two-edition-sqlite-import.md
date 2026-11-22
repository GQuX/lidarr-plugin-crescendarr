# M12: two MusicBrainz releases, stock importer and real SQLite

**Date:** 2026-11-22. **Lidarr pin:** stable 3.1.6.5079 / `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **PR:** [#33](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/33). **CI:** [run 45049081859](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45049081859), 45/45 native tests plus 1/1 checkpoint preparation and 1/1 separate-process recovery, all green.

## What the stock importer actually does

Native source `src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs` unconditionally calls `_releaseService.SetMonitored(albumDecision.First().Item.Release)` for each album with an approved import **before moving any tracks**, regardless of whether `replaceExisting` is true or false. The stock implementation in `src/NzbDrone.Core/Music/Repositories/ReleaseRepository.cs` loads every release of the album, sets only the supplied release ID's `Monitored` flag to true, and writes **all** those rows.

PR #33 adds two cases to the real disposable M12 importer fixture. Both create **two actual `AlbumRelease` rows** under the same album with different MusicBrainz release UUIDs; the original release begins monitored, the alternate begins unmonitored. Both retain the real SQLite repositories, native 11-of-12 quality selection, stock native file mover/recycler, physical lyric bytes and independent original backups.

| Candidate release supplied to approved tracks | `Import(... replaceExisting:false)` result | Actual SQLite monitored flags |
|---|---|---|
| Original user-selected release | 11 imported, protected better track 12 rejected | Original true, alternate false |
| Alternate previously unmonitored release | 11 imported, protected better track 12 rejected | Original false, alternate true |

Both outcomes are source-backed and observed with actual native SQLite, not merely a mocked `IReleaseService`. In both cases existing lyric bytes and protected 12th superior file survive, but the **second case violates the required immutable edition selection**. The native importer does not enforce this invariant.

## Non-negotiable executor preflight gate

Before any native import call or other filesystem mutation, Crescendarr must establish and then re-read, at the authoritative stock host boundary, the **single selected monitored MusicBrainz release UUID** and every candidate's resolved release UUID. A candidate for an alternate release, multiple monitored releases, missing/changed/unverified release identity, or stale album inventory makes the entire plan `NeedsReview` and causes **zero calls** to `ImportApprovedTracks` or `UpgradeMediaFileService`. Enforce stable album ID, original release ID and fresh snapshot identity before writing; don't rely on `replaceExisting:false`. `MergePlanner` already requires an exact incoming/current `EditionId` match (pure F04), but a write-capable Lidarr host adapter and its preflight integration are not yet implemented.

**Warning:** this native test intentionally passes the wrong edition to characterize unmodified Lidarr; this behavior is **not** approved Crescendarr behavior.

## Limits and next work

- The two editions have equal synthetic 12-track counts and the same album identity; they are not a tag-read/recording-identity experiment. Actual track IDs and MusicBrainz recording IDs still use synthetic fixture values.
- Physical MP3/FLAC-named bytes in the combined importer are opaque synthetic payloads, not playable, tagged audio. Real metadata parsing, album identification overrides and MP3/FLAC re-tagging remain untested.
- The native album selection is observed in disposable SQLite only. The full host dependency graph, release refresh, concurrent background scan, crashing server and SMB/CIFS filesystem remain untested.
- Next: native read-only preflight adapter proof with two competing editions and mismatch requiring **zero** stock import/upgrade invocations; actual valid tagged media; native post-delete sixth-transfer failure with real filesystem mover; idempotent durable journal and cross-process reconciliation.

**Production remains NO-GO.** Lidarr source was not patched; no production files or configuration were accessed.