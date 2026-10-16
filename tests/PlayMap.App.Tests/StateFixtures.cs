using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.App.Link;

namespace PlayMap.App.Tests;

/// <summary>The packages' STATE fixtures (tests/fixtures/state/&lt;pkg&gt;.json, each written by one of its package's own
/// engine tests) and their card descriptors (&lt;pkg&gt;.cards.json), so the desk's picture of STATE cannot drift from the
/// engine's. A fixture is laid over the fake engine's own STATE section by section (objects merge, arrays of objects merge
/// by id). A fixture of several nodes (backup.json: a backup, its primary, the backup after FAILOVER, the old primary fenced) is
/// keyed by scenario, none of its keys a STATE key; it is laid one scenario at a time.</summary>
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

    /// <summary>The top-level keys of the whole STATE (main.json's).</summary>
    private static readonly Lazy<HashSet<string>> StateKeys = new(() =>
        [.. JsonNode.Parse(Read("main"))!.AsObject().Select(p => p.Key)]);

    /// <summary>A fixture's scenarios, each a set of STATE sections: one, named after its package, for a fixture of one node; one per
    /// key for a fixture keyed by scenario.</summary>
    public static IReadOnlyList<(string Name, JsonObject Sections)> Scenarios(string package)
    {
        var root = JsonNode.Parse(Read(package))!.AsObject();
        if (package != "main" && root.Count > 0 && root.All(p => p.Value is JsonObject && !StateKeys.Value.Contains(p.Key)))
            return [.. root.Select(p => (p.Key, p.Value!.AsObject()))];
        return [(package, root)];
    }

    /// <summary>Lays one fixture over the fake engine's STATE: its only scenario, the one named, or else its first.</summary>
    public static void Load(FakeEngineLink link, string package, string? scenario = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        var scenarios = Scenarios(package);
        var (_, sections) = scenario is null ? scenarios[0] : scenarios.Single(s => s.Name == scenario);
        foreach (var (name, value) in sections)
        {
            if (Identity.Contains(name) || (package == "main" && FakeOwned.Contains(name)))
                continue;
            link.SetSection(name, value?.ToJsonString() ?? "null");
        }
    }

    /// <summary>Lays every fixture present over the fake engine's STATE (main first, then each package's; a fixture of scenarios
    /// its first).</summary>
    public static void LoadAll(FakeEngineLink link)
    {
        foreach (var p in Packages)
            Load(link, p);
    }
}
