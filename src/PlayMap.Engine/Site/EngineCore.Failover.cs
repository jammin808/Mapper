using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Devices.Model;

namespace PlayMap.Engine;

/// <summary>
/// What a stack does when a member fails (facade §3.9). Degraded is a measured fact: the member's projector in standby or cooling down,
/// reporting an error, or its shutter closed while PlayMap closed none (no panic, no scan); or the member's output, a real display,
/// not open. HOLD keeps the rest as they are and says so (STATE, the context, the journal). MATCH dims every other output of the stack's
/// blend group (its members' outputs and those the solved blend overlaps with them, transitively) to the stack's remaining light —
/// Σ remaining lumens ÷ Σ lumens — as a runtime degrade gain through <see cref="IOutputGains"/>, fading down in 0.5 s and back up over
/// 2 s when the member returns. Each change is journalled with its cause. The watch runs on the lane.
/// </summary>
public sealed partial class EngineCore
{
    /// <summary>A stack's degraded members (id → cause) and the gain its blend group heads to.</summary>
    internal sealed record StackDegradation(ImmutableSortedDictionary<string, string> Members, float Gain);

    private ImmutableDictionary<string, StackDegradation> _degraded = ImmutableDictionary<string, StackDegradation>.Empty;

    /// <summary>Each stack's degradation now (stacks with every member well are absent).</summary>
    internal ImmutableDictionary<string, StackDegradation> StackDegradations => Volatile.Read(ref _degraded);

    /// <summary>STACK FAILOVER &lt;stack&gt; HOLD|MATCH: what the stack does when a member fails; a stack already degraded applies it at once.</summary>
    private CommandReply StackFailoverVerb(List<string> args)
    {
        const string Usage = "STACK FAILOVER <stack> HOLD|MATCH";
        if (args.Count != 2)
            return CommandReply.Fail(Usage);
        var p = Edits.Project;
        if (StackNamed(p, args[0]) is not { } stack)
            return CommandReply.Fail($"no stack {args[0]} — STACK NEW makes one");
        StackFailover policy;
        if (args[1].Equals("HOLD", StringComparison.OrdinalIgnoreCase))
            policy = StackFailover.Hold;
        else if (args[1].Equals("MATCH", StringComparison.OrdinalIgnoreCase))
            policy = StackFailover.Match;
        else
            return CommandReply.Fail(Usage);
        if (stack.Failover == policy)
            return CommandReply.Fail($"{stack.Name} {(policy == StackFailover.Match ? "matches" : "holds")} already — this would change nothing");
        var r = Edits.Apply($"failover of {stack.Name}", pp => pp.WithStage(st => st with { Stacks = st.Stacks.Update(stack.Id, s => s with { Failover = policy }) }));
        if (!r.Changed)
            return FromEdit(r);
        var staged = EditingBlind;
        if (!staged)
            WatchStacks();
        var word = policy == StackFailover.Match ? "match" : "hold";
        var said = Staged(staged, policy == StackFailover.Match
            ? $"{stack.Name} matches: when a member fails, its blend group dims to the light that remains"
            : $"{stack.Name} holds: when a member fails, the rest stay as they are");
        if (Services.StackGains is null && policy == StackFailover.Match)
            said += " — this node composes no degrade gains (the site part is not attached), so the stack holds";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stack", stack.Id);
            w.WriteString("failover", word);
            w.WriteBoolean("staged", staged);
            w.WriteString("words", said);
            w.WriteEndObject();
        }), said);
    }

    /// <summary>Why a member is degraded now (null when it is well): its projector's measured state through its device link, then its
    /// output's health.</summary>
    private string? MemberFault(Project p, string projectorId)
    {
        if (Services.Devices is { } devices)
        {
            foreach (var link in p.Stage.Devices)
            {
                if (!string.Equals(link.ProjectorId, projectorId, StringComparison.Ordinal) || devices.State(link.Id) is not { } state)
                    continue;
                if (state.Power is PowerState.Standby or PowerState.CoolingDown)
                    return $"{link.Name} is {(state.Power == PowerState.Standby ? "in standby" : "cooling down")}";
                if (state.Faults.FirstOrDefault(f => f.Severity == FaultSeverity.Error) is { } fault)
                    return $"{link.Name}: {fault.Words}";
                if (state.ShutterClosed == true && _air.Panic is null && Services.Scan.Current is not { State: "starting" or "running" })
                    return $"{link.Name}'s shutter is closed, and PlayMap closed none";
            }
        }
        if (OutputOf(p, projectorId) is { Kind: not OutputKind.Virtual } output
            && Services.Outputs.Health().FirstOrDefault(h => string.Equals(h.OutputId, output.Id, StringComparison.Ordinal)) is { Open: false })
            return $"{output.Name} is not open";
        return null;
    }

    /// <summary>The outputs of a stack's blend group: its members' outputs and every output the solved blend overlaps with one of
    /// the group, transitively (its members' alone before a blend is solved).</summary>
    private HashSet<string> BlendGroupOf(Project p, StackGroup stack)
    {
        var group = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in stack.ProjectorIds)
            if (OutputOf(p, id) is { } o)
                group.Add(o.Id);
        if (LastBlendSolve?.Solution is not { } solution)
            return group;
        var byName = p.Outputs.GroupBy(o => o.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var blend in solution.Outputs)
            {
                if (!group.Contains(blend.OutputId))
                    continue;
                foreach (var name in blend.OverlapsWith)
                    if (byName.TryGetValue(name, out var other) && group.Add(other))
                        grew = true;
            }
        }
        return group;
    }

    /// <summary>The watch (on the lane): every stack's degraded members measured now, the degrade gains set for MATCH stacks, each
    /// change journalled with its cause and published as a fact.</summary>
    internal void WatchStacks()
    {
        if (Stopping)
            return;
        var p = Store.Project;
        var next = ImmutableDictionary.CreateBuilder<string, StackDegradation>(StringComparer.Ordinal);
        var targets = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var stack in p.Stage.Stacks)
        {
            var lost = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var id in stack.ProjectorIds)
                if (MemberFault(p, id) is { } why)
                    lost[id] = why;
            if (lost.Count == 0)
                continue;
            double all = 0, left = 0;
            foreach (var id in stack.ProjectorIds)
            {
                var lumens = p.FindProjector(id) is { Lumens: > 0 } pj ? pj.Lumens : 1;
                all += lumens;
                if (!lost.ContainsKey(id))
                    left += lumens;
            }
            var gain = stack.Failover == StackFailover.Match && Services.StackGains is not null ? (float)(all > 0 ? left / all : 0) : 1f;
            next[stack.Id] = new StackDegradation(lost.ToImmutable(), gain);
            if (gain >= 1)
                continue;
            var dark = lost.Keys.Select(id => OutputOf(p, id)?.Id).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var output in BlendGroupOf(p, stack))
                if (!dark.Contains(output))
                    targets[output] = Math.Min(targets.GetValueOrDefault(output, 1f), gain);
        }
        var was = _degraded;
        var now = next.ToImmutable();
        Services.StackGains?.Set(targets);
        if (Same(was, now))
            return;
        foreach (var stack in p.Stage.Stacks)
        {
            was.TryGetValue(stack.Id, out var before);
            now.TryGetValue(stack.Id, out var after);
            foreach (var (id, why) in after?.Members ?? ImmutableSortedDictionary<string, string>.Empty)
                if (before is null || !before.Members.ContainsKey(id))
                    Journal.Add("stack", $"{stack.Name} lost {NameOfProjector(p, id)} ({why}) — " + (after!.Gain < 1
                        ? string.Create(CultureInfo.InvariantCulture, $"matched: its blend group dims to {after.Gain * 100:0} %")
                        : "held: the rest stay as they are"));
            foreach (var id in before?.Members.Keys ?? Enumerable.Empty<string>())
                if (after is null || !after.Members.ContainsKey(id))
                    Journal.Add("stack", $"{stack.Name}: {NameOfProjector(p, id)} is back" + (after is null && before!.Gain < 1 ? " — its blend group returns to full over 2 s" : ""));
        }
        Volatile.Write(ref _degraded, now);
        FactsChanged();
    }

    private static string NameOfProjector(Project p, string id) => p.FindProjector(id)?.Name ?? id;

    private static bool Same(ImmutableDictionary<string, StackDegradation> a, ImmutableDictionary<string, StackDegradation> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o.Gain == kv.Value.Gain
            && o.Members.Count == kv.Value.Members.Count && o.Members.All(m => kv.Value.Members.TryGetValue(m.Key, out var w) && w == m.Value));
}
