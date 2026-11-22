// Source-pinned native scan cleanup characterization using synthetic DB records.
// It documents a scan-vs-replacement race; no physical filesystem is used.
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrRescanCleanupCharacterizationFixture : CoreTest<MediaFileTableCleanupService>
    {
        [Test]
        public void missing_original_path_deletes_trackfile_with_missing_from_disk_reason()
        {
            var artistDir = "/crescendarr-disposable-only/artist";
            var existing = new TrackFile
            {
                Id = 101,
                AlbumId = 22,
                Path = artistDir + "/album/original.flac"
            };
            var track = new Track { Id = 201, TrackFileId = existing.Id };

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesWithBasePath(artistDir))
                .Returns(new List<TrackFile> { existing });
            Mocker.GetMock<ITrackService>()
                .Setup(s => s.GetTracksByFileId(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Track> { track });

            // The original path temporarily disappears when renamed to a
            // protected recovery location. A normal scan sees an empty inventory.
            Subject.Clean(artistDir, new List<string>());

            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.DeleteMany(
                    It.Is<List<TrackFile>>(files => files.Count == 1 && files[0].Id == existing.Id),
                    DeleteMediaFileReason.MissingFromDisk), Times.Once());
            Assert.That(track.TrackFileId, Is.EqualTo(0));
            Mocker.GetMock<ITrackService>()
                .Verify(s => s.SetFileIds(It.Is<List<Track>>(tracks => tracks.Count == 1 && tracks[0].Id == track.Id)),
                    Times.Once());
        }
    }
}
