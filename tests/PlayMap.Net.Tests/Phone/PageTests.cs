using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;
using PlayMap.Net.Phone;

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

        // The sites as the node's socket sends them, so the page reads what it really receives.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            PhoneSession.WriteSites(w, new PhoneSites("pj00000001", located.Chosen, located.Lit, located.MinSites));
            w.WriteEndObject();
        }
        using var sent = JsonDocument.Parse(buffer.WrittenMemory);
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
                    minSites = sent.RootElement.GetProperty("minSites").GetInt32(),
                    sites = sent.RootElement.GetProperty("sites"),
                    lit = sent.RootElement.GetProperty("lit"),
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
                // The settle grid, over the whole picture and over the lit rectangle, sample for sample.
                Assert.Equal(Convert.ToBase64String(PictureGrid.Of(pictures[k], sim.CameraWidth, sim.CameraHeight, default)),
                    page.GetProperty("grid").GetString());
                Assert.Equal(Convert.ToBase64String(PictureGrid.Of(pictures[k], sim.CameraWidth, sim.CameraHeight, located.Lit)),
                    page.GetProperty("litGrid").GetString());
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

    /// <summary>The page in Chromium joined with a fresh invite and asked for the lock profile, as a scan asks: what the page saw and
    /// sent, what it answered, its session and the ticket.</summary>
    private static Task<(JsonElement Page, PhonePrepared Prepared, IPhoneSession Session, string Ticket)> RunPageAsync(PhoneNode node, string? y4m) =>
        RunPageAsync(node, y4m, (session, ct) => session.PrepareAsync(new PhonePrepare(PhoneProtocol.Lock, 60, 3, 2), ct));

    /// <summary>The page in Chromium joined with a fresh invite, driven by <paramref name="drive"/> (as a scan drives it), then told
    /// "done": what the page saw and sent, the last prepare it answered, its session and the ticket.</summary>
    private static async Task<(JsonElement Page, PhonePrepared Prepared, IPhoneSession Session, string Ticket)> RunPageAsync(PhoneNode node, string? y4m,
        Func<IPhoneSession, CancellationToken, Task<PhonePrepared>> drive)
    {
        var ticket = PhoneNode.TicketOf(node.Invite());
        var dir = Temp();
        try
        {
            var outPath = Path.Combine(dir, "page.json");
            var args = new List<string> { node.Base.ToString().TrimEnd('/'), ticket, outPath };
            if (y4m is not null)
                args.Add(y4m);
            var run = NodeTools.RunAsync("page.mjs", TimeSpan.FromSeconds(150), [.. args]);
            Assert.True(await Eventually.TrueAsync(() => node.Hub.Phones().Any(p => p.Connected) || run.IsCompleted, 60_000), "the page never joined");
            if (run.IsCompleted)
                Assert.Fail("the page ended before it joined: " + (await run).Output);
            var session = node.Hub.Session(node.Hub.Phones().Single(p => p.Connected).Id)!;
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var prepared = await drive(session, wait.Token);
            await session.SayAsync("info", "done", wait.Token);
            var (code, output) = await run;
            Assert.True(code == 0, output);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath));
            return (doc.RootElement.Clone(), prepared, session, ticket);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [NodeFact("playwright")]
    public async Task InChromiumThePageJoinsWithTheTicketOnlyInItsFirstMessageLocksExposureAndSaysItNeedsHttpsOverPlainHttp()
    {
        await using var node = await PhoneNode.StartAsync();
        var (r, prepared, session, ticket) = await RunPageAsync(node, null);

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
        // The socket's upgrade request was among them, with all its headers (its subprotocol: where a socket token would hide).
        Assert.Contains(requests, q => q.GetProperty("headers").EnumerateObject()
            .Any(h => h.Name.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase) && h.Value.GetString() == PhoneProtocol.SubProtocol));
        // It travels in the socket's first message, the hello, and nowhere after.
        var sent = r.GetProperty("sent").EnumerateArray().Select(e => e.GetString()!).ToList();
        using var hello = JsonDocument.Parse(sent[0]);
        Assert.Equal("hello", hello.RootElement.GetProperty("t").GetString());
        Assert.Equal(ticket, hello.RootElement.GetProperty("ticket").GetString());
        Assert.All(sent.Skip(1), m => Assert.DoesNotContain(ticket, m, StringComparison.Ordinal));
        Assert.StartsWith("Ready", r.GetProperty("status").GetString(), StringComparison.Ordinal);
        Assert.True(r.GetProperty("stored").GetBoolean(), "the key is kept for the next join");
        Assert.Equal("Pixel 8", Assert.Single(node.Hub.Phones()).Name);
        Assert.True(hello.RootElement.GetProperty("camera").GetProperty("width").GetInt32() > 0);
        // The clock exchange ran: the node knows the page's clock within a few milliseconds.
        Assert.Contains(r.GetProperty("received").EnumerateArray(), m => m.GetString()!.StartsWith("{\"t\":\"clock\"", StringComparison.Ordinal));

        // Chromium's fake camera offers manual exposure (an Android phone's): the lock profile sets it and the camera keeps it.
        var caps = hello.RootElement.GetProperty("camera").GetProperty("caps");
        Assert.Contains(caps.GetProperty("exposure").EnumerateArray(), e => e.GetString() == "manual");
        Assert.Equal(PhoneProtocol.Lock, session.Profile.Exposure);
        Assert.Equal("locked", prepared.Exposure);
        Assert.Equal("Exposure locked", prepared.Words);
        Assert.Equal("manual", r.GetProperty("settings").GetProperty("settings").GetProperty("exposureMode").GetString());
        Assert.NotEqual("", prepared.WhiteBalance);

        var plain = r.GetProperty("insecure");
        Assert.False(plain.GetProperty("secure").GetBoolean());
        Assert.Contains("needs the node's https address", plain.GetProperty("words").GetString()!, StringComparison.Ordinal);
        Assert.StartsWith("https://phone.test:", plain.GetProperty("link").GetString()!, StringComparison.Ordinal);
        Assert.False(plain.GetProperty("joinShown").GetBoolean());
        Assert.Equal(0, plain.GetProperty("cameraAsks").GetInt32());
        Assert.Equal(0, plain.GetProperty("sockets").GetInt32());
    }

    /// <summary>A Y4M picture sequence, 4:2:0, a soft gradient that drifts a little each frame.</summary>
    private static void WriteY4m(string path, int w, int h, int frames)
    {
        using var f = File.Create(path);
        f.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"YUV4MPEG2 W{w} H{h} F30:1 Ip A1:1 C420jpeg\n")));
        var y = new byte[w * h];
        var uv = new byte[w / 2 * (h / 2)];
        Array.Fill(uv, (byte)128);
        for (var n = 0; n < frames; n++)
        {
            for (var k = 0; k < y.Length; k++)
                y[k] = (byte)(64 + (k % w + n) % 128);
            f.Write("FRAME\n"u8);
            f.Write(y);
            f.Write(uv);
            f.Write(uv);
        }
    }

    /// <summary>A Y4M picture sequence whose left half holds still while its right half turns from dark to bright and back each
    /// second.</summary>
    private static void WriteHalfBlinkY4m(string path, int w, int h)
    {
        using var f = File.Create(path);
        f.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"YUV4MPEG2 W{w} H{h} F30:1 Ip A1:1 C420jpeg\n")));
        var y = new byte[w * h];
        var uv = new byte[w / 2 * (h / 2)];
        Array.Fill(uv, (byte)128);
        for (var n = 0; n < 60; n++)
        {
            for (var k = 0; k < y.Length; k++)
                y[k] = k % w < w / 2 ? (byte)(80 + k % w / 4) : n < 30 ? (byte)40 : (byte)200;
            f.Write("FRAME\n"u8);
            f.Write(y);
            f.Write(uv);
            f.Write(uv);
        }
    }

    /// <summary>The page's own prepare, places and change rule together: after a projector whose place was the still left half, the
    /// next projector's first locate frame is seen where the picture changes (the rule itself, frame by frame: unit/watch.test.mjs).</summary>
    [NodeFact("playwright")]
    public async Task InChromiumTheNextProjectorsFirstLocateFrameIsSeenWhereThePictureChanges()
    {
        await using var node = await PhoneNode.StartAsync();
        var dir = Temp();
        try
        {
            var y4m = Path.Combine(dir, "camera.y4m");
            WriteHalfBlinkY4m(y4m, 320, 240);
            PhoneSeen? seen = null;
            var (_, prepared, _, _) = await RunPageAsync(node, y4m, async (session, ct) =>
            {
                await session.PrepareAsync(new PhonePrepare(PhoneProtocol.Steady, 60, 3, 2), ct);
                // The first projector's tag places stood in the still left half of the picture.
                await session.SetSitesAsync(new PhoneSites("pj00000001", [], new Core.Scan.PixelRect(0, 0, 160, 240), 0), ct);
                // The next projector's run: its first locate frame lands where the picture changes, outside the first one's place.
                var next = await session.PrepareAsync(new PhonePrepare(PhoneProtocol.Steady, 60, 3, 2), ct);
                seen = await session.CueAsync(new PhoneCue(0, "locate", "pj00000002", 0, 0, 0, 0, 0, 8), ct);
                return next;
            });
            Assert.Equal("steady", prepared.Exposure);
            Assert.NotNull(seen);
            Assert.True(seen.Outcome == "seen", $"{seen.Outcome}: {seen.Why}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [NodeFact("playwright")]
    public async Task InChromiumACameraWithoutExposureControlsRunsTheSteadyProfileWithWords()
    {
        await using var node = await PhoneNode.StartAsync();
        var dir = Temp();
        try
        {
            var y4m = Path.Combine(dir, "camera.y4m");
            WriteY4m(y4m, 320, 240, 30);
            var (r, prepared, session, _) = await RunPageAsync(node, y4m);
            using var hello = JsonDocument.Parse(r.GetProperty("sent")[0].GetString()!);
            Assert.Empty(hello.RootElement.GetProperty("camera").GetProperty("caps").GetProperty("exposure").EnumerateArray());
            Assert.Equal(PhoneProtocol.Steady, session.Profile.Exposure);
            // Asked to lock, a camera that cannot runs the steady profile and says why.
            Assert.Equal("steady", prepared.Exposure);
            Assert.Equal("This camera cannot lock its exposure from a web page: brightness kept steady instead", prepared.Words);
            Assert.Equal("not offered by this camera", prepared.WhiteBalance);
            Assert.Equal("steady", Assert.Single(node.Hub.Phones()).Camera!.Exposure);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
