using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Storage;

namespace PlayMap.Net.Tls;

/// <summary>What a client makes of a server's certificate (Show Bus §10).</summary>
public enum PinVerdict
{
    /// <summary>The certificate pinned for this node: go on.</summary>
    Pinned,
    /// <summary>Nothing pinned and nothing contradicts it: go on, and pin it once the link has authenticated.</summary>
    FirstUse,
    /// <summary>A pin is held and the server shows another certificate: refused until the operator accepts it.</summary>
    Changed,
    /// <summary>The hello named one certificate and the server showed another (or none usable): refused.</summary>
    Mismatch,
}

/// <summary>App-to-app TLS is trusted by pin, never by a name or an authority (Show Bus §10). The hello is
/// unauthenticated, so the pin — set on the first link that authenticated — is the trust, and a change is refused
/// with CERTIFICATE CHANGED until the operator accepts it (as SSH does). Pins are kept per node id
/// ("node:&lt;id&gt;") or, for a typed address no hello names, per address ("addr:&lt;host&gt;:&lt;port&gt;"), in
/// a small JSON file written atomically (fingerprints are public: no secret is kept here).</summary>
public sealed class PinStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pins = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly ILogger _log;

    /// <param name="path">The file the pins live in; null keeps them in memory (tests, a node with no folder).</param>
    public PinStore(string? path = null, ILogger? log = null)
    {
        _path = path;
        _log = log ?? NullLogger.Instance;
        Load();
    }

    public static string NodeKey(string nodeId) => "node:" + nodeId;

    public static string AddressKey(string host, int port) =>
        "addr:" + (host ?? "").Trim().ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture);

    public string? Get(string key)
    {
        lock (_gate)
            return _pins.TryGetValue(key, out var f) ? f : null;
    }

    /// <param name="key">Whose pin: <see cref="NodeKey"/> or <see cref="AddressKey"/>.</param>
    /// <param name="presented">The fingerprint of the certificate the server showed.</param>
    /// <param name="advertised">The fingerprint the node's hello carries (null or "" for a typed address).</param>
    public PinVerdict Check(string key, string presented, string? advertised)
    {
        if (!TlsFingerprint.IsValid(presented))
            return PinVerdict.Mismatch;
        var pinned = Get(key);
        if (TlsFingerprint.IsValid(pinned))
            return pinned == presented ? PinVerdict.Pinned : PinVerdict.Changed;
        var hello = TlsFingerprint.Normal(advertised);
        if (hello.Length > 0 && hello != presented)
            return PinVerdict.Mismatch;
        return PinVerdict.FirstUse;
    }

    public static bool Accepts(PinVerdict verdict) => verdict is PinVerdict.Pinned or PinVerdict.FirstUse;

    /// <summary>Pins a fingerprint: after the first authenticated answer over it, or when the operator accepts a change.</summary>
    public void Pin(string key, string fingerprint)
    {
        var f = TlsFingerprint.Normal(fingerprint);
        if (f.Length == 0)
            throw new ArgumentException("a pin is sha256: and 64 hex characters", nameof(fingerprint));
        lock (_gate)
        {
            if (_pins.TryGetValue(key, out var held) && held == f)
                return;
            _pins[key] = f;
            Save();
        }
    }

    public bool Forget(string key)
    {
        lock (_gate)
        {
            if (!_pins.Remove(key))
                return false;
            Save();
            return true;
        }
    }

    /// <summary>The words for a link's status line.</summary>
    public static string Words(PinVerdict verdict, string name, string presented) => verdict switch
    {
        PinVerdict.Pinned => $"{name}'s certificate is the one pinned ({TlsFingerprint.Short(presented)})",
        PinVerdict.FirstUse => $"{name}'s certificate is new here ({TlsFingerprint.Short(presented)}) — pinned once the link authenticates",
        PinVerdict.Changed => $"CERTIFICATE CHANGED: {name} now shows {TlsFingerprint.Short(presented)} — refused until you accept it in the Network drawer",
        _ => $"{name}'s certificate is not the one its hello names — refused (another machine answering at its address?)",
    };

    /// <summary>A TLS validation callback that checks the server's certificate against the pin (and the hello's
    /// fingerprint) and reports what it saw; names and authorities are never consulted.</summary>
    public RemoteCertificateValidationCallback Validator(string key, string? advertised, Action<PinVerdict, string>? seen = null) =>
        (_, certificate, _, _) =>
        {
            if (certificate is null)
                return false;
            var presented = TlsFingerprint.Of(certificate.GetRawCertData());
            var verdict = Check(key, presented, advertised);
            seen?.Invoke(verdict, presented);
            return Accepts(verdict);
        };

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && TlsFingerprint.Normal(p.Value.GetString()) is { Length: > 0 } f)
                    _pins[p.Name] = f;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable pin file pins nothing: links go back to first use and say so.
            _log.LogWarning(ex, "The certificate pins could not be read: links pin again on first use.");
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            var json = JsonText.Write(w =>
            {
                w.WriteStartObject();
                foreach (var (k, v) in _pins.OrderBy(p => p.Key, StringComparer.Ordinal))
                    w.WriteString(k, v);
                w.WriteEndObject();
            });
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var temp = _path + ".new";
            File.WriteAllText(temp, json);
            DurableFile.MoveIntoPlace(temp, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run; the next start pins on first use again.
            _log.LogWarning(ex, "The certificate pins could not be written.");
        }
    }
}

/// <summary>The certificate a TLS client met (for tests and status words).</summary>
public static class Certificates
{
    public static string FingerprintOf(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return TlsFingerprint.Of(certificate.GetRawCertData());
    }
}
