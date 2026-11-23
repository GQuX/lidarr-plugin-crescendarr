using System.Security.Cryptography;
using NzbDrone.Core.Plugins.Crescendarr;

// Specs for the bounded-memory verified I/O introduced by the post-M23 cleanup (PR #60). They pin the integrity rules
// the M13-M23 journal and recovery depend on: a bad source never touches the destination tree, a copy never
// overwrites, a failed write leaves no visible or partial file, and the streaming hash equals a whole-buffer hash.
internal static class Program
{
    private static string root = string.Empty;

    public static int Main()
    {
        var specs = new (string Name, Action Test)[]
        {
            ("Streaming Sha equals whole-buffer SHA-256 on a multi-chunk file", StreamingSha),
            ("CopyVerified places identical bytes and leaves no partial file", CopySucceeds),
            ("CopyVerified with a mismatched source creates no destination directory", MismatchCreatesNothing),
            ("CopyVerified never overwrites an existing destination and cleans its temp", NoOverwrite),
            ("CopyVerified of an empty file", EmptyFile),
            ("WriteDurable without replace keeps the original and leaves no partial", DurableNoReplace),
            ("WriteDurable with replace swaps content atomically", DurableReplace),
            ("WriteDurable refuses to write over a dangling symlink", DanglingLink),
            ("Probe distinguishes absent, file and directory", ProbeStates),
        };
        var failed = 0;
        foreach (var (name, test) in specs)
        {
            root = Path.Combine(Path.GetTempPath(), "crescendarr-disk-spec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Console.WriteLine(failed == 0 ? $"ALL {specs.Length} DISK SPECS PASSED" : $"{failed} DISK SPEC(S) FAILED");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string[] Partials() => Directory.GetFiles(root, "*.partial-*", SearchOption.AllDirectories);

    private static void StreamingSha()
    {
        var bytes = Payload((3 * 1024 * 1024) + 12345, 1);
        Check(Disk.Sha(Write("big.flac", bytes)) == Hex(bytes), "streaming hash differs from buffer hash");
        Check(Disk.Sha(bytes) == Hex(bytes), "buffer overload differs");
    }

    private static void CopySucceeds()
    {
        var bytes = Payload((2 * 1024 * 1024) + 7, 2);
        var source = Write("source/a.flac", bytes);
        var destination = Path.Combine(root, "library", "artist", "a.flac");
        Disk.CopyVerified(source, destination, Hex(bytes));
        Check(File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes), "placed bytes differ");
        Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes), "source was altered");
        Check(Partials().Length == 0, "partial file left behind");
    }

    private static void MismatchCreatesNothing()
    {
        var source = Write("source/a.lrc", Payload(4096, 3));
        var destinationDirectory = Path.Combine(root, "library", "new-artist");
        var threw = false;
        try { Disk.CopyVerified(source, Path.Combine(destinationDirectory, "a.lrc"), Hex(Payload(4096, 99))); }
        catch (CrescendarrGuardException) { threw = true; }
        Check(threw, "a mismatched source was copied");
        Check(!Directory.Exists(destinationDirectory), "destination directory was created for a mismatched source");
        Check(Partials().Length == 0, "partial file left behind");
    }

    private static void NoOverwrite()
    {
        var bytes = Payload(300_000, 4);
        var source = Write("source/a.mp3", bytes);
        var original = Payload(1000, 5);
        var destination = Write("library/a.mp3", original);
        var threw = false;
        try { Disk.CopyVerified(source, destination, Hex(bytes)); }
        catch (IOException) { threw = true; }
        Check(threw, "an existing destination was overwritten or no error raised");
        Check(File.ReadAllBytes(destination).AsSpan().SequenceEqual(original), "existing destination changed");
        Check(Partials().Length == 0, "partial file left behind after the refused rename");
    }

    private static void EmptyFile()
    {
        var source = Write("source/empty.lrc", Array.Empty<byte>());
        var destination = Path.Combine(root, "library", "empty.lrc");
        Disk.CopyVerified(source, destination, Hex(Array.Empty<byte>()));
        Check(new FileInfo(destination).Length == 0, "empty copy not placed");
    }

    private static void DurableNoReplace()
    {
        var path = Write("ops/journal.json", Payload(100, 6));
        var before = File.ReadAllBytes(path);
        var threw = false;
        try { Disk.WriteDurable(path, "{}", replace: false); }
        catch (IOException) { threw = true; }
        Check(threw, "no-replace write over an existing file did not fail");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "original changed");
        Check(Partials().Length == 0, "partial file left behind");
    }

    private static void DurableReplace()
    {
        var path = Write("ops/journal.json", Payload(100, 7));
        Disk.WriteDurable(path, "{\"v\":4}", replace: true);
        Check(File.ReadAllText(path) == "{\"v\":4}", "replacement not visible");
        Check(Partials().Length == 0, "partial file left behind");
    }

    private static void DanglingLink()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("  (symlink case runs on Linux CI only)");
            return;
        }

        var link = Path.Combine(root, "library", "a.lrc");
        Directory.CreateDirectory(Path.GetDirectoryName(link));
        File.CreateSymbolicLink(link, Path.Combine(root, "elsewhere", "missing.lrc"));
        var threw = false;
        try { Disk.WriteDurable(link, "x", replace: false); }
        catch (CrescendarrGuardException) { threw = true; }
        Check(threw, "wrote through a dangling symlink");
        Check(!Directory.Exists(Path.Combine(root, "elsewhere")), "link target tree was created");
    }

    private static void ProbeStates()
    {
        var file = Write("x/file.mp3", Payload(10, 8));
        Check(Disk.Probe(file) == Disk.PathState.File, "file not reported as File");
        Check(Disk.Probe(Path.GetDirectoryName(file)) == Disk.PathState.Directory, "dir not reported as Directory");
        Check(Disk.Probe(Path.Combine(root, "nope", "missing.mp3")) == Disk.PathState.Absent, "missing not reported as Absent");
    }
}
