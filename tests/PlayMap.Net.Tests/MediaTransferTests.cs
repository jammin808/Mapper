using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using PlayMap.Core.Time;
using PlayMap.Net.Http;
using PlayMap.Net.Media;
using PlayMap.Tests;

namespace PlayMap.Net.Tests;

/// <summary>Content-addressed pulls between nodes through the real web port: the manifest, a pull interrupted
/// mid-way that resumes from its chunk map, says the bytes it keeps as it starts and verifies, a file whose hash is not the one asked
/// for kept nowhere, the token sent in a header, the bandwidth bucket that can be lowered while a show plays, a pull that says how far
/// it is at every block and whose bytes never step back, a peer whose body falls silent for the quiet on the transfer's clock named at
/// each try while the bucket's waits are not counted, and a list or an answer that comes late or never left to the client's own
/// timeout.</summary>
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
        // Its first word, before any chunk is asked for, is the chunks it keeps.
        Assert.Equal((256L * 1024 * result.ChunksResumed, result.ChunksResumed, result.ChunksResumed), (progress[0].BytesDone, progress[0].ChunksDone, progress[0].ChunksResumed));
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
        // One chunk (under 8 MiB), as the node pulls it: its start is said, then its bytes as each 1 MiB block lands however close
        // together they come, not only when the chunk is whole.
        var (source, bytes) = MakeFile("clip.bin", 3 * 1024 * 1024 + 17);
        var store = new FakeMediaStore();
        var hash = store.Add(source);
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), new HttpHostOptions { Media = store });
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0));
        var said = new ConcurrentQueue<MediaProgress>();
        transfer.Progress += said.Enqueue;

        var result = await transfer.PullAsync(new Uri("http://127.0.0.1:" + host.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "out.bin"), CancellationToken.None);

        Assert.Equal(MediaPullOutcome.Verified, result.Outcome);
        const long MiB = MediaTransfer.BlockBytes;
        long size = bytes.Length;
        Assert.Equal(
            [(0, 0, MediaPullStage.Receiving), (MiB, 0, MediaPullStage.Receiving), (2 * MiB, 0, MediaPullStage.Receiving), (3 * MiB, 0, MediaPullStage.Receiving),
                (size, 0, MediaPullStage.Receiving), (size, 1, MediaPullStage.Receiving), (size, 1, MediaPullStage.Verifying)],
            said.Select(p => (p.BytesDone, p.ChunksDone, p.Stage)));
    }

    [Fact]
    public async Task AChunkWhosePeerFallsSilentIsTriedAgainAndEachTryIsSaidWithItsWords()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hash, bytes, stallAfter: 64 * 1024);
        var clock = new ManualClock(Base);
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { Quiet = Quiet, ChunkAttempts = 2 }, clock: clock);
        var troubles = new ConcurrentQueue<MediaTrouble>();
        transfer.Trouble += troubles.Enqueue;

        // Its headers and 64 KiB come, then nothing: each try ends once the transfer's clock has counted the quiet, and the pull ends
        // rather than waiting for ever.
        var result = await DriveAsync(clock, transfer.PullAsync(new Uri("http://127.0.0.1:" + peer.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "silent.bin"),
            CancellationToken.None));

        Assert.Equal(MediaPullOutcome.Failed, result.Outcome);
        Assert.Equal(2, peer.FileAsks);
        var at = "127.0.0.1:" + peer.Port;
        Assert.Equal([(1, 2), (2, 2)], troubles.Select(t => (t.Attempt, t.Attempts)));
        Assert.All(troubles, t => Assert.StartsWith($"chunk 1 of 1 from {at}: sent nothing for 1 s, ", t.Words, StringComparison.Ordinal));
        Assert.StartsWith($"chunk 1 of 1 from {at} could not be had: sent nothing for 1 s, ", result.Words, StringComparison.Ordinal);
        Assert.EndsWith(" of 300000 bytes in — the finished chunks stay for the resume", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APeersSilenceIsCountedOnTheTransfersClockAndOnlyWhileABodyIsRead()
    {
        // The list and each answer come 1.2 s late and the body stops for 1.5 s after 64 KiB, each longer than the quiet in real time,
        // while the transfer's clock stands still: the list and the answers are the client's own timeout's to bound, and a body's
        // silence is what the transfer's clock counts between its reads.
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hash, bytes, answerAfter: TimeSpan.FromMilliseconds(1200), pauseAfter: 64 * 1024, pauseFor: TimeSpan.FromMilliseconds(1500));
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { Quiet = Quiet }, clock: new ManualClock(Base));
        var troubles = new ConcurrentQueue<MediaTrouble>();
        transfer.Trouble += troubles.Enqueue;
        var uri = new Uri("http://127.0.0.1:" + peer.Port);
        var destination = Path.Combine(_dir.FullName, "late.bin");

        var listed = await transfer.ManifestAsync(uri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var result = await transfer.PullAsync(uri, hash, bytes.Length, destination, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(hash, Assert.Single(listed).Hash);
        Assert.Equal(MediaPullOutcome.Verified, result.Outcome);
        Assert.Empty(troubles);
        Assert.Equal((1, 1), (peer.ListAsks, peer.FileAsks));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task APullHeldByTheBandwidthBucketIsNotCountedSilentHoweverFarTheClockMoves()
    {
        // The bucket stands on a clock of its own that stands still, so the pull's first take of bytes waits in it for as long as the
        // test likes; the transfer's clock is moved ten quiets meanwhile, and the watch is given a second to look. The time between
        // two reads of a body is not the peer's silence: no try is cut, and once the bucket fills the file lands.
        var bytes = RandomNumberGenerator.GetBytes(100_000);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hash, bytes);
        var clock = new ManualClock(Base);
        var bucketClock = new ManualClock(Base);
        var bucket = new TokenBucket(64L * 1024 * 1024, bucketClock);
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, bucket, new MediaTransferOptions { Quiet = Quiet, ChunkAttempts = 1 }, clock: clock);
        var troubles = new ConcurrentQueue<MediaTrouble>();
        transfer.Trouble += troubles.Enqueue;
        var destination = Path.Combine(_dir.FullName, "held.bin");

        var pull = transfer.PullAsync(new Uri("http://127.0.0.1:" + peer.Port), hash, bytes.Length, destination, CancellationToken.None);
        Assert.True(await Eventually.TrueAsync(() => bucket.Waiting == 1 || pull.IsCompleted, 20000), "the pull never waited in the bucket");
        Assert.False(pull.IsCompleted);
        clock.Advance(10 * Quiet.Ticks * TimeSpan.NanosecondsPerTick);
        await Task.WhenAny(pull, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, bucket.Waiting);
        bucketClock.Advance(Durations.NsPerSecond);
        var result = await pull.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MediaPullOutcome.Verified, result.Outcome);
        Assert.Empty(troubles);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task APeerThatNeverAnswersItsListIsGivenUpOnTheClientsTimeoutWithWordsThatSaySo()
    {
        var bytes = RandomNumberGenerator.GetBytes(1000);
        await using var peer = new FakeMediaPeer(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, answersList: false);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var transfer = new MediaTransfer(http, new TokenBucket(0));

        var refused = await Assert.ThrowsAsync<TimeoutException>(() => transfer.ManifestAsync(new Uri("http://127.0.0.1:" + peer.Port), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal($"127.0.0.1:{peer.Port} did not answer its media list in 1 s", refused.Message);
        Assert.Equal(1, peer.ListAsks);
    }

    [Fact]
    public async Task APullsBytesNeverStepBackWhenATryStartsItsChunkAgain()
    {
        // Two chunks of 2 MiB in flight, and every answer ends after 1 MiB and a byte, the connection closed in order: each try lands
        // one block whatever the machine's load (every byte sent before the end arrives), fails, and is tried again from its chunk's
        // first byte, over the block the try before it wrote.
        const int MiB = 1024 * 1024;
        var bytes = RandomNumberGenerator.GetBytes(4 * MiB);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hash, bytes, endAfter: MiB + 1);
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { ChunkBytes = 2 * MiB, Parallelism = 2, ChunkAttempts = 2 });
        var said = new ConcurrentQueue<long>();
        transfer.Progress += p => said.Enqueue(p.BytesDone);

        var result = await transfer.PullAsync(new Uri("http://127.0.0.1:" + peer.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "again.bin"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MediaPullOutcome.Failed, result.Outcome);
        Assert.Equal(4, peer.FileAsks);
        Assert.Equal(said.Order(), said);
        Assert.Equal([0, MiB, 2 * MiB], said);
    }

    [Fact]
    public async Task APullsBytesReachItsListenerInTheOrderTheyLandedHoweverLongTheListenerTakes()
    {
        var (source, bytes) = MakeFile("clip.bin", 3 * 1024 * 1024 + 17);
        var store = new FakeMediaStore();
        var hash = store.Add(source);
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true), new HttpHostOptions { Media = store });
        using var http = new HttpClient();
        var transfer = new MediaTransfer(http, new TokenBucket(0), new MediaTransferOptions { ChunkBytes = 1024 * 1024, Parallelism = 3 });
        var said = new ConcurrentQueue<long>();
        using var another = new SemaphoreSlim(0);
        var first = 0;
        // The first word is taken once another has been said, or after 2 s: a listener that takes its time while the other chunks land.
        transfer.Progress += p =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
                _ = another.Wait(TimeSpan.FromSeconds(2));
            said.Enqueue(p.BytesDone);
            another.Release();
        };

        var result = await transfer.PullAsync(new Uri("http://127.0.0.1:" + host.Port), hash, bytes.Length, Path.Combine(_dir.FullName, "ordered.bin"), CancellationToken.None);

        Assert.Equal(MediaPullOutcome.Verified, result.Outcome);
        Assert.Equal(said.Order(), said);
        Assert.Equal(bytes.Length, said.Last());
    }

    /// <summary>The quiet the fault tests give a try.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    /// <summary>Where a hand-moved clock starts.</summary>
    private const long Base = 1_000_000_000_000;

    /// <summary>Moves the transfer's clock a quiet and a millisecond at a time until the pull ends: each step ends the try then waiting
    /// on a silent peer, and a step taken while none waits counts nothing.</summary>
    private static async Task<MediaPullResult> DriveAsync(ManualClock clock, Task<MediaPullResult> pull)
    {
        var sw = Stopwatch.StartNew();
        while (!pull.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            clock.Advance(Quiet.Ticks * TimeSpan.NanosecondsPerTick + 1_000_000);
            await Task.WhenAny(pull, Task.Delay(100));
        }
        return await pull.WaitAsync(TimeSpan.FromSeconds(5));
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
