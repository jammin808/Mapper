using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Render.Shaders;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>A program's layout read from the GLSL library's declarations (the stand-in for the RHI's program table until wp3-rhi
/// lands it, in the same shape): its uniforms in declaration order (the vertex stage's, then the fragment stage's not declared there
/// already) packed as an HLSL cbuffer packs them — a value never straddles a 16-byte register, a matrix or an array starts one and
/// each array element takes one, a row_major float3x3 is three registers less its last pad — and its samplers by slot in the same
/// order, each sampled when the GLSL calls texture() on it, else only loaded (texelFetch, textureSize).</summary>
internal static partial class GlslLayout
{
    public sealed record Program(string Name, ShaderLayout Layout);

    private static readonly Dictionary<string, (string Hlsl, int Size, bool Register)> Types = new(StringComparer.Ordinal)
    {
        ["float"] = ("float", 4, false),
        ["vec2"] = ("float2", 8, false),
        ["vec3"] = ("float3", 12, false),
        ["vec4"] = ("float4", 16, false),
        ["int"] = ("int", 4, false),
        ["ivec2"] = ("int2", 8, false),
        ["ivec3"] = ("int3", 12, false),
        ["ivec4"] = ("int4", 16, false),
        ["bool"] = ("int", 4, false),
        ["mat3"] = ("float3x3", 44, true),
        ["mat4"] = ("float4x4", 64, true),
    };

    public static ImmutableArray<Program> All() => [.. ShaderLibrary.Names.Select(Of)];

    public static Program Of(string name)
    {
        var (vs, fs) = ShaderLibrary.Source(name);
        var declared = new List<(string Type, string Name, int Count)>();
        foreach (var source in new[] { vs, fs })
            foreach (Match m in Uniform().Matches(StripComments(source)))
                if (declared.All(d => d.Name != m.Groups[2].Value))
                    declared.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0));
        var constants = ImmutableArray.CreateBuilder<LayoutConstant>();
        var textures = ImmutableArray.CreateBuilder<LayoutTexture>();
        var offset = 0;
        var all = vs + "\n" + fs;
        foreach (var (type, uniform, count) in declared)
        {
            if (type == "sampler2D")
            {
                var sampled = Regex.IsMatch(all, @"\btexture(Lod|Grad|Offset)?\s*\(\s*" + Regex.Escape(uniform) + @"\b");
                textures.Add(new LayoutTexture(uniform, textures.Count, sampled));
                continue;
            }
            if (!Types.TryGetValue(type, out var t))
                throw new InvalidOperationException($"{name}: the uniform {uniform} has a type ({type}) the program table does not carry");
            var register = t.Register || count > 0;
            if (register || (offset % 16) + t.Size > 16)
                offset = (offset + 15) / 16 * 16;
            constants.Add(new LayoutConstant(uniform, t.Hlsl, offset, Math.Max(1, count)));
            offset += count > 0 ? (count - 1) * 16 + t.Size : t.Size;
        }
        return new Program(name, new ShaderLayout(constants.ToImmutable(), textures.ToImmutable(), (offset + 15) / 16 * 16));
    }

    private static string StripComments(string s) => LineComment().Replace(BlockComment().Replace(s, " "), "");

    /// <summary>The table as its file holds it: one program a block, one constant or texture a line, so the build's MSBuild step
    /// reads it line by line without a JSON reader.</summary>
    public static string Json(IEnumerable<Program> programs)
    {
        var sb = new StringBuilder("{\n  \"programs\": [\n");
        var list = programs.ToList();
        for (var p = 0; p < list.Count; p++)
        {
            var (name, layout) = (list[p].Name, list[p].Layout);
            sb.Append("    {\n      \"name\": \"").Append(name).Append("\",\n      \"constantBytes\": ")
                .Append(layout.ConstantBytes.ToString(CultureInfo.InvariantCulture)).Append(",\n      \"constants\": [");
            for (var c = 0; c < layout.Constants.Length; c++)
            {
                var k = layout.Constants[c];
                sb.Append(c == 0 ? "\n" : ",\n").Append(CultureInfo.InvariantCulture,
                    $"        {{ \"name\": \"{k.Name}\", \"type\": \"{k.Type}\", \"offset\": {k.Offset}, \"count\": {k.Count} }}");
            }
            sb.Append(layout.Constants.IsEmpty ? "],\n" : "\n      ],\n").Append("      \"textures\": [");
            for (var t = 0; t < layout.Textures.Length; t++)
            {
                var x = layout.Textures[t];
                sb.Append(t == 0 ? "\n" : ",\n").Append(CultureInfo.InvariantCulture,
                    $"        {{ \"name\": \"{x.Name}\", \"slot\": {x.Slot}, \"sampled\": {(x.Sampled ? "true" : "false")} }}");
            }
            sb.Append(layout.Textures.IsEmpty ? "]\n" : "\n      ]\n").Append(p < list.Count - 1 ? "    },\n" : "    }\n");
        }
        return sb.Append("  ]\n}\n").ToString();
    }

    [GeneratedRegex(@"\buniform\s+(\w+)\s+(\w+)\s*(?:\[\s*(\d+)\s*\])?\s*;")]
    private static partial Regex Uniform();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();
}
