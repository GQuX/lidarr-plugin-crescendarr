using System.Collections.Immutable;
using System.Text.Json;
using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

/// <summary>
/// A tiny stand-in for the Lidarr database, persisted as one JSON file per "statement".
/// It is NOT Lidarr and NOT SQLite. It reproduces only the ordering hazards proven by the
/// native characterization tests: the stock upgrader recycles the old file, deletes its row
/// (whose delete handler recycles any still-linked lyric), moves the incoming file, and only
/// then inserts the new row. Each step is a separate durable write with a named fault point.
/// </summary>
public sealed class FakeHost
{
    public const string FileName = "host.json";
    public const string UnavailableMarker = "host.unavailable";

    private readonly string _labRoot;
    private readonly string _path;

    public FakeHost(string labRoot)
    {
        _labRoot = labRoot;
        _path = Path.Combine(labRoot, FileName);
    }

    public sealed class State
    {
        public int AlbumId { get; set; }
        public List<HostRelease> Releases { get; set; } = new();
        public List<HostTrack> Tracks { get; set; } = new();
        public List<HostTrackFile> TrackFiles { get; set; } = new();
        public List<HostLyric> Lyrics { get; set; } = new();
        public int NextTrackFileId { get; set; }
        public int NextLyricFileId { get; set; }
        public string RecycleRoot { get; set; } = string.Empty;
        public string ArtistPath { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public State Load() => JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("host.json empty");

    public void Save(State state, string faultPrefix) =>
        DurableFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(state, Json), faultPrefix);

    public HostSnapshot Snapshot()
    {
        if (File.Exists(Path.Combine(_labRoot, UnavailableMarker)) || !File.Exists(_path))
            return new HostSnapshot(false, 0, ImmutableArray<HostRelease>.Empty, ImmutableArray<HostTrack>.Empty, ImmutableArray<HostTrackFile>.Empty, ImmutableArray<HostLyric>.Empty, null);
        var s = Load();
        return new HostSnapshot(true, s.AlbumId, s.Releases.ToImmutableArray(), s.Tracks.ToImmutableArray(), s.TrackFiles.ToImmutableArray(), s.Lyrics.ToImmutableArray(), s.ArtistPath);
    }

    public void DeleteLyricRow(int lyricFileId, string fault)
    {
        var s = Load();
        s.Lyrics.RemoveAll(l => l.LyricFileId == lyricFileId);
        Save(s, fault);
    }

    public int InsertLyricRow(int trackFileId, string relativePath, string fault)
    {
        var s = Load();
        var id = s.NextLyricFileId++;
        s.Lyrics.Add(new HostLyric(id, trackFileId, relativePath));
        Save(s, fault);
        return id;
    }

    /// <summary>Operator-approved recovery: a FRESH row (never the deleted numeric ID) linked to the track.</summary>
    public int InsertFreshTrackFile(int trackId, string path, string fault)
    {
        var s = Load();
        var id = s.NextTrackFileId++;
        s.TrackFiles.Add(new HostTrackFile(id, path));
        s.Tracks = s.Tracks.Select(t => t.TrackId == trackId ? t with { TrackFileId = id } : t).ToList();
        Save(s, fault);
        return id;
    }

    public void SetMonitored(int releaseId)
    {
        var s = Load();
        s.Releases = s.Releases.Select(r => r with { Monitored = r.ReleaseId == releaseId }).ToList();
        Save(s, "host.setMonitored");
    }

    /// <summary>Stock-ordered replacement with a durable write and fault point between every step.</summary>
    public int NativeUpgrade(int trackId, string incomingPath, string destinationPath)
    {
        var prefix = $"native.{trackId}";
        var s = Load();
        var track = s.Tracks.Single(t => t.TrackId == trackId);
        var existing = s.TrackFiles.SingleOrDefault(f => f.TrackFileId == track.TrackFileId);

        if (existing is not null)
        {
            // 1. Recycle the old file (stock RecycleBinProvider.DeleteFile).
            if (File.Exists(existing.Path)) MoveToRecycle(s, existing.Path);
            Faults.Hit($"{prefix}.afterRecycle");

            // 2. Delete the old row; the stock delete handler recycles any lyric still linked.
            foreach (var lyric in s.Lyrics.Where(l => l.TrackFileId == existing.TrackFileId).ToList())
            {
                var lyricPath = Path.Combine(s.ArtistPath, lyric.RelativePath);
                if (File.Exists(lyricPath)) MoveToRecycle(s, lyricPath);
                s.Lyrics.Remove(lyric);
            }

            s.TrackFiles.Remove(existing);
            Save(s, $"{prefix}.rowDelete");
            Faults.Hit($"{prefix}.afterRowDelete");
        }

        // 3. Move the incoming file into place. Stock refuses to overwrite.
        if (File.Exists(destinationPath)) throw new IOException("Destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(incomingPath, destinationPath, overwrite: false);
        NativeDurability.FsyncDirectory(Path.GetDirectoryName(destinationPath)!);
        Faults.Hit($"{prefix}.afterMove");

        // 4. Insert the new row and link the track (stock AddMany + SetFileIds after the loop).
        s = Load();
        var id = s.NextTrackFileId++;
        s.TrackFiles.Add(new HostTrackFile(id, destinationPath));
        s.Tracks = s.Tracks.Select(t => t.TrackId == trackId ? t with { TrackFileId = id } : t).ToList();
        Save(s, $"{prefix}.link");
        Faults.Hit($"{prefix}.afterLink");
        return id;
    }

    private static void MoveToRecycle(State s, string path)
    {
        var target = Path.Combine(s.RecycleRoot, $"{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        Directory.CreateDirectory(s.RecycleRoot);
        File.Move(path, target, overwrite: false);
    }
}
