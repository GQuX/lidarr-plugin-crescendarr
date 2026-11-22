// Experimental native characterization fixture: NOT a production plugin or safe-import proof.
// The CI job copies this original test into a SHA-pinned, disposable Lidarr source checkout.
// It does not copy or modify anyone's media files and does not patch upstream on GitHub.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Specifications;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrAlbumUpgradeCharacterizationFixture : CoreTest<ImportApprovedTracks>
    {
        private const int AlbumTrackCount = 12;
        private const int ExpectedUpgrades = 11;

        private List<ImportDecision<LocalTrack>> _decisions;
        private List<TrackFile> _existingFiles;
        private string _protectedTrackPath;

        [SetUp]
        public void Setup()
        {
            var artist = Builder<Artist>.CreateNew()
                .With(a => a.QualityProfile = new QualityProfile
                {
                    Items = Qualities.QualityFixture.GetDefaultQualities()
                })
                .With(a => a.Path = @"C:\Test\Music\Crescendarr Fixture".AsOsAgnostic())
                .Build();
            var album = Builder<Album>.CreateNew()
                .With(a => a.Artist = artist)
                .Build();
            var release = Builder<AlbumRelease>.CreateNew()
                .With(r => r.AlbumId = album.Id)
                .With(r => r.Monitored = true)
                .With(r => r.TrackCount = AlbumTrackCount)
                .Build();
            album.AlbumReleases = new List<AlbumRelease> { release };

            _decisions = new List<ImportDecision<LocalTrack>>();
            _existingFiles = new List<TrackFile>();

            // A single existing FLAC on track 12 is superior to the incoming MP3_256.
            // Tracks 1-11 are MP3_192 and qualify for MP3_256 upgrades.
            var qualitySpec = new UpgradeSpecification(
                Mocker.GetMock<IConfigService>().Object,
                Mocker.GetMock<ICustomFormatCalculationService>().Object,
                LogManager.GetLogger("CrescendarrAlbumUpgradeCharacterizationFixture"));

            for (var i = 1; i <= AlbumTrackCount; i++)
            {
                var existingFile = new TrackFile
                {
                    Id = 1000 + i,
                    AlbumId = album.Id,
                    Path = Path.Combine(artist.Path, "existing-" + i.ToString("D2") + ".flac"),
                    Quality = new QualityModel(i == AlbumTrackCount ? Quality.FLAC : Quality.MP3_192)
                };
                _existingFiles.Add(existingFile);

                var track = new Track
                {
                    Id = 2000 + i,
                    AbsoluteTrackNumber = i,
                    TrackFileId = existingFile.Id,
                    TrackFile = new LazyLoaded<TrackFile>(existingFile)
                };
                var incoming = new LocalTrack
                {
                    Artist = artist,
                    Album = album,
                    Release = release,
                    Tracks = new List<Track> { track },
                    Path = Path.Combine(artist.Path, "incoming-" + i.ToString("D2") + ".mp3"),
                    Quality = new QualityModel(Quality.MP3_256),
                    FileTrackInfo = new ParsedTrackInfo { ReleaseGroup = "CRESCENDARR-TEST" }
                };

                var accept = qualitySpec.IsSatisfiedBy(incoming, null).Accepted;
                Assert.That(accept, Is.EqualTo(i != AlbumTrackCount),
                    "Native UpgradeSpecification should accept exactly the 11 upgrades.");
                _decisions.Add(accept
                    ? new ImportDecision<LocalTrack>(incoming)
                    : new ImportDecision<LocalTrack>(incoming, new Rejection("Incoming audio is inferior")));
            }

            _protectedTrackPath = _existingFiles.Last().Path;
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByAlbum(album.Id))
                .Returns(_existingFiles);
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.FileExists(It.IsAny<string>()))
                .Returns(true);
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(s => Path.GetDirectoryName(s));
            Mocker.GetMock<IUpgradeMediaFiles>()
                .Setup(s => s.UpgradeTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>(), It.IsAny<bool>()))
                .Returns(new TrackFileMoveResult());
        }

        [Test]
        public void automatic_replace_existing_true_predeletes_all_12_including_inferior_candidate()
        {
            Assert.That(_decisions.Count(d => d.Approved), Is.EqualTo(ExpectedUpgrades));
            Assert.That(_decisions.Count(d => !d.Approved), Is.EqualTo(1));

            var results = Subject.Import(_decisions, true);

            // This is a CHARACTERIZATION of current undesirable behavior, not a passing
            // safety requirement. It must be revisited when testing an upstream fix.
            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.GetFilesByAlbum(It.IsAny<int>()), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()),
                    Times.Exactly(AlbumTrackCount));
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(_protectedTrackPath, It.IsAny<string>()),
                    Times.Once(), "The existing better track 12 must not be pre-deleted in a fixed importer.");
            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.Delete(It.IsAny<TrackFile>(), DeleteMediaFileReason.Upgrade),
                    Times.Exactly(AlbumTrackCount));
            Mocker.GetMock<IUpgradeMediaFiles>()
                .Verify(s => s.UpgradeTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>(), It.IsAny<bool>()),
                    Times.Exactly(ExpectedUpgrades));
            Assert.That(results.Count, Is.EqualTo(AlbumTrackCount));
        }

        [Test]
        public void replace_existing_false_still_calls_set_monitored_on_release()
        {
            Subject.Import(_decisions, false);

            // No whole-album pre-delete does not imply an immutable edition.
            // Lidarr still invokes SetMonitored for the first approved release.
            Mocker.GetMock<IReleaseService>()
                .Verify(s => s.SetMonitored(It.IsAny<AlbumRelease>()), Times.Once());
        }

        [Test]
        public void replace_existing_false_avoids_album_wipe_but_still_delegates_11_upgrades()
        {
            var results = Subject.Import(_decisions, false);

            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.GetFilesByAlbum(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.Delete(It.IsAny<TrackFile>(), DeleteMediaFileReason.Upgrade),
                    Times.Never());

            // The upgrader is intentionally mocked. This does NOT prove that any original
            // survives the real UpgradeMediaFileService's per-track delete-before-move.
            Mocker.GetMock<IUpgradeMediaFiles>()
                .Verify(s => s.UpgradeTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>(), It.IsAny<bool>()),
                    Times.Exactly(ExpectedUpgrades));
            Assert.That(results.Count(r => r.Result == ImportResultType.Imported),
                Is.EqualTo(ExpectedUpgrades));
            Assert.That(results.Count(r => r.Result == ImportResultType.Rejected),
                Is.EqualTo(1));
        }

        [Test]
        public void late_trackfile_bulk_insert_failure_follows_all_11_selected_upgrades()
        {
            // Characterizes a crash/DB-error window, NOT a safe recovery protocol.
            // The upgrader is mocked: actual filesystem bytes and SQLite are not involved.
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.AddMany(It.IsAny<List<TrackFile>>()))
                .Throws(new IOException("SYNTHETIC: bulk TrackFile insert failed"));

            Assert.Throws<IOException>(() => Subject.Import(_decisions, false));

            Mocker.GetMock<IUpgradeMediaFiles>()
                .Verify(s => s.UpgradeTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>(), It.IsAny<bool>()),
                    Times.Exactly(ExpectedUpgrades));
            Mocker.GetMock<IReleaseService>()
                .Verify(s => s.SetMonitored(It.IsAny<AlbumRelease>()), Times.Once());
            Mocker.GetMock<ITrackService>()
                .Verify(s => s.SetFileIds(It.IsAny<List<Track>>()), Times.Never());
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<TrackImportedEvent>()), Times.Never());
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(_protectedTrackPath, It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void sixth_upgrade_failure_still_allows_ten_imports_and_one_skipped_approved_decision()
        {
            // Native ImportApprovedTracks catches the sixth upgrader fault and continues.
            // This proves control-flow only, not that the eleven originals remain intact.
            var calls = 0;
            Mocker.GetMock<IUpgradeMediaFiles>()
                .Setup(s => s.UpgradeTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>(), It.IsAny<bool>()))
                .Returns((TrackFile file, LocalTrack local, bool copyOnly) =>
                {
                    if (++calls == 6)
                    {
                        throw new IOException("SYNTHETIC: interrupted sixth media upgrade");
                    }

                    return new TrackFileMoveResult();
                });

            var results = Subject.Import(_decisions, false);

            Assert.That(calls, Is.EqualTo(ExpectedUpgrades));
            Assert.That(results.Count, Is.EqualTo(AlbumTrackCount));
            Assert.That(results.Count(r => r.Result == ImportResultType.Imported), Is.EqualTo(10));
            // Stock ImportResult reports an approved candidate with an import error as Skipped.
            Assert.That(results.Count(r => r.Result == ImportResultType.Skipped), Is.EqualTo(1));
            Assert.That(results.Count(r => r.ImportDecision.Approved && r.Errors.Count > 0), Is.EqualTo(1));
            Assert.That(results.Count(r => !r.ImportDecision.Approved), Is.EqualTo(1));

            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.AddMany(It.Is<List<TrackFile>>(files => files.Count == 10)), Times.Once());
            Mocker.GetMock<ITrackService>()
                .Verify(s => s.SetFileIds(It.Is<List<Track>>(tracks => tracks.Count == 10)), Times.Once());
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<TrackImportedEvent>()), Times.Exactly(10));
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(s => s.DeleteFile(_protectedTrackPath, It.IsAny<string>()), Times.Never());
        }

    }
}
