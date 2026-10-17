using System.Diagnostics;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Engine.Playback;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The renderer's pictures from the engine, with no GPU: a library item opens on a worker (the first
/// asks get null, never a wait), two decoders' frames never share a generation, a removed item's decoder is
/// disposed, a source that could not open is opened again later, and an idle playlist routes to black.</summary>
public sealed class EngineSourcesTests
{
    private sealed class OneFrame(string key) : VideoFrameLease
    {
        public override PixelLayout Layout => PixelLayout.Rgba8;
        public override int Width => 1;
        public override int Height => 1;
        public override IReadOnlyList<FramePlane> Planes => [];
        public override long PtsNs => 0;
        public override long DurationNs => 40_000_000;
        public override long Generation => 1;
        public string Key { get; } = key;

        protected override void Release()
        {
            // Nothing pooled.
        }
    }

    private sealed class FakeSource(string key, Action? onDispose = null) : IVideoSource
    {
        public string Key { get; } = key;
        public MediaInfo Info => MediaInfo.Unknown with { DurationNs = 2_000_000_000 };
        public SourceHealth Health => new(true, "ok", 0, 0, 0, 0, "fake", false);
        public bool Disposed { get; private set; }
        public long LastAsked { get; private set; } = -1;

        public VideoFrameLease? Acquire(long mediaNs)
        {
            LastAsked = mediaNs;
            return new OneFrame(Key);
        }

        public void Prepare(long mediaNs, bool playing, double rate)
        {
            // The fake has every frame at once.
        }

        public bool IsReadyAt(long mediaNs) => true;

        public void Dispose()
        {
            onDispose?.Invoke();
            Disposed = true;
        }
    }

    private sealed class FakeMedia : IMediaBackend
    {
        public List<FakeSource> Made { get; } = [];
        public List<int> OpenedOn { get; } = [];
        public int Refusals { get; set; }
        public Action? OnDispose { get; init; }

        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

        public IVideoSource? Open(MediaItem item)
        {
            lock (Made)
            {
                OpenedOn.Add(Environment.CurrentManagedThreadId);
                if (Refusals > 0)
                {
                    Refusals--;
                    throw new IOException($"{item.Name} is not here yet");
                }
                var s = new FakeSource(item.Id, OnDispose);
                Made.Add(s);
                return s;
            }
        }

        public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

        public string? PathForHash(string contentHash) => null;
    }

    private static MediaItem Item(string id) => new(id, id, MediaKind.Video, "/clips/" + id + ".mp4", "", 0, MediaInfo.Unknown, []);

    private static EngineSnapshot Snapshot(Project project) =>
        new(new ProjectSnapshot(project, 1, ""), PlaybackSnapshot.Empty, AirState.Initial);

    /// <summary>Asks once a frame until the source answers (its open runs on a worker).</summary>
    private static VideoFrameLease Until(EngineSources sources, EngineSnapshot snap, SourceRef source, ref long frame)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            sources.BeginFrame(snap, ++frame);
            if (sources.Acquire(source, 7_000_000_000) is { } lease)
                return lease;
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the source never opened");
            Thread.Yield();
        }
    }

    [Fact]
    public void TwoDecodersFramesNeverShareAGenerationAndALibraryItemLoopsOnTheLeadersClock()
    {
        var media = new FakeMedia();
        var project = Project.CreateDefault() with { Media = [Item("md_a"), Item("md_b")] };
        var snap = Snapshot(project);
        using var sources = new EngineSources(media);
        long frame = 0;
        VideoFrameLease? a = null, b = null;
        var waited = Stopwatch.StartNew();
        while (a is null || b is null)
        {
            // Both are asked every frame, as the renderer would: a source no frame asks for is let go.
            sources.BeginFrame(snap, ++frame);
            var nextA = sources.Acquire(SourceRef.Media("md_a"), 7_000_000_000);
            var nextB = sources.Acquire(SourceRef.Media("md_b"), 7_000_000_000);
            if (a is null)
                a = nextA;
            else
                nextA?.Dispose();
            if (b is null)
                b = nextB;
            else
                nextB?.Dispose();
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the sources never opened");
            Thread.Yield();
        }
        using (a)
        using (b)
        {
            Assert.NotEqual(a.Generation, b.Generation);
            Assert.Equal(1, a.Generation & 0xFFFF_FFFF);
        }
        // Opened on a worker, never on the thread that asks for frames.
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, media.OpenedOn);
        // 7 s on the leader's clock into a 2 s clip is 1 s into it.
        Assert.Equal(1_000_000_000, media.Made.Single(m => m.Key == "md_a").LastAsked);
        Assert.Equal(2, sources.OpenSources);
    }

    [Fact]
    public void ARemovedItemsDecoderIsDisposed()
    {
        var media = new FakeMedia();
        var project = Project.CreateDefault() with { Media = [Item("md_a")] };
        using var sources = new EngineSources(media);
        long frame = 0;
        Until(sources, Snapshot(project), SourceRef.Media("md_a"), ref frame).Dispose();

        var without = Snapshot(project with { Media = [] });
        for (var i = 0; i < 31; i++)
            sources.BeginFrame(without, ++frame);
        Assert.Equal(0, sources.OpenSources);
        var waited = Stopwatch.StartNew();
        while (!media.Made[0].Disposed)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the decoder was never disposed");
            Thread.Yield();
        }
    }

    /// <summary>The open runs where the sources queue it (here, a queue this test runs, lock-step with its frames): a failed open says
    /// why, the item is not asked for again until <see cref="EngineSources.RetryFrames"/> frames after that open — at the first sweep
    /// past them — and then opens.</summary>
    [Fact]
    public void ASourceThatCouldNotOpenIsOpenedAgainLater()
    {
        var media = new FakeMedia { Refusals = 1 };
        var project = Project.CreateDefault() with { Media = [Item("md_late")] };
        var snap = Snapshot(project);
        var queued = new Queue<Action>();
        var said = new List<string>();
        // Its closes run when this test runs them: the dispose at the end waits for none.
        var sources = new EngineSources(media, work: queued.Enqueue, closeWait: TimeSpan.Zero, said: said.Add);
        void RunQueued()
        {
            while (queued.TryDequeue(out var work))
                work();
        }

        long frame = 1;
        sources.BeginFrame(snap, frame);
        Assert.Null(sources.Acquire(SourceRef.Media("md_late"), 0));
        Assert.Single(queued);
        RunQueued();
        Assert.Contains("not here yet", sources.Problem("md_late"), StringComparison.Ordinal);

        // Asked every frame, it is not opened again until a sweep comes more than RetryFrames after the open.
        var again = 0L;
        while (again == 0)
        {
            sources.BeginFrame(snap, ++frame);
            Assert.Null(sources.Acquire(SourceRef.Media("md_late"), 0));
            if (queued.Count > 0)
                again = frame;
            Assert.True(frame < 1 + 2 * EngineSources.RetryFrames, "never opened again");
        }
        Assert.True(again - 1 > EngineSources.RetryFrames, $"opened again at frame {again}");
        Assert.True(again - 1 <= EngineSources.RetryFrames + 30, $"opened again at frame {again}");
        RunQueued();
        sources.BeginFrame(snap, frame + 1);
        using (var lease = sources.Acquire(SourceRef.Media("md_late"), 7_000_000_000))
            Assert.NotNull(lease);
        Assert.Single(media.Made);
        Assert.Equal(2, media.OpenedOn.Count);

        // A close that has not run when the wait is up is said, by name; it still runs.
        sources.Dispose();
        Assert.Equal(["render: md_late did not close within 0 s of the stop — the file stays open until it does"], said);
        Assert.Equal(1, sources.Closing);
        RunQueued();
        Assert.Equal((0, true), (sources.Closing, media.Made[0].Disposed));
    }

    /// <summary>When dispose returns, every source has closed: the closes run on workers (here a thread this test holds until every
    /// close is queued), and dispose waits for them — an open still running when it began included, which closes what it opened.</summary>
    [Fact]
    public async Task WhenDisposeReturnsEverySourceHasClosed()
    {
        var media = new FakeMedia();
        var project = Project.CreateDefault() with { Media = [Item("md_a"), Item("md_b"), Item("md_c")] };
        var snap = Snapshot(project);
        using var work = new System.Collections.Concurrent.BlockingCollection<Action>();
        var worker = new Thread(() =>
        {
            foreach (var w in work.GetConsumingEnumerable())
                w();
        }) { IsBackground = true, Name = "sources worker" };
        worker.Start();
        var sources = new EngineSources(media, work: work.Add);
        long frame = 0;
        // Two open (their opens run); then the worker is held, and the third's open is queued behind the hold with the closes.
        Until(sources, snap, SourceRef.Media("md_a"), ref frame).Dispose();
        Until(sources, snap, SourceRef.Media("md_b"), ref frame).Dispose();
        using var gate = new ManualResetEventSlim(false);
        work.Add(gate.Wait);
        sources.BeginFrame(snap, frame + 1);
        Assert.Null(sources.Acquire(SourceRef.Media("md_c"), 0));

        var closedWhenDisposeReturned = -1;
        var disposing = Task.Run(() =>
        {
            sources.Dispose();
            closedWhenDisposeReturned = media.Made.Count(m => m.Disposed);
        });
        // Held behind the gate: md_c's open and the two closes.
        Assert.True(await Host.Eventually.TrueAsync(() => work.Count == 3 && sources.Closing == 3), $"{work.Count} {sources.Closing}");
        Assert.False(disposing.IsCompleted);
        gate.Set();
        await disposing;
        work.CompleteAdding();
        worker.Join();
        Assert.Equal(3, media.Made.Count);
        Assert.Equal(3, closedWhenDisposeReturned);
        Assert.Equal(0, sources.Closing);
    }

    /// <summary>A close that hangs (a read that never returns) does not hold the stop for ever: dispose returns after its wait with
    /// the words naming it.</summary>
    [Fact]
    public async Task ACloseThatHangsIsSaidAndTheStopGoesOn()
    {
        // Never disposed here: the hung close waits on it on a pool thread until this test lets it go.
        var hang = new ManualResetEventSlim(false);
        var media = new FakeMedia { OnDispose = hang.Wait };
        var project = Project.CreateDefault() with { Media = [Item("md_stuck")] };
        var said = new List<string>();
        var sources = new EngineSources(media, closeWait: TimeSpan.FromMilliseconds(100), said: said.Add);
        long frame = 0;
        Until(sources, Snapshot(project), SourceRef.Media("md_stuck"), ref frame).Dispose();
        sources.Dispose();
        Assert.Equal(["render: md_stuck did not close within 0.1 s of the stop — the file stays open until it does"], said);
        hang.Set();
        Assert.True(await Host.Eventually.TrueAsync(() => sources.Closing == 0), "the hung close never ended once let go");
        hang.Dispose();
    }

    [Fact]
    public void AnIdlePlaylistIsBlackAndAGeneratorItemIsRoutedToTheRenderer()
    {
        using var sources = new EngineSources(null);
        var project = Project.CreateDefault() with { Playlists = [new Playlist("pl_1", "Show", [], false, null)] };
        sources.BeginFrame(Snapshot(project), 1);
        Assert.Equal(SourceRef.None, sources.Route(SourceRef.Playlist("pl_1"), 0));
        Assert.Null(sources.Route(SourceRef.Media("md_x"), 0));

        var lanes = new PlaybackLanes(_ => 0);
        var list = new Playlist("pl_1", "Show", [PlaylistItem.For("it_1", SourceRef.Generator("gn_testcard"), "Card")], false, null);
        project = project with { Playlists = [list] };
        lanes.Reconcile(project);
        lanes.Go(list, 0, 1_000);
        sources.BeginFrame(new EngineSnapshot(new ProjectSnapshot(project, 2, ""), lanes.Snapshot, AirState.Initial), 2);
        Assert.Equal(SourceRef.Generator("gn_testcard"), sources.Route(SourceRef.Playlist("pl_1"), 2_000));
        Assert.Null(sources.Acquire(SourceRef.Playlist("pl_1"), 2_000));
    }
}
