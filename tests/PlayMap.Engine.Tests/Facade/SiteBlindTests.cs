using System.Text.Json;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>The site's edits from a blind draft: SITE MAINS, SITE ORIGIN, PROJECTOR PRIOR and SITE POINTS run into the draft — their
/// replies say staged, the live project, STATE and the air keep the site as it was — and land with the draft's commit as one undo
/// step. SURVEY IMPORT, heavy work that is never staged, is refused from the session that holds the draft and starts nothing.</summary>
public sealed class SiteBlindTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "playmap-siteblind-" + Guid.NewGuid().ToString("N")[..10]);

    public SiteBlindTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the OS is left for the temp cleaner; the test's result does not depend on it.
        }
    }

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement.Clone();

    [Fact]
    public async Task SiteEditsFromADraftAreStagedAndLandWithTheCommit()
    {
        await using var rig = new ShowRig(LongThrowVerbTests.Facade(0), s => s with { ProjectFolder = _folder });
        var points = Path.Combine(_folder, "points.csv");
        await File.WriteAllTextAsync(points, ControlPointVerbTests.Points(0));
        var revision = rig.Engine.Store.Current.Revision;
        await rig.Ok("BLIND ON");

        var mains = Json(await rig.Ok("SITE MAINS 50"));
        Assert.True(mains.GetProperty("staged").GetBoolean());
        var origin = Json(await rig.Ok("SITE ORIGIN 512300 5401200 100 CRS EPSG:25832"));
        Assert.True(origin.GetProperty("staged").GetBoolean());
        Assert.StartsWith("staged in the blind draft: site origin", origin.GetProperty("words").GetString(), StringComparison.Ordinal);
        var prior = Json(await rig.Ok("PROJECTOR PRIOR pj_far1 THROW 3.02"));
        Assert.True(prior.GetProperty("staged").GetBoolean());
        var imported = Json(await rig.Ok($"SITE POINTS {points}"));
        Assert.True(imported.GetProperty("staged").GetBoolean());
        Assert.Equal(14, imported.GetProperty("added").GetInt32());

        // Heavy work is never staged: refused from the draft's session, nothing started.
        Assert.Contains("not staged in a blind draft", await rig.Refused($"SURVEY IMPORT {points}"), StringComparison.Ordinal);
        Assert.Null(rig.Engine.SurveyJob);

        // Live: nothing moved — the document, STATE and the air's snapshot.
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        var live = rig.Engine.Store.Project;
        Assert.False(live.Stage.Site.IsSet);
        Assert.Empty(live.Stage.Controls);
        Assert.Equal(0, live.FindProjector("pj_far1")!.Priors.ThrowRatio);
        var stage = Json(rig.Engine.State().Json).GetProperty("stage");
        Assert.Equal(JsonValueKind.Null, stage.GetProperty("site").ValueKind);
        Assert.Equal(0, stage.GetProperty("controls").GetInt32());
        Assert.Equal("", stage.GetProperty("projectors")[0].GetProperty("priors").GetString());
        Assert.False(rig.Engine.Snapshot.Project.Project.Stage.Site.IsSet);

        // The draft holds all four; the diff names the site frame, the projector and the points.
        var draft = rig.Engine.Snapshot.Draft!.Project;
        Assert.Equal(("EPSG:25832", 50), (draft.Stage.Site.Crs, draft.Stage.Site.MainsHz));
        Assert.Equal(14, draft.Stage.Controls.Length);
        Assert.Equal(3.02, draft.FindProjector("pj_far1")!.Priors.ThrowRatio);
        var ids = Json(await rig.Ok("BLIND DIFF")).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Contains("site", ids);
        Assert.Contains("pj_far1", ids);
        Assert.All(draft.Stage.Controls, c => Assert.Contains(c.Id, ids));

        // The commit lands it all as one undo step.
        await rig.Ok("BLIND COMMIT");
        rig.Advance(34);
        await rig.Ticked();
        live = rig.Engine.Store.Project;
        Assert.Equal(("EPSG:25832", 50), (live.Stage.Site.Crs, live.Stage.Site.MainsHz));
        Assert.Equal(14, live.Stage.Controls.Length);
        Assert.Equal(3.02, live.FindProjector("pj_far1")!.Priors.ThrowRatio);
        Assert.Equal(revision + 1, rig.Engine.Store.Current.Revision);
        Assert.StartsWith("blind commit: ", rig.Engine.Store.UndoLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideADraftTheSiteEditsAreLiveAndSayNothingOfStaging()
    {
        await using var rig = new ShowRig(LongThrowVerbTests.Facade(0), s => s with { ProjectFolder = _folder });
        var mains = Json(await rig.Ok("SITE MAINS 60"));
        Assert.False(mains.GetProperty("staged").GetBoolean());
        Assert.Equal(60, rig.Engine.Store.Project.Stage.Site.MainsHz);
        var prior = Json(await rig.Ok("PROJECTOR PRIOR pj_far1 THROW 3.02"));
        Assert.False(prior.GetProperty("staged").GetBoolean());
        Assert.DoesNotContain("staged", prior.GetProperty("words").GetString(), StringComparison.Ordinal);
    }
}
