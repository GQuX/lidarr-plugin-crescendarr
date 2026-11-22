using System;
using System.IO;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// A single read-only call to Lidarr's configured download-client factory.
/// Crucially: DOES NOT register/override an importer and never calls GetItems,
/// Import, RemoveItem, SetMonitored, or any filesystem/DB mutation methods.
/// </summary>
public sealed class CrescendarrObserverStartupCheck : IHandle<ApplicationStartedEvent>
{
    private readonly IDownloadClientFactory _clients;
    private readonly IConfigService _config;
    private readonly Logger _logger;

    public CrescendarrObserverStartupCheck(
        IDownloadClientFactory clients,
        IConfigService config,
        Logger logger)
    {
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    public void Handle(ApplicationStartedEvent message)
    {
        RequireEphemeralCiHost();

        // The host is brand new and has no configured download clients.
        // Fetch the provider list but do not poll any downloader or import any files.
        var enabled = _config.EnableCompletedDownloadHandling;
        var clients = _clients.DownloadHandlingEnabled();

        var output = "CRESCENDARR_OBSERVER_READY CDH_ENABLED=" + enabled +
            " CLIENT_COUNT=" + clients.Count + " IMPORT_OVERRIDE=NONE";
        _logger.Warn(output);
        Console.WriteLine(output);
    }

    private static void RequireEphemeralCiHost()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("CRESCENDARR_OBSERVER_OPT_IN") != "DISPOSABLE_SYNTHETIC_ONLY")
            throw new InvalidOperationException("Observer probe requires an explicitly opted-in ephemeral CI host.");

        var root = Environment.GetEnvironmentVariable("CRESCENDARR_OBSERVER_SYNTHETIC_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Missing synthetic-only observer root.");

        var canonical = Path.GetFullPath(root);
        if (canonical == Path.GetPathRoot(canonical) ||
            !File.Exists(Path.Combine(canonical, ".crescendarr-disposable-host")) ||
            File.ReadAllText(Path.Combine(canonical, ".crescendarr-disposable-host")).Trim() !=
                "CRESCENDARR_DISPOSABLE_HOST_V1")
            throw new InvalidOperationException("Observer synthetic host marker is invalid.");
    }
}
