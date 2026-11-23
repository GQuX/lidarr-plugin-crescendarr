// Plan (read-only), Import (the only writer) and Recover. Each runs inside ONE stock RequiresDiskAccess command.
// M18 (issue #48): ONE durable whole-operation journal (M13 format v3) covers album-wide lyric backups, every
// replacement, every missing-track addition, protected unselected tracks and the final album-wide lyric state.
// The M13 Reconciler decides every step, on the first run and after a restart; nothing is inferred from absence.
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
            var plans = Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "plans"), "plan folder");
            var items = _adapter.CompletedItems(root, out var notes);
            CrescendarrGuard.Emit(_logger, $"OBSERVED clientItems=[{string.Join(",", notes)}] completed={items.Count}");
            foreach (var (item, clientId, watch) in items)
            {
                var observation = _adapter.Observe(item, clientId, watch);
                var line = _adapter.Describe(observation);
                Disk.WriteDurable(Path.Combine(plans, Disk.Sha(System.Text.Encoding.UTF8.GetBytes(item.DownloadId))[..16] + ".txt"), line + "\n", replace: true);
                CrescendarrGuard.Emit(_logger, "PLAN " + line);
            }
        }
    }

    /// <summary>State shared by the journal-driven loop for one operation.</summary>
    internal sealed class OperationContext
    {
        public string Root;
        public string OperationId;
        public string OperationRoot;
        public Artist Artist;
        public Album Album;
        public string ArtistMbid;
        public string Staging;
        public OperationManifest Manifest;
        public string LibraryIdentity;
        public string StorageProblem;
        public List<string> Salvaged = new List<string>();
        public List<string> SalvageSkipped = new List<string>();
        public NextAction Proposal;
        public Func<bool> LoadStagedDecisions;
        public Dictionary<int, ImportDecision<LocalTrack>> StagedDecisionByTrack = new Dictionary<int, ImportDecision<LocalTrack>>();
    }

    public sealed partial class CrescendarrImportExecutor : IExecute<CrescendarrImportCommand>, IExecute<CrescendarrRecoverCommand>,
        IExecute<CrescendarrOperatorStatusCommand>, IExecute<CrescendarrProposeCommand>, IExecute<CrescendarrApproveCommand>
    {
        private const string Completed = "Completed";
        private const string RequiresReview = "RequiresReview";
        private const string OperatorApprovalRequired = "OperatorApprovalRequired";
        private const string Abandoned = "Abandoned";
        private const string Refused = "Refused";

        /// <summary>M23: transient, NOT terminal: the library storage was unreachable, unmounted or changed. Nothing was concluded.</summary>
        private const string StorageUnavailable = "StorageUnavailable";

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
        private readonly NzbDrone.Core.Notifications.INotificationFactory _notifications;
        private readonly Logger _logger;

        public CrescendarrImportExecutor(HostAdapter adapter, IRootFolderService rootFolders, IConfigService config, IImportApprovedTracks importer,
            IBuildFileNames fileNames, IArtistService artists, IAlbumService albums, IReleaseService releases, ITrackService tracks,
            ITrackRepository trackRepository, IAlbumRepository albumRepository, IMediaFileService mediaFiles, IExtraFileService<LyricFile> lyrics,
            NzbDrone.Core.Notifications.INotificationFactory notifications, Logger logger)
        {
            _notifications = notifications;
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
            var outcome = RunImport(message.DownloadId, message.PlanId);
            if (outcome != Completed && outcome != Refused) throw new CommandFailedException($"Crescendarr import ended {outcome}.");
        }

        /// <summary>
        /// The ONE import path (manual command and M20 automatic cycle): re-observes, requires the exact approved plan ID,
        /// and returns Completed, Refused, RequiresReview or OperatorApprovalRequired. Must run inside a disk-access command.
        /// </summary>
        private string RunImport(string downloadId, string planId)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var match = _adapter.CompletedItems(root, out _).Where(i => i.Item.DownloadId == downloadId).ToList();
            if (match.Count != 1) throw new CommandFailedException("Approved download is not exactly one completed client item.");
            var (item, clientId, watch) = match[0];

            var observed = _adapter.Observe(item, clientId, watch);
            if (observed.Blocked != null || observed.Plan == null)
            {
                return Refuse(downloadId, "blocked: " + observed.Blocked, observed);
            }

            var plan = observed.Plan;
            if (plan.PlanId != planId)
            {
                return Refuse(downloadId, $"plan no longer current (fresh plan {plan.PlanId} disposition={plan.Disposition})", observed);
            }

            if (plan.Disposition != PlanDisposition.Ready)
            {
                return Refuse(downloadId, "plan disposition " + plan.Disposition, observed);
            }

            var operationId = OperationId(plan.PlanId);
            var opRoot = Path.Combine(root, "crescendarr", "ops", operationId);
            var staging = Path.Combine(root, "crescendarr", "staging", operationId);

            // M18 P1: an album with an unfinished (RequiresReview or operator-blocked) operation is never auto-resumed
            // and never gets a second, overlapping operation.
            var blocking = UnfinishedOperations(root, observed.Album.Id);
            if (blocking.Count > 0)
            {
                return Refuse(downloadId, $"album has an unfinished operation [{string.Join(",", blocking)}]; it is not auto-resumed and blocks new imports until an operator resolves it", observed);
            }

            if (Disk.Occupied(opRoot) || Disk.Occupied(staging))
            {
                return Refuse(downloadId, "operation already exists; only the recovery command may continue it", observed);
            }

            // M18 P1: canonical confinement and link checks (dangling links included) for EVERY path, before any write.
            var unsafePath = PathSafety(root, observed, opRoot, staging);
            if (unsafePath != null)
            {
                return Refuse(downloadId, "path safety: " + unsafePath, observed);
            }

            // 1. Operation-owned staged copies of every candidate (the completed download itself is never touched).
            foreach (var (key, sha) in observed.IncomingSha)
            {
                Disk.CopyVerified(observed.ByCandidate[key].Item.Path, Path.Combine(staging, key), sha);
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

            var context = new OperationContext
            {
                Root = root, OperationId = operationId, OperationRoot = opRoot, Artist = staged.Artist, Album = staged.Album,
                ArtistMbid = staged.Artist.Metadata.Value.ForeignArtistId, Staging = staging, LibraryIdentity = Disk.MountIdentity(staged.Artist.Path)
            };
            var artistRoot = staged.Artist.Path.TrimEnd('/');
            var upgrades = new List<TrackBinding>();
            var additions = new List<AdditionBinding>();
            foreach (var decision in selected)
            {
                var stock = staged.ByCandidate[decision.CandidateKey];
                var track = stock.Item.Tracks.Single();
                var destination = Destination(stock, staged.Artist, staged.Album);
                context.StagedDecisionByTrack[track.Id] = stock;
                if (decision.Action == MergeAction.AddMissing)
                {
                    additions.Add(new AdditionBinding(track.Id, track.ForeignRecordingId, destination, stock.Item.Path, Content(stock.Item.Path)));
                    continue;
                }

                var original = staged.TrackFiles[track.TrackFileId];
                var lyricRows = staged.AlbumLyrics.Where(l => l.TrackFileId == original.Id).ToList();
                if (lyricRows.Count > 1) throw new CommandFailedException("More than one lyric row is linked to an upgraded track file.");
                LyricBinding lyric = null;
                if (lyricRows.Count == 1)
                {
                    var lyricPath = Path.Combine(artistRoot, lyricRows[0].RelativePath);
                    lyric = new LyricBinding(lyricRows[0].Id, lyricRows[0].RelativePath, lyricPath, Content(lyricPath),
                        Path.Combine(opRoot, "track-lyrics", track.Id + ".lrc"));
                }

                upgrades.Add(new TrackBinding(track.Id, track.ForeignRecordingId, original.Id, original.Path, destination, stock.Item.Path,
                    Content(original.Path), Content(stock.Item.Path), Path.Combine(opRoot, "audio", track.Id + Path.GetExtension(original.Path)), lyric));
            }

            // Every other owned album track is PROTECTED: its bytes and its link to a row at its path must survive.
            var upgraded = upgrades.Select(u => u.TrackId).ToHashSet();
            var kept = new List<KeptTrackBinding>();
            foreach (var track in staged.AlbumTracks.Where(t => t.TrackFileId > 0 && !upgraded.Contains(t.Id)).OrderBy(t => t.Id))
            {
                if (!staged.TrackFiles.TryGetValue(track.TrackFileId, out var file)) throw new CommandFailedException($"Kept track {track.Id} has no readable track-file row.");
                kept.Add(new KeptTrackBinding(track.Id, track.ForeignRecordingId, file.Id, file.Path, Content(file.Path)));
            }

            // 4. Album-wide lyric backups BEFORE any destructive step, including unselected (superior) tracks.
            var albumLyrics = new List<AlbumLyricBinding>();
            var albumLyricEvidence = new List<object>();
            foreach (var row in staged.AlbumLyrics.OrderBy(l => l.Id))
            {
                var path = Path.Combine(artistRoot, row.RelativePath);
                var owner = staged.AlbumTracks.FirstOrDefault(t => t.TrackFileId == (row.TrackFileId ?? 0) && t.TrackFileId > 0);
                if (owner == null) throw new CommandFailedException($"Album lyric {row.RelativePath} is not linked to a monitored-release track.");
                var sha = Disk.Sha(path);
                var backup = Path.Combine(opRoot, "album-lyrics", row.Id + ".lrc");
                Disk.CopyVerified(path, backup, sha);
                albumLyrics.Add(new AlbumLyricBinding(row.Id, owner.Id, row.RelativePath, path, new ContentId(sha, new FileInfo(path).Length), backup));
                albumLyricEvidence.Add(new { row.Id, row.TrackFileId, trackId = owner.Id, row.RelativePath, sha, backup });
            }

            Disk.WriteDurable(Path.Combine(opRoot, "album-lyrics.json"), JsonSerializer.Serialize(albumLyricEvidence), replace: false);
            Disk.WriteDurable(Path.Combine(opRoot, "operation.json"), JsonSerializer.Serialize(new
            {
                operationId, planId = plan.PlanId, downloadId = item.DownloadId, clientId, staging, downloadFolder = observed.Folder,
                candidates = observed.IncomingSha,
                artistId = staged.Artist.Id, albumId = staged.Album.Id, artistMbid = staged.Artist.Metadata.Value.ForeignArtistId,
                storage = new { library = Disk.MountIdentity(artistRoot), operations = Disk.MountIdentity(opRoot) },
                additions = additions.Select(a => new { a.TrackId, a.DestinationPath }).ToList(),
                kept = kept.Select(k => new { k.TrackId, k.TrackFileId, k.Path }).ToList()
            }), replace: false);

            // 5. ONE write-ahead journal for the whole operation, published before any destructive step.
            var binding = new OperationBinding(operationId,
                InventoryText.IsValidDownloadId(item.DownloadId) ? new DownloadBinding(clientId, item.DownloadId, null) : null,
                staged.Album.Id, staged.Album.ForeignAlbumId, staged.MonitoredRelease.Id, staged.MonitoredRelease.ForeignReleaseId,
                PathStyle.Posix, Path.GetFullPath(_rootFolders.GetBestRootFolder(staged.Artist.Path).Path).TrimEnd('/'), staging, opRoot,
                upgrades.ToImmutableArray(), artistRoot)
            {
                Additions = additions.ToImmutableArray(),
                Kept = kept.ToImmutableArray(),
                AlbumLyrics = albumLyrics.ToImmutableArray(),
            };
            var manifest = JournalEngine.Begin(binding);
            Save(opRoot, manifest);
            context.Manifest = manifest;
            CrescendarrGuard.Emit(_logger, $"OPERATION_PUBLISHED op={operationId} upgrades={upgrades.Count} additions={additions.Count} kept={kept.Count} albumLyricBackups={albumLyrics.Count}");

            string outcome;
            try
            {
                outcome = RunJournal(context);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                outcome = FailClosed(context, error);
            }

            Finish(context, outcome);
            return OperationState(opRoot);
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

            Disk.RequireConfined(root, ops, "operation folder");
            var failures = new List<string>();
            foreach (var opRoot in Directory.GetDirectories(ops).OrderBy(d => d, StringComparer.Ordinal))
            {
                var outcomeFile = Path.Combine(opRoot, "outcome.json");
                if (File.Exists(outcomeFile))
                {
                    // Sticky: a finished, abandoned or RequiresReview operation is never resumed automatically.
                    CrescendarrGuard.Emit(_logger, $"RECOVERY_SKIP op={Path.GetFileName(opRoot)} {File.ReadAllText(outcomeFile)}");
                    continue;
                }

                var context = new OperationContext { Root = root, OperationId = Path.GetFileName(opRoot), OperationRoot = opRoot };
                string outcome;
                try
                {
                    outcome = RecoverOne(context);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    outcome = FailClosed(context, error);
                }

                if (!Finish(context, outcome) && outcome != Abandoned) failures.Add($"{context.OperationId}={outcome}");
            }

            if (failures.Count > 0) throw new CommandFailedException("Crescendarr recovery did not complete: " + string.Join(", ", failures));
        }

        private string RecoverOne(OperationContext context)
        {
            var prepared = PrepareContext(context);
            if (prepared != null) return prepared;
            CrescendarrGuard.Emit(_logger, $"RECOVERY_START op={context.OperationId} status={context.Manifest.Status} generation={context.Manifest.Generation}");
            return RunJournal(context);
        }

        /// <summary>Loads the journal and operation record into the context. Returns an outcome when it cannot.</summary>
        private string PrepareContext(OperationContext context)
        {
            var opRoot = context.OperationRoot;
            var journalPath = Path.Combine(opRoot, "journal.json");
            var operationPath = Path.Combine(opRoot, "operation.json");
            if (!File.Exists(journalPath))
            {
                // The journal is published before any destructive step: without it nothing destructive happened.
                CrescendarrGuard.Emit(_logger, $"RECOVERY_ABANDON op={context.OperationId} journal never published; nothing destructive ran");
                return Abandoned;
            }

            var load = JournalCodec.Decode(File.ReadAllText(journalPath), context.OperationId);
            if (!load.Succeeded) return RequiresReviewWithoutJournal(context, "journal unreadable: " + load.Error);
            context.Manifest = load.Manifest;
            if (!File.Exists(operationPath)) return RequiresReviewWithoutJournal(context, "operation record missing");
            var operation = JsonDocument.Parse(File.ReadAllText(operationPath)).RootElement;
            context.Staging = operation.GetProperty("staging").GetString();
            context.Album = _albums.GetAlbum(operation.GetProperty("albumId").GetInt32());
            context.Artist = _artists.GetArtist(operation.GetProperty("artistId").GetInt32());
            context.ArtistMbid = operation.GetProperty("artistMbid").GetString();
            if (operation.TryGetProperty("storage", out var storage) && storage.TryGetProperty("library", out var library) && library.ValueKind == JsonValueKind.String)
                context.LibraryIdentity = library.GetString();
            var downloadFolder = operation.GetProperty("downloadFolder").GetString();
            var candidates = operation.GetProperty("candidates").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            context.LoadStagedDecisions = () => RefillAndDecide(context, downloadFolder, candidates);
            return null;
        }

        private string RequiresReviewWithoutJournal(OperationContext context, string why)
        {
            context.Proposal = null;
            CrescendarrGuard.Emit(_logger, $"RECOVERY_REVIEW op={context.OperationId} {why}");
            return RequiresReview + ":" + why;
        }

        /// <summary>
        /// Recovery: refill consumed staged copies from the STILL-INTACT completed download (hash-verified against the
        /// operation record), then recompute stock NEW-download decisions on the complete album. Never from stored
        /// command bodies, never NewDownload=false. Called lazily, only for a not-yet-declared native step.
        /// </summary>
        private bool RefillAndDecide(OperationContext context, string downloadFolder, Dictionary<string, string> candidates)
        {
            Disk.RequireConfined(context.Root, context.Staging, "staging folder");
            foreach (var (key, sha) in candidates)
            {
                var staged = Path.Combine(context.Staging, key);
                if (File.Exists(staged)) continue;
                var source = Path.Combine(downloadFolder, key);
                if (!Disk.IsRegularFile(context.Root, source) || Disk.Sha(source) != sha)
                {
                    CrescendarrGuard.Emit(_logger, $"RECOVERY_REVIEW completed download changed or vanished: {key}");
                    return false;
                }

                Disk.CopyVerified(source, staged, sha);
            }

            context.StagedDecisionByTrack.Clear();
            foreach (var decision in _adapter.Decide(context.Staging, null))
            {
                if (decision.Item.Tracks?.Count == 1) context.StagedDecisionByTrack[decision.Item.Tracks[0].Id] = decision;
            }

            CrescendarrGuard.Emit(_logger, $"RECOVERY_DECISIONS staged={context.StagedDecisionByTrack.Count} approved={context.StagedDecisionByTrack.Values.Count(d => d.Approved)}");
            return true;
        }

        /// <summary>Only a stock NEW-download decision on an operation-owned staged copy may reach the native importer.</summary>
        private static bool SafeDecision(OperationContext context, ImportDecision<LocalTrack> decision, string boundIncoming, ContentId incoming) =>
            decision.Approved && !decision.Item.ExistingFile && context.Staging != null &&
            Disk.IsUnder(context.Staging.TrimEnd('/'), Path.GetFullPath(decision.Item.Path)) &&
            Path.GetFullPath(decision.Item.Path) == boundIncoming && Disk.IsRegularFile(context.Root, boundIncoming) && Disk.Sha(boundIncoming) == incoming.Sha256;

        // ------------------------------------------------------------------ the one journal loop

        private string RunJournal(OperationContext context)
        {
            var manifest = context.Manifest;
            var nativeCount = 0;
            string Review(string reason)
            {
                if (manifest.Status == OperationStatus.Active)
                {
                    manifest = JournalEngine.MarkReview(manifest, reason);
                    Save(context.OperationRoot, manifest);
                    context.Manifest = manifest;
                }

                CrescendarrGuard.Emit(_logger, "JOURNAL_REVIEW " + reason);
                return RequiresReview;
            }

            for (var guard = 0; guard < 1000; guard++)
            {
                context.Manifest = manifest;

                // PR #42 review: revalidate the FRESH artist MusicBrainz identity before every step.
                var freshArtist = _artists.GetArtist(context.Artist.Id);
                if (!string.Equals(freshArtist?.Metadata?.Value?.ForeignArtistId, context.ArtistMbid, StringComparison.OrdinalIgnoreCase) && manifest.Status == OperationStatus.Active)
                    return Review("artist MusicBrainz identity changed");

                var host = HostSnapshot(manifest, context.Artist.Id);
                var fs = Observe(context, manifest);
                if (!fs.Available)
                {
                    // M23: an unreachable, unmounted or changed library proves nothing. Do not review, salvage or write.
                    CrescendarrGuard.Emit(_logger, $"STORAGE_UNAVAILABLE op={context.OperationId} {context.StorageProblem}");
                    return StorageUnavailable + ":" + context.StorageProblem;
                }

                var verdict = Reconciler.Reconcile(manifest, host, fs);
                var next = verdict.Next;
                CrescendarrGuard.Emit(_logger, $"JOURNAL gen={manifest.Generation} verdict={verdict.Verdict} next={next?.Action}:{next?.TrackId}:{next?.Mode} \"{next?.Reason}\"" +
                    (verdict.Blockers.IsEmpty ? string.Empty : " blockers=[" + string.Join("; ", verdict.Blockers) + "]"));
                switch (verdict.Verdict)
                {
                    case Verdict.Completed:
                        return Completed;
                    case Verdict.OperatorApprovalRequired:
                        // M19: host-row writes after an interruption run only through CrescendarrPropose/CrescendarrApprove.
                        context.Proposal = next;
                        return OperatorApprovalRequired;
                    case Verdict.RequiresReview:
                        context.Proposal = next is { Mode: ActionMode.OperatorApproval } ? next : null;
                        if (next?.Action == RecoveryAction.MarkReview && manifest.Status == OperationStatus.Active)
                        {
                            manifest = JournalEngine.MarkReview(manifest, next.Reason);
                            Save(context.OperationRoot, manifest);
                            context.Manifest = manifest;
                        }

                        return RequiresReview;
                }

                if (next == null) return Review("reconciler proposed no next step");
                var trackId = next.TrackId ?? 0;
                var isAddition = trackId > 0 && manifest.IsAddition(trackId);
                var binding = trackId > 0 && !isAddition && manifest.Binding.Tracks.Any(t => t.TrackId == trackId) ? manifest.BindingFor(trackId) : null;
                var progress = binding != null ? manifest.ProgressFor(trackId) : null;
                void Declared(JournalStep step)
                {
                    if (progress.InFlight != null) return;
                    manifest = JournalEngine.MarkInFlight(manifest, trackId, step);
                    Save(context.OperationRoot, manifest);
                }

                switch (next.Action)
                {
                    case RecoveryAction.CopyBackup:
                        Declared(JournalStep.CopyBackup);
                        CopyIfAbsent(context.Root, binding.OriginalPath, binding.BackupAudioPath, binding.Original.Sha256);
                        if (binding.Lyric != null) CopyIfAbsent(context.Root, binding.Lyric.Path, binding.Lyric.BackupPath, binding.Lyric.Content.Sha256);
                        break;
                    case RecoveryAction.DetachLyric:
                        Disk.RequireConfined(context.Root, binding.Lyric.Path, "bound lyric");
                        Declared(JournalStep.DetachLyric);
                        // After a recorded rebase the host rescan already removed the row; the step then only records that.
                        if (_lyrics.GetFilesByArtist(context.Artist.Id).Any(l => l.Id == binding.Lyric.LyricFileId)) _lyrics.Delete(binding.Lyric.LyricFileId);
                        break;
                    case RecoveryAction.RecordOriginalRebase:
                        manifest = JournalEngine.RecordOriginalRebase(manifest, trackId, next.NewTrackFileId.Value);
                        Save(context.OperationRoot, manifest);
                        CrescendarrGuard.Emit(_logger, $"REBASE_ORIGINAL track={trackId} boundOriginal={manifest.BindingFor(trackId).OriginalTrackFileId} observedCurrent={next.NewTrackFileId} (evidence only; binding unchanged)");
                        break;
                    case RecoveryAction.InvokeNativeUpgrade:
                    {
                        if (!context.StagedDecisionByTrack.ContainsKey(trackId) && context.LoadStagedDecisions != null && !context.LoadStagedDecisions())
                            return Review("completed download changed or vanished; cannot re-stage the replacement");
                        if (!context.StagedDecisionByTrack.TryGetValue(trackId, out var decision) || !SafeDecision(context, decision, binding.IncomingPath, binding.Incoming))
                            return Review($"track {trackId}: no safe staged new-download decision for the bound incoming file");
                        if (Destination(decision, context.Artist, context.Album) != binding.DestinationPath)
                            return Review($"track {trackId}: stock destination differs from the bound destination");
                        Disk.RequireConfined(context.Root, Path.GetDirectoryName(binding.DestinationPath), "replacement destination folder");
                        Disk.RequireConfined(context.Root, binding.OriginalPath, "replacement original");
                        Declared(JournalStep.NativeUpgrade);
                        List<ImportResult> results;
                        using (ImportScope.Enter(context.Root, context.OperationRoot, trackId, "upgrade"))
                            results = _importer.Import(new List<ImportDecision<LocalTrack>> { decision }, false, null, ImportMode.Move);
                        nativeCount++;
                        CrescendarrGuard.Emit(_logger, $"NATIVE_IMPORT kind=upgrade track={trackId} results=[{Results(results)}]");
                        Faults.Point(_logger, context.Root, "after-native-" + nativeCount);
                        break;
                    }

                    case RecoveryAction.InvokeNativeAdd:
                    {
                        var add = manifest.AdditionBindingFor(trackId);
                        if (!context.StagedDecisionByTrack.ContainsKey(trackId) && context.LoadStagedDecisions != null && !context.LoadStagedDecisions())
                            return Review("completed download changed or vanished; cannot re-stage the addition");
                        if (!context.StagedDecisionByTrack.TryGetValue(trackId, out var decision) || !SafeDecision(context, decision, add.IncomingPath, add.Incoming))
                            return Review($"addition {trackId}: no safe staged new-download decision for the bound incoming file");
                        if (Destination(decision, context.Artist, context.Album) != add.DestinationPath)
                            return Review($"addition {trackId}: stock destination differs from the bound destination");
                        Disk.RequireConfined(context.Root, Path.GetDirectoryName(add.DestinationPath), "addition destination folder");
                        if (Disk.Occupied(add.DestinationPath)) return Review($"addition {trackId}: destination occupied (refusing to overwrite)");
                        manifest = JournalEngine.MarkAdditionInFlight(manifest, trackId);
                        Save(context.OperationRoot, manifest);
                        List<ImportResult> results;
                        using (ImportScope.Enter(context.Root, context.OperationRoot, trackId, "add"))
                            results = _importer.Import(new List<ImportDecision<LocalTrack>> { decision }, false, null, ImportMode.Move);
                        nativeCount++;
                        CrescendarrGuard.Emit(_logger, $"NATIVE_IMPORT kind=add track={trackId} results=[{Results(results)}]");
                        Faults.Point(_logger, context.Root, "after-native-" + nativeCount);
                        break;
                    }

                    case RecoveryAction.RelinkLyric:
                    {
                        var target = next.NewTrackFileId ?? progress.NewTrackFileId.Value;
                        Disk.RequireConfined(context.Root, binding.Lyric.Path, "bound lyric");
                        Declared(JournalStep.RelinkLyric);
                        // Re-read immediately before the insert; any row that appeared meanwhile goes back to the reconciler.
                        if (_lyrics.GetFilesByArtist(context.Artist.Id).Any(l => string.Equals(l.RelativePath, binding.Lyric.RelativePath, StringComparison.OrdinalIgnoreCase)) ||
                            _tracks.GetTrack(trackId).TrackFileId != target)
                            break;
                        _lyrics.Upsert(NewLyricRow(context, manifest.Binding.AlbumId, target, binding.Lyric.RelativePath));
                        break;
                    }

                    case RecoveryAction.RecordStepDone:
                        manifest = isAddition
                            ? JournalEngine.MarkAdditionDone(manifest, trackId, next.NewTrackFileId)
                            : JournalEngine.MarkDone(manifest, trackId, progress.InFlight.Value, next.NewTrackFileId, next.NewLyricFileId);
                        Save(context.OperationRoot, manifest);
                        break;
                    case RecoveryAction.AbandonStep:
                        manifest = isAddition ? JournalEngine.AbandonAddition(manifest, trackId) : JournalEngine.AbandonInFlight(manifest, trackId, progress.InFlight.Value);
                        Save(context.OperationRoot, manifest);
                        break;
                    case RecoveryAction.RestoreOriginalBytes:
                        Disk.RequireConfined(context.Root, binding.OriginalPath, "replacement original");
                        Disk.CopyVerified(binding.BackupAudioPath, binding.OriginalPath, binding.Original.Sha256);
                        break;
                    case RecoveryAction.RestoreAlbumLyric:
                    case RecoveryAction.BindAlbumLyric:
                        ApplyLyricAction(context, manifest, next);
                        break;

                    case RecoveryAction.CompleteOperation:
                        // M23: Completed only after every library file the operation relies on is flushed to the SERVER
                        // (fsync on CIFS is an SMB2 FLUSH round-trip); a client page cache must never stand in for it.
                        if (DurabilityBarrier(context, manifest) is { } unsynced)
                        {
                            context.StorageProblem = unsynced;
                            CrescendarrGuard.Emit(_logger, $"STORAGE_UNAVAILABLE op={context.OperationId} {unsynced}");
                            return StorageUnavailable + ":" + unsynced;
                        }

                        manifest = JournalEngine.Complete(manifest);
                        Save(context.OperationRoot, manifest);
                        break;
                    case RecoveryAction.MarkReview:
                        return Review(next.Reason);
                    default:
                        return Review("unsupported automatic action " + next.Action);
                }
            }

            return Review("journal loop did not converge");
        }

        /// <summary>
        /// OPERATOR-APPROVED only. The native import moved a file into the library but crashed before inserting its row.
        /// Register exactly that file through the stock importer as an existing library file (no album rescan, so no
        /// cascade). Every precondition is re-read; any doubt leaves the operation awaiting approval.
        /// </summary>
        /// <summary>
        /// Lyric-only actions shared by the journal loop and the protective salvage. Every precondition is re-read right
        /// before the write (no overwrite, exact hashes, the track's current row unchanged, no row at the path).
        /// </summary>
        private string ApplyLyricAction(OperationContext context, OperationManifest manifest, NextAction next)
        {
            var lyric = manifest.Binding.AlbumLyrics.Single(l => l.LyricFileId == next.AlbumLyricId);
            Disk.RequireConfined(context.Root, lyric.Path, "album lyric");
            Disk.RequireConfined(context.Root, lyric.BackupPath, "album lyric backup");
            if (next.Action == RecoveryAction.RestoreAlbumLyric)
            {
                if (Disk.Occupied(lyric.Path)) return null; // never overwrite; the reconciler re-evaluates what appeared
                Disk.CopyVerified(lyric.BackupPath, lyric.Path, lyric.Content.Sha256);
                CrescendarrGuard.Emit(_logger, $"RESTORE_ALBUM_LYRIC track={lyric.TrackId} \"{lyric.RelativePath}\" from verified backup sha={lyric.Content.Sha256[..12]}");
                Faults.Point(_logger, context.Root, "after-restore-lyric-" + lyric.TrackId);
                return "restored " + lyric.RelativePath;
            }

            var current = _tracks.GetTrack(lyric.TrackId).TrackFileId;
            if (current != next.NewTrackFileId || !Disk.IsRegularFile(context.Root, lyric.Path) || Disk.Sha(lyric.Path) != lyric.Content.Sha256 ||
                _trackRepository.GetTracksByFileId(current).Count != 1 ||
                _lyrics.GetFilesByArtist(context.Artist.Id).Any(l => string.Equals(l.RelativePath, lyric.RelativePath, StringComparison.OrdinalIgnoreCase)))
                return null; // drift since the snapshot: reconcile again before any insert
            _lyrics.Upsert(NewLyricRow(context, manifest.Binding.AlbumId, current, lyric.RelativePath));
            CrescendarrGuard.Emit(_logger, $"BIND_ALBUM_LYRIC track={lyric.TrackId} \"{lyric.RelativePath}\" -> current trackFile={current}");
            Faults.Point(_logger, context.Root, "after-bind-lyric-" + lyric.TrackId);
            return "bound " + lyric.RelativePath + "->" + current;
        }

        /// <summary>
        /// M19 protective salvage: lyric-only, bounded, each step from a fresh snapshot. It never touches audio, never
        /// changes the journal status and never completes the operation.
        /// </summary>
        private void RunSalvage(OperationContext context)
        {
            if (context.Manifest == null || context.Artist == null) return;
            var skipped = new List<string>();
            var reasons = new List<string>();
            for (var step = 0; step < 256; step++)
            {
                var plan = Reconciler.Salvage(context.Manifest, HostSnapshot(context.Manifest, context.Artist.Id), Observe(context, context.Manifest));
                skipped = plan.Skipped.ToList();
                reasons = plan.Reasons.ToList();
                var next = plan.Actions.FirstOrDefault();
                if (!plan.Applicable || next == null) break;
                var applied = ApplyLyricAction(context, context.Manifest, next);
                if (applied == null) break;
                context.Salvaged.Add(applied);
            }

            CrescendarrGuard.Emit(_logger, $"SALVAGE op={context.OperationId} restored=[{string.Join(",", context.Salvaged.Where(s => s.StartsWith("restored", StringComparison.Ordinal)))}] " +
                $"bound=[{string.Join(",", context.Salvaged.Where(s => s.StartsWith("bound", StringComparison.Ordinal)))}] skipped=[{string.Join("; ", skipped)}] notApplicable=[{string.Join("; ", reasons)}]");
            context.SalvageSkipped = skipped.Concat(reasons).ToList();
        }

        private bool RegisterPlaced(OperationContext context, OperationManifest manifest, int trackId)
        {
            var (destination, incoming) = manifest.IsAddition(trackId)
                ? (manifest.AdditionBindingFor(trackId).DestinationPath, manifest.AdditionBindingFor(trackId).Incoming)
                : (manifest.BindingFor(trackId).DestinationPath, manifest.BindingFor(trackId).Incoming);
            Disk.RequireConfined(context.Root, destination, "placed file");
            if (!Disk.IsRegularFile(context.Root, destination) || Disk.Sha(destination) != incoming.Sha256 || _mediaFiles.GetFileWithPath(new List<string> { destination }).Count > 0 ||
                _tracks.GetTrack(trackId).TrackFileId != 0)
            {
                CrescendarrGuard.Emit(_logger, $"REGISTER_REFUSED track={trackId} preconditions no longer hold");
                return false;
            }

            var decision = _adapter.DecideExistingLibraryFile(context.Artist, destination);
            if (decision == null || !decision.Approved || !decision.Item.ExistingFile || decision.Item.Tracks?.Count != 1 || decision.Item.Tracks[0].Id != trackId ||
                decision.Item.Album?.Id != manifest.Binding.AlbumId || decision.Item.Release?.Id != manifest.Binding.MonitoredReleaseId)
            {
                CrescendarrGuard.Emit(_logger, $"REGISTER_REFUSED track={trackId} stock decision does not match exactly this track and edition: " +
                    (decision == null ? "none" : $"approved={decision.Approved} existing={decision.Item.ExistingFile} rejections={string.Join("|", decision.Rejections.Select(r => r.Reason))}"));
                return false;
            }

            var results = _importer.Import(new List<ImportDecision<LocalTrack>> { decision }, false, null, ImportMode.Auto);
            // Postconditions: exactly one Imported result, the track linked to a row at the destination, and the audio
            // bytes unchanged (stock ExistingFile imports may rewrite tags when tag writing is enabled).
            var linked = _tracks.GetTrack(trackId).TrackFileId;
            var row = linked > 0 ? ExistingRows(new[] { linked }).FirstOrDefault() : null;
            var ok = results.Count == 1 && results[0].Result == ImportResultType.Imported && row?.Path == destination &&
                Disk.IsRegularFile(context.Root, destination) && Disk.Sha(destination) == incoming.Sha256;
            CrescendarrGuard.Emit(_logger, $"REGISTER_PLACED track={trackId} ok={ok} path=\"{destination}\" row={row?.Id} results=[{Results(results)}] (operator approved)");
            return ok;
        }

        /// <summary>Operator-approved: link the track to the row the stock importer inserted before the crash.</summary>
        private bool LinkPlacedRow(OperationContext context, OperationManifest manifest, int trackId, int rowId)
        {
            var (destination, incoming) = manifest.IsAddition(trackId)
                ? (manifest.AdditionBindingFor(trackId).DestinationPath, manifest.AdditionBindingFor(trackId).Incoming)
                : (manifest.BindingFor(trackId).DestinationPath, manifest.BindingFor(trackId).Incoming);
            Disk.RequireConfined(context.Root, destination, "placed file");
            var row = ExistingRows(new[] { rowId }).FirstOrDefault();
            var track = _tracks.GetTrack(trackId);
            if (row == null || row.Path != destination || !Disk.IsRegularFile(context.Root, destination) || Disk.Sha(destination) != incoming.Sha256 ||
                track.TrackFileId != 0 || _trackRepository.GetTracksByFileId(rowId).Count != 0)
            {
                CrescendarrGuard.Emit(_logger, $"LINK_REFUSED track={trackId} row={rowId} preconditions no longer hold");
                return false;
            }

            track.TrackFileId = rowId;
            _tracks.SetFileIds(new List<Track> { track });
            var ok = _tracks.GetTrack(trackId).TrackFileId == rowId && _trackRepository.GetTracksByFileId(rowId).Count == 1;
            CrescendarrGuard.Emit(_logger, $"LINK_PLACED_ROW track={trackId} row={rowId} ok={ok} (operator approved)");
            return ok;
        }

        /// <summary>
        /// fsync every library file the operation relies on (destinations, protected originals, album lyrics), then settle the
        /// TrackFile.Modified of every file this operation placed. Null when all flushed and settled.
        /// </summary>
        private string DurabilityBarrier(OperationContext context, OperationManifest manifest)
        {
            var flushed = FlushAll(manifest);
            return flushed ?? SettleModified(manifest);
        }

        /// <summary>
        /// M23: on the real kernel CIFS mount the server moved the mtime of freshly imported files ~2.4 s AFTER the client
        /// reported it (deferred close flushing buffered writes). Stock sets TrackFile.Modified at import and its rescan treats
        /// a >1 s mtime difference as a changed file, so the next rescan re-imported the whole album and unregistered every
        /// lyric. After flushing, wait out this mount's cache windows, record the SETTLED mtime in the rows this operation
        /// created (Modified only, through the stock service), and require it to stay stable.
        /// </summary>
        private string SettleModified(OperationManifest manifest)
        {
            var b = manifest.Binding;
            var placed = b.Tracks.Select(t => t.DestinationPath).Concat(b.Additions.Select(a => a.DestinationPath)).Distinct(StringComparer.Ordinal).ToList();
            var wait = Disk.CacheSettleSeconds(b.ArtistRoot);
            if (wait == 0) return null;
            Thread.Sleep(TimeSpan.FromSeconds(wait));
            var first = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            try
            {
                foreach (var path in placed)
                {
                    var info = new FileInfo(path);
                    info.Refresh();
                    first[path] = info.LastWriteTimeUtc;
                    var rows = _mediaFiles.GetFileWithPath(new List<string> { path });
                    if (rows.Count != 1) return $"expected exactly one TrackFile row at {path} before settling its mtime";
                    if (Math.Abs((rows[0].Modified.ToUniversalTime() - info.LastWriteTimeUtc).TotalSeconds) <= 0.5) continue;
                    CrescendarrGuard.Emit(_logger, $"MODIFIED_SETTLED \"{Path.GetFileName(path)}\" row={rows[0].Modified.ToUniversalTime():O} server={info.LastWriteTimeUtc:O}");
                    rows[0].Modified = info.LastWriteTimeUtc;
                    _mediaFiles.Update(rows[0]);
                }

                Thread.Sleep(TimeSpan.FromSeconds(wait));
                foreach (var path in placed)
                {
                    var info = new FileInfo(path);
                    info.Refresh();
                    if (Math.Abs((info.LastWriteTimeUtc - first[path]).TotalSeconds) > 0.5)
                        return $"mtime of {path} is still moving after the cache windows; a stock rescan could re-import the album";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"settling mtimes failed ({error.GetType().Name}: {error.Message})";
            }

            return null;
        }

        private static string FlushAll(OperationManifest manifest)
        {
            var b = manifest.Binding;
            var paths = b.Tracks.Select(t => t.DestinationPath).Concat(b.Additions.Select(a => a.DestinationPath)).Concat(b.Kept.Select(k => k.Path))
                .Concat(b.AlbumLyrics.Select(l => l.Path)).Distinct(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return $"durability barrier failed ({error.GetType().Name}: {error.Message}) for {path}";
                }
            }

            return null;
        }

        private string FailClosed(OperationContext context, Exception error)
        {
            CrescendarrGuard.Emit(_logger, $"OPERATION_EXCEPTION op={context.OperationId} {error.GetType().Name}: {error.Message}");
            // M23: an I/O failure (EIO, timeout, stale or read-only mount) is a storage outage, not evidence. The journal stays
            // Active and the album stays blocked; recovery re-observes once storage answers again.
            if (error is IOException || error.InnerException is IOException)
            {
                context.StorageProblem = error.GetType().Name + ": " + error.Message;
                return StorageUnavailable + ":" + context.StorageProblem;
            }

            if (context.Manifest is { Status: OperationStatus.Active } manifest)
            {
                context.Manifest = JournalEngine.MarkReview(manifest, "unexpected " + error.GetType().Name + ": " + error.Message);
                Save(context.OperationRoot, context.Manifest);
            }

            return RequiresReview;
        }

        // ------------------------------------------------------------------ outcome, summary and verification

        private bool Finish(OperationContext context, string outcome)
        {
            string detail = null;
            var colon = outcome.IndexOf(':');
            if (colon > 0)
            {
                detail = outcome[(colon + 1)..];
                outcome = outcome[..colon];
            }

            if (outcome == Completed)
            {
                var verified = VerifyAlbumLyrics(context);
                if (verified != null)
                {
                    outcome = RequiresReview;
                    detail = verified;
                }
            }

            // Completion cleanup: operation-owned staged copies only. Backups, journal and the completed download stay.
            if (outcome == Completed && context.Staging != null && Directory.Exists(context.Staging) && Disk.IsUnder(context.Root, context.Staging) &&
                !Disk.HasLinkInChain(context.Staging, context.Root))
                Directory.Delete(context.Staging, recursive: true);
            // M19: entering review triggers the protective lyric salvage (lyric-only, no overwrite, verified rows only).
            if (outcome == RequiresReview) RunSalvage(context);
            if (outcome is RequiresReview or OperatorApprovalRequired or StorageUnavailable) WriteSummary(context, outcome, detail);

            // OperatorApprovalRequired and StorageUnavailable are not terminal (the album stays blocked; recovery re-evaluates).
            if (outcome is not (OperatorApprovalRequired or StorageUnavailable))
            {
                Disk.WriteDurable(Path.Combine(context.OperationRoot, "outcome.json"), JsonSerializer.Serialize(new
                {
                    outcome, detail,
                    operationComplete = outcome == Completed,
                    success = outcome == Completed,
                    downloadSettled = false,
                    settlement = "Completed Download Handling is OFF: Crescendarr never settles or removes the client item; this outcome is NOT host/download-client settlement.",
                    utc = DateTime.UtcNow
                }), replace: false);
            }

            CrescendarrGuard.Emit(_logger, $"IMPORT_OUTCOME op={context.OperationId} outcome={outcome} detail=\"{detail}\" downloadSettled=false");
            return outcome == Completed;
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
                if (!Disk.IsRegularFile(context.Root, path) || Disk.Sha(path) != lyric.GetProperty("sha").GetString()) return "album lyric bytes changed: " + relative;
                var trackId = lyric.GetProperty("trackId").GetInt32();
                var current = _tracks.GetTrack(trackId);
                if (current.TrackFileId <= 0 || rows.Count(r => r.RelativePath == relative && r.TrackFileId == current.TrackFileId) != 1)
                    return "album lyric not registered to its track's current file: " + relative;
            }

            return null;
        }

        /// <summary>
        /// M18 P1: inspectable operator summary. Pre/post physical hashes, TrackFile IDs, release/recording/release-track
        /// identity, lyric linkage and the refusal reasons. It never claims settlement or success.
        /// </summary>
        private void WriteSummary(OperationContext context, string outcome, string detail)
        {
            try
            {
                var manifest = context.Manifest;
                // M23: never touch unavailable storage while describing it (a frozen SMB server would block the summary).
                var storageDown = outcome == StorageUnavailable;
                string Sha(string path) => storageDown || path == null ? null : Disk.IsRegularFile(context.Root, path) ? Disk.Sha(path) : null;
                Track Fresh(int id)
                {
                    try { return _tracks.GetTrack(id); }
                    catch (Exception) { return null; }
                }

                object Observed(int trackId, string expectedPath)
                {
                    var track = Fresh(trackId);
                    var row = track == null || track.TrackFileId <= 0 ? null : ExistingRows(new[] { track.TrackFileId }).FirstOrDefault();
                    return new
                    {
                        releaseTrackMbid = track?.ForeignTrackId, recordingMbid = track?.ForeignRecordingId, albumReleaseId = track?.AlbumReleaseId,
                        linkedTrackFileId = track?.TrackFileId, linkedRowExists = row != null, rowPath = row?.Path, shaAtExpectedPath = Sha(expectedPath)
                    };
                }

                var lyricRows = context.Artist == null ? new List<LyricFile>() : _lyrics.GetFilesByArtist(context.Artist.Id);
                var b = manifest?.Binding;
                var summary = new
                {
                    operationId = context.OperationId, status = outcome, detail, settled = false, success = false,
                    meaning = "NOT settled and NOT successful. The completed download is left in place. Nothing runs automatically for this album until an operator resolves it. Backups are kept.",
                    reasons = manifest == null ? new List<string> { detail } : manifest.ReviewReasons.ToList(),
                    proposal = context.Proposal == null ? null : new
                    {
                        action = context.Proposal.Action.ToString(), trackId = context.Proposal.TrackId, mode = context.Proposal.Mode.ToString(), reason = context.Proposal.Reason,
                        proposeAction = context.Proposal.Action == RecoveryAction.InsertFreshTrackFileForIncoming ? "register-placed" :
                            context.Proposal.Action == RecoveryAction.LinkPlacedRow ? "link-placed-row" : null
                    },
                    operatorAction = outcome == OperatorApprovalRequired
                        ? "Propose and approve the listed action (CrescendarrPropose then CrescendarrApprove with the returned token; it registers or links ONLY the placed file, no album rescan), " +
                          "or run a stock RescanFolders for the artist and then CrescendarrRecover (the rescan re-imports the whole album and recycles registered lyrics; recovery restores them from the verified backups)."
                        : "Inspect the files and rows below. Fix any conflict, then CrescendarrPropose action=salvage-lyrics and/or action=resolve and approve with the returned token. Crescendarr will not modify this album's audio again automatically.",
                    journal = manifest == null ? null : new { manifest.Status, manifest.Generation },
                    salvage = new { performed = context.Salvaged, notPerformed = context.SalvageSkipped },
                    resolution = "Use CrescendarrPropose/CrescendarrApprove (actions: salvage-lyrics, resolve, register-placed, link-placed-row). Never delete this folder: it holds the only independent backups.",
                    monitoredRelease = b == null ? null : new
                    {
                        b.MonitoredReleaseId, b.MonitoredReleaseMbid,
                        observedMonitored = _releases.GetReleasesByAlbum(b.AlbumId).Where(r => r.Monitored).Select(r => new { r.Id, r.ForeignReleaseId }).ToList()
                    },
                    tracks = b == null ? null : b.Tracks.Select(t => (object)new
                    {
                        role = "upgrade", t.TrackId, t.RecordingMbid, phase = manifest.ProgressFor(t.TrackId).Phase.ToString(), inFlight = manifest.ProgressFor(t.TrackId).InFlight?.ToString(),
                        original = new { trackFileId = t.OriginalTrackFileId, path = t.OriginalPath, sha = t.Original.Sha256, backup = t.BackupAudioPath, backupSha = Sha(t.BackupAudioPath) },
                        expected = new { path = t.DestinationPath, sha = t.Incoming.Sha256, recordedTrackFileId = manifest.ProgressFor(t.TrackId).NewTrackFileId },
                        observed = Observed(t.TrackId, t.DestinationPath)
                    }).Concat(b.Additions.Select(a => (object)new
                    {
                        role = "add", a.TrackId, a.RecordingMbid, phase = manifest.AdditionFor(a.TrackId).Phase.ToString(), inFlight = manifest.AdditionFor(a.TrackId).InFlight,
                        expected = new { path = a.DestinationPath, sha = a.Incoming.Sha256, recordedTrackFileId = manifest.AdditionFor(a.TrackId).NewTrackFileId },
                        observed = Observed(a.TrackId, a.DestinationPath)
                    })).Concat(b.Kept.Select(k => (object)new
                    {
                        role = "kept", k.TrackId, k.RecordingMbid,
                        expected = new { path = k.Path, sha = k.Content.Sha256, originalTrackFileId = k.TrackFileId },
                        observed = Observed(k.TrackId, k.Path)
                    })).ToList(),
                    albumLyrics = b == null ? null : b.AlbumLyrics.Select(l => new
                    {
                        originalLyricFileId = l.LyricFileId, l.TrackId, l.RelativePath, sha = l.Content.Sha256, observedSha = Sha(l.Path),
                        rows = lyricRows.Where(r => string.Equals(r.RelativePath, l.RelativePath, StringComparison.OrdinalIgnoreCase)).Select(r => new { r.Id, r.TrackFileId }).ToList(),
                        backup = l.BackupPath, backupSha = Sha(l.BackupPath), backupOk = Sha(l.BackupPath) == l.Content.Sha256
                    }).ToList(),
                    utc = DateTime.UtcNow
                };
                Disk.WriteDurable(Path.Combine(context.OperationRoot, "review.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), replace: true);
            }
            catch (Exception error)
            {
                CrescendarrGuard.Emit(_logger, "REVIEW_SUMMARY_FAILED " + error.GetType().Name + ": " + error.Message);
            }
        }

        private string Refuse(string downloadId, string why, Observation observed)
        {
            CrescendarrGuard.Emit(_logger, $"IMPORT_REFUSED download=\"{downloadId}\" reason=\"{why}\" mutations=0 {_adapter.Describe(observed)}");
            return Refused;
        }

        /// <summary>Operation IDs for this album that are not finished (no outcome, or an outcome other than Completed/Abandoned).</summary>
        private static List<string> UnfinishedOperations(string root, int albumId)
        {
            var ops = Path.Combine(root, "crescendarr", "ops");
            var result = new List<string>();
            if (!Directory.Exists(ops)) return result;
            foreach (var opRoot in Directory.GetDirectories(ops))
            {
                var operation = Path.Combine(opRoot, "operation.json");
                if (!File.Exists(operation) || JsonDocument.Parse(File.ReadAllText(operation)).RootElement.GetProperty("albumId").GetInt32() != albumId) continue;
                var outcome = Path.Combine(opRoot, "outcome.json");
                // M19: an operator RESOLUTION releases the album block (it is not import completion or settlement).
                var finished = (File.Exists(outcome) && JsonDocument.Parse(File.ReadAllText(outcome)).RootElement.GetProperty("outcome").GetString() is Completed or Abandoned) ||
                    File.Exists(Path.Combine(opRoot, "resolution.json"));
                if (!finished) result.Add(Path.GetFileName(opRoot));
            }

            return result;
        }

        /// <summary>Every path the import will read, write or rely on, checked BEFORE the first write. Returns the first problem.</summary>
        private string PathSafety(string root, Observation o, string opRoot, string staging)
        {
            foreach (var folder in new[] { Path.Combine(root, "crescendarr"), Path.GetDirectoryName(opRoot), opRoot, Path.GetDirectoryName(staging), staging })
            {
                if (!Disk.IsUnder(root, Path.GetFullPath(folder)) || Disk.HasLinkInChain(folder, root)) return "operation storage traverses a link or escapes the root: " + folder;
            }

            var artistRoot = o.Artist.Path.TrimEnd('/');
            if (!Disk.IsUnder(root, artistRoot) || Disk.HasLinkInChain(artistRoot, root)) return "artist folder escapes the root or is reached through a link";
            if (Disk.Probe(artistRoot) != Disk.PathState.Directory) return "artist folder is not reachable as a directory (unmounted or unavailable storage)";
            // M23: a read-only or failing library must be refused BEFORE any destructive step (stock recycles first).
            if (Disk.WriteProbeProblem(artistRoot) is { } notWritable) return "library is not writable: " + notWritable;
            foreach (var decision in o.Decisions)
            {
                var path = Path.GetFullPath(decision.Item.Path);
                if (!Disk.IsUnder(o.Folder, path) || !Disk.IsRegularFile(root, path)) return "download candidate is not a regular file inside the download folder: " + Path.GetFileName(path);
            }

            foreach (var file in o.TrackFiles.Values)
            {
                if (!Disk.IsUnder(artistRoot, file.Path) || !Disk.IsRegularFile(root, file.Path)) return "album audio is missing, outside the artist folder, or a link: " + Path.GetFileName(file.Path);
            }

            foreach (var lyric in o.AlbumLyrics)
            {
                var path = Path.Combine(artistRoot, lyric.RelativePath);
                if (!Disk.IsUnder(artistRoot, Path.GetFullPath(path)) || !Disk.IsRegularFile(root, path)) return "registered lyric is missing or a link (dangling links included): " + lyric.RelativePath;
            }

            foreach (var decision in o.Plan.Decisions.Where(d => d.Action is MergeAction.Upgrade or MergeAction.AddMissing))
            {
                var stock = o.ByCandidate[decision.CandidateKey];
                var destination = Destination(stock, o.Artist, o.Album);
                if (!Disk.IsUnder(artistRoot, destination) || Disk.HasLinkInChain(Path.GetDirectoryName(destination), root)) return "destination escapes the artist folder or traverses a link: " + Path.GetFileName(destination);
                var original = stock.Item.Tracks.Single().TrackFileId > 0 && o.TrackFiles.TryGetValue(stock.Item.Tracks.Single().TrackFileId, out var row) ? row.Path : null;
                if (destination != original && Disk.Occupied(destination)) return "destination is occupied (a file, folder or dangling link): " + Path.GetFileName(destination);
            }

            return null;
        }

        /// <summary>
        /// The destination stock will use: the same TrackFile fields ImportApprovedTracks sets before TrackFileMovingService
        /// builds the path (M21: with renaming ON, stock naming dereferences Quality and reads MediaInfo / ReleaseGroup).
        /// Any remaining difference is caught by the bound-destination check and the post-import byte verification.
        /// </summary>
        private string Destination(ImportDecision<LocalTrack> stock, Artist artist, Album album) =>
            Path.GetFullPath(_fileNames.BuildTrackFilePath(stock.Item.Tracks, artist, album,
                new TrackFile
                {
                    Path = stock.Item.Path, SceneName = stock.Item.SceneName, Size = stock.Item.Size, Quality = stock.Item.Quality,
                    MediaInfo = stock.Item.FileTrackInfo?.MediaInfo, ReleaseGroup = stock.Item.ReleaseGroup
                }, Path.GetExtension(stock.Item.Path)));

        private static LyricFile NewLyricRow(OperationContext context, int albumId, int trackFileId, string relativePath) => new LyricFile
        {
            ArtistId = context.Artist.Id, AlbumId = albumId, TrackFileId = trackFileId, RelativePath = relativePath,
            Extension = Path.GetExtension(relativePath), Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        };

        private static string Results(List<ImportResult> results) => string.Join(",", results.Select(r => r.Result + ":" + string.Join("|", r.Errors)));

        // ------------------------------------------------------------------ observation for the reconciler

        /// <summary>
        /// Fresh host view. M18 P0-B: global sharers are re-queried on EVERY snapshot for the union of original IDs,
        /// recorded fresh IDs, every bound track's CURRENT link, every album row and every row at a bound path. The
        /// album query alone is never taken as complete. TrackFileId 0 is excluded (it is "unlinked", not a row).
        /// </summary>
        private HostSnapshot HostSnapshot(OperationManifest manifest, int artistId)
        {
            try
            {
                var b = manifest.Binding;
                var album = _albums.GetAlbum(b.AlbumId);
                var artist = _artists.GetArtist(artistId);
                if (album.ArtistMetadataId != artist.ArtistMetadataId) throw new InvalidOperationException("album/artist mismatch");
                var releases = _releases.GetReleasesByAlbum(b.AlbumId);
                var releaseTracks = _tracks.GetTracksByReleases(releases.Select(r => r.Id).ToList());
                var bound = b.Tracks.Select(t => t.TrackId).Concat(b.Additions.Select(a => a.TrackId)).Concat(b.Kept.Select(k => k.TrackId)).ToHashSet();
                var albumFiles = _mediaFiles.GetFilesByAlbum(b.AlbumId);
                var paths = b.Tracks.SelectMany(t => new[] { t.OriginalPath, t.DestinationPath }).Concat(b.Additions.Select(a => a.DestinationPath)).Concat(b.Kept.Select(k => k.Path))
                    .Distinct(StringComparer.Ordinal).ToList();
                // The list overload returns zero or more rows; the single-path overload throws when no row exists.
                var pathRows = _mediaFiles.GetFileWithPath(paths);
                var ids = new HashSet<int>();
                ids.UnionWith(b.Tracks.Select(t => t.OriginalTrackFileId));
                ids.UnionWith(b.Kept.Select(k => k.TrackFileId));
                ids.UnionWith(manifest.Progress.Where(p => p.NewTrackFileId != null).Select(p => p.NewTrackFileId.Value));
                ids.UnionWith(manifest.Additions.Where(a => a.NewTrackFileId != null).Select(a => a.NewTrackFileId.Value));
                ids.UnionWith(releaseTracks.Where(t => bound.Contains(t.Id)).Select(t => t.TrackFileId));
                ids.UnionWith(albumFiles.Select(f => f.Id));
                ids.UnionWith(pathRows.Select(f => f.Id));
                ids.Remove(0);
                var sharers = ids.Count == 0 ? new List<Track>() : _trackRepository.GetTracksByFileId(ids);
                var tracks = releaseTracks.Concat(sharers).GroupBy(t => t.Id).Select(g => g.First()).ToList();
                var lyrics = _lyrics.GetFilesByArtist(artist.Id);
                var known = albumFiles.Concat(pathRows).Select(f => f.Id).ToHashSet();
                var wanted = tracks.Select(t => t.TrackFileId).Concat(lyrics.Select(l => l.TrackFileId ?? 0)).Concat(ids).Where(id => id > 0 && !known.Contains(id)).Distinct().ToList();
                var files = albumFiles.Concat(pathRows).Concat(wanted.Count == 0 ? new List<TrackFile>() : ExistingRows(wanted)).GroupBy(f => f.Id).Select(g => g.First()).ToList();
                var foreign = sharers.Where(t => !releaseTracks.Any(r => r.Id == t.Id)).ToList();
                if (foreign.Count > 0)
                    CrescendarrGuard.Emit(_logger, $"SNAPSHOT_GLOBAL_SHARERS rows=[{string.Join(",", ids.OrderBy(i => i))}] foreignTracks=[{string.Join(",", foreign.Select(t => t.Id + "->" + t.TrackFileId))}]");

                return new HostSnapshot(true, b.AlbumId,
                    releases.Select(r => new HostRelease(r.Id, r.ForeignReleaseId, r.Monitored)).ToImmutableArray(),
                    tracks.Select(t => new HostTrack(t.Id, t.ForeignRecordingId, t.TrackFileId)).ToImmutableArray(),
                    files.Select(f => new HostTrackFile(f.Id, f.Path)).ToImmutableArray(),
                    lyrics.Select(l => new HostLyric(l.Id, l.TrackFileId ?? 0, l.RelativePath)).ToImmutableArray(),
                    artist.Path.TrimEnd('/'));
            }
            catch (Exception error)
            {
                CrescendarrGuard.Emit(_logger, "HOST_SNAPSHOT_FAILED " + error.GetType().Name + ": " + error.Message);
                return new HostSnapshot(false, manifest.Binding.AlbumId, ImmutableArray<HostRelease>.Empty, ImmutableArray<HostTrack>.Empty,
                    ImmutableArray<HostTrackFile>.Empty, ImmutableArray<HostLyric>.Empty, null);
            }
        }

        /// <summary>
        /// Rows that exist among the IDs. Stock BasicRepository.Get(IEnumerable) throws unless EVERY ID exists, but deleted
        /// originals and dangling links are exactly what the reconciler must see as absent.
        /// </summary>
        private List<TrackFile> ExistingRows(IEnumerable<int> ids)
        {
            var rows = new List<TrackFile>();
            foreach (var id in ids.Where(i => i > 0).Distinct())
            {
                try
                {
                    rows.Add(_mediaFiles.Get(id));
                }
                catch (NzbDrone.Core.Datastore.ModelNotFoundException)
                {
                    // absent row: the reconciler decides what that means
                }
            }

            return rows;
        }

        /// <summary>
        /// M23: the snapshot is UNAVAILABLE (never "everything absent") when the library mount differs from the one the
        /// operation recorded (e.g. unmounted, leaving an empty local directory) or any bound path cannot be examined.
        /// </summary>
        private static FilesystemSnapshot Observe(OperationContext context, OperationManifest manifest)
        {
            var root = context.Root;
            context.StorageProblem = null;
            var files = ImmutableDictionary.CreateBuilder<string, FileObservation>(StringComparer.Ordinal);
            var available = Directory.Exists(root);
            if (context.LibraryIdentity != null)
            {
                var current = Disk.MountIdentity(manifest.Binding.ArtistRoot);
                if (!string.Equals(current, context.LibraryIdentity, StringComparison.Ordinal))
                {
                    context.StorageProblem = $"library storage changed or unmounted (recorded {context.LibraryIdentity}, now {current})";
                    return new FilesystemSnapshot(false, files.ToImmutable());
                }
            }

            if (Disk.Probe(manifest.Binding.ArtistRoot) != Disk.PathState.Directory)
            {
                context.StorageProblem = $"artist folder is not reachable as a directory ({Disk.Probe(manifest.Binding.ArtistRoot)})";
                return new FilesystemSnapshot(false, files.ToImmutable());
            }

            foreach (var path in Reconciler.AllBoundPaths(manifest.Binding))
            {
                var escapes = !Disk.IsUnder(root, path) || Disk.HasLinkInChain(path, root);
                var state = Disk.Probe(path);
                if (state == Disk.PathState.Error)
                {
                    context.StorageProblem = "a bound path could not be examined (I/O error): " + path;
                    return new FilesystemSnapshot(false, files.ToImmutable());
                }

                try
                {
                    files[path] = state == Disk.PathState.File && !escapes
                        ? new FileObservation(true, new FileInfo(path).Length, Disk.Sha(path), false)
                        : (state != Disk.PathState.Absent || Disk.IsLink(path) ? new FileObservation(true, null, null, escapes) : FileObservation.Absent with { Escapes = escapes });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    context.StorageProblem = $"a bound file could not be read ({error.GetType().Name}): {path}";
                    return new FilesystemSnapshot(false, files.ToImmutable());
                }
            }

            return new FilesystemSnapshot(available, files.ToImmutable());
        }

        // ------------------------------------------------------------------ helpers

        private static void Save(string opRoot, OperationManifest manifest) =>
            Disk.WriteDurable(Path.Combine(opRoot, "journal.json"), JournalCodec.Encode(manifest), replace: true);

        private static void CopyIfAbsent(string root, string source, string destination, string sha)
        {
            Disk.RequireConfined(root, source, "backup source");
            Disk.RequireConfined(root, destination, "backup destination");
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
    }
}
