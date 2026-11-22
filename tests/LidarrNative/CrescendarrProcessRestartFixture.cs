// Second process of the isolated Crescendarr M12 crash-window experiment.
// Reopens a stock Lidarr migrated SQLite snapshot produced by a previous
// dotnet test process, then repairs a missing sixth track in a disposable root.
// This is NOT an actual Lidarr server restart, scan, SMB or transaction journal.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrProcessRestartFixture : DbTest
    {
        [Test]
        public void separate_test_process_reopens_interrupted_sqlite_and_repairs_from_verified_copies()
        {
            Assert.That(Environment.GetEnvironmentVariable("CRESCENDARR_M12_PHASE"),
                Is.EqualTo("recover"), "Must be run only by the explicit second CI process.");
            var root = Environment.GetEnvironmentVariable("CRESCENDARR_M12_RESTART_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            Assert.That(Path.IsPathFullyQualified(root), Is.True);
            Assert.That(Path.GetFileName(root).StartsWith("crescendarr-m12-restart-"), Is.True);

            var sourceDb = Path.Combine(root, "interrupted-lidarr.db");
            var marker = Path.Combine(root, "ready.marker");
            var shaFile = Path.Combine(root, "sixth-original.sha256");
            Assert.That(File.ReadAllText(marker), Is.EqualTo("M12_INTERRUPTED_AT_SIX"));
            Assert.That(File.Exists(sourceDb), Is.True);

            // The new NUnit process has already migrated a fresh disposable
            // database. Replace only THAT TEST database before querying it.
            // No persistent host config or user library is ever opened.
            SQLiteConnection.ClearAllPools();
            File.Copy(sourceDb, TestFolderInfo.GetDatabase(), overwrite: true);
            SQLiteConnection.ClearAllPools();

            var tracks = Db.All<Track>().ToDictionary(x => x.AbsoluteTrackNumber);
            var files = Db.All<TrackFile>().ToDictionary(x => x.Id);
            var album = Db.All<Album>().Single();
            var artist = Db.All<Artist>().Single();
            var release = Db.All<AlbumRelease>().Single(x => x.Monitored);
            Assert.That(tracks.Count, Is.EqualTo(12));
            Assert.That(files.Count, Is.EqualTo(11));
            Assert.That(release.AlbumId, Is.EqualTo(album.Id));

            var lib = Path.Combine(root, "owned", "library", "synthetic-artist", "synthetic-album");
            var stage = Path.Combine(root, "owned", "staging");
            var backupDir = Path.Combine(root, "owned", "verified-copies");
            var recycleDir = Path.Combine(root, "owned", "synthetic-recycle");
            var sixthPath = Path.Combine(lib, "06-synthetic.mp3");
            var sixthBackup = Path.Combine(backupDir, "06-synthetic.mp3");
            var orphan = tracks[6];

            // Verify the intermediate state survived across processes:
            // no original destination, no matching TrackFile row, still
            // a stale Track.TrackFileId because the event bus was mocked.
            Assert.That(files.ContainsKey(orphan.TrackFileId), Is.False);
            Assert.That(File.Exists(sixthPath), Is.False);
            Assert.That(File.Exists(Path.Combine(stage, "06-synthetic.mp3")), Is.True);
            Assert.That(Db.All<LyricFile>().Any(x => x.TrackFileId == orphan.TrackFileId),
                Is.False);
            var oldBytes = Encoding.UTF8.GetBytes("OWNED-OPAQUE-SYNTHETIC-FILE-6");
            Assert.That(File.ReadAllBytes(sixthBackup), Is.EqualTo(oldBytes));
            Assert.That(File.ReadAllBytes(Path.Combine(recycleDir, "06-synthetic.mp3")),
                Is.EqualTo(oldBytes));
            Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sixthBackup))),
                Is.EqualTo(File.ReadAllText(shaFile)));

            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<ITrackRepository>(Mocker.Resolve<TrackRepository>());
            Mocker.SetConstant<IMediaFileService>(Mocker.Resolve<MediaFileService>());
            Mocker.SetConstant<ITrackService>(Mocker.Resolve<TrackService>());
            var extrasRepository = Mocker.Resolve<ExtraFileRepository<LyricFile>>();
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(extrasRepository);
            var lyrics = Mocker.Resolve<LyricFileService>();

            // A test-orchestrated restore (NOT production recovery):
            // copy only a byte-verified original into the known missing path,
            // insert a fresh native TrackFile, then repair the track mapping.
            File.Copy(sixthBackup, sixthPath, overwrite: false);
            Assert.That(File.ReadAllBytes(sixthPath), Is.EqualTo(oldBytes));
            var restored = new TrackFile
            {
                Path = sixthPath,
                AlbumId = album.Id,
                Size = oldBytes.LongLength,
                Modified = File.GetLastWriteTimeUtc(sixthPath),
                DateAdded = DateTime.UtcNow,
                Quality = new QualityModel(Quality.MP3_192)
            };
            Mocker.Resolve<IMediaFileService>().Add(restored);
            Assert.That(restored.Id, Is.Not.EqualTo(orphan.TrackFileId));
            orphan.TrackFileId = restored.Id;
            Mocker.Resolve<ITrackService>().SetFileIds(new List<Track> { orphan });

            // An interrupted selective import left 10 correctly upgraded tracks
            // without registered lyric rows. Reassociate those surviving physical
            // sidecars by new IDs, and the restored sixth file by its new ID.
            for (var i = 1; i <= 11; i++)
            {
                var track = tracks[i];
                var owned = Path.Combine(lib, i.ToString("D2") + "-synthetic.mp3");
                var actualBytes = File.ReadAllBytes(owned);
                var expected = Encoding.UTF8.GetBytes(
                    (i == 6 ? "OWNED-OPAQUE-SYNTHETIC-FILE-" : "INCOMING-OPAQUE-SYNTHETIC-FILE-") + i);
                Assert.That(actualBytes, Is.EqualTo(expected));

                var stored = Db.All<TrackFile>().Single(x => x.Id == track.TrackFileId);
                Assert.That(stored.Path, Is.EqualTo(owned));
                Assert.That(stored.AlbumId, Is.EqualTo(album.Id));
                var lyricPath = Path.Combine(lib, i.ToString("D2") + "-synthetic.lrc");
                Assert.That(File.ReadAllBytes(lyricPath), Is.EqualTo(
                    Encoding.UTF8.GetBytes("[00:00.00] SYNTHETIC LYRIC " + i)));

                Assert.That(Db.All<LyricFile>().Any(x => x.TrackFileId == track.TrackFileId),
                    Is.False);
                lyrics.Upsert(new LyricFile
                {
                    Id = 0,
                    ArtistId = artist.Id,
                    AlbumId = album.Id,
                    TrackFileId = track.TrackFileId,
                    RelativePath = Path.Combine("synthetic-album", i.ToString("D2") + "-synthetic.lrc"),
                    Extension = ".lrc"
                });
            }

            var protectedTrack = tracks[12];
            var protectedPath = Path.Combine(lib, "12-synthetic.flac");
            Assert.That(File.ReadAllBytes(protectedPath),
                Is.EqualTo(Encoding.UTF8.GetBytes("OWNED-OPAQUE-SYNTHETIC-FILE-12")));
            Assert.That(Db.All<LyricFile>().Any(x => x.TrackFileId == protectedTrack.TrackFileId),
                Is.True);
            Assert.That(File.Exists(Path.Combine(stage, "12-synthetic.mp3")), Is.True);

            var finalFiles = Db.All<TrackFile>().ToDictionary(x => x.Id);
            var finalTracks = Db.All<Track>();
            Assert.That(finalFiles.Count, Is.EqualTo(12));
            Assert.That(finalTracks.Count, Is.EqualTo(12));
            Assert.That(finalTracks.All(x => finalFiles.ContainsKey(x.TrackFileId)), Is.True);
            Assert.That(Db.All<LyricFile>().Count, Is.EqualTo(12));
            Assert.That(Db.All<AlbumRelease>().Single(x => x.Id == release.Id).Monitored, Is.True);
            Assert.That(File.ReadAllBytes(sixthBackup), Is.EqualTo(oldBytes));
        }
    }
}
