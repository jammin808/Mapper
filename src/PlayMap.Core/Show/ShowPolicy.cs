using System.Collections.Frozen;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Show;

/// <summary>Show Mode's table: who a session is (from its transport, never from its words), what a line does to the show (its
/// row's class; a generic edit by the id or kind it names), and what an armed or rehearsing node makes of that line from that
/// role. Pure: an Allow allocates nothing; a refusal's words name the rule ("Not while armed") and the way out.</summary>
public static class ShowPolicy
{
    private static readonly ShowDecision Allowed = new(ShowVerdict.Allow, null);

    /// <summary>The role a session plays, with no proof of the node's token on its connection: see the overload.</summary>
    public static ShowRole RoleOf(SessionInfo session, IReadOnlyCollection<string> operators) => RoleOf(session, operators, proved: false);

    /// <summary>The role a session plays: the desk is the operator; an assistant or MCP client is an assistant; a person's
    /// approval of one is approved; a fleet peer or the node itself is a peer or the system; any other paired client is a remote,
    /// or an operator when the node's operators list names it. A name is a claim, so the list grants the role only to a session
    /// that <paramref name="proved"/> the node's token on its own connection (trust of an address is not proof), and only by an
    /// address rule: an entry that is an address names that address; "name@address" names both; a bare name names only a session
    /// from this machine. An unpaired session only observes.</summary>
    public static ShowRole RoleOf(SessionInfo session, IReadOnlyCollection<string> operators, bool proved)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operators);
        if (!session.Paired)
            return ShowRole.Observer;
        switch (session.Transport)
        {
            case "desk":
                return ShowRole.Operator;
            case "assistant" or "mcp":
                return ShowRole.Assistant;
            case "approval":
                return ShowRole.Approved;
            case "fleet" or "sync":
                return ShowRole.Peer;
            case "system":
                return ShowRole.System;
        }
        if (!proved)
            return ShowRole.Remote;
        foreach (var o in operators)
        {
            if (Names(o.Trim(), session))
                return ShowRole.Operator;
        }
        return ShowRole.Remote;
    }

    /// <summary>One operators entry against a session: an address (IPv4 or IPv6, compared as text after trimming), "name@address",
    /// or a bare name from this machine only.</summary>
    private static bool Names(string entry, SessionInfo session)
    {
        if (entry.Length == 0)
            return false;
        var at = entry.LastIndexOf('@');
        if (at > 0 && at < entry.Length - 1)
        {
            return string.Equals(entry[..at].Trim(), session.ClientName, StringComparison.OrdinalIgnoreCase)
                && SameAddress(entry[(at + 1)..].Trim(), session.RemoteAddress);
        }
        if (System.Net.IPAddress.TryParse(entry, out _))
            return SameAddress(entry, session.RemoteAddress);
        return session.IsLoopback && string.Equals(entry, session.ClientName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameAddress(string listed, string remote)
    {
        if (!System.Net.IPAddress.TryParse(listed, out var a) || !System.Net.IPAddress.TryParse(remote, out var b))
            return false;
        if (a.IsIPv4MappedToIPv6)
            a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6)
            b = b.MapToIPv4();
        return a.Equals(b);
    }

    /// <summary>The line's class: the row's own, or for a generic edit (SET, ADD, REMOVE, MOVE, NUDGE) the class of the id it
    /// names (ADD: of the kind it adds). An unknown id prefix or kind is classed as mapping, the cautious side.</summary>
    public static ShowClass ClassOf(VerbSpec spec, WireLine line)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        if (spec.Show != ShowClass.Target)
            return spec.Show;
        var first = line.Word(VerbWords(spec));
        return string.Equals(spec.Name, "ADD", StringComparison.Ordinal) ? OfKind(first) : OfTarget(first);
    }

    /// <summary>The class of an entity by its id's prefix.</summary>
    public static ShowClass OfTarget(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length < 2)
            return ShowClass.Mapping;
        return id.AsSpan(0, 2) switch
        {
            Ids.Output or Ids.LedNode or Ids.LedFixture or Ids.LedType => ShowClass.Layout,
            Ids.Canvas or Ids.Layer or Ids.Generator or Ids.Fx or Ids.FxAsset or Ids.GeneratorPreset or Ids.Send => ShowClass.Content,
            Ids.Playlist or Ids.PlaylistItem => ShowClass.Playlists,
            Ids.Media or Ids.MediaVersion => ShowClass.Media,
            Ids.Device => ShowClass.DeviceSafe,
            Ids.Project => ShowClass.Project,
            Ids.ControlSurface or Ids.ControlBinding or Ids.TimecodeSource => ShowClass.Setup,
            _ => ShowClass.Mapping,
        };
    }

    /// <summary>The class of what ADD makes, by its kind word.</summary>
    public static ShowClass OfKind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Kinds.TryGetValue(kind, out var cls) ? cls : ShowClass.Mapping;
    }

    private static readonly FrozenDictionary<string, ShowClass> Kinds = new Dictionary<string, ShowClass>(StringComparer.OrdinalIgnoreCase)
    {
        ["media"] = ShowClass.Media,
        ["playlist"] = ShowClass.Playlists,
        ["item"] = ShowClass.Playlists,
        ["generator"] = ShowClass.Content,
        ["canvas"] = ShowClass.Content,
        ["layer"] = ShowClass.Content,
        ["fx"] = ShowClass.Content,
        ["send"] = ShowClass.Content,
        ["output"] = ShowClass.Layout,
        ["lednode"] = ShowClass.Layout,
        ["fixturetype"] = ShowClass.Layout,
        ["fixture"] = ShowClass.Layout,
        ["device"] = ShowClass.DeviceSafe,
        ["binding"] = ShowClass.Setup,
        ["controlsurface"] = ShowClass.Setup,
        ["timecodesource"] = ShowClass.Setup,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the show makes of a line from a role now. Rehearsal allows every line (the router's pairing, the locks and
    /// MinGo still apply); armed, the table of docs/SHOW.md decides. PANIC is never refused for a paired person.</summary>
    public static ShowDecision Decide(VerbSpec spec, WireLine line, ShowRole role, in ShowFacts facts)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        var cls = ClassOf(spec, line);
        if (cls == ShowClass.Query)
            return Allowed;
        if (IsStop(spec) && role is ShowRole.Operator or ShowRole.Remote or ShowRole.Observer or ShowRole.Approved or ShowRole.Peer or ShowRole.System)
            return Allowed;
        if (role == ShowRole.Observer)
            return Refuse("this client only reads — pair it (its token) to run lines");
        // The session that holds the blind draft edits the draft, armed or not: its document edits never reach the air.
        if (facts.BlindOwner && role is not (ShowRole.Peer or ShowRole.System) && Draftable(spec, cls))
            return Draft;
        // Landing a draft is a person's at the desk: an assistant's BLIND COMMIT is refused rather than queued, because the approval
        // would run under a session that neither holds the draft nor may land one while armed.
        if (role == ShowRole.Assistant && Is(spec, "BLIND COMMIT"))
            return Refuse("an assistant's blind draft is landed by a person: BLIND DIFF shows it, BLIND COMMIT on the desk lands it");
        if (!facts.Armed)
            return Allowed;
        // An approved assistant's line gets what a remote client gets and never more, so a read-only remote reads only for it too.
        if ((role is ShowRole.Remote or ShowRole.Approved) && facts.RemoteViewOnly)
            return Refuse(Armed(spec, "would change the show, and remote clients only read while the show is armed",
                "The desk can SHOW DISARM, or SHOW SET REMOTE-VIEW-ONLY OFF"));
        return role switch
        {
            ShowRole.System => Allowed,
            ShowRole.Peer => Peer(spec, cls, in facts),
            ShowRole.Assistant => Assistant(spec, cls),
            ShowRole.Operator => Operator(spec, line, cls),
            ShowRole.Approved => Approved(spec, line, cls, facts),
            _ => Remote(spec, line, cls, facts),
        };
    }

    private static readonly ShowDecision Draft = new(ShowVerdict.BlindOnly, null);

    /// <summary>A line whose edits a blind draft takes: content, the running order, mapping, layout, media, and the draft's own
    /// UNDO (UNDO TO is UNDO taken further) and REDO.</summary>
    public static bool Draftable(VerbSpec spec, ShowClass cls)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return cls is ShowClass.Content or ShowClass.Playlists or ShowClass.Mapping or ShowClass.Layout or ShowClass.Media
            || (cls == ShowClass.Project && (IsUndo(spec) || Is(spec, "REDO")));
    }

    /// <summary>UNDO, or UNDO TO: a run of steps taken back as one action, which the show treats exactly as UNDO.</summary>
    private static bool IsUndo(VerbSpec spec) => Is(spec, "UNDO") || Is(spec, "UNDO TO");

    /// <summary>True for PANIC itself: the stop, never refused for a person.</summary>
    private static bool IsStop(VerbSpec spec) => string.Equals(spec.Name, "PANIC", StringComparison.Ordinal);

    /// <summary>A fleet peer runs the group's air and the show's group lines, never heavy or set-up work; a show whose DISARM-FROM is
    /// the desk keeps DISARM and UNLOCK to its own desk, whichever node's line asks.</summary>
    private static ShowDecision Peer(VerbSpec spec, ShowClass cls, in ShowFacts facts) => cls switch
    {
        ShowClass.Heavy or ShowClass.Setup or ShowClass.DevicePower or ShowClass.Project or ShowClass.Admin =>
            Refuse(Armed(spec, What(cls), "SHOW DISARM on this node first")),
        ShowClass.Show when (Is(spec, "SHOW DISARM") || Is(spec, "UNLOCK")) && string.Equals(facts.DisarmFrom, "desk", StringComparison.OrdinalIgnoreCase) =>
            Refuse(Armed(spec, "is this node's own desk's on this show (DISARM-FROM DESK)", "Its desk can do it")),
        _ => Allowed,
    };

    private static ShowDecision Assistant(VerbSpec spec, ShowClass cls) => cls switch
    {
        ShowClass.Admin => Refuse(string.Concat(spec.Name, " touches this machine's secrets: an assistant never runs it")),
        ShowClass.Heavy or ShowClass.Setup => Refuse(Armed(spec, What(cls), "SHOW DISARM first; then propose it")),
        // An assistant drafts blind while armed (its edits stay off the air); landing the draft is a person's (refused above).
        ShowClass.Show when spec.Name.StartsWith("BLIND", StringComparison.Ordinal) => Allowed,
        _ => new ShowDecision(ShowVerdict.Propose, string.Concat(spec.Name,
            " waits for a person while the show is armed: an assistant proposes, a person approves or runs it")),
    };

    private static ShowDecision Operator(VerbSpec spec, WireLine line, ShowClass cls)
    {
        switch (cls)
        {
            case ShowClass.Transport or ShowClass.Air or ShowClass.Panic or ShowClass.Perform or ShowClass.Content or ShowClass.Show
                or ShowClass.DeviceSafe or ShowClass.Admin or ShowClass.Assistant:
                return Allowed;
            case ShowClass.Playlists:
                return Is(spec, "PLAYLIST ADD") ? Allowed : Refuse(Armed(spec, What(cls), "SHOW DISARM first (appending with PLAYLIST ADD stays live)"));
            case ShowClass.Media:
                return Is(spec, "MEDIA IMPORT") || Is(spec, "MEDIA FETCH") ? Allowed : Refuse(Armed(spec, What(cls), "SHOW DISARM first"));
            case ShowClass.DevicePower:
                return !HasWord(line, spec, "OFF") || HasWord(line, spec, "CONFIRM") ? Allowed
                    : Refuse(Armed(spec, "puts a projector into standby", "Add CONFIRM to the line to do it anyway"));
            case ShowClass.Project:
                return Is(spec, "PROJECT SAVE") || IsUndo(spec) || Is(spec, "REDO") ? Allowed
                    : Refuse(Armed(spec, What(cls), "SHOW DISARM first"));
            case ShowClass.Fleet:
                return !Is(spec, "FAILBACK") || HasWord(line, spec, "CONFIRM") ? Allowed
                    : Refuse(Armed(spec, "hands the show back to the primary", "Add CONFIRM to the line to do it anyway"));
            default:
                return Refuse(Armed(spec, What(cls), "SHOW DISARM on the desk first"));
        }
    }

    private static ShowDecision Remote(VerbSpec spec, WireLine line, ShowClass cls, in ShowFacts facts)
    {
        const string Desk = "The desk can SHOW DISARM";
        switch (cls)
        {
            case ShowClass.Transport or ShowClass.Air or ShowClass.Panic or ShowClass.Perform or ShowClass.Assistant:
                return Allowed;
            case ShowClass.Show:
                if (Is(spec, "SHOW ARM") || Is(spec, "LOCK") || Is(spec, "PREFLIGHT"))
                    return Allowed;
                if (Is(spec, "SHOW DISARM") || Is(spec, "UNLOCK"))
                    return string.Equals(facts.DisarmFrom, "desk", StringComparison.OrdinalIgnoreCase)
                        ? Refuse(Armed(spec, "is the desk's on this show (DISARM-FROM DESK)", "The desk can do it"))
                        : Allowed;
                return Refuse(Armed(spec, string.Concat(What(cls), ", and this client may only run the show while it is armed"), Desk));
            case ShowClass.Media when Is(spec, "MEDIA FETCH"):
                return Allowed;
            case ShowClass.DeviceSafe when Is(spec, "DEVICE SHUTTER") && HasWord(line, spec, "CLOSE"):
                return Allowed;
            case ShowClass.Fleet when Is(spec, "FAILOVER"):
                return Allowed;
            default:
                return Refuse(Armed(spec, string.Concat(What(cls), ", and this client may only run the show while it is armed"), Desk));
        }
    }

    /// <summary>A person's approval of an assistant's line gets what a remote client gets, never more: no Show Mode words, no
    /// media, no projector commands, no moving the show between machines, and the air only towards dark.</summary>
    private static ShowDecision Approved(VerbSpec spec, WireLine line, ShowClass cls, in ShowFacts facts)
    {
        const string WayOut = "Run it from the desk, or SHOW DISARM first";
        switch (cls)
        {
            case ShowClass.Air when !TowardsDark(spec, line, facts.AirDark):
                return Refuse(Armed(spec, "brings the air up, and an approved assistant's line only takes it towards dark", WayOut));
            case ShowClass.Show or ShowClass.Media or ShowClass.DeviceSafe or ShowClass.Fleet or ShowClass.Admin or ShowClass.Assistant:
                return Refuse(Armed(spec, string.Concat(What(cls), ", and an approved assistant's line may only run the show"), WayOut));
            default:
                var asRemote = Remote(spec, line, cls, facts);
                return asRemote.Verdict == ShowVerdict.Allow ? asRemote
                    : Refuse(Armed(spec, string.Concat(What(cls), ", and an approved assistant's line may only run the show"), WayOut));
        }
    }

    /// <summary>Whether an air line takes the air towards dark (BLACKOUT ON, a TOGGLE while lit, FREEZE ON, MASTER 0); any other
    /// air line counts as towards light, the cautious side.</summary>
    public static bool TowardsDark(VerbSpec spec, WireLine line, bool airDark)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        var arg = line.Word(VerbWords(spec));
        if (Is(spec, "BLACKOUT"))
            return arg.Equals("ON", StringComparison.OrdinalIgnoreCase) || (arg.Equals("TOGGLE", StringComparison.OrdinalIgnoreCase) && !airDark);
        if (Is(spec, "FREEZE"))
            return arg.Equals("ON", StringComparison.OrdinalIgnoreCase);
        if (Is(spec, "MASTER"))
            return arg is "0" or "0.0";
        return false;
    }

    private static string What(ShowClass cls) => cls switch
    {
        ShowClass.Content => "changes what is on the air",
        ShowClass.Playlists => "changes the running order",
        ShowClass.Mapping => "moves the mapping on the air",
        ShowClass.Layout => "changes the outputs on the air",
        ShowClass.Media => "changes the show's media",
        ShowClass.Heavy => "is heavy work (structured light, solves, measuring) that can disturb the air",
        ShowClass.Setup => "puts set-up pictures or settings on the air",
        ShowClass.DeviceSafe => "commands a projector",
        ShowClass.DevicePower => "powers a projector",
        ShowClass.Project => "replaces or rewinds the project",
        ShowClass.Network => "changes the node's links",
        ShowClass.Fleet => "moves the show between machines",
        ShowClass.Admin => "touches this machine's secrets",
        ShowClass.Show => "changes Show Mode",
        ShowClass.Air => "changes the air",
        ShowClass.Assistant => "speaks for the assistant",
        _ => "changes the show",
    };

    private static string Armed(VerbSpec spec, string what, string wayOut) => string.Concat("Not while armed: ", spec.Name, " ", what, ". ", wayOut, ".");

    private static ShowDecision Refuse(string words) => new(ShowVerdict.Refuse, words);

    private static bool Is(VerbSpec spec, string name) => string.Equals(spec.Name, name, StringComparison.Ordinal);

    /// <summary>True when a word after the verb's own equals <paramref name="word"/> (ignoring case).</summary>
    private static bool HasWord(WireLine line, VerbSpec spec, string word)
    {
        for (var i = VerbWords(spec); i < line.Count; i++)
        {
            if (line.Word(i).Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>How many words the verb's own name takes (every alias names as many).</summary>
    private static int VerbWords(VerbSpec spec)
    {
        var n = 1;
        foreach (var c in spec.Name)
        {
            if (c == ' ')
                n++;
        }
        return n;
    }
}
