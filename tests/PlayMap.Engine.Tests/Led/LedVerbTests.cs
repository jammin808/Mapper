using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Led;

/// <summary>The LED verbs on an engine without a renderer: the rig is added, laid out, patched and auto-patched through the gate as
/// one undo step each; overlaps and the bandwidth guard refuse with words (FORCE lets a patch overlap); the live air (test, locate,
/// level) is published in STATE and never an undo step; a no-op is refused with the reason; output needs a renderer.</summary>
public sealed class LedVerbTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    internal static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    internal static async Task<string> Ok(EngineCore engine, string line)
    {
        var r = await Run(engine, line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    internal static async Task<string> No(EngineCore engine, string line)
    {
        var r = await Run(engine, line);
        Assert.False(r.Ok, $"{line} was accepted: {r.Payload}");
        return r.Payload ?? "";
    }

    internal static JsonElement Led(EngineCore engine) => JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("led").Clone();

    /// <summary>Waits (bounded) for the engine's ticker to publish what the clock moved.</summary>
    internal static async Task Until(EngineCore engine, Func<JsonElement, bool> good)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!good(Led(engine)))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "STATE.led never showed it: " + Led(engine));
            await Task.Delay(5);
        }
    }

    private static Project Show()
    {
        var p = Project.CreateDefault("LED");
        return p with { Canvases = [new Canvas("cv00000001", "Wall", 640, 360, "#000000", [])] };
    }

    [Fact]
    public async Task ARigIsAddedLaidOutAndPatchedAndUndoTakesThePatchBack()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Show());
        var node = await Ok(engine, """ADD lednode {"name":"PixLite","protocol":"ArtNet","host":"10.0.0.21","enabled":true}""");
        Assert.StartsWith("ln", node, StringComparison.Ordinal);
        var strip = await Ok(engine, "LED NEW LINE 170 TYPE grb Strip A");
        Assert.StartsWith("lf", strip, StringComparison.Ordinal);
        var f = engine.Store.Project.Leds.Fixtures.Single();
        Assert.Equal(("Strip A", "builtin:grb", LedSpace.Canvas, "cv00000001", 170), (f.Name, f.TypeId, f.Space, f.SpaceId, f.Layout.Count));
        Assert.Null(f.Patch);
        Assert.Equal(0, Led(engine).GetProperty("universes").GetInt32());

        var range = await Ok(engine, $"LED PATCH {strip} PixLite 3.1");
        Assert.Equal("PixLite 3.1–3.510", range);
        var led = Led(engine);
        Assert.Equal((1, 170, 1), (led.GetProperty("fixtures").GetInt32(), led.GetProperty("pixels").GetInt32(), led.GetProperty("universes").GetInt32()));
        Assert.Contains("already patched so", await No(engine, $"LED PATCH {strip} {node} 3.1"), StringComparison.Ordinal);
        await Ok(engine, $"SET {strip} patch.universe 4");
        Assert.Equal(4, engine.Store.Project.Leds.Fixtures[0].Patch!.Universe);
        await Ok(engine, "UNDO");
        await Ok(engine, "UNDO");
        Assert.Null(engine.Store.Project.Leds.Fixtures[0].Patch);
        Assert.Equal(0, Led(engine).GetProperty("universes").GetInt32());
    }

    [Fact]
    public async Task AnOverlapIsRefusedNamingBothFixturesUnlessForced()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Show());
        var node = await Ok(engine, """ADD lednode {"name":"PixLite","host":"10.0.0.21","enabled":true}""");
        var a = await Ok(engine, "LED NEW LINE 16 A");
        var b = await Ok(engine, "LED NEW LINE 16 B");
        await Ok(engine, $"LED PATCH {a} {node} 3.1");
        var why = await No(engine, $"LED PATCH {b} {node} 3.25");
        Assert.Equal($"it would overlap on PixLite 3.25–3.48: {a} (A) and {b} (B) — add FORCE to patch it anyway", why);
        var revision = engine.Store.Current.Revision;
        Assert.Equal(revision, engine.Store.Current.Revision);
        await Ok(engine, $"LED PATCH {b} {node} 3.25 FORCE");
        var conflict = Led(engine).GetProperty("conflicts")[0];
        Assert.Equal((3, 25, 48), (conflict.GetProperty("universe").GetInt32(), conflict.GetProperty("from").GetInt32(), conflict.GetProperty("to").GetInt32()));
        Assert.Equal([a, b], conflict.GetProperty("fixtures").EnumerateArray().Select(x => x.GetString()!));
        // AUTOPATCH lays them end to end, and says so; a second run is refused.
        var json = JsonDocument.Parse(await Ok(engine, "LED AUTOPATCH ALL")).RootElement;
        Assert.Equal(("0–0", 32, 2, 0), (json.GetProperty("universes").GetString(), json.GetProperty("pixels").GetInt32(), json.GetProperty("fixtures").GetInt32(), json.GetProperty("conflicts").GetInt32()));
        Assert.Equal([new LedPatch(node, 0, 1), new LedPatch(node, 0, 49)], engine.Store.Project.Leds.Fixtures.Select(f => f.Patch!));
        Assert.Equal("already patched so", await No(engine, "LED AUTOPATCH ALL"));
        Assert.Equal(0, Led(engine).GetProperty("conflicts").GetArrayLength());
        // Part of the rig patches after what the rest uses; EACH starts a fixture on a fresh universe.
        var c = await Ok(engine, "LED NEW GRID 4x2 C");
        await Ok(engine, $"LED AUTOPATCH {c}");
        Assert.Equal(new LedPatch(node, 0, 97), engine.Store.Project.Leds.Fixtures[2].Patch);
        await Ok(engine, $"LED AUTOPATCH {c} FROM 7.10 EACH ON");
        Assert.Equal(new LedPatch(node, 8, 1), engine.Store.Project.Leds.Fixtures[2].Patch);
    }

    [Fact]
    public async Task TheBandwidthGuardRefusesWithItsArithmeticAndNothingIsPatched()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Show());
        await Ok(engine, """ADD lednode {"name":"PixLite","host":"10.0.0.21","enabled":true}""");
        for (var i = 0; i < 3; i++)
            await Ok(engine, "LED NEW GRID 256x256");
        var why = await No(engine, "LED AUTOPATCH ALL");
        Assert.Matches(@"^[0-9.]+ Mb/s is over this machine's 200 Mb/s guard \(1\d{3} universes at 44 Hz to PixLite\) — nothing is sent", why);
        Assert.All(engine.Store.Project.Leds.Fixtures, f => Assert.Null(f.Patch));
        // Two grids fit (154 Mb/s); a third patched by hand is refused too.
        await Ok(engine, $"LED AUTOPATCH {engine.Store.Project.Leds.Fixtures[0].Id}");
        await Ok(engine, $"LED AUTOPATCH {engine.Store.Project.Leds.Fixtures[1].Id}");
        Assert.InRange(Led(engine).GetProperty("mbps").GetDouble(), 150, 160);
        Assert.Contains("Mb/s guard", await No(engine, $"LED PATCH {engine.Store.Project.Leds.Fixtures[2].Id} PixLite 1000.1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLiveAirIsPublishedAndNeverAnUndoStep()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var engine = new EngineCore(EngineServices.ForTests(clock), Show());
        var node = await Ok(engine, """ADD lednode {"name":"PixLite","host":"10.0.0.21","enabled":true}""");
        var a = await Ok(engine, "LED NEW RING 24 Ring");
        var revision = engine.Store.Current.Revision;
        Assert.Equal("no LED test is up", await No(engine, "LED TEST OFF"));
        await Ok(engine, $"LED TEST CHASE ON {a}");
        var test = Led(engine).GetProperty("test");
        Assert.Equal(("chase", a), (test.GetProperty("pattern").GetString(), test.GetProperty("on").GetString()));
        Assert.Contains("already up", await No(engine, $"LED TEST CHASE ON {a}"), StringComparison.Ordinal);
        await Ok(engine, "LED TEST WALK");
        Assert.Equal("ALL", Led(engine).GetProperty("test").GetProperty("on").GetString());
        Assert.Equal($"nothing is patched to PixLite", await No(engine, $"LED TEST CHASE ON {node}"));
        await Ok(engine, "LED TEST OFF");
        Assert.Equal(JsonValueKind.Null, Led(engine).GetProperty("test").ValueKind);

        await Ok(engine, $"LED LOCATE Ring 2");
        Assert.Equal(a, Led(engine).GetProperty("locate").GetProperty("fixture").GetString());
        Assert.Equal("a locate lasts 1 to 60 seconds", await No(engine, $"LED LOCATE {a} 90"));
        clock.Advance(2_000_000_000);
        await Until(engine, s => s.GetProperty("locate").ValueKind == JsonValueKind.Null);

        await Ok(engine, "LED LEVEL 50 FADE 1000");
        clock.Advance(500_000_000);
        await Until(engine, s => Math.Abs(s.GetProperty("level").GetDouble() - 0.75) < 1e-3);
        clock.Advance(500_000_000);
        await Until(engine, s => Math.Abs(s.GetProperty("level").GetDouble() - 0.5) < 1e-3);
        Assert.Equal("the LED level is already 50", await No(engine, "LED LEVEL 50"));
        await Ok(engine, "LED LEVEL BY -20");
        Assert.Equal(0.3, Led(engine).GetProperty("level").GetDouble(), 3);
        Assert.Equal("the LED level is 0 to 100", await No(engine, "LED LEVEL 120"));
        Assert.Equal(revision, engine.Store.Current.Revision);
        Assert.Contains(engine.Journal.Recent(50), row => row.Line?.StartsWith("LED TEST CHASE", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task OutputNeedsARendererAndANodeOffIsAnEdit()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Show());
        var node = await Ok(engine, """ADD lednode {"name":"PixLite","host":"10.0.0.21","enabled":true}""");
        Assert.Equal("no renderer on this node — LED output needs the GPU", await No(engine, "LED OUTPUT ON"));
        Assert.Equal("LED output is already off on this machine", await No(engine, "LED OUTPUT OFF"));
        Assert.Equal("HOLD goes with OFF: it stops without the black frame", await No(engine, "LED OUTPUT ON PixLite HOLD"));
        await Ok(engine, $"LED OUTPUT OFF {node}");
        Assert.False(engine.Store.Project.Leds.Nodes[0].Enabled);
        Assert.Equal("PixLite is already off", await No(engine, "LED OUTPUT OFF PixLite"));
        var state = Led(engine);
        Assert.False(state.GetProperty("output").GetBoolean());
        Assert.Equal("disabled", state.GetProperty("nodes")[0].GetProperty("state").GetString());
        await Ok(engine, "UNDO");
        Assert.Equal("off", Led(engine).GetProperty("nodes")[0].GetProperty("state").GetString());
        var context = ContextBuilder.Build(engine);
        var item = context.Items.Single(i => i.Id == "led:" + node);
        Assert.Equal(("led", Light.Grey), (item.Kind, item.Light));
        Assert.False(LedSettings.Default.Output);
    }

    [Fact]
    public async Task LedNewRefusesWhatItCannotLayOut()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Show());
        Assert.StartsWith("LED NEW LINE|GRID|RING", await No(engine, "LED NEW STAR 10"), StringComparison.Ordinal);
        Assert.Equal("only a GRID takes columns x rows", await No(engine, "LED NEW LINE 4x4"));
        Assert.Equal("a fixture has 1 to 65536 pixels", await No(engine, "LED NEW GRID 1000x1000"));
        Assert.Equal("the show has no canvas nope", await No(engine, "LED NEW LINE 10 ON canvas:nope"));
        Assert.StartsWith("no fixture type builtin:nope", await No(engine, "LED NEW LINE 10 TYPE builtin:nope"), StringComparison.Ordinal);
        var id = await Ok(engine, "LED NEW LINE 10 ON source:color:#FF0000");
        Assert.Equal((LedSpace.Source, "color:#FF0000"), (engine.Store.Project.Leds.Fixtures[0].Space, engine.Store.Project.Leds.Fixtures[0].SpaceId));
        Assert.StartsWith("lf", id, StringComparison.Ordinal);
    }
}

/// <summary>LED IMPORT AS CSV: one undo step for the whole file, patched to the named node, refusals with the file's line.</summary>
public sealed class LedImportTests
{
    [Fact]
    public async Task ACsvIsImportedAsOneUndoStep()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-led-");
        try
        {
            var path = Path.Combine(dir.FullName, "rig.csv");
            await File.WriteAllTextAsync(path, "Fixture,Pixel,X,Y,Universe,Channel\nTop,1,0,1,4,1\nTop,2,10,1,4,4\nSide,1,0,20,5,1\n");
            var p = Project.CreateDefault("LED") with { Canvases = [new Canvas("cv00000001", "Wall", 640, 360, "#000000", [])] };
            await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), p);
            await LedVerbTests.Ok(engine, """ADD lednode {"name":"PixLite","host":"10.0.0.21","enabled":true}""");
            var revision = engine.Store.Current.Revision;
            var json = JsonDocument.Parse(await LedVerbTests.Ok(engine, $"LED IMPORT \"{path}\" AS CSV NODE PixLite SCALE 2")).RootElement;
            Assert.Equal((2, 3), (json.GetProperty("fixtures").GetInt32(), json.GetProperty("pixels").GetInt32()));
            Assert.Equal(revision + 1, engine.Store.Current.Revision);
            var fixtures = engine.Store.Project.Leds.Fixtures;
            Assert.Equal(["Top", "Side"], fixtures.Select(f => f.Name));
            Assert.Equal([0.0, 20], fixtures[0].Layout.Points.Select(x => x.X));
            Assert.Equal((4, 1), (fixtures[0].Patch!.Universe, fixtures[0].Patch!.Address));
            Assert.Equal(2, LedVerbTests.Led(engine).GetProperty("universes").GetInt32());
            await LedVerbTests.Ok(engine, "UNDO");
            Assert.Empty(engine.Store.Project.Leds.Fixtures);
            await File.WriteAllTextAsync(path, "X,Y\n1,nope\n");
            Assert.Equal("line 2, column 2 (y): 'nope' is not a number", await LedVerbTests.No(engine, $"LED IMPORT \"{path}\""));
            Assert.StartsWith("this node imports CSV pixel lists", await LedVerbTests.No(engine, $"LED IMPORT \"{path}\" AS MVR"), StringComparison.Ordinal);
            Assert.StartsWith("no file", await LedVerbTests.No(engine, $"LED IMPORT \"{Path.Combine(dir.FullName, "none.csv")}\""), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

/// <summary>STATE.led's values that move on their own (the locate's time left, the sender's words) are read fresh with nothing
/// playing, though nothing moved STATE's revision.</summary>
public sealed class LedMotionTests
{
    [Fact]
    public async Task MovingLedValuesAreFreshWhileNothingPlays()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var sender = new PlayMap.Net.Led.LedSender(new PlayMap.Net.Led.LedSenderOptions
        {
            Bind = System.Net.IPAddress.Parse(LedHostRig.Loopback), Clock = clock,
        });
        var services = EngineServices.ForTests(clock);
        services = services with { Settings = services.Settings with { Led = LedSettings.Default with { Output = true } }, LedOutput = sender };
        var p = Project.CreateDefault("LED") with { Canvases = [new Canvas("cv00000001", "Wall", 64, 32, "#000000", [])] };
        await using var engine = new EngineCore(services, p);
        await LedVerbTests.Ok(engine, $$"""ADD lednode {"name":"Bar","protocol":"Ddp","host":"{{LedHostRig.Address(95)}}","enabled":true}""");
        var strip = await LedVerbTests.Ok(engine, "LED NEW LINE 4");
        await LedVerbTests.Ok(engine, $"LED PATCH {strip} Bar 0.1");
        await LedVerbTests.Ok(engine, $"LED LOCATE {strip} 30");
        var revision = engine.State().Revision;
        var left = LedVerbTests.Led(engine).GetProperty("locate").GetProperty("leftMs").GetInt64();
        clock.Advance(1_000_000_000);
        await LedVerbTests.Until(engine, s => s.GetProperty("locate").GetProperty("leftMs").GetInt64() == left - 1000);
        Assert.Equal("idle", LedVerbTests.Led(engine).GetProperty("nodes")[0].GetProperty("state").GetString());
        // The sender sends a frame on its own thread's terms: STATE says so without any edit or verb.
        var plan = engine.Led.Current;
        var f = sender.Sink.Rent(plan.Rows.Length)!;
        (f.LeaderNs, f.LightMonoNs, f.PlanSerial) = (1, clock.NowNs, plan.Serial);
        sender.Sink.Submit(f);
        sender.Pump(clock.NowNs);
        await LedVerbTests.Until(engine, s => s.GetProperty("nodes")[0].GetProperty("state").GetString() == "sending");
        Assert.Equal(revision, engine.State().Revision);
    }
}
