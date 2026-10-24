using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>Round 3's fields that nothing on this node draws yet — an output's colour depth, HDR signal, nit target, frames in flight,
/// watermark and pass-through, a canvas's span and a viewpoint's fit — are refused by the generic SET and ADD with the words of a verb not
/// available yet, so no edit reports as done what the air never follows, until the package that draws a field takes it in its own check.
/// The check compares with the stored entity: a project opened with such a value is edited elsewhere freely, and a field set back to its
/// default is taken.</summary>
public sealed class RoundThreeFieldTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    private static Project WithViewpoint()
    {
        var project = Project.CreateDefault("Fields");
        var eye = new Viewpoint("vp_eye", "Eye", Intrinsics.FromThrowRatio(1920, 1080, 1.0), Pose.LookAt(new Vec3d(0, 0, 4), Vec3d.Zero, Vec3d.UnitY));
        return project.WithStage(s => s with { Viewpoints = [eye] });
    }

    public static TheoryData<string, string, string> OutputFields => new()
    {
        { "colorBits", "10", "colorBits" },
        { "hdr", "\"hdr10\"", "hdr" },
        { "nitTarget", "400", "nitTarget" },
        { "framesInFlight", "1", "framesInFlight" },
        { "watermark", ProjectJson.SerializeEntity(new OutputWatermark(WatermarkPlace.Top, 0, 0, 8)), "watermark" },
        { "passThrough", "true", "passThrough" },
    };

    [Theory]
    [MemberData(nameof(OutputFields))]
    public async Task AGenericSetOfAnOutputFieldNothingDrawsIsRefusedAndMovesNothing(string field, string value, string words)
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), WithViewpoint());
        var output = engine.Store.Project.Outputs[0];
        var revision = engine.Store.Current.Revision;

        var reply = await Run(engine, $"SET {output.Id} {field} {value}");

        Assert.False(reply.Ok);
        Assert.Equal(VerbCatalog.NotAvailableYet(words), reply.Payload);
        Assert.Equal(revision, engine.Store.Current.Revision);
        Assert.Equal(output, engine.Store.Project.Outputs[0]);
    }

    [Fact]
    public async Task ASpanAFitAndAnAddedOutputWithSuchAFieldAreRefusedToo()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), WithViewpoint());
        var canvas = engine.Store.Project.Canvases[0];
        var output = engine.Store.Project.Outputs[0];
        var revision = engine.Store.Current.Revision;
        var span = new CanvasSpan([output.Id], 1, 1, 1, 0, SpanOverlapUnit.Px, canvas.Width, canvas.Height, []);
        var fit = new ViewpointFit([], 5, new Vec3d(0, 0, -1), Intrinsics.FromThrowRatio(1920, 1080, 1.0));

        var spanned = await Run(engine, $"SET {canvas.Id} span {ProjectJson.SerializeEntity(span)}");
        var fitted = await Run(engine, $"SET vp_eye fit {ProjectJson.SerializeEntity(fit)}");
        var added = await Run(engine, "ADD output {\"name\":\"Wide\",\"passThrough\":true}");

        Assert.Equal(VerbCatalog.NotAvailableYet("span"), spanned.Payload);
        Assert.Equal(VerbCatalog.NotAvailableYet("fit"), fitted.Payload);
        Assert.Equal(VerbCatalog.NotAvailableYet("passThrough"), added.Payload);
        Assert.False(spanned.Ok || fitted.Ok || added.Ok);
        Assert.Equal(revision, engine.Store.Current.Revision);
    }

    [Fact]
    public async Task AProjectOpenedWithSuchAValueIsEditedElsewhereAndTheFieldGoesBackToItsDefault()
    {
        var project = WithViewpoint();
        project = project with { Outputs = project.Outputs.Update(project.Outputs[0].Id, o => o with { PassThrough = true, ColorBits = 10 }) };
        await using var engine = new EngineCore(EngineServices.ForTests(), project);
        var id = engine.Store.Project.Outputs[0].Id;

        Assert.True((await Run(engine, $"SET {id} name Front")).Ok);
        Assert.True((await Run(engine, $"SET {id} passThrough false")).Ok);
        Assert.True((await Run(engine, $"SET {id} colorBits 8")).Ok);

        var o = engine.Store.Project.FindOutput(id)!;
        Assert.Equal(("Front", false, 8), (o.Name, o.PassThrough, o.ColorBits));
    }
}
