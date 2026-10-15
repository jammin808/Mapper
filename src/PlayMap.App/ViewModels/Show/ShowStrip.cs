using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;

namespace PlayMap.App.ViewModels.Show;

/// <summary>STATE.show as the strip reads it (absent: the node has no Show Mode, and the strip hides).</summary>
public sealed record ShowSection(bool Present, bool Armed, string By, string? PanicOutcome, string PanicBy)
{
    public static readonly ShowSection Absent = new(false, false, "", null, "");

    public static ShowSection Read(JsonElement? e)
    {
        if (e is not { ValueKind: JsonValueKind.Object } s)
            return Absent;
        string? outcome = null;
        var by = "";
        if (s.TryGetProperty("panic", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            outcome = p.TryGetProperty("outcome", out var o) ? o.GetString() : "black";
            by = p.TryGetProperty("by", out var b) ? b.GetString() ?? "" : "";
        }
        var armed = s.TryGetProperty("armed", out var a) && a.ValueKind == JsonValueKind.True;
        var who = s.TryGetProperty("by", out var w) ? w.GetString() ?? "" : "";
        return new ShowSection(true, armed, who, outcome, by);
    }
}

/// <summary>The top bar's show strip: ARM, PANIC (never a dialog), following STATE.show, never its own press.</summary>
public sealed class ShowStripViewModel : ObservableObject
{
    private ShowSection _show = ShowSection.Absent;

    public ShowStripViewModel(DeskSession session)
    {
        Arm = DeskAction.Send(session, "show.arm", "Arm", () => _show.Armed ? "SHOW DISARM" : "SHOW ARM",
            () => _show.Armed ? "ARMED · " + _show.By : "ARM", isChecked: () => _show.Armed, isEnabled: () => session.IsLive).Look("lock");
        Panic = DeskAction.Send(session, "show.panic", "PANIC", () => "PANIC", isEnabled: () => session.IsLive).Look("blackout", "F12", danger: true);
    }

    public DeskAction Arm { get; }

    public DeskAction Panic { get; }

    public bool IsPresent => _show.Present;

    public bool IsArmed => _show.Armed;

    public ShowSection Show => _show;

    public void Update(ShowSection show)
    {
        if (Set(ref _show, show, nameof(Show)))
            Raise(nameof(IsPresent), nameof(IsArmed));
        Arm.Refresh();
        Panic.Refresh();
    }
}

/// <summary>The show package's module (a prototype of D1's): the strip's slot and the panic banner.</summary>
public sealed class ShowStripModule(DeskSession session) : DeskModule
{
    private readonly DeskAction _clear = DeskAction.Send(session, "panic.clear", "Clear panic", () => "PANIC CLEAR").Look("play");

    public ShowStripViewModel Strip { get; } = new(session);

    public override object? Slot(string name) => name == "show" ? Strip : null;

    public override void OnState(StateSnapshot state) => Strip.Update(state.Section("show", ShowSection.Read));

    public override IEnumerable<Banner> Banners(StateSnapshot state)
    {
        var show = state.Section("show", ShowSection.Read);
        if (show.PanicOutcome is { } outcome)
            yield return new Banner("panic", "red", $"PANIC {outcome} by {show.PanicBy}", [_clear], 0);
    }
}
