using System.Text;
using PlayMap.Core.Effects;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Gl;
using PlayMap.Render.Tests.Effects;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>A user's effect is a program the device makes at run time: a translated ISF source through
/// <see cref="IGpuDevice.CreateProgramAsync"/> answers a layout equal to the uniforms and samplers the translation declares; a
/// compile error answers the stage that refused, and its words map back to the ISF file's line, which the effect's slot says; a
/// program asked to be released stays until the last frame that drew with it has completed, and an effect's program the lane lets
/// go of is released by the device; and the effects draw through programs the device made, setting only names their layouts hold.
/// On the run's dialect (core 3.30, or ES 3.00 with PLAYMAP_GL_DIALECT=es300).</summary>
[Collection("gpu")]
public sealed class UserProgramTests(BackendFixture backends)
{
    private const int W = 64, H = 36;

    /// <summary>Every input type a uniform carries and one picture, each read by the body so the linker keeps it.</summary>
    private const string EveryInput = """
        /*{ "ISFVSN": "2", "INPUTS": [
            { "NAME": "inputImage", "TYPE": "image" },
            { "NAME": "amount", "TYPE": "float", "DEFAULT": 0.5 },
            { "NAME": "tint", "TYPE": "color", "DEFAULT": [1.0, 0.5, 0.25, 1.0] },
            { "NAME": "centre", "TYPE": "point2D", "DEFAULT": [0.5, 0.5] },
            { "NAME": "invert", "TYPE": "bool", "DEFAULT": false },
            { "NAME": "steps", "TYPE": "long", "DEFAULT": 2, "VALUES": [1, 2, 3], "LABELS": ["one", "two", "three"] }
        ] }*/
        void main() {
            vec4 p = IMG_NORM_PIXEL(inputImage, isf_FragNormCoord);
            vec2 size = IMG_SIZE(inputImage) + _inputImage_imgRect.zw;
            float t = TIME + TIMEDELTA + float(FRAMEINDEX + PASSINDEX + steps) + DATE.w + RENDERSIZE.x + size.x;
            vec4 c = mix(p, tint, amount) + vec4(centre, fract(t), 0.0);
            gl_FragColor = invert ? vec4(1.0) - c : c;
        }
        """;

    private static ProgramSource Source(string name, IsfProgramSource translated) =>
        new(name, "test " + name, ProgramCode.Glsl, Encoding.UTF8.GetBytes(translated.Vertex), Encoding.UTF8.GetBytes(translated.Fragment), default);

    /// <summary>What the translation declares, in its order: the six built-ins, each input that is a uniform (bool and long as ints),
    /// then for each picture its size, rectangle and flip; the pictures are the textures, sampled.</summary>
    private static ProgramLayout Declared(string name, IsfDocument doc)
    {
        static ConstantType Of(string glsl) => glsl switch
        {
            "float" => ConstantType.Float,
            "vec2" => ConstantType.Float2,
            "vec4" => ConstantType.Float4,
            _ => ConstantType.Int,
        };
        var constants = new List<(string, ConstantType, int)>
        {
            ("PASSINDEX", ConstantType.Int, 1), ("RENDERSIZE", ConstantType.Float2, 1), ("TIME", ConstantType.Float, 1),
            ("TIMEDELTA", ConstantType.Float, 1), ("DATE", ConstantType.Float4, 1), ("FRAMEINDEX", ConstantType.Int, 1),
        };
        foreach (var input in doc.Inputs)
            if (IsfTranslator.GlslType(input.Type) is { } t)
                constants.Add((input.Name, Of(t), 1));
        foreach (var img in doc.Images)
        {
            constants.Add(($"_{img}_imgSize", ConstantType.Float2, 1));
            constants.Add(($"_{img}_imgRect", ConstantType.Float4, 1));
            constants.Add(($"_{img}_flip", ConstantType.Int, 1));
        }
        return ProgramLayout.Pack(name, constants, doc.Images.Select(i => (i, true)));
    }

    /// <summary>Whether the device still holds a program of this id (a released user program is none).</summary>
    private static bool Held(GlBackend b, ProgramId id)
    {
        try
        {
            return b.Programs.Layout(id) is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ProgramMade Make(GlBackend b, ProgramSource source)
    {
        var made = b.CreateProgramAsync(source, CancellationToken.None);
        Assert.True(BackendFrames.Until(b, () => made.IsCompleted), $"{source.Name} was never made");
        return made.GetAwaiter().GetResult();
    }

    [Fact]
    public void ATranslatedIsfSourceMadeOnTheDeviceAnswersALayoutEqualToItsUniformsAndSamplers()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var lane = new FxLane(b);
            var doc = IsfParser.Parse(EveryInput);
            Assert.True(doc.Valid, string.Join("; ", doc.Issues));
            var translated = IsfTranslator.Translate(doc, null, lane.Dialect);
            Assert.Empty(translated.Issues);
            var made = Make(b, Source("every input", translated));
            Assert.True(made.Ok, $"{made.Stage}: {made.Words}");
            Assert.Equal(("", ""), (made.Stage, made.Words));
            Assert.Equal(Declared("every input", doc), made.Layout);
            // The device holds the layout for the id it answered, as it holds a product program's.
            Assert.Equal(made.Layout, b.Programs.Layout(made.Id));
            Assert.True(b.ProgramTable.IsUser(made.Id));
            b.ReleaseProgram(made.Id);
            Assert.True(BackendFrames.Until(b, () => !Held(b, made.Id)));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void ACompileErrorAnswersTheStageThatRefusedAndTheIsfLineItMeets()
    {
        const string fragment = """
            /*{ "ISFVSN": "2" }*/
            void main() {
                vec4 c = vec4(isf_FragNormCoord, 0.0, 1.0);
                gl_FragColor = c * notDeclared;
            }
            """;
        const string vertex = """
            /*{ }*/
            void main() {
                isf_vertShaderInit();
                gl_Position.x += alsoNotDeclared;
            }
            """;
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var lane = new FxLane(b);
            var fs = IsfTranslator.Translate(IsfParser.Parse(fragment), null, lane.Dialect);
            var refused = Make(b, Source("broken fragment", fs));
            Assert.False(refused.Ok);
            Assert.Equal("fragment", refused.Stage);
            var issues = fs.LineMap.Read(refused.Words, vertex: refused.Stage == "vertex");
            Assert.NotEmpty(issues);
            Assert.Equal(4, issues[0].Line);

            var vs = IsfTranslator.Translate(IsfParser.Parse("/*{ \"ISFVSN\": \"2\" }*/\nvoid main() { gl_FragColor = vec4(1.0); }\n"), vertex, lane.Dialect);
            var vertexRefused = Make(b, Source("broken vertex", vs));
            Assert.False(vertexRefused.Ok);
            Assert.Equal("vertex", vertexRefused.Stage);
            var vertexIssues = vs.LineMap.Read(vertexRefused.Words, vertex: true);
            Assert.NotEmpty(vertexIssues);
            Assert.Equal(4, vertexIssues[0].Line);

            // The effect's slot says the same line, through the device's make.
            var asset = new FxAsset("fa00000091", "Broken", FxFormat.Isf, "", FxCatalog.ContentHash(fragment, null)) { Source = fragment };
            using var runner = new FxChainRunner(b, lane);
            using var input = FxRig.Picture(c.Backend, W, H, (_, _) => (40, 70, 100));
            long frame = 0;
            FxRig.Run(c.Backend, lane, runner, Project.CreateDefault("Broken") with { FxAssets = [asset] }, FxRig.Chain(FxRig.Slot("isf:fa00000091")),
                input, ref frame);
            var live = FxRig.Live(runner, "fx00000001");
            Assert.Equal(FxSlotState.Faulted, live.State);
            Assert.StartsWith("line 4: ", live.Words, StringComparison.Ordinal);
            c.Device.TakeErrors();
        });
    }

    [Fact]
    public void AProgramAskedToBeReleasedStaysUntilTheLastFrameThatDrewWithItCompleted()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var lane = new FxLane(b);
            var translated = IsfTranslator.Translate(IsfParser.Parse(EveryInput), null, lane.Dialect);
            var made = Make(b, Source("released", translated));
            Assert.True(made.Ok, made.Words);
            using var target = b.CreateTarget(new TargetDesc(W, H, TextureFormat.Rgba8));
            using var picture = FxRig.Picture(c.Backend, W, H, (_, _) => (10, 20, 30));
            void DrawWith(ICommandList list)
            {
                list.BeginPass(target, PassDesc.ClearBlack);
                list.Draw(made.Id, DrawState.Opaque).Texture("inputImage", picture).Set("amount", 0.5f).Fullscreen();
                list.EndPass();
            }
            var signals = b.DirectSignals;
            FenceValue last;
            try
            {
                last = BackendFrames.Run(b, list =>
                {
                    DrawWith(list);
                    b.ReleaseProgram(made.Id);
                    // Asked inside the frame that draws with it: the frame goes on drawing it.
                    DrawWith(list);
                    // The GPU is scripted from here: it has not reached this frame (nor any after it) until the test says so.
                    signals.Holding = true;
                });
                // While the frame that drew with it is not complete, every frame that begins keeps it.
                for (var frames = 0; frames < 6; frames++)
                {
                    BackendFrames.Run(b, _ => { });
                    Assert.False(b.IsComplete(last));
                    Assert.True(Held(b, made.Id), $"released at frame {frames} before the last frame that drew with it completed");
                    Assert.Same(made.Layout, b.Programs.Layout(made.Id));
                }
            }
            finally
            {
                signals.Release();
            }
            // The GPU reaches it: the very next frame to begin lets it go.
            b.WaitFor(last);
            Assert.True(b.IsComplete(last));
            Assert.True(Held(b, made.Id));
            BackendFrames.Run(b, _ => { });
            Assert.False(Held(b, made.Id));
            Assert.Throws<ArgumentException>(() => b.PipelineCache.ProgramOf(made.Id));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void AnEffectsProgramTheLaneLetsGoOfIsReleasedByTheDevice()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var lane = new FxLane(b);
            using var runner = new FxChainRunner(b, lane);
            using var input = FxRig.Picture(c.Backend, W, H, (_, _) => (40, 70, 100));
            var show = Project.CreateDefault("Released");
            long frame = 0;
            FxRig.Run(c.Backend, lane, runner, show, FxRig.Chain(FxRig.Slot("fx.invert")), input, ref frame);
            var kind = FxCatalog.Builtin.Find("fx.invert")!;
            var key = FxCatalog.Builtin.Source(kind.Id)!.Hash;
            var program = lane.Get(key, frame)!;
            Assert.True(b.ProgramTable.IsUser(program.Id) && Held(b, program.Id));
            Assert.Equal(1, lane.Programs);
            // Unused past the lane's bound: let go at a sweep, then released by the device after its last frame.
            for (var i = 0; i < FxLane.UnusedFrames + 120 && Held(b, program.Id); i++)
            {
                var at = ++frame;
                FxRig.Frame(b, at, list =>
                {
                    lane.Collect(at, list);
                    return 0;
                });
            }
            Assert.Equal(0, lane.Programs);
            Assert.False(Held(b, program.Id));
        });
    }

    [Fact]
    public void TheEffectsDrawThroughProgramsTheDeviceMadeSettingOnlyNamesTheirLayoutsHold()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var lane = new FxLane(b);
            using var runner = new FxChainRunner(b, lane);
            using var input = FxRig.Picture(c.Backend, W, H, (_, _) => (40, 100, 200));
            var show = Project.CreateDefault("Through");
            long frame = 0;
            var chain = FxRig.Chain(FxRig.Slot("fx.invert"), FxRig.Slot("fx.blur", id: "fx00000002", mix: 0));
            FxRig.Run(c.Backend, lane, runner, show, chain, input, ref frame);
            var misses = b.ConstantMisses;
            var px = FxRig.Run(c.Backend, lane, runner, show, chain, input, ref frame);
            Pixels.Near((215, 155, 55), Pixels.At(px, W, 10, 10), 1);
            var key = FxCatalog.Builtin.Source("fx.invert")!.Hash;
            var program = lane.Get(key, frame)!;
            Assert.True(b.ProgramTable.IsUser(program.Id));
            Assert.Equal(program.Layout, b.Programs.Layout(program.Id));
            Assert.Equal(misses, b.ConstantMisses);
            Assert.Empty(c.Device.TakeErrors());
        });
    }
}
