using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Content;

/// <summary>An engine on a manual clock for the content verbs: lines run as the desk, refusals leave the revision where it was, and
/// STATE is read as JSON.</summary>
internal sealed class ContentRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = SessionInfo.Desk("t");

    public ContentRig(Project? project = null)
    {
        Clock = new ManualClock(1_000_000_000);
        Engine = new EngineCore(EngineServices.ForTests(Clock), project);
    }

    /// <summary>A show with a particle flow (Sparks), a canvas whose layer shows it (Main) and an empty canvas (Plate).</summary>
    public static Project Show()
    {
        var sparks = new Generator("gn_sparks", "Sparks", "particles.flow", ParamSet.Empty);
        var main = new Canvas("cv_main", "Main", 1920, 1080, "#000000",
            [new Layer("ly_sparks", "Sparks", SourceRef.Generator("gn_sparks"), new RectD(0, 0, 1920, 1080), 1, LayerBlend.Normal, true)]);
        var plate = new Canvas("cv_plate", "Plate", 1920, 1080, "#000000", []);
        return Project.CreateDefault("Content show") with { Canvases = [main, plate], Generators = [sparks] };
    }

    public ManualClock Clock { get; }

    public EngineCore Engine { get; }

    public async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    public async Task<CommandReply> Ok(string line)
    {
        var r = await Run(line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r;
    }

    /// <summary>A refusal: its words, the project not moved.</summary>
    public async Task<string> Refused(string line)
    {
        var revision = Engine.Store.Current.Revision;
        var r = await Run(line);
        Assert.False(r.Ok, $"{line} was not refused: {r.Effect}");
        Assert.Equal(revision, Engine.Store.Current.Revision);
        return r.Payload ?? "";
    }

    public JsonElement State()
    {
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.Clone();
    }

    public static JsonElement Json(CommandReply r)
    {
        using var doc = JsonDocument.Parse(r.Payload!);
        return doc.RootElement.Clone();
    }

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
