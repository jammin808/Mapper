using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The uniforms a program's GLSL declares, read from its sources: each constant's name, type and element count, and each
/// sampler with whether it is sampled or only loaded (every use a load: the first argument of <c>texelFetch</c> or <c>textureSize</c>,
/// or an argument of a function the stage defines whose sampler parameter in that place is itself only loaded). The vertex stage's
/// declarations come first, then the fragment stage's not already declared; one name declared in both stages with two types is
/// refused, as is a type the program table has no word for.</summary>
internal static partial class GlslDeclarations
{
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\buniform\s+(?:(?:highp|mediump|lowp)\s+)?(\w+)\s+([^;{]+);")]
    private static partial Regex Uniform();

    [GeneratedRegex(@"^(\w+)\s*(?:\[\s*(\d+)\s*\])?$")]
    private static partial Regex Declarator();

    [GeneratedRegex(@"\b\w+\s+(\w+)\s*\(([^()]*)\)\s*\{")]
    private static partial Regex FunctionHead();

    [GeneratedRegex(@"\blayout\s*\(\s*location\s*=\s*(\d+)\s*\)\s*in\s+(?:(?:highp|mediump|lowp)\s+)?(float|vec2|vec3|vec4)\s+\w+\s*;")]
    private static partial Regex VertexInput();

    /// <summary>The vertex inputs a vertex stage declares (<c>layout(location = n) in vecK name;</c>): each location and its float
    /// count, in location order.</summary>
    public static List<(int Location, int Components)> VertexInputs(string vertex) =>
        [.. VertexInput().Matches(Comments().Replace(vertex, " "))
            .Select(m => (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                m.Groups[2].Value == "float" ? 1 : m.Groups[2].Value[^1] - '0'))
            .OrderBy(i => i.Item1)];

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

    /// <summary>False only when the sampler is used and every use is a load (<see cref="Uses"/>), in either stage.</summary>
    private static bool Sampled(string sampler, string[] stages)
    {
        var uses = 0;
        var loads = 0;
        foreach (var source in stages)
        {
            var body = Uniform().Replace(source, " ");
            var (u, l) = Uses(sampler, body, LoadOnlyParameters(body));
            uses += u;
            loads += l;
        }
        return uses == 0 || loads < uses;
    }

    /// <summary>How often a sampler is named in a text (a swizzle of that letter is no use), and how many of those are loads: the
    /// first argument of texelFetch or textureSize, or the argument in a place a helper of <paramref name="helpers"/> only loads.</summary>
    private static (int Uses, int Loads) Uses(string sampler, string text, Dictionary<string, HashSet<int>> helpers)
    {
        var uses = 0;
        var loads = 0;
        foreach (Match m in new Regex(@"(?<!\.)\b" + Regex.Escape(sampler) + @"\b").Matches(text))
        {
            uses++;
            if (ArgumentOf(text, m.Index) is { } call
                && ((call.Place == 0 && call.Function is "texelFetch" or "textureSize")
                    || (helpers.TryGetValue(call.Function, out var only) && only.Contains(call.Place))))
                loads++;
        }
        return (uses, loads);
    }

    /// <summary>Each function a stage defines, with the places of its sampler parameters that it only loads. GLSL defines a function
    /// before any call to it, so a helper reads the helpers defined above it.</summary>
    private static Dictionary<string, HashSet<int>> LoadOnlyParameters(string body)
    {
        var helpers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (Match m in FunctionHead().Matches(body))
        {
            var open = m.Index + m.Length - 1;
            var inside = body[(open + 1)..Close(body, open)];
            var parameters = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries);
            var only = new HashSet<int>();
            for (var i = 0; i < parameters.Length; i++)
            {
                var words = parameters[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2 || words[^2] != "sampler2D")
                    continue;
                var (uses, loads) = Uses(words[^1], inside, helpers);
                if (uses > 0 && loads == uses)
                    only.Add(i);
            }
            helpers[m.Groups[1].Value] = only;
        }
        return helpers;
    }

    /// <summary>The call whose argument list holds the text at <paramref name="at"/> as one of its arguments: the function's name and
    /// the argument's place (0 the first); null when the text is no argument of a call.</summary>
    private static (string Function, int Place)? ArgumentOf(string text, int at)
    {
        var depth = 0;
        var place = 0;
        for (var i = at - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is ')' or ']')
            {
                depth++;
            }
            else if (c is '(' or '[')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                if (c == '[')
                    return null;
                var end = i;
                while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                    end--;
                var start = end;
                while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
                    start--;
                return start == end ? null : (text[start..end], place);
            }
            else if (depth == 0 && c == ',')
            {
                place++;
            }
            else if (depth == 0 && c is ';' or '{' or '}')
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The index of the brace that closes the one at <paramref name="open"/>.</summary>
    private static int Close(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i;
        }
        throw new InvalidOperationException($"The brace at {open} never closes.");
    }
}
