using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gpu;

/// <summary>What a device checks and reports.</summary>
public sealed record GlDeviceOptions(
    /// <summary>Read the GL error flag after each resource call and draw (a stall on some drivers; on in debug builds).</summary>
    bool CheckErrors,
    /// <summary>Subscribe to the driver's debug output when the context offers it.</summary>
    bool DebugOutput,
    /// <summary>Where notes go (debug messages that are not errors, shader warnings); null drops them.</summary>
    Action<string>? Log = null)
{
#if DEBUG
    public static readonly GlDeviceOptions Default = new(true, true);
#else
    public static readonly GlDeviceOptions Default = new(false, false);
#endif
}

/// <summary>The renderer's seam to the GPU: every GL call the renderer makes goes through this type and the
/// resource types beside it (textures, render targets, buffers, vertex arrays, programs), so a Direct3D 11
/// device can stand in its place behind the same shapes. It caches the bound state of its own context (the
/// program, the vertex array, the target, the viewport, the blend, depth and cull state, the textures per
/// unit) and skips redundant calls.
///
/// Threading: a device belongs to the thread that made it, with its context current; every call, and every
/// resource's Dispose, happens there. Objects it makes are shared with the contexts made to share its
/// context (output windows), except container objects — framebuffers and vertex arrays — which belong to
/// the context that made them.</summary>
public sealed unsafe partial class GlDevice : IDisposable
{
    private const int TextureUnits = 16;
    private const int MaxErrors = 64;

    private readonly GL _gl;
    private readonly GlDeviceOptions _options;
    private readonly int _ownerThread;
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, ShaderProgram> _programs = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _liveTextures = [];
    private readonly HashSet<uint> _liveFramebuffers = [];
    private readonly HashSet<uint> _liveRenderbuffers = [];
    private readonly HashSet<uint> _liveBuffers = [];
    private readonly HashSet<uint> _liveVertexArrays = [];
    private readonly HashSet<uint> _livePrograms = [];
    private readonly uint[] _boundTextures = new uint[TextureUnits];
    private DebugProc? _debugProc;
    private GCHandle _self;
    private uint _emptyVao;
    private bool _disposed;

    // The cached state of this device's context. uint.MaxValue = unknown.
    private uint _program = uint.MaxValue;
    private uint _vao = uint.MaxValue;
    private uint _framebuffer = uint.MaxValue;
    private int _activeUnit = -1;
    private (int X, int Y, int W, int H) _viewport = (-1, -1, -1, -1);
    private BlendMode? _blend;
    private bool? _depthTest;
    private bool? _depthWrite;
    private CullMode? _cull;
    private bool? _wireframe;

    public GlDevice(GL gl, GlDeviceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _gl = gl;
        _options = options ?? GlDeviceOptions.Default;
        _ownerThread = Environment.CurrentManagedThreadId;
        Renderer = ReadString(GLEnum.Renderer);
        Vendor = ReadString(GLEnum.Vendor);
        Version = ReadString(GLEnum.Version);
        var major = _gl.GetInteger(GLEnum.MajorVersion);
        var minor = _gl.GetInteger(GLEnum.MinorVersion);
        GlVersion = major * 10 + minor;
        HasDebugOutput = GlVersion >= 43 || _gl.IsExtensionPresent("GL_KHR_debug");
        MaxTextureSize = _gl.GetInteger(GLEnum.MaxTextureSize);
        Dialect = GlDialect.Core330;
        Caps = GlCaps.From(GlDialects.Word(Dialect), major, minor, ReadExtensions());
        if (_options.DebugOutput && HasDebugOutput)
            EnableDebugOutput();
        _emptyVao = _gl.GenVertexArray();
        _liveVertexArrays.Add(_emptyVao);
        _gl.PixelStore(GLEnum.UnpackAlignment, 1);
        _gl.PixelStore(GLEnum.PackAlignment, 1);
        InvalidateState();
        Check("device");
    }

    internal GL Gl => _gl;

    public string Renderer { get; }
    public string Vendor { get; }
    public string Version { get; }

    /// <summary>Major × 10 + minor (33 for 3.3, 45 for 4.5).</summary>
    public int GlVersion { get; }

    public bool HasDebugOutput { get; }
    public int MaxTextureSize { get; }

    /// <summary>The GLSL dialect this context compiles; every program source gets its header (<see cref="CreateProgram"/>).</summary>
    public GlDialect Dialect { get; }

    /// <summary>What this context samples and renders to, read once from its version and extensions.</summary>
    public GlCaps Caps { get; }

    /// <summary>The context's extensions, read one by one (glGetString(GL_EXTENSIONS) is an error on a core profile).</summary>
    private HashSet<string> ReadExtensions()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = _gl.GetInteger(GLEnum.NumExtensions);
        for (var i = 0u; i < count; i++)
        {
            var p = _gl.GetString(GLEnum.Extensions, i);
            if (p != null)
                names.Add(Marshal.PtrToStringUTF8((nint)p) ?? "");
        }
        names.Remove("");
        return names;
    }

    /// <summary>Errors seen since the last <see cref="TakeErrors"/> (error flags and debug-output errors).</summary>
    public int ErrorCount => _errors.Count;

    /// <summary>Resources this device made that are still alive — the census a leak test reads.</summary>
    public int LiveTextures => _liveTextures.Count;
    public int LiveTargets => _liveFramebuffers.Count;
    public int LiveBuffers => _liveBuffers.Count;

    /// <summary>The errors seen since the last call, and forgets them.</summary>
    public IReadOnlyList<string> TakeErrors()
    {
        Check("take");
        var copy = _errors.ToArray();
        _errors.Clear();
        return copy;
    }

    /// <summary>Reads the error flag when checks are on; the errors are kept for <see cref="TakeErrors"/>.</summary>
    public void Check(string where)
    {
        if (!_options.CheckErrors)
            return;
        for (var i = 0; i < 8; i++)
        {
            var e = _gl.GetError();
            if (e == GLEnum.NoError)
                return;
            AddError($"{where}: {e}");
        }
    }

    /// <summary>Forget the cached state: another context ran, or raw calls changed it. Puts the context in
    /// the device's base state (no blend, no depth, no cull, filled polygons).</summary>
    public void InvalidateState()
    {
        _program = uint.MaxValue;
        _vao = uint.MaxValue;
        _framebuffer = uint.MaxValue;
        _activeUnit = -1;
        Array.Fill(_boundTextures, uint.MaxValue);
        _viewport = (-1, -1, -1, -1);
        _blend = null;
        _depthTest = null;
        _depthWrite = null;
        _cull = null;
        _wireframe = null;
        SetBlend(BlendMode.Opaque);
        SetDepth(false, false);
        SetCull(CullMode.None);
        SetWireframe(false);
        foreach (var p in _programs.Values)
            p.ForgetValues();
    }

    // ---- targets and state ----

    /// <summary>Draw into a render target over its whole raster.</summary>
    public void BindTarget(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        AssertOwner();
        if (_framebuffer != target.Framebuffer)
        {
            _gl.BindFramebuffer(GLEnum.Framebuffer, target.Framebuffer);
            _framebuffer = target.Framebuffer;
        }
        SetViewport(0, 0, target.Width, target.Height);
    }

    public void SetViewport(int x, int y, int width, int height)
    {
        if (_viewport == (x, y, width, height))
            return;
        _gl.Viewport(x, y, (uint)width, (uint)height);
        _viewport = (x, y, width, height);
    }

    public void SetBlend(BlendMode mode)
    {
        if (_blend == mode)
            return;
        if (mode == BlendMode.Opaque)
        {
            _gl.Disable(GLEnum.Blend);
        }
        else
        {
            if (_blend is null or BlendMode.Opaque)
                _gl.Enable(GLEnum.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);
            // Every mode keeps the destination's alpha: targets are opaque pictures.
            switch (mode)
            {
                case BlendMode.Normal:
                    _gl.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, GLEnum.Zero, GLEnum.One);
                    break;
                case BlendMode.Premultiplied:
                    _gl.BlendFuncSeparate(GLEnum.One, GLEnum.OneMinusSrcAlpha, GLEnum.Zero, GLEnum.One);
                    break;
                case BlendMode.Add:
                    _gl.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.One, GLEnum.Zero, GLEnum.One);
                    break;
                case BlendMode.AddPremultiplied:
                    _gl.BlendFuncSeparate(GLEnum.One, GLEnum.One, GLEnum.Zero, GLEnum.One);
                    break;
                case BlendMode.Multiply:
                    _gl.BlendFuncSeparate(GLEnum.DstColor, GLEnum.Zero, GLEnum.Zero, GLEnum.One);
                    break;
                case BlendMode.Screen:
                    _gl.BlendFuncSeparate(GLEnum.One, GLEnum.OneMinusSrcColor, GLEnum.Zero, GLEnum.One);
                    break;
            }
        }
        _blend = mode;
    }

    public void SetDepth(bool test, bool write)
    {
        if (_depthTest != test)
        {
            if (test)
            {
                _gl.Enable(GLEnum.DepthTest);
                _gl.DepthFunc(GLEnum.Lequal);
            }
            else
            {
                _gl.Disable(GLEnum.DepthTest);
            }
            _depthTest = test;
        }
        if (_depthWrite != write)
        {
            _gl.DepthMask(write);
            _depthWrite = write;
        }
    }

    /// <summary>Back-face culling. The renderer draws its targets top row first, which mirrors the raster's y
    /// and turns a counter-clockwise front face clockwise in the target: the front face is set to match.</summary>
    public void SetCull(CullMode mode)
    {
        if (_cull == mode)
            return;
        if (mode == CullMode.None)
        {
            _gl.Disable(GLEnum.CullFace);
        }
        else
        {
            _gl.Enable(GLEnum.CullFace);
            _gl.FrontFace(GLEnum.CW);
            _gl.CullFace(GLEnum.Back);
        }
        _cull = mode;
    }

    public void SetWireframe(bool lines)
    {
        if (_wireframe == lines)
            return;
        _gl.PolygonMode(GLEnum.FrontAndBack, lines ? GLEnum.Line : GLEnum.Fill);
        _wireframe = lines;
    }

    public void UseProgram(ShaderProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (_program == program.Id)
            return;
        _gl.UseProgram(program.Id);
        _program = program.Id;
    }

    public void BindTexture(int unit, GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        BindTextureId(unit, texture.Id);
    }

    private void BindTextureId(int unit, uint id)
    {
        if (_boundTextures[unit] == id)
            return;
        if (_activeUnit != unit)
        {
            _gl.ActiveTexture(GLEnum.Texture0 + unit);
            _activeUnit = unit;
        }
        _gl.BindTexture(GLEnum.Texture2D, id);
        _boundTextures[unit] = id;
    }

    /// <summary>Binds a texture to a unit and points the program's sampler uniform at it.</summary>
    public void BindSampler(ShaderProgram program, string sampler, int unit, GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(program);
        program.SetInt(sampler, unit);
        BindTexture(unit, texture);
    }

    public void Clear(float r, float g, float b, float a)
    {
        AssertOwner();
        _gl.ClearColor(r, g, b, a);
        _gl.Clear((uint)GLEnum.ColorBufferBit);
    }

    /// <summary>Clears colour and depth (depth to the far plane; depth writes are switched on to do it).</summary>
    public void ClearWithDepth(float r, float g, float b, float a)
    {
        AssertOwner();
        SetDepth(_depthTest ?? false, true);
        _gl.ClearColor(r, g, b, a);
        _gl.Clear((uint)GLEnum.ColorBufferBit | (uint)GLEnum.DepthBufferBit);
    }

    /// <summary>One triangle that covers the target; the vertex shader makes it from gl_VertexID.</summary>
    public void DrawFullscreen(ShaderProgram program)
    {
        UseProgram(program);
        BindVertexArrayId(_emptyVao);
        _gl.DrawArrays(GLEnum.Triangles, 0, 3);
        Check(program.Name);
    }

    /// <summary>Four vertices as a strip with no buffers (a quad made in the vertex shader from gl_VertexID),
    /// <paramref name="instances"/> times.</summary>
    public void DrawGeneratedQuads(ShaderProgram program, int instances = 1)
    {
        UseProgram(program);
        BindVertexArrayId(_emptyVao);
        if (instances == 1)
            _gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
        else
            _gl.DrawArraysInstanced(GLEnum.TriangleStrip, 0, 4, (uint)instances);
        Check(program.Name);
    }

    public void Draw(ShaderProgram program, VertexArray vao, PrimitiveKind kind, int vertexCount, int first = 0)
    {
        ArgumentNullException.ThrowIfNull(vao);
        UseProgram(program);
        BindVertexArrayId(vao.Id);
        _gl.DrawArrays(Primitive(kind), first, (uint)vertexCount);
        Check(program.Name);
    }

    public void DrawIndexed(ShaderProgram program, VertexArray vao, PrimitiveKind kind, int indexCount)
    {
        ArgumentNullException.ThrowIfNull(vao);
        UseProgram(program);
        BindVertexArrayId(vao.Id);
        _gl.DrawElements(Primitive(kind), (uint)indexCount, GLEnum.UnsignedInt, null);
        Check(program.Name);
    }

    public void DrawInstanced(ShaderProgram program, VertexArray vao, PrimitiveKind kind, int vertexCount, int instances)
    {
        ArgumentNullException.ThrowIfNull(vao);
        UseProgram(program);
        BindVertexArrayId(vao.Id);
        _gl.DrawArraysInstanced(Primitive(kind), 0, (uint)vertexCount, (uint)instances);
        Check(program.Name);
    }

    /// <summary>Hands the queued commands to the GPU — what another context sharing these objects needs
    /// before it samples what this one drew.</summary>
    public void Flush() => _gl.Flush();

    /// <summary>Waits for the GPU to finish (tests and teardown only — never on the frame path).</summary>
    public void Finish() => _gl.Finish();

    private void BindVertexArrayId(uint id)
    {
        if (_vao == id)
            return;
        _gl.BindVertexArray(id);
        _vao = id;
    }

    private static GLEnum Primitive(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.TriangleStrip => GLEnum.TriangleStrip,
        PrimitiveKind.Lines => GLEnum.Lines,
        _ => GLEnum.Triangles,
    };

    // ---- uniforms (called by ShaderProgram) ----

    internal int UniformLocation(uint program, string name) => _gl.GetUniformLocation(program, name);

    internal void UniformInt(int location, int value) => _gl.Uniform1(location, value);

    internal void UniformVector(int location, Vector4 v, int components)
    {
        switch (components)
        {
            case 1:
                _gl.Uniform1(location, v.X);
                break;
            case 2:
                _gl.Uniform2(location, v.X, v.Y);
                break;
            case 3:
                _gl.Uniform3(location, v.X, v.Y, v.Z);
                break;
            default:
                _gl.Uniform4(location, v.X, v.Y, v.Z, v.W);
                break;
        }
    }

    internal void UniformMatrix4(int location, in Matrix4x4 m)
    {
        fixed (float* p = &m.M11)
            _gl.UniformMatrix4(location, 1, false, p);
    }

    internal void UniformMatrix3(int location, in Matrix3x3 m)
    {
        // Row-major here; GLSL wants columns, so the upload transposes.
        var p = stackalloc float[9] { m.M00, m.M01, m.M02, m.M10, m.M11, m.M12, m.M20, m.M21, m.M22 };
        _gl.UniformMatrix3(location, 1, true, p);
    }

    // ---- programs ----

    /// <summary>The library's program by name, compiled and linked on first use and kept for the device's
    /// life. A compile or link failure throws <see cref="GlException"/> with the driver's log.</summary>
    public ShaderProgram Program(string name)
    {
        if (_programs.TryGetValue(name, out var p))
            return p;
        var (vs, fs) = Shaders.ShaderLibrary.Source(name);
        p = CreateProgram(name, vs, fs);
        _programs[name] = p;
        return p;
    }

    /// <summary>A program from source (the library's, a generator's, an effect's or a test's). A source carries no #version and no
    /// precision line: the context's dialect header is prepended here, and a source that brings its own is refused with words.</summary>
    public ShaderProgram CreateProgram(string name, string vertexSource, string fragmentSource)
    {
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(fragmentSource);
        RefuseHeader(name, "vertex", vertexSource);
        RefuseHeader(name, "fragment", fragmentSource);
        AssertOwner();
        var header = GlDialects.Header(Dialect);
        var vs = CompileShader(GLEnum.VertexShader, header + vertexSource, name);
        uint fs;
        try
        {
            fs = CompileShader(GLEnum.FragmentShader, header + fragmentSource, name);
        }
        catch (GlException)
        {
            _gl.DeleteShader(vs);
            throw;
        }
        var id = _gl.CreateProgram();
        _gl.AttachShader(id, vs);
        _gl.AttachShader(id, fs);
        _gl.LinkProgram(id);
        _gl.DetachShader(id, vs);
        _gl.DetachShader(id, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        if (_gl.GetProgram(id, GLEnum.LinkStatus) == 0)
        {
            var log = _gl.GetProgramInfoLog(id);
            _gl.DeleteProgram(id);
            throw new GlException($"Program '{name}' did not link: {log}");
        }
        _livePrograms.Add(id);
        Check("program " + name);
        return new ShaderProgram(this, id, name);
    }

    /// <summary>A program source is dialect-free: no line is a #version directive (wherever it stands, after a comment too) and no
    /// line is a precision statement (the device writes both for the context it compiles on).</summary>
    internal static void RefuseHeader(string name, string stage, string source)
    {
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') && line[1..].TrimStart().StartsWith("version", StringComparison.Ordinal))
                throw new ArgumentException($"Program '{name}' ({stage}) holds its own #version — sources carry none; the device writes the dialect's header.", nameof(source));
            if (line.StartsWith("precision ", StringComparison.Ordinal))
                throw new ArgumentException($"Program '{name}' ({stage}) holds a precision statement — sources carry none; the device writes the dialect's header.", nameof(source));
        }
    }

    private uint CompileShader(GLEnum type, string source, string name)
    {
        var id = _gl.CreateShader(type);
        _gl.ShaderSource(id, source);
        _gl.CompileShader(id);
        if (_gl.GetShader(id, GLEnum.CompileStatus) == 0)
        {
            var log = _gl.GetShaderInfoLog(id);
            _gl.DeleteShader(id);
            throw new GlException($"Shader '{name}' ({(type == GLEnum.VertexShader ? "vertex" : "fragment")}) did not compile: {log}");
        }
        return id;
    }

    internal void DeleteProgram(uint id)
    {
        if (_disposed || !_livePrograms.Remove(id))
            return;
        AssertOwner();
        if (_program == id)
            _program = uint.MaxValue;
        _gl.DeleteProgram(id);
    }

    // ---- debug output ----

    private void EnableDebugOutput()
    {
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _debugProc = OnDebugMessage;
        _gl.Enable(GLEnum.DebugOutput);
        _gl.Enable(GLEnum.DebugOutputSynchronous);
        _gl.DebugMessageCallback(_debugProc, (void*)GCHandle.ToIntPtr(_self));
    }

    private static void OnDebugMessage(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        if (userParam == 0 || GCHandle.FromIntPtr(userParam).Target is not GlDevice device)
            return;
        // Notifications, and notes nobody listens to, are dropped before any text is made: a driver that
        // talks every frame must not make the frame allocate.
        var isError = type == GLEnum.DebugTypeError;
        if (!isError && (severity == GLEnum.DebugSeverityNotification || device._options.Log is null))
            return;
        var text = Marshal.PtrToStringUTF8(message, Math.Max(0, length)) ?? "";
        if (isError)
            device.AddError($"debug {severity}: {text}");
        else
            device._options.Log?.Invoke($"GL {type} {severity}: {text}");
    }

    private void AddError(string text)
    {
        if (_errors.Count < MaxErrors)
            _errors.Add(text);
        _options.Log?.Invoke("GL error " + text);
    }

    private string ReadString(GLEnum name)
    {
        var p = _gl.GetString(name);
        return p is null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
    }

    [Conditional("DEBUG")]
    private void AssertOwner() =>
        Debug.Assert(Environment.CurrentManagedThreadId == _ownerThread, "A GL device is used only on the thread that made it.");

    /// <summary>Deletes every object the device still holds (programs it compiled, and as a safety net any
    /// resource its owners forgot). Its context must be current on its thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var p in _programs.Values)
            p.Dispose();
        _programs.Clear();
        foreach (var id in _liveTextures)
            _gl.DeleteTexture(id);
        foreach (var id in _liveFramebuffers)
            _gl.DeleteFramebuffer(id);
        foreach (var id in _liveRenderbuffers)
            _gl.DeleteRenderbuffer(id);
        foreach (var id in _liveBuffers)
            _gl.DeleteBuffer(id);
        foreach (var id in _liveVertexArrays)
            _gl.DeleteVertexArray(id);
        foreach (var id in _livePrograms)
            _gl.DeleteProgram(id);
        _liveTextures.Clear();
        _liveFramebuffers.Clear();
        _liveRenderbuffers.Clear();
        _liveBuffers.Clear();
        _liveVertexArrays.Clear();
        _livePrograms.Clear();
        _emptyVao = 0;
        if (_debugProc is not null)
        {
            _gl.Disable(GLEnum.DebugOutput);
            _debugProc = null;
        }
        if (_self.IsAllocated)
            _self.Free();
        _disposed = true;
    }
}
