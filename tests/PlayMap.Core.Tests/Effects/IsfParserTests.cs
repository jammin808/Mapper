using PlayMap.Core.Content;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Effects;

/// <summary>The ISF header as the parser reads it: every key of v2, v1's PERSISTENT_BUFFERS folded into passes, the role from the
/// picture inputs, the body's first line, and a damaged file read as invalid with its line instead of an exception.</summary>
public sealed class IsfParserTests
{
    internal const string Full = """
        /*{
            "ISFVSN": "2",
            "VSN": "1.2",
            "DESCRIPTION": "every key",
            "CREDIT": "PlayMap",
            "CATEGORIES": ["Blur", "Test"],
            "INPUTS": [
                { "NAME": "inputImage", "TYPE": "image" },
                { "NAME": "amount", "TYPE": "float", "LABEL": "Amount", "DEFAULT": 0.25, "MIN": 0.0, "MAX": 2.0, "IDENTITY": 0.0 },
                { "NAME": "mode", "TYPE": "long", "VALUES": [0, 1, 4], "LABELS": ["Off", "Soft", "Hard"], "DEFAULT": 4 },
                { "NAME": "steps", "TYPE": "long", "MIN": 1, "MAX": 8, "DEFAULT": 3 },
                { "NAME": "on", "TYPE": "bool", "DEFAULT": true },
                { "NAME": "kick", "TYPE": "event" },
                { "NAME": "center", "TYPE": "point2D", "DEFAULT": [0.5, 0.25], "MIN": [0, 0], "MAX": [1, 1] },
                { "NAME": "tint", "TYPE": "color", "DEFAULT": [1.0, 0.5, 0.0, 0.75] },
                { "NAME": "matte", "TYPE": "image" },
                { "NAME": "wave", "TYPE": "audio", "MAX": 64 },
                { "NAME": "fft", "TYPE": "audioFFT" }
            ],
            "PASSES": [
                { "TARGET": "half", "WIDTH": "$WIDTH/2", "HEIGHT": "floor($HEIGHT/2)" },
                { "TARGET": "acc", "PERSISTENT": true, "FLOAT": true },
                { }
            ],
            "IMPORTED": { "grain": { "PATH": "grain.png" } }
        }*/
        void main() {
            gl_FragColor = IMG_THIS_PIXEL(inputImage);
        }
        """;

    [Fact]
    public void EveryV2KeyIsRead()
    {
        var doc = IsfParser.Parse(Full);
        Assert.True(doc.Valid);
        Assert.Empty(doc.Issues);
        Assert.Equal(("2", "1.2", "every key", "PlayMap"), (doc.IsfVsn, doc.Vsn, doc.Description, doc.Credit));
        Assert.Equal(["Blur", "Test"], doc.Categories.ToArray());
        Assert.Equal(IsfRole.Filter, doc.Role);
        Assert.Equal(11, doc.Inputs.Length);
        var amount = doc.Input("amount")!;
        Assert.Equal((IsfInputType.Float, "Amount", 0.25, 0.0, 2.0, 0.0), (amount.Type, amount.Label, amount.Default[0], amount.Min[0], amount.Max[0], amount.Identity[0]));
        var mode = doc.Input("mode")!;
        Assert.Equal([0, 1, 4], mode.Values.ToArray());
        Assert.Equal(["Off", "Soft", "Hard"], mode.Labels.ToArray());
        Assert.Equal([0.5, 0.25], doc.Input("center")!.Default.ToArray());
        Assert.Equal([1.0, 0.5, 0.0, 0.75], doc.Input("tint")!.Default.ToArray());
        Assert.Equal(1.0, doc.Input("on")!.Default[0]);
        Assert.Equal(IsfInputType.Event, doc.Input("kick")!.Type);
        Assert.Equal((IsfInputType.Audio, 64), (doc.Input("wave")!.Type, doc.Input("wave")!.MaxSamples));
        Assert.Equal(IsfInputType.AudioFft, doc.Input("fft")!.Type);
        Assert.Equal(3, doc.Passes.Length);
        Assert.Equal(new IsfPass("half", false, false, "$WIDTH/2", "floor($HEIGHT/2)"), doc.Passes[0]);
        Assert.Equal(new IsfPass("acc", true, true, "", ""), doc.Passes[1]);
        Assert.Equal(new IsfPass("", false, false, "", ""), doc.Passes[2]);
        Assert.Equal([new IsfImported("grain", "grain.png")], doc.Imported.ToArray());
        Assert.Equal(["inputImage", "matte", "wave", "fft", "half", "acc", "grain"], doc.Images.ToArray());
        Assert.True(doc.HistoryDependent);
        Assert.True(doc.ReadsAudio);
        Assert.Equal(26, doc.BodyLine);
        // The body starts at the header's line break, whichever line ends the checkout gave this file.
        Assert.StartsWith("\nvoid main()", doc.Body.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithWindowsLineEndsReadsAsTheSameDocument()
    {
        var lf = IsfParser.Parse(Full.ReplaceLineEndings("\n"));
        var crlf = IsfParser.Parse(Full.ReplaceLineEndings("\r\n"));
        Assert.True(crlf.Valid);
        Assert.Empty(crlf.Issues);
        Assert.Equal((lf.BodyLine, lf.Inputs.Length, lf.Passes.Length), (crlf.BodyLine, crlf.Inputs.Length, crlf.Passes.Length));
        Assert.Equal(lf.Images.ToArray(), crlf.Images.ToArray());
        Assert.Equal(lf.Body, crlf.Body.ReplaceLineEndings("\n"));
        var damaged = IsfParser.Parse("\r\n\r\n/*{ \"INPUTS\": [] \r\nvoid main() {}");
        Assert.Equal(3, Assert.Single(damaged.Issues).Line);
    }

    [Fact]
    public void V1PersistentBuffersMakeTheirPassesPersistent()
    {
        const string v1 = """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" } ],
                "PERSISTENT_BUFFERS": { "trail": { "WIDTH": "$WIDTH", "FLOAT": true }, "idle": {} },
                "PASSES": [ { "TARGET": "trail" }, { } ] }*/
            void main() { gl_FragColor = IMG_NORM_PIXEL(trail, vv_FragNormCoord); }
            """;
        var doc = IsfParser.Parse(v1);
        Assert.True(doc.Valid);
        Assert.Equal(new IsfPass("trail", true, true, "$WIDTH", ""), doc.Passes[0]);
        Assert.False(doc.Passes[1].Persistent);
        Assert.Equal(["inputImage", "trail", "idle"], doc.Images.ToArray());
        Assert.True(doc.HistoryDependent);

        var names = IsfParser.Parse("""
            /*{ "PERSISTENT_BUFFERS": ["buf"], "PASSES": [ { "TARGET": "buf" } ] }*/
            void main() { gl_FragColor = vec4(1.0); }
            """);
        Assert.True(names.Passes[0].Persistent);
        Assert.Equal(IsfRole.Generator, names.Role);
    }

    [Fact]
    public void TheRoleComesFromThePictureInputsAndAMissingPassListIsOnePass()
    {
        var transition = IsfParser.Parse("""
            /*{ "INPUTS": [ { "NAME": "startImage", "TYPE": "image" }, { "NAME": "endImage", "TYPE": "image" }, { "NAME": "progress", "TYPE": "float" } ] }*/
            void main() { gl_FragColor = mix(IMG_THIS_PIXEL(startImage), IMG_THIS_PIXEL(endImage), progress); }
            """);
        Assert.Equal(IsfRole.Transition, transition.Role);
        Assert.Equal([new IsfPass("", false, false, "", "")], transition.Passes.ToArray());
        Assert.False(transition.HistoryDependent);
    }

    [Theory]
    [InlineData("void main() {}", 1, "an ISF file starts with its JSON header")]
    [InlineData("/*{ \"INPUTS\": [ }*/\nvoid main() {}", 1, "the JSON header does not read")]
    [InlineData("\n\n/*{ \"INPUTS\": [] \nvoid main() {}", 3, "never closed")]
    [InlineData("/*[1, 2]*/ void main() {}", 1, "not an object")]
    public void ADamagedFileIsInvalidWithItsLine(string source, int line, string words)
    {
        var doc = IsfParser.Parse(source);
        Assert.False(doc.Valid);
        var issue = Assert.Single(doc.Issues);
        Assert.Equal(line, issue.Line);
        Assert.Contains(words, issue.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownInputTypeIsLeftOutWithAnIssue()
    {
        var doc = IsfParser.Parse("""
            /*{ "INPUTS": [ { "NAME": "a", "TYPE": "matrix" }, { "TYPE": "float" }, { "NAME": "b", "TYPE": "float" }, { "NAME": "b", "TYPE": "bool" } ] }*/
            void main() {}
            """);
        Assert.True(doc.Valid);
        Assert.Equal(["b"], doc.Inputs.Select(i => i.Name));
        Assert.Equal(3, doc.Issues.Length);
        Assert.Contains(doc.Issues, i => i.Words.Contains("unknown TYPE 'matrix'", StringComparison.Ordinal));
    }

    [Fact]
    public void AKindMapsEveryInputToTheGeneratorsParameterType()
    {
        var kind = FxKinds.FromIsf("isf:test", FxFormat.Isf, "Test", "blur", "", IsfParser.Parse(Full));
        Assert.Equal(["amount", "mode", "steps", "on", "kick", "center.x", "center.y", "tint"], kind.Params.Select(p => p.Name));
        Assert.Equal(ParamSpec.Num("amount", 0.25, 0, 2, "Amount"), kind.Param("amount"));
        Assert.Equal(ParamSpec.Pick("mode", "Hard", "mode", "Off", "Soft", "Hard").Choices, kind.Param("mode")!.Choices);
        Assert.Equal("Hard", kind.Param("mode")!.Default);
        Assert.Equal((ParamType.Integer, "3", 1.0, 8.0), (kind.Param("steps")!.Type, kind.Param("steps")!.Default, kind.Param("steps")!.Min, kind.Param("steps")!.Max));
        Assert.Equal((ParamType.Bool, "true"), (kind.Param("on")!.Type, kind.Param("on")!.Default));
        Assert.Equal("0.25", kind.Param("center.y")!.Default);
        Assert.Equal("#FF8000", kind.Param("tint")!.Default);
        Assert.Equal(["matte", "wave", "fft"], kind.ImageInputs.ToArray());
        Assert.Equal(new FxTraits(true, true, false, false, false), kind.Traits);
        Assert.True(kind.AtDefaults(ParamSet.Empty));
        Assert.False(kind.AtDefaults(ParamSet.Empty.With("amount", 1.0)));

        Span<float> v = stackalloc float[4];
        Assert.True(FxKinds.TryUniform(kind.Document.Input("mode")!, kind, ParamSet.Empty.With("mode", "Soft"), v));
        Assert.Equal(1f, v[0]);
        Assert.True(FxKinds.TryUniform(kind.Document.Input("mode")!, kind, ParamSet.Empty, v));
        Assert.Equal(4f, v[0]);
        Assert.True(FxKinds.TryUniform(kind.Document.Input("tint")!, kind, ParamSet.Empty, v));
        Assert.Equal([1f, 128 / 255f, 0f, 0.75f], v.ToArray());
        Assert.True(FxKinds.TryUniform(kind.Document.Input("center")!, kind, ParamSet.Empty.With("center.x", 0.75), v));
        Assert.Equal((0.75f, 0.25f), (v[0], v[1]));
        Assert.False(FxKinds.TryUniform(kind.Document.Input("matte")!, kind, ParamSet.Empty, v));
    }
}
