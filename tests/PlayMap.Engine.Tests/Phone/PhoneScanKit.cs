using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Scanning;
using PlayMap.Net.Http;
using PlayMap.Net.Phone;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>A scan display that keeps what it shows: the picture on the glass now and the one before (what a late camera still
/// sees), each present stamped on the monotonic clock; whether the outputs were given back.</summary>
internal sealed class GlassDisplay : IScanDisplay
{
    private readonly Lock _gate = new();
    private byte[] _now = [];
    private byte[] _before = [];
    private int _shown;
    private int _released;

    public (byte[] Now, byte[] Before) Glass
    {
        get
        {
            lock (_gate)
                return (_now, _before);
        }
    }

    public int Shown => Volatile.Read(ref _shown);

    public int Released => Volatile.Read(ref _released);

    public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
    {
        lock (_gate)
        {
            _before = _now;
            _now = gray8.ToArray();
        }
        Interlocked.Increment(ref _shown);
        return Task.FromResult(MonoClock.Instance.NowNs);
    }

    public void Release() => Interlocked.Increment(ref _released);
}

/// <summary>
/// A phone as the camera page behaves, in C#: it joins with its ticket, answers prepare with the profile it can run, keeps the
/// sites, and for each cue watches its camera — the simulated scene lit by what the display has on the glass — until two consecutive
/// pictures show the cue's tag at every lit site (or, for locate frames, have changed) and agree; then it says seen and uploads the
/// second as PMF1. Faults are injected by pattern index: a late camera (its first pictures still show the pattern before), a
/// rolling-shutter mix (top half the new pattern, bottom the old), a dropped upload (seen said, picture never sent), a lie (seen
/// said at once with a picture of the old pattern), and a phone that goes silent or hangs up.
/// </summary>
internal sealed class FakePhone : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly GlassDisplay _glass;
    private readonly ScanSimulator? _camera;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _locks;
    private readonly CancellationTokenSource _stop = new();
    private Pcg32 _gain;
    private double _gainNow = 1;
    private List<TagSitePlacement> _sites = [];
    private int _minSites;
    private PixelRect _lit;
    private byte[]? _lastGrid;
    private long _seq;
    private ulong _frameSeed = 1;
    private Task? _loop;
    private int _cues;

    /// <summary>A phone that answers still cues with what <paramref name="still"/> shows (an align run's camera), at its raster.</summary>
    public FakePhone(Func<CancellationToken, Task<CameraFrame>> still, int width, int height)
        : this(new GlassDisplay(), null, locks: true)
    {
        Still = still;
        (_width, _height) = (width, height);
    }

    public FakePhone(GlassDisplay glass, ScanSimulator? camera, bool locks, double gainWalk = 0)
    {
        _glass = glass;
        _camera = camera;
        _locks = locks;
        GainWalk = gainWalk;
        _gain = new Pcg32(77);
        (_width, _height) = camera is null ? (0, 0) : (camera.CameraWidth, camera.CameraHeight);
    }

    /// <summary>What a still cue is answered with (null: the scene lit by the glass, as for a pattern).</summary>
    public Func<CancellationToken, Task<CameraFrame>>? Still { get; init; }

    public int Stills => Volatile.Read(ref _stills);

    private int _stills;

    public double GainWalk { get; }

    public HashSet<int> Late { get; } = [];

    public HashSet<int> Half { get; } = [];

    public HashSet<int> DropUpload { get; } = [];

    public HashSet<int> Lie { get; } = [];

    /// <summary>After this many cues the phone stops answering (its socket stays open).</summary>
    public int SilentAfter { get; set; } = int.MaxValue;

    /// <summary>After this many cues the phone hangs up.</summary>
    public int HangUpAfter { get; set; } = int.MaxValue;

    public int Cues => Volatile.Read(ref _cues);

    /// <summary>What this phone says its pictures' capture instants were, against the truth (ms; set before joining for a clock that is
    /// off by a constant, after joining for one that says pictures were taken before they were).</summary>
    public double CaptureSkewMs { get; set; }

    private double PageMs => MonoClock.Instance.NowNs / 1e6;

    public int RollingSeen { get; private set; }

    public string Id { get; private set; } = "";

    public string Rig { get; private set; } = "";

    public async Task JoinAsync(int port, string ticket)
    {
        _ws.Options.AddSubProtocol(PhoneProtocol.SubProtocol);
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/scan/ws"), _stop.Token);
        await SendAsync(new
        {
            t = "hello",
            v = 1,
            ticket,
            ua = new { platform = _locks ? "android-chrome" : "ios-safari", model = _locks ? "Pixel 8" : "iPhone" },
            camera = new
            {
                label = "camera2 0, facing back",
                width = _width,
                height = _height,
                fps = 30,
                caps = _locks ? new { exposure = new[] { "manual", "continuous" }, exposureTime = new[] { 10, 1000, 5 } } : new { exposure = Array.Empty<string>(), exposureTime = Array.Empty<int>() },
            },
        });
        var welcome = await ReceiveAsync() ?? throw new InvalidOperationException($"refused: {_ws.CloseStatusDescription}");
        Id = welcome.GetProperty("phone").GetProperty("id").GetString()!;
        Rig = welcome.GetProperty("camera").GetString()!;
        // One clock exchange, as the page makes every second: this phone's clock is the node's own (plus any skew a test sets).
        var t1 = PageMs;
        await SendAsync(new { t = "ping", t1 });
        var pong = await ReceiveAsync() ?? throw new InvalidOperationException("no pong");
        await SendAsync(new { t = "ping", t1 = PageMs, prev = new { t1, t2 = pong.GetProperty("t2").GetDouble(), t3 = pong.GetProperty("t3").GetDouble(), t4 = PageMs } });
        _loop = Task.Run(LoopAsync, _stop.Token);
    }

    private async Task SendAsync(object m)
    {
        if (_ws.State == WebSocketState.Open)
            await _ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m)), WebSocketMessageType.Text, true, _stop.Token);
    }

    private async Task<JsonElement?> ReceiveAsync()
    {
        var buffer = new byte[1 << 20];
        var length = 0;
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer.AsMemory(length), _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close)
                return null;
            length += r.Count;
            if (r.EndOfMessage)
                return JsonDocument.Parse(buffer.AsMemory(0, length)).RootElement.Clone();
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await ReceiveAsync() is { } m)
            {
                switch (m.GetProperty("t").GetString())
                {
                    case "prepare":
                        await SendAsync(new
                        {
                            t = "prepared",
                            exposure = _locks ? "locked" : "steady",
                            exposureMs = _locks ? 16.7 : 0,
                            iso = _locks ? 200 : 0,
                            focus = _locks ? "locked" : "auto",
                            whiteBalance = "not offered by this camera",
                            words = _locks ? "Exposure locked" : "iPhone cannot lock exposure from a web page: brightness kept steady instead",
                        });
                        break;
                    case "sites":
                        _minSites = m.GetProperty("minSites").GetInt32();
                        var lit = m.GetProperty("lit");
                        _lit = new PixelRect(lit[0].GetInt32(), lit[1].GetInt32(), lit[2].GetInt32(), lit[3].GetInt32());
                        _sites = [.. m.GetProperty("sites").EnumerateArray().Select(s => new TagSitePlacement(s.GetProperty("id").GetInt32(),
                            [.. s.GetProperty("cells").EnumerateArray().Select(c => new Vec2d(c[0].GetDouble(), c[1].GetDouble()))],
                            s.GetProperty("rows")[0].GetInt32(), s.GetProperty("rows")[1].GetInt32()))];
                        break;
                    case "cue":
                        var n = Interlocked.Increment(ref _cues);
                        if (n > HangUpAfter)
                        {
                            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", _stop.Token);
                            return;
                        }
                        if (n > SilentAfter)
                            break;
                        await CueAsync(m);
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The test ended the phone, or the node hung up on it.
        }
    }

    private byte[] Picture(byte[] raster)
    {
        if (GainWalk > 0)
            _gainNow *= 1 + GainWalk * _gain.NextGaussian();
        return _camera!.RenderRaster(raster, _frameSeed++, _gainNow);
    }

    private byte[] Grid(byte[] gray)
    {
        var (w, h) = (_width, _height);
        var r = _lit.IsEmpty ? new PixelRect(0, 0, w, h) : _lit;
        var g = new byte[48 * 27];
        for (var j = 0; j < 27; j++)
        {
            var y = Math.Min(h - 1, (int)(r.Y + (j + 0.5) * r.Height / 27));
            for (var i = 0; i < 48; i++)
                g[j * 48 + i] = gray[y * w + Math.Min(w - 1, (int)(r.X + (i + 0.5) * r.Width / 48))];
        }
        return g;
    }

    private static double Diff(byte[] a, byte[] b)
    {
        double s = 0;
        for (var k = 0; k < a.Length; k++)
            s += Math.Abs(a[k] - b[k]);
        return s / a.Length;
    }

    private async Task CueAsync(JsonElement m)
    {
        var id = m.GetProperty("id").GetInt64();
        var kind = m.GetProperty("kind").GetString();
        var index = m.GetProperty("i").GetInt32();
        var tag = m.GetProperty("tag").GetInt32();
        var minChange = m.GetProperty("minChange").GetDouble();
        var retake = m.GetProperty("retake").GetInt32();
        if (kind == "still")
        {
            Interlocked.Increment(ref _stills);
            var gray = Still is { } still ? (await still(_stop.Token)).Gray : Picture(_glass.Glass.Now);
            await SeenAsync(id, "still", 0, gray, 0);
            return;
        }
        var (now, before) = _glass.Glass;
        var (w, h) = (_width, _height);
        var pattern = kind == "pattern";
        if (pattern && retake == 0 && Lie.Remove(index))
        {
            // A phone pairing by guesswork: it says seen at once, with the picture of the pattern before.
            await SeenAsync(id, kind!, tag, Picture(before.Length > 0 ? before : now), 0);
            return;
        }
        var stream = new List<byte[]>();
        if (pattern && Late.Contains(index) && before.Length > 0)
        {
            stream.Add(Picture(before));
            stream.Add(Picture(before));
        }
        if (pattern && retake == 0 && Half.Remove(index) && before.Length > 0)
        {
            var top = Picture(now);
            var bottom = Picture(before);
            Array.Copy(bottom, h / 2 * w, top, h / 2 * w, w * h - h / 2 * w);
            stream.Add(top);
        }
        for (var k = 0; k < 12; k++)
            stream.Add(Picture(now));
        byte[]? previous = null;
        var rolling = 0;
        foreach (var gray in stream)
        {
            var grid = Grid(gray);
            bool matches;
            if (pattern && _sites.Count > 0)
            {
                var read = FrameTag.ReadFrame(gray, w, h, _sites, _minSites);
                if (read.Outcome == FrameTagOutcome.Disagree)
                {
                    rolling++;
                    RollingSeen++;
                }
                matches = read.Outcome == FrameTagOutcome.Read && read.Value == tag;
            }
            else
            {
                matches = _lastGrid is null || Diff(_lastGrid, grid) >= minChange;
            }
            if (!matches)
            {
                previous = null;
                continue;
            }
            if (previous is not null && Diff(previous, grid) < 3)
            {
                _lastGrid = grid;
                var send = !(pattern && retake == 0 && DropUpload.Remove(index));
                await SeenAsync(id, kind!, tag, gray, rolling, upload: send);
                return;
            }
            previous = grid;
        }
        await SendAsync(new { t = "stuck", cue = id, why = "tag not seen" });
    }

    private async Task SeenAsync(long cue, string kind, int tag, byte[] gray, int rolling, bool upload = true)
    {
        var seq = ++_seq;
        var capture = PageMs + CaptureSkewMs;
        await SendAsync(new { t = "seen", cue, seq, tag, capture, sites = _sites.Count, margin = 10, settleDiff = 1.0, rolling });
        if (!upload)
            return;
        var (w, h) = (_width, _height);
        var frame = Pmf1.Write(new Pmf1Header(kind is "locate" or "still" ? kind : "pattern", seq, (int)cue, w, h, new PixelRect(0, 0, w, h), Pmf1.Gray8,
            Pmf1.Deflate, 1, 0, capture), gray);
        await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, _stop.Token);
    }

    /// <summary>Hangs up as a page closing does (a close frame, answered), then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", bye.Token);
                if (_loop is not null)
                    await _loop.WaitAsync(bye.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException)
            {
                // The node went first.
            }
        }
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop;
        _ws.Dispose();
        _stop.Dispose();
    }
}

/// <summary>An engine with a scan runner on a <see cref="GlassDisplay"/>, the phone hub on a real web port on loopback (the door to
/// invites a fixed https address), one projector driven by one output, and the floor-and-box scene as the world the phone's
/// camera sees.</summary>
internal sealed class PhoneScanRig : IAsyncDisposable
{
    private readonly IAsyncDisposable _ops;

    private PhoneScanRig(string folder, GlassDisplay display, ScanRunner runner, PhoneHub hub, HttpHost host, EngineCore engine, IAsyncDisposable ops,
        SimulatedRig truth, ScanSimulator camera)
    {
        Folder = folder;
        Display = display;
        Runner = runner;
        Hub = hub;
        Host = host;
        Engine = engine;
        _ops = ops;
        Truth = truth;
        Camera = camera;
    }

    public static readonly SessionInfo Desk = new("d1", "desk", "127.0.0.1", true, "Desk", true);

    public string Folder { get; }

    public GlassDisplay Display { get; }

    public ScanRunner Runner { get; }

    public PhoneHub Hub { get; }

    public HttpHost Host { get; }

    public EngineCore Engine { get; }

    public SimulatedRig Truth { get; }

    /// <summary>What the phone's camera sees: the scene lit by any projector picture.</summary>
    public ScanSimulator Camera { get; }

    public static async Task<PhoneScanRig> StartAsync(ScanRunnerOptions? scan = null, Func<SessionInfo, CancellationToken, Task<PhoneDoor>>? door = null,
        double cameraNoise = 1)
    {
        var folder = Directory.CreateTempSubdirectory("playmap-phonescan-").FullName;
        var display = new GlassDisplay();
        var runner = new ScanRunner(display, scan ?? new ScanRunnerOptions { PatternTimeout = TimeSpan.FromSeconds(2) });
        var hub = new PhoneHub(new PhoneHubOptions { KeysPath = Path.Combine(folder, "phones.json"), NodeId = "0badc0de", NodeName = "Test node" });
        var prior = Intrinsics.FromThrowRatio(256, 160, 1.4);
        var truth = new SimulatedRig(SimulatedRig.DefaultScene, 320, 240, [prior]);
        var t = truth.Projector(0);
        var aim = t.Pose.Position + t.Pose.Forward * 2.5 + new Vec3d(0.3, -0.2, 0.1);
        var project = Project.CreateDefault("Phone scan") with
        {
            Outputs = [Rendering.HostRig.Screen("op00000001", SourceRef.None, 256, 160) with { ProjectorId = "pj00000001" }],
        };
        project = project.WithStage(s => s with
        {
            Projectors = [new Projector("pj00000001", "Left", prior, LensDistortion.None, Pose.LookAt(t.Pose.Position, aim, Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0)],
        });
        var services = EngineServices.ForTests(MonoClock.Instance);
        services = services with
        {
            Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] },
            Scan = runner,
            ProjectFolder = folder,
            JournalPath = Path.Combine(folder, "journal.jsonl"),
            Phones = hub,
            PhoneDoor = door ?? ((_, _) => Task.FromResult(new PhoneDoor(new Uri("https://192.168.1.44:8443"), "1a2b 3c4d 5e6f 7a8b", ""))),
            PreflightChecks = [new PhonePreflight()],
        };
        var engine = new EngineCore(services, project);
        var ops = OpsServices.Attach(engine);
        engine.AttachPhones(hub);
        var host = new HttpHost(services.Settings, engine, services.Wall, new HttpHostOptions { Port = 0, Modules = [hub.Module], Mcp = false });
        await host.StartAsync();
        var sim = new ScanSimulator(new SimGeometry(SimScenes.FloorAndBox(256, 160, 320, 240) with { Projector = t }),
            ScanPlan.Create(256, 160), new SimOptions { NoiseSigma = cameraNoise });
        return new PhoneScanRig(folder, display, runner, hub, host, engine, ops, truth, sim);
    }

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed!, session ?? Desk, CancellationToken.None);
    }

    public async Task<JsonElement> Ok(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload is { Length: > 0 } p && p[0] is '{' or '[' ? JsonDocument.Parse(p).RootElement.Clone() : default;
    }

    public JsonElement State()
    {
        Engine.FactsChanged();
        return JsonDocument.Parse(Engine.State().Json).RootElement.Clone();
    }

    /// <summary>A phone invited from the desk and joined; its rig made calibrated at the camera's true place (a phone whose optics
    /// an earlier scan solved).</summary>
    public async Task<FakePhone> JoinAsync(bool locks = true, double gainWalk = 0, string name = "Pixel 8")
    {
        var invite = await Ok("PHONE INVITE " + name);
        var ticket = new Uri(invite.GetProperty("url").GetString()!).Fragment["#t=".Length..];
        var phone = new FakePhone(Display, Camera, locks, gainWalk);
        await phone.JoinAsync(Host.Port, ticket);
        var cam = Truth.Camera;
        var r = await Engine.EditAsync(Desk, "the phone's optics", p => p.WithStage(s => s with
        {
            Cameras = s.Cameras.Update(phone.Rig, c => c with { Intrinsics = cam.Intrinsics, Lens = cam.Lens, Pose = cam.Pose, Calibrated = true }),
        }));
        Assert.True(r.Changed, r.Effect);
        return phone;
    }

    /// <summary>Waits for the scan to end (a published fact), then answers its view.</summary>
    public async Task<ScanView> EndedAsync(int timeoutMs = 120_000)
    {
        Assert.True(await OpsRig.Eventually(() => Runner.View is { IsRunning: false }, timeoutMs), "the scan never ended");
        return Runner.View!;
    }

    /// <summary>Leaves the rig's folder after it is disposed (a test reads the journal file, then calls <see cref="DeleteFolder"/>).</summary>
    public bool KeepFolder { get; set; }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await Host.DisposeAsync();
        await Hub.DisposeAsync();
        await _ops.DisposeAsync();
        await Engine.DisposeAsync();
        if (!KeepFolder)
            DeleteFolder();
    }

    public void DeleteFolder()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held: the temp folder is the OS's to clear.
        }
    }
}
