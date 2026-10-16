using System.Diagnostics;
using System.Text.Json;
using PlayMap.Engine.Host;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Host;
using Xunit.Abstractions;
using NodeRig = PlayMap.Engine.Tests.Host.HostRig;
using PictureRig = PlayMap.Engine.Tests.Rendering.HostRig;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>The first map, scripted on a real headless node with the render host and FFmpeg (firstfive M9): LIGHT WALL, SURFACE QUAD,
/// eight nudges, MEDIA IMPORT of a 2 s clip, SURFACE SOURCE. STATE.onboarding reaches "content" with its four instants — the last
/// stamped when the render host drew a frame of the clip on the quad — and the read-back shows the clip inside the quad and black
/// outside. The machine time is recorded; a 4× ceiling on the 5 s bar is asserted here (the bar itself is a Perf measurement).</summary>
[Collection("render host")]
public sealed class FirstMapTests(ITestOutputHelper log)
{
    private static EngineParts WithRender => EngineParts.Bare with { RenderHost = RenderHost.ForNode };

    [FfmpegFact]
    public async Task TheScriptedFirstMapReachesContentWithItsFourInstants()
    {
        var machine = await FirstMapAsync();
        Assert.True(machine < TimeSpan.FromSeconds(20), $"{machine.TotalSeconds:0.0} s (a 4× ceiling on the 5 s bar)");
    }

    /// <summary>The bar itself, run serially by the integration gate.</summary>
    [FfmpegFact]
    [Trait("Category", "Perf")]
    public async Task TheFirstMapTakesUnderFiveSecondsOfMachineTime()
    {
        var machine = await FirstMapAsync();
        Assert.True(machine < TimeSpan.FromSeconds(5), $"{machine.TotalSeconds:0.0} s");
    }

    private async Task<TimeSpan> FirstMapAsync()
    {
        await using var node = await NodeRig.StartAsync("First map", parts: WithRender);
        var render = Assert.IsType<RenderHost>(node.Host.Render);
        var clip = await FfmpegFactAttribute.MakeClipAsync(node.Dir.FullName);
        var watch = Stopwatch.StartNew();

        Assert.Equal("none", node.State().GetProperty("onboarding").GetProperty("stage").GetString());
        using var lit = JsonDocument.Parse(await node.OkAsync("LIGHT WALL op_1"));
        var projector = lit.RootElement.GetProperty("projector").GetString()!;
        using var quad = JsonDocument.Parse(await node.OkAsync($"SURFACE QUAD {projector} 480 270 1440 270 1440 810 480 810 PX AS Wall"));
        var surface = quad.RootElement.GetProperty("id").GetString()!;
        await node.OkAsync($"HANDLE SELECT {surface} TL");
        for (var i = 0; i < 8; i++)
            await node.OkAsync(i % 2 == 0 ? "HANDLE NUDGE 1 0 PX" : "HANDLE NUDGE -1 0 PX");
        using var imported = JsonDocument.Parse(await node.OkAsync("MEDIA IMPORT " + Core.Wire.WireGrammar.Quote(clip) + " Clip"));
        var media = imported.RootElement.GetProperty("id").GetString()!;
        Assert.True(await Eventually.TrueAsync(() => node.Engine.Store.Project.FindMedia(media) is { ContentHash.Length: > 0, Info.Width: > 0 }, 20_000), node.Journal());
        await node.OkAsync($"SURFACE SOURCE {surface} media:{media}");
        Assert.True(await Eventually.TrueAsync(() => node.State().GetProperty("onboarding").GetProperty("stage").GetString() == "content", 20_000),
            node.Journal());
        var machine = watch.Elapsed;
        log.WriteLine($"first map in {machine.TotalSeconds:0.00} s of machine time");

        var onboarding = node.State().GetProperty("onboarding");
        var started = onboarding.GetProperty("startedAt").GetInt64();
        var litAt = onboarding.GetProperty("litAt").GetInt64();
        var quadAt = onboarding.GetProperty("quadAt").GetInt64();
        var contentAt = onboarding.GetProperty("contentAt").GetInt64();
        Assert.True(started <= litAt && litAt <= quadAt && quadAt <= contentAt, onboarding.ToString());
        Assert.True(onboarding.GetProperty("seconds").GetDouble() >= 0);

        // The read-back: the clip inside the quad, black outside (once the overlay and the identify are off the wall).
        await node.OkAsync("OVERLAY OFF");
        var o = node.Engine.Store.Project.FindOutput("op_1")!;
        byte[]? px = null;
        Assert.True(await Eventually.TrueAsync(async () =>
        {
            await render.FramesAsync(2).WaitAsync(PictureRig.Patience);
            px = await render.ReadOutputAsync("op_1").WaitAsync(PictureRig.Patience);
            return px is not null && Mean(px, o.Width, o.Width / 2, o.Height / 2, o.Width / 6) > 20 && Max(px, o.Width, o.Height) <= 2;
        }, 20_000), px is null ? "nothing drawn" : $"inside {Mean(px, o.Width, o.Width / 2, o.Height / 2, o.Width / 6):0.0}, outside max {Max(px, o.Width, o.Height)}");
        return machine;
    }

    /// <summary>The mean level over a square around a point.</summary>
    private static double Mean(byte[] px, int width, int cx, int cy, int half)
    {
        double sum = 0;
        var n = 0;
        for (var y = cy - half; y < cy + half; y++)
            for (var x = cx - half; x < cx + half; x++)
            {
                var (r, g, b) = PictureRig.At(px, width, x, y);
                sum += (r + g + b) / 3.0;
                n++;
            }
        return sum / n;
    }

    /// <summary>The brightest channel along the raster's border band (outside the quad, which spans the middle half).</summary>
    private static int Max(byte[] px, int width, int height)
    {
        var max = 0;
        for (var y = 0; y < height; y += 4)
            for (var x = 0; x < width; x += 4)
            {
                if (x > width / 5 && x < width * 4 / 5 && y > height / 5 && y < height * 4 / 5)
                    continue;
                var (r, g, b) = PictureRig.At(px, width, x, y);
                max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
            }
        return max;
    }
}
