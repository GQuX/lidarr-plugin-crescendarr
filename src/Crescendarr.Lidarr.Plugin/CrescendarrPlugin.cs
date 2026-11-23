// EXPERIMENTAL M17 vertical slice (issue #46). Loaded by an UNMODIFIED stock Lidarr v3.1.6.5079 daemon in a
// disposable CI host only. Every entry point is inert unless the disposable opt-in, CI markers and a marked
// root are present, and every write is confined to that root. Production write-enabled is NO-GO.
using System.Text;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Blackhole;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public sealed class CrescendarrPlugin : Plugin
    {
        public override string Name => "Crescendarr (EXPERIMENTAL disposable-host vertical slice)";
        public override string Owner => "Crescendarr project";
        public override string GithubUrl => string.Empty;
    }

    /// <summary>Read-only: observe completed items of the configured synthetic blackhole and plan each one.</summary>
    public sealed class CrescendarrPlanCommand : Command
    {
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>The ONLY writer. Re-plans inside the disk-access command and executes only the approved plan.</summary>
    public sealed class CrescendarrImportCommand : Command
    {
        public string DownloadId { get; set; } = string.Empty;
        public string PlanId { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>Restart recovery: drives every Active journal through the M13 reconciler. Never trusts the Commands table.</summary>
    public sealed class CrescendarrRecoverCommand : Command
    {

        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>M19 operator: read-only status of every operation (writes crescendarr/status.json and logs it).</summary>
    public sealed class CrescendarrOperatorStatusCommand : Command
    {
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>
    /// M19 operator dry run: compute whether an action is allowed against a FRESH host snapshot and, if so, a token bound
    /// to the operation ID, journal generation, action and snapshot digest. Changes nothing.
    /// Actions: register-placed, link-placed-row, salvage-lyrics, resolve.
    /// </summary>
    public sealed class CrescendarrProposeCommand : Command
    {
        public string OperationId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>M19 operator approval: re-proposes from a fresh snapshot; applies only when the typed token still matches.</summary>
    public sealed class CrescendarrApproveCommand : Command
    {
        public string OperationId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>All guard conditions, enforced at startup AND at the start of every command.</summary>
    public static class CrescendarrGuard
    {
        public const string OptInVariable = "CRESCENDARR_M17_OPT_IN";
        public const string RootVariable = "CRESCENDARR_M17_ROOT";
        public const string OptInValue = "DISPOSABLE_SYNTHETIC_ONLY";
        public const string ClientName = "Crescendarr Synthetic Blackhole";

        /// <summary>M21: only stock download clients whose name starts with this prefix are ever observed.</summary>
        public const string ClientNamePrefix = "Crescendarr Synthetic";
        private const string MarkerName = ".crescendarr-disposable-host";
        private const string MarkerText = "CRESCENDARR_DISPOSABLE_HOST_V1";

        public static bool TryRoot(out string root, out string why)
        {
            root = string.Empty;
            why = string.Empty;
            if (Environment.GetEnvironmentVariable(OptInVariable) != OptInValue) { why = "no disposable opt-in"; return false; }
            if (Environment.GetEnvironmentVariable("CI") != "true" || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") { why = "not an ephemeral CI runner"; return false; }
            if (!OperatingSystem.IsLinux()) { why = "only the Linux disposable host is supported"; return false; }
            var raw = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
            if (!Path.IsPathFullyQualified(raw)) { why = "root is not absolute"; return false; }
            var full = Path.GetFullPath(raw).TrimEnd('/');
            var marker = Path.Combine(full, MarkerName);
            if (full.Length < 8 || !File.Exists(marker) || File.ReadAllText(marker).Trim() != MarkerText) { why = "marked disposable root missing"; return false; }
            if (Disk.HasLinkInChain(full, "/")) { why = "root traverses a symbolic link"; return false; }
            root = full;
            return true;
        }

        public static string RequireRoot()
        {
            if (!TryRoot(out var root, out var why)) throw new CrescendarrGuardException("Crescendarr is inert: " + why);
            return root;
        }

        /// <summary>Execution-time host checks: every configured location must live in the disposable root.</summary>
        public static void RequireHostConfined(string root, IRootFolderService rootFolders, IConfigService config)
        {
            foreach (var folder in rootFolders.All())
            {
                if (!Disk.IsUnder(root, Path.GetFullPath(folder.Path))) throw new CrescendarrGuardException("A root folder is outside the disposable root.");
            }

            if (!string.IsNullOrEmpty(config.RecycleBin) && !Disk.IsUnder(root, Path.GetFullPath(config.RecycleBin)))
                throw new CrescendarrGuardException("Recycle bin is outside the disposable root.");
            if (config.EnableCompletedDownloadHandling)
                throw new CrescendarrGuardException("Completed download handling must be OFF so stock never races Crescendarr.");
        }

        public static void Emit(Logger logger, string message)
        {
            var line = "CRESCENDARR " + message;
            Console.WriteLine(line);
            logger.Info(line);
        }
    }

    /// <summary>Observe-only at startup. It never writes, never pushes commands.</summary>
    public sealed class CrescendarrStartup : IHandle<ApplicationStartedEvent>
    {
        private readonly IDownloadClientFactory _clients;
        private readonly IConfigService _config;
        private readonly Logger _logger;

        public CrescendarrStartup(IDownloadClientFactory clients, IConfigService config, Logger logger)
        {
            _clients = clients;
            _config = config;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            if (!CrescendarrGuard.TryRoot(out var root, out var why))
            {
                CrescendarrGuard.Emit(_logger, $"INERT reason=\"{why}\"");
                return;
            }

            var clients = _clients.DownloadHandlingEnabled().Count(c => c is UsenetBlackhole && c.Definition.Name == CrescendarrGuard.ClientName);
            CrescendarrGuard.Emit(_logger, $"READY root={root} cdh={_config.EnableCompletedDownloadHandling} syntheticClients={clients} " +
                $"assembly={typeof(CrescendarrStartup).Assembly.GetName().Name}");
        }
    }
}
