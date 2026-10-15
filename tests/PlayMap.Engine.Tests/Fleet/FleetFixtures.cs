using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Three machines as a board knows them (the same facts as Core's FleetGraphTests): FOH-A green, FOH-B its backup and
/// follower dropping frames on one output, FOH-C gone quiet 7 s after its last record.</summary>
internal static class FleetFixtures
{
    public static HealthFacts Known() => new()
    {
        Node = "0badc0de",
        Name = "FOH-A",
        AtUnixMs = 1_760_000_000_000,
        Role = "primary",
        Epoch = 7,
        Build = "0.2.0",
        Rev = 12,
        Outputs = [new HealthOutput("op_1", "Left", true, 60, 0, 0, "gl-window"), new HealthOutput("op_2", "Right", true, 60, 0, 0, "gl-window")],
        Projectors = [new HealthProjector("pj1", "P1", Light.Green, "calibrated, 0.8 px · on", "op_1")],
        Renderer = "llvmpipe",
        RenderPath = "gl",
        CpuPct = 23,
        MemMb = 1400,
        MemFreeMb = 8000,
        DiskFreeGb = 812,
        GpuPct = 40,
        GpuTempC = 61,
        NetMbps = 12.5,
        UptimeS = 86400,
        SyncRole = "leader",
        SyncUncertaintyMs = 0,
        DevicesTotal = 1,
        DevicesGreen = 1,
        MediaItems = 42,
        LaneMs = 12,
        RenderMs = 4.1,
    };

    public static IReadOnlyList<FleetMachine> ThreeNodes()
    {
        var a = Known();
        var b = a with
        {
            Node = "9f8e7d6c",
            Name = "FOH-B",
            Role = "backup",
            Of = "0badc0de",
            SyncRole = "follower",
            SyncLeader = "0badc0de",
            SyncUncertaintyMs = 0.4,
            Outputs = [new HealthOutput("op_1", "Left", true, 59, 4, 1, "gl-window")],
            Projectors = [],
        };
        var c = a with { Node = "c0ffee00", Name = "FOH-C", Role = "solo", Projectors = [], Outputs = [new HealthOutput("op_9", "Rear", true, 60, 0, 0, "gl-window")] };
        return
        [
            new("0badc0de", "FOH-A", FleetHeard.Live, HealthRecord.Of(a), "", Self: true),
            new("9f8e7d6c", "FOH-B", FleetHeard.Live, HealthRecord.Of(b), "subscribed over TLS, certificate pinned") { SilentS = 0.4 },
            new("c0ffee00", "FOH-C", FleetHeard.Gone, HealthRecord.Of(c), "FOH-C's health feed ended") { SilentS = 7 },
        ];
    }
}
