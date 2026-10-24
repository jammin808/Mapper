using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>An engine on loopback as the check meets it: it pushes a NONCE and a STATE first, then answers the line it is asked with
/// <c>OK</c> or <c>ERR</c> and its request id — or never answers.</summary>
internal sealed class FakeEngine : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    private FakeEngine(Func<string, string?> answer)
    {
        _listener.Start();
        _serving = ServeAsync(answer, _stop.Token);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Asked { get; private set; }

    public string LastLine { get; private set; } = "";

    public static FakeEngine Answering(string payload) => new(line => "OK " + line.Split(' ')[0] + " " + payload);

    public static FakeEngine Refusing(string reason) => new(line => "ERR " + line.Split(' ')[0] + " " + reason);

    public static FakeEngine Silent() => new(_ => null);

    private async Task ServeAsync(Func<string, string?> answer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes("NONCE 0123456789abcdef0123456789abcdef\nSTATE {\"v\":1}\n"), ct);
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct) ?? "";
                Asked++;
                LastLine = line;
                if (answer(line) is { } reply)
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("OK @other {}\n" + reply + "\n"), ct);
                else
                    await Task.Delay(Timeout.Infinite, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The test is done with this engine.
        }
        catch (IOException)
        {
            // The check hung up first.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        try
        {
            await _serving;
        }
        catch (SocketException)
        {
            // The listener stopped under an accept.
        }
        _stop.Dispose();
    }
}
