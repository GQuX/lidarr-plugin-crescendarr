using System.Collections.Immutable;

namespace Crescendarr.Core.Inventory;

// Pure, host-agnostic model of completed-download observation (M10, read-only).
// Nothing in this namespace references Lidarr types, touches storage, the network,
// a database, or a download client. An adapter (not part of this milestone) would
// translate stock Lidarr DownloadClientItem values into ClientItemSnapshot values.

/// <summary>
/// Download status vocabulary. The named values mirror stock Lidarr's
/// DownloadItemStatus at v3.1.6.5079 (Queued, Paused, Downloading, Completed,
/// Failed, Warning). <see cref="Unknown"/> is what an adapter must emit for any
/// value it cannot map; it always fails closed.
/// </summary>
public enum ClientItemStatus
{
    Unknown = 0,
    Queued = 1,
    Paused = 2,
    Downloading = 3,
    Completed = 4,
    Failed = 5,
    Warning = 6,
}

public enum PollOutcome
{
    /// <summary>GetItems returned normally; absence of an item is meaningful.</summary>
    Succeeded = 1,

    /// <summary>GetItems threw or the client was unreachable; absence means nothing.</summary>
    ClientUnavailable = 2,
}

/// <summary>
/// Read-only observation state. There is deliberately no "Imported" or "Cleared"
/// state: observation alone can never establish either.
/// </summary>
public enum InventoryItemState
{
    /// <summary>First sighting. Never actionable.</summary>
    Observed = 1,

    /// <summary>Seen again with a non-terminal status (queued, paused, downloading, warning).</summary>
    StillDownloading = 2,

    /// <summary>Reported Completed, but the stability policy is not yet satisfied.</summary>
    CompletedSettling = 3,

    /// <summary>Completed with an unchanged fingerprint across the policy's polls and age.</summary>
    CompletedStable = 4,

    /// <summary>
    /// Not present in the policy's number of consecutive successful polls.
    /// This is NOT evidence of import and NOT permission to clear staging.
    /// </summary>
    MissingFromClient = 5,

    /// <summary>Identity collision (ID or output path). Sticky until operator review.</summary>
    Ambiguous = 6,

    /// <summary>Untrusted, unsafe or contradictory evidence. Sticky until operator review.</summary>
    RequiresReview = 7,
}

/// <summary>
/// Reason codes. Numeric values are part of the canonical persisted ordering;
/// append only, never renumber without bumping the schema version.
/// </summary>
public enum InventoryReason
{
    // Item evidence that forces RequiresReview.
    UnsupportedStatus = 100,
    ClientReportedFailure = 101,
    StatusRegressedAfterCompletion = 102,
    OutputPathMissing = 103,
    OutputPathUnsafe = 104,
    OutputPathOutsideAllowedRoots = 105,
    OutputPathChangedAfterCompletion = 106,
    OutputPathCasingChanged = 107,
    OutputRootChanged = 108,
    IdReusedWithDifferentPath = 109,
    SizeInvalid = 110,
    UntrustedMetadata = 111,
    StockHandlingWasActive = 112,
    ClientIdentityChanged = 113,
    ClientNoLongerSupported = 114,
    /// <summary>Returned after MissingFromClient. Same ID and path may be a different job; sticky review.</summary>
    ReappearedAfterMissing = 115,
    /// <summary>Returned after a shorter absence with a different title or total size.</summary>
    ReappearedWithChangedEvidence = 116,

    // Item evidence that forces Ambiguous.
    DuplicateIdInPoll = 200,
    DuplicateIdAcrossClients = 201,
    OutputPathCollision = 202,

    // Non-escalating item evidence.
    CompletedWithRemainingBytes = 301,
    SizeUnknown = 302,
    SizeChangedWhileCompleted = 303,
    NotSeenInLatestPoll = 304,
    AbsentFromClient = 305,
    StabilityResetClientUnavailable = 306,
    StabilityResetClockRegression = 307,
    StabilityResetPollGap = 308,
    StabilityResetInvalidTimestamp = 309,
    RemainingBytesUnknown = 310,
    StabilityResetSequenceReset = 311,

    // Evaluation-time reasons (never persisted on items).
    OutputRootNoLongerAllowed = 400,
    ClientStale = 401,
    ClientUnavailable = 402,
    ClientClockAhead = 403,
    ClientNotHealthy = 404,

    // Client-level reasons.
    ClientUnsupported = 500,
    ClientIdentityConflict = 501,
    CapacityExceeded = 502,
    UnkeyableItemsReported = 503,
    ClockRegressed = 504,
    PollGapExceeded = 505,
    StockHandlingActive = 506,
    InvalidTimestamp = 507,
    ClientPollFailed = 508,
    /// <summary>A poll arrived with a non-increasing sequence but a newer timestamp: the adapter lost its counter.</summary>
    SequenceReset = 509,
}

/// <summary>
/// Provider identity as configured in Lidarr: the download-client definition ID plus
/// its implementation name. The display name is informational and may change.
/// </summary>
public sealed record ClientIdentity(int DefinitionId, string Implementation, string Name);

/// <summary>One item as reported by a client's GetItems, already translated by an adapter.</summary>
public sealed record ClientItemSnapshot(
    string? DownloadId,
    string? Title,
    string? Category,
    ClientItemStatus Status,
    string? OutputPath,
    long? TotalBytes,
    long? RemainingBytes);

/// <summary>
/// One poll of one client. <see cref="Sequence"/> is a strictly increasing per-client
/// counter owned by the adapter and persisted with the inventory; it orders polls.
/// <see cref="ObservedAt"/> is the observer's wall clock and is only used for ages.
/// </summary>
public sealed record ClientPoll(
    ClientIdentity Client,
    long Sequence,
    DateTimeOffset ObservedAt,
    bool HostCompletedDownloadHandlingEnabled,
    PollOutcome Outcome,
    ImmutableArray<ClientItemSnapshot> Items,
    string? FailureReason = null);

/// <summary>Item identity: (download-client definition ID, exact download ID).</summary>
public readonly record struct ItemKey(int ClientDefinitionId, string DownloadId) : IComparable<ItemKey>
{
    public int CompareTo(ItemKey other)
    {
        var byClient = ClientDefinitionId.CompareTo(other.ClientDefinitionId);
        return byClient != 0 ? byClient : string.CompareOrdinal(DownloadId, other.DownloadId);
    }

    public override string ToString() => $"{ClientDefinitionId}:{DownloadId}";
}

public sealed record ClientRecord(
    int DefinitionId,
    string Implementation,
    string Name,
    long LastSequence,
    long? LastPollTicks,
    long? LastSuccessTicks,
    int ConsecutiveFailures,
    string? LastFailureReason,
    long UnkeyableItemsTotal,
    ImmutableSortedSet<InventoryReason> StickyReasons,
    ImmutableSortedSet<InventoryReason> LastPollReasons)
{
    internal static ClientRecord New(ClientIdentity identity) => new(
        identity.DefinitionId,
        identity.Implementation,
        InventoryText.IsCleanToken(identity.Name, 128) ? identity.Name : string.Empty,
        LastSequence: -1,
        LastPollTicks: null,
        LastSuccessTicks: null,
        ConsecutiveFailures: 0,
        LastFailureReason: null,
        UnkeyableItemsTotal: 0,
        StickyReasons: ImmutableSortedSet<InventoryReason>.Empty,
        LastPollReasons: ImmutableSortedSet<InventoryReason>.Empty);
}

public sealed record ItemRecord(
    ItemKey Key,
    InventoryItemState State,
    ImmutableSortedSet<InventoryReason> Reasons,
    long FirstSeenTicks,
    long LastSeenTicks,
    long LastSeenSequence,
    ClientItemStatus LastStatus,
    string? Title,
    string? Category,
    string? CurrentPath,
    string? CurrentRoot,
    string? AnchoredPath,
    string? AnchoredRoot,
    string? RawPathEvidence,
    long? TotalBytes,
    long? RemainingBytes,
    bool EverCompleted,
    int StablePollCount,
    long? StableSinceTicks,
    int ConsecutiveAbsences,
    ImmutableSortedSet<string> VariantPaths)
{
    /// <summary>Ordinal set used for <see cref="VariantPaths"/>; never culture-sensitive.</summary>
    public static ImmutableSortedSet<string> NoPaths { get; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    /// <summary>Every canonical path this record has claimed (anchored, current, conflicting variants).</summary>
    public IEnumerable<string> ClaimedPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { AnchoredPath, CurrentPath }.Concat(VariantPaths))
        {
            if (path is not null && seen.Add(path)) yield return path;
        }
    }

    public bool IsSticky => State is InventoryItemState.Ambiguous or InventoryItemState.RequiresReview;
}

/// <summary>The whole persisted inventory. Immutable; every transition returns a new value.</summary>
public sealed record InventoryState(
    ImmutableSortedDictionary<int, ClientRecord> Clients,
    ImmutableSortedDictionary<ItemKey, ItemRecord> Items)
{
    public const int SchemaVersion = 1;

    public static InventoryState Empty { get; } = new(
        ImmutableSortedDictionary<int, ClientRecord>.Empty,
        ImmutableSortedDictionary<ItemKey, ItemRecord>.Empty);
}
