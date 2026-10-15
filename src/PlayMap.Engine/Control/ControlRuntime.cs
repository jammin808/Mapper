using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Control;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Control;

/// <summary>One input as the monitor shows it: when, the input's words, the raw value, the normalised value, the binding it fired
/// (or null) and where it came from.</summary>
public sealed record MonitorRow(long AtUnixMs, string Input, string Raw, double Value, string? Binding, string Origin);

/// <summary>A line a binding fired and the node's answer to it.</summary>
public sealed record FiredLine(string Binding, string Line, bool Ok, string Reply);

/// <summary>What learn is doing, for STATE: the target, its words, until when and from where.</summary>
public sealed record LearnView(string Target, string Words, long UntilUnixMs, string From);

/// <summary>The last input heard, for STATE.control.last.</summary>
public sealed record LastInput(string Input, double Value, string? Binding, long AtUnixMs);

/// <summary>The control side's bindings at run time, inside the engine: the binding table (rebuilt when the show changes), every
/// binding's shape state, learn, the monitor and the journal of gestures. Inputs reach it from the node's sockets and ports
/// (<see cref="ControlService"/>) or from CONTROL INJECT; each event is processed in arrival order under one gate, and every line a
/// binding fires goes through the engine's router and gate like any other line. A gesture (an axis or an encoder) is journalled
/// once, closed after 400 ms without a move; a press is journalled as its line.</summary>
public sealed class ControlRuntime
{
    public const int MonitorCapacity = 64;

    public const long GestureIdleNs = 400_000_000;

    private const long LastStateEveryNs = 200_000_000;

    private readonly EngineCore _engine;
    private readonly Lock _serialGate = new();
    private Task _tail = Task.CompletedTask;
    private readonly Learner _learner = new();
    private readonly Dictionary<string, BindingRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Gesture> _gestures = new(StringComparer.Ordinal);
    private readonly Lock _monitorGate = new();
    private readonly Lock _tableGate = new();
    private readonly Queue<MonitorRow> _monitor = new();
    private volatile Table _table = Table.Empty;
    private long _lastStateNs = long.MinValue;
    private bool _lastDirty;
    private volatile ControlSettings _settings;
    private volatile LearnView? _learning;
    private volatile LastInput? _last;
    private volatile string? _learnt;

    internal ControlRuntime(EngineCore engine, ControlSettings settings)
    {
        _engine = engine;
        _settings = settings;
    }

    /// <summary>This node's control settings as they stand now.</summary>
    public ControlSettings Settings => _settings;

    /// <summary>Writes the node's control settings where the node keeps them; answers why it could not, or null. Unset on a bare
    /// engine (the change holds for this run).</summary>
    public Func<ControlSettings, string?>? Persist { get; set; }

    /// <summary>The settings changed (the ports and sockets follow).</summary>
    public event Action<ControlSettings>? SettingsChanged;

    public LearnView? Learning => _learning;

    public LastInput? Last => _last;

    /// <summary>What the last learn made ("CC 1 48 on APC40 → Lobby opacity (was Stage opacity)"), or null.</summary>
    public string? Learnt => _learnt;

    public long Heard { get; private set; }

    /// <summary>Changes the node's control settings: persisted first (a change that could not be kept is not taken).</summary>
    public string? ChangeSettings(Func<ControlSettings, ControlSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(_settings);
        if (next == _settings)
            return null;
        if (Persist?.Invoke(next) is { } why)
            return why;
        _settings = next;
        SettingsChanged?.Invoke(next);
        _engine.FactsChanged();
        return null;
    }

    public IReadOnlyList<MonitorRow> Monitor(int count)
    {
        lock (_monitorGate)
            return _monitor.Reverse().Take(Math.Clamp(count, 1, MonitorCapacity)).ToList();
    }

    /// <summary>The binding table of a show (built once per revision).</summary>
    internal Table TableOf(Project project)
    {
        var t = _table;
        if (ReferenceEquals(t.Project, project))
            return t;
        lock (_tableGate)
        {
            t = _table;
            if (ReferenceEquals(t.Project, project))
                return t;
            t = Table.Build(project, _runtimes);
            _table = t;
            return t;
        }
    }

    /// <summary>The runtime of a binding of the current show (null when it is off, unplugged or does not parse).</summary>
    public BindingRuntime? RuntimeOf(string bindingId) =>
        TableOf(_engine.Store.Project).All.FirstOrDefault(r => string.Equals(r.Binding.Id, bindingId, StringComparison.Ordinal));

    /// <summary>Why a binding does not fire now (its input or target does not read, its surface is off), or null.</summary>
    public string? ProblemOf(string bindingId) => TableOf(_engine.Store.Project).Problems.GetValueOrDefault(bindingId);

    // ---- learn ----

    public void Arm(LearnSession session)
    {
        _learner.Arm(session);
        _learnt = null;
        _learning = new LearnView(session.Target.Text, session.Words, session.UntilUnixMs, session.From);
        _engine.FactsChanged();
    }

    public bool CancelLearn()
    {
        var was = _learner.Cancel();
        _learning = null;
        if (was)
            _engine.FactsChanged();
        return was;
    }

    // ---- events ----

    /// <summary>One input event: monitored, offered to learn, then to every binding of its input. Answers the lines it fired.</summary>
    public Task<IReadOnlyList<FiredLine>> ProcessAsync(InputEvent e, CancellationToken ct = default) => SerialAsync(() => ProcessLockedAsync(e, ct));

    /// <summary>Runs work after every earlier piece of work has finished (events and polls in arrival order, one at a time).</summary>
    private async Task<T> SerialAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_serialGate)
        {
            previous = _tail;
            _tail = done.Task;
        }
        try
        {
            await previous.ConfigureAwait(false);
            return await work().ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
        }
    }

    /// <summary>The timed work: hold-to-fire presses that are due, learn running out, gestures closed after 400 ms idle.</summary>
    public Task PollAsync(long nowNs, CancellationToken ct = default) => SerialAsync(async () =>
    {
        {
            if (_learner.Expire(nowNs))
            {
                _learning = null;
                _engine.Journal.Add("control", "learn stopped: no control moved in 30 s — nothing was bound", "control");
                _engine.FactsChanged();
            }
            CloseGestures(nowNs, force: false);
            PublishLast(nowNs);
            var table = TableOf(_engine.Store.Project);
            foreach (var r in table.All)
            {
                if (!_settings.InputEnabled)
                    break;
                if (r.Poll(nowNs, () => FactOf(r.Target)) is { } fire)
                    await FireAsync(r, fire, ct).ConfigureAwait(false);
            }
        }
        return 0;
    });

    private async Task<IReadOnlyList<FiredLine>> ProcessLockedAsync(InputEvent e, CancellationToken ct)
    {
        Heard++;
        var project = _engine.Store.Project;
        var table = TableOf(project);
        var matched = table.Match(e.Address);
        var fired = new List<FiredLine>();
        string? firstBinding = null;
        if (_learner.Session is not null)
        {
            var target = _learner.Session.Target.Text;
            var outcome = _learner.Offer(e, a => table.Match(a).Any(r => string.Equals(r.Target.Text, target, StringComparison.Ordinal)));
            if (outcome is not null)
            {
                _learning = null;
                await LearntAsync(outcome, ct).ConfigureAwait(false);
            }
            else if (_learner.Session is null)
            {
                _learning = null;
            }
        }
        else if (_settings.InputEnabled)
        {
            foreach (var r in matched)
            {
                if (r.Offer(e, () => ReadOf(r.Target), () => FactOf(r.Target)) is not { } fire)
                    continue;
                firstBinding ??= r.Binding.Id;
                fired.Add(await FireAsync(r, fire, ct).ConfigureAwait(false));
            }
        }
        var input = e.Address.Format();
        Record(new MonitorRow(_engine.Services.Wall.UnixMsAt(e.MonoNs), input, e.Raw.ToString(CultureInfo.InvariantCulture), e.Value, firstBinding, e.Origin));
        _last = new LastInput(input, e.Value, firstBinding, _engine.Services.Wall.UnixMsAt(e.MonoNs));
        _lastDirty = true;
        PublishLast(e.MonoNs);
        return fired;
    }

    private async Task<FiredLine> FireAsync(BindingRuntime r, BindingFire fire, CancellationToken ct)
    {
        var words = r.Input.Format();
        var session = new SessionInfo("bind:" + r.Binding.Id, "binding", "", false, $"binding:{r.Binding.Id} ({words})", true);
        var reply = await _engine.RunBindingLineAsync(fire.Line, session, journalLine: !fire.Gesture, ct).ConfigureAwait(false);
        if (fire.Gesture)
            Gestured(r, fire, _engine.Services.Mono.NowNs, reply.Ok ? null : reply.Payload ?? "refused");
        return new FiredLine(r.Binding.Id, fire.Line, reply.Ok, reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : reply.Payload ?? "") : reply.Payload ?? "refused");
    }

    /// <summary>Fires the bindings of an input as if it moved to a value (CONTROL INJECT): a continuous input moves there, a button
    /// presses at a value above zero and releases at zero.</summary>
    public Task<IReadOnlyList<FiredLine>> InjectAsync(InputAddress input, double value, CancellationToken ct)
    {
        var now = _engine.Services.Mono.NowNs;
        var kind = input.IsContinuous ? InputKind.Axis
            : input.Source == InputSource.Midi && input.Family is MidiFamily.Msc or MidiFamily.Mmc ? InputKind.Trigger
            : input.Source == InputSource.Midi && input.Family == MidiFamily.Program ? InputKind.Program
            : value > 0 ? InputKind.Press : InputKind.Release;
        var bits = (byte)(input.Source switch { InputSource.Midi => input.Family is MidiFamily.Cc14 or MidiFamily.Nrpn or MidiFamily.Rpn or MidiFamily.Bend ? 14 : 7, InputSource.Osc => 0, _ => 8 });
        var raw = bits == 0 ? 0 : (int)Math.Round(Math.Clamp(value, 0, 1) * ((1 << bits) - 1));
        return ProcessAsync(new InputEvent(input, kind, value, raw, bits, now) { Origin = "inject" }, ct);
    }

    /// <summary>Learn heard a control: the binding it makes replaces any other on the same input (one undo step), and says what it
    /// replaced.</summary>
    private async Task LearntAsync(LearnOutcome outcome, CancellationToken ct)
    {
        var input = outcome.Input.Format();
        var replaced = new List<string>();
        var id = "";
        var r = await _engine.EditAsync(new SessionInfo("control", "binding", "", false, "learn", true), $"learn {input}", p =>
        {
            var same = p.Control.Bindings.Where(b => InputAddress.Parse(b.Input, out _) is { } a && a == outcome.Input).ToList();
            replaced.AddRange(same.Select(b => _engine.BindingWords(p, b)));
            id = Ids.NewUnique(Ids.ControlBinding, p.Control.Bindings);
            var binding = new ControlBinding(id, "", input, outcome.Target, outcome.Shape, ControlFeedback.Default, true);
            var bindings = p.Control.Bindings.RemoveAll(b => same.Contains(b)).Add(binding);
            return p with { Control = p.Control with { Bindings = bindings } };
        }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var made = _engine.Store.Project.Control.Bindings.Find(id);
        var words = made is null ? input : _engine.BindingWords(_engine.Store.Project, made);
        _learnt = r.Changed ? words + (replaced.Count > 0 ? $" (was {string.Join(", ", replaced.Select(w => w[(w.IndexOf('→', StringComparison.Ordinal) + 1)..].Trim()))})" : "") : r.Effect;
        _engine.Journal.Add(r.Changed ? "control" : "refused", r.Changed ? "learnt: " + _learnt : "learn refused: " + r.Effect, "control");
        _engine.FactsChanged();
    }

    /// <summary>The last input moves STATE at most five times a second; the latest is published once the interval has passed.</summary>
    private void PublishLast(long nowNs)
    {
        if (!_lastDirty || (_lastStateNs != long.MinValue && nowNs - _lastStateNs < LastStateEveryNs))
            return;
        _lastDirty = false;
        _lastStateNs = nowNs;
        _engine.FactsChanged();
    }

    // ---- reads for takeover, encoders and toggles ----

    private double? ReadOf(TargetLine target)
    {
        if (target.Field is not { } field || target.Spec.Name == "GENERATOR SET")
            return null;
        return _engine.ReadNumber(field.Id, field.Path);
    }

    private bool? FactOf(TargetLine target) => _engine.FactOf(target);

    // ---- gestures ----

    /// <summary>One line of a gesture, taken or refused: the gesture is journalled once when it closes, with its refusals counted
    /// (a fader on a refused target is one row, not one per move).</summary>
    private void Gestured(BindingRuntime r, BindingFire fire, long nowNs, string? refused)
    {
        if (!_gestures.TryGetValue(r.Binding.Id, out var g) || nowNs - g.LastNs > GestureIdleNs)
        {
            if (g is not null)
                Close(r.Binding.Id, g);
            g = new Gesture(r.Input.Format(), _engine.BindingWords(_engine.Store.Project, r.Binding), fire.Value, nowNs, fire.Line);
            _gestures[r.Binding.Id] = g;
        }
        g.LastNs = nowNs;
        if (refused is not null)
        {
            g.Refused++;
            g.Why = refused;
            return;
        }
        if (g.Edits == 0)
            g.From = fire.Value;
        g.To = fire.Value;
        g.Edits++;
    }

    private void CloseGestures(long nowNs, bool force)
    {
        foreach (var (id, g) in _gestures.ToList())
        {
            if (!force && nowNs - g.LastNs < GestureIdleNs)
                continue;
            Close(id, g);
            _gestures.Remove(id);
        }
    }

    private void Close(string bindingId, Gesture g)
    {
        var target = g.Words[(g.Words.IndexOf('→', StringComparison.Ordinal) + 1)..].Trim();
        var seconds = (g.LastNs - g.FirstNs) / 1e9;
        var who = $"binding:{bindingId} ({g.Input})";
        if (g.Edits > 0)
        {
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{g.Input} → {target} {Num(g.From)} → {Num(g.To)} over {seconds:0.0} s ({g.Edits} edit{(g.Edits == 1 ? "" : "s")})");
            _engine.Journal.Add("control", text, who);
        }
        if (g.Refused > 0)
            _engine.Journal.Add("refused", string.Create(CultureInfo.InvariantCulture, $"{g.Input} → {target} refused {g.Refused} time{(g.Refused == 1 ? "" : "s")}: {g.Why}"),
                who, g.Line, false);
    }

    private static string Num(double v) => double.IsNaN(v) ? "?" : v.ToString("0.###", CultureInfo.InvariantCulture);

    private void Record(MonitorRow row)
    {
        lock (_monitorGate)
        {
            _monitor.Enqueue(row);
            while (_monitor.Count > MonitorCapacity)
                _monitor.Dequeue();
        }
    }

    private sealed class Gesture(string input, string words, double from, long firstNs, string line)
    {
        public string Input { get; } = input;
        public string Words { get; } = words;
        public string Line { get; } = line;
        public double From { get; set; } = from;
        public long FirstNs { get; } = firstNs;
        public double To { get; set; } = from;
        public long LastNs { get; set; } = firstNs;
        public int Edits { get; set; }
        public int Refused { get; set; }
        public string Why { get; set; } = "";
    }

    /// <summary>The bindings of one show revision by input: exact addresses, any-surface addresses, OSC patterns; and why each
    /// binding that does not fire does not.</summary>
    internal sealed class Table
    {
        public static readonly Table Empty = new(null, [], new Dictionary<InputAddress, List<BindingRuntime>>(), [], ImmutableDictionary<string, string>.Empty);

        private readonly Dictionary<InputAddress, List<BindingRuntime>> _byInput;
        private readonly List<BindingRuntime> _patterns;

        private Table(Project? project, List<BindingRuntime> all, Dictionary<InputAddress, List<BindingRuntime>> byInput, List<BindingRuntime> patterns,
            ImmutableDictionary<string, string> problems)
        {
            Project = project;
            All = all;
            _byInput = byInput;
            _patterns = patterns;
            Problems = problems;
        }

        public Project? Project { get; }

        public IReadOnlyList<BindingRuntime> All { get; }

        public ImmutableDictionary<string, string> Problems { get; }

        public static Table Build(Project project, Dictionary<string, BindingRuntime> keep)
        {
            var all = new List<BindingRuntime>();
            var byInput = new Dictionary<InputAddress, List<BindingRuntime>>();
            var patterns = new List<BindingRuntime>();
            var problems = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in project.Control.Bindings)
            {
                if (InputAddress.Parse(b.Input, out var why) is not { } input)
                {
                    problems[b.Id] = why;
                    continue;
                }
                if (TargetLine.Parse(b.Target, out why) is not { } target)
                {
                    problems[b.Id] = why;
                    continue;
                }
                var surface = input.Surface.Length > 0
                    ? project.Control.Surfaces.FirstOrDefault(s => s.Name.Equals(input.Surface, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (input.Surface.Length > 0 && surface is null)
                {
                    problems[b.Id] = $"no surface {input.Surface}";
                    continue;
                }
                if (!b.Enabled || surface is { Enabled: false })
                {
                    problems[b.Id] = b.Enabled ? $"{surface!.Name} is off" : "off";
                    continue;
                }
                live.Add(b.Id);
                if (!keep.TryGetValue(b.Id, out var r) || r.Binding != b)
                {
                    r = new BindingRuntime(b, input with { Surface = surface?.Name ?? "" }, target, TargetRanges.Of(target, project));
                    keep[b.Id] = r;
                }
                all.Add(r);
                if (input.Source == InputSource.Osc && OscPattern.IsPattern(input.Path))
                    patterns.Add(r);
                else
                {
                    if (!byInput.TryGetValue(r.Input, out var list))
                    {
                        list = [];
                        byInput[r.Input] = list;
                    }
                    list.Add(r);
                }
            }
            foreach (var gone in keep.Keys.Where(k => !live.Contains(k)).ToList())
                keep.Remove(gone);
            return new Table(project, all, byInput, patterns, problems.ToImmutable());
        }

        /// <summary>The bindings an event fires: its exact input on its surface, the same input on any surface, an OSC pattern that
        /// matches, and an MSC/MMC all-call (device 127) for every device.</summary>
        public IEnumerable<BindingRuntime> Match(InputAddress a)
        {
            var seen = new HashSet<BindingRuntime>();
            var keys = new List<InputAddress> { a };
            if (a.Surface.Length > 0)
                keys.Add(a.AnySurface);
            foreach (var k in keys)
                if (_byInput.TryGetValue(k, out var list))
                    foreach (var r in list)
                        if (seen.Add(r))
                            yield return r;
            if (a.Source == InputSource.Midi && a.Family is MidiFamily.Msc or MidiFamily.Mmc && a.Number == 127)
            {
                foreach (var r in All.Where(r => r.Input.Family == a.Family && string.Equals(r.Input.Path, a.Path, StringComparison.Ordinal)
                    && (r.Input.Surface.Length == 0 || string.Equals(r.Input.Surface, a.Surface, StringComparison.OrdinalIgnoreCase))))
                    if (seen.Add(r))
                        yield return r;
            }
            if (a.Source != InputSource.Osc)
                yield break;
            foreach (var r in _patterns)
                if (r.Input.Number == a.Number && (r.Input.Surface.Length == 0 || string.Equals(r.Input.Surface, a.Surface, StringComparison.OrdinalIgnoreCase))
                    && OscPattern.Matches(r.Input.Path, a.Path) && seen.Add(r))
                    yield return r;
        }
    }
}
