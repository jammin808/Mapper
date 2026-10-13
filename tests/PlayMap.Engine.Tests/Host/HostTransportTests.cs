using System.Text.Json;
using ModelContextProtocol.Client;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A real headless node reached the way peers reach it: a line client on the wire (PING, STATE, a project
/// subscription that hears the edit it made), and the web port (STATE over HTTP, MCP listing its tools and STATE
/// counting the assistant on its door).</summary>
public sealed class HostTransportTests
{
    [Fact]
    public async Task ALineClientPingsAndStateCarriesTheNode()
    {
        await using var rig = await HostRig.StartAsync("Stage left");
        await using var c = await rig.WireAsync();

        var pong = await c.AskAsync("@p PING 1760101234567");
        Assert.StartsWith("OK @p PONG 1760101234567 ", pong, StringComparison.Ordinal);

        var reply = await c.AskAsync("@s STATE");
        Assert.StartsWith("OK @s {\"v\":1,\"seq\":", reply, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(reply!["OK @s ".Length..]);
        var state = doc.RootElement;
        Assert.Equal(rig.Id, state.GetProperty("id").GetString());
        Assert.Equal("playmap", state.GetProperty("t").GetString());
        Assert.Equal("Stage left", state.GetProperty("name").GetString());
        Assert.Equal("none", state.GetProperty("machine").GetProperty("renderer").GetString());
        Assert.Empty(state.GetProperty("faults").EnumerateArray());
        Assert.True(state.GetProperty("ai").GetProperty("mcp").GetBoolean());
        Assert.Equal("solo", state.GetProperty("sync").GetProperty("role").GetString());

        // STATUS is STATE's alias, answered by the transport like STATE itself.
        Assert.StartsWith("OK @t {\"v\":1,\"seq\":", await c.AskAsync("@t STATUS"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectSubscriberHearsThePushAfterAnEdit()
    {
        await using var rig = await HostRig.StartAsync();
        await using var c = await rig.WireAsync();
        Assert.Equal("OK @sub subscribed project", await c.AskAsync("@sub SUBSCRIBE PROJECT"));
        var first = await c.PushAsync(l => l.StartsWith("PROJECT ", StringComparison.Ordinal));
        long Revision(string push)
        {
            using var d = JsonDocument.Parse(push["PROJECT ".Length..]);
            Assert.Equal(JsonValueKind.Object, d.RootElement.GetProperty("project").ValueKind);
            return d.RootElement.GetProperty("revision").GetInt64();
        }
        var before = Revision(first);

        var made = await c.AskAsync("@n PLAYLIST NEW Show");
        Assert.StartsWith("OK @n pl", made, StringComparison.Ordinal);
        var id = made!["OK @n ".Length..];
        var pushed = await c.PushAsync(l => l.StartsWith("PROJECT ", StringComparison.Ordinal) && l.Contains(id, StringComparison.Ordinal));
        Assert.StartsWith("PROJECT {\"revision\":", pushed, StringComparison.Ordinal);
        Assert.True(Revision(pushed) > before);
    }

    [Fact]
    public async Task TheWebPortAnswersStateAndMcpListsItsTools()
    {
        await using var rig = await HostRig.StartAsync();
        using var http = rig.Http();
        using (var state = JsonDocument.Parse(await http.GetStringAsync("/api/state")))
            Assert.Equal(rig.Id, state.RootElement.GetProperty("id").GetString());
        Assert.Contains("\"PAIRING NEW\"", await http.GetStringAsync("/api/verbs"), StringComparison.Ordinal);

        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));
        var tools = await mcp.ListToolsAsync();
        Assert.Equal(["get_context", "get_project", "get_state", "list_verbs", "run_command"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        var got = await mcp.CallToolAsync("get_state");
        Assert.Contains(rig.Id, string.Concat(got.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text)), StringComparison.Ordinal);

        // The assistant on the door is counted in STATE.ai (STATE moves on the next fact; a read now sees it).
        Assert.True(await Eventually.TrueAsync(() => rig.State().GetProperty("ai").GetProperty("clients").GetInt32() >= 1));
    }
}
