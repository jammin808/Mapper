using PlayMap.Core.Bus;
using PlayMap.Core.Effects;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Engine.ShaderWorker;
using HostRig = PlayMap.Engine.Tests.Host.HostRig;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>A real node composed with the shaders part the exe composes (EngineParts.Standard's own factory): STATE carries the
/// worker and the quarantine, FX CHECK's stage check is the worker, preflight carries the quarantine's row, and an entry the node
/// quarantined is read again by a node started from the same home.</summary>
public sealed class ShadersExtensionTests
{
    /// <summary>The exe's shaders part, taken from its composition by reference, alone on a bare node.</summary>
    private static EngineParts Parts()
    {
        var standard = EngineParts.Standard().Extensions;
        Assert.Contains(ShadersExtension.Factory, standard);
        return EngineParts.Bare with { Extensions = [Assert.Single(standard, f => f == ShadersExtension.Factory)] };
    }

    private static Task<IReadOnlyList<PreflightRow>> Rows(HostRig rig) =>
        Assert.Single(rig.Engine.PreflightChecks, c => c is QuarantinePreflight)
            .RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task TheExesCompositionCarriesTheWorkerAndAQuarantineKeptInTheHomeThatARestartReadsAgain()
    {
        var hash = new string('e', 64);
        await using var first = await HostRig.StartAsync(parts: Parts());
        var fx = first.State().GetProperty("fx");
        var worker = fx.GetProperty("compile").GetProperty("worker");
        Assert.False(worker.GetProperty("running").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, worker.GetProperty("pid").ValueKind);
        Assert.Equal(0, worker.GetProperty("restarts").GetInt32());
        Assert.Empty(fx.GetProperty("quarantine").EnumerateArray());
        Assert.IsType<ShaderWorkerHost>(first.Engine.Services.FxStages);
        Assert.Same(first.Engine.Services.FxStages, first.Engine.Services.ShaderWorker);
        var green = Assert.Single(await Rows(first));
        Assert.Equal(("fx.quarantine", Light.Green), (green.Id, green.Light));

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
        Assert.Equal(("fx.quarantine:" + hash, Light.Red, $"FX RELEASE {hash}"), (row.Id, row.Light, row.Fix));
        Assert.Equal("", second.Engine.Services.Quarantine.LoadWords);
    }
}
