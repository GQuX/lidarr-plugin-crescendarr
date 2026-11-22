using System;
using System.IO;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Runs only in the CI's disposable host. Inspects the resolved import service after
/// the host starts; never invokes Lidarr's stock importer or touches real media.
/// </summary>
public sealed class CrescendarrDisposableStartupCheck : IHandle<ApplicationStartedEvent>
{
    private readonly IDownloadedTracksImportService _selected;
    private readonly Logger _logger;

    public CrescendarrDisposableStartupCheck(IDownloadedTracksImportService selected, Logger logger)
    {
        _selected = selected;
        _logger = logger;
    }

    public void Handle(ApplicationStartedEvent message)
    {
        // Require exact opt-in even if DI selected the stock implementation.
        var root = ProbeSafetyGuard.RequireDisposableEnvironment();
        var selectedName = _selected.GetType().FullName ?? "(unknown)";
        _logger.Warn("CRESCENDARR_RUNTIME SERVICE_SELECTED={0}", selectedName);
        Console.WriteLine("CRESCENDARR_RUNTIME SERVICE_SELECTED=" + selectedName);

        // Crucial: DO NOT call ProcessPath if the stock importer won DI selection.
        if (_selected is not CrescendarrProbeImportService probe)
        {
            Console.WriteLine("CRESCENDARR_RUNTIME STOCK_SERVICE_SELECTED_NO_CALL");
            return;
        }

        var fixture = Path.Combine(root, "synthetic-nonexistent-audio-folder");
        var outcome = probe.ProcessPath(fixture, ImportMode.Auto);
        if (outcome.Count != 0)
            throw new InvalidOperationException("Disposable probe unexpectedly returned import results.");

        _logger.Warn("CRESCENDARR_RUNTIME INTERCEPTION_OK; imported_files=0; no delegation");
        Console.WriteLine("CRESCENDARR_RUNTIME INTERCEPTION_OK");
    }
}
