using System.Net;
using PlayMap.Core.Machine;
using PlayMap.Engine.Machine;
using PlayMap.Engine.Tests.Control;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.VideoIo;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>The show doors the machine part hands its platform with the facts: the network host's open doors, then the control
/// inputs', each at the address it is bound to, then each open network send's on every interface; none before the engine starts.</summary>
public sealed class MachineDoorTests
{
    [Fact]
    public async Task ThePlatformReadsTheNetworkHostsDoorsThenTheControlInputs()
    {
        await using var node = await HostRig.StartAsync("Doors");
        await using var control = await ControlRig.StartAsync(service: true);
        var loopback = IPAddress.Parse("127.0.0.1");
        Assert.Equal<ShowDoor>([new ShowDoor("wire", loopback), new ShowDoor("web", loopback), new ShowDoor("osc", IPAddress.Parse(ControlRig.Loopback))],
            MachineParts.ShowDoors(node.Network, control.Engine));
        Assert.Empty(MachineParts.ShowDoors(null, null));
    }

    [Fact]
    public async Task ThePlatformReadsEachOpenNetworkSendAsADoorOnEveryInterface()
    {
        await using var rig = new VideoIoRig();
        Assert.Empty(MachineParts.ShowDoors(null, rig.Engine));
        await rig.Ok("SEND ADD OMT Main output:op_1 ALWAYS");
        var door = Assert.Single(MachineParts.ShowDoors(null, rig.Engine));
        Assert.Equal(new ShowDoor("omt", IPAddress.Any), door);
        Assert.True(door.Everywhere);
    }
}
