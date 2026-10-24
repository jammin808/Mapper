using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Fleet;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Fleet;

/// <summary>Where a peer's health is read: its id and name, the address its hello came from, its web port, and the certificate its
/// hello names (null when it serves no TLS).</summary>
public sealed record FleetDial(string NodeId, string Name, string Host, int Port, string? Fingerprint)
{
    public bool Tls => Fingerprint is { Length: > 0 };
}

/// <summary>How a subscription to a peer's health ended: its words, whether a record arrived, whether the peer's certificate was
/// refused (a changed or foreign certificate: no record is read until the operator accepts it).</summary>
public sealed record FeedEnd(string Words, bool Heard, bool Refused);

/// <summary>The fleet monitor's transport: one subscription to a peer's health until it ends (a fake in tests).</summary>
public interface IHealthFeeds
{
    /// <summary>Runs one subscription until the peer closes, goes, sends what is not its own record, or <paramref name="ct"/> stops
    /// it. <paramref name="onPin"/> hears the pin's verdict ("pinned", "first use", "plain", "refused") once it is known. A dial that ends
    /// once <paramref name="ct"/> is cancelled ends as that cancel, whatever it met.</summary>
    Task<FeedEnd> RunAsync(FleetDial dial, Action<HealthRecord> onRecord, Action<string> onPin, CancellationToken ct);
}

/// <summary>A peer's <c>/health</c> as a WebSocket: <c>wss://</c> with the certificate checked against the pin (and the hello's
/// fingerprint) when the hello offers TLS, <c>ws://</c> when it offers none. The pin is set on the first record that is the peer's
/// own (its node id is the hello's), as the first link that authenticated sets the node's other pins; a certificate that changed
/// after is refused, and says so, until the operator removes the old pin from the fleet's pin file (read again on every dial).
/// Pins live in their own file (fingerprints are public).</summary>
public sealed class HealthFeedClient(Func<PinStore> pins, TimeProvider time, ILogger? log = null) : IHealthFeeds
{
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>Pins kept in one store for the client's life (a test, or a node with no pin file).</summary>
    public HealthFeedClient(PinStore pins, TimeProvider time, ILogger? log = null)
        : this(() => pins, time, log)
    {
    }

    /// <summary>The words for a certificate refused: changed since it was pinned, or not the one the hello names.</summary>
    public static string RefusedWords(PinVerdict verdict, string name, string presented) => verdict == PinVerdict.Changed
        ? $"CERTIFICATE CHANGED: {name} now shows {TlsFingerprint.Short(presented)} — refused until its old pin is removed from the fleet's pin file"
        : PinStore.Words(verdict, name, presented);

    /// <summary>How long a dial and its upgrade may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<FeedEnd> RunAsync(FleetDial dial, Action<HealthRecord> onRecord, Action<string> onPin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dial);
        ArgumentNullException.ThrowIfNull(onRecord);
        ArgumentNullException.ThrowIfNull(onPin);
        var key = PinStore.NodeKey(dial.NodeId);
        PinVerdict? verdict = null;
        var presented = "";
        using var socket = new ClientWebSocket();
        if (dial.Tls)
        {
            socket.Options.RemoteCertificateValidationCallback = pins().Validator(key, dial.Fingerprint, (v, p) =>
            {
                verdict = v;
                presented = p;
            });
        }
        var host = IPAddress.TryParse(dial.Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + dial.Host + "]" : dial.Host;
        var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"{(dial.Tls ? "wss" : "ws")}://{host}:{dial.Port}/health"));
        try
        {
            using (var dialing = new CancellationTokenSource(ConnectTimeout, time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, dialing.Token))
                await socket.ConnectAsync(uri, linked.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            // Once the stop is taken, whatever the dial ends on is the stop's (a refusal or a reset as well as the cancel): the
            // subscription ends as the stop, never as a fault of the feed.
            ct.ThrowIfCancellationRequested();
            if (verdict is PinVerdict.Changed or PinVerdict.Mismatch)
            {
                onPin("refused");
                return new FeedEnd(RefusedWords(verdict.Value, dial.Name, presented), false, true);
            }
            return new FeedEnd($"{dial.Name}'s health at {uri.Authority} did not answer: {ex.Message}", false, false);
        }
        onPin(!dial.Tls ? "plain" : verdict == PinVerdict.Pinned ? "pinned" : "first use");
        var heard = false;
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                    return new FeedEnd($"{dial.Name} closed its health feed", heard, false);
                await message.WriteAsync(buffer.AsMemory(0, r.Count), ct);
                if (message.Length > HealthJson.MaxBytes)
                    return new FeedEnd($"{dial.Name} sent a health record over {HealthJson.MaxBytes / 1024} KB: dropped, the feed closed", heard, false);
                if (!r.EndOfMessage)
                    continue;
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (HealthJson.Parse(json) is not { } record)
                    continue;
                if (record.Facts.Node != dial.NodeId)
                    return new FeedEnd($"{dial.Name}'s address answers as another node ({record.Facts.Node}): not read", heard, false);
                if (!heard && dial.Tls && verdict == PinVerdict.FirstUse && presented.Length > 0)
                {
                    pins().Pin(key, presented);
                    onPin("pinned");
                }
                heard = true;
                onRecord(record);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "{Peer}'s health feed ended.", dial.Name);
            return new FeedEnd($"{dial.Name}'s health feed ended: {ex.Message}", heard, false);
        }
        return new FeedEnd($"{dial.Name}'s health feed ended", heard, false);
    }
}
