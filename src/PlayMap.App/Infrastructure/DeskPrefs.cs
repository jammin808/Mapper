using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlayMap.App.Infrastructure;

/// <summary>The desk's own preferences (never the show's): Simple or Pro, and whether Esc in PLAY is PANIC. Kept in the
/// desk's desk.json beside its other files; a desk without a file (tests, a read-only folder) keeps them in memory.
/// Simple hides controls; it never disables one, and right-click menus and the show strip are the same in both.</summary>
public sealed class DeskPrefs : ObservableObject
{
    private readonly string? _path;
    private bool _isPro;
    private bool _escIsPanic;

    private DeskPrefs(string? path) => _path = path;

    /// <summary>Preferences held in memory only.</summary>
    public static DeskPrefs InMemory() => new(null);

    /// <summary>Reads desk.json at <paramref name="path"/> (missing or unreadable: the defaults, Simple and Esc not panic).</summary>
    public static DeskPrefs Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var prefs = new DeskPrefs(path);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                prefs._isPro = o["pro"]?.GetValueKind() == JsonValueKind.True;
                prefs._escIsPanic = o["escIsPanic"]?.GetValueKind() == JsonValueKind.True;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable desk.json is the defaults; the next change writes a fresh one.
            System.Diagnostics.Trace.WriteLine("PlayMap desk: desk.json unreadable, defaults kept: " + ex.Message);
        }
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

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var o = new JsonObject { ["pro"] = _isPro, ["escIsPanic"] = _escIsPanic };
            File.WriteAllText(_path, o.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The preference holds for this session; the desk says nothing more about a folder it cannot write.
            System.Diagnostics.Trace.WriteLine("PlayMap desk: desk.json not written: " + ex.Message);
        }
    }
}
