using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels.Map;

/// <summary>The first-light guide "Light a wall", three steps read from STATE.onboarding.stage: light a wall (one button per display
/// DISPLAYS names, each sending LIGHT WALL &lt;display&gt;; DISPLAYS is read again each time the step shows, and on Look again),
/// click its four corners on the wall (the quad tool on the lit projector's view), and drop a clip on it (SURFACE SOURCE &lt;the
/// quad&gt; media:&lt;id&gt; or playlist:&lt;id&gt;). It shows only on the first-light path: on a show with no projector before any light,
/// then — once a wall was lit in this run (onboarding.litAt) — until the first content, which it marks with "First map in 1:24"
/// (onboarding.seconds) as it closes. A show that draws a quad without a light never sees it; × closes it for good on this desk.</summary>
public sealed class FirstLightGuide : ObservableObject
{
    private readonly DeskSession _session;
    private readonly Action<string> _lit;
    private readonly Dictionary<string, DeskAction> _lights = new(StringComparer.Ordinal);
    private string _stage = "";
    private double _seconds = -1;
    private bool _dismissed;
    private bool _hasProjector;
    private bool _litThisRun;
    private bool _asking;
    private string? _quad;
    private Task _lighting = Task.CompletedTask;
    private Task _reading = Task.CompletedTask;

    /// <param name="lit">The wall is lit on this projector (the map shows its view with the quad tool armed).</param>
    /// <param name="drawQuad">Arms the quad tool on the lit projector's view.</param>
    public FirstLightGuide(DeskSession session, Action<string> lit, Action drawQuad)
    {
        _session = session;
        _lit = lit;
        Dismiss = new RelayCommand(() =>
        {
            _dismissed = true;
            Changed();
        });
        DrawQuad = DeskAction.Local("guide.quad", "Draw the quad (Q)", drawQuad).Look("surface");
        Clip = DeskAction.Local("guide.clip", "Choose a clip", () => { }).Look("media");
        LookAgain = DeskAction.Run(session, "guide.displays", "Look again", _ => ReadDisplays(), () => "DISPLAYS", isEnabled: () => session.IsLive && !_asking);
    }

    /// <summary>Step 1 with no display listed: DISPLAYS once more (the projector was connected since).</summary>
    public DeskAction LookAgain { get; }

    public bool CanLookAgain => IsLightStep && Lights.Count == 0;

    /// <summary>Step 1's buttons: one LIGHT WALL per display this machine has (the desk's own screen last).</summary>
    public ObservableCollection<DeskAction> Lights { get; } = [];

    /// <summary>Step 2: the quad tool on the lit projector.</summary>
    public DeskAction DrawQuad { get; }

    /// <summary>Step 3: Choose a clip ▸ — the show's media and playlists, each sending SURFACE SOURCE on the quad.</summary>
    public DeskAction Clip { get; }

    public RelayCommand Dismiss { get; }

    /// <summary>The LIGHT WALL a press started (tests wait on it).</summary>
    public Task Lighting => _lighting;

    /// <summary>The DISPLAYS read in flight, or the last one (tests wait on it).</summary>
    public Task Reading => _reading;

    public bool IsVisible => !_dismissed && (_stage == "none" ? !_hasProjector : _stage is "lit" or "quad" && _litThisRun);

    /// <summary>1 light a wall · 2 the quad · 3 a clip.</summary>
    public int Step => _stage switch
    {
        "lit" => 2,
        "quad" => 3,
        _ => 1,
    };

    public bool IsLightStep => Step == 1;

    public bool IsQuadStep => Step == 2;

    public bool IsClipStep => Step == 3;

    public string Title => Step switch
    {
        1 => "1 · Light a wall",
        2 => "2 · Click the four corners of your wall — on the wall",
        _ => "3 · Drop a clip on it",
    };

    public string Words => Step switch
    {
        1 => Lights.Count == 0 ? "No display to light yet — connect the projector, then try again." : "Choose the display the projector is on: it goes full screen as a projector, with the outlines on the wall.",
        2 => "The quad tool is armed: click the corners on the projector's view; each click shows on the wall. Enter draws it.",
        _ => _quad is null ? "Draw a quad first." : "Choose what it shows.",
    };

    /// <summary>"1:24": from the node's start to its first map (onboarding.seconds), "" before it.</summary>
    public string Seconds => _seconds < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{(int)(_seconds / 60)}:{(int)(_seconds % 60):00}");

    public void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var onboarding = state.Root.ValueKind == JsonValueKind.Object && state.Root.TryGetProperty("onboarding", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var stage = onboarding.ValueKind == JsonValueKind.Object ? onboarding.Str("stage") : "";
        _seconds = onboarding.ValueKind == JsonValueKind.Object ? onboarding.Num("seconds", -1) : -1;
        var lit = onboarding.ValueKind == JsonValueKind.Object && onboarding.Num("litAt", 0) > 0;
        if (stage == _stage && lit == _litThisRun)
            return;
        var wasVisible = IsVisible;
        var shownBefore = Step == 1 && wasVisible;
        _stage = stage;
        _litThisRun = lit;
        // The first content of a guided first map: the guide closes with how long it took.
        if (wasVisible && stage == "content" && Seconds.Length > 0)
            _session.Notify(ToastKind.Info, "First map in " + Seconds);
        if (Step == 1 && IsVisible && !shownBefore)
            _ = ReadDisplays();
        Changed();
    }

    public void OnProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var shownBefore = Step == 1 && IsVisible;
        _hasProjector = project.Stage.Projectors.Length > 0;
        _quad = project.Stage.Surfaces.LastOrDefault(s => s.Pixel is not null)?.Id;
        BuildClips(project);
        if (Step == 1 && IsVisible && !shownBefore)
            _ = ReadDisplays();
        Changed();
    }

    /// <summary>MAP is shown: step 1 reads the displays again (one may have been connected since).</summary>
    public void Shown()
    {
        if (Step == 1 && IsVisible)
            _ = ReadDisplays();
    }

    public void RefreshActions()
    {
        Lights.RefreshAll();
        DrawQuad.Refresh();
        LookAgain.Refresh();
        Clip.Children.RefreshAll();
    }

    /// <summary>Reads the displays (DISPLAYS, quietly), one read at a time.</summary>
    private Task ReadDisplays()
    {
        if (!_asking)
            _reading = AskDisplaysAsync();
        return _reading;
    }

    /// <summary>A display's button keeps its instance from read to read and takes its name as the node gives it now.</summary>
    private async Task AskDisplaysAsync()
    {
        _asking = true;
        LookAgain.Refresh();
        try
        {
            var reply = await _session.SendQuietAsync("DISPLAYS");
            if (reply.Ok)
                TakeDisplays(reply.Text);
        }
        finally
        {
            _asking = false;
            LookAgain.Refresh();
        }
    }

    private void TakeDisplays(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.EnumerateArray().Select(d => (Key: d.Str("key"), Name: d.Str("name"), Primary: d.Bool("primary"))).Where(d => d.Key.Length > 0)
                .OrderBy(d => d.Primary).ToList();
            var want = new List<DeskAction>();
            foreach (var (key, name, primary) in rows)
            {
                var label = "Light " + (name.Length > 0 ? name : key) + (primary ? " (this screen)" : "");
                if (_lights.TryGetValue(key, out var a))
                    a.SetLabel(label);
                else
                {
                    var display = key;
                    a = DeskAction.Run(_session, "guide.light." + key, label, ct => LightAsync(display, ct), () => "LIGHT WALL " + VerbText.Word(display),
                        isEnabled: () => _session.IsLive).Look("projector");
                    _lights[key] = a;
                }
                want.Add(a);
            }
            if (!want.SequenceEqual(Lights))
            {
                Lights.Clear();
                foreach (var a in want)
                    Lights.Add(a);
            }
            Raise(nameof(Words), nameof(CanLookAgain));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // An answer that is not the display list names no display: the step says so, and Look again asks once more.
            Raise(nameof(Words), nameof(CanLookAgain));
        }
    }

    private Task LightAsync(string display, CancellationToken ct)
    {
        _lighting = LightCoreAsync(display, ct);
        return _lighting;
    }

    private async Task LightCoreAsync(string display, CancellationToken ct)
    {
        var reply = await _session.SendAsync("LIGHT WALL " + VerbText.Word(display), ct);
        if (reply.Ok && QuadTool.Field(reply.Text, "projector") is { Length: > 0 } pj)
            _lit(pj);
    }

    /// <summary>Step 3's menu: each media item and playlist of the show, sent as the quad's source.</summary>
    private void BuildClips(Project p)
    {
        var want = new List<DeskAction>();
        foreach (var m in p.Media)
            want.Add(ClipAction("media:" + m.Id, "Clip · " + m.Name));
        foreach (var pl in p.Playlists)
            want.Add(ClipAction("playlist:" + pl.Id, "Playlist · " + pl.Name));
        if (want.SequenceEqual(Clip.Children))
            return;
        Clip.Children.Clear();
        foreach (var a in want)
            Clip.Children.Add(a);
    }

    private readonly Dictionary<string, DeskAction> _clips = new(StringComparer.Ordinal);

    private DeskAction ClipAction(string source, string label)
    {
        if (_clips.TryGetValue(source, out var had))
        {
            had.SetLabel(label);
            return had;
        }
        var a = DeskAction.Send(_session, "guide.clip." + source, label, () => _quad is { } q ? $"SURFACE SOURCE {q} {source}" : null,
            isEnabled: () => _session.IsLive && _quad is not null).Look("media");
        _clips[source] = a;
        return a;
    }

    /// <summary>The quad the third step sends a source to (the newest pixel quad), or null.</summary>
    public string? QuadId => _quad;

    private void Changed() => Raise(nameof(IsVisible), nameof(Step), nameof(IsLightStep), nameof(IsQuadStep), nameof(IsClipStep), nameof(Title),
        nameof(Words), nameof(Seconds), nameof(QuadId), nameof(CanLookAgain));
}
