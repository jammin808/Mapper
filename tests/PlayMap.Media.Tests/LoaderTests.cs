using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Tests;

/// <summary>The loader finds FFmpeg 9 by the documented folders, checks the majors against the binding, and
/// says in words what is wrong with a folder that will not do; FFmpeg's log reaches the app, not stderr.</summary>
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
        Assert.True(Path.IsPathFullyQualified(status.Folder), status.Folder + " is not an absolute path");
        // Found where the absolute PLAYMAP_FFMPEG_DIR the tests set points: nothing was refused on the way.
        Assert.Equal("", status.Refused);
        Assert.True(FfmpegLoader.TryLoad(out var problem));
        Assert.Equal("", problem);
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
