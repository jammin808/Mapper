using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PlayMap.Core.Bus;

namespace PlayMap.Net.Fleet;

/// <summary>The fleet's two reads on the web port, admitted before the token gate on purpose: they are open reads, like the hello —
/// a machine's health and the board are names, counts and lights, never a secret, a token or a file's content — and they change
/// nothing: only GET (and HEAD) is let in, anything else is refused with words. <c>/health</c> answers this node's HEALTH record,
/// and as a WebSocket pushes it at 1 Hz and on a light change (a peer's fleet monitor subscribes here, over TLS pinned by the
/// hello's fingerprint when the node serves it); <c>/fleet</c> answers the board in Patterns' Eye shape.</summary>
public static class FleetWeb
{
    public const string ReadOnly = "the fleet's pages are read-only: GET only — the node's verbs are on /api/cmd and the line wire, behind the token";

    /// <summary>Open for a read; refused with words for anything else.</summary>
    public static WebAdmission AdmitRead(HttpContext context, out string words)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            words = "";
            return WebAdmission.Open;
        }
        words = ReadOnly;
        return WebAdmission.Refuse;
    }

    internal static async Task WriteJsonAsync(HttpContext context, string json)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsync(json, context.RequestAborted);
    }
}

/// <summary><c>/health</c>: GET answers the record now; a WebSocket hears every record the hub publishes (the latest at once).</summary>
public sealed class HealthWebModule(HealthHub hub, Func<string> now) : IWebModule
{
    public string Name => "health";

    public string PathPrefix => "/health";

    public WebAdmission Admit(HttpContext context, NodeSettings settings, out string words) => FleetWeb.AdmitRead(context, out words);

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Map("/health", HandleAsync);
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await FleetWeb.WriteJsonAsync(context, now());
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await FeedAsync(socket, context.RequestAborted);
    }

    /// <summary>Sends each record the hub publishes until the peer closes or goes; a slow peer gets the newest, never a queue.</summary>
    private async Task FeedAsync(WebSocket socket, CancellationToken aborted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        using var wake = new SemaphoreSlim(0, 1);
        string? pending = null;
        void Take(string json)
        {
            Volatile.Write(ref pending, json);
            if (wake.CurrentCount == 0)
            {
                try
                {
                    wake.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Another record woke the sender first: it sends the newest.
                }
            }
        }
        var reading = DrainAsync(socket, stop);
        using (hub.Watch(Take))
        {
            try
            {
                while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await wake.WaitAsync(stop.Token);
                    if (Interlocked.Exchange(ref pending, null) is { } json)
                        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // The peer went or the node stops: the feed ends with it.
                await stop.CancelAsync();
            }
        }
        await stop.CancelAsync();
        await reading;
    }

    /// <summary>Reads (and drops) what the peer sends, so its close is seen; a close or an error ends the feed.</summary>
    private static async Task DrainAsync(WebSocket socket, CancellationTokenSource stop)
    {
        var buffer = new byte[1024];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The connection is gone: the sender stops on the cancel below.
        }
        await stop.CancelAsync();
    }
}

/// <summary><c>/fleet</c>: GET answers the fleet board (every machine this node hears, in Patterns' Eye shape).</summary>
public sealed class FleetWebModule(Func<string> board) : IWebModule
{
    public string Name => "fleet";

    public string PathPrefix => "/fleet";

    public WebAdmission Admit(HttpContext context, NodeSettings settings, out string words) => FleetWeb.AdmitRead(context, out words);

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/fleet", context => FleetWeb.WriteJsonAsync(context, board()));
    }
}
