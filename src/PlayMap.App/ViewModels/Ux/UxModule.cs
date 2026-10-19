using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Avalonia.Input;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels.Ux;

/// <summary>The consoles' safety patterns the desk adds to the show strip's: the GO guard made visible (<see cref="GoGuard"/>, its
/// refusal in the attention bar), blind made unmistakable (the grey variant, the word BLIND on every pane that edits the draft, the
/// window's frame in the blind colour, only while the draft is this desk's), the Oops history, the dark room and daylight themes with a
/// desk brightness of its own, and a confirmation before the desk closes while the show is armed. Everything it sends is a catalogue
/// line; the look and the brightness are this desk's preferences, never the show's.</summary>
public sealed class UxModule : DeskModule
{
    /// <summary>The words the close confirmation says: the desk going changes nothing on the node.</summary>
    public const string CloseArmedWords = "The show keeps playing on the node";

    private readonly DeskSession _session;
    private readonly DeskPrefs _prefs;
    private readonly Func<bool> _ownsDraft;
    private readonly ImmutableArray<DeskAction> _noActions = [];
    private bool _armed;
    private bool _blindMine;
    private int _draftChanges = -1;
    private DeskPalette _palette = DeskPalette.Dark;
    private bool _closeAsked;
    private bool _closeConfirmed;

    /// <param name="ownsDraft">Whether STATE.show.blind names this desk's own session (the show strip's reading of it).</param>
    public UxModule(DeskSession session, DeskPrefs prefs, Func<long> nowMs, Func<bool> ownsDraft)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(prefs);
        _session = session;
        _prefs = prefs;
        _ownsDraft = ownsDraft;
        Guard = new GoGuard(nowMs);
        session.GoGuard = Guard;
        session.Replied += (line, reply) => session.OnUi(() => Guard.Replied(line, reply.Ok, reply.Text, MinGoMs(session.State)));
        Guard.Changed += () => GuardMoved?.Invoke();
        Oops = new OopsPanelViewModel(session);
        Themes = [.. Enum.GetValues<DeskTheme>().Select(t => DeskAction.Local("look." + t.ToString().ToLowerInvariant(), ThemeName(t),
            () => prefs.Theme = t, isChecked: () => prefs.Theme == t))];
        Brighter = DeskAction.Local("look.brighter", "Brighter", () => prefs.Brightness += 10, isEnabled: () => prefs.Brightness < DeskPrefs.MaxBrightness);
        Darker = DeskAction.Local("look.darker", "Darker", () => prefs.Brightness -= 10, isEnabled: () => prefs.Brightness > DeskPrefs.MinBrightness);
        Look = DeskAction.Local("look", "Look", () => { }).Look("eye");
        foreach (var t in Themes)
            Look.Children.Add(t);
        Look.Children.Add(Brighter.Look(separator: true));
        Look.Children.Add(Darker);
        LookMenu = [.. Look.Children];
        CloseAnyway = new RelayCommand(() =>
        {
            _closeConfirmed = true;
            IsCloseAsked = false;
            CloseNow?.Invoke();
        });
        StayOpen = new RelayCommand(() => IsCloseAsked = false);
        prefs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DeskPrefs.Theme) or nameof(DeskPrefs.Brightness))
            {
                UpdateLook();
                Raise();
            }
        };
        UpdateLook();
    }

    public GoGuard Guard { get; }

    public OopsPanelViewModel Oops { get; }

    /// <summary>The Look button (its sub-actions are <see cref="LookMenu"/>).</summary>
    public DeskAction Look { get; }

    /// <summary>The look's entries: a theme each, then brighter and darker (the Look button's menu and the help's).</summary>
    public IReadOnlyList<DeskAction> LookMenu { get; }

    public IReadOnlyList<DeskAction> Themes { get; }

    public DeskAction Brighter { get; }

    public DeskAction Darker { get; }

    /// <summary>The palette the desk draws in now: the theme's, in grey while this desk edits its blind draft.</summary>
    public DeskPalette Palette => _palette;

    /// <summary>True while STATE.show.blind names this desk's own session: the desk is grey, every pane that edits the draft says
    /// BLIND, and the window's frame takes the blind colour.</summary>
    public bool IsBlindMine => _blindMine;

    /// <summary>"BLIND · edits go to this desk's draft".</summary>
    public string BlindTag => "BLIND · edits go to this desk's draft";

    /// <summary>The veil over the whole desk for its brightness: 0 at 100 %, 0.8 at 20 %.</summary>
    public double VeilOpacity => 1 - (_prefs.Brightness / 100.0);

    /// <summary>"Brightness 60 %".</summary>
    public string BrightnessText => string.Create(CultureInfo.InvariantCulture, $"Brightness {_prefs.Brightness} %");

    /// <summary>Raised when the palette moved (the window recolours the desk's tokens).</summary>
    public event Action<DeskPalette>? LookChanged;

    /// <summary>Raised when the GO guard opened, closed, flashed or its words moved (the window repaints the GO controls and the bar,
    /// and wakes when the guard is next due).</summary>
    public event Action? GuardMoved;

    /// <summary>Raised when one of the module's own properties moved.</summary>
    public event Action? Changed;

    /// <summary>The window's close itself (set by the window): the confirmation's "Close the desk" runs it.</summary>
    public Action? CloseNow { get; set; }

    /// <summary>True while the close confirmation is up.</summary>
    public bool IsCloseAsked
    {
        get => _closeAsked;
        private set
        {
            if (_closeAsked == value)
                return;
            _closeAsked = value;
            Raise();
        }
    }

    public RelayCommand CloseAnyway { get; }

    public RelayCommand StayOpen { get; }

    /// <summary>The window is about to close: true to hold it (armed and not yet confirmed: the confirmation goes up), false to let it
    /// close. Closing the desk sends nothing; the node keeps the show.</summary>
    public bool HoldClose()
    {
        if (!_armed || _closeConfirmed)
            return false;
        IsCloseAsked = true;
        return true;
    }

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var show = state.Root.ValueKind == JsonValueKind.Object && state.Root.TryGetProperty("show", out var s) && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
        _armed = show?.Bool("armed") ?? false;
        if (!_armed)
            IsCloseAsked = false;
        var mine = _ownsDraft();
        var changes = show?.Obj("blind") is { } b && b.Bool("on") ? (int)b.Long("changes") : -1;
        if (mine != _blindMine)
        {
            _blindMine = mine;
            UpdateLook();
            Raise();
        }
        _draftChanges = changes;
        Oops.Moved(_session.ProjectRevision, _draftChanges);
    }

    public override void OnProject(Project project) => Oops.Moved(_session.ProjectRevision, _draftChanges);

    public override void RefreshActions()
    {
        Oops.RefreshActions();
        LookMenu.RefreshAll();
    }

    public override void Tick() => Guard.Tick();

    /// <summary>The GO the node refused, in its words, while the window it hit is open.</summary>
    public override IEnumerable<Banner> Banners(StateSnapshot state)
    {
        if (Guard.RefusedWords is { } words)
            yield return new Banner("go-guard", "amber", "GO refused — " + words, _noActions, 5);
    }

    public override IEnumerable<Shortcut> Shortcuts() =>
    [
        new("Ctrl+Alt+Z", "The Oops history: every undo step, newest first, each with Undo to here (UNDO LIST, UNDO TO)", "Desk", Key.Z,
            KeyModifiers.Control | KeyModifiers.Alt, Oops.Toggle.Command) { WhileTyping = true },
        new("Ctrl+Alt+D", "The look: dark, the dark room, daylight (this desk only)", "Desk", Key.D, KeyModifiers.Control | KeyModifiers.Alt,
            new RelayCommand(() => _prefs.Theme = (DeskTheme)(((int)_prefs.Theme + 1) % 3))) { WhileTyping = true },
        new("Ctrl+Alt+Up", "The desk brighter by 10 % (this desk only, apart from the machine's)", "Desk", Key.Up, KeyModifiers.Control | KeyModifiers.Alt, Brighter.Command),
        new("Ctrl+Alt+Down", "The desk darker by 10 %, down to 20 % (this desk only)", "Desk", Key.Down, KeyModifiers.Control | KeyModifiers.Alt, Darker.Command),
    ];

    /// <summary>STATE.show.minGoMs: the window the node's GO fence holds now (0 when the node has none or no Show Mode).</summary>
    public static int MinGoMs(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var root = state.Root;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object
            ? (int)show.Long("minGoMs")
            : 0;
    }

    public static string ThemeName(DeskTheme theme) => theme switch
    {
        DeskTheme.Dim => "Dark room",
        DeskTheme.Daylight => "Daylight",
        _ => "Dark",
    };

    private void UpdateLook()
    {
        var palette = DeskPalette.Of(_prefs.Theme);
        if (_blindMine)
            palette = palette.Grey();
        if (palette.Name == _palette.Name)
            return;
        _palette = palette;
        LookChanged?.Invoke(palette);
    }

    private void Raise() => Changed?.Invoke();
}
