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
/// at 1080p, sent as UYVY by the render host's send stage, is received by a live input of the same engine and shown on a second
/// output within a code of luma; one receiver however it is shown; STATE says the input delivers 1920×1080 on air and the send has a
/// receiver and sends.</summary>
[Collection("render host")]
public sealed class OmtLoopbackTests
{
    [OmtFact]
    public async Task AnOutputTapSentOverOmtIsReceivedAsAnInputAndShownOnAnotherOutput()
    {
        using var omt = new OmtProvider(new OmtOptions(Path.Combine(Path.GetTempPath(), "playmap-omt-engine-tests")), MonoClock.Instance);
        var project = HostRig.WithOutputs(
            HostRig.Screen("op_1", SourceRef.Solid("#4080c0"), 1920, 1080),
            HostRig.Screen("op_2", SourceRef.None, 320, 180));
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
                if (shown.Length > 0 && HostRig.At(shown, 320, 160, 90) != (0, 0, 0))
                    break;
                Assert.True(waited.Elapsed < HostRig.Patience, "op_2 never showed the input");
            }
            static double Luma((int R, int G, int B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            var a = HostRig.At(sent, 1920, 960, 540);
            var b = HostRig.At(shown, 320, 160, 90);
            Assert.True(Math.Abs(Luma(a) - Luma(b)) <= 1.0, $"sent {a}, shown {b}");

            engine.VideoIo.Tick();
            engine.FactsChanged();
            var state = JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("videoio");
            var row = state.GetProperty("inputs").EnumerateArray().Single(i => i.GetProperty("id").GetString() == input);
            Assert.Equal("ok", row.GetProperty("state").GetString());
            Assert.Equal(1920, row.GetProperty("w").GetInt32());
            Assert.Equal(1080, row.GetProperty("h").GetInt32());
            Assert.Equal("uyvy", row.GetProperty("format").GetString());
            Assert.True(row.GetProperty("received").GetInt64() > 0);
            Assert.Equal("program", row.GetProperty("tally").GetString());
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
