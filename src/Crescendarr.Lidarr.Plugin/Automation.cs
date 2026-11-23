// M20 (issue #51): unattended operation inside ONE plugin on an unmodified stock Lidarr.
//
// Configuration: a stock "Connect" (notification) provider. Lidarr renders its [FieldDefinition] settings in the normal
// Settings > Connect UI and persists them in its own Notifications table; no UI injection or fork is needed.
// Trigger: stock schedules RefreshMonitoredDownloads every minute and publishes CommandExecutedEvent when it finishes.
// The plugin's handler only PUSHES a queued CrescendarrCycle command (never writes); ApplicationStartedEvent pushes one
// too, so recovery runs first after every restart. The cycle itself is one RequiresDiskAccess command.
// Review: a stock health check (System > Status, /api/v1/health) lists operations that need an operator.
// Download handling: a plugin-local durable registry (crescendarr/downloads) records what Crescendarr did with each
// completed download. It never calls the client's RemoveItem, never deletes sources and never claims stock settlement.
using System.Text;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public enum CrescendarrMode
    {
        /// <summary>Nothing runs automatically.</summary>
        Disabled = 0,

        /// <summary>Default: observe and plan only; no automatic writes (not even recovery). Imports need an explicit approval.</summary>
        AuditOnly = 1,

        /// <summary>Recovery first, then import ONLY fresh Ready plans; everything else goes to review.</summary>
        GuardedAuto = 2,
    }

    public enum CrescendarrRetention
    {
        /// <summary>Keep every journal, backup and registry entry (the only tested policy).</summary>
        KeepEverything = 0,
    }

    public enum CrescendarrQualityPolicy
    {
        /// <summary>Upgrade only when the artist's stock quality profile says the incoming file is strictly preferred.</summary>
        ArtistProfileStrictUpgrade = 0,
    }

    public sealed class CrescendarrSettingsValidator : AbstractValidator<CrescendarrSettings>
    {
        public CrescendarrSettingsValidator()
        {
            RuleFor(c => c.Mode).InclusiveBetween(0, 2);
            RuleFor(c => c.CycleMinutes).InclusiveBetween(1, 1440);
            RuleFor(c => c.MaxImportsPerCycle).InclusiveBetween(1, 10);
            RuleFor(c => c.Retention).Equal(0).WithMessage("Only 'Keep everything' is implemented and tested.");
            RuleFor(c => c.QualityPolicy).Equal(0);
        }
    }

    public sealed class CrescendarrSettings : IProviderConfig
    {
        private static readonly CrescendarrSettingsValidator Validator = new CrescendarrSettingsValidator();

        public CrescendarrSettings()
        {
            Mode = (int)CrescendarrMode.AuditOnly;
            CycleMinutes = 5;
            MaxImportsPerCycle = 1;
        }

        [FieldDefinition(0, Label = "Mode", Type = FieldType.Select, SelectOptions = typeof(CrescendarrMode),
            HelpText = "Audit only (default) plans without writing. Guarded auto recovers interrupted operations first, then imports only fresh Ready plans.")]
        public int Mode { get; set; }

        [FieldDefinition(1, Label = "Cycle interval (minutes)", Type = FieldType.Number, HelpText = "Minimum time between automatic cycles (1-1440).")]
        public int CycleMinutes { get; set; }

        [FieldDefinition(2, Label = "Max imports per cycle", Type = FieldType.Number, HelpText = "Upper bound on new operations started by one cycle (1-10).")]
        public int MaxImportsPerCycle { get; set; }

        [FieldDefinition(3, Label = "Quality policy", Type = FieldType.Select, SelectOptions = typeof(CrescendarrQualityPolicy),
            HelpText = "Per-track upgrades follow the artist's stock quality profile; equal or lower quality is never imported.")]
        public int QualityPolicy { get; set; }

        [FieldDefinition(4, Label = "Retention", Type = FieldType.Select, SelectOptions = typeof(CrescendarrRetention),
            HelpText = "Journals, lyric backups and the download registry are kept. No automatic cleanup exists yet.")]
        public int Retention { get; set; }

        public NzbDroneValidationResult Validate() => new NzbDroneValidationResult(Validator.Validate(this));
    }

    /// <summary>The configuration surface: Settings > Connect > Crescendarr. It never writes music and sends nothing anywhere.</summary>
    public sealed class CrescendarrConnect : NotificationBase<CrescendarrSettings>
    {
        public override string Name => "Crescendarr (EXPERIMENTAL, disposable hosts only)";

        public override string Link => "https://github.com/GQuX/lidarr-plugin-crescendarr";

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();
            if (!CrescendarrGuard.TryRoot(out _, out var why)) failures.Add(new ValidationFailure("Mode", "Crescendarr is inert on this host: " + why));
            return new ValidationResult(failures);
        }

        /// <summary>No-op: declared so the provider has a trigger; Crescendarr's own health check reports its state.</summary>
        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "operations" && CrescendarrGuard.TryRoot(out var root, out _))
            {
                var ops = Path.Combine(root, "crescendarr", "ops");
                var options = Directory.Exists(ops)
                    ? Directory.GetDirectories(ops).OrderBy(d => d, StringComparer.Ordinal)
                        .Select(d => new { value = Path.GetFileName(d), name = Path.GetFileName(d) + " (" + CrescendarrImportExecutor.OperationState(d) + ")" }).ToList()
                    : new[] { new { value = string.Empty, name = "none" } }.Take(0).ToList();
                return new { options };
            }

            return base.RequestAction(action, query);
        }
    }

    /// <summary>Reads the single Crescendarr Connect definition. Several definitions are a configuration error (fail closed).</summary>
    public static class CrescendarrConfiguration
    {
        public static CrescendarrSettings Read(INotificationFactory notifications, out string problem)
        {
            problem = null;
            var definitions = notifications.All().Where(d => d.Implementation == nameof(CrescendarrConnect)).ToList();
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

            var settings = definitions[0].Settings as CrescendarrSettings;
            if (settings == null || !settings.Validate().IsValid)
            {
                problem = "invalid Crescendarr settings";
                return null;
            }

            return settings;
        }
    }

    /// <summary>One automatic cycle. Pushed only by the scheduler or by the operator; it is the ONLY automatic writer.</summary>
    public sealed class CrescendarrCycleCommand : Command
    {
        public string Reason { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>Observe-only trigger: never writes; only pushes a queued CrescendarrCycle when one is due and none is pending.</summary>
    public sealed class CrescendarrScheduler : IHandle<CommandExecutedEvent>, IHandle<ApplicationStartedEvent>
    {
        private static readonly object Gate = new object();
        private readonly IManageCommandQueue _queue;
        private readonly INotificationFactory _notifications;
        private readonly Logger _logger;
        private DateTime _lastPush = DateTime.MinValue;

        public CrescendarrScheduler(IManageCommandQueue queue, INotificationFactory notifications, Logger logger)
        {
            _queue = queue;
            _notifications = notifications;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message) => Consider("startup", force: true);

        public void Handle(CommandExecutedEvent message)
        {
            if (message.Command?.Name == "RefreshMonitoredDownloads") Consider("schedule", force: false);
        }

        private void Consider(string reason, bool force)
        {
            try
            {
                if (!CrescendarrGuard.TryRoot(out _, out _)) return;
                var settings = CrescendarrConfiguration.Read(_notifications, out _);
                if (settings == null || settings.Mode == (int)CrescendarrMode.Disabled) return;
                lock (Gate)
                {
                    if (!force && DateTime.UtcNow - _lastPush < TimeSpan.FromMinutes(settings.CycleMinutes)) return;
                    // Duplicate scheduling: never more than one queued or running cycle (the stock queue state is the source of truth).
                    if (_queue.All().Any(c => c.Name == "CrescendarrCycle" && c.Status is CommandStatus.Queued or CommandStatus.Started)) return;
                    _lastPush = DateTime.UtcNow;
                }

                _queue.Push(new CrescendarrCycleCommand { Reason = reason });
                CrescendarrGuard.Emit(_logger, $"CYCLE_SCHEDULED reason={reason} mode={(CrescendarrMode)settings.Mode}");
            }
            catch (Exception error)
            {
                CrescendarrGuard.Emit(_logger, "CYCLE_SCHEDULE_FAILED " + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    /// <summary>Native review surface: System > Status lists operations that need an operator (read-only).</summary>
    public sealed class CrescendarrHealthCheck : HealthCheckBase
    {
        public CrescendarrHealthCheck(ILocalizationService localizationService)
            : base(localizationService)
        {
        }

        public override NzbDrone.Core.HealthCheck.HealthCheck Check()
        {
            if (!CrescendarrGuard.TryRoot(out var root, out _)) return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
            var ops = Path.Combine(root, "crescendarr", "ops");
            var blocked = !Directory.Exists(ops) ? new List<string>() : Directory.GetDirectories(ops)
                .Select(d => (Id: Path.GetFileName(d), State: CrescendarrImportExecutor.OperationState(d)))
                .Where(o => o.State is "RequiresReview" or "OperatorApprovalRequired" or "Active" or "StorageUnavailable")
                .Select(o => $"{o.Id} ({o.State})").ToList();
            if (blocked.Count > 0)
                return new NzbDrone.Core.HealthCheck.HealthCheck(GetType(), HealthCheckResult.Error, HealthCheckReason.ServerNotification,
                    $"Crescendarr: {blocked.Count} operation(s) need an operator: {string.Join(", ", blocked)}. Use CrescendarrOperatorStatus, then CrescendarrPropose/CrescendarrApprove.");
            var status = Path.Combine(root, "crescendarr", "status-cycle.json");
            if (File.Exists(status))
            {
                var awaiting = JsonDocument.Parse(File.ReadAllText(status)).RootElement.GetProperty("counters").GetProperty("awaitingApproval").GetInt32();
                if (awaiting > 0)
                    return new NzbDrone.Core.HealthCheck.HealthCheck(GetType(), HealthCheckResult.Warning, HealthCheckReason.ServerNotification,
                        $"Crescendarr (audit only): {awaiting} completed download(s) have a Ready plan awaiting approval (CrescendarrImport with the plan ID in the download registry).");
            }

            return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
        }
    }

    public sealed partial class CrescendarrImportExecutor : IExecute<CrescendarrCycleCommand>
    {
        private static readonly string[] TerminalDownloadStates = { "Imported", "NoImprovement", "NeedsReview", "Blocked", "RequiresReview", "Refused" };

        public void Execute(CrescendarrCycleCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var counters = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["observed"] = 0, ["ready"] = 0, ["awaitingApproval"] = 0, ["imported"] = 0, ["noImprovement"] = 0, ["review"] = 0,
                ["alreadyHandled"] = 0, ["blockedAlbum"] = 0, ["recovered"] = 0, ["pendingRecovery"] = 0, ["refused"] = 0, ["errors"] = 0,
            };
            var settings = CrescendarrConfiguration.Read(_notifications, out var problem);
            var mode = settings == null ? CrescendarrMode.Disabled : (CrescendarrMode)settings.Mode;
            CrescendarrGuard.Emit(_logger, $"CYCLE_START reason={message.Reason} mode={mode} config=\"{problem}\"");
            if (settings == null || mode == CrescendarrMode.Disabled)
            {
                WriteCycleStatus(root, mode, problem ?? "disabled", counters);
                return;
            }

            // 1. Recovery first: every journaled, non-terminal operation before any new work (GuardedAuto only).
            var ops = Path.Combine(root, "crescendarr", "ops");
            if (Directory.Exists(ops))
            {
                foreach (var opRoot in Directory.GetDirectories(ops).OrderBy(d => d, StringComparer.Ordinal).Where(d => OperationState(d) is "Active" or "Unpublished" or "StorageUnavailable"))
                {
                    if (mode != CrescendarrMode.GuardedAuto)
                    {
                        counters["pendingRecovery"]++;
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

                    var ok = Finish(context, outcome);
                    counters[ok ? "recovered" : "review"]++;
                    CrescendarrGuard.Emit(_logger, $"CYCLE_RECOVERY op={context.OperationId} state={OperationState(opRoot)}");
                }
            }

            // 2. Observe and plan every completed download; import only fresh Ready plans (GuardedAuto).
            var imports = 0;
            var clients = _adapter.ObserveClients(root);
            foreach (var clientProblem in clients.ClientErrors)
            {
                // Visible, never settlement: an unreachable client leaves every registry entry exactly as it was.
                counters["errors"]++;
                CrescendarrGuard.Emit(_logger, "CYCLE_CLIENT_ERROR " + clientProblem);
            }

            foreach (var (item, clientId, folder, reason) in clients.Refused)
            {
                counters["refused"]++;
                var registryPath = Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "downloads", Disk.Sha(Encoding.UTF8.GetBytes(folder))[..16] + ".json"), "download registry");
                Disk.WriteDurable(registryPath, JsonSerializer.Serialize(new
                {
                    downloadId = item.DownloadId, clientDefinitionId = clientId, folder, inventoryDigest = (string)null, state = "RefusedUnsafePath", reason,
                    pluginHandled = false, lidarrImported = false, downloaderRemoved = false, sourceRetained = Directory.Exists(folder), lastSeen = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }), replace: true);
                CrescendarrGuard.Emit(_logger, $"CYCLE_REFUSED_ITEM download=\"{item.DownloadId}\" folder=\"{folder}\" reason=\"{reason}\"");
            }

            foreach (var (item, clientId, watch) in clients.Completed)
            {
                counters["observed"]++;
                try
                {
                    var observation = _adapter.Observe(item, clientId, watch);
                    var digest = Disk.Sha(Encoding.UTF8.GetBytes(string.Join("\n", observation.IncomingSha.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value))));
                    var registryPath = Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "downloads", Disk.Sha(Encoding.UTF8.GetBytes(observation.Folder))[..16] + ".json"), "download registry");
                    var previous = File.Exists(registryPath) ? JsonDocument.Parse(File.ReadAllText(registryPath)).RootElement.Clone() : (JsonElement?)null;
                    if (previous is { } p && p.GetProperty("inventoryDigest").GetString() == digest && TerminalDownloadStates.Contains(p.GetProperty("state").GetString()))
                    {
                        // Duplicate processing prevention: same folder, same bytes, already handled. Nothing is re-imported.
                        counters["alreadyHandled"]++;
                        CrescendarrGuard.Emit(_logger, $"CYCLE_ALREADY_HANDLED download=\"{item.DownloadId}\" state={p.GetProperty("state").GetString()}");
                        continue;
                    }

                    var disposition = observation.Blocked != null ? "Blocked" : observation.Plan?.Disposition.ToString() ?? "Blocked";
                    var state = disposition switch
                    {
                        "Ready" => mode == CrescendarrMode.GuardedAuto ? "Ready" : "AwaitingApproval",
                        "NoImprovement" => "NoImprovement",
                        "NeedsReview" => "NeedsReview",
                        _ => "Blocked",
                    };
                    string operationOutcome = null;
                    if (state == "Ready")
                    {
                        counters["ready"]++;
                        if (observation.Album != null && UnfinishedOperations(root, observation.Album.Id).Count > 0)
                        {
                            state = "AlbumBlocked";
                            counters["blockedAlbum"]++;
                        }
                        else if (imports < settings.MaxImportsPerCycle)
                        {
                            imports++;
                            operationOutcome = RunImport(item.DownloadId, observation.Plan.PlanId);
                            state = operationOutcome == Completed ? "Imported" : operationOutcome;
                        }
                        else
                        {
                            state = "Deferred";
                        }
                    }

                    if (state == "AwaitingApproval") counters["awaitingApproval"]++;
                    if (state == "Imported") counters["imported"]++;
                    if (state == "NoImprovement") counters["noImprovement"]++;
                    if (state is "NeedsReview" or "Blocked" or "RequiresReview" or "OperatorApprovalRequired" or "Refused") counters["review"]++;
                    Disk.WriteDurable(registryPath, JsonSerializer.Serialize(new
                    {
                        downloadId = item.DownloadId, clientDefinitionId = clientId, folder = observation.Folder, inventoryDigest = digest, state,
                        planId = observation.Plan?.PlanId, disposition, blocked = observation.Blocked,
                        operationId = observation.Plan == null ? null : OperationId(observation.Plan.PlanId),
                        pluginHandled = state is "Imported" or "NoImprovement" or "NeedsReview" or "Blocked" or "RequiresReview" or "Refused",
                        lidarrImported = false, downloaderRemoved = false, sourceRetained = Directory.Exists(observation.Folder),
                        settlement = "Plugin-local record only. Crescendarr never calls the client's RemoveItem and never marks the stock queue imported (CDH OFF).",
                        firstSeen = previous?.TryGetProperty("firstSeen", out var first) == true ? first.GetDateTime() : DateTime.UtcNow, lastSeen = DateTime.UtcNow
                    }, new JsonSerializerOptions { WriteIndented = true }), replace: true);
                    CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{item.DownloadId}\" disposition={disposition} state={state} plan={observation.Plan?.PlanId} reason=\"{observation.Blocked}\"");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    counters["errors"]++;
                    CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD_ERROR download=\"{item.DownloadId}\" {error.GetType().Name}: {error.Message}");
                }
            }

            WriteCycleStatus(root, mode, null, counters);
            CrescendarrGuard.Emit(_logger, "CYCLE_SUMMARY " + string.Join(" ", counters.Select(c => c.Key + "=" + c.Value)));
            if (counters["errors"] > 0) throw new CommandFailedException($"Crescendarr cycle finished with {counters["errors"]} error(s); see the download registry.");
        }

        private static void WriteCycleStatus(string root, CrescendarrMode mode, string problem, Dictionary<string, int> counters)
        {
            Disk.WriteDurable(Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "status-cycle.json"), "cycle status"), JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, mode = mode.ToString(), problem, counters
            }, new JsonSerializerOptions { WriteIndented = true }), replace: true);
        }
    }
}
