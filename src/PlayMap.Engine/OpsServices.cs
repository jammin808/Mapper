using System.Runtime.ExceptionServices;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine;

/// <summary>
/// Attaches the operations services an engine was built with — the scan runner (<see cref="EngineServices.Scan"/>
/// when it is a <see cref="ScanRunner"/>), projector control (<see cref="EngineServices.Devices"/>) and the
/// assistant (<see cref="EngineServices.Assistant"/>) — to an engine built without the host (tests, a bare core);
/// the host attaches them through <see cref="OpsParts"/>. The value it returns owns them: disposing it detaches
/// each and disposes it.
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

    /// <summary>Disposes in reverse order of attachment (each detaches before its service goes), once. A part whose dispose
    /// throws keeps none of the parts before it: every part is let go, then what failed is thrown, itself when one part failed
    /// and together when several did.</summary>
    internal sealed class Owned(IReadOnlyList<IAsyncDisposable> parts) : IAsyncDisposable
    {
        private int _done;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
                return;
            List<Exception>? failed = null;
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                try
                {
                    await parts[i].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Kept for the owner once the parts before it are let go too.
                    (failed ??= []).Add(ex);
                }
            }
            if (failed is [var one])
                ExceptionDispatchInfo.Throw(one);
            if (failed is not null)
                throw new AggregateException("more than one of the node's operations services failed to stop", failed);
        }
    }
}
