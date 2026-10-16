using System.Runtime.ExceptionServices;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Engine.Tests.Audio;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Tests.Content;

/// <summary>Audio × content (PLAN §6.1): a reactive scene and a Mandelbulb drawn by the renderer through audio's own bus (the node's
/// render bus fed by the capture hub, under the content frame as the render host composes it): a bass burst lifts the picture,
/// never past the picture drawn at the flash guard's audio bound; silence dims it no further than the bound; at sound 0 the burst
/// changes nothing.</summary>
[Collection("render host")]
public sealed class ReactiveRealBusTests
{
    private const int W = 64;
    private const int H = 36;

    public static TheoryData<string> Kinds => new() { "reactive.scene", "fractal.mandelbulb" };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ABassBurstOnTheRealBusLiftsThePictureWithinTheGuardsBound(string kind)
    {
        Generator Gen(string id, ParamSet ps) => new(id, id, kind, ps.With("steps", 48));
        var listening = Gen("gn_ears", ParamSet.Empty.With("sound", 1.0));
        var deaf = Gen("gn_deaf", ParamSet.Empty);
        var high = Gen("gn_high", ParamSet.Empty.With("brightness", 1.0 + FlashGuard.MaxAudioBrightnessSwing));
        var low = Gen("gn_low", ParamSet.Empty.With("brightness", 1.0 - FlashGuard.MaxAudioBrightnessSwing));
        Generator[] generators = [listening, deaf, high, low];
        var project = AudioRig.Show() with
        {
            Generators = [.. generators],
            Outputs = [.. generators.Select(g => Output(g.Id))],
        };
        await using var rig = new AudioRig(project);
        await rig.Ok("AUDIO LISTEN fake:desk CHANNELS 1");
        var capture = rig.Fake.Captures.Single();
        // 500 ms of silence, then a 60 Hz burst for half a second, delivered in 10 ms blocks as they are read.
        const int burstAt = 24_000;
        var sound = Sounds.BassBurst(48_000, burstAt);
        var start = rig.Clock.NowNs;
        for (var at = 0; at < sound.Length; at += 480)
        {
            rig.Clock.NowNs = start + (at + 480) * 1_000_000_000L / 48_000;
            capture.Push(sound.AsSpan(at, 480), rig.Clock.NowNs);
        }
        var burstNs = start + burstAt * 1_000_000_000L / 48_000;
        var bus = rig.Engine.AudioRt.RenderBus;
        var frame = new ContentFrame();
        var pictures = OnGl(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(Dither: false, GpuTiming: false));
            byte[][] Draw(long t, bool hears = true)
            {
                bus.BeginFrame(t);
                engine.Render(new FrameInputs
                {
                    Project = project,
                    LeaderNs = t,
                    Sources = NoSources.Instance,
                    Stage = EmptyStageResources.Instance,
                    Modulation = hears ? frame.Over(bus) : null,
                });
                return [.. generators.Select(g =>
                {
                    var px = new byte[W * H * 4];
                    Assert.True(engine.ReadOutput("op_" + g.Id, px));
                    return px;
                })];
            }
            return (Quiet: Draw(burstNs - 100_000_000), Burst: Draw(burstNs + 300_000_000), Unheard: Draw(burstNs + 300_000_000, hears: false));
        });
        var (quiet, burst, unheard) = pictures;
        // At sound 0 the burst moves nothing: the picture is the one drawn with no bus at all.
        Assert.True(burst[1].AsSpan().SequenceEqual(unheard[1]), $"{kind} at sound 0 heard the burst");
        // The burst lifts the listening picture past the deaf one, never past the picture at the bound's brightness.
        Assert.True(Mean(burst[0]) > Mean(burst[1]) + 1, $"{kind}: the burst lifted nothing ({Mean(burst[1]):0.##} → {Mean(burst[0]):0.##})");
        for (var i = 0; i < burst[0].Length; i++)
            Assert.True(burst[0][i] <= burst[2][i] + 1, $"{kind}: {burst[0][i]} past the bound's {burst[2][i]} at byte {i}");
        // Silence dims it, no further than the bound.
        Assert.True(Mean(quiet[0]) < Mean(quiet[1]), $"{kind}: silence did not dim it");
        for (var i = 0; i < quiet[0].Length; i++)
            Assert.True(quiet[0][i] + 1 >= quiet[3][i], $"{kind}: {quiet[0][i]} under the bound's {quiet[3][i]} at byte {i}");
        // The renderer observed it listening.
        Assert.True(frame.Stats.TryGet("gn_ears", out var live));
        Assert.Equal("", live.Words);
    }

    private static Output Output(string generatorId) =>
        new("op_" + generatorId, generatorId, OutputKind.Virtual, "", W, H, 60, OutputMode.Source, null, SourceRef.Generator(generatorId), new RectD(0, 0, W, H),
            OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [], new RectD(0, 0, W, H), 0, true);

    private static double Mean(byte[] bgra)
    {
        var sum = 0L;
        for (var i = 0; i < bgra.Length; i += 4)
            sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
        return sum / (bgra.Length / 4 * 3.0);
    }

    /// <summary>Runs a body on a thread of its own with a hidden GL context current on it.</summary>
    private static T OnGl<T>(Func<RenderContext, T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = RenderContext.CreateHidden(new Render.Gpu.GlDeviceOptions(CheckErrors: true, DebugOutput: false));
                result = body(context);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        { Name = "reactive real bus" };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
