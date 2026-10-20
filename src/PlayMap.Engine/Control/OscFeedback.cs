using System.Collections.Immutable;
using System.Net;
using PlayMap.Core.Bus;
using PlayMap.Core.Control;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Control;

/// <summary>Value and lamp feedback over OSC: every binding with a route sends its target's value (a fader follows the desk) or its
/// switch (a lamp) to the OSC clients that drive it — each client's reply address (the endpoint its messages come from, heard in
/// the last 10 minutes), the surface's own feedbackTo, or the binding's own words — and to the node's feedback clients
/// (<c>CONTROL FEEDBACK ADD</c>). One pass at most <see cref="ControlSettings.FeedbackHz"/> times a second, and only when STATE,
/// the clients or the settings moved: each (client, address) slot sends only what differs from what the client shows (dedupe), a
/// value never within 300 ms of input from that client on that address (the echo guard: a fader in the hand is not fought), and a
/// client that sends back what it was sent within 50 ms is an echo, dropped. Several messages to one client ride in bundles of at most 1400
/// bytes. Single-threaded: only the control loop calls it (the counts and the client list are read by STATE).</summary>
internal sealed class OscFeedback
{
    public const long EchoGuardNs = 300_000_000;

    public const long EchoWindowNs = 50_000_000;

    public const long ClientIdleNs = 600_000_000_000;

    public const int MaxClients = 32;

    public const int MaxBundleBytes = 1400;

    private readonly Dictionary<(IPEndPoint To, string Address), Slot> _slots = [];
    private readonly Dictionary<IPEndPoint, long> _clients = [];
    private readonly Dictionary<BindingRuntime, FeedbackRoute> _routes = [];
    private readonly Dictionary<(string Id, string Path), (object? Entity, double? Value)> _reads = [];
    private readonly Lock _clientsGate = new();
    private long _lastPassNs = long.MinValue;
    private long _lastKey = long.MinValue;
    private ControlSettings? _lastSettings;
    private object? _lastTable;
    private bool _pending = true;
    private long _sent;
    private long _echoes;

    /// <summary>Messages sent, each counted before its datagram goes.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    public long Echoes => Interlocked.Read(ref _echoes);

    /// <summary>How many clients are fed because they were heard (the node's own feedback clients are in the settings).</summary>
    public int ClientCount
    {
        get
        {
            lock (_clientsGate)
                return _clients.Count;
        }
    }

    /// <summary>A control message from a client: the client is (re)leased, the slot remembers what the client now shows and when it
    /// moved (the echo guard). Answers false when the message is the client echoing what it was just sent (it is dropped).</summary>
    public bool Heard(IPEndPoint from, OscMessage m, long nowNs)
    {
        var key = (from, m.Address);
        if (_slots.TryGetValue(key, out var slot) && slot.Shows is { } shows && slot.SentNs > slot.InputNs && nowNs - slot.SentNs <= EchoWindowNs && Same(shows, [.. m.Args]))
        {
            Interlocked.Increment(ref _echoes);
            return false;
        }
        lock (_clientsGate)
        {
            if (!_clients.ContainsKey(from))
            {
                _pending = true;
                if (_clients.Count >= MaxClients)
                    _clients.Remove(_clients.MinBy(kv => kv.Value).Key);
            }
            _clients[from] = nowNs;
        }
        if (slot is null)
        {
            slot = new Slot();
            _slots[key] = slot;
        }
        slot.Shows = [.. m.Args];
        slot.InputNs = nowNs;
        // The guard holds this slot's feedback; a pass runs once it has passed, to set the client right if the target differs.
        _pending = true;
        return true;
    }

    /// <summary>One feedback pass, when one is due: what every binding's route shows now, sent where it differs from what each client
    /// shows.</summary>
    public void Pass(EngineCore engine, ControlSettings settings, ImmutableArray<ControlSurface> surfaces, Func<IPEndPoint, string> surfaceOf,
        Action<byte[], IPEndPoint> send, long nowNs)
    {
        var period = 1_000_000_000L / Math.Clamp(settings.FeedbackHz, 1, 1000);
        if (_lastPassNs != long.MinValue && nowNs - _lastPassNs < period)
            return;
        var key = engine.StateKey();
        if (key == _lastKey && !_pending && ReferenceEquals(settings, _lastSettings))
            return;
        _lastPassNs = nowNs;
        _lastKey = key;
        _lastSettings = settings;
        _pending = false;
        SendDue(engine, settings, surfaces, surfaceOf, send, nowNs);
    }

    /// <summary>A pass that is due. Its own method: its filters' closures (they capture the pass's clients) are made only when a pass
    /// runs. Written in <see cref="Pass"/>, an optimised build would allocate them at the method's entry, on every poll of the control
    /// loop, due or not.</summary>
    private void SendDue(EngineCore engine, ControlSettings settings, ImmutableArray<ControlSurface> surfaces, Func<IPEndPoint, string> surfaceOf,
        Action<byte[], IPEndPoint> send, long nowNs)
    {
        var statics = settings.OscFeedback.Select(s => FeedbackMath.TryEndpoint(s, out var e) ? e : null).OfType<IPEndPoint>().ToList();
        List<IPEndPoint> clients;
        lock (_clientsGate)
        {
            foreach (var (c, at) in _clients.ToList())
                if (nowNs - at > ClientIdleNs)
                    _clients.Remove(c);
            clients = [.. _clients.Keys];
        }
        var table = engine.ControlInput.TableOf(engine.Store.Project);
        if (!ReferenceEquals(table, _lastTable))
        {
            _lastTable = table;
            _routes.Clear();
        }
        var desired = new Dictionary<(IPEndPoint To, string Address), (OscArg?[] Args, bool Value)>();
        foreach (var r in table.All)
        {
            if (!_routes.TryGetValue(r, out var route))
            {
                route = FeedbackMath.RouteOf(r.Binding, r.Input, r.Target, r.Behaviour);
                _routes[r] = route;
            }
            if (route.Kind == FeedbackKind.None || ArgOf(engine, r, route) is not { } arg)
                continue;
            foreach (var to in Destinations(r, route, surfaces, clients, statics, surfaceOf))
            {
                var k = (to, route.Address);
                desired.TryGetValue(k, out var d);
                if (d.Args is null || d.Args.Length <= route.Arg)
                {
                    var grown = new OscArg?[route.Arg + 1];
                    d.Args?.CopyTo(grown, 0);
                    d.Args = grown;
                }
                d.Args[route.Arg] = arg;
                d.Value |= route.Kind == FeedbackKind.Value;
                desired[k] = d;
            }
        }
        var outgoing = new Dictionary<IPEndPoint, List<OscMessage>>();
        foreach (var ((to, address), (args, value)) in desired)
        {
            // A message with several arguments goes only once every one of them is known.
            if (args.Any(a => a is null))
                continue;
            var full = args.Select(a => a!).ToArray();
            if (!_slots.TryGetValue((to, address), out var slot))
            {
                slot = new Slot();
                _slots[(to, address)] = slot;
            }
            if (slot.Shows is { } shows && Same(shows, full))
                continue;
            // A value waits until the control has been still for 300 ms; a lamp answers a press at once.
            if (value && slot.InputNs != long.MinValue && nowNs - slot.InputNs < EchoGuardNs)
            {
                _pending = true;
                continue;
            }
            slot.Shows = full;
            slot.SentNs = nowNs;
            if (!outgoing.TryGetValue(to, out var list))
            {
                list = [];
                outgoing[to] = list;
            }
            list.Add(new OscMessage(address, [.. full]));
        }
        foreach (var (to, messages) in outgoing)
            Send(messages, to, send);
        // Slots of clients that are gone are forgotten (a client that comes back is sent everything again); a destination a binding's
        // own words or its surface's feedbackTo names stays while a route sends there, so it too hears only what changed.
        var routed = desired.Keys.Select(k => k.To).ToHashSet();
        foreach (var k in _slots.Keys.Where(k => !clients.Contains(k.To) && !statics.Contains(k.To) && !routed.Contains(k.To)).ToList())
            _slots.Remove(k);
    }

    /// <summary>Where a binding's feedback goes: its own words' endpoint; else its surface's feedbackTo; else every client its
    /// surface reaches (all of them for a binding on any surface) and the node's feedback clients.</summary>
    private static IEnumerable<IPEndPoint> Destinations(BindingRuntime r, FeedbackRoute route, ImmutableArray<ControlSurface> surfaces, List<IPEndPoint> clients,
        List<IPEndPoint> statics, Func<IPEndPoint, string> surfaceOf)
    {
        if (route.To.Length > 0)
            return FeedbackMath.TryEndpoint(route.To, out var e) ? [e!] : [];
        var surface = r.Input.Surface;
        if (surface.Length > 0 && surfaces.FirstOrDefault(s => s.Name.Equals(surface, StringComparison.OrdinalIgnoreCase)) is { Kind: "osc", FeedbackTo.Length: > 0 } named)
            return FeedbackMath.TryEndpoint(named.FeedbackTo, out var e) ? [e!] : [];
        var reached = surface.Length == 0 ? clients : clients.Where(c => surfaceOf(c).Equals(surface, StringComparison.OrdinalIgnoreCase));
        return reached.Concat(statics).Distinct();
    }

    /// <summary>What a route shows now: a value in the control's own range, or a lamp's word; null when the target cannot be read.</summary>
    private OscArg? ArgOf(EngineCore engine, BindingRuntime r, FeedbackRoute route)
    {
        if (route.Kind == FeedbackKind.Lamp && engine.FactOf(r.Target) is { } on)
            return FeedbackMath.LampArg(on, route.On, route.Off);
        if (r.Target.Field is not { } field || ReadOf(engine, field.Id, field.Path) is not { } y)
            return null;
        var x = FeedbackMath.ControlValue(r.Binding.Shape, r.Range, y);
        if (route.Kind == FeedbackKind.Value)
            return OscArg.F((float)x);
        var shape = r.Binding.Shape;
        var half = shape.InMin + (shape.InMax - shape.InMin) / 2;
        return FeedbackMath.LampArg(shape.InMax >= shape.InMin ? x >= half : x <= half, route.On, route.Off);
    }

    /// <summary>A bound field's number, read again only when its entity changed (an unchanged entity keeps its reference across
    /// show revisions).</summary>
    private double? ReadOf(EngineCore engine, string id, string path)
    {
        var entity = engine.EntityOf(id);
        if (_reads.TryGetValue((id, path), out var cached) && ReferenceEquals(cached.Entity, entity))
            return cached.Value;
        var value = entity is null ? null : EngineCore.NumberAt(entity, path);
        _reads[(id, path)] = (entity, value);
        return value;
    }

    /// <summary>Sends a client its messages, each counted before the datagram carrying it goes: a reader holding a datagram never
    /// reads a count that leaves it out.</summary>
    private void Send(List<OscMessage> messages, IPEndPoint to, Action<byte[], IPEndPoint> send)
    {
        if (messages.Count == 1)
        {
            Interlocked.Increment(ref _sent);
            send(OscCodec.Write(messages[0]), to);
            return;
        }
        // A bundle is 16 bytes of header and each element 4 bytes of size and its own bytes.
        var batch = new List<IOscPacket>();
        var size = 16;
        foreach (var m in messages)
        {
            var bytes = OscCodec.Write(m).Length + 4;
            if (batch.Count > 0 && size + bytes > MaxBundleBytes)
            {
                send(OscCodec.Write(new OscBundle(OscBundle.Immediately, [.. batch])), to);
                batch.Clear();
                size = 16;
            }
            batch.Add(m);
            size += bytes;
            Interlocked.Increment(ref _sent);
        }
        send(OscCodec.Write(new OscBundle(OscBundle.Immediately, [.. batch])), to);
    }

    /// <summary>Two argument lists show the same: numbers equal to six significant digits (a line carries six), words equal.</summary>
    private static bool Same(IReadOnlyList<OscArg> a, IReadOnlyList<OscArg> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].IsNumber && b[i].IsNumber)
            {
                var x = a[i].Number;
                var y = b[i].Number;
                if (Math.Abs(x - y) > 5e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y))))
                    return false;
            }
            else if (a[i].Tag != b[i].Tag || !string.Equals(a[i].Text, b[i].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>What one client shows at one address (as it last sent it, or as we last sent it), and when each happened.</summary>
    private sealed class Slot
    {
        public OscArg[]? Shows { get; set; }

        public long InputNs { get; set; } = long.MinValue;

        public long SentNs { get; set; } = long.MinValue;
    }
}
