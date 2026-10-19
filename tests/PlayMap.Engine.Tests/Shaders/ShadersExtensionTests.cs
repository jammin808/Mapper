using PlayMap.Core.Bus;
using PlayMap.Core.Effects;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Spine;
using HostRig = PlayMap.Engine.Tests.Host.HostRig;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>A real node composed through wp3-shaders' extension (as the exe will compose it once the spine's hook lands, the papers
/// note's first merge-gate item): STATE carries the worker and the quarantine, FX CHECK's stage check is the worker, preflight carries
/// the quarantine's row, and an entry the node quarantined is read again by a node started from the same home.</summary>
public sealed class ShadersExtensionTests
{
    private static EngineParts Parts() => EngineParts.Bare with
    {
        Extensions = [setup => new ShadersExtension(setup, new ShaderWorkerOptions { Worker = WorkerLaunch.Dll(NodeExe.Locate()) })],
    };

    private static Task<IReadOnlyList<PreflightRow>> Rows(HostRig rig) =>
        Assert.Single(rig.Engine.PreflightChecks, c => c is QuarantinePreflight)
            .RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task TheExtensionComposesTheWorkerAndAQuarantineKeptInTheHomeThatARestartReadsAgain()
    {
        var hash = new string('e', 64);
        await using var first = await HostRig.StartAsync(parts: Parts());
        var fx = first.State().GetProperty("fx");
        Assert.False(fx.GetProperty("compile").GetProperty("worker").GetProperty("running").GetBoolean());
        Assert.Empty(fx.GetProperty("quarantine").EnumerateArray());
        Assert.IsType<ShaderWorkerHost>(first.Engine.Services.FxStages);
        Assert.Same(first.Engine.Services.FxStages, first.Engine.Services.ShaderWorker);
        Assert.Equal(Light.Green, Assert.Single(await Rows(first)).Light);

        var path = ShaderParts.QuarantinePath(first.Home);
        Assert.False(File.Exists(path));
        Assert.True(first.Engine.Services.Quarantine.Add(new QuarantineEntry(hash, "Wobble", QuarantineReason.DeviceRemoved,
            "the GPU stopped inside this effect's pass on op_1", 1_760_000_000_000, "0.2.0+17.abc1234")).Applied);
        Assert.True(File.Exists(path));
        await first.StopAsync();

        await using var second = await HostRig.StartAsync(parts: Parts(), dir: first.Dir);
        var held = Assert.Single(second.State().GetProperty("fx").GetProperty("quarantine").EnumerateArray());
        Assert.Equal(hash, held.GetProperty("hash").GetString());
        Assert.Equal("deviceRemoved", held.GetProperty("reason").GetString());
        var row = Assert.Single(await Rows(second));
        Assert.Equal((Light.Red, $"FX RELEASE {hash}"), (row.Light, row.Fix));
        Assert.Equal("", second.Engine.Services.Quarantine.LoadWords);
    }
}
