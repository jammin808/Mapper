using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Show;

/// <summary>An engine on a manual show clock with the sessions Show Mode tells apart, and a project whose output shows one
/// playlist of three ten-second items (A, B, C).</summary>
internal sealed class ShowRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = SessionInfo.Desk("d1");
    public static readonly SessionInfo Remote = new("w1", "wire", "10.0.0.9", false, "Remote desk", true);
    public static readonly SessionInfo Companion = new("c1", "companion", "10.0.0.7", false, "Companion: FOH deck", true);
    public static readonly SessionInfo Mcp = new("m1", "mcp", "127.0.0.1", true, "MCP", true);
    public static readonly SessionInfo Assistant = new("a1", "assistant", "127.0.0.1", true, "Desk", true);
    public static readonly SessionInfo Unpaired = new("u1", "wire", "10.0.0.66", false, "Someone", false);

    public ShowRig(Project? project = null, Func<EngineServices, EngineServices>? services = null)
    {
        var s = EngineServices.ForTests(Clock);
        Engine = new EngineCore(services?.Invoke(s) ?? s, project ?? OneScreen());
    }

    public ManualClock Clock { get; } = new(5_000_000_000);

    public EngineCore Engine { get; }

    public long Now => Engine.LeaderNowNs;

    public void Advance(double ms) => Clock.AdvanceMs(ms);

    /// <summary>op_1 shows playlist pl_show: items it_a "A", it_b "B", it_c "C", ten seconds each, generators.</summary>
    public static Project OneScreen()
    {
        var p = Project.CreateDefault("Show test");
        var gen = p.Generators[0];
        PlaylistItem Item(string id, string label) => PlaylistItem.For(id, SourceRef.Generator(gen.Id), label) with { DurationNs = 10_000_000_000 };
        return p with
        {
            Playlists = [new Playlist("pl_show", "Show", [Item("it_a", "A"), Item("it_b", "B"), Item("it_c", "C")], false, null)],
            Outputs = [p.Outputs[0] with { Mode = OutputMode.Source, Source = SourceRef.Playlist("pl_show") }],
        };
    }

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, session ?? Desk, CancellationToken.None);
    }

    public async Task<string> Ok(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    public async Task<string> Refused(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.False(r.Ok, $"{line} ran: {r.Payload} {r.Effect}");
        return r.Payload ?? "";
    }

    public JsonElement State() => JsonDocument.Parse(Engine.State().Json).RootElement.Clone();

    public JsonElement Show() => State().GetProperty("show");

    public JsonElement Context() => JsonDocument.Parse(Engine.Context()).RootElement.Clone();

    /// <summary>Waits for a condition the lane's own ticks (every 10 ms) or a line's background work make true.</summary>
    public static Task<bool> Eventually(Func<bool> condition) => OpsRig.Eventually(condition);

    /// <summary>Waits until a tick of the lane has seen the manual clock where it is now: every panic step and line due by now
    /// has run, so what has not happened did not happen.</summary>
    public async Task Ticked()
    {
        var now = Now;
        Assert.True(await Eventually(() => Engine.ShowTickedNs >= now), "the lane did not tick");
    }

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
