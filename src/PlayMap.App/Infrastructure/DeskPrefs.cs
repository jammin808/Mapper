using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Infrastructure;

/// <summary>The desk's own preferences (never the show's): Simple or Pro, whether Esc in PLAY is PANIC, the look (its theme and a
/// brightness of its own, apart from the machine's) and every unfinished quad, kept per node and projector until it is drawn or
/// discarded. Kept in the desk's desk.json beside its other files, every key it does not know kept as it was; a desk without a file
/// (tests, a read-only folder) keeps them in memory. Simple hides controls; it never disables one, and right-click menus and the show
/// strip are the same in both.</summary>
public sealed class DeskPrefs : ObservableObject
{
    /// <summary>The desk's brightness runs from this…</summary>
    public const int MinBrightness = 20;

    /// <summary>…to this, in per cent.</summary>
    public const int MaxBrightness = 100;

    private readonly string? _path;
    private readonly JsonObject _doc;
    private bool _isPro;
    private bool _escIsPanic;
    private DeskTheme _theme;
    private int _brightness = MaxBrightness;

    private DeskPrefs(string? path, JsonObject doc)
    {
        _path = path;
        _doc = doc;
    }

    /// <summary>Preferences held in memory only.</summary>
    public static DeskPrefs InMemory() => new(null, []);

    /// <summary>Reads desk.json at <paramref name="path"/> (missing or unreadable: the defaults, Simple, Esc not panic, dark at full
    /// brightness, no quads).</summary>
    public static DeskPrefs Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonObject doc = [];
        try
        {
            if (File.Exists(path) && JsonNode.Parse(PlayMap.Core.Storage.DurableFile.ReadAllText(path)) is JsonObject o)
                doc = o;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable desk.json is the defaults; the next change writes a fresh one.
            System.Diagnostics.Trace.WriteLine("PlayMap desk: desk.json unreadable, defaults kept: " + ex.Message);
        }
        var prefs = new DeskPrefs(path, doc)
        {
            _isPro = doc["pro"]?.GetValueKind() == JsonValueKind.True,
            _escIsPanic = doc["escIsPanic"]?.GetValueKind() == JsonValueKind.True,
        };
        if (doc["theme"] is JsonValue t && t.GetValueKind() == JsonValueKind.String && Enum.TryParse<DeskTheme>(t.GetValue<string>(), true, out var theme))
            prefs._theme = theme;
        if (doc["brightness"] is JsonValue b && b.GetValueKind() == JsonValueKind.Number)
            prefs._brightness = Math.Clamp(b.GetValue<int>(), MinBrightness, MaxBrightness);
        return prefs;
    }

    /// <summary>The default place: desk.json in the user's PlayMap folder.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayMap", "desk.json");

    public bool IsPro
    {
        get => _isPro;
        set
        {
            if (Set(ref _isPro, value))
            {
                Raise(nameof(IsSimple));
                Save();
            }
        }
    }

    public bool IsSimple => !_isPro;

    public bool EscIsPanic
    {
        get => _escIsPanic;
        set
        {
            if (Set(ref _escIsPanic, value))
                Save();
        }
    }

    /// <summary>The desk's theme: dark, the dark room or daylight.</summary>
    public DeskTheme Theme
    {
        get => _theme;
        set
        {
            if (Set(ref _theme, value))
                Save();
        }
    }

    /// <summary>The desk's own brightness, 20–100 %, apart from the machine's (a veil over the whole desk).</summary>
    public int Brightness
    {
        get => _brightness;
        set
        {
            if (Set(ref _brightness, Math.Clamp(value, MinBrightness, MaxBrightness)))
                Save();
        }
    }

    /// <summary>The unfinished quad on a node's projector: its placed points (wall-normalised, in the order placed), or null.</summary>
    public IReadOnlyList<Vec2d>? QuadDraft(string node, string projector)
    {
        if (_doc["quadDrafts"]?[node]?[projector] is not JsonArray points)
            return null;
        var list = new List<Vec2d>(points.Count);
        foreach (var p in points)
        {
            if (p is JsonArray { Count: 2 } xy && xy[0] is JsonValue x && xy[1] is JsonValue y
                && x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number)
                list.Add(new Vec2d(x.GetValue<double>(), y.GetValue<double>()));
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>Every projector of a node that holds an unfinished quad.</summary>
    public IReadOnlyList<string> QuadDraftProjectors(string node) =>
        _doc["quadDrafts"]?[node] is JsonObject drafts ? [.. drafts.Select(d => d.Key)] : [];

    /// <summary>Keeps (or, with no points, forgets) the unfinished quad on a node's projector, and writes desk.json.</summary>
    public void SetQuadDraft(string node, string projector, IReadOnlyList<Vec2d>? points)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(projector);
        if (_doc["quadDrafts"] is not JsonObject all)
        {
            all = [];
            _doc["quadDrafts"] = all;
        }
        if (all[node] is not JsonObject drafts)
        {
            drafts = [];
            all[node] = drafts;
        }
        if (points is null || points.Count == 0)
        {
            if (!drafts.Remove(projector))
                return;
            if (drafts.Count == 0)
                all.Remove(node);
        }
        else
        {
            var array = new JsonArray();
            foreach (var p in points)
                array.Add(new JsonArray(Round(p.X), Round(p.Y)));
            drafts[projector] = array;
        }
        Save();
    }

    private static double Round(double v) => Math.Round(v, 6);

    private void Save()
    {
        _doc["pro"] = _isPro;
        _doc["escIsPanic"] = _escIsPanic;
        _doc["theme"] = _theme.ToString().ToLowerInvariant();
        _doc["brightness"] = _brightness;
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, _doc.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The preference holds for this session; the desk says nothing more about a folder it cannot write.
            System.Diagnostics.Trace.WriteLine("PlayMap desk: desk.json not written: " + ex.Message);
        }
    }
}
