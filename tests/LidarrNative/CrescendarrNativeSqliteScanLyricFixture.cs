// Source-pinned stock Lidarr scan-cleanup and lyric deletion characterization.
// Uses native migrated SQLite, real synthetic .lrc bytes and native services.
// Explicitly invokes the native event handler; the event bus remains mocked.
// Not a real full disk scan, plugin executor or production-safe recovery.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrNativeSqliteScanLyricFixture : DbTest
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void missing_file_cleanup_detaches_real_track_and_registered_lyrics_need_early_unlink(bool unlinkBeforeScan, bool useRealEventAggregator)
        {
            var temp = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m12-native-scan");
            var artistPath = Path.Combine(temp, "music", "synthetic-artist");
            var albumPath = Path.Combine(artistPath, "synthetic-album");
            var backupPath = Path.Combine(temp, "verified-outside-scan");
            var recyclePath = Path.Combine(temp, "synthetic-recycle");
            Directory.CreateDirectory(albumPath);
            Directory.CreateDirectory(backupPath);
            Directory.CreateDirectory(recyclePath);

            var originalPath = Path.Combine(albumPath, "01-synthetic.mp3");
            var lyricPath = Path.Combine(albumPath, "01-synthetic.lrc");
            var retainedOriginal = Path.Combine(backupPath, "01-synthetic.mp3");
            var recycledLyric = Path.Combine(recyclePath, "01-synthetic.lrc");
            var originalBytes = Encoding.UTF8.GetBytes("M12-SCAN-OPAQUE-ORIGINAL");
            var lyricBytes = Encoding.UTF8.GetBytes("[00:00.00] M12 SYNTHETIC LYRIC");
            File.WriteAllBytes(originalPath, originalBytes);
            File.WriteAllBytes(lyricPath, lyricBytes);

            var metadata = Builder<ArtistMetadata>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.Name = "Crescendarr Scan Test Artist")
                .With(x => x.ForeignArtistId = "b1656975-a5c7-4d32-9ad8-6839bd992e4c")
                .Build();
            Db.Insert(metadata);
            var artist = Builder<Artist>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.Path = artistPath)
                .Build();
            Db.Insert(artist);
            var album = Builder<Album>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.ForeignAlbumId = "e3894c97-d730-42aa-b57e-f83d116a2997")
                .Build();
            Db.Insert(album);
            var release = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.AlbumId = album.Id)
                .With(x => x.Monitored = true)
                .Build();
            Db.Insert(release);

            var file = new TrackFile
            {
                Path = originalPath,
                AlbumId = album.Id,
                Size = originalBytes.LongLength,
                Modified = File.GetLastWriteTimeUtc(originalPath),
                DateAdded = DateTime.UtcNow,
                Quality = new QualityModel(Quality.MP3_192),
                Artist = new LazyLoaded<Artist>(artist)
            };
            Db.Insert(file);
            var track = Builder<Track>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.AlbumReleaseId = release.Id)
                .With(x => x.TrackFileId = file.Id)
                .With(x => x.AbsoluteTrackNumber = 1)
                .Build();
            Db.Insert(track);

            // Actual SQLite-backed service types, not a fabricated mock DB.
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<ITrackRepository>(Mocker.Resolve<TrackRepository>());
            var realTrackService = Mocker.Resolve<TrackService>();
            Mocker.SetConstant<ITrackService>(realTrackService);
            var extraRepository = Mocker.Resolve<ExtraFileRepository<LyricFile>>();
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(extraRepository);
            var lyrics = Mocker.Resolve<LyricFileService>();

            if (useRealEventAggregator)
            {
                // Register the ACTUAL synchronous TrackService and LyricFileService
                // handlers with the unmodified stock EventAggregator. No manual
                // Handle(...) call will occur in this branch.
                Mocker.GetMock<IServiceFactory>()
                    .Setup(x => x.BuildAll<IHandle<TrackFileDeletedEvent>>())
                    .Returns(new List<IHandle<TrackFileDeletedEvent>>
                    {
                        realTrackService,
                        lyrics
                    });
                var aggregator = Mocker.Resolve<EventAggregator>();
                Mocker.SetConstant<IEventAggregator>(aggregator);
            }

            // The actual native media service now publishes TrackFileDeletedEvent
            // through either the test's old mock or the real event dispatcher.
            Mocker.SetConstant<IMediaFileService>(Mocker.Resolve<MediaFileService>());

            var lyric = new LyricFile
            {
                Id = 0,
                ArtistId = artist.Id,
                AlbumId = album.Id,
                TrackFileId = file.Id,
                RelativePath = Path.Combine("synthetic-album", "01-synthetic.lrc"),
                Extension = ".lrc"
            };
            lyrics.Upsert(lyric);
            var originalLyric = Db.All<LyricFile>().Single();
            if (unlinkBeforeScan)
            {
                lyrics.Delete(originalLyric.Id);
                Assert.That(File.ReadAllBytes(lyricPath), Is.EqualTo(lyricBytes));
                Assert.That(Db.All<LyricFile>(), Is.Empty);
            }

            Mocker.GetMock<IDiskProvider>()
                .Setup(x => x.FileExists(It.IsAny<string>()))
                .Returns<string>(File.Exists);
            Mocker.GetMock<IDiskProvider>()
                .Setup(x => x.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(Path.GetDirectoryName);
            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((path, subfolder) =>
                    File.Move(path, Path.Combine(recyclePath, Path.GetFileName(path))));

            // Outside-artist verified byte copy approximates a temporarily
            // missing destination. Stock scan-cleanup sees the DB path absent.
            File.Move(originalPath, retainedOriginal);
            Assert.That(File.ReadAllBytes(retainedOriginal), Is.EqualTo(originalBytes));
            Assert.That(File.Exists(originalPath), Is.False);

            // The REAL stock media table cleanup uses real media, track and
            // lyric repositories against migrated SQLite. With real dispatch,
            // published TrackFileDeletedEvent reaches native synchronous handlers
            // automatically. The fallback path keeps our prior direct-handler
            // characterization intact as a cross-check.
            Mocker.Resolve<MediaFileTableCleanupService>()
                .Clean(artistPath, new List<string>());
            if (!useRealEventAggregator)
            {
                lyrics.Handle(new TrackFileDeletedEvent(file, DeleteMediaFileReason.MissingFromDisk));
            }

            Assert.That(Db.All<TrackFile>(), Is.Empty);
            Assert.That(Db.All<Track>().Single().TrackFileId, Is.EqualTo(0),
                "Stock cleanup detaches the track when a known audio path disappears.");
            Assert.That(Db.All<LyricFile>(), Is.Empty);
            Assert.That(File.ReadAllBytes(retainedOriginal), Is.EqualTo(originalBytes));

            if (unlinkBeforeScan)
            {
                Assert.That(File.ReadAllBytes(lyricPath), Is.EqualTo(lyricBytes));
                Assert.That(File.Exists(recycledLyric), Is.False,
                    "A lyric row explicitly unlinked before scan is invisible to the native delete handler.");
                Mocker.GetMock<IRecycleBinProvider>()
                    .Verify(x => x.DeleteFile(lyricPath, It.IsAny<string>()), Times.Never());
            }
            else
            {
                Assert.That(File.Exists(lyricPath), Is.False,
                    "A still-registered lyric is recycled by the actual native handler.");
                Assert.That(File.ReadAllBytes(recycledLyric), Is.EqualTo(lyricBytes));
                Mocker.GetMock<IRecycleBinProvider>()
                    .Verify(x => x.DeleteFile(lyricPath, It.IsAny<string>()), Times.Once());
            }
        }
    }
}
