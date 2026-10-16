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
        "ALIGN PLAN", "ALIGN SCORE", "DEVICE NATIVE", // wp2-align: the dry run, the score's own verb and the projectors' own processing are wp2-resolve's
        "SCAN SUGGEST", "SCAN SOLVE", "SCAN VIEWS", "SCAN DROP", // wp2-phonescan: many views into one scan are wp2-multiscan's
        "GENERATOR MORPH", // wp2-content: morphs are next round
        "LED DISCOVER", // wp2-led: discovery is next round
        "MEDIA ESTIMATE", "MEDIA VERSION", // wp2-optimise: estimates and removing a version (with the lossless rewriter) are next round
        "SITE GUIDE", "PLAN LIGHT", "STACK NEW", "STACK MATCH", "STACK FAILOVER", "STACK LIST", // wp2-facade: the guide, the light plan and stacks are wp2-stacks'
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
            Assert.True(v.Round is 1 or 2, v.Name);
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
        var second = VerbCatalog.All.Skip(first.Count).ToList();

        Assert.Equal(FirstRoundRows, first.Count);
        Assert.Equal(RoundTwoRows - TrimmedRows.Length, second.Count);
        Assert.All(TrimmedRows, name => Assert.Null(VerbCatalog.Find(name)));
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
