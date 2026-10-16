using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Fleet;

/// <summary>The hot backup's pure rules: the epoch a takeover commits, when a node fences itself, when a follower takes its clock from a
/// new leader, what a backup or a fenced node refuses, the pull's proof, and which air lines the mirror carries.</summary>
public sealed class BackupRulesTests
{
    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line;
    }

    private static string? Refused(string text, string transport, string role)
    {
        var line = Line(text);
        var spec = VerbCatalog.Match(line)!.Value.Spec;
        return BackupGate.Refused(spec, line, transport, role, "mirrors");
    }

    [Fact]
    public void ATakeoverCommitsOnePastEveryEpochHeard()
    {
        Assert.Equal(1, Takeover.NextEpoch(0, []));
        Assert.Equal(8, Takeover.NextEpoch(7, [3, 5]));
        Assert.Equal(10, Takeover.NextEpoch(2, [9, 4]));
        Assert.Equal(1, Takeover.NextEpoch(-3, [0]));
    }

    [Theory]
    [InlineData("primary", 7, "primary", "0badc0de", 8, true)]    // the backup took this node's show under a higher epoch
    [InlineData("solo", 0, "primary", "0badc0de", 1, false)]      // a solo node holds no show a backup mirrored
    [InlineData("primary", 8, "primary", "0badc0de", 8, false)]   // the same epoch is not higher
    [InlineData("primary", 7, "primary", "c0ffee00", 9, false)]   // it took another node's show
    [InlineData("primary", 7, "backup", "0badc0de", 9, false)]    // a backup holds no show
    [InlineData("fenced", 7, "primary", "0badc0de", 9, false)]    // fenced already
    [InlineData("backup", 7, "primary", "0badc0de", 9, false)]    // a backup is fenced by nothing
    public void ANodeIsFencedOnlyByThePrimaryThatTookItsShowUnderAHigherEpoch(string role, long epoch, string peerRole, string peerOf, long peerEpoch, bool fenced)
    {
        Assert.Equal(fenced, Takeover.FencedBy("0badc0de", role, epoch, peerRole, peerOf, peerEpoch, peerKnown: true));
        // A machine that never joined this node's mirror (or is not heard over its pinned certificate) fences nothing, whatever it says.
        Assert.False(Takeover.FencedBy("0badc0de", role, epoch, peerRole, peerOf, peerEpoch, peerKnown: false));
    }

    [Fact]
    public void AFollowerRetargetsOnlyToTheGroupsNewLeaderUnderAHigherEpoch()
    {
        Assert.True(Takeover.Retargets("show", "0badc0de", 0, "9f8e7d6c", "leader", "show", 1));
        Assert.False(Takeover.Retargets("show", "0badc0de", 1, "9f8e7d6c", "leader", "show", 1));   // not higher
        Assert.False(Takeover.Retargets("show", "0badc0de", 0, "9f8e7d6c", "leader", "other", 2));  // another group
        Assert.False(Takeover.Retargets("show", "0badc0de", 0, "9f8e7d6c", "follower", "show", 2)); // it does not lead
        Assert.False(Takeover.Retargets("show", "0badc0de", 0, "0badc0de", "leader", "show", 2));   // the leader it follows already
        Assert.False(Takeover.Retargets("", "0badc0de", 0, "9f8e7d6c", "leader", "", 2));           // no group
    }

    [Fact]
    public void ABackupRefusesAClientsEditButKeepsQueriesPanicArmingAndTheFleetsOwnVerbs()
    {
        foreach (var role in new[] { FleetRoles.Backup, FleetRoles.Fenced })
        {
            Assert.Equal("mirrors", Refused("PLAYLIST GO pl1", "desk", role));
            Assert.Equal("mirrors", Refused("SET op_1 name Left", "wire", role));
            Assert.Equal("mirrors", Refused("BLACKOUT ON", "companion", role));
            Assert.Equal("mirrors", Refused("SYNC LEAD show", "desk", role));
            Assert.Equal("mirrors", Refused("FLEET ROUTE op_1 NONE", "desk", role));
            Assert.Null(Refused("FLEET ROUTE TEST", "desk", role));
            Assert.Null(Refused("STATE", "wire", role));
            Assert.Null(Refused("PANIC", "desk", role));
            Assert.Null(Refused("SHOW ARM", "desk", role));
            Assert.Null(Refused("MEDIA FETCH md1 VERSION mv_1", "desk", role));   // the fix line its preflight names
            Assert.Null(Refused("FAILOVER", "desk", role));
            Assert.Null(Refused("FLEET SOLO", "desk", role));
            Assert.Null(Refused("FLEET BACKUP 0badc0de", "desk", role));
            // The mirror, the group's feed and the node itself are never refused here.
            Assert.Null(Refused("PLAYLIST GO pl1", "fleet", role));
            Assert.Null(Refused("BLACKOUT ON", "sync", role));
            Assert.Null(Refused("SET op_1 name Left", "system", role));
        }
        Assert.Null(Refused("PLAYLIST GO pl1", "desk", FleetRoles.Primary));
        Assert.Null(Refused("SET op_1 name Left", "desk", FleetRoles.Solo));
    }

    [Fact]
    public void APullsProofIsOfTheTokenTheRequestAndAFreshNonce()
    {
        var nonce = PullProof.NewNonce(1_760_000_000_000);
        Assert.True(PullProof.TryTime(nonce, out var at));
        Assert.Equal(1_760_000_000_000, at);
        var proof = PullProof.Sign("K7QM-3XWD-P9RA", nonce, "GET", "https", "/mirror/project");
        Assert.Equal(64, proof.Length);
        Assert.DoesNotContain("K7QM", proof, StringComparison.OrdinalIgnoreCase);
        Assert.True(PullProof.Verify("K7QM3XWDP9RA", nonce, "get", "HTTPS", "/mirror/project", proof));
        Assert.False(PullProof.Verify("K7QM-3XWD-P9RA", nonce, "GET", "http", "/mirror/project", proof));            // relayed to another scheme
        Assert.False(PullProof.Verify("K7QM-3XWD-P9RB", nonce, "GET", "https", "/mirror/project", proof));          // another token
        Assert.False(PullProof.Verify("K7QM-3XWD-P9RA", nonce, "GET", "https", "/mirror/media/ab", proof));        // another request
        Assert.False(PullProof.Verify("K7QM-3XWD-P9RA", PullProof.NewNonce(1), "GET", "https", "/mirror/project", proof)); // another nonce
        Assert.False(PullProof.TryTime("12-xyz", out _));
        Assert.False(PullProof.TryTime(null, out _));
        Assert.False(PullProof.TryTime("abc-" + new string('0', 32), out _));
    }

    [Theory]
    [InlineData("PANIC FADE 1000", "panic")]
    [InlineData("PANIC CLEAR", "panic")]
    [InlineData("BLACKOUT ON", "blackout")]
    [InlineData("MASTER 0.5", "master")]
    [InlineData("FREEZE ON", "freeze")]
    [InlineData("PLAYLIST GO pl1", null)]
    [InlineData("SET op_1 name Left", null)]
    public void TheMirrorsAirIsTheAirVerbs(string line, string? kind) => Assert.Equal(kind, MirrorAir.KindOf(Line(line)));

    [Fact]
    public void ARouteShowsThePrimaryOnItsInputAndEveryOtherNodeOnTheBackups()
    {
        var route = new FleetRoute("op_1", "dv00000001", "31", "32", "0badc0de");
        Assert.Equal("31", route.InputFor("0badc0de"));
        Assert.Equal("32", route.InputFor("9f8e7d6c"));
        Assert.Equal("primary", route.Side("31"));
        Assert.Equal("backup", route.Side("32"));
        Assert.Equal("", route.Side("11"));
        var p = Project.CreateDefault("Routes") with { Fleet = FleetPlan.Default with { Routes = [route] } };
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Assert.Equal(route, Assert.Single(back.Fleet.Routes));
        // An older file reads no routes.
        Assert.Empty(ProjectJson.Deserialize(ProjectJson.Serialize(Project.CreateDefault("Old"))).Fleet.Routes);
    }
}
