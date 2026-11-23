// Host-independent filesystem primitives shared by the Crescendarr plugin. This file deliberately references no Lidarr
// type so tests/Crescendarr.Plugin.Disk.Spec can compile it directly and exercise its integrity rules.
using System.Text;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public sealed class CrescendarrGuardException : Exception
    {
        public CrescendarrGuardException(string message) : base(message) { }
        public CrescendarrGuardException() { }
        public CrescendarrGuardException(string message, Exception inner) : base(message, inner) { }
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

        /// <summary>M23: seconds to wait so a CIFS client's deferred close and attribute cache have settled (0 for other filesystems).</summary>
        public static int CacheSettleSeconds(string path)
        {
            var identity = MountIdentity(path);
            if (identity == null || !identity.StartsWith("cifs|", StringComparison.Ordinal)) return 0;
            var mountpoint = identity.Split('|')[2];
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (fields.Length < 5 || separator < 0 || separator + 3 >= fields.Length || Unescape(fields[4]) != mountpoint) continue;
                int Option(string name, int fallback)
                {
                    var pair = fields[separator + 3].Split(',').FirstOrDefault(o => o.StartsWith(name + "=", StringComparison.Ordinal));
                    return pair != null && int.TryParse(pair[(name.Length + 1)..], out var value) ? value : fallback;
                }

                return Math.Min(30, Option("actimeo", 1) + Option("closetimeo", 1) + 2);
            }

            return 4;
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

        /// <summary>Hash even large audio files with bounded memory; never materialize an entire track in the heap.</summary>
        public static string Sha(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }

        public static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>
        /// Shared temp-file, file-fsync, atomic-rename path for small journal writes and large audio/lyric copies.
        /// The destination is never exposed unless the writer succeeds. A failed write cleans up its partial file.
        /// Parent-directory fsync is NOT provided; see the M23 durability evidence.
        /// </summary>
        private static void WriteAtomically(string path, bool replace, Action<FileStream> writer)
        {
            if (!replace && IsLink(path)) throw new CrescendarrGuardException($"Refusing to write over a link: {path}");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    writer(stream);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, replace);
            }
            finally
            {
                // An exception before rename must not accumulate potentially large abandoned staging files.
                // File.Move has already removed the temporary pathname on success.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Write via temp file + fsync + rename; replaces an existing file only when allowed.</summary>
        public static void WriteDurable(string path, byte[] bytes, bool replace) =>
            WriteAtomically(path, replace, stream => stream.Write(bytes, 0, bytes.Length));

        public static void WriteDurable(string path, string text, bool replace) => WriteDurable(path, Encoding.UTF8.GetBytes(text), replace);

        /// <summary>
        /// Verified no-overwrite copy with bounded memory. Hash the actual bytes as they are streamed to the temporary
        /// file, fail before rename if they differ from the immutable expected content, then re-read the placed file.
        /// This preserves the M13/M18 crash journal and copy guarantees without allocating an entire audio track.
        /// </summary>
        public static void CopyVerified(string source, string destination, string expectedSha)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            // Preserve the original preflight guarantee: a mismatched source must not even create destination directories
            // (particularly important when the destination is a library lyric path on CIFS).
            var beforeSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
            if (beforeSha != expectedSha) throw new CrescendarrGuardException($"Source changed before copy: {source}");
            input.Position = 0;
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            WriteAtomically(destination, replace: false, writer: output =>
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    hash.AppendData(buffer, 0, count);
                    output.Write(buffer, 0, count);
                }

                var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actualSha != expectedSha) throw new CrescendarrGuardException($"Source changed before copy: {source}");
            });
            if (Sha(destination) != expectedSha) throw new CrescendarrGuardException($"Copy verification failed: {destination}");
        }
    }
}
