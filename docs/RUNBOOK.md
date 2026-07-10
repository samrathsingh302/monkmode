<!--
    Copyright (C) 2026 Samrath Singh

    This file is part of MonkMode, a fork of Cold Turkey.
    Source: https://github.com/samrathsingh302/monkmode

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
-->

# MonkMode — Operator Runbook

The document for when things are *weird* or need surgery — the opposite of the
happy-path lifecycle in `USER-GUIDE.md`. This is for the operator (Samrath,
admin on his own machine): the residuals you live with, how to read the
machine's state without arming anything, how to remove MonkMode completely, and
the operational footguns that have actually bitten.

**This runbook complements `USER-GUIDE.md`, it does not repeat it.** For the
command reference (setup, block, schedule, presets, cooling-off, the partner
code, stats), and for the *intended* exits from an active block, read the user
guide first. This document assumes you already know those and picks up where
they stop: diagnosis, forced removal, and the honest residual register.

Every factual claim below is cited to code (`file:line`) in the evidence
footnote at the end. Paths are relative to the app folder — the folder the four
executables run from (`AppContext.BaseDirectory`, i.e. `dist\` today) [E1].

> **Read-only fences still apply.** Nothing in this runbook should be run during
> dev or audit. The runbook *instructs the operator* to run diagnostic and
> teardown commands — that is its job — but those are live-machine actions.
> `sc query`, `Get-Service`, `reg query`, and reading the hosts file are
> read-only and safe. `monkmode unblock --force` and `sc delete` are
> destructive and mutate the live machine — run them only when you mean to.

---

## 1. Residual risk register — honest

MonkMode is **impulse-proof, not admin-proof**. You keep Administrator rights
and physical disk access on your own single machine, so the residuals below are
*accepted by design*, not bugs to be filed. The full bypass table (B1–B11,
ranked by effort, with live-verification evidence) is in
`ARCHITECTURE.md` §4; this section is the operator's short list of what you are
actually living with.

### 1.1 The offline attack (B10) is out of scope — by design

Boot from USB / WinRE / another OS, mount the disk, and edit the hosts file or
delete the service binary and its registry keys, and **nothing on the same
unencrypted disk can stop you** [E2]. This is not a hole to be closed in this
codebase — it is the honest ceiling. No software on a disk its owner fully
controls can be made truly unremovable. B10 is a Medium-effort, always-wins
residual and is documented as such rather than hidden.

### 1.2 No BitLocker integration — deliberately outside this codebase

MonkMode does **not** integrate with full-disk encryption, and that is a
conscious SKIP decision (D7), not an oversight [E3]. The consequence is direct:
**if the disk is unencrypted, the offline nuke (1.1) is trivial** — any live CD
edits hosts in seconds. BitLocker would raise the effort of the offline attack
substantially, but disk encryption is the operating system's job, not
MonkMode's. Closing B10 for real needs measures *outside* this codebase and
skipped by design: a non-admin daily account, full-disk encryption (BitLocker),
and a BIOS/boot-order lock [E3]. If you want the offline nuke to be hard, that
is where the effort goes — not into MonkMode.

### 1.3 The `unblock --force` escape hatch is retained on purpose

`monkmode unblock --force` unconditionally tears any block down and removes the
service [E4]. Despite the R1 exit model's "removed" wording, it is **kept
deliberately as brick-insurance**: a fail-closed bug or a dead DPAPI store must
never be able to trap the machine permanently, so the one guaranteed way out is
kept and documented rather than concealed. It is admin-only and gated behind an
explicit `--force`, so it can never be a casual one-word bypass — but an admin
who *wants* out can always take it. This is the honest ceiling in practice.
Full teardown behaviour is Section 3.

### 1.4 Everything self-healing chains to B1

Every per-tick self-heal — the hosts marker block (B2), the SafeBoot
registration (B3), the browser DoH-off policy (B5a), and the deny-DELETE service
ACE (B6) — is re-asserted by the *service* on its ~10 s timer, fail-closed [E5].
**If the service and its guardian both stay dead, all of those self-heals stop.**
So every residual chains back to B1 (force-kill the service *and* keep it dead):
defeat it and hosts edits stick, the SafeBoot keys stay deleted, DoH toggles
back on, and `sc delete` succeeds. B1 itself is mitigated to Medium (SCM
auto-restart + the SYSTEM-session guardian that reciprocally restarts the
service) [E6], and its own honest residuals are: a scripted near-simultaneous
double-kill of service+guardian inside the ~1 s / ~10 s restart windows, a
SYSTEM-token kill that also disables recovery (`sc failure … reset= 0`),
suspend-then-kill of both, or an *elevated* user pre-pinning their own
`mm_guard.exe` to hold the single-instance mutex [E6]. True kill-immunity would
need a PPL/kernel driver — out of scope, B10-tier.

**Operator takeaway:** when reasoning about "can attack X lift the block?", the
answer is almost always "only by first winning B1 (double-kill / recovery-disable
/ guardian pre-pin) or going offline (B10)". Do not attempt to close B10 or B11
in software — B11 (fixed identifiers like `MONKMODE`) is an *enabler*, not an
independent bypass, and every scripted teardown it eases hits the exact same
fail-closed gate a manual one hits [E7].

---

## 2. Diagnosis playbook — read the machine without arming anything

Everything here is **read-only**. None of it arms, lifts, or mutates a block.
Use it to answer "what state is this machine in?" before deciding whether
surgery is needed. **Never** pipe or capture `monkmode block` output as a probe
(Section 4.1) — to check live state use `monkmode status`, which is read-only.

### 2.1 Is the service there, and what is it doing?

```
sc query MONKMODE
Get-Service MONKMODE          # PowerShell equivalent
```

- **`RUNNING`** — the service is alive. If a block is genuinely active it is
  also carrying a deny-DELETE ACE, so `sc delete` will be refused (that is B6,
  by design, not a fault) [E8].
- **`STOPPED` but the query still returns a service** — this is what a **genuine
  expiry looks like**. When a block reaches its real end (timer, cooling-off, or
  a correct partner code) the service strips the hosts block, removes its
  protections, and **stops itself**, but the service *registration remains
  installed and idle** [E9]. This is normal, not a stuck state.
- **Service absent entirely** — MonkMode was removed (via `unblock --force`, or a
  manual `sc delete` while idle). Nothing is being enforced by the service.

The authoritative one-command read is:

```
monkmode status
```

It reports the active block and the current exit path if one is armed, or prints
**`MonkMode: no active block (service installed but idle).`** when the service
registration exists but nothing is enforced [E10]. `status` is read-only and
writes nothing.

**Do not use "service STOPPED" alone as the lift signal.** A block genuinely
lifts when the service is *not Running* **or** the hosts marker block is gone
(2.3) — poll that condition, never a captured block-arm.

### 2.2 What removes the service registration?

Only `monkmode unblock --force`, or a manual `sc delete MONKMODE` while the
service is **idle** (no active block — the deny-DELETE ACE is off at expiry)
[E9][E11]. During an active block, `sc delete` is refused (B6). See Section 3.

### 2.3 The hosts marker block

Site blocking is machine-wide hosts sinkholing. The MonkMode entries live in the
system hosts file between a marker:

```
C:\Windows\System32\drivers\etc\hosts
```

Look for the marker line `#### MonkMode Entries ####` [E12]. If it is present,
sites are being sinkholed; if it is absent, no MonkMode site block is in the
hosts file (the block has lifted, or none was armed). **Only ever touch the
marker block** if you edit by hand — the user's own hosts content sits outside
it and must be preserved byte-for-byte (the same no-data-loss rule the code
follows). While a block is unexpired and the service is alive, the service
re-asserts read-only and restores this block from its snapshot within ~10 s of
any edit (B2 self-heal) [E5], so a hand edit will not stick until the service is
gone.

### 2.4 The notifier autorun (HKCU Run)

The user-session notifier is registered to auto-start under the **arming user's**
`HKCU` Run key:

```
reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v MonkMode_notify
```

Hive `HKEY_CURRENT_USER`, subkey
`SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, value name **`MonkMode_notify`**,
pointing at `mm_notify.exe` [E13]. Because it is under the *arming user's* HKCU,
a *different* Windows account gets the machine-wide hosts block but not the
app-kill notifier (that is the B9 residual).

### 2.5 The SafeBoot registration (HKLM)

While a block is active the service registers itself to run in Safe Mode under
two leaf keys (each `(Default)=Service`):

```
reg query "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE"
reg query "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\MONKMODE"
```

Both are present during a block, self-healed each tick, and removed at a genuine
expiry [E14]. If you see these two keys but the service is gone and no block is
active, they are harmless orphans (e.g. left by an `sc delete` mid-block) —
`unblock --force` removes them, or delete the two leaf keys by hand.

### 2.6 The guardian process

The SYSTEM-session watchdog is `mm_guard.exe` (process name `mm_guard`). It is
spawned by the service and holds a single-instance mutex `Global\MonkModeGuardian`
[E15]. Check for it with:

```
Get-Process mm_guard -ErrorAction SilentlyContinue
tasklist /svc | findstr /i mm_guard
```

A running `mm_guard.exe` with **no** active block and **no** service is an
**orphan** from an interrupted smoke or loop — see the build-lock gotcha
(Section 4.3).

### 2.7 Where the MAC'd config, the setup file, and the stats live

All in the app folder (next to the exes) [E1]:

| Artefact | File | Protected? | What it holds |
|---|---|---|---|
| Enforcement config | `monkmode_settings.ini` | HMAC-SHA256, schema `v9` | Block deadline, HighWater, process list, exit-model fields [E16] |
| Config shadow backup | `monkmode_settings.ini.bak` | MAC-covered copy | Restore source if the primary is structurally corrupt (B8/R8) [E17] |
| Setup config | `monkmode_setup.ini` | HMAC, schema `s4` | Account defaults: partner, cooloff seconds, default site/app lists [E18] |
| Hosts snapshot | `monkmode_hosts.block` | (plain) | Exact marker block the service restores hosts from (B2) [E19] |
| DoH snapshot | `monkmode_doh.snapshot` | (plain) | User's prior browser DoH policy, captured at block start (B5a) [E20] |
| Stats | `monkmode_stats` | **non-MAC by design** | Display-only counts, zero enforcement authority [E21] |
| Cooling-off / code triggers | `monkmode_cooloff.request`, `monkmode_cooloff.cancel`, `monkmode_partner.code` | (plain, presence/candidate only) | Requests the service adjudicates [E22] |

**The DPAPI key that keys the MAC is not a separate store.** It lives *inside*
`monkmode_settings.ini` as the `[Integrity] Key` value — a random 32-byte key
DPAPI-protected at **LocalMachine** scope [E23]. There is no separate keystore
file to inspect; if DPAPI at LocalMachine scope is broken, the MAC can't be
validated and the block freezes fail-closed (Section 3.3).

### 2.8 Event-log breadcrumbs — there are none

**MonkMode writes no Windows Event Log entries, no debug trace, and no `.log`
file.** The service contains no `EventLog` writes [E24], and tracing is compiled
out (`<DefineTrace>false</DefineTrace>` in all four projects) so the inherited
`Trace.WriteLine` calls become no-ops and never leak config values [E7]. **Do
not go looking in Event Viewer for a MonkMode breadcrumb trail — it does not
exist.** Diagnosis is by *state inspection* (Sections 2.1–2.7), not by logs. The
only runtime output is the CLI's own `Console` text while you run a command.

---

## 3. Full-uninstall how-to

The intended way out of an *active* block is one of the user-facing exits
(cooling-off, the partner code, or the timer — `USER-GUIDE.md` §6). This section
is the **manual removal surgery** for when you want MonkMode gone entirely, or
when it is stuck.

### 3.1 The clean removal when no block is active

When `monkmode status` reports *no active block (service installed but idle)*
(2.1), the service is idle and its deny-DELETE ACE is already off, so the service
is removable normally:

```
sc delete MONKMODE
```

Then remove the app folder. For a Program Files install (via `tools\install.ps1`),
`tools\uninstall.ps1` automates the whole file-level teardown once idle — it
`sc delete`s the stopped service, removes the install dir, the machine `PATH`
entry and the current user's notifier autorun, and keeps your data unless you pass
`-PurgeData` (`USER-GUIDE.md` §9). It is fail-closed: it refuses if anything is
still enforcing, so it is never an escape hatch. The manual path below remains the
fallback (and the only route for a plain `dist\` folder that was never installed):
delete the folder by hand after the service is gone. `monkmode unblock --force`
also works when idle and additionally cleans up the artefacts below.

### 3.2 The forced teardown (`unblock --force`) — what it removes

`monkmode unblock --force` is the complete teardown for an *active* block or a
stuck one. It runs these steps, in order, **best-effort per step** — a failure in
one step is reported and the teardown continues rather than aborting [E4][E25]:

1. **Disable SCM recovery** on `MONKMODE` so the kills in step 2 actually stick
   (B1 layer 1 off) [E4].
2. **Kill the watchdog pair and notifier** — guardian first, then service, then
   `mm_notify` — retrying until both stay down [E4].
3. **Remove the deny-DELETE ACE, then delete the service** — restores the default
   service security descriptor (so the object can be opened for DELETE), then
   `DeleteServiceByName`. If the ACE restore hard-fails it is retried once and
   the delete is skipped with an actionable message rather than buried under a
   misleading AccessDenied [E4].
4. **Restore the hosts file** — strip *only* the MonkMode marker block; user
   content preserved byte-for-byte [E4].
5. **Delete the hosts snapshot** `monkmode_hosts.block` [E4].
6. **Delete the config shadow backup** `monkmode_settings.ini.bak` [E4].
7. **Delete the cooling-off + partner-code triggers** (`monkmode_cooloff.request`,
   `monkmode_cooloff.cancel`, `monkmode_partner.code`) [E4].
8. **Remove the two SafeBoot leaf keys** (HKLM Minimal + Network) [E4].
9. **Restore the browser DoH policy** from `monkmode_doh.snapshot` (or remove our
   lingering "off"), then consume the snapshot [E4].
10. **Clear the notifier autorun** — delete the `MonkMode_notify` value from HKCU
    Run [E4].

### 3.3 What `--force` leaves behind — verified

`--force` does **not** delete everything. Verified against the teardown code
(there is no `File.Delete` for these artefacts anywhere in the CLI) [E26], the
following **survive** a forced teardown and must be removed by hand if you want a
truly clean slate:

- **`monkmode_settings.ini`** — the enforcement config file itself is left in
  place. Harmless: the service that read it is gone, and the next `monkmode
  block` overwrites it. Delete it by hand only if you are wiping the folder.
- **`monkmode_setup.ini`** — your account defaults (partner label, cooloff
  seconds, default site/app lists) **survive**, so a reinstall keeps them. This
  is effectively a feature — but delete it if you want setup to start blank.
- **`monkmode_stats`** — your block history (counts only) **survives**. Delete it
  by hand to reset the history.
- **The `dist\` folder and the four executables** — MonkMode cannot delete its
  own running binaries, so `--force` never touches them. Remove the folder
  yourself after the service is gone (the exes are unlocked once the service and
  guardian processes have exited — confirm with 2.6).

Note the `monkmode_doh.snapshot` is *consumed* by step 9 (restore-then-delete),
so it is normally gone after `--force` — but if step 9 was skipped (best-effort),
it may linger and is safe to delete by hand.

After a successful `--force`, `sc query MONKMODE` returns *service does not
exist* and the hosts marker (2.3) is gone. That pair is your "it's off" signal.

### 3.4 The stuck / bricked variant

Symptom: the `MONKMODE` service exists and refuses the normal exits — the config
store is dead. Causes: DPAPI at LocalMachine scope is unreadable (so the MAC
can't be validated), a tampered (MAC-invalid) config that froze fail-closed, or a
structurally corrupt primary ini with no valid backup [E23][E27]. In all of
these the block **holds fail-closed on purpose** — no error path may ever lift a
block — so cooling-off and the partner code cannot help.

**The way out is `monkmode unblock --force`.** It is *unconditional*: the
teardown in 3.2 does **not** read or validate the MAC, does not require DPAPI,
and strips the hosts block by its literal marker text — so a dead config store
does not block it [E4]. That is exactly why the escape hatch is retained as
brick-insurance (1.3): the guaranteed exit survives even a frozen config.

If even `--force` cannot delete the service (something is re-denying the DACL, or
the machine is otherwise wedged), the honest floor is the offline route (B10):
boot from WinRE / a live USB, mount the disk, delete the `MONKMODE`
`HKLM\SYSTEM\CurrentControlSet\Services\MONKMODE` key and the app folder, and
strip the marker block from the offline hosts file. On an unencrypted disk this
always works (1.1) — it is the same residual the whole design accepts, now used
as the recovery of last resort.

---

## 4. Known operational gotchas

Real, live-observed footguns. The first three have bitten; internalise them
before scripting or running a smoke.

### 4.1 Never pipe or capture `monkmode block` output (pipe-wedge)

The notifier (`mm_notify`) inherits the CLI's stdout, so **redirecting or
capturing** the output of `monkmode block` (`| tee`, `> log.txt`, `$(...)`,
backticks, a captured subprocess) leaves the calling shell **wedged until the
block expires** [E28]. Run `block` with its output going straight to the
terminal — never through a pipe or capture. This is live-proven (10/07/2026); a
proper fix is pending as **P2**. Two corollaries for diagnosis:

- To poll for a lift, watch state (service not-Running **or** hosts marker gone,
  per 2.1/2.3) — **never** a captured block-arm.
- "Expiry" of a block leaves the service *Stopped but still installed*, not gone
  (2.1) — do not treat a surviving service registration as a wedge.

### 4.2 Never arm a block across a binary upgrade (forward-migration MAC freeze)

The enforcement config carries a **compile-time** schema version (`v9`) as its
first MAC-covered line. A block armed under **older** binaries fails the MAC when
read by **newer** binaries and **freezes fail-closed** — it keeps enforcing and
will not auto-lift [E16][E29]. **Arm blocks *after* upgrading the binaries, not
across an upgrade.** If you are rebuilding: let any live block end first, then
rebuild `dist\`, then arm. If you are already stuck in a forward-migration
freeze, that is a "config store dead" case — recover with `unblock --force`
(3.4).

### 4.3 Orphaned guardian holding a build file lock

A `dotnet build`/`test` that fails with MSB3021/MSB3027 *"Could not copy
mm_guard.dll … The file is locked by: MonkMode Guardian (PID)"* is almost always
an **orphaned `mm_guard.exe`** left by a torn-down or interrupted smoke / loop
iteration — *not* a live block [E30]. The guardian "exits only on genuine
expiry", so an interrupted teardown can strand it holding a lock on
`mm_guard.dll` in whatever bin it launched from (often
`MonkMode.Tests\bin\Release\...`), which blocks MSBuild's copy step.

**Before killing it, confirm nothing is being enforced** (this is the fence):
`Get-Service MONKMODE` absent, the hosts file has no `MonkMode` marker (2.3), no
`monkmode_settings.ini` in the launch bin, no HKCU `Run` entry (2.4). **All clear
⇒ no active block ⇒** `Stop-Process -Id <pid> -Force` (the owner is usually a
user-level process, killable from a non-elevated shell). Re-check that nothing
respawned, then rebuild. **Never kill the guardian if a real block IS active** —
that is defeating your own enforcement.

---

## See also

- `USER-GUIDE.md` — the command-first lifecycle (setup, block, schedule,
  presets, cooling-off, the partner code, stats, and the happy-path removal).
- `README.md` — the exit model, the honest ceiling, and engineering notes.
- `ARCHITECTURE.md` (project vault, `vault/dev/monk-mode/specs/`) — the full
  bypass surface B1–B11, ranked by effort, with live-verification evidence.

---

## Evidence footnote

Every claim in this runbook is grounded in current code (or the canonical spec
where noted). Cites are `file:line` at the time of writing.

- **[E1]** App folder = `AppContext.BaseDirectory` — `MonkMode\Blocker.vb:109-111`
  (`AppDir()`).
- **[E2]** B10 offline attack, always wins on an unencrypted disk —
  `ARCHITECTURE.md:282` (bypass table B10) + §5 :304-306.
- **[E3]** No BitLocker; B10 closure needs measures outside this codebase, D7
  SKIP — `ARCHITECTURE.md:311-315` (§5).
- **[E4]** `unblock --force` teardown, unconditional + best-effort per step —
  `MonkMode\Program.vb:642-751` (`DoUnblock` `--force` branch, steps 1–10 at
  :686-747); step semantics `Step_` at :767-777.
- **[E5]** Per-tick self-heals fail-closed (hosts B2) — `ARCHITECTURE.md:274`
  (B2 row); DoH/SafeBoot/ACE self-heal pattern :275, 277, 278.
- **[E6]** B1 mitigation (SCM recovery + guardian) and its honest residuals —
  `ARCHITECTURE.md:273` (B1 row).
- **[E7]** B11 accepted-as-is (enabler, not independent bypass); no secret leaks,
  `<DefineTrace>false</DefineTrace>` in all four projects — `ARCHITECTURE.md:283`
  (B11 row).
- **[E8]** Deny-DELETE ACE while active (B6) — `ARCHITECTURE.md:278` (B6 row);
  `USER-GUIDE.md` §9.
- **[E9]** Genuine expiry = service stops itself, registration stays installed —
  `USER-GUIDE.md` §7 ("What a genuine expiry looks like"); `ARCHITECTURE.md:60-62`
  (`stopMe()` strips hosts + `End`s).
- **[E10]** `status` idle string — `MonkMode\Program.vb:425` ("no active block
  (service installed but idle).").
- **[E11]** `sc delete` while idle removes it; refused during a block —
  `USER-GUIDE.md` §9.
- **[E12]** Hosts marker `#### MonkMode Entries ####` — `ARCHITECTURE.md:20`,
  :283 (B11 identifiers).
- **[E13]** HKCU Run, value `MonkMode_notify` — `MonkMode\Blocker.vb:84`
  (`RunValueName`), registration :791-792, clear :1865-1866.
- **[E14]** SafeBoot Minimal + Network leaf keys — `MonkMode\Blocker.vb:96-97`
  (`SafeBootMinimalKey`/`SafeBootNetworkKey`); lifecycle `ARCHITECTURE.md:275`
  (B3 row).
- **[E15]** Guardian process `mm_guard`, single-instance mutex
  `Global\MonkModeGuardian` — `MM_guard\MM_guard\Program.vb:85`;
  `MonkMode\Blocker.vb:90` (`GuardProcessName`).
- **[E16]** Enforcement config `monkmode_settings.ini`, schema `v9`, compile-time
  first MAC-covered line — `MonkMode\Blocker.vb:50` (`IniName`);
  `MonkMode\ConfigIntegrity.vb:81` (`CurrentSchemaVersion = "v9"`);
  `ARCHITECTURE.md:135-143` (§3a enforcement canonical).
- **[E17]** Config shadow backup `monkmode_settings.ini.bak` (R8) —
  `MonkMode\ConfigBackup.vb:65` (`BackupFileName`); `MonkMode\Blocker.vb:122-124`
  (`IniBackupPath`).
- **[E18]** Setup config `monkmode_setup.ini`, schema `s4` —
  `MonkMode\Blocker.vb:1287` (`SetupIniName`), :1296 (`SetupSchemaVersion = "s4"`);
  `ARCHITECTURE.md:144-148`.
- **[E19]** Hosts snapshot `monkmode_hosts.block` — `MonkMode\Blocker.vb:51`
  (`SnapshotName`).
- **[E20]** DoH snapshot `monkmode_doh.snapshot` — `MonkMode\Blocker.vb:54`
  (`DohSnapshotName`); B5a `ARCHITECTURE.md:277`.
- **[E21]** Stats `monkmode_stats`, non-MAC, display-only —
  `MonkMode\Stats.vb:45,52` (`StatsFileName`, `StatsPath`); `USER-GUIDE.md` §8.
- **[E22]** Trigger files (cooloff request/cancel, partner code) —
  `MonkMode\Blocker.vb:61-62,69`.
- **[E23]** DPAPI key inside the ini as `[Integrity] Key`, LocalMachine scope —
  `MonkMode\ConfigIntegrity.vb:355,370` (`ProtectedData.Protect/Unprotect …
  DataProtectionScope.LocalMachine`); `MonkMode\Blocker.vb:103` (`IntegrityKeyName`).
- **[E24]** No `EventLog` writes in the service — grep of
  `MonkMode_srv\MonkMode_srv\Service1.vb` for `EventLog`/`WriteEntry` returns no
  matches (verified at time of writing).
- **[E25]** Best-effort per step — `MonkMode\Program.vb:767-777` (`Step_`
  swallows + reports, returns success).
- **[E26]** No `File.Delete` of the ini / setup ini / stats anywhere in the CLI —
  grep of `MonkMode\` for `Delete.*IniPath` / `SetupIni` / `Stats` returns no
  matches; the `--force` step list (E4) omits them.
- **[E27]** Corrupt/blank/short ini holds fail-closed; tampered (MAC-invalid)
  freezes and is never "recovered" — `ARCHITECTURE.md:280` (B8 row, C1b backup).
- **[E28]** Pipe-wedge (notifier inherits stdout) — `USER-GUIDE.md` §10;
  auto-memory `monkmode-block-arm-pipe-wedge`. Live-proven 10/07/2026.
- **[E29]** Forward-migration MAC freeze (arm after upgrading) — `USER-GUIDE.md`
  §10; `ARCHITECTURE.md:279` (B7 row, C1 extension), :142-143.
- **[E30]** Orphaned guardian build-lock (MSB3021/MSB3027) — auto-memory
  `guardian-build-lock-drill` (verify-no-live-block then `Stop-Process`).
