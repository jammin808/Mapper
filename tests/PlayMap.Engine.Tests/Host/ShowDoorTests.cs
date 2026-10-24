using System.Net;
using PlayMap.Core.Machine;
using PlayMap.Engine.Tests.Control;
using PlayMap.Engine.Tests.VideoIo;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The show doors a node has open now, each with the address it is bound to: the network host's (the line wire, the web port,
/// and the clock sync while it leads) at the node's bind address or every interface, the control inputs' (OSC, Art-Net, sACN) at
/// their own, and each open network send's (NDI, OMT) on every interface; a door that is closed is not one. The hello, which hears
/// every interface to find its peers, is never one.</summary>
public sealed class ShowDoorTests
{
    private static readonly IPAddress Loopback = IPAddress.Parse("127.0.0.1");

    [Fact]
    public async Task ANodeBoundToAnAddressHasItsWireAndWebDoorsThereAndItsClockDoorWhileItLeads()
    {
        await using var node = await HostRig.StartAsync("Doors");
        Assert.Equal<ShowDoor>([new ShowDoor("wire", Loopback), new ShowDoor("web", Loopback)], node.Network.Doors);
        Assert.DoesNotContain(node.Network.Doors, d => d.Everywhere);

        await node.OkAsync("SYNC LEAD doors");
        Assert.True(await Eventually.TrueAsync(() => node.Network.Doors.Length == 3), "the clock sync's door did not open");
        Assert.Equal<ShowDoor>([new ShowDoor("wire", Loopback), new ShowDoor("web", Loopback), new ShowDoor("sync", Loopback)], node.Network.Doors);
    }

    [Fact]
    public async Task ANodeBoundToNoAddressHasItsDoorsOnEveryInterface()
    {
        await using var node = await HostRig.StartAsync("Everywhere", network: true);
        Assert.Equal<ShowDoor>([new ShowDoor("wire", IPAddress.Any), new ShowDoor("web", IPAddress.Any)], node.Network.Doors);
        Assert.All(node.Network.Doors, d => Assert.True(d.Everywhere, d.Name));
    }

    [Fact]
    public async Task TheControlInputsDoorsAreTheirSocketsAddressesWhileTheyAreOpen()
    {
        var art = ControlRig.Address(111);
        var sacn = ControlRig.Address(112);
        await using (var rig = await ControlRig.StartAsync(service: true, artNetAddress: art,
            settings: s => s with { ArtNetEnabled = true, SacnEnabled = true, SacnInterface = sacn }))
        {
            Assert.True(await Eventually.TrueAsync(() => rig.Service!.Doors.Length == 3), string.Join(", ", rig.Service!.Doors));
            Assert.Equal<ShowDoor>([new ShowDoor("osc", IPAddress.Parse(ControlRig.Loopback)), new ShowDoor("artnet", IPAddress.Parse(art)),
                new ShowDoor("sacn", IPAddress.Parse(sacn))], rig.Service.Doors);
        }

        // OSC off, and neither DMX input on: no control door is open.
        await using var quiet = await ControlRig.StartAsync(service: true, settings: s => s with { OscEnabled = false });
        Assert.Empty(quiet.Service!.Doors);
    }

    [Fact]
    public async Task EachOpenNetworkSendIsADoorOnEveryInterfaceWhileItsSenderIsOpen()
    {
        await using var rig = new VideoIoRig();
        Assert.Empty(rig.Engine.VideoIo.Doors);
        var omt = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT Main output:op_1 ALWAYS"));
        await rig.Ok("SEND ADD NDI Wall output:op_1 ALWAYS");
        Assert.Equal<ShowDoor>([new ShowDoor("omt", IPAddress.Any), new ShowDoor("ndi", IPAddress.Any)], rig.Engine.VideoIo.Doors);
        Assert.All(rig.Engine.VideoIo.Doors, d => Assert.True(d.Everywhere, d.Name));

        // A send turned off closes its sender, and its door with it.
        await rig.Ok($"SEND ENABLE {omt} OFF");
        Assert.Equal<ShowDoor>([new ShowDoor("ndi", IPAddress.Any)], rig.Engine.VideoIo.Doors);
    }

    [Fact]
    public void ADoorOnAnyAddressOfEitherFamilyListensEverywhere()
    {
        Assert.True(new ShowDoor("wire", IPAddress.Any).Everywhere);
        Assert.True(new ShowDoor("wire", IPAddress.IPv6Any).Everywhere);
        Assert.True(new ShowDoor("wire", IPAddress.Any.MapToIPv6()).Everywhere);
        Assert.False(new ShowDoor("wire", Loopback).Everywhere);
        Assert.False(new ShowDoor("wire", IPAddress.Parse("192.168.10.20")).Everywhere);
    }
}
