using Crescendarr.Core.Journal;

namespace Crescendarr.RecoveryLab;

public sealed record ExecutorReport(Verdict FinalVerdict, OperationStatus Status, IReadOnlyList<string> Executed, NextAction? Pending, IReadOnlyList<string> Blockers);

/// <summary>
/// Executes ONLY what the reconciler returns, one action at a time, re-observing host and disk
/// before every action. Operator-approval actions run only when <c>approve</c> is set.
/// Per-album serialization: an exclusive lock file is held for the whole run.
/// </summary>
public static class LabExecutor
{
    private const int MaxIterations = 200;

    public static ExecutorReport Run(string lab, OperationStore store, bool approve = false, int maxApprovals = 1)
    {
        var host = new FakeHost(lab);
        var executed = new List<string>();
        using var albumLock = new FileStream(Path.Combine(LabScenario.Ops(lab), $"album-{store.Manifest.Binding.AlbumId}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var approvals = 0;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var manifest = store.Manifest;
            var result = Reconciler.Reconcile(manifest, host.Snapshot(), FsObserver.Observe(manifest.Binding));
            var next = result.Next;

            if (result.Verdict == Verdict.Completed || next is null)
                return new ExecutorReport(result.Verdict, manifest.Status, executed, null, result.Blockers);

            if (next.Mode == ActionMode.OperatorApproval)
            {
                if (!approve || approvals >= maxApprovals)
                    return new ExecutorReport(result.Verdict, manifest.Status, executed, next, result.Blockers);
                approvals++;
            }
            else if (manifest.Status != OperationStatus.Active && next.Action != RecoveryAction.MarkReview)
            {
                return new ExecutorReport(result.Verdict, manifest.Status, executed, next, result.Blockers);
            }

            executed.Add($"{next.Action}:{next.TrackId}");
            Execute(lab, store, host, next);
            if (next.Action == RecoveryAction.MarkReview)
                return new ExecutorReport(Verdict.RequiresReview, store.Manifest.Status, executed, null, result.Blockers);
        }

        store.Save(JournalEngine.MarkReview(store.Manifest, "executor made no progress within the iteration bound"));
        return new ExecutorReport(Verdict.RequiresReview, store.Manifest.Status, executed, null, new[] { "no progress" });
    }

    private static void Execute(string lab, OperationStore store, FakeHost host, NextAction next)
    {
        var m = store.Manifest;
        var trackId = next.TrackId ?? 0;
        var track = next.TrackId is null ? null : m.BindingFor(trackId);
        var inFlight = next.TrackId is null ? null : m.ProgressFor(trackId).InFlight;

        switch (next.Action)
        {
            case RecoveryAction.CopyBackup:
                if (inFlight is null) Declare(store, trackId, JournalStep.CopyBackup, $"backup.{trackId}");
                DurableFiles.CopyDurably(track!.OriginalPath, track.BackupAudioPath, $"backup.{trackId}.audio");
                if (track.Lyric is { } lyric) DurableFiles.CopyDurably(lyric.Path, lyric.BackupPath, $"backup.{trackId}.lyric");
                Faults.Hit($"backup.{trackId}.afterCopy");
                // Verify by re-reading what is on disk now; an unverified backup never advances the phase.
                if (DurableFiles.Hash(track.BackupAudioPath) != track.Original || (track.Lyric is { } l && DurableFiles.Hash(l.BackupPath) != l.Content))
                {
                    store.Save(JournalEngine.MarkReview(store.Manifest, $"track {trackId}: backup verification failed"));
                    return;
                }

                store.Save(JournalEngine.MarkDone(store.Manifest, trackId, JournalStep.CopyBackup));
                Faults.Hit($"backup.{trackId}.afterPublish");
                break;

            case RecoveryAction.DetachLyric:
                if (inFlight is null) Declare(store, trackId, JournalStep.DetachLyric, $"lyric.{trackId}");
                host.DeleteLyricRow(track!.Lyric!.LyricFileId, $"lyric.{trackId}.detach");
                Faults.Hit($"lyric.{trackId}.afterDetach");
                store.Save(JournalEngine.MarkDone(store.Manifest, trackId, JournalStep.DetachLyric));
                Faults.Hit($"lyric.{trackId}.afterPublish");
                break;

            case RecoveryAction.InvokeNativeUpgrade:
                Declare(store, trackId, JournalStep.NativeUpgrade, $"native.{trackId}");
                // Revalidate immediately before the destructive call: the declared step must still
                // classify as "not started" against fresh observations, or nothing is called.
                var recheck = Reconciler.Reconcile(store.Manifest, host.Snapshot(), FsObserver.Observe(store.Manifest.Binding));
                if (recheck.Next?.Action != RecoveryAction.AbandonStep)
                {
                    store.Save(JournalEngine.MarkReview(store.Manifest, $"track {trackId}: revalidation before native upgrade failed"));
                    return;
                }

                var newId = host.NativeUpgrade(trackId, track!.IncomingPath, track.DestinationPath);
                store.Save(JournalEngine.MarkDone(store.Manifest, trackId, JournalStep.NativeUpgrade, newTrackFileId: newId));
                Faults.Hit($"native.{trackId}.afterPublish");
                break;

            case RecoveryAction.RelinkLyric:
                if (inFlight is null) Declare(store, trackId, JournalStep.RelinkLyric, $"relink.{trackId}");
                var newLyric = host.InsertLyricRow(store.Manifest.ProgressFor(trackId).NewTrackFileId!.Value, track!.Lyric!.RelativePath, $"relink.{trackId}.insert");
                Faults.Hit($"relink.{trackId}.afterInsert");
                store.Save(JournalEngine.MarkDone(store.Manifest, trackId, JournalStep.RelinkLyric, newLyricFileId: newLyric));
                Faults.Hit($"relink.{trackId}.afterPublish");
                break;

            case RecoveryAction.RecordStepDone:
                store.Save(JournalEngine.MarkDone(m, trackId, inFlight!.Value, next.NewTrackFileId, next.NewLyricFileId));
                break;

            case RecoveryAction.AbandonStep:
                store.Save(JournalEngine.AbandonInFlight(m, trackId, inFlight!.Value));
                break;

            case RecoveryAction.RestoreOriginalBytes:
                DurableFiles.RestoreWithoutOverwrite(track!.BackupAudioPath, track.OriginalPath, track.Original, $"restore.{trackId}");
                break;

            case RecoveryAction.InsertFreshTrackFileForOriginal:
                host.InsertFreshTrackFile(trackId, track!.OriginalPath, $"approve.{trackId}.original");
                break;

            case RecoveryAction.InsertFreshTrackFileForIncoming:
                host.InsertFreshTrackFile(trackId, track!.DestinationPath, $"approve.{trackId}.incoming");
                break;

            case RecoveryAction.ReattachOriginalLyric:
                var linked = host.Snapshot().Tracks.Single(t => t.TrackId == trackId).TrackFileId;
                host.InsertLyricRow(linked, track!.Lyric!.RelativePath, $"approve.{trackId}.lyric");
                break;

            case RecoveryAction.CompleteOperation:
                Faults.Hit("complete.beforePublish");
                store.Save(JournalEngine.Complete(m));
                break;

            case RecoveryAction.MarkReview:
                if (m.Status == OperationStatus.Active) store.Save(JournalEngine.MarkReview(m, next.Reason));
                break;

            default:
                throw new InvalidOperationException($"Unexpected action {next.Action}.");
        }
    }

    private static void Declare(OperationStore store, int trackId, JournalStep step, string prefix)
    {
        store.Save(JournalEngine.MarkInFlight(store.Manifest, trackId, step));
        Faults.Hit($"{prefix}.afterInFlight");
    }
}
