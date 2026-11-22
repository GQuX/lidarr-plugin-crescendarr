# Current stable development baseline (v0.2.0; 2026-11-22)

**Target:** Lidarr tag [`v3.1.6.5079`](https://github.com/Lidarr/Lidarr/tree/v3.1.6.5079), .NET 8. The tagged source has the plugin loader, plugin metadata and `IDownloadedTracksImportService` with matching signatures. Installed LinuxServer package `3.1.6.5079-ls49` and runtime `.NET 8.0.27` were reported by the user, not inspected as binary artifacts. Source inspection is not a host runtime test.

The earlier net10 research snapshot below remains valuable for pipeline hazards, but it is **not** our build target. All actual DI resolution, completion, sidecar and recovery claims remain unverified.

# Lidarr host contract: research snapshot and outstanding integration proof

**Status:** pinned source inspected, **no real host exercised**.

Lidarr repository SHA: [`a754943cbd85cf9257e984506e0143ede18135fe`](https://github.com/Lidarr/Lidarr/commit/a754943cbd85cf9257e984506e0143ede18135fe) (inspected 2026-11-22). That is a reference source snapshot, **not** the user's installed Lidarr binary identity.

| Host feature | Source-observed | Requires disposable execution |
|---|---|---|
| Plugin load | `Bootstrap.CreateConsoleHostBuilder` loads plugin assemblies, adds to base assemblies before `AutoAddServices` | Actual order/enablement in target build |
| DI | `WithNzbDroneRules()` enables MS-DI rules, auto concrete resolution; likely last registration for interface | Actual selected service; construction graph; effect of multiple plugins |
| Interceptable interface | `IDownloadedTracksImportService` in `MediaFiles/DownloadedTracksImportService.cs` with `ProcessPath`, `ProcessRootFolder`, `ShouldDeleteFolder` | Whether a plugin can reliably replace/decorate this interface |
| Concrete fallback | `DownloadedTracksImportService` is discoverable as a concrete type and not self-injecting the interface | Whether injectable without recursion |
| Return value | `List<ImportResult>`; zero selected can be treated as incomplete by downstream | No-op/partial/full completion and queue events |
| Edition | `ImportApprovedTracks` may call `SetMonitored(newRelease)` independently of `replaceExisting` | A host-owned enforced edition invariant |
| Staging | Folder cleanup is based on audio/large RAR checks; `.lrc` is not default extra-file import | Source `.lrc` preservation and cleanup ownership |
| Historical runtime | Host project targets `net10.0` at this SHA, **not the stable target** | Stable binary and plugin TFM compatibility |

## Reference files

- [Bootstrap.cs](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Host/Bootstrap.cs)
- [Extensions.cs](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Common/Composition/Extensions.cs)
- [DownloadedTracksImportService.cs](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs)
- [PluginLoader.cs](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Common/Composition/PluginLoader.cs)
- [PluginLoadContext.cs](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Common/Composition/PluginLoadContext.cs)
- [Plugins metadata](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/Plugins/Plugin.cs)
- [CompletedDownloadService.cs](https://github.com/Lidarr/Lidarr/blob/a754943cbd85cf9257e984506e0143ede18135fe/src/NzbDrone.Core/Download/CompletedDownloadService.cs)
- [Servarr plugin installation overview](https://github.com/Servarr/Wiki/blob/master/lidarr/plugins.md)

## Source-only probe caveats

`Crescendarr.Lidarr.Probe` is **not** a release plugin and is not compiled here. The test deliberately does not delegate to the stock importer; therefore it can only test DI selection and *attempted* method interception. It cannot prove that actual imports will remain safe, that queue state is correct or that sidecars survive. Its assembly references must be checked against actual host binary names before building.

## Needed from the operator (read only)

- Lidarr **System → Status → About** branch and version/build (or a redacted screenshot).
- Install type: Docker/LXC/native, plus platform CPU architecture.
- Whether an **entirely separate disposable Lidarr instance** can be used with a clean database and synthetic paths; no production NAS mount.

Do **not** provide API keys, SMB credentials, download paths with personal information, backup archives or music files.
