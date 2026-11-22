// M15 issue #40: source-pinned native, read-only repository characterization.
//
// This fixture uses a migrated runner-local SQLite DbTest and synthetic database rows only.
// It proves whether stock ITrackRepository.GetTracksByFileId reads cross-album links, and
// contrasts it with the unsafe album-scoped query. No filesystem audio, Lidarr daemon,
// NAS, downloader, write-enabled adapter, or production library is accessed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrCrossAlbumTrackFileQueryFixture : DbTest
    {
        private const string ArtistMbid = "e3831092-5f8a-474f-81ce-c674c198ed42";
        private const string AlbumOneMbid = "ab00b892-e938-44a8-963e-1ca9b447f110";
        private const string AlbumTwoMbid = "a8e6e7cc-36c1-43b1-b102-24b16df04507";
        private const string ReleaseOneMbid = "d492bb80-c3ed-4c32-86c0-f14b05809ae4";
        private const string ReleaseTwoMbid = "07d63f6a-4650-4c3b-9de2-c3cd00b36425";

        [Test]
        public void stock_repository_track_file_id_query_sees_links_from_other_albums()
        {
            var metadata = Builder<ArtistMetadata>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.Name = "Crescendarr Synthetic Artist")
                .With(x => x.ForeignArtistId = ArtistMbid)
                .Build();
            Db.Insert(metadata);

            var profile = new QualityProfile
            {
                Name = "Crescendarr M15 synthetic",
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.FLAC.Id
            };
            Db.Insert(profile);

            var artist = Builder<Artist>.CreateNew()
                .With(x => x.Id = 0)
                .With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.Path = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m15-query", "artist"))
                .With(x => x.QualityProfileId = profile.Id)
                .With(x => x.QualityProfile = profile)
                .With(x => x.Metadata = metadata)
                .Build();
            Db.Insert(artist);

            Album InsertAlbum(string mbid, string title)
            {
                var album = Builder<Album>.CreateNew()
                    .With(x => x.Id = 0)
                    .With(x => x.ArtistMetadataId = metadata.Id)
                    .With(x => x.ForeignAlbumId = mbid)
                    .With(x => x.Title = title)
                    .With(x => x.CleanTitle = title.ToLowerInvariant().Replace(" ", ""))
                    .With(x => x.ReleaseDate = new DateTime(2020, 1, 1))
                    .With(x => x.AlbumType = "Album")
                    .With(x => x.SecondaryTypes = new List<SecondaryAlbumType>())
                    .With(x => x.Genres = new List<string>())
                    .With(x => x.Images = new List<MediaCover.MediaCover>())
                    .With(x => x.Links = new List<Links>())
                    .With(x => x.OldForeignAlbumIds = new List<string>())
                    .With(x => x.Monitored = true)
                    .With(x => x.Artist = artist)
                    .With(x => x.ArtistMetadata = metadata)
                    .Build();
                Db.Insert(album);
                return album;
            }

            AlbumRelease InsertRelease(Album album, string mbid)
            {
                var release = Builder<AlbumRelease>.CreateNew()
                    .With(x => x.Id = 0)
                    .With(x => x.AlbumId = album.Id)
                    .With(x => x.ForeignReleaseId = mbid)
                    .With(x => x.Title = album.Title)
                    .With(x => x.Status = "Official")
                    .With(x => x.Label = new List<string>())
                    .With(x => x.Country = new List<string>())
                    .With(x => x.Disambiguation = "")
                    .With(x => x.OldForeignReleaseIds = new List<string>())
                    .With(x => x.ReleaseDate = new DateTime(2020, 1, 1))
                    .With(x => x.Media = new List<Medium> { new Medium { Number = 1, Name = "", Format = "Digital Media" } })
                    .With(x => x.TrackCount = 2)
                    .With(x => x.Monitored = true)
                    .Build();
                Db.Insert(release);
                return release;
            }

            var albumOne = InsertAlbum(AlbumOneMbid, "Album One");
            var albumTwo = InsertAlbum(AlbumTwoMbid, "Album Two");
            var releaseOne = InsertRelease(albumOne, ReleaseOneMbid);
            var releaseTwo = InsertRelease(albumTwo, ReleaseTwoMbid);
            var root = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m15-query", "files");

            TrackFile InsertFile(string stem, Album album)
            {
                var file = new TrackFile
                {
                    Path = Path.Combine(root, stem + ".flac"),
                    AlbumId = album.Id,
                    Size = 54321,
                    Modified = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.FLAC),
                    Artist = new LazyLoaded<Artist>(artist)
                };
                Db.Insert(file);
                return file;
            }

            var boundFile = InsertFile("operation-bound", albumOne);
            var unrelatedFile = InsertFile("not-operation-bound", albumTwo);

            Track InsertTrack(int trackNo, AlbumRelease release, TrackFile file, string mbid)
            {
                var track = Builder<Track>.CreateNew()
                    .With(x => x.Id = 0)
                    .With(x => x.ArtistMetadataId = metadata.Id)
                    .With(x => x.AlbumReleaseId = release.Id)
                    .With(x => x.TrackFileId = file.Id)
                    .With(x => x.Title = "Synthetic Track " + trackNo)
                    .With(x => x.TrackNumber = trackNo.ToString())
                    .With(x => x.AbsoluteTrackNumber = trackNo)
                    .With(x => x.MediumNumber = 1)
                    .With(x => x.Duration = 10000)
                    .With(x => x.ForeignTrackId = mbid)
                    .With(x => x.ForeignRecordingId = mbid)
                    .With(x => x.OldForeignTrackIds = new List<string>())
                    .With(x => x.OldForeignRecordingIds = new List<string>())
                    .With(x => x.TrackFile = new LazyLoaded<TrackFile>(file))
                    .Build();
                Db.Insert(track);
                return track;
            }

            var bound = InsertTrack(1, releaseOne, boundFile, "1a28672f-09e2-4475-a08b-97969c5dcb19");
            var crossAlbumAlias = InsertTrack(2, releaseTwo, boundFile, "262e9870-b794-4ff1-a22a-0cc37b996949");
            var unrelatedOne = InsertTrack(3, releaseOne, unrelatedFile, "cdb70743-9aef-459d-a87b-c945a748e68c");
            var unrelatedTwo = InsertTrack(4, releaseTwo, unrelatedFile, "08a5d82a-c237-40d3-a008-380903d48de2");

            var stock = Mocker.Resolve<TrackRepository>();

            var globalBoundRefs = stock.GetTracksByFileId(boundFile.Id);
            var albumOnly = stock.GetTracksByAlbum(albumOne.Id);
            Assert.That(globalBoundRefs.Select(t => t.Id), Is.EquivalentTo(new[] { bound.Id, crossAlbumAlias.Id }),
                "stock file-ID lookup must expose the alias in a second album");
            Assert.That(globalBoundRefs.Select(t => t.AlbumReleaseId).Distinct().Count(), Is.EqualTo(2),
                "cross-album coverage must be proven, not inferred from a second track in one album");
            Assert.That(albumOnly.Any(t => t.Id == bound.Id), Is.True, "the bound track is present in album-scoped query");
            Assert.That(albumOnly.Any(t => t.Id == crossAlbumAlias.Id), Is.False,
                "album-only HostSnapshot would silently miss the dangerous alias");

            var batchedRefs = stock.GetTracksByFileId(new[] { boundFile.Id, unrelatedFile.Id });
            Assert.That(batchedRefs.Select(t => t.Id),
                Is.EquivalentTo(new[] { bound.Id, crossAlbumAlias.Id, unrelatedOne.Id, unrelatedTwo.Id }),
                "batched cross-album lookup must retain ALL tracks linked to relevant numeric IDs");

            // The native query may return legitimate sharing too; M13's Reconciler must
            // reject sharing only when the row belongs to the operation.
            Assert.That(stock.GetTracksByFileId(unrelatedFile.Id).Select(t => t.Id),
                Is.EquivalentTo(new[] { unrelatedOne.Id, unrelatedTwo.Id }),
                "unrelated, legally shared row should remain observable without global blanket rejection");

            TestContext.Progress.WriteLine(
                $"M15_CROSS_ALBUM_QUERY: boundTrack={bound.Id} crossAlbumAlias={crossAlbumAlias.Id} " +
                $"fileId={boundFile.Id} globalMatches={globalBoundRefs.Count} " +
                $"albumOnlyMatches={albumOnly.Count} albumOnlyMissesAlias=True " +
                $"batchedMatches={batchedRefs.Count} unrelatedSharedMatches=2");
        }
    }
}
