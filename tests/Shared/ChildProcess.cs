using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PlayMap.Tests;

/// <summary>What a tool a test ran did: whether it started, whether it finished within its deadline (one that did not was stopped
/// with every process it started), its exit code, what it wrote, and whether its words were all in. Its words are read for at most
/// <see cref="ChildProcess.Drain"/> once it has exited, so a process it started that holds its output pipes cannot hold the run.</summary>
internal sealed record ChildRun(string Tool, bool Started, bool Finished, int ExitCode, byte[] Output, string Errors, bool WordsIn,
    TimeSpan Deadline, string Why)
{
    /// <summary>Its standard output as text.</summary>
    public string Text => Encoding.UTF8.GetString(Output);

    /// <summary>It finished with exit code 0 and every word it wrote is in.</summary>
    public bool Ok => Started && Finished && ExitCode == 0 && WordsIn;

    /// <summary>How it ended, in words, for a test's message: one stopped at its deadline with what it had written by then (its errors,
    /// else its output), as a finished one says what it wrote.</summary>
    public string Words =>
        !Started ? $"{Tool} could not be started: {Why}"
        : !Finished ? string.Create(CultureInfo.InvariantCulture, $"{Tool} did not finish within {Deadline.TotalSeconds:0} s and was stopped")
            + (Errors.Length > 0 ? ": " + Errors : Text.Length > 0 ? ": " + Text : "")
        : !WordsIn ? string.Create(CultureInfo.InvariantCulture, $"{Tool} (exit code {ExitCode}): its words were held open by a process it started")
        : string.Create(CultureInfo.InvariantCulture, $"{Tool} (exit code {ExitCode}): {(Errors.Length > 0 ? Errors : Text)}");
}

/// <summary>The tests' one way to run a child process and to end one they started: wait for the process itself under a deadline, stop
/// it with every process it started past it, then read its output for at most <see cref="Drain"/>. A process a tool starts (a linker's
/// helper, npm's, cmd.exe's for a .cmd) inherits the tool's output pipes and may hold them open long after the tool has exited, so a
/// run that ends with the end of those pipes, or waits for an exit with no bound, can stall a test, or a whole test host in discovery,
/// where blame has no test to name.</summary>
internal static class ChildProcess
{
    /// <summary>The longest a tool runs before it is stopped, when its caller names no deadline of its own.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    /// <summary>How long a process's words are read once it has exited, or has been stopped.</summary>
    public static readonly TimeSpan Drain = TimeSpan.FromSeconds(5);

    /// <summary>Runs a tool to its end, or stops it with every process it started once <paramref name="deadline"/> has passed.</summary>
    /// <param name="setup">Sets anything more of the start (a working folder, the environment).</param>
    public static ChildRun Run(string tool, IEnumerable<string> args, TimeSpan deadline, Action<ProcessStartInfo>? setup = null)
    {
        var p = Start(tool, args, setup, input: false, out var why);
        if (p is null)
            return NotStarted(tool, deadline, why);
        using (p)
        {
            var (output, errors) = Read(p);
            var finished = p.WaitForExit(deadline);
            if (!finished)
                Kill(p);
            return Ran(tool, p, finished, deadline, output, errors, WithinSync(Task.WhenAll(output, errors), Drain));
        }
    }

    /// <summary>As <see cref="Run"/>, awaited; <paramref name="input"/>, when given, writes the tool's standard input, which is closed
    /// once it returns.</summary>
    public static async Task<ChildRun> RunAsync(string tool, IEnumerable<string> args, TimeSpan deadline, Action<ProcessStartInfo>? setup = null,
        Func<Stream, Task>? input = null)
    {
        var p = Start(tool, args, setup, input is not null, out var why);
        if (p is null)
            return NotStarted(tool, deadline, why);
        using (p)
        {
            var (output, errors) = Read(p);
            var fed = input is null ? Task.CompletedTask : FeedAsync(p, input);
            var finished = await ExitAsync(p, deadline).ConfigureAwait(false);
            if (!finished)
            {
                Kill(p);
                await ExitAsync(p, Drain).ConfigureAwait(false);
            }
            var wordsIn = await WithinAsync(Task.WhenAll(output, errors, fed), Drain).ConfigureAwait(false);
            return Ran(tool, p, finished, deadline, output, errors, wordsIn);
        }
    }

    /// <summary>Ends a process the test started and reads itself (a node, a browser's driver): waits for it to exit within
    /// <paramref name="deadline"/>, stops it with every process it started past it, then gives <paramref name="reads"/> (the reads of
    /// its output the test began; reads the process makes for its own output events are waited with its exit) at most
    /// <see cref="Drain"/> more. True when it exited within the deadline. A read not done by then is left: its result is not to be
    /// awaited.</summary>
    public static async Task<bool> EndAsync(Process p, TimeSpan deadline, params Task?[] reads)
    {
        ArgumentNullException.ThrowIfNull(p);
        var exited = await ExitAsync(p, deadline).ConfigureAwait(false);
        if (!exited)
        {
            Kill(p);
            await ExitAsync(p, Drain).ConfigureAwait(false);
        }
        var started = reads.OfType<Task>().ToArray();
        if (started.Length > 0)
            await WithinAsync(Task.WhenAll(started), Drain).ConfigureAwait(false);
        return exited;
    }

    /// <summary>Stops a process the test started, now, with every process it started, and gives its reads at most
    /// <see cref="Drain"/>.</summary>
    public static Task StopAsync(Process p, params Task?[] reads) => EndAsync(p, TimeSpan.Zero, reads);

    /// <summary>Stops a process the test started, now, with every process it started, and waits at most <see cref="Drain"/> for its
    /// exit.</summary>
    public static void Stop(Process p)
    {
        ArgumentNullException.ThrowIfNull(p);
        Kill(p);
        p.WaitForExit(Drain);
    }

    private static Process? Start(string tool, IEnumerable<string> args, Action<ProcessStartInfo>? setup, bool input, out string why)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input,
            UseShellExecute = false,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        setup?.Invoke(start);
        try
        {
            var p = Process.Start(start);
            why = p is null ? "no process was started" : "";
            return p;
        }
        catch (Win32Exception ex)
        {
            why = ex.Message;
            return null;
        }
    }

    private static (Task<byte[]> Output, Task<string> Errors) Read(Process p) => (CopyAsync(p.StandardOutput.BaseStream), p.StandardError.ReadToEndAsync());

    private static async Task<byte[]> CopyAsync(Stream from)
    {
        using var to = new MemoryStream();
        await from.CopyToAsync(to).ConfigureAwait(false);
        return to.ToArray();
    }

    private static async Task FeedAsync(Process p, Func<Stream, Task> input)
    {
        try
        {
            await input(p.StandardInput.BaseStream).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The tool stopped reading (it exited, or was stopped): what it took is what it has.
        }
        finally
        {
            try
            {
                p.StandardInput.Close();
            }
            catch (IOException)
            {
                // As above: the pipe is gone already.
            }
        }
    }

    private static async Task<bool> ExitAsync(Process p, TimeSpan within)
    {
        if (within <= TimeSpan.Zero)
            return p.HasExited;
        using var limit = new CancellationTokenSource(within);
        try
        {
            await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the look and the kill.
        }
        catch (Win32Exception)
        {
            // It is exiting: the kill found it going.
        }
    }

    /// <summary>True when the reads ended within the bound (a read that failed has ended too).</summary>
    private static bool WithinSync(Task reads, TimeSpan within)
    {
        try
        {
            return reads.Wait(within);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private static async Task<bool> WithinAsync(Task reads, TimeSpan within)
    {
        try
        {
            await reads.WaitAsync(within).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return true;
        }
    }

    private static ChildRun NotStarted(string tool, TimeSpan deadline, string why) => new(tool, false, false, -1, [], "", false, deadline, why);

    private static ChildRun Ran(string tool, Process p, bool finished, TimeSpan deadline, Task<byte[]> output, Task<string> errors, bool wordsIn) =>
        new(tool, true, finished, finished ? p.ExitCode : -1, output.IsCompletedSuccessfully ? output.Result : [],
            errors.IsCompletedSuccessfully ? errors.Result : "", wordsIn && output.IsCompletedSuccessfully && errors.IsCompletedSuccessfully, deadline, "");
}
