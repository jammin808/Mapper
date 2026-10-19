using System.Text.Json;
using PlayMap.Engine.Tests.VideoIo;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The round-3 packages' STATE fixtures (docs/round3/PLAN.md §3.1 item 5, §4): each exists and is a STATE object. Until its
/// package's own test writes it, it is the placeholder this test writes ({}): the package's keys are absent from the node's STATE (its
/// stub writes nothing), and it has no card descriptor beside it, since every descriptor file the desk builds in describes at least one
/// card from its fixture (EveryCardTests) and a package's descriptors arrive with the block they describe. Once the package's test has
/// written its sections, this test only reads that the fixture parses. PLAYMAP_WRITE_FIXTURES=1 writes a placeholder where the
/// fixture is absent or a placeholder, never over a package's.</summary>
public sealed class RoundThreeFixtureTests
{
    /// <summary>Each reserved fixture, its package and the STATE keys its package adds (a.b: b in the object a; a[].b: b in every
    /// element of the list a).</summary>
    public static readonly TheoryData<string, string, string[]> Reserved = new()
    {
        { "machine", "wp3-platform", ["machine.identity", "machine.capabilities", "machine.platform", "machine.locked"] },
        { "install", "wp3-installer", ["machine.install"] },
        { "shaders", "wp3-shaders", ["fx.quarantine"] },
        { "gpu", "wp3-d3d12-core", ["render"] },
        { "graph", "wp3-d3d12-passes", ["render.graph"] },
        { "observe", "wp3-stability", ["observe"] },
        { "previews", "wp3-desk-gpu", ["previews"] },
        { "showing", "wp3-desk-map", ["outputs[].showing", "media.shownOn", "air.overlay.handleP95Ms"] },
        { "canvas", "wp3-canvas", ["outputs[].hand"] },
        { "guides", "wp3-workflows", ["guides"] },
        { "capture", "wp3-capture", ["outputs[].passThrough", "videoio.inputs[].capture", "videoio.inputs[].latency"] },
    };

    private static string Folder => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state");

    private static bool IsPlaceholder(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.EnumerateObject().Any();
    }

    /// <summary>Whether STATE holds the key at <paramref name="path"/> (in any element, for a list's).</summary>
    private static bool Holds(JsonElement node, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? path : path[..dot];
        var rest = dot < 0 ? null : path[(dot + 1)..];
        var each = head.EndsWith("[]", StringComparison.Ordinal);
        if (each)
            head = head[..^2];
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(head, out var child))
            return false;
        if (rest is null)
            return true;
        return each ? child.ValueKind == JsonValueKind.Array && child.EnumerateArray().Any(e => Holds(e, rest)) : Holds(child, rest);
    }

    /// <summary>A node with an output, a canvas, a stage and a live input: every block a reserved key sits in is in its STATE.</summary>
    private static async Task<JsonDocument> StateWithEveryBlock()
    {
        await using var rig = new VideoIoRig();
        await rig.Ok("INPUT ADD omt://10.0.0.7:6400 Program");
        await rig.Ok("ADD projector {\"name\":\"Stage left\"}");
        return JsonDocument.Parse(rig.Engine.State().Json);
    }

    [Theory]
    [MemberData(nameof(Reserved))]
    public async Task EachReservedFixtureIsItsPackagesOrThePlaceholderWhileItsStubWritesNothing(string fixture, string package, string[] keys)
    {
        var path = Path.Combine(Folder, fixture + ".json");
        var cards = Path.Combine(Folder, fixture + ".cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1"
            && (!File.Exists(path) || IsPlaceholder(PlayMap.Core.Storage.DurableFile.ReadAllText(path))))
            PlayMap.Core.Storage.DurableFile.WriteAllText(path, "{}\n");
        Assert.True(File.Exists(path), $"{fixture}.json is reserved by the spine: run this test once with PLAYMAP_WRITE_FIXTURES=1");
        var json = PlayMap.Core.Storage.DurableFile.ReadAllText(path);
        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        if (!IsPlaceholder(json))
            return;

        Assert.Equal("{}", json.ReplaceLineEndings("\n").TrimEnd('\n'));
        Assert.False(File.Exists(cards), $"{fixture}.cards.json describes no block of the placeholder: {package}'s descriptors come with its block");
        using var state = await StateWithEveryBlock();
        foreach (var key in keys)
            Assert.False(Holds(state.RootElement, key), $"STATE holds {key}, so {package} writes {fixture}.json from its own test");
    }

    [Fact]
    public async Task EveryBlockAReservedKeySitsInIsInTheProbedState()
    {
        using var state = await StateWithEveryBlock();
        foreach (var block in new[] { "machine", "fx", "outputs[].id", "media", "air.overlay", "videoio.inputs[].id" })
            Assert.True(Holds(state.RootElement, block), block);
    }
}
