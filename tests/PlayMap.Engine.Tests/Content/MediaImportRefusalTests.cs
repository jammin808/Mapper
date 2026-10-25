using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Tests.Content;

/// <summary>A page, an embed, an address that carries a pass or an https stream given to MEDIA IMPORT is refused with what PlayMap can
/// take instead, adds no item and asks the media edge nothing, and no word of a pass reaches the reply or the journal (the refusal's row
/// shows the line with the address as the marker); an edit (ADD, SET) bringing one in is refused the same way. A project that kept
/// one — opened, or the node's own at start — is scrubbed within a tick by one journalled edit no undo takes back. A page an older
/// project kept reads unplayable in MEDIA LIST, with a media edge or without one, and is never fetched.</summary>
public sealed class MediaImportRefusalTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    /// <summary>26 of Stagewatch's window alphabet: a window's pass.</summary>
    private const string Window = "q3x7m2k9p4abcdefghjk5n6r8t";

    /// <summary>Each pass the cases carry, none of which may reach a reply, a journal row or anything the node publishes.</summary>
    private static readonly string[] Passes = [Window, "k2m4zz", "4321zz", "admin:pw9"];

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    private static void HoldsNoPass(string text, string what)
    {
        foreach (var pass in Passes)
            Assert.False(text.Contains(pass, StringComparison.Ordinal), $"{what} holds a pass: {text}");
    }

    private static void JournalHoldsNoPass(EngineCore engine)
    {
        foreach (var row in engine.Journal.Recent(engine.Journal.RecentCapacity))
        {
            HoldsNoPass(row.Text, "a journal row's text");
            HoldsNoPass(row.Line ?? "", "a journal row's line");
        }
    }

    public static TheoryData<string, string> PassLines() => new()
    {
        { $"MEDIA IMPORT https://10.0.0.5:9924/w/{Window}", EngineCore.WindowAddressWords },
        { $"MEDIA IMPORT 10.0.0.5:9924/w/{Window} Stage", EngineCore.WindowAddressWords },
        { $"MEDIA IMPORT https://hall-a.local/w/{Window}.obj", EngineCore.WindowAddressWords },
        { "MEDIA IMPORT https://hall-a.local/stage?view=k2m4zz&lang=en", EngineCore.PassAddressWords },
        { "MEDIA IMPORT https://cdn.example.com/live/index.m3u8?token=4321zz", EngineCore.PassAddressWords },
        { "MEDIA IMPORT rtsp://admin:pw9@10.0.0.9/stream1 Cam", EngineCore.PassAddressWords },
        { $"MEDIA IMPORT /shows/clip.mov \"Stage https://h/w/{Window}\"", EngineCore.PassNameWords },
        // Punctuation glued to the address hides nothing of it.
        { $"MEDIA IMPORT (https://10.0.0.5:9924/w/{Window})", EngineCore.WindowAddressWords },
        { $"MEDIA IMPORT <https://hall-a.local/w/{Window}>", EngineCore.WindowAddressWords },
        { "MEDIA IMPORT Stage:https://hall-a.local/stage?view=k2m4zz", EngineCore.PassAddressWords },
        { $"MEDIA IMPORT (10.0.0.5:9924/w/{Window})", EngineCore.WindowAddressWords },
        { $"MEDIA IMPORT /shows/a.mov \"Lobby (https://h/w/{Window})\"", EngineCore.PassNameWords },
    };

    [Theory]
    [MemberData(nameof(PassLines))]
    public async Task AnAddressThatCarriesAPassIsRefusedAndNoWordOfItIsKept(string line, string words)
    {
        var media = new Recorded();
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });
        var project = engine.Store.Project;

        var reply = await Run(engine, line);

        Assert.False(reply.Ok);
        Assert.Equal(words, reply.Payload);
        HoldsNoPass(reply.Payload!, "the reply");
        Assert.Equal(0, media.Asked);
        Assert.Same(project, engine.Store.Project);
        var row = engine.Journal.Recent(1)[0];
        Assert.Equal(("refused", false, words), (row.Kind, row.Ok, row.Text));
        Assert.Equal(VerbCatalog.Redacted(line), row.Line);
        Assert.Contains(PrivateAddress.Marker, row.Line, StringComparison.Ordinal);
        JournalHoldsNoPass(engine);
    }

    [Theory]
    [InlineData("MEDIA IMPORT https://www.example.com/schedule", nameof(EngineCore.PageImportWords))]
    [InlineData("MEDIA IMPORT http://10.0.0.5/page.html Lobby", nameof(EngineCore.PageImportWords))]
    [InlineData("MEDIA IMPORT https://cdn.example.com/clip.mp4", nameof(EngineCore.PageImportWords))]
    [InlineData("MEDIA IMPORT https://www.youtube.com/watch?v=dQw4w9WgXcQ", nameof(EngineCore.EmbedImportWords))]
    [InlineData("MEDIA IMPORT https://youtu.be/dQw4w9WgXcQ Clip", nameof(EngineCore.EmbedImportWords))]
    [InlineData("MEDIA IMPORT https://cdn.example.com/live/index.m3u8", nameof(EngineCore.HttpsStreamWords))]
    [InlineData("MEDIA IMPORT https://cdn.example.com/live/manifest.MPD?start=1 Live", nameof(EngineCore.HttpsStreamWords))]
    public async Task APageAnEmbedOrAnHttpsStreamIsRefusedWithWhatThisNodeTakesInstead(string line, string words)
    {
        var expected = words switch
        {
            nameof(EngineCore.PageImportWords) => EngineCore.PageImportWords,
            nameof(EngineCore.EmbedImportWords) => EngineCore.EmbedImportWords,
            _ => EngineCore.HttpsStreamWords,
        };
        var media = new Recorded();
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });
        var project = engine.Store.Project;

        var reply = await Run(engine, line);

        Assert.False(reply.Ok);
        Assert.Equal(expected, reply.Payload);
        Assert.Contains("nothing was added", reply.Payload, StringComparison.Ordinal);
        Assert.Equal(0, media.Asked);
        Assert.Same(project, engine.Store.Project);
        Assert.Empty(engine.Store.Project.Media);
        Assert.Equal(("refused", line, false), (engine.Journal.Recent(1)[0].Kind, engine.Journal.Recent(1)[0].Line, engine.Journal.Recent(1)[0].Ok));
    }

    [Theory]
    [InlineData("http://cdn.example.com/live/index.m3u8", "Live")]
    [InlineData("http://cdn.example.com/live/manifest.mpd?start=1", "Dash")]
    [InlineData("rtsp://10.0.0.9/stream1", "Cam")]
    [InlineData("srt://10.0.0.9:9000", "Feed")]
    public async Task AStreamFfmpegReadsWithoutTlsIsAddedAsAStream(string location, string name)
    {
        var media = new Recorded();
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });

        var reply = await Run(engine, $"MEDIA IMPORT {location} {name}");

        Assert.True(reply.Ok, reply.Payload);
        Assert.Equal($"added {name}", reply.Effect);
        var item = Assert.Single(engine.Store.Project.Media);
        Assert.Equal((MediaKind.Stream, location, name), (item.Kind, item.Location, item.Name));
        Assert.Equal(0, media.Asked);
    }

    [Fact]
    public async Task AnEditThatWouldBringAPassOrAPageInIsRefusedAndAnOlderItemIsStillRenamed()
    {
        var project = Project.CreateDefault("Edits") with
        {
            Media =
            [
                new MediaItem("md_clip", "Clip", MediaKind.Video, "/shows/clip.mov", "", 0, MediaInfo.Unknown, []),
                new MediaItem("md_page", "Schedule", MediaKind.Web, "https://www.example.com/schedule", "", 0, MediaInfo.Unknown, []),
                new MediaItem("md_live", "Live", MediaKind.Stream, "https://cdn.example.com/live/index.m3u8", "", 0, MediaInfo.Unknown, []),
            ],
        };
        await using var engine = new EngineCore(EngineServices.ForTests(), project);
        var revision = engine.Store.Current.Revision;

        foreach (var (line, words) in new[]
        {
            ($"ADD media {{\"kind\":\"stream\",\"name\":\"Feed\",\"location\":\"https://h/w/{Window}\"}}", EngineCore.WindowAddressWords),
            ("ADD media {\"kind\":\"stream\",\"name\":\"Feed\",\"location\":\"rtsp://admin:pw9@10.0.0.9/s\"}", EngineCore.PassAddressWords),
            ($"ADD media {{\"kind\":\"video\",\"name\":\"Stage https://h/w/{Window}\",\"location\":\"/shows/a.mov\"}}", EngineCore.PassNameWords),
            ("ADD media {\"kind\":\"web\",\"name\":\"Page\",\"location\":\"https://www.example.com/\"}", EngineCore.PageImportWords),
            ("ADD media {\"kind\":\"embed\",\"name\":\"Clip\",\"location\":\"https://youtu.be/dQw4w9WgXcQ\"}", EngineCore.EmbedImportWords),
            ($"SET md_clip location \"https://hall-a.local/w/{Window}\"", EngineCore.WindowAddressWords),
            ("SET md_clip name \"Cam https://x/feed?pin=4321zz\"", EngineCore.PassNameWords),
            ("SET md_clip kind \"web\"", EngineCore.PageImportWords),
            // What the import refuses by kind is refused whatever kind the edit files it as.
            ("ADD media {\"kind\":\"stream\",\"name\":\"Live\",\"location\":\"https://cdn.example.com/live/index.m3u8\"}", EngineCore.HttpsStreamWords),
            ("ADD media {\"kind\":\"stream\",\"name\":\"Page\",\"location\":\"https://www.example.com/schedule\"}", EngineCore.PageImportWords),
            ("ADD media {\"kind\":\"video\",\"name\":\"Page\",\"location\":\"https://www.example.com/schedule\"}", EngineCore.PageImportWords),
            ("SET md_clip location \"https://cdn.example.com/clip.mp4\"", EngineCore.PageImportWords),
            ("SET md_page kind \"stream\"", EngineCore.PageImportWords),
            ("SET md_live kind \"video\"", EngineCore.HttpsStreamWords),
        })
        {
            var reply = await Run(engine, line);
            Assert.False(reply.Ok, line);
            Assert.Equal(words, reply.Payload);
        }
        Assert.Equal(revision, engine.Store.Current.Revision);
        JournalHoldsNoPass(engine);

        // A page and an https stream an older project kept are still renamed, and removed; an http HLS stream is added.
        Assert.True((await Run(engine, "SET md_page name \"Schedule (old)\"")).Ok);
        Assert.Equal("Schedule (old)", engine.Store.Project.FindMedia("md_page")!.Name);
        Assert.True((await Run(engine, "SET md_live name \"Live (old)\"")).Ok);
        Assert.Equal("Live (old)", engine.Store.Project.FindMedia("md_live")!.Name);
        Assert.True((await Run(engine, "MEDIA REMOVE md_page")).Ok);
        Assert.Null(engine.Store.Project.FindMedia("md_page"));
        var added = await Run(engine, "ADD media {\"kind\":\"stream\",\"name\":\"Live\",\"location\":\"http://cdn.example.com/live/index.m3u8\"}");
        Assert.True(added.Ok, added.Payload);
    }

    /// <summary>The camera's address: a stream plays from it, so the scrub keeps it (where the node publishes it is the live inputs'
    /// own open row, not this batch's).</summary>
    private const string Camera = "rtsp://admin:pw9@10.0.0.9/stream1";

    /// <summary>A show an older node kept with passes in it: a window page named by its address, a page with a viewing code under a
    /// name of its own, a camera whose default name is its address with a user, a window and an HLS address with a token filed as
    /// streams, a file whose name holds a window's address in brackets, and a YouTube embed and a file, which carry none.</summary>
    private static Project KeptPasses() => Project.CreateDefault("Kept") with
    {
        Media =
        [
            new MediaItem("md_window", $"https://10.0.0.5:9924/w/{Window}", MediaKind.Web, $"https://10.0.0.5:9924/w/{Window}", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_view", "Stage timer", MediaKind.Web, "https://hall-a.local/stage?view=k2m4zz", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_cam", Camera, MediaKind.Stream, Camera, "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_wfeed", "Window feed", MediaKind.Stream, $"https://hall-a.local/w/{Window}", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_feed", "Feed", MediaKind.Stream, "http://cdn.example.com/live/index.m3u8?token=4321zz", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_lobby", $"Lobby (https://10.0.0.5:9924/w/{Window})", MediaKind.Video, "/shows/lobby.mov", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_tube", "Trailer", MediaKind.Embed, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_clip", "Clip", MediaKind.Video, "/shows/clip.mov", "", 0, MediaInfo.Unknown, []),
        ],
    };

    /// <summary>The scrub's row for <see cref="KeptPasses"/>.</summary>
    private const string KeptPassesRow = "6 media items held an address that carries a pass, which PlayMap keeps nowhere: 10.0.0.5 ‹private address›,"
        + " Stage timer, 10.0.0.9 ‹private address›, Window feed, Feed, Lobby (10.0.0.5 ‹private address›) — such an address is cleared where"
        + " the item kept it (a stream whose only pass is a user keeps it to play from), and one in a name reads as its host";

    /// <summary>The journal's scrub row, once the tick has written it.</summary>
    private static async Task<JournalRow> ScrubRow(EngineCore engine, int nth = 1)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var rows = engine.Journal.Recent(engine.Journal.RecentCapacity).Where(r => r.Kind == "media").ToList();
            if (rows.Count >= nth)
                return rows[nth - 1];
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "no tick scrubbed the project's kept passes");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ANodesOwnProjectWithKeptPassesIsScrubbedByOneRowAndNoUndoBringsThemBack()
    {
        var kept = KeptPasses();
        await using var engine = new EngineCore(EngineServices.ForTests(), kept);

        var row = await ScrubRow(engine);

        Assert.Equal(("media", true, (string?)null, (string?)null), (row.Kind, row.Ok, row.Who, row.Line));
        Assert.Equal(KeptPassesRow, row.Text);
        var p = engine.Store.Project;
        Assert.Equal(("10.0.0.5 ‹private address›", ""), (p.FindMedia("md_window")!.Name, p.FindMedia("md_window")!.Location));
        Assert.Equal(("Stage timer", ""), (p.FindMedia("md_view")!.Name, p.FindMedia("md_view")!.Location));
        // A stream plays from its address when its only pass is a user: its name is scrubbed, its location kept. A window or a token
        // filed as a stream is no address a player reads it by: cleared.
        Assert.Equal(("10.0.0.9 ‹private address›", Camera), (p.FindMedia("md_cam")!.Name, p.FindMedia("md_cam")!.Location));
        Assert.Equal(("Window feed", ""), (p.FindMedia("md_wfeed")!.Name, p.FindMedia("md_wfeed")!.Location));
        Assert.Equal(("Feed", ""), (p.FindMedia("md_feed")!.Name, p.FindMedia("md_feed")!.Location));
        // An address glued inside a name's brackets is read as one: the brackets stay, the address reads as its host.
        Assert.Equal(("Lobby (10.0.0.5 ‹private address›)", "/shows/lobby.mov"), (p.FindMedia("md_lobby")!.Name, p.FindMedia("md_lobby")!.Location));
        Assert.Same(kept.Media[6], p.FindMedia("md_tube"));
        Assert.Same(kept.Media[7], p.FindMedia("md_clip"));

        // The scrub is no undo step: UNDO has nothing to take back, and an edit's undo after it keeps the pass out.
        Assert.Empty(engine.Store.Steps);
        var undo = await Run(engine, "UNDO");
        Assert.False(undo.Ok);
        Assert.Contains("nothing to undo", undo.Payload, StringComparison.Ordinal);
        Assert.True((await Run(engine, "SET md_clip name \"Clip 2\"")).Ok);
        Assert.True((await Run(engine, "UNDO")).Ok);
        Assert.Equal("Clip", engine.Store.Project.FindMedia("md_clip")!.Name);
        Assert.Equal("10.0.0.5 ‹private address›", engine.Store.Project.FindMedia("md_window")!.Name);

        // Nothing the node publishes holds a pass but the stream's own address, which it plays from; a stream cleared reads unplayable.
        var list = await Run(engine, "MEDIA LIST");
        Assert.True(list.Ok);
        HoldsNoPass(list.Payload!.Replace(Camera, "", StringComparison.Ordinal), "MEDIA LIST");
        var feed = Listed(list)["md_feed"];
        Assert.Equal((false, false, "unplayable"), (feed.GetProperty("present").GetBoolean(), feed.GetProperty("ready").GetBoolean(), feed.GetProperty("state").GetString()));
        HoldsNoPass(engine.State().Json.Replace(Camera, "", StringComparison.Ordinal), "STATE");
        HoldsNoPass(engine.Context().Replace(Camera, "", StringComparison.Ordinal), "the context");
        Assert.DoesNotContain(Window, engine.ProjectJson().Json, StringComparison.Ordinal);
        Assert.DoesNotContain("k2m4zz", engine.ProjectJson().Json, StringComparison.Ordinal);
        JournalHoldsNoPass(engine);
        Assert.Single(engine.Journal.Recent(engine.Journal.RecentCapacity), r => r.Kind == "media");
    }

    [Fact]
    public async Task AProjectOpenedWithAKeptPassIsScrubbedAndSavedWithoutIt()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-kept-");
        try
        {
            var path = Path.Combine(folder.FullName, "kept.playmap");
            await File.WriteAllTextAsync(path, ProjectJson.Serialize(KeptPasses()));
            Assert.Contains(Window, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            await using var engine = new EngineCore(EngineServices.ForTests());

            var opened = await Run(engine, "PROJECT OPEN " + WireGrammar.Quote(path));
            Assert.True(opened.Ok, opened.Payload);
            var row = await ScrubRow(engine);
            Assert.Equal(KeptPassesRow, row.Text);
            var saved = await Run(engine, "PROJECT SAVE");
            Assert.True(saved.Ok, saved.Payload);

            var text = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(Window, text, StringComparison.Ordinal);
            Assert.DoesNotContain("k2m4zz", text, StringComparison.Ordinal);
            var read = ProjectJson.Read(text).Project;
            Assert.Equal(("10.0.0.5 ‹private address›", ""), (read.FindMedia("md_window")!.Name, read.FindMedia("md_window")!.Location));
            JournalHoldsNoPass(engine);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A show an older node kept with a page, an embed and a stream, none carrying a pass, and a page and a stream whose
    /// addresses the scrub cleared.</summary>
    private static Project KeptPages() => Project.CreateDefault("Pages") with
    {
        Media =
        [
            new MediaItem("md_page", "Schedule", MediaKind.Web, "https://www.example.com/schedule", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_tube", "Trailer", MediaKind.Embed, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_live", "Live", MediaKind.Stream, "rtsp://10.0.0.9/stream1", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_gone", "Gone", MediaKind.Web, "", "", 0, MediaInfo.Unknown, []),
            new MediaItem("md_dark", "Dark", MediaKind.Stream, "", "", 0, MediaInfo.Unknown, []),
        ],
    };

    private static Dictionary<string, JsonElement> Listed(CommandReply list)
    {
        Assert.True(list.Ok, list.Payload);
        using var doc = JsonDocument.Parse(list.Payload!);
        return doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.Clone());
    }

    [Fact]
    public async Task APageReadsUnplayableWithAMediaEdgeOrWithoutOneAndIsNeverFetched()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-pages-");
        try
        {
            await using var backend = new MediaBackend(new NodeHome(Path.Combine(folder.FullName, "node")), NullLogger.Instance);
            await using var withEdge = new EngineCore(EngineServices.ForTests() with { Media = backend }, KeptPages());
            await using var withoutEdge = new EngineCore(EngineServices.ForTests(), KeptPages());

            foreach (var engine in new[] { withEdge, withoutEdge })
            {
                var listed = Listed(await Run(engine, "MEDIA LIST"));
                foreach (var (id, present, words) in new[]
                {
                    ("md_page", true, "a web page — PlayMap has no browser source for pages and embeds (ROADMAP L10)"),
                    ("md_tube", true, "an embed — PlayMap has no browser source for pages and embeds (ROADMAP L10)"),
                    ("md_gone", false, "a web page whose address carried a pass and was cleared — PlayMap has no browser source for pages and embeds (ROADMAP L10)"),
                    ("md_dark", false, "a stream with no address (one that carried a pass is cleared) — import it again by an address that carries none"),
                })
                {
                    var item = listed[id];
                    Assert.Equal(present, item.GetProperty("present").GetBoolean());
                    Assert.False(item.GetProperty("ready").GetBoolean());
                    Assert.Equal("unplayable", item.GetProperty("state").GetString());
                    Assert.Equal(words, item.GetProperty("words").GetString());
                }
                // A stream is read from the network when it plays, as before.
                Assert.True(listed["md_live"].GetProperty("ready").GetBoolean());
                Assert.Equal("ready", listed["md_live"].GetProperty("state").GetString());

                var fetch = await Run(engine, "MEDIA FETCH md_page");
                Assert.False(fetch.Ok);
                Assert.Equal("Schedule is a web page, not a file, and PlayMap has no browser source for it (ROADMAP L10): never fetched", fetch.Payload);
                Assert.Equal("Trailer is an embed, not a file, and PlayMap has no browser source for it (ROADMAP L10): never fetched",
                    (await Run(engine, "MEDIA FETCH md_tube")).Payload);
                Assert.Equal("Live is a live input: it is received from the network, never fetched", (await Run(engine, "MEDIA FETCH md_live")).Payload);

                // Nothing the show uses never plays: the library is ready.
                Assert.Equal("{\"items\":5,\"missing\":0,\"ready\":true,\"unplayable\":0}", (await Run(engine, "MEDIA STATUS")).Payload);
            }

            // A page the show uses (here, the look a SAFE panic shows) is a library that is not ready, with a media edge or without one.
            var used = KeptPages() with
            {
                Show = KeptPages().Show with { Panic = [new OutputPanicPlan("op_1", PanicOutcome.Safe, 1000, SourceRef.Media("md_page"))] },
            };
            await using var usedWithEdge = new EngineCore(EngineServices.ForTests() with { Media = backend }, used);
            await using var usedWithoutEdge = new EngineCore(EngineServices.ForTests(), used);
            foreach (var engine in new[] { usedWithEdge, usedWithoutEdge })
            {
                using var status = JsonDocument.Parse((await Run(engine, "MEDIA STATUS")).Payload!);
                Assert.Equal((false, 1), (status.RootElement.GetProperty("ready").GetBoolean(), status.RootElement.GetProperty("unplayable").GetInt32()));
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A media edge that records where it was asked to import from and finds a clip there at once.</summary>
    private sealed class Recorded : IMediaBackend
    {
        private readonly List<string> _locations = [];

        public int Asked => _locations.Count;

        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct)
        {
            _locations.Add(location);
            return Task.FromResult(new MediaImport(MediaKind.Video, MediaInfo.Unknown, "", 16, Path.GetFileNameWithoutExtension(location)));
        }

        public IVideoSource? Open(MediaItem item) => null;

        public PlayMap.Engine.MediaReadiness Readiness(MediaItem item) => new(true, false, false, false, "a recorded import");

        public string? PathForHash(string contentHash) => null;
    }
}
