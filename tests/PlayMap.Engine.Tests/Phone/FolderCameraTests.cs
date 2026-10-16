using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Host;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// The Folder camera (wp2-multiscan; facade's night scans need it): a fake tether writes the simulator's picture of what is on the
/// glass into the folder as each cue comes, and the scan completes with the projector where the simulator hung it; a photo of the
/// wrong pattern is refused with words in STATE ("photo 17 shows pattern 16: re-shoot") and the scan waits for the right one; in
/// manual mode SCAN NEXT takes the newest picture; a 16-bit TIFF arrives as gray16. Pictures are read through FFmpeg.
/// </summary>
public sealed class FolderCameraTests(ITestOutputHelper output)
{
    private const string Rig = "cm0000f01d";

    /// <summary>A minimal uncompressed grey TIFF (8 or 16 bits a sample, little-endian), as a tethering tool writes.</summary>
    internal static byte[] Tiff(int width, int height, ReadOnlySpan<ushort> samples, int bits)
    {
        var bytesPerSample = bits / 8;
        var data = width * height * bytesPerSample;
        const int Entries = 9;
        var ifd = 8 + data;
        var file = new byte[ifd + 2 + Entries * 12 + 4];
        "II"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)ifd);
        for (var i = 0; i < width * height; i++)
        {
            if (bits == 8)
                file[8 + i] = (byte)samples[i];
            else
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8 + 2 * i), samples[i]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(ifd), Entries);
        var at = ifd + 2;
        void Entry(ushort tag, ushort type, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), 1);
            if (type == 3)
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 8), (ushort)value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 8), value);
            at += 12;
        }
        Entry(256, 4, (uint)width);
        Entry(257, 4, (uint)height);
        Entry(258, 3, (uint)bits);
        Entry(259, 3, 1);
        Entry(262, 3, 1);
        Entry(273, 4, 8);
        Entry(277, 3, 1);
        Entry(278, 4, (uint)height);
        Entry(279, 4, (uint)data);
        return file;
    }

    /// <summary>A picture written as a tethering tool does: beside, then renamed into place.</summary>
    private static void Drop(string folder, int n, byte[] gray, int width, int height)
    {
        var samples = gray.Select(b => (ushort)b).ToArray();
        var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"photo-{n:0000}.tif"));
        File.WriteAllBytes(path + ".part", Tiff(width, height, samples, 8));
        File.Move(path + ".part", path);
    }

    private static async Task AddRigAsync(PhoneScanRig rig, string folder, FolderAdvance advance)
    {
        var cam = rig.Truth.Camera;
        var r = await rig.Engine.EditAsync(PhoneScanRig.Desk, "the tethered camera", p => p.WithStage(s => s with
        {
            Cameras = s.Cameras.Add(new CameraRig(Rig, "Night camera", CameraSourceKind.Folder, folder, cam.Intrinsics, cam.Lens, cam.Pose, true)
            {
                Folder = new FolderCapture(folder, advance, 30),
            }),
        }));
        Assert.True(r.Changed, r.Effect);
    }

    [FfmpegFact]
    public async Task ATetheredFolderScansAProjectorAndAPhotoOfTheWrongPatternIsRefusedWithWords()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-tether-").FullName;
        PhoneScanRig? rigRef = null;
        var shot = 0;
        var wrongDone = false;
        byte[]? last = null;
        var messages = new ConcurrentQueue<string>();
        var tether = new ConcurrentQueue<Task>();
        void Shoot(PatternCue cue)
        {
            var rig = rigRef!;
            var (now, _) = rig.Display.Glass;
            var camera = rig.Camera;
            if (cue.Kind == "pattern" && cue.Index == 16 && !wrongDone && last is not null)
            {
                // The photographer shoots the pattern before again; once the node says so, the right one.
                wrongDone = true;
                var stale = last;
                Drop(folder, ++shot, camera.RenderRaster(stale, (ulong)shot), camera.CameraWidth, camera.CameraHeight);
                tether.Enqueue(Task.Run(async () =>
                {
                    Assert.True(await OpsRig.Eventually(() => messages.Contains("photo 17 shows pattern 16: re-shoot"), 30_000), "the wrong photo was not refused");
                    Drop(folder, Interlocked.Increment(ref shot), camera.RenderRaster(now, (ulong)shot), camera.CameraWidth, camera.CameraHeight);
                }));
                return;
            }
            last = now;
            Drop(folder, Interlocked.Increment(ref shot), camera.RenderRaster(now, (ulong)shot), camera.CameraWidth, camera.CameraHeight);
        }
        await using var rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions
        {
            PatternTimeout = TimeSpan.FromSeconds(5),
            OpenCamera = r => new FolderCamera(r, new FolderCameraOptions { Cued = Shoot, Poll = TimeSpan.FromMilliseconds(20) }),
        });
        rigRef = rig;
        rig.Runner.Progressed += v => messages.Enqueue(v.Message);
        try
        {
            await AddRigAsync(rig, folder, FolderAdvance.NewFile);
            await rig.Ok($"SCAN START {Rig} pj00000001 MODE FAST");
            var view = await rig.EndedAsync();
            Assert.True(view.State == "done", view.Message);
            await Task.WhenAll(tether);
            Assert.Contains("photo 17 shows pattern 16: re-shoot", messages);
            Assert.Equal("tag", view.Pairing!.Mode);
            var solved = Assert.Single(rig.Runner.Find(view.Id)!.Solution.Projectors);
            Assert.True(solved.Solved, solved.Words);
            var truth = rig.Truth.Projector(0);
            var (position, degrees) = ScanRunner.PoseError(solved.Pose, truth.Pose);
            var throwError = Math.Abs(solved.Intrinsics.ThrowRatio / truth.Intrinsics.ThrowRatio - 1);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{shot} photos; {position * 1000:0.0} mm, {degrees:0.000}°, throw {throwError:P2}"));
            Assert.True(position < 0.01 && degrees < 0.25 && throwError < 0.006, $"{position * 1000:0.0} mm, {degrees:0.000}°, {throwError:P2}");
            Assert.Equal("manual", view.Pairing.Exposure);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task AManualFolderTakesTheNewestPictureOnScanNext()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-manual-").FullName;
        PhoneScanRig? rigRef = null;
        var shot = 0;
        var nexts = new ConcurrentQueue<Task>();
        void Shoot(PatternCue cue)
        {
            var rig = rigRef!;
            var (now, _) = rig.Display.Glass;
            var n = Interlocked.Increment(ref shot);
            nexts.Enqueue(Task.Run(async () =>
            {
                // The person sees the pattern, shoots, and presses Next once the picture is in the folder.
                Assert.True(await OpsRig.Eventually(() => rig.Runner.View?.Message.EndsWith("then SCAN NEXT", StringComparison.Ordinal) == true));
                Drop(folder, n, rig.Camera.RenderRaster(now, (ulong)n), rig.Camera.CameraWidth, rig.Camera.CameraHeight);
                var next = await rig.Run("SCAN NEXT");
                Assert.True(next.Ok, next.Payload);
            }));
        }
        await using var rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions
        {
            OpenCamera = r => new FolderCamera(r, new FolderCameraOptions { Cued = Shoot, Poll = TimeSpan.FromMilliseconds(20) }),
        });
        rigRef = rig;
        try
        {
            await AddRigAsync(rig, folder, FolderAdvance.Manual);
            // Nothing waits on a picture yet: SCAN NEXT says so.
            Assert.Equal("no scan is waiting for a picture", (await rig.Run("SCAN NEXT")).Payload);
            await rig.Ok($"SCAN START {Rig} pj00000001 MODE FAST");
            var view = await rig.EndedAsync();
            Assert.True(view.State == "done", view.Message);
            await Task.WhenAll(nexts);
            Assert.Contains(rig.Engine.Journal.Recent(400), r => r.Text == "taken: the newest picture in the folder is the pattern's on the glass");
            Assert.True(Assert.Single(rig.Runner.Find(view.Id)!.Solution.Projectors).Solved);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task ASixteenBitTiffArrivesAsGray16()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-gray16-").FullName;
        try
        {
            var rig = new CameraRig(Rig, "Night camera", CameraSourceKind.Folder, folder, Intrinsics.FromFieldOfView(64, 48, 60), LensDistortion.None, Pose.Identity, false)
            {
                Folder = new FolderCapture(folder, FolderAdvance.NewFile, 10),
            };
            using var camera = Assert.IsType<FolderCamera>(ScanCameras.Open(null, rig));
            Assert.True(await camera.OpenAsync(CancellationToken.None), camera.Problem);
            var samples = Enumerable.Range(0, 64 * 48).Select(i => (ushort)(i * 21 % 65536)).ToArray();
            File.WriteAllBytes(Path.Combine(folder, "deep.tif"), Tiff(64, 48, samples, 16));
            var frame = await camera.NextAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.NotNull(frame);
            Assert.Equal((64, 48), (frame.Width, frame.Height));
            Assert.NotNull(frame.Gray16);
            Assert.Equal(samples, frame.Gray16);
            Assert.Equal(samples.Select(s => (byte)(s >> 8)), frame.Gray);
            Assert.Contains(frame.Gray16, s => s > 255);

            // A folder that is not there does not open, with words; a RAW file is refused with what to do.
            using var missing = new FolderCamera(rig with { Folder = new FolderCapture(Path.Combine(folder, "nope"), FolderAdvance.NewFile, 10) });
            Assert.False(await missing.OpenAsync(CancellationToken.None));
            Assert.Contains("does not exist", missing.Problem, StringComparison.Ordinal);
            var raw = Assert.Throws<Media.Ffmpeg.MediaOpenException>(() => Media.Stills.StillImageReader.Read(Path.Combine(folder, "shot.CR2")));
            Assert.Contains("shoot JPEG or TIFF", raw.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
