using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Control;

/// <summary>OSC 1.0 (opensoundcontrol.org, "The Open Sound Control 1.0 Specification": atomic data types, OSC-string padding, type
/// tag strings, bundles and timetags, address pattern matching) read and written, and the node's address map.</summary>
public sealed class OscCodecTests
{
    [Fact]
    public void AMessageRoundTripsWithEveryTag()
    {
        var m = OscMessage.Of("/all", OscArg.I(-7), OscArg.F(0.25f), OscArg.S("abc"), OscArg.B([1, 2, 3]), OscArg.H(1L << 40), OscArg.D(-2.5),
            OscArg.Bool(true), OscArg.Bool(false), OscArg.Nil(), OscArg.Array(OscArg.I(1), OscArg.S("x")));
        var bytes = OscCodec.Write(m);
        Assert.Equal(0, bytes.Length % 4);
        Assert.True(OscCodec.TryRead(bytes, out var read, out var why), why);
        Assert.Equal(m, read);
    }

    [Fact]
    public void AStringIsPaddedToFourBytesWithAtLeastOneZero()
    {
        Assert.Equal("2f61626300000000", Convert.ToHexStringLower(OscCodec.Write(OscMessage.Of("/abc")))[..16]);
        Assert.Equal("2f616263640000002c000000", Convert.ToHexStringLower(OscCodec.Write(OscMessage.Of("/abcd"))));
    }

    [Fact]
    public void AMessageWithoutATypeTagStringReadsAsNoArguments()
    {
        Assert.True(OscCodec.TryRead("/ping\0\0\0"u8.ToArray(), out var read, out _));
        Assert.Equal(OscMessage.Of("/ping"), read);
    }

    [Fact]
    public void AnUnknownTagDropsTheMessageButNotItsBundle()
    {
        var unknown = "/x\0\0,Q\0\0"u8.ToArray();
        Assert.False(OscCodec.TryRead(unknown, out _, out var why));
        Assert.StartsWith("unknown type tag 'Q'", why, StringComparison.Ordinal);
        var good = OscCodec.Write(OscMessage.Of("/y", OscArg.I(1)));
        var bundle = new List<byte>("#bundle\0"u8.ToArray());
        bundle.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
        foreach (var e in new[] { unknown, good })
        {
            bundle.AddRange(new byte[] { 0, 0, 0, (byte)e.Length });
            bundle.AddRange(e);
        }
        Assert.True(OscCodec.TryRead(bundle.ToArray(), out var read, out why), why);
        var b = Assert.IsType<OscBundle>(read);
        Assert.Equal(OscMessage.Of("/y", OscArg.I(1)), Assert.Single(b.Elements));
    }

    [Theory]
    [InlineData("", "an OSC packet is a non-zero multiple of 4 bytes")]
    [InlineData("2f78", "an OSC packet is a non-zero multiple of 4 bytes")]
    [InlineData("78797a00", "an OSC address starts with '/'")]
    [InlineData("2f7800002c690000", "an OSC argument is cut short")]
    [InlineData("2f7800002c73000061626364", "an OSC string has no terminating zero")]
    public void AMalformedPacketIsRefusedWithWordsNeverThrown(string hex, string why)
    {
        Assert.False(OscCodec.TryRead(Convert.FromHexString(hex), out _, out var reason));
        Assert.Equal(why, reason);
    }

    [Fact]
    public void BundlesNestAtMostEightDeepAndTimetagsAreNtp()
    {
        IOscPacket p = OscMessage.Of("/x");
        for (var i = 0; i < 8; i++)
            p = new OscBundle(OscBundle.Immediately, [p]);
        Assert.True(OscCodec.TryRead(OscCodec.Write(p), out _, out _));
        p = new OscBundle(OscBundle.Immediately, [p]);
        Assert.False(OscCodec.TryRead(OscCodec.Write(p), out _, out var why));
        Assert.Equal("OSC bundles nest at most 8 deep", why);
        // 2025-10-14T00:00:00Z: Unix 1760400000 s, NTP + 2 208 988 800 s.
        var tag = OscBundle.TimeTagOf(1_760_400_000_500);
        Assert.Equal(1_760_400_000_500, OscBundle.UnixMs(tag));
        Assert.Equal(1_760_400_000UL + 2_208_988_800UL, tag >> 32);
    }

    [Theory]
    [InlineData("/fader/?", "/fader/1", true)]
    [InlineData("/fader/?", "/fader/12", false)]
    [InlineData("/fader/*", "/fader/12", true)]
    [InlineData("/fader/[1-3]", "/fader/2", true)]
    [InlineData("/fader/[!1-3]", "/fader/2", false)]
    [InlineData("/fader/[!1-3]", "/fader/4", true)]
    [InlineData("/{fader,knob}/1", "/knob/1", true)]
    [InlineData("/{fader,knob}/1", "/pad/1", false)]
    [InlineData("/a/*/c", "/a/b/c", true)]
    [InlineData("/a/*", "/a/b/c", false)]
    public void PatternsMatchPartByPart(string pattern, string address, bool match) => Assert.Equal(match, OscPattern.Matches(pattern, address));
}

/// <summary>The /playmap address space into lines.</summary>
public sealed class OscAddressMapTests
{
    private static Project Show() => Project.CreateDefault("Show") with
    {
        Playlists = [new Playlist("pl_show", "Show", [], false, null)],
    };

    private static string Line(OscMessage m, Project? p = null)
    {
        var r = OscAddressMap.Map(m, p ?? Show());
        Assert.Equal(OscRouteKind.Lines, r.Kind);
        return Assert.Single(r.Lines);
    }

    [Fact]
    public void VerbWordsBecomeALineWithTheArgumentsAsWords()
    {
        Assert.Equal("PLAYLIST GO show", Line(OscMessage.Of("/playmap/playlist/go", OscArg.S("show"))));
        Assert.Equal("PLAYLIST GO show", Line(OscMessage.Of("/playmap/playlist/go/show")));
        Assert.Equal("PLAYLIST GO Show #3", Line(OscMessage.Of("/playmap/playlist/go/Show", OscArg.I(3))));
        Assert.Equal("BLACKOUT ON", Line(OscMessage.Of("/playmap/blackout", OscArg.I(1))));
        Assert.Equal("BLACKOUT OFF", Line(OscMessage.Of("/playmap/blackout", OscArg.Bool(false))));
        Assert.Equal("BLACKOUT TOGGLE", Line(OscMessage.Of("/playmap/blackout")));
        Assert.Equal("VIDEO SEEK 83.5", Line(OscMessage.Of("/playmap/video/seek", OscArg.F(83.5f))));
        Assert.Equal("VIDEO RATE 0.1", Line(OscMessage.Of("/playmap/video/rate", OscArg.F(0.1f))));
    }

    [Fact]
    public void LineRunsAnyLineAndCarriesARequestId()
    {
        var r = OscAddressMap.Map(OscMessage.Of("/playmap/line", OscArg.S("PLAYLIST NEXT show"), OscArg.S("a1")), Show());
        Assert.Equal((OscRouteKind.Lines, "PLAYLIST NEXT show", "a1"), (r.Kind, r.Lines[0], r.RequestId));
    }

    [Fact]
    public void SetWritesJsonNudgesAndReadsAndExpandsAPatternOverIds()
    {
        var p = Show() with
        {
            Stage = Show().Stage with
            {
                Surfaces =
                [
                    new Surface("sf_a", "Lobby", "", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", []),
                    new Surface("sf_b", "Stage", "", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", []),
                ],
            },
        };
        Assert.Equal("SET sf_a opacity 0.5", Line(OscMessage.Of("/playmap/set/sf_a/opacity", OscArg.F(0.5f)), p));
        Assert.Equal("SET sf_a color \"#FF0000\"", Line(OscMessage.Of("/playmap/set/sf_a/color", OscArg.S("#FF0000")), p));
        Assert.Equal("NUDGE sf_a opacity +0.1", Line(OscMessage.Of("/playmap/set/sf_a/opacity", OscArg.S("+"), OscArg.D(0.1)), p));
        Assert.Equal("NUDGE sf_a opacity -0.1", Line(OscMessage.Of("/playmap/set/sf_a/opacity", OscArg.S("-"), OscArg.D(0.1)), p));
        var many = OscAddressMap.Map(OscMessage.Of("/playmap/set/sf_*/opacity", OscArg.I(0)), p);
        Assert.Equal(["SET sf_a opacity 0", "SET sf_b opacity 0"], many.Lines);
        var query = OscAddressMap.Map(OscMessage.Of("/playmap/set/sf_a/opacity", OscArg.S("?")), p);
        Assert.Equal((OscRouteKind.Query, "sf_a", "opacity"), (query.Kind, query.QueryId, query.QueryPath));
        Assert.Equal("SET sf_b visible false", Line(OscMessage.Of("/playmap/surfaces/Stage/visible", OscArg.Bool(false)), p));
        Assert.Equal("PLAYLIST NEXT pl_show", Line(OscMessage.Of("/playmap/playlists/Show/next"), p));
    }

    [Fact]
    public void AnAddressThatNamesNothingIsRefusedWithWords()
    {
        Assert.Equal("no verb FROB — /playmap/line ,s \"<line>\" runs any line", OscAddressMap.Map(OscMessage.Of("/playmap/frob"), Show()).Why);
        Assert.Equal("no surface named Nowhere", OscAddressMap.Map(OscMessage.Of("/playmap/surfaces/Nowhere/opacity", OscArg.I(1)), Show()).Why);
        Assert.Equal("/x is not under /playmap", OscAddressMap.Map(OscMessage.Of("/x"), Show()).Why);
        Assert.Equal("Stage_left_1_", OscAddressMap.Sanitise("Stage left#1?"));
    }
}

/// <summary>Interop with the osc package for Node (osc.js, MIT; tests/interop/osc/generate.mjs writes the fixtures): every packet it
/// wrote reads here as it reads there and is written back byte for byte; every packet written here reads there as here.</summary>
public sealed class OscInteropTests
{
    private static string Folder() =>
        Path.Combine(PlayMap.Core.Tests.Model.RoundTwoSample.RepoRoot(), "tests", "interop", "osc");

    /// <summary>A packet in the generator's normal form: messages {address, args}, bundles {timeTag (16 hex), packets}; int64 and
    /// timetags as decimal text, blobs as hex, arrays as {type:"[", value:[…]}.</summary>
    internal static JsonNode Normal(IOscPacket packet)
    {
        if (packet is OscBundle b)
            return new JsonObject { ["timeTag"] = b.TimeTag.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), ["packets"] = new JsonArray(b.Elements.Select(Normal).ToArray()) };
        var m = (OscMessage)packet;
        return new JsonObject { ["address"] = m.Address, ["args"] = new JsonArray(m.Args.Select(Arg).ToArray()) };
    }

    private static JsonNode Arg(OscArg a) => a.Tag switch
    {
        'i' => new JsonObject { ["type"] = "i", ["value"] = (int)a.Int },
        'h' => new JsonObject { ["type"] = "h", ["value"] = a.Int.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        'f' => new JsonObject { ["type"] = "f", ["value"] = a.Float },
        'd' => new JsonObject { ["type"] = "d", ["value"] = a.Float },
        's' or 'S' => new JsonObject { ["type"] = a.Tag.ToString(), ["value"] = a.Text },
        'b' => new JsonObject { ["type"] = "b", ["value"] = Convert.ToHexStringLower(a.Blob.AsSpan()) },
        '[' => new JsonObject { ["type"] = "[", ["value"] = new JsonArray(a.Items.Select(Arg).ToArray()) },
        _ => new JsonObject { ["type"] = a.Tag.ToString() },
    };

    /// <summary>The packets written here for the generator to read.</summary>
    private static IEnumerable<(string Name, IOscPacket Packet)> Ours()
    {
        yield return ("reply", OscMessage.Of("/playmap/reply", OscArg.S("OK"), OscArg.S("{\"id\":\"bd1\"}"), OscArg.S("a1")));
        yield return ("error", OscMessage.Of("/playmap/error", OscArg.S("not paired")));
        yield return ("pong", OscMessage.Of("/playmap/pong", OscArg.H(1_760_000_000_000), OscArg.H(1_760_000_000_001), OscArg.H(1_760_000_000_002)));
        yield return ("every tag", OscMessage.Of("/all", OscArg.I(-7), OscArg.F(0.1f), OscArg.S("Bühne"), OscArg.B([9, 8, 7]), OscArg.D(83.5), OscArg.Bool(true),
            OscArg.Bool(false), OscArg.Nil(), OscArg.Array(OscArg.I(1), OscArg.S("x"))));
        yield return ("bundle", new OscBundle(OscBundle.TimeTagOf(1_760_400_000_500), [OscMessage.Of("/a", OscArg.I(1)),
            new OscBundle(OscBundle.Immediately, [OscMessage.Of("/b", OscArg.S("two"))])]));
    }

    [Fact]
    public void PacketsWrittenByOscJsReadHereAsThereAndWriteBackByteForByte()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder(), "node-packets.json")))!.AsArray();
        Assert.True(fixtures.Count >= 18);
        foreach (var f in fixtures)
        {
            var name = f!["name"]!.GetValue<string>();
            var bytes = Convert.FromHexString(f["hex"]!.GetValue<string>());
            Assert.True(OscCodec.TryRead(bytes, out var packet, out var why), $"{name}: {why}");
            Assert.True(JsonNode.DeepEquals(f["read"], Normal(packet!)), $"{name}: {Normal(packet!).ToJsonString()} ≠ {f["read"]!.ToJsonString()}");
            Assert.Equal(f["hex"]!.GetValue<string>(), Convert.ToHexStringLower(OscCodec.Write(packet!)));
        }
    }

    [Fact]
    public void PacketsWrittenHereReadInOscJsAsHere()
    {
        var ours = new JsonArray(Ours().Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["hex"] = Convert.ToHexStringLower(OscCodec.Write(o.Packet)) }).ToArray());
        var text = ours.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = Path.Combine(Folder(), "ours.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            File.WriteAllText(path, text);
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), text);
        // ours-read.json is what generate.mjs read from ours.json (run it after this test writes ours.json).
        var read = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder(), "ours-read.json")))!.AsArray().ToDictionary(r => r!["name"]!.GetValue<string>(), r => r!["read"]);
        foreach (var (name, packet) in Ours())
            Assert.True(JsonNode.DeepEquals(read[name], Normal(packet)), $"{name}: osc.js read {read[name]!.ToJsonString()}, here {Normal(packet).ToJsonString()}");
    }
}
