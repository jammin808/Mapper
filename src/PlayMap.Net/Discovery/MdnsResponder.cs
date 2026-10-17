using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PlayMap.Net.Discovery;

/// <summary>A DNS-SD service this node offers: its instance name (what a list shows), its type ("_playmap._tcp"), its port and its TXT
/// keys.</summary>
public sealed record ServiceRecord(string Instance, string Type, int Port, IReadOnlyDictionary<string, string> Txt);

/// <summary>Who announces this node's services on the local network; other packages add their records to the same one.</summary>
public interface IServiceAnnouncer : IAsyncDisposable
{
    void Announce(ServiceRecord record);

    void Withdraw(string instance, string type);
}

/// <summary>How the responder runs; the defaults are RFC 6762's.</summary>
public sealed record MdnsOptions
{
    /// <summary>The address it binds (the node's bind address; any = every interface).</summary>
    public IPAddress Bind { get; init; } = IPAddress.Any;

    public int Port { get; init; } = MdnsResponder.MdnsPort;

    /// <summary>Where multicast queries and announcements go (224.0.0.251:5353).</summary>
    public IPEndPoint Group { get; init; } = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>This machine's name in ".local" (the A record's name).</summary>
    public string Host { get; init; } = MdnsResponder.HostLabel(Environment.MachineName);

    /// <summary>The addresses the A records carry; null = the bound address, or every up IPv4 address when bound to any.</summary>
    public Func<IReadOnlyList<IPAddress>>? Addresses { get; init; }

    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>TTL of host and SRV records (RFC 6762 §10: 120 s); PTR and TXT use <see cref="OtherTtl"/>.</summary>
    public uint HostTtl { get; init; } = 120;

    public uint OtherTtl { get; init; } = 4500;

    public ILogger Log { get; init; } = NullLogger.Instance;
}

/// <summary>A minimal mDNS / DNS-SD responder (RFC 6762, RFC 6763) for the node's own services: it answers PTR for its service types
/// and for <c>_services._dns-sd._udp.local</c>, SRV and TXT for its instances, and A for its host — by multicast, by unicast to a
/// querier that asked for it, and to a legacy querier (a source port other than 5353) by unicast with its id echoed and TTLs of at most
/// 10 s (§6.7). It probes three times before it announces twice; a name another host answers for becomes "&lt;name&gt; (2)" (§9);
/// it says goodbye (TTL 0) when disposed. It shares the port with Avahi and Bonjour (address reuse). A bind that fails is a fault
/// in words (<see cref="Fault"/>), never a throw.</summary>
public sealed class MdnsResponder : IServiceAnnouncer
{
    public const string ServicesName = "_services._dns-sd._udp.local";

    /// <summary>mDNS's port (RFC 6762 §3).</summary>
    public const int MdnsPort = 5353;
    private const uint LegacyTtl = 10;

    private readonly MdnsOptions _o;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private Task? _receive;
    private int _disposed;

    public MdnsResponder(MdnsOptions? options = null)
    {
        _o = options ?? new MdnsOptions();
        _log = _o.Log;
    }

    /// <summary>Why the responder is not answering (its socket would not bind), or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>The port it listens on once started.</summary>
    public int LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <summary>The records it answers for now, by their current names (a conflict renames them).</summary>
    public IReadOnlyList<ServiceRecord> Records
    {
        get
        {
            lock (_gate)
                return [.. _entries.Select(e => e.Record)];
        }
    }

    /// <summary>A record's instance was renamed after a conflict (old, new).</summary>
    public event Action<string, string>? Renamed;

    /// <summary>Opens the socket: false with <see cref="Fault"/> when the machine refuses it.</summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_socket is not null)
            return true;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(_o.Bind, _o.Port));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            Fault = string.Create(CultureInfo.InvariantCulture,
                $"the network announcement (mDNS) could not open {_o.Bind}:{_o.Port} — {ex.Message}; Companion lists this node only when added by its address");
            _log.LogWarning(ex, "mDNS: {Fault}", Fault);
            return false;
        }
        JoinGroup(socket);
        _socket = socket;
        _receive = Task.Run(() => ReceiveAsync(socket, _stop.Token), CancellationToken.None);
        return true;
    }

    /// <summary>Joins the group on every up IPv4 interface (or the bound one); an interface that refuses is skipped.</summary>
    private void JoinGroup(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "mDNS: the multicast TTL could not be set.");
        }
        if (_o.Group.Address.AddressFamily != AddressFamily.InterNetwork || (_o.Group.Address.GetAddressBytes()[0] & 0xF0) != 0xE0)
            return;   // a unicast "group" (a test's own socket) has nothing to join
        var addresses = _o.Bind.Equals(IPAddress.Any) ? NetAddress.LanAddresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork) : [_o.Bind];
        foreach (var a in addresses)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_o.Group.Address, a));
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "mDNS: {Address} did not join the group.", a);
            }
        }
    }

    public void Announce(ServiceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Entry entry;
        lock (_gate)
        {
            _entries.RemoveAll(e => DnsMessage.SameName(e.Record.Type, record.Type) && e.Base == record.Instance);
            entry = new Entry(record with { Instance = InstanceLabel(record.Instance) }, record.Instance);
            _entries.Add(entry);
        }
        _ = Task.Run(() => ProbeAndAnnounceAsync(entry, _stop.Token), CancellationToken.None);
    }

    public void Withdraw(string instance, string type)
    {
        List<Entry> gone;
        lock (_gate)
        {
            gone = _entries.Where(e => e.Base == instance && DnsMessage.SameName(e.Record.Type, type)).ToList();
            _entries.RemoveAll(gone.Contains);
        }
        foreach (var e in gone)
            Send(Response(0, RecordsOf(e, goodbye: true), [], legacy: false), _o.Group);
    }

    private async Task ProbeAndAnnounceAsync(Entry entry, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var name = entry.Record.Instance;
                entry.State = EntryState.Probing;
                for (var i = 0; i < 3 && entry.State == EntryState.Probing; i++)
                {
                    var probe = new DnsMessage(0, false, false, [new DnsQuestion(FullName(entry.Record), DnsType.Any, UnicastResponse: true)],
                        [], RecordsOf(entry, goodbye: false).Where(r => r.Type is DnsType.Srv or DnsType.Txt).ToList(), []);
                    Send(probe.Write(), _o.Group);
                    if (!await Waits.DelayAsync(_o.ProbeInterval, ct))
                        return;
                }
                if (entry.State == EntryState.Conflict || entry.Record.Instance != name)
                    continue;   // renamed while probing: probe the new name
                entry.State = EntryState.Announced;
                for (var i = 0; i < 2 && entry.State == EntryState.Announced; i++)
                {
                    Send(Response(0, RecordsOf(entry, goodbye: false), [], legacy: false), _o.Group);
                    if (i == 0 && !await Waits.DelayAsync(_o.AnnounceInterval, ct))
                        return;
                }
                return;   // a conflict after this probes its new name on its own
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or ArgumentException)
        {
            _log.LogDebug(ex, "mDNS: announcing {Instance} stopped.", entry.Record.Instance);
        }
    }

    private async Task ReceiveAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // One bad datagram (an ICMP-reported unreachable peer) is that peer's; the responder keeps listening.
                _log.LogDebug(ex, "mDNS: a receive failed.");
                continue;
            }
            DnsMessage message;
            try
            {
                message = DnsMessage.Read(buffer.AsSpan(0, r.ReceivedBytes));
            }
            catch (InvalidDataException ex)
            {
                _log.LogDebug(ex, "mDNS: a packet that is not DNS was dropped.");
                continue;
            }
            try
            {
                Take(message, (IPEndPoint)r.RemoteEndPoint);
            }
            catch (ArgumentException ex)
            {
                // A record of ours that cannot be written (a TXT string past 255 bytes) is that answer's: the responder keeps listening.
                _log.LogWarning(ex, "mDNS: an answer could not be written.");
            }
        }
    }

    /// <summary>One message from the network: a response may conflict with a name of ours; a query is answered.</summary>
    internal void Take(DnsMessage message, IPEndPoint from)
    {
        if (message.IsResponse)
        {
            Conflicts(message);
            return;
        }
        // A legacy querier is one whose source port is not mDNS's own (RFC 6762 §6.7), whatever port this responder is on.
        var legacy = from.Port != MdnsPort;
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        var unicast = legacy;
        foreach (var q in message.Questions)
        {
            unicast |= q.UnicastResponse;
            Answer(q, answers, additionals);
        }
        if (answers.Count == 0)
            return;
        additionals.RemoveAll(a => answers.Any(x => x.Name == a.Name && x.Type == a.Type));
        var bytes = legacy
            ? new DnsMessage(message.Id, true, true, message.Questions, Legacy(answers), [], Legacy(additionals)).Write()
            : Response(0, answers, additionals, legacy: false);
        Send(bytes, unicast ? from : _o.Group);
    }

    private static List<DnsRecord> Legacy(List<DnsRecord> records) =>
        [.. records.Select(r => r with { Ttl = Math.Min(r.Ttl, LegacyTtl), CacheFlush = false })];

    private void Answer(DnsQuestion q, List<DnsRecord> answers, List<DnsRecord> additionals)
    {
        List<Entry> entries;
        lock (_gate)
            entries = [.. _entries.Where(e => e.State == EntryState.Announced)];
        var any = q.Type == DnsType.Any;
        foreach (var e in entries)
        {
            var records = RecordsOf(e, goodbye: false);
            var typeName = TypeName(e.Record);
            if (DnsMessage.SameName(q.Name, ServicesName) && (any || q.Type == DnsType.Ptr))
            {
                Add(answers, DnsRecord.Ptr(ServicesName, typeName, _o.OtherTtl));
            }
            else if (DnsMessage.SameName(q.Name, typeName) && (any || q.Type == DnsType.Ptr))
            {
                Add(answers, records.First(r => r.Type == DnsType.Ptr));
                foreach (var r in records.Where(r => r.Type != DnsType.Ptr))
                    Add(additionals, r);
            }
            else if (DnsMessage.SameName(q.Name, FullName(e.Record)))
            {
                foreach (var r in records.Where(r => r.Type is DnsType.Srv or DnsType.Txt && (any || r.Type == q.Type)))
                    Add(answers, r);
                foreach (var r in records.Where(r => r.Type is DnsType.A))
                    Add(additionals, r);
            }
            else if (DnsMessage.SameName(q.Name, HostName) && (any || q.Type == DnsType.A))
            {
                foreach (var r in records.Where(r => r.Type == DnsType.A))
                    Add(answers, r);
            }
        }
    }

    private static void Add(List<DnsRecord> list, DnsRecord r)
    {
        if (!list.Any(x => x.Name == r.Name && x.Type == r.Type && x.Data.Span.SequenceEqual(r.Data.Span)))
            list.Add(r);
    }

    /// <summary>A response from another host that holds one of our instance names with other data is a conflict (RFC 6762 §9): while
    /// probing any answer for the name is; once announced, only a different SRV or TXT is (our own echo is not).</summary>
    private void Conflicts(DnsMessage message)
    {
        List<(Entry Entry, string Old, string New)> renamed = [];
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                var full = FullName(e.Record);
                var ours = RecordsOf(e, goodbye: false);
                var clash = message.Answers.Concat(message.Additionals).Any(r =>
                    DnsMessage.SameName(r.Name, full) && r.Type is DnsType.Srv or DnsType.Txt or DnsType.Any
                    && (e.State == EntryState.Probing || !ours.Any(o => o.Type == r.Type && o.Data.Span.SequenceEqual(r.Data.Span))));
                if (!clash)
                    continue;
                var old = e.Record.Instance;
                e.Generation++;
                e.Record = e.Record with { Instance = InstanceLabel(e.Base) + string.Create(CultureInfo.InvariantCulture, $" ({e.Generation + 1})") };
                var wasAnnounced = e.State == EntryState.Announced;
                e.State = EntryState.Conflict;
                renamed.Add((e, old, e.Record.Instance));
                if (wasAnnounced)
                    _ = Task.Run(() => ProbeAndAnnounceAsync(e, _stop.Token), CancellationToken.None);
            }
        }
        foreach (var (_, old, now) in renamed)
        {
            _log.LogInformation("mDNS: another host answers for {Old}; this node is {New} now.", old, now);
            Renamed?.Invoke(old, now);
        }
    }

    private string HostName => _o.Host + ".local";

    private static string TypeName(ServiceRecord r) => r.Type.TrimEnd('.') + ".local";

    private static string FullName(ServiceRecord r) => r.Instance + "." + TypeName(r);

    private List<DnsRecord> RecordsOf(Entry e, bool goodbye)
    {
        var r = e.Record;
        var full = FullName(r);
        var hostTtl = goodbye ? 0 : _o.HostTtl;
        var otherTtl = goodbye ? 0 : _o.OtherTtl;
        var list = new List<DnsRecord>
        {
            DnsRecord.Ptr(TypeName(r), full, otherTtl),
            DnsRecord.Srv(full, HostName, (ushort)r.Port, hostTtl, flush: !goodbye),
            DnsRecord.Txt(full, [.. r.Txt.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)], otherTtl, flush: !goodbye),
        };
        foreach (var a in Addresses())
            list.Add(DnsRecord.A(HostName, a, hostTtl, flush: !goodbye));
        return list;
    }

    private IReadOnlyList<IPAddress> Addresses()
    {
        if (_o.Addresses is { } f)
            return f();
        if (!_o.Bind.Equals(IPAddress.Any))
            return [_o.Bind];
        return [.. NetAddress.LanAddresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork)];
    }

    private static byte[] Response(ushort id, IReadOnlyList<DnsRecord> answers, IReadOnlyList<DnsRecord> additionals, bool legacy) =>
        new DnsMessage(id, true, true, [], legacy ? Legacy([.. answers]) : answers, [], legacy ? Legacy([.. additionals]) : additionals).Write();

    private void Send(byte[] bytes, IPEndPoint to)
    {
        var socket = _socket;
        if (socket is null)
            return;
        try
        {
            socket.SendTo(bytes, to);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "mDNS: a send to {To} failed.", to);
        }
        catch (ObjectDisposedException)
        {
            // Closing: nothing more to say.
        }
    }

    /// <summary>An instance name as one label: dots become dashes, at most 63 bytes.</summary>
    public static string InstanceLabel(string name)
    {
        var t = (name ?? "").Replace('.', '-').Trim();
        if (t.Length == 0)
            t = "PlayMap";
        while (System.Text.Encoding.UTF8.GetByteCount(t) > 58)
            t = t[..^1];
        return t;
    }

    /// <summary>A host name as one DNS label: letters, digits and dashes.</summary>
    public static string HostLabel(string name)
    {
        var chars = (name ?? "").Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var t = new string(chars).Trim('-');
        if (t.Length == 0)
            t = "playmap";
        return t.Length > 63 ? t[..63] : t;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        List<Entry> all;
        lock (_gate)
            all = [.. _entries.Where(e => e.State == EntryState.Announced)];
        foreach (var e in all)
            Send(Response(0, RecordsOf(e, goodbye: true), [], legacy: false), _o.Group);
        await _stop.CancelAsync();
        _socket?.Dispose();
        if (_receive is not null)
        {
            try
            {
                await _receive.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (TimeoutException ex)
            {
                _log.LogDebug(ex, "mDNS: the receive loop was still closing.");
            }
        }
        _stop.Dispose();
    }

    private enum EntryState
    {
        Probing,
        Conflict,
        Announced,
    }

    private sealed class Entry(ServiceRecord record, string baseName)
    {
        public ServiceRecord Record { get; set; } = record;

        public string Base { get; } = baseName;

        public int Generation { get; set; }

        public volatile EntryState State = EntryState.Probing;
    }
}
