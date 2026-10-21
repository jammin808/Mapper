using System.Text.Json;
using PlayMap.Core.Machine;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests.Machine;

/// <summary>The machine's desk steps proposed by the assistant (the card's MACHINE LOCK and MACHINE SET lines, preflight's fix lines)
/// go through the same gate as everyone's: while the show is armed the Setup class refuses both, neither run nor queued; disarmed, a
/// review line runs on the assistant's word only where the settings let it, and GPU-PRIORITY REALTIME (high risk) always waits for a
/// person; approved on a node whose account holds the privilege, both read back from STATE (machine.locked,
/// machine.platform.settings.gpuPriority).</summary>
public sealed class AssistantMachineTests
{
    /// <summary>A node's machine part as the wire sees it: MACHINE LOCK keeps the driver set, MACHINE SET GPU-PRIORITY claims the class
    /// (REALTIME refused without the privilege, naming it), and STATE carries both; it routes the machine's rows, and its Show Mode is the
    /// node's own policy, armed or not.</summary>
    private sealed class MachineNode : IWireEndpoint, IShowDecisions, IVerbAvailability
    {
        private readonly FakeEndpoint _project = new();
        private string _gpuPriority = "normal";
        private bool _locked;

        public bool Armed { get; set; }

        public bool Privileged { get; set; }

        public List<string> Handled { get; } = [];

        public bool IsAvailable(VerbSpec spec) => spec.Name.StartsWith("MACHINE", StringComparison.Ordinal) || VerbAvailability.RoundOne.IsAvailable(spec);

        public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) =>
            ShowPolicy.Decide(spec, line, ShowRole.Assistant, new ShowFacts(Armed, false, false, false, "anyone"));

        public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct)
        {
            Handled.Add(line.Raw);
            var verb = VerbCatalog.Match(line)?.Spec.Name;
            CommandReply reply;
            if (verb == "MACHINE LOCK")
            {
                _locked = true;
                reply = CommandReply.Success("{\"locked\":true}", "this machine's driver set is locked");
            }
            else if (verb == "MACHINE SET" && line.WordUpper(2) == "GPU-PRIORITY" && line.WordUpper(3) == "REALTIME")
            {
                if (Privileged)
                    _gpuPriority = "realtime";
                reply = Privileged ? CommandReply.Success("{\"asked\":\"realtime\"}", "the GPU priority is realtime now")
                    : CommandReply.Fail(PlatformPolicy.RealtimeWithoutPrivilegeWords);
            }
            else
            {
                reply = CommandReply.Fail("not this node's line");
            }
            StateChanged?.Invoke(1);
            return ValueTask.FromResult(reply);
        }

        public (string Json, long Revision) State()
        {
            var locked = _locked ? "{\"at\":1760600000000,\"drift\":[]}" : "null";
            return ("{\"v\":1,\"machine\":{\"platform\":{\"settings\":{\"gpuPriority\":\"" + _gpuPriority + "\"}},\"locked\":" + locked + "}}", 1);
        }

        public string Context() => SampleShow.ContextJson;

        public (string Json, long Revision) ProjectJson() => (PlayMap.Core.Model.ProjectJson.Serialize(SampleShow.Project()), 1);

        public event Action<long>? StateChanged;

        /// <summary>The project's changes are the sample show's node's (none here).</summary>
        public event Action<long>? ProjectChanged
        {
            add => _project.ProjectChanged += value;
            remove => _project.ProjectChanged -= value;
        }
    }

    private const string Lock = "MACHINE LOCK";
    private const string Realtime = "MACHINE SET GPU-PRIORITY REALTIME";

    private static readonly AssistantSettings RunsReview = AssistantSettings.Default with { Enabled = true, RunReviewWithoutAsking = true };

    private static ScriptedProvider Proposing() => new ScriptedProvider()
        .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "hold this machine's drivers and give the GPU the realtime class", Lock, Realtime)))
        .Then(ScriptedProvider.Say("Done what the gate allows."));

    private static string Result(ScriptedProvider provider) =>
        provider.Requests[1].Messages[^1].Parts.OfType<ToolResultPart>().Single(r => r.ToolCallId == "c1").Content;

    private static JsonElement Machine(MachineNode node)
    {
        using var state = JsonDocument.Parse(node.State().Json);
        return state.RootElement.GetProperty("machine").Clone();
    }

    [Fact]
    public async Task WhileArmedTheSetupClassRefusesBothAndNothingRunsOrWaits()
    {
        var node = new MachineNode { Armed = true, Privileged = true };
        var provider = Proposing();
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, RunsReview, new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("lock the drivers and raise the GPU class");

        Assert.Empty(node.Handled);
        Assert.Empty(queue.All);
        var text = Result(provider);
        Assert.Contains("\"status\":\"refused\"", text, StringComparison.Ordinal);
        Assert.Contains("Not while armed: MACHINE LOCK", text, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"not_run\"", text, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Machine(node).GetProperty("locked").ValueKind);
    }

    [Fact]
    public async Task DisarmedTheReviewLineRunsWhereTheSettingsLetItAndRealtimeWaitsForAPerson()
    {
        var node = new MachineNode { Privileged = true };
        var provider = Proposing();
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, RunsReview, new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("lock the drivers and raise the GPU class");

        // MACHINE LOCK (review) ran on the assistant's word; REALTIME (high risk) waits for a person whatever the settings say.
        Assert.Equal([Lock], node.Handled);
        var waiting = Assert.Single(queue.All);
        Assert.Equal((Realtime, VerbRisk.High, ProposalState.Pending), (waiting.Line, waiting.Risk, waiting.State));
        Assert.Contains("\"status\":\"queued\"", Result(provider), StringComparison.Ordinal);
        Assert.Equal("normal", Machine(node).GetProperty("platform").GetProperty("settings").GetProperty("gpuPriority").GetString());

        // A person approves it on a node whose account holds the privilege: the class reads back from STATE.
        var done = await queue.ApproveAsync(waiting.Id, "Sam", CancellationToken.None);
        Assert.Equal(ProposalState.Done, done.State);
        Assert.Equal([Lock, Realtime], node.Handled);
        var machine = Machine(node);
        Assert.Equal("realtime", machine.GetProperty("platform").GetProperty("settings").GetProperty("gpuPriority").GetString());
        Assert.Equal(JsonValueKind.Object, machine.GetProperty("locked").ValueKind);
    }

    [Fact]
    public async Task WithReviewLinesAskingBothWaitAndApprovedWithoutThePrivilegeRealtimeIsRefusedNamingIt()
    {
        var node = new MachineNode();
        var provider = Proposing();
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, AssistantSettings.Default with { Enabled = true },
            new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("lock the drivers and raise the GPU class");

        Assert.Empty(node.Handled);
        Assert.Equal([(Lock, VerbRisk.Review), (Realtime, VerbRisk.High)], queue.All.Select(p => (p.Line, p.Risk)));
        var locked = await queue.ApproveAsync(queue.All[0].Id, "Sam", CancellationToken.None);
        Assert.Equal(ProposalState.Done, locked.State);
        Assert.Equal(JsonValueKind.Object, Machine(node).GetProperty("locked").ValueKind);
        var refused = await queue.ApproveAsync(queue.All[1].Id, "Sam", CancellationToken.None);
        Assert.Equal(ProposalState.Failed, refused.State);
        Assert.Contains("SeIncreaseBasePriorityPrivilege", refused.ResultWords, StringComparison.Ordinal);
        Assert.Equal("normal", Machine(node).GetProperty("platform").GetProperty("settings").GetProperty("gpuPriority").GetString());
    }
}
