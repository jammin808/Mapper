using System.Runtime.InteropServices;
using System.Text;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>What glslang made of one stage: the SPIR-V words, or its log.</summary>
public sealed record SpirvModule(bool Ok, uint[] Words, string Log);

/// <summary>glslang through shaderc's C interface: GLSL 4.50 to SPIR-V for Vulkan 1.0 with Vulkan's rules relaxed (glslang's
/// <c>-R</c>: loose uniforms gather into the default uniform block, gl_VertexID is allowed) and the locations and bindings a stage
/// leaves out mapped automatically. One compiler per worker; the worker compiles one source at a time.</summary>
public sealed unsafe class Glslang : IDisposable
{
    // shaderc_target_env_vulkan and shaderc_env_version_vulkan_1_0; shaderc_glsl_vertex_shader and shaderc_glsl_fragment_shader.
    private const int TargetVulkan = 0;
    private const uint Vulkan10 = 1u << 22;
    private const int VertexKind = 0;
    private const int FragmentKind = 1;

    private readonly nint _compiler;
    private readonly nint _options;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _releaseCompiler;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _releaseOptions;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, int, byte*, byte*, nint, nint> _compile;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _status;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*> _error;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*> _bytes;
    private readonly delegate* unmanaged[Cdecl]<nint, nuint> _length;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _releaseResult;

    private Glslang(nint library)
    {
        nint F(string name) => NativeLibrary.GetExport(library, name);
        var initCompiler = (delegate* unmanaged[Cdecl]<nint>)F("shaderc_compiler_initialize");
        var initOptions = (delegate* unmanaged[Cdecl]<nint>)F("shaderc_compile_options_initialize");
        var targetEnv = (delegate* unmanaged[Cdecl]<nint, int, uint, void>)F("shaderc_compile_options_set_target_env");
        var relaxed = (delegate* unmanaged[Cdecl]<nint, byte, void>)F("shaderc_compile_options_set_vulkan_rules_relaxed");
        var autoLocations = (delegate* unmanaged[Cdecl]<nint, byte, void>)F("shaderc_compile_options_set_auto_map_locations");
        var autoBindings = (delegate* unmanaged[Cdecl]<nint, byte, void>)F("shaderc_compile_options_set_auto_bind_uniforms");
        var debugInfo = (delegate* unmanaged[Cdecl]<nint, void>)F("shaderc_compile_options_set_generate_debug_info");
        _releaseCompiler = (delegate* unmanaged[Cdecl]<nint, void>)F("shaderc_compiler_release");
        _releaseOptions = (delegate* unmanaged[Cdecl]<nint, void>)F("shaderc_compile_options_release");
        _compile = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, int, byte*, byte*, nint, nint>)F("shaderc_compile_into_spv");
        _status = (delegate* unmanaged[Cdecl]<nint, int>)F("shaderc_result_get_compilation_status");
        _error = (delegate* unmanaged[Cdecl]<nint, byte*>)F("shaderc_result_get_error_message");
        _bytes = (delegate* unmanaged[Cdecl]<nint, byte*>)F("shaderc_result_get_bytes");
        _length = (delegate* unmanaged[Cdecl]<nint, nuint>)F("shaderc_result_get_length");
        _releaseResult = (delegate* unmanaged[Cdecl]<nint, void>)F("shaderc_result_release");
        _compiler = initCompiler();
        _options = initOptions();
        targetEnv(_options, TargetVulkan, Vulkan10);
        relaxed(_options, 1);
        autoLocations(_options, 1);
        // The default uniform block takes a binding of glslang's choosing; SPIRV-Cross moves it to b0 either way.
        autoBindings(_options, 1);
        // Names survive into SPIR-V, so SPIRV-Cross's HLSL keeps the ISF file's own names and DXC's PDB maps back to them.
        debugInfo(_options);
    }

    /// <summary>glslang from the pinned shaderc; null with words when it cannot be loaded.</summary>
    public static Glslang? TryLoad(out string words, string? baseDirectory = null)
    {
        if (!CompilerNatives.TryLoad(CompilerNatives.Shaderc, out var library, out words, baseDirectory))
            return null;
        try
        {
            return new Glslang(library);
        }
        catch (EntryPointNotFoundException ex)
        {
            words = "the pinned shaderc lacks a call PlayMap makes: " + ex.Message;
            return null;
        }
    }

    /// <summary>Compiles one stage. <paramref name="name"/> is the file name glslang's messages carry.</summary>
    public SpirvModule Compile(string source, bool vertex, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        var text = Encoding.UTF8.GetBytes(source);
        var file = Encoding.UTF8.GetBytes(name + "\0");
        nint result;
        fixed (byte* s = text)
        fixed (byte* f = file)
        fixed (byte* e = "main\0"u8)
            result = _compile(_compiler, s, (nuint)text.Length, vertex ? VertexKind : FragmentKind, f, e, _options);
        if (result == 0)
            return new SpirvModule(false, [], "glslang returned no result");
        try
        {
            var status = _status(result);
            var log = Marshal.PtrToStringUTF8((nint)_error(result)) ?? "";
            if (status != 0)
                return new SpirvModule(false, [], log.Length > 0 ? log : $"glslang stopped (status {status})");
            var length = (int)_length(result);
            var words = new uint[length / 4];
            new ReadOnlySpan<byte>(_bytes(result), length).CopyTo(MemoryMarshal.AsBytes(words.AsSpan()));
            return new SpirvModule(true, words, log);
        }
        finally
        {
            _releaseResult(result);
        }
    }

    public void Dispose()
    {
        _releaseOptions(_options);
        _releaseCompiler(_compiler);
    }
}
