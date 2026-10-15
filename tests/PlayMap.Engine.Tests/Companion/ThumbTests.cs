using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Tests.Host;
using SkiaSharp;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>THUMB: a clip's poster is a small decodable JPEG named by its content's ETag, "same" when the asker holds that ETag, never
/// decoded on an armed node (a poster made before is served; one never made is refused with words); generators and colours are drawn;
/// a playlist stands for its current item; SUBSCRIBE THUMBS pushes when a watched picture's ETag moves.</summary>
public sealed class ThumbTests
{
    private static JsonElement Picture(string payload) => CompanionRig.Json(payload);

    private static SKBitmap Decode(JsonElement picture)
    {
        var bytes = Convert.FromBase64String(picture.GetProperty("data").GetString()!);
        return SKBitmap.Decode(bytes) ?? throw new InvalidDataException("the picture does not decode");
    }

    [FfmpegFact]
    public async Task AClipsPosterIsASmallJpegAndItsEtagAnswersSame()
    {
        await using var rig = await HostRig.StartAsync();
        var clip = await FfmpegFactAttribute.MakeClipAsync(rig.Dir.FullName);
        string id;
        using (var accepted = JsonDocument.Parse(await rig.OkAsync("MEDIA IMPORT " + Core.Wire.WireGrammar.Quote(clip) + " Clip")))
            id = accepted.RootElement.GetProperty("id").GetString()!;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.Store.Project.FindMedia(id) is { ContentHash.Length: > 0 } m
            && rig.Engine.Services.Media!.Readiness(m) is { Present: true, Decodable: true }, 20000), rig.Journal());

        var reply = await rig.RunAsync($"THUMB media:{id} 96x96");
        Assert.True(reply.Ok, reply.Payload);
        var picture = Picture(reply.Payload!);
        Assert.Equal("media:" + id, picture.GetProperty("ref").GetString());
        Assert.Equal("image/jpeg", picture.GetProperty("mime").GetString());
        var bytes = Convert.FromBase64String(picture.GetProperty("data").GetString()!);
        Assert.InRange(bytes.Length, 1, 12 * 1024);
        using (var bitmap = Decode(picture))
        {
            // 320×240 fits a 96×96 box at 96×72.
            Assert.Equal(96, bitmap.Width);
            Assert.Equal(72, bitmap.Height);
            Assert.Equal(96, picture.GetProperty("w").GetInt32());
            Assert.Equal(72, picture.GetProperty("h").GetInt32());
        }
        var etag = picture.GetProperty("etag").GetString()!;
        var hash = rig.Engine.Store.Project.FindMedia(id)!.ContentHash["sha256:".Length..][..16];
        Assert.Equal($"media-{hash}-96x96-j", etag);

        var same = Picture(await rig.OkAsync($"THUMB media:{id} 96x96 IF \"{etag}\""));
        Assert.True(same.GetProperty("same").GetBoolean());
        Assert.Equal(etag, same.GetProperty("etag").GetString());
        Assert.False(same.TryGetProperty("data", out _));

        // The poster is kept under the node's folder: a restart serves it without decoding.
        Assert.True(File.Exists(Path.Combine(rig.Home.Folder, "thumbs", hash, "96x96.jpg")));
    }

    /// <summary>A show with one clip whose file is not on this machine: its hash is known, its picture is not.</summary>
    private static Project OneClip(out string id)
    {
        var p = Project.CreateDefault("Thumbs");
        id = "md00000001";
        var item = new MediaItem(id, "Opening", MediaKind.Video, "/nowhere/opening.mp4", "sha256:" + new string('a', 64), 1000,
            MediaInfo.Unknown with { Width = 1920, Height = 1080, DurationNs = 10_000_000_000 }, []);
        return p with { Media = [item] };
    }

    [Fact]
    public async Task AnArmedNodeServesOnlyPostersMadeBeforeAndRefusesTheRestWithWords()
    {
        var posters = new Posters();
        await using var rig = new CompanionRig(s => s with { ShowGate = new ArmedGate(), Posters = posters }, OneClip(out var id));
        var refused = await rig.Run($"THUMB media:{id}");
        Assert.False(refused.Ok);
        Assert.Equal("no picture of it yet — posters are made in rehearsal or on import", refused.Payload);

        posters.Jpeg = Engine.Companion.Posters.Encode(new byte[16 * 9 * 4], 16, 9, jpeg: true);
        var served = Picture(await rig.Ok($"THUMB media:{id} 96x96"));
        Assert.Equal("image/jpeg", served.GetProperty("mime").GetString());
        using var bitmap = Decode(served);
        Assert.Equal(16, bitmap.Width);
    }

    [Fact]
    public async Task AHashThatIsNotHexNeverNamesAFolder()
    {
        var p = Project.CreateDefault("Thumbs");
        var item = new MediaItem("md00000002", "Odd", MediaKind.Video, "/nowhere/odd.mp4", "sha256:../../../../../../tmp/elsewhere", 1000,
            MediaInfo.Unknown with { Width = 1920, Height = 1080, DurationNs = 10_000_000_000 }, []);
        await using var rig = new CompanionRig(project: p with { Media = [item] });
        var refused = await rig.Run("THUMB media:md00000002");
        Assert.False(refused.Ok);
        Assert.Equal("Odd is not hashed yet — its poster follows its hash", refused.Payload);
    }

    [Fact]
    public async Task GeneratorsAndColoursAreDrawnAndAPlaylistIsItsCurrentItem()
    {
        await using var rig = new CompanionRig();
        var gen = await rig.Ok("GENERATOR NEW flat Amber");
        await rig.Ok($"GENERATOR SET {gen} color #FF8000");
        var flat = Picture(await rig.Ok($"THUMB generator:{gen} 32x32"));
        Assert.Equal("image/png", flat.GetProperty("mime").GetString());
        using (var bitmap = Decode(flat))
            Assert.Equal(new SKColor(0xFF, 0x80, 0x00), bitmap.GetPixel(16, 16));

        var red = Picture(await rig.Ok("THUMB color:#FF0000 8x8"));
        using (var bitmap = Decode(red))
            Assert.Equal(SKColors.Red, bitmap.GetPixel(4, 4));
        Assert.Equal("color-FF0000-8x8-p", red.GetProperty("etag").GetString());

        var list = await rig.Ok("PLAYLIST NEW Show");
        var idle = await rig.Run($"THUMB playlist:{list}");
        Assert.False(idle.Ok);
        Assert.True(idle.Payload!.Contains("idle", StringComparison.Ordinal), idle.Payload);
        await rig.Ok($"PLAYLIST ADD {list} generator:{gen} Amber");
        await rig.Ok($"PLAYLIST GO {list}", OpsRig.Desk);
        var current = Picture(await rig.Ok($"THUMB playlist:{list} 32x32"));
        Assert.Equal(flat.GetProperty("etag").GetString(), current.GetProperty("etag").GetString());

        var output = await rig.Run("THUMB output:op_1");
        Assert.False(output.Ok);
        Assert.Contains("not served yet", output.Payload, StringComparison.Ordinal);
        Assert.False((await rig.Run("THUMB media:md00000009 2000x2000")).Ok);
    }

    [Fact]
    public async Task AWatchedPicturePushesWhenItsEtagMoves()
    {
        await using var rig = new CompanionRig();
        var gen = await rig.Ok("GENERATOR NEW flat Wash");
        var sink = new RecordingSink();
        using var sub = rig.Open("THUMBS", sink, null, "generator:" + gen, "color:#00FF00");
        await sink.Heard("THUMBS {");
        var first = CompanionRig.Json(sink.Latests["thumbs"]["THUMBS ".Length..]);
        Assert.StartsWith("color-00FF00-96x96", first.GetProperty("color:#00FF00").GetString(), StringComparison.Ordinal);
        var before = first.GetProperty("generator:" + gen).GetString()!;

        await rig.Ok($"GENERATOR SET {gen} level 50");
        // The picture's new ETag, as THUMB names it now: the push that carries it is waited for, never a wall-clock window.
        var after = Picture(await rig.Ok($"THUMB generator:{gen}")).GetProperty("etag").GetString()!;
        Assert.NotEqual(before, after);
        await sink.Heard("\"generator:" + gen + "\":\"" + after + "\"");
        Assert.Equal(after, CompanionRig.Json(sink.Latests["thumbs"]["THUMBS ".Length..]).GetProperty("generator:" + gen).GetString());

        var unpaired = new RecordingSink();
        Assert.False(((PlayMap.Net.IPushTopics)rig.Engine).TryOpen("THUMBS", ["color:#000000"], CompanionRig.Paired with { Paired = false }, unpaired, out _, out var words));
        Assert.Contains("not paired", words, StringComparison.Ordinal);
    }

    private sealed class Posters : IPosterStore
    {
        public byte[]? Jpeg;

        public bool TryGet(string contentHash, int maxWidth, int maxHeight, out ReadOnlyMemory<byte> jpeg, out int width, out int height)
        {
            jpeg = Jpeg ?? ReadOnlyMemory<byte>.Empty;
            (width, height) = (16, 9);
            return Jpeg is not null;
        }
    }
}
