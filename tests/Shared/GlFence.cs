using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayMap.Tests;

/// <summary>The line around the GL backend as the suites hold it: a scan of source and project files for the backend's and Silk.NET's
/// GL names (comments blanked, strings kept), and a walk of an assembly's types — their signatures and the IL of their methods — for
/// the types they reach. Shared by the Render suite (<c>RhiFenceTests</c>) and the Engine suite (<c>EngineFenceTests</c>).</summary>
internal static partial class GlFence
{
    /// <summary>The backend's namespace (and those under it).</summary>
    internal const string GlNamespace = "PlayMap.Render.Gl";

    /// <summary>The one GL name the platform factory, where backends are composed, may use.</summary>
    internal const string FactoryName = "GlPlatforms";

    private static readonly (OpCode[] One, OpCode[] Two) Codes = OpCodeTables();

    [GeneratedRegex(@"^[ \t]*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?(global::)?(PlayMap\.Render\.Gl|Silk\.NET\.(OpenGL|Windowing|GLFW|Maths|Input))\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UsingGl();

    [GeneratedRegex(@"^[ \t]*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?(global::)?Silk\.NET\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UsingSilk();

    [GeneratedRegex(@"\bnamespace\s+PlayMap\.Render\.Gl\b", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceGl();

    [GeneratedRegex(@"\bGl\s*\.\s*(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedGl();

    [GeneratedRegex(@"\bSilk\s*\.\s*NET\s*\.\s*(OpenGL|Windowing|GLFW|Maths|Input)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SilkGl();

    [GeneratedRegex(@"\bSilk\s*\.\s*NET\b", RegexOptions.CultureInvariant)]
    private static partial Regex SilkNet();

    // ---- the source scan ----

    /// <summary>Each line of <paramref name="source"/> (comments blanked) that names the backend or Silk.NET's GL side (any of
    /// Silk.NET with <paramref name="anySilk"/>), as "line: text"; <paramref name="allowFactory"/> lets <c>Gl.GlPlatforms</c>
    /// through.</summary>
    internal static List<string> Violations(string source, bool allowFactory, bool anySilk = false)
    {
        var code = StripComments(source);
        var usings = anySilk ? UsingSilk() : UsingGl();
        var silk = anySilk ? SilkNet() : SilkGl();
        var found = new List<(int Index, string Text)>();
        foreach (Match m in UsingGl().Matches(code))
            found.Add((m.Index, m.Value.Trim()));
        if (anySilk)
        {
            foreach (Match m in UsingSilk().Matches(code))
            {
                if (!UsingGl().IsMatch(LineOf(code, m.Index)))
                    found.Add((m.Index, m.Value.Trim()));
            }
        }
        foreach (Match m in NamespaceGl().Matches(code))
            found.Add((m.Index, m.Value));
        foreach (Match m in silk.Matches(code))
        {
            if (!usings.IsMatch(LineOf(code, m.Index)) && !UsingGl().IsMatch(LineOf(code, m.Index)))
                found.Add((m.Index, m.Value));
        }
        foreach (Match m in QualifiedGl().Matches(code))
        {
            if (allowFactory && m.Groups["name"].Value == FactoryName)
                continue;
            if (!UsingGl().IsMatch(LineOf(code, m.Index)) && !NamespaceGl().IsMatch(LineOf(code, m.Index)))
                found.Add((m.Index, m.Value));
        }
        return [.. found.OrderBy(f => f.Index).Select(f => $"{LineNumber(code, f.Index)}: {f.Text}")];
    }

    private static string LineOf(string code, int index)
    {
        var start = code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = code.IndexOf('\n', index);
        return code[start..(end < 0 ? code.Length : end)];
    }

    internal static int LineNumber(string code, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (code[i] == '\n')
                line++;
        }
        return line;
    }

    /// <summary>The source with every comment blanked (each character a space, its newlines kept, so lines keep their numbers);
    /// string and character literals are kept as written, an interpolated string's holes read as code.</summary>
    internal static string StripComments(string source)
    {
        var o = new StringBuilder(source.Length);
        var i = 0;
        Code(source, ref i, o, hole: false);
        return o.ToString();
    }

    /// <summary>Code up to the end, or in a hole up to the brace that closes it (left unread).</summary>
    private static void Code(string s, ref int i, StringBuilder o, bool hole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    o.Append(' ');
                    i++;
                }
                continue;
            }
            if (c == '/' && next == '*')
            {
                o.Append("  ");
                i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    o.Append(s[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < s.Length)
                {
                    o.Append("  ");
                    i += 2;
                }
                continue;
            }
            if (c == '\'')
            {
                CharLiteral(s, ref i, o);
                continue;
            }
            if ((c == '"' || c == '$' || c == '@') && StringLiteral(s, ref i, o))
                continue;
            if (hole)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                        return;
                    depth--;
                }
            }
            o.Append(c);
            i++;
        }
    }

    private static void CharLiteral(string s, ref int i, StringBuilder o)
    {
        o.Append(s[i++]);
        while (i < s.Length && s[i] != '\'' && s[i] != '\n')
        {
            if (s[i] == '\\' && i + 1 < s.Length)
                o.Append(s[i++]);
            o.Append(s[i++]);
        }
        if (i < s.Length && s[i] == '\'')
            o.Append(s[i++]);
    }

    /// <summary>A string literal of any form at <paramref name="i"/>, copied (its holes read as code); false when none starts there
    /// (a verbatim identifier, say).</summary>
    private static bool StringLiteral(string s, ref int i, StringBuilder o)
    {
        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '$')
                dollars++;
            else
                verbatim = true;
            j++;
        }
        if (j >= s.Length || s[j] != '"')
            return false;
        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"')
            quotes++;
        o.Append(s, i, j - i);
        i = j;
        if (quotes >= 3)
        {
            RawString(s, ref i, o, quotes, dollars);
            return true;
        }
        if (quotes == 2)
        {
            o.Append("\"\"");
            i += 2;
            return true;
        }
        o.Append(s[i++]);
        while (i < s.Length)
        {
            var c = s[i];
            if (!verbatim && c == '\\' && i + 1 < s.Length)
            {
                o.Append(c).Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"')
                {
                    o.Append("\"\"");
                    i += 2;
                    continue;
                }
                o.Append(c);
                i++;
                return true;
            }
            if (dollars > 0 && (c == '{' || c == '}'))
            {
                if (i + 1 < s.Length && s[i + 1] == c)
                {
                    o.Append(c).Append(c);
                    i += 2;
                    continue;
                }
                if (c == '{')
                {
                    Hole(s, ref i, o, 1);
                    continue;
                }
            }
            if (c == '\n' && !verbatim)
                return true;
            o.Append(c);
            i++;
        }
        return true;
    }

    private static void RawString(string s, ref int i, StringBuilder o, int quotes, int dollars)
    {
        o.Append(s, i, quotes);
        i += quotes;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                var run = 0;
                while (i + run < s.Length && s[i + run] == '"')
                    run++;
                o.Append(s, i, run);
                i += run;
                if (run >= quotes)
                    return;
                continue;
            }
            if (dollars > 0 && s[i] == '{')
            {
                var run = 0;
                while (i + run < s.Length && s[i + run] == '{')
                    run++;
                if (run >= dollars)
                {
                    o.Append(s, i, run - dollars);
                    i += run - dollars;
                    Hole(s, ref i, o, dollars);
                }
                else
                {
                    o.Append(s, i, run);
                    i += run;
                }
                continue;
            }
            o.Append(s[i++]);
        }
    }

    /// <summary>An interpolation hole opened by <paramref name="braces"/> braces: its code, then its closing braces.</summary>
    private static void Hole(string s, ref int i, StringBuilder o, int braces)
    {
        o.Append(s, i, braces);
        i += braces;
        Code(s, ref i, o, hole: true);
        for (var k = 0; k < braces && i < s.Length && s[i] == '}'; k++)
            o.Append(s[i++]);
    }

    // ---- the reflection walk ----

    internal static bool Under(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    internal static bool InGl(Type type) => Under(type.Namespace ?? "", GlNamespace);

    /// <summary>Whether a type, or a type it is made of (an element, a generic argument), is the backend's or Silk.NET's.</summary>
    internal static bool IsGl(Type type, out Type gl)
    {
        foreach (var t in Parts(type))
        {
            var ns = t.Namespace ?? "";
            if (Under(ns, GlNamespace) || Under(ns, "Silk.NET"))
            {
                gl = t;
                return true;
            }
        }
        gl = type;
        return false;
    }

    /// <summary>A type and the types it is made of: elements of arrays, pointers and references, generic arguments.</summary>
    internal static IEnumerable<Type> Parts(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var t in Parts(element))
                yield return t;
            yield break;
        }
        if (type.IsGenericParameter)
            yield break;
        yield return type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var t in Parts(arg))
                    yield return t;
            }
        }
    }

    /// <summary>Every type a type names: its base and interfaces, its fields', properties' and events' types, its methods' and
    /// constructors' signatures, and every type, field and method the code of its methods refers to.</summary>
    internal static IEnumerable<(string Where, Type Type)> References(Type type)
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic;
        if (type.BaseType is { } baseType)
            yield return ("base", baseType);
        foreach (var i in type.GetInterfaces())
            yield return ("interface", i);
        foreach (var f in type.GetFields(Declared))
            yield return ($"field {f.Name}", f.FieldType);
        foreach (var p in type.GetProperties(Declared))
            yield return ($"property {p.Name}", p.PropertyType);
        foreach (var e in type.GetEvents(Declared))
        {
            if (e.EventHandlerType is { } handler)
                yield return ($"event {e.Name}", handler);
        }
        var methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));
        if (type.TypeInitializer is { } cctor)
            methods = methods.Append(cctor);
        foreach (var m in methods)
        {
            if (m is MethodInfo mi)
                yield return ($"{m.Name} returns", mi.ReturnType);
            foreach (var p in m.GetParameters())
                yield return ($"{m.Name}({p.Name})", p.ParameterType);
            foreach (var r in Body(m))
                yield return ($"{m.Name} code", r);
        }
    }

    /// <summary>The types a method's IL refers to: each type token, and each field's and method's declaring type and signature.</summary>
    private static IEnumerable<Type> Body(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;
        var typeArgs = method.DeclaringType is { IsGenericType: true } d ? d.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            var op = il[i] == 0xFE ? Codes.Two[il[++i]] : Codes.One[il[i]];
            i++;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    var member = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                    i += 4;
                    switch (member)
                    {
                        case Type t:
                            yield return t;
                            break;
                        case FieldInfo f:
                            if (f.DeclaringType is { } fd)
                                yield return fd;
                            yield return f.FieldType;
                            break;
                        case MethodBase m:
                            if (m.DeclaringType is { } md)
                                yield return md;
                            if (m is MethodInfo mi)
                                yield return mi.ReturnType;
                            foreach (var p in m.GetParameters())
                                yield return p.ParameterType;
                            break;
                    }
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }

    private static (OpCode[] One, OpCode[] Two) OpCodeTables()
    {
        var one = new OpCode[256];
        var two = new OpCode[256];
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op)
                continue;
            var value = (ushort)op.Value;
            if (value < 0x100)
                one[value] = op;
            else if ((value & 0xFF00) == 0xFE00)
                two[value & 0xFF] = op;
        }
        return (one, two);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above the test assembly");
    }

    // ---- project files ----

    [GeneratedRegex(@"<Using\s+Include\s*=\s*""(?<ns>(?:PlayMap\.Render\.Gl|Silk\.NET\.(?:OpenGL|Windowing|GLFW|Maths|Input))\b[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectUsingGl();

    /// <summary>Each global using a project or props file declares of the backend's or Silk.NET's GL namespaces, as "line: namespace"
    /// (a project that declared one would let every source in it name GL types unqualified, unseen by the source scan).</summary>
    internal static List<string> ProjectViolations(string projectXml)
    {
        ArgumentNullException.ThrowIfNull(projectXml);
        var code = StripXmlComments(projectXml);
        return [.. ProjectUsingGl().Matches(code).Select(m => $"{LineNumber(code, m.Index)}: {m.Groups["ns"].Value}")];
    }

    /// <summary>An XML file with its comments blanked, its lines kept.</summary>
    private static string StripXmlComments(string xml)
    {
        var o = new StringBuilder(xml.Length);
        var i = 0;
        while (i < xml.Length)
        {
            if (string.CompareOrdinal(xml, i, "<!--", 0, 4) == 0)
            {
                var end = xml.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? xml.Length : end + 3;
                for (; i < end; i++)
                    o.Append(xml[i] == '\n' ? '\n' : ' ');
                continue;
            }
            o.Append(xml[i++]);
        }
        return o.ToString();
    }

    // ---- an assembly's types ----

    /// <summary>Every backend or Silk.NET type the types of <paramref name="assembly"/> outside the backend's namespaces reach, as
    /// "type where: reached", but those <paramref name="allowed"/> lets through (a type and what it reaches), and how many types were
    /// read.</summary>
    internal static (SortedSet<string> Violations, int Checked) AssemblyViolations(Assembly assembly, Func<Type, Type, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(allowed);
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        var checkedTypes = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (InGl(type))
                continue;
            checkedTypes++;
            foreach (var (where, referenced) in References(type))
            {
                if (!IsGl(referenced, out var gl) || allowed(type, gl))
                    continue;
                violations.Add($"{type.FullName ?? type.Name} {where}: {gl.FullName}");
            }
        }
        return (violations, checkedTypes);
    }
}
