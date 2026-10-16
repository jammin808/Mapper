using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;
using PlayMap.Net.Companion;

namespace PlayMap.Net.Tests.Companion;

/// <summary>The contracts between the node and its Companion module (integrations/companion-module-playmap), held from the node's
/// side: the module's verbs.json is VERBS as the catalogue writes it (PLAYMAP_WRITE_FIXTURES=1 rewrites it); every line the module
/// can send (test/lines.txt, written by the module's own generator and checked current by its tests), and every section key and Live
/// slot line it fills from the node's INDEX (test/section-lines.txt, written the same way), parses and fits a typed form of its row;
/// the rows the module never carries inside a line are every row that carries a secret or pairs; every
/// STATE path the module reads (test/state-paths.txt) is in a STATE the node wrote; the module's version is one number in its
/// package, manifest and code; its line protocol is the node's; its palette is Patterns', byte for byte.</summary>
public sealed partial class CompanionContractTests
{
    /// <summary>SHA-256 of Patterns' src/palette.js (Patterns' module, the commit the design read): the colour language both decks
    /// speak. PlayMap adds kinds in palette/playmap.ts and never edits this file.</summary>
    private const string PatternsPaletteSha256 = "b0a8a8ce9c56b16ce8eee24bd59f34750d8089e4b0fe9a5e774d3df0c8ffb647";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
    }

    private static string Module(params string[] parts) =>
        Path.Combine([RepoRoot(), "integrations", "companion-module-playmap", .. parts]);

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    [Fact]
    public async Task TheModulesVerbsAreTheCatalogue()
    {
        var path = Module("src", "catalog", "verbs.json");
        var written = JsonNode.Parse(VerbJson.All)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            await File.WriteAllTextAsync(path, written);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VerbJson.All), JsonNode.Parse(Read(path))),
            "the module's verbs.json is VERBS as the catalogue writes it — PLAYMAP_WRITE_FIXTURES=1 dotnet test rewrites it");
    }

    [Fact]
    public void EveryLineTheModuleCanSendParsesAndFitsItsRow()
    {
        var lines = Read(Module("test", "lines.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 200, $"the module writes a line for every action it generates ({lines.Length})");
        var rows = new HashSet<string>(StringComparer.Ordinal);
        var refused = new List<string>();
        foreach (var text in lines)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), $"{text}: {error?.Reason}");
            Assert.Null(line.RequestId);
            var match = VerbCatalog.Match(line);
            Assert.True(match is not null, $"no row of the catalogue names '{text}'");
            // Every word after the verb fills a parameter of one of the row's typed forms, the forms VERBS hands a controller.
            if (!FormMatcher.Fits(match!.Value.Spec, line, match.Value.Words, out var why))
                refused.Add($"'{text}': {why}");
            Assert.False(match.Value.Spec.Internal, $"'{text}' is a node-to-node verb");
            Assert.False(VerbCatalog.HoldsSecret(line), $"'{text}' carries a secret");
            rows.Add(match.Value.Spec.Name);
        }
        Assert.True(refused.Count == 0, "lines that fill no typed form of their row:\n" + string.Join("\n", refused));
        // Every catalogue row the module generates an action for is exercised (session words and secret rows are the module's own or
        // never generated).
        using var verbs = JsonDocument.Parse(Read(Module("src", "catalog", "verbs.json")));
        var generated = verbs.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).Where(n => !NotGenerated(n)).ToList();
        var unexercised = generated.Where(n => !rows.Contains(n)).ToList();
        Assert.True(unexercised.Count == 0, "rows the module generates an action for but writes no line of: " + string.Join(", ", unexercised));
    }

    [Fact]
    public void EverySectionKeyLineTheModuleFillsFitsItsRow()
    {
        // test/section-lines.txt is every line the module sends from a section key or a Live slot for the node's INDEX fixture, filled
        // by the module's own fillHint (a row's id quoted when it holds a space; a hint that is "$(id)" alone is the row's own line),
        // for every row but an encoder's, with a sample row for a section that has none: each such line fills a typed form of its row.
        var lines = Read(Module("test", "section-lines.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 20, $"the module writes the section keys' lines ({lines.Length})");
        Assert.Contains("ALIGN RUN \"Front wall\"", lines);
        var refused = lines.Select(text => Refusal(text) is { } why ? $"'{text}': {why}" : null).OfType<string>().ToList();
        Assert.True(refused.Count == 0, "section key lines that fill no typed form of their row:\n" + string.Join("\n", refused));
    }

    /// <summary>Why a line a key sends is not one the node takes as written (it does not parse, names no row, fills none of the row's
    /// forms, is a node-to-node verb or carries a secret), or null.</summary>
    private static string? Refusal(string text)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return error?.Reason ?? "does not parse";
        if (line.RequestId is not null)
            return "carries a request id of its own";
        if (VerbCatalog.Match(line) is not { } match)
            return "no row of the catalogue names it";
        if (!FormMatcher.Fits(match.Spec, line, match.Words, out var why))
            return why;
        if (match.Spec.Internal)
            return "a node-to-node verb";
        return VerbCatalog.HoldsSecret(line) ? "carries a secret" : null;
    }

    /// <summary>The rows the module never makes an action of (it says them itself, or they carry a secret): the same list as the
    /// module's catalog/verbs.ts EXCLUDED.</summary>
    private static bool NotGenerated(string name)
    {
        var excluded = ExcludedList().Match(Read(Module("src", "catalog", "verbs.ts")));
        Assert.True(excluded.Success, "catalog/verbs.ts names its EXCLUDED rows");
        var names = Regex.Matches(excluded.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return names.Contains(name) || (VerbCatalog.Find(name) is { SecretFrom: not null });
    }

    [Fact]
    public void TheModulesSecretRowsAreEveryRowThatCarriesASecretOrPairs()
    {
        // The fence refuses a line that starts with one of these, and a line carried inside one (a binding's target) that holds one's
        // secret words: every row the catalogue redacts a secret from, and the rows that pair a session, are in the module's SECRET
        // set, every name there is a row of the catalogue, and its SECRET_FROM table is every SecretFrom row at its own position.
        var secret = SecretList().Match(Read(Module("src", "catalog", "verbs.ts")));
        Assert.True(secret.Success, "catalog/verbs.ts names its SECRET rows");
        var names = Regex.Matches(secret.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var expected = VerbCatalog.All.Where(v => v.SecretFrom is not null).Select(v => v.Name)
            .Concat(["AUTH-PROOF", "PAIRING NEW"]).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(names),
            $"the module's SECRET rows ({string.Join(", ", names.Order(StringComparer.Ordinal))}) are the catalogue's ({string.Join(", ", expected.Order(StringComparer.Ordinal))})");
        Assert.All(names, n => Assert.True(VerbCatalog.Find(n) is { } spec && spec.Name == n, $"{n} is a row of the catalogue"));
        Assert.All(names, n => Assert.True(NotGenerated(n), $"{n} is never a key's"));

        var table = SecretFromTable().Match(Read(Module("src", "catalog", "verbs.ts")));
        Assert.True(table.Success, "catalog/verbs.ts names its SECRET_FROM rows");
        var positions = SecretFromEntry().Matches(table.Groups[1].Value)
            .Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}").Order(StringComparer.Ordinal).ToList();
        var catalogue = VerbCatalog.All
            .Select(v => v.SecretFrom is int from ? $"{v.Name}={from.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : null)
            .OfType<string>().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(catalogue, positions);
    }

    [Fact]
    public void EveryStatePathTheModuleReadsIsInAStateTheNodeWrote()
    {
        var states = Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "state"), "*.json")
            .Where(f => !f.EndsWith(".cards.json", StringComparison.Ordinal))
            .Concat(Directory.GetFiles(Module("test", "fixtures"), "state*.json"))
            .Select(f => JsonNode.Parse(Read(f))!)
            .ToList();
        var paths = Read(Module("test", "state-paths.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(paths.Length > 40, $"the module lists the STATE paths it reads ({paths.Length})");
        var missing = paths.Where(p => !states.Any(s => Resolves(s, p.Split('.')))).ToList();
        Assert.True(missing.Count == 0, "STATE paths the module reads that no STATE written by the node holds: " + string.Join(", ", missing));
    }

    /// <summary>A dotted path where "[]" steps into any element of an array.</summary>
    private static bool Resolves(JsonNode? at, ArraySegment<string> parts)
    {
        if (parts.Count == 0)
            return true;
        var part = parts[0];
        if (part == "[]")
            return at is JsonArray array && array.Any(e => Resolves(e, parts[1..]));
        return at is JsonObject o && o.TryGetPropertyValue(part, out var next) && Resolves(next, parts[1..]);
    }

    [Fact]
    public void TheModulesVersionIsOneNumberAndItsLineProtocolIsTheNodes()
    {
        var package = JsonNode.Parse(Read(Module("package.json")))!;
        var manifest = JsonNode.Parse(Read(Module("companion", "manifest.json")))!;
        var code = Read(Module("src", "version.ts"));
        var version = package["version"]!.GetValue<string>();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal(version, manifest["version"]!.GetValue<string>());
        Assert.Equal(version, ModuleVersion().Match(code).Groups[1].Value);
        Assert.Equal(Controllers.LineProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture), LineProtocol().Match(code).Groups[1].Value);
        // The subprotocol the module asks for is the one the web port's line channel answers.
        Assert.Equal(PlayMap.Net.Wire.WebSocketLineChannel.SubProtocol, SubProtocol().Match(code).Groups[1].Value);
        Assert.Equal("jammin808-playmap", manifest["id"]!.GetValue<string>());
        Assert.Equal("MIT", package["license"]!.GetValue<string>());
        Assert.Equal("GPL-3.0-only", manifest["license"]!.GetValue<string>());
    }

    [Fact]
    public void ThePaletteIsPatternsByteForByte()
    {
        var bytes = File.ReadAllBytes(Module("src", "palette", "palette.js"));
        Assert.Equal(PatternsPaletteSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [GeneratedRegex(@"EXCLUDED[^=]*=\s*new Set\(\[([^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedList();

    [GeneratedRegex(@"const SECRET[^=]*=\s*new Set\(\[([^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SecretList();

    [GeneratedRegex(@"const SECRET_FROM[^=]*=\s*new Map\(\[(.*?)\]\)", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SecretFromTable();

    [GeneratedRegex(@"\[\s*'([^']+)'\s*,\s*(\d+)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex SecretFromEntry();

    [GeneratedRegex(@"MODULE_VERSION\s*=\s*'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleVersion();

    [GeneratedRegex(@"KNOWN_LINE_PROTOCOL\s*=\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LineProtocol();

    [GeneratedRegex(@"SUBPROTOCOL\s*=\s*'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex SubProtocol();
}
