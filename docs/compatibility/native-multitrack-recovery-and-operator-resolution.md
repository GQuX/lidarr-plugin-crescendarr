# M19: multi-track crash/rescan recovery, protective salvage, operator resolution (issue #50, draft PR #53)

**DRAFT, test-only, disposable CI hosts. Production write-enabled stays NO-GO.**

**Host:** the official SHA-256-pinned Lidarr v3.1.6.5079 daemon with exactly one Crescendarr plugin, the stock Usenet Blackhole client with CDH OFF, synthetic tagged audio and a fresh SQLite database. **Every lane below runs on the real host; none uses a fake.** Checks read rows from a copy of `lidarr.db` (+WAL) and bytes from disk.

**Evidence:**
- **Red before fix:** run [45157210745](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45157210745) on `62d9040`. All four lanes failed on the M18 code.
- **Green:** run [45158883877](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45158883877) on `54ee0c8`, 4/4. On the same head, M18 [45158883850](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45158883850) is 7/7, M17 [45158885065](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45158885065) is 4/4, and the journal lab [45158883934](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45158883934) is green.

## Outcome for the multi-track crash lane: PROVEN (unambiguous) / SAFE-REVIEW (ambiguous)

| Lane | Checks | Result |
|---|---|---|
| `twelve-postmove-recover` | 85 | **PROVEN, Completed.** The daemon was SIGKILLed inside stock Import after moving track 6 of 12 (no row yet). A real stock rescan then re-imported all 12 files with fresh rows (`CASCADE_ROTATED tracks=[1..12]`). Recovery recorded `REBASE_ORIGINAL` evidence for the unstarted tracks 7–11 (old rows 7–11, current rows 25–29); the immutable binding still holds the seeded IDs. It restored every recycled lyric from its verified backup and upgraded tracks 7–11. **All 12 lyrics** are present with their original SHA-256 and registered once to the verified current row. Replay, restart replay and a further rescan changed nothing. |
| `twelve-postmove-ambiguous` | 159 | **SAFE-REVIEW, then resolved.** Same crash and rescan, plus a foreign album track aliasing track 8's rescanned row. Recovery entered **sticky RequiresReview** and ran **no** further native import. Protective salvage restored every recycled lyric and registered all of them except track 8's, whose row is ambiguous. Tracks 7–11 kept their original MP3-192 bytes, no audio link changed, and the summary names the reason. **Operator workflow:** `resolve` was refused while the alias existed. After the alias was removed, a `salvage-lyrics` proposal was approved and bound lyric 8. Replaying the same token was rejected. `resolve` was then allowed and applied, writing `resolution.json` with `importCompleted=false` and `downloadSettled=false`; the outcome stays RequiresReview, and the journal and backups were kept. A **new** journaled import then finished tracks 7–11, and both operations are retained. |
| `three-postrow` | 32 | SIGKILL **after the stock row insert, before `SetFileIds`**. This uses the additive seam `IHandle<TrackFileAddedEvent>`, which `MediaFileService.AddMany` publishes before `ImportApprovedTracks` links the track. The DB copy shows the row at the destination and the track unlinked; the subsequent stock rescan left it unlinked (`POSTROW_AFTER_SCAN track1=[0]`). Recovery stopped at OperatorApprovalRequired with zero changes. The typed `link-placed-row` approval linked exactly that row, and recovery completed. |
| `three-lyric-crash` | 37 | Post-move crash and rescan, then the recovery command was SIGKILLed **after restoring a lyric**, and again **after binding it**. Each crash was followed by a restart and a stock rescan. Recovery completed, and was idempotent on replay, restart and rescan. |

## What changed in the one journal and the plugin

- **Rescan rebase evidence** (`TrackProgress.ObservedOriginalTrackFileId`, codec v4). It is recorded only for a replacement whose native step never started, and only when all of these hold:
  - the recorded original row is gone;
  - the track is linked to exactly one row at the original path;
  - the bytes there are the exact original bytes;
  - no other track in any album links that row, and no other row shares the path;
  - the ID is unused by any other binding;
  - the whole operation reconciles consistently with the evidence applied.

  The binding and its digest never change. Anything ambiguous goes to review.
- **Early non-destructive restore.** A missing lyric that is unregistered and has a verified backup is restored immediately, because stock recycles only registered extras. Registration still waits until all bound audio is linked.
- **Every backup verified before each destructive step**, not only those of missing lyrics.
- **Protective salvage** (`Reconciler.Salvage`, run automatically when review is entered, or by operator approval). It is lyric-only and never overwrites. It binds only to a **verified current row**: an acceptable path, exact bytes and a sole owner. It is not applicable at all if the album, edition, artist root or paths are ambiguous.
- **Operator commands** (stock queued, `RequiresDiskAccess`):
  - `CrescendarrOperatorStatus` writes `crescendarr/status.json`.
  - `CrescendarrPropose` is a dry run. Its token is bound to the operation ID, journal generation, action and **fresh snapshot digest**.
  - `CrescendarrApprove` re-proposes from a fresh snapshot and applies only on an exact token match. Everything is recorded in `audit.log`.
  - The actions are `register-placed`, `link-placed-row`, `salvage-lyrics` and `resolve`.
- **`resolve`** requires `Reconciler.AlbumConsistency` to be empty. It releases the album block, deletes nothing, and is not import completion or download settlement. **The documented "delete the operation folder" workaround is withdrawn.**
- **`RegisterPlaced`** now requires exactly one `Imported` result, a row at the destination and unchanged audio bytes. The old reusable approve string is gone.

## Remaining risks
- **Unexercised crash points:** a crash during the byte transfer itself, during the stock rescan, and during a stock `SetFileIds` that partially links a multi-track file.
- **Out-of-queue writers** between the last re-read and the write are detected on the next snapshot, not prevented. There is no lock across restarts.
- **Retention:** backups and journals are kept indefinitely. There is no tested retention or cleanup policy yet, by design.
- **Untested:** rename-enabled naming, real metadata, other clients, SMB/NAS and power loss.

**Can user-facing automation (M20) start?** Yes, for the disposable host. Recovery and review now fail safe in the multi-track case, and every block has an explicit, non-destructive operator path.
