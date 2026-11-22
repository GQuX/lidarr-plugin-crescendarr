# M12: persisted SQLite handoff between distinct test processes

**Status:** **PASSED in disposable CI**, not production readiness. [Native run #45039966505](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45039966505): **36/36 base tests**, plus **1/1 preparation** and **1/1 recovery** in distinct NUnit test processes. **Date:** 2026-11-22. **Source:** stock Lidarr stable v3.1.6.5079 pinned to `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **Tracking:** [#23](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/23), [PR #27](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/27).

## What the new two-process test does

The standard 36 native characterization tests remain independent and use disposable source-pinned Lidarr NUnit fixtures. The GitHub Actions job adds **two further `dotnet test` invocations**, in separate OS processes, after the normal suite succeeds:

1. **Prepare:** create native artist/album/release/track/TrackFile/lyric data in real migrated disposable SQLite, and write 12 original + 12 staged opaque media files and 12 physical `.lrc` files in a uniquely named GitHub Actions runner temporary directory. Compute verified independent copies of original audio. Pass **11** native quality upgrades and reject the superior twelfth FLAC. Use stock `ImportApprovedTracks`, `UpgradeMediaFileService`, `MediaFileService`, `TrackService` and `ReleaseService`, but a **test-controlled** filesystem mover/recycler and mocked event aggregator.
2. **Interrupt:** fail the sixth candidate *after the native old TrackFile row has been removed and the old physical bytes moved into the disposable recycle path*. Confirm its destination is missing, that the old SQLite row is absent but still referenced in the test's mocked-event scenario, that the candidate remains staged, and that independent backups and physical lyric bytes survive. Use `System.Data.SQLite.SQLiteConnection.BackupDatabase` to export a consistent **actual SQLite snapshot**, check `PRAGMA integrity_check`, save the sixth original's SHA256 and write a ready marker **last**.
3. **End first process:** NUnit teardown removes its own temporary SQLite database. The uniquely named GitHub runner temp directory remains with the independent backups, audio-like byte payloads, .lrc, and an exported native DB snapshot.
4. **Reopen:** a second `dotnet test` invocation migrates a **new disposable** test DB, swaps in the previous process's SQLite snapshot and queries native Track/TrackFile/AlbumRelease/LyricFile records. It confirms the sixth orphan and exact byte integrity. It restores the sixth old bytes from the independent copy to the missing destination, inserts a **fresh native TrackFile**, updates the native Track row, then reattaches original lyrics to the 10 improved TrackFiles and the sixth restored TrackFile using new `Id=0` lyric rows.
5. **Verify:** query all real DB rows again, ensure 12 valid Track → TrackFile links and 12 lyric associations, unchanged superior twelfth FLAC and untouched staged candidate for track 12, correct bytes on upgraded tracks 1–5 and 7–11, and original bytes for restored track six. Fail closed on missing inputs or inconsistent state.

All input/output paths are runner-local and under an explicit `crescendarr-m12-restart-` directory with CI-only environment opt-in. No host Lidarr configuration, NAS, SMB, SABnzbd, existing music or real user lyrics are opened.

## Evidence and strict limitations

This tests whether **raw native SQLite/byte recovery state survives a process boundary**, not whether a working Crescendarr plugin can automatically recover after a crash:

- Neither process boots a Lidarr web/server host. It is a **different NUnit process**, not a Lidarr daemon restart or a plugin reload.
- The fault is deliberately injected as an `IOException`, not produced by `SIGKILL`, power loss or an SMB disconnect.
- The test does not persist or replay a Crescendarr operation journal. Recovery decisions and original track metadata are **hardcoded to synthetic fixture identities**; a real executor must validate immutable identities, stale paths, source mutations, edition and quality before changing anything.
- The low-level move/recycle components and event aggregator remain mocked or test-controlled; native scan/removal handlers do not execute. The test's orphan state is not necessarily the same as a live event aggregator's.
- Media payloads are **not valid tagged MP3 or FLAC**. Host tag writing, MusicBrainz identification, multi-track physical files, competing releases, custom formats, storage permissions, and CIFS atomicity remain open.
- The test has no simultaneous reader, manual import, external scanner, or NAS writer. It does not certify that recovery is safe if stock Lidarr resumes scans **before** restoration.
- SQLite online-backup consistency inside CI is narrower than crash durability, where disk flush, backup storage location, file integrity and journal fsync are distinct risks.

## Next gates

1. Run the **stock** `TrackFileMovingService`, `RecycleBinProvider`, and `IDiskTransferService` on **valid synthetic tagged audio**, not test-controlled transfers.
2. Test live native `TrackFileDeletedEvent` fan-out and a scan *before* restoration. Verify physical registered/unregistered `.lrc` preservation and no incorrect auto-reassociation.
3. Build explicit operation identity, checksum, state-transition and idempotent restart logic, then test kill/restart and stale file/colliding destination rejection. Do not derive transaction safety merely from a passing online-backup test.
4. Check native `IMakeImportDecision` overrides against multiple MusicBrainz releases and preserve existing monitored-edition identity.
5. Add read-only CDH-OFF inventory (#21) and observable, truthful queue settlement before any write-enabled prototype.

**Release policy: NO-GO.** All work is source-pinned, CI-only test characterization. No installable write-enabled plugin is present.
