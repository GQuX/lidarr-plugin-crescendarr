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

    public sealed class CrescendarrGuardException : Exception
    {
        public CrescendarrGuardException(string message) : base(message) { }
        public CrescendarrGuardException() { }
        public CrescendarrGuardException(string message, Exception inner) : base(message, inner) { }
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

    public static class Disk
    {
        public static bool IsUnder(string root, string path)
        {
            var prefix = root.TrimEnd('/') + "/";
            return path.StartsWith(prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the path itself is a symbolic link, INCLUDING a dangling one (M17's check used Exists, which is false
        /// for a dangling link). Unknown I/O conditions fail closed (treated as a link).
        /// </summary>
        public static bool IsLink(string path)
        {
            try
            {
                return new FileInfo(path).LinkTarget != null;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>True when the path or any parent up to (and including) stop is a symbolic link, dangling or not.</summary>
        public static bool HasLinkInChain(string path, string stop)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (IsLink(current)) return true;
                if (current == stop || current == "/") break;
                current = Path.GetDirectoryName(current);
            }

            return false;
        }

        public enum PathState
        {
            Absent,
            File,
            Directory,

            /// <summary>The path could not be examined (I/O error, stale or unreachable mount, permission).</summary>
            Error,
        }

        /// <summary>
        /// M23: like File.Exists, but an I/O error is NOT reported as "absent". An unreachable CIFS server or a stale mount
        /// must never look like a missing file (that would invite restores into the wrong place or false conclusions).
        /// </summary>
        public static PathState Probe(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0 ? PathState.Directory : PathState.File;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return PathState.Absent;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return PathState.Error;
            }
        }

        /// <summary>
        /// M23: identity of the filesystem that holds a path ("fstype|source|mountpoint" from /proc/self/mountinfo), so an
        /// operation can tell that its library mount disappeared (and the path is now a plain local directory) or changed.
        /// </summary>
        public static string MountIdentity(string path)
        {
            if (!File.Exists("/proc/self/mountinfo")) return null;
            var full = Path.GetFullPath(path).TrimEnd('/');
            string best = null;
            var bestLength = -1;
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (fields.Length < 5 || separator < 0 || separator + 2 >= fields.Length) continue;
                var mountpoint = Unescape(fields[4]);
                var prefix = mountpoint == "/" ? "/" : mountpoint.TrimEnd('/') + "/";
                if (!(full == mountpoint.TrimEnd('/') || full.StartsWith(prefix, StringComparison.Ordinal) || mountpoint == "/")) continue;
                if (mountpoint.Length <= bestLength) continue;
                bestLength = mountpoint.Length;
                best = fields[separator + 1] + "|" + Unescape(fields[separator + 2]) + "|" + mountpoint;
            }

            return best;
        }

        private static string Unescape(string value) => value.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

        /// <summary>M23: the folder accepts a durable write and a delete right now (a read-only or failing mount does not).</summary>
        public static string WriteProbeProblem(string folder)
        {
            var probe = Path.Combine(folder, ".crescendarr-write-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteDurable(probe, "probe\n", replace: false);
                File.Delete(probe);
                return Probe(probe) == PathState.Absent ? null : "the write probe could not be removed";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception) { }
                return error.GetType().Name + ": " + error.Message;
            }
        }

        /// <summary>Something (file, directory or link, even a dangling one) already occupies the path.</summary>
        public static bool Occupied(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

        /// <summary>A real regular file: present, not a link, and no link anywhere in its chain up to root.</summary>
        public static bool IsRegularFile(string root, string path) => File.Exists(path) && !HasLinkInChain(path, root);

        /// <summary>Canonical confinement required before every destructive operation: under root and no link in the chain.</summary>
        public static string RequireConfined(string root, string path, string role)
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(full, path.TrimEnd('/'), StringComparison.Ordinal) && !string.Equals(full, path, StringComparison.Ordinal))
                throw new CrescendarrGuardException($"{role} is not a canonical path.");
            if (!IsUnder(root, full)) throw new CrescendarrGuardException($"{role} escapes the disposable root.");
            if (HasLinkInChain(full, root)) throw new CrescendarrGuardException($"{role} traverses a symbolic link.");
            return full;
        }

        public static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        public static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>Write via temp file + fsync + rename; replaces an existing file only when allowed.</summary>
        public static void WriteDurable(string path, byte[] bytes, bool replace)
        {
            if (!replace && IsLink(path)) throw new CrescendarrGuardException($"Refusing to write over a link: {path}");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            File.Move(temporary, path, replace);
        }

        public static void WriteDurable(string path, string text, bool replace) => WriteDurable(path, Encoding.UTF8.GetBytes(text), replace);

        /// <summary>No-overwrite verified copy: fails if the destination exists or the re-read bytes differ.</summary>
        public static void CopyVerified(string source, string destination, string expectedSha)
        {
            var bytes = File.ReadAllBytes(source);
            if (Sha(bytes) != expectedSha) throw new CrescendarrGuardException($"Source changed before copy: {source}");
            WriteDurable(destination, bytes, replace: false);
            if (Sha(destination) != expectedSha) throw new CrescendarrGuardException($"Copy verification failed: {destination}");
        }
    }
}
