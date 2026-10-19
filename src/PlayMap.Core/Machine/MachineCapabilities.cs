using System.Collections.Immutable;

namespace PlayMap.Core.Machine;

/// <summary>The machine platform's own capability probes, by feature name in <see cref="CapabilitySnapshot"/> (the machine's: adapter 0).</summary>
public static class MachineCapabilities
{
    /// <summary>Whether this account holds SeIncreaseBasePriorityPrivilege, which GPU-PRIORITY REALTIME needs.</summary>
    public const string RealtimePrivilege = "realtimePrivilege";

    /// <summary>Hardware-accelerated GPU scheduling as each adapter's kernel driver answers it (supported, and on or off in the words),
    /// one probe per adapter by its LUID; recorded, never forced.</summary>
    public const string Hags = "hags";

    /// <summary>Why no adapter was read: DXGI's failure, published only when DXGI fails.</summary>
    public const string Adapters = "adapters";

    /// <summary>Why NVIDIA's driver facts were not read (the RTX PRO probe, the driver's branch): NVAPI's failure, published only where an
    /// NVIDIA adapter is present.</summary>
    public const string NvApi = "nvapi";

    /// <summary>Whether Windows makes a high-resolution waitable timer.</summary>
    public const string HighResolutionTimer = "highResolutionTimer";

    /// <summary>Whether the process's timer requests are honoured while its windows are hidden (the QoS claim's IGNORE_TIMER_RESOLUTION).</summary>
    public const string TimerResolutionHonoured = "timerResolutionHonoured";

    /// <summary>The probes that say why something the identity names is unread: DXGI's failure (no adapter read, which would otherwise
    /// read as "no GPU"), and NVAPI's only where an NVIDIA adapter is there to ask, so a machine with no NVIDIA adapter says nothing of
    /// it. Empty when both answered.</summary>
    public static ImmutableArray<CapabilityProbe> Unread(string adapterWords, string nvapiWords, IEnumerable<AdapterIdentity> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapterWords);
        ArgumentNullException.ThrowIfNull(nvapiWords);
        ArgumentNullException.ThrowIfNull(adapters);
        var probes = ImmutableArray.CreateBuilder<CapabilityProbe>();
        if (adapterWords.Length > 0)
            probes.Add(new CapabilityProbe(Adapters, 0, false, adapterWords, ""));
        if (nvapiWords.Length > 0 && adapters.Any(a => a.VendorId == VendorPolicy.Nvidia))
            probes.Add(new CapabilityProbe(NvApi, 0, false, nvapiWords + ": the RTX PRO probe and the driver's branch are unread", ""));
        return probes.ToImmutable();
    }

    /// <summary>An adapter's hardware scheduling probe from its driver's two flags (<c>D3DKMT_WDDM_2_7_CAPS</c>): supported, on or off
    /// in the words, or not supported by that driver.</summary>
    public static CapabilityProbe HagsOf(AdapterIdentity adapter, bool supported, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var words = !supported ? "this adapter's driver does not support hardware-accelerated GPU scheduling"
            : enabled ? "hardware-accelerated GPU scheduling is on"
            : "hardware-accelerated GPU scheduling is supported and off";
        return new CapabilityProbe(Hags, adapter.Luid, supported, words, adapter.Driver);
    }

    /// <summary>Whether the process's timer requests are honoured while its windows are hidden, from the QoS claim: only where it asked
    /// HIGH, was applied and reads back high. A claim the system does not read back is set, unobserved; one that reads back otherwise
    /// says the claim's words.</summary>
    public static CapabilityProbe TimerHonoured(Claim? qos) => qos switch
    {
        null => new(TimerResolutionHonoured, 0, false, "no process claim has been made yet", ""),
        { Applied: false } c => new(TimerResolutionHonoured, 0, false, c.Words, ""),
        { Asked: not "high" } => new(TimerResolutionHonoured, 0, false, "QoS is AUTO: Windows may ignore the timer requests of a hidden process", ""),
        { ReadBack: "high" } => new(TimerResolutionHonoured, 0, true, "", ""),
        { ReadBack: "unread" } => new(TimerResolutionHonoured, 0, false, "set; Windows does not read it back here", ""),
        { } c => new(TimerResolutionHonoured, 0, false, c.Words, ""),
    };
}
