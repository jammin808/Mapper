using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Engine.VideoIo;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>A packed UYVY frame of one grey, pinned until the sender lets it go.</summary>
internal sealed class GreyFrame : SendFrame
{
    private readonly GCHandle _pin;
    private int _released;

    public GreyFrame(int width, int height, byte code, long leaderNs)
    {
        var bytes = Enumerable.Repeat(code, 2 * width * height).ToArray();
        _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Data = _pin.AddrOfPinnedObject();
        Bytes = bytes.Length;
        Stride = 2 * width;
        Width = width;
        Height = height;
        Format = SendFormat.Uyvy;
        LeaderNs = leaderNs;
        FpsN = 60;
        FpsD = 1;
    }

    public override void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _pin.Free();
    }
}

/// <summary>Sends and receivers as a send stops and as the node stops. A send that stops (SEND ENABLE OFF, SEND REMOVE, the node
/// stopping) sends one black frame before its sender closes, as a stopped output and the LED rig go dark; one made again under its
/// own name (a new size or format) hands over with no black between. Once the node has begun to stop no receiver or sender starts:
/// a consumer gets a handle only on an input already received, the hub reconciles nothing, INPUT RECONNECT says why. A render host
/// still drawing while the hub closes under it raises no fault.</summary>
[Collection("render host")]
public sealed class VideoIoStopTests
{
    private static bool IsUyvyBlack(byte[] frame)
    {
        for (var i = 0; i + 3 < frame.Length; i += 4)
            if (frame[i] != 128 || frame[i + 1] != 16 || frame[i + 2] != 128 || frame[i + 3] != 16)
                return false;
        return frame.Length > 0;
    }

    private static async Task UntilSent(FakeSink sink, long count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (sink.Stats.Sent < count)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"{sink.Stats.Sent} of {count} frames sent");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task ASendThatStopsSendsOneBlackFrameAndOneMadeAgainUnderItsOwnNameDoesNot()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT Main output:op_1"));
        var first = rig.Omt.Sinks.Single();
        first.Offer(new GreyFrame(1280, 720, 0x60, 16_666_600));
        await UntilSent(first, 1);
        // A new format under the same name: the new sender takes the name at once, and the old one sends no black first.
        await rig.Ok($"SET {id} format \"uyva\"");
        rig.Engine.VideoIo.Tick();
        Assert.Equal(1, first.Closes);
        Assert.Single(first.Frames);
        var second = rig.Omt.Sinks[1];
        Assert.Equal(SendFormat.Uyva, second.Spec.Format);
        second.Offer(new GreyFrame(640, 360, 0x60, 33_333_300));
        await UntilSent(second, 1);
        // Stopped: one black frame of its last shape, then it closes.
        await rig.Ok($"SEND ENABLE {id} OFF");
        Assert.Equal(1, second.Closes);
        Assert.Equal(2, second.Frames.Count);
        Assert.Equal(2 * 640 * 360, second.Frames[1].Length);
        Assert.True(IsUyvyBlack(second.Frames[1]));
        // Removed: the same.
        await rig.Ok($"SEND ENABLE {id} ON");
        var third = rig.Omt.Sinks[2];
        third.Offer(new GreyFrame(640, 360, 0x60, 50_000_000));
        await UntilSent(third, 1);
        await rig.Ok($"SEND REMOVE {id}");
        Assert.Equal(2, third.Frames.Count);
        Assert.True(IsUyvyBlack(third.Frames[1]));
    }

    [Fact]
    public async Task AStoppingNodeStartsNoReceiverOrSenderAndAReconnectSaysWhy()
    {
        var warm = new MediaItem("md_warm", "Warm", MediaKind.Stream, "omt:STUDIO (Warm)", "", 0, MediaInfo.Unknown, []);
        var cold = new MediaItem("md_cold", "Cold", MediaKind.Stream, "omt:STUDIO (Cold)", "", 0, MediaInfo.Unknown, [])
        {
            Live = LiveSettings.Default with { Warm = false },
        };
        await using var rig = new VideoIoRig(VideoIoRig.Show() with { Media = [warm, cold] });
        var hub = rig.Engine.VideoIo;
        hub.Reconcile(rig.Engine.Store.Project);
        var received = rig.Omt.Opened.Single();
        Assert.Equal("STUDIO (Warm)", received.Address);
        rig.Engine.BeginStop();
        // A cold input nothing receives yet: no receiver starts for a consumer that comes now.
        Assert.Null(hub.Open(cold));
        // A rig camera's feed the hub never had: none either.
        Assert.Null(hub.Open(new MediaItem("md_rig", "Rig cam", MediaKind.Stream, "omt:STUDIO (Rig)", "", 0, MediaInfo.Unknown, [])));
        // The warm input already received: a handle on it, and still one receiver.
        using (var handle = hub.Open(warm))
            Assert.NotNull(handle);
        Assert.Single(rig.Omt.Opened);
        // What a stopping node's project would start is not started: a send and a new warm input.
        var send = new VideoSend("sd_late", "Late", SendProtocol.Omt, "output:op_1", 0, 0, 0, SendFormat.Auto, true);
        var late = new MediaItem("md_late", "Late", MediaKind.Stream, "omt:STUDIO (Late)", "", 0, MediaInfo.Unknown, []);
        Assert.True((await rig.Engine.Lane.RunAsync(() => rig.Engine.Store.Apply("late edits",
            p => p with { Sends = p.Sends.Add(send), Media = p.Media.Add(late) }))).Changed);
        hub.Reconcile(rig.Engine.Store.Project);
        hub.Tick();
        Assert.Empty(rig.Omt.Sinks);
        Assert.Single(rig.Omt.Opened);
        Assert.True(hub.Plan.IsEmpty);
        // A reconnect that got past the door before the stop began closes nothing and opens nothing.
        Assert.False(hub.Reconnect(hub.InputOf("md_warm")!));
        Assert.False(received.Closed);
        Assert.Single(rig.Omt.Opened);
        // The node's lines are refused at the door with the node's words.
        Assert.Equal(EngineCore.StoppingWords, await rig.Refused("INPUT RECONNECT md_warm"));
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Kind == "fault");
    }

    [Fact]
    public async Task ASendOnTheAirStopsCleanlyWhenTheHubClosesUnderARunningRenderHost()
    {
        var clock = MonoClock.Instance;
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
        var hub = engine.VideoIo;
        hub.Reconcile(engine.Store.Project);
        provider.Opened.Single().Push(64, 36, 150);
        var send = new VideoSend("sd_air", "Air", SendProtocol.Omt, "output:op_1", 0, 0, 0, SendFormat.Uyvy, true) { OnlyWhenWatched = false };
        Assert.True((await engine.Lane.RunAsync(() => engine.Store.Apply("add send", p => p with { Sends = p.Sends.Add(send) }))).Changed);
        hub.Reconcile(engine.Store.Project);
        var sink = provider.Sinks.Single();
        host.Start(engine);
        try
        {
            await host.FramesAsync(10).WaitAsync(HostRig.Patience);
            await UntilSent(sink, 2);
            // The node stops as the host stops it: the lines first, then the hub (an extension) while the render host still draws.
            engine.BeginStop();
            hub.Dispose();
            await host.FramesAsync(20).WaitAsync(HostRig.Patience);
            Assert.Equal(1, sink.Closes);
            Assert.True(IsUyvyBlack(sink.Frames[^1]), "the last frame the send sent is black");
            // A frame offered after the stop is let go, never sent.
            var sent = sink.Stats.Sent;
            sink.Offer(new GreyFrame(64, 36, 0x60, 0));
            Assert.Equal(sent, sink.Stats.Sent);
            Assert.True(provider.Opened.Single().Closed);
            Assert.DoesNotContain(engine.Journal.Recent(500), r => r.Kind == "fault");
        }
        finally
        {
            await host.DisposeAsync();
            hub.Dispose();
            await engine.DisposeAsync();
        }
    }
}
