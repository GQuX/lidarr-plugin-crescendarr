// Native Lidarr v3.1.6.5079 behavior characterization with mocked downloader.
// No real music, downloaded files or production host is accessed.
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
    public class CrescendarrCompletedDownloadHandlingBoundaryFixture : CoreTest<DownloadProcessingService>
    {
        private TrackedDownload _pending;

        [SetUp]
        public void Setup()
        {
            _pending = new TrackedDownload
            {
                IsTrackable = true,
                State = TrackedDownloadState.ImportPending,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "CRESCENDARR-SYNTHETIC",
                    Title = "Synthetic completed album",
                    Status = DownloadItemStatus.Completed,
                    CanBeRemoved = false
                }
            };

            Mocker.GetMock<ITrackedDownloadService>()
                .Setup(s => s.GetTrackedDownloads())
                .Returns(new List<TrackedDownload> { _pending });
        }

        [Test]
        public void stock_processing_does_not_import_pending_item_when_cdh_disabled()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.EnableCompletedDownloadHandling).Returns(false);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>()
                .Verify(s => s.Import(It.IsAny<TrackedDownload>()), Times.Never());
            Assert.That(_pending.State, Is.EqualTo(TrackedDownloadState.ImportPending));
        }

        [Test]
        public void stock_processing_imports_pending_item_when_cdh_enabled()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.EnableCompletedDownloadHandling).Returns(true);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>()
                .Verify(s => s.Import(_pending), Times.Once());
        }

        [Test]
        public void cdh_disabled_does_not_disable_cleanup_notifications_for_already_imported_items()
        {
            // Crucial qualification: the cleanup path runs regardless of CDH setting
            // when an item is already Imported and the download client allows removal.
            _pending.State = TrackedDownloadState.Imported;
            _pending.DownloadItem.CanBeRemoved = true;

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.EnableCompletedDownloadHandling).Returns(false);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>()
                .Verify(s => s.Import(It.IsAny<TrackedDownload>()), Times.Never());
            Mocker.GetMock<IEventAggregator>()
                .Verify(s => s.PublishEvent(It.IsAny<DownloadCanBeRemovedEvent>()), Times.Once());
        }
    }
}
