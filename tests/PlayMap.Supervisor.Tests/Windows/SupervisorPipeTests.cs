using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The supervisor's pipe on Windows, served by a supervisor this test runs under names of its own: it answers its words; a
/// client that sends a megabyte with no newline, one that sends nothing and one that leaves before its answer each end alone while the
/// pipe goes on answering, and another client is answered while one sits idle; a name another process holds is not served on until it
/// is free; and a client refuses an answer from a server that is not the supervisor.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorPipeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>This test's process serves the pipe: the rule a test client holds it to.</summary>
    private static string? ThisProcess(int pid, int session) => pid == Environment.ProcessId ? null : "pid " + pid + ", not this test";

    private static string Ask(string pipe, string word)
    {
        var answer = SupervisorPipe.Ask(pipe, word, ThisProcess);
        Assert.NotNull(answer);
        Assert.True(answer.FromSupervisor, answer.Words);
        return answer.Words;
    }

    [WindowsFact]
    public async Task ThePipeAnswersItsWordsAndRefusesAnyOther()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        using var host = place.Host();
        host.Prepare();
        using var stop = new CancellationTokenSource();
        var serving = host.ServePipeAsync(stop.Token);
        Assert.Contains("\"state\":\"waiting-for-session\"", Ask(place.PipeName, "status"), StringComparison.Ordinal);
        Assert.Equal("ERR there is nothing to resume: waiting-for-session", Ask(place.PipeName, "resume"));
        Assert.Equal(SupervisorHost.NotAWord, Ask(place.PipeName, "frobnicate"));
        Assert.StartsWith("OK held", Ask(place.PipeName, "stop"), StringComparison.Ordinal);
        Assert.Equal("OK resumed: the supervisor starts the engine again", Ask(place.PipeName, "resume"));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    [WindowsFact]
    public async Task AClientThatFloodsSitsIdleOrLeavesEndsAloneAndThePipeGoesOnAnswering()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        using var host = place.Host();
        host.Prepare();
        using var stop = new CancellationTokenSource();
        var serving = host.ServePipeAsync(stop.Token);

        // A megabyte with no newline: cut off at the cap, answered with the refusal or not at all.
        await using (var flood = new NamedPipeClientStream(".", place.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await flood.ConnectAsync(2000);
            var megabyte = new byte[1024 * 1024];
            Array.Fill(megabyte, (byte)'x');
            try
            {
                using var within = new CancellationTokenSource(Patience);
                await flood.WriteAsync(megabyte, within.Token);
            }
            catch (IOException)
            {
                // The supervisor cut the connection off before the megabyte was written.
            }
        }
        Assert.Contains("\"state\":", Ask(place.PipeName, "status"), StringComparison.Ordinal);

        // A client that sends nothing: while it sits connected another client is answered, and it is cut off in its time.
        await using (var idle = new NamedPipeClientStream(".", place.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await idle.ConnectAsync(2000);
            Assert.Contains("\"state\":", Ask(place.PipeName, "status"), StringComparison.Ordinal);
            var buffer = new byte[16];
            using var within = new CancellationTokenSource(Patience);
            // The supervisor ends the connection: the read sees its end, never a hang.
            Assert.Equal(0, await idle.ReadAsync(buffer, within.Token));
        }

        // A client that asks and leaves before its answer.
        await using (var leaver = new NamedPipeClientStream(".", place.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await leaver.ConnectAsync(2000);
            await leaver.WriteAsync(Encoding.UTF8.GetBytes("status\n"));
        }
        Assert.Contains("\"state\":", Ask(place.PipeName, "status"), StringComparison.Ordinal);
        Assert.False(serving.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
        Assert.Contains("a pipe client was cut off", place.Log(), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ANameAnotherProcessHoldsIsNotServedOnUntilItIsFree()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        using var host = place.Host();
        host.Prepare();
        var squatter = new NamedPipeServerStream(place.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var stop = new CancellationTokenSource();
        var serving = host.ServePipeAsync(stop.Token);
        var deadline = DateTime.UtcNow + Patience;
        while (!place.Log().Contains("is held by another process", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Contains($"the pipe name {place.PipeName} is held by another process", place.Log(), StringComparison.Ordinal);
        await squatter.DisposeAsync();
        // Once the name is free the supervisor takes it with its first instance and answers.
        string? answered = null;
        while (answered is null && DateTime.UtcNow < deadline)
        {
            if (SupervisorPipe.Ask(place.PipeName, "status", ThisProcess) is { FromSupervisor: true } a)
                answered = a.Words;
        }
        Assert.NotNull(answered);
        Assert.Contains("\"state\":", answered, StringComparison.Ordinal);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    [WindowsFact]
    public async Task AClientTakesNoAnswerFromAServerThatIsNotTheSupervisor()
    {
        var name = "PlayMap.Supervisor.Test." + Guid.NewGuid().ToString("N");
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var answering = Task.Run(async () =>
        {
            try
            {
                await squatter.WaitForConnectionAsync();
                await squatter.WriteAsync(Encoding.UTF8.GetBytes("OK resumed: a squatter's words\n"));
            }
            catch (IOException)
            {
                // The client hung up without reading, before or after the connection was taken: it took nothing from this server.
            }
        });
        var answer = SupervisorPipe.Ask(name, "resume", SupervisorPipe.ThisMachinesSupervisor(@"C:\Program Files\PlayMap\Supervisor\PlayMap.Supervisor.exe"));
        Assert.NotNull(answer);
        Assert.False(answer.FromSupervisor);
        Assert.Equal($"pid {Environment.ProcessId}, which runs as another account than SYSTEM", answer.Words);
        await answering;
    }
}
