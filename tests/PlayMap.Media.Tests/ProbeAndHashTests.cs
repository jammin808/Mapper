using System.Diagnostics;
using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Tests;

/// <summary>The probe (kind, exact facts, decodability proved by a decoded picture, problems in words) and the
/// content hash (equal to sha256sum, cached by path, size and write time, cancellable).</summary>
public class ProbeAndHashTests
{
    [Fact]
    public async Task TheProbeReadsExactDurationSizeAndRationalRate()
    {
        var r = await MediaProbe.ProbeAsync(TestMedia.Clip("h264_25.mp4"));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(MediaKind.Video, r.Kind);
        Assert.True(r.Decodable);
        Assert.Equal(2_000_000_000, r.Info.DurationNs);
        Assert.Equal((320, 240), (r.Info.Width, r.Info.Height));
        Assert.Equal("25/1", r.Info.FrameRate);
        Assert.Equal("h264", r.Info.VideoCodec);
        Assert.Equal("yuv420p", r.Info.PixelFormat);
        Assert.Equal("mp4", r.Info.Container);
        Assert.Equal(50, r.FrameCount);
        Assert.StartsWith("h264", r.Decoder, StringComparison.Ordinal);
        Assert.False(r.Info.HasAlpha);
    }

    [Fact]
    public async Task TheProbeKeepsNtscRatesRational()
    {
        var r = await MediaProbe.ProbeAsync(TestMedia.Clip("h264_2997.mp4"));
        Assert.Equal("30000/1001", r.Info.FrameRate);
        Assert.Equal(2_002_000_000, r.Info.DurationNs);
        Assert.Equal(60, r.FrameCount);
    }

    [Fact]
    public async Task TheProbeTellsAStillFromAClip()
    {
        var r = await MediaProbe.ProbeAsync(TestMedia.Clip("still.png"));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(MediaKind.Image, r.Kind);
        Assert.Equal((64, 48), (r.Info.Width, r.Info.Height));
        Assert.Equal("png", r.Info.VideoCodec);
        Assert.True(r.Info.HasAlpha);
        Assert.Equal(0, r.Info.DurationNs);
        Assert.Equal(1, r.FrameCount);
    }

    [Fact]
    public async Task TheProbeReadsAFolderOrAPatternAsASequence()
    {
        var folder = TestMedia.Clip("seq");
        foreach (var location in new[] { folder, Path.Combine(folder, "frame_%04d.png"), Path.Combine(folder, "frame_####.png") })
        {
            var r = await MediaProbe.ProbeAsync(location);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(MediaKind.ImageSequence, r.Kind);
            Assert.Equal(12, r.FrameCount);
            Assert.Equal(480_000_000, r.Info.DurationNs);
            Assert.Equal("25/1", r.Info.FrameRate);
            Assert.Equal((160, 120), (r.Info.Width, r.Info.Height));
            Assert.Equal("sequence", r.Info.Container);
        }
        var at30 = await MediaProbe.ProbeSequenceAsync(folder, Core.Time.FrameRate.Fps30);
        Assert.Equal(400_000_000, at30.Info.DurationNs);
    }

    [Fact]
    public async Task TheProbeReadsAudioAlone()
    {
        var r = await MediaProbe.ProbeAsync(TestMedia.Clip("tone48.wav"));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(MediaKind.Audio, r.Kind);
        Assert.Equal("pcm_s16le", r.Info.AudioCodec);
        Assert.Equal(2, r.Info.AudioChannels);
        Assert.Equal(48000, r.Info.AudioRate);
        Assert.Equal(2_000_000_000, r.Info.DurationNs);
        Assert.Equal("", r.Info.VideoCodec);
    }

    [Fact]
    public async Task TheProbeReadsPictureAndSoundTogether()
    {
        var r = await MediaProbe.ProbeAsync(TestMedia.Clip("av.mp4"));
        Assert.Equal(MediaKind.Video, r.Kind);
        Assert.Equal(("h264", "aac", 2, 48000), (r.Info.VideoCodec, r.Info.AudioCodec, r.Info.AudioChannels, r.Info.AudioRate));
    }

    [Fact]
    public async Task TheProbeReportsTenBitsAndAlpha()
    {
        Assert.Equal("yuv420p10le", (await MediaProbe.ProbeAsync(TestMedia.Clip("hevc10.mp4"))).Info.PixelFormat);
        var alpha = await MediaProbe.ProbeAsync(TestMedia.Clip("prores4444a.mov"));
        Assert.True(alpha.Info.HasAlpha);
        Assert.Equal("prores", alpha.Info.VideoCodec);
        Assert.Equal("mov", alpha.Info.Container);
    }

    [Fact]
    public async Task ACodecWithoutADecoderIsReportedInWords()
    {
        var audio = await MediaProbe.ProbeAsync(TestMedia.Clip("notag.wav"));
        Assert.Equal(MediaKind.Audio, audio.Kind);
        Assert.False(audio.Decodable);
        Assert.Equal(MediaProblem.DecoderNotAvailable, audio.Problem);
        Assert.Contains("decoder not available", audio.Message, StringComparison.Ordinal);

        var video = await MediaProbe.ProbeAsync(TestMedia.Clip("unknown.avi"));
        Assert.Equal(MediaKind.Video, video.Kind);
        Assert.Equal(MediaProblem.DecoderNotAvailable, video.Problem);
        Assert.Equal((320, 240), (video.Info.Width, video.Info.Height));
    }

    [Fact]
    public async Task AMissingFileCannotBeOpened()
    {
        var r = await MediaProbe.ProbeAsync(Path.Combine(TestMedia.Dir, "nothing-here.mov"));
        Assert.Equal(MediaProblem.CannotOpen, r.Problem);
        Assert.Equal(MediaKind.Video, r.Kind);
        Assert.False(r.Decodable);
    }

    [Fact]
    public async Task EveryTestCodecIsDecodableHere()
    {
        foreach (var clip in new[] { "hevc.mp4", "vp9.webm", "av1.mkv", "prores.mov", "mjpeg.mov", "still.exr", "still.jpg", "still16.png", "tone.m4a" })
        {
            var r = await MediaProbe.ProbeAsync(TestMedia.Clip(clip));
            // A build missing one of these decoders says which; this build has them all.
            Assert.True(r.Decodable, clip + ": " + r.Message);
        }
    }

    [Fact]
    public async Task TheHashIsTheSha256OfTheFile()
    {
        TestMedia.Ensure();
        var path = TestMedia.Clip("h264_long.mp4");
        var hash = await ContentHasher.ComputeAsync(path);
        Assert.True(ContentHasher.IsValid(hash));
        var psi = new ProcessStartInfo("sha256sum") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        var line = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal("sha256:" + line.Split(' ')[0], hash);
    }

    [Fact]
    public async Task AnUnchangedFileIsHashedOnceAndTheCacheSurvivesARestart()
    {
        var file = Path.Combine(TestMedia.Dir, "cache-" + Guid.NewGuid().ToString("N") + ".json");
        var clip = Path.Combine(TestMedia.Dir, "copy-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.Copy(TestMedia.Clip("h264_25.mp4"), clip);
        string first;
        using (var cache = new HashCache(file))
        {
            var hasher = new ContentHasher(cache);
            var reports = new List<double>();
            first = await hasher.HashAsync(clip, new SyncProgress(reports.Add));
            Assert.Equal(1.0, reports[^1]);
            Assert.Equal(first, await hasher.HashAsync(clip));
            Assert.Equal(1, cache.Hits);
        }
        using (var reloaded = new HashCache(file))
        {
            Assert.Equal(1, reloaded.Count);
            Assert.Equal(first, await new ContentHasher(reloaded).HashAsync(clip));
            Assert.Equal(1, reloaded.Hits);

            // An edit changes the size and the write time: hashed again, to a different hash.
            await File.AppendAllTextAsync(clip, "x");
            var second = await new ContentHasher(reloaded).HashAsync(clip);
            Assert.NotEqual(first, second);
            Assert.Equal(1, reloaded.Hits);
        }
    }

    [Fact]
    public async Task ACancelledHashStops()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContentHasher.ComputeAsync(TestMedia.Clip("h264_long.mp4"), null, cts.Token));
    }

    [Fact]
    public void OnlyWellFormedHashesAreValid()
    {
        Assert.True(ContentHasher.IsValid("sha256:" + new string('a', 64)));
        Assert.False(ContentHasher.IsValid("sha256:" + new string('A', 64)));
        Assert.False(ContentHasher.IsValid("sha1:" + new string('a', 64)));
        Assert.False(ContentHasher.IsValid("sha256:" + new string('a', 63)));
        Assert.False(ContentHasher.IsValid(null));
    }

    /// <summary>Progress delivered on the reporting thread (Progress&lt;T&gt; posts to a context; a test reads
    /// the list right after the await).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
