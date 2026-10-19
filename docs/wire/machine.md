# wp3-platform on the wire

What a show node claims from Windows, what it is, and the driver set its shows run on (`docs/PLATFORM.md`). Every row is a catalogue
row, so VERBS, HELP, MCP, the assistant's prompt, the context and the Companion module carry it.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `MACHINE` | `MACHINE [STATUS]` | JSON `{platform, identity, claims:[{what, asked, applied, readBack, words, light}], locked, drift:[{item, what, locked, now, words}], words}`; the words: "PlayMap 0.2.0 (build 17, abc1234) · Windows 11 IoT Enterprise LTSC 24H2 · build 26100.4061 · tier ltsc; 9 claims, every one applied; the drivers are the locked set". Any other word refused with the syntax | low | no |
| `MACHINE SET` | `MACHINE SET QOS HIGH\|AUTO \| TIMER HIGH\|DEFAULT \| MMCSS ON\|OFF \| PROCESS HIGH\|NORMAL \| CPUSETS AUTO\|OFF \| GPU-PRIORITY NORMAL\|HIGH\|REALTIME \| GC DEFAULT\|SHOW \| AWAKE ON\|OFF` | JSON `{word, asked, was, applies:"now"\|"next start"\|"next arm", claim:{what, asked, applied, readBack, words, light}\|null, words}`. Kept in this node's settings (`NodeSettings.Machine`) by the settings file's one writer. QOS, PROCESS and GPU-PRIORITY are claimed again at once and the claim's answer is in the receipt and in STATE (Windows may refuse: the setting is kept and the words say so); AWAKE takes or lets go keep-awake at once; TIMER, MMCSS and CPUSETS apply from the render host's or each claimed thread's next start, and GC from the show's next arm, said. Refused with words: a value the setting has ("QoS is already high"), a word or value it does not take (the syntax), REALTIME on an account without `SeIncreaseBasePriorityPrivilege` (named, with "the installer grants it to the show account"), a node with no node home, a settings file that cannot be written; while armed, by the Setup class | review (REALTIME: high) | yes |
| `MACHINE LOCK` | `MACHINE LOCK [CLEAR]` | JSON `{locked, set:{os, playmap, agility, ffmpeg, syncFirmware, adapters:[{vendorId, deviceId, name, driver}]}\|null, drift, words}`: the set this node runs now kept as its shows' (adapters keyed by vendor, device and name, never by LUID), taking the drift the old lock named; `CLEAR` forgets it. Kept in `NodeSettings.Machine.Locked`, never in the project. Refused: the set already locked ("this machine's drivers are already the locked set"), `CLEAR` with none ("no driver set is locked"), nothing written either way; a node with no node home; while armed, by the Setup class | review | yes |

`SHOW ARM` and `SHOW DISARM` name the arm hook `gc` in their answer: under GC DEFAULT "the collector keeps its own mode (GC DEFAULT)";
under SHOW "the collector holds back blocking full collections while armed (SustainedLowLatency)", or the mode the runtime kept, and at
disarm the mode it is back in.

## STATE

At the end of the `machine` block, after round 2's keys:

```jsonc
"machine": { …,
  "identity": {
    "build": {"version":"0.2.0","informational":"0.2.0+17.abc1234","fileVersion":"0.2.0.17"},
    "os": {"edition":"IoTEnterpriseS","family":"iot","version":"24H2","build":26100,"revision":4061,"ltsc":true,"iot":true,
           "tier":"ltsc|managed|home|unsupported|unknown","name":"Windows 11 IoT Enterprise LTSC"},
    "cpu": {"model":"…","logical":24,"byEfficiencyClass":[8,16],"vectorBits":256,"avx512":false,"avx10":false},
    "adapters": [{"luid":"0x00000000000123a4","vendorId":4318,"deviceId":11185,"name":"NVIDIA RTX PRO 4000 Blackwell","driver":"582.16",
                  "branch":"r580_00","tier":"nvidiaRtxPro|nvidiaRtx|radeon|intelArc|software|other",
                  "support":"tier1|validatedPerDriver|bestEffort|unsupported","drivesOutputs":true,"dedicatedMb":24576}],
    "runtimes": {"agility":"1.619.2 (app)","ffmpeg":"n9.0.2","syncFirmware":"3.06"}
  },
  "capabilities": [{"feature":"realtimePrivilege|hags|highResolutionTimer|timerResolutionHonoured","luid":"","supported":true,"words":"","driver":""}],
  "platform": {
    "name":"windows|none","words":"…","light":"green|amber|grey",
    "claims": [{"what":"process.qos","asked":"high","applied":true,"readBack":"high","words":"","light":"green|amber|grey"}],
    "settings": {"qos":"high","timer":"high","mmcss":"on","process":"high","cpuSets":"auto","gpuPriority":"normal","gc":"default","awake":"on"},
    "timer": {"mode":"high|default","waits":1000,"lateP99Us":550},
    "awake": true,
    "gc": {"setting":"default|show","held":"sustainedLowLatency"|"",
           "pauses": null | {"count":412,"p99Ms":1.8,"maxMs":3.1,"buckets":[{"underMs":1,"count":380},…,{"overMs":50,"count":0}]}}
  },
  "locked": null | {"at":1760600000000,"set":{…as MACHINE LOCK answers…},"drift":[{"item":"gpu:10de:2bb1","what":"…","locked":"582.16","now":"591.44","words":"…"}]}
}
```

A claim's `what` is `process.priority`, `process.qos`, `process.gpuPriority` or `thread.<role>.priority|mmcss|cpusets|qos`; its light
green when applied, amber when Windows refused it, grey when nothing was asked (a platform that claims nothing). On a node that is not
Windows: `platform.name` `"none"`, its words "the platform seam is Windows only", one grey claim; the identity carries the build and
what the runtime reports of the CPU, everything else unread (empty, 0). Fixture: `tests/fixtures/state/machine.json`, cards
`machine.cards.json` (the platform, each claim, this machine, each GPU, the driver lock, each capability).

## The context

On a Windows node, the item `machine:platform` (kind `machine`): label "Windows 11 IoT Enterprise LTSC 24H2", state
"0.2.0+17.abc1234 · build 26100.4061 · tier ltsc · drift 0", lit amber by a refused claim or any drift; linked to the fleet's
`machine` item (`serves`). A node whose platform claims nothing adds none.

## INDEX

Section `machine`: one row per claim (`id` `claim:<what>`, kind `claim`, its light, its read-back or its words), feedback only; its
key sends `MACHINE STATUS`, a query.

## Preflight

Section `machine` (`docs/PLATFORM.md`, Machine preflight): row ids `machine.<item>` — `os.build`, `os.edition`, `os.servicing`,
`edition.updates|kiosk|displays|remote`, `sac`, `power.plan|sleep|display`, `screensaver`, `reboot`, `network` /
`network.public:<name>`, `claims` / `claim:<what>`, `timer`, `lock` / `lock.drift:<item>`, `display.primary:<output>`, and on a node
that is not Windows `platform`. Fix lines: `MACHINE SET AWAKE ON` (a timeout with keep-awake off), `MACHINE LOCK` (drift).

## Papers note

For the integration pass: `docs/ARCHITECTURE.md` §2's module table gains `PlayMap.Platform.Windows` (an edge of Core; the engine
composes it); `docs/ROADMAP.md`'s round-3 row for wp3-platform is built as written (the GC's latency mode while armed is built and DEFAULT); `docs/INSTALL.md` (wp3-installer) names the three support tiers the identity reports and the privilege GPU-PRIORITY REALTIME needs.
