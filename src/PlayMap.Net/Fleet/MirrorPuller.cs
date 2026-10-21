using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Fleet;
using PlayMap.Core.Time;
using PlayMap.Net.Media;
using PlayMap.Net.Tls;
using PlayMap.Core.Storage;

namespace PlayMap.Net.Fleet;

/// <summary>Where a backup pulls from: the primary's node id, address, web port, the certificate fingerprint its hello advertises
/// ("" when it offers no TLS), and its pairing token from this node's credential store ("" when it asks none).</summary>
public sealed record MirrorSource(string NodeId, string Name, string Address, int HttpPort, string Fingerprint, string Token)
{
    /// <summary>Says who, never the token.</summary>
    public override string ToString() => $"{Name} ({NodeId}) at {Address}:{HttpPort.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A backup's pulls from its primary's <c>/mirror</c>: over HTTPS pinned by the hello's fingerprint (the fleet's pin file
/// keeps it; a changed certificate is refused) when the primary offers TLS, each request signed with the pairing proof
/// (<see cref="PullProof"/>), so no token crosses the network and no stranger reads the show. Files come content-addressed through
/// <see cref="MediaTransfer"/> (ranged, resumed, verified by SHA-256 before they land), a body's silence counted on the node's clock.</summary>
public sealed class MirrorPuller : IDisposable
{
    private readonly MirrorSource _source;
    private readonly Func<long> _unixMs;
    private readonly HttpClient _http;
    private readonly MediaTransfer _transfer;
    private readonly ILogger _log;
    private readonly PinStore _pins;
    private readonly string _pinKey;
    private volatile string _pin = "";
    private volatile string _firstSeen = "";

    /// <param name="mono">The node's clock: the bandwidth bucket's, and the one a body's silence is counted on.</param>
    /// <param name="pulls">How a file's pull asks (the quiet, the tries per chunk; null = the transfer's own); its path and the proof are
    /// the mirror's whatever it says.</param>
    public MirrorPuller(MirrorSource source, PinStore pins, Func<long> unixMs, IMonoClock? mono = null, ILogger? log = null, MediaTransferOptions? pulls = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _pinKey = PinStore.NodeKey(source.NodeId);
        _unixMs = unixMs ?? throw new ArgumentNullException(nameof(unixMs));
        _log = log ?? NullLogger.Instance;
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
        if (Tls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = pins.Validator(_pinKey, source.Fingerprint, (verdict, presented) =>
            {
                _pin = PinStore.Accepts(verdict) ? "" : PinStore.Words(verdict, source.Name, presented);
                _firstSeen = verdict == PinVerdict.FirstUse ? presented : "";
            });
        }
        else if (!IPAddress.TryParse(source.Address, out var a) || !IPAddress.IsLoopback(a))
        {
            Refusal = $"{source.Name}'s hello offers no certificate: a mirror pull from another machine goes over HTTPS only";
        }
        if (!Tls && pins.Get(_pinKey) is not null)
            Refusal = $"{source.Name}'s hello offers no certificate, yet one is pinned for it — refused (an answer in clear could be anyone's)";
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _transfer = new MediaTransfer(_http, new TokenBucket(0, mono), (pulls ?? new MediaTransferOptions()) with { MediaPath = MirrorWebModule.MediaPath, Sign = Sign },
            _log, mono);
    }

    /// <summary>True when the pulls go over HTTPS (the primary's hello offers a certificate).</summary>
    public bool Tls => _source.Fingerprint.Length > 0;

    /// <summary>Why the primary's certificate was refused ("" while it is pinned or seen first).</summary>
    public string PinWords => _pin;

    /// <summary>Why nothing is pulled from this source at all ("" when pulls may go): plain HTTP to another machine, or a hello that
    /// drops the certificate a pin holds.</summary>
    public string Refusal { get; } = "";

    /// <summary>A certificate seen for the first time is pinned once an answer over it proved the node (its proof was taken).</summary>
    private void PinFirstUse()
    {
        if (_firstSeen is { Length: > 0 } seen)
        {
            _pins.Pin(_pinKey, seen);
            _firstSeen = "";
        }
    }

    public Uri Base
    {
        get
        {
            var host = IPAddress.TryParse(_source.Address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "[" + ip + "]" : _source.Address;
            return new Uri(string.Create(CultureInfo.InvariantCulture, $"{(Tls ? "https" : "http")}://{host}:{_source.HttpPort}/"));
        }
    }

    /// <summary>The primary's show now: its revision and its compact JSON.</summary>
    public async Task<(long Rev, string Json)> ProjectAsync(CancellationToken ct)
    {
        if (Refusal.Length > 0)
            throw new HttpRequestException(Refusal);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, MirrorWebModule.ProjectPath));
        Sign(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{_source.Name} answered {(int)response.StatusCode}: {Short(body)}");
        PinFirstUse();
        var rev = response.Headers.TryGetValues("X-PlayMap-Rev", out var v) && long.TryParse(v.FirstOrDefault(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r) ? r : -1;
        return (rev, body);
    }

    /// <summary>One file by its hash into <paramref name="destination"/>; its size is asked first when the show does not say it.</summary>
    public async Task<MediaPullResult> FileAsync(string hash, long size, string destination, CancellationToken ct)
    {
        if (Refusal.Length > 0)
            return new MediaPullResult(MediaPullOutcome.Failed, Refusal, 0, 0, 0);
        var hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..] : hash;
        if (size <= 0)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, new Uri(Base, MirrorWebModule.MediaPath + hex));
            Sign(head);
            using var answer = await _http.SendAsync(head, ct).ConfigureAwait(false);
            if (!answer.IsSuccessStatusCode || answer.Content.Headers.ContentLength is not { } length)
                return new MediaPullResult(MediaPullOutcome.Failed, $"{_source.Name} does not hold it ({(int)answer.StatusCode})", 0, 0, 0);
            size = length;
        }
        var result = await _transfer.PullAsync(Base, hex, size, destination, ct).ConfigureAwait(false);
        if (result.Outcome is MediaPullOutcome.Verified or MediaPullOutcome.AlreadyHere)
            PinFirstUse();
        return result;
    }

    /// <summary>One file the show names beside its project folder, by its key, into <paramref name="destination"/> (written beside and
    /// moved over once its SHA-256 is the one the show names, <see cref="ShowFiles.Matches"/>); answers whether it landed and, when it
    /// did not, why.</summary>
    public async Task<(bool Landed, string Words)> ShowFileAsync(string key, string destination, string hash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(hash);
        if (Refusal.Length > 0)
            return (false, Refusal);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, MirrorWebModule.FilePath + key));
        Sign(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (false, $"{_source.Name} answered {(int)response.StatusCode}: {Short(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".pulling";
        try
        {
            string hex;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (file.ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using (body.ConfigureAwait(false))
                    {
                        var buffer = new byte[1 << 20];
                        int n;
                        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            sha.AppendData(buffer, 0, n);
                            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        }
                    }
                }
                hex = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (!ShowFiles.Matches(hex, hash))
                return (false, "what came is not the file the show names (its hash differs)");
            DurableFile.MoveIntoPlace(temp, destination);
            PinFirstUse();
            return (true, "");
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>The pairing proof on a request: a fresh nonce and the HMAC of the token over it and the request; nothing when the
    /// primary asks no token.</summary>
    private void Sign(HttpRequestMessage request)
    {
        if (_source.Token.Length == 0 || request.RequestUri is null)
            return;
        var nonce = PullProof.NewNonce(_unixMs());
        request.Headers.TryAddWithoutValidation(PullProof.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(PullProof.ProofHeader, PullProof.Sign(_source.Token, nonce, request.Method.Method, request.RequestUri.Scheme, request.RequestUri.AbsolutePath));
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}
