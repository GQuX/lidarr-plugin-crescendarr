using Crescendarr.Core.Registry;

// M24: the download-registry state machine. Each spec names the hazard it pins.
internal static class RegistrySpecs
{
    private static readonly SourceIdentity Source = new(1, "client_download_1", "/downloads/album", "digest-a");
    private static readonly string SameEval = DownloadRegistryPolicy.Evaluation("NoImprovement", "plan-1", null);

    public static (string Name, Action Test)[] All => new (string, Action)[]
    {
        ("R01 first observation is evaluated", FirstObservation),
        ("R02 verified completed import is durably skipped even when the fresh plan is Ready again", CompletedSkipped),
        ("R03 completed import with changed bytes goes to review, never re-imported", CompletedChangedBytes),
        ("R04 completed import replayed under another download ID or client goes to review", CompletedOtherIdentity),
        ("R05 completed operation bound to another source is an identity conflict", CompletedForOtherSource),
        ("R06 Imported without a verifiable completed operation is never trusted", ImportedUnverified),
        ("R07 unchanged NoImprovement stays quiet", NoImprovementUnchanged),
        ("R08 NoImprovement re-evaluated after a library, profile or edition change", NoImprovementChanged),
        ("R09 Blocked and NeedsReview re-evaluated when the evaluation changes", BlockedChanged),
        ("R10 an unfinished operation keeps the download locked (no re-plan, no import)", UnfinishedLocks),
        ("R11 operator-resolved operation: re-planned but never auto-imported", ResolvedNoAuto),
        ("R12 an approved AwaitingApproval plan completed by an operation is recorded Imported", ApprovalCompleted),
        ("R13 pending decisions (AwaitingApproval, Deferred, AlbumBlocked, Refused) are always re-evaluated", PendingReevaluated),
        ("R14 registry review states are only cleared by an operator", ReviewStatesSticky),
        ("R15 unknown schema or state fails closed to review", UnknownFailsClosed),
        ("R16 a schema-1 conclusion without a fingerprint is re-evaluated, a schema-1 Imported is verified", SchemaOne),
        ("R17 locked state naming a missing operation goes to review", LockedMissingOperation),
        ("R18 same folder, new content after a conclusion is re-evaluated (nothing was imported)", ConclusionNewContent),
        ("R19 no record but a completed operation bound to this source: recorded Imported, not re-planned", NoRecordCompleted),
        ("R20 no record but an unfinished operation for this source: locked", NoRecordUnfinished),
        ("R21 manual-only downloads are never auto-imported again, even when pending or changed", ManualOnlySticks),
    };

    private static void Expect(RegistryDecision decision, RegistryAction action, string? state = null, bool? auto = null)
    {
        if (decision.Action != action) throw new InvalidOperationException($"expected {action}, got {decision.Action} ({decision.Reason})");
        if (state != null && decision.State != state) throw new InvalidOperationException($"expected state {state}, got {decision.State}");
        if (auto != null && decision.AutoImportAllowed != auto) throw new InvalidOperationException($"expected auto={auto}, got {decision.AutoImportAllowed}");
    }

    private static RegistryRecord Record(string state, SourceIdentity? identity = null, string? evaluation = null, int schema = 2) =>
        new(schema, state, identity ?? Source, evaluation ?? SameEval);

    private static RegistryDecision Decide(RegistryRecord? previous, OperationEvidence evidence, SourceIdentity? current = null, string? evaluation = null) =>
        DownloadRegistryPolicy.Decide(previous, current ?? Source, evaluation ?? SameEval, evidence);

    private static void FirstObservation() => Expect(Decide(null, OperationEvidence.None), RegistryAction.Evaluate, auto: true);

    private static void CompletedSkipped()
    {
        var ready = DownloadRegistryPolicy.Evaluation("Ready", "plan-2", null);
        Expect(Decide(Record("Imported"), OperationEvidence.CompletedForThisSource, evaluation: ready), RegistryAction.SkipHandled, "Imported", auto: false);
    }

    private static void CompletedChangedBytes() =>
        Expect(Decide(Record("Imported"), OperationEvidence.CompletedForThisSource, Source with { ContentDigest = "digest-b" }),
            RegistryAction.Review, DownloadRegistryPolicy.IdentityConflict);

    private static void CompletedOtherIdentity()
    {
        Expect(Decide(Record("Imported"), OperationEvidence.CompletedForThisSource, Source with { DownloadId = "reused" }), RegistryAction.Review, DownloadRegistryPolicy.IdentityConflict);
        Expect(Decide(Record("Imported"), OperationEvidence.CompletedForThisSource, Source with { ClientId = 2 }), RegistryAction.Review, DownloadRegistryPolicy.IdentityConflict);
    }

    private static void CompletedForOtherSource() =>
        Expect(Decide(Record("Imported"), OperationEvidence.CompletedForOtherSource), RegistryAction.Review, DownloadRegistryPolicy.IdentityConflict);

    private static void ImportedUnverified()
    {
        Expect(Decide(Record("Imported"), OperationEvidence.None), RegistryAction.Review, DownloadRegistryPolicy.CompletionUnverified);
        Expect(Decide(Record("Imported"), OperationEvidence.ResolvedByOperator), RegistryAction.Review, DownloadRegistryPolicy.CompletionUnverified);
        Expect(Decide(Record("Imported"), OperationEvidence.Unfinished), RegistryAction.Review, DownloadRegistryPolicy.CompletionUnverified);
    }

    private static void NoImprovementUnchanged() =>
        Expect(Decide(Record("NoImprovement"), OperationEvidence.None), RegistryAction.SkipUnchanged, "NoImprovement");

    private static void NoImprovementChanged() =>
        Expect(Decide(Record("NoImprovement"), OperationEvidence.None, evaluation: DownloadRegistryPolicy.Evaluation("Ready", "plan-9", null)),
            RegistryAction.Evaluate, auto: true);

    private static void BlockedChanged()
    {
        var blocked = DownloadRegistryPolicy.Evaluation("Blocked", null, "album has 2 monitored releases");
        Expect(Decide(Record("Blocked", evaluation: blocked), OperationEvidence.None, evaluation: blocked), RegistryAction.SkipUnchanged, "Blocked");
        Expect(Decide(Record("Blocked", evaluation: blocked), OperationEvidence.None), RegistryAction.Evaluate, auto: true);
        Expect(Decide(Record("NeedsReview"), OperationEvidence.None, evaluation: blocked), RegistryAction.Evaluate, auto: true);
    }

    private static void UnfinishedLocks()
    {
        Expect(Decide(Record("RequiresReview"), OperationEvidence.Unfinished, evaluation: "changed"), RegistryAction.SkipUnchanged, "RequiresReview", auto: false);
        Expect(Decide(Record("AwaitingApproval"), OperationEvidence.Unfinished), RegistryAction.SkipUnchanged, DownloadRegistryPolicy.OperationUnfinished, auto: false);
        Expect(Decide(Record(DownloadRegistryPolicy.OperationUnfinished), OperationEvidence.Unfinished), RegistryAction.SkipUnchanged, DownloadRegistryPolicy.OperationUnfinished);
    }

    private static void ResolvedNoAuto() =>
        Expect(Decide(Record("RequiresReview"), OperationEvidence.ResolvedByOperator), RegistryAction.Evaluate, auto: false);

    private static void ApprovalCompleted() =>
        Expect(Decide(Record("AwaitingApproval"), OperationEvidence.CompletedForThisSource), RegistryAction.RecordCompletion, "Imported");

    private static void PendingReevaluated()
    {
        foreach (var state in new[] { "AwaitingApproval", "Deferred", "AlbumBlocked", "Refused", DownloadRegistryPolicy.AmbiguousSource })
            Expect(Decide(Record(state), OperationEvidence.None), RegistryAction.Evaluate, auto: true);
    }

    private static void ReviewStatesSticky()
    {
        foreach (var state in new[] { DownloadRegistryPolicy.IdentityConflict, DownloadRegistryPolicy.CompletionUnverified, DownloadRegistryPolicy.RegistryUnreadable })
        {
            Expect(Decide(Record(state), OperationEvidence.None, evaluation: "anything"), RegistryAction.SkipUnchanged, state, auto: false);
            Expect(Decide(Record(state), OperationEvidence.CompletedForOtherSource, Source with { ContentDigest = "x" }), RegistryAction.SkipUnchanged, state, auto: false);
            Expect(Decide(Record(state), OperationEvidence.CompletedForThisSource), RegistryAction.SkipUnchanged, state, auto: false);
        }
    }

    private static void UnknownFailsClosed()
    {
        Expect(Decide(Record("NoImprovement", schema: 3), OperationEvidence.None), RegistryAction.Review, DownloadRegistryPolicy.RegistryUnreadable);
        Expect(Decide(Record("SomethingNew"), OperationEvidence.None), RegistryAction.Review, DownloadRegistryPolicy.RegistryUnreadable);
    }

    private static void SchemaOne()
    {
        var legacy = new RegistryRecord(1, "NoImprovement", Source, null);
        Expect(Decide(legacy, OperationEvidence.None), RegistryAction.Evaluate, auto: true);
        Expect(Decide(Record("Imported", schema: 1), OperationEvidence.CompletedForThisSource), RegistryAction.SkipHandled, "Imported");
        Expect(Decide(Record("Imported", schema: 1), OperationEvidence.None), RegistryAction.Review, DownloadRegistryPolicy.CompletionUnverified);
    }

    private static void LockedMissingOperation() =>
        Expect(Decide(Record("RequiresReview"), OperationEvidence.None), RegistryAction.Review, DownloadRegistryPolicy.CompletionUnverified);

    private static void NoRecordCompleted() =>
        Expect(Decide(null, OperationEvidence.CompletedForThisSource), RegistryAction.RecordCompletion, "Imported", auto: false);

    private static void NoRecordUnfinished() =>
        Expect(Decide(null, OperationEvidence.Unfinished), RegistryAction.SkipUnchanged, DownloadRegistryPolicy.OperationUnfinished, auto: false);

    private static void ManualOnlySticks()
    {
        var manual = new RegistryRecord(2, "AwaitingApproval", Source, SameEval, ManualOnly: true);
        Expect(Decide(manual, OperationEvidence.None), RegistryAction.Evaluate, auto: false);
        Expect(Decide(manual with { State = "NoImprovement" }, OperationEvidence.None, evaluation: "changed"), RegistryAction.Evaluate, auto: false);
        Expect(Decide(manual, OperationEvidence.None, Source with { ContentDigest = "other" }), RegistryAction.Evaluate, auto: false);
    }

    private static void ConclusionNewContent() =>
        Expect(Decide(Record("NoImprovement"), OperationEvidence.None, Source with { ContentDigest = "digest-b" }), RegistryAction.Evaluate, auto: true);
}
