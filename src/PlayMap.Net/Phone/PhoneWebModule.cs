using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PlayMap.Core.Bus;
using PlayMap.Net.Http;

namespace PlayMap.Net.Phone;

/// <summary>
/// The phone's camera page under <c>/scan</c> of the node's web port. Admitted before the node's token gate on its own terms:
/// <list type="bullet">
/// <item>the page's own files (GET or HEAD) are open — they carry no data and no secret, and over plain HTTP from another machine
/// the page itself says it needs the node's https address;</item>
/// <item><c>/scan/ws</c> is a capability: the hub admits the socket only by the ticket or key in its first message; the upgrade is
/// refused from a page of another origin, by a host name that is not the node's own (as the node's own routes refuse it), and over
/// plain HTTP from anything but this machine;</item>
/// <item>anything else under /scan is refused.</item>
/// </list>
/// The module maps nothing outside /scan, so the node's own routes keep their gate. Every answer carries a strict content security
/// policy (this origin only, no framing), no referrer, camera and motion for this origin only, and no sniffing; the page is never
/// cached and its scripts carry strong ETags.
/// </summary>
public sealed partial class PhoneWebModule(PhoneHub hub) : IWebModule
{
    public const string Prefix = "/scan";

    public const string SocketPath = "/scan/ws";

    private static readonly FrozenDictionary<string, (byte[] Body, string Type, string ETag)> Files = LoadFiles();

    public string Name => "phone camera page";

    public string PathPrefix => Prefix;

    /// <summary>The page's files by name ("index.html", "app.js", …).</summary>
    public static IReadOnlyCollection<string> FileNames => Files.Keys;

    private static FrozenDictionary<string, (byte[], string, string)> LoadFiles()
    {
        var asm = typeof(PhoneWebModule).Assembly;
        var files = new Dictionary<string, (byte[], string, string)>(StringComparer.Ordinal);
        foreach (var resource in asm.GetManifestResourceNames().Where(n => n.StartsWith("phone/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var name = resource["phone/".Length..];
            var body = ms.ToArray();
            var type = Path.GetExtension(name) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" or ".mjs" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".webmanifest" => "application/manifest+json",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream",
            };
            files[name] = (body, type, "\"" + Convert.ToHexStringLower(SHA256.HashData(body))[..32] + "\"");
        }
        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public WebAdmission Admit(HttpContext context, NodeSettings settings, out string words)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var path = context.Request.Path.Value ?? "";
        if (!settings.Phone.Enabled || !hub.Options.Enabled())
        {
            words = "phones are switched off on this node";
            return WebAdmission.Refuse;
        }
        if (path == SocketPath)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                words = "the phone's socket is a WebSocket";
                return WebAdmission.Refuse;
            }
            if (!SameOrigin(context.Request))
            {
                words = "a page from another site may not use the phone's socket";
                return WebAdmission.Refuse;
            }
            // As the node's own routes: a name some page pointed at this machine is not the node's (a phone uses its address).
            if (!HttpHost.IsOwnHost(context.Request, settings))
            {
                words = "the phone's socket answers on the node's address or its own names, as the invite's link gives it";
                return WebAdmission.Refuse;
            }
            if (!context.Request.IsHttps && !NetAddress.IsLoopback(context.Connection.RemoteIpAddress))
            {
                words = "the phone's camera needs the node's https address";
                return WebAdmission.Refuse;
            }
            words = "";
            return WebAdmission.Capability;
        }
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            if (path is Prefix or Prefix + "/" || (path.StartsWith(Prefix + "/", StringComparison.Ordinal) && Files.ContainsKey(path[(Prefix.Length + 1)..])))
            {
                words = "";
                return WebAdmission.Open;
            }
        }
        words = "no such page";
        return WebAdmission.Refuse;
    }

    /// <summary>A browser names the page a socket comes from: only this node's own page may open it (a request without an Origin
    /// is not a browser page's).</summary>
    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && request.Host.HasValue
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Scheme, request.IsHttps ? "https" : "http", StringComparison.OrdinalIgnoreCase);
    }

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapMethods(Prefix + "/{file?}", ["GET", "HEAD"], ServeAsync);
        routes.Map(SocketPath, (HttpContext c) =>
        {
            Secure(c, html: false);
            return hub.AcceptAsync(c);
        });
    }

    private static async Task ServeAsync(HttpContext c)
    {
        if (c.Request.Path.Value == Prefix)
        {
            // The page's own files are relative to /scan/.
            Secure(c, html: true);
            c.Response.Redirect(Prefix + "/", permanent: false);
            return;
        }
        var name = c.Request.RouteValues["file"] as string;
        if (string.IsNullOrEmpty(name))
            name = "index.html";
        if (!Files.TryGetValue(name, out var file))
        {
            c.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var html = file.Type.StartsWith("text/html", StringComparison.Ordinal);
        Secure(c, html);
        c.Response.ContentType = file.Type;
        if (!html)
        {
            c.Response.Headers.ETag = file.ETag;
            c.Response.Headers.CacheControl = "no-cache";
            if (c.Request.Headers.IfNoneMatch.ToString() == file.ETag)
            {
                c.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
        }
        c.Response.ContentLength = file.Body.Length;
        if (HttpMethods.IsHead(c.Request.Method))
            return;
        await c.Response.Body.WriteAsync(file.Body, c.RequestAborted);
    }

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+(:[0-9]{1,5})?$|^\[[0-9A-Fa-f:.]+\](:[0-9]{1,5})?$")]
    private static partial Regex PlainHost();

    /// <summary>The page's headers: a policy that lets it reach only this origin (its socket included), never be framed, and send
    /// no referrer.</summary>
    internal static void Secure(HttpContext c, bool html)
    {
        var host = c.Request.Host.Value ?? "";
        // The socket's own address, spelled out for browsers whose 'self' does not cover wss: only a plain host name or address.
        var socket = PlainHost().IsMatch(host) ? $" wss://{host}" + (c.Request.IsHttps ? "" : $" ws://{host}") : "";
        var h = c.Response.Headers;
        h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; worker-src 'self'; connect-src 'self'" + socket
            + "; img-src 'self' blob: data:; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(self), accelerometer=(self), gyroscope=(self), screen-wake-lock=(self)";
        h.XContentTypeOptions = "nosniff";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        if (html)
            h.CacheControl = "no-store";
    }
}
