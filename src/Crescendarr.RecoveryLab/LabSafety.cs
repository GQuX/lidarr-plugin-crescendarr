using System.Runtime.InteropServices;
using Crescendarr.Core.Inventory;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// Narrow, fail-closed sandbox contract for the disposable lab (revised after PR #37 review finding A).
/// <list type="number">
/// <item>Explicit opt-in environment variable.</item>
/// <item>The system temp directory, and EVERY ancestor of it, must be an existing real directory with no
///   symlink, junction or other reparse point. Otherwise lexical containment says nothing about where
///   writes physically land, so the lab refuses to run at all (e.g. macOS /tmp -> /private/tmp).</item>
/// <item>A lab root must be a DIRECT child of that temp directory named <c>crescendarr-*</c>. Nested roots
///   (temp/alias/lab) are refused, so an attacker-controlled intermediate directory is never traversed.</item>
/// <item>Locality (no network filesystem) is checked on the link-free temp directory BEFORE anything is
///   created, then re-checked, together with the whole chain, after the single-level create.</item>
/// <item>A new root must not exist in any form (including a dangling link); the marker is created with
///   CreateNew. An existing root must not be a link, must hold a regular marker file, and its working
///   directories (library, staging, ops, recycle) must not be links. Checked on every open.</item>
/// </list>
/// RESIDUAL RACE: these are check-then-use path operations, not handle-relative (openat/O_NOFOLLOW)
/// operations. A process running as the SAME user could swap a lab directory for a link between a check
/// and a later write. The lab therefore trusts same-user processes and must not be run where another
/// process of the same user is hostile. Other users cannot rename entries of a sticky-bit temp directory.
/// This is a reduced-risk sandbox for synthetic data, not a containment guarantee.
/// </summary>
public static class LabSafety
{
    public const string OptInVariable = "CRESCENDARR_RECOVERY_LAB";
    public const string OptInValue = "DISPOSABLE_LOCAL_ONLY";
    public const string MarkerName = ".crescendarr-recovery-lab";
    public const string MarkerContent = "CRESCENDARR_RECOVERY_LAB_V1";
    public const string RootPrefix = "crescendarr-";
    private static readonly string[] WorkingDirectories = { "library", "staging", "ops", "recycle" };

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) full = Path.GetPathRoot(Path.GetFullPath(path))!;
        var check = PathCanonicalizer.Canonicalize(full, Style);
        return check.Verdict == PathVerdict.Ok ? check.CanonicalPath! : throw new LabSafetyException($"Path is not canonicalizable: {check.Detail}");
    }

    public static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != OptInValue)
            throw new LabSafetyException($"Set {OptInVariable}={OptInValue} to run the disposable recovery lab.");
    }

    /// <summary>The temp directory, verified link-free and local. Throws otherwise.</summary>
    public static string TrustedTemp()
    {
        var temp = Canonical(Path.GetTempPath());
        RequireLinkFreeChain(temp, includeSelf: true);
        RequireLocal(temp);
        return temp;
    }

    /// <summary>Validates and creates a brand new lab root (single directory level).</summary>
    public static string PrepareNewRoot(string root)
    {
        RequireOptIn();
        var canonical = Canonical(root);
        var temp = TrustedTemp();
        RequireDirectChild(canonical, temp);
        if (AnyEntryExists(canonical)) throw new LabSafetyException("A new lab root must not already exist in any form (file, directory or link).");

        Directory.CreateDirectory(canonical); // parent exists and is verified, so this creates exactly one level
        // Re-verify after creation: the new directory and its whole chain must still be real and local.
        RequireLinkFreeChain(canonical, includeSelf: true);
        RequireLocal(canonical);
        if (Directory.EnumerateFileSystemEntries(canonical).Any()) throw new LabSafetyException("Newly created lab root is not empty.");
        using (var marker = new FileStream(Path.Combine(canonical, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            marker.Write(System.Text.Encoding.ASCII.GetBytes(MarkerContent));
        return canonical;
    }

    /// <summary>Validates an existing, marked lab root and its working directories. Called on every open.</summary>
    public static string RequireExistingRoot(string root)
    {
        RequireOptIn();
        var canonical = Canonical(root);
        var temp = TrustedTemp();
        RequireDirectChild(canonical, temp);
        RequireLinkFreeChain(canonical, includeSelf: true);
        if (!Directory.Exists(canonical)) throw new LabSafetyException("Lab root does not exist.");
        RequireLocal(canonical);

        var marker = new FileInfo(Path.Combine(canonical, MarkerName));
        if (!marker.Exists || IsLink(marker) || marker.Length > 64 || File.ReadAllText(marker.FullName) != MarkerContent)
            throw new LabSafetyException("Lab marker missing, linked or wrong; refusing.");

        foreach (var name in WorkingDirectories)
        {
            var path = Path.Combine(canonical, name);
            var info = new DirectoryInfo(path);
            if (IsLink(info) || (File.Exists(path) && !Directory.Exists(path))) throw new LabSafetyException($"Lab working directory '{name}' is a link or not a directory; refusing.");
        }

        return canonical;
    }

    private static void RequireDirectChild(string canonical, string temp)
    {
        var parent = Path.GetDirectoryName(canonical);
        if (parent is null || !PathCanonicalizer.PathsEqual(parent, temp, Style))
            throw new LabSafetyException("Lab root must be a direct child of the system temporary directory.");
        if (!Path.GetFileName(canonical).StartsWith(RootPrefix, StringComparison.Ordinal))
            throw new LabSafetyException($"Lab root name must start with '{RootPrefix}'.");
    }

    /// <summary>Every existing component from the volume root down to <paramref name="path"/> must be a real directory, not a link or reparse point.</summary>
    private static void RequireLinkFreeChain(string path, bool includeSelf)
    {
        var current = includeSelf ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            var info = new DirectoryInfo(current);
            if (IsLink(info)) throw new LabSafetyException($"Path component is a link or reparse point: {current}");
            if (!info.Exists && current != path) throw new LabSafetyException($"Path component does not exist: {current}");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static bool AnyEntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static void RequireLocal(string canonical)
    {
        if (OperatingSystem.IsWindows())
        {
            if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) throw new LabSafetyException("Network paths are refused.");
            var drive = new DriveInfo(Path.GetPathRoot(canonical)!);
            if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) throw new LabSafetyException("Network or unknown drives are refused.");
            return;
        }

        // Because the chain is link-free, the lexical path equals the physical path, so matching it
        // against mount points from mountinfo is meaningful. No mountinfo means no evidence: refuse.
        if (!File.Exists("/proc/self/mountinfo"))
        {
            if (OperatingSystem.IsLinux()) throw new LabSafetyException("Cannot read /proc/self/mountinfo to prove filesystem locality.");
            return;
        }

        string? bestMount = null, bestType = null;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var parts = line.Split(" - ", 2);
            if (parts.Length != 2) continue;
            var left = parts[0].Split(' ');
            if (left.Length < 5) continue;
            var mount = left[4].Replace("\\040", " ", StringComparison.Ordinal);
            if (!(canonical == mount || canonical.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal))) continue;
            if (bestMount is null || mount.Length > bestMount.Length) { bestMount = mount; bestType = parts[1].Split(' ')[0]; }
        }

        if (bestType is null) throw new LabSafetyException("No mount found for the lab path.");
        if (bestType is "cifs" or "smb3" or "smbfs" or "nfs" or "nfs4" or "fuse.sshfs" or "9p" or "fuse" || bestType.StartsWith("fuse.", StringComparison.Ordinal))
            throw new LabSafetyException($"Network or FUSE filesystem '{bestType}' refused for the lab.");
    }
}

public sealed class LabSafetyException : Exception
{
    public LabSafetyException(string message) : base(message) { }

    public LabSafetyException() { }

    public LabSafetyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Linux directory fsync so a rename is durable in the local directory. Windows: documented no-op.</summary>
internal static partial class NativeDurability
{
    private const int ORdOnly = 0;
    private const int ODirectory = 0x10000;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    public static void FsyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fd = Open(directory, ORdOnly | ODirectory);
        if (fd < 0) throw new IOException($"open(directory) failed: errno {Marshal.GetLastPInvokeError()}");
        try
        {
            if (Fsync(fd) != 0) throw new IOException($"fsync(directory) failed: errno {Marshal.GetLastPInvokeError()}");
        }
        finally
        {
            _ = Close(fd);
        }
    }
}
