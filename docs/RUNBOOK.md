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
command reference (setup, block, schedule, presets, the partner
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
> read-only and safe. `sc delete` is destructive and mutates the live machine —
> run it only when you mean to, and only while idle.

---

## 1. Residual risk register — honest

MonkMode is **impulse-proof, not admin-proof**. You keep Administrator rights
and physical disk access on your own single machine, so the residuals below are
*accepted by design*, not bugs to be filed. The full bypass table (B1–B14,
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

### 1.3 There is no escape hatch — removed 30/08/2026 (ledger 319)

`monkmode unblock --force` used to tear any block down unconditionally and remove
the service. It was **removed**, along with the self-serve cooling-off wait, at
Samrath's instruction on 30/08/2026: *"i dont like how i can force unblock it
regardless ... i should only be able to unblock with code."* A running block now
ends on exactly two events — its own end time, or a service-verified partner code.

The removal is a removal, not a hiding. `monkmode.exe` no longer contains any code
path that can disable SCM recovery, kill the watchdog pair, remove the deny-DELETE
ACE, delete the service, strip the hosts block or clear the SafeBoot registration:
those primitives were deleted from `Blocker.vb` and `ServiceTools.vb`, and the
`DeleteService` P/Invoke went with them. `--force` and `--cancel` are now reported
as unknown options.

**What that costs, stated plainly.** The escape hatch was retained for four
releases as brick-insurance, and the brick case is real: a dead DPAPI store or a
MAC-invalid config freezes fail-closed and can now be lifted by *nothing* — not
the end time, and not the partner code either, because the code is verified
against a config the service will not trust. Such a block holds indefinitely and
only the offline route (B10, 3.4) gets out. That trade was made knowingly.

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
  expiry looks like**. When a block reaches its real end (its timer, or a correct
  partner code) the service strips the hosts block, removes its
  protections, and **stops itself**, but the service *registration remains
  installed and idle** [E9]. This is normal, not a stuck state.
- **Service absent entirely** — MonkMode was removed by a manual `sc delete`
  while idle (the only route since ledger 319). Nothing is enforced by the service.

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

Only a manual `sc delete MONKMODE` while the service is **idle** (no active
block — the service re-grants DELETE on its own genuine-expiry teardown)
[E9][E11]. During an active block, `sc delete` is refused (B6). Ledger 319
removed the other route (`unblock --force`). See Section 3.

### 2.3 The hosts marker block

Site blocking is machine-wide hosts sinkholing. The MonkMode entries live in the
system hosts file between a marker:

```
C:\Windows\System32\drivers\etc\hosts
```

Look for the marker line `#### MonkMode Entries ####` [E12]. If it is present,
sites are being sinkholed; if it is absent, no MonkMode site block is in the
hosts file (the block has lifted, or none was armed). Since v1.1 the block is
**closed** by `#### MonkMode End ####` on its own line, so MonkMode's region is
exactly those two marker lines and everything between them; anything below the
end marker is your own content and MonkMode leaves it alone (F35). **Only ever
touch the marker block** if you edit by hand — the user's own hosts content sits
outside it and must be preserved byte-for-byte (the same no-data-loss rule the
code follows). A block written by a pre-v1.1 build has no end marker; it is
treated as running to the end of the file until the next write closes it. If an
end-marker line is ever injected *inside* the block, the entry lines below it are
demoted to your own content: they survive the lift as stray `127.0.0.1` lines and
need deleting by hand. That is tamper-only (MonkMode writes exactly one end
marker, at the bottom) and it over-blocks rather than under-blocks. While a block is unexpired and the service is alive, the service
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
The service removes them at a genuine expiry; otherwise delete the two leaf keys by hand.

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
| Enforcement config | `monkmode_settings.ini` | HMAC-SHA256, schema `v12` | Global header (HighWater, Now, TrustedUtc, SlotCount, NextSlotId, Guard fields, the global `[Schedule]` pair) + one `[SlotN]` group per block: its Until/StartAt, sites, apps, URL patterns and partner fields [E16]. The `CoolOffUntil` / `CoolOffDuration` keys are still in the canonical and still MAC-covered, but since ledger 319 nothing writes them and nothing reads them to decide a lift — they are kept only to avoid a schema bump |
| Config shadow backup | `monkmode_settings.ini.bak` | MAC-covered copy | Restore source if the primary is structurally corrupt (B8/R8) [E17] |
| Setup config | `monkmode_setup.ini` | HMAC, schema `s4` | Account defaults: partner, cooloff seconds, default site/app lists [E18] |
| Hosts snapshot | `monkmode_hosts.block` | (plain) | Exact marker block the service restores hosts from — the **union** over every contributing slot (B2) [E19] |
| DoH snapshot | `monkmode_doh.snapshot` | (plain) | User's prior browser DoH policy, captured at block start (B5a) [E20] |
| Stats | `monkmode_stats` | **non-MAC by design** | Display-only counts, zero enforcement authority [E21] |
| Counter sidecars | `%ProgramData%\MonkMode\stats-service.ini`, `…\stats-notify.ini` | **non-MAC by design**, one writer each | Per-block app-kill counts (service) and URL-redirect counts (notifier). Outside the app folder because Program Files is admin-write-only and the notifier is not elevated. Survive retire and teardown [E31] |
| Code / add triggers | `monkmode_partner.code.<id>`, `monkmode_add.request.<id>` | (plain, presence/candidate only) | Requests the service adjudicates. **Slot-addressed**: the id is a zero-authority routing hint (an unknown or retired id deletes the file and changes nothing), and a partner code is verified only against the slot it names [E22]. Ledger 319 retired the two cooling-off names — `monkmode_cooloff.request.<id>` / `.cancel.<id>` are still *swept* by the tick, but they address no family any more, so they are deleted unread and can never start anything |

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
exist.** Diagnosis is by *state inspection* (Sections 2.1–2.9), not by logs. The
only runtime output is the CLI's own `Console` text while you run a command.

### 2.9 Reading a multi-block machine (v1.1)

`monkmode status` prints one row per block — `Id · State · Ends/Starts · Sites ·
Apps · URLs · Exit`. Three things follow that did not apply to v1.0:

- **`STOPPED` service + no hosts marker still means "nothing enforcing", but
  "one block ended" no longer means the machine is clear.** A slot retiring
  rewrites the config, the snapshot and the hosts block and leaves the service
  running for the others; only the last slot leaving runs the full teardown.
  Poll `monkmode status` (or the marker, 2.3), never "did the service stop".
- **The hosts block is a union.** Every ACTIVE *and* PENDING slot contributes its
  sites, so entries can outlive the block you were watching. A pending
  (`--start`) block's sites are in hosts from arm time by design — an over-block,
  never an under-block.
- **`127.0.0.1:80` may be listening.** The notifier binds the loopback block page
  while a real deadline is ahead of the monotonic mark (plus 60 s of grace), and
  releases it otherwise — including for the whole of a pending block's wait, and
  between schedule windows. `netstat -ano | findstr :80` shows it; the owner is
  `mm_notify.exe`. If some other process holds port 80 first, MonkMode's bind
  fails silently and only the page is lost (see B12 in `ARCHITECTURE.md`).

Two v1.1 state flags worth recognising when a block looks stuck:

- **`[Time] TimeChanging`** is a cooperation flag the notifier raises for ~2 s
  around a system clock change, during which the service holds rather than
  advances. Since FX6 the hold is **bounded to 300 s of monotonic time**
  (`TimeChangeHoldMaxSeconds`, `Service1.vb:2400`), and the heartbeat lowers a
  provably-orphaned flag inside a MAC-verified write, so a flag left set by a
  killed notifier can no longer wedge a block forever. The over-run is at most
  that bound, and it always errs late rather than early.
- **`[Guard] HoldUntil` / `ArmedCount`** are what the SYSTEM guardian reads
  instead of parsing the config; a non-empty global `[Schedule] Spec` counts
  towards `ArmedCount`, so slot arithmetic can never zero a hold a surviving
  schedule owns.

---

## 3. Full-uninstall how-to

The way out of an *active* block is one of the two user-facing exits (the partner
code, or the timer — `USER-GUIDE.md` §6). This section
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
delete the folder by hand after the service is gone.

### 3.2 There is no forced teardown any more (ledger 319, 30/08/2026)

`monkmode unblock --force` ran a ten-step best-effort teardown: disable SCM
recovery, kill the watchdog pair and notifier, remove the deny-DELETE ACE and
delete the service, strip the hosts marker block, delete the hosts snapshot and
the config shadow backup, delete the trigger files, remove the two SafeBoot leaf
keys, restore the browser DoH policy, and clear the notifier autorun.

**All ten steps, and every primitive behind them, were deleted** (1.3). The CLI
cannot perform any of them.

What still happens, and does the equivalent work, is the SERVICE's own
genuine-expiry teardown: at a real end (timer or partner code) it strips the
hosts marker block, drops the snapshot, restores the browser DoH policy, removes
its SafeBoot registration, re-grants DELETE on its own service object and stops
itself — leaving the registration installed and idle (2.1). From there `sc delete
MONKMODE`, or `tools\uninstall.ps1`, finishes the job. The operator sequence is
therefore: **let the block end → confirm idle (2.1) → `sc delete` → remove the
folder.** There is no way to compress that while a block is live.

### 3.3 What an ended block leaves behind — verified

The teardown does **not** delete everything. Verified against the code (there is
no `File.Delete` for these artefacts anywhere in the CLI) [E26], the following
**survive** and must be removed by hand if you want a truly clean slate:

- **`monkmode_settings.ini`** — the enforcement config file itself is left in
  place. Harmless: the service that read it is gone, and the next `monkmode
  block` overwrites it. Delete it by hand only if you are wiping the folder.
- **`monkmode_setup.ini`** — your account defaults (partner label, the now-inert
  cooloff seconds, default site/app lists) **survive**, so a reinstall keeps them. This
  is effectively a feature — but delete it if you want setup to start blank.
- **`monkmode_stats`** — your block history (counts only) **survives**. Delete it
  by hand to reset the history.
- **The `dist\` folder and the four executables** — MonkMode cannot delete its
  own running binaries. Remove the folder yourself after the service is gone (the
  exes are unlocked once the service and guardian processes have exited — confirm
  with 2.6).

Note the `monkmode_doh.snapshot` is *consumed* by the service's DoH restore
(restore-then-delete), so it is normally gone after an expiry — but if that step
was skipped (best-effort), it may linger and is safe to delete by hand.

After the block has ended and `sc delete MONKMODE` has run, `sc query MONKMODE`
returns *service does not exist* and the hosts marker (2.3) is gone. That pair is
your "it's off" signal.

### 3.4 The stuck / bricked variant

Symptom: the `MONKMODE` service exists and refuses the normal exits — the config
store is dead. Causes: DPAPI at LocalMachine scope is unreadable (so the MAC
can't be validated), a tampered (MAC-invalid) config that froze fail-closed, or a
structurally corrupt primary ini with no valid backup [E23][E27]. In all of
these the block **holds fail-closed on purpose** — no error path may ever lift a
block. The partner code cannot help either: `ClassifyPartnerCodeSignal` requires
a valid MAC before it will even attempt a verify, so a frozen config refuses the
code as well as the clock.

**Since ledger 319 there is no in-band way out of this state.** The escape hatch
that used to be the answer here — unconditional, MAC-free, DPAPI-free, stripping
hosts by literal marker text — was removed on 30/08/2026 (1.3). A frozen block
now holds indefinitely, past its own end time, until the machine is recovered
out-of-band. This is the sharp edge of the removal and it was accepted knowingly:
avoid it by never arming across a binary upgrade (4.2) and never hand-editing the
config.

The floor is the offline route (B10):
boot from WinRE / a live USB, mount the disk, delete the `MONKMODE`
`HKLM\SYSTEM\CurrentControlSet\Services\MONKMODE` key and the app folder, and
strip the marker block from the offline hosts file. On an unencrypted disk this
always works (1.1) — it is the same residual the whole design accepts, now used
as the recovery of last resort.

---

## 4. Known operational gotchas

Real, live-observed footguns. All but 4.4 have bitten; internalise them before
scripting or running a smoke.

### 4.1 Never pipe or capture `monkmode block` output (pipe-wedge)

Historically the notifier (`mm_notify`) inherited the CLI's stdout, so
**redirecting or capturing** the output of `monkmode block` (`| tee`,
`> log.txt`, `$(...)`, backticks, a captured subprocess) left the calling shell
**wedged until the block expires** [E28]. This is live-proven (10/07/2026).
**Fixed in source 10/07/2026** — the notifier is launched detached
(`UseShellExecute=True`, no handle inheritance) — and **live-proven fixed
14/07/2026**: a deliberately piped arm returned in 0.8 s [E28]. Two caveats keep
the habit worth having: an older `dist` build under smoke still carries the
wedge, and the one-time partner code belongs on the screen, not in a log. Two
corollaries for diagnosis:

- To poll for a lift, watch state (service not-Running **or** hosts marker gone,
  per 2.1/2.3) — **never** a captured block-arm.
- "Expiry" of a block leaves the service *Stopped but still installed*, not gone
  (2.1) — do not treat a surviving service registration as a wedge.

### 4.2 Never arm a block across a binary upgrade (forward-migration MAC freeze)

The enforcement config carries a **compile-time** schema version (`v11`) as its
first MAC-covered line. A block armed under **older** binaries fails the MAC when
read by **newer** binaries and **freezes fail-closed** — it keeps enforcing and
will not auto-lift [E16][E29]. **Arm blocks *after* upgrading the binaries, not
across an upgrade.** If you are rebuilding: let any live block end first, then
rebuild `dist\`, then arm. If you are already stuck in a forward-migration
freeze, that is a "config store dead" case — and since ledger 319 there is no
in-band recovery for it at all (3.4). Do not risk it.

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

### 4.4 `install.ps1 -InstallDir` outside Program Files

The tamper model rests on Program Files' admin-only write ACL: it is what stops a
standard user swapping `MonkMode_srv.exe` for a no-op. `-InstallDir "D:\Apps\…"`
used to inherit `BUILTIN\Users: Modify` from a freshly created data-drive folder
and silently void that fence. Since FX9 the installer detects an install dir
outside **all three** Program Files roots and stamps an explicit admin-only DACL
built from empty (inheritance broken; SYSTEM and Administrators full control,
Users read+execute), applied after the copy so it propagates to the files, and
**throws before touching the machine PATH** if it cannot. The default Program
Files path is byte-identical to before — zero ACL calls, zero output.

Operator notes: verify with `icacls "<InstallDir>"` after a non-default install
(this is smoke-owed, not unit-pinned — there is no PowerShell test harness in the
repo); and if the path resolves through an 8.3 short name or a junction, the
root classification errs towards *hardening* or towards needing admin, never
towards leaving the folder open.

### 4.5 A non-elevated `monkmode` run looks like it did nothing (F73)

`MonkMode\My Project\app.manifest:19` sets
`<requestedExecutionLevel level="requireAdministrator" />`. From a **non-elevated**
prompt, Windows therefore does not run `monkmode.exe` in your console at all: it
raises UAC and starts a **separate console window**, which closes the instant the
command returns. Every line of output goes there. Your prompt gets **zero lines
and exit code 0**.

Measured 22/08/2026 on the fresh v1.1 install: `cmd /c 'monkmode status'` from a
non-elevated shell returned **0 lines of output, exit 0**, while the same command
in an elevated prompt printed normally; `Process.Start` *without*
`UseShellExecute` threw *"The requested operation requires elevation"*. This is
what made a perfectly good install look broken.

- **Diagnosis:** a `monkmode` command that produces no output and exit 0 is almost
  always **an unelevated shell**, not a broken binary. Check with
  `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)`
  — `False` means you are seeing this, not a fault.
- **Fix:** open an elevated prompt and re-run. Redirection does not help (`>`,
  `| tee`, `$(...)` all capture *your* console, and the output is in the other
  one), and note 4.1's separate rule about never capturing a `block` arm anyway.
- **Not going to change:** dropping `requireAdministrator` is not on the table —
  the CLI writes the hosts file, the SCM and HKLM, so the elevation is
  load-bearing, and a `asInvoker` manifest would simply move the failure to a
  worse place (a half-completed arm). A relauncher shim was considered on 22/08
  and deliberately **not** built: it would add a second process on the arm path
  for a documentation problem. Documented instead, here and in `README.md`, and
  the `install.ps1` closing banner now names the failure mode rather than only
  saying "from an elevated prompt".

---

## 5. Known limitations / accepted findings (v1.1)

Everything here was **found, understood and deliberately accepted** for the v1.1
line — the P3 tail of the 19/08/2026 adversarial bug-hunt plus the residuals the
nine fix slices recorded rather than chased. None of them lifts, shortens or
weakens a block: they over-block, fail soft, or are cosmetic. Source for each is
`logs\2026-08-19-v11-bughunt.md` (finding ids) and the FX1–FX9 commit messages.

### 5.1 Input and CLI

- **F17 — a path-only `--urls` pattern is silently inert.** `--urls /shorts` is
  accepted and MAC-covered but can never match (a pattern with no authority
  normalises to nothing). Accepted: fail-soft in the nudge layer; write
  `youtube.com/shorts`. Documented in `USER-GUIDE.md` §3b.
- **F18 + F37 — a site with interior whitespace is accepted, and the two paths
  then disagree.** `block`/`add` both accept `"a b.com"` (only control characters
  are refused, FX4). On the **arm** path it is written straight through, so hosts
  gets the mangled line `127.0.0.1 a b.com` — which Windows reads as one address
  mapped to *two* hostnames, i.e. an **over**-block on names the user never typed
  (F37). On the **`add`** path the service's merge drops any entry containing
  whitespace, so `add` prints success and adds nothing — an **under**-block
  against what the CLI just claimed (F18). Accepted for v1.1: no real domain
  contains a space, both effects are confined to that one malformed input, and
  the whitespace filter itself must stay, since dropping an *existing* entry
  would be a genuine under-block.
- **F38 — a trailing-dot site suppresses mirror expansion.** `youtube.com.`
  blocks only itself, not its `www.`/`m.`/`web.`/`mobile.` mirrors, because the
  bare-second-level test sees two dots. Accepted: cosmetic under-coverage of an
  unusual spelling; the URL watcher now normalises trailing dots (FX8), so the
  nudge layer still sees it.
- **F24 — a PENDING block cannot be cancelled. CLOSED by ledger 319 (30/08/2026),
  by fixing the wording rather than the behaviour.** `status` and `help` both used
  to say `unblock --id N --cancel` "cancels it freely until it starts"; the cancel
  only ever cleared a pending cooling-off deadline, which a PENDING slot has none
  of, so it was a no-op and the block armed anyway. `--cancel` is gone with the
  cooling-off, and both surfaces now say plainly that a waiting block cannot be
  cancelled.
- **F27 — the CLI `--preset video`/`social` vocabularies and the same-named
  `mm-video`/`mm-insta` wrappers arm different lists.** Two independent list
  sources, one name. Accepted, documented in `USER-GUIDE.md` §5 — check
  `monkmode status` after arming if the exact list matters.
- **F28 — one display-only regression left:** the wrappers' duplicate-arm warning
  is suppressed whenever an account default app list is set. Cosmetic only. (Its
  other half, a cooling-off "started" toast that could never fire, was deleted
  outright by ledger 319 along with the cooling-off exit.)
- **F19 — `"1 sites blocked"`** on the block page is not pluralised. Cosmetic.

### 5.2 Enforcement-adjacent, bounded

- **F20 — `monkmode status` can truncate.** The slot-reading loop's `Catch` sits
  outside the loop, so a throw while reading slot *k* prints *k−1* rows. Latent
  today (no known throwing input) and display-only — `status` has no enforcement
  authority.
- **F21 — the `add_to_hosts` channel is orphaned but live.** No production code
  writes it any more, yet the service still watches the file and appends its
  contents to hosts and to the snapshot. Admin-write-only (so B10-tier) and
  append-only (so over-block-only); since FX7, entries that are not in config
  truth are reverted by the next repair anyway.
- **F39 — the arm-time hosts union copies snapshot lines verbatim.** A line
  planted in `monkmode_hosts.block` (admin-write-only) is carried into hosts. Same
  tier and same direction as F21.
- **F23 — the notifier re-parses its counter sidecar on the UI thread every
  5 s.** A planted ~999 KB `stats-notify.ini` is re-read each time. Bounded by
  design (a 1 MB read cap and a 730-day key cap, added in S7b), display-only, and
  it costs UI responsiveness rather than enforcement.
- **F26 — the block page's accept loop can leak a bound socket** on a non-stop
  accept fault, wedging port 80 for the page itself. Page-only; enforcement never
  reads that socket.
- **F40 — `MaxTriggerFilesPerTick = 16` is smaller than its worst case.** Four
  trigger families × eight slots = 32, and the file selection sorts ordinally, so
  a full flood defers some triggers by roughly one extra tick. Deferring an exit
  trigger is fail-closed (the block holds ~10 s longer); the constant's comment
  has been corrected to describe the four families.
- **F61 — the trigger deleters build paths from an unvalidated id.** Containment
  is a property of the only caller (which yields file-name leaves), not of the
  construction. **Unreachable as shipped**; worth a one-line
  `Path.GetFileName` guard whenever that code is next touched.

### 5.3 The URL nudge layer

- **F36 — the synthesised Enter is global and unverified.** The redirect refuses
  to act if focus could not be taken, but nothing re-checks between typing the
  target and pressing Enter, so a toast, a UAC prompt or a click landing in that
  gap can send Enter somewhere unintended. This is the one way the feature can do
  something unasked; it is bounded to the ~milliseconds between two calls and
  gated by FX8's same-window, watched-browser, once-per-read check.
- **F60 — `youtube.com:8080?v=1` is discarded entirely** (a port plus a query with
  no path reads the host as the scheme). Every neighbouring shape is correct.
  Fail-soft: a silent miss in the nudge layer, and no shipped preset host carries
  a port.
- **FX8 residual — cross-pass window attribution under a takeover race.** If a
  hung pass is taken over after its 60 s staleness bound, a URL read by the old
  pass could in principle be attributed to the new one; bounded by the
  window-identity and browser re-check, which still have to agree.
- **FX8 residual — IDN pass-through.** Internationalised hosts are matched raw
  rather than punycode-normalised, by design.

### 5.4 Hosts and lifecycle

- **FX7 residual — a legacy block's tail is lost once.** A hosts block written
  before the end marker existed has no closing line, so the first write after
  upgrading strips it to end-of-file, taking any line you had appended below it.
  Nothing in such a file distinguishes your line from ours, and guessing would
  destroy user data in the other direction. Converges in exactly one rewrite.
- **FX7 residual — a spurious end marker inside the block.** If an end-marker
  line is injected *above* MonkMode's real entries, those entries are demoted to
  ordinary user content: they survive the lift as stray `127.0.0.1` lines and
  need deleting by hand. Tamper-only, and it over-blocks rather than
  under-blocks (2.3).
- **F22 — `uninstall.ps1 -PurgeData` does not remove `%ProgramData%\MonkMode`.**
  The counter sidecars and the `BUILTIN\Users: Modify` ACE on that folder survive
  a "clean slate" uninstall. Delete the folder by hand if you want it gone.
- **F25 — `uninstall.ps1`'s deletion loops use `-Path`, not `-LiteralPath`**, so
  a file name containing `[ ]` could match the wrong path and abort the uninstall
  part-way. Uninstall-only, and it fails towards leaving files behind.

### 5.5 Block page and install

- **FX9 residual — a schedule armed by the pre-slot writer gets no block page.**
  A v9-shaped, slot-less schedule config carries no slot deadline for the bind
  gate to read, so the page does not run for it. Pre-existing (the page is a v1.1
  addition), and the block itself is unaffected.
- **FX9 residual — the bind is unbounded on persistent garbage.** An undecryptable
  or unreadable deadline biases the gate *towards* binding, so a config full of
  junk datetimes keeps the page bound. That is the designed fail direction: the
  page erring towards "shown" costs a port, erring towards "hidden" costs the
  explanation at the moment it is needed.
- **FX9 residual — 8.3 short names and junctions** in an `-InstallDir` path can
  make the "is this inside Program Files?" test err towards hardening or towards
  requiring admin; never towards leaving the folder writable (4.4).
- **B12 — port 80 is unprivileged on Windows**, so any local non-elevated process
  can bind it before MonkMode does; MonkMode's bind then fails silently and the
  squatter receives the plain-HTTP requests (and cookies) for every blocked
  domain. Pre-existing to the hosts design, which has always pointed blocked names
  at `127.0.0.1`. See the B12 row in `ARCHITECTURE.md`.

### 5.6 Build hygiene

- **F29 —** three `NU1510` warnings on every build, and `build-dist.ps1`'s own
  ini reader trims values and honours `#` comments while the product's reader is
  verbatim. Neither reaches enforcement; the divergence matters only if
  `build-dist.ps1` is ever asked to read a value that has leading/trailing spaces
  or a `#`.

---

## See also

- `USER-GUIDE.md` — the command-first lifecycle (setup, block, schedule,
  presets, the partner code, stats, and the happy-path removal).
- `README.md` — the exit model, the honest ceiling, and engineering notes.
- `ARCHITECTURE.md` (working docs, `OneDrive/dev/repos/monk-mode/specs/`) — the full
  bypass surface B1–B14, ranked by effort, with live-verification evidence.

---

## Evidence footnote

Every claim in this runbook is grounded in current code (or the canonical spec
where noted). Cites are `file:line` at the time of writing.

- **[E1]** App folder = `AppContext.BaseDirectory` — `MonkMode\Blocker.vb:127`
  (`AppDir()`).
- **[E2]** B10 offline attack, always wins on an unencrypted disk —
  `ARCHITECTURE.md:282` (bypass table B10) + §5 :304-306.
- **[E3]** No BitLocker; B10 closure needs measures outside this codebase, D7
  SKIP — `ARCHITECTURE.md:311-315` (§5).
- **[E4]** RETIRED by ledger 319: the `unblock --force` teardown and its `Step_`
  helper no longer exist in `MonkMode\Program.vb`. Grep the CLI for `--force`:
  the only hits are the comments recording the removal.
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
- **[E10]** `status` idle string — `MonkMode\Program.vb:632` ("no active block
  (service installed but idle).").
- **[E11]** `sc delete` while idle removes it; refused during a block —
  `USER-GUIDE.md` §9.
- **[E12]** Hosts marker `#### MonkMode Entries ####` — `ARCHITECTURE.md:20`,
  :283 (B11 identifiers).
- **[E13]** HKCU Run, value `MonkMode_notify` — `MonkMode\Blocker.vb:102`
  (`RunValueName`).
- **[E14]** SafeBoot Minimal + Network leaf keys — `MonkMode\Blocker.vb:114-115`
  (`SafeBootMinimalKey`/`SafeBootNetworkKey`); lifecycle `ARCHITECTURE.md`
  (B3 row).
- **[E15]** Guardian process `mm_guard`, single-instance mutex
  `Global\MonkModeGuardian` — `MM_guard\MM_guard\Program.vb:89`;
  `MonkMode\Blocker.vb:108` (`GuardProcessName`).
- **[E16]** Enforcement config `monkmode_settings.ini`, schema `v11`, compile-time
  first MAC-covered line — `MonkMode\Blocker.vb:50` (`IniName`);
  `MonkMode\ConfigIntegrity.vb:92` (`CurrentSchemaVersion = "v11"`);
  `ARCHITECTURE.md` §3a (enforcement canonical).
- **[E17]** Config shadow backup `monkmode_settings.ini.bak` (R8) —
  `MonkMode\ConfigBackup.vb:65` (`BackupFileName`); `MonkMode\Blocker.vb:140`
  (`IniBackupPath`).
- **[E18]** Setup config `monkmode_setup.ini`, schema `s4` —
  `MonkMode\Blocker.vb:2709` (`SetupIniName`), :2718 (`SetupSchemaVersion = "s4"`);
  `ARCHITECTURE.md` §3a.
- **[E19]** Hosts snapshot `monkmode_hosts.block` — `MonkMode\Blocker.vb:51`
  (`SnapshotName`).
- **[E20]** DoH snapshot `monkmode_doh.snapshot` — `MonkMode\Blocker.vb:54`
  (`DohSnapshotName`); B5a `ARCHITECTURE.md:277`.
- **[E21]** Stats `monkmode_stats`, non-MAC, display-only —
  `MonkMode\Stats.vb:45,52` (`StatsFileName`, `StatsPath`); `USER-GUIDE.md` §8.
- **[E22]** Slot-addressed trigger files (cooloff request/cancel, partner code,
  add) — `MonkMode\Blocker.vb:68-71` (the four prefixes; the id is appended,
  :3000, :3007, :3028), service side `MonkMode_srv\MonkMode_srv\Service1.vb:3397-3403`.
- **[E23]** DPAPI key inside the ini as `[Integrity] Key`, LocalMachine scope —
  `MonkMode\ConfigIntegrity.vb:456,471` (`ProtectedData.Protect/Unprotect …
  DataProtectionScope.LocalMachine`); `MonkMode\Blocker.vb:121` (`IntegrityKeyName`).
- **[E24]** No `EventLog` writes in the service — grep of
  `MonkMode_srv\MonkMode_srv\Service1.vb` for `EventLog`/`WriteEntry` returns no
  matches (verified at time of writing).
- **[E25]** RETIRED by ledger 319 with `Step_` itself; see [E4].
- **[E26]** No `File.Delete` of the ini / setup ini / stats anywhere in the CLI —
  grep of `MonkMode\` for `Delete.*IniPath` / `SetupIni` / `Stats` returns no
  matches. Since ledger 319 the CLI deletes nothing at all.
- **[E27]** Corrupt/blank/short ini holds fail-closed; tampered (MAC-invalid)
  freezes and is never "recovered" — `ARCHITECTURE.md:280` (B8 row, C1b backup).
- **[E28]** Pipe-wedge (notifier inherits stdout) — `USER-GUIDE.md` §10;
  auto-memory `monkmode-block-arm-pipe-wedge`. Live-proven 10/07/2026; the fix
  live-proven 14/07/2026 (`CHANGELOG.md`, 1.0.0 live-verification: a piped arm
  returned in 0.8 s).
- **[E29]** Forward-migration MAC freeze (arm after upgrading) — `USER-GUIDE.md`
  §10; `ARCHITECTURE.md:279` (B7 row, C1 extension), :142-143.
- **[E30]** Orphaned guardian build-lock (MSB3021/MSB3027) — auto-memory
  `guardian-build-lock-drill` (verify-no-live-block then `Stop-Process`).
- **[E31]** Counter sidecars in `%ProgramData%\MonkMode\`, one writer each,
  non-MAC, capped read — `MonkMode\StatsSidecar.vb:110-111` (`ServiceFileName` /
  `NotifyFileName`), :166 (why not the app folder), :427 (the deliberate
  `BUILTIN\Users : Modify` grant there), :436 (`MaxFileBytes` early return).
- **[E32]** v1.1 surface cited in §2.9, §4.4 and §5 — `TimeChangeHoldMaxSeconds`
  `MonkMode_srv\MonkMode_srv\Service1.vb:2400`; block-page bind gate
  `MM_notify\MM_notify\BlockPage.vb:278` (`ShouldBindNow`), :248
  (`BindGraceSeconds = 60`), :72 (`LoopbackPort = 80`); slot cap
  `MonkMode\ConfigIntegrity.vb:99` (`MaxSlots = 8`); URL-watch cadence
  `MM_notify\MM_notify\Form1.vb:146` (2 s) and watched browsers
  `MM_notify\MM_notify\UrlWatch.vb:547`; `-InstallDir` DACL `tools\install.ps1`
  §"admin-only DACL" block (:167-230). Findings F17–F61 and the FX residuals:
  `logs\2026-08-19-v11-bughunt.md` + the FX1–FX9 commit messages.
