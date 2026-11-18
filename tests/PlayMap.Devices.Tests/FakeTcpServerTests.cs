using System.Net;
using System.Net.Sockets;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Devices.Tests;

/// <summary>The simulators' server ends quietly however a test's end meets its accept loop, and only then.</summary>
public sealed class FakeTcpServerTests
{
    /// <summary>A test that ends at once disposes the server while its loop stands between its look at the stop and its accept: the
    /// accept then meets a listener already stopped, and the loop ends as the server does, never as a fault DisposeAsync rethrows.
    /// The order is made, not waited for: the loop's seam runs DisposeAsync itself before that accept, and the stop is observed (the
    /// server's port can be listened on again) before the accept is made.</summary>
    [Fact]
    public async Task AnAcceptMadeAfterTheStopEndsTheLoopQuietly()
    {
        var server = new StopsBeforeItsFirstAccept();
        server.Built();
        var disposing = await server.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await disposing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(server.StoppedBeforeAccept,
            "the server's port was still held after DisposeAsync returned to the loop's seam: the stop did not come before the accept");
    }

    /// <summary>A server whose loop, before its first accept, waits until the test has it whole (the loop's task assigned), disposes
    /// itself, and records whether its port can then be listened on again. A listen, not a connect: a connect to a free loopback
    /// port can, rarely, meet itself; a listen succeeds only on a port nothing holds, so a stray result fails, never passes.</summary>
    private sealed class StopsBeforeItsFirstAccept : FakeTcpServer
    {
        private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rounds;

        /// <summary>DisposeAsync as the loop's seam started it, pending on the loop it was started from.</summary>
        public TaskCompletionSource<Task> Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool StoppedBeforeAccept { get; private set; }

        public void Built() => _built.SetResult();

        protected override async ValueTask BeforeAcceptAsync()
        {
            if (Interlocked.Increment(ref _rounds) != 1)
                return;
            await _built.Task;
            var disposing = DisposeAsync().AsTask();
            var again = new TcpListener(IPAddress.Loopback, Port);
            try
            {
                again.Start();
                StoppedBeforeAccept = true;
            }
            catch (SocketException)
            {
                StoppedBeforeAccept = false;
            }
            finally
            {
                again.Stop();
            }
            Disposing.SetResult(disposing);
        }

        protected override Task ServeAsync(Stream stream, CancellationToken ct) => Task.CompletedTask;
    }
}
