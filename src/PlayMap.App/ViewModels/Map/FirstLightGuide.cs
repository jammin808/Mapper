using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels.Map;

/// <summary>The first-light guide "Light a wall", three steps read from STATE.onboarding.stage: light a wall (one button per display
/// DISPLAYS names, each sending LIGHT WALL &lt;display&gt;), click its four corners on the wall (the quad tool on the lit projector's
/// view), and drop a clip on it (SURFACE SOURCE &lt;the quad&gt; media:&lt;id&gt; or playlist:&lt;id&gt;). It shows while the first map
/// is not made yet — on a show with no projector before the first light, then until the first content — and closes for good on this
/// desk with ×.</summary>
public sealed class FirstLightGuide : ObservableObject
{
    private readonly DeskSession _session;
    private readonly Action<string> _lit;
    private readonly Dictionary<string, DeskAction> _lights = new(StringComparer.Ordinal);
    private string _stage = "";
    private double _seconds = -1;
    private bool _dismissed;
    private bool _hasProjector;
    private bool _displaysAsked;
    private string? _quad;
    private Task _lighting = Task.CompletedTask;

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
    }

    /// <summary>Step 1's buttons: one LIGHT WALL per display this machine has (the desk's own screen last).</summary>
    public ObservableCollection<DeskAction> Lights { get; } = [];

    /// <summary>Step 2: the quad tool on the lit projector.</summary>
    public DeskAction DrawQuad { get; }

    /// <summary>Step 3: Choose a clip ▸ — the show's media and playlists, each sending SURFACE SOURCE on the quad.</summary>
    public DeskAction Clip { get; }

    public RelayCommand Dismiss { get; }

    /// <summary>The LIGHT WALL a press started (tests wait on it).</summary>
    public Task Lighting => _lighting;

    public bool IsVisible => !_dismissed && _stage is "none" or "lit" or "quad" && (_stage != "none" || !_hasProjector);

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

    /// <summary>"First map in 1:24" once the first content is up.</summary>
    public string Seconds => _seconds < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{(int)(_seconds / 60)}:{(int)(_seconds % 60):00}");

    public void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var onboarding = state.Root.ValueKind == JsonValueKind.Object && state.Root.TryGetProperty("onboarding", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var stage = onboarding.ValueKind == JsonValueKind.Object ? onboarding.Str("stage") : "";
        _seconds = onboarding.ValueKind == JsonValueKind.Object ? onboarding.Num("seconds", -1) : -1;
        if (stage == _stage)
            return;
        _stage = stage;
        if (Step == 1 && IsVisible && !_displaysAsked)
            _ = AskDisplaysAsync();
        Changed();
    }

    public void OnProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _hasProjector = project.Stage.Projectors.Length > 0;
        _quad = project.Stage.Surfaces.LastOrDefault(s => s.Pixel is not null)?.Id;
        BuildClips(project);
        if (Step == 1 && IsVisible && !_displaysAsked)
            _ = AskDisplaysAsync();
        Changed();
    }

    public void RefreshActions()
    {
        Lights.RefreshAll();
        DrawQuad.Refresh();
        Clip.Children.RefreshAll();
    }

    /// <summary>Reads the displays once the first step shows (DISPLAYS, quietly).</summary>
    private async Task AskDisplaysAsync()
    {
        _displaysAsked = true;
        var reply = await _session.SendQuietAsync("DISPLAYS");
        if (!reply.Ok)
        {
            _displaysAsked = false;
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(reply.Text);
            var rows = doc.RootElement.EnumerateArray().Select(d => (Key: d.Str("key"), Name: d.Str("name"), Primary: d.Bool("primary"))).Where(d => d.Key.Length > 0)
                .OrderBy(d => d.Primary).ToList();
            Lights.Clear();
            foreach (var (key, name, primary) in rows)
            {
                if (!_lights.TryGetValue(key, out var a))
                {
                    var display = key;
                    a = DeskAction.Run(_session, "guide.light." + key, "Light " + (name.Length > 0 ? name : key) + (primary ? " (this screen)" : ""),
                        ct => LightAsync(display, ct), () => "LIGHT WALL " + VerbText.Word(display), isEnabled: () => _session.IsLive).Look("projector");
                    _lights[key] = a;
                }
                Lights.Add(a);
            }
            Raise(nameof(Words));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // An answer that is not the display list names no display: the step says so and asks again on the next show.
            _displaysAsked = false;
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
            return had;
        var a = DeskAction.Send(_session, "guide.clip." + source, label, () => _quad is { } q ? $"SURFACE SOURCE {q} {source}" : null,
            isEnabled: () => _session.IsLive && _quad is not null).Look("media");
        _clips[source] = a;
        return a;
    }

    /// <summary>The quad the third step sends a source to (the newest pixel quad), or null.</summary>
    public string? QuadId => _quad;

    private void Changed() => Raise(nameof(IsVisible), nameof(Step), nameof(IsLightStep), nameof(IsQuadStep), nameof(IsClipStep), nameof(Title),
        nameof(Words), nameof(Seconds), nameof(QuadId));
}
