using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Wire;

/// <summary>Typed parameters for the whole catalogue: every row and every alternative has its list; the list agrees with the syntax
/// HELP prints (each token up to the first optional part is a required parameter — a keyword and its value, a literal choice with
/// the same words, or a placeholder); no override names a row the catalogue lacks; every Panic, Air and Transport row fires at once;
/// a keyword is never parted from the choice it introduces, and a range inside a placeholder is not a repeat.</summary>
public sealed class VerbParamTests
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
    public void EveryPanicAirAndTransportRowFiresAtOnce()
    {
        var air = VerbCatalog.All.Where(v => v.Show is ShowClass.Panic or ShowClass.Air or ShowClass.Transport).ToList();
        Assert.NotEmpty(air);
        Assert.All(air, v => Assert.Equal(PressShapes.Instant, VerbParams.Press(v)));
        foreach (var name in new[] { "PANIC", "BLACKOUT", "PLAYLIST GO", "PLAYLIST NEXT", "PLAYLIST PREV" })
            if (VerbCatalog.Find(name) is { } spec && spec.Name == name)
                Assert.Equal(PressShapes.Instant, VerbParams.Press(spec));
        // Anything else that is high risk is held to confirm; low risk fires at once.
        string[] twice = ["PROJECT OPEN", "PROJECT NEW", "SCAN START", "APPROVE"];
        Assert.All(VerbCatalog.All.Where(v => v.Risk == VerbRisk.High && !air.Contains(v) && v.Show != ShowClass.DevicePower && !twice.Contains(v.Name)),
            v => Assert.Equal(PressShapes.Hold, VerbParams.Press(v)));
        // What replaces the show or cannot be taken back is pressed twice, as a projector's power is.
        foreach (var name in twice)
            Assert.Equal(PressShapes.Double, VerbParams.Press(VerbCatalog.Find(name)!));
        Assert.All(VerbCatalog.All.Where(v => v.Show == ShowClass.DevicePower), v => Assert.Equal(PressShapes.Double, VerbParams.Press(v)));
        Assert.All(VerbCatalog.All.Where(v => v.Risk == VerbRisk.Low && !air.Contains(v)), v => Assert.Equal(PressShapes.Instant, VerbParams.Press(v)));
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
        foreach (var spec in VerbCatalog.All)
        {
            foreach (var form in VerbParams.Forms(spec))
            {
                for (var i = 0; i + 1 < form.Count; i++)
                {
                    Assert.False(form[i] is { Kind: ParamKind.Flag } && form[i + 1] is { Kind: ParamKind.Enum, Optional: true, Keyword: null },
                        $"{spec.Name}: the flag {form[i].Name} stands before the choice {form[i + 1].Name}, either of which a key could send alone");
                }
            }
        }
        var mode = VerbParams.For("SCAN START").Single(p => p.Name == "mode");
        Assert.Equal((ParamKind.Enum, "MODE", true), (mode.Kind, mode.Keyword, mode.Optional));
        Assert.Equal(["FAST", "ROBUST", "DIFFICULT"], mode.Values);
        Assert.Equal(["hap", "hap-alpha", "hap-q", "hap-q-alpha", "rechunk"], VerbParams.For("MEDIA OPTIMISE").Single(p => p.Keyword == "TO").Values);

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
}
