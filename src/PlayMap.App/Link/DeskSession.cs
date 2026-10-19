using System.Collections.Frozen;
using System.Collections.ObjectModel;
using PlayMap.App.Infrastructure;
using PlayMap.App.State;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

public enum ToastKind
{
    Info,
    Done,
    Refused,
    Fault,
}

/// <summary>One line on the status strip: what happened, in plain words, and the verb that caused it.</summary>
public sealed record Toast(ToastKind Kind, string Text, string Verb, long Id, long At)
{
    public bool IsRefusal => Kind is ToastKind.Refused or ToastKind.Fault;

    /// <summary>"refused", "fault", "done", "info" — the strip's colour key.</summary>
    public string KindWord => Kind.ToString().ToLowerInvariant();

    public bool HasVerb => Verb.Length > 0;
}

/// <summary>Why the gate would refuse a line now: the node's own words, and the short tag a menu item carries after its label
/// ("armed", "locked", "mirror", "fenced", "not paired", "blind draft").</summary>
public sealed record GateRefusal(string Words, string Tag);

/// <summary>The desk's side of one engine: the link, the mirrored project and STATE (latest wins, rebuilt on
/// the UI thread at most once per dispatcher turn however fast the engine pushes), and the one door every
/// view model sends verbs through. A refusal comes back as ERR with the reason; the session turns it into a
/// toast in plain words. Nothing here edits the project — the engine's next push is the truth.</summary>
public sealed class DeskSession : ObservableObject, IAsyncDisposable
{
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private IEngineLink? _link;
    private bool _statePending;
    private bool _projectPending;
    private bool _statusPending;
    private long _toastIds;
    private StateSnapshot _state = StateSnapshot.Empty;
    private Project? _project;
    private long _projectRevision = -1;
    private LinkStatus _status = LinkStatus.Down;
    private string _linkLabel = "Not connected";
    private Toast? _lastToast;
    private long _linkEpoch;

    /// <param name="post">Runs an action on the UI thread later (the dispatcher's Post); tests pass the
    /// headless dispatcher's.</param>
    public DeskSession(Action<Action> post)
    {
        _post = post;
        // Drags and fields go through the throttle; a refusal of what it sends is said (once — the same
        // refusal repeated by a drag folds into one toast). A step the gate refuses is not sent: its words are said.
        Throttle = new ThrottledSender((line, ct) =>
        {
            if (GateWords(line) is { } why)
            {
                _post(() => Notify(ToastKind.Refused, why, line));
                return Task.CompletedTask;
            }
            return SendAsync(line, ct);
        });
    }

    /// <summary>The coalescing sender drags go through (≈ 60 Hz, latest wins per key).</summary>
    public ThrottledSender Throttle { get; }

    /// <summary>The desk's one armed menu entry of a held action (its menu form of hold-to-confirm), on the desk's clock: the window
    /// sets it with its own injected clock; the machine's until then.</summary>
    public HeldPress Presses { get; set; } = new(EscapeLadder.MonotonicMs(System.Diagnostics.Stopwatch.GetTimestamp()));

    /// <summary>The ring the node names for each preview view (from the VIEW … SHOW replies and STATE <c>machine.views</c>): the only
    /// files the desk's previews read. Emptied when the link changes.</summary>
    public PreviewNames Previews { get; } = new();

    private FrozenSet<string> _verbs = FrozenSet<string>.Empty;
    private Func<string, GateRefusal?>? _gateHook;
    private string _role = "operator";
    private string _sessionId = "";
    private string _transport = "";
    private bool _paired = true;
    private long _gateStamp;

    /// <summary>What Show Mode takes this desk for on the node (WHOAMI's role: "operator" for the desk on this machine or a
    /// listed desk address, "remote" otherwise); the gate decides with it, as the engine does.</summary>
    public string Role => _role;

    /// <summary>This desk's session on the node as WHOAMI names it ("" until it answers): STATE.show.blind names its draft's owner
    /// the same way.</summary>
    public string SessionId => _sessionId;

    /// <summary>The transport the node took this desk's session on (WHOAMI's), with <see cref="SessionId"/> the session's identity.</summary>
    public string Transport => _transport;

    /// <summary>Whether the node holds this desk's session as paired (WHOAMI's; true until it answers). An unpaired session's line
    /// that would change something is refused at the node's door.</summary>
    public bool Paired => _paired;

    /// <summary>Moves whenever what the gate reads moved (the facts a module's gate reads from STATE, the project, the node's verbs,
    /// the desk's identity on the node), so an action re-asks the gate only then: a push that moves only a playhead re-asks nothing.</summary>
    public long GateStamp => Interlocked.Read(ref _gateStamp);

    /// <summary>Marks the gate's facts as moved (a module whose facts change outside a push calls it).</summary>
    public void GateMoved() => Interlocked.Increment(ref _gateStamp);

    /// <summary>The verb names the node answered VERBS with at attach (empty until it answers: then every control
    /// shows). A control whose verb the node lacks hides, so a node without a package shows none of its controls.</summary>
    public IReadOnlySet<string> Verbs => _verbs;

    /// <summary>Raised on the UI thread when the node's verb list arrived.</summary>
    public event Action? VerbsUpdated;

    /// <summary>True when the node runs the verb a line starts with (or the list is not known yet).</summary>
    public bool Knows(string line)
    {
        if (_verbs.Count == 0 || string.IsNullOrWhiteSpace(line))
            return true;
        return WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && _verbs.Contains(m.Spec.Name);
    }

    /// <summary>The desk-wide refusal hook (the node's doors in its order: pairing, the fleet, the show's policy, the locks): why a
    /// line would be refused now, or null. Every <see cref="ViewModels.DeskAction"/> asks it on refresh, so a control greys with the
    /// gate's words before the press; the engine still decides.</summary>
    public Func<string, GateRefusal?>? Gate
    {
        get => _gateHook;
        set => _gateHook = value;
    }

    /// <summary>Why the gate would refuse this line now (its words and its tag), or null.</summary>
    public GateRefusal? GateRefusalOf(string line) => _gateHook is { } g && !string.IsNullOrWhiteSpace(line) ? g(line) : null;

    /// <summary>The words why the gate would refuse this line now, or null.</summary>
    public string? GateWords(string line) => GateRefusalOf(line)?.Words;

    public IEngineLink? Link => _link;

    /// <summary>Moves whenever the session takes or lets go of a link: a reply to a line sent before it moved came from a link the
    /// session no longer holds, and what it names is not this link's.</summary>
    public long LinkEpoch => Interlocked.Read(ref _linkEpoch);

    public StateSnapshot State => _state;

    /// <summary>The mirrored project (null until the engine's first push).</summary>
    public Project? Project => _project;

    public long ProjectRevision => _projectRevision;

    public LinkStatus Status => _status;

    public bool IsLive => _status == LinkStatus.Live;

    /// <summary>The link would carry a line now: live, or stale (quiet, still open). The air's own controls (PANIC, BLACKOUT, the
    /// master) stay live on a stale link; the reply, or its timeout, says what came of it.</summary>
    public bool CanSend => _status is LinkStatus.Live or LinkStatus.Stale;

    /// <summary>"Demo node", "This machine", "10.0.0.7:9941".</summary>
    public string LinkLabel
    {
        get => _linkLabel;
        private set => Set(ref _linkLabel, value);
    }

    /// <summary>The most recent toast (the status line).</summary>
    public Toast? LastToast
    {
        get => _lastToast;
        private set => Set(ref _lastToast, value);
    }

    /// <summary>Toasts still on screen, newest last.</summary>
    public ObservableCollection<Toast> Toasts { get; } = [];

    /// <summary>Raised on the UI thread after STATE was rebuilt.</summary>
    public event Action<StateSnapshot>? StateUpdated;

    /// <summary>Raised on the UI thread after the project mirror moved.</summary>
    public event Action<Project>? ProjectUpdated;

    public event Action<LinkStatus>? StatusUpdated;

    /// <summary>Binds the session to a link (any previous one is let go, not disposed: its owner disposes it).</summary>
    public void Attach(IEngineLink link, string label)
    {
        ArgumentNullException.ThrowIfNull(link);
        Detach();
        Previews.Clear();
        _link = link;
        Interlocked.Increment(ref _linkEpoch);
        LinkLabel = label;
        link.StatusChanged += OnStatus;
        link.StateChanged += OnState;
        link.ProjectChanged += OnProject;
        _status = link.Status;
        Raise(nameof(Link), nameof(Status), nameof(IsLive), nameof(CanSend), nameof(NodeId));
        // Read what the link already holds at once, so a window opened on a live link is never empty.
        ApplyProject();
        ApplyState();
        StatusUpdated?.Invoke(_status);
        _ = LoadVerbsAsync(link);
    }

    /// <summary>The node's verbs and the desk's role, asked at attach and again whenever the link comes back live.</summary>
    private void Reload()
    {
        if (_link is { } link)
            _ = LoadVerbsAsync(link);
    }

    /// <summary>Asks the node for its vocabulary (VERBS: a JSON list of rows, or names separated by spaces) and
    /// keeps the names; a node that does not answer leaves every control showing.</summary>
    private async Task LoadVerbsAsync(IEngineLink link)
    {
        var reply = await SendQuietAsync("VERBS");
        var who = await SendQuietAsync("WHOAMI");
        if (!ReferenceEquals(link, _link))
            return;
        var names = reply.Ok ? ParseVerbs(reply.Text) : FrozenSet<string>.Empty;
        var me = who.Ok ? WhoAmI.Read(who.Text) : null;
        _post(() =>
        {
            if (!ReferenceEquals(link, _link))
                return;
            if (reply.Ok)
                _verbs = names;
            if (me is not null)
            {
                _role = me.Role ?? _role;
                _sessionId = me.Session;
                _transport = me.Transport;
                _paired = me.Paired;
            }
            GateMoved();
            VerbsUpdated?.Invoke();
        });
    }

    /// <summary>A WHOAMI reply as the gate reads it: the role (null when it names none), the session, its transport, paired.</summary>
    private sealed record WhoAmI(string? Role, string Session, string Transport, bool Paired)
    {
        public static WhoAmI? Read(string text)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var r = doc.RootElement;
                if (r.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return null;
                string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                var paired = !r.TryGetProperty("paired", out var p) || p.ValueKind != System.Text.Json.JsonValueKind.False;
                return new WhoAmI(Str("role"), Str("session") ?? "", Str("transport") ?? "", paired);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The names in a VERBS reply that the node answers now (a row with "available": false is left out).</summary>
    public static FrozenSet<string> ParseVerbs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (t.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                var names = new List<string>();
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind == System.Text.Json.JsonValueKind.String)
                        names.Add(row.GetString() ?? "");
                    else if (row.ValueKind == System.Text.Json.JsonValueKind.Object && row.TryGetProperty("name", out var n)
                        && !(row.TryGetProperty("available", out var av) && av.ValueKind == System.Text.Json.JsonValueKind.False))
                        names.Add(n.GetString() ?? "");
                }
                return names.Where(n => n.Length > 0).ToFrozenSet(StringComparer.Ordinal);
            }
            catch (System.Text.Json.JsonException)
            {
                return FrozenSet<string>.Empty;
            }
        }
        // The names joined by spaces are ambiguous for two-word verbs: a name is any catalogue name whose words appear in order.
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var known = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < words.Length; i++)
        {
            foreach (var spec in VerbCatalog.All)
            {
                var parts = spec.Name.Split(' ');
                if (i + parts.Length <= words.Length && parts.AsSpan().SequenceEqual(words.AsSpan(i, parts.Length)))
                    known.Add(spec.Name);
            }
        }
        return known.ToFrozenSet(StringComparer.Ordinal);
    }

    public void Detach()
    {
        if (_link is null)
            return;
        _link.StatusChanged -= OnStatus;
        _link.StateChanged -= OnState;
        _link.ProjectChanged -= OnProject;
        _link = null;
        Interlocked.Increment(ref _linkEpoch);
        Previews.Clear();
        _status = LinkStatus.Down;
        _verbs = FrozenSet<string>.Empty;
        _role = "operator";
        _sessionId = "";
        _transport = "";
        _paired = true;
        GateMoved();
        Raise(nameof(Link), nameof(Status), nameof(IsLive), nameof(CanSend));
        StatusUpdated?.Invoke(_status);
    }

    /// <summary>Sends a verb. A refusal (or a dead link) becomes a toast with the reason; the reply comes back
    /// either way so a caller can read an id (ADD, PLAYLIST NEW) or a payload.</summary>
    public async Task<WireReply> SendAsync(string line, CancellationToken ct = default)
    {
        var reply = await SendCoreAsync(line, ct);
        if (!reply.Ok)
            Notify(ToastKind.Refused, Plain(reply.Text), line);
        return reply;
    }

    /// <summary>Sends and moves on: the reply is handled (a refusal becomes a toast) when it arrives. For
    /// an edit from a field, whose truth is the engine's next push.</summary>
    public void Fire(string line) => _ = SendAsync(line);

    /// <summary>Sends without a toast on refusal (previews, camera moves, drags' intermediate steps): the
    /// next line supersedes this one, so a refusal of an intermediate step is noise.</summary>
    public Task<WireReply> SendQuietAsync(string line, CancellationToken ct = default) => SendCoreAsync(line, ct);

    private async Task<WireReply> SendCoreAsync(string line, CancellationToken ct)
    {
        var link = _link;
        if (link is null || link.Status is not (LinkStatus.Live or LinkStatus.Stale))
            return new WireReply(false, "not connected to an engine — nothing was sent");
        WireReply reply;
        try
        {
            reply = await link.SendAsync(line, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WireReply(false, "the link failed: " + ex.Message);
        }
        Replied?.Invoke(line, reply);
        return reply;
    }

    /// <summary>Raised when the node answered a line this session sent (on the thread the reply came back on): what the node accepted
    /// or refused, for the parts of the desk that show the node's answer (the GO guard).</summary>
    public event Action<string, WireReply>? Replied;

    /// <summary>The GO guard every GO control draws (null until the desk's look module sets it).</summary>
    public ViewModels.Ux.GoGuard? GoGuard { get; set; }

    /// <summary>Runs an action on the UI thread later (for notes that arrive from a worker).</summary>
    public void OnUi(Action action) => _post(action);

    /// <summary>Puts a line on the status strip (and keeps the last few as toasts). The same words again
    /// within two seconds refresh the toast already up instead of stacking a copy. UI thread only.</summary>
    public void Notify(ToastKind kind, string text, string verb = "")
    {
        if (Toasts.Count > 0 && Toasts[^1] is { } last && last.Kind == kind && last.Text == text
            && System.Diagnostics.Stopwatch.GetElapsedTime(last.At) < TimeSpan.FromSeconds(2))
        {
            Toasts[^1] = last with { Verb = verb, At = System.Diagnostics.Stopwatch.GetTimestamp() };
            LastToast = Toasts[^1];
            return;
        }
        var toast = new Toast(kind, text, verb, Interlocked.Increment(ref _toastIds), System.Diagnostics.Stopwatch.GetTimestamp());
        LastToast = toast;
        Toasts.Add(toast);
        while (Toasts.Count > 4)
            Toasts.RemoveAt(0);
    }

    public void Dismiss(Toast toast) => Toasts.Remove(toast);

    /// <summary>Takes away toasts older than the given age (the window's tick); refusals stay twice as long.</summary>
    public void Expire(TimeSpan age)
    {
        for (var i = Toasts.Count - 1; i >= 0; i--)
        {
            var t = Toasts[i];
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t.At) > (t.IsRefusal ? age * 2 : age))
                Toasts.RemoveAt(i);
        }
    }

    /// <summary>The node the desk is on (its preview rings are named after it).</summary>
    public string NodeId => _link?.NodeId ?? "";

    /// <summary>An engine refusal in plain words: the reason without the grammar's prefix, capitalised.</summary>
    public static string Plain(string reason)
    {
        var t = reason.Trim();
        if (t.StartsWith("ERR", StringComparison.Ordinal))
            t = t[3..].TrimStart();
        if (t.Length == 0)
            return "The engine refused that.";
        return char.ToUpperInvariant(t[0]) + t[1..];
    }

    private void OnStatus(LinkStatus status)
    {
        lock (_gate)
        {
            if (_statusPending)
                return;
            _statusPending = true;
        }
        _post(() =>
        {
            lock (_gate)
                _statusPending = false;
            var s = _link?.Status ?? LinkStatus.Down;
            if (s == _status)
                return;
            _status = s;
            Raise(nameof(Status), nameof(IsLive), nameof(CanSend));
            StatusUpdated?.Invoke(s);
            if (s == LinkStatus.Live)
                Reload();
        });
    }

    private void OnState(long revision)
    {
        lock (_gate)
        {
            if (_statePending)
                return;
            _statePending = true;
        }
        _post(() =>
        {
            lock (_gate)
                _statePending = false;
            ApplyState();
        });
    }

    private void OnProject(long revision)
    {
        lock (_gate)
        {
            if (_projectPending)
                return;
            _projectPending = true;
        }
        _post(() =>
        {
            lock (_gate)
                _projectPending = false;
            ApplyProject();
        });
    }

    private void ApplyState()
    {
        if (_link is not { } link)
            return;
        var (json, _) = link.State;
        StateSnapshot next;
        try
        {
            next = StateSnapshot.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Notify(ToastKind.Fault, "The engine sent a STATE the desk could not read: " + ex.Message);
            return;
        }
        _state = next;
        Previews.Read(next);
        GateMoved();
        Raise(nameof(State));
        StateUpdated?.Invoke(next);
    }

    private void ApplyProject()
    {
        if (_link is not { } link)
            return;
        var (json, revision) = link.Project;
        if (string.IsNullOrEmpty(json) || revision == _projectRevision)
            return;
        Project next;
        try
        {
            next = ProjectJson.Deserialize(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Notify(ToastKind.Fault, "The engine sent a project the desk could not read: " + ex.Message);
            return;
        }
        _project = next;
        _projectRevision = revision;
        Raise(nameof(Project), nameof(ProjectRevision));
        ProjectUpdated?.Invoke(next);
    }

    public ValueTask DisposeAsync()
    {
        Detach();
        Throttle.Dispose();
        return ValueTask.CompletedTask;
    }
}
