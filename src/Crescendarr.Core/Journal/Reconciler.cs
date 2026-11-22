using System.Collections.Immutable;
using Crescendarr.Core.Inventory;

namespace Crescendarr.Core.Journal;

// ---------- fresh observations supplied by an executor (abstract; no host types) ----------

/// <summary>What the executor saw at one path just now. <see cref="Escapes"/> is true when the path
/// or any parent is a link/reparse point, or resolves outside its root.</summary>
public sealed record FileObservation(bool Exists, long? Length, string? Sha256, bool Escapes)
{
    public static FileObservation Absent { get; } = new(false, null, null, false);

    public bool Is(ContentId content) => Exists && !Escapes && Length == content.Length && string.Equals(Sha256, content.Sha256, StringComparison.Ordinal);
}

public sealed record FilesystemSnapshot(bool Available, ImmutableDictionary<string, FileObservation> Files);

public sealed record HostRelease(int ReleaseId, string ReleaseMbid, bool Monitored);

public sealed record HostTrack(int TrackId, string RecordingMbid, int TrackFileId);

public sealed record HostTrackFile(int TrackFileId, string Path);

public sealed record HostLyric(int LyricFileId, int TrackFileId, string RelativePath);

/// <summary>A fresh host/database view. <see cref="Obtained"/> false means it could not be read.</summary>
public sealed record HostSnapshot(
    bool Obtained,
    int AlbumId,
    ImmutableArray<HostRelease> Releases,
    ImmutableArray<HostTrack> Tracks,
    ImmutableArray<HostTrackFile> TrackFiles,
    ImmutableArray<HostLyric> Lyrics);

// ---------- results ----------

public enum TrackCondition
{
    NotStarted = 1,
    BackupPending = 2,
    BackupVerified = 3,
    LyricDetached = 4,
    NativeNotStarted = 5,
    OriginalMovedRowIntact = 6,
    OrphanedOriginalBytesMissing = 7,
    OrphanedOriginalBytesRestored = 8,
    IncomingPlacedUnlinked = 9,
    Upgraded = 10,
    LyricRelinked = 11,
    RolledBackLyricDetached = 12,
    RolledBack = 13,
    Inconsistent = 14,
}

public enum RecoveryAction
{
    None = 0,
    CopyBackup = 1,
    DetachLyric = 2,

    /// <summary>The only action that permits calling the host's destructive native upgrade.</summary>
    InvokeNativeUpgrade = 3,
    RelinkLyric = 4,
    RecordStepDone = 5,
    AbandonStep = 6,
    RestoreOriginalBytes = 7,
    InsertFreshTrackFileForOriginal = 8,
    InsertFreshTrackFileForIncoming = 9,
    ReattachOriginalLyric = 10,
    CompleteOperation = 11,
    MarkReview = 12,
}

public enum ActionMode
{
    Automatic = 1,

    /// <summary>A host database write after an interruption. Never executed without an explicit operator decision.</summary>
    OperatorApproval = 2,
}

public enum Verdict
{
    Proceed = 1,
    OperatorApprovalRequired = 2,
    RequiresReview = 3,
    Completed = 4,
}

public sealed record TrackDecision(int TrackId, TrackCondition Condition, ImmutableArray<string> Reasons);

/// <summary>
/// One guarded next action, or none. <see cref="NewTrackFileId"/> / <see cref="NewLyricFileId"/>
/// carry observed fresh IDs for <see cref="RecoveryAction.RecordStepDone"/>.
/// </summary>
public sealed record NextAction(int? TrackId, RecoveryAction Action, ActionMode Mode, string Reason, int? NewTrackFileId = null, int? NewLyricFileId = null);

public sealed record Reconciliation(Verdict Verdict, NextAction? Next, ImmutableArray<string> Blockers, ImmutableArray<TrackDecision> Tracks);

/// <summary>
/// Pure reconciliation of a saved manifest against fresh observations. It never assumes a
/// step succeeded because something is absent; success requires positive evidence (exact
/// bytes at the expected path plus the expected host rows). Anything it cannot explain is a
/// blocker: the verdict is RequiresReview and the only permitted action is recording that.
/// </summary>
public static class Reconciler
{
    public static Reconciliation Reconcile(OperationManifest manifest, HostSnapshot host, FilesystemSnapshot fs)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fs);
        var b = manifest.Binding;

        if (manifest.Status == OperationStatus.Completed)
            return new Reconciliation(Verdict.Completed, null, ImmutableArray<string>.Empty, ImmutableArray<TrackDecision>.Empty);

        var blockers = new List<string>();
        if (!host.Obtained) blockers.Add("fresh host data could not be obtained");
        if (!fs.Available) blockers.Add("filesystem or mount unavailable");
        if (blockers.Count == 0) GlobalChecks(manifest, host, fs, blockers);

        var decisions = new List<TrackDecision>();
        NextAction? inFlightAction = null;
        if (blockers.Count == 0)
        {
            foreach (var track in b.Tracks)
            {
                var (condition, action, reasons) = Classify(manifest, track, host, fs);
                decisions.Add(new TrackDecision(track.TrackId, condition, reasons.ToImmutableArray()));
                if (condition == TrackCondition.Inconsistent) blockers.AddRange(reasons.Select(r => $"track {track.TrackId}: {r}"));
                if (manifest.ProgressFor(track.TrackId).InFlight is not null) inFlightAction = action;
            }
        }

        if (blockers.Count > 0)
        {
            var record = manifest.Status == OperationStatus.Active
                ? new NextAction(null, RecoveryAction.MarkReview, ActionMode.Automatic, blockers[0])
                : null;
            return new Reconciliation(Verdict.RequiresReview, record, blockers.ToImmutableArray(), decisions.ToImmutableArray());
        }

        var next = inFlightAction ?? NextNormalAction(manifest, decisions);
        if (manifest.Status == OperationStatus.RequiresReview)
        {
            // Sticky: report the proposal for an operator, but nothing runs automatically.
            var proposal = next is { Mode: ActionMode.OperatorApproval } ? next : null;
            return new Reconciliation(Verdict.RequiresReview, proposal, manifest.ReviewReasons, decisions.ToImmutableArray());
        }

        var verdict = next?.Mode == ActionMode.OperatorApproval ? Verdict.OperatorApprovalRequired : Verdict.Proceed;
        return new Reconciliation(verdict, next, ImmutableArray<string>.Empty, decisions.ToImmutableArray());
    }

    private static void GlobalChecks(OperationManifest m, HostSnapshot host, FilesystemSnapshot fs, List<string> blockers)
    {
        var b = m.Binding;
        if (host.AlbumId != b.AlbumId) blockers.Add("host album ID differs from the bound album");

        var monitored = host.Releases.IsDefault ? new List<HostRelease>() : host.Releases.Where(r => r.Monitored).ToList();
        if (monitored.Count != 1) blockers.Add($"expected exactly one monitored release, found {monitored.Count}");
        else if (monitored[0].ReleaseId != b.MonitoredReleaseId || !string.Equals(monitored[0].ReleaseMbid, b.MonitoredReleaseMbid, StringComparison.Ordinal))
            blockers.Add("monitored MusicBrainz release changed since the operation was planned");

        foreach (var track in b.Tracks)
        {
            var hostTrack = host.Tracks.IsDefault ? null : host.Tracks.SingleOrDefault(t => t.TrackId == track.TrackId);
            if (hostTrack is null) blockers.Add($"track {track.TrackId} missing from host");
            else if (!string.Equals(hostTrack.RecordingMbid, track.RecordingMbid, StringComparison.Ordinal)) blockers.Add($"track {track.TrackId} recording identity changed");

            foreach (var path in BoundPaths(track))
            {
                if (fs.Files is null || !fs.Files.TryGetValue(path, out var observation)) blockers.Add($"no fresh observation for {path}");
                else if (observation.Escapes) blockers.Add($"path escapes its root or traverses a link: {path}");
            }

            // Any extra lyric (or other sidecar row) linked to the original file would be recycled
            // by the stock delete handler; only the bound lyric may be linked, and only before detach.
            var phase = m.ProgressFor(track.TrackId).Phase;
            if (phase < TrackPhase.Upgraded && !host.Lyrics.IsDefault)
            {
                var linked = host.Lyrics.Where(l => l.TrackFileId == track.OriginalTrackFileId).ToList();
                if (linked.Any(l => track.Lyric is null || l.LyricFileId != track.Lyric.LyricFileId))
                    blockers.Add($"track {track.TrackId}: an unbound lyric row is linked to the original track file");
            }

            var original = Row(host, track.OriginalTrackFileId);
            if (original is not null && !PathCanonicalizer.PathsEqual(original.Path, track.OriginalPath, b.PathStyle))
                blockers.Add($"track {track.TrackId}: original track-file ID now refers to a different path (stale or reused ID)");
        }

        if (!host.TrackFiles.IsDefault && host.TrackFiles.GroupBy(f => f.TrackFileId).Any(g => g.Count() > 1)) blockers.Add("duplicate track-file IDs in host snapshot");
        if (!host.Lyrics.IsDefault && host.Lyrics.GroupBy(l => l.LyricFileId).Any(g => g.Count() > 1)) blockers.Add("duplicate lyric IDs in host snapshot");
    }

    private static (TrackCondition, NextAction?, List<string>) Classify(OperationManifest m, TrackBinding t, HostSnapshot host, FilesystemSnapshot fs)
    {
        var p = m.ProgressFor(t.TrackId);
        var reasons = new List<string>();
        var style = m.Binding.PathStyle;
        var hostTrack = host.Tracks.Single(x => x.TrackId == t.TrackId);
        var o = fs.Files[t.OriginalPath];
        var d = fs.Files[t.DestinationPath];
        var i = fs.Files[t.IncomingPath];
        var sameDestination = PathCanonicalizer.PathsEqual(t.OriginalPath, t.DestinationPath, style);
        var originalRow = Row(host, t.OriginalTrackFileId);
        var linkedToOriginal = hostTrack.TrackFileId == t.OriginalTrackFileId;
        var backupOk = fs.Files[t.BackupAudioPath].Is(t.Original) && (t.Lyric is null || fs.Files[t.Lyric.BackupPath].Is(t.Lyric.Content));
        var lyricBytesOk = t.Lyric is null || fs.Files[t.Lyric.Path].Is(t.Lyric.Content);
        var originalLyricRow = t.Lyric is null || host.Lyrics.IsDefault ? null : host.Lyrics.SingleOrDefault(l => l.LyricFileId == t.Lyric.LyricFileId);
        var destinationFree = sameDestination || !d.Exists;
        var incomingStaged = i.Is(t.Incoming);
        var originalIntact = linkedToOriginal && originalRow is not null && o.Is(t.Original) && destinationFree && incomingStaged;

        (TrackCondition, NextAction?, List<string>) Bad(string why) { reasons.Add(why); return (TrackCondition.Inconsistent, null, reasons); }
        NextAction Auto(RecoveryAction a, string why, int? tf = null, int? lf = null) => new(t.TrackId, a, ActionMode.Automatic, why, tf, lf);
        if (!lyricBytesOk) return Bad("physical lyric bytes differ from the bound lyric");

        // Lyric row expectations before the native step.
        if (t.Lyric is not null && p.Phase <= TrackPhase.BackupVerified && p.InFlight != JournalStep.DetachLyric)
        {
            if (originalLyricRow is null || originalLyricRow.TrackFileId != t.OriginalTrackFileId || originalLyricRow.RelativePath != t.Lyric.RelativePath)
                return Bad("registered lyric row missing or relinked before detach");
        }

        switch (p.Phase)
        {
            case TrackPhase.Planned:
                if (!originalIntact) return Bad("original, incoming or destination changed before any destructive step");
                if (p.InFlight == JournalStep.CopyBackup)
                    return backupOk
                        ? (TrackCondition.BackupVerified, Auto(RecoveryAction.RecordStepDone, "backup observed verified"), reasons)
                        : (TrackCondition.BackupPending, Auto(RecoveryAction.CopyBackup, "resume interrupted backup into operation-owned path"), reasons);
                return (TrackCondition.NotStarted, null, reasons);

            case TrackPhase.BackupVerified:
                if (!backupOk) return Bad("verified backup is missing or changed");
                if (p.InFlight == JournalStep.NativeUpgrade) return ClassifyNative(m, t, host, fs, reasons, backupOk);
                if (!originalIntact) return Bad("original, incoming or destination changed after backup");
                if (p.InFlight == JournalStep.DetachLyric)
                {
                    if (originalLyricRow is not null)
                        return originalLyricRow.TrackFileId == t.OriginalTrackFileId
                            ? (TrackCondition.BackupVerified, Auto(RecoveryAction.DetachLyric, "resume interrupted lyric detach"), reasons)
                            : Bad("lyric row relinked elsewhere during detach");
                    return (TrackCondition.LyricDetached, Auto(RecoveryAction.RecordStepDone, "lyric row observed removed and bytes intact"), reasons);
                }

                return (TrackCondition.BackupVerified, null, reasons);

            case TrackPhase.LyricDetached:
                if (!backupOk) return Bad("verified backup is missing or changed");
                if (originalLyricRow is not null) return Bad("original lyric row reappeared after detach");
                if (p.InFlight == JournalStep.NativeUpgrade) return ClassifyNative(m, t, host, fs, reasons, backupOk);
                if (!originalIntact) return Bad("original, incoming or destination changed after lyric detach");
                return (TrackCondition.LyricDetached, null, reasons);

            case TrackPhase.Upgraded:
            case TrackPhase.LyricRelinked:
                var newRow = Row(host, p.NewTrackFileId!.Value);
                if (newRow is null || hostTrack.TrackFileId != newRow.TrackFileId || !PathCanonicalizer.PathsEqual(newRow.Path, t.DestinationPath, style))
                    return Bad("recorded fresh track-file row is missing or not linked");
                if (!d.Is(t.Incoming)) return Bad("upgraded destination bytes differ from the bound incoming content");
                if (originalRow is not null) return Bad("original track-file row reappeared after upgrade");
                if (t.Lyric is null) return (TrackCondition.Upgraded, null, reasons);
                var relinked = host.Lyrics.Where(l => l.TrackFileId == newRow.TrackFileId && l.RelativePath == t.Lyric.RelativePath).ToList();
                if (relinked.Count > 1) return Bad("duplicate relinked lyric rows");
                if (p.Phase == TrackPhase.LyricRelinked)
                    return relinked.Count == 1 && relinked[0].LyricFileId == p.NewLyricFileId
                        ? (TrackCondition.LyricRelinked, null, reasons)
                        : Bad("recorded relinked lyric row missing");
                if (p.InFlight == JournalStep.RelinkLyric)
                    return relinked.Count == 1
                        ? (TrackCondition.LyricRelinked, Auto(RecoveryAction.RecordStepDone, "relinked lyric row observed", lf: relinked[0].LyricFileId), reasons)
                        : (TrackCondition.Upgraded, Auto(RecoveryAction.RelinkLyric, "resume interrupted lyric relink"), reasons);
                return relinked.Count == 0 ? (TrackCondition.Upgraded, null, reasons) : Bad("lyric relinked without a declared step");

            default:
                return Bad("unknown phase");
        }

        // Local function: the only place where interrupted native-upgrade states are interpreted.
        static (TrackCondition, NextAction?, List<string>) ClassifyNative(OperationManifest m, TrackBinding t, HostSnapshot host, FilesystemSnapshot fs, List<string> reasons, bool backupOk)
        {
            var style = m.Binding.PathStyle;
            var hostTrack = host.Tracks.Single(x => x.TrackId == t.TrackId);
            var o = fs.Files[t.OriginalPath];
            var d = fs.Files[t.DestinationPath];
            var i = fs.Files[t.IncomingPath];
            var same = PathCanonicalizer.PathsEqual(t.OriginalPath, t.DestinationPath, style);
            var originalRow = Row(host, t.OriginalTrackFileId);
            var destinationRows = host.TrackFiles.Where(f => PathCanonicalizer.PathsEqual(f.Path, t.DestinationPath, style) && f.TrackFileId != t.OriginalTrackFileId).ToList();
            var originalPathRows = host.TrackFiles.Where(f => PathCanonicalizer.PathsEqual(f.Path, t.OriginalPath, style) && f.TrackFileId != t.OriginalTrackFileId).ToList();
            var linkOrig = hostTrack.TrackFileId == t.OriginalTrackFileId;
            var linkMissingOrOrphan = hostTrack.TrackFileId == 0 || (linkOrig && originalRow is null);
            var lyricRows = t.Lyric is null ? new List<HostLyric>() : host.Lyrics.Where(l => l.RelativePath == t.Lyric.RelativePath).ToList();

            NextAction Auto(RecoveryAction a, string why, int? tf = null) => new(t.TrackId, a, ActionMode.Automatic, why, tf);
            NextAction Operator(RecoveryAction a, string why) => new(t.TrackId, a, ActionMode.OperatorApproval, why);
            (TrackCondition, NextAction?, List<string>) Bad(string why) { reasons.Add(why); return (TrackCondition.Inconsistent, null, reasons); }

            if (!backupOk) return Bad("verified backup missing during an interrupted native upgrade");
            var incomingAtDestination = d.Is(t.Incoming);
            var originalAtOriginal = o.Is(t.Original);

            // E. Positive evidence of completion: incoming bytes at destination, fresh linked row, old row gone.
            if (incomingAtDestination && !i.Exists && originalRow is null && destinationRows.Count == 1 && hostTrack.TrackFileId == destinationRows[0].TrackFileId)
                return (TrackCondition.Upgraded, Auto(RecoveryAction.RecordStepDone, "native upgrade observed complete", destinationRows[0].TrackFileId), reasons);

            // A. Stock recycles the original first; untouched original + row + link + staged incoming means it never began.
            if (linkOrig && originalRow is not null && originalAtOriginal && i.Is(t.Incoming) && (same || !d.Exists) && destinationRows.Count == 0)
                return (TrackCondition.NativeNotStarted, Auto(RecoveryAction.AbandonStep, "native upgrade observably not started"), reasons);

            // Rolled back by an approved operator action: original bytes back, linked to a fresh row.
            if (originalAtOriginal && originalRow is null && i.Is(t.Incoming) && originalPathRows.Count == 1 && hostTrack.TrackFileId == originalPathRows[0].TrackFileId && (same || !d.Exists))
            {
                if (t.Lyric is null) return (TrackCondition.RolledBack, Auto(RecoveryAction.MarkReview, "track rolled back to its original; review before any retry"), reasons);
                var reattached = lyricRows.Where(l => l.TrackFileId == originalPathRows[0].TrackFileId).ToList();
                if (reattached.Count == 1 && lyricRows.Count == 1)
                    return (TrackCondition.RolledBack, Auto(RecoveryAction.MarkReview, "track and lyric rolled back to originals; review before any retry"), reasons);
                if (lyricRows.Count == 0)
                    return (TrackCondition.RolledBackLyricDetached, Operator(RecoveryAction.ReattachOriginalLyric, "insert a fresh lyric row for the restored original"), reasons);
                return Bad("unexpected lyric rows after rollback");
            }

            if (lyricRows.Count > 0) return Bad("lyric row present during an interrupted native upgrade");

            // B. Original moved away by the recycler but its row and link survive: restore bytes (filesystem only).
            if (!o.Exists && originalRow is not null && linkOrig && i.Is(t.Incoming) && (same || !d.Exists))
                return (TrackCondition.OriginalMovedRowIntact, Auto(RecoveryAction.RestoreOriginalBytes, "restore original bytes from verified backup (no overwrite)"), reasons);

            // C. Stock failure mode: original moved and its row deleted, incoming never placed.
            if (originalRow is null && linkMissingOrOrphan && i.Is(t.Incoming) && destinationRows.Count == 0 && originalPathRows.Count == 0)
            {
                if (!o.Exists && (same || !d.Exists))
                    return (TrackCondition.OrphanedOriginalBytesMissing, Auto(RecoveryAction.RestoreOriginalBytes, "restore original bytes from verified backup (no overwrite)"), reasons);
                if (originalAtOriginal && (same || !d.Exists))
                    return (TrackCondition.OrphanedOriginalBytesRestored, Operator(RecoveryAction.InsertFreshTrackFileForOriginal, "host row deleted: insert a FRESH track-file row for the restored original and relink the track"), reasons);
            }

            // D. Incoming placed but never linked (crash before the host inserted the new row).
            if (incomingAtDestination && !i.Exists && originalRow is null && linkMissingOrOrphan && destinationRows.Count == 0)
                return (TrackCondition.IncomingPlacedUnlinked, Operator(RecoveryAction.InsertFreshTrackFileForIncoming, "incoming placed without a row: insert a FRESH track-file row for it and relink the track"), reasons);

            return Bad("interrupted native upgrade left an unexplained state (conflicting destination, partial transfer or changed rows)");
        }
    }

    private static NextAction? NextNormalAction(OperationManifest m, List<TrackDecision> decisions)
    {
        foreach (var track in m.Binding.Tracks)
        {
            var step = JournalEngine.NextStep(m, track.TrackId);
            if (step is null) continue;
            var action = step switch
            {
                JournalStep.CopyBackup => RecoveryAction.CopyBackup,
                JournalStep.DetachLyric => RecoveryAction.DetachLyric,
                JournalStep.NativeUpgrade => RecoveryAction.InvokeNativeUpgrade,
                JournalStep.RelinkLyric => RecoveryAction.RelinkLyric,
                _ => RecoveryAction.None,
            };
            return new NextAction(track.TrackId, action, ActionMode.Automatic, $"next planned step {step}");
        }

        return m.Binding.Tracks.All(t => JournalEngine.IsFinal(m, t.TrackId))
            ? new NextAction(null, RecoveryAction.CompleteOperation, ActionMode.Automatic, "every track reached its final phase")
            : null;
    }

    private static HostTrackFile? Row(HostSnapshot host, int id) =>
        host.TrackFiles.IsDefault ? null : host.TrackFiles.FirstOrDefault(f => f.TrackFileId == id);

    public static IEnumerable<string> BoundPaths(TrackBinding t)
    {
        yield return t.OriginalPath;
        if (!string.Equals(t.DestinationPath, t.OriginalPath, StringComparison.Ordinal)) yield return t.DestinationPath;
        yield return t.IncomingPath;
        yield return t.BackupAudioPath;
        if (t.Lyric is null) yield break;
        yield return t.Lyric.Path;
        yield return t.Lyric.BackupPath;
    }
}
