using System.Collections.Immutable;
using System.Net;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The networks' firewall row reads only the networks show traffic can cross: when every show door is bound to an address,
/// the connected networks whose adapters carry one, the row naming the doors, the address and the adapter; when a door listens on every
/// interface, every connected network, the row saying which doors do. A network whose adapters were not read may carry a door, so it is
/// read as one show traffic can cross. A Public network with no rule letting PlayMap in is amber; with none, one green row says what
/// was read and what was not.</summary>
public sealed class NetworkRowTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static readonly NetworkAdapter Ethernet = new(Guid.Parse("6f1d2c3b-0a4e-4b5c-9d8e-7f6a5b4c3d2e"), "Ethernet 2",
        [IPAddress.Parse("192.168.10.20"), IPAddress.Parse("fe80::1c2d:3e4f:5a6b:7c8d")]);

    private static readonly NetworkAdapter WiFi = new(Guid.Parse("0b1c2d3e-4f5a-4b6c-8d7e-9f0a1b2c3d4e"), "Wi-Fi", [IPAddress.Parse("10.0.0.57")]);

    private static NetworkProfile Network(string name, string category, NetworkAdapter adapter, bool rule = false) =>
        new(name, category, rule) { Adapters = [adapter], AdaptersRead = true };

    /// <summary>A connected network whose adapters were not read (its connections did not list, or name an adapter the machine does
    /// not list).</summary>
    private static NetworkProfile Unread(string name, string category, bool rule = false) => new(name, category, rule);

    private static ImmutableArray<MachineFact> Rows(ImmutableArray<ShowDoor> doors, params NetworkProfile[] networks) =>
        [.. MachineRows.Build(new MachineReadings { NetworksRead = true, Networks = [.. networks], Doors = doors }, Today)
            .Where(r => r.Item == "network" || r.Item.StartsWith("network.public:", StringComparison.Ordinal))];

    private static ShowDoor Door(string name, string address) => new(name, IPAddress.Parse(address));

    [Fact]
    public void ANodeBoundToItsShowAdapterReadsOnlyThatAdaptersNetworkAndNamesTheDoorsThere()
    {
        var rows = Rows([Door("wire", "192.168.10.20"), Door("web", "192.168.10.20"), Door("osc", "192.168.10.20")],
            Network("Show LAN", "public", Ethernet), Network("Venue Wi-Fi", "public", WiFi));
        var row = Assert.Single(rows);
        Assert.Equal(("network.public:Show LAN", Light.Amber, "public", (string?)null), (row.Item, row.Light, row.Value, row.Fix));
        Assert.Equal("the network Show LAN carries this node's line wire, web port and OSC on 192.168.10.20 through Ethernet 2 and is on the "
            + "Public firewall profile with no rule letting PlayMap in: make it Private in Settings ▸ Network & internet ▸ Show LAN ▸ Network "
            + "profile type", row.Words);
        Assert.Equal("Network Show LAN", MachineRows.Label(row.Item));
    }

    [Fact]
    public void AShowNetworkThatIsPrivateIsGreenAndSaysWhatItReadWhateverTheOthersAre()
    {
        var row = Assert.Single(Rows([Door("wire", "192.168.10.20"), Door("web", "192.168.10.20"), Door("sacn", "10.0.0.57")],
            Network("Show LAN", "private", Ethernet), Network("Venue Wi-Fi", "domain", WiFi), Network("Lobby", "public", WiFi with { Id = Guid.NewGuid(), Addresses = [IPAddress.Parse("172.16.0.9")] })));
        Assert.Equal(("network", Light.Green, "192.168.10.20, 10.0.0.57"), (row.Item, row.Light, row.Value));
        Assert.Equal("the show doors are on Show LAN (private): line wire and web port on 192.168.10.20 through Ethernet 2; on Venue Wi-Fi "
            + "(domain): sACN on 10.0.0.57 through Wi-Fi", row.Words);
        Assert.Equal("Firewall profiles", MachineRows.Label(row.Item));
    }

    [Fact]
    public void ANodeWithADoorOnEveryInterfaceReadsEveryNetworkAndSaysWhichDoorsListenEverywhere()
    {
        // OSC on the show adapter does not narrow it: the line wire and the web port listen on every interface.
        var rows = Rows([new ShowDoor("wire", IPAddress.IPv6Any), new ShowDoor("web", IPAddress.Any), Door("osc", "192.168.10.20")],
            Network("Show LAN", "private", Ethernet), Network("Venue Wi-Fi", "public", WiFi), Network("Backstage", "public", WiFi, rule: true));
        var row = Assert.Single(rows);
        Assert.Equal(("network.public:Venue Wi-Fi", Light.Amber, "public"), (row.Item, row.Light, row.Value));
        Assert.Equal("the network Venue Wi-Fi is on the Public firewall profile and no rule lets PlayMap in there, and this node's line wire and "
            + "web port listen on every interface, so show traffic can cross it: make it Private in Settings ▸ Network & internet ▸ Venue Wi-Fi "
            + "▸ Network profile type, or bind the node to its show adapter's address", row.Words);

        var green = Assert.Single(Rows([new ShowDoor("wire", IPAddress.Any)], Network("Show LAN", "private", Ethernet)));
        Assert.Equal(("network", Light.Green, "every interface"), (green.Item, green.Light, green.Value));
        Assert.Equal("this node's line wire listens on every interface, and no connected network is on the Public firewall profile without a "
            + "rule letting PlayMap in", green.Words);
    }

    [Fact]
    public void ANodeBoundToAnAddressNoNetworkCarriesHasNothingToReadAndSaysSo()
    {
        var row = Assert.Single(Rows([Door("wire", "127.0.0.1"), Door("web", "127.0.0.1")], Network("Venue Wi-Fi", "public", WiFi)));
        Assert.Equal(("network", Light.Green, "127.0.0.1"), (row.Item, row.Light, row.Value));
        Assert.Equal("the show doors are bound to 127.0.0.1, which no connected network carries", row.Words);
    }

    [Fact]
    public void ADoorsAddressMappedIntoIPv6IsItsIPv4Address()
    {
        var row = Assert.Single(Rows([new ShowDoor("sync", IPAddress.Parse("192.168.10.20").MapToIPv6())], Network("Show LAN", "public", Ethernet)));
        Assert.Equal(("network.public:Show LAN", Light.Amber), (row.Item, row.Light));
        Assert.Contains("clock sync on 192.168.10.20 through Ethernet 2", row.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ANodeThatNamesNoDoorReadsEveryNetworkAndSaysWhatToDoIfShowTrafficCrossesIt()
    {
        var row = Assert.Single(Rows([], Network("Venue Wi-Fi", "public", WiFi), Network("Show LAN", "private", Ethernet)));
        Assert.Equal(("network.public:Venue Wi-Fi", Light.Amber), (row.Item, row.Light));
        Assert.Equal("the network Venue Wi-Fi is on the Public firewall profile and no rule lets PlayMap in there: if show traffic crosses it, make "
            + "it Private in Settings ▸ Network & internet ▸ Venue Wi-Fi ▸ Network profile type", row.Words);
        var green = Assert.Single(Rows([], Network("Show LAN", "private", Ethernet)));
        Assert.Equal(("network", Light.Green, "none public"), (green.Item, green.Light, green.Value));
        Assert.Equal("no connected network is on the Public firewall profile without a rule letting PlayMap in", green.Words);
    }

    [Fact]
    public void ANetworkWhoseAdaptersWereNotReadMayCarryTheDoorsSoOnPublicWithNoRuleItIsAmber()
    {
        var rows = Rows([Door("wire", "192.168.10.20"), Door("web", "192.168.10.20")], Unread("Show LAN", "public"), Network("Office", "private", WiFi));
        var row = Assert.Single(rows);
        Assert.Equal(("network.public:Show LAN", Light.Amber, "public"), (row.Item, row.Light, row.Value));
        Assert.Equal("the network Show LAN is on the Public firewall profile and no rule lets PlayMap in there, and the adapters it goes "
            + "through could not be read, so show traffic can cross it: make it Private in Settings ▸ Network & internet ▸ Show LAN ▸ "
            + "Network profile type", row.Words);

        // A rule letting PlayMap in on Public, or another profile, keeps the network open to the show whichever adapters it goes through.
        var green = Assert.Single(Rows([Door("wire", "192.168.10.20")], Unread("Show LAN", "public", rule: true), Network("Office", "private", Ethernet)));
        Assert.Equal(("network", Light.Green, "192.168.10.20"), (green.Item, green.Light, green.Value));
        Assert.Equal("the show doors are on Office (private): line wire on 192.168.10.20 through Ethernet 2; the adapters of Show LAN could "
            + "not be read, and it is not on the Public firewall profile without a rule letting PlayMap in", green.Words);
    }

    [Fact]
    public void NoNetworkCarriesTheDoorsOnlyWhenEveryNetworksAdaptersWereRead()
    {
        var row = Assert.Single(Rows([Door("wire", "127.0.0.1")], Network("Venue Wi-Fi", "private", WiFi), Unread("Office", "domain"), Unread("Lab", "private")));
        Assert.Equal(("network", Light.Green, "127.0.0.1"), (row.Item, row.Light, row.Value));
        Assert.Equal("the show doors are bound to 127.0.0.1, which no connected network whose adapters were read carries; the adapters of "
            + "Office and Lab could not be read, and none of them is on the Public firewall profile without a rule letting PlayMap in", row.Words);
    }

    [Fact]
    public void ANetworksAdaptersAreReadWholeOnlyWhenEachOfItsConnectionsAdaptersIsTheMachines()
    {
        var unknown = Guid.Parse("11111111-2222-4333-8444-555555555555");
        var whole = NetworkProfile.Read("Show LAN", "public", false, [Ethernet.Id], [Ethernet, WiFi]);
        Assert.Equal(("Show LAN", "public", false, true), (whole.Name, whole.Category, whole.PublicRule, whole.AdaptersRead));
        Assert.Equal<NetworkAdapter>([Ethernet], whole.Adapters);
        // A connection whose adapter the machine does not list: the ones it does are kept, and the network is not read whole.
        var part = NetworkProfile.Read("Show LAN", "public", false, [Ethernet.Id, unknown], [Ethernet, WiFi]);
        Assert.Equal<NetworkAdapter>([Ethernet], part.Adapters);
        Assert.False(part.AdaptersRead);
        // The machine's adapter list empty or unread, the network's connections unread or none: nothing is known of its adapters.
        Assert.False(NetworkProfile.Read("Show LAN", "public", false, [Ethernet.Id], []).AdaptersRead);
        Assert.False(NetworkProfile.Read("Show LAN", "public", false, [Ethernet.Id], null).AdaptersRead);
        Assert.False(NetworkProfile.Read("Show LAN", "public", false, null, [Ethernet]).AdaptersRead);
        Assert.False(NetworkProfile.Read("Show LAN", "public", false, [], [Ethernet]).AdaptersRead);
        Assert.Empty(NetworkProfile.Read("Show LAN", "public", false, null, [Ethernet]).Adapters);

        // An empty interface list leaves every network unread: the bound doors' row reads each one as one show traffic can cross.
        var rows = Rows([Door("wire", "192.168.10.20")], NetworkProfile.Read("Show LAN", "public", false, [Ethernet.Id], []),
            NetworkProfile.Read("Office", "private", false, [WiFi.Id], []));
        Assert.Equal(("network.public:Show LAN", Light.Amber), (Assert.Single(rows).Item, Assert.Single(rows).Light));
    }

    [Fact]
    public void UnreadNetworksAreOneGreyRowWhateverTheDoors()
    {
        var rows = MachineRows.Build(new MachineReadings { NetworksRead = false, Doors = [Door("wire", "192.168.10.20")] }, Today);
        var row = Assert.Single(rows, r => r.Item.StartsWith("network", StringComparison.Ordinal));
        Assert.Equal(("network", Light.Grey, "unknown"), (row.Item, row.Light, row.Value));
    }

    [Fact]
    public void ANetworksAdaptersAreTheOnesItsConnectionsNameOnceEachInOrder()
    {
        var unknown = Guid.Parse("11111111-2222-4333-8444-555555555555");
        Assert.Equal<NetworkAdapter>([WiFi, Ethernet], NetworkAdapter.Of([WiFi.Id, unknown, Ethernet.Id, WiFi.Id], [Ethernet, WiFi]));
        Assert.Empty(NetworkAdapter.Of([unknown], [Ethernet, WiFi]));
        Assert.True(Ethernet.Carries(IPAddress.Parse("192.168.10.20").MapToIPv6()));
        Assert.False(Ethernet.Carries(IPAddress.Parse("10.0.0.57")));
    }
}
