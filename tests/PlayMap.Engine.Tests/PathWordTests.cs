using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Facade;

namespace PlayMap.Engine.Tests;

/// <summary>A path on a line as the grammar reads it: inside quotes a backslash escapes the mark after it (Patterns' reading), so every
/// producer writes a path through <see cref="WireGrammar.Quote"/> (bare when it holds no space or quote, else quoted with each backslash
/// doubled), and a verb that takes a path refuses one quoted with single backslashes, which the grammar would read as another file,
/// naming the spelling that reads as typed. A Windows path is text until a verb opens it, so every case runs on every OS.</summary>
public sealed class PathWordTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("test");

    [Fact]
    public async Task AWindowsPathQuotedWithSingleBackslashesIsRefusedWithTheSpellingThatReadsAsTyped()
    {
        const string Typed = @"C:\Users\me\clip.mp4";
        var media = new Recorded();
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });
        var project = engine.Store.Project;
        var line = $"MEDIA IMPORT \"{Typed}\" Clip";

        var reply = await Run(engine, line);

        // The grammar reads the quoted word as Patterns does; the verb refuses it rather than look for a file nobody named.
        Assert.Equal("C:Usersmeclip.mp4", Line(line).Word(2));
        Assert.False(reply.Ok);
        Assert.Contains($"so \"{Typed}\" reads as C:Usersmeclip.mp4", reply.Payload, StringComparison.Ordinal);
        Assert.Contains($"write the path bare, {Typed},", reply.Payload, StringComparison.Ordinal);
        Assert.Equal(Typed, Line("MEDIA IMPORT " + WireGrammar.Quote(Typed)).Word(2));
        Assert.Equal(0, media.Asked);
        Assert.Same(project, engine.Store.Project);
        var row = engine.Journal.Recent(1)[0];
        Assert.Equal(("refused", line, false, reply.Payload), (row.Kind, row.Line, row.Ok, row.Text));
    }

    /// <summary>Every row of the catalogue that takes a path (<see cref="VerbSyntax.TakesPath"/>).</summary>
    public static TheoryData<string> PathRows()
    {
        var rows = new TheoryData<string>();
        foreach (var spec in VerbCatalog.All.Where(VerbSyntax.TakesPath))
            rows.Add(spec.Name);
        return rows;
    }

    [Theory]
    [MemberData(nameof(PathRows))]
    public async Task EveryRowThatTakesAPathRefusesOneQuotedWithSingleBackslashesAtTheDoor(string verb)
    {
        // On the lane or off it, before the show's gate or the verb's handler reads a word: nothing is read, written or changed.
        const string Typed = @"C:\Users\me\show\a.x";
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = new Recorded() });
        var project = engine.Store.Project;
        var line = $"{verb} \"{Typed}\"";

        var reply = await Run(engine, line);

        Assert.False(reply.Ok);
        Assert.Equal($"{verb}: inside quotes a backslash escapes the mark after it, so \"{Typed}\" reads as C:Usersmeshowa.x — write the path bare, {Typed}, or with / in place of each \\",
            reply.Payload);
        Assert.Same(project, engine.Store.Project);
        Assert.False(File.Exists("C:Usersmeshowa.x"));
        var row = engine.Journal.Recent(1)[0];
        Assert.Equal(("refused", line, false), (row.Kind, row.Line, row.Ok));
    }

    [Fact]
    public async Task AWindowsPathWithASpaceIsRefusedWithItsBackslashesDoubledInsideTheQuotes()
    {
        const string Typed = @"C:\My clips\take 1.mp4";
        var media = new Recorded();
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });

        var reply = await Run(engine, $"MEDIA IMPORT \"{Typed}\"");

        Assert.False(reply.Ok);
        var spelled = WireGrammar.Quote(Typed);
        Assert.Equal("\"C:\\\\My clips\\\\take 1.mp4\"", spelled);
        Assert.Contains($"write the path {spelled}, each \\ doubled inside the quotes,", reply.Payload, StringComparison.Ordinal);
        Assert.Equal(Typed, Line("MEDIA IMPORT " + spelled).Word(2));
        Assert.Equal(0, media.Asked);
        Assert.Empty(engine.Store.Project.Media);
    }

    [Fact]
    public async Task APathWrittenAsAProducerWritesItReadsAsTypedAndIsImported()
    {
        // A file whose path holds a backslash and a space (on Windows a folder; elsewhere a mark of the file's own name), written as
        // the desk's picker, the Companion module and the node's own lines write a path.
        var folder = Directory.CreateTempSubdirectory("playmap-pathword-");
        try
        {
            var file = Path.Combine(folder.FullName, "My clips\\take 1.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, new byte[16]);
            var media = new Recorded();
            await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });

            var reply = await Run(engine, "MEDIA IMPORT " + WireGrammar.Quote(file) + " Take");

            Assert.True(reply.Ok, reply.Payload);
            Assert.Equal([file], media.Locations);
            Assert.Equal((file, "Take"), engine.Store.Project.Media.Select(m => (m.Location, m.Name)).Single());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AModelRefusedAsMediaNamesTheSurveyImportThatReadsItsPathAsTyped()
    {
        var file = Path.Combine(Path.GetTempPath(), "My scans", "front.ply");
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = new Recorded() });

        var reply = await Run(engine, "MEDIA IMPORT " + WireGrammar.Quote(file));

        Assert.False(reply.Ok);
        var suggested = "SURVEY IMPORT " + WireGrammar.Quote(file);
        Assert.Contains($"— {suggested} brings it onto the stage", reply.Payload, StringComparison.Ordinal);
        Assert.Equal(file, Line(suggested).Word(2));
    }

    [Fact]
    public async Task ASurveysRowsCarryALineThatReadsItsPathAsTyped()
    {
        // The rows a survey import writes when it ends carry the import's line: read back, it names the file that was read.
        await using var rig = new SiteRig();
        var file = rig.File("broken scan.ply");
        await File.WriteAllTextAsync(file, "this is not a ply file");

        await rig.Ok($"SURVEY IMPORT {WireGrammar.Quote(file)} CRS LOCAL");
        await rig.Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Equal("failed", rig.Engine.SurveyJob!.State);
        var row = rig.Engine.Journal.Recent(200).Last(r => r.Kind == "refused" && r.Text.StartsWith("broken scan was not imported", StringComparison.Ordinal));
        Assert.Equal(["SURVEY", "IMPORT", file], Line(row.Line!).Words);
    }

    [Fact]
    public async Task ABindingsTargetWhosePathLosesItsBackslashesIsRefusedHoweverTheBindingIsMade()
    {
        // A control fires its target later, rebuilt from the words the grammar read, past the door: a path that lost its backslashes would
        // fire a line naming another file. In words, in JSON, as a set, learnt or set on a binding, such a target is refused.
        const string Refusal = @"LED IMPORT: inside quotes a backslash escapes the mark after it, so ""C:\leds\a.csv"" reads as C:ledsa.csv — write the path bare, C:\leds\a.csv, or with / in place of each \";
        await using var engine = new EngineCore(EngineServices.ForTests());
        var made = await Run(engine, "BIND ADD MIDI CC 1 9 TO PLAYLIST NEW Bound");
        Assert.True(made.Ok, made.Payload);
        var id = JsonDocument.Parse(made.Payload!).RootElement.GetProperty("id").GetString()!;
        var project = engine.Store.Project;
        string[] exact =
        [
            @"BIND ADD MIDI CC 1 7 TO LED IMPORT ""C:\leds\a.csv"" SCALE *",
            @"BIND LEARN LED IMPORT ""C:\leds\a.csv"" SCALE *",
            @"BIND LEARN FROM MIDI LED IMPORT ""C:\leds\a.csv""",
        ];
        string[] carried =
        [
            """BIND ADD {"input":"MIDI CC 1 7","target":"LED IMPORT \"C:\\leds\\a.csv\" SCALE *"}""",
            """BIND ADD {"bindings":[{"input":"MIDI CC 1 7","target":"LED IMPORT \"C:\\leds\\a.csv\" SCALE *"}]}""",
            "SET " + id + @" target ""LED IMPORT \""C:\\leds\\a.csv\"" SCALE *""",
        ];
        foreach (var line in exact.Concat(carried))
        {
            var reply = await Run(engine, line);
            Assert.False(reply.Ok, line);
            if (exact.Contains(line))
                Assert.Equal(Refusal, reply.Payload);
            else
                Assert.EndsWith(Refusal, reply.Payload, StringComparison.Ordinal);
            var row = engine.Journal.Recent(1)[0];
            Assert.Equal(("refused", line, false), (row.Kind, row.Line, row.Ok));
        }
        Assert.Same(project, engine.Store.Project);
        Assert.Equal(["PLAYLIST NEW Bound"], engine.Store.Project.Control.Bindings.Select(b => b.Target));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("control").GetProperty("learning").ValueKind);
        var injected = await Run(engine, "CONTROL INJECT MIDI CC 1 7 0.5");
        Assert.False(injected.Ok);
        Assert.StartsWith("nothing is bound to MIDI CC 1 7", injected.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABindingsTargetWrittenAsAProducerWritesItFiresALineThatReadsItsPathAsTyped()
    {
        // The value word is filled in and the line rebuilt from the words the grammar read: the path in it is the path that was typed.
        const string Typed = @"C:\My leds\a.csv";
        await using var engine = new EngineCore(EngineServices.ForTests());
        var made = await Run(engine, "BIND ADD MIDI CC 1 7 TO LED IMPORT " + WireGrammar.Quote(Typed) + " SCALE *");
        Assert.True(made.Ok, made.Payload);

        var injected = await Run(engine, "CONTROL INJECT MIDI CC 1 7 0.5");

        Assert.True(injected.Ok, injected.Payload);
        var fired = Assert.Single(JsonDocument.Parse(injected.Payload!).RootElement.GetProperty("fired").EnumerateArray());
        Assert.Equal(["LED", "IMPORT", Typed, "SCALE", "0.5"], Line(fired.GetProperty("line").GetString()!).Words);
    }

    [Fact]
    public async Task ARefusedPathThatReadsAsALineHoldingASecretIsKeptOutOfTheReplyAndTheRow()
    {
        // The journal keeps such a phrase back from the row's line; the refusal, which is the reply and the row's words, keeps it back too.
        const string Secret = "s3cret";
        await using var engine = new EngineCore(EngineServices.ForTests() with { Media = new Recorded() });
        foreach (var line in new[] { @"MEDIA IMPORT ""AUTH s3cret\x"" Take", @"BIND ADD MIDI CC 1 7 TO MEDIA IMPORT ""AUTH s3cret\x""" })
        {
            var reply = await Run(engine, line);

            Assert.False(reply.Ok, line);
            Assert.Contains("so the quoted path loses a backslash", reply.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, reply.Payload, StringComparison.Ordinal);
            var row = engine.Journal.Recent(1)[0];
            Assert.Equal(("refused", false), (row.Kind, row.Ok));
            Assert.DoesNotContain(Secret, row.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, row.Line, StringComparison.Ordinal);
        }
        Assert.Empty(engine.Store.Project.Control.Bindings);
    }

    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line;
    }

    private static async Task<CommandReply> Run(EngineCore engine, string text) => await engine.HandleAsync(Line(text), Desk, CancellationToken.None);

    /// <summary>A media edge that records where it was asked to import from and finds a clip there at once.</summary>
    private sealed class Recorded : IMediaBackend
    {
        private readonly List<string> _locations = [];

        public IReadOnlyList<string> Locations => _locations;

        public int Asked => _locations.Count;

        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct)
        {
            _locations.Add(location);
            return Task.FromResult(new MediaImport(MediaKind.Video, MediaInfo.Unknown, "", 16, Path.GetFileNameWithoutExtension(location)));
        }

        public IVideoSource? Open(MediaItem item) => null;

        public MediaReadiness Readiness(MediaItem item) => new(true, false, false, false, "a recorded import");

        public string? PathForHash(string contentHash) => null;
    }
}
