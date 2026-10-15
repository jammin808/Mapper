using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Core.Time;

namespace PlayMap.Net.Phone;

/// <summary>A phone that stopped answering (its socket closed, it said bye, it was forgotten): every wait on it ends with these
/// words.</summary>
public sealed class PhoneGoneException(string message) : InvalidOperationException(message);

/// <summary>A connected phone as a scan drives it, lock-step: prepare the exposure, place the tag sites, cue each pattern and wait
/// for the phone to report it seen (or stuck), then take the picture that seen named. Every wait ends when the phone goes, with its
/// words.</summary>
public interface IPhoneSession
{
    string PhoneId { get; }

    string Name { get; }

    PhoneProfile Profile { get; }

    bool Connected { get; }

    /// <summary>Why the phone is not connected ("" while it is).</summary>
    string Gone { get; }

    /// <summary>Whether a clock exchange has given the page clock's offset yet.</summary>
    bool HasClock { get; }

    /// <summary>A page instant (ms) on the node's monotonic clock (ns), through the clock exchange (unchanged before the first).</summary>
    long ToNodeNs(double pageMs);

    Task<PhonePrepared> PrepareAsync(PhonePrepare request, CancellationToken ct);

    Task SetSitesAsync(PhoneSites sites, CancellationToken ct);

    /// <summary>Puts a cue to the phone (its id is the session's own) and completes with the phone's seen or stuck.</summary>
    Task<PhoneSeen> CueAsync(PhoneCue cue, CancellationToken ct);

    /// <summary>The picture of upload <paramref name="seq"/>, once it has arrived whole.</summary>
    Task<PhonePicture> PictureAsync(long seq, CancellationToken ct);

    /// <summary>Words for the phone's screen ("info", "warn").</summary>
    Task SayAsync(string level, string words, CancellationToken ct);

    /// <summary>How many times the phone's motion sensor has said it moved (a move reported, or a movement starting) since it joined.</summary>
    int Moves { get; }
}

/// <summary>The facts STATE shows of a phone's camera and link.</summary>
public sealed record PhoneCameraFacts(string Label, int Width, int Height, double Fps, string Exposure, double ExposureMs, double Iso, string Focus,
    string WhiteBalance);

/// <summary>
/// One phone's socket on <c>/scan/ws</c> (<c>playmap.camera.v1</c>) after its hello was admitted. One receive loop: text messages up
/// to the hub's limit and only of the types a phone sends (anything else closes with 1008), binary PMF1 frames up to theirs (larger
/// closes with 1009; a frame that is not whole closes with 1007). One sender at a time. The clock exchange: each ping carries the
/// page's send time and its previous exchange's four instants; the node keeps the least-delayed of the last eight, and its offset
/// turns page instants into node ones. A picture is kept only when a seen for a cue still waited on named it, and only at the
/// camera's own raster; anything else is acknowledged and dropped. A phone that sends nothing (not even its ping each second) for
/// the quiet limit is ended with words.
/// </summary>
internal sealed class PhoneSession : IPhoneSession, IDisposable
{
    private readonly WebSocket _socket;
    private readonly PhoneHubOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Lock _gate = new();
    private readonly ClockOffsetEstimator _clock = new();
    private readonly Dictionary<long, TaskCompletionSource<PhoneSeen>> _cues = [];
    private readonly Dictionary<long, PhonePicture> _pictures = [];
    private readonly Dictionary<long, TaskCompletionSource<PhonePicture>> _pictureWaits = [];
    private readonly HashSet<long> _expected = [];
    private TaskCompletionSource<PhonePrepared>? _prepare;
    private long _nextCue;
    private volatile string _gone = "";
    private double _uploadMBps;
    private PhoneCameraFacts _camera;
    private string _motion = "still";
    private bool _visible = true;
    private long _lastSeenUnixMs;
    private int _moves;

    public PhoneSession(WebSocket socket, PhoneRecord phone, PhoneProfile profile, PhoneCameraFacts camera, string address,
        PhoneHubOptions options, ILogger log)
    {
        _socket = socket;
        PhoneId = phone.Id;
        Name = phone.Name;
        Profile = profile;
        _camera = camera;
        Address = address;
        _options = options;
        _log = log;
        _lastSeenUnixMs = options.Time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    public string PhoneId { get; }

    public string Name { get; }

    public PhoneProfile Profile { get; }

    public string Address { get; }

    public bool Connected => _gone.Length == 0;

    public string Gone => _gone;

    public int Moves => Volatile.Read(ref _moves);

    /// <summary>Raised when a fact STATE shows moved (on the receive loop).</summary>
    public event Action? Changed;

    public bool HasClock
    {
        get
        {
            lock (_gate)
                return _clock.HasEstimate;
        }
    }

    public long ToNodeNs(double pageMs)
    {
        double offsetMs;
        lock (_gate)
            offsetMs = _clock.HasEstimate ? _clock.Offset / 1000.0 : 0;
        return (long)Math.Round((pageMs + offsetMs) * 1e6);
    }

    /// <summary>The link and camera facts for STATE.</summary>
    public (double RttMs, double OffsetMs, double UncertaintyMs, double UploadMBps, PhoneCameraFacts Camera, string Motion, bool Visible, long LastSeenUnixMs) Facts()
    {
        lock (_gate)
        {
            var rtt = _clock.HasEstimate ? _clock.RoundTrip / 1000.0 : 0;
            return (Math.Round(rtt, 1), Math.Round(_clock.HasEstimate ? _clock.Offset / 1000.0 : 0, 1), Math.Round(rtt / 2, 1),
                Math.Round(_uploadMBps, 2), _camera, _motion, _visible, _lastSeenUnixMs);
        }
    }

    public async Task<PhonePrepared> PrepareAsync(PhonePrepare request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tcs = new TaskCompletionSource<PhonePrepared>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfGone();
            _prepare?.TrySetCanceled(CancellationToken.None);
            _prepare = tcs;
        }
        await SendAsync(w =>
        {
            w.WriteString("t", "prepare");
            w.WriteString("profile", request.Profile);
            w.WriteNumber("displayHz", request.DisplayHz);
            w.WriteNumber("agreeTolerance", request.AgreeTolerance);
            w.WriteNumber("stableFrames", request.StableFrames);
        }, ct).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task SetSitesAsync(PhoneSites sites, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sites);
        return SendAsync(w => WriteSites(w, sites), ct);
    }

    /// <summary>The <c>sites</c> message's fields. The cell centres go unrounded: the page samples the very pixels the node's own tag
    /// check does.</summary>
    internal static void WriteSites(Utf8JsonWriter w, PhoneSites sites)
    {
        w.WriteString("t", "sites");
        w.WriteString("projector", sites.ProjectorId);
        w.WriteNumber("minSites", sites.MinSites);
        w.WriteStartArray("lit");
        w.WriteNumberValue(sites.Lit.X);
        w.WriteNumberValue(sites.Lit.Y);
        w.WriteNumberValue(sites.Lit.Width);
        w.WriteNumberValue(sites.Lit.Height);
        w.WriteEndArray();
        w.WriteStartArray("sites");
        foreach (var s in sites.Sites)
        {
            w.WriteStartObject();
            w.WriteNumber("id", s.Id);
            w.WriteStartArray("cells");
            foreach (var c in s.Cells)
            {
                w.WriteStartArray();
                w.WriteNumberValue(c.X);
                w.WriteNumberValue(c.Y);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("rows");
            w.WriteNumberValue(s.RowTop);
            w.WriteNumberValue(s.RowBottom);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public async Task<PhoneSeen> CueAsync(PhoneCue cue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var tcs = new TaskCompletionSource<PhoneSeen>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            ThrowIfGone();
            id = ++_nextCue;
            _cues[id] = tcs;
        }
        try
        {
            await SendAsync(w =>
            {
                w.WriteString("t", "cue");
                w.WriteNumber("id", id);
                w.WriteString("kind", cue.Kind);
                w.WriteString("projector", cue.ProjectorId);
                w.WriteNumber("i", cue.Index);
                w.WriteNumber("tag", cue.Tag);
                w.WriteNumber("at", Math.Round(cue.PresentNs / 1e6, 3));
                w.WriteNumber("earliest", Math.Round(cue.EarliestNs / 1e6, 3));
                w.WriteNumber("retake", cue.Retake);
                w.WriteNumber("minChange", cue.MinChange);
            }, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _cues.Remove(id);
        }
    }

    public async Task<PhonePicture> PictureAsync(long seq, CancellationToken ct)
    {
        TaskCompletionSource<PhonePicture> tcs;
        lock (_gate)
        {
            if (_pictures.Remove(seq, out var arrived))
            {
                _expected.Remove(seq);
                return arrived;
            }
            ThrowIfGone();
            if (!_pictureWaits.TryGetValue(seq, out tcs!))
                _pictureWaits[seq] = tcs = new TaskCompletionSource<PhonePicture>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Taken, or no longer waited for: a picture arriving later is dropped.
            lock (_gate)
            {
                _pictureWaits.Remove(seq);
                _expected.Remove(seq);
            }
        }
    }

    public Task SayAsync(string level, string words, CancellationToken ct) =>
        SendAsync(w =>
        {
            w.WriteString("t", "say");
            w.WriteString("level", level);
            w.WriteString("words", words);
        }, ct);

    private void ThrowIfGone()
    {
        if (_gone.Length > 0)
            throw new PhoneGoneException($"{Name} {_gone}");
    }

    /// <summary>Sends one JSON message; one sender at a time.</summary>
    public async Task SendAsync(Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        if (_gone.Length > 0)
            throw new PhoneGoneException($"{Name} {_gone}");
        try
        {
            await _send.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new PhoneGoneException($"{Name} {_gone}");
        }
        try
        {
            if (_gone.Length > 0)
                throw new PhoneGoneException($"{Name} {_gone}");
            await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new PhoneGoneException($"{Name} stopped answering: {ex.Message}");
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>The receive loop, after the hello: runs until the socket closes, then ends every wait with the reason (the node
    /// stopping is told from the phone's connection dropping by <paramref name="nodeStopping"/>).</summary>
    public async Task RunAsync(CancellationToken ct, CancellationToken nodeStopping)
    {
        string why;
        try
        {
            why = await ReceiveLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            why = nodeStopping.IsCancellationRequested ? "was disconnected: the node is stopping" : "stopped sending: its connection dropped";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            why = "stopped sending: its connection dropped (" + ex.Message + ")";
            _log.LogDebug(ex, "A phone's socket dropped.");
        }
        // Words that may carry a phone's own text are kept short: they reach STATE and the log.
        why = Clip(why);
        _log.LogInformation("Phone {Phone} ({Name}) {Why}.", PhoneId, Name, why);
        End(why);
    }

    /// <summary>Ends the session with words: every wait on it fails with them and the socket is closed.</summary>
    public void End(string why)
    {
        List<TaskCompletionSource<PhoneSeen>> cues;
        List<TaskCompletionSource<PhonePicture>> pictures;
        TaskCompletionSource<PhonePrepared>? prepare;
        lock (_gate)
        {
            if (_gone.Length > 0)
                return;
            _gone = why;
            cues = [.. _cues.Values];
            pictures = [.. _pictureWaits.Values];
            prepare = _prepare;
            _cues.Clear();
            _pictureWaits.Clear();
            _pictures.Clear();
            _expected.Clear();
        }
        var gone = new PhoneGoneException($"{Name} {why}");
        foreach (var c in cues)
            c.TrySetException(gone);
        foreach (var p in pictures)
            p.TrySetException(gone);
        prepare?.TrySetException(gone);
        Changed?.Invoke();
    }

    /// <summary>Ends the session from the node's side (forgotten, replaced, the node stopping) and closes its socket.</summary>
    public void Disconnect(string why)
    {
        End(why);
        _ = CloseAsync(WebSocketCloseStatus.PolicyViolation, why, CancellationToken.None);
    }

    private async Task<string> ReceiveLoopAsync(CancellationToken ct)
    {
        var limit = Math.Max(_options.MaxBinaryBytes, _options.MaxTextBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var quietWords = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"stopped sending: nothing for {_options.QuietTimeout.TotalSeconds:0.#} s");
        using var receive = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Quiet for too long: the phone is told why (a close with the words), and its socket let go if it never answers that.
        using var quiet = _options.Time.CreateTimer(_ =>
        {
            Disconnect(quietWords);
            try
            {
                receive.CancelAfter(TimeSpan.FromSeconds(2));
            }
            catch (ObjectDisposedException)
            {
                // The loop ended first.
            }
        }, null, _options.QuietTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            var message = new ArrayBufferWriter<byte>(64 * 1024);
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.ResetWrittenCount();
                WebSocketReceiveResult r;
                var started = _options.MonoNs();
                do
                {
                    try
                    {
                        r = await _socket.ReceiveAsync(buffer, receive.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return quietWords;
                    }
                    quiet.Change(_options.QuietTimeout, Timeout.InfiniteTimeSpan);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        // The close is answered, so the phone's side ends cleanly too; its own words, if it gave any, say why.
                        var said = _socket.CloseStatusDescription;
                        await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                        return string.IsNullOrWhiteSpace(said) || said == "bye" ? "said goodbye (its page closed)" : "said goodbye: " + Clip(said);
                    }
                    var max = r.MessageType == WebSocketMessageType.Text ? _options.MaxTextBytes : _options.MaxBinaryBytes;
                    if (message.WrittenCount + r.Count > Math.Min(max, limit))
                        return await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct).ConfigureAwait(false);
                    message.Write(buffer.AsSpan(0, r.Count));
                }
                while (!r.EndOfMessage);
                _lastSeenUnixMs = _options.Time.GetUtcNow().ToUnixTimeMilliseconds();
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    var elapsed = Math.Max(1, _options.MonoNs() - started);
                    var refused = OnFrame(message.WrittenSpan, elapsed);
                    if (refused is not null)
                        return await CloseAsync(refused.Value.Status, refused.Value.Words, ct).ConfigureAwait(false);
                    continue;
                }
                var (closeWith, words) = await OnTextAsync(message.WrittenMemory, ct).ConfigureAwait(false);
                if (closeWith is { } status)
                    return await CloseAsync(status, words, ct).ConfigureAwait(false);
            }
            return "stopped sending: its connection closed";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> CloseAsync(WebSocketCloseStatus status, string words, CancellationToken ct)
    {
        try
        {
            using var bye = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bye.CancelAfter(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync(status, CloseReason(words), bye.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "A phone's socket closed before its close frame went.");
        }
        return status == WebSocketCloseStatus.NormalClosure ? "said goodbye" : "was disconnected: " + words;
    }

    /// <summary>A close reason fits in 123 UTF-8 bytes: longer words are cut on a character boundary.</summary>
    internal static string CloseReason(string words)
    {
        const int Max = 123;
        if (Encoding.UTF8.GetByteCount(words) <= Max)
            return words;
        var n = Math.Min(words.Length, Max - 3);
        while (n > 0 && (Encoding.UTF8.GetByteCount(words.AsSpan(0, n)) > Max - 3 || char.IsHighSurrogate(words[n - 1])))
            n--;
        return words[..n] + "…";
    }

    private (WebSocketCloseStatus Status, string Words)? OnFrame(ReadOnlySpan<byte> frame, long elapsedNs)
    {
        if (!Pmf1.TryParse(frame, out var header, out var payload, out var words))
            return (WebSocketCloseStatus.InvalidPayloadData, "bad frame: " + words);
        if (header.Kind is not ("pattern" or "locate" or "still"))
            return (WebSocketCloseStatus.PolicyViolation, "a picture of a kind that was not asked for");
        if (header.Width != Profile.Width || header.Height != Profile.Height)
            return (WebSocketCloseStatus.InvalidPayloadData, "bad frame: a picture whose size is not the camera's");
        bool wanted;
        lock (_gate)
            wanted = _expected.Contains(header.Seq);
        if (!wanted)
        {
            // A picture of a cue nobody waits for any more (cancelled, timed out, shown again): acknowledged and dropped unread —
            // nothing is inflated for it.
            Ack(header.Seq);
            return null;
        }
        if (!Pmf1.TryDecode(header, payload, out var pixels, out words))
            return (WebSocketCloseStatus.InvalidPayloadData, "bad frame: " + words);
        var gray = new byte[header.Width * header.Height];
        var bpp = header.BytesPerPixel;
        for (var y = 0; y < header.Roi.Height; y++)
        {
            var dst = (header.Roi.Y + y) * header.Width + header.Roi.X;
            var src = y * header.Roi.Width * bpp;
            for (var x = 0; x < header.Roi.Width; x++)
                gray[dst + x] = bpp == 1 ? pixels[src + x] : pixels[src + 2 * x + 1]; // gray16 little-endian: its high byte
        }
        var picture = new PhonePicture(header.Seq, header.Kind, header.Width, header.Height, gray, ToNodeNs(header.Capture));
        TaskCompletionSource<PhonePicture>? waiter;
        lock (_gate)
        {
            _uploadMBps = 0.7 * _uploadMBps + 0.3 * (frame.Length / (elapsedNs / 1e9) / 1e6);
            if (_pictureWaits.Remove(header.Seq, out waiter))
            {
                _expected.Remove(header.Seq);
            }
            else if (_expected.Contains(header.Seq))
            {
                // Its seen came first: kept until the scan asks for it (the in-flight limit bounds how many).
                if (_pictures.Count >= 2 * _options.MaxInFlight)
                    return (WebSocketCloseStatus.PolicyViolation, "too many pictures on the way at once");
                _pictures[header.Seq] = picture;
            }
        }
        waiter?.TrySetResult(picture);
        Ack(header.Seq);
        return null;
    }

    private void Ack(long seq) =>
        _ = SendAsync(w =>
        {
            w.WriteString("t", "ack");
            w.WriteNumber("seq", seq);
        }, CancellationToken.None).ContinueWith(t => _log.LogDebug(t.Exception, "An ack did not go."), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task<(WebSocketCloseStatus? Close, string Words)> OnTextAsync(ReadOnlyMemory<byte> text, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return (WebSocketCloseStatus.PolicyViolation, "a message that is not JSON");
        }
        using (doc)
        {
            var m = doc.RootElement;
            var t = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : "";
            if (!PhoneProtocol.FromPhone.Contains(t))
                return (WebSocketCloseStatus.PolicyViolation, "unknown message");
            try
            {
                switch (t)
                {
                    case "hello":
                        return (WebSocketCloseStatus.PolicyViolation, "hello twice");
                    case "bye":
                        return (WebSocketCloseStatus.NormalClosure, "bye");
                    case "ping":
                        await OnPingAsync(m, ct).ConfigureAwait(false);
                        break;
                    case "state":
                        OnState(m);
                        break;
                    case "prepared":
                        OnPrepared(m);
                        break;
                    case "seen" or "stuck":
                        OnSeen(t, m);
                        break;
                    case "moved":
                        lock (_gate)
                            _motion = "moved";
                        Interlocked.Increment(ref _moves);
                        Changed?.Invoke();
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return (WebSocketCloseStatus.PolicyViolation, $"a {t} without its fields");
            }
        }
        return (null, "");
    }

    private double NowMs => _options.MonoNs() / 1e6;

    private async Task OnPingAsync(JsonElement m, CancellationToken ct)
    {
        var t2 = NowMs;
        var t1 = m.GetProperty("t1").GetDouble();
        if (m.TryGetProperty("prev", out var prev) && prev.ValueKind == JsonValueKind.Object)
        {
            var sample = new ClockSample(Us(prev.GetProperty("t1").GetDouble()), Us(prev.GetProperty("t2").GetDouble()),
                Us(prev.GetProperty("t3").GetDouble()), Us(prev.GetProperty("t4").GetDouble()));
            bool added;
            lock (_gate)
                added = _clock.Add(sample);
            if (added)
                Changed?.Invoke();
        }
        await SendAsync(w =>
        {
            w.WriteString("t", "pong");
            w.WriteNumber("t1", t1);
            w.WriteNumber("t2", Math.Round(t2, 3));
            w.WriteNumber("t3", Math.Round(NowMs, 3));
        }, ct).ConfigureAwait(false);
        var (_, offset, uncertainty, _, _, _, _, _) = Facts();
        if (_clock.HasEstimate)
        {
            await SendAsync(w =>
            {
                w.WriteString("t", "clock");
                w.WriteNumber("offsetMs", offset);
                w.WriteNumber("uncertaintyMs", uncertainty);
            }, ct).ConfigureAwait(false);
        }
    }

    private static long Us(double ms) => (long)Math.Round(ms * 1000);

    private void OnState(JsonElement m)
    {
        lock (_gate)
        {
            if (m.TryGetProperty("exposure", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                _camera = _camera with
                {
                    Exposure = Str(e, "mode", _camera.Exposure),
                    ExposureMs = Num(e, "timeMs", _camera.ExposureMs),
                    Iso = Num(e, "iso", _camera.Iso),
                };
            }
            if (m.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.Object)
                _camera = _camera with { Focus = Str(f, "mode", _camera.Focus) };
            if (m.TryGetProperty("wb", out var wb) && wb.ValueKind == JsonValueKind.Object)
                _camera = _camera with { WhiteBalance = Str(wb, "mode", _camera.WhiteBalance) };
            if (m.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number)
                _camera = _camera with { Fps = fps.GetDouble() };
            if (m.TryGetProperty("motion", out var motion) && motion.ValueKind == JsonValueKind.String)
            {
                var moving = motion.GetString() == "moving";
                // Every start of a movement counts as a move, cue or no cue: a view counts those after it began.
                if (moving && _motion != "moving")
                    Interlocked.Increment(ref _moves);
                _motion = moving ? "moving" : "still";
            }
            if (m.TryGetProperty("visible", out var visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _visible = visible.GetBoolean();
        }
        Changed?.Invoke();
    }

    private void OnPrepared(JsonElement m)
    {
        var prepared = new PhonePrepared(Str(m, "exposure", "steady"), Num(m, "exposureMs", 0), Num(m, "iso", 0), Str(m, "focus", "auto"),
            Str(m, "whiteBalance", "auto"), Str(m, "words", ""));
        TaskCompletionSource<PhonePrepared>? tcs;
        lock (_gate)
        {
            tcs = _prepare;
            _prepare = null;
            _camera = _camera with
            {
                Exposure = prepared.Exposure,
                ExposureMs = prepared.ExposureMs,
                Iso = prepared.Iso,
                Focus = prepared.Focus,
                WhiteBalance = prepared.WhiteBalance,
            };
        }
        tcs?.TrySetResult(prepared);
        Changed?.Invoke();
    }

    private void OnSeen(string t, JsonElement m)
    {
        var cue = m.GetProperty("cue").GetInt64();
        var seen = new PhoneSeen(t, cue, t == "seen" ? m.GetProperty("seq").GetInt64() : -1, (int)Num(m, "tag", 0), Num(m, "capture", double.NaN),
            (int)Num(m, "sites", 0), (int)Num(m, "margin", 0), Num(m, "settleDiff", 0), (int)Num(m, "rolling", 0), Str(m, "why", ""));
        TaskCompletionSource<PhoneSeen>? tcs;
        lock (_gate)
        {
            // Only a cue still waited on names a picture worth keeping.
            if (_cues.Remove(cue, out tcs) && t == "seen")
                _expected.Add(seen.Seq);
        }
        tcs?.TrySetResult(seen);
    }

    private static string Str(JsonElement m, string name, string fallback) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString() ?? fallback) : fallback;

    /// <summary>A phone's own words are kept short: they reach STATE.</summary>
    private static string Clip(string s) => s.Length <= 120 ? s : s[..120];

    private static double Num(JsonElement m, string name, double fallback) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : fallback;

    /// <summary>Ends the session; the send gate is let go only when no send holds or waits on it (one in flight lets go itself).</summary>
    public void Dispose()
    {
        End("was disconnected");
        if (_send.Wait(0))
            _send.Dispose();
    }
}
