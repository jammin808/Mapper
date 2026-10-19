using System.Globalization;
using System.Numerics;
using System.Text;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Gl;
using PlayMap.Render.Shaders;
using PlayMap.Tests;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The GL backend behind the render hardware interface, on the fixture's context in either dialect: the device it says it
/// is; draws through its immediate list that read byte for byte what the device's own calls draw, every constant type reaching the
/// program as those calls set it and an unset constant reading zero; fences that grow and complete; the read-back, upload and timer
/// rings; the importer's words; the pipeline warm-up and the render thread's count of what it made; user programs made on the
/// compile lane, reflected into their layout, drawn and released after their last frame, and a refusal naming its stage; and a warm
/// frame through the list that allocates nothing.</summary>
[Collection("gpu")]
public sealed class GlBackendTests(BackendFixture backends)
{
    private const string FullscreenVs = """
        out vec2 vUv;
        void main() {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string UserFs = """
        in vec2 vUv;
        uniform vec4 uTint;
        uniform float uGain;
        uniform vec2 uOffset;
        uniform int uMode;
        uniform bool uFlip;
        uniform vec4 uPalette[3];
        uniform sampler2D uPicture;
        out vec4 oColor;
        void main() {
            vec4 p = texture(uPicture, vUv + uOffset);
            vec4 c = uMode == 1 ? uPalette[2] : uTint;
            if (uFlip) c = c.bgra;
            oColor = vec4(c.rgb * uGain + p.rgb * 0.0039, 1.0);
        }
        """;

    [Fact]
    public void TheDeviceSaysWhatItIsAndThePlatformsDeviceIsIt()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            Assert.Same(b, c.Device.Backend);
            Assert.Equal(GpuBackendKind.Gl, b.Kind);
            Assert.Equal(1, b.Epoch);
            Assert.Null(b.Exporter);
            Assert.Equal(c.Device.Renderer, b.Adapter.Name);
            Assert.Equal(c.Device.Version, b.Adapter.Driver);
            Assert.Equal(c.Device.Renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase), b.Adapter.Software);
            // The API in the words a renderer's name leads with ("OpenGL 3.3 llvmpipe …", "OpenGL ES 3.0 …").
            var version = string.Create(CultureInfo.InvariantCulture, $"{c.Device.GlVersion / 10}.{c.Device.GlVersion % 10}");
            Assert.Equal(c.Device.Dialect == GlDialect.Es300 ? "OpenGL ES " + version : "OpenGL " + version, b.Adapter.Api);
            Assert.Equal(ShaderLibrary.Names, b.Programs.Names);
            var caps = c.Device.Caps;
            foreach (var f in Enum.GetValues<TextureFormat>())
                Assert.Equal(new FormatSupport(caps.Supports(f), caps.SupportsRenderTarget(f), caps.SupportsRenderTarget(f, blended: true)), b.Features.Formats[f]);
            Assert.Equal(new FormatSupport(true, true, true), b.Features.Formats[TextureFormat.Rgba8]);
            Assert.Equal(c.Device.BlockFormats, b.Features.Blocks);
            Assert.Equal(caps.IsCore, b.Features.Timers);
            Assert.False(b.Features.SharedHandles);
            Assert.Equal(b.Features.Timers ? GlTimers.SlotCount : 0, b.Timers.Slots);
            Assert.StartsWith("0 · " + c.Device.Renderer, b.Pipelines.Key, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheListDrawsWhatTheDevicesOwnCallsDraw()
    {
        backends.RunGl(c =>
        {
            var d = c.Device;
            var b = c.Backend;
            using var target = b.CreateTarget(new TargetDesc(48, 24, TextureFormat.Rgba8, Depth: true));
            using var lineBuffer = b.CreateBuffer(new BufferDesc(0, BufferUsage.Vertex));
            float[] lines = [-0.8f, -0.5f, 0.2f, 1, 0.5f, 0, 1, 0.8f, 0.6f, 0.2f, 0, 1, 0.5f, 1, -0.6f, 0.7f, 0.5f, 0.2f, 0.3f, 1, 1, 0.7f, -0.7f, 0.5f, 1, 1, 1, 1];
            b.SetBufferData<float>(lineBuffer, lines);
            using var mesh = b.CreateMesh([new VertexAttribute(0, 3, 28, 0), new VertexAttribute(1, 4, 28, 12)], lineBuffer);
            var view = Matrix4x4.CreateRotationZ(0.3f) * Matrix4x4.CreateScale(0.9f, 0.8f, 1);
            var colors = new (float R, float G, float B)[] { (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 0) };

            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, new PassDesc(LoadOp.Clear, new Vector4(0.05f, 0.1f, 0.15f, 1)));
                list.Draw(b.Programs.Id("colorcycle"), DrawState.Opaque)
                    .Set("uColors[0]", colors[0].R, colors[0].G, colors[0].B)
                    .Set("uColors[1]", colors[1].R, colors[1].G, colors[1].B)
                    .Set("uColors[2]", colors[2].R, colors[2].G, colors[2].B)
                    .Set("uColors[3]", colors[3].R, colors[3].G, colors[3].B)
                    .SetInt("uCount", 4)
                    .Set("uTime", 2.5f)
                    .Set("uInterval", 1f)
                    .SetInt("uFade", 1)
                    .Fullscreen();
                list.Draw(b.Programs.Id("lines"), new DrawState(BlendMode.Opaque, DepthMode.TestOnly, CullMode.None, Topology.Lines))
                    .Set("uViewProj", view)
                    .Vertices(mesh, 4);
                list.EndPass();
            });
            var listed = Pixels.Read(b, target);

            d.BindTarget(target);
            d.SetBlend(BlendMode.Opaque);
            d.SetDepth(false, false);
            d.SetCull(CullMode.None);
            d.ClearWithDepth(0.05f, 0.1f, 0.15f, 1);
            var cycle = d.Program("colorcycle");
            for (var i = 0; i < colors.Length; i++)
                cycle.Set($"uColors[{i}]", colors[i].R, colors[i].G, colors[i].B);
            cycle.SetInt("uCount", 4);
            cycle.Set("uTime", 2.5f);
            cycle.Set("uInterval", 1f);
            cycle.SetInt("uFade", 1);
            d.DrawFullscreen(cycle);
            var linesProgram = d.Program("lines");
            linesProgram.Set("uViewProj", view);
            d.SetDepth(true, false);
            d.Draw(linesProgram, (Gl.VertexArray)mesh, PrimitiveKind.Lines, 4);
            d.SetDepth(false, false);
            var called = Pixels.Read(d.Backend, target);

            Assert.Equal(called, listed);
            Assert.True(listed.Distinct().Count() > 3);
            Assert.Empty(d.TakeErrors());
        });
    }

    [Fact]
    public void AConstantTheDrawDidNotSetReadsZeroAndTheDevicesCallsStillSetIt()
    {
        backends.RunGl(c =>
        {
            var d = c.Device;
            var b = c.Backend;
            using var target = d.CreateRenderTarget(4, 4, TextureFormat.Rgba8);
            var solid = d.Program("solid");
            d.BindTarget(target);
            d.SetBlend(BlendMode.Opaque);
            solid.Set("uColor", 1, 0, 0, 1);
            d.DrawFullscreen(solid);
            Assert.Equal((255, 0, 0), Pixels.At(Pixels.Read(d.Backend, target), 4, 1, 1));

            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, PassDesc.Keep);
                list.Draw(b.Programs.Id("solid"), DrawState.Opaque).Fullscreen();
                list.EndPass();
            });
            Assert.All(Pixels.Read(b, target), v => Assert.Equal(0, v));

            // The program remembers what the list set: the device's own call sets red again, and is drawn red.
            d.BindTarget(target);
            solid.Set("uColor", 1, 0, 0, 1);
            d.DrawFullscreen(solid);
            Assert.Equal((255, 0, 0), Pixels.At(Pixels.Read(d.Backend, target), 4, 1, 1));
            Assert.Empty(d.TakeErrors());
        });
    }

    [Fact]
    public unsafe void EveryConstantTypeReachesTheProgramAsTheDevicesCallsSetIt()
    {
        backends.RunGl(c =>
        {
            var d = c.Device;
            var b = c.Backend;
            var api = d.Gl;
            using var target = d.CreateRenderTarget(4, 4, TextureFormat.Rgba8, depth: true);
            var model = Matrix4x4.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f) * Matrix4x4.CreateTranslation(1, 2, 3);
            var normal = new Matrix3x3(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f);
            var stage = d.Program("stage");

            stage.Set("uModel", model);
            stage.Set("uNormalMat", normal);
            stage.SetInt("uMode", 3);
            stage.Set("uOpacity", 0.25f);
            stage.Set("uColorA", 0.1f, 0.2f, 0.3f, 0.4f);
            stage.Set("uBoundsMin", -1, -2, -3);
            var set = Uniforms(api, stage);

            // Through the list: zeros first (every constant is set each draw), then the same values as the device's calls.
            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, PassDesc.Keep);
                list.Draw(b.Programs.Id("stage"), DrawState.Opaque).Fullscreen();
                list.EndPass();
            });
            Assert.All(Uniforms(api, stage), v => Assert.Equal(0f, v));
            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, PassDesc.Keep);
                list.Draw(b.Programs.Id("stage"), DrawState.Opaque)
                    .Set("uModel", model)
                    .Set("uNormalMat", normal)
                    .SetInt("uMode", 3)
                    .Set("uOpacity", 0.25f)
                    .Set("uColorA", 0.1f, 0.2f, 0.3f, 0.4f)
                    .Set("uBoundsMin", -1, -2, -3)
                    .Fullscreen();
                list.EndPass();
            });
            Assert.Equal(set, Uniforms(api, stage));
            Assert.Contains(0.9f, set);
            Assert.Contains(3f, set);
            Assert.Empty(d.TakeErrors());
        });
    }

    /// <summary>The values a program holds for the constants the test sets, read back from the GL.</summary>
    private static unsafe float[] Uniforms(GL api, ShaderProgram program)
    {
        var values = new List<float>();
        var buffer = stackalloc float[16];
        foreach (var (name, count) in new[] { ("uModel", 16), ("uNormalMat", 9), ("uOpacity", 1), ("uColorA", 4), ("uBoundsMin", 3) })
        {
            var location = program.Location(name);
            Assert.True(location >= 0, $"{name} is active");
            api.GetUniform(program.Id, location, buffer);
            for (var i = 0; i < count; i++)
                values.Add(buffer[i]);
        }
        int mode;
        api.GetUniform(program.Id, program.Location("uMode"), &mode);
        values.Add(mode);
        return [.. values];
    }

    [Fact]
    public void FencesGrowPerQueueAndCompleteOnceTheGpuPassedThem()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            var first = BackendFrames.Run(b, _ => { });
            var second = BackendFrames.Run(b, _ => { });
            Assert.Equal(QueueKind.Direct, first.Queue);
            Assert.Equal(first.Value + 1, second.Value);
            b.WaitFor(second);
            Assert.True(b.IsComplete(first));
            Assert.True(b.IsComplete(second));
            Assert.True(b.IsComplete(FenceValue.None));
            Assert.False(b.IsComplete(new FenceValue(QueueKind.Direct, second.Value + 1)));
            Assert.Throws<InvalidOperationException>(() => b.WaitFor(new FenceValue(QueueKind.Direct, second.Value + 1)));

            var list = b.BeginFrame(BackendFrames.Next);
            Assert.Throws<InvalidOperationException>(() => b.BeginFrame(BackendFrames.Next));
            Assert.Throws<InvalidOperationException>(() => list.Draw(b.Programs.Id("solid"), DrawState.Opaque));
            using var target = c.Device.CreateRenderTarget(2, 2, TextureFormat.Rgba8);
            list.BeginPass(target, PassDesc.Keep);
            Assert.Throws<InvalidOperationException>(() => b.Submit(list));
            list.EndPass();
            var third = b.Submit(list);
            Assert.Equal(second.Value + 1, third.Value);
            Assert.Throws<InvalidOperationException>(() => b.Submit(list));
            b.WaitFor(third);
        });
    }

    [Fact]
    public void TheReadbackRingAnswersWhatAReadNowReads()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var target = b.CreateTarget(new TargetDesc(8, 4, TextureFormat.Rgba8));
            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, new PassDesc(LoadOp.Clear, new Vector4(0.2f, 0.4f, 0.6f, 1)));
                list.Draw(b.Programs.Id("checker"), DrawState.Opaque)
                    .Set("uCell", 2f).Set("uColorA", 1, 1, 0, 1).Set("uColorB", 0, 0, 1, 1).Fullscreen();
                list.EndPass();
            });
            var now = Pixels.Read(b, target);
            if (!c.Device.HasReadBgra)
            {
                Assert.Throws<NotSupportedException>(() => b.Readbacks.Request(target, 7));
                return;
            }
            Assert.True(b.Readbacks.Request(target, 7));
            Assert.Equal(1, b.Readbacks.Pending);
            var done = BackendFrames.Run(b, _ => { });
            b.WaitFor(done);
            byte[]? read = null;
            long stamp = -1;
            Assert.True(b.Readbacks.TryCollect(0, (int _, ReadOnlySpan<byte> bgra, int w, int h, long s) =>
            {
                read = bgra.ToArray();
                stamp = s;
                Assert.Equal((8, 4), (w, h));
            }));
            Assert.Equal(now, read);
            Assert.Equal(7, stamp);
            Assert.Equal(0, b.Readbacks.Pending);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public unsafe void TheUploadRingFillsATextureAndItsCopyValueCompletes()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var picture = b.CreateTexture(new TextureDesc(8, 4, TextureFormat.Rgba8, TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDest));
            using var target = b.CreateTarget(new TargetDesc(8, 4, TextureFormat.Rgba8));
            // Rows 40 bytes apart, 32 of them pixels: the ring packs them tight.
            var source = new byte[40 * 4];
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 32; x++)
                    source[y * 40 + x] = (byte)(y * 50 + x * 7);
            FenceValue done;
            fixed (byte* p = source)
                Assert.True(b.Uploads.TryUpload(picture, (nint)p, 40, 32, 4, UploadFormat.Rgba8, out done));
            Assert.Equal(QueueKind.Copy, done.Queue);
            BackendFrames.Run(b, list => list.Copy(picture, target.Color));
            b.WaitFor(done);
            Assert.True(b.IsComplete(done));
            var read = Pixels.Read(b, target);
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var i = (y * 8 + x) * 4;
                    var s = y * 40 + x * 4;
                    // RGBA uploaded, BGRA read.
                    Assert.Equal((source[s], source[s + 1], source[s + 2], source[s + 3]), (read[i + 2], read[i + 1], read[i], read[i + 3]));
                }
            }
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void TimestampsAreReadOnceTheFrameCompletedAndNothingIsMeasuredWithoutThem()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var target = b.CreateTarget(new TargetDesc(64, 64, TextureFormat.Rgba8));
            var frame = BackendFrames.Next;
            var done = BackendFrames.Run(b, list =>
            {
                list.Timestamp(0);
                list.BeginPass(target, PassDesc.ClearBlack);
                list.Draw(b.Programs.Id("checker"), DrawState.Opaque).Set("uCell", 3f).Fullscreen();
                list.EndPass();
                list.Timestamp(1);
            });
            Assert.Throws<ArgumentOutOfRangeException>(() => b.GlTimers.Stamp(frame, GlTimers.SlotCount));
            b.WaitFor(done);
            if (!b.Features.Timers)
            {
                Assert.Equal(0, b.Timers.Slots);
                Assert.False(b.Timers.TryElapsedUs(frame, 0, 1, out _));
                return;
            }
            Assert.True(b.Timers.TryElapsedUs(frame, 0, 1, out var us));
            Assert.True(us >= 0, $"{us} µs");
            Assert.False(b.Timers.TryElapsedUs(frame, 0, 2, out _));
            Assert.False(b.Timers.TryElapsedUs(frame + 1000, 0, 1, out _));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void TheImporterRefusesWhatItCannotImportWithWords()
    {
        backends.RunGl(c =>
        {
            var importer = c.Backend.Importer;
            var surface = new GpuSurface { Api = GpuApi.OpenGl, Width = 16, Height = 16 };
            Assert.False(importer.CanImport(surface, out var words));
            Assert.Equal("this renderer imports no OpenGl frames", words);
            Assert.False(importer.TryImport(surface, out _, out var tried));
            Assert.Equal(words, tried);
            var dmabuf = new GpuSurface { Api = GpuApi.DmaBuf, Width = 16, Height = 16 };
            var expected = DmaBufImporter.ForCurrentContext(c.Device, out var why);
            using (expected)
            {
                Assert.Equal(expected is not null, importer.CanImport(dmabuf, out var dmabufWords));
                Assert.Equal(expected is null ? why : null, dmabufWords);
            }
        });
    }

    [Fact]
    public async Task AWarmUpMakesItsPipelinesBeforeTheyAreDrawnAndADrawCountsWhatItHadToMake()
    {
        var (keys, made) = backends.RunGl(c =>
        {
            var b = c.Backend;
            var program = b.Programs.Id("syncmarks");
            PipelineKey[] k =
            [
                new(program, new DrawState(BlendMode.Screen, DepthMode.Off, CullMode.Back, Topology.Triangles), TextureFormat.Rgba16F, null, false),
                new(program, new DrawState(BlendMode.Add, DepthMode.TestOnly, CullMode.None, Topology.Triangles), TextureFormat.Rgba16F, null, true),
            ];
            return (k, b.Pipelines.WarmAsync(k, CancellationToken.None));
        });
        Assert.True(backends.RunGl(c => BackendFrames.Until(c.Backend, () => made.IsCompleted)));
        var result = await made;
        Assert.Equal(2, result.Asked);
        Assert.Equal(2, result.Made + result.FromCache);
        Assert.Empty(result.Failed);
        backends.RunGl(c =>
        {
            var b = c.Backend;
            Assert.All(keys, k => Assert.True(b.Pipelines.TryGet(k, out var p) && p.Key == k));
            using var target = b.CreateTarget(new TargetDesc(4, 4, TextureFormat.Rgba16F, Depth: true));
            var before = b.Pipelines.RenderThreadMakes;
            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, PassDesc.ClearBlack);
                list.Draw(keys[1].Program, keys[1].State).Fullscreen();
                list.EndPass();
            });
            Assert.Equal(before, b.Pipelines.RenderThreadMakes);
            var unmade = new DrawState(BlendMode.Multiply, DepthMode.TestWrite, CullMode.Back, Topology.Triangles, Wireframe: true);
            var key = PipelineKey.For(keys[1].Program, unmade, target);
            Assert.False(b.Pipelines.TryGet(key, out _));
            BackendFrames.Run(b, list =>
            {
                list.BeginPass(target, PassDesc.Keep);
                list.Draw(keys[1].Program, unmade).Fullscreen();
                list.EndPass();
            });
            Assert.Equal(before + 1, b.Pipelines.RenderThreadMakes);
            Assert.True(b.Pipelines.TryGet(key, out _));
            c.Device.SetWireframe(false);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void AWarmUpLinksItsProgramsOnTheLanesOwnContextSoTheirDrawsMakeNothing()
    {
        // A context of the test's own, so the programs are not linked yet whatever ran before.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var c = RenderContext.CreateHidden(new GlDeviceOptions(CheckErrors: true, DebugOutput: false, Dialect: GlLeg.Dialect));
                var b = c.Backend;
                using var target = b.CreateTarget(new TargetDesc(8, 8, TextureFormat.Rgba8));
                string[] names = ["checker", "motion", "syncmarks"];
                var keys = names.Select(n => PipelineKey.For(b.Programs.Id(n), DrawState.Opaque, target)).ToArray();
                Assert.All(names, n => Assert.False(c.Device.HasProgram(n)));
                var warm = b.Pipelines.WarmAsync(keys, CancellationToken.None);
                var frames = 0;
                while (!warm.IsCompleted && frames++ < 2000)
                {
                    BackendFrames.Run(b, _ => { });
                    Thread.Sleep(1);
                }
                Assert.True(warm.IsCompletedSuccessfully);
                Assert.Equal("shared-context", b.CompileLane!.Mode);
                var result = warm.Result;
                Assert.Equal((3, 3, 0), (result.Asked, result.Made, result.FromCache));
                Assert.Empty(result.Failed);
                Assert.All(names, n => Assert.True(c.Device.HasProgram(n)));
                BackendFrames.Run(b, list =>
                {
                    list.BeginPass(target, PassDesc.ClearBlack);
                    foreach (var k in keys)
                        list.Draw(k.Program, k.State).Fullscreen();
                    list.EndPass();
                });
                Assert.Equal(0, b.Pipelines.RenderThreadMakes);
                Assert.Empty(c.Device.TakeErrors());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "warm-up lane test" };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("the warm-up on the lane failed", failure);
    }

    [LinuxFact]
    [Trait("Category", "Linux")]
    public void WithNoSecondContextAWarmUpLinksOneProgramAFrameOnTheRenderThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var c = RenderContext.CreateSurfaceless(new GlDeviceOptions(CheckErrors: true, DebugOutput: false, Dialect: GlLeg.Dialect));
                var b = c.Backend;
                using var target = b.CreateTarget(new TargetDesc(8, 8, TextureFormat.Rgba8));
                string[] names = ["checker", "motion", "syncmarks"];
                var keys = names.Select(n => PipelineKey.For(b.Programs.Id(n), DrawState.Opaque, target)).ToArray();
                var warm = b.Pipelines.WarmAsync(keys, CancellationToken.None);
                var linked = new List<int>();
                for (var i = 0; i < 12 && !warm.IsCompleted; i++)
                {
                    BackendFrames.Run(b, _ => { });
                    linked.Add(names.Count(c.Device.HasProgram));
                }
                Assert.True(warm.IsCompletedSuccessfully);
                Assert.Equal("render-thread", b.CompileLane!.Mode);
                // Never two links in one frame.
                Assert.All(linked.Zip(linked.Skip(1)), p => Assert.InRange(p.Second - p.First, 0, 1));
                Assert.Equal(3, linked[^1]);
                Assert.Equal(3, warm.Result.Made);
                Assert.Empty(c.Device.TakeErrors());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "warm-up render-thread test" };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("the warm-up on the render thread failed", failure);
    }

    [Fact]
    public async Task AUserProgramIsMadeOnTheLaneReflectedDrawnAndReleasedAfterItsLastFrame()
    {
        var making = backends.RunGl(c => c.Backend.CreateProgramAsync(Glsl("user tint", FullscreenVs, UserFs), CancellationToken.None));
        Assert.True(backends.RunGl(c => BackendFrames.Until(c.Backend, () => making.IsCompleted)));
        var made = await making;
        Assert.True(made.Ok, made.Stage + ": " + made.Words);
        Assert.Equal(ProgramLayout.Pack("user tint",
            [("uTint", ConstantType.Float4, 1), ("uGain", ConstantType.Float, 1), ("uOffset", ConstantType.Float2, 1), ("uMode", ConstantType.Int, 1),
                ("uFlip", ConstantType.Int, 1), ("uPalette", ConstantType.Float4, 3)],
            [("uPicture", true)]), made.Layout);
        backends.RunGl(c =>
        {
            var b = c.Backend;
            Assert.Equal(made.Layout, b.Programs.Layout(made.Id));
            Assert.True(made.Id.Value >= b.Programs.Names.Count);
            using var target = b.CreateTarget(new TargetDesc(4, 4, TextureFormat.Rgba8));
            using var black = b.CreateTexture(new TextureDesc(1, 1, TextureFormat.Rgba8, TextureUsage.Sampled | TextureUsage.CopyDest));
            BackendFrames.Run(b, list =>
            {
                list.Upload(black, [0, 0, 0, 255], UploadFormat.Rgba8);
                list.BeginPass(target, PassDesc.ClearBlack);
                list.Draw(made.Id, DrawState.Opaque)
                    .Set("uTint", 0.2f, 0.4f, 0.6f, 1)
                    .Set("uGain", 1f)
                    .SetInt("uMode", 1)
                    .Set("uPalette[2]", 0.8f, 0.6f, 0.4f, 1)
                    .Texture("uPicture", black)
                    .Fullscreen();
                list.EndPass();
            });
            Pixels.Near((204, 153, 102), Pixels.At(Pixels.Read(b, target), 4, 2, 2), 1, "the palette's third colour");
            var pipelines = b.Pipelines.Count;

            b.ReleaseProgram(made.Id);
            Assert.True(BackendFrames.Until(b, () => !Holds(b, made.Id)));
            Assert.Equal(pipelines - 1, b.Pipelines.Count);
            Assert.Throws<ArgumentException>(() => b.ReleaseProgram(b.Programs.Id("solid")));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    private static bool Holds(IGpuDevice device, ProgramId id)
    {
        try
        {
            device.Programs.Layout(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("fragment", FullscreenVs, "out vec4 oColor;\nvoid main() { oColor = vec4(1.0; }")]
    [InlineData("vertex", "void main() { gl_Position = vec4(0.0) }", "out vec4 oColor;\nvoid main() { oColor = vec4(1.0); }")]
    [InlineData("link", "uniform float uX;\nvoid main() { gl_Position = vec4(uX); }", "uniform vec2 uX;\nout vec4 oColor;\nvoid main() { oColor = vec4(uX, 0.0, 1.0); }")]
    [InlineData("source", "#version 330 core\nvoid main() { gl_Position = vec4(0.0); }", "out vec4 oColor;\nvoid main() { oColor = vec4(1.0); }")]
    public async Task AUserProgramThatIsNotMadeNamesTheStageThatRefusedIt(string stage, string vertex, string fragment)
    {
        var making = backends.RunGl(c => c.Backend.CreateProgramAsync(Glsl("broken " + stage, vertex, fragment), CancellationToken.None));
        Assert.True(backends.RunGl(c => BackendFrames.Until(c.Backend, () => making.IsCompleted)));
        var made = await making;
        Assert.False(made.Ok);
        Assert.Equal(stage, made.Stage);
        Assert.Null(made.Layout);
        Assert.NotEmpty(made.Words);
    }

    [Fact]
    public async Task ASourceTheGlBackendCannotReadIsRefusedAtOnce()
    {
        var (dxil, garbled) = backends.RunGl(c => (
            c.Backend.CreateProgramAsync(new ProgramSource("dxil", "k", ProgramCode.Dxil, new byte[] { 1 }, new byte[] { 2 }, default), CancellationToken.None),
            c.Backend.CreateProgramAsync(new ProgramSource("garbled", "k", ProgramCode.Glsl, new byte[] { 0xC3, 0x28 }, new byte[] { 0x41 }, default), CancellationToken.None)));
        Assert.True(dxil.IsCompleted);
        Assert.True(garbled.IsCompleted);
        Assert.Equal(("code", false), ((await dxil).Stage, (await dxil).Ok));
        Assert.Equal(("code", false), ((await garbled).Stage, (await garbled).Ok));
    }

    [Fact]
    public void AWarmFrameThroughTheListAllocatesNothing()
    {
        backends.RunGl(c =>
        {
            var b = c.Backend;
            using var target = b.CreateTarget(new TargetDesc(32, 32, TextureFormat.Rgba8));
            using var picture = b.CreateTexture(new TextureDesc(4, 4, TextureFormat.Rgba8, TextureUsage.Sampled | TextureUsage.CopyDest));
            var solid = b.Programs.Id("solid");
            var quad = b.Programs.Id("quad");
            var blend = new DrawState(BlendMode.Normal, DepthMode.Off, CullMode.None, Topology.TriangleStrip);
            for (var i = 0; i < 3; i++)
                Frame(b, target, picture, solid, quad, blend, i);
            var start = Allocations.Start();
            for (var i = 0; i < 30; i++)
                Frame(b, target, picture, solid, quad, blend, i);
            Assert.Equal(0, Allocations.Since(start));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    private static void Frame(GlBackend b, RenderTarget target, GpuTexture picture, ProgramId solid, ProgramId quad, in DrawState blend, int i)
    {
        var list = b.BeginFrame(BackendFrames.Next + i);
        list.BeginPass(target, PassDesc.ClearBlack);
        list.Draw(solid, DrawState.Opaque).Set("uColor", 0.1f * (i % 7), 0.2f, 0.3f, 1).Fullscreen();
        list.Draw(quad, blend)
            .Set("uRect", 4, 4, 16, 16)
            .Set("uTarget", 32, 32)
            .Set("uUv", 0, 0, 1, 1)
            .SetInt("uMode", 0)
            .Set("uOpacity", 0.5f)
            .Set("uTint", 1, 1, 1, 1)
            .Texture("uTex", picture)
            .GeneratedQuads();
        list.EndPass();
        b.Submit(list);
    }

    private static ProgramSource Glsl(string name, string vertex, string fragment) =>
        new(name, name, ProgramCode.Glsl, Encoding.UTF8.GetBytes(vertex), Encoding.UTF8.GetBytes(fragment), default);
}
