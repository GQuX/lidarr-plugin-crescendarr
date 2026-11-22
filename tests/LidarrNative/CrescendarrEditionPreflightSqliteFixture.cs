// Native preflight tests run ONLY in throwaway, SHA-pinned stock Lidarr Core.Test.
// Real migrated SQLite Album/Release/Track repositories, no real download or import.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrEditionPreflightSqliteFixture : DbTest
    {
        private const string GroupId = "cf825c29-f0c9-4e6b-b7df-5ace592c54af";
        private const string SelectedId = "244361c1-e8d9-4ca6-8bad-6c0980811af0";
        private const string AlternateId = "f36ce30a-cbb6-47e9-85b7-828d95dad413";
        private const string TrackId = "b51789ba-1e27-4133-85ba-2e137b4a5b75";
        private const string RecordingId = "6b13e0c0-78ec-42cb-975d-1f6ae0018b52";

        private Album _album;
        private AlbumRelease _selected;
        private AlbumRelease _alternate;
        private Track _track;
        private IAlbumRepository _albums;
        private IReleaseService _releases;
        private ITrackRepository _tracks;
        private CrescendarrEditionPreflight _gate;

        [SetUp]
        public void Setup()
        {
            var metadata = Builder<ArtistMetadata>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ForeignArtistId = "a27f9c86-b885-4c66-8fbb-e27111a30a2f")
                .With(x => x.Name = "Crescendarr Preflight Synthetic Artist")
                .Build();
            Db.Insert(metadata);

            _album = Builder<Album>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.ForeignAlbumId = GroupId)
                .With(x => x.Monitored = true)
                .With(x => x.AnyReleaseOk = false)
                .Build();
            Db.Insert(_album);

            _selected = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.AlbumId = _album.Id)
                .With(x => x.ForeignReleaseId = SelectedId)
                .With(x => x.Monitored = true)
                .With(x => x.TrackCount = 1)
                .Build();
            Db.Insert(_selected);
            _selected.Album = _album;

            _alternate = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.AlbumId = _album.Id)
                .With(x => x.ForeignReleaseId = AlternateId)
                .With(x => x.Monitored = false)
                .With(x => x.TrackCount = 1)
                .Build();
            Db.Insert(_alternate);
            _alternate.Album = _album;

            _track = Builder<Track>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.AlbumReleaseId = _selected.Id)
                .With(x => x.ForeignTrackId = TrackId)
                .With(x => x.ForeignRecordingId = RecordingId)
                .With(x => x.TrackFileId = 0)
                .Build();
            Db.Insert(_track);

            _albums = Mocker.Resolve<AlbumRepository>();
            Mocker.SetConstant<IAlbumRepository>(_albums);
            var releaseRepository = Mocker.Resolve<ReleaseRepository>();
            Mocker.SetConstant<IReleaseRepository>(releaseRepository);
            _releases = Mocker.Resolve<ReleaseService>();
            Mocker.SetConstant<IReleaseService>(_releases);
            _tracks = Mocker.Resolve<TrackRepository>();
            Mocker.SetConstant<ITrackRepository>(_tracks);
            Mocker.GetMock<IConfigService>()
                .SetupGet(x => x.EnableCompletedDownloadHandling)
                .Returns(false);
            _gate = Mocker.Resolve<CrescendarrEditionPreflight>();
        }

        private ImportDecision<LocalTrack> Decision(AlbumRelease release = null, Track track = null, bool approved = true)
        {
            var candidate = new LocalTrack
            {
                Album = _album,
                Release = release ?? _selected,
                Tracks = new List<Track> { track ?? _track }
            };
            return approved
                ? new ImportDecision<LocalTrack>(candidate)
                : new ImportDecision<LocalTrack>(candidate, new Rejection("Synthetic rejected candidate"));
        }

        private CrescendarrEditionPreflightResult Check(params ImportDecision<LocalTrack>[] decisions) =>
            _gate.Check(_album.Id, GroupId, SelectedId, decisions);

        private void ConfirmNoReleaseMutation()
        {
            var releases = Db.All<AlbumRelease>().ToDictionary(x => x.Id);
            Assert.That(releases.Count, Is.EqualTo(2));
            Assert.That(releases[_selected.Id].Monitored, Is.True);
            Assert.That(releases[_alternate.Id].Monitored, Is.False);
        }

        [Test]
        public void selected_edition_with_native_track_and_album_ids_is_confirmed_read_only()
        {
            var result = Check(Decision());
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.ExactEditionConfirmed));
            Assert.That(result.EditionMatches, Is.True);
            Assert.That(result.SelectedReleaseRowId, Is.EqualTo(_selected.Id));
            ConfirmNoReleaseMutation();
            Assert.That(Db.All<Track>().Single().TrackFileId, Is.Zero);
        }

        [Test]
        public void alternate_candidate_is_denied_before_calling_any_stock_importer()
        {
            var result = Check(Decision(_alternate));
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.CandidateEditionMismatch));
            Assert.That(result.EditionMatches, Is.False);
            ConfirmNoReleaseMutation();
            Mocker.GetMock<IImportApprovedTracks>().Verify(x => x.Import(
                It.IsAny<List<ImportDecision<LocalTrack>>>(), It.IsAny<bool>(),
                It.IsAny<NzbDrone.Core.Download.DownloadClientItem>(),
                It.IsAny<ImportMode>()), Times.Never());
        }

        [Test]
        public void changed_selected_release_in_real_sqlite_is_denied()
        {
            _releases.SetMonitored(_alternate);
            var result = Check(Decision());
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.SelectedEditionChanged));
            Assert.That(result.EditionMatches, Is.False);
            Assert.That(Db.All<AlbumRelease>().Single(x => x.Monitored).Id, Is.EqualTo(_alternate.Id));
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void missing_or_multiple_monitored_releases_are_denied(bool monitorOriginal, bool monitorAlternate)
        {
            _selected.Monitored = monitorOriginal;
            _alternate.Monitored = monitorAlternate;
            Mocker.Resolve<IReleaseRepository>().Update(_selected);
            Mocker.Resolve<IReleaseRepository>().Update(_alternate);
            var result = Check(Decision());
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.MonitoredReleaseAmbiguous));
        }

        [Test]
        public void album_group_identity_changed_in_real_sqlite_is_denied()
        {
            _album.ForeignAlbumId = "df07eb77-1911-441f-b6e6-613a51ab107a";
            _albums.Update(_album);
            var result = Check(Decision());
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.AlbumIdentityChanged));
        }

        [Test]
        public void a_changed_native_trackfile_link_or_recording_identity_blocks_import()
        {
            var outdated = new Track
            {
                Id = _track.Id,
                AlbumReleaseId = _track.AlbumReleaseId,
                ForeignTrackId = _track.ForeignTrackId,
                ForeignRecordingId = _track.ForeignRecordingId,
                TrackFileId = 27391
            };
            Assert.That(Check(Decision(track: outdated)).Reason,
                Is.EqualTo(CrescendarrEditionPreflightReason.TrackIdentityMismatch));
            ConfirmNoReleaseMutation();
        }

        [Test]
        public void a_rejected_candidate_for_another_edition_blocks_the_entire_batch()
        {
            var result = Check(Decision(), Decision(_alternate, approved: false));
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.CandidateEditionMismatch));
            ConfirmNoReleaseMutation();
        }

        [Test]
        public void enabling_stock_completed_download_handling_blocks_even_exact_edition()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(x => x.EnableCompletedDownloadHandling).Returns(true);
            var result = Check(Decision());
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.CompletedDownloadHandlingEnabled));
            ConfirmNoReleaseMutation();
        }

        [Test]
        public void invalid_previous_release_identity_and_empty_candidates_are_denied()
        {
            Assert.That(_gate.Check(_album.Id, GroupId, "not-a-guid", new[] { Decision() }).Reason,
                Is.EqualTo(CrescendarrEditionPreflightReason.InvalidExpectedIdentity));
            Assert.That(Check().Reason, Is.EqualTo(CrescendarrEditionPreflightReason.NoApprovedCandidates));
            ConfirmNoReleaseMutation();
        }

        [Test]
        public void repository_failure_is_not_permitted_to_enable_import()
        {
            // An explicit failing host query stands in for a stale/restarting DB.
            Mocker.GetMock<IConfigService>()
                .SetupGet(x => x.EnableCompletedDownloadHandling)
                .Throws(new IOException("SYNTHETIC: unavailable host config"));
            var result = Check(Decision());
            Assert.That(result.Reason, Is.EqualTo(CrescendarrEditionPreflightReason.HostReadFailed));
            Assert.That(result.EditionMatches, Is.False);
            ConfirmNoReleaseMutation();
        }
    }
}
