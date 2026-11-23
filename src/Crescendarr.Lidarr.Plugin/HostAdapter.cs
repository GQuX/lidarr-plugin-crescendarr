// Read-only host adapter: stock download client -> stock identification/decisions -> Crescendarr.Core planner.
using System.Collections.Immutable;
using System.Text;
using Crescendarr.Core.Domain;
using Crescendarr.Core.Planning;
using Crescendarr.Core.Policies;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Blackhole;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using CoreDecision = Crescendarr.Core.Domain.TrackDecision;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    /// <summary>Everything one plan was computed from; the executor recomputes it inside its own command.</summary>
    public sealed class Observation
    {
        public DownloadClientItem Item;
        public int ClientDefinitionId;
        public string WatchFolder;
        public string Folder;
        public List<ImportDecision<LocalTrack>> Decisions = new List<ImportDecision<LocalTrack>>();
        public Dictionary<string, ImportDecision<LocalTrack>> ByCandidate = new Dictionary<string, ImportDecision<LocalTrack>>(StringComparer.Ordinal);
        public Dictionary<string, string> IncomingSha = new Dictionary<string, string>(StringComparer.Ordinal);
        public Artist Artist;
        public Album Album;
        public AlbumRelease MonitoredRelease;
        public AlbumRelease CandidateRelease;
        public List<Track> AlbumTracks = new List<Track>();
        public Dictionary<int, TrackFile> TrackFiles = new Dictionary<int, TrackFile>();
        public List<LyricFile> AlbumLyrics = new List<LyricFile>();
        public MergePlan Plan;
        public string Blocked;
    }

    /// <summary>Quality comparison supplied by the artist's ACTUAL stock quality profile, never by format or size.</summary>
    public sealed class StockQualityPolicy : IHostQualityPolicy
    {
        private readonly QualityProfile _profile;
        private readonly Dictionary<string, QualityModel> _tokens;

        public StockQualityPolicy(QualityProfile profile, Dictionary<string, QualityModel> tokens)
        {
            _profile = profile;
            _tokens = tokens;
        }

        public static string Token(QualityModel quality) =>
            $"{quality.Quality.Id}:{quality.Quality.Name}:v{quality.Revision.Version}:r{quality.Revision.Real}";

        public IncomingQuality Compare(long albumId, string incomingHostQualityToken, string existingHostQualityToken)
        {
            if (!_tokens.TryGetValue(incomingHostQualityToken, out var incoming) || !_tokens.TryGetValue(existingHostQualityToken, out var existing))
                return IncomingQuality.Incomparable;
            var allowed = _profile.Items.Any(i => (i.Allowed && i.Quality?.Id == incoming.Quality.Id) ||
                (i.Allowed && i.Items != null && i.Items.Any(n => n.Allowed && n.Quality?.Id == incoming.Quality.Id)));
            if (!allowed) return IncomingQuality.Incomparable;
            if (!_profile.UpgradeAllowed) return IncomingQuality.Inferior;
            var compare = new QualityModelComparer(_profile).Compare(incoming, existing);
            return compare > 0 ? IncomingQuality.StrictlyPreferred : compare == 0 ? IncomingQuality.Equal : IncomingQuality.Inferior;
        }
    }

    public sealed class HostAdapter
    {
        // Exact stock UpgradeSpecification rejection prefix (v3.1.6.5079). Any other rejection is NOT eligible.
        private const string StockNotAnUpgrade = "Not an upgrade for existing track file(s)";

        private readonly IDownloadClientFactory _clients;
        private readonly IDiskScanService _diskScan;
        private readonly IMakeImportDecision _decisions;
        private readonly IAlbumService _albums;
        private readonly IArtistService _artists;
        private readonly IReleaseService _releases;
        private readonly ITrackService _tracks;
        private readonly ITrackRepository _trackRepository;
        private readonly IMediaFileService _mediaFiles;
        private readonly IExtraFileService<LyricFile> _lyrics;
        private readonly Logger _logger;

        public HostAdapter(IDownloadClientFactory clients, IDiskScanService diskScan, IMakeImportDecision decisions, IAlbumService albums,
            IArtistService artists, IReleaseService releases, ITrackService tracks, ITrackRepository trackRepository,
            IMediaFileService mediaFiles, IExtraFileService<LyricFile> lyrics, Logger logger)
        {
            _clients = clients;
            _diskScan = diskScan;
            _decisions = decisions;
            _albums = albums;
            _artists = artists;
            _releases = releases;
            _tracks = tracks;
            _trackRepository = trackRepository;
            _mediaFiles = mediaFiles;
            _lyrics = lyrics;
            _logger = logger;
        }

        /// <summary>
        /// Completed items of every download-handling-enabled stock client explicitly named "Crescendarr Synthetic ..."
        /// (M17 blackhole; M21 any client type, e.g. SABnzbd), read ONLY through Lidarr's own client factory and client
        /// implementation (no direct downloader API). Every reported output folder must lie in the disposable downloads
        /// folder and contain no link. Unrecognized client names are never touched.
        /// </summary>
        public List<(DownloadClientItem Item, int ClientId, string WatchFolder)> CompletedItems(string root, out List<string> notes)
        {
            notes = new List<string>();
            var result = new List<(DownloadClientItem, int, string)>();
            var downloads = Path.Combine(root, "downloads");
            foreach (var client in _clients.DownloadHandlingEnabled())
            {
                if (client.Definition.Name == null || !client.Definition.Name.StartsWith(CrescendarrGuard.ClientNamePrefix, StringComparison.Ordinal)) continue;
                var confine = downloads;
                if (client is UsenetBlackhole)
                {
                    if (client.Definition.Settings is not UsenetBlackholeSettings settings) continue;
                    confine = Path.GetFullPath(settings.WatchFolder).TrimEnd('/');
                    if (!Disk.IsUnder(root, confine)) throw new CrescendarrGuardException("Synthetic client watch folder is outside the disposable root.");
                }

                foreach (var item in client.GetItems())
                {
                    notes.Add($"{client.Definition.Implementation}:{item.DownloadId}:{item.Status}");
                    if (item.Status != DownloadItemStatus.Completed || item.OutputPath.IsEmpty) continue;
                    var output = Path.GetFullPath(item.OutputPath.FullPath).TrimEnd('/');
                    if (!Disk.IsUnder(confine, output) || Disk.HasLinkInChain(output, root))
                        throw new CrescendarrGuardException("Completed item escapes the disposable downloads folder or traverses a link.");
                    result.Add((item, client.Definition.Id, confine));
                }
            }

            return result;
        }

        /// <summary>Real stock identification and import decisions for a folder, as a NEW single-release download.</summary>
        /// <remarks>
        /// ALWAYS NewDownload=true: stock ImportDecisionMaker sets ExistingFile = !NewDownload, and an
        /// "existing" decision is imported IN PLACE wherever the file lives (observed on the real daemon).
        /// </remarks>
        public List<ImportDecision<LocalTrack>> Decide(string folder, DownloadClientItem item)
        {
            var files = _diskScan.GetAudioFiles(folder).ToList();
            return _decisions.GetImportDecisions(files, null, new ImportDecisionMakerInfo { DownloadClientItem = item },
                new ImportDecisionMakerConfig
                {
                    Filter = FilterFilesType.None,
                    NewDownload = true,
                    SingleRelease = true,
                    IncludeExisting = false,
                    AddNewArtists = false
                });
        }

        /// <summary>
        /// M18, OPERATOR-APPROVED recovery only: the stock disk-scan decision (existing file, IncludeExisting for identity)
        /// for ONE file already at its final library path. Only the decision for exactly that path is returned, so the
        /// album's other tracked files are never re-imported (no album-wide cascade, no lyric recycling).
        /// </summary>
        public ImportDecision<LocalTrack> DecideExistingLibraryFile(Artist artist, string path)
        {
            var files = _diskScan.GetAudioFiles(Path.GetDirectoryName(path), false).Where(f => f.FullName == path).ToList();
            if (files.Count != 1) return null;
            var decisions = _decisions.GetImportDecisions(files, new IdentificationOverrides { Artist = artist }, null,
                new ImportDecisionMakerConfig
                {
                    Filter = FilterFilesType.None,
                    NewDownload = false,
                    SingleRelease = false,
                    IncludeExisting = true,
                    AddNewArtists = false
                });
            var matching = decisions.Where(d => d.Item.Path == path).ToList();
            return matching.Count == 1 ? matching[0] : null;
        }

        public Observation Observe(DownloadClientItem item, int clientId, string watchFolder, string decisionFolder = null)
        {
            var o = new Observation { Item = item, ClientDefinitionId = clientId, WatchFolder = watchFolder, Folder = Path.GetFullPath(item.OutputPath.FullPath).TrimEnd('/') };
            o.Decisions = Decide(decisionFolder ?? o.Folder, item);
            foreach (var decision in o.Decisions)
            {
                var key = Path.GetFileName(decision.Item.Path);
                if (o.ByCandidate.ContainsKey(key)) { o.Blocked = "duplicate candidate file name"; continue; }
                o.ByCandidate[key] = decision;
                o.IncomingSha[key] = Disk.Sha(decision.Item.Path);
            }

            var albums = o.Decisions.Where(d => d.Item.Album != null).Select(d => d.Item.Album.Id).Distinct().ToList();
            var releases = o.Decisions.Where(d => d.Item.Release != null).Select(d => d.Item.Release.Id).Distinct().ToList();
            if (o.Decisions.Count == 0) o.Blocked = "no audio files";
            else if (albums.Count != 1 || releases.Count != 1 || o.Decisions.Any(d => d.Item.Album == null || d.Item.Release == null))
                o.Blocked = "candidate files do not identify exactly one album edition";
            if (o.Blocked != null) return o;

            o.Album = _albums.GetAlbum(albums[0]);
            o.Artist = _artists.GetArtist(o.Decisions[0].Item.Artist.Id);
            if (o.Album.ArtistMetadataId != o.Artist.ArtistMetadataId) { o.Blocked = "album and artist identities disagree"; return o; }
            o.CandidateRelease = _releases.GetReleasesByAlbum(o.Album.Id).Single(r => r.Id == releases[0]);
            var monitored = _releases.GetReleasesByAlbum(o.Album.Id).Where(r => r.Monitored).ToList();
            if (monitored.Count != 1) { o.Blocked = $"album has {monitored.Count} monitored releases"; return o; }
            o.MonitoredRelease = monitored[0];
            o.AlbumTracks = _tracks.GetTracksByRelease(o.MonitoredRelease.Id);
            foreach (var file in _mediaFiles.GetFilesByAlbum(o.Album.Id)) o.TrackFiles[file.Id] = file;
            o.AlbumLyrics = _lyrics.GetFilesByArtist(o.Artist.Id).Where(l => l.AlbumId == o.Album.Id).ToList();
            o.Plan = BuildPlan(o);
            return o;
        }

        private MergePlan BuildPlan(Observation o)
        {
            var tokens = new Dictionary<string, QualityModel>(StringComparer.Ordinal);
            string Remember(QualityModel q)
            {
                var token = StockQualityPolicy.Token(q);
                tokens[token] = q;
                return token;
            }

            var incoming = ImmutableArray.CreateBuilder<IncomingFile>();
            var inventory = new StringBuilder();
            foreach (var (key, decision) in o.ByCandidate.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var local = decision.Item;
                var tracks = local.Tracks ?? new List<Track>();
                var track = tracks.Count == 1 ? tracks[0] : null;
                var info = local.FileTrackInfo ?? new ParsedTrackInfo();

                // Identity must agree between the file's own tags and the stock-matched release track.
                var identity = track != null && local.Release != null && track.AlbumReleaseId == local.Release.Id &&
                    Same(info.RecordingMBId, track.ForeignRecordingId) && Same(info.TrackMBId, track.ForeignTrackId) &&
                    Same(info.ReleaseMBId, local.Release.ForeignReleaseId) &&
                    Same(info.ArtistMBId, o.Artist.Metadata.Value.ForeignArtistId);
                var onlyQualityRejections = decision.Rejections.All(r => r.Reason != null && r.Reason.StartsWith(StockNotAnUpgrade, StringComparison.Ordinal));
                var lrc = File.Exists(Path.ChangeExtension(local.Path, ".lrc"));
                var stable = File.Exists(local.Path) && new FileInfo(local.Path).Length == local.Size && Disk.Sha(local.Path) == o.IncomingSha[key];
                incoming.Add(new IncomingFile(key, track == null ? ImmutableArray<string>.Empty : ImmutableArray.Create(track.ForeignRecordingId),
                    local.Quality == null ? null : Remember(local.Quality), identity, stable, decision.Approved || onlyQualityRejections, lrc));
                inventory.Append(key).Append('|').Append(o.IncomingSha[key]).Append('|').Append(local.Size).Append('\n');
            }

            var existing = ImmutableArray.CreateBuilder<ExistingFile>();
            var state = new StringBuilder();
            foreach (var track in o.AlbumTracks.Where(t => t.TrackFileId > 0).OrderBy(t => t.Id))
            {
                o.TrackFiles.TryGetValue(track.TrackFileId, out var file);
                file ??= _mediaFiles.Get(track.TrackFileId);

                // GLOBAL sharer enumeration (M15): every track in ANY album that links this file.
                var sharers = _trackRepository.GetTracksByFileId(track.TrackFileId);
                var present = file != null && File.Exists(file.Path) && new FileInfo(file.Path).Length == file.Size &&
                    !Disk.HasLinkInChain(file.Path, o.Artist.Path);
                var lyricRows = o.AlbumLyrics.Where(l => l.TrackFileId == track.TrackFileId).ToList();
                existing.Add(new ExistingFile(track.TrackFileId, sharers.Select(s => s.ForeignRecordingId).ToImmutableArray(),
                    file?.Quality == null ? null : Remember(file.Quality),
                    sharers.Count == 1 && sharers[0].Id == track.Id && file?.AlbumId == o.Album.Id, present, lyricRows.Count > 0));
                state.Append(track.Id).Append('|').Append(track.TrackFileId).Append('|').Append(file?.Path).Append('|').Append(file?.Size)
                    .Append('|').Append(file == null ? string.Empty : StockQualityPolicy.Token(file.Quality))
                    .Append('|').Append(string.Join(",", lyricRows.Select(l => l.Id + ":" + l.RelativePath))).Append('\n');
            }

            var download = new DownloadSnapshot(o.Item.DownloadId, o.Album.Id, o.CandidateRelease.ForeignReleaseId,
                Disk.Sha(Encoding.UTF8.GetBytes(inventory.ToString())), incoming.ToImmutable());
            var album = new AlbumSnapshot(o.Album.Id, o.MonitoredRelease.ForeignReleaseId,
                Disk.Sha(Encoding.UTF8.GetBytes(state.ToString())), existing.ToImmutable());
            var plan = new MergePlanner().Plan(download, album, new StockQualityPolicy(o.Artist.QualityProfile.Value, tokens));

            // Stock must agree with every write the planner wants; anything else blocks the whole plan.
            var disagreements = plan.Decisions.Where(d => d.Action is MergeAction.Upgrade or MergeAction.AddMissing && !o.ByCandidate[d.CandidateKey].Approved).ToList();
            if (disagreements.Count > 0 && plan.Disposition == PlanDisposition.Ready)
            {
                o.Blocked = "stock import decision rejected a planned write: " + string.Join(",", disagreements.Select(d => d.CandidateKey));
            }

            return plan;
        }

        public string Describe(Observation o)
        {
            var text = new StringBuilder();
            text.Append($"download=\"{o.Item.DownloadId}\" disposition={(o.Blocked != null ? "Blocked" : o.Plan?.Disposition.ToString())} ");
            text.Append($"planId={o.Plan?.PlanId ?? "none"} blocked=\"{o.Blocked}\" album={o.Album?.Id} monitoredRelease={o.MonitoredRelease?.ForeignReleaseId} ");
            text.Append($"candidateRelease={o.CandidateRelease?.ForeignReleaseId} decisions=[");
            if (o.Plan != null)
            {
                text.Append(string.Join(";", o.Plan.Decisions.Select(d => DescribeDecision(o, d))));
            }
            else
            {
                text.Append(string.Join(";", o.Decisions.Select(d => $"{Path.GetFileName(d.Item.Path)}:stockApproved={d.Approved}:{string.Join("|", d.Rejections.Select(r => r.Reason))}")));
            }

            return text.Append(']').ToString();
        }

        private static string DescribeDecision(Observation o, CoreDecision d)
        {
            var stock = o.ByCandidate[d.CandidateKey];
            var track = stock.Item.Tracks?.Count == 1 ? stock.Item.Tracks[0] : null;
            return $"{d.CandidateKey}:track={track?.AbsoluteTrackNumber}:{d.Action}:{d.ReasonCode}:in={stock.Item.Quality?.Quality.Name}:" +
                $"existingFile={d.ExpectedExistingTrackFileId}:keepLyric={d.Lyrics.PreserveExistingLrc}:stockApproved={stock.Approved}" +
                (stock.Approved ? string.Empty : ":" + string.Join("|", stock.Rejections.Select(r => r.Reason)));
        }

        private static bool Same(string a, string b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y && x != Guid.Empty;
    }
}
