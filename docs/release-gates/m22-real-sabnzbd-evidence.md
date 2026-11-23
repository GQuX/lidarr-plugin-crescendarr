# M22: real SABnzbd through stock Lidarr: evidence (issue #56, draft PR #57)

**DRAFT, disposable CI only. Production write-enabled is NO-GO.**

**What runs:**
- **Downloader:** a REAL SABnzbd **5.1.3** process. It comes from the pinned source release (SHA-256 `12a01e30…`), installed from its own pinned `requirements.txt` in a venv, plus the Ubuntu 24.04 `par2` and `unrar` packages; their versions are printed in the job summary.
- **Articles:** SABnzbd downloads synthetic, **par2-protected** NZBs over real NNTP from a CI-only localhost fixture (`tools/experiments/nntp_fixture.py`). SABnzbd does its own queueing, yEnc decoding, assembly, par2 verify/repair and history.
- **Lidarr:** official SHA-256-pinned v3.1.6.5079, using its **stock Sabnzbd client**. The client is saved through Lidarr's own connection test (no `forceSave`), with CDH OFF and removal of completed/failed downloads OFF.
- **Crescendarr:** the unchanged scheduler and download registry, observing only through `IDownloadClientFactory`.
- **Secrets:** API keys are random per run and redacted. No DB copies are uploaded.

**Command (per lane):**

```
run_disposable_m22.py <lidarr-bin> <plugin.dll> <seeder.dll> <sabnzbd-dir> <sab-venv-python> <lane>
```

The workflow is `.github/workflows/m22-real-sabnzbd.yml`.

**Final head** `05a18c7`. It includes the owner's stricter integrity commits `385af8a` and `61391e0`, which also reject truncated final frames and short trailing garbage.
- M22 [45185118806](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45185118806): **5/5**.
- Same head: M17 4/4, M18 7/7, M19 4/4, M20 4/4, M21 4/4, journal lab, planner and inventory specs. 35/35 jobs green.

**One intermittent, unexplained failure.** On `61391e0` (run [45180367826](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45180367826)), `sab-real-restarts` failed its "SAB storage equals the folder used" check after a correct import that followed a SAB SIGKILL/restart. The check was then instrumented to log both paths. On the final head they are identical, and the lane passed on three of four heads. The cause was not established at the time.

**M27 update: root cause found (a harness race, not a product defect).** Run [45261466914](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45261466914) logged `used='.'`: real SABnzbd briefly publishes the history slot as `Completed` before it fills in `storage`, and `RealSab.wait` returned that slot, so the lane compared against `Path('') == '.'`. SABnzbd's own later answer, the "reported" side of the comparison, was correct. The fix makes `RealSab.wait` wait for a completed slot that names its storage folder. The comparison itself is unchanged and still exact.

## Evidence table

| Behaviour | Status | Evidence |
|---|---|---|
| On-wire NNTP download, yEnc decode, assembly, history Completed in real SAB | **REAL-SAB-PROVEN** | `sab-real-happy` (30 checks, 13 articles served) |
| Real par2 verification (par2 sets in every NZB) | **REAL-SAB-PROVEN** | all lanes; "Verifying" state observed |
| Stock Lidarr Sabnzbd client `GetItems()` feeding the plugin; automatic 3-track selective import (MP3 upgrade, superior FLAC row+bytes+lyric kept, missing track added, edition kept) verified in SQLite and on disk | **REAL-SAB-PROVEN** | `sab-real-happy` |
| Completed folder intact; job still in real SAB history; storage path equals Lidarr's output path (no remote mapping) | **REAL-SAB-PROVEN** | all import lanes |
| SAB + Lidarr restart: no duplicate import, no status drift | **REAL-SAB-PROVEN** | `sab-real-happy` |
| Real failure (missing articles beyond par2: "Repair failed, not enough repair blocks"): no mutation, nothing recorded as handled | **REAL-SAB-PROVEN** | `sab-real-failed-retry` (27 checks) |
| SAB's own retry API, then Completed, then imported exactly once | **REAL-SAB-PROVEN** | `sab-real-failed-retry` |
| SAB SIGKILLed mid-download, restarted, resumed from its own queue; Lidarr down at completion; startup cycle imports once | **REAL-SAB-PROVEN** | `sab-real-restarts` (27 checks) |
| SAB unavailable: visible `CYCLE_CLIENT_ERROR` (errors=1), no mutation, no settlement | **REAL-SAB-PROVEN** (fixed in M22) | `sab-real-restarts` |
| Job completing **outside** the disposable root: refused with an actionable reason and a durable `RefusedUnsafePath` record; other jobs still import in the same cycle | **REAL-SAB-PROVEN** (fixed in M22) | `sab-real-paths` (33 checks) |
| Same job name, different content: separate SAB id and folder; registry identity is folder + content; never imported | **REAL-SAB-PROVEN** | `sab-real-paths` |
| **No-par2 job with a missing article reported Completed by real SAB; refused** (`lost MPEG frame sync…`), zero library change, registry Blocked | **REAL-SAB-PROVEN hazard, FAIL-CLOSED after the fix** | `sab-real-unverified-missing` (13 checks) |
| Crescendarr never asks SAB to delete | **REAL-SAB-PROVEN** (indirect: the job stays in history and the folder stays intact; there is no plugin HTTP code) | all lanes |
| Malformed SAB API responses | **MOCK-ONLY** (M21 `sab-client`), not reproducible with real SAB | — |
| RAR/7z unpack (`Extracting`), direct unpack | **NOT TESTED** (synthetic jobs are not archived; unpack disabled) | — |
| Remote path mapping (SAB on another host or container) | **NOT TESTED** (same host and paths) | — |
| SAB history ID changes (e.g. after a history purge) | **NOT TESTED** (retry kept or reissued an id; covered only as "different job, different id") | — |
| Restart of SAB *during post-processing* | **NOT TESTED** (post-processing finishes too quickly to interrupt) | — |
| Damaged frame payload with intact headers; non-MP3 incoming formats (FLAC etc.) | **NOT TESTED / NOT DETECTED** by the integrity check | — |

## Plugin changes caused by real-SAB behaviour

- **Red runs:** [45174766490](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45174766490) (cycle aborts) and [45176792988](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45176792988) (damaged download **imported**).
1. **`AudioIntegrity.Mp3Problem`** (pure Core, with spec). Every incoming MP3 must be a continuous MPEG frame chain, or the download is Blocked with the reason logged on `CYCLE_DOWNLOAD`.
2. **`HostAdapter.ObserveClients`** isolates each client and item. A failing client gives `CYCLE_CLIENT_ERROR`; an unsafe path gives `CYCLE_REFUSED_ITEM` plus a registry record. No new downloader integration and no direct plugin-to-SAB call.

## What blocks a first audit-only beta

- **Real-world storage and inputs:** SMB/NAS and power-loss durability, unpack/remote-mapping coverage, and FLAC/other-format integrity checks.
- **Opt-in and operations:** a real-user opt-in replacing the CI-only guard, a retention policy, and an audit-only trial on a copy of a real library.
