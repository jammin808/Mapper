using System.Net.Sockets;

namespace PlayMap.Tests;

/// <summary>The one accept a test fake's loop makes. A fake's stop asks its token first and then stops its listener (each fake's
/// DisposeAsync does both, in that order, and nothing else stops the listener), so an accept that meets the listener already stopped
/// while the token is asked is the fake ending, never a fault; the same throw while the token is not asked is a fault and escapes to
/// the fake's owner. The cancel, the disposed listener and the socket's own end are the ends every fake's loop took already.</summary>
internal static class Accepts
{
    /// <summary>The next connection, or null when the fake is ending: its token asked, its listener disposed or closed under the
    /// accept. An accept on a listener stopped while <paramref name="stop"/> is not asked throws, as the listener throws it.</summary>
    public static async Task<TcpClient?> NextAsync(TcpListener listener, CancellationToken stop)
    {
        try
        {
            return await listener.AcceptTcpClientAsync(stop);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            return null;
        }
        catch (InvalidOperationException) when (stop.IsCancellationRequested)
        {
            // "Not listening": the accept was made between the loop's look at the stop and its call, after the owner's stop had asked
            // the token and stopped the listener. The loop ends as the fake does.
            return null;
        }
    }
}
