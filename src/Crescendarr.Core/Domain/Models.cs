using System.Collections.Immutable;

namespace Crescendarr.Core.Domain;

/// <summary>These are pre-validated, host-sourced snapshots, NOT direct filesystem models.</summary>
public sealed record IncomingFile(
    string CandidateKey,
    ImmutableArray<string> RecordingIds,
    string? HostQualityToken,
    bool IdentityVerified,
    bool SourceStable,
    bool HostImportEligible,
    bool HasStagedLrc);

public sealed record ExistingFile(
    long TrackFileId,
    ImmutableArray<string> RecordingIds,
    string? HostQualityToken,
    bool MetadataVerified,
    bool FileVerifiedPresent,
    bool HasLibraryLrc);

public sealed record DownloadSnapshot(
    string DownloadId,
    long AlbumId,
    string EditionId,
    string InventoryFingerprint,
    ImmutableArray<IncomingFile> Files);

public sealed record AlbumSnapshot(
    long AlbumId,
    string EditionId,
    string StateFingerprint,
    ImmutableArray<ExistingFile> Files);

public enum MergeAction { AddMissing, Upgrade, KeepExisting, NeedsReview }
public enum PlanDisposition { Ready, NoImprovement, NeedsReview, Blocked }

/// <summary>Host adapter must preserve the existing library lyric and staged lyric inventories.</summary>
public sealed record SidecarRequirement(bool PreserveExistingLrc, bool PreserveStagedLrc);

public sealed record TrackDecision(
    string CandidateKey,
    ImmutableArray<string> RecordingIds,
    MergeAction Action,
    string ReasonCode,
    long? ExpectedExistingTrackFileId,
    SidecarRequirement Lyrics);

public sealed record MergePlan(
    string PlanId,
    string DownloadId,
    long AlbumId,
    string ExpectedEditionId,
    string InventoryFingerprint,
    string AlbumStateFingerprint,
    PlanDisposition Disposition,
    ImmutableArray<TrackDecision> Decisions);
