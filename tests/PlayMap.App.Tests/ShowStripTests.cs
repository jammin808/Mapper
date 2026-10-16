using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
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
        var source = sf.Actions.Single(a => a.Id == "content").Children.First(c => c.Verb == $"SURFACE SOURCE {sf.Id} color:#FFFFFF");
        Assert.False(source.IsEnabled);
        // The desk shows LockGuard's own words for STATE's lock, and they are what the engine's guard refuses that edit with.
        var contentLock = new ShowLock("content", LockKind.Area, "Desk", 1760000000000, "show night");
        Assert.Equal(LockGuard.AreaWords(contentLock), source.Refusal);
        var project = desk.Link.CurrentProject;
        var locked = project with { Show = project.Show with { Locks = [contentLock] } };
        var edited = locked.WithStage(st => st with { Surfaces = st.Surfaces.Update(sf.Id, x => x with { Content = x.Content with { Source = SourceRef.Solid("#FFFFFF") } }) });
        Assert.Equal(LockGuard.Check(locked, edited, armed: false), source.Refusal);

        // The show stays live: transport, Blackout, PANIC, identify (setup is refused armed; transport is not).
        Assert.True(desk.Main.Blackout.IsEnabled);
        Assert.True(desk.Main.ShowModule.Strip.Panic.IsEnabled);
        Assert.All(desk.Main.Master.Actions.Where(a => a.Verb.Length > 0), a => Assert.Null(a.Refusal));
        Assert.True(desk.Main.ShowModule.Strip.Arm.IsEnabled);

        // A real engine, armed, refuses the desk the same line with the same words.
        await using (var engine = new PlayMap.Engine.EngineCore(PlayMap.Engine.EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000))))
        {
            var desk1 = new SessionInfo("d1", "desk", "127.0.0.1", true, "PlayMapDesk", true);
            Assert.True(WireGrammar.TryParse("SHOW ARM", out var arm, out _));
            Assert.True((await engine.HandleAsync(arm, desk1, Ct)).Ok);
            var engineReply = await engine.HandleAsync(line, desk1, Ct);
            Assert.False(engineReply.Ok);
            Assert.Equal(expected, engineReply.Payload);
        }

        // Disarmed, the mapping is live again at once.
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        DeskHarness.Pump();
        Assert.True(remove.IsEnabled);
        Assert.Null(remove.Refusal);
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(MainWindowTests.Find<ContentControl>(window, "InspectorFields").IsEffectivelyEnabled || desk.Main.Map.Inspector.Current!.EditRefusal is not null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARemoteDeskIsGatedAsTheNodeTakesItAndLocksNeverHoldDeviceCommandsOrTheAir()
    {
        await using var desk = new DeskHarness(link => link.Role = "remote");
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        Assert.Equal("remote", desk.Session.Role);
        var gate = desk.Session.GateWords;
        // Armed, a remote desk may not edit content (an operator may): the node's words for its role.
        var sf = desk.Link.CurrentProject.Stage.Surfaces[0].Id;
        Assert.True(WireGrammar.TryParse($"SURFACE SOURCE {sf} color:#FFFFFF", out var l, out _));
        var words = ShowPolicy.Decide(VerbCatalog.Match(l)!.Value.Spec, l, ShowRole.Remote, new ShowFacts(true, false, false, false, "paired")).Words;
        Assert.NotNull(words);
        Assert.Equal(words, gate(l.Raw));
        Assert.Equal(words, (await desk.Session.SendQuietAsync(l.Raw, Ct)).Text);
        // PANIC and the transport stay live for it too.
        Assert.Null(gate("PANIC"));
        Assert.Null(gate("BLACKOUT ON"));

        // Disarmed, with every area locked: device commands, the air and playback are never held; edits are.
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        foreach (var area in PlayMap.Core.Model.LockAreas.All.Where(a => a != "content"))
            await desk.Session.SendAsync("LOCK AREA " + area, Ct);
        DeskHarness.Pump();
        Assert.Null(gate("DEVICE SHUTTER dv_1 CLOSE"));
        Assert.Null(gate("INPUT SWITCH in_1 PRIMARY"));
        Assert.Null(gate("PANIC"));
        Assert.Null(gate("MASTER 50"));
        Assert.Null(gate("PLAYLIST GO " + desk.Link.CurrentProject.Playlists[0].Id));
        Assert.StartsWith("mapping is locked by Desk (", gate($"WARP RESET op_1"), StringComparison.Ordinal);
        // A locked entity named second (the source, not the target) holds nothing.
        var gn = desk.Link.CurrentProject.Generators[0].Id;
        await desk.Session.SendAsync("UNLOCK AREA content", Ct);
        await desk.Session.SendAsync("LOCK " + gn, Ct);
        DeskHarness.Pump();
        Assert.Null(gate($"SURFACE SOURCE {sf} generator:{gn}"));
        Assert.Contains("is locked by Desk", gate($"GENERATOR SET {gn} speed 2") ?? "", StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task EscWhileAFieldTypesNeverPanicsAndThePanicBannerStaysPutThroughPushes()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        desk.Main.Prefs.EscIsPanic = true;
        var window = MainWindowTests.Open(desk);
        var banner = Assert.Single(desk.Main.ShownBanners);
        // Pushes during the panic leave the banner (and its buttons) where they are.
        desk.Link.SetSection("probe", "{\"n\":1}");
        desk.Link.SetSection("probe", "{\"n\":2}");
        DeskHarness.Pump();
        Assert.Same(banner, Assert.Single(desk.Main.ShownBanners));

        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        var box = new TextBox();
        ((Panel)MainWindowTests.Find<Views.PlayView>(window, "PlayView").Parent!).Children.Add(box);
        DeskHarness.Pump();
        box.Focus();
        DeskHarness.Pump();
        Key(window, Avalonia.Input.Key.Escape);
        Assert.DoesNotContain(desk.Verbs(), v => v.StartsWith("PANIC", StringComparison.Ordinal));
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
        Assert.StartsWith($"{desk.Link.CurrentProject.Stage.Surfaces.Find(sf.Id)!.Name} (surface) is locked by Desk (", remove.Refusal, StringComparison.Ordinal);
        Assert.EndsWith($") — UNLOCK {sf.Id} first", remove.Refusal, StringComparison.Ordinal);
        // Those are LockGuard's own words for STATE's lock on that entity, and the padlock's tooltip says the same.
        var held = desk.Session.State.Section("show", ViewModels.Show.ShowSection.Read).LockOn(sf.Id)!.AsCore();
        var words = LockGuard.EntityWords(held, ShowEntities.Find(desk.Link.CurrentProject, sf.Id)!);
        Assert.Equal(words, remove.Refusal);
        Assert.Equal(words, Assert.Single(badge.Shown).Tip);
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
        Assert.Contains(first.Actions, a => a.Refusal is { } why && why.EndsWith("— UNLOCK " + first.Id + " first", StringComparison.Ordinal));
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

    /// <summary>A stack that has lost a member holds or dims the building on the air: while STATE names the member, the attention bar
    /// has a red banner in the node's words (the light the building is at) and the stack's card is red with its failover buttons; the
    /// banner goes when the member is back.</summary>
    [AvaloniaFact]
    public async Task AStackThatHasLostAMemberIsARedBannerAndARedCardUntilTheMemberIsBack()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "facade");
        var window = MainWindowTests.Open(desk);
        DeskHarness.Pump();
        var stage = JsonNode.Parse(StateFixtures.Read("facade"))!["stage"]!.AsObject();
        var sk = stage["stacks"]![0]!.AsObject();
        var id = sk["id"]!.GetValue<string>();
        Assert.DoesNotContain(desk.Main.Banners, b => b.Id == "stack:" + id);
        Assert.Equal("amber", desk.Main.Cards.Shown.Single(c => c.Instance.EntityId == id).Light);

        var words = sk["name"]!.GetValue<string>() + " lost Far twin — matched: the building at 67 %";
        sk["degraded"] = new JsonArray(sk["members"]![1]!.GetValue<string>());
        sk["gain"] = 0.667;
        sk["failover"] = "match";
        sk["light"] = "red";
        sk["words"] = words;
        desk.Link.SetSection("stage", stage.ToJsonString());
        DeskHarness.Pump();
        var banner = Assert.Single(desk.Main.Banners, b => b.Id == "stack:" + id);
        Assert.Equal(("red", words), (banner.Light, banner.Words));
        var card = desk.Main.Cards.Shown.Single(c => c.Instance.EntityId == id);
        Assert.Equal("red", card.Light);
        var hold = card.Actions.Single(a => a.Verb == $"STACK FAILOVER {id} HOLD");
        desk.Link.ClearSent();
        await hold.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal($"STACK FAILOVER {id} HOLD", desk.Verbs()[^1]);

        // The member is back: the node's STATE names no member lost, and the banner goes.
        StateFixtures.Load(desk.Link, "facade");
        DeskHarness.Pump();
        Assert.DoesNotContain(desk.Main.Banners, b => b.Id == "stack:" + id);
        Assert.Equal("amber", desk.Main.Cards.Shown.Single(c => c.Instance.EntityId == id).Light);
        window.Close();
    }
}
