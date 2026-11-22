using System.Collections.Immutable;

namespace Crescendarr.Core.Journal;

/// <summary>
/// Pure, fail-closed transitions. Every method returns a new manifest with
/// <see cref="OperationManifest.Generation"/> incremented, or throws
/// <see cref="JournalTransitionException"/>. Rules:
/// <list type="bullet">
/// <item>at most one step is in flight across the whole operation (per-album serialization);</item>
/// <item>phases only advance one step, and only by completing the declared in-flight step;</item>
/// <item>no lyric detach or native upgrade may be declared until every track's backup is verified;</item>
/// <item>a RequiresReview or Completed operation accepts no further transitions.</item>
/// </list>
/// </summary>
public static class JournalEngine
{
    public const int MaxInterruptedAttempts = 3;
    public const int MaxReviewReasons = 32;
    public const int MaxReasonLength = 256;

    public static OperationManifest Begin(OperationBinding binding)
    {
        var errors = JournalValidation.Validate(binding);
        if (!errors.IsEmpty) throw new JournalTransitionException("Invalid binding: " + string.Join(" ", errors));
        var progress = binding.Tracks
            .Select(t => new TrackProgress(t.TrackId, TrackPhase.Planned, null, 0, null, null))
            .ToImmutableArray();
        return new OperationManifest(1, OperationStatus.Active, ImmutableArray<string>.Empty, binding, JournalCodec.BindingDigest(binding), progress);
    }

    public static TrackPhase FinalPhase(TrackBinding track) => track.Lyric is null ? TrackPhase.Upgraded : TrackPhase.LyricRelinked;

    public static bool AllBackupsVerified(OperationManifest manifest) =>
        manifest.Progress.All(p => p.Phase >= TrackPhase.BackupVerified);

    public static bool IsFinal(OperationManifest manifest, int trackId) =>
        manifest.ProgressFor(trackId).Phase == FinalPhase(manifest.BindingFor(trackId));

    /// <summary>Which step may be declared next for a track, or null when none.</summary>
    public static JournalStep? NextStep(OperationManifest manifest, int trackId)
    {
        var track = manifest.BindingFor(trackId);
        var progress = manifest.ProgressFor(trackId);
        return progress.Phase switch
        {
            TrackPhase.Planned => JournalStep.CopyBackup,
            TrackPhase.BackupVerified when !AllBackupsVerified(manifest) => null,
            TrackPhase.BackupVerified => track.Lyric is null ? JournalStep.NativeUpgrade : JournalStep.DetachLyric,
            TrackPhase.LyricDetached => JournalStep.NativeUpgrade,
            TrackPhase.Upgraded when track.Lyric is not null => JournalStep.RelinkLyric,
            _ => null,
        };
    }

    public static OperationManifest MarkInFlight(OperationManifest manifest, int trackId, JournalStep step)
    {
        RequireActive(manifest);
        var progress = Find(manifest, trackId);
        if (manifest.InFlightTrack is { } busy)
            throw new JournalTransitionException($"Track {busy.TrackId} already has {busy.InFlight} in flight.");
        if (NextStep(manifest, trackId) != step)
            throw new JournalTransitionException($"Step {step} is not allowed for track {trackId} in phase {progress.Phase}.");
        return Replace(manifest, progress with { InFlight = step });
    }

    public static OperationManifest MarkDone(OperationManifest manifest, int trackId, JournalStep step, int? newTrackFileId = null, int? newLyricFileId = null)
    {
        RequireActive(manifest);
        var progress = Find(manifest, trackId);
        var track = manifest.BindingFor(trackId);
        if (progress.InFlight != step) throw new JournalTransitionException($"Step {step} was not declared in flight for track {trackId}.");

        var next = step switch
        {
            JournalStep.CopyBackup => progress with { Phase = TrackPhase.BackupVerified },
            JournalStep.DetachLyric => progress with { Phase = TrackPhase.LyricDetached },
            JournalStep.NativeUpgrade => progress with { Phase = TrackPhase.Upgraded, NewTrackFileId = RequireFreshTrackFileId(manifest, track, newTrackFileId) },
            JournalStep.RelinkLyric => progress with { Phase = TrackPhase.LyricRelinked, NewLyricFileId = RequireFreshLyricId(track, newLyricFileId) },
            _ => throw new JournalTransitionException("Unknown step."),
        };

        return Replace(manifest, next with { InFlight = null });
    }

    /// <summary>
    /// Clears an in-flight declaration after reconciliation proved by observation that the step
    /// had no effect. Bounded: repeated interruptions escalate to review.
    /// </summary>
    public static OperationManifest AbandonInFlight(OperationManifest manifest, int trackId, JournalStep step)
    {
        RequireActive(manifest);
        var progress = Find(manifest, trackId);
        if (progress.InFlight != step) throw new JournalTransitionException($"Step {step} is not in flight for track {trackId}.");
        var attempts = progress.InterruptedAttempts + 1;
        var cleared = Replace(manifest, progress with { InFlight = null, InterruptedAttempts = attempts });
        return attempts > MaxInterruptedAttempts
            ? MarkReview(cleared, $"Track {trackId}: {step} interrupted {attempts} times.")
            : cleared;
    }

    public static OperationManifest MarkReview(OperationManifest manifest, string reason)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Status == OperationStatus.Completed) throw new JournalTransitionException("A completed operation cannot be reopened for review.");
        var clean = Sanitize(reason);
        var reasons = manifest.ReviewReasons.Contains(clean) || manifest.ReviewReasons.Length >= MaxReviewReasons
            ? manifest.ReviewReasons
            : manifest.ReviewReasons.Add(clean);
        if (manifest.Status == OperationStatus.RequiresReview && reasons == manifest.ReviewReasons) return manifest;
        return manifest with { Generation = manifest.Generation + 1, Status = OperationStatus.RequiresReview, ReviewReasons = reasons };
    }

    public static OperationManifest Complete(OperationManifest manifest)
    {
        RequireActive(manifest);
        if (manifest.InFlightTrack is not null) throw new JournalTransitionException("A step is still in flight.");
        if (!manifest.Binding.Tracks.All(t => IsFinal(manifest, t.TrackId))) throw new JournalTransitionException("Not every track reached its final phase.");
        return manifest with { Generation = manifest.Generation + 1, Status = OperationStatus.Completed };
    }

    private static int RequireFreshTrackFileId(OperationManifest manifest, TrackBinding track, int? id)
    {
        // A fresh row is required: never reuse the deleted original row's numeric ID, nor any
        // other bound original ID.
        if (id is not > 0 || manifest.Binding.Tracks.Any(t => t.OriginalTrackFileId == id))
            throw new JournalTransitionException($"Track {track.TrackId}: upgrade must record a fresh, positive track-file ID.");
        if (manifest.Progress.Any(p => p.TrackId != track.TrackId && p.NewTrackFileId == id))
            throw new JournalTransitionException($"Track {track.TrackId}: track-file ID {id} is already recorded for another track.");
        return id.Value;
    }

    private static int RequireFreshLyricId(TrackBinding track, int? id)
    {
        if (id is not > 0 || id == track.Lyric!.LyricFileId)
            throw new JournalTransitionException($"Track {track.TrackId}: relink must record a fresh, positive lyric-file ID.");
        return id.Value;
    }

    private static void RequireActive(OperationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Status != OperationStatus.Active) throw new JournalTransitionException($"Operation is {manifest.Status}; no transitions allowed.");
    }

    private static TrackProgress Find(OperationManifest manifest, int trackId) =>
        manifest.Progress.SingleOrDefault(p => p.TrackId == trackId) ?? throw new JournalTransitionException($"Track {trackId} is not bound.");

    private static OperationManifest Replace(OperationManifest manifest, TrackProgress progress) => manifest with
    {
        Generation = manifest.Generation + 1,
        Progress = manifest.Progress.Select(p => p.TrackId == progress.TrackId ? progress : p).ToImmutableArray(),
    };

    internal static string Sanitize(string? reason)
    {
        var text = Crescendarr.Core.Inventory.InventoryText.Evidence(reason) ?? "unspecified";
        return text.Length > MaxReasonLength ? text[..MaxReasonLength] : text;
    }
}
