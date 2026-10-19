using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Machine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>A machine platform a test sets: its identity, whether the account holds the realtime privilege, its own facts, and claims
/// that apply (or, for the claims named in <see cref="Refuse"/>, are refused with words) and read back as asked.</summary>
internal sealed class FakePlatform : IMachinePlatform
{
    public NodeIdentity Ident { get; set; } = MachineRig.Identity("582.16");

    public bool Privileged { get; set; }

    public ImmutableArray<MachineFact> OwnFacts { get; set; } = [];

    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

    public List<MachineSettings> Asked { get; } = [];

    public string Name => "windows";

    public string Words => "a test's Windows";

    public ImmutableArray<Claim> ClaimProcess(MachineSettings settings)
    {
        Asked.Add(settings);
        return [Answer("process.priority", settings.Process), Answer("process.qos", settings.Qos), Answer("process.gpuPriority", settings.GpuPriority)];
    }

    private Claim Answer(string what, string asked) =>
        Refuse.Contains(what) ? new Claim(what, asked, false, "", "Windows refused it here") : new Claim(what, asked, true, asked, "");

    public IThreadClaim ClaimThread(ThreadRole role) => new Claimed([Answer($"thread.{ThreadClaims.Word(role)}.priority", "aboveNormal")]);

    public IDeadlineTimer CreateTimer() => new RuntimeDeadlineTimer();

    public IDisposable KeepAwake() => new Claimed([]);

    public NodeIdentity Identity() => Ident;

    public CapabilitySnapshot Capabilities() => new(1_760_600_000_000,
    [
        new CapabilityProbe(MachineCapabilities.RealtimePrivilege, 0, Privileged, Privileged ? "" : PlatformPolicy.RealtimeWithoutPrivilegeWords, ""),
        new CapabilityProbe(MachineCapabilities.Hags, 0, true, "", ""),
    ]);

    public ImmutableArray<MachineFact> Facts() => OwnFacts;

    public void Dispose()
    {
        // Nothing is held.
    }

    private sealed class Claimed(ImmutableArray<Claim> claims) : IThreadClaim
    {
        public ImmutableArray<Claim> Claims => claims;

        public void Dispose()
        {
            // Nothing to revert.
        }
    }
}

/// <summary>Displays a test names, the first primary.</summary>
internal sealed class FakeDisplays(params DisplayInfo[] displays) : IOutputBackend
{
    public IReadOnlyList<DisplayInfo> Displays() => displays;

    public IReadOnlyList<OutputHealth> Health() => [];

    public string RendererName => "test";
}

/// <summary>An engine whose machine part is a fake Windows platform, read back through <see cref="RecordedPlatform"/>, with its machine
/// settings kept in a list (or refused by a test's writer), on a manual clock.</summary>
internal sealed class MachineRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = SessionInfo.Desk("d1");

    public readonly FakePlatform Platform = new();
    public readonly List<MachineSettings> Saved = [];
    public readonly MachineNode Node;
    public readonly RecordedPlatform Recorded;
    public readonly EngineCore Engine;

    public MachineRig(Project? project = null, Action<MachineSettings>? save = null, bool home = true, MachineSettings? settings = null,
        IOutputBackend? outputs = null)
    {
        Node = new MachineNode(settings ?? MachineSettings.Default, save ?? Saved.Add);
        Recorded = new RecordedPlatform(Platform, Clock, () => Node.Runtimes);
        _ = Recorded.ClaimProcess(Node.Saved);
        var services = EngineServices.ForTests(Clock) with
        {
            MachinePlatform = Recorded,
            MachineNode = home ? Node : null,
            Outputs = outputs ?? NullBackends.Instance,
        };
        Engine = new EngineCore(services, project ?? Project.CreateDefault("Machine test"));
    }

    public ManualClock Clock { get; } = new(5_000_000_000);

    /// <summary>A node: build 0.2.0+17, IoT Enterprise LTSC 24H2, a hybrid CPU, one RTX PRO with <paramref name="driver"/>.</summary>
    public static NodeIdentity Identity(string driver) => new(
        BuildInfo.Parse("0.2.0+17.abc1234"),
        EditionTable.Classify("IoTEnterpriseS", 0xBF, "24H2", 26100, 4061),
        new CpuIdentity("Intel(R) Core(TM) Ultra 9 285K", 24, [8, 16], 256, false, false),
        [new AdapterIdentity(0x123a4, VendorPolicy.Nvidia, 0x2bb1, "NVIDIA RTX PRO 4000 Blackwell", driver, "r580_00", VendorTier.NvidiaRtxPro,
            SupportLevel.Tier1, true, 24_576)],
        RuntimeIdentity.Empty);

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, session ?? Desk, CancellationToken.None);
    }

    public async Task<JsonElement> Ok(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return JsonDocument.Parse(r.Payload!).RootElement.Clone();
    }

    public async Task<string> Refused(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.False(r.Ok, $"{line} ran: {r.Payload}");
        return r.Payload ?? "";
    }

    public JsonElement State()
    {
        Engine.FactsChanged();
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.Clone();
    }

    public JsonElement Machine() => State().GetProperty("machine");

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
