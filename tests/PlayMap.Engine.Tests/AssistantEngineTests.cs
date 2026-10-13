using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Wire;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine.Tests;

/// <summary>The node's assistant through the engine's verbs on a scripted model: a high-risk line it proposes waits
/// for a person, APPROVE runs it through the node's own path and answers the node's reply, REJECT leaves the node as
/// it was; an assistant never approves, never asks itself and never handles a secret; and no secret the node holds
/// reaches STATE, the context, the journal or a reply.</summary>
public sealed class AssistantEngineTests
{
    [Fact]
    public async Task AnAskedHighRiskLineWaitsApproveRunsItAndRejectLeavesTheNodeAsItWas()
    {
        var model = new ScriptedModel()
            .Then(ScriptedModel.Propose("c1", "the operator asked for black", "BLACKOUT ON"))
            .Then(ScriptedModel.Say("The blackout waits for your approval."))
            .Then(ScriptedModel.Propose("c2", "the operator asked for the output off", "OUTPUT ENABLE op_1 OFF"))
            .Then(ScriptedModel.Say("Turning output 1 off waits for your approval."));
        var rig = new OpsRig(model: model);
        try
        {
            var asked = await rig.Run("ASK black everything out please");
            Assert.True(asked.Ok, asked.Payload);
            var answer = JsonDocument.Parse(asked.Payload!).RootElement;
            Assert.Equal("answered", answer.GetProperty("outcome").GetString());
            Assert.Equal("The blackout waits for your approval.", answer.GetProperty("text").GetString());
            var queued = answer.GetProperty("queued")[0].GetString()!;
            Assert.Equal("queued", answer.GetProperty("lines")[0].GetProperty("status").GetString());
            Assert.Equal("high", answer.GetProperty("lines")[0].GetProperty("risk").GetString());
            Assert.False(rig.Engine.Air.Blackout);
            Assert.Equal(1, rig.Assistant.Waiting);

            var pending = JsonDocument.Parse(await rig.Ok("PROPOSALS")).RootElement;
            Assert.Equal(queued, Assert.Single(pending.EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal("pending", pending[0].GetProperty("state").GetString());
            Assert.False(pending[0].GetProperty("preTicked").GetBoolean());
            var item = rig.Context().GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "assistant");
            Assert.Equal("amber", item.GetProperty("light").GetString());

            // An assistant's line, an MCP client's or an approval's own never approves.
            foreach (var who in new[] { OpsRig.Mcp, OpsRig.Desk with { Transport = "assistant" }, OpsRig.Desk with { Transport = "approval" } })
                Assert.False((await rig.Run("APPROVE " + queued, who)).Ok);
            Assert.False(rig.Engine.Air.Blackout);
            Assert.False((await rig.Run("ASK hello", OpsRig.Desk with { Transport = "assistant" })).Ok);

            var approved = await rig.Run("APPROVE " + queued);
            Assert.True(approved.Ok, approved.Payload);
            Assert.True(rig.Engine.Air.Blackout);
            var done = JsonDocument.Parse(approved.Payload!).RootElement;
            Assert.Equal("done", done.GetProperty("state").GetString());
            Assert.True(done.GetProperty("ok").GetBoolean());
            Assert.Equal("Desk", done.GetProperty("decidedBy").GetString());
            Assert.False((await rig.Run("APPROVE " + queued)).Ok);
            Assert.Contains(rig.Engine.Journal.Recent(50), r => r.Who == "Desk" && r.Text.Contains("every output to black", StringComparison.Ordinal));

            var second = JsonDocument.Parse(await rig.Ok("ASK turn output one off")).RootElement;
            var id2 = second.GetProperty("queued")[0].GetString()!;
            var revision = rig.Engine.Store.Current.Revision;
            var rejected = await rig.Run($"REJECT {id2} not during the show");
            Assert.True(rejected.Ok, rejected.Payload);
            Assert.Equal("rejected", JsonDocument.Parse(rejected.Payload!).RootElement.GetProperty("state").GetString());
            Assert.True(rig.Engine.Store.Project.Outputs[0].Enabled);
            Assert.Equal(revision, rig.Engine.Store.Current.Revision);
            Assert.False((await rig.Run("APPROVE " + id2)).Ok);
            Assert.Equal(0, rig.Assistant.Waiting);
            Assert.Equal(2, JsonDocument.Parse(await rig.Ok("PROPOSALS ALL")).RootElement.GetArrayLength());
            Assert.Empty(JsonDocument.Parse(await rig.Ok("PROPOSALS")).RootElement.EnumerateArray());
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task AnAssistantThatIsOffSaysSoAndMcpHighRiskLinesQueueOnTheSameList()
    {
        var rig = new OpsRig(assistant: new AssistantSettings { Enabled = false });
        try
        {
            var off = await rig.Run("ASK what is playing");
            Assert.False(off.Ok);
            Assert.Contains("off", off.Payload, StringComparison.Ordinal);
            var id = await rig.Assistant.Approvals.ProposeAsync("BLACKOUT ON", "the house lights failed", VerbRisk.High, CancellationToken.None);
            Assert.NotNull(id);
            Assert.Null(await rig.Assistant.Approvals.ProposeAsync("AUTH 0123456789abcdef", "", VerbRisk.High, CancellationToken.None));
            Assert.Equal(1, rig.Assistant.Waiting);
            await rig.Ok("APPROVE " + id);
            Assert.True(rig.Engine.Air.Blackout);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task NoSecretTheNodeHoldsReachesStateTheContextTheJournalOrAReply()
    {
        const string ApiKey = "sk-ant-api03-ZYXWVUTSRQPONMLKJIHGFEDCBA0123456789";
        const string ProjectorSecret = "pj-secret-Hx7Q2";
        await using var projector = new PjLinkSimulator(ProjectorSecret);
        var model = new ScriptedModel()
            .Then(r => ScriptedModel.Say("Your key is " + ApiKey + " and the projector's is " + ProjectorSecret + "."));
        var rig = new OpsRig(model: model);
        var replies = new List<string>();
        try
        {
            async Task<CommandReply> Send(string line, SessionInfo? who = null)
            {
                var r = await rig.Run(line, who);
                replies.Add((r.Payload ?? "") + " " + r.Effect);
                return r;
            }

            var id = (await Send($"DEVICE ADD Box pjlink 127.0.0.1:{projector.Port}")).Payload!;
            // Only a person on this machine hands the node a secret.
            Assert.False((await Send($"CREDENTIAL SET {id} {ProjectorSecret}", OpsRig.Remote)).Ok);
            Assert.False((await Send($"CREDENTIAL SET {id} {ProjectorSecret}", OpsRig.Mcp)).Ok);
            Assert.False((await Send($"CREDENTIAL SET {id} {ProjectorSecret}", OpsRig.Desk with { Paired = false })).Ok);
            Assert.Null(rig.Credentials.Find(id));
            Assert.True((await Send($"CREDENTIAL SET {id} {ProjectorSecret}")).Ok);
            Assert.True((await Send($"CREDENTIAL SET {NodeCredentialStore.AssistantKey} {ApiKey}")).Ok);
            Assert.True((await Send($"DEVICE POWER {id} ON")).Ok);
            Assert.Equal("1", projector.Power);

            var asked = await Send("ASK what are the keys");
            Assert.True(asked.Ok, asked.Payload);
            Assert.Equal(ApiKey, model.KeySeen);
            Assert.Contains(SecretRedactor.Mask, asked.Payload, StringComparison.Ordinal);
            var list = await Send("CREDENTIAL LIST");
            Assert.Contains(id, list.Payload, StringComparison.Ordinal);
            await Send($"DEVICE GET {id}");
            await Send("DEVICE LIST");
            await Send("PROPOSALS ALL");

            var state = rig.Engine.State().Json;
            var context = rig.Engine.Context();
            var journal = string.Join("\n", rig.Engine.Journal.Recent(500).Select(r => $"{r.Text} {r.Line} {r.Who}"));
            Assert.Contains("\"credential\":true", state, StringComparison.Ordinal);
            await rig.DisposeAsync();
            var file = rig.JournalText();
            var stored = File.ReadAllBytes(Path.Combine(rig.Folder, "keys", NodeCredentialStore.FileName));
            foreach (var secret in new[] { ApiKey, ProjectorSecret })
            {
                Assert.DoesNotContain(secret, state, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, context, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, journal, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, file, StringComparison.Ordinal);
                Assert.All(replies, r => Assert.DoesNotContain(secret, r, StringComparison.Ordinal));
            }
            Assert.Contains("a secret was set for", file, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(rig.Folder, "keys", NodeCredentialStore.FileName)));
            Assert.NotEmpty(stored);

            // The store reads back: the secret survives a restart of the node.
            var again = new NodeCredentialStore(Path.Combine(rig.Folder, "keys"));
            Assert.Equal(ProjectorSecret, again.Find(id)!.Secret);
            Assert.Equal("credential store", again.ToString());
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
