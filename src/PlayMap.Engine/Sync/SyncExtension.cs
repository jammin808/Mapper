using PlayMap.Engine.Host;

namespace PlayMap.Engine.Sync;

/// <summary>The sync part in the exe's composition: the group's sync node over the network host, handed to the engine (the
/// fan-out, the board, the feed) and to show's ARM/PREFLIGHT ALL through <see cref="EngineServices.Fanout"/>; its preflight check;
/// its role re-entered once the engine runs. Disposed after the transports close.</summary>
public sealed class SyncExtension(ExtensionSetup setup, SyncNodeOptions? options = null) : IEngineExtension
{
    /// <summary>The part as the exe's composition adds it.</summary>
    public static readonly EngineExtensionFactory Factory = s => new SyncExtension(s);

    private SyncNode? _node;

    public string Name => "sync";

    /// <summary>The part with its pace and vblank source chosen (tests: synthetic vblanks, a fast watch).</summary>
    public static EngineExtensionFactory With(SyncNodeOptions options) => s => new SyncExtension(s, options);

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var node = new SyncNode(setup.Network, setup.Home, setup.Wall, setup.Settings.Sync, setup.Logs.CreateLogger("PlayMap.Engine.Sync"), options);
        _node = node;
        return services with
        {
            GroupSync = node,
            Fanout = node,
            PreflightChecks = [.. services.PreflightChecks, new SyncPreflight(node)],
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_node is not { } node)
            return Task.CompletedTask;
        node.Attach(engine);
        return engine.RestoreFollowBlackoutAsync(setup.Settings.Sync.FollowPatternsBlackout);
    }

    public ValueTask DisposeAsync() => _node?.DisposeAsync() ?? ValueTask.CompletedTask;
}
