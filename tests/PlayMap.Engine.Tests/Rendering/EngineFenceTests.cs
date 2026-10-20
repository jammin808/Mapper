using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The line around the GL backend from the engine's side: no type of the engine's assembly takes, holds, returns or calls a
/// type of the GL backend or of Silk.NET (its signatures and the IL of its methods: the source scan cannot see a name a project's
/// global using brought in), and the engine suite's tests that name the GL backend — their own platforms over the GL device, the GL
/// encoder's and decoders' checks — are listed by name, to leave with GL, and are exactly the files that do.</summary>
public sealed class EngineFenceTests
{
    /// <summary>The engine tests that may name the GL backend. Each leaves with GL, by name.</summary>
    private static readonly string[] GlTests =
    [
        "Content/ReactiveRealBusTests.cs",
        "Decode/DecodeGlApiTests.cs",
        "Optimise/HapEncoderTests.cs",
        "Optimise/OptimiseClips.cs",
        "Presentation/SyncVerbTests.cs",
        "Rendering/DeviceLockHostTests.cs",
    ];

    [Fact]
    public void NoEngineTypeTakesHoldsOrCallsAGlType()
    {
        var (violations, checkedTypes) = GlFence.AssemblyViolations(typeof(EngineCore).Assembly, static (_, _) => false);
        Assert.True(checkedTypes > 300, $"{checkedTypes} engine types read");
        Assert.True(violations.Count == 0, "GL types reached by the engine:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void TheEngineTestsThatNameGlAreTheListedOnes()
    {
        var root = Path.Combine(GlFence.RepoRoot(), "tests", "PlayMap.Engine.Tests");
        var naming = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            scanned++;
            if (GlFence.Violations(File.ReadAllText(file), allowFactory: false).Count > 0)
                naming.Add(rel);
        }
        Assert.True(scanned > 100, $"the scan read {scanned} test files: the suite is not where the test looks");
        Assert.Equal(GlTests.Order(StringComparer.Ordinal), naming);
    }
}
