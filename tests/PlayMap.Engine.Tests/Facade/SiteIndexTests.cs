using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Site;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>INDEX's site section: the NEXT key (SITE GUIDE) lights by the facade work begun. On a node that holds none of the facade's
/// own inputs — a playback node, or a rig of projectors, outputs, a scan, a stack and the lens prior any weak solve asks for — it is
/// grey with the guide's next step named, as STATE (no site, so no next step), the context (no site item) and preflight (green, "no
/// control points") ask nothing of it either; a site begins the work and lights it amber while a step is left, with STATE's next step
/// and the context's site item beside it. Through is green (<see cref="FacadeEndToEndTests"/>). The stack's own keys light as before.</summary>
public sealed class SiteIndexTests
{
    private const string FirstStep = "survey: Import the surveyed model or cloud (E57, LAS, OBJ, PLY, glTF)";

    /// <summary>The site section's rows from INDEX, by label.</summary>
    internal static async Task<Dictionary<string, (string Id, string Light, string State)>> SiteRows(SiteRig rig)
    {
        var index = SiteRig.Json(await rig.Ok("INDEX"));
        var site = index.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "site");
        return site.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("label").GetString()!,
            r => (r.GetProperty("id").GetString()!, r.GetProperty("light").GetString()!, r.GetProperty("state").GetString()!));
    }

    /// <summary>The NEXT key's light and state.</summary>
    internal static async Task<(string Light, string State)> GuideKey(SiteRig rig)
    {
        var (id, light, state) = (await SiteRows(rig))["NEXT"];
        Assert.Equal("SITE GUIDE", id);
        return (light, state);
    }

    private static bool HasSiteItem(SiteRig rig) =>
        SiteRig.Json(rig.Engine.Context()).GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetString() == "site");

    [Fact]
    public async Task APlainNodesGuideKeyIsGreyAndNamesTheFirstStep()
    {
        await using var rig = new SiteRig();
        Assert.Equal(("grey", FirstStep), await GuideKey(rig));
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("stage").GetProperty("site").ValueKind);
        Assert.False(HasSiteItem(rig));
        var preflight = Assert.Single(await new SitePreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));
        Assert.Equal((Light.Green, "no control points"), (preflight.Light, preflight.Value));
    }

    [Fact]
    public async Task AStackedRigWithALensPriorIsGreyUntilASiteBeginsTheWork()
    {
        await using var rig = new SiteRig(StackKit.Project(StackKit.Sim()));
        await rig.Ok($"STACK NEW S1 {StackKit.P1} {StackKit.P2}");
        var rows = await SiteRows(rig);
        Assert.Equal(("amber", "not aligned"), (rows["ALIGN S1"].Light, rows["ALIGN S1"].State));
        Assert.Equal(("grey", FirstStep), (rows["NEXT"].Light, rows["NEXT"].State));

        // The lens prior a weak solve on any rig asks for, and a measured position: no facade work, so nothing is asked of the node on
        // any surface.
        await rig.Ok($"PROJECTOR PRIOR {StackKit.P1} THROW 3.02 ±2");
        await rig.Ok($"PROJECTOR PRIOR {StackKit.P2} AT 0 10 100 ±0.5");
        var priors = rig.State().GetProperty("stage").GetProperty("projectors").EnumerateArray()
            .ToDictionary(pj => pj.GetProperty("id").GetString()!, pj => pj.GetProperty("priors").GetString());
        Assert.Equal(("TR 3.02 ±2 %", "at 0 10 100 ±50 cm"), (priors[StackKit.P1], priors[StackKit.P2]));
        Assert.Equal(("grey", FirstStep), await GuideKey(rig));
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("stage").GetProperty("site").ValueKind);
        Assert.False(HasSiteItem(rig));

        // A site begins the work: amber, and STATE's next step is the same step.
        await rig.Ok("SITE ORIGIN 512340 5401230 120 CRS EPSG:25832");
        Assert.Equal(("amber", FirstStep), await GuideKey(rig));
        var next = rig.State().GetProperty("stage").GetProperty("site").GetProperty("next");
        Assert.Equal(("survey", "waiting"), (next.GetProperty("step").GetString(), next.GetProperty("state").GetString()));
        Assert.True(HasSiteItem(rig));
    }
}
