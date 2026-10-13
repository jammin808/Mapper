# Effects: ISF v2 and FFGL 2.x hosting, effect chains

Research for the PlayMap "effects" work package. Written 13 October 2026.

**Marks.**
- **V**: verified from the primary source named (cloned repo, fetched page, or a measurement in this container).
- **V-local**: verified in this container (a build or a measurement).
- **U**: unverified (a secondary source, or a page that could not be opened).
- **E**: an engineering inference.

**Sources opened.**
- `github.com/mrRay/ISF_Spec` README. Cloned; HEAD 2025-03-20; MIT.
- `github.com/mrRay/VVISF-GL`. Cloned; HEAD 2024-12-05; BSD-3. This is the reference ISF host. It was read only, and no code was taken.
- `github.com/Vidvox/ISF-Files`. Cloned; HEAD 2026-06-04; MIT.
- `github.com/resolume/ffgl`. Cloned; HEAD 2026-09-18 "Merge pull request #109"; BSD-3, "Copyright 2023 FreeFrame".
- `docs.isf.video/ref_variables.html`, `docs.isf.video/ref_functions.html` and `isf.video/integrations/`. Fetched 2026-10-13; undated.
- `resolume.com/support/en/effects`. Fetched 2026-10-13; undated, current manual.
- `resolume.com/support/en/isf` and `resolume.com/update/releaseNotes_updates_wire.html`. Read from search extracts; Wire 7.28.1 is dated 2026-10-06.
- `docs.madmapper.com/madmapper/6/...` and `github.com/madmappersoftware/MadMapper-Materials` (`MaterialsDoc.md`, `MadMapperSurfaceFxDoc.md`). Read from search extracts.
- `help.millumin.com/docs/playback/effects/`. Fetched. `help.millumin.com/docs/tutorials/create-images-and-effects-with-shaders/`. Read from extract.
- `help.disguise.one/designer/layers/layer-types/effect/effect-layers-overview` and `.../layer-stacks`. Read from extracts.
- `help.pixera.one/timeline-/effects`. Fetched; the page footer says "PIXERA 2.0.144 | 14. August 2024". Also `help.pixera.one/en_US/glsl-effects/effect-performance-tips`, read from extract.

---

## 1. ISF: the format (spec v2)

### 1.1 The file

All facts in this section are **V** from the ISF_Spec README unless marked.

- **Format.** An ISF file is a GLSL fragment shader. It must start with a `/* … */` comment that holds a JSON dict, the "top-level dict". If the comment is missing or the JSON is malformed, the file "can't be loaded".
- **An optional vertex shader** has the same base name with `.vs`. Its `main()` must first call `isf_vertShaderInit();`.
- **Top-level keys:**
  - `ISFVSN` is a string such as "2". If it is missing, the file is v1.
  - `VSN` is the file's own version.
  - `DESCRIPTION`.
  - `CATEGORIES`, an array of strings.
  - `CREDIT`. VVISF reads it (V, `ISFDoc.cpp`); the README does not list it.
  - `INPUTS`, an ordered array.
  - `PASSES`, an array.
  - `IMPORTED`, a dict of name → `{ "PATH": "relative/to/file.png" }`.
  - `PERSISTENT_BUFFERS`. This is v1 only; v2 removed it, and pass dicts carry `PERSISTENT` instead.
- **`INPUTS[i]`:**
  - `NAME` has no whitespace and becomes the uniform name.
  - `TYPE` is one of `event`, `bool`, `long`, `float`, `point2D`, `color`, `image`, `audio` or `audioFFT`.
  - Optional keys: `LABEL`, `DEFAULT`, `MIN`, `MAX` and `IDENTITY`. A `color` input uses float arrays for its min, max and default.
  - `long` adds `VALUES` (ints) and `LABELS` (strings). This is a pop-up menu.
  - `event` is a momentary button.
  - VVISF also knows a `cube` type, a samplerCube (V, `ISFDoc.cpp`). It is not in the README.
- **Audio inputs** are passed as images:
  - `audio` has one row per channel and one column per sample, "centered around 0.5".
  - `audioFFT` has one row per channel and one column per FFT bin.
  - `MAX` on an audio input means "the number of samples the shader wants". `MAX: 1` gives a one-pixel "total volume".
  - Hosts should use 16- or 32-bit float textures where they can.
- **`PASSES[i]`:**
  - `TARGET` names a temporary buffer that later passes read by name. Temporary buffers do not survive the frame.
  - `PERSISTENT` (truthy) keeps the buffer across frames and resizes it with the render size.
  - `FLOAT` (truthy) asks for 32-bit float per channel.
  - `WIDTH` and `HEIGHT` are expression strings using `$WIDTH`, `$HEIGHT` and `$<inputName>`. They are evaluated once per frame. VVISF uses DDMathParser.
- **Conventions:**
  - A filter has an `image` input named exactly `inputImage`.
  - A transition has `startImage`, `endImage` and a normalised float `progress`.
  - Anything else is a generator. Resolume and VVISF tools classify files the same way (V, Resolume ISF page extract).

### 1.2 Built-ins

From the README and `docs.isf.video/ref_variables.html` (V):

| Name | Type | Meaning |
|---|---|---|
| `PASSINDEX` | int | 0 on the first pass, +1 per pass |
| `RENDERSIZE` | vec2 | pixel size of the *current pass* |
| `isf_FragNormCoord` | vec2 (varying) | normalised fragment coord, **[0,0] bottom-left** |
| `TIME` | float | seconds the shader has been running; updated once per frame, not per pass |
| `TIMEDELTA` | float | seconds since the last frame; 0.0 on the first frame |
| `DATE` | vec4 | (year, month, day, seconds within the day) |
| `FRAMEINDEX` | int | 0 on the first frame, +1 per frame |

Functions:
- `IMG_PIXEL(img, pixelCoord)` and `IMG_NORM_PIXEL(img, normCoord)`. Both have an optional third argument, a LOD bias (V, VVISF).
- `IMG_THIS_PIXEL(img)` and `IMG_THIS_NORM_PIXEL(img)`.
- `IMG_SIZE(img)`.

**Naming discrepancy (V).**
- `docs.isf.video/ref_functions.html` spells the normalised "this pixel" function **`IMG_NORM_THIS_PIXEL`**.
- The reference implementation VVISF replaces **`IMG_THIS_NORM_PIXEL`**.
- A host must accept both spellings.

### 1.3 How the reference host translates

VVISF-GL `ISFDoc.cpp` and `VVISF_Constants.hpp` were read for behaviour (V). They are BSD-3, but PlayMap writes its own code.

- **Translation is find-and-replace on the source text.**
  - It strips `#version` lines and inserts one for the target GL: 110, 100 (ES2), `300 es`, 330 or 400.
  - It renames `vv_FragNormCoord` → `isf_FragNormCoord` and `vv_vertShaderInit` → `isf_vertShaderInit` (v1 → v2).
  - It rewrites `IMG_*` calls with an argument counter. A wrong argument count throws a parse error with a line number.
- **GL3+ output.**
  - The fragment output is `#define gl_FragColor isf_FragColor` + `out vec4 gl_FragColor;`.
  - Every image gets three companion uniforms: `_<name>_imgRect` (vec4, in native texture coords), `_<name>_imgSize` (vec2, pixels) and `_<name>_flip` (bool).
  - The sampling helpers are `VVSAMPLER_2DBYPIXEL` and `VVSAMPLER_2DBYNORM`, plus rect-texture variants.
- **The generated vertex shader**:
  - builds an orthographic matrix from `RENDERSIZE`;
  - sets `isf_FragNormCoord = (gl_Position.xy + 1) / 2`;
  - precomputes per-image texcoord varyings for `IMG_THIS_PIXEL` / `IMG_THIS_NORM_PIXEL`.
- **ES:** it prefixes `precision highp float; precision highp int;`.
- **Uniforms in a UBO.** VVISF can optionally put all uniforms in a UBO (`inVarsAsUBO`). This is its path to Metal and Vulkan.
- **Metal.** The README says the newer **ISFMSLKit** for VDMX6 transpiles ISF to Metal and that ">99%" of tested shaders work unmodified (V, README 2025-03).

### 1.4 v1 versus v2

All V from the README:
- v2 removed `PERSISTENT_BUFFERS`; pass dicts now carry `PERSISTENT`, `FLOAT`, `WIDTH` and `HEIGHT`.
- `vv_FragNormCoord` became `isf_FragNormCoord`.
- `audio` and `audioFFT`, `IMG_SIZE`, `TIMEDELTA`, `DATE`, `FRAMEINDEX`, `VSN` and `ISFVSN` are new in v2.
- A host that wants v1 files maps `PERSISTENT_BUFFERS` (an array of names, or a dict) onto pass flags.

### 1.5 The public corpus, measured here (V-local)

`Vidvox/ISF-Files` (MIT, HEAD 2026-06-04) holds:
- **327 `.fs` and 38 `.vs` files.** All 327 headers parse as JSON.
- 323 declare `ISFVSN` 2; 4 have no version.
- By convention: **207 filters, 68 transitions, 52 generators.**
- Feature use:
  - 75 use `PASSES`;
  - 49 use `PERSISTENT`;
  - 7 use `audio` or `audioFFT`.
- Input `TYPE` counts:
  - float: 873
  - image: 358
  - bool: 137
  - color: 151
  - long: 96
  - point2D: 90
  - event: 21
  - audioFFT: 5
  - audio: 3
- Top categories:
  - Stylize: 39
  - Color Effect: 39
  - Wipe: 35
  - Utility: 31
  - Glitch: 27
  - Distortion Effect: 24
  - Retro: 23
  - Blur: 20
  - Geometry Adjustment: 20
- Licence:
  - The repository is MIT, but individual files carry their own credits: "by VIDVOX" in about 170, "by zoidberg" in 27, v002 and others.
  - **6 files reference Shadertoy or GLSL Sandbox ports.** Shadertoy's default licence is CC BY-NC-SA 3.0 (**U** for any specific shader).
  - PlayMap must not *ship* this corpus. It may use it as an **opt-in test corpus** (a path in an environment variable, like `PLAYMAP_FFMPEG_DIR`).
- Install paths the repository documents (V):
  - macOS: `/Library/Graphics/ISF` and `~/Library/Graphics/ISF`.
  - Windows: "ideally" `/ProgramData/ISF`.
  - Linux: "haven't targeted this platform yet".

**Translation probe (V-local).**
- I wrote a 230-line C# translator, PlayMap-style and our own code (`ext/isfprobe/Program.cs`). What it does:
  - parse the header;
  - declare uniforms for inputs and built-ins;
  - give every image `sampler2D` + `_imgSize` + `_imgRect` + `_flip`;
  - rewrite the `IMG_*` calls with balanced-parenthesis argument parsing;
  - `#define` `gl_FragColor`, `texture2D` and `varying`;
  - supply a full-screen-triangle vertex shader built from `gl_VertexID` that calls `isf_vertShaderInit()`.
- It translated the whole corpus to **GLSL 330 core** and **GLSL ES 3.00**. The ES shaders were compiled in the same 4.5 core context through `ARB_ES3_compatibility`, which Mesa enforces with ES rules.
- Platform: Mesa 25.2.8 llvmpipe (LLVM 20.1.2), GL 4.5 core on Xvfb.

| Target | Compile + link OK | Notes |
|---|---|---|
| GLSL 330 core | **324 / 327 (99.1 %)** | The 3 failures are shader bugs Mesa rejects: an `int` returned from a `float` function, an int/float relational, and an `int` initialiser for a `uint` |
| GLSL ES 3.00 (plain) | **298 / 327 (91.1 %)** | See the reasons below |
| GLSL ES 3.00 + builtin-rename fix-up | **305 / 327 (93.3 %)** | A user function named like a builtin (`round`, `mod`…) is renamed with `#define` |

ES-only failure reasons, from the first error line:
- 10 "A shader cannot redefine or overload built-in function" (the fix-up cures them);
- 10 "initializer of global variable must be a constant expression";
- 4 implicit int↔float conversions;
- 1 operand-type mismatch.

Two more measurements:
- **The first translator draft failed every file** because it emitted a `texture(s, uv, bias)` helper into the *vertex* shader. Implicit-LOD bias is fragment-only. A real host must emit the bias overload only into the fragment stage.
- **Compile + link time on llvmpipe:** median 3.6 ms, p95 18.8 ms and max 56 ms (one 330 ms outlier on the very first, cold run). With Mesa's on-disk shader cache warm: median 0.8 ms, p95 3.5 ms.
- Mesa exposes `GL_KHR_parallel_shader_compile`, `GL_ARB_get_program_binary`, `GL_ARB_ES3_compatibility` and `GL_ARB_clip_control` (V-local, glxinfo).

### 1.6 How other tools host ISF

- **VDMX6.** ISFMSLKit on Metal (V, ISF_Spec README).
- **MadMapper 6.** "Materials" and "Surface FX" are *based on* ISF but **are not standard ISF** (V):
  - **Their own boilerplate.** You implement `vec4 materialColorForPixel(vec2 texCoord)` or `fxColorForPixel` instead of `main`, because MadMapper inlines materials and FX into its surface shader.
  - **No multi-pass.** "MadMapper Materials don't support multi-pass ISF (because… dozens of video surface… huge performance issues)". The same holds for Surface FX.
  - **`RENDERSIZE`** is valid only when "Render to Texture" is on.
  - **Extensions:**
    - `GENERATORS`: `time_base`, `animator`, `damper`, `adsr`, `linear_filter`, multiplier, incrementer and `pass_thru`. These are CPU-side filtered uniforms. `time_base` "solves the TIME problem": `TIME*speed` jumps when the speed slider moves, and MadMapper integrates the speed instead.
    - `audioFFT` / `audio` with `SIZE`, `ATTACK`, `DECAY` and `RELEASE` smoothing.
    - `IMPORTED` with cube and 3D textures, plus filter and wrap settings.
    - `FLAGS` (`button`, `no_alpha`, `generate_as_define`).
    - `LABEL` "Group/Name" for grouping.
    - Variables named `mat_*` or `fx_*` to avoid clashes.
    - Surface-type defines (`SURFACE_IS_quad`, `SURFACE_IS_surface3D`, `SURFACE_IS_fixture`).
    - `FX_NORM_PIXEL`, `IMG_NORM_PIXEL_LOD` and `IMG_TEXEL_FETCH`.
  - **Libraries:** bundled MadCommon, MadNoise and MadSDF (by Simon Geilfus).
  - **Separate FX families:** Surface 2D FX, Surface 3D FX (with lights, positions) and Line FX.
  - **AI:** a "MadAI" assistant generates Materials and 2D Surface FX from prompts, and there is a Public Material Library (V, docs.madmapper.com v6 pages).
- **Millumin.** ISF "medias" (generators) and ISF effects (V, help.millumin.com):
  - User folders are `~/Library/Millumin/ISF-effects` and `ISF-source`.
  - Shader medias have **size, speed and quality** (from "highest" to "draft", a render-scale control).
  - Effect "image" inputs list only image files and live inputs, **not other layers**. A forum answer from April 2025 says layers are "not planned" (V, forum.millumin.com/discussion/2355). A user who wanted a matte from another layer was told to use a two-layer workaround.
- **Resolume.**
  - Wire, the node patcher, has an ISF node. "Wire currently (version 7.22) does not support vertex shaders" (V, resolume.com/support/en/isf extract).
  - Wire 7.28.1 (2026-10-06) fixed "Edit ISF is disabled on Windows" (V, Wire release notes).
  - A third-party guide says Arena/Avenue 7.8+ load ISF natively from `Sources/` and `Effects/` folders (**U**).
  - Resolume 7.28 added computer-vision effects: Background Removal, Depth Estimation, Depth Blur, Depth Displace and a Depth blend mode (V, Wire 7.28 notes).
- **TouchDesigner.** Partial, through the community "TouchDesigner-ISF-parser". isf.video marks it partial (V, integrations page).
- **Other hosts** listed by isf.video (V): CoGe, EboSuite, Magic Music Visuals, Modulo Player, Motion/FCP X, Smode, Synesthesia, Videosync, Max (`jit.gl.isf`), openFrameworks, Vuo and Fugio. Libraries: VVISF-GL, ISF-JS-Renderer and Rust ISF.

---

## 2. FFGL 2.x: the ABI

All V from `resolume/ffgl` master (2026-09-18) unless marked.

### 2.1 Versions and requirements

- The repository is "the Resolume fork… 64 bit plugins that can be loaded by Resolume 7.0.3 and up". The latest *stable* SDK is **FFGL 2.2**. Master works with Resolume 7.3.1 and up.
- The changelog in `FFGL.h`:
  - **2.0** removed CPU processing. It completed `FF_TYPE_OPTION` (dropdowns) and `FF_TYPE_BUFFER` (host-filled buffers, for example FFT).
  - **2.1** added thumbnails, RAII bindings and file parameters.
  - **2.2** added:
    - context-state validation in debug builds;
    - parameter groups;
    - **top-left texture orientation** (`FF_CAP_TOP_LEFT_TEXTURE_ORIENTATION = 16`; "hosts rendering top-left currently need to flip both inputs and the output every frame");
    - a host log hook (`SetLogCallback`).
  - **2.3** added dynamic display names.
  - Master also "replaced glload by glew, enabling OpenGL 4.6 extensions" and added value-change events and dynamic option elements (Resolume 7.4.0 / 7.4.1).
- **GL version.**
  - `FFGL.cpp`: "Due to the move to OpenGL 4.1 the host no longer has [the] ability" to isolate plugin state.
  - The samples' shaders are `#version 410 core`.
  - **The host must provide a desktop GL ≥ 4.1 core context.**
  - Plugins include `<OpenGL/gl3.h>` on macOS and GLEW elsewhere.
- **The licence** is BSD-3 (`LICENSE.md`, "Copyright 2023 FreeFrame"). Headers may be reimplemented or transcribed into C# with attribution.

### 2.2 The single entry point

```c
// Windows: extern "C" __declspec(dllexport) FFMixed __stdcall plugMain(FFUInt32, FFMixed, FFInstanceID);
// macOS/Linux: FFMixed plugMain(FFUInt32 functionCode, FFMixed inputValue, FFInstanceID instanceID);  (extern "C")
typedef union FFMixed { FFUInt32 UIntValue; void* PointerValue; } FFMixed;   // 8 bytes on x64
// also exported: void SetLogCallback(PFNLog)   (PFNLog = void(*)(char*))
```

In .NET this is `delegate* unmanaged[Stdcall]<uint, nint, nint, nint>` on Windows, where x64 stdcall equals the default convention, and `[Cdecl]` on Linux and macOS (E).

**Function codes.** These are the exact values in `FFGL.h`.
- Lifecycle:
  - `FF_GET_INFO = 0` returns a `PluginInfoStruct*`: API major/minor, a 4-char `PluginUniqueID`, a 16-char `PluginName` (neither NUL-terminated) and `PluginType` (`FF_EFFECT 0`, `FF_SOURCE 1`, `FF_MIXER 2`).
  - `FF_INITIALISE_V2 = 34` and `FF_DEINITIALISE = 2` (per DLL).
  - `FF_INSTANTIATE_GL = 18` takes `FFGLViewportStruct* {x,y,width,height}` and returns the instance pointer. `FF_DEINSTANTIATE_GL = 19`.
  - `FF_RESIZE = 23` takes a viewport.
  - `FF_CONNECT = 21` and `FF_DISCONNECT = 22`. The SDK calls these itself on the first `FF_PROCESS_OPENGL`.
- Info:
  - `FF_GET_EXTENDED_INFO = 13`, `FF_GET_PLUGIN_SHORT_NAME = 33`.
  - `FF_GET_PLUGIN_CAPS = 10` with `FF_CAP_SET_TIME 5`, `FF_CAP_MINIMUM_INPUT_FRAMES 10`, `FF_CAP_MAXIMUM_INPUT_FRAMES 11` and `FF_CAP_TOP_LEFT_TEXTURE_ORIENTATION 16`.
  - `FF_ENABLE_PLUGIN_CAP = 49`.
- Parameters:
  - `FF_GET_NUM_PARAMETERS 4`, `…_NAME 5`, `…_DEFAULT 6`, `…_DISPLAY 7`, `FF_SET_PARAMETER 8` (`SetParameterStruct{ParameterNumber, FFMixed NewParameterValue}`; floats travel as their bits in `UIntValue`, text as a pointer), `FF_GET_PARAMETER 9`.
  - `FF_GET_PARAMETER_TYPE 15`, `FF_GET_RANGE 41` (`GetRangeStruct{parameterNumber, {min,max}}`), `FF_GET_PARAM_GROUP 50`, `FF_GET_PARAM_DISPLAY_NAME 51`.
  - `FF_GET_PARAMETER_USAGE 32` (`FF_USAGE_STANDARD 0`, `FF_USAGE_FFT 1`).
  - Option and buffer elements: `FF_GET_NUM_PARAMETER_ELEMENTS 31`, `…_ELEMENT_NAME 35`, `…_ELEMENT_VALUE 36`, `FF_SET_PARAMETER_ELEMENT_VALUE 37`, `FF_GET_NUM_ELEMENT_SEPARATORS 47`, `FF_GET_SEPARATOR_ELEMENT_INDEX 48`.
  - Files: `FF_GET_NUM_FILE_PARAMETER_EXTENSIONS 43`, `FF_GET_FILE_PARAMETER_EXTENSION 44`.
  - `FF_GET_PRAMETER_VISIBILITY 45` (sic).
  - `FF_GET_PARAMETER_EVENTS 46` (`GetParamEventsStruct{numEvents, ParamEventStruct*}`; the flags are `VISIBILITY 0x01`, `DISPLAY_NAME 0x02`, `VALUE 0x04` and `ELEMENTS 0x08`).
- Per frame:
  - `FF_SET_TIME = 20` takes a `double*` in seconds.
  - `FF_SET_BEATINFO = 38` takes `{float bpm, float barPhase}`.
  - `FF_PROCESS_OPENGL = 17` takes a `ProcessOpenGLStruct{numInputTextures, FFGLTextureStruct** inputTextures, GLuint HostFBO}`. Each texture is `{Width, Height, HardwareWidth, HardwareHeight, GLuint Handle}`.
  - `FF_GET_INPUT_STATUS = 16`.
- Host info:
  - `FF_SET_HOSTINFO = 39` takes `{const char* name, const char* version}`.
  - `FF_SET_SAMPLERATE = 40`.
  - `FF_GET_THUMBNAIL = 42` takes `GetThumbnailStruct{width, height, rgbaPixelBuffer}`. Call it twice: once for the size, once for the pixels.
- `FF_SUCCESS 0`, `FF_FAIL 0xFFFFFFFF`.

**Parameter types.**
- `BOOLEAN 0`, `EVENT 1`
- `RED 2`, `GREEN 3`, `BLUE 4`
- `XPOS 5`, `YPOS 6`
- `STANDARD 10` (a 0..1 float, display range from `FF_GET_RANGE`)
- `OPTION 11`
- `BUFFER 12`
- `INTEGER 13`
- `FILE 14`
- `TEXT 100`
- `HUE 200`, `SATURATION 201`, `BRIGHTNESS 202`, `ALPHA 203`

**FFT buffers.**
- The quickstart `ParamFFT` default size is **2048** elements.
- The host fills it one element at a time with `FF_SET_PARAMETER_ELEMENT_VALUE` (`SetParameterElementValueStruct{ParameterNumber, ElementNumber, FFMixed value}`).

### 2.3 The host contract (the GL state)

- `ValidateContextState()` in `FFGL.cpp` runs in debug builds before and after `INSTANTIATE_GL`, `PROCESS_OPENGL` and `DEINSTANTIATE_GL`. It defines "the context default state" the host must hand over:
  - **Bindings:**
    - program 0;
    - `GL_ACTIVE_TEXTURE == GL_TEXTURE0`;
    - **no texture bound on any unit for any target** (1D, 2D, 3D, arrays, rect, cube, cube-array, buffer, 2D-MS, 2D-MS-array);
    - array, draw-indirect, pixel-pack, pixel-unpack, transform-feedback, element, uniform-buffer and VAO bindings all 0.
  - **Render state:**
    - `GL_POLYGON_MODE == GL_FILL`;
    - cull off; front face CCW;
    - blend off with equation ADD and factors ONE/ZERO;
    - depth write mask TRUE; depth test off; depth func LESS;
    - colour mask all TRUE.
- The plugin must return the context in that state. The SDK's scoped bindings (`ScopedShaderBinding`, `ScopedTextureBinding`…) exist for this. Third-party plugins built in release mode are **not** checked (E).
- **The output** goes into the FBO the host bound. The host passes its handle as `HostFBO` so a plugin that binds its own FBOs can restore it.
- **Threading (E, from the code):**
  - Every call needs the plugin's GL context current on the calling thread.
  - `s_pPrototype` and `FF_INITIALISE_V2` are per-DLL globals, so instances of one DLL share static state. Load each DLL once per process.
  - The SDK has no locking.
- **The SDK's `InitGLExts()`** calls `glewInit()` only on Windows. On Linux and macOS it returns true without initialising anything.

### 2.4 A Linux build of an SDK sample (V-local)

- I built `source/plugins/AddSubtract` (an FF_EFFECT) and `Gradients` (an FF_SOURCE) with the SDK's own sources and its bundled GLEW 2.1.0 headers:
  `g++ -std=c++17 -O2 -fPIC -shared -DGLEW_NO_GLU -I source/lib -I deps/glew-2.1.0/include source/lib/FFGLSDK.cpp source/plugins/AddSubtract/AddSubtract.cpp -o libAddSubtract.so`
  - Compiler: g++ 13.3.
  - Time: 8.3 s each.
  - The SDK's CMake builds plugins as *static* libraries and needs `find_package(GLEW)`. GLEW is not installed here, so I compiled the same sources directly.
- Result: a 275 KB `.so` that exports `plugMain` and `SetLogCallback` with C linkage.
- It has **40 undefined GLEW function-pointer data symbols**: `__glewActiveTexture`, `__glewBindFramebuffer`, `__glewCreateProgram`, `__glewBindVertexArray`, `__glewUniform1f`, `__glewTransformFeedbackVaryings` and others.
- It has 8 undefined GL 1.1 functions: `glBindTexture`, `glDeleteTextures`, `glDrawArrays`, `glGenTextures`, `glGetIntegerv`, `glTexImage2D`, `glTexParameteri` and `glViewport`.
- It has no `DT_NEEDED` on libGL or libGLEW.
- **What this means (E):**
  - Data relocations are resolved at `dlopen` time, even with `RTLD_LAZY`. A Linux host must therefore make the `__glew*` symbols resolvable before it loads a plugin. There are two ways:
    - (a) `dlopen("libGLEW.so.2.x", RTLD_GLOBAL)`, then call `glewExperimental = GL_TRUE; glewInit()` with the context current. This needs the user's libGLEW (Ubuntu `libglew2.2`, not present here).
    - (b) PlayMap ships a tiny MIT C shim (`libplaymap_ffglshim.so`). It defines the `__glew*` pointers (the ~250 GL 2.0–4.6 entry points GLEW declares) and fills them from `glXGetProcAddressARB` or `eglGetProcAddress` when the host calls `pm_ffgl_shim_init()`. Load it `RTLD_GLOBAL` before the plugin, and load `libGL.so.1` `RTLD_GLOBAL` for the 1.1 functions.
  - Option (b) makes **host tests on Linux with the SDK's own samples feasible on Mesa llvmpipe** (GL 4.5 core here). No sample was run in this session; I did build only.
  - FFGL plugins in the wild are Windows `.dll` and macOS `.bundle`. Linux FFGL is a test vehicle and a niche (E).

### 2.5 FFGL in a D3D11 host

From `present.md` §4, V there.
- `WGL_NV_DX_interop2` registers D3D11 textures as GL textures (`wglDXOpenDeviceNV`, `wglDXRegisterObjectNV`, `wglDXLockObjectsNV`/`Unlock`).
- Lock and unlock cost and vendor variance are documented. Godot measured the lock as the main culprit.
- The round-2 present path is ANGLE on PlayMap's own D3D11 device. FFGL needs desktop GL 4.1+, so FFGL lives on a **GL island**: a WGL context whose input and output textures are D3D11 textures shared through interop. This is Spout's mechanism (`SpoutGL` `IsGLDXavailable`, `CreateInterop`, `LockInteropObject`).
- A D3D11 **shared NT handle** (`IDXGIResource1::CreateSharedHandle`) also lets that island live in **another process** (E).
- On Linux, Mesa llvmpipe exposes `GL_EXT_memory_object_fd` but not `GL_EXT_semaphore_fd`, and EGL lacks `EGL_MESA_image_dma_buf_export` (V-local, glxinfo/eglinfo). A cross-process GPU share cannot be proven on this box. A shared-memory CPU ring is the testable stand-in (E).

---

## 3. Effect chains in the products PlayMap meets

| Product | Where effects attach | Mix and bypass | Presets | Animation | Formats |
|---|---|---|---|---|---|
| **Resolume Arena/Avenue 7** (V, resolume.com/support/en/effects) | Clip, layer, group, composition. Clip effects run before layer/group effects, which run before composition effects. An **effect clip** is an adjustment layer for the layers below. **Transform is itself an effect** and can be placed anywhere in the stack | Every video effect has **Opacity and Blend Mode** (dry/wet). A **B** bypass toggle, itself animatable (right-click → animation). Alt-drop adds at opacity 0 | Per-effect presets in the effects list. Presets are applied once ("the octopus… swims away") and not linked. Copy Effects between targets. Rename and recolour instances. Favourites. Quick search to add. Double-click to preview on the Preview output | Dashboard, BPM sync, envelopes, audio analysis and timeline on any parameter (V, manual TOC "Parameter Animation: Timeline, BPM Sync, Dashboard, Clip Position, Analysis, Envelope") | Built-in (>100), FFGL from an "Extra Effects" folder, VST audio, Wire patches, ISF via Wire (V); ISF in Arena natively (U) |
| **MadMapper 6** (V, docs.madmapper.com) | Materials (generators) on surfaces. **Surface FX** per surface type (2D / 3D / Line) | Not documented in the sources I could read (U) | Public Material Library; MadAI generation | `GENERATORS` (time_base, damper, adsr…) inside the shader file. Audio FFT with ADR smoothing | ISF-derived dialect (not standard ISF: no multi-pass, own entry function) |
| **Millumin** (V, help.millumin.com) | A layer or a media; groups have effects too | An `enabled` checkbox is always the first property, animatable per column | Copy/paste "Effects Only" | Timeline and per-column property animation | Built-in (colouring, standard, special trackers), ISF. No FFGL mentioned (U) |
| **disguise Designer** (V, help.disguise.one) | **Effect layers** in the timeline stack (Blur, ChannelRouter, ColourAdjust, Compose, EdgeFilter, Kaleidoscope, Lut, MotionBlur, Noise, OCIO, PixelMap, Pixelate, Scroll, SpinBitmap, UVLookup, VideoTransition…). Sources are "piped" by **layer arrows** or taken via "Source Texture from BG". **Layer Stacks** (r20+) attach a time-independent stack to a video input | Blend modes and brightness on effect layers | (not covered) | Keyframed timeline parameters | Native, Notch blocks (DFX) as Notch layers |
| **Pixera 2.0.144** (V, help.pixera.one, 2024-08-14) | Effects dropped on a **timeline layer** and stacked. "Layer Mapping effects" change how a layer meets the screen object. Heavy effects advised on a **Layer Reference** resource at a lower resolution | (not covered) | **MultiFX**: effects with 4, 8 or 14 parameters behind fixed **DMX IDs** (16-bit since 2.0.14x; IDs 0–99 for custom effects) | Timeline keyframes; DMX | GLSL fragment shaders (Pixera's own format) and compiled DFX (Notch) DLLs |

**Performance practice (V, Pixera "Effect Performance Tips").**
- Blurs are called the heaviest effect. Pixera's advice:
  - bake static effects into the content;
  - put live heavy effects on a reduced-resolution layer reference;
  - watch GPU usage above 80 %.
- Millumin exposes a per-shader quality (render scale).
- MadMapper refuses multi-pass in inline FX for performance. It offers "Render to Texture" for heavy materials and a `REQUIRES_TEXTURE` flag that warns when a many-tap FX meets an inline material.

**What nobody in this table documents (E, from the pages above):**
- per-effect GPU cost shown to the operator;
- crash isolation of native plugins;
- deterministic feedback effects across machines;
- geometry-aware ISF (world position or normal) on projection surfaces in a *standard* file format;
- effects whose parameters are reachable by one verb grammar shared with an AI and a Stream Deck.

---

## 4. Design implications for PlayMap

1. **ISF v2 is the effect file format, and PlayMap hosts it as standard ISF.**
   - Multi-pass, persistent, float and imported images are all in, unlike MadMapper.
   - Accept both `IMG_THIS_NORM_PIXEL` and `IMG_NORM_THIS_PIXEL`.
   - Map v1 `PERSISTENT_BUFFERS` onto pass flags.
   - Measured bar: **≥ 99 % of the public corpus compiles on desktop GL 330** with a small managed translator (324/327 here).
2. **ANGLE (GLSL ES 3.00) is the compatibility risk, about 7–9 points lower.**
   - Text fix-ups get 93 %.
   - The rest needs global-initialiser hoisting and implicit-conversion repair. Two ways to get it:
     - a tokenising rewriter in Core, which is testable here against Mesa's strict ES compiler; or
     - glslang → SPIR-V → SPIRV-Cross ES at import time (native, permissive licences, Windows build only).
   - Translate once at **import** ("optimise on import" for effects). Cache the dialect sources by SHA-256.
3. **TIME must be deterministic and precise.**
   - `TIME = (leaderNs − originNs) / 1e9` sent as float32, with `originNs` stamped into the model, so every node computes the same frame.
   - `TIMEDELTA` = the show frame period (not a measured delta).
   - `FRAMEINDEX` = the show frame since origin.
   - `DATE` = from the show wall clock at the leader instant.
   - Warn when TIME passes 65 536 s (float32 step ≈ 8 ms).
   - Leader time is the leader's monotonic clock, so an origin is required.
   - **The existing generators pass `(float)frame.Seconds` of that clock.** That is a latent precision bug after days of uptime; fix it with the same origin rule.
4. **Persistent (feedback) buffers break frame identity across nodes.**
   - Pre-roll K frames on (re)start so a late node converges.
   - Badge them "history-dependent" in STATE and the desk, using the backup-mirror vocabulary of COMPETITIVE.md §5c.
5. **Images are 2D textures, top-row-first in PlayMap; ISF is bottom-left.**
   - Carry an orientation bit per texture (`_flip`).
   - Fold the final flip into the consumer's UV rectangle, so no extra pass is spent.
   - The same applies to FFGL: ask for `FF_CAP_TOP_LEFT_TEXTURE_ORIENTATION` and avoid flips entirely when granted.
6. **Beat Millumin's input limit.** An ISF `image` input other than `inputImage` may name **any PlayMap source** (`canvas:…`, `media:…`, `live:…`, another layer's output). This gives dynamic mattes and displacement maps from other content. Recursion is refused as canvases already do.
7. **Beat MadMapper's geometry FX in a standard way.**
   - Reserved image inputs `PM_POSITION`, `PM_NORMAL` and `PM_EDGE` (a UV-space G-buffer per surface, baked when the mesh changes) are bound automatically when an ISF effect sits on a surface.
   - The file stays valid ISF everywhere; another host just shows extra image inputs.
8. **Beat MadMapper's `time_base` without breaking determinism.** Parameter changes that would make `TIME*speed` jump become anchored phase segments, `phase(t) = phase0 + speed·(t − t0)`, re-anchored at each edit. This keeps the result a pure function of show time.
9. **FFGL 2.x host.**
   - desktop GL ≥ 4.1 core;
   - the full default-state contract around every call, then `GlDevice.InvalidateState()`;
   - `FF_SET_TIME(&leaderSeconds)`;
   - FFT buffers from the audio analysis;
   - `FF_SET_BEATINFO` from the tempo source;
   - `FF_SET_HOSTINFO("PlayMap", version)`;
   - thumbnails into the library;
   - plugin-originated events reflected in STATE as *observed* values, never as project edits (one writer).
10. **Crash-isolate native code.**
    - **Probe** every plugin DLL in a short-lived child process (scan crash = quarantined entry, not a failed start).
    - Run untrusted plugins in an **fx-host process** with GPU texture sharing: D3D11 shared handles + `WGL_NV_DX_interop2` on Windows; a shared-memory ring on Linux for tests.
    - When the host dies, the slot passes its input through and the air survives.
    - Trusted plugins may run in-process. This is the place to beat Resolume, which scans and runs plugins in-process.
11. **Linux FFGL testing is feasible** with the SDK's own samples and a PlayMap GLEW shim (§2.4).
12. **The effect chain model matches the leaders:**
    - chains on layers, canvases, surfaces, outputs and playlist items (Resolume's clip/layer/group/composition);
    - per-effect **mix (dry/wet) and blend**;
    - **bypass**, animatable;
    - **presets** applied once and unlinked, plus optional *linked looks* (beyond Resolume);
    - **copy/paste chains**;
    - an effect-only playlist item = an **adjustment item** (Resolume's effect clip);
    - **per-effect render scale** (Millumin quality, Pixera layer-reference advice);
    - **per-effect GPU ms in STATE** (nobody shows this);
    - a fixed-ID **DMX/Art-Net personality per chain** (Pixera MultiFX), through the control package.
13. **Licensing.**
    - Ship only PlayMap-written shaders.
    - The public corpus is an opt-in test fixture (`PLAYMAP_ISF_CORPUS`).
    - FFGL headers are BSD-3 and are transcribed with attribution in `THIRD-PARTY-NOTICES`.
    - Plugins bought for Resolume may carry host-specific licences. PlayMap never auto-scans Resolume's "Extra Effects" folder; the user adds folders.
14. **Compile cost.**
    - Measured 0.8–19 ms on llvmpipe; driver compiles on discrete GPUs can be longer (U).
    - Compile on a shared-context worker with `KHR_parallel_shader_compile` where present, and cache program binaries (`ARB_get_program_binary`).
    - An effect that is not ready passes through and says "compiling" in STATE. A frame never waits on a compile.
