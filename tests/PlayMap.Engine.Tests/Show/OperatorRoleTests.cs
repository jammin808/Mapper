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
