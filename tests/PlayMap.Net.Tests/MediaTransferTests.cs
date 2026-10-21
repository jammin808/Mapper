using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using PlayMap.Net.Http;
using PlayMap.Net.Media;
using PlayMap.Tests;

namespace PlayMap.Net.Tests;

/// <summary>Content-addressed pulls between nodes through the real web port: the manifest, a pull interrupted
/// mid-way that resumes from its chunk map and verifies, a file whose hash is not the one asked for kept nowhere,
/// the token sent in a header, the bandwidth bucket that can be lowered while a show plays, a pull that says how far it
/// is block by block, and a peer that falls silent — in its list or in a chunk — named within the quiet a try is given.</summary>
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
    public async Task APullSaysHowFarItIsAsItsBlocksLandAndThatItVerifiesOnceEveryByteIsIn()
    {
        // One chunk (under 8 MiB): its bytes are said as each 1 MiB block lands, not only when the chunk is whole.
        var (source, bytes) = MakeFile("clip.bin", 3 * 1024 * 1024 + 17);
        var store = new FakeMediaStore();
        var hash = store.Add(source);
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), new HttpHostOptions { Media = store });
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { ProgressEvery = TimeSpan.Zero });
        var said = new ConcurrentQueue<MediaProgress>();
        transfer.Progress += said.Enqueue;

        var result = await transfer.PullAsync(new Uri("http://127.0.0.1:" + host.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "out.bin"), CancellationToken.None);

        Assert.Equal(MediaPullOutcome.Verified, result.Outcome);
        const long MiB = MediaTransfer.BlockBytes;
        long size = bytes.Length;
        Assert.Equal(
            [(MiB, 0, MediaPullStage.Receiving), (2 * MiB, 0, MediaPullStage.Receiving), (3 * MiB, 0, MediaPullStage.Receiving), (size, 0, MediaPullStage.Receiving),
                (size, 1, MediaPullStage.Receiving), (size, 1, MediaPullStage.Verifying)],
            said.Select(p => (p.BytesDone, p.ChunksDone, p.Stage)));
    }

    [Fact]
    public async Task AChunkWhosePeerFallsSilentIsTriedAgainAndEachTryIsSaidWithItsWords()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hash, bytes, stallAfter: 64 * 1024);
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { Quiet = TimeSpan.FromSeconds(1), ChunkAttempts = 2 });
        var troubles = new ConcurrentQueue<MediaTrouble>();
        transfer.Trouble += troubles.Enqueue;

        // Its headers and 64 KiB come, then nothing: each try ends at the quiet, and the pull ends rather than waiting for ever.
        var result = await transfer.PullAsync(new Uri("http://127.0.0.1:" + peer.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "silent.bin"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MediaPullOutcome.Failed, result.Outcome);
        Assert.Equal(2, peer.FileAsks);
        var at = "127.0.0.1:" + peer.Port;
        Assert.Equal([(1, 2), (2, 2)], troubles.Select(t => (t.Attempt, t.Attempts)));
        Assert.All(troubles, t => Assert.StartsWith($"chunk 1 of 1 from {at}: sent nothing for 1 s, ", t.Words, StringComparison.Ordinal));
        Assert.StartsWith($"chunk 1 of 1 from {at} could not be had: sent nothing for 1 s, ", result.Words, StringComparison.Ordinal);
        Assert.EndsWith(" of 300000 bytes in — the finished chunks stay for the resume", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APeerThatNeverAnswersItsListIsGivenUpWithWordsThatSaySo()
    {
        var bytes = RandomNumberGenerator.GetBytes(1000);
        await using var peer = new FakeMediaPeer(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, answersList: false);
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { Quiet = TimeSpan.FromSeconds(1) });

        var refused = await Assert.ThrowsAsync<TimeoutException>(() => transfer.ManifestAsync(new Uri("http://127.0.0.1:" + peer.Port), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal($"127.0.0.1:{peer.Port} did not answer its media list in 1 s", refused.Message);
        Assert.Equal(1, peer.ListAsks);
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
