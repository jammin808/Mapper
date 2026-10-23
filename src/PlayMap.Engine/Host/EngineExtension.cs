using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Host;

/// <summary>A part the host attaches beside the engine — projector devices, the assistant. Its life, in the host's
/// order:
/// <list type="number">
/// <item>made by its <see cref="EngineExtensionFactory"/> after the media backend, the network, the render host and
/// the scan backend, from an <see cref="ExtensionSetup"/> naming all of them;</item>
/// <item><see cref="Configure"/>: the services the engine will be built from, answered with this extension's own
/// added (init properties on <see cref="EngineServices"/> declared in the extension's own file);</item>
/// <item><see cref="StartAsync"/>: the engine exists and the render host runs, but no transport is open yet — so
/// nothing reaches the node before the extension is attached (an approval queue handed to
/// <see cref="ExtensionSetup.Approvals"/> here is in place before the first MCP call);</item>
/// <item><see cref="IAsyncDisposable.DisposeAsync"/>: after every transport has closed, in reverse order of start,
/// before the render host and the engine go. The engine refuses every line by then, so a line source an extension
/// owns (OSC, MIDI, DMX, its bindings) cannot start work on a part disposed before it.</item>
/// </list>
/// An extension whose factory, Configure or start throws is left out; its words become a fault in STATE and the
/// node runs without it.</summary>
public interface IEngineExtension : IAsyncDisposable
{
    /// <summary>A short name for STATE's faults and the log ("devices", "assistant").</summary>
    string Name { get; }

    /// <summary>The services with this extension's own added. The default adds nothing.</summary>
    EngineServices Configure(EngineServices services) => services;

    /// <summary>Attaches to the running engine. Transports open after every extension has started.</summary>
    Task StartAsync(EngineCore engine, CancellationToken ct);
}

/// <summary>Makes an extension from what the host has built so far.</summary>
public delegate IEngineExtension EngineExtensionFactory(ExtensionSetup setup);

/// <summary>Everything built before the engine, for an extension's factory.</summary>
/// <param name="Approvals">Hand the assistant's proposal queue (and the "AI may act" switch) to it: HTTP's MCP tools
/// ask it before any high-risk line.</param>
public sealed record ExtensionSetup(
    EngineOptions Options,
    NodeHome Home,
    NodeSettings Settings,
    IMonoClock Mono,
    ShowWallClock Wall,
    ILoggerFactory Logs,
    IMediaBackend Media,
    NetworkHost Network,
    IOutputBackend Outputs,
    IScanDisplay Display,
    IScanBackend Scan,
    ApprovalHook Approvals)
{
    /// <summary>Hands the host a faults source (a control port refused, a runtime absent, a page's port in use): STATE's faults
    /// and the context list its faults beside the host's own for the node's life.</summary>
    public Action<INodeFaults>? AddFaults { get; init; }

    /// <summary>The node's time (<see cref="EngineHostOptions.Time"/>; the system's in a node) for an extension that counts waits,
    /// expiries and timers on a <see cref="TimeProvider"/>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>Extensions made from a function, for parts that need nothing before the engine exists.</summary>
public static class EngineExtension
{
    /// <summary>An extension that attaches once the engine runs: <paramref name="start"/> answers what to dispose at
    /// shutdown (or null).</summary>
    public static EngineExtensionFactory OnStart(string name, Func<EngineCore, CancellationToken, Task<IAsyncDisposable?>> start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(start);
        return _ => new Started(name, start);
    }

    private sealed class Started(string name, Func<EngineCore, CancellationToken, Task<IAsyncDisposable?>> start) : IEngineExtension
    {
        private IAsyncDisposable? _attached;

        public string Name => name;

        public async Task StartAsync(EngineCore engine, CancellationToken ct) => _attached = await start(engine, ct);

        public ValueTask DisposeAsync() => _attached?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
