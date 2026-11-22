// M14 (issue #38): stock Lidarr DiskScanService with REAL tag reading, identification and
// import decisions over valid, synthetically generated, tagged audio in a disposable folder.
// Source-pinned Lidarr v3.1.6.5079 test project only. No server, network, NAS, downloader,
// user library or Lidarr modification. Synthetic audio is generated here at runtime
// (silent MPEG-1 Layer III frames and a FLAC STREAMINFO stream) and tagged with Lidarr's
// own AudioTag writer; it is never committed as a binary.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FizzWare.NBuilder;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation.Aggregators;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.MediaFiles.TrackImport.Specifications;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Mono.Disk;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.AutoMoq;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrTaggedAudioRescanFixture : DbTest
    {
        private const int Total = 12;
        private const int Upgrades = 11;
        private const int Sixth = 6;
        private const string ArtistName = "Crescendarr Synthetic Artist";
        private const string AlbumTitle = "Crescendarr Synthetic Album";
        private const string ArtistMbid = "b8e9bf54-3424-4691-8657-7b7e3ff902e5";
        private const string ReleaseGroupMbid = "c5b4e91e-40fa-42c1-a25b-4f1220713587";
        private const string ReleaseMbid = "9fd3c58e-91db-4d89-86a7-e57558394c1f";

        public enum Scenario
        {
            /// <summary>No import: track six simply has a valid, unregistered file. Proves a real decision is made.</summary>
            ControlUnlinkedValidFile,

            /// <summary>Sixth transfer fails after stock delete; original restored from backup before any fresh row.</summary>
            RestoredOriginalBeforeFreshRow,

            /// <summary>Sixth transfer COMPLETES, then the importer fails before the new row is inserted.</summary>
            IncomingTransferredButUnlinked,

            /// <summary>A real scan runs INSIDE the importer, at track six, after the stock deletion events of tracks 1-6.</summary>
            ScanInterleavedInsideImporter,
        }

        [TestCase(Scenario.ControlUnlinkedValidFile)]
        [TestCase(Scenario.RestoredOriginalBeforeFreshRow)]
        [TestCase(Scenario.IncomingTransferredButUnlinked)]
        [TestCase(Scenario.ScanInterleavedInsideImporter)]
        public void native_tagged_audio_rescan_characterization(Scenario scenario)
        {
            var w = BuildWorld(sixthUnlinkedFromStart: scenario == Scenario.ControlUnlinkedValidFile);
            WireRealEventsAndStockDisk(w);
            var recorder = WireRealScan(w);

            if (scenario == Scenario.ControlUnlinkedValidFile)
            {
                Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Sixth).TrackFileId, Is.EqualTo(0));
                RunRealScan(w, recorder, "control");
                FinishWithEvidence(w, scenario);
                return;
            }

            ScanObservation interleaved = null;
            w.TransferHook = (source, destination, mode, overwrite) =>
            {
                if (source != w.IncomingPaths[Sixth] || destination != w.OwnedPaths[Sixth])
                {
                    return w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
                }

                switch (scenario)
                {
                    case Scenario.RestoredOriginalBeforeFreshRow:
                        throw new IOException("SYNTHETIC: sixth transfer interrupted after stock delete (M14 tagged)");
                    case Scenario.IncomingTransferredButUnlinked:
                        w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
                        throw new IOException("SYNTHETIC: failure after the sixth transfer completed, before the row insert");
                    case Scenario.ScanInterleavedInsideImporter:
                        interleaved = RunRealScan(w, recorder, "interleaved");
                        return w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
                    default:
                        throw new InvalidOperationException("unexpected scenario");
                }
            };

            var results = ImportUpgrades(w);
            Log($"M14_TAGGED_IMPORT_RESULTS scenario={scenario} " + string.Join(",", results.Select(r => r.Result)));
            LogDb("after-import", w);

            switch (scenario)
            {
                case Scenario.RestoredOriginalBeforeFreshRow:
                    Assert.That(results.Count(r => r.Result == ImportResultType.Imported), Is.EqualTo(Upgrades - 1));
                    Assert.That(File.Exists(w.OwnedPaths[Sixth]), Is.False, "sixth original recycled by stock upgrader");
                    Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Sixth).TrackFileId, Is.EqualTo(0),
                        "real stock deletion handler detaches track six");

                    // M13's automatic RestoreOriginalBytes step: verified backup back in place, no overwrite,
                    // and deliberately NO fresh row yet (that step needs operator approval in M13).
                    File.Copy(w.BackupPaths[Sixth], w.OwnedPaths[Sixth], overwrite: false);
                    Assert.That(File.ReadAllBytes(w.OwnedPaths[Sixth]), Is.EqualTo(w.OwnedBytes[Sixth]));
                    RunRealScan(w, recorder, "after-restore");
                    break;

                case Scenario.IncomingTransferredButUnlinked:
                    Assert.That(File.ReadAllBytes(w.OwnedPaths[Sixth]), Is.EqualTo(w.IncomingBytes[Sixth]),
                        "incoming sixth bytes placed at the destination");
                    Assert.That(Db.All<TrackFile>().Any(f => f.Path == w.OwnedPaths[Sixth]), Is.False,
                        "no row for the placed sixth file before the scan");
                    RunRealScan(w, recorder, "after-unlinked-transfer");
                    break;

                case Scenario.ScanInterleavedInsideImporter:
                    Assert.That(interleaved, Is.Not.Null, "the interleaved scan ran inside the importer");
                    Assert.That(results.Count(r => r.Result == ImportResultType.Imported), Is.EqualTo(Upgrades));
                    break;
            }

            FinishWithEvidence(w, scenario);
        }

        /// <summary>Evidence-gathering pass: reports every violated invariant at once.</summary>
        private void FinishWithEvidence(World w, Scenario scenario)
        {
            var violations = CollectInvariants(w, scenario);
            Assert.That(violations, Is.Empty, "Crescendarr invariants violated by the stock scan: " + string.Join("; ", violations));
        }

        // ------------------------------------------------------------------ world

        private sealed class World
        {
            public string Temp;
            public string Library;
            public string Staging;
            public string Recycle;
            public string Backups;
            public Artist Artist;
            public Album Album;
            public AlbumRelease Release;
            public LyricFileService Lyrics;
            public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();
            public readonly Dictionary<int, TrackFile> Previous = new Dictionary<int, TrackFile>();
            public readonly Dictionary<int, string> OwnedPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, string> IncomingPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, string> BackupPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, string> LyricPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, byte[]> OwnedBytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, byte[]> IncomingBytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, byte[]> LyricBytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, int> LyricIds = new Dictionary<int, int>();
            public DiskTransferService NativeTransfer;
            public Func<string, string, TransferMode, bool, TransferMode> TransferHook;
            public EventAggregator Bus;
            public MediaFileService MediaFiles;
            public TrackService TrackService;
        }

        private sealed class ScanObservation
        {
            public string Label;
            public List<ImportDecision<LocalTrack>> Decisions = new List<ImportDecision<LocalTrack>>();
        }

        /// <summary>Records every decision the REAL stock decision maker returns.</summary>
        private sealed class RecordingDecisionMaker : IMakeImportDecision
        {
            private readonly IMakeImportDecision _inner;
            public readonly List<List<ImportDecision<LocalTrack>>> Calls = new List<List<ImportDecision<LocalTrack>>>();

            public RecordingDecisionMaker(IMakeImportDecision inner) => _inner = inner;

            public List<ImportDecision<LocalTrack>> GetImportDecisions(List<System.IO.Abstractions.IFileInfo> musicFiles, IdentificationOverrides idOverrides, ImportDecisionMakerInfo itemInfo, ImportDecisionMakerConfig config)
            {
                var decisions = _inner.GetImportDecisions(musicFiles, idOverrides, itemInfo, config);
                Calls.Add(decisions);
                return decisions;
            }
        }

        private static double TrackSeconds(int i) => 10 + i;

        private static string Uuid(int kind, int i) => $"0000{kind:0000}-0000-4000-8000-{i:000000000000}";

        private static string RecordingMbid(int i) => Uuid(1, i);

        private static string TrackMbid(int i) => Uuid(2, i);

        /// <summary>Silent MPEG-1 Layer III, 44.1 kHz mono, constant bitrate, zero payload frames.</summary>
        private static byte[] SilentMp3(int kbps, double seconds, out int durationMs)
        {
            var index = kbps switch { 128 => 9, 192 => 11, 256 => 13, 320 => 14, _ => throw new ArgumentException("bitrate") };
            var frameLength = 144 * kbps * 1000 / 44100;
            var frames = (int)Math.Ceiling(seconds * 44100 / 1152.0);
            var bytes = new byte[frames * frameLength];
            for (var f = 0; f < frames; f++)
            {
                var o = f * frameLength;
                bytes[o] = 0xFF;
                bytes[o + 1] = 0xFB;                    // MPEG-1, Layer III, no CRC
                bytes[o + 2] = (byte)(index << 4);       // bitrate index, 44.1 kHz, no padding
                bytes[o + 3] = 0xC4;                     // mono, original
            }

            durationMs = (int)Math.Round(bytes.LongLength * 8.0 / kbps);
            return bytes;
        }

        /// <summary>FLAC stream: marker + STREAMINFO (16-bit mono 44.1 kHz, exact sample count) + opaque frame area.</summary>
        private static byte[] SyntheticFlac(double seconds, out int durationMs)
        {
            const int rate = 44100;
            var samples = (long)(seconds * rate);
            var info = new byte[34];
            info[0] = 0x10; info[1] = 0x00;              // min block 4096
            info[2] = 0x10; info[3] = 0x00;              // max block 4096
            // min/max frame size unknown (0): bytes 4..9 stay zero
            var packed = ((ulong)rate << 44) | (0UL << 41) | (15UL << 36) | (ulong)samples;
            for (var b = 0; b < 8; b++) info[10 + b] = (byte)(packed >> (56 - (8 * b)));
            // MD5 (bytes 18..33) zero = unknown
            var header = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0x80, 0x00, 0x00, 0x22 }; // last block, STREAMINFO, length 34
            var audio = new byte[(int)(seconds * 40000)];
            durationMs = (int)(samples * 1000 / rate);
            return header.Concat(info).Concat(audio).ToArray();
        }

        private static void Tag(string path, int i)
        {
            new AudioTag
            {
                Title = $"Synthetic Track {i}",
                Performers = new[] { ArtistName },
                AlbumArtists = new[] { ArtistName },
                Album = AlbumTitle,
                Track = (uint)i,
                TrackCount = Total,
                Disc = 1,
                DiscCount = 1,
                Year = 2020,
                MusicBrainzReleaseId = ReleaseMbid,
                MusicBrainzReleaseGroupId = ReleaseGroupMbid,
                MusicBrainzArtistId = ArtistMbid,
                MusicBrainzReleaseArtistId = ArtistMbid,
                MusicBrainzTrackId = RecordingMbid(i),
                MusicBrainzReleaseTrackId = TrackMbid(i)
            }.Write(path);
        }

        private World BuildWorld(bool sixthUnlinkedFromStart)
        {
            var w = new World { Temp = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m14-tagged") };
            w.Library = Path.Combine(w.Temp, "library", "synthetic-artist", "synthetic-album");
            w.Staging = Path.Combine(w.Temp, "staging");
            w.Recycle = Path.Combine(w.Temp, "synthetic-recycle");
            w.Backups = Path.Combine(w.Temp, "verified-copies");
            foreach (var d in new[] { w.Library, w.Staging, w.Recycle, w.Backups }) Directory.CreateDirectory(d);

            var metadata = Builder<ArtistMetadata>.CreateNew()
                .With(x => x.Id = 0).With(x => x.Name = ArtistName).With(x => x.ForeignArtistId = ArtistMbid).Build();
            Db.Insert(metadata);

            var profile = new QualityProfile { Name = "Crescendarr synthetic profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Cutoff = Quality.FLAC.Id };
            Db.Insert(profile);

            w.Artist = Builder<Artist>.CreateNew()
                .With(x => x.Id = 0).With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.Path = Path.GetDirectoryName(w.Library))
                .With(x => x.QualityProfileId = profile.Id).With(x => x.QualityProfile = profile)
                .With(x => x.Metadata = metadata)
                .Build();
            Db.Insert(w.Artist);

            w.Album = Builder<Album>.CreateNew()
                .With(x => x.Id = 0).With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.ForeignAlbumId = ReleaseGroupMbid).With(x => x.Title = AlbumTitle)
                .With(x => x.CleanTitle = "crescendarrsyntheticalbum")
                .With(x => x.ReleaseDate = new DateTime(2020, 1, 1)).With(x => x.AlbumType = "Album")
                .With(x => x.SecondaryTypes = new List<SecondaryAlbumType>()).With(x => x.Genres = new List<string>())
                .With(x => x.Images = new List<MediaCover.MediaCover>()).With(x => x.Links = new List<Links>())
                .With(x => x.OldForeignAlbumIds = new List<string>()).With(x => x.Monitored = true)
                .With(x => x.Artist = w.Artist).With(x => x.ArtistMetadata = metadata)
                .Build();
            Db.Insert(w.Album);

            w.Release = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0).With(x => x.AlbumId = w.Album.Id).With(x => x.ForeignReleaseId = ReleaseMbid)
                .With(x => x.Title = AlbumTitle).With(x => x.Status = "Official")
                .With(x => x.Label = new List<string>()).With(x => x.Country = new List<string>())
                .With(x => x.Disambiguation = "").With(x => x.OldForeignReleaseIds = new List<string>())
                .With(x => x.ReleaseDate = new DateTime(2020, 1, 1))
                .With(x => x.Media = new List<Medium> { new Medium { Number = 1, Name = "", Format = "Digital Media" } })
                .With(x => x.TrackCount = Total).With(x => x.Monitored = true)
                .Build();
            Db.Insert(w.Release);
            w.Release.Album = w.Album;
            w.Album.AlbumReleases = new List<AlbumRelease> { w.Release };

            var extraRepository = Mocker.Resolve<ExtraFileRepository<LyricFile>>();
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(extraRepository);
            w.Lyrics = Mocker.Resolve<LyricFileService>();
            Mocker.SetConstant<IExtraFileService<LyricFile>>(w.Lyrics);

            for (var i = 1; i <= Total; i++)
            {
                var stem = i.ToString("D2") + "-synthetic";
                var flac = i == Total;
                w.OwnedPaths[i] = Path.Combine(w.Library, stem + (flac ? ".flac" : ".mp3"));
                w.IncomingPaths[i] = Path.Combine(w.Staging, stem + ".mp3");
                w.LyricPaths[i] = Path.Combine(w.Library, stem + ".lrc");
                w.BackupPaths[i] = Path.Combine(w.Backups, Path.GetFileName(w.OwnedPaths[i]));

                var ownedAudio = flac ? SyntheticFlac(TrackSeconds(i), out var durationMs) : SilentMp3(192, TrackSeconds(i), out durationMs);
                File.WriteAllBytes(w.OwnedPaths[i], ownedAudio);
                Tag(w.OwnedPaths[i], i);
                File.WriteAllBytes(w.IncomingPaths[i], SilentMp3(256, TrackSeconds(i), out _));
                Tag(w.IncomingPaths[i], i);
                w.OwnedBytes[i] = File.ReadAllBytes(w.OwnedPaths[i]);
                w.IncomingBytes[i] = File.ReadAllBytes(w.IncomingPaths[i]);
                w.LyricBytes[i] = Encoding.UTF8.GetBytes($"[00:00.00] SYNTHETIC LYRIC {i}\n");
                File.WriteAllBytes(w.LyricPaths[i], w.LyricBytes[i]);
                File.Copy(w.OwnedPaths[i], w.BackupPaths[i]);

                // Prove the generated audio is valid for the stock tag reader BEFORE using it.
                var read = new AudioTag(w.OwnedPaths[i]);
                Assert.That(read.IsValid, Is.True, $"stock AudioTag could not read synthetic track {i}");
                Assert.That(read.MusicBrainzReleaseId, Is.EqualTo(ReleaseMbid));
                Assert.That(read.MusicBrainzTrackId, Is.EqualTo(RecordingMbid(i)));
                Assert.That(read.Quality?.Quality, Is.EqualTo(flac ? Quality.FLAC : Quality.MP3_192), $"quality of owned track {i}");
                Assert.That(new AudioTag(w.IncomingPaths[i]).Quality?.Quality, Is.EqualTo(Quality.MP3_256), $"quality of incoming track {i}");

                var unlinked = sixthUnlinkedFromStart && i == Sixth;
                TrackFile old = null;
                if (!unlinked)
                {
                    old = new TrackFile
                    {
                        Path = w.OwnedPaths[i],
                        AlbumId = w.Album.Id,
                        Size = w.OwnedBytes[i].LongLength,
                        Modified = File.GetLastWriteTimeUtc(w.OwnedPaths[i]),
                        DateAdded = DateTime.UtcNow,
                        Quality = new QualityModel(flac ? Quality.FLAC : Quality.MP3_192),
                        MediaInfo = read.MediaInfo,
                        Artist = new LazyLoaded<Artist>(w.Artist)
                    };
                    Db.Insert(old);
                    w.Previous[i] = old;
                }

                var track = Builder<Track>.CreateNew()
                    .With(x => x.Id = 0).With(x => x.ArtistMetadataId = metadata.Id)
                    .With(x => x.AlbumReleaseId = w.Release.Id)
                    .With(x => x.TrackFileId = old?.Id ?? 0)
                    .With(x => x.Title = $"Synthetic Track {i}")
                    .With(x => x.TrackNumber = i.ToString()).With(x => x.AbsoluteTrackNumber = i)
                    .With(x => x.MediumNumber = 1).With(x => x.Duration = durationMs)
                    .With(x => x.ForeignTrackId = TrackMbid(i)).With(x => x.ForeignRecordingId = RecordingMbid(i))
                    .With(x => x.OldForeignTrackIds = new List<string>()).With(x => x.OldForeignRecordingIds = new List<string>())
                    .With(x => x.TrackFile = old == null ? null : new LazyLoaded<TrackFile>(old))
                    .Build();
                Db.Insert(track);
                w.Tracks[i] = track;

                if (old != null)
                {
                    w.Lyrics.Upsert(new LyricFile
                    {
                        ArtistId = w.Artist.Id,
                        AlbumId = w.Album.Id,
                        TrackFileId = old.Id,
                        RelativePath = Path.Combine("synthetic-album", stem + ".lrc"),
                        Extension = ".lrc"
                    });
                    w.LyricIds[i] = Db.All<LyricFile>().Single(x => x.TrackFileId == old.Id).Id;
                }
            }

            Mocker.GetMock<IArtistService>().Setup(x => x.GetArtists(It.IsAny<IEnumerable<int>>())).Returns(new List<Artist> { w.Artist });
            Mocker.GetMock<IArtistService>().Setup(x => x.GetArtist(w.Artist.Id)).Returns(w.Artist);
            Mocker.GetMock<IAlbumService>().Setup(x => x.GetAlbum(w.Album.Id)).Returns(w.Album);
            Mocker.GetMock<IRootFolderService>().Setup(x => x.GetBestRootFolder(It.IsAny<string>()))
                .Returns(new RootFolder { Path = Path.Combine(w.Temp, "library"), DefaultQualityProfileId = profile.Id });
            return w;
        }

        // ------------------------------------------------------------------ wiring

        private void WireRealEventsAndStockDisk(World w)
        {
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<ITrackRepository>(Mocker.Resolve<TrackRepository>());
            Mocker.SetConstant<IReleaseRepository>(Mocker.Resolve<ReleaseRepository>());
            w.TrackService = Mocker.Resolve<TrackService>();
            Mocker.SetConstant<ITrackService>(w.TrackService);
            Mocker.SetConstant<IReleaseService>(Mocker.Resolve<ReleaseService>());

            // Real synchronous stock deletion subscribers, registered BEFORE the importer runs.
            Mocker.GetMock<IServiceFactory>().Setup(x => x.BuildAll<IHandle<TrackFileDeletedEvent>>())
                .Returns(new List<IHandle<TrackFileDeletedEvent>> { w.TrackService, w.Lyrics });
            w.Bus = Mocker.Resolve<EventAggregator>();
            Mocker.SetConstant<IEventAggregator>(w.Bus);
            w.MediaFiles = new MediaFileService(Mocker.Resolve<IMediaFileRepository>(), w.Bus, LogManager.GetLogger("CrescendarrM14Tagged"));
            Mocker.SetConstant<IMediaFileService>(w.MediaFiles);

            Mocker.SetConstant<IProcMountProvider>(Mocker.Resolve<ProcMountProvider>());
            Mocker.SetConstant<ISymbolicLinkResolver>(Mocker.Resolve<SymbolicLinkResolver>());
            Mocker.SetConstant<IDiskProvider>(Mocker.Resolve<IDiskProvider>(FileSystemType.Actual));
            w.NativeTransfer = Mocker.Resolve<DiskTransferService>();
            Mocker.GetMock<IDiskTransferService>()
                .Setup(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()))
                .Returns((string s, string d, TransferMode m, bool o) =>
                    w.TransferHook != null ? w.TransferHook(s, d, m, o) : w.NativeTransfer.TransferFile(s, d, m, o));

            Mocker.GetMock<IConfigService>().SetupGet(x => x.RecycleBin).Returns(w.Recycle);
            Mocker.SetConstant<IRecycleBinProvider>(Mocker.Resolve<RecycleBinProvider>());
            Mocker.GetMock<IBuildFileNames>()
                .Setup(x => x.BuildTrackFilePath(It.IsAny<List<Track>>(), It.IsAny<Artist>(), It.IsAny<Album>(),
                    It.IsAny<TrackFile>(), It.IsAny<string>(), It.IsAny<NamingConfig>(), It.IsAny<List<CustomFormat>>()))
                .Returns((List<Track> tracks, Artist a, Album al, TrackFile tf, string ext, NamingConfig n, List<CustomFormat> cf) =>
                    w.OwnedPaths[tracks.Single().AbsoluteTrackNumber]);
            Mocker.GetMock<IImportScript>()
                .Setup(x => x.TryImport(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalTrack>(), It.IsAny<TrackFile>(), It.IsAny<TransferMode>(), null))
                .Returns(ScriptImportDecision.DeferMove);
            Mocker.SetConstant<IMoveTrackFiles>(Mocker.Resolve<TrackFileMovingService>());
            Mocker.SetConstant<IUpgradeMediaFiles>(Mocker.Resolve<UpgradeMediaFileService>());
        }

        private RecordingDecisionMaker WireRealScan(World w)
        {
            var log = LogManager.GetLogger("CrescendarrM14TaggedScan");
            var tags = Mocker.Resolve<AudioTagService>();
            Mocker.SetConstant<IAudioTagService>(tags);
            var augmenting = new AugmentingService(
                new List<IAggregate<LocalTrack>> { Mocker.Resolve<AggregateQuality>(), Mocker.Resolve<AggregateReleaseGroup>() },
                new List<IAggregate<LocalAlbumRelease>> { Mocker.Resolve<AggregateFilenameInfo>() },
                Mocker.Resolve<IDiskProvider>(), log);
            Mocker.SetConstant<IAugmentingService>(augmenting);
            Mocker.SetConstant<ITrackGroupingService>(Mocker.Resolve<TrackGroupingService>());
            Mocker.SetConstant<ICandidateService>(Mocker.Resolve<CandidateService>());
            var identification = Mocker.Resolve<IdentificationService>();

            var config = Mocker.GetMock<IConfigService>().Object;
            var trackSpecs = new List<IImportDecisionEngineSpecification<LocalTrack>>
            {
                new NzbDrone.Core.MediaFiles.TrackImport.Specifications.FreeSpaceSpecification(Mocker.Resolve<IDiskProvider>(), config, log),
                new NotUnpackingSpecification(Mocker.Resolve<IDiskProvider>(), config, log),
                new SameTracksImportSpecification(new SameTracksSpecification(w.TrackService), log),
                new UpgradeSpecification(config, Mocker.GetMock<ICustomFormatCalculationService>().Object, log),
                new CloseTrackMatchSpecification(log),
            };
            var albumSpecs = new List<IImportDecisionEngineSpecification<LocalAlbumRelease>>
            {
                new AlbumUpgradeSpecification(log),
                new NzbDrone.Core.MediaFiles.TrackImport.Specifications.AlreadyImportedSpecification(Mocker.GetMock<IHistoryService>().Object, log),
                new ArtistPathInRootFolderSpecification(Mocker.GetMock<IRootFolderService>().Object, log),
                new CloseAlbumMatchSpecification(log),
                new MoreTracksSpecification(log),
                new NoMissingOrUnmatchedTracksSpecification(log),
                new ReleaseWantedSpecification(log),
            };
            var maker = new ImportDecisionMaker(trackSpecs, albumSpecs, w.MediaFiles, tags, augmenting, identification,
                Mocker.GetMock<IRootFolderService>().Object, Mocker.GetMock<IQualityProfileService>().Object, log);
            var recorder = new RecordingDecisionMaker(maker);
            Mocker.SetConstant<IMakeImportDecision>(recorder);
            Mocker.SetConstant<IMediaFileTableCleanupService>(Mocker.Resolve<MediaFileTableCleanupService>());
            return recorder;
        }

        private List<ImportResult> ImportUpgrades(World w)
        {
            var nativeQuality = new UpgradeSpecification(Mocker.GetMock<IConfigService>().Object,
                Mocker.GetMock<ICustomFormatCalculationService>().Object, LogManager.GetLogger("CrescendarrM14Quality"));
            var decisions = new List<ImportDecision<LocalTrack>>();
            for (var i = 1; i <= Total; i++)
            {
                var local = new LocalTrack
                {
                    Artist = w.Artist,
                    Album = w.Album,
                    Release = w.Release,
                    Tracks = new List<Track> { Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i) },
                    Path = w.IncomingPaths[i],
                    Size = w.IncomingBytes[i].LongLength,
                    Modified = File.GetLastWriteTimeUtc(w.IncomingPaths[i]),
                    Quality = new QualityModel(Quality.MP3_256),
                    FileTrackInfo = new ParsedTrackInfo { ReleaseGroup = "CRESCENDARR-SYNTHETIC" }
                };
                local.Tracks[0].TrackFile = new LazyLoaded<TrackFile>(w.Previous[i]);
                var accepted = nativeQuality.IsSatisfiedBy(local, null).Accepted;
                Assert.That(accepted, Is.EqualTo(i != Total), "stock quality approves 11 upgrades and rejects the FLAC");
                decisions.Add(accepted ? new ImportDecision<LocalTrack>(local) : new ImportDecision<LocalTrack>(local, new Rejection("Inferior to owned FLAC")));
            }

            // Same lyric protection as M12/M13: remove only the DB association of the eleven targets.
            for (var i = 1; i <= Upgrades; i++) w.Lyrics.Delete(w.LyricIds[i]);

            var importer = Mocker.Resolve<ImportApprovedTracks>();
            ExceptionVerification.IgnoreWarns();
            return importer.Import(decisions, false, importMode: ImportMode.Move);
        }

        private ScanObservation RunRealScan(World w, RecordingDecisionMaker recorder, string label)
        {
            // The scan's own importer must see the real tag service, so resolve it now.
            Mocker.SetConstant<IImportApprovedTracks>(Mocker.Resolve<ImportApprovedTracks>());
            var scanner = Mocker.Resolve<DiskScanService>();
            Mocker.SetConstant<IDiskScanService>(scanner);
            var existingExtras = new ExistingExtraFileService(Mocker.Resolve<IDiskProvider>(), scanner,
                new List<IImportExistingExtraFiles> { new ExistingLyricImporter(w.Lyrics, Mocker.Resolve<IAugmentingService>(), LogManager.GetLogger("CrescendarrM14Lyrics")) },
                LogManager.GetLogger("CrescendarrM14Extras"));
            Mocker.GetMock<IServiceFactory>().Setup(x => x.BuildAll<IHandle<ArtistScannedEvent>>())
                .Returns(new List<IHandle<ArtistScannedEvent>> { existingExtras });

            var before = recorder.Calls.Count;
            ExceptionVerification.IgnoreWarns();
            scanner.Scan(new List<string> { w.Artist.Path }, FilterFilesType.Known, false, new List<int> { w.Artist.Id });
            Assert.That(recorder.Calls.Count, Is.EqualTo(before + 1), "real decision maker invoked once by the scan");

            var observation = new ScanObservation { Label = label, Decisions = recorder.Calls.Last() };
            foreach (var d in observation.Decisions)
            {
                var item = d.Item;
                Log($"M14_TAGGED_DECISION scan={label} file={Path.GetFileName(item.Path)} approved={d.Approved} " +
                    $"tracks=[{string.Join(",", item.Tracks?.Select(t => t.AbsoluteTrackNumber) ?? Enumerable.Empty<int>())}] " +
                    $"release={item.Release?.ForeignReleaseId} quality={item.Quality?.Quality} existing={item.ExistingFile} additional={item.AdditionalFile} " +
                    $"rejections=[{string.Join(" | ", d.Rejections.Select(r => r.Reason))}]");
            }

            LogDb("after-scan-" + label, w);
            return observation;
        }

        // ------------------------------------------------------------------ assertions and evidence

        /// <summary>
        /// Collects EVERY Crescendarr invariant outcome (no early exit) and logs it, so a single CI run
        /// yields the complete stock behavior. Returns the violated invariants.
        /// </summary>
        private List<string> CollectInvariants(World w, Scenario scenario)
        {
            var violations = new List<string>();
            void Check(bool ok, string name)
            {
                Log($"M14_TAGGED_INVARIANT scenario={scenario} {(ok ? "held" : "VIOLATED")} {name}");
                if (!ok) violations.Add(name);
            }

            var tracks = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber);
            var rows = Db.All<TrackFile>();
            var lyricRows = Db.All<LyricFile>();
            Check(File.Exists(w.OwnedPaths[Total]) && File.ReadAllBytes(w.OwnedPaths[Total]).SequenceEqual(w.OwnedBytes[Total]), "protected superior FLAC bytes unchanged");
            Check(tracks[Total].TrackFileId == w.Previous[Total].Id, "protected track still linked to its original TrackFile row");
            Check(lyricRows.Any(l => l.Id == w.LyricIds[Total] && l.TrackFileId == w.Previous[Total].Id), "protected lyric row unchanged");
            for (var i = 1; i <= Total; i++)
            {
                Check(File.Exists(w.LyricPaths[i]) && File.ReadAllBytes(w.LyricPaths[i]).SequenceEqual(w.LyricBytes[i]), $"physical lyric {i} present with original bytes");
                Check(File.ReadAllBytes(w.BackupPaths[i]).SequenceEqual(w.OwnedBytes[i]), $"independent backup {i} intact");
            }

            var releases = Db.All<AlbumRelease>();
            Check(releases.Count(r => r.Monitored) == 1 && releases.Single(r => r.Monitored).ForeignReleaseId == ReleaseMbid, "monitored MusicBrainz release unchanged");
            Check(!rows.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1), "no two TrackFile rows share a path");
            Check(rows.All(f => tracks.Values.Any(t => t.TrackFileId == f.Id)), "every TrackFile row is linked to a track");
            Check(tracks.Values.All(t => t.TrackFileId == 0 || rows.Any(f => f.Id == t.TrackFileId)), "no track points at a missing TrackFile row");

            var recycled = Directory.Exists(w.Recycle)
                ? Directory.EnumerateFiles(w.Recycle, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x).ToList()
                : new List<string>();
            Log($"M14_TAGGED_RECYCLE scenario={scenario} count={recycled.Count} lrc={recycled.Count(x => x.EndsWith(".lrc"))} files=[{string.Join(",", recycled)}]");
            Log($"M14_TAGGED_SUMMARY scenario={scenario} trackFiles={rows.Count} lyricRows={lyricRows.Count} violations={violations.Count}");
            return violations;
        }

        private void LogDb(string stage, World w)
        {
            var files = Db.All<TrackFile>().ToDictionary(f => f.Id);
            var lyricsByFile = Db.All<LyricFile>().GroupBy(l => l.TrackFileId).ToDictionary(g => g.Key, g => g.Count());
            foreach (var t in Db.All<Track>().OrderBy(t => t.AbsoluteTrackNumber))
            {
                files.TryGetValue(t.TrackFileId, out var f);
                Log($"M14_TAGGED_STATE stage={stage} track={t.AbsoluteTrackNumber} trackFileId={t.TrackFileId} " +
                    $"row={(f == null ? "none" : Path.GetFileName(f.Path) + "/" + f.Quality?.Quality)} " +
                    $"lyrics={(lyricsByFile.TryGetValue(t.TrackFileId, out var n) ? n : 0)} " +
                    $"diskBytes={(File.Exists(w.OwnedPaths[t.AbsoluteTrackNumber]) ? (File.ReadAllBytes(w.OwnedPaths[t.AbsoluteTrackNumber]).SequenceEqual(w.OwnedBytes[t.AbsoluteTrackNumber]) ? "original" : File.ReadAllBytes(w.OwnedPaths[t.AbsoluteTrackNumber]).SequenceEqual(w.IncomingBytes[t.AbsoluteTrackNumber]) ? "incoming" : "other") : "missing")}");
            }

            foreach (var f in files.Values.Where(f => !Db.All<Track>().Any(t => t.TrackFileId == f.Id)))
            {
                Log($"M14_TAGGED_STATE stage={stage} unlinkedRow id={f.Id} path={Path.GetFileName(f.Path)}");
            }
        }

        private static void Log(string line) => TestContext.Progress.WriteLine(line);
    }
}
