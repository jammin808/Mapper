using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Media.Tests.Video;
using PlayMap.Media.Video.Omt;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>The media edge of an engine that routes live items to its hub, as the node's own backend does.</summary>
internal sealed class LiveMedia : IMediaBackend
{
    public EngineCore? Engine { get; set; }

    public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) =>
        throw new NotSupportedException("tests import nothing");

    public IVideoSource? Open(MediaItem item) => Engine?.VideoIo.Open(item);

    public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

    public string? PathForHash(string contentHash) => null;
}

/// <summary>PlayMap → OMT → PlayMap on this machine's loopback with libvmx built from the vendored source: an output's final picture
/// at 1080p — a chart of four coloured blocks — sent as UYVY by the render host's send stage, is received by a live input of the same
/// engine and shown on a second output, every block within a code of luma; the frames arrive in the order sent; one receiver however
/// it is shown; STATE says the input delivers 1920×1080 on air and the send has a receiver and sends. (The lock-step, frame-by-frame
/// check of the same path without a free-running render loop is Media's OmtLoopbackTests.)</summary>
[Collection("render host")]
public sealed class OmtLoopbackTests
{
    [OmtFact]
    public async Task AnOutputTapSentOverOmtIsReceivedAsAnInputAndShownOnAnotherOutput()
    {
        // The input's state is a function of its own clock (its frames' arrivals), held still here: read after the frames, it is the
        // state of a source that is receiving, however long the machine took between (LiveSource's rule is Media's tests').
        using var omt = new OmtProvider(new OmtOptions(Path.Combine(Path.GetTempPath(), "playmap-omt-engine-tests")), new ManualClock(1_000_000_000));
        static Layer Block(string id, string colour, double x, double y) =>
            new(id, id, SourceRef.Solid(colour), new RectD(x, y, 960, 540), 1, LayerBlend.Normal, true);
        var chart = new Canvas("cv_chart", "Chart", 1920, 1080, "#000000",
            [Block("ly_a", "#4080c0", 0, 0), Block("ly_b", "#c04020", 960, 0), Block("ly_c", "#20a040", 0, 540), Block("ly_d", "#e0e0e0", 960, 540)]);
        var project = HostRig.WithOutputs(
            HostRig.Screen("op_1", SourceRef.Canvas("cv_chart"), 1920, 1080),
            HostRig.Screen("op_2", SourceRef.None, 320, 180)) with { Canvases = [chart] };
        var host = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = false }, out var why) ?? throw new InvalidOperationException(why);
        var media = new LiveMedia();
        var services = EngineServices.ForTests(MonoClock.Instance);
        services = services with
        {
            Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] },
            Outputs = host,
            Media = media,
            LiveInputs = [omt],
            SendProviders = [omt],
        };
        var engine = new EngineCore(services, project);
        media.Engine = engine;
        host.Start(engine);
        engine.VideoIo.Start();
        try
        {
            async Task<CommandReply> Ok(string line)
            {
                Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
                var r = await engine.HandleAsync(parsed, SessionInfo.Desk("t"), CancellationToken.None);
                Assert.True(r.Ok, $"{line} → {r.Payload}");
                return r;
            }
            var send = JsonDocument.Parse((await Ok("SEND ADD OMT Loop output:op_1 ALWAYS")).Payload!).RootElement.GetProperty("id").GetString()!;
            var sink = Assert.IsType<OmtSink>(engine.VideoIo.SendOf(send)!.Sink);
            var input = JsonDocument.Parse((await Ok($"INPUT ADD {sink.Url} Loopback")).Payload!).RootElement.GetProperty("id").GetString()!;
            await Ok($"OUTPUT SOURCE op_2 media:{input}");

            var waited = Stopwatch.StartNew();
            byte[] sent, shown;
            while (true)
            {
                await host.FramesAsync(2).WaitAsync(HostRig.Patience);
                sent = await host.ReadOutputAsync("op_1").WaitAsync(HostRig.Patience) ?? [];
                shown = await host.ReadOutputAsync("op_2").WaitAsync(HostRig.Patience) ?? [];
                if (shown.Length > 0 && HostRig.At(shown, 320, 80, 45) != (0, 0, 0) && HostRig.At(shown, 320, 240, 135) != (0, 0, 0))
                    break;
                Assert.True(waited.Elapsed < HostRig.Patience, "op_2 never showed the input");
            }
            static double Luma((int R, int G, int B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            // The middle of each block, sent at 1920×1080 and shown at 320×180.
            foreach (var (x, y) in new[] { (480, 270), (1440, 270), (480, 810), (1440, 810) })
            {
                var a = HostRig.At(sent, 1920, x, y);
                var b = HostRig.At(shown, 320, x / 6, y / 6);
                Assert.True(Math.Abs(Luma(a) - Luma(b)) <= 1.0, $"block at {x},{y}: sent {a}, shown {b}");
            }

            // In order: each frame the input holds is stamped later than the one before (the sender's stamp is the leader instant). The
            // frames are counted as the receiver delivers them, each render frame asking once, until two new ones have come.
            using (var reader = engine.VideoIo.Open(engine.Store.Project.FindMedia(input)!)!)
            {
                var last = long.MinValue;
                var changes = 0;
                var asked = 0;
                var counting = Stopwatch.StartNew();
                while (changes < 2)
                {
                    Assert.True(counting.Elapsed < HostRig.Patience, $"{changes} new frames in {asked} render frames");
                    await host.FramesAsync(1).WaitAsync(HostRig.Patience);
                    asked++;
                    using var lease = reader.Acquire(0);
                    if (lease is null || lease.SenderTimestampNs == last)
                        continue;
                    Assert.True(lease.SenderTimestampNs > last, $"frame stamped {lease.SenderTimestampNs} after {last}");
                    last = lease.SenderTimestampNs;
                    changes++;
                }
            }

            // The tally is the hub's window over the air's draws (a quarter second on the node's clock): it reads program at a tick
            // that finds the input drawn into the air within it, which a tick just after a drawn frame does on any machine but one
            // that holds this thread longer than the window between them.
            JsonElement state = default;
            JsonElement row = default;
            Assert.True(await Host.Eventually.TrueAsync(() =>
            {
                engine.VideoIo.Tick();
                engine.FactsChanged();
                state = JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("videoio").Clone();
                row = state.GetProperty("inputs").EnumerateArray().Single(i => i.GetProperty("id").GetString() == input);
                return row.GetProperty("tally").GetString() == "program";
            }), $"the input's tally reads {row.GetProperty("tally").GetString()}");
            Assert.Equal("ok", row.GetProperty("state").GetString());
            Assert.Equal(1920, row.GetProperty("w").GetInt32());
            Assert.Equal(1080, row.GetProperty("h").GetInt32());
            Assert.Equal("uyvy", row.GetProperty("format").GetString());
            Assert.True(row.GetProperty("received").GetInt64() > 0);
            var sendRow = state.GetProperty("sends").EnumerateArray().Single(s => s.GetProperty("id").GetString() == send);
            Assert.Equal("sending", sendRow.GetProperty("state").GetString());
            Assert.True(sendRow.GetProperty("connections").GetInt32() >= 1);
            Assert.True(sendRow.GetProperty("sent").GetInt64() > 0);
            Assert.EndsWith("(Loop)", sendRow.GetProperty("fullName").GetString(), StringComparison.Ordinal);
            Assert.Equal("ready", state.GetProperty("omt").GetProperty("state").GetString());
            // One receiver for the input however many consumers read it.
            Assert.Equal(1, sink.Connections);
        }
        finally
        {
            await host.DisposeAsync();
            engine.VideoIo.Dispose();
            await engine.DisposeAsync();
        }
    }
}

/// <summary>A live input on the air through the render host: drawn by an output, it is on program (the frame's air drew it), the last
/// pass marks each frame's air ended on the host's own sources (so what is drawn after it, the previews, is preview), and a warm frame
/// allocates nothing on the render thread with the hub made before the frames run.</summary>
[Collection("render host")]
public sealed class LiveOnAirHostTests
{
    [Fact]
    public async Task ALiveInputOnAnOutputIsOnProgramAndAWarmFrameAllocatesNothing()
    {
        var clock = new ManualClock(5_000_000_000);
        using var provider = new FakeVideoProvider(LiveProtocol.Omt, clock);
        var cam = new MediaItem("md_cam", "Cam 1", MediaKind.Stream, "omt:STUDIO (Cam 1)", "", 0, MediaInfo.Unknown, []);
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Media("md_cam"), 64, 36)) with { Media = [cam] };
        var host = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = false }, out var why) ?? throw new InvalidOperationException(why);
        var media = new LiveMedia();
        var services = EngineServices.ForTests(clock);
        services = services with
        {
            Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] },
            Outputs = host,
            Media = media,
            LiveInputs = [provider],
            SendProviders = [provider],
        };
        var engine = new EngineCore(services, project);
        media.Engine = engine;
        engine.VideoIo.Reconcile(engine.Store.Project);
        provider.Opened.Single().Push(64, 36, 150);
        host.Start(engine);
        try
        {
            var endsBefore = host.AirEnds;
            await host.FramesAsync(40).WaitAsync(HostRig.Patience);
            // Every frame's air was marked ended on the sources the frame drew from.
            Assert.True(host.AirEnds - endsBefore >= 39, $"{host.AirEnds - endsBefore} air ends in 40 frames");
            engine.VideoIo.Tick();
            engine.FactsChanged();
            var row = JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("videoio").GetProperty("inputs")[0];
            Assert.Equal("program", row.GetProperty("tally").GetString());
            Assert.Equal((true, false), provider.Opened.Single().Tally);
            Allocations.Begin();
            var before = host.Stats;
            await host.FramesAsync(90).WaitAsync(HostRig.Patience);
            var after = host.Stats;
            Assert.True(after.Frames - before.Frames >= 90);
            Assert.Equal(before.FramePathAllocatedBytes, after.FramePathAllocatedBytes);
        }
        finally
        {
            await host.DisposeAsync();
            engine.VideoIo.Dispose();
            await engine.DisposeAsync();
        }
    }
}
