using System.Collections.Immutable;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using PlayMap.Core;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Platform.Windows.Interop;

namespace PlayMap.Platform.Windows;

/// <summary>What a show node claims from Windows and what it is: the process's priority class, its HighQoS with every timer request
/// honoured and its GPU scheduling class, each thread's claims by role, the high-resolution timer, keep-awake by a power request,
/// the node's identity (OS edition and build, CPU, every adapter by LUID with its driver, its vendor tier and support level) and the
/// machine's facts for preflight. Every claim answers what it asked, what Windows said and what reading it back shows; nothing on the
/// machine is changed silently, and what a standard user cannot claim is a fact with words. Every saved setting reaches it
/// (<see cref="Use"/>): each thread's next claim, the next timer, keep-awake and the facts follow the settings in use. A thread claimed
/// twice (two listeners fed by one capture thread) holds one claim, joined: its claims are the first's, and they revert when the last
/// holder lets go. Any thread.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsMachinePlatform : IMachinePlatform
{
    public const string PlatformWords =
        "Windows: the process's and each thread's claims, the high-resolution timer, keep-awake and the node's identity";

    private readonly IMonoClock _clock;
    private readonly Func<long> _unixMs;
    private readonly Func<RuntimeIdentity> _runtimes;
    private readonly Func<ImmutableArray<ShowDoor>> _doors;
    private readonly Lateness _late = new();
    private readonly Lock _gate = new();
    private readonly List<Claim> _threadClaims = [];
    private readonly Dictionary<uint, SharedThreadClaim> _claimed = [];
    private MachineSettings _settings = MachineSettings.Default;
    private ImmutableArray<Claim> _process = [];
    private CpuPlacement? _placement;
    private NodeIdentity? _identity;
    private string _adapterWords = "";
    private string _nvapiWords = "";
    private CapabilitySnapshot? _capabilities;
    private PowerReading _power = PowerReading.Unread;
    private AwakeRequest? _awake;
    private int _awakeHolders;

    /// <param name="unixMs">The wall clock (Unix ms) for the servicing row's date and the capability snapshot's stamp.</param>
    /// <param name="runtimes">The runtimes their owners filled (Agility, FFmpeg, the sync firmware), read with the identity.</param>
    /// <param name="doors">The node's show doors open now, read with the facts: the networks' row reads the networks they are on.</param>
    public WindowsMachinePlatform(IMonoClock? clock = null, Func<long>? unixMs = null, Func<RuntimeIdentity>? runtimes = null,
        Func<ImmutableArray<ShowDoor>>? doors = null)
    {
        _clock = clock ?? MonoClock.Instance;
        _unixMs = unixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _runtimes = runtimes ?? (() => RuntimeIdentity.Empty);
        _doors = doors ?? (() => []);
    }

    public string Name => "windows";

    public string Words => PlatformWords;

    /// <summary>The settings in use: the last process claim's, or the last handed to <see cref="Use"/>.</summary>
    public MachineSettings Settings
    {
        get
        {
            lock (_gate)
                return _settings;
        }
    }

    public ImmutableArray<Claim> ClaimProcess(MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var process = Native.GetCurrentProcess();
        var claims = ImmutableArray.CreateBuilder<Claim>();
        claims.Add(PriorityClass(process, settings.Process == "high" ? Native.HighPriorityClass : Native.NormalPriorityClass, settings.Process));
        claims.Add(Qos(process, settings.Qos == "high"));
        claims.Add(GpuPriority(process, settings.GpuPriority));
        var made = claims.ToImmutable();
        lock (_gate)
        {
            _settings = settings;
            _placement = null;
            _process = made;
        }
        // The capabilities read the process's claims (whether timer requests are honoured): read again here, on the claimant's thread,
        // so no reader of them waits on the registry or the token.
        Reread();
        return made;
    }

    /// <summary>Claims from now on under <paramref name="settings"/>: each thread's next claim (MMCSS, CPU sets, placed afresh), the
    /// next timer (TIMER), the next keep-awake (AWAKE) and the facts follow them. The process's claims are made again only by
    /// <see cref="ClaimProcess"/>.</summary>
    public void Use(MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
            _placement = null;
        }
    }

    private static Claim PriorityClass(nint process, uint asked, string word)
    {
        if (!Native.SetPriorityClass(process, asked))
            return new Claim("process.priority", word, false, ClassWord(Native.GetPriorityClass(process)), "Windows refused the priority class: " + Native.LastErrorWords());
        var read = Native.GetPriorityClass(process);
        return new Claim("process.priority", word, read == asked, ClassWord(read), read == asked ? "" : "Windows kept another class");
    }

    private static string ClassWord(uint priorityClass) => priorityClass switch
    {
        Native.HighPriorityClass => "high",
        Native.NormalPriorityClass => "normal",
        Native.AboveNormalPriorityClass => "aboveNormal",
        Native.BelowNormalPriorityClass => "belowNormal",
        Native.IdlePriorityClass => "idle",
        Native.RealtimePriorityClass => "realtime",
        0 => "unread",
        _ => "0x" + priorityClass.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>HighQoS with every timer request honoured: execution speed and the timer resolution in the control mask, a zero state
    /// mask; AUTO hands both back to Windows. Read back with <c>GetProcessInformation</c> where Windows answers it.</summary>
    private static Claim Qos(nint process, bool high)
    {
        var asked = high ? "high" : "auto";
        var state = new PowerThrottlingState(Native.PowerThrottlingCurrentVersion, high ? Native.ExecutionSpeed | Native.IgnoreTimerResolution : 0, 0);
        if (!Native.SetProcessInformation(process, Native.ProcessPowerThrottling, &state, (uint)sizeof(PowerThrottlingState)))
            return new Claim("process.qos", asked, false, "", "Windows refused the power throttling state: " + Native.LastErrorWords());
        var read = new PowerThrottlingState(Native.PowerThrottlingCurrentVersion, 0, 0);
        if (!Native.GetProcessInformation(process, Native.ProcessPowerThrottling, &read, (uint)sizeof(PowerThrottlingState)))
            return new Claim("process.qos", asked, true, "unread", "set; Windows does not read it back here (" + Native.LastErrorWords() + ")");
        var readWord = QosWord(read);
        return new Claim("process.qos", asked, true, readWord, readWord == asked ? "" : "Windows reads back " + readWord);
    }

    /// <summary>A throttling state as the claim words it: "high" (both bits controlled, none throttled), "auto" (none controlled), else
    /// the masks.</summary>
    internal static string QosWord(PowerThrottlingState s) =>
        s.ControlMask == (Native.ExecutionSpeed | Native.IgnoreTimerResolution) && s.StateMask == 0 ? "high"
        : s.ControlMask == 0 ? "auto"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"control 0x{s.ControlMask:X} state 0x{s.StateMask:X}");

    private static Claim GpuPriority(nint process, string word)
    {
        var asked = word switch
        {
            "high" => 4,
            "realtime" => 5,
            _ => 2,
        };
        if (asked == 5 && !WindowsReadings.EnableIncreaseBasePriority())
            return new Claim("process.gpuPriority", word, false, ReadGpuPriority(process), PlatformPolicy.RealtimeWithoutPrivilegeWords);
        var status = Native.D3DKMTSetProcessSchedulingPriorityClass(process, asked);
        var read = ReadGpuPriority(process);
        return status >= 0
            ? new Claim("process.gpuPriority", word, true, read, read == word ? "" : "Windows reads back " + read)
            : new Claim("process.gpuPriority", word, false, read, "Windows refused the GPU scheduling class (" + Native.StatusWords(status) + ")");
    }

    private static string ReadGpuPriority(nint process) =>
        Native.D3DKMTGetProcessSchedulingPriorityClass(process, out var read) < 0 ? "unread" : read switch
        {
            0 => "idle",
            1 => "belowNormal",
            2 => "normal",
            3 => "aboveNormal",
            4 => "high",
            5 => "realtime",
            _ => "unknown",
        };

    /// <summary>Claims the calling thread's role under the settings in use. A thread already claimed (and still running) joins its
    /// claim instead: the same claims, reverted when the last holder disposes, so a second claim never reads the first's priority as
    /// the thread's own.</summary>
    public IThreadClaim ClaimThread(ThreadRole role)
    {
        var id = Native.GetCurrentThreadId();
        MachineSettings settings;
        CpuPlacement? placement;
        lock (_gate)
        {
            if (_claimed.TryGetValue(id, out var held) && held.Claim.Running)
            {
                held.Holders++;
                return new ThreadHold(this, held);
            }
            settings = _settings;
            placement = _placement;
        }
        placement ??= Place(settings);
        var claim = WindowsThreadClaim.Claim(PlatformPolicy.For(role, settings), placement);
        lock (_gate)
        {
            _threadClaims.RemoveAll(c => claim.Claims.Any(n => n.What == c.What));
            _threadClaims.AddRange(claim.Claims);
            if (claim is not WindowsThreadClaim made)
                return claim;
            var shared = new SharedThreadClaim(id, made);
            _claimed[id] = shared;
            return new ThreadHold(this, shared);
        }
    }

    /// <summary>One holder of a joined thread claim lets go; the last reverts it.</summary>
    private void Release(SharedThreadClaim shared)
    {
        lock (_gate)
        {
            if (--shared.Holders > 0)
                return;
            if (_claimed.TryGetValue(shared.ThreadId, out var now) && ReferenceEquals(now, shared))
                _ = _claimed.Remove(shared.ThreadId);
        }
        shared.Claim.Dispose();
    }

    private CpuPlacement Place(MachineSettings settings)
    {
        var placement = CpuSetPlan.Place(WindowsReadings.CpuSets(), settings);
        lock (_gate)
            if (ReferenceEquals(settings, _settings))
                _placement = placement;
        return placement;
    }

    /// <summary>The high-resolution timer under TIMER HIGH, else (or where Windows makes none) the runtime's own wait; either records
    /// its lateness in the platform's window.</summary>
    public IDeadlineTimer CreateTimer()
    {
        if (Settings.Timer == "high" && HighResolutionTimer.TryCreate(_clock, _late, out _) is { } timer)
            return timer;
        return new RecordedTimer(_clock, _late);
    }

    public string TimerMode(IDeadlineTimer timer) => timer is HighResolutionTimer ? "high" : "default";

    /// <summary>Keeps the display and the system awake while any holder holds it: one power request (display and system required)
    /// shared by every holder, cleared when the last lets go. Under AWAKE OFF nothing is held.</summary>
    public IDisposable KeepAwake()
    {
        if (Settings.Awake != "on")
            return new AwakeHold(null);
        lock (_gate)
        {
            _awake ??= AwakeRequest.TryCreate();
            _awakeHolders++;
        }
        return new AwakeHold(this);
    }

    /// <summary>Whether a power request keeps the machine awake now.</summary>
    public bool Awake
    {
        get
        {
            lock (_gate)
                return _awake is { Set: true } && _awakeHolders > 0;
        }
    }

    public Claim? AwakeClaim
    {
        get
        {
            lock (_gate)
            {
                if (_awakeHolders == 0 || _awake is not { } request)
                    return null;
                return request.Set ? new Claim(AwakeWhat, "on", true, "held", "") : new Claim(AwakeWhat, "on", false, "", request.Words);
            }
        }
    }

    /// <summary>Keep-awake's claim in STATE and preflight.</summary>
    public const string AwakeWhat = "process.awake";

    private void LetGo()
    {
        lock (_gate)
        {
            if (--_awakeHolders > 0)
                return;
            _awakeHolders = 0;
            _awake?.Dispose();
            _awake = null;
        }
    }

    public NodeIdentity Identity()
    {
        lock (_gate)
            if (_identity is not null)
                return _identity with { Runtimes = _runtimes() };
        var identity = ReadIdentity();
        lock (_gate)
            _identity = identity;
        return identity;
    }

    /// <summary>Reads the identity again (after a device change or a rebuild), and the capabilities with it; the next
    /// <see cref="Identity"/> answers it.</summary>
    public NodeIdentity RefreshIdentity()
    {
        var identity = ReadIdentity();
        lock (_gate)
            _identity = identity;
        Reread();
        return identity;
    }

    private NodeIdentity ReadIdentity()
    {
        var os = WindowsReadings.Os();
        var sets = WindowsReadings.CpuSets();
        var bits = Vector512.IsHardwareAccelerated ? 512 : Vector256.IsHardwareAccelerated ? 256 : Vector128.IsHardwareAccelerated ? 128 : 0;
        var cpu = new CpuIdentity(WindowsReadings.CpuModel(), Environment.ProcessorCount, CpuSetPlan.ByEfficiencyClass(sets), bits,
            Avx512F.IsSupported, Avx10v1.IsSupported);
        var dxgi = AdapterProbe.Read(out var dxgiWords);
        var nvidia = NvApiProbe.TryRead(out var nvDriver, out var nvBranch, out var workstation, out var nvWords);
        lock (_gate)
        {
            _adapterWords = dxgiWords;
            _nvapiWords = nvidia ? "" : nvWords;
        }
        var adapters = dxgi.Select(a =>
        {
            bool? pro = a.VendorId == VendorPolicy.Nvidia && nvidia ? workstation.Contains(a.Luid) : null;
            var tier = a.Software ? VendorTier.Software : VendorPolicy.Tier(a.VendorId, a.Name, pro);
            var driver = a.VendorId == VendorPolicy.Nvidia && nvDriver.Length > 0 ? nvDriver : a.UmdVersion;
            var branch = a.VendorId == VendorPolicy.Nvidia ? nvBranch : "";
            return new AdapterIdentity(a.Luid, a.VendorId, a.DeviceId, a.Name, driver, branch, tier, VendorPolicy.Support(tier, a.Name),
                a.HasOutputs, a.DedicatedMb);
        }).ToImmutableArray();
        return new NodeIdentity(BuildInfo.Current, os, cpu, adapters, _runtimes());
    }

    /// <summary>The platform's own probes, read once and again where the process's claims or the identity are made again (on that
    /// caller's thread), never on a reader's path once read.</summary>
    public CapabilitySnapshot Capabilities()
    {
        lock (_gate)
            if (_capabilities is not null)
                return _capabilities;
        return Reread();
    }

    public PowerReading Power
    {
        get
        {
            lock (_gate)
                return _power;
        }
    }

    private CapabilitySnapshot Reread()
    {
        var (read, power) = ReadCapabilities();
        lock (_gate)
        {
            _capabilities = read;
            _power = power;
        }
        return read;
    }

    private (CapabilitySnapshot Snapshot, PowerReading Power) ReadCapabilities()
    {
        var identity = Identity();
        string adapterWords, nvapiWords;
        lock (_gate)
            (adapterWords, nvapiWords) = (_adapterWords, _nvapiWords);
        var probes = ImmutableArray.CreateBuilder<CapabilityProbe>();
        var privileged = WindowsReadings.HoldsIncreaseBasePriority();
        probes.Add(new CapabilityProbe(MachineCapabilities.RealtimePrivilege, 0, privileged, privileged ? "" : PlatformPolicy.RealtimeWithoutPrivilegeWords, ""));
        probes.AddRange(MachineCapabilities.Unread(adapterWords, nvapiWords, identity.Adapters));
        foreach (var adapter in identity.Adapters)
            probes.Add(HagsProbe.Read(adapter));
        using (var timer = HighResolutionTimer.TryCreate(_clock, new Lateness(), out var why))
            probes.Add(new CapabilityProbe(MachineCapabilities.HighResolutionTimer, 0, timer is not null, why, ""));
        Claim? qos = null;
        lock (_gate)
            foreach (var c in _process)
                if (c.What == "process.qos")
                    qos = c;
        probes.Add(MachineCapabilities.TimerHonoured(qos));
        return (new CapabilitySnapshot(_unixMs(), probes.ToImmutable()), WindowsReadings.Power());
    }

    public ImmutableArray<MachineFact> Facts()
    {
        MachineSettings settings;
        lock (_gate)
            settings = _settings;
        var claims = Claims;
        var networksRead = WindowsReadings.TryNetworks(Environment.ProcessPath ?? "", out var networks, out _);
        var power = WindowsReadings.Power();
        lock (_gate)
            _power = power;
        var readings = new MachineReadings
        {
            Os = Identity().Os,
            Signed = WindowsReadings.Signed(Environment.ProcessPath),
            SmartAppControl = WindowsReadings.SmartAppControl(),
            Power = power,
            ScreenSaver = WindowsReadings.ScreenSaver(),
            RebootPending = WindowsReadings.RebootPending(),
            RebootRead = true,
            Networks = networks,
            NetworksRead = networksRead,
            Doors = _doors(),
            Claims = claims,
            TimerWaits = _late.Count,
            TimerLateP99Us = _late.P99Us,
            AwakeOn = settings.Awake == "on",
            UsbSuspend = WindowsReadings.UsbSuspend(),
            PcieLinkState = WindowsReadings.PcieLinkStatePower(),
            Notifications = WindowsReadings.Notifications(),
            Updates = WindowsReadings.Updates(_unixMs()),
            Hags = WindowsReadings.Hags() switch { true => 1, false => 0, _ => -1 },
            MemoryIntegrity = WindowsReadings.MemoryIntegrity(),
            GameMode = WindowsReadings.GameMode(),
        };
        return MachineRows.Build(readings, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(_unixMs()).UtcDateTime));
    }

    /// <summary>The claims made so far: the process's, keep-awake's while it is asked, then each role's latest.</summary>
    public ImmutableArray<Claim> Claims
    {
        get
        {
            var awake = AwakeClaim;
            lock (_gate)
                return awake is { } a ? [.. _process, a, .. _threadClaims] : [.. _process, .. _threadClaims];
        }
    }

    /// <summary>The p99 lateness of the platform's deadline waits (µs) and how many are counted.</summary>
    public (double P99Us, int Waits) TimerLateness => (_late.P99Us, _late.Count);

    public void Dispose()
    {
        lock (_gate)
        {
            _awake?.Dispose();
            _awake = null;
            _awakeHolders = 0;
        }
    }

    private sealed class AwakeHold(WindowsMachinePlatform? platform) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                platform?.LetGo();
        }
    }

    /// <summary>One thread's claim and how many hold it (the platform's lock guards the count).</summary>
    internal sealed class SharedThreadClaim(uint threadId, WindowsThreadClaim claim)
    {
        public uint ThreadId { get; } = threadId;

        public WindowsThreadClaim Claim { get; } = claim;

        public int Holders { get; set; } = 1;
    }

    /// <summary>One holder of a thread's claim: its claims are the thread's; disposing it lets go once, and the last holder reverts.</summary>
    internal sealed class ThreadHold : IThreadClaim
    {
        private readonly WindowsMachinePlatform _platform;
        private readonly SharedThreadClaim _shared;
        private int _done;

        public ThreadHold(WindowsMachinePlatform platform, SharedThreadClaim shared)
        {
            _platform = platform;
            _shared = shared;
        }

        public ImmutableArray<Claim> Claims => _shared.Claim.Claims;

        /// <summary>The thread's claim this holder shares.</summary>
        internal WindowsThreadClaim Shared => _shared.Claim;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _platform.Release(_shared);
        }
    }
}

/// <summary>A power request keeping the display and the system awake (<c>PowerCreateRequest</c>, display and system required): a
/// handle any thread holds and clears, unlike a thread's execution state, which ends with the thread that set it. Made even where Windows
/// refuses it, so the refusal's words are kept: <see cref="Set"/> is whether both requests are in force.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AwakeRequest : IDisposable
{
    private const string Reason = "PlayMap is showing on an output";

    private nint _request;

    private AwakeRequest(nint request, bool set, string words)
    {
        _request = request;
        Set = set;
        Words = words;
    }

    public bool Set { get; }

    /// <summary>Why the request is not in force ("" when it is): Windows' words.</summary>
    public string Words { get; }

    public static AwakeRequest TryCreate()
    {
        fixed (char* reason = Reason)
        {
            var context = new ReasonContext(Native.PowerRequestContextVersion, Native.PowerRequestContextSimpleString, (nint)reason);
            var request = Native.PowerCreateRequest(context);
            if (request == 0 || request == -1)
                return new AwakeRequest(0, false, "Windows made no power request: " + Native.LastErrorWords());
            if (!Native.PowerSetRequest(request, Native.PowerRequestDisplayRequired))
                return new AwakeRequest(request, false, "Windows refused the display-required request: " + Native.LastErrorWords());
            if (!Native.PowerSetRequest(request, Native.PowerRequestSystemRequired))
                return new AwakeRequest(request, false, "Windows refused the system-required request: " + Native.LastErrorWords());
            return new AwakeRequest(request, true, "");
        }
    }

    public void Dispose()
    {
        var request = Interlocked.Exchange(ref _request, 0);
        if (request == 0)
            return;
        _ = Native.PowerClearRequest(request, Native.PowerRequestDisplayRequired);
        _ = Native.PowerClearRequest(request, Native.PowerRequestSystemRequired);
        _ = Native.CloseHandle(request);
    }
}
