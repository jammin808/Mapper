using System.Runtime.InteropServices;
using System.Text.Json;
using PlayMap.Devices.Midi;

namespace PlayMap.Engine.Tests.Control;

/// <summary>MIDI into the engine through a backend (the fake one: in-memory ports): a port is the surface its alias or match names,
/// its bytes fire the surface's bindings, and STATE says which port is which surface.</summary>
public sealed class MidiInputTests
{
    [Fact]
    public async Task APortIsItsSurfaceAndItsNotesFireTheBindings()
    {
        var midi = new FakeMidiBackend();
        midi.Plug("APC40 mkII");
        midi.Plug("nanoKONTROL2");
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, midi: midi);
        await rig.Ok("CONTROL SURFACE ADD APC40 MIDI \"APC40*\"");
        await rig.Ok("BIND ADD MIDI APC40 NOTE 1 53 TO PLAYLIST GO pl_show");
        await ControlRig.Until(() => rig.Service!.PortOf(rig.Engine.Store.Project.Control.Surfaces[0]) == "APC40 mkII", "APC40 on its port");
        // The same note from another port is not the APC40's.
        midi.Play("nanoKONTROL2", [0x90, 0x35, 0x7F], rig.Engine.Services.Mono.NowNs);
        midi.Play("APC40 mkII", [0x90, 0x36, 0x7F], rig.Engine.Services.Mono.NowNs);
        await ControlRig.Until(() => rig.State().GetProperty("control").GetProperty("last").ValueKind == JsonValueKind.Object
            && rig.State().GetProperty("control").GetProperty("last").GetProperty("input").GetString() == "MIDI APC40 NOTE 1 54", "the second note");
        Assert.True(rig.Engine.Playback.Lane("pl_show")!.IsIdle);
        midi.Play("APC40 mkII", [0x90, 0x35, 0x7F], rig.Engine.Services.Mono.NowNs);
        await ControlRig.Until(() => !rig.Engine.Playback.Lane("pl_show")!.IsIdle, "the playlist playing");

        var control = rig.State().GetProperty("control");
        var surface = control.GetProperty("surfaces")[0];
        Assert.Equal((true, "APC40 mkII", "green"), (surface.GetProperty("connected").GetBoolean(), surface.GetProperty("port").GetString(), surface.GetProperty("light").GetString()));
        var inputs = control.GetProperty("midi").GetProperty("inputs").EnumerateArray().ToList();
        Assert.Equal(2, inputs.Count);
        Assert.Equal(surface.GetProperty("id").GetString(), inputs.First(i => i.GetProperty("name").GetString() == "APC40 mkII").GetProperty("surface").GetString());
        Assert.Equal("fake", control.GetProperty("midi").GetProperty("backend").GetString());
        // The monitor's row is written once the binding's line has run (the playlist may play a moment before it).
        await ControlRig.Until(() => rig.Engine.ControlInput.Monitor(1)[0].Input == "MIDI APC40 NOTE 1 53", "the monitor's row");
        var monitor = JsonDocument.Parse((await rig.Ok("CONTROL MONITOR 4")).Payload!).RootElement;
        Assert.Equal("MIDI APC40 NOTE 1 53", monitor[0].GetProperty("input").GetString());
        Assert.StartsWith("bd", monitor[0].GetProperty("binding").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, monitor[1].GetProperty("binding").ValueKind);

        // Unplugged: the surface says so, and BIND LIST calls its binding unplugged.
        midi.Unplug("APC40 mkII");
        await ControlRig.Until(() => !rig.State().GetProperty("control").GetProperty("surfaces")[0].GetProperty("connected").GetBoolean(), "APC40 gone");
        Assert.Equal("unplugged", JsonDocument.Parse((await rig.Ok("BIND LIST APC40")).Payload!).RootElement[0].GetProperty("state").GetString());
        Assert.Equal("amber", rig.State().GetProperty("control").GetProperty("surfaces")[0].GetProperty("light").GetString());
    }

    [Fact]
    public async Task AnAliasNamesThisMachinesPortAndIsKeptOnTheNode()
    {
        var midi = new FakeMidiBackend();
        midi.Plug("USB MIDI 2");
        Core.Bus.ControlSettings? kept = null;
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, midi: midi);
        rig.Engine.ControlInput.Persist = s =>
        {
            kept = s;
            return null;
        };
        await rig.Ok("CONTROL SURFACE ADD APC40 MIDI \"APC40*\"");
        await rig.Ok("BIND ADD MIDI APC40 CC 1 7 TO SET sf_lobby opacity *");
        Assert.Equal("APC40 is 'USB MIDI 2' on this machine", (await rig.Ok("CONTROL ALIAS APC40 \"USB MIDI 2\"")).Effect);
        Assert.Equal("USB MIDI 2", kept!.SurfacePorts["APC40"]);
        await ControlRig.Until(() => rig.Service!.PortOf(rig.Engine.Store.Project.Control.Surfaces[0]) == "USB MIDI 2", "the alias");
        Assert.Equal("APC40 is already 'USB MIDI 2' on this machine", await rig.Refused("CONTROL ALIAS APC40 \"USB MIDI 2\""));
        Assert.Equal("APC40 finds its port by its own match again", (await rig.Ok("CONTROL ALIAS APC40 OFF")).Effect);
        Assert.Equal("no control surface Launchpad", await rig.Refused("CONTROL ALIAS Launchpad OFF"));
    }

    [Fact]
    public void AlsaRunsHereAndSaysWhyItHasNoPorts()
    {
        using var alsa = new AlsaSeqBackend();
        Assert.Equal("alsa-seq", alsa.Name);
        if (File.Exists("/dev/snd/seq"))
            return;
        // The reason is this machine's: without the ALSA library (Windows, a machine with no sound stack) it says so; with it, the
        // sequencer device is what is missing.
        var library = NativeLibrary.TryLoad("libasound.so.2", out var handle);
        if (library)
            NativeLibrary.Free(handle);
        Assert.Equal(library
            ? "no ALSA sequencer: /dev/snd/seq is missing (load the snd-seq module, or this machine has no sound devices)"
            : "no ALSA library (libasound.so.2) on this machine — MIDI input needs it", alsa.Unavailable);
        Assert.Empty(alsa.Ports());
        var refused = Assert.Throws<Core.Control.Midi.MidiUnavailableException>(() =>
            alsa.OpenInput(new Core.Control.Midi.MidiPortInfo("20:0", "x", true), new NullReceiver()));
        Assert.Equal(alsa.Unavailable, refused.Message);
    }

    private sealed class NullReceiver : Core.Control.Midi.IMidiReceiver
    {
        public void Receive(ReadOnlySpan<byte> message, long monoNs)
        {
            // Nothing arrives on a backend that cannot open.
        }
    }
}
