using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Align;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>Blend γ as the hand's override: an output's blend is drawn through the hand's γ, else its measured one, else 2.2, so a γ of
/// 0 draws exactly what a stored 2.2 drew; PROJECT OPEN reads an earlier round's file by the γ rule and names, in its answer and its
/// journal row, every output it moved; ALIGN REVERT after an accept draws the γ the output drew before the run.</summary>
public sealed class BlendGammaTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    private static async Task<BlendMap> EdgeMapOf(Project project, string outputId)
    {
        var store = new ProjectStore(project);
        await using var resources = new StageResources(new StageMeshes(() => null), StageResourceOptions.Default with { BlendDebounce = TimeSpan.FromSeconds(30) });
        resources.Observe(store.Current);
        await resources.WhenSettledAsync(store.Current.Revision).WaitAsync(Patience);
        resources.Observe(store.Current);
        return resources.Blend(outputId)!;
    }

    private static Project WithEdge(double gamma, MeasuredCorrection? measured = null)
    {
        var project = BlendSolveTests.TwoProjectorsOnAWall();
        return project with
        {
            Outputs = project.Outputs.Update("op_right", o => o with
            {
                Blend = o.Blend with { Auto3D = false, Edges = new EdgeBlend(300, 0, 0, 0), BlackLevel = 0.02, Gamma = gamma },
                Measured = measured,
            }),
        };
    }

    private static MeasuredCorrection MeasuredGamma(double gamma) => new(null, null, null, new MeasuredColor(1, 1, 1, gamma, 1), 0, "", "", "", 1);

    [Fact]
    public async Task AManualEdgeAtGammaZeroIsTheMapAStored22DrewAndFollowsTheMeasuredGamma()
    {
        var zero = await EdgeMapOf(WithEdge(0), "op_right");
        var stored = await EdgeMapOf(WithEdge(2.2), "op_right");
        Assert.Equal(stored.Weights, zero.Weights);
        Assert.Equal(stored.Pedestal, zero.Pedestal);

        var measured = await EdgeMapOf(WithEdge(0, MeasuredGamma(2.4)), "op_right");
        var typed = await EdgeMapOf(WithEdge(2.4), "op_right");
        Assert.Equal(typed.Weights, measured.Weights);
        Assert.NotEqual(zero.Weights, measured.Weights);
        // The hand's γ wins over the measured one.
        var hand = await EdgeMapOf(WithEdge(1.8, MeasuredGamma(2.4)), "op_right");
        Assert.Equal((await EdgeMapOf(WithEdge(1.8), "op_right")).Weights, hand.Weights);
    }

    [Fact]
    public void TheAutomaticBlendAtGammaZeroIsTheBlendAStored22Made()
    {
        static Project At(double gamma)
        {
            var p = BlendSolveTests.TwoProjectorsOnAWall();
            return p with { Outputs = [.. p.Outputs.Select(o => o with { Blend = o.Blend with { Gamma = gamma } })] };
        }
        var meshes = new StageMeshes(() => null);
        var zero = BlendInputs.Of(At(0)).Solve(meshes);
        var stored = BlendInputs.Of(At(2.2)).Solve(meshes);
        var other = BlendInputs.Of(At(1.8)).Solve(meshes);
        foreach (var id in new[] { "op_left", "op_right" })
        {
            Assert.Equal(stored.For(id)!.Map.Weights, zero.For(id)!.Map.Weights);
            Assert.NotEqual(other.For(id)!.Map.Weights, zero.For(id)!.Map.Weights);
        }
    }

    [Fact]
    public async Task ProjectOpenReadsAnEarlierRoundsFileByTheGammaRuleAndNamesEveryOutputItMoved()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-gamma-").FullName;
        try
        {
            var path = Path.Combine(folder, "round2.playmap.json");
            File.Copy(Path.Combine(RepoRoot(), "tests", "fixtures", "projects", "round2.playmap.json"), path);
            await using var engine = new EngineCore(EngineServices.ForTests());

            var opened = await Run(engine, "PROJECT OPEN " + WireGrammar.Quote(path));

            Assert.True(opened.Ok, opened.Payload);
            var project = engine.Store.Project;
            Assert.Equal(Project.CurrentSchema, project.Schema);
            Assert.Equal(new[] { 0, 1.8, 0, 2.2 }, project.Outputs.Select(o => o.Blend.Gamma));
            Assert.Equal(new[] { 2.31, 1.8, 2.2, 2.2 }, project.Outputs.Select(o => o.DrawnBlendGamma()));
            foreach (var o in project.Outputs)
                Assert.Equal(o.Blend.Gamma == 0, opened.Effect.Contains($"({o.Id})", StringComparison.Ordinal));
            Assert.StartsWith("opened " + project.Name + " — ", opened.Effect, StringComparison.Ordinal);
            Assert.Contains(engine.Journal.Recent(engine.Journal.RecentCapacity),
                r => r.Text.Contains("op_1", StringComparison.Ordinal) && r.Text.Contains("op0a0b0c03", StringComparison.Ordinal));

            // Saved by this build, the file opens as it was written, with no note.
            var saved = Path.Combine(folder, "saved.playmap.json");
            Assert.True((await Run(engine, "PROJECT SAVE " + WireGrammar.Quote(saved))).Ok);
            var again = await Run(engine, "PROJECT OPEN " + WireGrammar.Quote(saved));
            Assert.Equal("opened " + project.Name, again.Effect);
            Assert.Equal(ProjectJson.Serialize(project), ProjectJson.Serialize(engine.Store.Project));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ARevertAfterAnAcceptDrawsTheGammaTheOutputDrewBeforeTheRun()
    {
        var project = AlignRig.Project(2, "flat-screen:tile2x1");
        Assert.All(project.Outputs, o => Assert.Equal(0, o.Blend.Gamma));
        await using var rig = new AlignRig(project);
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {run}");
        Assert.All(rig.Current.Outputs, o => Assert.NotEqual(0, o.Blend.Gamma));

        await rig.Ok($"ALIGN REVERT {group}");

        Assert.All(rig.Current.Outputs, o => Assert.Null(o.Measured));
        Assert.All(rig.Current.Outputs, o => Assert.Equal(BlendSettings.AssumedGamma, o.DrawnBlendGamma()));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
    }
}
