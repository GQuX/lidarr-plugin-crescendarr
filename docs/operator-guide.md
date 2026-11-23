# Crescendarr operator guide (EXPERIMENTAL, disposable hosts only)

**Production write-enabled is NO-GO.** The plugin is inert unless all of these are present:
- `CRESCENDARR_M17_OPT_IN=DISPOSABLE_SYNTHETIC_ONLY`;
- `CI=true` and `GITHUB_ACTIONS=true`;
- a marked `CRESCENDARR_M17_ROOT`.

Every write is confined to that root.

## Configure (stock Lidarr UI, no fork)

Settings > Connect > **+** > **Crescendarr (EXPERIMENTAL, disposable hosts only)**. This is an ordinary stock notification provider: Lidarr renders the form and stores it in its own database.

| Setting | Values | Default |
|---|---|---|
| Mode | Disabled, **Audit only**, Guarded auto | Audit only |
| Cycle interval (minutes) | 1–1440 | 5 |
| Max imports per cycle | 1–10 | 1 |
| Quality policy | Artist profile, strict upgrade only | (only option) |
| Retention | Keep everything | (only tested option) |

The **Test** button reports whether the host is inert, and why. More than one Crescendarr Connect entry is treated as a configuration error, and nothing runs.

## What runs automatically

- **Trigger:** stock Lidarr runs `RefreshMonitoredDownloads` every minute. When it finishes, the plugin's handler pushes one queued **`CrescendarrCycle`**, at most once per interval and never while one is already queued or running. A restart also pushes one. The handler itself never writes.
- **Each cycle is one stock `RequiresDiskAccess` command:**
  1. **Recovery first (Guarded auto).** Every journaled, unfinished operation is reconciled before any new work. Audit only performs no recovery and reports it as pending.
  2. **Observe every completed download** from stock download clients whose name starts with `Crescendarr Synthetic`, read only through Lidarr's own client code.
  3. **Plan** each download with the stock decision maker and Crescendarr's per-track planner.
  4. **Import only fresh `Ready` plans** (Guarded auto), and only for albums with no unfinished operation. Anything else is recorded for review with zero library change.
- **Download registry** (`crescendarr/downloads/*.json`): one record per download folder and content digest. It records the state, plan, `pluginHandled`, `lidarrImported=false`, `downloaderRemoved=false` and `sourceRetained`. A download already handled with the same bytes is never processed again.
  - The plugin **never** calls a client's `RemoveItem`, never deletes a source and never marks a stock queue item imported. Completed Download Handling stays off.

## Review and approval

- **System > Status** (stock health check):
  - an **error** lists every operation that needs an operator;
  - a **warning** lists Audit-only downloads whose Ready plan awaits approval.
- **Approve an Audit-only download:** queue `CrescendarrImport` with `downloadId` and `planId` from its registry file. A stale plan ID is refused.
- **Inspect:** `CrescendarrOperatorStatus` writes `crescendarr/status.json`. Each operation folder also holds `review.json`: hashes, row IDs, identities, lyric linkage, reasons, and what salvage did.
- **Act:**
  1. `CrescendarrPropose {operationId, action}` is a dry run. It returns a token bound to the operation, journal generation, action and a fresh host snapshot.
  2. `CrescendarrApprove {operationId, action, token}` re-checks and applies.
  3. The actions are:
     - `register-placed`: a file was placed but has no row;
     - `link-placed-row`: a row exists but the track was not linked;
     - `salvage-lyrics`: restore or register lyrics only;
     - `resolve`: release the album block once everything is verified. This is not import completion and not download settlement.
- **Never delete `crescendarr/ops/<id>`.** After a destructive rescan it can hold the only copy of your lyrics.

## Known limits

- **Cutoff searches:** stock wanted/cutoff searches may still grab albums Crescendarr considers done. Stock has no per-track "satisfied" concept, and no plugin-only suppression was found.
- **Settlement:** with CDH off, the download stays in the client and the stock queue shows it as untracked. Removal is left to the operator.
- **Health-check link:** the "more info" link points at a non-existent stock wiki anchor. That URL is built by stock code from the check's reason.
