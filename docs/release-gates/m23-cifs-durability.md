# M23: kernel CIFS (SMB 3.1.1) storage and interrupted-import durability (issue #58, draft PR #59)

**DRAFT, disposable CI only. Production write-enabled stays NO-GO. No real NAS, SMB account or media.**

## Stage 0: where Crescendarr reads and writes (from the code, `src/Crescendarr.Lidarr.Plugin`)

| Data | Path | Writer | M23 lanes |
|---|---|---|---|
| Download source (completed job) | client output folder, under `<root>/downloads` | the download client; Crescendarr only **reads** and hashes it, and never moves or deletes it | local |
| Candidate staging | `<root>/crescendarr/staging/<op>/` | `Disk.CopyVerified` (temp file, `Flush(true)` = fsync of the file, then `File.Move`/rename, then re-read hash) | local |
| Operation journal | `<root>/crescendarr/ops/<op>/journal.json` | `Disk.WriteDurable` (temp, fsync, rename) on every transition | local |
| Album-lyric and audio backups | `<root>/crescendarr/ops/<op>/album-lyrics/*.lrc`, `audio/`, `track-lyrics/` | `Disk.CopyVerified`, written and verified **before** the journal is published, i.e. before any destructive step | local |
| Operator state | `outcome.json`, `review.json`, `proposal.json`, `resolution.json`, `audit.log`, download registry | `Disk.WriteDurable` | local |
| Library (artist folder, owned audio and `.lrc`) | stock root folder | **stock** `ImportApprovedTracks` / `UpgradeMediaFileService` / `TrackFileMovingService` (copy+delete across filesystems, rename within one); Crescendarr writes only lyric restores (`CopyVerified`) | **kernel CIFS** |
| Stock recycle bin | configured recycle folder | stock `RecycleBinProvider` (a move: cross-filesystem copy+delete from CIFS to local) | local |
| Lidarr SQLite (rows and links) | `<lidarr-data>/lidarr.db` (+WAL) | stock repositories | local |

**What the durability primitives guarantee:**
- `Flush(true)` asks the kernel to fsync the **file** before the rename.
- .NET does not fsync the **parent directory** after the rename, so the new directory entry may be lost on power loss even on local disk.
- On CIFS, an fsync becomes an SMB2 FLUSH that the server acknowledges. Whether that reaches stable media depends on the server's own configuration (`strict sync`, the underlying filesystem, disk and controller write caches), which **cannot be proven from the client**.
- **Nothing in M23 tests physical power loss.** SIGKILL, SMB server SIGSTOP/SIGKILL and unmounting are process and mount events only.

**Write boundaries the reconciler must survive** (all exercised against the real stock importer since M18/M19): before the move; post-move/pre-row; post-row/pre-link; during lyric restore and rebind. The album-lyric backups are materialized locally before any destructive step, so they stay outside the CIFS failure domain of the library being overwritten.

## Test infrastructure (`tools/experiments/cifs_fixture.py`, `.github/workflows/m23-cifs-durability.yml`)

- **Server:** Ubuntu 24.04 archive `samba` running as `smbd --foreground`, bound only to `127.0.0.1:445`, minimum protocol SMB3_11, `strict sync = yes`, one share exporting a fresh runner temp folder. The account is random per run.
- **Client:** `mount -t cifs //127.0.0.1/crescendarr <root>/library -o vers=3.1.1,soft,echo_interval=2,retrans=1,actimeo=1,serverino,uid,gid,file_mode=0664,dir_mode=0775`. Every lane checks `findmnt` reports `cifs` and records `/proc/mounts`, the negotiated dialect, and the kernel/cifs.ko/smbd versions.
- **Faults:** deterministic.
  - Plugin-owned fault points (`post-move-<track>`, `after-native-N`) pause the daemon at an exact boundary.
  - The harness then applies SIGSTOP/SIGCONT to `smbd` (server unreachable), unmounts lazily (the path becomes an empty local directory), remounts read-only, or SIGKILLs Lidarr.

## Environment actually exercised

| Item | Value |
|---|---|
| Lidarr | official v3.1.6.5079 linux-core-x64, SHA-256 `fed16c58...` |
| Kernel / CIFS client | GitHub ubuntu-24.04 images, kernel 7.0.0-1014-aws and 7.0.0-1017-azure, `cifs.ko` 2.59, mount.cifs 7.0 |
| SMB server | Samba 4.19.5-Ubuntu (`smbd -D`), loopback only, `server min protocol = SMB3_11`, `strict sync = yes` |
| Mount (from `/proc/mounts`) | `cifs rw,vers=3.1.1,cache=strict,soft,serverino,actimeo=1,closetimeo=1,echo_interval=2,retrans=1,rsize=4194304,wsize=4194304` |
| Placement | library on CIFS; journal, lyric backups, staging, recycle bin, Lidarr DB and download source local (asserted with `findmnt` in every lane) |

**Final head `16e02d1`:** M23 [45208473594](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45208473594) 6/6. All M17-M22 suites and the specs are green on the same head (37/37 jobs).

## Defects found on real CIFS and fixed (each failed before its fix)

1. **An unmounted share looked like "every file is missing."** In red run [45194111054](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45194111054), recovery went sticky RequiresReview, and protective salvage **created the artist folder and a lyric inside the bare local mountpoint**. `File.Exists` returns false on I/O errors and on a missing mount.
   - **Fix:** each operation records its library mount identity (`fstype|source|mountpoint`).
   - Observation is *unavailable* when that identity changes, the artist folder is unreachable, or any path errors (`Disk.Probe` tells absent from error).
   - An unavailable observation gives the non-terminal **StorageUnavailable** outcome: no review, no salvage, no writes, and the album stays blocked.
2. **A frozen SMB server could still end in Completed.** This first looked so in run 45194111054, but there the harness had not frozen the per-connection smbd child (Samba retitles it), so that observation was invalid. The class of risk is real either way: a client page cache can answer for an unreachable server.
   - **Fix:** a durability barrier fsyncs every relied-on library file, which on CIFS is an SMB2 FLUSH round-trip, before CompleteOperation.
   - Any I/O failure gives StorageUnavailable.
3. **A read-only library was not refused up front.** The native import ran and ended in review (run 45194111054).
   - **Fix:** import preflight now requires a reachable artist directory and a durable write probe.
4. **The next stock rescan re-imported the whole album and unregistered every lyric** (runs 45204837904 and the `f9030c5` run).
   - Right after import, the client reported the imported files' mtimes equal to their rows. Eight seconds later the server reported them about 2.4 s later: the kernel's deferred close (`closetimeo`) flushed buffered writes.
   - Stock records `TrackFile.Modified` at import (`ImportApprovedTracks.cs:193`) and treats a >1 s mtime difference as a changed file (`MediaFileService.FilterUnchangedFiles`). So the next `RescanFolders` cascaded across the album, superior FLAC included.
   - **Fix:** after the flush, the barrier waits out this mount's `actimeo`/`closetimeo` windows, records the **settled** mtime in the rows this operation created (Modified only, through `IMediaFileService.Update`), and requires the mtime to stay stable. Otherwise the outcome is StorageUnavailable.
   - **Wrong first diagnosis:** I first suspected the cross-filesystem move. A diagnostic showed the move kept the mtime, so that fix was **reverted**.
   - **The fixture had the same effect:** the seeder now records settled mtimes, after `setattr` was shown not to stick under deferred close.

**Stock limitation this exposes (not Crescendarr-specific):** any process, Lidarr itself included, that writes a file on a CIFS library and immediately records its mtime can leave a row more than 1 s stale. The next stock rescan then re-imports the album and recycles registered lyrics. Crescendarr now settles rows it creates, but it **cannot** protect rows stock writes on its own.

## Acceptance table

| Case | Status | Evidence |
|---|---|---|
| 3-track selective import on kernel CIFS: superior FLAC row+SHA+lyric kept, lyric relinked, missing track added, edition kept, no lyric recycled, rescan no-op, restart replay | **PROVEN** | `cifs-three` (23 checks) |
| 12-track selective import on kernel CIFS (11 upgrades), rescan no-op after settle, restart replay | **PROVEN** | `cifs-twelve` (52 checks; 5 rows settled) |
| SIGKILL inside stock Import after the move onto CIFS (pre-row); restart; real stock rescan cascade; recovery restores and rebinds lyrics; replay | **PROVEN** | `cifs-sigkill-rescan` (33) |
| SMB server unreachable (every smbd SIGSTOPped, asserted state T) at the post-move boundary: outcome **StorageUnavailable**, never Completed; after SIGCONT, recovery reconciles to Completed with every byte, row and lyric verified | **PROVEN (SIGSTOP model)** | `cifs-server-loss` (24) |
| Share unmounted after a native write (mountpoint becomes an empty local directory): nothing written there, not sticky, StorageUnavailable naming the mount change; after remount, recovery completes | **PROVEN** | `cifs-unmounted` (28) |
| Read-only remount: refused before any destructive step ("library is not writable"), zero change | **FAIL-CLOSED** | `cifs-readonly` (9) |
| SMB server SIGKILL+restart mid-copy, partial/short writes, ENOSPC/quota, permission denial, stale mount, symlink/hardlink/bind on CIFS, preexisting destination on CIFS | **NOT TESTED** | - |
| Faults during lyric restore/rebind specifically on CIFS | **NOT TESTED** on CIFS (proven locally in M19) | - |
| Recycle bin on CIFS (stock recycle within one share) | **NOT TESTED** (recycle is local here) | - |
| Journal and backups colocated on CIFS | **NOT TESTED** (deliberately local) | - |
| Whole-VM / host power cut, NAS disk or controller caches, directory fsync | **NOT TESTED.** Nothing here models physical power loss; SIGKILL and SIGSTOP are not power loss | - |

**Carried forward from M22, unrelated to CIFS:** the one intermittent SABnzbd "reported storage path" check failure on `61391e0` is still unexplained.

**Production write-enabled remains NO-GO.** No NAS install instructions are published, and the CI-only guard is unchanged.
