using System.Diagnostics.CodeAnalysis;
using PlayMap.Core.Media;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Video;

[assembly: SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Scope = "type",
    Target = "~T:PlayMap.Engine.Rendering.RemappedLeasePool",
    Justification = "SourceLeases.cs is compiled into this suite for the timer's lease; the decoders' pool beside it is made in the engine.")]

namespace PlayMap.Render.Tests.Content;

/// <summary>The Stagewatch timer surface as each backend leg's device draws the picture the engine hands it: under Stagewatch's
/// blackout nothing but its watermark, bottom right; without one the time in its phase colour with the watermark still there; the
/// watermark kept while the time is not a fact; and no watermark, no mark. The picture is STATE's timer object drawn by the engine's
/// own <see cref="TimerView"/> into its own lease.</summary>
[Collection("gpu")]
public class TimerViewRenderTests(BackendFixture backends)
{
    private const int W = 640;
    private const int H = 360;
    private const string Mark = "StageWatch by Ben Rohleder";

    /// <summary>The watermark's corner: the right half of the picture's bottom 7%.</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) Corner = (W / 2, (int)(H * 0.93), W, H);

    /// <summary>Where the time's digits sit: the middle half across, from 30% to 65% down.</summary>
    private static readonly (int X0, int Y0, int X1, int Y1) Middle = (W / 4, (int)(H * 0.30), W * 3 / 4, (int)(H * 0.65));

    private static string Timer(bool blackout, string watermark = Mark, bool trusted = true) => trusted
        ? "{\"state\":\"running\",\"text\":\"12:30\",\"seconds\":750,\"phase\":\"warning\",\"color\":\"#F59E0B\",\"title\":\"Keynote\","
          + "\"message\":\"Wrap up\",\"trusted\":true,\"why\":\"\",\"blackout\":" + (blackout ? "true" : "false") + ",\"watermark\":\"" + watermark + "\"}"
        : "{\"state\":\"unknown\",\"text\":\"?\",\"seconds\":null,\"phase\":\"warning\",\"color\":\"#F59E0B\",\"title\":\"Keynote\","
          + "\"message\":\"Wrap up\",\"trusted\":false,\"why\":\"Stagewatch has gone quiet\",\"blackout\":" + (blackout ? "true" : "false")
          + ",\"watermark\":\"" + watermark + "\"}";

    /// <summary>Every leg's read-back (BGRA, top row first) of the <paramref name="w"/> × <paramref name="h"/> picture the view draws
    /// for <paramref name="json"/> (null: no Stagewatch link).</summary>
    private List<(string Leg, byte[] Bgra)> Drawn(string? json, int w = W, int h = H)
    {
        using var view = new TimerView(() => json, w, h);
        Assert.True(view.Refresh());
        using var lease = view.TakePicture();
        Assert.NotNull(lease);
        Assert.Equal((PixelLayout.Rgba8, w, h), (lease.Layout, lease.Width, lease.Height));
        var drawn = new List<(string, byte[])>();
        foreach (var leg in backends.Legs)
        {
            drawn.Add((leg.Name, leg.Run(l =>
            {
                using var set = new VideoTextureSet(l.Device);
                Assert.True(set.Update(lease));
                var bgra = Scene.ReadTexture(l.Device, set.Picture!);
                Assert.Empty(l.TakeErrors());
                return bgra;
            })));
        }
        return drawn;
    }

    private static bool Inside((int X0, int Y0, int X1, int Y1) box, int x, int y) => x >= box.X0 && x < box.X1 && y >= box.Y0 && y < box.Y1;

    /// <summary>The pixels in <paramref name="box"/> of a picture <paramref name="w"/> wide that pass <paramref name="test"/>.</summary>
    private static int Count(byte[] bgra, (int X0, int Y0, int X1, int Y1) box, Func<(int R, int G, int B), bool> test, int w = W)
    {
        var n = 0;
        for (var y = box.Y0; y < box.Y1; y++)
            for (var x = box.X0; x < box.X1; x++)
                if (test(Pixels.At(bgra, w, x, y)))
                    n++;
        return n;
    }

    /// <summary>The rows of <paramref name="box"/> in a picture <paramref name="w"/> wide holding a pixel that passes
    /// <paramref name="test"/>.</summary>
    private static int Rows(byte[] bgra, (int X0, int Y0, int X1, int Y1) box, Func<(int R, int G, int B), bool> test, int w = W)
    {
        var n = 0;
        for (var y = box.Y0; y < box.Y1; y++)
            if (Count(bgra, (box.X0, y, box.X1, y + 1), test, w) > 0)
                n++;
        return n;
    }

    private static bool Lit((int R, int G, int B) p) => Math.Max(p.R, Math.Max(p.G, p.B)) > 0;

    /// <summary>The brightest channel of any pixel outside <paramref name="box"/>.</summary>
    private static int BrightestOutside(byte[] bgra, (int X0, int Y0, int X1, int Y1) box)
    {
        var most = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (!Inside(box, x, y))
                {
                    var (r, g, b) = Pixels.At(bgra, W, x, y);
                    most = Math.Max(most, Math.Max(r, Math.Max(g, b)));
                }
        return most;
    }

    private static bool Whitish((int R, int G, int B) p) => Math.Min(p.R, Math.Min(p.G, p.B)) >= 150;

    private static bool Amber((int R, int G, int B) p) => p.R >= 200 && p.G is >= 110 and <= 200 && p.B <= 60;

    [Fact]
    public void UnderABlackoutOnlyTheWatermarkIsDrawnBottomRight()
    {
        foreach (var (leg, px) in Drawn(Timer(blackout: true)))
        {
            Assert.True(BrightestOutside(px, Corner) == 0, $"{leg}: something but the watermark is drawn under a blackout");
            Assert.True(Count(px, Corner, Whitish) >= 20, $"{leg}: no watermark in the corner");
            Assert.Equal(0, Count(px, Middle, Amber));
        }
    }

    [Fact]
    public void WithoutABlackoutTheTimeIsDrawnAndTheWatermarkStillSitsInItsCorner()
    {
        foreach (var (leg, px) in Drawn(Timer(blackout: false)))
        {
            Assert.True(Count(px, Middle, Amber) >= 500, $"{leg}: the time is not drawn in its phase colour");
            Assert.True(Count(px, Corner, Whitish) >= 20, $"{leg}: no watermark in the corner");
        }
    }

    [Fact]
    public void TheWatermarkStaysWhileTheTimeIsNotAFactAndAHeldBlackoutStaysBlack()
    {
        foreach (var (leg, px) in Drawn(Timer(blackout: false, trusted: false)))
        {
            Assert.True(Count(px, Middle, Amber) > 0, $"{leg}: the question mark is not drawn");
            Assert.True(Count(px, Corner, Whitish) >= 20, $"{leg}: the watermark left with the time");
        }
        foreach (var (leg, px) in Drawn(Timer(blackout: true, trusted: false)))
        {
            Assert.True(BrightestOutside(px, Corner) == 0, $"{leg}: a held blackout is not black");
            Assert.True(Count(px, Corner, Whitish) >= 20, $"{leg}: the watermark left with the time");
        }
    }

    [Fact]
    public void NoWatermarkLeavesTheCornerBlack()
    {
        foreach (var (leg, px) in Drawn(Timer(blackout: true, watermark: "")))
        {
            Assert.True(BrightestOutside(px, Corner) == 0, leg);
            Assert.True(Count(px, Corner, p => Math.Max(p.R, Math.Max(p.G, p.B)) > 0) == 0, $"{leg}: the corner is not black");
        }
        foreach (var (leg, px) in Drawn(Timer(blackout: false, watermark: "")))
            Assert.True(Count(px, Corner, Whitish) == 0, $"{leg}: a mark with no watermark");
    }

    /// <summary>A mark longer than the picture's width is drawn smaller to fit between the side insets, to its bound in characters,
    /// and still nothing but it under a blackout.</summary>
    [Fact]
    public void AWatermarkWiderThanThePictureIsDrawnSmallerWithinItsInsets()
    {
        var mark = string.Concat(Enumerable.Repeat(Mark + " · ", 8));
        Assert.True(mark.Length > TimerView.MaxWatermark);
        Assert.Equal(mark[..TimerView.MaxWatermark], TimerView.Read(Timer(blackout: true, watermark: mark)).Watermark);
        var band = (0, (int)(H * 0.93), W, H);
        var left = (int)Math.Floor(W * 0.02) - 1;
        var right = (int)Math.Ceiling(W * 0.98) + 1;
        foreach (var (leg, px) in Drawn(Timer(blackout: true, watermark: mark)))
        {
            Assert.True(BrightestOutside(px, band) == 0, $"{leg}: something but the watermark is drawn under a blackout");
            Assert.True(Count(px, (0, band.Item2, left, H), Lit) == 0, $"{leg}: the mark runs past the left inset");
            Assert.True(Count(px, (right, band.Item2, W, H), Lit) == 0, $"{leg}: the mark runs past the right inset");
            Assert.True(Count(px, (left, band.Item2, W / 2, H), Whitish) >= 20, $"{leg}: the mark is not drawn across the width");
            Assert.True(Count(px, (W / 2, band.Item2, right, H), Whitish) >= 20, $"{leg}: the mark is not drawn to its right inset");
        }
    }

    /// <summary>On a small picture the mark keeps its 14 px floor: on a 640 × 200 picture 3.2% of the height would be 6.4 px.</summary>
    [Fact]
    public void OnASmallPictureTheWatermarkKeepsItsFloor()
    {
        const int h = 200;
        foreach (var (leg, px) in Drawn(Timer(blackout: true), W, h))
        {
            var rows = Rows(px, (W / 2, h / 2, W, h), p => Math.Min(p.R, Math.Min(p.G, p.B)) >= 100, W);
            Assert.True(rows >= 10, $"{leg}: the mark's ink spans {rows} rows, under its 14 px floor");
        }
    }

    /// <summary>No Stagewatch link draws "--:--" in grey, and a timer object that cannot be read says so; neither has a blackout or a
    /// mark, so the corner stays black.</summary>
    [Fact]
    public void NoLinkAndAnUnreadableObjectDrawNoMark()
    {
        static bool Grey((int R, int G, int B) p) => p.R is >= 60 and <= 120 && Math.Abs(p.R - p.G) <= 8 && Math.Abs(p.R - p.B) <= 8;
        foreach (var json in new[] { null, "{not json" })
        {
            Assert.Equal((false, ""), (TimerView.Read(json).Blackout, TimerView.Read(json).Watermark));
            foreach (var (leg, px) in Drawn(json))
            {
                Assert.True(Count(px, Middle, Grey) >= 200, $"{leg} ({json ?? "no link"}): no grey --:--");
                Assert.True(Count(px, Corner, Lit) == 0, $"{leg} ({json ?? "no link"}): a mark with none to draw");
            }
        }
    }

    [Fact]
    public void APictureIsDrawnAgainOnlyWhenTheTimersObjectChanges()
    {
        var json = Timer(blackout: false);
        using var view = new TimerView(() => json, W, H);
        Assert.True(view.Refresh());
        using (var first = view.TakePicture())
            Assert.NotNull(first);
        Assert.False(view.Refresh());
        Assert.Null(view.TakePicture());

        json = Timer(blackout: true);
        Assert.True(view.Refresh());
        using var second = view.TakePicture();
        Assert.NotNull(second);
        Assert.Equal(2, view.Pictures);
        Assert.True(TimerView.Read(json).Blackout);
        Assert.Equal(Mark, TimerView.Read(json).Watermark);
    }
}
