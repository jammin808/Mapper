using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;
using PlayMap.Render.Gl;
using SkiaSharp;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Content;

/// <summary>The raymarched fractals: where the GPU's rays meet the surface is where the CPU twin's do (16 probes per kind); each kind
/// draws its committed 128×72 golden (tests/fixtures/content, written with PLAYMAP_WRITE_FIXTURES=1) with depth in it; the render
/// scale draws a smaller picture filtered up.</summary>
[Collection("gl")]
public class FractalTests(GlFixture gl, ITestOutputHelper log)
{
    private const int W = 128;
    private const int H = 72;

    public static TheoryData<string> Kinds => new() { "fractal.mandelbulb", "fractal.mandelbox", "fractal.kifs" };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheGpusDepthIsTheCpuTwinsAtSixteenProbes(string kind)
    {
        var g = Scene.Gen(kind, ParamSet.Empty.With("renderScale", 1.0).With("steps", 160), "gn_probe");
        var (gpu, cpu) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            ContentGl.Draw(host, g, 3, 1, W, H);
            var r = (RaymarchGenerator)host.RendererOf(g.Id, W, H)!;
            var camera = r.Camera(3, EffectSurge.Zero);
            using var target = c.Device.CreateRenderTarget(W, H, TextureFormat.Rgba32F, filter: TextureFilter.Nearest);
            var p = c.Device.Program("content.raymarch");
            r.Apply(p, camera, W, H, mode: 1);
            c.Device.BindTarget(target);
            c.Device.SetBlend(BlendMode.Opaque);
            c.Device.DrawFullscreen(p);
            var depth = new float[W * H * 4];
            c.Device.ReadPixelsFloat(target, depth);
            var gpuProbes = new List<float>();
            var cpuProbes = new List<float>();
            var cone = Fractal3D.Cone(camera.TanHalfFov, H, 1f);
            foreach (var (x, y) in Probes())
            {
                gpuProbes.Add(depth[(y * W + x) * 4]);
                cpuProbes.Add(Fractal3D.March(Fractal3DSettings.KindOf(kind)!.Value, r.De(), camera.Eye, camera.Ray(x + 0.5f, y + 0.5f, W, H), 160, cone));
            }
            return (gpuProbes, cpuProbes);
        });
        var hits = 0;
        for (var i = 0; i < gpu.Count; i++)
        {
            log.WriteLine($"{kind} probe {i}: gpu {gpu[i]:0.#####} cpu {cpu[i]:0.#####}");
            Assert.Equal(cpu[i] < 0, gpu[i] < 0);
            if (cpu[i] < 0)
                continue;
            hits++;
            Assert.True(Math.Abs(gpu[i] - cpu[i]) <= 0.01 * cpu[i] + 1e-3, $"{kind} probe {i}: GPU {gpu[i]} against the twin's {cpu[i]}");
        }
        Assert.True(hits >= 8, $"{kind}: only {hits} of 16 probes meet the surface");
    }

    /// <summary>A 4×4 grid over the picture's middle.</summary>
    private static IEnumerable<(int X, int Y)> Probes()
    {
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 4; i++)
                yield return (W / 2 - 18 + i * 12, H / 2 - 12 + j * 8);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachKindDrawsItsGoldenWithDepthInIt(string kind)
    {
        var g = Scene.Gen(kind, ParamSet.Empty.With("renderScale", 1.0), "gn_golden");
        var bgra = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            return Scene.ReadTexture(c.Device, ContentGl.Draw(host, g, 2, 1, W, H));
        });
        var lit = 0;
        var lumas = new List<double>();
        for (var i = 0; i < W * H; i++)
        {
            var l = (bgra[i * 4] + bgra[i * 4 + 1] + bgra[i * 4 + 2]) / 3.0;
            lumas.Add(l);
            if (l > 8)
                lit++;
        }
        Assert.True(lit > W * H / 10, $"{kind}: only {lit} pixels lit");
        var mean = lumas.Average();
        var variance = lumas.Average(l => (l - mean) * (l - mean));
        Assert.True(variance > 20, $"{kind}: a flat picture (variance {variance:0.#}), not a shaded surface");

        var path = Path.Combine(GoldenFolder(), kind + ".png");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            WritePng(path, bgra);
        Assert.True(File.Exists(path), $"{path} is written by this test: run it once with PLAYMAP_WRITE_FIXTURES=1");
        var golden = ReadPng(path);
        var off = 0;
        var total = 0L;
        for (var i = 0; i < bgra.Length; i++)
        {
            if (i % 4 == 3)
                continue;
            var d = Math.Abs(bgra[i] - golden[i]);
            total += d;
            if (d > 8)
                off++;
        }
        var meanDiff = total / (W * H * 3.0);
        log.WriteLine($"{kind}: {off} channel values off by more than 8, mean difference {meanDiff:0.###}");
        Assert.True(off <= W * H * 3 / 50 && meanDiff <= 2, $"{kind} differs from its golden: {off} values off by more than 8, mean {meanDiff:0.##}");
    }

    [Fact]
    public void TheRenderScaleDrawsASmallerPictureFilteredUp()
    {
        Assert.Equal((64, 36), RaymarchGenerator.InnerSize(128, 72, 0.5));
        Assert.Equal((32, 18), RaymarchGenerator.InnerSize(128, 72, 0.1));
        Assert.Equal((128, 72), RaymarchGenerator.InnerSize(128, 72, 3));
        var (full, half) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var a = Scene.ReadTexture(c.Device, ContentGl.Draw(host, Scene.Gen("fractal.mandelbulb", ParamSet.Empty.With("renderScale", 1.0), "gn_a"), 2, 1, W, H));
            var b = Scene.ReadTexture(c.Device, ContentGl.Draw(host, Scene.Gen("fractal.mandelbulb", ParamSet.Empty.With("renderScale", 0.5), "gn_b"), 2, 2, W, H));
            return (a, b);
        });
        Assert.Equal(full.Length, half.Length);
        Assert.False(full.AsSpan().SequenceEqual(half));
    }

    private static string GoldenFolder() => Path.Combine(RepoRoot(), "tests", "fixtures", "content");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above the test folder");
    }

    private static void WritePng(string path, byte[] bgra)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        PlayMap.Core.Storage.DurableFile.WriteAllBytes(path, data.ToArray());
    }

    private static byte[] ReadPng(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        using var bitmap = decoded.Copy(SKColorType.Bgra8888);
        var bytes = new byte[W * H * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }
}
