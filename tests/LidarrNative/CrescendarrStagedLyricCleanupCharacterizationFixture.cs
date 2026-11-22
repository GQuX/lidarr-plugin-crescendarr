// Original Crescendarr observation of stock Lidarr cleanup eligibility.
// Does not create or delete any directories; dependency interfaces are mocked.
using System;
using System.IO;
using System.IO.Abstractions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrStagedLyricCleanupCharacterizationFixture : CoreTest<DownloadedTracksImportService>
    {
        [Test]
        public void lyric_only_staging_folder_is_considered_safe_to_delete()
        {
            var folder = Path.Combine(Path.GetTempPath(), "crescendarr-synthetic-lyric-only");
            var lyric = Path.Combine(folder, "01-title.lrc");
            var directoryInfo = new Mock<IDirectoryInfo>();
            directoryInfo.SetupGet(s => s.FullName).Returns(folder);

            Mocker.GetMock<IDiskScanService>()
                .Setup(s => s.GetAudioFiles(folder, true))
                .Returns(Array.Empty<IFileInfo>());
            Mocker.GetMock<IDiskProvider>()
                .Setup(s => s.GetFiles(folder, true))
                .Returns(new[] { lyric });

            // ShouldDeleteFolder checks audio and large RARs, not lyrics.
            // The surrounding ProcessFolder's move-mode cleanup can recursively
            // delete that folder after an import succeeds.
            Assert.That(Subject.ShouldDeleteFolder(directoryInfo.Object, null), Is.True);
        }
    }
}
