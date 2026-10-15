using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine.Show;

namespace PlayMap.Engine;

/// <summary>Blind edit: BLIND ON gives the calling session a draft (a second store seeded with the live root) that its document
/// edits go to — through the show's gate, which sets the line's store, and through <see cref="EditAsync"/> off the lane — while the
/// air, STATE and PROJECT GET keep the live project. BLIND DIFF lists the draft entity by entity in field words; BLIND COMMIT lands
/// it as one undo step at a show boundary: now (a commit lead ahead), at an instant, at the playlist's next switch, or with the next
/// GO. A scheduled commit is published as (root, leader instant) in the snapshot, so the render thread draws the new root from the
/// boundary's frame on and the lane promotes it on the tick that reaches it. A draft change to an entity that also changed live
/// since BLIND ON is a conflict, named, never merged (FORCE lands the draft's version); while a commit waits, a live edit to an
/// entity it lands is refused, and any other live edit is carried into the root it publishes.</summary>
public sealed partial class EngineCore
{
    private volatile BlindDraft? _blind;
    private bool _promoting;

    /// <summary>The draft now (any thread reads the published value).</summary>
    internal BlindDraft? BlindNow => _blind;

    /// <summary>Who holds the blind draft, or null.</summary>
    public string? BlindOwnerName => _blind?.OwnerName;

    private bool OwnsDraft(SessionInfo session) => _blind is { } b && b.OwnerSession == session.SessionId;

    private ProjectStore? DraftStoreFor(SessionInfo session) => _blind is { Commit: null } b && b.OwnerSession == session.SessionId ? b.Store : null;

    /// <summary>The store guard while a commit waits: a live edit to an entity it lands is refused (its words say when it lands);
    /// the promotion itself passes.</summary>
    private string? PendingGuard(Project before, Project after)
    {
        if (_promoting || _blind?.Commit is not { } commit)
            return null;
        if (commit.Patch.Touches(before, after) is not { } id)
            return null;
        var what = commit.Patch.Changes.First(c => c.Id == id);
        return $"a blind commit lands {what.Label} ({what.Kind}) {When(commit)} — wait for it, or BLIND DISCARD";
    }

    private string When(BlindCommit commit) => commit.EffectiveLeaderNs == long.MaxValue
        ? "with the next GO" + (commit.PlaylistId is { } pl && Store.Project.FindPlaylist(pl) is { } l ? " on " + l.Name : "")
        : "at " + ClockAt(commit.EffectiveLeaderNs);

    /// <summary>A leader instant as the operator's clock reads it (to the millisecond).</summary>
    private string ClockAt(long leaderNs)
    {
        var unix = Services.Wall.UnixMs + (leaderNs - LeaderNowNs) / 1_000_000;
        return DateTimeOffset.FromUnixTimeMilliseconds(unix).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private CommandReply Blind(VerbCall c)
    {
        if (c.Args.Count > 1 || (c.Args.Count == 1 && !c.Args[0].Equals("STATUS", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("BLIND [STATUS]");
        return CommandReply.Success(JsonOut(WriteBlind));
    }

    private CommandReply BlindOn(VerbCall c)
    {
        if (c.Args.Count > 0)
            return CommandReply.Fail("BLIND ON takes no words (one draft per node; ALL is next round)");
        if (_blind is { } held)
        {
            if (held.OwnerSession == c.Session.SessionId)
                return CommandReply.Fail($"already blind — {Changes(held)} staged since {LockGuard.Clock(held.SinceUnixMs)}");
            // A desk that reconnected (a new session, the same operator) takes its own draft back.
            if (ShowRoleOf(c.Session) == ShowRole.Operator && string.Equals(held.OwnerName, c.Session.ClientName, StringComparison.Ordinal))
            {
                held.OwnerSession = c.Session.SessionId;
                TouchShow();
                return CommandReply.Success(JsonOut(WriteBlind), $"{c.Session.ClientName} takes its blind draft back ({Changes(held)} staged)");
            }
            return CommandReply.Fail($"{held.OwnerName} holds the blind draft ({Changes(held)} staged) — one draft per node: it commits or discards it first");
        }
        var live = Store.Current;
        var draft = new ProjectStore(live.Project, () => Services.Mono.NowNs / 1_000_000);
        // A draft may move what an armed show locks (mapping, layout): that is what it is for. The show's explicit locks hold on it as
        // they hold live, so a draft never stages what its commit would be refused for.
        draft.Guard = (before, after) => LockGuard.Check(WithLiveLocks(before), WithLiveLocks(after), armed: false);
        _blind = new BlindDraft(c.Session.SessionId, c.Session.ClientName, Services.Wall.UnixMs, draft, live);
        draft.Changed += _ => DraftMoved();
        TouchShow();
        RaiseEvent("show.blind", LeaderNowNs, w =>
        {
            w.WriteBoolean("on", true);
            w.WriteString("owner", c.Session.ClientName);
        });
        return CommandReply.Success(JsonOut(WriteBlind), $"blind editing by {c.Session.ClientName}: changes stay off the air until BLIND COMMIT");
    }

    private Project WithLiveLocks(Project p)
    {
        var locks = Store.Project.Show.Locks;
        return p.Show.Locks == locks ? p : p with { Show = p.Show with { Locks = locks } };
    }

    /// <summary>The draft moved (lane): its patch is made again and STATE, the snapshot and the context follow.</summary>
    private void DraftMoved()
    {
        if (_blind is not { } b)
            return;
        b.Patch = ProjectDiff.Between(b.Base.Project, b.Store.Project, b.Base.Revision);
        TouchShow();
    }

    private CommandReply BlindOff(VerbCall c)
    {
        if (c.Args.Count > 0)
            return CommandReply.Fail("BLIND OFF takes no words");
        if (_blind is not { } b)
            return CommandReply.Fail("not blind — BLIND ON starts a draft");
        if (BlindHandsOff(c.Session, b) is { } not)
            return not;
        if (b.Patch.Changes.Count > 0)
            return CommandReply.Fail($"{Changes(b)} staged — BLIND COMMIT or BLIND DISCARD");
        EndBlind();
        return CommandReply.Success(effect: $"{c.Session.ClientName} left blind editing (nothing was staged)");
    }

    /// <summary>The draft is its owner's; the desk (an operator) may also land or drop a draft a session left behind.</summary>
    private CommandReply? BlindHandsOff(SessionInfo session, BlindDraft b) =>
        b.OwnerSession == session.SessionId || ShowRoleOf(session) == ShowRole.Operator
            ? null
            : CommandReply.Fail($"the blind draft is {b.OwnerName}'s — it commits or discards it (or the desk does)");

    private CommandReply BlindDiff(VerbCall c)
    {
        if (c.Args.Count > 0)
            return CommandReply.Fail("BLIND DIFF takes no words");
        if (_blind is not { } b)
            return CommandReply.Fail("not blind — BLIND ON starts a draft");
        var patch = b.Patch;
        var conflicts = patch.ConflictsWith(b.Base.Project, Store.Project);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("base", b.Base.Revision);
            w.WriteString("owner", b.OwnerName);
            w.WriteStartArray("changes");
            foreach (var ch in patch.Changes)
            {
                w.WriteStartObject();
                w.WriteString("id", ch.Id);
                w.WriteString("kind", ch.Kind);
                w.WriteString("label", ch.Label);
                w.WriteString("op", ch.Op.ToString().ToLowerInvariant());
                w.WriteStartArray("fields");
                foreach (var f in ch.Fields)
                {
                    w.WriteStartObject();
                    w.WriteString("field", f.Field);
                    w.WriteString("from", f.From);
                    w.WriteString("to", f.To);
                    w.WriteString("words", f.Words);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (ch.More > 0)
                    w.WriteNumber("more", ch.More);
                w.WriteStartArray("words");
                foreach (var line in ProjectDiff.Words(ch))
                    w.WriteStringValue(line);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("conflicts");
            foreach (var id in conflicts)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private CommandReply BlindDiscard(VerbCall c)
    {
        if (c.Args.Count > 0)
            return CommandReply.Fail("BLIND DISCARD takes no words");
        if (_blind is not { } b)
            return CommandReply.Fail("not blind — nothing to discard");
        if (BlindHandsOff(c.Session, b) is { } not)
            return not;
        var n = b.Patch.Changes.Count;
        var pending = b.Commit is not null;
        EndBlind();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("discarded", n);
            w.WriteBoolean("commitCancelled", pending);
            w.WriteEndObject();
        }), $"blind draft discarded by {c.Session.ClientName} ({n} {(n == 1 ? "change" : "changes")}{(pending ? ", its commit cancelled" : "")})");
    }

    private void EndBlind()
    {
        _blind = null;
        TouchShow();
        RaiseEvent("show.blind", LeaderNowNs, w => w.WriteBoolean("on", false));
    }

    private CommandReply BlindCommitVerb(VerbCall c)
    {
        const string Syntax = "BLIND COMMIT [NOW|NEXT [<playlist>]|ON GO [<playlist>]|AT +<ms>|<unix ms>] [FORCE]";
        if (_blind is not { } b)
            return CommandReply.Fail("not blind — BLIND ON starts a draft");
        if (BlindHandsOff(c.Session, b) is { } not)
            return not;
        if (b.Commit is { } waiting)
            return CommandReply.Fail($"the draft already lands {When(waiting)} — BLIND DISCARD cancels it");
        var a = new List<string>(c.Args);
        var force = a.RemoveAll(x => x.Equals("FORCE", StringComparison.OrdinalIgnoreCase)) > 0;
        var lead = Store.Project.Show.Rules.CommitLeadMs * 1_000_000L;
        var now = LeaderNowNs;
        CommitBoundary boundary;
        string? playlist = null;
        long effective;
        var head = a.Count > 0 ? a[0].ToUpperInvariant() : "NOW";
        switch (head)
        {
            case "NOW" when a.Count <= 1:
                boundary = CommitBoundary.Now;
                effective = now + lead;
                break;
            case "AT":
                var (at, why) = TakeAt(a);
                if (why is not null)
                    return CommandReply.Fail(why);
                if (a.Count > 0)
                    return CommandReply.Fail(Syntax);
                if (at!.Value < now + lead)
                    return CommandReply.Fail($"too close to the boundary — a commit lands at least {lead / 1_000_000} ms ahead (SHOW SET COMMIT-LEAD)");
                boundary = CommitBoundary.At;
                effective = at.Value;
                break;
            case "NEXT" when a.Count <= 2:
            {
                var list = ResolvePlaylist(a.Count > 1 ? a[1] : null);
                if (list is null)
                    return CommandReply.Fail(a.Count > 1 ? $"no playlist {a[1]}" : "no playlist is focused — BLIND COMMIT NEXT <playlist>");
                var lane = Playback.Lane(list.Id);
                if (lane is null || lane.IsIdle || lane.NextItemId is null || lane.SwitchAtLeaderNs == long.MaxValue || lane.SwitchAtLeaderNs <= now)
                    return CommandReply.Fail($"nothing ends on {list.Name} — use ON GO or AT");
                if (lane.SwitchAtLeaderNs < now + lead)
                    return CommandReply.Fail($"too close to the boundary — {list.Name} switches in {(lane.SwitchAtLeaderNs - now) / 1_000_000} ms; the next one, ON GO or AT");
                boundary = CommitBoundary.Next;
                playlist = list.Id;
                effective = lane.SwitchAtLeaderNs;
                break;
            }
            case "ON" when a.Count is 2 or 3 && a[1].Equals("GO", StringComparison.OrdinalIgnoreCase):
            {
                if (a.Count == 3)
                {
                    var list = ResolvePlaylist(a[2]);
                    if (list is null)
                        return CommandReply.Fail($"no playlist {a[2]}");
                    playlist = list.Id;
                }
                boundary = CommitBoundary.OnGo;
                effective = long.MaxValue;
                break;
            }
            default:
                return CommandReply.Fail(Syntax);
        }
        var patch = b.Patch;
        if (patch.Changes.Count == 0)
            return CommandReply.Fail("the draft changes nothing — BLIND OFF leaves");
        var live = Store.Project;
        var conflicts = patch.ConflictsWith(b.Base.Project, live);
        if (conflicts.Count > 0 && !force)
        {
            var named = string.Join(", ", conflicts.Select(id => patch.Changes.First(x => x.Id == id)).Select(x => $"{x.Label} ({x.Kind}, {x.Id})"));
            return CommandReply.Fail($"changed on the air since BLIND ON: {named} — BLIND DIFF shows the draft; FORCE lands the draft's version");
        }
        var root = patch.ApplyTo(live);
        if (LockGuard.Check(live, root, armed: false) is { } locked)
            return CommandReply.Fail(locked);
        var commit = new BlindCommit(patch, boundary, playlist, effective, c.Session.ClientName, force, root, live);
        b.Commit = commit;
        TouchShow();
        return new CommandReply(true, JsonOut(w => WriteCommit(w, commit)),
            $"blind draft ({patch.Changes.Count} {(patch.Changes.Count == 1 ? "change" : "changes")}) lands {When(commit)}{(force ? " (forced over " + conflicts.Count + " conflict" + (conflicts.Count == 1 ? ")" : "s)") : "")}",
            Applied: false);
    }

    /// <summary>A GO (PLAYLIST GO, NEXT, PREV, VIDEO PLAY or RESTART) accepted on a playlist, starting at a leader instant: a commit
    /// waiting for a GO there takes that instant, so the draft lands on the same frame as the item.</summary>
    private void BlindOnGo(string playlistId, long startNs)
    {
        if (_blind?.Commit is not { Boundary: CommitBoundary.OnGo, EffectiveLeaderNs: long.MaxValue } commit
            || (commit.PlaylistId is { } only && only != playlistId))
            return;
        _blind.Commit = commit with { EffectiveLeaderNs = startNs, PlaylistId = playlistId };
        TouchShow();
    }

    /// <summary>On the lane's tick: a commit whose instant came is published as one undo step and the draft ends. The render thread
    /// has drawn its root since that instant; this makes it the document.</summary>
    private void TickBlind(long leaderNs)
    {
        if (_blind is not { Commit: { } commit } b || leaderNs < commit.EffectiveLeaderNs)
            return;
        var root = commit.Patch.ApplyTo(Store.Project);
        EditResult r;
        _promoting = true;
        try
        {
            r = Store.Apply(commit.Label, _ => root);
        }
        finally
        {
            _promoting = false;
        }
        if (!r.Changed)
        {
            // A lock set after the commit was scheduled refuses it: the draft stays, so nothing staged is lost.
            b.Commit = null;
            Journal.Add("refused", $"{commit.Label} by {commit.By} did not land: {r.Effect}", commit.By, ok: false);
            TouchShow();
            return;
        }
        _blind = null;
        Journal.Add("verb", $"{commit.Label} landed ({commit.BoundaryWord}) by {commit.By}", commit.By);
        TouchShow();
        RaiseEvent("show.commit", commit.EffectiveLeaderNs, w =>
        {
            w.WriteNumber("changes", commit.Patch.Changes.Count);
            w.WriteString("by", commit.By);
            w.WriteString("boundary", commit.BoundaryWord);
        });
    }

    /// <summary>The snapshot's draft (for the editor's draft view) and a waiting commit's root, carried over whatever changed live
    /// since it was made (lane).</summary>
    private void SnapshotBlind(ref EngineSnapshot snapshot)
    {
        if (_blind is not { } b)
            return;
        var pending = (PendingCommit?)null;
        if (b.Commit is { } commit)
        {
            var live = snapshot.Project.Project;
            if (!ReferenceEquals(commit.LiveAt, live))
            {
                commit = commit with { Root = commit.Patch.ApplyTo(live), LiveAt = live };
                b.Commit = commit;
            }
            pending = new PendingCommit(commit.Root, commit.EffectiveLeaderNs, commit.Label, commit.Patch.Changes.Count);
        }
        snapshot = snapshot with { Draft = b.Store.Current, Pending = pending };
    }

    private static string Changes(BlindDraft b) => b.Patch.Changes.Count == 1 ? "1 change" : $"{b.Patch.Changes.Count} changes";

    /// <summary>STATE.show.blind and BLIND's reply: null when no draft is open.</summary>
    internal void WriteBlind(Utf8JsonWriter w)
    {
        if (_blind is not { } b)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteBoolean("on", true);
        w.WriteString("owner", b.OwnerName);
        w.WriteNumber("since", b.SinceUnixMs);
        w.WriteNumber("base", b.Base.Revision);
        w.WriteNumber("changes", b.Patch.Changes.Count);
        w.WritePropertyName("commit");
        if (b.Commit is { } commit)
            WriteCommit(w, commit);
        else
            w.WriteNullValue();
        w.WriteEndObject();
    }

    private static void WriteCommit(Utf8JsonWriter w, BlindCommit commit)
    {
        w.WriteStartObject();
        w.WriteString("boundary", commit.BoundaryWord);
        if (commit.PlaylistId is { } pl)
            w.WriteString("playlist", pl);
        else
            w.WriteNull("playlist");
        if (commit.EffectiveLeaderNs == long.MaxValue)
            w.WriteNull("effectiveLeaderNs");
        else
            w.WriteNumber("effectiveLeaderNs", commit.EffectiveLeaderNs);
        w.WriteNumber("changes", commit.Patch.Changes.Count);
        w.WriteString("by", commit.By);
        w.WriteBoolean("forced", commit.Forced);
        w.WriteEndObject();
    }
}
