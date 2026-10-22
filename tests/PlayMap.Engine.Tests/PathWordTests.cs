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
