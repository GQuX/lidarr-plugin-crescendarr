using System.Text;
using System.Text.Json;
using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

/// <summary>High-level lab operations shared by the CLI and in-process specs.</summary>
public static class Lab
{
    public static string OperationId(string lab)
    {
        var file = Path.Combine(lab, LabScenario.OperationIdFile);
        if (File.Exists(file)) return File.ReadAllText(file, Encoding.ASCII).Trim();
        var id = Guid.NewGuid().ToString("D");
        DurableFiles.WriteAtomically(file, Encoding.ASCII.GetBytes(id), "lab.operationId");
        return id;
    }

    /// <summary>Starts (or resumes) the synthetic operation and runs automatic actions.</summary>
    public static ExecutorReport Run(string lab)
    {
        lab = LabSafety.RequireExistingRoot(lab);
        var id = OperationId(lab);
        var directory = Path.Combine(LabScenario.Ops(lab), id);
        OperationStore store;
        if (!Directory.Exists(directory))
        {
            store = OperationStore.Create(LabScenario.Ops(lab), LabScenario.Bind(lab, id));
        }
        else
        {
            var opened = OperationStore.TryOpen(directory);
            store = opened.Store ?? throw new InvalidOperationException($"Operation unreadable: {opened.Error}");
        }

        return LabExecutor.Run(lab, store);
    }

    public static ExecutorReport Recover(string lab, bool approve)
    {
        lab = LabSafety.RequireExistingRoot(lab);
        var store = Open(lab);
        return LabExecutor.Run(lab, store, approve);
    }

    public static OperationStore Open(string lab)
    {
        var directory = Path.Combine(LabScenario.Ops(lab), OperationId(lab));
        var opened = OperationStore.TryOpen(directory);
        return opened.Store ?? throw new InvalidOperationException($"Operation unreadable: {opened.Error}");
    }

    public static Reconciliation Reconcile(string lab)
    {
        lab = LabSafety.RequireExistingRoot(lab);
        var store = Open(lab);
        return Reconciler.Reconcile(store.Manifest, new FakeHost(lab).Snapshot(), FsObserver.Observe(store.Manifest.Binding));
    }

    /// <summary>
    /// Safety invariants that must hold at EVERY crash point: the protected superior track and
    /// its lyric are untouched; every target's original bytes are recoverable (in place or in a
    /// verified backup, once the intent is published); every physical lyric keeps its bytes;
    /// the monitored edition is unchanged. Returns violations (empty means safe).
    /// </summary>
    public static List<string> CheckInvariants(string lab, ContentIdSnapshot baseline)
    {
        var violations = new List<string>();
        var host = new FakeHost(lab).Load();
        var protectedTrack = host.Tracks.Single(t => t.TrackId == LabScenario.ProtectedTrackId);
        if (protectedTrack.TrackFileId != baseline.ProtectedTrackFileId) violations.Add("protected track relinked");
        if (!host.TrackFiles.Any(f => f.TrackFileId == baseline.ProtectedTrackFileId)) violations.Add("protected track-file row deleted");
        if (!host.Lyrics.Any(l => l.LyricFileId == baseline.ProtectedLyricId && l.TrackFileId == baseline.ProtectedTrackFileId)) violations.Add("protected lyric row changed");
        if (Hash(LabScenario.OriginalPath(lab, LabScenario.ProtectedTrackId)) != baseline.Originals[LabScenario.ProtectedTrackId]) violations.Add("protected audio bytes changed");
        if (Hash(LabScenario.LyricPath(lab, LabScenario.ProtectedTrackId)) != baseline.Lyrics[LabScenario.ProtectedTrackId]) violations.Add("protected lyric bytes changed");
        if (host.Releases.Count(r => r.Monitored) != 1 || !host.Releases.Single(r => r.Monitored).ReleaseMbid.Equals(LabScenario.MonitoredReleaseMbid, StringComparison.Ordinal))
            violations.Add("monitored edition changed");

        var store = Directory.Exists(LabScenario.Ops(lab)) && File.Exists(Path.Combine(lab, LabScenario.OperationIdFile)) ? OperationStore.TryOpen(Path.Combine(LabScenario.Ops(lab), OperationId(lab))).Store : null;
        foreach (var trackId in LabScenario.TargetTrackIds)
        {
            if (Hash(LabScenario.LyricPath(lab, trackId)) != baseline.Lyrics[trackId]) violations.Add($"track {trackId}: physical lyric bytes lost or changed");
            var inPlace = Hash(LabScenario.OriginalPath(lab, trackId)) == baseline.Originals[trackId];
            var binding = store?.Manifest.BindingFor(trackId);
            var backedUp = binding is not null && Hash(binding.BackupAudioPath) == baseline.Originals[trackId];
            if (!inPlace && !backedUp) violations.Add($"track {trackId}: original bytes neither in place nor in a verified backup");
            var incomingSomewhere = Hash(LabScenario.IncomingPath(lab, trackId)) == baseline.Incoming[trackId] || Hash(LabScenario.DestinationPath(lab, trackId)) == baseline.Incoming[trackId];
            if (!incomingSomewhere) violations.Add($"track {trackId}: incoming bytes lost");
        }

        return violations;
    }

    public static ContentIdSnapshot Baseline(string lab)
    {
        var host = new FakeHost(lab).Load();
        var protectedTrack = host.Tracks.Single(t => t.TrackId == LabScenario.ProtectedTrackId);
        var all = LabScenario.TargetTrackIds.Append(LabScenario.ProtectedTrackId).ToArray();
        return new ContentIdSnapshot(
            all.ToDictionary(t => t, t => Hash(LabScenario.OriginalPath(lab, t))!),
            LabScenario.TargetTrackIds.ToDictionary(t => t, t => Hash(LabScenario.IncomingPath(lab, t))!),
            all.ToDictionary(t => t, t => Hash(LabScenario.LyricPath(lab, t))!),
            protectedTrack.TrackFileId,
            host.Lyrics.Single(l => l.TrackFileId == protectedTrack.TrackFileId).LyricFileId);
    }

    public static string Describe(Reconciliation r) => JsonSerializer.Serialize(new
    {
        verdict = r.Verdict.ToString(),
        next = r.Next is null ? null : new { action = r.Next.Action.ToString(), mode = r.Next.Mode.ToString(), trackId = r.Next.TrackId, reason = r.Next.Reason },
        blockers = r.Blockers,
        tracks = r.Tracks.Select(t => new { t.TrackId, condition = t.Condition.ToString() }),
    });

    private static ContentId? Hash(string path) => File.Exists(path) ? DurableFiles.Hash(path) : null;
}

public sealed record ContentIdSnapshot(
    IReadOnlyDictionary<int, ContentId> Originals,
    IReadOnlyDictionary<int, ContentId> Incoming,
    IReadOnlyDictionary<int, ContentId> Lyrics,
    int ProtectedTrackFileId,
    int ProtectedLyricId);
