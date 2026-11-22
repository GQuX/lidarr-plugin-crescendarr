# Native Lidarr upgrade fault characterization

**Date:** 2026-11-22
**Source:** [Lidarr v3.1.6.5079, commit da7b4dfb](https://github.com/Lidarr/Lidarr/tree/da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd)
**Evidence:** [GitHub Actions run #45023888023](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45023888023): **6/6 native NUnit tests passed**, including three new faults.
**Fixture:** [CrescendarrUpgradeFailureCharacterizationFixture.cs](../../tests/LidarrNative/CrescendarrUpgradeFailureCharacterizationFixture.cs)

## Real host service, mocked side effects

These tests instantiate the actual stock `UpgradeMediaFileService` with mocked disk, recycle-bin, track-file repository and mover services. The deliberate fault conditions verify exact call ordering:

| Fault | Calls observed |
|---|---|
| Move of replacement fails | Old path `DeleteFile` called, old DB `Delete` called, then `MoveTrackFile` throws `IOException` |
| Copy of replacement fails | Old path `DeleteFile` called, old DB `Delete` called, then `CopyTrackFile` throws `IOException` |
| Recycling the old file fails | `DeleteFile` throws before database deletion and before replacement move |

The first two tests assert an ordered three-step callback list, not just individual call counts.

[Upstream source: UpgradeMediaFileService.UpgradeTrackFile lines 39-89](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/UpgradeMediaFileService.cs#L39-L89).

## Interpretation and limitations

Stock Lidarr invokes removal of the existing audio path **and** its database mapping before attempting to transfer the replacement. This is a separate failure window from the album-wide pre-wipe documented in [native 12-track characterization](native-12-track-characterization.md).

**These mock tests do not prove that bytes or database rows were physically lost.** They also do not test actual SMB outages, power failures, transaction boundaries, staged/library `.lrc` files, rollback, or post-crash recovery. A configured recycle bin might retain an old file, but its ability to recover automatically remains unproven.

The CI workflow copies these original test files into a disposable checkout of the SHA-pinned upstream source; it modifies neither the upstream GitHub repository nor any user storage. Historical vulnerable NuGet dependencies cause restore warnings-as-errors; `NuGetAudit=false` is applied only to this isolated historical-source test. This is **not** appropriate production security policy.

## Required next gate

Design and validate host-owned staged replacement, verified backups and sidecar handling, durable transaction/recovery, edition protection, and truthful queue settlement before any write-enabled Crescendarr release. Follow [#6](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6) and [#7](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7).

**No production Lidarr, NAS, downloader or real audio was touched.**
