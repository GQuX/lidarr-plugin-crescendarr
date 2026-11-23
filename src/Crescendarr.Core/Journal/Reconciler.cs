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

/// <summary>
/// A fresh host/database view. <see cref="Obtained"/> false means it could not be read.
/// <see cref="ArtistPath"/> is the host's current artist directory (Lidarr <c>Artist.Path</c>), the root that
/// registered lyric relative paths are resolved against.
/// </summary>
public sealed record HostSnapshot(
    bool Obtained,
    int AlbumId,
    ImmutableArray<HostRelease> Releases,
    ImmutableArray<HostTrack> Tracks,
    ImmutableArray<HostTrackFile> TrackFiles,
    ImmutableArray<HostLyric> Lyrics,
    string? ArtistPath);

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

    // M18: additions and album-wide lyrics.
    AddNotStarted = 15,
    AddPlacedUnlinked = 16,
    Added = 17,
    LyricNeedsRestore = 18,
    LyricNeedsBind = 19,
    LyricSettled = 20,
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

    /// <summary>M18: the only action that permits the host's native import of a missing track.</summary>
    InvokeNativeAdd = 13,

    /// <summary>M18: copy a missing album lyric back from its verified backup; never overwrites.</summary>
    RestoreAlbumLyric = 14,

    /// <summary>M18: register an album lyric (fresh row) to its track's CURRENT track file.</summary>
    BindAlbumLyric = 15,

    /// <summary>M19: record (journal only) that a host rescan replaced an unstarted replacement's original row.</summary>
    RecordOriginalRebase = 16,

    /// <summary>M19: link the track to the row the host inserted before it crashed (operator approval only).</summary>
    LinkPlacedRow = 17,
}

/// <summary>
/// M19 protective salvage: lyric-only, no-overwrite, hash-exact actions that are safe even while the audio operation
/// is blocked for review. <see cref="Applicable"/> is false when the album, edition, artist root or paths are ambiguous.
/// </summary>
public sealed record SalvagePlan(bool Applicable, ImmutableArray<string> Reasons, ImmutableArray<NextAction> Actions, ImmutableArray<string> Skipped);

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
public sealed record NextAction(int? TrackId, RecoveryAction Action, ActionMode Mode, string Reason, int? NewTrackFileId = null, int? NewLyricFileId = null, int? AlbumLyricId = null);

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
        if (manifest.Status == OperationStatus.Active && host.Obtained && fs.Available && !host.Tracks.IsDefault && !host.TrackFiles.IsDefault)
        {
            // M19: a host rescan may have replaced the rows of replacements that never started. Re-identify each by
            // path + exact bytes + its track + no other owner, then check the WHOLE operation as if that evidence were
            // recorded. Only if everything is consistent is the evidence recorded (journal only, binding unchanged).
            var rebases = RebaseCandidates(manifest, host, fs);
            if (rebases.Count > 0)
            {
                var view = manifest with
                {
                    Progress = manifest.Progress.Select(p => rebases.TryGetValue(p.TrackId, out var id) ? p with { ObservedOriginalTrackFileId = id } : p).ToImmutableArray(),
                };
                var checkedView = ReconcileCore(view, host, fs);
                if (checkedView.Verdict == Verdict.RequiresReview) return checkedView;
                var first = rebases.First();
                return new Reconciliation(Verdict.Proceed,
                    new NextAction(first.Key, RecoveryAction.RecordOriginalRebase, ActionMode.Automatic,
                        "a host rescan replaced the original row of an unstarted replacement (same track, path and bytes; no other owner)", first.Value),
                    ImmutableArray<string>.Empty, checkedView.Tracks);
            }
        }

        return ReconcileCore(manifest, host, fs);
    }

    private static Reconciliation ReconcileCore(OperationManifest manifest, HostSnapshot host, FilesystemSnapshot fs)
    {
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

        if (blockers.Count == 0)
        {
            foreach (var addition in b.Additions)
            {
                var (condition, action, reasons) = ClassifyAddition(manifest, addition, host, fs);
                decisions.Add(new TrackDecision(addition.TrackId, condition, reasons.ToImmutableArray()));
                if (condition == TrackCondition.Inconsistent) blockers.AddRange(reasons.Select(r => $"addition {addition.TrackId}: {r}"));
                if (manifest.AdditionFor(addition.TrackId).InFlight) inFlightAction = action;
            }
        }

        // Album-wide lyric settlement runs only once every bound album audio file is linked (PR #42: lyrics
        // re-registered while an audio file is unregistered can be recycled again by the next host scan).
        var lyricActions = new List<NextAction>();
        if (blockers.Count == 0 && JournalEngine.AllAudioFinal(manifest))
        {
            foreach (var lyric in b.AlbumLyrics)
            {
                var (condition, action, reasons) = ClassifyAlbumLyric(manifest, lyric, host, fs);
                decisions.Add(new TrackDecision(lyric.TrackId, condition, reasons.ToImmutableArray()));
                if (condition == TrackCondition.Inconsistent) blockers.AddRange(reasons.Select(r => $"album lyric {lyric.RelativePath}: {r}"));
                else if (action is not null) lyricActions.Add(action);
            }

            lyricActions = lyricActions.OrderBy(a => a.Action == RecoveryAction.RestoreAlbumLyric ? 0 : 1).ToList();
        }

        if (blockers.Count > 0)
        {
            var record = manifest.Status == OperationStatus.Active
                ? new NextAction(null, RecoveryAction.MarkReview, ActionMode.Automatic, blockers[0])
                : null;
            return new Reconciliation(Verdict.RequiresReview, record, blockers.ToImmutableArray(), decisions.ToImmutableArray());
        }

        // M19: restoring an UNREGISTERED lyric file is non-destructive (stock recycles only registered extras), so any
        // lyric a scan recycled is put back from its verified backup as early as possible. Registration still waits.
        NextAction? salvageRestore = null;
        if (manifest.Status == OperationStatus.Active && !manifest.AnyInFlight)
            salvageRestore = Salvage(manifest, host, fs).Actions.FirstOrDefault(a => a.Action == RecoveryAction.RestoreAlbumLyric);
        var next = inFlightAction ?? salvageRestore ?? NextNormalAction(manifest, host, lyricActions);
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
        if (host.ArtistPath is null || !string.Equals(host.ArtistPath, b.ArtistRoot, StringComparison.Ordinal))
            blockers.Add("host artist root is unknown or differs from the bound artist root (lyric identity cannot be established)");

        // Identity comparison rule: any two host rows whose paths are equal ignoring case are a
        // collision on EVERY host, because a NAS may fold case even under a case-sensitive path
        // grammar. Hardlinks and bind mounts cannot be detected from paths and remain an adapter duty.
        if (!host.Tracks.IsDefault && host.Tracks.GroupBy(t => t.TrackId).Any(g => g.Count() > 1)) blockers.Add("duplicate track IDs in host snapshot");
        if (!host.TrackFiles.IsDefault && host.TrackFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            blockers.Add("two track-file rows share a path (compared ignoring case)");
        // Ignore-case comparison does not fold Unicode normalization (NFC vs NFD spellings of the same
        // name, as some SMB servers return). Bound paths are already NFC-only; any host path that is not
        // clean NFC cannot be compared reliably, so it blocks.
        if ((!host.TrackFiles.IsDefault && host.TrackFiles.Any(f => f.Path is null || InventoryText.UnsafeCharacter(f.Path) is not null)) ||
            (!host.Lyrics.IsDefault && host.Lyrics.Any(l => l.RelativePath is null || InventoryText.UnsafeCharacter(l.RelativePath) is not null)))
            blockers.Add("a host row path is not clean NFC text and cannot be compared for identity");
        if (!host.Lyrics.IsDefault && host.Lyrics.GroupBy(l => l.RelativePath, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            blockers.Add("two lyric rows share a relative path (compared ignoring case)");

        var monitored = host.Releases.IsDefault ? new List<HostRelease>() : host.Releases.Where(r => r.Monitored).ToList();
        if (monitored.Count != 1) blockers.Add($"expected exactly one monitored release, found {monitored.Count}");
        else if (monitored[0].ReleaseId != b.MonitoredReleaseId || !string.Equals(monitored[0].ReleaseMbid, b.MonitoredReleaseMbid, StringComparison.Ordinal))
            blockers.Add("monitored MusicBrainz release changed since the operation was planned");

        // Contradictory snapshots must produce review, never an exception: stop before any per-track
        // lookup that assumes uniqueness.
        if (blockers.Count > 0 && (host.Tracks.IsDefault || host.Tracks.GroupBy(t => t.TrackId).Any(g => g.Count() > 1))) return;

        foreach (var path in AllBoundPaths(b))
        {
            if (fs.Files is null || !fs.Files.TryGetValue(path, out var observation)) blockers.Add($"no fresh observation for {path}");
            else if (observation.Escapes) blockers.Add($"path escapes its root or traverses a link: {path}");
        }

        if (blockers.Count > 0) return;

        foreach (var track in b.Tracks)
        {
            var hostTrack = host.Tracks.IsDefault ? null : host.Tracks.SingleOrDefault(t => t.TrackId == track.TrackId);
            if (hostTrack is null) blockers.Add($"track {track.TrackId} missing from host");
            else if (!string.Equals(hostTrack.RecordingMbid, track.RecordingMbid, StringComparison.Ordinal)) blockers.Add($"track {track.TrackId} recording identity changed");
            else if (hostTrack.TrackFileId != 0 && hostTrack.TrackFileId != Orig(m, track) && Row(host, hostTrack.TrackFileId) is null)
                blockers.Add($"track {track.TrackId}: dangling track-file link {hostTrack.TrackFileId} (stale nonzero ID)");

            // Any extra lyric (or other sidecar row) linked to the original file would be recycled
            // by the stock delete handler; only the bound lyric may be linked, and only before detach.
            var phase = m.ProgressFor(track.TrackId).Phase;
            if (phase < TrackPhase.Upgraded && !host.Lyrics.IsDefault)
            {
                var linked = host.Lyrics.Where(l => l.TrackFileId == Orig(m, track)).ToList();
                if (linked.Any(l => track.Lyric is null || l.LyricFileId != track.Lyric.LyricFileId))
                    blockers.Add($"track {track.TrackId}: an unbound lyric row is linked to the original track file");
            }

            var progress = m.ProgressFor(track.TrackId);
            var beforeNative = progress.Phase < TrackPhase.Upgraded && progress.InFlight != JournalStep.NativeUpgrade;
            if (beforeNative && !host.TrackFiles.IsDefault)
            {
                // Before the native step, the ONLY row allowed at the original or destination path is the bound original.
                if (host.TrackFiles.Any(f => f.TrackFileId != Orig(m, track) &&
                        (string.Equals(f.Path, track.OriginalPath, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Path, track.DestinationPath, StringComparison.OrdinalIgnoreCase))))
                    blockers.Add($"track {track.TrackId}: another track-file row refers to the original or destination path");
            }

            // The stock upgrader deletes the linked file for every track it touches; a bound original
            // shared with any other track would orphan that track.
            if (progress.Phase < TrackPhase.Upgraded && !host.Tracks.IsDefault &&
                host.Tracks.Any(x => x.TrackId != track.TrackId && x.TrackFileId == Orig(m, track)))
                blockers.Add($"track {track.TrackId}: the original track file is linked to another track");

            // Per-track isolation also covers every row the operation creates or relies on: the recorded
            // fresh ID, and while the native step is in flight any non-original row at the track's
            // destination or original path (the candidate fresh row, or an approved rollback row).
            // Sharing between tracks the operation never touches stays legitimate.
            if (!host.Tracks.IsDefault && !host.TrackFiles.IsDefault)
            {
                var operationRows = new HashSet<int>();
                if (progress.NewTrackFileId is { } fresh) operationRows.Add(fresh);
                // M18: after the upgrade the track's CURRENT row is the operation's row even when a host scan replaced it.
                if (progress.Phase >= TrackPhase.Upgraded && hostTrack is { TrackFileId: > 0 } && hostTrack.TrackFileId != Orig(m, track))
                    operationRows.Add(hostTrack.TrackFileId);
                if (progress.InFlight == JournalStep.NativeUpgrade)
                {
                    foreach (var row in host.TrackFiles.Where(f => f.TrackFileId != Orig(m, track) &&
                                 (string.Equals(f.Path, track.DestinationPath, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(f.Path, track.OriginalPath, StringComparison.OrdinalIgnoreCase))))
                        operationRows.Add(row.TrackFileId);
                }

                if (operationRows.Count > 0 && host.Tracks.Any(x => x.TrackId != track.TrackId && operationRows.Contains(x.TrackFileId)))
                    blockers.Add($"track {track.TrackId}: a track-file row created by this operation is linked to another track");
            }

            var original = Row(host, Orig(m, track));
            if (original is not null && !PathCanonicalizer.PathsEqual(original.Path, track.OriginalPath, b.PathStyle))
                blockers.Add($"track {track.TrackId}: original track-file ID now refers to a different path (stale or reused ID)");
        }

        GlobalOperationChecks(m, host, fs, blockers);
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
        var originalRow = Row(host, Orig(m, t));
        var linkedToOriginal = hostTrack.TrackFileId == Orig(m, t);
        var backupOk = fs.Files[t.BackupAudioPath].Is(t.Original) && (t.Lyric is null || fs.Files[t.Lyric.BackupPath].Is(t.Lyric.Content));
        var lyricObservation = t.Lyric is null ? null : fs.Files[t.Lyric.Path];
        var albumOwnsLyric = t.Lyric is not null && m.Binding.AlbumLyrics.Any(l => l.LyricFileId == t.Lyric.LyricFileId);
        // Different bytes always block. Missing bytes are explained only after the track's lyric was relinked and only
        // when the album-wide binding (with its own verified backup) owns the lyric's final state.
        var albumLyric = t.Lyric is null ? null : m.Binding.AlbumLyrics.FirstOrDefault(l => l.LyricFileId == t.Lyric.LyricFileId);
        // M19: a missing lyric that is unregistered and has a verified album backup is awaiting the salvage restore.
        var awaitingRestore = lyricObservation is { Exists: false } && albumLyric is not null && fs.Files[albumLyric.BackupPath].Is(albumLyric.Content) &&
            !host.Lyrics.Any(l => string.Equals(l.RelativePath, t.Lyric!.RelativePath, StringComparison.OrdinalIgnoreCase));
        var lyricBytesOk = lyricObservation is null || lyricObservation.Is(t.Lyric!.Content) ||
            (!lyricObservation.Exists && ((p.Phase == TrackPhase.LyricRelinked && albumOwnsLyric) || awaitingRestore));
        var originalLyricRow = t.Lyric is null || host.Lyrics.IsDefault ? null : host.Lyrics.SingleOrDefault(l => l.LyricFileId == t.Lyric.LyricFileId);
        var destinationFree = sameDestination || !d.Exists;
        var incomingStaged = i.Is(t.Incoming);
        var originalIntact = linkedToOriginal && originalRow is not null && o.Is(t.Original) && destinationFree && incomingStaged;

        (TrackCondition, NextAction?, List<string>) Bad(string why) { reasons.Add(why); return (TrackCondition.Inconsistent, null, reasons); }
        NextAction Auto(RecoveryAction a, string why, int? tf = null, int? lf = null) => new(t.TrackId, a, ActionMode.Automatic, why, tf, lf);
        if (!lyricBytesOk) return Bad(lyricObservation!.Exists ? "physical lyric bytes differ from the bound lyric" : "bound lyric file is missing");

        // Lyric row expectations before the native step.
        if (t.Lyric is not null && p.Phase <= TrackPhase.BackupVerified && p.InFlight != JournalStep.DetachLyric)
        {
            // M19: after a recorded rebase the host rescan already removed (and recycled) the lyric row; the album
            // backup owns the bytes and the detach step only records that observation.
            var removedByRescan = p.ObservedOriginalTrackFileId is not null && originalLyricRow is null && albumOwnsLyric;
            if (!removedByRescan && (originalLyricRow is null || originalLyricRow.TrackFileId != Orig(m, t) || originalLyricRow.RelativePath != t.Lyric.RelativePath))
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
                        return originalLyricRow.TrackFileId == Orig(m, t)
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
                // M18: a host rescan may replace the fresh row with another fresh row for the SAME file (PR #42:
                // every TrackFile ID changes across the cascade). Identity is the track's current link to a row at
                // the destination holding the incoming bytes; the stored ID is only the first observation.
                var newRow = Row(host, hostTrack.TrackFileId);
                if (newRow is null || hostTrack.TrackFileId == Orig(m, t) || !PathCanonicalizer.PathsEqual(newRow.Path, t.DestinationPath, style))
                    return Bad("recorded fresh track-file row is missing or not linked");
                if (!d.Is(t.Incoming)) return Bad("upgraded destination bytes differ from the bound incoming content");
                if (originalRow is not null) return Bad("original track-file row reappeared after upgrade");
                if (t.Lyric is null) return (TrackCondition.Upgraded, null, reasons);
                var relinked = host.Lyrics.Where(l => l.TrackFileId == newRow.TrackFileId && l.RelativePath == t.Lyric.RelativePath).ToList();
                var atLyricPath = host.Lyrics.Where(l => string.Equals(l.RelativePath, t.Lyric.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
                if (relinked.Count > 1) return Bad("duplicate relinked lyric rows");
                if (p.Phase == TrackPhase.LyricRelinked)
                {
                    // Album-wide settlement owns the final lyric state (bytes, backup, single registration to the current row).
                    if (albumOwnsLyric) return (TrackCondition.LyricRelinked, null, reasons);
                    return relinked.Count == 1 && relinked[0].LyricFileId == p.NewLyricFileId
                        ? (TrackCondition.LyricRelinked, null, reasons)
                        : Bad("recorded relinked lyric row missing");
                }

                if (p.InFlight == JournalStep.RelinkLyric)
                {
                    if (relinked.Count == 1 && atLyricPath.Count == 1)
                        return (TrackCondition.LyricRelinked, Auto(RecoveryAction.RecordStepDone, "relinked lyric row observed", lf: relinked[0].LyricFileId), reasons);
                    return atLyricPath.Count == 0
                        ? (TrackCondition.Upgraded, Auto(RecoveryAction.RelinkLyric, "resume interrupted lyric relink", tf: newRow.TrackFileId), reasons)
                        : Bad("lyric row linked elsewhere during relink");
                }

                return atLyricPath.Count == 0 ? (TrackCondition.Upgraded, null, reasons) : Bad("lyric relinked without a declared step");

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
            var originalRow = Row(host, Orig(m, t));
            var destinationRows = host.TrackFiles.Where(f => PathCanonicalizer.PathsEqual(f.Path, t.DestinationPath, style) && f.TrackFileId != Orig(m, t)).ToList();
            var originalPathRows = host.TrackFiles.Where(f => PathCanonicalizer.PathsEqual(f.Path, t.OriginalPath, style) && f.TrackFileId != Orig(m, t)).ToList();
            var linkOrig = hostTrack.TrackFileId == Orig(m, t);
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
            if (linkOrig && originalRow is not null && originalAtOriginal && i.Is(t.Incoming) && (same || !d.Exists) && destinationRows.Count == 0 && originalPathRows.Count == 0)
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

            // D2 (M19). The host inserted the fresh row but crashed before linking the track (AddMany, then SetFileIds).
            if (incomingAtDestination && !i.Exists && originalRow is null && hostTrack.TrackFileId == 0 && destinationRows.Count == 1 &&
                !host.Tracks.Any(x => x.TrackFileId == destinationRows[0].TrackFileId))
                return (TrackCondition.IncomingPlacedUnlinked, new NextAction(t.TrackId, RecoveryAction.LinkPlacedRow, ActionMode.OperatorApproval,
                    "row inserted but the track was never linked (crash between the stock row insert and SetFileIds): link the track to that row", destinationRows[0].TrackFileId), reasons);

            // D. Incoming placed but never linked (crash before the host inserted the new row).
            if (incomingAtDestination && !i.Exists && originalRow is null && linkMissingOrOrphan && destinationRows.Count == 0)
                return (TrackCondition.IncomingPlacedUnlinked, Operator(RecoveryAction.InsertFreshTrackFileForIncoming, "incoming placed without a row: insert a FRESH track-file row for it and relink the track"), reasons);

            return Bad("interrupted native upgrade left an unexplained state (conflicting destination, partial transfer or changed rows)");
        }
    }

    private static NextAction? NextNormalAction(OperationManifest m, HostSnapshot host, List<NextAction> lyricActions)
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
            // A relink always targets the track's CURRENT row (validated by Classify in this same reconciliation).
            int? current = step == JournalStep.RelinkLyric ? host.Tracks.Single(t => t.TrackId == track.TrackId).TrackFileId : null;
            return new NextAction(track.TrackId, action, ActionMode.Automatic, $"next planned step {step}", current);
        }

        if (JournalEngine.NextAddition(m) is { } addition)
            return new NextAction(addition, RecoveryAction.InvokeNativeAdd, ActionMode.Automatic, "next planned addition");
        if (!JournalEngine.AllAudioFinal(m)) return null;
        if (lyricActions.Count > 0) return lyricActions[0];
        return new NextAction(null, RecoveryAction.CompleteOperation, ActionMode.Automatic, "every track reached its final phase");
    }

    /// <summary>M18: operation-wide invariants for protected tracks, additions and album lyrics.</summary>
    private static void GlobalOperationChecks(OperationManifest m, HostSnapshot host, FilesystemSnapshot fs, List<string> blockers)
    {
        var b = m.Binding;
        if (host.Tracks.IsDefault || host.TrackFiles.IsDefault || host.Lyrics.IsDefault) return;
        foreach (var k in b.Kept)
        {
            var hostTrack = host.Tracks.SingleOrDefault(t => t.TrackId == k.TrackId);
            if (hostTrack is null) { blockers.Add($"kept track {k.TrackId} missing from host"); continue; }
            if (!string.Equals(hostTrack.RecordingMbid, k.RecordingMbid, StringComparison.Ordinal)) blockers.Add($"kept track {k.TrackId} recording identity changed");
            if (!fs.Files[k.Path].Is(k.Content)) blockers.Add($"kept track {k.TrackId}: protected audio bytes changed or missing");
            var row = Row(host, hostTrack.TrackFileId);
            if (row is null || !PathCanonicalizer.PathsEqual(row.Path, k.Path, b.PathStyle))
                blockers.Add($"kept track {k.TrackId}: no longer linked to a row at its protected audio path");
        }

        foreach (var a in b.Additions)
        {
            var hostTrack = host.Tracks.SingleOrDefault(t => t.TrackId == a.TrackId);
            if (hostTrack is null) { blockers.Add($"addition {a.TrackId} missing from host"); continue; }
            if (!string.Equals(hostTrack.RecordingMbid, a.RecordingMbid, StringComparison.Ordinal)) blockers.Add($"addition {a.TrackId} recording identity changed");
            if (hostTrack.TrackFileId != 0 && Row(host, hostTrack.TrackFileId) is null)
                blockers.Add($"addition {a.TrackId}: dangling track-file link {hostTrack.TrackFileId} (stale nonzero ID)");
            var progress = m.AdditionFor(a.TrackId);
            var rows = new HashSet<int>();
            if (progress.NewTrackFileId is { } recorded) rows.Add(recorded);
            if (progress.Phase == AdditionPhase.Added || progress.InFlight)
            {
                if (hostTrack.TrackFileId > 0) rows.Add(hostTrack.TrackFileId);
                foreach (var row in host.TrackFiles.Where(f => PathCanonicalizer.PathsEqual(f.Path, a.DestinationPath, b.PathStyle))) rows.Add(row.TrackFileId);
            }

            if (rows.Count > 0 && host.Tracks.Any(x => x.TrackId != a.TrackId && rows.Contains(x.TrackFileId)))
                blockers.Add($"addition {a.TrackId}: a track-file row created by this operation is linked to another track");
        }

        foreach (var l in b.AlbumLyrics)
        {
            var observed = fs.Files[l.Path];
            var rows = host.Lyrics.Where(x => string.Equals(x.RelativePath, l.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (observed.Exists && !observed.Is(l.Content))
                blockers.Add($"album lyric {l.RelativePath}: physical lyric bytes differ from the backed-up lyric; unrelated bytes occupy the lyric path, refusing to overwrite");
            else if (!observed.Exists && !fs.Files[l.BackupPath].Is(l.Content))
                blockers.Add($"album lyric {l.RelativePath}: lyric missing and backup unverifiable");
            else if (!fs.Files[l.BackupPath].Is(l.Content))
                blockers.Add($"album lyric {l.RelativePath}: album backup unverifiable (every backup is verified before any further destructive step)");
            if (rows.Any(x => x.TrackFileId == 0 || Row(host, x.TrackFileId) is null))
                blockers.Add($"album lyric {l.RelativePath}: stale lyric row points at a track file that no longer exists");
            else if (!observed.Exists && rows.Count > 0)
                blockers.Add($"album lyric {l.RelativePath}: a lyric row is registered for a missing lyric file");
        }
    }

    /// <summary>M18: a journaled missing-track addition. Success needs positive evidence, exactly like an upgrade.</summary>
    private static (TrackCondition, NextAction?, List<string>) ClassifyAddition(OperationManifest m, AdditionBinding a, HostSnapshot host, FilesystemSnapshot fs)
    {
        var reasons = new List<string>();
        var style = m.Binding.PathStyle;
        var p = m.AdditionFor(a.TrackId);
        var hostTrack = host.Tracks.Single(x => x.TrackId == a.TrackId);
        var d = fs.Files[a.DestinationPath];
        var i = fs.Files[a.IncomingPath];
        var destinationRows = host.TrackFiles.Where(f => PathCanonicalizer.PathsEqual(f.Path, a.DestinationPath, style)).ToList();
        var linked = hostTrack.TrackFileId == 0 ? null : Row(host, hostTrack.TrackFileId);
        (TrackCondition, NextAction?, List<string>) Bad(string why) { reasons.Add(why); return (TrackCondition.Inconsistent, null, reasons); }
        NextAction Auto(RecoveryAction action, string why, int? tf = null) => new(a.TrackId, action, ActionMode.Automatic, why, tf);

        if (p.Phase == AdditionPhase.Added)
        {
            if (linked is null || !PathCanonicalizer.PathsEqual(linked.Path, a.DestinationPath, style) || destinationRows.Count != 1)
                return Bad("added track is not linked to exactly one row at its destination");
            return d.Is(a.Incoming) ? (TrackCondition.Added, null, reasons) : Bad("added destination bytes differ from the bound incoming content");
        }

        if (!p.InFlight)
        {
            if (hostTrack.TrackFileId != 0) return Bad("the missing track was linked by someone else before the add");
            if (d.Exists || destinationRows.Count > 0) return Bad("the add destination is occupied before the add");
            return (TrackCondition.AddNotStarted, null, reasons);
        }

        // Interrupted add: never infer success from absence.
        if (d.Is(a.Incoming) && !i.Exists && destinationRows.Count == 1 && linked?.TrackFileId == destinationRows[0].TrackFileId)
            return (TrackCondition.Added, Auto(RecoveryAction.RecordStepDone, "native add observed complete", destinationRows[0].TrackFileId), reasons);
        if (hostTrack.TrackFileId == 0 && !d.Exists && destinationRows.Count == 0 && i.Is(a.Incoming))
            return (TrackCondition.AddNotStarted, Auto(RecoveryAction.AbandonStep, "native add observably not started"), reasons);
        if (d.Is(a.Incoming) && !i.Exists && destinationRows.Count == 1 && hostTrack.TrackFileId == 0 && !host.Tracks.Any(x => x.TrackFileId == destinationRows[0].TrackFileId))
            return (TrackCondition.AddPlacedUnlinked, new NextAction(a.TrackId, RecoveryAction.LinkPlacedRow, ActionMode.OperatorApproval,
                "row inserted but the track was never linked (crash between the stock row insert and SetFileIds): link the track to that row", destinationRows[0].TrackFileId), reasons);
        if (d.Is(a.Incoming) && !i.Exists && destinationRows.Count == 0 && hostTrack.TrackFileId == 0)
            return (TrackCondition.AddPlacedUnlinked, new NextAction(a.TrackId, RecoveryAction.InsertFreshTrackFileForIncoming, ActionMode.OperatorApproval,
                "incoming placed without a row (crash between the native move and the row insert): register the placed file and link the track"), reasons);
        return Bad("interrupted add left an unexplained state (conflicting destination, partial transfer or changed rows)");
    }

    /// <summary>M18: final state of one album lyric once all album audio is linked (PR #42 policy, M13 vocabulary).</summary>
    private static (TrackCondition, NextAction?, List<string>) ClassifyAlbumLyric(OperationManifest m, AlbumLyricBinding l, HostSnapshot host, FilesystemSnapshot fs)
    {
        var reasons = new List<string>();
        var style = m.Binding.PathStyle;
        (TrackCondition, NextAction?, List<string>) Bad(string why) { reasons.Add(why); return (TrackCondition.Inconsistent, null, reasons); }
        var upgrade = m.Binding.Tracks.FirstOrDefault(t => t.TrackId == l.TrackId);
        var kept = m.Binding.Kept.FirstOrDefault(k => k.TrackId == l.TrackId);
        var audioPath = upgrade?.DestinationPath ?? kept?.Path;
        var audio = upgrade?.Incoming ?? kept?.Content;
        if (audioPath is null || audio is null) return Bad("lyric belongs to a track the operation does not bind");
        var hostTrack = host.Tracks.SingleOrDefault(x => x.TrackId == l.TrackId);
        var current = hostTrack is null || hostTrack.TrackFileId == 0 ? null : Row(host, hostTrack.TrackFileId);
        if (current is null || !PathCanonicalizer.PathsEqual(current.Path, audioPath, style) || !fs.Files[audioPath].Is(audio))
            return Bad("the lyric's track is not linked to its expected audio");
        var observed = fs.Files[l.Path];
        var rows = host.Lyrics.Where(x => string.Equals(x.RelativePath, l.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (observed.Exists && !observed.Is(l.Content)) return Bad("unrelated bytes occupy the lyric path; refusing to overwrite");
        if (!observed.Exists)
        {
            if (rows.Count > 0) return Bad("a lyric row is registered for a missing lyric file");
            if (!fs.Files[l.BackupPath].Is(l.Content)) return Bad("lyric missing and backup unverifiable");
            return (TrackCondition.LyricNeedsRestore, new NextAction(l.TrackId, RecoveryAction.RestoreAlbumLyric, ActionMode.Automatic,
                "restore the lyric from its verified album backup (no overwrite)", AlbumLyricId: l.LyricFileId), reasons);
        }

        if (rows.Count == 0)
            return (TrackCondition.LyricNeedsBind, new NextAction(l.TrackId, RecoveryAction.BindAlbumLyric, ActionMode.Automatic,
                "register the lyric to its track's current file", current.TrackFileId, AlbumLyricId: l.LyricFileId), reasons);
        if (rows.Count == 1 && rows[0].TrackFileId == current.TrackFileId) return (TrackCondition.LyricSettled, null, reasons);
        return Bad("conflicting or stale lyric row");
    }

    private static int Orig(OperationManifest m, TrackBinding t) => JournalEngine.EffectiveOriginal(m, t);

    /// <summary>
    /// M19: unstarted replacements whose current original row was replaced by a host rescan. Every condition must hold:
    /// no step in flight for the track, the recorded original row is gone, the track is linked to exactly one other row
    /// at the ORIGINAL path, the original bytes are unchanged there, no other track (any album) links that row, no other
    /// row shares the path, and the row ID is not used by any other binding.
    /// </summary>
    public static ImmutableSortedDictionary<int, int> RebaseCandidates(OperationManifest m, HostSnapshot host, FilesystemSnapshot fs)
    {
        var result = ImmutableSortedDictionary.CreateBuilder<int, int>();
        if (host.Tracks.IsDefault || host.TrackFiles.IsDefault) return result.ToImmutable();
        var style = m.Binding.PathStyle;
        foreach (var t in m.Binding.Tracks)
        {
            var p = m.ProgressFor(t.TrackId);
            if (p.InFlight is not null || p.Phase >= TrackPhase.Upgraded || Row(host, Orig(m, t)) is not null) continue;
            var hostTracks = host.Tracks.Where(x => x.TrackId == t.TrackId).ToList();
            if (hostTracks.Count != 1 || !string.Equals(hostTracks[0].RecordingMbid, t.RecordingMbid, StringComparison.Ordinal)) continue;
            var id = hostTracks[0].TrackFileId;
            var row = id > 0 ? Row(host, id) : null;
            if (row is null || !PathCanonicalizer.PathsEqual(row.Path, t.OriginalPath, style)) continue;
            if (!fs.Files.TryGetValue(t.OriginalPath, out var original) || !original.Is(t.Original)) continue;
            if (host.Tracks.Count(x => x.TrackFileId == id) != 1) continue;
            if (host.TrackFiles.Count(f => string.Equals(f.Path, t.OriginalPath, StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(f.Path, t.DestinationPath, StringComparison.OrdinalIgnoreCase)) != 1) continue;
            var used = m.Binding.Tracks.Any(o => o.TrackId != t.TrackId && (o.OriginalTrackFileId == id || m.ProgressFor(o.TrackId).ObservedOriginalTrackFileId == id)) ||
                m.Binding.Kept.Any(k => k.TrackFileId == id) || m.Progress.Any(o => o.NewTrackFileId == id) || m.Additions.Any(a => a.NewTrackFileId == id) ||
                result.Values.Contains(id);
            if (!used) result[t.TrackId] = id;
        }

        return result.ToImmutable();
    }

    /// <summary>Audio a bound track may legitimately be linked to right now: (path, bytes) pairs.</summary>
    private static IEnumerable<(string Path, ContentId Content)> AcceptableAudio(OperationManifest m, int trackId)
    {
        var upgrade = m.Binding.Tracks.FirstOrDefault(t => t.TrackId == trackId);
        if (upgrade is not null)
        {
            yield return (upgrade.DestinationPath, upgrade.Incoming);
            yield return (upgrade.OriginalPath, upgrade.Original);
        }

        foreach (var k in m.Binding.Kept.Where(k => k.TrackId == trackId)) yield return (k.Path, k.Content);
        foreach (var a in m.Binding.Additions.Where(a => a.TrackId == trackId)) yield return (a.DestinationPath, a.Incoming);
    }

    /// <summary>The track's current row when it is verified (acceptable path, exact bytes, no other owner anywhere), else null.</summary>
    private static HostTrackFile? VerifiedCurrent(OperationManifest m, int trackId, HostSnapshot host, FilesystemSnapshot fs)
    {
        var tracks = host.Tracks.Where(x => x.TrackId == trackId).ToList();
        if (tracks.Count != 1 || tracks[0].TrackFileId <= 0) return null;
        var row = Row(host, tracks[0].TrackFileId);
        if (row is null || host.Tracks.Count(x => x.TrackFileId == row.TrackFileId) != 1) return null;
        if (host.TrackFiles.Count(f => string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase)) != 1) return null;
        return AcceptableAudio(m, trackId).Any(a => PathCanonicalizer.PathsEqual(row.Path, a.Path, m.Binding.PathStyle) && fs.Files.TryGetValue(a.Path, out var o) && o.Is(a.Content))
            ? row : null;
    }

    private static ImmutableArray<string> SalvagePreconditions(OperationManifest m, HostSnapshot host, FilesystemSnapshot fs)
    {
        var b = m.Binding;
        var reasons = new List<string>();
        if (!host.Obtained || host.Tracks.IsDefault || host.TrackFiles.IsDefault || host.Lyrics.IsDefault) reasons.Add("fresh host data could not be obtained");
        if (!fs.Available || fs.Files is null) reasons.Add("filesystem or mount unavailable");
        if (reasons.Count > 0) return reasons.ToImmutableArray();
        if (host.AlbumId != b.AlbumId) reasons.Add("album identity differs");
        if (!string.Equals(host.ArtistPath, b.ArtistRoot, StringComparison.Ordinal)) reasons.Add("artist root differs (lyric identity cannot be established)");
        var monitored = host.Releases.IsDefault ? new List<HostRelease>() : host.Releases.Where(r => r.Monitored).ToList();
        if (monitored.Count != 1 || monitored[0].ReleaseId != b.MonitoredReleaseId || !string.Equals(monitored[0].ReleaseMbid, b.MonitoredReleaseMbid, StringComparison.Ordinal))
            reasons.Add("monitored edition is not exactly the bound edition");
        if (host.TrackFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) reasons.Add("two track-file rows share a path");
        if (host.Lyrics.GroupBy(l => l.RelativePath, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) reasons.Add("two lyric rows share a relative path");
        foreach (var path in AllBoundPaths(b))
        {
            if (!fs.Files!.TryGetValue(path, out var o)) reasons.Add($"no fresh observation for {path}");
            else if (o.Escapes) reasons.Add($"path escapes its root or traverses a link: {path}");
        }

        return reasons.ToImmutableArray();
    }

    /// <summary>
    /// M19 protective salvage, valid in ANY operation state: restore missing album lyrics from verified backups (never
    /// overwrite) and register present ones to a VERIFIED current row. Anything ambiguous is skipped with its reason.
    /// No audio is touched and nothing here may complete the operation.
    /// </summary>
    public static SalvagePlan Salvage(OperationManifest manifest, HostSnapshot host, FilesystemSnapshot fs)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var reasons = SalvagePreconditions(manifest, host, fs);
        if (!reasons.IsEmpty) return new SalvagePlan(false, reasons, ImmutableArray<NextAction>.Empty, ImmutableArray<string>.Empty);
        var actions = new List<NextAction>();
        var skipped = new List<string>();
        foreach (var l in manifest.Binding.AlbumLyrics)
        {
            var observed = fs.Files[l.Path];
            var rows = host.Lyrics.Where(x => string.Equals(x.RelativePath, l.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
            var current = VerifiedCurrent(manifest, l.TrackId, host, fs);
            if (observed.Exists && !observed.Is(l.Content)) skipped.Add($"{l.RelativePath}: unrelated bytes occupy the lyric path; not overwritten");
            else if (!observed.Exists)
            {
                if (rows.Count > 0) skipped.Add($"{l.RelativePath}: a lyric row is registered for the missing file");
                else if (!fs.Files[l.BackupPath].Is(l.Content)) skipped.Add($"{l.RelativePath}: lyric missing and backup unverifiable");
                else actions.Add(new NextAction(l.TrackId, RecoveryAction.RestoreAlbumLyric, ActionMode.Automatic, "salvage: restore the lyric from its verified album backup (no overwrite)", AlbumLyricId: l.LyricFileId));
            }
            else if (rows.Count == 0)
            {
                if (current is null) skipped.Add($"{l.RelativePath}: no unambiguous verified current file for track {l.TrackId}; left unregistered");
                else actions.Add(new NextAction(l.TrackId, RecoveryAction.BindAlbumLyric, ActionMode.Automatic, "salvage: register the lyric to its track's verified current file", current.TrackFileId, AlbumLyricId: l.LyricFileId));
            }
            else if (!(rows.Count == 1 && current is not null && rows[0].TrackFileId == current.TrackFileId))
                skipped.Add($"{l.RelativePath}: registered to a row that is not the track's verified current file");
        }

        return new SalvagePlan(true, ImmutableArray<string>.Empty, actions.OrderBy(a => a.Action == RecoveryAction.RestoreAlbumLyric ? 0 : 1).ToImmutableArray(), skipped.ToImmutableArray());
    }

    /// <summary>
    /// M19 operator resolution gate: empty when every bound audio file is linked to a verified current row (or an
    /// addition is cleanly absent) and every album lyric is present with its exact bytes and registered once to its
    /// track's verified current row. Anything else is a reason to refuse resolution.
    /// </summary>
    public static ImmutableArray<string> AlbumConsistency(OperationManifest manifest, HostSnapshot host, FilesystemSnapshot fs)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = SalvagePreconditions(manifest, host, fs).ToList();
        if (problems.Count > 0) return problems.ToImmutableArray();
        var b = manifest.Binding;
        foreach (var id in b.Tracks.Select(t => t.TrackId).Concat(b.Kept.Select(k => k.TrackId)))
            if (VerifiedCurrent(manifest, id, host, fs) is null) problems.Add($"track {id}: not linked to a verified current file (acceptable path, exact bytes, sole owner)");
        foreach (var a in b.Additions)
        {
            var linked = host.Tracks.Where(x => x.TrackId == a.TrackId).Select(x => x.TrackFileId).FirstOrDefault();
            var cleanlyAbsent = linked == 0 && !fs.Files[a.DestinationPath].Exists && !host.TrackFiles.Any(f => PathCanonicalizer.PathsEqual(f.Path, a.DestinationPath, b.PathStyle));
            if (!cleanlyAbsent && VerifiedCurrent(manifest, a.TrackId, host, fs) is null) problems.Add($"addition {a.TrackId}: neither cleanly absent nor linked to a verified current file");
        }

        foreach (var l in b.AlbumLyrics)
        {
            var current = VerifiedCurrent(manifest, l.TrackId, host, fs);
            var rows = host.Lyrics.Where(x => string.Equals(x.RelativePath, l.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!fs.Files[l.Path].Is(l.Content)) problems.Add($"{l.RelativePath}: lyric not physically present with its original bytes");
            else if (current is null || rows.Count != 1 || rows[0].TrackFileId != current.TrackFileId) problems.Add($"{l.RelativePath}: not registered exactly once to its track's verified current file");
            if (!fs.Files[l.BackupPath].Is(l.Content)) problems.Add($"{l.RelativePath}: album backup unverifiable (evidence would be lost)");
        }

        return problems.ToImmutableArray();
    }

    /// <summary>Every path any binding of the operation names, each observed once.</summary>
    public static IEnumerable<string> AllBoundPaths(OperationBinding b) => b.Tracks.SelectMany(BoundPaths)
        .Concat(b.Additions.SelectMany(a => new[] { a.DestinationPath, a.IncomingPath }))
        .Concat(b.Kept.Select(k => k.Path))
        .Concat(b.AlbumLyrics.SelectMany(l => new[] { l.Path, l.BackupPath }))
        .Distinct(StringComparer.Ordinal);

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
