using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayMap.Core.Effects;

/// <summary>What GLSL ES 3.00 refuses that desktop GLSL lets through, put right before the body: a shader that defines a
/// function with a built-in's name (ES forbids redefining or overloading a built-in) has that name renamed throughout to
/// isf_user_&lt;name&gt;, by a #define ahead of the body. Only definitions count — a name in a comment or a call is left alone.</summary>
public static partial class IsfEsFixups
{
    private static readonly FrozenSet<string> Builtins = new[]
    {
        "radians", "degrees", "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",
        "pow", "exp", "log", "exp2", "log2", "sqrt", "inversesqrt", "abs", "sign", "floor", "trunc", "round", "roundEven", "ceil",
        "fract", "mod", "modf", "min", "max", "clamp", "mix", "step", "smoothstep", "isnan", "isinf", "length", "distance", "dot",
        "cross", "normalize", "faceforward", "reflect", "refract", "matrixCompMult", "outerProduct", "transpose", "determinant",
        "inverse", "lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "equal", "notEqual", "any", "all", "not",
        "texture", "textureSize", "textureProj", "textureLod", "textureOffset", "texelFetch", "textureGrad", "dFdx", "dFdy", "fwidth",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The #define lines that rename the body's own definitions of built-in names ("" when there are none).</summary>
    public static string Renames(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var code = StripComments(body);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Definition().Matches(code))
        {
            var name = m.Groups[1].Value;
            if (Builtins.Contains(name) && IsDefinition(code, m.Index + m.Length))
                names.Add(name);
        }
        if (names.Count == 0)
            return "";
        var sb = new StringBuilder();
        foreach (var n in names)
            sb.Append("#define ").Append(n).Append(" isf_user_").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>True when the '(' just read opens a parameter list followed by a function body or a prototype's ';'.</summary>
    private static bool IsDefinition(string code, int afterParen)
    {
        var depth = 0;
        for (var i = afterParen; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    var j = i + 1;
                    while (j < code.Length && char.IsWhiteSpace(code[j]))
                        j++;
                    return j < code.Length && code[j] is '{' or ';';
                }
                depth--;
            }
        }
        return false;
    }

    /// <summary>The code with every comment blanked to spaces (newlines kept).</summary>
    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }
            }
            else if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                for (; i < end; i++)
                    sb.Append(s[i] == '\n' ? '\n' : ' ');
            }
            else
            {
                sb.Append(s[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\b(?:float|int|uint|bool|void|vec[234]|ivec[234]|uvec[234]|bvec[234]|mat[234])\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex Definition();
}
