using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Crescendarr.Core.Domain;
using Crescendarr.Core.Policies;

namespace Crescendarr.Core.Planning;

/// <summary>
/// Pure read-only planner. It cannot import, move, rename, delete, settle downloads,
/// change album editions, or call Lidarr. Review anywhere blocks the WHOLE plan.
/// </summary>
public sealed class MergePlanner : IMergePlanner
{
    public MergePlan Plan(DownloadSnapshot incoming, AlbumSnapshot current, IHostQualityPolicy hostQuality)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(hostQuality);

        var snapshotValid = incoming.AlbumId > 0 && current.AlbumId > 0
            && incoming.AlbumId == current.AlbumId
            && !string.IsNullOrWhiteSpace(incoming.DownloadId)
            && !string.IsNullOrWhiteSpace(incoming.InventoryFingerprint)
            && !string.IsNullOrWhiteSpace(current.StateFingerprint)
            && HasGuid(incoming.EditionId) && HasGuid(current.EditionId)
            && !incoming.Files.IsDefault && !current.Files.IsDefault
            // One Lidarr track-file record linked to multiple snapshots is not safe to replace per track.
            && current.Files.Select(f => f.TrackFileId).Distinct().Count() == current.Files.Length
            && current.Files.All(f => f.TrackFileId > 0 && !f.RecordingIds.IsDefault
                && f.RecordingIds.Length > 0 && f.RecordingIds.All(HasGuid));

        var editionMatches = snapshotValid && SameId(incoming.EditionId, current.EditionId);
        var globalBlockReason = !snapshotValid ? ReasonCodes.InvalidSnapshot
            : !editionMatches ? ReasonCodes.EditionMismatch : null;

        // The only automatic identity proof at this stage is an exact MusicBrainz recording ID.
        // Multi-recording physical files are deliberately sent for review, NOT split by the planner.
        var existingByRecording = new Dictionary<string, List<ExistingFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in current.Files.IsDefault ? ImmutableArray<ExistingFile>.Empty : current.Files)
        {
            foreach (var recordingId in file.RecordingIds.IsDefault ? ImmutableArray<string>.Empty : file.RecordingIds)
            {
                if (!HasGuid(recordingId)) continue;
                var normalized = NormalizeId(recordingId);
                if (!existingByRecording.TryGetValue(normalized, out var list))
                {
                    list = [];
                    existingByRecording.Add(normalized, list);
                }
                list.Add(file);
            }
        }

        var candidates = incoming.Files.IsDefault
            ? ImmutableArray<IncomingFile>.Empty
            : incoming.Files.OrderBy(x => x.CandidateKey, StringComparer.Ordinal).ToImmutableArray();
        var duplicateKeys = candidates.GroupBy(c => c.CandidateKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var duplicateRecordings = candidates
            .Where(c => !c.RecordingIds.IsDefault && c.RecordingIds.Length == 1 && HasGuid(c.RecordingIds[0]))
            .GroupBy(c => NormalizeId(c.RecordingIds[0]), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var decisions = ImmutableArray.CreateBuilder<TrackDecision>();
        foreach (var candidate in candidates)
        {
            var ids = candidate.RecordingIds.IsDefault
                ? ImmutableArray<string>.Empty : candidate.RecordingIds;
            var sidecar = new SidecarRequirement(false, candidate.HasStagedLrc);
            var matchingExisting = ids.Length == 1 && HasGuid(ids[0])
                && existingByRecording.TryGetValue(NormalizeId(ids[0]), out var matches)
                ? matches : null;
            var existing = matchingExisting is { Count: 1 } ? matchingExisting[0] : null;
            sidecar = sidecar with { PreserveExistingLrc = existing?.HasLibraryLrc ?? false };

            TrackDecision Decide(MergeAction action, string reason) => new(
                candidate.CandidateKey, ids, action, reason, existing?.TrackFileId, sidecar);

            if (globalBlockReason is not null)
                decisions.Add(Decide(MergeAction.NeedsReview, globalBlockReason));
            else if (string.IsNullOrWhiteSpace(candidate.CandidateKey) || ids.Length == 0
                || ids.Any(id => !HasGuid(id)))
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.UnverifiedIdentity));
            else if (ids.Length != 1 || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.CompoundFile));
            else if (duplicateKeys.Contains(candidate.CandidateKey))
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.DuplicateCandidate));
            else if (duplicateRecordings.Contains(NormalizeId(ids[0])))
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.DuplicateRecording));
            else if (!candidate.IdentityVerified)
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.UnverifiedIdentity));
            else if (!candidate.SourceStable)
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.SourceChanged));
            else if (!candidate.HostImportEligible)
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.HostIneligible));
            else if (matchingExisting is { Count: > 1 })
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.DuplicateRecording));
            else if (existing is not null && (existing.RecordingIds.Length != 1
                || !existing.MetadataVerified || !existing.FileVerifiedPresent || existing.TrackFileId <= 0))
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.ExistingUnverified));
            else if (string.IsNullOrWhiteSpace(candidate.HostQualityToken))
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.MissingQuality));
            else if (existing is null)
                decisions.Add(Decide(MergeAction.AddMissing, ReasonCodes.AddMissing));
            else if (string.IsNullOrWhiteSpace(existing.HostQualityToken))
                decisions.Add(Decide(MergeAction.NeedsReview, ReasonCodes.MissingQuality));
            else
            {
                // No exception, enum or unsupported quality setting is a license to delete.
                IncomingQuality comparison;
                try { comparison = hostQuality.Compare(incoming.AlbumId, candidate.HostQualityToken, existing.HostQualityToken); }
                catch { comparison = IncomingQuality.Incomparable; }

                decisions.Add(comparison switch
                {
                    IncomingQuality.StrictlyPreferred => Decide(MergeAction.Upgrade, ReasonCodes.Upgrade),
                    IncomingQuality.Equal or IncomingQuality.Inferior => Decide(MergeAction.KeepExisting, ReasonCodes.ExistingBetter),
                    _ => Decide(MergeAction.NeedsReview, ReasonCodes.QualityUnknown)
                });
            }
        }

        var result = decisions.ToImmutable();
        var disposition = globalBlockReason == ReasonCodes.InvalidSnapshot || candidates.IsEmpty
            ? PlanDisposition.Blocked
            : result.Any(x => x.Action == MergeAction.NeedsReview) ? PlanDisposition.NeedsReview
            : result.Any(x => x.Action is MergeAction.AddMissing or MergeAction.Upgrade) ? PlanDisposition.Ready
            : PlanDisposition.NoImprovement;
        var fingerprint = Fingerprint(incoming, current, result);
        return new MergePlan(fingerprint, incoming.DownloadId, incoming.AlbumId,
            current.EditionId, incoming.InventoryFingerprint, current.StateFingerprint,
            disposition, result);
    }

    private static bool HasGuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    private static string NormalizeId(string value) => Guid.Parse(value).ToString("D");
    private static bool SameId(string first, string second) => Guid.Parse(first) == Guid.Parse(second);

    private static string Fingerprint(DownloadSnapshot incoming, AlbumSnapshot current,
        ImmutableArray<TrackDecision> decisions)
    {
        var text = new StringBuilder();
        static void Part(StringBuilder builder, string? value)
        {
            var s = value ?? string.Empty;
            builder.Append(s.Length).Append(':').Append(s);
        }
        Part(text, incoming.DownloadId);
        Part(text, incoming.AlbumId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Part(text, incoming.EditionId);
        Part(text, incoming.InventoryFingerprint);
        Part(text, current.StateFingerprint);
        Part(text, current.AlbumId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Part(text, current.EditionId);

        // Include COMPLETE incoming and existing snapshots. Two different sets of bytes,
        // quality metadata or sidecar inventories must not reuse an approval plan ID
        // merely because they happen to yield the same Add/Upgrade/Keep decision labels.
        // The host must still recompute and compare authoritative snapshots before writes.
        foreach (var file in (incoming.Files.IsDefault ? ImmutableArray<IncomingFile>.Empty : incoming.Files)
            .OrderBy(f => f.CandidateKey, StringComparer.Ordinal))
        {
            Part(text, file.CandidateKey);
            Part(text, file.HostQualityToken);
            Part(text, file.IdentityVerified ? "1" : "0");
            Part(text, file.SourceStable ? "1" : "0");
            Part(text, file.HostImportEligible ? "1" : "0");
            Part(text, file.HasStagedLrc ? "1" : "0");
            foreach (var id in file.RecordingIds.IsDefault ? ImmutableArray<string>.Empty : file.RecordingIds)
                Part(text, id);
        }
        foreach (var file in (current.Files.IsDefault ? ImmutableArray<ExistingFile>.Empty : current.Files)
            .OrderBy(f => f.TrackFileId))
        {
            Part(text, file.TrackFileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Part(text, file.HostQualityToken);
            Part(text, file.MetadataVerified ? "1" : "0");
            Part(text, file.FileVerifiedPresent ? "1" : "0");
            Part(text, file.HasLibraryLrc ? "1" : "0");
            foreach (var id in file.RecordingIds.IsDefault ? ImmutableArray<string>.Empty : file.RecordingIds)
                Part(text, id);
        }
        foreach (var decision in decisions)
        {
            Part(text, decision.CandidateKey);
            foreach (var recording in decision.RecordingIds) Part(text, recording);
            Part(text, decision.Action.ToString());
            Part(text, decision.ReasonCode);
            Part(text, decision.ExpectedExistingTrackFileId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Part(text, decision.Lyrics.PreserveExistingLrc ? "L1" : "L0");
            Part(text, decision.Lyrics.PreserveStagedLrc ? "S1" : "S0");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
