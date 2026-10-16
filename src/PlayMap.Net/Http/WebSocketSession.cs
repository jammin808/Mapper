using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Http;

/// <summary>One screen or client on <c>/ws</c> (Show Bus §3–§5): state frames <c>{"type":"state",…}</c> (STATE with
/// its v, seq and id) at once and on every change, latest-wins — a slow reader gets the newest, never a backlog; at
/// least one frame or beat every 2 s, <c>{"type":"beat","live":false}</c> (and no frame) while the node's show thread
/// is stuck; <c>{"cmd":"…","id":"a1"}</c> runs a line and answers its receipt; <c>{"cmd":"ping","t":…}</c> answers a
/// pong with t2 at read and t3 at send. One sender at a time on the socket.</summary>
internal sealed class WebSocketSession(
    WebSocket socket,
    CommandRunner runner,
    ChangeSignal changes,
    Func<bool> isAnimating,
    Func<bool> isLive,
    SessionInfo session,
    TimeSpan beatEvery,
    ILogger log) : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly byte[] Beat = "{\"type\":\"beat\"}"u8.ToArray();
    private static readonly byte[] StuckBeat = "{\"type\":\"beat\",\"live\":false}"u8.ToArray();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly TopicSubscriptions _topics = new();
    private readonly PushQueue _pushes = new();
    private long _lastSentAt = Stopwatch.GetTimestamp();

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var sender = SendLoopAsync(linked.Token);
            var receiver = ReceiveLoopAsync(linked.Token);
            var pump = PushLoopAsync(linked.Token);
            await Task.WhenAny(sender, receiver, pump);
            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(sender, receiver, pump);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // One side ended the conversation; the other's end is expected.
                log.LogDebug(ex, "A WebSocket loop ended with its peer.");
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", bye.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // A screen that went away mid-close: nothing more to say to it.
            log.LogDebug(ex, "A WebSocket closed abruptly.");
        }
    }

    public void Dispose()
    {
        _topics.Dispose();
        _pushes.Dispose();
        _send.Dispose();
    }

    /// <summary>The package topics' lines this screen subscribed to, as push frames, in order.</summary>
    private async Task PushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await _pushes.WaitAsync(ct);
            foreach (var frame in _pushes.Drain())
                await SendAsync(frame, ct);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        string? last = null;
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var next = changes.Next;
            if (!isLive())
            {
                // The show thread is stuck: its STATE is a frozen time, so no frame — a beat that says so.
                await SendAsync(StuckBeat, ct);
                last = null;
                await changes.WaitAsync(next, beatEvery, ct);
                continue;
            }
            var json = runner.Endpoint.State().Json;
            if (!string.Equals(json, last, StringComparison.Ordinal))
            {
                await SendAsync(Encoding.UTF8.GetBytes(Frame(json)), ct);
                last = json;
            }
            var animating = isAnimating();
            var wait = animating ? TimeSpan.FromMilliseconds(Math.Min(1000, beatEvery.TotalMilliseconds)) : beatEvery;
            var changed = await changes.WaitAsync(next, wait, ct);
            if (!changed && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSentAt)) >= beatEvery - TimeSpan.FromMilliseconds(50))
                await SendAsync(Beat, ct);
        }
    }

    /// <summary>{"type":"state", then STATE's own keys (v, seq, id first).</summary>
    public static string Frame(string stateJson) =>
        stateJson.StartsWith('{') && stateJson.Length > 2 ? "{\"type\":\"state\"," + stateJson[1..] : "{\"type\":\"state\"}";

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var count = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (count >= buffer.Length)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "a message past 64 KiB", ct);
                    return;
                }
                r = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                count += r.Count;
            }
            while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text)
                continue;
            var readAt = runner.Wall.UnixMs;
            var reply = await HandleAsync(buffer.AsMemory(0, count), readAt, ct);
            if (reply is not null)
                await SendAsync(reply, ct);
        }
    }

    private async Task<byte[]?> HandleAsync(ReadOnlyMemory<byte> message, long readAt, CancellationToken ct)
    {
        string? verb;
        string? id;
        long? t1;
        var args = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Error("a message is a JSON object: {\"cmd\":\"VIDEO SEEK 83\",\"id\":\"a1\"}");
            verb = JsonText.Str(root, "cmd");
            id = JsonText.Str(root, "id") is { Length: > 0 } raw && WireGrammar.IsRequestId(raw) ? raw : null;
            t1 = root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var v) ? v : null;
            if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var e in a.EnumerateArray())
                    args.Add(e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText());
        }
        catch (JsonException)
        {
            return Error("bad JSON");
        }
        if (string.IsNullOrWhiteSpace(verb))
            return Error("missing cmd");
        if (verb == "ping")
        {
            if (t1 is not { } t)
                return "{\"type\":\"pong\"}"u8.ToArray();
            var t3 = runner.Wall.UnixMs;
            return Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{{\"type\":\"pong\",\"t\":{t},\"t2\":{readAt},\"t3\":{t3}}}"));
        }
        var text = args.Count == 0 ? verb : verb + " " + WireGrammar.Join(args);
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return Encoding.UTF8.GetBytes(CommandRunner.ReceiptJson(id, CommandReply.Fail(error!.Reason)));
        if (line.Verb == "SUBSCRIBE" && line.Count > 1 && Subscribe(line) is { } subscribed)
        {
            // The receipt first, then what the topic said while it opened.
            await SendAsync(Encoding.UTF8.GetBytes(CommandRunner.ReceiptJson(id ?? line.RequestId, subscribed)), ct);
            _topics.Release();
            return null;
        }
        if (line.Verb is "HELLO" or "NONCE" or "AUTH" or "AUTH-PROOF" or "SUBSCRIBE")
            return Encoding.UTF8.GetBytes(CommandRunner.ReceiptJson(id ?? line.RequestId, CommandReply.Fail(WireWords.WireOnly)));
        var reply = await runner.RunAsync(line, session, ct, readAt);
        return Encoding.UTF8.GetBytes(CommandRunner.ReceiptJson(id ?? line.RequestId, reply));
    }

    /// <summary>SUBSCRIBE of a package's topic (STATE reaches every screen already): opened through the endpoint's topics, or OFF to
    /// end them. Null for the session's own topics, which stay the wire's.</summary>
    private CommandReply? Subscribe(WireLine line)
    {
        var topic = line.Word(1);
        if (topic.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            _topics.Dispose();
            return CommandReply.Success(effect: "topics off");
        }
        if (TopicSubscriptions.IsOwn(topic))
            return null;
        // The topic's owner decides who may open it (a paired-only topic refuses an unpaired screen with words).
        return _topics.TryOpen(runner.Endpoint, topic, line.Words.Skip(2).ToList(), session, _pushes.SinkFor, out var words)
            ? CommandReply.Success(effect: "subscribed " + words.ToLowerInvariant())
            : CommandReply.Fail(words);
    }

    private static byte[] Error(string words) => JsonText.WriteBytes(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "error");
        w.WriteString("error", words);
        w.WriteEndObject();
    });

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token);
            Volatile.Write(ref _lastSentAt, Stopwatch.GetTimestamp());
        }
        finally
        {
            _send.Release();
        }
    }
}

/// <summary>Body reading with a ceiling: a command is a line, not an upload.</summary>
internal static class Bodies
{
    public static async Task<string?> ReadLineAsync(Stream body, int maxBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        try
        {
            var total = 0;
            while (total <= maxBytes)
            {
                var n = await body.ReadAsync(buffer.AsMemory(total, maxBytes + 1 - total), ct);
                if (n == 0)
                    break;
                total += n;
            }
            return total > maxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
