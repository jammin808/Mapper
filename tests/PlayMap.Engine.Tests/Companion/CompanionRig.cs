using System.Collections.Concurrent;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>An engine on a manual clock with the controllers' side to hand: lines as a paired client, STATE, and topics opened onto
/// a recording sink.</summary>
internal sealed class CompanionRig : IAsyncDisposable
{
    public static readonly SessionInfo Paired = new("w1", "companion", "10.0.0.7", false, "Companion:FOH deck", true);

    public readonly ManualClock Clock = new(5_000_000_000);
    public readonly EngineCore Engine;

    public CompanionRig(Func<EngineServices, EngineServices>? services = null, Project? project = null)
    {
        var s = EngineServices.ForTests(Clock);
        Engine = new EngineCore(services?.Invoke(s) ?? s, project);
    }

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, session ?? Paired, CancellationToken.None);
    }

    public async Task<string> Ok(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    public JsonElement State()
    {
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.Clone();
    }

    public static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Opens a topic as a session would (SUBSCRIBE &lt;topic&gt; …), asserting it opened.</summary>
    public IDisposable Open(string topic, RecordingSink sink, SessionInfo? session = null, params string[] args)
    {
        var topics = (IPushTopics)Engine;
        Assert.True(topics.TryOpen(topic, args, session ?? Paired, sink, out var sub, out var words), words);
        return sub!;
    }

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}

/// <summary>A session's sink as the tests hold it: every line in the order it was said, the newest per key, and a wait for the next
/// line that matches (event driven, never a wall-clock window). In-order lines past <see cref="Bound"/> are refused.</summary>
internal sealed class RecordingSink : IPushSink
{
    private readonly Lock _gate = new();
    private readonly List<(Func<string, bool> Means, TaskCompletionSource Done)> _waits = [];

    public int Bound { get; init; } = int.MaxValue;

    public ConcurrentQueue<string> Lines { get; } = new();

    public ConcurrentDictionary<string, string> Latests { get; } = new(StringComparer.Ordinal);

    public int InOrderCount;

    public void Latest(string key, string line)
    {
        Latests[key] = line;
        Said(line);
    }

    public bool InOrder(string line)
    {
        if (Interlocked.Increment(ref InOrderCount) > Bound)
            return false;
        Said(line);
        return true;
    }

    private void Said(string line)
    {
        Lines.Enqueue(line);
        lock (_gate)
        {
            foreach (var (_, done) in _waits.Where(w => w.Means(line)).ToList())
                done.TrySetResult();
            _waits.RemoveAll(w => w.Done.Task.IsCompleted);
        }
    }

    /// <summary>Completes when a line containing <paramref name="text"/> is said (at once when it was already).</summary>
    public Task Heard(string text, int timeoutMs = 10000) => Heard(l => l.Contains(text, StringComparison.Ordinal), timeoutMs);

    /// <summary>Completes when a line that <paramref name="means"/> holds for is said (at once when one was already): a wait for what
    /// a line says, where its words alone may be in an earlier line too.</summary>
    public Task Heard(Func<string, bool> means, int timeoutMs = 10000)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (Lines.Any(means))
                return Task.CompletedTask;
            _waits.Add((means, done));
        }
        return done.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }
}

/// <summary>Show Mode armed, as wp2-show's gate answers it: a remote may run only the listed verbs.</summary>
internal sealed class ArmedGate(params string[] may) : IShowGate
{
    public bool Armed => true;

    public string ArmedBy => "Desk";

    public string? BlindOwner => null;

    public int MinGoMs => 300;

    public ShowRole RoleOf(SessionInfo session) => session.Transport == "desk" ? ShowRole.Operator : ShowRole.Remote;

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) =>
        session.Transport == "desk" || may.Contains(spec.Name) || !spec.Mutates
            ? new ShowDecision(ShowVerdict.Allow, null)
            : new ShowDecision(ShowVerdict.Refuse, $"the show is armed — {spec.Name} is refused from a remote");

    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => session.Transport == "desk" ? null : may;
}
