using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Show;
using PlayMap.Net.Bus;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>A busy node's context as Patterns' Eye reads it: more than thirty items, shaped so the Eye's twelve hold show and machine
/// first, every output that is not green, the playlist that plays and the projector its device makes amber; every link one of the
/// Eye's eleven verbs; every item still there for an assistant.</summary>
public sealed class EyeContextTests
{
    internal static Project Busy()
    {
        var p = ShowRig.OneScreen();
        var outputs = Enumerable.Range(1, 20)
            .Select(i => p.Outputs[0] with { Id = "op_" + i, Name = "Out " + i, Mode = OutputMode.Canvas, Source = p.Outputs[0].Source })
            .ToList();
        var projectors = Enumerable.Range(1, 2).Select(i => new Projector("pj0a0b0c0" + i, "P" + i, Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5),
            LensDistortion.None, Pose.LookAt(new Vec3d(i, 1.5, -6), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 12000, "#FF8800", false, [], SolveLocks.None, 0.8)).ToList();
        outputs[0] = outputs[0] with { Mode = OutputMode.Projector, ProjectorId = projectors[0].Id };
        outputs[1] = outputs[1] with { Mode = OutputMode.Projector, ProjectorId = projectors[1].Id };
        return p with
        {
            Outputs = [.. outputs],
            Playlists = [p.Playlists[0], p.Playlists[0] with { Id = "pl_walk", Name = "Walk-in" }, p.Playlists[0] with { Id = "pl_out", Name = "Outro" }],
            Stage = p.Stage with
            {
                Projectors = [.. projectors],
                Devices =
                [
                    new DeviceLink("dv0a0b0c01", "Box 1", "pjlink", "10.0.0.21", 0, "", projectors[0].Id, "op_1", 0, false),
                    new DeviceLink("dv0a0b0c02", "Box 2", "pjlink", "10.0.0.22", 0, "", projectors[1].Id, "op_2", 0, false),
                ],
            },
        };
    }

    [Fact]
    public async Task ABusyNodeFitsTheEyesTwelveWithShowAndMachineFirst()
    {
        var outputs = new HealthTests.Outputs
        {
            Now = [.. Enumerable.Range(1, 20).Select(i => new OutputHealth("op_" + i, i != 7, i == 7 ? 0 : 60, i == 12 ? 3 : 0, 4.1, "DP-" + i))],
        };
        var services = HealthTests.Services(new ManualTime(), outputs, new HealthTests.FakeSampler());
        await using var engine = new EngineCore(services, Busy());
        Assert.True(WireGrammar.TryParse("PLAYLIST GO pl_show", out var go, out _));
        Assert.True((await engine.HandleAsync(go, SessionInfo.Desk("test"), CancellationToken.None)).Ok);

        var context = ContextBuilder.Build(engine);
        var items = context.Items;
        Assert.True(items.Count >= 30, $"{items.Count} items");
        Assert.Equal("show", items[0].Id);
        Assert.Equal("machine", items[1].Id);
        Assert.Equal("outputs", items[2].Id);
        Assert.Equal("18 of 20 outputs green", items[2].State);
        Assert.Contains(items, i => i.Id == "playlists" && i.State.StartsWith("2 idle", StringComparison.Ordinal));
        // Every item is still there: each output one by one, each device, each idle playlist.
        Assert.Equal(20, items.Count(i => i.Id.StartsWith("output:", StringComparison.Ordinal)));
        Assert.Equal(2, items.Count(i => i.Id.StartsWith("device:", StringComparison.Ordinal)));
        Assert.Contains(items, i => i.Id == "playlist:pl_walk");
        // The device's facts are said on its projector.
        Assert.Contains("Box 1", items.Single(i => i.Id == "projector:pj0a0b0c01").State, StringComparison.Ordinal);

        var window = ContextShape.EyeWindow(items).Select(i => i.Id).ToList();
        var said = string.Join(", ", ContextShape.EyeWindow(items).Select(i => $"{i.Id}={i.Light}"));
        Assert.Contains("show", window);
        Assert.Contains("machine", window);
        Assert.Contains("output:op_7", window);     // not open: red
        Assert.Contains("output:op_12", window);    // dropping frames: amber
        Assert.Contains("playlist:pl_show", window);
        // The green outputs stand in the aggregate; room left in the twelve is filled from the end, in order.
        Assert.False(window.Contains("output:op_20"), said);
        Assert.DoesNotContain("playlist:pl_walk", window);
        Assert.All(context.Links, l => Assert.True(ContextLinkKinds.IsEyeVerb(l.Kind), $"{l.From} → {l.To}: {l.Kind}"));
        Assert.Contains(context.Links, l => l is { From: "show", To: "outputs", Kind: "controls" });

        // The machine item: worst of this node's HEALTH, with its role, build and renderer.
        var machine = items[1];
        Assert.Equal(Light.Red, machine.Light);
        Assert.Contains("solo", machine.State, StringComparison.Ordinal);
        Assert.Contains("build " + HealthTests.TestBuild.Informational, machine.State, StringComparison.Ordinal);
        Assert.Contains("test", machine.State, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelloOffersEyeAndShowSixteenCapsInItsBytes()
    {
        var settings = NodeSettings.CreateDefault("FOH-A") with { NodeId = "0badc0de", Token = "" };
        var hello = HelloService.PlayMapHello(settings, 1760101234567, "sha256:" + new string('a', 64), "RACK-1");
        Assert.Contains("eye", hello.Caps);
        Assert.Contains("show", hello.Caps);
        Assert.Equal(16, hello.Caps.Count);
        Assert.Equal(16, hello.Caps.Distinct(StringComparer.Ordinal).Count());
        var bytes = hello.ToJson();
        Assert.InRange(bytes.Length, 1, ShowBusConstants.HelloMaxBytes);
        var back = ShowBusHello.Parse(bytes)!;
        Assert.True(back.Caps.Contains("eye", StringComparer.Ordinal) && back.Caps.Contains("show", StringComparer.Ordinal), string.Join(",", back.Caps));
    }
}
