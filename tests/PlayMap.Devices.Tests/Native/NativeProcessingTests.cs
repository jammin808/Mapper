using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.Christie;
using PlayMap.Devices.Drivers.Panasonic;
using PlayMap.Devices.Model;
using PlayMap.Devices.Native;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Devices.Tests.Native;

/// <summary>A projector's own processing read live: Panasonic's geometry and edge blend as the simulator holds them, read with queries
/// only (nothing written), with the line that turns one off; another protocol answers with words and reads nothing.</summary>
public sealed class NativeProcessingTests
{
    [Fact]
    public async Task PanasonicsGeometryAndBlendAreReadWithQueriesOnly()
    {
        await using var sim = new PanasonicSimulator { Geometry = "+00001", EdgeBlend = "+00000" };
        await using var d = new PanasonicDriver(TestSupport.Link(ProjectorProtocol.PanasonicNtControl, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);
        var r = await NativeProcessing.ReadAsync(d, CancellationToken.None);
        Assert.True(r.Read);
        Assert.True(r.GeometryOn);
        Assert.False(r.BlendOn);
        Assert.Equal(["geometry", "edge blend"], r.Settings.Select(s => s.Name));
        Assert.Equal("+00001", r.Settings[0].Value);
        Assert.Equal("VXX:GMMI0=+00000", NativeProcessing.NeutralLine(r.Settings[0]));
        Assert.Equal("its own geometry is on", r.Words);
        Assert.All(sim.Received, c => Assert.StartsWith("QVX:", c, StringComparison.Ordinal));

        sim.Geometry = "+00000";
        sim.EdgeBlend = "+00001";
        r = await NativeProcessing.ReadAsync(d, CancellationToken.None);
        Assert.False(r.GeometryOn);
        Assert.True(r.BlendOn);
    }

    [Fact]
    public async Task AnotherProtocolReadsNothingAndSaysWhereToLook()
    {
        await using var sim = new ChristieSimulator();
        await using var d = new ChristieDriver(TestSupport.Link(ProjectorProtocol.ChristieAscii, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);
        var r = await NativeProcessing.ReadAsync(d, CancellationToken.None);
        Assert.False(r.Read);
        Assert.Empty(r.Settings);
        Assert.Equal(CommandStage.Refused, r.Result.Stage);
        Assert.Contains("check its menu", r.Words, StringComparison.Ordinal);
        Assert.Empty(sim.Received);
    }
}
