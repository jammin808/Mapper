using System.Security.Cryptography;

namespace PlayMap.Installer.Tests;

/// <summary>The unsigned build as the job publishes it (docs/round3/PLAN.md §3.5 item 6): the installer is named <c>-unsigned</c>, the
/// job's summary carries the notice, and <c>SHA256SUMS</c> lists every file of the artifact with the hash it has — no file left out,
/// none listed that is not there. PLAYMAP_ARTIFACT_DIR is the artifact's folder, GITHUB_STEP_SUMMARY the job's summary.</summary>
public sealed class UnsignedArtifactTests
{
    private static string Artifact => Environment.GetEnvironmentVariable("PLAYMAP_ARTIFACT_DIR") is { Length: > 0 } d
        ? d
        : throw new InvalidOperationException("PLAYMAP_ARTIFACT_DIR is not set: the installer job sets it");

    [Fact]
    public void TheInstallerIsNamedUnsigned()
    {
        var installers = Directory.GetFiles(Artifact, "PlayMap-*-win-x64*.exe");
        Assert.NotEmpty(installers);
        Assert.All(installers, i => Assert.Matches(@"^PlayMap-\d+\.\d+\.\d+\.\d+-win-x64-unsigned\.exe$", Path.GetFileName(i)));
    }

    [Fact]
    public void SumsListEveryFileWithItsHash()
    {
        var sums = Path.Combine(Artifact, "SHA256SUMS");
        Assert.True(File.Exists(sums));
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(sums).Where(l => l.Length > 0))
        {
            Assert.Matches("^[0-9a-f]{64}  [^ ].*$", line);
            listed.Add(line[66..], line[..64]);
        }
        var files = Directory.GetFiles(Artifact, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Artifact, f).Replace('\\', '/'))
            .Where(f => f != "SHA256SUMS")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(files, listed.Keys.Order(StringComparer.Ordinal).ToList());
        foreach (var (name, hash) in listed)
        {
            using var stream = File.OpenRead(Path.Combine(Artifact, name));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    [Fact]
    public void ThePayloadsSumsRideBesideTheInstaller()
    {
        // The artifact's SHA256SUMS covers SHA256SUMS.payload, the sums of every file the installer lays down (its {app}\SHA256SUMS).
        var payload = Path.Combine(Artifact, "SHA256SUMS.payload");
        Assert.True(File.Exists(payload));
        var names = File.ReadAllLines(payload).Where(l => l.Length > 66).Select(l => l[66..]).ToList();
        foreach (var name in new[] { "PlayMap.exe", "Supervisor/PlayMap.Supervisor.exe", "D3D12/D3D12Core.dll", "ffmpeg/avcodec-63.dll", "ffmpeg/LICENSE.txt",
                     "THIRD-PARTY-NOTICES.md", "licence.txt" })
            Assert.Contains(name, names);
    }

    [Fact]
    public void TheSummaryCarriesTheUnsignedNotice()
    {
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        Assert.False(string.IsNullOrEmpty(summary), "GITHUB_STEP_SUMMARY is not set: the job sets it");
        Assert.Contains("unsigned: the owner's decision — PLAN.md §9 decision 2", File.ReadAllText(summary), StringComparison.Ordinal);
    }
}
