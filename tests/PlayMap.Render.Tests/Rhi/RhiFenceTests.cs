using System.Reflection;
using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The line around the GL backend: no source under <c>src/</c> outside <c>src/PlayMap.Render/Gl/</c> names a type of the
/// backend or of Silk.NET's GL side (OpenGL, windowing, GLFW, input, its maths; anywhere in Render, any Silk.NET) — no using of
/// their namespaces, no name qualified into them, no namespace declared in them — save the platform factory, the one place backends
/// are composed, which names <c>GlPlatforms</c> and nothing else of GL (the sound's OpenAL binding is the Media module's own); no type
/// of the Render assembly outside the backend's namespaces takes, holds, returns or calls a backend or Silk.NET type (its signatures
/// and the code of its methods, read from the IL); Core's Gpu and Presentation namespaces reference no Render type; a pass and the
/// frame it is handed carry no GL type; and no test of the suite but the GL backend's own (listed by name, to leave with GL) names a GL
/// type or the fixture's GL leg; no project or props file under <c>src/</c> declares a global using of the backend's or Silk.NET's GL
/// namespaces. The scan reads the code with its comments blanked, its strings kept (<see cref="GlFence"/>).</summary>
public sealed partial class RhiFenceTests
{
    /// <summary>The composition point and the one GL name it may use.</summary>
    private const string FactoryFile = "src/PlayMap.Render/Platform/GpuPlatform.cs";

    private const string FactoryType = "PlayMap.Render.Platform.GpuPlatforms";

    [Fact]
    public void NoSourceOutsideTheGlBackendNamesAGlTypeOrSilk()
    {
        var root = GlFence.RepoRoot();
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
            violations.AddRange(GlFence.Violations(File.ReadAllText(file), allowFactory: rel == FactoryFile, anySilk: render).Select(v => $"{rel}:{v}"));
        }
        Assert.True(scanned > 500, $"the scan read {scanned} files under src/: the tree is not where the test looks");
        // A project's or props file's global using would name GL in every source of the project, unseen above.
        var projects = Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(src, "*.props", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, "*.props", SearchOption.TopDirectoryOnly))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(projects.Count > 10, $"the scan read {projects.Count} project files");
        foreach (var file in projects)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(GlFence.ProjectViolations(File.ReadAllText(file)).Select(v => $"{rel}:{v}"));
        }
        Assert.True(violations.Count == 0, "GL named outside src/PlayMap.Render/Gl/:\n" + string.Join("\n", violations));

        // The factory names the backend's maker, and only it.
        var factory = GlFence.Violations(File.ReadAllText(Path.Combine(root, FactoryFile)), allowFactory: false, anySilk: true);
        Assert.NotEmpty(factory);
        Assert.All(factory, v => Assert.Contains("Gl." + GlFence.FactoryName, v, StringComparison.Ordinal));
    }

    /// <summary>The test files that may name the GL backend: its own tests and fixtures, and the GL leg of the backend fixture. Each
    /// leaves with GL, by name; every other test draws through the device a leg hands it.</summary>
    private static readonly string[] GlTests =
    [
        "BackendLegs.cs", "GlLeg.cs",
        "Decode/DmaBufImportTests.cs", "Decode/EglFixture.cs", "Decode/GlUploadArenaTests.cs",
        "Effects/FxLaneContextTests.cs", "Effects/IsfTranslatorTests.cs",
        "Hap/HapGlTests.cs",
        "Presentation/AngleAssetTests.cs", "Presentation/DxgiLayoutTests.cs", "Presentation/DxgiWarpPresenterTests.cs",
        "Presentation/EsFixture.cs", "Presentation/GlApiTests.cs", "Presentation/GlDialectTests.cs", "Presentation/GlPlatformTesting.cs",
        "Presentation/GlPlatformTests.cs", "Presentation/GpuPlatformFaultTests.cs",
        "Rhi/DrawStateTests.cs", "Rhi/FenceValueTests.cs", "Rhi/GlBackendTests.cs", "Rhi/ProgramLayoutTests.cs", "Rhi/RhiFenceTests.cs",
        "Rhi/TransientContractTests.cs", "Rhi/UserProgramTests.cs",
        "Spine/GlCapsTests.cs", "Spine/ShaderConventionTests.cs",
    ];

    [GeneratedRegex(@"\b(RunGl|GlLeg|GlLegs)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GlLegNamed();

    [Fact]
    public void NoTestOutsideTheGlBackendsOwnNamesAGlType()
    {
        var root = Path.Combine(GlFence.RepoRoot(), "tests", "PlayMap.Render.Tests");
        var violations = new List<string>();
        var naming = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            scanned++;
            var source = File.ReadAllText(file);
            var found = GlFence.Violations(source, allowFactory: false, anySilk: true);
            var code = GlFence.StripComments(source);
            foreach (Match m in GlLegNamed().Matches(code))
                found.Add($"{GlFence.LineNumber(code, m.Index)}: {m.Value}");
            if (found.Count == 0)
                continue;
            naming.Add(rel);
            if (!GlTests.Contains(rel, StringComparer.Ordinal))
                violations.AddRange(found.Select(v => $"{rel}:{v}"));
        }
        Assert.True(scanned > 60, $"the scan read {scanned} test files: the suite is not where the test looks");
        Assert.True(violations.Count == 0, "GL named by a test that is not the GL backend's own:\n" + string.Join("\n", violations));
        // The list holds only files that still name GL, so a test that stops naming it leaves the list too.
        Assert.Equal(GlTests.Order(StringComparer.Ordinal), naming);
    }

    [Fact]
    public void TheScanSeesNamesInCodeAndNoneInComments()
    {
        Assert.Single(GlFence.Violations("using PlayMap.Render.Gl;\n", allowFactory: false));
        Assert.Single(GlFence.Violations("global using Silk.NET.OpenGL;\n", allowFactory: false));
        Assert.Single(GlFence.Violations("using Api = Silk.NET.OpenGL.GL;\n", allowFactory: false));
        Assert.Single(GlFence.Violations("IWindow w = Silk.NET.Windowing.Window.Create(o);\n", allowFactory: false));
        // The sound's binding is Media's own; in Render every Silk.NET name is the backend's.
        Assert.Empty(GlFence.Violations("using Silk.NET.OpenAL;\nusing Silk.NET.Core.Contexts;\n", allowFactory: false));
        Assert.Equal(2, GlFence.Violations("using Silk.NET.OpenAL;\nusing Silk.NET.Core.Contexts;\n", allowFactory: false, anySilk: true).Count);
        Assert.Single(GlFence.Violations("namespace PlayMap.Render.Gl.Windows;\n", allowFactory: false));
        Assert.Single(GlFence.Violations("var d = Render.Gl.GlDevice.CurrentBinding();\n", allowFactory: false));
        Assert.Single(GlFence.Violations("var u = \"http://x\"; var d = Gl.GlDevice.CurrentBinding();\n", allowFactory: false));
        Assert.Single(GlFence.Violations("var s = $\"{(a ? \"x\" : \"y\")} // text\"; Gl.GlDialects.Word(d);\n", allowFactory: false));
        Assert.Single(GlFence.Violations("var p = Gl.GlPlatforms.Create(r, o, w);\n", allowFactory: false));
        Assert.Empty(GlFence.Violations("var p = Gl.GlPlatforms.Create(r, o, w);\n", allowFactory: true));
        Assert.Single(GlFence.Violations("var p = Gl.GlDevice.CurrentBinding();\n", allowFactory: true));
        Assert.Empty(GlFence.Violations("// using PlayMap.Render.Gl;\n/* Gl.GlDevice */\n/// <see cref=\"Gl.GlDevice\"/>\n", allowFactory: false));
        Assert.Empty(GlFence.Violations("var mode = PresentMode.Gl; var kind = GpuBackendKind.Gl;\n", allowFactory: false));
        // A comment opener inside a raw string is the string's.
        Assert.Single(GlFence.Violations("var g = \"\"\"\n  // GLSL\n  \"\"\"; Gl.GlDevice.CurrentBinding();\n", allowFactory: false));
        // Line numbers survive the blanking.
        Assert.StartsWith("3:", GlFence.Violations("/* one\n two */\nusing PlayMap.Render.Gl;\n", allowFactory: false)[0], StringComparison.Ordinal);
        // A project's global usings: the backend's and Silk.NET's GL namespaces, not in a comment; Core's contracts are fine.
        Assert.Equal(["2: PlayMap.Render.Gl", "3: Silk.NET.OpenGL"], GlFence.ProjectViolations(
            "<ItemGroup>\n  <Using Include=\"PlayMap.Render.Gl\" />\n  <Using Include=\"Silk.NET.OpenGL\"/>\n  <!-- <Using Include=\"PlayMap.Render.Gl.Windows\" /> -->\n</ItemGroup>\n"));
        Assert.Empty(GlFence.ProjectViolations("<ItemGroup><Using Include=\"PlayMap.Core.Gpu\" /><Using Include=\"Silk.NET.OpenAL\" /></ItemGroup>"));
    }

    [Fact]
    public void NoRenderTypeOutsideTheGlNamespaceTakesHoldsOrCallsAGlType()
    {
        var assembly = typeof(RenderEngine).Assembly;
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        var checkedTypes = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (GlFence.InGl(type))
                continue;
            checkedTypes++;
            var name = type.FullName ?? type.Name;
            var allowed = name == FactoryType || name.StartsWith(FactoryType + "+", StringComparison.Ordinal);
            foreach (var (where, referenced) in GlFence.References(type))
            {
                if (!GlFence.IsGl(referenced, out var gl))
                    continue;
                if (allowed && gl.Name == GlFence.FactoryName && gl.Namespace == GlFence.GlNamespace)
                    continue;
                violations.Add($"{name} {where}: {gl.FullName}");
            }
        }
        Assert.True(violations.Count == 0, "GL types reached outside the backend:\n" + string.Join("\n", violations));
        Assert.True(checkedTypes > 150, $"{checkedTypes} types read outside the GL namespace");

        // The factory does reach the backend's maker: the walk sees calls.
        var factory = assembly.GetType(FactoryType, throwOnError: true)!;
        Assert.Contains(GlFence.References(factory), r => GlFence.IsGl(r.Type, out var gl) && gl.Name == GlFence.FactoryName);
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
            if (!GlFence.Under(ns, "PlayMap.Core.Gpu") && !GlFence.Under(ns, "PlayMap.Core.Presentation"))
                continue;
            checkedTypes++;
            foreach (var (where, referenced) in GlFence.References(type))
            {
                foreach (var t in GlFence.Parts(referenced))
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
        var gl = carried.Where(c => GlFence.IsGl(c.Type, out _)).Select(c => $"{c.Where}: {c.Type.FullName}").ToList();
        Assert.True(gl.Count == 0, "GL types in a pass's signatures:\n" + string.Join("\n", gl));
    }
}
