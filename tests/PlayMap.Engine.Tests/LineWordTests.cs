using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Model;
using PlayMap.Engine.Phone;
using PlayMap.Engine.Show;
using PlayMap.Engine.Tests.Show;
using PlayMap.Net.Phone;

namespace PlayMap.Engine.Tests;

/// <summary>A line the node writes for a person or a model to run (a suggestion, a preflight row's fix) reads back as the words it names,
/// whatever marks they hold: a word through <see cref="WireGrammar.Quote"/>, SET's value as a JSON string.</summary>
public sealed class LineWordTests
{
    /// <summary>A name with a quote, a backslash and spaces: hand quoting without escapes reads it as other words.</summary>
    private const string Name = @"Hall ""B"" \ left";

    [Fact]
    public void ADiscoveredProjectorsAddLineNamesItAsItCalledItself()
    {
        var found = new DiscoveredProjector("10.0.0.20", "00:0a:1b:2c:3d:4e", 1, "EPSON", "EB L1", null, Name, null, ProjectorProtocol.PjLink, "", null, "");

        Assert.Equal(["DEVICE", "ADD", Name, "pjlink", "10.0.0.20"], Line(EngineCore.DiscoveredAddLine(found)).Words);
        // A projector that gives no name is added under its model.
        Assert.Equal(["DEVICE", "ADD", "EB L1", "pjlink", "10.0.0.20"], Line(EngineCore.DiscoveredAddLine(found with { Name = "" })).Words);
    }

    [Fact]
    public async Task AnUnpairedPhoneRigsInviteNamesTheRigSoItPairsThatRigAgain()
    {
        // PHONE INVITE pairs a rig of the very name it is given again, so the fix line's name is the rig's to the mark.
        var folder = Directory.CreateTempSubdirectory("playmap-linewords-").FullName;
        try
        {
            await using var hub = new PhoneHub(new PhoneHubOptions { KeysPath = Path.Combine(folder, "phones.json"), NodeId = "0badc0de" });
            var rig = new CameraRig("cm00000001", Name, CameraSourceKind.Phone, "ph00000001", Intrinsics.FromFieldOfView(320, 240, 68),
                LensDistortion.None, Pose.Identity, false);
            var project = Project.CreateDefault("Phones").WithStage(s => s with { Cameras = [rig] });
            await using var engine = new EngineCore(EngineServices.ForTests() with { Phones = hub }, project);

            var row = Assert.Single(await new PhonePreflight().RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None));

            Assert.Equal("not connected", row.Value);
            Assert.Equal(["PHONE", "INVITE", Name], Line(row.Fix!).Words);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnOutputsFixLineSetsTheOtherDisplaysKeyAsTheMachineNamesIt()
    {
        // SET reads its value as JSON: the key rides as a JSON string, so a key holding a quote or a backslash is set as itself.
        var displays = new Displays();
        displays.Screens.Add(new DisplayInfo(Name, "Side", 0, 0, 1280, 720, 60, true));
        var project = ShowRig.OneScreen();
        project = project with { Outputs = [project.Outputs[0] with { Kind = OutputKind.Display, DisplayKey = "DP-1" }] };
        await using var rig = new ShowRig(project, s => s with { Outputs = displays });
        var id = project.Outputs[0].Id;

        var row = Assert.Single(await new OutputsCheck().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));

        Assert.Equal($@"SET {id} displayKey ""Hall \""B\"" \\ left""", row.Fix);
        await rig.Ok(row.Fix!);
        Assert.Equal(Name, rig.Engine.Store.Project.FindOutput(id)!.DisplayKey);
    }

    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line;
    }

    /// <summary>The displays a test sets.</summary>
    private sealed class Displays : IOutputBackend
    {
        public List<DisplayInfo> Screens { get; } = [];

        public string RendererName => "test";

        IReadOnlyList<DisplayInfo> IOutputBackend.Displays() => [.. Screens];

        public IReadOnlyList<OutputHealth> Health() => [];
    }
}
