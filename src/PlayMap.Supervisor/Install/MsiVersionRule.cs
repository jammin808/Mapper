using System.Globalization;

namespace PlayMap.Supervisor.Install;

/// <summary>The installer's version rules (docs/round3/PLAN.md §1.3): MAJOR.MINOR.BUILD from a numbered build, a major and a minor of
/// at most 255, a build of 1–65535; and a release's version greater than the last release's, since Windows Installer orders builds by
/// those three fields alone.</summary>
public static class MsiVersionRule
{
    /// <summary>The most an installer's major or minor holds.</summary>
    public const int MaxMajorMinor = 255;

    /// <summary>The installer's version of product version <paramref name="version"/> (SemVer MAJOR.MINOR.PATCH) at build
    /// <paramref name="build"/>, or null with <paramref name="why"/> when no installer may carry it.</summary>
    public static string? Compose(string version, int build, out string why)
    {
        why = "";
        if (!System.Version.TryParse(version, out var v) || v.Build < 0 || v.Revision >= 0)
        {
            why = $"'{version}' is not MAJOR.MINOR.PATCH";
            return null;
        }
        if (v.Major > MaxMajorMinor || v.Minor > MaxMajorMinor)
        {
            why = string.Create(CultureInfo.InvariantCulture,
                $"{version} has a major or minor above {MaxMajorMinor}, the most an installer's version holds");
            return null;
        }
        if (build < 1)
        {
            why = "an installer is made from a numbered build only: a local build's would read as "
                + string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.0") + " and could not be upgraded in order";
            return null;
        }
        if (build > Core.BuildInfo.MaxBuild)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"build {build} is past {Core.BuildInfo.MaxBuild}");
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{build}");
    }

    /// <summary>Why release <paramref name="next"/> may not follow the last release <paramref name="last"/> (both MAJOR.MINOR.BUILD;
    /// <paramref name="last"/> empty when there is none yet), or null when it may.</summary>
    public static string? Refusal(string next, string last)
    {
        if (!TryParse(next, out var n))
            return $"'{next}' is not an installer version (MAJOR.MINOR.BUILD)";
        if (string.IsNullOrEmpty(last))
            return null;
        if (!TryParse(last, out var l))
            return $"the last release's version '{last}' is not an installer version (MAJOR.MINOR.BUILD)";
        if (n > l)
            return null;
        var raise = n.Major == l.Major && n.Minor == l.Minor ? ": raise MINOR (Version.props) and set PlayMapRunBase to the last run's number" : "";
        return n == l
            ? $"{next} is the last release's version: a release is greater than the one before it{raise}"
            : $"{next} is lower than the last release {last}{raise}";
    }

    private static bool TryParse(string text, out System.Version version)
    {
        version = new System.Version();
        if (string.IsNullOrEmpty(text) || text.Split('.').Length != 3 || !System.Version.TryParse(text, out var v)
            || v.Major > MaxMajorMinor || v.Minor > MaxMajorMinor || v.Build > Core.BuildInfo.MaxBuild)
            return false;
        version = v;
        return true;
    }
}
