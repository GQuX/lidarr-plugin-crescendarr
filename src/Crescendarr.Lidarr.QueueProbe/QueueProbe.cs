// M16 (issue #44): TEST-ONLY command-queue probe for a fresh, disposable stock Lidarr v3.1.6.5079 host.
// Nothing here writes music, the Lidarr database, configuration or host files. The only side effects are
// log lines and blocking on test-owned barrier files that the CI harness creates. Every entry point refuses
// to act without the explicit disposable opt-in and a marker file in the test root.
// Never package, publish or install this assembly on a real host.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;

namespace NzbDrone.Core.Plugins.CrescendarrQueueProbe
{
    /// <summary>Not a production plugin. No GitHub install URL is published.</summary>
    public sealed class CrescendarrQueueProbePlugin : Plugin
    {
        public override string Name => "Crescendarr Disposable Command Queue Probe";
        public override string Owner => "Crescendarr project";
        public override string GithubUrl => string.Empty;
    }

    /// <summary>Read-only, disk-access-flagged command that blocks at a test-owned barrier.</summary>
    public sealed class CrescendarrQueueProbeCommand : Command
    {
        public string Label { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>Negative control: NOT disk-access-flagged; calls IDiskScanService.Scan directly.</summary>
    public sealed class CrescendarrDirectScanProbeCommand : Command
    {
        public string Folder { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => false;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    internal static class QueueProbeGuard
    {
        public const string OptInVariable = "CRESCENDARR_QUEUE_PROBE_OPT_IN";
        public const string RootVariable = "CRESCENDARR_QUEUE_PROBE_ROOT";
        public const string OptInValue = "DISPOSABLE_SYNTHETIC_ONLY";
        private static readonly Regex SafeLabel = new Regex("^[a-z0-9-]{1,40}$");

        public static bool TryRoot(out string root)
        {
            root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
            return Environment.GetEnvironmentVariable(OptInVariable) == OptInValue &&
                   Path.IsPathFullyQualified(root) &&
                   File.Exists(Path.Combine(root, ".crescendarr-disposable-host"));
        }

        public static string RequireRoot()
        {
            if (!TryRoot(out var root))
            {
                throw new CommandFailedException("Crescendarr queue probe is disabled without explicit disposable-host opt-in.");
            }

            return root;
        }

        public static string Barrier(string root, string name)
        {
            if (!SafeLabel.IsMatch(name))
            {
                throw new CommandFailedException("Unsafe probe label.");
            }

            return Path.Combine(root, "barriers", name);
        }

        public static void WaitForRelease(string root, string label)
        {
            var release = Barrier(root, "release-" + label);
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!File.Exists(release))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new CommandFailedException($"CRESCENDARR_QUEUE barrier deadline exceeded label={label}");
                }

                Thread.Sleep(50);
            }
        }

        public static string Snapshot(IManageCommandQueue queue)
        {
            var active = queue.All().Where(c => c.Status == CommandStatus.Started || c.Status == CommandStatus.Queued)
                .OrderBy(c => c.Id)
                .Select(c => $"{c.Name}#{c.Id}:{c.Status}:disk={c.Body.RequiresDiskAccess}");
            return "queue=[" + string.Join(",", active) + "]";
        }

        public static void Emit(Logger logger, string message)
        {
            var line = $"CRESCENDARR_QUEUE {DateTime.UtcNow:O} thread={Environment.CurrentManagedThreadId} {message}";
            Console.WriteLine(line);
            logger.Info(line);
        }
    }

    public sealed class CrescendarrQueueProbeExecutor : IExecute<CrescendarrQueueProbeCommand>
    {
        private readonly IManageCommandQueue _queue;
        private readonly Logger _logger;

        public CrescendarrQueueProbeExecutor(IManageCommandQueue queue, Logger logger)
        {
            _queue = queue;
            _logger = logger;
        }

        public void Execute(CrescendarrQueueProbeCommand message)
        {
            var root = QueueProbeGuard.RequireRoot();
            var others = _queue.All()
                .Where(c => c.Status == CommandStatus.Started && c.Body.RequiresDiskAccess && !ReferenceEquals(c.Body, message))
                .Select(c => $"{c.Name}#{c.Id}")
                .ToList();
            QueueProbeGuard.Emit(_logger, $"START label={message.Label} otherDiskAccessStarted=[{string.Join(",", others)}] {QueueProbeGuard.Snapshot(_queue)}");
            if (message.Label == "fail-path")
            {
                QueueProbeGuard.Emit(_logger, "FAIL label=fail-path deliberate");
                throw new CommandFailedException("SYNTHETIC deliberate failure path");
            }

            QueueProbeGuard.WaitForRelease(root, message.Label);
            QueueProbeGuard.Emit(_logger, $"END label={message.Label} {QueueProbeGuard.Snapshot(_queue)}");
        }
    }

    public sealed class CrescendarrDirectScanProbeExecutor : IExecute<CrescendarrDirectScanProbeCommand>
    {
        private readonly IDiskScanService _scan;
        private readonly IManageCommandQueue _queue;
        private readonly Logger _logger;

        public CrescendarrDirectScanProbeExecutor(IDiskScanService scan, IManageCommandQueue queue, Logger logger)
        {
            _scan = scan;
            _queue = queue;
            _logger = logger;
        }

        public void Execute(CrescendarrDirectScanProbeCommand message)
        {
            var root = QueueProbeGuard.RequireRoot();
            var folder = Path.GetFullPath(message.Folder);
            if (!folder.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new CommandFailedException("Direct scan folder is outside the disposable test root.");
            }

            QueueProbeGuard.Emit(_logger, $"DIRECT_SCAN_BEGIN folder={folder} {QueueProbeGuard.Snapshot(_queue)}");
            _scan.Scan(new List<string> { folder });
            QueueProbeGuard.Emit(_logger, $"DIRECT_SCAN_END folder={folder} {QueueProbeGuard.Snapshot(_queue)}");
        }
    }

    /// <summary>
    /// Holds a STOCK RescanFolders command in its Started state. CommandExecutor publishes
    /// CommandUpdatedEvent synchronously after marking a command started and before calling its handler,
    /// so blocking here keeps the stock worker (and its disk-access claim) busy without touching anything.
    /// </summary>
    public sealed class CrescendarrStockScanHold : IHandle<CommandUpdatedEvent>
    {
        private static readonly HashSet<int> Held = new HashSet<int>();
        private readonly IManageCommandQueue _queue;
        private readonly Logger _logger;

        public CrescendarrStockScanHold(IManageCommandQueue queue, Logger logger)
        {
            _queue = queue;
            _logger = logger;
        }

        public void Handle(CommandUpdatedEvent message)
        {
            var command = message.Command;
            if (command == null || command.Name != "RescanFolders" || command.Status != CommandStatus.Started ||
                !QueueProbeGuard.TryRoot(out var root) ||
                !File.Exists(QueueProbeGuard.Barrier(root, "arm-stock-hold")) ||
                File.Exists(QueueProbeGuard.Barrier(root, "release-stock-hold")))
            {
                return;
            }

            lock (Held)
            {
                if (!Held.Add(command.Id))
                {
                    return;
                }
            }

            QueueProbeGuard.Emit(_logger, $"STOCK_HOLD_BEGIN id={command.Id} {QueueProbeGuard.Snapshot(_queue)}");
            QueueProbeGuard.WaitForRelease(root, "stock-hold");
            QueueProbeGuard.Emit(_logger, $"STOCK_HOLD_END id={command.Id}");
        }
    }

    public sealed class CrescendarrQueueProbeStartup : IHandle<ApplicationStartedEvent>
    {
        private readonly KnownTypes _knownTypes;
        private readonly IServiceFactory _serviceFactory;
        private readonly IManageCommandQueue _queue;
        private readonly Logger _logger;

        public CrescendarrQueueProbeStartup(KnownTypes knownTypes, IServiceFactory serviceFactory, IManageCommandQueue queue, Logger logger)
        {
            _knownTypes = knownTypes;
            _serviceFactory = serviceFactory;
            _queue = queue;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            if (!QueueProbeGuard.TryRoot(out var root))
            {
                QueueProbeGuard.Emit(_logger, "DISABLED no disposable opt-in; probe commands will refuse to run");
                return;
            }

            var ours = typeof(CrescendarrQueueProbeCommand).Assembly;
            var known = _knownTypes.GetImplementations(typeof(Command)).Where(t => t.Assembly == ours).Select(t => t.Name).OrderBy(n => n);
            var executor = _serviceFactory.Build(typeof(IExecute<CrescendarrQueueProbeCommand>))?.GetType().FullName ?? "(none)";
            var directExecutor = _serviceFactory.Build(typeof(IExecute<CrescendarrDirectScanProbeCommand>))?.GetType().FullName ?? "(none)";

            // The stock CommandConverter rehydrates persisted bodies with typeof(Command).Assembly.FindTypeByName.
            var coreResolvesPersisted = typeof(Command).Assembly.GetExportedTypes()
                .Any(t => t.Name.Equals(nameof(CrescendarrQueueProbeCommand), StringComparison.InvariantCultureIgnoreCase));
            QueueProbeGuard.Emit(_logger, $"REGISTRATION knownCommandTypes=[{string.Join(",", known)}] executor={executor} " +
                $"directExecutor={directExecutor} coreAssemblyResolvesPersistedName={coreResolvesPersisted} " +
                $"pluginAssembly={ours.GetName().Name}");

            if (File.Exists(QueueProbeGuard.Barrier(root, "push-on-start")))
            {
                var model = _queue.Push(new CrescendarrQueueProbeCommand { Label = "plugin-pushed" });
                QueueProbeGuard.Emit(_logger, $"PLUGIN_PUSHED id={model.Id} status={model.Status}");
            }
        }
    }
}
