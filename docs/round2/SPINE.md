# PlayMap round 2: the spine

*What lands on `main` (branch `wp2-spine`, cut from `main`'s head when it starts — read at `cb3cfc0`; `main` has since gained `d7d7862` and `7a01783`, which touch `Media/Frames/FramePool.cs`, three Media sources and one line of `ScanRunner.cs` away from its `Store.Apply` site, so every hunk below applies unchanged) before any round-2 package starts. It is the shared code:
model records, the catalogue lines, the seams, the hooks each package plugs into, the node's settings, the STATE and
context contract, and the Show Bus/Eye contract. It is written as exact C# so that the twenty-eight packages of `PLAN.md` can
be built in parallel without editing each other's files. It lands in two sessions: **W0a** (§0–§4: model, Core types,
catalogue, risk, syntax) and **W0b** (§5–§11: engine, render, network, settings, stubs, the tree). The package list, scopes
and waves are in `PLAN.md`; this file is the contract they all build against.*

*Second cut, after review: `RiskWhen` only raises risk; a row's alternatives are read by one `VerbSyntax`; the live-video
contracts land here; the air path (gain, hold, override, `uAir`) and the shader dialect convention land in W0b, so no package
edits another's renderer file; `GlCaps` reads extensions per dialect; vendored code lives in `src/ThirdParty` outside the fences;
a node host without Avalonia; `IIndexContributor`, `IFleetFanout` and the `Sync` hook word.*

*Read against `main` at `cb3cfc0` (13 Oct 2026): `src/PlayMap.Core/Model/*`, `Wire/VerbCatalog.cs`, `Wire/Endpoints.cs`,
`Bus/{Pairing,ShowBus,ShowContext}.cs`, `Engine/{Seams,EngineCore*,StateWriter,ContextBuilder,NodeHome}.cs`,
`Engine/Host/{EngineParts,EngineExtension,HostViews}.cs`, `Engine/Rendering/RenderHost.cs`, `Render/Engine/{Contracts,
RenderEngine}.cs`, `Render/Shaders/ShaderLibrary.cs`, `Net/Wire/WireSession.cs`, `Net/Contracts.cs`; the 22 design specs in
`r2/design/` and the research in `r2/research/`.*

---

## 0. Rules every package keeps

| # | Rule | Why |
|---|---|---|
| R1 | **New fields on existing records are init properties with defaults** (the `Stage.Devices` precedent), never new positional parameters. New records may be positional. Old project files load unchanged; a re-save adds the new keys ("every list written"). | Several packages add to `Output`, `Surface`, `Projector`, `PlaylistItem`: trailing positional parameters from two packages cannot both land. |
| R2 | A missing or `null` nested record reads as its type's `Default`/`Empty`/`None` (§2.5's `ProjectJson` rule) unless the property is declared nullable (`T?`), which stays `null`. | `"show": null` in a hand-edited file must not crash a render thread. |
| R3 | **One owner per file.** The spine creates every shared file and one *stub file per package* for each hook; after the spine, a file belongs to exactly one package (`PLAN.md` lists them). A package touching another's file does so only through a hunk named in `PLAN.md`. | Parallel builds, clean merges. |
| R4 | **Verbs are routed through `VerbRoutes`** (§5.1) from the package's own `EngineCore.<Pkg>.cs`, never by editing `EngineCore.Router.cs`'s switch. STATE sections, context items, ticks, frame inputs, render passes, shader programs, push topics and host parts likewise have one hook per package. | The router, `StateWriter`, `ContextBuilder`, `RenderHost`, `ShaderLibrary` and `EngineParts` stay spine-owned. |
| R5 | **Edit handlers write through `Edits`**, never `Store.Apply/Undo/Redo` (§5.3). Transport, air, show state, locks and runtime facts are not edits and use `Store`/`SetAir` as today. | Blind edit redirects every document edit of a blind session to its draft without touching other packages' handlers. |
| R6 | **Every line passes the gate** (`GateLine`, §5.2) before its handler: pairing → Show Mode policy → (on apply) the lock guard. The gate is open until `wp2-show` lands. | One door for the desk, Patterns, Companion, OSC, MIDI bindings, MCP and the assistant. |
| R7 | Verb names are one or two words (`VerbCatalog.Match` reads two). Sub-commands are arguments. Every mutating verb refuses a no-op with words. No new verb carries a secret; the only new secret is the Companion REST key (next round, `CREDENTIAL SET companion`). | Doctrine: attempts are not facts; fail closed. |
| R8 | **STATE**: a package writes only its own keys, at the end of their object, in the order §9 fixes. **Context links use only the Eye's eleven verbs** (§10.2). Lights follow the evidence rule (unknown is grey). | Patterns' Eye drops other link kinds; raw-substring readers. |
| R9 | **Fixtures**: every package that adds STATE keys ships `tests/fixtures/state/<pkg>.json`, written by one of its own engine tests from a real scenario and golden-checked there. The desk package reads these, never hand-made shapes (unless marked "fixture from spec"). | The desk cannot drift from the engine. |
| R10 | Module fences: Core references only the runtime; edges reference Core and their native library; only App references Avalonia; analyzers apply to tests (no empty catch, no `System.Random`, invariant culture, no `Thread.Sleep`/`DateTime.Now`/`Task.Wait` in `src/`, structs as `record struct`, dispose what you own). Comments state invariants; no model or tool identifiers. Vendored third-party source lives only under `src/ThirdParty/**` (its own projects, analyzers off, §11) and is referenced, never copied into a fenced project. | CLAUDE.md. |
| R11 | **Program sources carry no `#version` and no precision line.** `GlDevice.CreateProgram` prepends the context's dialect header and refuses a source that holds one (§6.3). | Generators, effects and `ShaderLibrary` compile on core 3.3 and on ES 3.00 from one source. |
| R12 | **`RiskWhen` only raises risk on lines of class Air, Transport, Panic or DevicePower**, and every new row of class Air or Panic is High (§4.1). | An assistant or an MCP client never blacks out the air or closes a shutter without a person. |
| R13 | **Every package that adds STATE keys ships a card descriptor** `tests/fixtures/state/<pkg>.cards.json` (title, STATE path, light, words, fields, action lines) beside its fixture; **every package that adds an entity a controller can act on contributes an INDEX section** through `IIndexContributor` (§5.4). | The desk and Companion show every package without per-package code in either. |

---

## 1. One owner per concept (the overlaps between the designs, resolved)

| Concept | Overlap in the designs | Decision (owner package) |
|---|---|---|
| **Control bindings vs modulation** | control's `Binding` (input → line) vs effects/audio/content's `ParamBinding` (source → parameter); `FX BIND`, `GENERATOR BIND`, `MOD BIND` | Two concepts, two names. **`ControlBinding`** (`bd…`, input words → a wire line; writes the project through the line, undoable, journalled per gesture) is `wp2-control`'s. **`ParamBinding`** (a modulation source → a numeric parameter, evaluated per frame, never written to the project) is one record (§2.3 `ModulationModel.cs`); its verbs `MOD BIND/UNBIND/SOURCES/BYPASS/PEEK` are `wp2-audio`'s; `FX BIND`, `FX UNBIND`, `GENERATOR BIND`, `GENERATOR UNBIND` are **aliases** of `MOD BIND`/`MOD UNBIND` (the entity id says what is bound). `wp2-fx` and `wp2-content` evaluate bindings on their own render paths through `IModulationBus`. MIDI/OSC/DMX as *modulation sources* are next round (control registers an `IModSourceProvider`). |
| **Show Mode vs locks vs risk** | showsafety's armed table, locks, `RiskOf`; companion/firstfive/optimise each defined an `IShowGate` | Four layers, one order, one owner each: **pairing** (exists) → **`ShowPolicy`** (role × verb show class × armed; `wp2-show`) → **locks** (`LockGuard` inside `ProjectStore.Apply/Undo/Redo`; `wp2-show`) → **risk** (`VerbCatalog.RiskOf(line)`, spine; decides only *who must approve an assistant's line*, never what an armed node refuses; this round `RiskWhen` only raises it on air-moving classes, R12). One `IShowGate` (§5.4) with an open default; everything that asked for a gate reads it. A controller's press shape (instant, hold, double) is `VerbParams`' (wp2-companion), from the show class first: Panic, Air and Transport rows are instant. |
| **Blind edit** | `Store.Apply` sites in 32 handlers across packages | Spine introduces `Edits` (§5.3) and converts every edit site now; `wp2-blind` (show's continuation, W2) owns the draft store, sets `_lineStore`, shows the draft in an editor view (`EngineSnapshot.Draft`) and commits at boundaries. |
| **Panic, master, degrade gains** | showsafety `IAirLevels`, facade `IOutputGains`, LED `LED LEVEL`, effects `FX BYPASS ALL` | `wp2-show` owns `PANIC`, `MASTER` and `IAirLevels` (per-output gain = master × panic × degrade, the safe-look override, the hold, and the air's sound gain = master × panic). The spine plumbs all of it through `RenderEngine` and `OutputStage` (§6.1), so no package edits the renderer for it; wp2-blind puts clip sound on air under that gain; wp2-sync fans a leader's PANIC, CLEAR, BLACKOUT and MASTER out to its group (`IFleetFanout`). Facade's stack failover (next round) supplies a degrade gain through `IOutputGains` (§5.4), which `AirLevels` multiplies in. `LED LEVEL` is the LED rig's own master (LED is not an output); panic outcome `FX-OFF` runs `FX BYPASS ALL ON`. |
| **Health vs the Eye vs preflight** | fleet `HEALTH`/`FLEET`/`EYE`, showsafety `PREFLIGHT`, context ordering | `wp2-fleet` owns `HEALTH` (one node's machine record), `FLEET` (the board over every node's HEALTH, `EYE` aliases in Patterns' `EyeJson` shape, with focus and lenses) and the **context's order and links** (`ShapeContext`, §10); it also prepares the Patterns-side P1–P3 change as a patch outside `patterns-svc` (§10.3). `wp2-show` owns `PREFLIGHT` (readiness checks with fix lines); **every engine package adds at least one check** through `IPreflightCheck`. |
| **Sync roles, anchors, clock** | framelock F1/F2 vs fleet M9 (both change `NetworkClock`); present vs framelock (vblank) | `wp2-sync` owns the **network** side: anchors pushed to followers, persisted roles (`NodeSettings.Sync`), clock slew/step/hold/promote, the PMVB vblank reports on its own clock-sync socket and the sync board's verdict, and the group's air fan-out (`IFleetFanout`). `wp2-output` owns the **display** side: vblank truth and the lock machine with its drivers. `wp2-backup` (fleet's continuation) owns the takeover verbs (`FAILOVER`, `FAILBACK`, `FLEET PROMOTE`), which call `NetworkClock.Promote()`. |
| **Stacks, measured correction, calibration history** | facade `StackGroup` + `MeasuredCorrection`; autoalign stacks, `ALIGN ACCEPT/REVERT/history`; calibops snapshots/fallback/history | **One ledger**: `CalibrationSnapshot` (`cs…`) records any calibration result for a **projector** (pose and optics) or an **output** (its `MeasuredCorrection`); fallback/accept/recall/history/forget are `wp2-wall`'s `CALIBRATION …` verbs. `ALIGN ACCEPT/REVERT` stage and accept output snapshots through the ledger (`ICalibrationLedger`, §5.4). **Stacks** (`StackGroup`, `STACK …`) are `wp2-facade`'s; flat-screen alignment (`wp2-align`) fits every output to the canvas target, so stacked outputs converge without a group. `MeasuredCorrection` is one record (§2.3), written by align (warp/blend/colour) and facade (stack lattice/gains). Align's `MeasuredStage` draws the grid, blend, pedestal, gains and γ; facade's stack lattice is drawn as the first layer of `WarpField`/`WarpMeshBuilder` (Core), so it reaches the air through the existing warp mesh with no shader of align's. |
| **Explainable score** | calibops `CalibrationScore`/`ScoreTerm` (px), autoalign score items (points), phonescan view score | One record pair, `QualityScore` + `ScoreTerm` (§2.3), grades **A/B/C/F** everywhere; `Unit` says whether the value is px-equivalents (lower is better) or points (higher is better); `Source` says what made it (pins, scan, align, stack). `wp2-wall` owns the term vocabulary (`ScoreTermCodes`); phonescan and align fill their terms. Every term carries a fix line that parses with `VerbCatalog.Match`. |
| **Video I/O spine** | texshare `VideoSend(SendKind, Format, RateDivider)`, `LiveInputPolicy`, `STATE.inputs/sends/videoio`, `SEND ADD <name> SPOUT…`; netvideo `VideoSend(SendProtocol, Fps)`, `LiveSettings`, `STATE.videoio.*`, `SEND ADD NDI <name>…` | One model (§2.3 `VideoIoModel.cs`): `VideoSend` with `SendProtocol {Ndi, Omt, Spout, Syphon}`, one `SendFormat`, `Fps` *and* `RateDivider`; one live policy `LiveSettings` on stream items. **Protocol word first**: `SEND ADD NDI|OMT|SPOUT <name> <tap> …`. STATE under **`videoio.{ndi,omt,spout,inputs,sends,discovered,bandwidth}`** only. The contracts (`LiveUri`, `ILiveInputProvider`, `ISendProvider`, `ISendSinkProvider`, `IGpuSendTarget`) land in the spine (§3.8) so NDI/OMT (`wp2-videoio`) and Spout (`wp2-d3d11`, on the D3D11 device it makes) build in parallel; videoio owns the file afterwards and the engine side; Spout registers its provider in videoio's lists. |
| **Art-Net port 6454** | control (DMX in, ArtTimeCode), LED (ArtDmx/ArtSync out), timecode | One socket per node, `IArtNetEndpoint` (§3.6), created by `wp2-control` (lands first) and handed to LED through `EngineServices.ArtNet`; bound to the configured address and opened when any consumer needs it (DMX input, an ARTNET timecode source, LED output). sACN likewise for receive; LED opens its own send sockets. |
| **MIDI, audio inputs, timecode feeds** | control MIDI backends + `ITimecodeInput`; timecode `IMidiPorts`, `IAudioInputs`; audio `IAudioInputs` + hub | Timecode chase is inside `wp2-control`, so MIDI and MTC are one package. `IAudioInputs` (§3.4) is spine-owned: `wp2-control` (LTC) and `wp2-audio` (analysis) both consume it; `wp2-audio` owns the hub, the FFmpeg libavdevice backend and WASAPI (NAudio.Wasapi, inside the one `net10.0` TFM). Audio's host parts register before control's (§5.5), and control resolves the hub at start. |
| **Tempo** | audio `TEMPO …` vs content S4 `TEMPO SET/TAP` | `wp2-audio` owns the beat clock and every `TEMPO` verb. |
| **mDNS** | companion `_playmap._tcp`, control `_oscjson._tcp`, LED `_wled._tcp` browse | One responder in `PlayMap.Net/Discovery`, owned by `wp2-companion`, announcing `_playmap._tcp` **this round** (the module's `bonjour-device` field lists the node). Browsing (`_oscjson._tcp`, `_wled._tcp`) on the same code is next round; LED finds WLED by address and Art-Net nodes by ArtPoll. |
| **Words that collide** | facade `CONTROL IMPORT/CHECK` vs control `CONTROL …`; two `SEND ADD` orders; calibops task id `ct` vs control surface `ct` | Facade's survey control points are **`SITE POINTS`** and **`SITE CHECK`**. `SEND ADD <protocol> <name> <tap>`. Calibration task ids are `kt…`. Node-local runtime ids (never project ids): `pf` preflight report, `oj` optimise job, `ar` align run, `kt` calibration task, `ph` phone, `sg` suggestion, `dp` decode probe, `tk` fleet takeover. |
| **Typed verb parameters** | companion `VerbParams` (shared with control, desk) | `wp2-companion` owns `src/PlayMap.Core/Wire/VerbParams.cs` (types and table); desk and control read it. |
| **Events** | companion `EVENT` stream; every package names events | Spine adds `EngineCore.RaiseEvent` (§5.6). `wp2-companion` owns the ring, `EVENTS` and the push. Packages raise their events from day one. |
| **Ephemeral and gesture lines** | firstfive `Ephemeral`/`Gesture`, control `Gesture` | One pair of flags on `VerbSpec` (§4.1): **`Ephemeral`** (never journalled, never moves STATE's revision: `CURSOR`, `HANDLE SELECT`, `FLEET FOCUS`, `SELECT`) and **`Gesture`** (journalled once per gesture by its owner's coalescing: `SURFACE CORNER`, `SURFACE NODE`, `HANDLE NUDGE`, `NUDGE`). |
| **Internal verbs** | fleet `MIRROR`, `FLEET FENCE` | `VerbSpec.Internal`: kept out of HELP's list, MCP tools and the context's commands; refused unless the session's transport is `fleet` or `system`. |
| **GPU device and surfaces** | present `GpuApi {None, D3D11, OpenGl}` + `GpuDeviceShare`; zerocopy `GpuApi {None, D3D11, DmaBuf, IoSurface, Cuda}` + `IGpuShare`; texshare `IGpuShare` | One `GpuApi` and one `IGpuShare` (§3.2, owner `wp2-decode` after the spine), made by the render platform (`wp2-output` for GL/EGL, `wp2-d3d11` for the D3D11 device) and handed to decoders by `IMediaBackend.UseGpu` and to Spout. |
| **INDEX for controllers** | companion's INDEX lists the base kinds; every package adds entities a controller acts on (effect slots, LED, inputs and sends, calibration targets, peers, proposals) | `IIndexContributor` (§5.4), registered from each package's Route hook: rows and a preset-section hint as data ("menus as data", Patterns' approach). `wp2-companion` writes INDEX from the project and every contributor; the module builds a section per hint. |

---
## 2. Core model (`src/PlayMap.Core/Model`)

Every type below is in `namespace PlayMap.Core.Model;` unless a block says otherwise. JSON is the project's
(camel-case, enums as camel-case words). The owner column says which package owns the file after the spine.

### 2.1 Ids (`Ids.cs`, spine-owned)

```csharp
    /// <summary>A viewpoint content is projected from (the literal "vp" used by ADD until now).</summary>
    public const string Viewpoint = "vp";
    /// <summary>A pin (the literal "pn" used by ADD until now).</summary>
    public const string Pin = "pn";
    public const string ControlSurface = "ct";   // wp2-control
    public const string ControlBinding = "bd";   // wp2-control
    public const string TimecodeSource = "tc";   // wp2-control
    public const string Send = "sd";             // wp2-videoio
    public const string Fx = "fx";               // wp2-fx: an effect slot
    public const string FxAsset = "fa";          // wp2-fx: an ISF file carried with the show
    public const string GeneratorPreset = "gp";  // wp2-content
    public const string LedNode = "ln";          // wp2-led
    public const string LedFixture = "lf";       // wp2-led
    public const string LedType = "lt";          // wp2-led
    public const string MediaVersion = "mv";     // wp2-optimise
    public const string ControlPoint = "cp";     // wp2-facade
    public const string Survey = "sv";           // wp2-facade
    public const string Stack = "sk";            // wp2-facade
    public const string Align = "al";            // wp2-align
    public const string Calibration = "cs";      // wp2-wall
    public const string FleetMember = "fm";      // wp2-fleet

    /// <summary>Node-local runtime ids (never project ids, never in a project file): a preflight report, an optimise job,
    /// an align run, a calibration task, a phone, a scan suggestion, a decode probe, a fleet takeover.</summary>
    public static readonly IReadOnlyList<string> RuntimePrefixes = ["pf", "oj", "ar", "kt", "ph", "sg", "dp", "tk"];
```

### 2.2 Additions to existing records (init properties; spine-owned files)

```csharp
// Project.cs — record body
    /// <summary>Show Mode rules, panic plans and locks (wp2-show).</summary>
    public ShowPlan Show { get; init; } = ShowPlan.Default;                          // "show"
    /// <summary>Who backs up whom and who leads next (wp2-fleet).</summary>
    public FleetPlan Fleet { get; init; } = FleetPlan.Default;                       // "fleet"
    /// <summary>Controllers and their bindings (wp2-control).</summary>
    public ControlSet Control { get; init; } = ControlSet.Empty;                     // "control"
    /// <summary>Timecode sources (wp2-control).</summary>
    public TimecodeModel Timecode { get; init; } = TimecodeModel.Empty;              // "timecode"
    /// <summary>Pictures this show sends: NDI, OMT, Spout (wp2-videoio).</summary>
    public ImmutableArray<VideoSend> Sends { get; init; } = [];                       // "sends"
    /// <summary>ISF effects carried with the show, by content hash (wp2-fx).</summary>
    public ImmutableArray<FxAsset> FxAssets { get; init; } = [];                      // "fxAssets"
    /// <summary>Generator looks saved in the show (wp2-content).</summary>
    public ImmutableArray<GeneratorPreset> GeneratorPresets { get; init; } = [];      // "generatorPresets"
    /// <summary>What the show listens to (wp2-audio).</summary>
    public AudioSettings Audio { get; init; } = AudioSettings.Default;               // "audio"
    /// <summary>The show's beat clock settings (wp2-audio).</summary>
    public TempoSettings Tempo { get; init; } = TempoSettings.Default;               // "tempo"
    /// <summary>LED pixel mapping (wp2-led).</summary>
    public LedRig Leds { get; init; } = LedRig.Empty;                                // "leds"
    /// <summary>Optimise-on-import mode and this show's own rules (wp2-optimise).</summary>
    public OptimiseSettings Optimise { get; init; } = OptimiseSettings.Default;      // "optimise"

    public VideoSend? FindSend(string? id) => Sends.Find(id);
    public ControlBinding? FindBinding(string? id) => Control.Bindings.Find(id);
    public ControlSurface? FindControlSurface(string? id) => Control.Surfaces.Find(id);
    public TimecodeSource? FindTimecodeSource(string? id) => Timecode.Sources.Find(id);
    public GeneratorPreset? FindGeneratorPreset(string? id) => GeneratorPresets.Find(id);
    public StackGroup? FindStack(string? id) => Stage.Stacks.Find(id);
    public AlignGroup? FindAlign(string? id) => Stage.Aligns.Find(id);
    public CalibrationSnapshot? FindCalibration(string? id) => Stage.Calibrations.Find(id);
    /// <summary>An effect slot by id with the entity whose chain holds it (an item, layer, canvas, surface or output).</summary>
    public (FxSlot Slot, string OwnerId, int Index)? FindFx(string? id)
    {
        if (id is null)
            return null;
        static (FxSlot, string, int)? In(FxChain chain, string owner, string id)
        {
            var i = chain.Slots.IndexOfId(id);
            return i < 0 ? null : (chain.Slots[i], owner, i);
        }
        foreach (var l in Playlists)
            foreach (var it in l.Items)
                if (In(it.Fx, it.Id, id) is { } a) return a;
        foreach (var c in Canvases)
        {
            if (In(c.Fx, c.Id, id) is { } b) return b;
            foreach (var ly in c.Layers)
                if (In(ly.Fx, ly.Id, id) is { } d) return d;
        }
        foreach (var sf in Stage.Surfaces)
            if (In(sf.Fx, sf.Id, id) is { } e) return e;
        foreach (var o in Outputs)
            if (In(o.Fx, o.Id, id) is { } f) return f;
        return null;
    }

// Project.AllIds() yields, after today's ids: Sends; Control.Surfaces; Control.Bindings; Timecode.Sources; FxAssets;
// GeneratorPresets; every FxSlot of every chain; Leds.Nodes, Leds.Types, Leds.Fixtures; every MediaVersion;
// Stage.Controls, Stage.Surveys, Stage.Stacks, Stage.Aligns, Stage.Calibrations; Fleet.Members.

// ProjectSettings — record body
    /// <summary>The sync group's air latency, ms; −1 = the largest any member reports (wp2-output).</summary>
    public double SyncLatencyMs { get; init; } = -1;                                 // "syncLatencyMs"
    /// <summary>Calibration results kept per projector or output besides current, fallback, staged and pinned (wp2-wall).</summary>
    public int CalibrationKeep { get; init; } = 20;                                  // "calibrationKeep" 1–100
    /// <summary>A re-solve applies itself only when its score ≤ max(gate, 1.25 × the fallback's) (wp2-wall).</summary>
    public double CalibrationGate { get; init; } = 1.5;                              // "calibrationGate"

// MediaItem — record body
    /// <summary>How to read the item's colour when its own tags are missing or wrong (HAP HDR has none) (wp2-decode).</summary>
    public ColorOverride Color { get; init; } = ColorOverride.Auto;                  // "color"
    /// <summary>Derived files of this item (wp2-optimise).</summary>
    public ImmutableArray<MediaVersion> Versions { get; init; } = [];                 // "versions"
    /// <summary>"auto" (the best version this node plays, else the source), "source", or a version id (wp2-optimise).</summary>
    public string Use { get; init; } = "auto";                                       // "use"
    /// <summary>A live input's pacing, loss policy and backup; stream items only (wp2-videoio).</summary>
    public LiveSettings? Live { get; init; }                                         // "live"

// Output — record body
    /// <summary>The display's input lag, ms (light leaves the lens this long after the vblank); 0 = unknown (wp2-output).</summary>
    public double LatencyMs { get; init; }                                           // "latencyMs" 0–500
    /// <summary>Effects on the output's picture before its warp (wp2-fx).</summary>
    public FxChain Fx { get; init; } = FxChain.Empty;                                // "fx"
    /// <summary>A camera-measured correction drawn under the operator's warp (wp2-align, wp2-facade).</summary>
    public MeasuredCorrection? Measured { get; init; }                               // "measured"
    /// <summary>The ledger's current, fallback and staged results for this output (wp2-wall).</summary>
    public string? CalibrationId { get; init; }                                      // "calibrationId"
    public string? FallbackId { get; init; }                                         // "fallbackId"
    public string? StagedId { get; init; }                                           // "stagedId"

// Playlist — record body
    /// <summary>Set: the playlist follows timecode (wp2-control).</summary>
    public TimecodeChase? Chase { get; init; }                                       // "chase"

// PlaylistItem — record body
    /// <summary>The timecode the item starts at, as the cue sheet writes it ("01:00:00:00", ';' for drop frame) (wp2-control).</summary>
    public string? TcIn { get; init; }                                               // "tcIn"
    public FxChain Fx { get; init; } = FxChain.Empty;                                // "fx" (wp2-fx)

// Generator — record body (wp2-content unless noted)
    /// <summary>Leader ns its own time counts from; 0 = the leader epoch (files before round 2).</summary>
    public long OriginNs { get; init; }                                              // "originNs"
    /// <summary>Modulation of its parameters (wp2-audio's verbs; wp2-content's renderers read them).</summary>
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];                 // "bindings"
    /// <summary>A look crossfade in progress (inert once over).</summary>
    public ParamMorph? Morph { get; init; }                                          // "morph"
    /// <summary>Pictures it reads: "source" (an emitter or fill), "flow", "matte".</summary>
    public ImmutableSortedDictionary<string, SourceRef> Inputs { get; init; } =
        ImmutableSortedDictionary.Create<string, SourceRef>(StringComparer.Ordinal); // "inputs"

// Layer — record body
    public FxChain Fx { get; init; } = FxChain.Empty;                                // "fx" (wp2-fx)
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];                 // "bindings" (opacity, rect.*) (wp2-audio)

// Canvas — record body
    public FxChain Fx { get; init; } = FxChain.Empty;                                // "fx" (wp2-fx)

// Surface — record body
    public FxChain Fx { get; init; } = FxChain.Empty;                                // "fx" (wp2-fx)
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];                 // "bindings" (opacity) (wp2-audio)
    /// <summary>Set: a quad drawn in one projector's raster (MeshId "", Transform and Faces unused) (wp2-wall).</summary>
    public PixelQuad? Pixel { get; init; }                                           // "pixel"

// Projector — record body
    /// <summary>What a solve may assume about the lens and the position (wp2-facade).</summary>
    public SolvePriors Priors { get; init; } = SolvePriors.None;                     // "priors"
    /// <summary>The last solve's uncertainty on the wall and along the axis (wp2-facade); null before a solve.</summary>
    public SolveUncertainty? Uncertainty { get; init; }                              // "uncertainty"
    /// <summary>The ledger's current, fallback and staged results (wp2-wall).</summary>
    public string? CalibrationId { get; init; }                                      // "calibrationId"
    public string? FallbackId { get; init; }                                         // "fallbackId"
    public string? StagedId { get; init; }                                           // "stagedId"

// CalibrationPin — record body
    /// <summary>The surveyed control point this pin's world point comes from; "" = picked on a mesh (wp2-facade).</summary>
    public string ControlId { get; init; } = "";                                     // "controlId"

// CameraRig — record body (wp2-phonescan)
    /// <summary>What the phone behind a Phone rig is (null for other kinds).</summary>
    public PhoneProfile? Phone { get; init; }                                        // "phone"
    /// <summary>How a Folder rig takes its pictures (null for other kinds).</summary>
    public FolderCapture? Folder { get; init; }                                      // "folder"

// Stage — record body
    public SiteFrame Site { get; init; } = SiteFrame.None;                           // "site"        (wp2-facade)
    public ImmutableArray<ControlPoint> Controls { get; init; } = [];                 // "controls"    (wp2-facade)
    public ImmutableArray<SurveySource> Surveys { get; init; } = [];                  // "surveys"     (wp2-facade)
    public ImmutableArray<StackGroup> Stacks { get; init; } = [];                     // "stacks"      (wp2-facade)
    public ImmutableArray<AlignGroup> Aligns { get; init; } = [];                     // "aligns"      (wp2-align)
    public ImmutableArray<CalibrationSnapshot> Calibrations { get; init; } = [];      // "calibrations"(wp2-wall)

// DeviceLink — record body
    /// <summary>The projector's own processing as read before PlayMap reset it (wp2-align).</summary>
    public NativeSnapshot? Snapshot { get; init; }                                   // "snapshot"
```

### 2.3 New model files

**`ShowModel.cs`** (owner wp2-show)

```csharp
public enum PanicOutcome { Black, Run, Rewind, Next, Freeze, Safe, Shutter, FxOff, Hard }
public enum LockKind { Entity, Area }

/// <summary>The areas a lock may cover, by name (an armed show adds mapping and layout implicitly).</summary>
public static class LockAreas
{
    public const string Mapping = "mapping", Layout = "layout", Content = "content", Playlists = "playlists",
        Media = "media", Devices = "devices", Network = "network";
    public static readonly IReadOnlyList<string> All = [Mapping, Layout, Content, Playlists, Media, Devices, Network];
}

/// <summary>What PANIC does on one output: the outcome, its fade, and the safe look SAFE dips to.</summary>
public sealed record OutputPanicPlan(string OutputId, PanicOutcome Outcome, int FadeMs, SourceRef? SafeLook);

/// <summary>A lock on an entity id or an area name, who set it, when and why.</summary>
public sealed record ShowLock(string Target, LockKind Kind, string By, long AtUnixMs, string Words);

/// <summary>The show's rules for Show Mode, GO protection, panic and blind commits.</summary>
public sealed record ShowRules
{
    public static readonly ShowRules Default = new();
    public int MinGoMsArmed { get; init; } = 300;
    public int MinGoMsRehearsal { get; init; }
    /// <summary>Armed, paired remote clients may only read (QLab's "view only in Show Mode").</summary>
    public bool RemoteViewOnlyWhenArmed { get; init; }
    /// <summary>"paired": any paired operator or remote may DISARM; "desk": the desk only.</summary>
    public string DisarmFrom { get; init; } = "paired";
    public PanicOutcome DefaultOutcome { get; init; } = PanicOutcome.Black;
    public int DefaultFadeMs { get; init; } = 1000;
    /// <summary>A panic and a blind commit land this far ahead on the show clock, so mirrors and followers fade on the same frames.</summary>
    public int PanicLeadMs { get; init; }
    public int CommitLeadMs { get; init; } = 34;
    /// <summary>Warn (amber) when a playlist plays while the show is not armed.</summary>
    public bool ExpectArmed { get; init; }
}

public sealed record ShowPlan
{
    public static readonly ShowPlan Default = new();
    public ShowRules Rules { get; init; } = ShowRules.Default;
    public ImmutableArray<OutputPanicPlan> Panic { get; init; } = [];
    public ImmutableArray<ShowLock> Locks { get; init; } = [];
}
```

**`FleetModel.cs`** (owner wp2-fleet)

```csharp
public enum FleetRole { Primary, Backup }
/// <summary>Round 2 has hot backups only (a full render of the primary's show); warm and cold are later values.</summary>
public enum BackupMode { Hot }

/// <summary>A machine in the show's fleet as the primary recorded it when the backup joined.</summary>
public sealed record FleetMember(string Id, string NodeId, string Name, FleetRole Role, string? BackupOf, BackupMode Mode,
    int TimeoutMs) : IEntity;

public sealed record FleetPlan
{
    public static readonly FleetPlan Default = new();
    public ImmutableArray<FleetMember> Members { get; init; } = [];
    /// <summary>Node ids in the order they take the lead of the sync group when its leader goes quiet; [] = nobody promotes.</summary>
    public ImmutableArray<string> Succession { get; init; } = [];
}
```

**`ControlModel.cs`** (owner wp2-control)

```csharp
/// <summary>The show's control map: the controllers it names and the bindings from their inputs to wire lines. Which port
/// a surface is on this machine is the node's (NodeSettings.Control.SurfacePorts).</summary>
public sealed record ControlSet
{
    public static readonly ControlSet Empty = new();
    public ImmutableArray<ControlSurface> Surfaces { get; init; } = [];
    public ImmutableArray<ControlBinding> Bindings { get; init; } = [];
}

/// <param name="Name">The word input lines use ("APC40"): letters, digits, '-', '_', 1–32 characters, unique.</param>
/// <param name="Kind">"midi", "osc", "artnet" or "sacn".</param>
/// <param name="Match">midi: port-name glob; osc: sender address[:port]; artnet/sacn: source address or CID; "" = any.</param>
/// <param name="Profile">A controller profile id ("mackie-control"); "" = generic.</param>
/// <param name="FeedbackTo">midi: output-port glob ("" = the input's name); osc: host:port ("" = the sender's).</param>
public sealed record ControlSurface(string Id, string Name, string Kind, string Match, string Profile, string FeedbackTo,
    bool Enabled) : IEntity
{
    /// <summary>Equal-priority DMX sources: "ltp" or "htp"; a lost DMX source leaves "hold" or "release".</summary>
    public string Merge { get; init; } = "ltp";
    public string OnLoss { get; init; } = "hold";
    public bool AcceptPreview { get; init; }
}

/// <param name="Input">Input words: "MIDI APC40 CC 1 48", "OSC /fader/1", "ARTNET 0 101+102", "SACN 7 101".</param>
/// <param name="Target">A wire line; "*" carries the value ("SET sf… opacity *"), "#*" an item number, "*%" percent.</param>
public sealed record ControlBinding(string Id, string Name, string Input, string Target, ControlShape Shape,
    ControlFeedback Feedback, bool Enabled) : IEntity;

public enum ControlBehaviour { Auto, Trigger, Toggle, Momentary, Value, Velocity, Absolute, Relative }
public enum ControlCurve { Linear, Exponential, Logarithmic, SCurve, Steps }
public enum TakeoverMode { Auto, None, PickUp, ValueScaling }
public enum RelativeCoding { Auto, TwosComplement, BinaryOffset, SignedBit, SignedBit2, Increment }
public enum FeedbackMode { Auto, Off, Value, Lamp }

public sealed record ControlShape
{
    public static readonly ControlShape Default = new();
    public ControlBehaviour Behaviour { get; init; } = ControlBehaviour.Auto;
    public double InMin { get; init; }
    public double InMax { get; init; } = 1;
    /// <summary>null = the target's own range.</summary>
    public double? OutMin { get; init; }
    public double? OutMax { get; init; }
    public bool Invert { get; init; }
    public ControlCurve Curve { get; init; } = ControlCurve.Linear;
    public double CurveAmount { get; init; } = 2;
    public TakeoverMode Takeover { get; init; } = TakeoverMode.Auto;
    public RelativeCoding Relative { get; init; } = RelativeCoding.Auto;
    /// <summary>0 = range ÷ 128 per tick.</summary>
    public double Step { get; init; }
    public double Acceleration { get; init; } = 0.5;
    public bool Wrap { get; init; }
    public string PressValue { get; init; } = "";
    public string ReleaseValue { get; init; } = "";
    public int HoldMs { get; init; }
    public int DebounceMs { get; init; } = 30;
}

public sealed record ControlFeedback
{
    public static readonly ControlFeedback Default = new();
    public FeedbackMode Mode { get; init; } = FeedbackMode.Auto;
    /// <summary>"" = the same control; else output words ("MIDI APC40 NOTE 1 53", "OSC 10.0.0.9:9000 /led/1").</summary>
    public string To { get; init; } = "";
    /// <summary>"" = inferred from the target; else a STATE predicate ("air.blackout").</summary>
    public string When { get; init; } = "";
    public string On { get; init; } = "";
    public string Off { get; init; } = "";
}
```

**`TimecodeModel.cs`** (owner wp2-control)

```csharp
public enum TimecodeKind { Ltc, Mtc, ArtNet, Internal }
public enum ChaseMode { Locked }
public enum ChaseLoss { Hold, Stop, Continue }
public enum ChaseGap { Black, Hold }

/// <summary>A timecode input: where code arrives and how it is read.</summary>
public sealed record TimecodeSource(string Id, string Name, TimecodeKind Kind) : IEntity
{
    /// <summary>ltc: audio input key ("" = default; "file:/path.wav" loops a file); mtc: MIDI port key; artNet/internal: "".</summary>
    public string Device { get; init; } = "";
    /// <summary>ltc: 1-based input channel; artNet: StreamId 0–255.</summary>
    public int Channel { get; init; } = 1;
    /// <summary>"auto" | "23.976" | "24" | "25" | "29.97df" | "29.97" | "30" | "50" | "59.94df" | "59.94" | "60".</summary>
    public string Format { get; init; } = "auto";
    /// <summary>Which node opens it ("" = the node that writes the chase).</summary>
    public string NodeId { get; init; } = "";
    /// <summary>Input path latency: code arrives this late.</summary>
    public long LatencyNs { get; init; }
    public double FloorDbfs { get; init; } = -46;
    public int FitWindowMs { get; init; } = 1500;
}

/// <summary>How a playlist follows timecode. Engagement is runtime (kept in the recovery record), not the document.</summary>
public sealed record TimecodeChase(ImmutableArray<string> Sources)
{
    public ChaseMode Mode { get; init; } = ChaseMode.Locked;
    /// <summary>+ = the picture is shown later than its timecode.</summary>
    public long DelayNs { get; init; }
    public ChaseLoss OnLoss { get; init; } = ChaseLoss.Hold;
    /// <summary>How long a dropout is ridden through on the last line (0–10 s).</summary>
    public long FreewheelNs { get; init; } = 500_000_000;
    /// <summary>0 = auto: max(2 frames, 5σ).</summary>
    public long JumpNs { get; init; }
    public ChaseGap Gap { get; init; } = ChaseGap.Black;
}

public sealed record TimecodeModel
{
    public static readonly TimecodeModel Empty = new();
    public ImmutableArray<TimecodeSource> Sources { get; init; } = [];
}
```

**`VideoIoModel.cs`** (owner wp2-videoio)

```csharp
public enum LivePacing { Latest, Smooth, Locked }
public enum LiveLoss { Hold, Black, Fallback }
public enum LiveDepth { Auto, Eight, High }

/// <summary>How a live input (an NDI, OMT or Spout stream item) is received and what it shows when it fails.</summary>
public sealed record LiveSettings
{
    public static readonly LiveSettings Default = new();
    public LivePacing Pacing { get; init; } = LivePacing.Latest;
    /// <summary>NDI lowest bandwidth / OMT 1/8 preview.</summary>
    public bool Proxy { get; init; }
    public LiveDepth Depth { get; init; } = LiveDepth.Auto;
    /// <summary>Keep it connected while it is in the project.</summary>
    public bool Warm { get; init; } = true;
    /// <summary>A second live URI taken over on loss; "" = none.</summary>
    public string Backup { get; init; } = "";
    public LiveLoss OnLoss { get; init; } = LiveLoss.Hold;
    /// <summary>SourceRef text shown on loss with Fallback (never a live or stream source).</summary>
    public string Fallback { get; init; } = "";
    /// <summary>0 = three periods of the input's own rate.</summary>
    public int StaleMs { get; init; }
    public int LostMs { get; init; } = 1000;
    /// <summary>Steady frames before leaving a fallback.</summary>
    public int RecoverMs { get; init; } = 2000;
    /// <summary>Tell the sender when it is on air.</summary>
    public bool Tally { get; init; } = true;
    /// <summary>OMT suggested quality: default | low | medium | high.</summary>
    public string Quality { get; init; } = "default";
}

public enum SendProtocol { Ndi, Omt, Spout, Syphon }
public enum SendFormat { Auto, Uyvy, Uyva, P216, Pa16, Bgra8, Rgba8, Rgb10A2, Rgba16F }
public enum SendFit { Fit, Fill, Stretch }

/// <summary>A picture this node sends: a tap of one point of the pipeline, packed and announced under a name.</summary>
/// <param name="Tap">"output:op…" (final), "prewarp:op…", "canvas:cv…", "source:&lt;sourceref&gt;".</param>
/// <param name="Width">0 = the tap's own size.</param>
/// <param name="Fps">0 = the tap's rate (an output's RefreshHz, else the show rate).</param>
public sealed record VideoSend(string Id, string Name, SendProtocol Protocol, string Tap, int Width, int Height, double Fps,
    SendFormat Format, bool Enabled) : IEntity
{
    /// <summary>Publish every n-th frame (Spout).</summary>
    public int RateDivider { get; init; } = 1;
    /// <summary>The name other apps see; "" = "PlayMap &lt;node&gt; - &lt;name&gt;" (Spout) or the protocol's own "HOST (name)".</summary>
    public string PublishedName { get; init; } = "";
    /// <summary>Blackout and freeze apply to canvas and source taps too.</summary>
    public bool FollowAir { get; init; } = true;
    /// <summary>OMT sender quality; NDI refuses anything but "default".</summary>
    public string Quality { get; init; } = "default";
    /// <summary>NDI groups ("" = public).</summary>
    public string Groups { get; init; } = "";
    public SendFit Fit { get; init; } = SendFit.Fit;
    /// <summary>Pack and read back only while a receiver is connected.</summary>
    public bool OnlyWhenWatched { get; init; } = true;
}
```

**`ModulationModel.cs`** (owner wp2-audio; read by wp2-fx and wp2-content)

```csharp
public enum BindingMode { Add, Scale, Replace, Rate }
public enum BindingCurve { Linear, Ease, Step, Exponential, Logarithmic, SCurve }

/// <summary>A modulation source driving one numeric parameter, evaluated per frame on the render thread; the project keeps the
/// base value and is never written per frame. Source names: "audio:bass~10/250", "beat:pulse", "lfo:sine/4b/0", "sting".</summary>
public sealed record ParamBinding(string Param, string Source, double Amount, double Offset, BindingCurve Curve, double SmoothSeconds)
{
    public BindingMode Mode { get; init; } = BindingMode.Add;
    /// <summary>−1: SmoothSeconds both ways.</summary>
    public double AttackSeconds { get; init; } = -1;
    public double ReleaseSeconds { get; init; } = -1;
    /// <summary>REPLACE: fractions of the parameter's range.</summary>
    public double RangeMin { get; init; }
    public double RangeMax { get; init; } = 1;
    public bool Invert { get; init; }
    /// <summary>0 = continuous.</summary>
    public int Steps { get; init; }
    /// <summary>RATE: the leader ns the integral starts at (stamped by the verb).</summary>
    public long AnchorNs { get; init; }
    public bool Enabled { get; init; } = true;
}
```

**`FxModel.cs`** (owner wp2-fx)

```csharp
public enum FxBlend { Normal, Add, Multiply, Screen, Difference }
public enum FxFormat { Builtin, Isf, Ffgl }

/// <summary>One effect in a chain: a kind from the FX catalogue ("fx.blur", "isf:fa1b2c3d4e", "ffgl:RM01") with its parameters,
/// how its result mixes over its input, at what render scale, and the show-time origin its TIME counts from.</summary>
/// <param name="Mix">0..1.</param><param name="Scale">0.25..1.</param><param name="OriginNs">0 = the owner's origin.</param>
public sealed record FxSlot(string Id, string Kind, string Name, ParamSet Params, bool Bypass, double Mix, FxBlend Blend,
    double Scale, long OriginNs) : IEntity
{
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];
}

public sealed record FxChain(ImmutableArray<FxSlot> Slots)
{
    public static readonly FxChain Empty = new([]);
    public bool IsEmpty => Slots.IsDefaultOrEmpty;
}

/// <summary>An ISF file carried with the project, content-addressed and fetched by hash like media.</summary>
public sealed record FxAsset(string Id, string Name, FxFormat Format, string Path, string ContentHash) : IEntity;
```

**`ContentModel.cs`** (owner wp2-content)

```csharp
public enum MorphCurve { Linear, Ease }

/// <summary>A crossfade from <see cref="From"/> to the generator's own Params, on the show clock.</summary>
public sealed record ParamMorph(ParamSet From, long StartNs, long LengthNs, MorphCurve Curve);

/// <summary>A look saved in the show: a kind and its full parameter set.</summary>
public sealed record GeneratorPreset(string Id, string Name, string Kind, ParamSet Params) : IEntity;
```

**`AudioModel.cs`** (owner wp2-audio)

```csharp
/// <summary>What the show listens to; a node may override the device (NodeSettings.Audio).</summary>
public sealed record AudioSettings
{
    public static readonly AudioSettings Default = new();
    /// <summary>"" off · "default" · "system" · "test:click|sweep|noise|drums" · a device name.</summary>
    public string Input { get; init; } = "";
    /// <summary>"1", "1+2" (summed), "3-4", "M", "S".</summary>
    public string Channels { get; init; } = "1+2";
    public double GainDb { get; init; }
    public bool AutoGain { get; init; } = true;
    public double GateDb { get; init; } = -60;
    /// <summary>0 = not shown; the acoustic delay is d ÷ 343 m/s.</summary>
    public double ListenerDistanceM { get; init; }
}

public sealed record TempoSettings
{
    public static readonly TempoSettings Default = new();
    /// <summary>"manual" | "detect".</summary>
    public string Source { get; init; } = "manual";
    public double Bpm { get; init; } = 120;
    public int BeatsPerBar { get; init; } = 4;
    public int BarsPerPhrase { get; init; } = 8;
    public double DetectMinBpm { get; init; } = 75;
    public double DetectMaxBpm { get; init; } = 180;
}
```

**`LedModel.cs`** (owner wp2-led)

```csharp
public enum LedProtocol { ArtNet, Sacn, Ddp }
public enum LedSync { None, Latch }
public enum LedSpace { Canvas, Output, Source, Surface, World }
public enum LedShape { Line, Grid, Ring, Points }
public enum LedStart { TopLeft, TopRight, BottomLeft, BottomRight }
public enum LedSampling { Point, Area }
public enum LedChannel { R, G, B, W, WW, CW, A, UV, L, Dimmer, Constant, Gap }

/// <summary>A receiver of pixel data: an Art-Net node, an sACN receiver, a DDP/WLED controller.</summary>
/// <param name="Host">Address or name; "" = sACN multicast.</param><param name="Port">0 = the protocol's default.</param>
/// <param name="RateHz">0 = the protocol's default.</param><param name="LatencyMs">Node + network + driver, to land with the projection.</param>
/// <param name="UniverseChannels">Slots used per universe when auto-patching (default 510).</param>
public sealed record LedNode(string Id, string Name, LedProtocol Protocol, string Host, int Port, bool Enabled, double RateHz,
    double LatencyMs, LedSync Sync, int UniverseChannels, string Color) : IEntity
{
    public int SacnPriority { get; init; } = 100;
    public int SacnSyncUniverse { get; init; }
    public bool SacnMulticast { get; init; } = true;
    public bool FollowBlackout { get; init; } = true;
    public string Vendor { get; init; } = "";
}

public sealed record LedChannelSlot(LedChannel Channel, int Bits, int Value);

/// <summary>A fixture type: the channels before, per and after its pixels. Built-in types ("builtin:grb", "builtin:rgbw"…) are
/// answered by the LED package and never stored.</summary>
public sealed record LedFixtureType(string Id, string Name, ImmutableArray<LedChannelSlot> Head, ImmutableArray<LedChannelSlot> PerPixel,
    ImmutableArray<LedChannelSlot> Tail, double WattsPerPixel, string Origin, string Manufacturer) : IEntity;

public sealed record LedLayout(LedShape Shape, int Count, int Rows, Vec3d Size, LedStart Start, bool ColumnsFirst, bool Snake,
    ImmutableArray<Vec3d> Points);

/// <param name="Universe">Art-Net port-address 0–32767; sACN 1–63999; DDP 0.</param><param name="Address">1–512 (DDP: 1-based byte).</param>
public sealed record LedPatch(string NodeId, int Universe, int Address);

public sealed record LedColor(double Brightness, double GainR, double GainG, double GainB, double GainW, double ContentGamma,
    double OutputGamma)
{
    public static readonly LedColor Neutral = new(1, 1, 1, 1, 1, 2.2, 2.2);
}

public sealed record LedFixture(string Id, string Name, string TypeId, LedSpace Space, string SpaceId, Transform3 Placement,
    LedLayout Layout, LedPatch? Patch, LedSampling Sampling, double Footprint, LedColor Color, bool Enabled, bool Locked,
    bool Reverse, string EditorColor, ImmutableArray<string> Tags) : IEntity
{
    /// <summary>World fixtures sampled through a viewpoint.</summary>
    public string? ViewpointId { get; init; }
    /// <summary>Metres: the nearest-surface binding radius for world fixtures.</summary>
    public double BindDistance { get; init; } = 0.25;
}

public sealed record LedRig
{
    public static readonly LedRig Empty = new();
    public ImmutableArray<LedNode> Nodes { get; init; } = [];
    public ImmutableArray<LedFixtureType> Types { get; init; } = [];
    public ImmutableArray<LedFixture> Fixtures { get; init; } = [];
}
```

**`OptimiseModel.cs`** (owner wp2-optimise)

```csharp
public enum EncodeQuality { Fast, Show, Archive }
public enum OptimiseMode { Auto, Ask, Never }
public enum RuleMode { Inherit, Auto, Ask, Never }

/// <summary>A derived file of a library item: same content, another codec, content-addressed; the source is never changed.</summary>
/// <param name="To">"hap", "hap-alpha", "hap-q", "hap-q-alpha", "rechunk".</param>
/// <param name="Recipe">The canonical recipe JSON (sorted keys, invariant).</param>
/// <param name="Key">"ok:" + 32 hex of (source hash, recipe, encoder version).</param>
public sealed record MediaVersion(string Id, string To, string Recipe, string Key, string ContentHash, long SizeBytes, MediaInfo Info,
    VersionQuality Quality, string Encoder, string MadeOn, long CreatedUnixMs) : IEntity
{
    /// <summary>Below the quality floor: never picked by "auto" until a person names it.</summary>
    public bool Review { get; init; }
}

public sealed record VersionQuality(double PsnrAvg, double PsnrMin, int Sampled, long WorstAtMs)
{
    public static readonly VersionQuality None = new(0, 0, 0, 0);
}

public sealed record OptimiseMatch
{
    public ImmutableArray<string> Codecs { get; init; } = [];
    public ImmutableArray<MediaKind> Kinds { get; init; } = [];
    public bool? Alpha { get; init; }
    public bool? Hdr { get; init; }
    public int MinBitDepth { get; init; }
    public int MinWidth { get; init; }
    public bool? CpuDecode { get; init; }
    public int MaxChunks { get; init; }
    public ImmutableArray<string> Tags { get; init; } = [];
}

public sealed record OptimiseRule(string Name, OptimiseMatch When, string To, RuleMode Mode)
{
    public EncodeQuality? Quality { get; init; }
}

/// <summary>The show's optimise mode and its own rules, consulted before the built-in table (wp2-optimise's
/// <c>OptimiseRules.BuiltIn</c>); Targets = node ids the show plays on, [] = every PlayMap node heard.</summary>
public sealed record OptimiseSettings
{
    public static readonly OptimiseSettings Default = new();
    public OptimiseMode Mode { get; init; } = OptimiseMode.Ask;
    public ImmutableArray<OptimiseRule> Rules { get; init; } = [];
    public EncodeQuality Quality { get; init; } = EncodeQuality.Show;
    public ImmutableArray<string> Targets { get; init; } = [];
}

/// <summary>How to read an item's colour when its tags are missing or wrong: "auto" values follow the file, then the codec's rule.</summary>
/// <param name="Transfer">"auto" | "sdr" | "pq" | "hlg" | "linear".</param><param name="WhiteNits">0 = the rule's (203 for HAP HDR).</param>
public sealed record ColorOverride(string Transfer, double WhiteNits, string Range, string Matrix)
{
    public static readonly ColorOverride Auto = new("auto", 0, "auto", "auto");
}
```

**`SiteModel.cs`** (owner wp2-facade)

```csharp
/// <summary>The project's site frame: the stage's origin in a surveyed coordinate system, held in double so a facade 500 km
/// from the grid's origin is millimetre-exact after rebasing.</summary>
/// <param name="Crs">"EPSG:25832" | "LOCAL" | "" (not set).</param><param name="Origin">Grid E, N, H in metres.</param>
/// <param name="RotationDeg">Stage +X from grid east, anticlockwise seen from above.</param>
/// <param name="GridScale">Projection × elevation scale at the origin; 1 = unknown.</param><param name="MainsHz">50 | 60 | 0.</param>
public sealed record SiteFrame(string Crs, string CrsName, Vec3d Origin, double RotationDeg, double GridScale, int MainsHz)
{
    public static readonly SiteFrame None = new("", "", Vec3d.Zero, 0, 1, 0);
    public bool IsSet => Crs.Length > 0;

    /// <summary>Grid (E, N, H) to stage (+X east turned by the rotation, +Y up, +Z south), horizontal distances divided by the
    /// grid scale; in double throughout.</summary>
    public Vec3d ToStage(Vec3d grid)
    {
        var a = RotationDeg * Math.PI / 180;
        var s = GridScale > 0 ? GridScale : 1;
        double de = grid.X - Origin.X, dn = grid.Y - Origin.Y;
        var e = (Math.Cos(a) * de + Math.Sin(a) * dn) / s;
        var n = (-Math.Sin(a) * de + Math.Cos(a) * dn) / s;
        return new Vec3d(e, grid.Z - Origin.Z, -n);
    }

    public Vec3d ToGrid(Vec3d stage)
    {
        var a = RotationDeg * Math.PI / 180;
        var s = GridScale > 0 ? GridScale : 1;
        double e = stage.X * s, n = -stage.Z * s;
        return new Vec3d(Origin.X + Math.Cos(a) * e - Math.Sin(a) * n, Origin.Y + Math.Sin(a) * e + Math.Cos(a) * n, Origin.Z + stage.Y);
    }
}

public sealed record ControlPoint(string Id, string Name, Vec3d Grid, double SigmaM, string Code, bool Enabled) : IEntity;

public enum SurveyFormat { Obj, Ply, Gltf, Las, Xyz, Pts, Csv }
public enum SurveyState { Importing, Ready, Failed }

public sealed record SurveySource(string Id, string Name, SurveyFormat Format, string SourcePath, string SourceHash, string Crs,
    long SourcePoints, long SourceTriangles, string? MeshId, string CloudPath, double VoxelM, double DecimationErrorM,
    SurveyState State, long ImportedUnixMs, string Notes) : IEntity;

public enum StackFailover { Hold, Match }

/// <summary>Projectors lighting one picture: the blend treats them as one; each member's measured residual and gains live
/// on its output's MeasuredCorrection.</summary>
public sealed record StackGroup(string Id, string Name, ImmutableArray<string> ProjectorIds, string MasterId, StackFailover Failover) : IEntity;

/// <summary>What a solve may assume: a throw ratio with its uncertainty, a position (a control point or coordinates).</summary>
public sealed record SolvePriors(double ThrowRatio, double ThrowRatioSigmaPct, Vec3d? Position, string ControlId, double PositionSigmaM,
    string Source)
{
    public static readonly SolvePriors None = new(0, 0, null, "", 0, "");
}

/// <summary>A solve's uncertainty from its own information matrix: on the wall (px), along the axis (m), the focal (%), and the
/// weak direction in words ("zoom↔distance").</summary>
public sealed record SolveUncertainty(double WallPx, double ProudPx, double FocalPct, double AxisM, double LateralM, double Condition,
    string Weak);
```

**`AlignModel.cs`** (owner wp2-align; `MeasuredCorrection` shared with wp2-facade)

```csharp
public enum ScreenShape { Auto, Flat, Cylinder }
public enum CanvasFit { Inside, Corners }
public enum ColorMatchMode { Off, White }

/// <summary>The screen as measured.</summary>
public sealed record ScreenModel(ScreenShape Shape, Vec3d Origin, Vec3d Axis, Vec3d Up, double RadiusM, double ArcDeg,
    double WidthM, double HeightM, double RmsM, bool Metric);

/// <param name="CameraCorners">TL TR BR BL, 0–1 of the camera picture; [] when unset.</param>
public sealed record AlignTarget(CanvasFit Fit, double Aspect, ImmutableArray<Vec2d> CameraCorners, double MarginPct)
{
    public static readonly AlignTarget Default = new(CanvasFit.Inside, 0, [], 0);
}

public sealed record AlignOptions
{
    public static readonly AlignOptions Default = new();
    public ColorMatchMode Color { get; init; } = ColorMatchMode.White;
    public bool BlackLevel { get; init; } = true;
    public BlendCurve Curve { get; init; } = BlendCurve.Cosine;
    /// <summary>Fraction of the raster side (as BlendSolveOptions).</summary>
    public double EdgeWidth { get; init; } = 0.15;
    public int GridColumns { get; init; } = 33;
    public int GridRows { get; init; } = 19;
    /// <summary>"FAST" | "ROBUST".</summary>
    public string Mode { get; init; } = "FAST";
}

/// <summary>Outputs aligned together through one camera onto one canvas. Its results live in the calibration ledger.</summary>
public sealed record AlignGroup(string Id, string Name, string CameraId, ImmutableArray<string> OutputIds, string CanvasId,
    ScreenShape Shape, ScreenModel? Screen, AlignTarget Target, AlignOptions Options, string LastRunId) : IEntity;

/// <summary>Texture coordinates at a regular grid of output positions, in File (PFM, under the project folder).</summary>
public sealed record MeasuredWarp(int Columns, int Rows, string File, string Hash, double ResidualPx);
/// <summary>A blend weight and pedestal map at a quarter of the raster, in File.</summary>
public sealed record MeasuredBlend(int Width, int Height, string File, string Hash);
/// <summary>Diagonal colour gains and the transfer γ in linear light (WHITE matching); a full matrix is a later field.</summary>
public sealed record MeasuredColor(double GainR, double GainG, double GainB, double Gamma, double Level);

/// <summary>A camera-measured correction of one output, drawn under the operator's warp: a measured warp grid (align), a small
/// residual lattice and gains (a stack member, facade), a measured blend and colour. One record, whoever measured it.</summary>
public sealed record MeasuredCorrection(MeasuredWarp? Warp, WarpLattice? Lattice, MeasuredBlend? Blend, MeasuredColor? Color,
    double ResidualPx, string ScanId, string StackId, string RunId, long MeasuredUnixMs);

/// <summary>A projector's own processing as read before PlayMap reset it.</summary>
public sealed record NativeSnapshot(long ReadUnixMs, ImmutableSortedDictionary<string, string> Values, ImmutableArray<string> Unsupported,
    bool Neutral);
```

**`CalibrationModel.cs`** (owner wp2-wall; `QualityScore`/`ScoreTerm` used by wp2-align, wp2-phonescan, wp2-facade)

```csharp
/// <summary>One reason in a score: its code, its value with a unit, its share of the score, a light, the words, where on the
/// projector or output it is, a verb line that fixes it ("" when only hands can) and what the hands do.</summary>
public sealed record ScoreTerm(string Code, string Words, double Value, string Unit, double Penalty, string Light, string Where,
    string Fix, string Hint);

/// <summary>An explainable score. Unit "px": px-equivalents, lower is better (A ≤ 1.0, B ≤ 1.5, C ≤ 4, else F — OmniCal's scale);
/// unit "points": 0–100, higher is better (A ≥ 85, B ≥ 70, C ≥ 50, else F). A hard failure is F whatever the value.</summary>
public sealed record QualityScore(double Value, string Unit, string Grade, double RmsPx, double P95Px, double Confidence,
    ImmutableArray<ScoreTerm> Terms, string Words)
{
    public static readonly QualityScore None = new(0, "px", "", 0, 0, 0, [], "");

    /// <summary>What made the result: "pins" (a projector solve), "scan" (SCAN APPLY), "align" (a camera alignment run),
    /// "stack" (STACK ALIGN), "resolve" (a camera re-solve).</summary>
    public string Source { get; init; } = "pins";

    public static string GradeOf(double value, string unit, bool failed) =>
        failed ? "F"
        : unit == "points" ? (value >= 85 ? "A" : value >= 70 ? "B" : value >= 50 ? "C" : "F")
        : value <= 1.0 ? "A" : value <= 1.5 ? "B" : value <= 4 ? "C" : "F";
}

/// <summary>The term vocabulary every score draws from (owner wp2-wall): one code per reason, so the desk, Companion and the
/// assistant word a scan's or an alignment's score the same way as a pin solve's. Packages fill values; they add no codes.</summary>
public static class ScoreTermCodes
{
    // pins (wp2-wall)
    public const string Rms = "rms", P95 = "p95", Coverage = "coverage", Conditioning = "conditioning", Lens = "lens",
        Outliers = "outliers", Geometry = "geometry", Spread = "spread";
    // scans (wp2-multiscan)
    public const string DenseRms = "dense-rms", Decoded = "decoded", Views = "views";
    // alignment and stacks (wp2-resolve, wp2-stacks)
    public const string Seam = "seam", StackConvergence = "stack", Uniformity = "uniformity", ColourMatch = "colour";
    public static readonly FrozenSet<string> All = new[] { Rms, P95, Coverage, Conditioning, Lens, Outliers, Geometry, Spread,
        DenseRms, Decoded, Views, Seam, StackConvergence, Uniformity, ColourMatch }.ToFrozenSet(StringComparer.Ordinal);
}

public enum CalibrationTarget { Projector, Output }
public enum CalibrationSource { Pins, Scan, Resolve, Revert, Align, Stack, Manual }

/// <summary>A projector's optics, pose, locks and pins as solved.</summary>
public sealed record ProjectorCalibration(Intrinsics Intrinsics, LensDistortion Lens, Pose Pose, SolveLocks Locks,
    ImmutableArray<CalibrationPin> Pins);

/// <summary>A calibration result, never edited after it is made: a projector's pose and optics, or an output's measured
/// correction, with the score that explains it and where it came from. Files beside it live in DataPath.</summary>
public sealed record CalibrationSnapshot(string Id, string Name, CalibrationTarget Target, string TargetId, long CreatedUnixMs,
    CalibrationSource Source, string? ScanId, string? ParentId, QualityScore Score, string Who, bool Pinned) : IEntity
{
    public ProjectorCalibration? Projector { get; init; }
    public MeasuredCorrection? Output { get; init; }
    /// <summary>0 = not verified on the wall.</summary>
    public double VerifiedP50Px { get; init; }
    public double VerifiedP95Px { get; init; }
    public string MeshHash { get; init; } = "";
    public string RunId { get; init; } = "";
    public string DataPath { get; init; } = "";
}
```

**`PixelQuad.cs`** (owner wp2-wall)

```csharp
/// <summary>A surface drawn in one projector's raster: four corners in raster-normalised units ((0,0) is the raster's top-left)
/// make a homography of the unit square; an optional lattice adds offsets on top of it (zero offsets are the plain quad). Lit by
/// that projector alone; a projector solve never moves it (a re-solve re-seats it by the measured displacement).</summary>
public sealed record PixelQuad(string ProjectorId, Vec2d TopLeft, Vec2d TopRight, Vec2d BottomRight, Vec2d BottomLeft, WarpLattice? Lattice);
```

**`Overlay.cs`** (owner wp2-wall; runtime, not in the project file; read by the render thread)

```csharp
[Flags]
public enum OverlayLayers
{
    None = 0, Cursor = 1, Outlines = 2, Names = 4, Handles = 8, Lattice = 16, Pins = 32, Grid = 64,
    Edit = Cursor | Outlines | Names | Handles | Lattice, All = 127,
}
public enum HandleKind { Corner, Node }
/// <summary>Corner: Column 0..3 (TL, TR, BR, BL), Row 0.</summary>
public readonly record struct OverlayHandle(string SurfaceId, HandleKind Kind, int Column, int Row);

/// <summary>The edit overlay as the wall shows it: on which outputs, which layers, the selected handle, a grid flash, and the
/// lease that turns it off when the session that asked stops renewing (15 s).</summary>
public sealed record OverlayState(bool On, ImmutableArray<string> Outputs, OverlayLayers Layers, OverlayHandle? Handle,
    long GridUntilLeaderNs, string Session, long LeaseUntilMonoNs)
{
    public static readonly OverlayState Off = new(false, [], OverlayLayers.Edit, null, 0, "", 0);
}

/// <summary>One session's cursor on the wall, in wall-normalised units of an output, with the clicks so far.</summary>
public sealed record WallCursor(string Session, string Name, string OutputId, Vec2d At, ImmutableArray<Vec2d> Trail, string Color,
    long ReceivedMonoNs, long ExpiresMonoNs);
```

**`PhoneModel.cs`** (owner wp2-phonescan)

```csharp
/// <summary>A phone as it described itself when it joined; intrinsics solved with this profile are reused only while all of it matches.</summary>
/// <param name="Platform">"android-chrome" | "ios-safari" | "other".</param><param name="Exposure">"lock" | "steady".</param>
public sealed record PhoneProfile(string Platform, string Model, string CameraLabel, int Width, int Height, string Exposure);

public enum FolderAdvance { NewFile, Manual }
/// <summary>A camera that is a folder: each new image (NewFile) or SCAN NEXT (Manual) is the picture of the current pattern.</summary>
public sealed record FolderCapture(string Path, FolderAdvance Advance, int SettleMs);
```

### 2.4 Edits on the new kinds (spine hunks in `EngineCore.Edits.cs`)

`Locate` finds every new top-level list: `send` (`Sends`), `controlsurface`, `binding` (`Control`), `timecodesource`
(`Timecode.Sources`), `fxasset`, `generatorpreset`, `lednode`, `fixturetype`, `fixture` (`Leds`), `controlpoint`, `survey`,
`stack`, `align`, `calibration` (`Stage`), `fleetmember` (`Fleet.Members`); `Nested` finds `fx` slots in every chain and
`version` in every media item. `ADD` learns `binding`, `controlsurface`, `timecodesource`, `send`, `lednode`, `fixturetype`,
`fixture`, `controlpoint`, `stack` (top level) and `fx` (owned: `ADD fx <owner> {json}`). It refuses, with the words in quotes:
`fxasset` ("use FX IMPORT"), `generatorpreset` ("use GENERATOR SAVE"), `survey` ("use SURVEY IMPORT"), `align` ("use ALIGN NEW"),
`calibration` ("calibration results are made by solves"), `version` ("use MEDIA OPTIMISE"), `fleetmember` ("use FLEET BACKUP on
the backup"). `SET cs… <field>` is refused for every field but `name` and `pinned` ("a calibration result is never edited —
CALIBRATION RECALL makes a new one"). The existing `Validate` calls one hook per package at its end (§5.1).

### 2.5 `ProjectJson` rule for nested records (spine)

`Sanitize` gains one rule beside the default-array rule: a writable property whose value is `null`, whose declared type is a
class with a `public static readonly` field named `Default`, `Empty`, `None` or `Auto` of that type, and whose nullability
(`NullabilityInfoContext`) is *not-null*, is set to that field's value. Nullable properties (`PixelQuad?`, `TimecodeChase?`,
`MeasuredCorrection?` …) stay null. `ImmutableSortedDictionary` properties that read as null become empty. Tests: a round-1
sample project and a file with every new key written as `null` load into exactly the defaults and re-save with every new key.

---

## 3. Core shared types (non-model)

### 3.1 Media (`src/PlayMap.Core/Media/VideoFrames.cs` hunks, spine-owned)

```csharp
// PixelLayout — appended at the end (existing members keep their values)
    /// <summary>Block-compressed (HAP): a plane's Width/Height count 4×4 blocks; Stride = blocks × block bytes.</summary>
    Bc1, Bc3, Bc3YCoCg, Bc3YCoCgBc4, Bc4, Bc7, Bc6hUfloat, Bc6hSfloat,
    /// <summary>4:2:2 from network senders (NDI, OMT), by plane (stride in bytes for width w):
    /// Uyvy — one packed plane, 2·w; Uyva — the UYVY plane (2·w) then an 8-bit alpha plane (w);
    /// P216 — semi-planar: a 16-bit Y plane (2·w) then an interleaved 16-bit CbCr plane (2·w, full height: 4:2:2 halves only
    /// the horizontal);
    /// Pa16 — P216's two planes then a 16-bit alpha plane (2·w). LiveFramePool sizes slots and VideoTextureSet uploads by these.</summary>
    Uyvy, Uyva, P216, Pa16,
    /// <summary>RGBA, 16-bit unsigned normalised (a CPU texture-share path).</summary>
    Rgba16,

// VideoFrameLease — virtual members (defaults keep every existing lease)
    /// <summary>The picture on the GPU, or null for a CPU picture (Planes). A lease with a surface may have no planes.</summary>
    public virtual GpuSurface? Surface => null;
    /// <summary>Where the planes live when written into the renderer's upload arena (empty otherwise); disposed only after the GPU read it.</summary>
    public virtual UploadBlock Staged => default;
    /// <summary>The monotonic instant the frame arrived (network sources; 0 = unknown).</summary>
    public virtual long ArrivalNs => 0;
    /// <summary>The sender's own timestamp, ns (long.MinValue = none).</summary>
    public virtual long SenderTimestampNs => long.MinValue;
// MediaFrameLease.ArrivalNs becomes an override of the virtual.

// SourceHealth — record body (init properties; the positional shape is unchanged)
    public DecodePath Path { get; init; } = DecodePath.Software;
    public bool ZeroCopy => Path is DecodePath.HwDirect or DecodePath.HwGpuCopy or DecodePath.HapGpu or DecodePath.GpuShare;
    public int GpuCopies { get; init; }
    /// <summary>"vaapi", "d3d11va", "cuda", "".</summary>
    public string Device { get; init; } = "";
    /// <summary>Why not a better path, in words.</summary>
    public string Why { get; init; } = "";
    public long BusBytesPerFrame { get; init; }
    public long VramBytes { get; init; }
    public int Surfaces { get; init; }
    public double ImportMsP95 { get; init; }
    public HapFacts? Hap { get; init; }

/// <summary>What a HAP source measured (STATE.decode.sources[].hap).</summary>
public sealed record HapFacts(string Codec, string Texture, int Chunks, double RawShare, string SecondStage, double StageMsP95,
    long UploadBytes, string Staging, long Corrupt, string LastRefusal, string Hdr);
```

### 3.2 GPU surfaces and the upload arena (`src/PlayMap.Core/Media/GpuSurfaces.cs`, `UploadArena.cs`; owner wp2-decode)

```csharp
namespace PlayMap.Core.Media;

/// <summary>The graphics API a device or a picture belongs to.</summary>
public enum GpuApi { None, OpenGl, D3D11, DmaBuf, IoSurface, Cuda }

public enum GpuSurfaceFormat { Nv12, P010, Bgra8, Rgba8, Rgb10A2, Rgba16, Rgba16F, Rgba32F }

/// <summary>How a frame reaches the renderer: the hardware ladder HwDirect → HwGpuCopy → HwCopyBack → Software, HapGpu (HAP),
/// GpuShare (texture-sharing inputs).</summary>
public enum DecodePath { HwDirect, HwGpuCopy, HapGpu, GpuShare, HwCopyBack, Software }

/// <summary>One plane of a dmabuf frame; the fd stays owned by the frame (closed when the lease ends).</summary>
public readonly record struct DmaBufPlane(int Fd, uint Fourcc, int Width, int Height, long Offset, int Pitch, ulong Modifier);

/// <summary>A picture on the GPU, pooled by its producer and valid until the lease that carries it is disposed.</summary>
public sealed class GpuSurface
{
    public GpuApi Api { get; set; }
    public GpuSurfaceFormat Format { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CodedWidth { get; set; }
    public int CodedHeight { get; set; }
    /// <summary>ID3D11Texture2D* | IOSurfaceRef | CUdeviceptr (plane 0).</summary>
    public nint Handle { get; set; }
    public int Slice { get; set; }
    public long SurfaceId { get; set; }
    public long PoolEpoch { get; set; }
    public DmaBufPlane[] Planes { get; } = new DmaBufPlane[4];
    public int PlaneCount { get; set; }
    public bool Persistent { get; set; }
}

/// <summary>The renderer's device as decoders and texture-share inputs see it: what it can import, the D3D11 device to decode
/// on, its lock. Made by the render platform (wp2-output) and handed to decoders through IMediaBackend.UseGpu.</summary>
public interface IGpuShare
{
    GpuApi Api { get; }
    string Describe { get; }
    nint D3D11Device { get; }
    long AdapterLuid { get; }
    string? DrmRenderNode { get; }
    long DeviceEpoch { get; }
    bool DirectProven { get; }
    bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier);
    void Enter();
    void Exit();
    void ReportImportFailure(string sourceKey, string words);
    bool ImportFailed(string sourceKey, out string words);
}

/// <summary>Block-compressed families the renderer samples on this node, and whether it stages through a persistent buffer.</summary>
[Flags] public enum BlockFormats { None = 0, Bc1 = 1, Bc3 = 2, Bc4 = 4, Bc7 = 8, Bc6h = 16, PersistentStaging = 32 }

/// <summary>A block of GPU-visible host memory a producer writes a picture into from any thread.</summary>
public readonly record struct UploadBlock(int Arena, long Offset, long Bytes, nint Pointer, long Epoch)
{
    public bool IsEmpty => Bytes == 0;
}

public interface IUploadArena
{
    long Epoch { get; }
    long CapacityBytes { get; }
    long FreeBytes { get; }
    bool TryRent(long bytes, out UploadBlock block);
    void Return(in UploadBlock block);
}
```

### 3.3 Show Mode types (`src/PlayMap.Core/Wire/ShowClass.cs` spine-owned; `src/PlayMap.Core/Show/ShowTypes.cs`, `AirTypes.cs` owner wp2-show)

```csharp
namespace PlayMap.Core.Wire;

/// <summary>What a verb does to the show, for Show Mode's table (one per catalogue row; Target = classed by the id it names).</summary>
public enum ShowClass
{
    Query, Transport, Air, Panic, Show, Perform, Content, Playlists, Mapping, Layout, Media, Heavy, Setup,
    DeviceSafe, DevicePower, Project, Network, Fleet, Admin, Assistant, Target,
}
```

```csharp
namespace PlayMap.Core.Show;

/// <summary>Who a session is to Show Mode, from its transport (never from words a client sends).</summary>
public enum ShowRole { Operator, Remote, Observer, Assistant, Approved, Peer, System }
public enum ShowVerdict { Allow, Refuse, Propose, BlindOnly }

/// <summary>The facts a decision reads; a value, no allocation.</summary>
public readonly record struct ShowFacts(bool Armed, bool BlindOwner, bool AirDark, bool RemoteViewOnly, string DisarmFrom);
public readonly record struct ShowDecision(ShowVerdict Verdict, string? Words);

// wp2-show implements (pure, allocation-free, every catalogue row × role × armed has a decision):
//   public static class ShowPolicy
//   {
//       public static ShowRole RoleOf(SessionInfo session, IReadOnlyCollection<string> operators);
//       public static ShowClass ClassOf(VerbSpec spec, WireLine line);   // Target verbs classed by the id's prefix
//       public static ShowDecision Decide(VerbSpec spec, WireLine line, ShowRole role, in ShowFacts facts);
//   }

/// <summary>A level fading on the show clock: a pure function of the leader instant, so every node fades on the same frames.</summary>
public readonly record struct AirFade(float From, float To, long StartLeaderNs, long DurationNs)
{
    public static readonly AirFade Full = new(1, 1, 0, 0);
    public float At(long leaderNs) =>
        leaderNs <= StartLeaderNs ? From
        : DurationNs <= 0 || leaderNs >= StartLeaderNs + DurationNs ? To
        : From + (To - From) * (float)((leaderNs - StartLeaderNs) / (double)DurationNs);
    public bool Settled(long leaderNs) => leaderNs >= StartLeaderNs + DurationNs;
}

/// <summary>A panic in force: the outcome, who, since when, whether the second press made it hard, and each output's air.</summary>
public sealed record PanicAir(PanicOutcome Outcome, string By, long SinceUnixMs, long SinceLeaderNs, bool Hard,
    ImmutableArray<OutputPanicAir> Outputs, long Seq);
public sealed record OutputPanicAir(string OutputId, PanicOutcome Outcome, AirFade Level, SourceRef? Safe, long SafeFromLeaderNs,
    long SafeUntilLeaderNs);
```

### 3.4 Modulation and audio seams (`src/PlayMap.Core/Modulation/ModulationSeams.cs`, `src/PlayMap.Core/Audio/AudioInputs.cs`; owner wp2-audio)

```csharp
namespace PlayMap.Core.Modulation;

public enum SourceDeterminism { Pure, Shared, Live }

/// <summary>Modulation sources read on the render thread: never block, never allocate. A source the node lacks answers false
/// (its binding holds the base value and STATE names it).</summary>
public interface IModulationBus
{
    bool TryRead(string source, long leaderNs, out double value01);
    bool IsLive(string source);
    /// <summary>A hot-path slot for a source name (−1 = unknown); TryRead(slot) then reads without a lookup.</summary>
    int Resolve(string source) => -1;
    bool TryRead(int slot, long leaderNs, out double value01) { value01 = 0.5; return false; }
    /// <summary>A raw value (bpm, dB, counts).</summary>
    bool TryReadRaw(string source, long leaderNs, out double value) { value = 0; return false; }
    SourceDeterminism Determinism(string source) => IsLive(source) ? SourceDeterminism.Live : SourceDeterminism.Pure;
    /// <summary>Called once per frame before any read, with the frame's leader instant.</summary>
    void BeginFrame(long leaderNs) { }
}

/// <summary>Sound for ISF audio textures and FFGL buffers.</summary>
public interface IAudioFrames
{
    int Channels { get; }
    bool TryWave(long leaderNs, int samples, Span<float> interleaved);
    bool TrySpectrum(long leaderNs, int bins, Span<float> magnitudes01);
}

/// <summary>No sources at all (a node without the audio package; effects' built-ins are wp2-fx's BuiltinModulation).</summary>
public sealed class NoModulation : IModulationBus
{
    public static readonly NoModulation Instance = new();
    public bool TryRead(string source, long leaderNs, out double value01) { value01 = 0.5; return false; }
    public bool IsLive(string source) => false;
}
```

```csharp
namespace PlayMap.Core.Audio;

public enum AudioInputKind { Capture, Loopback, ProcessLoopback, File, Synthetic }

public sealed record AudioInputInfo(string Key, string Name, int Channels, IReadOnlyList<int> Rates, string Backend)
{
    public AudioInputKind Kind { get; init; } = AudioInputKind.Capture;
    public bool IsDefault { get; init; }
}

/// <summary>Receives capture blocks on the capture thread: never block (copy into a ring and return).</summary>
public interface IAudioCaptureSink
{
    void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs);
    void Faulted(string words);
}

public interface IAudioCapture : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
}

/// <summary>The sound inputs of this machine; Open throws AudioUnavailableException with words when the input cannot open.</summary>
public interface IAudioInputs
{
    IReadOnlyList<AudioInputInfo> Inputs();
    IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink);
}

public sealed class AudioUnavailableException(string message) : Exception(message);
```

### 3.5 Preflight model (`src/PlayMap.Core/Preflight/PreflightModel.cs`; owner wp2-show)

```csharp
namespace PlayMap.Core.Preflight;

public enum PreflightDepth { Quick, Full }

/// <summary>One readiness row: what was checked, its light, the value and note in words, and a line that fixes it.</summary>
public sealed record PreflightRow(string Id, string Node, string Section, string Item, Light Light, string Value, string Note, string? Fix);

/// <summary>Five steps (outputs, media, mapping, sync and backup, safety); a step that does not apply counts as done and says so.</summary>
public sealed record ShowReady(int Done, int Of, string FirstOpen);

public sealed record PreflightReport(string Id, long AtUnixMs, PreflightDepth Depth, long DurationMs, IReadOnlyList<PreflightRow> Rows,
    ShowReady Ready, bool Complete);
```

### 3.6 Art-Net endpoint (`src/PlayMap.Core/Dmx/IArtNetEndpoint.cs`; owner wp2-control)

```csharp
namespace PlayMap.Core.Dmx;

/// <summary>A received Art-Net packet: <paramref name="packet"/> and <paramref name="from"/> are valid only during the call (the
/// receive buffer and address are reused); copy what you keep.</summary>
public delegate void ArtNetReceived(ReadOnlySpan<byte> packet, System.Net.SocketAddress from);

/// <summary>The node's one socket on UDP 6454 (Art-Net 4), shared by control input (ArtDmx, ArtSync, ArtTimeCode, ArtPoll) and LED
/// output (ArtDmx, ArtSync, ArtPoll). Opened when any consumer needs it (DMX input, an ARTNET timecode source, LED output) and bound
/// to the configured address. Subscribers demux by OpCode. Send never blocks and, once warm, never allocates: it uses the .NET
/// `SendTo(ReadOnlySpan&lt;byte&gt;, SocketFlags, SocketAddress)` overload with one cached SocketAddress per destination, and
/// receive uses `ReceiveFrom(Span&lt;byte&gt;, SocketFlags, SocketAddress)` into one reused buffer.</summary>
public interface IArtNetEndpoint : IAsyncDisposable
{
    event ArtNetReceived? Received;
    /// <summary>A destination's cached address (made once per destination, off the send path).</summary>
    System.Net.SocketAddress AddressOf(System.Net.IPEndPoint to);
    bool Send(ReadOnlySpan<byte> packet, System.Net.SocketAddress to);
    /// <summary>The interface it is bound to (null = all).</summary>
    System.Net.IPAddress? Interface { get; }
    /// <summary>Why it is not bound as asked ("UDP 6454 is in use by another program — sending from an ephemeral port"), or null.</summary>
    string? Problem { get; }
}
```

### 3.7 Context link verbs (`src/PlayMap.Core/Bus/ShowContext.cs` hunk, spine-owned)

```csharp
/// <summary>The only link kinds Patterns' Eye draws (it drops any other silently).</summary>
public static class ContextLinkKinds
{
    public const string Feeds = "feeds", Shows = "shows", Drives = "drives", Carries = "carries", Controls = "controls",
        Mirrors = "mirrors", Follows = "follows", Hears = "hears", Routes = "routes", Serves = "serves", Sends = "sends";
    public static readonly FrozenSet<string> All = new[] { Feeds, Shows, Drives, Carries, Controls, Mirrors, Follows, Hears, Routes,
        Serves, Sends }.ToFrozenSet(StringComparer.Ordinal);
    public static bool IsEyeVerb(string kind) => All.Contains(kind);
}
```

`ShowContext.CommandsFromCatalog()` leaves out `Internal` verbs and orders the rest: Panic, Air, Transport and Show classes first
(the Eye keeps 64). A test asserts every link `ContextBuilder` emits passes `IsEyeVerb`.

### 3.8 Live inputs and sends (`src/PlayMap.Core/Media/LiveVideo.cs`; owner wp2-videoio after the spine)

The contracts NDI and OMT (`wp2-videoio`) and Spout (`wp2-d3d11`) implement, landed by the spine so both build in parallel. The
block is `design/netvideo.md` §4.3 verbatim — `LiveProtocol`, `LiveUri`, `LiveEndpoint`, `LiveState`, `LiveHealth`, `IPtz`,
`ILiveSource`, `LiveAnchor`, `ProviderStatus`, `ILiveInputProvider`, `SendSpec`, `SendStats`, `ISendSink`, `ISendProvider`,
`SendFrame` — plus the GPU send path a texture-sharing sender needs (from `design/texshare.md` §4.2, keyed by the spine's
`SendProtocol`):

```csharp
namespace PlayMap.Core.Media;

public readonly record struct SendFrameInfo(long Frame, long LeaderNs, string Tap, int Width, int Height);
public sealed record SendHealth(string State, long Frames, long Dropped, long Busy, double Fps, string Receivers, string Words);

/// <summary>A sender that takes the picture on the GPU (Spout): the render thread draws into its surface; never blocks.</summary>
public interface IGpuSendTarget : IDisposable
{
    string Key { get; }
    SendHealth Health { get; }
    /// <summary>Render thread, non-blocking: the surface to draw this frame into, or false to skip it (counted).</summary>
    bool TryBegin(int width, int height, SendFormat format, out GpuSurface target);
    void End(bool drawn, in SendFrameInfo info);
}

public interface ISendSinkProvider : IDisposable
{
    SendProtocol Protocol { get; }
    ProviderStatus Status { get; }
    /// <summary>Control lane: registers the send's published name and makes its shared texture.</summary>
    IGpuSendTarget Create(VideoSend send, string publishedName, IGpuShare gpu);
}
```

`EngineServices.VideoIo.cs` (wp2-videoio's stub) declares the provider lists, which `wp2-d3d11` extends with Spout's in its own
extension's `Configure`: `ImmutableArray<ILiveInputProvider> LiveInputs`, `ImmutableArray<ISendProvider> SendProviders`,
`ImmutableArray<ISendSinkProvider> GpuSendProviders` (each `= []`).

---
## 4. The wire (`src/PlayMap.Core/Wire/VerbCatalog.cs`, spine-owned)

### 4.1 `VerbSpec`, categories, show classes, risk

```csharp
public enum VerbCategory
{
    Session, Transport, Playlist, Media, Content, Output, Mapping, Scan, Project, Network, Device, Assistant,
    // round 2, appended (HELP groups by category in this order)
    Show, Fleet, Video, Control, Timecode, Effects, Audio, Modulation, Led, Site, Align, Calibration,
}

/// <summary>A per-argument risk: when the word at <see cref="Arg"/> (0-based after the verb's own words; −1 = any position)
/// equals <see cref="Word"/> (compared upper-cased), the line's risk is <see cref="Risk"/>. The first match wins. On a row of
/// class Air, Transport, Panic or DevicePower a RiskWhen may only raise the risk (CatalogueTests).</summary>
public sealed record RiskWhen(int Arg, string Word, VerbRisk Risk);

public sealed record VerbSpec(string Name, string Syntax, string Words, VerbCategory Category, VerbRisk Risk, bool Mutates,
    IReadOnlyList<string> Aliases)
{
    // existing: WordCount, SecretFrom, HighFor

    /// <summary>What the verb does to the show for Show Mode (set by V from the category; overridden with `with`).</summary>
    public ShowClass Show { get; init; }

    /// <summary>Words that change a line's risk (DECODE POLICY … FORCE is high; MEDIA OPTIMISE CANCEL is low). Never lowers a
    /// line that moves the air, a transport, a panic or a projector's power.</summary>
    public IReadOnlyList<RiskWhen> RiskWhen { get; init; } = [];

    /// <summary>1 for today's rows, 2 for round 2's (set by V2); with VerbRoutes it gives VERBS' "available".</summary>
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

public static class ShowClasses
{
    /// <summary>The class a verb has unless its row says otherwise.</summary>
    public static ShowClass Default(VerbCategory category, bool mutates) => !mutates ? ShowClass.Query : category switch
    {
        VerbCategory.Session => ShowClass.Query,
        VerbCategory.Transport => ShowClass.Transport,
        VerbCategory.Playlist => ShowClass.Playlists,
        VerbCategory.Media => ShowClass.Media,
        VerbCategory.Content or VerbCategory.Effects or VerbCategory.Modulation or VerbCategory.Video => ShowClass.Content,
        VerbCategory.Output or VerbCategory.Led => ShowClass.Layout,
        VerbCategory.Mapping or VerbCategory.Site or VerbCategory.Align or VerbCategory.Calibration => ShowClass.Mapping,
        VerbCategory.Scan => ShowClass.Heavy,
        VerbCategory.Project => ShowClass.Project,
        VerbCategory.Network => ShowClass.Network,
        VerbCategory.Fleet => ShowClass.Network,
        VerbCategory.Device => ShowClass.DeviceSafe,
        VerbCategory.Assistant => ShowClass.Assistant,
        VerbCategory.Show => ShowClass.Show,
        VerbCategory.Control or VerbCategory.Timecode or VerbCategory.Audio => ShowClass.Setup,
        _ => ShowClass.Mapping,
    };
}

// V(...) sets Show = ShowClasses.Default(cat, mutates); rows whose class differs say so with `with { Show = … }`.
// Every row of §4.3 is written V2(...), which is V(...) with Round = 2.
private static VerbSpec V(string name, string syntax, string words, VerbCategory cat, VerbRisk risk, bool mutates, params string[] aliases) =>
    new(name, syntax, words, cat, risk, mutates, aliases) { Show = ShowClasses.Default(cat, mutates) };
private static VerbSpec V2(string name, string syntax, string words, VerbCategory cat, VerbRisk risk, bool mutates, params string[] aliases) =>
    V(name, syntax, words, cat, risk, mutates, aliases) with { Round = 2 };
```

```csharp
// Core/Wire/VerbSyntax.cs (spine-owned): the one reader of a row's syntax, used by CommandGate (Assistant), VerbParams
// (wp2-companion), HELP and the CatalogueTests.
public static class VerbSyntax
{
    /// <summary>The row's forms: its syntax split at every top-level " | " (outside &lt;…&gt; and […]), each form starting
    /// with the verb's name ("GENERATOR SET g p v | GENERATOR SET g p BY d" → two forms; "UNLOCK x | UNLOCK AREA a |
    /// UNLOCK ALL" → three; an alternative that omits the name is re-prefixed with it).</summary>
    public static IReadOnlyList<string> Alternatives(VerbSpec spec);

    /// <summary>A form's tokens after the verb up to its first optional part ("[…]"); literal words stay literal.</summary>
    public static IReadOnlyList<string> RequiredTokens(VerbSpec spec, string form);

    /// <summary>True when the line's words after the verb satisfy at least one form's required tokens.</summary>
    public static bool Fits(VerbSpec spec, WireLine line, out string? why);
}

/// <summary>Whether a row is answered on this node now: every today's row (Round 1), and a round-2 row once its owner routed it
/// (EngineCore implements it from VerbRoutes.IsRouted). The assistant's prompt, MCP's list_verbs and VERBS read it.</summary>
public interface IVerbAvailability
{
    bool IsAvailable(VerbSpec spec);
}
```

`CommandGate.RequiredTokens` becomes `VerbSyntax.Alternatives` + `RequiredTokens`, and a line passes when it fits **any**
form (W0a hunk in `Assistant/CommandGate.cs`; today's one top-level `|`, LINK PATTERNS, keeps passing).

**`RiskOf(WireLine)`** (replaces today's): the matched row's risk; `HighFor` (unchanged) raises it; then the first matching
`RiskWhen` sets it — **on a row of class Air, Transport, Panic or DevicePower only upward** (R12); for an `EmbedsLine` verb the
result is `max(row, RiskOf(embedded))`, where the embedded line is the words after `TO` (BIND ADD), else after an optional
`FROM <x>` (BIND LEARN); a line no verb names is High. `CommandGate.RiskOf` (Assistant), `McpTools` (which queues every High line
for a person, as today) and the context's commands call it. Today's tests that assert BLACKOUT ON is high risk and queued
(`OpsHostTests`, `HostLifecycleTests`, `StagewatchSinkTests`, `AssistantEngineTests`) pass unchanged. Lowering safe-direction lines
is not part of round 2 (`PLAN.md` §9 decision 11). Existing rows gain:

```csharp
// BLACKOUT:        with { Show = ShowClass.Air }          (risk stays High in every direction)
// FREEZE:          with { Show = ShowClass.Air }          (risk stays High in every direction)
// DEVICE SHUTTER:  (unchanged: High in both directions)
// DEVICE POWER:    with { Show = ShowClass.DevicePower }
// DEVICE PATTERN, DEVICE LENS, DEVICE PREPARE, DEVICE RAW, DEVICE IDENTIFY, DEVICE DISCOVER, PATTERN, OUTPUT IDENTIFY: with { Show = ShowClass.Setup }
// CREDENTIAL SET, CREDENTIAL CLEAR: with { Show = ShowClass.Admin }
// VIDEO *, PLAYLIST GO|CUE|NEXT|PREV|FOCUS|ANCHOR: with { Show = ShowClass.Transport }
// OUTPUT SOURCE, SURFACE SOURCE: with { Show = ShowClass.Content }
// SET, ADD, REMOVE, MOVE: with { Show = ShowClass.Target }
// BLEND SOLVE, LATENCY MEASURE: with { Show = ShowClass.Heavy }
// SYNC LEAD, SYNC FOLLOW, LINK *, PAIRING NEW: (Network, unchanged)
// ASK, APPROVE, REJECT: (Assistant, unchanged)
```

**`Match`**: `TC` reads as `TIMECODE` (as `VT`/`CLIP` read as `VIDEO`). **`VERBS`** rows gain `"show"`, `"riskWhen"`,
`"ephemeral"`, `"gesture"`, `"pairedOnly"`, `"forms"` (the alternatives) and `"available"` (a today's row, or a round-2 row its owner
routed, read through `IVerbAvailability` that `EngineCore` implements), and leave out `Internal` verbs. `AssistantPrompt.System` and
MCP's `list_verbs` list only available rows. **The router** (spine hunk): a verb that `Mutates`
**or** is `PairedOnly` needs pairing; an `Internal` verb from any transport but `fleet`/`system` is refused "this line is
node-to-node only"; `IsJournalled` is `Mutates && !Ephemeral` (gestures are journalled by their owner, once per gesture).

### 4.2 Existing rows whose syntax changes

```csharp
V("SUBSCRIBE", "SUBSCRIBE STATE|CONTEXT|PROJECT|INDEX|EVENTS|THUMBS [<ref>…]|ANCHORS|HEALTH|OFF", "Pushes STATE (and the other topics) on change, latest wins; EVENTS in order", VerbCategory.Session, VerbRisk.Low, false),
V("VIDEO PLAY", "VIDEO PLAY [ON <playlist>] [AT +<ms>|<unix ms>] [EXPECT <item>|#<n>]", "Plays on from where the clip holds (EXPECT refuses if the item changed under the key)", VerbCategory.Transport, VerbRisk.High, true, "VIDEO RESUME"),
V("VIDEO RESTART", "VIDEO RESTART [ON <playlist>] [AT …] [EXPECT <item>|#<n>]", "Plays from the top", VerbCategory.Transport, VerbRisk.High, true, "VIDEO TOP", "VIDEO START", "VIDEO BEGIN", "VIDEO REWIND"),
V("PLAYLIST GO", "PLAYLIST GO <playlist> [<item>|#<n>] [AT +<ms>|<unix ms>] [EXPECT <item>|#<n>]", "Cues and plays an item (the next one when none is named); EXPECT refuses if the next item is not the one the key showed", VerbCategory.Playlist, VerbRisk.High, true),
V("PLAYLIST NEXT", "PLAYLIST NEXT <playlist> [EXPECT <item>|#<n>]", "Plays the next item", VerbCategory.Playlist, VerbRisk.High, true),
V("PLAYLIST PREV", "PLAYLIST PREV <playlist> [EXPECT <item>|#<n>]", "Plays the previous item", VerbCategory.Playlist, VerbRisk.High, true),
V("DEVICE POWER", "DEVICE POWER <device> ON|OFF [CONFIRM]", "Powers a projector on, or to standby (OFF needs CONFIRM on an armed show)", VerbCategory.Device, VerbRisk.High, true),
V("GENERATOR KINDS", "GENERATOR KINDS [<family>]", "Every generator kind with its parameters (grouped), traits, inputs and preset count", VerbCategory.Content, VerbRisk.Low, false),
V("GENERATOR NEW", "GENERATOR NEW <kind> [<name>] [PRESET <preset>]", "Makes a generator, optionally from a look; answers its id", VerbCategory.Content, VerbRisk.Low, true),
V("GENERATOR SET", "GENERATOR SET <generator> <param> <value> | GENERATOR SET <generator> <param> BY <delta>", "Sets a parameter (BY: a step for encoders)", VerbCategory.Content, VerbRisk.Review, true),
V("ADD", "ADD <media|playlist|item|generator|canvas|layer|surface|mesh|projector|output|mask|pin|camera|viewpoint|device|binding|controlsurface|timecodesource|send|fx|lednode|fixturetype|fixture|controlpoint|stack> [<owner>] <json>", "Adds an entity; answers its id (an item, layer, mask, pin or fx slot names its owner first or as \"parent\" in the JSON)", VerbCategory.Project, VerbRisk.Review, true),
// VIEW: "editor DRAFT" shows the session's blind draft in that view only (wp2-blind)
V("VIEW", "VIEW <view> SHOW|CAMERA|KEEP|HIDE [<what> [<w>x<h>] [DRAFT]|<camera json>]", "A preview into the view's shared-memory ring on this machine: SHOW editor, output:<id>, projector:<id>, camera:<id> or scan:<id>:smart|confidence; DRAFT shows this session's blind draft instead of the air (editor only); CAMERA moves an editor view's eye; KEEP renews it (a view not renewed for 15 s is hidden); HIDE lets it go", VerbCategory.Output, VerbRisk.Low, false),
// SCAN START: INTO adds a view to an existing scan (wp2-multiscan)
V("SCAN START", "SCAN START <camera> [<projector>…] [MODE FAST|ROBUST|DIFFICULT] [INTO <scan>]", "Structured-light scan of the projectors through the camera (INTO: another view of the same scan)", VerbCategory.Scan, VerbRisk.High, true),
// SCAN SURFACE: ON names the projector whose region it is (wp2-multiscan)
V("SCAN SURFACE", "SCAN SURFACE <scan> <x> <y> [ON <projector>] [<name>] | SCAN SURFACE <scan> REGION <id> [ON <projector>] [<name>]", "Makes a surface of what is at x, y (0–1 across and down the scan's smart-scan picture), or of a suggested region: its mesh faces and a mask outlining it; ON takes that projector's smart scan; answers its id", VerbCategory.Scan, VerbRisk.Review, true),
// MEDIA FETCH: VERSION pulls one optimised version by hash, with the peer's proof (wp2-backup)
V("MEDIA FETCH", "MEDIA FETCH <media>|ALL [VERSION <version>] [FROM <node>]", "Copies missing files (or one version of a clip) from a peer that has them", VerbCategory.Media, VerbRisk.Low, true),
```

### 4.3 The new rows, by package

Placed in `VerbCatalog.All` after today's rows, grouped by package with a comment line naming the owner. Every name is one or two
words; the router's default answers "… is not available on this node yet" until the owner routes it, and the integration test
(`PLAN.md` §6) requires every row routed. **Each package owns the rows inside its own block** (between its comment line and the
next): it trims a row's syntax to the options it implements and removes a row it does not implement this round (the row moves to
its next-round list in `PLAN.md`); it may add a row inside its block for a stretch item it reaches; it never edits a row outside
its block. A continuation package (`PLAN.md` §2.2) owns its parent's block in its wave. Blocks are separated by a blank line so the
merges of different packages' blocks never touch the same hunk. Rows outside these blocks (§4.2) stay the spine's. Every row
here is `V2(…)` (`Round = 2`), and every row of class Air or Panic is High (R12).

```csharp
        // Show Mode, panic, locks, blind edit, preflight (wp2-show; blind edit and the ALL forms by wp2-blind, its continuation).
        V2("SHOW", "SHOW [STATUS]", "Show Mode: armed or rehearsal, by whom and since, the rules, the blind draft, panic and locks", VerbCategory.Show, VerbRisk.Low, false),
        V2("SHOW ARM", "SHOW ARM [ALL] [<words>]", "Arms the show with no password: edits that would move the air are refused for every caller until SHOW DISARM; transport, blackout and PANIC stay live (ALL: every node of the group, with each node's receipt)", VerbCategory.Show, VerbRisk.Review, true),
        V2("SHOW DISARM", "SHOW DISARM [ALL]", "Back to rehearsal (ALL: every node of the group)", VerbCategory.Show, VerbRisk.Review, true),
        V2("SHOW SET", "SHOW SET MINGO <ms> [ARMED|REHEARSAL] | REMOTE-VIEW-ONLY ON|OFF | DISARM-FROM PAIRED|DESK | EXPECT-ARMED ON|OFF | COMMIT-LEAD <ms> | PANIC-LEAD <ms>", "Changes a show rule (kept in the project, one undo step)", VerbCategory.Show, VerbRisk.Review, true),
        V2("PANIC", "PANIC [BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD] [ON <output>…|ALL] [FADE <ms 0-10000>] [AT +<ms>|<unix ms>] [HERE]", "Takes each output to its planned outcome over its fade, picture and sound; a second PANIC during the fade is the hard stop; on a group's leader every follower and backup fades on the same frames unless HERE; never refused for an operator (an assistant's PANIC waits for a person)", VerbCategory.Show, VerbRisk.High, true) with { Show = ShowClass.Panic },
        V2("PANIC CLEAR", "PANIC CLEAR [ON <output>…|ALL] [FADE <ms>] [HERE]", "Brings the air back from a panic over a fade (transports stay as the outcome left them); a leader's clear reaches its group unless HERE", VerbCategory.Show, VerbRisk.High, true, "PANIC OFF") with { Show = ShowClass.Panic },
        V2("PANIC SET", "PANIC SET <output>|ALL BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD [FADE <ms>] [SAFE <source>]", "Plans what PANIC does on an output (kept in the project)", VerbCategory.Show, VerbRisk.Review, true),
        V2("MASTER", "MASTER <0-100> [FADE <ms>] [HERE]", "The grand master over every output, picture and sound, faded on the show clock (not an undo step); a leader's master reaches its group unless HERE", VerbCategory.Output, VerbRisk.High, true) with { Show = ShowClass.Air },
        V2("LOCK", "LOCK <id> [<words>] | LOCK AREA mapping|layout|content|playlists|media|devices|network [<words>]", "Locks an entity or an area: no verb, undo or assistant changes it; playback and reading go on", VerbCategory.Show, VerbRisk.Review, true),
        V2("UNLOCK", "UNLOCK <id> | UNLOCK AREA <area> | UNLOCK ALL", "Lifts a lock", VerbCategory.Show, VerbRisk.Review, true),
        V2("LOCKS", "LOCKS", "Every lock with who set it and why, and the areas an armed show adds", VerbCategory.Show, VerbRisk.Low, false),
        V2("BLIND", "BLIND [STATUS]", "The blind draft: whose, how many changes, when it commits", VerbCategory.Show, VerbRisk.Low, false),
        V2("BLIND ON", "BLIND ON", "Blind editing for this session: its edits go to a draft, off the air, until BLIND COMMIT", VerbCategory.Show, VerbRisk.Low, true),
        V2("BLIND OFF", "BLIND OFF", "Leaves blind editing (refused while the draft holds changes: COMMIT or DISCARD them)", VerbCategory.Show, VerbRisk.Low, true),
        V2("BLIND DIFF", "BLIND DIFF", "What the draft changes, entity by entity, in field words, with conflicts", VerbCategory.Show, VerbRisk.Low, false),
        V2("BLIND COMMIT", "BLIND COMMIT [NOW|NEXT [<playlist>]|ON GO [<playlist>]|AT +<ms>|<unix ms>] [FORCE]", "Lands the draft on the air as one undo step at a show boundary, frame-exact; FORCE overrides the conflicts it names", VerbCategory.Show, VerbRisk.High, true),
        V2("BLIND DISCARD", "BLIND DISCARD", "Drops the draft", VerbCategory.Show, VerbRisk.Review, true),
        V2("PREFLIGHT", "PREFLIGHT [QUICK|FULL] [ALL]", "Checks the node is ready for the show (FULL also decodes every clip in it; ALL every node of the group, one section per node); each row carries the line that fixes it; answers the report's id", VerbCategory.Show, VerbRisk.Low, true),
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
        V2("SYNC LATENCY", "SYNC LATENCY AUTO|<ms 0-500>", "The group's air latency (AUTO: the largest output latency any member reports)", VerbCategory.Network, VerbRisk.Review, true) with { Show = ShowClass.Layout },

        // HAP on the GPU (wp2-decode).
        V2("MEDIA INSPECT", "MEDIA INSPECT <media>", "What a clip is to the decoder: codec and texture, chunks, second stage, MB per frame and per second, HDR reading, odd size, and the lines that fix what is slow", VerbCategory.Media, VerbRisk.Low, false),

        // The decode-path ladder and hardware imports (wp2-zerocopy, decode's continuation).
        V2("DECODE STATUS", "DECODE STATUS", "This node's decode paths: zero-copy ready, direct sampling proven, and every open decoder's path, why and cost", VerbCategory.Media, VerbRisk.Low, false),
        V2("DECODE POLICY", "DECODE POLICY AUTO|DIRECT|GPU-COPY|COPY-BACK|SOFTWARE [FORCE]", "The best path decoders opened from now on may take (open ones keep theirs); DIRECT needs a proof unless FORCE", VerbCategory.Media, VerbRisk.Review, true) with { Show = ShowClass.Setup, RiskWhen = [new(-1, "FORCE", VerbRisk.High)] },
        V2("DECODE PROBE", "DECODE PROBE [<media>|ALL] [DIRECT]", "Decodes clips by every path and compares the pictures; DIRECT writes the direct-sampling proof for this driver", VerbCategory.Media, VerbRisk.Review, true) with { Show = ShowClass.Heavy },

        // Optimise on import (wp2-optimise).
        V2("MEDIA OPTIMISE", "MEDIA OPTIMISE <media>…|ALL|SUGGESTED [TO hap|hap-alpha|hap-q|hap-q-alpha|rechunk] [QUALITY FAST|SHOW] | MEDIA OPTIMISE CANCEL|PAUSE|RESUME [<job>|ALL]", "Makes a version of clips that plays on the GPU here, in the background, never disturbing playback and never touching the source; CANCEL, PAUSE and RESUME steer jobs", VerbCategory.Media, VerbRisk.Review, true, "MEDIA OPTIMIZE") with { Show = ShowClass.Heavy, RiskWhen = [new(0, "CANCEL", VerbRisk.Low), new(0, "PAUSE", VerbRisk.Low), new(0, "RESUME", VerbRisk.Low)] },
        V2("MEDIA JOBS", "MEDIA JOBS", "Every optimise job and the governor's state", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA ESTIMATE", "MEDIA ESTIMATE <media>…|ALL|SUGGESTED [TO <target>] [QUALITY FAST|SHOW]", "What optimising would do, how long and how much disk, before any work", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA VERSIONS", "MEDIA VERSIONS <media>", "A clip's versions, which plays on this node and why", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA USE", "MEDIA USE <media> AUTO|SOURCE|<version>", "Which file a clip plays (from its next open, never mid-air)", VerbCategory.Media, VerbRisk.Review, true),
        V2("MEDIA VERSION", "MEDIA VERSION REMOVE <version> [FILES]", "Forgets a version (FILES also deletes this node's derived file when nothing else names it)", VerbCategory.Media, VerbRisk.Review, true),
        V2("MEDIA RULES", "MEDIA RULES", "The optimise mode, this show's rules, then the built-in table", VerbCategory.Media, VerbRisk.Low, false),
        V2("MEDIA RULE", "MEDIA RULE MODE AUTO|ASK|NEVER | MEDIA RULE ADD <json> [AT <n>] | MEDIA RULE REMOVE <n>|<name>", "Changes the optimise mode or this show's rules", VerbCategory.Media, VerbRisk.Review, true),

        // Network video and texture sharing: NDI, OMT, Spout (wp2-videoio).
        V2("INPUT LIST", "INPUT LIST", "Every live input with its health, its users and whether it is on air", VerbCategory.Video, VerbRisk.Low, false),
        V2("INPUT DISCOVER", "INPUT DISCOVER [NDI|OMT|SPOUT|ALL] [WAIT <0-5000 ms>]", "The NDI, OMT and Spout sources this node sees", VerbCategory.Video, VerbRisk.Low, false),
        V2("INPUT ADD", "INPUT ADD <uri> [<name>] [PROXY] [COLD]", "Adds a live input (ndi:HOST (Name), omt:HOST (Name), omt://host:port, spout:Name) to the library, received once however many surfaces show it; answers its id", VerbCategory.Video, VerbRisk.Review, true),
        V2("INPUT FALLBACK", "INPUT FALLBACK <input> HOLD|BLACK|<source>", "What an input shows when its feed is lost", VerbCategory.Video, VerbRisk.Review, true),
        V2("INPUT BACKUP", "INPUT BACKUP <input> <uri>|OFF", "A second feed the input takes over on loss", VerbCategory.Video, VerbRisk.Review, true),
        V2("INPUT SWITCH", "INPUT SWITCH <input> PRIMARY|BACKUP", "Switches an input between its feeds (answered at the first frame)", VerbCategory.Video, VerbRisk.High, true) with { Show = ShowClass.DeviceSafe },
        V2("INPUT RECONNECT", "INPUT RECONNECT <input>|ALL", "Reconnects a stale or lost input (refused while it delivers)", VerbCategory.Video, VerbRisk.High, true) with { Show = ShowClass.DeviceSafe },
        V2("SEND LIST", "SEND LIST", "Every send with its tap, format, receivers, tally and drops", VerbCategory.Video, VerbRisk.Low, false),
        V2("SEND ADD", "SEND ADD NDI|OMT|SPOUT <name> <tap> [<w>x<h>] [@<fps>] [UYVY|UYVA|P216|PA16|BGRA8|RGBA8|RGBA16F] [QUALITY LOW|MEDIUM|HIGH] [ALWAYS]", "Sends a picture to the network or to other apps on this machine: tap output:<id>, prewarp:<id>, canvas:<id> or source:<ref>; answers its id", VerbCategory.Video, VerbRisk.Review, true),
        V2("SEND ENABLE", "SEND ENABLE <send> ON|OFF", "Starts or stops a send", VerbCategory.Video, VerbRisk.High, true) with { Show = ShowClass.Layout, RiskWhen = [new(1, "ON", VerbRisk.Review)] },
        V2("SEND REMOVE", "SEND REMOVE <send>", "Removes a send (its receivers lose the picture)", VerbCategory.Video, VerbRisk.High, true) with { Show = ShowClass.Layout },
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
        V2("TIMECODE SOURCE", "TIMECODE SOURCE ADD <name> LTC [<device>[:<channel>]]|MTC <port>|ARTNET [STREAM <n>]|INTERNAL [FORMAT <fmt>] | TIMECODE SOURCE REMOVE <source> | TIMECODE SOURCE RESET <source>", "Adds, removes or resets a timecode input; answers its id", VerbCategory.Timecode, VerbRisk.Review, true) with { RiskWhen = [new(0, "RESET", VerbRisk.Low)] },
        V2("TIMECODE CHASE", "TIMECODE CHASE <playlist> <source>[,<backup>…]|OFF", "A playlist follows timecode: each item at its TC in, locked to the code's fitted line, freewheeling through dropouts", VerbCategory.Timecode, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V2("TIMECODE ENGAGE", "TIMECODE ENGAGE <playlist>|ALL ON|OFF|TOGGLE", "Engages or disengages a chase (disengaged, the playlist plays by hand from where it is)", VerbCategory.Timecode, VerbRisk.High, true) with { Show = ShowClass.Transport },
        V2("TIMECODE SET", "TIMECODE SET <playlist> DELAY <±ms>|FREEWHEEL <ms>|ONLOSS HOLD|STOP|CONTINUE|JUMP <ms>|AUTO|GAP BLACK|HOLD", "Tunes a chase", VerbCategory.Timecode, VerbRisk.Review, true) with { Show = ShowClass.Playlists },
        V2("TIMECODE IN", "TIMECODE IN <playlist> <item> <HH:MM:SS:FF>|NOW|OFF", "The timecode an item starts at (NOW takes the chase's current code)", VerbCategory.Timecode, VerbRisk.Review, true) with { Show = ShowClass.Playlists },
        V2("TIMECODE INTERNAL", "TIMECODE INTERNAL <source> PLAY|STOP|LOCATE <tc>|RATE <0.5-2>", "Runs an internal timecode source (rehearsal or master)", VerbCategory.Timecode, VerbRisk.High, true) with { Show = ShowClass.Transport },

        // Controllers' side of the wire: index, events, presence (wp2-companion).
        V2("INDEX", "INDEX", "The show's structure for controllers (playlists and items, surfaces, projectors, outputs, devices, generators, and every package's section with its preset hint) with structure and label signatures", VerbCategory.Session, VerbRisk.Low, false),
        V2("EVENTS", "EVENTS [SINCE <seq>]", "The node's recent events (item start and end, armed, panic, faults, calibration moved…) for automations", VerbCategory.Session, VerbRisk.Low, false),
        V2("RECORD", "RECORD ON|OFF", "This session hears every line an operator performs on the desk as ACTION lines (never a secret)", VerbCategory.Session, VerbRisk.Low, false) with { PairedOnly = true },
        V2("PRESENCE", "PRESENCE <json>", "A controller says what it is and what it shows (kind, module version, page)", VerbCategory.Session, VerbRisk.Low, false) with { PairedOnly = true },
        V2("WHOAMI", "WHOAMI", "This session as the node sees it: transport, role, paired, armed, and the verbs it may run now", VerbCategory.Session, VerbRisk.Low, false),
        V2("SELECT", "SELECT <id>…|NONE", "What the desk has selected, for controllers that learn from it", VerbCategory.Session, VerbRisk.Low, false) with { PairedOnly = true, Ephemeral = true },
        V2("THUMB", "THUMB <media:<id>|output:<id>|playlist:<id>> [<w>x<h>] [JPEG|PNG] [IF <etag>]", "A small picture for a key or a list, cached by content and ETag", VerbCategory.Media, VerbRisk.Low, false) with { PairedOnly = true },
        V2("COMPANION PRESS", "COMPANION PRESS <page>/<row>/<column> [ON <companion>]", "Presses a button on a Companion this node links to (its HTTP API)", VerbCategory.Network, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("COMPANION VAR", "COMPANION VAR <name> <value> [ON <companion>]", "Sets a Companion custom variable", VerbCategory.Network, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("COMPANION BRIGHTNESS", "COMPANION BRIGHTNESS <0-100> [<surface>] [ON <companion>]", "Dims Companion's surfaces (REST v2; the key from the credential store)", VerbCategory.Network, VerbRisk.Review, true) with { Show = ShowClass.Perform },

        // Effects: ISF and FFGL in chains (wp2-fx). FX BIND / FX UNBIND are aliases of MOD BIND / MOD UNBIND.
        V2("FX KINDS", "FX KINDS [<family>|<search>]", "Every effect kind (built-in, this show's ISF, FFGL plugins) with its parameters, cost and traits", VerbCategory.Effects, VerbRisk.Low, false),
        V2("FX LIST", "FX LIST <target>|ALL", "The effect chain of a layer, canvas, surface, output or playlist item, with each slot's state", VerbCategory.Effects, VerbRisk.Low, false),
        V2("FX ADD", "FX ADD <target> <kind> [AT <index>] [<name>]", "Adds an effect to a chain; answers its slot id", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX REMOVE", "FX REMOVE <slot>", "Removes an effect", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX MOVE", "FX MOVE <slot> <index>", "Moves an effect in its chain", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX SET", "FX SET <slot> <param>|mix|blend|scale|name <value> | FX SET <slot> <param> BY <delta>", "Sets an effect's parameter, mix, blend or scale", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX BYPASS", "FX BYPASS <slot>|<target>|ALL ON|OFF|TOGGLE", "Bypasses effects (ALL ON is the panic outcome 'effects off')", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX RESET", "FX RESET <slot>", "Sets an effect's parameters back to their defaults", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX IMPORT", "FX IMPORT <path>|<json>", "Adds an ISF effect to the show (content-addressed, carried with it); answers its asset id and the compile report", VerbCategory.Effects, VerbRisk.Review, true),
        V2("FX CHECK", "FX CHECK <asset>|<json>", "Compiles an ISF source for GLSL 3.30 and ES 3.00 and reports its issues by line", VerbCategory.Effects, VerbRisk.Low, false),
        V2("FX PLUGINS", "FX PLUGINS [RESCAN]", "The FFGL plugins this node found, each with its trust and probe state", VerbCategory.Effects, VerbRisk.Low, false),
        V2("FX TRUST", "FX TRUST <plugin> IN-PROCESS|OFF", "Lets an FFGL plugin run inside the render process, or not (this machine only, never from an assistant)", VerbCategory.Effects, VerbRisk.High, true) with { Show = ShowClass.Admin },

        // Sound, the beat clock and the modulation bus (wp2-audio).
        V2("AUDIO INPUTS", "AUDIO INPUTS", "The sound inputs this node can listen to (devices, the system's sound, test signals)", VerbCategory.Audio, VerbRisk.Low, false),
        V2("AUDIO LISTEN", "AUDIO LISTEN <input>|DEFAULT|SYSTEM|TEST CLICK|SWEEP|NOISE|DRUMS|OFF [CHANNELS 1|1+2|3-4|M|S] [GAIN <dB>] [HERE]", "Chooses what the show listens to (HERE: this machine only)", VerbCategory.Audio, VerbRisk.Review, true),
        V2("AUDIO GAIN", "AUDIO GAIN <-24…24>|AUTO|FIXED", "The listening gain", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("AUDIO STATUS", "AUDIO STATUS", "Levels, bands, the spectrum, onsets and the input's state", VerbCategory.Audio, VerbRisk.Low, false),
        V2("TEMPO STATUS", "TEMPO STATUS", "The beat clock: BPM, beat, bar, phrase, source, lock and confidence", VerbCategory.Audio, VerbRisk.Low, false),
        V2("TEMPO SET", "TEMPO SET <20-999> [AT +<ms>|<unix ms>|BEAT|BAR|PHRASE]", "Sets the tempo from the next beat", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO TAP", "TEMPO TAP [AT <unix ms>]", "A tap; two or more set the tempo", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO RESYNC", "TEMPO RESYNC [BEAT|BAR|PHRASE] [AT <unix ms>]", "Puts the beat, bar or phrase on now", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO NUDGE", "TEMPO NUDGE <±1-200 ms>|FASTER|SLOWER|STOP", "Moves the beat's phase, slewed over a bar", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO HALF", "TEMPO HALF", "Halves the tempo", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO DOUBLE", "TEMPO DOUBLE", "Doubles the tempo", VerbCategory.Audio, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("TEMPO SOURCE", "TEMPO SOURCE MANUAL|DETECT", "Where the beat comes from: taps and SET, or detected from the sound", VerbCategory.Audio, VerbRisk.Review, true),
        V2("TEMPO METER", "TEMPO METER <beats per bar 1-16> [<bars per phrase 1-64>]", "The meter the bar and phrase sources count", VerbCategory.Audio, VerbRisk.Low, true),
        V2("MOD SOURCES", "MOD SOURCES [<prefix>]", "Every modulation source on this node (audio:, beat, lfo:, sting) with its value and whether it is pure, shared or live", VerbCategory.Modulation, VerbRisk.Low, false),
        V2("MOD BIND", "MOD BIND <entity> <param> <source> [AMOUNT <-1…1>] [OFFSET <x>] [MODE ADD|SCALE|REPLACE|RATE] [CURVE LINEAR|EASE|STEP|EXP|LOG|S] [SMOOTH <attack ms>[/<release ms>]] [RANGE <min> <max>] [STEPS <n>] [INVERT]", "Modulates a number of a generator, layer, surface or effect slot from a source, live; the show keeps the base value", VerbCategory.Modulation, VerbRisk.Review, true, "FX BIND", "GENERATOR BIND"),
        V2("MOD UNBIND", "MOD UNBIND <entity> <param>|ALL", "Removes modulation", VerbCategory.Modulation, VerbRisk.Review, true, "FX UNBIND", "GENERATOR UNBIND"),
        V2("MOD BYPASS", "MOD BYPASS ON|OFF|TOGGLE [<entity>|ALL]", "Holds every modulated number at its base value", VerbCategory.Modulation, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("MOD PEEK", "MOD PEEK <entity>", "The live value of each modulated number of an entity", VerbCategory.Modulation, VerbRisk.Low, false),

        // Particles, fractals, reactive: looks, morphs, stings (wp2-content).
        V2("GENERATOR GET", "GENERATOR GET <generator>", "A generator with its effective values, bindings, inputs, morph, cost and state", VerbCategory.Content, VerbRisk.Low, false),
        V2("GENERATOR RESET", "GENERATOR RESET <generator> [<param>|ALL]", "Parameters back to their defaults", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR INPUT", "GENERATOR INPUT <generator> source|flow|matte <source>|NONE", "A picture a generator reads (particles emit from it)", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR PRESETS", "GENERATOR PRESETS [<kind>|<family>]", "Curated and saved looks", VerbCategory.Content, VerbRisk.Low, false),
        V2("GENERATOR RECALL", "GENERATOR RECALL <generator> <preset>", "Applies a look (one undo step)", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR SAVE", "GENERATOR SAVE <generator> <name>", "Saves the generator's look in the show; answers its id", VerbCategory.Content, VerbRisk.Low, true),
        V2("GENERATOR MORPH", "GENERATOR MORPH <generator> <preset> [OVER <0.1-600 s>] [AT +<ms>|<unix ms>] [CURVE LINEAR|EASE]", "Crossfades a generator to a look on the show clock", VerbCategory.Content, VerbRisk.Review, true),
        V2("GENERATOR RETIME", "GENERATOR RETIME <generator>|ALL [AT +<ms>|<unix ms>]", "Restarts a generator's own time (particles re-seed from that instant)", VerbCategory.Content, VerbRisk.Review, true),
        V2("STING", "STING <preset> [<100-5000 ms>] [AT +<ms>|<unix ms>]", "Fires a sting every generator answers, on the show clock", VerbCategory.Content, VerbRisk.Review, true) with { Show = ShowClass.Perform },
        V2("STING CLEAR", "STING CLEAR", "Stops the sting", VerbCategory.Content, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("STING LIST", "STING LIST", "The sting presets", VerbCategory.Content, VerbRisk.Low, false),

        // LED pixel mapping (wp2-led).
        V2("LED LIST", "LED LIST", "The LED rig: nodes, fixtures, pixels, universes, conflicts and bandwidth", VerbCategory.Led, VerbRisk.Low, false),
        V2("LED NEW", "LED NEW LINE|GRID|RING <count>[x<rows>] [ON canvas:<id>|output:<id>|surface:<id>|source:<ref>|world] [TYPE <type>] [<name>]", "Adds a fixture laid out on a space; answers its id", VerbCategory.Led, VerbRisk.Low, true),
        V2("LED IMPORT", "LED IMPORT <path> [AS CSV] [ON <space>] [SCALE <x>] [NODE <node>]", "Imports pixel positions and patches from a CSV (one undo step)", VerbCategory.Led, VerbRisk.Review, true),
        V2("LED PATCH", "LED PATCH <fixture> <node> <universe>[.<address>] [FORCE]", "Patches a fixture to a node's universe and address (FORCE allows an overlap)", VerbCategory.Led, VerbRisk.Review, true),
        V2("LED AUTOPATCH", "LED AUTOPATCH [<fixture>|#<tag>|ALL] [NODE <node>…] [FROM <universe>[.<address>]] [ALIGN ON|OFF] [EACH ON|OFF]", "Patches fixtures in order within each node's capacity", VerbCategory.Led, VerbRisk.Review, true),
        V2("LED DISCOVER", "LED DISCOVER [ARTNET|ALL] [<seconds 1-10>]", "Polls the network for Art-Net nodes", VerbCategory.Led, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("LED OUTPUT", "LED OUTPUT ON|OFF [<node>|ALL] [HOLD]", "This machine sends pixel data, or stops (one black frame first unless HOLD)", VerbCategory.Led, VerbRisk.High, true) with { Show = ShowClass.Air },
        V2("LED TEST", "LED TEST CHASE|ORDER|UNIVERSE|FIXTURE|GRADIENT|WALK|WHITE|FULL|BLACK|OFF [ON <node>|<fixture>|#<tag>|ALL]", "A test pattern on the LEDs instead of the show", VerbCategory.Led, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V2("LED LOCATE", "LED LOCATE <fixture> [<seconds 1-60>]", "Blinks one fixture", VerbCategory.Led, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("LED LEVEL", "LED LEVEL <0-100> [FADE <ms>]", "The LED rig's master level (live, not an undo step)", VerbCategory.Led, VerbRisk.High, true) with { Show = ShowClass.Air },
        V2("LED MONITOR", "LED MONITOR <node> <universe>", "The bytes last sent to a universe, with the fixtures in it", VerbCategory.Led, VerbRisk.Low, false),

        // Facades: site, survey, control points, long-throw priors, light plan, stacks (wp2-facade).
        V2("SITE", "SITE", "The site frame: coordinate system, grid origin, rotation, scale and mains frequency", VerbCategory.Site, VerbRisk.Low, false),
        V2("SITE ORIGIN", "SITE ORIGIN <e> <n> <h> [CRS <code>|LOCAL] [ROTATE <deg>]", "Sets the site frame; the whole stage moves with it in one edit", VerbCategory.Site, VerbRisk.Review, true),
        V2("SITE MAINS", "SITE MAINS 50|60", "The venue's mains frequency", VerbCategory.Site, VerbRisk.Low, true),
        V2("SITE POINTS", "SITE POINTS <path> [ORDER PENZD|PNEZD|ENH|NEH] [CRS <code>] [UNITS m|cm|mm|ft|usft]", "Imports surveyed control points; answers their ids and the lines it skipped", VerbCategory.Site, VerbRisk.Review, true),
        V2("SITE CHECK", "SITE CHECK [<surface>|<survey>]", "Measures the model against the control points: each point's distance and the fit", VerbCategory.Site, VerbRisk.Low, false),
        V2("SURVEY IMPORT", "SURVEY IMPORT <path> [CRS <code>|LOCAL] [UNITS m|cm|mm|ft|usft] [UP Z|Y] [VOXEL <m>] [DECIMATE <triangles>|OFF] [NAME <name>]", "Imports a surveyed model or point cloud (OBJ, PLY, glTF, LAS) into the site frame, off the control lane; answers its id", VerbCategory.Site, VerbRisk.Review, true) with { Show = ShowClass.Heavy },
        V2("SURVEY LIST", "SURVEY LIST", "Imported surveys with provenance and state", VerbCategory.Site, VerbRisk.Low, false),
        V2("SITE GUIDE", "SITE GUIDE", "The facade workflow as steps (survey, control points, pins, priors and solve, stacks, blend, score), each with its state and the line that does it", VerbCategory.Site, VerbRisk.Low, false, "FACADE GUIDE"),
        V2("PROJECTOR PRIOR", "PROJECTOR PRIOR <projector> THROW <ratio> [±<pct>] | PROJECTOR PRIOR <projector> AT <control>|<x> <y> <z> [±<m>] | PROJECTOR PRIOR <projector> CLEAR", "What a solve may assume about a projector's lens and position", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("PLAN LIGHT", "PLAN LIGHT [<surface>|ALL] [AMBIENT <lux>] [ALBEDO <0-1>] [TARGET <lux>] [DERATE <0-1>]", "Predicted illuminance, luminance, contrast and pixel pitch per surface from the projectors as placed", VerbCategory.Site, VerbRisk.Low, false),
        V2("STACK NEW", "STACK NEW <name> <projector> <projector> [<projector>…] [MASTER <projector>]", "Groups projectors lighting one picture (the blend treats them as one); answers its id", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("STACK ALIGN", "STACK ALIGN <stack> FROM <scan> [GRID <c>x<r>]", "Fits each member's measured residual to the master's picture from a shared scan", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("STACK MATCH", "STACK MATCH <stack> FROM <scan>", "Matches the members' colour gains from the scan's white frames", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("STACK FAILOVER", "STACK FAILOVER <stack> HOLD|MATCH", "What the stack does when a member fails: hold the rest, or dim its blend group to match", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("STACK LIST", "STACK LIST", "Stacks with members, residual, gains and degraded members", VerbCategory.Mapping, VerbRisk.Low, false),

        // Auto-align from the truck: camera-measured flat-screen correction (wp2-align; ALIGN SCORE, ONLY and DEVICE NATIVE by wp2-resolve).
        V2("ALIGN NEW", "ALIGN NEW <name> <camera> <output>…|ALL [CANVAS <canvas>|NEW] [SHAPE AUTO|FLAT|CYLINDER]", "Outputs aligned together through one camera onto one canvas; answers its id", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN PLAN", "ALIGN PLAN <group>", "A dry run: camera, outputs, devices and their own processing, steps and time; nothing reaches the wall or a projector", VerbCategory.Align, VerbRisk.Low, false),
        V2("ALIGN RUN", "ALIGN RUN <group> [ONLY <output>…] [STEPS NEUTRAL,SCAN,SOLVE,COLOR,VERIFY|ALL] [FIT INSIDE|CORNERS] [COLOR OFF|WHITE]", "Measures the outputs through the camera and stages a correction (warp, blend, colour) with its score; nothing changes on the air until ALIGN ACCEPT", VerbCategory.Align, VerbRisk.High, true) with { Show = ShowClass.Heavy },
        V2("ALIGN STATUS", "ALIGN STATUS", "The running or last run", VerbCategory.Align, VerbRisk.Low, false),
        V2("ALIGN CANCEL", "ALIGN CANCEL", "Stops a run and restores what it changed on the projectors", VerbCategory.Align, VerbRisk.Low, true) with { Show = ShowClass.Perform },
        V2("ALIGN SCORE", "ALIGN SCORE <run>|<group>", "A run's score and every term with its words and fix", VerbCategory.Align, VerbRisk.Low, false),
        V2("ALIGN ACCEPT", "ALIGN ACCEPT <run>", "Applies a staged run to its outputs as one edit; the previous corrections become the fallbacks", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN REVERT", "ALIGN REVERT <group>", "Back to each output's fallback correction", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN CORNERS", "ALIGN CORNERS <group> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> | ALIGN CORNERS <group> CLEAR", "The screen's corners in the camera picture (0–1), for an exact canvas", VerbCategory.Align, VerbRisk.Review, true),
        V2("ALIGN CLEAR", "ALIGN CLEAR <output>|<group>", "Removes measured corrections from outputs", VerbCategory.Align, VerbRisk.Review, true),
        V2("DEVICE NATIVE", "DEVICE NATIVE <device>", "A projector's own geometry and edge blend, read live (Panasonic this round; others answer with words), for the double-correction guard", VerbCategory.Device, VerbRisk.Low, false),

        // The phone as a scan camera, fully local; many views into one scan (wp2-phonescan; SCAN SOLVE/VIEWS/DROP by wp2-multiscan).
        V2("PHONE INVITE", "PHONE INVITE [<name>] [FOR <minutes 1-60>]", "A one-time link and QR code that makes a phone a scan camera (to this session only; never from an assistant)", VerbCategory.Scan, VerbRisk.High, true) with { Show = ShowClass.Setup },
        V2("PHONE LIST", "PHONE LIST", "Phones paired with this node and what their cameras can do", VerbCategory.Scan, VerbRisk.Low, false),
        V2("PHONE FORGET", "PHONE FORGET <phone>|ALL", "Forgets a phone's key and disconnects it", VerbCategory.Scan, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("SCAN NEXT", "SCAN NEXT", "The picture for the current pattern is taken (a folder camera in manual mode waits for this)", VerbCategory.Scan, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("SCAN SUGGEST", "SCAN SUGGEST <scan> [ASSISTANT]", "Measured surface suggestions for a scan (regions, and a pixel quad per planar region), each queued as a proposal a person approves (ASSISTANT: names from the node's assistant, from text only)", VerbCategory.Scan, VerbRisk.Low, false),
        V2("SCAN SOLVE", "SCAN SOLVE <scan>", "Solves every projector, view and the camera of a scan together (projector-anchored bundle adjustment)", VerbCategory.Scan, VerbRisk.Review, true),
        V2("SCAN VIEWS", "SCAN VIEWS <scan>", "A scan's views with their residuals, coverage per projector and pairing facts", VerbCategory.Scan, VerbRisk.Low, false),
        V2("SCAN DROP", "SCAN DROP <scan> <view>", "Drops a view from a scan and solves again", VerbCategory.Scan, VerbRisk.Review, true),

        // On-wall editing and the quad path (wp2-wall).
        V2("LIGHT WALL", "LIGHT WALL <display>|#<n>|<output> [AS <name>] [THROW <ratio>]", "First light in one line: an output full screen on that display as a projector, with the edit overlay and the wall cursor on", VerbCategory.Mapping, VerbRisk.High, true) with { Show = ShowClass.Layout },
        V2("SURFACE QUAD", "SURFACE QUAD <projector>|<output> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> [PX|RASTER] [AS <name>] [SOURCE <source>]", "Draws a surface as four corners in a projector's picture, clicked on the wall in any order; answers its id", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("SURFACE CORNER", "SURFACE CORNER <surface> TL|TR|BR|BL|0-3 TO <x> <y> [PX|RASTER] | SURFACE CORNER <surface> <corner> BY <dx> <dy> [PX|RASTER]", "Moves a quad's corner (BY: wall pixels by default)", VerbCategory.Mapping, VerbRisk.Review, true) with { Gesture = true },
        V2("SURFACE LATTICE", "SURFACE LATTICE <surface> <c>x<r>|OFF [LINEAR|SMOOTH|BEZIER]", "Gives a quad a lattice on top of its corners (its shape does not move)", VerbCategory.Mapping, VerbRisk.Review, true),
        V2("SURFACE NODE", "SURFACE NODE <surface> <col> <row> TO|BY <x> <y> [PX|RASTER]", "Moves a quad's lattice node", VerbCategory.Mapping, VerbRisk.Review, true) with { Gesture = true },
        V2("HANDLE SELECT", "HANDLE SELECT <surface> TL|TR|BR|BL|<col>,<row> | HANDLE SELECT NONE", "The handle the wall shows enlarged and the arrows move", VerbCategory.Mapping, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },
        V2("HANDLE NEXT", "HANDLE NEXT", "The next handle", VerbCategory.Mapping, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },
        V2("HANDLE PREV", "HANDLE PREV", "The previous handle", VerbCategory.Mapping, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },
        V2("HANDLE NUDGE", "HANDLE NUDGE <dx> <dy> [PX|RASTER]", "Moves the selected handle (encoders and dials)", VerbCategory.Mapping, VerbRisk.Review, true) with { Gesture = true },
        V2("OVERLAY", "OVERLAY ON|OFF|TOGGLE|KEEP [ON <output>|ALL] [LAYERS <cursor,outlines,names,handles,lattice,pins>|EDIT|ALL]", "The edit overlay on the wall, drawn through each output's warp; KEEP renews this session's lease (it lapses 15 s after the last)", VerbCategory.Output, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("OVERLAY GRID", "OVERLAY GRID [<output>|ALL] [<seconds 0.5-30>]", "Flashes a fine grid through the warp to judge alignment", VerbCategory.Output, VerbRisk.Review, true) with { Show = ShowClass.Setup },
        V2("CURSOR", "CURSOR <output> <x> <y> [TRAIL <x> <y>…] [COLOR #rrggbb] | CURSOR OFF", "This session's cursor on the wall (0–1 across the output)", VerbCategory.Output, VerbRisk.Low, true) with { Ephemeral = true, Show = ShowClass.Setup },

        // Explainable score, results ledger, fallback, history, re-solving the projector that moved (wp2-wall).
        V2("CALIBRATION STATUS", "CALIBRATION STATUS [<projector>|<output>|ALL]", "Each projector's and output's score with its terms, current, fallback and staged results, and the running task", VerbCategory.Calibration, VerbRisk.Low, false),
        V2("CALIBRATION HISTORY", "CALIBRATION HISTORY <projector>|<output> [<n>]", "Its results, newest first, with how far each moved the picture", VerbCategory.Calibration, VerbRisk.Low, false),
        V2("CALIBRATION SAVE", "CALIBRATION SAVE <projector>|<output>|ALL [AS <name>]", "Keeps the current calibration as a pinned result", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION RECALL", "CALIBRATION RECALL <snapshot>", "Puts a kept result back (recorded as a new result)", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION ACCEPT", "CALIBRATION ACCEPT <projector>|<output>|ALL|<snapshot>", "Accepts the current (or a staged) result: it becomes the fallback", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION FALLBACK", "CALIBRATION FALLBACK <projector>|<output>|ALL", "Back to the fallback result, exactly", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION FORGET", "CALIBRATION FORGET <snapshot>", "Forgets a kept result (never the current, fallback or staged one)", VerbCategory.Calibration, VerbRisk.Review, true),
        V2("CALIBRATION RESOLVE", "CALIBRATION RESOLVE <projector>… [CAMERA <camera>] [STAGE]", "Re-solves only the projectors named from a fresh capture of each against the stage, scores and applies it (or stages it when it fails the gate); the others are not touched", VerbCategory.Calibration, VerbRisk.High, true) with { Show = ShowClass.Heavy },
        V2("CALIBRATION CANCEL", "CALIBRATION CANCEL", "Stops a calibration task and gives the outputs back", VerbCategory.Calibration, VerbRisk.Low, true) with { Show = ShowClass.Perform },
```

Rows that answer off the control lane (each owner routes them with `VerbRoutes.OffLane`): `PREFLIGHT`, `FAILOVER`, `FAILBACK`,
`FLEET BACKUP`, `SYNC APPLY`, `SYNC LEAVE`, `SYNC REJOIN`, `DECODE PROBE`, `MEDIA INSPECT`, `MEDIA OPTIMISE`, `INPUT DISCOVER`,
`INPUT SWITCH`, `INPUT RECONNECT`, `CONTROL INJECT`, `BIND LEARN`, `CONTROL PORTS`, `CONTROL MONITOR`, `THUMB`, `FX CHECK`,
`FX IMPORT`, `FX PLUGINS`, `AUDIO LISTEN`, `SURVEY IMPORT`, `SITE CHECK`, `STACK ALIGN`, `ALIGN RUN`, `ALIGN PLAN`, `DEVICE NATIVE`,
`PHONE INVITE`, `SCAN SUGGEST`, `SCAN SOLVE`, `SCAN DROP`, `CALIBRATION RESOLVE`, `LED DISCOVER`, `FLEET ROUTE`, `FLEET PROMOTE`,
`COMPANION PRESS`, `COMPANION VAR`, `COMPANION BRIGHTNESS`.
Their project edits go back through the lane (`EditAsync`, §5.3).

---
## 5. The engine spine (`src/PlayMap.Engine`)

### 5.1 Module hooks (`EngineCore.Modules.cs`, spine-owned) and the per-package stubs

The engine-side packages are named by a short word used in every hook: **Show, Fleet, Sync, Output, Decode, Optimise,
VideoIo, Control, Companion, Fx, Audio, Content, Led, Site** (wp2-facade), **Align, Phone** (wp2-phonescan), **Wall**. A
continuation package uses its parent's word (wp2-blind: Show; wp2-d3d11: Output; wp2-zerocopy: Decode; wp2-resolve: Wall and
Align; wp2-multiscan: Phone; wp2-stacks: Site; wp2-backup: Fleet). wp2-multiview is Core-only and has no hooks. The Sync hooks
are appended to today's `EngineCore.Sync.cs`, which wp2-sync owns.

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>One line of a package's verb as its handler receives it: the matched row, the words after the verb's own, the whole
/// line and who sent it.</summary>
public sealed record VerbCall(VerbSpec Spec, List<string> Args, WireLine Line, SessionInfo Session, CancellationToken Ct);

/// <summary>The packages' verbs and where each is answered: on the control lane (the default — what it changes is the lane's), or
/// off it (work that waits on a device, a peer, the GPU, a file or a person; what it changes in the project still goes through
/// the lane with <see cref="EngineCore.EditAsync"/>). Built once in the engine's constructor; a row routed twice, or a name
/// that is not a row's own name, throws there.</summary>
public sealed class VerbRoutes
{
    private readonly Dictionary<string, Func<VerbCall, CommandReply>> _lane = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<VerbCall, Task<CommandReply>>> _offLane = new(StringComparer.Ordinal);

    public void Lane(string verb, Func<VerbCall, CommandReply> handler)
    {
        Check(verb);
        _lane.Add(verb, handler);
    }

    public void OffLane(string verb, Func<VerbCall, Task<CommandReply>> handler)
    {
        Check(verb);
        _offLane.Add(verb, handler);
    }

    public bool TryLane(string verb, [MaybeNullWhen(false)] out Func<VerbCall, CommandReply> handler) => _lane.TryGetValue(verb, out handler);

    public bool TryOffLane(string verb, [MaybeNullWhen(false)] out Func<VerbCall, Task<CommandReply>> handler) => _offLane.TryGetValue(verb, out handler);

    public bool IsRouted(string verb) => _lane.ContainsKey(verb) || _offLane.ContainsKey(verb);

    private readonly Dictionary<string, Func<IReadOnlyList<string>, SessionInfo, IPushSink, PushOpen>> _topics =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A SUBSCRIBE topic a package serves (STATE, CONTEXT, PROJECT and OFF stay the session's own).</summary>
    public void Topic(string name, Func<IReadOnlyList<string>, SessionInfo, IPushSink, PushOpen> open)
    {
        if (name is "STATE" or "CONTEXT" or "PROJECT" or "OFF")
            throw new ArgumentException($"{name} is the session's own topic", nameof(name));
        _topics.Add(name, open);
    }

    public bool TryTopic(string name, [MaybeNullWhen(false)] out Func<IReadOnlyList<string>, SessionInfo, IPushSink, PushOpen> open) =>
        _topics.TryGetValue(name, out open);

    public IEnumerable<string> Topics => _topics.Keys;

    private readonly Dictionary<string, IIndexContributor> _index = new(StringComparer.Ordinal);

    /// <summary>A package's INDEX section (§5.4): its rows and a preset hint, as data. One contributor per section name; a second
    /// throws here, at construction.</summary>
    public void Index(IIndexContributor contributor) => _index.Add(contributor.Section, contributor);

    public IEnumerable<IIndexContributor> IndexSections => _index.Values;

    private void Check(string verb)
    {
        if (VerbCatalog.Find(verb) is not { } spec || !string.Equals(spec.Name, verb, StringComparison.Ordinal))
            throw new ArgumentException($"{verb} is not a row of the catalogue", nameof(verb));
        if (IsRouted(verb))
            throw new InvalidOperationException($"{verb} is routed twice");
    }
}

public sealed partial class EngineCore
{
    private readonly VerbRoutes _routes = new();

    /// <summary>Every package's verbs, in this order (called once from the constructor).</summary>
    private void RouteModules()
    {
        RouteShow(_routes); RouteFleet(_routes); RouteSync(_routes); RouteOutput(_routes); RouteDecode(_routes); RouteOptimise(_routes);
        RouteVideoIo(_routes); RouteControl(_routes); RouteCompanion(_routes); RouteFx(_routes); RouteAudio(_routes);
        RouteContent(_routes); RouteLed(_routes); RouteSite(_routes); RouteAlign(_routes); RoutePhone(_routes); RouteWall(_routes);
    }

    /// <summary>Every package's lane work each 10 ms tick, after the playlists' own tick, with the leader's now.</summary>
    private void TickModules(long leaderNs)
    {
        TickShow(leaderNs); TickFleet(leaderNs); TickSync(leaderNs); TickOutput(leaderNs); TickControl(leaderNs); TickAudio(leaderNs);
        TickContent(leaderNs); TickLed(leaderNs); TickWall(leaderNs);
    }

    /// <summary>STATE's new top-level keys, in this order, after "cameras" (§9).</summary>
    internal void WriteModuleState(Utf8JsonWriter w)
    {
        StateShow(w); StateFleet(w); StateDecode(w); StateVideoIo(w); StateControl(w); StateCompanion(w); StateFx(w);
        StateAudio(w); StateContent(w); StateLed(w); StateWall(w); StateAlign(w); StatePhone(w);
    }

    /// <summary>Keys added at the end of existing STATE objects, in package order.</summary>
    internal void WriteAirExtras(Utf8JsonWriter w) { AirShow(w); AirWall(w); }
    internal void WriteOutputExtras(Utf8JsonWriter w, Output o) { OutputOutput(w, o); OutputFx(w, o); OutputAlign(w, o); }
    internal void WritePlaylistExtras(Utf8JsonWriter w, Playlist list) { PlaylistControl(w, list); }
    internal void WriteSyncExtras(Utf8JsonWriter w) { SyncSync(w); SyncOutput(w); SyncFleet(w); }
    internal void WriteMediaExtras(Utf8JsonWriter w) { MediaOptimise(w); }
    internal void WriteStageExtras(Utf8JsonWriter w) { StageSite(w); }
    internal void WriteProjectorExtras(Utf8JsonWriter w, Projector pj) { ProjectorSite(w, pj); ProjectorWall(w, pj); }
    internal void WriteMachineExtras(Utf8JsonWriter w) { MachineOutput(w); MachineDecode(w); }
    internal void WriteProjectExtras(Utf8JsonWriter w) { ProjectCompanion(w); }

    /// <summary>Every package's context items and links (Eye verbs only), then the fleet package's shaping for a 12-thing window.</summary>
    internal void AddModuleContext(List<ContextItem> items, List<ContextLink> links)
    {
        ContextShow(items, links); ContextFleet(items, links); ContextSync(items, links); ContextOutput(items, links); ContextDecode(items, links);
        ContextOptimise(items, links); ContextVideoIo(items, links); ContextControl(items, links); ContextCompanion(items, links);
        ContextFx(items, links); ContextAudio(items, links); ContextContent(items, links); ContextLed(items, links);
        ContextSite(items, links); ContextAlign(items, links); ContextPhone(items, links); ContextWall(items, links);
        ShapeContext(items, links);
    }

    /// <summary>Words the show and fleet packages put in front of and after the headline.</summary>
    internal string ModuleHeadline(string headline)
    {
        HeadlineShow(ref headline);
        HeadlineFleet(ref headline);
        return headline;
    }

    /// <summary>Each package's check of an entity it owns, at the end of the generic Validate (null = fine).</summary>
    internal string? ValidateModules(Project p, object entity)
    {
        string? why = null;
        ValidateShow(p, entity, ref why); ValidateFleet(p, entity, ref why); ValidateOutput(p, entity, ref why);
        ValidateDecode(p, entity, ref why); ValidateOptimise(p, entity, ref why); ValidateVideoIo(p, entity, ref why);
        ValidateControl(p, entity, ref why); ValidateFx(p, entity, ref why); ValidateAudio(p, entity, ref why);
        ValidateContent(p, entity, ref why); ValidateLed(p, entity, ref why); ValidateSite(p, entity, ref why);
        ValidateAlign(p, entity, ref why); ValidatePhone(p, entity, ref why); ValidateWall(p, entity, ref why);
        return why;
    }

    /// <summary>Runtime facts a package keeps across a restart, as one JSON object per package key in the recovery record.</summary>
    internal ImmutableSortedDictionary<string, string> SaveModuleRecovery()
    {
        var into = new SortedDictionary<string, string>(StringComparer.Ordinal);
        RecoverySaveShow(into); RecoverySaveFleet(into); RecoverySaveSync(into); RecoverySaveControl(into); RecoverySaveLed(into);
        return into.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    internal void LoadModuleRecovery(IReadOnlyDictionary<string, string> from)
    {
        RecoveryLoadShow(from); RecoveryLoadFleet(from); RecoveryLoadSync(from); RecoveryLoadControl(from); RecoveryLoadLed(from);
    }

    // The hooks. Every one is implemented in its package's stub file with an empty body that holds one comment line,
    // `// wp2-<pkg> fills this.` (Sonar S3251 fails the build on an unimplemented partial method and S1186 on an empty body,
    // both errors here), so the stubs land with the spine and each package fills its own.
    partial void RouteShow(VerbRoutes routes);      partial void RouteFleet(VerbRoutes routes);    partial void RouteOutput(VerbRoutes routes);
    partial void RouteDecode(VerbRoutes routes);    partial void RouteOptimise(VerbRoutes routes); partial void RouteVideoIo(VerbRoutes routes);
    partial void RouteControl(VerbRoutes routes);   partial void RouteCompanion(VerbRoutes routes);partial void RouteFx(VerbRoutes routes);
    partial void RouteAudio(VerbRoutes routes);     partial void RouteContent(VerbRoutes routes);  partial void RouteLed(VerbRoutes routes);
    partial void RouteSite(VerbRoutes routes);      partial void RouteAlign(VerbRoutes routes);    partial void RoutePhone(VerbRoutes routes);
    partial void RouteWall(VerbRoutes routes);      partial void RouteSync(VerbRoutes routes);
    partial void TickShow(long leaderNs); partial void TickFleet(long leaderNs); partial void TickOutput(long leaderNs);
    partial void TickControl(long leaderNs); partial void TickAudio(long leaderNs); partial void TickContent(long leaderNs);
    partial void TickLed(long leaderNs); partial void TickWall(long leaderNs); partial void TickSync(long leaderNs);
    partial void StateShow(Utf8JsonWriter w); partial void StateFleet(Utf8JsonWriter w); partial void StateDecode(Utf8JsonWriter w);
    partial void StateVideoIo(Utf8JsonWriter w); partial void StateControl(Utf8JsonWriter w); partial void StateCompanion(Utf8JsonWriter w);
    partial void StateFx(Utf8JsonWriter w); partial void StateAudio(Utf8JsonWriter w); partial void StateContent(Utf8JsonWriter w);
    partial void StateLed(Utf8JsonWriter w); partial void StateWall(Utf8JsonWriter w); partial void StateAlign(Utf8JsonWriter w);
    partial void StatePhone(Utf8JsonWriter w);
    partial void AirShow(Utf8JsonWriter w); partial void AirWall(Utf8JsonWriter w);
    partial void OutputOutput(Utf8JsonWriter w, Output o); partial void OutputFx(Utf8JsonWriter w, Output o); partial void OutputAlign(Utf8JsonWriter w, Output o);
    partial void PlaylistControl(Utf8JsonWriter w, Playlist list);
    partial void SyncSync(Utf8JsonWriter w); partial void SyncFleet(Utf8JsonWriter w); partial void SyncOutput(Utf8JsonWriter w);
    partial void MediaOptimise(Utf8JsonWriter w);
    partial void StageSite(Utf8JsonWriter w);
    partial void ProjectorSite(Utf8JsonWriter w, Projector pj); partial void ProjectorWall(Utf8JsonWriter w, Projector pj);
    partial void MachineOutput(Utf8JsonWriter w); partial void MachineDecode(Utf8JsonWriter w);
    partial void ProjectCompanion(Utf8JsonWriter w);
    partial void ContextShow(List<ContextItem> i, List<ContextLink> l); partial void ContextFleet(List<ContextItem> i, List<ContextLink> l);
    partial void ContextOutput(List<ContextItem> i, List<ContextLink> l); partial void ContextDecode(List<ContextItem> i, List<ContextLink> l);
    partial void ContextOptimise(List<ContextItem> i, List<ContextLink> l); partial void ContextVideoIo(List<ContextItem> i, List<ContextLink> l);
    partial void ContextControl(List<ContextItem> i, List<ContextLink> l); partial void ContextCompanion(List<ContextItem> i, List<ContextLink> l);
    partial void ContextFx(List<ContextItem> i, List<ContextLink> l); partial void ContextAudio(List<ContextItem> i, List<ContextLink> l);
    partial void ContextContent(List<ContextItem> i, List<ContextLink> l); partial void ContextLed(List<ContextItem> i, List<ContextLink> l);
    partial void ContextSite(List<ContextItem> i, List<ContextLink> l); partial void ContextAlign(List<ContextItem> i, List<ContextLink> l);
    partial void ContextPhone(List<ContextItem> i, List<ContextLink> l); partial void ContextWall(List<ContextItem> i, List<ContextLink> l);
    partial void ContextSync(List<ContextItem> i, List<ContextLink> l);
    partial void ShapeContext(List<ContextItem> items, List<ContextLink> links);
    partial void HeadlineShow(ref string headline); partial void HeadlineFleet(ref string headline);
    partial void ValidateShow(Project p, object e, ref string? why); partial void ValidateFleet(Project p, object e, ref string? why);
    partial void ValidateOutput(Project p, object e, ref string? why); partial void ValidateDecode(Project p, object e, ref string? why);
    partial void ValidateOptimise(Project p, object e, ref string? why); partial void ValidateVideoIo(Project p, object e, ref string? why);
    partial void ValidateControl(Project p, object e, ref string? why); partial void ValidateFx(Project p, object e, ref string? why);
    partial void ValidateAudio(Project p, object e, ref string? why); partial void ValidateContent(Project p, object e, ref string? why);
    partial void ValidateLed(Project p, object e, ref string? why); partial void ValidateSite(Project p, object e, ref string? why);
    partial void ValidateAlign(Project p, object e, ref string? why); partial void ValidatePhone(Project p, object e, ref string? why);
    partial void ValidateWall(Project p, object e, ref string? why);
    partial void RecoverySaveShow(IDictionary<string, string> into); partial void RecoverySaveFleet(IDictionary<string, string> into);
    partial void RecoverySaveControl(IDictionary<string, string> into); partial void RecoverySaveLed(IDictionary<string, string> into);
    partial void RecoveryLoadShow(IReadOnlyDictionary<string, string> from); partial void RecoveryLoadFleet(IReadOnlyDictionary<string, string> from);
    partial void RecoveryLoadControl(IReadOnlyDictionary<string, string> from); partial void RecoveryLoadLed(IReadOnlyDictionary<string, string> from);
    partial void RecoverySaveSync(IDictionary<string, string> into); partial void RecoveryLoadSync(IReadOnlyDictionary<string, string> from);
}
```

The spine calls these from the files it owns: `EngineCore` constructor (`RouteModules()`), `Tick` (`TickModules(now)` after
`ExpireViews()`), `StateWriter.WriteState` (`e.WriteModuleState(w)` after `"cameras"`; the extras at the end of `air`, each
output row, each playlist row, `sync`, `media`, `stage`, each `stage.projectors[]` row, `machine` and `project`),
`ContextBuilder.Build` (`engine.AddModuleContext(items, links)` after `AddOpsContext`, then `headline = engine.ModuleHeadline(headline)`),
`EngineCore.Edits.Validate` (`ValidateModules`), `RecoveryRecord.Capture` (`Modules = engine.SaveModuleRecovery()`) and
`RestoreAsync` (`LoadModuleRecovery(record.Modules)` last). `RecoveryRecord` gains
`public ImmutableSortedDictionary<string, string> Modules { get; init; } = ImmutableSortedDictionary<string, string>.Empty;`.

**Stub files the spine creates** (each then owned by its package; every hook above has an empty implementation in its package's
file): `src/PlayMap.Engine/EngineCore.<Pkg>.cs` for the seventeen package words (for Sync, hunks in today's `EngineCore.Sync.cs`); `src/PlayMap.Engine/EngineServices.<Pkg>.cs`
(the init properties of §5.4); `src/PlayMap.Engine/Host/EngineParts.<Pkg>.cs` (§5.5); `src/PlayMap.Engine/Rendering/RenderHost.<Pkg>.cs`
(§6.2); `src/PlayMap.Render/Shaders/ShaderLibrary.<Pkg>.cs` (§6.3); `src/PlayMap.Core/Bus/NodeSettings.<Pkg>.cs` (§8);
`src/PlayMap.Engine/Host/EngineEntry.Optimise.cs` (§5.5); `tests/fixtures/state/<pkg>.json` and `<pkg>.cards.json` (`{}` and `[]`
until the package writes them). A script generates them all (`tools/gen_stubs.py`, kept for the amendment lane).

### 5.2 The router and the gate (spine hunks in `EngineCore.Router.cs`, `EngineCore.Ops.cs`)

```csharp
// EngineCore.Modules.cs
/// <summary>The show's door for a line about to run — on the lane, or at the start of an off-lane verb (reading only published
/// state there). Sets <paramref name="refused"/> to refuse it with words, or <paramref name="lineStore"/> to send the line's edits
/// to a blind draft. wp2-show implements it (wp2-blind adds the redirection); its stub lets every line pass.</summary>
partial void GateLine(VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused, ref ProjectStore? lineStore);

/// <summary>The blind draft a session's off-lane edits go to (null = the live store). wp2-blind (show's continuation) fills it in show's stub.</summary>
private partial ProjectStore? BlindStoreFor(SessionInfo session);   // stub in EngineCore.Show.cs: => null;
```

`Route(line, session, ct)` after the match and the pairing check (now `spec.Mutates || spec.PairedOnly`) and the `Internal` check:

```csharp
CommandReply? refused = null;
ProjectStore? lineStore = null;
GateLine(spec, line, session, ref refused, ref lineStore);
if (refused is not null)
    return refused;
_lineStore = lineStore;
try
{
    if (_routes.TryLane(spec.Name, out var handler))
        return handler(new VerbCall(spec, args, line, session, ct));
    return spec.Name switch { /* today's switch, unchanged */ };
}
finally
{
    _lineStore = null;
}
```

`HandleAsync` sends a line to `_routes.TryOffLane` before `TryOps`; the off-lane path (module routes and `RunOpsAsync`) checks
pairing, `Internal`, then `GateLine` (refusals are journalled as today), then runs the handler; its journal row follows the
router's rules. The **gate order** for every caller: pairing → `GateLine` (Show Mode policy, blind redirection, MinGo/EXPECT) →
the handler → on apply, `ProjectStore.Guard` (locks). `VerbCatalog.RiskOf` decides only whether an assistant's line runs or waits
for a person (MCP and the assistant's `CommandGate`, before the line reaches `HandleAsync`); they also call
`IShowGate.Decide` first so an armed refusal reads "proposed" rather than "refused" (§5.4).

### 5.3 `Edits` and `EditAsync` (spine hunks; every edit handler converted by the spine)

```csharp
// EngineCore.cs
private ProjectStore? _lineStore;

/// <summary>Where this line's document edits go: the session's blind draft while it edits blind, else the live store. Set and
/// cleared inside Route on the lane only; transport, air, show state and runtime facts use Store.</summary>
internal ProjectStore Edits => _lineStore ?? Store;

/// <summary>An off-lane verb's document edit: posted to the lane, applied to the session's blind draft or the live store, answered
/// with the store's own result (refusals included).</summary>
public Task<EditResult> EditAsync(SessionInfo session, string label, Func<Project, Project> edit, string? coalesceKey = null) =>
    _lane.RunAsync(() => (BlindStoreFor(session) ?? Store).Apply(label, edit, coalesceKey));
```

The spine converts every `Store.Apply`, `Store.Undo` and `Store.Redo` in the document-edit handlers of
`EngineCore.{Edits,Content,Mapping,Devices,Playlist parts of Transport,BlendSolve,Views}.cs` and `Scanning/ScanRunner.cs` /
`ScanSurfaces` to `Edits.…` (or `EditAsync` off the lane); transport, air, focus, pattern, identify and anchors keep `Store`/
`SetAir`. A spine test runs every edit verb with a fake `_lineStore` and asserts `Store.Current.Revision` does not move.
`ProjectStore` gains (spine; logic of the guard is wp2-show's):

```csharp
/// <summary>Checked before Apply, Undo and Redo publish: (before, after) → refusal words, or null to let it through.</summary>
public Func<Project, Project, string?>? Guard { get; set; }

/// <summary>Publishes a new root with no undo entry (locks): an undo afterwards restores the older root with these fields re-applied
/// by <paramref name="keep"/>, so undo never unlocks.</summary>
public EditResult ApplyUnrecorded(string label, Func<Project, Project> edit, Func<Project, Project, Project>? keep = null);

/// <summary>Several edits as one undo step and one Changed (wildcard OSC, MOD AUTO, LED IMPORT, BLIND COMMIT).</summary>
public EditResult ApplyGroup(string label, IReadOnlyList<Func<Project, Project>> edits);
```

### 5.4 Cross-package seams (`src/PlayMap.Engine/Seams.Round2.cs`, spine-owned)

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>Show Mode as every caller reads it: the router's gate (wp2-show), MCP and the assistant (proposal instead of refusal),
/// Companion's WHOAMI (wp2-companion), optimise and heavy work (refused while armed), the edit overlay (off while armed).</summary>
public interface IShowGate
{
    bool Armed { get; }
    string ArmedBy { get; }
    /// <summary>The session that owns the blind draft, or null.</summary>
    string? BlindOwner { get; }
    int MinGoMs { get; }
    ShowRole RoleOf(SessionInfo session);
    /// <summary>What the show's table says about this line from this session now (pure; published state only).</summary>
    ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session);
    /// <summary>The verbs this session may run now (null = every verb) — for greying keys before they are pressed.</summary>
    IReadOnlyCollection<string>? Allowed(SessionInfo session);
}

/// <summary>Rehearsal, everything allowed: the gate before wp2-show lands and on a bare core.</summary>
public sealed class OpenShowGate : IShowGate
{
    public static readonly OpenShowGate Instance = new();
    public bool Armed => false;
    public string ArmedBy => "";
    public string? BlindOwner => null;
    public int MinGoMs => 0;
    public ShowRole RoleOf(SessionInfo session) => session.Transport == "desk" ? ShowRole.Operator : ShowRole.Remote;
    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) => new(ShowVerdict.Allow, null);
    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => null;
}

/// <summary>A degrade gain per output that the air multiplies in (a stack that lost a member dims its blend group); 1 = none.
/// Read on the render thread: lock-free.</summary>
public interface IOutputGains
{
    float GainOf(string outputId);
}

/// <summary>The calibration ledger (wp2-wall) as the packages that make results use it: align and stacks stage and accept output
/// results; PROJECTOR SOLVE and SCAN APPLY record projector results. Every call is an edit on the lane.</summary>
public interface ICalibrationLedger
{
    /// <summary>Records a result and makes it current (applied) or staged; answers its id. The edit that applies the projector's
    /// pose or the output's correction is the same edit (one undo step).</summary>
    Task<(EditResult Result, string SnapshotId)> RecordAsync(SessionInfo session, CalibrationSnapshot snapshot, bool apply, CancellationToken ct);
    Task<EditResult> AcceptAsync(SessionInfo session, string targetOrSnapshotId, CancellationToken ct);
    Task<EditResult> FallbackAsync(SessionInfo session, string targetId, CancellationToken ct);
    QualityScore ScoreOf(Project project, string targetId);
}

/// <summary>One preflight section's checks (machine, outputs, media, mapping, devices, sync, links, safety — wp2-show; video I/O,
/// decode, fleet, control … — their packages), run with a time budget; an overrun is a grey row "did not finish in 5 s".</summary>
public interface IPreflightCheck
{
    string Section { get; }
    PreflightDepth MinDepth { get; }
    TimeSpan Budget { get; }
    Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct);
}

public sealed record PreflightContext(EngineCore Engine, PreflightDepth Depth, bool Armed);

/// <summary>Posters by content hash (wp2-optimise makes them; wp2-companion's THUMB reads them first, decoding one frame when
/// absent).</summary>
public interface IPosterStore
{
    bool TryGet(string contentHash, int maxWidth, int maxHeight, out ReadOnlyMemory<byte> jpeg, out int width, out int height);
}

/// <summary>The group's air across machines (wp2-sync implements it; wp2-show and wp2-blind call it). A line sent here runs on
/// every follower and backup of the group this node leads, at the given leader instant, through each member's own gate.</summary>
public interface IFleetFanout
{
    /// <summary>The members a line would reach now (followers and backups, by node id), empty when this node leads no group.</summary>
    IReadOnlyList<string> Members { get; }
    /// <summary>Sends an air line (PANIC…, PANIC CLEAR…, BLACKOUT…, MASTER…, SHOW ARM|DISARM, PREFLIGHT) as an anchored air line;
    /// answers each member's receipt (accepted, applied with its leader instant, or refused with its words; silent members time
    /// out with words).</summary>
    Task<IReadOnlyList<FanoutReceipt>> SendAsync(string line, long atLeaderNs, CancellationToken ct);
}

public sealed record FanoutReceipt(string Node, string Name, string Stage, long AppliedLeaderNs, string Words);

/// <summary>A package's section of INDEX (wp2-companion writes INDEX): the entities a controller can act on, and how a deck should
/// lay them out — data, never code (Patterns' "menus as data"). Registered with VerbRoutes.Index from the package's Route hook.</summary>
public interface IIndexContributor
{
    /// <summary>The section's key ("fx", "led", "video", "calibration", "eye", "scan", …); one per package section.</summary>
    string Section { get; }
    /// <summary>The preset section the Companion module builds for these rows.</summary>
    IndexPresetHint Hint { get; }
    /// <summary>Writes the section's rows as a JSON array of {"id","kind","label","parent","light","state"} from published state
    /// only (any thread; no lane work).</summary>
    void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w);
}

/// <summary>One key template per row: its label, the line its press sends ("$(id)" is the row's id), the feedback that lights it,
/// and the press shape ("instant", "hold", "double").</summary>
public sealed record IndexKey(string Label, string DownLine, string? Feedback, string Press);
public sealed record IndexPresetHint(string Title, string RowKind, ImmutableArray<IndexKey> Keys);
```

**Additions to existing seams** (`Seams.cs`, spine hunks, default members so fakes compile unchanged):

```csharp
// IMediaBackend
    /// <summary>The renderer's device for decoders opened for it (wp2-output makes it; wp2-decode's backend uses it); null when the
    /// render context goes.</summary>
    void UseGpu(IGpuShare? share) { }
    /// <summary>What the renderer samples and its upload arena (wp2-decode).</summary>
    void UseRenderer(BlockFormats formats, IUploadArena? arena) { }

// IScanDisplay
    /// <summary>An 8-bit RGB frame (interleaved) at the output's raster; completes at its present (ns) (wp2-align).</summary>
    Task<long> ShowRgbAsync(string outputId, ReadOnlyMemory<byte> rgb24, int width, int height, CancellationToken ct) =>
        Task.FromException<long>(new NotSupportedException("this display shows no colour frames"));

// Scanning/ScanCamera.cs — CameraFrame body (owner wp2-phonescan)
    /// <summary>Interleaved RGB when the camera delivers colour (wp2-align's photometry).</summary>
    public byte[]? Rgb { get; init; }
    /// <summary>16-bit grey when the camera averages or delivers more than 8 bits (phone, folder of TIFFs).</summary>
    public ushort[]? Gray16 { get; init; }
```

**`EngineServices` init properties the spine declares** (each in its package's `EngineServices.<Pkg>.cs`; the package adds any
other property it needs in the same file):

| File | Property | Set by | Read by |
|---|---|---|---|
| `EngineServices.Show.cs` | `IShowGate ShowGate { get; init; } = OpenShowGate.Instance;` · `ImmutableArray<IPreflightCheck> PreflightChecks { get; init; } = [];` | wp2-show (gate); every package adds its checks in its extension's `Configure` (`services with { PreflightChecks = [.. services.PreflightChecks, check] }`) | all |
| `EngineServices.Site.cs` | `IOutputGains? OutputGains { get; init; }` | wp2-facade | wp2-show (`AirLevels`) |
| `EngineServices.Wall.cs` | `ICalibrationLedger? Ledger { get; init; }` | wp2-wall | wp2-align, wp2-facade |
| `EngineServices.Control.cs` | `PlayMap.Core.Dmx.IArtNetEndpoint? ArtNet { get; init; }` | wp2-control | wp2-led |
| `EngineServices.Audio.cs` | `PlayMap.Core.Modulation.IModulationBus? Modulation { get; init; }` · `PlayMap.Core.Modulation.IAudioFrames? AudioFrames { get; init; }` · `PlayMap.Core.Audio.IAudioInputs? AudioInputs { get; init; }` (the hub; audio's parts configure before control's, §5.5, and control resolves it at start, falling back to its own synthetic/file input when absent) | wp2-audio | wp2-fx, wp2-content, wp2-led (via the render host), wp2-control |
| `EngineServices.Sync.cs` | `IFleetFanout? Fanout { get; init; }` | wp2-sync | wp2-show, wp2-blind |
| `EngineServices.VideoIo.cs` | the provider lists of §3.8 | wp2-videoio, wp2-d3d11 (Spout) | wp2-videoio |
| `EngineServices.Optimise.cs` | `IPosterStore? Posters { get; init; }` | wp2-optimise | wp2-companion |

### 5.5 Host parts (`Host/EngineParts.cs` hunk, spine-owned)

```csharp
    // EngineParts.Standard(): after AddExtensions(extensions), in this order (an extension's Configure sees every earlier one's services):
    AddShowParts(extensions); AddFleetParts(extensions); AddSyncParts(extensions); AddOutputParts(extensions); AddDecodeParts(extensions);
    AddAudioParts(extensions); AddControlParts(extensions); AddOptimiseParts(extensions); AddVideoIoParts(extensions);
    AddCompanionParts(extensions); AddFxParts(extensions); AddContentParts(extensions); AddLedParts(extensions);
    AddSiteParts(extensions); AddWallParts(extensions); AddAlignParts(extensions); AddPhoneParts(extensions);

    static partial void AddShowParts(List<EngineExtensionFactory> extensions);   // … one per package word, implemented in
    // Host/EngineParts.<Pkg>.cs (stub: empty)
```

`ExtensionSetup` gains `public Action<INodeFaults>? AddFaults { get; init; }` — an extension hands its faults source to the host,
whose `HostFaults` concatenates them into STATE `faults` (control's ports, the NDI runtime, the phone page's port …).
`NetworkHost` gains (spine) `public void AddWebModule(IWebModule module)` (before start; §7) and the push-topic registry (§7).

**Entry points and the node host** (spine). `Host/EngineEntry.cs` gains `public static bool TryDispatch(string[] args, out int
exit)`: `engine [headless] …` as today, then each package's `static partial void Dispatch<Pkg>(string[] args, ref int? exit)` (stub
`Host/EngineEntry.Optimise.cs`, where wp2-optimise adds `optimise-worker`). App's `Program.cs` calls `TryDispatch` before Avalonia
(a W0b hunk; the desk owns the file afterwards). `tools/PlayMap.Node/PlayMap.Node.csproj` is a console `Exe` that references only
`PlayMap.Engine`, whose `Main` is `TryDispatch` or exit 2 with words: tests and the Companion module's e2e launch it (≈ 150 MB built,
against App's 832 MB). `Engine.Tests` references it with `ReferenceOutputAssembly="false"` so it is built with the tests;
`NodeExe.Locate()` finds it.

### 5.6 Air, snapshot and events (spine hunks in `EngineCore.cs`)

```csharp
// AirState — record body (init; Initial unchanged)
    /// <summary>The grand master over every output (wp2-show).</summary>
    public AirFade Master { get; init; } = AirFade.Full;
    /// <summary>A panic in force, or null (wp2-show).</summary>
    public PanicAir? Panic { get; init; }
    /// <summary>The edit overlay on the wall (wp2-wall).</summary>
    public OverlayState Overlay { get; init; } = OverlayState.Off;

// EngineSnapshot — record body
    /// <summary>The blind draft, for the desk's draft view only (never an output) (wp2-blind; the editor view `VIEW … SHOW editor DRAFT`).</summary>
    public ProjectSnapshot? Draft { get; init; }
    /// <summary>A blind commit scheduled at a leader instant: the render thread draws Root from that frame on (wp2-blind).</summary>
    public PendingCommit? Pending { get; init; }

/// <summary>A blind commit waiting for its boundary.</summary>
public sealed record PendingCommit(Project Root, long EffectiveLeaderNs, string Label, int Changes);

// Events (EngineCore.Modules.cs) — wp2-companion keeps the ring and serves EVENTS; packages raise from day one.
/// <summary>A show event for automations, raised on the thread that saw it; <paramref name="atLeaderNs"/> is when it happened on the
/// show clock (an item's end is its scheduled instant, not when the lane noticed).</summary>
public void RaiseEvent(string kind, long atLeaderNs, Action<Utf8JsonWriter>? fields = null);
public event Action<ShowEvent>? EventRaised;

public sealed record ShowEvent(long Seq, string Kind, long AtLeaderNs, long AtUnixMs, string FieldsJson);
```

Event kinds (each raised by its owner): `item.start`, `item.end` (spine raises both from `PlaybackLanes.Changed`),
`air.blackout`, `air.freeze` (spine, from `SetAir`), `show.armed`, `show.disarmed`, `show.blind`, `show.commit`, `panic`,
`panic.clear`, `lock`, `preflight.done`, `refused` (≤ 1/s) (wp2-show); `fault.raised`, `fault.cleared` (wp2-companion, by diffing
STATE faults); `fleet.node.gone`, `fleet.node.back` (wp2-fleet); `fleet.suspect`, `fleet.takeover`, `fleet.fenced`, `sync.promoted`
(wp2-backup); `sync.stepped`, `sync.leader.lost`, `sync.mixedrate` (wp2-sync); `sync.fellout` (wp2-output); `calibration.moved`, `calibration.staged` (wp2-wall); `align.staged` (wp2-align);
`optimise.done`, `optimise.failed`, `optimise.suggested` (wp2-optimise); `input.lost`, `input.back` (wp2-videoio);
`chase.locked`, `chase.lost` (wp2-control); `scan.done`, `proposal.waiting` (wp2-phonescan).

---

## 6. The render spine (`src/PlayMap.Render`, `src/PlayMap.Engine/Rendering`)

### 6.1 Frame inputs, passes, output stage (spine hunks)

```csharp
// Render/Engine/Contracts.cs — FrameInputs gains (init; a struct, so no initialisers: null means "none")
    /// <summary>Per-output gain, safe-look override and hold (wp2-show). Null = gain 1, no override, no hold.</summary>
    public IAirLevels? Levels { get; init; }
    /// <summary>Modulation sources for bindings (wp2-audio's bus; wp2-fx's built-ins when audio is absent). Null = none.</summary>
    public IModulationBus? Modulation { get; init; }
    public IAudioFrames? Audio { get; init; }
    /// <summary>The edit overlay and every session's cursor (wp2-wall); off when Armed.</summary>
    public OverlayState? Overlay { get; init; }
    public ImmutableArray<WallCursor> Cursors { get; init; }
    public bool Armed { get; init; }
    /// <summary>Extra work at fixed points of the frame (effect chains, overlay, sends, LED sampling), in Order.</summary>
    public ImmutableArray<IFramePass> Passes { get; init; }
    /// <summary>Each output's measured correction as the output stage draws it (wp2-align). Null = none.</summary>
    public IMeasuredStages? Measured { get; init; }

/// <summary>Per-output air the render thread reads every frame: built on the lane once per air change, allocation-free to read.</summary>
public interface IAirLevels
{
    /// <summary>Master × panic level × degrade gain at the frame's leader instant (1 = untouched).</summary>
    float Gain(string outputId, long leaderNs);
    /// <summary>The safe look while a SAFE outcome shows it, else null.</summary>
    SourceRef? Override(string outputId, long leaderNs);
    /// <summary>Hold this output's last picture (the FREEZE outcome).</summary>
    bool Hold(string outputId, long leaderNs);
    /// <summary>The air's sound gain (master × panic) at the leader instant, from the same fades as the picture.</summary>
    float AudioGain(long leaderNs) => 1f;
}

/// <summary>Per output, the measured correction the output stage draws (wp2-align fills it; built on the lane, read lock-free).</summary>
public interface IMeasuredStages
{
    MeasuredStage? For(string outputId);
}

/// <summary>A pass a package adds to the frame. Every method runs on the render thread, must not block, and allocates nothing
/// once warm. Blackout wins before any pass; raw scan frames skip <see cref="Picture"/> and <see cref="Final"/>.</summary>
public interface IFramePass : IDisposable
{
    string Name { get; }
    int Order { get; }
    /// <summary>A playlist item's picture while its playlist shows it (each side of a transition on its own, before the mix): may
    /// return another picture (an effect chain's result). startNs is the leader instant the item started, the same on every node.</summary>
    GpuTexture Item(in PassFrame frame, PlaylistItem item, long startNs, GpuTexture picture) => picture;
    /// <summary>A canvas layer's picture, its source resolved at the layer's rectangle, before anything is drawn into the canvas.</summary>
    GpuTexture Layer(in PassFrame frame, Layer layer, GpuTexture picture) => picture;
    /// <summary>A stage surface's content picture, made at one size for the frame (the largest projector raster) before any
    /// projector pass draws it; every projector samples the result through the surface's UVs.</summary>
    GpuTexture Surface(in PassFrame frame, Surface surface, GpuTexture picture) => picture;
    /// <summary>After an output's picture is made, before its output stage (warp, blend, trim): may return another picture
    /// (an effect chain's result), drawn on <paramref name="picture"/>'s own scale; the overlay's pre-warp layer draws here.</summary>
    GpuTexture Picture(in PassFrame frame, Output output, GpuTexture picture) => picture;
    /// <summary>After the output stage drew the output's final raster (and its identify card): the post-warp overlay, final taps.</summary>
    void Final(in PassFrame frame, Output output, RenderTarget final) { }
    /// <summary>After every output is drawn, before any present: LED sampling, send packing and readback.</summary>
    void FrameEnd(in PassFrame frame) { }
}

public readonly record struct PassFrame(long Frame, long LeaderNs, Project Project, GeneratorFrame Generator, GlDevice Device,
    bool Blackout, bool Freeze)
{
    /// <summary>The air's test pattern, or null; the outputs it covers (empty = every output); per-output gain, safe look, hold.</summary>
    public SourceRef? PatternOverride { get; init; }
    public ImmutableArray<string> PatternOutputs { get; init; }
    public IAirLevels? Levels { get; init; }
    /// <summary>The output shows the air's test pattern or a panic's safe look this frame instead of the show.</summary>
    public bool Covered(string outputId) =>
        FrameInputs.PatternCovers(PatternOverride, PatternOutputs, outputId) || Levels?.Override(outputId, LeaderNs) is { IsNone: false };
    /// <summary>The output holds its last picture this frame (FREEZE, or a FREEZE outcome on it).</summary>
    public bool Held(string outputId) => Freeze || (Levels?.Hold(outputId, LeaderNs) ?? false);
}

// Render/Engine/Contracts.cs — ISourceProvider names the item on the air (default null): `ItemOnAir? Item(SourceRef source,
// long leaderNs)` with `record struct ItemOnAir(PlaylistItem Item, long StartNs)`; a SourceMix carries `FromItem` and `ToItem`
// for each side of a transition. SourceResolver calls each pass's Item on a playlist's picture (each side before the mix) only
// when the item's chain draws, each pass's Layer on a layer's picture before it is drawn into the canvas, and each pass's
// Surface on a surface's content picture before the projector passes; `ResolvePlain` (internal) resolves a picture with no
// pass applied, for taps that must see the source as it is. Effect chains attach through these hooks (FxPass); no package
// keeps its own attach list.

// Render/Engine/RenderEngine.cs — the output call points (spine): in DrawOutput after `picture` is chosen, each pass's Picture in
// Order; after _outputStage.Draw and DrawIdentify, each pass's Final; in Render after the output loop and before the presents,
// each pass's FrameEnd. RenderEngine also exposes `public GpuTexture? CanvasPicture(string canvasId)` and
// `public GpuTexture? SourcePicture(SourceRef source, int width, int height)` for passes that tap them (sends, LEDs).
//
// The air path, end to end (spine, W0b — no package edits RenderEngine or OutputStage for it):
//   • per output, before the picture is chosen: `inputs.Levels?.Override(o.Id, leaderNs)` is not null → the picture is that source
//     (the SAFE outcome, drawn like a pattern override);
//   • `inputs.Levels?.Hold(o.Id, leaderNs)` → this output keeps its last picture (FREEZE's hold, per output; today's global FREEZE
//     path unchanged);
//   • `new OutputStageInputs(...) { Gain = inputs.Levels?.Gain(o.Id, leaderNs), Measured = inputs.Measured?.For(o.Id) }`.

// Render/Passes/OutputStage.cs — OutputStageInputs gains (init)
    /// <summary>The air's gain multiplied last, after the pedestal and the dither, so 0 is true black (wp2-show's levels). Null = 1.</summary>
    public float? Gain { get; init; }
    /// <summary>The output's measured correction as the stage draws it (wp2-align: grid, blend and pedestal, gains, γ).</summary>
    public MeasuredStage? Measured { get; init; }
// OutputStage.Draw sets `uAir` to `Gain ?? 1f` on every draw (a GLSL uniform left unset reads 0, and ES 3.00 has no uniform
// initialisers), and the spine's `output` program ends with `color.rgb *= uAir;`. MeasuredStage is a class in
// Render/Passes/MeasuredStage.cs (owner wp2-align; created by the spine with the fields `GpuTexture? WarpGrid; GpuTexture? Blend;
// Vector4 Gains; float Gamma; float Pedestal;`); a stack's residual lattice is not here: wp2-stacks draws it as the first layer of
// Core's WarpField/WarpMeshBuilder, through the existing warp mesh.

// Render/Generators/GeneratorRenderer.cs — GeneratorFrame gains (init)
    public long LeaderNs { get; init; }
    public IModulationBus? Modulation { get; init; }
    public IAudioFrames? Audio { get; init; }
```

### 6.2 Render host hooks (`Engine/Rendering/RenderHost.cs` hunk; stubs `RenderHost.<Pkg>.cs`)

```csharp
    // In the frame loop, after the FrameInputs initialiser and before renderer.Render(in inputs):
    ComposeShow(engine, snap, ref inputs); ComposeOutput(engine, snap, ref inputs); ComposeDecode(engine, snap, ref inputs);
    ComposeVideoIo(engine, snap, ref inputs); ComposeFx(engine, snap, ref inputs); ComposeAudio(engine, snap, ref inputs);
    ComposeContent(engine, snap, ref inputs); ComposeLed(engine, snap, ref inputs); ComposeAlign(engine, snap, ref inputs);
    ComposeSite(engine, snap, ref inputs); ComposeWall(engine, snap, ref inputs);
    // Once the context exists (render thread) and when it goes:
    OnContextShow(renderer); OnContextOutput(renderer); … (same order) / OnContextLostShow(); …

    partial void ComposeShow(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);   // … one per word above
    partial void OnContextShow(RenderEngine renderer);
    partial void OnContextLostShow();
```

wp2-show's compose sets `Levels`; wp2-blind's (same hook, show's continuation) picks the pending blind root per frame
(`snap.Pending is { } p && inputs.LeaderNs >= p.EffectiveLeaderNs ? p.Root : …`); wp2-align's sets `Measured`; wp2-output replaces
the window path with its presenter (its own refactor of `OutputWindows`, `RenderEngine.Attach` and the pacing, inside the files it
owns per `PLAN.md`); the others add their passes and inputs.

### 6.3 Shader programs (`Render/Shaders/ShaderLibrary.cs` hunk; stubs `ShaderLibrary.<Pkg>.cs`)

```csharp
public static partial class ShaderLibrary
{
    /// <summary>Programs the packages add, by name: (vertex, fragment).</summary>
    private static readonly FrozenDictionary<string, (string Vertex, string Fragment)> Added = BuildAdded();

    private static FrozenDictionary<string, (string, string)> BuildAdded()
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        AddShow(d); AddOutput(d); AddDecode(d); AddOptimise(d); AddVideoIo(d); AddFx(d); AddContent(d); AddLed(d); AddAlign(d); AddWall(d);
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }

    static partial void AddShow(Dictionary<string, (string, string)> programs);   // … one per word, stub with a comment body
    // Source(name): today's switch; its default arm answers Added[name] before throwing. Names = today's ∪ Added.Keys.
    // Edits to existing programs are hunks named in PLAN.md §4 (zerocopy: `yuv` uCrop/uMaxUv; align: `output` measured grid,
    // blend and pedestal, gains, γ). The spine itself adds `uniform float uAir;` and the final multiply to `output`.
}
```

**The dialect convention** (R11; spine, W0b). Today every program constant starts with `Header` (`"#version 330 core\n"`) and
generators compile through `GlDevice.CreateProgram(name, vs, fs)` without passing through `ShaderLibrary.Source`. From W0b:

```csharp
// Render/Gpu/GlDialect.cs (spine creates; wp2-output owns afterwards and adds Es300, the per-dialect format table and the alias loader)
public enum GlDialect { Core330 }

public static class GlDialects
{
    /// <summary>The first lines of every program for the dialect ("#version 330 core\n"; Es300 adds its precision lines).</summary>
    public static string Header(GlDialect dialect) => dialect switch
    {
        GlDialect.Core330 => "#version 330 core\n",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };
}

// GlDevice: public GlDialect Dialect { get; }   (from the context; Core330 until wp2-output adds Es300)
// GlDevice.CreateProgram(name, vertex, fragment): refuses a source whose first non-blank line starts with "#version" or holds a
// "precision" statement (ArgumentException with words), then compiles Header(Dialect) + source.
```

Every program in `ShaderLibrary`, the generators, the patterns and the passes is converted (the `Header +` prefix removed);
`ShaderConventionTests` keeps it so. wp2-output (W1, first in the merge order) adds `Es300` and an ES test fixture; wp2-fx and
wp2-content write sources with no header from day one.

### 6.4 Texture formats (`Render/Gpu/GpuTypes.cs` and `GlDevice.Resources.cs` hunks, spine-owned)

Four packages need new formats (decode: block-compressed; content and fx: four-channel float targets; videoio: 16-bit
unorm; output: 10-bit). The enum values and the format tables land once, here; each package's use of them (the compressed
upload path, 3D textures, MRT) is in its own `GlDevice.<Pkg>.cs` partial file (`GlDevice` is already partial).

```csharp
// GpuTypes.cs — TextureFormat gains (appended; the existing values keep their order)
    /// <summary>Four 32-bit float channels (particle state, accumulation).</summary>
    Rgba32F,
    /// <summary>Four 16-bit unsigned normalised channels (P216/PA16 unpack, deep sends).</summary>
    Rgba16,
    /// <summary>10-bit colour, 2-bit alpha (the 10-bit output picture).</summary>
    Rgb10A2,
    // Block-compressed (4×4 blocks; sampled only, never a render target). Present only when the context's extensions say so
    // (GlCaps below); an absent format refuses with words, it never falls back silently.
    Bc1,
    Bc1Alpha,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
    Bc6hUfloat,
    Bc6hSfloat,

// GpuTypes.cs — UploadFormat gains (appended)
    /// <summary>16-bit unsigned, four channels.</summary>
    Rgba16,

// GpuTypes.cs — new
public static class TextureFormats
{
    /// <summary>Named, never by enum order: formats are appended, and a later uncompressed one must not read as a block format.</summary>
    public static bool IsBlockCompressed(TextureFormat f) => f is TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc3
        or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat;

    /// <summary>Bytes per 4×4 block (8 for BC1 and BC4, 16 for BC3, BC5, BC7 and BC6H); 0 for an uncompressed format.</summary>
    public static int BlockBytes(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc4 => 8,
        TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat => 16,
        _ => 0,
    };

    /// <summary>Bytes of one level of a block-compressed texture (sizes round up to whole blocks).</summary>
    public static long CompressedSize(TextureFormat f, int width, int height) =>
        (long)((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(f);
}

/// <summary>What the context in hand can sample and render to, from its version and extensions (never from
/// GL_COMPRESSED_TEXTURE_FORMATS, which drivers under-report); filled once per context by the render engine. One name for it
/// everywhere (hap.md's "BlockCaps" is this record).</summary>
public sealed record GlCaps(bool Dxt1, bool Dxt5, bool Rgtc, bool Bptc, bool Norm16, bool FloatBlend, bool ColorBufferFloat,
    string Dialect)
{
    public static readonly GlCaps None = new(false, false, false, false, false, false, false, "core330");

    public bool IsCore => Dialect == "core330";

    /// <summary>Pure, so tests feed fake extension lists. Desktop: RGTC is core from 3.0, BPTC from 4.2; ES needs the EXT names.
    /// S3TC gives DXT1 and DXT5; EXT_texture_compression_dxt1 gives DXT1 only; ANGLE_texture_compression_dxt5 gives DXT5.</summary>
    public static GlCaps From(string dialect, int major, int minor, IReadOnlySet<string> ext)
    {
        var core = dialect == "core330";
        var s3tc = ext.Contains("GL_EXT_texture_compression_s3tc");
        return new GlCaps(
            Dxt1: s3tc || ext.Contains("GL_EXT_texture_compression_dxt1"),
            Dxt5: s3tc || ext.Contains("GL_ANGLE_texture_compression_dxt5"),
            Rgtc: (core && major >= 3) || ext.Contains("GL_ARB_texture_compression_rgtc") || ext.Contains("GL_EXT_texture_compression_rgtc"),
            Bptc: (core && (major > 4 || (major == 4 && minor >= 2))) || ext.Contains("GL_ARB_texture_compression_bptc")
                  || ext.Contains("GL_EXT_texture_compression_bptc"),
            Norm16: core || ext.Contains("GL_EXT_texture_norm16"),
            FloatBlend: core || ext.Contains("GL_EXT_float_blend"),
            ColorBufferFloat: core || ext.Contains("GL_EXT_color_buffer_float"),
            Dialect: dialect);
    }

    /// <summary>Can it be sampled? (Block formats only through the compressed upload path.)</summary>
    public bool Supports(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc1Alpha => Dxt1,
        TextureFormat.Bc3 => Dxt5,
        TextureFormat.Bc4 or TextureFormat.Bc5 => Rgtc,
        TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat => Bptc,
        TextureFormat.R16 or TextureFormat.Rg16 or TextureFormat.Rgba16 => Norm16,
        _ => true,
    };

    /// <summary>Can it be a render target (and, for 32-bit float, be blended)? Block formats never are.</summary>
    public bool SupportsRenderTarget(TextureFormat f, bool blended = false) => f switch
    {
        _ when TextureFormats.IsBlockCompressed(f) => false,
        TextureFormat.R16 or TextureFormat.Rg16 or TextureFormat.Rgba16 => Norm16,
        TextureFormat.R16F or TextureFormat.Rg16F or TextureFormat.Rgba16F => ColorBufferFloat,
        TextureFormat.R32F or TextureFormat.Rg32F or TextureFormat.Rgba32F => ColorBufferFloat && (!blended || FloatBlend),
        _ => true,
    };
}

// GlDevice.Resources.cs — Describe(TextureFormat) gains these arms (block formats carry their compressed internal format and
// are uploaded only through the compressed path; Format/Type are unused for them)
        TextureFormat.Rgba32F => (GLEnum.Rgba32f, GLEnum.Rgba, GLEnum.Float),
        TextureFormat.Rgba16 => (GLEnum.Rgba16, GLEnum.Rgba, GLEnum.UnsignedShort),
        TextureFormat.Rgb10A2 => (GLEnum.Rgb10A2, GLEnum.Rgba, GLEnum.UnsignedInt2101010Rev),
        TextureFormat.Bc1 => ((GLEnum)0x83F0, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGB_S3TC_DXT1_EXT
        TextureFormat.Bc1Alpha => ((GLEnum)0x83F1, GLEnum.Rgba, GLEnum.UnsignedByte),   // COMPRESSED_RGBA_S3TC_DXT1_EXT
        TextureFormat.Bc3 => ((GLEnum)0x83F3, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGBA_S3TC_DXT5_EXT
        TextureFormat.Bc4 => ((GLEnum)0x8DBB, GLEnum.Red, GLEnum.UnsignedByte),         // COMPRESSED_RED_RGTC1
        TextureFormat.Bc5 => ((GLEnum)0x8DBD, GLEnum.RG, GLEnum.UnsignedByte),          // COMPRESSED_RG_RGTC2
        TextureFormat.Bc7 => ((GLEnum)0x8E8C, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGBA_BPTC_UNORM
        TextureFormat.Bc6hSfloat => ((GLEnum)0x8E8E, GLEnum.Rgb, GLEnum.Float),         // COMPRESSED_RGB_BPTC_SIGNED_FLOAT
        TextureFormat.Bc6hUfloat => ((GLEnum)0x8E8F, GLEnum.Rgb, GLEnum.Float),         // COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT

// GlDevice.Resources.cs — Describe(UploadFormat) and BytesPerPixel gain
        UploadFormat.Rgba16 => (GLEnum.Rgba, GLEnum.UnsignedShort),     //  BytesPerPixel: UploadFormat.Rgba16 => 8,

// GlDevice.Resources.cs — CreateTexture refuses a block format (ArgumentException naming the compressed path) and
// CreateRenderTarget refuses what Caps.SupportsRenderTarget refuses, with words (callers fall back: P216/PA16 packing into
// RGBA16F, a Spout RGBA16 upload as RGBA16F); GlDevice gains
//     public GlCaps Caps { get; private set; } = GlCaps.None;
// filled at construction by GlCaps.From(dialect, GL_MAJOR_VERSION, GL_MINOR_VERSION, extensions), the extensions read with
// glGetIntegerv(GL_NUM_EXTENSIONS) and a glGetStringi(GL_EXTENSIONS, i) loop on both dialects (glGetString(GL_EXTENSIONS) is
// an error on a core profile).
```

Tests the spine adds for these: `TextureFormatTests` (`CompressedSize` of 1920×1080 BC1 = 1 036 800 bytes (the bench's
`f0.hap.bc1`), BC3 = 2 073 600 (`f0.hapq.bc3`); every value of `Enum.GetValues<TextureFormat>()` has a `Describe` arm and the
expected `IsBlockCompressed`/`BlockBytes`); `GlCapsTests` (`From` on three fake lists — core 3.3 Mesa: all true; ES 3.0 Mesa with
`EXT_texture_compression_rgtc`, `EXT_texture_compression_bptc`, `EXT_texture_norm16`, `EXT_color_buffer_float`: all true; an
ANGLE-like ES list with `EXT_texture_compression_dxt1` and `ANGLE_texture_compression_dxt5` and no S3TC: DXT1 and DXT5 true,
no Norm16 → `SupportsRenderTarget(Rgba16)` false; on llvmpipe's real core context all five block families true);
`CreateRenderTarget(Rgba32F)` and `(Rgba16)` render and read back a known colour on core 3.3.

---

## 7. The network spine (`src/PlayMap.Net`)

```csharp
// Net/Contracts.cs (spine)
/// <summary>Push topics beyond STATE, CONTEXT and PROJECT that an endpoint serves (INDEX, EVENTS, THUMBS — wp2-companion;
/// ANCHORS — wp2-sync; HEALTH — wp2-fleet; MIRROR — wp2-backup). The wire and WebSocket sessions ask it in SUBSCRIBE's default branch.</summary>
public interface IPushTopics
{
    IReadOnlyCollection<string> Topics { get; }
    /// <summary>Opens a session's subscription: false with words when refused (not paired for a paired-only topic, an unknown
    /// argument). The subscription ends when disposed or when the session closes.</summary>
    bool TryOpen(string topic, IReadOnlyList<string> args, SessionInfo session, IPushSink sink, out IDisposable? subscription, out string words);
}

/// <summary>A session's outgoing pushes: latest-wins per key (INDEX, THUMBS, HEALTH) or in order and bounded (EVENT, ANCHOR, MIRROR;
/// an overflow closes the topic with an "overflow" line).</summary>
public interface IPushSink
{
    void Latest(string key, string line);
    bool InOrder(string line);
}

/// <summary>A part of the node that serves pages and sockets under a path of the web port (the phone's /scan — wp2-phonescan).</summary>
public enum WebAdmission { Defer, Open, Capability, Refuse }
public interface IWebModule
{
    string Name { get; }
    string PathPrefix { get; }
    /// <summary>Open (static, no secret), Capability (the module checks its own credential), Refuse (with words), or Defer (the
    /// node's token gate decides).</summary>
    WebAdmission Admit(Microsoft.AspNetCore.Http.HttpContext context, NodeSettings settings, out string words);
    void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes);
}
```

`EngineCore` implements `IPushTopics` (spine, in `EngineCore.Modules.cs`) over the topic table each package fills from its Route
hook with `routes.Topic(name, open)` (§5.1):

```csharp
/// <summary>What opening a topic gave: a subscription to dispose on OFF/close, or the refusal words.</summary>
public readonly record struct PushOpen(IDisposable? Sub, string? Refused)
{
    public static PushOpen Refuse(string words) => new(null, words);
    public static PushOpen Open(IDisposable sub) => new(sub, null);
}

public sealed partial class EngineCore : IPushTopics
{
    IReadOnlyCollection<string> IPushTopics.Topics => [.. _routes.Topics];

    bool IPushTopics.TryOpen(string topic, IReadOnlyList<string> args, SessionInfo session, IPushSink sink,
        out IDisposable? subscription, out string words)
    {
        subscription = null;
        if (!_routes.TryTopic(topic, out var open))
        {
            words = $"{topic} is not a topic (STATE, CONTEXT, PROJECT, OFF, {string.Join(", ", _routes.Topics)})";
            return false;
        }
        var opened = open(args, session, sink);
        subscription = opened.Sub;
        words = opened.Refused ?? "";
        return opened.Refused is null;
    }
}
```
`WireSession.Subscribe` and the WebSocket session's subscribe call `Endpoint as IPushTopics` for any topic but STATE, CONTEXT,
PROJECT and OFF (a two-line spine hunk each; wp2-companion then owns both files for the WebSocket line channel). `HttpHost`
mounts `HttpHostOptions.Modules` (spine hunk; admission before the token gate).

---
## 8. The node's settings (`src/PlayMap.Core/Bus/Pairing.cs` hunk: `NodeSettings` becomes `partial`; one file per package)

Machine-local by design (a show travels; who counts as an operator, which port a controller is on, whether this machine sends
LED data do not). Each package's file holds one init property and its record; old `playmap.node.json` files read the defaults.

```csharp
// NodeSettings.Show.cs (wp2-show)
public sealed partial record NodeSettings { public ShowSafetySettings Show { get; init; } = ShowSafetySettings.Default; }
public sealed record ShowSafetySettings
{
    public static readonly ShowSafetySettings Default = new();
    /// <summary>HELLO names or addresses whose remote sessions count as Operator; [] = only the desk.</summary>
    public ImmutableArray<string> Operators { get; init; } = [];
}

// NodeSettings.Fleet.cs (wp2-fleet; the backup fields filled by wp2-backup)
public sealed partial record NodeSettings { public FleetSettings Fleet { get; init; } = FleetSettings.Default; }
public sealed record FleetSettings
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
}

// NodeSettings.Sync.cs (wp2-sync) — the persisted sync role (the carry-over: lead/follow and follow-blackout survive a restart)
public sealed partial record NodeSettings { public SyncSettings Sync { get; init; } = SyncSettings.Default; }
public sealed record SyncSettings
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
}

// NodeSettings.Output.cs (wp2-output)
public sealed partial record NodeSettings { public PresentSettings Present { get; init; } = PresentSettings.Default; }
public sealed record PresentSettings
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
}

// NodeSettings.Decode.cs (wp2-decode)
public sealed partial record NodeSettings { public DecodeSettings Decode { get; init; } = DecodeSettings.Default; }
public sealed record DecodeSettings
{
    public static readonly DecodeSettings Default = new();
    /// <summary>gpu | ffmpeg (today's CPU path for Hap1/5/Y/A/M only).</summary>
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
}

// NodeSettings.Optimise.cs (wp2-optimise)
public sealed partial record NodeSettings { public OptimiseNodeSettings Optimise { get; init; } = OptimiseNodeSettings.Default; }
public sealed record OptimiseNodeSettings
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
}

// NodeSettings.VideoIo.cs (wp2-videoio)
public sealed partial record NodeSettings { public VideoIoSettings VideoIo { get; init; } = VideoIoSettings.Default; }
public sealed record VideoIoSettings
{
    public static readonly VideoIoSettings Default = new();
    public bool NdiEnabled { get; init; } = true;
    /// <summary>"" = the runtime's own search (NDI_RUNTIME_DIR_V6, the system paths); never shipped with PlayMap.</summary>
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
}

// NodeSettings.Control.cs (wp2-control)
public sealed partial record NodeSettings { public ControlSettings Control { get; init; } = ControlSettings.Default; }
public sealed record ControlSettings
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
}

// NodeSettings.Companion.cs (wp2-companion)
public sealed partial record NodeSettings { public CompanionSettings Companion { get; init; } = CompanionSettings.Default; }
public sealed record CompanionSettings
{
    public static readonly CompanionSettings Default = new();
    /// <summary>Cached posters always; live output thumbnails at most 0.5 fps while armed.</summary>
    public bool ThumbsWhileArmed { get; init; } = true;
    public int MaxThumbsPerSession { get; init; } = 128;
}

// NodeSettings.Fx.cs (wp2-fx)
public sealed partial record NodeSettings { public FxSettings Fx { get; init; } = FxSettings.Default; }
public sealed record FxSettings
{
    public static readonly FxSettings Default = new();
    public ImmutableArray<string> IsfFolders { get; init; } = [];
    public ImmutableArray<string> FfglFolders { get; init; } = [];
    /// <summary>FFGL plugins load only when this is on and the plugin is trusted (FX TRUST, this machine only).</summary>
    public bool FfglEnabled { get; init; }
    /// <summary>Plugin kind → "in-process" | "off" (unlisted = off).</summary>
    public ImmutableSortedDictionary<string, string> FfglTrust { get; init; } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);
    public double BudgetMs { get; init; } = 4.0;
}

// NodeSettings.Audio.cs (wp2-audio)
public sealed partial record NodeSettings { public AudioNodeSettings Audio { get; init; } = AudioNodeSettings.Default; }
public sealed record AudioNodeSettings
{
    public static readonly AudioNodeSettings Default = new();
    /// <summary>This machine's input override ("" = the project's).</summary>
    public string Input { get; init; } = "";
    public string Channels { get; init; } = "";
    /// <summary>auto | ffmpeg | wasapi | none.</summary>
    public string Backend { get; init; } = "auto";
    /// <summary>Measured or entered input latency, ms; −1 = the backend's report.</summary>
    public double InputLatencyMs { get; init; } = -1;
}

// NodeSettings.Content.cs (wp2-content)
public sealed partial record NodeSettings { public ContentSettings Content { get; init; } = ContentSettings.Default; }
public sealed record ContentSettings
{
    public static readonly ContentSettings Default = new();
    /// <summary>GPU ms per frame at 60 Hz before the governor advises (scaled by refresh).</summary>
    public double BudgetMs { get; init; } = 6.0;
    /// <summary>off | advise.</summary>
    public string Governor { get; init; } = "advise";
    public int MaxParticles { get; init; } = 4_194_304;
}

// NodeSettings.Led.cs (wp2-led)
public sealed partial record NodeSettings { public LedSettings Led { get; init; } = LedSettings.Default; }
public sealed record LedSettings
{
    public static readonly LedSettings Default = new();
    /// <summary>The machine master: this machine transmits LED data (off on a fresh node: fail closed).</summary>
    public bool Output { get; init; }
    public int MulticastTtl { get; init; } = 1;
    /// <summary>The bandwidth guard.</summary>
    public int MaxMbps { get; init; } = 200;
}

// NodeSettings.Align.cs (wp2-align)
public sealed partial record NodeSettings { public AlignSettings Align { get; init; } = AlignSettings.Default; }
public sealed record AlignSettings
{
    public static readonly AlignSettings Default = new();
    public int RunsKept { get; init; } = 20;
}

// NodeSettings.Phone.cs (wp2-phonescan)
public sealed partial record NodeSettings { public PhoneSettings Phone { get; init; } = PhoneSettings.Default; }
public sealed record PhoneSettings
{
    public static readonly PhoneSettings Default = new();
    /// <summary>Serve /scan and accept invites.</summary>
    public bool Enabled { get; init; } = true;
    public int InviteMinutes { get; init; } = 10;
    public int MaxConnected { get; init; } = 4;
}

// NodeSettings.Wall.cs (wp2-wall)
public sealed partial record NodeSettings { public WallSettings Wall { get; init; } = WallSettings.Default; }
public sealed record WallSettings
{
    public static readonly WallSettings Default = new();
    public int CursorLeaseMs { get; init; } = 3000;
    public int OverlayLeaseMs { get; init; } = 15000;
    public int MaxCursors { get; init; } = 8;
    public int CalibrationTaskTimeoutS { get; init; } = 120;
}
```

`ShowBusConstants` gains `PlayMapOscPort = 9943` (UDP) and `PlayMapOscQueryPort = 9944` (TCP), clear of Patterns (9696–9701)
and Stagewatch (9923–9928). `PlayMapSyncPort = 9942` is today's clock-sync port; the PMVB vblank reports ride it, demuxed by
their magic, in wp2-sync's `ClockSync.cs`. Secrets stay in the credential store, never here (R7).

---

## 9. STATE: the order and the shapes

Every key below is added at the **end** of its object, in this order; nothing existing moves. A package writes only its own keys
(R8) and ships the fixture `tests/fixtures/state/<pkg>.json` (R9). `null` means "this node has none"; unknown numbers are `-1`
and grey, never `0`.

**New top-level keys, after `"cameras"`:** `show`, `fleet`, `eye` (wp2-show, wp2-fleet) · `decode` (wp2-decode) · `videoio`
(wp2-videoio) · `control`, `timecode` (wp2-control) · `clients`, `events`, `selection` (wp2-companion) · `fx` (wp2-fx) · `audio`,
`modulation` (wp2-audio) · `content` (wp2-content) · `led` (wp2-led) · `calibration`, `onboarding` (wp2-wall) · `align`
(wp2-align) · `phones` (wp2-phonescan).

**Keys appended inside existing objects:** `air` → `master`, `panic` (show), `overlay` (wall) · `outputs[]` → `latencyMs`,
`present`, `lock` (output), `fx` (fx), `measured` (align) · `playlists[]` → `chase` (control) · `sync` → `group`, `feed`,
`slewing`, `steps`, `freewheelS`, `outputs`, `pairs`, `verdict`, `words` (sync), `latencyMs`, `lock` (output), `epoch` (backup) ·
`media` → `optimise` (optimise) · `stage` → `site` (with `next`, the guide's step), `controls`, `surveys`, `stacks` (facade) · `stage.projectors[]` →
`uncertainty`, `priors`, `stack` (facade), `grade`, `quads` (wall) · `machine` → `present` (output), `gl` (decode) · `project`
→ `index` (companion).

```jsonc
"air": { …, "master":{"level":1.0,"to":1.0,"untilLeaderNs":0},
            "panic":null|{"outcome":"black","hard":false,"by":"Companion: FOH deck","since":1760101234567,
                          "outputs":[{"id":"op_1","outcome":"safe","level":0.42,"settled":false}]},
            "overlay":{"on":true,"outputs":["op_1"],"layers":["cursor","outlines","names","handles","lattice"],
                       "handle":{"surface":"sf…","kind":"corner","index":2}|null,"grid":false,"cursors":2,"cursorP95Ms":24.1} },
"outputs": [{ …, "latencyMs":33.0,
              "present":{"path":"gl-window|dxgi-flip|offscreen","mode":"gl|iflip|composed|offscreen","evidence":"none|hint|proven",
                         "refreshHz":59.999823,"nominalHz":60,"ppm":-2.95,"settled":true,"missedVblanks":0,"missed60s":0,
                         "held":0,"delayFrames":0,"latencyResidualMs":0.0,"statsAgeMs":16.7,"words":"59.999823 Hz · 0 missed"},
              "lock":{"state":"free-measured|framelocked|joining|fell-out|free-unknown","words":""},
              "fx":{"slots":2,"costMs":1.4,"light":"green"},
              "measured":null|{"run":"ar…","residualPx":0.21,"colour":true,"blend":true,"files":"ok"} }],
"playlists": [{ …, "chase":null|{"state":"locked|freewheel|held|waiting|outside","source":"tc…","text":"01:02:03:04","engaged":true} }],
"sync": { …, "group":"show","feed":"live","slewing":false,"steps":1,"freewheelS":0,
          "outputs":[{"node":"9f8e7d6c","output":"op_1","state":"framelocked","refreshHz":59.999823,"ppm":-2.95,"phaseUs":0,
                      "uncertaintyUs":210,"nextSlipS":null,"missed60s":0,"heard":true}],
          "pairs":[{"a":"op_1","b":"op_7","phaseUs":12,"uncertaintyUs":210,"nextSlipS":null,"mixedRate":false}],
          "verdict":"same vblank|free-running|mixed rate|unknown","words":"3 nodes, 7 outputs on one vblank",
          "latencyMs":33.0,"lock":{"state":"framelocked","driver":"nvapi|fake|none","board":"Quadro Sync II (P2061)|sync board 0x…","words":"…"},
          "epoch":7 },
"media": { …, "optimise":{"mode":"ask","governor":"run|trickle|paused|waiting","words":"…","running":1,"queued":3,"failed":0,
           "suggested":4,"etaS":412,"worker":{"gl":"…","restarts":0,"gpuDuty":0.14},
           "jobs":[{"id":"oj…","media":"md…","name":"Facade loop","to":"hap-q","state":"encoding","framesDone":5400,"frames":18000,"fps":51.2,"etaS":246,"words":""}]} },
"stage": { …, "site":{"crs":"EPSG:25832","name":"ETRS89 / UTM zone 32N","origin":[512345.123,5401234.567,112.4],"rotationDeg":0,"gridScale":0.99961,"mainsHz":50,
                   "next":{"step":"controls","state":"waiting","words":"Import the surveyed control points","line":"SITE POINTS <path>"}}|null,
           "controls":14,"surveys":[{"id":"sv…","name":"North facade","format":"las","state":"ready","points":10000000,"triangles":0,"mesh":"ms…"}],
           "stacks":[{"id":"sk…","name":"S1","members":["pj1","pj2"],"master":"pj1","residualPx":0.21,"failover":"match","degraded":[],"gain":1.0}],
           "projectors":[{ …, "uncertainty":{"wallPx":0.6,"axisM":0.4,"focalPct":0.8,"weak":""}, "priors":"datasheet ±2 %", "stack":"sk…"|null,
                           "grade":"A", "quads":2 }] },
"machine": { …, "present":{"api":"gl|d3d11+angle","adapter":"llvmpipe","path":"gl","experimental":false,"why":"","resets":0,"presentMsP95":0.21}, "gl":"egl|glx|wgl|angle" },
"project": { …, "index":"9f1c2ab07d3e4410" },

"show": {"armed":true,"by":"Desk","since":1760101234567,"rules":{"minGoMs":300,"remoteViewOnly":false,"disarmFrom":"paired","expectArmed":false},
         "blind":null|{"owner":"Desk","since":…,"changes":7,"commit":null|{"boundary":"next","playlist":"pl…","effectiveLeaderNs":…}},
         "panic":null|{…as air.panic},"locks":[{"target":"mapping","kind":"area","by":"Desk","at":…,"words":"","implicit":true}],
         "refused":{"last60s":3,"last":"Companion: FOH deck — WARP NODE … — mapping is locked"},
         "preflight":null|{"id":"pf…","at":…,"depth":"quick","light":"amber","green":41,"amber":2,"red":0,"grey":1,"ready":"4/5","running":false},
         "minGoMs":300},
"fleet": {"role":"solo|primary|backup|fenced","epoch":7,"state":"serving|mirroring|identical|alarm|fenced","of":null|"0badc0de",
          "backups":[{"node":"9f8e7d6c","name":"FOH-B","state":"identical","light":"green","words":"…"}],
          "mirror":null|{"of":"0badc0de","state":"identical|different|joining|resyncing","behindRev":0,"words":"…"},
          "succession":["9f8e7d6c"],"alarm":null|{"kind":"silence","since":…,"words":"FOH-A silent 2.0 s — FAILOVER to FOH-B?"},
          "last":null|{"kind":"takeover","id":"tk…","at":…,"words":"…complete in 1.4 s"},
          "nodes":[{"id":"…","name":"…","role":"…","light":"green","headline":"…","heard":"live|stale|gone","build":"…","rev":123}],
          "light":"green","headline":"4 machines · 18 outputs green · FOH-B identical"},
"eye": {"headline":"…","worst":"output:9f8e7d6c:op_3","worstLight":"amber","worstWords":"P3: shutter closed",
        "counts":{"green":30,"amber":1,"red":0,"grey":2,"things":33,"links":41},"problems":["…"],"focus":"","lens":"all"},
"decode": {"policy":"auto","platform":{"api":"dmabuf","describe":"…","gl":"egl","zeroCopy":"ready|unavailable","direct":"proven|unproven|n/a",
             "blocks":{"bc1":true,"bc3":true,"bc4":true,"bc7":true,"bc6h":true,"staging":"persistent|copy|none","arenaMb":256,"arenaFreeMb":140},
             "hapLayers4k60":{"value":9,"limit":"cores|disk|upload","measuredAt":1760101234567},"words":""},
           "sources":[{"key":"md…","media":"md…","name":"Intro","codec":"hevc|hap_q|…","device":"vaapi","path":"hw-direct|hw-gpu-copy|hap-gpu|hw-copy-back|software",
                       "zeroCopy":true,"decodeMsP95":0.6,"importMsP95":0.02,"busMbPerFrame":0,"dropped":0,"late":0,"why":"",
                       "hap":null|{"texture":"ycocg-bc3","chunks":16,"secondStage":"snappy","stageMsP95":2.1,"uploadMb":8.29,"staging":"persistent","corrupt":0,"lastRefusal":"","hdr":null}}],
           "probes":[{"id":"dp…","media":"md…","state":"done","path":"hw-direct","words":""}]},
"videoio": {"ndi":{"state":"ready|absent|failed|off","version":"…","words":"","link":"https://ndi.video"},
            "omt":{"state":"ready|off|failed","version":"…","vmx":"avx2|sse42|missing","mdns":"none|avahi","words":""},
            "spout":{"state":"ready|unavailable","version":"2.007","senders":3,"words":"Spout is Windows only"},
            "discoveredCount":7,"discovered":[{"uri":"ndi:STUDIO (Cam 1)","proto":"ndi","name":"Cam 1","host":"STUDIO","added":"md…"|""}],
            "inputs":[{"id":"md…","name":"Cam 1","uri":"…","proto":"ndi|omt|spout","state":"connecting|ok|stale|lost|absent|failed","words":"",
                       "w":1920,"h":1080,"fps":59.94,"ageMs":11,"received":53110,"dropped":0,"repeated":3,"using":"primary|backup|fallback",
                       "tally":"program|preview|none","onAir":true,"onLoss":"hold"}],
            "sends":[{"id":"sd…","name":"Main out","proto":"ndi|omt|spout","fullName":"SHOWPC (Main out)","tap":"output:op_1","w":1920,"h":1080,
                      "fps":60,"format":"uyvy","state":"sending|idle|off|starting|failed","words":"","connections":2,"tally":{"program":true,"preview":false},
                      "sent":120330,"dropped":4}],
            "bandwidth":{"nic":"eth0","linkMbps":1000,"inMbps":131.2,"outMbps":364.0,"limitMbps":700,"light":"green"}},
"control": {"enabled":true,"osc":{"port":9943,"open":true,"senders":1,"subscribers":1,"refused":0,"malformed":0},
            "midi":{"backend":"alsa-seq","open":false,"words":"no ALSA sequencer: /dev/snd/seq is missing","inputs":[],"outputs":[]},
            "dmx":{"artnet":{"enabled":false},"sacn":{"enabled":false}},
            "surfaces":[{"id":"ct…","name":"APC40","kind":"midi","connected":true,"port":"APC40 mkII","profile":"","bindings":12,"light":"green"}],
            "bindings":{"count":24,"enabled":22,"waiting":1,"unplugged":2},
            "learning":null|{"target":"SET sf… opacity *","words":"Lobby opacity","until":1760101264567,"from":"MIDI"},
            "last":null|{"input":"MIDI APC40 CC 1 48","value":0.4173,"binding":"bd…","at":1760101234567}},
"timecode": {"sources":[{"id":"tc…","name":"FOH LTC","kind":"ltc","state":"rolling|parked|acquiring|dropout|silent|unavailable","text":"01:02:03:04",
                         "format":"25","detected":"25","speedPpm":16,"jitterUs":4.1,"levelDb":-12.3,"errors":{"biphase":0,"parity":0,"discontinuity":1},"words":"…"}],
             "chases":[{"playlist":"pl…","sources":["tc…"],"active":"tc…","engaged":true,"state":"locked|freewheel|held|stopped|waiting|outside",
                        "text":"01:02:03:04","item":"it…","delayMs":40,"freewheelLeftMs":null,"words":"…"}]},
"clients": [{"session":"w12","name":"Companion:FOH deck","kind":"companion","transport":"companion","channel":"ws|tcp","address":"10.0.0.7",
             "paired":true,"module":"1.0.0","since":1760101234567,"recording":false,"where":"Live · projectors 1/3","thumbs":12}],
"events": {"seq":412},
"selection": null|{"by":"Desk","ids":["sf…"],"kind":"surface"},
"fx": {"budgetMs":4.0,"library":{"builtin":12,"isf":4,"ffgl":2,"quarantined":1},
       "slots":[{"id":"fx…","owner":"ly_base","ownerKind":"layer","index":0,"kind":"fx.blur","name":"Blur","state":"ok|compiling|faulted|missing|bypassed",
                 "gpuMs":0.31,"deterministic":true,"live":{"radius":12.5},"words":""}],
       "plugins":{"enabled":false,"inProcess":0,"words":""}},
"audio": {"level":0.42,"bpm":128.0,"beat":3,"bar":17,"phrase":3,"phase":0.42,"bass":0.61,"mid":0.30,"high":0.18,"peakDb":-6.1,"silent":false,
          "input":{"key":"…","name":"Line In","channels":"1+2","rateHz":48000,"state":"listening|retrying|off","words":"","backend":"ffmpeg"},
          "tempo":{"source":"manual|detect","lock":"manual|searching|locked|holding|lost","confidence":0.86,"bpm":128.0,"beatsPerBar":4,"barsPerPhrase":8,
                   "epochUnixMs":1760101234567,"anchor":{"leaderNs":0,"beat":0,"bpm":128.0,"seq":12}},
          "latencyMs":{"input":10.0,"analysis":13.3,"total":23.3,"acoustic":0}},
"modulation": {"bindings":12,"bypassed":false,"live":1,"shared":0,"pure":11,"unbound":["audio:bass (no audio on this node)"],"flashLimited":0},
"content": {"budgetMs":6.0,"light":"green","sting":null|{"preset":"Explosion","atUnixMs":…,"lengthMs":900,"progress":0.42},
            "generators":[{"id":"gn…","kind":"particles.flow","name":"Snow","state":"ok|warming|faulted|idle","time":"pure|bounded|live",
                           "gpuMs":0.9,"gpuMsP95":1.1,"count":524288,"morph":null|{"to":"Blizzard","progress":0.3},"live":{"rate":0.82},"onAir":true,"words":""}]},
"led": {"output":true,"level":1.0,"fixtures":12,"pixels":4320,"universes":26,"mbps":38.4,"test":null,"locate":null,"lagMs":0.4,
        "nodes":[{"id":"ln…","name":"PixLite A","protocol":"artnet","host":"10.0.0.21","state":"off|disabled|idle|sending|late|unreachable|refused",
                  "fps":44.0,"universes":8,"lateMs":0.2,"seen":{"name":"PixLite16","refreshHz":44,"ageS":1.2}|null,"why":""}],
        "conflicts":[{"node":"ln…","universe":3,"from":1,"to":48,"fixtures":["lf…","lf…"]}],"warnings":[]},
"calibration": {"moved":[],"task":null|{"id":"kt…","kind":"resolve","state":"capturing|solving|verifying|done|failed|cancelled","projector":"pj…","progress":0.4,"words":"…"},
                "targets":[{"id":"pj…","kind":"projector|output","name":"Projector 2","score":0.6,"unit":"px","grade":"A","words":"A 0.6 — …",
                            "worst":"","confidence":0.94,"snapshot":"cs…","fallback":"cs…","staged":null,"history":7,"verifiedP95":0.31,
                            "terms":[{"code":"rms","words":"…","value":0.4,"unit":"px","penalty":0.4,"light":"green","fix":""}]}]},
"onboarding": {"stage":"none|lit|quad|content","startedAt":…,"litAt":…,"quadAt":…,"contentAt":…,"seconds":84.2},
"align": {"run":null|{"id":"ar…","group":"al…","state":"running|staged|failed|cancelled","step":"scan","progress":0.5,"etaS":40,"words":"…",
                      "outputs":[{"id":"op_1","step":"scan","progress":0.5,"light":"green","words":""}],"score":null|{"value":91,"unit":"points","grade":"A"}},
          "groups":[{"id":"al…","name":"Main screen","shape":"flat","outputs":["op_1","op_2"],"canvas":"cv_main","lastRun":"ar…","grade":"A","light":"green","words":"…"}],
          "devices":[{"id":"dv…","snapshot":true,"neutral":true,"internal":[],"words":""}]},
"phones": [{"id":"ph…","name":"Pixel 8","camera":"cm…","platform":"android-chrome","connected":true,"address":"192.168.1.44","transport":"ws|http",
            "link":{"rttMs":9.8,"uncertaintyMs":4.9,"uploadMBps":6.1},
            "camera":{"label":"…","width":1920,"height":1080,"fps":30,"exposure":"locked|steady","focus":"locked","whiteBalance":"locked"},
            "motion":"still","words":"Ready: exposure, focus and white balance locked"}]
```

Pushes: STATE's `seq` moves when any of these change; numbers that move every frame (levels, ages, rates) are quantised to
100–250 ms so a push happens at most four times a second (latest wins), and `control.last` moves STATE at most five times a
second (the desk polls `CONTROL MONITOR` while it is open).

---

## 10. The context, the Show Bus and the Eye

### 10.1 Context items by package (kinds and ids)

| Owner | Item id | Kind | Light rule (short) |
|---|---|---|---|
| show | `show` (always present, first) | `show` | red panic; amber blind draft or "rehearsal while playing" (ExpectArmed); green armed; grey rehearsal |
| show | `preflight` | `preflight` | the last report's worst light; words "41 green · 2 amber · show-ready 4/5 · 3 min ago" |
| fleet | `machine` (always present, second) | `machine` | worst of this node's HEALTH; words role, build, render path |
| backup | `mirror:<node>` | `mirror` | green identical, amber joining/resyncing, red different/silent |
| sync | `sync:<group>` (each node publishes its own; the leader's carries the verdict) | `sync` | red fell out; amber mixed rate, joining > 5 s, slip < 10 min while playing; green same vblank |
| output | (words of `output:<id>` from today's item) | `output` | the present words ("59.999823 Hz · independent flip · 0 missed"); red not open / composed proven / stats stale; amber missed vblanks |
| decode | `decode` | `decode` | red a source failed or HAP R/HDR on a node without BPTC; amber ≥ 4K on copy-back/software or the copy path; green otherwise |
| optimise | `optimise` | `optimise` | red a job of an item in the show failed; amber a show item decodes on the CPU at ≥ 4K and is suggested |
| videoio | `videoio`, `input:<id>` (on air or not ok), `send:<id>` | `videoio`, `input`, `send` | red runtime absent while the show uses it / input lost on air; amber stale, on fallback, > 1 % drops |
| control | `control:<ct>`, `control:learning`, `control:off`, `timecode:<tc>`, `chase:<pl>` | `control`, `timecode`, `chase` | red port refused while bound / chase held; amber bound but unplugged, learning, freewheel; green connected, locked |
| companion | `client:<session>` | `deck` | green paired and live; amber connected not paired |
| fx | `fx` | `fx` | red an output's chain at > 2 × budget; amber a slot faulted or over budget |
| audio | `audio`, `tempo` | `audio`, `tempo` | red missing while bindings depend on it; amber silent > 10 s, retrying, searching; green listening/locked |
| content | `content` | `content` | red 2 × budget; amber a generator faulted or over budget |
| led | `led:<node>` | `led` | red unreachable or refused while the master is on; amber late, test or locate up; green sending; grey off |
| facade | `site`, `survey:<id>`, `stack:<id>` | `site`, `survey`, `stack` | red stack degraded; amber importing, control check amber/red, residual > 0.5 px |
| align | `align:<group>` | `align` | red last run failed with nothing accepted, or a measured file missing; amber Fair/Poor score; device double correction amber on `device:<id>` |
| phonescan | `phone:<id>`, `suggestions` | `phone`, `scan` | amber moved during a view / link slow / suggestions waiting; grey not connected |
| wall | `calibration:<target>`, `overlay` | `calibration`, `overlay` | red F or moved; amber C, staged; green A/B; overlay amber whenever it is up (the desk's mode frame) |

### 10.2 Links, order and the 12-thing window (wp2-fleet's `ShapeContext`)

- **Links use only the Eye's eleven verbs** (`ContextLinkKinds`): `playlist:<id>` **feeds** `output:<id>`; `output:<id>` **drives**
  `projector:<id>`; `show` **controls** `output:<id>` (replacing today's `link:patterns → air`); `link:patterns` **controls** `show`;
  `link:stagewatch` **feeds** `show`; `machine` **follows** `sync:<group>` (follower; leader: `sync:<group>` **follows** `machine`);
  a free-running output `output:<id>` **hears** `sync:<group>`; `mirror:<node>` **mirrors** `output:<id>`; `input:<id>` **feeds**
  `surface:<sf>`/`output:<op>`; `output:<op>` **sends** `send:<id>`; `control:<ct>` **controls** what its bindings target;
  `led:<node>` **shows** nothing new (an LED node is a display: `canvas`/`output` **feeds** `led:<node>`). A spine test rejects any
  other kind.
- **Order for the Eye's window** (Patterns draws 12: reds, then ambers, then the given order): `show`, `machine`, then outputs (≤ 6
  individually; above 6 one `outputs` aggregate plus every non-green output), projectors (device facts folded into the projector's
  words: one item per projector), playing playlists (idle ones folded into `playlists`), `mirror:*`, `sync:*`, `media`, `preflight`,
  `link:*`, then everything else, `fault:*` last. Every item stays in the context (an assistant reads up to 200); the order only
  decides what the Eye draws.
- **Commands**: at most 64, `Internal` rows left out, Panic/Air/Transport/Show classes first (`CommandsFromCatalog`).
- **Headline**: facts, never a model — `ARMED · Show · playing Intro 2:10 left · 6 outputs green · backup FOH-B hot, same frame`.
- **Peers**: one hop — the other PlayMap nodes this node hears, with their own light and headline from their HEALTH.

### 10.3 The Show Bus and Patterns' Eye

- **Hello caps**: today's 14 plus **`eye`** (PlayMap answers `EYE`/`EYE STATUS` in Patterns' `EyeJson` shape) and **`show`**
  (Show Mode facts in STATE and the context): 16 of the 16 a strict reader keeps. Nothing else is added (LED, video I/O, tempo,
  control are read from the context and STATE). The hello gains two optional fields (fleet): `"role":"solo|leader|follower|
  primary|backup|fenced"` and `"epoch":<long>`; it stays ≤ 2 048 bytes.
- **`EYE`/`FLEET` JSON** (`FleetGraph`, wp2-fleet), shape-compatible with Patterns' `EyeJson`: `{"headline","worst","worstLight",
  "worstWords","counts":{"green","amber","red","grey","things","links"},"focus","lens","problems":[…],"nodes":[{"id","kind","plane",
  "tier","label","sub","light","x","y","w","h","words","wire":"EYE FOCUS <id>"}],"edges":[{"from","to","kind","light","words"}],
  "machines":[{"id","name","role","light","headline","heard","outputs":[…],"projectors":[…]}]}`; lenses `ALL|OUTPUTS|PROJECTORS|SYNC|
  BACKUP|PROBLEMS`; unknown = grey; a node not heard for 6 s is red "gone quiet" and never removed silently.
- **`HEALTH`** (`HealthRecord`, wp2-fleet): `{"v":1,"node","name","at","role","epoch","build","light","headline","outputs":[{"id","light",
  "fps","dropped","missed60s","path"}],"render":{"renderer","path","resets"},"cpuPct","memMb","memFreeMb","diskFreeGb","gpuPct":-1,
  "gpuTempC":-1,"netMbps","sync":{"role","uncertaintyMs","freewheelS"},"devices":{"total","green","amber","red"},"media":{"items","missing"},
  "watchdog":{"laneMs","renderMs"}}`; read open (no token), pushed by `SUBSCRIBE HEALTH` at 1 Hz and on any light change; TLS pinned
  when the hello offers it.
- **Anchors to followers** (wp2-sync; the carry-over): the leader serves `SUBSCRIBE ANCHORS`; it pushes
  `ANCHOR <group> <playlist> <json>` (the existing `AnchorJson`) in order on every transport change and once per subscription; a
  follower adopts each through its own `PLAYLIST ANCHOR` (session `sync`, Peer role). **The group's air** rides the same feed:
  `ANCHOR <group> @air {"line":"PANIC FADE 1000","leaderNs":…}` for PANIC, PANIC CLEAR, BLACKOUT and MASTER from the leader (and
  SHOW ARM/DISARM ALL, PREFLIGHT ALL with receipts through `IFleetFanout`), run by each follower and backup at that instant through
  its own gate. The group's air latency rides as `ANCHOR <group> @latency {"ms":33.0}` (stretch).
- **The mirror feed** (wp2-backup, `SUBSCRIBE MIRROR`, paired fleet sessions only; this round `MIRROR PROJECT`, `MIRROR AIR` and
  `MIRROR ANCHOR`, the project re-pulled with proof when the revision moves; deltas next round): `MIRROR PROJECT <json>` once, then `MIRROR DELTA
  <ProjectDiff json>` per revision, `MIRROR AIR <json with leader instants>`, `MIRROR ANCHOR …`, `MIRROR FOCUS …`, `MIRROR BEAT
  {"rev","leaderNs"}` at 4 Hz; a gap in revisions asks `MIRROR RESYNC`. The backup refuses local edits: "FOH-B mirrors FOH-A — send it
  to FOH-A (10.0.0.21:9941) or FAILOVER".
- **Vblank reports** (wp2-sync, fed by wp2-output's `VblankClock`) on UDP 9942 — today's clock-sync socket, owned by wp2-sync —
  beside the clock exchange, demuxed by the 4-byte magic `PMVB`: 64 bytes,
  little-endian — magic, version 1, `LockState` (byte), flags (byte), path (byte), node id hash (u32), output id hash (u32), seq (u32),
  board frame count (u32, 24 bits used), period (ps, i64), last vblank (leader ns, i64), refresh index (u64), missed in 60 s (u32),
  house sync (milli-Hz, u32), air latency (µs, i32), reserved to 64. Fail closed: any other length, version or a field out of range
  is dropped and counted; only nodes in the group are heard.
- **Patterns-side proposals** (taken or not by Patterns' maintainers; recorded in `docs/SHOWBUS-NODE.md`): P1 `KindWords
  ("playmap") => "PlayMap"`; P2 read every live peer's context with the `context` cap (not only the linked Stagewatch); P3 output,
  projector, surface, playlist and media items on the Video plane. wp2-fleet prepares them as a patch with tests against Patterns'
  `ShowBusView`/`EyeGraph` under `scratchpad/ext/patterns-eye-p2/` (never in `patterns-svc`), and PlayMap's
  `PatternsEyeContractTests` feed its context through a copy of Patterns' `ShowContext.Parse` and `EyeGraph.Build` (as data, with
  the commit). Without P2 PlayMap is a grey ShowApp with word lines; with it its things, lights and problems appear in Patterns' Eye.

---

## 11. What the spine lands, and how it is accepted

The spine is two sessions, each with its own review-and-fix pass; W0b starts from W0a merged, and W1 starts only on a green W0b.

### 11.1 W0a — model, Core types, catalogue, risk, syntax (§0–§4)

**Files it creates** (owner after the spine in brackets): `Core/Model/{ShowModel [show], FleetModel [fleet], ControlModel,
TimecodeModel [control], VideoIoModel [videoio], ModulationModel, AudioModel [audio], FxModel [fx], ContentModel [content],
LedModel [led], OptimiseModel [optimise], SiteModel [facade], AlignModel [align], CalibrationModel, PixelQuad, Overlay [wall],
PhoneModel [phonescan]}.cs`; `Core/Media/{GpuSurfaces, UploadArena}.cs` [decode]; `Core/Media/LiveVideo.cs` [videoio];
`Core/Wire/{ShowClass, VerbSyntax}.cs` [spine]; `Core/Show/{ShowTypes, AirTypes}.cs` [show]; `Core/Modulation/ModulationSeams.cs`,
`Core/Audio/AudioInputs.cs` [audio]; `Core/Preflight/PreflightModel.cs` [show]; `Core/Dmx/IArtNetEndpoint.cs` [control].
**Files it changes** (hunks): `Ids.cs`, `Project.cs`, `Content.cs`, `MediaModel.cs`, `OutputModel.cs`, `PlaylistModel.cs`,
`StageModel.cs`, `DeviceModel.cs`, `ProjectJson.cs`, `ProjectStore.cs`, `VideoFrames.cs`, `MediaFrameLease.cs`, `VerbCatalog.cs`,
`ShowContext.cs`, `ShowBus.cs`, `Assistant/CommandGate.cs` (alternatives through `VerbSyntax`).

**Tests it adds** (all in this container): `ModelCompatTests` (main's sample project and a round-1 file load unchanged; a file with
every new key `null` reads the defaults; every new entity kind round-trips through `ProjectJson`, `GET`, `SET` and `ADD`);
`CatalogueTests` (every row: ≤ 2 words, syntax parses into words HELP prints, risk set, `SecretFrom` null on every new row, `Show`
class set, aliases unique, `TC` alias, `Round` = 2 on every §4.3 row; **for every row and every alternative from
`VerbSyntax.Alternatives`, a line built from that form passes `CommandGate.Check`**; **no `RiskWhen` on a row of class Air,
Transport, Panic or DevicePower lowers its risk, and every new Air or Panic row is High**); `RiskOfTests` (BLACKOUT ON high, FREEZE
ON high, DEVICE SHUTTER dv CLOSE high, MASTER 0 high, PANIC high, LED LEVEL 0 high, BIND ADD … TO BLACKOUT OFF high, DECODE POLICY
DIRECT FORCE high, MEDIA OPTIMISE CANCEL low, BIND LEARN OFF low, TIMECODE SOURCE RESET tc low, SEND ENABLE sd ON review, an unknown
verb high); `VerbSyntaxTests` (GENERATOR SET, UNLOCK, MEDIA RULE, VIDEOIO SET, BIND ADD, FX SET, ALIGN CORNERS, HANDLE SELECT,
SCAN SURFACE and LINK PATTERNS split into their forms, each re-prefixed); `ContextLinkTests` (Eye verbs only); `LiveContractTests`
(`LiveUri` table). **Every existing test passes unchanged** — among them `OpsHostTests`, `HostLifecycleTests`,
`StagewatchSinkTests`, `AssistantEngineTests` and `GateTests`, which hold BLACKOUT ON at high risk and queued.

### 11.2 W0b — engine, render, network, settings, the tree (§5–§10)

**Files it creates**: `Engine/EngineCore.Modules.cs`, `Engine/Seams.Round2.cs` [spine]; the per-package stubs of §5.1
(`EngineCore.<Pkg>.cs` — for Sync, hunks in today's `EngineCore.Sync.cs` — `EngineServices.<Pkg>.cs`, `Host/EngineParts.<Pkg>.cs`,
`Rendering/RenderHost.<Pkg>.cs`, `Render/Shaders/ShaderLibrary.<Pkg>.cs`, `Core/Bus/NodeSettings.<Pkg>.cs`,
`Host/EngineEntry.Optimise.cs`, `tests/fixtures/state/<pkg>.json` and `<pkg>.cards.json`), each empty body holding
`// wp2-<pkg> fills this.`; `tools/gen_stubs.py`; `Render/Engine/FramePasses.cs` (`IFramePass`, `PassFrame`, `IAirLevels`,
`IMeasuredStages`) [spine]; `Render/Gpu/GlDialect.cs` [output]; `Render/Passes/MeasuredStage.cs` [align];
`tools/PlayMap.Node/{PlayMap.Node.csproj, Program.cs}` [spine]; `src/ThirdParty/Directory.Build.props` (sets the TFM and language
itself, imports nothing from the root, `RunAnalyzers=false`, `EnableNETAnalyzers=false`, `TreatWarningsAsErrors=false`,
`Nullable=disable`, no `BannedSymbols.txt`) [spine] and an empty `src/ThirdParty/libomtnet/PlayMap.ThirdParty.Omt.csproj` in
`PlayMap.sln` [videoio]; `THIRD-PARTY-NOTICES.md` with one empty heading per package [each fills its own]; `docs/wire/<pkg>.md`
[each]; `tests/fixtures/state/main.json` (today's STATE) [desk reads it]. No `.editorconfig` change: vendored code sits in its own
project outside the fences (the file is generated; any later change goes through `tools/gen_editorconfig.py`).

**Files it changes** (hunks): `Engine/{EngineCore, EngineCore.Router, EngineCore.Ops, EngineCore.Edits, EngineCore.Content,
EngineCore.Mapping, EngineCore.Devices, EngineCore.Transport, EngineCore.BlendSolve, EngineCore.Sync, StateWriter, ContextBuilder,
NodeHome, Seams}.cs`, `Engine/Host/{EngineParts, EngineExtension, EngineHost, NetworkHost, EngineEntry}.cs`,
`Engine/Rendering/RenderHost.cs`, `Engine/Scanning/{ScanCamera, ScanRunner}.cs`, `Render/Engine/{Contracts, RenderEngine}.cs` (the
air path), `Render/Passes/OutputStage.cs` (`uAir`, `Measured`), `Render/Generators/GeneratorRenderer.cs`,
`Render/Shaders/ShaderLibrary.cs` (no `Header` prefixes; `uAir` in `output`), `Render/Gpu/{GpuTypes, GlDevice, GlDevice.Resources,
ShaderProgram}.cs` (formats, `GlCaps`, the dialect header in `CreateProgram`), `Net/Contracts.cs`, `Net/Wire/WireSession.cs`,
`Net/Http/{HttpHost, WebSocketSession}.cs`, `Net/Mcp/McpTools.cs` (`RiskOf`, `Internal`, available rows), `Assistant/AssistantTools.cs` (`list_verbs`: available rows),
`Assistant/AssistantPrompt.cs` (available rows), `Core/Bus/Pairing.cs` (`NodeSettings` partial), `App/Program.cs`
(`EngineEntry.TryDispatch`), `tests/PlayMap.Engine.Tests/PlayMap.Engine.Tests.csproj` (the node host reference),
`.github/workflows/build.yml` (the Windows job also runs `Render.Tests` and `Media.Tests` with `--filter Category=Windows`).

**Tests it adds**: `RoutesTests` (an engine builds; no row routed twice; every round-2 row answers "not available on this node
yet" until routed — the integration pass flips this to "every row routed"); `EditsRedirectTests` (§5.3); `StateOrderTests` (the
spine's STATE has today's keys in today's order and nothing else; each stub writes nothing); `PushTopicTests` (SUBSCRIBE of an
unknown topic is refused with the topic list; a fake topic's lines reach a wire session); `IndexContributorTests`;
`TextureFormatTests` and `GlCapsTests` (§6.4); `ShaderConventionTests` (no program source holds `#version` or `precision`;
`CreateProgram` refuses one that does, with words; every program compiles and links on core 3.3); `AirPlumbingTests` (llvmpipe:
with no `IAirLevels` every existing render read-back is byte-identical; a fake gain of 0.5 halves one output's codes ± 1 and
leaves the other; `Hold` holds one output only; `Override` shows its source); `NodeHostTests` (`PlayMap.Node engine --headless`
answers `PING`; an unknown verb exits 2 with words); `AvailabilityTests` (the assistant prompt and `list_verbs` hold every routed row
and no unrouted round-2 row; `VERBS` marks them); `WindowsPlatformTests` (every Windows-only type carries
`[SupportedOSPlatform("windows")]`; CA1416 at error). The whole suite stays green, analyzers on; the desk builds.

### 11.3 What the spine does not do

No behaviour of any package (the gate is open, every new verb is refused with words, no STATE key is written, no pass runs, `uAir`
is 1); no desk change beyond `Program.cs`'s dispatch (the desk spine is part of `wp2-desk`); no docs beyond a "Round 2" section in
`docs/WIRE.md` (the STATE key order and one link per package) and `docs/wire/<pkg>.md` — each package's rows and STATE keys,
generated from the catalogue — which the packages then own.
