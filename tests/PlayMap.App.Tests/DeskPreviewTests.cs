using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.State;
using PlayMap.Core.Media;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>The desk's previews read the ring the node names for each view — from the VIEW … SHOW reply (<c>{"view","ring"}</c>) and
/// from STATE <c>machine.views[].ring</c> — and no other file: a new name (a new generation, a restart) closes and disposes the old
/// reader and opens the new ring; no name, a refusal or a named ring that is not there shows the stand-in with the words why.</summary>
public sealed class DeskPreviewTests
{
    private const string View = "op-op_1";

    /// <summary>With rings at the view's old fixed path and at a generation the node did not name, the view opens only the rings the
    /// node names, in turn: the reply's, then the one STATE names (the first reader disposed), then none (the stand-in), then a refusal
    /// (the node's words), then a ring that is not there (words naming it).</summary>
    [AvaloniaFact]
    public void AViewOpensOnlyTheRingTheNodeNamesAndFollowsTheName()
    {
        var node = $"t{Environment.ProcessId}v";
        var decoyFixed = PreviewRing.PathFor(node, View);
        var decoyGeneration = PreviewRing.PathFor(node, View, 1);
        var ringA = PreviewRing.PathFor(node, View, 7);
        var ringB = PreviewRing.PathFor(node, View, 8);
        var missing = PreviewRing.PathFor(node, View, 9);
        using var fixedRing = Ring(decoyFixed, 40, 30);
        using var generation = Ring(decoyGeneration, 50, 30);
        using var a = Ring(ringA, 64, 36);
        using var b = Ring(ringB, 32, 18);
        Gone(missing);

        var names = new PreviewNames();
        var surface = new PreviewSurface { NodeId = node, ViewId = View, Label = "Main" };
        var window = new Window { Content = surface, Width = 480, Height = 270 };
        PreviewSurface.SetNames(window, names);
        window.Show();
        DeskHarness.Pump();
        var source = Assert.IsType<RingPreviewSource>(surface.Source);

        // Nothing named yet: the stand-in, and no file opened though two rings lie where an unnamed reader would look.
        surface.PollFrame();
        Assert.False(surface.HasFrame);
        Assert.Null(surface.StandInWords);
        Assert.Empty(source.Opened);

        // The SHOW reply names ring A: exactly that ring is opened and its picture drawn.
        names.Shown(View, new WireReply(true, ShowReply(ringA)));
        surface.PollFrame();
        Assert.True(surface.HasFrame);
        Assert.Equal([ringA], source.Opened);
        var readerA = source.Reader!;
        Assert.Equal((64, 36), (readerA.MaxWidth, readerA.MaxHeight));

        // STATE names a new generation, ring B: the reader of A is disposed and B opened.
        names.Read(State((View, ringB)));
        surface.PollFrame();
        Assert.True(readerA.IsDisposed);
        Assert.True(surface.HasFrame);
        Assert.Equal([ringA, ringB], source.Opened);
        var readerB = source.Reader!;
        Assert.Equal((32, 18), (readerB.MaxWidth, readerB.MaxHeight));

        // STATE lists the view no more: B is let go and the stand-in is up.
        names.Read(State());
        surface.PollFrame();
        Assert.True(readerB.IsDisposed);
        Assert.Null(source.Reader);
        Assert.False(surface.HasFrame);
        Assert.Null(surface.StandInWords);

        // The node refuses the view: the stand-in carries its words.
        names.Shown(View, new WireReply(false, "this node draws no pictures (no OpenGL here) — the view is the desk's own"));
        surface.PollFrame();
        Assert.False(surface.HasFrame);
        Assert.Equal("This node draws no pictures (no OpenGL here) — the view is the desk's own", surface.StandInWords);

        // The node names a ring that is not there: the stand-in says so, and nothing else is opened in its place.
        names.Read(State((View, missing)));
        surface.PollFrame();
        Assert.False(surface.HasFrame);
        Assert.Null(source.Reader);
        Assert.Contains(Path.GetFileName(missing), surface.StandInWords, StringComparison.Ordinal);

        Assert.Equal([ringA, ringB], source.Opened);
        window.Close();
        Assert.Null(surface.Source);
    }

    /// <summary>Through the desk itself: the demo node names a ring in its SHOW reply and the Play view's preview opens it; the node
    /// makes the view's ring again (a new generation, STATE moving) and the preview reopens it, the old reader disposed; the node names
    /// none and the stand-in is up.</summary>
    [AvaloniaFact]
    public async Task TheDeskOpensTheRingTheNodesReplyAndStateName()
    {
        var node = $"t{Environment.ProcessId}d";
        var ringA = PreviewRing.PathFor(node, View, 1);
        var ringB = PreviewRing.PathFor(node, View, 2);
        using var a = Ring(ringA, 64, 36);
        using var b = Ring(ringB, 32, 18);
        await using var desk = new DeskHarness(l => l.SetRing(View, ringA));
        Assert.Equal(new PreviewRingName(ringA, null), desk.Session.Previews.Of(View));
        Assert.Equal(new ShownView(View, "output:op_1", ringA), desk.Session.State.Views.Single(v => v.Id == View));

        var window = MainWindowTests.Open(desk);
        var surface = window.GetVisualDescendants().OfType<PreviewSurface>().Single(s => s.ViewId == View);
        surface.PollFrame();
        var source = Assert.IsType<RingPreviewSource>(surface.Source);
        Assert.True(surface.HasFrame);
        Assert.Equal([ringA], source.Opened);
        var readerA = source.Reader!;

        desk.Link.SetRing(View, ringB);
        DeskHarness.Pump();
        surface.PollFrame();
        Assert.True(readerA.IsDisposed);
        Assert.True(surface.HasFrame);
        Assert.Equal([ringA, ringB], source.Opened);
        Assert.Equal((32, 18), (source.Reader!.MaxWidth, source.Reader.MaxHeight));

        var readerB = source.Reader;
        desk.Link.SetRing(View, null);
        DeskHarness.Pump();
        surface.PollFrame();
        Assert.True(readerB.IsDisposed);
        Assert.False(surface.HasFrame);
        Assert.Equal([ringA, ringB], source.Opened);
        window.Close();
    }

    /// <summary>The demo node draws no pictures: it lists each view it shows in STATE with no ring, its SHOW reply names none, and the
    /// desk's previews draw their stand-in, opening nothing.</summary>
    [AvaloniaFact]
    public async Task TheDemoNamesNoRingAndItsPreviewsDrawTheirStandIn()
    {
        await using var desk = new DeskHarness();
        Assert.Equal(new ShownView(View, "output:op_1", ""), desk.Session.State.Views.Single(v => v.Id == View));
        Assert.All(desk.Session.State.Views, v => Assert.Equal("", v.Ring));
        Assert.Equal(new PreviewRingName(null, null), desk.Session.Previews.Of(View));
        var reply = await desk.Link.SendAsync("VIEW pj-x SHOW projector:pj_left 1920x1080");
        using (var doc = JsonDocument.Parse(reply.Text))
            Assert.Equal(("pj-x", ""), (doc.RootElement.GetProperty("view").GetString(), doc.RootElement.GetProperty("ring").GetString()));

        var window = MainWindowTests.Open(desk);
        var surface = window.GetVisualDescendants().OfType<PreviewSurface>().Single(s => s.ViewId == View);
        surface.PollFrame();
        var source = Assert.IsType<RingPreviewSource>(surface.Source);
        Assert.False(surface.HasFrame);
        Assert.Null(surface.StandInWords);
        Assert.Empty(source.Opened);
        window.Close();
    }

    /// <summary>A ring with one picture of its own size, made fresh at the path.</summary>
    private static PreviewRingWriter Ring(string path, int width, int height)
    {
        Gone(path);
        var writer = new PreviewRingWriter(path, width, height);
        Assert.True(writer.Write(new byte[width * height * 4], width, height, width * 4, 1));
        return writer;
    }

    /// <summary>No file at the path (one an earlier run of this process id left).</summary>
    private static void Gone(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string ShowReply(string ring) => JsonSerializer.Serialize(new Dictionary<string, string> { ["view"] = View, ["ring"] = ring });

    /// <summary>A STATE whose machine.views lists these views (output views, each with its ring).</summary>
    private static StateSnapshot State(params (string Id, string Ring)[] views) =>
        StateSnapshot.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["machine"] = new Dictionary<string, object>
            {
                ["views"] = views.Select(v => new Dictionary<string, string> { ["id"] = v.Id, ["what"] = "output:op_1", ["ring"] = v.Ring }).ToArray(),
            },
        }));
}
