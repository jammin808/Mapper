using FFmpeg.AutoGen;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;
using PlayMap.Tests;

namespace PlayMap.Media.Tests;

/// <summary>The loader finds FFmpeg 9 by the documented folders, checks the majors against the binding, and
/// says in words what is wrong with a folder that will not do; the seek path is bound as they load; FFmpeg's log reaches the
/// app, not stderr.</summary>
public class LoaderTests
{
    [Fact]
    public void TheLoadedLibrariesAreTheBindingsMajors()
    {
        TestMedia.Ensure();
        var status = FfmpegLoader.Status!;
        Assert.Equal(FfmpegLoader.ExpectedMajor("avcodec"), status.AvCodecMajor);
        Assert.Equal(FfmpegLoader.ExpectedMajor("avformat"), status.AvFormatMajor);
        Assert.Equal(FfmpegLoader.ExpectedMajor("avutil"), status.AvUtilMajor);
        Assert.Equal(63, status.AvCodecMajor);
        Assert.True(status.DevicesAvailable);
        Assert.True(status.FiltersAvailable);
        Assert.False(string.IsNullOrEmpty(status.Build));
        Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable)!), status.Folder);
        Assert.Equal(status.Folder, FfmpegLoader.CandidateFolders()[0]);
        Assert.True(FfmpegLoader.TryLoad(out var problem));
        Assert.Equal("", problem);
    }

    /// <summary>The binding binds an entry point on its first call, and that call allocates on its thread (av_gcd: called nowhere
    /// else). A source's first seek comes at a loop point or a seek, at any frame of a show, on a frame thread: the seek path is
    /// bound as the libraries load, each entry point FFmpeg's own function rather than the binder.</summary>
    [Fact]
    public void TheSeekPathIsBoundAsTheLibrariesLoad()
    {
        TestMedia.Ensure();
        long first = -1;
        var caller = new Thread(() =>
        {
            LockWaits.PrepareThisThread();
            var before = Allocations.Start();
            _ = ffmpeg.av_gcd(12, 18);
            first = Allocations.Since(before);
        });
        caller.Start();
        caller.Join();
        Assert.True(first > 0, "a first call binds its entry point");
        foreach (var name in FfmpegLoader.SeekPath)
        {
            // Bound, the delegate is the native function's own (its method is its type's Invoke); unbound, it is the binder's lambda.
            var entry = (Delegate)typeof(vectors).GetField(name)!.GetValue(null)!;
            Assert.True(entry.Method.DeclaringType == entry.GetType(), name + " is bound on its first call, not as the libraries load");
        }
    }

    [Fact]
    public void LibraryFileNamesFollowThePlatform()
    {
        var name = FfmpegLoader.LibraryFileName("avcodec", 63);
        if (OperatingSystem.IsWindows())
            Assert.Equal("avcodec-63.dll", name);
        else if (OperatingSystem.IsMacOS())
            Assert.Equal("libavcodec.63.dylib", name);
        else
            Assert.Equal("libavcodec.so.63", name);
    }

    [Fact]
    public void AFolderWithTheWrongMajorIsRefusedInWords()
    {
        TestMedia.Ensure();
        var dir = Path.Combine(TestMedia.Dir, "wrong-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.Contains("missing", FfmpegLoader.InspectFolder(dir), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, FfmpegLoader.LibraryFileName("avutil", 60)), "");
        var why = FfmpegLoader.InspectFolder(dir)!;
        Assert.Contains("wrong major version", why, StringComparison.Ordinal);
        Assert.Contains(FfmpegLoader.LibraryFileName("avutil", 61), why, StringComparison.Ordinal);
        Assert.Equal("no such folder", FfmpegLoader.InspectFolder(Path.Combine(dir, "absent")));
        Assert.Null(FfmpegLoader.InspectFolder(FfmpegLoader.Status!.Folder));
    }

    [Fact]
    public void FfmpegsWarningsReachTheLogNotStderr()
    {
        TestMedia.Ensure();
        var path = TestMedia.Clip("unknown.avi");
        var raised = new List<FfmpegLogLine>();
        void OnMessage(FfmpegLogLine line)
        {
            lock (raised)
                raised.Add(line);
        }
        FfmpegLog.Message += OnMessage;
        try
        {
            List<FfmpegLogLine> captured;
            using (FfmpegLog.Capture(out captured))
                MediaProbe.Probe(path);
            Assert.Contains(captured, l => l.Text.Contains("unknown codec", StringComparison.Ordinal));
            lock (raised)
                Assert.Contains(raised, l => l.Level <= 24 && l.Text.Contains("Could not find codec parameters", StringComparison.Ordinal));
            Assert.Contains(FfmpegLog.Recent(), l => l.Text.Contains("Could not find codec parameters", StringComparison.Ordinal));
        }
        finally
        {
            FfmpegLog.Message -= OnMessage;
        }
    }
}
