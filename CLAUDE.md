# PlayMap — the agent brief

Read this before any change. The papers people read are `README.md`, `docs/`; this is for whoever works the tree.

## What this is

PlayMap (repo: Mapper) is one portable .NET 10 exe with two processes: the **desk** (`PlayMap`, Avalonia 12) and
the **engine** (`PlayMap engine`, the node: project, playback, FFmpeg decode, OpenGL render, outputs, Show Bus,
scan). It is a media player / render node of the Patterns ecosystem (Patterns, Stagewatch) and a 3D
projection-mapping suite (structured-light scanning, LiDAR import, pose solving, 3D blending, pen-tool masks).
`docs/ARCHITECTURE.md` is the design; read it first.

## The standing brief

Stability, resilience, efficiency, user experience, modularity, performance across machines, rapid show
construction. Every selection change or view update is instant. Simplicity hides power: few workspaces, the
right-click menu on everything, in step with the desk's buttons and the wire's verbs. AI integration at every step
(STATE, CONTEXT, MCP). Anything done is followed down every chain it affects — Core, engine, wire, STATE, context,
MCP, desk, right-click menus, papers.

## The doctrine (Patterns')

- **Attempts are not facts.** Sent → accepted → applied → observed. A verb that would change nothing is refused
  with the reason. Tests assert fields and published state, never words alone.
- **Content is a pure function of show time.** No node integrates frame deltas; frame = f(leader time at vblank).
- **One writer.** The engine's `ProjectStore` is the only writer of the project; readers take immutable snapshots.
- **The desk has no private API.** It uses the verbs Patterns and an AI use.
- **Fail closed on the wire.** Unknown words are refused; secrets ride in no line, no journal row, no hello.
- **Measured, then governed.** No governor without a measured cause and a deterministic test.

## Constraints that bite

- **One `dotnet build` at a time** in a tree. A `--no-build` test run is safe beside edits.
- **The analyzers are fences** (`.editorconfig`, `BannedSymbols.txt`, inherited from Patterns) and apply to tests:
  no empty catch, no dead stores, no `System.Random` (use `Pcg32`), culture-invariant parsing/formatting,
  `Thread.Sleep`/`Environment.TickCount`/`DateTime.Now`/`GC.Collect`/`Console.Write`/`Task.Wait` banned in `src/`.
  Structs implement equality (use `record struct`). Dispose what you own.
- **Comments state the invariant**, not the history. No model or tool identifiers in anything pushed.
- **Never** skip, disable or quarantine a test to get green; never rewrite pushed history.
- Module rules: Core references only the runtime; edges reference Core and their own native library; only App
  references Avalonia.

## The loop

```
dotnet build PlayMap.sln -c Debug -v q
dotnet test tests/PlayMap.Core.Tests -c Debug --no-build
# GL tests need a display: Xvfb :99 & export DISPLAY=:99 (Mesa llvmpipe is enough)
# FFmpeg tests need FFmpeg 9.0 shared libraries: PLAYMAP_FFMPEG_DIR=/path/to/lib
```

## Where things live

| What | Where |
|---|---|
| The design | `docs/ARCHITECTURE.md` |
| The wire, STATE, the verbs | `docs/WIRE.md`, `src/PlayMap.Core/Wire/VerbCatalog.cs` (the one table) |
| The Show Bus node (Patterns/Stagewatch interop) | `docs/SHOWBUS-NODE.md` |
| Mapping and scanning | `docs/MAPPING.md` |
| Playback, sync, media distribution | `docs/PLAYBACK.md` |
| Research behind the choices | `docs/RESEARCH.md` |
| What is built, what is next | `docs/ROADMAP.md` |

## House words

**the air** — what the audience sees; **a lane** — a single-writer path; **a seam** — an interface where a platform
or heavy implementation is swapped; **STATE** — the node's JSON; **the context** — Show Bus §6; **a surface** — a
mappable mesh face set with content; **pins** — 3D↔2D calibration pairs; **the smart scan** — the camera image
seen from the projector's point of view.
