#!/usr/bin/env python3
"""Writes the round-3 packages' stub files (docs/round3/PLAN.md §2.3) and their wire pages.

A file is written only when it is absent, so a package's filled stub or page is never overwritten; the script is kept for the
spine amendment lane (a new hook or a new package word adds a line to the tables below and the script writes the new stubs).
Every empty body holds one comment line, "// wp3-<pkg> fills this.", so an unimplemented partial method and an empty method
body both pass the analyzers. The hooks are declared, and called in their order, by the spine's files (EngineCore.Modules.cs,
Rendering/RenderHost.cs, Host/EngineParts.cs, Host/EngineEntry.cs).

Usage: python3 tools/gen_stubs3.py [section ...]   (sections: engine, core, render, docs; none = all)
"""

import os
import re
import sys
import textwrap

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

W = "Utf8JsonWriter w"
CTX = "List<ContextItem> items, List<ContextLink> links"
VAL = "Project p, object entity, ref string? why"

# EngineCore stub files: (file under src/PlayMap.Engine, package, summary, hooks). A hook is (name, parameters), or
# (name, parameters, return type, the value it answers until the package fills it).
ENGINE_CORE = [
    ("EngineCore.Machine.cs", "platform", "its verbs, the machine block's identity, capabilities, platform and locked keys, and the context's machine item", [
        ("RouteMachine", "VerbRoutes routes"), ("MachineMachine", W), ("ContextMachine", CTX)]),
    ("Install/EngineCore.Install.cs", "installer", "the machine block's install key, from the install's registry key and the supervisor's status file", [
        ("MachineInstall", W)]),
    ("EngineCore.Gpu.cs", "d3d12-core", "its verbs, STATE's render block and the render device's context items", [
        ("RouteGpu", "VerbRoutes routes"), ("StateGpu", W), ("ContextGpu", CTX)]),
    ("EngineCore.Plan.cs", "d3d12-passes", "STATE's render.graph, written inside the render block", [
        ("RenderPlan", W)]),
    ("EngineCore.Observe.cs", "stability", "its verbs, STATE's observe block, each output's observed key, the observed outputs' context items and its checks", [
        ("RouteObserve", "VerbRoutes routes"), ("StateObserve", W), ("OutputObserve", W + ", Output o"), ("ContextObserve", CTX),
        ("ValidateObserve", VAL)]),
    ("EngineCore.Shaders.cs", "shaders", "its verbs (FX RELEASE), the fx block's quarantine and the compile lane's worker", [
        ("RouteShaders", "VerbRoutes routes"), ("FxCompileShaders", W), ("FxShaders", W)]),
    ("EngineCore.Undo.cs", "desk-ux", "its verbs", [
        ("RouteUndo", "VerbRoutes routes")]),
    ("EngineCore.Preview.cs", "desk-gpu", "STATE's previews block and VIEW … SHARED's answer", [
        ("StatePreview", W), ("ViewShared", "List<string> args, SessionInfo session, ref CommandReply? reply")]),
    ("EngineCore.Handles.cs", "desk-map", "its verbs and the overlay's handle latency", [
        ("RouteHandles", "VerbRoutes routes"), ("OverlayHandles", W)]),
    ("EngineCore.Showing.cs", "desk-map", "what each screen shows: the outputs' showing key, the media's shownOn key and the words the context's output items carry", [
        ("OutputShowing", W + ", Output o"), ("MediaShowing", W),
        ("ShowingWords", "Output output", "string", '""')]),
    ("EngineCore.Canvas.cs", "canvas", "its verbs, the outputs' hand key and its checks", [
        ("RouteCanvas", "VerbRoutes routes"), ("OutputCanvas", W + ", Output o"), ("ValidateCanvas", VAL)]),
    ("EngineCore.Guide.cs", "workflows", "its verbs, STATE's guides block, the context's guide items and its checks", [
        ("RouteGuide", "VerbRoutes routes"), ("StateGuide", W), ("ContextGuide", CTX), ("ValidateGuide", VAL)]),
    ("EngineCore.Capture.cs", "capture", "its verbs, the outputs' passThrough key, the live inputs' capture and latency keys and its checks", [
        ("RouteCapture", "VerbRoutes routes"), ("OutputCapture", W + ", Output o"), ("InputCapture", W + ", LiveInput input"),
        ("ValidateCapture", VAL)]),
]

# EngineServices stub files: (word, package).
ENGINE_SERVICES = [("Machine", "platform"), ("Observe", "stability"), ("Shaders", "shaders"), ("Guide", "workflows"), ("Capture", "capture")]

# Host/EngineParts stub files: (word, package).
ENGINE_PARTS = [("Machine", "platform"), ("Plan", "d3d12-passes"), ("Observe", "stability"), ("Shaders", "shaders"), ("Preview", "desk-gpu"),
                ("Capture", "capture")]

# Host/EngineEntry stub files: (file word, method, package, what the process is).
ENGINE_ENTRY = [
    ("Observer", "DispatchObserver", "stability", "the air observer's own process"),
    ("ShaderWorker", "DispatchShaderWorker", "shaders", "the shader worker's own process, where user effects compile"),
]

# Rendering/RenderHost stub files: (word, package).
RENDER_HOST = [("Gpu", "d3d12-core"), ("Plan", "d3d12-passes"), ("Preview", "desk-gpu"), ("Canvas", "canvas"), ("Capture", "capture")]

# Core/Bus/NodeSettings stub files: (word, package).
NODE_SETTINGS = [("Machine", "platform"), ("Gpu", "d3d12-core"), ("Observe", "stability"), ("Capture", "capture")]

# The namespaces a stub's parameters need, by the types that need them (only the ones used are written).
ENGINE_USINGS = [
    ("System.Text.Json", ["Utf8JsonWriter"]),
    ("PlayMap.Core.Bus", ["ContextItem", "ContextLink"]),
    ("PlayMap.Core.Model", ["Output", "Project"]),
    ("PlayMap.Core.Wire", ["SessionInfo", "CommandReply"]),
    ("PlayMap.Engine.VideoIo", ["LiveInput"]),
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


def method(hook, pkg, static=False):
    if len(hook) == 4:
        name, params, returns, value = hook
        return "    internal partial %s %s(%s) =>\n        %s; // wp3-%s fills this.\n" % (returns, name, params, value, pkg)
    name, params = hook
    return ("    %spartial void %s(%s)\n    {\n        // wp3-%s fills this.\n    }\n"
            % ("static " if static else "", name, params, pkg))


def owner(pkg):
    """The package's name as an owner: "wp3-canvas's", "wp3-shaders'"."""
    return "wp3-%s'" % pkg if pkg.endswith("s") else "wp3-%s's" % pkg


def summary(text):
    """A doc comment's summary, wrapped as the tree's are (at most 132 characters a line, the indent included)."""
    lines = textwrap.wrap("<summary>" + text + "</summary>", width=128, break_long_words=False, break_on_hyphens=False)
    return "".join("/// %s\n" % line for line in lines)


def usings_for(text):
    return "".join("using %s;\n" % u for u, types in ENGINE_USINGS if any(re.search(r"\b%s\b" % t, text) for t in types))


def engine():
    for rel, pkg, what, hooks in ENGINE_CORE:
        params = " ".join(h[1] for h in hooks)
        usings = usings_for(params)
        write("src/PlayMap.Engine/" + rel,
              usings + ("\n" if usings else "") + "namespace PlayMap.Engine;\n\n"
              + summary("%s hooks into the engine (EngineCore.Modules.cs): %s. The package fills each body." % (owner(pkg), what))
              + "public sealed partial class EngineCore\n{\n%s}\n" % "\n".join(method(h, pkg) for h in hooks))
    for word, pkg in ENGINE_SERVICES:
        write("src/PlayMap.Engine/EngineServices.%s.cs" % word,
              "namespace PlayMap.Engine;\n\n"
              "/// <summary>%s services: set by its extension's Configure, read by the packages named beside each.</summary>\n"
              "public sealed partial record EngineServices\n{\n    // wp3-%s fills this.\n}\n" % (owner(pkg), pkg))
    for word, pkg in ENGINE_PARTS:
        write("src/PlayMap.Engine/Host/EngineParts.%s.cs" % word,
              "namespace PlayMap.Engine.Host;\n\n"
              "/// <summary>%s parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>\n"
              "public sealed partial record EngineParts\n{\n%s}\n"
              % (owner(pkg), method(("Add%sParts" % word, "List<EngineExtensionFactory> extensions"), pkg, static=True)))
    for word, name, pkg, what in ENGINE_ENTRY:
        write("src/PlayMap.Engine/Host/EngineEntry.%s.cs" % word,
              "namespace PlayMap.Engine.Host;\n\n"
              "/// <summary>%s entry point beside \"engine\": %s.</summary>\n"
              "public static partial class EngineEntry\n{\n%s}\n"
              % (owner(pkg), what, method((name, "string[] args, ref int? exit"), pkg, static=True)))


def core():
    for word, pkg in NODE_SETTINGS:
        write("src/PlayMap.Core/Bus/NodeSettings.%s.cs" % word,
              "namespace PlayMap.Core.Bus;\n\n"
              "/// <summary>%s machine-local settings; an older settings file reads the defaults.</summary>\n"
              "public sealed partial record NodeSettings\n{\n    // wp3-%s fills this.\n}\n" % (owner(pkg), pkg))


def render():
    for word, pkg in RENDER_HOST:
        hooks = [("Compose%s" % word, "EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs"),
                 ("OnContext%s" % word, "RenderEngine renderer"), ("OnContextLost%s" % word, "")]
        write("src/PlayMap.Engine/Rendering/RenderHost.%s.cs" % word,
              "using PlayMap.Render.Engine;\n\nnamespace PlayMap.Engine.Rendering;\n\n"
              "/// <summary>%s hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the\n"
              "/// context exists and let go when it goes (render thread).</summary>\n"
              "public sealed partial class RenderHost\n{\n%s}\n" % (owner(pkg), "\n".join(method(h, pkg) for h in hooks)))


# Each wire page: (file under docs/wire, the packages whose catalogue blocks it lists, in order, the extended rows' forms those
# packages own, the STATE keys and context items they add). A continuation's rows sit on its parent's page (it owns the parent's
# files); a page is named by the package's hook word where it has one.
PAGES = [
    ("machine", ["platform"], [], "`machine.identity`, `machine.capabilities`, `machine.platform`, `machine.locked`; the context's machine item"),
    ("install", ["installer"], [], "`machine.install`"),
    ("shaders", ["shaders", "shaders-2"], [], "`fx.compile` and `fx.quarantine`, inside wp2-fx's `fx` block"),
    ("gpu", ["d3d12-core"], [], "top level `render`; `machine.present.api` reads \"d3d12\" on this backend"),
    ("plan", ["d3d12-passes", "d3d12-passes-2"], [], "`render.graph` (d3d12-passes), `render.passes` (d3d12-passes-2)"),
    ("observe", ["stability", "stability-2"], [], "top level `observe`; `render.removals` (stability-2)"),
    ("present", ["present"], ["`PRESENT PATH D3D12` (the row's D3D12 path)"], "inside the existing `outputs[].present` and `machine.present` (wp2-output's)"),
    ("undo", ["desk-ux"], [], "none"),
    ("preview", ["desk-gpu"], ["`VIEW … SHARED <pid>`"], "top level `previews`"),
    ("handles", ["desk-map"], ["`WARP NODE <output> <c> <r> TO|BY <x> <y> PX`", "`HANDLE SELECT <pin>`, `… WARP …` and `… MASK …`"],
     "`outputs[].showing`, `media.shownOn`, `air.overlay.handleP95Ms`; the context's output items carry the showing words"),
    ("canvas", ["canvas"], ["`PATTERN <kind>|OFF ON <canvas>`"], "`outputs[].hand`"),
    ("guide", ["workflows"], [], "top level `guides`; the context's `guide` items"),
    ("capture", ["capture"], ["`INPUT DISCOVER CAPTURE`", "`INPUT ADD` of a `capture:` URI with `MODE <w>x<h>@<rate>` and `FORMAT UYVY|V210|NV12|AUTO`"],
     "`outputs[].passThrough`, `videoio.inputs[].capture`, `videoio.inputs[].latency`"),
]


def catalogue_blocks():
    """Each package's rows from VerbCatalog.cs: a block starts at a comment naming wp3-<pkg> and ends at the next blank line."""
    path = os.path.join(ROOT, "src/PlayMap.Core/Wire/VerbCatalog.cs")
    with open(path, encoding="utf-8") as f:
        lines = f.read().split("\n")
    blocks = {}
    current = None
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("//"):
            m = re.search(r"\(wp3-([a-z0-9-]+)", stripped)
            if m and current is None:
                current = m.group(1)
                blocks.setdefault(current, [])
            continue
        if stripped == "":
            current = None
            continue
        if current and stripped.startswith("V3("):
            m = re.match(r'V3\("([^"]+)",\s*"((?:[^"\\]|\\.)*)",\s*"((?:[^"\\]|\\.)*)",\s*VerbCategory\.(\w+),\s*VerbRisk\.(\w+),\s*(true|false)', stripped)
            if m:
                blocks[current].append(m.groups())
        elif current:
            current = None
    return blocks


def cell(text):
    return text.replace("|", "\\|")


def docs():
    blocks = catalogue_blocks()
    for page, packages, extended, state in PAGES:
        rows = [(pkg, row) for pkg in packages for row in blocks.get(pkg, [])]
        table = "\n".join("| `%s` | `%s` | %s | %s | %s | wp3-%s |" % (n, cell(s), cell(w), r.lower(), "yes" if m == "true" else "no", pkg)
                          for pkg, (n, s, w, _, r, m) in rows) or "| — | — | (no rows of its own) | | | |"
        forms = "\n".join("- %s" % f for f in extended) if extended else "None."
        write("docs/wire/%s.md" % page,
              "# %s on the wire\n\n"
              "*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is\n"
              "answered \"not available on this node yet\" until the package routes it, and `VERBS` marks it `\"available\"` once it is;\n"
              "a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*\n\n"
              "## Verbs\n\n| Verb | Syntax | Words | Risk | Changes the show | Package |\n|---|---|---|---|---|---|\n%s\n\n"
              "## Forms added to existing rows\n\n%s\n\n"
              "## STATE\n\n%s.\n\n## Papers note\n\n(What shipped, for the integration docs pass.)\n"
              % (", ".join("wp3-" + p for p in packages), table, forms, state))


SECTIONS = {"engine": engine, "core": core, "render": render, "docs": docs}

if __name__ == "__main__":
    wanted = sys.argv[1:] or list(SECTIONS)
    for name in wanted:
        SECTIONS[name]()
