using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;

namespace PlayMap.Net.Fleet;

/// <summary><c>/mirror</c>: what a hot backup pulls from its primary — <c>GET /mirror/project</c> (the show as compact JSON, its
/// revision in <c>X-PlayMap-Rev</c>) and <c>GET /mirror/media/&lt;sha256&gt;</c> (a file this node holds verified, by its content hash,
/// in ranges). Each request carries the pairing proof of <see cref="PullProof"/> (the token's HMAC over a fresh nonce, the scheme and
/// the request, never the token), checked here in fixed time with each nonce taken once within a minute of this node's clock; from an
/// address the node trusts, or on a node that asks no token, the node's own gate decides (its Origin and host rules included). From another machine a pull must come over HTTPS (the peer pins this
/// node's certificate by the hello's fingerprint). Only GET and HEAD.</summary>
public sealed class MirrorWebModule(Func<(long Rev, string Json)> project, Func<string, string?> fileForHash, Func<long> unixMs) : IWebModule
{
    public const string ProjectPath = "/mirror/project";
    public const string MediaPath = "/mirror/media/";

    /// <summary>Nonces remembered at most (the oldest forgotten first; a forgotten one is outside the window by then at any sane rate).</summary>
    private const int NoncesKept = 8192;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Nonce, long AtMs)> _order = new();
    private long _proved;
    private long _refused;
    private long _overHttps;

    /// <summary>Pulls let in by a valid proof.</summary>
    public long Proved => Interlocked.Read(ref _proved);

    /// <summary>Pulls refused at the door.</summary>
    public long RefusedPulls => Interlocked.Read(ref _refused);

    /// <summary>Pulls served over HTTPS.</summary>
    public long OverHttps => Interlocked.Read(ref _overHttps);

    public string Name => "mirror";

    public string PathPrefix => "/mirror";

    public WebAdmission Admit(HttpContext context, NodeSettings settings, out string words)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var request = context.Request;
        var from = context.Connection.RemoteIpAddress;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return Refuse(out words, "the mirror's pulls are read-only: GET only");
        if (!request.IsHttps && !NetAddress.IsLoopback(from))
            return Refuse(out words, "a mirror pull from another machine comes over HTTPS (pinned by this node's hello fingerprint), never in clear");
        // With no proof to check, the node's own gate decides (its Origin, host and token rules), as for any route.
        if (!NetAddress.TokenNeeded(settings) || NetAddress.IsTrusted(from, settings))
        {
            words = "";
            return WebAdmission.Defer;
        }
        var nonce = request.Headers[PullProof.NonceHeader].ToString();
        var proof = request.Headers[PullProof.ProofHeader].ToString();
        if (nonce.Length == 0 || proof.Length == 0)
            return Refuse(out words, "a mirror pull carries this node's pairing proof (X-PlayMap-Nonce and X-PlayMap-Proof) — CREDENTIAL SET node.<this node's id> <token> on the backup");
        if (!PullProof.TryTime(nonce, out var at) || Math.Abs(unixMs() - at) > PullProof.WindowMs)
            return Refuse(out words, "the pull's nonce is not from now (the two machines' clocks are more than a minute apart, or the nonce is not one)");
        var path = request.Path.Value ?? "";
        if (!PullProof.Verify(settings.Token, nonce, request.Method, request.IsHttps ? "https" : "http", path, proof))
            return Refuse(out words, "the pull's proof is not of this node's pairing token");
        switch (TakeOnce(nonce))
        {
            case false:
                return Refuse(out words, "that nonce was used already: each pull proves itself afresh");
            case null:
                return Refuse(out words, "too many pulls within the minute: this node remembers each one's nonce, and it is full — pull again shortly");
        }
        Interlocked.Increment(ref _proved);
        return Admitted(request, out words);
    }

    private WebAdmission Admitted(HttpRequest request, out string words)
    {
        if (request.IsHttps)
            Interlocked.Increment(ref _overHttps);
        words = "";
        return WebAdmission.Capability;
    }

    private WebAdmission Refuse(out string words, string why)
    {
        Interlocked.Increment(ref _refused);
        words = why;
        return WebAdmission.Refuse;
    }

    /// <summary>True the first time a nonce is seen, false when it was seen, null when every remembered nonce is still inside the
    /// window (a nonce is forgotten only once it is too old to be taken again, so a replay is never let in by forgetting).</summary>
    private bool? TakeOnce(string nonce)
    {
        lock (_gate)
        {
            var now = unixMs();
            while (_order.Count > 0 && now - _order.Peek().AtMs > 2 * PullProof.WindowMs)
                _seen.Remove(_order.Dequeue().Nonce);
            if (_seen.Contains(nonce))
                return false;
            if (_seen.Count >= NoncesKept)
                return null;
            _seen.Add(nonce);
            // Kept by arrival, for twice the window: a nonce up to a minute ahead of this clock is still remembered when it expires.
            _order.Enqueue((nonce, now));
            return true;
        }
    }

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapMethods(ProjectPath, ["GET", "HEAD"], async context =>
        {
            var (rev, json) = project();
            context.Response.Headers["X-PlayMap-Rev"] = rev.ToString(CultureInfo.InvariantCulture);
            await FleetWeb.WriteJsonAsync(context, json);
        });
        routes.MapMethods(MediaPath + "{hash}", ["GET", "HEAD"], (HttpContext context, string hash) =>
        {
            var hex = hash.Trim().ToLowerInvariant();
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit) || fileForHash(hex) is not { } path || !File.Exists(path))
                return Results.Text("this node holds no verified file with that SHA-256", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });
    }
}
