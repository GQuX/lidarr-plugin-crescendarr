# M18: crash inside the native import, album-wide lyric recovery and fresh-row sharers (issue #48, draft PR #49)

**DRAFT, test-only, disposable CI hosts. Production write-enabled stays NO-GO.**

**Host:**
- The official SHA-256-pinned Lidarr v3.1.6.5079 Linux binary, run as a real daemon.
- Exactly one Crescendarr plugin assembly (the separate seeder runs only in the setup boot).
- Stock Usenet Blackhole client with Completed Download Handling OFF.
- Synthetic tagged audio and a fresh SQLite database.

**Evidence:** every check reads DB rows from a copy of `lidarr.db` (+WAL) and file bytes from disk.
- **Red before fix:** run [45142507643](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45142507643) on `045f591`. Its lanes run against the M17 executor logic, with only the observation hook added.
- **Green:** M18 run [45146944203](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45146944203) on `fcaa04b`, all seven lanes. Final head `80746e6`: M18 [45147884561](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45147884561) 7/7, M17 [45147884558](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45147884558) 4/4, journal lab [45147884572](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45147884572) 35/35.

## What is actually intercepted inside stock Import

Stock `ImportApprovedTracks.Import` runs these steps for each track:

1. `UpgradeTrackFile` recycles the old file and deletes its row. That deletion unlinks the track and recycles any linked extras. It then **moves** the new file into place and writes tags (`ImportApprovedTracks.cs:240`).
2. `IExtraService.ImportTrack` calls `IManageExtraFiles.CreateAfterTrackImport` on every registered manager (`ImportApprovedTracks.cs:272`, `ExtraService.cs:57,123`).
3. Only after the loop does `AddMany` insert the rows, and `SetFileIds` links the tracks (`ImportApprovedTracks.cs:317-320`).

The plugin registers an observe-only `IManageExtraFiles`, which stock DI resolves into that enumerable. When the import was started by Crescendarr, the hook records the boundary and hosts a test-owned, one-shot pause.

**Exercised on the real daemon:** every post-move lane proved, from a DB copy taken at the pause, that:
- the moved file is at the library path with the incoming bytes;
- no `TrackFiles` row exists for it;
- the track is unlinked;
- for an upgrade, the original row is already deleted and the original bytes are in the recycle bin.

The harness then SIGKILLs the daemon. Stock Orphans the plugin command on restart.

**Not intercepted:** a crash *during* the byte transfer itself (a partial file), and a crash between `AddMany` and `SetFileIds`.

## Lanes and results

| Lane | Checks | What happened |
|---|---|---|
| `three-postmove-upgrade` | 32 | SIGKILL in the window during track 1's upgrade. A real stock `RescanFolders` ran **before** recovery. **The album-wide cascade happened:** the unselected superior FLAC got a fresh row (2 → 5), its bytes stayed in place, and its registered `.lrc` was **physically recycled**. Recovery recorded the upgrade from the scan-created row, relinked track 1's detached lyric and journaled the add of track 3. It then **restored `02 … .lrc` from its verified backup** (no overwrite) and **registered it to the FLAC's fresh row 5**. Outcome Completed. Replay, restart replay and a further rescan changed nothing. |
| `three-postmove-add` | 32 | SIGKILL in the window during the **missing-track add**. The journal showed the add **in flight**, not complete. The rescan cascade rotated rows 1 → 5, 2 → 6 and 3 → 7, and recycled both registered lyrics. Recovery recorded the add from positive evidence, then restored and rebound **both** lyrics to the current rows. Completed, with replays as above. |
| `three-postmove-noscan` | 39 | The same upgrade crash, but no scan. Recovery stops at **OperatorApprovalRequired** with zero row, link, lyric or byte changes, and writes `review.json`. A new import for the album is **refused** ("unfinished operation"). With the approval token, recovery registers **only the placed file** through the stock existing-file decision, then completes. **No cascade:** the FLAC keeps its original row and **no lyric is ever recycled**. |
| `three-alias` | 16 | Paused after track 1's native upgrade. The harness linked a foreign album's track to the **fresh** row. On the next snapshot the global sharer query found it (`SNAPSHOT_GLOBAL_SHARERS … foreignTracks=[9->4]`), giving **RequiresReview**. The add was not imported, the lyric was not relinked and the FLAC was untouched. The foreign tracks that legitimately share a foreign row stayed untouched, as did the foreign tracks with `TrackFileId=0` or a dangling ID. The review is sticky. |
| `three-lyric-negatives` | 77 | From the post-scan state, each case is replayed from a snapshot of the whole disposable root. Each ends in **sticky RequiresReview** with zero row, link, lyric or library-byte changes: conflicting bytes at the recycled lyric path (**not overwritten**), corrupt backup, missing backup, a dangling nonzero `TrackFileId` on the pending add, and a stale lyric row pointing at a deleted TrackFile. The unmodified control **completes**, and identical replay plus restart replay plus rescan change nothing. |
| `three-guards` | 45 | Each case is refused **before the first write**: no operation published, nothing written outside the root, no row or byte change. The cases are a dangling symlink at a registered lyric, a download candidate linked to a file outside the root, the artist folder aliased to an out-of-root copy, a staging folder linked outside the root, and a **dangling symlink at the add destination**. The control import then completes. |
| `twelve-postmove` | 33 | SIGKILL in the window at track 6 of 12, then a rescan. The cascade re-imported all 12 files with fresh rows and **unregistered all 12 album lyrics**. Tracks 1–6 were already upgraded; tracks 7–11 were still pending; 12 is the superior FLAC. Recovery ends **RequiresReview** ("another track-file row refers to the original or destination path" for the pending tracks). For every one of the 12 lyrics, either the original bytes are on disk or the album backup is hash-verified, and nothing was overwritten. **This is the remaining fail-closed residual.** |

The M17 lanes (`three`, `twelve`, `three-crash`, `three-review`) stay green on the reworked plugin ([45147884558](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45147884558)).

### What failed before the fix (red run 45142507643)

- **Upgrade crash:** recovery Completed the upgrade and the add, then stopped at RequiresReview with the unselected FLAC's lyric still **recycled**, because no restore path existed.
- **Add crash:** the M17 journal already said **Completed** while the add was in flight. Recovery logged `RECOVERY_ADD_ALREADY_LINKED` (it trusted `TrackFileId > 0`) and ended in review with lyric 1 recycled.
- **Alias:** the operation **completed** despite the foreign alias of its fresh row.
- **Guards:** a dangling lyric symlink was refused only after album-lyric backups had already been written into a new operation folder.
- **No-scan and twelve-track crashes:** no operator summary existed.

## Algorithm (one journal, M13 format v3)

**Bindings.** The binding covers:
- replacements, with album-wide backups first;
- additions;
- kept tracks (bytes plus a link to a row at their path);
- every album lyric (relative path, bytes, verified backup).

**Identity after a host scan** is track + recording + path + bytes + current link. Stored row IDs are never trusted: PR #42 showed every ID changes in the cascade.

**Order.** Additions run only after every replacement is final. Album lyrics are restored and registered only once **all bound audio is linked**, because PR #42 showed that lyrics registered while an audio file is unregistered are recycled again by the next scan.

**Sharer projection.** Every snapshot queries `GetTracksByFileId` for the union of:
- original IDs;
- recorded fresh IDs;
- every bound track's **current** link;
- all album rows;
- path-resolved rows.

ID 0 is excluded. Rows referenced by lyrics are loaded too, so stale rows are visible.

**Fail closed.** Each of these blocks before any mutation:
- unrelated bytes at a lyric path;
- a missing lyric with a corrupt or missing backup;
- a stale or extra lyric row;
- a dangling link;
- changed protected bytes;
- an operation row shared with another track.

**Operator states.**
- `review.json` records hashes, TrackFile IDs, release, recording and release-track MBIDs, lyric rows, backup verification and reasons.
- `outcome.json` always carries `downloadSettled=false`.
- An album with an unfinished operation refuses new imports.
- Backups are never deleted on failure.

## Exact operator actions

- **OperatorApprovalRequired** (a file was placed without a row). Do one of:
  - dispatch `CrescendarrRecover` with `approve` = the token in `review.json`. This registers only that file, with no rescan and no lyric recycling;
  - or run stock `RescanFolders` for the artist, then `CrescendarrRecover`. The rescan cascades; recovery restores the lyrics from backups.
- **RequiresReview.** Nothing runs automatically for the album, and the download is not settled.
  - Inspect `review.json`.
  - Missing lyrics can be copied back from `crescendarr/ops/<op>/album-lyrics/<id>.lrc`; the summary gives each file's hash and verification.
  - Rows must be fixed by a human.
  - **M19 replaces this:** use `CrescendarrPropose`/`CrescendarrApprove` (`salvage-lyrics`, `resolve`). Never delete the operation folder; it holds the only independent backups.

## Still unsafe or unproven

- **Mid-album crash plus rescan on a multi-upgrade album** (`twelve-postmove`) ends in review, with album lyrics unregistered and some physically absent until an operator restores them. Bytes are safe in verified backups. Automatic continuation for still-pending tracks whose rows were rotated is not implemented.
- **Crash points not exercised:** during the byte transfer, between `AddMany` and `SetFileIds`, and during the stock rescan itself.
- **Unattended window:** between a crash and the next plugin run, a scheduled stock scan can cascade. The backups make that recoverable for the 3-track shapes above, not for every shape.
- **Environment:** naming is the stock default only, with no rename-enabled lane, and no real MusicBrainz metadata. No other download clients, no SMB/NAS, no power loss or directory fsync.
- **Out-of-queue writers:** the alias test proves detection at the next snapshot, not prevention. There is still no host-wide lock.
