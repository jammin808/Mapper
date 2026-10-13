using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Content;
using PlayMap.Core.Media;
using PlayMap.Render.Generators.Skia;
using SkiaSharp;

namespace PlayMap.Engine.Rendering;

/// <summary>The Stagewatch timer as a picture (a <c>timer:</c> source): the time large in its phase colour,
/// the title above, the message below, black around — drawn with the house faces, so every node shows the
/// same type. The network edge's timer JSON is polled four times a second while any frame has asked for the
/// view in the last two seconds, and the picture is redrawn only when the JSON changes; drawing happens on the
/// poller, never on the render thread, which only takes the newest finished picture (null until it changes,
/// so the GPU keeps what it has). Two buffers: the poller never draws into the one a lease still holds.</summary>
internal sealed class TimerView : IDisposable
{
    private const long IdleMs = 2000;

    private readonly Func<string?> _timerJson;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[][] _buffers;
    private readonly int[] _leased = new int[2];
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task? _poller;
    private PinnedLease? _ready;
    private long _wantedAtMs;
    private long _generation;
    private string? _drawnJson = "\0";
    private int _drawnInto = -1;

    public TimerView(Func<string?> timerJson, int width = 1280, int height = 720)
    {
        ArgumentNullException.ThrowIfNull(timerJson);
        _timerJson = timerJson;
        _width = width;
        _height = height;
        _buffers = [new byte[width * height * 4], new byte[width * height * 4]];
    }

    /// <summary>Pictures drawn so far.</summary>
    public long Pictures => Interlocked.Read(ref _generation);

    /// <summary>On the render thread: the newest picture not handed over yet, or null (hold what the GPU has).
    /// Starts the poller when the view was idle.</summary>
    public VideoFrameLease? Acquire()
    {
        var now = Environment.TickCount64;
        var idle = now - Interlocked.Exchange(ref _wantedAtMs, now) > IdleMs;
        if (idle || _poller is null)
            StartPoller();
        return Interlocked.Exchange(ref _ready, null);
    }

    private void StartPoller()
    {
        lock (_gate)
        {
            if (_poller is { IsCompleted: false } || _stop.IsCancellationRequested)
                return;
            _poller = Task.Run(() => PollAsync(_stop.Token), CancellationToken.None);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        do
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _wantedAtMs) > IdleMs)
                return;
            var json = _timerJson();
            if (!string.Equals(json, _drawnJson, StringComparison.Ordinal) && Draw(json))
                _drawnJson = json;
        }
        while (await NextTick(timer, ct).ConfigureAwait(false));
    }

    private static async ValueTask<bool> NextTick(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Draws into the buffer no lease holds and publishes it; false when both are still held.</summary>
    private bool Draw(string? json)
    {
        var target = _drawnInto == 0 ? 1 : 0;
        if (Volatile.Read(ref _leased[target]) != 0)
            return false;
        var (text, color, title, message) = Read(json);
        var buffer = _buffers[target];
        var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* pixels = buffer)
            {
                using var surface = SKSurface.Create(info, (nint)pixels, _width * 4);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { IsAntialias = true, Color = color };
                using var big = new SKFont(HouseFonts.SemiBold, _height * 0.36f) { Edging = SKFontEdging.Antialias };
                using var small = new SKFont(HouseFonts.Regular, _height * 0.07f) { Edging = SKFontEdging.Antialias };
                canvas.DrawText(text, _width / 2f, _height * 0.62f, SKTextAlign.Center, big, paint);
                paint.Color = new SKColor(200, 200, 200);
                if (title.Length > 0)
                    canvas.DrawText(title, _width / 2f, _height * 0.16f, SKTextAlign.Center, small, paint);
                if (message.Length > 0)
                    canvas.DrawText(message, _width / 2f, _height * 0.88f, SKTextAlign.Center, small, paint);
                canvas.Flush();
            }
        }
        _drawnInto = target;
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _leased[target], 1);
        var lease = new PinnedLease(PixelLayout.Rgba8, buffer, _width, _height, _width * 4, generation, () => Volatile.Write(ref _leased[target], 0));
        // A picture the render thread never took is let go, so its buffer is free again.
        Interlocked.Exchange(ref _ready, lease)?.Dispose();
        return true;
    }

    /// <summary>The time, its colour, the title and the message from STATE's timer object.</summary>
    internal static (string Text, SKColor Color, string Title, string Message) Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ("--:--", new SKColor(90, 90, 90), "", "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Text(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var text = Text("text");
            if (text.Length == 0 && r.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var seconds))
                text = string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{(int)Math.Abs(seconds % 60):00}");
            var c = ColorRgb.Parse(Text("color"), ColorRgb.White);
            return (text.Length > 0 ? text : "--:--", new SKColor(c.R8, c.G8, c.B8), Text("title"), Text("message"));
        }
        catch (JsonException)
        {
            return ("--:--", new SKColor(90, 90, 90), "", "the timer's state could not be read");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Interlocked.Exchange(ref _ready, null)?.Dispose();
        _stop.Dispose();
    }
}
