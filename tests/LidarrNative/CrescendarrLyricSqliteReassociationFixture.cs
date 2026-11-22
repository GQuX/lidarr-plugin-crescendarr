// Uses stock Lidarr's migrated TEST SQLite database and a disposable text .lrc.
// This is NOT a real media import and never accesses any user library or NAS.
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrLyricSqliteReassociationFixture : DbTest
    {
        private LyricFileService MakeService()
        {
            var repository = Mocker.Resolve<ExtraFileRepository<LyricFile>>();
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(repository);
            return Mocker.Resolve<LyricFileService>();
        }

        [Test]
        public void deleting_and_reinserting_lyric_row_reassociates_to_new_trackfile_and_keeps_bytes()
        {
            var dir = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-synthetic", "album");
            Directory.CreateDirectory(dir);
            var lyricPath = Path.Combine(dir, "01-title.lrc");
            const string text = "[00:00.00] SYNTHETIC LYRICS ONLY";
            File.WriteAllText(lyricPath, text);

            var original = new LyricFile
            {
                ArtistId = 42,
                AlbumId = 22,
                TrackFileId = 51,
                RelativePath = Path.Combine("album", "01-title.lrc"),
                Extension = ".lrc"
            };
            var service = MakeService();
            service.Upsert(original);

            var stored = Db.All<LyricFile>().Single();
            Assert.That(stored.TrackFileId, Is.EqualTo(51));
            Assert.That(stored.Id, Is.GreaterThan(0));

            service.Delete(stored.Id);
            Assert.That(Db.All<LyricFile>(), Is.Empty);
            Assert.That(File.ReadAllText(lyricPath), Is.EqualTo(text));

            // The deleted old record cannot be reinserted via Upsert unless
            // its Id is zero. Use a new record linked to the new TrackFile.
            var linkedToReplacement = new LyricFile
            {
                Id = 0,
                ArtistId = stored.ArtistId,
                AlbumId = stored.AlbumId,
                TrackFileId = 152,
                RelativePath = stored.RelativePath,
                Extension = stored.Extension
            };
            service.Upsert(linkedToReplacement);

            var relinked = Db.All<LyricFile>().Single();
            Assert.That(relinked.TrackFileId, Is.EqualTo(152));
            Assert.That(relinked.Id, Is.Not.EqualTo(stored.Id));
            Assert.That(File.ReadAllText(lyricPath), Is.EqualTo(text));
        }

        [Test]
        public void upsert_with_deleted_id_does_not_recreate_missing_sqlite_row()
        {
            var service = MakeService();
            var original = new LyricFile
            {
                ArtistId = 42,
                AlbumId = 22,
                TrackFileId = 51,
                RelativePath = Path.Combine("album", "02-title.lrc"),
                Extension = ".lrc"
            };

            service.Upsert(original);
            var stored = Db.All<LyricFile>().Single();
            service.Delete(stored.Id);
            Assert.That(Db.All<LyricFile>(), Is.Empty);

            // UpdateMany on deleted ID is not an INSERT. Whether it reports
            // a missing-row error is an implementation detail; it cannot
            // restore the deleted row. The mock-based test separately checks
            // the UpdateMany-vs-InsertMany method selection.
            try
            {
                service.Upsert(stored);
            }
            catch (System.Exception)
            {
                // Regardless of its error behavior, no row was inserted.
            }

            Assert.That(Db.All<LyricFile>(), Is.Empty);
        }
    }
}
