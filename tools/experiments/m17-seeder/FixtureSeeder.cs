// M17 TEST HARNESS ONLY. Seeds a synthetic artist, album (two editions), tracks and an existing owned
// library into a DISPOSABLE stock Lidarr daemon through stock repositories and services. Synthetic
// MusicBrainz IDs cannot be fetched from the metadata server, so the catalog rows are inserted directly
// (no ArtistAdded/Refresh events, no network). Owned audio and every download candidate are valid tagged
// synthetic audio written with Lidarr's own AudioTag writer. Never install outside the CI harness.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLog;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Plugins.CrescendarrFixtureSeeder
{
    public sealed class CrescendarrFixtureSeederPlugin : Plugin
    {
        public override string Name => "Crescendarr M17 disposable fixture seeder (test harness)";
        public override string Owner => "Crescendarr project";
        public override string GithubUrl => string.Empty;
    }

    public sealed class CrescendarrSeedFixtureCommand : Command
    {
        public string Scenario { get; set; } = string.Empty;
        public override bool RequiresDiskAccess => true;
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
    }

    public sealed partial class CrescendarrSeedFixtureExecutor : IExecute<CrescendarrSeedFixtureCommand>
    {
        public const string ArtistName = "Crescendarr Synthetic Artist";
        public const string AlbumTitle = "Crescendarr Synthetic Album";
        public const string ArtistMbid = "b8e9bf54-3424-4691-8657-7b7e3ff902e5";
        public const string ReleaseGroupMbid = "c5b4e91e-40fa-42c1-a25b-4f1220713587";
        public const string ReleaseMbid = "9fd3c58e-91db-4d89-86a7-e57558394c1f";
        public const string AltReleaseMbid = "3a0b7c55-1f62-4f0e-9a64-7d2f0e3b51aa";

        // M18: an unrelated FOREIGN artist/album used to prove the global TrackFile sharer projection.
        public const string ForeignArtistName = "Crescendarr Foreign Artist";
        public const string ForeignAlbumTitle = "Crescendarr Foreign Album";
        public const string ForeignArtistMbid = "6f1c0d3e-2b4a-4c5d-9e8f-0a1b2c3d4e5f";
        public const string ForeignReleaseGroupMbid = "7a2d1e4f-3c5b-4d6e-8f9a-1b2c3d4e5f60";
        public const string ForeignReleaseMbid = "8b3e2f50-4d6c-4e7f-9a0b-2c3d4e5f6071";
        public const int DanglingTrackFileId = 900001;

        private readonly IRootFolderService _rootFolders;
        private readonly IQualityProfileService _qualityProfiles;
        private readonly IMetadataProfileService _metadataProfiles;
        private readonly IArtistMetadataRepository _artistMetadata;
        private readonly IArtistRepository _artists;
        private readonly IAlbumRepository _albums;
        private readonly IReleaseRepository _releases;
        private readonly ITrackRepository _tracks;
        private readonly IMediaFileService _mediaFiles;
        private readonly IExtraFileService<LyricFile> _lyrics;
        private readonly IBuildFileNames _fileNames;
        private readonly Logger _logger;

        public CrescendarrSeedFixtureExecutor(IRootFolderService rootFolders, IQualityProfileService qualityProfiles,
            IMetadataProfileService metadataProfiles, IArtistMetadataRepository artistMetadata, IArtistRepository artists,
            IAlbumRepository albums, IReleaseRepository releases, ITrackRepository tracks, IMediaFileService mediaFiles,
            IExtraFileService<LyricFile> lyrics, IBuildFileNames fileNames, Logger logger)
        {
            _rootFolders = rootFolders;
            _qualityProfiles = qualityProfiles;
            _metadataProfiles = metadataProfiles;
            _artistMetadata = artistMetadata;
            _artists = artists;
            _albums = albums;
            _releases = releases;
            _tracks = tracks;
            _mediaFiles = mediaFiles;
            _lyrics = lyrics;
            _fileNames = fileNames;
            _logger = logger;
        }

        public void Execute(CrescendarrSeedFixtureCommand message)
        {
            var root = RequireRoot();
            var (total, superior, missing) = message.Scenario switch
            {
                "three" => (3, new[] { 2 }, new[] { 3 }),
                "twelve" => (12, new[] { 12 }, Array.Empty<int>()),
                "realistic" or "realistic-repeated" => (0, Array.Empty<int>(), Array.Empty<int>()),
                _ => throw new CommandFailedException("Unknown scenario.")
            };

            var library = Path.Combine(root, "library");
            var rootFolder = _rootFolders.All().Single(r => Path.GetFullPath(r.Path).TrimEnd('/') == library);
            var profile = _qualityProfiles.GetDefaultProfile("Crescendarr synthetic", Quality.FLAC,
                Quality.MP3_192, Quality.MP3_256, Quality.MP3_320, Quality.FLAC);
            profile.UpgradeAllowed = true;
            profile = _qualityProfiles.Add(profile);
            var metadataProfile = _metadataProfiles.All().First();
            if (total == 0)
            {
                var realistic = SeedRealistic(root, message.Scenario, rootFolder, profile, metadataProfile);
                File.WriteAllText(Path.Combine(root, "seed.json"), JsonSerializer.Serialize(realistic, new JsonSerializerOptions { WriteIndented = true }));
                var seeded = $"CRESCENDARR_SEEDED scenario={message.Scenario} (captured MusicBrainz fixture, synthetic audio)";
                Console.WriteLine(seeded);
                _logger.Info(seeded);
                return;
            }

            var metadata = _artistMetadata.Insert(new ArtistMetadata
            {
                ForeignArtistId = ArtistMbid, OldForeignArtistIds = new List<string>(), Name = ArtistName,
                Aliases = new List<string>(), Overview = string.Empty, Disambiguation = string.Empty, Type = "Group",
                Status = ArtistStatusType.Continuing, Images = new List<MediaCover.MediaCover>(), Links = new List<Links>(),
                Genres = new List<string>(), Ratings = new Ratings(), Members = new List<Member>()
            });
            var artist = new Artist
            {
                ArtistMetadataId = metadata.Id, CleanName = "crescendarrsyntheticartist", SortName = ArtistName.ToLowerInvariant(),
                Monitored = true, MonitorNewItems = NewItemMonitorTypes.None, LastInfoSync = DateTime.UtcNow,
                RootFolderPath = rootFolder.Path, Added = DateTime.UtcNow, QualityProfileId = profile.Id,
                MetadataProfileId = metadataProfile.Id, Tags = new HashSet<int>(), Metadata = metadata, QualityProfile = profile
            };
            artist.Path = Path.Combine(rootFolder.Path, _fileNames.GetArtistFolder(artist));
            artist = _artists.Insert(artist);
            Directory.CreateDirectory(artist.Path);

            var album = _albums.Insert(new Album
            {
                ArtistMetadataId = metadata.Id, ForeignAlbumId = ReleaseGroupMbid, OldForeignAlbumIds = new List<string>(),
                Title = AlbumTitle, Overview = string.Empty, Disambiguation = string.Empty, ReleaseDate = new DateTime(2020, 1, 1),
                Images = new List<MediaCover.MediaCover>(), Links = new List<Links>(), Genres = new List<string>(),
                AlbumType = "Album", SecondaryTypes = new List<SecondaryAlbumType>(), Ratings = new Ratings(),
                CleanTitle = "crescendarrsyntheticalbum", Monitored = true, AnyReleaseOk = false,
                LastInfoSync = DateTime.UtcNow, Added = DateTime.UtcNow
            });

            var durations = Enumerable.Range(1, total).ToDictionary(i => i, i => 0);
            for (var i = 1; i <= total; i++)
            {
                SilentMp3(192, Seconds(i), out var ms);
                durations[i] = ms;
            }

            AlbumRelease Release(string mbid, bool monitored, string format) => _releases.Insert(new AlbumRelease
            {
                AlbumId = album.Id, ForeignReleaseId = mbid, OldForeignReleaseIds = new List<string>(), Title = AlbumTitle,
                Status = "Official", Duration = durations.Values.Sum(), Label = new List<string>(),
                Disambiguation = monitored ? string.Empty : "alternate", Country = new List<string>(),
                ReleaseDate = new DateTime(2020, 1, 1), Media = new List<Medium> { new Medium { Number = 1, Name = string.Empty, Format = format } },
                TrackCount = total, Monitored = monitored
            });
            var release = Release(ReleaseMbid, true, "Digital Media");
            var alternate = Release(AltReleaseMbid, false, "CD");

            List<Track> Tracks(AlbumRelease r, int kind) => Enumerable.Range(1, total).Select(i => new Track
            {
                ForeignTrackId = Uuid(kind, i), OldForeignTrackIds = new List<string>(), ForeignRecordingId = Uuid(1, i),
                OldForeignRecordingIds = new List<string>(), AlbumReleaseId = r.Id, ArtistMetadataId = metadata.Id,
                TrackNumber = i.ToString(), AbsoluteTrackNumber = i, Title = $"Synthetic Track {i}", Duration = durations[i],
                Explicit = false, Ratings = new Ratings(), MediumNumber = 1, TrackFileId = 0
            }).ToList();
            var mainTracks = Tracks(release, 2);
            _tracks.InsertMany(mainTracks);
            _tracks.InsertMany(Tracks(alternate, 3));
            mainTracks = _tracks.GetTracksByRelease(release.Id).OrderBy(t => t.AbsoluteTrackNumber).ToList();

            var evidence = new List<object>();
            album.Artist = artist;
            release.Album = album;
            foreach (var track in mainTracks)
            {
                var i = track.AbsoluteTrackNumber;
                track.AlbumRelease = release;
                var stem = $"{i:D2} - Synthetic Track {i}";
                if (missing.Contains(i))
                {
                    evidence.Add(new { n = i, trackId = track.Id, recording = track.ForeignRecordingId, owned = false });
                    continue;
                }

                var flac = superior.Contains(i);
                var bytes = flac ? SyntheticFlac(Seconds(i), out _) : SilentMp3(192, Seconds(i), out _);
                var nominal = new TrackFile { Path = Path.Combine(artist.Path, stem + (flac ? ".flac" : ".mp3")) };
                var path = _fileNames.BuildTrackFilePath(new List<Track> { track }, artist, album, nominal, flac ? ".flac" : ".mp3");
                File.WriteAllBytes(path, bytes);
                Tag(path, i, ReleaseMbid, track.ForeignTrackId);
                var tag = new AudioTag(path);
                if (!tag.IsValid || tag.Quality?.Quality != (flac ? Quality.FLAC : Quality.MP3_192))
                    throw new CommandFailedException($"Synthetic owned track {i} unreadable by stock AudioTag.");

                var info = new FileInfo(path);
                var file = new TrackFile
                {
                    Path = path, AlbumId = album.Id, Size = info.Length, Modified = info.LastWriteTimeUtc, DateAdded = DateTime.UtcNow,
                    Quality = new QualityModel(flac ? Quality.FLAC : Quality.MP3_192), MediaInfo = tag.MediaInfo
                };
                _mediaFiles.Add(file);
                track.TrackFileId = file.Id;
                _tracks.SetFileId(new List<Track> { track });

                var lyricPath = Path.Combine(artist.Path, stem + ".lrc");
                File.WriteAllText(lyricPath, $"[00:00.00] SYNTHETIC LYRIC {i}\n[00:05.00] protected by Crescendarr\n");
                _lyrics.Upsert(new LyricFile
                {
                    ArtistId = artist.Id, AlbumId = album.Id, TrackFileId = file.Id,
                    RelativePath = Path.GetRelativePath(artist.Path, lyricPath), Extension = ".lrc",
                    Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                });
                evidence.Add(new
                {
                    n = i, trackId = track.Id, recording = track.ForeignRecordingId, owned = true, path, sha = Sha(path),
                    quality = flac ? "FLAC" : "MP3-192", trackFileId = file.Id, lyricPath, lyricSha = Sha(lyricPath)
                });
            }

            // Prepared candidate downloads (moved into the blackhole watch folder by the harness).
            var prepared = Path.Combine(root, "prepared");
            void Download(string name, string releaseMbid, int kind)
            {
                var folder = Path.Combine(prepared, name);
                Directory.CreateDirectory(folder);
                for (var i = 1; i <= total; i++)
                {
                    var path = Path.Combine(folder, $"{i:D2} - Synthetic Track {i}.mp3");
                    File.WriteAllBytes(path, SilentMp3(256, Seconds(i), out _));
                    Tag(path, i, releaseMbid, Uuid(kind, i));
                    if (new AudioTag(path).Quality?.Quality != Quality.MP3_256)
                        throw new CommandFailedException("Synthetic incoming track unreadable by stock AudioTag.");
                }
            }

            Download("Crescendarr Synthetic Album [good]", ReleaseMbid, 2);
            Download("Crescendarr Synthetic Album [wrong edition]", AltReleaseMbid, 3);

            var foreign = SeedForeign(rootFolder, profile, metadataProfile);
            var seed = new
            {
                scenario = message.Scenario, artistId = artist.Id, artistPath = artist.Path, albumId = album.Id,
                releaseId = release.Id, alternateReleaseId = alternate.Id, qualityProfileId = profile.Id, tracks = evidence, foreign
            };
            File.WriteAllText(Path.Combine(root, "seed.json"), JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true }));
            var line = $"CRESCENDARR_SEEDED scenario={message.Scenario} artist={artist.Id} album={album.Id} release={release.Id} alternate={alternate.Id} owned={evidence.Count}";
            Console.WriteLine(line);
            _logger.Info(line);
        }

        /// <summary>
        /// Unrelated foreign album (separate artist and folder): F1 and F2 legitimately share ONE TrackFile row,
        /// F3 is unlinked (TrackFileId=0) and F4 carries a dangling nonzero TrackFileId. None of it may block or be touched.
        /// </summary>
        private object SeedForeign(RootFolder rootFolder, QualityProfile profile, MetadataProfile metadataProfile)
        {
            var metadata = _artistMetadata.Insert(new ArtistMetadata
            {
                ForeignArtistId = ForeignArtistMbid, OldForeignArtistIds = new List<string>(), Name = ForeignArtistName,
                Aliases = new List<string>(), Overview = string.Empty, Disambiguation = string.Empty, Type = "Group",
                Status = ArtistStatusType.Continuing, Images = new List<MediaCover.MediaCover>(), Links = new List<Links>(),
                Genres = new List<string>(), Ratings = new Ratings(), Members = new List<Member>()
            });
            var artist = new Artist
            {
                ArtistMetadataId = metadata.Id, CleanName = "crescendarrforeignartist", SortName = ForeignArtistName.ToLowerInvariant(),
                Monitored = true, MonitorNewItems = NewItemMonitorTypes.None, LastInfoSync = DateTime.UtcNow,
                RootFolderPath = rootFolder.Path, Added = DateTime.UtcNow, QualityProfileId = profile.Id,
                MetadataProfileId = metadataProfile.Id, Tags = new HashSet<int>(), Metadata = metadata, QualityProfile = profile
            };
            artist.Path = Path.Combine(rootFolder.Path, _fileNames.GetArtistFolder(artist));
            artist = _artists.Insert(artist);
            Directory.CreateDirectory(artist.Path);
            var album = _albums.Insert(new Album
            {
                ArtistMetadataId = metadata.Id, ForeignAlbumId = ForeignReleaseGroupMbid, OldForeignAlbumIds = new List<string>(),
                Title = ForeignAlbumTitle, Overview = string.Empty, Disambiguation = string.Empty, ReleaseDate = new DateTime(2019, 1, 1),
                Images = new List<MediaCover.MediaCover>(), Links = new List<Links>(), Genres = new List<string>(),
                AlbumType = "Album", SecondaryTypes = new List<SecondaryAlbumType>(), Ratings = new Ratings(),
                CleanTitle = "crescendarrforeignalbum", Monitored = true, AnyReleaseOk = false,
                LastInfoSync = DateTime.UtcNow, Added = DateTime.UtcNow
            });
            SilentMp3(192, 30, out var duration);
            var release = _releases.Insert(new AlbumRelease
            {
                AlbumId = album.Id, ForeignReleaseId = ForeignReleaseMbid, OldForeignReleaseIds = new List<string>(), Title = ForeignAlbumTitle,
                Status = "Official", Duration = duration * 4, Label = new List<string>(), Disambiguation = string.Empty, Country = new List<string>(),
                ReleaseDate = new DateTime(2019, 1, 1), Media = new List<Medium> { new Medium { Number = 1, Name = string.Empty, Format = "Digital Media" } },
                TrackCount = 4, Monitored = true
            });
            _tracks.InsertMany(Enumerable.Range(1, 4).Select(i => new Track
            {
                ForeignTrackId = Uuid(5, i), OldForeignTrackIds = new List<string>(), ForeignRecordingId = Uuid(4, i),
                OldForeignRecordingIds = new List<string>(), AlbumReleaseId = release.Id, ArtistMetadataId = metadata.Id,
                TrackNumber = i.ToString(), AbsoluteTrackNumber = i, Title = $"Foreign Track {i}", Duration = duration,
                Explicit = false, Ratings = new Ratings(), MediumNumber = 1, TrackFileId = 0
            }).ToList());
            var tracks = _tracks.GetTracksByRelease(release.Id).OrderBy(t => t.AbsoluteTrackNumber).ToList();

            var path = Path.Combine(artist.Path, "01 - Foreign Medley.mp3");
            File.WriteAllBytes(path, SilentMp3(192, 30, out _));
            new AudioTag
            {
                Title = "Foreign Medley", Performers = new[] { ForeignArtistName }, AlbumArtists = new[] { ForeignArtistName },
                Album = ForeignAlbumTitle, Track = 1, Disc = 1, DiscCount = 1, Year = 2019,
                MusicBrainzReleaseId = ForeignReleaseMbid, MusicBrainzReleaseGroupId = ForeignReleaseGroupMbid,
                MusicBrainzArtistId = ForeignArtistMbid, MusicBrainzReleaseArtistId = ForeignArtistMbid,
                MusicBrainzTrackId = Uuid(4, 1), MusicBrainzReleaseTrackId = Uuid(5, 1)
            }.Write(path);
            var tag = new AudioTag(path);
            var info = new FileInfo(path);
            var shared = new TrackFile
            {
                Path = path, AlbumId = album.Id, Size = info.Length, Modified = info.LastWriteTimeUtc, DateAdded = DateTime.UtcNow,
                Quality = new QualityModel(Quality.MP3_192), MediaInfo = tag.MediaInfo
            };
            _mediaFiles.Add(shared);
            tracks[0].TrackFileId = shared.Id;
            tracks[1].TrackFileId = shared.Id;
            tracks[3].TrackFileId = DanglingTrackFileId;
            _tracks.SetFileId(new List<Track> { tracks[0], tracks[1], tracks[3] });
            return new
            {
                artistId = artist.Id, artistPath = artist.Path, albumId = album.Id, releaseId = release.Id, sharedTrackFileId = shared.Id,
                sharedPath = path, sharedSha = Sha(path), danglingTrackFileId = DanglingTrackFileId,
                tracks = tracks.Select(t => new { n = t.AbsoluteTrackNumber, trackId = t.Id, trackFileId = t.TrackFileId }).ToList()
            };
        }

        private static string RequireRoot()
        {
            var root = Environment.GetEnvironmentVariable("CRESCENDARR_M17_ROOT") ?? string.Empty;
            if (Environment.GetEnvironmentVariable("CRESCENDARR_M17_SEEDER_OPT_IN") != "DISPOSABLE_SYNTHETIC_ONLY" ||
                Environment.GetEnvironmentVariable("CI") != "true" || !Path.IsPathFullyQualified(root) ||
                !File.Exists(Path.Combine(root, ".crescendarr-disposable-host")))
                throw new CommandFailedException("Fixture seeder requires the disposable CI opt-in.");
            return Path.GetFullPath(root).TrimEnd('/');
        }

        private static double Seconds(int i) => 10 + i;

        private static string Uuid(int kind, int i) => $"0000{kind:0000}-0000-4000-8000-{i:000000000000}";

        private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static void Tag(string path, int i, string releaseMbid, string releaseTrackMbid)
        {
            new AudioTag
            {
                Title = $"Synthetic Track {i}", Performers = new[] { ArtistName }, AlbumArtists = new[] { ArtistName },
                Album = AlbumTitle, Track = (uint)i, Disc = 1, DiscCount = 1, Year = 2020,
                MusicBrainzReleaseId = releaseMbid, MusicBrainzReleaseGroupId = ReleaseGroupMbid,
                MusicBrainzArtistId = ArtistMbid, MusicBrainzReleaseArtistId = ArtistMbid,
                MusicBrainzTrackId = Uuid(1, i), MusicBrainzReleaseTrackId = releaseTrackMbid
            }.Write(path);
        }

        /// <summary>Silent MPEG-1 Layer III, 44.1 kHz mono, constant bitrate, zero payload frames.</summary>
        private static byte[] SilentMp3(int kbps, double seconds, out int durationMs)
        {
            var index = kbps switch { 192 => 11, 256 => 13, 320 => 14, _ => throw new ArgumentException("bitrate") };
            var frameLength = 144 * kbps * 1000 / 44100;
            var frames = (int)Math.Ceiling(seconds * 44100 / 1152.0);
            var bytes = new byte[frames * frameLength];
            for (var f = 0; f < frames; f++)
            {
                var o = f * frameLength;
                bytes[o] = 0xFF;
                bytes[o + 1] = 0xFB;
                bytes[o + 2] = (byte)(index << 4);
                bytes[o + 3] = 0xC4;
            }

            durationMs = (int)Math.Round(bytes.LongLength * 8.0 / kbps);
            return bytes;
        }

        /// <summary>FLAC marker + STREAMINFO (16-bit mono 44.1 kHz, exact sample count) + opaque frame area.</summary>
        private static byte[] SyntheticFlac(double seconds, out int durationMs)
        {
            const int rate = 44100;
            var samples = (long)(seconds * rate);
            var info = new byte[34];
            info[0] = 0x10;
            info[2] = 0x10;
            var packed = ((ulong)rate << 44) | (15UL << 36) | (ulong)samples;
            for (var b = 0; b < 8; b++) info[10 + b] = (byte)(packed >> (56 - (8 * b)));
            var header = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0x80, 0x00, 0x00, 0x22 };
            durationMs = (int)(samples * 1000 / rate);
            return header.Concat(info).Concat(new byte[(int)(seconds * 40000)]).ToArray();
        }
    }
}
