namespace PlayMap.Core.Machine;

/// <summary>The machine platform's own capability probes, by feature name in <see cref="CapabilitySnapshot"/> (the machine's: adapter 0).</summary>
public static class MachineCapabilities
{
    /// <summary>Whether this account holds SeIncreaseBasePriorityPrivilege, which GPU-PRIORITY REALTIME needs.</summary>
    public const string RealtimePrivilege = "realtimePrivilege";

    /// <summary>Hardware-accelerated GPU scheduling, as set (recorded, never forced).</summary>
    public const string Hags = "hags";

    /// <summary>Whether Windows makes a high-resolution waitable timer.</summary>
    public const string HighResolutionTimer = "highResolutionTimer";

    /// <summary>Whether the process's timer requests are honoured while its windows are hidden (the QoS claim's IGNORE_TIMER_RESOLUTION).</summary>
    public const string TimerResolutionHonoured = "timerResolutionHonoured";
}
