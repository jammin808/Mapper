using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Host;
using PlayMap.Tests;
using Xunit.Abstractions;
using NodeRig = PlayMap.Engine.Tests.Host.HostRig;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>A fact for the closed browser loop: it runs when PLAYMAP_E2E is 1 and node, Playwright and its Chromium are on the
/// machine; elsewhere it is skipped with the reason.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PLAYMAP_E2E") != "1")
            Skip = "the closed browser loop runs with PLAYMAP_E2E=1 (it takes a few minutes)";
        else if (LoopPhone.Node is null)
            Skip = "node (22 or later) is not installed: the camera page runs under Playwright";
        else if (!LoopPhone.HasPlaywright)
            Skip = "Playwright is not installed: run npm ci in tests/e2e/phonescan";
        else if (!Directory.Exists(LoopPhone.Browsers))
            Skip = "Playwright's Chromium is not installed (PLAYWRIGHT_BROWSERS_PATH)";
    }
}

/// <summary>
/// The phone's camera in the closed loop: the simulator's scene lit by the picture the render host really has on the output (its
/// grey through the projector's gamma 2.2, bilinear, over a little ambient), exposed as a phone exposes — auto-exposure settling on
/// the mean (hunting ±3 % with <see cref="Hunting"/>), or the manual exposure time and ISO the page set — gamma-encoded as a phone's
/// picture is (the level is the light to the power 1/2.2), with a little noise,
/// at 30 frames a second, each frame delivered 120 ms after it was taken, over a WebSocket to the page's synthesised track.
/// </summary>
internal sealed class CameraBridge : IAsyncDisposable
{
    public const double FrameMs = 1000.0 / 30;
    public const double LatencyMs = 120;
    private const double Ambient = 0.03;
    private const double Gain = 0.8;
    private const double AutoTarget = 110;
    private static readonly double[] Lut = Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255.0, 2.2)).ToArray();

    private readonly Func<RenderHost?> _render;
    private readonly string _output;
    private readonly WebApplication _app;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private SimGeometry _scene;
    private Pcg32 _noise = new(7, 11);
    private double _auto = 1;
    private bool _manual;
    private double _manualGain = 1;
    private long _frames;

    public CameraBridge(Func<RenderHost?> render, string outputId, SimGeometry scene, bool hunting)
    {
        _render = render;
        _output = outputId;
        _scene = scene;
        Hunting = hunting;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.UseWebSockets();
        _app.Map("/frames", ServeAsync);
    }

    public bool Hunting { get; }

    /// <summary>Frames sent to the page so far.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>The exposure modes the page set, in order.</summary>
    public List<string> Exposures { get; } = [];

    public string Url { get; private set; } = "";

    public async Task StartAsync()
    {
        await _app.StartAsync(_stop.Token);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Url = "ws" + address["http".Length..] + "/frames";
    }

    /// <summary>The phone stands somewhere else (another view): the scene as traced from its new place.</summary>
    public void Move(SimGeometry scene)
    {
        lock (_gate)
            _scene = scene;
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync().WaitAsync(context.RequestAborted);
        var send = Task.Run(() => SendAsync(socket, _stop.Token), CancellationToken.None);
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
                var m = doc.RootElement;
                if (m.GetProperty("t").GetString() != "exposure")
                    continue;
                lock (_gate)
                {
                    _manual = m.GetProperty("mode").GetString() == "manual";
                    _manualGain = m.GetProperty("exposureTime").GetDouble() / 100 * (m.GetProperty("iso").GetDouble() / 100);
                    Exposures.Add(string.Create(CultureInfo.InvariantCulture, $"{(_manual ? "manual" : "auto")} {_manualGain:0.000}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The bridge stops with the test.
        }
        catch (WebSocketException)
        {
            // The page went away.
        }
        await send;
    }

    /// <summary>One picture a frame period, each sent once its latency has passed.</summary>
    private async Task SendAsync(WebSocket socket, CancellationToken ct)
    {
        var due = new Queue<(double At, byte[] Frame)>();
        var clock = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct) && socket.State == WebSocketState.Open)
            {
                if (await TakeAsync() is { } frame)
                    due.Enqueue((clock.Elapsed.TotalMilliseconds + LatencyMs, frame));
                while (due.Count > 0 && due.Peek().At <= clock.Elapsed.TotalMilliseconds)
                {
                    await socket.SendAsync(due.Dequeue().Frame, WebSocketMessageType.Binary, true, ct);
                    Interlocked.Increment(ref _frames);
                }
                bool manual;
                lock (_gate)
                    manual = _manual;
                if (!manual)
                {
                    var report = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{{\"t\":\"auto\",\"exposureTime\":{_auto * 100:0.###}}}"));
                    await socket.SendAsync(report, WebSocketMessageType.Text, true, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (WebSocketException)
        {
            // The page went away.
        }
    }

    /// <summary>The scene as the camera takes it now: lit by the output's picture, exposed, with noise; 4 bytes of size first.</summary>
    private async Task<byte[]?> TakeAsync()
    {
        if (_render() is not { } host)
            return null;
        byte[]? shown;
        try
        {
            shown = await host.ReadOutputAsync(_output).WaitAsync(TimeSpan.FromSeconds(2), _stop.Token);
        }
        catch (TimeoutException)
        {
            return null;
        }
        if (shown is null)
            return null;
        lock (_gate)
        {
            var scene = _scene;
            var k = scene.Scene.Projector.Intrinsics;
            var n = scene.Width * scene.Height;
            var radiance = new double[n];
            double sum = 0;
            var lit = 0;
            for (var i = 0; i < n; i++)
            {
                if (scene.Triangles[i] < 0)
                    continue;
                var light = Ambient;
                var u = scene.RasterX[i];
                var v = scene.RasterY[i];
                if (!float.IsNaN(u) && !scene.Occluded[i])
                    light += Gain * scene.Cos[i] * Sample(shown, k.Width, k.Height, u - 0.5, v - 0.5);
                radiance[i] = scene.Albedo[i] * light;
                sum += radiance[i];
                lit++;
            }
            if (!_manual && lit > 0)
            {
                // Auto-exposure walks toward a mean level, part of the way a frame.
                var mean = Encode(_auto * sum / lit);
                _auto *= Math.Clamp(Math.Pow(AutoTarget / Math.Max(1, mean), 0.5), 0.8, 1.25);
            }
            var gain = _manual ? _manualGain : _auto;
            if (Hunting)
                gain *= 1 + 0.03 * Math.Sin(2 * Math.PI * Interlocked.Read(ref _frames) / 45.0);
            var frame = new byte[4 + n];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 2), (ushort)scene.Width);
            BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)scene.Height);
            for (var i = 0; i < n; i++)
                frame[4 + i] = (byte)Math.Clamp(Math.Round(Encode(radiance[i] * gain) + _noise.NextGaussian()), 0, 255);
            return frame;
        }
    }

    /// <summary>A phone's picture is gamma-encoded: the level is the light to the power 1/2.2.</summary>
    private static double Encode(double light) => 255 * Math.Pow(Math.Clamp(light, 0, 1), 1 / 2.2);

    private static double Sample(byte[] bgra, int w, int h, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        double At(int px, int py) => Lut[bgra[(Math.Clamp(py, 0, h - 1) * w + Math.Clamp(px, 0, w - 1)) * 4 + 1]];
        return (1 - fy) * ((1 - fx) * At(x0, y0) + fx * At(x0 + 1, y0)) + fy * ((1 - fx) * At(x0, y0 + 1) + fx * At(x0 + 1, y0 + 1));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary>The page in Chromium as the closed loop's phone (tests/e2e/phonescan/loop.mjs), until told to quit.</summary>
internal sealed class LoopPhone : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _out;
    private Task<string> _errors = Task.FromResult("");
    private Task<string> _rest = Task.FromResult("");

    private LoopPhone(Process process, string output)
    {
        _process = process;
        _out = output;
    }

    public static string? Node { get; } = Find("node");

    public static string Browsers => Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "/opt/pw-browsers";

    public static string E2e => Path.Combine(NodeExeRoot(), "tests", "e2e", "phonescan");

    public static bool HasPlaywright =>
        Directory.Exists(Path.Combine(E2e, "node_modules", "playwright"))
        || (GlobalRoot.Value is { Length: > 0 } root && Directory.Exists(Path.Combine(root, "playwright")));

    private static string NodeExeRoot() => Spine.NodeExe.RepoRoot();

    /// <summary>npm's global root, asked once, as <see cref="BrowserFactAttribute"/> asks it while the suite is discovered: ended with
    /// npm, never with its output pipe (<see cref="ToolProbe"/>).</summary>
    private static readonly Lazy<string?> GlobalRoot = new(() => Find("npm") is { } npm ? ToolProbe.Run(npm, ["root", "-g"], ToolProbe.Deadline) : null);

    private static string? Find(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Opens the page and taps Allow; answers once the page says it is ready (the phone has joined).</summary>
    public static async Task<LoopPhone> OpenAsync(string pageUrl, string bridgeUrl, string profile, string folder)
    {
        var output = Path.Combine(folder, "loop-" + profile + ".json");
        var start = new ProcessStartInfo(Node!)
        {
            WorkingDirectory = E2e,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "loop.mjs", pageUrl, bridgeUrl, profile, CameraBridge.LatencyMs.ToString(CultureInfo.InvariantCulture), output })
            start.ArgumentList.Add(a);
        start.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Browsers;
        var process = Process.Start(start)!;
        var phone = new LoopPhone(process, output);
        try
        {
            // What the browser writes is drained as it comes: a full pipe would stop it.
            phone._errors = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null || line.StartsWith("FAILED", StringComparison.Ordinal))
                    throw new InvalidOperationException("the page did not join: " + line + " " + await phone._errors.WaitAsync(cts.Token));
                if (line == "READY")
                {
                    phone._rest = process.StandardOutput.ReadToEndAsync();
                    return phone;
                }
            }
        }
        catch
        {
            await phone.DisposeAsync();
            throw;
        }
    }

    /// <summary>Quits the browser and answers what the driver saw.</summary>
    public async Task<JsonElement> QuitAsync()
    {
        await _process.StandardInput.WriteLineAsync("quit");
        await _process.StandardInput.FlushAsync();
        if (!await ChildProcess.EndAsync(_process, TimeSpan.FromSeconds(60), _errors, _rest))
            throw new TimeoutException("the browser's driver did not quit within 60 s and was stopped: " + (_errors.IsCompletedSuccessfully ? _errors.Result : ""));
        return JsonDocument.Parse(await File.ReadAllTextAsync(_out)).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await ChildProcess.StopAsync(_process, _errors, _rest);
        _process.Dispose();
    }
}

/// <summary>
/// The closed browser loop (wp2-multiscan): a real node with its render host on Xvfb, the phone hub on its web port and the scan runner;
/// the real camera page in Chromium, its camera a synthesised track of the simulator's scene lit by what the node's output really shows
/// (<see cref="CameraBridge"/>: 30 frames a second, 120 ms late). An Android profile (manual exposure honoured) scans a projector to
/// within the render loop's bar — 10 mm, 0.25°, 0.6 % of throw — and a second view from elsewhere joins the scan INTO it, both views
/// solved together; an iOS profile (no controls, auto-exposure hunting ±3 %) completes a scan with brightness kept steady. Each
/// projector's run is counted in camera frames against 15 s.
/// </summary>
[Collection("render host")]
public sealed class PhoneScanE2ETests(ITestOutputHelper output)
{
    private const double RotationToleranceDeg = 0.25;
    private const double ThrowTolerance = 0.006;
    private const double PositionToleranceM = 0.01;

    /// <summary>15 s of a 30 fps camera.</summary>
    private const int FramesPerProjector = 450;

    private sealed record Loop(NodeRig Node, CameraBridge Bridge, LoopPhone Phone, SimulatedRig Truth, SimGeometry Scene, string PhoneId) : IAsyncDisposable
    {
        public ScanRunner Runner => (ScanRunner)Node.Engine.Services.Scan;

        public async ValueTask DisposeAsync()
        {
            await Phone.DisposeAsync();
            await Bridge.DisposeAsync();
            await Node.DisposeAsync();
        }
    }

    private static async Task<Loop> StartAsync(string profile)
    {
        var prior = Intrinsics.FromThrowRatio(256, 160, 1.4);
        var truthRig = new SimulatedRig(SimulatedRig.DefaultScene, 320, 240, [prior]);
        var truth = truthRig.Projector(0);
        var scene = truthRig.Simulator(0, ScanPlan.Create(256, 160)).Geometry;
        RenderHost? render = null;
        var parts = OpsParts.With(EngineParts.Bare with { RenderHost = RenderHost.ForNode, Extensions = [PhoneExtension.Factory] },
            OpsHostTests.Quick(new ScanRunnerOptions { PatternTimeout = TimeSpan.FromSeconds(6) }));
        // The node's phone hub runs on a time the loop holds, so the browser's pings, paced by the real clock, are never raced against the
        // hub's hello wait and its 10 s of silence; the scan's own patience still counts real time.
        var node = await NodeRig.StartAsync("Loop node", parts: parts, time: new ManualTime());
        CameraBridge? bridge = null;
        LoopPhone? phone = null;
        try
        {
            render = Assert.IsType<RenderHost>(node.Host.Render);
            var aim = truth.Pose.Position + truth.Pose.Forward * 2.5 + new Vec3d(0.3, -0.2, 0.1);
            var project = Project.CreateDefault("Phone loop") with
            {
                Outputs = [Rendering.HostRig.Screen("op00000001", SourceRef.None, 256, 160) with { ProjectorId = "pj00000001" }],
            };
            project = project.WithStage(s => s with
            {
                Projectors = [new Projector("pj00000001", "Left", prior, LensDistortion.None, Pose.LookAt(truth.Pose.Position, aim, Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0)],
            });
            var folder = Directory.CreateDirectory(Path.Combine(node.Dir.FullName, "show")).FullName;
            var path = Path.Combine(folder, "show.playmap");
            await File.WriteAllTextAsync(path, ProjectJson.Serialize(project));
            await node.OkAsync("PROJECT OPEN " + WireGrammar.Quote(path));

            bridge = new CameraBridge(() => render, "op00000001", scene, hunting: profile == "ios");
            await bridge.StartAsync();
            var invite = JsonDocument.Parse(await node.OkAsync("PHONE INVITE " + (profile == "ios" ? "iPhone" : "Pixel 8"))).RootElement;
            var url = new UriBuilder(invite.GetProperty("url").GetString()!) { Host = "127.0.0.1" }.Uri.ToString();
            phone = await LoopPhone.OpenAsync(url, bridge.Url, profile, node.Dir.FullName);
            var hub = node.Engine.Services.Phones!;
            Assert.True(await Eventually.TrueAsync(() => hub.Phones().Any(p => p.Connected && p.Rig.Length > 0), 30_000), "the phone never joined");
            var facts = hub.Phones().Single(p => p.Connected);
            // The phone's optics as an earlier scan solved them, where it stands: the scan finds the projector.
            var cam = truthRig.Camera;
            var r = await node.Engine.EditAsync(EngineCore.SystemSession, "the phone's optics", p => p.WithStage(s => s with
            {
                Cameras = s.Cameras.Update(facts.Rig, c => c with { Intrinsics = cam.Intrinsics, Lens = cam.Lens, Pose = cam.Pose, Calibrated = true }),
            }));
            Assert.True(r.Changed, r.Effect);
            return new Loop(node, bridge, phone, truthRig, scene, facts.Id);
        }
        catch
        {
            // A loop that did not start leaves no browser, bridge or node behind.
            if (phone is not null)
                await phone.DisposeAsync();
            if (bridge is not null)
                await bridge.DisposeAsync();
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a scan line to its end; answers the scan and the camera frames its capture took — from the line until the first
    /// projector's decoding begins (prepare, locate, the patterns and the closing check; the solve after is not the camera's).</summary>
    private async Task<(string Id, long Frames)> ScanAsync(Loop loop, string line)
    {
        var before = loop.Bridge.Frames;
        long captured = -1;
        void Watch(ScanView v)
        {
            if (v.Stage == "decoding")
                Interlocked.CompareExchange(ref captured, loop.Bridge.Frames - before, -1);
        }
        loop.Runner.Progressed += Watch;
        var started = await loop.Node.RunAsync(line);
        Assert.True(started.Ok, started.Payload);
        Assert.True(await Eventually.TrueAsync(() => loop.Runner.View is { IsRunning: false }, 240_000), "the scan never ended");
        loop.Runner.Progressed -= Watch;
        var view = loop.Runner.View!;
        var frames = Interlocked.Read(ref captured);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{line}: {view.State} — {view.Message} ({frames} camera frames)"));
        output.WriteLine(loop.Node.State().GetProperty("scan").GetProperty("pairing").GetRawText());
        Assert.True(view.State == "done", view.Message);
        return (view.Id, frames);
    }

    private void Placed(ScanOutcome outcome, SimDevice truth)
    {
        var solved = Assert.Single(outcome.Solution.Projectors);
        Assert.True(solved.Solved, solved.Words);
        var (position, degrees) = ScanRunner.PoseError(solved.Pose, truth.Pose);
        var throwError = Math.Abs(solved.Intrinsics.ThrowRatio / truth.Intrinsics.ThrowRatio - 1);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{position * 1000:0.0} mm, {degrees:0.000}°, throw {throwError:P2}, {solved.RmsPx:0.000} px"));
        Assert.True(degrees < RotationToleranceDeg, $"{degrees:0.000}°");
        Assert.True(throwError < ThrowTolerance, $"throw {throwError:P2}");
        Assert.True(position < PositionToleranceM, $"{position * 1000:0.0} mm");
    }

    [BrowserFact]
    public async Task AnAndroidPhoneScansAProjectorThroughTheRealPageAndASecondViewJoinsTheScan()
    {
        await using var loop = await StartAsync("android");
        var (id, frames) = await ScanAsync(loop, $"SCAN START phone:{loop.PhoneId} pj00000001 MODE ROBUST");
        var pairing = loop.Node.State().GetProperty("scan").GetProperty("pairing");
        Assert.Equal("locked", pairing.GetProperty("exposure").GetString());
        Assert.Contains(loop.Bridge.Exposures, e => e.StartsWith("manual", StringComparison.Ordinal));
        Placed(loop.Runner.Find(id)!, loop.Truth.Projector(0));
        Assert.True(frames <= FramesPerProjector, $"{frames} camera frames for one projector (15 s is {FramesPerProjector})");

        // The phone moves to another place and adds a view INTO the scan: both views solve together.
        var cam = loop.Truth.Camera;
        var target = cam.Pose.Position + cam.Pose.Forward * 2.5;
        var eye = cam.Pose.Position + Vec3d.Cross(cam.Pose.Forward, Vec3d.UnitY).Normalized() * 0.35 + new Vec3d(0, 0.1, 0);
        var moved = loop.Scene.Scene with { Camera = new SimDevice(cam.Intrinsics, cam.Lens, Pose.LookAt(eye, target, Vec3d.UnitY)) };
        loop.Bridge.Move(new SimGeometry(moved));
        var (again, second) = await ScanAsync(loop, $"SCAN START phone:{loop.PhoneId} pj00000001 MODE ROBUST INTO {id}");
        Assert.Equal(id, again);
        var outcome = loop.Runner.Find(id)!;
        output.WriteLine(outcome.Solution.Words);
        Assert.Equal(["v1", "v2"], outcome.Multi!.ViewIds.ToArray());
        Assert.All(outcome.Multi.Result.Views, v => Assert.True(v.RmsPx <= 0.5, string.Create(CultureInfo.InvariantCulture, $"{v.Id} {v.RmsPx:0.000} px")));
        Assert.True(outcome.Multi.Result.RmsPx <= 0.5);
        Assert.Equal(["tag", "tag"], outcome.Captured.Select(v => v.Pairing));
        Assert.True(second <= FramesPerProjector, $"{second} camera frames for the second view");
        var seen = await loop.Phone.QuitAsync();
        output.WriteLine(seen.GetRawText());
        Assert.Contains(seen.GetProperty("prepared").EnumerateArray(), p => p.GetProperty("exposure").GetString() == "locked");
    }

    [BrowserFact]
    public async Task AnIphoneWithoutExposureControlsCompletesAScanWhileItsExposureHunts()
    {
        await using var loop = await StartAsync("ios");
        var (id, frames) = await ScanAsync(loop, $"SCAN START phone:{loop.PhoneId} pj00000001 MODE ROBUST");
        var pairing = loop.Node.State().GetProperty("scan").GetProperty("pairing");
        Assert.Equal("steady", pairing.GetProperty("exposure").GetString());
        Assert.Empty(loop.Bridge.Exposures);
        var solved = Assert.Single(loop.Runner.Find(id)!.Solution.Projectors);
        Assert.True(solved.Solved, solved.Words);
        Placed(loop.Runner.Find(id)!, loop.Truth.Projector(0));
        Assert.True(frames <= FramesPerProjector, $"{frames} camera frames for one projector (15 s is {FramesPerProjector})");
        var seen = await loop.Phone.QuitAsync();
        Assert.Contains(seen.GetProperty("prepared").EnumerateArray(), p => p.GetProperty("exposure").GetString() == "steady");
    }
}
