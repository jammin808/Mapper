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
/// that apply (or, for the claims named in <see cref="Refuse"/>, are refused with words) and read back as asked. Like Windows' platform it
/// claims under the settings it was last handed (<see cref="Used"/>, else its last process claim's): each thread's claims follow the
/// roles' table under them (MMCSS ON joins its task), the timer it makes is the high one under TIMER HIGH, and keep-awake holds nothing
/// under AWAKE OFF; a hold is refused with <see cref="RefuseAwake"/>'s words when set.</summary>
internal sealed class FakePlatform : IMachinePlatform
{
    private int _awakeHolders;

    public NodeIdentity Ident { get; set; } = MachineRig.Identity("582.16");

    public bool Privileged { get; set; }

    public ImmutableArray<MachineFact> OwnFacts { get; set; } = [];

    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

    public List<MachineSettings> Asked { get; } = [];

    /// <summary>Every set of settings handed to the platform, in order.</summary>
    public List<MachineSettings> Used { get; } = [];

    /// <summary>The settings the platform claims under now.</summary>
    public MachineSettings InUse => Used.Count > 0 ? Used[^1] : Asked.Count > 0 ? Asked[^1] : MachineSettings.Default;

    /// <summary>The AWAKE setting in use at each keep-awake asked of it.</summary>
    public List<string> AwakeAskedUnder { get; } = [];

    /// <summary>Set: a hold is refused with these words.</summary>
    public string? RefuseAwake { get; set; }

    public PowerReading Power { get; set; } = new("Ultimate Performance", "ultimate", false, "", 0, 0);

    public string Name => "windows";

    public string Words => "a test's Windows";

    public ImmutableArray<Claim> ClaimProcess(MachineSettings settings)
    {
        Asked.Add(settings);
        return [Answer("process.priority", settings.Process), Answer("process.qos", settings.Qos), Answer("process.gpuPriority", settings.GpuPriority)];
    }

    public void Use(MachineSettings settings) => Used.Add(settings);

    private Claim Answer(string what, string asked) =>
        Refuse.Contains(what) ? new Claim(what, asked, false, "", "Windows refused it here") : new Claim(what, asked, true, asked, "");

    public IThreadClaim ClaimThread(ThreadRole role)
    {
        var claims = PlatformPolicy.For(role, InUse);
        var word = ThreadClaims.Word(role);
        ImmutableArray<Claim> made = claims.MmcssTask is { } task
            ? [Answer($"thread.{word}.priority", PlatformPolicy.Word(claims.Priority)), Answer($"thread.{word}.mmcss", task)]
            : [Answer($"thread.{word}.priority", PlatformPolicy.Word(claims.Priority))];
        lock (_made)
            _claimedOn.Add((word, Thread.CurrentThread.Name ?? ""));
        return new Claimed(made);
    }

    /// <summary>Each role claimed ("render", "audio", …) with the name of the thread it was claimed on ("" for an unnamed one), in
    /// order.</summary>
    public IReadOnlyList<(string Role, string Thread)> ClaimedOn
    {
        get
        {
            lock (_made)
                return [.. _claimedOn];
        }
    }

    /// <summary>The name of each thread a timer was made on ("" for an unnamed one), in order.</summary>
    public IReadOnlyList<string> TimersMadeOn
    {
        get
        {
            lock (_made)
                return [.. _timersMadeOn];
        }
    }

    /// <summary>The name of each thread a timer was disposed on ("" for an unnamed one), in order.</summary>
    public IReadOnlyList<string> TimersDisposedOn
    {
        get
        {
            lock (_made)
                return [.. _timersDisposedOn];
        }
    }

    private readonly Lock _made = new();
    private readonly List<(string, string)> _claimedOn = [];
    private readonly List<string> _timersMadeOn = [];
    private readonly List<string> _timersDisposedOn = [];

    public IDeadlineTimer CreateTimer()
    {
        lock (_made)
            _timersMadeOn.Add(Thread.CurrentThread.Name ?? "");
        return InUse.Timer == "high" ? new HighTimer(this) : new RuntimeDeadlineTimer();
    }

    private void Disposed()
    {
        lock (_made)
            _timersDisposedOn.Add(Thread.CurrentThread.Name ?? "");
    }

    public string TimerMode(IDeadlineTimer timer) => timer is HighTimer ? "high" : "default";

    public IDisposable KeepAwake()
    {
        AwakeAskedUnder.Add(InUse.Awake);
        if (InUse.Awake != "on")
            return new Hold(null);
        _awakeHolders++;
        return new Hold(this);
    }

    public Claim? AwakeClaim => _awakeHolders == 0 ? null
        : RefuseAwake is { } words ? new Claim("process.awake", "on", false, "", words) : new Claim("process.awake", "on", true, "held", "");

    public NodeIdentity Identity() => Ident;

    public CapabilitySnapshot Capabilities() => new(1_760_600_000_000,
    [
        new CapabilityProbe(MachineCapabilities.RealtimePrivilege, 0, Privileged, Privileged ? "" : PlatformPolicy.RealtimeWithoutPrivilegeWords, ""),
        new CapabilityProbe(MachineCapabilities.Hags, 0x123a4, true, "hardware-accelerated GPU scheduling is on", "582.16"),
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

    private sealed class Hold(FakePlatform? platform) : IDisposable
    {
        public void Dispose()
        {
            if (platform is not null)
                platform._awakeHolders--;
        }
    }

    /// <summary>The fake's high-resolution timer: the runtime's wait under another name, its platform told the thread it is disposed on.</summary>
    private sealed class HighTimer(FakePlatform platform) : IDeadlineTimer
    {
        private readonly RuntimeDeadlineTimer _inner = new();

        public double LateP99Us => _inner.LateP99Us;

        public bool WaitUntil(long monoNs, WaitHandle? cancel = null) => _inner.WaitUntil(monoNs, cancel);

        public void Dispose()
        {
            platform.Disposed();
            _inner.Dispose();
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

/// <summary>An engine whose machine part (the node's own <see cref="MachineParts"/>, configured and started as the host does) runs on a
/// fake Windows platform, read back through <see cref="RecordedPlatform"/>, with its machine settings kept in a list (or refused by a
/// test's writer), on a manual clock. The collector's pauses are not counted.</summary>
internal sealed class MachineRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = SessionInfo.Desk("d1");

    public readonly FakePlatform Platform = new();
    public readonly List<MachineSettings> Saved = [];
    public readonly MachineNode Node;
    public readonly RecordedPlatform Recorded;
    public readonly EngineCore Engine;
    private readonly MachineParts _parts;

    public MachineRig(Project? project = null, Action<MachineSettings>? save = null, bool home = true, MachineSettings? settings = null,
        IOutputBackend? outputs = null)
    {
        Node = new MachineNode(settings ?? MachineSettings.Default, save ?? Saved.Add);
        _parts = new MachineParts(home ? Node : null, Platform);
        var services = _parts.Configure(EngineServices.ForTests(Clock) with { Outputs = outputs ?? NullBackends.Instance });
        Recorded = (RecordedPlatform)services.MachinePlatform;
        Engine = new EngineCore(services, project ?? Project.CreateDefault("Machine test"));
        // The part's start does no wait: it attaches to the engine and holds keep-awake as the project asks.
        Assert.True(_parts.StartAsync(Engine, CancellationToken.None).IsCompletedSuccessfully);
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

    /// <summary>The default project with its one display output disabled: nothing asks for keep-awake.</summary>
    public static Project WithoutDisplay()
    {
        var project = Project.CreateDefault("Machine test");
        return project with { Outputs = [project.Outputs[0] with { Enabled = false }] };
    }

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

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await _parts.DisposeAsync();
    }
}
