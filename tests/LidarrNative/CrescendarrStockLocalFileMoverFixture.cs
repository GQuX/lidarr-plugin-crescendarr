// Real stock Lidarr TrackFileMovingService, RecycleBinProvider and
// DiskTransferService on disposable local runner filesystem with valid PCM WAV.
// Database, naming policy, script hook and metadata side effects are mocked.
// No user paths, SMB mounts, real downloads, or production server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common.AutoMoq;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrStockLocalFileMoverFixture : CoreTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void stock_native_recycle_and_move_operate_on_real_local_wav_and_reject_existing_target(bool existingDestination)
        {
            var root = Path.Combine(TempFolder, "crescendarr-m12-local-stock-io");
            var artistDir = Path.Combine(root, "library", "synthetic-artist");
            var albumDir = Path.Combine(artistDir, "synthetic-album");
            var staged = Path.Combine(root, "staging");
            var recycleRoot = Path.Combine(root, "recycle");
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(staged);
            Directory.CreateDirectory(recycleRoot);

            var oldPath = Path.Combine(albumDir, "01-synthetic.wav");
            var sourcePath = Path.Combine(staged, "01-synthetic-new.wav");
            var protectedPath = Path.Combine(albumDir, "12-better.wav");
            var lyricPath = Path.Combine(albumDir, "01-synthetic.lrc");

            WriteValidWave(oldPath, 700);
            WriteValidWave(sourcePath, 1800);
            WriteValidWave(protectedPath, 2500);
            File.WriteAllText(lyricPath, "[00:00.00] Disposable synthetic lyric");
            var originalBytes = File.ReadAllBytes(oldPath);
            var upgradedBytes = File.ReadAllBytes(sourcePath);
            var protectedHash = SHA256.HashData(File.ReadAllBytes(protectedPath));
            var lyricBytes = File.ReadAllBytes(lyricPath);
            Assert.That(originalBytes, Is.Not.EqualTo(upgradedBytes));
            Assert.That(originalBytes.Length, Is.EqualTo(1644),
                "One tenth second of 8 kHz 16-bit mono PCM plus RIFF header.");

            // Explicitly opt into the upstream harness's REAL platform disk
            // provider; unlike all prior Crescendarr byte-transfer callbacks,
            // these calls execute stock DiskTransferService and stock recycler.
            var physicalDisk = Mocker.Resolve<IDiskProvider>(FileSystemType.Actual);
            Mocker.SetConstant<IDiskProvider>(physicalDisk);
            var nativeTransfer = Mocker.Resolve<DiskTransferService>();
            Mocker.SetConstant<IDiskTransferService>(nativeTransfer);

            Mocker.GetMock<NzbDrone.Core.Configuration.IConfigService>()
                .SetupGet(x => x.RecycleBin)
                .Returns(recycleRoot);
            var nativeRecycle = Mocker.Resolve<RecycleBinProvider>();

            var artist = new Artist { Id = 1, Path = artistDir };
            var album = new Album { Id = 2, Title = "Synthetic Audio Album" };
            var track = new Track { Id = 3, AbsoluteTrackNumber = 1, TrackNumber = "1" };
            var localTrack = new LocalTrack
            {
                Artist = artist,
                Album = album,
                Tracks = new List<Track> { track },
                Path = sourcePath
            };
            var incomingFile = new TrackFile
            {
                Path = sourcePath,
                AlbumId = album.Id,
                Size = upgradedBytes.LongLength,
                DateAdded = DateTime.UtcNow
            };

            // Use the native mover and actual transfer, but control the naming
            // rule, root-folder lookup, and external script hook so this is a
            // filesystem contract test rather than an end-to-end import test.
            Mocker.GetMock<IBuildFileNames>()
                .Setup(x => x.BuildTrackFilePath(
                    It.IsAny<List<Track>>(), It.IsAny<Artist>(), It.IsAny<Album>(),
                    It.IsAny<TrackFile>(), It.IsAny<string>(),
                    It.IsAny<NamingConfig>(), It.IsAny<List<CustomFormat>>()))
                .Returns(oldPath);
            Mocker.GetMock<IRootFolderService>()
                .Setup(x => x.GetBestRootFolder(artistDir))
                .Returns(new RootFolder { Path = Path.Combine(root, "library") });
            Mocker.GetMock<IImportScript>()
                .Setup(x => x.TryImport(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalTrack>(),
                    It.IsAny<TrackFile>(), It.IsAny<TransferMode>(), null))
                .Returns(ScriptImportDecision.DeferMove);

            var nativeMover = Mocker.Resolve<TrackFileMovingService>();
            if (existingDestination)
            {
                Assert.Throws<DestinationAlreadyExistsException>(
                    () => nativeMover.MoveTrackFile(incomingFile, localTrack));
                Assert.That(File.ReadAllBytes(oldPath), Is.EqualTo(originalBytes));
                Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(upgradedBytes));
                Assert.That(incomingFile.Path, Is.EqualTo(sourcePath));
                Assert.That(Directory.GetFiles(recycleRoot, "*", SearchOption.AllDirectories), Is.Empty);
            }
            else
            {
                var recycleRelative = Path.Combine("synthetic-artist", "synthetic-album");
                nativeRecycle.DeleteFile(oldPath, recycleRelative);
                var recycledPath = Path.Combine(recycleRoot, recycleRelative, "01-synthetic.wav");
                Assert.That(File.Exists(oldPath), Is.False);
                Assert.That(File.ReadAllBytes(recycledPath), Is.EqualTo(originalBytes));

                var moved = nativeMover.MoveTrackFile(incomingFile, localTrack);
                Assert.That(moved.Path, Is.EqualTo(oldPath));
                Assert.That(File.ReadAllBytes(oldPath), Is.EqualTo(upgradedBytes));
                Assert.That(File.Exists(sourcePath), Is.False);
                Assert.That(File.ReadAllBytes(recycledPath), Is.EqualTo(originalBytes));
            }

            Assert.That(SHA256.HashData(File.ReadAllBytes(protectedPath)),
                Is.EqualTo(protectedHash), "Never touch unrelated better original.");
            Assert.That(File.ReadAllBytes(lyricPath), Is.EqualTo(lyricBytes),
                "The physical .lrc should not be touched by a direct audio move.");
        }

        private static void WriteValidWave(string path, short sample)
        {
            // Minimal valid PCM WAVE: RIFF/WAVE, fmt 16-bit mono 8000 Hz,
            // and 800 constant samples. No external media software or network.
            const int samples = 800;
            const int dataBytes = samples * sizeof(short);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);
            for (var i = 0; i < samples; i++)
            {
                writer.Write(sample);
            }
        }
    }
}
