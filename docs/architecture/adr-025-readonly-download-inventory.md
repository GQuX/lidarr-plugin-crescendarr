# ADR-025: Read-only, restart-safe completed-download inventory (M10, pure layer)

**Status:** DRAFT for independent review. Pure domain + specs only. No host adapter, persistence adapter, importer, cleanup, settlement or production change.
**Issue:** [#21](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/21). **Host baseline:** stock Lidarr v3.1.6.5079 (`da7b4dfb`). **Date:** 2026-11-22.

## Decision

Crescendarr models completed downloads as a pure transition function

```
InventoryEngine.Apply(state, policy, ClientPoll) -> state        // observe
InventoryEvaluator.Evaluate(state, policy, now)  -> report       // decide (read-only)
InventoryCodec.Encode/Decode(state) <-> canonical text           // rehydrate, no storage
```

in `src/Crescendarr.Core/Inventory/`, with no Lidarr types, no storage, network, clock, randomness or process access. A future in-process adapter would translate stock `IDownloadClient.GetItems()` results into `ClientPoll` values; it is **not** part of this change.

**Item identity** is `(download-client definition ID, exact download ID)`. The client's implementation name is part of the provider identity; a changed implementation for the same definition ID is an identity conflict. The **normalized output path** is anchored on first completion and must never change afterwards.

**States:** `Observed`, `StillDownloading`, `CompletedSettling`, `CompletedStable`, `MissingFromClient`, `Ambiguous`, `RequiresReview`. There is no Imported, Cleared or Settled state, and a spec (L09) enforces that vocabulary. `Ambiguous` and `RequiresReview` are sticky: no automatic transition leaves them in this milestone.

**Eligibility** (evaluation only): `ReadyForPlanning`, `Wait`, `OperatorReview`, `NoAction`. `ReadyForPlanning` only permits computing a read-only merge plan after host revalidation. Nothing in this layer permits import, staging cleanup or downloader removal.

## Stability: the checkable definition

`CompletedStable` holds only when all of the following are true (`InventoryPolicy` documents and `Validate()` enforces the bounds):

1. The client reported `Completed` in at least `MinStablePolls` (2 or more; conservative default 3) consecutive successful polls of that client.
2. Between those polls there was no failed poll, absence, clock regression, implausible timestamp, or gap larger than `MaxPollGap`.
3. Output path, total bytes and remaining bytes were identical in every one of those polls.
4. Remaining bytes were 0 and total bytes were known and non-zero (`RequireKnownSize`). Stock `TotalSize` is non-nullable, so 0 is treated as unknown.
5. At least `MinStableAge` (default 2 minutes) elapsed on the observer clock between the first and the latest of those polls.

Evaluation also refuses a stable item when any of these holds:

- its client is stale (`StaleAfter`), unavailable, unsupported, conflicted, or showing stock CDH active;
- the evaluation clock is behind the client's last success;
- the anchored root is no longer in the policy.

## Fail-closed rules (each covered by a spec)

| Input | Result |
|---|---|
| Empty, padded, control/bidi/invisible, non-NFC or over-256-char download ID | Not ingested; counted on the client (`UnkeyableItemsTotal`), client `Degraded` |
| Identical duplicate entries in one poll | Idempotent |
| Conflicting duplicate entries in one poll | `Ambiguous`. Every variant is judged, and a poisoned variant escalates to `RequiresReview` (bug found by fuzzing) |
| Same download ID in two clients | Both `Ambiguous`, whatever the poll order. Stock `TrackedDownloadService` caches by download ID alone (`TrackedDownloadService.cs:229`), so the host itself cannot separate them |
| Equal, case-variant or nested output paths across items | All involved items `Ambiguous`. Compared case-insensitively on every host, because SMB/NAS storage may fold case |
| Relative path, `.`/`..`, POSIX backslash or leading `//`; Windows UNC, device, ADS, reserved or trailing-dot path; URI; control/bidi/invisible/U+FFFD/non-NFC characters | `RequiresReview` (`OutputPathUnsafe`); sanitized raw evidence retained |
| Path outside the allowed roots, or matching more than one | `RequiresReview` |
| Path, casing or root change after completion | `RequiresReview` with a specific reason. Changes before completion (incomplete to complete folder) are allowed |
| Reappearance after `MissingFromClient` with a different path | `RequiresReview` (`IdReusedWithDifferentPath`) |
| Reappearance with the same path | `CompletedSettling`; stability must be re-proven |
| `Completed` to anything else, `Failed`, `Unknown` or undefined status | `RequiresReview` |
| Negative size, or remaining greater than total | `RequiresReview` |
| `GetItems` failure, or a "successful" poll with no items array | No absence counted, stability reset, evidence kept, client `Unavailable` |
| Absent in one successful poll | `NotSeenInLatestPoll`, stability reset |
| Absent in `MissingAfterPolls` polls | `MissingFromClient`, evidence kept, eligibility `NoAction` |
| Poll sequence not greater than the last processed one (replay, out-of-order delivery, adapter reset after restart) | Ignored; returns the identical state object. A reset adapter therefore surfaces as `Stale`, never as fresh data |
| Clock regression, gap above `MaxPollGap`, timestamp outside 2000-2200 | Stability reset; client flagged |
| Stock CDH reported ON | Nothing ingested; previously completed items `RequiresReview` (`StockHandlingWasActive`); client `StockHandlingActive` |
| Implementation not allow-listed | Nothing ingested; existing items `RequiresReview`; client `Unsupported` |
| More than `MaxItemsPerClient` items | New IDs refused, existing ones still tracked; client `Degraded` |
| Tampered, non-canonical-digest, unknown/duplicate property, numeric or invented enum, wrong schema/format, truncated, commented, orphan item, or forged stable record in a decoded document | `Decode` fails. There is **no empty-inventory fallback**, which would erase review evidence |

## What is proven, and how

- **Executable (pure, CI):** 59 inventory specs in `tests/Crescendarr.Core.Inventory.Spec`, covering:
  - permutation invariance over 25 seeds, and order independence of interleaved clients over 25 seeds;
  - restart at every cut point of a 10-poll trace, giving byte-identical canonical state after encode, decode and resume;
  - a 200-seed adversarial fuzz with random statuses, poisoned paths, clock regressions, gaps, failures and replays. It asserts round-trip identity, replay identity, sticky review for poisoned keys, `Ready` only for stable items of healthy clients with no cross-client duplicate, and `NoAction` for missing items.

  The 29 existing planner specs still pass.
- **Source-verified (v3.1.6.5079), not executed by this change:**
  - `DownloadClientItem` field types (`DownloadClientItem.cs:11-26`);
  - Blackhole download IDs are `<folder name>_<creation ticks>` or `<file name>_<last-write ticks>` (`ScanWatchFolder.cs:65,101`);
  - the tracked-download cache is keyed by download ID only;
  - `DownloadMonitoringService` keeps polling, and calls `CompletedDownloadService.Check`, with CDH off.
- **Executable in earlier PRs:** with CDH off, a running stock host exposes a completed synthetic Blackhole item to an in-process observer (#20).

## What remains a guess about the stock schema

1. Lidarr's `GetItems()` is a full snapshot per client, so absence carries meaning. SABnzbd history paging and retention can make old jobs vanish without import. This model treats that only as `MissingFromClient` / `NoAction`, never as import.
2. A download ID is stable for the job's lifetime. Blackhole IDs embed filesystem timestamps, and over SMB a changed creation or write time would mint a new ID for the same path. Here that fails closed as an `OutputPathCollision` with the old record.
3. `OutputPath` may be absent or move before completion, but is final at `Completed`.
4. The adapter will take provider identity from `IDownloadClient.Definition` (`Id`, `Implementation`), not from `DownloadClientItemClientInfo`. Also assumed: definition IDs are not reused for a different implementation. If they are, the result is an identity conflict, not silent reuse.
5. The roughly one-minute stock refresh cadence that the conservative timings assume.
6. Status mapping is one-to-one with `DownloadItemStatus`. Anything else must map to `Unknown`.

## Health and retry signals a future adapter would expose

- **Per-client `ClientHealthStatus`:** `Healthy`, `NeverSucceeded`, `Unavailable` (with `ConsecutiveFailures` and a sanitized `LastFailureReason`), `Stale`, `ClockAhead`, `Unsupported`, `IdentityConflict`, `StockHandlingActive`, `Degraded`.
- **Counts:** `ReadyCount`, `ReviewCount`, `MissingCount`, and `AnyClientUnhealthy`. These suit a Lidarr health check, since the stock queue hides completed jobs while CDH is off.
- **Retries:** the core never retries or polls anything itself. It consumes whatever polls the host's own refresh produces; retry is simply the next poll. Backoff belongs to the stock monitor, not to Crescendarr.

## Not competing with stock Completed Download Handling

- The adapter must pass the live `EnableCompletedDownloadHandling` value in every poll.
- While stock CDH is ON, the inventory ingests nothing, and anything it had seen complete becomes `RequiresReview`, because stock may already have imported or pre-wiped it.
- Crescendarr never toggles CDH. Turning CDH off in a real deployment remains an explicit, unapproved operator decision.

## Connection to a future journaled executor (not implemented)

1. The executor may consider only `ReadyForPlanning` items. It must re-read the host's tracked download (album mapping, history) and revalidate the path on the host before any plan.
2. `ItemDecision.EvidenceDigest` (SHA-256 of the canonical record) acts as an optimistic-concurrency token. The executor's journal records it, and the operation aborts if the digest changes before commit.
3. Import, keep and settlement outcomes live in the executor's own journal. They are never written back as inventory states. The inventory can only ever say "the client still reports this as completed and stable".
4. `MissingFromClient` never triggers recovery, cleanup or settlement. An executor operation open for a missing item stays open until the operator reviews it.
5. Sticky states need an explicit operator acknowledgement API in a later milestone. None exists yet, by design.

## Remaining limitations and open questions

- Path checks are lexical. Symlinks, bind-mount remaps, SMB case folding of *different* strings, and Unicode case folding beyond ordinal-ignore-case remain host-side revalidation duties.
- Records are never pruned. Size is capped per client only through `MaxItemsPerClient`, which blocks new items once full. A retention and pruning policy needs its own review.
- Sticky states have no reset path yet, so long-lived installations will accumulate review items.
- No persistence adapter is included. Writing the encoded text somewhere atomic is future work, and it belongs with the journaling work.

## Needs a disposable full Lidarr host and the synthetic Blackhole client

- A real `IDownloadClient.GetItems()` stream mapped by an adapter, including exceptions, empty results and the Blackhole 30-second grace behaviour.
- The adapter-owned persisted poll sequence across a real process restart and a crash.
- Grab-history mapping (`RemoteAlbum`), which a non-grabbed Blackhole item never reaches (`CompletedDownloadService.Check` early return).
- Health-check registration from a plugin, and Lidarr's actual refresh cadence.
- Behaviour with CDH toggled ON mid-stream on the disposable host.
