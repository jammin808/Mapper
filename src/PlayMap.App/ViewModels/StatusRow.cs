using PlayMap.App.Infrastructure;

namespace PlayMap.App.ViewModels;

/// <summary>One status line, the desk's most common part: a light, a title, a value, the words that explain it, and a
/// Fix that runs the fix line (a verb, never a private call). Rebuilt from STATE: <see cref="Update"/> raises only what
/// changed, so a push that moves one value repaints one value.</summary>
public sealed class StatusRow(string id) : ObservableObject
{
    private string _light = "grey";
    private string _title = "";
    private string _value = "";
    private string _words = "";
    private DeskAction? _fix;

    public string Id { get; } = id;

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Value
    {
        get => _value;
        private set => Set(ref _value, value);
    }

    public string Words
    {
        get => _words;
        private set
        {
            if (Set(ref _words, value))
                Raise(nameof(HasWords));
        }
    }

    public bool HasWords => _words.Length > 0;

    /// <summary>The fix, as an action sending its line; null when nothing on the wire fixes it.</summary>
    public DeskAction? Fix
    {
        get => _fix;
        private set
        {
            if (Set(ref _fix, value))
                Raise(nameof(HasFix));
        }
    }

    public bool HasFix => _fix is not null;

    public void Update(string light, string title, string value, string words, DeskAction? fix = null)
    {
        Light = light;
        Title = title;
        Value = value;
        Words = words;
        Fix = fix;
        fix?.Refresh();
    }
}
