using System.Text.Json;
using PlayMap.Core.Content;

namespace PlayMap.Engine.Tests.Content;

/// <summary>STING on the wire (the render host's impulse channel was there and nothing fired it): a preset with a length and an AT
/// lands in the channel every generator reads, with its start on the leader clock; STATE.content.sting observes it and lets it go
/// when it is over; unknown presets, lengths out of range and a CLEAR with nothing running are refused with words; STING LIST
/// lists every preset with its channels. A sting changes no show.</summary>
public sealed class StingTests
{
    [Fact]
    public async Task AStingLandsInTheChannelOnTheLeaderClock()
    {
        await using var rig = new ContentRig();
        var revision = rig.Engine.Store.Current.Revision;
        var r = await rig.Ok("STING Explosion 900 AT +100");
        var current = rig.Engine.Content.Stings.Current;
        Assert.Equal(PulsePreset.Explosion, current.Preset);
        Assert.Equal((rig.Engine.LeaderNowNs + 100_000_000) / 1e9, current.StartSeconds, 9);
        Assert.Equal(0.9, current.LengthSeconds, 9);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        var json = ContentRig.Json(r);
        Assert.Equal("Explosion", json.GetProperty("preset").GetString());
        Assert.Equal(900, json.GetProperty("lengthMs").GetInt32());
        Assert.Equal(rig.Engine.Services.Wall.UnixMs + 100, json.GetProperty("atUnixMs").GetInt64());

        // Observed in STATE while it runs, half way through at its middle; gone once over.
        var sting = rig.State().GetProperty("content").GetProperty("sting");
        Assert.Equal("Explosion", sting.GetProperty("preset").GetString());
        Assert.Equal(0, sting.GetProperty("progress").GetDouble());
        rig.Clock.AdvanceMs(550);
        rig.Engine.FactsChanged();
        Assert.Equal(0.5, rig.State().GetProperty("content").GetProperty("sting").GetProperty("progress").GetDouble(), 3);
        rig.Clock.AdvanceMs(500);
        rig.Engine.FactsChanged();
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("content").GetProperty("sting").ValueKind);
        Assert.Contains("no sting is running", await rig.Refused("STING CLEAR"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APresetsOwnLengthIsTheDefaultAndClearStopsIt()
    {
        await using var rig = new ContentRig();
        await rig.Ok("STING strobe");
        var current = rig.Engine.Content.Stings.Current;
        Assert.Equal(PulsePreset.Strobe, current.Preset);
        Assert.Equal(2.0, current.LengthSeconds, 9);
        Assert.Equal(rig.Engine.LeaderNowNs / 1e9, current.StartSeconds, 9);
        var r = await rig.Ok("STING CLEAR");
        Assert.Equal("Strobe stopped", r.Effect);
        Assert.True(rig.Engine.Content.Stings.Current.IsNone);
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("content").GetProperty("sting").ValueKind);
    }

    [Fact]
    public async Task UnknownPresetsAndLengthsOutOfRangeAreRefusedWithWords()
    {
        await using var rig = new ContentRig();
        var unknown = await rig.Refused("STING Fireworks");
        Assert.Contains("no sting 'Fireworks'", unknown, StringComparison.Ordinal);
        Assert.Contains("Explosion", unknown, StringComparison.Ordinal);
        Assert.Contains("100–5000 ms", await rig.Refused("STING Flash 50"), StringComparison.Ordinal);
        Assert.Contains("100–5000 ms", await rig.Refused("STING Flash 6000"), StringComparison.Ordinal);
        Assert.Contains("AT takes", await rig.Refused("STING Flash AT soon"), StringComparison.Ordinal);
        Assert.Contains("no sting is running", await rig.Refused("STING CLEAR"), StringComparison.Ordinal);
        Assert.True(rig.Engine.Content.Stings.Current.IsNone);
    }

    [Fact]
    public async Task ListNamesEveryPresetWithItsChannels()
    {
        await using var rig = new ContentRig();
        using var list = JsonDocument.Parse((await rig.Ok("STING LIST")).Payload!);
        var rows = list.RootElement.EnumerateArray().ToList();
        Assert.Equal(Enum.GetValues<PulsePreset>().Length, rows.Count);
        var explosion = rows.Single(r => r.GetProperty("name").GetString() == "Explosion");
        Assert.Equal("pulse", explosion.GetProperty("kind").GetString());
        Assert.Equal(900, explosion.GetProperty("defaultMs").GetInt32());
        Assert.Contains("burst", explosion.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        var vortex = rows.Single(r => r.GetProperty("name").GetString() == "Vortex");
        Assert.Equal("score", vortex.GetProperty("kind").GetString());
        Assert.Contains("swirl", vortex.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
    }
}
