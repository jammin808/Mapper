namespace PlayMap.Core.Model;

/// <summary>What an edit did, in words a journal row, a wire reply and an assistant can all read.
/// <see cref="Changed"/> false means the edit was refused or would have changed nothing — and says why.</summary>
public sealed record EditResult(bool Changed, string Effect, long Revision)
{
    public static EditResult Refused(string why, long revision) => new(false, why, revision);
}

/// <summary>A published, immutable state of the project. <see cref="Revision"/> rises with every change;
/// readers compare revisions, never contents.</summary>
public sealed record ProjectSnapshot(Project Project, long Revision, string LastEdit);

/// <summary>The single writer of the project. Every edit — from the desk, the wire, Patterns, an approved
/// AI proposal — goes through <see cref="Apply"/>, one at a time, and lands as a new immutable root with an
/// undo entry. Readers take <see cref="Current"/> (a volatile reference) and never lock. Drags coalesce: a
/// run of edits with the same coalesce key within the window is one undo step.</summary>
public sealed class ProjectStore
{
    private readonly Lock _gate = new();
    private readonly List<(Project Root, string Label)> _undo = [];
    private readonly List<(Project Root, string Label)> _redo = [];
    private readonly Func<long> _nowMs;
    private volatile ProjectSnapshot _current;
    private string? _lastCoalesceKey;
    private long _lastCoalesceMs;

    public ProjectStore(Project initial, Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = new ProjectSnapshot(initial, 1, "opened");
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>How many undo steps are kept (the rest are dropped oldest first).</summary>
    public int UndoLimit { get; init; } = 256;

    /// <summary>Edits with the same key closer than this are one undo step.</summary>
    public long CoalesceWindowMs { get; init; } = 750;

    public ProjectSnapshot Current => _current;

    public Project Project => _current.Project;

    /// <summary>Raised after every published change, on the thread that made it.</summary>
    public event Action<ProjectSnapshot>? Changed;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
                return _undo.Count > 0;
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
                return _redo.Count > 0;
        }
    }

    public string? UndoLabel
    {
        get
        {
            lock (_gate)
                return _undo.Count > 0 ? _undo[^1].Label : null;
        }
    }

    public string? RedoLabel
    {
        get
        {
            lock (_gate)
                return _redo.Count > 0 ? _redo[^1].Label : null;
        }
    }

    /// <summary>Applies an edit. The edit returns the new root (or the same instance to change nothing)
    /// and may throw <see cref="EditRefusedException"/> with the reason. Returns what happened.</summary>
    public EditResult Apply(string label, Func<Project, Project> edit, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ProjectSnapshot published;
        lock (_gate)
        {
            var before = _current;
            Project after;
            try
            {
                after = edit(before.Project);
            }
            catch (EditRefusedException refused)
            {
                return EditResult.Refused(refused.Message, before.Revision);
            }
            if (ReferenceEquals(after, before.Project) || after == before.Project)
                return EditResult.Refused($"{label}: nothing to change", before.Revision);

            var now = _nowMs();
            var coalesce = coalesceKey is not null
                && string.Equals(coalesceKey, _lastCoalesceKey, StringComparison.Ordinal)
                && now - _lastCoalesceMs <= CoalesceWindowMs
                && _undo.Count > 0;
            if (!coalesce)
            {
                _undo.Add((before.Project, label));
                if (_undo.Count > UndoLimit)
                    _undo.RemoveAt(0);
            }
            _redo.Clear();
            _lastCoalesceKey = coalesceKey;
            _lastCoalesceMs = now;
            published = new ProjectSnapshot(after, before.Revision + 1, label);
            _current = published;
        }
        Changed?.Invoke(published);
        return new EditResult(true, label, published.Revision);
    }

    public EditResult Undo()
    {
        ProjectSnapshot published;
        lock (_gate)
        {
            if (_undo.Count == 0)
                return EditResult.Refused("nothing to undo", _current.Revision);
            var (root, label) = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add((_current.Project, label));
            _lastCoalesceKey = null;
            published = new ProjectSnapshot(root, _current.Revision + 1, "undo " + label);
            _current = published;
        }
        Changed?.Invoke(published);
        return new EditResult(true, published.LastEdit, published.Revision);
    }

    public EditResult Redo()
    {
        ProjectSnapshot published;
        lock (_gate)
        {
            if (_redo.Count == 0)
                return EditResult.Refused("nothing to redo", _current.Revision);
            var (root, label) = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add((_current.Project, label));
            _lastCoalesceKey = null;
            published = new ProjectSnapshot(root, _current.Revision + 1, "redo " + label);
            _current = published;
        }
        Changed?.Invoke(published);
        return new EditResult(true, published.LastEdit, published.Revision);
    }

    /// <summary>Replaces the whole project (open, new) and clears the history.</summary>
    public void Replace(Project project, string label)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectSnapshot published;
        lock (_gate)
        {
            _undo.Clear();
            _redo.Clear();
            _lastCoalesceKey = null;
            published = new ProjectSnapshot(project, _current.Revision + 1, label);
            _current = published;
        }
        Changed?.Invoke(published);
    }
}

/// <summary>Thrown inside an edit to refuse it with a reason the operator reads ("no surface sf12 — …").</summary>
public sealed class EditRefusedException(string message) : Exception(message);
