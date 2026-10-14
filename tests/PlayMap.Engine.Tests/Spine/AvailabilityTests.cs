using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlayMap.Assistant;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>What an assistant is shown is what the node answers: the prompt, MCP's list_verbs and the context's commands hold every
/// routed row and no unrouted round-2 row; VERBS lists every row but the node-to-node ones and marks each available or not.</summary>
public sealed class AvailabilityTests
{
    private static string PromptLine(VerbSpec v) => "- " + v.Syntax + " — " + v.Words + " — risk " + ShowContext.RiskWord(v.Risk);

    [Fact]
    public async Task VerbsListsEveryRowButTheNodeToNodeOnesAndMarksWhichAreAvailable()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        Assert.True(WireGrammar.TryParse("VERBS", out var line, out _));
        var reply = await engine.HandleAsync(line, SessionInfo.Desk("t"), CancellationToken.None);
        Assert.True(reply.Ok);

        using var doc = JsonDocument.Parse(reply.Payload!);
        var rows = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("available").GetBoolean());
        foreach (var v in VerbCatalog.All)
        {
            if (v.Internal)
            {
                Assert.False(rows.ContainsKey(v.Name), v.Name);
                continue;
            }
            Assert.True(rows.TryGetValue(v.Name, out var available), v.Name);
            Assert.Equal(v.Round == 1, available);
        }
    }

    [Fact]
    public void ThePromptHoldsEveryRoutedRowAndNoUnroutedRoundTwoRow()
    {
        var routes = new VerbRoutes();
        var routed = VerbCatalog.Find("SHOW ARM")!;
        routes.Lane(routed.Name, _ => CommandReply.Success());

        var bare = AssistantPrompt.For(new VerbRoutes());
        var withShow = AssistantPrompt.For(routes);

        Assert.Same(AssistantPrompt.System, bare);
        Assert.Same(withShow, AssistantPrompt.For(routes));
        Assert.Contains(PromptLine(routed), withShow, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptLine(routed), bare, StringComparison.Ordinal);
        foreach (var v in VerbCatalog.All)
        {
            if (v.Round == 1 && !v.Internal)
                Assert.Contains(PromptLine(v), withShow, StringComparison.Ordinal);
            else if (v != routed)
                Assert.DoesNotContain(PromptLine(v), withShow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task McpListVerbsAndTheContextsCommandsHoldOnlyWhatTheNodeAnswers()
    {
        await using var rig = await HostRig.StartAsync();
        var routed = VerbCatalog.Find("SHOW ARM")!;
        rig.Engine.Routes.Lane(routed.Name, _ => CommandReply.Success(effect: "armed"));
        using var http = rig.Http();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));

        var listed = await mcp.CallToolAsync("list_verbs");
        using var doc = JsonDocument.Parse(string.Concat(listed.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        var names = doc.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.All(doc.RootElement.EnumerateArray(), r => Assert.True(r.GetProperty("available").GetBoolean()));
        foreach (var v in VerbCatalog.All)
            Assert.Equal(!v.Internal && (v.Round == 1 || v == routed), names.Contains(v.Name));

        using var context = JsonDocument.Parse(rig.Engine.Context());
        var commands = context.RootElement.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("verb").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(routed.Syntax, commands);
        foreach (var v in VerbCatalog.All.Where(v => v.Round == 2 && v != routed))
            Assert.DoesNotContain(v.Syntax, commands);
    }
}
