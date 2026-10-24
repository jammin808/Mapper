using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Assistant;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Manager;
using PlayMap.Engine.Assistant;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests;

/// <summary>An engine with the operations services attached as a host attaches them — the scan runner (no outputs:
/// the simulator scans), projector control (no PJLink UDP, quick timings), the assistant on a scripted model — in a
/// project folder of its own, with a journal file, on the real monotonic clock (projectors and models answer in real
/// time).</summary>
internal sealed class OpsRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = new("d1", "desk", "127.0.0.1", true, "Desk", true);
    public static readonly SessionInfo Remote = new("w1", "wire", "10.0.0.9", false, "Remote desk", true);
    public static readonly SessionInfo Mcp = new("m1", "mcp", "127.0.0.1", true, "MCP", true);

    private readonly IAsyncDisposable _ops;
    private int _disposed;

    /// <param name="display">The outputs a real scan shows its patterns on (null: none — the simulator scans).</param>
    /// <param name="services">A test's own parts on top of the rig's (a camera re-solve's display and camera).</param>
    /// <param name="clock">The node's and the devices' clock (null: the real monotonic clock, which a scan display's stamps share).</param>
    /// <param name="assistantLog">The engine's log as the assistant has it (null: none).</param>
    public OpsRig(AssistantSettings? assistant = null, ScriptedModel? model = null, Project? project = null, INetworkControl? network = null,
        ScanRunnerOptions? scan = null, IScanDisplay? display = null, Func<EngineServices, EngineServices>? services = null,
        IMonoClock? clock = null, ILogger? assistantLog = null)
    {
        Folder = Directory.CreateTempSubdirectory("playmap-ops-").FullName;
        Credentials = new NodeCredentialStore(Path.Combine(Folder, "keys"));
        Model = model ?? new ScriptedModel();
        Scanner = new ScanRunner(display, scan);
        clock ??= MonoClock.Instance;
        Devices = new DeviceService(Credentials, clock, new DeviceServiceOptions
        {
            PjLinkUdpPort = -1,
            Timings = DriverTimings.Fast,
            Manager = new DeviceManagerOptions { TickMs = 50 },
        });
        Home = new NodeHome(Folder);
        var home = Home;
        Assistant = new AssistantService(Credentials, assistant ?? new AssistantSettings { Enabled = true }, new AssistantServiceOptions
        {
            Save = s => AssistantSettingsFile.Save(home, s),
            Log = assistantLog,
            Providers = (_, key) =>
            {
                Model.KeySeen = key;
                return Model;
            },
        });
        var parts = EngineServices.ForTests(clock);
        parts = parts with
        {
            // A node id of its own: the preview rings are files every process of this user shares.
            Settings = parts.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] },
            Scan = Scanner,
            ProjectFolder = Folder,
            JournalPath = Path.Combine(Folder, "journal.jsonl"),
            NetworkControl = network,
        };
        parts = services?.Invoke(parts) ?? parts;
        Engine = new EngineCore(parts with { Devices = Devices, Assistant = Assistant, Proposals = Assistant }, project);
        // A folder run whatever the test machine holds: STATE's machine.install never reads the host's install key.
        Engine.InstallSource = () => PlayMap.Engine.Install.InstallFacts.NotInstalled;
        Engine.InstallReadAgain();
        _ops = OpsServices.Attach(Engine);
    }

    public string Folder { get; }

    /// <summary>The node's folder as the host has it: the assistant's settings file is kept here.</summary>
    public NodeHome Home { get; }

    public NodeCredentialStore Credentials { get; }

    public ScriptedModel Model { get; }

    public ScanRunner Scanner { get; }

    public DeviceService Devices { get; }

    public AssistantService Assistant { get; }

    public EngineCore Engine { get; }

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

    public JsonElement State() => JsonDocument.Parse(Engine.State().Json).RootElement.Clone();

    public JsonElement Context() => JsonDocument.Parse(Engine.Context()).RootElement.Clone();

    /// <summary>Waits for a fact, never a fixed time.</summary>
    public static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>How often the desk renews the views it shows (MainViewModel: every 5 s, a third of the node's 15 s lease).</summary>
    public static readonly TimeSpan DeskRenewsViews = TimeSpan.FromSeconds(5);

    /// <summary>Waits for a condition as a desk showing <paramref name="views"/> waits: renewing them every
    /// <see cref="DeskRenewsViews"/>, so a wait longer than the node's lease (a scan under load) keeps them shown.</summary>
    public async Task<bool> EventuallyShowing(Func<bool> condition, int timeoutMs, params string[] views)
    {
        var sw = Stopwatch.StartNew();
        var renewed = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            if (renewed.Elapsed >= DeskRenewsViews)
            {
                foreach (var view in views)
                    await Ok($"VIEW {view} KEEP");
                renewed.Restart();
            }
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>The journal's rows about views (a lease that ran out is one), for a failure's words.</summary>
    public string ViewRows() => string.Join("; ", Engine.Journal.Recent(200).Where(r => r.Kind == "view").Select(r => r.Text));

    /// <summary>The newest picture on a view's ring: its size and how many pictures the ring has had (null before
    /// the first).</summary>
    public PreviewFrameInfo? Picture(string viewId)
    {
        // Not shown (a lease that lapsed, a view being shown again): no picture yet.
        if (Engine.Previews.RingOf(viewId) is not { } path || !File.Exists(path))
            return null;
        using var reader = new PreviewRingReader(path);
        var pixels = new byte[reader.MaxWidth * reader.MaxHeight * 4];
        return reader.TryRead(pixels, 0, out var info) ? info : null;
    }

    /// <summary>Everything the journal kept, the file included (read after the engine is gone).</summary>
    public string JournalText()
    {
        var path = Path.Combine(Folder, "journal.jsonl");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _ops.DisposeAsync();
        }
        finally
        {
            // A service that fails to stop fails the test, and leaves no engine running in the test host.
            await Engine.DisposeAsync();
        }
    }

    /// <summary>Removes the rig's folder (after <see cref="DisposeAsync"/>, once the journal has been read).</summary>
    public void DeleteFolder()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a closing writer: the temp folder is left for the OS to clear.
        }
    }
}

/// <summary>A model that answers from a script, one reply per call; when the script runs out it says "Done.".</summary>
internal sealed class ScriptedModel : IAssistantProvider
{
    private readonly Queue<Func<AssistantRequest, AssistantReply>> _script = new();
    private readonly Lock _gate = new();

    public AssistantProviderKind Kind => AssistantProviderKind.Claude;

    public string Model => "scripted-model";

    public int ValidationRetries => 2;

    /// <summary>The key the service handed the provider (to prove it came from the credential store).</summary>
    public string? KeySeen { get; set; }

    public List<AssistantRequest> Requests { get; } = [];

    public ScriptedModel Then(Func<AssistantRequest, AssistantReply> reply)
    {
        lock (_gate)
            _script.Enqueue(reply);
        return this;
    }

    public ScriptedModel Then(AssistantReply reply) => Then(_ => reply);

    public Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Func<AssistantRequest, AssistantReply> next;
        lock (_gate)
        {
            Requests.Add(request);
            next = _script.Count > 0 ? _script.Dequeue() : _ => Say("Done.");
        }
        var reply = next(request);
        foreach (var t in reply.Parts.OfType<TextPart>())
            onText?.Invoke(t.Text);
        return Task.FromResult(reply);
    }

    public void Dispose()
    {
        // Nothing is held.
    }

    public static AssistantReply Say(string text) =>
        new([new TextPart(text)], StopKind.EndTurn, new UsageCounts(100, 20, 0, 0), "scripted-model");

    public static AssistantReply Propose(string id, string reason, params string[] lines) =>
        new([new ToolCallPart(id, AssistantTools.ProposeCommands,
            JsonSerializer.Serialize(new Dictionary<string, object> { ["lines"] = lines, ["reason"] = reason }))],
            StopKind.ToolUse, new UsageCounts(100, 20, 0, 0), "scripted-model");
}

/// <summary>The network edge's link verbs, recorded exactly as the node handed them over (the secret words included).</summary>
internal sealed class LinkSpy : INetworkControl
{
    public ConcurrentQueue<(string? Host, string? Secret)> Patterns { get; } = new();

    public ConcurrentQueue<(string? Host, string? Secret)> Stagewatch { get; } = new();

    public Task<string> LinkPatternsAsync(string? host, string? token, CancellationToken ct)
    {
        Patterns.Enqueue((host, token));
        return Task.FromResult("following Patterns at " + host);
    }

    public Task<string> LinkStagewatchAsync(string? host, string? key, CancellationToken ct)
    {
        Stagewatch.Enqueue((host, key));
        return Task.FromResult("following Stagewatch at " + host);
    }

    public Task<string> LeadAsync(string group, CancellationToken ct) => Task.FromResult("leading " + group);

    public Task<string> FollowAsync(string group, string nodeId, CancellationToken ct) => Task.FromResult("following " + nodeId);

    public Task<string> FetchAsync(IReadOnlyList<MediaItem> items, string? fromNode, CancellationToken ct) => Task.FromResult("nothing to fetch");
}
