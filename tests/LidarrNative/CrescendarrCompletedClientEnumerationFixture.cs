// Source-pinned stock Lidarr characterization: CDH-disabled monitoring can still
// enumerate a completed item from Lidarr's own configured download-client abstraction.
// Uses Moq only; no actual downloader, queue, disk, or production files.
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class CrescendarrCompletedClientEnumerationFixture : CoreTest<DownloadMonitoringService>
    {
        private Mock<IDownloadClient> _client;
        private DownloadClientItem _completed;
        private TrackedDownload _tracked;

        [SetUp]
        public void Setup()
        {
            var definition = new DownloadClientDefinition { Id = 42, Name = "Synthetic Lidarr client" };
            _completed = new DownloadClientItem
            {
                DownloadId = "CRESCENDARR-DOWNLOAD-001",
                Title = "Synthetic completed album",
                Status = DownloadItemStatus.Completed
            };
            _tracked = new TrackedDownload
            {
                IsTrackable = true,
                DownloadItem = _completed,
                State = TrackedDownloadState.ImportPending
            };

            _client = new Mock<IDownloadClient>();
            _client.SetupGet(x => x.Definition).Returns(definition);
            _client.Setup(x => x.GetItems()).Returns(new List<DownloadClientItem> { _completed });

            Mocker.GetMock<IDownloadClientFactory>()
                .Setup(x => x.DownloadHandlingEnabled(It.IsAny<bool>()))
                .Returns(new List<IDownloadClient> { _client.Object });
            Mocker.GetMock<ITrackedDownloadService>()
                .Setup(x => x.TrackDownload(It.IsAny<DownloadClientDefinition>(), _completed))
                .Returns(_tracked);
        }

        [Test]
        public void cdh_off_still_reads_completed_items_from_configured_lidarr_download_client()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(x => x.EnableCompletedDownloadHandling).Returns(false);

            Subject.Execute(new RefreshMonitoredDownloadsCommand());

            _client.Verify(x => x.GetItems(), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>()
                .Verify(x => x.TrackDownload(It.Is<DownloadClientDefinition>(d => d.Id == 42), _completed),
                    Times.Once());

            // Lidarr itself then excludes complete downloads from its tracked/queue list.
            Mocker.GetMock<ITrackedDownloadService>()
                .Verify(x => x.UpdateTrackable(It.Is<List<TrackedDownload>>(items => items.Count == 0)),
                    Times.Once());
            Mocker.GetMock<IEventAggregator>()
                .Verify(x => x.PublishEvent(It.Is<TrackedDownloadRefreshedEvent>(
                    message => message.TrackedDownloads.Count == 0)), Times.Once());
        }

        [Test]
        public void cdh_on_retains_same_completed_item_in_stock_tracked_queue()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(x => x.EnableCompletedDownloadHandling).Returns(true);

            Subject.Execute(new RefreshMonitoredDownloadsCommand());

            _client.Verify(x => x.GetItems(), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>()
                .Verify(x => x.UpdateTrackable(It.Is<List<TrackedDownload>>(
                    items => items.Count == 1 && items[0].DownloadItem.DownloadId == _completed.DownloadId)),
                    Times.Once());
        }
    }
}
