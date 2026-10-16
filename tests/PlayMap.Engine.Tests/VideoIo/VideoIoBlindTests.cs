using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>Video I/O under blind edit: a send or a live input staged in a draft is never sent or received — the hub reads the live
/// project only — and the answer says it is staged rather than naming a sender or a connection that does not exist; once the draft
/// lands at its boundary the hub makes them. Armed, an operator stops a send by its own verb as by a generic edit.</summary>
public sealed class VideoIoBlindTests
{
    [Fact]
    public async Task ASendAndAnInputStagedBlindAreNeitherSentNorReceivedUntilTheDraftLands()
    {
        using var omt = new FakeVideoProvider(LiveProtocol.Omt, new ManualClock(5_000_000_000));
        await using var rig = new ShowRig(services: s => s with { LiveInputs = [omt], SendProviders = [omt] });
        var hub = rig.Engine.VideoIo;
        try
        {
            await rig.Ok("BLIND ON");
            var add = await rig.Run("SEND ADD OMT Main output:op_1");
            Assert.True(add.Ok, add.Payload);
            Assert.Equal("staged Main from output:op_1 in the blind draft — it sends when the blind draft lands", add.Effect);
            var reply = JsonDocument.Parse(add.Payload!).RootElement;
            var send = reply.GetProperty("id").GetString()!;
            Assert.Equal("", reply.GetProperty("fullName").GetString());
            var input = await rig.Run("INPUT ADD \"omt:STUDIO (Cam 1)\" Cam");
            Assert.True(input.Ok, input.Payload);
            Assert.Equal("staged", JsonDocument.Parse(input.Payload!).RootElement.GetProperty("state").GetString());
            Assert.Equal("staged Cam (omt:STUDIO (Cam 1)) in the blind draft — it is received when the blind draft lands", input.Effect);
            var off = await rig.Run($"SEND ENABLE {send} OFF");
            Assert.Equal("Main stops when the blind draft lands", off.Effect);
            await rig.Ok($"SEND ENABLE {send} ON");

            // The live project, the hub and the providers have none of it, however often the hub ticks.
            hub.Tick();
            Assert.Empty(rig.Engine.Store.Project.Sends);
            Assert.Empty(omt.Sinks);
            Assert.Empty(omt.Opened);
            Assert.True(hub.Plan.IsEmpty);

            var commit = await rig.Run("BLIND COMMIT");
            Assert.True(commit.Ok, commit.Payload);
            rig.Advance(34);
            await rig.Ticked();
            hub.Tick();
            Assert.Single(rig.Engine.Store.Project.Sends);
            Assert.Equal("FAKEHOST (Main)", Assert.Single(omt.Sinks).FullName);
            Assert.Equal("STUDIO (Cam 1)", Assert.Single(omt.Opened).Address);
            Assert.Equal(send, Assert.Single(hub.Plan).Id);
        }
        finally
        {
            hub.Dispose();
        }
    }

    [Fact]
    public async Task ArmedAnOperatorStopsASendByItsOwnVerbAsByAGenericEditAndARemoteByNeither()
    {
        using var omt = new FakeVideoProvider(LiveProtocol.Omt, new ManualClock(5_000_000_000));
        await using var rig = new ShowRig(services: s => s with { LiveInputs = [omt], SendProviders = [omt] });
        var hub = rig.Engine.VideoIo;
        try
        {
            var a = JsonDocument.Parse(await rig.Ok("SEND ADD OMT A output:op_1")).RootElement.GetProperty("id").GetString()!;
            var b = JsonDocument.Parse(await rig.Ok("SEND ADD OMT B output:op_1")).RootElement.GetProperty("id").GetString()!;
            await rig.Ok("SHOW ARM");
            Assert.Contains("armed", await rig.Refused($"SEND ENABLE {a} OFF", ShowRig.Remote), StringComparison.Ordinal);
            Assert.Contains("armed", await rig.Refused($"SET {b} enabled false", ShowRig.Remote), StringComparison.Ordinal);
            await rig.Ok($"SEND ENABLE {a} OFF");
            await rig.Ok($"SET {b} enabled false");
            Assert.All(rig.Engine.Store.Project.Sends, s => Assert.False(s.Enabled));
        }
        finally
        {
            hub.Dispose();
        }
    }
}
