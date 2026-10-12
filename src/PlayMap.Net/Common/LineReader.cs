using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PlayMap.Net;

/// <summary>Reads UTF-8 lines ("\n", a trailing "\r" stripped) from a stream with two fences: a line longer than
/// the limit, and a line whose first byte arrived more than the deadline ago without its end, are not commands —
/// both throw <see cref="InvalidDataException"/> with the words the peer is told before the close. An idle
/// connection (no partial line) may wait for ever: liveness is the client's business (it pings).</summary>
public sealed class LineReader(Stream stream, int maxLineBytes, TimeSpan lineDeadline)
{
    private byte[] _buffer = new byte[Math.Min(8192, maxLineBytes + 2)];
    private int _start;
    private int _end;
    private long _lineStartedAt;

    /// <summary>The next line, or null at the end of the stream (a partial line at the end is dropped).</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                var length = nl - _start;
                if (length > 0 && _buffer[nl - 1] == (byte)'\r')
                    length--;
                if (length > maxLineBytes)
                    throw TooLong();
                var line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start = nl + 1;
                if (_start == _end)
                    _start = _end = 0;
                else
                    _lineStartedAt = Stopwatch.GetTimestamp();
                return line;
            }
            if (_end - _start > maxLineBytes + 1)
                throw TooLong();
            MakeRoom();
            var n = await ReadSomeAsync(ct);
            if (n == 0)
                return null;
            if (_end == _start)
                _lineStartedAt = Stopwatch.GetTimestamp();
            _end += n;
        }
    }

    private InvalidDataException TooLong() =>
        new(string.Create(CultureInfo.InvariantCulture, $"a line past {maxLineBytes / 1024} KiB"));

    private InvalidDataException TooSlow() =>
        new(string.Create(CultureInfo.InvariantCulture, $"a line that did not end within {lineDeadline.TotalSeconds:0.#} s"));

    private void MakeRoom()
    {
        if (_end < _buffer.Length)
            return;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
            return;
        }
        var grown = new byte[Math.Min(_buffer.Length * 2, maxLineBytes + 2)];
        if (grown.Length <= _buffer.Length)
            throw TooLong();
        Buffer.BlockCopy(_buffer, 0, grown, 0, _end);
        _buffer = grown;
    }

    private async ValueTask<int> ReadSomeAsync(CancellationToken ct)
    {
        if (_end == _start)
            return await stream.ReadAsync(_buffer.AsMemory(_end), ct);
        // A line has started: what is left of its deadline bounds this read.
        var left = lineDeadline - Stopwatch.GetElapsedTime(_lineStartedAt);
        if (left <= TimeSpan.Zero)
            throw TooSlow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(left);
        try
        {
            return await stream.ReadAsync(_buffer.AsMemory(_end), deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TooSlow();
        }
    }
}
