using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>The site's verbs under the node's other rules: once the node begins to stop every site line is refused with the words and
/// nothing moves; LOCK AREA mapping holds the site, its points and the priors (a survey import is refused before its file is read),
/// and a locked control point holds against a re-survey; a survey is no media, so the show's media and the optimiser never see it.</summary>
public sealed class SiteCrossTests
{
    [Fact]
    public async Task EverySiteLineIsRefusedOnceTheNodeBeginsToStop()
    {
        await using var rig = await ControlPointVerbTests.Surveyed();
        var revision = rig.Engine.Store.Current.Revision;
        rig.Engine.BeginStop();
        foreach (var line in new[]
        {
            "SITE", "SITE MAINS 50", "SITE ORIGIN 512300 5401200 100", $"SITE POINTS {rig.File("points.csv")}", "SITE CHECK",
            $"SURVEY IMPORT {rig.File("front.obj")}", "SURVEY LIST", "PROJECTOR PRIOR pj_none THROW 3",
        })
            Assert.Equal(EngineCore.StoppingWords, await rig.Refused(line));
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
    }

    [Fact]
    public async Task ALockedMappingAreaHoldsTheSiteAndALockedPointHoldsAgainstAResurvey()
    {
        await using var rig = await ControlPointVerbTests.Surveyed();
        var job = rig.Engine.SurveyJob;
        await rig.Ok("LOCK AREA mapping set for the night");
        var revision = rig.Engine.Store.Current.Revision;
        foreach (var line in new[] { "SITE MAINS 50", "SITE ORIGIN 512300 5401200 100", $"SITE POINTS {rig.File("points.csv")}" })
            Assert.Contains("mapping is locked by", await rig.Refused(line), StringComparison.Ordinal);
        // The import is refused before it reads the file: no job starts.
        Assert.Contains("mapping is locked by", await rig.Refused($"SURVEY IMPORT {rig.File("front.obj")} NAME again"), StringComparison.Ordinal);
        Assert.Same(job, rig.Engine.SurveyJob);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        await rig.Ok("UNLOCK AREA mapping");

        var ids = SiteRig.Json(await rig.Ok($"SITE POINTS {rig.File("points.csv")}")).GetProperty("ids");
        var cp1 = ids[0].GetString()!;
        var locked = SiteRig.Json(await rig.Ok($"LOCK {cp1}"));
        Assert.Equal(cp1, locked.GetProperty("target").GetString());
        await File.WriteAllTextAsync(rig.File("points2.csv"), ControlPointVerbTests.Points(1.0));
        Assert.Contains("CP1 (control point) is locked by", await rig.Refused($"SITE POINTS {rig.File("points2.csv")}"), StringComparison.Ordinal);
        Assert.Contains("CP1 (control point) is locked by", await rig.Refused($"SET {cp1} name \"Corner\""), StringComparison.Ordinal);
        var survey = rig.Engine.Store.Project.Stage.Surveys[0].Id;
        await rig.Ok($"LOCK {survey}");
        Assert.Contains("(survey) is locked by", await rig.Refused($"SET {survey} name \"Front\""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASurveyIsNoMediaTheShowPlaysOrTheOptimiserSees()
    {
        await using var rig = await ControlPointVerbTests.Surveyed();
        var p = rig.Engine.Store.Project;
        Assert.Single(p.Stage.Surveys);
        Assert.Empty(p.Media);
        Assert.Empty(ShowMedia.Of(p));
        // The survey's surface shows nothing until something is put on it.
        Assert.Equal(Core.Model.SourceKind.None, Assert.Single(p.Stage.Surfaces).Content.Source.Kind);
    }
}
