# Crescendarr

**Selective, quality-aware track merging for Lidarr.**

> **DEVELOPMENT SOURCE ONLY. NOT INSTALLABLE. NOT SAFE FOR PRODUCTION.**
> The plugin integration seam, import lifecycle, recovery and `.lrc` preservation have **not** been proven in a running Lidarr host.

## Hard integration requirement: unmodified Lidarr (2026-11-22)

The owner has ruled out **upstream Lidarr code changes, forks or custom Lidarr binaries**. Crescendarr must work as **one plugin on stock stable Lidarr**; a companion daemon or separate download manager is also out of scope. All upstream-only designs are historical research, **not the implementation roadmap**.

**Current investigation:** [stock-Lidarr plugin-only feasibility](docs/architecture/stock-lidarr-plugin-only-feasibility-v0.1.md), tracked in [#16](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/16). The leading test hypothesis uses **Lidarr's own Completed Download Handling persistently OFF** while an in-process plugin observes jobs through Lidarr's configured download clients and selectively imports them. This would change one normal configuration setting only with user approval; it is neither implemented nor proven safe. It can hide completed jobs from Lidarr's stock queue and shifts cleanup/status/retry responsibilities to the plugin. Manual import and rescans remain separate hazards.

No write-enabled plugin exists. Do not disable CDH or install anything on the user's production instance on the basis of this proposal.

## Architecture safety decision (2026-11-22)

An independent review, followed by [Lidarr source verification](docs/reviews/2026-11-22-independent-review-triage.md), found a destructive mismatch: stock Lidarr can delete an entire album before selective imports, recycle/delete linked extras, and retry without plugins after some startup failures.

**Do not pursue IDownloadedTracksImportService as a write-enabled release mechanism.** The current probe characterizes disposable-host dependency injection only. Priority is [native import characterization (#6)](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6), [completion/lyrics/recovery (#7)](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7), and upstream compatibility with [PR #6166](https://github.com/Lidarr/Lidarr/pull/6166). The single-plugin goal is conditional on a safe host integration point. Our [independently reviewed incremental import proposal v0.2](docs/architecture/host-selective-import-incremental-v0.2.md) replaces the overlarge [v0.1 draft](docs/architecture/host-recoverable-import-protocol-v0.1.md). Both are **unimplemented** and require upstream Lidarr changes; safe standalone production operation remains unavailable.

## Stock-Lidarr read-only observer milestone

The [disposable stock host test](docs/compatibility/stock-lidarr-readonly-observer-evidence.md) loaded a **separate, read-only** observer plugin that resolved Lidarr's configured download-client factory with **no importer overrides**. A [26/26 passing native NUnit suite](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45031455402) separately verified that Lidarr's monitor still reads completed downloads from its configured client even with Completed Download Handling OFF, before filtering them from the stock queue.

**These are two separate proofs**, not a functioning plugin-managed download queue. The real host had zero configured clients; the native client had a mocked completed item. No actual audio or user downloader was accessed.

## Completed job observed inside stock Lidarr with CDH OFF

[The synthetic-client disposable-host test](docs/compatibility/stock-cdh-off-configured-client.md) has now confirmed an **actual in-process** read-only Crescendarr observer receiving a completed item from Lidarr's **configured built-in Usenet Blackhole client** with Completed Download Handling set to OFF via **stock local API**. The same ephemeral configuration was restarted **without the plugin**, and CDH remained OFF while the synthetic `.lrc` survived intact. [CI evidence](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45033044145).

**This validates completed-job observation and a narrow uninstall fallback, not safe per-track file merging.** The test had no audio to import, no SABnzbd connection, and no real library or NAS; all file replacement, edition, lyrics/DB consistency and crash recovery remain release blockers. Do not change production Completed Download Handling or install the disposable probe.

## Native lyric-row recovery validated in disposable SQLite

An independent plugin-only architecture review identified a viable **test-only** lyric-protection method: unlink a registered `.lrc` database row through Lidarr's own `IExtraFileService<LyricFile>` before a target upgrade, then reinsert a new association for the verified replacement TrackFile. [Six new tests, **32/32 total passing**](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45034922371), including **real migrated disposable SQLite** and real synthetic `.lrc` text bytes, show that the DB-only unlink preserves the physical lyric and that a new `Id=0` is required for reinsert. [Full review triage](docs/reviews/2026-11-22-plugin-only-claude-review-triage.md).

This does **not** yet demonstrate a safe audio import or a collision-free crash recovery path. No actual per-track replacement was performed in these tests. Do not enable writes or modify production Lidarr based on these results.

## Native 12-track import against disposable SQLite and physical bytes

An incremental [stock-Lidarr integration test](docs/compatibility/native-sqlite-synthetic-import.md) now runs `ImportApprovedTracks.Import(..., replaceExisting:false)` with a **real native upgrader and native TrackFile/Track/release repositories** backed by migrated disposable SQLite. It moves 11 incoming temporary byte files, preserves an existing better twelfth file, and uses native lyric-row unlink/reinsert to reassociate preserved `.lrc` bytes. All results are asserted from the test files and database. [Native CI: 36/36 passing](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45038820816). A second scenario injects a failed sixth transfer *after* deletion, observes a missing native TrackFile row and restores the old bytes, track association and lyric link **inside the test process**.

**This is not production-safe media merging.** The `.mp3` and `.flac` names wrap non-audio synthetic bytes; native audio tag reading/writing, complete identification and stock disk mover/recycler are still excluded, and the event aggregator is mocked. No process crash/restart, rescan race, actual download, multi-edition or SMB durability test has passed. No write-enabled plugin release exists.

## Interrupted SQLite recovery across separate test processes

[Cross-process native recovery CI](docs/compatibility/native-cross-process-sqlite-recovery.md) now passes on the pinned, unmodified Lidarr source: **36/36 base native tests, 1/1 interrupted-checkpoint preparation and 1/1 recovery in a separate new test process** ([GitHub Actions evidence](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45039966505)). The first process saves a consistent migrated SQLite snapshot after the sixth replacement is interrupted. The second process reopens that snapshot and uses native Lidarr repository/service methods to restore the original test bytes and all track/lyric associations while preserving the other ten upgrades and the better twelfth file.

**This is not an actual Lidarr server restart or crash recovery implementation.** Native event dispatch, rescan races, journaling, actual stock disk transfer, valid tagged audio and SMB durability are still untested. All media are opaque test bytes on a disposable runner, not any user's library. Crescendarr remains non-installable and unsafe for production.

## Native scan cleanup versus registered lyrics

[Two new native SQLite/physical lyric tests](docs/compatibility/native-scan-lyric-sqlite.md) show how stock `MediaFileTableCleanupService.Clean` detaches a missing TrackFile (`TrackFileId=0`). When the real native lyric deletion handler is explicitly invoked, a still-registered `.lrc` is recycled, whereas a `.lrc` DB-unlinked in advance survives physically. [CI: 38/38 native tests and both cross-process recovery stages passing](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45040268264).

This is **not a full Lidarr rescan or event-dispatch test**. The event handler is called directly, and a test-controlled recycler performs the physical move. Recovery under actual stock event order, startup scans and SMB remains unproven.

## Real native deletion-event dispatch

[PR #29](docs/compatibility/native-event-dispatch-sqlite.md) now runs the real unmodified Lidarr EventAggregator against selected real stock TrackService and LyricFileService handlers with migrated disposable SQLite and synthetic physical lyrics. Missing-file cleanup detaches the track, while a previously DB-unlinked lyric physically survives; a still-linked lyric is recycled. [Native CI 40/40, plus separate-process prepare/recovery 1/1 each](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45041825078).

**Limited test scope:** the fixture explicitly wires these two subscribers and a test-controlled recycler. It is not the complete host event graph, native disk transfer, true startup rescan, or a production-safe import executor.

## Real local stock filesystem mover and recycling

[Source-pinned filesystem test](docs/compatibility/native-real-local-file-transfer.md) now runs stock **TrackFileMovingService**, **RecycleBinProvider**, **DiskTransferService**, the actual Linux disk provider, mount discovery and symlink resolution on disposable, valid 16-bit mono PCM WAV samples. Original audio is retained in the test recycler; the staged upgrade moves into place without affecting an unrelated better original or adjacent .lrc; a still-occupied destination rejects the move with no clobber. [CI: 42/42 native plus both cross-process recovery stages passed](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45042406597).

This is **not** the full native 12-track importer with actual disk mover. The fixture controls filename policy and peripheral host services; real tags/MP3/FLAC, simultaneous rescans and CIFS/SMB behavior are not proven. Crescendarr has no production-ready write-enabled plugin.

## 12-track importer with stock native transfer and real SQLite

[The combined source-pinned M12 test](docs/compatibility/native-full-12track-stock-disk-sqlite.md) now invokes **unmodified stock Lidarr's importer, per-file upgrader, real mover, real recycler, native disk service and SQLite repositories together** on a disposable 12-track album. It imports 11 strictly preferred candidates, keeps the better twelfth original byte-identical, retains all physical `.lrc` bytes and re-links 11 replacement lyric records to their new TrackFile IDs. [Native CI: 43/43 passed, plus both separate-process recovery phases](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45042718808).

**Not yet playable-audio or production evidence:** the combined importer uses opaque synthetic media bytes and a mocked audio tag reader/writer, with controlled filename/root policy, a mocked event aggregator and one monitored release. Full multi-release identification, crash-safe plugin journaling, concurrent stock rescans and SMB durability remain unproven.

## Source-proven two-edition import hazard

[Native two-edition SQLite characterization](docs/compatibility/native-two-edition-sqlite-import.md) proves stock Lidarr **switches the monitored MusicBrainz release** if approved incoming tracks reference an alternate release, even when called with `replaceExisting:false`. Two new cases run the real 12-track importer + stock local disk mover + SQLite; the selected release stays selected only when incoming release IDs already match. [CI: 45/45 native plus both separate-process recovery phases passed](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45049081859).

**Executor invariant:** before any native import or file changes, verify the selected monitored release ID from the host against every incoming release; block the whole plan on mismatch. The pure planner checks IDs, but no write-capable host adapter or crash-safe journal exists yet.

## What we are building

A single Lidarr plugin that plans missing-track imports and strictly preferred individual upgrades without replacing entire album releases. It will preserve the monitored MusicBrainz edition, unrelated/better originals, and both existing and staged `.lrc` lyrics. It will not depend on Tubifarry, LRCGET, SABnzbd APIs, a daemon, or any other application besides compatible Lidarr. SABnzbd remains managed by Lidarr.

The current source is **only**:

- `src/Crescendarr.Core/`: pure deterministic merge planner. No Lidarr references, no disk/database/network access, and no file-write operations.
- `tests/Crescendarr.Core.Spec/`: standalone executable specifications using synthetic identities and a **fake** quality policy. They do not validate Lidarr's real quality profiles.
- `src/Crescendarr.Lidarr.Probe/`: deliberately non-delegating, test-only registration experiment. It has **no installer, no release workflow** and must not be put into production Lidarr. CI has compiled and loaded this probe in a disposable host; the probe has never processed a real download. The outer import-service override is not a release design.
- `deploy/portainer/disposable-lidarr-stable.compose.yml`: no-production-mount disposable host example (separate `/config`, port 8687); this does **not** install the probe.
- `tools/check_disposable_compose.py` and `tools/tests/`: development-only Compose lint with synthetic safety regression checks, including image-digest support and a loopback-only WebUI. These cannot prove network isolation.
- `docs/`: pinned compatibility hypotheses, safety boundaries, Portainer guide, experiment protocol and report template.

No installable Crescendarr import executor, queue settlement, or recovery implementation exists yet. The native test fixture exercises stock Lidarr import on disposable synthetic files only.

## Running the disposable Portainer stack lint

This is a development configuration check only. Requires Python and PyYAML, never connects to Docker.

```sh
python -m pip install pyyaml
python tools/check_disposable_compose.py
python -m unittest discover -s tools/tests -v
```

For deployment instructions, read [Portainer disposable stable lab](docs/portainer/lidarr-stable-lab.md). **Do not copy production settings, downloads, database, or media mounts.**

## Running the pure planner specs

Requires the **.NET 8 SDK**:

```sh
dotnet run --project tests/Crescendarr.Core.Spec/Crescendarr.Core.Spec.csproj -c Release
```

No NuGet test framework dependency is needed. **GitHub Actions compiled and executed all 29 core specifications successfully on 2026-11-22.** The authoring environment has no local .NET SDK; the CI result applies to the pure planner, not to the test-only Lidarr probe or production imports.

## The critical host experiment

Read [disposable host protocol](docs/safety/disposable-host.md). The test-only probe requires exact disposable-host opt-in, a sentinel file and an explicit synthetic-root allowlist. It intentionally returns empty import decisions, which may cause *Lidarr's own* queue/import state to change. Use it only in an isolated host with a fresh disposable DB and no real downloads.

The [first disposable runtime test](docs/compatibility/disposable-runtime-evidence.md) showed the test-only service selected and its own startup handler directly calling a synthetic `ProcessPath`. **That is not a real Lidarr download/import caller.** **This is not a supported API nor proof of safe automatic imports.** The source-pinned Lidarr import interface at commit `a754943cbd85cf9257e984506e0143ede18135fe` is not an endorsed plugin interception API. We must prove type selection, startup, original concrete resolution and unload before designing a write-enabled plugin. A DI wrapper alone would **not** solve completion/edition/lyrics safety.

## Documentation

- [Host contract and uncertain integration seam](docs/compatibility/host-contract.md)
- [Disposable-host probe protocol](docs/safety/disposable-host.md)
- [Proposed upstream API if needed](docs/architecture/upstream-interface-proposal.md)
- [Project architecture blueprint](docs/architecture/blueprint.md)

## Project status

- [x] Name and dependency boundary agreed
- [x] Initial pure domain/planner and synthetic specs authored
- [x] Experimental host-probe source authored
- [x] Disposable Portainer example and digest-aware, loopback-only configuration lint (synthetic checks, not host isolation)
- [x] .NET 8 pure-core compile and 29/29 synthetic specs passed in [GitHub Actions](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45018712104)
- [x] Test-only adapter compiled against checksum-pinned Lidarr 3.1.6.5079 Linux binaries ([compile-only CI](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45019459665))
- [x] Disposable Lidarr v3.1.6.5079 host selected the test-only importer and intercepted a synthetic call ([CI evidence](docs/compatibility/disposable-runtime-evidence.md))
- [x] Isolated baseline, missing opt-in, and positive synthetic-interception tests ([CI #45020330835](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45020330835))
- [x] Lidarr-native 12-track characterization ([3/3 NUnit tests](docs/compatibility/native-12-track-characterization.md)): stock `replaceExisting=true` pre-deletes even the better track; `false` skips only album-wide pre-wipe but still calls `SetMonitored`
- [x] Native fault injection ([6/6 NUnit tests total](docs/compatibility/native-upgrade-failure-characterization.md)): original recycle and DB deletion are invoked before a failing replacement move **or copy**; real files and rollback remain untested
- [x] Native lyrics and download-completion characterization ([16/16 NUnit tests total](docs/compatibility/native-lyrics-completion-characterization.md)): linked `.lrc` is recycled during upgrade; lyric-only staging directory passes cleanup check; intentional 11+1 upgrade is failed without qualifying history. **No physical file handling or recovery was tested.**
- [x] Actual native history predicate and rescan cleanup characterization ([21/21 NUnit tests](docs/compatibility/native-history-rescan-characterization.md)): a single TrackFileImported history entry can satisfy a 12-track album; MissingFromDisk path cleanup unlinks track files and can recycle registered `.lrc`. **No real database or filesystem tested.**
- [ ] Real import caller, no-improvement and partial-upgrade settlement, clean unload/fallback, unsupported-version and multi-plugin behavior
- [ ] Supported pre-import contract with truthful zero/partial/full outcomes
- [ ] Verified `.lrc` preservation, edition lock and recoverable SMB-safe upgrades
- [ ] Build a real installable plugin

GitHub development repository: https://github.com/GQuX/lidarr-plugin-crescendarr. Licensed under [0BSD](LICENSE): reuse, modification, and redistribution are permitted without attribution requirements.

## Portainer lab deployment (v0.1.1)

Use **a new Portainer stack**, not the production multi-service stack. The lab Compose includes an explicit Watchtower exclusion and a fresh `/config` volume. See `docs/portainer/lidarr-stable-lab.md`. Never install the probe in production.

## Stable-target decision (2026-11-22, v0.2.0)

Lidarr stable v3.1.6.5079 has plugin loading in its tagged source and targets net8.0.
This source now targets .NET 8. Production Lidarr is a **compatibility reference only**.
The operator confirmed an independent backup and Watchtower exclusion; **no probe may be installed**
on that live container. The pure .NET 8 planner compiled and passed 29/29 synthetic specifications in GitHub Actions. The test-only host probe compiled and loaded in a clean disposable Lidarr runtime; **real tracked download processing and all file/queue/lyrics/recovery safety remain untested**.
Full input snapshot hashing is now included in merge plan identifiers, but these fingerprints
are advisory: only a host-owned revalidation and recoverable import executor can authorize writes.

## Upstream overlap

[Lidarr PR #6166](https://github.com/Lidarr/Lidarr/pull/6166) (open and unmerged when checked on 2026-11-22) proposes song selection, selective imports, and queue-aware recording intent. That work may supply a safer host-owned seam or make some Crescendarr functionality redundant. We are tracking its changes before considering any upstream submission. The PR does **not** on its own verify our automatic album-download upgrade rules, edition-lock guarantees, `.lrc` handling, crash recovery, or SMB-safe replacement semantics. See [upstream overlap notes](docs/architecture/upstream-overlap-pr6166.md).

**Host ABI check:** `.github/workflows/probe-abi.yml` downloads the pinned `v3.1.6.5079` Linux x64 release, checks SHA-256, discovers `Lidarr.Core.dll` and `Lidarr.Common.dll`, and compiles the non-installable adapter. It never executes Lidarr or installs the probe. A green result is **not** permission to test on a real library.

## Disposable runtime milestone (2026-11-22)

[GitHub Actions #45020067092](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45020067092) started stock Lidarr v3.1.6.5079 with a new empty database and our guarded probe, confirmed DryIoc chose the Crescendarr service, resolved Lidarr's concrete original without recursion, and intercepted a synthetic method call made directly by the plugin's own startup handler, not by Lidarr's real download pipeline. **No real download-client job, import, replacement, .lrc sidecar, or crash recovery was tested.** [Evidence and limitations](docs/compatibility/disposable-runtime-evidence.md).
