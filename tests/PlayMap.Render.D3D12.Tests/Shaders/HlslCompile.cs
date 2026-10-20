using System.Reflection;
using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The HLSL library as the Direct3D 12 backend's assembly embeds it (its sources, its tables and the build's generated
/// headers), compiled here through the shader worker's pinned DXC — on both jobs, since DXC's Linux build signs its DXIL (M6) — one
/// program at a time, each entry with the arguments the build's own compile uses. Compiled once per test run.</summary>
internal static class HlslCompile
{
    public sealed record Stage(DxilOutput Output, DxilReflection? Reflection, string Words);

    public sealed record Compiled(HlslLibrary.Entry Program, Stage Vs, Stage Ps);

    /// <summary>The arguments the build's compile passes beside -T, -E and the -I folders.</summary>
    public static readonly string[] Arguments = ["-HV", "2021", "-Zi", "-Qstrip_debug", "-Qstrip_reflect"];

    public static Assembly Backend { get; } = Assembly.Load("PlayMap.Render.D3D12");

    private static readonly Lazy<Dxc> Compiler = new(() => Dxc.TryLoad(out var words) ?? throw new InvalidOperationException(words));

    private static readonly Lazy<IReadOnlyList<Compiled>> All = new(CompileAll);

    public static IReadOnlyList<Compiled> Programs => All.Value;

    /// <summary>A reflection blob read through the shader worker's pinned DXC; null with words when it does not read.</summary>
    public static DxilReflection? Reflect(byte[] reflection, out string words) => Compiler.Value.Reflect(reflection, out words);

    /// <summary>One entry of a source compiled with the build's arguments, and its reflection; null with words when either fails.</summary>
    public static DxilReflection? CompileAndReflect(string source, string entry, string target, out string words)
    {
        var output = Compiler.Value.Compile(source, entry, target, Arguments);
        if (output.Ok)
            return Compiler.Value.Reflect(output.Reflection, out words);
        words = output.Words;
        return null;
    }

    public static byte[]? Resource(string name)
    {
        using var stream = Backend.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static IEnumerable<string> Resources(string prefix) =>
        Backend.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    private static IReadOnlyList<Compiled> CompileAll()
    {
        var root = Directory.CreateTempSubdirectory("playmap-hlsl-").FullName;
        try
        {
            var sources = Path.Combine(root, "src");
            Directory.CreateDirectory(sources);
            foreach (var name in Resources("shaders/").Where(n => !n.StartsWith("shaders/gen/", StringComparison.Ordinal) && !n.StartsWith("shaders/dxil/", StringComparison.Ordinal)))
                File.WriteAllBytes(Path.Combine(sources, name["shaders/".Length..]), Resource(name)!);
            var list = new List<Compiled>();
            foreach (var p in HlslLibrary.Programs())
            {
                var gen = Path.Combine(root, "gen", p.Name);
                Directory.CreateDirectory(gen);
                var header = Resource($"shaders/gen/{p.Name}/Layout.hlsli") ?? throw new InvalidOperationException($"the build embedded no header for {p.Name}");
                File.WriteAllBytes(Path.Combine(gen, "Layout.hlsli"), header);
                var file = Path.Combine(sources, p.File);
                var text = File.ReadAllText(file);
                Stage One(string entry, string target)
                {
                    var output = Compiler.Value.Compile(text, entry, target, [.. Arguments, "-I", gen, "-I", sources], file);
                    var reflection = output.Ok ? Compiler.Value.Reflect(output.Reflection, out var words) : null;
                    return new Stage(output, reflection, output.Ok ? "" : output.Words);
                }
                list.Add(new Compiled(p, One(p.Vs, "vs_6_6"), One(p.Ps, "ps_6_6")));
            }
            return list;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
