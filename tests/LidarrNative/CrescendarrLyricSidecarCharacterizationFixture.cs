// Original, read-only Lidarr native behavior characterization for Crescendarr.
// All filesystem, extra-file repository and recycle-bin effects are mocked.
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrLyricSidecarCharacterizationFixture : CoreTest<LyricFileService>
    {
        private Artist _artist;
        private TrackFile _trackFile;
        private LyricFile _linkedLyric;
        private string _absoluteLyricPath;

        [SetUp]
        public void Setup()
        {
            var artistPath = Path.Combine(Path.GetTempPath(), "crescendarr-synthetic", "artist");
            _artist = new Artist { Id = 42, Path = artistPath };
            _trackFile = new TrackFile
            {
                Id = 51,
                Artist = new LazyLoaded<Artist>(_artist)
            };
            _linkedLyric = new LyricFile
            {
                Id = 61,
                ArtistId = _artist.Id,
                TrackFileId = _trackFile.Id,
                RelativePath = Path.Combine("album", "01-title.lrc"),
                Extension = ".lrc"
            };
            _absoluteLyricPath = Path.Combine(_artist.Path, _linkedLyric.RelativePath);

            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Setup(s => s.GetFilesByTrackFile(_trackFile.Id))
                .Returns(new List<LyricFile> { _linkedLyric });
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(Path.GetDirectoryName);
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FileExists(_absoluteLyricPath))
                .Returns(true);
        }

        [Test]
        public void deleting_linked_lyric_record_uses_repository_delete_without_recycling_physical_lrc()
        {
            Subject.Delete(_linkedLyric.Id);

            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.Delete(_linkedLyric.Id), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IDiskProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void upsert_after_delete_with_old_lyric_id_attempts_update_not_insert()
        {
            // Important crash-recovery trap: a deleted row cannot simply be
            // recreated with its old nonzero ID through this native Upsert API.
            Subject.Delete(_linkedLyric.Id);
            Subject.Upsert(_linkedLyric);

            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.Delete(_linkedLyric.Id), Times.Once());
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.UpdateMany(It.Is<List<LyricFile>>(rows =>
                    rows.Count == 1 && rows[0].Id == _linkedLyric.Id)), Times.Once());
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.InsertMany(It.IsAny<List<LyricFile>>()), Times.Never());
        }

        [Test]
        public void upsert_relinked_lyric_with_zero_id_uses_insert_and_new_track_file_association()
        {
            var relink = new LyricFile
            {
                Id = 0,
                ArtistId = _artist.Id,
                TrackFileId = 152,
                RelativePath = _linkedLyric.RelativePath,
                Extension = ".lrc"
            };

            Subject.Upsert(relink);

            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.InsertMany(It.Is<List<LyricFile>>(rows =>
                    rows.Count == 1 && rows[0].Id == 0 &&
                    rows[0].TrackFileId == 152)), Times.Once());
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.UpdateMany(It.IsAny<List<LyricFile>>()), Times.Never());
        }

        [Test]
        public void native_delete_event_cannot_recycle_previously_unlinked_lyric_row()
        {
            // Simulate committed unlink before an Upgrade event, not a race.
            // Real concurrent scans and event ordering remain untested.
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Setup(s => s.GetFilesByTrackFile(_trackFile.Id))
                .Returns(new List<LyricFile>());

            Subject.Handle(new TrackFileDeletedEvent(_trackFile, DeleteMediaFileReason.Upgrade));

            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.DeleteForTrackFile(_trackFile.Id), Times.Once());
        }

        [Test]
        public void lrc_is_supported_for_existing_lyrics_discovery()
        {
            Assert.That(LyricFileExtensions.Extensions, Does.Contain(".lrc"));
        }

        [Test]
        public void upgrading_track_file_recycles_registered_lrc_before_unlinking_lyric_row()
        {
            var steps = new List<string>();
            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(s => s.DeleteFile(_absoluteLyricPath, It.IsAny<string>()))
                .Callback(() => steps.Add("recycle-registered-lyric"));
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Setup(s => s.DeleteForTrackFile(_trackFile.Id))
                .Callback(() => steps.Add("unlink-lyric-record"));

            Subject.Handle(new TrackFileDeletedEvent(_trackFile, DeleteMediaFileReason.Upgrade));

            Assert.That(steps, Is.EqualTo(new[] { "recycle-registered-lyric", "unlink-lyric-record" }));
        }

        [Test]
        public void missing_from_disk_scan_cleanup_recycles_registered_lrc_and_unlinks_row()
        {
            // The stock media table scan uses DeleteMediaFileReason.MissingFromDisk.
            // A scan during an unfinished swap can trigger this path.
            var steps = new List<string>();
            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(s => s.DeleteFile(_absoluteLyricPath, It.IsAny<string>()))
                .Callback(() => steps.Add("recycle-registered-lyric"));
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Setup(s => s.DeleteForTrackFile(_trackFile.Id))
                .Callback(() => steps.Add("unlink-lyric-record"));

            Subject.Handle(new TrackFileDeletedEvent(_trackFile, DeleteMediaFileReason.MissingFromDisk));

            Assert.That(steps, Is.EqualTo(new[] { "recycle-registered-lyric", "unlink-lyric-record" }));
        }

        [Test]
        public void no_linked_tracks_cleanup_does_not_recycle_lrc_but_unlinks_database_row()
        {
            Subject.Handle(new TrackFileDeletedEvent(_trackFile, DeleteMediaFileReason.NoLinkedEpisodes));

            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.DeleteForTrackFile(_trackFile.Id), Times.Once());
        }

        [Test]
        public void missing_registered_lyric_file_still_has_database_link_removed()
        {
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FileExists(_absoluteLyricPath))
                .Returns(false);

            Subject.Handle(new TrackFileDeletedEvent(_trackFile, DeleteMediaFileReason.Upgrade));

            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IExtraFileRepository<LyricFile>>()
                .Verify(s => s.DeleteForTrackFile(_trackFile.Id), Times.Once());
        }
    }
}
