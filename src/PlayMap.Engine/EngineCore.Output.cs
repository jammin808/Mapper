using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Sync;
using PlayMap.Core.Wire;
using PlayMap.Engine.Presentation;
using PlayMap.Render.Timing;

namespace PlayMap.Engine;

/// <summary>wp2-output's hooks into the engine (EngineCore.Modules.cs): presentation from the display's side (OUTPUT STATS, PRESENT
/// RESET, <c>outputs[].present</c>, <c>machine.present</c>) and the frame lock (SYNC APPLY, LEAVE, REJOIN, <c>outputs[].lock</c>,
/// <c>sync.lock</c>). Every refusal leads with its reason word ("armed: …", "no-board: …") and is kept in <c>sync.lock.refused</c>.</summary>
public sealed partial class EngineCore
{
    /// <summary>How long a lock verb waits to observe what it asked (a join confirms in 120 refreshes, 2 s at 60 Hz; 5 s at most).</summary>
    private static readonly TimeSpan LockObserveWithin = TimeSpan.FromSeconds(10);

    private volatile LockRefusal? _lockRefusal;

    /// <summary>The last lock verb refused: which, its reason word, its words and when (unix ms).</summary>
    private sealed record LockRefusal(string Verb, string Reason, string Words, long AtUnixMs);

    private IPresentBackend? PresentBackend => Services.Outputs as IPresentBackend;

    private IFrameLockBackend? LockBackend => Services.Outputs as IFrameLockBackend;

    partial void RouteOutput(VerbRoutes routes)
    {
        routes.Lane("OUTPUT STATS", c => OutputStats(c.Args));
        routes.OffLane("PRESENT RESET", c => PresentResetAsync(c.Args, c.Session, c.Ct));
        routes.Lane("PRESENT PATH", c => PresentPathVerb(c.Args, c.Session));
        routes.OffLane("SYNC APPLY", c => SyncApplyAsync(c.Args, c.Ct));
        routes.OffLane("SYNC LEAVE", c => SyncLeaveAsync(c.Args, c.Ct));
        routes.OffLane("SYNC REJOIN", c => SyncRejoinAsync(c.Args, c.Ct));
        routes.Index(new PresentIndex(this));
    }

    partial void TickOutput(long leaderNs)
    {
        // Presentation and the lock run on the render thread; the lane only lets a DXGI start's fuse go once its outputs are up.
        if (PresentBackend is { } backend && backend.ConfirmFirstFrames())
        {
            Services.PresentNode?.RefreshFuse();
            Journal.Add("present", "the DXGI path reached its first settled frames on every display: its fuse is cleared", ok: true);
            FactsChanged();
        }
    }

    /// <summary>A fuse left by a DXGI start that never got its outputs up (not the one this start armed and is still waiting on).</summary>
    private FuseState? BlownFuse =>
        Services.PresentNode?.FuseNow is { } f && !(PresentBackend?.Machine().FuseArmed ?? false) ? f : null;

    /// <summary>PRESENT PATH AUTO|DXGI|GL: the path the render host starts its outputs on from its next start, kept on the node. A no-op
    /// is refused; DXGI is refused where it cannot run (not Windows), and asking it again with a blown fuse clears the fuse.</summary>
    private CommandReply PresentPathVerb(List<string> args, SessionInfo session)
    {
        if (args.Count != 1 || PresentChoice.Normalise(args[0]) is var word && !args[0].Trim().Equals(word, StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail("PRESENT PATH AUTO|DXGI|GL");
        if (Services.PresentNode is not { } node)
            return CommandReply.Fail("this node keeps no settings (it has no node home): PRESENT PATH has nowhere to keep the path");
        if (word == "dxgi" && !OperatingSystem.IsWindows())
            return CommandReply.Fail($"the DXGI path is Windows only (this node runs {RuntimeName()}): outputs present through OpenGL");
        var saved = node.Saved;
        var current = PresentChoice.Normalise(saved.Path);
        var blown = BlownFuse;
        if (current == word && !(word == "dxgi" && blown is not null))
            return CommandReply.Fail($"PRESENT PATH is already {word.ToUpperInvariant()} (from the render host's next start)");
        var cleared = false;
        if (current != word)
            node.Save(saved with { Path = word });
        if (word == "dxgi" && blown is not null)
        {
            cleared = node.Fuse.Clear();
            node.RefreshFuse();
        }
        var words = word switch
        {
            "dxgi" => "outputs present on the DXGI flip model from the render host's next start (experimental: a start that does not reach its first frames falls back to OpenGL)",
            "gl" => "outputs present through OpenGL from the render host's next start",
            _ => "outputs present through OpenGL from the render host's next start (AUTO is OpenGL on every platform this round)",
        };
        Journal.Add("present", $"present path {current} → {word}{(cleared ? " (fuse cleared)" : "")}", session.ClientName);
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("asked", word);
            w.WriteString("was", current);
            w.WriteString("applies", "next start");
            w.WriteBoolean("experimental", word == "dxgi");
            w.WriteBoolean("fuseCleared", cleared);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private static string RuntimeName() => OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "this platform";

    // ---- verbs ------------------------------------------------------------------------------------------

    /// <summary>The outputs a verb names: one by id, number or name, or every enabled output (none named, or ALL); null with the
    /// refusal when the name is no output.</summary>
    private List<Output>? OutputTargets(List<string> args, out CommandReply? refused)
    {
        refused = null;
        var p = Store.Project;
        if (args.Count == 0 || args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return [.. p.Outputs.Where(o => o.Enabled)];
        if (ResolveOutput(args[0]) is { } one)
            return [one];
        refused = CommandReply.Fail(NoOutputWords(args[0]));
        return null;
    }

    private static string NoOutputWords(string name) => $"no output '{name}' — STATE's outputs name them (id, number or name)";

    private CommandReply OutputStats(List<string> args)
    {
        var targets = OutputTargets(args, out var refused);
        if (targets is null)
            return refused!;
        var present = PresentBackend?.Present();
        var locks = LockBackend?.Locks();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var o in targets)
            {
                w.WriteStartObject();
                w.WriteString("id", o.Id);
                w.WriteString("name", o.Name);
                WritePresentAndLock(w, o, present, locks);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }));
    }

    private async Task<CommandReply> PresentResetAsync(List<string> args, SessionInfo session, CancellationToken ct)
    {
        // A whole-node reset also lets a blown present fuse go: the next start tries the asked path again.
        const string FuseWords = "the present fuse is cleared: the DXGI path is tried again at the render host's next start";
        var fuseCleared = false;
        if ((args.Count == 0 || args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase)) && BlownFuse is not null && Services.PresentNode is { } node)
        {
            fuseCleared = node.Fuse.Clear();
            node.RefreshFuse();
            Journal.Add("present", FuseWords, session.ClientName);
            FactsChanged();
        }
        if (PresentBackend is not { } backend)
            return fuseCleared ? CommandReply.Success("[]", FuseWords) : CommandReply.Fail("no render host on this node: nothing is presented");
        var targets = OutputTargets(args, out var refused);
        if (targets is null)
            return refused!;
        var views = backend.Present().ToDictionary(v => v.OutputId, StringComparer.Ordinal);
        var ids = targets.Where(o => views.TryGetValue(o.Id, out var v) && v.Present.Presented > 0).Select(o => o.Id).ToList();
        if (ids.Count == 0)
        {
            if (fuseCleared)
                return CommandReply.Success("[]", FuseWords);
            return CommandReply.Fail(targets.Count == 1
                ? $"nothing to reset: {targets[0].Name} has presented nothing since its counters last started"
                : "nothing to reset: no output has presented since its counters last started");
        }
        var before = await backend.ResetAsync(ids, ct).ConfigureAwait(false);
        foreach (var v in before)
            Journal.Add("present", string.Create(CultureInfo.InvariantCulture,
                $"{v.OutputId} counters reset — before: {v.Present.Presented} presented, {v.Present.MissedVblanks} missed vblanks, {v.Present.Held} held, {v.Present.Glitches} glitches"),
                session.ClientName);
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var v in before)
            {
                w.WriteStartObject();
                w.WriteString("id", v.OutputId);
                w.WriteNumber("presented", v.Present.Presented);
                w.WriteNumber("missedVblanks", v.Present.MissedVblanks);
                w.WriteNumber("held", v.Present.Held);
                w.WriteNumber("glitches", v.Present.Glitches);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }), $"reset {before.Count} output{(before.Count == 1 ? "" : "s")}{(fuseCleared ? "; " + FuseWords : "")}");
    }

    /// <summary>NodeSettings.Present.FrameLock "off": this node's outputs free-run, measured, and never join a lock.</summary>
    private bool FrameLockOff => string.Equals(Services.Settings.Present.FrameLock.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    private const string FrameLockOffWords =
        "this node's frame lock is off (NodeSettings.Present.FrameLock): its outputs present free, measured — set it to auto to join";

    /// <summary>A refusal by its reason word, kept for STATE (sync.lock.refused).</summary>
    private CommandReply LockRefuse(string verb, string reason, string words)
    {
        _lockRefusal = new LockRefusal(verb, reason, words, Services.Wall.UnixMs);
        FactsChanged();
        return CommandReply.Fail($"{reason}: {words}");
    }

    private async Task<CommandReply> SyncApplyAsync(List<string> args, CancellationToken ct)
    {
        const string verb = "SYNC APPLY";
        if (LockBackend is not { } backend)
            return LockRefuse(verb, "no-render", "this node has no render host: no output can join a lock");
        if (FrameLockOff)
            return LockRefuse(verb, "off", FrameLockOffWords);
        var sync = Services.Network.Sync();
        if (sync.Role == "follower")
            return LockRefuse(verb, "follower", $"the group's leader applies the lock — SYNC APPLY on {sync.Leader ?? "the leader"}");
        if (Services.ShowGate.Armed && !Air.EffectiveBlackout)
            return LockRefuse(verb, LockReasons.Armed, "the show is armed: joining the lock may step the picture — black out first, or SHOW DISARM");
        var p = Store.Project;
        if (args.Count > 0 && !p.Playlists.Any(l => string.Equals(l.SyncGroup, args[0], StringComparison.Ordinal)))
            return LockRefuse(verb, "unknown-group", $"no sync group '{args[0]}' on this node — a playlist's sync group names it");
        if (backend.Board is not { } board)
            return LockRefuse(verb, LockReasons.NoBoard,
                $"no sync board on this node ({(backend.DriverWords.Length > 0 ? backend.DriverWords : backend.Driver)}): outputs present free, measured");
        if (!board.Configured)
            return LockRefuse(verb, LockReasons.NotConfigured,
                $"the {board.Name} is not configured: once, as administrator, enable it as server on the timing node and as client on the others (or Frame Lock in the driver's control panel), then SYNC APPLY again");
        if (board.CableIntoSwitch)
            return LockRefuse(verb, LockReasons.CableIntoSwitch,
                $"a sync cable of the {board.Name} runs into a network switch: its RJ45 ports go board to board, never to Ethernet — move the cable, then SYNC APPLY again");
        var targets = p.Outputs.Where(o => o.Enabled && o.Width > 0 && o.Height > 0).ToList();
        if (targets.Count == 0)
            return LockRefuse(verb, "no-outputs", "no output is on: nothing to lock");
        var rates = targets.GroupBy(o => Math.Round(o.RefreshHz, 3)).ToList();
        if (rates.Count > 1)
            return LockRefuse(verb, "mixed-rates", "one lock runs at one rate: "
                + string.Join(", ", rates.Select(g => string.Create(CultureInfo.InvariantCulture, $"{string.Join(" and ", g.Select(o => o.Name))} at {g.Key:0.###} Hz"))));
        var ids = targets.Select(o => o.Id).ToList();
        var locks = backend.Locks();
        if (backend.Intent.Apply && backend.HeldOut.Count == 0 && ids.All(id => locks.TryGetValue(id, out var v) && v.Locked))
            return LockRefuse(verb, "already", "every output is in the lock already — SYNC LEAVE first to apply it again");
        var (group, barrier) = (Services.Settings.Present.SwapGroup, Services.Settings.Present.SwapBarrier);
        backend.Apply((uint)Math.Max(1, group), (uint)Math.Max(0, barrier));
        Journal.Add("sync", $"applying the frame lock to {ids.Count} output{(ids.Count == 1 ? "" : "s")} on the {board.Name}");
        var seen = await backend.ObserveAsync(ids, v => v.Locked || v.State == LockState.FellOut || v.Reason.Length > 0, LockObserveWithin, ct)
            .ConfigureAwait(false);
        FactsChanged();
        return LockReceipt(ids, seen, "applied");
    }

    private async Task<CommandReply> SyncLeaveAsync(List<string> args, CancellationToken ct)
    {
        const string verb = "SYNC LEAVE";
        if (LockBackend is not { } backend)
            return LockRefuse(verb, "no-render", "this node has no render host: nothing is in a lock");
        if (!backend.Intent.Apply)
            return LockRefuse(verb, "not-applied", "the lock is not applied: nothing to leave");
        var all = args.Count == 0 || args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase);
        List<string> ids;
        if (all)
        {
            ids = [.. backend.Locks().Keys];
            backend.LeaveAll();
        }
        else
        {
            var targets = OutputTargets(args, out _);
            if (targets is null)
                return LockRefuse(verb, "no-output", NoOutputWords(args[0]));
            ids = [.. targets.Select(o => o.Id).Where(id => !backend.HeldOut.Contains(id))];
            if (ids.Count == 0)
                return LockRefuse(verb, "not-in-lock", $"{targets[0].Name} is out of the lock already");
            backend.Leave(ids);
        }
        var seen = await backend.ObserveAsync(ids, v => v.State is LockState.FreeMeasured or LockState.FreeUnknown or LockState.FellOut,
            LockObserveWithin, ct).ConfigureAwait(false);
        Journal.Add("sync", all ? "the group left the frame lock" : $"{string.Join(", ", ids)} left the frame lock");
        FactsChanged();
        return LockReceipt(ids, seen, "left");
    }

    private async Task<CommandReply> SyncRejoinAsync(List<string> args, CancellationToken ct)
    {
        const string verb = "SYNC REJOIN";
        if (LockBackend is not { } backend)
            return LockRefuse(verb, "no-render", "this node has no render host: nothing is in a lock");
        if (FrameLockOff)
            return LockRefuse(verb, "off", FrameLockOffWords);
        if (!backend.Intent.Apply)
            return LockRefuse(verb, "not-applied", "the lock is not applied: SYNC APPLY first");
        if (Services.ShowGate.Armed && !Air.EffectiveBlackout)
            return LockRefuse(verb, LockReasons.Armed, "the show is armed: rejoining may step the picture — rejoin while blacked out, or SHOW DISARM");
        var locks = backend.Locks();
        IEnumerable<string> candidates;
        if (args.Count == 0 || args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            candidates = locks.Keys;
        }
        else
        {
            var targets = OutputTargets(args, out _);
            if (targets is null)
                return LockRefuse(verb, "no-output", NoOutputWords(args[0]));
            candidates = targets.Select(o => o.Id);
        }
        var ids = candidates.Where(id => backend.HeldOut.Contains(id) || (locks.TryGetValue(id, out var v) && v.State == LockState.FellOut)).ToList();
        if (ids.Count == 0)
            return LockRefuse(verb, "nothing-fell-out", "nothing to rejoin: no output fell out of the lock or was left out");
        backend.Rejoin(ids);
        var seen = await backend.ObserveAsync(ids, v => v.Locked || (v.State == LockState.FellOut && v.Rejoins > 0) || v.Reason.Length > 0,
            LockObserveWithin, ct).ConfigureAwait(false);
        Journal.Add("sync", $"{string.Join(", ", ids)} asked to rejoin the frame lock");
        FactsChanged();
        return LockReceipt(ids, seen, "rejoined");
    }

    /// <summary>What the verb observed of each output it acted on: its lock state, reason and words.</summary>
    private static CommandReply LockReceipt(List<string> ids, IReadOnlyDictionary<string, LockView> seen, string what)
    {
        var locked = ids.Count(id => seen.TryGetValue(id, out var v) && v.Locked);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("did", what);
            w.WriteNumber("locked", locked);
            w.WriteNumber("of", ids.Count);
            w.WriteStartArray("outputs");
            foreach (var id in ids)
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                if (seen.TryGetValue(id, out var v))
                {
                    w.WriteString("state", v.StateWord);
                    w.WriteString("reason", v.Reason);
                    w.WriteString("words", v.Words);
                }
                else
                {
                    w.WriteString("state", "free-unknown");
                    w.WriteString("reason", "");
                    w.WriteString("words", "not seen by the render thread yet");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }), string.Create(CultureInfo.InvariantCulture, $"{what}: {locked} of {ids.Count} locked"));
    }

    // ---- STATE --------------------------------------------------------------------------------------------

    partial void OutputOutput(Utf8JsonWriter w, Output o) =>
        WritePresentAndLock(w, o, PresentBackend?.Present(), LockBackend?.Locks());

    /// <summary>An output's latency, presentation and lock, as STATE's outputs[] and OUTPUT STATS write them.</summary>
    private void WritePresentAndLock(Utf8JsonWriter w, Output o, IReadOnlyList<OutputPresentView>? present,
        IReadOnlyDictionary<string, LockView>? locks)
    {
        w.WriteNumber("latencyMs", Math.Round(o.LatencyMs, 2));
        var view = present?.FirstOrDefault(v => v.OutputId == o.Id);
        if (view is null)
            w.WriteNull("present");
        else
            WritePresent(w, view.Present);
        w.WriteStartObject("lock");
        if (locks is not null && locks.TryGetValue(o.Id, out var l))
        {
            w.WriteString("state", l.StateWord);
            w.WriteString("words", l.Words);
            w.WriteString("reason", l.Reason);
            w.WriteNumber("fellOut", l.FellOut);
            w.WriteNumber("rejoins", l.Rejoins);
        }
        else
        {
            w.WriteString("state", "free-unknown");
            w.WriteString("words", LockBackend is null ? "no render host on this node" : "");
            w.WriteString("reason", "");
            w.WriteNumber("fellOut", 0);
            w.WriteNumber("rejoins", 0);
        }
        w.WriteEndObject();
    }

    private static void WritePresent(Utf8JsonWriter w, in PresentSnapshot p)
    {
        w.WriteStartObject("present");
        w.WriteString("path", p.PathWord);
        w.WriteString("mode", p.ModeWord);
        w.WriteString("evidence", p.EvidenceWord);
        w.WriteNumber("refreshHz", Math.Round(p.RefreshHz, 6));
        w.WriteNumber("nominalHz", Math.Round(p.NominalHz, 3));
        w.WriteNumber("ppm", Math.Round(p.Ppm, 2));
        w.WriteBoolean("settled", p.Settled);
        w.WriteNumber("missedVblanks", p.MissedVblanks);
        w.WriteNumber("missed60s", p.Missed60s);
        w.WriteNumber("held", p.Held);
        w.WriteNumber("delayFrames", p.DelayFrames);
        w.WriteNumber("latencyResidualMs", Math.Round(p.LatencyResidualMs, 2));
        w.WriteNumber("statsAgeMs", Math.Round(p.StatsAgeMs, 1));
        w.WriteString("words", p.Words());
        w.WriteNumber("presented", p.Presented);
        w.WriteNumber("late10s", p.Late10s);
        w.WriteString("phase", p.Path == Render.Platform.PresentPath.Offscreen ? "nominal" : p.Measured ? "approximate" : "unknown");
        w.WriteString("light", ShowContext.LightWord(p.Light));
        w.WriteEndObject();
    }

    partial void SyncOutput(Utf8JsonWriter w)
    {
        var p = Store.Project;
        var local = p.Outputs.Where(o => o.Enabled).Select(o => o.LatencyMs).DefaultIfEmpty(0).Max();
        w.WriteNumber("latencyMs", Math.Round(p.Settings.SyncLatencyMs >= 0 ? p.Settings.SyncLatencyMs : local, 2));
        w.WriteStartObject("lock");
        var backend = LockBackend;
        var locks = backend?.Locks();
        var (state, words) = GroupLock(backend, locks);
        w.WriteString("state", state);
        w.WriteString("driver", backend?.Driver ?? "none");
        if (backend?.Board is { } board)
            w.WriteString("board", board.Name);
        else
            w.WriteNull("board");
        w.WriteString("words", words);
        w.WriteBoolean("applied", backend?.Intent.Apply ?? false);
        w.WriteNumber("locked", locks?.Values.Count(v => v.Locked) ?? 0);
        w.WriteNumber("fellOut", locks?.Values.Sum(v => v.FellOut) ?? 0);
        w.WriteNumber("rejoins", locks?.Values.Sum(v => v.Rejoins) ?? 0);
        w.WriteString("time", backend?.TimeSource ?? "clock");
        if (_lockRefusal is { } r)
        {
            w.WriteStartObject("refused");
            w.WriteString("verb", r.Verb);
            w.WriteString("reason", r.Reason);
            w.WriteString("words", r.Words);
            w.WriteNumber("at", r.AtUnixMs);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("refused");
        }
        w.WriteEndObject();
    }

    /// <summary>The group's lock in one word and its words: framelocked when every output is, fell-out when any fell out, joining while
    /// any joins, free otherwise.</summary>
    private static (string State, string Words) GroupLock(IFrameLockBackend? backend, IReadOnlyDictionary<string, LockView>? locks)
    {
        if (backend is null)
            return ("free-unknown", "no render host on this node");
        if (locks is null || locks.Count == 0)
            return ("free-unknown", backend.Board is null ? $"no sync board ({(backend.DriverWords.Length > 0 ? backend.DriverWords : backend.Driver)})" : "no output is on");
        var views = locks.Values.ToList();
        var board = backend.Board?.Name ?? "no board";
        var locked = views.Count(v => v.Locked);
        if (views.FirstOrDefault(v => v.State == LockState.FellOut) is { } fell)
            return ("fell-out", $"{fell.OutputId} fell out: {fell.Words}");
        if (views.Any(v => v.State == LockState.Joining))
            return ("joining", $"{locked} of {views.Count} outputs locked on the {board}");
        if (locked == views.Count)
            return (views.All(v => v.State == LockState.HouseLocked) ? "house-locked" : "framelocked",
                $"{locked} output{(locked == 1 ? "" : "s")} framelocked on the {board}");
        var free = views.All(v => v.State == LockState.FreeMeasured) ? "free-measured" : "free-unknown";
        var refused = views.FirstOrDefault(v => v.Reason.Length > 0);
        return (free, refused is not null ? refused.Words : backend.Intent.Apply ? $"{locked} of {views.Count} outputs locked" : "presenting free");
    }

    partial void MachineOutput(Utf8JsonWriter w)
    {
        if (PresentBackend is not { } backend)
        {
            w.WriteNull("present");
            return;
        }
        var m = backend.Machine();
        w.WriteStartObject("present");
        w.WriteString("api", m.Api);
        w.WriteString("adapter", m.Adapter);
        w.WriteString("path", m.Path);
        w.WriteBoolean("experimental", m.Experimental);
        w.WriteString("why", m.Why);
        w.WriteNumber("resets", m.Resets);
        w.WriteNumber("presentMsP95", m.PresentMsP95);
        w.WriteString("dialect", m.Dialect);
        w.WriteString("context", m.Context);
        WritePathAndFuse(w, m.FuseArmed);
        w.WriteNumber("airLatencyMs", m.AirLatencyMs);
        w.WriteString("latencyWords", m.LatencyWords);
        w.WriteEndObject();
    }

    /// <summary>In <c>machine.present</c>: <c>asked</c> (the path the next start takes, as kept on the node) and <c>fuse</c>: clear, armed (this DXGI start waits
    /// for its outputs' first settled frames) or blown (a DXGI start never got its outputs up: OpenGL until PRESENT RESET or PRESENT
    /// PATH DXGI), with the attempts and when.</summary>
    private void WritePathAndFuse(Utf8JsonWriter w, bool armed)
    {
        w.WriteString("asked", PresentChoice.Normalise(Services.PresentNode?.Saved.Path ?? Services.Settings.Present.Path));
        w.WriteBoolean("kept", Services.PresentNode is not null);
        var fuse = Services.PresentNode?.FuseNow;
        w.WriteStartObject("fuse");
        w.WriteString("state", armed ? "armed" : fuse is null ? "clear" : "blown");
        w.WriteNumber("attempt", fuse?.Attempt ?? 0);
        w.WriteNumber("at", fuse?.AtUnixMs ?? 0);
        w.WriteEndObject();
    }

    // ---- context, validation --------------------------------------------------------------------------------

    partial void ContextOutput(List<ContextItem> items, List<ContextLink> links)
    {
        var backend = LockBackend;
        var locks = backend?.Locks();
        if (backend is not null && (backend.Intent.Apply || (locks?.Values.Any(v => v.State == LockState.FellOut) ?? false)))
        {
            var (state, words) = GroupLock(backend, locks);
            var light = state switch
            {
                "framelocked" or "house-locked" => Light.Green,
                "fell-out" => Light.Red,
                "joining" => Light.Amber,
                _ => Light.Amber,
            };
            items.Add(new ContextItem("sync:lock", "sync", "Frame lock", light, words));
            // Links speak only the Eye's verbs: a locked output follows the lock; a free-running one hears it.
            foreach (var (id, v) in locks ?? new Dictionary<string, LockView>())
                links.Add(new ContextLink("output:" + id, "sync:lock", v.Locked ? ContextLinkKinds.Follows : ContextLinkKinds.Hears,
                    v.Locked ? Light.Green : v.State == LockState.FellOut ? Light.Red : Light.Amber));
        }
        if (PresentBackend?.Machine() is { Why.Length: > 0 } m)
            items.Add(new ContextItem("machine:present", "machine", "Present path", Light.Amber, m.Why));
    }

    partial void ValidateOutput(Project p, object entity, ref string? why)
    {
        // Another check's words stand: this one only adds its own.
        if (entity is not Output o || why is not null)
            return;
        if (double.IsNaN(o.LatencyMs) || o.LatencyMs < 0 || o.LatencyMs > 500)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"latencyMs is 0–500 ms (the display's input lag); {o.LatencyMs} was given");
            return;
        }
        // The delay ring holds at most six frames: input lags further apart than that cannot be lined up on one node.
        if (o.Enabled && LatencyPlan.Refuse(p.Outputs.Where(x => x.Enabled && x.Id != o.Id).Append(o).Select(x => (x.Id, x.LatencyMs, x.RefreshHz))) is { } apart)
            why = apart;
    }
}
