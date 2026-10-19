using System.Globalization;
using System.Runtime.Versioning;
using Xunit.Abstractions;

namespace PlayMap.Render.D3D12.Tests;

/// <summary>A Direct3D 12 device on WARP, made through TerraFX on the Windows job, which has no GPU: the device comes up on the WARP
/// adapter, says its feature level and the runtime that made it, and its debug layer, when on, stored no warning, error or
/// corruption. Where the job put the pinned WARP and the Agility SDK beside the test host (PLAYMAP_D3D12_REQUIRED=1), the WARP that
/// made the device is that copy, not Windows' own, and it reaches the backend's floor, feature level 12_1
/// (docs/round3/design/d3d12.md §1). Whether the Agility runtime loads in a .NET apphost (M7) is written out, not asserted: the probe
/// step answers it in the job's summary. Off Windows it is skipped.</summary>
[SupportedOSPlatform("windows10.0.19043")]
public sealed class WarpDeviceTests(ITestOutputHelper output)
{
    /// <summary>D3D_FEATURE_LEVEL_12_1, the backend's floor.</summary>
    private const int Floor = 0xc100;

    /// <summary>D3D_FEATURE_LEVEL_11_0, the lowest level a Direct3D 12 device is made at.</summary>
    private const int Lowest = 0xb000;

    [WindowsFact]
    [Trait("Category", "Windows")]
    [Trait("Backend", "D3D12")]
    public void AWarpDeviceIsMadeThroughTerraFxAndSaysItsFeatureLevel()
    {
        var host = AppContext.BaseDirectory;
        var agility = Path.Combine(host, "D3D12");
        var required = Environment.GetEnvironmentVariable("PLAYMAP_D3D12_REQUIRED") == "1";
        var sdk = uint.TryParse(Environment.GetEnvironmentVariable("PLAYMAP_AGILITY_SDK"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0u;

        var answer = WarpDevice.Make(sdk, agility);

        output.WriteLine($"{answer.Adapter}: feature level {answer.FeatureLevelWord} on the {answer.Runtime} runtime; debug layer {(answer.DebugLayer ? "on" : "off")}; DRED {(answer.Dred ? "armed" : "not armed")}");
        output.WriteLine($"WARP: {string.Join(", ", answer.WarpModules)}");
        output.WriteLine($"D3D12Core: {string.Join(", ", answer.CoreModules)}");
        foreach (var step in answer.Steps)
            output.WriteLine(step);
        foreach (var message in answer.Messages)
            output.WriteLine(message.ToString());
        const string Test = nameof(WarpDeviceTests) + "." + nameof(AWarpDeviceIsMadeThroughTerraFxAndSaysItsFeatureLevel);
        D3D12Evidence.Write(Test, D3D12Evidence.InfoQueueLog,
            answer.DebugLayer ? answer.Messages.Select(m => m.ToString()) : ["the debug layer was off: no info queue"]);
        D3D12Evidence.Write(Test, D3D12Evidence.DredOutput,
            [$"DRED {(answer.Dred ? "armed before the device: auto-breadcrumbs and page faults forced on" : "not armed: its settings interface did not answer")}",
             $"the device's removed reason: {answer.RemovedReason}{(answer.RemovedReason == "0x00000000" ? " (not removed: no breadcrumbs to read)" : "")}"]);

        Assert.InRange(answer.FeatureLevel, Lowest, 0xffff);
        Assert.Matches("^1[12]_[0-9]$", answer.FeatureLevelWord);
        Assert.Contains(answer.Runtime, new[] { "agility", "os" });
        Assert.NotEmpty(answer.WarpModules);
        Assert.Equal("0x00000000", answer.RemovedReason);
        Assert.DoesNotContain(answer.Messages, m => m.Counts);
        if (!required)
            return;
        Assert.True(File.Exists(Path.Combine(agility, "D3D12Core.dll")), $"no D3D12Core.dll in {agility}");
        Assert.True(File.Exists(Path.Combine(agility, "d3d12SDKLayers.dll")), $"no d3d12SDKLayers.dll in {agility}");
        Assert.All(answer.WarpModules, m => Assert.False(m.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase),
            $"the WARP that made the device is Windows' own ({m}), not the pinned copy beside the host"));
        Assert.True(answer.FeatureLevel >= Floor, $"WARP reports feature level {answer.FeatureLevelWord}, under the backend's floor 12_1");
    }
}
