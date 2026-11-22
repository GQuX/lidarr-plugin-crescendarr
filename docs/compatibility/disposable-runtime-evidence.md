# Crescendarr disposable runtime probe: first verified result

**Run date:** 2026-11-22  
**Result:** `EXPERIMENTAL_INTERCEPTION_ONLY`  
**Evidence:** [GitHub Actions #45020067092](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45020067092)  
**Branch/PR:** [PR #4](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/4)  
**Host binary:** Official Lidarr `v3.1.6.5079` Linux x64 release, archive SHA-256 `fed16c58f5de797be955e830a3a93994f020c1d8d60f5a6cb061d7647d45b8bc`.

## Environment

Ephemeral GitHub Actions Ubuntu runner. Fresh temporary Lidarr configuration/database, no external downloader, no production API keys, no mounted NAS, no real media, local loopback HTTP only. Guarded test-only `Lidarr.Plugin.Crescendarr.Probe.dll` loaded from `<fresh-data>/plugins/GQuX/CrescendarrProbe/`. A disposable sentinel and explicit opt-in environment variables were provided.

## Observed log evidence

```text
CRESCENDARR_PROBE constructor selected; concrete_original=NzbDrone.Core.MediaFiles.DownloadedTracksImportService; assembly=3.1.6.5079
CRESCENDARR_RUNTIME SERVICE_SELECTED=NzbDrone.Core.Plugins.CrescendarrProbeImportService
CRESCENDARR_PROBE ProcessPath intercepted; mode=Auto; NO DELEGATION
CRESCENDARR_RUNTIME INTERCEPTION_OK
DISPOSABLE_RUNTIME_PROBE_OK: DI selected Crescendarr and synthetic-only method was intercepted
```

The workflow also reported `localhost_http_responded=True`, `selected_service_logged=True`, `synthetic_interception_logged=True`, and `source_marker_still_present=True`. It asserts that the synthetic directory contains only the sentinel after the test. The executable exited after the runner requested shutdown.

## What is actually proven

1. Lidarr v3.1.6.5079 can load the disposable plugin assembly when placed under its nested plugin-directory discovery pattern.
2. DryIoc selected `CrescendarrProbeImportService` for `IDownloadedTracksImportService` in this single-plugin test host.
3. The plugin constructor received a concrete `DownloadedTracksImportService` instance without an observed recursive-resolution failure.
4. The plugin's `IHandle<ApplicationStartedEvent>` handler resolved and ran.
5. A **synthetic test path**, not a download-client job, called `ProcessPath` and was intercepted by the deliberately non-delegating service, returning an empty list.
6. A local HTTP endpoint responded, showing the test host had started.
7. No production environment was available to the workflow.

## What is NOT proven

- The import service wrapper is a documented or supported Lidarr plugin interception API.
- Any real completed download follows this custom service when invoked by download tracking (the handler called it directly).
- Any imported file is handled safely; the probe never delegates, copies, moves, renames, or deletes.
- Download completion and queue/history can truthfully handle no improvement, partial upgrade, failures, retries, or zero results.
- Monitored MusicBrainz release identity is preserved.
- Existing or staged `.lrc` sidecars survive rename, cleanup, rollback, or storage failure.
- SMB/CIFS crash recovery, ownership/permissions, duplicate files, or concurrency.
- Compatibility with Tubifarry, other plugins, or different Lidarr versions.
- Clean uninstall, fallback to stock host implementation, non-opt-in behavior or supported lifecycle recovery.

## Decision and next gate

**Keep the DI seam as an experimental integration hypothesis, not an approved production interface.** This test clears the first runtime-selection gate, but not the release gate. Continue [issue #2](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/2) with clean-disable/unload, lifecycle and completion behavior. Prefer an upstream-supported import-planning/settlement contract if the existing seam cannot safely preserve data. See [issue #3](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/3) for overlapping upstream Lidarr PR #6166.

Never install this non-delegating probe on a production Lidarr server; doing so can disrupt real imports.
