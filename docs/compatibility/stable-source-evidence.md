# Stable v3.1.6.5079 source compatibility checkpoint

Date: 2026-11-22. Evidence type: GitHub repository files **at the exact Lidarr tag**, not a running container.

- [Lidarr.Host.csproj](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Host/Lidarr.Host.csproj) targets `net8.0`, DryIoc.dll 5.4.3, DryIoc MS.DI 6.2.0.
- [Bootstrap.cs](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Host/Bootstrap.cs) uses `GetPluginAssemblies()`, `PluginLoader.LoadPlugins()`, `AutoAddServices(assemblies)`. This does not prove overriding the original import service works.
- [DownloadedTracksImportService.cs](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/MediaFiles/DownloadedTracksImportService.cs) defines `IDownloadedTracksImportService`, `ProcessRootFolder`, `ProcessPath`, `ShouldDeleteFolder`. The service handles actual filesystem imports and must not be patched on production.
- [Plugin.cs](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Plugins/Plugin.cs) defines the plugin metadata root.
- [Lidarr.Core.csproj](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Lidarr.Core.csproj) includes NLog and System.IO.Abstractions references.

To run pure planning specs in an independent .NET 8 development environment:

```sh
dotnet --info
dotnet run --project tests/Crescendarr.Core.Spec/Crescendarr.Core.Spec.csproj -c Release
python tools/validate-source.py
```

The test-only import probe intentionally cannot be built without an explicit disposable host opt-in. Do not copy it into production Lidarr. Neither a service wrapper nor a release installer has been validated.
