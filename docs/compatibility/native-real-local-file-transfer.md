# Native Lidarr disk transfer, recycling and mover on real local audio

**Date:** 2026-11-22. **Lidarr source pin:** stable v3.1.6.5079 / `da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd`. **Experiment:** [PR #30](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/30). **Evidence:** [native CI #45042406597](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45042406597), **42/42 native** plus **1/1 checkpoint preparation + 1/1 separate-process recovery**, all passed. The dependency-free planner CI also passed.

## What this closes

The new native `CrescendarrStockLocalFileMoverFixture` uses the exact stock classes `TrackFileMovingService`, `RecycleBinProvider`, and `DiskTransferService`, with the **actual Linux disk provider, actual ProcMountProvider and actual SymbolicLinkResolver**. The fixture creates a fully disposable root beneath NUnit's per-test temporary folder, with an artist/album, staging, recycler and physical .lrc. All three WAV files are **valid deterministic 16-bit mono PCM WAVE, 8000 Hz, 0.1 seconds**. A copy in an unrelated protected track-12 path is byte-checked by SHA256.

Two tested paths:

1. **Selected replacement, destination cleared:** move the old track-1 WAV to the disposable recycle folder using stock RecycleBinProvider + DiskTransferService. Confirm old bytes survive in recycle. Move the staged new WAV into track 1 using stock TrackFileMovingService + DiskTransferService and verify byte-identical incoming contents, staged file disappearance, old recycled bytes retained, protected track-12 SHA256 unchanged and adjacent .lrc bytes untouched.
2. **Destination collision:** do **not** remove the original destination. Call the stock mover with `overwrite=false` (its standard path) and assert `DestinationAlreadyExistsException`. Confirm original track-1 WAV, staged new WAV, protected track-12 WAV and .lrc remain unchanged and no recycle output was created.

The first trial of this experiment was blocked by an unused import. The next trial passed file assertions but failed the native warning verifier because the NUnit harness auto-mocked Mono mount and symlink dependencies, producing invalid/empty paths. The final green trial supplies the **real** stock mount and symlink services rather than silencing or accepting unexpected warnings.

## Critical scope boundaries

- This is genuine native file transfer/recycling **as a standalone isolated filesystem contract**, not the complete 12-track approved-importer pipeline. The separate 12-track importer tests still use test-controlled move/recycle callbacks.
- Naming, configured root folder, script-import behavior, track-file date/permissions and event handling are intentionally mocked or controlled. No real Lidarr host dependency container or full event graph is installed here.
- Valid PCM WAVE audio is used only as byte payload. Stock tag reading/writing, MP3/FLAC codecs, metadata identification, custom formats, download-client settlement and the MusicBrainz edition pin are **not** tested by this fixture.
- All paths are local ephemeral GitHub CI filesystems, **not SMB/CIFS**, a NAS, a download client or an existing user's audio collection. Local success does not imply crash durability, safe copy+delete on disconnected network mounts, or protection against concurrently triggered scans.
- The sidecar remains intact because this test calls only an audio-file recycler, not a `TrackFileDeletedEvent` with a registered lyric row. Separate [real event dispatch tests](native-event-dispatch-sqlite.md) prove that a still-registered lyric may be recycled.

## Next gate

1. Combine the real native filesystem mover/recycler with the full source-pinned **12-track SQLite approved-importer** fixture, using actual valid tagged audio and deliberate safe backup/lyric-unlink steps.
2. Test the stock native tag reader/writer, identification overrides against two selected releases and all 11 strict quality upgrades + 12th protected FLAC.
3. Inject a sixth-file physical fault, run the real event dispatcher, and perform recovery after a separate process restart and rescan. Add durable journal revalidation, no-clobber and idempotence checks.
4. Test synthetic mount volatility and CIFS-like failure where feasible, without exposing production.

**Release remains NO-GO.** There is no installable write-enabled Crescendarr plugin, and no production environment was modified.