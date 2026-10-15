using System.Diagnostics;
using PlayMap.Core.Geometry;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Tests.Rendering;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>The projector VIEW is what the projector puts on the wall: the stage as it lights it, through the lens lookup of the output
/// that drives it — equal to that output's picture before its trim within one code — and black where it lights nothing (the editor's
/// grey never shows there).</summary>
[Collection("render host")]
public sealed class ProjectorViewTests(ITestOutputHelper log)
{
    private const int W = 640;
    private const int H = 360;

    private static Project WallWithALensedProjector()
    {
        var wall = new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty.With("width", 4.0).With("height", 2.0), 0, 0);
        var surface = new Surface("sf_wall", "Wall", "ms_wall", Transform3.Identity, [],
            SurfaceContent.Empty with { Source = new SourceRef(SourceKind.Color, "#C08040") }, 1, [], true, false, "#ffffff", []);
        // Far enough back that the wall fills only the middle of its picture; a strong barrel lens.
        var pj = new Projector("pj_1", "Projector 1", Intrinsics.FromThrowRatio(W, H, 1.0), new LensDistortion(0.12, 0, 0, 0, 0),
            Pose.LookAt(new Vec3d(0.2, 0.1, 7), Vec3d.Zero, Vec3d.UnitY), 0, "#ffffff", false, [], SolveLocks.None, 0);
        var o = new Output("op_1", "Output 1", OutputKind.Virtual, "", W, H, 60, OutputMode.Projector, "pj_1", SourceRef.None, new RectD(0, 0, W, H),
            OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [], new RectD(0, 0, W, H), 0, true);
        return Project.CreateDefault("Projector view") with { Outputs = [o], Stage = Stage.Empty with { Surfaces = [surface], Meshes = [wall], Projectors = [pj] } };
    }

    [Fact]
    public async Task TheProjectorViewIsTheOutputsPictureBeforeItsTrimAndBlackAround()
    {
        await using var rig = HostRig.Start(WallWithALensedProjector());
        var output = await rig.Read("op_1");
        await rig.Ok($"VIEW pj-pj_1 SHOW projector:pj_1 {W}x{H}");
        var (info, view) = await Frame(rig.Engine.Previews.RingPath("pj-pj_1"));
        Assert.Equal((W, H), (info.Width, info.Height));

        var worst = 0;
        var off = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var a = HostRig.At(output, W, x, y);
                var b = HostRig.At(view, W, x, y);
                var d = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                worst = Math.Max(worst, d);
                if (d > 1)
                    off++;
            }
        log.WriteLine($"view against the output: worst {worst} codes, {off} pixels more than one code off");
        Assert.Equal(0, off);

        // Black around: the corners light no wall; the middle shows the wall's colour.
        Assert.Equal((0, 0, 0), HostRig.At(view, W, 2, 2));
        Assert.Equal((0, 0, 0), HostRig.At(view, W, W - 3, H - 3));
        var mid = HostRig.At(view, W, W / 2, H / 2);
        Assert.True(mid.R > 100 && mid.G > 40, $"middle {mid}");

        // The lens is in it: the wall's edge in the view sits where the lens puts it, not where a plain pinhole would.
        var plain = WallWithALensedProjector();
        plain = plain.WithStage(st => st with { Projectors = st.Projectors.Update("pj_1", pj => pj with { Lens = LensDistortion.None }) });
        var r = await rig.Engine.EditAsync(WallRig.Desk, "no lens", _ => plain);
        Assert.True(r.Changed);
        // The view follows the project: new pictures until one shows the pinhole's edge (bounded by the rig's patience).
        var seq = info.Sequence;
        var differ = 0;
        var waited = Stopwatch.StartNew();
        while (differ <= 4 && waited.Elapsed < HostRig.Patience)
        {
            var (next, pinhole) = await Frame(rig.Engine.Previews.RingPath("pj-pj_1"), seq);
            seq = next.Sequence;
            differ = 0;
            for (var x = 0; x < W; x++)
                if (HostRig.At(pinhole, W, x, H / 2) != HostRig.At(view, W, x, H / 2))
                    differ++;
        }
        Assert.True(differ > 4, $"{differ} pixels of the middle row moved with the lens");
    }

    private static async Task<(PreviewFrameInfo Info, byte[] Pixels)> Frame(string path, long after = 0)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var reader = new PreviewRingReader(path);
                    var buffer = new byte[reader.MaxWidth * reader.MaxHeight * 4];
                    if (reader.TryRead(buffer, after, out var info))
                        return (info, buffer);
                }
                catch (InvalidDataException)
                {
                    // Being made: the next look finds it.
                }
            }
            Assert.True(waited.Elapsed < HostRig.Patience, $"no picture in {path}");
            await Task.Delay(30);
        }
    }
}
