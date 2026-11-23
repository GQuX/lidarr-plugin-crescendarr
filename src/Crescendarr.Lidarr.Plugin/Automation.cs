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
using Crescendarr.Core.Registry;
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
                var counters = JsonDocument.Parse(File.ReadAllText(status)).RootElement.GetProperty("counters");
                int Count(string name) => counters.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
                var registryReview = Count("registryReview");
                if (registryReview > 0)
                    return new NzbDrone.Core.HealthCheck.HealthCheck(GetType(), HealthCheckResult.Warning, HealthCheckReason.ServerNotification,
                        $"Crescendarr: {registryReview} completed download(s) need review in the download registry (identity conflict, unverified completion, " +
                        "unreadable record or several clients reporting one folder). Nothing is imported for them until an operator removes the record.");
                var awaiting = Count("awaitingApproval");
                if (awaiting > 0)
                    return new NzbDrone.Core.HealthCheck.HealthCheck(GetType(), HealthCheckResult.Warning, HealthCheckReason.ServerNotification,
                        $"Crescendarr (audit only): {awaiting} completed download(s) have a Ready plan awaiting approval (CrescendarrImport with the plan ID in the download registry).");
            }

            return new NzbDrone.Core.HealthCheck.HealthCheck(GetType());
        }
    }

    public sealed partial class CrescendarrImportExecutor : IExecute<CrescendarrCycleCommand>
    {
        public void Execute(CrescendarrCycleCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var counters = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["observed"] = 0, ["ready"] = 0, ["awaitingApproval"] = 0, ["imported"] = 0, ["noImprovement"] = 0, ["review"] = 0,
                ["alreadyHandled"] = 0, ["blockedAlbum"] = 0, ["recovered"] = 0, ["pendingRecovery"] = 0, ["refused"] = 0, ["errors"] = 0,
                ["reevaluated"] = 0, ["registryReview"] = 0,
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
                var registryPath = RegistryPath(root, folder);
                var (previousRecord, raw, readable) = ReadRegistry(registryPath);
                if (readable && previousRecord != null && !DownloadRegistryPolicy.PendingStates.Contains(previousRecord.State) &&
                    !DownloadRegistryPolicy.ConclusionStates.Contains(previousRecord.State))
                {
                    counters["registryReview"]++;
                    CrescendarrGuard.Emit(_logger, $"CYCLE_REFUSED_ITEM download=\"{item.DownloadId}\" folder=\"{folder}\" reason=\"{reason}\" kept={previousRecord.State}");
                    continue;
                }

                WriteRegistry(registryPath, readable ? raw : null, new SourceIdentity(clientId, item.DownloadId, folder, null), "RefusedUnsafePath", reason, null, null, null, false);
                if (previousRecord?.State != "RefusedUnsafePath")
                    CrescendarrGuard.Emit(_logger, $"CYCLE_REFUSED_ITEM download=\"{item.DownloadId}\" folder=\"{folder}\" reason=\"{reason}\"");
            }

            // M24: two client items claiming one completed folder (or one download ID) are never told apart by guessing.
            var completed = clients.Completed.Select(c => (c.Item, c.ClientId, c.WatchFolder, Folder: Path.GetFullPath(c.Item.OutputPath.FullPath).TrimEnd('/'))).ToList();
            var ambiguous = completed.GroupBy(c => c.Folder, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g)
                .Concat(completed.GroupBy(c => c.Item.DownloadId, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g))
                .Select(c => c.Folder).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in ambiguous.OrderBy(f => f, StringComparer.Ordinal))
            {
                counters["observed"]++;
                try
                {
                    var ids = completed.Where(c => c.Folder == folder).Select(c => c.Item.DownloadId).ToHashSet(StringComparer.Ordinal);
                    var claimants = completed.Where(c => c.Folder == folder || ids.Contains(c.Item.DownloadId)).ToList();
                    var registryPath = RegistryPath(root, folder);
                    var (previousRecord, raw, readable) = ReadRegistry(registryPath);
                    counters["registryReview"]++;
                    if (readable && previousRecord != null && previousRecord.State != DownloadRegistryPolicy.AmbiguousSource &&
                        !DownloadRegistryPolicy.PendingStates.Contains(previousRecord.State) && !DownloadRegistryPolicy.ConclusionStates.Contains(previousRecord.State))
                    {
                        // Durable, locked or review records are never overwritten by an ambiguity; their evidence stays.
                        counters["alreadyHandled"]++;
                        continue;
                    }

                    var first = claimants[0];
                    var reason = "several download-client items report this completed folder or download ID: " +
                        string.Join("; ", claimants.Select(c => $"client {c.ClientId} download \"{c.Item.DownloadId}\""));
                    if (previousRecord?.State != DownloadRegistryPolicy.AmbiguousSource)
                        CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{first.Item.DownloadId}\" state={DownloadRegistryPolicy.AmbiguousSource} reason=\"{reason}\"");
                    WriteRegistry(registryPath, readable ? raw : null, new SourceIdentity(first.ClientId, first.Item.DownloadId, folder, null), DownloadRegistryPolicy.AmbiguousSource,
                        reason, null, null, null, false,
                        extra: new { claimants = claimants.Select(c => new { clientDefinitionId = c.ClientId, downloadId = c.Item.DownloadId, folder = c.Folder }).ToList() });
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    counters["errors"]++;
                    CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD_ERROR folder=\"{folder}\" {error.GetType().Name}: {error.Message}");
                }
            }

            foreach (var (item, clientId, watch, folder) in completed.Where(c => !ambiguous.Contains(c.Folder)))
            {
                counters["observed"]++;
                try
                {
                    var registryPath = RegistryPath(root, folder);
                    var (previousRecord, raw, readable) = ReadRegistry(registryPath);
                    if (!readable)
                    {
                        // Broken or foreign registry record: preserve it as evidence and stop; never plan or import from it.
                        var preserved = registryPath + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
                        File.Move(registryPath, preserved);
                        counters["registryReview"]++;
                        counters["review"]++;
                        CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{item.DownloadId}\" state={DownloadRegistryPolicy.RegistryUnreadable} preserved=\"{Path.GetFileName(preserved)}\"");
                        WriteRegistry(registryPath, null, new SourceIdentity(clientId, item.DownloadId, folder, null), DownloadRegistryPolicy.RegistryUnreadable,
                            "the previous registry record could not be read; it was preserved and an operator must review it", null, null, null, false,
                            extra: new { preservedAs = Path.GetFileName(preserved) });
                        continue;
                    }

                    var observation = _adapter.Observe(item, clientId, watch);
                    var current = new SourceIdentity(clientId, item.DownloadId, folder, ContentDigest(observation.IncomingSha));
                    var disposition = observation.Blocked != null ? "Blocked" : observation.Plan?.Disposition.ToString() ?? "Blocked";
                    var evaluation = DownloadRegistryPolicy.Evaluation(disposition, observation.Plan?.PlanId, observation.Blocked);
                    var (evidence, evidenceOperation) = Evidence(root, raw, current);
                    var decision = DownloadRegistryPolicy.Decide(previousRecord, current, evaluation, evidence);

                    if (decision.Action == RegistryAction.SkipHandled)
                    {
                        counters["alreadyHandled"]++;
                        continue;
                    }

                    if (decision.Action == RegistryAction.SkipUnchanged)
                    {
                        counters["alreadyHandled"]++;
                        if (DownloadRegistryPolicy.ReviewStates.Contains(decision.State)) counters["registryReview"]++;
                        if (decision.State != previousRecord?.State)
                        {
                            CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{item.DownloadId}\" state={decision.State} reason=\"{decision.Reason}\"");
                            WriteRegistry(registryPath, raw, current, decision.State, decision.Reason, observation, disposition, evaluation, false,
                                operationId: evidenceOperation);
                        }

                        continue;
                    }

                    if (decision.Action == RegistryAction.RecordCompletion)
                    {
                        counters["alreadyHandled"]++;
                        CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{item.DownloadId}\" state={DownloadRegistryPolicy.Imported} operation={evidenceOperation} reason=\"{decision.Reason}\"");
                        WriteRegistry(registryPath, raw, current, DownloadRegistryPolicy.Imported, decision.Reason, observation, disposition, evaluation, false,
                            operationId: evidenceOperation, completion: new { operationId = evidenceOperation, outcome = Completed, verifiedUtc = DateTime.UtcNow });
                        continue;
                    }

                    if (decision.Action == RegistryAction.Review)
                    {
                        counters["registryReview"]++;
                        counters["review"]++;
                        if (decision.State == previousRecord?.State) continue;
                        CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{item.DownloadId}\" state={decision.State} reason=\"{decision.Reason}\"");
                        WriteRegistry(registryPath, raw, current, decision.State, decision.Reason, observation, disposition, evaluation, false,
                            operationId: evidenceOperation, keepPrevious: true);
                        continue;
                    }

                    // Evaluate: the FRESH plan decides; any import still goes through RunImport's re-observation and plan gate.
                    var state = disposition switch
                    {
                        "Ready" => mode == CrescendarrMode.GuardedAuto && decision.AutoImportAllowed ? "Ready" : "AwaitingApproval",
                        "NoImprovement" => "NoImprovement",
                        "NeedsReview" => "NeedsReview",
                        _ => "Blocked",
                    };
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
                            var operationOutcome = RunImport(item.DownloadId, observation.Plan.PlanId);
                            state = operationOutcome == Completed ? DownloadRegistryPolicy.Imported : operationOutcome;
                        }
                        else
                        {
                            state = "Deferred";
                        }
                    }

                    var changed = previousRecord == null || previousRecord.State != state || previousRecord.Evaluation != evaluation;
                    if (previousRecord != null && changed) counters["reevaluated"]++;
                    if (state == "AwaitingApproval") counters["awaitingApproval"]++;
                    if (state == DownloadRegistryPolicy.Imported) counters["imported"]++;
                    if (state == "NoImprovement") counters["noImprovement"]++;
                    if (state is "NeedsReview" or "Blocked" or "RequiresReview" or "OperatorApprovalRequired" or "Refused") counters["review"]++;
                    var operationId = observation.Plan == null ? null : OperationId(observation.Plan.PlanId);
                    WriteRegistry(registryPath, raw, current, state, decision.Reason, observation, disposition, evaluation, !decision.AutoImportAllowed,
                        operationId: operationId,
                        completion: state == DownloadRegistryPolicy.Imported ? new { operationId, outcome = Completed, verifiedUtc = DateTime.UtcNow } : null);
                    if (changed)
                    {
                        CrescendarrGuard.Emit(_logger, $"CYCLE_DOWNLOAD download=\"{item.DownloadId}\" disposition={disposition} state={state} plan={observation.Plan?.PlanId} " +
                            $"previous={previousRecord?.State ?? "none"} reason=\"{observation.Blocked ?? decision.Reason}\"");
                    }
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

        private static readonly JsonSerializerOptions Indented = new JsonSerializerOptions { WriteIndented = true };

        private static string RegistryPath(string root, string folder) =>
            Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "downloads", Disk.Sha(Encoding.UTF8.GetBytes(folder))[..16] + ".json"), "download registry");

        private static string ContentDigest(IReadOnlyDictionary<string, string> incoming) =>
            Disk.Sha(Encoding.UTF8.GetBytes(string.Join("\n", incoming.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value))));

        /// <summary>The previous record, or (null, null, true) when there is none; (null, null, false) when it cannot be trusted.</summary>
        private static (RegistryRecord Record, JsonElement? Raw, bool Readable) ReadRegistry(string path)
        {
            if (!File.Exists(path)) return (null, null, true);
            try
            {
                var raw = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
                if (raw.ValueKind != JsonValueKind.Object) return (null, null, false);
                string Text(string name) => raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var schema = raw.TryGetProperty("schema", out var s) ? s.GetInt32() : 1;
                var state = Text("state");
                if (state == null || Text("folder") == null || !raw.TryGetProperty("clientDefinitionId", out var client)) return (null, null, false);
                var manual = raw.TryGetProperty("manualOnly", out var m) && m.ValueKind == JsonValueKind.True;
                return (new RegistryRecord(schema, state, new SourceIdentity(client.GetInt32(), Text("downloadId"), Text("folder"), Text("inventoryDigest")),
                    Text("evaluation"), manual), raw, true);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                return (null, null, false);
            }
        }

        /// <summary>
        /// Completion evidence from the operations' OWN durable files: first the operation the record names, otherwise any
        /// operation whose operation.json binds exactly this client, download ID, folder and content.
        /// </summary>
        private static (OperationEvidence Evidence, string OperationId) Evidence(string root, JsonElement? raw, SourceIdentity current)
        {
            var ops = Path.Combine(root, "crescendarr", "ops");
            string Named()
            {
                if (raw is not { } r) return null;
                if (r.TryGetProperty("completion", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("operationId", out var ci) && ci.ValueKind == JsonValueKind.String) return ci.GetString();
                return r.TryGetProperty("operationId", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            }

            (OperationEvidence, string) Of(string id)
            {
                if (id == null || !Guid.TryParseExact(id, "D", out _)) return (OperationEvidence.None, null);
                var opRoot = Disk.RequireConfined(root, Path.Combine(ops, id), "operation");
                if (!Directory.Exists(opRoot)) return (OperationEvidence.None, null);
                var state = OperationState(opRoot);
                if (state is "Resolved" or Abandoned) return (OperationEvidence.ResolvedByOperator, id);
                if (state != Completed) return (OperationEvidence.Unfinished, id);
                return (BindsSource(opRoot, current) ? OperationEvidence.CompletedForThisSource : OperationEvidence.CompletedForOtherSource, id);
            }

            var named = Of(Named());
            if (named.Item1 != OperationEvidence.None || !Directory.Exists(ops)) return named;
            foreach (var opRoot in Directory.GetDirectories(ops).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!BindsSource(opRoot, current)) continue;
                var found = Of(Path.GetFileName(opRoot));
                if (found.Item1 != OperationEvidence.None) return found;
            }

            return (OperationEvidence.None, null);
        }

        private static bool BindsSource(string opRoot, SourceIdentity current)
        {
            var path = Path.Combine(opRoot, "operation.json");
            if (!File.Exists(path)) return false;
            try
            {
                var op = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
                var candidates = op.GetProperty("candidates").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.Ordinal);
                return op.GetProperty("clientId").GetInt32() == current.ClientId && op.GetProperty("downloadId").GetString() == current.DownloadId &&
                    Path.GetFullPath(op.GetProperty("downloadFolder").GetString()).TrimEnd('/') == current.Folder && ContentDigest(candidates) == current.ContentDigest;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }

        private static void WriteRegistry(string path, JsonElement? previous, SourceIdentity identity, string state, string reason, Observation observation,
            string disposition, string evaluation, bool manualOnly, string operationId = null, object completion = null, bool keepPrevious = false, object extra = null)
        {
            object Carried(string name) => previous is { } p && p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
            var record = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["schema"] = DownloadRegistryPolicy.CurrentSchema,
                ["downloadId"] = identity.DownloadId, ["clientDefinitionId"] = identity.ClientId, ["folder"] = identity.Folder, ["inventoryDigest"] = identity.ContentDigest,
                ["state"] = state, ["reason"] = reason, ["evaluation"] = evaluation, ["manualOnly"] = manualOnly,
                ["planId"] = observation?.Plan?.PlanId, ["disposition"] = disposition, ["blocked"] = observation?.Blocked,
                ["operationId"] = operationId,
                // Completion evidence is never dropped: a review record keeps the completion it is disputing.
                ["completion"] = completion ?? Carried("completion"),
                ["pluginHandled"] = state is DownloadRegistryPolicy.Imported or "NoImprovement" or "NeedsReview" or "Blocked" or "RequiresReview" or "Refused",
                ["lidarrImported"] = false, ["downloaderRemoved"] = false, ["sourceRetained"] = Directory.Exists(identity.Folder),
                ["settlement"] = "Plugin-local record only. Crescendarr never calls the client's RemoveItem and never marks the stock queue imported (CDH OFF).",
                ["firstSeen"] = Carried("firstSeen") ?? DateTime.UtcNow, ["lastSeen"] = DateTime.UtcNow,
            };
            if (keepPrevious && previous is { } prior) record["previousRecord"] = prior.Clone();
            if (extra != null) record["details"] = extra;
            Disk.WriteDurable(path, JsonSerializer.Serialize(record, Indented), replace: true);
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
