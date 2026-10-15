using PlayMap.Core.Media;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Optimise;

/// <summary>A recipe's canonical text and key (a golden value: the same on every machine and run), its round trip, and which
/// version a node plays.</summary>
public class RecipeAndChoiceTests
{
    private const string Source = "sha256:" + "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";

    [Fact]
    public void TheCanonicalTextHasSortedKeysAndInvariantNumbers()
    {
        var r = Recipe.For("hap-q", EncodeQuality.Show);
        Assert.Equal("{\"audio\":\"copy\",\"chunks\":0,\"quality\":\"show\",\"rawBelow\":0.1,\"secondStage\":\"auto\",\"to\":\"hap-q\"}", r.Canonical());
        Assert.Equal(r, Recipe.Parse(r.Canonical()));
        Assert.Null(Recipe.Parse("{\"to\":\"hap-r\"}"));
    }

    [Fact]
    public void TheKeyIsAGoldenValueOfSourceRecipeAndAlgorithm()
    {
        var r = Recipe.For("hap-q", EncodeQuality.Show);
        Assert.Equal(Golden, r.Key(Source));
        Assert.Matches("^ok:[0-9a-f]{32}$", r.Key(Source));
        Assert.NotEqual(r.Key(Source), (r with { To = "hap" }).Key(Source));
        Assert.NotEqual(r.Key(Source), r.Key(Source, "hapenc/2"));
    }

    // sha256("playmap-optimise/1\n" + Source + "\n" + canonical + "\nhapenc/1"), first 32 hex digits.
    private const string Golden = "ok:6f602d772ad81d0c22c5d2e9a6abec8c";

    [Theory]
    [InlineData(640, 4)]
    [InlineData(1920, 8)]
    [InlineData(3840, 16)]
    public void TheChunkCountFollowsTheWidth(int width, int chunks) => Assert.Equal(chunks, Recipe.For("hap", EncodeQuality.Fast).ChunksFor(width));

    private static MediaVersion Version(string id, string to, double psnr, long at, bool review = false) =>
        new(id, to, Recipe.For(to, EncodeQuality.Show).Canonical(), "ok:x", "sha256:" + new string(id[^1], 64), 1000, MediaInfo.Unknown,
            new VersionQuality(psnr, psnr - 3, 3, 0), "hapenc/1", "a1b2c3d4", at) { Review = review };

    private static MediaItem Item(string use, params MediaVersion[] versions) =>
        new MediaItem("md1", "Clip", MediaKind.Video, "clip.mp4", "sha256:" + new string('0', 64), 10, MediaInfo.Unknown, []) with
        {
            Versions = [.. versions],
            Use = use,
        };

    private const BlockFormats Desktop = BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4;

    [Fact]
    public void AutoPlaysTheBestPlayableVersionPresentHere()
    {
        var item = Item("auto", Version("mv1", "hap", 38, 1), Version("mv2", "hap-q", 44, 2), Version("mv3", "hap-q", 47, 3, review: true));
        var (v, why) = VersionChoice.Pick(item, Desktop, _ => true);
        Assert.Equal("mv2", v?.Id);
        Assert.Equal("HAP Q, present, 44.0 dB", why);
        var (none, words) = VersionChoice.Pick(item, Desktop, _ => false);
        Assert.Null(none);
        Assert.Equal("mv1's file is not on this node; mv2's file is not on this node; mv3 waits for a person (below the quality floor): the source plays", words);
        Assert.Equal("mv1", VersionChoice.Pick(item, BlockFormats.Bc1, _ => true).Version?.Id);
    }

    [Fact]
    public void SourceAndANamedVersionAreObeyed()
    {
        var item = Item("source", Version("mv1", "hap", 38, 1));
        Assert.Equal((null, "use is source"), VersionChoice.Pick(item, Desktop, _ => true));
        var named = Item("mv3", Version("mv1", "hap", 38, 1), Version("mv3", "hap-q", 30, 3, review: true));
        Assert.Equal("mv3", VersionChoice.Pick(named, Desktop, _ => true).Version?.Id);
        var (v, why) = VersionChoice.Pick(named, BlockFormats.Bc1, _ => true);
        Assert.Null(v);
        Assert.Equal("mv3 is HAP Q; this GPU's GL has no S3TC: the source plays", why);
    }

    [Fact]
    public void ThePlayedItemCarriesTheVersionsBytesAndFacts()
    {
        var item = Item("auto", Version("mv2", "hap-q", 44, 2));
        var played = VersionChoice.Played(item, Desktop, _ => true);
        Assert.Equal(("md1", item.Versions[0].ContentHash, ""), (played.Id, played.ContentHash, played.Location));
        Assert.Equal(item.ContentHash, VersionChoice.Played(item with { Use = "source" }, Desktop, _ => true).ContentHash);
    }
}
