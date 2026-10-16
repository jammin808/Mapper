using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Site;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>Preflight's site row: no control points is green and says so; the surveyed model within tolerance is green; a fault
/// injected — one control point re-surveyed 30 cm off the model — is red with the line that takes that point out; the whole model off
/// is red with SITE CHECK, which names every point. Every fix is a line of the catalogue. The node's own extension registers it.</summary>
public sealed class SitePreflightTests
{
    private static async Task<PreflightRow> Row(SiteRig rig) =>
        Assert.Single(await new SitePreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));

    private static void IsACatalogueLine(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out _), line);
        Assert.NotNull(VerbCatalog.Match(parsed!));
    }

    [Fact]
    public async Task TheModelAgainstItsControlPointsWithAFixForTheFault()
    {
        await using var rig = await ControlPointVerbTests.Surveyed();
        var none = await Row(rig);
        Assert.Equal((Light.Green, "no control points"), (none.Light, none.Value));

        await rig.Ok($"SITE POINTS {rig.File("points.csv")}");
        var good = await Row(rig);
        Assert.Equal(Light.Green, good.Light);
        Assert.Null(good.Fix);

        // The fault: CP3 re-surveyed 30 cm off the facade (north).
        var text = (await File.ReadAllTextAsync(rig.File("points.csv"))).Split('\n');
        var parts = text[3].Split(',');
        parts[1] = (double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 0.3).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        text[3] = string.Join(',', parts);
        await File.WriteAllTextAsync(rig.File("points-bad.csv"), string.Join('\n', text));
        await rig.Ok($"SITE POINTS {rig.File("points-bad.csv")}");
        var cp3 = rig.Engine.Store.Project.Stage.Controls.Single(c => c.Name == "CP3");
        var bad = await Row(rig);
        Assert.Equal(Light.Red, bad.Light);
        Assert.Equal($"SET {cp3.Id} enabled false", bad.Fix);
        Assert.Contains("CP3 30 cm", bad.Note, StringComparison.Ordinal);
        IsACatalogueLine(bad.Fix!);

        // Taking the point out (the fix) turns the row green again.
        await rig.Ok(bad.Fix!);
        Assert.Equal(Light.Green, (await Row(rig)).Light);

        // The whole model off: SITE CHECK names every point.
        var surface = rig.Engine.Store.Project.Stage.Surfaces[0];
        await rig.Engine.EditAsync(SiteRig.Desk, "move", p => p.WithStage(st => st with
        {
            Surfaces = st.Surfaces.Update(surface.Id, s => s with { Transform = s.Transform with { Position = new Vec3d(0, 0, 0.5) } }),
        }));
        var off = await Row(rig);
        Assert.Equal((Light.Red, "SITE CHECK"), (off.Light, off.Fix));
        IsACatalogueLine(off.Fix!);
    }

    [Fact]
    public void TheSiteExtensionRegistersTheCheck()
    {
        var services = new SiteExtension().Configure(EngineServices.ForTests());
        Assert.Contains(services.PreflightChecks, c => c is SitePreflight { Section: "site" });
    }
}
