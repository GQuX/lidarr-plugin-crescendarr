// M25: one read-only audit cycle. Observes the operator-selected clients' completed downloads through stock Lidarr's own
// client implementations, plans each through stock identification/decisions and the shared Crescendarr planner, records
// the decision with the shared M24 registry policy, and writes a local report. It has no write path to the library.
using System.Text;
using System.Text.Json;
using Crescendarr.Core.Domain;
using Crescendarr.Core.Registry;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public sealed class CrescendarrAuditExecutor : IExecute<CrescendarrAuditCycleCommand>
    {
        private static readonly JsonSerializerOptions Indented = new JsonSerializerOptions { WriteIndented = true };
        private readonly INotificationFactory _notifications;
        private readonly IDownloadClientFactory _clients;
        private readonly HostAdapter _adapter;
        private readonly IRootFolderService _rootFolders;
        private readonly IAppFolderInfo _appFolder;
        private readonly Logger _logger;

        public CrescendarrAuditExecutor(INotificationFactory notifications, IDownloadClientFactory clients, HostAdapter adapter, IRootFolderService rootFolders,
            IAppFolderInfo appFolder, Logger logger)
        {
            _notifications = notifications;
            _clients = clients;
            _adapter = adapter;
            _rootFolders = rootFolders;
            _appFolder = appFolder;
            _logger = logger;
        }

        public void Execute(CrescendarrAuditCycleCommand message)
        {
            var counters = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["observed"] = 0, ["wouldChange"] = 0, ["wouldUpgrade"] = 0, ["wouldAdd"] = 0, ["wouldKeep"] = 0, ["noImprovement"] = 0,
                ["review"] = 0, ["refused"] = 0, ["unavailable"] = 0, ["inProgress"] = 0, ["unchanged"] = 0, ["reevaluated"] = 0, ["errors"] = 0,
            };
            var settings = CrescendarrAuditConfiguration.Read(_notifications, out var problem);
            var state = AuditBarrier.Initialise(_appFolder, _rootFolders);
            if (settings == null || settings.Mode != (int)CrescendarrAuditMode.AuditOnly)
            {
                WriteStatus(state, problem ?? "disabled", counters, new List<string>());
                AuditLog.Emit(_logger, $"AUDIT_CYCLE_SKIPPED reason=\"{problem ?? "disabled"}\"");
                return;
            }

            AuditLog.Emit(_logger, $"AUDIT_CYCLE_START reason={message.Reason} version={CrescendarrBuild.Version}");
            var allowed = CrescendarrAuditSettings.ParseFolders(settings.AllowedDownloadFolders).Select(f => Path.GetFullPath(f).TrimEnd('/')).ToList();
            var selected = (settings.ObservedClients ?? Array.Empty<int>()).ToHashSet();
            var clientProblems = new List<string>();
            var items = new List<(DownloadClientItem Item, int ClientId, string ClientName, string Folder)>();
            var refused = new List<(DownloadClientItem Item, int ClientId, string Folder, string State, string Reason)>();
            var enabled = _clients.DownloadHandlingEnabled().Where(c => selected.Contains(c.Definition.Id)).ToList();
            foreach (var id in selected.Where(id => enabled.All(c => c.Definition.Id != id)).OrderBy(i => i))
                clientProblems.Add($"selected download client {id} is missing or disabled");

            foreach (var client in enabled.OrderBy(c => c.Definition.Id))
            {
                List<DownloadClientItem> reported;
                try
                {
                    reported = client.GetItems().ToList();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    clientProblems.Add($"{client.Definition.Name} unavailable: {error.GetType().Name}");
                    continue;
                }

                foreach (var item in reported)
                {
                    if (item.Status != DownloadItemStatus.Completed)
                    {
                        counters["inProgress"]++;
                        continue;
                    }

                    if (item.OutputPath.IsEmpty)
                    {
                        refused.Add((item, client.Definition.Id, null, "RefusedUnsafePath", "the client reports no output folder"));
                        continue;
                    }

                    var folder = Path.GetFullPath(item.OutputPath.FullPath).TrimEnd('/');
                    if (!allowed.Any(a => folder == a || Disk.IsUnder(a, folder)))
                        refused.Add((item, client.Definition.Id, folder, "RefusedUnsafePath", "the completed folder is outside the allowed download folders (check the setting and Lidarr's remote path mappings)"));
                    else if (Disk.HasLinkInChain(folder, "/"))
                        refused.Add((item, client.Definition.Id, folder, "RefusedUnsafePath", "the completed folder is reached through a symbolic link"));
                    else if (Disk.Probe(folder) != Disk.PathState.Directory)
                        refused.Add((item, client.Definition.Id, folder, "SourceUnavailable", $"the completed folder is not reachable ({Disk.Probe(folder)}); offline, moved or no permission"));
                    else
                        items.Add((item, client.Definition.Id, client.Definition.Name, folder));
                }
            }

            var report = new List<object>();
            foreach (var (item, clientId, folder, refusedState, reason) in refused)
            {
                counters[refusedState == "SourceUnavailable" ? "unavailable" : "refused"]++;
                report.Add(new { downloadId = item.DownloadId, client = clientId, folder = folder == null ? null : Path.GetFileName(folder), state = refusedState, reason, tracks = Array.Empty<object>() });
                if (folder != null) Record(state, new SourceIdentity(clientId, item.DownloadId, folder, null), refusedState, reason, null, null, null);
            }

            var ambiguous = items.GroupBy(i => i.Folder, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g)
                .Concat(items.GroupBy(i => i.Item.DownloadId, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g))
                .Select(i => i.Folder).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in ambiguous.OrderBy(f => f, StringComparer.Ordinal))
            {
                counters["review"]++;
                var claimants = items.Where(i => i.Folder == folder).ToList();
                var reason = "several download-client items report this completed folder or download ID; it is not inspected";
                report.Add(new { downloadId = claimants[0].Item.DownloadId, client = claimants[0].ClientId, folder = Path.GetFileName(folder), state = DownloadRegistryPolicy.AmbiguousSource, reason, tracks = Array.Empty<object>() });
                Record(state, new SourceIdentity(claimants[0].ClientId, claimants[0].Item.DownloadId, folder, null), DownloadRegistryPolicy.AmbiguousSource, reason, null, null, null);
            }

            foreach (var (item, clientId, clientName, folder) in items.Where(i => !ambiguous.Contains(i.Folder)))
            {
                counters["observed"]++;
                try
                {
                    report.Add(Audit(state, item, clientId, clientName, folder, counters));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    counters["errors"]++;
                    AuditLog.Emit(_logger, $"AUDIT_DOWNLOAD_ERROR download=\"{item.DownloadId}\" {error.GetType().Name}: {error.Message}");
                    report.Add(new { downloadId = item.DownloadId, client = clientId, folder = Path.GetFileName(folder), state = "Error", reason = error.GetType().Name + ": " + error.Message, tracks = Array.Empty<object>() });
                }
            }

            WriteReport(state, report, counters, clientProblems);
            WriteStatus(state, null, counters, clientProblems);
            AuditLog.Emit(_logger, "AUDIT_CYCLE_SUMMARY " + string.Join(" ", counters.Select(c => c.Key + "=" + c.Value)) + $" clientProblems={clientProblems.Count}");
        }

        private object Audit(string state, DownloadClientItem item, int clientId, string clientName, string folder, Dictionary<string, int> counters)
        {
            var observation = _adapter.Observe(item, clientId, folder);
            var libraryProblem = LibraryProblem(observation);
            var disposition = libraryProblem != null ? "LibraryUnavailable" : observation.Blocked != null ? "Blocked" : observation.Plan?.Disposition.ToString() ?? "Blocked";
            var blocked = libraryProblem ?? observation.Blocked;
            var evaluation = DownloadRegistryPolicy.Evaluation(disposition, observation.Plan?.PlanId, blocked);
            var current = new SourceIdentity(clientId, item.DownloadId, folder, Digest(observation.IncomingSha));
            var previous = Read(state, folder);
            var decision = DownloadRegistryPolicy.Decide(previous, current, evaluation, OperationEvidence.None);
            var tracks = libraryProblem == null ? Tracks(observation) : new List<TrackPreview>();
            var outcome = decision.Action == RegistryAction.Review ||
                (decision.Action == RegistryAction.SkipUnchanged && DownloadRegistryPolicy.ReviewStates.Contains(decision.State))
                ? decision.State
                : disposition switch
                {
                    "Ready" => "WouldImport",
                    "NoImprovement" => "NoImprovement",
                    "NeedsReview" => "NeedsReview",
                    "LibraryUnavailable" => "LibraryUnavailable",
                    _ => "Blocked",
                };

            if (outcome == "WouldImport")
            {
                counters["wouldChange"]++;
                counters["wouldUpgrade"] += tracks.Count(t => t.Action == "Upgrade");
                counters["wouldAdd"] += tracks.Count(t => t.Action == "Add");
                counters["wouldKeep"] += tracks.Count(t => t.Action == "Keep");
            }
            else if (outcome == "NoImprovement") counters["noImprovement"]++;
            else if (outcome == "LibraryUnavailable") counters["unavailable"]++;
            else counters["review"]++;

            var changed = previous == null || previous.State != outcome || previous.Evaluation != evaluation;
            if (changed)
            {
                if (previous != null) counters["reevaluated"]++;
                AuditLog.Emit(_logger, $"AUDIT_DOWNLOAD download=\"{item.DownloadId}\" state={outcome} previous={previous?.State ?? "none"} plan={observation.Plan?.PlanId} " +
                    $"upgrade={tracks.Count(t => t.Action == "Upgrade")} add={tracks.Count(t => t.Action == "Add")} keep={tracks.Count(t => t.Action == "Keep")} reason=\"{blocked}\"");
                Record(state, current, outcome, blocked ?? decision.Reason, observation.Plan?.PlanId, disposition, evaluation);
            }
            else
            {
                counters["unchanged"]++;
            }

            return new
            {
                downloadId = item.DownloadId, client = clientId, clientName, folder = Path.GetFileName(folder), state = outcome, disposition, reason = blocked,
                planId = observation.Plan?.PlanId,
                artist = observation.Artist?.Name, album = observation.Album?.Title,
                monitoredRelease = observation.MonitoredRelease == null ? null : $"{observation.MonitoredRelease.Title} [{observation.MonitoredRelease.ForeignReleaseId}]",
                candidateRelease = observation.CandidateRelease == null ? null : $"{observation.CandidateRelease.Title} [{observation.CandidateRelease.ForeignReleaseId}]",
                tracks,
                summary = new
                {
                    upgrade = tracks.Count(t => t.Action == "Upgrade"), keep = tracks.Count(t => t.Action == "Keep"), add = tracks.Count(t => t.Action == "Add"),
                    review = tracks.Count(t => t.Action == "Review"), missing = tracks.Count(t => t.Action == "Missing"),
                },
            };
        }

        /// <summary>
        /// Absent media must never be read as "new": an unreachable artist folder or an unreadable tracked file (unmounted
        /// share, stale SMB, no permission) stops the preview instead of proposing additions.
        /// </summary>
        private string LibraryProblem(Observation o)
        {
            if (o.Artist == null) return null;
            var artistPath = Path.GetFullPath(o.Artist.Path).TrimEnd('/');
            var root = _rootFolders.All().Select(r => Path.GetFullPath(r.Path).TrimEnd('/')).Where(r => Disk.IsUnder(r, artistPath)).OrderByDescending(r => r.Length).FirstOrDefault();
            if (root == null) return "the artist folder is not inside a configured root folder";
            if (Disk.HasLinkInChain(artistPath, root)) return "the artist folder is reached through a symbolic link";
            var artistState = Disk.Probe(artistPath);
            if (artistState != Disk.PathState.Directory) return $"the artist folder is not reachable ({artistState}); unmounted, offline or no permission";
            foreach (var file in o.TrackFiles.Values)
            {
                var fileState = Disk.Probe(file.Path);
                if (fileState == Disk.PathState.Error) return "a tracked file could not be examined (I/O error or no permission): " + Path.GetFileName(file.Path);
                if (fileState == Disk.PathState.File && Disk.HasLinkInChain(file.Path, root)) return "a tracked file is reached through a symbolic link: " + Path.GetFileName(file.Path);
            }

            return null;
        }

        public sealed record TrackPreview(int Track, string Title, string Action, string Current, string Incoming, string Reason, bool KeepsLyric, string Candidate);

        /// <summary>Per album track: what a selective import would do, with current and incoming quality as stock names them.</summary>
        private static List<TrackPreview> Tracks(Observation o)
        {
            var result = new List<TrackPreview>();
            if (o.AlbumTracks == null || o.AlbumTracks.Count == 0) return result;
            var decisions = o.Plan?.Decisions ?? System.Collections.Immutable.ImmutableArray<TrackDecision>.Empty;
            foreach (var track in o.AlbumTracks.OrderBy(t => t.MediumNumber).ThenBy(t => t.AbsoluteTrackNumber))
            {
                o.TrackFiles.TryGetValue(track.TrackFileId, out var file);
                var current = track.TrackFileId > 0 ? file?.Quality?.Quality?.Name ?? "unknown" : null;
                var decision = decisions.FirstOrDefault(d => d.RecordingIds.Any(r => Same(r, track.ForeignRecordingId)));
                if (decision == null)
                {
                    result.Add(new TrackPreview(track.AbsoluteTrackNumber, track.Title, track.TrackFileId > 0 ? "Unchanged" : "Missing", current, null,
                        track.TrackFileId > 0 ? "not in this download" : "missing and not in this download", false, null));
                    continue;
                }

                o.ByCandidate.TryGetValue(decision.CandidateKey, out var stock);
                var action = decision.Action switch
                {
                    MergeAction.Upgrade => "Upgrade",
                    MergeAction.AddMissing => "Add",
                    MergeAction.KeepExisting => "Keep",
                    _ => "Review",
                };
                result.Add(new TrackPreview(track.AbsoluteTrackNumber, track.Title, action, current, stock?.Item.Quality?.Quality?.Name, decision.ReasonCode,
                    decision.Lyrics.PreserveExistingLrc, decision.CandidateKey));
            }

            return result;
        }

        private static bool Same(string a, string b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y && x != Guid.Empty;

        private static string Digest(IReadOnlyDictionary<string, string> incoming) =>
            Disk.Sha(Encoding.UTF8.GetBytes(string.Join("\n", incoming.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value))));

        private static string RegistryPath(string state, string folder) => Path.Combine(state, "downloads", Disk.Sha(Encoding.UTF8.GetBytes(folder))[..16] + ".json");

        /// <summary>The previous audit record, or null. An unreadable record is preserved as evidence (inside the state folder only).</summary>
        private static RegistryRecord Read(string state, string folder)
        {
            var path = RegistryPath(state, folder);
            if (!File.Exists(path)) return null;
            try
            {
                var raw = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
                string Text(string name) => raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return new RegistryRecord(raw.GetProperty("schema").GetInt32(), Text("state"),
                    new SourceIdentity(raw.GetProperty("clientDefinitionId").GetInt32(), Text("downloadId"), Text("folder"), Text("inventoryDigest")), Text("evaluation"));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                AuditBarrier.RequireStatePath(path);
                File.Move(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture));
                return null;
            }
        }

        private static void Record(string state, SourceIdentity identity, string outcome, string reason, string planId, string disposition, string evaluation)
        {
            Disk.WriteDurable(RegistryPath(state, identity.Folder), JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["schema"] = DownloadRegistryPolicy.CurrentSchema, ["build"] = CrescendarrBuild.Flavor,
                ["downloadId"] = identity.DownloadId, ["clientDefinitionId"] = identity.ClientId, ["folder"] = identity.Folder, ["inventoryDigest"] = identity.ContentDigest,
                ["state"] = outcome, ["reason"] = reason, ["planId"] = planId, ["disposition"] = disposition, ["evaluation"] = evaluation,
                ["pluginHandled"] = false, ["lidarrImported"] = false, ["downloaderRemoved"] = false, ["imported"] = false,
                ["note"] = "Audit-only preview. Nothing was imported, approved, recovered or settled; the download client item was not touched.",
                ["lastEvaluatedUtc"] = DateTime.UtcNow,
            }, Indented), replace: true);
        }

        private void WriteStatus(string state, string problem, Dictionary<string, int> counters, List<string> clientProblems)
        {
            Disk.WriteDurable(Path.Combine(state, "status.json"), JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, version = CrescendarrBuild.Version, build = CrescendarrBuild.Flavor, libraryWrites = "impossible in this build",
                problem, counters, clientProblems,
            }, Indented), replace: true);
        }

        private static void WriteReport(string state, List<object> downloads, Dictionary<string, int> counters, List<string> clientProblems)
        {
            var json = JsonSerializer.Serialize(new
            {
                version = CrescendarrBuild.Version, build = CrescendarrBuild.Flavor, generatedUtc = DateTime.UtcNow,
                notice = "AUDIT ONLY: this is a preview. Nothing was imported, moved, deleted, relinked, approved or settled.",
                counters, clientProblems, downloads,
            }, Indented);
            Disk.WriteDurable(Path.Combine(state, "report.json"), json, replace: true);
            Disk.WriteDurable(Path.Combine(state, "report.md"), Markdown(JsonDocument.Parse(json).RootElement), replace: true);
            Disk.WriteDurable(Path.Combine(state, "diagnostics-redacted.json"), Redacted(JsonDocument.Parse(json).RootElement), replace: true);
        }

        private static string Markdown(JsonElement report)
        {
            var text = new StringBuilder();
            text.Append("# Crescendarr audit-only preview\n\n");
            text.Append("**AUDIT ONLY.** Nothing was imported, moved, deleted, relinked, approved or settled. Build ").Append(report.GetProperty("version").GetString())
                .Append(", generated ").Append(report.GetProperty("generatedUtc").GetDateTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture)).Append(".\n\n");
            foreach (var problem in report.GetProperty("clientProblems").EnumerateArray()) text.Append("- Client problem: ").Append(problem.GetString()).Append('\n');
            foreach (var d in report.GetProperty("downloads").EnumerateArray())
            {
                string S(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                text.Append("\n## ").Append(S("artist") ?? "?").Append(" - ").Append(S("album") ?? S("folder") ?? "?").Append('\n');
                text.Append("\n- Download: `").Append(S("folder")).Append("` (client ").Append(d.GetProperty("client").GetInt32()).Append(")\n");
                text.Append("- **").Append(Verdict(S("state"))).Append("**");
                if (S("reason") != null) text.Append(": ").Append(S("reason"));
                text.Append('\n');
                if (S("monitoredRelease") != null) text.Append("- Monitored edition: ").Append(S("monitoredRelease")).Append('\n');
                if (S("candidateRelease") != null) text.Append("- Download identified as: ").Append(S("candidateRelease")).Append('\n');
                var tracks = d.GetProperty("tracks");
                if (tracks.GetArrayLength() == 0) continue;
                text.Append("\n| # | Title | Would | Current | Incoming | Why | Lyric kept |\n|---|---|---|---|---|---|---|\n");
                foreach (var t in tracks.EnumerateArray())
                {
                    string T(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : string.Empty;
                    text.Append("| ").Append(t.GetProperty("Track").GetInt32()).Append(" | ").Append(T("Title").Replace("|", "/", StringComparison.Ordinal))
                        .Append(" | ").Append(T("Action")).Append(" | ").Append(T("Current")).Append(" | ").Append(T("Incoming")).Append(" | ").Append(T("Reason"))
                        .Append(" | ").Append(t.GetProperty("KeepsLyric").GetBoolean() ? "yes" : string.Empty).Append(" |\n");
                }
            }

            return text.ToString();
        }

        private static string Verdict(string state) => state switch
        {
            "WouldImport" => "Would change the library (a write-enabled build would import the tracks marked Upgrade/Add)",
            "NoImprovement" => "No improvement: every incoming track is equal or worse than what you have",
            "NeedsReview" => "Needs review: a write-enabled build would not import this automatically",
            "Blocked" => "Blocked",
            "LibraryUnavailable" => "Library not readable: nothing can be previewed",
            "SourceUnavailable" => "Download folder not reachable",
            "RefusedUnsafePath" => "Refused: unsafe or disallowed download path",
            _ => state,
        };

        /// <summary>Shareable diagnostics: no paths, names, titles, IDs or secrets; only counts, states, reason codes and quality names.</summary>
        private static string Redacted(JsonElement report)
        {
            var downloads = report.GetProperty("downloads").EnumerateArray().Select(d => new
            {
                id = Disk.Sha(Encoding.UTF8.GetBytes(d.GetProperty("downloadId").GetString() ?? string.Empty))[..12],
                state = d.GetProperty("state").GetString(),
                tracks = d.GetProperty("tracks").EnumerateArray().Select(t => new
                {
                    action = t.GetProperty("Action").GetString(), current = t.GetProperty("Current").GetString(), incoming = t.GetProperty("Incoming").GetString(),
                    reason = t.GetProperty("Reason").GetString(),
                }).ToList(),
            }).ToList();
            return JsonSerializer.Serialize(new
            {
                version = report.GetProperty("version").GetString(), counters = report.GetProperty("counters"),
                clientProblems = report.GetProperty("clientProblems").GetArrayLength(), downloads,
            }, Indented);
        }
    }
}
