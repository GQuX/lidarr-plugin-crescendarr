# Proposed host-owned recoverable selective-import protocol (v0.1)

**Status:** DESIGN FOR ADVERSARIAL REVIEW ONLY, not implemented, not an upstream API, not a production-safe plugin.
**Baseline:** Lidarr v3.1.6.5079 at upstream commit da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd. **Date:** 2026-11-22.
**Intent:** preserve higher-quality existing audio, selected MusicBrainz edition, adjacent and registered `.lrc`, and truthful download history when an album release improves only some tracks.

## Evidence driving the design

- [12-track native characterization](../compatibility/native-12-track-characterization.md): 11 accepted upgrades and one inferior candidate, yet stock `replaceExisting=true` schedules deletion of all 12 existing album files. Passing `false` avoids the album-wide removal but still calls `SetMonitored`.
- [Native fault injection](../compatibility/native-upgrade-failure-characterization.md): in stock `UpgradeMediaFileService`, the old path and DB relation are removed before an attempted replacement move/copy; an injected `IOException` occurs after those calls.
- [Native lyrics + completion tests](../compatibility/native-lyrics-completion-characterization.md): registered `.lrc` is recycled when its TrackFile is upgraded; lyric-only staging passes the folder-deletion eligibility check; 11 imported + 1 intentionally kept results in ImportFailed without qualifying history.
- [Independent-review triage](../reviews/2026-11-22-independent-review-triage.md): the outer `IDownloadedTracksImportService` DI override is a NO-GO production write path; a failed plugin can allow stock behavior on some host restart paths.
- [Upstream Lidarr PR #6166](https://github.com/Lidarr/Lidarr/pull/6166) is open and unmerged as checked 2026-11-22; it addresses selected recordings but is not proven to solve general album quality merging, lyrics, completion and crash consistency.

All tests above use mocked external effects, not a real NAS or production library. The proposal below specifies *desired* guarantees, not currently available capabilities.

## Hard invariants

**INV-1: Better files never become replacement targets.** A file can be replaced only if each native Track association has an explicitly verified upgrade decision. An existing file linked to more than one Track is an atomic group: either every associated Track is supported by the candidate, or reject it for review. Preserve files unrelated to the selected set.

**INV-2: Monitored edition is immutable during automatic subset upgrades.** Resolve native Album.Id, AlbumRelease.Id and Track.Id, with recording IDs as supporting evidence. Confirm the currently monitored release under a host lock immediately before any mutation. A different/ambiguous release is RequiresReview, never an implicit `SetMonitored`.

**INV-3: Existing and incoming lyrics are protected objects, not disposable folder debris.** Inventory registered LyricFile entries and adjacent `.lrc` files not yet indexed. Preserve byte-identical originals unless an explicit, safe lyric replacement decision exists. Do not trust the default extra-import toggle to copy lyrics.

**INV-4: No destructive operation precedes a verified recovery copy and staged candidate.** The host, not a plugin's direct SQLite edits, must own storage operations, journal and DB associations. The default optional Lidarr Recycle Bin is not a sufficient durability contract.

**INV-5: Download settlement is separate from file imported status.** Per-track outcomes are Imported, KeptBetterExisting, RequiresReview, or RecoveryRequired. A verified intentional keep is satisfied without creating a false TrackFileImported history event.

**INV-6: Safe behavior must not depend on a plugin successfully loading.** The current startup fallback can return to stock importer. A supported host-owned hook/default non-destructive policy or equivalent core change is required before making guarantees to users. An unrecognized plugin API version means hold/review, not a silent stock pre-wipe.

**INV-7: At every crash/retry boundary, a durable operation can be reconciled without guessing.** An ambiguous filesystem/DB split must pause for review and keep recovery copies, never run cleanup on uncertainty.

## Candidate host API boundaries (illustrative, not Lidarr interfaces)

1. `InspectImportContext`: host returns canonical selected release, track-to-file relationships, profile quality rules, candidate IDs, sidecar inventory and storage topology. The plugin supplies no authoritative booleans such as FileVerifiedPresent or EditionVerified.
2. `PlanSelectiveImport`: a pure policy module proposes Improve, AddMissing, KeepBetterExisting or RequiresReview for each candidate. The host validates every target using current native objects, profile and locking.
3. `ExecuteRecoverableImport`: a **host-owned** coordinator stages candidates, guards originals, publishes files, updates TrackFile/extra-file relationships and records receipts. No Crescendarr filesystem executor or raw database edits.
4. `SettleImportAttempt`: a host-owned typed completion protocol reconciles receipts and previously recorded history. It must distinguish zero improvements, partial applied-but-satisfied, blocked, failed and recovery required.
5. `ReconcileUnfinishedOperation`: run on restart and before a conflicting scan/download retry; restore/resume based on the journal and verified media/DB state.

All methods above are CONCEPTS. The smallest practical upstream change may be an opt-in pre-import plan, protected execution options and a settlement result, rather than adopting these exact names.

## Proposed durable state sequence

| State | Preconditions and durable evidence | Allowed next action |
|---|---|---|
| `Observed` | no writes; candidate and album inventory captured | reject ambiguity or begin host preflight |
| `Prepared` | host lock held; native identities and paths revalidated; unique operation ID and intent journal persisted | stage new media and lyrics |
| `StagedVerified` | candidate audio and sidecars copied to unique destination-side temporary paths; sizes and hashes verified; storage write durability attempted/checked | create protected recovery copies |
| `RecoveryVerified` | original audio, linked and adjacent lyrics recoverable at verified separate paths; journal records their hashes | publish candidate without losing backup |
| `Published` | destination-side publication attempted and actual resulting paths verified | commit/link DB records |
| `DbReconciled` | host verifies exact TrackFile IDs/Track associations, monitored release, linked extras and final file inventory | derive track-level outcomes |
| `Settled` | host records truthful import results and download history; no forged imports for kept-better tracks | guarded staging cleanup |
| `CleanupComplete` | only unprotected staging files removed; all sidecar commitments satisfied; recovery retention policy still respected | terminal |
| `RecoveryRequired` | any ambiguous or inconsistent outcome | do not delete, stop automatic retry, show actionable diagnostics |

The journal should record operation ID, expected identities, source and destination versions, checksums, backup paths, stage transitions, and replay status. It must be written and durably committed before the corresponding risky operation. This is a *design requirement*, not evidence that SQLite plus SMB writes are atomic.

### Filesystem reality check

- Assume the completed download may be on a different mount from the library. Stage by **copying**, not moving, until recovery and settlement are verified.
- Stage inside the destination's storage domain to avoid a cross-volume final transfer where possible. Even same-share rename semantics, fsync and durability vary across SMB/CIFS servers and mounts. **Do not assert atomicity or guaranteed crash persistence until tested on the specific filesystem**.
- A possible sequence is `copy candidate to stage` -> verify stage -> copy/verify backup of original audio and lyrics -> persist journal -> rename original to a protected unique recovery path -> promote staged file -> verify -> update DB. At any interrupted boundary the journal and observed paths decide whether to resume, restore or hold.
- Do not delete backups after a successful return code alone. Verify DB and post-import audio/lyrics inventory and define retention. If the database update fails after publication, retain backups and reconcile; do not claim an automatic rollback is always possible.
- Avoid assuming an unconfigured Recycle Bin protects files; for actual network outages distinguish operation returned error, server committed but client lost ACK, path inaccessible, and truly missing bytes.

## Lyrics ownership and conflict policy

Capture both existing LyricFile associations and raw adjacent `*.lrc` inventory (case-insensitive, normalized paths), plus incoming sidecars. Default to retaining the original lyrics on upgrade; never overwrite an existing `.lrc` just because the incoming file has the same basename. If an audio filename changes, the host must preserve/relink the old lyric or produce an explicit conflict requiring review.

Downloaded `.lrc` may be kept as part of a new/add-missing track, but staging cleanup may proceed only after verifying that protected sidecar bytes were imported or intentionally retained elsewhere. A lyric-only staging folder is NOT automatically empty of protected content.

## Queue and history settlement contract

| Per-track plan outcome | Host meaning | Should count toward satisfied download? |
|---|---|---|
| `Imported` | new TrackFile exists and committed/verified | yes |
| `KeptBetterExisting` | existing TrackFile verified preferred and intact, with edition/lyrics unchanged | yes, with explicit kept disposition, **not** a fake import |
| `RequiresReview` | identity, format, edition or sidecar conflict | no; hold and notify |
| `RecoveryRequired` | unfinished or ambiguous disk/DB transition | no; suspend cleanup/retries and provide recovery guidance |

Only after all expected target tracks are either verified Imported or KeptBetterExisting may the host settle the download as satisfied. History must distinguish what changed from what was deliberately preserved. An empty/no-improvement plan needs the same truthful verification and sidecar disposition before settlement. A partially executed but recoverable operation must not be falsely marked complete by old history.

## Verification matrix before any write-enabled prototype

- Baseline: 11 upgrades and 1 better kept; 0 upgrades/12 kept; 12 upgrades; missing tracks; equal-quality decisions; configurable quality/revision/cutoff.
- Metadata: wrong MusicBrainz release, two releases with the same recordings, repeating recordings inside one release, multi-track/cue file, dynamic remapping between plan and commit.
- Sidecars: registered vs unregistered library `.lrc`, staged `.lrc`, mismatched basename, duplicate/conflicting lyrics, ImportExtraFiles false and true, rename, staging folder with no remaining audio.
- Failure injection: crash before/after every journal transition, stage verification mismatch, destination full, copy truncation, failed original backup, old path renamed but new path absent, DB commit error, cleanup interruption, retry after restart.
- Filesystems: temporary local ext4-like disposable fixture first, then a **synthetic dedicated SMB test share** with no actual library media. Validate reconnect, mount identity, server ACK uncertainty, permissions/ownership, temp-file visibility and concurrent scanners.
- Host lifecycle: manual vs completed-download callers, 2 plugins intercepting same service, plugin absent/disabled/ABI mismatch, clean uninstall, notification/events/queue/history under all conditions.
- Security: path traversal/symlink escape, race between preflight and execution, untrusted archive metadata, operation journal path disclosure, stale hashes, cleanup limited to operation-owned directories.

Every test must distinguish assertions about mocked call order from assertions about real bytes, actual DB rows or end-to-end download-client cleanup.

## Implementation and upstream decision points

**Smallest upstream question:** Can Lidarr provide a safe pre-destructive, per-track import plan callback and typed completion outcomes while making its baseline album replacement non-destructive, independently of plugin presence? PR #6166's selective-removal mechanism may offer a starting point, but it is conditional and not a general quality upgrade guarantee.

**Follow-on question:** Can host-owned staged replacement and extra-file preservation be delivered as small, separately testable upstream changes? A single Crescendarr plugin could then supply selection policy, with no additional daemon and no direct SABnzbd/LRCGET dependency.

**Unacceptable shortcuts:** overriding only IDownloadedTracksImportService and delegating to the current destructive importer; direct plugin SQLite writes; pretending a kept-better file was imported; copying/renaming files without recovery journal; relying on backups as a replacement for a tested design; shipping a fork as the promised one-plugin install.

**Current decision:** NO-GO for write-enabled Crescendarr. Next step is independent architecture review of this proposal, followed by an executable *synthetic* recovery protocol prototype in an upstream test fork if the design survives review. The project owner has not authorized any outreach or upstream PR, and no production data is involved.
