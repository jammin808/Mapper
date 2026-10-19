using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;

namespace PlayMap.Core.Gpu;

/// <summary>A program of the library by index: resolved once, at a pass's construction, from its name.</summary>
public readonly record struct ProgramId(int Value);

/// <summary>A constant's type, the same on every backend (GLSL's float, vec2–4, int, ivec2, ivec4, mat3, mat4; HLSL's float…float4x4).</summary>
public enum ConstantType
{
    Float,
    Float2,
    Float3,
    Float4,
    Int,
    Int2,
    Int4,
    Float3x3,
    Float4x4,
}

/// <summary>A constant of a program's block: its name, type, byte offset in the block and element count (an array's elements each
/// start a 16-byte register).</summary>
public readonly record struct ConstantSlot(string Name, ConstantType Type, int Offset, int Count = 1);

/// <summary>A texture the program reads: its name, its slot (0–15), and whether it is sampled or only loaded.</summary>
public readonly record struct TextureSlot(string Name, int Slot, bool Sampled);

/// <summary>Sizes and words of the constant types, and the cbuffer packing rules both backends read a block by.</summary>
public static class ConstantTypes
{
    /// <summary>Bytes one element takes: 4 a scalar, 8–16 a vector, 44 a 3×3 matrix (three rows of three, each row starting a
    /// 16-byte register), 64 a 4×4 matrix (four rows of four).</summary>
    public static int Bytes(ConstantType type) => type switch
    {
        ConstantType.Float or ConstantType.Int => 4,
        ConstantType.Float2 or ConstantType.Int2 => 8,
        ConstantType.Float3 => 12,
        ConstantType.Float4 or ConstantType.Int4 => 16,
        ConstantType.Float3x3 => 44,
        ConstantType.Float4x4 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a constant type"),
    };

    /// <summary>Scalars a row holds: 1–4 for a scalar or vector, 3 or 4 for a matrix's row.</summary>
    public static int Components(ConstantType type) => type switch
    {
        ConstantType.Float or ConstantType.Int => 1,
        ConstantType.Float2 or ConstantType.Int2 => 2,
        ConstantType.Float3 or ConstantType.Float3x3 => 3,
        _ => 4,
    };

    /// <summary>Rows a type takes, each in its own 16-byte register past the first: 3 or 4 for a matrix, 1 otherwise.</summary>
    public static int Rows(ConstantType type) => type switch
    {
        ConstantType.Float3x3 => 3,
        ConstantType.Float4x4 => 4,
        _ => 1,
    };

    public static bool IsInt(ConstantType type) => type is ConstantType.Int or ConstantType.Int2 or ConstantType.Int4;

    public static bool IsMatrix(ConstantType type) => type is ConstantType.Float3x3 or ConstantType.Float4x4;

    /// <summary>The bytes a constant of <paramref name="count"/> elements takes: every element but the last a whole number of
    /// registers.</summary>
    public static int Bytes(ConstantType type, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var element = Bytes(type);
        return count == 1 ? element : (count - 1) * Register(element) + element;
    }

    /// <summary>The type's word in the program table and in HLSL: float, float2, float3, float4, int, int2, int4, float3x3,
    /// float4x4.</summary>
    public static string Word(ConstantType type) => type switch
    {
        ConstantType.Float => "float",
        ConstantType.Float2 => "float2",
        ConstantType.Float3 => "float3",
        ConstantType.Float4 => "float4",
        ConstantType.Int => "int",
        ConstantType.Int2 => "int2",
        ConstantType.Int4 => "int4",
        ConstantType.Float3x3 => "float3x3",
        ConstantType.Float4x4 => "float4x4",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a constant type"),
    };

    /// <summary>The type of a word <see cref="Word"/> writes; false for any other.</summary>
    public static bool TryParse(string word, out ConstantType type)
    {
        foreach (var t in Enum.GetValues<ConstantType>())
        {
            if (string.Equals(Word(t), word, StringComparison.Ordinal))
            {
                type = t;
                return true;
            }
        }
        type = default;
        return false;
    }

    /// <summary>Rounds up to a whole number of 16-byte registers.</summary>
    internal static int Register(int bytes) => (bytes + 15) & ~15;
}

/// <summary>What a program takes, the same on every backend: its constants packed in one block (16-byte rules, as an HLSL
/// cbuffer packs them), its textures by slot. The GL backend sets the block's members as uniforms of the same names.
///
/// The block's layout: a scalar or vector sits at the next offset where it does not cross a 16-byte register; a matrix, and every
/// element of an array, starts a register; a matrix's rows each start a register (row r at offset + 16·r, the System.Numerics
/// row order); what follows packs into the space after the last row or element. <see cref="ConstantBytes"/> is the block rounded up
/// to a whole register.</summary>
public sealed record ProgramLayout(string Name, ImmutableArray<ConstantSlot> Constants, ImmutableArray<TextureSlot> Textures,
    int ConstantBytes)
{
    private readonly FrozenDictionary<string, ConstantSlot> _constants = IndexConstants(Constants.IsDefault ? [] : Constants);
    private readonly FrozenDictionary<string, TextureSlot> _textures =
        (Textures.IsDefault ? [] : Textures).ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);

    public string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));
    public ImmutableArray<ConstantSlot> Constants { get; } = Constants.IsDefault ? [] : Constants;
    public ImmutableArray<TextureSlot> Textures { get; } = Textures.IsDefault ? [] : Textures;
    public int ConstantBytes { get; } = ConstantBytes;

    /// <summary>The slot of a constant by name, from a frozen table built once: no allocation on look-up. An array answers by its
    /// name (element 0, with its count) and by each element's name (<c>uColors[3]</c>: that element's offset, a count of 1).</summary>
    public bool TryConstant(string name, out ConstantSlot slot) => _constants.TryGetValue(name, out slot);

    /// <summary>The slot of a texture by name; no allocation.</summary>
    public bool TryTexture(string name, out TextureSlot slot) => _textures.TryGetValue(name, out slot);

    /// <summary>Lays declared constants out in the order given by the block's rules, and numbers the textures 0, 1, 2… in the order
    /// given (at most 16). A name declared twice, or a constant and a texture of one name, is refused.</summary>
    public static ProgramLayout Pack(string name, IEnumerable<(string Name, ConstantType Type, int Count)> constants,
        IEnumerable<(string Name, bool Sampled)> textures)
    {
        ArgumentNullException.ThrowIfNull(constants);
        ArgumentNullException.ThrowIfNull(textures);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var slots = ImmutableArray.CreateBuilder<ConstantSlot>();
        var offset = 0;
        foreach (var (n, type, count) in constants)
        {
            if (!names.Add(n))
                throw new ArgumentException($"Program '{name}' declares '{n}' twice.", nameof(constants));
            var bytes = ConstantTypes.Bytes(type, count);
            if (count > 1 || ConstantTypes.IsMatrix(type) || (offset & 15) + bytes > 16)
                offset = ConstantTypes.Register(offset);
            slots.Add(new ConstantSlot(n, type, offset, count));
            offset += bytes;
        }
        var units = ImmutableArray.CreateBuilder<TextureSlot>();
        foreach (var (n, sampled) in textures)
        {
            if (!names.Add(n))
                throw new ArgumentException($"Program '{name}' declares '{n}' twice.", nameof(textures));
            if (units.Count == 16)
                throw new ArgumentException($"Program '{name}' reads more than 16 textures.", nameof(textures));
            units.Add(new TextureSlot(n, units.Count, sampled));
        }
        return new ProgramLayout(name, slots.ToImmutable(), units.ToImmutable(), ConstantTypes.Register(offset));
    }

    /// <summary>Two layouts are equal when they hold the same name, block size, constants and textures, in the same order.</summary>
    public bool Equals(ProgramLayout? other) =>
        other is not null && Name == other.Name && ConstantBytes == other.ConstantBytes
        && Constants.SequenceEqual(other.Constants) && Textures.SequenceEqual(other.Textures);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Name, StringComparer.Ordinal);
        h.Add(ConstantBytes);
        foreach (var c in Constants)
            h.Add(c);
        foreach (var t in Textures)
            h.Add(t);
        return h.ToHashCode();
    }

    private static FrozenDictionary<string, ConstantSlot> IndexConstants(ImmutableArray<ConstantSlot> constants)
    {
        var d = new Dictionary<string, ConstantSlot>(StringComparer.Ordinal);
        foreach (var c in constants)
        {
            d[c.Name] = c;
            if (c.Count <= 1)
                continue;
            var stride = ConstantTypes.Register(ConstantTypes.Bytes(c.Type));
            for (var i = 0; i < c.Count; i++)
                d[string.Create(CultureInfo.InvariantCulture, $"{c.Name}[{i}]")] = new ConstantSlot(c.Name, c.Type, c.Offset + i * stride);
        }
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

/// <summary>The programs a device draws: the product programs by name (the program table) and the user programs it made.</summary>
public interface IProgramLibrary
{
    /// <summary>A product program; throws with words for an unknown name (at construction, never mid-frame).</summary>
    ProgramId Id(string name);

    /// <summary>Product and user programs alike.</summary>
    ProgramLayout Layout(ProgramId id);

    /// <summary>The product programs, in the table's order.</summary>
    IReadOnlyList<string> Names { get; }
}

/// <summary>A program made at run time — a user's ISF effect, one entry per pass. GL takes GLSL text (UTF-8); Direct3D 12 takes the
/// shader worker's DXIL and its reflection blob. The kind's hash keys the quarantine and the pipeline cache.</summary>
public sealed record ProgramSource(string Name, string KindHash, ProgramCode Code, ReadOnlyMemory<byte> Vertex,
    ReadOnlyMemory<byte> Pixel, ReadOnlyMemory<byte> Reflection);

/// <summary>What a <see cref="ProgramSource"/> carries.</summary>
public enum ProgramCode
{
    Glsl,
    Dxil,
}

/// <summary>What a make answered: the program and its reflected layout, or the stage that refused and its words.</summary>
public sealed record ProgramMade(bool Ok, ProgramId Id, ProgramLayout? Layout, string Stage, string Words);
