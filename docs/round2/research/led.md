# Research: LED pixel mapping — Art-Net, sACN, DDP, fixtures, GDTF/MVR, WLED

Date of research: 13 October 2026. Legend: **[V]** verified this session in the cited source (URL and the
source's own date where it has one); **[V-impl]** verified in a public implementation's source code (not the
standard itself); **[R]** recalled, not re-read this session — treat as unverified; **[U]** could not open.

Sources fetched: the MVR and GDTF specs in markdown (`mvrdevelopment/spec`, main branch, fetched raw), the DDP spec
page, WLED's knowledge base and source (`wled/WLED` main, tag `v16.0.1`), ETC's sACN library (`ETCLabs/sACN`, HEAD
of 25 Sep 2026, Apache-2.0), Hundemeier's `sacn` Python library (MIT), Art-Net spec extracts (search highlights of
`art-net.org.uk/downloads/art-net.pdf` and the Art-Net 3 V1.4 1.4bi document), vendor manuals (Resolume, MadMapper,
Pixera, disguise, MADRIX), the GDTF Share terms and API, NuGet metadata.

---

## 1. Art-Net 4 (Artistic Licence)

### Facts

| Fact | Status | Source |
|---|---|---|
| OpCodes: OpPoll `0x2000`, OpPollReply `0x2100`, OpDiagData `0x2300`, OpCommand `0x2400`, OpDataRequest `0x2700`, OpDataReply `0x2800`, OpDmx/OpOutput `0x5000`, OpNzs `0x5100`, OpSync `0x5200`, OpAddress `0x6000`, OpInput `0x7000`, OpTodRequest `0x8000`, OpTodData `0x8100`, OpTodControl `0x8200`, OpRdm `0x8300`, OpRdmSub `0x8400`, OpVideoSetup `0xa010` | [V] | https://art-net.org.uk/downloads/art-net.pdf (current Art-Net 4 document, search extract) |
| OpCode is transmitted **low byte first**; ID is `"Art-Net\0"` (8 bytes); ProtVerHi/ProtVerLo = 0/**14** | [V] | Art-Net 3 V1.4 rev 1.4bi doc (docplayer mirror) https://docplayer.net/13478850-…; consistent with `clj-artnet` spec table https://github.com/robinlahtinen/clj-artnet/blob/main/src/clj_artnet/impl/protocol/codec/spec.clj |
| **ArtDmx** = 18-byte header: ID[8], OpCode(u16 LE)=0x5000, ProtVerHi, ProtVerLo, **Sequence** (1–255, 0 disables re-ordering), **Physical** (informational input port), **SubUni** (low byte of the 15-bit Port-Address: Sub-Net high nibble, Universe low nibble), **Net** (top 7 bits of Port-Address), **LengthHi, LengthLo** (big-endian; even, 2–512), then Data[Length] | [V] | same Art-Net 3 extract ("Net … top 7 bits of the 15 bit Port-Address", "even number in the range…"); clj-artnet `art-dmx-spec` |
| Art-Net 4 Port-Address is 15 bits → **32,768 universes** | [V] | disguise Art-Net driver page https://help.disguise.one/designer/devices/dmx-device-drivers/artnet ("up to 32,768 universes (Net / Subnet / Universe)"); MadMapper FAQ ("universes 0-32767 with a software limitation to 16384") https://madmapper.com/madmapper/faq |
| ArtDmx **packet strategy: unicast only**; "ArtDmx packets must be unicast to subscribers of the specific universe … If there are no subscribers to a universe, the controller shall not send ArtDmx" and "There are no conditions in which broadcast is allowed" | [V] (quoted second-hand from the spec by dmxdesktop; the strategy table "Broadcast No" verified in the Art-Net 3 extract) | https://www.dmxdesktop.com/guides/what-is-art-net ; Art-Net 3 doc §ArtDmx |
| In practice vendors still offer broadcast (Pixera's Art-Net stream defaults to `255.255.255.255`, broadcast on; MadMapper "Broadcast / Unicast with polling") | [V] | https://help.pixera.one/artnet-output-mapping ; https://madmapper.com/madmapper/features |
| **ArtSync** (`0x5200`) = 14 bytes: ID, OpCode, ProtVerHi, ProtVerLo, Aux1=0, Aux2=0 | [V] | Art-Net 3 doc §ArtSync packet definition; clj-artnet `art-sync-spec` |
| ArtSync strategy: controller sends it as **directed broadcast**; unicast **not allowed**. "A controller that wishes to implement synchronous transmission will unicast multiple universes of ArtDmx and then broadcast an ArtSync" | [V] | Art-Net 3 doc §ArtSync |
| Sync mode: at power-on a node is non-synchronous (outputs ArtDmx immediately); on receiving ArtSync it buffers ArtDmx and outputs on the next ArtSync; it **times out to non-synchronous after 4 s** without ArtSync | [V] | Art-Net 3 doc "Managing Synchronous and non-synchronous modes" |
| Refresh: "the ArtDmx packet for a specific IP Address should not be transmitted at a repeat rate faster than the maximum repeat rate of a DMX packet containing 512 data slots" (≈ 44 Hz) | [V] | Art-Net 3 doc §Refresh Rate |
| Art-Net 4 ArtPollReply fields 51–52 **RefreshRateHi/Lo**: the max ArtDmx rate a device can process; "0 to 44 represents the maximum DMX512 rate of 44Hz" (higher for SPI pixel gateways) | [V] | https://art-net.org.uk/downloads/art-net.pdf (extract) |
| Keep-alive: an unchanging input re-transmits about every 4 s; **recommended 800–1000 ms for sACN compatibility** (rev BK) | [V] | art-net.pdf revision notes; scribd mirrors of the current doc |
| Merging: two sources to one universe from different IPs → node may error or merge (HTP/LTP); Art-Net has **no priority field** | [V] | Art-Net 3 doc §Data Merging; dmxdesktop guide |
| UDP port **6454** (`0x1936`) | [V] | Pixera table (default UDP port 6454); WLED `ARTNET_DEFAULT_PORT 6454` (`wled00/src/dependencies/e131/ESPAsyncE131.h`) |
| ArtPoll is broadcast; controllers poll every ~2.5–3 s; ArtPollReply is 239 bytes; Art-Net 4 ArtPoll carries Target-Mode Port-Address bounds | [R] | spec not opened at those sections |
| Art-Net 4 released September 2016 | [V] | https://artisticlicence.com/pages/art-net-overview |
| Licence: free to implement; "Art-Net™ Designed by and Copyright Artistic Licence" credit, free OEM/ESTA code registration recommended | [R] | spec front matter not read this session |

### Consequences
- Discovery (ArtPoll/ArtPollReply) is *part of addressing*, not a convenience: unicast targets come from the replies'
  SwOut Port-Addresses.
- Frame sync across universes = unicast ArtDmx for every universe of a frame, then one directed-broadcast ArtSync
  per subnet; keep sending ArtSync while in sync mode (4 s timeout).
- Rate cap per destination IP ≈ 44 Hz unless the node's ArtPollReply RefreshRate says more.

## 2. sACN (ANSI E1.31-2018)

| Fact | Status | Source |
|---|---|---|
| UDP port **5568**; universe discovery universe **64214**; source-loss timeout **2500 ms** | [V-impl] | ETC sACN `src/sacn/private/common.h` (`kSacnPort = 5568`, `kSacnDiscoveryUniverse = 64214`, `kSacnSourceLossTimeout = 2500`), https://github.com/ETCLabs/sACN (HEAD 2026-09-25) |
| Multicast address `239.255.<hi>.<lo>` of the universe | [V-impl] | ETC `sockets.c`: `ETCPAL_IP_SET_V4_ADDRESS(ip_addr, (0xefff0000 \| universe))` |
| Data packet header **126 bytes**: root layer to 38 (preamble size 0x0010, postamble 0, ACN id `"ASC-E1.17\0\0\0"`, flags+length, vector `VECTOR_ROOT_E131_DATA = 0x00000004`, CID 16 bytes); framing at 38 (flags+len, vector `0x00000002`, **source name 64 bytes at 44**, **priority at 108**, sync address 109–110, **sequence at 111**, **options at 112** (0x80 preview, 0x40 stream terminated, 0x20 force sync), universe 113–114); DMP at 115 (vector 0x02, address&type 0xa1, first address 0, increment 1, **property value count at 123** = slots+1, **start code at 125**), data from 126 | [V-impl] | ETC `src/sacn/private/pdu.h` offsets and `pdu.c` packers; Hundemeier `sacn/messages/root_layer.py` (vectors 0x04, 0x08, 0x02, ACN identifier bytes) https://github.com/Hundemeier/sacn |
| **Sync packet 49 bytes**: root vector `VECTOR_ROOT_E131_EXTENDED = 0x00000008`, framing vector `VECTOR_E131_EXTENDED_SYNCHRONIZATION = 0x00000001`, sequence, sync address (u16), 2 reserved | [V-impl] | ETC `pdu.c` `pack_sacn_sync_framing_layer`, `SACN_SYNC_PDU_SIZE 49`; Hundemeier `sync_packet.py` |
| Priority **0–200, default 100** | [V-impl] | ETC `include/sacn/source.h` ("only allowed to be from 0 - 200. Defaults to 100") |
| Per-address priority start code `0xDD` | [V-impl] | ETC `include/sacn/common.h` `kSacnStartcodePriority = 0xdd` |
| Keep-alive defaults: levels **800 ms**, per-address priority 1000 ms | [V-impl] | ETC `source.h` |
| On stop a source sends **3 packets with Stream_Terminated** | [V-impl] | ETC `source_state.c` (`num_terminations_sent < 3`) |
| ETC's reference library **does not implement sending the sync address or force-sync** (`/* TODO sacn_sync */`) | [V-impl] | ETC `pdu.c` lines 200–220 |
| WLED: E1.31 priority handling (since 0.14.0-b1), "Non-zero start code and E1.31 preview data is ignored", 170 LEDs/universe, up to 9 universes; for ≥40 fps use ≤ 3 universes | [V] | https://kno.wled.ge/interfaces/e1.31-dmx/ |
| Pixera: sACN multicast only (broadcast unavailable), default target `239.255.0.0`, port 5568, "multiple-stream synchronization available" | [V] | https://help.pixera.one/artnet-output-mapping |
| MadMapper: "sACN (multicast only)" in FAQ; features list "Universe synchronization support (ArtNet & sACN)", "Jumbo frames support", "Up to 1000 fps DMX output" | [V] | https://madmapper.com/madmapper/faq ; https://madmapper.com/madmapper/features |

### Consequences
- sACN gives **priority-based redundancy for free**: a backup PlayMap node sending the same universes at a lower
  priority takes over at the receiver 2.5 s after the primary stops (receiver-side source loss).
- sACN sync is spec'd but under-implemented in receivers and even in ETC's own source code: treat it as optional,
  default off, and report it honestly.

## 3. DDP (Distributed Display Protocol, 3waylabs)

| Fact | Status | Source |
|---|---|---|
| Spec page "Last update 2022-09-05"; UDP/TCP port **4048** | [V] | http://www.3waylabs.com/ddp/ |
| Header **10 bytes** (14 with timecode): byte 0 flags `V V x T S R Q P` (version 01 → `0x40`; T timecode `0x10`; S storage `0x08`; R reply `0x04`; Q query `0x02`; P **push** `0x01`); byte 1 sequence (low nibble 1–15, 0 = unused); byte 2 data type `C R TTT SSS` (TTT 001 RGB, 010 HSL, 011 RGBW, 100 grey; SSS 3 = 8 bit, 4 = 16 bit); byte 3 destination id (1 default output, 246 JSON control, 250 JSON config, 251 JSON status, 254 DMX transit, 255 all); bytes 4–7 data offset (u32 BE, bytes); bytes 8–9 data length (u16 BE) | [V] | http://www.3waylabs.com/ddp/ |
| Max data per packet 1440 bytes (480 RGB pixels); efficiency table: E1.31 header 126 / 512 data; Art-Net 18 / 512; DDP 10 / 1440 | [V] | same page |
| Sync: Push flag (with last data packet, or a data-less broadcast push to all displays); or timecode (NTP-based) for future push | [V] | same page |
| WLED constants: `DDP_TYPE_RGB24 0x0B`, `DDP_TYPE_RGBW32 0x1B`, `DDP_CHANNELS_PER_PACKET 1440`, `DDP_FLAGS_VER1 0x40`, `DDP_FLAGS_PUSH 0x01`, `DDP_DEFAULT_PORT 4048`; query flag "used by xLights for auto-discovery" (unsupported in WLED) | [V-impl] | `wled00/src/dependencies/e131/ESPAsyncE131.h` (WLED main) |
| WLED's DDP receiver: assumes 8 bits per channel, RGB unless type says RGBW, rejects query/reply/storage, ignores timecode (skips 4 bytes), **renders on push once a push has been seen**, otherwise on every packet | [V-impl] | `wled00/e131.cpp` `handleDDPPacket` (WLED main) |

## 4. WLED

| Fact | Status | Source |
|---|---|---|
| Latest release tag **v16.0.1** (tag commit 30 Jun 2026); versioning jumped from 0.15.x to 16.0; main is `17.0.0-devV5` | [V] | `git ls-remote --tags https://github.com/wled/WLED.git`; tag commit date; `package.json` on main |
| **Licence changed MIT → EUPL** in build 2410270 (0.15.0-b7, Oct 2024) | [V] | WLED `CHANGELOG.md` |
| mDNS: `MDNS.addService("http","tcp",80)`, **`MDNS.addService("wled","tcp",80)`** with TXT `mac=<mac>`; hostname `wled-<last 6 of mac>` by default | [V-impl] | `wled00/wled.cpp` lines 595, 935–941 |
| JSON `/json/info` keys: `ver`, `leds.count`, `leds.rgbw` (deprecated, use `leds.lc`), `udpport`, `arch`, `brand`, `product` | [V-impl] | `wled00/json.cpp` `serializeInfo` |
| Realtime UDP on port **21324**: protocols byte 0 = 1 WARLS (255 LEDs), 2 DRGB (490), 3 DRGBW (367), 4 DNRGB (489/packet with u16 start index); byte 1 = timeout seconds (255 = stay) | [V] | https://kno.wled.ge/interfaces/udp-realtime/ |
| E1.31/Art-Net/DDP: switch mode in Sync settings and reboot; DDP always "Multi RGB"; E1.31 priority since 0.14 | [V] | https://kno.wled.ge/interfaces/e1.31-dmx/ |

## 5. GDTF (DIN SPEC 15800) and MVR (DIN SPEC 15801)

| Fact | Status | Source |
|---|---|---|
| Current versions: **GDTF DIN SPEC 15800:2022-02** (markdown: "GDTF Version 1.2 Draft 1"); **MVR DIN SPEC 15801:2023-12** (markdown: "MVR Version 1.6") | [V] | https://github.com/mvrdevelopment/spec README, `gdtf-spec.md`, `mvr-spec.md` (main) |
| `.gdtf` = zip with `description.xml`, `thumbnail.*`, `wheels/`, `models/{3ds,gltf,svg…}` | [V] | gdtf-spec.md §File Format Definition |
| GDTF **Geometry Reference** "is used to describe multiple instances of the same geometry. Example: LED panel with multiple pixels"; children `<Break DMXOffset DMXBreak>` give each instance's DMX offset for channels whose DMXBreak = "Overwrite" | [V] | gdtf-spec.md §Geometry Type Reference, Table 48–49 |
| DMXChannel attributes `DMXBreak`, `Offset` (array "highest to least significant" → `"1,2"` is 16-bit coarse/fine), `InitialFunction`, `Highlight`, `Geometry`; children LogicalChannel `Attribute` (e.g. `Dimmer`, `ColorAdd_R/G/B`, …) | [V] | gdtf-spec.md Table 58–59 and Listing 1 (a "Pixel" geometry with Dimmer + ColorAdd_R/G/B, `DMXBreak="Overwrite"`) |
| GDTF Matrix: 4×4 row-major storage, translation in 4th column; right-handed, X right, Y into the screen, **Z up**; model sizes in **metres** | [V] | gdtf-spec.md Table (attrtype-matrix), Model Length/Width/Height "Unit: meter" |
| `.mvr` = zip (STORE or DEFLATE), root `GeneralSceneDescription.xml` (UTF-8), GDTF files and geometry (`.3ds`, `.glb`) at root level | [V] | mvr-spec.md §File Format Definition |
| MVR `Fixture` node: `uuid`, `name`, children `Matrix`, `GDTFSpec` (file), `GDTFMode`, `Addresses` (`<Address break="0">` absolute integer or `"Universe.Address"`, universe from 1), `Protocols` (`type` Art-Net, sACN, …), `FixtureID`, `UnitNumber`, `Mappings`, `ChildList` | [V] | mvr-spec.md Tables 25–26, 50–54 |
| MVR Matrix: 4×3, **right-handed, Z-up, 1 unit = 1 mm** | [V] | mvr-spec.md Table 35 |
| MVR **MappingDefinition** (a video source of SizeX×SizeY) and per-fixture **Mapping** (`linkedDef`, `ux`,`uy` offset px, `ox`,`oy` size px, `rz` degrees CCW) = pixel-mapping placement in the file format itself | [V] | mvr-spec.md Tables 12–13, 63–64 |
| MVR-xchange: TCP mode discovered by mDNS **`_mvrxchange._tcp.local.`** (sub-service per group), or WebSocket mode | [V] | mvr-spec.md §Communication Format, line ~1280 |
| **GDTF Share**: free account required (site and REST API: `login.php`, `getList.php`, `downloadFile.php?rid=`); terms forbid commercial use of website material without a licence and redistribution beyond the terms | [V] | http://gdtf-share.com/landing/pages/termsAndConditions.php ; https://github.com/mvrdevelopment/tools/blob/main/GDTF_Share_API/GDTF%20Share%20API.md ; https://www.gdtf.eu/gdtf/share_api/share-api/index.html (2023-09-27) |
| Developer tools: GDTF 1.2 XSD, MVR 1.6 XSD, Share API, MVR-xchange Wireshark dissector | [V] | https://gdtf-share.com/help/developers/ |
| Licence of the spec markdown repo | [U] | no LICENSE file at the raw path; GitHub API not reachable here |

**Practicality.** Pixel positions are computable from GDTF alone: walk the geometry tree from the DMX mode's top
geometry, multiply `Position` matrices, emit one pixel per GeometryReference (or per geometry that owns ColorAdd/
ColorRGB channels), take its colour channels' offsets (+ the Break DMXOffset − 1 for "Overwrite" channels) and
16-bit pairs. Then the MVR fixture `Matrix` (mm, Z-up) places the fixture, and `Addresses` give universe.address.
Everything else in GDTF (wheels, physical emitters, relations) can be ignored for colour pixel mapping, except
channel `Default`/`Highlight` values for the non-colour channels (a pixel bar's master dimmer must be sent at 255).

## 6. How the competition does LED mapping

| Product | What it does (verified) | Source |
|---|---|---|
| **Resolume Arena** | "Lumiverses" = virtual sets of universes; fixtures sample the composition at the **centre pixel of each cell**; fixture editor (pixels W×H, distribution/snake, colour spaces RGB variants, L, CMY, W/A extraction, gamma correction, dummy channels with defaults); right-click a Lumiverse lists **detected Art-Net nodes** and fills IP/universes; Auto Span grows a Lumiverse past 512 channels, **Align Output** keeps a pixel from splitting across universes; DMX Output view shows channel layout, overlaps marked red; drag fixtures to re-address; test card diagonal line to check alignment; **ArtSync** option with a DMX framerate and a delay (default 40 ms for mixed light+projection, 0 for lights only); "having more than one pixel block is not supported" | https://resolume.com/support/en/dmx ; https://www.resolume.com/support/en/fixture-editor ; ENTTEC guide (retrieved Jul 2026) https://support.enttec.com/dmx/edmx-art-net-sacn-networking/using-resolume-arena-with-enttec-art-net-nodes |
| **MadMapper** | "Light Surfaces" convert texture to DMX; fixture library + editor (incl. GLSL "Expression" pixels); Art-Net 4 (0–32767, limited to 16384), sACN multicast only, broadcast/unicast with polling, **universe sync (Art-Net & sACN)**, jumbo frames, **up to 1000 fps DMX output**, cross-universe fixtures, CSV fixtures import, SVG fixture import, **"Led Scanner"**, DMX monitor; cannot map fixtures onto a media directly (workaround via Syphon/Spout) | https://madmapper.com/madmapper/features ; https://madmapper.com/madmapper/faq ; https://madmapper.com/files/01-Introduction%20to%20the%20User%20Interface.pdf |
| **Pixera** | Art-Net, sACN, **KiNET v1 (port 6038)** output streams; GPU chosen per stream; stream resolution derived from mapping; destination size starts at **1×1 as a safety measure** ("activating a Full HD or 4K pixel mapping could otherwise generate excessive Art-Net traffic"); Auto Transform editor with Address/Net/Sub-Net/Universe/Footprint/Offset; **GDTF import**; fixture layout with stride and fixed-value control channels; streams from **CSV (25.1)**; limitation: control channels interleaved between per-pixel colour channels cannot span multiple pixels | https://help.pixera.one/artnet-output-mapping ; https://pixera.helpjuice.com/mapping-/extended-dmx-output-mapping |
| **disguise** | Art-Net driver in/out, 32,768 universes, broadcast or unicast, **strict IP filtering** for Art-Net and sACN input | https://help.disguise.one/designer/devices/dmx-device-drivers/artnet |
| disguise 3D LED pixel mapping sampled from the stage | [U] not verified this session | — |
| **MADRIX 5** (help 5.8) | Pixel (2D) / **voxel (3D)** matrix; Patch Editor with Voxel Map / DMX Map views, Auto Address, Z-layers; **CSV fixture list import** (Position X/Y/Z as centres, DMX address column "universe+channel", scale with negative = mirror) and export (columns incl. Universe, Channel, Position X/Y/Z, Rotation, Flip; optimised for Capture); "Match Voxel Map Area" | https://help.madrix.com/m5/html/madrix/hidd_csv_import.html ; https://help.madrix.com/m5/html/madrix/hidd_preferences_patch.html ; https://help.madrix.com/m5/html/madrix/hidd_csv_export.html |
| **LightAct** | 3D LED mapping, KiNET | [U] not verified |
| **TouchDesigner pattern** | pixel positions from an OBJ polyline, sample a TOP at those points, smoothing, send sACN | https://jmarsico.github.io/rsma2019/tutorials/td_dmx/ (2019) |

## 7. .NET libraries (for reference; MIT-compatible)

| Package | Version / date | Licence | Notes | Status |
|---|---|---|---|---|
| `Haukcode.sACN` | 2.0.112 (published 2026-09-10) | MIT | depends on `Haukcode.HighPerfComm`, `Haukcode.Network` | [V] NuGet flat container + registration |
| `Haukcode.ArtNet` | 4.0.115 | MIT | ".NET8+" | [V] NuGet |
| `Kadmium-sACN-Core` | 0.0.11 | — | stale | [V] NuGet |

The packets are tiny and fixed; PlayMap's own allocation-free writers (span over pooled buffers, one per universe)
are less code than adapting a library's threading model, and keep the analyzers' rules. Use the libraries only as
cross-check references in tests (or not at all).

## 8. Pixel formats, gamma, power — what the field expects

| Item | Status | Note |
|---|---|---|
| Colour orders RGB/GRB/BRG…, RGBW/GRBW, RGBWW, RGBA(mber), single-channel L; WS2812-class strips are GRB | [R] | Resolume colour spaces include RGB variants, L, CMY, W/A extraction [V]; IC-specific orders recalled |
| 16-bit pixels: GDTF expresses them as 2-byte offsets (coarse, fine) [V]; DDP data type SSS=4 [V]; 16-bit pixel ICs exist (e.g. UCS8903/8904-class) [R] | mixed | |
| WLED applies its own brightness and gamma to realtime data ("It uses the current brightness and gamma correction settings") | [V] | kno.wled.ge UDP realtime page |
| Resolume has per-fixture **Gamma Correction** | [V] | Resolume manual TOC (Fixture Editor → Gamma Correction) |
| Power/current limiting is expected on large pixel rigs (WLED's ABL) | [R] | not re-read |

---

## Design implications for PlayMap

1. **LED fixtures belong in the model as their own rig (`Project.Leds`)**, placed in any of five sampling spaces —
   *canvas* (Resolume/MadMapper parity), *output* (sample a finished output), *source*, *surface UV*, and *world 3D*.
   The 3D case is where PlayMap wins: a pixel bound to a surface samples exactly what the projector paints at that
   UV (same `uMode/uRegion/uProjector` maths as `StageRenderer.SetContent`), so LEDs embedded in a mapped facade
   continue the projection seamlessly — something none of the verified competitors states.
2. **GPU sampling in two passes**: (a) sample pass — one texel per LED pixel, footprint-averaged in linear light;
   (b) patch pass — one texel per DMX slot (512 per row, a row per universe) that applies colour order, RGBW
   extraction, white balance, gamma, 16-bit coarse/fine and default channels. The read-back is then the packet
   payload; the CPU only copies rows into pre-built packets. A CPU reference of the patch pass gives exact tests.
3. **Network output in step with the render loop**: every LED frame is stamped with the leader instant of the light
   it belongs to; a sender thread emits it at that instant minus the node's latency, then latches all universes at
   once (ArtSync directed broadcast / sACN sync universe / DDP push). Decimate to each node's rate on the leader-time
   grid (≈44 Hz Art-Net unless ArtPollReply RefreshRate says more) so two PlayMap nodes pick the same frames.
4. **Discovery is addressing**: ArtPoll every 3 s while LED output runs (unicast targets come from ArtPollReply),
   mDNS `_wled._tcp` + `/json/info` for WLED, MVR `Protocols`/`Addresses` on import. Never send ArtDmx broadcast by
   default (spec), offer it as an explicit legacy option.
5. **Safety, by the doctrine**: a project carries the rig but the *node* decides whether to transmit
   (`NodeSettings.LedOutput`, off on a fresh node — fail closed); blackout reaches the LEDs before any content code;
   sACN termination (3 packets) on stop; keep-alive 800–1000 ms; a power cap; FlashGuard on LED test patterns; and
   Pixera's lesson — an imported 4K-sized mapping must not flood the network by accident (bandwidth estimate shown
   and refused above a limit until confirmed).
6. **Redundancy**: sACN priority (primary 150 / backup 100) gives receiver-side failover; for Art-Net the backup
   stays silent until the primary's Show Bus hello is lost.
7. **Import**: MVR (positions, GDTF modes, addresses, `Mappings`), GDTF (pixel geometry + channel layout), CSV
   (MADRIX/Capture-style columns). Never bundle GDTF Share files (terms); the user brings `.gdtf`/`.mvr`.
8. **Everything testable over loopback**: packet codecs are pure (Core), the sender talks UDP to 127.0.0.1 where a
   test receiver decodes Art-Net/sACN/DDP; GL passes run on llvmpipe against the CPU reference; WLED is faked by a
   loopback HTTP + DDP endpoint; mDNS stays behind a seam.
9. **Beat MadMapper's "Led Scanner"** with camera auto-locate that decodes blink codes like structured light and
   lands each pixel on the stage mesh in 3D (simulated in the scan simulator here; real cameras on hardware).
