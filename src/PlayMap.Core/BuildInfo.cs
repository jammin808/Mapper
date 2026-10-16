using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PlayMap.Core;

/// <summary>Which build of PlayMap this is, as the stamp every PlayMap assembly carries says (Directory.Build.props writes the
/// product version once; CI numbers each build and names its commit; a build without them is a local build). The one reader of
/// the stamp: everything that shows a version takes it from here.
/// <list type="bullet">
/// <item><see cref="Version"/>: the product version, SemVer MAJOR.MINOR.PATCH — the same for every build of a release
/// (STATE's "version").</item>
/// <item><see cref="Build"/>: CI's build number, 1–65535; 0 is a local build.</item>
/// <item><see cref="Commit"/>: the commit built, seven hex digits; empty for a local build outside a git checkout.</item>
/// <item><see cref="Informational"/>: the whole identity in SemVer — <c>0.2.0+17.abc1234</c> from CI, <c>0.2.0-dev+abc1234</c>
/// local (HEALTH's "build").</item>
/// </list></summary>
public sealed partial record BuildInfo(string Version, int Build, string Commit)
{
    /// <summary>The most a Windows file version's part holds: the build number's ceiling.</summary>
    public const int MaxBuild = 65535;

    /// <summary>This process's build, read once from PlayMap.Core's own stamp (every PlayMap assembly of a build carries the
    /// same one).</summary>
    public static BuildInfo Current { get; } =
        Parse(typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>A local build: one no CI numbered.</summary>
    public bool IsLocal => Build == 0;

    /// <summary>The SemVer string the assemblies carry as their informational version (and Windows as ProductVersion).</summary>
    public string Informational =>
        IsLocal
            ? Version + "-dev" + (Commit.Length > 0 ? "+" + Commit : "")
            : string.Create(CultureInfo.InvariantCulture, $"{Version}+{Build}.{Commit}");

    /// <summary>Windows' four-part file version: MAJOR.MINOR.PATCH.BUILD.</summary>
    public string FileVersion => string.Create(CultureInfo.InvariantCulture, $"{Version}.{Build}");

    /// <summary>The words a person reads: "PlayMap 0.2.0 (build 17, abc1234)", "PlayMap 0.2.0-dev (local build, abc1234)".</summary>
    public string Display =>
        IsLocal
            ? $"PlayMap {Version}-dev (local build{(Commit.Length > 0 ? ", " + Commit : "")})"
            : string.Create(CultureInfo.InvariantCulture, $"PlayMap {Version} (build {Build}, {Commit})");

    /// <summary>The short form a title bar carries: "PlayMap 0.2.0", "PlayMap 0.2.0-dev".</summary>
    public string Short => IsLocal ? $"PlayMap {Version}-dev" : $"PlayMap {Version}";

    /// <summary>Reads an informational version in either of the stamp's shapes. Anything else — no stamp, a foreign one — reads as
    /// a local build of the version it starts with (0.0.0 when it starts with none), never as a numbered build: a build number and
    /// a commit are only ever what CI stamped.</summary>
    public static BuildInfo Parse(string? informational)
    {
        var text = informational?.Trim() ?? "";
        var numbered = NumberedStamp().Match(text);
        if (numbered.Success
            && int.TryParse(numbered.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var build)
            && build is >= 1 and <= MaxBuild)
            return new BuildInfo(numbered.Groups[1].Value, build, numbered.Groups[3].Value);
        var local = LocalStamp().Match(text);
        if (local.Success)
            return new BuildInfo(local.Groups[1].Value, 0, local.Groups[2].Value);
        var leading = LeadingVersion().Match(text);
        return new BuildInfo(leading.Success ? leading.Groups[1].Value : "0.0.0", 0, "");
    }

    [GeneratedRegex(@"^(\d{1,5}\.\d{1,5}\.\d{1,5})\+(\d{1,5})\.([0-9a-f]{7})$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStamp();

    [GeneratedRegex(@"^(\d{1,5}\.\d{1,5}\.\d{1,5})-dev(?:\+([0-9a-f]{7}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalStamp();

    [GeneratedRegex(@"^(\d{1,5}\.\d{1,5}\.\d{1,5})(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersion();
}
