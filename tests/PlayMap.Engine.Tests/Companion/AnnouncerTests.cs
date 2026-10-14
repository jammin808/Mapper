using System.Net;
using PlayMap.Core.Bus;
using PlayMap.Engine.Companion;
using PlayMap.Net.Discovery;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>The node's announcement: its record carries the web port and the TXT keys Companion's module reads, and is not made before
/// the web port is open; a responder the machine refuses becomes an entry in STATE's faults, a fault.raised event and a red preflight
/// row — the node runs on.</summary>
public sealed class AnnouncerTests
{
    [Fact]
    public void TheRecordWaitsForTheWebPortAndCarriesTheModulesKeys()
    {
        var settings = NodeSettings.CreateDefault("Stage left") with { NodeId = "a1b2c3d4" };
        Assert.Null(Announcer.RecordFor(settings, 0, 9941));
        var record = Announcer.RecordFor(settings, 9940, 9941)!;
        Assert.Equal(("Stage left", "_playmap._tcp", 9940), (record.Instance, record.Type, record.Port));
        Assert.Equal("a1b2c3d4", record.Txt["id"]);
        Assert.Equal("1", record.Txt["proto"]);
        Assert.Equal("/ws", record.Txt["ws"]);
        Assert.Equal("9941", record.Txt["wire"]);
    }

    [Fact]
    public async Task ARefusedBindIsAFaultInStateAnEventAndARedRow()
    {
        await using var announcer = new Announcer(new MdnsOptions { Bind = IPAddress.Parse("192.0.2.1") }, () => null);
        announcer.Start();
        var fault = Assert.Single(announcer.Faults());
        Assert.Equal("mdns", fault.Part);

        await using var rig = new CompanionRig(s => s with { Faults = announcer });
        rig.Engine.FactsChanged();
        var faults = rig.State().GetProperty("faults").EnumerateArray().ToList();
        Assert.Contains(faults, f => f.GetProperty("part").GetString() == "mdns"
            && f.GetProperty("words").GetString()!.Contains("could not open 192.0.2.1:5353", StringComparison.Ordinal));
        var events = CompanionRig.Json(await rig.Ok("EVENTS")).GetProperty("events").EnumerateArray();
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "fault.raised" && e.GetProperty("part").GetString() == "mdns");

        var rows = await new CompanionPreflight(announcer.Faults).RunAsync(
            new PreflightContext(rig.Engine, Core.Preflight.PreflightDepth.Quick, Armed: false), CancellationToken.None);
        Assert.Contains(rows, r => r.Light == Light.Red && r.Id == "announce:mdns");
    }
}
