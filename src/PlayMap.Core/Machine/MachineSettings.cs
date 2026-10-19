using System.Collections.Immutable;
using System.Globalization;

namespace PlayMap.Core.Machine;

/// <summary>MACHINE SET's settings, one per claim, and MACHINE LOCK's driver set, kept in this node's own settings by its settings
/// writer (never in the project, so a hot backup keeps its own). Each value is the word <c>MACHINE SET</c> takes, lower case; a
/// claim's default and the cause of that default are <see cref="PlatformPolicy.Defaults"/>'.</summary>
public sealed record MachineSettings
{
    public static MachineSettings Default { get; } = new();

    /// <summary>high | auto: the process asks for HighQoS and every timer request honoured, or leaves Windows its own throttling.</summary>
    public string Qos { get; init; } = "high";

    /// <summary>high | default: deadline waits on the high-resolution waitable timer, or on the runtime's own wait.</summary>
    public string Timer { get; init; } = "high";

    /// <summary>on | off: render and present join MMCSS's "Games", audio and timecode "Pro Audio", capture "Capture".</summary>
    public string Mmcss { get; init; } = "on";

    /// <summary>high | normal: the process's priority class (realtime never).</summary>
    public string Process { get; init; } = "high";

    /// <summary>auto | off: the critical threads on the highest efficiency class's CPU sets, the background on the rest.</summary>
    public string CpuSets { get; init; } = "auto";

    /// <summary>normal | high | realtime: the process's GPU scheduling class.</summary>
    public string GpuPriority { get; init; } = "normal";

    /// <summary>on | off: the display and the system kept awake while an output is open.</summary>
    public string Awake { get; init; } = "on";

    /// <summary>The driver set this node's shows run on (MACHINE LOCK), or null when none is locked.</summary>
    public LockedDriverSet? Locked { get; init; }
}

/// <summary>The driver set MACHINE LOCK keeps: when it was locked, the OS build and revision, PlayMap's build, the Direct3D 12 Agility
/// runtime, FFmpeg, the sync board's firmware and every GPU by vendor, device and name with its driver. A LUID names an adapter only
/// within one boot, so the set never keys by one.</summary>
public sealed record LockedDriverSet(long AtUnixMs, string Os, string PlayMap, string Agility, string Ffmpeg, string SyncFirmware,
    ImmutableArray<LockedAdapter> Adapters)
{
    /// <summary>The set this node runs now, from its identity.</summary>
    public static LockedDriverSet Of(NodeIdentity identity, long atUnixMs)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var os = identity.Os.Build == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{identity.Os.Build}.{identity.Os.Revision}");
        return new LockedDriverSet(atUnixMs, os, identity.Build.Informational, identity.Runtimes.Agility, identity.Runtimes.Ffmpeg,
            identity.Runtimes.SyncFirmware,
            [.. identity.Adapters.Select(a => new LockedAdapter(a.VendorId, a.DeviceId, a.Name, a.Driver))
                .OrderBy(a => a.VendorId).ThenBy(a => a.DeviceId).ThenBy(a => a.Name, StringComparer.Ordinal)]);
    }

    /// <summary>What moved from this locked set to <paramref name="now"/>, in a fixed order (OS, PlayMap, the runtimes, then each
    /// adapter): an adapter's driver that changed, an adapter gone, an adapter added. Empty when the two sets are the same drivers.</summary>
    public ImmutableArray<DriverDrift> DriftTo(LockedDriverSet now)
    {
        ArgumentNullException.ThrowIfNull(now);
        var drift = ImmutableArray.CreateBuilder<DriverDrift>();
        Moved("os", "Windows build", Os, now.Os);
        Moved("playmap", "PlayMap build", PlayMap, now.PlayMap);
        Moved("agility", "Direct3D 12 Agility runtime", Agility, now.Agility);
        Moved("ffmpeg", "FFmpeg", Ffmpeg, now.Ffmpeg);
        Moved("syncFirmware", "sync board firmware", SyncFirmware, now.SyncFirmware);
        var left = now.Adapters.ToList();
        foreach (var a in Adapters)
        {
            var match = left.FindIndex(b => b.SameAdapter(a));
            if (match < 0)
            {
                drift.Add(new DriverDrift("gpu:" + a.Key, a.Name, a.Driver, ""));
                continue;
            }
            var b = left[match];
            left.RemoveAt(match);
            if (!string.Equals(a.Driver, b.Driver, StringComparison.Ordinal))
                drift.Add(new DriverDrift("gpu:" + a.Key, a.Name + " driver", a.Driver, b.Driver));
        }
        foreach (var b in left)
            drift.Add(new DriverDrift("gpu:" + b.Key, b.Name, "", b.Driver));
        return drift.ToImmutable();

        void Moved(string item, string what, string locked, string current)
        {
            if (!string.Equals(locked, current, StringComparison.Ordinal))
                drift.Add(new DriverDrift(item, what, locked, current));
        }
    }

    /// <summary>Whether <paramref name="other"/> holds the same drivers (when each was locked aside).</summary>
    public bool SameDrivers(LockedDriverSet other) => DriftTo(other).IsEmpty;
}

/// <summary>One GPU of a locked driver set: its vendor and device ids, its name and its driver's version.</summary>
public sealed record LockedAdapter(int VendorId, int DeviceId, string Name, string Driver)
{
    /// <summary>"10de:2bb1" — the vendor and device, as a drift row names the adapter.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{VendorId:x4}:{DeviceId:x4}");

    public bool SameAdapter(LockedAdapter other) =>
        other is not null && other.VendorId == VendorId && other.DeviceId == DeviceId && string.Equals(other.Name, Name, StringComparison.Ordinal);
}

/// <summary>One move from the locked driver set: the item ("os", "gpu:10de:2bb1"), what it is in words, the locked version ("" when
/// it was not there) and the version now ("" when it is gone).</summary>
public sealed record DriverDrift(string Item, string What, string Locked, string Now)
{
    /// <summary>"GPU driver 582.16 → 591.44 since the driver set was locked", "… added since …", "… gone since …".</summary>
    public string Words =>
        Locked.Length == 0 ? $"{What} {Now} added since the driver set was locked"
        : Now.Length == 0 ? $"{What} {Locked} gone since the driver set was locked"
        : $"{What} {Locked} → {Now} since the driver set was locked";
}
