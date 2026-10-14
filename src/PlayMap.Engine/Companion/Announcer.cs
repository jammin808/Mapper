using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Engine.Host;
using PlayMap.Net;
using PlayMap.Net.Discovery;

namespace PlayMap.Engine.Companion;

/// <summary>The node on the local network as <c>_playmap._tcp</c> (Companion's bonjour-device list): its name, the web port (the line
/// channel is <c>/ws</c> there) and TXT keys id, name, proto, ws and wire. It announces once the web port is open, on the node's bind
/// address; a responder that cannot open is a fault in STATE with words, and the node runs on.</summary>
internal sealed class Announcer : INodeFaults, IAsyncDisposable
{
    public const string ServiceType = "_playmap._tcp";

    private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(30);

    private readonly MdnsResponder _responder;
    private readonly Func<ServiceRecord?> _record;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _announce;
    private volatile string? _fault;

    public Announcer(ExtensionSetup setup, EngineCore engine)
        : this(OptionsFor(setup), () => RecordFor(setup.Network.Settings, setup.Network.HttpPort, setup.Network.WirePort),
            setup.Logs.CreateLogger("PlayMap.Discovery"))
    {
        ArgumentNullException.ThrowIfNull(engine);
    }

    internal Announcer(MdnsOptions options, Func<ServiceRecord?> record, ILogger? log = null)
    {
        _responder = new MdnsResponder(options);
        _record = record;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>The responder (its records as renamed after a conflict).</summary>
    internal MdnsResponder Responder => _responder;

    public IReadOnlyList<NodeFault> Faults() => _fault is { } f ? [new NodeFault("mdns", f)] : [];

    public void Start()
    {
        if (!_responder.Start())
        {
            _fault = _responder.Fault;
            return;
        }
        _announce = Task.Run(() => AnnounceAsync(_stop.Token), CancellationToken.None);
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        ServiceRecord? record;
        while ((record = _record()) is null)
        {
            if (waited.Elapsed > PortWait)
            {
                _fault = "the network announcement (mDNS) has nothing to announce: the web port did not open";
                return;
            }
            if (!await Waits.DelayAsync(TimeSpan.FromMilliseconds(100), ct))
                return;
        }
        _responder.Announce(record);
        _log.LogInformation("mDNS: announcing {Instance}.{Type} on port {Port}.", record.Instance, record.Type, record.Port);
    }

    /// <summary>The node's service record once its web port is open (null before).</summary>
    public static ServiceRecord? RecordFor(PlayMap.Core.Bus.NodeSettings settings, int httpPort, int wirePort)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (httpPort <= 0)
            return null;
        var txt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = Fit("id", settings.NodeId),
            ["name"] = Fit("name", settings.Name),
            ["proto"] = "1",
            ["ws"] = "/ws",
            ["wire"] = wirePort.ToString(CultureInfo.InvariantCulture),
        };
        return new ServiceRecord(settings.Name, ServiceType, httpPort, txt);
    }

    /// <summary>A TXT string ("key=value") is at most 255 bytes (RFC 6763 §6.1): a longer value is cut at a character boundary.</summary>
    private static string Fit(string key, string value)
    {
        var room = 255 - System.Text.Encoding.UTF8.GetByteCount(key) - 1;
        var v = value;
        while (System.Text.Encoding.UTF8.GetByteCount(v) > room)
            v = char.IsLowSurrogate(v[^1]) && v.Length > 1 ? v[..^2] : v[..^1];
        return v;
    }

    private static MdnsOptions OptionsFor(ExtensionSetup setup)
    {
        IPAddress bind;
        try
        {
            bind = NetAddress.ParseBind(setup.Settings.Bind) ?? IPAddress.Any;
        }
        catch (BindRefusedException)
        {
            bind = IPAddress.None;   // a bind address that is not one: the responder's bind fails and says so
        }
        return new MdnsOptions { Bind = bind, Log = setup.Logs.CreateLogger("PlayMap.Discovery") };
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_announce is not null)
            await _announce;
        await _responder.DisposeAsync();
        _stop.Dispose();
    }
}
