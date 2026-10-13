using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Wire;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Assistant;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine.Tests;

/// <summary>The node's assistant through the engine's verbs on a scripted model: a high-risk line it proposes waits
/// for a person, APPROVE runs it through the node's own path and answers the node's reply, REJECT leaves the node as
/// it was; an assistant never approves, never asks itself and never handles a secret; and no secret the node holds
/// reaches STATE, the context, the journal or a reply.</summary>
public sealed class AssistantEngineTests
{
    [Fact]
    public async Task APersonReadsAndSetsTheAssistantsSettingsWhichAreKeptAndTakeHoldAtOnce()
    {
        var model = new ScriptedModel()
            .Then(ScriptedModel.Propose("c1", "name the output", "SET op_1 name \"Stage left\""))
            .Then(ScriptedModel.Say("Renaming waits for you."))
            .Then(ScriptedModel.Propose("c2", "name the output", "SET op_1 name \"Stage right\""))
            .Then(ScriptedModel.Say("Renamed."));
        var rig = new OpsRig(model: model);
        try
        {
            var status = JsonDocument.Parse(await rig.Ok("ASSISTANT STATUS")).RootElement;
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.Equal("claude", status.GetProperty("provider").GetString());
            Assert.Equal("medium", status.GetProperty("effort").GetString());
            Assert.False(status.GetProperty("runReviewWithoutAsking").GetBoolean());
            Assert.False(status.GetProperty("keySet").GetBoolean());
            await rig.Ok("CREDENTIAL SET assistant sk-ant-test-key-0123456789");
            Assert.True(JsonDocument.Parse(await rig.Ok("ASSISTANT STATUS")).RootElement.GetProperty("keySet").GetBoolean());
            Assert.True(rig.State().GetProperty("ai").GetProperty("assistant").GetProperty("keySet").GetBoolean());

            // A review line waits for a person while the switch is off …
            var first = JsonDocument.Parse(await rig.Ok("ASK name output one stage left")).RootElement;
            Assert.Equal("queued", first.GetProperty("lines")[0].GetProperty("status").GetString());
            Assert.Equal("Output 1", rig.Engine.Store.Project.FindOutput("op_1")!.Name);

            // … and runs on the assistant's word once a person turns it on: kept in the node's file, in force at once.
            var set = await rig.Run("ASSISTANT SET runReviewWithoutAsking on");
            Assert.True(set.Ok, set.Payload);
            Assert.Equal("review lines run on the assistant's word", set.Effect);
            Assert.True(AssistantSettingsFile.Load(rig.Home).RunReviewWithoutAsking);
            Assert.True(rig.State().GetProperty("ai").GetProperty("assistant").GetProperty("runReviewWithoutAsking").GetBoolean());
            var second = JsonDocument.Parse(await rig.Ok("ASK name output one stage right")).RootElement;
            Assert.Equal("ran", second.GetProperty("lines")[0].GetProperty("status").GetString());
            Assert.Equal("Stage right", rig.Engine.Store.Project.FindOutput("op_1")!.Name);

            // Only a person here or at the paired desk sets them; a value that changes nothing, a field or value the
            // verb does not know, are refused with the reason.
            Assert.Contains("this machine or its paired desk", (await rig.Run("ASSISTANT SET model other", OpsRig.Remote)).Payload, StringComparison.Ordinal);
            Assert.Contains("never changes its own settings", (await rig.Run("ASSISTANT SET runLowWithoutAsking off", OpsRig.Mcp)).Payload, StringComparison.Ordinal);
            Assert.StartsWith("already so", (await rig.Run("ASSISTANT SET runReviewWithoutAsking on")).Payload, StringComparison.Ordinal);
            Assert.False((await rig.Run("ASSISTANT SET temperature 2")).Ok);
            Assert.Contains("low, medium, high, xhigh or max", (await rig.Run("ASSISTANT SET effort extreme")).Payload, StringComparison.Ordinal);
            await rig.Ok("ASSISTANT SET effort high");
            await rig.Ok("ASSISTANT SET model claude-test-model");
            var kept = AssistantSettingsFile.Load(rig.Home);
            Assert.Equal((AssistantEffort.High, "claude-test-model"), (kept.Effort, kept.Model));

            await rig.Ok("ASSISTANT SET enabled off");
            var off = await rig.Run("ASK anything");
            Assert.False(off.Ok);
            Assert.Contains("the assistant is off", off.Payload, StringComparison.Ordinal);
            Assert.False(AssistantSettingsFile.Load(rig.Home).Enabled);

            // Turning it on or changing who answers is high risk; the rest is review. The key is never shown.
            Assert.True(WireGrammar.TryParse("ASSISTANT SET enabled on", out var enable, out _));
            Assert.True(WireGrammar.TryParse("ASSISTANT SET model x", out var name, out _));
            Assert.Equal((VerbRisk.High, VerbRisk.Review), (VerbCatalog.RiskOf(enable), VerbCatalog.RiskOf(name)));
            Assert.DoesNotContain("sk-ant-test-key", rig.Engine.State().Json + await rig.Ok("ASSISTANT STATUS") + File.ReadAllText(AssistantSettingsFile.PathIn(rig.Home)), StringComparison.Ordinal);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task AProposedLinkNeverShowsItsTokenYetItsApprovalRunsItWithTheToken()
    {
        var links = new LinkSpy();
        var rig = new OpsRig(network: links);
        try
        {
            // The words a person sees are the verb's own redaction, not a guess at a key's shape: a short key is hidden too.
            var patterns = await rig.Assistant.Queue.EnqueueAsync(new ProposalDraft("LINK PATTERNS host secret-token", VerbCatalog.Find("LINK PATTERNS")!,
                "follow the desk", "an assistant over MCP", "mcp", "mcp/1", 0), CancellationToken.None);
            var stagewatch = await rig.Assistant.Queue.EnqueueAsync(new ProposalDraft("LINK STAGEWATCH host k3y-9q", VerbCatalog.Find("LINK STAGEWATCH")!,
                "follow the timer", "an assistant over MCP", "mcp", "mcp/2", 0), CancellationToken.None);
            var listed = JsonDocument.Parse(await rig.Ok("PROPOSALS")).RootElement;
            Assert.Equal("LINK PATTERNS host " + VerbCatalog.SecretMarker, listed[0].GetProperty("line").GetString());
            Assert.Equal("LINK STAGEWATCH host " + VerbCatalog.SecretMarker, listed[1].GetProperty("line").GetString());
            Assert.Equal(2, rig.State().GetProperty("ai").GetProperty("proposals").GetInt32());

            var approved = await rig.Run("APPROVE " + patterns.Id);
            Assert.True(approved.Ok, approved.Payload);
            await rig.Ok("APPROVE " + stagewatch.Id);
            Assert.True(await OpsRig.Eventually(() => links.Patterns.Count == 1 && links.Stagewatch.Count == 1), approved.Payload);
            Assert.Equal(("host", "secret-token"), Assert.Single(links.Patterns));
            Assert.Equal(("host", "k3y-9q"), Assert.Single(links.Stagewatch));
            Assert.Equal(ProposalState.Done, rig.Assistant.Queue.Find(patterns.Id)!.State);

            // A shown copy never runs in place of the line it stands for.
            var copy = await rig.Run("LINK PATTERNS host " + VerbCatalog.SecretMarker);
            Assert.False(copy.Ok);
            Assert.Contains("shown copy", copy.Payload, StringComparison.Ordinal);
            Assert.Single(links.Patterns);

            var seen = string.Join("\n", await rig.Ok("PROPOSALS ALL"), approved.Payload, approved.Effect, rig.Engine.State().Json, rig.Engine.Context(),
                string.Join("\n", rig.Engine.Journal.Recent(200).Select(r => r.Text + " " + r.Line)));
            await rig.DisposeAsync();
            seen += rig.JournalText();
            Assert.Contains(VerbCatalog.SecretMarker, seen, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token", seen, StringComparison.Ordinal);
            Assert.DoesNotContain("k3y-9q", seen, StringComparison.Ordinal);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

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
