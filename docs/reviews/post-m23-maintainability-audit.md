# Post-M23 maintainability and hot-path audit

**Scope:** Review of M23 head `c8569ce` (real-CIFS support) before a beta or additional feature work. This is a **narrow, behaviour-preserving** optimization branch stacked on draft PR #59. No merges or production writes. No synthetic benchmark numbers are claimed.

## Applied in this branch

| Concern | Observation before | Change | Safety invariant |
|---|---|---|---|
| `Disk.Sha(string)` | `File.ReadAllBytes` allocated an entire track per hash (including repeated journal snapshots, candidate catalog scans, and album reviews). | Stream through SHA-256 with bounded buffering. | Digest remains lowercase SHA-256; files are not mutated. |
| `Disk.CopyVerified` | Allocated whole audio/lyric file in managed memory before writing. | Pre-hash the source through a stream **before any destination directory changes**, rewind, stream through a 64-KiB buffer while hashing the copied bytes, reject a mismatch **before rename**, fsync temporary file, rename without overwrite, then re-read and verify destination hash. This trades an extra sequential source read for lower peak memory without weakening the original pre-copy validation. | Same source SHA, verified content, no overwrite, and file fsync before visibility. Source download never moved/deleted. |
| Atomic writer repetition | Journal text writes and large verified copies needed separate temporary-writing paths. | Shared `WriteAtomically` helper with one temp/fsync/rename implementation. | `replace` policy unchanged; partial temporary file cleaned up after exceptions; no parent directory fsync guarantee. |
| Review hashes | `WriteSummary` hashed the same lyric backup path twice (once to display and once to compare). | Intra-report memoization of SHA by path; never reads storage for StorageUnavailable report. | Memoization lasts only one read-only report, not journal recovery or import authority. |
| CIFS directory probe | `Observe` probed the artist directory twice when unavailable. | Capture probe result once. | Missing/unreachable storage still returns StorageUnavailable; mount identity still rechecked each snapshot. |
| Planner query repetition | `HostAdapter.BuildPlan` called global `GetTracksByFileId` once per existing track; `Observe` fetched album releases twice. | One global bulk sharer query grouped by TrackFile ID, and one reused release query. | Still queries **all cross-album sharers**, not just this album; shared rows continue to fail closed. |

## Identified but NOT automatically refactored

1. **Global ownership queries are not optional.** The planner's new bulk query must keep all cross-album sharers, including aliases; the full M15/M18 negative test lanes should stay green. If the host bulk overload behaves differently for empty or missing IDs, fail closed rather than falling back to album-only evidence.
2. **Repeated host and filesystem snapshots are intentional safety checks.** `Executors.RunJournal` gets fresh Artist MBID, `HostSnapshot`, `FilesystemSnapshot`, global TrackFile sharers and lyric links before each irreversible step. Consolidating them into a long-lived cache would invalidate M13/M18/M19 crash/alias guarantees. Preserve per-step freshness. Measure overhead if real libraries expose a bottleneck, rather than blindly memoizing.
3. **`Reconciler.cs` has similar row/lyric filtering in several phases.** The similar-looking checks serve different proof obligations: original association, fresh rebase, album-wide salvage, postcondition and operator approval. Do not replace them with a single permissive lookup to reduce line count. A future low-risk refactor could index snapshot collections *within a single reconciliation invocation* and reuse pure predicate functions while keeping all negative fixtures.
4. **Remaining large MP3 buffer:** `HostAdapter.Observe` still calls `AudioIntegrity.Mp3Problem(File.ReadAllBytes(...))`. That is currently the remaining large allocation for each incoming MP3; a separately reviewed stream-based parser is worth doing before a beta with long MP3s. It must keep M22 missing-article, broken frame-chain and truncated-last-frame tests green. Do not omit structural checks to gain speed.
5. **Repeated rehashing by snapshots:** `Observe` SHA-256 hashes every bound original/destination/backup/lyric path on each recovery step. This is intentionally authoritative for crash recovery but may be expensive on remote shares for a 28-track album. If performance data justifies change, instrument bytes read, snapshot count, network round trips and per-operation latency. Never cache audio checksums across destructive steps or stock rescans without proving snapshot validity.
6. **`SettleModified` deliberately waits for CIFS deferred close and attribute cache windows.** It uses two waits and updates only placed TrackFile.Modified rows to avoid destructive stock rescans and album-wide lyric recycling. Removing or shortening the waits without repeatable real-CIFS evidence risks reintroducing M23's data loss.
7. **`OperatorStatus` and `UnfinishedOperations` read several small JSON documents repeatedly.** This is secondary overhead. A consolidated metadata index adds consistency/cleanup complexity and is not justified before measured demand.
8. **`Executors.cs` (~1,237 lines) and `Reconciler.cs` (~868 lines) are large.** A mechanical split into partial files might improve navigation, but is not inherently an optimization and could obscure recovery authority. Defer until release gates are green; retain exactly one journal and reconciler.

## What this audit does NOT claim

- No performance benchmark or end-to-end speedup measured yet.
- No improvements to audio authenticity detection, NAS physical power-loss stability, file-directory fsync, SMB server caches, partial writes, stock Lidarr's unrelated CIFS rescan behavior or user opt-in.
- This branch is not authorization to merge or deploy; production writes remain **NO-GO**.

## Validation required

- CI pure specs, DI smoke, M17–M22 integration and **all six** real kernel-CIFS M23 lanes green on the final cleanup head.
- Compare exact SHA / track ownership / lyric registrations, no album-wide cascade, no unexpected tmp files after normal operation or failure.
- If a test fails, fix or revert the optimization, **never weaken a safety assertion**.
