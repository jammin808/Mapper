using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Core.Time;
using PlayMap.Net.Http;

namespace PlayMap.Net.Phone;

/// <summary>How the phone hub runs: where its keys live, its clocks (the wall and expiry clock; the monotonic clock presents are
/// stamped on), and its limits.</summary>
public sealed record PhoneHubOptions
{
    /// <summary>phones.json in the node's own folder.</summary>
    public required string KeysPath { get; init; }

    /// <summary>Tickets expire and wrong tickets are counted on this clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The node's monotonic clock (ns): the clock exchange answers on it, so a phone's capture times compare with presents.</summary>
    public Func<long> MonoNs { get; init; } = () => MonoClock.Instance.NowNs;

    public int MaxConnected { get; init; } = 4;

    /// <summary>Pattern pictures a phone may have on the way unacknowledged.</summary>
    public int MaxInFlight { get; init; } = 6;

    public int MaxBinaryBytes { get; init; } = 16 << 20;

    public int MaxTextBytes { get; init; } = 64 << 10;

    /// <summary>How long a socket may stay open before its hello.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Wrong tickets or keys from one address in a minute before it is locked out for the rest of that minute.</summary>
    public int WrongPerMinute { get; init; } = 5;

    public string NodeId { get; init; } = "";

    public string NodeName { get; init; } = "PlayMap";

    /// <summary>Whether phones may join now (the node's settings; read on every request).</summary>
    public Func<bool> Enabled { get; init; } = () => true;

    public ILogger? Log { get; init; }
}

/// <summary>An invite as its asker receives it: the link (the ticket in its fragment, which a browser never sends), the ticket, when
/// it expires, the name the phone will join under, and the link as a QR code.</summary>
public sealed record PhoneInvite(string Url, string Ticket, DateTimeOffset Expires, string Name, QrMatrix Qr);

/// <summary>A phone that joined (first time: no rig yet) with what it said of itself; the node answers the camera rig it is.</summary>
public sealed record PhoneJoined(string PhoneId, string Name, PhoneProfile Profile, string Rig, bool First);

/// <summary>A phone as STATE shows it.</summary>
public sealed record PhoneFacts(string Id, string Name, string Rig, string Platform, string Model, bool Connected, string Address, string Transport,
    double RttMs, double OffsetMs, double UncertaintyMs, double UploadMBps, PhoneCameraFacts? Camera, string Motion, bool Visible,
    long LastSeenUnixMs, string Words);

/// <summary>
/// The phones of a node: invites, keys, sessions. An invite mints a ticket — 20 random bytes, 32 base32 letters — good once,
/// until it expires; the hub keeps only its SHA-256. A phone opening the invite's link sends the ticket in its first message on
/// <c>/scan/ws</c> (never in a URL, a header or a request body), and is given its own key once: 32 random bytes the page keeps,
/// of which the node keeps only the digest (<see cref="PhoneKeys"/>). Later it joins with the key. The phone's session is that
/// capability alone: never the node's token, and it reaches nothing but its own socket. Five wrong tickets or keys from an
/// address in a minute lock it out for the rest of that minute. At most <see cref="PhoneHubOptions.MaxConnected"/> phones at
/// once; a phone joining again replaces its older socket.
/// </summary>
public sealed class PhoneHub : IAsyncDisposable
{
    private readonly PhoneHubOptions _options;
    private readonly ILogger _log;
    private readonly PhoneKeys _keys;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> _wrong = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PhoneSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    private sealed record Ticket(string Name, DateTimeOffset Expires, string InvitedBy);

    public PhoneHub(PhoneHubOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? NullLogger.Instance;
        _keys = new PhoneKeys(options.KeysPath);
        Module = new PhoneWebModule(this);
    }

    public PhoneHubOptions Options => _options;

    /// <summary>The page and its socket under /scan, for the node's web port.</summary>
    public IWebModule Module { get; }

    /// <summary>The node's answer to a join: the camera rig the phone is (made or updated on its lane). Without it the phone joins
    /// with no rig.</summary>
    public Func<PhoneJoined, CancellationToken, Task<string>>? Join { get; set; }

    /// <summary>Raised when a phone joins, leaves or a fact STATE shows moves (on the thread that saw it).</summary>
    public event Action? Changed;

    public int Connected => _sessions.Count(s => s.Value.Connected);

    /// <summary>Mints a one-time invite whose link is under <paramref name="baseUri"/> (the node's https address).</summary>
    public PhoneInvite Invite(string name, TimeSpan life, string invitedBy, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (life <= TimeSpan.Zero || life > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(life), "an invite lasts between a minute and an hour");
        var ticket = Base32(RandomNumberGenerator.GetBytes(20));
        var now = _options.Time.GetUtcNow();
        var expires = now + life;
        lock (_gate)
        {
            foreach (var stale in _tickets.Where(t => t.Value.Expires < now - TimeSpan.FromHours(1)).Select(t => t.Key).ToList())
                _tickets.Remove(stale);
            _tickets[Digest(ticket)] = new Ticket(name.Trim(), expires, invitedBy);
        }
        var url = new UriBuilder(baseUri) { Path = "/scan/", Query = "", Fragment = "t=" + ticket }.Uri.AbsoluteUri;
        return new PhoneInvite(url, ticket, expires, name.Trim(), QrMatrix.Encode(url));
    }

    /// <summary>Forgets a phone (or every phone: "ALL"): its key no longer opens anything and its socket closes. Answers the ids.</summary>
    public IReadOnlyList<string> Forget(string phoneIdOrAll)
    {
        ArgumentNullException.ThrowIfNull(phoneIdOrAll);
        var ids = phoneIdOrAll.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? _keys.All.Select(p => p.Id).ToList()
            : _keys.Find(phoneIdOrAll) is { } one ? [one.Id] : [];
        foreach (var id in ids)
        {
            _keys.Remove(id);
            if (_sessions.TryRemove(id, out var s))
                s.Disconnect("was forgotten: its key no longer opens anything");
        }
        if (ids.Count > 0)
            Changed?.Invoke();
        return ids;
    }

    /// <summary>Every phone this node knows, connected or not, in the order they first joined.</summary>
    public IReadOnlyList<PhoneFacts> Phones()
    {
        var list = new List<PhoneFacts>();
        foreach (var p in _keys.All)
        {
            if (_sessions.TryGetValue(p.Id, out var s) && s.Connected)
            {
                var f = s.Facts();
                var words = f.Camera.Exposure switch
                {
                    "locked" => "Ready: exposure locked" + (f.Camera.Focus == "locked" ? ", focus locked" : ""),
                    "steady" => "Ready: brightness kept steady (this camera cannot lock its exposure from a web page)",
                    _ => "Ready",
                };
                if (f.Motion == "moved")
                    words = "Moved during the last view — set it down and keep it still";
                list.Add(new PhoneFacts(p.Id, p.Name, p.Rig, s.Profile.Platform, s.Profile.Model, true, s.Address, "ws", f.RttMs, f.OffsetMs,
                    f.UncertaintyMs, f.UploadMBps, f.Camera, f.Motion, f.Visible, f.LastSeenUnixMs, words));
            }
            else
            {
                var why = _sessions.TryGetValue(p.Id, out var gone) ? gone.Gone : "is not connected — open the invite on the phone";
                list.Add(new PhoneFacts(p.Id, p.Name, p.Rig, gone?.Profile.Platform ?? "", gone?.Profile.Model ?? "", false, "", "", 0, 0, 0, 0,
                    gone is null ? null : gone.Facts().Camera, "", false, p.LastSeenUnixMs, $"{p.Name} {why}"));
            }
        }
        return list;
    }

    /// <summary>The connected session of a phone, or null.</summary>
    public IPhoneSession? Session(string phoneId) =>
        _sessions.TryGetValue(phoneId, out var s) && s.Connected ? s : null;

    /// <summary>The phone a camera rig is, by the rig's source (its phone id).</summary>
    public PhoneRecord? Find(string phoneId) => _keys.Find(phoneId);

    internal static string Digest(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static string NewId() => "ph" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public bool IsLockedOut(string address)
    {
        lock (_gate)
            return _wrong.TryGetValue(address, out var q) && Prune(q) >= _options.WrongPerMinute;
    }

    private void NoteWrong(string address)
    {
        lock (_gate)
        {
            if (!_wrong.TryGetValue(address, out var q))
                _wrong[address] = q = new Queue<long>();
            Prune(q);
            q.Enqueue(_options.Time.GetTimestamp());
            if (_wrong.Count > 4096)
            {
                foreach (var key in _wrong.Where(p => Prune(p.Value) == 0).Select(p => p.Key).ToList())
                    _wrong.Remove(key);
            }
        }
    }

    private int Prune(Queue<long> q)
    {
        while (q.Count > 0 && _options.Time.GetElapsedTime(q.Peek()) > TimeSpan.FromMinutes(1))
            q.Dequeue();
        return q.Count;
    }

    /// <summary>A socket on /scan/ws: the subprotocol, then the hello within its time — ticket or key — then the session.</summary>
    internal async Task AcceptAsync(HttpContext context)
    {
        var address = NetAddress.Text(context.Connection.RemoteIpAddress);
        if (!context.WebSockets.IsWebSocketRequest
            || !context.WebSockets.WebSocketRequestedProtocols.Contains(PhoneProtocol.SubProtocol, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"the phone's socket speaks {PhoneProtocol.SubProtocol}", context.RequestAborted);
            return;
        }
        if (IsLockedOut(address))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("too many wrong invites from this address — wait a minute", context.RequestAborted);
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(PhoneProtocol.SubProtocol);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        PhoneSession? session = null;
        try
        {
            session = await HelloAsync(socket, address, stop.Token);
            if (session is null)
                return;
            session.Changed += OnChanged;
            _log.LogInformation("Phone {Phone} ({Name}) joined from {Address} with a {Profile} camera.", session.PhoneId, session.Name, address,
                session.Profile.Exposure);
            Changed?.Invoke();
            await session.RunAsync(stop.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            _log.LogDebug(ex, "A phone's socket ended before its hello.");
        }
        finally
        {
            if (session is not null)
            {
                session.End("is not connected — open the invite on the phone");
                session.Changed -= OnChanged;
                var phone = _keys.Find(session.PhoneId);
                if (phone is not null)
                    _keys.Put(phone with { LastSeenUnixMs = _options.Time.GetUtcNow().ToUnixTimeMilliseconds() });
                Changed?.Invoke();
                session.Dispose();
            }
        }
    }

    private void OnChanged() => Changed?.Invoke();

    /// <summary>Reads the hello and admits it, or closes the socket with the reason (1008) and answers null.</summary>
    private async Task<PhoneSession?> HelloAsync(WebSocket socket, string address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HelloTimeout);
        var buffer = new byte[_options.MaxTextBytes];
        var length = 0;
        ValueWebSocketReceiveResult r;
        try
        {
            do
            {
                if (length == buffer.Length)
                {
                    await RefuseAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too big", ct);
                    return null;
                }
                r = await socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await RefuseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return null;
                }
                length += r.Count;
            }
            while (!r.EndOfMessage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "no hello", ct);
            return null;
        }
        if (r.MessageType != WebSocketMessageType.Text)
        {
            await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "hello first", ct);
            return null;
        }
        Hello hello;
        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            var t = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("t", out var tv) ? tv.GetString() : null;
            if (t is null || !PhoneProtocol.FromPhone.Contains(t))
            {
                await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "unknown message", ct);
                return null;
            }
            if (t != "hello")
            {
                await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "hello first", ct);
                return null;
            }
            hello = Hello.Read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "a hello without its fields", ct);
            return null;
        }

        if (!_options.Enabled())
        {
            await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "phones are switched off on this node", ct);
            return null;
        }
        if (IsLockedOut(address))
        {
            await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "too many wrong invites from this address — wait a minute", ct);
            return null;
        }
        PhoneRecord? phone;
        string? key = null;
        if (hello.Ticket.Length > 0)
        {
            Ticket? ticket;
            var expired = false;
            lock (_gate)
            {
                // Used once, whether or not it is still good.
                if (_tickets.Remove(Digest(hello.Ticket), out ticket))
                    expired = ticket.Expires < _options.Time.GetUtcNow();
            }
            if (ticket is null)
            {
                NoteWrong(address);
                await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "ERR this invite is not known or was used — ask for a new one", ct);
                return null;
            }
            if (expired)
            {
                await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "ERR ticket expired — ask for a new invite", ct);
                return null;
            }
            key = NewKey();
            var name = ticket.Name.Length > 0 ? ticket.Name : hello.Name.Length > 0 ? hello.Name : "Phone";
            var now = _options.Time.GetUtcNow().ToUnixTimeMilliseconds();
            phone = new PhoneRecord(NewId(), name, PhoneKeys.Digest(key), now, now, "");
            _keys.Put(phone);
        }
        else
        {
            phone = hello.Key.Length > 0 ? _keys.FindByKey(hello.Key) : null;
            if (phone is null)
            {
                NoteWrong(address);
                await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation, "ERR this phone's key is not known here — ask for a new invite", ct);
                return null;
            }
        }
        if (Connected >= _options.MaxConnected && !_sessions.ContainsKey(phone.Id))
        {
            await RefuseAsync(socket, WebSocketCloseStatus.PolicyViolation,
                string.Create(CultureInfo.InvariantCulture, $"{_options.MaxConnected} phones are connected already"), ct);
            return null;
        }

        if (Join is { } join)
            phone = phone with { Rig = await join(new PhoneJoined(phone.Id, phone.Name, hello.Profile, phone.Rig, key is not null), ct) };
        phone = phone with { LastSeenUnixMs = _options.Time.GetUtcNow().ToUnixTimeMilliseconds() };
        _keys.Put(phone);
        var session = new PhoneSession(socket, phone, hello.Profile, hello.Camera, address, _options, _log);
        if (_sessions.TryGetValue(phone.Id, out var older))
            older.Disconnect("was replaced: the phone joined again");
        _sessions[phone.Id] = session;
        var welcome = phone;
        await session.SendAsync(w =>
        {
            w.WriteString("t", "welcome");
            w.WriteStartObject("phone");
            w.WriteString("id", welcome.Id);
            w.WriteString("name", welcome.Name);
            w.WriteEndObject();
            if (key is not null)
                w.WriteString("key", key);
            w.WriteStartObject("node");
            w.WriteString("id", _options.NodeId);
            w.WriteString("name", _options.NodeName);
            w.WriteEndObject();
            w.WriteString("camera", welcome.Rig);
            w.WriteStartObject("limits");
            w.WriteNumber("maxInFlight", _options.MaxInFlight);
            w.WriteNumber("maxBinary", _options.MaxBinaryBytes);
            w.WriteEndObject();
        }, ct);
        return session;
    }

    private static async Task RefuseAsync(WebSocket socket, WebSocketCloseStatus status, string words, CancellationToken ct)
    {
        using var bye = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bye.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            // A close reason is at most 123 bytes.
            var reason = words.Length > 100 ? words[..100] : words;
            await socket.CloseOutputAsync(status, reason, bye.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            // The phone went first: the refusal has nobody to reach.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        foreach (var s in _sessions.Values)
            s.Disconnect("was disconnected: the node is stopping");
        _stop.Dispose();
    }

    /// <summary>What a hello says: the ticket or the key, the phone's name, and its camera.</summary>
    private sealed record Hello(string Ticket, string Key, string Name, PhoneProfile Profile, PhoneCameraFacts Camera)
    {
        public static Hello Read(JsonElement m)
        {
            if (!m.TryGetProperty("v", out var v) || v.GetInt32() != 1)
                throw new FormatException("hello v1");
            var ticket = Text(m, "ticket");
            var key = Text(m, "key");
            if (ticket.Length == 0 && key.Length == 0)
                throw new FormatException("a ticket or a key");
            var name = m.TryGetProperty("phone", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "name") : "";
            var ua = m.TryGetProperty("ua", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            var camera = m.GetProperty("camera");
            var caps = camera.TryGetProperty("caps", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var locks = caps.ValueKind == JsonValueKind.Object && caps.TryGetProperty("exposure", out var ex) && ex.ValueKind == JsonValueKind.Array
                && ex.EnumerateArray().Any(e => e.GetString() == "manual")
                && caps.TryGetProperty("exposureTime", out var et) && et.ValueKind == JsonValueKind.Array;
            var platform = ua.ValueKind == JsonValueKind.Object ? Text(ua, "platform") : "";
            if (platform is not ("android-chrome" or "ios-safari"))
                platform = "other";
            var width = camera.GetProperty("width").GetInt32();
            var height = camera.GetProperty("height").GetInt32();
            if (width is < 16 or > 16384 || height is < 16 or > 16384)
                throw new FormatException("a camera raster");
            var label = Text(camera, "label");
            var fps = camera.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
            var profile = new PhoneProfile(platform, ua.ValueKind == JsonValueKind.Object ? Text(ua, "model") : "", label, width, height,
                locks ? PhoneProtocol.Lock : PhoneProtocol.Steady);
            return new Hello(ticket, key, name, profile, new PhoneCameraFacts(label, width, height, fps, "auto", 0, 0, "auto", "auto"));
        }

        private static string Text(JsonElement m, string name)
        {
            var s = m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return s.Length <= 80 ? s : s[..80];
        }
    }
}
