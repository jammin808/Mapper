using System.Runtime.InteropServices;

namespace PlayMap.Media;

/// <summary>Where an optional native library is looked for: the absolute candidates (its files, or the folders that hold a set of
/// libraries), in the order they are tried, and the words for each value refused because it was not an absolute path.</summary>
public sealed record NativeSearch(IReadOnlyList<string> Candidates, IReadOnlyList<string> Refused)
{
    /// <summary>The refusals in one line ("" when there are none).</summary>
    public string RefusedWords => string.Join("; ", Refused);
}

/// <summary>The rule the node's native libraries are found by (FFmpeg's, NDI's runtime, OMT's libvmx): each candidate is an absolute
/// path, loaded by that full path, so the system's search by bare name is never asked — on Windows that search takes in the current
/// directory and PATH, and a copy planted in either would load into the engine. A relative value from a setting or a variable is
/// refused with the words, never read against the current directory.</summary>
internal static class NativePaths
{
    /// <summary>A setting's or a variable's value as a normalised absolute path; null when it is empty, and null with the words added
    /// to <paramref name="refused"/> when it is not absolute.</summary>
    public static string? Absolute(string? value, string source, List<string> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (Path.IsPathFullyQualified(trimmed))
            return Path.GetFullPath(trimmed);
        refused.Add($"{source} \"{trimmed}\" is refused: it is not an absolute path, and a relative one would be read against the current directory");
        return null;
    }

    /// <summary>A known folder of this machine with the parts below it, or null when the machine has none (an empty folder would
    /// leave the parts relative).</summary>
    public static string? Under(Environment.SpecialFolder folder, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var path = Environment.GetFolderPath(folder);
        if (!Path.IsPathFullyQualified(path))
            return null;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    /// <summary>The system's library folders, as absolute paths: on Linux /usr/lib, /usr/local/lib and the architecture's
    /// /usr/lib/&lt;triplet&gt;; on macOS /usr/local/lib; none on Windows, whose system folders hold none of these libraries.</summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        if (OperatingSystem.IsWindows())
            return [];
        if (OperatingSystem.IsMacOS())
            return ["/usr/local/lib"];
        var triplet = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64-linux-gnu",
            Architecture.Arm64 => "aarch64-linux-gnu",
            _ => "",
        };
        return triplet.Length > 0 ? ["/usr/lib", "/usr/local/lib", "/usr/lib/" + triplet] : ["/usr/lib", "/usr/local/lib"];
    }
}
