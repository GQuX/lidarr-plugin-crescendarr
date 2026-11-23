# M21 acceptance matrix and pre-release gates (issue #52, draft PR #55)

**Scope and limits:**
- **Host:** the official SHA-256-pinned stock Lidarr v3.1.6.5079 daemon on disposable CI runners, with exactly one Crescendarr plugin assembly.
- **Status:** no release is published. Production write-enabled is **NO-GO**, and no installation on a user NAS or library is recommended.

**Evidence** (final M21 head `693d751`): every workflow below is green on this head.

| Workflow | Run | Result |
|---|---|---|
| M21 | [45163002812](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45163002812) | 4/4 |
| M20 | [45163002780](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45163002780) | 4/4 |
| M19 | [45163002721](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45163002721) | 4/4 |
| M18 | [45163002739](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45163002739) | 7/7 |
| M17 | [45163002782](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45163002782) | 4/4 |

**Legend:**
- **PROVEN:** exercised on the real stock daemon, with DB rows and bytes verified.
- **FAIL-CLOSED:** exercised; the unsupported case is refused with zero mutation.
- **UNTESTED:** not exercised. Do not infer.

## Matrix

| Area | Case | Status | Risk if wrong | Evidence |
|---|---|---|---|---|
| Identity | Real MusicBrainz artist / release-group / 2 editions / recording / release-track IDs (captured CC0 fixture, SHA-pinned) | **PROVEN** | High | `realistic-rename` 123 checks |
| Identity | Other edition of the same release group (same recordings, different release-track IDs) | **FAIL-CLOSED** | High | `realistic-rename` |
| Identity | Repeated recording MBID on two release tracks (derived from the fixture) | **FAIL-CLOSED** (`DUPLICATE_RECORDING`, NeedsReview) | High | `realistic-repeated` |
| Identity | Unicode / case-folded titles and paths | UNTESTED (the fixture is ASCII) | Medium | — |
| Naming | Rename ON, standard and multi-disc formats (`… (1995)/CD 0n/…`); download names differ from destinations | **PROVEN** (27 tracks, in place at the stock-named paths) | High | `realistic-rename` |
| Naming | Rename OFF | **PROVEN** | High | M17–M20 lanes |
| Naming | Conflicting destination / dangling link at destination | **FAIL-CLOSED** | High | M18 `three-guards` |
| Discs | Multi-disc album (2 × 14) | **PROVEN** | High | `realistic-rename` |
| Extras | Album-folder `cover.jpg`, `.nfo` in the download | **PROVEN untouched / ignored** | Medium | `realistic-rename` |
| Extras | Unidentifiable extra audio in the download | **FAIL-CLOSED** (whole download refused) | Medium | `realistic-rename` |
| Extras | `.lrc` case variants, other sidecars (`.txt`, `.cue`), embedded art | UNTESTED | Medium | — |
| Lyrics | Every album lyric preserved across replacement | **PROVEN** (no `.lrc` recycled; 26 audio files recycled) | High | `realistic-rename`, M17–M19 |
| Clients | Stock Usenet Blackhole | **PROVEN** | — | M17–M21 |
| Clients | **Stock SABnzbd integration** (Lidarr's own client against a CI-only mock SAB API): waits while Extracting, imports when Completed, never sends delete, source kept, no duplicates | **PROVEN for the mock** | High | `sab-client` 20 checks |
| Clients | A real SABnzbd server, retries, failed jobs, remote path mappings, qBittorrent/NZBGet | UNTESTED | High | — |
| Crash | Post-move/pre-row (upgrade and add), post-row/pre-link, after lyric restore/bind, mid-album rescan cascade (12 tracks) | **PROVEN** (recovery or typed approval) | Critical | M18, M19 |
| Crash | During the byte transfer; during the stock rescan; power loss; directory fsync | UNTESTED / source-level only | Critical | — |
| Faults | Corrupt or missing lyric backup, conflicting bytes, stale rows, dangling links, foreign aliases of fresh rows, symlinks and out-of-root aliases | **FAIL-CLOSED** | Critical | M18 negatives/guards, M19 ambiguous |
| Faults | Hardlinks and bind mounts (not visible from paths) | UNTESTED | High | — |
| Faults | Unreadable or corrupt journal | **FAIL-CLOSED** (pure specs; sticky review) | High | journal lab |
| Automation | Stock-driven cycles, recovery first, Audit only default, Guarded auto, dedupe, Disabled, health surface | **PROVEN** | High | M20 4/4 |
| Settlement | Download never removed or deleted; no false stock "imported" | **PROVEN** | High | M20, `sab-client` |
| Settlement | Stopping stock cutoff re-searches for per-track-satisfied albums | **Not achievable plugin-only** (documented) | Medium | — |
| Safety | Inert without opt-in; writes only in `RequiresDiskAccess` commands; no Lidarr setting changed by install/upgrade/uninstall | **PROVEN** | Critical | `install-cycle` 11 checks, M17 boot 3 |
| Packaging | Reproducible single-dll build (two builds byte-identical) with SHA256SUMS, plus an install/upgrade/uninstall cycle | **PROVEN** on a disposable host | Medium | all M21 jobs (zip `45c0c6b3…`, dll `b3eb0c66…`) |
| Storage | SMB/NFS/NAS semantics, case-insensitive file systems, Windows hosts | UNTESTED | Critical | — |

## Defect found and fixed by M21

With renaming ON, stock `FileNameBuilder.AddQualityTokens` dereferences `TrackFile.Quality`. Before this fix, Crescendarr's predicted-destination call threw a `NullReferenceException` (`FileNameBuilder.cs:388`). The bug was invisible in every earlier lane because renaming was off.

The plugin now mirrors the `TrackFile` fields that stock `ImportApprovedTracks` sets. Any remaining mismatch still fails closed at the bound-destination check (commit `693d751`).

## Shortest honest route to a production-safe opt-in

1. **Real downloader:** a real SABnzbd in a disposable container, plus retries, failed jobs and remote path mappings.
2. **Storage:** SMB/NAS and power-loss characterization of the journal, backups and stock moves. This is the gate that keeps production NO-GO.
3. **Lifting the guard:** replace the CI-only opt-in with an explicit, documented user opt-in (default Audit only), keeping every other guard.
4. **Field trial:** a read-only (Audit only) field trial on a copy of a real library before any write is enabled.
5. **Retention:** a tested retention policy; today everything is kept.
