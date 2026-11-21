using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Host;

/// <summary>The engine process's own log: what the host, the transports and the links say through
/// Microsoft.Extensions.Logging, one stamped line each (an exception's text on the lines after), appended to
/// playmap.engine.log in the node's folder. The journal is the show's record; this is the machine's. Lines go
/// through a thread of their own so a slow disk never holds a network loop; past 16 MB the file moves aside to
/// ".1" (one generation kept, replaced through <see cref="DurableFile.Replace"/>) and a fresh one starts. A move aside that fails (on
/// Windows, a handle on either file that shares no delete) loses no line: the live file keeps its lines and the next ones, and the
/// roll stays owed, tried again on a wait of its own whether or not another line comes, until it lands or the log closes. Every file
/// the log starts begins with a line naming the build and the process that wrote it, so a file read on its own — the one after a roll
/// among them — says which build it is from. The engine has no console, so this is where its words go.</summary>
public sealed class NodeLogProvider : ILoggerProvider
{
    public const string FileName = "playmap.engine.log";
    private const long RollBytes = 16L * 1024 * 1024;
    private const string Category = "PlayMap.Engine.Log";

    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>(), 8192);
    private readonly Thread _writer;
    private readonly LogLevel _minimum;
    private readonly long _rollBytes;
    private readonly Action<string>? _rolling;
    private long _dropped;
    private long _rollsFailed;
    private int _rollOwed;
    private int _disposed;

    // The writer's thread alone: when an owed roll is tried again (Environment.TickCount64) and the wait after the next failure.
    private long _retryAt;
    private TimeSpan _retryAfter = FirstRetry;

    public NodeLogProvider(string path, LogLevel minimum = LogLevel.Information)
        : this(path, minimum, RollBytes)
    {
    }

    /// <summary>The wait after a roll's first failure before it is tried again: a scanner that opens a file just closed lets go of
    /// it within this, as read (not measured). Each further failure doubles the wait, up to <see cref="LongestRetry"/>.</summary>
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between two tries of an owed roll: a holder that keeps a file open for hours (a viewer) costs one
    /// failed open every 5 s, and the roll lands within 5 s of its letting go.</summary>
    internal static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(5);

    /// <summary>A log that moves aside past <paramref name="rollBytes"/>. <paramref name="rolling"/> (tests only; null in the product)
    /// is called on the writer's thread with the live file's path before each move aside: an <see cref="IOException"/> from it stands
    /// for the move refused.</summary>
    internal NodeLogProvider(string path, LogLevel minimum, long rollBytes, Action<string>? rolling = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rollBytes);
        Path = System.IO.Path.GetFullPath(path);
        _minimum = minimum;
        _rollBytes = rollBytes;
        _rolling = rolling;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "PlayMap log" };
        _writer.Start();
    }

    /// <summary>The first line of every file the log starts: the build (BuildInfo's words) and the process.</summary>
    internal static string Heading =>
        string.Create(CultureInfo.InvariantCulture, $"{Core.BuildInfo.Current.Display} begins this file of the engine's log (process {Environment.ProcessId}).");

    public string Path { get; }

    /// <summary>Lines that did not reach a file: refused because the writer was 8192 lines behind (a disk that stopped) or the log
    /// had closed, or lost to a disk that refused them (the file could not be opened, written or flushed). Counted, never waited on. A
    /// roll that failed loses no line and is never counted here (<see cref="RollsFailed"/>).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Moves aside that failed: each leaves every line in the live file and the roll owed (<see cref="RollOwed"/>).</summary>
    public long RollsFailed => Interlocked.Read(ref _rollsFailed);

    /// <summary>True from a failed move aside until one lands: the live file is past the roll size and is tried again on its own wait.
    /// Set before <see cref="RollsFailed"/> counts the failure.</summary>
    public bool RollOwed => Volatile.Read(ref _rollOwed) != 0;

    /// <summary>The log in a node's folder.</summary>
    public static NodeLogProvider For(NodeHome home, LogLevel minimum = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(home);
        return new NodeLogProvider(System.IO.Path.Combine(home.Folder, FileName), minimum);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var line = Line(level, category, message, ex);
        try
        {
            if (!_lines.TryAdd(line))
                Interlocked.Increment(ref _dropped);
        }
        catch (InvalidOperationException)
        {
            // Added after the log closed: the process is on its way out; nothing waits for this line.
            Interlocked.Increment(ref _dropped);
        }
    }

    private static string Line(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder(128);
        sb.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(' ').Append(Word(level)).Append(' ').Append(category).Append(": ").Append(message);
        if (ex is not null)
            sb.Append('\n').Append(ex);
        return sb.ToString();
    }

    private static string Word(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "fatal",
        _ => "none",
    };

    private void WriteLoop()
    {
        StreamWriter? file = null;
        var headed = false;
        try
        {
            while (true)
            {
                if (RollOwed && Environment.TickCount64 >= _retryAt)
                {
                    // An owed roll whose wait is over is tried before the next line: the writer lets go of the live file first.
                    if (file is not null)
                        Close(file);
                    file = null;
                    Roll();
                }
                if (!_lines.TryTake(out var line, Wait()))
                {
                    // Nothing came within an owed roll's wait (tried at the top), or the log closed and every line was written.
                    if (_lines.IsCompleted)
                        break;
                    continue;
                }
                if (file is null)
                {
                    file = Open();
                    if (file is null)
                    {
                        // The folder cannot be written: the line is dropped and the next one tries again.
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }
                    // A file that already holds lines is not started here: its first line is its own.
                    headed = file.BaseStream.Length > 0;
                }
                try
                {
                    if (!headed)
                    {
                        file.Write(Line(LogLevel.Information, Category, Heading, null));
                        file.Write('\n');
                        headed = true;
                    }
                    file.Write(line);
                    file.Write('\n');
                    if (_lines.Count == 0)
                        file.Flush();
                    // An owed roll keeps its own wait: a line meanwhile is appended to the live file and tries nothing.
                    if (!RollOwed && file.BaseStream.Length > _rollBytes)
                    {
                        Close(file);
                        file = null;
                        Roll();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A full or vanished disk: this line is lost, the file is reopened for the next.
                    Interlocked.Increment(ref _dropped);
                    if (file is not null)
                        Close(file);
                    file = null;
                }
            }
        }
        finally
        {
            if (file is not null)
                Close(file);
        }
    }

    /// <summary>How long the writer waits for a line: without end while no roll is owed, else until the owed roll's next try.</summary>
    private int Wait()
    {
        if (!RollOwed)
            return Timeout.Infinite;
        return (int)Math.Clamp(_retryAt - Environment.TickCount64, 0, (long)LongestRetry.TotalMilliseconds);
    }

    private void Close(StreamWriter file)
    {
        try
        {
            file.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Its last buffered lines could not be flushed: counted as dropped.
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>The live file opened to append, a file left past the roll size by an earlier process moved aside first (unless a roll
    /// is owed, which keeps its own wait); null when the folder cannot be written (the caller counts the line).</summary>
    private StreamWriter? Open()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (!RollOwed && File.Exists(Path) && new FileInfo(Path).Length > _rollBytes)
                Roll();
            var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves the live file aside to ".1", on the writer's thread with the file closed. A move that fails moves nothing and
    /// loses no line: it is counted in <see cref="RollsFailed"/>, never in <see cref="Dropped"/>, and leaves the roll owed, tried again
    /// after <see cref="FirstRetry"/>, the wait doubling with each failure up to <see cref="LongestRetry"/>. A live file no longer there
    /// owes no roll.</summary>
    private void Roll()
    {
        try
        {
            _rolling?.Invoke(Path);
            DurableFile.Replace(Path, Path + ".1");
            Landed();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(Path))
            {
                Landed();
                return;
            }
            Volatile.Write(ref _rollOwed, 1);
            Interlocked.Increment(ref _rollsFailed);
            _retryAt = Environment.TickCount64 + (long)_retryAfter.TotalMilliseconds;
            _retryAfter = _retryAfter * 2 < LongestRetry ? _retryAfter * 2 : LongestRetry;
        }
    }

    private void Landed()
    {
        Volatile.Write(ref _rollOwed, 0);
        _retryAfter = FirstRetry;
    }

    /// <summary>Takes no more lines and lets the writer finish what it has (two seconds at most).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lines.CompleteAdding();
        // A writer stuck on a dead disk keeps the queue it still reads; the process is ending anyway.
        if (_writer.Join(TimeSpan.FromSeconds(2)))
            _lines.Dispose();
    }

    private sealed class Logger(NodeLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
                owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
