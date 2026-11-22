using System.Collections.Immutable;
using Crescendarr.Core.Inventory;

// Dependency-free executable specs for the read-only completed-download inventory (M10).
// Run: dotnet run --project tests/Crescendarr.Core.Inventory.Spec -c Release
// All values are synthetic. Nothing here touches storage, a network, a database or Lidarr.
internal static class Program
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ClientIdentity Sab = new(1, "Sabnzbd", "Synthetic SAB");
    private static readonly ClientIdentity Hole = new(2, "UsenetBlackhole", "Synthetic Blackhole");
    private const string Complete = "/downloads/complete";
    private const string Incomplete = "/downloads/incomplete";

    public static int Main()
    {
        var specs = new (string Name, Action Test)[]
        {
            ("P01 POSIX paths collapse repeated and trailing separators only", PosixCanonical),
            ("P02 POSIX rejects relative, dot, backslash, // root, control, bidi, invisible, NFD, URI, padded, oversize", PosixRejects),
            ("P03 Windows canonical drive form; UNC, device, ADS, reserved, trailing-dot, drive-relative rejected", WindowsPaths),
            ("P04 Root containment is segment-wise and strict", Containment),
            ("P05 Output outside every allowed root is reported, not trusted", OutsideRoots),
            ("Q01 Conservative policy validates", PolicyValid),
            ("Q02 Unsafe or unreachable policies are rejected with reasons", PolicyInvalid),
            ("Q03 Apply refuses an invalid policy", PolicyApplyThrows),
            ("L01 First sighting Observed, then StillDownloading, never actionable", ObservedThenDownloading),
            ("L02 CompletedStable needs MinStablePolls consecutive identical completed polls", StableAfterPolls),
            ("L03 Fast polls cannot satisfy MinStableAge", StableNeedsAge),
            ("L04 Age without enough polls is not stable", AgeWithoutPolls),
            ("L05 Completed with remaining bytes never stabilizes", RemainingBytes),
            ("L06 Unknown or zero size never stabilizes when RequireKnownSize", UnknownSize),
            ("L10 Unknown remaining bytes never stabilize", UnknownRemaining),
            ("L07 Size change while completed restarts stability", SizeChange),
            ("L08 Negative or inconsistent sizes require review", InvalidSizes),
            ("L09 No state or eligibility claims import, cleanup or removal", NoImportVocabulary),
            ("S01 Status regression after completion is sticky review", StatusRegression),
            ("S02 Client-reported failure requires review", ClientFailure),
            ("S03 Unknown or undefined status fails closed", UnknownStatus),
            ("S04 Flapping non-terminal statuses never become actionable", FlappingStatus),
            ("I01 Unkeyable download IDs are counted, not ingested", UnkeyableIds),
            ("I02 Identical duplicate entries are idempotent", IdenticalDuplicates),
            ("I03 Conflicting duplicates in one poll are Ambiguous", ConflictingDuplicates),
            ("I04 Same ID in two clients is Ambiguous in either poll order", CrossClientDuplicate),
            ("I05 Equal, case-variant or nested output paths collide", PathCollisions),
            ("I06 Reused ID with a different path after disappearance requires review", IdReuse),
            ("I07 Reappearance after Missing is sticky review even with the same path", Reappearance),
            ("I10 Same ID and path, different title and size, across a restart: review", ReappearanceAcrossRestart),
            ("I11 Discarded duplicate variant pointing at another item blocks that item", HiddenVariantCollision),
            ("I12 Review item whose current path moves onto another item blocks it", CurrentPathCollision),
            ("I13 Short absence then changed title or size requires review", ShortAbsenceChangedEvidence),
            ("I08 Implementation change for a definition ID is an identity conflict", IdentityChange),
            ("I09 Display-name change is harmless", RenameHarmless),
            ("X01 Poisoned path is sticky review with evidence retained", PoisonedPath),
            ("X02 Output outside allowed roots requires review", OutsidePathReview),
            ("X03 Path, casing and root changes after completion require review", PathChangeAfterCompletion),
            ("X04 Path may move before completion (incomplete -> complete)", PathMoveBeforeCompletion),
            ("X05 Completed without an output path requires review", CompletedWithoutPath),
            ("X06 Root removed from policy after restart blocks planning", RootRemovedAfterRestart),
            ("A01 One absence resets stability but is not Missing", SingleAbsence),
            ("A02 Repeated absence is MissingFromClient with evidence and NoAction", RepeatedAbsence),
            ("A03 GetItems failures never age items toward Missing", UnavailableKeepsEvidence),
            ("A04 Recovery after an outage re-proves stability", RecoveryReproves),
            ("A05 Failure reasons are sanitized evidence", FailureReasonSanitized),
            ("A06 Stale client inventory blocks planning", StaleInventory),
            ("A07 Succeeded poll without an items array is treated as missing data", MissingItemsArray),
            ("C01 Clock regression resets stability", ClockRegression),
            ("C02 Poll gap beyond MaxPollGap resets stability", PollGap),
            ("C03 Evaluation clock behind last success blocks planning", EvaluateClockAhead),
            ("C04 Implausible timestamps ingest nothing", ImplausibleTimestamps),
            ("R01 Replayed or older sequences return the identical state", Replay),
            ("R02 Encode/decode restart equals uninterrupted run", RestartEquivalence),
            ("R03 Adapter sequence reset is flagged at once and blocks readiness", SequenceReset),
            ("R04 Canonical encoding ignores input order", CanonicalOrder),
            ("R05 Decoder fails closed on tampering and malformed documents", DecoderRejects),
            ("R06 Evidence digest tracks record changes only", EvidenceDigest),
            ("H01 Stock CDH on: ingest nothing, completed items require review", StockCdhActive),
            ("H02 Unsupported client implementation ingests nothing", UnsupportedClient),
            ("H03 Capacity limit refuses new items but keeps existing evidence", Capacity),
            ("M01 Metamorphic: item permutations within a poll", PermutationInvariance),
            ("M02 Metamorphic: interleaving independent clients", InterleavingInvariance),
            ("M03 Adversarial fuzz: safety invariants hold for random poll streams", Fuzz),
        };

        var failures = 0;
        foreach (var (name, test) in specs)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }

        Console.WriteLine($"{specs.Length - failures}/{specs.Length} inventory specs passed");
        return failures == 0 ? 0 : 1;
    }

    // ---------- helpers ----------

    private static InventoryPolicy Policy(Func<InventoryPolicy, InventoryPolicy>? change = null)
    {
        var policy = InventoryPolicy.Conservative(PathStyle.Posix, [Complete, Incomplete], ["Sabnzbd", "UsenetBlackhole"]);
        return change is null ? policy : change(policy);
    }

    private static ClientItemSnapshot Item(string id, ClientItemStatus status = ClientItemStatus.Completed, string? path = null, long? total = 1000, long? remaining = 0, string? title = "Synthetic Album") =>
        new(id, title, "music", status, path ?? $"{Complete}/{id}", total, remaining);

    private static ClientPoll Poll(ClientIdentity client, long seq, double minutes, params ClientItemSnapshot[] items) =>
        new(client, seq, T0.AddMinutes(minutes), false, PollOutcome.Succeeded, items.ToImmutableArray());

    private static ClientPoll Failed(ClientIdentity client, long seq, double minutes, string reason = "synthetic timeout") =>
        new(client, seq, T0.AddMinutes(minutes), false, PollOutcome.ClientUnavailable, ImmutableArray<ClientItemSnapshot>.Empty, reason);

    private static InventoryState Run(InventoryState state, InventoryPolicy policy, params ClientPoll[] polls) =>
        polls.Aggregate(state, (s, p) => InventoryEngine.Apply(s, policy, p));

    private static InventoryState Run(params ClientPoll[] polls) => Run(InventoryState.Empty, Policy(), polls);

    private static ItemRecord Rec(InventoryState state, ClientIdentity client, string id) =>
        state.Items.TryGetValue(new ItemKey(client.DefinitionId, id), out var r) ? r : throw new InvalidOperationException($"no record {id}");

    private static ItemDecision Decision(InventoryState state, ClientIdentity client, string id, double minutes, InventoryPolicy? policy = null) =>
        InventoryEvaluator.Evaluate(state, policy ?? Policy(), T0.AddMinutes(minutes)).Items.Single(d => d.Key == new ItemKey(client.DefinitionId, id));

    private static ClientHealth Health(InventoryState state, ClientIdentity client, double minutes, InventoryPolicy? policy = null) =>
        InventoryEvaluator.Evaluate(state, policy ?? Policy(), T0.AddMinutes(minutes)).Clients.Single(c => c.DefinitionId == client.DefinitionId);

    /// <summary>Three completed polls at minutes 0, 1, 2: the minimum stable run under the conservative policy.</summary>
    private static InventoryState StableRun(string id = "job-1", string? path = null) =>
        Run(Poll(Sab, 1, 0, Item(id, path: path)), Poll(Sab, 2, 1, Item(id, path: path)), Poll(Sab, 3, 2, Item(id, path: path)));

    private static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}");
    }

    private static void HasReason(ItemRecord record, InventoryReason reason) =>
        True(record.Reasons.Contains(reason), $"{record.Key} reasons [{string.Join(",", record.Reasons)}] lack {reason}");

    // ---------- path ----------

    private static void PosixCanonical()
    {
        var check = PathCanonicalizer.Canonicalize("/downloads//complete/Album/", PathStyle.Posix);
        Eq(PathVerdict.Ok, check.Verdict, "verdict");
        Eq("/downloads/complete/Album", check.CanonicalPath, "canonical");
        Eq("/downloads/complete/Album", PathCanonicalizer.Canonicalize(check.CanonicalPath, PathStyle.Posix).CanonicalPath, "idempotent");
    }

    private static void PosixRejects()
    {
        var unsafePaths = new[]
        {
            "downloads/x", "/a/../b", "/a/./b", "/a/..", "//server/share", "/a\\b", "/a\u0000b", "/a\nb", "/a\u202Eb",
            "/a\u200Bb", "/cafe\u0301", "/a\uFFFDb", "file:///a", " /a", "/a ", "/" + new string('a', 5000), "/a\uD800b",
        };
        foreach (var raw in unsafePaths)
            Eq(PathVerdict.Unsafe, PathCanonicalizer.Canonicalize(raw, PathStyle.Posix).Verdict, $"unsafe '{InventoryEscape(raw)}'");
        foreach (var raw in new[] { null, "", "   " })
            Eq(PathVerdict.Missing, PathCanonicalizer.Canonicalize(raw, PathStyle.Posix).Verdict, "missing");
    }

    private static void WindowsPaths()
    {
        Eq(@"C:\Downloads\Complete", PathCanonicalizer.Canonicalize(@"c:/Downloads\\Complete\", PathStyle.Windows).CanonicalPath, "canonical");
        foreach (var raw in new[] { @"\\nas\share\x", @"\\?\C:\x", @"\\.\pipe\x", "C:x", @"C:\a:stream", @"C:\CON\x", @"C:\nul.txt", @"C:\a.\b", @"C:\a \b", @"C:\a<b", @"C:\..\x", "/posix/style" })
            Eq(PathVerdict.Unsafe, PathCanonicalizer.Canonicalize(raw, PathStyle.Windows).Verdict, $"unsafe '{raw}'");
    }

    private static void Containment()
    {
        True(PathCanonicalizer.IsStrictlyUnder("/downloads/complete/x", Complete, PathStyle.Posix), "child");
        True(!PathCanonicalizer.IsStrictlyUnder("/downloads/completed/x", Complete, PathStyle.Posix), "sibling prefix");
        True(!PathCanonicalizer.IsStrictlyUnder(Complete, Complete, PathStyle.Posix), "equal is not under");
        True(!PathCanonicalizer.IsStrictlyUnder("/Downloads/complete/x", Complete, PathStyle.Posix), "POSIX is case-sensitive");
        True(PathCanonicalizer.IsStrictlyUnder(@"C:\DL\x", @"C:\dl", PathStyle.Windows), "Windows is case-insensitive");
    }

    private static void OutsideRoots()
    {
        var check = PathCanonicalizer.CheckOutput("/mnt/other/album", PathStyle.Posix, [Complete]);
        Eq(PathVerdict.OutsideAllowedRoots, check.Verdict, "verdict");
        Eq(Complete, PathCanonicalizer.CheckOutput("/downloads/complete/a", PathStyle.Posix, [Complete]).MatchedRoot, "matched root");
    }

    // ---------- policy ----------

    private static void PolicyValid() => Eq(0, Policy().Validate().Length, "conservative policy errors");

    private static void PolicyInvalid()
    {
        var variants = new Func<InventoryPolicy, InventoryPolicy>[]
        {
            p => p with { MinStablePolls = 1 },
            p => p with { AllowedOutputRoots = ["/d", "/d/x"] },
            p => p with { AllowedOutputRoots = ["/d/"] },
            p => p with { AllowedOutputRoots = ["/"] },
            p => p with { AllowedOutputRoots = ["/d", "/d"] },
            p => p with { AllowedOutputRoots = ImmutableArray<string>.Empty },
            p => p with { SupportedImplementations = ImmutableArray<string>.Empty },
            p => p with { SupportedImplementations = ["Sab nzbd"] },
            p => p with { StaleAfter = TimeSpan.FromMinutes(1) },
            p => p with { MissingAfterPolls = 1 },
            p => p with { MinStableAge = TimeSpan.Zero },
            p => p with { MaxItemsPerClient = 0 },
        };
        for (var i = 0; i < variants.Length; i++)
            True(!Policy(variants[i]).Validate().IsEmpty, $"variant {i} accepted");
    }

    private static void PolicyApplyThrows() =>
        Throws<ArgumentException>(() => InventoryEngine.Apply(InventoryState.Empty, Policy(p => p with { MinStablePolls = 1 }), Poll(Sab, 1, 0, Item("a"))), "invalid policy");

    // ---------- lifecycle ----------

    private static void ObservedThenDownloading()
    {
        var s = Run(Poll(Sab, 1, 0, Item("a", ClientItemStatus.Downloading, total: 1000, remaining: 500)));
        Eq(InventoryItemState.Observed, Rec(s, Sab, "a").State, "first");
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 0.5).Eligibility, "first eligibility");
        s = Run(s, Policy(), Poll(Sab, 2, 1, Item("a", ClientItemStatus.Downloading, total: 1000, remaining: 100)));
        Eq(InventoryItemState.StillDownloading, Rec(s, Sab, "a").State, "second");
    }

    private static void StableAfterPolls()
    {
        var two = Run(Poll(Sab, 1, 0, Item("a")), Poll(Sab, 2, 1, Item("a")));
        Eq(InventoryItemState.CompletedSettling, Rec(two, Sab, "a").State, "two polls");
        var three = Run(two, Policy(), Poll(Sab, 3, 2, Item("a")));
        Eq(InventoryItemState.CompletedStable, Rec(three, Sab, "a").State, "three polls");
        Eq(PlanningEligibility.ReadyForPlanning, Decision(three, Sab, "a", 2.5).Eligibility, "eligible");
    }

    private static void StableNeedsAge()
    {
        var s = Run(Poll(Sab, 1, 0, Item("a")), Poll(Sab, 2, 0.1, Item("a")), Poll(Sab, 3, 0.2, Item("a")), Poll(Sab, 4, 0.3, Item("a")));
        Eq(InventoryItemState.CompletedSettling, Rec(s, Sab, "a").State, "fast polls");
        s = Run(s, Policy(), Poll(Sab, 5, 2, Item("a")));
        Eq(InventoryItemState.CompletedStable, Rec(s, Sab, "a").State, "age reached");
    }

    private static void AgeWithoutPolls()
    {
        var s = Run(Poll(Sab, 1, 0, Item("a")), Poll(Sab, 2, 4, Item("a")));
        Eq(InventoryItemState.CompletedSettling, Rec(s, Sab, "a").State, "two polls four minutes apart");
    }

    private static void RemainingBytes()
    {
        var polls = Enumerable.Range(1, 8).Select(i => Poll(Sab, i, i, Item("a", remaining: 10))).ToArray();
        var r = Rec(Run(polls), Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "state");
        HasReason(r, InventoryReason.CompletedWithRemainingBytes);
    }

    private static void UnknownSize()
    {
        foreach (var total in new long?[] { null, 0 })
        {
            var polls = Enumerable.Range(1, 8).Select(i => Poll(Sab, i, i, Item("a", total: total, remaining: 0))).ToArray();
            var r = Rec(Run(polls), Sab, "a");
            Eq(InventoryItemState.CompletedSettling, r.State, $"state for total {total?.ToString() ?? "null"}");
            HasReason(r, InventoryReason.SizeUnknown);
        }
    }

    private static void SizeChange()
    {
        var s = Run(StableRun("a"), Policy(), Poll(Sab, 4, 3, Item("a", total: 2000)));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "after change");
        HasReason(r, InventoryReason.SizeChangedWhileCompleted);
        s = Run(s, Policy(), Poll(Sab, 5, 4, Item("a", total: 2000)));
        Eq(InventoryItemState.CompletedSettling, Rec(s, Sab, "a").State, "one more poll");
        s = Run(s, Policy(), Poll(Sab, 6, 5, Item("a", total: 2000)));
        Eq(InventoryItemState.CompletedStable, Rec(s, Sab, "a").State, "re-proven");
    }

    private static void UnknownRemaining()
    {
        var polls = Enumerable.Range(1, 8).Select(i => Poll(Sab, i, i, Item("a", total: 1000, remaining: null))).ToArray();
        var s = Run(polls);
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "state");
        HasReason(r, InventoryReason.RemainingBytesUnknown);
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 8.5).Eligibility, "eligibility");
    }

    private static void InvalidSizes()
    {
        foreach (var (total, remaining) in new (long?, long?)[] { (-1, 0), (100, -1), (100, 200) })
        {
            var r = Rec(Run(Poll(Sab, 1, 0, Item("a", total: total, remaining: remaining))), Sab, "a");
            Eq(InventoryItemState.RequiresReview, r.State, $"size {total}/{remaining}");
            HasReason(r, InventoryReason.SizeInvalid);
        }
    }

    private static void NoImportVocabulary()
    {
        string[] banned = ["Import", "Clear", "Clean", "Remove", "Delete", "Settled", "Done"];
        foreach (var name in Enum.GetNames<InventoryItemState>().Concat(Enum.GetNames<PlanningEligibility>()))
            True(!banned.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase)), $"misleading name {name}");
    }

    // ---------- status ----------

    private static void StatusRegression()
    {
        var s = Run(StableRun("a"), Policy(), Poll(Sab, 4, 3, Item("a", ClientItemStatus.Downloading)));
        HasReason(Rec(s, Sab, "a"), InventoryReason.StatusRegressedAfterCompletion);
        s = Run(s, Policy(), Poll(Sab, 5, 4, Item("a")), Poll(Sab, 6, 5, Item("a")), Poll(Sab, 7, 6, Item("a")));
        Eq(InventoryItemState.RequiresReview, Rec(s, Sab, "a").State, "sticky");
        Eq(PlanningEligibility.OperatorReview, Decision(s, Sab, "a", 6.5).Eligibility, "eligibility");
    }

    private static void ClientFailure()
    {
        var r = Rec(Run(Poll(Sab, 1, 0, Item("a", ClientItemStatus.Failed))), Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "state");
        HasReason(r, InventoryReason.ClientReportedFailure);
    }

    private static void UnknownStatus()
    {
        foreach (var status in new[] { ClientItemStatus.Unknown, (ClientItemStatus)99 })
        {
            var r = Rec(Run(Poll(Sab, 1, 0, Item("a", status))), Sab, "a");
            Eq(InventoryItemState.RequiresReview, r.State, $"status {(int)status}");
            HasReason(r, InventoryReason.UnsupportedStatus);
        }
    }

    private static void FlappingStatus()
    {
        var cycle = new[] { ClientItemStatus.Queued, ClientItemStatus.Paused, ClientItemStatus.Downloading, ClientItemStatus.Warning };
        var polls = Enumerable.Range(1, 20).Select(i => Poll(Sab, i, i, Item("a", cycle[i % cycle.Length], remaining: 10))).ToArray();
        var s = Run(polls);
        Eq(InventoryItemState.StillDownloading, Rec(s, Sab, "a").State, "state");
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 20.5).Eligibility, "eligibility");
    }

    // ---------- identity ----------

    private static void UnkeyableIds()
    {
        var bad = new ClientItemSnapshot?[]
        {
            Item("") , Item(" padded"), Item("line\nbreak"), Item(new string('x', 300)), Item("rtl\u202Eid"), null,
        };
        var items = bad.Append(Item("good")).ToImmutableArray();
        var s = InventoryEngine.Apply(InventoryState.Empty, Policy(), new ClientPoll(Sab, 1, T0, false, PollOutcome.Succeeded, items!));
        Eq(1, s.Items.Count, "only the keyable item is tracked");
        Eq(6L, s.Clients[Sab.DefinitionId].UnkeyableItemsTotal, "unkeyable count retained as evidence");
        Eq(ClientHealthStatus.Degraded, Health(s, Sab, 0.5).Status, "health");
    }

    private static void IdenticalDuplicates()
    {
        var single = Run(Poll(Sab, 1, 0, Item("a")), Poll(Sab, 2, 1, Item("a")));
        var doubled = Run(Poll(Sab, 1, 0, Item("a"), Item("a")), Poll(Sab, 2, 1, Item("a"), Item("a"), Item("a")));
        Eq(InventoryCodec.Encode(single), InventoryCodec.Encode(doubled), "duplicate entries");
    }

    private static void ConflictingDuplicates()
    {
        var r = Rec(Run(Poll(Sab, 1, 0, Item("a"), Item("a", total: 999))), Sab, "a");
        Eq(InventoryItemState.Ambiguous, r.State, "state");
        HasReason(r, InventoryReason.DuplicateIdInPoll);
    }

    private static void CrossClientDuplicate()
    {
        var a = Poll(Sab, 1, 0, Item("same", path: $"{Complete}/sab-copy"));
        var b = Poll(Hole, 1, 0, Item("same", path: $"{Complete}/hole-copy"));
        var ab = Run(a, b);
        var ba = Run(b, a);
        Eq(InventoryCodec.Encode(ab), InventoryCodec.Encode(ba), "order independent");
        foreach (var client in new[] { Sab, Hole })
        {
            Eq(InventoryItemState.Ambiguous, Rec(ab, client, "same").State, $"{client.Name} state");
            HasReason(Rec(ab, client, "same"), InventoryReason.DuplicateIdAcrossClients);
        }
    }

    private static void PathCollisions()
    {
        var cases = new[]
        {
            ($"{Complete}/album", $"{Complete}/album"),
            ($"{Complete}/album", $"{Complete}/ALBUM"),
            ($"{Complete}/album", $"{Complete}/album/disc1"),
        };
        foreach (var (first, second) in cases)
        {
            var s = Run(Poll(Sab, 1, 0, Item("x", path: first)), Poll(Hole, 1, 0, Item("y", path: second)));
            HasReason(Rec(s, Sab, "x"), InventoryReason.OutputPathCollision);
            HasReason(Rec(s, Hole, "y"), InventoryReason.OutputPathCollision);
        }

        var distinct = Run(Poll(Sab, 1, 0, Item("x", path: $"{Complete}/album")), Poll(Sab, 2, 1, Item("x", path: $"{Complete}/album"), Item("y", path: $"{Complete}/album-2")));
        True(!Rec(distinct, Sab, "y").Reasons.Contains(InventoryReason.OutputPathCollision), "sibling prefix is not a collision");
    }

    private static InventoryState GoneFor3(InventoryState s, long fromSeq, double fromMinute) =>
        Run(s, Policy(), Poll(Sab, fromSeq, fromMinute), Poll(Sab, fromSeq + 1, fromMinute + 1), Poll(Sab, fromSeq + 2, fromMinute + 2));

    private static void IdReuse()
    {
        var s = GoneFor3(StableRun("a"), 4, 3);
        Eq(InventoryItemState.MissingFromClient, Rec(s, Sab, "a").State, "missing");
        s = Run(s, Policy(), Poll(Sab, 7, 6, Item("a", path: $"{Complete}/a-new-job")));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "reused");
        HasReason(r, InventoryReason.IdReusedWithDifferentPath);
    }

    private static void Reappearance()
    {
        var s = Run(GoneFor3(StableRun("a"), 4, 3), Policy(), Poll(Sab, 7, 6, Item("a")));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "reappeared");
        HasReason(r, InventoryReason.ReappearedAfterMissing);
        s = Run(s, Policy(), Poll(Sab, 8, 7, Item("a")), Poll(Sab, 9, 8, Item("a")), Poll(Sab, 10, 9, Item("a")));
        Eq(InventoryItemState.RequiresReview, Rec(s, Sab, "a").State, "never requalifies automatically");
        Eq(PlanningEligibility.OperatorReview, Decision(s, Sab, "a", 9.5).Eligibility, "eligibility");
    }

    private static void ReappearanceAcrossRestart()
    {
        var restored = InventoryCodec.Decode(InventoryCodec.Encode(GoneFor3(StableRun("a"), 4, 3)));
        True(restored.Succeeded, "decode");
        var impostor = Item("a", total: 4242, title: "A different album");
        var s = Run(restored.State!, Policy(), Poll(Sab, 7, 6, impostor), Poll(Sab, 8, 7, impostor), Poll(Sab, 9, 8, impostor));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "state");
        HasReason(r, InventoryReason.ReappearedAfterMissing);
        Eq(PlanningEligibility.OperatorReview, Decision(s, Sab, "a", 8.5).Eligibility, "eligibility");
    }

    private static InventoryState TwoStable() => Run(
        Poll(Sab, 1, 0, Item("a"), Item("b")), Poll(Sab, 2, 1, Item("a"), Item("b")), Poll(Sab, 3, 2, Item("a"), Item("b")));

    private static void HiddenVariantCollision()
    {
        var before = TwoStable();
        Eq(PlanningEligibility.ReadyForPlanning, Decision(before, Sab, "b", 2.5).Eligibility, "b ready before");
        var s = Run(before, Policy(), Poll(Sab, 4, 3, Item("a"), Item("a", path: $"{Complete}/b"), Item("b")));
        True(Rec(s, Sab, "a").VariantPaths.Contains($"{Complete}/b"), "variant path retained as evidence");
        HasReason(Rec(s, Sab, "b"), InventoryReason.OutputPathCollision);
        True(Decision(s, Sab, "b", 3.5).Eligibility != PlanningEligibility.ReadyForPlanning, "b must not stay ready");
        var after = InventoryCodec.Decode(InventoryCodec.Encode(s));
        True(after.Succeeded, $"decode: {after.Error}");
        Eq(InventoryCodec.Encode(s), InventoryCodec.Encode(after.State!), "variant evidence survives restart");
    }

    private static void CurrentPathCollision()
    {
        var s = Run(TwoStable(), Policy(), Poll(Sab, 4, 3, Item("a", path: $"{Complete}/b"), Item("b")));
        Eq(InventoryItemState.RequiresReview, Rec(s, Sab, "a").State, "a changed path");
        HasReason(Rec(s, Sab, "b"), InventoryReason.OutputPathCollision);
        Eq(PlanningEligibility.OperatorReview, Decision(s, Sab, "b", 3.5).Eligibility, "b blocked");
    }

    private static void ShortAbsenceChangedEvidence()
    {
        foreach (var changed in new[] { Item("a", title: "Other title"), Item("a", total: 2000) })
        {
            var s = Run(StableRun("a"), Policy(), Poll(Sab, 4, 3), Poll(Sab, 5, 4, changed));
            var r = Rec(s, Sab, "a");
            Eq(InventoryItemState.RequiresReview, r.State, "state");
            HasReason(r, InventoryReason.ReappearedWithChangedEvidence);
        }

        var same = Run(StableRun("a"), Policy(), Poll(Sab, 4, 3), Poll(Sab, 5, 4, Item("a")));
        Eq(InventoryItemState.CompletedSettling, Rec(same, Sab, "a").State, "unchanged return only re-proves stability");
    }

    private static void IdentityChange()
    {
        var impostor = new ClientIdentity(Sab.DefinitionId, "UsenetBlackhole", "Replaced provider row");
        var s = Run(StableRun("a"), Policy(), Poll(impostor, 4, 3, Item("b")));
        Eq(InventoryItemState.RequiresReview, Rec(s, Sab, "a").State, "existing item");
        HasReason(Rec(s, Sab, "a"), InventoryReason.ClientIdentityChanged);
        True(!s.Items.ContainsKey(new ItemKey(Sab.DefinitionId, "b")), "impostor items not ingested");
        Eq("Sabnzbd", s.Clients[Sab.DefinitionId].Implementation, "original identity kept");
        Eq(ClientHealthStatus.IdentityConflict, Health(s, Sab, 3.5).Status, "health");
    }

    private static void RenameHarmless()
    {
        var renamed = Sab with { Name = "Renamed SAB" };
        var s = Run(Poll(Sab, 1, 0, Item("a")), Poll(renamed, 2, 1, Item("a")), Poll(renamed, 3, 2, Item("a")));
        Eq(InventoryItemState.CompletedStable, Rec(s, Sab, "a").State, "state");
        Eq("Renamed SAB", s.Clients[Sab.DefinitionId].Name, "name");
    }

    // ---------- paths ----------

    private static void PoisonedPath()
    {
        var s = Run(Poll(Sab, 1, 0, Item("a", path: $"{Complete}/../../etc/passwd")));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "state");
        HasReason(r, InventoryReason.OutputPathUnsafe);
        Eq($"{Complete}/../../etc/passwd", r.RawPathEvidence, "evidence");
        s = Run(s, Policy(), Poll(Sab, 2, 1, Item("a")), Poll(Sab, 3, 2, Item("a")), Poll(Sab, 4, 3, Item("a")));
        Eq(InventoryItemState.RequiresReview, Rec(s, Sab, "a").State, "sticky");
    }

    private static void OutsidePathReview()
    {
        var r = Rec(Run(Poll(Sab, 1, 0, Item("a", path: "/mnt/nas/music/album"))), Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "state");
        HasReason(r, InventoryReason.OutputPathOutsideAllowedRoots);
    }

    private static void PathChangeAfterCompletion()
    {
        var changed = Rec(Run(StableRun("a"), Policy(), Poll(Sab, 4, 3, Item("a", path: $"{Complete}/elsewhere"))), Sab, "a");
        HasReason(changed, InventoryReason.OutputPathChangedAfterCompletion);
        var cased = Rec(Run(StableRun("a"), Policy(), Poll(Sab, 4, 3, Item("a", path: $"{Complete}/A"))), Sab, "a");
        HasReason(cased, InventoryReason.OutputPathCasingChanged);
        var moved = Rec(Run(StableRun("a"), Policy(), Poll(Sab, 4, 3, Item("a", path: $"{Incomplete}/a"))), Sab, "a");
        HasReason(moved, InventoryReason.OutputRootChanged);
        foreach (var r in new[] { changed, cased, moved }) Eq(InventoryItemState.RequiresReview, r.State, "state");
    }

    private static void PathMoveBeforeCompletion()
    {
        var s = Run(
            Poll(Sab, 1, 0, Item("a", ClientItemStatus.Downloading, path: $"{Incomplete}/a", remaining: 400)),
            Poll(Sab, 2, 1, Item("a")), Poll(Sab, 3, 2, Item("a")), Poll(Sab, 4, 3, Item("a")));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedStable, r.State, "state");
        Eq($"{Complete}/a", r.AnchoredPath, "anchored at completion");
    }

    private static void CompletedWithoutPath()
    {
        var snapshot = Item("a") with { OutputPath = null };
        var r = Rec(Run(Poll(Sab, 1, 0, snapshot)), Sab, "a");
        Eq(InventoryItemState.RequiresReview, r.State, "state");
        HasReason(r, InventoryReason.OutputPathMissing);
        var downloading = Item("b", ClientItemStatus.Downloading, remaining: 10) with { OutputPath = null };
        Eq(InventoryItemState.Observed, Rec(Run(Poll(Sab, 1, 0, downloading)), Sab, "b").State, "no path while downloading is normal");
    }

    private static void RootRemovedAfterRestart()
    {
        var decoded = InventoryCodec.Decode(InventoryCodec.Encode(StableRun("a")));
        True(decoded.Succeeded, "decode");
        var narrowed = Policy(p => p with { AllowedOutputRoots = [Incomplete] });
        var decision = Decision(decoded.State!, Sab, "a", 2.5, narrowed);
        Eq(PlanningEligibility.OperatorReview, decision.Eligibility, "eligibility");
        True(decision.Reasons.Contains(InventoryReason.OutputRootNoLongerAllowed), "reason");
    }

    // ---------- absence and availability ----------

    private static void SingleAbsence()
    {
        var s = Run(StableRun("a"), Policy(), Poll(Sab, 4, 3));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "downgraded");
        HasReason(r, InventoryReason.NotSeenInLatestPoll);
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 3.5).Eligibility, "eligibility");
    }

    private static void RepeatedAbsence()
    {
        var s = GoneFor3(StableRun("a"), 4, 3);
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.MissingFromClient, r.State, "missing");
        Eq($"{Complete}/a", r.AnchoredPath, "path evidence retained");
        Eq(ClientItemStatus.Completed, r.LastStatus, "status evidence retained");
        Eq(PlanningEligibility.NoAction, Decision(s, Sab, "a", 5.5).Eligibility, "absence permits nothing");
    }

    private static void UnavailableKeepsEvidence()
    {
        var polls = Enumerable.Range(4, 10).Select(i => Failed(Sab, i, i - 1)).ToArray();
        var s = Run(StableRun("a"), Policy(), polls);
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "not missing");
        Eq(0, r.ConsecutiveAbsences, "absences");
        Eq($"{Complete}/a", r.AnchoredPath, "evidence");
        Eq(ClientHealthStatus.Unavailable, Health(s, Sab, 12.5).Status, "health");
        Eq(10, Health(s, Sab, 12.5).ConsecutiveFailures, "failure count");
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 12.5).Eligibility, "eligibility");
    }

    private static void RecoveryReproves()
    {
        var s = Run(StableRun("a"), Policy(), Failed(Sab, 4, 3), Poll(Sab, 5, 4, Item("a")));
        Eq(InventoryItemState.CompletedSettling, Rec(s, Sab, "a").State, "first poll after outage");
        s = Run(s, Policy(), Poll(Sab, 6, 5, Item("a")), Poll(Sab, 7, 6, Item("a")));
        Eq(InventoryItemState.CompletedStable, Rec(s, Sab, "a").State, "re-proven");
    }

    private static void FailureReasonSanitized()
    {
        var s = Run(Failed(Sab, 1, 0, "boom\n\u202Eevil\u0007"));
        var reason = s.Clients[Sab.DefinitionId].LastFailureReason!;
        True(!reason.Any(char.IsControl) && !reason.Contains('\u202E'), $"sanitized '{InventoryEscape(reason)}'");
    }

    private static void StaleInventory()
    {
        var s = StableRun("a");
        Eq(ClientHealthStatus.Stale, Health(s, Sab, 20).Status, "health");
        var decision = Decision(s, Sab, "a", 20);
        Eq(PlanningEligibility.Wait, decision.Eligibility, "eligibility");
        True(decision.Reasons.Contains(InventoryReason.ClientStale), "reason");
    }

    private static void MissingItemsArray()
    {
        var broken = new ClientPoll(Sab, 4, T0.AddMinutes(3), false, PollOutcome.Succeeded, default);
        var s = Run(StableRun("a"), Policy(), broken, broken with { Sequence = 5, ObservedAt = T0.AddMinutes(4) }, broken with { Sequence = 6, ObservedAt = T0.AddMinutes(5) });
        Eq(0, Rec(s, Sab, "a").ConsecutiveAbsences, "absences");
        True(Rec(s, Sab, "a").State != InventoryItemState.MissingFromClient, "not missing");
        Eq(ClientHealthStatus.Unavailable, Health(s, Sab, 5.5).Status, "health");
    }

    // ---------- clocks ----------

    private static void ClockRegression()
    {
        var s = Run(StableRun("a"), Policy(), Poll(Sab, 4, 1.5, Item("a")));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "reset");
        HasReason(r, InventoryReason.StabilityResetClockRegression);
        True(s.Clients[Sab.DefinitionId].LastPollReasons.Contains(InventoryReason.ClockRegressed), "client flag");
    }

    private static void PollGap()
    {
        var s = Run(Poll(Sab, 1, 0, Item("a")), Poll(Sab, 2, 1, Item("a")), Poll(Sab, 3, 20, Item("a")));
        var r = Rec(s, Sab, "a");
        Eq(InventoryItemState.CompletedSettling, r.State, "reset");
        HasReason(r, InventoryReason.StabilityResetPollGap);
        Eq(1, r.StablePollCount, "count restarted");
    }

    private static void EvaluateClockAhead()
    {
        var s = StableRun("a");
        Eq(ClientHealthStatus.ClockAhead, Health(s, Sab, 1).Status, "health");
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 1).Eligibility, "eligibility");
    }

    private static void ImplausibleTimestamps()
    {
        foreach (var when in new[] { DateTimeOffset.MinValue, new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero) })
        {
            var s = InventoryEngine.Apply(StableRun("a"), Policy(), new ClientPoll(Sab, 4, when, false, PollOutcome.Succeeded, [Item("b")]));
            True(!s.Items.ContainsKey(new ItemKey(Sab.DefinitionId, "b")), "not ingested");
            Eq(InventoryItemState.CompletedSettling, Rec(s, Sab, "a").State, "stability reset");
            True(s.Clients[Sab.DefinitionId].LastPollReasons.Contains(InventoryReason.InvalidTimestamp), "flag");
        }
    }

    // ---------- replay, restart, persistence ----------

    private static void Replay()
    {
        var s = StableRun("a");
        True(ReferenceEquals(s, InventoryEngine.Apply(s, Policy(), Poll(Sab, 3, 2, Item("a")))), "same sequence");
        True(ReferenceEquals(s, InventoryEngine.Apply(s, Policy(), Poll(Sab, 1, 0.5, Item("zzz")))), "late delivery of an older poll");
        True(ReferenceEquals(s, InventoryEngine.Apply(s, Policy(), Failed(Sab, 2, 1.5))), "late delivery of an older failure");
    }

    private static void RestartEquivalence()
    {
        var polls = new[]
        {
            Poll(Sab, 1, 0, Item("a", ClientItemStatus.Downloading, remaining: 10)), Poll(Hole, 1, 0.2, Item("h")),
            Poll(Sab, 2, 1, Item("a")), Failed(Hole, 2, 1.2), Poll(Sab, 3, 2, Item("a")), Poll(Hole, 3, 2.2, Item("h")),
            Poll(Sab, 4, 3, Item("a"), Item("b")), Poll(Hole, 4, 3.2), Poll(Sab, 5, 4, Item("a"), Item("b")), Poll(Hole, 5, 4.2),
        };
        var uninterrupted = Run(polls);
        for (var cut = 0; cut <= polls.Length; cut++)
        {
            var before = Run(polls.Take(cut).ToArray());
            var decoded = InventoryCodec.Decode(InventoryCodec.Encode(before));
            True(decoded.Succeeded, $"decode at {cut}: {decoded.Error}");
            var resumed = Run(decoded.State!, Policy(), polls.Skip(cut).ToArray());
            Eq(InventoryCodec.Encode(uninterrupted), InventoryCodec.Encode(resumed), $"restart at {cut}");
        }
    }

    private static void SequenceReset()
    {
        var restored = InventoryCodec.Decode(InventoryCodec.Encode(StableRun("a"))).State!;
        Eq(PlanningEligibility.ReadyForPlanning, Decision(restored, Sab, "a", 2.5).Eligibility, "ready before the reset");
        var s = Run(restored, Policy(), Poll(Sab, 0, 2.4, Item("new")));
        True(!s.Items.ContainsKey(new ItemKey(Sab.DefinitionId, "new")), "nothing ingested from a reset adapter");
        Eq(ClientHealthStatus.SequenceReset, Health(s, Sab, 2.5).Status, "flagged immediately, not after StaleAfter");
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 2.5).Eligibility, "previously ready item blocked at once");
        s = Run(s, Policy(), Poll(Sab, 4, 3, Item("a")), Poll(Sab, 5, 4, Item("a")), Poll(Sab, 6, 5, Item("a")));
        Eq(ClientHealthStatus.SequenceReset, Health(s, Sab, 5.5).Status, "sticky until operator review");
        Eq(PlanningEligibility.Wait, Decision(s, Sab, "a", 5.5).Eligibility, "still blocked");
    }

    private static void CanonicalOrder()
    {
        var items = new[] { Item("c"), Item("a"), Item("b", ClientItemStatus.Downloading, remaining: 5) };
        var one = Run(Poll(Sab, 1, 0, items), Poll(Hole, 1, 0, Item("z")));
        var two = Run(Poll(Hole, 1, 0, Item("z")), Poll(Sab, 1, 0, items.Reverse().ToArray()));
        Eq(InventoryCodec.Encode(one), InventoryCodec.Encode(two), "encoding");
    }

    private static void DecoderRejects()
    {
        var good = InventoryCodec.Encode(StableRun("job-1"));
        True(InventoryCodec.Decode(good).Succeeded, "baseline decodes");

        var tampered = new Dictionary<string, string>
        {
            ["digest mismatch"] = good.Replace("job-1", "job-2", StringComparison.Ordinal),
            ["schema"] = good.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            ["format"] = good.Replace(InventoryCodec.Format, "other.format", StringComparison.Ordinal),
            ["unknown property"] = good.Replace("\"payload\":{", "\"payload\":{\"extra\":1,", StringComparison.Ordinal),
            ["duplicate property"] = good.Replace("\"format\":", "\"format\":\"x\",\"format\":", StringComparison.Ordinal),
            ["numeric enum"] = good.Replace("\"state\":\"CompletedStable\"", "\"state\":\"4\"", StringComparison.Ordinal),
            ["invented state"] = good.Replace("\"state\":\"CompletedStable\"", "\"state\":\"Imported\"", StringComparison.Ordinal),
            ["truncated"] = good[..(good.Length / 2)],
            ["comment"] = "/*x*/" + good,
            ["empty"] = "",
            ["not json"] = "Imported",
        };
        foreach (var (name, text) in tampered)
        {
            True(text != good || name == "empty", $"{name} mutation applied");
            var result = InventoryCodec.Decode(text);
            True(!result.Succeeded && result.State is null && result.Error is not null, $"{name} accepted");
        }

        var orphan = new InventoryState(
            ImmutableSortedDictionary<int, ClientRecord>.Empty,
            StableRun("job-1").Items);
        True(!InventoryCodec.Decode(InventoryCodec.Encode(orphan)).Succeeded, "item without client rejected");

        var forgedStable = StableRun("job-1");
        var key = new ItemKey(Sab.DefinitionId, "job-1");
        forgedStable = forgedStable with { Items = forgedStable.Items.SetItem(key, forgedStable.Items[key] with { StablePollCount = 1 }) };
        True(!InventoryCodec.Decode(InventoryCodec.Encode(forgedStable)).Succeeded, "stable without evidence rejected");
    }

    private static void EvidenceDigest()
    {
        var r = Rec(StableRun("a"), Sab, "a");
        Eq(InventoryCodec.EvidenceDigest(r), InventoryCodec.EvidenceDigest(r with { }), "stable");
        True(InventoryCodec.EvidenceDigest(r) != InventoryCodec.EvidenceDigest(r with { TotalBytes = 1001 }), "changes");
        Eq(64, InventoryCodec.EvidenceDigest(r).Length, "sha-256 hex");
    }

    // ---------- stock CDH and provider limits ----------

    private static void StockCdhActive()
    {
        var s = Run(StableRun("a"), Policy(), Poll(Sab, 4, 3, Item("a"), Item("d", ClientItemStatus.Downloading, remaining: 5)));
        s = Run(s, Policy(), Poll(Sab, 5, 4, Item("a"), Item("d", ClientItemStatus.Downloading, remaining: 5), Item("new")) with { HostCompletedDownloadHandlingEnabled = true });
        True(!s.Items.ContainsKey(new ItemKey(Sab.DefinitionId, "new")), "nothing ingested while stock CDH is on");
        HasReason(Rec(s, Sab, "a"), InventoryReason.StockHandlingWasActive);
        Eq(InventoryItemState.RequiresReview, Rec(s, Sab, "a").State, "completed item");
        Eq(InventoryItemState.Observed, Rec(s, Sab, "d").State, "downloading item untouched (not advanced while stock CDH is on)");
        Eq(ClientHealthStatus.StockHandlingActive, Health(s, Sab, 4.5).Status, "health");
    }

    private static void UnsupportedClient()
    {
        var qbit = new ClientIdentity(3, "QBittorrent", "Synthetic qBit");
        var s = Run(Poll(qbit, 1, 0, Item("q")));
        True(s.Items.IsEmpty, "nothing ingested");
        Eq(ClientHealthStatus.Unsupported, Health(s, qbit, 0.5).Status, "health");
    }

    private static void Capacity()
    {
        var policy = Policy(p => p with { MaxItemsPerClient = 2 });
        var s = Run(InventoryState.Empty, policy, Poll(Sab, 1, 0, Item("a"), Item("b")), Poll(Sab, 2, 1, Item("a"), Item("b"), Item("c")));
        Eq(2, s.Items.Count, "capped");
        Eq(InventoryItemState.CompletedSettling, Rec(s, Sab, "a").State, "existing still updated");
        Eq(ClientHealthStatus.Degraded, Health(s, Sab, 1.5, policy).Status, "health");
    }

    // ---------- metamorphic and adversarial ----------

    private static void PermutationInvariance()
    {
        var items = Enumerable.Range(0, 12).Select(i => Item($"job-{i:00}", i % 3 == 0 ? ClientItemStatus.Downloading : ClientItemStatus.Completed, remaining: i % 3 == 0 ? 5 : 0))
            .Append(Item("job-03", total: 1)) // conflicting duplicate
            .Append(Item("job-05"))           // identical duplicate
            .Append(Item("bad", path: "/downloads/complete/../x"))
            .ToArray();
        var expected = InventoryCodec.Encode(Run(Poll(Sab, 1, 0, items), Poll(Sab, 2, 1, items)));
        for (var seed = 1; seed <= 25; seed++)
        {
            var rng = new Random(seed);
            var first = items.OrderBy(_ => rng.Next()).ToArray();
            var second = items.OrderBy(_ => rng.Next()).ToArray();
            Eq(expected, InventoryCodec.Encode(Run(Poll(Sab, 1, 0, first), Poll(Sab, 2, 1, second))), $"seed {seed}");
        }
    }

    private static void InterleavingInvariance()
    {
        var sab = Enumerable.Range(1, 6).Select(i => Poll(Sab, i, i, Item("s1"), Item("s2", ClientItemStatus.Downloading, remaining: 3))).ToList();
        var hole = Enumerable.Range(1, 6).Select(i => i == 3 ? Failed(Hole, i, i + 0.5) : Poll(Hole, i, i + 0.5, Item("h1"))).ToList();
        var expected = InventoryCodec.Encode(Run(sab.Concat(hole).ToArray()));
        for (var seed = 1; seed <= 25; seed++)
        {
            var rng = new Random(seed);
            var merged = new List<ClientPoll>();
            int i = 0, j = 0;
            while (i < sab.Count || j < hole.Count)
                merged.Add(j >= hole.Count || (i < sab.Count && rng.Next(2) == 0) ? sab[i++] : hole[j++]);
            Eq(expected, InventoryCodec.Encode(Run(merged.ToArray())), $"seed {seed}");
        }
    }

    private static void Fuzz()
    {
        var paths = new[] { $"{Complete}/x", $"{Complete}/y", $"{Complete}/X", $"{Incomplete}/x", $"{Complete}/x/sub", "/mnt/escape", $"{Complete}/../x", null };
        var ids = new[] { "a", "b", "c", "shared" };
        var statuses = Enum.GetValues<ClientItemStatus>().Append((ClientItemStatus)42).ToArray();
        var policy = Policy();

        for (var seed = 1; seed <= 200; seed++)
        {
            var rng = new Random(seed);
            var state = InventoryState.Empty;
            var sequence = new Dictionary<int, long> { [1] = 0, [2] = 0 };
            var minutes = 0.0;
            var poisoned = new HashSet<ItemKey>();
            ClientPoll? last = null;

            for (var step = 0; step < 40; step++)
            {
                var client = rng.Next(2) == 0 ? Sab : Hole;
                minutes += rng.Next(10) switch { 0 => -1.5, 1 => 9, _ => rng.NextDouble() * 1.5 };
                ClientPoll poll;
                if (last is not null && rng.Next(8) == 0)
                {
                    poll = last; // replay
                }
                else if (rng.Next(6) == 0)
                {
                    poll = Failed(client, ++sequence[client.DefinitionId], minutes, "fuzz");
                }
                else
                {
                    var count = rng.Next(4);
                    var snapshots = Enumerable.Range(0, count).Select(_ =>
                        Item(ids[rng.Next(ids.Length)], statuses[rng.Next(statuses.Length)], paths[rng.Next(paths.Length)],
                            total: rng.Next(5) == 0 ? null : 1000, remaining: rng.Next(6) switch { 0 => 7, 1 => null, _ => 0 })
                        with { OutputPath = paths[rng.Next(paths.Length)] }).ToArray();
                    poll = Poll(client, ++sequence[client.DefinitionId], minutes, snapshots);
                    foreach (var snap in snapshots.Where(s => s.OutputPath is not null && PathCanonicalizer.CheckOutput(s.OutputPath, PathStyle.Posix, policy.AllowedOutputRoots).Verdict is PathVerdict.Unsafe or PathVerdict.OutsideAllowedRoots))
                        poisoned.Add(new ItemKey(client.DefinitionId, snap.DownloadId!));
                }

                var next = InventoryEngine.Apply(state, policy, poll);
                if (ReferenceEquals(poll, last)) True(ReferenceEquals(next, state), $"seed {seed} step {step}: replay changed state");
                state = next;
                last = poll;

                var decoded = InventoryCodec.Decode(InventoryCodec.Encode(state));
                True(decoded.Succeeded, $"seed {seed} step {step}: round trip failed: {decoded.Error}");
                Eq(InventoryCodec.Encode(state), InventoryCodec.Encode(decoded.State!), $"seed {seed} step {step}: round trip");

                var report = InventoryEvaluator.Evaluate(state, policy, T0.AddMinutes(minutes + 0.1));
                foreach (var decision in report.Items)
                {
                    var record = state.Items[decision.Key];
                    var client2 = state.Clients[decision.Key.ClientDefinitionId];
                    if (poisoned.Contains(decision.Key))
                        Eq(InventoryItemState.RequiresReview, record.State, $"seed {seed} step {step}: poisoned {decision.Key}");
                    if (decision.Eligibility == PlanningEligibility.ReadyForPlanning)
                    {
                        Eq(InventoryItemState.CompletedStable, record.State, $"seed {seed}: ready implies stable");
                        Eq(0, client2.ConsecutiveFailures, $"seed {seed}: ready with failing client");
                        True(record.StablePollCount >= policy.MinStablePolls, $"seed {seed}: ready without polls");
                        True(!state.Items.Values.Any(other => other.Key != record.Key && Overlaps(record, other)),
                            $"seed {seed}: ready while another record claims an overlapping path");
                        Eq((long?)0, record.RemainingBytes, $"seed {seed}: ready without explicit zero remaining");
                        True(record.Key.DownloadId != "shared" || !state.Items.Keys.Any(k => k.DownloadId == "shared" && k != record.Key), $"seed {seed}: ready while duplicated across clients");
                    }

                    if (record.State == InventoryItemState.MissingFromClient)
                        Eq(PlanningEligibility.NoAction, decision.Eligibility, $"seed {seed}: missing must permit nothing");
                }
            }
        }
    }

    /// <summary>Independent oracle: equal, case-variant or nested claimed paths (case-insensitive).</summary>
    private static bool Overlaps(ItemRecord a, ItemRecord b) =>
        a.ClaimedPaths().Any(x => b.ClaimedPaths().Any(y =>
        {
            var (u, v) = (x.ToUpperInvariant(), y.ToUpperInvariant());
            return u == v || PathCanonicalizer.IsStrictlyUnder(u, v, PathStyle.Posix) || PathCanonicalizer.IsStrictlyUnder(v, u, PathStyle.Posix);
        }));

    private static string InventoryEscape(string value) =>
        string.Concat(value.Take(40).Select(c => char.IsControl(c) || c > '\u007E' ? $"\\u{(int)c:X4}" : c.ToString()));
}
