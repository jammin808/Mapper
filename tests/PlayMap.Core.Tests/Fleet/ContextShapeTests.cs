using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;

namespace PlayMap.Core.Tests.Fleet;

/// <summary>The context's order for Patterns' Eye: show and machine first, outputs beyond six folded (the green ones), devices folded
/// into their projectors, idle playlists as one item, faults last, and every item kept.</summary>
public sealed class ContextShapeTests
{
    /// <summary>Thirty items as a busy node gives them, in the order its packages add them: twelve outputs (one red, one amber), two
    /// projectors each with its device (one amber), three playlists (one playing), the links, the air blacked out, a fault.</summary>
    private static (List<ContextItem> Items, List<ContextLink> Links) Thirty()
    {
        var items = new List<ContextItem>
        {
            new("show", "show", "Show", Light.Grey, "rehearsal"),
            new("air", "air", "Air", Light.Amber, "blacked out"),
            new("playlist:pl1", "playlist", "Main", Light.Green, "playing Intro, 2:10 left"),
            new("playlist:pl2", "playlist", "Walk-in", Light.Grey, "idle"),
            new("playlist:pl3", "playlist", "Outro", Light.Grey, "idle"),
        };
        for (var i = 1; i <= 12; i++)
            items.Add(new("output:op_" + i, "output", "Out " + i, i == 5 ? Light.Red : i == 9 ? Light.Amber : Light.Green, "60 fps"));
        for (var i = 1; i <= 2; i++)
            items.Add(new("projector:pj" + i, "projector", "P" + i, Light.Grey, "not calibrated"));
        items.Add(new("media", "media", "Media", Light.Green, "4 files ready"));
        items.Add(new("link:patterns", "link", "Patterns", Light.Green, "live"));
        items.Add(new("link:stagewatch", "link", "Stagewatch", Light.Green, "live"));
        items.Add(new("sync:show", "sync", "Sync", Light.Green, "leads show"));
        items.Add(new("fault:web", "fault", "web", Light.Red, "the web port stays closed"));
        for (var i = 1; i <= 2; i++)
            items.Add(new("device:dv" + i, "device", "Box " + i, i == 2 ? Light.Amber : Light.Green, "on · shutter open"));
        items.Add(new("decode", "decode", "Decode", Light.Green, "GPU"));
        items.Add(new("fx", "fx", "Effects", Light.Green, "none"));
        items.Add(new("preflight", "preflight", "Preflight", Light.Green, "41 green"));
        items.Add(new("machine", "machine", "FOH-A", Light.Green, "solo · build 0.2.0 · llvmpipe"));
        var links = new List<ContextLink> { new("link:patterns", "show", "controls", Light.Green) };
        for (var i = 1; i <= 2; i++)
        {
            links.Add(new("device:dv" + i, "projector:pj" + i, "controls", Light.Green));
            links.Add(new("output:op_" + i, "projector:pj" + i, "drives", Light.Green));
        }
        for (var i = 1; i <= 12; i++)
            links.Add(new("show", "output:op_" + i, "controls", Light.Green));
        return (items, links);
    }

    private static IReadOnlyList<FoldedProjector> Folds() =>
        [.. Enumerable.Range(1, 2).Select(i => new FoldedProjector("pj" + i, "P" + i, i == 2 ? Light.Amber : Light.Green, $"not calibrated · Box {i}: on", "op_" + i))];

    [Fact]
    public void ThirtyItemsFitTheEyesWindowWithShowAndMachineFirst()
    {
        var (items, links) = Thirty();
        var before = items.Select(i => i.Id).ToHashSet();
        Assert.Equal(30, before.Count);

        ContextShape.Shape(items, links, Folds());

        Assert.Equal("show", items[0].Id);
        Assert.Equal("machine", items[1].Id);
        Assert.Equal("outputs", items[2].Id);
        Assert.Equal("10 of 12 outputs green", items[2].State);
        Assert.Equal(
            ["show", "machine", "outputs", "output:op_5", "output:op_9", "projector:pj1", "projector:pj2", "playlist:pl1", "playlists", "sync:show",
             "media", "preflight", "link:patterns", "link:stagewatch", "air", "decode", "fx", "fault:web"],
            items.Take(18).Select(i => i.Id));
        // Every item stays; the aggregates are added.
        Assert.True(before.IsSubsetOf(items.Select(i => i.Id)));
        Assert.Contains(items, i => i.Id == "playlists" && i.State == "2 idle: Walk-in, Outro");

        var window = ContextShape.EyeWindow(items).Select(i => i.Id).ToList();
        Assert.Equal(ContextShape.Window, window.Count);
        Assert.Contains("show", window);
        Assert.Contains("machine", window);
        Assert.Contains("output:op_5", window);       // red
        Assert.Contains("output:op_9", window);       // amber
        Assert.Contains("playlist:pl1", window);      // playing
        Assert.Contains("projector:pj2", window);     // amber by its device, folded in
        Assert.DoesNotContain("output:op_1", window); // green, in the aggregate
        Assert.DoesNotContain("device:dv1", window);  // folded into P1
        Assert.All(links, l => Assert.True(ContextLinkKinds.IsEyeVerb(l.Kind), l.Kind));
        Assert.Contains(links, l => l is { From: "show", To: "outputs", Kind: "controls" });
    }

    [Fact]
    public void ProjectorsCarryTheirDevicesWordsAndTheWorseLight()
    {
        var (items, links) = Thirty();
        ContextShape.Shape(items, links, Folds());
        var p2 = items.Single(i => i.Id == "projector:pj2");
        Assert.Equal(Light.Amber, p2.Light);
        Assert.Equal("not calibrated · Box 2: on", p2.State);
        // The devices folded into their projectors sit after the faults, still in the context.
        var fault = items.FindIndex(i => i.Id == "fault:web");
        Assert.True(items.FindIndex(i => i.Id == "device:dv2") > fault);
    }

    [Fact]
    public void SixOutputsOrFewerStayOneByOne()
    {
        var items = new List<ContextItem> { new("machine", "machine", "A", Light.Green, ""), new("show", "show", "Show", Light.Grey, "") };
        for (var i = 1; i <= 6; i++)
            items.Add(new("output:op_" + i, "output", "Out " + i, Light.Green, "60 fps"));
        var links = new List<ContextLink>();
        ContextShape.Shape(items, links, []);
        Assert.Equal(["show", "machine", "output:op_1", "output:op_2", "output:op_3", "output:op_4", "output:op_5", "output:op_6"], items.Select(i => i.Id));
        Assert.Empty(links);
    }
}
