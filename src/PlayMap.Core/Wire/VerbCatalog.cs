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

    // Round 2, appended: HELP groups by category in this order.
    Show,
    Fleet,
    Video,
    Control,
    Timecode,
    Effects,
    Audio,
    Modulation,
    Led,
    Site,
    Align,
    Calibration,
}

/// <summary>A per-argument risk: when the word at <see cref="Arg"/> (0-based after the verb's own words; −1 = any position)
/// equals <see cref="Word"/> (compared upper-cased), the line's risk is <see cref="Risk"/>. The first match wins. On a row of
/// class Air, Transport, Panic or DevicePower a RiskWhen may only raise the risk.</summary>
public sealed record RiskWhen(int Arg, string Word, VerbRisk Risk);

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

    /// <summary>What the verb does to the show for Show Mode (set by V from the category; overridden with `with`).</summary>
    public ShowClass Show { get; init; }

    /// <summary>Words that change a line's risk (DECODE POLICY … FORCE is high; MEDIA OPTIMISE CANCEL is low). Never lowers a
    /// line that moves the air, a transport, a panic or a projector's power.</summary>
    public IReadOnlyList<RiskWhen> RiskWhen { get; init; } = [];

    /// <summary>1 for the first round's rows, 2 for round 2's (set by V2); with the node's routes it gives VERBS' "available".</summary>
    public int Round { get; init; } = 1;

    /// <summary>The line carries another line (BIND ADD … TO &lt;line&gt;, BIND LEARN &lt;line&gt;): its risk is the higher of the two.</summary>
    public bool EmbedsLine { get; init; }

    /// <summary>Never journalled and never moves STATE's revision (cursors, view focus, selection).</summary>
    public bool Ephemeral { get; init; }

    /// <summary>A drag or a nudge: its owner coalesces the run into one undo step and one journal row.</summary>
    public bool Gesture { get; init; }

    /// <summary>Node to node only (a mirror's lines, a fence): kept out of HELP's list, MCP tools and the context's commands, and
    /// refused unless the session's transport is "fleet" or "system".</summary>
    public bool Internal { get; init; }

    /// <summary>A query that still needs pairing (it opens a stream or reveals session facts): RECORD, PRESENCE, THUMB, SELECT.</summary>
    public bool PairedOnly { get; init; }
}

/// <summary>Whether a row is answered on this node now: every first-round row, and a round-2 row once its owner routed it.
/// The assistant's prompt, MCP's list_verbs and VERBS read it.</summary>
public interface IVerbAvailability
{
    bool IsAvailable(VerbSpec spec);
}

/// <summary>The availability of a node that routes no round-2 row: the first round's rows only.</summary>
public sealed class VerbAvailability : IVerbAvailability
{
    public static readonly VerbAvailability RoundOne = new();

    private VerbAvailability()
    {
    }

    public bool IsAvailable(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Round == 1;
    }
}

/// <summary>The vocabulary. Patterns' clip verbs (VIDEO, with VT and CLIP as aliases) mean exactly what they
/// mean on a Patterns desk, so Stagewatch's VT link and a Patterns cue drive PlayMap unchanged.</summary>
public static class VerbCatalog
{
    /// <summary>A row with the show class its category gives (rows whose class differs say so with `with { Show = … }`).</summary>
    private static VerbSpec V(string name, string syntax, string words, VerbCategory cat, VerbRisk risk, bool mutates, params string[] aliases) =>
        new(name, syntax, words, cat, risk, mutates, aliases) { Show = ShowClasses.Default(cat, mutates) };

    /// <summary>A round-2 row: answered "not available on this node yet" until its owner routes it.</summary>
    private static VerbSpec V2(string name, string syntax, string words, VerbCategory cat, VerbRisk risk, bool mutates, params string[] aliases) =>
        V(name, syntax, words, cat, risk, mutates, aliases) with { Round = 2 };

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
        V("SUBSCRIBE", "SUBSCRIBE STATE|CONTEXT|PROJECT|INDEX|EVENTS|ANCHORS|HEALTH|OFF | SUBSCRIBE THUMBS [<ref>…]", "Pushes STATE (and the other topics) on change, latest wins; EVENTS in order", VerbCategory.Session, VerbRisk.Low, false),

        // Transport — Patterns' clip verbs, on the focused playlist (or ON <playlist>).
        V("VIDEO PLAY", "VIDEO PLAY [ON <playlist>] [AT +<ms>|<unix ms>] [EXPECT <item>|#<n>]", "Plays on from where the clip holds (EXPECT refuses if the item changed under the key)", VerbCategory.Transport, VerbRisk.High, true, "VIDEO RESUME"),
        V("VIDEO PAUSE", "VIDEO PAUSE [ON <playlist>]", "Holds the clip where it is", VerbCategory.Transport, VerbRisk.High, true, "VIDEO HOLD"),
        V("VIDEO STOP", "VIDEO STOP [ON <playlist>]", "Back to the top and held (bare STOP is never a clip verb)", VerbCategory.Transport, VerbRisk.High, true),
        V("VIDEO RESTART", "VIDEO RESTART [ON <playlist>] [AT …] [EXPECT <item>|#<n>]", "Plays from the top", VerbCategory.Transport, VerbRisk.High, true, "VIDEO TOP", "VIDEO START", "VIDEO BEGIN", "VIDEO REWIND"),
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
        V("PLAYLIST GO", "PLAYLIST GO <playlist> [<item>|#<n>] [AT +<ms>|<unix ms>] [EXPECT <item>|#<n>]", "Cues and plays an item (the next one when none is named); EXPECT refuses if the next item is not the one the key showed", VerbCategory.Playlist, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V("PLAYLIST CUE", "PLAYLIST CUE <playlist> <item>|#<n>", "Loads an item and holds its first frame", VerbCategory.Playlist, VerbRisk.Review, true) with { Show = ShowClass.Transport },
        V("PLAYLIST NEXT", "PLAYLIST NEXT <playlist> [EXPECT <item>|#<n>]", "Plays the next item", VerbCategory.Playlist, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V("PLAYLIST PREV", "PLAYLIST PREV <playlist> [EXPECT <item>|#<n>]", "Plays the previous item", VerbCategory.Playlist, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V("PLAYLIST LOOP", "PLAYLIST LOOP <playlist> ON|OFF", "Whether the list wraps to the top", VerbCategory.Playlist, VerbRisk.Low, true),
        V("PLAYLIST SYNC", "PLAYLIST SYNC <playlist> <group>|OFF", "Joins a sync group: the transport follows the group's leader", VerbCategory.Playlist, VerbRisk.Review, true),
        V("PLAYLIST FOCUS", "PLAYLIST FOCUS <playlist>", "The playlist the VIDEO verbs act on", VerbCategory.Playlist, VerbRisk.Low, true) with { Show = ShowClass.Transport },
        V("PLAYLIST ANCHOR", "PLAYLIST ANCHOR <playlist> <json>", "A sync group leader's transport, taken exactly (leader to followers)", VerbCategory.Playlist, VerbRisk.High, true) with { Show = ShowClass.Transport },

        // Media.
        V("MEDIA LIST", "MEDIA LIST", "The library with each item's availability", VerbCategory.Media, VerbRisk.Low, false),
        V("MEDIA GET", "MEDIA GET <media>", "One item, its probe and where it is", VerbCategory.Media, VerbRisk.Low, false),
        V("MEDIA IMPORT", "MEDIA IMPORT <path|url> [<name>]", "Adds a file or URL; hashes and probes it; answers its id", VerbCategory.Media, VerbRisk.Low, true),
        V("MEDIA REMOVE", "MEDIA REMOVE <media>", "Removes an item from the library (the file stays)", VerbCategory.Media, VerbRisk.Review, true),
        V("MEDIA HAS", "MEDIA HAS <sha256:…>", "Whether this node holds a file with that hash", VerbCategory.Media, VerbRisk.Low, false),
        V("MEDIA FETCH", "MEDIA FETCH <media>|ALL [VERSION <version>] [FROM <node>]", "Copies missing files (or one version of a clip) from a peer that has them", VerbCategory.Media, VerbRisk.Low, true),
        V("MEDIA STATUS", "MEDIA STATUS", "Transfers, readiness and disk", VerbCategory.Media, VerbRisk.Low, false),

        // Content: generators and test patterns.
        V("PATTERN", "PATTERN <kind>|OFF [ON <output>|ALL]", "Puts a test pattern over the outputs (OFF returns them to the show)", VerbCategory.Content, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V("GENERATOR KINDS", "GENERATOR KINDS [<family>]", "Every generator kind with its parameters (grouped), traits, inputs and preset count", VerbCategory.Content, VerbRisk.Low, false),
        V("GENERATOR NEW", "GENERATOR NEW <kind> [<name>] [PRESET <preset>]", "Makes a generator, optionally from a look; answers its id", VerbCategory.Content, VerbRisk.Low, true),
        V("GENERATOR SET", "GENERATOR SET <generator> <param> <value> | GENERATOR SET <generator> <param> BY <delta>", "Sets a parameter (BY: a step for encoders)", VerbCategory.Content, VerbRisk.Review, true),

        // Outputs.
        V("OUTPUT LIST", "OUTPUT LIST", "Every output, its display and its health", VerbCategory.Output, VerbRisk.Low, false),
        V("DISPLAYS", "DISPLAYS", "The displays this machine sees", VerbCategory.Output, VerbRisk.Low, false),
        // VIEW: "editor DRAFT" shows the session's blind draft in that view only (wp2-blind).
        V("VIEW", "VIEW <view> SHOW|CAMERA|KEEP|HIDE [<what> [<w>x<h>] [DRAFT]|<camera json>]",
            "A preview into the view's shared-memory ring on this machine: SHOW editor, output:<id>, projector:<id>, camera:<id> or scan:<id>:smart|confidence; DRAFT shows this session's blind draft instead of the air (editor only); CAMERA moves an editor view's eye; KEEP renews it (a view not renewed for 15 s is hidden); HIDE lets it go",
            VerbCategory.Output, VerbRisk.Low, false),
        V("OUTPUT ENABLE", "OUTPUT ENABLE <output> ON|OFF", "Opens or closes an output", VerbCategory.Output, VerbRisk.High, true),
        V("OUTPUT SOURCE", "OUTPUT SOURCE <output> <source>", "What a canvas or source output shows", VerbCategory.Output, VerbRisk.High, true) with { Show = ShowClass.Content },
        V("OUTPUT IDENTIFY", "OUTPUT IDENTIFY [<output>|ALL] [<seconds>]", "Shows each output's name and number over its picture", VerbCategory.Output, VerbRisk.Review, true, "IDENTIFY") with { Show = ShowClass.Setup },
        V("BLACKOUT", "BLACKOUT ON|OFF|TOGGLE", "Every output to black, and back", VerbCategory.Output, VerbRisk.High, true) with { Show = ShowClass.Air },
        V("FREEZE", "FREEZE ON|OFF|TOGGLE", "Every output holds its frame", VerbCategory.Output, VerbRisk.High, true) with { Show = ShowClass.Air },

        // Mapping.
        V("SURFACE SOURCE", "SURFACE SOURCE <surface> <source>", "What a surface shows", VerbCategory.Mapping, VerbRisk.High, true) with { Show = ShowClass.Content },
        V("SURFACE SHOW", "SURFACE SHOW <surface> ON|OFF", "Shows or hides a surface", VerbCategory.Mapping, VerbRisk.High, true),
        V("PROJECTOR SOLVE", "PROJECTOR SOLVE <projector> [LOCK SHIFT|FOCAL|NONE]", "Solves pose (and lens) from the projector's pins", VerbCategory.Mapping, VerbRisk.Review, true),
        V("BLEND SOLVE", "BLEND SOLVE [<output>|ALL]", "Computes the 3D blend maps from the projectors' coverage", VerbCategory.Mapping, VerbRisk.Review, true) with { Show = ShowClass.Heavy },
        V("WARP RESET", "WARP RESET <output>", "Takes an output's 2D warp back to rest", VerbCategory.Mapping, VerbRisk.Review, true),
        V("WARP NODE", "WARP NODE <output> <column> <row> <dx> <dy>", "Moves one lattice node (normalised output units)", VerbCategory.Mapping, VerbRisk.Review, true),

        // Scan.
        // SCAN START: INTO adds a view to an existing scan (wp2-multiscan).
        V("SCAN START", "SCAN START <camera> [<projector>…] [MODE FAST|ROBUST|DIFFICULT] [INTO <scan>]", "Structured-light scan of the projectors through the camera (INTO: another view of the same scan)", VerbCategory.Scan, VerbRisk.High, true),
        V("SCAN STATUS", "SCAN STATUS", "The running scan's progress and quality", VerbCategory.Scan, VerbRisk.Low, false),
        V("SCAN CANCEL", "SCAN CANCEL", "Stops a scan and gives the outputs back", VerbCategory.Scan, VerbRisk.Low, true),
        V("SCAN APPLY", "SCAN APPLY <scan>", "Turns a scan into mesh, projectors and surfaces", VerbCategory.Scan, VerbRisk.Review, true),
        // SCAN SURFACE: ON names the projector whose region it is (wp2-multiscan).
        V("SCAN SURFACE", "SCAN SURFACE <scan> <x> <y> [ON <projector>] [<name>] | SCAN SURFACE <scan> REGION <id> [ON <projector>] [<name>]", "Makes a surface of what is at x, y (0–1 across and down the scan's smart-scan picture), or of a suggested region: its mesh faces and a mask outlining it; ON takes that projector's smart scan; answers its id", VerbCategory.Scan, VerbRisk.Review, true),
        V("LATENCY MEASURE", "LATENCY MEASURE <camera> [<output>]", "Flashes white and times the camera's view of it", VerbCategory.Scan, VerbRisk.High, true) with { Show = ShowClass.Heavy },

        // Generic project edits — what the desk itself uses, so nothing the desk can do is closed to Patterns or an AI.
        V("GET", "GET <id>", "Any entity as JSON", VerbCategory.Project, VerbRisk.Low, false),
        V("SET", "SET <id> <field.path> <json>", "Sets one field of an entity", VerbCategory.Project, VerbRisk.Review, true) with { Show = ShowClass.Target },
        V("ADD", "ADD <media|playlist|item|generator|canvas|layer|surface|mesh|projector|output|mask|pin|camera|viewpoint|device|binding|controlsurface|timecodesource|send|fx|lednode|fixturetype|fixture|controlpoint|stack> [<owner>] <json>", "Adds an entity; answers its id (an item, layer, mask, pin or fx slot names its owner first or as \"parent\" in the JSON)", VerbCategory.Project, VerbRisk.Review, true) with { Show = ShowClass.Target },
        V("REMOVE", "REMOVE <id>", "Removes an entity", VerbCategory.Project, VerbRisk.Review, true) with { Show = ShowClass.Target },
        V("MOVE", "MOVE <id> <index>", "Reorders an entity within its list", VerbCategory.Project, VerbRisk.Low, true) with { Show = ShowClass.Target },
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
        V("DEVICE POWER", "DEVICE POWER <device> ON|OFF [CONFIRM]", "Powers a projector on, or to standby (OFF needs CONFIRM on an armed show)", VerbCategory.Device, VerbRisk.High, true) with { Show = ShowClass.DevicePower },
        V("DEVICE SHUTTER", "DEVICE SHUTTER <device> OPEN|CLOSE", "Opens or closes a projector's shutter (AV mute)", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE INPUT", "DEVICE INPUT <device> <input>", "Switches a projector's input, in its protocol's own words (31, HD1…)", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE FREEZE", "DEVICE FREEZE <device> ON|OFF", "Holds a projector's picture", VerbCategory.Device, VerbRisk.High, true),
        V("DEVICE PATTERN", "DEVICE PATTERN <device> <pattern>|OFF", "Shows one of the projector's own test patterns, or none", VerbCategory.Device, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V("DEVICE LENS", "DEVICE LENS <device> SHIFTH|SHIFTV|ZOOM|FOCUS|MEMORY [TO|BY] <n>", "Drives a lens motor to a raw position or by steps, or recalls a lens memory", VerbCategory.Device, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V("DEVICE RAW", "DEVICE RAW <device> <line>", "Sends one line in the projector's own protocol (framing and authentication added)", VerbCategory.Device, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V("DEVICE DISCOVER", "DEVICE DISCOVER [SWEEP <address>]", "Searches the network for projectors (the PJLink search; SWEEP also knocks on a /24)", VerbCategory.Device, VerbRisk.Low, true) with { Show = ShowClass.Setup },
        V("DEVICE PREPARE", "DEVICE PREPARE <device>", "Readies a projector for a scan: shutter open, freeze and patterns off, its own geometry off, lens recorded", VerbCategory.Device, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V("DEVICE IDENTIFY", "DEVICE IDENTIFY <device> [<seconds>]", "Shows the projector's own identify pattern for a while", VerbCategory.Device, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        // A secret rides in this line, so — like AUTH — it is never journalled, it is taken from this machine only and
        // never from an assistant, and no answer repeats it.
        V("CREDENTIAL SET", "CREDENTIAL SET <ref> [<user>] <secret>", "Keeps a secret in this node's credential store (a projector link's password, the assistant's key); this machine only", VerbCategory.Device, VerbRisk.High, false) with { SecretFrom = 3, Show = ShowClass.Admin },
        V("CREDENTIAL CLEAR", "CREDENTIAL CLEAR <ref>", "Forgets a secret from this node's credential store; this machine only", VerbCategory.Device, VerbRisk.High, false) with { Show = ShowClass.Admin },
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

        // Round 2. Each block belongs to the package its comment names: it trims a row's syntax to what it implements, removes a
        // row it does not build this round, and may add a row inside its own block; no package edits a row outside its block.
        // Every row here is V2 (answered "not available on this node yet" until its owner routes it); every row of class Air
        // or Panic is high.

        // Show Mode, panic, locks, blind edit, preflight (wp2-show; blind edit and the ALL forms by wp2-blind, its continuation).
        V2("SHOW", "SHOW [STATUS]", "Show Mode: armed or rehearsal, by whom and since, the rules, the blind draft, panic, locks and the last preflight", VerbCategory.Show, VerbRisk.Low, false),
        V2("SHOW ARM", "SHOW ARM [ALL] [<words>]", "Arms the show with no password: edits that would move the air are refused for every caller until SHOW DISARM; transport, blackout and PANIC stay live; armed survives a restart (ALL: every node of the group, answered with each member's receipt; refused while this node fans out to none)", VerbCategory.Show, VerbRisk.Review, true),
        V2("SHOW DISARM", "SHOW DISARM [ALL]", "Back to rehearsal (ALL: every node of the group, answered with each member's receipt; refused while this node fans out to none)", VerbCategory.Show, VerbRisk.Review, true),
        V2("SHOW SET", "SHOW SET MINGO <ms> [ARMED|REHEARSAL] | REMOTE-VIEW-ONLY ON|OFF | DISARM-FROM PAIRED|DESK | EXPECT-ARMED ON|OFF | COMMIT-LEAD <ms> | PANIC-LEAD <ms>", "Changes a show rule: double-GO protection, remote clients read-only while armed, who may disarm, warn when playing unarmed, how far ahead on the show clock a panic lands (COMMIT-LEAD waits for blind commits) (kept in the project, one undo step)", VerbCategory.Show, VerbRisk.Review, true),
        V2("PANIC", "PANIC [BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD] [ON <output>…|ALL] [FADE <ms 0-10000>] [AT +<ms>|<unix ms>] [HERE]", "Takes each output to its planned outcome over its fade, picture and sound, now or at an instant on the show clock; a second PANIC during the fade is the hard stop; HERE keeps it to this node; never refused for an operator (an assistant's PANIC waits for a person)", VerbCategory.Show, VerbRisk.High, true) with { Show = ShowClass.Panic },
        V2("PANIC CLEAR", "PANIC CLEAR [ON <output>…|ALL] [FADE <ms 0-10000>] [HERE]", "Brings the air back from a panic over a fade (transports stay as the outcome left them)", VerbCategory.Show, VerbRisk.High, true, "PANIC OFF") with { Show = ShowClass.Panic },
        V2("PANIC SET", "PANIC SET <output>|ALL BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD [FADE <ms 0-10000>] [SAFE <source>]", "Plans what PANIC does on an output, with its fade and the safe look SAFE shows (kept in the project, one undo step)", VerbCategory.Show, VerbRisk.Review, true),
        V2("MASTER", "MASTER <0-100> [FADE <ms 0-60000>] [HERE]", "The grand master over every output, picture and sound, faded on the show clock (not an undo step); HERE keeps it to this node", VerbCategory.Output, VerbRisk.High, true) with { Show = ShowClass.Air },
        V2("LOCK", "LOCK <id> [<words>] | LOCK AREA mapping|layout|content|playlists|media|devices|network [<words>]", "Locks an entity or an area: no verb, undo or assistant changes it; playback and reading go on (not an undo step)", VerbCategory.Show, VerbRisk.Review, true),
        V2("UNLOCK", "UNLOCK <id> | UNLOCK AREA <area> | UNLOCK ALL", "Lifts a lock (ALL: every lock this show holds)", VerbCategory.Show, VerbRisk.Review, true),
        V2("LOCKS", "LOCKS", "Every lock with who set it and why, and the areas an armed show adds", VerbCategory.Show, VerbRisk.Low, false),
        V2("BLIND", "BLIND [STATUS]", "The blind draft: whose, how many changes, when it commits", VerbCategory.Show, VerbRisk.Low, false),
        V2("BLIND ON", "BLIND ON", "Blind editing for this session: its edits go to a draft, off the air, until BLIND COMMIT", VerbCategory.Show, VerbRisk.Low, true),
        V2("BLIND OFF", "BLIND OFF", "Leaves blind editing (refused while the draft holds changes: COMMIT or DISCARD them)", VerbCategory.Show, VerbRisk.Low, true),
        V2("BLIND DIFF", "BLIND DIFF", "What the draft changes, entity by entity, in field words, with conflicts", VerbCategory.Show, VerbRisk.Low, false),
        V2("BLIND COMMIT", "BLIND COMMIT [NOW|NEXT [<playlist>]|ON GO [<playlist>]|AT +<ms>|<unix ms>] [FORCE]", "Lands the draft on the air as one undo step at a show boundary, frame-exact; FORCE overrides the conflicts it names", VerbCategory.Show, VerbRisk.High, true),
        V2("BLIND DISCARD", "BLIND DISCARD", "Drops the draft", VerbCategory.Show, VerbRisk.Review, true),
        V2("PREFLIGHT", "PREFLIGHT [QUICK] [ALL|WAIT]", "Checks the node is ready for the show from facts: outputs on present displays, media present and readable, linked projectors reachable and solved, free disk, the show clock, the links, the show's safety; each row carries the line that fixes it; answers the report's id (ALL: every node of the group, one section per node, refused while this node fans out to none; WAIT: answers with the finished report)", VerbCategory.Show, VerbRisk.Low, true),
        V2("PREFLIGHT REPORT", "PREFLIGHT REPORT [<id>]", "The last preflight report, or one by id", VerbCategory.Show, VerbRisk.Low, false),

        // Health, the fleet board in Patterns' Eye shape (wp2-fleet).
        V2("HEALTH", "HEALTH", "This machine's health in one record: outputs, render, CPU, memory, disk, GPU, network, sync, devices, media (unknown is −1)", VerbCategory.Fleet, VerbRisk.Low, false),
        V2("FLEET", "FLEET [STATUS]", "Every PlayMap machine this node hears as one board in Patterns' Eye shape: lights by evidence, headline, problems", VerbCategory.Fleet, VerbRisk.Low, false, "EYE"),
        V2("FLEET FOCUS", "FLEET FOCUS <machine|output|id|label>", "Focuses the board on a thing and its neighbours (a view, not journalled)", VerbCategory.Fleet, VerbRisk.Low, false, "EYE FOCUS") with { PairedOnly = true, Ephemeral = true },
        V2("FLEET LENS", "FLEET LENS ALL|OUTPUTS|PROJECTORS|SYNC|BACKUP|PROBLEMS", "Shows one kind of thing on the board", VerbCategory.Fleet, VerbRisk.Low, false, "EYE LENS") with { PairedOnly = true, Ephemeral = true },
        V2("FLEET NEXT", "FLEET NEXT", "Focuses the next problem (reds, then ambers)", VerbCategory.Fleet, VerbRisk.Low, false, "EYE NEXT") with { PairedOnly = true, Ephemeral = true },
        V2("FLEET PREV", "FLEET PREV", "Focuses the previous problem", VerbCategory.Fleet, VerbRisk.Low, false, "EYE PREV") with { PairedOnly = true, Ephemeral = true },
        V2("FLEET RESET", "FLEET RESET", "The board's view from before the focus", VerbCategory.Fleet, VerbRisk.Low, false, "EYE RESET") with { PairedOnly = true, Ephemeral = true },

        // Hot backup, failover, promotion, routes; the mirror between nodes (wp2-backup, fleet's continuation).
        V2("FLEET BACKUP", "FLEET BACKUP <primary node> [HOT] [TIMEOUT <ms 0-60000>]", "This node becomes a hot backup of a primary: it mirrors the primary's project, air and transports on its own outputs; FAILOVER makes it the primary", VerbCategory.Fleet, VerbRisk.High, true),
        V2("FLEET SOLO", "FLEET SOLO", "This node stops backing up and plays on alone", VerbCategory.Fleet, VerbRisk.High, true),
        V2("FLEET SUCCESSION", "FLEET SUCCESSION <node> [<node>…] | OFF", "Who leads the sync group, in order, when its leader goes quiet", VerbCategory.Fleet, VerbRisk.Review, true),
        V2("FLEET PROMOTE", "FLEET PROMOTE [<group>]", "This node leads the group now, continuing the old leader's show clock without a step", VerbCategory.Fleet, VerbRisk.High, true) with { Show = ShowClass.Fleet },
        V2("FAILOVER", "FAILOVER [<primary node>] [HOLD]", "The backup takes the show from its primary and fences the old primary by epoch; HOLD cancels a pending takeover", VerbCategory.Fleet, VerbRisk.High, true) with { Show = ShowClass.Fleet },
        V2("FAILBACK", "FAILBACK CONFIRM", "Hands the show back to the original primary (a person's press only)", VerbCategory.Fleet, VerbRisk.High, true) with { Show = ShowClass.Fleet },
        V2("FLEET FENCE", "FLEET FENCE <epoch> <node>", "A peer that took the show tells this node: hold the outputs", VerbCategory.Fleet, VerbRisk.High, true) with { Internal = true, Show = ShowClass.Fleet },
        V2("MIRROR", "MIRROR PROJECT|DELTA|AIR|ANCHOR|FOCUS|BEAT|RESYNC <json>", "A primary's show, line by line, to its backup", VerbCategory.Fleet, VerbRisk.High, true) with { Internal = true, Show = ShowClass.Fleet },
        V2("FLEET ROUTE", "FLEET ROUTE <output> DEVICE <device> <input> <backup input> | FLEET ROUTE <output> NONE | FLEET ROUTE TEST", "What FAILOVER switches so the audience sees the backup: the projector's input, confirmed by its own answer; TEST reads reachability and the current input", VerbCategory.Fleet, VerbRisk.Review, true),

        // Presentation and the frame-lock machine (wp2-output).
        V2("OUTPUT STATS", "OUTPUT STATS [<output>|ALL]", "Each output's presentation from the display's side: path, measured refresh, missed vblanks, held frames, latency, lock", VerbCategory.Output, VerbRisk.Low, false),
        V2("PRESENT RESET", "PRESENT RESET [<output>|ALL]", "Zeroes presentation counters (the old totals go to the journal)", VerbCategory.Output, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("SYNC APPLY", "SYNC APPLY [<group>]", "The leader joins the group's outputs to the hardware framelock and answers each output's observed lock", VerbCategory.Network, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V2("SYNC LEAVE", "SYNC LEAVE [<output>|ALL]", "Outputs leave the framelock and present free-running, measured", VerbCategory.Network, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V2("SYNC REJOIN", "SYNC REJOIN [<output>|ALL]", "Outputs that fell out of the lock join it again (armed: only while blacked out)", VerbCategory.Network, VerbRisk.High, true) with { Show = ShowClass.Setup },

        // The D3D11 present path (wp2-d3d11, output's continuation).
        V2("PRESENT PATH", "PRESENT PATH AUTO|DXGI|GL", "Where outputs present from the render host's next start: AUTO is OpenGL this round; DXGI (flip model on Windows) is experimental until proven on the machine (kept on the node)", VerbCategory.Output, VerbRisk.High, true) with { Show = ShowClass.Setup },

        // The group's clock, anchors and vblank verdict (wp2-sync). SUBSCRIBE ANCHORS is a topic; SYNC STATUS (today's row) gains the verdict.
        V2("SYNC RECEIPT", "SYNC RECEIPT <seq> ACCEPTED|APPLIED|REFUSED <leader ns> [<words>]", "A group member's answer to an air line its leader fanned out (member to leader, on the member's anchor feed)", VerbCategory.Network, VerbRisk.Low, false) with { Ephemeral = true },

        // HAP on the GPU (wp2-decode).
        V2("MEDIA INSPECT", "MEDIA INSPECT <media>", "What a clip is to the decoder: codec and texture, chunks, second stage, MB per frame and per second, HDR reading, odd size, and the lines that fix what is slow", VerbCategory.Media, VerbRisk.Low, false),

        // The decode-path ladder and hardware imports (wp2-zerocopy, decode's continuation).
        V2("DECODE STATUS", "DECODE STATUS", "This node's decode paths: zero-copy ready, direct sampling proven, and every open decoder's path, why and cost", VerbCategory.Media, VerbRisk.Low, false),
        V2("DECODE POLICY", "DECODE POLICY AUTO|DIRECT|GPU-COPY|COPY-BACK|SOFTWARE [FORCE]", "The best path decoders opened from now on may take (open ones keep theirs); DIRECT needs a proof unless FORCE", VerbCategory.Media, VerbRisk.Review, true) with { Show = ShowClass.Setup, RiskWhen = [new(-1, "FORCE", VerbRisk.High)] },
        V2("DECODE PROBE", "DECODE PROBE [<media>|ALL] [DIRECT]", "Decodes clips by every path and compares the pictures; DIRECT writes the direct-sampling proof for this driver", VerbCategory.Media, VerbRisk.Review, true) with { Show = ShowClass.Heavy },

        // Optimise on import (wp2-optimise).
        V2("MEDIA OPTIMISE", "MEDIA OPTIMISE <media>…|ALL [TO hap|hap-alpha|hap-q|hap-q-alpha] [QUALITY FAST|SHOW] | MEDIA OPTIMISE CANCEL|PAUSE|RESUME [<job>|ALL]", "Makes a version of clips that plays on the GPU here, in the background, never disturbing playback and never touching the source; CANCEL, PAUSE and RESUME steer jobs", VerbCategory.Media, VerbRisk.Review, true, "MEDIA OPTIMIZE") with { Show = ShowClass.Heavy, RiskWhen = [new(0, "CANCEL", VerbRisk.Low), new(0, "PAUSE", VerbRisk.Low), new(0, "RESUME", VerbRisk.Low)] },
        V2("MEDIA JOBS", "MEDIA JOBS", "Every optimise job and the governor's state", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA VERSIONS", "MEDIA VERSIONS <media>", "A clip's versions, which plays on this node and why", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA USE", "MEDIA USE <media> AUTO|SOURCE|<version>", "Which file a clip plays (from its next open, never mid-air)", VerbCategory.Media, VerbRisk.Review, true),
        V2("MEDIA RULES", "MEDIA RULES", "The optimise mode and quality, this show's rules, then the built-in table's rule names", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA RULE", "MEDIA RULE MODE AUTO|NEVER", "Changes the optimise mode: AUTO optimises clips imported from now on as the rules say, NEVER optimises nothing unless a person asks", VerbCategory.Media, VerbRisk.Review, true),

        // Network video and texture sharing: NDI, OMT, Spout (wp2-videoio).
        V2("INPUT LIST", "INPUT LIST", "Every live input with its health, its users and whether it is on air", VerbCategory.Video, VerbRisk.Low, false),
        V2("INPUT DISCOVER", "INPUT DISCOVER [NDI|OMT|SPOUT|ALL] [WAIT <0-5000 ms>]", "The NDI, OMT and Spout sources this node sees", VerbCategory.Video, VerbRisk.Low, false),
        V2("INPUT ADD", "INPUT ADD <uri> [<name>] [PROXY] [COLD]", "Adds a live input (ndi:HOST (Name), omt:HOST (Name), omt://host:port, spout:Name) to the library, received once however many surfaces show it; answers its id", VerbCategory.Video, VerbRisk.Review, true),
        V2("INPUT FALLBACK", "INPUT FALLBACK <input> HOLD|BLACK|<source>", "What an input shows when its feed is lost", VerbCategory.Video, VerbRisk.Review, true),
        V2("INPUT BACKUP", "INPUT BACKUP <input> <uri>|OFF", "A second feed the input takes over on loss", VerbCategory.Video, VerbRisk.Review, true),
        V2("INPUT SWITCH", "INPUT SWITCH <input> PRIMARY|BACKUP", "Switches an input between its feeds (answered at the first frame)", VerbCategory.Video, VerbRisk.High, true) with { Show = ShowClass.DeviceSafe },
        V2("INPUT RECONNECT", "INPUT RECONNECT <input>|ALL", "Reconnects a stale or lost input (refused while it delivers)", VerbCategory.Video, VerbRisk.High, true) with { Show = ShowClass.DeviceSafe },
        V2("SEND LIST", "SEND LIST", "Every send with its tap, format, receivers, tally and drops", VerbCategory.Video, VerbRisk.Low, false),
        V2("SEND ADD", "SEND ADD NDI|OMT|SPOUT <name> <tap> [<w>x<h>] [@<fps>] [UYVY|UYVA|P216|PA16|BGRA8] [QUALITY LOW|MEDIUM|HIGH] [ALWAYS]", "Sends a picture to the network or to other apps on this machine: tap output:<id>, canvas:<id> or source:<ref>; answers its id", VerbCategory.Video, VerbRisk.Review, true),
        V2("SEND ENABLE", "SEND ENABLE <send> ON|OFF", "Starts or stops a send", VerbCategory.Video, VerbRisk.High, true) with { RiskWhen = [new(1, "ON", VerbRisk.Review)] },
        V2("SEND REMOVE", "SEND REMOVE <send>", "Removes a send (its receivers lose the picture)", VerbCategory.Video, VerbRisk.High, true),
        V2("VIDEOIO STATUS", "VIDEOIO STATUS", "The NDI, OMT and Spout runtimes on this node: loaded, versions, discovery, bandwidth", VerbCategory.Video, VerbRisk.Low, false),
        V2("VIDEOIO CHECK", "VIDEOIO CHECK", "Video I/O readiness rows with the lines that fix them", VerbCategory.Video, VerbRisk.Low, false),
        V2("VIDEOIO SET", "VIDEOIO SET NDI ON|OFF | NDI-GROUPS <groups> | NDI-DISCOVERY <host,…>|OFF | OMT ON|OFF | OMT-DISCOVERY <host[:port]>|OFF | SPOUT ON|OFF | MAX-SEND-MBPS <0-100000>", "Changes this node's video I/O settings", VerbCategory.Video, VerbRisk.Review, true) with { Show = ShowClass.Setup },

        // Control input and bindings (wp2-control).
        V2("CONTROL STATUS", "CONTROL STATUS", "Control input: OSC, MIDI and DMX, surfaces, bindings, learning", VerbCategory.Control, VerbRisk.Low, false),
        V2("CONTROL PORTS", "CONTROL PORTS", "The MIDI ports and OSC, Art-Net and sACN sockets of this machine, and which surface each is", VerbCategory.Control, VerbRisk.Low, false),
        V2("CONTROL MONITOR", "CONTROL MONITOR [<n 1-64>]", "The last inputs heard, raw and normalised, with the binding each fired", VerbCategory.Control, VerbRisk.Low, false),
        V2("CONTROL PROFILES", "CONTROL PROFILES", "The controller profiles (feedback, init and exit messages) and whether each is verified", VerbCategory.Control, VerbRisk.Low, false),
        V2("CONTROL ENABLE", "CONTROL ENABLE ON|OFF|TOGGLE", "Every control input on or off (bindings pause; kept on the node)", VerbCategory.Control, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("CONTROL SURFACE", "CONTROL SURFACE ADD <name> MIDI|OSC|ARTNET|SACN [<match>] [PROFILE <id>] | CONTROL SURFACE REMOVE <surface>", "Names a controller the show's bindings use; answers its id", VerbCategory.Control, VerbRisk.Review, true),
        V2("CONTROL ALIAS", "CONTROL ALIAS <surface> <port glob>|OFF", "Which port a surface is on this machine (kept on the node)", VerbCategory.Control, VerbRisk.Review, true),
        V2("CONTROL FEEDBACK", "CONTROL FEEDBACK ADD|REMOVE <host:port>", "An OSC client that hears the node's state", VerbCategory.Control, VerbRisk.Review, true),
        V2("CONTROL DMX", "CONTROL DMX ARTNET|SACN ON|OFF [<interface address>]", "Listens for Art-Net or sACN control input (off until turned on; kept on the node)", VerbCategory.Control, VerbRisk.Review, true),
        V2("CONTROL INJECT", "CONTROL INJECT <input words> <value>", "Fires the bindings of an input as if it moved (never from an assistant)", VerbCategory.Control, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("BIND LIST", "BIND LIST [<surface>|<entity id>]", "The show's control bindings with their words, state and takeover", VerbCategory.Control, VerbRisk.Low, false),
        V2("BIND ADD", "BIND ADD <input words> TO <target line> | BIND ADD <json>", "Binds a control input to a wire line ('*' carries the value: SET sf… opacity *); answers its id", VerbCategory.Control, VerbRisk.Review, true) with { EmbedsLine = true },
        V2("BIND LEARN", "BIND LEARN [FROM <surface>|MIDI|OSC|ARTNET|SACN] <target line> | BIND LEARN OFF", "Binds the next control that moves to the target line (30 s); OFF stops learning", VerbCategory.Control, VerbRisk.Review, true) with { EmbedsLine = true, RiskWhen = [new(0, "OFF", VerbRisk.Low)] },
        V2("BIND FORGET", "BIND FORGET <target line>|<input words>", "Forgets bindings by target or by input", VerbCategory.Control, VerbRisk.Review, true),
        V2("BIND ENABLE", "BIND ENABLE <binding>|<surface> ON|OFF", "Turns a binding, or a surface's bindings, on or off", VerbCategory.Control, VerbRisk.Review, true),
        V2("NUDGE", "NUDGE <id> <field.path> <±delta>", "Moves a number by a step within its range (encoders, Companion dials)", VerbCategory.Control, VerbRisk.Review, true) with { Gesture = true, Show = ShowClass.Target },

        // Timecode chase (wp2-control). "TC" reads as "TIMECODE".
        V2("TIMECODE STATUS", "TIMECODE STATUS [<source>|<playlist>]", "Timecode sources (rolling, format, speed, errors) and every playlist that chases one", VerbCategory.Timecode, VerbRisk.Low, false),
        V2("TIMECODE DEVICES", "TIMECODE DEVICES", "The audio inputs, MIDI ports and Art-Net interfaces timecode can arrive on", VerbCategory.Timecode, VerbRisk.Low, false),
        V2("TIMECODE SOURCE", "TIMECODE SOURCE ADD <name> LTC [<device>[:<channel>]] [FORMAT <fmt>] | TIMECODE SOURCE ADD <name> MTC <port> [FORMAT <fmt>] | TIMECODE SOURCE ADD <name> ARTNET [STREAM <n>] [FORMAT <fmt>] | TIMECODE SOURCE ADD <name> INTERNAL [FORMAT <fmt>] | TIMECODE SOURCE REMOVE <source> | TIMECODE SOURCE RESET <source>", "Adds, removes or resets a timecode input; answers its id", VerbCategory.Timecode, VerbRisk.Review, true) with { RiskWhen = [new(0, "RESET", VerbRisk.Low)] },
        V2("TIMECODE CHASE", "TIMECODE CHASE <playlist> <source>[,<backup>…]|OFF", "A playlist follows timecode: each item at its TC in, locked to the code's fitted line, freewheeling through dropouts", VerbCategory.Timecode, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V2("TIMECODE ENGAGE", "TIMECODE ENGAGE <playlist>|ALL ON|OFF|TOGGLE", "Engages or disengages a chase (disengaged, the playlist plays by hand from where it is)", VerbCategory.Timecode, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V2("TIMECODE SET", "TIMECODE SET <playlist> DELAY <±ms> | TIMECODE SET <playlist> FREEWHEEL <ms> | TIMECODE SET <playlist> ONLOSS HOLD|STOP|CONTINUE | TIMECODE SET <playlist> JUMP <ms>|AUTO | TIMECODE SET <playlist> GAP BLACK|HOLD", "Tunes a chase", VerbCategory.Timecode, VerbRisk.Review, true) with { Show = ShowClass.Playlists },
        V2("TIMECODE IN", "TIMECODE IN <playlist> <item> <HH:MM:SS:FF>|NOW|OFF", "The timecode an item starts at (NOW takes the chase's current code)", VerbCategory.Timecode, VerbRisk.Review, true) with { Show = ShowClass.Playlists },
        V2("TIMECODE INTERNAL", "TIMECODE INTERNAL <source> PLAY|STOP | TIMECODE INTERNAL <source> LOCATE <tc> | TIMECODE INTERNAL <source> RATE <0.5-2>", "Runs an internal timecode source (rehearsal or master)", VerbCategory.Timecode, VerbRisk.High, true) with { Show = ShowClass.Transport },

        // Controllers' side of the wire: index, events, presence (wp2-companion).
        V2("INDEX", "INDEX", "The show's structure for controllers (playlists and items, surfaces, projectors, outputs, devices, generators, and every package's section with its preset hint) with structure and label signatures", VerbCategory.Session, VerbRisk.Low, false),
        V2("EVENTS", "EVENTS [SINCE <seq>]", "The node's recent events (item start and end, armed, panic, faults, calibration moved…) for automations", VerbCategory.Session, VerbRisk.Low, false),
        V2("PRESENCE", "PRESENCE <json>", "A controller says what it is and what it shows (kind, module version, page)", VerbCategory.Session, VerbRisk.Low, false) with { PairedOnly = true },
        V2("WHOAMI", "WHOAMI", "This session as the node sees it: transport, role, paired, armed, and the verbs it may run now", VerbCategory.Session, VerbRisk.Low, false),
        V2("THUMB", "THUMB <media:<id>|generator:<id>|playlist:<id>|color:#RRGGBB> [<w>x<h>] [JPEG|PNG] [IF <etag>]", "A small picture for a key or a list, cached by content and ETag", VerbCategory.Media, VerbRisk.Low, false) with { PairedOnly = true },

        // Effects: ISF in chains (wp2-fx). FX BIND / FX UNBIND are aliases of MOD BIND / MOD UNBIND.
        V2("FX KINDS", "FX KINDS [<family>|<search>]", "Every effect kind (built-in and this show's ISF files) with its parameters and traits", VerbCategory.Effects, VerbRisk.Low, false),
        V2("FX LIST", "FX LIST <target>|ALL", "The effect chain of a layer, canvas, surface, output or playlist item, with each slot's state", VerbCategory.Effects, VerbRisk.Low, false),
        V2("FX ADD", "FX ADD <target> <kind> [AT <position>] [<name>]", "Adds an effect to a chain (positions count from 1); answers its slot id", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX REMOVE", "FX REMOVE <slot>", "Removes an effect", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX MOVE", "FX MOVE <slot> <position>", "Moves an effect to a position in its chain (from 1)", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX SET", "FX SET <slot> <param>|mix|blend|scale|name <value> | FX SET <slot> <param> BY <delta>", "Sets an effect's parameter, mix, blend or scale", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX BYPASS", "FX BYPASS <slot>|<target>|ALL ON|OFF|TOGGLE", "Bypasses effects (ALL ON is the panic outcome 'effects off')", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX RESET", "FX RESET <slot>", "Sets an effect's parameters back to their defaults", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX IMPORT", "FX IMPORT <path>|<json>", "Adds an ISF effect to the show (content-addressed, carried with it); answers its asset id and the compile report", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX CHECK", "FX CHECK <asset>|<kind>|<json>", "Translates an ISF source for GLSL 3.30 and ES 3.00, compiles it on this node's context and reports its issues by line", VerbCategory.Effects, VerbRisk.Low, false),

        // Sound, the beat clock and the modulation bus (wp2-audio).
        V2("AUDIO INPUTS", "AUDIO INPUTS", "The sound inputs this node can listen to (devices, the system's sound, test signals)", VerbCategory.Audio, VerbRisk.Low, false),
        V2("AUDIO LISTEN", "AUDIO LISTEN <input>|DEFAULT|SYSTEM|OFF [CHANNELS 1|1+2|3-4|M|S] [GAIN <dB>] [HERE] | AUDIO LISTEN TEST CLICK|SWEEP|NOISE|DRUMS [CHANNELS 1|1+2|3-4|M|S] [GAIN <dB>] [HERE]", "Chooses what the show listens to (HERE: this machine only)", VerbCategory.Audio, VerbRisk.Review, true),
        V2("AUDIO GAIN", "AUDIO GAIN <-24…24>|AUTO|FIXED", "The listening gain", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("AUDIO STATUS", "AUDIO STATUS", "Levels, bands, the spectrum, onsets and the input's state", VerbCategory.Audio, VerbRisk.Low, false),
        V2("TEMPO STATUS", "TEMPO STATUS", "The beat clock: BPM, beat, bar, phrase, source, lock and confidence", VerbCategory.Audio, VerbRisk.Low, false),
        V2("TEMPO SET", "TEMPO SET <20-999>|+<n>|-<n> [AT +<ms>|<unix ms>|BEAT|BAR|PHRASE]", "Sets the tempo (or moves it by n BPM), one lead ahead or at AT", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO TAP", "TEMPO TAP [AT <unix ms>]", "A tap; two or more set the tempo", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO RESYNC", "TEMPO RESYNC [BEAT|BAR|PHRASE] [AT <unix ms>]", "Puts the beat, bar or phrase on now", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO NUDGE", "TEMPO NUDGE <±1-200 ms>|FASTER|SLOWER|STOP", "Moves the beat's phase, slewed over a bar", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO HALF", "TEMPO HALF", "Halves the tempo", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO DOUBLE", "TEMPO DOUBLE", "Doubles the tempo", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO SOURCE", "TEMPO SOURCE MANUAL|DETECT", "Where the beat comes from: taps and SET, or detected from the sound", VerbCategory.Audio, VerbRisk.Review, true),
        V2("TEMPO METER", "TEMPO METER <beats per bar 1-16> [<bars per phrase 1-64>]", "The meter the bar and phrase sources count", VerbCategory.Audio, VerbRisk.Low, true),
        V2("MOD SOURCES", "MOD SOURCES [<prefix>]", "Every modulation source on this node (audio:, beat, bar, phrase, beat:pulse, bpm, lfo:) with its value and whether it is pure or live", VerbCategory.Modulation, VerbRisk.Low, false),
        V2("MOD BIND", "MOD BIND <entity> <param> <source> [AMOUNT <-1…1>] [OFFSET <x>] [MODE ADD|SCALE|REPLACE] [CURVE LINEAR|EASE|STEP|EXP|LOG|S] [SMOOTH <attack ms>[/<release ms>]] [RANGE <min> <max>] [STEPS <n>] [INVERT]", "Modulates a number of a generator, layer, surface or effect slot from a source, live; the show keeps the base value", VerbCategory.Modulation, VerbRisk.Review, true, "FX BIND", "GENERATOR BIND"),
        V2("MOD UNBIND", "MOD UNBIND <entity> <param>|ALL", "Removes modulation", VerbCategory.Modulation, VerbRisk.Review, true, "FX UNBIND", "GENERATOR UNBIND"),
        V2("MOD BYPASS", "MOD BYPASS ON|OFF|TOGGLE [<entity>|ALL]", "Holds every modulated number at its base value", VerbCategory.Modulation, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("MOD PEEK", "MOD PEEK <entity>", "The live value of each modulated number of an entity", VerbCategory.Modulation, VerbRisk.Low, false),

        // Particles, fractals, reactive: time, inputs, looks, stings (wp2-content).
        V2("GENERATOR GET", "GENERATOR GET <generator>", "A generator with its effective values, bindings, inputs, morph, cost and state", VerbCategory.Content, VerbRisk.Low, false),
        V2("GENERATOR RESET", "GENERATOR RESET <generator> [<param>|ALL]", "Parameters back to their defaults", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR INPUT", "GENERATOR INPUT <generator> source <source>|NONE", "A picture a generator reads (a particle flow emits where it is bright)", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR PRESETS", "GENERATOR PRESETS [<kind>|<family>]", "Curated and saved looks", VerbCategory.Content, VerbRisk.Low, false),
        V2("GENERATOR RECALL", "GENERATOR RECALL <generator> <preset>", "Applies a look (one undo step)", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR SAVE", "GENERATOR SAVE <generator> <name>", "Saves the generator's look in the show; answers its id", VerbCategory.Content, VerbRisk.Low, true),
        V2("GENERATOR RETIME", "GENERATOR RETIME <generator>|ALL [AT +<ms>|<unix ms>]", "Restarts a generator's own time (particles re-seed from that instant)", VerbCategory.Content, VerbRisk.Review, true),
        V2("STING", "STING <preset> [<100-5000 ms>] [AT +<ms>|<unix ms>]", "Fires a sting every generator answers, on the show clock", VerbCategory.Content, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("STING CLEAR", "STING CLEAR", "Stops the sting", VerbCategory.Content, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("STING LIST", "STING LIST", "The sting presets", VerbCategory.Content, VerbRisk.Low, false),

        // LED pixel mapping (wp2-led).
        V2("LED LIST", "LED LIST", "The LED rig: nodes, fixtures, pixels, universes, conflicts and bandwidth", VerbCategory.Led, VerbRisk.Low, false),
        V2("LED NEW", "LED NEW LINE|GRID|RING <count>[x<rows>] [ON canvas:<id>|output:<id>|surface:<id>|source:<ref>] [TYPE <type>] [<name>]", "Adds a fixture laid out on a space; answers its id", VerbCategory.Led, VerbRisk.Low, true),
        V2("LED IMPORT", "LED IMPORT <path> [AS CSV] [ON canvas:<id>|output:<id>|surface:<id>|source:<ref>] [SCALE <x>] [NODE <node>]", "Imports pixel positions and patches from a CSV (one undo step)", VerbCategory.Led, VerbRisk.Review, true),
        V2("LED PATCH", "LED PATCH <fixture> <node> <universe>[.<address>] [FORCE]", "Patches a fixture to a node's universe and address (FORCE allows an overlap)", VerbCategory.Led, VerbRisk.Review, true),
        V2("LED AUTOPATCH", "LED AUTOPATCH [<fixture>|#<tag>|ALL] [NODE <node>…] [FROM <universe>[.<address>]] [EACH ON|OFF]", "Patches fixtures in order within each node's capacity", VerbCategory.Led, VerbRisk.Review, true),
        V2("LED OUTPUT", "LED OUTPUT ON|OFF [<node>|ALL] [HOLD]", "This machine sends pixel data, or stops (one black frame first unless HOLD)", VerbCategory.Led, VerbRisk.High, true) with { Show = ShowClass.Air },
        V2("LED TEST", "LED TEST CHASE|ORDER|UNIVERSE|FIXTURE|GRADIENT|WALK|WHITE|FULL|BLACK|OFF [ON <node>|<fixture>|#<tag>|ALL]", "A test pattern on the LEDs instead of the show", VerbCategory.Led, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V2("LED LOCATE", "LED LOCATE <fixture> [<seconds 1-60>]", "Blinks one fixture", VerbCategory.Led, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("LED LEVEL", "LED LEVEL <0-100> [FADE <ms>] | LED LEVEL BY <delta> [FADE <ms>]", "The LED rig's master level (live, not an undo step)", VerbCategory.Led, VerbRisk.High, true) with { Show = ShowClass.Air },
        V2("LED MONITOR", "LED MONITOR <node> <universe>", "The bytes last sent to a universe, with the fixtures in it", VerbCategory.Led, VerbRisk.Low, false),

        // Facades: site, survey, control points, long-throw priors (wp2-facade; the guide, the light plan and stacks are wp2-stacks').
        V2("SITE", "SITE", "The site frame: coordinate system, grid origin, rotation, scale and mains frequency", VerbCategory.Site, VerbRisk.Low, false),
        V2("SITE ORIGIN", "SITE ORIGIN <e> <n> <h> [CRS <code>|LOCAL] [ROTATE <deg>]", "Sets the site frame; the whole stage moves with it in one edit", VerbCategory.Site, VerbRisk.Review, true),
        V2("SITE MAINS", "SITE MAINS 50|60", "The venue's mains frequency", VerbCategory.Site, VerbRisk.Low, true),
        V2("SITE POINTS", "SITE POINTS <path> [ORDER PENZD|PNEZD|ENH|NEH] [CRS <code>] [UNITS m|cm|mm|ft|usft]", "Imports surveyed control points; answers their ids and the lines it skipped", VerbCategory.Site, VerbRisk.Review, true),
        V2("SITE CHECK", "SITE CHECK [<surface>|<survey>]", "Measures the model against the control points: each point's distance and the fit", VerbCategory.Site, VerbRisk.Low, false),
        V2("SURVEY IMPORT", "SURVEY IMPORT <path> [CRS <code>|LOCAL] [UNITS m|cm|mm|ft|usft] [UP Z|Y] [VOXEL <m>] [DECIMATE <triangles>|OFF] [NAME <name>]", "Imports a surveyed model or point cloud (OBJ, PLY, glTF, LAS, E57, XYZ) into the site frame, off the control lane; answers its id", VerbCategory.Site, VerbRisk.Review, true) with { Show = ShowClass.Heavy },
        V2("SURVEY LIST", "SURVEY LIST", "Imported surveys with provenance and state", VerbCategory.Site, VerbRisk.Low, false),
        V2("PROJECTOR PRIOR", "PROJECTOR PRIOR <projector> THROW <ratio> [±<pct>] | PROJECTOR PRIOR <projector> AT <control> [±<m>] | PROJECTOR PRIOR <projector> AT <x> <y> <z> [±<m>] | PROJECTOR PRIOR <projector> CLEAR", "What a solve may assume about a projector's lens and position", VerbCategory.Mapping, VerbRisk.Review, true),
        // Kept unrouted for wp2-stacks: the calibration score's stack term names it as its fix (unavailable until routed).
        V2("STACK ALIGN", "STACK ALIGN <stack> FROM <scan> [GRID <c>x<r>]", "Fits each member's measured residual to the master's picture from a shared scan", VerbCategory.Mapping, VerbRisk.Review, true),

        // Auto-align from the truck: camera-measured flat-screen correction (wp2-align, continued by wp2-resolve: ONLY, COLOR WHITE, the verify pass, ALIGN SCORE and DEVICE NATIVE).
        V2("ALIGN NEW", "ALIGN NEW <name> <camera> <output>…|ALL [CANVAS <canvas>|NEW] [SHAPE AUTO|FLAT]", "Outputs aligned together through one camera onto one canvas; answers its id", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN RUN", "ALIGN RUN <group> [ONLY <output>…] [STEPS SCAN,SOLVE,COLOR|ALL] [FIT INSIDE|CORNERS] [COLOR OFF|WHITE]", "Measures the outputs through the camera and stages a correction (warp, blend and pedestal, γ, and with COLOR WHITE each white matched) with its score; ONLY scans those outputs again and keeps the others; nothing changes on the air until ALIGN ACCEPT", VerbCategory.Align, VerbRisk.High, true) with { Show = ShowClass.Heavy },
        V2("ALIGN STATUS", "ALIGN STATUS", "The running or last run", VerbCategory.Align, VerbRisk.Low, false),
        V2("ALIGN SCORE", "ALIGN SCORE <run>|<group>", "A run's score and every term with its words and fix", VerbCategory.Align, VerbRisk.Low, false),
        V2("ALIGN CANCEL", "ALIGN CANCEL", "Stops a run; the outputs are the show's again", VerbCategory.Align, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("ALIGN ACCEPT", "ALIGN ACCEPT <run>|<group>", "Applies a staged run to its outputs as one edit; the corrections they had become their fallbacks", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN REVERT", "ALIGN REVERT <group>", "Back to each output's fallback correction", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN CORNERS", "ALIGN CORNERS <group> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> | ALIGN CORNERS <group> CLEAR", "The screen's corners in the camera picture (0–1), for an exact canvas", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN CLEAR", "ALIGN CLEAR <output>|<group>", "Removes measured corrections from outputs; what they had becomes the fallback", VerbCategory.Align, VerbRisk.Review, true),
        V2("DEVICE NATIVE", "DEVICE NATIVE <device>", "A projector's own geometry and edge blend, read live (Panasonic this round; others answer with words), for the double-correction guard", VerbCategory.Device, VerbRisk.Low, false),

        // The phone as a scan camera, fully local; many views into one scan (wp2-phonescan, wp2-multiscan).
        V2("PHONE INVITE", "PHONE INVITE [<name>] [FOR <minutes 1-60>]", "A one-time link and QR code that makes a phone a scan camera (to this session only; never from an assistant)", VerbCategory.Scan, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V2("PHONE LIST", "PHONE LIST", "Phones paired with this node and what their cameras can do", VerbCategory.Scan, VerbRisk.Low, false),
        V2("PHONE FORGET", "PHONE FORGET <phone>|ALL", "Forgets a phone's key and disconnects it", VerbCategory.Scan, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("SCAN NEXT", "SCAN NEXT", "A folder camera in manual mode takes the picture of the pattern on the glass; any other camera has the pattern it waits on put up again and asked for afresh (a phone that was bumped or lost its view)", VerbCategory.Scan, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("SCAN SUGGEST", "SCAN SUGGEST <scan> [ASSISTANT]", "Measured surface suggestions for a scan with their reasons, each queued as proposals only a person's APPROVE runs (SCAN SURFACE, and SURFACE QUAD for a plane); ASSISTANT: names and ranking from the node's assistant, from text only", VerbCategory.Scan, VerbRisk.Low, false),
        V2("SCAN SOLVE", "SCAN SOLVE <scan> [HOLD <projector>…]", "Solves every projector, view and camera of a scan together (projector-anchored bundle adjustment); HOLD keeps those projectors at their stage poses", VerbCategory.Scan, VerbRisk.Review, true),
        V2("SCAN VIEWS", "SCAN VIEWS <scan>", "A scan's views with their residuals, tracks, pairing and state, and its coverage per projector", VerbCategory.Scan, VerbRisk.Low, false),
        V2("SCAN DROP", "SCAN DROP <scan> <view>", "Drops a view from a scan and solves the rest again", VerbCategory.Scan, VerbRisk.Review, true),

        // On-wall editing and the quad path (wp2-wall).
        V2("LIGHT WALL", "LIGHT WALL <display>|#<n>|<output> [AS <name>] [THROW <ratio>]", "First light in one line: an output full screen on that display as a projector, with the edit overlay and the wall cursor on", VerbCategory.Mapping, VerbRisk.High, true) with { Show = ShowClass.Layout },
        V2("SURFACE QUAD", "SURFACE QUAD <projector>|<output> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> [PX|RASTER] [AS <name>] [SOURCE <source>]", "Draws a surface as four corners in a projector's picture, clicked on the wall in any order; answers its id", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("SURFACE CORNER", "SURFACE CORNER <surface> TL|TR|BR|BL|0|1|2|3 TO <x> <y> [PX|RASTER] | SURFACE CORNER <surface> <corner> BY <dx> <dy> [PX|RASTER]", "Moves a quad's corner (BY: wall pixels by default)", VerbCategory.Mapping, VerbRisk.Review, true) with { Gesture = true },
        V2("SURFACE LATTICE", "SURFACE LATTICE <surface> <c>x<r>|OFF [LINEAR|SMOOTH|BEZIER]", "Gives a quad a lattice on top of its corners (its shape does not move)", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("SURFACE NODE", "SURFACE NODE <surface> <col> <row> TO|BY <x> <y> [PX|RASTER]", "Moves a quad's lattice node", VerbCategory.Mapping, VerbRisk.Review, true) with { Gesture = true },
        V2("HANDLE SELECT", "HANDLE SELECT <surface> TL|TR|BR|BL|<col>,<row> | HANDLE SELECT NONE", "The handle the wall shows enlarged and the arrows move", VerbCategory.Mapping, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },
        V2("HANDLE NEXT", "HANDLE NEXT", "The next handle", VerbCategory.Mapping, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },
        V2("HANDLE PREV", "HANDLE PREV", "The previous handle", VerbCategory.Mapping, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },
        V2("HANDLE NUDGE", "HANDLE NUDGE <dx> <dy> [PX|RASTER]", "Moves the selected handle (encoders and dials)", VerbCategory.Mapping, VerbRisk.Review, true) with { Gesture = true },
        V2("OVERLAY", "OVERLAY ON|OFF|TOGGLE|KEEP [ON <output>|ALL] [LAYERS <cursor,outlines,handles,lattice>|EDIT|ALL]", "The edit overlay on the wall, drawn through each output's warp; KEEP renews this session's lease (it lapses 15 s after the last)", VerbCategory.Output, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("CURSOR", "CURSOR <output> <x> <y> [TRAIL <x> <y>…] [COLOR #rrggbb] | CURSOR OFF", "This session's cursor on the wall (0–1 across the output)", VerbCategory.Output, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },

        // Explainable score, results ledger, fallback, history, re-solving the projector that moved (wp2-wall).
        V2("CALIBRATION STATUS", "CALIBRATION STATUS [<projector>|<output>|ALL]", "Each projector's and output's score with its terms, current, fallback and staged results, and the running task", VerbCategory.Calibration, VerbRisk.Low, false),
        V2("CALIBRATION HISTORY", "CALIBRATION HISTORY <projector>|<output> [<n>]", "Its results, newest first, with how far each moved the picture", VerbCategory.Calibration, VerbRisk.Low, false),
        V2("CALIBRATION SAVE", "CALIBRATION SAVE <projector>|<output>|ALL [AS <name>]", "Keeps the current calibration as a pinned result", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION RECALL", "CALIBRATION RECALL <snapshot>", "Puts a kept result back (recorded as a new result)", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION ACCEPT", "CALIBRATION ACCEPT <projector>|<output>|ALL|<snapshot>", "Accepts the current (or a staged) result: it becomes the fallback", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION FALLBACK", "CALIBRATION FALLBACK <projector>|<output>|ALL", "Back to the fallback result, exactly", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION FORGET", "CALIBRATION FORGET <snapshot>", "Forgets a kept result (never the current, fallback or staged one)", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION RESOLVE", "CALIBRATION RESOLVE <projector>…|ALL [CAMERA <camera>] [STAGE]", "Re-solves only the projectors named — from their pins as they stand, or with CAMERA from a fresh look at their dots (only the ones found moved) — scores and applies it (or stages it when it fails the gate); the others are not touched", VerbCategory.Calibration, VerbRisk.High, true) with { Show = ShowClass.Heavy },
        V2("CALIBRATION CANCEL", "CALIBRATION CANCEL", "Stops a calibration task and gives the outputs back", VerbCategory.Calibration, VerbRisk.Low, true) with { Show = ShowClass.Perform },
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

    /// <summary>A line's risk: its verb's, set by the first <see cref="VerbSpec.RiskWhen"/> its words match (only upward on a
    /// line that moves the air, a transport, a panic or a projector's power), raised to high by the words that make it so
    /// (<see cref="VerbSpec.HighFor"/>), and for a verb that carries another line, at least that line's risk. High for a line no
    /// verb names. It decides who must approve an assistant's line, never what an armed node refuses.</summary>
    public static VerbRisk RiskOf(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return RiskOf(line, 0);
    }

    /// <summary>How deep lines may carry lines before the carried one counts as high (a line binding a line that binds a line).</summary>
    private const int MaxEmbedding = 4;

    private static VerbRisk RiskOf(WireLine line, int depth)
    {
        if (Match(line) is not { } m)
            return VerbRisk.High;
        var spec = m.Spec;
        var risk = spec.Risk;
        foreach (var when in spec.RiskWhen)
        {
            if (!Says(line, m.Words, when))
                continue;
            risk = ShowClasses.RiskOnlyRises(spec.Show) ? Max(risk, when.Risk) : when.Risk;
            break;
        }
        var first = line.WordUpper(m.Words);
        foreach (var high in spec.HighFor)
            if (string.Equals(high, first, StringComparison.Ordinal))
                return VerbRisk.High;
        if (!spec.EmbedsLine)
            return risk;
        foreach (var carried in CarriedReadings(spec, line, m.Words))
        {
            if (carried is null)
                return VerbRisk.High;
            if (Match(carried) is not null)
                risk = Max(risk, depth >= MaxEmbedding ? VerbRisk.High : RiskOf(carried, depth + 1));
        }
        return risk;
    }

    private static VerbRisk Max(VerbRisk a, VerbRisk b) => a >= b ? a : b;

    private static bool Says(WireLine line, int verbWords, RiskWhen when)
    {
        var word = when.Word.ToUpperInvariant();
        if (when.Arg >= 0)
            return string.Equals(line.WordUpper(verbWords + when.Arg), word, StringComparison.Ordinal);
        for (var i = verbWords; i < line.Count; i++)
            if (string.Equals(line.WordUpper(i), word, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The line a carrying verb's line carries, as its owner reads it: the words after the first TO for a row whose syntax
    /// binds "TO &lt;target line&gt;", else the words after an optional "FROM &lt;x&gt;"; a JSON argument's "target" (in any case;
    /// the last one given). Null when it carries none. Words that name no verb are no line: they can never run, so they add no
    /// risk.</summary>
    public static WireLine? EmbeddedLine(VerbSpec spec, WireLine line, int verbWords)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        if (!spec.EmbedsLine || line.Count <= verbWords)
            return null;
        var first = line.Word(verbWords);
        if (first.StartsWith('{'))
            return Targets(first).LastOrDefault();
        if (!BindsTo(spec))
            return CarriedAfterFrom(line, verbWords);
        for (var i = verbWords; i < line.Count; i++)
            if (string.Equals(line.WordUpper(i), "TO", StringComparison.Ordinal))
                return Tail(line, i + 1);
        return null;
    }

    /// <summary>Every line a carrying verb's line could be read to carry, so its risk is the highest of them: the words after each
    /// TO (an input's own words may hold one), else after an optional FROM x; every "target" of a JSON argument, in any case. A
    /// null reading is a JSON argument no reader can read, which counts as high.</summary>
    private static IEnumerable<WireLine?> CarriedReadings(VerbSpec spec, WireLine line, int verbWords)
    {
        if (line.Count <= verbWords)
            yield break;
        var first = line.Word(verbWords);
        if (first.StartsWith('{'))
        {
            foreach (var target in Targets(first))
                yield return target;
            yield break;
        }
        if (!BindsTo(spec))
        {
            if (CarriedAfterFrom(line, verbWords) is { } carried)
                yield return carried;
            yield break;
        }
        for (var i = verbWords; i < line.Count; i++)
            if (string.Equals(line.WordUpper(i), "TO", StringComparison.Ordinal) && Tail(line, i + 1) is { } after)
                yield return after;
    }

    private static bool BindsTo(VerbSpec spec) => spec.Syntax.Contains(" TO <target line>", StringComparison.Ordinal);

    private static WireLine? CarriedAfterFrom(WireLine line, int verbWords) =>
        Tail(line, string.Equals(line.WordUpper(verbWords), "FROM", StringComparison.Ordinal) ? verbWords + 2 : verbWords);

    private static WireLine? Tail(WireLine line, int from) =>
        from < line.Count ? new WireLine(null, line.Words.Skip(from).ToList(), line.RestAfter(from)) : null;

    /// <summary>Every "target" a JSON argument gives (any case, each one when it is given twice), read as lines; a text that does not
    /// read as one is skipped (it can never run). One null when the argument is not JSON any reader takes.</summary>
    private static IEnumerable<WireLine?> Targets(string json)
    {
        System.Text.Json.JsonElement root;
        try
        {
            root = System.Text.Json.JsonElement.Parse(json, ReaderOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return [null];
        }
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            return [];
        var lines = new List<WireLine?>();
        foreach (var p in root.EnumerateObject())
            if (p.Name.Equals("target", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                && WireGrammar.TryParse(p.Value.GetString(), out var parsed, out _))
                lines.Add(parsed);
        return lines;
    }

    /// <summary>How a JSON argument is read everywhere a verb takes one: comments and trailing commas allowed.</summary>
    private static readonly System.Text.Json.JsonDocumentOptions ReaderOptions =
        new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true };

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
    /// how many words it took. VT and CLIP are VIDEO (Patterns' aliases) and TC is TIMECODE, whatever follows. Null
    /// when nothing matches.</summary>
    public static (VerbSpec Spec, int Words)? Match(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var first = line.WordUpper(0);
        if (first is "VT" or "CLIP")
            first = "VIDEO";
        else if (first is "TC")
            first = "TIMECODE";
        if (line.Count >= 2 && ByName.TryGetValue(first + " " + line.WordUpper(1), out var two))
            return (two, 2);
        if (line.Count >= 1 && ByName.TryGetValue(first, out var one))
            return (one, 1);
        return null;
    }

    public static VerbSpec? Find(string name) =>
        ByName.TryGetValue(name.Trim().ToUpperInvariant(), out var v) ? v : null;
}
