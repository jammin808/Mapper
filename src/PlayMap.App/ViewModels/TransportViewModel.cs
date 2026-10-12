using System.Diagnostics;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;

namespace PlayMap.App.ViewModels;

/// <summary>A playlist's transport as buttons and counters: previous, play/pause, stop, next — the master
/// transport in the top bar (the focused playlist) and every playlist card's own. Play on an idle or ended
/// playlist is PLAYLIST GO (VIDEO PLAY would be refused: nothing is loaded); otherwise the clip verbs, ON the
/// playlist, so a press means the same whatever has the focus. Counters run on between STATE pushes from
/// the last push's position and rate; the next push corrects them.</summary>
public sealed class TransportViewModel : ObservableObject
{
    private readonly Func<PlaylistState?> _state;
    private long _baseMs;
    private long _lengthMs;
    private double _rate = 1;
    private long _baseStamp;
    private bool _running;
    private string _positionText = "0:00";
    private string _remainingText = "";
    private string _lengthText = "";
    private string _stateWord = "idle";
    private string _itemLabel = "";
    private string _nextLabel = "";
    private double _progress;
    private bool _isPlaying;
    private bool _hasPlaylist;

    public TransportViewModel(DeskSession session, Func<PlaylistState?> state, bool master)
    {
        ArgumentNullException.ThrowIfNull(session);
        _state = state;
        string? Id() => _state()?.Id;
        Prev = DeskAction.Send(session, "prev", "Previous", () => Id() is { } id ? "PLAYLIST PREV " + id : null,
            isEnabled: () => Id() is not null).Look("prev", master ? "Ctrl+Left" : null);
        PlayPause = DeskAction.Send(session, "play", "Play", PlayVerb, () => _state()?.IsPlaying == true ? "Pause" : "Play",
            isEnabled: () => Id() is not null).Look("play", master ? "Space" : null);
        Stop = DeskAction.Send(session, "stop", "Stop", () => Id() is { } id ? "VIDEO STOP ON " + id : null,
            isEnabled: () => _state() is { IsIdle: false }).Look("stop", master ? "Shift+Space" : null);
        Next = DeskAction.Send(session, "next", "Next", () => Id() is { } id ? "PLAYLIST NEXT " + id : null,
            isEnabled: () => Id() is not null).Look("next", master ? "Ctrl+Right" : null);
        Actions = [Prev, PlayPause, Stop, Next];
    }

    private string? PlayVerb()
    {
        var s = _state();
        if (s is null)
            return null;
        if (s.IsPlaying)
            return "VIDEO PAUSE ON " + s.Id;
        if (s.IsIdle || s.State == "ended")
            return "PLAYLIST GO " + s.Id;
        return "VIDEO PLAY ON " + s.Id;
    }

    public DeskAction Prev { get; }

    public DeskAction PlayPause { get; }

    public DeskAction Stop { get; }

    public DeskAction Next { get; }

    public IReadOnlyList<DeskAction> Actions { get; }

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => Set(ref _remainingText, value);
    }

    public string LengthText
    {
        get => _lengthText;
        private set => Set(ref _lengthText, value);
    }

    /// <summary>"playing", "paused", "cued", "ended", "idle".</summary>
    public string StateWord
    {
        get => _stateWord;
        private set => Set(ref _stateWord, value);
    }

    public string ItemLabel
    {
        get => _itemLabel;
        private set => Set(ref _itemLabel, value);
    }

    public string NextLabel
    {
        get => _nextLabel;
        private set => Set(ref _nextLabel, value);
    }

    /// <summary>0..1 through the current item.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    public bool HasPlaylist
    {
        get => _hasPlaylist;
        private set => Set(ref _hasPlaylist, value);
    }

    /// <summary>Takes a STATE push.</summary>
    public void Update()
    {
        var s = _state();
        HasPlaylist = s is not null;
        if (s is null || s.IsIdle)
        {
            _running = false;
            _baseMs = 0;
            _lengthMs = 0;
            StateWord = s is null ? "no playlist" : "idle";
            ItemLabel = s is null ? "" : s.Next is { } n ? "Next: " + n.Label : "Empty";
            NextLabel = "";
            IsPlaying = false;
        }
        else
        {
            _baseMs = s.PositionMs;
            _lengthMs = s.LengthMs;
            _rate = s.Rate;
            _running = s.IsPlaying;
            _baseStamp = Stopwatch.GetTimestamp();
            StateWord = s.State;
            ItemLabel = s.Item?.Label ?? "";
            NextLabel = s.Next is { } n ? "Next: " + n.Label : "";
            IsPlaying = s.IsPlaying;
        }
        Advance();
        Actions.RefreshAll();
    }

    /// <summary>Moves the counters on to now (a 4 Hz tick on the UI thread while something plays).</summary>
    public void Advance()
    {
        var pos = _baseMs;
        if (_running)
            pos += (long)(Stopwatch.GetElapsedTime(_baseStamp).TotalMilliseconds * _rate);
        if (_lengthMs > 0)
            pos = Math.Min(pos, _lengthMs);
        PositionText = VerbText.Clock(pos);
        LengthText = _lengthMs > 0 ? VerbText.Clock(_lengthMs) : "";
        RemainingText = _lengthMs > 0 ? "−" + VerbText.Clock(_lengthMs - pos, countdown: true) : "";
        Progress = _lengthMs > 0 ? Math.Clamp(pos / (double)_lengthMs, 0, 1) : 0;
    }

    public bool IsRunning => _running;
}
