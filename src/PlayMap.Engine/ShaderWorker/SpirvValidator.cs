using System.Runtime.InteropServices;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>SPIRV-Tools' validator (<c>spvValidateBinary</c>) for Vulkan 1.0: a module is valid before anything downstream reads it.</summary>
public sealed unsafe class SpirvValidator : IDisposable
{
    // SPV_ENV_VULKAN_1_0 and SPV_SUCCESS.
    private const int VulkanEnv = 1;
    private const int Success = 0;

    /// <summary>spv_diagnostic_t: its position (line, column, index) and its words.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Diagnostic
    {
        public nuint Line;
        public nuint Column;
        public nuint Index;
        public byte* Error;
        public byte IsTextSource;
    }

    private readonly nint _context;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyContext;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, nuint, Diagnostic**, int> _validate;
    private readonly delegate* unmanaged[Cdecl]<Diagnostic*, void> _destroyDiagnostic;

    private SpirvValidator(nint library)
    {
        nint F(string name) => NativeLibrary.GetExport(library, name);
        var create = (delegate* unmanaged[Cdecl]<int, nint>)F("spvContextCreate");
        _destroyContext = (delegate* unmanaged[Cdecl]<nint, void>)F("spvContextDestroy");
        _validate = (delegate* unmanaged[Cdecl]<nint, uint*, nuint, Diagnostic**, int>)F("spvValidateBinary");
        _destroyDiagnostic = (delegate* unmanaged[Cdecl]<Diagnostic*, void>)F("spvDiagnosticDestroy");
        _context = create(VulkanEnv);
    }

    /// <summary>The validator from the pinned SPIRV-Tools; null with words when it cannot be loaded.</summary>
    public static SpirvValidator? TryLoad(out string words, string? baseDirectory = null)
    {
        if (!CompilerNatives.TryLoad(CompilerNatives.SpirvTools, out var library, out words, baseDirectory))
            return null;
        try
        {
            return new SpirvValidator(library);
        }
        catch (EntryPointNotFoundException ex)
        {
            words = "the pinned SPIRV-Tools has no validator: " + ex.Message;
            return null;
        }
    }

    /// <summary>Null when the module is valid; else the validator's words.</summary>
    public string? Validate(uint[] words)
    {
        ArgumentNullException.ThrowIfNull(words);
        Diagnostic* diagnostic = null;
        int result;
        fixed (uint* w = words)
            result = _validate(_context, w, (nuint)words.Length, &diagnostic);
        try
        {
            if (result == Success)
                return null;
            var text = diagnostic is null ? null : Marshal.PtrToStringUTF8((nint)diagnostic->Error);
            return string.IsNullOrWhiteSpace(text) ? $"the module is not valid SPIR-V (result {result})" : text.Trim();
        }
        finally
        {
            if (diagnostic is not null)
                _destroyDiagnostic(diagnostic);
        }
    }

    public void Dispose() => _destroyContext(_context);
}
