using PlayMap.Core.Model;

namespace PlayMap.Assistant.Tests;

/// <summary>Explain(STATE) answers in plain words with no model, from the facts alone; the recipes turn
/// mapping intents into the lines a person would type, from the project alone, and every line they write
/// passes the gate.</summary>
public class ExplainAndRecipeTests
{
    [Fact]
    public void ExplainReadsTheWireDocsSampleStateInPlainWords()
    {
        var lines = StateExplainer.ExplainLines(SampleShow.StateJson);

        Assert.Equal(
        [
            "Stage left is green: Show · Intro 0:42 left · 3 outputs green · synced to FOH (0.2 ms).",
            "The focused playlist Show is playing intro.mp4 at 1:02 of 3:30, 2:28 left.",
            "The one output is open and green.",
            "The stage has 6 surfaces on 2 meshes; 1 projector (solved: Left (0.6 px)).",
            "It follows 9f8e7d6c for sync: offset 0.21 ms, round trip 0.35 ms.",
            "The Patterns link at 10.0.0.5:9697 is live, clip VT 2:28.",
            "The Stagewatch link at 10.0.0.6:9924 is live, timer Keynote 12:30 running.",
            "It hears one peer, none in trouble.",
            "The project Show is saved.",
        ], lines);
    }

    [Fact]
    public void ExplainSaysWhatIsWrong()
    {
        const string state =
            """
            {"name":"Rack 2","light":"red","air":{"blackout":true,"frozen":false,"pattern":"pattern.grid","identify":false},
             "video":null,
             "playlists":[{"name":"Loop","focused":false,"state":"playing","item":{"label":"Snow"},"remainingMs":95000},{"name":"Spare","state":"stopped"}],
             "outputs":[{"name":"Projector 1","open":true,"light":"amber","dropped":12},{"name":"Projector 2","open":false,"light":"red"}],
             "media":{"missing":2,"ready":false,"transfers":[{"name":"Intro","state":"pulling"}]},
             "stage":{"surfaces":3,"meshes":1,"projectors":[{"name":"Left","solved":false}]},
             "scan":{"state":"decoding"},
             "links":{"patterns":{"state":"down","host":"10.0.0.5:9697"}},
             "peers":[{"name":"FOH","light":"amber"},{"name":"SW","light":"green"}],
             "project":{"name":"Show","dirty":true}}
            """;

        var text = StateExplainer.Explain(state);

        Assert.Contains("Rack 2 is red.", text, StringComparison.Ordinal);
        Assert.Contains("Every output is blacked out.", text, StringComparison.Ordinal);
        Assert.Contains("The test pattern pattern.grid is over the outputs.", text, StringComparison.Ordinal);
        Assert.Contains("Nothing is loaded on the focused playlist.", text, StringComparison.Ordinal);
        Assert.Contains("Playlist Loop is playing Snow, 1:35 left.", text, StringComparison.Ordinal);
        Assert.Contains("One other playlist is not playing.", text, StringComparison.Ordinal);
        Assert.Contains("Projector 1 is amber (12 frames dropped); Projector 2 is closed.", text, StringComparison.Ordinal);
        Assert.Contains("2 media files are missing on this node.", text, StringComparison.Ordinal);
        Assert.Contains("One media transfer is running.", text, StringComparison.Ordinal);
        Assert.Contains("not solved: Left", text, StringComparison.Ordinal);
        Assert.Contains("A scan is decoding.", text, StringComparison.Ordinal);
        Assert.Contains("The Patterns link at 10.0.0.5:9697 is down.", text, StringComparison.Ordinal);
        Assert.Contains("It hears 2 peers; FOH is amber.", text, StringComparison.Ordinal);
        Assert.Contains("The project Show has unsaved changes.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainCountsOnlyTheTransfersOnTheirWayAndSaysOnesThatFailed()
    {
        const string one =
            """
            {"name":"Rack 2","light":"amber","media":{"missing":1,"ready":false,"transfers":[
              {"name":"Intro","state":"failed: Stage left's media list could not be read: answered 503"}]}}
            """;
        var text = StateExplainer.Explain(one);
        Assert.DoesNotContain("is running", text, StringComparison.Ordinal);
        Assert.Contains("The media transfer of Intro failed: Stage left's media list could not be read: answered 503; MEDIA FETCH asks for it again.", text,
            StringComparison.Ordinal);

        const string several =
            """
            {"name":"Rack 2","light":"amber","media":{"missing":4,"ready":false,"transfers":[
              {"name":"A","state":"pulling"},{"name":"B","state":"queued"},{"name":"C","state":"failed: answered 503"},{"name":"D","state":"failed: answered 503"}]}}
            """;
        var lines = StateExplainer.ExplainLines(several);
        Assert.Contains("2 media transfers are running.", lines);
        Assert.Contains("2 media transfers failed (each row's state says why); MEDIA FETCH asks for them again.", lines);
    }

    [Fact]
    public void ExplainNeverGuessesWhatStateDoesNotSay()
    {
        Assert.Equal(["This node is grey (not known)."], StateExplainer.ExplainLines("{}"));
        Assert.Equal(["STATE could not be read, so nothing is known about the node."], StateExplainer.ExplainLines("{not json"));
        Assert.Empty(StateExplainer.ExplainLines(""));
        Assert.Equal("1:02:03", StateExplainer.Clock(3_723_000));
        Assert.Null(StateExplainer.Clock(null));
    }

    [Fact]
    public void FillTaggedPutsTheGeneratorOnEveryMatchingSurfaceAndShowsHiddenOnes()
    {
        var plan = MappingRecipes.FillTagged(SampleShow.Project(), "windows", "Snow");

        Assert.Empty(plan.Before);
        Assert.Equal(
        [
            $"SURFACE SOURCE {SampleShow.WindowLeft} generator:{SampleShow.Snow}",
            $"SURFACE SOURCE {SampleShow.WindowRight} generator:{SampleShow.Snow}",
            $"SURFACE SHOW {SampleShow.WindowRight} ON",
        ], plan.Lines);
        // The same generator found by its kind gives the same lines.
        Assert.Equal(plan.Lines, MappingRecipes.FillTagged(SampleShow.Project(), "window", "particles.field").Lines);
    }

    [Fact]
    public void FillTaggedWithAKindNotYetMadePlansTheGeneratorFirst()
    {
        var plan = MappingRecipes.FillTagged(SampleShow.Project(), "door", "julia");

        Assert.Equal(["GENERATOR NEW fractal.julia Julia"], plan.Before);
        Assert.Empty(plan.Lines);
        Assert.Contains("plan again", Assert.Single(plan.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void FillTaggedSaysWhichTagsExistWhenNoneMatch()
    {
        var plan = MappingRecipes.FillTagged(SampleShow.Project(), "chimney", "Snow");

        Assert.True(plan.IsEmpty);
        Assert.Contains("door, glass, window", Assert.Single(plan.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void FillTaggedNeverWritesALineThatChangesNothing()
    {
        var project = SampleShow.Project();
        project = project.WithStage(s => s with
        {
            Surfaces = s.Surfaces.Update(SampleShow.WindowLeft, f => f with { Content = f.Content with { Source = SourceRef.Generator(SampleShow.Snow) } }),
        });

        var plan = MappingRecipes.FillTagged(project, "window", SampleShow.Snow);

        Assert.DoesNotContain($"SURFACE SOURCE {SampleShow.WindowLeft} generator:{SampleShow.Snow}", plan.Lines);
        Assert.Contains("Window L (sf00000001) already shows Snow.", plan.Notes);
    }

    [Fact]
    public void BlendingProjectorsTwoAndThreeSwitchesManualOutputsToTheAutoBlendFirst()
    {
        var plan = MappingRecipes.BlendOutputs(SampleShow.Project(), ["2", "3"]);

        // One solve covers every output that blends automatically: a line per output would be refused after the first.
        Assert.Equal(["SET op00000002 blend.auto3D true", "BLEND SOLVE ALL"], plan.Lines);
    }

    [Fact]
    public void BlendingEveryProjectorOutputIsOneSolveForAll()
    {
        var plan = MappingRecipes.BlendOutputs(SampleShow.Project(), []);

        Assert.Equal(["SET op00000002 blend.auto3D true", "BLEND SOLVE ALL"], plan.Lines);
    }

    [Fact]
    public void ABlendOfACanvasOutputOrOfOneProjectorIsExplainedNotPlanned()
    {
        var plan = MappingRecipes.BlendOutputs(SampleShow.Project(), ["Confidence", "Projector 1", "Nowhere"]);

        Assert.Empty(plan.Lines);
        Assert.Contains(plan.Notes, n => n.Contains("Confidence (op00000004) is not a projector output", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Contains("Nothing in the project answers to 'Nowhere'", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Contains("only Projector 1 (op00000001) qualifies", StringComparison.Ordinal));
    }

    [Fact]
    public void ATestPatternGoesOnEveryProjectorOutputAndNotTheConfidenceScreen()
    {
        var plan = MappingRecipes.TestPatternOnProjectors(SampleShow.Project(), "grid");

        Assert.Equal(["PATTERN pattern.grid ON op00000001", "PATTERN pattern.grid ON op00000002", "PATTERN pattern.grid ON op00000003"], plan.Lines);
        Assert.Contains("is not a test pattern", Assert.Single(MappingRecipes.TestPatternOnProjectors(SampleShow.Project(), "julia").Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLineARecipeWritesPassesTheGate()
    {
        var project = SampleShow.Project();
        var plans = new[]
        {
            MappingRecipes.FillTagged(project, "window", "Snow"),
            MappingRecipes.FillTagged(project, "door", "julia"),
            MappingRecipes.BlendOutputs(project, []),
            MappingRecipes.BlendOutputs(project, ["Projector 2", "pj00000003"]),
            MappingRecipes.TestPatternOnProjectors(project),
            MappingRecipes.PatternsOff(),
        };

        foreach (var line in plans.SelectMany(p => p.Before.Concat(p.Lines)))
        {
            var check = CommandGate.Check(line, project);
            Assert.True(check.Ok, line + ": " + check.Reason);
        }
    }
}
