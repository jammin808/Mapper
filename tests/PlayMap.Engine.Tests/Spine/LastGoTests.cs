using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>STATE.show.lastGo is the last accepted GO on each playlist the project holds, newest first: its leader time (the one the GO
/// fence counts from), its time on the node's wall clock, and the session, transport and name it came from. PLAYLIST GO, NEXT and PREV
/// and VIDEO PLAY and RESTART each write their playlist's row with the session that sent it; a GO the fence refuses, or the playback
/// refuses (a NEXT at the last item, a PLAY on a clip already playing), writes nothing; SHOW's reply carries the same rows.</summary>
public sealed class LastGoTests
{
    /// <summary>ShowRig's screen with a second playlist, pl_two, of one item.</summary>
    private static Project TwoPlaylists()
    {
        var p = ShowRig.OneScreen();
        var item = p.Playlists[0].Items[0] with { Id = "it_x", Label = "X" };
        return p with { Playlists = p.Playlists.Add(new Playlist("pl_two", "Two", [item], false, null)) };
    }

    private static JsonElement Row(JsonElement lastGo, string playlist) =>
        Assert.Single(lastGo.EnumerateArray(), r => r.GetProperty("playlist").GetString() == playlist);

    private static void Is(JsonElement row, long leaderNs, long at, string session, string transport, string by)
    {
        Assert.Equal(leaderNs, row.GetProperty("leaderNs").GetInt64());
        Assert.Equal(at, row.GetProperty("at").GetInt64());
        Assert.Equal(session, row.GetProperty("session").GetString());
        Assert.Equal(transport, row.GetProperty("transport").GetString());
        Assert.Equal(by, row.GetProperty("by").GetString());
    }

    [Fact]
    public async Task AnAcceptedGoPublishesItsPlaylistsLastGoWithItsSession()
    {
        await using var rig = new ShowRig(TwoPlaylists());
        Assert.Equal(0, rig.Show().GetProperty("lastGo").GetArrayLength());
        await rig.Ok("SHOW SET MINGO 500 REHEARSAL");

        // A GO from Companion: one row, at the GO's leader time and the node's wall time, with Companion's session.
        var first = rig.Now;
        var wall = rig.State().GetProperty("at").GetInt64();
        await rig.Ok("PLAYLIST GO pl_show", ShowRig.Companion);
        var state = rig.State();
        Assert.Equal(wall, state.GetProperty("at").GetInt64());
        var row = Assert.Single(state.GetProperty("show").GetProperty("lastGo").EnumerateArray());
        Assert.Equal("pl_show", row.GetProperty("playlist").GetString());
        Is(row, first, wall, "c1", "companion", "Companion: FOH deck");

        // The desk's NEXT inside the window is refused by the fence: the row is still Companion's.
        rig.Advance(200);
        Assert.StartsWith("double GO protection", await rig.Refused("PLAYLIST NEXT pl_show"), StringComparison.Ordinal);
        Is(Row(rig.Show().GetProperty("lastGo"), "pl_show"), first, wall, "c1", "companion", "Companion: FOH deck");

        // Once the window has closed the desk's NEXT is accepted, and the row is the desk's at the NEXT's instant.
        rig.Advance(300);
        await rig.Ok("PLAYLIST NEXT pl_show");
        Is(Row(rig.Show().GetProperty("lastGo"), "pl_show"), first + 500_000_000, wall + 500, "d1", "desk", "Desk");

        // A clip GO from a remote wire session writes the row too.
        rig.Advance(500);
        await rig.Ok("VIDEO RESTART ON pl_show", ShowRig.Remote);
        Is(Row(rig.Show().GetProperty("lastGo"), "pl_show"), first + 1_000_000_000, wall + 1_000, "w1", "wire", "Remote desk");

        // A GO on another playlist: newest first, each playlist its own row; SHOW's reply carries the same rows.
        rig.Advance(250);
        await rig.Ok("PLAYLIST GO pl_two");
        var lastGo = rig.Show().GetProperty("lastGo");
        Assert.Equal(["pl_two", "pl_show"], lastGo.EnumerateArray().Select(r => r.GetProperty("playlist").GetString()));
        Is(lastGo[0], first + 1_250_000_000, wall + 1_250, "d1", "desk", "Desk");
        Assert.Equal(lastGo.GetRawText(), JsonDocument.Parse(await rig.Ok("SHOW")).RootElement.GetProperty("lastGo").GetRawText());

        // PREV from MCP's session, once the window after the RESTART has closed: the row is MCP's.
        rig.Advance(250);
        await rig.Ok("PLAYLIST PREV pl_show", ShowRig.Mcp);
        Is(Row(rig.Show().GetProperty("lastGo"), "pl_show"), first + 1_500_000_000, wall + 1_500, "m1", "mcp", "MCP");

        // A PAUSE is no GO and leaves the row; the PLAY after it is Companion's GO.
        rig.Advance(500);
        await rig.Ok("VIDEO PAUSE ON pl_show", ShowRig.Remote);
        Is(Row(rig.Show().GetProperty("lastGo"), "pl_show"), first + 1_500_000_000, wall + 1_500, "m1", "mcp", "MCP");
        await rig.Ok("VIDEO PLAY ON pl_show", ShowRig.Companion);
        Is(Row(rig.Show().GetProperty("lastGo"), "pl_show"), first + 2_000_000_000, wall + 2_000, "c1", "companion", "Companion: FOH deck");

        // GOs the fence lets through and the playback refuses write nothing: a PLAY on a clip already playing, a NEXT at the last item.
        rig.Advance(500);
        var before = rig.Show().GetProperty("lastGo").GetRawText();
        Assert.Equal("already playing", await rig.Refused("VIDEO PLAY ON pl_show", ShowRig.Remote));
        Assert.Equal(before, rig.Show().GetProperty("lastGo").GetRawText());
        Assert.Equal("Two is at its last item", await rig.Refused("PLAYLIST NEXT pl_two", ShowRig.Remote));
        Assert.Equal(before, rig.Show().GetProperty("lastGo").GetRawText());

        // A playlist the project no longer holds has no row.
        await rig.Ok("VIDEO STOP ON pl_two");
        await rig.Ok("PLAYLIST DELETE pl_two");
        Assert.Equal("pl_show", Assert.Single(rig.Show().GetProperty("lastGo").EnumerateArray()).GetProperty("playlist").GetString());
    }
}
