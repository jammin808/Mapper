using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Media.Tests.Video.Spout;
using PlayMap.Media.Video.Spout;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>videoio × d3d11: Spout's provider in videoio's lists. <c>SEND ADD SPOUT</c> and <c>INPUT ADD spout:</c> reach it — off
/// Windows refused "Spout is Windows only" by the provider the exe's composition registers; over a fake kernel the send is made by the
/// provider under its default name and an input is opened by it. A Spout send takes the frames the send stage hands every sender, so it
/// follows the air: the show's picture, black while blacked out, one black frame when it stops.</summary>
public sealed class SpoutProviderTests
{
    private static EngineServices WithSpout(EngineServices services, SpoutProvider provider) => services with
    {
        LiveInputs = [.. services.LiveInputs, provider],
        SendProviders = [.. services.SendProviders, provider],
        GpuSendProviders = [.. services.GpuSendProviders, (ISendSinkProvider)provider],
    };

    private static SpoutProvider Fake(FakeSpoutKernel kernel, FakeSpoutGpu gpu, IMonoClock clock) =>
        new(new SpoutOptions("stage", DiscoveryMs: 0, ReceivePollMs: 0), clock, kernel, () => gpu);

    [Fact]
    public async Task TheCompositionsProviderTakesSpoutLinesAndOffWindowsRefusesThemWithWords()
    {
        var part = SpoutParts.Factory(null!);
        Assert.Contains(SpoutParts.Factory, EngineParts.Standard().Extensions);
        var services = part.Configure(EngineServices.ForTests());
        Assert.Contains(services.LiveInputs, p => p is SpoutProvider);
        Assert.Contains(services.SendProviders, p => p is SpoutProvider);
        Assert.Contains(services.GpuSendProviders, p => p is SpoutProvider);
        await using var engine = new EngineCore(services, VideoIoRig.Show());
        try
        {
            var send = await Run(engine, "SEND ADD SPOUT Main output:op_1 BGRA8");
            var input = await Run(engine, "INPUT ADD spout:Arena");
            engine.FactsChanged();
            var spout = JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("videoio").GetProperty("spout");
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(send.Ok);
                Assert.Equal("Spout is Windows only", send.Payload);
                Assert.False(input.Ok);
                Assert.Equal("Spout is Windows only", input.Payload);
                Assert.Equal("Spout is Windows only", spout.GetProperty("words").GetString());
                Assert.Empty(engine.Store.Project.Sends);
                return;
            }
            // Windows: the provider is the real one (the kernel's objects, a Direct3D 11 device): both lines are taken.
            Assert.True(send.Ok, send.Payload);
            Assert.True(input.Ok, input.Payload);
            Assert.Single(engine.Store.Project.Sends, s => s.Protocol == SendProtocol.Spout);
        }
        finally
        {
            engine.VideoIo.Dispose();
            await part.DisposeAsync();
        }
    }

    [Fact]
    public async Task OverAFakeKernelTheSendAndTheInputAreTheProvidersOwn()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        var clock = new ManualClock(5_000_000_000);
        using var provider = Fake(kernel, gpu, clock);
        await using var engine = new EngineCore(WithSpout(EngineServices.ForTests(clock), provider), VideoIoRig.Show());
        try
        {
            using var arena = new SpoutSender(kernel, gpu, "Arena", SpoutFormats.Bgra8);
            arena.Register(64, 36);
            var input = await Run(engine, "INPUT ADD spout:Arena");
            Assert.True(input.Ok, input.Payload);
            var send = await Run(engine, "SEND ADD SPOUT Main output:op_1 BGRA8 ALWAYS");
            Assert.True(send.Ok, send.Payload);
            engine.VideoIo.Reconcile(engine.Store.Project);
            // The provider made the sender under its default name, beside the app already sending.
            Assert.Equal(["Arena", "PlayMap stage - Main"], SpoutNames.Read(kernel.MapBytes(SpoutNames.List)));
            engine.FactsChanged();
            var videoio = JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("videoio");
            Assert.Equal("ready", videoio.GetProperty("spout").GetProperty("state").GetString());
            var s = Assert.Single(videoio.GetProperty("sends").EnumerateArray());
            Assert.Equal("spout", s.GetProperty("proto").GetString());
            // The input was opened by the provider: a Spout source on Arena.
            var item = engine.Store.Project.Media.Single(m => m.Location == "spout:Arena");
            using var handle = engine.VideoIo.Open(item);
            Assert.NotNull(handle);
        }
        finally
        {
            engine.VideoIo.Dispose();
        }
        Assert.DoesNotContain("PlayMap stage - Main", SpoutNames.Read(kernel.MapBytes(SpoutNames.List) ?? []));
    }

    [Fact]
    public async Task ASpoutSendFollowsTheAirAsTheOutputShowsIt()
    {
        var kernel = new FakeSpoutKernel();
        var gpu = new FakeSpoutGpu();
        var clock = MonoClock.Instance;
        using var provider = Fake(kernel, gpu, clock);
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#336699"), 64, 36));
        var host = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = false }, out var why) ?? throw new InvalidOperationException(why);
        var services = WithSpout(EngineServices.ForTests(clock), provider) with { Outputs = host };
        services = services with { Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] } };
        var engine = new EngineCore(services, project);
        var send = new VideoSend("sd_air", "Air", SendProtocol.Spout, "output:op_1", 0, 0, 0, SendFormat.Bgra8, true) { OnlyWhenWatched = false };
        Assert.True((await engine.Lane.RunAsync(() => engine.Store.Apply("add send", p => p with { Sends = p.Sends.Add(send) }))).Changed);
        engine.VideoIo.Reconcile(engine.Store.Project);
        var info = SpoutInfo.Read(kernel.MapBytes("PlayMap stage - Air"));
        var texture = gpu.Sender(info.ShareHandle)!;
        host.Start(engine);
        try
        {
            await Until(() => Near(texture.Rows, 0x33, 0x66, 0x99), host, "the show's colour reached the Spout texture");
            Assert.True((await engine.HandleAsync(Parse("BLACKOUT ON"), VideoIoRig.Desk, CancellationToken.None)).Ok);
            await Until(() => Near(texture.Rows, 0, 0, 0), host, "blackout reached the Spout texture");
            Assert.True((await engine.HandleAsync(Parse("BLACKOUT OFF"), VideoIoRig.Desk, CancellationToken.None)).Ok);
            await Until(() => Near(texture.Rows, 0x33, 0x66, 0x99), host, "the show came back to the Spout texture");
            // Stopping: one black frame, then the name goes.
            engine.BeginStop();
            engine.VideoIo.Dispose();
            await host.FramesAsync(5).WaitAsync(HostRig.Patience);
            Assert.True(Near(texture.Rows, 0, 0, 0), "the last frame the Spout send published is black");
            Assert.DoesNotContain("PlayMap stage - Air", SpoutNames.Read(kernel.MapBytes(SpoutNames.List) ?? []));
        }
        finally
        {
            await host.DisposeAsync();
            engine.VideoIo.Dispose();
            await engine.DisposeAsync();
        }
    }

    /// <summary>Every pixel of a BGRA picture within 3 codes of (r, g, b).</summary>
    private static bool Near(byte[] bgra, int r, int g, int b)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
            if (Math.Abs(bgra[i] - b) > 3 || Math.Abs(bgra[i + 1] - g) > 3 || Math.Abs(bgra[i + 2] - r) > 3)
                return false;
        return true;
    }

    private static async Task Until(Func<bool> done, RenderHost host, string what)
    {
        var deadline = DateTimeOffset.UtcNow.Add(HostRig.Patience);
        while (!done())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, what);
            await host.FramesAsync(2).WaitAsync(HostRig.Patience);
        }
    }

    private static Core.Wire.WireLine Parse(string line)
    {
        Assert.True(Core.Wire.WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return parsed!;
    }

    private static async Task<Core.Wire.CommandReply> Run(EngineCore engine, string line) =>
        await engine.HandleAsync(Parse(line), VideoIoRig.Desk, CancellationToken.None);
}
