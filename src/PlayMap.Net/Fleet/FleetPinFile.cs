using Microsoft.Extensions.Logging;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Fleet;

/// <summary>The fleet's pins as their file holds them now. The file is read again whenever it was written since it was last read, so
/// an operator who removes a reinstalled machine's line sees it pinned again on its next record without a restart, and a pin the
/// node sets after the edit is written over the edited file, never over the old one. Fingerprints are public: the file holds no
/// secret.</summary>
public sealed class FleetPinFile(string path, ILogger? log = null)
{
    private readonly Lock _gate = new();
    private PinStore? _store;
    private DateTime _stamp;

    public string FilePath => path;

    /// <summary>The pins now (read again when the file changed since).</summary>
    public PinStore Current()
    {
        var stamp = Stamp();
        lock (_gate)
        {
            if (_store is null || stamp != _stamp)
            {
                _store = new PinStore(path, log);
                _stamp = Stamp();
            }
            return _store;
        }
    }

    private DateTime Stamp()
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
