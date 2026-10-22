using System.Collections.Concurrent;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Host;
using Xunit.Abstractions;
using NodeRig = PlayMap.Engine.Tests.Host.HostRig;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>A camera that looks at the scan simulator's scene lit by the picture the render host really has on one
/// output: each camera pixel sees the scene point the simulator traced for it, lit by the output's pixel at that
/// point's place in the projector's raster (bilinear, pixel centres at +0.5, the projector's gamma 2.2) over a little
/// ambient; a point in the projector's shadow or off its raster has the ambient alone. Each picture is the output as
/// it is when the camera is asked, stamped when it arrived.</summary>
internal sealed class SceneCamera(Func<RenderHost?> render, string outputId, SimGeometry scene) : IScanCamera
{
    private const double Ambient = 0.03;
    private const double Gain = 0.8;
    private static readonly double[] Lut = Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255.0, 2.2)).ToArray();
    private readonly ConcurrentQueue<(long ArrivalNs, byte Green)> _seen = new();
    private long _generation;

    public int Pictures => (int)Interlocked.Read(ref _generation);

    /// <summary>Each picture's arrival and the output's grey at its first pixel (a raw scan frame: 255 white, 0 black).</summary>
    public IReadOnlyList<(long ArrivalNs, byte Green)> Seen => [.. _seen];

    public string Problem => render() is null ? "no render host" : "";

    public Task<bool> OpenAsync(CancellationToken ct) => Task.FromResult(render() is not null);

    public async Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        if (render() is not { } host)
            return null;
        byte[]? shown;
        try
        {
            shown = await host.ReadOutputAsync(outputId).WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        if (shown is null)
            return null;
        var arrival = MonoClock.Instance.NowNs;
        _seen.Enqueue((arrival, shown[1]));
        return new CameraFrame(Light(shown), scene.Width, scene.Height, arrival, Interlocked.Increment(ref _generation));
    }

    private byte[] Light(byte[] bgra)
    {
        var k = scene.Scene.Projector.Intrinsics;
        var image = new byte[scene.Width * scene.Height];
        for (var i = 0; i < image.Length; i++)
        {
            if (scene.Triangles[i] < 0)
                continue;
            var albedo = scene.Albedo[i];
            var light = Ambient;
            var u = scene.RasterX[i];
            var v = scene.RasterY[i];
            if (!float.IsNaN(u) && !scene.Occluded[i])
                light += Gain * scene.Cos[i] * Sample(bgra, k.Width, k.Height, u - 0.5, v - 0.5);
            image[i] = (byte)Math.Clamp(Math.Round(255 * albedo * light), 0, 255);
        }
        return image;
    }

    /// <summary>The projector's light at a raster point: the output's grey (its green), through the gamma, bilinear.</summary>
    private static double Sample(byte[] bgra, int w, int h, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        double At(int px, int py) => Lut[bgra[(Math.Clamp(py, 0, h - 1) * w + Math.Clamp(px, 0, w - 1)) * 4 + 1]];
        return (1 - fy) * ((1 - fx) * At(x0, y0) + fx * At(x0 + 1, y0)) + fy * ((1 - fx) * At(x0, y0 + 1) + fx * At(x0 + 1, y0 + 1));
    }

    public void Dispose()
    {
        // Nothing is held: the pictures are read from the render host.
    }
}

/// <summary>The render host's scan display with every present it answered kept, in order.</summary>
internal sealed class PresentLog(IScanDisplay inner) : IScanDisplay
{
    private readonly ConcurrentQueue<long> _at = new();

    public IReadOnlyList<long> Presents => [.. _at];

    public async Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
    {
        var at = await inner.ShowAsync(outputId, gray8, width, height, ct).ConfigureAwait(false);
        _at.Enqueue(at);
        return at;
    }

    public void Release() => inner.Release();
}

/// <summary>A real node with both the render host and the operations parts: a scan's patterns go out raw on the
/// projector's output through the render host's scan display, each present stamped, and a camera watching the output
/// light a simulated scene — so the scan finds the projector's aim and lens to within a stated tolerance of the truth
/// and gives the output back to the show; and with the node blacked out the display refuses the scan's frames, so the
/// scan fails with the reason instead of capturing a projector that shows nothing.</summary>
[Collection("render host")]
public sealed class OpsRenderScanTests(ITestOutputHelper output)
{
    /// <summary>How close the scan must find the projector: its orientation within 0.25°, its throw ratio within
    /// 0.6 % and its centre within 1 cm of the truth (a 256 × 160 output read back and seen by a 320 × 240 calibrated
    /// camera from about 2.5 m; the prior was 12 % off in throw and aimed decimetres off). One projector against the
    /// camera alone lets its focal length and its aim trade against each other more than two projectors do (the
    /// simulated pair in ScanEngineTests lands within 0.05°); this one lands at about 0.16°, 0.3 % and 3 mm.</summary>
    private const double RotationToleranceDeg = 0.25;

    private const double ThrowTolerance = 0.006;

    private const double PositionToleranceM = 0.01;

    [Fact]
    public async Task AScanThroughTheRenderHostsOutputFindsTheProjectorAndBlackoutRefusesIt()
    {
        var prior = Intrinsics.FromThrowRatio(256, 160, 1.4);
        var truthRig = new SimulatedRig(SimulatedRig.DefaultScene, 320, 240, [prior]);
        var truth = truthRig.Projector(0);
        var scene = truthRig.Simulator(0, ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = ScanMode.Fast })).Geometry;
        RenderHost? render = null;
        SceneCamera? camera = null;
        PresentLog? presents = null;
        var options = OpsHostTests.Quick(new ScanRunnerOptions
        {
            OpenCamera = _ => camera = new SceneCamera(() => render, "op00000001", scene),
            FlashHold = TimeSpan.FromMilliseconds(150),
            LatencyFlashes = 2,
        });
        var parts = OpsParts.With(EngineParts.Bare with { RenderHost = RenderHost.ForNode }, options) with
        {
            ScanBackend = r =>
            {
                presents = new PresentLog(r.Display);
                return new ScanRunner(presents, options.Scan);
            },
        };
        await using var node = await NodeRig.StartAsync("Render scan node", parts: parts);
        render = Assert.IsType<RenderHost>(node.Host.Render);

        // The projector hangs where the plan says (its distance from the camera sets the scale); its aim and its lens
        // are what the scan finds.
        var aim = truth.Pose.Position + truth.Pose.Forward * 2.5 + new Vec3d(0.3, -0.2, 0.1);
        var project = Project.CreateDefault("Render scan") with
        {
            Outputs = [HostRig.Screen("op00000001", SourceRef.None, 256, 160) with { ProjectorId = "pj00000001" }],
        };
        project = project.WithStage(s => s with
        {
            Projectors = [new Projector("pj00000001", "Left", prior, LensDistortion.None, Pose.LookAt(truth.Pose.Position, aim, Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0)],
            Cameras = [new CameraRig("cm00000001", "Loop", CameraSourceKind.Device, "loop", truthRig.Camera.Intrinsics, LensDistortion.None, truthRig.Camera.Pose, true)],
        });
        var folder = Directory.CreateDirectory(Path.Combine(node.Dir.FullName, "show")).FullName;
        var path = Path.Combine(folder, "show.playmap");
        await File.WriteAllTextAsync(path, ProjectJson.Serialize(project));
        await node.OkAsync("PROJECT OPEN " + WireGrammar.Quote(path));

        var started = await node.RunAsync("SCAN START cm00000001 MODE ROBUST");
        Assert.True(started.Ok, started.Payload);
        var runner = Assert.IsType<ScanRunner>(node.Engine.Services.Scan);
        Assert.True(await Eventually.TrueAsync(() => runner.View is { IsRunning: false }, 120_000), "the scan never ended");
        Assert.True(runner.View!.State == "done", runner.View.Message);
        var record = Assert.Single(node.Engine.Store.Project.Stage.Scans);
        output.WriteLine(record.Notes);
        // Each frame is stamped at its present and read back after it: the latency is made out from the flashes, between their
        // present and the first picture that showed each of them (how far above zero is the camera's delivery delay after the
        // present, which the machine's load sets), never below zero, and the guard against it never fired.
        var (_, latency) = Assert.Single(runner.Find(record.Id)!.Latencies);
        Assert.True(latency.Transitions > 0, latency.Words);
        var flashes = presents!.Presents.Skip(1).Take(4).ToArray();   // black first, then white, black, white, black
        var seen = camera!.Seen;
        var bracketMs = flashes.Select((p, k) => (seen.First(s => s.ArrivalNs > p && s.Green == (k % 2 == 0 ? 255 : 0)).ArrivalNs - p) / 1e6).Max();
        Assert.InRange(latency.LatencyMs, 0, bracketMs);
        Assert.DoesNotContain(node.Engine.Journal.Recent(200), r => r.Text.Contains("below zero", StringComparison.Ordinal));
        var solved = Assert.Single(runner.Find(record.Id)!.Solution.Projectors);
        Assert.True(solved.Solved, solved.Words);
        var (position, degrees) = ScanRunner.PoseError(solved.Pose, truth.Pose);
        var throwError = Math.Abs(solved.Intrinsics.ThrowRatio / truth.Intrinsics.ThrowRatio - 1);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{camera!.Pictures} camera pictures; {position * 1000:0.0} mm, {degrees:0.000}°, throw {solved.Intrinsics.ThrowRatio:0.0000} (truth {truth.Intrinsics.ThrowRatio:0.0000}), {solved.RmsPx:0.000} px"));
        Assert.True(degrees < RotationToleranceDeg, $"{degrees:0.000}°");
        Assert.True(throwError < ThrowTolerance, $"throw {solved.Intrinsics.ThrowRatio:0.0000}");
        Assert.True(position < PositionToleranceM, $"{position * 1000:0.0} mm");

        // The output is the show's again: no scan frame is left on it.
        await render.FramesAsync(3).WaitAsync(HostRig.Patience);
        var after = await render.ReadOutputAsync("op00000001").WaitAsync(HostRig.Patience);
        Assert.NotNull(after);
        Assert.Equal((0, 0, 0), HostRig.At(after, 256, 128, 80));

        // Blackout wins over scan frames: the display refuses them and the scan says why.
        await node.OkAsync("BLACKOUT ON");
        Assert.True((await node.RunAsync("SCAN START cm00000001 MODE FAST")).Ok);
        Assert.True(await Eventually.TrueAsync(() => runner.View is { IsRunning: false } v && v.Id != record.Id, 60_000), "the second scan never ended");
        Assert.Equal("failed", runner.View!.State);
        Assert.Contains("blacked out", runner.View.Message, StringComparison.Ordinal);
    }
}
