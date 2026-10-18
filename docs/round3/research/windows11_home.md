# Windows 11 Home for a PlayMap node and desk

Research for the round-3 plan revision, as of 18 October 2026. The owner's answer: "PlayMap also needs to run on Windows 11
Home as well." The plan today names Windows 11 IoT Enterprise LTSC 2024 for show nodes and Pro or Enterprise for desks
(`docs/round3/design/installer.md` §7, `PLAN.md` wp3-installer, wp3-platform).

Confidence marks on every claim:

- **[H]** a primary source (Microsoft Learn, Microsoft Support, a Microsoft blog, the vendor's own document) states it.
- **[M]** a primary source implies it, or reputable secondary sources agree and nothing contradicts them.
- **[L]** community reports only, conflicting evidence, or an inference not yet seen on hardware.

Nothing here was tried on a Windows 11 Home machine (the owner: full physical hardware testing is not available yet).

---

## 1. The answer in one page

Windows 11 Home runs everything PlayMap's engine and desk need at the API level: Direct3D 12 and 11, DXGI flip model with
independent flip and multiplane overlays, HAGS, MMCSS, process and thread QoS, high-resolution waitable timers, power requests,
Windows Installer (MSI), Windows services, Task Scheduler, auto-logon, WER local dumps, Defender exclusions, Dev Drive, and the
vendor stacks (NVIDIA RTX PRO Sync, Mosaic, Warp and Blend; AMD Eyefinity and S400), which are gated by GPU class, not by
Windows edition.

What Home lacks are the **management and lockdown layers** a dedicated node normally leans on:

| Missing on Home | What it costs a show node | Confidence |
|---|---|---|
| Assigned Access (kiosk) | no supported single-app lockdown | [H] |
| Shell Launcher (also missing on Pro) | no supported shell replacement | [H] |
| Specialized displays ("Remove display from desktop"; the EDID route too) | outputs stay part of the desktop: cursor, windows, toasts and dialogs can land on the air | [H] |
| Windows Update client policies (deferral, target version, exclude drivers, active-hours range, no-restart policies) | no deferral; only pause (35 days at a time, now re-extendable), active hours (max 18 h), metered connection | [H] |
| Local Group Policy editor, `secpol.msc`, `lusrmgr.msc` | the same settings through APIs or Settings, not a GUI; many `Policies` registry values are ignored on Home | [H] gpedit absent, [L] which values Home honours |
| Remote Desktop host | no RDP into a Home node | [H] |
| BitLocker management (Home has Device Encryption only) | encryption turns itself on with a Microsoft account; recovery prompts after firmware/hardware changes | [H] |
| More than 128 GB RAM; more than one CPU socket | a Home node tops out at 128 GB and one socket | [H] RAM, [M] sockets |
| Local account in setup | setup needs internet and a Microsoft account; new Microsoft accounts are passwordless, which breaks auto-logon until a password is added or a local account is made | [H] |

And Home ships with **consumer defaults that matter to an unsigned show application**: Smart App Control (blocks unsigned
binaries, including DLLs, when on), S mode on some new Home PCs (Store apps only), OneDrive folder backup turned on in setup,
memory integrity on by default.

**Recommendation.** Support Home as a named tier — "Home: runs, unmanaged" — beside the IoT Enterprise LTSC tier for permanent
show nodes and Pro for desks. PlayMap must not depend on any Pro-only feature; it detects the edition and publishes each missing
layer as an **information row** in preflight (the owner's answer 9 sets information rows as the pattern), never as a refusal,
and the installer's opt-in show-machine feature takes a Home branch that writes only what Home honours. Four things are hard
gates on Home and need words, not information: S mode, Smart App Control in enforcement (while PlayMap is unsigned), an output
display that is also the primary display (toasts, UAC and setup screens land there), and media inside a OneDrive placeholder.
Confidence in the recommendation: **high** for the edition facts, **medium** for the operational advice until a Home machine
runs L-rows.

---

## 2. Versions and servicing (as of October 2026)

- Windows 11 **26H2** is generally available since 2026-09-29 (build 26300); Home and Pro get 24 months (to 2028-10-10). **25H2**
  Home and Pro end 2027-10-12. **24H2** Home and Pro reached end of updates on 2026-10-13, so 24H2 Home machines are now being
  moved to a supported version automatically. **26H1** (build 28000) is for new devices only and is not offered in place. [H]
  — Windows 11 release information; Supported versions of Windows client.
- Automatic feature updates are offered to consumer and non-managed devices "before—or as they are approaching—end of servicing".
  [H] — Windows Update FAQ. On Home there is no policy to hold a version: a Home node moves to a new feature update on Microsoft's
  schedule, at worst every 24 months.
- 26H2 is an enablement package over the same servicing branch as 24H2 and 25H2 (one restart). [H] — 26H2 update history.
- The plan's floor ("Pro or Enterprise 24H2/25H2 for desks"; "Windows 10 is not supported") becomes, for Home: **25H2 or 26H2**.
  24H2 Home is out of support this month. [H]

---

## 3. Kiosk and auto-start on Home

### 3.1 What the editions offer

- **Assigned Access** (single-app kiosk, restricted user experience): Pro, Enterprise/Enterprise LTSC, Education, IoT
  Enterprise/IoT Enterprise LTSC. Not Home. Its single-app kiosk runs a UWP app or Edge only, not a Win32 exe. [H] — Assigned
  Access overview (updated 2026-07).
- **Shell Launcher** (v1 `Eshell.exe`, v2 `CustomShellHost.exe`): Enterprise/Enterprise LTSC, Education, IoT Enterprise/IoT
  Enterprise LTSC. Not Home, not Pro. It needs a shell that does not exit after launching another process; an elevated shell
  needs UAC off. [H] — Shell Launcher overview.
- **Keyboard Filter, Unified Write Filter**: Enterprise/IoT only. [M]

### 3.2 What works on Home instead

1. **Auto-logon** works on every edition (Winlogon `AutoAdminLogon`). Microsoft's guidance: store the password as an LSA secret
   with `LsaStorePrivateData` (secret name `DefaultPassword`), never as the plain-text `DefaultPassword` registry value, which
   Authenticated Users can read remotely. [H] — "Configure Windows to automate logon"; "Protecting the Automatic Logon Password".
   Sysinternals Autologon does exactly this [H], but its licence does not allow PlayMap to redistribute it [M]: the installer
   either points the operator at it (the plan's present wording) or calls `LsaOpenPolicy`/`LsaStorePrivateData` itself, holding
   the password only for the call and writing it to no log, journal, STATE or hello (the doctrine). Anyone with admin rights can
   read an LSA secret back. [H]
2. **Microsoft-account trap.** New Microsoft accounts are "passwordless by default" since May 2025 [H — Microsoft Security blog,
   2025-05-01]. Auto-logon needs a password; with "only allow Windows Hello sign-in for Microsoft accounts on this device" on,
   the netplwiz checkbox disappears [M]. On Home, whose setup now requires a Microsoft account (§9), the practical route is a
   **local standard "show" account created after setup** (Settings ▸ Accounts ▸ Other users, or `net user` from an elevated
   prompt) with a password, auto-logon to it, and the Microsoft account kept as the administrator. [M]
3. **Starting the engine at logon.** A Task Scheduler task "at log on of the show user" runs on Home [H — Task Scheduler is
   edition-neutral]. The plan's supervisor (a LocalSystem service) works on Home; services run in session 0 and cannot show UI,
   so the supervisor starts the engine in the console session with `WTSQueryUserToken` + `CreateProcessAsUser` (`winsta0\default`),
   which needs `SeTcbPrivilege` (LocalSystem has it) [H — Interactive Services; CreateProcessAsUser; the session-0 note]. This is
   the plan's M9 unchanged; nothing about it is edition-specific. [H]
4. **Shell replacement by registry**: `HKLM\…\Winlogon\Shell`, or per user via `IniFileMapping\system.ini\boot\Shell =
   USR:Software\Microsoft\Windows NT\CurrentVersion\Winlogon` plus `HKCU\…\Winlogon\Shell`. Documented by Microsoft only for
   Windows XP Embedded [H — that archive page]; on Windows 11 it is unsupported, it is exactly the persistence mechanism malware
   uses (Defender may flag it), and it loses Shell Launcher's restart-on-exit handling. [M] Recommendation: **do not** offer it in
   the product; the supervisor already restarts the engine. The desk-less node on Home runs Explorer underneath the engine.
5. **Home "kiosk" limits, summarised**: no supported lockdown, no write filter, Explorer always present, Win+key shortcuts,
   Ctrl+Alt+Del and Task Manager available to anyone at the keyboard. A Home node is "dedicated by convention", not locked. [M]

---

## 4. Group Policy and its equivalents on Home

- Home has no Local Group Policy Editor; `gpedit.msc`, `secpol.msc` and `lusrmgr.msc` are absent. [H for gpedit — Microsoft
  Support/Learn statements; M for the others]
- **Policy CSP** pages list editions per policy. Every Windows Update policy PlayMap's plan writes — `DeferFeatureUpdatesPeriodInDays`,
  `DeferQualityUpdatesPeriodInDays`, `PauseFeatureUpdatesStartTime`, `ActiveHoursMaxRange`, `ActiveHoursStart/End`,
  `ExcludeWUDriversInQualityUpdate`, `TargetReleaseVersion`, `ProductVersion`, `SetMaxPauseDays`,
  `NoUpdateNotificationsDuringActiveHours` — lists **Pro, Enterprise, Education, IoT Enterprise** and not Home. [H] —
  Update Policy CSP. "Windows Update client policies" (formerly Windows Update for Business) are available for Pro, Pro for
  Workstations, Education and Enterprise editions. [H]
- **Writing the same values under `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` on Home**: evidence conflicts. A
  Spiceworks report (Dec 2024) says Home ignores the WSUS values in that key; consumer how-to sites (2023–2026) say `AU\NoAutoUpdate
  = 1` works on Home; a Microsoft Q&A reply says "this group policy does not work on Home versions". [L] Recommendation: PlayMap
  **writes no Windows Update policy on Home** (an unhonoured policy is an attempt that is not a fact) and reports the edition's
  limits instead.
- **What does work on Home** is what the Settings app itself writes (per-user and per-machine *settings*, not *policies*):
  notifications, Do Not Disturb rules, Storage Sense, power plans, Game Mode, graphics preferences, WER, Defender exclusions,
  firewall rules, local accounts and groups (`net localgroup` works where `lusrmgr.msc` does not), and LSA account rights through
  `LsaAddAccountRights` (the plan's "MSI grants two named privileges to one named account" needs the API on Home, not the
  `secpol.msc` UI). [M]
- **MDM enrolment** (Intune and the like) is not a Home path. [M]

---

## 5. Windows Update on Home

- **Pause**: up to 35 days from today; the calendar picks an end date. A pause blocks restart-requiring downloads and automatic
  restarts until it ends; updates in progress are cancelled; when it ends, Windows checks, downloads and installs. [H] —
  "Pause updates in Windows" (current page). **New in 2026**: the pause can be extended again and again, each time to at most 35
  days from today, "with no limits on how many times you can reset the pause end date". [H — Windows Insider blog 2026-04-24, and
  the current Support page describes extending] Rolled out broadly with the October 2026 update wave [M — Windows Experience blog
  2026-10-15 says "improved Windows Update to give you greater control"]. Verify on the target build. [M]
- **Other 2026 changes** [H — the same Insider blog, M for retail availability]: skip updates during setup (OOBE); Restart and
  Shut down always available **without** installing pending updates; driver, .NET and firmware updates coordinated with the monthly
  quality update so a device sees **one monthly restart**; driver titles name their device class (display, audio).
- **Active hours**: set manually or automatically in Settings ▸ Windows Update ▸ Advanced options; the range is at most 18 hours
  (the policy that changes the range is Pro+). Outside active hours Windows may restart to finish updates. [H]
- **Metered connection**: updates "won't download until you get them" on a metered connection [H — active hours page]; some
  security-critical updates may still download [M]. Ethernet can be set metered in Settings. [M]
- **No deferral, no version pinning, no driver exclusion** on Home (policies listed above are Pro+). [H] The practical Home
  controls are: pause and re-extend before a show, active hours around show hours, the restart-without-update power menu, and a
  maintenance window the operator chooses (manual install). [H/M]
- **Drivers from Windows Update**: on Home there is no supported switch to exclude them (the policy is Pro+) [H]; the 2026 change
  bundles driver installs with the monthly restart [H]; Game Mode suppresses Windows Update driver installs and restart
  notifications only "when you're running a game" [H — Game Mode support text]. The classic "Device installation settings ▸ No"
  switch exists on Home but its effect on Windows Update driver offers is not documented [L]. Recommendation: `MACHINE LOCK`
  (the plan's driver pin) **observes** the GPU driver version on every start and turns amber when it changed, rather than claiming
  it can stop a change on Home.
- **A pending reboot** is readable on every edition through the Windows Update Agent API (`ISystemInformation::RebootRequired`)
  [M], which the plan already makes a preflight fact.

---

## 6. Security defaults that change how a node behaves

### 6.1 Smart App Control (Home included) — a hard gate while PlayMap is unsigned

- SAC exists on Windows 11 (not 10) and is on by default in **evaluation** mode on clean installs; it may switch itself to **On**
  (enforcement) or **Off** after evaluating the user. [H] — App & browser control; SAC FAQ; Microsoft Learn SAC overview.
- In enforcement, "apps cannot be run unless they are recognized by Microsoft's app intelligence services, or they are signed with
  a trusted certificate"; unsigned code is blocked; there is **no per-app exception**. [H]
- SAC "evaluates binaries as it loads them": every DLL, not only the exe. [H — "Test App Signatures with Smart App Control"] So
  even after PlayMap's own files are signed, an **unsigned FFmpeg build** (the LGPL shared build PlayMap ships) would be blocked
  on an enforcing machine. [M] Signing the shipped FFmpeg DLLs with PlayMap's identity is the fix when signing arrives. [M]
- Since late 2025 / early 2026, SAC can be turned **on and off again without reinstalling** on 24H2/25H2 [H — SAC FAQ "Recent
  Windows updates allow…"; Neowin and MakeUseOf, Feb 2026].
- State is readable: `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState` (0 off, 1 enforce, 2
  evaluation), or `citool.exe -lp`. Blocks are logged as Code Integrity events 3077 (enforce) and 3076 (evaluation). [H]
- **Consequence of the owner's "Leave unsigned for now"**: on any Home (or Pro) machine with SAC On, PlayMap does not start, and
  in evaluation it may start today and be blocked after SAC enforces. Preflight (run by whatever does start — the installer's
  launch check, or a signed probe later) names SAC's state; the guide says "turn Smart App Control off for an unsigned build".
  Also expect SmartScreen's "Windows protected your PC" on an unsigned, downloaded installer (Mark of the Web). [H for
  SmartScreen behaviour generally, M for the exact prompt]

### 6.2 S mode (Home only)

- Windows 11 in S mode exists **only in the Home edition**; it runs only Microsoft Store apps; switching out is free, needs
  internet, and is one-way. [H] — S mode FAQ; Windows 11 requirements.
- Detect with `Windows.System.Profile.WindowsIntegrityPolicy.IsEnabled` / `CanDisable`. [H] An S-mode Home PC cannot run the
  unpackaged PlayMap at all; the installer cannot even start there, so the words belong in `docs/INSTALL.md` and on the download
  page. [H]

### 6.3 Memory integrity (HVCI) and VBS

- Memory integrity is enabled by default on new Windows 11 devices with capable hardware, Home included. [H — Microsoft Support
  "Options to optimize gaming performance in Windows 11"]
- Microsoft states it, and the Virtual Machine Platform, can cost game performance "in some scenarios and some configurations",
  and that gamers may turn them off while playing and back on after. [H] Independent measurements put it at a few percent in some
  games and nothing in others. [L]
- Drivers incompatible with memory integrity fail to load (or, rarely, fail to boot). [H — "Enable memory integrity"] This matters
  for **capture-card, DMX/USB and sync-board drivers**: an old driver that is not HVCI-compatible will simply not be there on a
  default Home install. [M]
- Recommendation (measured, then governed): leave it on, report it (Win32_DeviceGuard `SecurityServicesRunning`, [M]), and add
  an L-row that measures shown-frame gaps and capture latency with it on and off on the same machine before any advice.
  PlayMap never toggles it.

### 6.4 Administrator protection

- Generally available since the August 2026 update (KB5120998), **off by default**, supported on Home, Pro, Enterprise,
  Education. Enabled by default only inside the Windows security baselines from October 2026 (commercial). [H — Microsoft Learn;
  Message Center MC1506291]
- When on: no auto-elevation, every elevation authorised interactively, and the elevated process runs under a separate,
  profile-separated system-managed account, so an elevated process does not see the user's `HKCU` and profile. [H] PlayMap's rule
  "the engine never elevated" is right; an elevated desk would read a different settings hive. [M]

### 6.5 The UAC secure desktop, the lock screen, Ctrl+Alt+Del

- Only one desktop of `Winsta0` is active at a time; the system switches to the Winlogon desktop for Ctrl+Alt+Del and while the
  UAC dialog is open, and to the ScreenSaver desktop for a secure screen saver. [H — "Desktops"] Outputs drawn on the Default
  desktop therefore stop being the input desktop for that time. Whether the air freezes, blacks or keeps its last frame is
  driver- and mode-dependent. [L] On Home, with no specialized displays, this is the same for every output. Rule for the guide:
  **no elevation, no lock, no secure screen saver on a node while armed**; the screen saver and dynamic lock read as facts (the
  plan already reads the screen saver).

### 6.6 Device encryption (Home's BitLocker)

- Device Encryption is BitLocker turned on automatically for the OS drive **and fixed drives** at first sign-in with a Microsoft
  account; the recovery key goes to that account. Not turned on automatically with a local account. Available on Home; full
  BitLocker management is Pro, Enterprise, Education. [H] — "Device Encryption in Windows"; "BitLocker drive encryption in
  Windows 11 for OEMs".
- From 24H2 the automatic requirements no longer include Modern Standby/HSTI or the DMA check, so far more desktops encrypt
  themselves. [H] Since Home setup now requires a Microsoft account (§9), a new Home node is in practice **encrypted by default**.
  [M]
- Recovery is asked for after hardware, firmware or boot changes BitLocker cannot tell from an attack. [H] On a show node that
  means a BIOS update, a Secure Boot change or a new GPU's option ROM can stop the machine at a recovery screen. [M] Suspend
  protectors before such work (`manage-bde -protectors -disable C: -rebootcount 1`; Microsoft's own SAC test procedure uses
  `manage-bde` on consumer machines, so the tool is present on Home). [M]
- **Throughput.** Software BitLocker costs NVMe throughput: Microsoft's own demo shows ~1.6 GB/s software vs ~3.7 GB/s
  hardware-accelerated single-thread sequential reads [H for the existence of the comparison, M for the numbers via Tom's
  Hardware]; Tom's Hardware measured up to 45 % [M]. Hardware-accelerated BitLocker (crypto offload) exists from 24H2 with the
  September 2025 update and 25H2, but only on new crypto-offload SoCs, first Intel Core Ultra series 3 vPro; `manage-bde -status`
  says "Hardware accelerated". [H] For high-bitrate HAP/ProRes/NotchLC playback from an encrypted media drive this is a measured
  cause candidate: an L-row should read media throughput with encryption on and off.
- Recommendation: publish `encryption` (on/off, hardware-accelerated or not, per volume holding media) as a fact; the guide says
  where the recovery key is and that a dedicated media volume may be left unencrypted by the owner's choice. PlayMap never
  changes it.

---

## 7. Scheduling, timing and power (all edition-neutral)

- **Game Mode** is on by default on every edition; it "prevents Windows Update from performing driver installations and sending
  restart notifications" and "helps achieve a more stable frame rate" — while a game runs. [H] Its API (`HasExpandedResources`,
  `GetExpandedResourceExclusiveCpuCount`, `ReleaseExclusiveCpuSets`) grants exclusive CPU sets only to an app "in the foreground
  and [with] focus". [H] PlayMap's outputs are usually not the focused window (the desk is), so Game Mode's benefits are not
  dependable for a node. [M] Report it; don't rely on it.
- **HAGS** (hardware-accelerated GPU scheduling) needs a supporting GPU and a WDDM 2.7+ driver; it was introduced opt-in, with
  OEMs encouraged to ship it on [H — DirectX blog 2020]. Windows requires it for the WDDM 3.0 **hardware flip queue** (and its
  advanced form, which removes a CPU round trip before a present) [H — "Hardware flip queue"]. NVIDIA frame generation needs it
  [M]; some capture/encode stacks advise it off [L]. The default depends on driver and OEM (`HwSchEnabledByDefault`) [H]. The
  plan already reports HAGS; on Home nothing differs. Never toggled by PlayMap; an L-row measures.
- **MMCSS** (Multimedia Class Scheduler) exists on all editions; tasks Audio, Capture, Distribution, Games, Playback, Pro Audio,
  Window Manager; `SystemResponsiveness` reserves CPU for low-priority work (default 20 %, 100 disables MMCSS). [H] MMCSS tags
  threads with the **Media** and **Deadline** QoS levels. [H]
- **QoS / EcoQoS / HighQoS.** QoS levels High, Medium, Low, Utility, Eco, Media, Deadline. A window-owning process is High when
  in focus, Medium when visible, Low when minimised or fully occluded. A process opts out of throttling ("HighQoS") with
  `SetProcessInformation(ProcessPowerThrottling, ControlMask = EXECUTION_SPEED, StateMask = 0)`; threads likewise with
  `SetThreadInformation`. [H] Since the 2025 servicing, Windows may lower a foreground app to Medium after user inactivity **on
  battery**; `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\DisableUserPresenceQos = 1` turns that off (the plan's
  stretch already writes it). [H]
- **Timer resolution.** Since Windows 10 2004 `timeBeginPeriod` is per process; since Windows 11 a window-owning process that is
  fully occluded, minimised, invisible and inaudible is not guaranteed its higher resolution. Opt out with
  `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` and `StateMask = 0` ("always honour"). [H] High-resolution waitable timers
  (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803+) do not depend on the global resolution. [H] The plan's wp3-platform
  item 2 already moves deadline waits to these timers; add the two QoS opt-outs for the engine's deadline threads, both measured
  first. A minimised desk losing timer resolution is fine; an engine whose only window is occluded (a headless send-only node
  with a hidden window) is the case to guard. [M]
- **Power plans.** Modern Standby systems allow only Balanced (and plans derived from it) [H — Power Policy Settings]; the
  power-mode slider ("Best performance") appears only on Balanced [H]. "Ultimate Performance" ships visible only on Pro for
  Workstations but `powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61` adds it on any edition, Home included
  [M — Microsoft TechNet wiki archive; users report it on Home Single Language]; it is not offered on battery systems [M].
  Everything else in the plan's power list (USB selective suspend, PCIe ASPM, display never sleeps) is a power-plan setting and
  works on Home. [H]
- **Keep-awake and idle.** `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)` (or
  `PowerSetRequest` with DisplayRequired **and** SystemRequired) is what video and presentation apps must hold. [H] The system
  does not detect disk, CPU or video activity as use. [H] Task Scheduler's idle detection (user absent after 4 minutes without
  input; CPU and I/O under the threshold) starts idle tasks; Microsoft documents that an app holding `ES_DISPLAY_REQUIRED` stops
  the system being treated as idle (stated for Windows 7) [H for 7, M for 11]. **Automatic Maintenance** runs daily (2–3 AM by
  default), can wake the machine, and runs when idle [M — Microsoft troubleshooting article + Windows Central]. A GPU-bound node
  with hardware decode can look idle by CPU and disk, so maintenance (Defender scans, optimisation, .NET NGEN) is a real risk in a
  long show without the display request. [M] The plan's keep-awake item covers it while an output is open; the guide adds
  "maintenance time outside show hours, wake-to-maintain off" (Control Panel ▸ Security and Maintenance, available on Home). [M]

---

## 8. Notifications and full-screen prompts

- Do Not Disturb turns on automatically, if the rules are ticked, "when using an app in full-screen mode (priority notification
  banners are also hidden)", "when playing a game", "when duplicating your display", during set times, and for the first hour
  after a feature update. [H — the Windows Backup settings catalogue lists these rules] Whether the full-screen rule fires for a
  borderless window on a **secondary** output, while the desk has focus, is not documented. [L]
- `SHQueryUserNotificationState` reports QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN / QUNS_PRESENTATION_MODE; well-behaved apps hold
  their own notifications then. [H]
- "Show the Windows welcome experience after updates…", "Suggest ways to get the most out of Windows and finish setting up this
  device", "Get tips and suggestions" (Settings ▸ System ▸ Notifications ▸ Additional settings) govern the full-screen "finish
  setting up" screens that can appear after updates. [M — Computerworld, Digital Citizen; consistent Microsoft Q&A answers]
- Recommendation for the Home branch of the show-machine feature (per user, Settings-level, honoured on Home [M]): notifications
  off for the show user, DND rules on, the three "additional settings" off, Game Bar background capture off. And one layout rule
  for every edition without specialized displays: **the desk's monitor (or a dummy/admin output) is the primary display**, never
  a projector, because toasts, UAC, setup screens and new windows open on the primary display. [M]

---

## 9. Accounts and setup on Home

- Windows 11 Home (and Pro for personal use) require internet and a Microsoft account to complete setup. [H — Windows 11
  specifications / requirements]
- In October 2025 Microsoft removed the known local-account mechanisms from OOBE (`oobe\bypassnro`, `start ms-cxh:localonly`)
  starting with builds 26120.6772/26220.6772. [H — quoted Windows Insider blog text via PCWorld, Windows Latest, Tom's Hardware]
  Unattended setup (`autounattend.xml`) is still reported to work. [L] After setup a local account can be added or the Microsoft
  account switched to a local one. [M]
- Recommendation: the guide's Home path is "set up with the owner's Microsoft account, then create a local standard 'show'
  account with a password, auto-logon to it (LSA secret)". Note that this also triggers Device Encryption for the owner's account
  (§6.6) and OneDrive folder backup (§10). [M]

---

## 10. Storage: OneDrive, Storage Sense, Defender, Dev Drive

- On Home, setup turns on **OneDrive folder backup** (Desktop, Documents, Pictures move into OneDrive) without the choice Pro
  offers [M — Thurrott 2024, ZDNET 2026]. **Files On-Demand** is on by default (OneDrive 23.066+), so files can be online-only
  placeholders that download on open and cannot open offline. [H] **Storage Sense** is off by default but Windows may turn it on
  at low disk; when on, OneDrive content not opened for 30 days becomes online-only by default (22H2+). [H]
- Consequence: a show whose media sits under Documents or Desktop can stall on a cloud recall, or fail offline. Recommendation: a
  **hard preflight row** for any project media whose file attributes say cloud placeholder or not pinned
  (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, `FILE_ATTRIBUTE_OFFLINE`, `FILE_ATTRIBUTE_UNPINNED`) [M], and the default media root
  outside any OneDrive folder (for example `%ProgramData%\PlayMap\media` or a dedicated volume).
- **Defender exclusions** (file, folder, extension, process) can be added on Home in Windows Security; they affect real-time
  scanning only — scheduled scans still scan. [H] Real-time protection turned off turns itself back on. [H] An exclusion is a
  security trade; the guide offers "exclude the media volume" as the owner's choice, never a default. [M]
- **Dev Drive** (ReFS) is available on all Windows 11 SKUs (22621.2338+), 50 GB minimum; Defender scans it in asynchronous
  **performance mode** when trusted. [H] Microsoft positions it for developer workloads [H]; as a media volume it is an option to
  measure, not a recommendation. [L]

---

## 11. Display on Home

- **Specialized displays** ("Remove display from desktop") are Enterprise and Pro for Workstations (Windows 10 2004+) [H — Insider
  archive], and the custom-compositor documentation states specialized displays "require Windows 10 Enterprise, Windows 10 Pro
  for Workstations, or Windows 10 IoT Enterprise"; a display "may not be designated as HMD or specialized by overriding the EDID
  in software". [H — "Building a Custom Compositor App"] So on Home, `Windows.Devices.Display.Core` exclusive ownership is not
  available for projectors, and every output is a desktop monitor. [H]
- **What still gives tear-free, low-latency output on Home** (edition-neutral): flip-model swap chains; a borderless window that
  covers the output gets DirectFlip and then **independent flip**, equivalent to exclusive full screen, and DWM can keep it with
  MPO when something overlaps [H — "For best performance, use DXGI flip model"; "Demystifying Fullscreen Optimizations"].
  `IDXGISwapChain2::SetMaximumFrameLatency` with a waitable swap chain controls queue depth [H — NVIDIA swap-chain guidance].
  Exclusive full screen on more than one monitor is limited and drops on focus loss, which makes it a poor fit for a desk plus
  outputs on one machine [M]. PlayMap's present design (verify the present mode with PresentMon/ETW) is right on Home unchanged.
- **Desktop-attached risks on Home** and their mitigations [M]: the cursor wanders onto an output (hide it over output windows,
  park it on the desk); another window opens on an output (outputs topmost; desk primary); a projector powering off removes a
  monitor and the desktop re-lays out (EDID emulation — AMD Radeon PRO Settings and NVIDIA RTX PRO drivers offer it, hardware
  EDID emulators for consumer GPUs [H for AMD PRO EDID emulation via 7thSense's AMD guide, M for others]); "Remember window
  locations based on monitor connection" is a Windows 11 setting [H].
- **Vendor features are GPU-class gated, not edition gated**: RTX PRO Sync (formerly Quadro Sync II) supports Windows 10/11 64-bit
  with listed RTX PRO / RTX Ada / RTX A-series / Quadro RTX GPUs, up to 4 GPUs per board, 2 boards, 32 displays, Mosaic with sync,
  NVWMI remote configuration [H]; Mosaic needs RTX PRO/RTX/Quadro [H]; NVAPI Warp and Blend is "available only with NVIDIA Quadro
  GPUs" (the page's OS list is old) [H]; AMD Eyefinity runs on consumer Radeon under Windows 10/11 [H], genlock needs the FirePro
  S400 with Radeon PRO [H — 7thSense's AMD guide]. Nothing found says any of these refuses Home. [M] The owner's "hardware lock
  where possible, fall back to software lock" therefore holds on Home exactly as on Pro: hardware lock needs the pro GPU and board,
  not the edition.

---

## 12. Installers and services on Home

- **MSI** (Windows Installer) is part of every edition; per-machine installs elevate through UAC; services, firewall rules and
  registry are laid the same way. [H for Windows Installer being a system component, M for "identical on Home"]
- **Firewall**: inbound blocked unless a rule matches; rules are per profile (Domain, Private, Public); **Public is the default
  for an unidentified network**. [H — Windows Firewall overview] A show LAN that Windows sees as Public blocks the node unless the
  rules cover Public or the network is set Private (`Set-NetConnectionProfile`). The installer's rules name their profiles and
  preflight reports the active profile per adapter. [H/M]
- **Session 0** rules as §3.2. [H]
- **MSIX**: signed packages install on Home; unsigned MSIX exists only through the "unsigned publisher" OID marker path, with
  limits [H for the marker, L for its constraints]. Not a route for an unsigned PlayMap; the MSI stays.
- **WiX and the owner's answer 4 ("Do not use WIX paid services")** [H — FireGiant docs; the OSMF EULA in the WiX repository]:
  WiX v6+ binaries carry the Open Source Maintenance Fee EULA; v7 enforces EULA acceptance (`-acceptEula wix7`) and exempts those
  under US$10 000 annual revenue; organisations above it must sponsor `wixtoolset`. The EULA "applies only to the Binary Release"
  and does not restrict "self-compiled binaries" built from the MS-RL source. HeatWave and WiX Developer Direct are FireGiant's
  paid products. So "no paid WiX services" is satisfiable two ways: PlayMap uses only the open-source WiX CLI/SDK and either
  qualifies for the exemption or builds WiX from source in CI; it uses no HeatWave and no Developer Direct. `PLAN.md` §9 decision
  3 ("the plan assumes it is paid if it applies") needs rewording to match. [H for the terms, M for the reading]

---

## 13. Remote administration and hardware limits

- Windows Home cannot be a Remote Desktop **host** (it can connect out). [H — "Enable Remote Desktop on your PC"] A Home node is
  administered at the keyboard, through Quick Assist, through a third-party remote tool the owner chooses, or — PlayMap's own
  answer — entirely through the wire's verbs from a desk on another machine (the desk has no private API). [H/M]
- **Physical memory**: Home 128 GB; Pro and Education 2 TB; Pro for Workstations and Enterprise 6 TB. [H — Memory Limits for
  Windows releases] **Sockets/cores**: Home one socket (commonly cited 64 cores), Pro two [M — secondary sources only]. A
  Threadripper PRO or dual-socket render node needs Pro or better. [M]
- Hyper-V and Windows Sandbox are not on Home. [H] Not needed by PlayMap; VBS still runs on Home's hypervisor. [H]

---

## 14. What changes in the round-3 plan

1. **Support tiers** (`docs/INSTALL.md`, `installer.md` §7): IoT Enterprise LTSC 2024 — permanent show nodes (Shell Launcher,
   specialized displays, deferral); Pro / Enterprise 25H2+ — desks and nodes with policies; **Home 25H2+ — runs, unmanaged**
   (no lockdown, no deferral, desktop-attached outputs, 128 GB). S mode — not supported (words on the download page).
2. **Detection** (wp3-platform item 4, `machine.identity`): edition from `GetProductInfo` (`PRODUCT_CORE` 0x65 and the Home
   variants 0x62–0x64) or `EditionID = Core*` [H]; S mode from `WindowsIntegrityPolicy` [H]; SAC state, memory integrity, device
   encryption (per media volume, hardware-accelerated or not), Game Mode, HAGS, OneDrive-backed media paths, the active firewall
   profile per adapter, the primary display, the pause end date [L — no documented read API; omit rather than guess].
3. **Preflight on Home**, as information rows (answer 9's pattern), each naming the edition's limit and the Settings path:
   "updates cannot be deferred on Home: pause until a date after the show (Settings ▸ Windows Update)"; "no kiosk on Home";
   "outputs are desktop monitors on Home: keep the desk's monitor primary"; "Remote Desktop cannot reach this node". **Hard rows**
   (red/amber with words): SAC enforcing while the build is unsigned; a projector output that is the primary display; media that
   are cloud placeholders; Public network profile on the show adapter with no Public rule.
4. **The opt-in show-machine feature** (wp3-installer stretch 6) gets a Home branch: it writes no Windows Update policy on Home
   (the existing amber "updates not deferred" becomes an information row there), and writes only Settings-level values Home
   honours (notifications, DND rules, additional-settings tips, `DisableUserPresenceQos`, Game Bar capture off). Each write is
   read back and reported as written or not (attempts are not facts).
5. **Auto-logon on Home**: the guide's path in §9; the installer either guides to Sysinternals Autologon or stores the LSA secret
   itself; either way the password is in no log, journal, STATE or hello.
6. **Engine QoS** (wp3-platform item 2): add HighQoS and "always honour timer resolution" for the engine process and its deadline
   threads, measured first on the runner.
7. **L-rows to add** (when hardware exists): a clean Home 26H2 install (SAC evaluation, device encryption, OneDrive backup as
   setup leaves them) running the L1/L10 present and decode measures; memory integrity on/off; BitLocker software on/off on the
   media volume; UAC prompt and Ctrl+Alt+Del during playback (what the air shows); a projector power cycle with and without EDID
   emulation.
8. **WiX** wording in `PLAN.md` §9 decision 3 to match answer 4 (§12).

---

## 15. Sources

Microsoft Learn / Support / blogs (primary):

- Assigned Access overview — https://learn.microsoft.com/en-us/windows/configuration/assigned-access/overview
- Shell Launcher overview — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/
- WESL_UserSetting.SetEnabled (Shell Launcher editions) — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetenabled
- Memory limits for Windows releases — https://learn.microsoft.com/en-us/windows/win32/memory/memory-limits-for-windows-releases
- Windows 11 release information — https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information
- Supported versions of Windows client — https://learn.microsoft.com/en-us/windows/release-health/supported-versions-windows-client
- Windows 11 26H2 update history — https://support.microsoft.com/en-us/servicing/os/windows-11/2026/09/windows-11-version-26h2-update-history
- Windows 11 requirements — https://learn.microsoft.com/en-gb/windows/whats-new/windows-11-requirements
- Windows 11 specifications — https://www.microsoft.com/en-US/windows/windows-11-specifications
- S mode FAQ — https://support.microsoft.com/en-us/windows/experience/platform-variants/windows-10-and-windows-11-in-s-mode-faq
- Switching out of S mode — https://support.microsoft.com/en-us/windows/experience/platform-variants/switching-out-of-s-mode-in-windows
- WindowsIntegrityPolicy — https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.windowsintegritypolicy
- Detecting S mode (The Old New Thing, 2025-08-07) — https://devblogs.microsoft.com/oldnewthing/20250807-00/?p=111444
- GetProductInfo — https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getproductinfo
- Pause updates in Windows — https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/pause-updates-in-windows
- Active hours — https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/keep-your-pc-up-to-date-with-active-hours
- Windows Update FAQ — https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/windows-update-faq
- Update Policy CSP — https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-update
- Windows Update client policies — https://learn.microsoft.com/en-us/windows/deployment/update/waas-manage-updates-wufb
- Manage additional Windows Update settings — https://learn.microsoft.com/en-us/windows/deployment/update/waas-wu-settings
- Servicing channels (Home row) — https://learn.microsoft.com/en-us/windows/deployment/update/waas-servicing-channels-windows-10-updates
- "Your Windows update experience just got updated" (2026-04-24) — https://blogs.windows.com/windows-insider/2026/04/24/your-windows-update-experience-just-got-updated/
- "Our commitment to Windows quality" (2026-03-20) — https://blogs.windows.com/windows-insider/2026/03/20/our-commitment-to-windows-quality/
- "What's new in Windows 11: Everyday experiences, reimagined" (2026-10-15) — https://blogs.windows.com/windowsexperience/2026/10/15/whats-new-in-windows-11-everyday-experiences-reimagined/
- Take control of Windows Updates on your DAW PC (2022) — https://devblogs.microsoft.com/windows-music-dev/take-control-of-windows-updates-on-your-windows-11-daw-pc/
- Options to optimize gaming performance in Windows 11 — https://support.microsoft.com/en-us/windows/options-to-optimize-gaming-performance-in-windows-11-a255f612-2949-4373-a566-ff6f3f474613
- Enable memory integrity — https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity
- Smart App Control FAQ — https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions
- App & browser control — https://support.microsoft.com/en-us/windows/security/windows-security/app-browser-control-in-the-windows-security-app
- Smart App Control (developers) — https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview
- Test app signatures with Smart App Control — https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control
- App Control and Smart App Control — https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/appcontrol
- Administrator protection — https://learn.microsoft.com/en-us/windows/security/application-security/application-control/administrator-protection/
- Administrator protection for developers (2025-05-19) — https://blogs.windows.com/windowsdeveloper/2025/05/19/enhance-your-application-security-with-administrator-protection/
- Device Encryption in Windows — https://support.microsoft.com/en-us/windows/security/encryption/device-encryption-in-windows
- BitLocker drive encryption in Windows 11 for OEMs — https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/oem-bitlocker
- Find your BitLocker recovery key — https://support.microsoft.com/en-us/windows/security/encryption/find-your-bitlocker-recovery-key
- Windows security book: hardware-accelerated BitLocker — https://learn.microsoft.com/en-us/windows/security/book/operating-system-security-encryption-and-data-protection
- Announcing hardware-accelerated BitLocker — https://techcommunity.microsoft.com/blog/windows-itpro-blog/announcing-hardware-accelerated-bitlocker/4474609
- Configure Windows to automate logon — https://learn.microsoft.com/en-us/troubleshoot/windows-server/user-profiles-and-logon/turn-on-automatic-logon
- Protecting the automatic logon password — https://learn.microsoft.com/en-us/windows/win32/secauthn/protecting-the-automatic-logon-password
- Sysinternals Autologon — https://learn.microsoft.com/en-us/sysinternals/downloads/autologon
- Sign-in options in Windows — https://support.microsoft.com/en-us/accounts-billing/security/sign-in-options-in-windows
- Pushing passkeys forward (2025-05-01) — https://www.microsoft.com/en-us/security/blog/2025/05/01/pushing-passkeys-forward-microsofts-latest-updates-for-simpler-safer-sign-ins/
- Interactive services — https://learn.microsoft.com/en-us/windows/win32/services/interactive-services
- CreateProcessAsUserW — https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw
- Launching an interactive process from a service (archive) — https://learn.microsoft.com/en-us/archive/blogs/winsdk/how-to-launch-a-process-interactively-from-a-windows-service
- Per-user shell (XP Embedded archive) — https://learn.microsoft.com/en-us/previous-versions/windows/embedded/ms838576(v=winembedded.5)
- Desktops (Winlogon desktop, UAC) — https://learn.microsoft.com/en-us/windows/win32/winstation/desktops
- Quality of Service — https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service
- SetProcessInformation — https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
- timeBeginPeriod — https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
- CreateWaitableTimerExW — https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
- Multimedia Class Scheduler Service — https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- Game Mode API — https://learn.microsoft.com/en-us/windows/win32/api/_gamemode/ ; GetExpandedResourceExclusiveCpuCount — https://learn.microsoft.com/en-us/windows/win32/api/expandedresources/nf-expandedresources-getexpandedresourceexclusivecpucount
- Game Mode (Xbox support text, quoted) — https://support.xbox.com/en-US/help/games-apps/game-setup-and-play/use-game-mode-gaming-on-pc
- Hardware Accelerated GPU Scheduling (2020) — https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/
- D3DKMT_WDDM_2_7_CAPS — https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmdt/ns-d3dkmdt-d3dkmt_wddm_2_7_caps
- Hardware flip queue — https://learn.microsoft.com/en-us/windows-hardware/drivers/display/hardware-flip-queue
- Redesigned graphics settings page (2023) — https://devblogs.microsoft.com/directx/navigating-the-redesigned-graphics-settings-page/
- For best performance, use DXGI flip model — https://devblogs.microsoft.com/directx/dxgi-flip-model/
- Demystifying Fullscreen Optimizations — https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/
- Updates in graphics and gaming (windowed optimizations) — https://devblogs.microsoft.com/directx/updates-in-graphics-and-gaming/
- Power policy settings (Modern Standby → Balanced only) — https://learn.microsoft.com/en-us/windows/win32/power/power-policy-settings
- Customize the power slider — https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider
- Ultimate Performance power policy (TechNet wiki archive) — https://learn.microsoft.com/en-us/archive/technet-wiki/52059.windows-10-the-ultimate-performance-power-policy
- powercfg options — https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options
- SetThreadExecutionState — https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate
- System sleep criteria — https://learn.microsoft.com/en-us/windows/win32/power/system-sleep-criteria
- PowerSetRequest — https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest
- Task idle conditions — https://learn.microsoft.com/en-us/windows/win32/taskschd/task-idle-conditions
- Desktop wakes unexpectedly (maintenance wake) — https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/desktop-wakes-up-unexpectedly-from-sleep-hibernation
- QUERY_USER_NOTIFICATION_STATE — https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
- Windows Backup settings catalogue (DND rules) — https://learn.microsoft.com/en-us/windows/configuration/windows-backup/catalog
- Specialized displays (20H1 Insider archive) — https://learn.microsoft.com/en-us/previous-versions/windows-insider/archive/new-in-20h1
- Building a custom compositor for HMD and specialized monitors — https://learn.microsoft.com/en-us/windows-hardware/drivers/display/specialized-monitors-compositor
- EDID extension for HMD and specialized monitors — https://learn.microsoft.com/en-us/windows-hardware/drivers/display/specialized-monitors-edid-extension
- DisplayManager — https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager
- Enable Remote Desktop (Home cannot host) — https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-allow-access
- Windows Firewall overview — https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/
- WER settings — https://learn.microsoft.com/en-us/windows/win32/wer/wer-settings ; Collecting user-mode dumps — https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps
- Storage Sense — https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/manage-drive-space-with-storage-sense
- OneDrive Files On-Demand — https://support.microsoft.com/en-us/onedrive/save-disk-space-with-onedrive-files-on-demand-for-windows
- Add an exclusion to Windows Security — https://support.microsoft.com/en-au/windows/add-an-exclusion-to-windows-security-811816c0-4dfd-af4a-47e4-c301afe13b26
- Virus and threat protection — https://support.microsoft.com/en-us/windows/security/threat-malware-protection/virus-and-threat-protection-in-the-windows-security-app
- Dev Drive — https://learn.microsoft.com/en-us/windows/dev-drive/ ; Defender performance mode — https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode
- Package identity (unsigned MSIX marker) — https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview

Vendors:

- NVIDIA RTX PRO Sync — https://www.nvidia.com/en-us/design-visualization/solutions/rtx-pro-sync/ ; user guide — https://images.nvidia.com/aem-dam/Solutions/design-visualization/quadro-product-literature/nvidia-rtx-pro-sync-user-guide.pdf
- NVIDIA Mosaic — https://www.nvidia.com/en-us/technologies/nvidia-mosaic/
- NVIDIA Warp and Blend — https://developer.nvidia.com/warp-and-blend
- NVIDIA swap-chain guidance — https://developer.nvidia.com/blog/advanced-api-performance-swap-chains/
- AMD Eyefinity — https://www.amd.com/en/resources/support-articles/faqs/DH3-014.html
- AMD Software: PRO Edition release notes (OS support) — https://drivers.amd.com/relnotes/amd_software_pro_software_25.q4_full_set.pdf
- AMD FirePro / Radeon PRO display setup (EDID emulation, S400 genlock; 7thSense) — https://portal.7thsense.one/user-guides/pdf-library/Display%20configuration/M048-7%20AMD%20Displays.pdf
- WiX Open Source Maintenance Fee — https://docs.firegiant.com/wix/osmf/ ; OSMF EULA — https://github.com/wixtoolset/wix/blob/v6.0.0/OSMFEULA.txt ; introduction issue — https://github.com/wixtoolset/issues/issues/8974

Secondary (used only where marked [M] or [L]):

- Windows Latest on OOBE local accounts (2025-10-07) — https://www.windowslatest.com/2025/10/07/microsoft-confirms-windows-11-to-require-microsoft-account-internet-during-oobe-tested/
- PCWorld on the same — https://www.pcworld.com/article/2932975/microsoft-kills-two-more-ways-to-install-windows-11-with-local-accounts.html
- Tom's Hardware on hardware-accelerated BitLocker and software overhead — https://www.tomshardware.com/software/windows/microsoft-forced-slow-software-accelerated-bitlocker-on-windows-but-expects-to-nearly-double-storage-performance-and-improve-battery-life-with-new-cpu-hardware-accelerated-crypto-new-feature-requires-new-cpus-that-arent-available-on-the-market-yet
- BleepingComputer on hardware-accelerated BitLocker — https://www.bleepingcomputer.com/news/security/microsoft-rolls-out-hardware-accelerated-bitlocker-in-windows-11/
- Neowin / MakeUseOf on SAC re-enable (Feb 2026) — https://www.neowin.net/news/this-windows-11-feature-no-longer-requires-clean-installing-the-system-to-activate-it/ ; https://www.makeuseof.com/turn-off-smart-app-control-without-reinstalling-windows/
- Thurrott on OneDrive folder backup in Home setup — https://www.thurrott.com/windows/windows-11/305040/microsoft-is-silently-reversing-some-onedrive-enshittification-in-windows-11
- ZDNET on OneDrive backup in 25H2 — https://www.zdnet.com/article/how-to-undo-onedrive-backup-windows-11/
- Spiceworks on WSUS/policy values on Home — https://community.spiceworks.com/t/wsus-on-home-clients/1155443
- Microsoft Q&A archive "does not work on Home versions" — https://learn.microsoft.com/en-us/archive/msdn-technet-forums/1038ba2f-5366-4d33-9a52-479dc1280729
- Geek Rewind / Eleven Forum / TenForums on specialized displays and Ultimate Performance — https://www.elevenforum.com/t/turn-on-or-off-remove-display-from-desktop-in-windows-11.7632/ ; https://www.tenforums.com/tutorials/107613-add-remove-ultimate-performance-power-plan-windows-10-a.html
- Windows Central on Automatic Maintenance — https://www.windowscentral.com/how-manage-automatic-maintenance-windows-10
- Computerworld on the "finish setting up" settings — https://www.computerworld.com/article/2096419/10-ways-to-turn-off-windows-worst-ads.html
- XDA on HAGS — https://www.xda-developers.com/what-is-hags-windows-turn-it-on/
- Pureinfotech on Home core/socket limits — https://pureinfotech.com/upgrade-windows-11-home-pro/
