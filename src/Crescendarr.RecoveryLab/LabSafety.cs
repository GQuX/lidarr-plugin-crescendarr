using System.Runtime.InteropServices;
using Crescendarr.Core.Inventory;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// Refuses to operate anywhere except a marked, freshly created directory under the local
/// system temporary directory, on a non-network filesystem, with an explicit opt-in.
/// </summary>
public static class LabSafety
{
    public const string OptInVariable = "CRESCENDARR_RECOVERY_LAB";
    public const string OptInValue = "DISPOSABLE_LOCAL_ONLY";
    public const string MarkerName = ".crescendarr-recovery-lab";
    public const string MarkerContent = "CRESCENDARR_RECOVERY_LAB_V1";

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var check = PathCanonicalizer.Canonicalize(full, Style);
        return check.Verdict == PathVerdict.Ok ? check.CanonicalPath! : throw new LabSafetyException($"Path is not canonicalizable: {check.Detail}");
    }

    public static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != OptInValue)
            throw new LabSafetyException($"Set {OptInVariable}={OptInValue} to run the disposable recovery lab.");
    }

    /// <summary>Validates a lab root for a brand new lab (must not exist yet).</summary>
    public static string PrepareNewRoot(string root)
    {
        RequireOptIn();
        var canonical = Canonical(root);
        RequireUnderTemp(canonical);
        if (Directory.Exists(canonical) || File.Exists(canonical)) throw new LabSafetyException("A new lab root must not already exist.");
        Directory.CreateDirectory(canonical);
        RequireLocal(canonical);
        File.WriteAllText(Path.Combine(canonical, MarkerName), MarkerContent);
        return canonical;
    }

    /// <summary>Validates an existing, marked lab root.</summary>
    public static string RequireExistingRoot(string root)
    {
        RequireOptIn();
        var canonical = Canonical(root);
        RequireUnderTemp(canonical);
        var marker = Path.Combine(canonical, MarkerName);
        if (!File.Exists(marker) || File.ReadAllText(marker) != MarkerContent) throw new LabSafetyException("Lab marker missing or wrong; refusing.");
        if (new DirectoryInfo(canonical).LinkTarget is not null) throw new LabSafetyException("Lab root is a link; refusing.");
        RequireLocal(canonical);
        return canonical;
    }

    private static void RequireUnderTemp(string canonical)
    {
        var temp = Canonical(Path.GetTempPath());
        if (!PathCanonicalizer.IsStrictlyUnder(canonical, temp, Style)) throw new LabSafetyException("Lab root must be under the local system temporary directory.");
    }

    private static void RequireLocal(string canonical)
    {
        if (OperatingSystem.IsWindows())
        {
            if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) throw new LabSafetyException("Network paths are refused.");
            var drive = new DriveInfo(Path.GetPathRoot(canonical)!);
            if (drive.DriveType == DriveType.Network) throw new LabSafetyException("Network drives are refused.");
            return;
        }

        if (!File.Exists("/proc/self/mountinfo")) return;
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

        if (bestType is "cifs" or "smb3" or "smbfs" or "nfs" or "nfs4" or "fuse.sshfs" or "9p")
            throw new LabSafetyException($"Network filesystem '{bestType}' refused for the lab.");
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
