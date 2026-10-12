using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Numerics;

namespace PlayMap.Net;

/// <summary>A trailing throttle: the first <see cref="Arm"/> starts a window; every arm inside it is absorbed;
/// at the window's end the action runs once and reads the newest facts itself (latest wins). An arm while the
/// action runs starts the next window, so the last change is never lost.</summary>
public sealed class TrailingThrottle(TimeSpan window, Action fire, ILogger log, CancellationToken stop)
{
    private int _armed;

    public void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) == 0)
            _ = FireLaterAsync();
    }

    private async Task FireLaterAsync()
    {
        try
        {
            await Task.Delay(window, stop);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Volatile.Write(ref _armed, 0);
        try
        {
            fire();
        }
        catch (Exception ex)
        {
            // A push that faults is that push's: logged, and the next change pushes again.
            log.LogWarning(ex, "A throttled push faulted.");
        }
    }
}

/// <summary>Jitter for redials so a room of clients does not redial in step — a shuffle, not a secret (seeded once
/// from the cryptographic generator so two processes differ).</summary>
public sealed class Jitter
{
    private readonly object _gate = new();
    private Pcg32 _rng = new(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));

    public static readonly Jitter Shared = new();

    /// <summary>Uniform in [0, 1).</summary>
    public double Next01()
    {
        lock (_gate)
            return _rng.NextDouble();
    }

    /// <summary>The wait before redial <paramref name="attempt"/> (0-based): the contract's 0.5 s doubling to 5 s, ±20 %.</summary>
    public TimeSpan Redial(int attempt) => TimeSpan.FromMilliseconds(Liveness.RedialMs(attempt, Next01()));
}

/// <summary>Waits that end quietly on cancellation: a loop that sleeps between beats checks its token itself.</summary>
public static class Waits
{
    public static async Task<bool> DelayAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
