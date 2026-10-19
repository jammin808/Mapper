using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Gpu;
using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;
using PlayMap.Render.Passes;
using PlayMap.Render.Projection;
using PlayMap.Render.Tests.Wall;
using PlayMap.Render.Video;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>One <see cref="DrawState"/> per draw in place of the loose calls: every state maps to exactly one pipeline key for a
/// target, and the GL state read back after each draw of a scripted frame drawn through the backend's list — blend and its
/// factors, depth test and writes, culling and the front face, polygon mode, the program — is what the device's own calls set before
/// the same draw, draw by draw. The show's own frames hold the same line: every draw the renderer makes in a scripted show (canvases
/// in every blend, the generators of every family with a surface effect drawn flat, a playlist's mix, a video's conversion, a
/// projector with a pixel quad and a mesh, the overlay, the identify card, a delay ring, a blackout and the three kinds of preview)
/// leaves the GL state the loose calls left, draw for draw, as recorded per dialect in <c>DrawStates/frames.&lt;dialect&gt;.txt</c>
/// (written by this test with PLAYMAP_WRITE_FIXTURES=1).</summary>
[Collection("gl")]
public sealed class DrawStateTests(GlFixture gl)
{
    /// <summary>A scripted frame: each step's state, the program it draws and the loose calls the frame code makes for it.</summary>
    private static readonly (DrawState State, string Program)[] Script =
    [
        (DrawState.Opaque, "solid"),
        (new DrawState(BlendMode.Normal, DepthMode.TestOnly, CullMode.Back, Topology.TriangleStrip), "quad"),
        (new DrawState(BlendMode.Add, DepthMode.TestWrite, CullMode.None, Topology.Triangles), "solid"),
        (new DrawState(BlendMode.Premultiplied, DepthMode.Off, CullMode.Back, Topology.Lines), "lines"),
        (new DrawState(BlendMode.Multiply, DepthMode.Off, CullMode.None, Topology.Triangles, Wireframe: true), "solid"),
        (new DrawState(BlendMode.Screen, DepthMode.TestWrite, CullMode.Back, Topology.Triangles), "checker"),
        (new DrawState(BlendMode.AddPremultiplied, DepthMode.TestOnly, CullMode.None, Topology.Triangles), "solid"),
        (DrawState.Opaque, "checker"),
    ];

    [Fact]
    public void EveryDrawStateMapsToExactlyOnePipelineKeyForATarget()
    {
        gl.Run(c =>
        {
            var b = c.Backend;
            using var rgba8 = b.CreateTarget(new TargetDesc(2, 2, TextureFormat.Rgba8, Depth: true));
            using var half = b.CreateTarget(new TargetDesc(2, 2, TextureFormat.Rgba16F));
            var program = b.Programs.Id("solid");
            var states = (from blend in Enum.GetValues<BlendMode>()
                          from depth in Enum.GetValues<DepthMode>()
                          from cull in Enum.GetValues<CullMode>()
                          from topology in Enum.GetValues<Topology>()
                          from wireframe in new[] { false, true }
                          select new DrawState(blend, depth, cull, topology, wireframe)).ToList();
            var keys = states.Select(s => PipelineKey.For(program, s, rgba8)).ToList();
            Assert.Equal(states.Count, keys.Distinct().Count());
            Assert.All(states, s => Assert.Equal(PipelineKey.For(program, s, rgba8), PipelineKey.For(program, s with { }, rgba8)));
            Assert.All(keys, k => Assert.Equal((TextureFormat.Rgba8, (TextureFormat?)null, true), (k.Color0, k.Color1, k.Depth)));
            var other = PipelineKey.For(program, states[0], half);
            Assert.DoesNotContain(other, keys);
            Assert.Equal((TextureFormat.Rgba16F, false), (other.Color0, other.Depth));
            Assert.NotEqual(PipelineKey.For(b.Programs.Id("checker"), states[0], rgba8), keys[0]);
        });
    }

    [Fact]
    public void AfterEachDrawTheGlStateIsWhatTheDevicesOwnCallsSet()
    {
        gl.Run(c =>
        {
            var d = c.Device;
            var b = c.Backend;
            using var target = b.CreateTarget(new TargetDesc(16, 16, TextureFormat.Rgba8, Depth: true));
            using var picture = d.CreateTexture(2, 2, TextureFormat.Rgba8);
            using var buffer = b.CreateBuffer(new BufferDesc(0, BufferUsage.Vertex));
            float[] line = [-1, -1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1];
            b.SetBufferData<float>(buffer, line);
            using var mesh = b.CreateMesh([new VertexAttribute(0, 3, 28, 0), new VertexAttribute(1, 4, 28, 12)], buffer);

            d.InvalidateState();
            d.BindTarget(target);
            d.ClearWithDepth(0, 0, 0, 1);
            var byCalls = new List<string>();
            foreach (var (state, name) in Script)
            {
                var p = d.Program(name);
                d.SetBlend(state.Blend);
                d.SetDepth(state.Depth != DepthMode.Off, state.Depth == DepthMode.TestWrite);
                d.SetCull(state.Cull);
                d.SetWireframe(state.Wireframe);
                switch (name)
                {
                    case "quad":
                        d.BindSampler(p, "uTex", 0, picture);
                        d.DrawGeneratedQuads(p);
                        break;
                    case "lines":
                        d.Draw(p, (Gl.VertexArray)mesh, PrimitiveKind.Lines, 2);
                        break;
                    default:
                        d.DrawFullscreen(p);
                        break;
                }
                byCalls.Add(Read(c, name, p.Id));
            }

            d.InvalidateState();
            var byList = new List<string>();
            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, new PassDesc(LoadOp.Clear, new System.Numerics.Vector4(0, 0, 0, 1)));
                foreach (var (state, name) in Script)
                {
                    var draw = list.Draw(b.Programs.Id(name), state);
                    switch (name)
                    {
                        case "quad":
                            draw.Texture("uTex", picture).GeneratedQuads();
                            break;
                        case "lines":
                            draw.Vertices(mesh, 2);
                            break;
                        default:
                            draw.Fullscreen();
                            break;
                    }
                    byList.Add(Read(c, name, d.Program(name).Id));
                }
                list.EndPass();
            });
            d.InvalidateState();
            Assert.Equal(byCalls, byList);
            Assert.Equal(Script.Length, byList.Distinct().Count());
            Assert.Empty(d.TakeErrors());
        });
    }

    [Fact]
    public void TheShowsFramesLeaveTheStateTheLooseCallsLeftDrawForDraw()
    {
        var path = FramesFile();
        var (project, stage, sources) = Show();
        var lines = gl.Run(c =>
        {
            var d = c.Device;
            var drawn = new List<string>();
            var ring = PreviewRing.PathFor("draws", Guid.NewGuid().ToString("N"));
            using var writer = new PreviewRingWriter(ring, 64, 64);
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var overlay = new OverlayPass(d);
            engine.SetDelay("op1", 2, 0);
            var eye = Pose.LookAt(new Vec3d(3, 2, 4), new Vec3d(0, 0.3, 0), new Vec3d(0, 1, 0));
            var camera = new EditorCamera(Intrinsics.FromFieldOfView(64, 36, 60), eye, Selected: new HashSet<string> { "sf_mesh" });
            d.InvalidateState();
            d.AfterDraw = p => drawn.Add(Read(c, p.Name, p.Id, depthWhereHeld: true) + Viewport(c));
            try
            {
                for (var f = 0; f < 4; f++)
                {
                    drawn.Add(string.Create(CultureInfo.InvariantCulture, $"frame {f + 1}"));
                    var inputs = Scene.Inputs(project, 1 + f / 60.0, sources, stage) with
                    {
                        Identify = f == 1,
                        Blackout = f == 3,
                        Overlay = new OverlayState(true, [], OverlayLayers.Outlines | OverlayLayers.Handles, null, 0, "", 0),
                        Passes = [overlay],
                    };
                    overlay.Prepare(in inputs);
                    engine.Render(inputs);
                    drawn.Add("preview output");
                    engine.Preview(new PreviewRequest("v_out", writer, 32, 18, OutputId: "op1"));
                    drawn.Add("preview editor");
                    engine.Preview(new PreviewRequest("v_editor", writer, 64, 36, Editor: camera));
                    drawn.Add("preview projector");
                    engine.Preview(new PreviewRequest("v_lamp", writer, 40, 30, ProjectorId: "pj1"));
                }
            }
            finally
            {
                d.AfterDraw = null;
            }
            Assert.Empty(engine.GeneratorFaults());
            Assert.Empty(d.TakeErrors());
            return drawn;
        });
        var text = string.Join('\n', lines) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        Assert.True(File.Exists(path), "the frames' draw states are recorded: run this test once with PLAYMAP_WRITE_FIXTURES=1");
        var want = File.ReadAllText(path).Split('\n');
        var got = text.Split('\n');
        for (var i = 0; i < Math.Min(want.Length, got.Length); i++)
            Assert.True(want[i] == got[i], $"draw line {i + 1}: recorded '{want[i]}', drawn '{got[i]}'");
        Assert.Equal(want.Length, got.Length);
        Assert.Contains(got, l => l.StartsWith("blend True", StringComparison.Ordinal));
        Assert.True(got.Count(l => l.StartsWith("blend", StringComparison.Ordinal)) > 100, "the show draws");
    }

    /// <summary>The recorded draws of <see cref="TheShowsFramesLeaveTheStateTheLooseCallsLeftDrawForDraw"/> for this run's dialect.</summary>
    private static string FramesFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above the test assembly");
        return Path.Combine(root, "tests", "PlayMap.Render.Tests", "Rhi", "DrawStates", $"frames.{GlDialects.Word(GlFixture.Dialect)}.txt");
    }

    /// <summary>A show that draws through every path of the frame: two canvases (an opaque first layer, then add, screen, multiply,
    /// a half-transparent colour and a test card; one over its background), every generator family, a playlist mixing two
    /// generators, an NV12 video, a projector lighting a pixel quad and a mesh through a corner pin, a rotated output, a flat
    /// surface effect.</summary>
    private static (Project Project, TestStage Stage, ISourceProvider Sources) Show()
    {
        ImmutableArray<Generator> generators =
        [
            Scene.Gen("pattern.checker", ParamSet.Empty.With("animate", true), "gn_check"),
            Scene.Gen("pattern.motion", ParamSet.Empty.With("fps", true), "gn_motion"),
            Scene.Gen("pattern.colorcycle", ParamSet.Empty.With("fade", true), "gn_cycle"),
            Scene.Gen("pattern.syncmarks", null, "gn_marks"),
            Scene.Gen("fractal.julia", null, "gn_julia"),
            Scene.Gen("reactive.scene", null, "gn_plasma"),
            Scene.Gen("particles.field", ParamSet.Empty.With("count", 300), "gn_snow"),
            Scene.Gen("particles.flow", ParamSet.Empty.With("count", 4096).With("lifeMin", 0.1).With("lifeMax", 0.25), "gn_flow"),
            Scene.Gen("fractal.mandelbulb", ParamSet.Empty.With("renderScale", 0.5), "gn_bulb"),
            Scene.Gen("pattern.testcard", null, "gn_card"),
            Scene.Gen("effect.sweep", null, "gn_sweep"),
        ];
        Layer L(string id, SourceRef source, RectD rect, double opacity, LayerBlend blend) => new(id, id, source, rect, opacity, blend, true);
        var cv1 = new Canvas("cv1", "Canvas", 160, 90, "#102030",
        [
            L("l1", SourceRef.Generator("gn_check"), new RectD(0, 0, 160, 90), 1, LayerBlend.Normal),
            L("l2", SourceRef.Generator("gn_julia"), new RectD(80, 0, 80, 45), 1, LayerBlend.Add),
            L("l3", SourceRef.Generator("gn_snow"), new RectD(0, 45, 80, 45), 0.8, LayerBlend.Screen),
            L("l4", SourceRef.Generator("gn_plasma"), new RectD(80, 45, 80, 45), 1, LayerBlend.Multiply),
            L("l5", SourceRef.Solid("#FF8000"), new RectD(40, 20, 80, 50), 0.5, LayerBlend.Normal),
            L("l6", SourceRef.Generator("gn_card"), new RectD(0, 0, 40, 22), 1, LayerBlend.Normal),
        ]);
        var cv2 = new Canvas("cv2", "Small", 64, 36, "#204060", [L("m1", SourceRef.Solid("#FFFFFF"), new RectD(8, 4, 32, 18), 0.5, LayerBlend.Normal)]);
        var lamp = PixelSurfaceRenderTests.Lamp();
        var quad = PixelSurfaceRenderTests.QuadSurface(PixelSurfaceRenderTests.Keystone(), SourceRef.Generator("gn_flow"));
        var mesh = new Surface("sf_mesh", "Wall", "ms1", Transform3.Identity, [], SurfaceContent.Empty with { Source = SourceRef.Generator("gn_bulb") },
            1, [], true, false, "#20C060", []);
        var pinned = new OutputWarp(true, new CornerPin(new Vec2d(0.04, 0.02), new Vec2d(-0.02, 0.03), new Vec2d(0, 0), new Vec2d(0.01, -0.02)), null);
        Output Source(string id, SourceRef source, int w, int h, int rotation = 0) => Scene.SourceOutput(id, source, w, h, rotation: rotation);
        ImmutableArray<Output> outputs =
        [
            Source("op1", SourceRef.Canvas("cv1"), 160, 90) with { Mode = OutputMode.Canvas, SourceRect = new RectD(0, 0, 160, 90) },
            Source("op2", SourceRef.Generator("gn_motion"), 96, 54, rotation: 90),
            Source("op3", SourceRef.None, 160, 120) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id, Warp = pinned },
            Source("op4", SourceRef.Media("md1"), 64, 36),
            Source("op5", SourceRef.Playlist("pl1"), 64, 36),
            Source("op6", SourceRef.Canvas("cv2"), 64, 36) with { Mode = OutputMode.Canvas, SourceRect = new RectD(0, 0, 64, 36) },
            Source("op7", SourceRef.Generator("gn_marks"), 64, 36),
            Source("op8", SourceRef.Generator("gn_sweep"), 64, 36),
        ];
        var project = Scene.Project(generators, outputs, [cv1, cv2], Stage.Empty with { Surfaces = [quad, mesh], Projectors = [lamp] });
        var stage = new TestStage();
        stage.Meshes["ms1"] = new MeshData([-0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0], [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
            [0, 1, 1, 1, 1, 0, 0, 0], [0, 1, 2, 0, 2, 3]);
        stage.Warps["op3"] = WarpMeshBuilder.Build(pinned);
        return (project, stage, new ShowSources());
    }

    /// <summary>The show's provider: an NV12 picture for the media, and the playlist between the colour cycle and the motion test.</summary>
    private sealed class ShowSources : ISourceProvider
    {
        private const int W = 64;
        private const int H = 36;
        private long _generation;

        public VideoFrameLease? Acquire(SourceRef source, long leaderNs)
        {
            if (source.Kind != SourceKind.Media)
                return null;
            var luma = new byte[W * H];
            Array.Fill(luma, (byte)120);
            var chroma = new byte[W * (H / 2)];
            for (var i = 0; i < chroma.Length; i += 2)
                (chroma[i], chroma[i + 1]) = (90, 170);
            return new ArrayLease(PixelLayout.Nv12, W, H, ++_generation, ColorMatrix.Bt709, ColorRange.Limited, (luma, W, W, H), (chroma, W, W / 2, H / 2));
        }

        public SourceMix? Mix(SourceRef source, long leaderNs) =>
            source.Kind == SourceKind.Playlist ? new SourceMix(SourceRef.Generator("gn_cycle"), SourceRef.Generator("gn_check"), TransitionKind.Fade, 0.4) : null;
    }

    private static unsafe string Viewport(RenderContext c)
    {
        var v = stackalloc int[4];
        c.Device.Gl.GetInteger(GLEnum.Viewport, v);
        return string.Create(CultureInfo.InvariantCulture, $" viewport {v[2]}x{v[3]}");
    }

    /// <summary>The GL's fixed-function state now, in words, and whether the program in use is the one the step drew. With
    /// <paramref name="depthWhereHeld"/> the depth test and writes are read only where the target drawn into has a depth buffer
    /// ("depth none" elsewhere: there they touch no pixel, whatever they are).</summary>
    private static unsafe string Read(RenderContext c, string name, uint program, bool depthWhereHeld = false)
    {
        var api = c.Device.Gl;
        var blend = api.IsEnabled(EnableCap.Blend);
        var factors = blend
            ? $"{api.GetInteger(GLEnum.BlendSrcRgb)}/{api.GetInteger(GLEnum.BlendDstRgb)}/{api.GetInteger(GLEnum.BlendSrcAlpha)}/{api.GetInteger(GLEnum.BlendDstAlpha)}/{api.GetInteger(GLEnum.BlendEquationRgb)}"
            : "-";
        var depth = api.IsEnabled(EnableCap.DepthTest);
        var func = depth ? api.GetInteger(GLEnum.DepthFunc) : 0;
        var mask = api.GetBoolean(GLEnum.DepthWritemask);
        var held = 0;
        if (depthWhereHeld)
            api.GetFramebufferAttachmentParameter(GLEnum.DrawFramebuffer, GLEnum.DepthAttachment, GLEnum.FramebufferAttachmentObjectType, &held);
        var depthWords = !depthWhereHeld || held != 0 ? $"depth {depth} {func} write {mask}" : "depth none";
        var cull = api.IsEnabled(EnableCap.CullFace);
        var face = cull ? $"{api.GetInteger(GLEnum.CullFaceMode)}/{api.GetInteger(GLEnum.FrontFace)}" : "-";
        var polygon = "-";
        if (c.Device.Dialect == GlDialect.Core330)
        {
            var modes = stackalloc int[2];
            api.GetInteger(GLEnum.PolygonMode, modes);
            polygon = modes[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var current = api.GetInteger(GLEnum.CurrentProgram);
        return $"blend {blend} {factors} {depthWords} cull {cull} {face} polygon {polygon} program {name} {current == (int)program}";
    }
}
