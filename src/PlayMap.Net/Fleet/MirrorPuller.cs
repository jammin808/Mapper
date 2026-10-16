using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Fleet;
using PlayMap.Core.Time;
using PlayMap.Net.Media;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Fleet;

/// <summary>Where a backup pulls from: the primary's node id, address, web port, the certificate fingerprint its hello advertises
/// ("" when it offers no TLS), and its pairing token from this node's credential store ("" when it asks none).</summary>
public sealed record MirrorSource(string NodeId, string Name, string Address, int HttpPort, string Fingerprint, string Token);

/// <summary>A backup's pulls from its primary's <c>/mirror</c>: over HTTPS pinned by the hello's fingerprint (the fleet's pin file
/// keeps it; a changed certificate is refused) when the primary offers TLS, each request signed with the pairing proof
/// (<see cref="PullProof"/>), so no token crosses the network and no stranger reads the show. Files come content-addressed through
/// <see cref="MediaTransfer"/> (ranged, resumed, verified by SHA-256 before they land).</summary>
public sealed class MirrorPuller : IDisposable
{
    private readonly MirrorSource _source;
    private readonly Func<long> _unixMs;
    private readonly HttpClient _http;
    private readonly MediaTransfer _transfer;
    private readonly ILogger _log;
    private volatile string _pin = "";

    public MirrorPuller(MirrorSource source, PinStore pins, Func<long> unixMs, IMonoClock? mono = null, ILogger? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(pins);
        _unixMs = unixMs ?? throw new ArgumentNullException(nameof(unixMs));
        _log = log ?? NullLogger.Instance;
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
        if (Tls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = pins.Validator(PinStore.NodeKey(source.NodeId), source.Fingerprint,
                (verdict, presented) => _pin = PinStore.Accepts(verdict) ? "" : PinStore.Words(verdict, source.Name, presented));
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _transfer = new MediaTransfer(_http, new TokenBucket(0, mono), new MediaTransferOptions { MediaPath = MirrorWebModule.MediaPath, Sign = Sign }, _log);
    }

    /// <summary>True when the pulls go over HTTPS (the primary's hello offers a certificate).</summary>
    public bool Tls => _source.Fingerprint.Length > 0;

    /// <summary>Why the primary's certificate was refused ("" while it is pinned or seen first).</summary>
    public string PinWords => _pin;

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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, MirrorWebModule.ProjectPath));
        Sign(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{_source.Name} answered {(int)response.StatusCode}: {Short(body)}");
        var rev = response.Headers.TryGetValues("X-PlayMap-Rev", out var v) && long.TryParse(v.FirstOrDefault(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r) ? r : -1;
        return (rev, body);
    }

    /// <summary>One file by its hash into <paramref name="destination"/>; its size is asked first when the show does not say it.</summary>
    public async Task<MediaPullResult> FileAsync(string hash, long size, string destination, CancellationToken ct)
    {
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
        return await _transfer.PullAsync(Base, hex, size, destination, ct).ConfigureAwait(false);
    }

    /// <summary>The pairing proof on a request: a fresh nonce and the HMAC of the token over it and the request; nothing when the
    /// primary asks no token.</summary>
    private void Sign(HttpRequestMessage request)
    {
        if (_source.Token.Length == 0 || request.RequestUri is null)
            return;
        var nonce = PullProof.NewNonce(_unixMs());
        request.Headers.TryAddWithoutValidation(PullProof.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(PullProof.ProofHeader, PullProof.Sign(_source.Token, nonce, request.Method.Method, request.RequestUri.AbsolutePath));
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}
