// M25 (issue #62): the AUDIT-ONLY BETA build of the one Crescendarr plugin for an unmodified stable Lidarr.
//
// What it does: for completed downloads of download clients the operator EXPLICITLY selects, it asks stock Lidarr how it
// identifies the files and how the artist's quality profile ranks them, and previews per track what a selective import
// would do (Upgrade / Keep / Add / Review). It reports through stock surfaces only: Settings > Connect (configuration),
// System > Status (health), a local report under Lidarr's own AppData folder, and the read-only provider action "report".
//
// What it cannot do: this assembly contains no importer, recovery, salvage, approval, link/re-register or cleanup code and
// no writer command (those sources are not compiled into it; tools/audit-scan proves no Lidarr library-mutation API is
// referenced). Every file it writes goes through Disk, which in this build refuses any path outside its state folder.
// It never calls a download client's RemoveItem and never changes stock settings.
using System.Reflection;
using System.Text;
using System.Text.Json;
using Crescendarr.Core.Domain;
using Crescendarr.Core.Registry;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    /// <summary>Immutable build identity. There is no setting, environment variable or command that changes it.</summary>
    public static class CrescendarrBuild
    {
        public const bool AuditOnly = true;
        public const string Flavor = "audit-only beta";

        public static string Version => typeof(CrescendarrBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    }

    public sealed class CrescendarrPlugin : Plugin
    {
        // Lidarr's InstallPluginService matches IPlugin.Owner and IPlugin.Name to the GitHub repository
        // owner/name when tracking installed plugins and uninstalling them. They must match the URL exactly.
        // The friendly label lives on the Crescendarr Connect provider, not on this identity.
        public override string Name => "lidarr-plugin-crescendarr";
        public override string Owner => "GQuX";
        public override string GithubUrl => "https://github.com/GQuX/lidarr-plugin-crescendarr";
    }

    /// <summary>
    /// The only places this build may write: its own state folder under Lidarr's AppData folder. Set once; a second,
    /// different root is refused. Disk (compiled with CRESCENDARR_AUDIT_BUILD) calls RequireStatePath before every write.
    /// </summary>
    public static class AuditBarrier
    {
        public const string FolderName = "crescendarr-audit";
        private static string _stateRoot;

        public static string StateRoot => _stateRoot ?? throw new CrescendarrGuardException("audit-only build: the state folder is not initialised; nothing may be written");

        public static string Initialise(IAppFolderInfo appFolder, IRootFolderService rootFolders)
        {
            if (!OperatingSystem.IsLinux()) throw new CrescendarrGuardException("the audit-only beta supports Lidarr on Linux only");
            var root = Path.GetFullPath(Path.Combine(appFolder.AppDataFolder, FolderName)).TrimEnd('/');
            var prior = Interlocked.CompareExchange(ref _stateRoot, root, null);
            if (prior != null && prior != root) throw new CrescendarrGuardException("audit-only build: the state folder changed while running; refusing");
            foreach (var folder in rootFolders.All())
            {
                var library = Path.GetFullPath(folder.Path).TrimEnd('/');
                if (root == library || Disk.IsUnder(library, root) || Disk.IsUnder(root, library))
                    throw new CrescendarrGuardException("audit-only build: Lidarr's AppData folder overlaps a library root folder; refusing to write diagnostics there");
            }

            if (Disk.HasLinkInChain(root, "/")) throw new CrescendarrGuardException("audit-only build: the state folder path traverses a symbolic link");
            return root;
        }

        public static void RequireStatePath(string path)
        {
            var full = Path.GetFullPath(path);
            if (!Disk.IsUnder(StateRoot, full)) throw new CrescendarrGuardException("audit-only build: refusing to write outside its state folder: " + full);
            if (Disk.HasLinkInChain(full, StateRoot)) throw new CrescendarrGuardException("audit-only build: refusing to write through a symbolic link: " + full);
        }
    }

    public enum CrescendarrAuditMode
    {
        /// <summary>Default after install: nothing is observed.</summary>
        Disabled = 0,

        /// <summary>Observe the selected clients' completed downloads and preview; never write the library.</summary>
        AuditOnly = 1,
    }

    public sealed class CrescendarrAuditSettingsValidator : AbstractValidator<CrescendarrAuditSettings>
    {
        public CrescendarrAuditSettingsValidator()
        {
            RuleFor(c => c.Mode).InclusiveBetween(0, 1).WithMessage("This build is audit-only: choose Disabled or Audit only.");
            RuleFor(c => c.CycleMinutes).InclusiveBetween(1, 1440);
            RuleFor(c => c.ObservedClients).Must(c => c != null && c.Any()).When(c => c.Mode == (int)CrescendarrAuditMode.AuditOnly)
                .WithMessage("Select at least one download client to observe.");
            RuleFor(c => c.AllowedDownloadFolders).Must(v => CrescendarrAuditSettings.ParseFolders(v).Count > 0 && CrescendarrAuditSettings.ParseFolders(v).All(Path.IsPathFullyQualified))
                .When(c => c.Mode == (int)CrescendarrAuditMode.AuditOnly)
                .WithMessage("Enter one or more absolute folders (separated by ';') that the selected clients' completed downloads live in, as Lidarr sees them.");
        }
    }

    public sealed class CrescendarrAuditSettings : IProviderConfig
    {
        private static readonly CrescendarrAuditSettingsValidator Validator = new CrescendarrAuditSettingsValidator();

        public CrescendarrAuditSettings()
        {
            Mode = (int)CrescendarrAuditMode.Disabled;
            CycleMinutes = 5;
            ObservedClients = Array.Empty<int>();
            AllowedDownloadFolders = string.Empty;
        }

        [FieldDefinition(0, Label = "Mode", Type = FieldType.Select, SelectOptions = typeof(CrescendarrAuditMode),
            HelpText = "Disabled (default) observes nothing. Audit only previews what Crescendarr would do. This build can never import, move, delete or relink anything.")]
        public int Mode { get; set; }

        [FieldDefinition(1, Label = "Download clients to observe", Type = FieldType.Select, SelectOptionsProviderAction = "downloadClients",
            HelpText = "Only completed downloads of the clients selected here are read. Nothing is ever removed from the client.")]
        public IEnumerable<int> ObservedClients { get; set; }

        [FieldDefinition(2, Label = "Allowed download folders", Type = FieldType.Textbox,
            HelpText = "Absolute folder(s), separated by ';', where those clients' completed downloads appear to Lidarr (after remote path mappings). Anything else is refused.")]
        public string AllowedDownloadFolders { get; set; }

        [FieldDefinition(3, Label = "Audit interval (minutes)", Type = FieldType.Number, HelpText = "Minimum time between automatic audit cycles (1-1440).")]
        public int CycleMinutes { get; set; }

        public static List<string> ParseFolders(string value) => (value ?? string.Empty)
            .Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        public NzbDroneValidationResult Validate() => new NzbDroneValidationResult(Validator.Validate(this));
    }

    /// <summary>Settings > Connect > Crescendarr. It never sends anything anywhere and never writes music.</summary>
    public sealed class CrescendarrAuditConnect : NotificationBase<CrescendarrAuditSettings>
    {
        private readonly IDownloadClientFactory _clients;
        private readonly IAppFolderInfo _appFolder;

        public CrescendarrAuditConnect(IDownloadClientFactory clients, IAppFolderInfo appFolder)
        {
            _clients = clients;
            _appFolder = appFolder;
        }

        public override string Name => "Crescendarr (audit-only beta, read-only)";

        public override string Link => "https://github.com/GQuX/lidarr-plugin-crescendarr";

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();
            if (!OperatingSystem.IsLinux()) failures.Add(new ValidationFailure("Mode", "The audit-only beta supports Lidarr on Linux only."));
            var known = _clients.All().Select(d => d.Id).ToHashSet();
            foreach (var id in Settings.ObservedClients ?? Array.Empty<int>())
            {
                if (!known.Contains(id)) failures.Add(new ValidationFailure("ObservedClients", $"Download client {id} no longer exists."));
            }

            foreach (var folder in CrescendarrAuditSettings.ParseFolders(Settings.AllowedDownloadFolders))
            {
                if (!Path.IsPathFullyQualified(folder) || Disk.Probe(folder) != Disk.PathState.Directory)
                    failures.Add(new ValidationFailure("AllowedDownloadFolders", $"Not a reachable absolute folder: {folder}"));
            }

            return new ValidationResult(failures);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "downloadClients")
            {
                var options = _clients.All().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { value = d.Id, name = $"{d.Name} ({d.Implementation})" }).ToList();
                return new { options };
            }

            if (action == "report")
            {
                var report = Path.Combine(_appFolder.AppDataFolder, AuditBarrier.FolderName, "report.json");
                return File.Exists(report) ? JsonDocument.Parse(File.ReadAllText(report)).RootElement.Clone() : (object)new { downloads = Array.Empty<object>() };
            }

            return base.RequestAction(action, query);
        }
    }

    public static class CrescendarrAuditConfiguration
    {
        public static CrescendarrAuditSettings Read(INotificationFactory notifications, out string problem)
        {
            problem = null;
            var definitions = notifications.All().Where(d => d.Implementation == nameof(CrescendarrAuditConnect)).ToList();
            if (definitions.Count == 0)
            {
                problem = "not configured (add Settings > Connect > Crescendarr)";
                return null;
            }

            if (definitions.Count > 1)
            {
                problem = "more than one Crescendarr Connect definition; refusing to guess";
                return null;
            }

            // Fail closed: a definition edited outside the UI (for example Mode=GuardedAuto written into the database) is
            // invalid in this build and disables observation; it can never enable a write.
            var settings = definitions[0].Settings as CrescendarrAuditSettings;
            if (settings == null || !settings.Validate().IsValid)
            {
                problem = "invalid Crescendarr settings for the audit-only build (only Disabled and Audit only exist)";
                return null;
            }

            return settings;
        }
    }

    /// <summary>The only Crescendarr command in this build. It reads; it writes nothing but its state folder.</summary>
    public sealed class CrescendarrAuditCycleCommand : Command
    {
        public string Reason { get; set; } = "manual";
        public override bool RequiresDiskAccess => false;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>Pushes a queued audit cycle when one is due. Never writes.</summary>
    public sealed class CrescendarrAuditScheduler : IHandle<CommandExecutedEvent>, IHandle<ApplicationStartedEvent>
    {
        private static readonly object Gate = new object();
        private readonly IManageCommandQueue _queue;
        private readonly INotificationFactory _notifications;
        private readonly Logger _logger;
        private DateTime _lastPush = DateTime.MinValue;

        public CrescendarrAuditScheduler(IManageCommandQueue queue, INotificationFactory notifications, Logger logger)
        {
            _queue = queue;
            _notifications = notifications;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            AuditLog.Emit(_logger, $"AUDIT_BUILD_READY version={CrescendarrBuild.Version} flavor=\"{CrescendarrBuild.Flavor}\" libraryWrites=impossible");
            Consider("startup", force: true);
        }

        public void Handle(CommandExecutedEvent message)
        {
            if (message.Command?.Name == "RefreshMonitoredDownloads") Consider("schedule", force: false);
        }

        private void Consider(string reason, bool force)
        {
            try
            {
                var settings = CrescendarrAuditConfiguration.Read(_notifications, out _);
                if (settings == null || settings.Mode != (int)CrescendarrAuditMode.AuditOnly) return;
                lock (Gate)
                {
                    if (!force && DateTime.UtcNow - _lastPush < TimeSpan.FromMinutes(settings.CycleMinutes)) return;
                    if (_queue.All().Any(c => c.Name == "CrescendarrAuditCycle" && c.Status is CommandStatus.Queued or CommandStatus.Started)) return;
                    _lastPush = DateTime.UtcNow;
                }

                _queue.Push(new CrescendarrAuditCycleCommand { Reason = reason });
            }
            catch (Exception error)
            {
                AuditLog.Emit(_logger, "AUDIT_SCHEDULE_FAILED " + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    public static class AuditLog
    {
        public static void Emit(Logger logger, string message)
        {
            var line = "CRESCENDARR " + message;
            Console.WriteLine(line);
            logger.Info(line);
        }
    }

    /// <summary>System > Status: configuration problems, unreachable clients, and every would-be library change (yellow).</summary>
    public sealed class CrescendarrAuditHealthCheck : HealthCheckBase
    {
        private readonly IAppFolderInfo _appFolder;
        private readonly INotificationFactory _notifications;

        public CrescendarrAuditHealthCheck(IAppFolderInfo appFolder, INotificationFactory notifications, ILocalizationService localizationService)
            : base(localizationService)
        {
            _appFolder = appFolder;
            _notifications = notifications;
        }

        public override NzbDrone.Core.HealthCheck.HealthCheck Check()
        {
            var definitions = _notifications.All().Count(d => d.Implementation == nameof(CrescendarrAuditConnect));
            if (definitions == 0) return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
            var settings = CrescendarrAuditConfiguration.Read(_notifications, out var problem);
            if (settings == null)
                return new NzbDrone.Core.HealthCheck.HealthCheck(GetType(), HealthCheckResult.Error, HealthCheckReason.ServerNotification,
                    "Crescendarr audit-only beta is not observing anything: " + problem);
            if (settings.Mode != (int)CrescendarrAuditMode.AuditOnly) return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
            var status = Path.Combine(_appFolder.AppDataFolder, AuditBarrier.FolderName, "status.json");
            if (!File.Exists(status)) return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
            var root = JsonDocument.Parse(File.ReadAllText(status)).RootElement;
            var counters = root.GetProperty("counters");
            int Count(string name) => counters.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
            var clientProblems = root.TryGetProperty("clientProblems", out var cp) ? cp.GetArrayLength() : 0;
            var parts = new List<string>();
            if (Count("wouldChange") > 0)
                parts.Add($"{Count("wouldChange")} completed download(s) WOULD change library tracks ({Count("wouldUpgrade")} upgrade(s), {Count("wouldAdd")} addition(s); " +
                    $"{Count("wouldKeep")} better existing file(s) would be kept)");
            if (Count("review") > 0) parts.Add($"{Count("review")} download(s) would need review");
            if (Count("refused") + Count("unavailable") > 0) parts.Add($"{Count("refused") + Count("unavailable")} download(s) could not be inspected safely");
            if (clientProblems > 0) parts.Add($"{clientProblems} selected download client(s) could not be read");
            if (parts.Count == 0) return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
            return new NzbDrone.Core.HealthCheck.HealthCheck(GetType(), HealthCheckResult.Warning, HealthCheckReason.ServerNotification,
                "Crescendarr AUDIT ONLY (nothing was imported or changed): " + string.Join("; ", parts) +
                $". Report: {Path.Combine(_appFolder.AppDataFolder, AuditBarrier.FolderName, "report.md")}");
        }
    }
}
