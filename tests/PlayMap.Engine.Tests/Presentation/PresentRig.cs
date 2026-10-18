using System.Collections.Frozen;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Presentation;
using PlayMap.Core.Show;
using PlayMap.Core.Sync;
using PlayMap.Core.Wire;
using PlayMap.Engine.Presentation;
using PlayMap.Render.Sync;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>A show gate that allows every line and says whether the show is armed (the armed rules of the lock verbs are theirs).</summary>
internal sealed class ArmedGate : IShowGate
{
    public bool Armed { get; set; }

    public string ArmedBy => Armed ? "Desk" : "";

    public string? BlindOwner => null;

    public int MinGoMs => 0;

    public ShowRole RoleOf(SessionInfo session) => ShowRole.Operator;

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) => new(ShowVerdict.Allow, null);

    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => null;
}

/// <summary>A network view that names this node's sync role.</summary>
internal sealed class RoleNetwork(string role, string? leader = null) : INetworkView
{
    public IReadOnlyList<PeerView> Peers() => [];

    public LinkView? Patterns() => null;

    public LinkView? Stagewatch() => null;

    public string? StagewatchTimerJson() => null;

    public SyncView Sync() => new(role, leader, 0, 0, 0);
}

/// <summary>An output edge scripted by the test: health and presentation come from each output's <see cref="PresentClock"/>, fed by
/// the test (health's dropped is the render host's own rule: the clock's late presents over the budget), and the lock from views
/// the test sets. Every lock verb's wish is recorded.</summary>
internal sealed class ScriptedOutputs : IOutputBackend, IPresentBackend, IFrameLockBackend
{
    public readonly Dictionary<string, PresentClock> Clocks = new(StringComparer.Ordinal);
    public Dictionary<string, LockView> LockViews { get; set; } = new(StringComparer.Ordinal);
    public long NowNs { get; set; } = 100_000_000_000;
    public FrameLockBoard? BoardFacts { get; set; } = new("fake", 8289, SyncBoards.Name(8289), "3.6", true, true, false, 0, false, true, "fake 1.0");
    public List<string> Calls { get; } = [];
    public HashSet<string> Held { get; } = new(StringComparer.Ordinal);

    public string RendererName => "scripted";

    public IReadOnlyList<DisplayInfo> Displays() => [];

    public IReadOnlyList<OutputHealth> Health() =>
        [.. Clocks.Select(c => new OutputHealth(c.Key, true, 60, c.Value.Snapshot(NowNs).LateOverBudget, 16.7, ""))];

    public IReadOnlyList<OutputPresentView> Present() => [.. Clocks.Select(c => new OutputPresentView(c.Key, c.Value.Snapshot(NowNs)))];

    public MachinePresentView MachineView { get; set; } = new("gl", "scripted", "gl", false, "", 0, 0.2, "core330", "glx");

    public MachinePresentView Machine() => MachineView;

    /// <summary>What ConfirmFirstFrames does (null: nothing to confirm).</summary>
    public Func<bool>? FirstFramesUp { get; set; }

    public bool ConfirmFirstFrames() => FirstFramesUp is { } up && up();

    /// <summary>The group's air latency the engine wrote through, in order.</summary>
    public List<double> GroupLatencies { get; } = [];

    public void SetGroupAirLatency(double ms) => GroupLatencies.Add(ms);

    public Task<IReadOnlyList<OutputPresentView>> ResetAsync(IReadOnlyCollection<string>? outputIds, CancellationToken ct)
    {
        var list = new List<OutputPresentView>();
        foreach (var id in outputIds ?? [.. Clocks.Keys])
            list.Add(new OutputPresentView(id, Clocks[id].Reset(NowNs)));
        return Task.FromResult<IReadOnlyList<OutputPresentView>>(list);
    }

    public string Driver => "fake";

    public string DriverWords => "";

    public FrameLockBoard? Board => BoardFacts;

    public LockIntent Intent { get; set; } = LockIntent.None;

    public IReadOnlyCollection<string> HeldOut => Held;

    public IReadOnlyDictionary<string, LockView> Locks() => LockViews.ToFrozenDictionary(StringComparer.Ordinal);

    public string TimeSource => "clock";

    public void Apply(uint swapGroup, uint barrier)
    {
        Calls.Add($"apply {swapGroup} {barrier}");
        Intent = new LockIntent(true, swapGroup, barrier, Intent.Seq + 1);
        foreach (var id in LockViews.Keys.ToList())
            LockViews[id] = LockViews[id] with { State = LockState.Framelocked, Reason = "", Words = "framelocked: swap group 1, barrier 1" };
    }

    public void LeaveAll()
    {
        Calls.Add("leave all");
        Intent = Intent with { Apply = false };
        foreach (var id in LockViews.Keys.ToList())
            LockViews[id] = LockViews[id] with { State = LockState.FreeMeasured, Words = "left the lock: presenting free" };
    }

    public void Leave(IReadOnlyCollection<string> outputIds)
    {
        Calls.Add("leave " + string.Join(",", outputIds));
        foreach (var id in outputIds)
        {
            Held.Add(id);
            LockViews[id] = LockViews[id] with { State = LockState.FreeMeasured };
        }
    }

    public void Rejoin(IReadOnlyCollection<string> outputIds)
    {
        Calls.Add("rejoin " + string.Join(",", outputIds));
        foreach (var id in outputIds)
        {
            Held.Remove(id);
            LockViews[id] = LockViews[id] with { State = LockState.Framelocked, Reason = "", Rejoins = LockViews[id].Rejoins + 1 };
        }
    }

    public Task<IReadOnlyDictionary<string, LockView>> ObserveAsync(IReadOnlyCollection<string> outputIds, Func<LockView, bool> settled,
        TimeSpan within, CancellationToken ct) => Task.FromResult(Locks());

    public LockAnchor? LocalAnchor() => null;

    public void OfferAnchor(LockAnchor anchor) => Calls.Add("anchor " + anchor.Seq);

    /// <summary>A free, measured view for each output, as the render thread would publish before any wish.</summary>
    public ScriptedOutputs WithFreeLocks(params string[] ids)
    {
        foreach (var id in ids)
            LockViews[id] = new LockView(id, LockState.FreeMeasured, "", "", 0, 0);
        return this;
    }
}

/// <summary>An engine over scripted outputs, with the helpers the presentation tests read STATE and run lines with.</summary>
internal sealed class PresentRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private PresentRig(EngineCore engine, ScriptedOutputs outputs, ArmedGate gate)
    {
        Engine = engine;
        Outputs = outputs;
        Gate = gate;
    }

    public EngineCore Engine { get; }
    public ScriptedOutputs Outputs { get; }
    public ArmedGate Gate { get; }

    public static PresentRig Start(Project project, ScriptedOutputs? outputs = null, INetworkView? network = null, bool scripted = true,
        Func<NodeSettings, NodeSettings>? settings = null)
    {
        outputs ??= new ScriptedOutputs();
        var gate = new ArmedGate();
        var services = EngineServices.ForTests() with { ShowGate = gate };
        if (settings is not null)
            services = services with { Settings = settings(services.Settings) };
        if (scripted)
            services = services with { Outputs = outputs };
        if (network is not null)
            services = services with { Network = network };
        return new PresentRig(new EngineCore(services, project), outputs, gate);
    }

    public async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    public JsonElement State()
    {
        Engine.FactsChanged();
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.Clone();
    }

    /// <summary>A display output (a virtual one is never lit), at a refresh.</summary>
    public static Output Screen(string id, double hz = 60) => Rendering.HostRig.Screen(id, SourceRef.Solid("#336699"), 64, 36, OutputKind.Display) with
    {
        RefreshHz = hz,
        Name = id.ToUpperInvariant(),
    };

    public static Project With(params Output[] outputs) => Rendering.HostRig.WithOutputs(outputs);

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
