// ORIGINAL Crescendarr test fixture for an EPHEMERAL pinned Lidarr checkout.
// Uses only Moq; no music file, database, NAS, or user configuration is touched.
// Expected test passes document unsafe stock behavior, NOT a proposed fix.
using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrUpgradeFailureCharacterizationFixture : CoreTest<UpgradeMediaFileService>
    {
        private TrackFile _original;
        private TrackFile _incoming;
        private LocalTrack _candidate;

        [SetUp]
        public void Setup()
        {
            var artistPath = @"C:\Test\Music\Crescendarr Failure Fixture".AsOsAgnostic();
            var artist = new Artist { Path = artistPath };
            _original = new TrackFile
            {
                Id = 101,
                Path = Path.Combine(artistPath, "existing.flac")
            };
            _incoming = new TrackFile
            {
                Id = 102,
                Path = Path.Combine(artistPath, "candidate.flac")
            };
            var track = new Track
            {
                Id = 201,
                TrackFileId = _original.Id,
                TrackFile = new LazyLoaded<TrackFile>(_original)
            };
            _candidate = new LocalTrack
            {
                Artist = artist,
                Tracks = new List<Track> { track },
                Path = _incoming.Path
            };

            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FileExists(It.IsAny<string>()))
                .Returns(true);
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FolderExists(It.IsAny<string>()))
                .Returns(true);
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(s => Path.GetDirectoryName(s));
        }

        [Test]
        public void failed_move_happens_after_original_is_recycled_and_database_mapping_deleted()
        {
            var steps = new List<string>();

            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(s => s.DeleteFile(_original.Path, It.IsAny<string>()))
                .Callback(() => steps.Add("delete-original"));
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.Delete(_original, DeleteMediaFileReason.Upgrade))
                .Callback(() => steps.Add("delete-db-mapping"));
            Mocker.GetMock<IMoveTrackFiles>()
                .Setup(s => s.MoveTrackFile(_incoming, _candidate))
                .Callback(() => steps.Add("move-attempt"))
                .Throws(new IOException("SYNTHETIC: simulated NAS disconnection during move"));

            Assert.Throws<IOException>(() => Subject.UpgradeTrackFile(_incoming, _candidate));
            Assert.That(steps, Is.EqualTo(new[] { "delete-original", "delete-db-mapping", "move-attempt" }));
            Mocker.GetMock<IAudioTagService>()
                .Verify(s => s.WriteTags(It.IsAny<TrackFile>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void failed_recycle_does_not_delete_database_mapping_or_start_move()
        {
            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(s => s.DeleteFile(_original.Path, It.IsAny<string>()))
                .Throws(new IOException("SYNTHETIC: cannot recycle old file"));

            Assert.Throws<IOException>(() => Subject.UpgradeTrackFile(_incoming, _candidate));

            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.Delete(It.IsAny<TrackFile>(), It.IsAny<DeleteMediaFileReason>()),
                    Times.Never());
            Mocker.GetMock<IMoveTrackFiles>()
                .Verify(s => s.MoveTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>()),
                    Times.Never());
        }

        [Test]
        public void failed_copy_mode_still_recycles_and_unlinks_original_before_copy_attempt()
        {
            var steps = new List<string>();

            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(s => s.DeleteFile(_original.Path, It.IsAny<string>()))
                .Callback(() => steps.Add("delete-original"));
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.Delete(_original, DeleteMediaFileReason.Upgrade))
                .Callback(() => steps.Add("delete-db-mapping"));
            Mocker.GetMock<IMoveTrackFiles>()
                .Setup(s => s.CopyTrackFile(_incoming, _candidate))
                .Callback(() => steps.Add("copy-attempt"))
                .Throws(new IOException("SYNTHETIC: simulated NAS disconnection during copy"));

            Assert.Throws<IOException>(() => Subject.UpgradeTrackFile(_incoming, _candidate, copyOnly: true));
            Assert.That(steps, Is.EqualTo(new[] { "delete-original", "delete-db-mapping", "copy-attempt" }));
        }
    }
}
