using System.Text.Json;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;

namespace PlayMap.Net.Tests.Phone;

/// <summary>The camera page against the node, under Node 22: its tag reader reads exactly what Core's reads from the same rendered
/// pictures, its PMF1 frames are read bit-identical by Core, an outside decoder reads the invite's QR code, and in Chromium the
/// page joins with the ticket only in its first socket message — no request's URL, header or body carries it — while over plain
/// HTTP it says it needs https and asks for no camera.</summary>
public sealed class PageTests
{
    private static string Temp() => Directory.CreateTempSubdirectory("playmap-page-").FullName;

    [NodeFact]
    public async Task ThePagesTagReaderReadsWhatCoresReadsFromRenderedPictures()
    {
        var scene = SimScenes.FloorAndBox();
        var plan = ScanPlan.Create(scene.Projector.Intrinsics.Width, scene.Projector.Intrinsics.Height);
        var sim = new ScanSimulator(new SimGeometry(scene), plan, new SimOptions { NoiseSigma = 2 });
        var candidates = CellLocate.Candidates(plan.ProjectorWidth, plan.ProjectorHeight);
        var located = CellLocate.Decode(sim.CameraWidth, sim.CameraHeight, [.. Enumerable.Range(0, CellLocate.Frames).Select(j =>
        {
            var r = new byte[plan.ProjectorWidth * plan.ProjectorHeight];
            CellLocate.RenderFrame(j, r, plan.ProjectorWidth, plan.ProjectorHeight, candidates, 0);
            return sim.RenderRaster(r, (ulong)j);
        })], candidates);
        Assert.True(located.Chosen.Length >= 2, located.Words);

        byte[] Tagged(int i, double gain = 1)
        {
            var r = plan.Render(i);
            TagOverlay.Apply(r, plan.ProjectorWidth, located.Sites, FrameTag.Value(i), 255, 0);
            return sim.RenderRaster(r, (ulong)(1000 + i), gain);
        }
        var pictures = new List<byte[]>();
        for (var i = 0; i < plan.Count; i++)
            pictures.Add(Tagged(i, i % 3 == 0 ? 0.35 : 1));
        var mixed = Tagged(9);
        var before = Tagged(8);
        var split = sim.CameraHeight / 2 * sim.CameraWidth;
        Array.Copy(before, split, mixed, split, mixed.Length - split);
        pictures.Add(mixed);
        pictures.Add(new byte[sim.CameraWidth * sim.CameraHeight]);

        var dir = Temp();
        try
        {
            var cases = new List<object>();
            for (var k = 0; k < pictures.Count; k++)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"p{k}.gray"), pictures[k]);
                cases.Add(new
                {
                    file = $"p{k}.gray",
                    width = sim.CameraWidth,
                    height = sim.CameraHeight,
                    minSites = located.MinSites,
                    sites = located.Chosen.Select(s => new { cells = s.Cells.Select(c => new[] { c.X, c.Y }) }),
                });
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "cases.json"), JsonSerializer.Serialize(cases));
            var (code, output) = await NodeTools.RunAsync("tags-check.mjs", TimeSpan.FromSeconds(60), dir);
            Assert.True(code == 0, output);
            using var js = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "js.json")));
            var reads = 0;
            for (var k = 0; k < pictures.Count; k++)
            {
                var core = FrameTag.ReadFrame(pictures[k], sim.CameraWidth, sim.CameraHeight, located.Chosen, located.MinSites);
                var page = js.RootElement[k];
                Assert.Equal(core.Outcome.ToString().ToLowerInvariant(), page.GetProperty("outcome").GetString());
                Assert.Equal(core.Value, page.GetProperty("value").GetInt32());
                Assert.Equal(core.ValidSites, page.GetProperty("validSites").GetInt32());
                Assert.Equal(core.Margin, page.GetProperty("margin").GetInt32());
                // Every picture at full brightness reads its own tag; a third of them are dimmed to where some do not, and the
                // page must not read those either.
                if (k < plan.Count && k % 3 != 0)
                {
                    Assert.Equal(FrameTagOutcome.Read, core.Outcome);
                    Assert.Equal(FrameTag.Value(k), core.Value);
                    reads++;
                }
            }
            Assert.Equal(plan.Count - (plan.Count + 2) / 3, reads);
            Assert.Contains(js.RootElement.EnumerateArray().Take(plan.Count), e => e.GetProperty("outcome").GetString() != "read");
            Assert.Equal("disagree", js.RootElement[plan.Count].GetProperty("outcome").GetString());
            Assert.Equal("none", js.RootElement[plan.Count + 1].GetProperty("outcome").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [NodeFact]
    public async Task AFrameThePageWritesIsReadBitIdenticalByCore()
    {
        var dir = Temp();
        try
        {
            var (code, output) = await NodeTools.RunAsync("pmf1-write.mjs", TimeSpan.FromSeconds(60), dir);
            Assert.True(code == 0, output);
            var pixels = await File.ReadAllBytesAsync(Path.Combine(dir, "pixels.gray"));
            foreach (var (file, codec, kind) in new[] { ("deflate.pmf1", Pmf1.Deflate, "pattern"), ("none.pmf1", Pmf1.None, "locate") })
            {
                var frame = await File.ReadAllBytesAsync(Path.Combine(dir, file));
                Assert.True(Pmf1.TryParse(frame, out var header, out var payload, out var words), words);
                Assert.Equal(codec, header.Codec);
                Assert.Equal(kind, header.Kind);
                Assert.Equal(new PixelRect(0, 0, 320, 240), header.Roi);
                Assert.True(Pmf1.TryDecode(header, payload, out var back, out words), words);
                Assert.Equal(pixels, back);
            }
            Assert.True(new FileInfo(Path.Combine(dir, "deflate.pmf1")).Length < pixels.Length * 0.8, "the page's deflate compresses");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [NodeFact("jsqr")]
    public async Task AnOutsideDecoderReadsTheInvitesQrCode()
    {
        var texts = new[]
        {
            "https://192.168.1.44:8080/scan/#t=ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
            "https://[fd00::1234:5678:9abc:def0]:49152/scan/#t=QZ7KX2M4N6P8R3S5T7V9W2Y4Z6B8C3D5",
            "a",
            new string('x', 100),
            string.Concat(Enumerable.Repeat("Stage left ✓ ", 12)),
            new string('q', 213),
        };
        var codes = texts.Select(QrMatrix.Encode).ToList();
        Assert.Contains(codes, c => c.Version >= 7);
        var dir = Temp();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "qr.json"), JsonSerializer.Serialize(codes.Select(c => new { size = c.Size, bits = c.ToBase64Bitset() })));
            var (code, output) = await NodeTools.RunAsync("qr-check.mjs", TimeSpan.FromSeconds(60), dir);
            Assert.True(code == 0, output);
            var read = JsonSerializer.Deserialize<string?[]>(await File.ReadAllTextAsync(Path.Combine(dir, "js.json")))!;
            Assert.Equal(texts, read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [NodeFact("playwright")]
    public async Task InChromiumThePageJoinsWithTheTicketOnlyInItsFirstMessageAndSaysItNeedsHttpsOverPlainHttp()
    {
        await using var node = await PhoneNode.StartAsync();
        var invite = node.Invite();
        var ticket = PhoneNode.TicketOf(invite);
        var dir = Temp();
        try
        {
            var outPath = Path.Combine(dir, "page.json");
            var (code, output) = await NodeTools.RunAsync("page.mjs", TimeSpan.FromSeconds(120), node.Base.ToString().TrimEnd('/'), ticket, outPath);
            Assert.True(code == 0, output);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath));
            var r = doc.RootElement;

            // The fragment is gone from the address at once, and no request carries the ticket anywhere.
            Assert.Equal("", r.GetProperty("hashAfter").GetString());
            var requests = r.GetProperty("requests").EnumerateArray().ToList();
            Assert.True(requests.Count >= 5, r.GetProperty("requests").ToString());
            foreach (var q in requests)
            {
                Assert.DoesNotContain(ticket, q.GetProperty("url").GetString()!, StringComparison.Ordinal);
                Assert.DoesNotContain(ticket, q.GetProperty("body").GetString()!, StringComparison.Ordinal);
                foreach (var h in q.GetProperty("headers").EnumerateObject())
                    Assert.DoesNotContain(ticket, h.Value.GetString()!, StringComparison.Ordinal);
            }
            // It travels in the socket's first message, the hello, and nowhere after.
            var sent = r.GetProperty("sent").EnumerateArray().Select(e => e.GetString()!).ToList();
            using var hello = JsonDocument.Parse(sent[0]);
            Assert.Equal("hello", hello.RootElement.GetProperty("t").GetString());
            Assert.Equal(ticket, hello.RootElement.GetProperty("ticket").GetString());
            Assert.All(sent.Skip(1), m => Assert.DoesNotContain(ticket, m, StringComparison.Ordinal));
            var caps = hello.RootElement.GetProperty("camera").GetProperty("caps");
            Assert.True(caps.GetProperty("exposure").ValueKind == JsonValueKind.Array);
            Assert.StartsWith("Ready", r.GetProperty("status").GetString(), StringComparison.Ordinal);
            Assert.True(r.GetProperty("stored").GetBoolean(), "the key is kept for the next join");
            var phone = Assert.Single(node.Hub.Phones());
            Assert.Equal("Pixel 8", phone.Name);
            Assert.True(hello.RootElement.GetProperty("camera").GetProperty("width").GetInt32() > 0);

            var plain = r.GetProperty("insecure");
            Assert.False(plain.GetProperty("secure").GetBoolean());
            Assert.Contains("needs the node's https address", plain.GetProperty("words").GetString()!, StringComparison.Ordinal);
            Assert.StartsWith("https://phone.test:", plain.GetProperty("link").GetString()!, StringComparison.Ordinal);
            Assert.False(plain.GetProperty("joinShown").GetBoolean());
            Assert.Equal(0, plain.GetProperty("cameraAsks").GetInt32());
            Assert.Equal(0, plain.GetProperty("sockets").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
