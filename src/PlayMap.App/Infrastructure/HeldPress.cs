using PlayMap.App.ViewModels;

namespace PlayMap.App.Infrastructure;

/// <summary>A held action's menu entry, pressed twice: a menu has no hold, so its form of hold-to-confirm is a second press of the same
/// entry (the house rule: a risky line is held or pressed twice, as a Companion key pressed twice). The desk arms at most one entry at
/// a time. A first press arms the entry (its label then says what a second press sends, in the node's words); a second press of the
/// same entry within <see cref="WindowMs"/> of the desk's clock sends its line; anything else disarms it — another entry pressed, the
/// menu closing or made again, the window passing (a press after it arms the entry afresh). UI thread only.</summary>
public sealed class HeldPress(Func<long> nowMs)
{
    /// <summary>How long an armed entry waits for its second press: the Companion module's press-twice window.</summary>
    public const long WindowMs = 3000;

    private DeskAction? _armed;
    private long _armedAtMs;
    private Action? _revert;

    /// <summary>The entry armed now, while its window is open; null otherwise.</summary>
    public DeskAction? Armed => _armed is { } a && nowMs() - _armedAtMs <= WindowMs ? a : null;

    /// <summary>A press of a held entry: true when it is the second press of the armed entry within the window (the caller sends it,
    /// and nothing stays armed); false when it arms the entry, disarming any other. <paramref name="revert"/> puts the entry's own
    /// label back when it is disarmed.</summary>
    public bool Press(DeskAction action, Action? revert = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (ReferenceEquals(Armed, action))
        {
            var done = _revert;
            _armed = null;
            _revert = null;
            done?.Invoke();
            return true;
        }
        Disarm();
        _armed = action;
        _armedAtMs = nowMs();
        _revert = revert;
        return false;
    }

    /// <summary>Lets go of whatever is armed, its label put back.</summary>
    public void Disarm()
    {
        var revert = _revert;
        _armed = null;
        _revert = null;
        revert?.Invoke();
    }

    /// <summary>Lets go of <paramref name="action"/> if it is the one armed (its menu closed or was made again).</summary>
    public void Disarm(DeskAction action)
    {
        if (ReferenceEquals(_armed, action))
            Disarm();
    }

    /// <summary>An armed entry's label: what its second press sends, in the node's words.</summary>
    public static string ArmedLabel(DeskAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return "Press again to send " + action.Verb;
    }
}
