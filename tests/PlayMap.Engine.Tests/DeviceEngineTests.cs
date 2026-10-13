using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Engine.Tests;

/// <summary>The DEVICE verbs through the engine against the Devices module's PJLink projector simulator: a link made
/// and kept in the project, a password from the credential store, the projector's own receipt for each command,
/// STATE's device rows and the context's light for a projector in error, refusals that change nothing, and a scan
/// that readies a linked projector and takes its black frame.</summary>
public sealed class DeviceEngineTests
{
    private const string Password = "JBMIA-pj1";

    private static Project WithProjector() => Project.CreateDefault("Devices").WithStage(s => s with
    {
        Projectors =
        [
            new Projector("pj00000001", "Left", Intrinsics.FromThrowRatio(256, 160, 1.4), LensDistortion.None,
                Pose.LookAt(new Vec3d(0.1, 1.5, 2.3), new Vec3d(0, 0.2, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0),
        ],
    });

    private static JsonElement Row(OpsRig rig, string id) =>
        rig.State().GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == id);

    [Fact]
    public async Task ADeviceLinkDrivesThePjLinkSimulatorWithItsOwnReceipts()
    {
        await using var projector = new PjLinkSimulator(Password);
        var rig = new OpsRig(project: WithProjector());
        try
        {
            var id = await rig.Ok($"DEVICE ADD \"Left box\" pjlink 127.0.0.1:{projector.Port} PROJECTOR Left");
            Assert.StartsWith(Ids.Device, id, StringComparison.Ordinal);
            var link = rig.Engine.Store.Project.FindDevice(id)!;
            Assert.Equal(("pjlink", "127.0.0.1", projector.Port, "pj00000001", id), (link.Protocol, link.Host, link.Port, link.ProjectorId, link.CredentialRef));
            Assert.False((await rig.Run($"DEVICE ADD Again pjlink 127.0.0.1:{projector.Port}")).Ok);

            // Without its password the projector refuses: the receipt says the projector was not reached.
            var refused = await rig.Run($"DEVICE POWER {id} ON");
            Assert.False(refused.Ok);
            Assert.Contains("not reached", refused.Payload, StringComparison.Ordinal);
            Assert.Equal("0", projector.Power);

            await rig.Ok($"CREDENTIAL SET {id} {Password}");
            var on = await rig.Run($"DEVICE POWER {id} ON");
            Assert.True(on.Ok, on.Payload);
            Assert.Equal("1", projector.Power);
            var receipt = JsonDocument.Parse(on.Payload!).RootElement;
            Assert.Contains(receipt.GetProperty("stage").GetString(), new[] { "accepted", "observed" });
            Assert.Equal(receipt.GetProperty("stage").GetString() == "observed", on.Applied);

            var shut = await rig.Run($"DEVICE SHUTTER \"Left box\" CLOSE");
            Assert.True(shut.Ok, shut.Payload);
            Assert.Equal("31", projector.Avmt);
            Assert.True((await rig.Run($"DEVICE INPUT {id} 32")).Ok);
            Assert.Equal("32", projector.Input);

            Assert.True(await OpsRig.Eventually(() =>
            {
                var row = Row(rig, id);
                return row.GetProperty("power").GetString() == "on" && row.GetProperty("shutter").GetString() == "closed"
                    && row.GetProperty("lampHours").GetArrayLength() == 1;
            }), Row(rig, id).ToString());
            var state = Row(rig, id);
            Assert.Equal(1234, state.GetProperty("lampHours")[0].GetInt32());
            Assert.Equal("green", state.GetProperty("light").GetString());
            Assert.True(state.GetProperty("credential").GetBoolean());
            Assert.Equal("input 32", state.GetProperty("last").GetProperty("command").GetString());

            // A lamp error makes the projector's context item red, and STATE lists it.
            projector.Erst = "020000";
            Assert.True((await rig.Run($"DEVICE FREEZE {id} ON")).Ok);
            Assert.True(await OpsRig.Eventually(() => Row(rig, id).GetProperty("light").GetString() == "red"), Row(rig, id).ToString());
            Assert.Equal("error", Row(rig, id).GetProperty("errors")[0].GetProperty("severity").GetString());
            var item = rig.Context().GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "device:" + id);
            Assert.Equal("red", item.GetProperty("light").GetString());
            Assert.Equal("red", rig.Context().GetProperty("light").GetString());

            // Refusals change nothing: an unknown protocol, a command PJLink has no word for, an unpaired peer, an
            // unknown link.
            Assert.False((await rig.Run("DEVICE ADD Box telnet 10.0.0.9")).Ok);
            var lens = await rig.Run($"DEVICE LENS {id} ZOOM TO 100");
            Assert.False(lens.Ok);
            Assert.Contains("refused", lens.Payload, StringComparison.Ordinal);
            var unpaired = await rig.Run($"DEVICE POWER {id} OFF", OpsRig.Desk with { Paired = false });
            Assert.False(unpaired.Ok);
            Assert.StartsWith("not paired", unpaired.Payload, StringComparison.Ordinal);
            Assert.Equal("1", projector.Power);
            Assert.False((await rig.Run("DEVICE POWER dv99999999 OFF")).Ok);

            Assert.Equal(VerbRisk.High, VerbCatalog.Find("DEVICE POWER")!.Risk);
            Assert.Equal(VerbRisk.High, VerbCatalog.Find("DEVICE SHUTTER")!.Risk);
            Assert.Equal(VerbRisk.Low, VerbCatalog.Find("DEVICE LIST")!.Risk);

            var get = JsonDocument.Parse(await rig.Ok($"DEVICE GET {id}")).RootElement;
            Assert.Contains("shutter", get.GetProperty("capabilities").GetString(), StringComparison.Ordinal);
            Assert.NotEmpty(get.GetProperty("prepare").EnumerateArray());
            Assert.Equal(1, JsonDocument.Parse(await rig.Ok("DEVICE LIST")).RootElement.GetArrayLength());

            // The projector a link controls cannot be removed from under it; the link can, and UNDO brings it back.
            Assert.False((await rig.Run("REMOVE pj00000001")).Ok);
            await rig.Ok($"DEVICE REMOVE {id}");
            Assert.Empty(rig.Engine.Store.Project.Stage.Devices);
            await rig.Ok("UNDO");
            Assert.NotNull(rig.Engine.Store.Project.FindDevice(id));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task AGenericEditMakesAndChangesALinkThatTheProjectKeeps()
    {
        var rig = new OpsRig(project: WithProjector());
        try
        {
            var id = await rig.Ok("ADD device {\"name\":\"Right box\",\"protocol\":\"christie\",\"host\":\"10.0.0.22\",\"projectorId\":\"pj00000001\"}");
            Assert.StartsWith(Ids.Device, id, StringComparison.Ordinal);
            Assert.Equal(id, rig.Engine.Store.Project.FindDevice(id)!.CredentialRef);
            await rig.Ok($"SET {id} pollIntervalMs 2000");
            Assert.Equal(2000, rig.Engine.Store.Project.FindDevice(id)!.PollIntervalMs);
            Assert.False((await rig.Run($"SET {id} protocol \"smoke-signals\"")).Ok);
            Assert.False((await rig.Run($"SET {id} projectorId \"pj99999999\"")).Ok);
            Assert.Contains("\"devices\":[{", rig.Engine.ProjectJson().Json, StringComparison.Ordinal);
            var saved = Path.Combine(rig.Folder, "show.playmap");
            await rig.Ok("PROJECT SAVE " + saved);
            Assert.Equal(rig.Engine.Store.Project.FindDevice(id), ProjectJson.Deserialize(File.ReadAllText(saved)).FindDevice(id));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task ASimulatedScanReadiesALinkedProjectorAndTakesItsBlackFrame()
    {
        await using var projector = new PjLinkSimulator();
        projector.Power = "1";
        projector.Avmt = "31";
        var rig = new OpsRig(project: WithProjector());
        try
        {
            await rig.Ok($"DEVICE ADD Box pjlink 127.0.0.1:{projector.Port} PROJECTOR pj00000001");
            Assert.True((await rig.Run("SCAN START sim MODE FAST")).Ok);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.Current?.State is "done" or "failed", 120_000));
            Assert.Equal("done", rig.Scanner.Current!.State);
            var lines = projector.Received.ToArray();
            // Prepare opened the shutter; the black frame closed it and opened it again.
            Assert.Contains("%1AVMT 30", lines);
            Assert.Contains("%1AVMT 31", lines);
            Assert.True(Array.LastIndexOf(lines, "%1AVMT 30") > Array.IndexOf(lines, "%1AVMT 31"));
            Assert.Equal("30", projector.Avmt);
            Assert.Contains("Box", rig.Engine.Store.Project.Stage.Scans[0].Notes, StringComparison.Ordinal);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
