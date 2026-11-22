> **Historical research only, superseded as the implementation roadmap.** The owner requires unmodified stock Lidarr and will not use a fork or upstream patches. See [plugin-only feasibility v0.1](stock-lidarr-plugin-only-feasibility-v0.1.md) and [issue #16](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/16). The proposed Lidarr core patches remain useful risk analysis but are no longer required or planned deliverables.

# Incremental host-owned selective-import plan, v0.2

**Date:** 2026-11-22. **Status:** research proposal, not implemented and not production safe.
**Source:** Lidarr stable v3.1.6.5079, commit da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd.
**Review:** [independent ADR-024 triage](../reviews/2026-11-22-adr024-review-triage.md). Supersedes [v0.1](host-recoverable-import-protocol-v0.1.md) as an implementation direction.

## What can and cannot ship as one plugin

The product remains a single Crescendarr package with no daemon and no Tubifarry, LRCGET or direct SABnzbd dependency. Stock Lidarr does **not** provide a safe hosted write/rollback/settlement API. A standalone plugin on Lidarr v3.1.6.5079 cannot guarantee its own presence after host ABI/startup failure. The feasible hypothesis is a sequence of small upstream Lidarr fixes, followed by an optional additive policy plugin if real host discovery supports it.

## Evidence corrections

1. The previous 11-imported/1-kept => ImportFailed claim was a **mocked no-history case**, not an end-to-end download outcome. ImportApprovedTracks publishes successful TrackImportedEvent events after DB writes, and EntityHistoryService records TrackFileImported. The real TrackedDownloadAlreadyImported predicate accepts the latest per-album import history event without counting expected tracks. It can settle partial real failures as complete. [Native follow-up tests in PR #14](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/14) are evaluating that predicate without mocking it.
2. ImportApprovedTracks catches most per-track exceptions and still batches DB inserts for successes. A failure on file six does not automatically leave successful first-five files without DB rows. Process crashes and DB write failures remain distinct risks.
3. A rescan while an original file is renamed aside can mark its TrackFile MissingFromDisk, causing an event-driven cleanup of registered lyrics. Scan exclusion or synchronous startup recovery **must be in place before** any rename-aside write design can be accepted.
4. Existing CommandQueue RequiresDiskAccess serialization helps command-driven operations, but does not cover all API, event, startup or external NAS activity.

## Small upstream patch sequence, with prerequisites

### A. Same-release preservation only

Native tests first: 12 old files, 11 preferred incoming, a better old track 12. When native monitored AlbumRelease.Id equals the incoming release ID and identities are verified, skip the album-wide pre-wipe. An import targeting a different or ambiguous release must **not** silently use the same-release rule. Explicit edition switches need separate, tested behavior. This reduces album-wide deletion; it does not protect original files for the 11 individual upgrades. Consider overlap with open Lidarr PR #6166, whose selective branch is conditional.

### B. Guard shared physical files

Before removing an existing TrackFile, check **all** native Track.Id links. Reject an incoming single-track replacement that would orphan another track in a cue/single-file rip. Test one-file/multi-track and no-op behavior.

### C. Prepare safer publication, but protect rescans first

Reuse existing RequiresDiskAccess command serialization for command-driven import work. An additional core-owned per-album exclusion is needed for MediaFileTableCleanupService / DiskScanService when a recovery operation is open, or startup recovery must complete before any scan dispatch. The existing command lock alone is insufficient.

An initial *disposable local filesystem* candidate protocol:
1. Record an operation intent, album/release/track IDs, original and target paths, and registered/unregistered lyric inventory in a persistent host journal. Journal location must be decided and tested; local Lidarr configuration storage is preferred over SQLite on SMB.
2. Copy incoming audio into a unique hidden destination-side stage. Perform **all tagging before final candidate verification**. Read-back size and hash do not prove NAS power-loss durability.
3. With active scan protections, rename the original to a protected, same-storage recovery path, not delete it. Retain protected existing lyrics. Whether a second independent copy is warranted is a testable safety and storage policy choice.
4. Promote the staged candidate. On errors and lost acknowledgements, inspect actual source, stage, recovery and final paths after reconnect before deciding whether retry, restore or hold is safe.
5. Reconcile native TrackFile and track links, selected edition and LyricFile associations without plugin-owned raw SQLite writes. Hold for review on ambiguous DB/file mismatch.
6. Do not clean staging or retire protected originals until verified completion and an explicit retention policy.

Persisted observational markers can be Intent, Staged, Swapped, Linked plus a RecoveryRequired flag. They do **not** promise atomicity between SQLite and a filesystem or SMB durability at commit. A tested restoration may be reasonable in a uniquely identified precommit state; the default uncertain case is to hold and reconcile, never blind rollback.

### D. Correct no-improvement and partial completion

Introduce typed ExistingFileIsPreferred/KeptBetterExisting for exact native track and file identities after quality and edition revalidation. That state counts as **satisfied** but never creates a false TrackFileImported event. A real failed import, unknown/ambiguous file or open recovery operation is **not** satisfied even when the last history event for an album is TrackFileImported.

Acceptance: 0 imported + all better existing, 11 imported + 1 verified kept, 11 imported + 1 genuinely failed, 12 imported, and interrupted retries, with **real history service/predicate** rather than an injected true/false result.

### E. Per-file association and lyric preservation

Reconcile new TrackFile IDs and associated native Track links per applied file, preserving old LyricFile links and adjacent .lrc bytes when no safe incoming lyric replacement exists. A new audio filename does not authorize discarding or overwriting old lyrics. Include staged .lrc under default ImportExtraFiles=false in acceptance tests. Changes to the host event/lifecycle order will likely be required.

These are **not guaranteed independent**, and the order may change when native regressions reveal coupled requirements. Each patch needs positive and negative tests, especially release-switch and missing-file scans. Smaller source changes are not automatically safer.

## Crash/SMB observation table

| Observed paths after an error | Safe provisional handling |
|---|---|
| Original present, staged present, recovery absent | Validate identities; retry only before publication and under scan protection |
| Original absent, recovery present, final absent | Preserve recovery and hold; a guarded restore or promotion may be possible |
| Original absent, recovery present, final present | Verify both, reconcile DB, **do not delete recovery** |
| Duplicates or contradictory file identities | Hold for review; do not infer which copy is authoritative |
| Storage unavailable or both original and recovery unverified | RecoveryRequired; no cleanup or automatic retry |
| Rename returned failure but target exists after reconnect | Treat as ambiguous ACK; inspect journal and cold-cache path content |

Original/final paths may intentionally be the same filename. The real protocol must track staged and recovery locations, operation IDs, file identities, hashes, mount identity and DB version tokens. Neither local read-back nor fsync universally proves NAS persistence. Testing a dedicated synthetic CIFS share may characterize behavior, but must never use production media or credentials.

## Optional policy plugin experiment

Evaluate additive IImportDecisionEngineSpecification<LocalTrack> / IImportDecisionEngineSpecification<LocalAlbumRelease> registrations in an isolated Lidarr host. Prove actual plugin specs appear in the decision collection and affect a real synthetic candidate. Never use outer service overrides as a write path. Manual import and plugin-absent fallback still require safe upstream host behavior.

## Next executable gates

1. Complete [PR #14](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/14): source-native history and MissingFromDisk scan/lyrics characterization.
2. Prototype the same-release pre-wipe guard in a disposable upstream fork, first mocked and then with synthetic real bytes and SQLite rows. Check better track 12, edition identity and lyrics.
3. Reproduce scan cleanup while an original filename is absent. Add host scan protection before testing a rename-aside executor.
4. Exercise staged candidate tagging, file publication and kill-at-each-boundary recovery on local synthetic storage.
5. Test native history-backed completion for all-kept, kept+improved and genuinely failed partial albums.
6. Only after local tests pass, test controlled failures on a dedicated synthetic SMB share.

**Release gate remains closed.** No write-enabled plugin, production Lidarr operations or external upstream PRs are authorized.
