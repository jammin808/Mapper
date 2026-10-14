using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>The quad on the wall as a peer drives it: LIGHT WALL lights a display as a projector with the overlay on; SURFACE QUAD
/// turns four wall pixels (any order, through the output's warp) into a quad of that projector; a corner, a lattice node or the
/// selected handle moves only itself; an armed show refuses every one with the gate's words and changes nothing.</summary>
public sealed class QuadVerbTests
{
    private static async Task<(WallRig Rig, string Output, string Projector)> Lit()
    {
        var rig = new WallRig();
        var reply = WallRig.Json(await rig.Ok("LIGHT WALL #2"));
        return (rig, reply.GetProperty("output").GetString()!, reply.GetProperty("projector").GetString()!);
    }

    [Fact]
    public async Task LightWallMakesTheDisplayAProjectorWithTheOverlayOn()
    {
        var (rig, outputId, projectorId) = await Lit();
        await using var _ = rig;
        var o = rig.Project.FindOutput(outputId)!;
        Assert.Equal(OutputKind.Display, o.Kind);
        Assert.Equal("DISPLAY2", o.DisplayKey);
        Assert.Equal((1920, 1200), (o.Width, o.Height));
        Assert.Equal(OutputMode.Projector, o.Mode);
        Assert.Equal(projectorId, o.ProjectorId);
        Assert.True(o.Enabled);
        var pj = rig.Project.FindProjector(projectorId)!;
        Assert.Equal(1.5, pj.Intrinsics.ThrowRatio, 6);
        var overlay = rig.State().GetProperty("air").GetProperty("overlay");
        Assert.True(overlay.GetProperty("on").GetBoolean());
        Assert.Equal(outputId, overlay.GetProperty("outputs")[0].GetString());
        Assert.Equal("lit", rig.State().GetProperty("onboarding").GetProperty("stage").GetString());
        Assert.True(rig.Engine.Air.IdentifyUntilLeaderNs > 0);
        // Lit already, with the overlay on: nothing to do, and said so.
        Assert.Contains("already lights", await rig.Refused("LIGHT WALL #2"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SurfaceQuadTakesFourWallPixelsInAnyOrder()
    {
        var (rig, _, projectorId) = await Lit();
        await using var __ = rig;
        // Clicked BR, TL, BL, TR on a 1920 × 1200 wall.
        var reply = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 1500 1000 200 150 260 1050 1700 120 PX AS Door"));
        var id = reply.GetProperty("id").GetString()!;
        var s = rig.Project.FindSurface(id)!;
        Assert.Equal("Door", s.Name);
        Assert.Equal("", s.MeshId);
        Assert.True(s.Content.Source.IsNone);
        var q = s.Pixel!;
        Assert.Equal(projectorId, q.ProjectorId);
        Assert.Equal(new Vec2d(200 / 1920.0, 150 / 1200.0), q.TopLeft);
        Assert.Equal(new Vec2d(1700 / 1920.0, 120 / 1200.0), q.TopRight);
        Assert.Equal(new Vec2d(1500 / 1920.0, 1000 / 1200.0), q.BottomRight);
        Assert.Equal(new Vec2d(260 / 1920.0, 1050 / 1200.0), q.BottomLeft);
        Assert.Equal(200, reply.GetProperty("corners").GetProperty("tl")[0].GetDouble(), 6);
        var row = rig.State().GetProperty("stage").GetProperty("projectors").EnumerateArray().Single(p => p.GetProperty("id").GetString() == projectorId);
        Assert.Equal(1, row.GetProperty("quads").GetInt32());
        Assert.Equal("quad", rig.State().GetProperty("onboarding").GetProperty("stage").GetString());
        // Crossed corners or a speck are refused with the reason.
        Assert.Contains("px²", await rig.Refused($"SURFACE QUAD {projectorId} 10 10 12 10 12 12 10 12 PX"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WallPixelsGoThroughTheOutputsWarp()
    {
        var (rig, outputId, projectorId) = await Lit();
        await using var _ = rig;
        await rig.Ok($"SET {outputId} warp {{\"enabled\":true,\"pin\":{{\"topLeft\":{{\"x\":0.05,\"y\":0.02}},\"topRight\":{{\"x\":-0.03,\"y\":0.04}},\"bottomRight\":{{\"x\":0,\"y\":0}},\"bottomLeft\":{{\"x\":0.02,\"y\":-0.05}}}},\"lattice\":null}}");
        var id = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 300 200 1600 220 1580 1000 320 980")).GetProperty("id").GetString()!;
        var q = rig.Project.FindSurface(id)!.Pixel!;
        var field = new WarpField(rig.Project.FindOutput(outputId)!.Warp);
        Assert.False(field.IsIdentity);
        // Each corner, through the warp, lands on the wall pixel that was clicked.
        var wall = field.Evaluate(q.TopLeft.X, q.TopLeft.Y);
        Assert.Equal(300, wall.X * 1920, 6);
        Assert.Equal(200, wall.Y * 1200, 6);
        wall = field.Evaluate(q.BottomRight.X, q.BottomRight.Y);
        Assert.Equal(1580, wall.X * 1920, 6);
        Assert.Equal(1000, wall.Y * 1200, 6);
        // And a quad given in raster pixels is not warped.
        var raw = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 300 200 1600 220 1580 1000 320 980 RASTER")).GetProperty("id").GetString()!;
        Assert.Equal(new Vec2d(300 / 1920.0, 200 / 1200.0), rig.Project.FindSurface(raw)!.Pixel!.TopLeft);
    }

    [Fact]
    public async Task SurfaceCornerByMovesOnlyThatCorner()
    {
        var (rig, _, projectorId) = await Lit();
        await using var _r = rig;
        var id = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 200 150 1700 120 1500 1000 260 1050")).GetProperty("id").GetString()!;
        var before = rig.Project.FindSurface(id)!.Pixel!;
        var reply = WallRig.Json(await rig.Ok($"SURFACE CORNER {id} TR BY 0.1 0 PX"));
        Assert.Equal("TR", reply.GetProperty("corner").GetString());
        var after = rig.Project.FindSurface(id)!.Pixel!;
        Assert.Equal(before.TopRight.X + 0.1 / 1920, after.TopRight.X, 12);
        Assert.Equal(before.TopRight.Y, after.TopRight.Y, 12);
        Assert.Equal(before.TopLeft, after.TopLeft);
        Assert.Equal(before.BottomRight, after.BottomRight);
        Assert.Equal(before.BottomLeft, after.BottomLeft);
        // TO is wall-normalised by default; RASTER is the projector's pixels.
        await rig.Ok($"SURFACE CORNER {id} 3 TO 0.25 0.9");
        Assert.Equal(new Vec2d(0.25, 0.9), rig.Project.FindSurface(id)!.Pixel!.BottomLeft);
        await rig.Ok($"SURFACE CORNER {id} BL TO 400 900 RASTER");
        Assert.Equal(new Vec2d(400 / 1920.0, 900 / 1200.0), rig.Project.FindSurface(id)!.Pixel!.BottomLeft);
        // A move to where it is changes nothing and is refused; a move that crosses the shape is refused with the reason.
        Assert.Contains("moves nothing", await rig.Refused($"SURFACE CORNER {id} BL TO 400 900 RASTER"), StringComparison.Ordinal);
        Assert.Contains("cross", await rig.Refused($"SURFACE CORNER {id} TL TO 1900 1190 RASTER"), StringComparison.Ordinal);
        // A burst of nudges on one corner is one undo step.
        var tl = rig.Project.FindSurface(id)!.Pixel!.TopLeft;
        await rig.Ok($"SURFACE CORNER {id} TL BY 1 0");
        await rig.Ok($"SURFACE CORNER {id} TL BY 1 0");
        await rig.Ok($"SURFACE CORNER {id} TL BY 1 0");
        Assert.Equal(tl.X + 3 / 1920.0, rig.Project.FindSurface(id)!.Pixel!.TopLeft.X, 12);
        await rig.Ok("UNDO");
        Assert.Equal(tl, rig.Project.FindSurface(id)!.Pixel!.TopLeft);
    }

    [Fact]
    public async Task ALockedQuadRefusesItsCorners()
    {
        var (rig, _, projectorId) = await Lit();
        await using var _r = rig;
        var id = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 200 150 1700 120 1500 1000 260 1050")).GetProperty("id").GetString()!;
        await rig.Ok($"SET {id} locked true");
        Assert.Contains("locked", await rig.Refused($"SURFACE CORNER {id} TL BY 1 0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlesAreSelectedSteppedAndNudged()
    {
        var (rig, _, projectorId) = await Lit();
        await using var _r = rig;
        var id = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 200 150 1700 120 1500 1000 260 1050")).GetProperty("id").GetString()!;
        await rig.Ok($"HANDLE SELECT {id} TL");
        Assert.Equal(new OverlayHandle(id, HandleKind.Corner, 0, 0), rig.Engine.Air.Overlay.Handle);
        Assert.Contains("selected already", await rig.Refused($"HANDLE SELECT {id} TL"), StringComparison.Ordinal);
        var before = rig.Project.FindSurface(id)!.Pixel!.TopLeft;
        await rig.Ok("HANDLE NUDGE 2 -1");
        var after = rig.Project.FindSurface(id)!.Pixel!.TopLeft;
        Assert.Equal(before.X + 2 / 1920.0, after.X, 12);
        Assert.Equal(before.Y - 1 / 1200.0, after.Y, 12);
        await rig.Ok("HANDLE NEXT");
        Assert.Equal(1, rig.Engine.Air.Overlay.Handle!.Value.Column);
        await rig.Ok("HANDLE PREV");
        await rig.Ok("HANDLE PREV");
        Assert.Equal(3, rig.Engine.Air.Overlay.Handle!.Value.Column);
        var state = rig.State().GetProperty("air").GetProperty("overlay").GetProperty("handle");
        Assert.Equal(id, state.GetProperty("surface").GetString());
        Assert.Equal(3, state.GetProperty("index").GetInt32());
        await rig.Ok("HANDLE SELECT NONE");
        Assert.Null(rig.Engine.Air.Overlay.Handle);
        Assert.Contains("no handle", await rig.Refused("HANDLE NUDGE 1 0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALatticeKeepsTheShapeAndANodeLandsWhereItIsSent()
    {
        var (rig, _, projectorId) = await Lit();
        await using var _r = rig;
        var id = WallRig.Json(await rig.Ok($"SURFACE QUAD {projectorId} 200 150 1700 120 1500 1000 260 1050")).GetProperty("id").GetString()!;
        var reply = WallRig.Json(await rig.Ok($"SURFACE LATTICE {id} 3x3"));
        Assert.Equal(0, reply.GetProperty("shapeChangePx").GetDouble(), 6);
        Assert.Equal(3, rig.Project.FindSurface(id)!.Pixel!.Lattice!.Columns);
        Assert.Contains("already", await rig.Refused($"SURFACE LATTICE {id} 3x3"), StringComparison.Ordinal);
        await rig.Ok($"SURFACE NODE {id} 1 1 TO 0.52 0.47");
        var shape = PixelQuadShape.Of(rig.Project.FindSurface(id)!.Pixel!)!;
        Assert.Equal(0, (PixelQuadMath.Node(shape, 1, 1) - new Vec2d(0.52, 0.47)).Length, 12);
        await rig.Ok($"SURFACE LATTICE {id} OFF");
        Assert.Null(rig.Project.FindSurface(id)!.Pixel!.Lattice);
    }

    [Fact]
    public async Task AnArmedShowRefusesTheQuadWithTheGatesWordsAndChangesNothing()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(1), new ArmedGate());
        var revision = rig.Engine.Store.Current.Revision;
        var words = await rig.Refused("SURFACE QUAD pj_test1 200 150 1700 120 1500 1000 260 1050");
        Assert.Equal(ArmedGate.Words, words);
        Assert.Equal(ArmedGate.Words, await rig.Refused("LIGHT WALL #2"));
        Assert.Equal(ArmedGate.Words, await rig.Refused("OVERLAY ON"));
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.DoesNotContain(rig.Project.Stage.Surfaces, s => s.Pixel is not null);
        Assert.False(rig.Engine.Air.Overlay.On);
        Assert.True(rig.State().GetProperty("air").GetProperty("overlay").GetProperty("armed").GetBoolean());
    }

    [Fact]
    public async Task TheOverlaysLeaseLapsesWhenItIsNotRenewed()
    {
        var (rig, outputId, _) = await Lit();
        await using var _r = rig;
        Assert.True(rig.Engine.Air.Overlay.On);
        Assert.Contains("on already", await rig.Refused($"OVERLAY ON ON {outputId}"), StringComparison.Ordinal);
        var lease = rig.Engine.Air.Overlay.LeaseUntilMonoNs;
        rig.Clock.AdvanceMs(10_000);
        await rig.Ok("OVERLAY KEEP");
        Assert.True(rig.Engine.Air.Overlay.LeaseUntilMonoNs > lease);
        rig.Clock.AdvanceMs(10_000);
        Assert.True(rig.Engine.Air.Overlay.On);
        rig.Clock.AdvanceMs(6_000);
        await WallRig.Until(() => !rig.Engine.Air.Overlay.On, "the overlay's lease to lapse");
        await rig.Ok("OVERLAY ON LAYERS outlines,handles");
        Assert.Equal(OverlayLayers.Outlines | OverlayLayers.Handles, rig.Engine.Air.Overlay.Layers);
        await rig.Ok("OVERLAY OFF");
        Assert.Contains("off already", await rig.Refused("OVERLAY OFF"), StringComparison.Ordinal);
        Assert.Contains("— not hats", await rig.Refused("OVERLAY ON LAYERS outlines,hats"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CursorsAreEphemeralAndTheirLeaseLapses()
    {
        var (rig, outputId, _) = await Lit();
        await using var _r = rig;
        var revision = rig.Engine.Store.Current.Revision;
        var journal = rig.Engine.Journal.Recent(100).Count;
        var reply = await rig.Run($"CURSOR {outputId} 0.5 0.4 TRAIL 0.1 0.1 0.9 0.12 COLOR #ff8800");
        Assert.True(reply.Ok);
        Assert.True(reply.Silent);
        var cursor = Assert.Single(rig.Engine.WallCursors);
        Assert.Equal((outputId, new Vec2d(0.5, 0.4), "#FF8800", 2), (cursor.OutputId, cursor.At, cursor.Color, cursor.Trail.Length));
        Assert.Equal(1, rig.State().GetProperty("air").GetProperty("overlay").GetProperty("cursors").GetInt32());
        // The same session moves its one cursor; nothing reaches the project or the journal.
        await rig.Run($"CURSOR {outputId} 0.6 0.4");
        Assert.Equal(new Vec2d(0.6, 0.4), Assert.Single(rig.Engine.WallCursors).At);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(journal, rig.Engine.Journal.Recent(100).Count);
        Assert.Contains("0–1", await rig.Refused($"CURSOR {outputId} 3 0.4"), StringComparison.Ordinal);
        rig.Clock.AdvanceMs(3_500);
        await WallRig.Until(() => rig.Engine.WallCursors.IsEmpty, "the cursor's lease to lapse");
        Assert.Contains("no cursor", await rig.Refused("CURSOR OFF"), StringComparison.Ordinal);
    }
}
