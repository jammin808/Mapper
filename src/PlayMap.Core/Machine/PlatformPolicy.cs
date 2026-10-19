using System.Collections.Immutable;

namespace PlayMap.Core.Machine;

/// <summary>A thread's scheduling priority as Windows numbers it (THREAD_PRIORITY_*).</summary>
public enum ThreadLevel
{
    BelowNormal = -1,
    Normal = 0,
    AboveNormal = 1,
    Highest = 2,
}

/// <summary>Which CPU sets a thread is placed on: none chosen, the critical pool (the highest efficiency class) or the background
/// pool (every other class).</summary>
public enum CpuPool
{
    None,
    Critical,
    Background,
}

/// <summary>What the platform claims for one thread role: its priority, the MMCSS task it joins (null: none), its CPU pool, and whether
/// it asks for EcoQoS.</summary>
public sealed record RoleClaims(ThreadRole Role, ThreadLevel Priority, string? MmcssTask, CpuPool Pool, bool EcoQos);

/// <summary>One MACHINE SET word: its values (the first is the default), when a change applies ("now"; "next start" for what is
/// claimed as a thread or the render host starts; "next arm" for what the show's arming takes), the default's documented cause, and its
/// words in a refusal ("QoS").</summary>
public sealed record ClaimDefault(string Word, ImmutableArray<string> Values, string Default, string Applies, string Cause, string Label);

/// <summary>The policy of the machine's claims, pure so both CI jobs prove it: the role → claims table, MACHINE SET's words with their
/// defaults and each default's cause, and how a setting changes (refused at its value, REALTIME refused without the privilege).
/// A default is on only where its cause is documented; the GPU scheduling class and the GC's show mode have none and stay off until the
/// lab measures them.</summary>
public static class PlatformPolicy
{
    /// <summary>The MMCSS tasks the roles join (their registry entries under SystemProfile\Tasks, read and never written).</summary>
    public const string Games = "Games", ProAudio = "Pro Audio", Capture = "Capture";

    /// <summary>The words of GPU-PRIORITY REALTIME on an account without the privilege it needs.</summary>
    public const string RealtimeWithoutPrivilegeWords =
        "GPU-PRIORITY REALTIME needs SeIncreaseBasePriorityPrivilege, which this account does not hold: the installer grants it to the show account";

    /// <summary>Every MACHINE SET word in the catalogue's order, with its default and the cause of that default.</summary>
    public static ImmutableArray<ClaimDefault> Defaults { get; } =
    [
        new("QOS", ["high", "auto"], "high", "now",
            "documented: Windows 11 lowers the QoS of a process whose windows are occluded, minimised or absent and stops honouring its timer requests",
            "QoS"),
        new("TIMER", ["high", "default"], "high", "next start",
            "documented: a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION) wakes a deadline wait without raising the system's timer resolution",
            "the timer"),
        new("MMCSS", ["on", "off"], "on", "next start",
            "documented: MMCSS is Windows' service for time-critical multimedia threads, which it places in the Media and Deadline QoS levels",
            "MMCSS"),
        new("PROCESS", ["high", "normal"], "high", "now",
            "documented: the high priority class is for a process whose threads are mostly blocked and run briefly for time-critical events; realtime never",
            "the process priority"),
        new("CPUSETS", ["auto", "off"], "auto", "next start",
            "documented: Intel and Microsoft advise CPU sets, never affinity, on hybrid processors; one efficiency class leaves it a no-op",
            "CPU sets"),
        new("GPU-PRIORITY", ["normal", "high", "realtime"], "normal", "now",
            "none documented: the GPU scheduling class stays NORMAL until the lab measures HIGH (L10)",
            "the GPU priority"),
        new("GC", ["default", "show"], "default", "next arm",
            "none documented: Microsoft names animation as SustainedLowLatency's case, but it grows an uncompacted heap; DEFAULT until the lab's GC pause histogram beside the frame time measures it (L10)",
            "the GC's mode while armed"),
        new("AWAKE", ["on", "off"], "on", "now",
            "documented: the display and the system otherwise sleep on their timeouts while an output is open",
            "keep-awake"),
    ];

    /// <summary>A word's default row, or null for a word MACHINE SET does not take.</summary>
    public static ClaimDefault? Find(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        foreach (var d in Defaults)
            if (string.Equals(d.Word, word.Trim(), StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    /// <summary>MACHINE SET's syntax, as a refusal names it.</summary>
    public static string Syntax => "MACHINE SET " + string.Join(" | ", Defaults.Select(d => d.Word + " " + string.Join('|', d.Values.Select(v => v.ToUpperInvariant()))));

    /// <summary>A setting's value now, by its word.</summary>
    public static string ValueOf(MachineSettings settings, string word)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Find(word)?.Word switch
        {
            "QOS" => settings.Qos,
            "TIMER" => settings.Timer,
            "MMCSS" => settings.Mmcss,
            "PROCESS" => settings.Process,
            "CPUSETS" => settings.CpuSets,
            "GPU-PRIORITY" => settings.GpuPriority,
            "GC" => settings.Gc,
            "AWAKE" => settings.Awake,
            _ => throw new ArgumentException($"{word} is not a MACHINE SET word", nameof(word)),
        };
    }

    /// <summary>The settings with one word's value changed: false, with the refusal's words, for a word or value MACHINE SET does not
    /// take, a value the setting already has, or REALTIME on an account without the privilege
    /// (<paramref name="realtimePrivileged"/>).</summary>
    public static bool TrySet(MachineSettings settings, string word, string value, bool realtimePrivileged, out MachineSettings next, out string words)
    {
        ArgumentNullException.ThrowIfNull(settings);
        next = settings;
        if (Find(word ?? "") is not { } row)
        {
            words = Syntax;
            return false;
        }
        var asked = value?.Trim().ToLowerInvariant() ?? "";
        if (!row.Values.Contains(asked))
        {
            words = $"MACHINE SET {row.Word} {string.Join('|', row.Values.Select(v => v.ToUpperInvariant()))}";
            return false;
        }
        if (string.Equals(ValueOf(settings, row.Word), asked, StringComparison.Ordinal))
        {
            words = $"{char.ToUpperInvariant(row.Label[0])}{row.Label[1..]} is already {asked}";
            return false;
        }
        if (asked == "realtime" && !realtimePrivileged)
        {
            words = RealtimeWithoutPrivilegeWords;
            return false;
        }
        next = row.Word switch
        {
            "QOS" => settings with { Qos = asked },
            "TIMER" => settings with { Timer = asked },
            "MMCSS" => settings with { Mmcss = asked },
            "PROCESS" => settings with { Process = asked },
            "CPUSETS" => settings with { CpuSets = asked },
            "GPU-PRIORITY" => settings with { GpuPriority = asked },
            "GC" => settings with { Gc = asked },
            _ => settings with { Awake = asked },
        };
        words = "";
        return true;
    }

    /// <summary>What a role claims with MMCSS on and CPU sets on AUTO: render, present and capture join MMCSS on the critical pool,
    /// decode runs above normal on the critical pool, audio and timecode join Pro Audio, the control lane and the LED sender run above
    /// normal as they always have, network servers at normal, and background pools below normal at EcoQoS on the background pool.</summary>
    public static RoleClaims For(ThreadRole role) => role switch
    {
        ThreadRole.Render => new(role, ThreadLevel.AboveNormal, Games, CpuPool.Critical, false),
        ThreadRole.Present => new(role, ThreadLevel.AboveNormal, Games, CpuPool.Critical, false),
        ThreadRole.Capture => new(role, ThreadLevel.AboveNormal, Capture, CpuPool.Critical, false),
        ThreadRole.Decode => new(role, ThreadLevel.AboveNormal, null, CpuPool.Critical, false),
        ThreadRole.Audio => new(role, ThreadLevel.AboveNormal, ProAudio, CpuPool.None, false),
        ThreadRole.Timecode => new(role, ThreadLevel.AboveNormal, ProAudio, CpuPool.None, false),
        ThreadRole.Control => new(role, ThreadLevel.AboveNormal, null, CpuPool.None, false),
        ThreadRole.Led => new(role, ThreadLevel.AboveNormal, null, CpuPool.None, false),
        ThreadRole.Network => new(role, ThreadLevel.Normal, null, CpuPool.None, false),
        _ => new(role, ThreadLevel.BelowNormal, null, CpuPool.Background, true),
    };

    /// <summary>What a role claims under <paramref name="settings"/>: MMCSS OFF drops the task (the thread keeps its priority), CPU sets
    /// OFF places it on none.</summary>
    public static RoleClaims For(ThreadRole role, MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var claims = For(role);
        if (settings.Mmcss == "off")
            claims = claims with { MmcssTask = null };
        if (settings.CpuSets == "off")
            claims = claims with { Pool = CpuPool.None };
        return claims;
    }

    /// <summary>A priority's word in a claim: "belowNormal", "normal", "aboveNormal", "highest".</summary>
    public static string Word(ThreadLevel level) => level switch
    {
        ThreadLevel.BelowNormal => "belowNormal",
        ThreadLevel.AboveNormal => "aboveNormal",
        ThreadLevel.Highest => "highest",
        _ => "normal",
    };

    /// <summary>A pool's word: "none", "critical", "background".</summary>
    public static string Word(CpuPool pool) => pool switch
    {
        CpuPool.Critical => "critical",
        CpuPool.Background => "background",
        _ => "none",
    };
}
