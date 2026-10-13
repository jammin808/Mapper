# Companion 5.1, the module API 2.2, and Patterns' module: what PlayMap's Companion module should be

*Research for PlayMap round 2, 13 October 2026. Topic: Bitfocus Companion 5.1, `@companion-module/base` 2.2.1, two-way
video-server modules, and Patterns' own Companion module. The last section, "Design implications for PlayMap", gives the
feature list.*

## 0. How to read this

- Every fact has a source tag `[Sn]`. The source table in §10 gives each tag's URL, its date, and whether I opened it
  (**V**, verified) or not (**U**, unverified). Statements marked *(inference)* or *(design)* are mine, not a source's.
- Local shallow clones used (read-only, under `scratchpad/ext/`):

  | Repo | Commit |
  |---|---|
  | `bitfocus/companion` (sparse: docs, CHANGELOG, a few source files) | `404070f`, 11 Oct 2026, "chore: prepare for v5.2 development" |
  | `bitfocus/companion-module-base` | `9360ad7`, 9 Oct 2026 |
  | `bitfocus/companion-module-template-ts` | `e0d42b7`, 11 Oct 2026 |
  | `companion-module-resolume-arena` | `a0e25f4`, 9 Sep 2026 |
  | `companion-module-avstumpfl-pixera` | `ffe9453`, 22 Sep 2026 |
  | `companion-module-figure53-qlab-advance` | `da0f69e`, 7 Oct 2026 |
  | `companion-module-anomes-millumin` | `80c99b9`, 9 Sep 2026 |
  | `companion-surface-elgato-stream-deck` | v1.5.2, 29 Sep 2026 |

- Patterns was read at `/home/user/patterns-svc` (`bcdbf28`, 12 Oct 2026). PlayMap was read at `/home/user/mapper`
  (`0dd62d2`, 13 Oct 2026) and `/home/user/mw/stage`.
- No Companion runs on this machine, so nothing below was confirmed against a running Companion. Behaviour claims come
  from the source code and the documentation.

## 1. The findings in brief

1. **Companion v5.1.0 shipped on 11 Oct 2026 at 18:49 (tag `v5.1.0`, commit `257b325`)** [S2]. The same day the
   repository moved to 5.2 development (`package.json` 5.2.0) [S1].
2. **Module API versions.** `@companion-module/base` **2.2.1** is npm `latest` (9 Oct 2026). Alongside it:
   `@companion-module/host` 1.2.1 and `@companion-module/tools` 3.1.1 [S19].
   - API 2.2 needs Companion 5.1+, API 2.1 needs 5.0+, and API 2.0 needs 4.3+ [S22].
3. **The 5.x features a two-way module can use:**
   - layered buttons built from elements (text, image, box, line, circle, gauge, group, composite);
   - value feedbacks used as element style overrides through `$(this:value)` (5.1);
   - feedback-driven local variables;
   - template preset groups;
   - **linked presets** (5.1, the default when placing, needs a module on API ≥ 2.0);
   - page variables `$(page:…)` and `$(this:delta)` on dials;
   - action results stored into local, page or custom variables (`storeResult` in presets since 2.2);
   - Companion's internal logic blocks inside module presets;
   - image elements fed from data-URI variables;
   - the REST API v2, Satellite button subscriptions, and gauges shown on surface LEDs.

   Sources: [S3][S5][S6][S9][S18][S23][S25].
4. **No studied video-server module is on base 2.x.** Resolume 3.13.0-beta.4 uses ~1.10, Pixera 3.2.3 ~1.4.3,
   QLab 2.15.0 ^1.8, Millumin 4.0.0 ^1.11, Mitti 3.10.2 ~1.12.1, disguise-osc 2.1.0 ~1.12.0 and Watchout 2.0.2
   ~1.4.1 [S27–S33]. None can offer linked presets, template groups, value-feedback style overrides, layered gauges or
   composites. A 2.2 module that does would lead the category on platform features alone *(inference)*.
5. **Patterns' module (3.18.0, base ~2.1.3) is the strongest two-way design studied** [S34–S36]:
   - one line wire with live STATE, mDNS discovery and HMAC pairing;
   - a palette held equal on both sides by a test;
   - presets built from the show, a Navigator, the action recorder, learn, and encoders.

   Measured against 5.1 and API 2.2 it falls short in five places:
   - every feedback is boolean;
   - show presets are keyed by index and name, which breaks linked presets on rename or reorder;
   - the pairing token sits in a plain `textinput`;
   - it has no thumbnails, composites or template groups;
   - replies are matched first in, first out.

   PlayMap should share Patterns' language (palette, words, Navigator, Eye) and fix these gaps, then hand the fixes back.

## 2. Companion 4.0 → 5.1: what changed

Dates: 5.x from the GitHub release page [S2]; 4.x from the date stamps in the "What's new" files [S3].

| Version | Date | What matters for a control module | Src |
|---|---|---|---|
| 4.0.0 | ~5 Jun 2025 | Modules become installable plugins, updated apart from Companion (store or offline bundle). Adds `Logic: if statement`, finer `internal: abort`, editable custom-variable descriptions, object values in custom variables, granular module permissions (internal) | [S1] |
| 4.1.0 | ~16 Sep 2025 | Collections for connections, triggers and custom variables, which can enable or disable everything in them. Adds expression variables, a `while loop` action, **local variables on buttons and triggers**, steps driven by an expression, `secret` config fields, the Ember+ API exposing variables and the action recorder, MX Creative Console | [S1] |
| 4.2.0 | ~9 Dec 2025 | New expression editor, `getVariable()`, local variables in step expressions, page access restrictions, reworked connection config layout, Stream Deck + swipe to change page, Loupedeck touch-strip variables, complex layouts over Satellite, a red warning triangle for button errors, version-number variables. Large entity updates are batched to modules | [S1] |
| 4.3.0 | ~29 Mar 2026 | **Surfaces become modules.** Expressions in *any* action or feedback field (modules opt in through API 2.0). `blink()`, URI and date functions, random-interval triggers, HTTP endpoints for managing connections, Satellite extended to cover the Elgato plugin | [S1] |
| 5.0.0 | 12 Jul 2026 | **Graphics overhaul**: a button is a stack of elements, drawing need not be square, image library, fonts, inactive pages rendered lazily. Expressions gain loops, ifs, statements and an operation limit. Local variables can be read from another button. Feedback-based local variables come from presets. **Actions return results** into local or custom variables. Surface brightness action and variable. Satellite ports announced over mDNS, compressed Satellite images. Shell commands and network module installs off by default. **Module variable updates limited to 50 Hz.** macOS 13.5+ | [S1][S2][S4] |
| 5.0.1–5.0.7 | 14 Jul–1 Oct 2026 | Fixes. Among them: TCP no-delay for Satellite and TCP clients (5.0.2); gauge "auto" origin (5.0.3); variable id format enforced (5.0.4); in-flight updates to each connection limited (5.0.5); simple presets get an imageBuffer layer only when a feedback declares `imageBuffer`, advanced-feedback overrides limited to `affectedProperties`, abort signals reach module action callbacks (5.0.7) | [S2] |
| **5.1.0** | **11 Oct 2026** | Graphics: pinned properties, editing on the preview, aspect-ratio previews, **surface gauge LEDs** (#4314), fill width and alpha on gauges, **value feedbacks as style overrides on layered buttons** (#4379), bold/italic/underline/strike text, smooth autosize, **box corner radius**, rotation of composites. APIs: **REST API v2** prototype (#4259), surfaces list and brightness (#4405), API token management (#4453). Expressions: maths functions (#4388), **`oscillate()`** (#4328), colour functions (#4420). Variables: **page variables** (#4332), **`$(this:delta)`** with rotation amount (#4387). Controls: **button reference (mirror) controls** (#4339), **linked presets** (#4284). Also: Prometheus metrics (#4304), collection status variables (#4479), "Surface: When connected" feedback, trigger conditions re-checked when a trigger fires, hold duration on "trigger press and release", `COMPANION_INSTALL_NAME` | [S1][S2][S3] |

## 3. Companion 5.1 for a module author

### 3.1 Layered buttons and their elements

- **A button is a stack of elements.** Element types are `group`, `composite`, `text`, `image`, `box`, `line`,
  `circle` and `gauge` [S18 graphics.ts][S25].
- **Common properties.** Every element has `id`, `name`, `enabled`, and `opacity` (0–100).
  - Bounds `x`, `y`, `width` and `height` are percentages, 0–100 of the button. A line uses its own endpoints instead.
  - `rotation` is in degrees; the type comment allows −360 to 360 [S18][S25].
  - Every property may be a plain value or `{ isExpression: true, value: '<expression>' }` [S25].
- **text:** `text`, `fontsize` (3–200, as a percentage of the element height), `fontsizeAllowShrink`,
  `font: 'companion-sans' | 'companion-mono'`, `weight: 'normal' | 'bold'`,
  `styles: ('italic'|'underline'|'strikethrough')[]`, `color`, `halign`, `valign`, `outlineColor` [S18].
- **image:** `base64Image` must be a data URI (`data:image/png;base64,…` or JPG); a bare base64 string is refused.
  Also `fillMode: 'crop'|'fill'|'fit'`, `halign` and `valign` [S25].
  - The documented way to show a module-made picture: put the data URI in a module variable, or return it from a
    **value feedback** bound to a local variable. Then reference it from `base64Image`. An empty or invalid value
    draws nothing [S25].
- **box:** `color`, `cornerRadius` (0–50, a percentage of the shorter side, new in 5.1), and the border properties
  `borderWidth`, `borderColor` and `borderPosition: 'inside'|'center'|'outside'` [S18].
- **line:** `fromX`, `fromY`, `toX`, `toY` and the border properties [S18].
- **circle:** `color`, `startAngle`, `endAngle`, `drawSlice`, `borderOnlyArc` [S18].
- **gauge** [S18][S5]:
  - Value: `value`, `min`, `max`, `origin`, `symmetric`.
  - Shape: `orientation: 'horizontal'|'vertical'|'ring'`, `reverse`, `startAngle` and `endAngle` (0–360),
    `ringWidth` (1–50), `roundedEnds`.
  - Fill: `fillEnabled`, `multiColour`, `fillWidth` (0–100, new in 5.1), `stops: [{ value, color, gradient }]`.
  - Marker: `markerEnabled`, `markerColor`, `markerWidth` (1–100).
  - Track: `trackStyle: 'transparent'|'dimmed'`, `trackAmount` (0–100), `trackWidth` (0–100).
- **group:** `children[]` and `squareCoords`, which keeps arrows square on non-square keys.
  **composite:** `elementId` and `options` [S18][S5].
- **Canvas** (layered presets): `decoration: 'default'|'topbar'|'border'|'none'` and
  `showStatusIcons: 'default'|'all'|'none'` [S18][S25].
- **Colours** are a number `0xRRGGBB` or, since API 2.2, a CSS string [S18][S22].
- **Composite elements** are drawing components a module defines with `setCompositeElementDefinitions(...)`. Each
  definition is `{ type: 'composite', name, sortName?, description?, options: <feedback-style input fields>,
  elements[] }`. Modules use them in layered presets, and users can add them to their own buttons. 5.1 lets
  composites rotate [S18][S1].
- **Button reference element:** draws the rendered graphics of another button. Its `page/row/column` location may be
  an expression, for example `$(this:page)/0/0` [S5].

### 3.2 Feedbacks in 5.x

- **Boolean feedbacks override element properties.** In 5.0 a boolean feedback no longer applies a flat style; it
  overrides chosen properties of chosen elements through a "Style Overrides" table. **The lowest feedback in the list
  wins**, the reverse of 4.x. Old configs are converted to the standard Text, Image and Background elements [S6].
- **Value feedbacks** (5.1):
  - Each override is an expression, and `$(this:value)` is the feedback's current value. For example
    `$(this:value) > -6 ? 0xff4040 : 0xffffff`.
  - If the expression returns `undefined` or fails, the element keeps its own value.
  - The UI previews what each override currently evaluates to [S6][S3].
- **"Legacy style" (advanced) feedbacks** return a whole style. Each override copies one of its properties onto an
  element. **Generated images** still exist [S6].
- Users can often skip feedbacks entirely: **every element property can be an expression**, and redraws when any
  variable it uses changes. Examples: `blink(500)` on `enabled`, or a colour chosen by an expression [S6].

### 3.3 Expressions

- **Numeric:** `round`, `floor`, `ceil`, `abs`, `fromRadix`, `toRadix`, plus (5.1) `sqrt`, `pow`, `log`,
  trigonometry and the constant `PI` [S7][S3].
- **Colour (5.1):**
  - Builders: `rgb()`/`rgba()`, `hsl()`/`hsla()`, `hsv()`.
  - Converters: `colorToRgb()`, `colorToHsl()`, `colorToHsv()`, `colorToHex()`, `isColor()`.
  - Adjusters: `colorAlpha()`, `colorLighten()`, `colorDarken()`, `colorSaturate()`, `colorInvert()`.
  - `colorMix(a, b, ratio)` mixes in CIE Lab; `colorIsDark()` [S7].
- **`blink(period, ?duty)`**: the period is at least 100 ms, the duty defaults to 0.5 [S7].
- **`oscillate(period, ?waveform, ?phase)`** (5.1):
  - It gives 0–1. Waveforms are `'sine'` (the default), `'triangle'`, `'sawtooth'` and `'square'`.
  - The minimum period is 100 ms, and it **updates 10 times a second**.
  - Oscillators with the same period stay in step across buttons.
  - It can be used in element properties, feedbacks, local and expression variables and trigger conditions. **It
    cannot be used in action options** [S7].
- Also: `parseVariables()`, `getVariable()`, string, array, time and date functions; since 5.0, control flow with an
  operation limit [S7][S4].

### 3.4 Variables

- **Local variables** belong to a button or a trigger and are read as `$(local:name)`. Since 5.0 another button's
  local variable can be read and set. They come in three kinds: User Value, Evaluate Expression, and Feedback [S8].
- **Page variables (5.1)** are shared by every button on a page and read as `$(page:name)`.
  - They are set with the internal actions **Page Variable: Set value**, **Reset to startup value** and **Write current
    value to startup value**; these can target another page. They are exported with the page.
  - **Triggers cannot read them**, but can set them [S8].
- **Built-in `$(this:…)` variables:** `page`, `page_name`, `column`, `row`, `location`, `active`, `step`, `step_count`,
  `button_status`, `actions_running`, `surface_id` (actions only), **`delta`** (rotary actions only: positive turns
  right, negative turns left), `current` (when setting a variable), and **`value`** (inside a feedback's style
  overrides) [S8].

### 3.5 Linked presets and button references

- **Placing a preset.** Placing is **Linked** by default; Copy is a one-off. The choice is remembered per browser.
  - A linked button runs normally but cannot be edited.
  - The editor shows **Connection**, which can switch to another connection of the same module, and **Preset
    variables**, the template values that make each button of a set different.
  - **Edit** unlinks the button; this cannot be undone.
  - If the connection is disabled or deleted, the button keeps its last look.
  - Linking needs a module on API ≥ 2.0 [S9][S3].
- **How it works in the source** (`PresetReference.ts`, "@since 5.0.0") [S16]:
  - When a connection's presets update, the button rebuilds from the new definition, **keeping the user's template
    values**.
  - Updates whose checksum has not changed are ignored.
  - **If the preset id no longer exists, the button keeps its last-known data.**
  - A rebuild loads the entities as clones, which gives them new ids, and calls `resubscribeEntities()`.
- *(Inference.)* So preset ids must be stable and keyed by identity, never by name or position. The module should
  re-send presets only when their structure changes, because each real change re-subscribes every linked button.
- **Button reference control (5.1).** A whole button that mirrors another button and presses it too. The mirrored
  location may use variables and expressions, such as `$(page:camera)/0/1`.
  - It shows "Unresolved Reference" when the location is invalid and "∞" for a loop.
  - It is created with **Paste as reference** [S9].

### 3.6 Triggers

- **Events:** `interval`, `intervalRandom`, `timeofday`, `specificDate`, `sun_event`, `startup`, `client_connect`,
  `button_press`, `button_release`, `condition_true`, `condition_false`, `variable_changed`, `computer_locked`,
  `computer_unlocked` [S10].
- **Conditions** are boolean feedbacks, the internal ones included. In 5.1 they are re-checked when the trigger fires;
  module feedbacks use the connection's last reported value [S10].
- **A module cannot define trigger events.** It reaches triggers through its boolean feedbacks (used as conditions or
  with `condition_true`/`condition_false`) and its variables (`variable_changed`) *(inference from [S10][S18])*.

### 3.7 Surfaces

- **Surfaces are modules since 4.3** [S1].
- **Elgato module v1.5.2** supports [S17]:
  - Stream Deck 15-key, Mini, XL, Mk2, Pedal, **+**, **+ XL**, **Neo**, **Studio**, Module 6/15/32 and Corsair
    Galleon 100 SD.
  - The Stream Deck + touch strip maps to four press-only buttons and supports swipes: horizontal turns the page,
    vertical rotates.
  - The Neo's LCD segment is not drawn.
  - On the Studio, the left encoder is `0/0` and the right `0/17`; NFC can be written to a custom variable.
  - Its HELP still says the Studio's LED rings are unsupported, but its v1.5.2 source builds `leds` style presets for
    rings with ≥ 6 steps (`MIN_LED_RING_STEPS = 6`). That matches 5.1's "surface gauge LEDs".
- **Loupedeck module** [S17]: Loupedeck Live, Live S and CT, Razer Stream Controller and Stream Controller X, over
  serial firmware. Touch strips write custom variables (0–256).
- **X-keys module** [S17]: most models, with T-bars and jog/shuttle exposed as custom variables.
- **Gauges on LEDs (5.1)** [S5]:
  - The button's first gauge drives the LEDs; the "External Usage" setting picks a different one.
  - No marker is drawn.
  - A ring follows the start and end angles; a strip spreads the value across all its lights.
- **Others:** Contour Shuttle, Resolve Speed/Replay Editor, Mirabox Stream Dock models and MX Creative Console (4.x),
  plus remote surfaces over Satellite [S1].
- **Web buttons:** every page in one browser view, configured through URL parameters. It is **not a surface**, so it
  cannot follow page changes [S13].

### 3.8 Remote-control APIs

- **REST API v2 (5.1, a prototype)** [S11][S3]:
  - Base path `/api/v2`, **off by default**. Every request needs a bearer API key; keys are made in Settings → Protocols
    and shown once. Interactive docs are at `/api/v2/docs`.
  - Scopes: `admin` implies all; `write` and `execute` imply `read`.
  - Stable contract (`openapi-stable.json`):
    - `GET|POST /connections/v1` (create: `{ moduleId, label, disabled?, updatePolicy: 'manual'|'stable'|'beta',
      versionId? }`; when the module is not installed, Companion queues its installation)
    - `GET|PATCH|DELETE /connections/v1/{id}` (patch: `{ label?, disabled?, config?, secrets?, updatePolicy?,
      versionId? }`; config and secrets are merged)
    - `GET /connections/v1/{id}/config-fields`
    - `POST /connections/v1/{id}/restart`
    - `PATCH /connections/v1/move`
    - `GET /connections/v1/tree`
    - `GET /surfaces/v1` and `GET /surfaces/v1/{id}`
    - `PATCH /surfaces/v1/{id}` with `{ brightness: 0–100 }`
- **Older HTTP API** (on by default, admin port 8000) [S13]:
  - Button routes: `/api/location/<p>/<r>/<c>/press|down|up|rotate-left|rotate-right|step|style`.
  - Variable routes: `/api/custom-variable/<name>/value` and `/api/variable/<label>/<name>/value`.
  - Connection routes: `/api/connections[/<id>/status|restart|enable|disable]`.
- **TCP/UDP API.** Default port **16759**, but `tcp_enabled` and `udp_enabled` are **false on a fresh install**.
  Installs upgraded from old databases keep 51234/51235 enabled [S12a].
  - Lines: `SURFACE <id> PAGE-SET|PAGE-UP|PAGE-DOWN`;
    `LOCATION <p>/<r>/<c> PRESS|DOWN|UP|ROTATE-LEFT|ROTATE-RIGHT|SET-STEP <n>|STYLE TEXT|COLOR|BGCOLOR <v>`;
    `CUSTOM-VARIABLE <name> SET-VALUE|GET-VALUE`; `SURFACES RESCAN` [S13].
- **Other listeners:** OSC on 12321, plus Ember+, Art-Net and RossTalk, each off by default [S12a].
- **Prometheus metrics (5.1).** `/api/metrics`, off by default, needs a scrape token [S12].

### 3.9 Satellite API (protocol 1.14.0 in the source that opened 5.2)

- **Transport.** TCP **16622**, WebSocket **16623**. Each message is one line:
  `COMMAND ARG=VAL ARG2="with spaces"`. Booleans are sent as 0 or 1 [S14].
- **Handshake.** The server sends `BEGIN CompanionVersion=… ApiVersion=…`, then (≥ 1.10)
  `CAPS SUBSCRIPTIONS=0|1 NONSQUARE=1 BITMAP_FORMATS=rgb,png,webp`. Clients must `PING` about every 2 s [S14].
- **Surfaces.**
  - Registration: `ADD-DEVICE DEVICEID=… PRODUCT_NAME="…"`. Simple mode takes `KEYS_TOTAL`, `KEYS_PER_ROW`,
    `BITMAPS=<px>`, `COLORS=hex|rgb`, `TEXT`, `TEXT_STYLE`. Advanced mode takes `LAYOUT_MANIFEST=<base64 json
    {stylePresets, controls}>`. Optional on both: `VARIABLES`, `SERIAL`, `BRIGHTNESS`, `PINCODE_LOCK`,
    `CONFIG_FIELDS`, `CAN_CHANGE_PAGE`, `BITMAP_FORMAT`.
  - Client to server: `KEY-PRESS … PRESSED=`, `KEY-ROTATE … DIRECTION=`, `SET-VARIABLE-VALUE` (base64),
    `CHANGE-PAGE`.
  - Server to client: `KEY-STATE DEVICEID KEY|CONTROLID [TYPE PRESSED LOCATION BITMAP COLOR TEXTCOLOR TEXT
    FONT_SIZE]`, `KEYS-CLEAR`, `BRIGHTNESS`, `VARIABLE-VALUE`, `LOCKED-STATE`, `DEVICE-CONFIG` [S14].
- **Button subscriptions** (≥ 1.10; Companion setting "Enable Button Subscriptions API", **off by default**; it
  bypasses PIN lockout and the page system) [S14][S12a]:

  ```
  → ADD-SUB SUBID=pm-go LOCATION=1/0/0 BITMAP=72 COLORS=hex TEXT=true BITMAP_FORMAT=png
  ← ADD-SUB OK
  ← SUB-STATE SUBID=pm-go TYPE=BUTTON PRESSED=0 BITMAP=data:image/png;base64,… COLOR=#1e9e5a TEXT=R08=
  → SUB-PRESS SUBID=pm-go PRESSED=true      → SUB-ROTATE SUBID=pm-go DIRECTION=1      → REMOVE-SUB SUBID=pm-go
  ```

- **Later protocol versions** [S14b]:
  - 1.13.0 adds a `leds` capability to advanced style presets.
  - 1.14.0 lets `DIRECTION` on `KEY-ROTATE` and `SUB-ROTATE` be a signed step count, advertised as `ROTARY_AMOUNT`
    in CAPS.
- **mDNS.** Companion announces `_companion-satellite-tcp._tcp` (16622) and `_companion-satellite-ws._tcp` (16623)
  with TXT `id`, `version` and `protocolVersion`. The name is the install name, or `Companion (<hostname>)` [S15].

### 3.10 Other platform notes

- **Bitfocus Buttons.** Bitfocus' routing and control product runs Companion connection modules.
  - v1.8 (2 Oct 2026) "added basic Companion module API 2.0 and 2.1 support" [S26, U: search excerpt].
  - Bitfocus' module docs say simple presets are "required for compatibility with Bitfocus Buttons", and recommend
    `alternatives` with a simple fallback [S23][S25].
- **Security defaults in 5.0** [S4]: shell commands and network module installs are off, with DNS-rebind and origin
  hardening. The admin password protects the UI, not the remote-control protocols, the REST API or metrics [S11].
- **Export format.** `ExportBase.version` runs 6–17, with types `full`, `page` and `trigger_list`. Control and trigger
  content are typed `Record<string, any> // TODO`, so generating export files is fragile [S39].

## 4. The module API: `@companion-module/base` 2.2.1

### 4.1 Versions and timeline

- **Release dates** [S18][S19]:

  | Version | Date |
  |---|---|
  | 2.0.0-alpha.0 | 22 Feb 2026 |
  | 2.0.0 | 2 Mar 2026 |
  | 2.0.1–2.0.4 | Mar–Apr 2026 |
  | 2.1.0 | 26 Jun 2026 |
  | 2.1.3 | 12 Aug 2026 |
  | 2.2.0 | 1 Oct 2026 |
  | **2.2.1** | **9 Oct 2026** |

  The base package's engines field is `node ^22.20 || ^26.5`.
- **2.2.1** adds `sanitizeVariable()` and allows rotation on composites.
- **2.2.0 adds:**
  - value-feedback result types in schemas;
  - CSS colour strings;
  - preset `notes`;
  - `storeResult` in presets;
  - a manifest schema generated from zod.
- **2.1.0 adds:**
  - action results;
  - internal actions and feedbacks in presets;
  - node26;
  - `affectedProperties`;
  - the graphics overhaul (layered presets, composites, gauges in presets);
  - abortable learn;
  - abort signals for actions and feedbacks;
  - **preset alternatives**;
  - feedback-based local variables;
  - a TCPHelper fix so it reconnects after the connection closes.
- **Companion compatibility.** API 2.2 needs Companion 5.1+, 2.1 needs 5.0+, and 2.0 needs 4.3+ [S22].
- **Tools.** `@companion-module/tools` 3.1.1 provides `companion-module-build` (bundles with esbuild),
  `companion-module-check`, `companion-surface-build` and `companion-surface-check`; it needs node ^22.18 [S19].
- **Host.** `@companion-module/host` 1.2.1 is the host-side wrapper; it validates presets and options [S20].
- **The TypeScript template** (11 Oct 2026) [S21]:
  - Dependencies: base `~2.1.3`, TypeScript 6.0.3, yarn 4.18.1, a `node26` runtime, ESLint 10, husky and lint-staged.
  - CI: `bitfocus/actions/.github/workflows/module-checks.yaml@main`.
  - Releases attach `pkg.tgz`.

### 4.2 `InstanceBase<TManifest>`: lifecycle and calls [S18 base.ts]

- **Entry point.** A module is the **default export** of its entry file, with **`UpgradeScripts`** as a named export.
  `runEntrypoint` was removed in 2.0.
- **Typing.** `TManifest` is `{ config; secrets; actions; feedbacks; variables; compositeElements? }`. The schemas give
  typed options in actions, feedbacks and presets.

| Member | Notes |
|---|---|
| `init(config, isFirstInit, secrets)` | async, abstract |
| `configUpdated(config, secrets)` | async, abstract. `label` may change just before it is called |
| `destroy()` | async, abstract |
| `getConfigFields()` | config fields |
| `id`, `label`, `instanceOptions` | the last holds `disableVariableValidation` and `disableNewConfigLayout` (deprecated) |
| `saveConfig(newConfig, newSecrets?)` | The whole config, and the **keys** of secrets, are reported to the web UI |
| `setActionDefinitions(defs)`, `setFeedbackDefinitions(defs)` | Can be called again at any time to replace the lists; the docs say to batch, because "updating the list has a cost" [S24] |
| `setPresetDefinitions(structure, presets)` | Call after actions and feedbacks are set [S23] |
| `setCompositeElementDefinitions(defs)` | 2.1+ |
| `setVariableDefinitions(object)` | An array throws |
| `setVariableValues(partial)`, `getVariableValue(id)` | |
| `checkAllFeedbacks()` | |
| `checkFeedbacks(type, ...types)` | **At least one type is required** in 2.x |
| `checkFeedbacksById(...ids)` | |
| `subscribeActions(...ids)`, `unsubscribeActions(...ids)`, `unsubscribeFeedbacks(...ids)` | |
| `recordAction(action, uniquenessId?)` | Paired with `handleStartStopRecordActions?(isRecording)` |
| `handleHttpRequest?(req)` | `req` is `{ baseUrl, body?, headers, hostname, ip, method, originalUrl, path, query }`; returns `{ status?, headers?, body? }` |
| `oscSend(host, port, path, args)` | |
| `updateStatus(status, message?)` | |
| `log(level, message)` | |
| `createSharedUdpSocket('udp4'|'udp6' or { type }, callback?)` | Companion hosts the socket, so several connections can share a fixed port |

- **`InstanceStatus` values:** `ok`, `connecting`, `disconnected`, `connection_failure`, `bad_config`,
  `unknown_error`, `unknown_warning`, `authentication_failure`, `insufficient_permissions`.
- **Helpers:**
  - `TCPHelper` options `{ reconnect: true, reconnect_interval: 2000 }` by default; also `UDPHelper` and
    `TelnetHelper`.
  - `combineRgb`, `splitRgb`, `splitHsl`, `splitHsv`, `splitHex`, `parseEscapeCharacters`,
    `substituteEscapeCharacters`, `sanitizeVariable(id, replacements)`.
  - The `Regex` constants (IP, HOSTNAME, PORT, …) [S18].

### 4.3 Replacing definitions live, and the limits

- **Every definition can be replaced at any time.** That includes presets: Resolume rebuilds its actions and
  feedbacks when its REST API connects, and Patterns rebuilds its show presets when the show's lists change
  [S27][S34]. Batch the calls [S24].
- **Variable ids must match `^[a-zA-Z0-9-_.!]+$`.** For modules built on API ≥ 2.2 a bad id is a **hard failure and
  the value is dropped** [S20]. Use `sanitizeVariable()` [S18].
- **Variable updates** from one module are limited to **50 Hz** by Companion [S1].
- **Template groups** exist because modules emitting "100s or 1000s of presets" caused performance problems and
  crashes on small machines [S23].
- **Learn** times out after **5 s** by default (`learnTimeout`) [S18].
- **Feedback rechecks.** A recheck queued while a callback runs aborts that callback through `context.signal` [S18].

### 4.4 Actions [S18 action.ts]

- **Definition fields:** `name`, `sortName?`, `description?`, `options[]`, and `callback(event, context)`.
  - `event` is `{ id, controlId, actionId, options, surfaceId }`.
  - `context` carries `signal: AbortSignal` and a deprecated `setCustomVariableValue`.
- **Results:** `hasResult: true` lets the callback return a `JsonValue`. A preset can store it with
  `storeResult: { type: 'local-variable', variableName }` (2.2), and the user can store it to custom or page
  variables.
- **Learn:** `learn(event, { signal })` returns **only the learned option values** (2.0 change) [S18 changelog].
- **Subscriptions:** `subscribe(info)` and `unsubscribe(info)` need `optionsToMonitorForSubscribe[]`;
  `skipUnsubscribeOnOptionsChange` is optional.
- **Option field types:** `static-text`, `textinput`, `dropdown`, `multidropdown`, `colorpicker`, `number`,
  `checkbox`, `custom-variable`, `bonjour-device` and `secret-text` (the last two are config only).
  - Common properties: `tooltip`, `description`, `expressionDescription`, `isVisibleExpression` (e.g.
    `!$(options:desk)`), `disableAutoExpression` and `allowInvalidValues`.
  - **Since 2.0 every field can be switched to an expression** unless `disableAutoExpression` is set. Companion
    evaluates expressions before calling the module, which can no longer parse variables itself [S18][S22][S24].
- **Recorded actions** are `{ actionId, options, delay? }`; the delay is relative to the previous action [S18].

### 4.5 Feedbacks [S18 feedback.ts][S24]

- **`boolean`:** `defaultStyle`, `callback → boolean`, `showInvert?` (Companion adds "Inverted" by default). These are
  the recommended type.
- **`value`:** `callback → JsonValue`; since 2.2 the schema's `result` types it. Companion evaluates value feedbacks
  only while they are placed. Results reach buttons through feedback-based local variables or style overrides
  through `$(this:value)` [S24][S6].
- **`advanced`:** returns a partial style plus `imageBuffer` (base64), `imageBufferEncoding.pixelFormat
  'RGB'|'RGBA'|'ARGB'` and `imageBufferPosition { x, y, width, height, drawScale? }`, and should declare
  `affectedProperties`. The base package marks it *"discouraged … will likely be removed in a future major version"*.
  Its events carry `image { width, height }`.
- **Common fields:**
  - `name`, `sortName`, `description`, `options`.
  - `unsubscribe(fb)`: the subscribe hook was removed in 2.0. The callback is called whenever a feedback is added;
    load data by dispatching without awaiting, then call `checkFeedbacksById`.
  - `learn` and `learnTimeout`.
- **Feedback info** is `{ type, id, controlId, feedbackId, options, previousOptions }` [S24].

### 4.6 Variables

- **Definitions** are an object `{ id: { name } }`; values are any `JsonValue`.
- A variable that has been **removed is cleared** [S18].
- A type-checked `InstanceTypes.variables` record types `setVariableValues` [S18].

### 4.7 Presets [S18 preset/*][S23]

- **Preset types:**
  - `simple`: `style` (`text`, `textExpression`, `size`, `color`, `bgcolor`, `alignment`, `pngalignment`, `png64`,
    `show_topbar`), plus `previewStyle`, `options.stepAutoProgress`, `steps`, `feedbacks` (each with a `style`),
    `localVariables`, and `keywords`/`notes`.
  - `layered`: `elements[]`, `canvas`, `options`, `steps`, `feedbacks` (each with `styleOverrides: [{ elementId,
    elementProperty, override }]`) and `localVariables`.
  - `alternatives`: `{ type: 'alternatives', variants: [...] }`, most preferred first and never nested. The host shows
    the first variant it can draw.
- **Steps:** `{ name?, down[], up[], rotate_left?[], rotate_right?[], [ms]: { options: { runWhileHeld }, actions[] }
  }`.
  - Each action may carry `delay` (always relative since 2.0), `headline` and `storeResult` (2.2).
- **Local variables:**
  - `{ variableType: 'simple', variableName, startupValue, headline? }`;
  - `{ variableType: 'feedback', variableName, feedbackId, options }` (2.1).
- **Internal entries usable in presets** (2.1):
  - Actions: `internal:wait { time }`, `internal:customLog { message }`, `internal:abortButton { skipReleaseActions? }`,
    `internal:localVariableSet { name, value }`.
  - Feedbacks: `internal:checkExpression { expression }`, `internal:buttonPushed { treatSteppedAsPressed? }`,
    `internal:buttonCurrentStep { step }`.
  - Blocks: `internal:actionGroup { executionMode }`, `internal:logicIf { condition, actions, elseActions? }`,
    `internal:logicWhile` and `internal:logicOperator { and|or|xor }`.
  - The host drops any entry the module's API version predates.
- **Structure:** `CompanionPresetSection { id, name, description?, keywords?, definitions: groups[] | presetIds[] }`.
  - Simple group: `{ id, type: 'simple', name, description?, keywords?, presets[] }`.
  - **Template group:** `{ id, type: 'template', name, presetId, templateVariableName, templateValues: [{ name?, value
    }], commonVariableValues? }`. One preset becomes many buttons, and a value's `name` replaces the preset's name.
  - Section and group ids "should be stable across updates".
- **Bitfocus' standard colours:** red STOP, green GO, yellow HOLD, blue AUX, purple for "needs configuration" [S23].
  *(Patterns' palette replaces these; PlayMap should follow Patterns.)*

### 4.8 Configuration, secrets and discovery

- **Config fields** are input fields with `width`. Secrets are a separate typed object, passed to `init` and
  `configUpdated` and saved through `saveConfig(config, secrets)`; `secret-text` is the field type [S18].
- **Manifest** (one zod schema; strict mode rejects template placeholders) [S18 manifest-schema.ts]:
  - Required: `type: 'connection'`, `id`, `name`, `shortname`, `description`, `version`, `license` (SPDX),
    `repository`, `bugs`, `maintainers[]`, `legacyIds[]`, `runtime`, `manufacturer`, `products[]` (≥ 1),
    `keywords[]`.
  - Optional: `isPrerelease`.
  - `runtime` is `{ type: 'node22'|'node26', api: 'nodejs-ipc', apiVersion, entrypoint, permissions?:
    { worker-threads, child-process, native-addons, filesystem, insecure-algorithms } }`.
  - **`bonjourQueries`** is `{ <fieldId>: query | query[] }`, where a query is `{ type, protocol: 'tcp'|'udp', port?,
    txt?: {k: v}, addressFamily?: 'ipv4'|'ipv6'|'ipv4+6' }`. Each key needs a `bonjour-device` config field with the
    same id. Companion hands the module `address:port`, or nothing for manual entry [S18][S35].

### 4.9 Upgrade scripts

- **Signature:** `CompanionStaticUpgradeScript(context: { currentConfig }, props: { config, secrets, actions[],
  feedbacks[] }) → { updatedConfig, updatedSecrets?, updatedActions, updatedFeedbacks }`.
  - Migration options are `ExpressionOrValue` wrappers.
  - A migrated action can gain `storeResult: { type: 'custom-variable', variableName }`.
- **Helpers:** `EmptyUpgradeScript`, `CreateConvertToBooleanFeedbackUpgradeScript`,
  `CreateUseBuiltinInvertForFeedbacksUpgradeScript`, `CreateUseActionResultStoreUpgradeScript`,
  `FixupNumericOrVariablesValueToExpressions`, `FixupBooleanOrVariablesValueToExpressions` [S18].
- **Scripts are append-only:** "once it has been added it cannot be removed" [S21].

### 4.10 Packaging and testing

- **Packaging.** `companion-module-build` bundles to `pkg.tgz`; `companion-module-check` lints and validates; the
  Bitfocus CI workflow builds on push and attaches the package on release [S19][S21].
- **Validators:**
  - `validateManifest()` from `@companion-module/base/manifest`.
  - `sanitisePresetDefinitions()` from the host package drops unknown action or feedback ids and option keys, checks
    layered elements against a schema, and checks style overrides against declared element ids.
  - The host's `validate` export carries Companion's option validators (#266) [S20][S34].
- **How modules are tested in practice:**
  - The base repository uses vitest [S18].
  - Patterns boots its real instance against a fake `InstanceContext` that records every definition and value. It
    runs presets through the host's sanitiser, emits every line the actions can send into `test/lines.txt` for the
    desk's C# parser, and tests the package as Companion opens it [S34].
  - The peers studied have no automated tests (`"test": "echo … no test"` in Watchout) [S33].

### 4.11 What changed from 1.x to 2.x [S18 CHANGELOG 2.0.0-alpha.0 … 2.2.1][S22]

| Area | 1.x | 2.x |
|---|---|---|
| Entry point | `runEntrypoint(Class, UpgradeScripts)` | `export default` class plus `export const UpgradeScripts`; ESM; Node 18 dropped |
| Manifest | `type` optional | `type: 'connection'` required; `node22` or `node26` |
| Variables | array of `{ variableId, name }` | object `{ id: { name } }`; any JSON value; id regex enforced (hard failure at 2.2) |
| Options | `InputValue`, `useVariables`, `parseVariablesInString` | `JsonValue`; **automatic expressions** on any field; no parsing in the module; `isVisible` functions replaced by `isVisibleExpression`; `required` replaced |
| Feedbacks | subscribe and unsubscribe hooks; `checkFeedbacks()` with no arguments | subscribe removed; unsubscribe only; `checkAllFeedbacks()` split out; `previousOptions`; abort signal; imageBuffer format changed |
| Actions | `optionsToIgnoreForSubscribe` | `optionsToMonitorForSubscribe`; results (`hasResult`); abort signal; typed subscribe hooks |
| Learn | return all options | return only the learned values; abortable |
| Presets | flat dictionary with a `category` string; `text` presets; absolute delays | sections, groups and **template groups**; local variables; layered presets; alternatives; internal entries; `storeResult` and `notes` (2.2); delays always relative |
| Typing | loose | `InstanceBase<{ config, secrets, actions, feedbacks, variables, compositeElements }>` gives strong typing through presets |
| Misc | — | `sortName`; scoped logging; `InsufficientPermissions` status; Bonjour `addressFamily`; TCP and UDP helpers reworked; TCPHelper reconnects on close |

## 5. Two-way video-server modules on GitHub

| Module (version, base, runtime) | State in | Dynamic definitions | Pictures | Reconnect and health | Notable |
|---|---|---|---|---|---|
| **resolume-arena** 3.13.0-beta.4, base ~1.10.0, node18, about 15.4k lines of TS [S27] | WebSocket `ws(s)://host:port/api/v1`. The full composition is pushed; parameters are subscribed by id or path (`{action:'subscribe'}`, also `unsubscribe`) with `parameter_update` pushes. An OSC listener carries transport position | Actions and feedbacks rebuilt when the REST API comes up; variables and presets depend on config (OSC listener on or off) | **Clip thumbnails** from REST `composition/layers/<l>/clips/<c>/thumbnail`, decoded from PNG and scaled to 64×64 RGB with `@julusian/image-rs`, drawn by an advanced "Clip Info" feedback whose subscribe and unsubscribe manage the fetches | A 5 s reconnect timer after close; `ConnectionFailure` on `ECONNREFUSED`; API restart | The deepest state model; encoder actions subscribe parameters |
| **avstumpfl-pixera** 3.2.3, base ~1.4.3, node18 [S28] | JSON-RPC 2.0 over TCP **1400**, frames with a 4-byte `pxr1` header. **Polling**: optional 50–1000 ms timeline-state poll and a 100 ms poll of the selected timelines; `Pixera.Utility.pollMonitoring` | Discovery of live systems, outputs, studio cameras, projectors, resource folders, timelines and screens fills dropdowns, retried when the engine is not ready | none | TCPHelper reconnect at 2 s, **plus a watchdog**: a receive timeout, rebuilds with exponential backoff, a `getApiRevision` keepalive, and engine state dropped on link loss so feedbacks never report a dead system | A `livesystem_state` feedback is a small per-machine health view |
| **figure53-qlab-advance** 2.15.0, base ^1.8.0, node18 [S29] | OSC over TCP or UDP (replies on 53001); `/updates 1` subscription (QLab 5); workspace passcode via `/connect`; `/showMode` | Per-cue variables `q_<num>_<field>` and `id_<uniqueID>_<field>`, cleared when a cue disappears | PNG icons (`png64`) in presets; 4 advanced feedbacks | A 100 ms or 250 ms pulse polls what QLab does not push | **`min_go` feedback**: double-GO protection from `/settings/general/minGoTime`; show-mode aware |
| **anomes-millumin** 4.0.0, base ^1.11.0 [S30] | OSC | — | — | — | `GetPresetList()` returns `{}`: no presets |
| **imimot-mitti** 3.10.2, base ~1.12.1, node22 [S31] | OSC/UDP control; feedback to UDP 51001 | — | — | Optional "Feedback Alert" error when no feedback arrives | `bonjourQueries: { bonjourHost: { type: 'osc', protocol: 'udp', port: 51000 } }`; **Panic** action |
| **disguise** (`disguise-osc`) 2.1.0, base ~1.12.0, node18 [S32] | OSC only | — | — | — | "Full functionality only … if disguise is configured to always send OSC feedback" |
| **dataton-watchout** 2.0.2, base ~1.4.1 [S33] | — | — | — | — | No tests |

**What to take from them** *(inference)*:

- Pushed subscriptions beat polling.
- Load data per feedback, lazily, and only while the feedback is placed.
- Fetch thumbnails from the server, and only for keys that are on a button.
- Use a watchdog with backoff, and drop state on link loss.
- Fill dropdowns from the device's own lists.
- Guard against double GO.
- Report connection state honestly. PlayMap's UX paper already notes that Resolume's module cannot tell whether its
  OSC port is connected [S37].

None of them uses linked presets, template groups, gauges, value feedbacks, composites, encoders with `$(this:delta)`,
action results or the action recorder.

## 6. Patterns' Companion module (3.18.0)

### 6.1 Its shape [S34]

- **Package.** `companion-module-jammin808-patterns` 3.18.0. Manifest id `jammin808-patterns`, runtime `node22`,
  apiVersion `2.1.3`, `bonjourQueries: { desk: { type: 'patterns', protocol: 'tcp' } }`. Dependencies: base `~2.1.3`,
  host `^1.1.5`, tools `^3.1.0`.
- **Source files:**

  | File | Lines | Role |
  |---|---|---|
  | `main.js` | 378 | the wire, pairing, replies, recorder |
  | `actions.js` | 748 | about 129 actions |
  | `feedbacks.js` | 250 | about 107 feedbacks, **all boolean** |
  | `variables.js` | 249 | about 154 fixed plus banks, roughly 400 variables (counted statically) |
  | `state.js` | 319 | STATE to variables, pure |
  | `presets.js` | 443 | fixed, show-built and Navigator presets |
  | `nav.js` | 426 | the Navigator state machine, pure |
  | `config.js` | 102 | discovery, groups |
  | `palette.js` | 104 | the colour table, as data |

- **Tests:** `module.test.mjs` (607 lines), `nav.test.mjs` (266), `package.test.mjs` (122), a fake-host harness, and
  `emit-lines.mjs`, which writes `test/lines.txt` (301 lines).

### 6.2 The wire contract [S34][S35]

```
→ HELLO FOH deck module=3.18.0                 (TCP 9697; the connection's label is the name)
← NONCE <hex>                                  (round 87 desks with a pairing token)
→ AUTH-PROOF <hex HMAC-SHA256(key = token without dashes or spaces, upper-cased; msg = nonce + name)>
  (no NONCE within 1500 ms → AUTH <token>, for older desks)
→ NAV                                          ← OK {"protocol":1,"rails":[…],"pages":[…]}
← STATE {…}                                    (pushed on every change, latest wins)
→ MENU PAGE Looks / MENU LOOK Walk-in          ← OK {"protocol":1,"groups":[{"heading","entries":[{id,text,tone,on,enabled,because,wire,menu,…}]}]}
→ RECORD ON                                    ← ACTION <line> …  (while Companion's recorder is open)
→ NAV DECK PLAN › Looks                        (the deck says where its Navigator is)
```

- **Reply matching.** Every line gets exactly one `OK` or `ERR`, matched first in, first out. Questions time out after
  5000 ms; a drop or reconnect rejects everything pending.
- **Trust errors set the status.** `ERR wrong token` and `ERR not paired` set `BadConfig` with words saying what to
  type. A desk speaking a newer descriptor protocol sets `UnknownWarning`.
- **"A dead key must not stay green":** on disconnect the state is emptied and every feedback rechecked.

### 6.3 Presets, feedbacks, variables

- **Groups.** 20 preset groups can be ticked in the config, so each deck lists only what it uses.
- **Banks label themselves.** `look_bank_n` carries `$(patterns:look_n)`, and a `slot_empty` feedback dims empty
  slots.
- **Show-built presets.** "… — this show" presets are rebuilt whenever the show's names change. Their ids are
  `show_look_${i+1}_${safe(name)}`.
- **Navigator presets.** Rails and pages come from the `NAV` reply.
- **One `alternatives` preset.** The stage timer: a layered ring gauge, digits and a ground in the timer's colour, with
  a simple fallback.
- **Two-press TAKE OVER.** Two steps plus the `internal:buttonCurrentStep` feedback, showing "SURE?".
- **Encoders.** `rotate_left` and `rotate_right` set audio and music levels as an absolute value from STATE ± a step,
  never a relative nudge.
- **The Navigator** [S35]:
  - Eight fixed keys: HOME, BACK, PREV, NEXT, MODE (MENU or RUN), FOLLOW, SETTINGS and a title. Then 24 slot keys
    (a Stream Deck XL minus 8).
  - Levels go rails → pages → page menu → a thing's menu → drawer.
  - It follows the desk both ways, colours each slot by the entry's tone, and dims disabled entries with the reason.
- **Recorder and learn.** `RECORD ON` makes the desk push `ACTION` lines, which become `raw` actions. The `raw` action's
  `learn` takes the desk's next action (30 s timeout).

### 6.4 The contract held by tests [S34][S36]

- **`palette.js` ↔ `CompanionPalette.cs`.** C# parses the JS with a regex: 26 colours, and the state rows by kind.
- **`lines.txt` ↔ `ControlProtocol.Parse`.** Every line the module can send must parse on the desk. It must also
  round-trip through `WireWriter`, which is the recorder's oracle.
- **Manifest and package.** Checked for `type: connection`, `node22`, `bonjourQueries` and the default export.
- **Protocol version.** `KNOWN_PROTOCOL` must equal `ControlProtocol.DescriptorVersion`.
- **Module version.** `CompanionModule.Version` must equal `package.json`. The rule: the version moves on every build,
  because Companion 5 refuses a version it already has.
- **The desk side:**
  - `CompanionWords` parses `HELLO <name> module=<ver>`, marks decks running an older module, and writes the Remote
    page's lines.
  - `CompanionSession` drives Companion's TCP API as a device profile: `PAGE`, `PRESS`, `DOWN`, `UP`, `ROTATE`,
    `STEP`, `TEXT`, `COLOR`, `BGCOLOR`, `VAR`, `GET`, `RESCAN` and `RAW`, answered with `+OK` or `-ERR`.
  - The desk browses for `_companion-satellite-tcp._tcp` to name the Companion in the room.

### 6.5 Strengths

- **One protocol for everything.** Every key is a journalled wire line. The desk's own menus reach the deck as data,
  so new pages appear on the deck without a module release.
- **Honest feedback.** A key's light is the desk's STATE, never the deck's press ("attempts are not facts"). Keys go
  dark on disconnect, and refusals carry words.
- **Zero typing to connect.** The desk is found over mDNS, paired with an HMAC proof, and announces the module version
  on HELLO.
- **The best colour discipline of any module studied.** One hue per kind and one treatment per state, enforced
  across C# and JS.
- **Testable without Companion.** A fake host, the real base, the real host sanitiser, and a packaging emulation.

### 6.6 Gaps against Companion 5.1 and API 2.2 *(inference from [S34] against [S18][S23])*

1. **Every feedback is boolean.** There are no value feedbacks, no feedback-based local variables, and only one gauge
   in one preset.
2. **Show-built preset ids contain the item's index and name.** A rename or reorder creates a new preset id, so a
   **linked** button keeps the stale last-known copy instead of following [S16]. The bank keys, which use variables,
   avoid this.
3. **The pairing token is a `textinput` in config, not a `secret-text` secret.** Companion reports the whole config to
   the web UI [S18].
4. **Every STATE push rewrites about 400 variables and rechecks every feedback.** That is correct but coarse.
   Companion caps module variable updates at 50 Hz [S1].
5. **No thumbnails** (deliberately left, [S35] §6), no composites, no template groups, and no `storeResult` or `notes`
   (needs base 2.2).
6. **Replies are matched first in, first out.** Patterns' wire has no request ids; PlayMap's has (`@id`) [S37].
7. **Satellite was left out on purpose.** A generated page file was left too, because the export schema is untyped
   [S35][S39].

### 6.7 What PlayMap's module must share so the two feel like one family *(design)*

| Shared item | How |
|---|---|
| **Palette** | The same `COLOURS` and `STATES` numbers, word for word. PlayMap may **add** kinds (`playlist`, `output`, `projector`, `surface`, `scan`, `calibration`, `show`, `fleet`) but never recolour Patterns'. Hold it with a cross-repo test: PlayMap vendors `palette.js` with a SHA-256 pin, a test fails if Patterns' copy moves, and Patterns adopts PlayMap's new rows. Better still, a single `show-palette.json` published from Patterns |
| **Words** | `HELLO <name> module=<ver>`. NONCE then `AUTH-PROOF` HMAC-SHA256 over nonce + name (both wires already agree, [S37]). The same `ERR not paired` / `ERR wrong token` mapped to the same status words. Variables `last_error` and `node_protocol` beside Patterns' `desk_protocol` |
| **Variable conventions** | `<kind>_<n>` banks that label themselves. **PlayMap's `video` row is already Patterns' shape** [S37], so keep the names `video_file`, `video_remaining`, `video_chip`, … identical: a key built for Patterns' VT clock then works on PlayMap |
| **Navigator** | The same 8 fixed keys, 24 slots, levels, `NAV` / `MENU` JSON descriptor (`protocol: 1`) and tones. Make `nav.js` (pure) a shared file, so an operator who learnt Patterns' deck can program PlayMap's. PlayMap's rails: PLAY, MAP, SCAN, plus SETUP and ADMIN |
| **Eye** | The GOD'S EYE headline key lit by the worst light, NEXT/PREV PROBLEM, LENS and RESET, with the same verbs (`EYE NEXT`, …). PlayMap has `light`, `headline` and `faults` in STATE and Show Bus `CONTEXT` items [S37]. PlayMap also appears as a node in Patterns' Eye |
| **UX of the config** | Discover the node by mDNS (`bonjour-device`), pairing token, preset groups as ticks, the same HELP.md layout, the same version-bump rule |
| **Tests** | `lines.txt` parsed by PlayMap's `VerbCatalog.Match`; the palette test; the host sanitiser test; the package test |
| **Composites** *(new, offered back to Patterns)* | `tile`, `light`, `score` and `countdown` composite elements, defined the same way in both modules, so users build mixed Patterns and PlayMap pages in one look |

## 7. PlayMap's side today (what the module can rely on) [S37]

- **Wire and request ids.** The line wire is TCP 9941; HTTP, the WebSocket and MCP are on 9940. Optional request ids
  `@a1` are echoed after the first word (`OK @a1 …`). Receipts are `{"stage":"accepted","effect":…}` for slow verbs.
- **Pairing.** NONCE is the first line on connect when the node has a token and the peer is not trusted. Proof is
  `AUTH-PROOF` = HMAC-SHA256(token, nonce + name). Queries are open; mutating verbs need pairing.
- **State and verbs.**
  - `HELLO` makes the session a STATE listener: a 200 ms trailing throttle, identical pushes skipped, at least 1 Hz
    while a playlist plays.
  - `SUBSCRIBE STATE|CONTEXT|PROJECT|OFF` adds or stops pushes.
  - `VERBS` returns JSON with name, syntax, words, risk (Low, Review or High) and whether the verb mutates.
- **STATE rows:** `light`, `headline`, `air`, `video` (Patterns' shape), `playlists[]` (with `next`), `outputs[]`
  (fps, dropped, light), `media` (transfers), `stage` (projectors solved and residual), `scan`, `sync`, `links`
  (Patterns, Stagewatch timer), `peers[]`, `machine`, `project` (revision, dirty), `faults[]`, `ai` (proposals).
- **Previews.** `VIEW` previews are a local shared-memory ring; remote JPEG frames are planned. There is **no HTTP
  thumbnail endpoint yet** [S37 stage].
- **Discovery** is a Show Bus hello on UDP 9928 every 2 s. **mDNS is not built** (roadmap R4.9).
- **Planned.** Show Mode (`SHOW ARM`), blind edit, panic outcomes and locks (R3.5). The role table puts "Companion" in
  **remote operator**: everything granted while in rehearsal, **transport only (GO, NEXT, blackout) while armed**
  (UX §7.1). The Companion module itself is roadmap R3.4.

## 8. Design implications for PlayMap

The aim: the best module of its kind. A mapping server's whole show — playlists, surfaces, projectors, calibration,
fleet health and AI proposals — becomes live, linked, picture-bearing keys in Patterns' colour language. It is safe
in Show Mode and updates itself as the project changes.

### 8.1 Target and packaging

- **Package and runtime.** `companion-module-jammin808-playmap`: id `jammin808-playmap`, manufacturer `jammin808`,
  products `["PlayMap"]`, base **`~2.2.1`**, runtime `node22`, TypeScript from the TS template.
  - The manifest schema accepts `node22` or `node26` [S18]. Patterns ships `node22` and its docs say Companion 5
    bundles it; I did not check that myself [S34][S35]. The TS template defaults to `node26` [S21].
- **Typing from the catalogue.** Typed `InstanceTypes` come from a **codegen of `VerbCatalog`** (via `GET /api/verbs`
  at build time), checked into the module as `verbs.generated.ts`.
- **Companion floor is 5.1**, for linked presets, value-feedback overrides, `storeResult` and `notes`.
- **Fallbacks.** Every rich preset is an `alternatives` pair, layered first and simple second, for Bitfocus Buttons
  and older hosts. Measure whether Buttons loads an API 2.2 module (unverified, §8.19).

### 8.2 Connection and wire

- **Discovery.** `bonjour-device` with `bonjourQueries: { node: { type: 'playmap', protocol: 'tcp' } }`, a manual
  host and port as fallback. **PlayMap must announce `_playmap._tcp`** on 9941 with TXT
  `id, name, t=playmap, v, proto, web=9940, role` (new node work).
- **Pairing.** The token lives in **secrets** (`secret-text`), never in config. Read `NONCE`, send `HELLO Companion:<label>
  module=<ver>`, prove with `AUTH-PROOF`. Use PlayMap's own token normalisation; confirm it matches Patterns'.
- **Request ids and receipts.**
  - Tag every line with a request id (`@c<n>`) and match replies by id, not order.
  - Show receipts on the key: **amber while accepted, green when applied, red on `ERR`** with the reason in
    `last_error` and `last_refusal`. Patterns' rule holds: a light is a fact from STATE, never the press.
- **Subscriptions.** `SUBSCRIBE STATE` plus `CONTEXT` (the Eye headline and items). Answer the **Action Recorder**
  with `RECORD ON`; PlayMap pushes `ACTION <line>` (new), which is recorded as a **typed** action through
  `VerbCatalog.Match`, not as a raw line.
- **Health and status.**
  - TCPHelper reconnects; a watchdog with backoff (Pixera's lesson) uses `PING` → `OK PONG t1 t2 t3`, which also
    gives the clock offset for interpolation.
  - A **stale guard**: no STATE for 3 s while a playlist plays dims every key with a "STALE" mark.
  - On a drop: empty state, `checkAllFeedbacks()`.
  - Statuses: `AuthenticationFailure` for a wrong token, `BadConfig` for a missing one, `UnknownWarning` for a newer
    node protocol.

### 8.3 The state engine

- **Diff, don't rewrite.**
  - Keep the last STATE and diff on `seq`; map changed paths to variable ids and feedback types.
  - Call `setVariableValues` with changes only, `checkFeedbacks` for the touched types only, and `checkFeedbacksById`
    for per-entity value feedbacks.
  - This beats Patterns' full rewrite and stays under Companion's 50 Hz cap.
- **Smooth clocks without traffic.** While a playlist plays, interpolate `remaining` and `elapsed` locally at
  **10 Hz** from `positionMs`, `rate` and `at`, corrected by the `PING` offset. Remaining-time keys and progress
  gauges then move smoothly between the node's 5 Hz pushes.
- **Structure versus content.**
  - A *structural* signature (entity ids and kinds, plus `project.revision` for structure only) re-sends presets and
    entity dropdowns.
  - **Renames and live values never re-send presets.** Names reach keys through variables and value feedbacks.
  - Companion ignores identical presets by checksum anyway [S16].

### 8.4 Variables (stable, sanitised ids)

- **Node:** `node_name`, `light`, `headline`, `show_mode` (ARMED or REHEARSAL), `armed_by`, `project`,
  `project_dirty`, `revision`, `faults_count`, `fault_1`–`fault_3`, `renderer`, `cpu`, `fps_min`, `dropped_total`,
  `node_protocol`, `module_behind`.
- **Air:** `blackout`, `frozen`, `pattern`, `identify`, `patterns_blackout_follow`.
- **Focused clip (Patterns names):** `video_file`, `video_tag`, `video_position`, `video_length`, `video_remaining`,
  `video_remaining_seconds`, `video_chip`, `video_text`.
- **Banks** (labels themselves; N = 8 to start):
  - `pl_<n>_name/state/item/next/remaining/progress`
  - `out_<n>_name/light/fps/dropped`
  - `pj_<n>_name/solved/residual_px/score/power/shutter/input/temp_c/lamp_h`
  - `peer_<n>_name/light/headline`
- **Calibration:** `cal_score`, `cal_grade`, `cal_words` (the self-explaining reason), `cal_worst`, `cal_moved` (which
  projector drift-watch flagged), `cal_history_n`.
- **Scan:** `scan_stage`, `scan_progress`, `scan_eta`.
- **Sync:** `sync_role`, `sync_offset_ms`, `sync_leader`.
- **Media:** `media_ready`, `media_missing`, `transfer_progress`.
- **Links:** `stagewatch_timer`, `stagewatch_phase`, `patterns_link`.
- **AI:** `ai_proposals`, `ai_top` (the top proposal's words and risk).
- **Audio:** `bpm`, `level`. **Errors:** `last_error`, `last_refusal`.
- **Entities beyond the banks** are reached through **value feedbacks by id** (§8.5), not thousands of variables.

### 8.5 Feedbacks

**Boolean feedbacks** (default styles from the shared palette):

- `blackout`, `frozen`, `show_armed`, `node_light_is`, `peer_light_is`, `link_state_is`
- `playlist_state_is`, `item_on_air`, `item_cued`
- `output_light_is`, `projector_state_is` (on, warming, shutter, fault), `surface_shown`
- `scan_running`, `calibration_grade_is`, `projector_moved`
- `fault_any`, `proposal_waiting`
- `receipt_pending` and `receipt_failed` (per control)
- **`verb_allowed`**: from STATE's `show.allowed`, which the node publishes per transport, so the module never guesses
  the role table.

All of them suit trigger conditions (`condition_true`/`condition_false`) [S10].

**Value feedbacks** (2.2 typed results), lazy because they are evaluated only while placed:

| Feedback | Result | Use |
|---|---|---|
| `label` `{ ref }` | string | live name of any entity |
| `progress` `{ playlist, item? }` | 0–1 | gauge `value` |
| `remaining` `{ playlist }` | `"0:42"` | text |
| `thumb` `{ ref, size, live }` | PNG or JPEG data URI | image `base64Image` (§8.8) |
| `cal_score` `{ projector? }` | 0–100 | ring gauge |
| `light` `{ ref }` | `'green'`, `'amber'`, `'red'`, `'grey'` | colour through `$(this:value)` |
| `temp` `{ projector }` | °C | gauge plus colour |

**Advanced feedbacks: none.** They are discouraged and likely to be removed [S18].

### 8.6 Actions

- **Typed actions from `VerbCatalog`.** Each carries its `sortName` and category, with the risk in the description:
  `PLAYLIST GO`, `CUE`, `NEXT`, `PREV`, `VIDEO PLAY`, `PAUSE`, `SEEK`, `END`, `RATE`, `BLACKOUT`, `FREEZE`,
  `PATTERN`, `OUTPUT ENABLE`, `OUTPUT SOURCE`, `IDENTIFY`, `SURFACE SOURCE`, `SURFACE SHOW`, `PROJECTOR SOLVE`,
  `BLEND SOLVE`, `WARP NODE`, `WARP RESET`, `SCAN START`, `SCAN CANCEL`, `SCAN APPLY`, `SYNC …`, `MEDIA FETCH`,
  `PROJECT SAVE` and `PROJECT OPEN`. Planned verbs follow: `SHOW ARM`/`DISARM`, `PANIC <outcome>`, `EYE …`,
  `CALIBRATION RECALL|HISTORY`, `TAP`.
- **Entity dropdowns** are filled live from STATE or `PROJECT` and accept expressions such as `$(local:item)`.
  Definitions are re-set only on structural change, batched.
- **`Any verb`.** A dropdown fed by the live `VERBS` reply, so verbs a node gains appear without a module release
  (Patterns' "menus as data" lesson).
- **Results** (`hasResult`):
  - `Ask` returns `GET <id>`, `SCAN STATUS`, `PLAYLIST GET` and similar as JSON.
  - Presets store them with `storeResult` into local variables; users store them into page or custom variables for
    Companion logic.
- **Learn:**
  - `surface_source`, `projector_*` and `item` learn from the desk's current **selection** (a new STATE `selection`
    row).
  - `any_verb` learns from the node's next `ACTION`.
- **Encoders.** Rotary actions take `delta` (default expression `$(this:delta)`).
  - Absolute verbs send STATE's value ± `delta` × step: master and surface opacity, playlist rate, effect parameter,
    blend gamma, audio-reactive gain.
  - Natively relative verbs pass `delta` straight through: `WARP NODE … dx dy` for the selected node, lens shift,
    zoom and focus through the Devices module.
  - Satellite 1.14 sends the rotation amount, so a fast turn moves faster [S14b].

### 8.7 Presets created live from the project

**Sections, each a preset group the user can tick:**

- **Show:** GO / NEXT / PANIC / BLACKOUT / FREEZE / SHOW MODE.
- **Playlists:** one **template group** per playlist.
- **Surfaces:** a template over surfaces.
- **Outputs.**
- **Projectors:** a template; power, shutter, input, identify, solve, lens.
- **Calibration and Scan.**
- **Content:** generators, effects, Particles, Fractals and Reactive parameters on encoders.
- **Fleet:** a template over peers.
- **Eye.**
- **Links:** Patterns and Stagewatch.
- **Assistant:** proposals.
- **Navigator.**
- **Encoders.**

**One definition per kind, many buttons through templates.** For example, `item_go` carries local variables:

- `item`: simple, the templated id;
- `label`, `thumb` and `progress`: feedback-based, from the value feedbacks with `ref: $(local:item)`.

The section's template group gives `templateValues = items.map(i => ({ name: i.label, value: i.id }))`.

- A placed linked button stores only `item = <id>`.
- **Renames change the label variable, not the preset.**
- Adding or removing items changes only the structure.
- A module update restyles every item key at once.

This uses Companion's own answer to "100s or 1000s of presets" [S23]. It also fixes Patterns' gap 2. The preset text
is the value feedback, so a renamed item relabels itself even on unlinked copies.

**Identity rules:**

- Preset and group ids are entity ids (`pl.<id>`, `pj.<id>`), never a name or position.
- A deleted entity's preset disappears, so linked buttons keep their last look [S16]. To make the loss visible, the
  module's value feedbacks return a **"REMOVED"** label and a grey light for an unknown id.

**What each preset carries:**

- `notes` (2.2) from the catalogue's `Words` plus the risk, so every placed key explains itself.
- `keywords` for search.
- An `alternatives` simple fallback.

**Smart keys built from Companion's internal blocks** (2.1):

- **GO-or-CUE:** `internal:logicIf` with `internal:checkExpression` on `$(playmap:show_mode) == 'ARMED'` sends GO,
  else CUE.
- **Safe identify:** `IDENTIFY ON`, `internal:wait 3000`, `IDENTIFY OFF`.

### 8.8 Thumbnails (first in the category on API 2.x)

- **Node side (new):**
  - `GET /api/thumb?ref=<id|media:sha256|output:id|projector:id|surface:id>&w=&h=&fmt=png|jpeg`, sent with the paired
    token (`X-PlayMap-Token`).
  - Responses carry `ETag` = hash of the source and parameters, and honour `If-None-Match`.
  - **Media posters are made once by the "optimise on import" step**: poster frame plus a 3-frame strip. An armed node
    never transcodes (UX §7.2).
  - Live output and projector previews come at ≤ 1 fps from the `VIEW` ring, only in rehearsal or on request.
  - A wire push `SUBSCRIBE THUMBS <refs…>` sends `THUMB <ref> <etag>` on change, so nothing polls.
- **Module side:**
  - The `thumb` value feedback returns a cached data URI at once, or `''` while it dispatches a fetch, then calls
    `checkFeedbacksById(fb.id)` [S24].
  - `unsubscribe` releases the reference. An LRU cache is keyed by ETag.
  - JPEG at q≈80 for pictures (about 5 KB at 96 px), PNG for line art.
  - Only keys that are placed are fetched, and Companion renders inactive pages lazily [S4].
  - Layered presets draw the image underneath, a progress bar, the name with an outline, and a frame whose colour
    comes from on-air or cued feedbacks.
- *(Unverified.)* Draw cost of many data-URI images on a 32-key XL is untested; size defaults to 96 px and is
  configurable 72–144.

### 8.9 Gauges, LEDs and encoders

- **Ring gauges:**
  - playlist progress;
  - calibration score, with stops red < 70, amber 70–90, green > 90 *(design thresholds)* and the grade letter in the
    middle;
  - projector temperature;
  - transfer progress;
  - scan progress.
- **On a Stream Deck Studio**, the button's first gauge appears on the encoder's LED ring (5.1) [S5][S17]. The
  composites set "External Usage" so the right gauge drives the ring.
- **On a Stream Deck +**, the four LCD-strip keys show the thumbnails of the focused playlist's current and next items.
  The dials scrub rate, opacity and effect parameters.
- **Absolute-from-STATE** for levels (Patterns' rule), so a turn never jumps and a turn while disconnected does
  nothing.

### 8.10 Show-mode safety

Mirror PlayMap's own rules [S37 UX §7]. The node stays the authority; the module only shows and asks.

- **ARMED is visible everywhere.**
  - `show_armed` puts a red frame on every PlayMap key.
  - A **lock glyph** appears on keys whose verb `verb_allowed` says the node would refuse; Companion is transport-only
    while armed.
  - Pressing a locked key does nothing locally, shows the refusal in `last_refusal` and flashes the key.
- **Risk-shaped presses**, generated from `VerbRisk`:

  | Risk | Press |
  |---|---|
  | GO, NEXT, BLACKOUT, PANIC | **instant**, never confirmed (UX §7.2) |
  | High structural verbs (PATTERN on air, OUTPUT ENABLE OFF, SCAN START, PROJECT OPEN or NEW, PAIRING NEW) | **hold 0.8 s**: a step duration group `800: { options: { runWhileHeld: true }, actions }`, with `internal:buttonPushed` turning the key amber "HOLD…" |
  | The most destructive | two steps ("SURE?" through `internal:buttonCurrentStep`, Patterns' TAKE OVER) **plus** the hold |

- **PANIC with defined outcomes.** Three presets: `PANIC HOLD` (fade and hold), `PANIC REWIND`, `PANIC NEXT`. Red,
  instant.
- **Double-GO guard** (QLab's lesson): a `go_cooldown` feedback dims GO for the node's minimum GO interval after a GO.
- **Blind edit:** `EDIT BLIND`, `COMMIT AT NEXT ITEM` (`AT`), `DISCARD`, with the diff count as a variable.
- **Locks:** lock and unlock per playlist, surface or layer, lit in the palette's `lock` colour.
- **Preflight key:** a check run with a result ring and the worst finding as words; NEXT steps through the findings.

### 8.11 Fleet, health and the Eye

- **Health view** (Pixera's lesson, Patterns' Eye as the model):
  - The **headline key** is lit by `light` and reads `headline`; a press goes to the next fault.
  - **NEXT/PREV PROBLEM**, **LENS** (video, outputs, projectors, sync, media, AI) and **RESET**, with the same `EYE`
    verbs as Patterns.
- **Fleet:** a template over `peers[]`. Each key is a node lit by its light; the text is its name and headline; the
  gauge is its sync offset.
  - *(Node work.)* A `fleet` row of aggregate health (fps, dropped, offset, media ready, armed) published by the sync
    leader, so one connection shows every machine.
  - **Controller-loss resilience** is the node's job (Watchout's lesson), but the deck shows it. If Companion loses the
    node, keys dim; the nodes keep playing.
- **Projector grid page.** One template key per projector: power, shutter, temperature ring, calibration score,
  "MOVED" flag.

### 8.12 Calibration and scan keys (the edge competitors lack on Companion)

- **SCORE.** A ring with the grade and a **one-line reason** ("P2 1.8 px at the top-left edge"): disguise's
  self-explaining score [S37 COMPETITIVE].
- **RE-SOLVE MOVED.** Drift-watch names the projector that moved, and the key reads "P3 moved — re-solve". A hold runs
  `PROJECTOR SOLVE <that one>` (Scalable's "re-solve only the projector that moved").
- **FALLBACK.** Recalls the saved good calibration (hold).
- **HISTORY.** An encoder steps through calibration snapshots, previewed on the desk only; a hold applies the one shown.
- **SCAN.** A progress gauge with the stage name; CANCEL is instant; APPLY is held.
- **Phone-scan and auto-mapping status** as a key ("local · 3 projectors · curved: OK").

### 8.13 Two-way beyond the module

1. **PlayMap drives Companion.**
   - Reuse Patterns' `CompanionSession` device profile so playlist items and Show Mode can turn Companion pages
     (`DEVICE Companion PAGE 3`), press buttons and set custom variables.
   - Companion's TCP listener is **off on fresh installs** [S12a]. Detect that and fall back to the HTTP API
     (`/api/location/…`, on by default) [S13]. Whether the HTTP API needs auth is **unverified**.
2. **One-click "Add to Companion"** from PlayMap's Network drawer, through the REST API v2 [S11]. With a user-made API
   key holding `connections` and `write`:
   - `POST /api/v2/connections/v1 { moduleId: 'jammin808-playmap', label, updatePolicy: 'stable' }` (Companion
     queues the install);
   - `PATCH { config: { host, port }, secrets: { token } }`.

   Nobody types an address or token in Companion.
3. **Dark-room sync.** When PlayMap's night-red show strip comes on, `PATCH /api/v2/surfaces/v1/{id} { brightness: 5
   }` dims every deck, and restores them after [S11].
4. **Companion buttons inside PlayMap's desk** (first in category). The Satellite **button subscription API**
   (`ADD-SUB`, `SUB-STATE`, `SUB-PRESS`) shows chosen Companion buttons as a strip in PlayMap's desk or `/remote`.
   - It is off by default on both sides, because it bypasses Companion's PIN lockout; label it as such [S14][S12a].
   - Patterns left Satellite out; PlayMap would be first.
5. **Decks on the Network drawer.** Show which decks are connected, with module version and Navigator position
   (Patterns' `CompanionWords.DecksLine`), and name the Companion heard over mDNS (`_companion-satellite-tcp._tcp`)
   [S15][S36].

### 8.14 Creative uses of 5.1

- **Page variables as "one page, any target"** [S8].
  - A *Projector page* runs on `$(page:pj)` and a *Surface page* on `$(page:surface)`; presets use those expressions
    in their options, and an encoder or key sets the page variable.
  - Twelve keys then serve 40 projectors.
  - Modules cannot create page variables, so HELP.md gives the two-click setup.
- **Button references.** GO, PANIC and BLACKOUT mirrored on every page from one source [S9].
- **Breathing decks with no traffic.**
  - Armed keys pulse with `oscillate(2000)` on opacity.
  - Faults blink with `blink(500)`.
  - Beat keys pulse with `oscillate(60000/$(playmap:bpm), 'square')`.
  - Score colours come from `colorMix('#E0342E','#1E9E5A', $(local:score)/100)` [S7].
- **Value-feedback restyling.** `light` returns the palette word, and overrides map it to colours through
  `$(this:value)` [S6].
- **Shared composites** (`tile`, `light`, `score`, `countdown`), rotatable in 5.1, which users add to their own
  Patterns or PlayMap buttons [S18][S1].
- **AI proposals as keys.** The top proposal's words in its risk colour, with APPROVE (hold) and REJECT, and a queue
  count. While armed: a queue only (UX §10).
- **Audio-reactive and generators.** The Reactive, Particles and Fractals parameters sit on Stream Deck + and Studio
  dials, with LED rings showing the level (5.1 LEDs).
- **Trigger recipes in HELP.md**, using PlayMap boolean feedbacks as conditions:
  - armed → every surface goes to the Show page;
  - projector fault → page the tech;
  - scan done → beep.

### 8.15 Work the PlayMap node must add for this module

- **Discovery:** an mDNS `_playmap._tcp` announcement.
- **Pictures:** `/api/thumb` and `SUBSCRIBE THUMBS` / `THUMB` pushes.
- **STATE rows:**
  - `show { armed, by, since, allowed[] }`;
  - `selection`;
  - `calibration { score, grade, words, worst, moved[], history }`;
  - projector device fields: power, shutter, input, temperature, lamp hours;
  - `fleet`, `audio { level, bpm }`, and a minimum GO interval.
- **Verbs:** `NAV`, `MENU …` (descriptor `protocol: 1`, as Patterns), `RECORD ON|OFF` with `ACTION` lines, `EYE …`,
  `SHOW ARM|DISARM`, `PANIC <outcome>`, `CALIBRATION …`, `TAP`.
- **Catalogue for codegen:** `VERBS` with typed parameter schemas (enums, entity kinds, ranges), so actions and options
  can be generated.
- **Role table:** a `SessionInfo.Transport` of `companion` for a `HELLO Companion:…` session, mapped to the remote
  operator role.

### 8.16 Testing on this machine (no Companion, no Stream Deck)

- **Module tests.** node:test or vitest with a fake `InstanceContext` harness, Patterns' pattern [S34]. Run every
  preset through `@companion-module/host` 1.2.1 `sanitisePresetDefinitions` and the host's `validate`. Validate the
  manifest. Build `pkg.tgz` with `companion-module-build` and open it as Companion does.
- **A fake PlayMap node in Node.js:** NONCE, HELLO, AUTH-PROOF, STATE pushes, `@id` receipts, and `THUMB`. It tests
  reconnects, the stale guard, diff rates (count `setVariableValues` calls per second under 5 Hz STATE), template
  growth to 500 items, and the thumbnail cache.
- **C# contract tests in PlayMap:**
  - every line in `lines.txt` matches `VerbCatalog`;
  - the palette is equal to Patterns';
  - the version is equal in every place it is read;
  - the `NAV` / `MENU` descriptor version equals the module's `KNOWN_PROTOCOL`.
- **Optional end to end.** The real engine headless on Xvfb :99, with the module connected in-process through the
  harness.

### 8.17 Phasing

| Phase | Contents |
|---|---|
| **P0** | Skeleton, wire (secrets, `@id`, receipts), STATE diffing into variables and boolean feedbacks, transport and Show presets, contract tests |
| **P1** | Template and linked presets for playlists, surfaces and projectors; value feedbacks (label, progress, light); Show-mode safety; Eye and fleet |
| **P2** | Thumbnails (node endpoint plus value feedback); composites; gauges and LEDs; encoders |
| **P3** | Navigator (shared `nav.js`); typed recorder and learn; calibration and scan keys; AI keys |
| **P4** | REST provisioning; Satellite strip in the desk; PlayMap driving Companion's pages; back-port of composites, templates and secrets to Patterns |

### 8.18 Comparable versus superior

| Capability | Best peer today | PlayMap target |
|---|---|---|
| State | Resolume's WebSocket subscription | Diffed STATE pushes, `@id` receipts, local 10 Hz clock interpolation |
| Live preset creation | Patterns: rebuilt per show, keyed by name | **Linked template presets keyed by id**; renames never relink |
| Pictures on keys | Resolume 64×64 advanced feedback | Value-feedback data URIs on layered keys; posters from import; pushed ETags |
| Meters | Patterns' one ring | Gauges everywhere, plus Studio LED rings |
| Safety | QLab min-GO, Patterns two-press | Armed locks shown before the press, risk-shaped presses, panic outcomes, double-GO guard |
| Health | Pixera livesystem feedback | Eye headline, fleet template, projector grid, preflight |
| Programming from the deck | Patterns' Navigator | The same Navigator, plus typed recording and learn from selection |
| Setup | mDNS pick | mDNS pick **or** one click from PlayMap through the REST API |
| Reverse direction | Patterns drives Companion's pages | Plus Companion buttons inside PlayMap (Satellite subscriptions) and dark-room brightness |

### 8.19 Risks and unknowns

- **Bitfocus Buttons and API 2.2.** v1.8 claims 2.0 and 2.1 only [S26, U]. If Buttons matters, build a 2.1 variant, or
  check that 2.2 modules load there.
- **Untested at runtime:** feedback-based local variables whose options are **expressions on another local variable**
  (`$(local:item)`) inside template presets. The types allow it [S18]; build a fake-host test, then confirm on a real
  Companion 5.1.
- **Image cost.** The redraw cost of many image elements is unmeasured.
- **Shared UDP.** Binding UDP 9928 through `createSharedUdpSocket` beside a PlayMap node on the same machine is
  untested. Prefer STATE `peers[]` for fleet discovery.
- **HTTP API auth** is unverified; the TCP API is off by default on new installs [S12a].
- **No generated `.companionconfig` files.** The export schema is untyped and versioned 6–17 [S39].
- **Store and maintenance.** Module-store submission is a process, not code. The version must move on every build
  (Patterns' rule) [S35].

## 9. What could not be verified here

- No Companion or Stream Deck was run, so Companion's behaviour comes from its source and documentation.
- The Bitfocus Buttons API claims come from a search excerpt; the page was not opened [S26].
- Companion's per-model button pixel sizes were not checked.
- Whether the old HTTP API needs auth was not checked.
- Developer-portal store listings did not render (a JavaScript page) [S38].
- WebSearch was not used; Exa was used for the vendor pages.

## 10. Sources

| Tag | Source | Date | V/U |
|---|---|---|---|
| S1 | `CHANGELOG.md`, github.com/bitfocus/companion (clone `404070f`) — https://github.com/bitfocus/companion/blob/main/CHANGELOG.md | 11 Oct 2026 | V |
| S2 | Companion releases page — https://github.com/bitfocus/companion/releases (fetched through Exa) | v5.1.0 11 Oct 2026; v5.0.0 12 Jul 2026 | V |
| S3 | `docs/user-guide/9_whatsnew/v5-1-0.md`, v4-x files (dates from `sidebar_position`) — https://companion.free/user-guide/ | 11 Oct 2026 | V |
| S4 | `docs/user-guide/9_whatsnew/v5-0-0.md` | Jul 2026 | V |
| S5 | `docs/user-guide/3_config/buttons/creating/button-styling/gauge.md`, `groups-and-references.md` | Oct 2026 | V |
| S6 | `docs/user-guide/3_config/buttons/creating/feedbacks.md` | Oct 2026 | V |
| S7 | `docs/user-guide/4_expressions/functions.md` | Oct 2026 | V |
| S8 | `docs/user-guide/3_config/variables.md`, `buttons/pages.md` | Oct 2026 | V |
| S9 | `docs/user-guide/3_config/buttons/creating/index.md` (linked presets, rotary, references); also https://companion.free/user-guide/v5.1/config/buttons/creating/ | Oct 2026 | V |
| S10 | `docs/user-guide/3_config/triggers.md`; `companion/lib/Resources/EventDefinitions.ts` | Oct 2026 | V |
| S11 | `docs/user-guide/3_config/settings.md`, `security.md`; `companion/test/Service/RestApi/contracts/openapi-stable.json`; `lib/Service/RestApi/constants.ts`, `RestApiAuth.ts` | Oct 2026 | V |
| S12 | `docs/user-guide/5_remote-control/prometheus-metrics.md` | Oct 2026 | V |
| S12a | `companion/lib/Data/UserConfig.ts` (defaults: `tcp_enabled:false`, port 16759, OSC 12321, `satellite_subscriptions_enabled:false`, `rest_api_enabled:false`, `http_api_enabled:true`) | 11 Oct 2026 | V |
| S13 | `docs/user-guide/5_remote-control/tcp-udp.md`, `http-remote-control.md`, `6_interactive-buttons/web-buttons.md`, `1_getting-started/Installation.md` | Oct 2026 | V |
| S14 | Satellite API — https://companion.free/for-developers/Satellite-API (fetched through Exa) | Oct 2026 | V |
| S14b | `companion/lib/Service/Satellite/SatelliteApi.ts` (`API_VERSION = '1.14.0'`, version history) | 11 Oct 2026 | V |
| S15 | `companion/lib/Service/MdnsAdvertise.ts` | 11 Oct 2026 | V |
| S16 | `companion/lib/Controls/ControlTypes/Button/PresetReference.ts` | 11 Oct 2026 | V |
| S17 | github.com/bitfocus/companion-surface-elgato-stream-deck (v1.5.2: `companion/HELP.md`, `src/surface-schema.ts`); companion-surface-loupedeck (1.1.1) and companion-surface-xkeys (1.0.2) `HELP.md` (raw) | 29 Sep 2026 | V |
| S18 | github.com/bitfocus/companion-module-base, `packages/base` 2.2.1 (`src/module-api/*.ts`, `helpers/tcp.ts`, `manifest-schema.ts`, `util.ts`, `CHANGELOG.md`) | 9 Oct 2026 | V |
| S19 | `npm view @companion-module/base|tools|host` (versions, publish times, bins) | 13 Oct 2026 | V |
| S20 | `packages/host` 1.2.1 (`CHANGELOG.md`, `src/instance.ts`, `src/internal/util.ts` `VARIABLE_ID_REGEX`) | 9 Oct 2026 | V |
| S21 | github.com/bitfocus/companion-module-template-ts (`package.json`, `companion/manifest.json`, `src/*`, `.github/workflows/*`) | 11 Oct 2026 | V |
| S22 | Module API Changelog — https://companion.free/for-developers/module-development/api-changes/ | Oct 2026 | V |
| S23 | Module Preset Definitions (API 2.x) — https://companion.free/for-developers/module-development/connection-basics/presets | Oct 2026 | V |
| S24 | Module Feedback Definitions — https://companion.free/for-developers/module-development/connection-basics/feedbacks | Oct 2026 | V |
| S25 | Button Graphics Elements — https://companion.free/for-developers/module-development/connection-basics/graphics-elements | Oct 2026 | V |
| S26 | Bitfocus Buttons v1.8 — https://bitfocus.io/blog/product-updates/buttons-v1-8 (search excerpt only); FAQ https://support.bitfocus.io/hc/en-us/articles/33892878535442-FAQ-Bitfocus-Buttons | 2 Oct 2026 | U |
| S27 | github.com/bitfocus/companion-module-resolume-arena (`src/websocket.ts`, `index.ts`, `image-utils.ts`, `arena-api/child-apis/ArenaClipsApi.ts`, `feedbacks/clip/feedbacks/clipInfo.ts`) | 9 Sep 2026 | V |
| S28 | github.com/bitfocus/companion-module-avstumpfl-pixera (`src/Pixera.js`, `config.js`, `feedbacks.js`) | 22 Sep 2026 | V |
| S29 | github.com/bitfocus/companion-module-figure53-qlab-advance (`qlabfb.js`, `feedbacks.js`, `presets.js`, `workspaces.js`) | 7 Oct 2026 | V |
| S30 | github.com/bitfocus/companion-module-anomes-millumin (`src/presets.ts`, `index.ts`) | 9 Sep 2026 | V |
| S31 | github.com/bitfocus/companion-module-imimot-mitti (raw `package.json`, `companion/manifest.json`, `HELP.md`, main) | Oct 2026 | V |
| S32 | github.com/bitfocus/companion-module-disguise (raw `package.json`, manifest, `HELP.md`, main) | Oct 2026 | V |
| S33 | github.com/bitfocus/companion-module-dataton-watchout (raw `package.json`, master) | Oct 2026 | V |
| S34 | `/home/user/patterns-svc/integrations/companion-module-patterns` (all source and tests, 3.18.0; repo `bcdbf28`) | 12 Oct 2026 | V |
| S35 | `/home/user/patterns-svc/docs/COMPANION.md`, `COMPANION-NAVIGATOR.md`, `EYE.md` | 12 Oct 2026 | V |
| S36 | `Patterns.Core/Services/CompanionPalette.cs`, `CompanionWords.cs`, `CompanionProfile.cs`; `tests/Patterns.Core.Tests/CompanionModuleContractTests.cs`, `CompanionPaletteTests.cs` | 12 Oct 2026 | V |
| S37 | `/home/user/mapper` (`0dd62d2`): `docs/WIRE.md`, `UX.md` §7–8, `ROADMAP.md` R3.4/R3.5/R4.9, `SHOWBUS-NODE.md`, `COMPETITIVE.md`, `src/PlayMap.Core/Wire/VerbCatalog.cs`; `/home/user/mw/stage` `VerbCatalog.cs` (VIEW), `Endpoints.cs`, `PreviewRing.cs` | 13 Oct 2026 | V |
| S38 | Getting started (connections) — https://companion.free/for-developers/module-development/home; developer portal https://developer.bitfocus.io/modules/companion-surface/discover (did not render) | Oct 2026 | V / U |
| S39 | `shared-lib/lib/Model/ExportModel.ts` (export versions 6–17; content typed `Record<string, any>`) | 11 Oct 2026 | V |
