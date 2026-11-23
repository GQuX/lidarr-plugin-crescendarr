// M24 (issue #61): when a completed download is durably skipped, when it is re-evaluated, and when it must go to
// review. Pure: no I/O, clock or host types. The plugin gathers the facts (previous registry record, the fresh
// observation and the verified state of the operation that record points at) and applies the returned decision.
namespace Crescendarr.Core.Registry;

/// <summary>Immutable identity of one completed download as the client and the filesystem reported it.</summary>
public sealed record SourceIdentity(int ClientId, string DownloadId, string Folder, string ContentDigest);

/// <summary>What the registry previously recorded for this download folder.</summary>
/// <param name="ManualOnly">Set once an operator resolved an operation for this download: it is never imported automatically again.</param>
public sealed record RegistryRecord(int Schema, string State, SourceIdentity Identity, string? Evaluation, bool ManualOnly = false);

/// <summary>
/// The verified state of the Crescendarr operation the previous record points at, read from that operation's OWN durable
/// files (outcome.json, operation.json), never from the registry.
/// </summary>
public enum OperationEvidence
{
    /// <summary>No operation directory exists for the record (nothing was ever started).</summary>
    None = 0,

    /// <summary>The operation completed AND its operation.json binds exactly the current source identity and content.</summary>
    CompletedForThisSource = 1,

    /// <summary>The operation completed, but for a different client, download ID, folder or content.</summary>
    CompletedForOtherSource = 2,

    /// <summary>Journaled but not finished (Active, RequiresReview, OperatorApprovalRequired, StorageUnavailable, ...).</summary>
    Unfinished = 3,

    /// <summary>An operator resolution or abandonment ended the operation without import completion.</summary>
    ResolvedByOperator = 4,
}

public enum RegistryAction
{
    /// <summary>Durably handled by a verified completed operation; nothing is planned or imported.</summary>
    SkipHandled = 0,

    /// <summary>Re-evaluable, but nothing relevant changed; keep the record and stay quiet.</summary>
    SkipUnchanged = 1,

    /// <summary>Use the fresh plan (and, when allowed, import it through the normal guarded path).</summary>
    Evaluate = 2,

    /// <summary>The previous plan was completed by an operation for exactly this source: record it as imported.</summary>
    RecordCompletion = 3,

    /// <summary>Do nothing to the library; record the review state and keep the previous evidence.</summary>
    Review = 4,
}

public sealed record RegistryDecision(RegistryAction Action, string State, string Reason, bool AutoImportAllowed);

public static class DownloadRegistryPolicy
{
    public const int CurrentSchema = 2;

    public const string Imported = "Imported";
    public const string IdentityConflict = "IdentityConflict";
    public const string CompletionUnverified = "CompletionUnverified";
    public const string RegistryUnreadable = "RegistryUnreadable";
    public const string AmbiguousSource = "AmbiguousSource";
    public const string OperationUnfinished = "OperationUnfinished";

    /// <summary>Durable: never re-planned while the source identity is unchanged.</summary>
    public static readonly IReadOnlyList<string> DurableStates = new[] { Imported };

    /// <summary>Tied to an operation the operator or recovery owns; the registry never clears them.</summary>
    public static readonly IReadOnlyList<string> OperationLockedStates = new[] { "RequiresReview", "OperatorApprovalRequired", "StorageUnavailable", OperationUnfinished };

    /// <summary>Registry-level review states: only an operator clears them (by removing the registry record).</summary>
    public static readonly IReadOnlyList<string> ReviewStates = new[] { IdentityConflict, CompletionUnverified, RegistryUnreadable };

    /// <summary>A current, reproducible conclusion: re-evaluated whenever the fresh evaluation differs.</summary>
    public static readonly IReadOnlyList<string> ConclusionStates = new[] { "NoImprovement", "NeedsReview", "Blocked" };

    /// <summary>"Would import, not yet": always re-evaluated (the fresh plan, mode and album locks decide).</summary>
    public static readonly IReadOnlyList<string> PendingStates = new[] { "AwaitingApproval", "Deferred", "AlbumBlocked", "Refused", "RefusedUnsafePath", AmbiguousSource,
        "WouldImport", "LibraryUnavailable", "SourceUnavailable" };

    /// <summary>The fingerprint of a fresh evaluation: what decided the outcome, so an unchanged one stays quiet.</summary>
    public static string Evaluation(string? disposition, string? planId, string? blocked) =>
        $"{disposition ?? "none"}|{planId ?? "none"}|{blocked ?? string.Empty}";

    public static RegistryDecision Decide(RegistryRecord? previous, SourceIdentity current, string evaluation, OperationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            // No record (first sight, or a record an operator removed): the operations' own files still decide whether
            // this exact source was already imported or is owned by an unfinished operation.
            return evidence switch
            {
                OperationEvidence.CompletedForThisSource => new RegistryDecision(RegistryAction.RecordCompletion, Imported,
                    "a completed operation is bound to exactly this source", false),
                OperationEvidence.Unfinished => new RegistryDecision(RegistryAction.SkipUnchanged, OperationUnfinished,
                    "an unfinished operation is bound to this source; recovery or an operator owns it", false),
                OperationEvidence.ResolvedByOperator => Evaluate("an operator resolved the operation for this source; never imported automatically", auto: false),
                _ => Evaluate("first observation", auto: true),
            };
        }

        if (previous.Schema is < 1 or > CurrentSchema)
            return Review(RegistryUnreadable, $"unknown registry schema {previous.Schema}");

        // 0. A registry review state is sticky: re-checking it every cycle would only repeat (and nest) the same review.
        if (Has(ReviewStates, previous.State))
            return new RegistryDecision(RegistryAction.SkipUnchanged, previous.State, "registry review: an operator must remove this record before Crescendarr reconsiders the download", false);

        // 1. Completion evidence comes from the operation's own durable files and must bind THIS source.
        if (evidence == OperationEvidence.CompletedForOtherSource)
            return Review(IdentityConflict, "a completed operation recorded for this folder was bound to a different client, download ID, folder or content");
        if (evidence == OperationEvidence.CompletedForThisSource)
        {
            if (!SameIdentity(previous.Identity, current))
                return Review(IdentityConflict, "the download changed after it was imported: " + Difference(previous.Identity, current));
            return previous.State == Imported
                ? new RegistryDecision(RegistryAction.SkipHandled, Imported, "imported by a verified completed operation", false)
                : new RegistryDecision(RegistryAction.RecordCompletion, Imported, "the plan recorded for this download was completed by an operation", false);
        }

        if (Has(DurableStates, previous.State))
            return Review(CompletionUnverified, "the registry says Imported, but no completed operation for this source can be verified");


        // 2. An unfinished operation owns the album (recovery or an operator); the registry never clears or bypasses it.
        if (evidence == OperationEvidence.Unfinished)
            return new RegistryDecision(RegistryAction.SkipUnchanged, Has(OperationLockedStates, previous.State) ? previous.State : OperationUnfinished,
                "the operation for this download is unfinished; recovery or an operator owns it", false);

        // 3. After an operator resolution the download may be re-planned and shown, but is never imported automatically.
        if (evidence == OperationEvidence.ResolvedByOperator)
            return Evaluate("the operation was resolved by an operator; re-planned for review, never imported automatically", auto: false);

        if (Has(OperationLockedStates, previous.State))
        {
            // The record names an operation that does not exist: nothing proves what happened, so an operator decides.
            return Review(CompletionUnverified, $"the registry says {previous.State}, but the operation it names is missing");
        }

        var auto = !previous.ManualOnly;
        if (!SameIdentity(previous.Identity, current))
            return Evaluate("the download source changed: " + Difference(previous.Identity, current), auto);
        if (Has(PendingStates, previous.State))
            return Evaluate("pending decision re-evaluated", auto);
        if (Has(ConclusionStates, previous.State))
        {
            return string.Equals(previous.Evaluation, evaluation, StringComparison.Ordinal)
                ? new RegistryDecision(RegistryAction.SkipUnchanged, previous.State, "unchanged", false)
                : Evaluate(previous.Evaluation is null
                    ? "no evaluation fingerprint in a schema-1 record"
                    : "library, profile, monitored edition or plan changed since the last decision", auto);
        }

        return Review(RegistryUnreadable, $"unknown registry state '{previous.State}'");
    }

    private static RegistryDecision Evaluate(string reason, bool auto) => new(RegistryAction.Evaluate, string.Empty, reason, auto);

    private static RegistryDecision Review(string state, string reason) => new(RegistryAction.Review, state, reason, false);

    private static bool Has(IReadOnlyList<string> states, string state) => states.Contains(state, StringComparer.Ordinal);

    private static bool SameIdentity(SourceIdentity a, SourceIdentity b) =>
        a.ClientId == b.ClientId && string.Equals(a.DownloadId, b.DownloadId, StringComparison.Ordinal) &&
        string.Equals(a.Folder, b.Folder, StringComparison.Ordinal) && string.Equals(a.ContentDigest, b.ContentDigest, StringComparison.Ordinal);

    private static string Difference(SourceIdentity a, SourceIdentity b)
    {
        var parts = new List<string>();
        if (a.ClientId != b.ClientId) parts.Add("client");
        if (!string.Equals(a.DownloadId, b.DownloadId, StringComparison.Ordinal)) parts.Add("download ID");
        if (!string.Equals(a.Folder, b.Folder, StringComparison.Ordinal)) parts.Add("folder");
        if (!string.Equals(a.ContentDigest, b.ContentDigest, StringComparison.Ordinal)) parts.Add("content");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}
