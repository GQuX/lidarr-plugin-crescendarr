# ADR-026: Crash-safe per-track operation journal and disposable recovery lab (M13)

**Status:** DRAFT, revision 3 after two independent reviews of PR #37 (findings A, B and C, and the shared fresh-row finding, below). Lab only. **NO-GO for any write-enabled plugin**: host transactionality is unproven.
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
- **Roots:** library, staging and operation, which must not overlap, plus the host-authoritative **artist root** (Lidarr `Artist.Path`), which must be strictly under the library. Original, destination and lyric paths must be under the artist root.
- **Per track:**
  - track ID and recording MBID;
  - the original track-file ID (must be positive; multi-track files are rejected);
  - original, destination and incoming paths;
  - original and incoming SHA-256 and length;
  - the backup path;
  - the registered lyric: ID, host relative path, physical path, SHA-256 and length, and backup path. The physical path **must equal** the artist root joined with the registered relative path, mirroring stock `Path.Combine(artist.Path, extra.RelativePath)`.

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
- the host artist root is unknown or differs from the bound artist root;
- two host track-file rows, or two lyric rows, share a path **ignoring case**; or any host path is not clean NFC text;
- before the native step, any row other than the bound original at a track's original or destination path;
- a bound original track file linked to any other track;
- any row the operation created or relies on (the recorded fresh ID, or while the native step is in flight any non-original row at the track's destination or original path) linked to any other track;
- duplicate track IDs;
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

## Revision 2: independent review of PR #37 (findings A, B and C)

The review ([comment](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/37#issuecomment-7083292447)) flagged three code-path gaps. Each was first **reproduced** by a spec added in an expected-red commit (`61e06c2`) against the unfixed code. That run is Linux CI [45053369119](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45053369119): X01, X02, X03 and X04 failed, and X05 passed. Each was then fixed, and every fix was confirmed by putting the defect back and seeing its spec fail.

### A. Ancestor link redirected the lab sandbox (reproduced on Linux: X01, X02)

- **Root cause.**
  - Containment was lexical only.
  - `Directory.CreateDirectory` ran before the locality check.
  - Reopening inspected only the root's own link target.
  - Mount detection matched an unresolved path string.

  So `/tmp/alias/new-lab`, with `/tmp/alias -> elsewhere`, created a directory outside the sandbox. A lab whose `ops` directory had been replaced by a link would also write its journal through that link.
- **Fix: a narrower contract** (`LabSafety`).
  - The temp directory and **every ancestor** must be real directories with no symlink, junction or reparse point; otherwise the lab refuses to run.
  - A lab root must be a **direct child** of that temp directory, named `crescendarr-*`.
  - Locality is checked on the link-free temp directory **before** anything is created. The chain and locality are re-checked after a single-level create.
  - The marker is created with `CreateNew`.
  - A new root must not exist in any form, including as a dangling link.
  - Reopening re-verifies the chain, a regular marker file, and that `library`, `staging`, `ops` and `recycle` are not links.
  - Because the chain is link-free, the lexical path equals the physical path, so mount matching is meaningful. Missing mountinfo on Linux now refuses, and FUSE is refused.
- **Residual risk.** These are still check-then-use path operations, not handle-relative `openat`/`O_NOFOLLOW` operations. A hostile process running as the **same user** could swap a directory for a link between a check and a write. The lab trusts same-user processes. Other users cannot rename entries in a sticky-bit temp directory. **This is a reduced-risk sandbox for synthetic data, not a containment guarantee.**
- **Tests.**
  - **X01:** an ancestor alias, a root that is itself a link, a marked root behind a link, and a real lab reopened through an alias. All are refused, and the test asserts nothing was created or modified through the alias.
  - **X02:** the `ops` directory replaced by a link, and the lab guard refuses before any journal write.
  - Both need Linux symlinks and are reported as `NOT_EXECUTED` on Windows hosts that refuse to create them.

### B. A different row at a bound path could be classified as safe (reproduced: X03)

- **Root cause.**
  - `GlobalChecks` rejected duplicate row *IDs* but not distinct IDs at the same path.
  - The `NativeNotStarted` branch computed `originalPathRows` without using it.
  - The `Planned` phase relied on `originalIntact` alone.

  With a stray row at track 103's original path (its destination differs), the operation ran to `Completed`.
- **Fix.**
  - **Global rule.** Any two host track-file rows whose paths are equal **ignoring case** block, on every host, because a NAS may fold case under a case-sensitive grammar. The same applies to two lyric rows with case-variant relative paths.
  - **Clean NFC only.** Any host path that is not clean NFC text blocks, because ignore-case does not fold NFC and NFD spellings.
  - **Before the native step,** the only row allowed at a track's original or destination path is the bound original.
  - **Shared original file.** If any other track links to a bound original track file, the operation blocks, because the stock upgrader would orphan that track.
  - **Duplicate track IDs block.** Before this fix the reconciler *threw* on such a snapshot; it now returns review.
  - **`NativeNotStarted`** additionally requires no other row at the original path.
- **Tests.**
  - **X03** injects:
    - a duplicate row before the first step;
    - a case-variant row;
    - a row during recovery, with operator approval enabled;
    - a row immediately before the native call, through the interleave hook;
    - a case-variant row at an already-upgraded destination, which must block the lyric relink;
    - an unbound track sharing an original file.

    It asserts review and **zero** detach, native, restore or insert actions.
  - **R01** adds pure cases: duplicate, case-variant, destination-before-native, shared-file, duplicate-track-ID, case-variant-lyric and NFD rows.
  - Removing any one of the global collision, per-track, or shared-file checks makes X03 or R01 fail.
- **Limits.** Hardlinks, bind mounts and server-side aliases cannot be detected from path strings. ASCII and invariant ignore-case is not the full filesystem case-folding table. A future adapter must not treat "no collision found" as proof of uniqueness beyond these rules.

### C. The physical lyric was not tied to the registered lyric (reproduced: X04)

- **Root cause.** The binding accepted `Lyric.Path` and `Lyric.RelativePath` independently. Meanwhile the fake host, like stock Lidarr, resolves the registered lyric as `Path.Combine(artist.Path, extra.RelativePath)` (stock `ExtraFileService.cs:117` at v3.1.6.5079). The journal could therefore back up and verify a decoy while the real registered file stayed unverified. One X04 case deletes the real lyric entirely, and the old code still ran.
- **Fix.**
  - The binding now carries **`ArtistRoot`**, the host-authoritative `Artist.Path`. The manifest format is version 2, and version-1 manifests are refused.
  - Validation requires `ArtistRoot` to be strictly under the library, and the original and destination paths to be under `ArtistRoot`.
  - Validation also requires **`Lyric.Path == JoinUnder(ArtistRoot, RelativePath)`** exactly.
  - `HostSnapshot.ArtistPath` is compared against `ArtistRoot` on **every** reconciliation; unknown or different blocks.
  - The lab derives the physical lyric path from the host artist root plus the registered relative path, and never supplies it independently.
- **Adapter precondition (not implementable in the pure layer).** The future host adapter **must** read `Artist.Path` and `LyricFile.RelativePath` from the same fresh host observation, and supply both to the binding and to every snapshot. If it cannot obtain them authoritatively, it must not build a binding. This ADR does not assume any Lidarr path semantics beyond the cited stock join.
- **Tests.**
  - **X04:** a decoy lyric is refused at creation, with no operation directory, no host change, and a correct operation still able to start afterwards. A deleted real lyric is refused at creation. The artist root changing just before the native call leads to review with no native call.
  - **J01** gains mismatch, artist-outside-library and original-outside-artist variants.
  - **R01** gains artist-root-changed and artist-root-unknown cases.

### Additional defects found during this revision

- **`OperationStore.Create` created the operation directory before validating the binding.** A refused binding left an empty, unreadable operation directory, which by the "no silent reset" rule blocked every later operation for the album. The binding is now validated first. X04 covers it.
- **The reconciler threw on a contradictory snapshot** with duplicate track IDs, instead of returning review. Fixed, and covered by R01.
- **The spec runner reported a spec that returned early on an unsupported host as PASS.** It now prints `NOT_EXECUTED` and counts it separately (`JOURNAL_SPEC_TOTALS`).
- **Suspected, not reproduced:** a hardlink planted at the operation-owned backup path, followed by a backup resume that opens the destination for writing. That could truncate the original's inode. In X05, run against the old code, .NET's emulated sharing lock made the write fail first on both Windows and Linux. That lock is a .NET convention, not an OS guarantee, so copies now go to a fresh `CreateNew` temp file that is renamed into place. X05 asserts that the verified backup holds the original bytes and the operation completes.

## Revision 3: shared fresh track-file row (second review of PR #37)

The second review ([comment](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/37#issuecomment-7083721396)) suspected that a fresh destination row could be shared with an unrelated track without blocking reconciliation.

- **Reproduced.** Spec X06 was added fail-first in commit `cdaebb4`, against `775f656`. In every case the only host change was relinking a track to the fresh row; no file bytes, paths or IDs were duplicated.

  | Case | Setup | Unfixed result |
  |---|---|---|
  | 1. Interrupted native | Crash after `native.101.afterLink` (host linked, journal not yet recorded), then protected track 104 relinked to track 101's fresh row | reached `Completed` |
  | 2. Already upgraded | Crash after `native.101.afterPublish` (fresh ID recorded), same relink | reached `Completed` |
  | 3. Extra track | An unbound extra track linked to the recorded fresh row | reached `Completed` |
  | 4. Legitimate sharing | Two unbound tracks sharing the protected file, which the operation never touches | completed (correct) |

- **Root cause.** The isolation check in `GlobalChecks` covered only `OriginalTrackFileId`, and only before `Upgraded`. Case E (native observed complete) and the `Upgraded` and `LyricRelinked` checks validated the bound track's own link, but never asked whether another track also linked to that row.
- **Fix (smallest safe change, `Reconciler.GlobalChecks`).** Per-track isolation now covers every row the operation creates or relies on:
  - the journal's recorded `NewTrackFileId`;
  - while `NativeUpgrade` is in flight, any non-original row at the track's destination or original path, meaning the candidate fresh row or an operator-approved rollback row.

  If any other track links to one of these, the reconciler returns review before any further step, including recording success. Shared files that the operation never touches stay allowed (case 4).
- **Both halves are needed.**
  - Without the in-flight half, case 1 records a false "step done" before the recorded-ID half catches it on the next pass.
  - Without the recorded-ID half, cases 2 and 3 complete.
- **Contract.** Crescendarr promises per-track isolation only for the rows it binds or creates. A future adapter must observe *all* tracks that link to those rows. A host snapshot scoped to one album would miss links from other albums, so the adapter has to query by track-file ID, not by album.
- **Residual limit.** Association is checked only at observation time. Another track being relinked between the last observation and a host write is the same window documented in limit 2. Links from outside the snapshot's scope are invisible to the pure layer.

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
6. **Lab sandbox is not a containment guarantee.** It relies on check-then-use path operations, so it trusts same-user processes (finding A).
7. **Path identity rules are string rules.** Hardlinks, bind mounts, server-side aliases and full Unicode case folding are not detected (finding B). The adapter must supply authoritative host identities and must not widen these rules.
8. **The adapter must supply `Artist.Path` and `LyricFile.RelativePath` from one fresh host observation** (finding C). The pure layer cannot establish lyric identity without that trusted input.
9. **Manifest rollback is undetectable.** If an older but internally valid manifest generation were restored (for example from a filesystem snapshot), nothing in the lab detects it. Observation-based reconciliation would then classify against the older phase, and most mismatches would surface as review, but this is unproven.
10. **Cross-album links are invisible to an album-scoped snapshot** (revision 3). The adapter must look up every track linked to each bound or fresh track-file ID.
11. **Recycle copies are never cleaned, and backups are retained indefinitely.** A retention and cleanup policy, including operator acknowledgement of review states, is future work.
