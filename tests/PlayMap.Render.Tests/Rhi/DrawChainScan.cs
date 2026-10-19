using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>Reads the draws of a renderer source file against the program table: each <c>.Draw(&lt;program&gt;, …)</c> chain, from the
/// draw to its terminal call (<c>Fullscreen</c>, <c>GeneratedQuads</c>, <c>Vertices</c>, <c>Indexed</c>, <c>Instanced</c>), has its
/// program resolved from the file (a field, property or local assigned <c>Programs.Id("name")</c>, the nearest assignment before the
/// draw, or <c>ProgramIds.X.Program</c>), and every literal name the chain sets or binds (<c>Set("…"</c>, <c>SetInt("…"</c>,
/// <c>Texture("…"</c>; an element <c>name[i]</c> as its array) must be in that program's layout, and every generated id it uses
/// (<c>ProgramIds.X.uY</c>) must be that program's. A literal a helper sets on a builder it was handed (a parameter or local typed
/// <see cref="DrawBuilder"/>), outside every chain, must be in one of the file's programs. A chain whose program the file does not name (a parameter, a program made at run time) is allowed
/// only in a file of <see cref="Unresolved"/>, its names checked against the programs that entry lists. Comments are read as
/// blank (<see cref="RhiFenceTests.StripComments"/>).</summary>
internal static partial class DrawChainScan
{
    /// <summary>The files whose draws name a program the file cannot resolve, the programs those draws are of, and why.</summary>
    public static readonly FrozenDictionary<string, (string[] Programs, string Why)> Unresolved =
        new Dictionary<string, (string[], string)>(StringComparer.Ordinal)
        {
            ["Hap/HapPass.cs"] = (["hap"], "the pass draws the program its caller resolved (the video set's hap)"),
            ["Effects/IsfRenderer.cs"] = ([], "a user's effect: each name is set only where the program's layout, reflected from its source, holds it"),
            ["Generators/ShaderGenerators.cs"] = ([.. ProgramTable.Product.Select(p => p.Name).Where(n => n.StartsWith("fractal.", StringComparison.Ordinal)
                || n.StartsWith("reactive.", StringComparison.Ordinal))], "a fractal's program is its kind's, a reactive scene's its scene's"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] Terminals = [".Fullscreen(", ".GeneratedQuads(", ".Vertices(", ".Indexed(", ".Instanced("];

    /// <summary>Each generated id class (<c>ProgramIds.Quad</c>) and its program's name.</summary>
    private static readonly Lazy<FrozenDictionary<string, string>> IdClasses = new(() =>
        typeof(ProgramIds).GetNestedTypes(BindingFlags.Public).ToFrozenDictionary(t => t.Name,
            t => ProgramTable.Product[((ProgramId)t.GetField("Program")!.GetValue(null)!).Value].Name, StringComparer.Ordinal));

    [GeneratedRegex(@"\b(?<ident>[A-Za-z_]\w*)\s*=\s*(?:[\w.]+\.)?Programs\.Id\(\s*(?:""(?<lit>[^""]*)""\s*\)|(?<expr>[^;]*?)\)\s*;)", RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\bProgramId\??\s+(?<ident>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramIdName();

    [GeneratedRegex(@"\.Draw\(\s*(?<arg>[A-Za-z_][\w.]*)\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex Draw();

    [GeneratedRegex(@"\.(?<kind>Set|SetInt|Texture)\(\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();

    [GeneratedRegex(@"\bProgramIds\.(?<cls>\w+)\.(?<member>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedId();

    [GeneratedRegex(@"\bDrawBuilder\s+(?<ident>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex BuilderName();

    [GeneratedRegex(@"\b(?<recv>[A-Za-z_]\w*)\s*\.\s*(?:Set|SetInt|Texture)\(", RegexOptions.CultureInvariant)]
    private static partial Regex BuilderCall();

    /// <summary>The draws a source file asks of names its programs do not hold, each a line of words (none: every name is held);
    /// <paramref name="drawn"/> takes the program of each chain read.</summary>
    public static List<string> Misses(string file, string source, List<string>? drawn = null)
    {
        var code = RhiFenceTests.StripComments(source);
        var table = ProgramTable.Product.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var misses = new List<string>();
        var assignments = Assignment().Matches(code).Select(m => (m.Index, Ident: m.Groups["ident"].Value,
            Program: m.Groups["lit"].Success ? m.Groups["lit"].Value : null)).ToList();
        var programIds = ProgramIdName().Matches(code).Select(m => m.Groups["ident"].Value).ToHashSet(StringComparer.Ordinal);
        programIds.UnionWith(assignments.Select(a => a.Ident));
        var filePrograms = assignments.Where(a => a.Program is not null).Select(a => a.Program!).ToHashSet(StringComparer.Ordinal);
        foreach (Match m in GeneratedId().Matches(code))
            if (IdClasses.Value.TryGetValue(m.Groups["cls"].Value, out var name))
                filePrograms.Add(name);
        Unresolved.TryGetValue(file, out var listed);
        filePrograms.UnionWith(listed.Programs ?? []);
        foreach (var p in filePrograms)
        {
            if (!table.ContainsKey(p))
                misses.Add($"{file}: no product program is named '{p}'");
        }
        var covered = new List<(int From, int To)>();
        var unresolved = 0;
        foreach (Match d in Draw().Matches(code))
        {
            var arg = d.Groups["arg"].Value;
            if (!IsProgram(arg, programIds))
                continue;
            var end = Terminals.Select(t => code.IndexOf(t, d.Index, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            var line = Line(code, d.Index);
            if (end < 0)
            {
                misses.Add($"{file}:{line}: a draw of {arg} has no terminal call");
                continue;
            }
            covered.Add((d.Index, end));
            var program = Resolve(arg, d.Index, assignments);
            string[] programs;
            if (program is not null)
            {
                programs = [program];
                drawn?.Add(program);
            }
            else
            {
                unresolved++;
                if (listed.Programs is null)
                {
                    misses.Add($"{file}:{line}: the draw's program ({arg}) is not named in the file, and the file is not listed with the programs it draws");
                    continue;
                }
                programs = listed.Programs;
                drawn?.AddRange(programs);
            }
            foreach (Match l in Literal().Matches(code[d.Index..end]))
                Check(file, Line(code, d.Index + l.Index), l, programs, table, misses);
            foreach (Match g in GeneratedId().Matches(code[d.Index..end]))
            {
                if (g.Groups["member"].Value == "Program" || !IdClasses.Value.TryGetValue(g.Groups["cls"].Value, out var of))
                    continue;
                if (!programs.Contains(of, StringComparer.Ordinal))
                    misses.Add($"{file}:{Line(code, d.Index + g.Index)}: {g.Value} is {of}'s, set in a draw of {string.Join(" or ", programs)}");
            }
        }
        // A helper's statements on a builder it was handed (a parameter or local typed DrawBuilder), outside every chain.
        var builders = BuilderName().Matches(code).Select(m => m.Groups["ident"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (Match b in BuilderCall().Matches(code))
        {
            if (!builders.Contains(b.Groups["recv"].Value) || covered.Any(c => b.Index >= c.From && b.Index < c.To))
                continue;
            var end = code.IndexOf(';', b.Index);
            end = end < 0 ? code.Length : end;
            foreach (Match l in Literal().Matches(code[b.Index..end]))
                Check(file, Line(code, b.Index + l.Index), l, [.. filePrograms], table, misses);
            covered.Add((b.Index, end));
        }
        if (listed.Programs is not null && unresolved == 0)
            misses.Add($"{file}: listed as drawing programs it does not name, and every draw of it names its program");
        return misses;
    }

    /// <summary>Whether a draw's first argument is a program: a name the file types or assigns as a <see cref="ProgramId"/>, a generated
    /// id's program, or a member a program is read from (<c>entry.Id</c>).</summary>
    private static bool IsProgram(string arg, HashSet<string> programIds)
    {
        if (arg.StartsWith("ProgramIds.", StringComparison.Ordinal))
            return arg.EndsWith(".Program", StringComparison.Ordinal);
        var last = arg[(arg.LastIndexOf('.') + 1)..];
        return programIds.Contains(last) || (arg.Contains('.', StringComparison.Ordinal) && last is "Id" or "Program");
    }

    /// <summary>The program a draw's argument names: a generated id's, or the literal of the nearest assignment before the draw; null
    /// when the file does not say (a parameter, a run-time name, a member of something else).</summary>
    private static string? Resolve(string arg, int at, List<(int Index, string Ident, string? Program)> assignments)
    {
        if (arg.StartsWith("ProgramIds.", StringComparison.Ordinal))
        {
            var cls = arg.Split('.')[1];
            return IdClasses.Value.TryGetValue(cls, out var name) ? name : null;
        }
        if (arg.Contains('.', StringComparison.Ordinal) && !arg.StartsWith("this.", StringComparison.Ordinal))
            return null;
        var ident = arg.StartsWith("this.", StringComparison.Ordinal) ? arg[5..] : arg;
        var nearest = assignments.Where(a => a.Ident == ident && a.Index < at).Select(a => ((int, string, string?)?)a).LastOrDefault();
        return nearest?.Item3;
    }

    private static void Check(string file, int line, Match literal, string[] programs, Dictionary<string, ProgramLayout> table, List<string> misses)
    {
        var name = literal.Groups["name"].Value;
        var bracket = name.IndexOf('[', StringComparison.Ordinal);
        var bare = bracket < 0 ? name : name[..bracket];
        var texture = literal.Groups["kind"].Value == "Texture";
        var held = programs.Any(p => table.TryGetValue(p, out var layout)
            && (texture ? layout.TryTexture(bare, out _) : layout.TryConstant(bare, out _)));
        if (!held)
            misses.Add(programs.Length == 0
                ? $"{file}:{line}: '{name}' is set where the file draws no program it names"
                : $"{file}:{line}: '{name}' is no {(texture ? "texture" : "constant")} of {string.Join(" or ", programs)}");
    }

    private static int Line(string code, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < code.Length; i++)
            if (code[i] == '\n')
                line++;
        return line;
    }

    /// <summary>The renderer's source files outside the GL backend, by their path under <c>src/PlayMap.Render</c> ('/' separated).</summary>
    public static ImmutableArray<(string File, string Source)> RenderSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        var render = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln"), "src", "PlayMap.Render");
        return [.. Directory.EnumerateFiles(render, "*.cs", SearchOption.AllDirectories)
            .Select(p => (File: Path.GetRelativePath(render, p).Replace('\\', '/'), Path: p))
            .Where(f => !f.File.StartsWith("Gl/", StringComparison.Ordinal) && !f.File.StartsWith("obj/", StringComparison.Ordinal)
                && !f.File.StartsWith("bin/", StringComparison.Ordinal))
            .OrderBy(f => f.File, StringComparer.Ordinal)
            .Select(f => (f.File, File.ReadAllText(f.Path)))];
    }
}
