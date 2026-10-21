using System.Security.Cryptography;

namespace PlayMap.Core.Machine;

/// <summary>The names a supervisor and the engine it started share, beside the files in its folder (<see cref="EnginePidRecord"/>,
/// <see cref="SupervisorStatus"/>).</summary>
/// <remarks>The engine's stop event is made by the supervisor for each engine it starts, under a name no account could have made
/// before: <see cref="StopEventPrefix"/> and 32 hex digits a cryptographic generator draws for that start (<see cref="NewStopEvent"/>),
/// the name given to the engine on its command line (<see cref="StopEventOption"/>). A named object in the global namespace belongs to
/// whoever makes its name first, and any account may make an event there; a fixed name could be made by a standard user while no
/// process held it (between an upgrade's stop and the new service's start) and so keep the supervisor from making its own. A drawn
/// name is made with nothing there before it, or another is drawn, and every reader of a name — the engine as it starts, a supervisor
/// that adopts the engine, an elevated stop — takes only a name of this shape and only an event SYSTEM or administrators made
/// (<see cref="StopEventOwnerTrusted"/>).</remarks>
public static class SupervisorNames
{
    /// <summary>The start of every stop event's name on this machine; the rest is <see cref="StopEventDigits"/> lower-case hex
    /// digits.</summary>
    public const string StopEventPrefix = @"Global\PlayMap-Engine-Stop-";

    /// <summary>How many hex digits follow the prefix: 128 bits drawn for each engine start.</summary>
    public const int StopEventDigits = 32;

    /// <summary>The engine's option that names its stop event (<c>PlayMap.exe engine --supervised --stop-event &lt;name&gt;</c>), given
    /// by the supervisor that started it only.</summary>
    public const string StopEventOption = "--stop-event";

    /// <summary>A new stop event's name under <paramref name="prefix"/>: the prefix and <see cref="StopEventDigits"/> lower-case hex
    /// digits from <see cref="RandomNumberGenerator"/>, so no account can make it before the supervisor does.</summary>
    public static string NewStopEvent(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return prefix + RandomNumberGenerator.GetHexString(StopEventDigits, lowercase: true);
    }

    /// <summary>Whether <paramref name="name"/> is a name <see cref="NewStopEvent"/> makes under <paramref name="prefix"/>: the prefix,
    /// then exactly <see cref="StopEventDigits"/> lower-case hex digits and nothing else. Any other name — another program's event, the
    /// prefix alone — is never opened as a stop event, so an elevated stop never sets an event a command line chose.</summary>
    public static bool IsStopEvent(string? name, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (name is null || name.Length != prefix.Length + StopEventDigits || !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var c in name.AsSpan(prefix.Length))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        return true;
    }

    /// <summary>The stop event a process's command line names after <see cref="StopEventOption"/> (its last such option, as the engine
    /// reads it), when that is one <see cref="NewStopEvent"/> makes under <paramref name="prefix"/>; null otherwise.</summary>
    public static string? StopEventIn(string? commandLine, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (string.IsNullOrEmpty(commandLine))
            return null;
        var words = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.LastIndexOf(words, StopEventOption);
        return at >= 0 && at + 1 < words.Length && IsStopEvent(words[at + 1], prefix) ? words[at + 1] : null;
    }

    /// <summary>Whether a stop event's owner is SYSTEM or administrators, the only owners it is used from: an event any other account
    /// owns, that account could set or clear (or give itself the right to).</summary>
    public static bool StopEventOwnerTrusted(string? ownerSid) => ownerSid is "S-1-5-18" or "S-1-5-32-544";
}
