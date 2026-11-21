using System.Net;
using System.Net.Sockets;

namespace PlayMap.Net.Tests;

/// <summary>The test fakes' one accept (<see cref="Accepts"/>, shared by every fake whose loop accepts beside its own stop) ends
/// quietly on a listener its owner's stop has stopped, and only then. The order a test that ends at once can make is made here, not
/// waited for: the owner's stop asks the token and stops the listener before the accept is called, as each fake's DisposeAsync does,
/// with nothing between the loop's look at the stop and its accept.</summary>
public sealed class AcceptsTests
{
    [Fact]
    public async Task AnAcceptMadeAfterTheOwnersStopEndsTheLoopQuietly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        listener.Stop();
        Assert.Null(await Accepts.NextAsync(listener, stop.Token));
    }

    /// <summary>The same throw while the owner has not asked its stop is a fault, and escapes as the listener threw it: nothing but
    /// the owner's own end is swallowed.</summary>
    [Fact]
    public async Task AnAcceptOnAListenerStoppedWhileItsOwnerRunsIsAFaultThatEscapes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        listener.Stop();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Accepts.NextAsync(listener, stop.Token));
    }

    /// <summary>The owner's stop asked while the accept waits ends it too, and a connection made while it runs is handed on.</summary>
    [Fact]
    public async Task AWaitingAcceptEndsOnTheStopAndAConnectionIsHandedOn()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var stop = new CancellationTokenSource();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var dialler = new TcpClient();
            var accepting = Accepts.NextAsync(listener, stop.Token);
            await dialler.ConnectAsync(IPAddress.Loopback, port);
            using var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(accepted);

            var waiting = Accepts.NextAsync(listener, stop.Token);
            await stop.CancelAsync();
            Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            listener.Stop();
        }
    }
}
