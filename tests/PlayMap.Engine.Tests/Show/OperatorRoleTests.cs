using PlayMap.Core.Model;
using PlayMap.Core.Bus;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Show;

/// <summary>The node's operators list is not a claim: a session it lists is an operator only when it proved the node's token on its
/// own connection (a session paired because its address is trusted proved nothing) and the entry's address rule holds; armed, that
/// is the difference between changing a show rule and being refused.</summary>
public sealed class OperatorRoleTests
{
    private static ShowRig Rig(string token, params string[] trusted) => new(services: s => s with
    {
        Settings = s.Settings with
        {
            Token = token,
            TrustedAddresses = [.. trusted],
            Show = new ShowSafetySettings { Operators = ["Companion: FOH deck@10.0.0.7", "10.0.0.8"] },
        },
    });

    private static SessionInfo Deck(string address, string name = "Companion: FOH deck") => new("c-" + address, "companion", address, false, name, true);

    [Fact]
    public async Task AListedSessionThatProvedTheTokenIsTheOperator()
    {
        await using var rig = Rig("K7QM-3XWD-P9RA");
        await rig.Ok("SHOW ARM");
        Assert.Equal(ShowRole.Operator, rig.Engine.ShowRoleOf(Deck("10.0.0.7")));
        Assert.Equal(ShowRole.Operator, rig.Engine.ShowRoleOf(Deck("10.0.0.8", "anything")));
        await rig.Ok("SHOW SET MINGO 500", Deck("10.0.0.7"));

        // The same name from another address, or another name at the listed address, is a remote.
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(Deck("10.0.0.9")));
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(Deck("10.0.0.7", "Companion: Spare")));
        Assert.StartsWith("Not while armed: SHOW SET", await rig.Refused("SHOW SET MINGO 400", Deck("10.0.0.9")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustOfAnAddressIsNotProof()
    {
        await using var rig = Rig("K7QM-3XWD-P9RA", "10.0.0.7");
        await rig.Ok("SHOW ARM");
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(Deck("10.0.0.7")));
        Assert.StartsWith("Not while armed: SHOW SET", await rig.Refused("SHOW SET MINGO 500", Deck("10.0.0.7")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionFromATrustedAddressOrALinkThisNodeOpenedProvedNothing()
    {
        // The default node: a token, and this machine trusted. A same-machine client paired by trust cannot be told from one that
        // proved the token, so a bare name never makes it an operator (the desk's own HELLO does).
        await using var rig = new ShowRig(services: s => s with
        {
            Settings = s.Settings with
            {
                Token = "K7QM-3XWD-P9RA",
                TrustLoopback = true,
                Show = new ShowSafetySettings { Operators = ["Companion: FOH deck", "10.0.0.5"] },
            },
        });
        var local = new SessionInfo("l", "companion", "127.0.0.1", true, "Companion: FOH deck", true);
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(local));
        // A Patterns link this node opened to a listed machine is not a client that proved anything.
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(new SessionInfo("p", "patterns", "10.0.0.5", false, "Patterns", true)));
        Assert.Equal(ShowRole.Operator, rig.Engine.ShowRoleOf(new SessionInfo("w", "wire", "10.0.0.5", false, "Any", true)));
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(new SessionInfo("x", "wire", "not-an-address", false, "Any", true)));
    }

    [Fact]
    public async Task ANodeThatAsksNoTokenListsOnlyThisMachine()
    {
        await using var rig = Rig("");
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(Deck("10.0.0.7")));
        var local = new SessionInfo("l", "companion", "127.0.0.1", true, "Companion: FOH deck", true);
        await using var local8 = new ShowRig(services: s => s with
        {
            Settings = s.Settings with { Token = "", Show = new ShowSafetySettings { Operators = ["Companion: FOH deck"] } },
        });
        Assert.Equal(ShowRole.Operator, local8.Engine.ShowRoleOf(local));
        Assert.Equal(ShowRole.Remote, local8.Engine.ShowRoleOf(local with { IsLoopback = false, RemoteAddress = "10.0.0.7" }));
    }
}

/// <summary>A fleet mirror (a hot backup copying its primary) edits past the armed show's implicit mapping lock through
/// MirrorEditAsync, as one undo step; the show's explicit locks and a waiting blind commit still hold it; a client's line never
/// gets the same way past.</summary>
public sealed class MirrorEditTests
{
    [Fact]
    public async Task AMirrorEditPassesTheArmedShowsImplicitLockButNotItsOwnLocks()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW ARM");
        Assert.StartsWith("Not while armed", await rig.Refused("SET op_1 rotation 90"), StringComparison.Ordinal);
        var live = rig.Engine.Store.Apply("a fleet line without the mirror's way", p => p with { Outputs = p.Outputs.Update("op_1", o => o with { Rotation = 90 }) });
        Assert.False(live.Changed);
        Assert.StartsWith("layout is locked while the show is armed", live.Effect, StringComparison.Ordinal);

        var mirrored = await rig.Engine.MirrorEditAsync("mirror: op_1 rotation", p => p with { Outputs = p.Outputs.Update("op_1", o => o with { Rotation = 90 }) });
        Assert.True(mirrored.Changed, mirrored.Effect);
        Assert.Equal(90, rig.Engine.Store.Project.Outputs[0].Rotation);
        Assert.Equal("mirror: op_1 rotation", rig.Engine.Store.UndoLabel);

        // The show's own lock holds a mirror as it holds everyone.
        await rig.Ok("LOCK op_1 \"focus\"");
        var locked = await rig.Engine.MirrorEditAsync("mirror: op_1 rotation", p => p with { Outputs = p.Outputs.Update("op_1", o => o with { Rotation = 180 }) });
        Assert.False(locked.Changed);
        Assert.StartsWith("Output 1 (output) is locked by Desk", locked.Effect, StringComparison.Ordinal);
        await rig.Ok("UNLOCK op_1");

        // A blind commit waiting to land the same output refuses the mirror's change to it.
        await rig.Ok("BLIND ON");
        await rig.Ok("SET op_1 rotation 270");
        await rig.Ok("BLIND COMMIT AT +500");
        var landing = await rig.Engine.MirrorEditAsync("mirror: op_1 rotation", p => p with { Outputs = p.Outputs.Update("op_1", o => o with { Rotation = 0 }) });
        Assert.False(landing.Changed);
        Assert.StartsWith("a blind commit lands Output 1 (output)", landing.Effect, StringComparison.Ordinal);

        // After the mirror's edit, the next client edit meets the armed lock again.
        Assert.StartsWith("Not while armed", await rig.Refused("SET op_1 rotation 0", ShowRig.Remote), StringComparison.Ordinal);
    }
}
