using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Sync;

namespace PlayMap.Engine.Presentation;

/// <summary>The preflight rows of presentation and the frame lock, per enabled output (section "outputs"):
/// <list type="bullet">
/// <item>present — green within the late budget, amber over it (0.5 % of the last 10 s of refreshes late), grey before anything was
/// presented (or with no render host);</item>
/// <item>lock — while the group's lock is applied: green locked; amber joining, held out (fix: SYNC REJOIN), or free-running after a
/// refused join (fix: SYNC APPLY); red fell out (fix: SYNC REJOIN). An output that fell out shows even when the lock was released
/// since.</item>
/// </list>
/// Reads published state only.</summary>
public sealed class OutputPresentCheck : IPreflightCheck
{
    public string Section => "outputs";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var present = (engine.Services.Outputs as IPresentBackend)?.Present();
        var lockBackend = engine.Services.Outputs as IFrameLockBackend;
        var locks = lockBackend?.Locks();
        var applied = lockBackend?.Intent.Apply ?? false;
        var held = lockBackend?.HeldOut ?? [];
        var rows = new List<PreflightRow>();
        foreach (var o in engine.Store.Project.Outputs.Where(o => o.Enabled))
        {
            rows.Add(PresentRow(node, o, present?.FirstOrDefault(v => v.OutputId == o.Id)));
            if (locks is not null && locks.TryGetValue(o.Id, out var l) && LockRow(node, o, l, applied, held.Contains(o.Id)) is { } row)
                rows.Add(row);
        }
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    private static PreflightRow PresentRow(string node, Output o, OutputPresentView? view)
    {
        var id = "outputs.present." + o.Id;
        if (view is null)
            return new PreflightRow(id, node, "outputs", o.Name, Light.Grey, "not presented", "no render host has drawn it yet", null);
        var p = view.Present;
        var note = p.LateOverBudget > 0
            ? "more than 0.5 % of its refreshes came late in the last 10 s: lighten what it shows, or lower its refresh"
            : "measured against its own refresh";
        return new PreflightRow(id, node, "outputs", o.Name, p.Light, p.Words(), note, null);
    }

    private static PreflightRow? LockRow(string node, Output o, LockView l, bool applied, bool heldOut)
    {
        var id = "outputs.lock." + o.Id;
        var rejoin = "SYNC REJOIN " + o.Id;
        if (l.State == LockState.FellOut)
            return new PreflightRow(id, node, "outputs", o.Name, Light.Red, "fell out", l.Words, rejoin);
        if (!applied)
            return null;
        if (l.Locked)
            return new PreflightRow(id, node, "outputs", o.Name, Light.Green, l.StateWord, l.Words, null);
        if (l.State == LockState.Joining)
            return new PreflightRow(id, node, "outputs", o.Name, Light.Amber, "joining", l.Words, null);
        // Held out: SYNC REJOIN brings it back. Free by a refused join (no board, armed, …): only a new SYNC APPLY asks again, once
        // what refused it is fixed (SYNC REJOIN would answer "nothing fell out").
        if (heldOut)
            return new PreflightRow(id, node, "outputs", o.Name, Light.Amber, "free-running", "left out of the lock the group applied", rejoin);
        var why = l.Words.Length > 0 ? l.Words : "free-running in a group that applied the lock";
        return new PreflightRow(id, node, "outputs", o.Name, Light.Amber, "free-running", why, "SYNC APPLY");
    }
}

/// <summary>INDEX section "present": per enabled output its present light (and red when it fell out of the lock), its lock state,
/// and keys a deck lights from them — feedback hints only (OUTPUT STATS reads, SYNC REJOIN rejoins).</summary>
public sealed class PresentIndex(EngineCore engine) : IIndexContributor
{
    private static readonly IndexPresetHint PresetHint = new("Outputs: present and lock", "output",
    [
        new IndexKey("$(label)", "OUTPUT STATS $(id)", "outputs.$(id).present.light", "instant"),
        new IndexKey("Rejoin", "SYNC REJOIN $(id)", "outputs.$(id).lock.state", "hold"),
    ]);

    public string Section => "present";

    public IndexPresetHint Hint => PresetHint;

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var present = (engine.Services.Outputs as IPresentBackend)?.Present() ?? [];
        var locks = (engine.Services.Outputs as IFrameLockBackend)?.Locks() ?? ImmutableDictionary<string, LockView>.Empty;
        w.WriteStartArray();
        foreach (var o in project.Outputs.Where(o => o.Enabled))
        {
            var view = present.FirstOrDefault(v => v.OutputId == o.Id);
            var lockView = locks.GetValueOrDefault(o.Id);
            var light = lockView?.State == LockState.FellOut ? Light.Red : view?.Present.Light ?? Light.Grey;
            w.WriteStartObject();
            w.WriteString("id", o.Id);
            w.WriteString("kind", "output");
            w.WriteString("label", o.Name);
            w.WriteNull("parent");
            w.WriteString("light", ShowContext.LightWord(light));
            w.WriteString("state", lockView?.StateWord ?? "free-unknown");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
