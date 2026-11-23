// WRITER (disposable CI) build only: discovery of the synthetic "Crescendarr Synthetic ..." clients inside the marked
// disposable root. The audit-only build does not compile this file; it selects clients explicitly instead.
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Blackhole;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public sealed partial class HostAdapter
    {
        /// <summary>
        /// Completed items of every download-handling-enabled stock client explicitly named "Crescendarr Synthetic ..."
        /// (M17 blackhole; M21 any client type, e.g. SABnzbd), read ONLY through Lidarr's own client factory and client
        /// implementation (no direct downloader API). Every reported output folder must lie in the disposable downloads
        /// folder and contain no link. Unrecognized client names are never touched.
        /// </summary>
        public List<(DownloadClientItem Item, int ClientId, string WatchFolder)> CompletedItems(string root, out List<string> notes)
        {
            var observed = ObserveClients(root);
            notes = observed.Notes;
            return observed.Completed;
        }

        /// <summary>
        /// M22: per-client and per-item isolation. An unavailable or failing client, or one item reporting an output folder
        /// outside the disposable downloads folder (or through a link), is recorded with an actionable reason and SKIPPED;
        /// it never aborts observation of the other clients and items, and is never treated as settlement.
        /// </summary>
        public ClientObservation ObserveClients(string root)
        {
            var result = new ClientObservation();
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

                List<DownloadClientItem> items;
                try
                {
                    items = client.GetItems().ToList();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    result.ClientErrors.Add($"{client.Definition.Name} ({client.Definition.Implementation}) unavailable: {error.GetType().Name}: {error.Message}");
                    continue;
                }

                foreach (var item in items)
                {
                    result.Notes.Add($"{client.Definition.Implementation}:{item.DownloadId}:{item.Status}");
                    if (item.Status != DownloadItemStatus.Completed || item.OutputPath.IsEmpty) continue;
                    var output = Path.GetFullPath(item.OutputPath.FullPath).TrimEnd('/');
                    if (!Disk.IsUnder(confine, output))
                        result.Refused.Add((item, client.Definition.Id, output, $"completed folder is outside the disposable downloads folder ({confine}); check the client's category folder or a remote path mapping"));
                    else if (Disk.HasLinkInChain(output, root))
                        result.Refused.Add((item, client.Definition.Id, output, "completed folder is reached through a symbolic link"));
                    else
                        result.Completed.Add((item, client.Definition.Id, confine));
                }
            }

            return result;
        }
    }
}
