using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Crescendarr.Core.Inventory;
using Crescendarr.Core.Journal;
using Crescendarr.RecoveryLab;

// Dependency-free executable specs for the M13 journal and disposable local recovery lab.
// Every lab lives in a fresh directory under the system temp directory and is deleted after use.
// Run: dotnet run --project tests/Crescendarr.Journal.Spec -c Release
internal static class Program
{
    private static readonly List<string> NotExecuted = new();

    public static int Main()
    {
        Environment.SetEnvironmentVariable(LabSafety.OptInVariable, LabSafety.OptInValue);
        var specs = new (string Name, Action Test)[]
        {
            ("J01 Binding validation rejects unsafe identities and paths", BindingValidation),
            ("J02 Transitions: one step in flight, ordered phases, no destructive step before all backups", TransitionOrdering),
            ("J03 Fresh IDs required; deleted original numeric ID is never reused", FreshIds),
            ("J04 Repeated interruptions escalate to review; review and completion are terminal", TerminalStates),
            ("J05 Codec round trip is canonical; binding digest is stable across generations", CodecRoundTrip),
            ("J06 Codec fails closed on corrupt, truncated, oversize or conflicting manifests", CodecRejects),
            ("R01 Reconciler blocks on edition, host, mount, identity, stale ID, link escape and missing evidence", ReconcilerBlockers),
            ("R02 Reconciler never infers native success from absence", NoSuccessFromAbsence),
            ("L01 Happy path completes; protected track, lyrics, edition and backups preserved", HappyPath),
            ("L02 Crash at EVERY fault point: invariants hold, recovery is guarded and idempotent", CrashMatrix),
            ("L03 Failed fsync/rename/transfer at EVERY fault point: invariants hold", FailureMatrix),
            ("L04 Crash during recovery itself, then recover again", CrashDuringRecovery),
            ("L05 Edition switched mid-operation: review, zero native calls", EditionSwitch),
            ("L06 Changed original/incoming/lyric bytes, conflicting destination, stale row, lost host, lost mount: review", TamperMatrix),
            ("L07 Backup corrupted after verification: no native call", CorruptBackup),
            ("L08 Corrupt, conflicting or duplicate manifests: refused, never silently reset", ManifestCorruption),
            ("L09 Interrupted manifest save leaves the previous generation and a reported stray temp file", StrayTemp),
            ("L10 Fake host reproduces the stock lyric hazard when a lyric stays registered", HostHazardFidelity),
            ("L11 Operator-approval state is not changed by unapproved recovery", UnapprovedRecoveryIsInert),
            ("L12 Symlink at a bound path is treated as escaping", SymlinkEscape),
            ("L13 Lab refuses roots outside temp, unmarked roots and missing opt-in", LabGuards),
            ("L14 Change between the last check and the native call: revalidation blocks it", ConcurrentChangeBeforeNative),
            ("X01 Review A: ancestor or root link refused for new and reopened labs; nothing created through it", AncestorLinkEscape),
            ("X02 Review A: lab-internal directory replaced by a link stops the lab before writing through it", InternalLinkEscape),
            ("X03 Review B: duplicate or case-variant rows at a bound path, or a shared original file, never reach a destructive step", DuplicatePathRows),
            ("X04 Review C: physical lyric path that is not artist root + registered relative path fails closed", LyricIdentityMismatch),
            ("X05 Broader: hardlink planted at the backup path cannot destroy the original", HardlinkedBackupPath),
            ("K01 SIGKILL a separate process between local writes; a new process reconciles and recovers", KillAndReopen),
        };

        // Optional local filter (comma-separated name prefixes); CI always runs everything.
        var filter = Environment.GetEnvironmentVariable("JOURNAL_SPEC_FILTER");
        if (!string.IsNullOrEmpty(filter))
            specs = specs.Where(s => filter.Split(',').Any(f => s.Name.StartsWith(f, StringComparison.Ordinal))).ToArray();

        var failures = 0;
        var notExecuted = 0;
        foreach (var (name, test) in specs)
        {
            var timer = Stopwatch.StartNew();
            var notesBefore = NotExecuted.Count;
            try
            {
                test();
                // A spec that could not exercise its scenario on this host is reported as
                // NOT_EXECUTED, never as a pass.
                if (NotExecuted.Count > notesBefore) { notExecuted++; Console.WriteLine($"NOT_EXECUTED {name}: {string.Join("; ", NotExecuted.Skip(notesBefore))}"); }
                else Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
        }

        var passed = specs.Length - failures - notExecuted;
        Console.WriteLine($"JOURNAL_SPEC_TOTALS passed={passed} failed={failures} not_executed={notExecuted} total={specs.Length}");
        Console.WriteLine($"{passed}/{specs.Length} journal specs passed");
        return failures == 0 ? 0 : 1;
    }

    // ---------- helpers ----------

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

    private static string NewLabPath() => Path.Combine(Path.GetTempPath(), "crescendarr-m13-lab-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates a lab, runs the body, always deletes the lab directory afterwards.</summary>
    private static void WithLab(Action<string, ContentIdSnapshot> body)
    {
        Faults.Arm(null, null);
        var lab = LabScenario.Create(NewLabPath());
        try
        {
            body(lab, Lab.Baseline(lab));
        }
        finally
        {
            Faults.Arm(null, null);
            try { Directory.Delete(lab, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void NoViolations(string lab, ContentIdSnapshot baseline, string when)
    {
        var violations = Lab.CheckInvariants(lab, baseline);
        True(violations.Count == 0, $"{when}: {string.Join("; ", violations)}");
    }

    private static Exception? RunCatching(Func<object> action)
    {
        try { action(); return null; }
        catch (SimulatedCrashException error) { return error; }
        catch (IOException error) { return error; }
    }

    private static string HostBytes(string lab) => File.ReadAllText(Path.Combine(lab, FakeHost.FileName));

    private static OperationBinding SampleBinding()
    {
        var style = LabSafety.Style;
        string P(params string[] parts) => style == PathStyle.Windows ? @"C:\lab\" + string.Join('\\', parts) : "/lab/" + string.Join('/', parts);
        var op = "0f0e0d0c-0b0a-4908-8706-050403020100";
        var tracks = new[] { 1, 2 }.Select(n => new TrackBinding(
            n, $"22222222-3333-4444-8555-00000000000{n}", 500 + n,
            P("library", "a", $"{n}.flac"), P("library", "a", $"{n}.flac"), P("staging", $"{n}.flac"),
            new ContentId(new string('a', 63) + n, 100 + n), new ContentId(new string('b', 63) + n, 200 + n),
            P("ops", op, $"{n}.audio"),
            new LyricBinding(800 + n, style == PathStyle.Windows ? $@"a\{n}.lrc" : $"a/{n}.lrc", P("library", "a", $"{n}.lrc"), new ContentId(new string('c', 63) + n, 10 + n), P("ops", op, $"{n}.lrc"))))
            .ToImmutableArray();
        return new OperationBinding(op, null, 7, "11111111-2222-4333-8444-555555555500", 70, "11111111-2222-4333-8444-555555555501", style, P("library"), P("staging"), P("ops", op), tracks);
    }

    // ---------- J: pure journal ----------

    private static void BindingValidation()
    {
        var good = SampleBinding();
        Eq(0, JournalValidation.Validate(good).Length, "sample binding errors: " + string.Join(" | ", JournalValidation.Validate(good)));
        var t = good.Tracks[0];
        var variants = new (string, OperationBinding)[]
        {
            ("uppercase uuid", good with { MonitoredReleaseMbid = "aaaaaaaa-2222-4333-8444-555555555501".ToUpperInvariant() }),
            ("non-uuid operation", good with { OperationId = "op-1" }),
            ("album id", good with { AlbumId = 0 }),
            ("overlapping roots", good with { StagingRoot = good.LibraryRoot + (good.PathStyle == PathStyle.Windows ? @"\staging" : "/staging") }),
            ("no tracks", good with { Tracks = ImmutableArray<TrackBinding>.Empty }),
            ("duplicate track", good with { Tracks = good.Tracks.Add(t) }),
            ("incoming outside staging", good with { Tracks = good.Tracks.SetItem(0, t with { IncomingPath = t.OriginalPath + ".x" }) }),
            ("dot segment", good with { Tracks = good.Tracks.SetItem(0, t with { OriginalPath = good.LibraryRoot + (good.PathStyle == PathStyle.Windows ? @"\..\x" : "/../x") }) }),
            ("aliasing case-variant paths", good with { Tracks = good.Tracks.SetItem(0, t with { BackupAudioPath = t.Lyric!.BackupPath.ToUpperInvariant() }) }),
            ("equal contents", good with { Tracks = good.Tracks.SetItem(0, t with { Incoming = t.Original }) }),
            ("bad hash", good with { Tracks = good.Tracks.SetItem(0, t with { Original = new ContentId("XYZ", 1) }) }),
            ("zero length", good with { Tracks = good.Tracks.SetItem(0, t with { Original = t.Original with { Length = 0 } }) }),
            ("escaping lyric relative path", good with { Tracks = good.Tracks.SetItem(0, t with { Lyric = t.Lyric! with { RelativePath = "../x.lrc" } }) }),
            ("missing original row", good with { Tracks = good.Tracks.SetItem(0, t with { OriginalTrackFileId = 0 }) }),
            ("op root without id", good with { OperationRoot = good.LibraryRoot.Replace("library", "elsewhere", StringComparison.Ordinal) }),
        };
        foreach (var (name, binding) in variants)
        {
            True(!JournalValidation.Validate(binding).IsEmpty, $"accepted: {name}");
            Throws<JournalTransitionException>(() => JournalEngine.Begin(binding), $"Begin accepted {name}");
        }
    }

    private static void TransitionOrdering()
    {
        var m = JournalEngine.Begin(SampleBinding());
        Throws<JournalTransitionException>(() => JournalEngine.MarkInFlight(m, 1, JournalStep.DetachLyric), "detach before backup");
        Throws<JournalTransitionException>(() => JournalEngine.MarkInFlight(m, 1, JournalStep.NativeUpgrade), "native before backup");
        Throws<JournalTransitionException>(() => JournalEngine.MarkDone(m, 1, JournalStep.CopyBackup), "done without in-flight");
        var a = JournalEngine.MarkInFlight(m, 1, JournalStep.CopyBackup);
        Throws<JournalTransitionException>(() => JournalEngine.MarkInFlight(a, 2, JournalStep.CopyBackup), "two steps in flight");
        a = JournalEngine.MarkDone(a, 1, JournalStep.CopyBackup);
        Throws<JournalTransitionException>(() => JournalEngine.MarkInFlight(a, 1, JournalStep.DetachLyric), "detach before ALL backups");
        a = JournalEngine.MarkDone(JournalEngine.MarkInFlight(a, 2, JournalStep.CopyBackup), 2, JournalStep.CopyBackup);
        Throws<JournalTransitionException>(() => JournalEngine.MarkInFlight(a, 1, JournalStep.NativeUpgrade), "native while lyric still registered");
        a = JournalEngine.MarkDone(JournalEngine.MarkInFlight(a, 1, JournalStep.DetachLyric), 1, JournalStep.DetachLyric);
        a = JournalEngine.MarkInFlight(a, 1, JournalStep.NativeUpgrade);
        True(a.Generation > m.Generation, "generation increases");
        Eq(m.BindingDigest, a.BindingDigest, "binding digest immutable");
    }

    private static OperationManifest ThroughNative(int newTrackFileId)
    {
        var m = JournalEngine.Begin(SampleBinding());
        foreach (var id in new[] { 1, 2 }) m = JournalEngine.MarkDone(JournalEngine.MarkInFlight(m, id, JournalStep.CopyBackup), id, JournalStep.CopyBackup);
        m = JournalEngine.MarkDone(JournalEngine.MarkInFlight(m, 1, JournalStep.DetachLyric), 1, JournalStep.DetachLyric);
        m = JournalEngine.MarkInFlight(m, 1, JournalStep.NativeUpgrade);
        return JournalEngine.MarkDone(m, 1, JournalStep.NativeUpgrade, newTrackFileId: newTrackFileId);
    }

    private static void FreshIds()
    {
        Throws<JournalTransitionException>(() => ThroughNative(501), "reused deleted original ID");
        Throws<JournalTransitionException>(() => ThroughNative(502), "reused another bound original ID");
        Throws<JournalTransitionException>(() => ThroughNative(0), "non-positive ID");
        var m = ThroughNative(900);
        m = JournalEngine.MarkInFlight(m, 1, JournalStep.RelinkLyric);
        Throws<JournalTransitionException>(() => JournalEngine.MarkDone(m, 1, JournalStep.RelinkLyric, newLyricFileId: 801), "reused original lyric ID");
    }

    private static void TerminalStates()
    {
        var m = JournalEngine.Begin(SampleBinding());
        for (var i = 0; i < JournalEngine.MaxInterruptedAttempts; i++)
            m = JournalEngine.AbandonInFlight(JournalEngine.MarkInFlight(m, 1, JournalStep.CopyBackup), 1, JournalStep.CopyBackup);
        Eq(OperationStatus.Active, m.Status, "within the bound");
        m = JournalEngine.AbandonInFlight(JournalEngine.MarkInFlight(m, 1, JournalStep.CopyBackup), 1, JournalStep.CopyBackup);
        Eq(OperationStatus.RequiresReview, m.Status, "escalated");
        Throws<JournalTransitionException>(() => JournalEngine.MarkInFlight(m, 1, JournalStep.CopyBackup), "review is terminal");
        Throws<JournalTransitionException>(() => JournalEngine.Complete(JournalEngine.Begin(SampleBinding())), "complete with unfinished tracks");
    }

    private static void CodecRoundTrip()
    {
        var m = ThroughNative(900);
        var text = JournalCodec.Encode(m);
        var decoded = JournalCodec.Decode(text, m.Binding.OperationId, m.BindingDigest);
        True(decoded.Succeeded, $"decode: {decoded.Error}");
        Eq(text, JournalCodec.Encode(decoded.Manifest!), "canonical");
        Eq(JournalEngine.Begin(SampleBinding()).BindingDigest, m.BindingDigest, "digest stable across generations");
    }

    private static void CodecRejects()
    {
        var m = ThroughNative(900);
        var good = JournalCodec.Encode(m);
        var p1 = m.Progress[0];
        string Forge(OperationManifest x) => JournalCodec.Encode(x);
        var cases = new Dictionary<string, string>
        {
            ["truncated"] = good[..(good.Length / 2)],
            ["digest mismatch"] = good.Replace("\"interruptedAttempts\":0", "\"interruptedAttempts\":1", StringComparison.Ordinal),
            ["unknown property"] = good.Replace("\"payload\":{", "\"payload\":{\"x\":1,", StringComparison.Ordinal),
            ["duplicate property"] = good.Replace("\"format\":", "\"format\":\"y\",\"format\":", StringComparison.Ordinal),
            ["numeric enum"] = good.Replace("\"status\":\"Active\"", "\"status\":\"1\"", StringComparison.Ordinal),
            ["version"] = good.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            ["empty"] = "",
            ["oversize"] = new string(' ', JournalCodec.MaxDocumentChars + 1024),
            ["two in flight"] = Forge(m with { Progress = m.Progress.Select(p => p with { InFlight = p.TrackId == 2 ? JournalStep.DetachLyric : JournalStep.RelinkLyric }).ToImmutableArray() }),
            ["destructive before every backup"] = Forge(m with { Progress = m.Progress.SetItem(1, m.Progress[1] with { Phase = TrackPhase.Planned }) }),
            ["completed unfinished"] = Forge(m with { Status = OperationStatus.Completed }),
            ["review without reason"] = Forge(m with { Status = OperationStatus.RequiresReview }),
            ["phase without fresh id"] = Forge(m with { Progress = m.Progress.SetItem(0, p1 with { NewTrackFileId = null }) }),
            ["binding changed under same digest"] = Forge(m with { Binding = m.Binding with { AlbumId = 8 } }),
        };
        foreach (var (name, text) in cases)
            True(!JournalCodec.Decode(text, m.Binding.OperationId, m.BindingDigest).Succeeded, $"accepted: {name}");

        True(!JournalCodec.Decode(good, "00000000-0000-4000-8000-000000000000", m.BindingDigest).Succeeded, "accepted at the wrong location");
        True(!JournalCodec.Decode(good, m.Binding.OperationId, new string('0', 64)).Succeeded, "accepted a different original binding");
    }

    // ---------- R: pure reconciler over real lab observations ----------

    private static (OperationManifest, HostSnapshot, FilesystemSnapshot) LabState(string lab)
    {
        var store = Lab.Open(lab);
        return (store.Manifest, new FakeHost(lab).Snapshot(), FsObserver.Observe(store.Manifest.Binding));
    }

    private static void ReconcilerBlockers()
    {
        WithLab((lab, unusedBaseline) =>
        {
            Faults.Arm("crash", "lyric.101.afterPublish");
            _ = RunCatching(() => Lab.Run(lab));
            var (m, host, fs) = LabState(lab);
            Eq(Verdict.Proceed, Reconciler.Reconcile(m, host, fs).Verdict, "baseline");
            var t = m.Binding.Tracks[0];
            var mutations = new Dictionary<string, (HostSnapshot, FilesystemSnapshot)>
            {
                ["edition changed"] = (host with { Releases = host.Releases.Select(r => r with { Monitored = !r.Monitored }).ToImmutableArray() }, fs),
                ["two monitored"] = (host with { Releases = host.Releases.Select(r => r with { Monitored = true }).ToImmutableArray() }, fs),
                ["host unavailable"] = (host with { Obtained = false }, fs),
                ["mount unavailable"] = (host, fs with { Available = false }),
                ["album changed"] = (host with { AlbumId = 8 }, fs),
                ["recording changed"] = (host with { Tracks = host.Tracks.Select(x => x.TrackId == t.TrackId ? x with { RecordingMbid = LabScenario.RecordingMbid(999) } : x).ToImmutableArray() }, fs),
                ["stale original row path"] = (host with { TrackFiles = host.TrackFiles.Select(f => f.TrackFileId == t.OriginalTrackFileId ? f with { Path = t.OriginalPath + ".moved" } : f).ToImmutableArray() }, fs),
                ["unbound lyric linked"] = (host with { Lyrics = host.Lyrics.Add(new HostLyric(9999, t.OriginalTrackFileId, "x.lrc")) }, fs),
                ["link escape"] = (host, fs with { Files = fs.Files.SetItem(t.IncomingPath, fs.Files[t.IncomingPath] with { Escapes = true }) }),
                ["missing observation"] = (host, fs with { Files = fs.Files.Remove(t.BackupAudioPath) }),
                ["incoming changed"] = (host, fs with { Files = fs.Files.SetItem(t.IncomingPath, fs.Files[t.IncomingPath] with { Sha256 = new string('0', 64) }) }),
                ["lyric bytes differ"] = (host, fs with { Files = fs.Files.SetItem(t.Lyric!.Path, fs.Files[t.Lyric.Path] with { Length = 1 }) }),
                ["backup changed"] = (host, fs with { Files = fs.Files.SetItem(t.BackupAudioPath, FileObservation.Absent) }),
            };
            foreach (var (name, (h, f)) in mutations)
            {
                var r = Reconciler.Reconcile(m, h, f);
                Eq(Verdict.RequiresReview, r.Verdict, name);
                True(r.Next is null || r.Next.Action == RecoveryAction.MarkReview, $"{name}: only recording review is allowed, got {r.Next?.Action}");
            }
        });
    }

    private static void NoSuccessFromAbsence()
    {
        WithLab((lab, unusedBaseline) =>
        {
            Faults.Arm("crash", "native.101.afterInFlight");
            _ = RunCatching(() => Lab.Run(lab));
            var (m, host, fs) = LabState(lab);
            var t = m.Binding.Tracks[0];
            // Original gone, nothing placed, rows untouched: absence alone must not look like success.
            var gone = fs with { Files = fs.Files.SetItem(t.OriginalPath, FileObservation.Absent).SetItem(t.IncomingPath, FileObservation.Absent) };
            var r = Reconciler.Reconcile(m, host, gone);
            Eq(Verdict.RequiresReview, r.Verdict, "absence of both original and incoming");
            var hostWithoutRow = host with { TrackFiles = host.TrackFiles.Where(f => f.TrackFileId != t.OriginalTrackFileId).ToImmutableArray() };
            var r2 = Reconciler.Reconcile(m, hostWithoutRow, gone);
            Eq(Verdict.RequiresReview, r2.Verdict, "row also gone, but no positive evidence of placement");
        });
    }

    // ---------- L: lab ----------

    private static void HappyPath()
    {
        WithLab((lab, baseline) =>
        {
            var report = Lab.Run(lab);
            Eq(Verdict.Completed, Lab.Reconcile(lab).Verdict, "completed");
            Eq(OperationStatus.Completed, report.Status, "status");
            NoViolations(lab, baseline, "after completion");
            var host = new FakeHost(lab).Load();
            foreach (var trackId in LabScenario.TargetTrackIds)
            {
                var track = host.Tracks.Single(t => t.TrackId == trackId);
                var row = host.TrackFiles.Single(f => f.TrackFileId == track.TrackFileId);
                Eq(LabScenario.DestinationPath(lab, trackId), row.Path, $"track {trackId} row path");
                Eq(baseline.Incoming[trackId], DurableFiles.Hash(row.Path), $"track {trackId} incoming bytes");
                True(row.TrackFileId >= 900, $"track {trackId} fresh row id");
                True(host.Lyrics.Count(l => l.TrackFileId == row.TrackFileId) == 1, $"track {trackId} lyric relinked");
            }

            var again = Lab.Recover(lab, approve: false);
            Eq(0, again.Executed.Count, "idempotent re-run executes nothing");
        });
    }

    private static List<string>? _points;

    /// <summary>Every fault point the happy path passes through, in order.</summary>
    private static List<string> HappyPoints()
    {
        if (_points is not null) return _points;
        var points = new List<string>();
        WithLab((lab, unusedBaseline) =>
        {
            Faults.Arm("crash", "__never__");
            Lab.Run(lab);
            points.AddRange(Faults.VisitedPoints);
        });
        _points = points.Distinct().ToList();
        True(_points.Count > 60, $"expected many fault points, saw {_points.Count}");
        return _points;
    }

    private enum Expect { AutoComplete, ApproveOriginal, ApproveIncoming }

    private static Expect Expected(string point)
    {
        if (point.StartsWith("native.", StringComparison.Ordinal))
        {
            if (point.EndsWith(".afterRowDelete", StringComparison.Ordinal) || point.Contains(".rowDelete.afterRename", StringComparison.Ordinal)) return Expect.ApproveOriginal;
            if (point.EndsWith(".afterMove", StringComparison.Ordinal) || point.Contains(".link.before", StringComparison.Ordinal)) return Expect.ApproveIncoming;
        }

        return Expect.AutoComplete;
    }

    private static void CrashMatrix() => FaultMatrix("crash");

    private static void FailureMatrix() => FaultMatrix("fail");

    private static void FaultMatrix(string mode)
    {
        var counts = new Dictionary<Expect, int>();
        foreach (var point in HappyPoints())
        {
            WithLab((lab, baseline) =>
            {
                Faults.Arm(mode, point);
                var error = RunCatching(() => Lab.Run(lab));
                True(error is not null, $"{point}: fault did not fire");
                Faults.Arm(null, null);
                NoViolations(lab, baseline, $"{mode}@{point} immediately after fault");

                var expect = Expected(point);
                // A fault while creating the operation directory before the first manifest leaves no journal.
                var store = OperationStore.TryOpen(Path.Combine(LabScenario.Ops(lab), Lab.OperationId(lab)));
                if (store.Store is null)
                {
                    True(point.StartsWith("intent.", StringComparison.Ordinal) || point.StartsWith("manifest.g1.", StringComparison.Ordinal) || point.StartsWith("lab.", StringComparison.Ordinal), $"{point}: unreadable journal after fault: {store.Error}");
                    True(HostBytes(lab).Contains("\"TrackFileId\": 501", StringComparison.Ordinal), $"{point}: host changed before intent was published");
                    counts[Expect.AutoComplete] = counts.GetValueOrDefault(Expect.AutoComplete) + 1;
                    return;
                }

                var first = Lab.Recover(lab, approve: false);
                NoViolations(lab, baseline, $"{mode}@{point} after automatic recovery");
                switch (expect)
                {
                    case Expect.AutoComplete:
                        Eq(OperationStatus.Completed, first.Status, $"{mode}@{point} auto recovery");
                        break;
                    case Expect.ApproveOriginal:
                    case Expect.ApproveIncoming:
                        Eq(Verdict.OperatorApprovalRequired, first.FinalVerdict, $"{mode}@{point} needs operator");
                        var wanted = expect == Expect.ApproveOriginal ? RecoveryAction.InsertFreshTrackFileForOriginal : RecoveryAction.InsertFreshTrackFileForIncoming;
                        Eq(wanted, first.Pending?.Action, $"{mode}@{point} proposal");
                        var hostBefore = HostBytes(lab);
                        Eq(0, Lab.Recover(lab, approve: false).Executed.Count, $"{mode}@{point} unapproved recovery is inert");
                        Eq(hostBefore, HostBytes(lab), $"{mode}@{point} host unchanged without approval");
                        ExecutorReport approved;
                        var rounds = 0;
                        do { approved = Lab.Recover(lab, approve: true); rounds++; }
                        while (approved.Pending is { Mode: ActionMode.OperatorApproval } && rounds < 5);
                        NoViolations(lab, baseline, $"{mode}@{point} after approved recovery");
                        if (expect == Expect.ApproveIncoming) Eq(OperationStatus.Completed, approved.Status, $"{mode}@{point} forward recovery completes");
                        else
                        {
                            Eq(OperationStatus.RequiresReview, approved.Status, $"{mode}@{point} rollback ends in review");
                            var trackId = int.Parse(point.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture);
                            Eq(baseline.Originals[trackId], DurableFiles.Hash(LabScenario.OriginalPath(lab, trackId)), $"{mode}@{point} original restored in place");
                            var host = new FakeHost(lab).Load();
                            var link = host.Tracks.Single(t => t.TrackId == trackId).TrackFileId;
                            True(link >= 900 && host.TrackFiles.Any(f => f.TrackFileId == link && f.Path == LabScenario.OriginalPath(lab, trackId)), $"{mode}@{point} fresh row for restored original");
                            True(host.Lyrics.Count(l => l.TrackFileId == link) == 1, $"{mode}@{point} lyric reattached");
                        }

                        break;
                }

                counts[expect] = counts.GetValueOrDefault(expect) + 1;
                var settledHost = HostBytes(lab);
                var settledManifest = File.ReadAllText(Path.Combine(Lab.Open(lab).Directory, OperationStore.ManifestName));
                Eq(0, Lab.Recover(lab, approve: false).Executed.Count, $"{mode}@{point} repeated recovery is idempotent");
                Eq(settledHost, HostBytes(lab), $"{mode}@{point} host stable on repeat");
                Eq(settledManifest, File.ReadAllText(Path.Combine(Lab.Open(lab).Directory, OperationStore.ManifestName)), $"{mode}@{point} manifest stable on repeat");
            });
        }

        True(counts.GetValueOrDefault(Expect.ApproveOriginal) >= 3 && counts.GetValueOrDefault(Expect.ApproveIncoming) >= 3, $"matrix did not reach operator cases: {string.Join(",", counts)}");
        Console.WriteLine($"  {mode} matrix: {HappyPoints().Count} points; {string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    private static void CrashDuringRecovery()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "native.102.afterRecycle");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm("crash", "restore.102.beforeRename");
            True(RunCatching(() => Lab.Recover(lab, false)) is SimulatedCrashException, "crash during restore fired");
            NoViolations(lab, baseline, "after crash during restore");
            Faults.Arm(null, null);
            var final = Lab.Recover(lab, false);
            Eq(OperationStatus.Completed, final.Status, "recovers after a crash in recovery");
            NoViolations(lab, baseline, "final");
            True(Directory.EnumerateFiles(LabScenario.AlbumDir(lab), "*.partial~").Count() <= 1, "at most the one interrupted restore temp remains");
        });
    }

    private static void EditionSwitch()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "lyric.101.afterPublish");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            new FakeHost(lab).SetMonitored(LabScenario.AlternateReleaseId);
            var rows = new FakeHost(lab).Load().TrackFiles.Select(f => f.TrackFileId).ToList();
            var report = Lab.Recover(lab, false);
            Eq(OperationStatus.RequiresReview, report.Status, "review");
            True(!report.Executed.Any(e => e.StartsWith(nameof(RecoveryAction.InvokeNativeUpgrade), StringComparison.Ordinal)), "no native call");
            True(rows.SequenceEqual(new FakeHost(lab).Load().TrackFiles.Select(f => f.TrackFileId)), "no row changed");
            new FakeHost(lab).SetMonitored(LabScenario.MonitoredReleaseId);
            Eq(OperationStatus.RequiresReview, Lab.Recover(lab, false).Status, "review is sticky even after the edition is restored");
            NoViolations(lab, baseline, "after edition switch");
        });
    }

    private static void TamperMatrix()
    {
        var tampers = new Dictionary<string, Action<string>>
        {
            ["original bytes changed"] = lab => File.AppendAllText(LabScenario.OriginalPath(lab, 102), "x"),
            ["incoming bytes changed"] = lab => File.AppendAllText(LabScenario.IncomingPath(lab, 102), "x"),
            ["conflicting destination"] = lab => File.WriteAllText(LabScenario.DestinationPath(lab, 103), "someone else's file"),
            ["stale original row"] = lab =>
            {
                var host = new FakeHost(lab);
                var s = host.Load();
                s.TrackFiles = s.TrackFiles.Select(f => f.TrackFileId == 502 ? f with { Path = f.Path + ".elsewhere" } : f).ToList();
                host.Save(s, "tamper");
            },
            ["host unavailable"] = lab => File.WriteAllText(Path.Combine(lab, FakeHost.UnavailableMarker), "down"),
            ["mount disconnected"] = lab => Directory.Move(LabScenario.Staging(lab), LabScenario.Staging(lab) + "-offline"),
        };
        foreach (var (name, tamper) in tampers)
        {
            WithLab((lab, baseline) =>
            {
                Faults.Arm("crash", "backup.103.afterPublish");
                _ = RunCatching(() => Lab.Run(lab));
                Faults.Arm(null, null);
                var hostBefore = new FakeHost(lab).Load().TrackFiles.Count;
                tamper(lab);
                var report = Lab.Recover(lab, false);
                Eq(Verdict.RequiresReview, report.FinalVerdict, name);
                True(!report.Executed.Any(e => e.StartsWith("InvokeNativeUpgrade", StringComparison.Ordinal) || e.StartsWith("DetachLyric", StringComparison.Ordinal)), $"{name}: destructive action executed");
                if (name is not ("host unavailable" or "stale original row")) Eq(hostBefore, new FakeHost(lab).Load().TrackFiles.Count, $"{name}: rows changed");
            });
        }

        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "backup.103.afterPublish");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            File.AppendAllText(LabScenario.LyricPath(lab, 101), "edited");
            Eq(Verdict.RequiresReview, Lab.Recover(lab, false).FinalVerdict, "lyric bytes differ");
        });
    }

    private static void CorruptBackup()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "backup.103.afterPublish");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            var backup = Lab.Open(lab).Manifest.BindingFor(101).BackupAudioPath;
            using (var stream = new FileStream(backup, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length / 2);
            var report = Lab.Recover(lab, false);
            Eq(Verdict.RequiresReview, report.FinalVerdict, "incomplete backup");
            True(!report.Executed.Any(e => e.StartsWith("InvokeNativeUpgrade", StringComparison.Ordinal)), "no native call");
            Eq(baseline.Originals[101], DurableFiles.Hash(LabScenario.OriginalPath(lab, 101)), "original untouched");
        });
    }

    private static void ManifestCorruption()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "backup.103.afterPublish");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            var dir = Lab.Open(lab).Directory;
            var manifestPath = Path.Combine(dir, OperationStore.ManifestName);
            var good = File.ReadAllText(manifestPath);

            File.WriteAllText(manifestPath, good[..(good.Length / 3)]);
            Throws<InvalidOperationException>(() => Lab.Recover(lab, false), "truncated manifest accepted");
            Throws<InvalidOperationException>(() => OperationStore.Create(LabScenario.Ops(lab), LabScenario.Bind(lab, Guid.NewGuid().ToString("D"))), "new operation started beside an unreadable one (silent reset)");

            // Syntactically valid, correctly hashed, but bound to a different operation.
            var other = JournalEngine.Begin(LabScenario.Bind(lab, Guid.NewGuid().ToString("D")));
            File.WriteAllText(manifestPath, JournalCodec.Encode(other));
            Throws<InvalidOperationException>(() => Lab.Recover(lab, false), "foreign manifest accepted");

            File.WriteAllText(manifestPath, good);
            File.WriteAllText(Path.Combine(dir, OperationStore.BindingDigestName), new string('0', 64));
            Throws<InvalidOperationException>(() => Lab.Recover(lab, false), "binding digest conflict accepted");
            File.WriteAllText(Path.Combine(dir, OperationStore.BindingDigestName), Lab.OperationId(lab) == string.Empty ? "" : JournalCodec.Decode(good).Manifest!.BindingDigest);

            Throws<InvalidOperationException>(() => OperationStore.Create(LabScenario.Ops(lab), LabScenario.Bind(lab, Lab.OperationId(lab))), "duplicate operation ID accepted");
            Throws<InvalidOperationException>(() => OperationStore.Create(LabScenario.Ops(lab), LabScenario.Bind(lab, Guid.NewGuid().ToString("D"))), "second unfinished operation for the album accepted");
            NoViolations(lab, baseline, "after corruption attempts");
            Eq(OperationStatus.Completed, Lab.Recover(lab, false).Status, "restored manifest resumes");
        });
    }

    private static void StrayTemp()
    {
        WithLab((lab, baseline) =>
        {
            var points = HappyPoints();
            var target = points.First(p => p.StartsWith("manifest.g", StringComparison.Ordinal) && p.EndsWith(".beforeRename", StringComparison.Ordinal) && p != "manifest.g1.beforeRename");
            Faults.Arm("crash", target);
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            var store = Lab.Open(lab);
            var generation = int.Parse(target.Split('.')[1][1..], System.Globalization.CultureInfo.InvariantCulture);
            Eq((long)generation - 1, store.Manifest.Generation, "previous generation is the durable one");
            Eq(1, store.StrayTempFiles().Count(), "stray temp reported");
            Eq(OperationStatus.Completed, Lab.Recover(lab, false).Status, "resumes");
            NoViolations(lab, baseline, "final");
        });
    }

    private static void HostHazardFidelity()
    {
        WithLab((lab, baseline) =>
        {
            // Bypass the journal on purpose: the stock-ordered upgrade with the lyric still registered.
            new FakeHost(lab).NativeUpgrade(101, LabScenario.IncomingPath(lab, 101), LabScenario.DestinationPath(lab, 101));
            True(!File.Exists(LabScenario.LyricPath(lab, 101)), "registered lyric was recycled by the stock-ordered delete");
            True(Lab.CheckInvariants(lab, baseline).Any(v => v.Contains("lyric", StringComparison.Ordinal)), "invariant checker detects the loss");
        });
    }

    private static void UnapprovedRecoveryIsInert()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "native.102.afterMove");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            var first = Lab.Recover(lab, false);
            Eq(Verdict.OperatorApprovalRequired, first.FinalVerdict, "operator needed");
            var host = HostBytes(lab);
            var manifest = File.ReadAllText(Path.Combine(Lab.Open(lab).Directory, OperationStore.ManifestName));
            for (var i = 0; i < 3; i++) Eq(0, Lab.Recover(lab, false).Executed.Count, "inert");
            Eq(host, HostBytes(lab), "host unchanged");
            Eq(manifest, File.ReadAllText(Path.Combine(Lab.Open(lab).Directory, OperationStore.ManifestName)), "manifest unchanged");
        });
    }

    private static void SymlinkEscape()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "backup.103.afterPublish");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            var outside = Path.Combine(Path.GetTempPath(), "crescendarr-m13-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            try
            {
                var destination = LabScenario.DestinationPath(lab, 103);
                try { File.CreateSymbolicLink(destination, Path.Combine(outside, "target.flac")); }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    NotExecuted.Add($"L12 symlink creation not permitted on this host ({error.GetType().Name})");
                    return;
                }

                var report = Lab.Recover(lab, false);
                Eq(Verdict.RequiresReview, report.FinalVerdict, "symlink treated as escape");
                True(!report.Executed.Any(e => e.StartsWith("InvokeNativeUpgrade", StringComparison.Ordinal)), "no native call");
                True(Directory.EnumerateFileSystemEntries(outside).Count() == 0, "nothing written outside the lab");
            }
            finally
            {
                Directory.Delete(outside, true);
            }
        });
    }

    private static void LabGuards()
    {
        Throws<LabSafetyException>(() => LabSafety.PrepareNewRoot(OperatingSystem.IsWindows() ? @"C:\crescendarr-not-temp" : "/var/crescendarr-not-temp"), "outside temp");
        var unmarked = NewLabPath();
        Directory.CreateDirectory(unmarked);
        try { Throws<LabSafetyException>(() => LabSafety.RequireExistingRoot(unmarked), "unmarked root"); }
        finally { Directory.Delete(unmarked); }
        Environment.SetEnvironmentVariable(LabSafety.OptInVariable, null);
        try { Throws<LabSafetyException>(() => LabSafety.PrepareNewRoot(NewLabPath()), "missing opt-in"); }
        finally { Environment.SetEnvironmentVariable(LabSafety.OptInVariable, LabSafety.OptInValue); }
    }

    private static void ConcurrentChangeBeforeNative()
    {
        var changes = new Dictionary<string, Action<string>>
        {
            ["original edited"] = lab => File.AppendAllText(LabScenario.OriginalPath(lab, 102), "concurrent edit"),
            ["destination occupied"] = lab => File.WriteAllText(LabScenario.DestinationPath(lab, 103), "concurrent file"),
            ["edition switched"] = lab => new FakeHost(lab).SetMonitored(LabScenario.AlternateReleaseId),
        };
        foreach (var (name, change) in changes)
        {
            WithLab((lab, baseline) =>
            {
                var track = name == "destination occupied" ? 103 : 102;
                Faults.Interleave = ($"native.{track}.afterInFlight", () => change(lab));
                var report = Lab.Run(lab);
                Faults.Interleave = null;
                Eq(OperationStatus.RequiresReview, report.Status, $"{name}: review");
                var host = new FakeHost(lab).Load();
                Eq(500 + track - 100, host.Tracks.Single(t => t.TrackId == track).TrackFileId, $"{name}: track {track} still linked to its original row");
                True(File.Exists(LabScenario.IncomingPath(lab, track)), $"{name}: incoming still staged (native never called)");
                True(host.TrackFiles.Any(f => f.TrackFileId == 500 + track - 100), $"{name}: original row intact");
            });
        }
    }

    // ---------- X: independent review findings (A, B, C) and broader adversarial review ----------

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLinkWindows(string newPath, string existingPath, IntPtr security);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkPosix(string existingPath, string newPath);

    /// <summary>Creates a hardlink; returns false (caller records NOT_EXECUTED) when the platform refuses.</summary>
    private static bool TryHardLink(string existing, string newPath)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CreateHardLinkWindows(newPath, existing, IntPtr.Zero) : LinkPosix(existing, newPath) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryDirectorySymlink(string link, string target, string spec)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            NotExecuted.Add($"{spec} directory symlink creation not permitted on this host ({error.GetType().Name})");
            return false;
        }
    }

    private static string[] DestructiveExecuted(ExecutorReport report) =>
        report.Executed.Where(e => e.StartsWith("DetachLyric", StringComparison.Ordinal) || e.StartsWith("InvokeNativeUpgrade", StringComparison.Ordinal) ||
                                   e.StartsWith("RestoreOriginalBytes", StringComparison.Ordinal) || e.StartsWith("Insert", StringComparison.Ordinal)).ToArray();

    private static void AddHostRow(string lab, int id, string path)
    {
        var host = new FakeHost(lab);
        var s = host.Load();
        s.TrackFiles.Add(new HostTrackFile(id, path));
        host.Save(s, "test.addRow");
    }

    private static Exception? Record(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    /// <summary>A: ancestor link must be refused for new roots and reopened roots; nothing written through it.</summary>
    private static void AncestorLinkEscape()
    {
        var temp = LabSafety.Canonical(Path.GetTempPath());
        var outside = Path.Combine(temp, "crescendarr-m13-outside-" + Guid.NewGuid().ToString("N"));
        var alias = Path.Combine(temp, "crescendarr-m13-alias-" + Guid.NewGuid().ToString("N"));
        var realAlias = Path.Combine(temp, "crescendarr-m13-alias-" + Guid.NewGuid().ToString("N"));
        string? real = null;
        Directory.CreateDirectory(outside);
        try
        {
            if (!TryDirectorySymlink(alias, outside, "X01")) return;
            Throws<LabSafetyException>(() => LabSafety.PrepareNewRoot(Path.Combine(alias, "new-lab")), "new root under an ancestor link accepted");
            Throws<LabSafetyException>(() => LabSafety.PrepareNewRoot(alias), "new root that is itself a link accepted");
            Eq(0, Directory.EnumerateFileSystemEntries(outside).Count(), "something was created through the alias");

            // A marked lab that is lexically inside temp but physically outside it.
            File.WriteAllText(Path.Combine(outside, LabSafety.MarkerName), LabSafety.MarkerContent);
            Throws<LabSafetyException>(() => LabSafety.RequireExistingRoot(alias), "marked root behind a link accepted");
            Eq(1, Directory.EnumerateFileSystemEntries(outside).Count(), "only the test's own marker exists outside");

            // A real lab reopened through an alias of its own root.
            real = LabScenario.Create(NewLabPath());
            if (!TryDirectorySymlink(realAlias, real, "X01")) return;
            var before = Directory.EnumerateFileSystemEntries(real, "*", SearchOption.AllDirectories).Count();
            Throws<LabSafetyException>(() => LabSafety.RequireExistingRoot(realAlias), "existing root reopened through a link");
            Throws<LabSafetyException>(() => Lab.Run(realAlias), "lab ran through a link");
            Eq(before, Directory.EnumerateFileSystemEntries(real, "*", SearchOption.AllDirectories).Count(), "lab modified through the alias");
        }
        finally
        {
            if (Directory.Exists(realAlias)) Directory.Delete(realAlias);
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(outside, true);
            if (real is not null) Directory.Delete(real, true);
        }
    }

    /// <summary>A: a lab-internal directory replaced by a link must stop the lab before any write through it.</summary>
    private static void InternalLinkEscape()
    {
        WithLab((lab, baseline) =>
        {
            var outside = Path.Combine(Path.GetTempPath(), "crescendarr-m13-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            var ops = LabScenario.Ops(lab);
            try
            {
                Directory.Delete(ops);
                if (!TryDirectorySymlink(ops, outside, "X02")) { Directory.CreateDirectory(ops); return; }
                var error = Record(() => Lab.Run(lab));
                True(error is LabSafetyException, $"run through a linked ops directory was not refused by the lab guard: {error?.GetType().Name ?? "no error"}");
                Eq(0, Directory.EnumerateFileSystemEntries(outside).Count(), "journal written through the link");
            }
            finally
            {
                if (new DirectoryInfo(ops).LinkTarget is not null) Directory.Delete(ops);
                Directory.Delete(outside, true);
            }
        });
    }

    /// <summary>B: a different row at the original path, before the first step, during recovery, and just before native.</summary>
    private static void DuplicatePathRows()
    {
        // Before the first step (track 103 destination differs from its original path).
        WithLab((lab, baseline) =>
        {
            AddHostRow(lab, 777, LabScenario.OriginalPath(lab, 103));
            var report = Lab.Run(lab);
            Eq(OperationStatus.RequiresReview, report.Status, "duplicate row before first step");
            Eq(0, DestructiveExecuted(report).Length, "destructive action with a duplicate row: " + string.Join(",", report.Executed));
            True(File.Exists(LabScenario.IncomingPath(lab, 103)), "incoming 103 still staged");
        });

        // Case-variant spelling of the same path (NAS case folding).
        WithLab((lab, baseline) =>
        {
            AddHostRow(lab, 778, LabScenario.OriginalPath(lab, 101).ToUpperInvariant());
            var report = Lab.Run(lab);
            Eq(OperationStatus.RequiresReview, report.Status, "case-variant duplicate row");
            Eq(0, DestructiveExecuted(report).Length, "destructive action with a case-variant row");
        });

        // During recovery of an interrupted native step, even with operator approval enabled.
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "native.103.afterInFlight");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            AddHostRow(lab, 779, LabScenario.OriginalPath(lab, 103));
            var report = Lab.Recover(lab, approve: true);
            Eq(Verdict.RequiresReview, report.FinalVerdict, "duplicate row during recovery");
            Eq(0, DestructiveExecuted(report).Length, "destructive action during recovery: " + string.Join(",", report.Executed));
            True(File.Exists(LabScenario.IncomingPath(lab, 103)), "incoming 103 still staged");
            NoViolations(lab, baseline, "after duplicate row in recovery");
        });

        // Immediately before the destructive native call.
        WithLab((lab, baseline) =>
        {
            Faults.Interleave = ("native.103.afterInFlight", () => AddHostRow(lab, 780, LabScenario.OriginalPath(lab, 103)));
            var report = Lab.Run(lab);
            Faults.Interleave = null;
            Eq(OperationStatus.RequiresReview, report.Status, "duplicate row just before native");
            True(File.Exists(LabScenario.IncomingPath(lab, 103)), "native never called for 103");
            Eq(503, new FakeHost(lab).Load().Tracks.Single(t => t.TrackId == 103).TrackFileId, "track 103 still linked to its original");
        });

        // Another (unbound) track linked to a bound original file: the native delete would orphan it.
        WithLab((lab, baseline) =>
        {
            var host = new FakeHost(lab);
            var s = host.Load();
            s.Tracks.Add(new HostTrack(199, LabScenario.RecordingMbid(199), 502));
            host.Save(s, "test.sharedFile");
            var report = Lab.Run(lab);
            Eq(OperationStatus.RequiresReview, report.Status, "shared original file");
            Eq(0, DestructiveExecuted(report).Length, "destructive action with a shared original file");
        });
    }

    /// <summary>C: physical lyric path that is not artist root + registered relative path.</summary>
    private static void LyricIdentityMismatch()
    {
        // A decoy .lrc bound as the physical file while the registered relative path names the real one.
        WithLab((lab, baseline) =>
        {
            var decoy = Path.Combine(LabScenario.AlbumDir(lab), "decoy.lrc");
            File.WriteAllText(decoy, "[00:01.00] decoy lyric\n");
            var binding = LabScenario.Bind(lab, Lab.OperationId(lab));
            var t = binding.Tracks[0];
            var mismatched = binding with { Tracks = binding.Tracks.SetItem(0, t with { Lyric = t.Lyric! with { Path = decoy, Content = DurableFiles.Hash(decoy) } }) };
            var hostBefore = HostBytes(lab);
            var error = Record(() => LabExecutor.Run(lab, OperationStore.Create(LabScenario.Ops(lab), mismatched)));
            True(error is not null, "mismatched lyric binding was accepted and executed");
            Eq(hostBefore, HostBytes(lab), "host changed for a mismatched lyric binding");
            NoViolations(lab, baseline, "after mismatched lyric binding");
        });

        // The registered lyric goes missing while the journal watches a byte-identical decoy.
        WithLab((lab, baseline) =>
        {
            var decoy = Path.Combine(LabScenario.AlbumDir(lab), "decoy.lrc");
            File.Copy(LabScenario.LyricPath(lab, 101), decoy);
            var binding = LabScenario.Bind(lab, Lab.OperationId(lab));
            var t = binding.Tracks[0];
            var mismatched = binding with { Tracks = binding.Tracks.SetItem(0, t with { Lyric = t.Lyric! with { Path = decoy } }) };
            File.Delete(LabScenario.LyricPath(lab, 101));
            var error = Record(() => LabExecutor.Run(lab, OperationStore.Create(LabScenario.Ops(lab), mismatched)));
            var host = new FakeHost(lab).Load();
            True(error is not null || host.Lyrics.Any(l => l.LyricFileId == 801), "journal proceeded past lyric detach while the real registered lyric was missing");
        });

        // The host artist root changes after planning (fresh observation disagrees with the binding).
        WithLab((lab, baseline) =>
        {
            Faults.Interleave = ("native.101.afterInFlight", () =>
            {
                var host = new FakeHost(lab);
                var s = host.Load();
                s.ArtistPath = Path.Combine(LabScenario.Library(lab), "Other Artist");
                host.Save(s, "test.artist");
            });
            var report = Record(() => Lab.Run(lab));
            Faults.Interleave = null;
            True(File.Exists(LabScenario.IncomingPath(lab, 101)), "native called after the artist root changed");
        });
    }

    /// <summary>Broader review: a hardlink planted at the operation-owned backup path must never let a backup write through to the original.</summary>
    private static void HardlinkedBackupPath()
    {
        WithLab((lab, baseline) =>
        {
            Faults.Arm("crash", "backup.101.afterInFlight");
            _ = RunCatching(() => Lab.Run(lab));
            Faults.Arm(null, null);
            var backup = Lab.Open(lab).Manifest.BindingFor(101).BackupAudioPath;
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (!TryHardLink(LabScenario.OriginalPath(lab, 101), backup)) { NotExecuted.Add("X05 hardlink creation not supported on this host"); return; }
            _ = Record(() => Lab.Recover(lab, approve: false));
            Eq(baseline.Originals[101], DurableFiles.Hash(LabScenario.OriginalPath(lab, 101)), "original bytes destroyed through a hardlinked backup path");
            NoViolations(lab, baseline, "after hardlinked backup path");
        });
    }

    // ---------- K: real process kill ----------

    private static (int Exit, string Output) RunLabProcess(string command, string lab, string? pausePoint = null, string? extra = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Crescendarr.RecoveryLab.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add(command);
        info.ArgumentList.Add(lab);
        if (extra is not null) info.ArgumentList.Add(extra);
        info.Environment[LabSafety.OptInVariable] = LabSafety.OptInValue;
        info.Environment[Faults.Variable] = pausePoint is null ? "" : $"pause:{pausePoint}";
        using var process = Process.Start(info)!;
        var output = new StringBuilder();
        if (pausePoint is not null)
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                output.AppendLine(line);
                if (line == $"CHECKPOINT {pausePoint}")
                {
                    process.Kill(entireProcessTree: true); // SIGKILL on Linux: no finally blocks, no flush.
                    process.WaitForExit();
                    return (-9, output.ToString());
                }
            }

            process.WaitForExit();
            throw new InvalidOperationException($"checkpoint {pausePoint} never reached: {output}{process.StandardError.ReadToEnd()}");
        }

        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException(command); }
        return (process.ExitCode, output.ToString());
    }

    private static void KillAndReopen()
    {
        var cases = new (string Point, string ExpectAfterRecover)[]
        {
            ("backup.102.audio.midCopy", "Completed"),
            ("lyric.102.afterDetach", "Completed"),
            ("native.102.afterRecycle", "Completed"),
            ("native.102.afterRowDelete", "OperatorApprovalRequired"),
            ("native.102.afterMove", "OperatorApprovalRequired"),
            ("relink.102.afterInsert", "Completed"),
        };
        var manifestPoint = HappyPoints().First(p => p.StartsWith("manifest.g", StringComparison.Ordinal) && p.EndsWith(".beforeRename", StringComparison.Ordinal) &&
            HappyPoints().IndexOf(p) > HappyPoints().IndexOf("native.102.afterLink"));
        cases = cases.Append((manifestPoint, "Completed")).ToArray();

        foreach (var (point, expect) in cases)
        {
            var lab = NewLabPath();
            try
            {
                var init = RunLabProcess("init", lab);
                Eq(0, init.Exit, "init: " + init.Output);
                var canonical = LabSafety.Canonical(lab);
                var baseline = Lab.Baseline(canonical);
                var killed = RunLabProcess("run", canonical, point);
                Eq(-9, killed.Exit, $"{point}: killed");
                NoViolations(canonical, baseline, $"SIGKILL@{point}");

                var reconcile = RunLabProcess("reconcile", canonical);
                Eq(0, reconcile.Exit, $"{point}: reconcile process: {reconcile.Output}");
                True(reconcile.Output.Contains("RECONCILIATION ", StringComparison.Ordinal), $"{point}: no reconciliation output");

                var recover = RunLabProcess("recover", canonical);
                Eq(0, recover.Exit, $"{point}: recover process: {recover.Output}");
                var expectStatus = expect == "Completed" ? "\"status\":\"Completed\"" : "\"verdict\":\"OperatorApprovalRequired\"";
                True(recover.Output.Contains(expectStatus, StringComparison.Ordinal), $"{point}: expected {expect}, got {recover.Output}");
                NoViolations(canonical, baseline, $"SIGKILL@{point} after separate-process recovery");

                var repeat = RunLabProcess("recover", canonical);
                True(repeat.Output.Contains("\"executed\":[]", StringComparison.Ordinal), $"{point}: repeat recovery executed actions: {repeat.Output}");
            }
            finally
            {
                try { Directory.Delete(lab, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        Console.WriteLine($"  killed {cases.Length} separate processes; each reopened by two further processes");
    }
}
