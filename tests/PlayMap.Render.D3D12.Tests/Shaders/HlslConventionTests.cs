using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The render hardware interface's conventions held in the HLSL library's source, on both jobs: PmClip computes
/// ClipSpace.ToDirect3D's numbers, and every vertex entry writes its position through it and nothing else; each matrix constant of
/// the program table is multiplied the way its type is read (a Float4x4 as mul(v, M), a Float3x3 as mul(M, v)) wherever a program
/// names it; and a vertex entry takes each of the table's inputs at ATTR&lt;location&gt; with the table's float count, and no other.</summary>
public sealed partial class HlslConventionTests
{
    private static readonly IReadOnlyList<HlslLibrary.Entry> Programs = HlslLibrary.Programs();

    private static string Common => File.ReadAllText(Path.Combine(HlslLibrary.Folder, "Common.hlsli"));

    /// <summary>A program's code with its comments taken out: the shared header, then its family file's shared code and its block.</summary>
    private static string CodeOf(HlslLibrary.Entry e) => LineComment().Replace(Common + "\n" + HlslLibrary.BlockOf(e), "");

    [Fact]
    public void PmClipComputesClipSpaceToDirect3DsNumbers()
    {
        var m = PmClipBody().Match(Common);
        Assert.True(m.Success, "Common.hlsli defines float4 PmClip(float4 c) { return …; }");
        var parameter = m.Groups["p"].Value;
        var expression = m.Groups["e"].Value;
        Vector4[] samples =
        [
            new(0, 0, 0, 1), new(-1, -1, -1, 1), new(1, 1, 1, 1), new(0.25f, -0.75f, 0.5f, 1),
            new(-3, 2, -1.5f, 2), new(5, -4, 3, 6), new(0.125f, 0.375f, -2.5f, 3),
        ];
        foreach (var clip in samples)
            Assert.Equal(ClipSpace.ToDirect3D(clip), new HlslExpression(expression, parameter, clip).Float4());
    }

    [Fact]
    public void EveryVertexEntryWritesItsPositionThroughPmClipAndNothingElse()
    {
        Assert.Equal(40, Programs.Count);
        foreach (var e in Programs)
        {
            var code = CodeOf(e);
            var (returns, _, body) = Function(code, e.Vs, e.Name);
            var position = PositionMember(code, returns, e.Name);
            var writes = Writes(position).Matches(body);
            Assert.True(writes.Count > 0, $"{e.Name}: {e.Vs} writes no {returns}.{position}");
            foreach (Match w in writes)
                Assert.True(w.Groups["part"].Length == 0 && w.Groups["op"].Value == "="
                    && w.Groups["rhs"].Value.StartsWith("PmClip(", StringComparison.Ordinal),
                    $"{e.Name}: {e.Vs} writes its position other than through PmClip: {w.Value.Trim()}");
        }
    }

    [Fact]
    public void EachMatrixConstantIsMultipliedTheWayItsTypeIsRead()
    {
        var checkedMatrices = 0;
        foreach (var layout in ProgramTable.Product)
        {
            var matrices = layout.Constants.Where(c => c.Type is ConstantType.Float4x4 or ConstantType.Float3x3).ToList();
            if (matrices.Count == 0)
                continue;
            var e = Assert.Single(Programs, p => p.Name == layout.Name);
            var code = CodeOf(e);
            foreach (var matrix in matrices)
            {
                var uses = Regex.Count(code, $@"\b{Regex.Escape(matrix.Name)}\b");
                var muls = Muls(code).Where(a => a.Contains(matrix.Name)).ToList();
                Assert.True(uses > 0, $"{layout.Name}: {matrix.Name} is never read");
                Assert.True(muls.Count == uses, $"{layout.Name}: {matrix.Name} is read {uses} times, {muls.Count} of them in a mul");
                // A Float4x4 holds a row-vector transform (v·M), a Float3x3 a column-vector one (M·v): ConstantType.
                var at = matrix.Type == ConstantType.Float4x4 ? 1 : 0;
                foreach (var args in muls)
                    Assert.True(args.Length == 2 && args[at] == matrix.Name,
                        $"{layout.Name}: {matrix.Name} ({matrix.Type}) is mul({string.Join(", ", args)}); it is argument {at + 1}");
                checkedMatrices++;
            }
        }
        Assert.Equal(5, checkedMatrices);
    }

    [Fact]
    public void EachVertexEntryTakesTheTablesInputsAtTheirAttrLocations()
    {
        var withInputs = 0;
        foreach (var layout in ProgramTable.Product)
        {
            var e = Assert.Single(Programs, p => p.Name == layout.Name);
            var code = CodeOf(e);
            var (_, parameters, _) = Function(code, e.Vs, e.Name);
            var attributes = new SortedDictionary<int, int>();
            foreach (var parameter in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var words = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var members = StructBody(code, words[0]) is { } fields ? fields : parameter + ";";
                foreach (Match a in Attribute().Matches(members))
                {
                    var n = a.Groups["n"].Value;
                    attributes.Add(int.Parse(a.Groups["loc"].Value, CultureInfo.InvariantCulture),
                        n.Length == 0 ? 1 : int.Parse(n, CultureInfo.InvariantCulture));
                }
            }
            var expected = new SortedDictionary<int, int>(layout.Inputs.ToDictionary(i => i.Location, i => i.Components));
            Assert.True(expected.SequenceEqual(attributes),
                $"{layout.Name}: {e.Vs} takes {Words(attributes)}; the table's inputs are {Words(expected)}");
            if (expected.Count > 0)
                withInputs++;
        }
        Assert.Equal(6, withInputs);
    }

    private static string Words(SortedDictionary<int, int> attributes) =>
        attributes.Count == 0 ? "none" : string.Join(", ", attributes.Select(a => $"ATTR{a.Key} float{a.Value}"));

    /// <summary>A function's return type, parameters and body (its text between the braces).</summary>
    private static (string Returns, string Parameters, string Body) Function(string code, string name, string program)
    {
        var m = Regex.Match(code, $@"\b(?<ret>\w+)\s+{Regex.Escape(name)}\s*\((?<params>[^)]*)\)\s*(:\s*\w+\s*)?\{{");
        Assert.True(m.Success, $"{program}: no function {name}");
        var open = m.Index + m.Length - 1;
        return (m.Groups["ret"].Value, m.Groups["params"].Value, code[(open + 1)..Close(code, open, '{', '}')]);
    }

    /// <summary>The member of a struct that carries SV_Position.</summary>
    private static string PositionMember(string code, string type, string program)
    {
        var fields = StructBody(code, type);
        Assert.True(fields is not null, $"{program}: no struct {type}");
        var m = SvPosition().Match(fields);
        Assert.True(m.Success, $"{program}: {type} has no SV_Position member");
        return m.Groups["name"].Value;
    }

    private static string? StructBody(string code, string type)
    {
        var m = Regex.Match(code, $@"\bstruct\s+{Regex.Escape(type)}\s*\{{");
        if (!m.Success)
            return null;
        var open = m.Index + m.Length - 1;
        return code[(open + 1)..Close(code, open, '{', '}')];
    }

    /// <summary>Every write to a member of that name: the whole member or a part of it (a swizzle), by any assignment.</summary>
    private static Regex Writes(string member) =>
        new($@"\.\s*{Regex.Escape(member)}\b(?<part>\s*\.\s*\w+)?\s*(?<op>[-+*/]?=)(?!=)\s*(?<rhs>[^;]*);");

    /// <summary>The arguments of every mul call, each trimmed, split at the call's own commas.</summary>
    private static IEnumerable<string[]> Muls(string code)
    {
        foreach (Match m in MulCall().Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var inner = code[(open + 1)..Close(code, open, '(', ')')];
            var args = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                depth += inner[i] == '(' ? 1 : inner[i] == ')' ? -1 : 0;
                if (inner[i] == ',' && depth == 0)
                {
                    args.Add(inner[start..i].Trim());
                    start = i + 1;
                }
            }
            args.Add(inner[start..].Trim());
            yield return [.. args];
        }
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>.</summary>
    private static int Close(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opening)
                depth++;
            else if (text[i] == closing && --depth == 0)
                return i;
        }
        throw new InvalidOperationException($"the bracket at {open} never closes");
    }

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"float4\s+PmClip\s*\(\s*float4\s+(?<p>\w+)\s*\)\s*\{\s*return\s+(?<e>[^;]+);\s*\}")]
    private static partial Regex PmClipBody();

    [GeneratedRegex(@"\b(float(?<n>[234])?|int|uint)\s+\w+\s*:\s*ATTR(?<loc>\d+)\b")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"float4\s+(?<name>\w+)\s*:\s*SV_Position\b")]
    private static partial Regex SvPosition();

    [GeneratedRegex(@"\bmul\s*\(")]
    private static partial Regex MulCall();

    [GeneratedRegex(@"^(?<num>\d+(\.\d*)?)|^(?<name>\w+)\.(?<c>[xyzw])\b")]
    private static partial Regex Term();

    /// <summary>An HLSL expression of one float4 parameter, evaluated: float4(…) of four parts, a part a sum or product of
    /// negations, numbers, parenthesised parts and the parameter's components.</summary>
    private sealed class HlslExpression(string text, string parameter, Vector4 value)
    {
        private int _at;

        public Vector4 Float4()
        {
            Expect("float4");
            Expect("(");
            var x = Sum();
            Expect(",");
            var y = Sum();
            Expect(",");
            var z = Sum();
            Expect(",");
            var w = Sum();
            Expect(")");
            Skip();
            Assert.True(_at == text.Length, $"PmClip's expression goes on after its float4: {text[_at..]}");
            return new Vector4(x, y, z, w);
        }

        private float Sum()
        {
            var v = Product();
            while (Peek() is '+' or '-')
                v = Take() == '+' ? v + Product() : v - Product();
            return v;
        }

        private float Product()
        {
            var v = Unary();
            while (Peek() is '*' or '/')
                v = Take() == '*' ? v * Unary() : v / Unary();
            return v;
        }

        private float Unary()
        {
            if (Peek() == '-')
            {
                Take();
                return -Unary();
            }
            if (Peek() == '(')
            {
                Take();
                var v = Sum();
                Expect(")");
                return v;
            }
            Skip();
            var m = Term().Match(text[_at..]);
            Assert.True(m.Success, $"PmClip's expression has a term this reader does not know: {text[_at..]}");
            _at += m.Length;
            if (m.Groups["num"].Success)
                return float.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
            Assert.Equal(parameter, m.Groups["name"].Value);
            return m.Groups["c"].Value switch
            {
                "x" => value.X,
                "y" => value.Y,
                "z" => value.Z,
                _ => value.W,
            };
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
                _at++;
        }

        private char Peek()
        {
            Skip();
            return _at < text.Length ? text[_at] : '\0';
        }

        private char Take() => text[_at++];

        private void Expect(string token)
        {
            Skip();
            Assert.True(string.CompareOrdinal(text, _at, token, 0, token.Length) == 0, $"PmClip's expression wants {token} at: {text[_at..]}");
            _at += token.Length;
        }
    }
}
