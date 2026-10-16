using System.Text.Json;
using PlayMap.Core.Control;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Control;

/// <summary>Bindings in the engine: made, listed, forgotten and switched by their verbs; fired by CONTROL INJECT through the router
/// and the show's gate like any line; learnt from the next control that moves; journalled once per gesture.</summary>
public sealed class BindingEngineTests
{
    internal static Surface Surface(string id, string name, SourceRef? source = null) =>
        new(id, name, "", Transform3.Identity, [], SurfaceContent.Empty with { Source = source ?? SourceRef.None }, 1, [], true, false, "#FFFFFF", []);

    internal static Project Show() => Project.CreateDefault("Show") with
    {
        Stage = Project.CreateDefault("Show").Stage with { Surfaces = [Surface("sf_lobby", "Lobby"), Surface("sf_stage", "Stage")] },
        Playlists = [new Playlist("pl_show", "Show", [PlaylistItem.For("it_a", SourceRef.Solid("#FF0000"), "Red") with { DurationNs = 5_000_000_000 },
            PlaylistItem.For("it_b", SourceRef.Solid("#00FF00"), "Green") with { DurationNs = 5_000_000_000 }], false, null)],
    };

    [Fact]
    public async Task ABindingIsMadeInWordsListedAndFiredThroughTheRouter()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        var refused = await rig.Refused("BIND ADD MIDI APC40 CC 1 48 TO SET sf_lobby opacity *");
        Assert.Equal("no control surface APC40 — CONTROL SURFACE ADD APC40 MIDI names it", refused);
        var surface = await rig.Ok("CONTROL SURFACE ADD APC40 MIDI \"APC40*\"");
        Assert.StartsWith("{\"id\":\"ct", surface.Payload, StringComparison.Ordinal);
        Assert.Equal("there is already a surface called apc40", await rig.Refused("CONTROL SURFACE ADD apc40 MIDI"));

        var made = await rig.Ok("BIND ADD MIDI APC40 CC 1 48 TO SET sf_lobby opacity *");
        using var doc = JsonDocument.Parse(made.Payload!);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("MIDI APC40 CC 1 48 → Lobby opacity", doc.RootElement.GetProperty("words").GetString());
        var binding = rig.Engine.Store.Project.Control.Bindings.Find(id)!;
        Assert.Equal(("MIDI APC40 CC 1 48", "SET sf_lobby opacity *", true), (binding.Input, binding.Target, binding.Enabled));

        // A second binding on the same input is kept and says so.
        var also = await rig.Ok("BIND ADD MIDI APC40 CC 1 48 TO SET sf_stage opacity *");
        Assert.Contains(id, also.Payload, StringComparison.Ordinal);
        Assert.Contains("also bound to " + id, also.Effect, StringComparison.Ordinal);

        var list = JsonDocument.Parse((await rig.Ok("BIND LIST APC40")).Payload!).RootElement;
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("waiting", list[0].GetProperty("state").GetString());
        Assert.Equal("absolute", list[0].GetProperty("behaviour").GetString());

        // A takeover reads the target: Lobby is at 1, so a fader at 0.25 is held until it reaches it; Stage too.
        var injected = JsonDocument.Parse((await rig.Ok("CONTROL INJECT MIDI APC40 CC 1 48 0.25")).Payload!).RootElement;
        Assert.Equal(0, injected.GetProperty("fired").GetArrayLength());
        Assert.Equal("up", JsonDocument.Parse((await rig.Ok("BIND LIST " + id)).Payload!).RootElement[0].GetProperty("takeover").GetString());
        var fired = JsonDocument.Parse((await rig.Ok("CONTROL INJECT MIDI APC40 CC 1 48 1")).Payload!).RootElement.GetProperty("fired");
        Assert.Equal(2, fired.GetArrayLength());
        fired = JsonDocument.Parse((await rig.Ok("CONTROL INJECT MIDI APC40 CC 1 48 0.5")).Payload!).RootElement.GetProperty("fired");
        Assert.Equal("SET sf_lobby opacity 0.5", fired[0].GetProperty("line").GetString());
        Assert.True(fired[0].GetProperty("ok").GetBoolean());
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity, 9);
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf_stage")!.Opacity, 9);

        // STATE: the last input and the bindings' counts.
        var control = rig.State().GetProperty("control");
        Assert.Equal("MIDI APC40 CC 1 48", control.GetProperty("last").GetProperty("input").GetString());
        Assert.Equal(2, control.GetProperty("bindings").GetProperty("count").GetInt32());

        Assert.Equal("nothing is bound to MIDI CC 1 49 — BIND LIST shows what is", await rig.Refused("CONTROL INJECT MIDI CC 1 49 0.5"));
        Assert.Equal("CONTROL INJECT is a person's test of a control — never from an assistant",
            await rig.Refused("CONTROL INJECT MIDI APC40 CC 1 48 0.2", new SessionInfo("a1", "assistant", "127.0.0.1", true, "Assistant", true)));
        Assert.Equal("APC40 has 2 bindings — BIND FORGET them first (BIND LIST APC40 lists them)", await rig.Refused("CONTROL SURFACE REMOVE APC40"));
        var forgot = await rig.Ok("BIND FORGET MIDI APC40 CC 1 48");
        Assert.Equal("2 bindings forgotten", forgot.Effect);
        Assert.Equal("no binding matches — BIND LIST shows them", await rig.Refused("BIND FORGET MIDI APC40 CC 1 48"));
        Assert.Empty(rig.Engine.Store.Project.Control.Bindings);
    }

    [Fact]
    public async Task AButtonFiresOnPressAndASwitchFollowsTheFact()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(Show(), clock: clock);
        await rig.Ok("BIND ADD MIDI NOTE 1 53 TO PLAYLIST GO pl_show");
        await rig.Ok("BIND ADD OSC /blackout TO BLACKOUT TOGGLE");
        var fired = JsonDocument.Parse((await rig.Ok("CONTROL INJECT MIDI NOTE 1 53 1")).Payload!).RootElement.GetProperty("fired");
        Assert.Equal("PLAYLIST GO pl_show", fired[0].GetProperty("line").GetString());
        Assert.Equal("it_a", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        Assert.Equal(Core.Playback.TransportState.Playing, rig.Engine.Playback.Lane("pl_show")!.Clip.State);
        // A release fires nothing on a trigger.
        Assert.Equal(0, JsonDocument.Parse((await rig.Ok("CONTROL INJECT MIDI NOTE 1 53 0")).Payload!).RootElement.GetProperty("fired").GetArrayLength());
        // An OSC control rising through 56 % presses: the node toggles its own fact; falling through 44 % releases.
        await rig.Ok("CONTROL INJECT OSC /blackout 1");
        Assert.True(rig.Engine.Air.Blackout);
        clock.AdvanceMs(100);
        await rig.Ok("CONTROL INJECT OSC /blackout 0");
        Assert.True(rig.Engine.Air.Blackout);
        clock.AdvanceMs(100);
        await rig.Ok("CONTROL INJECT OSC /blackout 1");
        Assert.False(rig.Engine.Air.Blackout);
        // A press within the debounce (30 ms) of the last press is dropped.
        clock.AdvanceMs(5);
        await rig.Ok("CONTROL INJECT OSC /blackout 0");
        clock.AdvanceMs(10);
        await rig.Ok("CONTROL INJECT OSC /blackout 1");
        Assert.False(rig.Engine.Air.Blackout);
    }

    [Fact]
    public async Task AFaderSweepIsOneJournalRowAndOneUndoStep()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(Show(), clock: clock);
        await rig.Ok("BIND ADD {\"input\":\"OSC /fader\",\"target\":\"SET sf_lobby opacity *\"}");
        foreach (var v in new[] { 0.1, 0.2, 0.3, 0.4, 0.5 })
        {
            clock.AdvanceMs(50);
            await rig.Ok("CONTROL INJECT OSC /fader " + v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity, 9);
        // The lines of the sweep are not journalled one by one; the gesture closes 400 ms after the last move.
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Line?.StartsWith("SET sf_lobby", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Kind == "control");
        clock.AdvanceMs(500);
        await rig.Engine.ControlInput.PollAsync(clock.NowNs);
        var gesture = Assert.Single(rig.Engine.Journal.Recent(200), r => r.Kind == "control");
        Assert.Equal("OSC /fader → Lobby opacity 0.1 → 0.5 over 0.2 s (5 edits)", gesture.Text);
        Assert.StartsWith("binding:bd", gesture.Who, StringComparison.Ordinal);
        // The sweep coalesced into one undo step: one UNDO puts the opacity back.
        await rig.Ok("UNDO");
        Assert.Equal(1, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity, 9);
    }

    [Fact]
    public async Task TheUndoWindowCountsOnTheNodesClockNotTheWall()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(Show(), clock: clock);
        await rig.Ok("SET sf_lobby opacity 0.2");
        clock.AdvanceMs(1000);
        await rig.Ok("SET sf_lobby opacity 0.6");
        // A second apart on the node's clock, however close on the wall: two undo steps.
        await rig.Ok("UNDO");
        Assert.Equal(0.2, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity, 9);
    }

    [Fact]
    public async Task AFaderOnARefusedTargetIsOneRefusedRowNotOnePerMove()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(Show(), clock: clock);
        await rig.Ok("BIND ADD OSC /bad TO SET sf_gone opacity *");
        foreach (var v in new[] { 0.2, 0.4, 0.6 })
        {
            clock.AdvanceMs(20);
            var fired = JsonDocument.Parse((await rig.Ok("CONTROL INJECT OSC /bad " + v.ToString(System.Globalization.CultureInfo.InvariantCulture))).Payload!).RootElement.GetProperty("fired")[0];
            Assert.Equal((false, "nothing has the id sf_gone"), (fired.GetProperty("ok").GetBoolean(), fired.GetProperty("reply").GetString()));
        }
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Kind == "refused");
        clock.AdvanceMs(500);
        await rig.Engine.ControlInput.PollAsync(clock.NowNs);
        var row = Assert.Single(rig.Engine.Journal.Recent(200), r => r.Kind == "refused");
        Assert.Equal("OSC /bad → sf_gone opacity refused 3 times: nothing has the id sf_gone", row.Text);
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Kind == "control");
    }

    [Fact]
    public async Task LearnBindsTheNextControlThatMovesAndSaysWhatItReplaced()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        await rig.Ok("BIND ADD MIDI CC 1 7 TO SET sf_stage opacity *");
        var learn = await rig.Ok("BIND LEARN FROM MIDI SET sf_lobby opacity *");
        Assert.False(learn.Applied);
        Assert.Equal("accepted", JsonDocument.Parse(learn.Payload!).RootElement.GetProperty("stage").GetString());
        Assert.Equal("Lobby opacity", rig.State().GetProperty("control").GetProperty("learning").GetProperty("words").GetString());
        // An OSC move is not from MIDI; a release never binds; a first reading only sets where the fader starts.
        await rig.Engine.ControlInput.InjectAsync(InputAddress.Osc("/x"), 0.9, CancellationToken.None);
        await rig.Engine.ControlInput.InjectAsync(InputAddress.Midi(MidiFamily.Note, 1, 1), 0, CancellationToken.None);
        await rig.Engine.ControlInput.InjectAsync(InputAddress.Midi(MidiFamily.Cc, 1, 7), 0.2, CancellationToken.None);
        Assert.NotEqual(JsonValueKind.Null, rig.State().GetProperty("control").GetProperty("learning").ValueKind);
        await rig.Engine.ControlInput.InjectAsync(InputAddress.Midi(MidiFamily.Cc, 1, 7), 0.5, CancellationToken.None);
        var control = rig.State().GetProperty("control");
        Assert.Equal(JsonValueKind.Null, control.GetProperty("learning").ValueKind);
        Assert.Equal("MIDI CC 1 7 → Lobby opacity (was Stage opacity)", control.GetProperty("learnt").GetString());
        var only = Assert.Single(rig.Engine.Store.Project.Control.Bindings);
        Assert.Equal(("MIDI CC 1 7", "SET sf_lobby opacity *", ControlBehaviour.Absolute), (only.Input, only.Target, only.Shape.Behaviour));
        Assert.Equal("not learning", await rig.Refused("BIND LEARN OFF"));
        await rig.Ok("BIND LEARN BLACKOUT TOGGLE");
        Assert.Equal("learn stopped — nothing was bound", (await rig.Ok("BIND LEARN OFF")).Effect);
        Assert.Equal("STATE cannot be bound to a control (no session, secret, approval or binding verb is)", await rig.Refused("BIND LEARN STATE"));
    }

    [Fact]
    public async Task ABindingOffOrInputOffFiresNothingAndSaysSo()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        var id = JsonDocument.Parse((await rig.Ok("BIND ADD OSC /f TO SET sf_lobby opacity *")).Payload!).RootElement.GetProperty("id").GetString()!;
        await rig.Ok($"BIND ENABLE {id} OFF");
        Assert.Equal("the binding is already off", await rig.Refused($"BIND ENABLE {id} OFF"));
        Assert.Equal("nothing is bound to OSC /f — BIND LIST shows what is", await rig.Refused("CONTROL INJECT OSC /f 0.3"));
        Assert.Equal("off", JsonDocument.Parse((await rig.Ok("BIND LIST")).Payload!).RootElement[0].GetProperty("state").GetString());
        await rig.Ok($"BIND ENABLE {id} ON");
        var off = await rig.Ok("CONTROL ENABLE OFF");
        Assert.Equal("control input off — 1 binding paused", off.Effect);
        Assert.Equal("control input is already off", await rig.Refused("CONTROL ENABLE OFF"));
        await rig.Ok("CONTROL INJECT OSC /f 0.3");
        Assert.Equal(1, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity);
        Assert.Contains(JsonDocument.Parse(rig.Engine.Context()).RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetString() == "control:off");
        await rig.Ok("CONTROL ENABLE ON");
        await rig.Ok("CONTROL INJECT OSC /f 0.3");
        Assert.Equal(0.3, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity, 9);
    }

    [Fact]
    public async Task NudgeMovesANumberWithinItsRangeAndRefusesAtTheEdge()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        Assert.Equal("Lobby opacity 1 → 0.9", (await rig.Ok("NUDGE sf_lobby opacity -0.1")).Effect);
        Assert.Equal("Lobby opacity 0.9 → 1", (await rig.Ok("NUDGE sf_lobby opacity +0.5")).Effect);
        Assert.Equal("Lobby opacity is already at its top (1)", await rig.Refused("NUDGE sf_lobby opacity +0.1"));
        Assert.Equal("Lobby has no number at name", await rig.Refused("NUDGE sf_lobby name +1"));
        Assert.Equal("nothing has the id sf_none", await rig.Refused("NUDGE sf_none opacity +1"));
    }

    [Fact]
    public async Task ABindingTargetIsCheckedWhereverItIsAdded()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        Assert.Equal("the binding's target: STATE cannot be bound to a control (no session, secret, approval or binding verb is)",
            await rig.Refused("ADD binding {\"input\":\"OSC /x\",\"target\":\"STATE\"}"));
        Assert.Equal("the binding's input: 'KEYS' is no input — MIDI, OSC, ARTNET or SACN",
            await rig.Refused("BIND ADD {\"input\":\"KEYS a\",\"target\":\"BLACKOUT TOGGLE\"}"));
        Assert.Equal("SACN [<surface>] <universe 1-63999> <channel 1-512>[+<fine>]", await rig.Refused("BIND ADD SACN 0 1 TO BLACKOUT TOGGLE"));
        // Risk: a binding to a high-risk line is itself high risk.
        Assert.True(WireGrammar.TryParse("BIND ADD MIDI NOTE 1 1 TO BLACKOUT ON", out var line, out _));
        Assert.Equal(VerbRisk.High, VerbCatalog.RiskOf(line));
    }
}
