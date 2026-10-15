using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Fleet.PatternsEye;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>PlayMap's context through Patterns' own reading of it (a copy of ShowContext.Parse and the show-app section of
/// EyeGraph.Build, patterns-svc bcdbf28, as data): with Patterns' P2 — the context of every peer with the <c>context</c> cap read —
/// a busy PlayMap node is drawn with its show, its machine and its problems lit with PlayMap's own lights, and every link it gives
/// between drawn things is drawn (each one of the Eye's verbs). Without P2 it is a grey app heard on the network. The context is the
/// committed golden <c>Golden/context-busy.json</c>, which the Patterns-side patch's tests read too.</summary>
public sealed class PatternsEyeContractTests
{
    private static string GoldenPath() => Path.Combine(NodeExe.RepoRoot(), "tests", "PlayMap.Engine.Tests", "Fleet", "Golden", "context-busy.json");

    internal static async Task<string> BusyContextJson()
    {
        var outputs = new HealthTests.Outputs
        {
            Now = [.. Enumerable.Range(1, 20).Select(i => new OutputHealth("op_" + i, i != 7, i == 7 ? 0 : 60, i == 12 ? 3 : 0, 4.1, "DP-" + i))],
        };
        var services = HealthTests.Services(new ManualTime(), outputs, new HealthTests.FakeSampler());
        await using var engine = new EngineCore(services, EyeContextTests.Busy());
        Assert.True(WireGrammar.TryParse("PLAYLIST GO pl_show", out var go, out _));
        Assert.True((await engine.HandleAsync(go, SessionInfo.Desk("test"), CancellationToken.None)).Ok);
        return ContextBuilder.Build(engine).ToJson();
    }

    [Fact]
    public async Task PatternsDrawsPlayMapsThingsLitWithItsOwnLights()
    {
        var json = await BusyContextJson();
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            await File.WriteAllTextAsync(GoldenPath(), json + "\n");
        Assert.Equal((await File.ReadAllTextAsync(GoldenPath())).ReplaceLineEndings("\n"), json + "\n");

        var context = ShowContext.Parse(json);
        Assert.NotNull(context);
        Assert.Equal("playmap", context.Node.Kind);
        Assert.Equal("red", context.Light);

        // P2: Patterns reads the context of every live peer with the context cap and hands it to the Eye.
        var app = new EyeShowApp(context.Node.Id, EyeShowApps.KindWords("playmap"), context.Node.Name, ShowBusState.Live, false, "10.0.0.21")
        {
            Context = context,
        };
        var (nodes, edges) = EyeShowApps.Build([app]);
        var me = nodes.Single(n => n.Id == "app:0badc0de");
        Assert.Equal(CheckLight.Red, me.Light);
        Assert.Equal(context.Headline, me.Sub);

        var drawn = nodes.Where(n => n.Kind == "ShowAppItem").ToDictionary(n => n.Id[("app:0badc0de:").Length..], StringComparer.Ordinal);
        Assert.Equal(12, drawn.Count);
        foreach (var id in new[] { "show", "machine", "output:op_7", "output:op_12", "playlist:pl_show", "outputs" })
            Assert.True(drawn.ContainsKey(id), $"{id} is not drawn: {string.Join(", ", drawn.Keys)}");
        foreach (var item in context.Items.Where(i => drawn.ContainsKey(i.Id)))
            Assert.Equal(ShowContext.LightOf(item.Light), drawn[item.Id].Light);
        Assert.Equal(CheckLight.Red, drawn["output:op_7"].Light);
        Assert.Equal(CheckLight.Amber, drawn["output:op_12"].Light);

        // Every link PlayMap gives between two drawn things is drawn: none is dropped for a verb the Eye does not know.
        var given = context.Links.Where(l => drawn.ContainsKey(l.From) && drawn.ContainsKey(l.To)).ToList();
        Assert.NotEmpty(given);
        foreach (var l in given)
            Assert.Contains(edges, e => e.From == "app:0badc0de:" + l.From && e.To == "app:0badc0de:" + l.To);

        // PlayMap's reds and ambers join Patterns' problems queue.
        var problems = EyeShowApps.Problems(nodes);
        Assert.Contains("app:0badc0de:output:op_7", problems);
        Assert.Contains("app:0badc0de:output:op_12", problems);
        Assert.Contains("app:0badc0de:machine", problems);
    }

    [Fact]
    public async Task WithoutP2PlayMapIsAGreyAppHeardOnTheNetwork()
    {
        var context = ShowContext.Parse(await BusyContextJson())!;
        var app = new EyeShowApp(context.Node.Id, EyeShowApps.KindWords("playmap"), context.Node.Name, ShowBusState.Live, false, "10.0.0.21");
        var (nodes, _) = EyeShowApps.Build([app]);
        var me = nodes.Single(n => n.Id == "app:0badc0de");
        Assert.Equal(CheckLight.Grey, me.Light);
        Assert.Equal("Playmap Test node", me.Label);   // P1 makes it "PlayMap"
        Assert.DoesNotContain(nodes, n => n.Kind == "ShowAppItem");
    }
}
