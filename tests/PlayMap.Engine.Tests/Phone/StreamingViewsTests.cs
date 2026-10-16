using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>The outputs as a scan's display: the grey frame each output shows now, every present counted and stamped on the clock the
/// camera's pictures are.</summary>
internal sealed class LitGlass : IScanDisplay
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _frames = new(StringComparer.Ordinal);
    private int _presents;

    public int Presents => Volatile.Read(ref _presents);

    /// <summary>What each output shows now, read in one instant, and that instant.</summary>
    public (Dictionary<string, byte[]> Frames, long AtNs) Read()
    {
        lock (_gate)
            return (new Dictionary<string, byte[]>(_frames, StringComparer.Ordinal), MonoClock.Instance.NowNs);
    }

    public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
    {
        long at;
        lock (_gate)
        {
            _frames[outputId] = gray8.ToArray();
            at = MonoClock.Instance.NowNs;
        }
        Interlocked.Increment(ref _presents);
        return Task.FromResult(at);
    }

    public void Release()
    {
        lock (_gate)
            _frames.Clear();
    }
}

/// <summary>A streaming camera that stands at one of the multi-view scene's places and sees the scene lit by what the outputs show now:
/// each pixel the surface the simulator traced for it, lit by each projector's output at that point's place in its raster (bilinear,
/// gamma 2.2) over a little ambient. A picture arrives every 16 ms, stamped when the outputs were read for it.</summary>
internal sealed class LitCamera : IScanCamera
{
    private const double Ambient = 0.03;
    private const double Gain = 0.8;
    private static readonly double[] Lut = Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255.0, 2.2)).ToArray();
    private readonly LitGlass _glass;
    private readonly (string OutputId, SimGeometry Geometry)[] _lit;
    private long _generation;

    public LitCamera(LitGlass glass, int view, IReadOnlyList<(string OutputId, Intrinsics Prior)> projectors)
    {
        _glass = glass;
        var rig = new SimulatedRig(SimulatedRig.ViewsScene, MultiViewScanKit.CameraWidth, MultiViewScanKit.CameraHeight, [.. projectors.Select(p => p.Prior)], view,
            [.. Enumerable.Range(0, projectors.Count)]);
        _lit = [.. projectors.Select((p, j) => (p.OutputId, rig.Simulator(j, ScanPlan.Create(p.Prior.Width, p.Prior.Height, new ScanPlanOptions { Mode = ScanMode.Fast })).Geometry))];
    }

    public string Problem => "";

    public Task<bool> OpenAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(16, ct);
        var (frames, at) = _glass.Read();
        var first = _lit[0].Geometry;
        var image = new byte[first.Width * first.Height];
        for (var i = 0; i < image.Length; i++)
        {
            if (first.Triangles[i] < 0)
                continue;
            var light = Ambient;
            foreach (var (outputId, g) in _lit)
            {
                if (!frames.TryGetValue(outputId, out var shown) || float.IsNaN(g.RasterX[i]) || g.Occluded[i])
                    continue;
                var k = g.Scene.Projector.Intrinsics;
                light += Gain * g.Cos[i] * Sample(shown, k.Width, k.Height, g.RasterX[i] - 0.5, g.RasterY[i] - 0.5);
            }
            image[i] = (byte)Math.Clamp(Math.Round(255 * first.Albedo[i] * light), 0, 255);
        }
        return new CameraFrame(image, first.Width, first.Height, at, Interlocked.Increment(ref _generation));
    }

    private static double Sample(byte[] gray, int w, int h, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        double At(int px, int py) => Lut[gray[Math.Clamp(py, 0, h - 1) * w + Math.Clamp(px, 0, w - 1)]];
        return (1 - fy) * ((1 - fx) * At(x0, y0) + fx * At(x0 + 1, y0)) + fy * ((1 - fx) * At(x0, y0 + 1) + fx * At(x0 + 1, y0 + 1));
    }

    public void Dispose()
    {
        // Nothing is held: the pictures are made from the glass.
    }
}

/// <summary>
/// Views of a scan through a camera that streams (not one that pairs its pictures by what they show): each view's run measures the
/// camera's latency with its white flashes per projector before its patterns go up on the outputs (through the display the render host
/// marks raw, so LED fixtures and sends darken as for any scan); the view keeps what it measured, the scan answers each projector's from
/// its newest view, STATE and SCAN VIEWS show each view's, and the scan's folder keeps them through a restart.
/// </summary>
public sealed class StreamingViewsTests(ITestOutputHelper output)
{
    private static Output ProjectorOutput(string id, string projectorId) =>
        new(id, id, OutputKind.Virtual, "", MultiViewScanKit.ProjectorWidth, MultiViewScanKit.ProjectorHeight, 60, OutputMode.Projector, projectorId,
            SourceRef.None, new RectD(0, 0, MultiViewScanKit.ProjectorWidth, MultiViewScanKit.ProjectorHeight), OutputWarp.None, BlendSettings.Default,
            ColorTrim.Neutral, [], new RectD(0, 0, 1, 1), 0, true);

    [Fact]
    public async Task EachViewKeepsTheLatencyItsFlashesMeasuredThroughTheSolveAndARestart()
    {
        var p = MultiViewScanKit.Project();
        p = p with { Outputs = [ProjectorOutput("op00000001", MultiViewScanKit.Pj1), ProjectorOutput("op00000002", MultiViewScanKit.Pj2)] };
        p = p.WithStage(s => s with { Cameras = s.Cameras.Update(MultiViewScanKit.Camera, c => c with { SourceKind = CameraSourceKind.Device, Source = "lit" }) });
        var priors = p.Stage.Projectors.Select(x => x.Intrinsics).ToList();

        // The camera sees a flash: white on one output lifts its picture's mean by more than the estimator's smallest step.
        var probe = new LitGlass();
        using (var look = new LitCamera(probe, 0, [("op00000001", priors[0]), ("op00000002", priors[1])]))
        {
            var dark = LatencyEstimator.MeanBrightness((await look.NextAsync(0, TimeSpan.FromSeconds(1), CancellationToken.None))!.Gray);
            var white = new byte[MultiViewScanKit.ProjectorWidth * MultiViewScanKit.ProjectorHeight];
            Array.Fill(white, (byte)255);
            await probe.ShowAsync("op00000001", white, MultiViewScanKit.ProjectorWidth, MultiViewScanKit.ProjectorHeight, CancellationToken.None);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lit = LatencyEstimator.MeanBrightness((await look.NextAsync(0, TimeSpan.FromSeconds(1), CancellationToken.None))!.Gray);
            output.WriteLine($"dark {dark:0.0}, lit {lit:0.0}, a picture in {sw.Elapsed.TotalMilliseconds:0} ms");
            Assert.True(lit - dark > 2 * new LatencyEstimator().MinStep, $"dark {dark:0.0}, lit {lit:0.0}");
        }

        var glass = new LitGlass();
        var views = 0;
        var rig = new OpsRig(project: p, display: glass, scan: new ScanRunnerOptions
        {
            // Each run opens the camera afresh, at the scene's next place.
            OpenCamera = _ => new LitCamera(glass, Interlocked.Increment(ref views) - 1, [("op00000001", priors[0]), ("op00000002", priors[1])]),
            // Each flash held as long as a node holds it: a slow machine's pictures still fall several to a flash.
            LatencyFlashes = 2,
        });
        try
        {
            var line = $"SCAN START {MultiViewScanKit.Camera} {MultiViewScanKit.Pj1} {MultiViewScanKit.Pj2} MODE FAST";
            var id = await MultiViewScanKit.ScanAsync(rig, line);
            var one = rig.Scanner.Find(id)!;
            var first = Assert.Single(one.Captured);
            Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], first.Latencies.Select(l => l.ProjectorId));
            Assert.All(first.Latencies, l =>
            {
                output.WriteLine($"v1 {l.ProjectorId}: {l.Latency.Words}");
                Assert.True(l.Latency.Transitions > 0, l.Latency.Words);
                Assert.InRange(l.Latency.LatencyMs, 0, 500);
            });
            Assert.Equal(first.Latencies.Select(l => (l.ProjectorId, l.Latency)), one.Latencies);
            var shownForOne = glass.Presents;

            // A second view INTO the scan, from another place: its own flashes, its own latencies; the patterns went up on the display.
            Assert.Equal(id, await MultiViewScanKit.ScanAsync(rig, line + " INTO " + id));
            Assert.True(glass.Presents > 2 * shownForOne - 10, $"{glass.Presents} presents after {shownForOne}");
            var two = rig.Scanner.Find(id)!;
            Assert.NotNull(two.Multi);
            Assert.Equal(["v1", "v2"], two.Captured.Select(v => v.Id));
            Assert.Equal(first.Latencies, two.Captured[0].Latencies);
            var second = two.Captured[1].Latencies;
            Assert.Equal([MultiViewScanKit.Pj1, MultiViewScanKit.Pj2], second.Select(l => l.ProjectorId));
            Assert.All(second, l => Assert.True(l.Latency.Transitions > 0, l.Latency.Words));
            // The scan's latency for each projector is its newest view's.
            Assert.Equal(second.Select(l => (l.ProjectorId, l.Latency)), two.Latencies);

            // STATE and SCAN VIEWS show each view's.
            var state = rig.State().GetProperty("scan").GetProperty("views");
            var reply = JsonDocument.Parse(await rig.Ok("SCAN VIEWS " + id)).RootElement.GetProperty("views");
            foreach (var shown in new[] { state, reply })
            {
                Assert.Equal(2, shown.GetArrayLength());
                foreach (var (row, view) in shown.EnumerateArray().Zip(two.Captured))
                {
                    var latencies = row.GetProperty("latencies").EnumerateArray().ToList();
                    Assert.Equal(view.Latencies.Select(l => l.ProjectorId), latencies.Select(l => l.GetProperty("projector").GetString()));
                    Assert.Equal(view.Latencies.Select(l => Math.Round(l.Latency.LatencyMs, 1)), latencies.Select(l => l.GetProperty("ms").GetDouble()));
                    Assert.All(latencies, l => Assert.True(l.GetProperty("measured").GetBoolean()));
                }
            }

            // Read back from the scan's folder alone, every view keeps what it measured, and the scan answers its newest views'.
            var back = ScanArchive.Load(rig.Folder, ScanArchive.DataPath(id))!;
            Assert.Equal(two.Captured.Select(v => v.Latencies.ToArray()), back.Captured.Select(v => v.Latencies.ToArray()));
            Assert.Equal(two.Latencies, back.Latencies);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
