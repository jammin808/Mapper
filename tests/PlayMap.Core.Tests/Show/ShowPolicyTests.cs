using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Show;

/// <summary>Show Mode's table: every class × role × armed/rehearsal cell decides as docs/SHOW.md says; every catalogue row has a
/// decision for every role; a refusal names the rule and the way out; the role comes from the transport, never from words; a
/// generic edit is classed by the id it names; an allowed line allocates nothing.</summary>
public sealed class ShowPolicyTests
{
    private static readonly ShowRole[] Roles = [ShowRole.Operator, ShowRole.Remote, ShowRole.Assistant, ShowRole.Approved, ShowRole.Peer, ShowRole.System];

    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line!;
    }

    private static ShowDecision Decide(string text, ShowRole role, bool armed, bool viewOnly = false, string disarmFrom = "paired", bool dark = false)
    {
        var line = Line(text);
        var spec = VerbCatalog.Match(line)!.Value.Spec;
        return ShowPolicy.Decide(spec, line, role, new ShowFacts(armed, false, dark, viewOnly, disarmFrom));
    }

    private static ShowDecision Decide(string text, ShowRole role, ShowFacts facts)
    {
        var line = Line(text);
        return ShowPolicy.Decide(VerbCatalog.Match(line)!.Value.Spec, line, role, in facts);
    }

    /// <summary>One line per class and the armed verdict for Operator, Remote, Assistant, Approved, Peer and System
    /// (A allow, R refuse, P propose).</summary>
    public static TheoryData<ShowClass, string, string> ArmedTable => new()
    {
        { ShowClass.Query, "STATE", "AAAAAA" },
        { ShowClass.Transport, "PLAYLIST GO pl_show", "AAPAAA" },
        { ShowClass.Air, "BLACKOUT ON", "AAPAAA" },
        { ShowClass.Panic, "PANIC", "AAPAAA" },
        { ShowClass.Panic, "PANIC CLEAR", "AAPAAA" },
        { ShowClass.Show, "SHOW ARM", "AAPRAA" },
        { ShowClass.Perform, "TEMPO TAP", "AAPAAA" },
        { ShowClass.Content, "SURFACE SOURCE sf00000001 color:#000000", "ARPRAA" },
        { ShowClass.Playlists, "PLAYLIST REMOVE pl_show it_a", "RRPRAA" },
        { ShowClass.Mapping, "WARP NODE op_1 1 1 0.01 0", "RRPRAA" },
        { ShowClass.Layout, "OUTPUT ENABLE op_1 OFF", "RRPRAA" },
        { ShowClass.Media, "MEDIA REMOVE md00000001", "RRPRAA" },
        { ShowClass.Heavy, "SCAN START cm00000001", "RRRRRA" },
        { ShowClass.Setup, "PATTERN grid", "RRRRRA" },
        { ShowClass.DeviceSafe, "DEVICE INPUT dv00000001 31", "ARPRAA" },
        { ShowClass.DevicePower, "DEVICE POWER dv00000001 OFF", "RRPRRA" },
        { ShowClass.Project, "PROJECT OPEN show.playmap", "RRPRRA" },
        { ShowClass.Network, "LINK PATTERNS OFF", "RRPRAA" },
        { ShowClass.Fleet, "FAILBACK", "RRPRAA" },
        { ShowClass.Fleet, "FAILOVER", "AAPRAA" },
        { ShowClass.Admin, "CREDENTIAL SET dv00000001 secret", "ARRRRA" },
        { ShowClass.Assistant, "APPROVE ap1", "AAPRAA" },
    };

    [Theory]
    [MemberData(nameof(ArmedTable))]
    public void EveryClassAndRoleDecidesAsTheTableSaysArmedAndAllowsInRehearsal(ShowClass cls, string text, string armed)
    {
        var line = Line(text);
        Assert.Equal(cls, ShowPolicy.ClassOf(VerbCatalog.Match(line)!.Value.Spec, line));
        for (var i = 0; i < Roles.Length; i++)
        {
            var d = Decide(text, Roles[i], armed: true);
            var want = armed[i] switch { 'A' => ShowVerdict.Allow, 'P' => ShowVerdict.Propose, _ => ShowVerdict.Refuse };
            Assert.True(want == d.Verdict, $"{text} from {Roles[i]} armed: {d.Verdict} ({d.Words})");
            Assert.Equal(d.Verdict == ShowVerdict.Allow, d.Words is null);
            // Rehearsal: everything runs as it does today (the assistant's risk and the locks still apply elsewhere).
            Assert.Equal(ShowVerdict.Allow, Decide(text, Roles[i], armed: false).Verdict);
        }
    }

    [Fact]
    public void EveryCatalogueRowHasADecisionForEveryRoleAndARefusalNamesTheRuleAndTheWayOut()
    {
        foreach (var spec in VerbCatalog.All)
        {
            var line = new WireLine(null, spec.Name.Split(' '), spec.Name);
            foreach (var role in Roles.Append(ShowRole.Observer))
            {
                foreach (var armed in new[] { false, true })
                {
                    var d = ShowPolicy.Decide(spec, line, role, new ShowFacts(armed, false, false, false, "paired"));
                    if (d.Verdict == ShowVerdict.Allow)
                        continue;
                    Assert.False(string.IsNullOrWhiteSpace(d.Words), $"{spec.Name} from {role}");
                    if (role is ShowRole.Observer or ShowRole.Assistant)
                        continue;
                    Assert.StartsWith("Not while armed: " + spec.Name, d.Words, StringComparison.Ordinal);
                    Assert.True(d.Words!.Contains("DISARM", StringComparison.Ordinal) || d.Words.Contains("CONFIRM", StringComparison.Ordinal)
                        || d.Words.Contains("desk", StringComparison.Ordinal), $"{spec.Name} from {role}: no way out in '{d.Words}'");
                }
            }
        }
    }

    [Fact]
    public void TheOperatorsExceptionsHoldArmed()
    {
        Assert.Equal(ShowVerdict.Allow, Decide("PLAYLIST ADD pl_show color:#FF0000", ShowRole.Operator, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("MEDIA IMPORT /shows/intro.mov", ShowRole.Operator, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("MEDIA FETCH ALL", ShowRole.Operator, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("DEVICE POWER dv00000001 ON", ShowRole.Operator, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("DEVICE POWER dv00000001 OFF CONFIRM", ShowRole.Operator, true).Verdict);
        var off = Decide("DEVICE POWER dv00000001 OFF", ShowRole.Operator, true);
        Assert.Equal(ShowVerdict.Refuse, off.Verdict);
        Assert.Contains("CONFIRM", off.Words, StringComparison.Ordinal);
        foreach (var ok in new[] { "PROJECT SAVE", "UNDO", "REDO", "SHOW DISARM", "UNLOCK ALL", "SHOW SET MINGO 500", "FAILBACK CONFIRM", "BLACKOUT OFF" })
            Assert.Equal(ShowVerdict.Allow, Decide(ok, ShowRole.Operator, true).Verdict);
        var warp = Decide("WARP NODE op_1 1 1 0.01 0", ShowRole.Operator, true);
        Assert.Equal("Not while armed: WARP NODE moves the mapping on the air. SHOW DISARM on the desk first.", warp.Words);
    }

    [Fact]
    public void ARemoteRunsTheShowOnlyWhileArmedAndTheRulesNarrowIt()
    {
        foreach (var ok in new[] { "SHOW ARM", "LOCK op_1", "PREFLIGHT", "MEDIA FETCH ALL", "DEVICE SHUTTER dv00000001 CLOSE", "FAILOVER", "MASTER 0" })
            Assert.Equal(ShowVerdict.Allow, Decide(ok, ShowRole.Remote, true).Verdict);
        foreach (var no in new[] { "DEVICE SHUTTER dv00000001 OPEN", "SHOW SET MINGO 500", "PANIC SET op_1 BLACK", "SET op_1 name \"Left\"" })
            Assert.Equal(ShowVerdict.Refuse, Decide(no, ShowRole.Remote, true).Verdict);

        // DISARM-FROM: paired lets a remote disarm; desk keeps it to the desk.
        Assert.Equal(ShowVerdict.Allow, Decide("SHOW DISARM", ShowRole.Remote, true, disarmFrom: "paired").Verdict);
        var desk = Decide("SHOW DISARM", ShowRole.Remote, true, disarmFrom: "desk");
        Assert.Equal(ShowVerdict.Refuse, desk.Verdict);
        Assert.Contains("desk", desk.Words, StringComparison.Ordinal);
        Assert.Equal(ShowVerdict.Allow, Decide("SHOW DISARM", ShowRole.Operator, true, disarmFrom: "desk").Verdict);

        // REMOTE-VIEW-ONLY: armed, a remote only reads — but PANIC is never refused for a person.
        var go = Decide("PLAYLIST GO pl_show", ShowRole.Remote, true, viewOnly: true);
        Assert.Equal(ShowVerdict.Refuse, go.Verdict);
        Assert.Contains("REMOTE-VIEW-ONLY OFF", go.Words, StringComparison.Ordinal);
        Assert.Equal(ShowVerdict.Allow, Decide("PANIC", ShowRole.Remote, true, viewOnly: true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("STATE", ShowRole.Remote, true, viewOnly: true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("PLAYLIST GO pl_show", ShowRole.Remote, false, viewOnly: true).Verdict);
    }

    [Fact]
    public void AnApprovedLineGetsWhatARemoteGetsAndTheAirOnlyTowardsDark()
    {
        Assert.Equal(ShowVerdict.Allow, Decide("BLACKOUT ON", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("BLACKOUT TOGGLE", ShowRole.Approved, true, dark: false).Verdict);
        Assert.Equal(ShowVerdict.Refuse, Decide("BLACKOUT TOGGLE", ShowRole.Approved, true, dark: true).Verdict);
        Assert.Equal(ShowVerdict.Refuse, Decide("BLACKOUT OFF", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("MASTER 0", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Refuse, Decide("MASTER 100", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("FREEZE ON", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Refuse, Decide("FREEZE OFF", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("PLAYLIST NEXT pl_show", ShowRole.Approved, true).Verdict);
        Assert.Equal(ShowVerdict.Refuse, Decide("DEVICE SHUTTER dv00000001 CLOSE", ShowRole.Approved, true).Verdict);

        // Never more than a remote: while remote clients only read, an approved line only reads too — PANIC aside.
        var next = Decide("PLAYLIST NEXT pl_show", ShowRole.Approved, true, viewOnly: true);
        Assert.Equal(ShowVerdict.Refuse, next.Verdict);
        Assert.Contains("REMOTE-VIEW-ONLY OFF", next.Words, StringComparison.Ordinal);
        Assert.Equal(ShowVerdict.Refuse, Decide("BLACKOUT ON", ShowRole.Approved, true, viewOnly: true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("PANIC", ShowRole.Approved, true, viewOnly: true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("PLAYLIST NEXT pl_show", ShowRole.Approved, false, viewOnly: true).Verdict);
    }

    [Fact]
    public void AnObserverOnlyReadsAndStillStopsTheShow()
    {
        foreach (var armed in new[] { false, true })
        {
            Assert.Equal(ShowVerdict.Allow, Decide("STATE", ShowRole.Observer, armed).Verdict);
            Assert.Equal(ShowVerdict.Allow, Decide("PANIC", ShowRole.Observer, armed).Verdict);
            Assert.Equal(ShowVerdict.Refuse, Decide("PLAYLIST GO pl_show", ShowRole.Observer, armed).Verdict);
        }
        // An assistant's PANIC waits for a person, armed; in rehearsal the assistant's risk rule decides (outside the table).
        Assert.Equal(ShowVerdict.Propose, Decide("PANIC", ShowRole.Assistant, true).Verdict);
    }

    [Fact]
    public void TheRoleComesFromTheTransportAndTheOperatorsList()
    {
        string[] none = [];
        Assert.Equal(ShowRole.Operator, ShowPolicy.RoleOf(SessionInfo.Desk("d"), none));
        Assert.Equal(ShowRole.Observer, ShowPolicy.RoleOf(new SessionInfo("w", "wire", "10.0.0.9", false, "FOH", false), none));
        Assert.Equal(ShowRole.Assistant, ShowPolicy.RoleOf(new SessionInfo("m", "mcp", "127.0.0.1", true, "MCP", true), none));
        Assert.Equal(ShowRole.Assistant, ShowPolicy.RoleOf(new SessionInfo("a", "assistant", "127.0.0.1", true, "Desk", true), none));
        Assert.Equal(ShowRole.Approved, ShowPolicy.RoleOf(new SessionInfo("p", "approval", "127.0.0.1", true, "Desk", true), none));
        Assert.Equal(ShowRole.Peer, ShowPolicy.RoleOf(new SessionInfo("f", "fleet", "10.0.0.2", false, "FOH-B", true), none));
        Assert.Equal(ShowRole.System, ShowPolicy.RoleOf(new SessionInfo("s", "system", "127.0.0.1", true, "PlayMap", true), none));
        var companion = new SessionInfo("c", "companion", "10.0.0.7", false, "Companion: FOH deck", true);
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(companion, none));
        // The operators list grants the role only to a session that proved the node's token on its connection, by an address rule.
        Assert.Equal(ShowRole.Operator, ShowPolicy.RoleOf(companion, ["companion: foh deck@10.0.0.7"], proved: true));
        Assert.Equal(ShowRole.Operator, ShowPolicy.RoleOf(companion, ["10.0.0.7"], proved: true));
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(companion, ["10.0.0.7"]));
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(companion, ["companion: foh deck"], proved: true));
        // A client calling itself "Desk" over the wire is still a remote: names never make a role, only the operators list.
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(new SessionInfo("x", "wire", "10.0.0.8", false, "Desk", true), none));
    }

    [Fact]
    public void AnOperatorsEntryIsAnAddressRuleAndANameAloneIsThisMachinesOnly()
    {
        var deck = new SessionInfo("c", "companion", "10.0.0.7", false, "Companion: FOH deck", true);
        var claim = deck with { RemoteAddress = "10.0.0.99" };
        var local = deck with { RemoteAddress = "127.0.0.1", IsLoopback = true };
        // The same HELLO name from another address is not the operator; neither is an unproved session at the listed address.
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(claim, ["Companion: FOH deck@10.0.0.7"], proved: true));
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(claim, ["10.0.0.7"], proved: true));
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(deck, ["Companion: FOH deck@10.0.0.7"], proved: false));
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(deck with { ClientName = "Companion: Other" }, ["Companion: FOH deck@10.0.0.7"], proved: true));
        // A bare name names only this machine; an IPv4 address matches its IPv6-mapped form.
        Assert.Equal(ShowRole.Operator, ShowPolicy.RoleOf(local, ["Companion: FOH deck"], proved: true));
        Assert.Equal(ShowRole.Remote, ShowPolicy.RoleOf(deck, ["Companion: FOH deck"], proved: true));
        Assert.Equal(ShowRole.Operator, ShowPolicy.RoleOf(deck with { RemoteAddress = "::ffff:10.0.0.7" }, ["10.0.0.7"], proved: true));
        // The transport's own roles never come from the list.
        Assert.Equal(ShowRole.Assistant, ShowPolicy.RoleOf(new SessionInfo("m", "mcp", "10.0.0.7", false, "MCP", true), ["10.0.0.7"], proved: true));
    }

    [Fact]
    public void TheBlindOwnersDraftableLinesGoToItsDraftArmedOrNot()
    {
        foreach (var armed in new[] { false, true })
        {
            var facts = new ShowFacts(armed, true, false, false, "paired");
            foreach (var line in new[] { "SET op_1 rotation 90", "WARP NODE op_1 1 1 0.01 0", "SET cv_main background \"#FF0000\"", "PLAYLIST NEW Encore", "UNDO", "REDO" })
                Assert.Equal(ShowVerdict.BlindOnly, Decide(line, ShowRole.Operator, facts).Verdict);
            // The air, the transport, Show Mode itself and the stop are never drafted.
            foreach (var line in new[] { "PLAYLIST GO pl_show", "BLACKOUT ON", "SHOW SET MINGO 500", "PANIC", "STATE" })
                Assert.NotEqual(ShowVerdict.BlindOnly, Decide(line, ShowRole.Operator, facts).Verdict);
            // An assistant that holds the draft drafts; a peer never does.
            Assert.Equal(ShowVerdict.BlindOnly, Decide("SET op_1 rotation 90", ShowRole.Assistant, facts).Verdict);
            Assert.NotEqual(ShowVerdict.BlindOnly, Decide("SET op_1 rotation 90", ShowRole.Peer, facts).Verdict);
        }
        // Armed, an assistant may open and read a draft; landing it waits for a person.
        Assert.Equal(ShowVerdict.Allow, Decide("BLIND ON", ShowRole.Assistant, true).Verdict);
        Assert.Equal(ShowVerdict.Allow, Decide("BLIND DIFF", ShowRole.Assistant, true).Verdict);
        Assert.Equal(ShowVerdict.Propose, Decide("BLIND COMMIT", ShowRole.Assistant, true).Verdict);
        Assert.Equal(ShowVerdict.Refuse, Decide("BLIND ON", ShowRole.Remote, true).Verdict);
    }

    [Theory]
    [InlineData("SET op_1 name \"Left\"", ShowClass.Layout)]
    [InlineData("SET sf00000001 opacity 0.5", ShowClass.Mapping)]
    [InlineData("SET pj00000001 lumens 9000", ShowClass.Mapping)]
    [InlineData("SET cv_main background \"#000000\"", ShowClass.Content)]
    [InlineData("REMOVE it00000001", ShowClass.Playlists)]
    [InlineData("REMOVE md00000001", ShowClass.Media)]
    [InlineData("SET dv00000001 name \"Box\"", ShowClass.DeviceSafe)]
    [InlineData("SET pr00000001 name \"Show\"", ShowClass.Project)]
    [InlineData("SET zz_unknown name \"x\"", ShowClass.Mapping)]
    [InlineData("ADD media {\"location\":\"a.mov\"}", ShowClass.Media)]
    [InlineData("ADD playlist {\"name\":\"Show\"}", ShowClass.Playlists)]
    [InlineData("ADD surface {\"name\":\"Wall\"}", ShowClass.Mapping)]
    [InlineData("ADD output {\"name\":\"Left\"}", ShowClass.Layout)]
    [InlineData("ADD gizmo {}", ShowClass.Mapping)]
    public void AGenericEditIsClassedByTheIdOrKindItNames(string text, ShowClass cls)
    {
        var line = Line(text);
        Assert.Equal(cls, ShowPolicy.ClassOf(VerbCatalog.Match(line)!.Value.Spec, line));
    }

    [Fact]
    public void AnAllowedLineAllocatesNothing()
    {
        var line = Line("PLAYLIST GO pl_show");
        var set = Line("SET op_1 name \"Left\"");
        var spec = VerbCatalog.Match(line)!.Value.Spec;
        var setSpec = VerbCatalog.Match(set)!.Value.Spec;
        var facts = new ShowFacts(true, false, false, false, "paired");
        ShowPolicy.Decide(spec, line, ShowRole.Operator, in facts);
        ShowPolicy.Decide(setSpec, set, ShowRole.Peer, in facts);
        var allowed = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            if (ShowPolicy.Decide(spec, line, ShowRole.Operator, in facts).Verdict == ShowVerdict.Allow)
                allowed++;
            if (ShowPolicy.Decide(setSpec, set, ShowRole.Peer, in facts).Verdict == ShowVerdict.Allow)
                allowed++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2000, allowed);
        Assert.Equal(0, allocated);
    }
}
