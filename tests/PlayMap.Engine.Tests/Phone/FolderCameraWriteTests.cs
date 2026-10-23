using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A folder camera and pictures that are not yet, or not the right, picture: a file a tethering tool has made but not yet filled (no bytes)
/// is not a picture — the camera waits for its bytes, then takes it once its size stands still for the settle time, counted on the clock
/// the test holds, so a file written in place is taken only once it is whole; photos of another size than the camera rig's fail the scan
/// at the first one with the size to shoot, before anything is solved or recorded.
/// </summary>
public sealed class FolderCameraWriteTests
{
    [FfmpegFact]
    public async Task AnEmptyFileIsWaitedForUntilItsPictureIsWritten()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-writing-").FullName;
        try
        {
            var rig = new CameraRig("cm0000f01d", "Night camera", CameraSourceKind.Folder, folder, Intrinsics.FromFieldOfView(64, 48, 60), LensDistortion.None,
                Pose.Identity, false)
            {
                Folder = new FolderCapture(folder, FolderAdvance.NewFile, 20),
            };
            var time = new ManualTime();
            using var camera = new FolderCamera(rig, new FolderCameraOptions { Poll = TimeSpan.FromMilliseconds(10), Time = time });
            Assert.True(await camera.OpenAsync(CancellationToken.None), camera.Problem);

            // The tool makes the file, and fills it a while later (far longer than the settle time).
            var path = Path.Combine(folder, "shot-0001.tif");
            await File.WriteAllBytesAsync(path, []);
            var next = camera.NextAsync(0, TimeSpan.FromSeconds(20), CancellationToken.None);
            await Task.Delay(400);
            Assert.False(next.IsCompleted, "an empty file was taken as a picture");
            var samples = Enumerable.Range(0, 64 * 48).Select(i => (ushort)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(path, FolderCameraTests.Tiff(64, 48, samples, 8));

            // The file is whole: the settle time passes on the test's clock, as often as the camera begins one, until it takes it.
            var settle = TimeSpan.FromMilliseconds(20);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!next.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(20))
            {
                time.Advance(settle);
                await Task.WhenAny(next, Task.Delay(10));
            }
            var frame = await next;
            Assert.NotNull(frame);
            Assert.Equal((64, 48), (frame.Width, frame.Height));
            Assert.Equal(samples.Select(s => (byte)s), frame.Gray);
            Assert.Equal("", camera.Problem);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task PhotosOfAnotherSizeFailTheScanWithTheSizeToShoot()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-size-").FullName;
        PhoneScanRig? rigRef = null;
        var shot = 0;
        void Shoot(PatternCue cue)
        {
            var rig = rigRef!;
            var (w, h) = (rig.Camera.CameraWidth / 2, rig.Camera.CameraHeight / 2);
            FolderCameraTests.Drop(folder, Interlocked.Increment(ref shot), new byte[w * h], w, h);
        }
        await using var rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions
        {
            PatternTimeout = TimeSpan.FromSeconds(5),
            OpenCamera = r => new FolderCamera(r, new FolderCameraOptions { Cued = Shoot, Poll = TimeSpan.FromMilliseconds(20) }),
        });
        rigRef = rig;
        try
        {
            await FolderCameraTests.AddRigAsync(rig, folder, FolderAdvance.NewFile);
            var k = rig.Engine.Store.Project.Stage.Cameras.Find(FolderCameraTests.Rig)!.Intrinsics;
            await rig.Ok($"SCAN START {FolderCameraTests.Rig} pj00000001 MODE FAST");
            var view = await rig.EndedAsync();
            Assert.Equal("failed", view.State);
            Assert.Equal(1, Volatile.Read(ref shot));
            Assert.Empty(rig.Engine.Store.Project.Stage.Scans);
            var (w, h) = (rig.Camera.CameraWidth / 2, rig.Camera.CameraHeight / 2);
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"sends {w}×{h} pictures"), view.Message, StringComparison.Ordinal);
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"shoot at {k.Width}×{k.Height}"), view.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
