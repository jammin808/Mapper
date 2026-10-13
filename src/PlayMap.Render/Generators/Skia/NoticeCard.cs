using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>A picture of words for a view that has no picture of its own to show (a camera that would not open): a
/// dark card with an amber frame, a title and the reason, drawn on the CPU as BGRA so any preview ring can carry it.</summary>
public static class NoticeCard
{
    /// <summary>The card at a size (top row first, BGRA, <paramref name="width"/> × 4 bytes a row).</summary>
    public static byte[] Bgra(int width, int height, string title, string detail)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(detail);
        var w = Math.Max(16, width);
        var h = Math.Max(16, height);
        using var kit = new SkiaKit();
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bitmap))
        {
            c.Clear(new SKColor(0x14, 0x18, 0x1E));
            var s = Math.Min(w, h);
            Px.BorderInside(c, new SKRectI(0, 0, w, h), Math.Max(2, s / 60), kit.Fill(Amber));
            var head = kit.Bold;
            head.Size = Math.Max(9, s * 0.07f);
            Px.TextCentered(c, title, w / 2f, h * 0.40f, head, kit.Text(Amber));
            var body = kit.Regular;
            body.Size = Math.Max(7, s * 0.04f);
            var (first, second) = TwoLines(detail.Length > 140 ? detail[..139] + "…" : detail, 70);
            Px.TextCentered(c, first, w / 2f, h * 0.56f, body, kit.Text(new SKColor(0xD8, 0xDE, 0xE6)));
            if (second.Length > 0)
                Px.TextCentered(c, second, w / 2f, h * 0.66f, body, kit.Text(new SKColor(0xD8, 0xDE, 0xE6)));
            c.Flush();
        }
        var bytes = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, bytes, y * w * 4, w * 4);
        return bytes;
    }

    private static readonly SKColor Amber = new(0xF5, 0x9E, 0x0B);

    /// <summary>Words broken at a space near <paramref name="width"/> characters into at most two lines.</summary>
    private static (string First, string Second) TwoLines(string text, int width)
    {
        if (text.Length <= width)
            return (text, "");
        var cut = text.LastIndexOf(' ', width);
        if (cut <= 0)
            cut = width;
        return (text[..cut].TrimEnd(), text[cut..].Trim());
    }
}
