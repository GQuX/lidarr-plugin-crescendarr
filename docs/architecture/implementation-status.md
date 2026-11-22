# Crescendarr engineering checkpoint: stock-Lidarr-only route (2026-11-22)

**Production readiness: NO-GO.** Pure-planner and test-only probes exist. No production-ready installer, file-writing importer or verified NAS recovery.

## Nonnegotiable owner requirements

- Unmodified stock Lidarr (target stable 3.1.6.5079 / .NET 8), with no custom binaries, upstream patches or fork required.
- Single plugin, no separate daemon or additional music manager.
- SABnzbd remains configured through Lidarr, no new direct SABnzbd API dependency.
- Preserve existing better audio, the selected MusicBrainz edition, and registered/unregistered .lrc.

## Verified native evidence (effects mocked)

- Pure planner: 29/29 synthetic C# specs passed.
- Native Lidarr: [21/21 tests](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45029118148) establish album-wide pre-wipe, individual delete-before-transfer, registered lyric removal, history settlement weakness, and MissingFromDisk scan cleanup.
- Disposable host: outer importer DI selection was observed; this does not prove a safe released plugin.
- New [24/24 native NUnit cases](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45030193297) include three Completed Download Handling (CDH) boundary tests: OFF suppresses stock automatic imports for pending completed jobs; ON calls normal importer; **OFF still permits removal notifications for previously Imported removable jobs**.

## Active stock-only workaround hypothesis

A user could explicitly opt into Crescendarr-managed imports with stock Lidarr Completed Download Handling persistently OFF. The in-process plugin would use Lidarr's configured client/tracking APIs to discover completed jobs and selectively import with verified backups, lyric protection and safe recovery. The stock queue hides completed items under that setting, so plugin status/retention/retries must be independently tested.

**No proof yet** that a plugin can observe and process all jobs safely or keep native file/DB/sidecar state consistent. Manual import bypasses CDH OFF; rescans can also alter associations. An additive import spec alone cannot disable the stock album-wide replacement call. A guarded IImportApprovedTracks decorator with replaceExisting=false remains only a hypothesis because stock per-track replacement deletes first.

## Next gates and decisions

1. [#16](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/16): disposable plugin-only completed-job observation while CDH OFF, queue/cleanup and ABI fallback.
2. [#15](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/15): additive spec discovery, supplementary only.
3. [#6](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/6) and [#7](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/7): protected native imports, lyrics, and crash recovery.
4. [Stock-Lidarr feasibility design](stock-lidarr-plugin-only-feasibility-v0.1.md): alternatives and safety gates.

**No production Lidarr, NAS, SMB, SABnzbd, Tubifarry or music changed.** Do not disable CDH, install probes or process real downloads based on these tests. A future config change requires operator approval.