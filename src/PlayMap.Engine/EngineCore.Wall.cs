using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-wall's hooks into the engine (EngineCore.Modules.cs): the quad on the wall and the edit overlay
/// (Wall/EngineCore.Quads.cs), the calibration score, ledger and re-solve (Wall/EngineCore.Calibration.cs).</summary>
public sealed partial class EngineCore
{
    partial void RouteWall(VerbRoutes routes)
    {
        RouteQuads(routes);
        RouteCalibration(routes);
    }

    partial void TickWall(long leaderNs)
    {
        TickQuads();
        TickNative();
    }

    partial void StateWall(Utf8JsonWriter w)
    {
        WriteCalibration(w);
        WriteOnboarding(w);
    }

    partial void AirWall(Utf8JsonWriter w) => WriteOverlay(w);

    partial void ProjectorWall(Utf8JsonWriter w, Projector pj)
    {
        var p = _snapshot.Project.Project;
        w.WriteString("grade", GradeOf(p, pj.Id));
        var quads = 0;
        foreach (var s in p.Stage.Surfaces)
            if (s.Pixel is { } q && string.Equals(q.ProjectorId, pj.Id, StringComparison.Ordinal))
                quads++;
        w.WriteNumber("quads", quads);
    }

    partial void ContextWall(List<ContextItem> items, List<ContextLink> links)
    {
        ContextCalibration(items);
        var o = _snapshot.Air.Overlay;
        if (o.On)
            items.Add(new ContextItem("overlay", "overlay", "Edit overlay", Light.Amber, "edit overlay on " + OverlayWhere(o)));
    }

    partial void ValidateWall(Project p, object entity, ref string? why)
    {
        if (why is null && entity is Surface s)
            why = ValidateQuad(p, s);
    }
}
