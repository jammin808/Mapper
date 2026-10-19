using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The uniforms a program's GLSL declares, read from its sources: each constant's name, type and element count, and each
/// sampler with whether it is sampled or only loaded (every use the first argument of <c>texelFetch</c> or <c>textureSize</c>). The
/// vertex stage's declarations come first, then the fragment stage's not already declared; one name declared in both stages with two
/// types is refused, as is a type the program table has no word for.</summary>
internal static partial class GlslDeclarations
{
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\buniform\s+(?:(?:highp|mediump|lowp)\s+)?(\w+)\s+([^;{]+);")]
    private static partial Regex Uniform();

    [GeneratedRegex(@"^(\w+)\s*(?:\[\s*(\d+)\s*\])?$")]
    private static partial Regex Declarator();

    public static ProgramLayout Layout(string name, string vertex, string fragment)
    {
        var constants = new List<(string Name, ConstantType Type, int Count)>();
        var samplers = new List<string>();
        var stages = new[] { Comments().Replace(vertex, " "), Comments().Replace(fragment, " ") };
        foreach (var source in stages)
        {
            foreach (Match m in Uniform().Matches(source))
            {
                var type = m.Groups[1].Value;
                foreach (var part in m.Groups[2].Value.Split(','))
                {
                    var d = Declarator().Match(part.Trim());
                    if (!d.Success)
                        throw new InvalidOperationException($"Program '{name}': '{part.Trim()}' is no uniform declarator.");
                    var uniform = d.Groups[1].Value;
                    var count = d.Groups[2].Success ? int.Parse(d.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                    if (type == "sampler2D")
                    {
                        if (constants.Any(c => c.Name == uniform))
                            throw new InvalidOperationException($"Program '{name}' declares '{uniform}' as a constant and a sampler.");
                        if (!samplers.Contains(uniform))
                            samplers.Add(uniform);
                        continue;
                    }
                    var t = Type(name, type);
                    var before = constants.FindIndex(c => c.Name == uniform);
                    if (before >= 0)
                    {
                        if (constants[before] != (uniform, t, count))
                            throw new InvalidOperationException($"Program '{name}' declares '{uniform}' twice, two ways.");
                        continue;
                    }
                    if (samplers.Contains(uniform))
                        throw new InvalidOperationException($"Program '{name}' declares '{uniform}' as a sampler and a constant.");
                    constants.Add((uniform, t, count));
                }
            }
        }
        var textures = samplers.Select(s => (s, Sampled(s, stages))).ToList();
        return ProgramLayout.Pack(name, constants, textures);
    }

    private static ConstantType Type(string program, string glsl) => glsl switch
    {
        "float" => ConstantType.Float,
        "vec2" => ConstantType.Float2,
        "vec3" => ConstantType.Float3,
        "vec4" => ConstantType.Float4,
        "int" => ConstantType.Int,
        "ivec2" => ConstantType.Int2,
        "ivec4" => ConstantType.Int4,
        "mat3" => ConstantType.Float3x3,
        "mat4" => ConstantType.Float4x4,
        _ => throw new InvalidOperationException($"Program '{program}' declares a uniform of type {glsl}, which the program table has no word for."),
    };

    /// <summary>False only when the sampler is used and every use is the first argument of texelFetch or textureSize.</summary>
    private static bool Sampled(string sampler, string[] stages)
    {
        var uses = 0;
        var loads = 0;
        var use = new Regex(@"\b" + Regex.Escape(sampler) + @"\b");
        foreach (var source in stages)
        {
            var body = Uniform().Replace(source, " ");
            foreach (Match m in use.Matches(body))
            {
                uses++;
                var before = body[..m.Index].TrimEnd();
                if (before.EndsWith("texelFetch(", StringComparison.Ordinal) || before.EndsWith("textureSize(", StringComparison.Ordinal))
                    loads++;
            }
        }
        return uses == 0 || loads < uses;
    }
}
