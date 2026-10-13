using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PlayMap.Engine.Host;

/// <summary>The engine process's own log: what the host, the transports and the links say through
/// Microsoft.Extensions.Logging, one stamped line each (an exception's text on the lines after), appended to
/// playmap.engine.log in the node's folder. The journal is the show's record; this is the machine's. Lines go
/// through a thread of their own so a slow disk never holds a network loop; past 16 MB the file moves aside to
/// ".1" (one generation kept) and a fresh one starts. The engine has no console, so this is where its words go.</summary>
public sealed class NodeLogProvider : ILoggerProvider
{
    public const string FileName = "playmap.engine.log";
    private const long RollBytes = 16L * 1024 * 1024;

    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>(), 8192);
    private readonly Thread _writer;
    private readonly LogLevel _minimum;
    private long _dropped;
    private int _disposed;

    public NodeLogProvider(string path, LogLevel minimum = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _minimum = minimum;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "PlayMap log" };
        _writer.Start();
    }

    public string Path { get; }

    /// <summary>Lines refused because the writer was 8192 lines behind (a disk that stopped): counted, never waited on.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The log in a node's folder.</summary>
    public static NodeLogProvider For(NodeHome home, LogLevel minimum = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(home);
        return new NodeLogProvider(System.IO.Path.Combine(home.Folder, FileName), minimum);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder(128);
        sb.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(' ').Append(Word(level)).Append(' ').Append(category).Append(": ").Append(message);
        if (ex is not null)
            sb.Append('\n').Append(ex);
        try
        {
            if (!_lines.TryAdd(sb.ToString()))
                Interlocked.Increment(ref _dropped);
        }
        catch (InvalidOperationException)
        {
            // Added after the log closed: the process is on its way out; nothing waits for this line.
            Interlocked.Increment(ref _dropped);
        }
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
        try
        {
            foreach (var line in _lines.GetConsumingEnumerable())
            {
                file ??= Open();
                if (file is null)
                {
                    // The folder cannot be written: the line is dropped and the next one tries again.
                    Interlocked.Increment(ref _dropped);
                    continue;
                }
                try
                {
                    file.Write(line);
                    file.Write('\n');
                    if (_lines.Count == 0)
                        file.Flush();
                    if (file.BaseStream.Length > RollBytes)
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

    private StreamWriter? Open()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > RollBytes)
                Roll();
            var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _dropped);
            return null;
        }
    }

    private void Roll()
    {
        try
        {
            File.Move(Path, Path + ".1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old file stays where it is and keeps growing; a log is never worth stopping the node for.
            Interlocked.Increment(ref _dropped);
        }
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
