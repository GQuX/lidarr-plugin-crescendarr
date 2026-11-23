using System.Collections.Immutable;
using Crescendarr.Core.Inventory;
using Crescendarr.Core.Journal;

// M18 (issue #48): pure specs for the ONE operation journal now covering additions, protected (kept) tracks and
// album-wide lyrics. A small in-memory model reproduces the stock behaviours that matter, as observed on the real
// stock daemon in M18 CI: the upgrade recycles the original and deletes its row (unlinking the track) BEFORE it
// moves the new file and inserts the fresh row, and a rescan that finds an unregistered audio file re-imports every
// album file with fresh rows and recycles every registered lyric of the files it replaces.
internal static class M18Specs
{
    private const string Op = "11111111-2222-4333-8444-555555555590";
    private const string Artist = "/m18/library/artist";

    private static string Ops(params string[] parts) => "/m18/ops/" + Op + "/" + string.Join("/", parts);

    private static ContentId C(char c, long n) => new(new string(c, 64), n);

    private static string Rec(int n) => $"00000001-0000-4000-8000-{n:000000000000}";

    private static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    private static void Throws(Action action, string what)
    {
        try { action(); }
        catch (JournalTransitionException) { return; }
        throw new InvalidOperationException("no exception: " + what);
    }

    /// <summary>Track 1 upgrade (with lyric), track 2 kept superior FLAC (with lyric), track 3 missing (added).</summary>
    public static OperationBinding Binding()
    {
        var up = new TrackBinding(1, Rec(1), 11, Artist + "/01.mp3", Artist + "/01.mp3", "/m18/staging/01.mp3", C('a', 100), C('b', 120), Ops("audio", "1.mp3"),
            new LyricBinding(21, "01.lrc", Artist + "/01.lrc", C('c', 10), Ops("track-lyrics", "1.lrc")));
        return new OperationBinding(Op, null, 7, "11111111-2222-4333-8444-555555555500", 70, "11111111-2222-4333-8444-555555555501", PathStyle.Posix,
            "/m18/library", "/m18/staging", "/m18/ops/" + Op, ImmutableArray.Create(up), Artist)
        {
            Additions = ImmutableArray.Create(new AdditionBinding(3, Rec(3), Artist + "/03.mp3", "/m18/staging/03.mp3", C('e', 130))),
            Kept = ImmutableArray.Create(new KeptTrackBinding(2, Rec(2), 12, Artist + "/02.flac", C('f', 400))),
            AlbumLyrics = ImmutableArray.Create(
                new AlbumLyricBinding(21, 1, "01.lrc", Artist + "/01.lrc", C('c', 10), Ops("album-lyrics", "21.lrc")),
                new AlbumLyricBinding(22, 2, "02.lrc", Artist + "/02.lrc", C('d', 11), Ops("album-lyrics", "22.lrc"))),
        };
    }

    /// <summary>In-memory host + filesystem with the stock behaviours observed on the real daemon.</summary>
    private sealed class World
    {
        public readonly Dictionary<int, int> Links = new() { [1] = 11, [2] = 12, [3] = 0 };
        public readonly Dictionary<int, string> Rows = new() { [11] = Artist + "/01.mp3", [12] = Artist + "/02.flac" };
        public readonly List<HostLyric> Lyrics = new() { new(21, 11, "01.lrc"), new(22, 12, "02.lrc") };
        public readonly Dictionary<string, ContentId> Files = new()
        {
            [Artist + "/01.mp3"] = C('a', 100), [Artist + "/02.flac"] = C('f', 400), [Artist + "/01.lrc"] = C('c', 10), [Artist + "/02.lrc"] = C('d', 11),
            ["/m18/staging/01.mp3"] = C('b', 120), ["/m18/staging/03.mp3"] = C('e', 130),
            // Album-wide lyric backups are written and verified before the journal is published.
            [Ops("album-lyrics", "21.lrc")] = C('c', 10), [Ops("album-lyrics", "22.lrc")] = C('d', 11),
        };

        public readonly List<HostTrack> Foreign = new();
        public readonly List<string> Recycled = new();
        private int _nextRow = 31;
        private int _nextLyric = 41;

        public HostSnapshot Host() => new(true, 7, ImmutableArray.Create(new HostRelease(70, "11111111-2222-4333-8444-555555555501", true)),
            Links.Select(l => new HostTrack(l.Key, Rec(l.Key), l.Value)).Concat(Foreign).ToImmutableArray(),
            Rows.Select(r => new HostTrackFile(r.Key, r.Value)).ToImmutableArray(), Lyrics.ToImmutableArray(), Artist);

        public FilesystemSnapshot Fs(OperationBinding b) => new(true, Reconciler.AllBoundPaths(b).ToImmutableDictionary(p => p,
            p => Files.TryGetValue(p, out var c) ? new FileObservation(true, c.Length, c.Sha256, false) : FileObservation.Absent, StringComparer.Ordinal));

        public int InsertRow(string path) { var id = _nextRow++; Rows[id] = path; return id; }

        public int InsertLyric(int trackFileId, string relative) { var id = _nextLyric++; Lyrics.Add(new HostLyric(id, trackFileId, relative)); return id; }

        /// <summary>Stock TrackFileDeletedEvent: unlink tracks, recycle linked lyric files, drop their rows.</summary>
        public void DeleteRow(int id)
        {
            Rows.Remove(id);
            foreach (var track in Links.Where(l => l.Value == id).Select(l => l.Key).ToList()) Links[track] = 0;
            foreach (var lyric in Lyrics.Where(l => l.TrackFileId == id).ToList())
            {
                var path = Artist + "/" + lyric.RelativePath;
                if (Files.Remove(path)) Recycled.Add(path);
                Lyrics.Remove(lyric);
            }
        }

        /// <summary>Stock upgrade up to the post-move/pre-row boundary (CreateAfterTrackImport), optionally finishing it.</summary>
        public void NativeUpgrade(int track, string original, string incoming, string destination, bool finish)
        {
            Files.Remove(original);
            DeleteRow(Links[track]);
            Files[destination] = Files[incoming];
            Files.Remove(incoming);
            if (finish) Links[track] = InsertRow(destination);
        }

        public void NativeAdd(int track, string incoming, string destination, bool finish)
        {
            Files[destination] = Files[incoming];
            Files.Remove(incoming);
            if (finish) Links[track] = InsertRow(destination);
        }

        /// <summary>Stock rescan with an unregistered album audio file: every album file re-imported with a fresh row.</summary>
        public void CascadeRescan(IEnumerable<(int Track, string Path)> albumAudio)
        {
            foreach (var (track, path) in albumAudio.Where(a => Files.ContainsKey(a.Path)).ToList())
            {
                var previous = Rows.Where(r => r.Value == path).Select(r => r.Key).ToList();
                foreach (var row in previous) DeleteRow(row);
                Links[track] = InsertRow(path);
            }
        }
    }

    private static readonly (int Track, string Path)[] AlbumAudio = { (1, Artist + "/01.mp3"), (2, Artist + "/02.flac"), (3, Artist + "/03.mp3") };

    /// <summary>Applies the reconciler's next action to the model exactly as the plugin executor would, until a terminal verdict.</summary>
    private static (OperationManifest Manifest, Verdict Verdict, Reconciliation Last) Drive(OperationManifest m, World w, string? crashInside = null, int limit = 100)
    {
        for (var i = 0; i < limit; i++)
        {
            var (next, verdict, last) = Step(m, w, crashInside);
            m = next;
            if (verdict is { } terminal) return (m, terminal, last);
        }

        throw new InvalidOperationException("no terminal verdict");
    }

    /// <summary>One reconciliation and one executed action. A non-null verdict means stop (terminal, or crashed inside the host).</summary>
    private static (OperationManifest Manifest, Verdict? Verdict, Reconciliation Last) Step(OperationManifest m, World w, string? crashInside)
    {
        {
            var r = Reconciler.Reconcile(m, w.Host(), w.Fs(m.Binding));
            if (r.Verdict is Verdict.Completed or Verdict.OperatorApprovalRequired) return (m, r.Verdict, r);
            if (r.Verdict == Verdict.RequiresReview)
            {
                if (r.Next?.Action == RecoveryAction.MarkReview && m.Status == OperationStatus.Active) m = JournalEngine.MarkReview(m, r.Next.Reason);
                return (m, Verdict.RequiresReview, r);
            }

            var next = r.Next!;
            var id = next.TrackId ?? 0;
            switch (next.Action)
            {
                case RecoveryAction.CopyBackup:
                    var t = m.BindingFor(id);
                    if (m.ProgressFor(id).InFlight is null) m = JournalEngine.MarkInFlight(m, id, JournalStep.CopyBackup);
                    w.Files[t.BackupAudioPath] = w.Files[t.OriginalPath];
                    if (t.Lyric is not null) w.Files[t.Lyric.BackupPath] = w.Files[t.Lyric.Path];
                    break;
                case RecoveryAction.DetachLyric:
                    if (m.ProgressFor(id).InFlight is null) m = JournalEngine.MarkInFlight(m, id, JournalStep.DetachLyric);
                    w.Lyrics.RemoveAll(l => l.LyricFileId == m.BindingFor(id).Lyric!.LyricFileId);
                    break;
                case RecoveryAction.InvokeNativeUpgrade:
                    var u = m.BindingFor(id);
                    m = JournalEngine.MarkInFlight(m, id, JournalStep.NativeUpgrade);
                    w.NativeUpgrade(id, u.OriginalPath, u.IncomingPath, u.DestinationPath, finish: crashInside != "upgrade");
                    if (crashInside == "upgrade") return (m, Verdict.Proceed, r);
                    break;
                case RecoveryAction.InvokeNativeAdd:
                    var a = m.AdditionBindingFor(id);
                    m = JournalEngine.MarkAdditionInFlight(m, id);
                    w.NativeAdd(id, a.IncomingPath, a.DestinationPath, finish: crashInside != "add");
                    if (crashInside == "add") return (m, Verdict.Proceed, r);
                    break;
                case RecoveryAction.RelinkLyric:
                    if (m.ProgressFor(id).InFlight is null) m = JournalEngine.MarkInFlight(m, id, JournalStep.RelinkLyric);
                    w.InsertLyric(next.NewTrackFileId!.Value, m.BindingFor(id).Lyric!.RelativePath);
                    break;
                case RecoveryAction.RecordStepDone:
                    m = m.IsAddition(id)
                        ? JournalEngine.MarkAdditionDone(m, id, next.NewTrackFileId)
                        : JournalEngine.MarkDone(m, id, m.ProgressFor(id).InFlight!.Value, next.NewTrackFileId, next.NewLyricFileId);
                    break;
                case RecoveryAction.AbandonStep:
                    m = m.IsAddition(id) ? JournalEngine.AbandonAddition(m, id) : JournalEngine.AbandonInFlight(m, id, m.ProgressFor(id).InFlight!.Value);
                    break;
                case RecoveryAction.RestoreAlbumLyric:
                    var restore = m.Binding.AlbumLyrics.Single(l => l.LyricFileId == next.AlbumLyricId);
                    True(!w.Files.ContainsKey(restore.Path), "restore must never overwrite");
                    w.Files[restore.Path] = w.Files[restore.BackupPath];
                    break;
                case RecoveryAction.BindAlbumLyric:
                    var bind = m.Binding.AlbumLyrics.Single(l => l.LyricFileId == next.AlbumLyricId);
                    w.InsertLyric(next.NewTrackFileId!.Value, bind.RelativePath);
                    break;
                case RecoveryAction.CompleteOperation:
                    m = JournalEngine.Complete(m);
                    break;
                default:
                    throw new InvalidOperationException("unexpected action " + next.Action);
            }

            return (m, null, r);
        }
    }

    private static void FinalStateHolds(World w, string when)
    {
        Eq(Artist + "/01.mp3", w.Rows[w.Links[1]], $"{when}: track 1 linked at its destination");
        Eq(C('b', 120), w.Files[Artist + "/01.mp3"], $"{when}: track 1 holds the incoming bytes");
        Eq(Artist + "/02.flac", w.Rows[w.Links[2]], $"{when}: kept track 2 linked to its original audio");
        Eq(C('f', 400), w.Files[Artist + "/02.flac"], $"{when}: kept FLAC bytes unchanged");
        Eq(Artist + "/03.mp3", w.Rows[w.Links[3]], $"{when}: track 3 added");
        Eq(C('c', 10), w.Files[Artist + "/01.lrc"], $"{when}: lyric 1 bytes");
        Eq(C('d', 11), w.Files[Artist + "/02.lrc"], $"{when}: lyric 2 bytes (the unselected track's)");
        Eq(1, w.Lyrics.Count(l => l.RelativePath == "01.lrc" && l.TrackFileId == w.Links[1]), $"{when}: lyric 1 registered once to the CURRENT row");
        Eq(1, w.Lyrics.Count(l => l.RelativePath == "02.lrc" && l.TrackFileId == w.Links[2]), $"{when}: lyric 2 registered once to the CURRENT row");
        Eq(2, w.Lyrics.Count, $"{when}: no extra lyric rows");
    }

    // ---------- specs ----------

    public static void Validation()
    {
        var good = Binding();
        Eq(0, JournalValidation.Validate(good).Length, "M18 binding errors: " + string.Join(" | ", JournalValidation.Validate(good)));
        var a = good.Additions[0];
        var k = good.Kept[0];
        var l = good.AlbumLyrics[1];
        var variants = new (string, OperationBinding)[]
        {
            ("addition incoming outside staging", good with { Additions = ImmutableArray.Create(a with { IncomingPath = Artist + "/x.mp3" }) }),
            ("addition destination outside artist", good with { Additions = ImmutableArray.Create(a with { DestinationPath = "/m18/staging/x.mp3" }) }),
            ("addition aliases kept audio", good with { Additions = ImmutableArray.Create(a with { DestinationPath = k.Path.ToUpperInvariant() }) }),
            ("kept outside artist root", good with { Kept = ImmutableArray.Create(k with { Path = "/m18/staging/02.flac" }) }),
            ("track bound in two roles", good with { Kept = ImmutableArray.Create(k with { TrackId = 1 }) }),
            ("kept row equals an upgrade original", good with { Kept = ImmutableArray.Create(k with { TrackFileId = 11 }) }),
            ("album lyric of an unbound track", good with { AlbumLyrics = good.AlbumLyrics.SetItem(1, l with { TrackId = 99 }) }),
            ("album lyric path is not artist root + relative path", good with { AlbumLyrics = good.AlbumLyrics.SetItem(1, l with { Path = Artist + "/decoy.lrc" }) }),
            ("album lyric backup outside the operation root", good with { AlbumLyrics = good.AlbumLyrics.SetItem(1, l with { BackupPath = Artist + "/22.bak" }) }),
            ("album lyric disagrees with the replacement's lyric", good with { AlbumLyrics = good.AlbumLyrics.SetItem(0, good.AlbumLyrics[0] with { Content = C('9', 10) }) }),
            ("duplicate album lyric id", good with { AlbumLyrics = good.AlbumLyrics.Add(l) }),
            ("nothing to do", good with { Tracks = ImmutableArray<TrackBinding>.Empty, Additions = ImmutableArray<AdditionBinding>.Empty }),
        };
        foreach (var (name, binding) in variants) True(!JournalValidation.Validate(binding).IsEmpty, "accepted: " + name);
        True(JournalValidation.Validate(good with { Tracks = ImmutableArray<TrackBinding>.Empty, AlbumLyrics = ImmutableArray.Create(l) }).IsEmpty,
            "an add-only operation (no replacement) is valid");
    }

    public static void Transitions()
    {
        var m = JournalEngine.Begin(Binding());
        Throws(() => JournalEngine.MarkAdditionInFlight(m, 3), "addition before the replacement is final");
        Throws(() => JournalEngine.Complete(m), "complete with nothing done");
        var w = new World();
        // Drive the replacement only, then check the addition rules.
        for (var guard = 0; guard < 20 && !JournalEngine.AllUpgradesFinal(m); guard++)
        {
            var r = Reconciler.Reconcile(m, w.Host(), w.Fs(m.Binding));
            True(r.Next!.Action != RecoveryAction.InvokeNativeAdd, "no addition before the replacement is final");
            (m, _, _) = Step(m, w, null);
        }

        True(JournalEngine.AllUpgradesFinal(m), "replacement final");
        var adding = JournalEngine.MarkAdditionInFlight(m, 3);
        Throws(() => JournalEngine.MarkAdditionInFlight(adding, 3), "two additions in flight");
        Throws(() => JournalEngine.MarkAdditionDone(adding, 3, 11), "addition records the deleted original ID");
        Throws(() => JournalEngine.MarkAdditionDone(adding, 3, 12), "addition records the kept track's ID");
        Throws(() => JournalEngine.MarkAdditionDone(adding, 3, m.ProgressFor(1).NewTrackFileId), "addition records the upgrade's fresh ID");
        Throws(() => JournalEngine.Complete(adding), "complete while an addition is in flight");
        var added = JournalEngine.MarkAdditionDone(adding, 3, 77);
        Eq(AdditionPhase.Added, added.AdditionFor(3).Phase, "added");
        var text = JournalCodec.Encode(added);
        var decoded = JournalCodec.Decode(text, Op, added.BindingDigest);
        True(decoded.Succeeded, "v3 decode: " + decoded.Error);
        Eq(text, JournalCodec.Encode(decoded.Manifest!), "v3 canonical round trip");
        var forged = new Dictionary<string, OperationManifest>
        {
            ["addition in flight before the replacement is final"] = JournalEngine.Begin(Binding()) with
            {
                Additions = ImmutableArray.Create(new AdditionProgress(3, AdditionPhase.Planned, true, 0, null)),
            },
            ["added without a row"] = added with { Additions = ImmutableArray.Create(added.Additions[0] with { NewTrackFileId = null }) },
            ["completed with a pending addition"] = adding with { Status = OperationStatus.Completed, Additions = ImmutableArray.Create(adding.Additions[0] with { InFlight = false }) },
            ["addition progress missing"] = added with { Additions = ImmutableArray<AdditionProgress>.Empty },
        };
        foreach (var (name, manifest) in forged) True(!JournalCodec.Decode(JournalCodec.Encode(manifest), Op, manifest.BindingDigest).Succeeded, "decoded forged: " + name);
    }

    public static void HappyPath()
    {
        var w = new World();
        var (m, verdict, _) = Drive(JournalEngine.Begin(Binding()), w);
        Eq(Verdict.Completed, verdict, "happy path");
        Eq(OperationStatus.Completed, m.Status, "journal completed");
        FinalStateHolds(w, "happy path");
        Eq(12, w.Links[2], "kept track keeps its original row when no scan ran");
        Eq(0, w.Recycled.Count, "nothing recycled");
    }

    public static void PostMoveCrashThenCascade()
    {
        foreach (var where in new[] { "upgrade", "add" })
        {
            var w = new World();
            var (m, _, _) = Drive(JournalEngine.Begin(Binding()), w, crashInside: where);
            // Without a scan: the placed file has no row. Recovery never inserts host rows on its own.
            var unscanned = Reconciler.Reconcile(m, w.Host(), w.Fs(m.Binding));
            Eq(Verdict.OperatorApprovalRequired, unscanned.Verdict, $"{where}: post-move/pre-row needs operator approval");
            Eq(RecoveryAction.InsertFreshTrackFileForIncoming, unscanned.Next!.Action, $"{where}: proposal");
            // The real stock rescan before recovery: album-wide fresh rows and recycled registered lyrics.
            w.CascadeRescan(AlbumAudio);
            True(w.Recycled.Contains(Artist + "/02.lrc"), $"{where}: the unselected track's lyric was recycled by the cascade");
            var (after, verdict, last) = Drive(m, w);
            Eq(Verdict.Completed, verdict, $"{where}: recovery after the cascade completes (blockers: {string.Join("; ", last.Blockers)})");
            FinalStateHolds(w, $"{where} after cascade");
            True(w.Links[2] != 12, $"{where}: kept track's row was rotated by the scan and re-identified by path + bytes");
            var replay = Reconciler.Reconcile(after, w.Host(), w.Fs(after.Binding));
            Eq(Verdict.Completed, replay.Verdict, $"{where}: replay is a no-op");
        }
    }

    public static void Negatives()
    {
        OperationManifest Crashed(World w)
        {
            var (m, _, _) = Drive(JournalEngine.Begin(Binding()), w, crashInside: "upgrade");
            w.CascadeRescan(AlbumAudio);
            return m;
        }

        var cases = new Dictionary<string, (Action<World> Mutate, string Reason)>
        {
            ["conflicting bytes at the recycled lyric path"] = (w => w.Files[Artist + "/02.lrc"] = C('0', 5), "unrelated bytes occupy the lyric path"),
            ["corrupt album backup"] = (w => w.Files[Ops("album-lyrics", "22.lrc")] = C('1', 11), "lyric missing and backup unverifiable"),
            ["missing album backup"] = (w => w.Files.Remove(Ops("album-lyrics", "22.lrc")), "lyric missing and backup unverifiable"),
            ["stale lyric row at the path"] = (w => { w.Files[Artist + "/02.lrc"] = C('d', 11); w.Lyrics.Add(new HostLyric(99, 12, "02.lrc")); }, "stale lyric row"),
            ["dangling nonzero link on the pending addition"] = (w => w.Links[3] = 987654, "dangling"),
            ["kept FLAC bytes changed"] = (w => w.Files[Artist + "/02.flac"] = C('2', 400), "protected audio bytes changed"),
            ["foreign album aliases the fresh upgrade row"] = (w => w.Foreign.Add(new HostTrack(500, Rec(500), w.Links[1])), "created by this operation is linked to another track"),
        };
        foreach (var (name, (mutate, reason)) in cases)
        {
            var w = new World();
            var m = Crashed(w);
            mutate(w);
            var lyricsBefore = w.Lyrics.ToList();
            var filesBefore = w.Files.ToDictionary(f => f.Key, f => f.Value);
            var linksBefore = w.Links.ToDictionary(l => l.Key, l => l.Value);
            var (after, verdict, last) = Drive(m, w);
            Eq(Verdict.RequiresReview, verdict, name);
            True(last.Blockers.Any(b => b.Contains(reason, StringComparison.OrdinalIgnoreCase)), $"{name}: blockers [{string.Join("; ", last.Blockers)}] lack '{reason}'");
            True(lyricsBefore.SequenceEqual(w.Lyrics) && linksBefore.SequenceEqual(w.Links) && filesBefore.Count == w.Files.Count && filesBefore.All(f => w.Files[f.Key] == f.Value),
                $"{name}: zero mutations before review");
            Eq(OperationStatus.RequiresReview, after.Status, $"{name}: review recorded");
            Eq(Verdict.RequiresReview, Reconciler.Reconcile(after, w.Host(), w.Fs(after.Binding)).Verdict, $"{name}: sticky");
        }

        // Fresh ADD row aliased by a foreign album after the addition was recorded.
        var world = new World();
        var (done, _, _) = Drive(JournalEngine.Begin(Binding()), world);
        var reopened = done with { Status = OperationStatus.Active };
        world.Foreign.Add(new HostTrack(501, Rec(501), world.Links[3]));
        True(Reconciler.Reconcile(reopened, world.Host(), world.Fs(done.Binding)).Blockers.Any(b => b.Contains("created by this operation is linked to another track", StringComparison.Ordinal)),
            "a foreign alias of the fresh ADD row is a blocker");

        // Unrelated sharing and unlinked/dangling foreign tracks never block.
        var unrelated = new World();
        unrelated.Foreign.Add(new HostTrack(600, Rec(600), 700));
        unrelated.Foreign.Add(new HostTrack(601, Rec(601), 700));
        unrelated.Foreign.Add(new HostTrack(602, Rec(602), 0));
        unrelated.Foreign.Add(new HostTrack(603, Rec(603), 900001));
        unrelated.Rows[700] = "/m18/library/foreign/01.mp3";
        var (_, unrelatedVerdict, unrelatedLast) = Drive(JournalEngine.Begin(Binding()), unrelated);
        Eq(Verdict.Completed, unrelatedVerdict, "unrelated sharing, TrackFileId=0 and dangling foreign links do not block: " + string.Join("; ", unrelatedLast.Blockers));
    }

    public static void ReattachOnlyWhenAudioLinked()
    {
        var w = new World();
        var (m, _, _) = Drive(JournalEngine.Begin(Binding()), w, crashInside: "add");
        w.CascadeRescan(AlbumAudio.Where(a => a.Track != 3));
        // The placed add has no row: lyrics must NOT be restored or bound yet (a later scan could recycle them again).
        var r = Reconciler.Reconcile(m, w.Host(), w.Fs(m.Binding));
        True(r.Next?.Action is not (RecoveryAction.RestoreAlbumLyric or RecoveryAction.BindAlbumLyric), "no lyric settlement while an album audio file is unregistered: " + r.Next?.Action);
    }
}
