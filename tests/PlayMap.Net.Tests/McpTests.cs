using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlayMap.Core.Wire;
using PlayMap.Net.Http;

namespace PlayMap.Net.Tests;

/// <summary>MCP at /mcp through the SDK's own client over HTTP: the tools and resources are listed, a low-risk verb
/// runs with its receipt, a high-risk one is refused (nothing runs) unless the node's "AI may act" switch allows it
/// or a queue takes it, and the token fences the endpoint like every other route.</summary>
public class McpTests
{
    private static async Task<McpClient> ConnectAsync(HttpHost host, string? token = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://127.0.0.1:" + host.Port + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (token is not null)
            options.AdditionalHeaders = new Dictionary<string, string> { ["X-PlayMap-Token"] = token };
        return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: CancellationToken.None);
    }

    private static string TextOf(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task TheToolsAndResourcesAreListedAndALowRiskVerbRuns()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true));
        await using var client = await ConnectAsync(host);

        var tools = await client.ListToolsAsync();
        Assert.Equal(["get_context", "get_project", "get_state", "list_verbs", "run_command"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));

        var state = await client.CallToolAsync("get_state");
        Assert.StartsWith("{\"v\":1,\"seq\":", TextOf(state), StringComparison.Ordinal);

        var run = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "PLAYLIST NEW Show", ["reason"] = "the operator asked for a playlist" });
        Assert.NotEqual(true, run.IsError);
        using (var receipt = JsonDocument.Parse(TextOf(run)))
        {
            Assert.True(receipt.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("applied", receipt.RootElement.GetProperty("stage").GetString());
            Assert.Equal("pl_1", receipt.RootElement.GetProperty("refs").GetProperty("reply").GetString());
        }
        var (line, session) = Assert.Single(endpoint.Lines);
        Assert.Equal("PLAYLIST", line.Verb);
        Assert.Equal("mcp", session.Transport);

        var resources = await client.ListResourcesAsync();
        Assert.Contains("playmap://context", resources.Select(r => r.Uri));
        var read = await client.ReadResourceAsync("playmap://state");
        Assert.StartsWith("{\"v\":1", Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHighRiskVerbIsRefusedUnlessTheNodeLetsAssistantsAct()
    {
        var endpoint = new FakeEndpoint();
        var aiMayAct = false;
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true),
            new HttpHostOptions { Approvals = new SwitchApprovalPolicy(() => aiMayAct) });
        await using var client = await ConnectAsync(host);
        var args = new Dictionary<string, object?> { ["line"] = "BLACKOUT ON", ["reason"] = "the projector shows the wrong scene" };

        var refused = await client.CallToolAsync("run_command", args);
        Assert.True(refused.IsError);
        Assert.Contains("BLACKOUT is high risk", TextOf(refused), StringComparison.Ordinal);
        Assert.False(endpoint.Blackout);
        Assert.Empty(endpoint.Lines);

        var unknown = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "FROBNICATE", ["reason"] = "?" });
        Assert.True(unknown.IsError);
        Assert.Empty(endpoint.Lines);

        aiMayAct = true;
        var ran = await client.CallToolAsync("run_command", args);
        Assert.NotEqual(true, ran.IsError);
        Assert.True(endpoint.Blackout);
    }

    [Fact]
    public async Task AHighRiskVerbGoesToTheApprovalQueueWhenThereIsOne()
    {
        var endpoint = new FakeEndpoint();
        var queue = new FakeQueue();
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true),
            new HttpHostOptions { Approvals = new SwitchApprovalPolicy(() => false, queue) });
        await using var client = await ConnectAsync(host);
        var queued = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "VIDEO PLAY", ["reason"] = "the show starts" });
        Assert.NotEqual(true, queued.IsError);
        using var doc = JsonDocument.Parse(TextOf(queued));
        Assert.Equal("queued", doc.RootElement.GetProperty("stage").GetString());
        Assert.Equal("p1", doc.RootElement.GetProperty("proposal").GetString());
        Assert.Equal(("VIDEO PLAY", VerbRisk.High), Assert.Single(queue.Proposed));
        Assert.Empty(endpoint.Lines);
    }

    [Fact]
    public async Task APathQuotedWithSingleBackslashesIsRefusedWithItsSpellingAndNeverQueuedOrRun()
    {
        // The node's door would refuse the line, so it never waits for a person; the model is told the spelling that reads as typed,
        // whether "AI may act" is off or on, and for a binding that carries such a path too.
        var endpoint = new FakeEndpoint();
        var queue = new FakeQueue();
        var aiMayAct = false;
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: true),
            new HttpHostOptions { Approvals = new SwitchApprovalPolicy(() => aiMayAct, queue) });
        await using var client = await ConnectAsync(host);
        const string Refusal = @"PROJECT OPEN: inside quotes a backslash escapes the mark after it, so ""C:\shows\a.playmap"" reads as C:showsa.playmap — write the path bare, C:\shows\a.playmap, or with / in place of each \";

        foreach (var (line, mayAct) in new[]
        {
            (@"PROJECT OPEN ""C:\shows\a.playmap""", false),
            (@"PROJECT OPEN ""C:\shows\a.playmap""", true),
            (@"BIND ADD MIDI NOTE 1 60 TO PROJECT OPEN ""C:\shows\a.playmap""", false),
        })
        {
            aiMayAct = mayAct;
            var refused = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = line, ["reason"] = "the operator's show file" });
            Assert.True(refused.IsError, line);
            Assert.Equal(Refusal, TextOf(refused));
        }
        Assert.Empty(queue.Proposed);
        Assert.Empty(endpoint.Lines);
    }

    [Fact]
    public async Task AQueuedLineIsAnsweredWithoutItsSecretWordsButQueuedWhole()
    {
        var queue = new FakeQueue();
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: true),
            new HttpHostOptions { Approvals = new SwitchApprovalPolicy(() => false, queue) });
        await using var client = await ConnectAsync(host);
        var queued = await client.CallToolAsync("run_command", new Dictionary<string, object?> { ["line"] = "CREDENTIAL SET pj1 admin hunter2", ["reason"] = "the box" });
        var text = TextOf(queued);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("CREDENTIAL SET pj1 " + VerbCatalog.SecretMarker, doc.RootElement.GetProperty("line").GetString());
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        // The queue holds the line itself: an approval runs what was asked, not what was shown.
        Assert.Equal("CREDENTIAL SET pj1 admin hunter2", Assert.Single(queue.Proposed).Line);
    }

    [Fact]
    public async Task TheTokenFencesMcpLikeEveryOtherRoute()
    {
        await using var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: false));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host));
        await using var paired = await ConnectAsync(host, TestSettings.Token);
        Assert.NotEmpty(await paired.ListToolsAsync());
    }

    private sealed class FakeQueue : PlayMap.Net.IProposalSink
    {
        public List<(string Line, VerbRisk Risk)> Proposed { get; } = [];

        public ValueTask<string?> ProposeAsync(string line, string reason, VerbRisk risk, CancellationToken ct)
        {
            Proposed.Add((line, risk));
            return ValueTask.FromResult<string?>("p" + Proposed.Count);
        }
    }
}
