using System.Collections.Immutable;

namespace PlayMap.Core.Machine;

/// <summary>What this node is, as one record STATE carries (<c>machine.identity</c>) and preflight compares with the set locked for
/// the show: PlayMap's build, the operating system, the CPU, every GPU by LUID with its driver, and the runtimes it loaded. Read at
/// start, after a device change and after a rebuild; a field nobody has read is empty or <c>Unknown</c>, never guessed.</summary>
public sealed record NodeIdentity(BuildInfo Build, OsIdentity Os, CpuIdentity Cpu, ImmutableArray<AdapterIdentity> Adapters, RuntimeIdentity Runtimes)
{
    /// <summary>The identity of a node whose platform reads no machine facts: this build, everything else unknown.</summary>
    public static NodeIdentity Unread { get; } = new(BuildInfo.Current, OsIdentity.Unknown, CpuIdentity.Unknown, [], RuntimeIdentity.Empty);
}

/// <summary>An operating system's edition family, as STATE words it: <c>home</c>, <c>pro</c>, <c>workstation</c>, <c>enterprise</c>,
/// <c>education</c>, <c>iot</c>, <c>server</c>, <c>unknown</c>. The family is the edition's line; whether it is a long-term
/// servicing channel and whether it is an IoT edition are flags beside it (<see cref="OsIdentity.Ltsc"/>, <see cref="OsIdentity.Iot"/>),
/// so IoT Enterprise LTSC is family <c>iot</c> with both flags.</summary>
public enum EditionFamily
{
    Unknown,
    Home,
    Pro,
    Workstation,
    Enterprise,
    Education,
    Iot,
    Server,
}

/// <summary>The operating system: its edition as the system names it (<see cref="Edition"/>, e.g. "IoTEnterpriseS"), its family,
/// its version ("24H2"), build and revision, and whether it is a long-term servicing channel and an IoT edition.</summary>
public sealed record OsIdentity(string Edition, EditionFamily Family, string Version, int Build, int Revision, bool Ltsc, bool Iot)
{
    public static OsIdentity Unknown { get; } = new("", EditionFamily.Unknown, "", 0, 0, false, false);

    public string FamilyWord => Word(Family);

    /// <summary>A family's word in STATE.</summary>
    public static string Word(EditionFamily family) => family switch
    {
        EditionFamily.Home => "home",
        EditionFamily.Pro => "pro",
        EditionFamily.Workstation => "workstation",
        EditionFamily.Enterprise => "enterprise",
        EditionFamily.Education => "education",
        EditionFamily.Iot => "iot",
        EditionFamily.Server => "server",
        _ => "unknown",
    };

    /// <summary>A family from its STATE word, ignoring case; any other word is <see cref="EditionFamily.Unknown"/> and false.</summary>
    public static bool TryParseFamily(string? word, out EditionFamily family)
    {
        EditionFamily? read = word?.Trim().ToLowerInvariant() switch
        {
            "home" => EditionFamily.Home,
            "pro" => EditionFamily.Pro,
            "workstation" => EditionFamily.Workstation,
            "enterprise" => EditionFamily.Enterprise,
            "education" => EditionFamily.Education,
            "iot" => EditionFamily.Iot,
            "server" => EditionFamily.Server,
            "unknown" => EditionFamily.Unknown,
            _ => null,
        };
        family = read ?? EditionFamily.Unknown;
        return read is not null;
    }
}

/// <summary>The CPU: its model, its logical processors, how many of them each efficiency class holds (the highest class first; empty
/// when unread), the widest vector the runtime accelerates (bits), and whether AVX-512 and AVX10 are supported.</summary>
public sealed record CpuIdentity(string Model, int Logical, ImmutableArray<int> ByEfficiencyClass, int VectorBits, bool Avx512, bool Avx10)
{
    public static CpuIdentity Unknown { get; } = new("", 0, [], 0, false, false);
}

/// <summary>How PlayMap's published policy places a GPU's vendor line (<c>docs/INSTALL.md</c>).</summary>
public enum VendorTier
{
    Other,
    NvidiaRtxPro,
    NvidiaRtx,
    Radeon,
    IntelArc,
    Software,
}

/// <summary>What the published policy promises a vendor tier: validated (tier 1), validated per driver, best effort, unsupported.</summary>
public enum SupportLevel
{
    Unsupported,
    Tier1,
    ValidatedPerDriver,
    BestEffort,
}

/// <summary>One GPU: its LUID (which names an adapter within one boot only, so a driver set is keyed by vendor, device and name), its
/// vendor and device ids, its name, its driver's version and branch, its tier and support under the published policy, whether it
/// drives the outputs, and its dedicated memory (MB).</summary>
public sealed record AdapterIdentity(long Luid, int VendorId, int DeviceId, string Name, string Driver, string Branch, VendorTier Tier,
    SupportLevel Support, bool DrivesOutputs, long DedicatedMb)
{
    public string TierWord => Word(Tier);

    public string SupportWord => Word(Support);

    /// <summary>"0x" and the LUID's sixteen hex digits, as STATE carries it.</summary>
    public string LuidText => "0x" + Luid.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A tier's word in STATE: "nvidiaRtxPro", "nvidiaRtx", "radeon", "intelArc", "software", "other".</summary>
    public static string Word(VendorTier tier) => tier switch
    {
        VendorTier.NvidiaRtxPro => "nvidiaRtxPro",
        VendorTier.NvidiaRtx => "nvidiaRtx",
        VendorTier.Radeon => "radeon",
        VendorTier.IntelArc => "intelArc",
        VendorTier.Software => "software",
        _ => "other",
    };

    /// <summary>A support level's word in STATE: "tier1", "validatedPerDriver", "bestEffort", "unsupported".</summary>
    public static string Word(SupportLevel support) => support switch
    {
        SupportLevel.Tier1 => "tier1",
        SupportLevel.ValidatedPerDriver => "validatedPerDriver",
        SupportLevel.BestEffort => "bestEffort",
        _ => "unsupported",
    };
}

/// <summary>The runtimes the node loaded, each filled by its owner: the Direct3D 12 Agility runtime, FFmpeg's build, the sync board's
/// firmware ("" until read).</summary>
public sealed record RuntimeIdentity(string Agility, string Ffmpeg, string SyncFirmware)
{
    public static RuntimeIdentity Empty { get; } = new("", "", "");
}

/// <summary>Every capability probe's answer at one instant. Immutable: a device rebuild or a driver change makes a new snapshot.</summary>
public sealed record CapabilitySnapshot(long AtUnixMs, ImmutableArray<CapabilityProbe> Probes)
{
    public static CapabilitySnapshot Empty { get; } = new(0, []);

    /// <summary>The probe of a feature on an adapter (0: the machine's own), or null when none asked it.</summary>
    public CapabilityProbe? Find(string feature, long adapterLuid = 0)
    {
        ArgumentNullException.ThrowIfNull(feature);
        foreach (var p in Probes)
        {
            if (p.AdapterLuid == adapterLuid && string.Equals(p.Feature, feature, StringComparison.Ordinal))
                return p;
        }
        return null;
    }
}

/// <summary>One probe's answer: the feature asked ("presentBarrier"), the adapter it was asked of (0: the machine), whether it is
/// supported, the words why not, and the driver that answered.</summary>
public readonly record struct CapabilityProbe(string Feature, long AdapterLuid, bool Supported, string Words, string Driver);
