using System.Numerics;
using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Projection;

/// <summary>The desk's free view of the stage: an orbit camera's optics and pose, whether surfaces show
/// their content or a flat tint of their colour, and what is selected.</summary>
public sealed record EditorCamera(
    Intrinsics Intrinsics,
    Pose Pose,
    bool ShowContent = false,
    IReadOnlySet<string>? Selected = null,
    bool ShowFrusta = true,
    bool ShowFloor = true);

/// <summary>Draws the stage — each visible surface's mesh, placed by its transform, lit by its content — from
/// a camera: a projector (the projector pass, what that lamp must emit) or the editor's eye.
///
/// The projection is <see cref="CameraModel.GlMatrices"/> with one more mirror in y, because targets are
/// drawn top row first; the mirror turns the winding over, which the device's cull state accounts for. Depth
/// testing is on. A surface's content is sampled through its UVs (with the region's offset and scale), from
/// a canvas region, or projected from a viewpoint; a surface-aware effect is drawn from the surface's own
/// world position, normal and UVs. The surface mask (UV space) and the opacity scale the light.</summary>
internal sealed class StageRenderer : IDisposable
{
    private const int LineFloats = 7;
    private static readonly VertexAttribute[] LineLayout =
    [
        new(0, 3, LineFloats * 4, 0),
        new(1, 4, LineFloats * 4, 12),
    ];

    private static readonly Matrix4x4 FlipY = new(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    private readonly RenderServices _services;
    private readonly GpuCaches _caches;
    private readonly GpuBuffer _lineBuffer;
    private readonly VertexArray _lineVao;
    private float[] _lines = new float[LineFloats * 512];
    private int _lineCount;

    public StageRenderer(RenderServices services, GpuCaches caches)
    {
        _services = services;
        _caches = caches;
        _lineBuffer = services.Device.CreateBuffer(BufferKind.Vertex);
        services.Device.SetBufferData<float>(_lineBuffer, new float[LineFloats * 2]);
        _lineVao = services.Device.CreateVertexArray(LineLayout, _lineBuffer);
    }

    /// <summary>The view-projection a camera sees the world with, for a target drawn top row first.</summary>
    public static Matrix4x4 ViewProjection(Intrinsics k, Pose pose, double near, double far)
    {
        var (view, proj) = CameraModel.GlMatrices(k, pose, near, far);
        return view * proj * FlipY;
    }

    /// <summary>The surfaces' world bounds (visible ones with a loaded mesh; the mesh bounds come from the GPU
    /// copy's, computed once at upload); false when there are none.</summary>
    public bool WorldBounds(Project project, IStageResources resources, out Vec3d min, out Vec3d max)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(resources);
        min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        var any = false;
        foreach (var s in project.Stage.Surfaces)
        {
            if (!s.Visible || resources.Mesh(s.MeshId) is not { } mesh || mesh.VertexCount == 0)
                continue;
            var gpu = _caches.Mesh(mesh, s.Faces);
            var (lo, hi) = (gpu.Min, gpu.Max);
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vec3d((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
                var w = s.Transform.Apply(corner);
                min = Vec3d.Min(min, w);
                max = Vec3d.Max(max, w);
            }
            any = true;
        }
        return any;
    }

    /// <summary>Near and far planes that hold the stage for a camera (with margin), or a general default.</summary>
    public static (double Near, double Far) Planes(Pose pose, bool haveBounds, Vec3d min, Vec3d max)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!haveBounds)
            return (0.02, 1000);
        var lo = double.MaxValue;
        var hi = 0.0;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vec3d((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var z = pose.WorldToCamera(corner).Z;
            lo = Math.Min(lo, z);
            hi = Math.Max(hi, z);
        }
        var near = Math.Clamp(lo * 0.5, 0.01, 10);
        var far = Math.Max(near * 10, hi * 1.5 + 1);
        return (near, far);
    }

    /// <summary>The projector pass: the stage as the projector must emit it, into a target with depth.</summary>
    public void RenderProjector(RenderTarget target, Project project, Projector projector, IStageResources resources, SourceResolver sources,
        in GeneratorFrame frame, GeneratorContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(projector);
        var k = projector.Intrinsics.Width == target.Width && projector.Intrinsics.Height == target.Height
            ? projector.Intrinsics
            : projector.Intrinsics.Scaled(target.Width, target.Height);
        var haveBounds = WorldBounds(project, resources, out var min, out var max);
        var (near, far) = Planes(projector.Pose, haveBounds, min, max);
        var viewProj = ViewProjection(k, projector.Pose, near, far);
        var d = _services.Device;
        d.BindTarget(target);
        d.ClearWithDepth(0, 0, 0, 1);
        DrawSurfaces(target, project, resources, sources, in frame, context, viewProj, projector.Pose.Position, haveBounds ? min : Vec3d.Zero, haveBounds ? max : Vec3d.Zero, tint: false, null);
        target.Color.Version++;
    }

    /// <summary>The editor's view: the floor, the surfaces (their content or their colour), the selection
    /// in wireframe, and the projectors' frusta as lines in their colours.</summary>
    public void RenderEditor(RenderTarget target, Project project, EditorCamera camera, IStageResources resources, SourceResolver sources, in GeneratorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(camera);
        var k = camera.Intrinsics.Width == target.Width && camera.Intrinsics.Height == target.Height
            ? camera.Intrinsics
            : camera.Intrinsics.Scaled(target.Width, target.Height);
        var haveBounds = WorldBounds(project, resources, out var min, out var max);
        var (near, far) = Planes(camera.Pose, haveBounds, min, max);
        far = Math.Max(far, 100);
        var viewProj = ViewProjection(k, camera.Pose, near, far);
        var d = _services.Device;
        d.BindTarget(target);
        d.ClearWithDepth(0.07f, 0.075f, 0.09f, 1);
        var context = new GeneratorContext("Editor", 0, "#3EC1F3", false);
        DrawSurfaces(target, project, resources, sources, in frame, context, viewProj, camera.Pose.Position,
            haveBounds ? min : Vec3d.Zero, haveBounds ? max : Vec3d.Zero, tint: !camera.ShowContent, camera.Selected);

        _lineCount = 0;
        if (camera.ShowFloor)
        {
            for (var i = -5; i <= 5; i++)
            {
                var a = i == 0 ? 0.45f : 0.22f;
                AddLine(new Vec3d(i, 0, -5), new Vec3d(i, 0, 5), a, a, a, 1);
                AddLine(new Vec3d(-5, 0, i), new Vec3d(5, 0, i), a, a, a, 1);
            }
        }
        if (camera.ShowFrusta)
        {
            var reach = haveBounds ? Math.Max(0.5, Vec3d.Distance((min + max) * 0.5, camera.Pose.Position) * 0.25) : 2.0;
            foreach (var pj in project.Stage.Projectors)
            {
                var c = ColorRgb.Parse(pj.Color, ColorRgb.White);
                var selected = camera.Selected?.Contains(pj.Id) == true;
                var dist = haveBounds ? Math.Max(0.3, Vec3d.Distance((min + max) * 0.5, pj.Pose.Position)) : reach;
                AddFrustum(pj, dist, selected ? ColorRgb.Lerp(c, ColorRgb.White, 0.5f) : c);
            }
        }
        DrawLines(viewProj);
        target.Color.Version++;
    }

    private void DrawSurfaces(RenderTarget target, Project project, IStageResources resources, SourceResolver sources, in GeneratorFrame frame,
        GeneratorContext context, Matrix4x4 viewProj, Vec3d eye, Vec3d boundsMin, Vec3d boundsMax, bool tint, IReadOnlySet<string>? selected)
    {
        var d = _services.Device;
        var p = d.Program("stage");
        // Resolve every surface's source before drawing: a source may draw (a generator, a canvas) and must
        // not do so between this pass's draws.
        var surfaces = project.Stage.Surfaces;
        if (_textures.Length < surfaces.Length)
            _textures = new GpuTexture?[surfaces.Length * 2];
        for (var i = 0; i < surfaces.Length; i++)
        {
            var s = surfaces[i];
            var drawn = s.Visible && s.Opacity > 0 && !tint && sources.EffectOf(s.Content.Source) is null;
            _textures[i] = drawn ? sources.Resolve(s.Content.Source, target.Width, target.Height, context) : null;
        }

        d.BindTarget(target);
        d.SetBlend(BlendMode.Opaque);
        d.SetDepth(true, true);
        d.SetCull(CullMode.None);
        p.Set("uViewProj", viewProj);
        p.Set("uEye", (float)eye.X, (float)eye.Y, (float)eye.Z);
        p.Set("uBoundsMin", (float)boundsMin.X, (float)boundsMin.Y, (float)boundsMin.Z);
        p.Set("uBoundsMax", (float)boundsMax.X, (float)boundsMax.Y, (float)boundsMax.Z);
        p.Set("uTime", (float)frame.Seconds);
        for (var i = 0; i < surfaces.Length; i++)
        {
            var s = surfaces[i];
            if (!s.Visible || s.Opacity <= 0 || resources.Mesh(s.MeshId) is not { } mesh || mesh.TriangleCount == 0)
                continue;
            var gpu = _caches.Mesh(mesh, s.Faces);
            if (gpu.IndexCount == 0)
                continue;
            SetTransform(p, s.Transform);
            p.Set("uOpacity", (float)Math.Clamp(s.Opacity, 0, 1));
            var mask = resources.SurfaceMask(s.Id);
            p.SetInt("uHasMask", mask is null ? 0 : 1);
            d.BindSampler(p, "uMask", 1, mask is null ? _services.White : _caches.Coverage(mask));
            p.Set("uProjector", Matrix4x4.Identity);
            var isSelected = selected?.Contains(s.Id) == true;
            if (tint)
            {
                var c = ColorRgb.Parse(s.Color, new ColorRgb(0.6f, 0.6f, 0.6f));
                p.SetInt("uMode", 5);
                p.Set("uColorA", c.R, c.G, c.B, 1);
                p.Set("uColorB", 1f, 0.85f, 0.2f, isSelected ? 0.45f : 0);
                d.BindSampler(p, "uSource", 0, _services.Black);
            }
            else if (sources.EffectOf(s.Content.Source) is { } effect)
            {
                EffectUniforms.For(GeneratorCatalog.Find(effect.Kind)!, effect.Params).Apply(p, frame.Seconds);
                d.BindSampler(p, "uSource", 0, _services.Black);
            }
            else
            {
                SetContent(p, project, s.Content, _textures[i] ?? _services.Black);
                d.BindSampler(p, "uSource", 0, _textures[i] ?? _services.Black);
            }
            d.DrawIndexed(p, gpu.Vao, PrimitiveKind.Triangles, gpu.IndexCount);
            if (isSelected)
            {
                d.SetWireframe(true);
                p.SetInt("uMode", 6);
                p.Set("uColorA", 1f, 0.85f, 0.2f, 1);
                p.Set("uOpacity", 1f);
                p.SetInt("uHasMask", 0);
                d.DrawIndexed(p, gpu.Vao, PrimitiveKind.Triangles, gpu.IndexCount);
                d.SetWireframe(false);
            }
        }
        d.SetDepth(false, false);
        Array.Clear(_textures, 0, surfaces.Length);
    }

    private GpuTexture?[] _textures = new GpuTexture?[16];

    /// <summary>The content uniforms of a surface: mode 0 through its UVs (Region offsets and scales them in
    /// UV units; a canvas region is in canvas pixels), mode 1 projected from its viewpoint.</summary>
    private static void SetContent(ShaderProgram p, Project project, SurfaceContent content, GpuTexture texture)
    {
        var r = content.Region;
        switch (content.Mode)
        {
            case SurfaceMapping.CanvasRegion:
            {
                double cw = texture.Width, ch = texture.Height;
                if (content.Source.Kind == SourceKind.Canvas && project.FindCanvas(content.Source.Id) is { } canvas)
                    (cw, ch) = (canvas.Width, canvas.Height);
                p.SetInt("uMode", 0);
                p.Set("uRegion", (float)(r.X / cw), (float)(r.Y / ch), (float)(r.W / cw), (float)(r.H / ch));
                break;
            }
            case SurfaceMapping.Projection when content.ViewpointId is { } vid && project.Stage.Viewpoints.Find(vid) is { } vp:
            {
                var (view, proj) = CameraModel.GlMatrices(vp.Intrinsics, vp.Pose, 0.01, 10000);
                p.SetInt("uMode", 1);
                p.Set("uProjector", view * proj);
                p.Set("uRegion", (float)r.X, (float)r.Y, (float)(r.IsEmpty ? 1 : r.W), (float)(r.IsEmpty ? 1 : r.H));
                break;
            }
            default:
                p.SetInt("uMode", 0);
                p.Set("uRegion", (float)r.X, (float)r.Y, (float)(r.IsEmpty ? 1 : r.W), (float)(r.IsEmpty ? 1 : r.H));
                break;
        }
    }

    /// <summary>The model matrix (row-vector form: world = R·(S∘local) + P) and the normal matrix R·S⁻¹.</summary>
    private static void SetTransform(ShaderProgram p, Transform3 t)
    {
        var r = Mat3d.FromRotationVector(t.Rotation);
        var s = t.Scale;
        var model = new Matrix4x4(
            (float)(r.M00 * s.X), (float)(r.M10 * s.X), (float)(r.M20 * s.X), 0,
            (float)(r.M01 * s.Y), (float)(r.M11 * s.Y), (float)(r.M21 * s.Y), 0,
            (float)(r.M02 * s.Z), (float)(r.M12 * s.Z), (float)(r.M22 * s.Z), 0,
            (float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z, 1);
        p.Set("uModel", model);
        static double Inv(double v) => Math.Abs(v) < 1e-12 ? 0 : 1 / v;
        var normal = new Matrix3x3(
            (float)(r.M00 * Inv(s.X)), (float)(r.M01 * Inv(s.Y)), (float)(r.M02 * Inv(s.Z)),
            (float)(r.M10 * Inv(s.X)), (float)(r.M11 * Inv(s.Y)), (float)(r.M12 * Inv(s.Z)),
            (float)(r.M20 * Inv(s.X)), (float)(r.M21 * Inv(s.Y)), (float)(r.M22 * Inv(s.Z)));
        p.Set("uNormalMat", normal);
    }

    private void AddFrustum(Projector pj, double distance, ColorRgb c)
    {
        var k = pj.Intrinsics;
        Span<Vec3d> far = stackalloc Vec3d[4];
        ReadOnlySpan<Vec2d> corners = [new(0, 0), new(k.Width, 0), new(k.Width, k.Height), new(0, k.Height)];
        for (var i = 0; i < 4; i++)
        {
            var dir = CameraModel.PixelRayCamera(corners[i], k, pj.Lens);
            var cam = dir / Math.Max(1e-6, dir.Z) * distance;
            far[i] = pj.Pose.CameraToWorldPoint(cam);
        }
        for (var i = 0; i < 4; i++)
        {
            AddLine(pj.Pose.Position, far[i], c.R, c.G, c.B, 1);
            AddLine(far[i], far[(i + 1) % 4], c.R, c.G, c.B, 1);
        }
        // The top edge marked, so a frustum shows which way its picture is up.
        var top = (far[0] + far[1]) * 0.5;
        var lift = pj.Pose.CameraToWorld * new Vec3d(0, -distance * 0.08, 0);
        AddLine(far[0], top + lift, c.R, c.G, c.B, 1);
        AddLine(top + lift, far[1], c.R, c.G, c.B, 1);
    }

    private void AddLine(Vec3d a, Vec3d b, float r, float g, float bl, float alpha)
    {
        if ((_lineCount + 2) * LineFloats > _lines.Length)
            Array.Resize(ref _lines, _lines.Length * 2);
        Put(a, r, g, bl, alpha);
        Put(b, r, g, bl, alpha);
    }

    private void Put(Vec3d v, float r, float g, float b, float a)
    {
        var o = _lineCount * LineFloats;
        _lines[o] = (float)v.X;
        _lines[o + 1] = (float)v.Y;
        _lines[o + 2] = (float)v.Z;
        _lines[o + 3] = r;
        _lines[o + 4] = g;
        _lines[o + 5] = b;
        _lines[o + 6] = a;
        _lineCount++;
    }

    private void DrawLines(Matrix4x4 viewProj)
    {
        if (_lineCount == 0)
            return;
        var d = _services.Device;
        d.SetBufferData<float>(_lineBuffer, _lines.AsSpan(0, _lineCount * LineFloats));
        var p = d.Program("lines");
        p.Set("uViewProj", viewProj);
        d.SetDepth(true, false);
        d.Draw(p, _lineVao, PrimitiveKind.Lines, _lineCount);
        d.SetDepth(false, false);
    }

    public void Dispose()
    {
        _lineVao.Dispose();
        _lineBuffer.Dispose();
    }
}
