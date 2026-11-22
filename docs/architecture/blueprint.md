> **Post-review architecture update (2026-11-22, ADR-023):** The prior design assumption that Lidarr's stock importer can safely own all irreversible side effects has been **invalidated** by stable source review. Album-wide pre-wipe, per-file delete-before-copy, linked sidecar cleanup, delayed DB writes, and plugin-disabled startup fallback are documented in [the independent-review triage](../reviews/2026-11-22-independent-review-triage.md). The proposed `IDownloadedTracksImportService` write-enabled wrapper is a **NO-GO**. All prior designs of a host-owned coordinator are proposed future *changes* to Lidarr, not capabilities of stock v3.1.6.5079. No production integration permitted.

> **Revision note 2026-11-22:** v0.2.0 implementation now targets stable Lidarr v3.1.6.5079 and net8.0. Any net10/nightly-first recommendations below are historical research hypotheses, superseded by the new stable-target decision.

# CRESCENDARR

**Technical Architecture & Implementation Blueprint**  
**Version:** 1.1 (planning + source foundation)  
**Date:** 2026-11-22  
**Status:** Source foundation authored, not compiled or run; integration strategy remains unvalidated  
**Project name:** Crescendarr (formerly SmartMerge)  
**One-line description:** Selective, quality-aware track merging for Lidarr without destructive album replacement.

> **Source of truth for factual research:** `Crescendarr_Living_Research.md` v1.3, particularly historical sections 7, 12–16 and source-pinned findings SM-F-045 through SM-F-052. The research was performed against Lidarr commit [`a754943cbd85cf9257e984506e0143ede18135fe`](https://github.com/Lidarr/Lidarr/commit/a754943cbd85cf9257e984506e0143ede18135fe) on 2026-11-22. This blueprint is a **design proposal**, not a claim of a working host API or successfully compiled software. **No production system has been touched.** The v0.1 source-only foundation is documented in the appendix.

## 1. Product definition

Crescendarr is **one installable open-source Lidarr plugin** that safely merges selected tracks from an album download into the matching existing album. Its distinguishing behavior is to add missing tracks, upgrade an individual track only when Lidarr's configured policy prefers the incoming candidate, and preserve every other existing track.

### 1.1 Hard dependency boundary

- **Only external application dependency: a compatible, plugin-capable Lidarr build.** No Tubifarry, LRCGET, Prowlarr, download helper, new daemon, external database, or independent music manager is required. Ordinary .NET assemblies bundled inside the plugin are implementation details, **not another installed application**.
- **SABnzbd is not a Crescendarr dependency:** Lidarr manages any configured download client and hands Crescendarr import candidates through the host's import lifecycle. SABnzbd is the first download-client integration fixture because it is the current use case. No direct SABnzbd API, credentials, queue management, or postprocessing hook in v1.
- **LRCGET is not a Crescendarr dependency:** it may independently fetch lyrics. Crescendarr protects existing and staged `.lrc` sidecars without calling LRCGET.
- **No production changes during development:** every runtime experiment uses an isolated Lidarr installation, database and synthetic source/destination media. The production NAS and its SMB mounts are never test inputs.
- **One plugin package and one user-facing install action.** It may contain more than one internal .NET DLL; that does not change the installation requirement. A forked Lidarr binary is **not** acceptable as a release workaround.

### 1.2 Explicit non-goals for v1

Release search or acquisition policy changes; download-client plugins; unpacking individual NZB articles; Soulseek/VPN/torrent integrations; lyric generation; spectrogram authenticity detection; transcoding; audio tagging/editing; modifying MusicBrainz catalog identity; cross-edition automatic switches; merging independently collected physical album editions; broad filesystem crawls; direct edits to Lidarr's database schema or track-file rows.

### 1.3 Operating modes, staged availability

| Mode | Meaning | Earliest exposure | Guarantee |
|---|---|---|---|
| `Observe` | Inspect a completed-download candidate and explain what would happen; **never suppress** the stock importer merely because instrumentation is installed | Disposable Milestone 1 only | Zero plugin-authorized writes; test only against synthetic input |
| `DryRun` | Generate and report a full immutable merge plan, with safe deferral under a validated host interception contract | Milestone 2 after safe interception | No media or DB writes by Crescendarr; no unsafe stock-import fallthrough |
| `AddMissing` | Commit confidently matched missing recordings, preserve all existing files | Milestone 3, gated | Host-controlled import and truthful queue settlement |
| `SafeUpgrade` | Add missing and selectively upgrade better-ranked recordings with durable recovery | Milestone 4, gated | Verifiable old-copy recovery, sidecars, edition and DB invariants |

**Important:** `Observe` is an instrumentation mode, not a shipping mode. A "dry run" that allows Lidarr to continue its default album-destructive importer is **not** a safe dry run. Until interception is proven, read-only tests must use fixtures that cannot mutate real music.

## 2. Architectural stance and trust boundaries

The plugin should own **decision policy**, but Lidarr must **first be changed or extended** to safely own irreversible operations. Stock v3.1.6.5079 cannot currently be assumed to provide recoverable side effects, track-safe deletion, truthful partial completion or reliable lyric preservation. A supported core-owned planner callback plus a typed completion contract is the preferred integration. Whether the current host supports it is **unknown**. A log-only `IDownloadedTracksImportService` wrapper is an investigation, **not a production dependency**.

```text
  Existing Lidarr download client (SABnzbd first fixture)
                         |
                         v
    [Lidarr completed download / import intake]
                         |
                         v
    [Pre-destructive host seam: UNPROVEN / P0]
                         |
              +----------+-----------+
              | Crescendarr Plugin   |
              |  Host Adapter        |<---- Lidarr album, quality,
              |  Candidate Snapshot  |      settings and file records
              |  Identity Resolver   |
              |  Quality Policy      |
              |  Merge Planner       |
              |  Sidecar Guardian    |
              |  Plan Validator      |
              +----------+-----------+
                         |
                    Immutable Plan
                         |
                         v
        [Lidarr-owned safe import coordinator:]
        [lock, preflight, recovery, mutation,    ]
        [DB association, reconciliation, queue  ]
        [settlement and staging ownership]        
                         |
           Outcomes / review / audit log
```

**No implementation may work around this boundary by importing files behind Lidarr's back and then manually rewriting the database.** If current Lidarr cannot implement host-owned safe import coordination, the write-enabled release is blocked pending a minimal upstream extension or another demonstrably safe and supported host mechanism.

## 3. Proposed single-package repository layout

```text
crescendarr/
├── README.md
├── LICENSE                       # choose after GPL/linking review
├── CHANGELOG.md
├── CONTRIBUTING.md
├── SECURITY.md
├── Directory.Build.props
├── global.json                   # pin SDK used by selected Lidarr build
├── src/
│   ├── Crescendarr.Core/
│   │   ├── Domain/                # immutable plans, snapshot and outcomes
│   │   ├── Matching/              # strict recording/edition matching rules
│   │   ├── Planning/              # pure deterministic decision engine
│   │   ├── Policies/              # normalized quality-policy comparisons
│   │   └── Validation/            # invariants on plans and selected targets
│   ├── Crescendarr.Lidarr/
│   │   ├── Plugin/                # actual plugin registration / metadata
│   │   ├── Adapters/              # translate native Lidarr models
│   │   ├── Integration/           # interception seam; gated/instrumented
│   │   ├── Settings/              # host-native configuration, no new service
│   │   ├── Sidecars/              # .lrc inventories and protection plan
│   │   ├── Lifecycle/             # host outcome mapping, no fake completion
│   │   └── Diagnostics/           # redacted reason codes and audit results
│   └── Crescendarr.TestKit/
│       ├── Fixtures/              # synthetic media, 12-track albums
│       └── Faults/                # deterministic fault injection contracts
├── tests/
│   ├── Crescendarr.Core.Tests/
│   ├── Crescendarr.Contract.Tests/
│   └── Crescendarr.DisposableHost.Tests/
├── docs/
│   ├── architecture/
│   ├── adr/
│   ├── safety/
│   ├── install/
│   └── compatibility/
├── tools/
│   └── disposable-host/          # isolated setup; never target live host
└── .github/workflows/
    ├── ci.yml
    └── release.yml               # not enabled until gates pass
```

This layout is **proposed**, not present on GitHub. The user downloads/installs one plugin artifact. `Core` contains no Lidarr references and is unit-testable without a host. `Lidarr` owns every external host type, SDK pin, plugin interface and compatibility gate. `TestKit` is development-only, never installed as a separately running component. A later package audit should decide which internal assemblies to ship.

## 4. Module contracts and responsibilities

| Component | Input | Output | Forbidden responsibility |
|---|---|---|---|
| `ImportIntakeAdapter` | Host-supplied download/import event | `DownloadSnapshot` | Poll or directly control SABnzbd |
| `AlbumSnapshotReader` | Album/edition reference | `CurrentAlbumSnapshot` | Scan whole music library |
| `RecordingMatcher` | Incoming tags, host matches, edition inventory | Candidate-to-recording confidence/map | Guess ambiguous editions |
| `QualityPolicyAdapter` | Lidarr quality profile, existing/incoming `QualityModel` | Normalized **host-aligned** comparison | Invent a universal codec hierarchy |
| `MergePlanner` | Immutable snapshots and policies | One immutable `MergePlan` | File/DB mutation |
| `SidecarGuardian` | Old/new target basenames, staged and library `.lrc` inventories | Preserve/relocate/conflict instructions | Fetch or overwrite lyrics |
| `PlanValidator` | Plan, snapshot and host capability report | Validated plan or reason-coded hold | Convert errors into successful imports |
| `HostImportCoordinator` **(contract only)** | Validated plan and current revalidation | Applied operation receipts | Be emulated by direct SQLite manipulation |
| `ImportReconciler` | Host receipts, on-disk files, actual DB links and edition | Truthful terminal outcome | Infer success solely from command status |
| `DiagnosticsReporter` | Plans and receipts | Redacted operator explanation | Leak media paths, API keys or file contents by default |

**Implementation boundary:** `HostImportCoordinator` is an interface representing capabilities required from Lidarr. It does **not** assert that this API already exists. No production implementation until runtime/source verification and maintainer-compatible design.

## 5. Domain model, identities and contracts

### 5.1 Stable keys

A download attempt is scoped to a host download ID + immutable source inventory fingerprint + Lidarr album ID + expected monitored release/edition ID. Every incoming audio file can map to zero, one, or multiple recording IDs; multi-track files are **atomic groups**, not independent deletable individual tracks. Every existing file has a Lidarr track-file ID, path and full set of linked tracks. Track title, track number and basename are **not sufficient identities** for an automatic upgrade.

Do not copy artist or album IDs from a different release/edition merely because titles match. Source-derived data may be incomplete; uncertainty means review.

### 5.2 Suggested pure C# types (illustrative; not compiled)

```csharp
public enum MergeAction { AddMissing, Upgrade, KeepExisting, NeedsReview }
public enum PlanDisposition { Ready, NoImprovement, NeedsReview, Blocked }
public enum TerminalOutcome
{
    FullyApplied, PartiallyApplied, NoImprovement,
    RequiresReview, RecoveryRequired, Failed
}

public sealed record TrackDecision(
    string CandidateKey,
    IReadOnlyList<string> RecordingIds,
    MergeAction Action,
    string ReasonCode,
    string Explanation,
    long? ExpectedExistingTrackFileId);

public sealed record MergePlan(
    string PlanId,
    string DownloadId,
    long AlbumId,
    string ExpectedEditionId,
    string InventoryFingerprint,
    PlanDisposition Disposition,
    IReadOnlyList<TrackDecision> Decisions);

public interface IMergePlanner
{
    MergePlan Plan(DownloadSnapshot incoming,
                   CurrentAlbumSnapshot current,
                   MergePolicy policy);
}
```

These are **Crescendarr-internal contracts**, not names copied from Lidarr's current API. Host-specific adapters resolve native models and quality criteria. Persistent IDs and edition identifiers must be taken from reliable host data, with equality/normalization chosen per the pinned host version.

### 5.3 Canonical decision rules

1. Unknown/ambiguous album, artist, edition, track mapping, linked compound file or contradictory tags: `NeedsReview`; never silently delete, "pick the closest" or switch editions.
2. Proven recording with no existing file: `AddMissing` if the candidate passes standard host quality, identity and path guards.
3. Proven recording with an existing file and **strictly preferred** incoming quality by Lidarr's applicable configured policy: `Upgrade` if its full file-to-track mapping is safe and all guards pass.
4. Equal/inferior/incomparable quality, missing quality evidence, unsupported custom-format signals or same-content-but-larger FLAC: `KeepExisting` or `NeedsReview`, never a default upgrade.
5. One incoming file mapping to several recordings must be evaluated and executed as an indivisible unit; if any linked track would be endangered, `NeedsReview`.
6. Any plan with zero allowed additions/upgrades resolves to `NoImprovement` **as a planning result**. Do not claim the host can settle it until the lifecycle seam is implemented.
7. With mixed decisions, all intended keeps and unrelated existing tracks remain linked to the current edition. The plan never contains an album-wide `replaceExisting=true` directive.

### 5.4 Host quality comparison

No generic rule like `FLAC > MP3` or `larger file = better` replaces Lidarr's actual configured quality/profile ordering. The adapter must validate whether it can reuse the exact relevant Lidarr comparer with the selected profile, revisions and supported custom formats. If not, return `UnknownComparison -> NeedsReview`, not a guessed result. Keep bitrate/authenticity detection separate from host preference. Compare actual files where possible, not only release title metadata.

## 6. Operation lifecycle and outcome contract

```text
Discovered
  -> SnapshotCaptured
  -> Identified
  -> Planned
     +-> NoImprovement ----------> HostNoOpSettlement* -> Reconciled
     +-> NeedsReview ------------> ReviewHeld
     +-> Ready
           -> PlanValidated
           -> PreflightRevalidated
           -> RecoveryPrepared* 
           -> HostImportStarted* 
           -> FilesAndDbReconciled
           -> DownloadSettled* 
           -> FullyApplied | PartiallyApplied

Any failure/changed evidence -> ReviewHeld | RecoveryRequired | Failed

* Requires a verified core-owned implementation/extension; NOT assumed
  to exist on stock Lidarr.
```

### 6.1 Completion truth table

| Plan | Actual outcome | Required representation | What may **not** happen |
|---|---|---|---|
| All selected files apply | Confirmed DB/files/edition and sidecars | `FullyApplied` | Success based only on task return value |
| Some selected files apply, others remain unsafe/pending | Explicit pending work and accurate receipts | `PartiallyApplied` + review/recovery until resolved | Silent full success or unsafe source cleanup |
| Zero incoming improvements | Full plan assessed; old library retained | `NoImprovement`, with host-defined safe queue/history settlement | Fake imported tracks; retry forever; auto-delete unexplained sources |
| Identity/edition/sidecar conflict | No risky mutations | `RequiresReview` | Default importer silently continues destructively |
| Interrupted copy/DB mismatch | Durable recovery evidence + receipt reconciliation | `RecoveryRequired` | Remove old recovery bytes or stage files |

**Critical research link:** Lidarr's currently inspected `CompletedDownloadService.VerifyImport` can mark a subset of returned imported decisions as full success, while mixed failures can cause `AlbumImportIncompleteEvent` (SM-F-048). A successful Crescendarr plan must therefore be reconciled against the **full expected album-state contract**, not the count of passed-back decisions.

## 7. Safety invariants, enforced as release gates

- **S1: Preservation.** Every unchanged existing audio file and all its DB track links survive exactly as before. No album-wide removal.
- **S2: Strict target mapping.** Selected upgrades target the same verified recording/edition and do not strand additional tracks linked to the same physical file.
- **S3: Edition stability.** The monitored MusicBrainz release/edition identity does not change in v1. Note that source code calls `SetMonitored(newRelease)` even in `replaceExisting=false` paths (SM-F-047).
- **S4: Old-copy recovery.** Before any old audio is replaced, its bytes, metadata/link identities and protected sidecars remain recoverable. A recycle bin alone is not proof of this.
- **S5: Staged source protection.** No stage audio or `.lrc` is silently removed until outcome, ownership and explicitly selected source disposition are reconciled. Lidarr's default extra-file list does not include `.lrc` (SM-F-050).
- **S6: Lyrics protection.** Existing library `.lrc` is never overwritten or deleted automatically. If filename/target path changes, preserve the correct sidecar association or hold for review. Incoming `.lrc` is not blindly imported over existing lyrics.
- **S7: Truthful lifecycle.** Full, partial, no-op, review and recovery states are distinct. No fabricated `DownloadCompletedEvent` or imported rows.
- **S8: Idempotence.** Repeated event, process restart or unchanged source inventory cannot apply the same upgrade twice. Changed inventory or DB state forces revalidation.
- **S9: Remote filesystem caution.** An SMB/CIFS disconnect, non-atomic move, disk-full error or permission issue must leave recoverable old bytes and a durable failure receipt. POSIX/SMB operations are not magically transacted with Lidarr's database.
- **S10: Fail closed.** Host-version mismatch, unverified plugin intercept, missing recovery capability, unsupported quality comparison, failed preflight or duplicate active plan must prevent automatic changes and emit actionable diagnostics.
- **S11: No independent external side effects.** No direct download-client API calls, broad NAS rescans or background daemons.
- **S12: No premature cleanup.** Download-folder cleanup must account for non-audio sidecars even when no audio remains. SM-F-049/050 show why relying only on `ShouldDeleteFolder` is insufficient.

### 7.1 Recovery protocol proposal

For **future, core-owned** safe upgrades only: capture durable intent -> revalidate selected old files and source IDs -> prepare replacement in a temporary target location -> verify new bytes/metadata and space -> secure old file + sidecars in a validated recovery location -> commit through host-controlled file and DB operations -> reconcile host DB, release ID, expected bytes, and untouched tracks -> permit cleanup and retire recovery only under explicit retention policy.

At every phase, record plan/version, source/target identifiers, old and new file fingerprints, observed host receipts, and a recovery status. A journal can support recovery but **is not a transaction across SMB and SQLite**. The correct host sequence and ability to undo/check DB writes must be proven by injected-crash tests. If any transition is not provably recoverable, upgrades remain unavailable.

## 8. `.lrc` sidecar policy

1. Capture both **library-side** and **download-staging-side** lyrics inventory before an operation. Treat matching by basename alone as provisional when renaming or multitrack mapping is involved.
2. Default: prefer the existing library `.lrc`; never clobber it with an incoming `.lrc` or delete it on audio upgrade.
3. If host renaming changes the audio basename, compute the expected final sidecar path with the **same verified recording mapping**. When ambiguity/collision exists, `NeedsReview` rather than guessing.
4. Source `*.lrc` files must not be swept up by standard stage deletion merely because imported audio has moved. A host-owned preservation/disposition contract must be available before any automatic stage cleanup is allowed.
5. Lyrics do not influence track quality ranking. LRCGET need not be installed or running; there is **no LRCGET API integration** in v1.
6. Test `.lrc` sidecars with plain lyrics, synchronized timestamps, Unicode, case-sensitive and case-insensitive collisions, multiple discs, alternative filename tokens, read-only NAS mounts, and failure between audio and lyric transition.

## 9. Integration strategy: alternatives and gate

### Option A (preferred): official Lidarr planner extension

Provide an **opt-in pre-destructive planner callback** after identification and before removal, plus a core-owned import transaction/reconciliation path and real `NoImprovement`, `PartiallyApplied`, `FullyApplied`, `RequiresReview`, `RecoveryRequired` results. Preserve core's validation/permissions and leave ordinary Lidarr behavior exactly unchanged with the plugin absent. Model ideas from [Lidarr PR #6166](https://github.com/Lidarr/Lidarr/pull/6166), but its availability/acceptance cannot be assumed.

### Option B (research only): `IDownloadedTracksImportService` wrapper

Pinned source shows plugin assemblies being loaded into the host DI container and DryIoc configured with Microsoft DI rules (SM-F-045/046). A test-only plugin *might* resolve the interface last and delegate to the concrete core service. Required proof: deterministic registration order; exactly one interception; no recursive construction; correct manual-import and lifecycle callers; safe fallback; clean uninstall; no double events; and no hidden destructive continuation. **Even a passing wrapper smoke test does not prove partial/no-op completion or old-copy recovery.** Until all of those contracts are demonstrated and stability assessed, do not make this the shipping integration method.

### Option C (fallback): upstream core change before write-enabled release

If neither supported API nor a complete, safe host-owned integration exists, stop at a **read-only demonstrator** and prepare the smallest upstream proposal. The user-facing outcome must remain one plugin on compatible official Lidarr; do not replace the host binary, patch vendor DLLs or race the default importer with an external script.

### Go/no-go rule

**No write-capable plugin artifact unless we can reliably intercept BEFORE default destructive import and can faithfully represent zero/partial/full completion, keep `.lrc` safe and recover old bytes after injected faults.** A working planner is not sufficient.

## 10. Configuration and operator UX

Potential settings, added only when their underlying behavior is verified:

| Setting | Initial value | Notes |
|---|---|---|
| `Enabled` | Off | Until safe host integration is verified |
| `Mode` | `DryRun` | `AddMissing` and `SafeUpgrade` unlocked only by release maturity |
| `AmbiguityPolicy` | `HoldForReview` | Not overrideable in v1 for edition or compound-file conflicts |
| `PreserveExistingLrc` | On, mandatory | Cannot be disabled in v1 automatic operations |
| `PreserveStagedLrc` | On, mandatory | Must participate in cleanup guarantees |
| `AllowEditionSwitch` | Off, unavailable | Out of scope in v1 |
| `AutomaticReplacement` | Off | Explicit opt-in after backups and safety gates |
| `Concurrency` | 1 | One album operation at a time initially |
| `Diagnostics` | Concise | Reason-coded, redact paths by default; no secrets |

Do not display controls that imply a guarantee the host cannot yet enforce. First user-visible report should show album/current edition, each file's decision and reason, counts of `AddMissing`/`Upgrade`/`KeepExisting`/`NeedsReview`, source sidecars, estimated write operations, blockers and final reconciliation status.

## 11. Deterministic tests and synthetic fixture matrix

| ID | Fixture | Pass condition |
|---|---|---|
| F01 | 12-track album, 11 verified upgrades, one better existing track | 11 selected; 12th preserved byte-for-byte, links and lyrics unchanged |
| F02 | 12-track album, one missing and 11 existing | Exactly one `AddMissing`; no unrelated file changes |
| F03 | All 12 incoming candidates inferior/equal | `NoImprovement` plan; host outcome never forged; no endless retries |
| F04 | Different MusicBrainz edition but same names/numbers | `NeedsReview`; monitored edition unchanged |
| F05 | Existing FLAC vs larger FLAC | Keep (size alone never wins) |
| F06 | MP3 vs FLAC with host profile favoring MP3 in context | Follow **host policy**, not codec folklore |
| F07 | One audio file linked to two recordings | Atomic group or hold; no orphaning |
| F08 | Existing library `.lrc`, incoming `.lrc` with same basename | Existing lyric preserved; staged copy protected/explicitly reviewed |
| F09 | Audio rename due to Lidarr naming format | Correct verified `.lrc` association, no orphaned lyrics |
| F10 | Staging has only `.lrc` after audio move | Stage cleanup must not silently discard it |
| F11 | SMB disconnect at each write transition | Old bytes recoverable, correct `RecoveryRequired` state |
| F12 | Permission denial/disk full/cross-filesystem move | No silent loss; queue reflects unresolved state |
| F13 | Download event replay / host restart | Idempotent operation and recovered journal |
| F14 | Manual-import path calls same interface | No unintended interception/fallback or silent partial completion |
| F15 | Uninstall plugin / incompatible Lidarr update | No broken Lidarr startup, no latent override or writes |
| F16 | 60+ unrelated completed downloads and delayed import | Current download state tracked without global rescans or history assumptions |
| F17 | Source file swaps between plan and import | Preflight rejects or recalculates; no wrong target changes |
| F18 | Existing file missing/DB link stale | Hold; no destructive normalization |

Use generated synthetic audio, SQLite/test databases and disposable CIFS/SMB fixture where feasible. Never test against personal album files. **Snapshot before/after hashes and DB associations** are the acceptance oracle, not UI command status alone.

## 12. Development milestones and exit criteria

### M0: Pin and document the host contract

- Check exact Lidarr release/commit, plugin loader behavior, .NET version and supported package layout.
- Check plugin loader and host import service sources against the target build.
- Output: versioned `docs/compatibility/host-contract.md`, ADRs, test harness design.
- **Exit:** one reproducible disposable build available; no production configuration touched.

### M1: Read-only host integration experiment (first code)

- Generate a minimal **log-only** Crescendarr plugin, without production flags or write permissions.
- Record default and plugin DI resolution for `IDownloadedTracksImportService` and the concrete original; call order, manual caller behavior, and teardown.
- Ensure plugin cannot trigger regular destructive imports during a claimed dry run; use dummy rejected fixture and compare event log and source hashes.
- **Exit:** exactly-once, nonrecursive, startup-safe interception **or** a documented fail verdict and upstream-hook specification.

### M2: Pure planning engine and dry-run proof

- Implement core domain records, strict identity matching, host-aligned quality adapter, deterministic planner, reason codes and sidecar inventory.
- Implement fixture-driven tests F01–F10 plus property tests for unaffected-file preservation.
- **Exit:** all planning decisions deterministic, explainable and correct; no media mutation.

### M3: Missing-track-only import, disposable environments

- Only if supported core coordinator and truthful queue state exist.
- Commit verified missing recordings; capture edition, track-file IDs and stage lyrics before/after.
- **Exit:** F02, F03, F04, F08–F10, F14, F15 pass in actual disposable host. No failed/no-op download misclassified as success.

### M4: Recoverable selective upgrades, disposable environments

- Require reliable old-copy retention and full host reconciliation. Add single-target upgrades first, then mixed 11/12.
- Inject SMB disconnect, disk full, rename and restart at each transition; validate retained `.lrc` and release.
- **Exit:** F01 and F05–F18 pass, no unrecoverable bytes, no cross-edition changes or source cleanup surprises.

### M5: Publish restricted beta, then stable

- Publish compatibility matrix, installation guide, SPDX/license analysis, recoverability instructions, versioned ZIP release and checksums.
- Keep `SafeUpgrade` opt-in. Stable needs replayable fixture corpus, regression CI, release rollback and upgrade-path evidence.

**Work that may start in parallel now:** README, ADRs, domain models, deterministic fixtures, test harness, license review. **Work that may not start as production functionality yet:** replacing core services, intercepting live completed downloads, deleting stage folders, or performing selective media upgrades.

## 13. Initial ADRs for this blueprint

| ID | Decision | Status |
|---|---|---|
| C-ADR-001 | Official name **Crescendarr**; historical codename SmartMerge | **Accepted by user** |
| C-ADR-002 | One installable plugin; only compatible Lidarr as external app dependency | **Accepted by user** |
| C-ADR-003 | No LRCGET/Tubifarry integrations or dependency; preserve `.lrc` regardless | **Accepted requirement** |
| C-ADR-004 | Core planner is pure; Lidarr adapter owns all host type knowledge | **Proposed** |
| C-ADR-005 | Host owns irreversible import, queue, history and DB association | **Proposed, blocking capability test** |
| C-ADR-006 | DryRun -> AddMissing -> SafeUpgrade exposure order | **Proposed** |
| C-ADR-007 | Keep monitored edition frozen during v1 merge | **Proposed non-negotiable constraint** |
| C-ADR-008 | Host core planner contract preferred; DI wrapper is a disposable probe | **Proposed** |
| C-ADR-009 | No external metadata or database services, no background daemon | **Accepted requirement** |
| C-ADR-010 | Bundled internal assemblies permitted; no extra plugin installation | **Proposed packaging interpretation** |

## 14. What is known versus what is still unproven

**Source-observed:** automatic Lidarr import currently passes `replaceExisting=true` into approved import; host DryIoc uses MS DI rules; `SetMonitored` can happen in combine path; completed-download results can misrepresent intentionally selected subsets; audio-only staging cleanup can leave `.lrc` vulnerable, and `.lrc` is not an extra-file default. See source-pinned SM-F-045–052.

**Not tested:** actual plugin DI registration/delegation; compatible shipping pre-import hook; correct no-op/partial host states; SMB crash recovery; whether a plugin can safely defer default auto-import; official packaging compatibility with a selected nightly.

**Design inference:** independent pure planner, host-owned coordinator, journal, typed terminal states, API seams, and staged release policy. These are constraints and implementation proposals, **not existing guaranteed Lidarr capabilities**.

## 15. The next concrete coding task

Create **M1: `crescendarr-host-probe`** on a disposable host, producing a machine-readable report of:

1. Target Lidarr build SHA, framework and plugin loader assembly list.
2. DI resolution for `IDownloadedTracksImportService` without plugin and with the logging probe; selected type and whether concrete fallback is resolvable.
3. Trace for interface methods including `ProcessPath`, `ProcessRootFolder` and `ShouldDeleteFolder`, plus manual import caller if applicable.
4. Explicit confirmation of zero duplicate calls, zero recursive resolution, unchanged synthetic filesystem/DB snapshots and clean uninstall.
5. The decision: **SUPPORTED SEAM FOUND / EXPERIMENTAL WRAPPER ONLY / UPSTREAM CHANGE REQUIRED**.

Do not start file-replacement code until this result and the separate host-completion contract are known. If the machine lacks a .NET 10 SDK, commit test scaffolding and reproducibility notes, but mark the executable smoke test **NOT RUN**, not "passed".

---

### References

- [Pinned Lidarr host/DI bootstrap](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Host/Bootstrap.cs)
- [Pinned Lidarr composition rules](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Common/Composition/Extensions.cs)
- [Pinned Lidarr automatic import](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs)
- [Pinned Lidarr approved import](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs)
- [Pinned Lidarr completed download](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/Download/CompletedDownloadService.cs)
- [Pinned Lidarr extras](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/Extras/ExtraService.cs)
- [Lidarr Song Mode/selection PR #6166](https://github.com/Lidarr/Lidarr/pull/6166) (research snapshot open as of 2026-11-22)
- [Servarr Wiki plugin documentation](https://github.com/Servarr/Wiki/blob/master/lidarr/plugins.md)
- Previous full evidence register: `Crescendarr_Living_Research.md` v1.3 (historical files under `/SmartMerge`).


---

## 16. M0/M1 implementation-source checkpoint (2026-11-22; v1.1)

The initial source files are now present in `Crescendarr_Source_Foundation_v0.1.0.zip`, but **have not been compiled or tested with a .NET SDK or Lidarr host**. The archive includes a pure C# merge planner, 23 synthetic executable specifications, a deliberately non-delegating and build-guarded import-service probe, current source-pinned host contract, disposable-host instructions, and a blank result report.

- `Crescendarr.Core` is independently testable, file-I/O-free and has a host-quality comparison interface rather than a guessed codec ranking.
- A speculative `IDownloadedTracksImportService` wrapper is included **only as a non-installable source experiment**. It cannot import music and must never be tested outside a clean disposable instance. It does not prove a supported pre-import seam or real lifecycle correctness.
- A `SidecarRequirement` preservation flag is **not a completed `.lrc` guardian**; file rename and staging cleanup tests remain blocked by host integration.
- The host `net10.0` and DryIoc behavior are pinned source observations, not verified compatibility with the actual deployed Lidarr build.
- `python3 tools/validate-source.py` returned `STATIC_STRUCTURE_OK`, verifying source layout and basic manifest integrity. All C# compile/spec tests and host tests remain `NOT RUN`.

**Next user input:** redacted Lidarr build/version/branch, install type, and whether a separate empty disposable Lidarr can be used. Do not request the real music library or any credentials.
