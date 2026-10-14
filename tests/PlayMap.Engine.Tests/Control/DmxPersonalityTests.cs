using System.Net;
using System.Text.Json;
using PlayMap.Core.Dmx;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Control;

/// <summary>The built-in DMX personality: one line installs a channel block a lighting desk patches like a fixture, and the desk's
/// frames drive the show through the router and the gate.</summary>
public sealed class DmxPersonalityTests
{
    /// <summary>Three surfaces (one showing a playlist) and four outputs: 3 × 4 + 4 = 16 channels.</summary>
    private static Project Show()
    {
        var p = BindingEngineTests.Show();
        var outputs = Enumerable.Range(1, 4).Select(i => new Output($"op_{i}", $"Out {i}", OutputKind.Display, "", 1920, 1080, 60, OutputMode.Canvas, null,
            SourceRef.None, new RectD(0, 0, 1920, 1080), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, 1920, 1080), 0, true)).ToList();
        return p with
        {
            Stage = p.Stage with
            {
                Surfaces = [.. p.Stage.Surfaces, BindingEngineTests.Surface("sf_wall", "Wall", SourceRef.Playlist("pl_show"))],
            },
            Outputs = [.. outputs],
        };
    }

    [Fact]
    public async Task ASixteenChannelPersonalityInstalledByOneLineDrivesSurfaceOpacityThroughTheGate()
    {
        var address = ControlRig.Address(31);
        await using var rig = await ControlRig.StartAsync(Show(), service: true, artNetAddress: address, settings: s => s with { ArtNetEnabled = true, ArtNetInterface = address });
        var reply = await rig.Ok("BIND ADD {\"personality\":\"playmap\",\"universe\":3,\"address\":101}");
        using var doc = JsonDocument.Parse(reply.Payload!);
        var root = doc.RootElement;
        Assert.Equal(12, root.GetProperty("bindings").GetArrayLength());
        var sheet = root.GetProperty("sheet").GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16, sheet.Length);
        Assert.Equal(["ch101 Lobby intensity", "ch102 Lobby source index (unused)", "ch103 Lobby speed (unused)", "ch104 Lobby shown"], sheet[..4]);
        Assert.Equal(["ch109 Wall intensity", "ch110 Wall source index", "ch111 Wall speed", "ch112 Wall shown", "ch113 Out 1 master"], sheet[8..13]);
        Assert.Equal("ch116 Out 4 master", sheet[15]);
        // One undo step for the whole block.
        Assert.Equal(12, rig.Engine.Store.Project.Control.Bindings.Length);

        // The desk sends a frame: Lobby at 50 % (128), Stage at full, Wall's index to item 2, Out 2's master at 40 %.
        using var desk = ControlRig.Udp(ControlRig.Address(32), ArtNet.Port);
        var channels = new byte[116];
        channels[100] = 128;
        channels[103] = 255;
        channels[104] = 255;
        channels[107] = 255;
        channels[108] = 255;
        channels[109] = 255;
        channels[110] = 128;
        channels[113] = 102;
        var packet = new byte[18 + 512];
        var n = ArtNet.WriteDmx(packet, 1, 3, channels);
        desk.SendTo(packet.AsSpan(0, n), System.Net.Sockets.SocketFlags.None, new IPEndPoint(IPAddress.Parse(address), ArtNet.Port));
        await ControlRig.Until(() => Math.Abs(rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity - 128 / 255.0) < 1e-4, "Lobby at 50 %");
        await ControlRig.Until(() => Math.Abs(rig.Engine.Store.Project.FindOutput("op_2")!.Color.Brightness - 0.4) < 1e-6, "Out 2 at 40 %");
        await ControlRig.Until(() => rig.Engine.Playback.Lane("pl_show")?.ItemId == "it_b", "Wall's playlist at item 2");
        var p = rig.Engine.Store.Project;
        Assert.Equal(1, p.FindSurface("sf_stage")!.Opacity, 6);
        Assert.Equal(0.4, p.FindOutput("op_2")!.Color.Brightness, 6);
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        // The lines went through the router and the gate: an edge refused there is journalled with its binding as who, and a
        // channel's moves are journalled once as a gesture.
        Assert.Contains(rig.Engine.Journal.Recent(100), r => r.Kind == "refused" && r.Text == "Lobby is already shown" && r.Who!.StartsWith("binding:bd", StringComparison.Ordinal)
            && r.Line == "SURFACE SHOW sf_lobby ON");
        await ControlRig.Until(() => rig.Engine.Journal.Recent(100).Any(r => r.Kind == "control" && r.Text.StartsWith("ARTNET 3 110 → PLAYLIST GO Show #*", StringComparison.Ordinal)),
            "the index channel's gesture");
        var control = rig.State().GetProperty("control");
        Assert.True(control.GetProperty("dmx").GetProperty("artnet").GetProperty("open").GetBoolean());
        Assert.StartsWith("ARTNET 3 ", control.GetProperty("last").GetProperty("input").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersonalityThatRunsPastTheUniverseIsRefused()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        Assert.Equal("the personality needs 16 channels — from 500 it runs past 512", await rig.Refused("BIND ADD {\"personality\":\"playmap\",\"address\":500}"));
        Assert.Equal("no DMX personality media-8 — \"playmap\" is built in", await rig.Refused("BIND ADD {\"personality\":\"media-8\"}"));
        Assert.Empty(rig.Engine.Store.Project.Control.Bindings);
    }
}
