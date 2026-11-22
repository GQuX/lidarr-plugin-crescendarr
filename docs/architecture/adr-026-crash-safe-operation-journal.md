# ADR-026: Crash-safe per-track operation journal and disposable recovery lab (M13)

**Status:** DRAFT for independent review. Lab only. **NO-GO for any write-enabled plugin**: host transactionality is unproven.
**Issue:** [#35](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/35). **Date:** 2026-11-22.
**Evidence it is built on:** [native-sixth-transfer-failure](../compatibility/native-sixth-transfer-failure.md), [native-two-edition-sqlite-import](../compatibility/native-two-edition-sqlite-import.md), [native-event-dispatch-sqlite](../compatibility/native-event-dispatch-sqlite.md), [native-cross-process-sqlite-recovery](../compatibility/native-cross-process-sqlite-recovery.md).

## Decision

Three separate parts:

1. **`src/Crescendarr.Core/Journal`.** Pure code with no I/O, clock, host types or network:
   - `JournalValidation`: strict binding validation.
   - `JournalEngine`: forward-only transitions.
   - `JournalCodec`: canonical, strict, size-capped encoding.
   - `Reconciler`: classifies a saved manifest against fresh abstract host and filesystem snapshots.
2. **`src/Crescendarr.RecoveryLab`.** A disposable *local* executor:
   - It refuses to start without `CRESCENDARR_RECOVERY_LAB=DISPOSABLE_LOCAL_ONLY`. The root must also be a fresh, marked directory under the system temp directory. It also refuses Windows network drives, and Linux `cifs`/`smb3`/`nfs`/`sshfs`/`9p` mounts.
   - It performs atomic manifest saves, verified backups, no-overwrite restores and observation.
   - `FakeHost` is a one-JSON-file stand-in for the database. It follows the stock delete-before-move ordering.
3. **`tests/Crescendarr.Journal.Spec`.** Pure specs, an in-process crash/failure matrix over every fault point, and real SIGKILL of separate lab processes.

None of this touches Lidarr, SQLite, a NAS, SMB, a downloader, `RemoveItem`, `IImportApprovedTracks` or `IUpgradeMediaFiles`. It is a future interface plus a lab, not an adapter.

## Immutable binding (invariant 1)

Each operation binds the following. Its canonical SHA-256 (`BindingDigest`) is written once to `binding.sha256` before the first manifest, and every later generation must match it.

- **Operation:** a canonical lowercase UUID; it is also the directory name.
- **Download:** optional client definition ID, download ID and inventory evidence digest.
- **Album and edition:** album ID, release-group MBID, and the monitored release ID plus **MusicBrainz release UUID**.
- **Roots:** library, staging and operation, which must not overlap.
- **Per track:**
  - track ID and recording MBID;
  - the original track-file ID (must be positive; multi-track files are rejected);
  - original, destination and incoming paths;
  - original and incoming SHA-256 and length;
  - the backup path;
  - the registered lyric: ID, host relative path, physical path, SHA-256 and length, and backup path.

All paths are canonical, bounded, under their root, and must not alias each other even case-insensitively. Lyric relative paths reject `..` with either separator on Windows. That defect was found by the specs.

## State machine

Per-track phases advance only through `JournalEngine`, one step at a time:

```
Planned --CopyBackup--> BackupVerified --DetachLyric--> LyricDetached --NativeUpgrade--> Upgraded --RelinkLyric--> LyricRelinked
                         (all tracks must reach BackupVerified before ANY DetachLyric/NativeUpgrade)
operation: Active --> Completed (every track final) | RequiresReview (sticky)
```

**Write-ahead protocol for every step S.**
1. Save the manifest with `InFlight = S`: temp file, `Flush(true)`, rename, then directory fsync on Linux.
2. Perform S.
3. Verify by observation.
4. Save `MarkDone(S)`.

There is never more than one step in flight per operation, and the executor holds an exclusive album lock file. If a save fails, the earlier generation stays authoritative and S is never attempted. A backup counts only after it is re-read and its SHA-256 and length match exactly.

## Reconciliation (invariant 3)

`Reconciler.Reconcile(manifest, HostSnapshot, FilesystemSnapshot)` returns one guarded `NextAction` and a verdict: `Proceed`, `OperatorApprovalRequired`, `RequiresReview` or `Completed`. The executor re-observes host and disk before **every** action, and executes only what is returned.

Immediately before the destructive native call, it re-reconciles once more after declaring the step. It calls the native upgrade only if the result is still "native not started".

**Success is never inferred from absence.** An interrupted native step counts as done only with positive evidence:
- the incoming bytes are at the destination;
- exactly one fresh row exists at that path and is linked;
- the old row is gone.

### What can be resumed automatically, and what needs an operator

| Interruption (track in flight) | Observed condition | Action | Mode |
|---|---|---|---|
| intent / manifest generation 1 not yet published | no journal | nothing happened; host untouched (asserted) | n/a |
| during or after backup copy, before publish | `BackupPending` | re-copy into operation-owned path, re-verify | **Automatic** |
| backup published | `BackupVerified` | continue | **Automatic** |
| lyric detach in flight | row present: re-delete; row absent and bytes intact: record | idempotent | **Automatic** |
| native declared, not yet called | original, row and link intact, incoming staged | abandon step (attempts capped at 3, then review) and re-run | **Automatic** |
| after stock recycle, before row delete | `OriginalMovedRowIntact` | restore original bytes from verified backup (no overwrite), then retry | **Automatic** (filesystem only) |
| after stock row delete, before move | `OrphanedOriginal*` | restore bytes (automatic), then **insert a FRESH track-file row** for the original and relink. Lyric re-registered as a fresh row. Ends in RequiresReview | **Operator approval** |
| after move, before new row | `IncomingPlacedUnlinked` | **insert a FRESH row** for the incoming file and relink, then continue | **Operator approval** |
| after the new row is linked | `Upgraded` | record done | **Automatic** |
| relink in flight | row present: record; absent: insert | idempotent | **Automatic** |
| completion not yet published | every track final | complete | **Automatic** |
| anything unexplained | `Inconsistent` | record review; nothing else | review |

The deleted original's numeric track-file ID is **never reused**. `MarkDone(NativeUpgrade)` rejects it, and every recovery inserts a fresh row.

## Global blockers (invariant 5)

Any of these yields `RequiresReview` and zero automatic actions other than recording the review:

- fresh host data or the mount is unavailable;
- album changed;
- not exactly one monitored release, or the monitored MusicBrainz release changed;
- recording MBID changed;
- the original track-file ID now points at another path (stale or reused ID);
- an unbound lyric row is linked to an original file, which the stock delete handler would recycle;
- a missing observation for any bound path;
- any bound path traverses a link or reparse point;
- original, incoming, lyric or backup bytes differ;
- a conflicting destination file;
- a lyric row reappeared;
- duplicate rows.

Review is sticky: restoring the edition afterwards does not resume automatically.

## Persistence and corruption (invariant 6)

- **Limits:** the manifest is capped at 1 MiB and 200 tracks, with bounded review reasons.
- **Strict decode.** Decoding rejects all of the following:
  - truncation or an empty file;
  - unknown or duplicate properties, numeric enums, a wrong format or version;
  - a digest mismatch;
  - more than one step in flight;
  - a destructive phase before every backup is verified;
  - Completed with unfinished tracks, or review without a reason;
  - a phase without its fresh ID;
  - a binding that differs from `binding.sha256`;
  - a manifest stored under the wrong operation directory.
- **No silent reset.** A new operation is refused while any existing operation is unreadable, or while the album has an unfinished one. A duplicate operation ID is refused.
- **Temp files.** Stray temp files from interrupted saves are reported and never read as state.
- **The SHA-256 values are unkeyed.** They detect accidental corruption, **not** malicious tampering with a recomputed hash.

## Evidence

All of the evidence below comes from `tests/Crescendarr.Journal.Spec`:

- **Fault points.** The happy path passes 174 named fault points: every step, every manifest and fake-host save (`beforeFsync`, `beforeRename`, `afterRename`), and the mid-copy point of every backup. Both of these runs go through every point:
  - an in-process crash at each point;
  - an injected `IOException` (failed fsync, rename or transfer) at each point.
- **After every fault, the safety invariants are asserted:**
  - the protected superior track keeps its bytes, row and lyric;
  - every physical `.lrc` keeps its bytes;
  - every original is in place or in a verified backup;
  - incoming bytes are not lost;
  - the monitored edition is unchanged.
- **Recovery behaviour.** Automatic recovery completes or stops at the expected operator proposal. Unapproved recovery is byte-for-byte inert. Approved recovery reaches a consistent state, and a repeat recovery executes nothing.
- **Kill-and-reopen.** Real separate processes are killed with SIGKILL at:
  - backup mid-copy;
  - after lyric detach;
  - after stock recycle;
  - after row delete;
  - after move;
  - after lyric insert;
  - just before a manifest rename.

  Two further separate processes then reconcile and recover, and a third recovery executes nothing.
- **Concurrent change (L14).** A change injected *between* the executor's last observation and the native call is caught by the pre-call revalidation, and the native upgrade is never invoked. Three changes are injected: original edited, destination occupied, edition switched.
- **Specs catch reintroduced defects.** Each of these was reintroduced and confirmed to fail specs:
  - "native allowed before every backup" fails J02 and L02;
  - "orphaned row auto-retried" fails L02;
  - "executor skips pre-call revalidation" fails L14. Before L14 existed, **no spec caught it**.
- **Targeted cases:** edition switch, tamper matrix, corrupted backup, manifest corruption and duplication, stray temp files, the fake-host lyric hazard, and symlink escape (Linux CI only; recorded as `NOT_EXECUTED` where symlinks are not permitted).

## Remaining limits (treat as NO-GO until each is addressed)

1. **FakeHost is not Lidarr.** It is one JSON file with one atomic write per "statement", and it encodes only the native-proven ordering. Real SQLite transactions, the full host event fan-out, `DiskScanService` and the real naming and tagging pipeline are not exercised. The future host adapter must prove that "insert fresh row, `SetFileIds` and lyric row" happens in one verified host transaction, or keep these as operator actions.
2. **The time-of-check/time-of-use (TOCTOU) window is narrowed, not closed.** The re-observation and pre-call re-reconcile shrink it, but the stock host can still change state between the last observation and the native call. The album lock only serializes Crescendarr; it cannot stop Lidarr's own commands, rescans, manual imports or other tools. The adapter must run inside Lidarr's disk-access command lock (`DownloadsProcessedEvent`) and accept this residual window.
3. **Durability is proven only for a local Linux filesystem**, and only in the sense "fsync and rename survived SIGKILL". Power loss was not tested. Windows runs skip the directory fsync. Read-back verification may be served from the page cache. Nothing here says anything about SMB/CIFS, where flush, rename and lease semantics depend on the server.
4. **Where the journal and backups live in production is undecided.**
   - Backups on the library filesystem share its failure domain.
   - Backups on `/config` cost a full copy over SMB.
   - The lab puts both on one local disk.
5. **Stock behaviour the adapter must still enforce:**
   - The native importer calls `SetMonitored` before moving. An adapter must pin and pre-verify the edition, as the native two-edition test showed. The journal only detects the change afterwards.
   - Stock renaming or tagging may change the destination path or bytes. The binding assumes a known destination and unchanged incoming bytes; a real adapter would observe a mismatch and stop for review.
6. **Recycle copies are never cleaned, and backups are retained indefinitely.** A retention and cleanup policy, including operator acknowledgement of review states, is future work.
