using PlayMap.Core.Effects;
using PlayMap.Render.Effects;
using PlayMap.Render.Gl;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests.Effects;

/// <summary>Translated ISF compiles and links on the GL 3.3 core context (the device writes the dialect's header): every
/// built-in, samples that use every feature the translation rewrites, a vertex file that passes a varying to its fragment file,
/// and — opt-in, with PLAYMAP_ISF_CORPUS naming a checkout of the public ISF files — at least 324 of the corpus's 327.</summary>
[Collection("gl")]
public sealed class IsfTranslatorTests(GlFixture gl, ITestOutputHelper log)
{
    /// <summary>Samples, one per feature: multi-pass with sizes, persistent float buffers, every input type, bias sampling, v1's
    /// names and PERSISTENT_BUFFERS, IMG_SIZE, a #version line and a precision line in the body, sound inputs, an import.</summary>
    internal static readonly (string Name, string Fs, string? Vs)[] Samples =
    [
        ("passes", """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" }, { "NAME": "amount", "TYPE": "float", "DEFAULT": 0.5 } ],
                "PASSES": [ { "TARGET": "small", "WIDTH": "floor($WIDTH/4)", "HEIGHT": "$HEIGHT/4" }, { "TARGET": "acc", "PERSISTENT": true, "FLOAT": true }, {} ] }*/
            void main() {
                if (PASSINDEX == 0) gl_FragColor = IMG_THIS_NORM_PIXEL(inputImage);
                else if (PASSINDEX == 1) gl_FragColor = mix(IMG_NORM_PIXEL(acc, isf_FragNormCoord), IMG_NORM_THIS_PIXEL(small), amount);
                else gl_FragColor = IMG_PIXEL(acc, gl_FragCoord.xy) * vec4(IMG_SIZE(small) / RENDERSIZE, 1.0, 1.0);
            }
            """, null),
        ("inputs", """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" }, { "NAME": "on", "TYPE": "bool", "DEFAULT": true },
                { "NAME": "kick", "TYPE": "event" }, { "NAME": "mode", "TYPE": "long", "VALUES": [0, 1], "LABELS": ["A", "B"] },
                { "NAME": "pos", "TYPE": "point2D", "DEFAULT": [0.5, 0.5] }, { "NAME": "tint", "TYPE": "color", "DEFAULT": [1, 0, 0, 1] },
                { "NAME": "matte", "TYPE": "image" }, { "NAME": "wave", "TYPE": "audio", "MAX": 32 }, { "NAME": "fft", "TYPE": "audioFFT" } ],
                "IMPORTED": { "grain": { "PATH": "grain.png" } } }*/
            #version 120
            precision mediump float;
            void main() {
                vec4 c = IMG_NORM_PIXEL(inputImage, isf_FragNormCoord, 0.5) + IMG_PIXEL(matte, gl_FragCoord.xy, 1.0);
                float s = IMG_NORM_PIXEL(wave, vec2(pos.x, 0.0)).r + IMG_NORM_PIXEL(fft, pos).r + IMG_THIS_PIXEL(grain).g;
                gl_FragColor = (on && !kick && mode == 1) ? tint * s : c * vec4(TIME, TIMEDELTA, float(FRAMEINDEX), DATE.w);
            }
            """, null),
        ("v1", """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" } ], "PERSISTENT_BUFFERS": ["trail"],
                "PASSES": [ { "TARGET": "trail" }, {} ] }*/
            varying vec2 left;
            void main() {
                vec4 t = texture2D(trail, vv_FragNormCoord);
                gl_FragColor = PASSINDEX == 0 ? max(IMG_NORM_PIXEL(inputImage, left), t * 0.9) : t;
            }
            """, """
            varying vec2 left;
            void main() {
                vv_vertShaderInit();
                left = vv_FragNormCoord + vec2(IMG_SIZE(inputImage).x * 0.0, 0.0);
            }
            """),
        ("generator", """
            /*{ "ISFVSN": "2", "INPUTS": [ { "NAME": "speed", "TYPE": "float", "DEFAULT": 1.0 } ] }*/
            float wave(float x) { return 0.5 + 0.5 * sin(x); }
            void main() { gl_FragColor = vec4(wave(TIME * speed + isf_FragNormCoord.x * 6.28318), isf_FragNormCoord.y, 0.0, 1.0); }
            """, null),
    ];

    /// <summary>The ISF dialect a context compiles: desktop GLSL 3.30 on a core context, ES 3.00 on any other.</summary>
    private static IsfDialect DialectOf(GlDevice d) => d.Dialect == GlDialect.Core330 ? IsfDialect.Glsl330 : IsfDialect.Es300;

    private static void Compile(GlDevice d, string name, string fs, string? vs)
    {
        var doc = IsfParser.Parse(fs);
        Assert.True(doc.Valid, $"{name}: {string.Join("; ", doc.Issues)}");
        var src = IsfTranslator.Translate(doc, vs, DialectOf(d));
        Assert.Empty(src.Issues);
        using var program = d.CreateProgram("isf " + name, src.Vertex, src.Fragment);
        Assert.NotNull(program);
    }

    [Fact]
    public void EveryBuiltInCompilesOnCore33()
    {
        gl.Run(c =>
        {
            foreach (var kind in FxCatalog.Builtin.Kinds)
                Compile(c.Device, kind.Id, FxCatalog.Builtin.Source(kind.Id)!.Fragment, null);
        });
    }

    [Fact]
    public void EverySampleCompilesOnCore33()
    {
        gl.Run(c =>
        {
            foreach (var (name, fs, vs) in Samples)
                Compile(c.Device, name, fs, vs);
        });
    }

    [Fact]
    public void AVertexFileThatPassesAVaryingCompilesAndItsErrorsMapToItsLines()
    {
        const string fs = """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" } ] }*/
            varying vec2 shifted;
            void main() { gl_FragColor = IMG_NORM_PIXEL(inputImage, shifted); }
            """;
        const string vs = "varying vec2 shifted;\nvoid main() {\n    isf_vertShaderInit();\n    shifted = isf_FragNormCoord + vec2(0.25, 0.0);\n}\n";
        gl.Run(c =>
        {
            Compile(c.Device, "varying", fs, vs);
            var broken = IsfTranslator.Translate(IsfParser.Parse(fs.Replace("shifted);", "nowhere);", StringComparison.Ordinal)), vs, IsfDialect.Glsl330);
            var error = Assert.Throws<GlException>(() => c.Device.CreateProgram("isf broken", broken.Vertex, broken.Fragment));
            var issues = broken.LineMap.Read(error.Message, vertex: false);
            Assert.NotEmpty(issues);
            Assert.All(issues, i => Assert.Equal(3, i.Line));
            Assert.Contains("nowhere", issues[0].Words, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheCorpusCompilesWhenACheckoutIsNamed()
    {
        var folder = Environment.GetEnvironmentVariable("PLAYMAP_ISF_CORPUS");
        if (string.IsNullOrWhiteSpace(folder))
        {
            log.WriteLine("PLAYMAP_ISF_CORPUS is not set: the corpus count is the integration gate's (opt-in here)");
            return;
        }
        var files = Directory.GetFiles(folder, "*.fs").Order(StringComparer.Ordinal).ToArray();
        var failed = new List<string>();
        gl.Run(c =>
        {
            foreach (var file in files)
            {
                var vsPath = Path.ChangeExtension(file, ".vs");
                var doc = IsfParser.Parse(File.ReadAllText(file));
                var src = IsfTranslator.Translate(doc, File.Exists(vsPath) ? File.ReadAllText(vsPath) : null, DialectOf(c.Device));
                try
                {
                    using var program = c.Device.CreateProgram("isf corpus", src.Vertex, src.Fragment);
                }
                catch (GlException ex)
                {
                    var first = src.LineMap.Read(ex.Message, ex.Message.Contains("(vertex)", StringComparison.Ordinal)).FirstOrDefault();
                    failed.Add($"{Path.GetFileName(file)}: line {first.Line}: {first.Words}");
                }
            }
            c.Device.TakeErrors();
        });
        log.WriteLine($"{files.Length - failed.Count}/{files.Length} compile and link at GLSL 3.30 core");
        foreach (var f in failed)
            log.WriteLine("  " + f);
        Assert.True(files.Length - failed.Count >= 324 * files.Length / 327, $"{files.Length - failed.Count}/{files.Length}");
    }
}
