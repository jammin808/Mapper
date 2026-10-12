namespace PlayMap.Net;

/// <summary>The connections a listener holds, in all and per address. Admission and release are paired by the
/// caller (admit in the accept path, release in the connection's finally), so the counts never drift.</summary>
public sealed class ConnectionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _byAddress = new(StringComparer.Ordinal);
    private int _open;

    public int Open
    {
        get
        {
            lock (_gate)
                return _open;
        }
    }

    public int From(string address)
    {
        lock (_gate)
            return _byAddress.TryGetValue(address, out var n) ? n : 0;
    }

    /// <summary>Counts the connection in when both ceilings allow it; false (nothing counted) when either is reached.</summary>
    public bool TryAdmit(string address, int maxTotal, int maxPerAddress)
    {
        lock (_gate)
        {
            var mine = _byAddress.TryGetValue(address, out var n) ? n : 0;
            if (_open >= maxTotal || mine >= maxPerAddress)
                return false;
            _byAddress[address] = mine + 1;
            _open++;
            return true;
        }
    }

    public void Release(string address)
    {
        lock (_gate)
        {
            if (!_byAddress.TryGetValue(address, out var n))
                return;
            if (n <= 1)
                _byAddress.Remove(address);
            else
                _byAddress[address] = n - 1;
            _open = Math.Max(0, _open - 1);
        }
    }
}

/// <summary>The wire's ceilings (Patterns' numbers): connections in all and per address, the bytes and seconds a
/// line may take, the replies a connection may leave unread, how long one write may take, the wrong proofs a
/// connection may send before it is closed.</summary>
public sealed record WireLimits(
    int MaxConnections = 64,
    int MaxPerAddress = 16,
    int MaxLineBytes = 64 * 1024,
    double LineSeconds = 10,
    int MaxQueuedReplies = 256,
    double WriteSeconds = 10,
    int WrongProofsBeforeClose = 5)
{
    public static readonly WireLimits Default = new();

    /// <summary>The line a refused connection reads before the door closes: which ceiling, and what to do.</summary>
    public string BusyWords(int fromAddress) => fromAddress >= MaxPerAddress
        ? $"busy — {MaxPerAddress} connections already open from this address; close one first"
        : $"busy — {MaxConnections} connections already open on this port; close one first";
}
