using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The router: a line's verb to its handler. Runs on the control lane. Every handler answers what it
/// did or why it did nothing; nothing is defaulted, an unknown word is refused.</summary>
public sealed partial class EngineCore
{
    /// <summary>Patterns' Interactive fact keys (src/Patterns.Core/Services/Interactive.cs): a Patterns desk
    /// driving this node as a "Lines" device sends them on change and all of them on connect.</summary>
    private static readonly HashSet<string> PatternsFacts = new(StringComparer.Ordinal)
    {
        "BLACKOUT", "LIVE", "DUCK", "FROZEN", "REVIEW", "LOOK", "PROGRAM", "STINGER", "LOWERTHIRD",
        "VOL", "MUSICVOL", "LOOKEDITED", "ARMED", "HOLD", "CUE", "DECK", "STEP",
    };

    private CommandReply Route(WireLine line, SessionInfo session, CancellationToken ct)
    {
        if (TryFact(line, out var fact))
            return fact;
        var match = VerbCatalog.Match(line);
        if (match is null)
            return CommandReply.Fail($"unknown verb '{line.Word(0)}' — HELP lists the vocabulary");
        var (spec, used) = match.Value;
        if (spec.Mutates && !session.Paired)
            return CommandReply.Fail("not paired — this node asks for its pairing token first: AUTH-PROOF on the wire, X-PlayMap-Token on the web");
        var args = line.Words.Skip(used).ToList();
        return spec.Name switch
        {
            "STATE" => CommandReply.Success(State().Json),
            "CONTEXT" => CommandReply.Success(Context()),
            "HELP" => Help(args),
            "VERBS" => CommandReply.Success(VerbsJson()),
            "HELLO" or "NONCE" or "AUTH" or "AUTH-PROOF" or "PING" or "SUBSCRIBE" =>
                CommandReply.Fail($"{spec.Name} is the connection's own word — the transport answers it"),

            "VIDEO PLAY" or "VIDEO PAUSE" or "VIDEO STOP" or "VIDEO RESTART" or "VIDEO SEEK" or "VIDEO END" or "VIDEO RATE" => Video(spec.Name, args),

            "PLAYLIST LIST" => CommandReply.Success(PlaylistsJson()),
            "PLAYLIST GET" => PlaylistGet(args),
            "PLAYLIST NEW" => PlaylistNew(args),
            "PLAYLIST ADD" => PlaylistAdd(args),
            "PLAYLIST REMOVE" => PlaylistRemove(args),
            "PLAYLIST MOVE" => PlaylistMove(args),
            "PLAYLIST DELETE" => PlaylistDelete(args),
            "PLAYLIST GO" => PlaylistGo(args),
            "PLAYLIST CUE" => PlaylistCue(args),
            "PLAYLIST NEXT" => PlaylistStep(args, 1),
            "PLAYLIST PREV" => PlaylistStep(args, -1),
            "PLAYLIST LOOP" => PlaylistLoop(args),
            "PLAYLIST SYNC" => PlaylistSync(args),
            "PLAYLIST FOCUS" => PlaylistFocus(args),
            "PLAYLIST ANCHOR" => PlaylistAnchor(args, line),

            "MEDIA LIST" => CommandReply.Success(MediaListJson()),
            "MEDIA GET" => EntityGet(args),
            "MEDIA IMPORT" => MediaImport(args, line, ct),
            "MEDIA REMOVE" => Remove(args),
            "MEDIA HAS" => MediaHas(args),
            "MEDIA FETCH" => MediaFetch(args, ct),
            "MEDIA STATUS" => CommandReply.Success(MediaStatusJson()),

            "PATTERN" => Pattern(args),
            "GENERATOR KINDS" => CommandReply.Success(GeneratorKindsJson()),
            "GENERATOR NEW" => GeneratorNew(args),
            "GENERATOR SET" => GeneratorSet(args),

            "OUTPUT LIST" => CommandReply.Success(OutputsJson()),
            "DISPLAYS" => CommandReply.Success(DisplaysJson()),
            "OUTPUT ENABLE" => OutputEnable(args),
            "OUTPUT SOURCE" => OutputSource(args),
            "OUTPUT IDENTIFY" => Identify(args),
            "BLACKOUT" => Blackout(args),
            "FREEZE" => Freeze(args),

            "SURFACE SOURCE" => SurfaceSource(args),
            "SURFACE SHOW" => SurfaceShow(args),
            "PROJECTOR SOLVE" => ProjectorSolve(args),
            "BLEND SOLVE" => BlendSolve(args),
            "WARP RESET" => WarpReset(args),
            "WARP NODE" => WarpNode(args),

            "SCAN START" => ScanStart(args, ct),
            "SCAN STATUS" => CommandReply.Success(ScanStatusJson()),
            "SCAN CANCEL" => Services.Scan.Cancel() ? CommandReply.Success(effect: "scan cancelled") : CommandReply.Fail("no scan is running"),
            "SCAN APPLY" => ScanApply(args),
            "LATENCY MEASURE" => CommandReply.Fail("latency measurement needs a camera on this node — none is connected"),

            "GET" => EntityGet(args),
            "SET" => Set(args, line),
            "ADD" => Add(args, line),
            "REMOVE" => Remove(args),
            "MOVE" => Move(args),
            "UNDO" => FromEdit(Store.Undo()),
            "REDO" => FromEdit(Store.Redo()),
            "PROJECT GET" => CommandReply.Success(ProjectJson().Json),
            "PROJECT SAVE" => ProjectSave(args),
            "PROJECT OPEN" => ProjectOpen(args),
            "PROJECT NEW" => ProjectNew(args),

            "NODES" => CommandReply.Success(NodesJson()),
            "SYNC STATUS" => CommandReply.Success(SyncJson()),
            "SYNC LEAD" => NetworkAction(args, 1, "SYNC LEAD <group>", (nc, a) => nc.LeadAsync(a[0], ct)),
            "SYNC FOLLOW" => NetworkAction(args, 2, "SYNC FOLLOW <group> <node>", (nc, a) => nc.FollowAsync(a[0], a[1], ct)),
            "LINK PATTERNS" when args.Count > 0 && args[0].Equals("FOLLOW-BLACKOUT", StringComparison.OrdinalIgnoreCase) => FollowPatternsBlackout(args),
            "LINK PATTERNS" => NetworkAction(args, 1, "LINK PATTERNS <host[:port]>|OFF [<token>]",
                (nc, a) => nc.LinkPatternsAsync(IsOff(a[0]) ? null : a[0], a.Count > 1 ? a[1] : null, ct)),
            "LINK STAGEWATCH" => NetworkAction(args, 1, "LINK STAGEWATCH <host[:port]>|OFF [<key>]",
                (nc, a) => nc.LinkStagewatchAsync(IsOff(a[0]) ? null : a[0], a.Count > 1 ? a[1] : null, ct)),
            "PAIRING NEW" => NewPairing(),

            _ => CommandReply.Fail($"{spec.Name} is not available on this node yet"),
        };
    }

    /// <summary>A Patterns fact line ("BLACKOUT 1", "LIVE 0", "DECK 3 12"): taken in, never answered. Told
    /// apart from PlayMap's own verbs by its value — PlayMap's BLACKOUT takes ON/OFF/TOGGLE, a fact 0/1.</summary>
    private bool TryFact(WireLine line, out CommandReply reply)
    {
        reply = CommandReply.NoReply();
        if (line.Count < 2 || !PatternsFacts.Contains(line.Verb))
            return false;
        var value = line.Word(1);
        var numeric = value.All(c => char.IsAsciiDigit(c) || c == '.' || c == '-');
        var isFact = line.Verb switch
        {
            "BLACKOUT" or "FROZEN" or "LIVE" or "REVIEW" or "DUCK" or "LOOKEDITED" or "ARMED" or "HOLD" => numeric && line.Count == 2,
            "DECK" or "STEP" => numeric,
            "VOL" or "MUSICVOL" => numeric && line.Count == 2,
            _ => true,
        };
        if (!isFact)
            return false;
        if (line.Verb == "BLACKOUT")
        {
            var on = value != "0";
            SetAir(a => a with { PatternsBlackout = on });
            reply = CommandReply.NoReply(on ? "Patterns blacked out" : "Patterns out of blackout");
        }
        return true;
    }

    private CommandReply Help(List<string> args)
    {
        if (args.Count == 0)
        {
            var sb = new StringBuilder();
            foreach (var group in VerbCatalog.All.GroupBy(v => v.Category))
                sb.Append(group.Key).Append(": ").Append(string.Join(", ", group.Select(v => v.Name))).Append(". ");
            return CommandReply.Success(sb.ToString().Trim());
        }
        var spec = VerbCatalog.Find(string.Join(' ', args));
        return spec is null
            ? CommandReply.Fail($"no verb '{string.Join(' ', args)}'")
            : CommandReply.Success($"{spec.Syntax} — {spec.Words} (risk {spec.Risk.ToString().ToLowerInvariant()})");
    }

    private static string VerbsJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var v in VerbCatalog.All)
        {
            w.WriteStartObject();
            w.WriteString("name", v.Name);
            w.WriteString("syntax", v.Syntax);
            w.WriteString("words", v.Words);
            w.WriteString("category", v.Category.ToString().ToLowerInvariant());
            w.WriteString("risk", v.Risk.ToString().ToLowerInvariant());
            w.WriteBoolean("mutates", v.Mutates);
            w.WriteStartArray("aliases");
            foreach (var a in v.Aliases)
                w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private CommandReply NetworkAction(List<string> args, int needed, string syntax, Func<INetworkControl, List<string>, Task<string>> action)
    {
        if (args.Count < needed)
            return CommandReply.Fail(syntax);
        if (Services.NetworkControl is not { } nc)
            return CommandReply.Fail("the network is not running on this node");
        var task = action(nc, args);
        _ = task.ContinueWith(t => Journal.Add(t.IsCompletedSuccessfully ? "network" : "refused",
                t.IsCompletedSuccessfully ? t.Result : t.Exception?.InnerException?.Message ?? "failed", ok: t.IsCompletedSuccessfully),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return CommandReply.Accepted($"{syntax.Split(' ')[0]} {syntax.Split(' ')[1]} started");
    }

    private static CommandReply FromEdit(EditResult r) =>
        r.Changed ? CommandReply.Success(effect: r.Effect) : CommandReply.Fail(r.Effect);

    private static bool IsOff(string s) => s.Equals("OFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>"ON pl1a2b3c4" anywhere in the words: the target, removed from the list.</summary>
    private static string? TakeOn(List<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                var target = args[i + 1];
                args.RemoveRange(i, 2);
                return target;
            }
        }
        return null;
    }

    /// <summary>"AT +250" (ms from now) or "AT 1760101234567" (Unix ms on this node's show clock): the
    /// leader instant, removed from the list. Absent: now. Null with a reason when malformed.</summary>
    private (long? LeaderNs, string? Error) TakeAt(List<string> args)
    {
        var now = LeaderNowNs;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!args[i].Equals("AT", StringComparison.OrdinalIgnoreCase))
                continue;
            var v = args[i + 1];
            args.RemoveRange(i, 2);
            if (v.StartsWith('+') && double.TryParse(v.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms is >= 0 and <= 86_400_000)
                return (now + (long)(ms * 1e6), null);
            if (long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            {
                var delta = unix - Services.Wall.UnixMs;
                if (delta is < -60_000 or > 86_400_000)
                    return (null, "AT is a Unix ms within the next day (or +ms)");
                return (now + Math.Max(0, delta) * 1_000_000, null);
            }
            return (null, "AT takes +<ms> or a Unix ms");
        }
        return (now, null);
    }

    internal static string JsonOut(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
            write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private Playlist? ResolvePlaylist(string? idOrName)
    {
        var p = Store.Project;
        if (idOrName is null)
            return p.FindPlaylist(Playback.FocusedId);
        return p.FindPlaylist(idOrName)
            ?? p.Playlists.FirstOrDefault(l => l.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }
}
