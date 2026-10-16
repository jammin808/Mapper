using PlayMap.Core.Model;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A folder camera and a picture still being written: a file a tethering tool has made but not yet filled (no bytes) is not a picture —
/// the camera waits for its bytes, then takes it once its size stands still.
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
            using var camera = new FolderCamera(rig, new FolderCameraOptions { Poll = TimeSpan.FromMilliseconds(10) });
            Assert.True(await camera.OpenAsync(CancellationToken.None), camera.Problem);

            // The tool makes the file, and fills it a while later (far longer than the settle time).
            var path = Path.Combine(folder, "shot-0001.tif");
            await File.WriteAllBytesAsync(path, []);
            var next = camera.NextAsync(0, TimeSpan.FromSeconds(20), CancellationToken.None);
            await Task.Delay(400);
            Assert.False(next.IsCompleted, "an empty file was taken as a picture");
            var samples = Enumerable.Range(0, 64 * 48).Select(i => (ushort)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(path, FolderCameraTests.Tiff(64, 48, samples, 8));

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
}
