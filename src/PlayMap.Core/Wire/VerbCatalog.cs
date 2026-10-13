using System.Collections.Frozen;

namespace PlayMap.Core.Wire;

/// <summary>How much a verb can disturb a show (Show Bus §6/§11): low runs on a word, review asks a human
/// to look, high is never pre-ticked by any assistant.</summary>
public enum VerbRisk
{
    Low,
    Review,
    High,
}

public enum VerbCategory
{
    Session,
    Transport,
    Playlist,
    Media,
    Content,
    Output,
    Mapping,
    Scan,
    Project,
    Network,
    Device,
    Assistant,
}

/// <summary>One verb of PlayMap's vocabulary. The same table feeds the wire's parser and HELP, the HTTP
/// API, the MCP tools, the context's command list (what an AI may propose, with its risk) and the papers.
/// <see cref="Mutates"/> verbs need pairing on the wire; queries are open (Patterns' rule).</summary>
public sealed record VerbSpec(
    string Name,
    string Syntax,
    string Words,
    VerbCategory Category,
    VerbRisk Risk,
    bool Mutates,
    IReadOnlyList<string> Aliases)
{
    public int WordCount => Name.Count(c => c == ' ') + 1;

    /// <summary>The word index from which a line of this verb carries a secret (a token, a key, a password),
    /// or null. Everything from there on is kept out of journals, logs and records.</summary>
    public int? SecretFrom { get; init; }

    /// <summary>First words after the verb that make a line of it high risk whatever <see cref="Risk"/> says (an
    /// ASSISTANT SET of "enabled" or "provider"); compared upper-cased. Empty: the verb's risk is every line's.</summary>
    public IReadOnlyList<string> HighFor { get; init; } = [];
}

/// <summary>The vocabulary. Patterns' clip verbs (VIDEO, with VT and CLIP as aliases) mean exactly what they
/// mean on a Patterns desk, so Stagewatch's VT link and a Patterns cue drive PlayMap unchanged.</summary>
public static class VerbCatalog
{
    private static VerbSpec V(string name, string syntax, string words, VerbCategory cat, VerbRisk risk, bool mutates, params string[] aliases) =>
        new(name, syntax, words, cat, risk, mutates, aliases);

    public static readonly IReadOnlyList<VerbSpec> All =
    [
        // Session — open on the wire.
        V("HELLO", "HELLO <name>[ module=<version>]", "Says who is talking (used in the pairing proof)", VerbCategory.Session, VerbRisk.Low, false),
        V("NONCE", "NONCE", "A fresh pairing challenge", VerbCategory.Session, VerbRisk.Low, false),
        V("AUTH", "AUTH <token>", "Pairs with the token in clear (this machine only)", VerbCategory.Session, VerbRisk.Low, false) with { SecretFrom = 1 },
        V("AUTH-PROOF", "AUTH-PROOF <hex>", "Pairs by proof: HMAC-SHA256(token, nonce + name)", VerbCategory.Session, VerbRisk.Low, false),
        V("PING", "PING [<t1 unix ms>]", "Liveness and the clock exchange: OK PONG t1 t2 t3", VerbCategory.Session, VerbRisk.Low, false),
        V("STATE", "STATE", "The whole node as JSON (also pushed as STATE {…} on change)", VerbCategory.Session, VerbRisk.Low, false, "STATUS"),
        V("CONTEXT", "CONTEXT", "Show Bus §6 context: light, headline, items, commands, peers", VerbCategory.Session, VerbRisk.Low, false),
        V("HELP", "HELP [<verb>]", "The vocabulary, or one verb's syntax", VerbCategory.Session, VerbRisk.Low, false),
        V("VERBS", "VERBS", "The vocabulary as JSON (name, syntax, words, risk, mutates)", VerbCategory.Session, VerbRisk.Low, false),
        V("SUBSCRIBE", "SUBSCRIBE STATE|CONTEXT|PROJECT|OFF", "Pushes STATE (and CONTEXT or PROJECT) lines on change, latest wins", VerbCategory.Session, VerbRisk.Low, false),

        // Transport — Patterns' clip verbs, on the focused playlist (or ON <playlist>).
        V("VIDEO PLAY", "VIDEO PLAY [ON <playlist>] [AT +<ms>|<unix ms>]", "Plays on from where the clip holds", VerbCategory.Transport, VerbRisk.High, true, "VIDEO RESUME"),
        V("VIDEO PAUSE", "VIDEO PAUSE [ON <playlist>]", "Holds the clip where it is", VerbCategory.Transport, VerbRisk.High, true, "VIDEO HOLD"),
        V("VIDEO STOP", "VIDEO STOP [ON <playlist>]", "Back to the top and held (bare STOP is never a clip verb)", VerbCategory.Transport, VerbRisk.High, true),
        V("VIDEO RESTART", "VIDEO RESTART [ON <playlist>] [AT …]", "Plays from the top", VerbCategory.Transport, VerbRisk.High, true, "VIDEO TOP", "VIDEO START", "VIDEO BEGIN", "VIDEO REWIND"),
        V("VIDEO SEEK", "VIDEO SEEK <83|83.5|2.5s|1:23|1:02:03|top|END [s]> [ON <playlist>]", "Moves to a point, keeping play or hold", VerbCategory.Transport, VerbRisk.High, true, "VIDEO GOTO", "VIDEO AT"),
        V("VIDEO END", "VIDEO END [<seconds 0-3600, default 10>] [ON <playlist>]", "Moves to n seconds before the end", VerbCategory.Transport, VerbRisk.High, true, "VIDEO LAST", "VIDEO OUT", "VIDEO TAIL"),
        V("VIDEO RATE", "VIDEO RATE <0.05-20> [ON <playlist>]", "Playback speed", VerbCategory.Transport, VerbRisk.High, true),

        // Playlists.
        V("PLAYLIST LIST", "PLAYLIST LIST", "Every playlist with its transport", VerbCategory.Playlist, VerbRisk.Low, false),
        V("PLAYLIST GET", "PLAYLIST GET <playlist>", "One playlist, its items and transport", VerbCategory.Playlist, VerbRisk.Low, false),
        V("PLAYLIST NEW", "PLAYLIST NEW <name>", "Makes an empty playlist; answers its id", VerbCategory.Playlist, VerbRisk.Low, true),
        V("PLAYLIST ADD", "PLAYLIST ADD <playlist> <source> [<label>]", "Adds an item (media:…, generator:…, color:#…, playlist:… — another playlist's picture, never one that plays this one); answers its id", VerbCategory.Playlist, VerbRisk.Low, true),
        V("PLAYLIST REMOVE", "PLAYLIST REMOVE <playlist> <item>", "Removes an item", VerbCategory.Playlist, VerbRisk.Review, true),
        V("PLAYLIST MOVE", "PLAYLIST MOVE <playlist> <item> <index>", "Reorders an item", VerbCategory.Playlist, VerbRisk.Low, true),
        V("PLAYLIST DELETE", "PLAYLIST DELETE <playlist>", "Deletes a playlist", VerbCategory.Playlist, VerbRisk.Review, true),
        V("PLAYLIST GO", "PLAYLIST GO <playlist> [<item>|#<n>] [AT +<ms>|<unix ms>]", "Cues and plays an item (the next one when none is named)", VerbCategory.Playlist, VerbRisk.High, true),
        V("PLAYLIST CUE", "PLAYLIST CUE <playlist> <item>|#<n>", "Loads an item and holds its first frame", VerbCategory.Playlist, VerbRisk.Review, true),
        V("PLAYLIST NEXT", "PLAYLIST NEXT <playlist>", "Plays the next item", VerbCategory.Playlist, VerbRisk.High, true),
        V("PLAYLIST PREV", "PLAYLIST PREV <playlist>", "Plays the previous item", VerbCategory.Playlist, VerbRisk.High, true),
        V("PLAYLIST LOOP", "PLAYLIST LOOP <playlist> ON|OFF", "Whether the list wraps to the top", VerbCategory.Playlist, VerbRisk.Low, true),
        V("PLAYLIST SYNC", "PLAYLIST SYNC <playlist> <group>|OFF", "Joins a sync group: the transport follows the group's leader", VerbCategory.Playlist, VerbRisk.Review, true),
        V("PLAYLIST FOCUS", "PLAYLIST FOCUS <playlist>", "The playlist the VIDEO verbs act on", VerbCategory.Playlist, VerbRisk.Low, true),
        V("PLAYLIST ANCHOR", "PLAYLIST ANCHOR <playlist> <json>", "A sync group leader's transport, taken exactly (leader to followers)", VerbCategory.Playlist, VerbRisk.High, true),

        // Media.
        V("MEDIA LIST", "MEDIA LIST", "The library with each item's availability", VerbCategory.Media, VerbRisk.Low, false),
        V("MEDIA GET", "MEDIA GET <media>", "One item, its probe and where it is", VerbCategory.Media, VerbRisk.Low, false),
        V("MEDIA IMPORT", "MEDIA IMPORT <path|url> [<name>]", "Adds a file or URL; hashes and probes it; answers its id", VerbCategory.Media, VerbRisk.Low, true),
        V("MEDIA REMOVE", "MEDIA REMOVE <media>", "Removes an item from the library (the file stays)", VerbCategory.Media, VerbRisk.Review, true),
        V("MEDIA HAS", "MEDIA HAS <sha256:…>", "Whether this node holds a file with that hash", VerbCategory.Media, VerbRisk.Low, false),
        V("MEDIA FETCH", "MEDIA FETCH <media>|ALL [FROM <node>]", "Copies missing files from a peer that has them", VerbCategory.Media, VerbRisk.Low, true),
        V("MEDIA STATUS", "MEDIA STATUS", "Transfers, readiness and disk", VerbCategory.Media, VerbRisk.Low, false),

        // Content: generators and test patterns.
        V("PATTERN", "PATTERN <kind>|OFF [ON <output>|ALL]", "Puts a test pattern over the outputs (OFF returns them to the show)", VerbCategory.Content, VerbRisk.High, true),
        V("GENERATOR KINDS", "GENERATOR KINDS", "Every generator kind with its parameters", VerbCategory.Content, VerbRisk.Low, false),
        V("GENERATOR NEW", "GENERATOR NEW <kind> [<name>]", "Makes a generator; answers its id", VerbCategory.Content, VerbRisk.Low, true),
        V("GENERATOR SET", "GENERATOR SET <generator> <param> <value>", "Sets a parameter", VerbCategory.Content, VerbRisk.Review, true),

        // Outputs.
        V("OUTPUT LIST", "OUTPUT LIST", "Every output, its display and its health", VerbCategory.Output, VerbRisk.Low, false),
        V("DISPLAYS", "DISPLAYS", "The displays this machine sees", VerbCategory.Output, VerbRisk.Low, false),
        V("VIEW", "VIEW <view> SHOW|CAMERA|KEEP|HIDE [<what> [<w>x<h>]|<camera json>]",
            "A preview into the view's shared-memory ring on this machine: SHOW editor, output:<id>, projector:<id>, camera:<id> or scan:<id>:smart|confidence; CAMERA moves an editor view's eye; KEEP renews it (a view not renewed for 15 s is hidden); HIDE lets it go",
            VerbCategory.Output, VerbRisk.Low, false),
        V("OUTPUT ENABLE", "OUTPUT ENABLE <output> ON|OFF", "Opens or closes an output", VerbCategory.Output, VerbRisk.High, true),
        V("OUTPUT SOURCE", "OUTPUT SOURCE <output> <source>", "What a canvas or source output shows", VerbCategory.Output, VerbRisk.High, true),
        V("OUTPUT IDENTIFY", "OUTPUT IDENTIFY [<output>|ALL] [<seconds>]", "Shows each output's name and number over its picture", VerbCategory.Output, VerbRisk.Review, true, "IDENTIFY"),
        V("BLACKOUT", "BLACKOUT ON|OFF|TOGGLE", "Every output to black, and back", VerbCategory.Output, VerbRisk.High, true),
        V("FREEZE", "FREEZE ON|OFF|TOGGLE", "Every output holds its frame", VerbCategory.Output, VerbRisk.High, true),

        // Mapping.
        V("SURFACE SOURCE", "SURFACE SOURCE <surface> <source>", "What a surface shows", VerbCategory.Mapping, VerbRisk.High, true),
        V("SURFACE SHOW", "SURFACE SHOW <surface> ON|OFF", "Shows or hides a surface", VerbCategory.Mapping, VerbRisk.High, true),
        V("PROJECTOR SOLVE", "PROJECTOR SOLVE <projector> [LOCK SHIFT|FOCAL|NONE]", "Solves pose (and lens) from the projector's pins", VerbCategory.Mapping, VerbRisk.Review, true),
        V("BLEND SOLVE", "BLEND SOLVE [<output>|ALL]", "Computes the 3D blend maps from the projectors' coverage", VerbCategory.Mapping, VerbRisk.Review, true),
        V("WARP RESET", "WARP RESET <output>", "Takes an output's 2D warp back to rest", VerbCategory.Mapping, VerbRisk.Review, true),
        V("WARP NODE", "WARP NODE <output> <column> <row> <dx> <dy>", "Moves one lattice node (normalised output units)", VerbCategory.Mapping, VerbRisk.Review, true),

        // Scan.
        V("SCAN START", "SCAN START <camera> [<projector>…] [MODE FAST|ROBUST|DIFFICULT]", "Structured-light scan of the projectors through the camera", VerbCategory.Scan, VerbRisk.High, true),
        V("SCAN STATUS", "SCAN STATUS", "The running scan's progress and quality", VerbCategory.Scan, VerbRisk.Low, false),
        V("SCAN CANCEL", "SCAN CANCEL", "Stops a scan and gives the outputs back", VerbCategory.Scan, VerbRisk.Low, true),
        V("SCAN APPLY", "SCAN APPLY <scan>", "Turns a scan into mesh, projectors and surfaces", VerbCategory.Scan, VerbRisk.Review, true),
        V("SCAN SURFACE", "SCAN SURFACE <scan> <x> <y> [<name>]", "Makes a surface of what is at x, y (0–1 across and down the scan's smart-scan picture), or of a suggested region (REGION <id> in place of x y): its mesh faces and a mask outlining it; answers its id", VerbCategory.Scan, VerbRisk.Review, true),
        V("LATENCY MEASURE", "LATENCY MEASURE <camera> [<output>]", "Flashes white and times the camera's view of it", VerbCategory.Scan, VerbRisk.High, true),

        // Generic project edits — what the desk itself uses, so nothing the desk can do is closed to Patterns or an AI.
        V("GET", "GET <id>", "Any entity as JSON", VerbCategory.Project, VerbRisk.Low, false),
        V("SET", "SET <id> <field.path> <json>", "Sets one field of an entity", VerbCategory.Project, VerbRisk.Review, true),
        V("ADD", "ADD <media|playlist|item|generator|canvas|layer|surface|mesh|projector|output|mask|pin|camera|viewpoint|device> [<owner>] <json>", "Adds an entity; answers its id (an item, layer, mask or pin names its owner first or as \"parent\" in the JSON)", VerbCategory.Project, VerbRisk.Review, true),
        V("REMOVE", "REMOVE <id>", "Removes an entity", VerbCategory.Project, VerbRisk.Review, true),
        V("MOVE", "MOVE <id> <index>", "Reorders an entity within its list", VerbCategory.Project, VerbRisk.Low, true),
        V("UNDO", "UNDO", "Undoes the last edit", VerbCategory.Project, VerbRisk.Review, true),
        V("REDO", "REDO", "Redoes the last undone edit", VerbCategory.Project, VerbRisk.Review, true),
        V("PROJECT GET", "PROJECT GET", "The whole project as JSON", VerbCategory.Project, VerbRisk.Low, false),
        V("PROJECT SAVE", "PROJECT SAVE [<path>]", "Saves the project", VerbCategory.Project, VerbRisk.Low, true),
        V("PROJECT OPEN", "PROJECT OPEN <path>", "Opens a project (the show changes)", VerbCategory.Project, VerbRisk.High, true),
        V("PROJECT NEW", "PROJECT NEW [<name>]", "A new project (the show changes)", VerbCategory.Project, VerbRisk.High, true),

        // Network and sync.
        V("NODES", "NODES", "The Show Bus peers this node hears", VerbCategory.Network, VerbRisk.Low, false),
        V("SYNC STATUS", "SYNC STATUS", "Clock offset, jitter and the sync groups", VerbCategory.Network, VerbRisk.Low, false),
        V("SYNC LEAD", "SYNC LEAD <group>", "This node leads a sync group", VerbCategory.Network, VerbRisk.Review, true),
        V("SYNC FOLLOW", "SYNC FOLLOW <group> <node>", "This node follows a sync group's leader", VerbCategory.Network, VerbRisk.Review, true),
        V("LINK PATTERNS", "LINK PATTERNS <host[:port]>|OFF [<token>] | LINK PATTERNS FOLLOW-BLACKOUT ON|OFF", "Follows a Patterns desk (its clip clock, its blackout); FOLLOW-BLACKOUT says whether the desk's blackout blacks this node out", VerbCategory.Network, VerbRisk.Review, true) with { SecretFrom = 3 },
        V("LINK STAGEWATCH", "LINK STAGEWATCH <host[:port]>|\"\"|OFF [<key>]", "Follows a Stagewatch (its timer as a source); \"\" follows the first one heard", VerbCategory.Network, VerbRisk.Review, true) with { SecretFrom = 3 },
        V("PAIRING NEW", "PAIRING NEW", "A new pairing token for this node: every paired peer pairs again (the token never rides a line — it is read where the node keeps it)", VerbCategory.Network, VerbRisk.High, true),

        // Projector control (docs/PROJECTORS.md): the boxes behind the outputs. Anything that changes what leaves a
        // lens is high; every answer is the projector's own (refused, unreached, sent, rejected, accepted, observed).
        V("DEVICE LIST", "DEVICE LIST", "Every projector link with what its projector reports: power, shutter, input, hours, faults", VerbCategory.Device, VerbRisk.Low, false),
        V("DEVICE GET", "DEVICE GET <device>", "One link: what its projector reports, what its protocol can do and the plan PREPARE would run", VerbCategory.Device, VerbRisk.Low, false),
        V("DEVICE ADD", "DEVICE ADD <name> <protocol> <host[:port]> [PROJECTOR <projector>] [OUTPUT <output>]", "Links a projector box (pjlink, christie, panasonic, barco-pulse, sony-adcp, epson, nec); answers its id", VerbCategory.Device, VerbRisk.Review, true),
        V("DEVICE REMOVE", "DEVICE REMOVE <device>", "Forgets a projector link (the projector is left as it is)", VerbCategory.Device, VerbRisk.Review, true),
        V("DEVICE POWER", "DEVICE POWER <device> ON|OFF", "Powers a projector on, or to standby", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE SHUTTER", "DEVICE SHUTTER <device> OPEN|CLOSE", "Opens or closes a projector's shutter (AV mute)", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE INPUT", "DEVICE INPUT <device> <input>", "Switches a projector's input, in its protocol's own words (31, HD1…)", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE FREEZE", "DEVICE FREEZE <device> ON|OFF", "Holds a projector's picture", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE PATTERN", "DEVICE PATTERN <device> <pattern>|OFF", "Shows one of the projector's own test patterns, or none", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE LENS", "DEVICE LENS <device> SHIFTH|SHIFTV|ZOOM|FOCUS|MEMORY [TO|BY] <n>", "Drives a lens motor to a raw position or by steps, or recalls a lens memory", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE RAW", "DEVICE RAW <device> <line>", "Sends one line in the projector's own protocol (framing and authentication added)", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE DISCOVER", "DEVICE DISCOVER [SWEEP <address>]", "Searches the network for projectors (the PJLink search; SWEEP also knocks on a /24)", VerbCategory.Device, VerbRisk.Low, true),
        V("DEVICE PREPARE", "DEVICE PREPARE <device>", "Readies a projector for a scan: shutter open, freeze and patterns off, its own geometry off, lens recorded", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE IDENTIFY", "DEVICE IDENTIFY <device> [<seconds>]", "Shows the projector's own identify pattern for a while", VerbCategory.Device, VerbRisk.Review, true),
        // A secret rides in this line, so — like AUTH — it is never journalled, it is taken from this machine only and
        // never from an assistant, and no answer repeats it.
        V("CREDENTIAL SET", "CREDENTIAL SET <ref> [<user>] <secret>", "Keeps a secret in this node's credential store (a projector link's password, the assistant's key); this machine only", VerbCategory.Device, VerbRisk.High, false) with { SecretFrom = 3 },
        V("CREDENTIAL CLEAR", "CREDENTIAL CLEAR <ref>", "Forgets a secret from this node's credential store; this machine only", VerbCategory.Device, VerbRisk.High, false),
        V("CREDENTIAL LIST", "CREDENTIAL LIST", "Which references hold a secret on this node (never the secrets)", VerbCategory.Device, VerbRisk.Low, false),

        // The node's own assistant (Show Bus §6/§11): it reads freely, runs low-risk lines on its word and queues the
        // rest for a person. Approving and rejecting are a person's words, never an assistant's.
        V("ASK", "ASK <words>", "Asks the node's assistant; answers its words and every line it ran or queued", VerbCategory.Assistant, VerbRisk.Review, true),
        V("PROPOSALS", "PROPOSALS [ALL]", "The assistant's lines waiting for a person (ALL: every proposal and how it ended)", VerbCategory.Assistant, VerbRisk.Low, false),
        V("APPROVE", "APPROVE <proposal>", "A person approves a proposal: its line runs through the node's own checks", VerbCategory.Assistant, VerbRisk.High, true),
        V("REJECT", "REJECT <proposal> [<why>]", "A person rejects a proposal: it never runs", VerbCategory.Assistant, VerbRisk.Review, true),
        V("ASSISTANT STATUS", "ASSISTANT STATUS", "The assistant's settings: on or off, provider, model, effort, endpoint, what runs without asking, and whether a key is set (never the key)", VerbCategory.Assistant, VerbRisk.Low, false),
        V("ASSISTANT SET", "ASSISTANT SET enabled|provider|model|effort|baseUrl|runLowWithoutAsking|runReviewWithoutAsking <value>",
            "Changes one of the assistant's settings, kept in the node's playmap.assistant.json and applied at once; from this machine or its paired desk only, never by an assistant. The key goes through CREDENTIAL SET assistant",
            VerbCategory.Assistant, VerbRisk.Review, true) with { HighFor = ["ENABLED", "PROVIDER"] },
    ];

    private static readonly FrozenDictionary<string, VerbSpec> ByName = BuildIndex();

    private static FrozenDictionary<string, VerbSpec> BuildIndex()
    {
        var d = new Dictionary<string, VerbSpec>(StringComparer.Ordinal);
        foreach (var v in All)
        {
            d[v.Name] = v;
            foreach (var a in v.Aliases)
                d[a] = v;
        }
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>What stands in a shown line for a verb's secret words.</summary>
    public const string SecretMarker = "‹secret›";

    /// <summary>The line as it may be written down or shown: a verb's secret words (<see cref="VerbSpec.SecretFrom"/>)
    /// are replaced by one marker, so a journal row, a log, a proposal on a desk or a reply never holds a token, a
    /// key or a password. Only the line itself, kept where it runs, carries them.</summary>
    public static string Redacted(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Match(line) is not { Spec.SecretFrom: int from } || line.Count <= from)
            return line.Raw;
        var kept = line.Words.Take(from).Select(w => w.Length == 0 || w.Contains(' ', StringComparison.Ordinal) || w.Contains('"', StringComparison.Ordinal) ? "\"" + w.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : w);
        return string.Join(' ', kept) + " " + SecretMarker;
    }

    /// <summary>A line's text as it may be shown (<see cref="Redacted(WireLine)"/>); a text that does not read as a
    /// line names no verb, so it has no secret words, and is shown as it came.</summary>
    public static string Redacted(string text) =>
        WireGrammar.TryParse(text ?? "", out var line, out _) ? Redacted(line) : text ?? "";

    /// <summary>A line's risk: its verb's, raised to high by the words that make it so (<see cref="VerbSpec.HighFor"/>);
    /// high for a line no verb names.</summary>
    public static VerbRisk RiskOf(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Match(line) is not { } m)
            return VerbRisk.High;
        var first = line.WordUpper(m.Words);
        foreach (var high in m.Spec.HighFor)
            if (string.Equals(high, first, StringComparison.Ordinal))
                return VerbRisk.High;
        return m.Spec.Risk;
    }

    /// <summary>True when a line carries a verb's secret words (a token, a key, a password).</summary>
    public static bool HoldsSecret(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Match(line) is { Spec.SecretFrom: int from } && line.Count > from;
    }

    /// <summary>True when a line's secret words are the marker a shown copy carries: a copy taken from a desk or a
    /// log, which must never run in place of the line it stands for.</summary>
    public static bool IsShownCopy(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Match(line) is not { Spec.SecretFrom: int from })
            return false;
        for (var i = from; i < line.Count; i++)
            if (string.Equals(line.Word(i), SecretMarker, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The verb a line names: the longest match of its first one or two words (upper-cased), with
    /// how many words it took. VT and CLIP are VIDEO (Patterns' aliases), whatever follows. Null when
    /// nothing matches.</summary>
    public static (VerbSpec Spec, int Words)? Match(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var first = line.WordUpper(0);
        if (first is "VT" or "CLIP")
            first = "VIDEO";
        if (line.Count >= 2 && ByName.TryGetValue(first + " " + line.WordUpper(1), out var two))
            return (two, 2);
        if (line.Count >= 1 && ByName.TryGetValue(first, out var one))
            return (one, 1);
        return null;
    }

    public static VerbSpec? Find(string name) =>
        ByName.TryGetValue(name.Trim().ToUpperInvariant(), out var v) ? v : null;
}
