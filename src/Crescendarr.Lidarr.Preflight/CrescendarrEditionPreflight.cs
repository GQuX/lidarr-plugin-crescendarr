// Source-pinned stock Lidarr 3.1.6.5079 preflight component.
// READ ONLY: this does not invoke ImportApprovedTracks, SetMonitored or any writer.
// Deliberately not a registered Plugin or a deployable production entry point.
using System;
using System.Collections.Generic;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public enum CrescendarrEditionPreflightReason
    {
        ExactEditionConfirmed,
        InvalidExpectedIdentity,
        NoApprovedCandidates,
        TooManyCandidates,
        CompletedDownloadHandlingEnabled,
        AlbumUnmonitored,
        AlbumAllowsAnyEdition,
        AlbumIdentityChanged,
        MonitoredReleaseAmbiguous,
        SelectedEditionChanged,
        CandidateEditionMismatch,
        TrackIdentityMismatch,
        HostReadFailed
    }

    /// <summary>
    /// An exact-match READ-ONLY proof, never import permission. An executor must still
    /// require a valid plan, durable journal, unchanged bytes/lyrics, a host lock
    /// and another authoritative recheck immediately before each native mutation.
    /// </summary>
    public sealed record CrescendarrEditionPreflightResult(
        CrescendarrEditionPreflightReason Reason,
        int AlbumId,
        int SelectedReleaseRowId)
    {
        public bool EditionMatches => Reason == CrescendarrEditionPreflightReason.ExactEditionConfirmed;
    }

    /// <summary>
    /// Queries UNMODIFIED stock Album/Release/Track repositories at the last read-only
    /// preflight boundary. It never changes Lidarr state and fails closed on any
    /// ambiguity, exception or discrepancy with the previously approved plan.
    /// </summary>
    public sealed class CrescendarrEditionPreflight
    {
        private const int MaxCandidates = 4096;
        private readonly IAlbumRepository _albums;
        private readonly IReleaseService _releases;
        private readonly ITrackRepository _tracks;
        private readonly IConfigService _config;

        public CrescendarrEditionPreflight(
            IAlbumRepository albums, IReleaseService releases,
            ITrackRepository tracks, IConfigService config)
        {
            _albums = albums ?? throw new ArgumentNullException(nameof(albums));
            _releases = releases ?? throw new ArgumentNullException(nameof(releases));
            _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public CrescendarrEditionPreflightResult Check(
            int expectedAlbumId,
            string expectedMusicBrainzAlbumGroupId,
            string expectedMusicBrainzReleaseId,
            IReadOnlyList<ImportDecision<LocalTrack>> decisions)
        {
            CrescendarrEditionPreflightResult Block(CrescendarrEditionPreflightReason reason) =>
                new(reason, expectedAlbumId, 0);

            if (expectedAlbumId <= 0 ||
                !ValidId(expectedMusicBrainzAlbumGroupId, out _) ||
                !ValidId(expectedMusicBrainzReleaseId, out var expectedReleaseId) ||
                decisions == null)
            {
                return Block(CrescendarrEditionPreflightReason.InvalidExpectedIdentity);
            }

            if (decisions.Count == 0 || !HasApproved(decisions))
                return Block(CrescendarrEditionPreflightReason.NoApprovedCandidates);
            if (decisions.Count > MaxCandidates)
                return Block(CrescendarrEditionPreflightReason.TooManyCandidates);

            try
            {
                // Stock native importer may race with CDH when it is ON.
                if (_config.EnableCompletedDownloadHandling)
                    return Block(CrescendarrEditionPreflightReason.CompletedDownloadHandlingEnabled);

                var album = _albums.Get(expectedAlbumId);
                if (album == null || album.Id != expectedAlbumId ||
                    !SameId(album.ForeignAlbumId, expectedMusicBrainzAlbumGroupId))
                    return Block(CrescendarrEditionPreflightReason.AlbumIdentityChanged);
                if (!album.Monitored)
                    return Block(CrescendarrEditionPreflightReason.AlbumUnmonitored);
                if (album.AnyReleaseOk)
                    return Block(CrescendarrEditionPreflightReason.AlbumAllowsAnyEdition);

                // This reads actual current DB rows, never the detached AlbumReleases
                // in a candidate LocalTrack. SetMonitored updates *all* these rows.
                var releases = _releases.GetReleasesByAlbum(expectedAlbumId);
                if (releases == null || releases.Count == 0)
                    return Block(CrescendarrEditionPreflightReason.MonitoredReleaseAmbiguous);

                AlbumRelease selected = null;
                var releaseIds = new HashSet<int>();
                foreach (var release in releases)
                {
                    if (release == null || release.Id <= 0 || release.AlbumId != expectedAlbumId ||
                        !ValidId(release.ForeignReleaseId, out _) || !releaseIds.Add(release.Id))
                        return Block(CrescendarrEditionPreflightReason.MonitoredReleaseAmbiguous);
                    if (release.Monitored)
                    {
                        if (selected != null)
                            return Block(CrescendarrEditionPreflightReason.MonitoredReleaseAmbiguous);
                        selected = release;
                    }
                }

                if (selected == null)
                    return Block(CrescendarrEditionPreflightReason.MonitoredReleaseAmbiguous);
                if (!ValidId(selected.ForeignReleaseId, out var actualReleaseId) ||
                    actualReleaseId != expectedReleaseId)
                    return Block(CrescendarrEditionPreflightReason.SelectedEditionChanged);

                var candidateTrackIds = new HashSet<int>();
                foreach (var decision in decisions)
                {
                    var incoming = decision?.Item;
                    if (incoming?.Album == null || incoming.Release == null ||
                        incoming.Album.Id != expectedAlbumId ||
                        !SameId(incoming.Album.ForeignAlbumId, expectedMusicBrainzAlbumGroupId) ||
                        incoming.Release.Id != selected.Id ||
                        incoming.Release.AlbumId != expectedAlbumId ||
                        !SameId(incoming.Release.ForeignReleaseId, selected.ForeignReleaseId))
                        return Block(CrescendarrEditionPreflightReason.CandidateEditionMismatch);

                    // One physical file per release track: compound file import stays
                    // unapproved until its semantics are independently verified.
                    if (incoming.Tracks == null || incoming.Tracks.Count != 1)
                        return Block(CrescendarrEditionPreflightReason.TrackIdentityMismatch);
                    var track = incoming.Tracks[0];
                    if (track == null || track.Id <= 0 ||
                        !candidateTrackIds.Add(track.Id) ||
                        track.AlbumReleaseId != selected.Id ||
                        !ValidId(track.ForeignTrackId, out _) ||
                        !ValidId(track.ForeignRecordingId, out _))
                        return Block(CrescendarrEditionPreflightReason.TrackIdentityMismatch);

                    // Never infer native TrackFile IDs or recording/track UUIDs from
                    // potentially stale in-memory candidate objects.
                    var actualTrack = _tracks.Get(track.Id);
                    if (actualTrack == null ||
                        actualTrack.Id != track.Id ||
                        actualTrack.AlbumReleaseId != selected.Id ||
                        actualTrack.TrackFileId != track.TrackFileId ||
                        !SameId(actualTrack.ForeignTrackId, track.ForeignTrackId) ||
                        !SameId(actualTrack.ForeignRecordingId, track.ForeignRecordingId))
                        return Block(CrescendarrEditionPreflightReason.TrackIdentityMismatch);
                }

                return new CrescendarrEditionPreflightResult(
                    CrescendarrEditionPreflightReason.ExactEditionConfirmed,
                    expectedAlbumId, selected.Id);
            }
            catch (Exception)
            {
                // A failed or stale repository read MUST NOT become permission
                // to call ImportApprovedTracks. No exception messages are returned.
                return Block(CrescendarrEditionPreflightReason.HostReadFailed);
            }
        }

        private static bool HasApproved(IReadOnlyList<ImportDecision<LocalTrack>> decisions)
        {
            foreach (var decision in decisions)
            {
                if (decision?.Approved == true) return true;
            }

            return false;
        }

        private static bool ValidId(string value, out Guid parsed) =>
            Guid.TryParse(value, out parsed) && parsed != Guid.Empty;

        private static bool SameId(string left, string right) =>
            ValidId(left, out var a) && ValidId(right, out var b) && a == b;
    }
}
