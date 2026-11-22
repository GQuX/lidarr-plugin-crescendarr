using System.Collections.Immutable;
using Crescendarr.Core.Inventory;
using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// Builds a fresh filesystem snapshot of exactly the paths an operation binds. Any link or
/// reparse point on the way from the root to the path marks it as escaping. A missing root
/// (simulated disconnected mount) makes the snapshot unavailable.
/// </summary>
public static class FsObserver
{
    public static FilesystemSnapshot Observe(OperationBinding binding)
    {
        var roots = new[] { binding.LibraryRoot, binding.StagingRoot, binding.OperationRoot };
        if (roots.Any(r => !Directory.Exists(r)))
            return new FilesystemSnapshot(false, ImmutableDictionary<string, FileObservation>.Empty);

        var files = ImmutableDictionary.CreateBuilder<string, FileObservation>(StringComparer.Ordinal);
        foreach (var track in binding.Tracks)
        {
            foreach (var path in Reconciler.BoundPaths(track))
            {
                var root = roots.Single(r => PathCanonicalizer.IsStrictlyUnder(path, r, binding.PathStyle));
                files[path] = ObserveOne(path, root);
            }
        }

        return new FilesystemSnapshot(true, files.ToImmutable());
    }

    private static FileObservation ObserveOne(string path, string root)
    {
        if (TraversesLink(path, root)) return new FileObservation(File.Exists(path), null, null, Escapes: true);
        if (!File.Exists(path)) return Directory.Exists(path) ? new FileObservation(true, null, null, Escapes: true) : FileObservation.Absent;
        var content = DurableFiles.Hash(path);
        return new FileObservation(true, content.Length, content.Sha256, Escapes: false);
    }

    private static bool TraversesLink(string path, string root)
    {
        if (new DirectoryInfo(root).LinkTarget is not null) return true;
        var current = path;
        while (!string.Equals(current, root, StringComparison.Ordinal) && current.Length > root.Length)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))) return true;
            current = Path.GetDirectoryName(current)!;
        }

        return false;
    }
}
