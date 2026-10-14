using PlayMap.Core.Effects;

namespace PlayMap.Core.Tests.Effects;

/// <summary>The translation's text (the GPU compile of it is IsfTranslatorTests in the render suite), the WIDTH/HEIGHT evaluator,
/// the ES renames and an effect's clock.</summary>
public sealed class IsfTranslationTests
{
    private sealed class Vars(double w, double h, double amount) : IIsfVariables
    {
        public bool TryGet(string name, out double value)
        {
            value = name switch { "WIDTH" => w, "HEIGHT" => h, "amount" => amount, _ => double.NaN };
            return !double.IsNaN(value);
        }
    }

    [Theory]
    [InlineData("$WIDTH/2", 960)]
    [InlineData("floor($HEIGHT/7)", 154)]
    [InlineData("max(floor($WIDTH*0.02), 1.0)", 38)]
    [InlineData("$WIDTH * $amount + 2^3", 488)]
    [InlineData("-(-$WIDTH) % 1000", 920)]
    [InlineData("ceil(sqrt(abs(-16))) + round(0.5) + min(2, pow(2, 3))", 7)]
    [InlineData("1.5e1", 15)]
    public void AnExpressionEvaluatesWithItsVariables(string text, int expected)
    {
        var e = IsfExpression.Parse(text);
        Assert.Null(e.Error);
        Assert.Equal(expected, e.Evaluate(new Vars(1920, 1080, 0.25), 99, out var clamped));
        Assert.False(clamped);
    }

    [Theory]
    [InlineData("$WIDTH - 5000")]
    [InlineData("$nothing")]
    [InlineData("1/0 - 1/0")]
    public void ANonPositiveOrUnknownResultIsOneAndSaysSo(string text)
    {
        Assert.Equal(1, IsfExpression.Parse(text).Evaluate(new Vars(1920, 1080, 0), 99, out var clamped));
        Assert.True(clamped);
    }

    [Theory]
    [InlineData("floor($WIDTH")]
    [InlineData("sin($WIDTH)")]
    [InlineData("$WIDTH $HEIGHT")]
    [InlineData("max(1)")]
    public void ATextThatDoesNotParseKeepsItsErrorAndTheFallback(string text)
    {
        var e = IsfExpression.Parse(text);
        Assert.NotNull(e.Error);
        Assert.Equal(99, e.Evaluate(new Vars(1920, 1080, 0), 99, out _));
    }

    [Fact]
    public void TheTranslationDeclaresRewritesAndKeepsTheBodysLines()
    {
        var doc = IsfParser.Parse(IsfParserTests.Full.Replace("gl_FragColor = IMG_THIS_PIXEL(inputImage);",
            "#version 120\n    precision mediump float;\n    gl_FragColor = IMG_PIXEL(inputImage, gl_FragCoord.xy) + IMG_NORM_PIXEL(acc, vv_FragNormCoord, 1.0)\n" +
            "        + IMG_THIS_NORM_PIXEL(half) + IMG_NORM_THIS_PIXEL(matte) * IMG_SIZE(grain).x;", StringComparison.Ordinal));
        var src = IsfTranslator.Translate(doc, null, IsfDialect.Glsl330);
        Assert.Empty(src.Issues);
        Assert.DoesNotContain("#version", src.Fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("precision", src.Fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("#version", src.Vertex, StringComparison.Ordinal);
        foreach (var decl in new[] { "uniform float amount;", "uniform int mode;", "uniform bool on;", "uniform bool kick;", "uniform vec2 center;",
            "uniform vec4 tint;", "uniform sampler2D acc;", "uniform vec2 _half_imgSize;", "uniform vec4 _grain_imgRect;", "uniform bool _wave_flip;",
            "uniform int PASSINDEX;", "uniform vec4 DATE;", "uniform int FRAMEINDEX;", "in vec2 isf_FragNormCoord;" })
            Assert.Contains(decl, src.Fragment, StringComparison.Ordinal);
        Assert.Contains("isf_sample(inputImage, _inputImage_flip, (gl_FragCoord.xy) / _inputImage_imgSize)", src.Fragment, StringComparison.Ordinal);
        Assert.Contains("isf_sample(acc, _acc_flip, (isf_FragNormCoord), 1.0)", src.Fragment, StringComparison.Ordinal);
        Assert.Contains("isf_sample(half, _half_flip, isf_FragNormCoord)", src.Fragment, StringComparison.Ordinal);
        Assert.Contains("isf_sample(matte, _matte_flip, isf_FragNormCoord)", src.Fragment, StringComparison.Ordinal);
        Assert.Contains("(_grain_imgSize).x", src.Fragment, StringComparison.Ordinal);
        Assert.Contains("void main(void) { isf_vertShaderInit(); }", src.Vertex, StringComparison.Ordinal);
        Assert.Contains("out vec2 isf_FragNormCoord;", src.Vertex, StringComparison.Ordinal);
        Assert.DoesNotContain("float bias", src.Vertex, StringComparison.Ordinal);
        Assert.DoesNotContain("isf_FragColor", src.Vertex, StringComparison.Ordinal);

        // The body after "#line 1 1" has exactly the file's body lines, so line n of source string 1 is file line BodyLine + n − 1.
        var body = src.Fragment[(src.Fragment.IndexOf("#line 1 1\n", StringComparison.Ordinal) + 10)..];
        Assert.Equal(doc.Body.Split('\n').Length, body.TrimEnd('\n').Split('\n').Length + (doc.Body.EndsWith('\n') ? 1 : 0));
        var issues = src.LineMap.Read("0:3(10): error: `x' undeclared\n1:4(12): error: `y' undeclared\n1:5(1): warning: unused", vertex: false);
        Assert.Equal([new IsfIssue(0, 0, "in PlayMap's prelude: `x' undeclared"), new IsfIssue(doc.BodyLine + 3, 12, "`y' undeclared")], issues.ToArray());
        Assert.Equal([new IsfIssue(doc.BodyLine + 1, 0, "unexpected token")], src.LineMap.Read("ERROR: 1:2: unexpected token", vertex: false).ToArray());
        Assert.Equal([new IsfIssue(doc.BodyLine + 6, 0, "syntax error")], src.LineMap.Read("1(7) : error C0000: syntax error", vertex: false).ToArray());
    }

    [Fact]
    public void AWrongCallIsAnIssueAtItsFileLine()
    {
        var doc = IsfParser.Parse("/*{ \"INPUTS\": [ { \"NAME\": \"inputImage\", \"TYPE\": \"image\" } ] }*/\nvoid main() {\n  vec4 a = IMG_PIXEL(inputImage);\n  vec4 b = IMG_THIS_PIXEL(nothing);\n}\n");
        var src = IsfTranslator.Translate(doc, null, IsfDialect.Glsl330);
        Assert.Equal([new IsfIssue(3, 0, "IMG_PIXEL takes 2 or 3 values, here 1"),
            new IsfIssue(4, 0, "IMG_THIS_PIXEL reads 'nothing', which is not an image, pass target or import of this file")], src.Issues.ToArray());
    }

    [Fact]
    public void ACustomVertexShaderGetsTheVertexPreludeAndKeepsItsLines()
    {
        var doc = IsfParser.Parse("/*{ \"INPUTS\": [ { \"NAME\": \"inputImage\", \"TYPE\": \"image\" } ] }*/\nvarying vec2 left;\nvoid main() { gl_FragColor = IMG_NORM_PIXEL(inputImage, left); }\n");
        var src = IsfTranslator.Translate(doc, "varying vec2 left;\nvoid main() {\n  vv_vertShaderInit();\n  left = isf_FragNormCoord - vec2(0.1, 0.0);\n}\n", IsfDialect.Glsl330);
        Assert.Contains("#define varying out\n#define attribute in\n#define texture2D texture\n", src.Vertex, StringComparison.Ordinal);
        Assert.Contains("void isf_vertShaderInit(void)", src.Vertex, StringComparison.Ordinal);
        Assert.Contains("#line 1 1\nvarying vec2 left;\nvoid main() {\n  isf_vertShaderInit();", src.Vertex, StringComparison.Ordinal);
        Assert.Equal(1, src.LineMap.VertexBodyLine);
        Assert.Equal([new IsfIssue(3, 0, "vertex shader: bad")], src.LineMap.Read("1:3(0): error: bad", vertex: true).ToArray());
    }

    [Fact]
    public void EsRenamesOnlyDefinitionsOfBuiltInNames()
    {
        const string body = """
            // float distance(vec2 a) is only a comment
            float mod(float a, float b) { return a - b * floor(a / b); }
            vec3 normalize3(vec3 v) { return v; }
            float length(vec2 v);
            void main() { float d = distance(vec2(0.0), vec2(1.0)); }
            """;
        Assert.Equal("#define length isf_user_length\n#define mod isf_user_mod\n", IsfEsFixups.Renames(body));
        var doc = IsfParser.Parse("/*{}*/\n" + body);
        Assert.Contains("#define mod isf_user_mod\n#line 1 1\n", IsfTranslator.Translate(doc, null, IsfDialect.Es300).Fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("isf_user", IsfTranslator.Translate(doc, null, IsfDialect.Glsl330).Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEffectsClockIsAPureFunctionOfShowTime()
    {
        const long origin = 2_000_000_000;
        var c0 = FxTime.At(origin, origin, 60, 1_760_000_000_000);
        Assert.Equal((0f, 0f, 0), (c0.Time, c0.TimeDelta, c0.FrameIndex));
        var c1 = FxTime.At(origin + 1_500_000_000, origin, 60, 1_760_000_000_000);
        Assert.Equal((1.5f, (float)(1.0 / 60), 90), (c1.Time, c1.TimeDelta, c1.FrameIndex));
        Assert.Equal(c1, FxTime.At(origin + 1_500_000_000, origin, 60, 1_760_000_000_000));
        // 2025-10-09T08:53:20Z
        Assert.Equal((2025f, 10f, 9f, 8 * 3600f + 53 * 60 + 20), (c1.Year, c1.Month, c1.Day, c1.DaySeconds));
        Assert.False(c1.Reanchor);
        Assert.True(FxTime.At(origin - 1, origin, 60, 0).Reanchor);
        Assert.Equal(0, FxTime.At(origin - 1_000_000_000, origin, 60, 0).FrameIndex);
        Assert.True(FxTime.At(origin + 70_000L * 1_000_000_000, origin, 60, 0).Reanchor);
    }
}
