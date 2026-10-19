using System.Collections.Immutable;
using System.Text.Json;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>A constant of a program's block: its name, its type in HLSL's words (float, float2, float3, float4, int, int2, int3,
/// int4, float3x3, float4x4; a bool or uint reaches the block as a 32-bit int), its byte offset as HLSL's cbuffer packs it, and
/// its array length (1 for none).</summary>
public readonly record struct LayoutConstant(string Name, string Type, int Offset, int Count);

/// <summary>A texture a program reads: its name, its slot (0–15), whether it is sampled or only loaded.</summary>
public readonly record struct LayoutTexture(string Name, int Slot, bool Sampled);

/// <summary>What a program takes, in the shape of the RHI's program layout (its constants packed in one block, its textures by
/// slot, the block's size in bytes): what the shader worker answers for a user effect, and what the HLSL library's reflection is
/// held equal to.</summary>
public sealed record ShaderLayout(ImmutableArray<LayoutConstant> Constants, ImmutableArray<LayoutTexture> Textures, int ConstantBytes)
{
    public static readonly ShaderLayout Empty = new([], [], 0);

    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteStartArray("constants");
        foreach (var c in Constants)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("type", c.Type);
            w.WriteNumber("offset", c.Offset);
            w.WriteNumber("count", c.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("textures");
        foreach (var t in Textures)
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteNumber("slot", t.Slot);
            w.WriteBoolean("sampled", t.Sampled);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("constantBytes", ConstantBytes);
        w.WriteEndObject();
    }

    /// <summary>Reads a layout written by <see cref="Write"/>; null when the element is not one.</summary>
    public static ShaderLayout? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            var constants = e.GetProperty("constants").EnumerateArray()
                .Select(c => new LayoutConstant(c.GetProperty("name").GetString() ?? "", c.GetProperty("type").GetString() ?? "",
                    c.GetProperty("offset").GetInt32(), c.GetProperty("count").GetInt32()))
                .ToImmutableArray();
            var textures = e.GetProperty("textures").EnumerateArray()
                .Select(t => new LayoutTexture(t.GetProperty("name").GetString() ?? "", t.GetProperty("slot").GetInt32(), t.GetProperty("sampled").GetBoolean()))
                .ToImmutableArray();
            return new ShaderLayout(constants, textures, e.GetProperty("constantBytes").GetInt32());
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
