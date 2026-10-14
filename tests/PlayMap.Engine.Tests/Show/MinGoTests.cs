namespace PlayMap.Engine.Tests.Show;

/// <summary>The GO fence on a manual show clock: armed, a second GO within MinGo of the last one on the same playlist is refused
/// with the double-GO words (rehearsal's MinGo is 0 unless set); EXPECT refuses a GO, NEXT or PLAY whose item is not the one the
/// caller's key showed, naming both.</summary>
public sealed class MinGoTests
{
    [Fact]
    public async Task TwoGosTwoHundredMillisecondsApartArmedAreOneGo()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW ARM");
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(200);
        Assert.Equal("double GO protection: 0.20 s since the last GO on Show (0.30 s armed)", await rig.Refused("PLAYLIST GO pl_show"));
        Assert.Equal("double GO protection: 0.20 s since the last GO on Show (0.30 s armed)", await rig.Refused("PLAYLIST NEXT pl_show"));
        Assert.Equal("it_a", rig.Engine.Playback.Lane("pl_show")!.ItemId);

        rig.Advance(100);
        await rig.Ok("PLAYLIST NEXT pl_show");
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        rig.Advance(50);
        Assert.StartsWith("double GO protection: 0.05 s", await rig.Refused("VIDEO RESTART ON pl_show"), StringComparison.Ordinal);

        // Rehearsal: no fence unless the show sets one.
        await rig.Ok("SHOW DISARM");
        await rig.Ok("PLAYLIST GO pl_show");
        await rig.Ok("PLAYLIST GO pl_show #1");
        await rig.Ok("SHOW SET MINGO 500 REHEARSAL");
        rig.Advance(100);
        Assert.Equal("double GO protection: 0.10 s since the last GO on Show (0.50 s in rehearsal)", await rig.Refused("PLAYLIST PREV pl_show"));
    }

    [Fact]
    public async Task ExpectRefusesAGoWhoseItemIsNotTheOneTheKeyShowed()
    {
        await using var rig = new ShowRig();
        await rig.Ok("PLAYLIST GO pl_show");
        await rig.Ok("PLAYLIST GO pl_show");
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);

        Assert.Equal("the next item on Show is now 'C', not 'B' — the key was out of date", await rig.Refused("PLAYLIST GO pl_show EXPECT it_b"));
        Assert.Equal("the next item on Show is now 'C', not 'A' — the key was out of date", await rig.Refused("PLAYLIST NEXT pl_show EXPECT #1"));
        Assert.Equal("the previous item on Show is now 'A', not 'C' — the key was out of date", await rig.Refused("PLAYLIST PREV pl_show EXPECT it_c"));
        Assert.Equal("the item on air on Show is now 'B', not 'A' — the key was out of date", await rig.Refused("VIDEO PLAY ON pl_show EXPECT it_a"));
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);

        await rig.Ok("VIDEO PAUSE ON pl_show");
        await rig.Ok("VIDEO PLAY ON pl_show EXPECT it_b");
        await rig.Ok("VIDEO PAUSE ON pl_show");
        await rig.Ok("PLAYLIST GO pl_show EXPECT it_c");
        Assert.Equal("it_c", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        Assert.Equal("the next item on Show is now nothing, not 'Nope' — the key was out of date", await rig.Refused("PLAYLIST NEXT pl_show EXPECT Nope"));
    }
}
