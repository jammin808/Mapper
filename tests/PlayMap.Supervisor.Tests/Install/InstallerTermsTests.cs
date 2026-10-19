using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The terms the installer's licence page shows (installer/licence.txt, laid down as <c>{app}\licence.txt</c>): headed as the
/// terms for the software the installer lays down, a marked section for PlayMap's own terms that states none, Microsoft's licence
/// terms for D3D12Core.dll copied byte for byte from the Agility SDK package tools/ci-d3d12.ps1 pins, and pointers to the notices the
/// payload carries; the script shows that page as Inno Setup's own, with nothing that skips it or answers it; and THIRD-PARTY-NOTICES.md
/// names every package whose files a publish carries. The package is the one the restore downloaded (this project's PackageDownload),
/// read only once its SHA-256 is the pin's. The installer job proves the other half: the built installer's page holds these terms,
/// the install keeps them, and a quiet install without /ACCEPTTERMS=yes is refused (InstallerTests).</summary>
public sealed partial class InstallerTermsTests
{
    private const string Heading = "PlayMap: the terms for the software this installer lays down";

    private static readonly byte[] Rule = Encoding.ASCII.GetBytes(new string('-', 78) + "\r\n");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
    }

    private static byte[] Terms() => File.ReadAllBytes(Path.Combine(Root(), "installer", "licence.txt"));

    [GeneratedRegex(@"Id = 'Microsoft\.Direct3D\.D3D12'; Version = '(?<version>[0-9.]+)'; Sha256 = '(?<sha>[0-9A-F]{64})'", RegexOptions.CultureInvariant)]
    private static partial Regex AgilityPin();

    [GeneratedRegex(@"^LicenseFile=\{#Payload\}\\licence\.txt\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LicenceFileDirective();

    [GeneratedRegex(@"^\[(?<name>\w+)\]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Section();

    /// <summary>LICENSE.txt as the pinned package holds it, read from the .nupkg itself once its SHA-256 is tools/ci-d3d12.ps1's pin.</summary>
    private static byte[] PinnedLicence()
    {
        var pin = AgilityPin().Match(File.ReadAllText(Path.Combine(Root(), "tools", "ci-d3d12.ps1")));
        Assert.True(pin.Success, "tools/ci-d3d12.ps1 pins no Microsoft.Direct3D.D3D12");
        var version = pin.Groups["version"].Value;
        var packages = typeof(InstallerTermsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "NuGetPackageRoot").Value;
        Assert.False(string.IsNullOrEmpty(packages), "the build recorded no NuGetPackageRoot");
        var nupkg = Path.Combine(packages, "microsoft.direct3d.d3d12", version, "microsoft.direct3d.d3d12." + version + ".nupkg");
        Assert.True(File.Exists(nupkg), nupkg + " is not restored: this project's PackageDownload names the pin's version");
        using (var file = File.OpenRead(nupkg))
            Assert.Equal(pin.Groups["sha"].Value, Convert.ToHexString(SHA256.HashData(file)));
        using var zip = ZipFile.OpenRead(nupkg);
        var entry = zip.GetEntry("LICENSE.txt");
        Assert.True(entry is not null, nupkg + " holds no LICENSE.txt");
        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>The bytes from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
    private static byte[] Between(byte[] text, string from, string to)
    {
        var start = text.AsSpan().IndexOf(Encoding.ASCII.GetBytes(from));
        Assert.True(start >= 0, "no \"" + from + "\" in Microsoft's LICENSE.txt");
        var length = text.AsSpan(start).IndexOf(Encoding.ASCII.GetBytes(to));
        Assert.True(length > 0, "no \"" + to + "\" after \"" + from + "\" in Microsoft's LICENSE.txt");
        return text.AsSpan(start, length).ToArray();
    }

    [LinuxFact]
    public void TheTermsHoldMicrosoftsDistributableCodeSectionByteForByteFromThePinnedPackage()
    {
        var licence = PinnedLicence();
        var section = Between(licence, "DISTRIBUTABLE CODE.", "SCOPE OF LICENSE.");
        var said = Encoding.UTF8.GetString(section);
        Assert.Contains("require distributors and external end users to agree to terms that protect it and Microsoft at least as much as this agreement", said, StringComparison.Ordinal);
        Assert.Contains("indemnify, defend, and hold harmless Microsoft", said, StringComparison.Ordinal);
        var terms = Terms();
        Assert.True(terms.AsSpan().IndexOf(section) >= 0, "installer/licence.txt does not hold the DISTRIBUTABLE CODE section byte for byte");
        // Microsoft's terms whole, once, between the two rules of dashes: what a person agrees to protects Microsoft as much as they do.
        var at = terms.AsSpan().IndexOf(licence);
        Assert.True(at >= 0, "installer/licence.txt does not hold the pinned package's LICENSE.txt byte for byte");
        Assert.Equal(-1, terms.AsSpan(at + 1).IndexOf(licence));
        Assert.True(terms.AsSpan(0, at).EndsWith(Rule), "no rule of dashes before Microsoft's terms");
        Assert.True(terms.AsSpan(at + licence.Length).StartsWith(Rule), "no rule of dashes after Microsoft's terms");
    }

    [Fact]
    public void TheTermsAreHeadedForTheInstallAndStateNoTermsOfPlayMapsOwn()
    {
        var terms = Terms();
        // UTF-8 with its byte-order mark, which Inno Setup's licence page reads as UTF-8; every line ends CRLF, as Microsoft's do.
        Assert.True(terms.AsSpan().StartsWith("\uFEFF"u8), "installer/licence.txt carries no UTF-8 byte-order mark");
        var text = Encoding.UTF8.GetString(terms.AsSpan(3));
        Assert.StartsWith(Heading + "\r\n", text, StringComparison.Ordinal);
        var unbroken = text.Replace("\r\n", "", StringComparison.Ordinal);
        Assert.False(unbroken.Contains('\n', StringComparison.Ordinal) || unbroken.Contains('\r', StringComparison.Ordinal), "a line of installer/licence.txt does not end CRLF");
        Assert.Contains("A quiet install (/SILENT or /VERYSILENT) shows no page: it lays nothing down unless its command line carries /ACCEPTTERMS=yes.",
            text, StringComparison.Ordinal);

        // Section 1 holds PlayMap's own terms and states that none are named: no licence of PlayMap's own is named anywhere before
        // Microsoft's terms begin.
        var own = text.IndexOf("1. PLAYMAP'S OWN TERMS", StringComparison.Ordinal);
        var microsoft = text.IndexOf("2. MICROSOFT DIRECTX AGILITY SDK: D3D12Core.dll", StringComparison.Ordinal);
        var others = text.IndexOf("3. THE OTHER COMPONENTS", StringComparison.Ordinal);
        Assert.True(own > 0 && microsoft > own && others > microsoft, "the three sections are not in order");
        Assert.Contains("Their owner has not named any yet, so none are stated here.", text[own..microsoft], StringComparison.Ordinal);
        Assert.DoesNotContain("license", text[..microsoft], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("licence", text[..microsoft], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("By accepting, you agree to the following terms for that component.", text[microsoft..others], StringComparison.Ordinal);

        // Section 3 points at the notices and licence texts the payload carries, each laid down by build-installer.ps1 or by the
        // notices.ps1 it runs on the payload.
        var build = File.ReadAllText(Path.Combine(Root(), "installer", "build-installer.ps1"));
        Assert.Contains("& (Join-Path $PSScriptRoot 'notices.ps1') -Folder $payload", build, StringComparison.Ordinal);
        build += File.ReadAllText(Path.Combine(Root(), "installer", "notices.ps1"));
        foreach (var (named, laid) in new[]
                 {
                     ("THIRD-PARTY-NOTICES.md", "'THIRD-PARTY-NOTICES.md'"), (@"licenses\:", "'licenses'"),
                     (@"ffmpeg\LICENSE.txt", @"'ffmpeg\LICENSE.txt'"), (@"ffmpeg\GPL-3.0.txt", @"'ffmpeg\GPL-3.0.txt'"),
                     ("soft_oal-LICENSE.txt", "'soft_oal-LICENSE.txt'"), ("libvmx-LICENSE.txt", "'libvmx-LICENSE.txt'"),
                     ("SHA256SUMS", "sums.ps1"),
                 })
        {
            Assert.Contains(named, text[others..], StringComparison.Ordinal);
            Assert.Contains(laid, build, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheInstallerShowsThePayloadsTermsOnItsLicencePage()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "installer", "PlayMap.iss"));
        var directive = LicenceFileDirective().Match(script);
        Assert.True(directive.Success, "PlayMap.iss shows no licence page of the payload's licence.txt");
        var setup = Section().Matches(script).Last(m => m.Index < directive.Index);
        Assert.Equal("Setup", setup.Groups["name"].Value);
        // The payload's copy is the repository's file, so the page shows what the install keeps and SHA256SUMS lists.
        Assert.Contains("Copy-Item (Join-Path $PSScriptRoot 'licence.txt') (Join-Path $payload 'licence.txt')",
            File.ReadAllText(Path.Combine(Root(), "installer", "build-installer.ps1")), StringComparison.Ordinal);
    }
    /// <summary>The licence page is Inno Setup's own, shown for [Setup]'s LicenseFile: the wizard goes on only once "I accept" is
    /// chosen. Nothing in the script skips the page, chooses its answer or reaches it, and no other directive names a licence file in
    /// [Setup]'s place (a [Languages] entry's LicenseFile:) or rewrites the page's choices ([Messages]' LicenseAccepted and
    /// LicenseNotAccepted). A quiet install shows no page at all: InstallerTests proves the refusal that stands in for it.</summary>
    [Fact]
    public void NothingInTheScriptSkipsTheLicencePageOrAnswersItForAPerson()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "installer", "PlayMap.iss"));
        foreach (var word in new[] { "ShouldSkipPage", "LicenseAccepted", "LicenseNotAccepted", "wpLicense" })
            Assert.DoesNotContain(word, script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(LicenceFileWord().Matches(script));
        Assert.True(LicenceFileDirective().IsMatch(script), "PlayMap.iss shows no licence page of the payload's licence.txt");
    }

    [GeneratedRegex("LicenseFile", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LicenceFileWord();

    /// <summary>The packages whose files a win-x64 publish of <paramref name="project"/> carries, from its restore: those with managed,
    /// native or satellite assemblies for every platform, or native files for Windows on x64.</summary>
    private static List<string> Carried(string project)
    {
        var assets = Path.Combine(Root(), "src", project, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is not there: restore the solution first");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(assets));
        var carried = new List<string>();
        foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
            foreach (var library in target.Value.EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "package")
                    continue;
                var windows = library.Value.TryGetProperty("runtimeTargets", out var rids)
                    && rids.EnumerateObject().Any(r => r.Value.GetProperty("rid").GetString() is "win-x64" or "win");
                if (windows || Carries(library.Value, "runtime") || Carries(library.Value, "native") || Carries(library.Value, "resource"))
                    carried.Add(library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)]);
            }
        return carried;
    }

    private static bool Carries(JsonElement library, string kind) =>
        library.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("/_._", StringComparison.Ordinal));

    /// <summary>THIRD-PARTY-NOTICES.md names a package by its id in backticks, or by a family of ids in backticks as <c>Prefix.*</c>:
    /// the rule installer/notices.ps1 holds the publish itself to.</summary>
    private static bool Named(string notices, string id)
    {
        if (notices.Contains('`' + id + '`', StringComparison.Ordinal))
            return true;
        var parts = id.Split('.');
        for (var n = parts.Length; n >= 1; n--)
        {
            if (notices.Contains('`' + string.Join('.', parts[..n]) + ".*`", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    [Fact]
    public void TheNoticesNameEveryPackageAPublishCarriesAndTheTextsTheyPointAt()
    {
        var notices = File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.md"));
        var carried = Carried("PlayMap.App").Concat(Carried("PlayMap.Supervisor")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.Contains("Avalonia", carried);
        Assert.Contains("System.ServiceProcess.ServiceController", carried);
        var unnamed = carried.Where(id => !Named(notices, id)).ToList();
        Assert.True(unnamed.Count == 0, "THIRD-PARTY-NOTICES.md does not name " + string.Join(", ", unnamed) + ", which a publish carries");
        // The self-contained publish's runtime packs, which a restore without a runtime identifier does not list.
        Assert.True(Named(notices, "Microsoft.NETCore.App.Runtime.win-x64") && Named(notices, "Microsoft.AspNetCore.App.Runtime.win-x64"),
            "THIRD-PARTY-NOTICES.md does not name the .NET runtime packs a self-contained publish carries");
        // The Apache License 2.0 the MCP SDK's notice points at is the Apache Software Foundation's text, byte for byte; notices.ps1
        // lays it down with Inter's licence.
        using (var apache = File.OpenRead(Path.Combine(Root(), "installer", "licenses", "Apache-2.0.txt")))
            Assert.Equal("CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30", Convert.ToHexString(SHA256.HashData(apache)));
        var script = File.ReadAllText(Path.Combine(Root(), "installer", "notices.ps1"));
        Assert.Contains("'licenses/Apache-2.0.txt'", script, StringComparison.Ordinal);
        Assert.Contains("'src/PlayMap.Render/Assets/Inter-LICENSE-OFL.txt'", script, StringComparison.Ordinal);
    }
}
