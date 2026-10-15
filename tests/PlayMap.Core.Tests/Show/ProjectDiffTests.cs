using PlayMap.Core.Model;
using PlayMap.Core.Show;

namespace PlayMap.Core.Tests.Show;

/// <summary>The draft against its base, entity by entity: added, changed (with each field as a SET path and its values), removed;
/// a field set back to what it was is no change; the patch lands on a live root that moved elsewhere without undoing it; conflicts
/// are the ids changed on both sides; the show's locks are never a draft's.</summary>
public sealed class ProjectDiffTests
{
    private static Project Base() => Project.CreateDefault("Diff");

    [Fact]
    public void ChangesAreNamedEntityByEntityWithTheirFieldsAsSetPaths()
    {
        var b = Base();
        var surface = new Surface("sf_wall", "Wall", "ms_x", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#3EC1F3", []);
        var draft = b with
        {
            Canvases = b.Canvases.Update("cv_main", c => c with { Background = "#FF0000", Width = 1280 }),
            Generators = [],
            Stage = b.Stage with { Surfaces = [surface] },
            Settings = b.Settings with { FrameRate = "50" },
        };
        var patch = ProjectDiff.Between(b, draft, 7);
        Assert.Equal(7, patch.BaseRevision);
        Assert.Equal(["settings", "gn_testcard", "cv_main", "sf_wall"], patch.Changes.Select(c => c.Id));
        Assert.Equal([ChangeOp.Changed, ChangeOp.Removed, ChangeOp.Changed, ChangeOp.Added], patch.Changes.Select(c => c.Op));
        var canvas = patch.Changes[2];
        Assert.Equal(["width", "background"], canvas.Fields.Select(f => f.Field));
        Assert.Equal(new FieldChange("background", "\"#000000\"", "\"#FF0000\""), canvas.Fields[1]);
        Assert.Equal(["Main canvas (canvas): width 1920 → 1280", "Main canvas (canvas): background \"#000000\" → \"#FF0000\""], ProjectDiff.Words(canvas));
        Assert.Equal(["Test card (generator) removed"], ProjectDiff.Words(patch.Changes[1]));
        Assert.Equal(["Wall (surface) added"], ProjectDiff.Words(patch.Changes[3]));
        Assert.Equal("show settings: frameRate \"60\" → \"50\"", ProjectDiff.Words(patch.Changes[0])[0]);
    }

    [Fact]
    public void AFieldSetBackIsNoChangeAndListsAreDescendedByIndex()
    {
        var b = Base();
        var same = b with { Canvases = b.Canvases.Update("cv_main", c => c with { Background = new string("#000000".ToCharArray()) }) };
        Assert.Empty(ProjectDiff.Between(b, same, 1).Changes);
        var layer = b.Canvases[0].Layers[0];
        var draft = b with { Canvases = b.Canvases.Update("cv_main", c => c with { Layers = [layer with { Opacity = 0.5 }] }) };
        var change = Assert.Single(ProjectDiff.Between(b, draft, 1).Changes);
        Assert.Equal("layers.0.opacity", Assert.Single(change.Fields).Field);
        Assert.Equal("1", change.Fields[0].From);
        Assert.Equal("0.5", change.Fields[0].To);
    }

    [Fact]
    public void ThePatchLandsOnAMovedLiveRootAndNamesConflicts()
    {
        var b = Base();
        var draft = b with { Canvases = b.Canvases.Update("cv_main", c => c with { Background = "#FF0000" }), Generators = [] };
        var patch = ProjectDiff.Between(b, draft, 1);
        // Live moved elsewhere (an output renamed, a playlist added): landing keeps it.
        var live = b with
        {
            Outputs = b.Outputs.Update("op_1", o => o with { Name = "Left" }),
            Playlists = [new Playlist("pl_new", "New", [], false, null)],
        };
        var landed = patch.ApplyTo(live);
        Assert.Equal("#FF0000", landed.Canvases[0].Background);
        Assert.Empty(landed.Generators);
        Assert.Equal("Left", landed.Outputs[0].Name);
        Assert.Equal("pl_new", Assert.Single(landed.Playlists).Id);
        Assert.Empty(patch.ConflictsWith(b, live));
        Assert.Null(patch.Touches(b, live));

        // Live changed the canvas too: a conflict, and an edit that touches what the patch lands.
        var clash = live with { Canvases = live.Canvases.Update("cv_main", c => c with { Name = "Stage" }) };
        Assert.Equal(["cv_main"], patch.ConflictsWith(b, clash));
        Assert.Equal("cv_main", patch.Touches(live, clash));
    }

    [Fact]
    public void TheShowsLocksAreNeverADraftsAndItsRulesAre()
    {
        var b = Base();
        var locked = b with { Show = b.Show with { Locks = [new ShowLock("mapping", LockKind.Area, "Desk", 0, "")] } };
        Assert.Empty(ProjectDiff.Between(b, locked, 1).Changes);
        var rules = b with { Show = b.Show with { Rules = b.Show.Rules with { MinGoMsArmed = 500 } } };
        var patch = ProjectDiff.Between(b, rules, 1);
        Assert.Equal("show", Assert.Single(patch.Changes).Id);
        var live = locked;
        var landed = patch.ApplyTo(live);
        Assert.Equal(500, landed.Show.Rules.MinGoMsArmed);
        Assert.Single(landed.Show.Locks);
    }

    [Fact]
    public void ManyChangedFieldsAreListedUpToALimitAndCounted()
    {
        var b = Base();
        var item = PlaylistItem.For("it_1", SourceRef.Solid("#000000"), "One");
        var before = b with { Playlists = [new Playlist("pl_1", "List", [.. Enumerable.Range(0, 20).Select(i => item with { Id = "it_" + i, Label = "L" + i })], false, null)] };
        var after = before with { Playlists = before.Playlists.Update("pl_1", l => l with { Items = [.. l.Items.Select(i => i with { Label = i.Label + "!" })] }) };
        var change = Assert.Single(ProjectDiff.Between(before, after, 1).Changes);
        Assert.Equal(ProjectDiff.FieldsShown, change.Fields.Count);
        Assert.Equal(20 - ProjectDiff.FieldsShown, change.More);
        Assert.Equal($"List (playlist): and {20 - ProjectDiff.FieldsShown} more", ProjectDiff.Words(change)[^1]);
    }
}
