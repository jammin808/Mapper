using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Effects;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Show;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>The node's quarantine through the wire: a quarantined kind is in STATE with the slots that use it and red in preflight
/// with the lines that lift it and take the slot out; FX RELEASE armed is the gate's refusal by class and the quarantine is
/// unchanged; from the blind owner it is never drafted — refused armed, acting on the node's quarantine at once when disarmed, the
/// draft staging nothing either way; a kind not quarantined, and ALL with none, are refused with words; a release empties STATE's
/// list. (A quarantined slot's pixels are the GL suite's: QuarantineBypassTests.)</summary>
public sealed class QuarantineTests
{
    private static readonly FxSource Invert = FxCatalog.Builtin.Source("fx.invert")!;

    private static QuarantineEntry Entry(string kind = "Invert", string? hash = null) =>
        new(hash ?? Invert.Hash, kind, QuarantineReason.DeviceRemoved, "the GPU stopped inside this effect's pass on op_1", 1_760_000_000_000, "0.2.0+17.abc1234");

    private static ShowRig Rig(ShaderQuarantine quarantine)
    {
        var rig = new ShowRig(services: s => ShaderParts.Compose(s, quarantine, null));
        ShaderParts.Attach(rig.Engine);
        return rig;
    }

    /// <summary>STATE's quarantine list; a node composed without a worker says none until it holds a kind.</summary>
    private static JsonElement Quarantine(ShowRig rig) =>
        rig.State().GetProperty("fx").TryGetProperty("quarantine", out var q) ? q : JsonDocument.Parse("[]").RootElement;

    private static async Task<string> AddInvert(ShowRig rig)
    {
        var reply = await rig.Run("FX ADD op_1 fx.invert");
        Assert.True(reply.Ok, reply.Payload);
        return reply.Payload!;
    }

    [Fact]
    public async Task AQuarantinedKindIsInStateAndRedInPreflightWithTheLinesThatLiftItAndTakeItOut()
    {
        var quarantine = new ShaderQuarantine();
        await using var rig = Rig(quarantine);
        var slot = await AddInvert(rig);
        Assert.Empty(Quarantine(rig).EnumerateArray());
        Assert.True(quarantine.Add(Entry()));
        Assert.False(quarantine.Add(Entry()));

        var held = Assert.Single(Quarantine(rig).EnumerateArray());
        Assert.Equal(Invert.Hash, held.GetProperty("hash").GetString());
        Assert.Equal("Invert", held.GetProperty("kind").GetString());
        Assert.Equal("deviceRemoved", held.GetProperty("reason").GetString());
        Assert.Equal(1_760_000_000_000, held.GetProperty("at").GetInt64());
        Assert.Equal("0.2.0+17.abc1234", held.GetProperty("build").GetString());
        Assert.Equal([slot], held.GetProperty("slots").EnumerateArray().Select(s => s.GetString()));

        var check = Assert.Single(rig.Engine.PreflightChecks, c => c is QuarantinePreflight);
        Assert.Equal("fx", check.Section);
        var rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(Light.Red, r.Light));
        Assert.Equal(["FX RELEASE Invert", $"FX REMOVE {slot}"], rows.Select(r => r.Fix));
        foreach (var fix in rows.Select(r => r.Fix!))
            Assert.True(WireGrammar.TryParse(fix, out var parsed, out _) && VerbCatalog.Match(parsed) is not null, fix);
        Assert.Contains("1 slot uses it", rows[0].Note, StringComparison.Ordinal);

        // Taken out: the kind's row stays until it is released, and says no slot uses it.
        await rig.Ok(rows[1].Fix!);
        rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.Contains("no slot of this show uses it", Assert.Single(rows).Note, StringComparison.Ordinal);
        await rig.Ok(rows[0].Fix!);
        rows = await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.Equal(Light.Green, Assert.Single(rows).Light);
    }

    [Fact]
    public async Task ReleaseWhileArmedIsTheGatesRefusalByClassAndTheQuarantineIsUnchanged()
    {
        var quarantine = new ShaderQuarantine();
        await using var rig = Rig(quarantine);
        quarantine.Add(Entry());
        await rig.Ok("SHOW ARM");
        Assert.Equal("Not while armed: FX RELEASE puts set-up pictures or settings on the air. SHOW DISARM on the desk first.",
            await rig.Refused("FX RELEASE Invert"));
        Assert.Equal(ShowClass.Setup, VerbCatalog.Find("FX RELEASE")!.Show);
        Assert.Single(quarantine.All);
        Assert.Single(Quarantine(rig).EnumerateArray());
    }

    [Fact]
    public async Task FromTheBlindOwnerItIsNeverDraftedRefusedArmedAndActingOnTheNodeAtOnceDisarmed()
    {
        var quarantine = new ShaderQuarantine();
        await using var rig = Rig(quarantine);
        quarantine.Add(Entry());
        await rig.Ok("BLIND ON");
        await rig.Ok("SHOW ARM");
        Assert.StartsWith("Not while armed: FX RELEASE", await rig.Refused("FX RELEASE Invert"), StringComparison.Ordinal);
        Assert.Single(quarantine.All);
        await rig.Ok("SHOW DISARM");

        var revision = rig.Engine.Store.Current.Revision;
        var released = JsonDocument.Parse(await rig.Ok("FX RELEASE Invert")).RootElement;
        Assert.Equal(["Invert"], released.GetProperty("released").EnumerateArray().Select(e => e.GetString()));
        // The node's quarantine moved at once; neither the live show nor the draft took an edit.
        Assert.Empty(quarantine.All);
        Assert.Empty(Quarantine(rig).EnumerateArray());
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(0, rig.Show().GetProperty("blind").GetProperty("changes").GetInt32());
        var diff = JsonDocument.Parse(await rig.Ok("BLIND DIFF")).RootElement;
        Assert.Empty(diff.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task AKindNotQuarantinedAndAllWithNoneAreRefusedWithWordsAndAReleaseNamesWhatItLifted()
    {
        var quarantine = new ShaderQuarantine();
        await using var rig = Rig(quarantine);
        Assert.Equal("no kind is quarantined on this node", await rig.Refused("FX RELEASE ALL"));
        Assert.Equal("fx.blur is not quarantined: no kind is quarantined on this node", await rig.Refused("FX RELEASE fx.blur"));
        Assert.Equal("FX RELEASE <kind>|ALL", await rig.Refused("FX RELEASE"));

        quarantine.Add(Entry());
        quarantine.Add(Entry("Wobble Pro", new string('a', 64)));
        Assert.Equal("fx.blur is not quarantined on this node (quarantined: Invert, Wobble Pro)", await rig.Refused("FX RELEASE fx.blur"));
        // The show's kind by its id finds the quarantine of its sources.
        var one = JsonDocument.Parse(await rig.Ok("FX RELEASE fx.invert")).RootElement;
        Assert.Equal(["Invert"], one.GetProperty("released").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Invert is not quarantined on this node (quarantined: Wobble Pro)", await rig.Refused("FX RELEASE Invert"));
        var all = JsonDocument.Parse(await rig.Ok("FX RELEASE ALL")).RootElement;
        Assert.Equal(["Wobble Pro"], all.GetProperty("released").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(quarantine.All);
        Assert.Equal("no kind is quarantined on this node", await rig.Refused("FX RELEASE ALL"));
        Assert.Contains(rig.Engine.Journal.Recent(10), r => r.Kind == "fx.release" && r.Text.Contains("Wobble Pro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANodeWithoutTheShaderPartsSaysNoQuarantineUntilItHoldsOne()
    {
        await using var rig = new ShowRig();
        Assert.False(rig.State().GetProperty("fx").TryGetProperty("quarantine", out _));
        Assert.False(rig.State().GetProperty("fx").GetProperty("compile").TryGetProperty("worker", out _));
        rig.Engine.Services.Quarantine.Add(Entry());
        rig.Engine.FactsChanged();
        Assert.Single(Quarantine(rig).EnumerateArray());
    }
}
