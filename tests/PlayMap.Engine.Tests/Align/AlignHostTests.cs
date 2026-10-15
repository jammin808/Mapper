using PlayMap.Core.Align;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>The measured correction through the render host (headless, Mesa): an output whose project names a measured warp (the
/// picture squeezed into the middle half) and a measured blend (half the white, in light) shows exactly that, read back from the
/// air; the same project on a node whose folder lacks the files shows the output without its correction, and STATE says which
/// file.</summary>
[Collection("render host")]
public sealed class AlignHostTests(ITestOutputHelper log)
{
    private const int W = 64;
    private const int H = 48;
    private const string Run = "ar00c0ffee";

    /// <summary>A grid that squeezes the picture into the middle half: output (s, t) samples uv 2s − 0.5 (outside 0–1: black).</summary>
    private static (FloatMap Map, byte[] Bytes) SqueezeGrid()
    {
        const int c = 9, r = 7;
        var values = new float[c * r * 3];
        for (var k = 0; k < r; k++)
        {
            for (var i = 0; i < c; i++)
            {
                var n = (k * c + i) * 3;
                values[n] = 2f * i / (c - 1) - 0.5f;
                values[n + 1] = 2f * k / (r - 1) - 0.5f;
                values[n + 2] = 1;
            }
        }
        var map = new FloatMap(c, r, 3, values);
        return (map, Pfm.Write(map));
    }

    private static (FloatMap Map, byte[] Bytes) HalfBlend()
    {
        var values = Enumerable.Range(0, (W / 4) * (H / 4)).SelectMany(_ => new[] { 0.5f, 0f, 1f }).ToArray();
        var map = new FloatMap(W / 4, H / 4, 3, values);
        return (map, Pfm.Write(map));
    }

    private static Project Measured(string warpHash, string blendHash)
    {
        var o = HostRig.Screen("op_1", SourceRef.Solid("#FFFFFF"), W, H) with
        {
            Measured = new MeasuredCorrection(new MeasuredWarp(9, 7, $"align/{Run}/warp-op_1.pfm", warpHash, 0.1), null,
                new MeasuredBlend(W / 4, H / 4, $"align/{Run}/blend-op_1.pfm", blendHash), new MeasuredColor(1, 1, 1, 2.2, 1), 0.1, "", "", Run, 1),
        };
        return HostRig.WithOutputs(o);
    }

    private static (EngineCore Engine, RenderHost Host) Start(Project project, string folder)
    {
        var host = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = false }, out var why) ?? throw new InvalidOperationException("no render host: " + why);
        var services = EngineServices.ForTests(new Core.Time.ManualClock(5_000_000_000));
        services = services with { Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] }, Outputs = host, ProjectFolder = folder };
        var engine = new EngineCore(services, project);
        host.Start(engine);
        return (engine, host);
    }

    private static async Task<byte[]> Read(RenderHost host)
    {
        await host.FramesAsync(3).WaitAsync(HostRig.Patience);
        return await host.ReadOutputAsync("op_1").WaitAsync(HostRig.Patience) ?? throw new InvalidOperationException("op_1 drew nothing");
    }

    [Fact]
    public async Task TheAirShowsTheMeasuredWarpAndBlendAndAMissingFileIsSaid()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-align-host-");
        var empty = Directory.CreateTempSubdirectory("playmap-align-host-");
        try
        {
            var grid = SqueezeGrid();
            var blend = HalfBlend();
            Directory.CreateDirectory(Path.Combine(folder.FullName, "align", Run));
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "align", Run, "warp-op_1.pfm"), grid.Bytes);
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "align", Run, "blend-op_1.pfm"), blend.Bytes);
            var project = Measured(Pfm.Hash(grid.Bytes), Pfm.Hash(blend.Bytes));

            var (engine, host) = Start(project, folder.FullName);
            await using (engine)
            await using (host)
            {
                await engine.AlignFiles.Settled;
                var half = (int)Math.Round(Math.Pow(0.5, 1 / 2.2) * 255);
                byte[] px;
                var waited = System.Diagnostics.Stopwatch.StartNew();
                do
                    px = await Read(host);
                while (HostRig.At(px, W, 2, 2) != (0, 0, 0) && waited.Elapsed < HostRig.Patience);
                var centre = HostRig.At(px, W, W / 2, H / 2);
                log.WriteLine($"centre {centre}, corner {HostRig.At(px, W, 2, 2)}, want {half}");
                Assert.Equal((0, 0, 0), HostRig.At(px, W, 2, 2));
                Assert.Equal((0, 0, 0), HostRig.At(px, W, W - 3, H - 3));
                Assert.True(Math.Abs(centre.R - half) <= 1 && Math.Abs(centre.G - half) <= 1, $"centre {centre}, want {half} ± 1");
                Assert.Equal("ok", JsonOf(engine).GetProperty("outputs")[0].GetProperty("measured").GetProperty("files").GetString());
            }

            // The same project on a node whose folder lacks the files: the output without its correction, and STATE says which file.
            var (bare, bareHost) = Start(project, empty.FullName);
            await using (bare)
            await using (bareHost)
            {
                await bare.AlignFiles.Settled;
                var px = await Read(bareHost);
                Assert.Equal((255, 255, 255), HostRig.At(px, W, 2, 2));
                Assert.Equal((255, 255, 255), HostRig.At(px, W, W / 2, H / 2));
                var files = JsonOf(bare).GetProperty("outputs")[0].GetProperty("measured").GetProperty("files").GetString()!;
                Assert.Contains($"align/{Run}/warp-op_1.pfm missing", files, StringComparison.Ordinal);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
            empty.Delete(recursive: true);
        }
    }

    private static System.Text.Json.JsonElement JsonOf(EngineCore engine) =>
        System.Text.Json.JsonDocument.Parse(engine.State().Json).RootElement.Clone();
}
