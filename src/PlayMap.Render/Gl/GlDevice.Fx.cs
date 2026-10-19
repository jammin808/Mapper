using Silk.NET.OpenGL;

namespace PlayMap.Render.Gl;

/// <summary>What effects need of the device beyond the library's programs: compiling on another context that shares this one's
/// objects (the compile lane), taking such a program into the device's census, and GPU timestamps (a slot's time is measured
/// between two timestamps, which may sit inside the output's own elapsed-time query where a second one may not).</summary>
public sealed unsafe partial class GlDevice
{
    /// <summary>Compiles and links a program on whatever context is current on the calling thread (no state of this device is
    /// read or written), with the dialect's header in front; answers the program's id, or 0 with the driver's log. The caller
    /// finishes the context before another context uses the program.</summary>
    internal static uint CompileDetached(GL gl, GlDialect dialect, string name, string vertexSource, string fragmentSource, out string? error)
    {
        ArgumentNullException.ThrowIfNull(gl);
        RefuseHeader(name, "vertex", vertexSource);
        RefuseHeader(name, "fragment", fragmentSource);
        var header = GlDialects.Header(dialect);
        var vs = CompileDetachedShader(gl, GLEnum.VertexShader, header + vertexSource, out error);
        if (vs == 0)
        {
            error = $"Shader '{name}' (vertex) did not compile: {error}";
            return 0;
        }
        var fs = CompileDetachedShader(gl, GLEnum.FragmentShader, header + fragmentSource, out error);
        if (fs == 0)
        {
            gl.DeleteShader(vs);
            error = $"Shader '{name}' (fragment) did not compile: {error}";
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
            gl.DeleteProgram(id);
            return 0;
        }
        error = null;
        return id;
    }

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

    /// <summary>Records the GPU's clock into a query when the commands before it have run.</summary>
    internal void Timestamp(uint query) => _gl.QueryCounter(query, QueryCounterTarget.Timestamp);
}
