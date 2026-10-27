using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Content;
using PlayMap.Core.Media;
using PlayMap.Render.Generators.Skia;
using SkiaSharp;

namespace PlayMap.Engine.Rendering;

/// <summary>The Stagewatch timer as a picture (a <c>timer:</c> source): the time large in its phase colour,
/// the title above, the message below, black around — black alone while Stagewatch blacks out — and Stagewatch's
/// watermark bottom right over everything, in every state, drawn with the house faces, so every node shows the
/// same type. The network edge's timer JSON is polled four times a second while any frame has asked for the
/// view in the last two seconds, and the picture is redrawn only when the JSON changes, each word in it bounded
/// (<see cref="MaxWords"/>, <see cref="MaxWatermark"/>) so no redraw costs more than its bound; drawing happens on the
/// poller, never on the render thread, which only takes the newest finished picture (null until it changes,
/// so the GPU keeps what it has). Two buffers: the poller never draws into the one a lease still holds.</summary>
internal sealed class TimerView : IDisposable
{
    private const long IdleMs = 2000;

    /// <summary>The watermark's place and size as Stagewatch's own screens set them: its size a share of the picture's height (with a
    /// floor in pixels), its right edge and its bottom inset by a share of the width and of the height.</summary>
    private const float WatermarkHeight = 0.032f;
    private const float WatermarkMinPx = 14f;
    private const float WatermarkSide = 0.02f;
    private const float WatermarkBottom = 0.012f;

    /// <summary>Every word the picture draws is bounded in characters, so a drawing's cost is bounded whatever the link carries: the
    /// time, the title and the message at Stagewatch's own bound on a message (500), the watermark at 120 (Stagewatch's own mark is
    /// 26). A longer one is drawn to its bound; STATE keeps what Stagewatch sent.</summary>
    internal const int MaxWords = 500;
    internal const int MaxWatermark = 120;

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
        return TakePicture();
    }

    /// <summary>The newest picture not handed over yet, or null; its taker disposes it once uploaded.</summary>
    internal VideoFrameLease? TakePicture() => Interlocked.Exchange(ref _ready, null);

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
            Refresh();
        }
        while (await NextTick(timer, ct).ConfigureAwait(false));
    }

    /// <summary>One look at the timer JSON, on the one thread that draws (the poller): JSON not drawn yet is drawn and published.
    /// True when a picture was published; false when the JSON is the one drawn last, or both buffers are still held (it is drawn on
    /// a later look).</summary>
    internal bool Refresh()
    {
        var json = _timerJson();
        if (string.Equals(json, _drawnJson, StringComparison.Ordinal) || !Draw(json))
            return false;
        _drawnJson = json;
        return true;
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
        var face = Read(json);
        var buffer = _buffers[target];
        var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* pixels = buffer)
            {
                using var surface = SKSurface.Create(info, (nint)pixels, _width * 4);
                Paint(surface.Canvas, face, _width, _height);
                surface.Canvas.Flush();
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

    /// <summary>A canvas cleared black with the face drawn on it: under a blackout nothing but the watermark; otherwise the time,
    /// the title and the message. The watermark is drawn last, whatever the state (blackout, a time that is not a fact, any
    /// phase), as Stagewatch's own screens draw it: bottom right, 3.2% of the height (14 px at least, smaller only to fit the
    /// width), white at 70% over a dark halo.</summary>
    internal static void Paint(SKCanvas canvas, TimerFace face, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Clear(SKColors.Black);
        if (!face.Blackout)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = face.Color };
            using var big = new SKFont(HouseFonts.SemiBold, height * 0.36f) { Edging = SKFontEdging.Antialias };
            using var small = new SKFont(HouseFonts.Regular, height * 0.07f) { Edging = SKFontEdging.Antialias };
            canvas.DrawText(face.Text, width / 2f, height * 0.62f, SKTextAlign.Center, big, paint);
            paint.Color = new SKColor(200, 200, 200);
            if (face.Title.Length > 0)
                canvas.DrawText(face.Title, width / 2f, height * 0.16f, SKTextAlign.Center, small, paint);
            if (face.Message.Length > 0)
                canvas.DrawText(face.Message, width / 2f, height * 0.88f, SKTextAlign.Center, small, paint);
        }
        if (face.Watermark.Length > 0)
            DrawWatermark(canvas, face.Watermark, width, height);
    }

    private static void DrawWatermark(SKCanvas canvas, string watermark, int width, int height)
    {
        var right = width * (1 - WatermarkSide);
        var room = width * (1 - 2 * WatermarkSide);
        using var font = new SKFont(HouseFonts.SemiBold, Math.Max(WatermarkMinPx, height * WatermarkHeight)) { Edging = SKFontEdging.Antialias };
        var wide = font.MeasureText(watermark);
        if (wide > room)
            font.Size *= room / wide;
        var baseline = height * (1 - WatermarkBottom) - font.Metrics.Descent;
        using var halo = new SKPaint { IsAntialias = true, Color = SKColors.Black, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.12f) };
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 179) };
        canvas.DrawText(watermark, right, baseline, SKTextAlign.Right, font, halo);
        canvas.DrawText(watermark, right, baseline, SKTextAlign.Right, font, ink);
    }

    /// <summary>The face STATE's timer object asks for: the time, its colour, the title, the message, Stagewatch's blackout and its
    /// watermark, each word within its bound (<see cref="MaxWords"/>, <see cref="MaxWatermark"/>). No JSON (no Stagewatch link) is
    /// "--:--" in grey; JSON that cannot be read says so; neither has a blackout or a mark.</summary>
    internal static TimerFace Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TimerFace("--:--", new SKColor(90, 90, 90), "", "", false, "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Text(string name, int max = MaxWords) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Bounded(v.GetString() ?? "", max) : "";
            var text = Text("text");
            if (text.Length == 0 && r.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var seconds))
                text = string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{(int)Math.Abs(seconds % 60):00}");
            var c = ColorRgb.Parse(Text("color"), ColorRgb.White);
            var blackout = r.TryGetProperty("blackout", out var b) && b.ValueKind == JsonValueKind.True;
            return new TimerFace(text.Length > 0 ? text : "--:--", new SKColor(c.R8, c.G8, c.B8), Text("title"), Text("message"), blackout,
                Text("watermark", MaxWatermark));
        }
        catch (JsonException)
        {
            return new TimerFace("--:--", new SKColor(90, 90, 90), "", "the timer's state could not be read", false, "");
        }
    }

    /// <summary>The first <paramref name="max"/> characters of <paramref name="s"/>, never half a surrogate pair.</summary>
    private static string Bounded(string s, int max) =>
        s.Length <= max ? s : s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)];

    public void Dispose()
    {
        _stop.Cancel();
        Interlocked.Exchange(ref _ready, null)?.Dispose();
        _stop.Dispose();
    }
}

/// <summary>What the timer picture shows: the time in its colour, the title, the message, whether Stagewatch blacks out (then only the
/// watermark is drawn) and the watermark ("" for none).</summary>
internal readonly record struct TimerFace(string Text, SKColor Color, string Title, string Message, bool Blackout, string Watermark);
