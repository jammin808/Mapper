using PlayMap.Core.Gpu;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gl;

/// <summary>What effects need of the device beyond the library's programs: compiling on another context that shares this one's
/// objects (the compile lane), taking such a program into the device's census, and GPU timestamps written where a frame's list asks
/// (<see cref="GlTimers"/> reads them back).</summary>
public sealed unsafe partial class GlDevice
{
    /// <summary>Compiles and links a program on whatever context is current on the calling thread (no state of this device is
    /// read or written), with the dialect's header in front; answers the program's id, or 0 with the driver's log. The caller
    /// finishes the context before another context uses the program.</summary>
    internal static uint CompileDetached(GL gl, GlDialect dialect, string name, string vertexSource, string fragmentSource, out string? error) =>
        CompileDetached(gl, dialect, name, vertexSource, fragmentSource, out error, out _);

    /// <summary>As <see cref="CompileDetached(GL, GlDialect, string, string, string, out string?)"/>, naming the stage that refused:
    /// "vertex" or "fragment" (a compile), or "link"; "" when the program was made.</summary>
    internal static uint CompileDetached(GL gl, GlDialect dialect, string name, string vertexSource, string fragmentSource, out string? error,
        out string stage)
    {
        ArgumentNullException.ThrowIfNull(gl);
        RefuseHeader(name, "vertex", vertexSource);
        RefuseHeader(name, "fragment", fragmentSource);
        var header = GlDialects.Header(dialect);
        var vs = CompileDetachedShader(gl, GLEnum.VertexShader, header + vertexSource, out error);
        if (vs == 0)
        {
            error = $"Shader '{name}' (vertex) did not compile: {error}";
            stage = "vertex";
            return 0;
        }
        var fs = CompileDetachedShader(gl, GLEnum.FragmentShader, header + fragmentSource, out error);
        if (fs == 0)
        {
            gl.DeleteShader(vs);
            error = $"Shader '{name}' (fragment) did not compile: {error}";
            stage = "fragment";
            return 0;
        }
        var id = gl.CreateProgram();
        gl.AttachShader(id, vs);
        gl.AttachShader(id, fs);
        gl.LinkProgram(id);
        gl.DetachShader(id, vs);
        gl.DetachShader(id, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (gl.GetProgram(id, GLEnum.LinkStatus) == 0)
        {
            error = $"Program '{name}' did not link: {gl.GetProgramInfoLog(id)}";
            stage = "link";
            gl.DeleteProgram(id);
            return 0;
        }
        error = null;
        stage = "";
        return id;
    }

    /// <summary>A linked program's layout, read back from the GL (its active uniforms and samplers) on whatever context made it: each
    /// constant with its type and element count, each sampler a texture slot, in the order the sources first name them (the order
    /// they declare them in), packed by the cbuffer rules. A uniform of a type no layout holds (a three-component int vector, a
    /// 2×2 matrix, a sampler other than a 2D one) refuses with words; GLSL's bool takes an int's place.</summary>
    internal static ProgramLayout? ReflectLayout(GL gl, uint program, string name, string vertexSource, string fragmentSource, out string? error)
    {
        ArgumentNullException.ThrowIfNull(gl);
        gl.GetProgram(program, GLEnum.ActiveUniforms, out var count);
        var constants = new List<(string Name, ConstantType Type, int Count)>();
        var samplers = new List<string>();
        for (var i = 0u; i < (uint)count; i++)
        {
            var uniform = gl.GetActiveUniform(program, i, out var size, out UniformType type);
            if (uniform.StartsWith("gl_", StringComparison.Ordinal))
                continue;
            // An array reports its first element ("uColors[0]") and its length.
            var bare = uniform.EndsWith("[0]", StringComparison.Ordinal) ? uniform[..^3] : uniform;
            if (type == UniformType.Sampler2D)
            {
                if (size != 1)
                {
                    error = $"Program '{name}': '{bare}' is an array of samplers, which a layout has no slot for";
                    return null;
                }
                samplers.Add(bare);
                continue;
            }
            if (ConstantOf(type) is not { } constant)
            {
                error = $"Program '{name}': uniform '{bare}' is a {type}, which a layout has no type for";
                return null;
            }
            constants.Add((bare, constant, size));
        }
        var text = vertexSource + "\n" + fragmentSource;
        constants.Sort((a, b) => Order(text, a.Name, b.Name));
        samplers.Sort((a, b) => Order(text, a, b));
        try
        {
            error = null;
            return ProgramLayout.Pack(name, constants, samplers.Select(s => (s, true)));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static ConstantType? ConstantOf(UniformType type) => type switch
    {
        UniformType.Float => ConstantType.Float,
        UniformType.FloatVec2 => ConstantType.Float2,
        UniformType.FloatVec3 => ConstantType.Float3,
        UniformType.FloatVec4 => ConstantType.Float4,
        UniformType.Int or UniformType.Bool => ConstantType.Int,
        UniformType.IntVec2 or UniformType.BoolVec2 => ConstantType.Int2,
        UniformType.IntVec4 or UniformType.BoolVec4 => ConstantType.Int4,
        UniformType.FloatMat3 => ConstantType.Float3x3,
        UniformType.FloatMat4 => ConstantType.Float4x4,
        _ => null,
    };

    /// <summary>Orders two names by where the sources first name them as a whole word (a name never found sorts last), then by name.</summary>
    private static int Order(string text, string a, string b)
    {
        var c = FirstWord(text, a).CompareTo(FirstWord(text, b));
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    private static int FirstWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var end = at + word.Length;
            var after = end >= text.Length ? ' ' : text[end];
            if (!IsIdentifier(before) && !IsIdentifier(after))
                return at;
        }
        return int.MaxValue;
    }

    private static bool IsIdentifier(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static uint CompileDetachedShader(GL gl, GLEnum type, string source, out string? log)
    {
        var id = gl.CreateShader(type);
        gl.ShaderSource(id, source);
        gl.CompileShader(id);
        if (gl.GetShader(id, GLEnum.CompileStatus) != 0)
        {
            log = null;
            return id;
        }
        log = gl.GetShaderInfoLog(id);
        gl.DeleteShader(id);
        return 0;
    }

    /// <summary>A program linked on a context that shares this device's objects, finished there, now this device's: counted in
    /// its census and deleted with it.</summary>
    internal ShaderProgram AdoptProgram(uint id, string name)
    {
        AssertOwner();
        _livePrograms.Add(id);
        return new ShaderProgram(this, id, name);
    }

    /// <summary>Whether the library's program of this name is linked on this device.</summary>
    internal bool HasProgram(string name) => _programs.ContainsKey(name);

    /// <summary>A library program linked on a context that shares this device's objects, finished there, now the one
    /// <see cref="Program"/> answers for its name; false (nothing taken) when the device already holds one.</summary>
    internal bool AdoptLibraryProgram(string name, uint id)
    {
        AssertOwner();
        if (_programs.ContainsKey(name))
            return false;
        _livePrograms.Add(id);
        _programs[name] = new ShaderProgram(this, id, name);
        return true;
    }

    /// <summary>Records the GPU's clock into a query when the commands before it have run.</summary>
    internal void Timestamp(uint query) => _gl.QueryCounter(query, QueryCounterTarget.Timestamp);
}
