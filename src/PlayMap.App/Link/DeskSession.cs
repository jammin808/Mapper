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
public sealed record Toast(ToastKind Kind, string Text, string Verb, long Id)
{
    public bool IsRefusal => Kind is ToastKind.Refused or ToastKind.Fault;
}

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

    /// <param name="post">Runs an action on the UI thread later (the dispatcher's Post); tests pass the
    /// headless dispatcher's.</param>
    public DeskSession(Action<Action> post)
    {
        _post = post;
        Throttle = new ThrottledSender((line, ct) => SendQuietAsync(line, ct));
    }

    /// <summary>The coalescing sender drags go through (≈ 60 Hz, latest wins per key).</summary>
    public ThrottledSender Throttle { get; }

    public IEngineLink? Link => _link;

    public StateSnapshot State => _state;

    /// <summary>The mirrored project (null until the engine's first push).</summary>
    public Project? Project => _project;

    public long ProjectRevision => _projectRevision;

    public LinkStatus Status => _status;

    public bool IsLive => _status == LinkStatus.Live;

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
        _link = link;
        LinkLabel = label;
        link.StatusChanged += OnStatus;
        link.StateChanged += OnState;
        link.ProjectChanged += OnProject;
        _status = link.Status;
        Raise(nameof(Link), nameof(Status), nameof(IsLive));
        // Read what the link already holds at once, so a window opened on a live link is never empty.
        ApplyProject();
        ApplyState();
        StatusUpdated?.Invoke(_status);
    }

    public void Detach()
    {
        if (_link is null)
            return;
        _link.StatusChanged -= OnStatus;
        _link.StateChanged -= OnState;
        _link.ProjectChanged -= OnProject;
        _link = null;
        _status = LinkStatus.Down;
        Raise(nameof(Link), nameof(Status), nameof(IsLive));
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
        if (link is null || link.Status != LinkStatus.Live)
            return new WireReply(false, "not connected to an engine — nothing was sent");
        try
        {
            return await link.SendAsync(line, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WireReply(false, "the link failed: " + ex.Message);
        }
    }

    /// <summary>Puts a line on the status strip (and keeps the last few as toasts).</summary>
    public void Notify(ToastKind kind, string text, string verb = "")
    {
        var toast = new Toast(kind, text, verb, Interlocked.Increment(ref _toastIds));
        LastToast = toast;
        Toasts.Add(toast);
        while (Toasts.Count > 4)
            Toasts.RemoveAt(0);
    }

    public void Dismiss(Toast toast) => Toasts.Remove(toast);

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
            Raise(nameof(Status), nameof(IsLive));
            StatusUpdated?.Invoke(s);
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
