using System.Diagnostics;
using System.Security.Cryptography;
using PlayMap.Net.Http;
using PlayMap.Net.Media;

namespace PlayMap.Net.Tests;

/// <summary>Content-addressed pulls between nodes through the real web port: the manifest, a pull interrupted
/// mid-way that resumes from its chunk map and verifies, a file whose hash is not the one asked for kept nowhere,
/// the token sent in a header, and the bandwidth bucket that can be lowered while a show plays.</summary>
public sealed class MediaTransferTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("playmap-transfer-");

    public void Dispose() => _dir.Delete(recursive: true);

    private (string Path, byte[] Bytes) MakeFile(string name, int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    [Fact]
    public async Task AnInterruptedPullResumesFromItsChunkMapAndVerifies()
    {
        var (source, bytes) = MakeFile("clip.mov", 5 * 1024 * 1024 + 12345);
        var store = new FakeMediaStore();
        var hash = store.Add(source);
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: false), new HttpHostOptions { Media = store });
        var peer = new Uri("http://127.0.0.1:" + host.Port);
        using var http = new HttpClient();
        var options = new MediaTransferOptions { ChunkBytes = 256 * 1024, Parallelism = 2, Token = TestSettings.Token };
        var destination = Path.Combine(_dir.FullName, "pulled", "clip.mov");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var manifest = await new MediaTransfer(http, new TokenBucket(0), options).ManifestAsync(peer, CancellationToken.None);
        var entry = Assert.Single(manifest);
        Assert.Equal((hash, (long)bytes.Length, "clip.mov"), (entry.Hash, entry.Size, entry.Name));

        var first = new MediaTransfer(http, new TokenBucket(0), options);
        using var interrupt = new CancellationTokenSource();
        first.Progress += p =>
        {
            if (p.ChunksDone >= 6)
                interrupt.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.PullAsync(peer, hash, bytes.Length, destination, interrupt.Token));
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".part"));
        Assert.True(File.Exists(destination + ".part.map"));

        var progress = new List<MediaProgress>();
        var second = new MediaTransfer(http, new TokenBucket(0), options);
        second.Progress += p =>
        {
            lock (progress)
                progress.Add(p);
        };
        var result = await second.PullAsync(peer, hash, bytes.Length, destination, CancellationToken.None);
        Assert.Equal(MediaPullOutcome.Verified, result.Outcome);
        Assert.InRange(result.ChunksResumed, 6, 21);
        Assert.Equal(21, result.ChunksResumed + result.ChunksFetched);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.map"));
        Assert.Equal(bytes.Length, progress.Max(p => p.BytesDone));

        var again = await second.PullAsync(peer, hash, bytes.Length, destination, CancellationToken.None);
        Assert.Equal(MediaPullOutcome.AlreadyHere, again.Outcome);
    }

    [Fact]
    public async Task AFileWhoseHashIsNotTheOneAskedForIsKeptNowhere()
    {
        var (source, bytes) = MakeFile("liar.bin", 300_000);
        var claimed = Convert.ToHexStringLower(SHA256.HashData("something else"u8));
        var store = new FakeMediaStore();
        store.Add(source, claimed);
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), new HttpHostOptions { Media = store });
        using var http = new HttpClient();
        var destination = Path.Combine(_dir.FullName, "out.bin");
        var result = await new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { ChunkBytes = 64 * 1024 })
            .PullAsync(new Uri("http://127.0.0.1:" + host.Port), claimed, bytes.Length, destination, CancellationToken.None);
        Assert.Equal(MediaPullOutcome.HashMismatch, result.Outcome);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.map"));
    }

    [Fact]
    public async Task APullWithoutTheTokenFailsAndKeepsWhatItHas()
    {
        var (source, bytes) = MakeFile("private.bin", 200_000);
        var store = new FakeMediaStore();
        var hash = store.Add(source);
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: false), new HttpHostOptions { Media = store });
        using var http = new HttpClient();
        var result = await new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { ChunkBytes = 64 * 1024, ChunkAttempts = 1 })
            .PullAsync(new Uri("http://127.0.0.1:" + host.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "nope.bin"), CancellationToken.None);
        Assert.Equal(MediaPullOutcome.Failed, result.Outcome);
        Assert.Contains("resume", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBandwidthBucketHoldsItsRateAndCanBeLoweredAndLifted()
    {
        var bucket = new TokenBucket(2 * 1024 * 1024);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 24; i++)
            await bucket.TakeAsync(64 * 1024, CancellationToken.None);   // 1.5 MiB at 2 MiB/s, a quarter-second burst
        Assert.InRange(sw.ElapsedMilliseconds, 350, 3000);

        bucket.SetRate(0);
        sw.Restart();
        for (var i = 0; i < 100; i++)
            await bucket.TakeAsync(1024 * 1024, CancellationToken.None);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 200);
        Assert.Equal(0, bucket.BytesPerSecond);
    }
}
