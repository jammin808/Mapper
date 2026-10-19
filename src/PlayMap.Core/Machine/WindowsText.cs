using System.Buffers.Binary;

namespace PlayMap.Core.Machine;

/// <summary>Windows Firewall's rules as the registry keeps them (the local store <c>FirewallPolicy\FirewallRules</c> and the policy
/// store a GPO writes: one string per rule, <c>v2.30|Action=Allow|Active=TRUE|Dir=In|Profile=Public|App=C:\…\PlayMap.exe|Name=…|</c>),
/// read as text, pure.</summary>
public static class FirewallRules
{
    /// <summary>Whether any rule lets <paramref name="exePath"/> in on the Public profile: active, allow, inbound, for that program
    /// (or for every program: no App key), on Public or on every profile (no Profile key; a rule may list several). A rule's program
    /// path may carry environment variables (<c>%ProgramFiles%\…</c>): <paramref name="expand"/> expands them (the system's, given by
    /// the caller) before the paths are compared.</summary>
    public static bool CoverPublic(IEnumerable<string> rules, string exePath, Func<string, string>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(exePath);
        expand ??= static s => s;
        foreach (var rule in rules)
        {
            var keys = Parse(rule);
            if (!Is(keys, "Action", "Allow") || !Is(keys, "Active", "TRUE") || !Is(keys, "Dir", "In"))
                continue;
            if (keys.TryGetValue("App", out var apps) && !apps.Exists(a => string.Equals(expand(a), exePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (keys.TryGetValue("Profile", out var profiles) && !profiles.Exists(p => string.Equals(p, "Public", StringComparison.OrdinalIgnoreCase)))
                continue;
            return true;
        }
        return false;
    }

    private static bool Is(Dictionary<string, List<string>> keys, string key, string value) =>
        keys.TryGetValue(key, out var values) && values.Exists(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, List<string>> Parse(string rule)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (rule ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = part[..eq];
            if (!keys.TryGetValue(key, out var values))
                keys[key] = values = [];
            values.Add(part[(eq + 1)..]);
        }
        return keys;
    }
}

/// <summary>A Windows executable's header, read as bytes, pure: whether it carries an Authenticode signature (its certificate table,
/// the security data directory, is not empty). Says nothing about whether the signature is valid or trusted.</summary>
public static class PeImage
{
    public static bool HasCertificateTable(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
            return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        if (pe < 0 || pe + 24 + 2 > image.Length || BinaryPrimitives.ReadUInt32LittleEndian(image[pe..]) != 0x00004550)
            return false;
        var optional = pe + 24;
        // The data directories follow the optional header's fixed part: 96 bytes in PE32, 112 in PE32+; security is the fifth.
        var directories = BinaryPrimitives.ReadUInt16LittleEndian(image[optional..]) switch
        {
            0x10b => optional + 96,
            0x20b => optional + 112,
            _ => -1,
        };
        var security = directories + 4 * 8;
        if (directories < 0 || security + 8 > image.Length)
            return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(image[(security + 4)..]) > 0;
    }
}

/// <summary>The screen saver as two readings decide it, pure: the flag (<c>SPI_GETSCREENSAVEACTIVE</c>, null when unread), which reads
/// TRUE with "(None)" chosen, and whether a screen saver is chosen (<c>SCRNSAVE.EXE</c>, null when unread).</summary>
public static class ScreenSaverReading
{
    /// <summary>1 on (the flag set and a screen saver chosen), 0 off (either says no), −1 unknown (the readings do not decide).</summary>
    public static int State(bool? active, bool? chosen) => (active, chosen) switch
    {
        (_, false) => 0,
        (false, _) => 0,
        (true, true) => 1,
        _ => -1,
    };
}
