using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PlayMap.Core.Effects;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>One stage in HLSL: its source, the layout of its default uniform block and images and the varyings it writes, or
/// SPIRV-Cross's words.</summary>
public sealed record HlslStage(bool Ok, string Hlsl, ShaderLayout Layout, string Words, ImmutableArray<StageVarying> Outputs);

/// <summary>A varying a stage writes: its name, its location (the TEXCOORD index its HLSL carries) and its type in HLSL's words.</summary>
public readonly record struct StageVarying(string Name, int Location, string Type);

/// <summary>SPIRV-Cross's C interface: a validated module to HLSL (Shader Model 6.0), with the registers remapped to user effects'
/// root signature — the default uniform block (the ISF inputs, each image's size, rect and flag) at b0, ISF's built-ins at b1,
/// image i at t i, and every image sampled through the one sampler at s0 (linear and clamped, as the GL path samples ISF images),
/// all in space 0 — and the default block's layout read from the module (its members' names as the ISF file wrote them, their
/// offsets as the cbuffer packs them). A vertex stage's position is written in the render hardware interface's clip space, as the
/// GLSL's, and its entry ends with the conversion every Direct3D 12 vertex stage ends with (ClipSpace.ToDirect3D: y negated, then z
/// to (z + w) / 2, SPIRV-Cross's vertex flip and depth fixup), so an effect lands on the rows and depths it lands on in GL. A
/// fragment stage crossed with its vertex stage's outputs gives each input the location of the output of its name, so a varying
/// carries one TEXCOORD semantic in both entries whatever order and subset each file declares (GL links varyings by name); an input
/// no output of its name and type writes is refused with its name. A module
/// that declares any other resource (a storage buffer or image, a separate texture or sampler, a push-constant block, a subpass
/// input, an atomic counter, an acceleration structure) is refused with the resource's name: an ISF effect binds none, and the root
/// signature has no place for it. One context per stage.</summary>
public sealed unsafe partial class SpirvCross
{
    /// <summary>The one sampler every image samples through, at s0.</summary>
    public const string SharedSampler = "PlayMapIsfSampler";

    /// <summary>The resources an ISF effect never binds, by SPIRV-Cross's resource type, with the words that name each.</summary>
    private static readonly (int Type, string Words)[] Refused =
    [
        (2, "a storage buffer"),
        (5, "a subpass input"),
        (6, "a storage image"),
        (8, "an atomic counter"),
        (9, "a push-constant block"),
        (10, "a separate texture"),
        (11, "a separate sampler"),
        (12, "an acceleration structure"),
        (14, "a shader record buffer"),
        (16, "a tensor"),
    ];

    private const int BackendHlsl = 2;
    private const int CaptureTakeOwnership = 1;
    private const int ResourceUniformBuffer = 1;
    private const int ResourceStageInput = 3;
    private const int ResourceStageOutput = 4;
    private const int DecorationLocation = 30;
    private const int ResourceSampledImage = 7;
    private const int DecorationBinding = 33;
    private const int DecorationDescriptorSet = 34;
    private const int OptionHlslShaderModel = 0x400000d;
    private const int OptionEmitLineDirectives = 0x1000025;
    private const int OptionFixupDepthConvention = 0x1000003;
    private const int OptionFlipVertexY = 0x1000004;
    private const int BaseBoolean = 2;
    private const int BaseInt32 = 7;
    private const int BaseUint32 = 8;
    private const int BaseFp16 = 12;
    private const int BaseFp32 = 13;
    private const int BaseFp64 = 14;
    private const int BaseStruct = 15;

    /// <summary>spvc_reflected_resource: the variable, its base type and type, its name.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Resource
    {
        public uint Id;
        public uint BaseTypeId;
        public uint TypeId;
        public byte* Name;
    }

    private readonly delegate* unmanaged[Cdecl]<nint*, int> _createContext;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyContext;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*> _lastError;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, nuint, nint*, int> _parse;
    private readonly delegate* unmanaged[Cdecl]<nint, int, nint, int, nint*, int> _createCompiler;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> _createOptions;
    private readonly delegate* unmanaged[Cdecl]<nint, int, uint, int> _setUint;
    private readonly delegate* unmanaged[Cdecl]<nint, int, byte, int> _setBool;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int> _installOptions;
    private readonly delegate* unmanaged[Cdecl]<nint, byte**, int> _compile;
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, int> _createResources;
    private readonly delegate* unmanaged[Cdecl]<nint, int, Resource**, nuint*, int> _resourceList;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, int, uint> _getDecoration;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, int, uint, void> _setDecoration;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, nint> _typeHandle;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _baseType;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _vectorSize;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _columns;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _arrayDimensions;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint> _arrayDimension;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _memberCount;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint> _memberType;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, uint, byte*> _memberName;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, int> _memberOffset;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, nuint*, int> _structSize;

    private SpirvCross(nint library)
    {
        nint F(string name) => NativeLibrary.GetExport(library, name);
        _createContext = (delegate* unmanaged[Cdecl]<nint*, int>)F("spvc_context_create");
        _destroyContext = (delegate* unmanaged[Cdecl]<nint, void>)F("spvc_context_destroy");
        _lastError = (delegate* unmanaged[Cdecl]<nint, byte*>)F("spvc_context_get_last_error_string");
        _parse = (delegate* unmanaged[Cdecl]<nint, uint*, nuint, nint*, int>)F("spvc_context_parse_spirv");
        _createCompiler = (delegate* unmanaged[Cdecl]<nint, int, nint, int, nint*, int>)F("spvc_context_create_compiler");
        _createOptions = (delegate* unmanaged[Cdecl]<nint, nint*, int>)F("spvc_compiler_create_compiler_options");
        _setUint = (delegate* unmanaged[Cdecl]<nint, int, uint, int>)F("spvc_compiler_options_set_uint");
        _setBool = (delegate* unmanaged[Cdecl]<nint, int, byte, int>)F("spvc_compiler_options_set_bool");
        _installOptions = (delegate* unmanaged[Cdecl]<nint, nint, int>)F("spvc_compiler_install_compiler_options");
        _compile = (delegate* unmanaged[Cdecl]<nint, byte**, int>)F("spvc_compiler_compile");
        _createResources = (delegate* unmanaged[Cdecl]<nint, nint*, int>)F("spvc_compiler_create_shader_resources");
        _resourceList = (delegate* unmanaged[Cdecl]<nint, int, Resource**, nuint*, int>)F("spvc_resources_get_resource_list_for_type");
        _getDecoration = (delegate* unmanaged[Cdecl]<nint, uint, int, uint>)F("spvc_compiler_get_decoration");
        _setDecoration = (delegate* unmanaged[Cdecl]<nint, uint, int, uint, void>)F("spvc_compiler_set_decoration");
        _typeHandle = (delegate* unmanaged[Cdecl]<nint, uint, nint>)F("spvc_compiler_get_type_handle");
        _baseType = (delegate* unmanaged[Cdecl]<nint, int>)F("spvc_type_get_basetype");
        _vectorSize = (delegate* unmanaged[Cdecl]<nint, uint>)F("spvc_type_get_vector_size");
        _columns = (delegate* unmanaged[Cdecl]<nint, uint>)F("spvc_type_get_columns");
        _arrayDimensions = (delegate* unmanaged[Cdecl]<nint, uint>)F("spvc_type_get_num_array_dimensions");
        _arrayDimension = (delegate* unmanaged[Cdecl]<nint, uint, uint>)F("spvc_type_get_array_dimension");
        _memberCount = (delegate* unmanaged[Cdecl]<nint, uint>)F("spvc_type_get_num_member_types");
        _memberType = (delegate* unmanaged[Cdecl]<nint, uint, uint>)F("spvc_type_get_member_type");
        _memberName = (delegate* unmanaged[Cdecl]<nint, uint, uint, byte*>)F("spvc_compiler_get_member_name");
        _memberOffset = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, int>)F("spvc_compiler_type_struct_member_offset");
        _structSize = (delegate* unmanaged[Cdecl]<nint, nint, nuint*, int>)F("spvc_compiler_get_declared_struct_size");
    }

    /// <summary>SPIRV-Cross from its pinned build; null with words when it cannot be loaded.</summary>
    public static SpirvCross? TryLoad(out string words, string? baseDirectory = null)
    {
        if (!CompilerNatives.TryLoad(CompilerNatives.SpirvCross, out var library, out words, baseDirectory))
            return null;
        try
        {
            return new SpirvCross(library);
        }
        catch (EntryPointNotFoundException ex)
        {
            words = "the pinned SPIRV-Cross lacks a call PlayMap makes: " + ex.Message;
            return null;
        }
    }

    /// <summary>One stage's module to HLSL with the registers remapped, its default block's layout and the varyings it writes. With
    /// <paramref name="inputsFrom"/> (the vertex stage's outputs, for a fragment stage) each input takes the location of the output of
    /// its name; an input none of them writes, or writes as another type, is refused with its name.</summary>
    public HlslStage ToHlsl(uint[] spirv, ImmutableArray<StageVarying> inputsFrom = default)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        nint context;
        if (_createContext(&context) != 0)
            return new HlslStage(false, "", ShaderLayout.Empty, "SPIRV-Cross could not make a context", []);
        try
        {
            nint ir, compiler;
            fixed (uint* w = spirv)
            {
                if (_parse(context, w, (nuint)spirv.Length, &ir) != 0)
                    return Failed(context, "the module does not parse");
            }
            if (_createCompiler(context, BackendHlsl, ir, CaptureTakeOwnership, &compiler) != 0)
                return Failed(context, "no HLSL compiler for the module");
            nint options;
            if (_createOptions(compiler, &options) != 0 || _setUint(options, OptionHlslShaderModel, 60) != 0
                || _setBool(options, OptionEmitLineDirectives, 1) != 0 || _setBool(options, OptionFlipVertexY, 1) != 0
                || _setBool(options, OptionFixupDepthConvention, 1) != 0 || _installOptions(compiler, options) != 0)
                return Failed(context, "the HLSL options were refused");
            nint resources;
            if (_createResources(compiler, &resources) != 0)
                return Failed(context, "the module's resources do not read");
            if (Remap(compiler, resources, out var layout) is { } why)
                return new HlslStage(false, "", ShaderLayout.Empty, why, []);
            if (Varyings(compiler, resources, inputsFrom, out var outputs) is { } unmatched)
                return new HlslStage(false, "", ShaderLayout.Empty, unmatched, []);
            byte* source;
            if (_compile(compiler, &source) != 0)
                return Failed(context, "HLSL was not written");
            return new HlslStage(true, ShareSampler(Marshal.PtrToStringUTF8((nint)source) ?? ""), layout, "", outputs);
        }
        finally
        {
            _destroyContext(context);
        }
    }

    private HlslStage Failed(nint context, string what)
    {
        var last = Marshal.PtrToStringUTF8((nint)_lastError(context));
        return new HlslStage(false, "", ShaderLayout.Empty, string.IsNullOrWhiteSpace(last) ? what : $"{what}: {last.Trim()}", []);
    }

    /// <summary>Reads the varyings the stage writes and, with <paramref name="inputsFrom"/>, moves each input to the location of the
    /// output of its name; the reason when an input has no output of its name and type to read.</summary>
    private string? Varyings(nint compiler, nint resources, ImmutableArray<StageVarying> inputsFrom, out ImmutableArray<StageVarying> outputs)
    {
        outputs = [];
        Resource* list;
        nuint count;
        if (_resourceList(resources, ResourceStageOutput, &list, &count) != 0)
            return "the module's outputs do not read";
        var written = ImmutableArray.CreateBuilder<StageVarying>((int)count);
        for (nuint i = 0; i < count; i++)
        {
            var r = list[i];
            written.Add(new StageVarying(Marshal.PtrToStringUTF8((nint)r.Name) ?? "", (int)_getDecoration(compiler, r.Id, DecorationLocation),
                TypeName(_typeHandle(compiler, r.TypeId))));
        }
        outputs = written.ToImmutable();
        if (inputsFrom.IsDefault)
            return null;
        if (_resourceList(resources, ResourceStageInput, &list, &count) != 0)
            return "the module's inputs do not read";
        for (nuint i = 0; i < count; i++)
        {
            var r = list[i];
            var name = Marshal.PtrToStringUTF8((nint)r.Name) ?? "";
            var type = TypeName(_typeHandle(compiler, r.TypeId));
            var from = inputsFrom.FirstOrDefault(v => v.Name == name);
            if (from.Name is null)
                return $"{name}: the fragment shader reads a varying the vertex shader does not write";
            if (from.Type != type)
                return $"{name}: the fragment shader reads it as {type}, the vertex shader writes {from.Type}";
            _setDecoration(compiler, r.Id, DecorationLocation, (uint)from.Location);
        }
        return null;
    }

    /// <summary>A varying's type in HLSL's words: the scalar, the vector size, the columns of a matrix, each array dimension.</summary>
    private string TypeName(nint type)
    {
        var sb = new StringBuilder(_baseType(type) switch
        {
            BaseBoolean => "bool",
            BaseInt32 => "int",
            BaseUint32 => "uint",
            BaseFp16 => "half",
            BaseFp32 => "float",
            BaseFp64 => "double",
            BaseStruct => "struct",
            var other => "type " + other.ToString(CultureInfo.InvariantCulture),
        });
        var size = _vectorSize(type);
        var columns = _columns(type);
        if (size > 1)
            sb.Append(size.ToString(CultureInfo.InvariantCulture));
        if (columns > 1)
            sb.Append('x').Append(columns.ToString(CultureInfo.InvariantCulture));
        var dims = _arrayDimensions(type);
        for (uint d = 0; d < dims; d++)
            sb.Append('[').Append(_arrayDimension(type, d).ToString(CultureInfo.InvariantCulture)).Append(']');
        return sb.ToString();
    }

    /// <summary>Moves every resource to its register and reads the default block's layout; the reason when a resource is not one
    /// the Vulkan target declares.</summary>
    private string? Remap(nint compiler, nint resources, out ShaderLayout layout)
    {
        layout = ShaderLayout.Empty;
        Resource* list;
        nuint count;
        foreach (var (type, words) in Refused)
        {
            if (_resourceList(resources, type, &list, &count) != 0)
                return "the module's resources do not read";
            if (count > 0)
                return $"{Marshal.PtrToStringUTF8((nint)list[0].Name)}: {words} is not a resource an ISF effect binds";
        }
        if (_resourceList(resources, ResourceUniformBuffer, &list, &count) != 0)
            return "the module's uniform blocks do not read";
        var constants = ImmutableArray<LayoutConstant>.Empty;
        var bytes = 0;
        var seenDefault = false;
        for (nuint i = 0; i < count; i++)
        {
            var r = list[i];
            var name = Marshal.PtrToStringUTF8((nint)r.Name) ?? "";
            var type = _typeHandle(compiler, r.BaseTypeId);
            if (Members(compiler, r.BaseTypeId, type, out var members) is { } why)
                return why;
            // The built-ins' block is the one that holds PASSINDEX; the other is the default block glslang gathered the loose
            // uniforms into, at whatever binding it chose.
            if (members.Any(m => m.Name == "PASSINDEX"))
            {
                Place(compiler, r.Id, 1);
                continue;
            }
            if (seenDefault)
                return $"the uniform block {name} is not one an ISF effect declares";
            Place(compiler, r.Id, 0);
            seenDefault = true;
            constants = members;
            nuint size;
            bytes = _structSize(compiler, type, &size) == 0 ? (int)size : 0;
        }
        if (_resourceList(resources, ResourceSampledImage, &list, &count) != 0)
            return "the module's images do not read";
        var textures = ImmutableArray.CreateBuilder<LayoutTexture>();
        for (nuint i = 0; i < count; i++)
        {
            var r = list[i];
            var slot = (int)_getDecoration(compiler, r.Id, DecorationBinding) - IsfVulkanBindings.FirstImage;
            var name = Marshal.PtrToStringUTF8((nint)r.Name) ?? "";
            if (slot is < 0 or >= IsfVulkanBindings.MaxImages)
                return $"the image {name} is not at a binding an ISF effect declares";
            Place(compiler, r.Id, (uint)slot);
            textures.Add(new LayoutTexture(name, slot, true));
        }
        textures.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        layout = new ShaderLayout(constants, textures.ToImmutable(), bytes);
        return null;
    }

    private void Place(nint compiler, uint id, uint register)
    {
        _setDecoration(compiler, id, DecorationDescriptorSet, 0);
        _setDecoration(compiler, id, DecorationBinding, register);
    }

    private string? Members(nint compiler, uint typeId, nint type, out ImmutableArray<LayoutConstant> constants)
    {
        var list = ImmutableArray.CreateBuilder<LayoutConstant>();
        constants = [];
        var n = _memberCount(type);
        for (uint m = 0; m < n; m++)
        {
            var name = Marshal.PtrToStringUTF8((nint)_memberName(compiler, typeId, m)) ?? "";
            var member = _typeHandle(compiler, _memberType(type, m));
            uint offset;
            if (_memberOffset(compiler, type, m, &offset) != 0)
                return $"the uniform {name} has no offset";
            if (TypeWord(member) is not { } word)
                return $"the uniform {name} has a type a user effect does not take";
            var dims = _arrayDimensions(member);
            var length = dims == 0 ? 1 : (int)_arrayDimension(member, 0);
            list.Add(new LayoutConstant(name, word, (int)offset, length));
        }
        constants = list.ToImmutable();
        return null;
    }

    private string? TypeWord(nint type)
    {
        var size = _vectorSize(type);
        var columns = _columns(type);
        var scalar = _baseType(type) switch
        {
            BaseFp32 => "float",
            BaseInt32 or BaseUint32 or BaseBoolean => "int",
            _ => null,
        };
        if (scalar is null || size is 0 or > 4)
            return null;
        if (columns > 1)
            return scalar == "float" && columns == size && size is 3 or 4 ? $"float{size}x{size}" : null;
        var sb = new StringBuilder(scalar);
        if (size > 1)
            sb.Append((char)('0' + size));
        return sb.ToString();
    }

    /// <summary>SPIRV-Cross declares a sampler beside each image; each is made a name for the one shared sampler, declared once at s0,
    /// so the HLSL binds no sampler but s0 however many images the effect reads.</summary>
    public static string ShareSampler(string hlsl)
    {
        ArgumentNullException.ThrowIfNull(hlsl);
        var declared = false;
        return SamplerDeclaration().Replace(hlsl, m =>
        {
            var alias = $"#define {m.Groups[1].Value} {SharedSampler}";
            if (declared)
                return alias;
            declared = true;
            return $"SamplerState {SharedSampler} : register(s0, space0);\n{alias}";
        });
    }

    [GeneratedRegex(@"^SamplerState (\w+) : register\(s\d+, space0\);$", RegexOptions.Multiline)]
    private static partial Regex SamplerDeclaration();
}
