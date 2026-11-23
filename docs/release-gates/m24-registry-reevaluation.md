# M24: download-registry re-evaluation without replaying completed imports (issue #61, draft PR #63)

**DRAFT, disposable CI only. Production write-enabled stays NO-GO.**

## The defect (red before the fix)

Before M24, `Automation.cs` skipped any download whose incoming-file digest matched a registry record in a "terminal"
state (`Imported`, `NoImprovement`, `NeedsReview`, `Blocked`, `RequiresReview`, `Refused`). All four new stock-daemon lanes
failed on the unchanged plugin in run [45222519782](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45222519782):

| Lane | What went wrong before M24 |
|---|---|
| `reeval-library-change` | Another actor deleted track 3 through the stock API, but the byte-identical second download stayed `NoImprovement` forever. |
| `reeval-profile-audit` | A quality-profile change never re-planned a `NoImprovement` download. |
| `completed-idempotent` | Changed bytes in an imported download's folder overwrote `Imported` with `NeedsReview`, and the completion evidence was lost. |
| `ambiguous-source` | Two stock clients reported the same folder, and the plugin imported it anyway. |

## The rule now (`Crescendarr.Core.Registry.DownloadRegistryPolicy`, a pure function with 21 specs)

**Identity.** A download is the client definition ID, the download ID, the folder and a digest of every incoming file's
SHA-256. **Completion evidence** comes only from the operation's *own* durable files: `outcome.json` must say
`Completed` and `operation.json` must bind exactly that identity. The registry is never trusted on its own.

| Previous record / evidence | Decision |
|---|---|
| A completed operation binds exactly this source | **Durably skipped** (`Imported`). This holds even if the library later changes and the fresh plan would be Ready again. Approved and recovered imports are recorded this way too. |
| A completed operation exists, but the bytes, download ID, client or folder differ | **`IdentityConflict`** review. The completion evidence is kept and nothing is imported. |
| The record says `Imported` but no completed operation can be verified | **`CompletionUnverified`** review |
| The operation for this download is unfinished (Active, RequiresReview, StorageUnavailable, …) | **Locked** (`OperationUnfinished` or the operation's state). Recovery or an operator owns it, and the registry never clears or bypasses it. |
| An operator resolved or abandoned the operation | Re-planned and shown, marked `manualOnly`, and **never imported automatically** again |
| `NoImprovement`, `NeedsReview` or `Blocked` | **Re-evaluated whenever the fresh evaluation changes** (disposition, plan ID or reason). Otherwise skipped quietly. The plan ID covers the album's TrackFile/Track/lyric state, the monitored edition, the incoming files and now the **artist's quality profile** (ranking, cutoff, upgrade flag). |
| `AwaitingApproval`, `Deferred`, `AlbumBlocked`, `Refused`, `RefusedUnsafePath`, `AmbiguousSource` | Always re-evaluated. Any import still goes through `RunImport`'s fresh re-observation and plan-ID gate. |
| Several client items report one folder or one download ID | **`AmbiguousSource`**. Not inspected, and a durable, locked or review record of that folder is never overwritten. |
| An unreadable or foreign registry record | Preserved as `<record>.unreadable-<utc>`, then **`RegistryUnreadable`** review with no plan and no import |
| `IdentityConflict`, `CompletionUnverified` or `RegistryUnreadable` | Sticky until an operator removes the record |

**Visibility.**
- Each state change is logged once (`CYCLE_DOWNLOAD … previous=…`). Unchanged downloads produce no per-download lines.
- The cycle status counts `reevaluated` and `registryReview`.
- System > Status warns about every registry review state.
- `pluginHandled`, `lidarrImported=false` and `downloaderRemoved=false` are unchanged.

**Compatibility.** Records written before M24 (schema 1) are read:
- an `Imported` record is accepted only after its operation is verified;
- conclusions without a fingerprint are re-evaluated once.

The M20 `auto-audit` lane now expects an approved import to be recorded `Imported`, not `NoImprovement`. This is deliberate: a `NoImprovement` record could be re-planned into a second import after a library change.

## Evidence

The red run is above. The green runs are listed in PR #63, together with the M17–M23 suites on the same head.
