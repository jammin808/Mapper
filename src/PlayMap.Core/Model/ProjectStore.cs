using System.Collections.Immutable;
using System.Globalization;

namespace PlayMap.Core.Model;

/// <summary>What an edit did, in words a journal row, a wire reply and an assistant can all read.
/// <see cref="Changed"/> false means the edit was refused or would have changed nothing — and says why.</summary>
public sealed record EditResult(bool Changed, string Effect, long Revision)
{
    public static EditResult Refused(string why, long revision) => new(false, why, revision);
}

/// <summary>One undo step: its number (the step's own while it is kept, never another's: each new step takes the next), the edit's
/// label, who made it and when (Unix ms; 0 when the store has no wall clock).</summary>
public sealed record UndoStep(long Number, string Label, string Who, long AtUnixMs);

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
    private readonly List<(Project Root, UndoStep Step)> _undo = [];
    private readonly List<(Project Root, UndoStep Step)> _redo = [];
    private readonly Func<long> _nowMs;
    private volatile ProjectSnapshot _current;
    private string? _lastCoalesceKey;
    private long _lastCoalesceMs;
    private long _lastStep;

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

    /// <summary>Who makes the edit being applied, as its undo step records it ("" when nobody says).</summary>
    public Func<string>? Who { get; init; }

    /// <summary>The wall clock, Unix ms, an undo step records when it was made by (0 when none is given).</summary>
    public Func<long>? WallMs { get; init; }

    public ProjectSnapshot Current => _current;

    public Project Project => _current.Project;

    /// <summary>Raised after every published change, on the thread that made it.</summary>
    public event Action<ProjectSnapshot>? Changed;

    /// <summary>Checked before Apply, ApplyGroup, Undo and Redo publish: (before, after) → refusal words, or null to let it
    /// through. Runs under the store's lock, so it must be quick and pure.</summary>
    public Func<Project, Project, string?>? Guard { get; set; }

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
                return _undo.Count > 0 ? _undo[^1].Step.Label : null;
        }
    }

    public string? RedoLabel
    {
        get
        {
            lock (_gate)
                return _redo.Count > 0 ? _redo[^1].Step.Label : null;
        }
    }

    /// <summary>The undo steps, newest first: what UNDO takes back next is the first.</summary>
    public ImmutableArray<UndoStep> Steps
    {
        get
        {
            lock (_gate)
            {
                var steps = ImmutableArray.CreateBuilder<UndoStep>(_undo.Count);
                for (var i = _undo.Count - 1; i >= 0; i--)
                    steps.Add(_undo[i].Step);
                return steps.MoveToImmutable();
            }
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
            if (Guard?.Invoke(before.Project, after) is { } guarded)
                return EditResult.Refused(guarded, before.Revision);

            var now = _nowMs();
            var coalesce = coalesceKey is not null
                && string.Equals(coalesceKey, _lastCoalesceKey, StringComparison.Ordinal)
                && now - _lastCoalesceMs <= CoalesceWindowMs
                && _undo.Count > 0;
            if (!coalesce)
            {
                _undo.Add((before.Project, new UndoStep(++_lastStep, label, Who?.Invoke() ?? "", WallMs?.Invoke() ?? 0)));
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

    /// <summary>Several edits as one undo step and one <see cref="Changed"/>: each edit reads the root the previous one made,
    /// and a refusal of any refuses them all (wildcard OSC, MOD AUTO, LED IMPORT, BLIND COMMIT).</summary>
    public EditResult ApplyGroup(string label, IReadOnlyList<Func<Project, Project>> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return Apply(label, p =>
        {
            foreach (var edit in edits)
                p = edit(p);
            return p;
        });
    }

    /// <summary>Publishes a new root with no undo entry (locks). Every root kept for undo and redo has these fields re-applied
    /// by <paramref name="keep"/> (older root, the new root) → older root carrying them, so an undo never takes them back. The
    /// guard is not asked: what this writes is what the guard reads.</summary>
    public EditResult ApplyUnrecorded(string label, Func<Project, Project> edit, Func<Project, Project, Project>? keep = null)
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
            if (keep is not null)
            {
                for (var i = 0; i < _undo.Count; i++)
                    _undo[i] = (keep(_undo[i].Root, after), _undo[i].Step);
                for (var i = 0; i < _redo.Count; i++)
                    _redo[i] = (keep(_redo[i].Root, after), _redo[i].Step);
            }
            _lastCoalesceKey = null;
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
            var (root, step) = _undo[^1];
            if (Guard?.Invoke(_current.Project, root) is { } guarded)
                return EditResult.Refused(guarded, _current.Revision);
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add((_current.Project, step));
            _lastCoalesceKey = null;
            published = new ProjectSnapshot(root, _current.Revision + 1, "undo " + step.Label);
            _current = published;
        }
        Changed?.Invoke(published);
        return new EditResult(true, published.LastEdit, published.Revision);
    }

    /// <summary>Takes back the undo step numbered <paramref name="step"/> and every newer one as one action: one published root (the one
    /// before that step), one revision, one <see cref="Changed"/>, the guard asked once for the whole run. Each step taken back goes
    /// to the redo list as UNDO would put it there, so REDO brings them back one at a time, oldest first, and UndoTo of the newest step
    /// is one <see cref="Undo"/>. Refused, with nothing changed, on an empty list or a step not in it (naming the steps there are).</summary>
    public EditResult UndoTo(long step)
    {
        ProjectSnapshot published;
        lock (_gate)
        {
            if (_undo.Count == 0)
                return EditResult.Refused("nothing to undo", _current.Revision);
            var at = _undo.FindIndex(e => e.Step.Number == step);
            if (at < 0)
            {
                var range = _undo.Count == 1
                    ? string.Create(CultureInfo.InvariantCulture, $"the only one is {_undo[0].Step.Number}")
                    : string.Create(CultureInfo.InvariantCulture, $"they are {_undo[0].Step.Number} to {_undo[^1].Step.Number}");
                return EditResult.Refused(string.Create(CultureInfo.InvariantCulture, $"no undo step {step} — {range} (UNDO LIST lists them)"),
                    _current.Revision);
            }
            var root = _undo[at].Root;
            if (Guard?.Invoke(_current.Project, root) is { } guarded)
                return EditResult.Refused(guarded, _current.Revision);
            var labels = new List<string>(_undo.Count - at);
            var after = _current.Project;
            for (var i = _undo.Count - 1; i >= at; i--)
            {
                var (before, taken) = _undo[i];
                _redo.Add((after, taken));
                labels.Add(taken.Label);
                after = before;
            }
            _undo.RemoveRange(at, _undo.Count - at);
            _lastCoalesceKey = null;
            published = new ProjectSnapshot(root, _current.Revision + 1, "undo " + string.Join("; ", labels));
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
            var (root, step) = _redo[^1];
            if (Guard?.Invoke(_current.Project, root) is { } guarded)
                return EditResult.Refused(guarded, _current.Revision);
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add((_current.Project, step));
            _lastCoalesceKey = null;
            published = new ProjectSnapshot(root, _current.Revision + 1, "redo " + step.Label);
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
