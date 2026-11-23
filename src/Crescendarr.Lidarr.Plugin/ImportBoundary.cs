// M18 (issue #48): an additive observation point INSIDE the stock native import, between the file move and the
// TrackFile row insert. No stock source is changed. Stock v3.1.6.5079 ImportApprovedTracks.Import does, per track:
//   UpgradeTrackFile (recycle old file, delete old row, MOVE the new file, write tags)  [ImportApprovedTracks.cs:240]
//   IExtraService.ImportTrack -> every IManageExtraFiles.CreateAfterTrackImport          [ImportApprovedTracks.cs:272, ExtraService.cs:57,123]
//   ... loop ends ...
//   IMediaFileService.AddMany (row insert) + ITrackService.SetFileIds (link)            [ImportApprovedTracks.cs:317-320]
// Stock DI resolves IEnumerable<IManageExtraFiles> including this plugin type, so CreateAfterTrackImport runs on the
// importing thread after the move and before the row exists (trackFile.Id == 0). It is NOT a hook inside the byte
// transfer itself (a partial copy cannot be paused here), and it never runs for imports Crescendarr did not start.
using System.Text.Json;
using NLog;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    /// <summary>The plugin-owned native call currently running on this thread. Set only around IImportApprovedTracks.Import.</summary>
    public sealed class ImportScope : IDisposable
    {
        private static readonly AsyncLocal<ImportScope> CurrentScope = new AsyncLocal<ImportScope>();

        private ImportScope(string root, string operationRoot, int trackId, string kind)
        {
            Root = root;
            OperationRoot = operationRoot;
            TrackId = trackId;
            Kind = kind;
        }

        public static ImportScope Current => CurrentScope.Value;

        public string Root { get; }

        public string OperationRoot { get; }

        public int TrackId { get; }

        public string Kind { get; }

        public static ImportScope Enter(string root, string operationRoot, int trackId, string kind)
        {
            var scope = new ImportScope(root, operationRoot, trackId, kind);
            CurrentScope.Value = scope;
            return scope;
        }

        public void Dispose() => CurrentScope.Value = null;
    }

    /// <summary>Observe-only extra-file manager. It creates no extra files; it records the post-move/pre-row boundary.</summary>
    public sealed class CrescendarrImportBoundary : IManageExtraFiles
    {
        private static readonly ExtraFile[] None = Array.Empty<ExtraFile>();
        private readonly Logger _logger;

        public CrescendarrImportBoundary(Logger logger)
        {
            _logger = logger;
        }

        public int Order => int.MaxValue;

        public IEnumerable<ExtraFile> CreateAfterMediaCoverUpdate(Artist artist) => None;

        public IEnumerable<ExtraFile> CreateAfterArtistScan(Artist artist, List<TrackFile> trackFiles) => None;

        public IEnumerable<ExtraFile> CreateAfterTrackFolder(Artist artist, Album album, string artistFolder, string albumFolder) => None;

        public IEnumerable<ExtraFile> MoveFilesAfterRename(Artist artist, List<TrackFile> trackFiles) => None;

        public ExtraFile Import(Artist artist, TrackFile trackFile, string path, string extension, bool readOnly) => null;

        public IEnumerable<ExtraFile> CreateAfterTrackImport(Artist artist, TrackFile trackFile)
        {
            var scope = ImportScope.Current;
            if (scope == null) return None;
            try
            {
                var placed = File.Exists(trackFile.Path);
                CrescendarrGuard.Emit(_logger, $"NATIVE_BOUNDARY post-move pre-row kind={scope.Kind} track={scope.TrackId} rowId={trackFile.Id} placed={placed} path=\"{trackFile.Path}\"");
                Disk.WriteDurable(Path.Combine(scope.OperationRoot, $"boundary-{scope.TrackId}.json"), JsonSerializer.Serialize(new
                {
                    boundary = "post-move pre-row", kind = scope.Kind, trackId = scope.TrackId, rowId = trackFile.Id, path = trackFile.Path, placed,
                    placedSha = placed ? Disk.Sha(trackFile.Path) : null
                }), replace: true);
                Faults.Point(_logger, scope.Root, "post-move-" + scope.TrackId);
            }
            catch (Exception error)
            {
                // Never let an observation fault change the stock import outcome.
                CrescendarrGuard.Emit(_logger, "NATIVE_BOUNDARY_OBSERVATION_FAILED " + error.GetType().Name);
            }

            return None;
        }
    }

    /// <summary>
    /// M19: the stock row-insert / track-link boundary. MediaFileService.AddMany inserts the rows and then publishes
    /// TrackFileAddedEvent synchronously for each (MediaFileService.cs:59-65); only afterwards does ImportApprovedTracks
    /// call SetFileIds (ImportApprovedTracks.cs:319-320). Observe-only, and only for imports Crescendarr started.
    /// </summary>
    public sealed class CrescendarrRowBoundary : NzbDrone.Core.Messaging.Events.IHandle<NzbDrone.Core.MediaFiles.Events.TrackFileAddedEvent>
    {
        private readonly Logger _logger;

        public CrescendarrRowBoundary(Logger logger)
        {
            _logger = logger;
        }

        public void Handle(NzbDrone.Core.MediaFiles.Events.TrackFileAddedEvent message)
        {
            var scope = ImportScope.Current;
            if (scope == null) return;
            try
            {
                CrescendarrGuard.Emit(_logger, $"NATIVE_BOUNDARY post-row pre-link kind={scope.Kind} track={scope.TrackId} rowId={message.TrackFile.Id} path=\"{message.TrackFile.Path}\"");
                Faults.Point(_logger, scope.Root, "post-row-" + scope.TrackId);
            }
            catch (Exception error)
            {
                CrescendarrGuard.Emit(_logger, "NATIVE_BOUNDARY_OBSERVATION_FAILED " + error.GetType().Name);
            }
        }
    }

    /// <summary>Deterministic, test-owned fault points. Each pauses (so the harness can SIGKILL the daemon) only when armed.</summary>
    public static class Faults
    {
        public static void Point(Logger logger, string root, string name)
        {
            var armed = Path.Combine(root, "faults", "pause-" + name);
            if (!File.Exists(armed)) return;

            // One-shot: consume the arm so a restarted process (whose native-call counter restarts) never pauses again.
            File.Delete(armed);
            Disk.WriteDurable(Path.Combine(root, "faults", "paused-" + name), "paused\n", replace: true);
            CrescendarrGuard.Emit(logger, "FAULT_PAUSED " + name);
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(Path.Combine(root, "faults", "release-" + name)) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            CrescendarrGuard.Emit(logger, "FAULT_RELEASED " + name);
        }
    }
}
