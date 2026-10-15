using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>The show strip and Show Mode desk-wide: ARM sends SHOW ARM (no password); armed, the mapping actions grey with
/// the gate's own words and the transport, Blackout and PANIC stay live; a locked area or entity greys its edits with the
/// lock's words; F12 sends PANIC; the panic fixture raises the red banner whose Clear sends PANIC CLEAR FADE 1000; Esc
/// within 600 ms of closing a drawer never panics; Lock/Unlock sit in every lockable object's menu, in step with its
/// inspector toolbar.</summary>
public class ShowStripTests
{
    private static void Key(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, "");
        DeskHarness.Pump();
    }

    [AvaloniaFact]
    public async Task ArmSendsShowArmAndTheDeskShowsWhoArmedIt()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        DeskHarness.Pump();
        var window = MainWindowTests.Open(desk);
        var strip = desk.Main.ShowModule.Strip;
        Assert.True(strip.IsPresent);
        Assert.False(strip.IsArmed);
        Assert.Equal("", desk.Main.Frame);
        desk.Link.ClearSent();

        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "ArmButton"));
        await DeskHarness.Drain([strip.Arm]);
        Assert.Equal("SHOW ARM", Assert.Single(desk.Verbs()));
        Assert.True(strip.IsArmed);
        Assert.StartsWith("ARMED · Desk · ", strip.Arm.Label, StringComparison.Ordinal);
        Assert.Equal("red", desk.Main.Frame);
        Assert.Contains("armed", MainWindowTests.Find<Button>(window, "ArmButton").Classes);

        // Ctrl+Shift+A disarms (the key and the button are one action).
        Key(window, Avalonia.Input.Key.A, RawInputModifiers.Control | RawInputModifiers.Shift);
        await DeskHarness.Drain([strip.Arm]);
        Assert.Equal("SHOW DISARM", desk.Verbs()[^1]);
        Assert.False(strip.IsArmed);
        Assert.Equal("ARM", strip.Arm.Label);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArmedTheMappingActionsGreyWithTheGatesWordsAndTheShowStaysLive()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        Assert.True(desk.Main.ShowModule.Strip.IsArmed);
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var sf = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == "surface");
        desk.Main.Map.Select(sf.Kind, sf.Id);
        DeskHarness.Pump();

        // The words are the engine's own policy's for the desk.
        var remove = sf.Actions.Single(a => a.Id == "remove");
        Assert.True(WireGrammar.TryParse(remove.Verb, out var line, out _));
        var expected = ShowPolicy.Decide(VerbCatalog.Match(line)!.Value.Spec, line, ShowRole.Operator, new ShowFacts(true, false, false, false, "paired")).Words;
        Assert.StartsWith("Not while armed: REMOVE moves the mapping on the air.", expected, StringComparison.Ordinal);
        Assert.False(remove.IsEnabled);
        Assert.Equal(expected, remove.Refusal);
        Assert.Contains(expected!, remove.Tip, StringComparison.Ordinal);
        Assert.EndsWith(" · armed", remove.MenuLabel, StringComparison.Ordinal);
        var op = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Id == "op_1");
        Assert.False(op.Actions.Single(a => a.Id == "warpReset").IsEnabled);
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, sf.Actions);
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Tag == remove);
        Assert.False(item.IsEnabled);
        Assert.EndsWith("· armed", (string)item.Header!, StringComparison.Ordinal);
        // The inspector's buttons grey too.
        var inspectorRemove = desk.Main.Map.Inspector.Current!.Actions.Single(a => a.Id == "remove");
        Assert.Equal(expected, inspectorRemove.Refusal);
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == inspectorRemove);
        Assert.False(button.IsEffectivelyEnabled);

        // A locked area greys its edits with the lock's words (show.json locks content: "show night").
        var source = sf.Actions.Single(a => a.Id == "content").Children.First(c => c.Verb.StartsWith("SURFACE SOURCE", StringComparison.Ordinal));
        Assert.False(source.IsEnabled);
        Assert.Equal("Locked: content is locked by Desk ('show night'). UNLOCK AREA content first.", source.Refusal);

        // The show stays live: transport, Blackout, PANIC, identify (setup is refused armed; transport is not).
        Assert.True(desk.Main.Blackout.IsEnabled);
        Assert.True(desk.Main.ShowModule.Strip.Panic.IsEnabled);
        Assert.All(desk.Main.Master.Actions.Where(a => a.Verb.Length > 0), a => Assert.Null(a.Refusal));
        Assert.True(desk.Main.ShowModule.Strip.Arm.IsEnabled);

        // The engine (here the fake, with the same policy) refuses the line with the same words if it is sent anyway.
        var reply = await desk.Session.SendQuietAsync(remove.Verb, Ct);
        Assert.False(reply.Ok);
        Assert.Equal(expected, reply.Text);

        // Disarmed, the mapping is live again at once.
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        DeskHarness.Pump();
        Assert.True(remove.IsEnabled);
        Assert.Null(remove.Refusal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task F12SendsPanicAndThePanicBannersClearSendsPanicClearFade1000()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        var window = MainWindowTests.Open(desk);

        // The fixture's panic: the red banner, worst first, with who and when.
        var banner = Assert.Single(desk.Main.Banners);
        Assert.Equal("red", banner.Light);
        Assert.StartsWith("PANIC black since ", banner.Words, StringComparison.Ordinal);
        Assert.Contains(" by Desk — op_1: black", banner.Words, StringComparison.Ordinal);
        Assert.True(desk.Main.ShowModule.Strip.IsPanic);
        desk.Link.ClearSent();
        var clear = window.GetVisualDescendants().OfType<ActionMenuButton>().Single(b => b.Action is { Id: "panic.clear" });
        DeskHarness.Click(window, clear);
        await DeskHarness.Drain(banner.Actions);
        Assert.Equal("PANIC CLEAR FADE 1000", Assert.Single(desk.Verbs()));
        Assert.Empty(desk.Main.Banners);
        Assert.False(desk.Main.ShowModule.Strip.IsPanic);

        // F12 is PANIC from anywhere, even while a field types.
        desk.Main.Workspace = Workspace.Map;
        Key(window, Avalonia.Input.Key.F12);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Equal("PANIC", desk.Verbs()[^1]);
        Assert.Equal("red", Assert.Single(desk.Main.Banners).Light);
        // A second press within the fade is HARD (the engine's rule; the desk sends the same word).
        Key(window, Avalonia.Input.Key.F12);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.StartsWith("PANIC hard black", Assert.Single(desk.Main.Banners).Words, StringComparison.Ordinal);

        // The banner's ▾ gives the other fades.
        var fades = desk.Main.Banners[0].Actions.Single(a => a.HasChildren).Children.Select(c => c.Verb).ToArray();
        Assert.Equal(["PANIC CLEAR FADE 0", "PANIC CLEAR FADE 1000", "PANIC CLEAR FADE 2000", "PANIC CLEAR FADE 5000"], fades);
        Assert.Equal(["PANIC", "PANIC BLACK", "PANIC RUN", "PANIC REWIND", "PANIC NEXT", "PANIC FREEZE", "PANIC SAFE", "PANIC SHUTTER", "PANIC FX-OFF", "PANIC HARD"],
            desk.Main.ShowModule.Strip.PanicMenu.Select(a => a.Verb).ToArray());
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscWithin600MsOfClosingADrawerDoesNotSendPanic()
    {
        var now = 10_000L;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        desk.Main.Prefs.EscIsPanic = true;
        var window = MainWindowTests.Open(desk);
        desk.Link.ClearSent();

        desk.Main.IsOutputsOpen = true;
        DeskHarness.Pump();
        Key(window, Avalonia.Input.Key.Escape);
        Assert.False(desk.Main.Drawers.IsOpen);
        now += 100;
        Key(window, Avalonia.Input.Key.Escape);
        now += 499;
        Key(window, Avalonia.Input.Key.Escape);
        Assert.DoesNotContain("PANIC", desk.Verbs());

        now += 1;
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Equal("PANIC", Assert.Single(desk.Verbs(), l => l.StartsWith("PANIC", StringComparison.Ordinal)));

        // Esc is panic only in PLAY, and only with the preference on.
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        desk.Main.Workspace = Workspace.Map;
        now += 5_000;
        Key(window, Avalonia.Input.Key.Escape);
        desk.Main.Workspace = Workspace.Play;
        desk.Main.Prefs.EscIsPanic = false;
        Key(window, Avalonia.Input.Key.Escape);
        Assert.DoesNotContain(desk.Verbs(), l => l.StartsWith("PANIC", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task LockAndUnlockSitInEveryLockableMenuInStepWithTheInspector()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var sf = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == "surface");
        desk.Main.Map.Select(sf.Kind, sf.Id);
        DeskHarness.Pump();

        var lockAction = Assert.Single(desk.Main.ExtrasFor(sf), a => a.Id == "show.lock");
        Assert.Contains(lockAction, desk.Main.Map.Inspector.Extras);
        Assert.Equal("LOCK " + sf.Id, lockAction.Verb);
        Assert.Equal("Lock for the show", lockAction.Label);
        await lockAction.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("LOCK " + sf.Id, desk.Verbs()[^1]);
        Assert.Equal("UNLOCK " + sf.Id, lockAction.Verb);
        Assert.Equal("Unlock", lockAction.Label);
        Assert.True(lockAction.IsChecked);
        var badge = window.GetVisualDescendants().OfType<BadgeStrip>().First(b => b.DataContext == sf);
        Assert.Equal("lock", Assert.Single(badge.Shown).Id);

        // The locked surface's own edits grey with the lock's words; its unlock stays live.
        var remove = sf.Actions.Single(a => a.Id == "remove");
        Assert.Equal($"Locked: {sf.Id} is locked by Desk. UNLOCK {sf.Id} first.", remove.Refusal);
        Assert.True(lockAction.IsEnabled);
        await lockAction.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("UNLOCK " + sf.Id, desk.Verbs()[^1]);
        Assert.Empty(badge.Shown);
        Assert.Null(remove.Refusal);

        // A locked playlist still plays: locks hold edits, never the transport.
        var first = desk.Main.Play.Playlists[0];
        await desk.Session.SendAsync("LOCK " + first.Id, Ct);
        DeskHarness.Pump();
        Assert.All(first.Transport.Actions.Where(a => a.Verb.Length > 0), a => Assert.Null(a.Refusal));
        Assert.Contains(first.Actions, a => a.Refusal is { } why && why.StartsWith("Locked: " + first.Id, StringComparison.Ordinal));
        await desk.Session.SendAsync("UNLOCK " + first.Id, Ct);
        DeskHarness.Pump();

        // Every lockable kind has the entry; a mesh has none.
        foreach (var n in desk.Main.Map.Outliner.SelectMany(g => g.Nodes))
            Assert.Equal(n.Kind is "surface" or "projector" or "output", desk.Main.ExtrasFor(n).Any(a => a.Id == "show.lock"));
        foreach (var pl in desk.Main.Play.Playlists)
        {
            Assert.Contains(desk.Main.ExtrasFor(pl), a => a.Verb == "LOCK " + pl.Id);
            Assert.All(pl.Items, it => Assert.Contains(desk.Main.ExtrasFor(it), a => a.Verb == "LOCK " + it.Id));
        }
        window.Close();
    }
}
