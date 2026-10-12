using System.Collections.Immutable;
using System.Globalization;

namespace PlayMap.Core.Model;

/// <summary>What a picture comes from.</summary>
public enum SourceKind
{
    None,
    /// <summary>A library item: video, still, image sequence, audio, web page or stream.</summary>
    Media,
    /// <summary>A playlist's running output (its current item, with its transitions).</summary>
    Playlist,
    /// <summary>A generator: a test pattern, a fractal, a particle field, a reactive scene.</summary>
    Generator,
    /// <summary>A composed 2D canvas of layers.</summary>
    Canvas,
    /// <summary>A live input: a capture device, a camera, an NDI feed.</summary>
    Live,
    /// <summary>A Stagewatch timer view (its state frames rendered as a picture).</summary>
    Timer,
    /// <summary>A flat colour (the Id is #RRGGBB).</summary>
    Color,
}

/// <summary>A reference to a picture source by kind and id.</summary>
public sealed record SourceRef(SourceKind Kind, string Id)
{
    public static readonly SourceRef None = new(SourceKind.None, "");

    public static SourceRef Media(string id) => new(SourceKind.Media, id);
    public static SourceRef Playlist(string id) => new(SourceKind.Playlist, id);
    public static SourceRef Generator(string id) => new(SourceKind.Generator, id);
    public static SourceRef Canvas(string id) => new(SourceKind.Canvas, id);
    public static SourceRef Live(string id) => new(SourceKind.Live, id);
    public static SourceRef Timer(string id) => new(SourceKind.Timer, id);
    public static SourceRef Solid(string hex) => new(SourceKind.Color, hex);

    public bool IsNone => Kind == SourceKind.None;

    /// <summary>The wire form: "media:md1a2b3c4d", "color:#FF0000", "none".</summary>
    public override string ToString() => IsNone ? "none" : $"{Kind.ToString().ToLowerInvariant()}:{Id}";

    public static bool TryParse(string? text, out SourceRef source)
    {
        source = None;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;
        var colon = t.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == t.Length - 1)
            return false;
        if (!Enum.TryParse<SourceKind>(t[..colon], ignoreCase: true, out var kind) || kind == SourceKind.None)
            return false;
        var id = t[(colon + 1)..];
        if (kind != SourceKind.Color && !Ids.IsValid(id))
            return false;
        source = new SourceRef(kind, id);
        return true;
    }
}

/// <summary>Named parameters as invariant-culture strings, typed by the generator's schema
/// (<see cref="PlayMap.Core.Content.ParamSpec"/>). One shape for the inspector, the wire's SET, the
/// project file and an AI reading the vocabulary.</summary>
public sealed record ParamSet(ImmutableSortedDictionary<string, string> Values)
{
    public static readonly ParamSet Empty = new(ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;

    public double GetDouble(string name, double fallback) =>
        Values.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d : fallback;

    public int GetInt(string name, int fallback) =>
        Values.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public bool GetBool(string name, bool fallback) =>
        Values.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public string GetString(string name, string fallback) => Values.TryGetValue(name, out var v) ? v : fallback;

    public ParamSet With(string name, string value) => new(Values.SetItem(name, value));

    public ParamSet With(string name, double value) => With(name, value.ToString("R", CultureInfo.InvariantCulture));

    public ParamSet With(string name, int value) => With(name, value.ToString(CultureInfo.InvariantCulture));

    public ParamSet With(string name, bool value) => With(name, value ? "true" : "false");

    public ParamSet Without(string name) => new(Values.Remove(name));
}

/// <summary>A generator instance: a kind from the catalogue (<see cref="PlayMap.Core.Content.GeneratorCatalog"/>)
/// with its parameters — "pattern.testcard", "fractal.julia", "particles.snow".</summary>
public sealed record Generator(string Id, string Name, string Kind, ParamSet Params) : IEntity;

public enum LayerBlend
{
    Normal,
    Add,
    Multiply,
    Screen,
}

/// <summary>A rectangle in pixels of the space it lives in (a canvas, an output).</summary>
public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;

    public bool Contains(double x, double y) => x >= X && y >= Y && x < Right && y < Bottom;

    public RectD Intersect(RectD o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right);
        var b = Math.Min(Bottom, o.Bottom);
        return r > x && b > y ? new RectD(x, y, r - x, b - y) : default;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X:0.##},{Y:0.##} {W:0.##}x{H:0.##}");
}

/// <summary>One layer of a canvas: a source placed in a rectangle with an opacity and a blend.</summary>
public sealed record Layer(string Id, string Name, SourceRef Source, RectD Rect, double Opacity, LayerBlend Blend, bool Visible) : IEntity;

/// <summary>A 2D composition space. Surfaces and plain screens take regions of it; the multi-screen plan
/// lays outputs over it. Layers draw bottom to top.</summary>
public sealed record Canvas(string Id, string Name, int Width, int Height, string Background, ImmutableArray<Layer> Layers) : IEntity;
