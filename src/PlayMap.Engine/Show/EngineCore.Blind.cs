using System.Collections.Concurrent;
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

    /// <summary>What the show's gate decided for each session's last off-lane line while a draft was open: the draft it sends the
    /// line's edits to, or null for the live store. The edit is applied later, on the lane, and keeps that decision: a line decided
    /// for a draft that has since ended or started landing is refused, never written live.</summary>
    private readonly ConcurrentDictionary<(string Transport, string Session), BlindDraft?> _offLaneDraft = new();

    /// <summary>The draft now (any thread reads the published value).</summary>
    internal BlindDraft? BlindNow => _blind;

    /// <summary>Who holds the blind draft, or null.</summary>
    public string? BlindOwnerName => _blind?.OwnerName;

    private bool OwnsDraft(SessionInfo session) => _blind is { } b && b.Owner.Is(session);

    private ProjectStore? DraftStoreFor(SessionInfo session) => _blind is { Commit: null } b && b.Owner.Is(session) ? b.Store : null;

    /// <summary>Off the lane: notes the gate's decision for a session's line (the draft, or live) while a draft is open.</summary>
    internal void NoteOffLaneDecision(SessionInfo session, ProjectStore? draft)
    {
        var key = (session.Transport, session.SessionId);
        if (_blind is not { } b)
        {
            _offLaneDraft.TryRemove(key, out _);
            return;
        }
        _offLaneDraft[key] = draft is not null && ReferenceEquals(draft, b.Store) ? b : null;
    }

    /// <summary>The store an off-lane line's document edit goes to (on the lane): what its gate decided — the draft while it is still
    /// open and not landing, a closed store that refuses with words once it is not, or live; with no decision noted, the owner's
    /// draft.</summary>
    private ProjectStore? OffLaneStoreFor(SessionInfo session)
    {
        if (!_offLaneDraft.TryGetValue((session.Transport, session.SessionId), out var decided))
            return DraftStoreFor(session);
        if (decided is null)
            return null;
        if (ReferenceEquals(decided, _blind) && decided.Commit is null)
            return decided.Store;
        var closed = new ProjectStore(Store.Project);
        closed.Guard = (_, _) => ReferenceEquals(decided, _blind)
            ? "the blind draft this line was for is landing — nothing changed (BLIND DISCARD cancels the commit)"
            : "the blind draft this line was for has ended — nothing changed";
        return closed;
    }

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
        // A draft is a session's: a door whose every request is a session of its own could never edit the draft it opened.
        if (c.Session.Transport is "mcp" or "http")
            return CommandReply.Fail("a blind draft needs a lasting connection — BLIND ON from the desk, the wire or a WebSocket");
        if (_blind is { } held)
        {
            if (held.Owner.Is(c.Session))
                return CommandReply.Fail($"already blind — {Changes(held)} staged since {LockGuard.Clock(held.SinceUnixMs)}");
            // A desk that reconnected (a new session, the same operator) takes its own draft back — only once the old session is
            // gone, so two connected desks never edit one draft by turns without knowing.
            if (ShowRoleOf(c.Session) == ShowRole.Operator && string.Equals(held.OwnerName, c.Session.ClientName, StringComparison.Ordinal))
            {
                if (Companion.Clients.Find(held.Owner.SessionId) is { } still && still.Transport == held.Owner.Transport)
                    return CommandReply.Fail($"{held.OwnerName} holds the blind draft on another connection that is still open — it commits or discards it there");
                var was = held.Owner;
                held.Owner = DraftOwner.Of(c.Session);
                Journal.Add("verb", $"{c.Session.ClientName} took its blind draft back on a new connection ({Changes(held)} staged)", c.Session.ClientName);
                TouchShow();
                RaiseEvent("show.blind", LeaderNowNs, w =>
                {
                    w.WriteBoolean("on", true);
                    w.WriteString("owner", c.Session.ClientName);
                    w.WriteString("takenFrom", was.SessionId);
                });
                return CommandReply.Success(JsonOut(WriteBlind), $"{c.Session.ClientName} takes its blind draft back ({Changes(held)} staged)");
            }
            return CommandReply.Fail($"{held.OwnerName} holds the blind draft ({Changes(held)} staged) — one draft per node: it commits or discards it first");
        }
        var live = Store.Current;
        var draft = new ProjectStore(live.Project, () => Services.Mono.NowNs / 1_000_000);
        // A draft may move what an armed show locks (mapping, layout): that is what it is for. The show's explicit locks hold on it as
        // they hold live, so a draft never stages what its commit would be refused for.
        draft.Guard = (before, after) => LockGuard.Check(WithLiveLocks(before), WithLiveLocks(after), armed: false);
        // Decisions noted for an earlier draft are for a draft that is gone; each session's next off-lane line notes its own.
        _offLaneDraft.Clear();
        _blind = new BlindDraft(DraftOwner.Of(c.Session), Services.Wall.UnixMs, draft, live);
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
        b.Owner.Is(session) || ShowRoleOf(session) == ShowRole.Operator
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
        // Within a commit lead of its instant the render may already draw the commit: it lands, and UNDO takes it back.
        if (b.Commit is { } landing && LeaderNowNs >= landing.EffectiveLeaderNs - Store.Project.Show.Rules.CommitLeadMs * 1_000_000L)
            return CommandReply.Fail($"the draft is landing now ({ClockAt(landing.EffectiveLeaderNs)}) — UNDO takes it back once it has landed");
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
        // The gate's decisions stay: a line decided for this draft whose edit arrives after it ended is refused, not written live.
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
                if (NextBoundary(lane) is not { } next || next <= now)
                    return CommandReply.Fail($"nothing ends on {list.Name} — use ON GO or AT");
                if (next < now + lead)
                    return CommandReply.Fail($"too close to the boundary — {list.Name} switches in {(next - now) / 1_000_000} ms; the next one, ON GO or AT");
                boundary = CommitBoundary.Next;
                playlist = list.Id;
                effective = next;
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
        var commit = new BlindCommit(patch, boundary, playlist, effective, c.Session.ClientName, force, root, live)
        {
            ByOperator = ShowRoleOf(c.Session) is ShowRole.Operator or ShowRole.System,
        };
        b.Commit = commit;
        TouchShow();
        return new CommandReply(true, JsonOut(w => WriteCommit(w, commit)),
            $"blind draft ({patch.Changes.Count} {(patch.Changes.Count == 1 ? "change" : "changes")}) lands {When(commit)}{(force ? " (forced over " + conflicts.Count + " conflict" + (conflicts.Count == 1 ? ")" : "s)") : "")}",
            Applied: false);
    }

    /// <summary>The instant a playlist's next item first shows (its transition window's opening, or the switch for a cut), or null
    /// when nothing is scheduled to follow.</summary>
    private static long? NextBoundary(Playback.PlaylistPlayback? lane) =>
        lane is null || lane.IsIdle || lane.NextItemId is null || lane.SwitchAtLeaderNs == long.MaxValue ? null : lane.NextShowsAtNs;

    /// <summary>Before a GO (PLAYLIST GO, NEXT, PREV, VIDEO PLAY or RESTART) is applied on a playlist starting at a leader instant: a
    /// commit waiting for a GO there takes the GO's own instant, and one waiting for that playlist's next switch does too when the GO
    /// changes the item (<paramref name="switches"/>: the GO is the switch; a PLAY that resumes the same item is not). Nothing is
    /// published here — the GO's own playback change publishes the snapshot, so the item and the draft land on the same frame.
    /// Answers the commit as it was, for <see cref="BlindGoRefused"/> when the GO is refused after all.</summary>
    private BlindCommit? BlindOnGo(string playlistId, long startNs, bool switches)
    {
        if (_blind?.Commit is not { } commit || (commit.PlaylistId is { } only && only != playlistId))
            return null;
        if (commit is { Boundary: CommitBoundary.OnGo, EffectiveLeaderNs: long.MaxValue })
            _blind.Commit = commit with { EffectiveLeaderNs = startNs, PlaylistId = playlistId };
        else if (switches && commit.Boundary == CommitBoundary.Next && startNs < commit.EffectiveLeaderNs)
            _blind.Commit = commit with { EffectiveLeaderNs = startNs, Pinned = true };
        else
            return null;
        return commit;
    }

    /// <summary>The GO a commit was moved for did not happen: the commit waits as it did.</summary>
    private void BlindGoRefused(BlindCommit? before)
    {
        if (before is not null && _blind is { } b)
            b.Commit = before;
    }

    /// <summary>On the lane's tick: a commit whose instant came is published as one undo step and the draft ends. The render thread
    /// has drawn its root since that instant; this makes it the document.</summary>
    private void TickBlind(long leaderNs)
    {
        if (_blind is { } open && open.Base.Project.Id != Store.Project.Id)
        {
            // Another show was opened under the draft: the draft and its commit were for the show that left.
            Journal.Add("refused", $"the blind draft by {open.OwnerName} ended: another show was opened ({Changes(open)} were staged)", open.OwnerName, ok: false);
            EndBlind();
            return;
        }
        if (_blind is not { Commit: { } commit } b || leaderNs < commit.EffectiveLeaderNs)
            return;
        if (_showMode.Armed && !commit.ByOperator)
        {
            // An armed show lands only the desk's commits; a commit a remote scheduled in rehearsal waits for the desk.
            b.Commit = null;
            Journal.Add("refused", $"{commit.Label} by {commit.By} did not land: an armed show lands only an operator's commit — the draft stays; the desk can BLIND COMMIT it", commit.By, ok: false);
            TouchShow();
            return;
        }
        LandCommit(b, commit);
    }

    /// <summary>A commit whose instant came lands as one undo step, or stays staged with the words when a lock refuses it. Its own
    /// method: its closures (the root it lands, the commit its event names) are made only when a commit lands. Written in
    /// <see cref="TickBlind"/>, an optimised build would allocate them at the method's entry, on every tick of the lane.</summary>
    private void LandCommit(BlindDraft b, BlindCommit commit)
    {
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
    /// since it was made, at an instant that follows its playlist's next switch until a GO pins it (lane). A commit the air would not
    /// keep — one an armed show would not land, one a lock now refuses, one for a show that is no longer open — is not published, so
    /// the render never draws what the lane will not land.</summary>
    private void SnapshotBlind(ref EngineSnapshot snapshot)
    {
        if (_blind is not { } b)
            return;
        var live = snapshot.Project.Project;
        if (b.Base.Project.Id != live.Id)
            return;
        var pending = (PendingCommit?)null;
        if (b.Commit is { } commit)
        {
            if (commit is { Boundary: CommitBoundary.Next, Pinned: false } && LeaderNowNs < commit.EffectiveLeaderNs
                && commit.PlaylistId is { } pl && snapshot.Playback.Playlists.TryGetValue(pl, out var lane))
            {
                var next = NextBoundary(lane) ?? long.MaxValue;
                if (next != commit.EffectiveLeaderNs)
                    commit = commit with { EffectiveLeaderNs = next };
            }
            if (!ReferenceEquals(commit.LiveAt, live))
                commit = commit with { Root = commit.Patch.ApplyTo(live), LiveAt = live };
            if (!ReferenceEquals(commit, b.Commit))
                b.Commit = commit;
            var lands = (commit.ByOperator || !_showMode.Armed) && LockGuard.Check(live, commit.Root, armed: false) is null;
            if (lands)
                pending = new PendingCommit(commit.Root, commit.EffectiveLeaderNs, commit.Label, commit.Patch.Changes.Count);
        }
        snapshot = snapshot with { Draft = b.Store.Current, Pending = pending };
    }

    private static string Changes(BlindDraft b) => BlindWords.Changes(b.Patch.Changes.Count);

    /// <summary>STATE.show.blind and BLIND's reply: null when no draft is open. The owner's session and transport are WHOAMI's, so a
    /// client knows the draft is its own (its document edits go there) without asking.</summary>
    internal void WriteBlind(Utf8JsonWriter w)
    {
        if (_blind is not { } b)
        {
            w.WriteNullValue();
            return;
        }
        var owner = b.Owner;
        w.WriteStartObject();
        w.WriteBoolean("on", true);
        w.WriteString("owner", b.OwnerName);
        w.WriteString("session", owner.SessionId);
        w.WriteString("transport", owner.Transport);
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

    private void WriteCommit(Utf8JsonWriter w, BlindCommit commit)
    {
        w.WriteStartObject();
        // The boundary in the words the owner's refused edits are given (BlindWords.Landing).
        w.WriteString("when", When(commit));
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
