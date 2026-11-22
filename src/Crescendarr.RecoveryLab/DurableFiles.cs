using System.Security.Cryptography;
using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// Local-disk durability primitives. On a local Linux filesystem: write a unique temp file,
/// Flush(true) (fsync), rename over the target, fsync the directory. This is NOT evidence of
/// SMB/CIFS durability, where flush and rename semantics depend on the server and mount.
/// </summary>
public static class DurableFiles
{
    private const int ChunkSize = 16 * 1024;

    public static void WriteAtomically(string path, byte[] content, string faultPrefix)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.WriteThrough))
        {
            stream.Write(content);
            Faults.Hit($"{faultPrefix}.beforeFsync");
            stream.Flush(flushToDisk: true);
        }

        Faults.Hit($"{faultPrefix}.beforeRename");
        File.Move(temp, path, overwrite: true);
        NativeDurability.FsyncDirectory(directory);
        Faults.Hit($"{faultPrefix}.afterRename");
    }

    /// <summary>Copies into an operation-owned path (overwrite allowed), flushing to disk.</summary>
    public static void CopyDurably(string source, string destination, string faultPrefix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize))
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.WriteThrough))
        {
            var buffer = new byte[ChunkSize];
            long written = 0;
            var half = input.Length / 2;
            var halfReported = false;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                written += read;
                if (!halfReported && written >= half)
                {
                    halfReported = true;
                    output.Flush(flushToDisk: true);
                    Faults.Hit($"{faultPrefix}.midCopy");
                }
            }

            output.Flush(flushToDisk: true);
        }

        NativeDurability.FsyncDirectory(Path.GetDirectoryName(destination)!);
    }

    /// <summary>Restores a file to a path that must not exist; never overwrites.</summary>
    public static void RestoreWithoutOverwrite(string backup, string destination, ContentId expected, string faultPrefix)
    {
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Restore destination already exists; refusing to overwrite.");
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.restore-{Guid.NewGuid():N}.partial~");
        CopyDurably(backup, temp, faultPrefix);
        if (Hash(temp) != expected)
        {
            File.Delete(temp);
            throw new IOException("Restored bytes do not match the bound original.");
        }

        Faults.Hit($"{faultPrefix}.beforeRename");
        File.Move(temp, destination, overwrite: false);
        NativeDurability.FsyncDirectory(directory);
    }

    public static ContentId Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize);
        var digest = SHA256.HashData(stream);
        return new ContentId(Convert.ToHexString(digest).ToLowerInvariant(), stream.Length);
    }
}
