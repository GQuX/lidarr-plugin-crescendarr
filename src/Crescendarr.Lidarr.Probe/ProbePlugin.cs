namespace NzbDrone.Core.Plugins;

/// <summary>Not a production plugin. No GitHub install URL is published.</summary>
public sealed class CrescendarrProbePlugin : Plugin
{
    public override string Name => "Crescendarr Disposable Host Probe";
    public override string Owner => "Crescendarr project";
    public override string GithubUrl => string.Empty;
}
