using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>One <see cref="DrawState"/> per draw in place of the loose calls: every state maps to exactly one pipeline key for a
/// target, and the GL state read back after each draw of a scripted frame drawn through the backend's list — blend and its
/// factors, depth test and writes, culling and the front face, polygon mode, the program — is what the device's own calls set before
/// the same draw, draw by draw.</summary>
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

    /// <summary>The GL's fixed-function state now, in words, and whether the program in use is the one the step drew.</summary>
    private static unsafe string Read(RenderContext c, string name, uint program)
    {
        var api = c.Device.Gl;
        var blend = api.IsEnabled(EnableCap.Blend);
        var factors = blend
            ? $"{api.GetInteger(GLEnum.BlendSrcRgb)}/{api.GetInteger(GLEnum.BlendDstRgb)}/{api.GetInteger(GLEnum.BlendSrcAlpha)}/{api.GetInteger(GLEnum.BlendDstAlpha)}/{api.GetInteger(GLEnum.BlendEquationRgb)}"
            : "-";
        var depth = api.IsEnabled(EnableCap.DepthTest);
        var func = depth ? api.GetInteger(GLEnum.DepthFunc) : 0;
        var mask = api.GetBoolean(GLEnum.DepthWritemask);
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
        return $"blend {blend} {factors} depth {depth} {func} write {mask} cull {cull} {face} polygon {polygon} program {name} {current == (int)program}";
    }
}
