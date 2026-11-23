# ADR-027: Run host-visible Crescendarr operations inside one stock disk-access command (M16)

**Status:** DRAFT, proposed. Test evidence only. **Production write-enabled stays NO-GO.**
**Issue:** [#44](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/44). **Date:** 2026-11-22.
**Evidence:** [stock-command-queue-serialization](../compatibility/stock-command-queue-serialization.md), CI run 45084467908. Related: #41 / PR #42 (album-wide lyric hazard and crash lanes), #40 / PR #43 (host snapshot, not modified here), M13 ADR-026, M14 PR #39.

## Context

M14 showed that a stock rescan interleaved with an import does real damage: lyrics are recycled album-wide, TrackFile IDs change, and `UNIQUE(TrackFiles.Path)` failures occur. Issue #44 asked whether an additive plugin can use the stock queue's `RequiresDiskAccess` rule to exclude that interleaving, without modifying Lidarr.

## Findings (proven on the official binary)

1. **Integration through stock DI works.**
   - Plugin `Command` types appear in `KnownTypes`.
   - The plugin `IExecute<T>` resolves through `IServiceFactory`.
   - Commands pushed by the plugin or the API run on the stock `CommandExecutor`.
   - **No DI integration failure was found.**
2. **Live mutual exclusion holds in both directions** between our disk-access command and stock `RescanFolders`. No two finished disk-access commands overlapped. Stock de-duplication applies to our commands too.
3. **The exclusion covers queue scheduling only.** A direct `IDiskScanService.Scan` call ran while our disk-access command was Started.
4. **The exclusion is not durable.**
   - After SIGKILL and a restart, our Started command is **Orphaned and never resumed**.
   - Our Queued command is rehydrated as a stock **`UnknownCommand`** and **skipped**. It then claims no disk access and runs concurrently with a requeued stock scan.
   - Cause: stock `CommandConverter` looks up command types only in `Lidarr.Core`. This is stock behaviour we can't change without modifying Lidarr, so we won't change it.

## Decision (proposed)

- **D1. Single disk-access command.** Every host-visible write of a Crescendarr operation runs inside **one** plugin `IExecute<T>` whose command has `RequiresDiskAccess=true`. That covers file moves, `TrackFile` and lyric rows, recycling and restores. Its body must not call stock commands, which would deadlock or be queued behind it. It must not hand work to other threads, timers or `IHandleAsync` handlers.
- **D2. No host mutation outside it.** Event handlers, API controllers and startup handlers may only observe, or push our command.
- **D3. The `Commands` table is never a recovery source.** Durable state lives only in the M13 journal plus album-wide backups. On startup:
  - treat our Orphaned or Skipped command rows as "possibly partial";
  - block new Crescendarr operations;
  - push **one** recovery command (disk access) after `ApplicationStartedEvent` has completed;
  - reconcile from fresh observation, using the PR #42 reference behaviour, and return sticky `RequiresReview` on ambiguity.
- **D4. Fail-closed re-verification immediately before each mutation, inside the command.** The queue doesn't exclude direct callers, other plugins, users, NAS/SMB or downloaders. Before each mutation, re-check:
  - the hash, recording and release identity, and the monitored release;
  - global TrackFile sharers;
  - path and link aliases.
- **D5. The gate stays shut.** Unattended imports remain NO-GO. The window between a stock scan queued before our command and our recovery still exists, as do out-of-process writers. Together with the PR #42 evidence, this keeps album-wide lyric backups mandatory.

## Consequences

- **#41:** the M14 *queued* scan/import interleaving is excluded while our command runs. The lyric hazard remains for:
  - scans that run **between** a crash and our recovery command;
  - the window before our command starts;
  - out-of-queue writers.

  PR #42's backups and observation-based recovery remain required.
- **#40 / M15:** the host snapshot projection should be taken **inside** the disk-access command, so it is consistent with respect to queued stock commands. It can't be consistent against direct callers.
- **M13:** the journal's in-flight step and the reconciler remain the only recovery authority. The executor integration point is the D1 command body.
- **Startup duplicates:** never push from `ApplicationStartedEvent` and assume exactly-once execution. The stock requeue also runs from that event. In this run the startup push executed once.

## Carried forward from the PR #42 independent review (not addressed here; still open)

The PR #42 review of `50dada2` identified gaps in the test-only reference reconciler. They must be fixed before any reuse as a host adapter, and must not be forgotten:
1. **Artist MBID is recorded but not revalidated.** `P0Observe` checks `Artist.Id` and the path, but not the fresh `ArtistMetadata.ForeignArtistId`. A changed-artist-MBID negative case is needed.
2. **The duplicate-recording rule doesn't match the identity tuple.** `(MediumNumber, RecordingMbid)` uniqueness rejects legitimately repeated recordings that have distinct release-track MBIDs. Either document it as an intentional fail-closed limit, or add a multi-disc / repeated-recording fixture with the correct invariant.
3. **Path, link and concurrent-state checks are missing.**
   - There is no symlink or alias rejection.
   - Per-step rechecks don't recheck artist, release or recording identity, global sharers, all audio bytes, or filesystem aliases.

   D1 supplies queue-level serialization against stock commands only. It does not make per-step checks safe against direct or out-of-process actors (D4).

Also from that review: don't ship the ~1,100-line test reconciler as a second production recovery policy that diverges from M13's pure `Reconciler`.

## Alternatives rejected

- **Holding the lock via an event handler on stock commands.** It works in a test (we used it to hold a stock scan), but it blocks a stock worker thread and depends on publication order. It's a test technique, not a design.
- **Re-pushing our own commands on restart from the `Commands` table.** The bodies are rehydrated as `UnknownCommand`, so their parameters aren't reliably available. The journal is the authority.
- **Patching `CommandConverter` or the host.** Forbidden: no fork and no binary patch.
