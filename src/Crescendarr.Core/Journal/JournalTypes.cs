using System.Collections.Immutable;
using Crescendarr.Core.Inventory;

namespace Crescendarr.Core.Journal;

// Pure write-ahead operation journal (M13). Nothing in this namespace performs storage,
// network, database, clock or host access. An executor persists the encoded manifest and
// supplies fresh observations; this code only validates, transitions and reconciles.

/// <summary>
/// Last durably recorded checkpoint of one track. Phases only move forward, one step at a
/// time, and only through <see cref="JournalEngine"/>.
/// </summary>
public enum TrackPhase
{
    /// <summary>Intent published (the manifest exists); nothing destructive may have happened.</summary>
    Planned = 1,

    /// <summary>Original audio (and lyric, when bound) copied, flushed, re-read and hash-verified.</summary>
    BackupVerified = 2,

    /// <summary>Registered lyric row removed so the host cannot recycle the physical lyric.</summary>
    LyricDetached = 3,

    /// <summary>Native replacement observed complete: incoming bytes at destination, fresh row linked.</summary>
    Upgraded = 4,

    /// <summary>Original lyric bytes re-registered against the fresh track-file row. Final when a lyric is bound.</summary>
    LyricRelinked = 5,
}

/// <summary>A step the executor declares (write-ahead) before attempting it.</summary>
public enum JournalStep
{
    CopyBackup = 1,
    DetachLyric = 2,
    NativeUpgrade = 3,
    RelinkLyric = 4,
}

public enum OperationStatus
{
    Active = 1,
    Completed = 2,

    /// <summary>Sticky. Nothing automatic may run again for this operation.</summary>
    RequiresReview = 3,
}

/// <summary>Exact content identity: lowercase hex SHA-256 plus byte length.</summary>
public sealed record ContentId(string Sha256, long Length);

/// <summary>A registered lyric bound to the original track file.</summary>
public sealed record LyricBinding(
    int LyricFileId,
    string RelativePath,
    string Path,
    ContentId Content,
    string BackupPath);

/// <summary>
/// Immutable per-track binding. <see cref="DestinationPath"/> is where the host will place the
/// incoming file; it may equal <see cref="OriginalPath"/>.
/// </summary>
public sealed record TrackBinding(
    int TrackId,
    string RecordingMbid,
    int OriginalTrackFileId,
    string OriginalPath,
    string DestinationPath,
    string IncomingPath,
    ContentId Original,
    ContentId Incoming,
    string BackupAudioPath,
    LyricBinding? Lyric);

/// <summary>Optional link to the read-only download inventory (M10).</summary>
public sealed record DownloadBinding(int ClientDefinitionId, string DownloadId, string? InventoryEvidenceDigest);

/// <summary>
/// Immutable operation binding. Its canonical digest is fixed for the operation's lifetime.
/// <see cref="ArtistRoot"/> is the host-authoritative artist directory (Lidarr <c>Artist.Path</c>);
/// stock Lidarr resolves a registered extra file as <c>Path.Combine(artist.Path, extra.RelativePath)</c>
/// (ExtraFileService.cs:117 at v3.1.6.5079), so every bound lyric's physical path must equal that join.
/// </summary>
public sealed record OperationBinding(
    string OperationId,
    DownloadBinding? Download,
    int AlbumId,
    string ReleaseGroupMbid,
    int MonitoredReleaseId,
    string MonitoredReleaseMbid,
    PathStyle PathStyle,
    string LibraryRoot,
    string StagingRoot,
    string OperationRoot,
    ImmutableArray<TrackBinding> Tracks,
    string ArtistRoot);

/// <summary>Mutable progress of one track, changed only by <see cref="JournalEngine"/>.</summary>
public sealed record TrackProgress(
    int TrackId,
    TrackPhase Phase,
    JournalStep? InFlight,
    int InterruptedAttempts,
    int? NewTrackFileId,
    int? NewLyricFileId);

/// <summary>One saved generation of the journal.</summary>
public sealed record OperationManifest(
    long Generation,
    OperationStatus Status,
    ImmutableArray<string> ReviewReasons,
    OperationBinding Binding,
    string BindingDigest,
    ImmutableArray<TrackProgress> Progress)
{
    public const int FormatVersion = 2;

    public TrackProgress ProgressFor(int trackId) => Progress.Single(p => p.TrackId == trackId);

    public TrackBinding BindingFor(int trackId) => Binding.Tracks.Single(t => t.TrackId == trackId);

    /// <summary>The single step currently declared in flight across the whole operation, if any.</summary>
    public TrackProgress? InFlightTrack => Progress.SingleOrDefault(p => p.InFlight is not null);
}

/// <summary>Thrown for any transition the journal does not allow. Callers must treat it as fail-closed.</summary>
public sealed class JournalTransitionException : InvalidOperationException
{
    public JournalTransitionException(string message) : base(message) { }

    public JournalTransitionException() { }

    public JournalTransitionException(string message, Exception inner) : base(message, inner) { }
}
