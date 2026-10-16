using System.Text.Json;
using PlayMap.App.Link;

namespace PlayMap.App.Tests;

/// <summary>The packages' STATE fixtures (tests/fixtures/state/&lt;pkg&gt;.json, each written by one of its package's own
/// engine tests) and their card descriptors (&lt;pkg&gt;.cards.json), so the desk's picture of STATE cannot drift from the
/// engine's. A fixture is laid over the fake engine's own STATE section by section (objects merge, arrays of objects merge
/// by id).</summary>
public static class StateFixtures
{
    /// <summary>The keys the fake engine writes itself from its project and transports: main.json's copies are left out
    /// (they describe another project), a package fixture's are merged.</summary>
    private static readonly HashSet<string> Identity = new(StringComparer.Ordinal) { "v", "seq", "id", "t", "name", "version", "at", "light", "headline" };

    private static readonly HashSet<string> FakeOwned = new(StringComparer.Ordinal)
    {
        "video", "playlists", "outputs", "media", "stage", "scan", "sync", "links", "peers", "project", "machine", "ai",
    };

    public static string Folder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "state")))
                dir = dir.Parent;
            return dir is null
                ? throw new DirectoryNotFoundException("tests/fixtures/state not found above " + AppContext.BaseDirectory)
                : Path.Combine(dir.FullName, "tests", "fixtures", "state");
        }
    }

    /// <summary>Every package name with a STATE fixture (the file name without .json), main first.</summary>
    public static IReadOnlyList<string> Packages =>
        [.. Directory.GetFiles(Folder, "*.json").Select(Path.GetFileName).Where(f => !f!.EndsWith(".cards.json", StringComparison.Ordinal))
            .Select(f => f![..^5]).OrderBy(n => n == "main" ? "" : n, StringComparer.Ordinal)];

    /// <summary>Every card descriptor file present: (package, its JSON).</summary>
    public static IReadOnlyList<(string Package, string Json)> Cards =>
        [.. Directory.GetFiles(Folder, "*.cards.json").Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f)[..^".cards.json".Length], File.ReadAllText(f)))];

    public static string Read(string package) => File.ReadAllText(Path.Combine(Folder, package + ".json"));

    /// <summary>Lays one fixture over the fake engine's STATE.</summary>
    public static void Load(FakeEngineLink link, string package)
    {
        ArgumentNullException.ThrowIfNull(link);
        using var doc = JsonDocument.Parse(Read(package));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (Identity.Contains(p.Name) || (package == "main" && FakeOwned.Contains(p.Name)))
                continue;
            link.SetSection(p.Name, p.Value.GetRawText());
        }
    }

    /// <summary>Lays every fixture present over the fake engine's STATE (main first, then each package's).</summary>
    public static void LoadAll(FakeEngineLink link)
    {
        foreach (var p in Packages)
            Load(link, p);
    }
}
