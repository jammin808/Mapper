using PlayMap.Core.Model;
using PlayMap.Core.Show;

namespace PlayMap.Core.Tests.Show;

/// <summary>The lock guard's areas: mapping holds everything an output or a surface carries that is not its name, layout or
/// picture — the measured correction and the calibration it uses included, and any field no area names (the cautious side);
/// layout holds the raster, display and latency; content holds what is shown. Each refusal names the area's lock.</summary>
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
}
