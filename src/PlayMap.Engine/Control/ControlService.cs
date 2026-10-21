using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using PlayMap.Core.Audio;
using PlayMap.Core.Bus;
using PlayMap.Core.Control;
using PlayMap.Core.Control.Midi;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Dmx;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Tc;
using PlayMap.Core.Wire;
using PlayMap.Devices.Midi;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tc;
using PlayMap.Net.Control;

namespace PlayMap.Engine.Control;

/// <summary>What a control service is made from beyond the engine: the MIDI backend (the machine's own when null), the node's
/// Art-Net socket, the audio inputs LTC arrives on, where the OSC socket binds (the settings' when null) and where changed
/// settings are kept.</summary>
public sealed record ControlServiceOptions
{
    public IMidiBackend? Midi { get; init; }

    public IArtNetEndpoint? ArtNet { get; init; }

    public IAudioInputs? Audio { get; init; }

    public IPAddress? OscBind { get; init; }

    public int? OscPort { get; init; }

    public Func<ControlSettings, string?>? Persist { get; init; }

    /// <summary>The seed of the ArtPollReply delays.</summary>
    public ulong Seed { get; init; } = 0x5EED_A27E;
}

/// <summary>The node's control inputs, attached to the running engine: the OSC socket (lines under /playmap and bound addresses, and
/// feedback back to its clients), the MIDI ports (each through its own parser), the Art-Net consumer (DMX for bindings when Art-Net
/// input is on; ArtTimeCode for timecode sources; ArtPoll answered), the sACN receiver (DMX for bindings when sACN input is on, the
/// sources of a universe merged by priority), and the timecode inputs (LTC through the audio inputs, MTC through the MIDI ports,
/// Art-Net). Driver threads parse and enqueue; one control loop drains in arrival order (an axis keeps only its latest value per
/// drain; edges are never dropped) and runs every 10 ms for the timed work. Ports and sockets that cannot open say why in STATE
/// and the node's faults; the node runs on without them.</summary>
public sealed class ControlService : IAsyncDisposable, INodeFaults
{
    private readonly ControlServiceOptions _options;
    private readonly Channel<Work> _work = Channel.CreateBounded<Work>(new BoundedChannelOptions(8192) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly OscServer _osc;
    private readonly bool _ownsMidi;
    private IMidiBackend _midi;
    private readonly ArtNetDmxInput _artIn;
    private readonly SacnDmxInput _sacnIn = new();
    private SacnReceiver? _sacn;
    private readonly Dictionary<string, MidiPort> _midiInputs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LtcInput> _ltc = new(StringComparer.Ordinal);
    private readonly Lock _ltcGate = new();
    private Task _ltcWork = Task.CompletedTask;
    private readonly ConcurrentDictionary<string, MtcParser> _mtc = new(StringComparer.Ordinal);
    private const long OscSenderIdleMs = 600_000;

    private readonly ConcurrentDictionary<string, long> _oscSenders = new(StringComparer.Ordinal);
    private readonly byte[] _pollReply = new byte[ArtNet.PollReplyLength];
    private readonly OscFeedback _feedback = new();
    private readonly Func<IPEndPoint, string> _surfaceOfSender;
    private readonly Action<byte[], IPEndPoint> _sendFeedback;
    private EngineCore? _engine;
    private Task? _loop;
    private Task? _ticker;
    private bool _artSubscribed;
    private string? _oscProblem;
    private string _midiWords = "";
    private long _lastRescanNs;
    private (long, long, long, long, long, long, int, long, long, int, (long, long, long, int)) _counts;
    private long _countsAtNs = long.MinValue;
    private Project? _seen;
    private long _oscRefused;
    private long _oscMalformed;
    private ImmutableArray<ControlSurface> _surfaces = [];

    public ControlService(ControlServiceOptions? options = null)
    {
        _options = options ?? new ControlServiceOptions();
        _osc = new OscServer(OnOsc);
        _ownsMidi = _options.Midi is null;
        _midi = _options.Midi ?? new MidiBackends.NoMidi("not started");
        _artIn = new ArtNetDmxInput(_options.Seed);
        _surfaceOfSender = SurfaceOfSender;
        _sendFeedback = (bytes, to) => _osc.Send(bytes, to);
        _sacnIn.SurfaceOf = SacnSurfaceOf;
    }

    private IMidiBackend Midi => _midi;

    // ---- life ----

    /// <summary>Attaches to the running engine: opens the OSC socket, the MIDI ports, the Art-Net consumer and the timecode inputs
    /// the settings and the show ask for, and starts the control loop.</summary>
    public Task AttachAsync(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        var input = engine.ControlInput;
        input.Persist = _options.Persist;
        input.SettingsChanged += _ => _work.Writer.TryWrite(Work.Settings);
        var settings = input.Settings;
        if (_ownsMidi && settings.MidiEnabled)
        {
            _midi.Dispose();
            _midi = MidiBackends.ForThisMachine(settings.MidiBackend, engine.Services.Mono);
        }
        Midi.PortsChanged += () => _work.Writer.TryWrite(Work.Ports);
        OpenOsc(settings, engine.Services.Settings);
        _midiWords = Midi.Unavailable ?? "";
        Reconcile(engine.Store.Current, settings);
        _loop = Task.Run(() => LoopAsync(_stop.Token), _stop.Token);
        _ticker = Task.Run(() => TickerAsync(_stop.Token), _stop.Token);
        engine.FactsChanged();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        foreach (var t in new[] { _loop, _ticker })
        {
            if (t is null)
                continue;
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loop stops on cancellation.
            }
        }
        _osc.Dispose();
        foreach (var p in _midiInputs.Values)
            p.Input.Dispose();
        _midiInputs.Clear();
        foreach (var l in _ltc.Values)
        {
            l.Close();
            OffLoop(l.Dispose);
        }
        _ltc.Clear();
        Task ltcWork;
        lock (_ltcGate)
            ltcWork = _ltcWork;
        await ltcWork.ConfigureAwait(false);
        if (_artSubscribed && _options.ArtNet is { } art)
            art.Received -= OnArtNet;
        if (_sacn is not null)
            await _sacn.DisposeAsync().ConfigureAwait(false);
        if (_ownsMidi)
            _midi.Dispose();
        _stop.Dispose();
    }

    private async Task TickerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            _work.Writer.TryWrite(Work.Tick);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var batch = new List<Work>(256);
        var reader = _work.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 1024 && reader.TryRead(out var w))
                batch.Add(w);
            try
            {
                await RunAsync(batch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                _engine?.Journal.Add("fault", "control: " + Journal.Describe(ex), "control", null, false);
            }
        }
    }

    private async Task RunAsync(List<Work> batch, CancellationToken ct)
    {
        var engine = _engine!;
        // Latest wins for an axis within one drain: an earlier reading of the same input is passed over.
        var lastAxis = new Dictionary<InputAddress, int>();
        for (var i = 0; i < batch.Count; i++)
            if (batch[i].Kind == WorkKind.Event && batch[i].Event.Kind == InputKind.Axis)
                lastAxis[batch[i].Event.Address] = i;
        var ticked = false;
        for (var i = 0; i < batch.Count; i++)
        {
            var w = batch[i];
            switch (w.Kind)
            {
                case WorkKind.Event:
                    if (w.Event.Kind == InputKind.Axis && lastAxis[w.Event.Address] != i)
                        continue;
                    await engine.ControlInput.ProcessAsync(w.Event, ct).ConfigureAwait(false);
                    break;
                case WorkKind.Osc:
                    await OscAsync(w.Bytes!, w.From!, ct).ConfigureAwait(false);
                    break;
                case WorkKind.ArtNet:
                    await ArtNetAsync(w.Bytes!, w.Address, w.AtNs, ct).ConfigureAwait(false);
                    break;
                case WorkKind.Settings:
                    Reconcile(engine.Store.Current, engine.ControlInput.Settings);
                    break;
                case WorkKind.Ports:
                    ReconcileMidi(engine.Store.Project, engine.ControlInput.Settings);
                    break;
                case WorkKind.Want:
                    _artIn.Want(w.Channels!);
                    break;
                case WorkKind.Sacn:
                    await SacnAsync(w.Bytes!, w.Address, w.AtNs, ct).ConfigureAwait(false);
                    break;
                case WorkKind.WantSacn:
                    _sacnIn.Want(w.Channels!);
                    if (_sacn is { } sacn)
                    {
                        sacn.Listen(_sacnIn.Universes);
                        engine.FactsChanged();
                    }
                    break;
                default:
                    ticked = true;
                    break;
            }
        }
        if (ticked)
            await TickAsync(ct).ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var engine = _engine!;
        var now = engine.Services.Mono.NowNs;
        var shown = engine.Store.Current;
        var project = shown.Project;
        if (!ReferenceEquals(project, _seen))
            Reconcile(shown, engine.ControlInput.Settings);
        await engine.ControlInput.PollAsync(now, ct).ConfigureAwait(false);
        foreach (var p in _midiInputs.Values)
            lock (p.Parser)
                p.Parser.Flush(now);
        var events = new List<InputEvent>();
        _artIn.Tick(now, events);
        foreach (var e in events)
            await engine.ControlInput.ProcessAsync(e, ct).ConfigureAwait(false);
        events.Clear();
        _sacnIn.Tick(now, events);
        if (engine.ControlInput.Settings.SacnEnabled)
            foreach (var e in events)
                await engine.ControlInput.ProcessAsync(e, ct).ConfigureAwait(false);
        foreach (var due in _artIn.TakeDuePolls(now))
            ReplyToPoll(due.To);
        foreach (var l in _ltc.Values)
            l.Tick(now);
        if (_osc.IsOpen)
            _feedback.Pass(engine, engine.ControlInput.Settings, _surfaces, _surfaceOfSender, _sendFeedback, now);
        // Counters move STATE at most four times a second.
        var counts = (_artIn.Frames, _artIn.Dropped, _artIn.Polls, _artIn.Malformed, Interlocked.Read(ref _oscRefused), Interlocked.Read(ref _oscMalformed), _oscSenders.Count,
            _feedback.Sent, _feedback.Echoes, _feedback.ClientCount, (_sacnIn.Frames, _sacnIn.Dropped, _sacnIn.Previews, _sacnIn.Sources.Count));
        if (counts != _counts && (_countsAtNs == long.MinValue || now - _countsAtNs >= 250_000_000))
        {
            _counts = counts;
            _countsAtNs = now;
            engine.FactsChanged();
        }
        if (now - _lastRescanNs > 2_000_000_000)
        {
            _lastRescanNs = now;
            ReconcileMidi(project, engine.ControlInput.Settings);
            // STATE's OSC senders are the ones heard in the last 10 minutes (as feedback leases its clients), not every one ever.
            var wall = engine.Services.Wall.UnixMs;
            foreach (var sender in _oscSenders)
                if (wall - sender.Value > OscSenderIdleMs)
                    _oscSenders.TryRemove(sender);
        }
    }

    /// <summary>Opens and closes what the settings and the show ask for: MIDI inputs, the Art-Net consumer and its wanted channels,
    /// the timecode inputs.</summary>
    private void Reconcile(ProjectSnapshot shown, ControlSettings settings)
    {
        var project = shown.Project;
        _seen = project;
        _surfaces = project.Control.Surfaces;
        ReconcileMidi(project, settings);
        ReconcileArtNet(project, settings);
        ReconcileSacn(project, settings);
        ReconcileTimecode(shown);
        _engine?.FactsChanged();
    }

    // ---- OSC ----

    public bool OscOpen => _osc.IsOpen;

    /// <summary>Feedback messages sent, echoes dropped, and the clients fed because they were heard.</summary>
    public (long Sent, long Echoes, int Clients) Feedback => (_feedback.Sent, _feedback.Echoes, _feedback.ClientCount);

    public string? OscProblem => _oscProblem;

    /// <summary>The OSC socket's address (its port once bound; 0 = ephemeral in tests).</summary>
    public IPEndPoint? OscLocal => _osc.Local;

    /// <summary>The control inputs open now, each with the address it is bound to: OSC (its socket's own), Art-Net while DMX input or an
    /// Art-Net timecode source reads the shared socket bound as asked (its interface, or every interface), and sACN while its receiver
    /// is open (its interface, or every interface).</summary>
    public ImmutableArray<ShowDoor> Doors
    {
        get
        {
            var doors = ImmutableArray.CreateBuilder<ShowDoor>(3);
            if (_osc.Local is { } osc)
                doors.Add(new ShowDoor("osc", osc.Address));
            if (Volatile.Read(ref _artSubscribed) && _options.ArtNet is { Problem: null } art)
                doors.Add(new ShowDoor("artnet", art.Interface ?? IPAddress.Any));
            if (Volatile.Read(ref _sacn) is { IsOpen: true } sacn)
                doors.Add(new ShowDoor("sacn", sacn.Interface ?? IPAddress.Any));
            return doors.ToImmutable();
        }
    }

    private void OpenOsc(ControlSettings settings, NodeSettings node)
    {
        if (!settings.OscEnabled)
        {
            _oscProblem = null;
            return;
        }
        var bind = _options.OscBind
            ?? (settings.OscBind.Length > 0 && IPAddress.TryParse(settings.OscBind, out var a) ? a : null)
            ?? (node.Bind.Length > 0 && IPAddress.TryParse(node.Bind, out var b) ? b : IPAddress.Any);
        _oscProblem = _osc.Start(bind, _options.OscPort ?? settings.OscPort) ? null : _osc.Problem;
    }

    private void OnOsc(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        if (_osc.Local is { } local && from.Port == local.Port && (from.Address.Equals(local.Address) || IPAddress.IsLoopback(from.Address) && IPAddress.IsLoopback(local.Address)))
            return;
        _work.Writer.TryWrite(new Work(WorkKind.Osc, default, packet.ToArray(), from, 0));
    }

    /// <summary>Whether a sender's lines count as paired: an open node, a trusted loopback, an address the node trusts or OSC allows.</summary>
    private bool Paired(IPEndPoint from)
    {
        var node = _engine!.Services.Settings;
        var address = from.Address.ToString();
        return node.Token.Length == 0 || (IPAddress.IsLoopback(from.Address) && node.TrustLoopback)
            || node.TrustedAddresses.Contains(address, StringComparer.Ordinal) || _engine.ControlInput.Settings.OscAllow.Contains(address, StringComparer.Ordinal);
    }

    private async Task OscAsync(byte[] packet, IPEndPoint from, CancellationToken ct)
    {
        _oscSenders[from.Address.ToString()] = _engine!.Services.Wall.UnixMs;
        if (!OscCodec.TryRead(packet, out var read, out var why))
        {
            Interlocked.Increment(ref _oscMalformed);
            Send(OscMessage.Of("/playmap/error", OscArg.S("malformed: " + why)), from);
            return;
        }
        var messages = new List<OscMessage>();
        Flatten(read!, messages);
        foreach (var m in messages)
            await OscMessageAsync(m, from, ct).ConfigureAwait(false);
    }

    private static void Flatten(IOscPacket packet, List<OscMessage> into)
    {
        if (packet is OscMessage m)
        {
            into.Add(m);
            return;
        }
        foreach (var e in ((OscBundle)packet).Elements)
            Flatten(e, into);
    }

    private async Task OscMessageAsync(OscMessage m, IPEndPoint from, CancellationToken ct)
    {
        var engine = _engine!;
        if (!m.Address.StartsWith(OscAddressMap.Prefix, StringComparison.Ordinal))
        {
            await OscBoundAsync(m, from, ct).ConfigureAwait(false);
            return;
        }
        var route = OscAddressMap.Map(m, engine.Store.Project);
        switch (route.Kind)
        {
            case OscRouteKind.Ping:
            {
                var t = engine.Services.Wall.UnixMs;
                Send(OscMessage.Of("/playmap/pong", OscArg.H(t), OscArg.H(t), OscArg.H(t)), from);
                return;
            }
            case OscRouteKind.Query:
            {
                var v = engine.ReadNumber(route.QueryId, route.QueryPath);
                Send(v is { } n ? OscMessage.Of(m.Address, OscArg.D(n)) : OscMessage.Of("/playmap/error", OscArg.S($"no number at {route.QueryId} {route.QueryPath}")), from);
                return;
            }
            case OscRouteKind.Unknown:
                Interlocked.Increment(ref _oscRefused);
                Reply(false, route.Why, route.RequestId, from);
                return;
        }
        var session = new SessionInfo("osc:" + from, "osc", from.Address.ToString(), IPAddress.IsLoopback(from.Address), "OSC " + from, Paired(from));
        foreach (var text in route.Lines)
        {
            if (!WireGrammar.TryParse(text, out var line, out var error))
            {
                Interlocked.Increment(ref _oscRefused);
                Reply(false, "the line does not read: " + error?.Reason, route.RequestId, from);
                continue;
            }
            CommandReply reply;
            try
            {
                reply = await engine.HandleAsync(line, session, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                reply = CommandReply.Fail($"fault {ex.GetType().Name}");
            }
            if (!reply.Ok)
                Interlocked.Increment(ref _oscRefused);
            if (!reply.Silent)
                Reply(reply.Ok, reply.Ok ? (reply.Payload ?? reply.Effect) : reply.Payload ?? "refused", route.RequestId, from);
        }
    }

    /// <summary>The OSC surface a sender is: the first enabled one whose match is empty, its address or its address:port ("" = none).</summary>
    private string SurfaceOfSender(IPEndPoint from) =>
        _surfaces.FirstOrDefault(s => s.Kind == "osc" && s.Enabled && (s.Match.Length == 0 || s.Match == from.Address.ToString() || s.Match == from.ToString()))?.Name ?? "";

    /// <summary>A message to an address a binding uses: each argument (0-based) an axis value (T/F a press/release; no argument a trigger).
    /// Only a paired sender moves bindings (the OSC gate, as for /playmap lines); it becomes a feedback client, and a message that
    /// echoes what it was just sent is dropped.</summary>
    private async Task OscBoundAsync(OscMessage m, IPEndPoint from, CancellationToken ct)
    {
        var engine = _engine!;
        var now = engine.Services.Mono.NowNs;
        if (!Paired(from))
        {
            Interlocked.Increment(ref _oscRefused);
            Send(OscMessage.Of("/playmap/error", OscArg.S($"not paired — OSC from {from.Address} moves no binding until this node trusts the address (TrustedAddresses or OscAllow)")), from);
            return;
        }
        if (!_feedback.Heard(from, m, now))
            return;
        var surface = SurfaceOfSender(from);
        if (m.Args.Length == 0)
        {
            await engine.ControlInput.ProcessAsync(new InputEvent(InputAddress.Osc(m.Address, 0, surface), InputKind.Trigger, 1, 1, 0, now) { Origin = from.ToString() }, ct)
                .ConfigureAwait(false);
            return;
        }
        for (var i = 0; i < m.Args.Length; i++)
        {
            var a = m.Args[i];
            var address = InputAddress.Osc(m.Address, i, surface);
            InputEvent e = a.Tag switch
            {
                'T' => new InputEvent(address, InputKind.Press, 1, 1, 0, now),
                'F' => new InputEvent(address, InputKind.Release, 0, 0, 0, now),
                _ when a.IsNumber => new InputEvent(address, InputKind.Axis, a.Number, (int)Math.Round(a.Number), 0, now),
                _ => new InputEvent(address, InputKind.Trigger, 1, 1, 0, now),
            };
            await engine.ControlInput.ProcessAsync(e with { Origin = from.ToString() }, ct).ConfigureAwait(false);
        }
    }

    private void Reply(bool ok, string payload, string? requestId, IPEndPoint to)
    {
        var args = new List<OscArg> { OscArg.S(ok ? "OK" : "ERR"), OscArg.S(payload) };
        if (requestId is not null)
            args.Add(OscArg.S(requestId));
        Send(new OscMessage("/playmap/reply", [.. args]), to);
        if (!ok)
            Send(OscMessage.Of("/playmap/error", OscArg.S(payload)), to);
    }

    private void Send(OscMessage m, IPEndPoint to) => _osc.Send(OscCodec.Write(m), to);

    // ---- MIDI ----

    private sealed record MidiPort(MidiPortInfo Info, string Surface, IMidiInput Input, MidiParser Parser);

    /// <summary>The surface a port is: this machine's alias glob first, then the surface's own match glob, then its exact name.</summary>
    private static string SurfaceOfPort(MidiPortInfo port, Project project, ControlSettings settings)
    {
        foreach (var s in project.Control.Surfaces.Where(s => s.Kind == "midi" && s.Enabled))
        {
            if (settings.SurfacePorts.TryGetValue(s.Name, out var alias) ? PortGlob.Matches(alias, port.Name)
                : s.Match.Length > 0 ? PortGlob.Matches(s.Match, port.Name) : string.Equals(s.Name, port.Name, StringComparison.OrdinalIgnoreCase))
                return s.Name;
        }
        return "";
    }

    private void ReconcileMidi(Project project, ControlSettings settings)
    {
        var wanted = settings.MidiEnabled
            ? Midi.Ports().Where(p => p.IsInput && !settings.MidiIgnore.Any(g => PortGlob.Matches(g, p.Name))).ToList()
            : [];
        lock (_midiInputs)
        {
            foreach (var (id, open) in _midiInputs.ToList())
            {
                var still = wanted.FirstOrDefault(p => p.Id == id);
                if (still is not null && SurfaceOfPort(still, project, settings) == open.Surface)
                    continue;
                open.Input.Dispose();
                _midiInputs.Remove(id);
            }
            foreach (var port in wanted.Where(p => !_midiInputs.ContainsKey(p.Id)))
            {
                var surface = SurfaceOfPort(port, project, settings);
                var sink = new MidiSink(this, port.Name);
                var parser = new MidiParser(sink, surface, port.Name);
                try
                {
                    var input = Midi.OpenInput(port, new MidiReceiver(parser));
                    _midiInputs[port.Id] = new MidiPort(port, surface, input, parser);
                }
                catch (MidiUnavailableException ex)
                {
                    _midiWords = ex.Message;
                }
            }
        }
    }

    public IEnumerable<string> MidiInputNames()
    {
        lock (_midiInputs)
            return Midi.Ports().Where(p => p.IsInput).Select(p => p.Name).ToList();
    }

    /// <summary>The port a MIDI surface is on now (null when none of this machine's ports is it).</summary>
    public string? PortOf(ControlSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_midiInputs)
            return _midiInputs.Values.FirstOrDefault(p => p.Surface.Equals(surface.Name, StringComparison.OrdinalIgnoreCase))?.Info.Name;
    }

    private string? MidiRefused(ControlSurface surface) => Midi.Unavailable is { } why && surface.Enabled ? "MIDI: " + why : null;

    private sealed class MidiReceiver(MidiParser parser) : IMidiReceiver
    {
        public void Receive(ReadOnlySpan<byte> message, long monoNs)
        {
            lock (parser)
                parser.Feed(message, monoNs);
        }
    }

    /// <summary>A port's parsed messages: control events to the loop; MTC to every MTC source on this port.</summary>
    private sealed class MidiSink(ControlService owner, string port) : IMidiSink
    {
        public void Input(in InputEvent e) => owner._work.Writer.TryWrite(new Work(WorkKind.Event, e, null, null, 0));

        public void QuarterFrame(int piece, int value, long monoNs) => owner.Mtc(port, piece, value, monoNs);

        public void FullFrame(int device, int rateCode, int hours, int minutes, int seconds, int frames, long monoNs) =>
            owner.MtcLocate(port, rateCode, hours, minutes, seconds, frames, monoNs);

        public void UserBits(int device, uint bits, int flags, long monoNs)
        {
            // User bits ride beside the code; this build reports the LTC ones.
        }

        public void Realtime(byte status, long monoNs)
        {
            // MIDI clock is next round; real-time bytes never fire a binding.
        }

        public void Sysex(ReadOnlySpan<byte> message, long monoNs)
        {
            // Only MSC, MMC and MTC SysEx are read (by the parser); others are monitor-only next round.
        }
    }

    // ---- Art-Net ----

    public bool ArtNetOpen => _options.ArtNet is ArtNetEndpoint { IsOpen: true } || (_artSubscribed && _options.ArtNet is not null);

    /// <summary>DMX input on and its socket heard.</summary>
    public bool ArtNetListening => _artSubscribed && _engine?.ControlInput.Settings.ArtNetEnabled == true && _options.ArtNet?.Problem is null;

    public string? ArtNetProblem => _artSubscribed ? _options.ArtNet?.Problem : null;

    private void ReconcileArtNet(Project project, ControlSettings settings)
    {
        var needs = settings.ArtNetEnabled || project.Timecode.Sources.Any(s => s.Kind == TimecodeKind.ArtNet);
        if (_options.ArtNet is not { } art)
            return;
        if (needs && !_artSubscribed)
        {
            art.Received += OnArtNet;
            _artSubscribed = true;
        }
        else if (!needs && _artSubscribed)
        {
            art.Received -= OnArtNet;
            _artSubscribed = false;
        }
        var channels = new List<DmxChannel>();
        foreach (var b in project.Control.Bindings.Where(b => b.Enabled))
            if (InputAddress.Parse(b.Input, out _) is { Source: InputSource.ArtNet } a)
                channels.Add(new DmxChannel(a.Universe, a.Number, a.Fine));
        _work.Writer.TryWrite(new Work(WorkKind.Want, default, null, null, 0, [.. channels]));
    }

    private void OnArtNet(ReadOnlySpan<byte> packet, SocketAddress from)
    {
        // An IPv4 SocketAddress holds the address in bytes 4–7.
        var address = from.Family == System.Net.Sockets.AddressFamily.InterNetwork && from.Size >= 8
            ? (uint)(from[4] << 24 | from[5] << 16 | from[6] << 8 | from[7]) : 0u;
        // A packet is timed when the socket hands it over, not when the loop reaches it: a timecode line is fitted to arrivals.
        var at = _engine?.Services.Mono.NowNs ?? 0;
        _work.Writer.TryWrite(new Work(WorkKind.ArtNet, default, packet.ToArray(), null, address) { AtNs = at });
    }

    private async Task ArtNetAsync(byte[] packet, uint from, long now, CancellationToken ct)
    {
        var engine = _engine!;
        var settings = engine.ControlInput.Settings;
        var source = new IPAddress(BitConverterBig(from)).ToString();
        // A source the node does not allow takes no part in DMX input: its frames and its ArtSync never reach the input's state (so it
        // can neither hold an allowed console's frames nor take a newly bound channel's first level).
        if (settings.DmxAllow.Length > 0 && (ArtNet.OpOf(packet) is ArtOp.Dmx or ArtOp.Sync) && !settings.DmxAllow.Contains(source, StringComparer.Ordinal))
            return;
        var events = new List<InputEvent>();
        _artIn.LearnAll = engine.ControlInput.Learning is not null && settings.ArtNetEnabled;
        var result = _artIn.Receive(packet, from, now, events, out var tc);
        if (result == ArtNetInputResult.TimeCode)
        {
            ArtTimeCodeIn(tc, now);
            return;
        }
        if (!settings.ArtNetEnabled || events.Count == 0)
            return;
        var surface = _surfaces.FirstOrDefault(s => s.Kind == "artnet" && s.Enabled && (s.Match.Length == 0 || s.Match == source))?.Name ?? "";
        foreach (var e in events)
            await engine.ControlInput.ProcessAsync(e with { Address = e.Address with { Surface = surface }, Origin = source }, ct).ConfigureAwait(false);
    }

    private static byte[] BitConverterBig(uint a) => [(byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a];

    /// <summary>An unicast ArtPollReply to a poller, naming this node and the universes its bindings read.</summary>
    private void ReplyToPoll(uint to)
    {
        if (_options.ArtNet is not { } art || _engine is not { } engine)
            return;
        var iface = art.Interface ?? LocalAddressToward(new IPAddress(BitConverterBig(to)));
        var universes = engine.Store.Project.Control.Bindings
            .Select(b => InputAddress.Parse(b.Input, out _)).Where(a => a is { Source: InputSource.ArtNet }).Select(a => a!.Value.Universe).Distinct().Order().ToList();
        var info = new ArtPollReplyInfo(iface.GetAddressBytes(), "PlayMap", $"{engine.Services.Settings.Name} (PlayMap media server)", "#0001 [0000] control input",
            universes, 0x00FF);
        var n = ArtNet.WritePollReply(_pollReply, info);
        art.Send(_pollReply.AsSpan(0, n), art.AddressOf(new IPEndPoint(new IPAddress(BitConverterBig(to)), ArtNet.Port)));
    }

    /// <summary>The address of this machine's interface that reaches a peer (the route's source address; nothing is sent), or
    /// loopback when no route reaches it. A socket bound to all interfaces answers a poll with the address the poller can reach.</summary>
    internal static IPAddress LocalAddressToward(IPAddress peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            using var probe = new System.Net.Sockets.Socket(peer.AddressFamily, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            probe.Connect(new IPEndPoint(peer, ArtNet.Port));
            return probe.LocalEndPoint is IPEndPoint { Address: var local } && !local.Equals(IPAddress.Any) ? local : IPAddress.Loopback;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return IPAddress.Loopback;
        }
    }

    // ---- sACN ----

    /// <summary>sACN input on and its receiver open.</summary>
    public bool SacnListening => _engine?.ControlInput.Settings.SacnEnabled == true && _sacn is { IsOpen: true, Problem: null };

    public string? SacnProblem => _engine?.ControlInput.Settings.SacnEnabled == true ? _sacn?.Problem : null;

    /// <summary>Opens the receiver while sACN input is on (on the configured interface: a changed interface opens a new one), and
    /// joins the groups of the universes the bindings read; closes it when input is off.</summary>
    private void ReconcileSacn(Project project, ControlSettings settings)
    {
        if (!settings.SacnEnabled)
        {
            if (_sacn is { } closing)
            {
                closing.Received -= OnSacn;
                closing.Close();
                _sacn = null;
            }
            return;
        }
        var iface = settings.SacnInterface.Length > 0 && IPAddress.TryParse(settings.SacnInterface, out var a) ? a : null;
        if (_sacn is { } open && !Equals(open.Interface, iface))
        {
            open.Received -= OnSacn;
            open.Close();
            _sacn = null;
        }
        if (_sacn is null)
        {
            _sacn = new SacnReceiver(iface?.ToString() ?? "");
            _sacn.Received += OnSacn;
        }
        var channels = new List<DmxChannel>();
        foreach (var b in project.Control.Bindings.Where(b => b.Enabled))
            if (InputAddress.Parse(b.Input, out _) is { Source: InputSource.Sacn } c)
                channels.Add(new DmxChannel(c.Universe, c.Number, c.Fine));
        _work.Writer.TryWrite(new Work(WorkKind.WantSacn, default, null, null, 0, [.. channels]));
    }

    private void OnSacn(ReadOnlySpan<byte> packet, SocketAddress from)
    {
        var address = from.Family == System.Net.Sockets.AddressFamily.InterNetwork && from.Size >= 8
            ? (uint)(from[4] << 24 | from[5] << 16 | from[6] << 8 | from[7]) : 0u;
        var at = _engine?.Services.Mono.NowNs ?? 0;
        _work.Writer.TryWrite(new Work(WorkKind.Sacn, default, packet.ToArray(), null, address) { AtNs = at });
    }

    /// <summary>The sACN surface a source is: the first enabled one whose match is empty, its address or its CID.</summary>
    private string SacnSurfaceOf(uint address, Guid cid)
    {
        var ip = new IPAddress(BitConverterBig(address)).ToString();
        return _surfaces.FirstOrDefault(s => s.Kind == "sacn" && s.Enabled && (s.Match.Length == 0 || s.Match == ip
            || (Guid.TryParse(s.Match, out var g) && g == cid)))?.Name ?? "";
    }

    private async Task SacnAsync(byte[] packet, uint from, long now, CancellationToken ct)
    {
        var engine = _engine!;
        var settings = engine.ControlInput.Settings;
        if (!settings.SacnEnabled)
            return;
        // A source the node does not allow (by its address or its CID) takes no part in the merge.
        if (settings.DmxAllow.Length > 0 && !settings.DmxAllow.Contains(new IPAddress(BitConverterBig(from)).ToString(), StringComparer.Ordinal)
            && !(packet.Length >= 38 && settings.DmxAllow.Any(a => Guid.TryParse(a, out var g) && g == new Guid(packet.AsSpan(22, 16), bigEndian: true))))
            return;
        _sacnIn.LearnAll = engine.ControlInput.Learning is not null;
        _sacnIn.AcceptPreview = _surfaces.Any(s => s.Kind == "sacn" && s.Enabled && s.AcceptPreview);
        var events = new List<InputEvent>();
        _sacnIn.Receive(packet, from, now, events);
        foreach (var e in events)
            await engine.ControlInput.ProcessAsync(e, ct).ConfigureAwait(false);
    }

    private void WriteSacnState(Utf8JsonWriter w)
    {
        var settings = _engine?.ControlInput.Settings ?? ControlSettings.Default;
        w.WriteStartObject();
        w.WriteBoolean("enabled", settings.SacnEnabled);
        w.WriteBoolean("open", _sacn?.IsOpen == true);
        w.WriteString("interface", settings.SacnInterface.Length == 0 ? "all" : settings.SacnInterface);
        if (SacnProblem is { } problem)
            w.WriteString("words", problem);
        w.WriteStartArray("universes");
        foreach (var u in _sacn?.Joined ?? [])
            w.WriteNumberValue(u);
        w.WriteEndArray();
        w.WriteStartArray("sources");
        foreach (var (universe, sources) in _sacnIn.Sources.OrderBy(kv => kv.Key))
        {
            foreach (var src in sources)
            {
                w.WriteStartObject();
                w.WriteNumber("universe", universe);
                w.WriteString("cid", src.Cid.ToString("D"));
                w.WriteString("name", src.Name);
                w.WriteString("address", new IPAddress(BitConverterBig(src.Address)).ToString());
                w.WriteNumber("priority", src.Priority);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteNumber("frames", _sacnIn.Frames);
        w.WriteNumber("dropped", _sacnIn.Dropped);
        w.WriteNumber("previews", _sacnIn.Previews);
        w.WriteEndObject();
    }

    // ---- timecode inputs ----

    private void ReconcileTimecode(ProjectSnapshot shown)
    {
        var engine = _engine!;
        var project = shown.Project;
        // The board ignores a show older than one the lane has reconciled it to since this one was read.
        engine.TimecodeSources.Reconcile(shown);
        var ltc = project.Timecode.Sources.Where(s => s.Kind == TimecodeKind.Ltc).ToList();
        foreach (var (id, input) in _ltc.ToList())
        {
            var s = ltc.FirstOrDefault(x => x.Id == id);
            if (s is not null && s.Device == input.Source.Device && s.Channel == input.Source.Channel && s.FloorDbfs == input.Source.FloorDbfs)
                continue;
            _ltc.TryRemove(id, out _);
            // Deaf at once; its device is given back off the loop, perhaps behind another device's open.
            input.Close();
            OffLoop(input.Dispose);
        }
        foreach (var s in ltc.Where(s => !_ltc.ContainsKey(s.Id)))
        {
            var input = new LtcInput(s, _options.Audio ?? engine.Services.AudioInputs, engine.TimecodeSources, engine.Services.Mono);
            _ltc[s.Id] = input;
            OffLoop(input.Open);
        }
        foreach (var id in _mtc.Keys.Where(k => project.FindTimecodeSource(k) is not { Kind: TimecodeKind.Mtc }).ToList())
            _mtc.TryRemove(id, out _);
        foreach (var s in project.Timecode.Sources.Where(s => s.Kind == TimecodeKind.Mtc))
        {
            _mtc.GetOrAdd(s.Id, _ => new MtcParser());
            var heard = Midi.Ports().Any(p => p.IsInput && PortGlob.Matches(s.Device, p.Name));
            engine.TimecodeSources.Input(s.Id, heard, -120, heard ? "" : Midi.Unavailable is { } why ? "MIDI: " + why : $"no MIDI port {s.Device} on this machine");
        }
        foreach (var s in project.Timecode.Sources.Where(s => s.Kind == TimecodeKind.ArtNet))
            engine.TimecodeSources.Input(s.Id, _options.ArtNet is not null && _options.ArtNet.Problem is null, -120,
                _options.ArtNet is null ? "no Art-Net socket on this node" : _options.ArtNet.Problem ?? "");
    }

    /// <summary>An LTC input's open or close, off the control loop (the audio hub holds its lock while a device opens, and a device
    /// can take seconds): one at a time, in the order asked, so an input's close always follows its open. STATE moves once it is
    /// done (the source's input is open, or says why not).</summary>
    private void OffLoop(Action work)
    {
        lock (_ltcGate)
        {
            _ltcWork = _ltcWork.ContinueWith(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _engine?.Journal.Add("fault", "control: an LTC input: " + Journal.Describe(ex), "control", null, false);
                }
                _engine?.FactsChanged();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Mtc(string port, int piece, int value, long monoNs)
    {
        var engine = _engine;
        if (engine is null)
            return;
        foreach (var s in engine.Store.Project.Timecode.Sources.Where(s => s.Kind == TimecodeKind.Mtc && PortGlob.Matches(s.Device, port)))
        {
            var parser = _mtc.GetOrAdd(s.Id, _ => new MtcParser());
            var obs = new List<TcObservation>();
            lock (parser)
                parser.QuarterFrame(piece, value, monoNs - s.LatencyNs, obs);
            if (obs.Count > 0)
                engine.TimecodeSources.Observe(s.Id, obs);
        }
    }

    private void MtcLocate(string port, int rateCode, int h, int m, int s, int f, long monoNs)
    {
        if (_engine is not { } engine || MtcParser.FullFrame(rateCode, h, m, s, f, monoNs) is not { } obs)
            return;
        foreach (var src in engine.Store.Project.Timecode.Sources.Where(x => x.Kind == TimecodeKind.Mtc && PortGlob.Matches(x.Device, port)))
            engine.TimecodeSources.Observe(src.Id, obs);
    }

    private void ArtTimeCodeIn(ArtTimeCode tc, long monoNs)
    {
        var engine = _engine!;
        var format = TcFormat.OfRateCode(tc.Type);
        foreach (var s in engine.Store.Project.Timecode.Sources.Where(x => x.Kind == TimecodeKind.ArtNet && x.Channel == tc.StreamId))
        {
            var f = TcFormat.TryParse(s.Format, out var configured) ? configured : format;
            if (f.TryFrame(tc.Hours, tc.Minutes, tc.Seconds, tc.Frames, out var frame))
                engine.TimecodeSources.Observe(s.Id, new TcObservation(monoNs - s.LatencyNs, frame, 0, format, 1, TcObsKind.Packet, 0, false));
        }
    }

    // ---- what the engine reads ----

    /// <summary>Whether a surface's port or socket is here and open.</summary>
    public bool Connected(ControlSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return surface.Kind switch
        {
            "midi" => PortOf(surface) is not null,
            "osc" => OscOpen,
            "artnet" => ArtNetListening,
            "sacn" => SacnListening,
            _ => false,
        };
    }

    /// <summary>Why a surface's port or socket was refused while bindings need it, or null.</summary>
    public string? RefusedFor(ControlSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return surface.Kind switch
        {
            "osc" => OscProblem,
            "artnet" => ArtNetProblem,
            "sacn" => SacnProblem,
            "midi" => MidiRefused(surface),
            _ => null,
        };
    }

    /// <summary>Whether an input's source can be heard now: true (its socket or port is open), false (its surface's port is not
    /// here), null (nothing says yet).</summary>
    public bool? Hears(InputAddress input)
    {
        switch (input.Source)
        {
            case InputSource.Osc:
                return OscOpen;
            case InputSource.ArtNet:
                return ArtNetListening ? true : null;
            case InputSource.Sacn:
                return SacnListening ? true : null;
            case InputSource.Midi:
                if (input.Surface.Length == 0)
                {
                    lock (_midiInputs)
                        return _midiInputs.Count > 0 ? true : null;
                }
                return _surfaces.FirstOrDefault(s => s.Name.Equals(input.Surface, StringComparison.OrdinalIgnoreCase)) is { } s && PortOf(s) is not null;
            default:
                return false;
        }
    }

    public IReadOnlyList<NodeFault> Faults()
    {
        var faults = new List<NodeFault>();
        if (_oscProblem is { } osc)
            faults.Add(new NodeFault("osc", osc));
        if (ArtNetProblem is { } art)
            faults.Add(new NodeFault("artnet", art));
        if (SacnProblem is { } sacn)
            faults.Add(new NodeFault("sacn", sacn));
        if (Midi.Unavailable is { } midi && _surfaces.Any(s => s.Kind == "midi" && s.Enabled))
            faults.Add(new NodeFault("midi", midi));
        return faults;
    }

    /// <summary>CONTROL PORTS: the MIDI backend and its ports, and the OSC and Art-Net sockets.</summary>
    public void WritePorts(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        WriteMidiBody(w);
        w.WritePropertyName("osc");
        WriteOscState(w);
        w.WritePropertyName("artnet");
        WriteArtNetState(w);
        w.WritePropertyName("sacn");
        WriteSacnState(w);
        w.WriteEndObject();
    }

    /// <summary>STATE.control's osc, midi and dmx parts.</summary>
    public void WriteState(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WritePropertyName("osc");
        WriteOscState(w);
        w.WriteStartObject("midi");
        WriteMidiBody(w);
        w.WriteEndObject();
        w.WriteStartObject("dmx");
        w.WritePropertyName("artnet");
        WriteArtNetState(w);
        w.WritePropertyName("sacn");
        WriteSacnState(w);
        w.WriteEndObject();
    }

    private void WriteOscState(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("port", _osc.Local?.Port ?? _engine?.ControlInput.Settings.OscPort ?? 0);
        w.WriteBoolean("open", _osc.IsOpen);
        w.WriteNumber("senders", _oscSenders.Count);
        w.WriteNumber("refused", Interlocked.Read(ref _oscRefused));
        w.WriteNumber("malformed", Interlocked.Read(ref _oscMalformed));
        w.WriteStartArray("feedback");
        foreach (var f in _engine?.ControlInput.Settings.OscFeedback ?? [])
            w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteNumber("clients", _feedback.ClientCount);
        w.WriteNumber("feedbackSent", _feedback.Sent);
        w.WriteNumber("echoes", _feedback.Echoes);
        w.WriteString("words", _oscProblem ?? (_osc.IsOpen ? "" : "OSC input is off"));
        w.WriteEndObject();
    }

    private void WriteMidiBody(Utf8JsonWriter w)
    {
        w.WriteString("backend", Midi.Name);
        List<MidiPort> open;
        lock (_midiInputs)
            open = [.. _midiInputs.Values];
        w.WriteBoolean("open", Midi.Unavailable is null);
        var ports = Midi.Ports();
        w.WriteString("words", Midi.Unavailable ?? (_midiWords.Length > 0 ? _midiWords : ports.Count == 0 ? "no MIDI ports on this machine" : ""));
        w.WriteStartArray("inputs");
        foreach (var p in ports.Where(p => p.IsInput))
        {
            var o = open.FirstOrDefault(x => x.Info.Id == p.Id);
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            if (o is { Surface.Length: > 0 })
                w.WriteString("surface", _surfaces.FirstOrDefault(s => s.Name == o.Surface)?.Id ?? o.Surface);
            else
                w.WriteNull("surface");
            w.WriteBoolean("open", o is not null);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("outputs");
        w.WriteEndArray();
    }

    private void WriteArtNetState(Utf8JsonWriter w)
    {
        var settings = _engine?.ControlInput.Settings ?? ControlSettings.Default;
        w.WriteStartObject();
        w.WriteBoolean("enabled", settings.ArtNetEnabled);
        w.WriteBoolean("open", ArtNetOpen);
        w.WriteString("interface", settings.ArtNetInterface.Length == 0 ? "all" : settings.ArtNetInterface);
        if (ArtNetProblem is { } problem)
            w.WriteString("words", problem);
        w.WriteNumber("frames", _artIn.Frames);
        w.WriteNumber("dropped", _artIn.Dropped);
        w.WriteNumber("polls", _artIn.Polls);
        w.WriteEndObject();
    }

    private enum WorkKind : byte { Event, Osc, ArtNet, Tick, Settings, Ports, Want, Sacn, WantSacn }

    /// <summary>One piece of the loop's work: an input event, an OSC datagram, an Art-Net or sACN packet, the tick, a settings or port
    /// change, or the DMX channels the bindings want.</summary>
    private readonly record struct Work(WorkKind Kind, InputEvent Event, byte[]? Bytes, IPEndPoint? From, uint Address, DmxChannel[]? Channels = null)
    {
        /// <summary>When a packet arrived (mono ns).</summary>
        public long AtNs { get; init; }

        public static readonly Work Tick = new(WorkKind.Tick, default, null, null, 0);
        public static readonly Work Settings = new(WorkKind.Settings, default, null, null, 0);
        public static readonly Work Ports = new(WorkKind.Ports, default, null, null, 0);
    }
}
