using System.Collections.Immutable;
using Crescendarr.Core.Domain;
using Crescendarr.Core.Planning;
using Crescendarr.Core.Policies;

// Dependency-free executable specs. Run: dotnet run --project tests/Crescendarr.Core.Spec
// These use FAKE host-profile rankings, not the production Lidarr comparison API.
internal static class Program
{
public static int Main()
{
var specs = new (string Name, Action Test)[]
{
    ("F01 11 upgrades, 12th better remains", F01),
    ("F02 exactly one missing addition", F02),
    ("F03 all incoming inferior -> no improvement", F03),
    ("F04 edition switch requires review", F04),
    ("F05 same codec/size has no upgrade authority", F05),
    ("F06 host profile can prefer MP3", F06),
    ("F07 compound physical file held", F07),
    ("F08 staged and library lyrics preserved in plan", F08),
    ("F13 stable deterministic plan and replay ID", F13),
    ("F17 unstable download source held", F17),
    ("F18 missing target file held", F18),
    ("Duplicate candidate recording held", Duplicates),
    ("Host quality unavailable held", NoComparator),
    ("Absent quality evidence held", NoQuality),
    ("Malformed existing MusicBrainz data blocked", BrokenCatalog),
    ("Empty candidate inventory blocked", EmptyDownload),
    ("Album mismatch blocked", AlbumMismatch),
    ("Lidarr eligibility veto honored", HostVeto),
    ("Global hold when any candidate ambiguous", GlobalHold),
    ("Multi-recording existing file never partially replaced", ExistingCompound),
    ("Duplicate candidate keys blocked", DuplicateKeys),
    ("Incomparable quality held", Incomparable),
    ("Album fingerprint change alters plan identity", ChangedAlbumFingerprint),
    ("Changing incoming quality changes plan identity", ChangedIncomingQuality),
    ("Changing existing quality changes plan identity", ChangedExistingQuality),
    ("Changes to unrelated existing records change plan identity", ChangedUnrelatedInventory),
    ("A shared existing track-file ID blocks the whole album", SharedExistingTrackFileId),
    ("Changing incoming edition changes plan identity", ChangedIncomingEdition),
    ("Changing staged lyric inventory changes plan identity", ChangedStagedLyrics)
};
int failures = 0;
foreach (var (name, test) in specs)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"{specs.Length - failures}/{specs.Length} specs passed");
return failures == 0 ? 0 : 1;
}

static string Rec(int i) => $"00000000-0000-0000-0000-{i:000000000000}";
static readonly string Edition = "10000000-0000-0000-0000-000000000001";
static readonly string OtherEdition = "10000000-0000-0000-0000-000000000002";

static IncomingFile Candidate(int n, string quality = "H", bool stagedLyrics = false) =>
    new($"source-{n:00}", ImmutableArray.Create(Rec(n)), quality, true, true, true, stagedLyrics);
static ExistingFile Old(int n, string quality = "L", bool lyrics = false, bool exists = true) =>
    new(n, ImmutableArray.Create(Rec(n)), quality, true, exists, lyrics);
static DownloadSnapshot Download(IEnumerable<IncomingFile> files, string? edition = null, long album = 99) =>
    new("synthetic-download", album, edition ?? Edition, "sha256:fictional-synthetic-inventory", files.ToImmutableArray());
static AlbumSnapshot Album(IEnumerable<ExistingFile> files, string? edition = null, long album = 99) =>
    new(album, edition ?? Edition, "sha256:fictional-synthetic-db-state", files.ToImmutableArray());
static MergePlan Plan(DownloadSnapshot incoming, AlbumSnapshot current,
    IHostQualityPolicy? policy = null) => new MergePlanner().Plan(incoming, current, policy ?? new FakeHostPolicy());
static void Expect(bool condition, string why) { if (!condition) throw new Exception(why); }
static int Count(MergePlan plan, MergeAction action) => plan.Decisions.Count(d => d.Action == action);

static void F01()
{
    var before = Enumerable.Range(1, 12).Select(n => Old(n, n == 12 ? "H" : "L", n == 12));
    var incoming = Enumerable.Range(1, 12).Select(n => Candidate(n, n == 12 ? "L" : "H"));
    var plan = Plan(Download(incoming), Album(before));
    Expect(plan.Disposition == PlanDisposition.Ready, "expected ready");
    Expect(Count(plan, MergeAction.Upgrade) == 11, "expected 11 upgrades");
    Expect(Count(plan, MergeAction.KeepExisting) == 1, "expected keep for 12");
    Expect(plan.Decisions.Single(d => d.CandidateKey == "source-12").Lyrics.PreserveExistingLrc, "existing lyrics not tracked");
}
static void F02()
{
    var plan = Plan(Download(Enumerable.Range(1, 12).Select(n => Candidate(n))),
        Album(Enumerable.Range(2, 11).Select(n => Old(n, "H"))));
    Expect(plan.Disposition == PlanDisposition.Ready, "expected ready");
    Expect(Count(plan, MergeAction.AddMissing) == 1, "expected 1 addition");
    Expect(Count(plan, MergeAction.KeepExisting) == 11, "expected 11 keeps");
}
static void F03()
{
    var plan = Plan(Download(Enumerable.Range(1, 12).Select(n => Candidate(n, "L"))),
        Album(Enumerable.Range(1, 12).Select(n => Old(n, "H"))));
    Expect(plan.Disposition == PlanDisposition.NoImprovement, "no-improvement planning state expected");
    Expect(Count(plan, MergeAction.KeepExisting) == 12, "all originals should be kept");
}
static void F04()
{
    var plan = Plan(Download([Candidate(1)], OtherEdition), Album([Old(1)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "edition mismatch must hold");
    Expect(plan.Decisions[0].ReasonCode == ReasonCodes.EditionMismatch, "expected reason code");
}
static void F05()
{
    // Without a real Lidarr quality preference a bigger FLAC cannot 'win'.
    var plan = Plan(Download([Candidate(1, "H")]), Album([Old(1, "H")]));
    Expect(plan.Disposition == PlanDisposition.NoImprovement, "size is never a comparison input");
}
static void F06()
{
    var plan = Plan(Download([Candidate(1, "MP3")]), Album([Old(1, "FLAC")]),
        new FakeHostPolicy(new Dictionary<string, int> { ["MP3"] = 2, ["FLAC"] = 1 }));
    Expect(Count(plan, MergeAction.Upgrade) == 1, "the injected host policy must decide");
}
static void F07()
{
    var multi = Candidate(1) with { RecordingIds = ImmutableArray.Create(Rec(1), Rec(2)) };
    var plan = Plan(Download([multi]), Album([Old(1), Old(2)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "compound file cannot be split");
}
static void F08()
{
    var plan = Plan(Download([Candidate(1, "H", stagedLyrics: true)]),
        Album([Old(1, "L", lyrics: true)]));
    Expect(plan.Decisions[0].Lyrics == new SidecarRequirement(true, true), "both lyric inventories must be protected");
}
static void F13()
{
    var a = Plan(Download([Candidate(2), Candidate(1)]), Album([Old(1)]));
    var b = Plan(Download([Candidate(1), Candidate(2)]), Album([Old(1)]));
    Expect(a.PlanId == b.PlanId, "order-independent idempotency key expected");
    Expect(a.Decisions.Select(d => d.CandidateKey).SequenceEqual(b.Decisions.Select(d => d.CandidateKey)), "stable ordering expected");
}
static void F17()
{
    var plan = Plan(Download([Candidate(1) with { SourceStable = false }]), Album([Old(1)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "changed staging must hold");
}
static void F18()
{
    var plan = Plan(Download([Candidate(1)]), Album([Old(1, exists: false)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "missing linked target must hold");
}
static void Duplicates()
{
    var plan = Plan(Download([Candidate(1), Candidate(1) with { CandidateKey = "alternate-1" }]), Album([Old(1)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "ambiguous incoming recording must hold");
}
static void NoComparator()
{
    var plan = Plan(Download([Candidate(1)]), Album([Old(1)]), new ThrowingHostPolicy());
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "host error must not become approval");
}
static void NoQuality()
{
    var plan = Plan(Download([Candidate(1) with { HostQualityToken = null }]), Album([Old(1)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "unknown quality cannot auto-import");
}
static void BrokenCatalog()
{
    var broken = Old(2) with { RecordingIds = ImmutableArray.Create("invalid") };
    var plan = Plan(Download([Candidate(1)]), Album([broken]));
    Expect(plan.Disposition == PlanDisposition.Blocked, "broken catalog must block entire plan");
}
static void EmptyDownload()
{
    var plan = Plan(Download([]), Album([Old(1)]));
    Expect(plan.Disposition == PlanDisposition.Blocked, "empty candidate set is not a verified no-op");
}
static void AlbumMismatch()
{
    var plan = Plan(Download([Candidate(1)], album: 21), Album([Old(1)], album: 99));
    Expect(plan.Disposition == PlanDisposition.Blocked, "different album ID must block");
}
static void HostVeto()
{
    var plan = Plan(Download([Candidate(1) with { HostImportEligible = false }]), Album([]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "Lidarr host-level veto must hold");
}


static void GlobalHold()
{
    var notVerified = Candidate(2) with { IdentityVerified = false };
    var plan = Plan(Download([Candidate(1), notVerified]), Album([Old(1), Old(2)]));
    Expect(Count(plan, MergeAction.Upgrade) == 1, "candidate 1 should be recommended only");
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "any ambiguity must hold entire import");
}
static void ExistingCompound()
{
    var linkedFile = Old(1) with { RecordingIds = ImmutableArray.Create(Rec(1), Rec(2)) };
    var plan = Plan(Download([Candidate(1)]), Album([linkedFile]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "one file linked to multiple tracks must be atomic");
}
static void DuplicateKeys()
{
    var plan = Plan(Download([Candidate(1), Candidate(2) with { CandidateKey = "source-01" }]), Album([]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "duplicate source keys must hold");
}
static void Incomparable()
{
    var plan = Plan(Download([Candidate(1, "OTHER")]), Album([Old(1)]));
    Expect(plan.Disposition == PlanDisposition.NeedsReview, "missing host preference must hold");
}
static void ChangedAlbumFingerprint()
{
    var incoming = Download([Candidate(1)]);
    var album = Album([Old(1)]);
    var first = Plan(incoming, album);
    var later = Plan(incoming, album with { StateFingerprint = "different-db-snapshot" });
    Expect(first.PlanId != later.PlanId, "changed target state must invalidate plan id");
}

static void ChangedIncomingQuality()
{
    var before = Album([Old(1, "L")]);
    var incoming = Download([Candidate(1, "H")]);
    var preferred = new FakeHostPolicy(new Dictionary<string, int> { ["L"] = 1, ["H"] = 2, ["ULTRA"] = 3 });
    var first = Plan(incoming, before, preferred);
    var second = Plan(incoming with { Files = [Candidate(1, "ULTRA")] }, before, preferred);
    Expect(Count(first, MergeAction.Upgrade) == 1 && Count(second, MergeAction.Upgrade) == 1, "both must be upgrades");
    Expect(first.PlanId != second.PlanId, "a quality change cannot reuse an upgrade approval id");
}
static void ChangedExistingQuality()
{
    var incoming = Download([Candidate(1, "H")]);
    var policy = new FakeHostPolicy(new Dictionary<string, int> { ["L"] = 1, ["MID"] = 2, ["H"] = 3 });
    var first = Plan(incoming, Album([Old(1, "L")]), policy);
    var second = Plan(incoming, Album([Old(1, "MID")]), policy);
    Expect(first.PlanId != second.PlanId, "existing quality changes cannot reuse approval IDs");
}
static void ChangedUnrelatedInventory()
{
    var incoming = Download([Candidate(1)]);
    var first = Plan(incoming, Album([Old(1)]));
    var second = Plan(incoming, Album([Old(1), Old(2)]));
    Expect(first.PlanId != second.PlanId, "unrelated existing files are part of the reconciliation boundary");
}
static void SharedExistingTrackFileId()
{
    var shared = Old(2) with { TrackFileId = 1 };
    var plan = Plan(Download([Candidate(1)]), Album([Old(1), shared]));
    Expect(plan.Disposition == PlanDisposition.Blocked, "one file ID associated with multiple snapshots cannot be replaced");
}
static void ChangedIncomingEdition()
{
    var first = Plan(Download([Candidate(1)]), Album([Old(1)]));
    var second = Plan(Download([Candidate(1)], edition: OtherEdition), Album([Old(1)]));
    Expect(first.PlanId != second.PlanId, "edition identity must affect plan ID");
}
static void ChangedStagedLyrics()
{
    var first = Plan(Download([Candidate(1, stagedLyrics: false)]), Album([Old(1)]));
    var second = Plan(Download([Candidate(1, stagedLyrics: true)]), Album([Old(1)]));
    Expect(first.PlanId != second.PlanId, "staged sidecars must affect plan ID");
}

}

internal sealed class FakeHostPolicy(Dictionary<string, int>? rankings = null) : IHostQualityPolicy
{
    private readonly Dictionary<string, int> _ranks = rankings ?? new() { ["L"] = 1, ["H"] = 2 };
    public IncomingQuality Compare(long albumId, string incomingHostQualityToken, string existingHostQualityToken)
    {
        if (!_ranks.TryGetValue(incomingHostQualityToken, out var incoming) ||
            !_ranks.TryGetValue(existingHostQualityToken, out var existing))
            return IncomingQuality.Incomparable;
        return incoming > existing ? IncomingQuality.StrictlyPreferred
            : incoming == existing ? IncomingQuality.Equal : IncomingQuality.Inferior;
    }
}
internal sealed class ThrowingHostPolicy : IHostQualityPolicy
{
    public IncomingQuality Compare(long albumId, string incomingHostQualityToken, string existingHostQualityToken)
        => throw new InvalidOperationException("synthetic host quality failure");
}
