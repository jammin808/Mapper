# L23 — a Windows 11 Home node (wp3-platform's part; the install's is wp3-installer's, `docs/round3/lab/L23.md`)

**Status: not run** (no lab this round, D15).

**Machine.** A clean Windows 11 Home 26H2 install as its setup leaves it: a Microsoft account, Smart App Control evaluating, device
encryption on, OneDrive folder backup on.

**Pass mark (the platform's part).** Preflight's edition rows and hard rows each as their facts say; every claim answered as on Pro
(or refused with its words); an hour's show with L1's present and decode measures beside a Pro machine's; Memory Integrity on and
off, and the media volume encrypted and not, each with its frame times.

## Script

1. Before installing: Smart App Control's state (Windows Security ▸ App & browser control) — it should read evaluating.
2. Install the build with its installer, by the installer's paper, `docs/INSTALL.md`, as steps 2–5 of `docs/round3/lab/L23.md`
   (wp3-installer's part of this line) do, with Smart App Control still evaluating: the installer shows its warning page and installs.
   Start the show account's session. Turn Smart App Control off (that script's step 1) only once step 3 below is recorded: its amber
   row needs it evaluating, and it cannot be turned back on.
3. `PREFLIGHT QUICK`, section machine. Expect, and record each against the screen:
   - grey **i** rows: updates cannot be deferred; no kiosk; outputs are desktop monitors; Remote Desktop cannot reach this node;
   - amber: Smart App Control evaluating while the build is unsigned, with its words;
   - amber: a projector output on the primary display (make a projector primary in Settings ▸ System ▸ Display to see it, then put
     the desk's monitor back and see it go);
   - amber: the show network on Public (Settings ▸ Network & internet ▸ the network ▸ Network profile type) with no rule;
   - the OS edition row "Windows 11 Home 26H2 · … · supported, unmanaged" and no servicing row (26H2 is serviced).
4. `MACHINE`: every claim's answer; compare with a Pro machine's on the same hardware.
5. An hour's show with L1's present and decode measures; repeat with Memory Integrity off, and with the media volume decrypted.
6. `MACHINE LOCK`, then let Windows Update install a driver; preflight's drift row says the node observes the change (Home cannot
   hold drivers back).
7. Turn Smart App Control on enforcing (it cannot be turned back on once off — use a second clean install): record Windows' block of
   the unsigned build and event 3077 in `Microsoft-Windows-CodeIntegrity/Operational` (`docs/round3/design/installer.md` §6).

## Record

Each row as shown, beside the screen it names; the claims' answers; frame times per configuration.
