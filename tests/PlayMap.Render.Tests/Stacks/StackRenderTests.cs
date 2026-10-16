using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Stacks;

/// <summary>A stack member's measured lattice on the air (llvmpipe): drawn as the first layer of the output's warp mesh, with no shader
/// of its own, it moves what the output shows by the lattice's offset — a uniform offset by that many pixels, a zoom-like one by what
/// the lattice says at each point — each read back within 0.5 px; under an operator's corner pin the measured layer goes first.</summary>
[Collection("gl")]
public class StackRenderTests(GlFixture gl, ITestOutputHelper log)
{
    private const int W = 128;
    private const int H = 72;

    private static readonly SourceRef Media = SourceRef.Media("md1");

    /// <summary>Soft round markers (σ 2.5 px) at the given pixel centres: their light-weighted centroid moves with sub-pixel shifts.</summary>
    private static byte[] Blobs(params Vec2d[] centres)
    {
        var rgba = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var v = 0.0;
                foreach (var c in centres)
                {
                    var dx = x + 0.5 - c.X;
                    var dy = y + 0.5 - c.Y;
                    v = Math.Max(v, Math.Exp(-(dx * dx + dy * dy) / (2 * 2.5 * 2.5)));
                }
                var b = (byte)Math.Round(255 * v);
                var i = (y * W + x) * 4;
                rgba[i] = b;
                rgba[i + 1] = b;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        return rgba;
    }

    private static FuncSources Still(byte[] rgba) =>
        new((_, _) => new ArrayLease(PixelLayout.Rgba8, W, H, 1, ColorMatrix.Bt709, ColorRange.Full, ((byte[])rgba.Clone(), W * 4, W, H)));

    private byte[] Render(byte[] picture, WarpMeshGeometry? warp)
    {
        var output = Scene.SourceOutput("op1", Media, W, H);
        var stage = new TestStage();
        if (warp is not null)
            stage.Warps["op1"] = warp;
        return gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.Render(Scene.Inputs(Scene.Project([], [output]), 1, Still(picture), stage));
            var px = new byte[W * H * 4];
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Empty(c.Device.TakeErrors());
            return px;
        });
    }

    /// <summary>The light-weighted centroid of the read-back within a window around a point (pixel centres at +0.5).</summary>
    private static Vec2d Centroid(byte[] px, Vec2d around, int radius = 9)
    {
        double sx = 0, sy = 0, s = 0;
        for (var y = Math.Max(0, (int)around.Y - radius); y <= Math.Min(H - 1, (int)around.Y + radius); y++)
            for (var x = Math.Max(0, (int)around.X - radius); x <= Math.Min(W - 1, (int)around.X + radius); x++)
            {
                double v = px[(y * W + x) * 4 + 1];
                if (v < 8)
                    continue;
                sx += v * (x + 0.5);
                sy += v * (y + 0.5);
                s += v;
            }
        return new Vec2d(sx / s, sy / s);
    }

    private static WarpLattice Lattice(int cols, int rows, Func<double, double, Vec2d> offsetPx)
    {
        var rest = WarpLattice.AtRest(cols, rows);
        var offsets = new Vec2d[cols * rows];
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < cols; i++)
            {
                var o = offsetPx(i / (double)(cols - 1), j / (double)(rows - 1));
                offsets[j * cols + i] = new Vec2d(o.X / W, o.Y / H);
            }
        return rest with { Offsets = [.. offsets] };
    }

    [Fact]
    public void AUniformMeasuredLatticeMovesThePictureByItsOffset()
    {
        var at = new Vec2d(52.3, 30.7);
        var picture = Blobs(at);
        var plain = Centroid(Render(picture, null), at);
        var lattice = Lattice(9, 6, (_, _) => new Vec2d(2.3, -1.4));
        var moved = Centroid(Render(picture, WarpMeshBuilder.Build(OutputWarp.None, measured: lattice)), at + new Vec2d(2.3, -1.4));
        var shift = moved - plain;
        log.WriteLine($"plain {plain}, moved {moved}: shift {shift}");
        Assert.True((shift - new Vec2d(2.3, -1.4)).Length <= 0.5, $"shift {shift}");
    }

    [Fact]
    public void AZoomLikeMeasuredLatticeMovesEachPointByWhatTheLatticeSaysThere()
    {
        // A 1.5 % zoom about the middle: every point moves by 1.5 % of its distance from the centre.
        var lattice = Lattice(17, 10, (u, v) => new Vec2d((u - 0.5) * W * 0.015, (v - 0.5) * H * 0.015));
        var surface = new LatticeSurface(lattice);
        Vec2d[] points = [new(20.4, 14.6), new(108.2, 16.3), new(24.8, 58.1), new(104.5, 55.4), new(64.2, 36.3)];
        var picture = Blobs(points);
        var plainPx = Render(picture, null);
        var movedPx = Render(picture, WarpMeshBuilder.Build(OutputWarp.None, measured: lattice));
        foreach (var p in points)
        {
            var plain = Centroid(plainPx, p);
            var target = surface.Evaluate(p.X / W, p.Y / H);
            var expected = new Vec2d(target.X * W, target.Y * H) - p;
            var moved = Centroid(movedPx, p + expected);
            log.WriteLine($"{p}: moved by {moved - plain}, the lattice says {expected}");
            Assert.True((moved - plain - expected).Length <= 0.5, $"{p}: {moved - plain} vs {expected}");
        }
    }

    /// <summary>The measured lattice is drawn in the warp, before the output's finished picture: what a send or an LED map taps
    /// (<see cref="RenderEngine.OutputPicture"/>) and what a delayed display presents from its delay ring both carry it.</summary>
    [Fact]
    public void WhatASendTapsAndWhatADelayedDisplayPresentsCarryTheMeasuredLattice()
    {
        var at = new Vec2d(52.3, 30.7);
        var picture = Blobs(at);
        var plain = Centroid(Render(picture, null), at);
        var stage = new TestStage();
        stage.Warps["op1"] = WarpMeshBuilder.Build(OutputWarp.None, measured: Lattice(9, 6, (_, _) => new Vec2d(2.3, -1.4)));
        var output = Scene.SourceOutput("op1", Media, W, H);
        var (tapped, presented) = gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.SetDelay("op1", 2, 0);
            var sources = Still(picture);
            for (var frame = 1; frame <= 4; frame++)
                engine.Render(Scene.Inputs(Scene.Project([], [output]), frame, sources, stage));
            var finished = new byte[W * H * 4];
            var shown = new byte[W * H * 4];
            Assert.True(engine.ReadOutput("op1", finished));
            Assert.True(engine.ReadPresented("op1", shown));
            Assert.Empty(c.Device.TakeErrors());
            return (finished, shown);
        });
        foreach (var (name, px) in new[] { ("tapped", tapped), ("presented", presented) })
        {
            var shift = Centroid(px, at + new Vec2d(2.3, -1.4)) - plain;
            log.WriteLine($"{name}: shift {shift}");
            Assert.True((shift - new Vec2d(2.3, -1.4)).Length <= 0.5, $"{name}: shift {shift}");
        }
    }

    [Fact]
    public void UnderACornerPinTheMeasuredLayerGoesFirst()
    {
        var lattice = Lattice(5, 4, (_, _) => new Vec2d(3, 0));
        var pin = new OutputWarp(true, CornerPin.None with { TopLeft = new Vec2d(0.1, 0.1), BottomLeft = new Vec2d(0.1, -0.1) }, null);
        var field = new WarpField(pin, null, lattice);
        var at = new Vec2d(70.2, 40.1);
        var picture = Blobs(at);
        var landed = field.Evaluate(at.X / W, at.Y / H);
        var expected = new Vec2d(landed.X * W, landed.Y * H);
        var moved = Centroid(Render(picture, WarpMeshBuilder.Build(pin, measured: lattice)), expected);
        log.WriteLine($"expected {expected}, read back {moved}");
        Assert.True((moved - expected).Length <= 0.5, $"{moved} vs {expected}");
        // The layers' order: the measured offset first, then the pin — not the pin, then the offset.
        var pinFirst = WarpMeshBuilder.Evaluate(pin, at.X / W, at.Y / H);
        Assert.True((new Vec2d(pinFirst.X * W + 3, pinFirst.Y * H) - expected).Length > 0.05);
    }
}
