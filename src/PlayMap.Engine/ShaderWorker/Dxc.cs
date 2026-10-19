using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>One entry compiled by DXC: the DXIL container (validated and signed), its PDB and its reflection blob, or DXC's words.</summary>
public sealed record DxilOutput(bool Ok, byte[] Dxil, byte[] Pdb, byte[] Reflection, string Words)
{
    /// <summary>The container's digest is set: the validator signed it (a container no validator has seen carries zeros).</summary>
    public bool Signed => Dxil.Length >= 20 && Dxil.AsSpan(4, 16).ContainsAnyExcept((byte)0);
}

/// <summary>A constant as DXC's reflection reads it: its cbuffer, name, type word (as <see cref="LayoutConstant"/> spells it),
/// offset, array length and whether the entry reads it.</summary>
public readonly record struct ReflectedConstant(string Buffer, string Name, string Type, int Offset, int Count, bool Used);

/// <summary>A resource an entry binds: its name, kind (cbuffer, texture, sampler, other), register and space.</summary>
public readonly record struct ReflectedBinding(string Name, string Kind, int Register, int Space);

/// <summary>What an entry's reflection holds: every cbuffer's constants and every binding.</summary>
public sealed record DxilReflection(ImmutableArray<ReflectedConstant> Constants, ImmutableArray<ReflectedBinding> Bindings);

/// <summary>DXC through its COM interface (IDxcCompiler3, IDxcUtils, the validator): HLSL to DXIL, validated and signed by the
/// pinned validator (loaded first, so the compiler's own look-up finds that build), and a container's reflection read through
/// ID3D12ShaderReflection. The vtables have IUnknown's three slots first on every platform (DXC's Linux adapter declares no
/// virtual destructor). Wide strings are UTF-16 on Windows and UTF-32 elsewhere, as DXC's own wchar_t is.</summary>
public sealed unsafe class Dxc : IDisposable
{
    private const int Utf8 = 65001;
    private const int OutObject = 1;
    private const int OutPdb = 3;
    private const int OutReflection = 8;

    private static readonly Guid ClsidCompiler = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");
    private static readonly Guid IidCompiler3 = new("228b4687-5a6a-4730-900c-9702b2203f54");
    private static readonly Guid ClsidUtils = new("6245d6af-66e0-48fd-80b4-4d271796748c");
    private static readonly Guid IidUtils = new("4605c4cb-2019-492a-ada4-65f20bb7d67f");
    private static readonly Guid ClsidValidator = new("8ca3e215-f728-4cf3-8cdd-88af917587a1");
    private static readonly Guid IidValidator = new("a6e82bd2-1fd7-4826-9811-2857e797f49a");
    private static readonly Guid IidResult = new("58346cda-dde7-4497-9461-6f87af5e0659");
    private static readonly Guid IidBlob = new("8ba5fb08-5195-40e2-ac58-0d989c3a0102");
    private static readonly Guid IidReflection = new("5a58797d-a72c-478d-8ba2-efc6b0efe88e");

    /// <summary>DxcBuffer: the bytes and their code page.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer
    {
        public void* Ptr;
        public nuint Size;
        public uint Encoding;
    }

    private readonly nint _compiler;
    private readonly nint _utils;
    private readonly nint _validator;

    private Dxc(nint compiler, nint utils, nint validator)
    {
        _compiler = compiler;
        _utils = utils;
        _validator = validator;
    }

    /// <summary>The pinned DXC and validator; null with words when either cannot be loaded or made.</summary>
    public static Dxc? TryLoad(out string words, string? baseDirectory = null)
    {
        if (!CompilerNatives.TryLoad(CompilerNatives.Dxil, out var dxil, out words, baseDirectory))
            return null;
        if (!CompilerNatives.TryLoad(CompilerNatives.Dxc, out var dxc, out words, baseDirectory))
            return null;
        if (!NativeLibrary.TryGetExport(dxc, "DxcCreateInstance", out var createCompiler)
            || !NativeLibrary.TryGetExport(dxil, "DxcCreateInstance", out var createValidator))
        {
            words = "the pinned DXC has no DxcCreateInstance";
            return null;
        }
        var make = (delegate* unmanaged<Guid*, Guid*, nint*, int>)createCompiler;
        var makeValidator = (delegate* unmanaged<Guid*, Guid*, nint*, int>)createValidator;
        nint compiler = 0, utils = 0, validator = 0;
        var c = ClsidCompiler;
        var ci = IidCompiler3;
        var u = ClsidUtils;
        var ui = IidUtils;
        var v = ClsidValidator;
        var vi = IidValidator;
        int hr;
        if ((hr = make(&c, &ci, &compiler)) < 0 || (hr = make(&u, &ui, &utils)) < 0 || (hr = makeValidator(&v, &vi, &validator)) < 0)
        {
            Release(compiler);
            Release(utils);
            words = $"DXC did not make its compiler, utilities or validator (0x{hr:x8})";
            return null;
        }
        words = "";
        return new Dxc(compiler, utils, validator);
    }

    private static nint Slot(nint o, int i) => (*(nint**)o)[i];

    private static void Release(nint o)
    {
        if (o != 0)
            _ = ((delegate* unmanaged<nint, uint>)Slot(o, 2))(o);
    }

    private static byte[] BlobBytes(nint blob)
    {
        if (blob == 0)
            return [];
        var p = ((delegate* unmanaged<nint, byte*>)Slot(blob, 3))(blob);
        var n = (int)((delegate* unmanaged<nint, nuint>)Slot(blob, 4))(blob);
        return p is null || n == 0 ? [] : new ReadOnlySpan<byte>(p, n).ToArray();
    }

    /// <summary>A string as DXC's wide characters, terminated.</summary>
    private static byte[] Wide(string s) =>
        OperatingSystem.IsWindows() ? Encoding.Unicode.GetBytes(s + "\0") : Encoding.UTF32.GetBytes(s + "\0");

    /// <summary>Compiles one entry of an HLSL source with DXC's own arguments after <c>-E</c> and <c>-T</c>; the DXIL is validated
    /// (and signed) by the validator before it is answered.</summary>
    public DxilOutput Compile(string source, string entry, string target, IReadOnlyList<string> arguments, string name = "effect.hlsl")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(arguments);
        var text = Encoding.UTF8.GetBytes(source);
        var words = new List<string> { name, "-E", entry, "-T", target };
        words.AddRange(arguments);
        var wide = words.Select(Wide).ToArray();
        var handles = wide.Select(w => GCHandle.Alloc(w, GCHandleType.Pinned)).ToArray();
        nint result = 0;
        try
        {
            var pointers = handles.Select(h => h.AddrOfPinnedObject()).ToArray();
            int hr;
            fixed (byte* s = text)
            fixed (nint* args = pointers)
            {
                var buffer = new Buffer { Ptr = s, Size = (nuint)text.Length, Encoding = Utf8 };
                var iid = IidResult;
                hr = ((delegate* unmanaged<nint, Buffer*, nint*, uint, nint, Guid*, nint*, int>)Slot(_compiler, 3))(
                    _compiler, &buffer, args, (uint)pointers.Length, 0, &iid, &result);
            }
            if (hr < 0 || result == 0)
                return new DxilOutput(false, [], [], [], $"DXC did not run (0x{hr:x8})");
            int status;
            _ = ((delegate* unmanaged<nint, int*, int>)Slot(result, 3))(result, &status);
            nint errors = 0;
            _ = ((delegate* unmanaged<nint, nint*, int>)Slot(result, 5))(result, &errors);
            var log = Encoding.UTF8.GetString(BlobBytes(errors)).TrimEnd('\0').Trim();
            Release(errors);
            if (status < 0)
                return new DxilOutput(false, [], [], [], log.Length > 0 ? log : $"DXC refused the source (0x{status:x8})");
            var dxil = Output(result, OutObject, out var objectBlob);
            var pdb = Output(result, OutPdb, out var pdbBlob);
            var reflection = Output(result, OutReflection, out var reflectionBlob);
            Release(pdbBlob);
            Release(reflectionBlob);
            var refused = Validate(objectBlob);
            Release(objectBlob);
            return refused is null
                ? new DxilOutput(true, dxil, pdb, reflection, log)
                : new DxilOutput(false, dxil, pdb, reflection, "the validator refused the DXIL: " + refused);
        }
        finally
        {
            Release(result);
            foreach (var h in handles)
                h.Free();
        }
    }

    private static byte[] Output(nint result, int kind, out nint blob)
    {
        nint o = 0, name = 0;
        var iid = IidBlob;
        _ = ((delegate* unmanaged<nint, int, Guid*, nint*, nint*, int>)Slot(result, 7))(result, kind, &iid, &o, &name);
        Release(name);
        blob = o;
        return BlobBytes(o);
    }

    /// <summary>Null when the validator accepts the container; else its words.</summary>
    private string? Validate(nint blob)
    {
        if (blob == 0)
            return "DXC wrote no DXIL";
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, nint, uint, nint*, int>)Slot(_validator, 3))(_validator, blob, 0, &result);
        if (hr < 0 || result == 0)
            return $"the validator did not run (0x{hr:x8})";
        try
        {
            int status;
            _ = ((delegate* unmanaged<nint, int*, int>)Slot(result, 3))(result, &status);
            if (status >= 0)
                return null;
            nint errors = 0;
            _ = ((delegate* unmanaged<nint, nint*, int>)Slot(result, 5))(result, &errors);
            var log = Encoding.UTF8.GetString(BlobBytes(errors)).TrimEnd('\0').Trim();
            Release(errors);
            return log.Length > 0 ? log : $"0x{status:x8}";
        }
        finally
        {
            Release(result);
        }
    }

    /// <summary>Reads a reflection blob (or a container that keeps its reflection); null with words when DXC cannot.</summary>
    public DxilReflection? Reflect(byte[] reflection, out string words)
    {
        ArgumentNullException.ThrowIfNull(reflection);
        nint r = 0;
        int hr;
        fixed (byte* p = reflection)
        {
            var buffer = new Buffer { Ptr = p, Size = (nuint)reflection.Length, Encoding = 0 };
            var iid = IidReflection;
            hr = ((delegate* unmanaged<nint, Buffer*, Guid*, nint*, int>)Slot(_utils, 13))(_utils, &buffer, &iid, &r);
        }
        if (hr < 0 || r == 0)
        {
            words = $"DXC could not read the reflection (0x{hr:x8})";
            return null;
        }
        try
        {
            words = "";
            return ReadReflection(r);
        }
        finally
        {
            Release(r);
        }
    }

    private static DxilReflection ReadReflection(nint r)
    {
        // D3D12_SHADER_DESC: Version, Creator, Flags, then ConstantBuffers and BoundResources.
        var desc = stackalloc byte[512];
        _ = ((delegate* unmanaged<nint, byte*, int>)Slot(r, 3))(r, desc);
        var buffers = *(int*)(desc + 20);
        var bound = *(int*)(desc + 24);
        var bindings = ImmutableArray.CreateBuilder<ReflectedBinding>();
        var b = stackalloc byte[64];
        var bd = stackalloc byte[64];
        var vd = stackalloc byte[64];
        var td = stackalloc byte[64];
        for (var i = 0; i < bound; i++)
        {
            // D3D12_SHADER_INPUT_BIND_DESC: Name, Type, BindPoint, BindCount, uFlags, ReturnType, Dimension, NumSamples, Space.
            if (((delegate* unmanaged<nint, uint, byte*, int>)Slot(r, 6))(r, (uint)i, b) < 0)
                continue;
            var kind = *(int*)(b + 8) switch
            {
                0 => "cbuffer",
                2 => "texture",
                3 => "sampler",
                _ => "other",
            };
            bindings.Add(new ReflectedBinding(Marshal.PtrToStringUTF8(*(nint*)b) ?? "", kind, *(int*)(b + 12), *(int*)(b + 36)));
        }
        var constants = ImmutableArray.CreateBuilder<ReflectedConstant>();
        for (var i = 0; i < buffers; i++)
        {
            // ID3D12ShaderReflectionConstantBuffer, ID3D12ShaderReflectionVariable and ID3D12ShaderReflectionType are not COM
            // objects: their methods start at slot 0 and they are owned by the reflection.
            var cb = ((delegate* unmanaged<nint, uint, nint>)Slot(r, 4))(r, (uint)i);
            if (cb == 0 || ((delegate* unmanaged<nint, byte*, int>)Slot(cb, 0))(cb, bd) < 0)
                continue;
            var buffer = Marshal.PtrToStringUTF8(*(nint*)bd) ?? "";
            var variables = *(int*)(bd + 12);
            for (var v = 0; v < variables; v++)
            {
                var variable = ((delegate* unmanaged<nint, uint, nint>)Slot(cb, 1))(cb, (uint)v);
                if (variable == 0 || ((delegate* unmanaged<nint, byte*, int>)Slot(variable, 0))(variable, vd) < 0)
                    continue;
                var type = ((delegate* unmanaged<nint, nint>)Slot(variable, 1))(variable);
                if (type == 0 || ((delegate* unmanaged<nint, byte*, int>)Slot(type, 0))(type, td) < 0)
                    continue;
                // D3D12_SHADER_VARIABLE_DESC: Name, StartOffset, Size, uFlags (2: used). D3D12_SHADER_TYPE_DESC: Class, Type,
                // Rows, Columns, Elements.
                var elements = *(int*)(td + 16);
                constants.Add(new ReflectedConstant(buffer, Marshal.PtrToStringUTF8(*(nint*)vd) ?? "",
                    TypeWord(*(int*)td, *(int*)(td + 4), *(int*)(td + 8), *(int*)(td + 12)), *(int*)(vd + 8), Math.Max(1, elements),
                    (*(int*)(vd + 16) & 2) != 0));
            }
        }
        return new DxilReflection(constants.ToImmutable(), bindings.ToImmutable());
    }

    /// <summary>A reflected type in <see cref="LayoutConstant"/>'s words: float, floatN, floatNxN, int, intN (bool and uint are
    /// 32-bit ints in a block); "?" for anything else.</summary>
    public static string TypeWord(int typeClass, int type, int rows, int columns)
    {
        var scalar = type switch
        {
            3 => "float",
            1 or 2 or 19 => "int",
            _ => null,
        };
        return (scalar, typeClass) switch
        {
            (null, _) => "?",
            (_, 0) => scalar,
            (_, 1) => columns > 1 ? scalar + columns.ToString(System.Globalization.CultureInfo.InvariantCulture) : scalar,
            ("float", 2 or 3) when rows == columns => $"float{rows}x{columns}",
            _ => "?",
        };
    }

    public void Dispose()
    {
        Release(_validator);
        Release(_utils);
        Release(_compiler);
    }
}
