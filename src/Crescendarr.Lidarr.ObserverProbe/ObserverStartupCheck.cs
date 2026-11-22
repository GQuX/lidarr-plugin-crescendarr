using System;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Blackhole;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// A single read-only call to Lidarr's configured download-client factory.
/// Crucially: DOES NOT register/override an importer and never calls GetItems,
/// Import, RemoveItem, SetMonitored, or any filesystem/DB mutation methods.
/// </summary>
public sealed class CrescendarrObserverStartupCheck : IHandle<ApplicationStartedEvent>,
    IHandle<ProviderAddedEvent<IDownloadClient>>,
    IHandle<ProviderUpdatedEvent<IDownloadClient>>
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

    // Provider registration via Lidarr's ordinary configuration API raises this
    // event. We ONLY inspect the host's own configured, synthetic blackhole
    // client, and ONLY when the disposable host's native CDH has been disabled.
    // No downloader removals, import methods, database edits or media moves.
    public void Handle(ProviderAddedEvent<IDownloadClient> message) => InspectSyntheticClient("added");

    public void Handle(ProviderUpdatedEvent<IDownloadClient> message) => InspectSyntheticClient("updated");

    private void InspectSyntheticClient(string eventName)
    {
        var root = RequireEphemeralCiHost();
        if (_config.EnableCompletedDownloadHandling)
        {
            Console.WriteLine("CRESCENDARR_SYNTHETIC_CLIENT_SKIPPED CDH_ENABLED=True");
            return;
        }

        foreach (var client in _clients.DownloadHandlingEnabled())
        {
            if (client is not UsenetBlackhole ||
                client.Definition.Name != "Crescendarr Synthetic Blackhole" ||
                client.Definition.Settings is not UsenetBlackholeSettings settings)
                continue;

            var watchRoot = Path.GetFullPath(settings.WatchFolder);
            if (!IsWithin(root, watchRoot))
                throw new InvalidOperationException("Synthetic client watch folder is not under disposable root.");

            foreach (var item in client.GetItems())
            {
                var outputPath = Path.GetFullPath(item.OutputPath.FullPath);
                if (!IsWithin(watchRoot, outputPath))
                    throw new InvalidOperationException("Synthetic completed job escapes the configured watch folder.");

                var result = "CRESCENDARR_SYNTHETIC_CLIENT_ITEM phase=" + eventName +
                    " CDH_ENABLED=False STATUS=" + item.Status +
                    " ID_MATCH=" + item.DownloadId.StartsWith("Crescendarr Synthetic Blackhole_", StringComparison.Ordinal) +
                    " CAN_REMOVE=" + item.CanBeRemoved;
                _logger.Warn(result);
                Console.WriteLine(result);
            }
        }
    }

    private static bool IsWithin(string root, string target)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return target.Equals(root, comparison) || target.StartsWith(prefix, comparison);
    }

    private static string RequireEphemeralCiHost()
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

        return canonical;
    }
}
