using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Http;

/// <summary>A signal that fires on every published change: a long-poll or a WebSocket's sender awaits the next
/// one and then reads the newest STATE itself (latest wins — nothing is queued per change).</summary>
public sealed class ChangeSignal
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Next => Volatile.Read(ref _next).Task;

    public void Fire() => Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>True when a change came within the wait; false on the wait's end.</summary>
    public Task<bool> WaitAsync(Task next, TimeSpan max, CancellationToken ct) => WaitAsync(next, max, TimeProvider.System, ct);

    /// <summary>True when a change came within the wait, counted on <paramref name="time"/>; false on the wait's end, which is never
    /// sooner than <paramref name="max"/>: the runtime's timed wait counts whole milliseconds, dropping a part of one (and ending at
    /// once on less than one), so a part of a millisecond is waited as a whole one.</summary>
    public async Task<bool> WaitAsync(Task next, TimeSpan max, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(time);
        if (max <= TimeSpan.Zero)
            return next.IsCompleted;
        try
        {
            await next.WaitAsync(TimeSpan.FromMilliseconds((max.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond), time, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>What the web's gate made of a request.</summary>
public enum GateVerdict
{
    /// <summary>Trusted (this machine, a trusted address) or the node asks no token: in, as paired.</summary>
    Open,
    /// <summary>It carried the right token: in, as paired.</summary>
    Token,
    /// <summary>No token where one is needed: 401.</summary>
    NeedsToken,
    /// <summary>A wrong token: 401, and counted against the address.</summary>
    WrongToken,
    /// <summary>Too many wrong tokens from this address in the last minute: 429, the token is not even looked at.</summary>
    Locked,
    /// <summary>Plain HTTP from another machine while HTTPS is required: 403.</summary>
    NeedsHttps,
}

/// <summary>The web's gate (docs/WIRE.md): the token rides in <c>X-PlayMap-Token</c> or <c>Authorization: Bearer</c>,
/// never in a URL (a query string is never read for it); reads and verbs alike need it from anything but this machine
/// and the trusted list when a token is set; five wrong tokens in a minute lock the address out for the rest of
/// that minute; Require HTTPS refuses plain requests from anything but loopback.</summary>
public sealed class TokenGate(int wrongPerMinute = 5)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<long>> _wrong = new(StringComparer.Ordinal);

    public GateVerdict Check(HttpContext context, NodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var remote = context.Connection.RemoteIpAddress;
        var loopback = NetAddress.IsLoopback(remote);
        if (settings.RequireHttps && !context.Request.IsHttps && !loopback)
            return GateVerdict.NeedsHttps;
        if (!NetAddress.TokenNeeded(settings) || NetAddress.IsTrusted(remote, settings))
            return GateVerdict.Open;
        return CheckToken(context, settings);
    }

    /// <summary>The token alone, whatever the address: the address's lockout, then the header's token compared in fixed time, a
    /// wrong one counted. A request its address would let in, but that names a host which is not the node's, is let in by this.</summary>
    public GateVerdict CheckToken(HttpContext context, NodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var address = NetAddress.Text(context.Connection.RemoteIpAddress);
        if (Locked(address))
            return GateVerdict.Locked;
        var presented = Presented(context.Request);
        if (presented.Length == 0)
            return GateVerdict.NeedsToken;
        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(Pairing.Normalise(presented)),
                System.Text.Encoding.UTF8.GetBytes(Pairing.Normalise(settings.Token))))
            return GateVerdict.Token;
        NoteWrong(address);
        return GateVerdict.WrongToken;
    }

    /// <summary>True while an address has spent its wrong tokens for this minute (the line channel's proofs count here too).</summary>
    public bool IsLocked(string address) => Locked(address);

    /// <summary>A wrong proof on the web port's line channel, counted against its address like a wrong header token.</summary>
    public void NoteWrongFrom(string address) => NoteWrong(address);

    /// <summary>The token a request carries in a header ("" when none).</summary>
    public static string Presented(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.TryGetValue("X-PlayMap-Token", out var t) && t.ToString() is { Length: > 0 } token)
            return token.Trim();
        var auth = request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
    }

    private bool Locked(string address)
    {
        lock (_gate)
            return _wrong.TryGetValue(address, out var q) && Prune(q) >= wrongPerMinute;
    }

    private void NoteWrong(string address)
    {
        lock (_gate)
        {
            if (!_wrong.TryGetValue(address, out var q))
                _wrong[address] = q = new Queue<long>();
            Prune(q);
            q.Enqueue(Stopwatch.GetTimestamp());
            if (_wrong.Count > 4096)
                foreach (var key in _wrong.Where(p => Prune(p.Value) == 0).Select(p => p.Key).ToList())
                    _wrong.Remove(key);
        }
    }

    private static int Prune(Queue<long> q)
    {
        while (q.Count > 0 && Stopwatch.GetElapsedTime(q.Peek()) > Window)
            q.Dequeue();
        return q.Count;
    }
}

/// <summary>One port, two protocols (Show Bus §10): a connection whose first byte opens a TLS handshake (0x16)
/// goes through the TLS middleware; any other first byte goes straight to HTTP. The first byte is peeked, never
/// consumed. A connection that sends nothing within the wait is closed. Also the web's per-address ceiling.</summary>
internal sealed class TlsSniffer(ConnectionLedger ledger, int maxPerAddress, ILogger log)
{
    private static readonly TimeSpan FirstByteWait = TimeSpan.FromSeconds(10);
    private ConnectionDelegate? _plain;

    /// <summary>The outermost middleware: admits by address, peeks the first byte, picks the branch.</summary>
    public ConnectionDelegate Outer(ConnectionDelegate tls) => async context =>
    {
        var address = context.RemoteEndPoint is IPEndPoint ep ? NetAddress.Text(ep.Address) : "?";
        if (!ledger.TryAdmit(address, int.MaxValue, maxPerAddress))
        {
            log.LogWarning("The web port refused a connection from {Address}: {Max} already open from it.", address, maxPerAddress);
            context.Abort();
            return;
        }
        try
        {
            if (_plain is null)
            {
                await tls(context);
                return;
            }
            var input = context.Transport.Input;
            byte first;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed))
            {
                wait.CancelAfter(FirstByteWait);
                System.IO.Pipelines.ReadResult read;
                try
                {
                    read = await input.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    context.Abort();
                    return;
                }
                if (read.Buffer.IsEmpty)
                {
                    input.AdvanceTo(read.Buffer.Start);
                    return;
                }
                first = read.Buffer.FirstSpan[0];
                input.AdvanceTo(read.Buffer.Start);   // peeked, not consumed: the branch reads it again
            }
            if (first == 0x16)
                await tls(context);
            else
                await _plain(context);
        }
        finally
        {
            ledger.Release(address);
        }
    };

    /// <summary>The innermost middleware (after TLS): captures the HTTP pipeline so plain connections skip TLS.</summary>
    public ConnectionDelegate Inner(ConnectionDelegate http)
    {
        _plain = http;
        return http;
    }
}

/// <summary>The words a refused web request reads, with its status.</summary>
internal static class GateWords
{
    public static (int Status, string Words) Of(GateVerdict verdict) => verdict switch
    {
        GateVerdict.NeedsHttps => (StatusCodes.Status403Forbidden, WireWords.UseHttps),
        GateVerdict.Locked => (StatusCodes.Status429TooManyRequests, WireWords.TooManyWrongTokens),
        GateVerdict.WrongToken => (StatusCodes.Status401Unauthorized, WireWords.WrongToken),
        _ => (StatusCodes.Status401Unauthorized, WireWords.TokenNeeded),
    };
}
