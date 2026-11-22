using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// Synthetic 4-track album: tracks 1-3 are improved by the download, track 4 is a protected
/// superior original that is never part of the operation. Every track has a registered .lrc.
/// Track 3's destination differs from its original path (format change), tracks 1-2 reuse it.
/// All bytes are deterministic opaque payloads, not playable audio.
/// </summary>
public static class LabScenario
{
    public const int AlbumId = 7;
    public const int MonitoredReleaseId = 70;
    public const int AlternateReleaseId = 71;
    public const string MonitoredReleaseMbid = "11111111-2222-4333-8444-555555555501";
    public const string AlternateReleaseMbid = "11111111-2222-4333-8444-555555555502";
    public const string ReleaseGroupMbid = "11111111-2222-4333-8444-555555555500";
    public const int ProtectedTrackId = 104;
    public static readonly int[] TargetTrackIds = { 101, 102, 103 };
    public const string OperationIdFile = "operation-id.txt";

    public static string Library(string lab) => Path.Combine(lab, "library");
    public static string Staging(string lab) => Path.Combine(lab, "staging");
    public static string Ops(string lab) => Path.Combine(lab, "ops");
    public static string ArtistPath(string lab) => Path.Combine(Library(lab), "Synthetic Artist");
    public static string AlbumDir(string lab) => Path.Combine(ArtistPath(lab), "Synthetic Album (2026)");

    public static string RecordingMbid(int trackId) => $"22222222-3333-4444-8555-{trackId:000000000000}";

    public static byte[] Payload(string label, int length)
    {
        var output = new byte[length];
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(label));
        for (var offset = 0; offset < length; offset += seed.Length)
        {
            seed = SHA256.HashData(seed);
            Array.Copy(seed, 0, output, offset, Math.Min(seed.Length, length - offset));
        }

        return output;
    }

    /// <summary>Creates a new marked lab with host.json, library, staging and lyrics; returns its root.</summary>
    public static string Create(string root)
    {
        var lab = LabSafety.PrepareNewRoot(root);
        Directory.CreateDirectory(AlbumDir(lab));
        Directory.CreateDirectory(Path.Combine(Staging(lab), "job-1"));
        Directory.CreateDirectory(Ops(lab));

        var state = new FakeHost.State
        {
            AlbumId = AlbumId,
            Releases = new() { new HostRelease(MonitoredReleaseId, MonitoredReleaseMbid, true), new HostRelease(AlternateReleaseId, AlternateReleaseMbid, false) },
            NextTrackFileId = 900,
            NextLyricFileId = 9000,
            RecycleRoot = Path.Combine(lab, "recycle"),
            ArtistPath = ArtistPath(lab),
        };

        foreach (var trackId in TargetTrackIds.Append(ProtectedTrackId))
        {
            var n = trackId - 100;
            var original = OriginalPath(lab, trackId);
            File.WriteAllBytes(original, Payload($"original-{trackId}", 96 * 1024 + (n * 4093)));
            File.WriteAllBytes(LyricPath(lab, trackId), Encoding.UTF8.GetBytes($"[00:01.00] synthetic lyric line for track {n}\n[00:05.00] second line\n"));
            var trackFileId = 500 + n;
            state.TrackFiles.Add(new HostTrackFile(trackFileId, original));
            state.Tracks.Add(new HostTrack(trackId, RecordingMbid(trackId), trackFileId));
            state.Lyrics.Add(new HostLyric(800 + n, trackFileId, Path.GetRelativePath(ArtistPath(lab), LyricPath(lab, trackId))));
            File.WriteAllBytes(IncomingPath(lab, trackId), Payload($"incoming-{trackId}", 160 * 1024 + (n * 7919)));
        }

        new FakeHost(lab).Save(state, "host.init");
        return lab;
    }

    public static string OriginalPath(string lab, int trackId) =>
        Path.Combine(AlbumDir(lab), trackId == 103 ? "03 - Track Three.mp3" : $"{trackId - 100:00} - Track.flac");

    public static string DestinationPath(string lab, int trackId) =>
        trackId == 103 ? Path.Combine(AlbumDir(lab), "03 - Track Three.flac") : OriginalPath(lab, trackId);

    public static string IncomingPath(string lab, int trackId) => Path.Combine(Staging(lab), "job-1", $"{trackId - 100:00}.flac");

    public static string LyricPath(string lab, int trackId) => Path.Combine(AlbumDir(lab), $"{trackId - 100:00} - Track.lrc");

    /// <summary>Builds the immutable binding from the CURRENT host and disk state (hashes measured now).</summary>
    public static OperationBinding Bind(string lab, string operationId)
    {
        var host = new FakeHost(lab).Load();
        var opRoot = Path.Combine(Ops(lab), operationId);
        var tracks = TargetTrackIds.Select(trackId =>
        {
            var hostTrack = host.Tracks.Single(t => t.TrackId == trackId);
            var lyric = host.Lyrics.Single(l => l.TrackFileId == hostTrack.TrackFileId);
            // Physical lyric identity is DERIVED from the host-authoritative artist root and the registered
            // relative path, exactly as stock Lidarr resolves it; it is never supplied independently.
            var lyricPath = JournalValidation.JoinUnder(host.ArtistPath, lyric.RelativePath, LabSafety.Style)
                ?? throw new InvalidOperationException("Registered lyric relative path does not resolve under the artist root.");
            return new TrackBinding(
                trackId, hostTrack.RecordingMbid, hostTrack.TrackFileId,
                OriginalPath(lab, trackId), DestinationPath(lab, trackId), IncomingPath(lab, trackId),
                DurableFiles.Hash(OriginalPath(lab, trackId)), DurableFiles.Hash(IncomingPath(lab, trackId)),
                Path.Combine(opRoot, "backup", $"{trackId}.audio"),
                new LyricBinding(lyric.LyricFileId, lyric.RelativePath, lyricPath, DurableFiles.Hash(lyricPath), Path.Combine(opRoot, "backup", $"{trackId}.lrc")));
        }).ToImmutableArray();

        return new OperationBinding(operationId, new DownloadBinding(1, "Synthetic_job-1_638700000000000000", null), AlbumId, ReleaseGroupMbid,
            MonitoredReleaseId, MonitoredReleaseMbid, LabSafety.Style, Library(lab), Staging(lab), opRoot, tracks, host.ArtistPath);
    }
}
