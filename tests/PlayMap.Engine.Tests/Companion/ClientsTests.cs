using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Companion;
using PlayMap.Net.Companion;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>The controllers the node knows: HELLO adds one to STATE.clients and the context (green paired, amber connected but not
/// paired), PRESENCE says what it shows (known keys only, after HELLO), WHOAMI answers the session as the node sees it — its role and
/// what it may run while armed — and preflight's controllers rows read the same table.</summary>
public sealed class ClientsTests
{
    private static JsonElement ClientOf(JsonElement state, string session) =>
        state.GetProperty("clients").EnumerateArray().Single(c => c.GetProperty("session").GetString() == session);

    [Fact]
    public async Task HelloPresenceAndGoneMoveStateAndTheContext()
    {
        await using var rig = new CompanionRig();
        var registry = (IClientRegistry)rig.Engine;
        var before = rig.Engine.StateKey();
        registry.Hello(CompanionRig.Paired, "ws");
        Assert.NotEqual(before, rig.Engine.StateKey());

        var c = ClientOf(rig.State(), "w1");
        Assert.Equal("Companion:FOH deck", c.GetProperty("name").GetString());
        Assert.Equal("companion", c.GetProperty("kind").GetString());
        Assert.Equal("companion", c.GetProperty("transport").GetString());
        Assert.Equal("ws", c.GetProperty("channel").GetString());
        Assert.Equal("10.0.0.7", c.GetProperty("address").GetString());
        Assert.True(c.GetProperty("paired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("module").ValueKind);
        Assert.Equal(rig.Engine.Services.Wall.UnixMs, c.GetProperty("since").GetInt64());

        Assert.Equal("", await rig.Ok("PRESENCE {\"kind\":\"companion\",\"module\":\"1.0.0\",\"live\":\"projectors\",\"page\":\"1/3\",\"recording\":false,\"thumbs\":12}"));
        c = ClientOf(rig.State(), "w1");
        Assert.Equal("1.0.0", c.GetProperty("module").GetString());
        Assert.Equal("Live · projectors 1/3", c.GetProperty("where").GetString());
        Assert.Equal(12, c.GetProperty("thumbs").GetInt32());

        var item = CompanionRig.Json(rig.Engine.Context()).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == "client:w1");
        Assert.Equal("deck", item.GetProperty("kind").GetString());
        Assert.Equal("green", item.GetProperty("light").GetString());
        Assert.Equal("module 1.0.0 · Live · projectors 1/3", item.GetProperty("state").GetString());

        var unknown = await rig.Run("PRESENCE {\"kind\":\"companion\",\"token\":\"x\"}");
        Assert.False(unknown.Ok);
        Assert.Contains("'token'", unknown.Payload, StringComparison.Ordinal);
        Assert.False((await rig.Run("PRESENCE [1,2]")).Ok);
        var stranger = await rig.Run("PRESENCE {\"kind\":\"companion\"}", CompanionRig.Paired with { SessionId = "w9" });
        Assert.False(stranger.Ok);
        Assert.Contains("HELLO", stranger.Payload, StringComparison.Ordinal);
        Assert.False((await rig.Run("PRESENCE {\"kind\":\"companion\"}", CompanionRig.Paired with { Paired = false })).Ok);

        registry.Gone("w1");
        Assert.Equal(0, rig.State().GetProperty("clients").GetArrayLength());
    }

    [Fact]
    public async Task ANameSaidBeforePairingIsKeptShortInStateAndTheContext()
    {
        await using var rig = new CompanionRig();
        var registry = (IClientRegistry)rig.Engine;
        registry.Hello(CompanionRig.Paired with { SessionId = "l9", ClientName = "Companion:" + new string('x', 60 * 1024), Paired = false }, "ws");
        var name = ClientOf(rig.State(), "l9").GetProperty("name").GetString()!;
        Assert.Equal(ClientTable.MaxNameChars + 1, name.Length);
        Assert.StartsWith("Companion:xxx", name, StringComparison.Ordinal);
        Assert.EndsWith("…", name, StringComparison.Ordinal);
        var item = CompanionRig.Json(rig.Engine.Context()).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "client:l9");
        Assert.Equal(name, item.GetProperty("label").GetString());
    }

    [Fact]
    public async Task AnUnpairedCompanionIsAmberInTheContextAndInPreflight()
    {
        await using var rig = new CompanionRig();
        var registry = (IClientRegistry)rig.Engine;
        var check = new CompanionPreflight();
        var context = new PreflightContext(rig.Engine, PreflightDepth.Quick, Armed: false);

        var none = Assert.Single(await check.RunAsync(context, CancellationToken.None));
        Assert.Equal(Light.Grey, none.Light);

        // The fault: a Companion that connected without the node's token.
        registry.Hello(CompanionRig.Paired with { SessionId = "l4", Paired = false }, "ws");
        var amber = Assert.Single(await check.RunAsync(context, CancellationToken.None));
        Assert.Equal(Light.Amber, amber.Light);
        Assert.Equal("not paired", amber.Value);
        Assert.Equal("controllers", amber.Section);
        Assert.Contains("pairing token", amber.Note, StringComparison.Ordinal);
        var item = CompanionRig.Json(rig.Engine.Context()).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "client:l4");
        Assert.Equal("amber", item.GetProperty("light").GetString());
        Assert.Equal("connected, not paired", item.GetProperty("state").GetString());

        // Paired since: green.
        registry.Hello(CompanionRig.Paired with { SessionId = "l4" }, "ws");
        var green = Assert.Single(await check.RunAsync(context, CancellationToken.None));
        Assert.Equal(Light.Green, green.Light);

        // A responder that could not open is red with its words.
        var red = new CompanionPreflight(() => [new NodeFault("mdns", "the network announcement (mDNS) could not open 127.2.0.1:5353")]);
        Assert.Contains(await red.RunAsync(context, CancellationToken.None), r => r.Light == Light.Red && r.Note.Contains("mDNS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhoAmIAnswersTheSessionAsTheNodeSeesIt()
    {
        await using (var open = new CompanionRig())
        {
            ((IClientRegistry)open.Engine).Hello(CompanionRig.Paired, "ws");
            var me = CompanionRig.Json(await open.Ok("WHOAMI"));
            Assert.Equal("w1", me.GetProperty("session").GetString());
            Assert.Equal("companion", me.GetProperty("transport").GetString());
            Assert.Equal("ws", me.GetProperty("channel").GetString());
            Assert.Equal("Companion:FOH deck", me.GetProperty("client").GetString());
            Assert.True(me.GetProperty("paired").GetBoolean());
            Assert.Equal("remote", me.GetProperty("role").GetString());
            Assert.False(me.GetProperty("armed").GetBoolean());
            Assert.Equal("*", me.GetProperty("may").GetString());
            Assert.Equal(1, me.GetProperty("proto").GetInt32());
        }

        await using var armed = new CompanionRig(s => s with { ShowGate = new ArmedGate("PLAYLIST GO", "BLACKOUT", "PANIC") });
        var who = CompanionRig.Json(await armed.Ok("WHOAMI"));
        Assert.True(who.GetProperty("armed").GetBoolean());
        Assert.Equal(["BLACKOUT", "PANIC", "PLAYLIST GO"], who.GetProperty("may").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(300, who.GetProperty("minGoMs").GetInt32());
        Assert.Equal("tcp", who.GetProperty("channel").GetString());
        var desk = CompanionRig.Json(await armed.Ok("WHOAMI", SessionInfo.Desk("d1")));
        Assert.Equal("operator", desk.GetProperty("role").GetString());
        Assert.Equal("*", desk.GetProperty("may").GetString());

        // The node's own Show Mode: armed from the desk, WHOAMI says so to a controller.
        await using var real = new CompanionRig();
        await real.Ok("SHOW ARM", SessionInfo.Desk("d1"));
        var after = CompanionRig.Json(await real.Ok("WHOAMI"));
        Assert.True(after.GetProperty("armed").GetBoolean());
        Assert.Equal("remote", after.GetProperty("role").GetString());
    }

    [Fact]
    public async Task McpAsksTheNodesShowMode()
    {
        await using var rig = new CompanionRig();
        await rig.Ok("SHOW ARM", PlayMap.Core.Wire.SessionInfo.Desk("d1"));
        var decisions = (Core.Show.IShowDecisions)rig.Engine;
        Assert.True(WireGrammar.TryParse("BLACKOUT ON", out var line, out _));
        var mcp = new SessionInfo("m1", "mcp", "127.0.0.1", true, "MCP", true);
        var d = decisions.Decide(VerbCatalog.Find("BLACKOUT")!, line, mcp);
        Assert.NotEqual(Core.Show.ShowVerdict.Allow, d.Verdict);
        Assert.False(string.IsNullOrEmpty(d.Words));
    }
}
