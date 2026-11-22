// Original, read-only Lidarr native completion-state characterization.
// No downloader, filesystem import, or user media is involved.
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrCompletedDownloadCharacterizationFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _download;

        [SetUp]
        public void Setup()
        {
            var item = new DownloadClientItem
            {
                DownloadId = "CRESCENDARR-SYNTHETIC-ALBUM",
                Title = "Crescendarr Test Album",
                OutputPath = new OsPath(Path.Combine(Path.GetTempPath(), "crescendarr-synthetic-staging")),
                Status = DownloadItemStatus.Completed
            };
            var album = new Album
            {
                Id = 101,
                AlbumReleases = new List<AlbumRelease>
                {
                    new AlbumRelease { Monitored = true, TrackCount = 12 }
                }
            };
            _download = new TrackedDownload
            {
                DownloadItem = item,
                ImportItem = item,
                RemoteAlbum = new RemoteAlbum
                {
                    Artist = new Artist { Id = 44 },
                    Albums = new List<Album> { album }
                },
                State = TrackedDownloadState.Downloading
            };

            Mocker.GetMock<IProvideImportItemService>()
                .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                .Returns<DownloadClientItem, DownloadClientItem>((item, previous) => item);
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                .Returns(new List<EntityHistory>());
            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                .Setup(s => s.IsImported(It.IsAny<TrackedDownload>(), It.IsAny<List<EntityHistory>>()))
                .Returns(false);
        }

        private List<ImportResult> MakeResults(int imported, int deliberatelyKept)
        {
            var results = new List<ImportResult>();
            for (var i = 0; i < imported + deliberatelyKept; i++)
            {
                var track = new Track { Id = 700 + i };
                var local = new LocalTrack
                {
                    Path = Path.Combine(Path.GetTempPath(), "crescendarr-synthetic-track-" + i + ".flac"),
                    Tracks = new List<Track> { track }
                };
                results.Add(i < imported
                    ? new ImportResult(new ImportDecision<LocalTrack>(local))
                    : new ImportResult(new ImportDecision<LocalTrack>(
                        local,
                        new Rejection("Existing track is already better")),
                        "Existing track is already better"));
            }
            return results;
        }

        private void ImportResults(List<ImportResult> results)
        {
            Mocker.GetMock<IDownloadedTracksImportService>()
                .Setup(s => s.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Artist>(), It.IsAny<DownloadClientItem>()))
                .Returns(results);
            Subject.Import(_download);
        }

        [Test]
        public void empty_import_results_stay_pending_without_download_completed_event()
        {
            ImportResults(MakeResults(0, 0));
            Assert.That(_download.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void all_intentionally_kept_tracks_are_treated_as_failed_not_satisfied()
        {
            ImportResults(MakeResults(0, 12));
            Assert.That(_download.State, Is.EqualTo(TrackedDownloadState.ImportFailed));
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<AlbumImportIncompleteEvent>()), Times.Once());
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void eleven_upgrades_one_deliberately_kept_is_treated_as_failed_without_history()
        {
            ImportResults(MakeResults(11, 1));
            Assert.That(_download.State, Is.EqualTo(TrackedDownloadState.ImportFailed));
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<AlbumImportIncompleteEvent>()), Times.Once());
        }

        [Test]
        public void eleven_upgrades_one_deliberately_kept_can_be_marked_imported_by_history()
        {
            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                .Setup(s => s.IsImported(It.IsAny<TrackedDownload>(), It.IsAny<List<EntityHistory>>()))
                .Returns(true);

            ImportResults(MakeResults(11, 1));
            Assert.That(_download.State, Is.EqualTo(TrackedDownloadState.Imported));
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void twelve_imported_triggers_completion_with_no_partial_state()
        {
            ImportResults(MakeResults(12, 0));
            Assert.That(_download.State, Is.EqualTo(TrackedDownloadState.Imported));
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }
    }
}
