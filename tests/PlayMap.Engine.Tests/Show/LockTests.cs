using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Locks through the engine: a locked entity refuses every edit with the lock's words and nothing moves; an UNDO across a
/// locked entity is refused; a lock is not an undo step and no undo takes it back; LOCK AREA mapping blocks the mapping only;
/// an armed show adds mapping and layout; the network area holds the link verbs.</summary>
public sealed class LockTests
{
    private static Project WithWall() => ShowRig.OneScreen().WithStage(s => s with
    {
        Meshes = [new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty.With("width", 6.0).With("height", 3.0), 0, 0)],
        Surfaces = [new Surface("sf00000001", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#ffffff", [])],
    });

    [Fact]
    public async Task ALockedEntityRefusesEveryEditWithTheLocksWords()
    {
        await using var rig = new ShowRig(WithWall());
        var reply = JsonDocument.Parse(await rig.Ok("LOCK sf00000001 show night")).RootElement;
        Assert.Equal(("sf00000001", "entity"), (reply.GetProperty("target").GetString(), reply.GetProperty("kind").GetString()));
        Assert.Equal("sf00000001 is already locked", await rig.Refused("LOCK sf00000001"));
        var revision = rig.Engine.Store.Current.Revision;

        foreach (var edit in new[] { "SET sf00000001 opacity 0.5", "SURFACE SOURCE sf00000001 color:#FF0000", "REMOVE sf00000001" })
        {
            var words = await rig.Refused(edit);
            Assert.StartsWith("Wall (surface) is locked by Desk ('show night', ", words, StringComparison.Ordinal);
            Assert.EndsWith("— UNLOCK sf00000001 first", words, StringComparison.Ordinal);
        }
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(1.0, rig.Engine.Store.Project.FindSurface("sf00000001")!.Opacity);
        // Other entities and the show go on.
        await rig.Ok("SET op_1 name \"Left\"");
        await rig.Ok("PLAYLIST GO pl_show");

        var locks = JsonDocument.Parse(await rig.Ok("LOCKS")).RootElement;
        var l = Assert.Single(locks.EnumerateArray());
        Assert.Equal(("sf00000001", "Desk", "show night", false), (l.GetProperty("target").GetString(), l.GetProperty("by").GetString(),
            l.GetProperty("words").GetString(), l.GetProperty("implicit").GetBoolean()));

        await rig.Ok("UNLOCK sf00000001");
        Assert.Equal("nothing is locked", await rig.Refused("UNLOCK sf00000001"));
        await rig.Ok("SET sf00000001 opacity 0.5");
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf00000001")!.Opacity);
    }

    [Fact]
    public async Task AnUndoAcrossALockedEntityIsRefusedAndNoUndoTakesALockBack()
    {
        await using var rig = new ShowRig(WithWall());
        await rig.Ok("SET sf00000001 opacity 0.5");
        var label = rig.Engine.Store.UndoLabel;
        await rig.Ok("LOCK sf00000001");
        // The lock is no undo step: the history's top is the edit before it.
        Assert.Equal(label, rig.Engine.Store.UndoLabel);

        var undo = await rig.Refused("UNDO");
        Assert.StartsWith("Wall (surface) is locked by Desk (", undo, StringComparison.Ordinal);
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf00000001")!.Opacity);

        // An undo of something else keeps the lock: undo never unlocks.
        await rig.Ok("SET op_1 name \"Left\"");
        await rig.Ok("UNDO");
        Assert.Single(rig.Engine.Store.Project.Show.Locks);
        await rig.Ok("REDO");
        Assert.Single(rig.Engine.Store.Project.Show.Locks);

        await rig.Ok("UNLOCK ALL");
        await rig.Ok("UNDO");
        await rig.Ok("UNDO");
        Assert.Equal(1.0, rig.Engine.Store.Project.FindSurface("sf00000001")!.Opacity);
        Assert.Empty(rig.Engine.Store.Project.Show.Locks);
    }

    [Fact]
    public async Task LockAreaMappingBlocksTheMappingOnly()
    {
        await using var rig = new ShowRig(WithWall());
        await rig.Ok("LOCK AREA mapping calibrated");
        Assert.Equal("mapping is already locked", await rig.Refused("LOCK AREA MAPPING"));
        foreach (var edit in new[] { "SET sf00000001 visible false", "WARP NODE op_1 1 1 0.01 0", "ADD projector {\"name\":\"Right\"}" })
        {
            var words = await rig.Refused(edit);
            Assert.StartsWith("mapping is locked by Desk ('calibrated', ", words, StringComparison.Ordinal);
            Assert.EndsWith("— UNLOCK AREA mapping first", words, StringComparison.Ordinal);
        }
        // Content, playlists, the output's name and what the surface shows all go on.
        await rig.Ok("SURFACE SOURCE sf00000001 color:#FF0000");
        await rig.Ok("SET sf00000001 opacity 0.5");
        await rig.Ok("PLAYLIST NEW Encore");
        await rig.Ok("GENERATOR NEW julia Fractal");
        await rig.Ok("SET op_1 name \"Left\"");
        await rig.Ok("LOCK AREA playlists");
        Assert.StartsWith("playlists is locked by Desk", await rig.Refused("PLAYLIST NEW Second encore"), StringComparison.Ordinal);
        await rig.Ok("UNLOCK AREA mapping");
        await rig.Ok("SET sf00000001 visible false");
    }

    [Fact]
    public async Task AnArmedShowLocksTheMappingAndTheLayoutAndTheNetworkAreaHoldsTheLinks()
    {
        await using var rig = new ShowRig(WithWall());
        await rig.Ok("SET sf00000001 visible false");
        await rig.Ok("SHOW ARM");
        // What a surface shows is content, live for the desk while armed.
        await rig.Ok("SURFACE SOURCE sf00000001 color:#FF0000");
        var locks = JsonDocument.Parse(await rig.Ok("LOCKS")).RootElement.EnumerateArray()
            .Select(l => (l.GetProperty("target").GetString(), l.GetProperty("implicit").GetBoolean())).ToList();
        Assert.Equal([("mapping", true), ("layout", true)], locks);
        // The desk's own UNDO of a mapping edit meets the armed lock.
        await rig.Ok("UNDO");
        Assert.Equal("mapping is locked while the show is armed — SHOW DISARM first", await rig.Refused("UNDO"));
        await rig.Ok("SHOW DISARM");
        await rig.Ok("UNDO");
        Assert.True(rig.Engine.Store.Project.FindSurface("sf00000001")!.Visible);

        await rig.Ok("LOCK AREA network");
        Assert.StartsWith("network is locked by Desk", await rig.Refused("LINK PATTERNS OFF"), StringComparison.Ordinal);
        await rig.Ok("UNLOCK AREA network");
        Assert.Equal("the network is not running on this node", await rig.Refused("LINK PATTERNS OFF"));
    }
}
