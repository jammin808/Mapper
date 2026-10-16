using PlayMap.Core.Model;
using PlayMap.Core.Show;

namespace PlayMap.Core.Tests.Show;

/// <summary>The lock guard's areas: mapping holds everything an output or a surface carries that is not its name, layout or
/// picture — the measured correction and the calibration it uses included, and any field no area names (the cautious side);
/// layout holds the raster, display and latency; content holds what is shown. Each refusal names the area's lock. An edit made
/// the way the wire's SET makes it (through JSON, which gives every list of the entity anew) is judged by what it changed.</summary>
public sealed class LockGuardTests
{
    private static Project Locked(params string[] areas)
    {
        var p = Project.CreateDefault("Locks");
        var wall = new Surface("sf00000001", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#ffffff", []);
        return p.WithStage(s => s with { Surfaces = [wall] }) with
        {
            Show = p.Show with { Locks = [.. areas.Select(a => new ShowLock(a, LockKind.Area, "Desk", 0, ""))] },
        };
    }

    private static Project Output(Project p, Func<Output, Output> edit) => p with { Outputs = p.Outputs.SetItem(0, edit(p.Outputs[0])) };

    private static Project Surface(Project p, Func<Surface, Surface> edit) => p.WithStage(s => s with { Surfaces = [edit(s.Surfaces[0])] });

    [Fact]
    public void TheMappingAreaHoldsAnOutputsMeasuredCorrectionAndCalibration()
    {
        var p = Locked(LockAreas.Mapping);
        var measured = new MeasuredCorrection(null, null, null, null, 0.2, "", "", "ar00000001", 0);
        foreach (var edit in new Func<Output, Output>[]
        {
            o => o with { Measured = measured },
            o => o with { CalibrationId = "cs00000001" },
            o => o with { FallbackId = "cs00000002" },
            o => o with { StagedId = "cs00000003" },
            o => o with { ProjectorId = "pj00000001" },
        })
        {
            var why = LockGuard.Check(p, Output(p, edit), armed: false);
            Assert.NotNull(why);
            Assert.StartsWith("mapping is locked by Desk", why, StringComparison.Ordinal);
        }
        // The name, the layout and what the output shows are not the mapping.
        Assert.Null(LockGuard.Check(p, Output(p, o => o with { Name = "Left" }), armed: false));
        Assert.Null(LockGuard.Check(p, Output(p, o => o with { Width = 1280, LatencyMs = 40 }), armed: false));
        Assert.Null(LockGuard.Check(p, Output(p, o => o with { Source = SourceRef.Solid("#FF0000") }), armed: false));
    }

    [Fact]
    public void ASurfacesGeometryIsMappingAndItsPictureIsContent()
    {
        var mapping = Locked(LockAreas.Mapping);
        Assert.NotNull(LockGuard.Check(mapping, Surface(mapping, s => s with { Visible = false }), armed: false));
        Assert.NotNull(LockGuard.Check(mapping, Surface(mapping, s => s with { Content = s.Content with { Region = new RectD(0, 0, 0.5, 0.5) } }), armed: false));
        Assert.Null(LockGuard.Check(mapping, Surface(mapping, s => s with { Opacity = 0.5, Name = "Left wall" }), armed: false));
        Assert.Null(LockGuard.Check(mapping, Surface(mapping, s => s with { Content = s.Content with { Source = SourceRef.Solid("#FF0000") } }), armed: false));

        var content = Locked(LockAreas.Content);
        Assert.StartsWith("content is locked by Desk",
            LockGuard.Check(content, Surface(content, s => s with { Opacity = 0.5 }), armed: false), StringComparison.Ordinal);
        Assert.Null(LockGuard.Check(content, Surface(content, s => s with { Visible = false }), armed: false));
    }

    [Fact]
    public void TheLayoutAreaHoldsTheRasterAndTheLatencyAndArmedAddsMappingAndLayout()
    {
        var layout = Locked(LockAreas.Layout);
        Assert.NotNull(LockGuard.Check(layout, Output(layout, o => o with { LatencyMs = 40 }), armed: false));
        Assert.NotNull(LockGuard.Check(layout, Output(layout, o => o with { DisplayKey = "DP-2" }), armed: false));
        Assert.Null(LockGuard.Check(layout, Output(layout, o => o with { CalibrationId = "cs00000001" }), armed: false));

        var open = Locked();
        Assert.Null(LockGuard.Check(open, Output(open, o => o with { CalibrationId = "cs00000001" }), armed: false));
        Assert.Equal("mapping is locked while the show is armed — SHOW DISARM first",
            LockGuard.Check(open, Output(open, o => o with { CalibrationId = "cs00000001" }), armed: true));
        Assert.Equal("layout is locked while the show is armed — SHOW DISARM first",
            LockGuard.Check(open, Output(open, o => o with { Width = 1280 }), armed: true));
    }

    [Fact]
    public void ASetThroughJsonIsJudgedByWhatItChangedNotByItsNewLists()
    {
        var mask = new Mask("mk00000001", "Door", BezierPath.Rectangle(0.7, 0.3, 0.1, 0.6), MaskMode.Subtract, 0.01, false, true);
        var fx = new FxChain([new FxSlot("fx00000001", "fx.blur", "Blur", ParamSet.Empty.With("radius", 4), false, 1, FxBlend.Normal, 1, 0)]);
        var binding = new ParamBinding("opacity", "lfo:sine/4b/0", 0.5, 0, BindingCurve.Linear, 0);
        Project With(params string[] areas)
        {
            var p = Locked(areas);
            return Surface(p, s => s with { Masks = [mask], Tags = ["wall"], Fx = fx, Bindings = [binding] })
                with { Outputs = p.Outputs.SetItem(0, p.Outputs[0] with { Masks = [mask], Fx = fx }) };
        }
        Project Set(Project p, string field, string json) => field.StartsWith("out:", StringComparison.Ordinal)
            ? Output(p, o => ProjectJson.SetPath(o, field[4..], json))
            : Surface(p, s => ProjectJson.SetPath(s, field, json));

        // Under a mapping lock a masked surface's name, colour, picture and own flag, and an output's name, are not the mapping.
        var mapping = With(LockAreas.Mapping);
        foreach (var (field, json) in new[] { ("name", "\"Lobby\""), ("opacity", "0.5"), ("locked", "true"), ("color", "\"#FF0000\""), ("out:name", "\"Left\"") })
            Assert.Null(LockGuard.Check(mapping, Set(mapping, field, json), armed: false));
        // Its geometry and its masks are.
        Assert.StartsWith("mapping is locked by Desk", LockGuard.Check(mapping, Set(mapping, "transform.position", "{\"x\":1,\"y\":0,\"z\":0}"), armed: false),
            StringComparison.Ordinal);
        Assert.StartsWith("mapping is locked by Desk", LockGuard.Check(mapping, Set(mapping, "masks.0.feather", "0.2"), armed: false), StringComparison.Ordinal);

        // Under a content lock a surface with an effect and a binding is renamed; its opacity is content.
        var content = With(LockAreas.Content);
        Assert.Null(LockGuard.Check(content, Set(content, "name", "\"Lobby\""), armed: false));
        Assert.Null(LockGuard.Check(content, Set(content, "out:name", "\"Left\""), armed: false));
        Assert.StartsWith("content is locked by Desk", LockGuard.Check(content, Set(content, "opacity", "0.5"), armed: false), StringComparison.Ordinal);

        // A masked surface whose own Locked flag is set is unlocked by SET <id> locked false, and nothing else of it moves.
        var flagged = Surface(With(), s => s with { Locked = true });
        Assert.Null(LockGuard.Check(flagged, Set(flagged, "locked", "false"), armed: false));
        Assert.Equal(LockGuard.FlagWords(flagged.Stage.Surfaces[0]), LockGuard.Check(flagged, Set(flagged, "name", "\"Lobby\""), armed: false));
    }
}
