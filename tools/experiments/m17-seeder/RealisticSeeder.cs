// M21 TEST HARNESS ONLY. Seeds a catalog from a CAPTURED, immutable MusicBrainz fixture (CC0 metadata, two editions,
// two discs) into a disposable stock Lidarr. Real artist, release-group, release, recording and release-track MBIDs and
// real titles; SYNTHETIC silent audio and synthetic (short) durations. Owned files are written at the paths stock
// naming builds (the harness enables renaming before seeding), so incoming download names differ from destinations.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Plugins.CrescendarrFixtureSeeder
{
    public sealed partial class CrescendarrSeedFixtureExecutor
    {
        private sealed record FixtureTrack(int Medium, int Position, string ReleaseTrackMbid, string RecordingMbid, string Title);

        private static List<FixtureTrack> ReadRelease(string path, out JsonElement release)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            release = document.RootElement.Clone();
            var tracks = new List<FixtureTrack>();
            foreach (var medium in release.GetProperty("media").EnumerateArray())
            {
                foreach (var t in medium.GetProperty("tracks").EnumerateArray())
                {
                    tracks.Add(new FixtureTrack(medium.GetProperty("position").GetInt32(), t.GetProperty("position").GetInt32(), t.GetProperty("id").GetString(),
                        t.GetProperty("recording").GetProperty("id").GetString(), t.GetProperty("title").GetString()));
                }
            }

            return tracks;
        }

        /// <summary>
        /// "realistic": owned = every track MP3-192 with a lyric, except disc 1 track 3 (superior FLAC + lyric) and disc 2
        /// track 5 (missing). "realistic-repeated": identical, but disc 2 track 14 reuses disc 1 track 1's recording MBID
        /// (a DERIVED variant: same recording on two release tracks, distinguished only by release-track MBID).
        /// Downloads: the monitored CD edition (good), the Digital Media edition (other edition), and the CD edition plus
        /// an unidentifiable extra audio file.
        /// </summary>
        private object SeedRealistic(string root, string scenario, NzbDrone.Core.RootFolders.RootFolder rootFolder,
            NzbDrone.Core.Profiles.Qualities.QualityProfile profile, NzbDrone.Core.Profiles.Metadata.MetadataProfile metadataProfile)
        {
            var fixtures = Environment.GetEnvironmentVariable("CRESCENDARR_M21_FIXTURES") ?? string.Empty;
            var cd = ReadRelease(Path.Combine(fixtures, "release-abd65bff-4396-47f4-b6e6-65b2f2cf8cde.json"), out var cdRelease);
            var digital = ReadRelease(Path.Combine(fixtures, "release-7925da59-b862-41fd-b0cd-4a9b345f036f.json"), out var digitalRelease);
            if (scenario == "realistic-repeated")
            {
                cd[cd.Count - 1] = cd[cd.Count - 1] with { RecordingMbid = cd[0].RecordingMbid };
                digital[digital.Count - 1] = digital[digital.Count - 1] with { RecordingMbid = digital[0].RecordingMbid };
            }

            var credit = cdRelease.GetProperty("artist-credit")[0].GetProperty("artist");
            var artistMbid = credit.GetProperty("id").GetString();
            var artistName = credit.GetProperty("name").GetString();
            var group = cdRelease.GetProperty("release-group");
            var groupMbid = group.GetProperty("id").GetString();
            var albumTitle = cdRelease.GetProperty("title").GetString();

            var metadata = _artistMetadata.Insert(new ArtistMetadata
            {
                ForeignArtistId = artistMbid, OldForeignArtistIds = new List<string>(), Name = artistName,
                Aliases = new List<string>(), Overview = string.Empty, Disambiguation = string.Empty, Type = "Group",
                Status = ArtistStatusType.Continuing, Images = new List<MediaCover.MediaCover>(), Links = new List<Links>(),
                Genres = new List<string>(), Ratings = new Ratings(), Members = new List<Member>()
            });
            var artist = new Artist
            {
                ArtistMetadataId = metadata.Id, CleanName = new string(artistName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()),
                SortName = artistName.ToLowerInvariant(), Monitored = true, MonitorNewItems = NewItemMonitorTypes.None, LastInfoSync = DateTime.UtcNow,
                RootFolderPath = rootFolder.Path, Added = DateTime.UtcNow, QualityProfileId = profile.Id,
                MetadataProfileId = metadataProfile.Id, Tags = new HashSet<int>(), Metadata = metadata, QualityProfile = profile
            };
            artist.Path = Path.Combine(rootFolder.Path, _fileNames.GetArtistFolder(artist));
            artist = _artists.Insert(artist);
            Directory.CreateDirectory(artist.Path);
            var album = _albums.Insert(new Album
            {
                ArtistMetadataId = metadata.Id, ForeignAlbumId = groupMbid, OldForeignAlbumIds = new List<string>(), Title = albumTitle,
                Overview = string.Empty, Disambiguation = string.Empty, ReleaseDate = new DateTime(1995, 10, 23), Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links>(), Genres = new List<string>(), AlbumType = "Album", SecondaryTypes = new List<SecondaryAlbumType>(),
                Ratings = new Ratings(), CleanTitle = new string(albumTitle.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()), Monitored = true,
                AnyReleaseOk = false, LastInfoSync = DateTime.UtcNow, Added = DateTime.UtcNow
            });

            int Seconds(int index) => 8 + (index % 7);
            AlbumRelease Release(JsonElement json, List<FixtureTrack> tracks, bool monitored)
            {
                var formats = json.GetProperty("media").EnumerateArray().Select(m => m.GetProperty("format").GetString()).ToList();
                return _releases.Insert(new AlbumRelease
                {
                    AlbumId = album.Id, ForeignReleaseId = json.GetProperty("id").GetString(), OldForeignReleaseIds = new List<string>(), Title = albumTitle,
                    Status = "Official", Duration = tracks.Select((t, i) => Seconds(i) * 1000).Sum(), Label = new List<string>(),
                    Disambiguation = monitored ? string.Empty : "digital", Country = new List<string> { json.GetProperty("country").GetString() },
                    ReleaseDate = new DateTime(1995, 10, 23),
                    Media = formats.Select((f, i) => new Medium { Number = i + 1, Name = string.Empty, Format = f }).ToList(),
                    TrackCount = tracks.Count, Monitored = monitored
                });
            }

            var release = Release(cdRelease, cd, true);
            var alternate = Release(digitalRelease, digital, false);
            List<Track> Tracks(AlbumRelease r, List<FixtureTrack> fixture) => fixture.Select((t, i) => new Track
            {
                ForeignTrackId = t.ReleaseTrackMbid, OldForeignTrackIds = new List<string>(), ForeignRecordingId = t.RecordingMbid,
                OldForeignRecordingIds = new List<string>(), AlbumReleaseId = r.Id, ArtistMetadataId = metadata.Id, TrackNumber = t.Position.ToString(),
                AbsoluteTrackNumber = i + 1, Title = t.Title, Duration = Seconds(i) * 1000, Explicit = false, Ratings = new Ratings(),
                MediumNumber = t.Medium, TrackFileId = 0
            }).ToList();
            _tracks.InsertMany(Tracks(release, cd));
            _tracks.InsertMany(Tracks(alternate, digital));
            var mainTracks = _tracks.GetTracksByRelease(release.Id).OrderBy(t => t.AbsoluteTrackNumber).ToList();
            album.Artist = artist;
            album.AlbumReleases = new List<AlbumRelease> { release, alternate };
            release.Album = album;

            void Tag(string path, FixtureTrack t, string releaseMbid, int discCount) => new AudioTag
            {
                Title = t.Title, Performers = new[] { artistName }, AlbumArtists = new[] { artistName }, Album = albumTitle,
                Track = (uint)t.Position, Disc = (uint)t.Medium, DiscCount = (uint)discCount, Year = 1995,
                MusicBrainzReleaseId = releaseMbid, MusicBrainzReleaseGroupId = groupMbid, MusicBrainzArtistId = artistMbid,
                MusicBrainzReleaseArtistId = artistMbid, MusicBrainzTrackId = t.RecordingMbid, MusicBrainzReleaseTrackId = t.ReleaseTrackMbid
            }.Write(path);

            var evidence = new List<object>();
            string firstOwned = null;
            for (var i = 0; i < mainTracks.Count; i++)
            {
                var track = mainTracks[i];
                var f = cd[i];
                track.AlbumRelease = release;
                var superior = f.Medium == 1 && f.Position == 3;
                if (f.Medium == 2 && f.Position == 5)
                {
                    evidence.Add(new { n = i + 1, medium = f.Medium, position = f.Position, trackId = track.Id, recording = f.RecordingMbid, releaseTrack = f.ReleaseTrackMbid, owned = false });
                    continue;
                }

                var ext = superior ? ".flac" : ".mp3";
                var path = _fileNames.BuildTrackFilePath(new List<Track> { track }, artist, album, new TrackFile { Path = "seed" + ext, Quality = new QualityModel(superior ? Quality.FLAC : Quality.MP3_192) }, ext);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, superior ? SyntheticFlac(Seconds(i), out _) : SilentMp3(192, Seconds(i), out _));
                Tag(path, f, release.ForeignReleaseId, 2);
                var tag = new AudioTag(path);
                if (!tag.IsValid || tag.Quality?.Quality != (superior ? Quality.FLAC : Quality.MP3_192)) throw new CommandFailedException($"Owned fixture track {i + 1} unreadable.");
                firstOwned ??= path;
                var info = new FileInfo(path);
                var file = new TrackFile
                {
                    Path = path, AlbumId = album.Id, Size = info.Length, Modified = info.LastWriteTimeUtc, DateAdded = DateTime.UtcNow,
                    Quality = new QualityModel(superior ? Quality.FLAC : Quality.MP3_192), MediaInfo = tag.MediaInfo
                };
                _mediaFiles.Add(file);
                track.TrackFileId = file.Id;
                _tracks.SetFileId(new List<Track> { track });
                var lyricPath = Path.ChangeExtension(path, ".lrc");
                File.WriteAllText(lyricPath, $"[00:00.00] {f.Title}\n[00:05.00] protected by Crescendarr (fixture lyric {i + 1})\n");
                _lyrics.Upsert(new LyricFile
                {
                    ArtistId = artist.Id, AlbumId = album.Id, TrackFileId = file.Id, RelativePath = Path.GetRelativePath(artist.Path, lyricPath),
                    Extension = ".lrc", Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                });
                evidence.Add(new
                {
                    n = i + 1, medium = f.Medium, position = f.Position, trackId = track.Id, recording = f.RecordingMbid, releaseTrack = f.ReleaseTrackMbid, owned = true,
                    path, sha = Sha(path), quality = superior ? "FLAC" : "MP3-192", trackFileId = file.Id, lyricPath, lyricSha = Sha(lyricPath)
                });
            }

            // A non-audio extra in the album folder that the import must never touch.
            var cover = Path.Combine(Path.GetDirectoryName(firstOwned), "cover.jpg");
            File.WriteAllBytes(cover, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 0xFF, 0xD9 });

            var prepared = Path.Combine(root, "prepared");
            void Download(string name, string releaseMbid, List<FixtureTrack> fixture, bool unknownExtra)
            {
                var folder = Path.Combine(prepared, name);
                Directory.CreateDirectory(folder);
                for (var i = 0; i < fixture.Count; i++)
                {
                    var path = Path.Combine(folder, $"d{fixture[i].Medium}t{fixture[i].Position:D2}.mp3");
                    File.WriteAllBytes(path, SilentMp3(256, Seconds(i), out _));
                    Tag(path, fixture[i], releaseMbid, 2);
                }

                File.WriteAllText(Path.Combine(folder, "release.nfo"), "synthetic nfo\n");
                if (unknownExtra) File.WriteAllBytes(Path.Combine(folder, "hidden bonus.mp3"), SilentMp3(256, 9, out _));
            }

            Download("Realistic Fixture [good]", release.ForeignReleaseId, cd, false);
            Download("Realistic Fixture [other edition]", alternate.ForeignReleaseId, digital, false);
            Download("Realistic Fixture [unknown audio]", release.ForeignReleaseId, cd, true);
            return new
            {
                scenario, artistId = artist.Id, artistPath = artist.Path, albumId = album.Id, releaseId = release.Id, alternateReleaseId = alternate.Id,
                qualityProfileId = profile.Id, cover, coverSha = Sha(cover), tracks = evidence
            };
        }
    }
}
