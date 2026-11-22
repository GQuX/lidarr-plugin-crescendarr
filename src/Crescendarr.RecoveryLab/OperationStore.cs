using System.Text;
using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// Durable per-operation storage under ops/&lt;operationId&gt;/:
/// binding.sha256 (written once, before the first manifest) and manifest.json (replaced
/// atomically on every save). There is never a silent reset: an unreadable manifest is an error.
/// </summary>
public sealed class OperationStore
{
    public const string ManifestName = "manifest.json";
    public const string BindingDigestName = "binding.sha256";

    private OperationStore(string directory, OperationManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    public string Directory { get; }

    public OperationManifest Manifest { get; private set; }

    /// <summary>Creates a new operation. Fails if the ID already exists or if any other operation for the album is unfinished or unreadable.</summary>
    public static OperationStore Create(string opsRoot, OperationBinding binding)
    {
        foreach (var other in System.IO.Directory.EnumerateDirectories(opsRoot))
        {
            var loaded = TryOpen(other);
            if (loaded.Store is null) throw new InvalidOperationException($"Existing operation {Path.GetFileName(other)} is unreadable ({loaded.Error}); refusing to start another (no silent reset).");
            if (loaded.Store.Manifest.Binding.AlbumId == binding.AlbumId && loaded.Store.Manifest.Status != OperationStatus.Completed)
                throw new InvalidOperationException($"Album {binding.AlbumId} already has unfinished operation {loaded.Store.Manifest.Binding.OperationId}.");
        }

        var directory = binding.OperationRoot;
        if (System.IO.Directory.Exists(directory) || File.Exists(directory)) throw new InvalidOperationException("Duplicate operation ID: directory already exists.");
        Faults.Hit("intent.beforeManifest");
        System.IO.Directory.CreateDirectory(directory);
        var manifest = JournalEngine.Begin(binding);
        DurableFiles.WriteAtomically(Path.Combine(directory, BindingDigestName), Encoding.ASCII.GetBytes(manifest.BindingDigest), "intent.binding");
        DurableFiles.WriteAtomically(Path.Combine(directory, ManifestName), Encoding.UTF8.GetBytes(JournalCodec.Encode(manifest)), $"manifest.g{manifest.Generation}");
        Faults.Hit("intent.afterManifest");
        return new OperationStore(directory, manifest);
    }

    public static (OperationStore? Store, string? Error) TryOpen(string directory)
    {
        try
        {
            var manifestPath = Path.Combine(directory, ManifestName);
            var digestPath = Path.Combine(directory, BindingDigestName);
            if (!File.Exists(digestPath)) return (null, "binding digest missing");
            if (!File.Exists(manifestPath)) return (null, "manifest missing (intent never published)");
            var info = new FileInfo(manifestPath);
            if (info.Length > JournalCodec.MaxDocumentChars + 512) return (null, "manifest exceeds size cap");
            var digest = File.ReadAllText(digestPath, Encoding.ASCII);
            var loaded = JournalCodec.Decode(File.ReadAllText(manifestPath, Encoding.UTF8), Path.GetFileName(directory), digest);
            return loaded.Succeeded ? (new OperationStore(directory, loaded.Manifest!), null) : (null, loaded.Error);
        }
        catch (IOException error)
        {
            return (null, "read failed: " + error.Message);
        }
    }

    /// <summary>Saves the next generation. Generations must strictly increase and the binding must not change.</summary>
    public void Save(OperationManifest next)
    {
        if (next.Generation <= Manifest.Generation) throw new InvalidOperationException("Manifest generation must increase.");
        if (!string.Equals(next.BindingDigest, Manifest.BindingDigest, StringComparison.Ordinal)) throw new InvalidOperationException("Binding is immutable.");
        DurableFiles.WriteAtomically(Path.Combine(Directory, ManifestName), Encoding.UTF8.GetBytes(JournalCodec.Encode(next)), $"manifest.g{next.Generation}");
        Manifest = next;
    }

    /// <summary>Leftover temp files from interrupted saves; reported, never read as state.</summary>
    public IEnumerable<string> StrayTempFiles() =>
        System.IO.Directory.EnumerateFiles(Directory, ".*.tmp-*");
}
