# M25: audit-only beta release candidate (issue #62, draft PR #64)

**Candidate status: built, packaged and tested in CI only. Not published, not installed anywhere else.** Production write-enabled stays **NO-GO**.

**A first field install, even of the audit-only build on a real library, needs an explicit human decision.** See *Opt-in proposal* below.

## What the candidate is

`crescendarr-audit-beta-0.25.0-audit-beta.1.zip` is a CI artifact of `.github/workflows/m25-audit-beta.yml`. It contains:
- `Lidarr.Plugin.Crescendarr.dll`;
- `README-AUDIT-BETA.md` (install, configuration, upgrade, rollback, uninstall, troubleshooting, privacy, limitations);
- `CHANGELOG.md`, `LICENSE` and `THIRD-PARTY-NOTICES.md`;
- `SHA256SUMS`, plus an outer `.zip.sha256`.

It is the **same single plugin** (same assembly identity) built as a second flavor, `src/Crescendarr.Lidarr.AuditPlugin`. That flavor compiles only:
- the pure `Crescendarr.Core` planner and registry policy;
- `Disk.cs`;
- the read-only `HostAdapter`;
- the audit files.

The importer, recovery, salvage, operator approval and every writer command are **not in the binary**. The disposable-CI writer build is unchanged and still drives M17–M24.

## How "read-only" is enforced (independent layers)

1. **Absent code.** The writer commands (`CrescendarrImport`, `Recover`, `Approve`, `Propose`, `Cycle`, `Plan`, `OperatorStatus`) do not exist in this build. Stock Lidarr's command API rejects them as unknown commands with HTTP 500 "Sequence contains no matching element". No command row is created, and nothing is queued.
2. **Mechanical scan** (`tools/audit-scan`). It reads the DLL's metadata and IL and fails the build if:
   - any writer type is defined;
   - any Lidarr service or member that mutates media, lyrics, links, settings or clients is referenced;
   - any filesystem mutator is called outside the barrier sites.

   It passes the audit DLL. As a positive control it **rejects the writer DLL**, with 36 violations listed in the job log.
3. **Write barrier** (`CRESCENDARR_AUDIT_BUILD`). Every `Disk` write calls `AuditBarrier.RequireStatePath`, which allows only `<AppData>/crescendarr-audit/` and refuses:
   - paths that overlap any library root folder;
   - paths reached through links.

   There is no setting, environment variable or command that changes it.
4. **Configuration fails closed.** Only *Disabled* (the default) and *Audit only* validate. A `GuardedAuto` value written straight into the database makes the build observe nothing and raise a health **error**.

## Acceptance matrix

| # | Requirement (issue #62) | Status | Evidence |
|---|---|---|---|
| 1 | Separate audit-only build; writer commands impossible even with injected commands or an injected GuardedAuto mode | **PROVEN** | `audit-readonly`: all 8 injected writer commands refused by stock with no command row; the DB-injected `mode=2` gives a health error, a skipped cycle and zero mutation. Scan passes on audit, rejects writer. |
| 1 | Writes only its own local diagnostics | **PROVEN** | `audit-readonly`: every file written is under `<AppData>/crescendarr-audit/`. The barrier is in code and checked by the scan. |
| 1 | Startup never changes stock configuration | **PROVEN** | Stock settings snapshot (media management, client config, clients, quality profiles, root folders, naming, other Connect entries) is identical before and after, in both lanes. |
| 2 | Ordinary client names and explicit selection; never all clients silently | **PROVEN** | The client is renamed `Usenet Blackhole` and selected by ID. An unselected client's completed download is never observed. Default *Disabled* observes nothing for 75 s. |
| 2 | Completed, wrong-edition, path-refused, offline, partial and disabled-client states are visible, read-only | **PROVEN** for blackhole. **MOCK-ONLY / NOT TESTED** for the audit build with real SABnzbd. | `audit-readonly`. Real SABnzbd was proven with the writer build in M22, not re-run with the audit build. |
| 2 | No `RemoveItem`, no direct downloader API, no queue settlement | **PROVEN** | The scan finds no `RemoveItem` reference. Download sources are byte-identical before and after. |
| 3 | Per-track Upgrade / Keep / Add / Review, current vs incoming quality, lyric preservation, review reason, redacted diagnostics | **PROVEN** | The report shows track 1 Upgrade, track 2 Keep (FLAC current), track 3 Add. The wrong edition carries the planner and Lidarr reasons. The redacted file has no names or paths. The health warning says "AUDIT ONLY … WOULD change". |
| 4 | BEFORE/AFTER immutable baselines across cycles, injection, restart, invalid config, outage, unknown paths, no permission, profile change | **PROVEN** | `Baseline.compare` checks every library file and folder name and its bytes, TrackFile/Track/LyricFile/AlbumRelease rows, download trees and stock settings. |
| 4 | Report updates when library metadata or profile changes | **PROVEN** | A profile change alters the preview, and restoring the profile restores it. The library is unchanged. M24 re-evaluation lanes are green. |
| 5 | No probe or test file in the library; unreadable library is never read as "new" | **PROVEN** | `chmod 000` on the artist folder gives `LibraryUnavailable` with no Add rows. No `*probe*` file exists anywhere. |
| 5 | Unmounted or stale SMB library | **PROVEN for the writer build (M23)**, same `Disk.Probe` primitive; **NOT TESTED** with the audit build on kernel CIFS | M23 |
| 5 | Symlink or path escape, disallowed folder | **PROVEN** (disallowed folder → `RefusedUnsafePath`); symlinked download folder **NOT TESTED** in this lane (the code refuses link chains) | `audit-readonly` |
| 5 | Stock Lidarr may still change its own library | **DOCUMENTED** | README "What it cannot do" |
| 6 | Pinned Lidarr version and hash; deterministic build and zip | **PROVEN** | Two clean builds give byte-identical DLLs. Two packagings give byte-identical zips. Lidarr is pinned at `fed16c58…`. |
| 6 | Install, upgrade, rollback, uninstall | **PROVEN** | `package-lifecycle`: installs from the zip (DLL checked against `SHA256SUMS`), upgrades to `audit-beta.2` (settings and registry kept), rolls back, then uninstalls (Connect removed, plugin folder removed, stock settings equal to before install, library unchanged throughout). |
| 6 | State and registry migration | **PROVEN** (registry reused across upgrade and rollback). The writer-to-audit switch on one host is **NOT TESTED**: the two use separate state folders. | |
| 6 | Screenshots of the Connect UI | **NOT TESTED** (described in README) | |
| 7 | M22 SABnzbd restart-path flake | **ROOT-CAUSED in M27** (harness race on SABnzbd's not-yet-filled `storage`; fixed in the harness) | M22 doc |
| 7 | M23 stock CIFS timestamp/rescan limitation | **OPEN (stock behaviour)** | M23 doc, README limitations |
| 7 | Non-MP3 payload integrity | **WRITE-ENABLED BLOCKER** (audit build only reports) | |
| 7 | Physical power loss | **WRITE-ENABLED BLOCKER, NOT TESTED** | |
| — | Windows or macOS Lidarr | **NOT SUPPORTED** (the plugin stays inactive with a health error) | |
| — | Lidarr versions other than 3.1.6.5079 | **NOT TESTED** | |
| — | CDH ON, so stock imports in parallel with the audit | **NOT TESTED** (the audit never writes, so stock's own imports would be stock writes) | |

## CI evidence

**Candidate and lanes on `d19feee`:** run [45232414818](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45232414818). Every job is green:
- the double build is byte-identical (DLL `56f7d46e…`);
- the scan passes, and the writer build is rejected with 36 violations;
- the two packagings are byte-identical (zip `a4b390c0…`);
- the `audit-readonly` and `package-lifecycle` lanes pass.

**M24 on the same stack:** all four lanes are green (run 45229160476).

**M17–M23 on the M25 head `9a1d7ad`:** all green, including the six kernel-CIFS lanes.

**One flake.** M20 `auto-audit` failed once on `0fc72b3` with "health warning cleared". The registry and counters were already correct (`Imported`, `awaitingApproval=0`), so this is a race between the harness reading health and stock refreshing it. A rerun of the same commit passed (run 45229160588, attempt 2). It is recorded here, not hidden.

**Final heads:** see PR #64.

## Release checklist (owner decision required before every step marked ★)

- [x] Final head: M17–M24 suites green, M25 candidate, scan and both daemon lanes green (links on PR #64)
- [x] Candidate zip and `SHA256SUMS` downloaded from the CI artifact and compared with the job log
- [ ] ★ Owner reviews this matrix, `README-AUDIT-BETA.md` and the known limitations
- [ ] ★ Owner decides on a first field test (see the proposal)
- [ ] ★ Tag and **publish** a GitHub pre-release *only on explicit approval*. Not done.
- [ ] Field test feedback recorded. Redacted diagnostics only.

## Opt-in proposal for a first field test (for the owner to accept, change or reject)

1. **Host.** A *copy* of the Lidarr AppData folder and a *copy*, or read-only bind mount, of a small part of the library, on a non-production Lidarr 3.1.6.5079 on Linux. Not the production Lidarr.
2. **Install.** Install the verified zip, leave Mode *Disabled* first, and confirm `AUDIT_BUILD_READY … libraryWrites=impossible` and an unchanged library.
3. **Enable.** Turn on Audit only for **one** selected client and one allowed folder. Compare `report.md` with what the owner expects for 2–3 known albums.
4. **Check the library.** `find <library> -newer <marker>` must be empty after a day of cycles. Lidarr's own scheduled tasks are noted separately.
5. **Uninstall** using the documented steps, then share `diagnostics-redacted.json` only.
