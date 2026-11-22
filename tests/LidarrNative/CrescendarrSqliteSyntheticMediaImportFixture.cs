// Incremental M12 integration characterization. Real stock importer, upgrader,
// media/release/track services, migrated SQLite and disposable physical bytes.
// Test-controlled disk transfer/recycle adapters; no tag reading or real download.
// NOT a safe import plugin, recovery proof, or valid audio-decoding test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
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
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Mono.Disk;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.AutoMoq;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrSqliteSyntheticMediaImportFixture : DbTest
    {
        private const int Total = 12;
        private const int Upgrades = 11;

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void native_selective_import_relinks_real_sqlite_and_preserves_protected_bytes_and_lyrics(bool failAtSix, bool useStockDiskPipeline)
        {
            RunSelectiveImport(failAtSix, preserveInterruptedCheckpoint: false, useStockDiskPipeline);
        }

        // With two actual MusicBrainz editions in SQLite, demonstrate why an
        // executor must independently pin the incoming release before passing
        // approved tracks to stock ImportApprovedTracks. The stock importer itself
        // does NOT preserve a previously selected release.
        [TestCase(false)]
        [TestCase(true)]
        public void native_stock_import_with_competing_editions_selects_the_incoming_release(bool incomingAlternateEdition)
        {
            RunSelectiveImport(failAtSix: false, preserveInterruptedCheckpoint: false,
                useStockDiskPipeline: true, withCompetingEdition: true,
                incomingAlternateEdition: incomingAlternateEdition);
        }

        // M14 orphan-rescan gate: let actual stock DiskScanService.Scan run after the
        // real sixth file transfer fault but BEFORE the test-only recovery. The opaque
        // payloads cannot pass Lidarr's audio tag reader, so decision discovery is
        // deliberately stubbed to empty. This characterizes scan cleanup/event wiring,
        // not a full native identification and import pass.
        [Test]
        public void native_scan_before_recovering_sixth_transfer_failure_preserves_recovery_evidence()
        {
            RunSelectiveImport(failAtSix: true, preserveInterruptedCheckpoint: false,
                useStockDiskPipeline: true, scanBeforeRecovery: true);
        }

        // Compare a more host-like failure: native synchronous deletion handlers were
        // registered BEFORE the failing importer, not added only for the later scan.
        // This can change whether track 6 retains a dangling ID or becomes unlinked.
        [Test]
        public void native_delete_event_fanout_before_scan_then_recovers_sixth_transfer_failure()
        {
            RunSelectiveImport(failAtSix: true, preserveInterruptedCheckpoint: false,
                useStockDiskPipeline: true, scanBeforeRecovery: true,
                realEventsDuringImport: true);
        }

        // Called only by an explicit, isolated CI step; never in the normal suite.
        // The source-pinned DbTest database is exported before its teardown and
        // checked in a different dotnet test process by the companion fixture.
        [Test]
        public void produce_sixth_file_interruption_checkpoint_for_new_process()
        {
            Assert.That(Environment.GetEnvironmentVariable("CRESCENDARR_M12_PHASE"),
                Is.EqualTo("prepare"), "Separate CI phase must opt in explicitly.");
            RunSelectiveImport(failAtSix: true, preserveInterruptedCheckpoint: true);
        }

        private void RunSelectiveImport(bool failAtSix, bool preserveInterruptedCheckpoint,
            bool useStockDiskPipeline = false, bool withCompetingEdition = false,
            bool incomingAlternateEdition = false, bool scanBeforeRecovery = false,
            bool realEventsDuringImport = false)
        {
            var restartRoot = Environment.GetEnvironmentVariable("CRESCENDARR_M12_RESTART_ROOT");
            if (preserveInterruptedCheckpoint)
            {
                Assert.That(restartRoot, Is.Not.Null.And.Not.Empty);
                Assert.That(Path.IsPathFullyQualified(restartRoot), Is.True);
                Assert.That(Path.GetFileName(restartRoot).StartsWith("crescendarr-m12-restart-"), Is.True);
                Assert.That(Directory.Exists(restartRoot), Is.False,
                    "Refuse to overwrite a prior restart checkpoint.");
                Directory.CreateDirectory(restartRoot);
            }
            var temp = preserveInterruptedCheckpoint
                ? Path.Combine(restartRoot, "owned")
                : Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m12-disposable");
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
            AlbumRelease alternateRelease = null;
            if (withCompetingEdition)
            {
                // Different real MusicBrainz release UUID under the SAME album/group.
                // The currently selected release is always the first one, until
                // unmodified stock ImportApprovedTracks.SetMonitored changes it.
                alternateRelease = Builder<AlbumRelease>.CreateNew()
                    .With(x => x.Id = 0)
                    .With(x => x.AlbumId = album.Id)
                    .With(x => x.ForeignReleaseId = "65c9d81e-bcd2-4a9a-8be2-31c181fc23dd")
                    .With(x => x.TrackCount = Total)
                    .With(x => x.Monitored = false)
                    .Build();
                Db.Insert(alternateRelease);
                alternateRelease.Album = album;
            }

            album.AlbumReleases = alternateRelease == null
                ? new List<AlbumRelease> { release }
                : new List<AlbumRelease> { release, alternateRelease };
            Assert.That(Db.All<AlbumRelease>().Count, Is.EqualTo(withCompetingEdition ? 2 : 1));
            Assert.That(Db.All<AlbumRelease>().Single(r => r.Id == release.Id).Monitored, Is.True,
                "The selected edition starts as the original, not the alternate.");

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
                    Release = incomingAlternateEdition ? alternateRelease : release,
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

            IEventAggregator importerBus = null;
            if (realEventsDuringImport)
            {
                // Unlike the older M12 fixture, register ACTUAL stock synchronous
                // TrackFileDeletedEvent handlers BEFORE the sixth-file failure.
                // Native media deletion should now publish to real TrackService
                // (DetachTrackFile) and LyricFileService on the real SQLite state.
                var trackService = Mocker.Resolve<TrackService>();
                Mocker.SetConstant<ITrackService>(trackService);
                Mocker.GetMock<IServiceFactory>()
                    .Setup(x => x.BuildAll<IHandle<TrackFileDeletedEvent>>())
                    .Returns(new List<IHandle<TrackFileDeletedEvent>>
                    {
                        trackService,
                        lyrics
                    });
                Mocker.GetMock<IServiceFactory>()
                    .Setup(x => x.BuildAll<IHandleAsync<IEvent>>())
                    .Returns(new List<IHandleAsync<IEvent>>());
                Mocker.GetMock<IServiceFactory>()
                    .Setup(x => x.BuildAll<IHandleAsync<TrackFileDeletedEvent>>())
                    .Returns(new List<IHandleAsync<TrackFileDeletedEvent>>());
                Mocker.GetMock<IServiceFactory>()
                    .Setup(x => x.BuildAll<IHandle<TrackFileAddedEvent>>())
                    .Returns(new List<IHandle<TrackFileAddedEvent>>());
                Mocker.GetMock<IServiceFactory>()
                    .Setup(x => x.BuildAll<IHandleAsync<TrackFileAddedEvent>>())
                    .Returns(new List<IHandleAsync<TrackFileAddedEvent>>());
                importerBus = Mocker.Resolve<EventAggregator>();
                Mocker.SetConstant<IEventAggregator>(importerBus);
            }

            Mocker.SetConstant<IMediaFileService>(realEventsDuringImport
                ? new MediaFileService(Mocker.Resolve<IMediaFileRepository>(), importerBus,
                    NLog.LogManager.GetLogger("CrescendarrM14NativeEventsDuringImport"))
                : Mocker.Resolve<MediaFileService>());
            Mocker.SetConstant<ITrackService>(Mocker.Resolve<TrackService>());
            Mocker.SetConstant<IReleaseService>(Mocker.Resolve<ReleaseService>());

            if (useStockDiskPipeline)
            {
                // Unlike the prior 12-track fixture, the selected path now uses
                // unmodified native Linux disk, mount and symlink services, the
                // real stock recycler and the real native track-file mover.
                // Naming/root/script/permissions remain controlled test inputs.
                Mocker.SetConstant<IProcMountProvider>(Mocker.Resolve<ProcMountProvider>());
                Mocker.SetConstant<ISymbolicLinkResolver>(Mocker.Resolve<SymbolicLinkResolver>());
                Mocker.SetConstant<IDiskProvider>(Mocker.Resolve<IDiskProvider>(FileSystemType.Actual));
                var nativeDiskTransfer = Mocker.Resolve<DiskTransferService>();
                if (!failAtSix)
                {
                    Mocker.SetConstant<IDiskTransferService>(nativeDiskTransfer);
                }
                else
                {
                    // Inject failure ONLY at the actual sixth file's native
                    // disk-transfer boundary, after stock upgrader has recycled
                    // its original and deleted its TrackFile from real SQLite.
                    // Every other transfer is executed by stock DiskTransferService.
                    Mocker.GetMock<IDiskTransferService>()
                        .Setup(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(),
                            It.IsAny<TransferMode>(), It.IsAny<bool>()))
                        .Returns((string source, string destination, TransferMode mode, bool overwrite) =>
                        {
                            if (source == Path.Combine(staging, "06-synthetic.mp3") &&
                                destination == physicalPaths[6])
                            {
                                throw new IOException("SYNTHETIC: actual sixth disk transfer interrupted after stock delete");
                            }

                            return nativeDiskTransfer.TransferFile(source, destination, mode, overwrite);
                        });
                }

                Mocker.GetMock<IConfigService>()
                    .SetupGet(x => x.RecycleBin)
                    .Returns(recycle);
                Mocker.SetConstant<IRecycleBinProvider>(Mocker.Resolve<RecycleBinProvider>());
                Mocker.GetMock<IBuildFileNames>()
                    .Setup(x => x.BuildTrackFilePath(
                        It.IsAny<List<Track>>(), It.IsAny<Artist>(), It.IsAny<Album>(),
                        It.IsAny<TrackFile>(), It.IsAny<string>(),
                        It.IsAny<NamingConfig>(), It.IsAny<List<CustomFormat>>()))
                    .Returns((List<Track> tracks, Artist unusedArtist, Album unusedAlbum,
                        TrackFile unusedFile, string unusedExt, NamingConfig unusedName,
                        List<CustomFormat> unusedFormats) =>
                            physicalPaths[tracks.Single().AbsoluteTrackNumber]);
                Mocker.GetMock<IRootFolderService>()
                    .Setup(x => x.GetBestRootFolder(It.IsAny<string>()))
                    .Returns(new RootFolder { Path = Path.Combine(temp, "library") });
                Mocker.GetMock<IImportScript>()
                    .Setup(x => x.TryImport(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalTrack>(),
                        It.IsAny<TrackFile>(), It.IsAny<TransferMode>(), null))
                    .Returns(ScriptImportDecision.DeferMove);
                Mocker.SetConstant<IMoveTrackFiles>(Mocker.Resolve<TrackFileMovingService>());
            }
            else
            {
                // Legacy test-controlled transfer and recycle callbacks are kept
                // for precise post-delete sixth-transfer fault injection.
                Mocker.GetMock<IDiskProvider>()
                    .Setup(x => x.GetParentFolder(It.IsAny<string>()))
                    .Returns<string>(Path.GetDirectoryName);
                Mocker.GetMock<IDiskProvider>()
                    .Setup(x => x.FileExists(It.IsAny<string>()))
                    .Returns<string>(File.Exists);
                Mocker.GetMock<IDiskProvider>()
                    .Setup(x => x.FolderExists(It.IsAny<string>()))
                    .Returns<string>(Directory.Exists);

                Mocker.GetMock<IRecycleBinProvider>()
                    .Setup(x => x.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                    .Callback<string, string>((path, subfolder) =>
                        File.Move(path, Path.Combine(recycle, Path.GetFileName(path))));
                Mocker.GetMock<IMoveTrackFiles>()
                    .Setup(x => x.MoveTrackFile(It.IsAny<TrackFile>(), It.IsAny<LocalTrack>()))
                    .Returns((TrackFile file, LocalTrack local) =>
                    {
                        var trackNumber = local.Tracks.Single().AbsoluteTrackNumber;
                        if (failAtSix && trackNumber == 6)
                        {
                            throw new IOException("SYNTHETIC: sixth file transfer failed after native old-row deletion");
                        }

                        var destination = physicalPaths[trackNumber];
                        File.Move(file.Path, destination);
                        file.Path = destination;
                        return file;
                    });
            }

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
            if (failAtSix && !preserveInterruptedCheckpoint)
            {
                // Under the normal multi-test runner the injected exception is
                // captured as a warning. A single isolated test invocation may
                // initialize the native logger without the same NLog capture;
                // assert the returned Skipped result and actual DB fault instead.
                ExceptionVerification.ExpectedWarns(1);
            }

            Assert.That(results.Count(x => x.Result == ImportResultType.Imported),
                Is.EqualTo(failAtSix ? Upgrades - 1 : Upgrades));
            Assert.That(results.Count(x => x.Result == ImportResultType.Skipped),
                Is.EqualTo(failAtSix ? 1 : 0));
            Assert.That(results.Count(x => x.Result == ImportResultType.Rejected), Is.EqualTo(1));
            Assert.That(Db.All<TrackFile>().Count, Is.EqualTo(failAtSix ? Total - 1 : Total));

            if (failAtSix)
            {
                // Exercise the unsafe intermediate state against *real* SQLite/bytes:
                // the old file was recycled and its TrackFile row is gone, while the
                // incoming sixth file is still in staging and lyrics were DB-unlinked.
                var orphan = Db.All<Track>().Single(x => x.AbsoluteTrackNumber == 6);
                if (realEventsDuringImport)
                {
                    // With actual synchronous stock deletion subscribers, the
                    // ghost reference should be cleared by TrackService.Handle.
                    Assert.That(orphan.TrackFileId, Is.EqualTo(0),
                        "Real stock TrackFileDeletedEvent did not detach track six.");
                    TestContext.Progress.WriteLine(
                        "M14_REAL_EVENT_FAULT_OBSERVED: stock deletion handlers detached track 6 " +
                        "before the scan; sixth TrackFile DB row gone and original bytes recycled.");
                }
                else
                {
                    Assert.That(orphan.TrackFileId, Is.EqualTo(previous[6].Id));
                }
                Assert.That(Db.All<TrackFile>().Any(x => x.Id == previous[6].Id), Is.False);
                Assert.That(File.Exists(physicalPaths[6]), Is.False);
                Assert.That(File.Exists(Path.Combine(staging, "06-synthetic.mp3")), Is.True);
                var sixthRecycled = useStockDiskPipeline
                    ? Path.Combine(recycle, "synthetic-artist", "synthetic-album", Path.GetFileName(physicalPaths[6]))
                    : Path.Combine(recycle, Path.GetFileName(physicalPaths[6]));
                Assert.That(File.ReadAllBytes(sixthRecycled), Is.EqualTo(oldBytes[6]));
                Assert.That(Db.All<LyricFile>().Any(x => x.TrackFileId == previous[6].Id), Is.False);
                Assert.That(File.ReadAllBytes(lyricPaths[6]), Is.EqualTo(lyricBytes[6]));

                if (scanBeforeRecovery)
                {
                    // The actual stock DiskScanService.Scan executes its own file enumeration,
                    // MediaFileTableCleanupService.Clean, media-file repository queries, and
                    // event dispatch before any recovery. We deliberately suppress *new import
                    // decisions* because the fixture files are not tagged audio. The native
                    // ImportApprovedTracks and core cleanup implementations are not mocked.
                    var linksBeforeScan = Db.All<Track>().ToDictionary(x => x.Id, x => x.TrackFileId);
                    var fileIdsBeforeScan = Db.All<TrackFile>().Select(x => x.Id).OrderBy(x => x).ToArray();
                    var lyricIdsBeforeScan = Db.All<LyricFile>().Select(x => x.Id).OrderBy(x => x).ToArray();
                    var originalMediaService = Mocker.Resolve<IMediaFileService>();
                    var originalEventAggregator = Mocker.Resolve<IEventAggregator>();

                    Mocker.GetMock<IServiceFactory>()
                        .Setup(x => x.BuildAll<IHandle<TrackFileDeletedEvent>>())
                        .Returns(new List<IHandle<TrackFileDeletedEvent>>
                        {
                            Mocker.Resolve<TrackService>(),
                            lyrics
                        });
                    Mocker.GetMock<IServiceFactory>()
                        .Setup(x => x.BuildAll<IHandle<ArtistScannedEvent>>())
                        .Returns(new List<IHandle<ArtistScannedEvent>>());
                    Mocker.GetMock<IServiceFactory>()
                        .Setup(x => x.BuildAll<IHandleAsync<IEvent>>())
                        .Returns(new List<IHandleAsync<IEvent>>());
                    Mocker.GetMock<IServiceFactory>()
                        .Setup(x => x.BuildAll<IHandleAsync<ArtistScannedEvent>>())
                        .Returns(new List<IHandleAsync<ArtistScannedEvent>>());
                    Mocker.GetMock<IServiceFactory>()
                        .Setup(x => x.BuildAll<IHandleAsync<TrackFileDeletedEvent>>())
                        .Returns(new List<IHandleAsync<TrackFileDeletedEvent>>());

                    var realBus = Mocker.Resolve<EventAggregator>();
                    Mocker.SetConstant<IEventAggregator>(realBus);
                    // Construct a NEW real media service after selecting the real bus,
                    // otherwise an already-resolved instance still holds the mocked bus.
                    Mocker.SetConstant<IMediaFileService>(new MediaFileService(
                        Mocker.Resolve<IMediaFileRepository>(), realBus,
                        NLog.LogManager.GetLogger("CrescendarrM14ScanBeforeRecovery")));
                    Mocker.SetConstant<IMediaFileTableCleanupService>(
                        Mocker.Resolve<MediaFileTableCleanupService>());

                    Mocker.GetMock<IArtistService>()
                        .Setup(x => x.GetArtists(It.IsAny<IEnumerable<int>>()))
                        .Returns(new List<Artist> { artist });
                    Mocker.GetMock<IMakeImportDecision>()
                        .Setup(x => x.GetImportDecisions(
                            It.IsAny<List<System.IO.Abstractions.IFileInfo>>(),
                            It.IsAny<IdentificationOverrides>(),
                            It.IsAny<ImportDecisionMakerInfo>(),
                            It.IsAny<ImportDecisionMakerConfig>()))
                        .Returns(new List<ImportDecision<LocalTrack>>());

                    // DO NOT restore track six first: this is precisely the missing-file
                    // scenario that can occur after a process crash or before plugin load.
                    Mocker.Resolve<DiskScanService>().Scan(
                        new List<string> { artist.Path },
                        FilterFilesType.Known, false, new List<int> { artist.Id });

                    Mocker.GetMock<IMakeImportDecision>()
                        .Verify(x => x.GetImportDecisions(
                            It.IsAny<List<System.IO.Abstractions.IFileInfo>>(),
                            It.IsAny<IdentificationOverrides>(),
                            It.IsAny<ImportDecisionMakerInfo>(),
                            It.IsAny<ImportDecisionMakerConfig>()), Times.Once());

                    var filesAfterScan = Db.All<TrackFile>().Select(x => x.Id).OrderBy(x => x).ToArray();
                    Assert.That(filesAfterScan, Is.EqualTo(fileIdsBeforeScan),
                        "Real stock scan altered track-file IDs while the sixth file was orphaned.");
                    foreach (var entry in linksBeforeScan)
                    {
                        Assert.That(Db.All<Track>().Single(x => x.Id == entry.Key).TrackFileId,
                            Is.EqualTo(entry.Value),
                            "Real stock scan changed a track association before recovery.");
                    }
                    Assert.That(Db.All<LyricFile>().Select(x => x.Id).OrderBy(x => x).ToArray(),
                        Is.EqualTo(lyricIdsBeforeScan),
                        "Real stock scan altered registered lyrics before recovery.");
                    Assert.That(File.Exists(physicalPaths[6]), Is.False,
                        "The missing sixth original was unexpectedly recreated by scan.");
                    Assert.That(File.ReadAllBytes(Path.Combine(staging, "06-synthetic.mp3")),
                        Is.EqualTo(incomingBytes[6]), "Stock rescan consumed the staged sixth file.");
                    for (var i = 1; i <= Total; i++)
                    {
                        Assert.That(File.ReadAllBytes(lyricPaths[i]), Is.EqualTo(lyricBytes[i]),
                            "Physical lyric changed during rescan for track " + i);
                    }
                    Assert.That(Db.All<AlbumRelease>().Single(x => x.Id == release.Id).Monitored, Is.True,
                        "Stock rescan switched the selected MusicBrainz release.");
                    TestContext.Progress.WriteLine(
                        "M14_SCAN_OBSERVED: real DiskScanService.Scan reached its decision stage; " +
                        "opaque-media decisions suppressed; sixth remains missing/" +
                        (realEventsDuringImport ? "unlinked by stock event" : "orphaned nonzero file ID") +
                        ", staged bytes intact, " +
                        "track-file IDs, track associations, lyrics and monitored release unchanged.");

                    // The existing test-only repair below is still performed and validated.
                    // Restore original services so the baseline recovery path remains
                    // comparable to the already-characterized M12 test.
                    Mocker.SetConstant<IMediaFileService>(originalMediaService);
                    Mocker.SetConstant<IEventAggregator>(originalEventAggregator);
                }

                if (preserveInterruptedCheckpoint)
                {
                    // Live SQLite online backup produces a self-contained snapshot
                    // that survives DbTest teardown and is reopened by another
                    // dotnet test process. No native files are touched outside the
                    // dedicated GitHub runner temporary path.
                    var snapshot = Path.Combine(restartRoot, "interrupted-lidarr.db");
                    using (var sourceConnection = (SQLiteConnection)Db.OpenConnection())
                    using (var targetConnection = new SQLiteConnection(
                        "Data Source=" + snapshot + ";Version=3;"))
                    {
                        targetConnection.Open();
                        sourceConnection.BackupDatabase(targetConnection, "main", "main", -1, null, 0);
                    }

                    // Write the ready marker LAST, after verifying backup bytes
                    // and a structurally readable SQLite snapshot.
                    var expected = Convert.ToHexString(SHA256.HashData(oldBytes[6]));
                    using (var probe = new SQLiteConnection("Data Source=" + snapshot + ";Version=3;"))
                    {
                        probe.Open();
                        using var query = new SQLiteCommand("PRAGMA integrity_check", probe);
                        Assert.That(query.ExecuteScalar()?.ToString(), Is.EqualTo("ok"));
                    }

                    File.WriteAllText(Path.Combine(restartRoot, "sixth-original.sha256"), expected);
                    File.WriteAllText(Path.Combine(restartRoot, "ready.marker"), "M12_INTERRUPTED_AT_SIX");
                    Assert.That(File.Exists(snapshot), Is.True);
                    return;
                }

                // In-process TEST RECOVERY, not an implemented Crescendarr journal:
                // copy from a separately verified operation-owned backup, then insert
                // a fresh native TrackFile and repair the native Track association.
                File.Copy(Path.Combine(backups, Path.GetFileName(physicalPaths[6])),
                    physicalPaths[6], overwrite: false);
                Assert.That(File.ReadAllBytes(physicalPaths[6]), Is.EqualTo(oldBytes[6]));

                var restored = new TrackFile
                {
                    Path = physicalPaths[6],
                    AlbumId = album.Id,
                    Size = oldBytes[6].LongLength,
                    Modified = File.GetLastWriteTimeUtc(physicalPaths[6]),
                    DateAdded = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.MP3_192)
                };
                Mocker.Resolve<IMediaFileService>().Add(restored);
                orphan.TrackFileId = restored.Id;
                Mocker.Resolve<ITrackService>().SetFileIds(new List<Track> { orphan });
                lyrics.Upsert(new LyricFile
                {
                    Id = 0,
                    ArtistId = artist.Id,
                    AlbumId = album.Id,
                    TrackFileId = restored.Id,
                    RelativePath = Path.Combine("synthetic-album", "06-synthetic.lrc"),
                    Extension = ".lrc"
                });

                Assert.That(Db.All<Track>().Single(x => x.Id == orphan.Id).TrackFileId,
                    Is.EqualTo(restored.Id));
                Assert.That(Db.All<LyricFile>().Any(x => x.TrackFileId == restored.Id), Is.True);
                Assert.That(restored.Id, Is.Not.EqualTo(previous[6].Id));
            }

            Assert.That(Db.All<TrackFile>().Count, Is.EqualTo(Total));

            var storedTracks = Db.All<Track>().ToDictionary(x => x.AbsoluteTrackNumber);
            var storedFiles = Db.All<TrackFile>().ToDictionary(x => x.Id);
            for (var i = 1; i <= Total; i++)
            {
                Assert.That(File.ReadAllBytes(lyricPaths[i]), Is.EqualTo(lyricBytes[i]));
                Assert.That(File.ReadAllBytes(Path.Combine(backups, Path.GetFileName(physicalPaths[i]))),
                    Is.EqualTo(oldBytes[i]));
                if (failAtSix && i == 6)
                {
                    // Sixth track was restored, not upgraded.
                    Assert.That(File.ReadAllBytes(physicalPaths[i]), Is.EqualTo(oldBytes[i]));
                    Assert.That(storedTracks[i].TrackFileId, Is.Not.EqualTo(previous[i].Id));
                    Assert.That(storedFiles.ContainsKey(previous[i].Id), Is.False);
                    Assert.That(storedFiles.ContainsKey(storedTracks[i].TrackFileId), Is.True);
                    Assert.That(Db.All<LyricFile>().Single(x => x.TrackFileId == storedTracks[i].TrackFileId).Id,
                        Is.GreaterThan(0));
                    Assert.That(File.Exists(Path.Combine(staging, "06-synthetic.mp3")), Is.True);
                }
                else if (i == Total)
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
                    var recycled = useStockDiskPipeline
                        ? Path.Combine(recycle, "synthetic-artist", "synthetic-album", Path.GetFileName(physicalPaths[i]))
                        : Path.Combine(recycle, Path.GetFileName(physicalPaths[i]));
                    Assert.That(File.ReadAllBytes(recycled), Is.EqualTo(oldBytes[i]));
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
            var actualReleaseRows = Db.All<AlbumRelease>().ToDictionary(x => x.Id);
            Assert.That(actualReleaseRows[release.Id].Monitored,
                Is.EqualTo(!incomingAlternateEdition),
                "Stock importer calls ReleaseService.SetMonitored on the first approved incoming release.");
            if (withCompetingEdition)
            {
                Assert.That(actualReleaseRows[alternateRelease.Id].Monitored,
                    Is.EqualTo(incomingAlternateEdition),
                    "The competing edition must be updated in real SQLite.");
                Assert.That(actualReleaseRows.Values.Count(x => x.Monitored), Is.EqualTo(1),
                    "Stock SetMonitored always leaves one monitored edition.");
            }

            Assert.That(Db.All<Track>().All(x => storedFiles.ContainsKey(x.TrackFileId)), Is.True);
            if (!useStockDiskPipeline)
            {
                Mocker.GetMock<IRecycleBinProvider>()
                    .Verify(x => x.DeleteFile(physicalPaths[Total], It.IsAny<string>()), Times.Never());
            }
        }
    }
}
