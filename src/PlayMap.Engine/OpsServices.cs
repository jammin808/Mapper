using PlayMap.Engine.Scanning;

namespace PlayMap.Engine;

/// <summary>
/// Attaches the operations services an engine was built with — the scan runner (<see cref="EngineServices.Scan"/>
/// when it is a <see cref="ScanRunner"/>), projector control (<see cref="EngineServices.Devices"/>) and the
/// assistant (<see cref="EngineServices.Assistant"/>) — to the engine, once it exists. The host's extension hook
/// is this one call; the value it returns owns them: disposing it detaches each and disposes it.
/// </summary>
public static class OpsServices
{
    public static IAsyncDisposable Attach(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var owned = new List<IAsyncDisposable>();
        if (engine.Services.Scan is ScanRunner runner)
        {
            runner.Bind(engine);
            owned.Add(runner);
        }
        if (engine.Services.Devices is { } devices)
        {
            var attached = devices.Attach(engine);
            owned.Add(devices);
            owned.Add(attached);
        }
        if (engine.Services.Assistant is { } assistant)
        {
            var attached = assistant.Attach(engine);
            owned.Add(assistant);
            owned.Add(attached);
        }
        return new Owned(owned);
    }

    /// <summary>Disposes in reverse order of attachment (each detaches before its service goes), once.</summary>
    private sealed class Owned(List<IAsyncDisposable> parts) : IAsyncDisposable
    {
        private int _done;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
                return;
            for (var i = parts.Count - 1; i >= 0; i--)
                await parts[i].DisposeAsync().ConfigureAwait(false);
        }
    }
}
