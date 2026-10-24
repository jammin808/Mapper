using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PlayMap.Engine;

/// <summary>One row of the show journal: what happened, who asked, what it did.</summary>
public sealed record JournalRow(long AtUnixMs, string Kind, string Text, string? Who, string? Line, bool Ok);

/// <summary>The show journal (playmap.journal.jsonl): every verb, refusal, link change and fault, one JSON
/// object a line. Writes go through a lane of their own so a slow disk never holds the control lane; the
/// last rows are kept in memory, the context's "recent" read from them. Secrets never reach it (pairing
/// proofs and keys are not verbs; the wire's AUTH lines are never journalled).</summary>
public sealed class Journal : IAsyncDisposable
{
    private readonly Channel<JournalRow> _rows = Channel.CreateBounded<JournalRow>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Lock _recentGate = new();
    private readonly Queue<JournalRow> _recent = new();
    private readonly Task _writer;
    private readonly string? _path;
    private readonly Func<long> _unixMs;

    public Journal(string? path, Func<long> unixMs)
    {
        _path = path;
        _unixMs = unixMs;
        _writer = Task.Factory.StartNew(WriteLoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public int RecentCapacity { get; init; } = 200;

    public event Action<JournalRow>? Written;

    public void Add(string kind, string text, string? who = null, string? line = null, bool ok = true)
    {
        var row = new JournalRow(_unixMs(), kind, text, who, line, ok);
        lock (_recentGate)
        {
            _recent.Enqueue(row);
            while (_recent.Count > RecentCapacity)
                _recent.Dequeue();
        }
        _rows.Writer.TryWrite(row);
        Written?.Invoke(row);
    }

    public IReadOnlyList<JournalRow> Recent(int count)
    {
        lock (_recentGate)
            return _recent.Skip(Math.Max(0, _recent.Count - count)).ToList();
    }

    private async Task WriteLoopAsync()
    {
        if (_path is null)
        {
            await foreach (var _ in _rows.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // No file: the rows live only in memory (tests, a node without a home folder).
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        var buffer = new ArrayBufferWriter<byte>(1024);
        await foreach (var row in _rows.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            buffer.Clear();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteNumber("at", row.AtUnixMs);
                w.WriteString("kind", row.Kind);
                w.WriteString("text", row.Text);
                if (row.Who is not null)
                    w.WriteString("who", row.Who);
                if (row.Line is not null)
                    w.WriteString("line", row.Line);
                w.WriteBoolean("ok", row.Ok);
                w.WriteEndObject();
            }
            await stream.WriteAsync(buffer.WrittenMemory).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
            if (_rows.Reader.Count == 0)
                await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops taking rows and lets the writer flush what it has.</summary>
    public async ValueTask DisposeAsync()
    {
        _rows.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    public static string Describe(Exception ex) => new StringBuilder(ex.GetType().Name).Append(": ").Append(ex.Message).ToString();
}
