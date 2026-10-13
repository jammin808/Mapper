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
        V("AUTH", "AUTH <token>", "Pairs with the token in clear (this machine only)", VerbCategory.Session, VerbRisk.Low, false),
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
        V("PLAYLIST ADD", "PLAYLIST ADD <playlist> <source> [<label>]", "Adds an item (media:…, generator:…, color:#…); answers its id", VerbCategory.Playlist, VerbRisk.Low, true),
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
        V("VIEW", "VIEW <view> SHOW|CAMERA|HIDE [<what> [<w>x<h>]|<camera json>]",
            "A preview into the view's shared-memory ring on this machine: SHOW editor, output:<id>, projector:<id>, camera:<id> or scan:<id>:smart|confidence; CAMERA moves an editor view's eye; HIDE lets it go",
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
        V("LATENCY MEASURE", "LATENCY MEASURE <camera> [<output>]", "Flashes white and times the camera's view of it", VerbCategory.Scan, VerbRisk.High, true),

        // Generic project edits — what the desk itself uses, so nothing the desk can do is closed to Patterns or an AI.
        V("GET", "GET <id>", "Any entity as JSON", VerbCategory.Project, VerbRisk.Low, false),
        V("SET", "SET <id> <field.path> <json>", "Sets one field of an entity", VerbCategory.Project, VerbRisk.Review, true),
        V("ADD", "ADD <media|playlist|item|generator|canvas|layer|surface|mesh|projector|output|mask|pin|camera|viewpoint> [<owner>] <json>", "Adds an entity; answers its id (an item, layer, mask or pin names its owner first or as \"parent\" in the JSON)", VerbCategory.Project, VerbRisk.Review, true),
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
        V("LINK PATTERNS", "LINK PATTERNS <host[:port]>|OFF [<token>] | LINK PATTERNS FOLLOW-BLACKOUT ON|OFF", "Follows a Patterns desk (its clip clock, its blackout); FOLLOW-BLACKOUT says whether the desk's blackout blacks this node out", VerbCategory.Network, VerbRisk.Review, true),
        V("LINK STAGEWATCH", "LINK STAGEWATCH <host[:port]>|\"\"|OFF [<key>]", "Follows a Stagewatch (its timer as a source); \"\" follows the first one heard", VerbCategory.Network, VerbRisk.Review, true),
        V("PAIRING NEW", "PAIRING NEW", "A new pairing token for this node: every paired peer pairs again (the token never rides a line — it is read where the node keeps it)", VerbCategory.Network, VerbRisk.High, true),
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
