using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.MediaFiles.TrackImport;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Development-only service-resolution experiment. Requires opt-in environment marker.
/// NEVER calls original importer, moves files, imports audio, or completes downloads.
/// On a disposable host only, it reveals whether last registration selects this service
/// and whether the concrete original is injectable without recursion.
/// </summary>
public sealed class CrescendarrProbeImportService : IDownloadedTracksImportService
{
    private readonly Logger _logger;
    private readonly string _root;

    // Deliberately request the CONCRETE original to test DryIoc's resolution seam.
    // We do not invoke it, because even test-only delegation could delete files.
    public CrescendarrProbeImportService(DownloadedTracksImportService original, Logger logger)
    {
        _root = ProbeSafetyGuard.RequireDisposableEnvironment();
        _logger = logger;
        _logger.Warn("CRESCENDARR_PROBE constructor selected; concrete_original={0}; assembly={1}",
            original.GetType().FullName, typeof(DownloadedTracksImportService).Assembly.GetName().Version);
    }

    public List<ImportResult> ProcessRootFolder(IDirectoryInfo directoryInfo)
    {
        ProbeSafetyGuard.EnsureWithin(_root, directoryInfo.FullName);
        _logger.Warn("CRESCENDARR_PROBE ProcessRootFolder intercepted; NO DELEGATION");
        return new List<ImportResult>();
    }

    public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto,
        Artist? artist = null, DownloadClientItem? downloadClientItem = null)
    {
        ProbeSafetyGuard.EnsureWithin(_root, path);
        _logger.Warn("CRESCENDARR_PROBE ProcessPath intercepted; mode={0}; NO DELEGATION", importMode);
        return new List<ImportResult>();
    }

    public bool ShouldDeleteFolder(IDirectoryInfo directoryInfo, Artist artist)
    {
        ProbeSafetyGuard.EnsureWithin(_root, directoryInfo.FullName);
        _logger.Warn("CRESCENDARR_PROBE ShouldDeleteFolder intercepted; false (never cleanup)");
        return false;
    }
}

internal static class ProbeSafetyGuard
{
    private const string OptIn = "DISPOSABLE_SYNTHETIC_ONLY";
    private const string Marker = "CRESCENDARR_DISPOSABLE_HOST_V1";

    public static string RequireDisposableEnvironment()
    {
        if (Environment.GetEnvironmentVariable("CRESCENDARR_PROBE_OPT_IN") != OptIn)
            throw new InvalidOperationException("Crescendarr test probe is disabled without explicit disposable-host opt-in.");

        var raw = Environment.GetEnvironmentVariable("CRESCENDARR_PROBE_SYNTHETIC_ROOT");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("CRESCENDARR_PROBE_SYNTHETIC_ROOT must be set.");

        var root = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(root) || root == Path.GetPathRoot(root) || !Directory.Exists(root))
            throw new InvalidOperationException("Crescendarr probe root must be an existing non-root directory.");
        var markerFile = Path.Combine(root, ".crescendarr-disposable-host");
        if (!File.Exists(markerFile) || File.ReadAllText(markerFile).Trim() != Marker)
            throw new InvalidOperationException("Crescendarr disposable-host marker is missing or invalid.");
        return root;
    }

    public static void EnsureWithin(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Blank probe path.");
        var normalized = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalized.Equals(root, comparison) &&
            !normalized.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Crescendarr probe rejected a path outside its disposable fixture root.");
        // Symbolic links and mounted child folders are NOT resolved here. The test host
        // must exclude them; see docs/safety/disposable-host.md.
    }
}
