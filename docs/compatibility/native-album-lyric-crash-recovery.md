# P0: separate-process crash recovery for album-wide lyric detachment (issue #41, draft PR #42)

**Status: DRAFT, test-only. Production write-enabled stays NO-GO.**

Verified on source-pinned Linux native CI [45082305345](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45082305345) at `d0cd96d`. Planner run [45082305347](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45082305347) also passed.
- **Main native suite:** 68/68 passed; it excludes the new P0 tests.
- **Existing M12 separate-process stages:** 1/1 and 1/1 passed.
- **New P0 process steps:** all ten passed. That covers the three SIGKILLed hosts and seven passing processes; the negative step ran 8/8 cases.

The previous attempt, run 45082028276 on `47bea87`, also SIGKILLed lane D1 and its D2 test passed. That run still failed because my step wrapper didn't recognize the multi-line `dotnet test` summary. `d0cd96d` fixes only that wrapper check; the test code is unchanged.

## What is real and what is not

**Real:**
- Unmodified Lidarr v3.1.6.5079 source at `da7b4dfb`, compiled into its own test project.
- Migrated SQLite in WAL mode (Linux default).
- Synthetic tagged MP3 and FLAC files generated at runtime and read by the stock `AudioTag`.
- Stock `DiskScanService`, the real decision maker with all 12 specifications, real identification, `ImportApprovedTracks`, and the real `EventAggregator` with the stock `TrackService` and `LyricFileService` deletion handlers.
- The stock `ExistingLyricImporter`.
- Real local-disk recycling.

**Real crashes:** every phase runs in its **own `dotnet test` process**. A crash phase calls `Process.GetCurrentProcess().Kill()`, which is SIGKILL on Linux.
- No `finally` block, NUnit teardown, SQLite close or flush runs.
- CI requires a nonzero exit code, vstest's `Test host process crashed` message, and the kill-point evidence file.
- The next process copies the killed process's `lidarr.db` **and its `-wal`** exactly as SIGKILL left them, and SQLite's own WAL recovery decides which commits survived. The `.db`, `-wal` and `-shm` files are preserved as evidence.
- After the import, `PRAGMA integrity_check` must return `ok`.
- The recovering process asserts that its PID differs from the killed one.

**Not real:**
- **Process model:** this is not a Lidarr daemon or a server restart. There is no host DI container, command queue or `RequiresDiskAccess` lock, and no second process runs concurrently.
- **Inputs:** naming, the artist, album and root-folder services, and the history service are test-controlled, exactly as in the M14 fixture.
- **The reconciler** (`P0Recover` / `P0Observe` in `tests/LidarrNative/CrescendarrAlbumLyricCrashRecoveryFixture.cs`) is a **test-only reference model**, not Crescendarr production code.
- **Durability is process-kill durability only.** `FileStream.Flush(true)` is used for the journal, manifest, backups and restores. There is no directory fsync, no power-loss test, and no SMB/CIFS.

## Durable state written before any destructive step

`manifest.json` is written with temp-file + `Flush(true)` + no-overwrite rename, and is hash-sealed. It holds one binding for **every** album track, including the **unselected superior FLAC (track 12)** and the unregistered track six. Each binding records:

- **Identity:** medium number, recording MBID, release-track MBID, plus album, release-group and monitored-release MBIDs. The track number is informational only and is never used as a key.
- **Audio:** artist-relative audio path and its original SHA-256.
- **Lyric:** whether it was registered, its artist-relative path and SHA-256, plus an independently written and re-read backup with its SHA-256.
- **Original IDs:** TrackFile and lyric row IDs, recorded **for diagnostics only**. Recovery never writes them back and never uses them as identity.

`journal.log` is append-only, with `Flush(true)` per record. It is advisory: recovery decides from **fresh host observation**, not from the journal. One exception: a durable `REQUIRES_REVIEW` record is sticky.

## Lanes and observed results

### Lane D: crash mid-detach, plugin absent, real rescan, then recovery

| Step (separate process) | Observed in CI 45082305345 |
|---|---|
| D1 `detach-crash` | Manifest of 12 bindings published; 11 registered lyrics. Five detaches journaled. The sixth DB delete (track 7) committed, then **SIGKILL before its `DETACHED` record**. |
| D2 `absent-scan` | The reopened WAL shows track 7's delete survived without a journal record: **journal says 5 detached, DB says 6** (`P0_CRASH_AFTER_KILL`). With the plugin absent, a real stock tagged rescan made 12 approved decisions and relinked every track to a **fresh TrackFile ID**. It recycled the 5 still-registered lyrics **[8,9,10,11,12], including the unselected superior FLAC's**, and left 0 lyric rows. Detached lyrics 1–5 and 7, and the never-registered 6, survived. |
| D3 `negative` (8 cases on relocated copies of the D2 state) | Every case returned `RequiresReview`, twice, with no file, lyric row or track-link change (see below). |
| D4 `recover` | The plan had 11 steps: 5 restores from verified backups (no-overwrite) plus 11 fresh lyric rows bound to the **new** TrackFile IDs. No deleted lyric ID was reused. Track six's lyric stayed unregistered, as in the original. Every pre-existing file kept its bytes, and the only new files were the 5 restored lyrics. |
| D5 `replay` (second restart) | `AlreadyRecovered` (11 attached, 1 untouched, 0 steps). A real stock rescan of the recovered album made **0 decisions** and changed no link, row, lyric or byte. A third reconcile still returned `AlreadyRecovered`. |
| D6 `review-replay` | The D3 conflicting-bytes case was reopened in a new process after the conflicting file had been removed. It **stayed `RequiresReview`** (sticky) with zero mutations. |

### Lane R: crash mid-detach, then crash mid-reattach, plugin-absent rescan, recovery

| Step (separate process) | Observed |
|---|---|
| R1 `detach-crash` | Same kill point as D1. |
| R2 `recover-crash` | A restarted reconciler with **no scan in between** planned 5 attached, 1 untouched and 6 reattach steps. It was **SIGKILLed after the third committed reattach insert, before its `REATTACHED` record**. |
| R3 `absent-scan-recover` | The reopened DB shows lyrics [1,2,3,8,9,10,11,12] registered: **journal says 2 reattached, DB says 3**. Track six's audio was still unregistered, so the plugin-absent stock rescan cascaded again and recycled all 8 registered lyrics, **including the 3 just reattached** (`reattachedLyricsRecycledAgain=[1,2,3]`). The reconciler then restored the 8 from verified backups and bound 11 fresh rows to the fresh TrackFile IDs. |
| R4 `replay` | Same as D5: `AlreadyRecovered`, a 0-decision rescan, and no change. |

### Negative and ambiguous observations (lane D3; each must yield `RequiresReview`)

| Case | Durable reason recorded |
|---|---|
| Unrelated bytes at a missing lyric path | `unrelated bytes occupy the lyric path; refusing to overwrite` |
| Audio bytes at the bound path changed | `physical audio bytes differ from the manifest` |
| Another album's track links the same TrackFile | `TrackFile 21 shared by 2 tracks` |
| Monitored release switched to another edition | `monitored release is not exactly the manifest release` |
| Stale lyric row at the path, pointing at a deleted TrackFile ID | `conflicting or stale lyric row(s) [12->7]` |
| Truncated manifest | `manifest unreadable: …` |
| Manifest field edited without resealing | `manifest hash mismatch` |
| Lyric missing and its backup corrupted | `lyric missing and backup unverifiable` |

The reconciler applies nothing if **any** binding in the album needs review, so it never repairs an album partially. Immediately before each mutation it re-reads the track link and lyric rows, and any drift returns `RequiresReview`.

## Host states at each boundary (this fixture)

| Boundary | Registered lyrics | Physical lyrics | Plugin-absent stock scan outcome | Recoverable here? |
|---|---|---|---|---|
| Before manifest/backups | 11 | 12 | Recycles all 11 registered (M14) | **No.** No backup exists. Backups must come first. |
| Manifest + backups published, before detach | 11 | 12 | Recycles all 11 | Yes, from backups (not separately run; it is a superset of the D2 state) |
| Mid-detach crash (D1/R1) | 5 | 12 | Recycles the 5, including the superior FLAC's (D2) | **Yes** (D4) |
| All detached | 0 | 12 | Recycles none; fresh IDs (PR #42 control A) | Yes, rebind only |
| Mid-reattach crash (R2) | 8 | 12 | Recycles all 8, **including already reattached ones** (R3) | **Yes** (R3) |
| Recovered, every audio file linked | 11 | 12 | 0 decisions, no change (D5/R4) | n/a |

The stock scan only cascades while an **unregistered audio file** is present. Here that is track six, which is a fixture premise.

## Findings

1. **A durable intent record is not proof of state.** In both lanes SIGKILL landed between a committed SQLite write and its journal record. The journal lagged the DB both times: 5 vs 6, then 2 vs 3. Recovery that trusts the journal would be wrong, so recovery must be observation-based.
2. **Reattaching while an unregistered audio file exists re-exposes lyrics.** Lane R shows a rescan recycling lyrics the reconciler had just reattached. Reattachment is safe only after every album audio file is linked. That condition held in D5/R4, where the rescan was a no-op. Otherwise the independent backups are the only thing preserving the bytes.
3. **Every TrackFile ID changes across the cascade.** Recovery succeeded only by re-identifying tracks by release + medium + recording + release-track MBID and validating paths and SHA-256. The original IDs were never written back.
4. **Album-wide backups are indispensable,** including for the unselected superior track. Five or eight lyrics were physically gone after the plugin-absent scans, and only the independent backups restored them.

## Remaining hazards (not proven, do not infer)

- **Unattended windows remain.**
  - Between the crash and the next plugin start, the host can scan and recycle registered lyrics. The bytes survive only in the backups.
  - An album stays partially unregistered until recovery runs.
  - The backup directory must itself be scan-excluded and survive host cleanup. Here it sits beside the library in disposable storage.
- **Scan-free conditions unproven:** the cascade and the recycling are host behaviour the plugin can't prevent. Nothing here shows a stock-supported way to stop a scan between detach and reattach. No command-queue or `RequiresDiskAccess` interaction was exercised.
- **Not exercised:**
  - other event subscribers, metadata and other extra importers;
  - real naming;
  - multi-disc or duplicate-recording albums (identity includes medium and release-track MBID, but only a one-disc fixture was run);
  - release switching beyond the negative case;
  - concurrent user edits during recovery, beyond the per-step re-read.
- **Durability limits:**
  - no power loss or directory fsync;
  - no SMB/CIFS;
  - the no-overwrite rename relies on .NET's Unix `link()`/`unlink()` behaviour;
  - a torn final journal line is tolerated only because the journal is advisory.
- **Sticky review means a human must act.** No operator workflow exists. The reconciler is not production code, and nothing here wires writes into a plugin.

**Production write-enabled remains NO-GO.** This increment shows that this protocol can recover from real process kills at two specific points under a stock rescan. It does not show the protocol is safe unattended.
