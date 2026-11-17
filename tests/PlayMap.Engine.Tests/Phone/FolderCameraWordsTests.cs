using System.Collections.Concurrent;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Engine.Scanning;
using PlayMap.Media.Stills;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A manual Folder camera's words in STATE.scan name only a wait in force: "…, then SCAN NEXT" stands from the moment the camera
/// waits for that picture until SCAN NEXT takes it or the wait is given up, and in between the scan's view says the step it is at.
/// Each point where a wait ends is held on the scan's own thread (the cue before the camera waits, the camera reading the picture it
/// was given, the scan letting the outputs go) and read there once every SCAN NEXT's reply is in, never by timing; a SCAN NEXT sent
/// at such a point is refused with the same words as ever. A refusal's words ("photo 17 shows pattern 16: re-shoot") stand while the
/// camera waits for the picture that answers them: SCAN NEXT taken in the very publication that says them finds the camera waiting.
/// The camera's pictures are read by a reader of the test's own (no FFmpeg): each is the simulator's picture of what was on the glass
/// when it was shot.
/// </summary>
public sealed class FolderCameraWordsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private const string NotWaiting = "the folder camera is not waiting for a picture yet — SCAN NEXT once the pattern is up and shot";

    private const string Checking = "Left: checking each picture's tag";

    /// <summary>A cue's own words, made as the camera makes them.</summary>
    private static string Words(PatternCue cue) => string.Create(CultureInfo.InvariantCulture,
        $"{FolderCameraTests.RigName}: shoot {(cue.Kind == "pattern" ? "pattern" : "locate frame")} {cue.Index + 1}, then SCAN NEXT");

    private static string Locating(int frame) => string.Create(CultureInfo.InvariantCulture,
        $"Left: finding the tag places ({frame} of {CellLocate.Frames})");

    private static bool NamesAWait(string message) => message.EndsWith("then SCAN NEXT", StringComparison.Ordinal);

    [Fact]
    public async Task AfterAPictureIsTakenAndAfterTheLastLocateFrameNoWaitIsNamedUntilTheNextCueWaits()
    {
        await using var bench = await Bench.StartAsync();
        using var readingLocate5 = new Hold();
        using var cueingPattern1 = new Hold();
        bench.OnCue = cue =>
        {
            if (cue is { Kind: "pattern", Index: 0, Retake: 0 })
                cueingPattern1.Enter();
            return false;
        };
        bench.OnRead = cue =>
        {
            if (cue is { Kind: "locate", Index: 4 })
                readingLocate5.Enter();
        };
        await bench.Rig.Ok($"SCAN START {FolderCameraTests.Rig} pj00000001 MODE FAST");

        // Locate frame 5 taken, its picture being read: the step, not the wait that ended.
        await readingLocate5.Held("the camera reading locate frame 5's picture");
        await bench.SaysStepAsync("locating", Locating(5));
        readingLocate5.Release();

        // The last locate frame taken, the tag places decoded, the first pattern up and cued but not yet waited for.
        await cueingPattern1.Held("the first pattern's cue");
        await bench.SaysStepAsync("locating", Locating(CellLocate.Frames));
        cueingPattern1.Release();

        var pattern1 = string.Create(CultureInfo.InvariantCulture, $"{FolderCameraTests.RigName}: shoot pattern 1, then SCAN NEXT");
        Assert.True(await OpsRig.Eventually(() => bench.Views.Exists(v => v.Message == pattern1), 60_000), $"the view never said \"{pattern1}\"");
        await bench.StopAsync();

        // Every publication between the last locate frame's words and the first pattern's: at least the step, none a wait. The kept
        // order is the order here: the person pressed only once the last frame's words were kept, the take's view was kept before
        // its reply, and the first pattern's words were made only after the hold above had that reply.
        var views = bench.Views;
        var last = views.FindLastIndex(v => v.Message == Words(new PatternCue("locate", "", CellLocate.Frames - 1, 0, 0, 0, 0, 0)));
        var first = views.FindIndex(Math.Max(0, last), v => v.Message == pattern1);
        Assert.True(last >= 0 && first > last + 1, string.Create(CultureInfo.InvariantCulture,
            $"nothing was published between the last locate frame's words ({last}) and the first pattern's ({first})"));
        Assert.All(views.Skip(last + 1).Take(first - last - 1), v =>
        {
            Assert.False(NamesAWait(v.Message), v.Message);
            Assert.NotEqual("waiting", v.Stage);
        });
    }

    [Fact]
    public async Task ThePicturesCheckAndTheClosingCheckNameNoWaitOnceTheirPictureIsTaken()
    {
        await using var bench = await Bench.StartAsync();
        using var cueingAgain = new Hold();
        using var readingAgain = new Hold();
        using var cueingClosing = new Hold();
        using var readingClosing = new Hold();
        var patterns = false;
        bench.OnCue = cue =>
        {
            if (cue.Kind == "pattern")
            {
                patterns = true;
                if (cue is { Index: 5, Retake: 0 })
                {
                    // A photo with no tag in it passes the camera and is caught by the node's check, which asks for it again.
                    bench.Shoot(cue, new byte[bench.Rig.Camera.CameraWidth * bench.Rig.Camera.CameraHeight]);
                    return true;
                }
                if (cue is { Index: 5 })
                    cueingAgain.Enter();
                return false;
            }
            if (patterns)
                cueingClosing.Enter();
            return false;
        };
        bench.OnRead = cue =>
        {
            if (cue is { Kind: "pattern", Index: 5, Retake: > 0 })
                readingAgain.Enter();
            else if (patterns && cue.Kind == "locate")
                readingClosing.Enter();
        };
        await bench.Rig.Ok($"SCAN START {FolderCameraTests.Rig} pj00000001 MODE FAST");

        await cueingAgain.Held("the check's cue for pattern 6 again");
        await bench.SaysStepAsync("checking", Checking);
        cueingAgain.Release();
        await readingAgain.Held("the camera reading pattern 6's picture again");
        await bench.SaysStepAsync("checking", Checking);
        readingAgain.Release();
        await cueingClosing.Held("the closing check's cue");
        await bench.SaysStepAsync("checking", Checking);
        cueingClosing.Release();
        await readingClosing.Held("the camera reading the closing check's picture");
        await bench.SaysStepAsync("checking", Checking);
        readingClosing.Release();

        var view = await bench.Rig.EndedAsync(people: bench.People);
        Assert.True(view.State == "done", view.Message);
        await Task.WhenAll(bench.People);
        Assert.True(view.Pairing!.Retakes >= 1, "the photo with no tag was not asked for again");

        // From the closing check's words on, no publication names a wait.
        var views = bench.Views;
        var closing = views.FindLastIndex(v => v.Message == Words(new PatternCue("locate", "", 0, 0, 0, 0, 0, 0)));
        Assert.True(closing >= 0, "the closing check's words were never published");
        Assert.All(views.Skip(closing + 1), v => Assert.False(NamesAWait(v.Message), v.Message));
    }

    [Fact]
    public async Task ARefusalsWordsStandWhileTheCameraWaitsForThePictureThatAnswersThem()
    {
        await using var bench = await Bench.StartAsync();
        var taken = new ConcurrentQueue<(string Words, string Stage, bool Taken)>();
        var wrongDone = 0;
        var emptyDone = 0;
        bench.OnCue = cue =>
        {
            if (cue is { Kind: "locate", Index: 2, Retake: 0 })
            {
                // SCAN NEXT pressed before the photo is in the folder.
                bench.Shoot(cue, null);
                return true;
            }
            if (cue is { Kind: "pattern", Index: 16, Retake: 0 })
            {
                // The photographer shoots the pattern before again.
                bench.Shoot(cue, bench.Rig.Camera.RenderRaster(bench.Rig.Display.Glass.Before, 9_999));
                return true;
            }
            return false;
        };
        // In the publication that says the refusal, on the thread that says it: the right photo shot, then SCAN NEXT.
        bench.OnView = view =>
        {
            var empty = view.Message.StartsWith($"{FolderCameraTests.RigName}: no new picture in ", StringComparison.Ordinal)
                && view.Message.EndsWith(" since locate frame 3 went up — shoot it, then SCAN NEXT", StringComparison.Ordinal);
            var wrong = view.Message == "photo 17 shows pattern 16: re-shoot";
            if ((empty && Interlocked.Exchange(ref emptyDone, 1) == 0) || (wrong && Interlocked.Exchange(ref wrongDone, 1) == 0))
            {
                bench.Drop(bench.Rig.Camera.RenderRaster(bench.Rig.Display.Glass.Now, 7_777));
                taken.Enqueue((view.Message, view.Stage, bench.Rig.Runner.TakeByHand()));
            }
        };
        await bench.Rig.Ok($"SCAN START {FolderCameraTests.Rig} pj00000001 MODE FAST");

        Assert.True(await OpsRig.Eventually(() => !taken.IsEmpty, 60_000), "the empty folder's words were never said");
        Assert.True(taken.TryPeek(out var first) && first.Taken, $"SCAN NEXT answering \"{first.Words}\" found no camera waiting");
        Assert.Equal("waiting", first.Stage);
        Assert.True(await OpsRig.Eventually(() => taken.Count == 2, 60_000), "the wrong photo's words were never said");
        var second = taken.ToArray()[1];
        Assert.True(second.Taken, $"SCAN NEXT answering \"{second.Words}\" found no camera waiting");
        Assert.Equal("waiting", second.Stage);
        Assert.True(await OpsRig.Eventually(() => bench.Rig.Runner.View is { Pattern: >= 17 }, 60_000), "the scan did not go on past pattern 17");
        await bench.StopAsync();
    }

    [Fact]
    public async Task AWaitGivenUpOrCancelledNamesNoWaitAndTakesNoPicture()
    {
        var time = new ManualTime();
        await using var bench = await Bench.StartAsync(time);
        using var cueingAgain = new Hold();
        bench.OnCue = cue =>
        {
            // Nobody shoots locate frame 1 the first time, nor locate frame 2.
            if (cue is { Kind: "locate", Index: 0, Retake: 0 } or { Kind: "locate", Index: 1 })
                return true;
            if (cue is { Kind: "locate", Index: 0 })
                cueingAgain.Enter();
            return false;
        };
        await bench.Rig.Ok($"SCAN START {FolderCameraTests.Rig} pj00000001 MODE FAST");
        var frame1 = Words(new PatternCue("locate", "", 0, 0, 0, 0, 0, 0));
        Assert.True(await OpsRig.Eventually(() => bench.Rig.Runner.View?.Message == frame1, 60_000), $"the view never said \"{frame1}\"");

        // The camera's patience runs out on the scan's clock: the wait is given up and the frame cued again.
        var clock = OwnThread.Start("the scan's clock", () => time.Advance(TimeSpan.FromMinutes(11)));
        await cueingAgain.Held("locate frame 1 cued again");
        await bench.SaysStepAsync("locating", Locating(1));
        cueingAgain.Release();
        clock.Join(Patience);

        // SCAN CANCEL while the camera waits for locate frame 2: as the outputs are let go, no wait is named.
        var frame2 = Words(new PatternCue("locate", "", 1, 0, 0, 0, 0, 0));
        Assert.True(await OpsRig.Eventually(() => bench.Rig.Runner.View?.Message == frame2, 60_000), $"the view never said \"{frame2}\"");
        (string Stage, string Message)? atRelease = null;
        bench.Rig.Display.Releasing = () => atRelease = bench.Rig.Runner.View is { } v ? (v.Stage, v.Message) : null;
        await bench.StopAsync();
        Assert.Equal(("locating", Locating(2)), atRelease);
        Assert.Equal("cancelled", bench.Rig.Runner.View!.State);
    }

    [Fact]
    public async Task AScanThatFailsOnAPictureNamesNoWaitAsItEnds()
    {
        await using var bench = await Bench.StartAsync();
        bench.OnCue = cue =>
        {
            if (cue is { Kind: "locate", Index: 0 })
            {
                // A photo of another size than the camera rig's fails the scan once it is read.
                bench.Shoot(cue, new byte[16 * 12], 16, 12);
                return true;
            }
            return false;
        };
        (string Stage, string Message)? atRelease = null;
        bench.Rig.Display.Releasing = () => atRelease = bench.Rig.Runner.View is { } v ? (v.Stage, v.Message) : null;
        await bench.Rig.Ok($"SCAN START {FolderCameraTests.Rig} pj00000001 MODE FAST");
        var view = await bench.Rig.EndedAsync(people: bench.People);
        Assert.Equal("failed", view.State);
        Assert.Contains("sends 16×12 pictures", view.Message, StringComparison.Ordinal);
        Assert.Equal(("locating", Locating(1)), atRelease);
        await Task.WhenAll(bench.People);
    }

    /// <summary>A hold the test sets in a callback on the scan's thread: entered there, seen on a thread of the test's own, released by
    /// the test.</summary>
    private sealed class Hold : IDisposable
    {
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();

        public void Enter()
        {
            _held.Set();
            Assert.True(_release.Wait(Patience), "the test never let the scan go on");
        }

        public Task Held(string what) => OwnThread.WaitAsync(what, _held, Patience);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _held.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>The phone scan rig with a manual Folder camera whose pictures are the simulator's, read by the test's reader; a person
    /// at the camera for every cue the test leaves to one, and every view the runner publishes kept in the order its
    /// <c>Progressed</c> handler ran. The runner raises <c>Progressed</c> outside its gate, so two threads' views (the scan's and
    /// SCAN NEXT's) can be kept out of the order they were placed; the tests compare only views ordered otherwise: a person presses
    /// only once its cue's words are kept, and a hold's sample waits for every SCAN NEXT's reply, which comes after its view.</summary>
    private sealed class Bench : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, (byte[] Gray, int Width, int Height)> _pictures = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private readonly List<(string Stage, string Message)> _views = [];
        private int _shot;
        private volatile bool _stopping;
        private PatternCue? _cue;

        private Bench(string folder) => Folder = folder;

        public PhoneScanRig Rig { get; private set; } = null!;

        public string Folder { get; }

        public ConcurrentQueue<Task> People { get; } = new();

        /// <summary>At each cue, on the scan's thread before the camera waits: true when the test has dealt with it, else a person
        /// shoots once the cue's own words are said and presses SCAN NEXT.</summary>
        public Func<PatternCue, bool>? OnCue { get; set; }

        /// <summary>As the camera reads the picture SCAN NEXT gave it, on the scan's thread, with the cue it answers.</summary>
        public Action<PatternCue>? OnRead { get; set; }

        /// <summary>Each view as it is published, on the thread that publishes it.</summary>
        public Action<ScanView>? OnView { get; set; }

        public List<(string Stage, string Message)> Views
        {
            get
            {
                lock (_gate)
                    return [.. _views];
            }
        }

        private int Kept
        {
            get
            {
                lock (_gate)
                    return _views.Count;
            }
        }

        /// <summary>Whether a view kept from <paramref name="from"/> on says <paramref name="words"/>.</summary>
        private bool Said(int from, string words)
        {
            lock (_gate)
                return _views.FindIndex(from, v => v.Message == words) >= 0;
        }

        public static async Task<Bench> StartAsync(TimeProvider? time = null)
        {
            var bench = new Bench(Directory.CreateTempSubdirectory("playmap-words-").FullName);
            try
            {
                bench.Rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions
                {
                    Time = time ?? TimeProvider.System,
                    OpenCamera = r => new FolderCamera(r, new FolderCameraOptions { Cued = bench.Cued, Poll = TimeSpan.FromMilliseconds(20), Read = bench.Read }),
                });
                bench.Rig.Runner.Progressed += bench.Published;
                await FolderCameraTests.AddRigAsync(bench.Rig, bench.Folder, FolderAdvance.Manual);
                return bench;
            }
            catch (Exception)
            {
                await bench.DisposeAsync();
                throw;
            }
        }

        private void Published(ScanView view)
        {
            lock (_gate)
                _views.Add((view.Stage, view.Message));
            OnView?.Invoke(view);
        }

        private void Cued(PatternCue cue)
        {
            Volatile.Write(ref _cue, cue);
            if (OnCue?.Invoke(cue) == true)
                return;
            Shoot(cue, Rig.Camera.RenderRaster(Rig.Display.Glass.Now, (ulong)Interlocked.Increment(ref _shot)));
        }

        private StillImage Read(string path)
        {
            OnRead?.Invoke(Volatile.Read(ref _cue)!);
            var (gray, width, height) = _pictures[path];
            return new StillImage(width, height, gray, null, "test");
        }

        /// <summary>A person at the camera, sent at the cue before the camera waits: once a view kept since says <paramref name="cue"/>'s
        /// own words, puts <paramref name="picture"/> in the folder (none: nothing) and presses SCAN NEXT, so the view its take makes
        /// is kept after those words.</summary>
        public void Shoot(PatternCue cue, byte[]? picture, int width = 0, int height = 0)
        {
            var words = Words(cue);
            var since = Kept;
            People.Enqueue(OwnThread.StartAsync("person at the camera", async () =>
            {
                Assert.True(await OpsRig.Eventually(() => _stopping || Said(since, words), 60_000), $"the view never said \"{words}\"");
                if (_stopping)
                    return;
                if (picture is not null)
                    Drop(picture, width, height);
                var next = await Rig.Run("SCAN NEXT");
                Assert.True(next.Ok || _stopping, next.Payload);
            }));
        }

        /// <summary>A photo in the folder, the camera rig's size unless said otherwise.</summary>
        public void Drop(byte[] gray, int width = 0, int height = 0)
        {
            var n = Interlocked.Increment(ref _shot);
            var path = Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"photo-{n:0000}.tif"));
            _pictures[path] = (gray, width > 0 ? width : Rig.Camera.CameraWidth, height > 0 ? height : Rig.Camera.CameraHeight);
            File.WriteAllBytes(path, [0]);
        }

        /// <summary>The view now, read on the test's thread while the scan is held, says <paramref name="stage"/> and
        /// <paramref name="message"/> (STATE's scan block too), and a SCAN NEXT now is refused with the words it always had. Read once
        /// every SCAN NEXT pressed so far has had its reply: a take's view is placed before its reply, on SCAN NEXT's thread, not the
        /// held one.</summary>
        public async Task SaysStepAsync(string stage, string message)
        {
            await Task.WhenAll(People).WaitAsync(Patience);
            var view = Rig.Runner.View!;
            Assert.Equal((stage, message), (view.Stage, view.Message));
            var scan = Rig.State().GetProperty("scan");
            Assert.Equal((stage, message), (scan.GetProperty("stage").GetString(), scan.GetProperty("message").GetString()));
            Assert.Equal(NotWaiting, (await Rig.Run("SCAN NEXT")).Payload);
        }

        /// <summary>Ends the scan (SCAN CANCEL) and waits for its end; the people still waiting for words go home.</summary>
        public async Task StopAsync()
        {
            _stopping = true;
            await Rig.Ok("SCAN CANCEL");
            var view = await Rig.EndedAsync(people: People);
            Assert.Equal("cancelled", view.State);
            await Task.WhenAll(People);
        }

        public async ValueTask DisposeAsync()
        {
            _stopping = true;
            if (Rig is not null)
                await Rig.DisposeAsync();
            Directory.Delete(Folder, recursive: true);
        }
    }
}
