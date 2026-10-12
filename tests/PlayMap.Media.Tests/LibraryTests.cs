using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Sources;

namespace PlayMap.Media.Tests;

/// <summary>The library's file work: import (kind, facts, hash, progress), readiness (present, verified,
/// decodable, first frame), and opening the right source for an item.</summary>
public class LibraryTests
{
    private static string Copy(string clip)
    {
        var src = TestMedia.Clip(clip);
        var dst = Path.Combine(TestMedia.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(src));
        File.Copy(src, dst);
        return dst;
    }

    private sealed class Collect : IProgress<ImportProgress>
    {
        public readonly List<ImportProgress> Seen = [];

        public void Report(ImportProgress value)
        {
            lock (Seen)
                Seen.Add(value);
        }
    }

    [Fact]
    public async Task ImportingAClipGivesItsKindFactsAndHash()
    {
        var path = Copy("h264_25.mp4");
        var progress = new Collect();
        using var cache = new HashCache(null);
        var import = await MediaLibrary.ImportAsync(path, new ContentHasher(cache), progress);
        Assert.True(import.Ok, import.Message);
        Assert.Equal(MediaKind.Video, import.Kind);
        Assert.Equal(2_000_000_000, import.Info.DurationNs);
        Assert.Equal(await ContentHasher.ComputeAsync(path), import.ContentHash);
        Assert.Equal(new FileInfo(path).Length, import.SizeBytes);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), import.Name);
        Assert.Contains(progress.Seen, p => p.Stage == "probe");
        Assert.Contains(progress.Seen, p => p.Stage == "hash" && p.Fraction >= 1.0);
        Assert.Equal(new ImportProgress("done", 1), progress.Seen[^1]);

        var item = import.ToItem("md1");
        Assert.True(item.IsFile);
        Assert.Equal(MediaKind.Video, item.Kind);
        Assert.Equal(path, item.Location);
        Assert.Empty(item.Tags);
    }

    [Fact]
    public async Task ASequenceImportsAsOneItemWhoseHashIsItsFramesInOrder()
    {
        using var cache = new HashCache(null);
        var hasher = new ContentHasher(cache);
        var folder = TestMedia.Clip("seq");
        var a = await MediaLibrary.ImportAsync(folder, hasher);
        var b = await MediaLibrary.ImportAsync(Path.Combine(folder, "frame_%04d.png"), hasher);
        Assert.Equal(MediaKind.ImageSequence, a.Kind);
        Assert.True(ContentHasher.IsValid(a.ContentHash));
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal("seq", a.Name);
        Assert.Equal("frame", b.Name);
        Assert.Equal(Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length), a.SizeBytes);
    }

    [Fact]
    public async Task AFileThatCannotBePlayedStillImportsWithItsProblem()
    {
        using var cache = new HashCache(null);
        var import = await MediaLibrary.ImportAsync(Copy("unknown.avi"), new ContentHasher(cache));
        Assert.False(import.Ok);
        Assert.Equal(MediaProblem.DecoderNotAvailable, import.Problem);
        Assert.True(ContentHasher.IsValid(import.ContentHash));
        await Assert.ThrowsAsync<FileNotFoundException>(() => MediaLibrary.ImportAsync(Path.Combine(TestMedia.Dir, "gone.mp4"), new ContentHasher(cache)));
    }

    [Fact]
    public async Task ReadinessIsPresentVerifiedDecodableAndFirstFrameReady()
    {
        var path = Copy("h264_25.mp4");
        using var cache = new HashCache(null);
        var hasher = new ContentHasher(cache);
        var item = (await MediaLibrary.ImportAsync(path, hasher)).ToItem("md2");

        var ready = await MediaLibrary.CheckAsync(item, null, hasher);
        Assert.True(ready.Ready, ready.Problem);

        // With the source loaded, "first frame ready" is its preroll at the cue.
        using (var source = MediaSources.Open(item))
        {
            var cue = 1_000_000_000L;
            source.Prepare(cue, false, 1);
            Assert.True(((FfmpegVideoSource)source).WaitUntilReady(cue, TimeSpan.FromSeconds(10)));
            Assert.True((await MediaLibrary.CheckAsync(item, null, hasher, source, cue)).FirstFrameReady);
        }

        // Relative to the project folder.
        var relative = item with { Location = Path.GetFileName(path) };
        Assert.True((await MediaLibrary.CheckAsync(relative, TestMedia.Dir, hasher)).Ready);

        // Other bytes under the same name are not this media.
        await File.AppendAllTextAsync(path, "tamper");
        var changed = await MediaLibrary.CheckAsync(item, null, hasher);
        Assert.True(changed.Present);
        Assert.False(changed.HashVerified);
        Assert.False(changed.Ready);

        File.Delete(path);
        var missing = await MediaLibrary.CheckAsync(item, null, hasher);
        Assert.False(missing.Present);
        Assert.StartsWith("missing", missing.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnhashedOrUndecodableItemIsNotReady()
    {
        using var cache = new HashCache(null);
        var hasher = new ContentHasher(cache);
        var unhashed = (await MediaLibrary.ImportAsync(Copy("h264_25.mp4"), hasher)).ToItem("md3") with { ContentHash = "" };
        var r = await MediaLibrary.CheckAsync(unhashed, null, hasher);
        Assert.False(r.HashVerified);
        Assert.Equal("not hashed", r.Problem);

        var broken = (await MediaLibrary.ImportAsync(Copy("unknown.avi"), hasher)).ToItem("md4");
        var b = await MediaLibrary.CheckAsync(broken, null, hasher);
        Assert.True(b.HashVerified);
        Assert.False(b.Decodable);
        Assert.Contains("decoder not available", b.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachKindOpensItsSource()
    {
        using var cache = new HashCache(null);
        var hasher = new ContentHasher(cache);
        var clip = (await MediaLibrary.ImportAsync(TestMedia.Clip("h264_25.mp4"), hasher)).ToItem("v1");
        var still = (await MediaLibrary.ImportAsync(TestMedia.Clip("still.png"), hasher)).ToItem("i1");
        var seq = (await MediaLibrary.ImportAsync(TestMedia.Clip("seq"), hasher)).ToItem("s1");
        var sound = (await MediaLibrary.ImportAsync(TestMedia.Clip("tone48.wav"), hasher)).ToItem("a1");
        using (var s = MediaSources.Open(clip))
            Assert.IsType<FfmpegVideoSource>(s);
        using (var s = MediaSources.Open(still))
        {
            Assert.True(await ((ImageSource)s).Opened);
            Assert.Equal("i1", s.Key);
        }
        using (var s = MediaSources.Open(seq))
        {
            var sequence = Assert.IsType<ImageSequenceSource>(s);
            Assert.Equal(25, sequence.Rate.Numerator);
        }
        Assert.Throws<NotSupportedException>(() => MediaSources.Open(sound));
    }
}
