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
/// can send (test/lines.txt, written by the module's own generator and checked current by its tests), and every section key line it
/// fills from the node's INDEX, parses and fits a typed form of its row; every
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

    /// <summary>Sample row ids for a section the fixture's show has no rows of, by the hint's row kind.</summary>
    private static readonly Dictionary<string, string> SampleRowIds = new(StringComparer.Ordinal)
    {
        ["align"] = "Front wall", ["projector"] = "pj0a1b2c3d", ["fx"] = "fx0a1b2c3d", ["output"] = "op0a1b2c3d", ["chase"] = "pl0a1b2c3d", ["lednode"] = "ln0a1b2c3d",
    };

    [Fact]
    public void EverySectionKeyLineTheModuleFillsFitsItsRow()
    {
        // The module sends a section key's line as the node's INDEX hint says, the row's id in for "$(id)" (quoted when it holds a
        // space; a hint that is "$(id)" alone is the row's own line), for every row but an encoder's, which a dial turns with its
        // value: each such line fills a typed form of its row.
        using var index = JsonDocument.Parse(Read(Module("test", "fixtures", "index.json")));
        var refused = new List<string>();
        var lines = 0;
        foreach (var section in index.RootElement.GetProperty("sections").EnumerateArray())
        {
            var hint = section.GetProperty("hint");
            var kind = hint.GetProperty("rowKind").GetString()!;
            var ids = section.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("kind").GetString() != "encoder")
                .Select(r => r.GetProperty("id").GetString()!).ToList();
            if (ids.Count == 0)
            {
                // A section whose key lines name no row ("SCAN NEXT") needs no sample row id.
                var named = hint.GetProperty("keys").EnumerateArray()
                    .Any(k => k.TryGetProperty("line", out var l) && l.GetString()?.Contains("$(id)", StringComparison.Ordinal) == true);
                Assert.True(SampleRowIds.TryGetValue(kind, out var sample) || !named,
                    $"section {section.GetProperty("section")} has no rows and no sample id for its row kind '{kind}'");
                ids.Add(sample ?? "");
            }
            foreach (var key in hint.GetProperty("keys").EnumerateArray())
            {
                if (!key.TryGetProperty("line", out var template) || template.GetString() is not { Length: > 0 } t)
                    continue;
                foreach (var id in ids)
                {
                    var text = t.Trim() == "$(id)" ? id : t.Replace("$(id)", Quote(id), StringComparison.Ordinal);
                    lines++;
                    if (Refusal(text) is { } why)
                        refused.Add($"'{text}' ({section.GetProperty("section")}): {why}");
                }
            }
        }
        Assert.True(lines > 20, $"the fixture's sections hint section keys ({lines} lines)");
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

    /// <summary>A word as the module's grammar quotes it: as it stands unless empty or holding a space or a quote mark.</summary>
    private static string Quote(string word) =>
        word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? word
            : "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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

    [GeneratedRegex(@"MODULE_VERSION\s*=\s*'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleVersion();

    [GeneratedRegex(@"KNOWN_LINE_PROTOCOL\s*=\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LineProtocol();

    [GeneratedRegex(@"SUBPROTOCOL\s*=\s*'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex SubProtocol();
}
