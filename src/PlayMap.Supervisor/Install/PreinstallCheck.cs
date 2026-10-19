using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Machine;

namespace PlayMap.Supervisor.Install;

/// <summary>What the installer's armed check found on this machine.</summary>
public enum EngineFinding
{
    /// <summary>No engine process (none in the pid record, or its process is gone, and none running from the installed exe).</summary>
    NoEngine,
    /// <summary>An engine answered on loopback: armed.</summary>
    Armed,
    /// <summary>An engine answered on loopback: not armed.</summary>
    Disarmed,
    /// <summary>An engine process is alive and did not answer.</summary>
    Silent,
}

/// <summary>The check's facts: what it found, the engine's pid (0 none), and, when it answered armed, by whom and since when (Unix
/// ms).</summary>
public readonly record struct PreinstallFacts(EngineFinding Finding, int Pid, string ArmedBy, long ArmedSinceUnixMs);

/// <summary>The check's answer: go on, or refused with words.</summary>
public sealed record PreinstallVerdict(bool GoOn, string Words)
{
    /// <summary>The exit code of a refusal (0 goes on).</summary>
    public const int RefusedExit = 2;
}

/// <summary>The installer's armed check (docs/round3/design/installer.md §4). It fails closed: no engine process goes on; an engine
/// that answers armed is refused with the node's words; one that answers disarmed goes on; an engine process that does not answer is
/// refused, naming its pid — silence is never read as "not armed".</summary>
public static class PreinstallCheck
{
    /// <summary>How long a live engine has to answer.</summary>
    public static readonly TimeSpan AnswerTime = TimeSpan.FromSeconds(5);

    /// <summary>The line wire's port an engine listens on when its pid record names none.</summary>
    public const int DefaultWirePort = 9941;

    /// <summary>Pure: the verdict on what was found. Times are given in <paramref name="zone"/> (the machine's own).</summary>
    public static PreinstallVerdict Decide(in PreinstallFacts facts, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return facts.Finding switch
        {
            EngineFinding.NoEngine => new PreinstallVerdict(true, "no engine runs on this machine: the install goes on"),
            EngineFinding.Disarmed => new PreinstallVerdict(true, string.Create(CultureInfo.InvariantCulture,
                $"the engine (pid {facts.Pid}) is not armed: the install goes on")),
            EngineFinding.Armed => new PreinstallVerdict(false, ArmedWords(facts, zone)),
            _ => new PreinstallVerdict(false, string.Create(CultureInfo.InvariantCulture,
                $"an engine is running here (pid {facts.Pid}) but does not answer: stop it from the desk or with PlayMap.Supervisor.exe stop, then install again")),
        };
    }

    private static string ArmedWords(in PreinstallFacts facts, TimeZoneInfo zone)
    {
        var since = facts.ArmedSinceUnixMs > 0
            ? " since " + TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(facts.ArmedSinceUnixMs).UtcDateTime, zone)
                .ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";
        var by = facts.ArmedBy.Length > 0 ? " by " + facts.ArmedBy : "";
        return $"the show is armed on this node{since}{by}: disarm first";
    }

    /// <summary>What a SHOW answer says: armed (with by and since), disarmed, or null when the answer is not SHOW's.</summary>
    public static PreinstallFacts? ReadShow(int pid, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("armed", out var armed)
                || armed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            if (armed.ValueKind == JsonValueKind.False)
                return new PreinstallFacts(EngineFinding.Disarmed, pid, "", 0);
            var by = r.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            var since = r.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var t) ? t : 0;
            return new PreinstallFacts(EngineFinding.Armed, pid, by, since);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Looks on this machine: the pid record in <paramref name="supervisorFolder"/>, then any engine process
    /// <paramref name="findEngine"/> names (the installed exe run as an engine); a live one is asked SHOW on loopback.</summary>
    public static async Task<PreinstallFacts> LookAsync(string supervisorFolder, Func<(int Pid, int Port)?> findEngine, TimeSpan answerTime,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findEngine);
        int pid = 0, port = DefaultWirePort;
        if (Directory.Exists(supervisorFolder) && EnginePidRecord.Named(supervisorFolder) is { } record && IsAlive(record.Pid))
        {
            pid = record.Pid;
            port = record.WirePort > 0 ? record.WirePort : DefaultWirePort;
        }
        else if (findEngine() is { } found)
        {
            pid = found.Pid;
            port = found.Port > 0 ? found.Port : DefaultWirePort;
        }
        if (pid == 0)
            return new PreinstallFacts(EngineFinding.NoEngine, 0, "", 0);
        var answer = await LoopbackWire.AskAsync(port, "SHOW STATUS", answerTime, ct).ConfigureAwait(false);
        return answer is { Ok: true } && ReadShow(pid, answer.Payload) is { } show
            ? show
            : new PreinstallFacts(EngineFinding.Silent, pid, "", 0);
    }

    /// <summary>Whether a process of that id runs. One this process may not look at is read as running: the check fails closed.</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }
}
