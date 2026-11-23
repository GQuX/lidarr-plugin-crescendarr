// Plan (read-only), Import (the only writer) and Recover. Each runs inside ONE stock RequiresDiskAccess command.
// The M13 Reconciler decides every journaled step, both on the first run and after a restart.
using System.Collections.Immutable;
using System.Text.Json;
using Crescendarr.Core.Domain;
using Crescendarr.Core.Inventory;
using Crescendarr.Core.Journal;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public sealed class CrescendarrPlanExecutor : IExecute<CrescendarrPlanCommand>
    {
        private readonly HostAdapter _adapter;
        private readonly IRootFolderService _rootFolders;
        private readonly IConfigService _config;
        private readonly Logger _logger;

        public CrescendarrPlanExecutor(HostAdapter adapter, IRootFolderService rootFolders, IConfigService config, Logger logger)
        {
            _adapter = adapter;
            _rootFolders = rootFolders;
            _config = config;
            _logger = logger;
        }

        public void Execute(CrescendarrPlanCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var items = _adapter.CompletedItems(root, out var notes);
            CrescendarrGuard.Emit(_logger, $"OBSERVED clientItems=[{string.Join(",", notes)}] completed={items.Count}");
            foreach (var (item, clientId, watch) in items)
            {
                var observation = _adapter.Observe(item, clientId, watch);
                var line = _adapter.Describe(observation);
                Disk.WriteDurable(Path.Combine(root, "crescendarr", "plans", Disk.Sha(System.Text.Encoding.UTF8.GetBytes(item.DownloadId))[..16] + ".txt"), line + "\n", replace: true);
                CrescendarrGuard.Emit(_logger, "PLAN " + line);
            }
        }
    }

    /// <summary>State shared by the journal-driven loop for one operation.</summary>
    internal sealed class OperationContext
    {
        public string Root;
        public string OperationRoot;
        public Artist Artist;
        public Album Album;
        public string ArtistMbid;
        public Dictionary<int, ImportDecision<LocalTrack>> StagedDecisionByTrack = new Dictionary<int, ImportDecision<LocalTrack>>();
    }

    public sealed class CrescendarrImportExecutor : IExecute<CrescendarrImportCommand>, IExecute<CrescendarrRecoverCommand>
    {
        private readonly HostAdapter _adapter;
        private readonly IRootFolderService _rootFolders;
        private readonly IConfigService _config;
        private readonly IImportApprovedTracks _importer;
        private readonly IBuildFileNames _fileNames;
        private readonly IArtistService _artists;
        private readonly IAlbumService _albums;
        private readonly IReleaseService _releases;
        private readonly ITrackService _tracks;
        private readonly ITrackRepository _trackRepository;
        private readonly IAlbumRepository _albumRepository;
        private readonly IMediaFileService _mediaFiles;
        private readonly IExtraFileService<LyricFile> _lyrics;
        private readonly Logger _logger;

        public CrescendarrImportExecutor(HostAdapter adapter, IRootFolderService rootFolders, IConfigService config, IImportApprovedTracks importer,
            IBuildFileNames fileNames, IArtistService artists, IAlbumService albums, IReleaseService releases, ITrackService tracks,
            ITrackRepository trackRepository, IAlbumRepository albumRepository, IMediaFileService mediaFiles, IExtraFileService<LyricFile> lyrics, Logger logger)
        {
            _adapter = adapter;
            _rootFolders = rootFolders;
            _config = config;
            _importer = importer;
            _fileNames = fileNames;
            _artists = artists;
            _albums = albums;
            _releases = releases;
            _tracks = tracks;
            _trackRepository = trackRepository;
            _albumRepository = albumRepository;
            _mediaFiles = mediaFiles;
            _lyrics = lyrics;
            _logger = logger;
        }

        // ------------------------------------------------------------------ import

        public void Execute(CrescendarrImportCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var match = _adapter.CompletedItems(root, out _).Where(i => i.Item.DownloadId == message.DownloadId).ToList();
            if (match.Count != 1) throw new CommandFailedException("Approved download is not exactly one completed client item.");
            var (item, clientId, watch) = match[0];

            var observed = _adapter.Observe(item, clientId, watch);
            if (observed.Blocked != null || observed.Plan == null)
            {
                Refuse(message, "blocked: " + observed.Blocked, observed);
                return;
            }

            var plan = observed.Plan;
            if (plan.PlanId != message.PlanId)
            {
                Refuse(message, $"plan no longer current (fresh plan {plan.PlanId} disposition={plan.Disposition})", observed);
                return;
            }

            if (plan.Disposition != PlanDisposition.Ready)
            {
                Refuse(message, "plan disposition " + plan.Disposition, observed);
                return;
            }

            var operationId = OperationId(plan.PlanId);
            var opRoot = Path.Combine(root, "crescendarr", "ops", operationId);
            if (Directory.Exists(opRoot))
            {
                Refuse(message, "operation already exists; only the recovery command may continue it", observed);
                return;
            }

            // 1. Operation-owned staged copies of every candidate (the completed download itself is never touched).
            var staging = Path.Combine(root, "crescendarr", "staging", operationId);
            foreach (var (key, sha) in observed.IncomingSha)
            {
                var source = observed.ByCandidate[key].Item.Path;
                if (Disk.HasLinkInChain(source, root)) throw new CommandFailedException("Candidate traverses a link.");
                Disk.CopyVerified(source, Path.Combine(staging, key), sha);
            }

            // 2. Identical stock decisions on identical bytes, now pointing at the staged copies.
            var staged = _adapter.Observe(item, clientId, watch, staging);
            if (staged.Blocked != null || staged.Plan?.PlanId != plan.PlanId)
                throw new CommandFailedException($"Staged copies did not reproduce the approved plan ({staged.Blocked}).");

            var selected = staged.Plan.Decisions.Where(d => d.Action is MergeAction.Upgrade or MergeAction.AddMissing).ToList();
            var selectedStock = selected.Select(d => staged.ByCandidate[d.CandidateKey]).ToList();

            // 3. Read-only stock edition gate (M14 preflight) over the exact decisions to be imported.
            var preflight = new NzbDrone.Core.MediaFiles.TrackImport.CrescendarrEditionPreflight(_albumRepository, _releases, _trackRepository, _config)
                .Check(staged.Album.Id, staged.Album.ForeignAlbumId, staged.MonitoredRelease.ForeignReleaseId, selectedStock);
            if (!preflight.EditionMatches) throw new CommandFailedException("Edition preflight refused: " + preflight.Reason);

            var context = new OperationContext { Root = root, OperationRoot = opRoot, Artist = staged.Artist, Album = staged.Album,
                ArtistMbid = staged.Artist.Metadata.Value.ForeignArtistId };
            var upgrades = new List<TrackBinding>();
            var additions = new List<(global::Crescendarr.Core.Domain.TrackDecision Plan, ImportDecision<LocalTrack> Stock, string Destination)>();
            var artistRoot = staged.Artist.Path.TrimEnd('/');
            foreach (var decision in selected)
            {
                var stock = staged.ByCandidate[decision.CandidateKey];
                var track = stock.Item.Tracks.Single();
                var destination = _fileNames.BuildTrackFilePath(stock.Item.Tracks, staged.Artist, staged.Album,
                    new TrackFile { Path = stock.Item.Path, SceneName = stock.Item.SceneName }, Path.GetExtension(stock.Item.Path));
                if (!Disk.IsUnder(artistRoot, destination) || Disk.HasLinkInChain(Path.GetDirectoryName(destination), root))
                    throw new CommandFailedException("Destination escapes the artist root or traverses a link.");
                context.StagedDecisionByTrack[track.Id] = stock;
                if (decision.Action == MergeAction.AddMissing)
                {
                    if (File.Exists(destination)) throw new CommandFailedException("Add-missing destination already exists: refusing to overwrite.");
                    additions.Add((decision, stock, destination));
                    continue;
                }

                var original = staged.TrackFiles[track.TrackFileId];
                if (Disk.HasLinkInChain(original.Path, root)) throw new CommandFailedException("Original traverses a link.");
                var lyricRows = staged.AlbumLyrics.Where(l => l.TrackFileId == original.Id).ToList();
                if (lyricRows.Count > 1) throw new CommandFailedException("More than one lyric row is linked to an upgraded track file.");
                LyricBinding lyric = null;
                if (lyricRows.Count == 1)
                {
                    var lyricPath = Path.Combine(artistRoot, lyricRows[0].RelativePath);
                    if (!File.Exists(lyricPath) || Disk.HasLinkInChain(lyricPath, root)) throw new CommandFailedException("Registered lyric is missing or traverses a link.");
                    lyric = new LyricBinding(lyricRows[0].Id, lyricRows[0].RelativePath, lyricPath, Content(lyricPath),
                        Path.Combine(opRoot, "track-lyrics", track.Id + ".lrc"));
                }

                upgrades.Add(new TrackBinding(track.Id, track.ForeignRecordingId, original.Id, original.Path, destination, stock.Item.Path,
                    Content(original.Path), Content(stock.Item.Path), Path.Combine(opRoot, "audio", track.Id + Path.GetExtension(original.Path)), lyric));
            }

            // 4. Album-wide lyric backups BEFORE any destructive step, including unselected (superior) tracks.
            var albumLyrics = new List<object>();
            foreach (var lyric in staged.AlbumLyrics.OrderBy(l => l.Id))
            {
                var path = Path.Combine(artistRoot, lyric.RelativePath);
                if (!File.Exists(path) || Disk.HasLinkInChain(path, root)) throw new CommandFailedException("An album lyric is missing or traverses a link.");
                var sha = Disk.Sha(path);
                var backup = Path.Combine(opRoot, "album-lyrics", lyric.Id + ".lrc");
                Disk.CopyVerified(path, backup, sha);
                var trackId = staged.AlbumTracks.FirstOrDefault(t => t.TrackFileId == lyric.TrackFileId)?.Id ?? 0;
                albumLyrics.Add(new { lyric.Id, lyric.TrackFileId, trackId, lyric.RelativePath, sha, backup });
            }

            Disk.WriteDurable(Path.Combine(opRoot, "album-lyrics.json"), JsonSerializer.Serialize(albumLyrics), replace: false);
            Disk.WriteDurable(Path.Combine(opRoot, "operation.json"), JsonSerializer.Serialize(new
            {
                operationId, planId = plan.PlanId, downloadId = item.DownloadId, clientId, staging,
                artistId = staged.Artist.Id, albumId = staged.Album.Id, artistMbid = staged.Artist.Metadata.Value.ForeignArtistId,
                additions = additions.Select(a => new { a.Plan.CandidateKey, trackId = a.Stock.Item.Tracks.Single().Id, a.Destination }).ToList(),
                kept = plan.Decisions.Where(d => d.Action == MergeAction.KeepExisting).Select(d => d.CandidateKey).ToList()
            }), replace: false);
            CrescendarrGuard.Emit(_logger, $"OPERATION_PUBLISHED op={operationId} upgrades={upgrades.Count} additions={additions.Count} albumLyricBackups={albumLyrics.Count}");

            // 5. M13 write-ahead journal for every destructive replacement.
            var outcome = "Completed";
            if (upgrades.Count > 0)
            {
                var binding = new OperationBinding(operationId,
                    InventoryText.IsValidDownloadId(item.DownloadId) ? new DownloadBinding(clientId, item.DownloadId, null) : null,
                    staged.Album.Id, staged.Album.ForeignAlbumId, staged.MonitoredRelease.Id, staged.MonitoredRelease.ForeignReleaseId,
                    PathStyle.Posix, Path.GetFullPath(staged.Artist.RootFolderPath).TrimEnd('/'), staging, opRoot,
                    upgrades.ToImmutableArray(), artistRoot);
                var manifest = JournalEngine.Begin(binding);
                Save(opRoot, manifest);
                outcome = RunJournal(context, manifest);
            }

            if (outcome == "Completed") outcome = AddMissing(context, additions.Select(a => (a.Stock, a.Destination)).ToList());
            Finish(context, outcome);
        }

        // ------------------------------------------------------------------ recovery after restart

        public void Execute(CrescendarrRecoverCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var ops = Path.Combine(root, "crescendarr", "ops");
            if (!Directory.Exists(ops))
            {
                CrescendarrGuard.Emit(_logger, "RECOVERY_NONE no operations");
                return;
            }

            foreach (var opRoot in Directory.GetDirectories(ops).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(opRoot, "outcome.json"))) continue;
                var operation = JsonDocument.Parse(File.ReadAllText(Path.Combine(opRoot, "operation.json"))).RootElement;
                var staging = operation.GetProperty("staging").GetString();
                var journalPath = Path.Combine(opRoot, "journal.json");
                var context = new OperationContext
                {
                    Root = root, OperationRoot = opRoot,
                    Album = _albums.GetAlbum(operation.GetProperty("albumId").GetInt32()),
                    Artist = _artists.GetArtist(operation.GetProperty("artistId").GetInt32()),
                    ArtistMbid = operation.GetProperty("artistMbid").GetString()
                };
                StagedDecisions(context, staging);
                var outcome = "Completed";
                if (File.Exists(journalPath))
                {
                    var load = JournalCodec.Decode(File.ReadAllText(journalPath), Path.GetFileName(opRoot));
                    if (!load.Succeeded)
                    {
                        Finish(context, "RequiresReview", "journal unreadable: " + load.Error);
                        continue;
                    }

                    var manifest = load.Manifest;
                    CrescendarrGuard.Emit(_logger, $"RECOVERY_START op={Path.GetFileName(opRoot)} status={manifest.Status} generation={manifest.Generation}");
                    outcome = RunJournal(context, manifest);
                }

                if (outcome == "Completed")
                {
                    var additions = new List<(ImportDecision<LocalTrack>, string)>();
                    foreach (var add in operation.GetProperty("additions").EnumerateArray())
                    {
                        var trackId = add.GetProperty("trackId").GetInt32();
                        var destination = add.GetProperty("Destination").GetString();
                        var current = _tracks.GetTrack(trackId);
                        if (current.TrackFileId > 0 && _mediaFiles.Get(current.TrackFileId)?.Path == destination) continue;
                        if (File.Exists(destination))
                        {
                            outcome = "RequiresReview";
                            CrescendarrGuard.Emit(_logger, $"RECOVERY_REVIEW add-missing destination exists without a linked row: {destination}");
                            break;
                        }

                        if (!context.StagedDecisionByTrack.TryGetValue(trackId, out var decision))
                        {
                            outcome = "RequiresReview";
                            break;
                        }

                        additions.Add((decision, destination));
                    }

                    if (outcome == "Completed") outcome = AddMissing(context, additions);
                }

                Finish(context, outcome);
            }
        }

        /// <summary>Recompute stock decisions from the operation-owned staged copies (never from stored command bodies).</summary>
        private void StagedDecisions(OperationContext context, string staging)
        {
            if (!Directory.Exists(staging)) return;
            // Recovery: some staged copies were already consumed, so stock's new-download "missing tracks"
            // rule would reject the rest. Every other stock specification (upgrade, edition, match) still applies.
            foreach (var decision in _adapter.Decide(staging, null, newDownload: false))
            {
                if (decision.Item.Tracks?.Count == 1) context.StagedDecisionByTrack[decision.Item.Tracks[0].Id] = decision;
            }
        }

        // ------------------------------------------------------------------ M13 journal loop

        private string RunJournal(OperationContext context, OperationManifest manifest)
        {
            var nativeCount = 0;
            for (var guard = 0; guard < 500; guard++)
            {
                // PR #42 review: revalidate the FRESH artist MusicBrainz identity before every step.
                var freshArtist = _artists.GetArtist(context.Artist.Id);
                if (!string.Equals(freshArtist?.Metadata?.Value?.ForeignArtistId, context.ArtistMbid, StringComparison.OrdinalIgnoreCase) && manifest.Status == OperationStatus.Active)
                {
                    manifest = JournalEngine.MarkReview(manifest, "artist MusicBrainz identity changed");
                    Save(context.OperationRoot, manifest);
                    return "RequiresReview";
                }

                var host = HostSnapshot(manifest, context.Artist.Id);
                var fs = Observe(context.Root, manifest);
                var verdict = Reconciler.Reconcile(manifest, host, fs);
                var next = verdict.Next;
                CrescendarrGuard.Emit(_logger, $"JOURNAL gen={manifest.Generation} verdict={verdict.Verdict} next={next?.Action}:{next?.TrackId}:{next?.Mode} \"{next?.Reason}\"" +
                    (verdict.Blockers.IsEmpty ? string.Empty : " blockers=[" + string.Join("; ", verdict.Blockers) + "]"));
                switch (verdict.Verdict)
                {
                    case Verdict.Completed:
                        return "Completed";
                    case Verdict.OperatorApprovalRequired:
                        return "OperatorApprovalRequired";
                    case Verdict.RequiresReview:
                        if (next?.Action == RecoveryAction.MarkReview && manifest.Status == OperationStatus.Active)
                        {
                            manifest = JournalEngine.MarkReview(manifest, next.Reason);
                            Save(context.OperationRoot, manifest);
                        }

                        return "RequiresReview";
                }

                if (next == null) return "RequiresReview";
                var trackId = next.TrackId ?? 0;
                var binding = trackId > 0 ? manifest.BindingFor(trackId) : null;
                var progress = trackId > 0 ? manifest.ProgressFor(trackId) : null;
                JournalStep? Declared(JournalStep step)
                {
                    if (progress.InFlight == null)
                    {
                        manifest = JournalEngine.MarkInFlight(manifest, trackId, step);
                        Save(context.OperationRoot, manifest);
                    }

                    return step;
                }

                switch (next.Action)
                {
                    case RecoveryAction.CopyBackup:
                        Declared(JournalStep.CopyBackup);
                        CopyIfAbsent(binding.OriginalPath, binding.BackupAudioPath, binding.Original.Sha256);
                        if (binding.Lyric != null) CopyIfAbsent(binding.Lyric.Path, binding.Lyric.BackupPath, binding.Lyric.Content.Sha256);
                        break;
                    case RecoveryAction.DetachLyric:
                        Declared(JournalStep.DetachLyric);
                        _lyrics.Delete(binding.Lyric.LyricFileId);
                        break;
                    case RecoveryAction.InvokeNativeUpgrade:
                        if (!context.StagedDecisionByTrack.TryGetValue(trackId, out var decision)) return "RequiresReview";
                        Declared(JournalStep.NativeUpgrade);
                        var results = _importer.Import(new List<ImportDecision<LocalTrack>> { decision }, false, null, ImportMode.Move);
                        nativeCount++;
                        CrescendarrGuard.Emit(_logger, $"NATIVE_IMPORT track={trackId} results=[{string.Join(",", results.Select(r => r.Result + ":" + string.Join("|", r.Errors)))}]");
                        FaultPoint(context.Root, "after-native-" + nativeCount);
                        break;
                    case RecoveryAction.RelinkLyric:
                        Declared(JournalStep.RelinkLyric);
                        _lyrics.Upsert(new LyricFile
                        {
                            ArtistId = context.Artist.Id, AlbumId = manifest.Binding.AlbumId, TrackFileId = progress.NewTrackFileId.Value,
                            RelativePath = binding.Lyric.RelativePath, Extension = Path.GetExtension(binding.Lyric.RelativePath),
                            Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                        });
                        break;
                    case RecoveryAction.RecordStepDone:
                        manifest = JournalEngine.MarkDone(manifest, trackId, progress.InFlight.Value, next.NewTrackFileId, next.NewLyricFileId);
                        Save(context.OperationRoot, manifest);
                        break;
                    case RecoveryAction.AbandonStep:
                        manifest = JournalEngine.AbandonInFlight(manifest, trackId, progress.InFlight.Value);
                        Save(context.OperationRoot, manifest);
                        break;
                    case RecoveryAction.RestoreOriginalBytes:
                        Disk.CopyVerified(binding.BackupAudioPath, binding.OriginalPath, binding.Original.Sha256);
                        break;
                    case RecoveryAction.CompleteOperation:
                        manifest = JournalEngine.Complete(manifest);
                        Save(context.OperationRoot, manifest);
                        break;
                    case RecoveryAction.MarkReview:
                        manifest = JournalEngine.MarkReview(manifest, next.Reason);
                        Save(context.OperationRoot, manifest);
                        return "RequiresReview";
                    default:
                        return "RequiresReview";
                }
            }

            return "RequiresReview";
        }

        private string AddMissing(OperationContext context, List<(ImportDecision<LocalTrack> Stock, string Destination)> additions)
        {
            foreach (var (stock, destination) in additions)
            {
                var track = stock.Item.Tracks.Single();
                var fresh = _tracks.GetTrack(track.Id);
                if (fresh.TrackFileId != 0 || File.Exists(destination))
                {
                    CrescendarrGuard.Emit(_logger, $"ADD_MISSING_REVIEW track={track.Id} host changed before add");
                    return "RequiresReview";
                }

                var sha = Disk.Sha(stock.Item.Path);
                var results = _importer.Import(new List<ImportDecision<LocalTrack>> { stock }, false, null, ImportMode.Move);
                var linked = _tracks.GetTrack(track.Id);
                var row = linked.TrackFileId > 0 ? _mediaFiles.Get(linked.TrackFileId) : null;
                var ok = results.Count == 1 && results[0].Result == ImportResultType.Imported && row?.Path == destination && Disk.Sha(destination) == sha;
                CrescendarrGuard.Emit(_logger, $"ADD_MISSING track={track.Id} ok={ok} trackFile={linked.TrackFileId} results=[{string.Join(",", results.Select(r => r.Result.ToString()))}]");
                if (!ok) return "RequiresReview";
            }

            return "Completed";
        }

        private void Finish(OperationContext context, string outcome, string detail = null)
        {
            var verified = outcome == "Completed" ? VerifyAlbumLyrics(context) : null;
            if (verified != null)
            {
                outcome = "RequiresReview";
                detail = verified;
            }

            Disk.WriteDurable(Path.Combine(context.OperationRoot, "outcome.json"),
                JsonSerializer.Serialize(new { outcome, detail, utc = DateTime.UtcNow }), replace: false);
            CrescendarrGuard.Emit(_logger, $"IMPORT_OUTCOME op={Path.GetFileName(context.OperationRoot)} outcome={outcome} detail=\"{detail}\"");
            if (outcome != "Completed") throw new CommandFailedException($"Crescendarr operation ended {outcome}: {detail}");
        }

        /// <summary>Every album lyric backed up before the operation must still exist with identical bytes and be registered.</summary>
        private string VerifyAlbumLyrics(OperationContext context)
        {
            var file = Path.Combine(context.OperationRoot, "album-lyrics.json");
            if (!File.Exists(file) || context.Artist == null) return "album lyric evidence missing";
            var rows = _lyrics.GetFilesByArtist(context.Artist.Id);
            foreach (var lyric in JsonDocument.Parse(File.ReadAllText(file)).RootElement.EnumerateArray())
            {
                var relative = lyric.GetProperty("RelativePath").GetString();
                var path = Path.Combine(context.Artist.Path, relative);
                if (!File.Exists(path) || Disk.Sha(path) != lyric.GetProperty("sha").GetString()) return "album lyric bytes changed: " + relative;
                var trackId = lyric.GetProperty("trackId").GetInt32();
                var current = _tracks.GetTrack(trackId);
                if (rows.Count(r => r.RelativePath == relative && r.TrackFileId == current.TrackFileId) != 1) return "album lyric not registered to its track's current file: " + relative;
            }

            return null;
        }

        private void Refuse(CrescendarrImportCommand message, string why, Observation observed)
        {
            CrescendarrGuard.Emit(_logger, $"IMPORT_REFUSED download=\"{message.DownloadId}\" reason=\"{why}\" mutations=0 {_adapter.Describe(observed)}");
        }

        // ------------------------------------------------------------------ observation for the reconciler

        private HostSnapshot HostSnapshot(OperationManifest manifest, int artistId)
        {
            try
            {
                var b = manifest.Binding;
                var album = _albums.GetAlbum(b.AlbumId);
                var artist = _artists.GetArtist(artistId);
                if (album.ArtistMetadataId != artist.ArtistMetadataId) throw new InvalidOperationException("album/artist mismatch");
                var releases = _releases.GetReleasesByAlbum(b.AlbumId);
                var tracks = _tracks.GetTracksByReleases(releases.Select(r => r.Id).ToList())
                    .Concat(_trackRepository.GetTracksByFileId(b.Tracks.Select(t => t.OriginalTrackFileId)))
                    .GroupBy(t => t.Id).Select(g => g.First()).ToList();
                var files = _mediaFiles.GetFilesByAlbum(b.AlbumId).ToList();
                foreach (var path in b.Tracks.SelectMany(t => new[] { t.OriginalPath, t.DestinationPath }))
                {
                    var atPath = _mediaFiles.GetFileWithPath(path);
                    if (atPath != null) files.Add(atPath);
                }

                foreach (var id in tracks.Select(t => t.TrackFileId).Where(id => id > 0).Except(files.Select(f => f.Id)).ToList())
                {
                    var row = _mediaFiles.Get(id);
                    if (row != null) files.Add(row);
                }

                return new HostSnapshot(true, b.AlbumId,
                    releases.Select(r => new HostRelease(r.Id, r.ForeignReleaseId, r.Monitored)).ToImmutableArray(),
                    tracks.Select(t => new HostTrack(t.Id, t.ForeignRecordingId, t.TrackFileId)).ToImmutableArray(),
                    files.GroupBy(f => f.Id).Select(g => new HostTrackFile(g.Key, g.First().Path)).ToImmutableArray(),
                    _lyrics.GetFilesByArtist(artist.Id).Select(l => new HostLyric(l.Id, l.TrackFileId ?? 0, l.RelativePath)).ToImmutableArray(),
                    artist.Path.TrimEnd('/'));
            }
            catch (Exception error)
            {
                CrescendarrGuard.Emit(_logger, "HOST_SNAPSHOT_FAILED " + error.GetType().Name);
                return new HostSnapshot(false, manifest.Binding.AlbumId, ImmutableArray<HostRelease>.Empty, ImmutableArray<HostTrack>.Empty,
                    ImmutableArray<HostTrackFile>.Empty, ImmutableArray<HostLyric>.Empty, null);
            }
        }

        private static FilesystemSnapshot Observe(string root, OperationManifest manifest)
        {
            var files = ImmutableDictionary.CreateBuilder<string, FileObservation>(StringComparer.Ordinal);
            foreach (var path in manifest.Binding.Tracks.SelectMany(Reconciler.BoundPaths).Distinct(StringComparer.Ordinal))
            {
                var escapes = !Disk.IsUnder(root, path) || Disk.HasLinkInChain(path, root);
                files[path] = File.Exists(path)
                    ? new FileObservation(true, new FileInfo(path).Length, Disk.Sha(path), escapes)
                    : FileObservation.Absent with { Escapes = escapes };
            }

            return new FilesystemSnapshot(Directory.Exists(root), files.ToImmutable());
        }

        // ------------------------------------------------------------------ helpers

        private static void Save(string opRoot, OperationManifest manifest) =>
            Disk.WriteDurable(Path.Combine(opRoot, "journal.json"), JournalCodec.Encode(manifest), replace: true);

        private static void CopyIfAbsent(string source, string destination, string sha)
        {
            if (File.Exists(destination))
            {
                if (Disk.Sha(destination) != sha) throw new CommandFailedException("An existing backup has unexpected bytes.");
                return;
            }

            Disk.CopyVerified(source, destination, sha);
        }

        private static ContentId Content(string path) => new ContentId(Disk.Sha(path), new FileInfo(path).Length);

        private static string OperationId(string planId)
        {
            var bytes = Convert.FromHexString(planId[..32]);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes, true).ToString("D");
        }

        /// <summary>Deterministic, test-owned fault point: pauses (so the harness can SIGKILL the daemon) only when armed.</summary>
        private void FaultPoint(string root, string name)
        {
            var armed = Path.Combine(root, "faults", "pause-" + name);
            if (!File.Exists(armed)) return;
            Disk.WriteDurable(Path.Combine(root, "faults", "paused-" + name), "paused\n", replace: true);
            CrescendarrGuard.Emit(_logger, "FAULT_PAUSED " + name);
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(Path.Combine(root, "faults", "release-" + name)) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        }
    }
}
