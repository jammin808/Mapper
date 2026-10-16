using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>Typed parameters for the whole catalogue: every row and every alternative has its list; the list agrees with the syntax
/// HELP prints (each token up to the first optional part is a required parameter — a keyword and its value, a literal choice with
/// the same words, or a placeholder); no override names a row the catalogue lacks; only the design's named rows (GO, NEXT, PREV,
/// PLAY, PAUSE, BLACKOUT, FREEZE, PANIC) fire at once and every other high-risk row is held; a keyword is never parted from the
/// choice it introduces, and a range inside a placeholder is not a repeat; "line" and "json" type a parameter only as whole words,
/// and nothing follows a parameter that takes the rest of the line.</summary>
public sealed partial class VerbParamTests
{
    [Fact]
    public void EveryRowAndEveryAlternativeHasItsParameters()
    {
        foreach (var spec in VerbCatalog.All)
        {
            var forms = VerbParams.Forms(spec);
            Assert.True(forms.Count == VerbSyntax.Alternatives(spec).Count, $"{spec.Name}: {forms.Count} lists for {VerbSyntax.Alternatives(spec).Count} forms");
            Assert.All(forms, f => Assert.All(f, p => Assert.False(string.IsNullOrWhiteSpace(p.Name), spec.Name)));
            Assert.All(forms, f => Assert.Equal(f.Count, f.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()));
            Assert.Same(forms[0], VerbParams.For(spec));
        }
    }

    [Fact]
    public void TheTypesAgreeWithTheSyntaxHelpPrints()
    {
        foreach (var spec in VerbCatalog.All)
        {
            var alternatives = VerbSyntax.Alternatives(spec);
            var forms = VerbParams.Forms(spec);
            for (var f = 0; f < alternatives.Count; f++)
            {
                var tokens = VerbSyntax.RequiredTokens(spec, alternatives[f]);
                // HELP's required tokens run up to the form's first optional part; so do the parameters compared with them.
                var required = forms[f].TakeWhile(p => !p.Optional).ToList();
                var t = 0;
                foreach (var p in required)
                {
                    var where = $"{spec.Name} form {f + 1} ({alternatives[f]}), parameter {p.Name}";
                    Assert.True(t < tokens.Count, where + ": more required parameters than the syntax requires");
                    if (p.Keyword is { } keyword)
                    {
                        Assert.Equal(keyword, tokens[t++]);
                        Assert.True(t < tokens.Count && VerbSyntax.IsPlaceholder(tokens[t]), where + ": a keyword's value is a placeholder");
                        Assert.NotEqual(ParamKind.Enum, p.Kind);
                        t++;
                    }
                    else if (p.Kind is ParamKind.Enum)
                    {
                        Assert.False(VerbSyntax.IsPlaceholder(tokens[t]), where);
                        Assert.Equal(tokens[t++].Split('|'), p.Values);
                    }
                    else
                    {
                        Assert.True(VerbSyntax.IsPlaceholder(tokens[t++]), where + ": a typed value stands for a placeholder");
                    }
                }
                Assert.True(t == tokens.Count, $"{spec.Name} form {f + 1}: the syntax requires {tokens.Count} tokens, the parameters cover {t}");
            }
        }
    }

    [Fact]
    public void NoOverrideNamesAMissingRow()
    {
        Assert.NotEmpty(VerbParams.OverriddenRows);
        Assert.All(VerbParams.OverriddenRows, name => Assert.Equal(name, VerbCatalog.Find(name)?.Name));
    }

    [Fact]
    public void OnlyTheDesignsNamedRowsFireAtOnceAndEveryOtherHighRowIsHeld()
    {
        // A show's GO, its transport's play and pause, the blackout, the freeze and the panic are never confirmed.
        string[] instant = ["PLAYLIST GO", "PLAYLIST NEXT", "PLAYLIST PREV", "VIDEO PLAY", "VIDEO PAUSE", "BLACKOUT", "FREEZE", "PANIC"];
        foreach (var name in instant)
        {
            var spec = VerbCatalog.Find(name);
            Assert.Equal(name, spec?.Name);
            Assert.Equal(PressShapes.Instant, VerbParams.Press(spec!));
        }
        // A show class is no licence: lifting a panic, stopping or seeking a playlist, the master and an LED output are high risk and
        // held like any other.
        foreach (var name in new[] { "PANIC CLEAR", "VIDEO STOP", "VIDEO SEEK", "MASTER", "LED OUTPUT", "TIMECODE ENGAGE", "PLAYLIST ANCHOR" })
        {
            var spec = VerbCatalog.Find(name);
            Assert.Equal((name, VerbRisk.High), (spec?.Name, spec?.Risk));
            Assert.Equal(PressShapes.Hold, VerbParams.Press(spec!));
        }
        // The hot backup's takeovers and fences take or give the air: held like every other high-risk row (FLEET FENCE and MIRROR are
        // node-to-node and never a key's, but say held all the same).
        foreach (var name in new[] { "FAILOVER", "FAILBACK", "FLEET PROMOTE", "FLEET FENCE", "FLEET BACKUP", "FLEET SOLO", "MIRROR" })
            Assert.Equal((name, VerbRisk.High, PressShapes.Hold), (VerbCatalog.Find(name)?.Name, VerbCatalog.Find(name)?.Risk, VerbParams.Press(VerbCatalog.Find(name)!)));
        // A scan solved again, a view dropped, a survey imported, the site frame set: review rows that put no picture over the air (the
        // solve is recorded on the scan; the site frame moves the whole stage, every reprojection kept) fire at once.
        foreach (var name in new[] { "SCAN SOLVE", "SCAN DROP", "SURVEY IMPORT", "SITE ORIGIN", "SITE POINTS" })
            Assert.Equal((name, VerbRisk.Review, PressShapes.Instant), (VerbCatalog.Find(name)?.Name, VerbCatalog.Find(name)?.Risk, VerbParams.Press(VerbCatalog.Find(name)!)));
        // What replaces the show or cannot be taken back is pressed twice, as a projector's power is.
        string[] twice = ["PROJECT OPEN", "PROJECT NEW", "SCAN START", "APPROVE"];
        foreach (var name in twice)
            Assert.Equal(PressShapes.Double, VerbParams.Press(VerbCatalog.Find(name)!));
        Assert.All(VerbCatalog.All.Where(v => v.Show == ShowClass.DevicePower), v => Assert.Equal(PressShapes.Double, VerbParams.Press(v)));
        bool Named(VerbSpec v) => instant.Contains(v.Name) || twice.Contains(v.Name) || v.Show == ShowClass.DevicePower;
        // Every other high-risk row is held to confirm; a low-risk row fires at once, whatever its show class.
        Assert.All(VerbCatalog.All.Where(v => v.Risk == VerbRisk.High && !Named(v)), v => Assert.Equal(PressShapes.Hold, VerbParams.Press(v)));
        Assert.All(VerbCatalog.All.Where(v => v.Risk == VerbRisk.Low && !Named(v)), v => Assert.Equal(PressShapes.Instant, VerbParams.Press(v)));
    }

    [Fact]
    public void AnIdentifyThatPutsAPictureOverTheAirIsHeld()
    {
        foreach (var name in new[] { "OUTPUT IDENTIFY", "DEVICE IDENTIFY" })
        {
            var spec = VerbCatalog.Find(name);
            Assert.NotNull(spec);
            Assert.Equal(name, spec.Name);
            Assert.Equal(VerbRisk.Review, spec.Risk);
            Assert.Equal(PressShapes.Hold, VerbParams.Press(spec));
        }
        // Every other review row fires at once.
        Assert.All(VerbCatalog.All.Where(v => v.Risk == VerbRisk.Review && v.Name is not ("OUTPUT IDENTIFY" or "DEVICE IDENTIFY")
                && v.Show is not ShowClass.DevicePower),
            v => Assert.Equal(PressShapes.Instant, VerbParams.Press(v)));
    }

    [Fact]
    public void AKeywordIsNeverPartedFromItsChoiceAndARangeIsNotARepeat()
    {
        // An optional part "[KEYWORD A|B]" is one keyed choice, never a flag and a choice a key could send apart; a flag and a choice
        // in parts of their own ("[QUICK] [ALL|WAIT]") are two.
        foreach (var spec in VerbCatalog.All)
        {
            var alternatives = VerbSyntax.Alternatives(spec);
            var forms = VerbParams.Forms(spec);
            for (var f = 0; f < alternatives.Count && f < forms.Count; f++)
            {
                foreach (Match part in KeyedChoice().Matches(alternatives[f]))
                {
                    var keyword = part.Groups[1].Value;
                    var p = forms[f].SingleOrDefault(q => q.Keyword == keyword);
                    Assert.True(p is { Kind: ParamKind.Enum, Optional: true }, $"{spec.Name}: [{keyword} {part.Groups[2].Value}] is no keyed choice");
                    Assert.Equal(part.Groups[2].Value.Split('|'), p!.Values);
                }
            }
        }
        var mode = VerbParams.For("SCAN START").Single(p => p.Name == "mode");
        Assert.Equal((ParamKind.Enum, "MODE", true), (mode.Kind, mode.Keyword, mode.Optional));
        Assert.Equal(["FAST", "ROBUST", "DIFFICULT"], mode.Values);
        Assert.Equal(["hap", "hap-alpha", "hap-q", "hap-q-alpha"], VerbParams.For("MEDIA OPTIMISE").Single(p => p.Keyword == "TO").Values);

        Assert.False(Assert.Single(VerbParams.For("AUDIO GAIN")).Repeats);
        Assert.False(VerbParams.For("MOD BIND").Single(p => p.Name == "amount").Repeats);
        Assert.False(VerbParams.For("MEDIA HAS").Single().Repeats);
        Assert.True(VerbParams.For("ALIGN NEW").Single(p => p.Name == "output").Repeats);
        Assert.Equal((ParamKind.Time, "AT"), VerbParams.For("VIDEO RESTART").Single(p => p.Name == "at") is var at ? (at.Kind, at.Keyword) : default);

        var send = VerbParams.For("SEND ADD");
        Assert.Equal(("<w>x<h>", "@<fps>"), (send.Single(p => p.Name == "size").Alt, send.Single(p => p.Name == "fps").Alt));
        Assert.Equal(("QUALITY", ParamKind.Enum), send.Single(p => p.Name == "quality") is var q ? (q.Keyword, q.Kind) : default);
        var color = VerbParams.For("CURSOR").Single(p => p.Name == "color");
        Assert.Equal((ParamKind.Text, "COLOR", "#<rrggbb>"), (color.Kind, color.Keyword, color.Alt));
        Assert.True(VerbParams.For("HELLO")[0].Repeats);
    }

    [Fact]
    public void LineAndJsonTypeAParameterOnlyAsWholeWordsAndNothingFollowsOne()
    {
        // "<cursor,outlines,handles,lattice>" names layers, one word of them: "outlines" holds "line" inside another word.
        var layers = VerbParams.For("OVERLAY").Single(p => p.Keyword == "LAYERS");
        Assert.Equal((ParamKind.Text, true, "EDIT|ALL"), (layers.Kind, layers.Optional, layers.Alt));
        // A whole word types it: "<line>", "<target line>", "<json>".
        Assert.Equal(ParamKind.Line, VerbParams.For("DEVICE RAW").Single(p => p.Name == "line").Kind);
        Assert.Equal(ParamKind.Line, VerbParams.For("BIND LEARN").Single(p => p.Name == "target").Kind);
        Assert.Equal(ParamKind.Json, VerbParams.For("SET").Single(p => p.Name == "json").Kind);
        Assert.Equal(ParamKind.Json, VerbParams.For("PLAYLIST ANCHOR").Single(p => p.Name == "json").Kind);
        // JSON and an embedded line take the rest of the line, so no key could write a parameter after one.
        foreach (var spec in VerbCatalog.All)
        {
            foreach (var form in VerbParams.Forms(spec))
            {
                var rest = form.Select((p, i) => (p, i)).FirstOrDefault(x => x.p.Kind is ParamKind.Json or ParamKind.Line);
                Assert.True(rest.p is null || rest.i == form.Count - 1,
                    $"{spec.Name}: {rest.p?.Name} takes the rest of the line, yet {string.Join(", ", form.Skip(rest.i + 1).Select(p => p.Name))} follow it");
            }
        }
    }

    [Fact]
    public void AnOverridesLiteralWordsAreItsRowsSyntaxWords()
    {
        // An override restates a row's syntax; its keywords and literal choices are words of that syntax, so a catalogue edit that drops
        // or renames one fails here instead of a key sending a word the node no longer reads.
        foreach (var name in VerbParams.OverriddenRows)
        {
            var spec = VerbCatalog.Find(name)!;
            var words = spec.Syntax.Split([' ', '|', '[', ']', '…'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (var p in VerbParams.Forms(spec).SelectMany(f => f))
            {
                if (p.Keyword is { } keyword)
                    Assert.True(words.Contains(keyword), $"{name}: keyword {keyword} is no word of '{spec.Syntax}'");
                if (p.Kind is ParamKind.Enum or ParamKind.Flag)
                    Assert.All(p.Values ?? [], v => Assert.True(words.Contains(v), $"{name}: {p.Name} names {v}, no word of '{spec.Syntax}'"));
            }
        }
    }

    [Fact]
    public void AControllersFormsReadAsTheyShould()
    {
        var go = VerbParams.For("PLAYLIST GO");
        Assert.Equal(["playlist", "item", "at", "expect"], go.Select(p => p.Name));
        Assert.Equal((ParamKind.Entity, "pl", false), (go[0].Kind, go[0].Prefix, go[0].Optional));
        Assert.Equal((ParamKind.Entity, "it", true, "#<n>"), (go[1].Kind, go[1].Prefix, go[1].Optional, go[1].Alt));
        Assert.Equal((ParamKind.Time, "AT", true), (go[2].Kind, go[2].Keyword, go[2].Optional));
        Assert.Equal(("EXPECT", "it"), (go[3].Keyword, go[3].Prefix));

        var set = VerbParams.Forms(VerbCatalog.Find("GENERATOR SET")!);
        Assert.Equal(2, set.Count);
        Assert.Equal("BY", set[1][2].Keyword);

        var blackout = Assert.Single(VerbParams.For("BLACKOUT"));
        Assert.Equal(["ON", "OFF", "TOGGLE"], blackout.Values);
        Assert.Empty(VerbParams.For("INDEX"));
        Assert.Empty(VerbParams.For("NO SUCH VERB"));

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            VerbParams.Write(w, go);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan));
        var at = doc.RootElement[2];
        Assert.Equal(("at", "time", true, "AT", "<unix ms>"), (at.GetProperty("name").GetString(), at.GetProperty("kind").GetString(),
            at.GetProperty("optional").GetBoolean(), at.GetProperty("keyword").GetString(), at.GetProperty("alt").GetString()));
        Assert.False(doc.RootElement[0].TryGetProperty("optional", out _));
    }

    /// <summary>An optional part of a keyword and a literal choice: "[MODE FAST|ROBUST]".</summary>
    [GeneratedRegex(@"\[([A-Z][A-Z0-9-]*) ([A-Za-z0-9][A-Za-z0-9+,._-]*(?:\|[A-Za-z0-9][A-Za-z0-9+,._-]*)+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex KeyedChoice();
}
