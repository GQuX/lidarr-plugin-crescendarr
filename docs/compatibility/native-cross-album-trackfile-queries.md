# M15: read-only cross-album TrackFile reference discovery

**Status:** VERIFIED FIRST INCREMENT, draft / test-only. [Native CI 45070164250](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45070164250) on `fb2a941` passes **66/66** native tests plus **1/1 + 1/1** separate-process recovery stages; [planner CI 45070164209](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45070164209) passes. Independent review remains required. **Production write-enabled gate: NO-GO.** Linked issue: [#40](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/40).

## Source-verified stock query

Pinned unmodified Lidarr `v3.1.6.5079`, `src/NzbDrone.Core/Music/Repositories/TrackRepository.cs`:

- `ITrackRepository.GetTracksByFileId(int fileId)` calls `Query(e => e.TrackFileId == fileId)`.
- `ITrackRepository.GetTracksByFileId(IEnumerable<int> ids)` calls `Query(x => ids.Contains(x.TrackFileId))`.
- Neither query filters by artist, album, monitored release or file path. By comparison, `GetTracksByAlbum(int albumId)` expressly joins releases and albums and filters by monitored release and target album.

That is a **source-level candidate** for the host adapter's cross-album safety enumeration. It is not a durable snapshot guarantee; it does not solve a concurrent host rescan.

## Runnable native characterization

`tests/LidarrNative/CrescendarrCrossAlbumTrackFileQueryFixture.cs`, inside the existing isolated native workflow, uses migrated disposable SQLite and the **actual stock TrackRepository** with no media files, downloader or user library:

1. Two different album/release rows for the same synthetic artist, both monitored.
2. A primary album's operation-bound `TrackFile` referenced by a track in that album and an alias track in the **second** album.
3. A separate `TrackFile` also legitimately shared by two unrelated tracks.
4. Assert `GetTracksByFileId(operationBoundId)` returns **both** tracks, while `GetTracksByAlbum(primaryAlbumId)` misses the cross-album alias.
5. Assert the batched file-ID query returns every relevant link and that unrelated sharing is also fully visible for later per-operation filtering.

**The negative album-only result is essential:** M13's X06 reconciler can detect cross-album sharing only if the supplying host adapter includes those tracks. A partial `HostSnapshot` cannot be called safe merely because the pure reconciler returned a completed verdict.

## Verified result

Actual native SQLite log marker from CI run 45070164250:

```text
M15_CROSS_ALBUM_QUERY: boundTrack=1 crossAlbumAlias=2 fileId=1 globalMatches=2 albumOnlyMatches=2 albumOnlyMissesAlias=True batchedMatches=4 unrelatedSharedMatches=2
```

This directly establishes that both the single-ID and batch-ID stock repository queries can observe another album's linked track; a target-album-only query sees the target-album tracks but **misses the cross-album alias**. Unrelated shared-file links also remain visible. The result is a read-only repository characterization, **not** a host adapter, atomic snapshot or per-track merge.

## Next integration gates (not claimed here)

- Feed the real native SQLite query into an adapter model projected into M13's pure `HostSnapshot` and require `RequiresReview` for a row bound or created by an operation, but not unrelated sharing.
- Handle both native post-delete states (orphaned nonzero file ID and `TrackFileId = 0`) without inventing completeness.
- Verify exact artist path, selected release MBID, recording MBIDs, all album-sidecar identities, relevant `TrackFile` paths, fresh observations and database snapshot consistency.
- Show the query runs through the **actual additive stock-Lidarr plugin DI seam** in a disposable host, not merely a `DbTest`.
- Document race windows and fail closed on insufficient or inconsistent snapshots. Native local SQLite results do not establish safety on a real host or SMB.

No changes to upstream Lidarr source, user data, host configuration, native CDH, actual media or production plugin behavior.
