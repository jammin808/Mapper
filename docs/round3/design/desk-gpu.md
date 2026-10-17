# Design: `wp3-desk-gpu` — previews without a CPU copy

*How a preview will travel from the engine's GPU to the desk's compositor as a shared texture with a fence, instead of four copies
through the CPU; what keeps the engine from ever waiting on the desk; when the desk falls back to the ring; and the test that a desk
killed mid-import leaves the air untouched. Package section: `PLAN.md` §3.12. Today's path is the map's §2.15.*

**The decision in one paragraph.** For a local desk whose compositor shares the engine's adapter, the engine will draw each preview
view at its displayed size into three shared textures with two shared fences — one the engine signals when a slot is drawn, one the
desk signals when its compositor is done reading — and duplicate their NT handles into the desk's process on `VIEW … SHARED <pid>`.
The ring file stays, small, as the view's control block, so the desk learns the newest slot and its fence value without a line.
The desk imports each slot once through Avalonia's GPU interop and composes it with a wait on the engine's fence and a signal on its
own. The engine only ever reads the desk's fence without waiting: a slot not yet released is skipped, as the ring skips today. Every
other case — a remote node, the GL backend, another adapter, an interop that lists no D3D12 fence — keeps today's ring, and says so
in the preview's corner.

---

## 1. Today, and the bar

A preview goes GPU → pixel-pack buffer → shared-memory ring (three BGRA slots, a seqlock each, a stamp) → `byte[]` →
`WriteableBitmap` → Avalonia's upload: four copies. The caps keep it bounded — an output view is a blit at most ten times a second,
an editor or projector view a stage pass at most thirty, fitted inside 1920 × 1200 [map §2.15] — so a 1920 × 1200 stage view at
30 Hz moves about 1.1 GB/s through each copy. The desk has no GPU interop at all.

| Must | Proved by |
|---|---|
| No CPU copy of a shared preview on either side | `PreviewShareTests` count no map, no readback, no `byte[]` |
| The engine never waits on the desk | `DeskKillTests` (counted: no render-thread wait on the desk's fence); the engine's fence reads are `GetCompletedValue` only |
| The ring stays correct for every case the shared path does not take | the ring's existing tests, unchanged; `GpuPreviewSourceTests` for each fallback |
| Handles reach only the desk that asked, on this machine, as this user | `PreviewShareTests`' refusals |

## 2. The engine's side

- **Slots.** Per shared view, three `BGRA8` textures at the view's displayed size, created with `D3D12_HEAP_FLAG_SHARED` by the RHI's
  exporter (wp3-d3d12-core's `Share/`, merged in W2; this package adds the duplication into another process), and two fences
  created shared: `ready` (the engine signals value *v* when slot *v mod 3* is drawn) and `released` (the
  desk signals *v* when its compositor has finished with that slot).
- **The draw.** `RenderEngine.Preview` draws the view into the next slot whose last value the `released` fence has reached
  (`GetCompletedValue`, never a wait); if none has, the view skips this frame, counted, as the ring's writer skips a slot today.
  After the draw the frame's list signals `ready` = *v*; the control block is updated (§3).
- **The handles.** `VIEW <view> SHOW … SHARED <pid>`: the engine checks that the line came on a loopback session, that `<pid>` owns
  that session's TCP connection (the owning process of the connection's local endpoint, from `GetExtendedTcpTable`), and that the
  process runs in the engine's own Windows session and as the engine's own user (`ProcessIdToSessionId`, the token's user SID);
  then `CreateSharedHandle` and `DuplicateHandle` into it (`PROCESS_DUP_HANDLE`). The reply carries the handle values (meaningful in
  that process only), the adapter's LUID, the size, the format and the device epoch:

```
OK VIEW v_stage SHOWN SHARED luid=0x00000000000123a4 epoch=3 size=1280x720 format=bgra8
   slots=0x6f4,0x6f8,0x6fc ready=0x700 released=0x704
```

- **Its life.** The shared textures live while the view's lease does (`VIEW … KEEP` renews it, as today); at the lease's end, at
  `VIEW … HIDE`, or when the desk's process ends, the engine closes its handles and releases the textures after their last fence
  value. A device rebuild (wp3-stability-2) moves the epoch: the old handles go, and the desk asks again.

## 3. The control block

The ring file at `PreviewRing.PathFor(node, view)` keeps its header and gains a transport field; a shared view's file has no pixel
slots, only this:

```csharp
namespace PlayMap.Core.Media;

public enum PreviewTransport { Ring = 0, Shared = 1 }

/// <summary>The newest shared slot as the engine published it (a seqlock'd 64-byte record after the ring's header).</summary>
public readonly record struct SharedPreviewSlot(int Slot, ulong ReadyValue, int Width, int Height, long StampNs, long Epoch);
```

The desk reads it as it reads the ring today (`PreviewRingReader`), so it learns the newest slot, its fence value and its age without
a line on the wire.

## 4. The desk's side

```csharp
namespace PlayMap.App.Controls;

/// <summary>What a preview source hands the surface: a bitmap (the ring, a remote feed) or a composition surface (shared).</summary>
public abstract record PreviewPicture(long StampNs, string Transport, string Words);
public sealed record BitmapPicture(WriteableBitmap Bitmap, long StampNs, string Words) : PreviewPicture(StampNs, "ring", Words);
public sealed record SurfacePicture(CompositionDrawingSurface Surface, long StampNs) : PreviewPicture(StampNs, "shared", "");

public interface IPreviewSource : IDisposable
{
    /// <summary>The newest picture when it is newer than the last one returned; null otherwise.</summary>
    PreviewPicture? Poll();
}
```

`GpuPreviewSource : IPreviewSource`:

1. Asks the compositor for `ICompositionGpuInterop`; checks that `SupportedImageHandleTypes` lists `D3D11TextureNtHandle` and
   `SupportedSemaphoreTypes` lists `Direct3D12FenceNtHandle` (M2), and that `DeviceLuid` equals the engine's adapter LUID; any
   no → the ring, with the words.
2. Sends `VIEW … SHARED <its pid>`; imports each slot once (`ImportImage`) and both fences (`ImportSemaphore`).
3. On each poll (the surface's existing timer at render priority), reads the control block; for a new slot updates the surface
   with the imported image, a wait on `ready` at the slot's value and a signal of `released` at the same value (Avalonia's
   timeline-semaphore update, `UpdateWithTimelineSemaphoresAsync`; its exact signature is checked on M2's bench), so the compositor
   waits on the GPU for the engine and signals the release when it is done.
4. Re-imports once when the compositor's device is lost (`IsLost`) or the block's epoch moves; a second failure in a minute → the
   ring, with the words.

`PreviewSurface` hosts either picture; the age badge (wp3-desk-ux) reads `StampNs` on both paths.

## 5. When the ring stays

| Case | Words in the preview's corner (and its tooltip) |
|---|---|
| a remote node (the integrator's JPEG feed) | "remote" |
| the engine renders on GL | "ring: the GL backend shares no texture" |
| the desk's compositor is on another adapter | "ring: the desk draws on the Intel GPU, the engine on the NVIDIA one" |
| the interop lists no D3D12 fence (M2) | "ring: this compositor cannot wait on a Direct3D 12 fence" |
| an import failed twice in a minute | the failure's words |
| a desk build from before this round | nothing: it reads the ring as it always has |

## 6. Rates

The caps stay as they are (output views at most 10 Hz, stage and projector views at most 30). The shared path removes the copies, not
the GPU's drawing of each view; the stretch then sets rates from what the desk shows — the focused viewport at the display's rate,
thumbnails at 15 Hz, hidden views none — reported by the desk through `VIEW … KEEP`.

## 7. Facts and verbs

- `VIEW` (existing row): `[SHARED <pid>]` added by the spine; refused for a remote session ("shared previews are local only"), a pid
  that does not own the session's connection, another user's or session's process, and on the GL backend; the reply as §2.
- STATE `previews` (a top-level block the spine reserves): per shown view, its transport, size, rate and words; the desk's own
  status card shows what it imported, so the engine's "shared" and the desk's "imported" can be compared — attempts are not facts.

```json
"previews": [ { "view": "v_stage", "transport": "shared", "size": "1280x720", "hz": 30, "skipped60s": 0, "words": "" },
              { "view": "v_op_1", "transport": "ring", "size": "480x270", "hz": 10, "words": "ring: the GL backend shares no texture" } ]
```

## 8. Tests

| Test | Job | Holds |
|---|---|---|
| `PreviewShareTests` | Windows | a child test process asks `VIEW … SHARED <its pid>` over loopback, opens the handles on its own WARP device, reads the chart back, sees `ready` values grow and signals `released`; no CPU copy counted on the engine; another user's or session's pid, a pid not owning the connection, and a remote session are refused. If M8's first-day probe finds WARP's allocations cannot be duplicated into another process, the CI case runs in one process on two WARP devices and the cross-process import moves to the lab (L3) |
| `SharedSlotTests` | both | the control block's seqlock round trip; a slot not released is skipped, never waited on, and counted |
| `GpuPreviewSourceTests` | both | a fake `ICompositionGpuInterop`: adapter mismatch, no fence type and repeated import failure each → the ring with its words; a lost device → one re-import; an epoch move → `VIEW … SHARED` again |
| `DeskKillTests` | Windows | the child desk killed mid-import → facts, never a timing band on a shared runner: zero render-thread waits on the desk's fence (counted), the present ledger's missed and held counts unchanged across the kill, no allocation on the render thread, the view's textures released at the lease's end; the frame times before and after are recorded, not asserted (L3 judges them on real GPUs) |
| `ViewVerbTests` | both | the existing cases, plus `SHARED` |

## 9. Risks

- **Avalonia's fence type** (M2, `PLAN.md` R8): measured in W1 on a real GPU; without it the shared path is not taken and the ring,
  now with an age, stays.
- **Cross-API sharing** (a D3D12 texture opened by Avalonia's D3D11 device): `BGRA8` and a single mip are the formats both sides
  document; the format is fixed and tested. Whether the NuGet WARP the Direct3D 12 tests load and the system WARP a D3D11 device
  loads share NT-handle textures and fences is M8's first-day probe; if they do not, CI covers Direct3D 12 to Direct3D 12 and the
  cross-API case is the lab's (L3).
- **A desk on a hybrid laptop** composes on the integrated GPU; the LUID guard keeps the ring there, and the stretch's adapter choice
  puts the compositor on the engine's GPU where the desk allows it.
