#!/usr/bin/env python3
"""Writes the round-2 packages' stub files: one per package for each hook the spine declares.

A stub is written only when its file is absent, so a package's filled stub is never overwritten; the script is kept for the
spine amendment lane (a new hook or a new package word adds a line to the tables below and the script writes the new stubs).
Every empty body holds one comment line, "// wp2-<pkg> fills this.", so an unimplemented partial method and an empty method
body both pass the analyzers.

Usage: python3 tools/gen_stubs.py [section ...]   (sections: engine, core, render, fixtures, docs; none = all)
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The package words in hook order, with the package that owns each (a continuation uses its parent's word).
WORDS = [
    ("Show", "show"), ("Fleet", "fleet"), ("Sync", "sync"), ("Output", "output"), ("Decode", "decode"),
    ("Optimise", "optimise"), ("VideoIo", "videoio"), ("Control", "control"), ("Companion", "companion"), ("Fx", "fx"),
    ("Audio", "audio"), ("Content", "content"), ("Led", "led"), ("Site", "facade"), ("Align", "align"),
    ("Phone", "phonescan"), ("Wall", "wall"),
]
PKG = dict(WORDS)

W = "Utf8JsonWriter w"
CTX = "List<ContextItem> items, List<ContextLink> links"
VAL = "Project p, object entity, ref string? why"

# EngineCore hooks: (method name template, parameters, words that implement it). Sync's live in today's EngineCore.Sync.cs.
ENGINE_HOOKS = [
    ("Route{0}", "VerbRoutes routes", [w for w, _ in WORDS]),
    ("Tick{0}", "long leaderNs", ["Show", "Fleet", "Sync", "Output", "Control", "Audio", "Content", "Led", "Wall"]),
    ("State{0}", W, ["Show", "Fleet", "Decode", "VideoIo", "Control", "Companion", "Fx", "Audio", "Content", "Led", "Wall", "Align", "Phone"]),
    ("Air{0}", W, ["Show", "Wall"]),
    ("Output{0}", W + ", Output o", ["Output", "Fx", "Align"]),
    ("Playlist{0}", W + ", Playlist list", ["Control"]),
    ("Sync{0}", W, ["Sync", "Output", "Fleet"]),
    ("Media{0}", W, ["Optimise"]),
    ("Stage{0}", W, ["Site"]),
    ("Projector{0}", W + ", Projector pj", ["Site", "Wall"]),
    ("Machine{0}", W, ["Output", "Decode"]),
    ("Project{0}", W, ["Companion"]),
    ("Context{0}", CTX, [w for w, _ in WORDS]),
    ("ShapeContext", CTX, ["Fleet"]),
    ("Headline{0}", "ref string headline", ["Show", "Fleet"]),
    ("Validate{0}", VAL, [w for w, _ in WORDS if w not in ("Sync", "Companion")]),
    ("RecoverySave{0}", "IDictionary<string, string> into", ["Show", "Fleet", "Sync", "Control", "Led"]),
    ("RecoveryLoad{0}", "IReadOnlyDictionary<string, string> from", ["Show", "Fleet", "Sync", "Control", "Led"]),
    ("GateLine", "VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused, ref ProjectStore? lineStore", ["Show"]),
]

# Render host hooks (Engine/Rendering/RenderHost.cs) and shader program hooks (Render/Shaders/ShaderLibrary.cs).
RENDER_WORDS = ["Show", "Output", "Decode", "VideoIo", "Fx", "Audio", "Content", "Led", "Align", "Site", "Wall"]
SHADER_WORDS = ["Show", "Output", "Decode", "Optimise", "VideoIo", "Fx", "Content", "Led", "Align", "Wall"]

# The node's settings per package (NodeSettings.<Word>.cs): the property and its record, as the spine fixes them.
NODE_SETTINGS = {
    "Show": ("ShowSafetySettings Show", """public sealed record ShowSafetySettings
{
    public static readonly ShowSafetySettings Default = new();

    /// <summary>HELLO names or addresses whose remote sessions count as Operator; [] = only the desk.</summary>
    public ImmutableArray<string> Operators { get; init; } = [];
}"""),
    "Fleet": ("FleetSettings Fleet", """public sealed record FleetSettings
{
    public static readonly FleetSettings Default = new();

    /// <summary>solo | backup | primary | fenced — re-entered at start.</summary>
    public string Role { get; init; } = "solo";

    /// <summary>The primary this node backs up (node id).</summary>
    public string Of { get; init; } = "";

    public int TimeoutMs { get; init; } = 2000;

    public long Epoch { get; init; }

    /// <summary>What a fenced node's outputs show: black | close.</summary>
    public string FencedOutputs { get; init; } = "black";

    public bool Monitor { get; init; } = true;

    public int MonitorMax { get; init; } = 32;
}"""),
    "Sync": ("SyncSettings Sync", """public sealed record SyncSettings
{
    public static readonly SyncSettings Default = new();

    /// <summary>solo | lead | follow — re-entered at start.</summary>
    public string Role { get; init; } = "solo";

    public string Group { get; init; } = "";

    public string Leader { get; init; } = "";

    public bool FollowPatternsBlackout { get; init; } = true;

    /// <summary>Clock corrections below 1 ms are slewed at most this fast; larger ones step (and rebase every anchor).</summary>
    public double SlewPpm { get; init; } = 200;

    /// <summary>A leader not heard for this long: followers free-wheel (Hold) and the context says so.</summary>
    public int LeaderTimeoutMs { get; init; } = 3000;
}"""),
    "Output": ("PresentSettings Present", """public sealed record PresentSettings
{
    public static readonly PresentSettings Default = new();

    /// <summary>auto | dxgi | gl: the path the render host starts its outputs on. auto is gl on every platform this round; dxgi is
    /// experimental and explicit (a blown fuse forces gl, with words).</summary>
    public string Path { get; init; } = "auto";

    /// <summary>auto | core330 | es300: the GL dialect (ANGLE on Windows is es300).</summary>
    public string Dialect { get; init; } = "auto";

    /// <summary>auto: join the hardware lock when the group's leader applies it; off: free-run, measured.</summary>
    public string FrameLock { get; init; } = "auto";

    public int SwapGroup { get; init; } = 1;

    public int SwapBarrier { get; init; } = 1;
}"""),
    "Decode": ("DecodeSettings Decode", """public sealed record DecodeSettings
{
    public static readonly DecodeSettings Default = new();

    /// <summary>gpu | ffmpeg (the CPU path for Hap1/5/Y/A/M only).</summary>
    public string Hap { get; init; } = "gpu";

    /// <summary>0 = cores − 1.</summary>
    public int HapWorkers { get; init; }

    public int HapQueueFrames { get; init; } = 4;

    public int HapReadAheadMs { get; init; } = 250;

    public int HapReadAheadMb { get; init; } = 192;

    /// <summary>0 = no arena (the copy path).</summary>
    public int UploadArenaMb { get; init; } = 256;

    /// <summary>auto | egl | glx (Linux).</summary>
    public string GlApi { get; init; } = "auto";

    /// <summary>auto | direct | gpu-copy | copy-back | software.</summary>
    public string Policy { get; init; } = "auto";

    /// <summary>"" = the platform's order; vaapi | cuda | d3d11va | videotoolbox.</summary>
    public string Device { get; init; } = "";

    public int QueueFramesHw { get; init; } = 8;
}"""),
    "Optimise": ("OptimiseNodeSettings Optimise", """public sealed record OptimiseNodeSettings
{
    public static readonly OptimiseNodeSettings Default = new();

    /// <summary>auto (run jobs) | never (this node never encodes; it may still fetch).</summary>
    public string Role { get; init; } = "auto";

    public double TrickleGpuDuty { get; init; } = 0.15;

    public int TrickleDiskMbps { get; init; } = 50;

    public int PauseAfterDropS { get; init; } = 60;

    public int MinFreeGb { get; init; } = 10;

    public double MinPsnr { get; init; } = 30;

    public int IdleExitS { get; init; } = 120;
}"""),
    "VideoIo": ("VideoIoSettings VideoIo", """public sealed record VideoIoSettings
{
    public static readonly VideoIoSettings Default = new();

    public bool NdiEnabled { get; init; } = true;

    /// <summary>"" = the runtime's own search (its environment variable, the system paths); never shipped with PlayMap.</summary>
    public string NdiRuntimePath { get; init; } = "";

    public string NdiGroups { get; init; } = "";

    public string NdiDiscovery { get; init; } = "";

    public bool OmtEnabled { get; init; } = true;

    public string OmtDiscovery { get; init; } = "";

    public int OmtPortStart { get; init; } = 6400;

    public int OmtPortEnd { get; init; } = 6600;

    public bool SpoutEnabled { get; init; } = true;

    public int SpoutDiscoveryMs { get; init; } = 500;

    /// <summary>0 = 70 % of the measured link.</summary>
    public int MaxSendMbps { get; init; }

    /// <summary>"" = the default route's NIC.</summary>
    public string Interface { get; init; } = "";
}"""),
    "Control": ("ControlSettings Control", """public sealed record ControlSettings
{
    public static readonly ControlSettings Default = new();

    public bool InputEnabled { get; init; } = true;

    public bool OscEnabled { get; init; } = true;

    public int OscPort { get; init; } = ShowBusConstants.PlayMapOscPort;

    /// <summary>"" = the node's Bind; "127.0.0.1" = this machine only.</summary>
    public string OscBind { get; init; } = "";

    public ImmutableArray<string> OscAllow { get; init; } = [];

    public ImmutableArray<string> OscFeedback { get; init; } = [];

    public bool OscTimetags { get; init; } = true;

    public bool OscQueryEnabled { get; init; } = true;

    public int OscQueryPort { get; init; } = ShowBusConstants.PlayMapOscQueryPort;

    public bool MidiEnabled { get; init; } = true;

    /// <summary>"" = the OS default; winmm | alsa-seq | coremidi.</summary>
    public string MidiBackend { get; init; } = "";

    public ImmutableArray<string> MidiIgnore { get; init; } = [];

    /// <summary>Surface name → this machine's port glob.</summary>
    public ImmutableSortedDictionary<string, string> SurfacePorts { get; init; } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    /// <summary>DMX input → binding lines only. The Art-Net socket itself opens when any consumer needs it (DMX input, an ARTNET
    /// timecode source, LED output), so LED output never turns on control input.</summary>
    public bool ArtNetEnabled { get; init; }

    /// <summary>"" = all interfaces; the one Art-Net socket (control in, ArtTimeCode, LED out) binds this address on UDP 6454
    /// (tests give it their slot's loopback address).</summary>
    public string ArtNetInterface { get; init; } = "";

    public bool SacnEnabled { get; init; }

    public string SacnInterface { get; init; } = "";

    public ImmutableArray<string> DmxAllow { get; init; } = [];

    public int AxisHz { get; init; } = 100;

    public int FeedbackHz { get; init; } = 50;
}"""),
    "Companion": ("CompanionSettings Companion", """public sealed record CompanionSettings
{
    public static readonly CompanionSettings Default = new();

    /// <summary>Cached posters always; live output thumbnails at most 0.5 fps while armed.</summary>
    public bool ThumbsWhileArmed { get; init; } = true;

    public int MaxThumbsPerSession { get; init; } = 128;
}"""),
    "Fx": ("FxSettings Fx", """public sealed record FxSettings
{
    public static readonly FxSettings Default = new();

    public ImmutableArray<string> IsfFolders { get; init; } = [];

    public ImmutableArray<string> FfglFolders { get; init; } = [];

    /// <summary>FFGL plugins load only when this is on and the plugin is trusted (FX TRUST, this machine only).</summary>
    public bool FfglEnabled { get; init; }

    /// <summary>Plugin kind → "in-process" | "off" (unlisted = off).</summary>
    public ImmutableSortedDictionary<string, string> FfglTrust { get; init; } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    public double BudgetMs { get; init; } = 4.0;
}"""),
    "Audio": ("AudioNodeSettings Audio", """public sealed record AudioNodeSettings
{
    public static readonly AudioNodeSettings Default = new();

    /// <summary>This machine's input override ("" = the project's).</summary>
    public string Input { get; init; } = "";

    public string Channels { get; init; } = "";

    /// <summary>auto | ffmpeg | wasapi | none.</summary>
    public string Backend { get; init; } = "auto";

    /// <summary>Measured or entered input latency, ms; −1 = the backend's report.</summary>
    public double InputLatencyMs { get; init; } = -1;
}"""),
    "Content": ("ContentSettings Content", """public sealed record ContentSettings
{
    public static readonly ContentSettings Default = new();

    /// <summary>GPU ms per frame at 60 Hz before the governor advises (scaled by refresh).</summary>
    public double BudgetMs { get; init; } = 6.0;

    /// <summary>off | advise.</summary>
    public string Governor { get; init; } = "advise";

    public int MaxParticles { get; init; } = 4_194_304;
}"""),
    "Led": ("LedSettings Led", """public sealed record LedSettings
{
    public static readonly LedSettings Default = new();

    /// <summary>The machine master: this machine transmits LED data (off on a fresh node: fail closed).</summary>
    public bool Output { get; init; }

    public int MulticastTtl { get; init; } = 1;

    /// <summary>The bandwidth guard.</summary>
    public int MaxMbps { get; init; } = 200;
}"""),
    "Align": ("AlignSettings Align", """public sealed record AlignSettings
{
    public static readonly AlignSettings Default = new();

    public int RunsKept { get; init; } = 20;
}"""),
    "Phone": ("PhoneSettings Phone", """public sealed record PhoneSettings
{
    public static readonly PhoneSettings Default = new();

    /// <summary>Serve /scan and accept invites.</summary>
    public bool Enabled { get; init; } = true;

    public int InviteMinutes { get; init; } = 10;

    public int MaxConnected { get; init; } = 4;
}"""),
    "Wall": ("WallSettings Wall", """public sealed record WallSettings
{
    public static readonly WallSettings Default = new();

    public int CursorLeaseMs { get; init; } = 3000;

    public int OverlayLeaseMs { get; init; } = 15000;

    public int MaxCursors { get; init; } = 8;

    public int CalibrationTaskTimeoutS { get; init; } = 120;
}"""),
}

# The engine services each package declares (EngineServices.<Word>.cs): (usings, properties).
ENGINE_SERVICES = {
    "Show": (["System.Collections.Immutable"], [
        ("The show's gate: Show Mode's table, locks and the blind redirection (open until wp2-show sets it).",
         "IShowGate ShowGate { get; init; } = OpenShowGate.Instance;"),
        ("Every package's preflight checks; each adds its own in its extension's Configure.",
         "ImmutableArray<IPreflightCheck> PreflightChecks { get; init; } = [];"),
    ]),
    "Site": ([], [("A degrade gain per output that the air multiplies in (a stack that lost a member).", "IOutputGains? OutputGains { get; init; }")]),
    "Wall": ([], [("The calibration ledger align and stacks record their results in.", "ICalibrationLedger? Ledger { get; init; }")]),
    "Control": ([], [("The one Art-Net socket (DMX in, ArtTimeCode, LED out).", "PlayMap.Core.Dmx.IArtNetEndpoint? ArtNet { get; init; }")]),
    "Audio": ([], [
        ("Modulation sources for bindings.", "PlayMap.Core.Modulation.IModulationBus? Modulation { get; init; }"),
        ("The analysed audio frames.", "PlayMap.Core.Modulation.IAudioFrames? AudioFrames { get; init; }"),
        ("The capture hub: audio's parts configure before control's, and control resolves it at start (its own input when absent).",
         "PlayMap.Core.Audio.IAudioInputs? AudioInputs { get; init; }"),
    ]),
    "Sync": ([], [("The group's air across machines.", "IFleetFanout? Fanout { get; init; }")]),
    "VideoIo": (["System.Collections.Immutable", "PlayMap.Core.Media"], [
        ("Live input providers (NDI, OMT; Spout adds its own).", "ImmutableArray<ILiveInputProvider> LiveInputs { get; init; } = [];"),
        ("Network send providers.", "ImmutableArray<ISendProvider> SendProviders { get; init; } = [];"),
        ("GPU send providers (texture sharing).", "ImmutableArray<ISendSinkProvider> GpuSendProviders { get; init; } = [];"),
    ]),
    "Optimise": ([], [("Posters by content hash (THUMB reads them first).", "IPosterStore? Posters { get; init; }")]),
}

# The namespaces a stub's parameters need, by the types that need them (only the ones used are written).
ENGINE_USINGS = [
    ("System.Text.Json", ["Utf8JsonWriter"]),
    ("PlayMap.Core.Bus", ["ContextItem", "ContextLink"]),
    ("PlayMap.Core.Model", ["Output", "Playlist", "Projector", "Project", "ProjectStore"]),
    ("PlayMap.Core.Wire", ["VerbSpec", "WireLine", "SessionInfo", "CommandReply"]),
]


def write(rel, text):
    path = os.path.join(ROOT, rel)
    if os.path.exists(path):
        return False
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", rel)
    return True


def stub_body(pkg):
    return "    {\n        // wp2-%s fills this.\n    }\n" % pkg


def method(name, params, pkg, static=False):
    head = "    %spartial void %s(%s)\n" % ("static " if static else "", name, params)
    return head + stub_body(pkg)


def engine_hooks(word):
    out = []
    for template, params, words in ENGINE_HOOKS:
        if word in words:
            out.append((template.format(word), params))
    return out


def engine_core_file(word, pkg):
    hooks = engine_hooks(word)
    parts = [method(name, params, pkg) for name, params in hooks]
    if word == "Show":
        parts.append("    private partial ProjectStore? BlindStoreFor(SessionInfo session) =>\n"
                     "        null; // wp2-show fills this (wp2-blind: the session's draft).\n")
    text = " ".join(params for _, params in hooks) + (" ProjectStore SessionInfo" if word == "Show" else "")
    usings = "".join("using %s;\n" % u for u, types in ENGINE_USINGS if any(re.search(r"\b%s\b" % t, text) for t in types))
    return (usings + ("\n" if usings else "") + "namespace PlayMap.Engine;\n\n"
            "/// <summary>wp2-%s's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,\n"
            "/// checks and recovery facts. The package fills each body.</summary>\n"
            "public sealed partial class EngineCore\n{\n" % pkg
            + "\n".join(parts) + "}\n")


# Words whose EngineCore file is today's (EngineCore.Sync.cs, EngineCore.Content.cs): their hooks are appended to it.
HUNK_WORDS = ("Sync", "Content")


def hunk(word, pkg):
    hooks = engine_hooks(word)
    parts = [method(name, params, pkg) for name, params in hooks]
    return ("\n/// <summary>wp2-%s's hooks into the engine (EngineCore.Modules.cs). The package fills each body.</summary>\n"
            "public sealed partial class EngineCore\n{\n" % pkg + "\n".join(parts) + "}\n")


def engine():
    for word, pkg in WORDS:
        if word in HUNK_WORDS:
            path = os.path.join(ROOT, "src/PlayMap.Engine/EngineCore.%s.cs" % word)
            with open(path, encoding="utf-8") as f:
                text = f.read()
            if "Route%s(" % word not in text:
                for u, after in (("System.Text.Json", "using System"), ("PlayMap.Core.Bus", "using PlayMap")):  # Utf8JsonWriter, ContextItem
                    if "using %s;" % u not in text:
                        at = text.find(after)
                        text = text[:at] + "using %s;\n" % u + text[at:] if at >= 0 else "using %s;\n" % u + text
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text + hunk(word, pkg))
                print("appended the %s hooks to src/PlayMap.Engine/EngineCore.%s.cs" % (pkg, word))
        else:
            write("src/PlayMap.Engine/EngineCore.%s.cs" % word, engine_core_file(word, pkg))
        write("src/PlayMap.Engine/Host/EngineParts.%s.cs" % word,
              "namespace PlayMap.Engine.Host;\n\n"
              "/// <summary>wp2-%s's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>\n"
              "public sealed partial record EngineParts\n{\n%s}\n" % (pkg, method("Add%sParts" % word, "List<EngineExtensionFactory> extensions", pkg, static=True)))
        if word in ENGINE_SERVICES:
            usings, props = ENGINE_SERVICES[word]
            body = "\n".join("    /// <summary>%s</summary>\n    public %s\n" % (doc, p) for doc, p in props)
            write("src/PlayMap.Engine/EngineServices.%s.cs" % word,
                  "".join("using %s;\n" % u for u in usings) + ("\n" if usings else "")
                  + "namespace PlayMap.Engine;\n\n"
                  "/// <summary>wp2-%s's services: set by its extension's Configure, read by the packages named beside each.</summary>\n"
                  "public sealed partial record EngineServices\n{\n%s}\n" % (pkg, body))
    write("src/PlayMap.Engine/Host/EngineEntry.Optimise.cs",
          "namespace PlayMap.Engine.Host;\n\n"
          "/// <summary>wp2-optimise's entry points beside \"engine\" (its worker process).</summary>\n"
          "public static partial class EngineEntry\n{\n%s}\n" % method("DispatchOptimise", "string[] args, ref int? exit", "optimise", static=True))


def core():
    for word, pkg in WORDS:
        if word not in NODE_SETTINGS:
            continue
        prop, record = NODE_SETTINGS[word]
        typename, name = prop.split(" ")
        uses_immutable = "Immutable" in record
        write("src/PlayMap.Core/Bus/NodeSettings.%s.cs" % word,
              ("using System.Collections.Immutable;\n\n" if uses_immutable else "")
              + "namespace PlayMap.Core.Bus;\n\n"
              "/// <summary>wp2-%s's machine-local settings; an older settings file reads the defaults.</summary>\n"
              "public sealed partial record NodeSettings\n{\n    public %s %s { get; init; } = %s.Default;\n}\n\n"
              "%s\n" % (pkg, typename, name, typename, record))


def render():
    for word in RENDER_WORDS:
        pkg = PKG[word]
        parts = [method("Compose%s" % word, "EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs", pkg),
                 method("OnContext%s" % word, "RenderEngine renderer", pkg),
                 method("OnContextLost%s" % word, "", pkg)]
        write("src/PlayMap.Engine/Rendering/RenderHost.%s.cs" % word,
              "using PlayMap.Render.Engine;\n\nnamespace PlayMap.Engine.Rendering;\n\n"
              "/// <summary>wp2-%s's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the\n"
              "/// context exists and let go when it goes (render thread).</summary>\n"
              "public sealed partial class RenderHost\n{\n%s}\n" % (pkg, "\n".join(parts)))
    for word in SHADER_WORDS:
        pkg = PKG[word]
        write("src/PlayMap.Render/Shaders/ShaderLibrary.%s.cs" % word,
              "namespace PlayMap.Render.Shaders;\n\n"
              "/// <summary>wp2-%s's programs, by name (sources carry no #version and no precision line).</summary>\n"
              "public static partial class ShaderLibrary\n{\n%s}\n"
              % (pkg, method("Add%s" % word, "Dictionary<string, (string Vertex, string Fragment)> programs", pkg, static=True)))


# The packages that ship a STATE fixture and a card descriptor (each engine package; a continuation adds to its parent's).
FIXTURE_PACKAGES = [pkg for _, pkg in WORDS]


def fixtures():
    for pkg in FIXTURE_PACKAGES:
        write("tests/fixtures/state/%s.json" % pkg, "{}\n")
        write("tests/fixtures/state/%s.cards.json" % pkg, "[]\n")


# The packages with a block in the catalogue, by the name their block's comment gives, and the STATE keys each adds.
STATE_KEYS = {
    "show": "top level `show`; `air.master`, `air.panic`",
    "fleet": "top level `fleet`, `eye`; `sync` extras (`epoch` by wp2-backup)",
    "backup": "`sync.epoch` (with wp2-fleet's file)",
    "output": "`outputs[].latencyMs`, `present`, `lock`; `sync.latencyMs`, `sync.lock`; `machine.present`",
    "d3d11": "(adds to wp2-output's keys)",
    "sync": "`sync.group`, `feed`, `slewing`, `steps`, `freewheelS`, `outputs`, `pairs`, `verdict`, `words`",
    "decode": "top level `decode`; `machine.gl`",
    "zerocopy": "(adds to wp2-decode's keys)",
    "optimise": "`media.optimise`",
    "videoio": "top level `videoio`",
    "control": "top level `control`, `timecode`; `playlists[].chase`",
    "companion": "top level `clients`, `events`, `selection`; `project.index`",
    "fx": "top level `fx`; `outputs[].fx`",
    "audio": "top level `audio`, `modulation`",
    "content": "top level `content`",
    "led": "top level `led`",
    "facade": "`stage.site`, `controls`, `surveys`, `stacks`; `stage.projectors[].uncertainty`, `priors`, `stack`",
    "align": "top level `align`; `outputs[].measured`",
    "phonescan": "top level `phones`",
    "wall": "top level `calibration`, `onboarding`; `air.overlay`; `stage.projectors[].grade`, `quads`",
    "blind": "(adds to wp2-show's keys)",
    "resolve": "(adds to wp2-wall's and wp2-align's keys)",
    "multiscan": "(adds to wp2-phonescan's keys)",
    "stacks": "(adds to wp2-facade's keys)",
}


def catalogue_blocks():
    """Each package's rows from VerbCatalog.cs: a block starts at a comment naming wp2-<pkg> and ends at the next blank line."""
    path = os.path.join(ROOT, "src/PlayMap.Core/Wire/VerbCatalog.cs")
    with open(path, encoding="utf-8") as f:
        lines = f.read().split("\n")
    blocks = {}
    current = None
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("//"):
            m = re.search(r"\(wp2-([a-z0-9-]+)", stripped)
            if m:
                current = m.group(1)
                blocks.setdefault(current, [])
            continue
        if stripped == "":
            current = None
            continue
        if current and stripped.startswith("V2("):
            m = re.match(r'V2\("([^"]+)",\s*"((?:[^"\\]|\\.)*)",\s*"((?:[^"\\]|\\.)*)",\s*VerbCategory\.(\w+),\s*VerbRisk\.(\w+),\s*(true|false)', stripped)
            if m:
                blocks[current].append(m.groups())
    return blocks


def docs():
    blocks = catalogue_blocks()
    for pkg in sorted(set(blocks) | set(STATE_KEYS)):
        rows = blocks.get(pkg, [])
        table = "\n".join("| `%s` | `%s` | %s | %s | %s |" % (n, s.replace("|", "\\|"), w.replace("|", "\\|"), r.lower(), "yes" if m == "true" else "no")
                          for n, s, w, _, r, m in rows) or "| — | — | (no rows of its own) | | |"
        write("docs/wire/%s.md" % pkg,
              "# wp2-%s on the wire\n\n"
              "*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is\n"
              "answered \"not available on this node yet\" until the package routes it, and `VERBS` marks it `\"available\"` once it is.*\n\n"
              "## Verbs\n\n| Verb | Syntax | Words | Risk | Changes the show |\n|---|---|---|---|---|\n%s\n\n"
              "## STATE\n\n%s.\n\n## Papers note\n\n(What shipped, for the integration docs pass.)\n"
              % (pkg, table, STATE_KEYS.get(pkg, "none")))


SECTIONS = {"engine": engine, "core": core, "render": render, "fixtures": fixtures, "docs": docs}

if __name__ == "__main__":
    wanted = sys.argv[1:] or list(SECTIONS)
    for name in wanted:
        SECTIONS[name]()
