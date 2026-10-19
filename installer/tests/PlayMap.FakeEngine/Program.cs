using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using PlayMap.Core.Machine;

namespace PlayMap.FakeEngine;

/// <summary>"PlayMap.exe engine [--answer &lt;payload&gt; | --silent | --refuse &lt;reason&gt;] [--port-file &lt;file&gt;]
/// [--asked-file &lt;file&gt;] [--record &lt;folder&gt;] [--stop-event &lt;name&gt;]": a fake engine. It listens on loopback, answers
/// each <c>@id SHOW …</c> line with <c>OK @id &lt;payload&gt;</c>, with <c>ERR @id &lt;reason&gt;</c>, or never; writes each line
/// it is asked into the asked file; writes its pid record (its port, started now) into the folder; writes its port into the port file
/// last, so a reader that sees the port sees the record; and, given a stop event, opens it as the show user's engine does (Synchronize
/// alone), waits on it, takes its record back and exits 0. Otherwise it runs until it is ended.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || !string.Equals(args[0], "engine", StringComparison.OrdinalIgnoreCase))
            return 64;
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        Func<string, string?> answer = args.Contains("--silent")
            ? _ => null
            : Option("--refuse") is { } reason
                ? id => "ERR " + id + " " + reason
                : id => "OK " + id + " " + (Option("--answer") ?? "{\"armed\":false}");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = ServeAsync(listener, answer, Option("--asked-file"), stop.Token);

        EnginePidRecord? record = null;
        if (Option("--record") is { } folder)
        {
            record = new EnginePidRecord(Environment.ProcessId, port, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!record.TryWrite(folder))
                return 3;
        }
        if (Option("--port-file") is { } portFile)
        {
            await File.WriteAllTextAsync(portFile + ".tmp", port.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            File.Move(portFile + ".tmp", portFile, overwrite: true);
        }

        if (Option("--stop-event") is { } name && OperatingSystem.IsWindows())
        {
            await WaitForStopAsync(name).ConfigureAwait(false);
            if (record is not null && Option("--record") is { } at)
                record.TryRemove(at);
            await stop.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            await serving.ConfigureAwait(false);
            return 0;
        }
        await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Opens the stop event with Synchronize alone, as the show user's engine may, once it exists, and waits until it is set.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task WaitForStopAsync(string name)
    {
        EventWaitHandle? stop;
        while (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Synchronize, out stop))
            await Task.Delay(100).ConfigureAwait(false);
        using (stop)
            await Task.Run(() => stop.WaitOne()).ConfigureAwait(false);
    }

    private static async Task ServeAsync(TcpListener listener, Func<string, string?> answer, string? askedFile, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = AnswerAsync(client, answer, askedFile, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The fake is stopping.
            System.Diagnostics.Trace.WriteLine("PlayMap.FakeEngine: " + ex.Message);
        }
    }

    private static async Task AnswerAsync(TcpClient client, Func<string, string?> answer, string? askedFile, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (askedFile is not null)
                        await File.AppendAllTextAsync(askedFile, line + "\n", ct).ConfigureAwait(false);
                    var words = line.Split(' ', 3);
                    var id = words[0].StartsWith('@') ? words[0] : "@0";
                    var verb = words[0].StartsWith('@') && words.Length > 1 ? words[1] : words[0];
                    var reply = string.Equals(verb, "SHOW", StringComparison.OrdinalIgnoreCase)
                        ? answer(id)
                        : "ERR " + id + " this fake engine answers SHOW only";
                    if (reply is null)
                        continue;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The asker hung up, or the fake is stopping.
                System.Diagnostics.Trace.WriteLine("PlayMap.FakeEngine: " + ex.Message);
            }
        }
    }
}
