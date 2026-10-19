using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The line around the GL backend: no source under <c>src/</c> outside <c>src/PlayMap.Render/Gl/</c> names a type of the
/// backend or of Silk.NET's GL side (OpenGL, windowing, GLFW, input, its maths; anywhere in Render, any Silk.NET) — no using of
/// their namespaces, no name qualified into them, no namespace declared in them — save the platform factory, the one place backends
/// are composed, which names <c>GlPlatforms</c> and nothing else of GL (the sound's OpenAL binding is the Media module's own); no type
/// of the Render assembly outside the backend's namespaces takes, holds, returns or calls a backend or Silk.NET type (its signatures
/// and the code of its methods, read from the IL); Core's Gpu and Presentation namespaces reference no Render type; a pass and the
/// frame it is handed carry no GL type. The scan reads the code with its comments blanked, its strings kept.</summary>
public sealed partial class RhiFenceTests
{
    /// <summary>The backend's namespace (and those under it).</summary>
    private const string GlNamespace = "PlayMap.Render.Gl";

    /// <summary>The composition point and the one GL name it may use.</summary>
    private const string FactoryFile = "src/PlayMap.Render/Platform/GpuPlatform.cs";

    private const string FactoryType = "PlayMap.Render.Platform.GpuPlatforms";
    private const string FactoryName = "GlPlatforms";

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

    [Fact]
    public void NoSourceOutsideTheGlBackendNamesAGlTypeOrSilk()
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "src");
        var gl = Path.Combine(src, "PlayMap.Render", "Gl") + Path.DirectorySeparatorChar;
        var violations = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (file.StartsWith(gl, StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)
                || rel.Contains("/bin/", StringComparison.Ordinal))
                continue;
            scanned++;
            var render = rel.StartsWith("src/PlayMap.Render/", StringComparison.Ordinal);
            violations.AddRange(Violations(File.ReadAllText(file), allowFactory: rel == FactoryFile, anySilk: render).Select(v => $"{rel}:{v}"));
        }
        Assert.True(scanned > 500, $"the scan read {scanned} files under src/: the tree is not where the test looks");
        Assert.True(violations.Count == 0, "GL named outside src/PlayMap.Render/Gl/:\n" + string.Join("\n", violations));

        // The factory names the backend's maker, and only it.
        var factory = Violations(File.ReadAllText(Path.Combine(root, FactoryFile)), allowFactory: false, anySilk: true);
        Assert.NotEmpty(factory);
        Assert.All(factory, v => Assert.Contains("Gl." + FactoryName, v, StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanSeesNamesInCodeAndNoneInComments()
    {
        Assert.Single(Violations("using PlayMap.Render.Gl;\n", allowFactory: false));
        Assert.Single(Violations("global using Silk.NET.OpenGL;\n", allowFactory: false));
        Assert.Single(Violations("using Api = Silk.NET.OpenGL.GL;\n", allowFactory: false));
        Assert.Single(Violations("IWindow w = Silk.NET.Windowing.Window.Create(o);\n", allowFactory: false));
        // The sound's binding is Media's own; in Render every Silk.NET name is the backend's.
        Assert.Empty(Violations("using Silk.NET.OpenAL;\nusing Silk.NET.Core.Contexts;\n", allowFactory: false));
        Assert.Equal(2, Violations("using Silk.NET.OpenAL;\nusing Silk.NET.Core.Contexts;\n", allowFactory: false, anySilk: true).Count);
        Assert.Single(Violations("namespace PlayMap.Render.Gl.Windows;\n", allowFactory: false));
        Assert.Single(Violations("var d = Render.Gl.GlDevice.CurrentBinding();\n", allowFactory: false));
        Assert.Single(Violations("var u = \"http://x\"; var d = Gl.GlDevice.CurrentBinding();\n", allowFactory: false));
        Assert.Single(Violations("var s = $\"{(a ? \"x\" : \"y\")} // text\"; Gl.GlDialects.Word(d);\n", allowFactory: false));
        Assert.Single(Violations("var p = Gl.GlPlatforms.Create(r, o, w);\n", allowFactory: false));
        Assert.Empty(Violations("var p = Gl.GlPlatforms.Create(r, o, w);\n", allowFactory: true));
        Assert.Single(Violations("var p = Gl.GlDevice.CurrentBinding();\n", allowFactory: true));
        Assert.Empty(Violations("// using PlayMap.Render.Gl;\n/* Gl.GlDevice */\n/// <see cref=\"Gl.GlDevice\"/>\n", allowFactory: false));
        Assert.Empty(Violations("var mode = PresentMode.Gl; var kind = GpuBackendKind.Gl;\n", allowFactory: false));
        // A comment opener inside a raw string is the string's.
        Assert.Single(Violations("var g = \"\"\"\n  // GLSL\n  \"\"\"; Gl.GlDevice.CurrentBinding();\n", allowFactory: false));
        // Line numbers survive the blanking.
        Assert.StartsWith("3:", Violations("/* one\n two */\nusing PlayMap.Render.Gl;\n", allowFactory: false)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NoRenderTypeOutsideTheGlNamespaceTakesHoldsOrCallsAGlType()
    {
        var assembly = typeof(RenderEngine).Assembly;
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        var checkedTypes = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (InGl(type))
                continue;
            checkedTypes++;
            var name = type.FullName ?? type.Name;
            var allowed = name == FactoryType || name.StartsWith(FactoryType + "+", StringComparison.Ordinal);
            foreach (var (where, referenced) in References(type))
            {
                if (!IsGl(referenced, out var gl))
                    continue;
                if (allowed && gl.Name == FactoryName && gl.Namespace == GlNamespace)
                    continue;
                violations.Add($"{name} {where}: {gl.FullName}");
            }
        }
        Assert.True(violations.Count == 0, "GL types reached outside the backend:\n" + string.Join("\n", violations));
        Assert.True(checkedTypes > 150, $"{checkedTypes} types read outside the GL namespace");

        // The factory does reach the backend's maker: the walk sees calls.
        var factory = assembly.GetType(FactoryType, throwOnError: true)!;
        Assert.Contains(References(factory), r => IsGl(r.Type, out var gl) && gl.Name == FactoryName);
    }

    [Fact]
    public void CoreGpuAndPresentationReferenceNoRenderType()
    {
        var core = typeof(IGpuDevice).Assembly;
        Assert.DoesNotContain(core.GetReferencedAssemblies(), a => a.Name?.StartsWith("PlayMap.Render", StringComparison.Ordinal) == true);
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        var checkedTypes = 0;
        foreach (var type in core.GetTypes())
        {
            var ns = type.Namespace ?? "";
            if (!Under(ns, "PlayMap.Core.Gpu") && !Under(ns, "PlayMap.Core.Presentation"))
                continue;
            checkedTypes++;
            foreach (var (where, referenced) in References(type))
            {
                foreach (var t in Parts(referenced))
                {
                    if (t.Assembly.GetName().Name?.StartsWith("PlayMap.Render", StringComparison.Ordinal) == true)
                        violations.Add($"{type.FullName} {where}: {t.FullName}");
                }
            }
        }
        Assert.True(checkedTypes > 40, $"{checkedTypes} Core Gpu and Presentation types read");
        Assert.True(violations.Count == 0, "Render types named in Core's contracts:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void APassAndTheFrameItIsHandedCarryNoGlType()
    {
        var frame = typeof(PassFrame);
        var carried = new List<(string Where, Type Type)>();
        foreach (var f in frame.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            carried.Add(("field " + f.Name, f.FieldType));
        foreach (var p in frame.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            carried.Add(("property " + p.Name, p.PropertyType));
        foreach (var c in frame.GetConstructors())
            carried.AddRange(c.GetParameters().Select(p => ("constructor " + p.Name, p.ParameterType)));
        foreach (var m in typeof(IFramePass).GetMethods())
        {
            carried.Add(($"{m.Name} returns", m.ReturnType));
            carried.AddRange(m.GetParameters().Select(p => ($"{m.Name}({p.Name})", p.ParameterType)));
        }
        Assert.Contains(carried, c => c.Type == typeof(IGpuDevice));
        Assert.Contains(carried, c => c.Type == typeof(ICommandList));
        var gl = carried.Where(c => IsGl(c.Type, out _)).Select(c => $"{c.Where}: {c.Type.FullName}").ToList();
        Assert.True(gl.Count == 0, "GL types in a pass's signatures:\n" + string.Join("\n", gl));
    }

    // ---- the source scan ----

    /// <summary>Each line of <paramref name="source"/> (comments blanked) that names the backend or Silk.NET's GL side (any of
    /// Silk.NET with <paramref name="anySilk"/>), as "line: text"; <paramref name="allowFactory"/> lets <c>Gl.GlPlatforms</c>
    /// through.</summary>
    private static List<string> Violations(string source, bool allowFactory, bool anySilk = false)
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

    private static int LineNumber(string code, int index)
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

    private static bool Under(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    private static bool InGl(Type type) => Under(type.Namespace ?? "", GlNamespace);

    /// <summary>Whether a type, or a type it is made of (an element, a generic argument), is the backend's or Silk.NET's.</summary>
    private static bool IsGl(Type type, out Type gl)
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
    private static IEnumerable<Type> Parts(Type type)
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
    private static IEnumerable<(string Where, Type Type)> References(Type type)
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above the test assembly");
    }
}
