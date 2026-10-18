using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>The one table every reader shares: each row's name, syntax, forms, risk, show class and flags are well made, every
/// form of every row makes a line the assistant's gate passes, and no word lowers the risk of a line that moves the air.</summary>
public sealed class CatalogueTests
{
    /// <summary>The first round's rows, as main held them before round 2.</summary>
    private const int FirstRoundRows = 104;

    /// <summary>The rows SPINE §4.3 adds.</summary>
    private const int RoundTwoRows = 209;

    /// <summary>Rows a package removed from its own block because it does not ship them this round (PLAN §2.2).</summary>
    private static readonly string[] TrimmedRows =
    [
        "FX PLUGINS", "FX TRUST", // wp2-fx: FFGL is next round
        "RECORD", "SELECT", "COMPANION PRESS", "COMPANION VAR", "COMPANION BRIGHTNESS", // wp2-companion: the action feed, selection and the node → Companion link are next round
        "OVERLAY GRID", // wp2-wall: the alignment grid is next round
        "ALIGN PLAN", // wp2-resolve: the dry run is next round
        "GENERATOR MORPH", // wp2-content: morphs are next round
        "LED DISCOVER", // wp2-led: discovery is next round
        "MEDIA ESTIMATE", "MEDIA VERSION", // wp2-optimise: estimates and removing a version (with the lossless rewriter) are next round
        "DECODE PROBE", // wp2-zerocopy: the direct-sampling proof harness is next round
        "FLEET SUCCESSION", // wp2-backup: automatic takeover and succession are next round (FLEET PROMOTE is the press)
    ];

    /// <summary>Rows a package added inside its own block for what it ships (PLAN §2.2), beyond SPINE §4.3's.</summary>
    private static readonly string[] AddedRows =
    [
        "SYNC RECEIPT", // wp2-sync: a member's answer to its leader's air line
    ];

    /// <summary>The connection's own verbs: the gate refuses them by design.</summary>
    private static readonly HashSet<string> ConnectionVerbs = new(StringComparer.Ordinal) { "HELLO", "NONCE", "AUTH", "AUTH-PROOF", "SUBSCRIBE" };

    [Fact]
    public void EveryRowIsWellMade()
    {
        foreach (var v in VerbCatalog.All)
        {
            Assert.InRange(v.WordCount, 1, 2);
            Assert.Equal(v.Name, v.Name.Trim().ToUpperInvariant());
            Assert.False(string.IsNullOrWhiteSpace(v.Words), v.Name);
            Assert.DoesNotContain('\n', v.Words);
            Assert.True(Enum.IsDefined(v.Risk), v.Name);
            Assert.True(Enum.IsDefined(v.Category), v.Name);
            Assert.True(Enum.IsDefined(v.Show), v.Name);
            Assert.True(v.Round is 1 or 2 or 3, v.Name);
            Assert.True(Balanced(v.Syntax), $"{v.Name}: unbalanced brackets in {v.Syntax}");
            if (v.Mutates)
                Assert.NotEqual(ShowClass.Query, v.Show);
            else
                Assert.True(v.Show is ShowClass.Query or ShowClass.Admin, $"{v.Name} is a query of class {v.Show}");
            foreach (var form in VerbSyntax.Alternatives(v))
            {
                Assert.True(form == v.Name || form.StartsWith(v.Name + " ", StringComparison.Ordinal), $"{v.Name}: {form}");
                // Every required token is a word HELP prints: a placeholder or a choice of literal words, never an optional part.
                foreach (var token in VerbSyntax.RequiredTokens(v, form))
                {
                    Assert.False(token.StartsWith('['), $"{v.Name}: {token}");
                    Assert.False(string.IsNullOrWhiteSpace(token), v.Name);
                    if (!VerbSyntax.IsPlaceholder(token))
                        Assert.All(token.Split('|'), c => Assert.Matches("^[A-Za-z0-9+.,#*_-]+$", c));
                }
            }
            if (v.Internal)
                Assert.True(v.Mutates && v.Show == ShowClass.Fleet, v.Name);
            if (v.Ephemeral)
                Assert.False(v.Gesture, v.Name);
        }
    }

    [Fact]
    public void NamesAndAliasesAreUniqueAndEachReadsAsItsRow()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in VerbCatalog.All)
        {
            Assert.True(seen.Add(v.Name), $"{v.Name} twice");
            Assert.Same(v, VerbCatalog.Find(v.Name));
            foreach (var a in v.Aliases)
            {
                Assert.True(seen.Add(a), $"alias {a} of {v.Name} is taken");
                Assert.InRange(a.Count(c => c == ' ') + 1, 1, 2);
                Assert.Same(v, VerbCatalog.Find(a));
            }
        }
        Assert.DoesNotContain("TC", seen);
        Assert.True(WireGrammar.TryParse("TC ENGAGE pl1 ON", out var tc, out _));
        Assert.Equal("TIMECODE ENGAGE", VerbCatalog.Match(tc)!.Value.Spec.Name);
    }

    [Fact]
    public void RoundTwosRowsFollowTheFirstRoundsAndCarryNoSecret()
    {
        var first = VerbCatalog.All.TakeWhile(v => v.Round == 1).ToList();
        var second = VerbCatalog.All.Skip(first.Count).TakeWhile(v => v.Round == 2).ToList();

        Assert.Equal(FirstRoundRows, first.Count);
        Assert.Equal(RoundTwoRows - TrimmedRows.Length + AddedRows.Length, second.Count);
        Assert.All(TrimmedRows, name => Assert.Null(VerbCatalog.Find(name)));
        Assert.All(AddedRows, name => Assert.Equal(2, VerbCatalog.Find(name)?.Round));
        Assert.All(second, v => Assert.Equal(2, v.Round));
        Assert.All(second, v => Assert.Null(v.SecretFrom));
        Assert.All(second, v => Assert.Empty(v.HighFor));
        Assert.True(Enum.GetValues<VerbCategory>().Skip((int)VerbCategory.Assistant + 1).All(c => second.Any(v => v.Category == c)));
    }

    [Fact]
    public void EveryFormOfEveryRowMakesALineTheGatePasses()
    {
        var checkedForms = 0;
        foreach (var v in VerbCatalog.All.Where(v => !ConnectionVerbs.Contains(v.Name)))
        {
            foreach (var form in VerbSyntax.Alternatives(v))
            {
                var line = string.Join(' ', new[] { v.Name }.Concat(VerbSyntax.RequiredTokens(v, form).Select(Sample)));
                var check = CommandGate.Check(line);
                Assert.True(check.Ok, $"{form} → '{line}': {check.Reason}");
                Assert.Same(v, check.Spec);
                checkedForms++;
            }
        }
        Assert.True(checkedForms > VerbCatalog.All.Count, $"{checkedForms} forms");
    }

    [Fact]
    public void NoWordLowersALineThatMovesTheAirAndEveryNewAirOrPanicRowIsHigh()
    {
        foreach (var v in VerbCatalog.All)
        {
            if (ShowClasses.RiskOnlyRises(v.Show))
                Assert.All(v.RiskWhen, w => Assert.True(w.Risk >= v.Risk, $"{v.Name}: {w.Word} would lower {v.Risk} to {w.Risk}"));
            if (v.Show is ShowClass.Air or ShowClass.Panic)
                Assert.Equal(VerbRisk.High, v.Risk);
            foreach (var w in v.RiskWhen)
                Assert.InRange(w.Arg, -1, 16);
        }
        Assert.Contains(VerbCatalog.All, v => v.Show == ShowClass.Panic);
        Assert.Equal(ShowClass.Air, VerbCatalog.Find("BLACKOUT")!.Show);
        Assert.Equal(ShowClass.DevicePower, VerbCatalog.Find("DEVICE POWER")!.Show);
        Assert.Equal(ShowClass.Target, VerbCatalog.Find("SET")!.Show);
        Assert.Equal(ShowClass.Admin, VerbCatalog.Find("CREDENTIAL SET")!.Show);
        Assert.Equal(ShowClass.Transport, VerbCatalog.Find("PLAYLIST GO")!.Show);
        Assert.Equal(ShowClass.Setup, VerbCatalog.Find("DEVICE LENS")!.Show);
    }

    [Fact]
    public void TheContextsCommandsLeaveOutNodeToNodeVerbsAndPutPanicFirst()
    {
        var commands = ShowContext.CommandsFromCatalog();
        var internalSyntax = VerbCatalog.All.Where(v => v.Internal).Select(v => v.Syntax).ToList();

        Assert.NotEmpty(internalSyntax);
        Assert.DoesNotContain(commands, c => internalSyntax.Contains(c.Verb));
        Assert.Equal(VerbCatalog.All.Count(v => v.Mutates && !v.Internal), commands.Count);
        Assert.StartsWith("PANIC ", commands[0].Verb, StringComparison.Ordinal);
        var firstOther = commands.ToList().FindIndex(c => VerbCatalog.All.First(v => v.Syntax == c.Verb).Show is not (ShowClass.Panic or ShowClass.Air or ShowClass.Transport or ShowClass.Show));
        Assert.All(commands.Skip(firstOther), c => Assert.False(VerbCatalog.All.First(v => v.Syntax == c.Verb).Show is ShowClass.Panic or ShowClass.Air or ShowClass.Transport or ShowClass.Show));
    }

    /// <summary>The Eye keeps a node's first 64 commands: every command that moves a panic, the air, a transport or Show Mode is among
    /// them, and the set-up lines of the present path and the decode policy come after them all.</summary>
    [Fact]
    public void EveryCommandThatMovesTheShowIsInTheEyesFirst64AndTheGpuSetUpLinesFollow()
    {
        static bool Moves(ContextCommand c) =>
            VerbCatalog.All.First(v => v.Syntax == c.Verb).Show is ShowClass.Panic or ShowClass.Air or ShowClass.Transport or ShowClass.Show;
        var commands = ShowContext.CommandsFromCatalog();
        var moving = commands.Count(Moves);
        Assert.True(moving <= 64, $"{moving} commands move the show; the Eye keeps 64");
        Assert.All(commands.Take(moving), c => Assert.True(Moves(c), c.Verb));
        foreach (var name in new[] { "PRESENT PATH", "PRESENT RESET", "DECODE POLICY" })
        {
            var at = commands.ToList().FindIndex(c => c.Verb == VerbCatalog.Find(name)!.Syntax);
            Assert.True(at >= moving, $"{name} at {at}, among the {moving} that move the show");
        }
    }

    /// <summary>PLAN §3.1's round-3 rows in catalogue order, each with its risk, show class, whether it changes something and whether it
    /// is a gesture. GUIDE CHECK's forms are four rows (GUIDE BEGIN, END, CHECK, UNCHECK): a line names its row by its first one or two
    /// words, so a guide's action word follows GUIDE.</summary>
    private static readonly (string Name, VerbRisk Risk, ShowClass Show, bool Mutates, bool Gesture)[] RoundThreeRows =
    [
        ("MACHINE", VerbRisk.Low, ShowClass.Query, false, false),
        ("MACHINE SET", VerbRisk.Review, ShowClass.Setup, true, false),
        ("MACHINE LOCK", VerbRisk.Review, ShowClass.Setup, true, false),
        ("GPU", VerbRisk.Low, ShowClass.Query, false, false),
        ("GPU WARM", VerbRisk.Low, ShowClass.Perform, true, false),
        ("GPU REHEARSE", VerbRisk.High, ShowClass.Heavy, true, false),
        ("PRESENT PROVE", VerbRisk.Low, ShowClass.Perform, true, false),
        ("WATERMARK", VerbRisk.Review, ShowClass.Layout, true, false),
        ("OBSERVE", VerbRisk.Review, ShowClass.Setup, true, false),
        ("OBSERVE STATUS", VerbRisk.Low, ShowClass.Query, false, false),
        ("LATENCY STEP", VerbRisk.High, ShowClass.Heavy, true, false),
        ("FX RELEASE", VerbRisk.High, ShowClass.Setup, true, false),
        ("UNDO LIST", VerbRisk.Low, ShowClass.Query, false, false),
        ("UNDO TO", VerbRisk.Review, ShowClass.Project, true, false),
        ("PIN MOVE", VerbRisk.Review, ShowClass.Target, true, true),
        ("WARP CORNER", VerbRisk.Review, ShowClass.Layout, true, true),
        ("CANVAS SPAN", VerbRisk.Review, ShowClass.Layout, true, false),
        ("CANVAS SOURCE", VerbRisk.High, ShowClass.Content, true, false),
        ("HAND", VerbRisk.Low, ShowClass.Query, false, false),
        ("HAND CLEAR", VerbRisk.Review, ShowClass.Layout, true, false),
        ("GUIDE", VerbRisk.Low, ShowClass.Query, false, false),
        ("GUIDE BEGIN", VerbRisk.Review, ShowClass.Project, true, false),
        ("GUIDE END", VerbRisk.Review, ShowClass.Project, true, false),
        ("GUIDE CHECK", VerbRisk.Review, ShowClass.Project, true, false),
        ("GUIDE UNCHECK", VerbRisk.Review, ShowClass.Project, true, false),
        ("VIEWPOINT FIT", VerbRisk.Review, ShowClass.Target, true, false),
        ("SURFACE MAP", VerbRisk.Review, ShowClass.Content, true, false),
        ("OUTPUT PASSTHROUGH", VerbRisk.Review, ShowClass.Setup, true, false),
    ];

    /// <summary>A line of each form a round-3 package adds to an earlier row, with that row (whose round stays its own).</summary>
    private static readonly (string Line, string Row, int Round)[] FormsRoundThreeAdds =
    [
        ("VIEW v_stage SHOW editor 1280x720 SHARED 4242", "VIEW", 1),
        ("PRESENT PATH D3D12", "PRESENT PATH", 2),
        ("PATTERN grid ON cv_main", "PATTERN", 1),
        ("WARP NODE op_1 2 1 TO 640 360 PX", "WARP NODE", 1),
        ("WARP NODE op_1 2 1 BY 1 0 PX", "WARP NODE", 1),
        ("HANDLE SELECT pn_1", "HANDLE SELECT", 2),
        ("HANDLE SELECT op_1 WARP TL", "HANDLE SELECT", 2),
        ("HANDLE SELECT op_1 WARP 2,1", "HANDLE SELECT", 2),
        ("HANDLE SELECT op_1 MASK 0 3", "HANDLE SELECT", 2),
        ("INPUT DISCOVER CAPTURE WAIT 500", "INPUT DISCOVER", 2),
        ("INPUT ADD capture:dshow/Cam Front MODE 1920x1080@50 FORMAT V210", "INPUT ADD", 2),
    ];

    [Fact]
    public void RoundThreesRowsFollowRoundTwosWithThePlansRiskAndClassAndCarryNoSecret()
    {
        var third = VerbCatalog.All.SkipWhile(v => v.Round == 1).SkipWhile(v => v.Round == 2).ToList();

        Assert.Equal(RoundThreeRows.Select(r => r.Name), third.Select(v => v.Name));
        Assert.Equal(third.Count, VerbCatalog.All.Count(v => v.Round == 3));
        foreach (var (v, row) in third.Zip(RoundThreeRows))
        {
            Assert.Equal(3, v.Round);
            Assert.Equal((row.Name, row.Risk, row.Show, row.Mutates, row.Gesture), (v.Name, v.Risk, v.Show, v.Mutates, v.Gesture));
            Assert.Null(v.SecretFrom);
            Assert.Empty(v.HighFor);
            Assert.False(v.Internal || v.PairedOnly || v.EmbedsLine, v.Name);
            Assert.Empty(v.Aliases);
            Assert.False(v.Show is ShowClass.Air or ShowClass.Panic or ShowClass.Transport or ShowClass.Show, $"{v.Name} would move the air");
        }
        // A machine's GPU scheduling class set to REALTIME is high; its other settings are a review.
        Assert.Equal(VerbRisk.High, VerbCatalog.RiskOf(Parse("MACHINE SET GPU-PRIORITY REALTIME")));
        Assert.Equal(VerbRisk.Review, VerbCatalog.RiskOf(Parse("MACHINE SET GPU-PRIORITY HIGH")));
        Assert.Equal(VerbRisk.Review, VerbCatalog.RiskOf(Parse("MACHINE SET TIMER HIGH")));
    }

    [Fact]
    public void EveryFormOfARoundThreeRowAndEveryFormItAddsToAnEarlierRowMakesALineTheGatePassesAsItsRow()
    {
        foreach (var v in VerbCatalog.All.Where(v => v.Round == 3))
        {
            foreach (var form in VerbSyntax.Alternatives(v))
            {
                var line = string.Join(' ', new[] { v.Name }.Concat(VerbSyntax.RequiredTokens(v, form).Select(Sample)));
                var check = CommandGate.Check(line);
                Assert.True(check.Ok, $"{form} → '{line}': {check.Reason}");
                Assert.Same(v, check.Spec);
                Assert.Equal(VerbCatalog.RiskOf(Parse(line)), check.Risk);
            }
        }
        foreach (var (text, row, round) in FormsRoundThreeAdds)
        {
            var check = CommandGate.Check(text);
            Assert.True(check.Ok, $"'{text}': {check.Reason}");
            Assert.Equal(row, check.Spec!.Name);
            Assert.Equal(round, check.Spec.Round);
            Assert.Equal(check.Spec.Risk, check.Risk);
        }
    }

    [Fact]
    public void NoWordLowersARoundThreeLineOnAHeavyAirOrLayoutRow()
    {
        var rows = VerbCatalog.All.Where(v => v.Round == 3 || FormsRoundThreeAdds.Any(f => f.Row == v.Name)).ToList();
        Assert.Equal(RoundThreeRows.Length + FormsRoundThreeAdds.Select(f => f.Row).Distinct().Count(), rows.Count);
        foreach (var v in rows.Where(v => v.Show is ShowClass.Heavy or ShowClass.Air or ShowClass.Layout))
        {
            Assert.All(v.RiskWhen, w => Assert.True(w.Risk >= v.Risk, $"{v.Name}: {w.Word} would lower {v.Risk} to {w.Risk}"));
            foreach (var form in VerbSyntax.Alternatives(v))
            {
                var line = string.Join(' ', new[] { v.Name }.Concat(VerbSyntax.RequiredTokens(v, form).Select(Sample)));
                Assert.True(VerbCatalog.RiskOf(Parse(line)) >= v.Risk, $"'{line}' is below {v.Risk}");
            }
        }
        Assert.Contains(rows, v => v.Show == ShowClass.Heavy);
        Assert.Contains(rows, v => v.Show == ShowClass.Layout);
    }

    private static WireLine Parse(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line;
    }

    /// <summary>A word that satisfies a required token: the first of a literal choice, any word for a placeholder.</summary>
    private static string Sample(string token) => VerbSyntax.IsPlaceholder(token) ? "x1" : token.Split('|')[0];

    private static bool Balanced(string syntax)
    {
        int angle = 0, square = 0;
        foreach (var c in syntax)
        {
            angle += c == '<' ? 1 : c == '>' ? -1 : 0;
            square += c == '[' ? 1 : c == ']' ? -1 : 0;
            if (angle < 0 || square < 0)
                return false;
        }
        return angle == 0 && square == 0;
    }
}
