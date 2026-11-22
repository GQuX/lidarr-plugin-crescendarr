# M14: read-only stock Lidarr MusicBrainz edition preflight

**Date:** 2026-11-22. **Baseline:** stock Lidarr 3.1.6.5079, source commit `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **PR:** [#36](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/36) (number confirmed when opened). This is a disposable compile/test component, **not** a plugin release, import executor, or completed-data safety proof.

## Implemented host integration

`src/Crescendarr.Lidarr.Preflight/CrescendarrEditionPreflight.cs` is **actual .NET 8 host-facing code**, not a simulated policy. It accepts Lidarr `ImportDecision<LocalTrack>` objects and uses read-only methods on `IAlbumRepository`, `IReleaseService`, `ITrackRepository` and `IConfigService` to re-read the currently configured, monitored MusicBrainz album release and individual track identifiers.

The adapter does **not** call `ImportApprovedTracks.Import`, `ReleaseService.SetMonitored`, file writers, scanner, download removal, or database mutation. It is not registered into a running plugin or production Lidarr. Its result `EditionMatches` means only this specific edition/track snapshot matches, **never permission to write**.

For a given planned stock Album DB ID + MusicBrainz album group UUID + selected release UUID it refuses all of the following:

- Stock Completed Download Handling ON, unmonitored album, or `AnyReleaseOk` ON.
- No monitored release, more than one monitored release, malformed/duplicate release IDs or an edition different from the plan.
- Incorrect album group UUID, candidate `LocalTrack.Release` row ID or release UUID, including rejected candidates (entire candidate batch held).
- Missing/duplicate/compound release tracks, invalid recording/track UUIDs, wrong release membership, or discrepancies between candidate `TrackFileId`/recording/track UUID and a *fresh native stock SQLite* track row.
- Host read errors, malformed expected IDs, no approved candidates, and unbounded (>4096) candidate batches.

Every refusal yields a typed reason, rather than defaulting to a permissive result. The internal exception handler fails closed. An executor must separately require a `MergePlanner` plan that is still Ready, a fresh completed job inventory, file/lyric hashes and backup journal, serialized album lock, and repeat the host reads just before a write. There is still a possible time-of-check/time-of-use race between this read and native import. We **must** bridge that before production.

## Evidence and test limits

`tests/LidarrNative/CrescendarrEditionPreflightSqliteFixture.cs` creates two different real monitored/unmonitored MusicBrainz releases, real album and track database rows on native migrated SQLite. It tests exact-match acceptance and refusal on an alternate candidate, later selected-release flip, two/missing monitored rows, changed album group, stale track/file linkage, rejected alternate candidate, CDH ON, malformed/no approved input and an unavailable config source. Native CI stages the **same source file** into a temporary stock Core.Test checkout, with no upstream source changes, and a second ABI step compiles it against published pinned official .NET 8 stable binaries.

This does **not** prove a complete import with real tags, host concurrency, an atomic snapshot, or automatic rollback and does not make CDH OFF approved. It is one focused, source-pinned safety boundary in the future **single** Crescendarr plugin.

**Production status: NO-GO.**
