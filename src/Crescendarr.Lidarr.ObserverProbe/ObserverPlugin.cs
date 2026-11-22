namespace NzbDrone.Core.Plugins;

/// <summary>
/// NOT an installable Crescendarr release. It only tests DI with stock Lidarr.
/// No importer, filesystem writer, downloader provider or background task.
/// </summary>
public sealed class CrescendarrObserverProbePlugin : Plugin
{
    public override string Name => "Crescendarr Disposable Read-Only Observer";
    public override string Owner => "Crescendarr project";
    public override string GithubUrl => string.Empty;
}
