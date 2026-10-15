using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Align;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>An engine with the node's real media backend, the optimise service whose worker is the node host built beside these
/// tests (tools/PlayMap.Node optimise-worker — a real process), a show gate the test arms, a hand-moved monotonic clock (the
/// governor's and the restart backoff's), its folder of its own, and a headless render host when asked.</summary>
internal sealed class OptimiseRig : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(4);
    public static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private OptimiseRig(DirectoryInfo dir, ManualClock clock, SwitchGate gate, MediaBackend media, OptimiseService service, EngineCore engine, RenderHost? host)
    {
        Dir = dir;
        Clock = clock;
        Gate = gate;
        Media = media;
        Service = service;
        Engine = engine;
        Host = host;
    }

    public DirectoryInfo Dir { get; }
    public ManualClock Clock { get; }
    public SwitchGate Gate { get; }
    public MediaBackend Media { get; }
    public OptimiseService Service { get; }
    public EngineCore Engine { get; }
    public RenderHost? Host { get; }
    public NodeHome Home => new(Path.Combine(Dir.FullName, "node"));

    public static OptimiseRig Start(Project? project = null, bool render = false, OptimiseOptions? options = null)
    {
        FfmpegFactAttribute.UseDefaultLibraries();
        var dir = Directory.CreateTempSubdirectory("playmap-optimise-");
        var home = new NodeHome(Path.Combine(dir.FullName, "node"));
        var media = new MediaBackend(home, NullLogger.Instance);
        // The margin a show node keeps is 10 GB; a test box's disk is not the point here.
        var settings = OptimiseNodeSettings.Default with { MinFreeGb = 0 };
        var service = new OptimiseService(home, settings, NullLogger.Instance,
            options ?? new OptimiseOptions { Worker = WorkerLaunch.Dll(NodeExe.Locate()), Step = TimeSpan.FromMilliseconds(100) });
        RenderHost? host = null;
        if (render)
            host = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = false }, out var why) ?? throw new InvalidOperationException("no render host: " + why);
        var clock = new ManualClock(5_000_000_000);
        var gate = new SwitchGate();
        var services = EngineServices.ForTests(clock);
        services = services with
        {
            Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8] },
            Outputs = host is null ? services.Outputs : host,
            Media = media,
            Optimise = service,
            ShowGate = gate,
            ProjectFolder = dir.FullName,
        };
        var engine = new EngineCore(services, project ?? Project.CreateDefault("Optimise"));
        host?.Start(engine);
        media.Attach(engine);
        media.Changed += engine.FactsChanged;
        service.Said += (kind, words, ok) => engine.Journal.Add(kind, words, ok: ok);
        service.Attach(engine);
        return new OptimiseRig(dir, clock, gate, media, service, engine, host);
    }

    public async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    public async Task<string> Ok(string line)
    {
        var r = await Run(line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    /// <summary>The media id a MEDIA IMPORT answered, once its probe has landed.</summary>
    public async Task<string> ImportAsync(string path, string name)
    {
        var reply = await Ok("MEDIA IMPORT " + WireGrammar.Quote(path) + " " + name);
        using var doc = JsonDocument.Parse(reply);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        await Until(() => Engine.Store.Project.FindMedia(id)?.Info.VideoCodec.Length > 0, "the import's probe");
        return id;
    }

    /// <summary>Waits (polling, no fixed sleep) until the condition holds; fails with the journal when patience runs out.</summary>
    public async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, $"{what} never came; journal: {string.Join(" | ", Engine.Journal.Recent(20).Select(r => r.Text))}");
            await Task.Delay(50);
        }
    }

    public OptimiseJob Job(string id) => Service.Jobs.Single(j => j.Id == id);

    public JsonElement State()
    {
        Engine.FactsChanged();
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();
        await Service.DisposeAsync();
        await Engine.DisposeAsync();
        await Media.DisposeAsync();
        Dir.Delete(recursive: true);
    }
}
