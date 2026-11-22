// Incremental M12 integration characterization. Real stock importer, upgrader,
// media/release/track services, migrated SQLite and disposable physical bytes.
// Test-controlled disk transfer/recycle adapters; no tag reading or real download.
// NOT a safe import plugin, recovery proof, or valid audio-decoding test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Specifications;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrSqliteSyntheticMediaImportFixture : DbTest
    {
        private const int Total = 12;
        private const int Upgrades = 11;

        [Test]
        public void native_selective_import_relinks_real_sqlite_and_preserves_protected_bytes_and_lyrics()
        {
            var temp = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m12-disposable");
            var library = Path.Combine(temp, "library", "synthetic-artist", "synthetic-album");
            var staging = Path.Combine(temp, "staging");
            var recycle = Path.Combine(temp, "synthetic-recycle");
            var backups = Path.Combine(temp, "verified-copies");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(recycle);
            Directory.CreateDirectory(backups);

            // These are deliberately NON-AUDIO test payloads. The native tag reader
            // is not invoked in this intermediate test.
            var metadata = Builder<ArtistMetadata>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.Name = "Crescendarr Synthetic Artist")
                .With(x => x.ForeignArtistId = "b8e9bf54-3424-4691-8657-7b7e3ff902e5")
                .Build();
            Db.Insert(metadata);

            var profile = new QualityProfile
            {
                Name = "Crescendarr synthetic profile",
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.FLAC.Id
            };
            Db.Insert(profile);

            var artist = Builder<Artist>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.Path = Path.GetDirectoryName(library))
                .With(x => x.QualityProfileId = profile.Id)
                .With(x => x.QualityProfile = profile)
                .Build();
            Db.Insert(artist);

            var album = Builder<Album>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.ForeignAlbumId = "c5b4e91e-40fa-42c1-a25b-4f1220713587")
                .With(x => x.Artist = artist)
                .Build();
            Db.Insert(album);

            var release = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.AlbumId = album.Id)
                .With(x => x.ForeignReleaseId = "9fd3c58e-91db-4d89-86a7-e57558394c1f")
                .With(x => x.TrackCount = Total)
                .With(x => x.Monitored = true)
                .Build();
            Db.Insert(release);
            release.Album = album;
            album.AlbumReleases = new List<AlbumRelease> { release };

            var decisions = new List<ImportDecision<LocalTrack>>();
            var previous = new Dictionary<int, TrackFile>();
            var physicalPaths = new Dictionary<int, string>();
            var oldBytes = new Dictionary<int, byte[]>();
            var incomingBytes = new Dictionary<int, byte[]>();
            var lyricPaths = new Dictionary<int, string>();
            var lyricBytes = new Dictionary<int, byte[]>();
            var lyricIds = new Dictionary<int, int>();

            var nativeQuality = new UpgradeSpecification(
                Mocker.GetMock<IConfigService>().Object,
                Mocker.GetMock<ICustomFormatCalculationService>().Object,
                NLog.LogManager.GetLogger("CrescendarrSqliteSyntheticMediaImportFixture"));

            var extraRepository = Mocker.Resolve<ExtraFileRepository<LyricFile>>();
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(extraRepository);
            var lyrics = Mocker.Resolve<LyricFileService>();

            for (var i = 1; i <= Total; i++)
            {
                var extension = i == Total ? ".flac" : ".mp3";
                var stem = i.ToString("D2") + "-synthetic";
                var ownedPath = Path.Combine(library, stem + extension);
                var incomingPath = Path.Combine(staging, stem + ".mp3");
                var lyricPath = Path.Combine(library, stem + ".lrc");

                oldBytes[i] = Encoding.UTF8.GetBytes("OWNED-OPAQUE-SYNTHETIC-FILE-" + i);
                incomingBytes[i] = Encoding.UTF8.GetBytes("INCOMING-OPAQUE-SYNTHETIC-FILE-" + i);
                lyricBytes[i] = Encoding.UTF8.GetBytes("[00:00.00] SYNTHETIC LYRIC " + i);
                File.WriteAllBytes(ownedPath, oldBytes[i]);
                File.WriteAllBytes(incomingPath, incomingBytes[i]);
                File.WriteAllBytes(lyricPath, lyricBytes[i]);
                File.Copy(ownedPath, Path.Combine(backups, Path.GetFileName(ownedPath)));

                Assert.That(File.ReadAllBytes(Path.Combine(backups, Path.GetFileName(ownedPath))),
                    Is.EqualTo(oldBytes[i]), "Recovery copies must be checked before importing.");

                physicalPaths[i] = ownedPath;
                lyricPaths[i] = lyricPath;

                var old = new TrackFile
                {
                    Path = ownedPath,
                    AlbumId = album.Id,
                    Size = oldBytes[i].LongLength,
                    Modified = File.GetLastWriteTimeUtc(ownedPath),
                    Quality = new QualityModel(i == Total ? Quality.FLAC : Quality.MP3_192)
                };
                Db.Insert(old);
                previous[i] = old;

                var track = Builder<Track>.CreateNew()
                    .With(x => x.Id = 0)
                    .With(x => x.ArtistMetadataId = metadata.Id)
                    .With(x => x.AlbumReleaseId = release.Id)
                    .With(x => x.TrackFileId = old.Id)
                    .With(x => x.AbsoluteTrackNumber = i)
                    .With(x => x.ForeignTrackId = "crescendarr-track-" + i)
                    .With(x => x.ForeignRecordingId = "crescendarr-recording-" + i)
                    .With(x => x.TrackFile = new LazyLoaded<TrackFile>(old))
                    .Build();
                Db.Insert(track);

                var lyric = new LyricFile
                {
                    ArtistId = artist.Id,
                    AlbumId = album.Id,
                    TrackFileId = old.Id,
                    RelativePath = Path.Combine("synthetic-album", stem + ".lrc"),
                    Extension = ".lrc"
                };
                lyrics.Upsert(lyric);
                lyricIds[i] = Db.All<LyricFile>().Single(x => x.TrackFileId == old.Id).Id;

                var local = new LocalTrack
                {
                    Artist = artist,
                    Album = album,
                    Release = release,
                    Tracks = new List<Track> { track },
                    Path = incomingPath,
                    Size = incomingBytes[i].LongLength,
                    Modified = File.GetLastWriteTimeUtc(incomingPath),
                    Quality = new QualityModel(Quality.MP3_256),
                    FileTrackInfo = new ParsedTrackInfo { ReleaseGroup = "CRESCENDARR-SYNTHETIC" }
                };
                var accepted = nativeQuality.IsSatisfiedBy(local, null).Accepted;
                Assert.That(accepted, Is.EqualTo(i != Total),
                    "Stock quality specification should approve 11 upgrades, reject the FLAC.");
                decisions.Add(accepted
                    ? new ImportDecision<LocalTrack>(local)
                    : new ImportDecision<LocalTrack>(local, new Rejection("Inferior to owned FLAC")));
            }

            // The native media, track and monitored-release writes below use the real
            // migrated SQLite repositories. Only low-level file interfaces are adapted.
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<ITrackRepository>(Mocker.Resolve<TrackRepository>());
            Mocker.SetConstant<IReleaseRepository>(Mocker.Resolve<ReleaseRepository>());
            Mocker.SetConstant<IMediaFileService>(Mocker.Resolve<MediaFileService>());
            Mocker.SetConstant<ITrackService>(Mocker.Resolve<TrackService>());
            Mocker.SetConstant<IReleaseService>(Mocker.Resolve<ReleaseService>());

            Mocker.GetMock<IDiskProvider>()
                .Setup(x => x.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(Path.GetDirectoryName);
            Mocker.GetMock<IDiskProvider>()
                .Setup(x => x.FileExists(It.IsAny<string>()))
                .Returns<string>(File.Exists);
            Mocker.GetMock<IDiskProvider>()
                .Setup(x => x.FolderExists(It.IsAny<string>()))
                .Returns<string>(Directory.Exists);

            // Real disposable byte moves; these delegates DO NOT test the stock
            // RecycleBinProvider/TrackFileMovingService implementations.
            Mocker.GetMock<IRecycleBinProvider>()
                .Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((path, subfolder) =>
                    File.Move(path, Path.Combine(recycle, Path.GetFileName(path))));
            Mocker.GetMock<IMoveTrackFiles>()
                .Setup(x => x.MoveTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>()))
                .Returns((TrackFile file, LocalTrack local) =>
                {
                    var trackNumber = local.Tracks.Single().AbsoluteTrackNumber;
                    var destination = physicalPaths[trackNumber];
                    File.Move(file.Path, destination);
                    file.Path = destination;
                    return file;
                });

            Mocker.GetMock<IAlbumService>()
                .Setup(x => x.GetAlbum(album.Id))
                .Returns(album);
            Mocker.SetConstant<IUpgradeMediaFiles>(Mocker.Resolve<UpgradeMediaFileService>());

            // Protect registered lyrics by removing only the DB association before
            // stock UpgradeMediaFileService can emit TrackFileDeletedEvent.
            // The physical lyric bytes remain in the disposable library.
            for (var i = 1; i <= Upgrades; i++)
            {
                lyrics.Delete(lyricIds[i]);
                Assert.That(File.ReadAllBytes(lyricPaths[i]), Is.EqualTo(lyricBytes[i]));
            }

            var importer = Mocker.Resolve<ImportApprovedTracks>();
            var results = importer.Import(decisions, false, importMode: ImportMode.Move);

            Assert.That(results.Count(x => x.Result == ImportResultType.Imported), Is.EqualTo(Upgrades));
            Assert.That(results.Count(x => x.Result == ImportResultType.Rejected), Is.EqualTo(1));
            Assert.That(Db.All<TrackFile>().Count, Is.EqualTo(Total));

            var storedTracks = Db.All<Track>().ToDictionary(x => x.AbsoluteTrackNumber);
            var storedFiles = Db.All<TrackFile>().ToDictionary(x => x.Id);
            for (var i = 1; i <= Total; i++)
            {
                Assert.That(File.ReadAllBytes(lyricPaths[i]), Is.EqualTo(lyricBytes[i]));
                Assert.That(File.ReadAllBytes(Path.Combine(backups, Path.GetFileName(physicalPaths[i]))),
                    Is.EqualTo(oldBytes[i]));
                if (i == Total)
                {
                    Assert.That(File.ReadAllBytes(physicalPaths[i]), Is.EqualTo(oldBytes[i]));
                    Assert.That(storedTracks[i].TrackFileId, Is.EqualTo(previous[i].Id));
                    Assert.That(Db.All<LyricFile>().Single(x => x.TrackFileId == previous[i].Id).Id,
                        Is.EqualTo(lyricIds[i]));
                    Assert.That(File.Exists(Path.Combine(staging, "12-synthetic.mp3")), Is.True);
                }
                else
                {
                    Assert.That(File.ReadAllBytes(physicalPaths[i]), Is.EqualTo(incomingBytes[i]));
                    Assert.That(File.ReadAllBytes(Path.Combine(recycle, Path.GetFileName(physicalPaths[i]))),
                        Is.EqualTo(oldBytes[i]));
                    Assert.That(storedTracks[i].TrackFileId, Is.Not.EqualTo(previous[i].Id));
                    Assert.That(storedFiles.ContainsKey(previous[i].Id), Is.False);
                    Assert.That(storedFiles.ContainsKey(storedTracks[i].TrackFileId), Is.True);
                    Assert.That(Db.All<LyricFile>().Any(x => x.TrackFileId == previous[i].Id), Is.False);
                    lyrics.Upsert(new LyricFile
                    {
                        Id = 0,
                        ArtistId = artist.Id,
                        AlbumId = album.Id,
                        TrackFileId = storedTracks[i].TrackFileId,
                        RelativePath = Path.Combine("synthetic-album", i.ToString("D2") + "-synthetic.lrc"),
                        Extension = ".lrc"
                    });
                }
            }

            Assert.That(Db.All<LyricFile>().Count, Is.EqualTo(Total));
            Assert.That(Db.All<AlbumRelease>().Single(x => x.Id == release.Id).Monitored, Is.True);
            Assert.That(Db.All<Track>().All(x => storedFiles.ContainsKey(x.TrackFileId)), Is.True);
            Mocker.GetMock<IRecycleBinProvider>()
                .Verify(x => x.DeleteFile(physicalPaths[Total], It.IsAny<string>()), Times.Never());
        }
    }
}
