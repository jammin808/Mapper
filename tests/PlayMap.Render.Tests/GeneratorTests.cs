using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;
using PlayMap.Render.Generators.Skia;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests;

/// <summary>The generators' known pixels at 320×180: the bars' code values, the grid's lines, the flat
/// field's level, the test card's staircase, the mapping patterns, the moving patterns at known leader
/// times, the flash guard on a strobe, the fault card — and the fractal and reactive shaders against their
/// CPU twins.</summary>
[Collection("gl")]
public class GeneratorTests(GlFixture gl)
{
    private const int W = 320;
    private const int H = 180;

    private static (int R, int G, int B) Grey(int v) => (v, v, v);

    [Fact]
    public void SmpteBarsCarryTheirExactCodeValuesInFullRange()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.bars"), W, H, GeneratorFrame.At(0)));
        (int, int, int)[] expected = [(191, 191, 191), (191, 191, 0), (0, 191, 191), (0, 191, 0), (191, 0, 191), (191, 0, 0), (0, 0, 191)];
        var barW = (W - 2 * 40) / 7.0;
        for (var i = 0; i < 7; i++)
            Assert.Equal(expected[i], Pixels.At(px, W, (int)(40 + (i + 0.5) * barW), 50));
        Assert.Equal(Grey(BarsPattern.StretchByte(BarsPattern.G40)), Pixels.At(px, W, 20, 50));
        Assert.Equal(Grey(255), Pixels.At(px, W, 120, 165));
        Assert.Equal(Grey(BarsPattern.StretchByte(BarsPattern.G15)), Pixels.At(px, W, 10, 165));
    }

    [Fact]
    public void NarrowRangeBarsStayInSixteenToTwoThirtyFive()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.bars", ParamSet.Empty.With("fullRange", false)), W, H, GeneratorFrame.At(0)));
        Assert.Equal(Grey(180), Pixels.At(px, W, 57, 50));
        Assert.Equal((16, 180, 180), Pixels.At(px, W, (int)(40 + 2.5 * (240 / 7.0)), 50));
        Assert.Equal(Grey(104), Pixels.At(px, W, 20, 50));
    }

    [Fact]
    public void EbuBarsAreEightFullHeightBarsAtFullAmplitude()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.bars", ParamSet.Empty.With("variant", "ebu100")), W, H, GeneratorFrame.At(0)));
        (int, int, int)[] expected = [(255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0), (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)];
        for (var i = 0; i < 8; i++)
            Assert.Equal(expected[i], Pixels.At(px, W, i * 40 + 20, 90));
    }

    [Fact]
    public void GridLinesArePixelExactAndSymmetricAboutTheCentre()
    {
        var ps = ParamSet.Empty.With("subdivisions", 0).With("centerCross", false).With("border", false);
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.grid", ps), W, H, GeneratorFrame.At(0)));
        foreach (var x in new[] { 32, 96, 160, 224, 288 })
        {
            Assert.Equal(Grey(255), Pixels.At(px, W, x, 50));
            Assert.Equal(Grey(0), Pixels.At(px, W, x + 1, 50));
            Assert.Equal(Grey(0), Pixels.At(px, W, x - 1, 50));
        }
        foreach (var y in new[] { 26, 90, 154 })
        {
            Assert.Equal(Grey(255), Pixels.At(px, W, 100, y));
            Assert.Equal(Grey(0), Pixels.At(px, W, 100, y + 1));
        }
    }

    [Fact]
    public void AFlatFieldIsItsLevelEverywhere()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.flat", ParamSet.Empty.With("level", 40.0)), W, H, GeneratorFrame.At(0)));
        for (var y = 0; y < H; y += 17)
            for (var x = 0; x < W; x += 23)
                Assert.Equal(Grey(102), Pixels.At(px, W, x, y));
    }

    [Fact]
    public void TheRigCardsStaircaseReadsItsElevenFixedSteps()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.testcard"), W, H, GeneratorFrame.At(0)));
        var (x, y, w, h) = TestCardPattern.RigStaircase(W, H);
        for (var i = 0; i < 11; i++)
        {
            var x0 = x + (int)Math.Round(w * (double)i / 11);
            var x1 = x + (int)Math.Round(w * (double)(i + 1) / 11);
            Assert.Equal(Grey(TestCardPattern.Steps[i]), Pixels.At(px, W, (x0 + x1) / 2, y + h / 2));
        }
    }

    [Fact]
    public void TheLevelsCardsStaircaseReadsItsElevenFixedSteps()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.testcard", ParamSet.Empty.With("variant", "levels")), W, H, GeneratorFrame.At(0)));
        var (x, y, w, h) = TestCardPattern.LevelsStaircase(W, H);
        for (var i = 0; i < 11; i++)
        {
            var x0 = x + (int)Math.Round(w * (double)i / 11);
            var x1 = x + (int)Math.Round(w * (double)(i + 1) / 11);
            Assert.Equal(Grey(TestCardPattern.Steps[i]), Pixels.At(px, W, (x0 + x1) / 2, y + h / 2));
        }
    }

    [Fact]
    public void IdentifyShowsTheOutputsNumberInItsColour()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.identify"), W, H, GeneratorFrame.At(0), new GeneratorContext("Left wall", 3, "#FF8800", true)));
        var coloured = 0;
        for (var i = 0; i < W * H; i++)
        {
            var (r, g, b) = Pixels.At(px, W, i % W, i / W);
            if (r > 200 && g is > 100 and < 170 && b < 40)
                coloured++;
        }
        Assert.True(coloured > W * H / 50, $"only {coloured} pixels in the output's colour");
    }

    [Fact]
    public void ConvergenceDrawsEachChannelOnTheSameLines()
    {
        var all = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.convergence"), W, H, GeneratorFrame.At(0)));
        var red = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.convergence", ParamSet.Empty.With("channels", "red")), W, H, GeneratorFrame.At(0)));
        Assert.Equal((255, 255, 255), Pixels.At(all, W, 64, 30));
        Assert.Equal((255, 255, 255), Pixels.At(all, W, 130, 90));
        Assert.Equal((255, 0, 0), Pixels.At(red, W, 160, 30));
        Assert.Equal((0, 0, 0), Pixels.At(red, W, 100, 30));
    }

    [Fact]
    public void TheUvCheckersFirstCellIsTopLeftInItsColumnsHue()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.uvchecker"), W, H, GeneratorFrame.At(0)));
        var (r, g, b) = UvCheckerPattern.CellColor(0, 0, 8);
        Assert.Equal((r, g, b), Pixels.At(px, W, 4, 4));
        var (r2, g2, b2) = UvCheckerPattern.CellColor(7, 3, 8);
        Assert.Equal((r2, g2, b2), Pixels.At(px, W, W - 4, H - 4));
        Assert.Equal("A1", UvCheckerPattern.Label(0, 0));
        Assert.Equal("AB12", UvCheckerPattern.Label(27, 11));
    }

    [Fact]
    public void SteppedRampsCarryTheirWedgeValues()
    {
        var ps = ParamSet.Empty.With("variant", "steps").With("steps", 16).With("markers", false);
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.ramp", ps), W, H, GeneratorFrame.At(0)));
        for (var i = 0; i < 16; i++)
            Assert.Equal(Grey(17 * i), Pixels.At(px, W, i * 20 + 10, 90));
    }

    [Fact]
    public void ACheckerSwapsOnItsIntervalWhenAnimated()
    {
        var ps = ParamSet.Empty.With("animate", true).With("interval", 0.5);
        var still = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.checker", ps), W, H, GeneratorFrame.At(0.2)));
        var swapped = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.checker", ps), W, H, GeneratorFrame.At(0.6)));
        Assert.Equal(Grey(255), Pixels.At(still, W, 10, 10));
        Assert.Equal(Grey(0), Pixels.At(still, W, 70, 10));
        Assert.Equal(Grey(0), Pixels.At(swapped, W, 10, 10));
    }

    [Fact]
    public void TheMovingBarIsWhereItsSpeedPutsItAtTheLeaderTime()
    {
        var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.motion"), W, H, GeneratorFrame.At(0.1)));
        Assert.Equal(Grey(255), Pixels.At(px, W, 48, 100));
        Assert.Equal(Grey(255), Pixels.At(px, W, 87, 100));
        Assert.Equal(Grey(0), Pixels.At(px, W, 47, 100));
        Assert.Equal(Grey(0), Pixels.At(px, W, 88, 100));
    }

    [Fact]
    public void SyncMarksFlashOnTheLeadersGridThroughTheGuard()
    {
        var flash = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.syncmarks"), W, H, GeneratorFrame.At(4.01)));
        var dark = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.syncmarks"), W, H, GeneratorFrame.At(5.0)));
        var unguarded = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.syncmarks"), W, H, GeneratorFrame.At(4.01, flashGuard: false)));
        Pixels.Near(Grey(179), Pixels.At(flash, W, 160, 90), 1, "guarded flash");
        Assert.Equal(Grey(0), Pixels.At(dark, W, 160, 90));
        Assert.Equal(Grey(255), Pixels.At(unguarded, W, 160, 90));
    }

    [Fact]
    public void AColourCycleStepsThroughItsColoursOnTheLeaderClock()
    {
        var ps = ParamSet.Empty.With("fade", false).With("interval", 2.0);
        (double T, (int, int, int) Rgb)[] cases = [(0.5, (255, 0, 0)), (2.5, (0, 255, 0)), (4.5, (0, 0, 255)), (6.5, (255, 255, 255)), (8.5, (255, 0, 0))];
        foreach (var (t, rgb) in cases)
        {
            var px = gl.Run(c => Scene.RenderGenerator(c.Device, Scene.Gen("pattern.colorcycle", ps), W, H, GeneratorFrame.At(t)));
            Assert.Equal(rgb, Pixels.At(px, W, 100, 100));
        }
    }

    [Fact]
    public void TheFlashGuardHoldsAFrameFlashToThreeFlashesASecond()
    {
        var rises = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var g = Scene.Gen("pattern.motion", ParamSet.Empty.With("variant", "flash"));
            var count = 0;
            var wasBright = false;
            for (var f = 0; f < 60; f++)
            {
                host.BeginFrame(f + 1);
                var tex = host.Render(g, 64, 36, GeneratorContext.None, GeneratorFrame.At(10 + f / 60.0));
                var px = Scene.ReadTexture(c.Device, tex);
                var bright = Pixels.At(px, 64, 32, 10).R > 100;
                if (bright && !wasBright)
                    count++;
                wasBright = bright;
            }
            return count;
        });
        Assert.InRange(rises, 1, FlashGuard.MaxFlashesPerSecond);
    }

    [Fact]
    public void ParticlesDrawTheSameFieldInTwoHostsAtTheSameTime()
    {
        var g = Scene.Gen("particles.field", ParamSet.Empty.With("preset", "Confetti").With("count", 400));
        var a = gl.Run(c => Scene.RenderGenerator(c.Device, g, W, H, GeneratorFrame.At(21.5)));
        var b = gl.Run(c => Scene.RenderGenerator(c.Device, g, W, H, GeneratorFrame.At(21.5)));
        Assert.Equal(a, b);
        var lit = 0;
        for (var i = 0; i < W * H; i++)
            if (a[i * 4] + a[i * 4 + 1] + a[i * 4 + 2] > 60)
                lit++;
        Assert.True(lit > 200, $"only {lit} lit pixels");
    }

    [Fact]
    public void AGeneratorThatFaultsShowsItsCardUntilItsSettingsChange()
    {
        gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var bad = new Generator("gn_x", "Broken", "pattern.nonsense", ParamSet.Empty);
            host.BeginFrame(1);
            var card = host.Render(bad, 160, 90, GeneratorContext.None, GeneratorFrame.At(0));
            Assert.Single(host.Faults());
            var px = Scene.ReadTexture(c.Device, card);
            Assert.True(Pixels.At(px, 160, 80, 0).R > 150, "the card's red frame");

            host.BeginFrame(2);
            host.Render(bad with { Name = "Still broken" }, 160, 90, GeneratorContext.None, GeneratorFrame.At(0));
            Assert.Single(host.Faults());

            host.BeginFrame(3);
            var fixedTex = host.Render(bad with { Kind = "pattern.flat" }, 160, 90, GeneratorContext.None, GeneratorFrame.At(0));
            Assert.Empty(host.Faults());
            Assert.Equal((255, 255, 255), Pixels.At(Scene.ReadTexture(c.Device, fixedTex), 160, 80, 45));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void EachFractalShaderAgreesWithItsCpuTwin()
    {
        const int w = 96, h = 54;
        var palette = new[] { ColorRgb.FromBytes(0x10, 0x20, 0x80), ColorRgb.FromBytes(0xF0, 0x40, 0x20), ColorRgb.FromBytes(0xFF, 0xFF, 0xFF) };
        foreach (var kind in Enum.GetValues<FractalKind>())
        {
            var settings = new FractalSettings(kind, 1.1, kind == FractalKind.Mandelbrot ? -0.5 : 0, 0, 48, 0.4, -0.8, 0.156, "#102080,#F04020,#FFFFFF");
            var view = FractalView.Of(settings, 2.0, 64);
            var kindId = kind switch
            {
                FractalKind.Julia => "fractal.julia",
                FractalKind.BurningShip => "fractal.burningship",
                FractalKind.Newton => "fractal.newton",
                FractalKind.DomainWarp => "fractal.warp",
                _ => "fractal.mandelbrot",
            };
            var shaded = gl.Run(c =>
            {
                var d = c.Device;
                using var target = d.CreateRenderTarget(w, h, TextureFormat.Rgba8);
                var p = d.Program(kindId);
                FractalGenerator.Apply(p, view, palette, w, h);
                d.BindTarget(target);
                d.SetBlend(BlendMode.Opaque);
                d.DrawFullscreen(p);
                return Pixels.Read(d, target);
            });
            double sum = 0;
            var far = 0;
            var colours = new HashSet<int>();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var (px, py) = view.ToPlane(x + 0.5, y + 0.5, w, h);
                    var v = FractalMath.Sample(kind, px, py, view.JuliaRe, view.JuliaIm, view.Iterations, view.Time, view.Warp);
                    var cpu = FractalColor.Map(kind, v, palette, view.PaletteOffset, view.Brightness);
                    colours.Add(cpu.Packed);
                    var gpu = Pixels.At(shaded, w, x, y);
                    var dr = Math.Abs(cpu.R8 - gpu.R);
                    var dg = Math.Abs(cpu.G8 - gpu.G);
                    var db = Math.Abs(cpu.B8 - gpu.B);
                    sum += (dr + dg + db) / 3.0;
                    if (Math.Max(dr, Math.Max(dg, db)) > 64)
                        far++;
                }
            }
            var mean = sum / (w * h);
            Assert.True(colours.Count >= 3, $"{kind}: the CPU drew {colours.Count} colours");
            Assert.True(mean <= 10, $"{kind}: the shader and the CPU differ by {mean:0.0}/255 on average");
            Assert.True(far <= w * h * 0.06, $"{kind}: {far} pixels far apart");
        }
    }

    [Fact]
    public void EachReactiveShaderAgreesWithItsCpuTwin()
    {
        const int w = 96, h = 54;
        var settings = ReactiveSettings.Resolve(ParamSet.Empty);
        var palette = settings.Palette();
        foreach (var scene in Enum.GetValues<ReactiveScene>())
        {
            var view = ReactiveView.Of(settings with { Scene = scene }, 3.7);
            var shaded = gl.Run(c =>
            {
                var d = c.Device;
                using var target = d.CreateRenderTarget(w, h, TextureFormat.Rgba8);
                var p = d.Program(ReactiveGenerator.ProgramFor(scene));
                ReactiveGenerator.Apply(p, view, palette, w, h);
                d.BindTarget(target);
                d.SetBlend(BlendMode.Opaque);
                d.DrawFullscreen(p);
                return Pixels.Read(d, target);
            });
            double sum = 0;
            var far = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var v = ReactiveField.Sample(scene, (x + 0.5 - w / 2.0) / h, (y + 0.5 - h / 2.0) / h, view);
                    var cpu = ReactiveField.Map(v, palette, view.Brightness);
                    var gpu = Pixels.At(shaded, w, x, y);
                    var d = (Math.Abs(cpu.R8 - gpu.R) + Math.Abs(cpu.G8 - gpu.G) + Math.Abs(cpu.B8 - gpu.B)) / 3.0;
                    sum += d;
                    if (d > 64)
                        far++;
                }
            }
            Assert.True(sum / (w * h) <= 10, $"{scene}: the shader and the CPU differ by {sum / (w * h):0.0}/255 on average");
            Assert.True(far <= w * h * 0.06, $"{scene}: {far} pixels far apart");
        }
    }
}
