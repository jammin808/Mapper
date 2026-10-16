using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Align;
using PlayMap.Engine.Devices;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>The double-correction guard (autoalign M4): a Panasonic projector linked to an output, its own geometry off, then on
/// (<c>GMMI0=+00001</c>) after ALIGN ACCEPT put a measured correction on that output: the next read lights its <c>device:&lt;id&gt;</c>
/// context item amber with the words and the DEVICE RAW line that turns it off; DEVICE NATIVE reads it live and says the same; a read
/// never writes to the projector.</summary>
public sealed class DoubleCorrectionTests(ITestOutputHelper log)
{
    private static JsonElement DeviceItem(AlignRig rig, string id) =>
        JsonDocument.Parse(rig.Engine.Context()).RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == "device:" + id).Clone();

    [Fact]
    public async Task AProjectorsOwnGeometryOnUnderAMeasuredCorrectionLightsItsDeviceAmber()
    {
        await using var projector = new PanasonicSimulator { Geometry = "+00000" };
        var credentials = new NodeCredentialStore(Path.Combine(Directory.CreateTempSubdirectory("playmap-keys-").FullName, "keys"));
        await using var devices = new DeviceService(credentials, MonoClock.Instance, new DeviceServiceOptions
        {
            PjLinkUdpPort = -1,
            Timings = DriverTimings.Fast,
            Manager = new DeviceManagerOptions { TickMs = 50 },
        });
        await using var rig = new AlignRig(AlignRig.Project(2, "flat-screen:tile2x1"), configure: s => s with { Devices = devices });
        await using var attached = devices.Attach(rig.Engine);
        var id = (await rig.Ok($"DEVICE ADD P2 panasonic 127.0.0.1:{projector.Port} OUTPUT op2")).Trim();
        await Wall.WallRig.Until(() => devices.Manager.Driver(id) is not null, "the link to reach the manager");

        // Before any measured correction, the projector guards nothing: its own geometry being off or on is no fight.
        var native = AlignRig.Json(await rig.Ok($"DEVICE NATIVE {id}"));
        Assert.Equal("geometry", native.GetProperty("settings")[0].GetProperty("name").GetString());
        Assert.False(native.GetProperty("settings")[0].GetProperty("on").GetBoolean());
        Assert.Equal("", native.GetProperty("fix").GetString());
        Assert.NotEqual("amber", DeviceItem(rig, id).GetProperty("light").GetString());

        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        // The projector's own geometry is on when the correction is accepted: the read the accept makes due finds it.
        projector.Geometry = "+00001";
        await rig.Ok($"ALIGN ACCEPT {run}");
        await Wall.WallRig.Until(() => DeviceItem(rig, id).GetProperty("light").GetString() == "amber", "the guard's read");
        var item = DeviceItem(rig, id);
        log.WriteLine(item.GetProperty("state").GetString());
        Assert.Contains("own geometry is on while PlayMap corrects op2", item.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.Contains("two warps fight", item.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.Contains($"DEVICE RAW {id} VXX:GMMI0=+00000", item.GetProperty("state").GetString(), StringComparison.Ordinal);
        var row = rig.State().GetProperty("align").GetProperty("devices").EnumerateArray().Single();
        Assert.True(row.GetProperty("geometry").GetBoolean());
        Assert.Equal("amber", row.GetProperty("light").GetString());
        Assert.Equal("op2", row.GetProperty("outputs")[0].GetString());

        // Preflight says it too, amber with the line that turns it off.
        var rows = await new AlignPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var preflight = Assert.Single(rows, r => r.Id == "align:native:" + id);
        Assert.Equal((Light.Amber, $"DEVICE RAW {id} VXX:GMMI0=+00000"), (preflight.Light, preflight.Fix));

        native = AlignRig.Json(await rig.Ok($"DEVICE NATIVE {id}"));
        Assert.Equal($"DEVICE RAW {id} VXX:GMMI0=+00000", native.GetProperty("fix").GetString());
        Assert.Equal("op2", native.GetProperty("guards")[0].GetString());
        // The fix line runs; the next read (a poll interval later) finds the geometry off, and the item is the device's own light again.
        await rig.Ok(native.GetProperty("fix").GetString()!);
        Assert.Equal("+00000", projector.Geometry);
        rig.Clock.AdvanceMs(60_000);
        await Wall.WallRig.Until(() => DeviceItem(rig, id).GetProperty("light").GetString() != "amber", "the guard to clear");
        // Reads never wrote: the only writes the projector heard were the fix line's.
        Assert.Single(projector.Received, c => c.StartsWith("VXX:", StringComparison.Ordinal));
    }
}
