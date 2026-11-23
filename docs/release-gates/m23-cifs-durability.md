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

Results, defects and the acceptance table follow below once CI has run.
