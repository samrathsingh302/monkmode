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

# MonkMode — User Guide

A practical, command-first manual for the whole lifecycle: install, setup,
blocks, schedules, presets, cooling-off, the partner code, emergency recovery,
stats, and removal.

MonkMode is a personal, tamper-resistant website/app blocker for Windows. It is
a command-line tool (there is no GUI). Once a block starts it cannot be casually
removed before its timer expires — the point is to protect you from yourself.

**The honest ceiling first, because it frames everything below.** MonkMode is
*impulse-proof, not admin-proof*. You keep Administrator rights on your own
machine, so a deliberate, explicitly-flagged escape hatch (`monkmode unblock
--force`) always exists, and an offline / WinRE / determined-admin-with-time
attack always wins eventually. The design goal is to defeat casual-to-determined
bypasses, not to be unbreakable. See the bypass table (B1–B11) and the full
ceiling in `ARCHITECTURE.md`.

Throughout, `monkmode` means the CLI executable `monkmode.exe`. Every command
below is run from an **elevated (Administrator)** prompt — MonkMode edits the
hosts file and installs a system service, so it will refuse to do anything
useful without elevation (it exits with "Access denied. Run MonkMode as
Administrator.").

---

## 1. Install

The supported install is the `tools\install.ps1` script (slice H1): it publishes a
self-contained build and copies the four executables to `C:\Program Files\MonkMode\`,
then adds that folder to the machine `PATH` so `monkmode` works from any elevated
prompt. Program Files is admin-ACL'd, which raises the tamper bar over a
user-writable folder. **Installing files does not arm anything** — the first
`block`/`schedule` you run *is* the enforcement install, registering and starting the
`MONKMODE` service.

### 1a. Recommended: the install script

From an **elevated** PowerShell prompt, in the repo root:

```
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

That publishes a self-contained `win-x64` payload (bundling the .NET 8 runtime, so
the target machine needs no .NET installed), copies it to `C:\Program Files\MonkMode\`,
and adds that folder to the machine `PATH`. Open a **new** elevated prompt afterwards
so the updated `PATH` is picked up, then continue at Section 2 (`monkmode setup`).

What the installer deliberately does **not** do:

- It does **not** install or start the service — your first `monkmode block` does that.
- It does **not** create shortcuts (MonkMode is a CLI, no GUI).
- It does **not** uninstall — that is `tools\uninstall.ps1` (slice H2, Section 9). The
  uninstaller is deliberately weaker than the exits: it refuses while a block is
  enforcing and never acts as an escape hatch.

The installer **refuses to run while a `MONKMODE` service already exists** (installed
or running) — never upgrade the binaries across an armed block, or a MAC'd config can
freeze fail-closed (the forward-migration freeze; see Section 10). Let any live block
end and remove the service first, then re-run the installer. Re-running it on a machine
with no `MONKMODE` service upgrades in place, and the `PATH` entry is never duplicated.

Options: `-PayloadDir <folder>` installs a pre-built payload instead of publishing;
`-InstallDir <folder>` overrides the target location.

### 1b. Build from source (manual / dev)

If you would rather run from a plain `dist\` folder (the dev workflow, no Program Files
copy), build and assemble it yourself. The .NET 8 SDK is user-scoped on this machine
(not on `PATH`), so call it by its full path:

```
C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release
```

Run the tests the same way:

```
C:\Users\samra\.dotnet\dotnet.exe test MonkMode.sln -c Release
```

### 1c. Assemble the runnable folder (`dist\`)

All four executables must live together in one folder, alongside
`monkmode_settings.ini` (which is created at block time). `build-dist.ps1`
publishes them into `dist\`:

```
powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1
```

That produces `dist\` containing:

| File | Role |
|---|---|
| `monkmode.exe` | The CLI you run. |
| `MonkMode_srv.exe` | The `MONKMODE` LocalSystem service (the enforcement core). |
| `mm_notify.exe` | User-session notifier (app-kill, tray toasts). |
| `mm_guard.exe` | SYSTEM-session watchdog guardian. |

The script rebuilds `dist\` from scratch each run (it deletes and recreates the
folder). **Always rebuild `dist\` before any live/smoke run** — a stale `dist\`
missing `mm_guard.exe` is the classic cause of a half-broken block.

### 1d. First run (elevated)

Open an **elevated** command prompt, `cd` into `dist\`, and run `setup` once
(Section 2), then start a block (Section 3). For example:

```
dist\monkmode.exe setup --partner "Alex (alex@example.com)"
dist\monkmode.exe block --sites reddit.com --for 2h
```

Requires the .NET 8 desktop runtime. `monkmode.exe` requests Administrator
elevation automatically (it edits the hosts file and installs/starts the service
via the Service Control Manager).

---

## 2. Setup (required, once)

`monkmode setup` is mandatory first-run onboarding. `block` and `schedule`
**refuse to arm until it has run** (they exit with code 4 and tell you to run
`setup` first), so your first block always goes through the explanation of how
to get out. It is idempotent and safe to re-run any time — it never touches a
live block.

```
monkmode setup [--partner "Alex (alex@example.com)"] [--cooloff 2h] \
               [--default-sites a.com,b.com] [--default-preset social] \
               [--default-apps chrome.exe,foo.exe] [--default-app-preset games]
```

Setup writes a **separate**, MAC-protected file `monkmode_setup.ini` (its own
schema, currently `s4`, independent of the enforcement config). It records:

| Option | What it stores | Notes |
|---|---|---|
| `--partner` | A free-text accountability-partner label. | Cosmetic — shown in the setup summary and relayed with the code. No email is sent. |
| `--cooloff <dur>` | An **account-default** cooling-off wait. | Every later `block` without its own `--cooloff` inherits it. Same duration grammar as `--for`; capped at ~365 days. The ~1 h floor still applies. |
| `--default-sites a.com,b.com` | An account-default blocklist. | A bare `monkmode block` (no `--sites`/`--preset`/`--file`) inherits it. |
| `--default-preset social` | Preset categories folded into the default blocklist. | Validated once, here, so a stored default can never make a later block fail to arm. |
| `--default-apps chrome.exe` | An account-default app-kill list. | A `block` with no `--apps`/`--app-preset` inherits it. |
| `--default-app-preset games` | App-preset categories folded into the default app list. | |

**Each `setup` run rewrites these defaults** — pass them again if you want to
keep them. A bad preset name fails fast *before* anything is written (no partial
state). If Windows DPAPI is unavailable, setup refuses (it can't protect its
config), exits with code 2, and no block will arm until DPAPI is resolved.

All setup state is fail-closed: a missing, tampered, or DPAPI-unreadable
`monkmode_setup.ini` reads as "not set up" (arming is refused) or, for the
defaults, as "no default" (empty). It can never *lift* or weaken a block.

---

## 3. Blocks

A block sinkholes **sites** (hosts-level, machine-wide `127.0.0.1` entries) and
kills **apps** on sight. Once started, a block **cannot be shortened, replaced,
or started anew** until it expires — the service enforces this.

```
monkmode block [--sites a.com,b.com] [--preset social,video] \
               [--apps chrome.exe,foo.exe] [--app-preset games,chat] \
               (--for 2h30m | --until "2026-06-11 18:00") \
               [--file list.txt] [--commit] [--cooloff 2h] [--all-session-kill]
```

You must give at least one duration (`--for` or `--until`) and at least one
thing to block (any of `--sites`/`--preset`/`--apps`/`--app-preset`/`--file`, or
inherited account defaults). If you name nothing and have no defaults, it
refuses ("Nothing to block.").

### Sources of what to block

| Flag | Effect |
|---|---|
| `--sites a.com,b.com` | Explicit domains (comma- or semicolon-separated). URLs are tolerated — scheme and path are stripped; a bare second-level domain also blocks its `www.`, `m.`, `web.` and `mobile.` mirrors (so `snapchat.com` covers `web.snapchat.com`). |
| `--preset social,video` | Expand named site categories (Section 5) into the site list. Pure input sugar. |
| `--file list.txt` | Read domains from a file, one per line; blank lines and `#` comments are skipped. |
| `--apps chrome.exe,foo.exe` | Executable names to kill. `.exe` is appended if you omit it. |
| `--app-preset games,chat` | Expand named app categories (Section 5) into the app-kill list. |

Explicit sources are merged; if you name **no** site source at all, the
account-default blocklist fills in (and likewise for apps), independently per
dimension. An unknown preset **aborts the block up front** with a friendly error
(fail-closed — a typo never silently under-blocks).

### Duration

| Flag | Grammar | Examples |
|---|---|---|
| `--for <dur>` | `Nd`, `Nh`, `Nm` in any combination, or a bare number = minutes. | `45` (45 min), `90m`, `2h`, `1d12h`, `1d2h30m` |
| `--until "<datetime>"` | A date/time parsed in your current locale, then the invariant locale. | `--until "2026-06-11 18:00"` |

The block must end **at least a minute in the future** (see the `--for 1`
gotcha in Section 9), or it refuses.

### Modifier flags

| Flag | Effect |
|---|---|
| `--commit` | Arms a **committed** block: self-serve cooling-off is disabled, leaving the partner code (or the timer) as the only early exit. Use it when you mean it. |
| `--cooloff <dur>` | Sets *this* block's cooling-off wait. Same grammar as `--for`; capped at ~365 days. The ~1 h floor still applies, so this can only ever *extend* the wait, never shorten it. Absent → inherit the account default → else the ~1 h floor. |
| `--all-session-kill` | Widens app-kill from your session (+ session 0) to **every** logged-in Windows session — useful if you fast-user-switch to a second account to dodge the kill. No effect unless you block apps. |

Note: `--commit` and `--all-session-kill` are on/off flags — pass them bare. If
you write `--commit=yes`, the value form is **ignored** and the flag is treated
as OFF (the CLI warns but still proceeds). Unrecognised `--flags` (likely typos
such as `--site` for `--sites`) are warned about and ignored, never fatal.

### What you see when a block arms

```
MonkMode is now active until <end time> (<time left>).
  Sites: ...
  Apps:  ...
Close and reopen your browser to see the block. It cannot be removed until the timer ends.

Emergency unlock code (give it to your accountability partner NOW - it will NOT be shown again):
    <CODE>
To end the block early, they run:  monkmode unblock --code <CODE>
```

The **partner code is shown exactly once** — see Section 6. Check the live state
any time with:

```
monkmode status
```

`status` reports the active block, time left, the sites/apps, and the current
exit path (committed / cooling-off pending / the self-serve wait + code). If a
schedule is armed instead, it reports the schedule and whether a window is open
right now.

### Adding sites to a running block

A block can only ever **grow**:

```
monkmode add --sites x.com,y.com
```

`add` only adds sites (not apps), only to an already-active manual block, and
refuses when a schedule is armed (edit the schedule instead). There is no
command to remove a site from a running block — that is by design.

---

## 4. Schedules

A **schedule** is a recurring wall-clock rule the service opens and closes
automatically, at the same strength as a manual block. A schedule and a manual
block are **mutually exclusive** — you can't have both armed at once.

```
monkmode schedule --sites a.com,b.com [--apps chrome.exe] \
                  --windows "Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00"
monkmode schedule --clear      # stop future windows; an open window still runs to its end
monkmode schedule --show       # print the armed schedule (read-only)
monkmode schedule --validate --sites a.com --windows "Mon-Fri 09:00-17:00"   # dry-run, arms nothing
```

**Windows grammar:** days `Mon`–`Sun` (single days `Tue`, ranges `Mon-Fri`,
lists `Sat,Sun`) plus 24-hour `HH:MM-HH:MM`, **same-day only**. Separate several
windows with `;`. A reversed day range (`Fri-Mon`) or an unknown day is rejected
with a friendly error — nothing is armed on a bad spec.

Arming a schedule installs and starts the service (so windows are evaluated) but
does **not** open a block now and does not write the hosts snapshot — the
service creates it when a window opens. During an open window the block holds at
full strength until the window closes; **it cannot be ended early**.
`--clear` blanks the rule so no future windows open; a currently-open window
still runs to its monotonic end, after which MonkMode tears down within ~10 s.

`--show` and `--validate` are read-only and never touch the service, hosts, or
registry. `--validate` requires `--sites` (the builder needs at least one site)
and returns exit code 0 (valid) or 1 (invalid), so it is scriptable.

---

## 5. Presets (input sugar)

Presets are named bundles of well-known sites/apps, expanded into the ordinary
site/app lists **before** the block arms. They carry **no enforcement
authority** — the expanded domains/executables are enforced and MAC-covered
exactly like hand-typed `--sites`/`--apps`, and the preset tables are
compile-time constants (nothing extra to protect). You pick categories; you
can't edit them (an *editable* default list is the `setup --default-*` feature
in Section 2).

**Site presets** (`--preset`, or `setup --default-preset`):

| Category | Domains (as shipped) |
|---|---|
| `social` | facebook, instagram, twitter, x, tiktok, reddit, snapchat, tumblr, pinterest, linkedin, threads |
| `video` | youtube, netflix, twitch, hulu, disneyplus, primevideo |
| `news` | cnn, nytimes, foxnews, bbc, buzzfeed, theverge |
| `shopping` | amazon, ebay, etsy, aliexpress, walmart, target |
| `adult` | (six well-known adult sites) |

**App presets** (`--app-preset`, or `setup --default-app-preset`):

| Category | Executables (as shipped) |
|---|---|
| `games` | steam, epicgameslauncher, battle.net, riotclientservices, leagueclient, valorant, robloxplayerbeta |
| `chat` | discord, telegram, whatsapp, signal, slack |

Combine presets with your own `--sites`/`--apps`; comma-separate several
categories (`--preset social,video`). An unknown category name aborts the whole
command with the list of valid names (fail-closed). The live category names are
printed by `monkmode help`.

---

## 6. Cooling-off & the partner code

There are three ordinary ways a block ends, in increasing friction. **All three
are decided by the service** (the sole timing authority), never by the CLI.

### Wait for the timer

A block always lifts at its end time. Expiry is decided off a monotonic
high-water mark, so rolling the clock forward can't bring it early (and a
backward roll only makes it run longer).

### Cooling-off (self-serve, but delayed)

```
monkmode unblock            # request a cooling-off lift
monkmode unblock --cancel   # abort a pending cooling-off; stay blocked
```

`monkmode unblock` does **not** lift the block — it *requests* a lift. The block
stays fully enforced while the service counts down a mandatory wait of **~1 hour
of active machine time** (the shipped default floor, `MinCoolOffFloorSeconds =
3600`), then lifts itself. The wait is measured against the monotonic
high-water mark (active machine time), not the wall clock. You cannot shorten
it: the request carries no timing, and the deadline is service-computed and
floor-clamped. Raise it with `--cooloff` at block time (Section 3); you can
never shorten it below the floor.

`monkmode status` shows the remaining cooling-off time once one is pending.

### Partner accountability code (immediate)

```
monkmode unblock --code <CODE>
```

Every block mints a fresh **one-time code**, shown once at block start and
stored only as a salted, MAC-covered one-way hash (never in plaintext, never
logged). Relay it to your accountability partner. `unblock --code` drops the
candidate for the **service** to verify (KDF + constant-time compare against the
MAC-covered hash); on a match it lifts within ~10 s. The CLI has **zero lift
authority** — it only submits. A wrong, blank, or tampered code leaves the block
standing, and correctness is *not* revealed synchronously (the service
adjudicates on its next tick).

**Rotate-on-use:** a fresh code is minted for every block, and a used code dies
with its block, so a code you watched yourself type can't be banked for the
next block. A tampered config (invalid MAC) disables the code exit too.

### Committed blocks

A block armed with `--commit` disables self-serve cooling-off entirely. If you
run `monkmode unblock` against it, it refuses and points you at the code:

```
This block is COMMITTED: self-serve cooling-off is disabled. The only early exit is the accountability code: monkmode unblock --code <CODE>
```

---

## 7. Emergency recovery

```
monkmode unblock --force
```

This is the deliberate, admin-only, explicitly-flagged **escape hatch**. It
unconditionally tears a block down and removes the service: disables SCM
recovery, kills the watchdog pair and notifier, removes the deny-DELETE ACE,
deletes the `MONKMODE` service, strips **only** the MonkMode hosts marker block
(your own hosts content is preserved byte-for-byte), removes the B2 snapshot, B3
SafeBoot keys, config backup, cooling-off/partner-code triggers, DoH policy, and
the notifier autorun. Every step is best-effort and reported; a failure in one
step does not abort the rest.

It is **retained on purpose as brick-insurance**: a fail-closed bug or a dead
DPAPI store must never trap the machine permanently, so the guaranteed way out
is kept and documented rather than hidden. It is gated behind an explicit
`--force`, so it can never be a casual one-word bypass — but an admin who *wants*
out can always take it. This is the honest ceiling in practice (see the intro
and `ARCHITECTURE.md` B6 / §5).

### What a genuine expiry looks like

When a block reaches its real end (timer, cooling-off, or a correct code), the
service strips the hosts block, removes its protections, and **stops itself** —
but the service registration remains **installed and idle**. That is normal:
`monkmode status` then reports "no active block (service installed but idle)".
Only `monkmode unblock --force` (or a manual `sc delete` while idle — see
Section 8) removes the service registration entirely.

If your browser still shows a block after a lift, flush DNS / reopen the
browser — the entries are already gone from hosts.

---

## 8. Stats

```
monkmode stats
```

A read-only summary of your block history: blocks started (completed vs
active/upcoming), committed count, total planned focus time, longest block,
first and latest block dates. Stats live in a **deliberately separate,
non-MAC file** (`monkmode_stats` in the app folder) and are **display-only** —
they record **counts only** (no site or app names) and have **zero enforcement
authority**. A corrupt or missing stats file simply reads as no/less history; it
can never freeze a block or error you out. If nothing is recorded yet, `stats`
tells you how to start your first block.

---

## 9. Uninstall / removal

The intended way out of an *active* block is one of the exits in Section 6
(cooling-off, the partner code, or the timer). While a block is active the
service carries a deny-DELETE ACE, so **`sc delete MONKMODE` is refused** — that
is by design (bypass B6), not a bug.

**Removing the service:**

- **During an active block:** use `monkmode unblock --force` (Section 7). It is
  the honest, documented removal for a fail-closed corner or a determined admin.
- **When no block is active** (service installed but idle): the service is idle
  and `sc delete MONKMODE` removes it normally. `monkmode unblock --force` also
  works and additionally cleans up the snapshot, SafeBoot keys, DoH policy, and
  autorun.

**Removing the files (`tools\uninstall.ps1`):** once no block is active, the
sibling uninstaller does the file-level teardown that `install.ps1` (Section 1)
set up. From an **elevated** prompt in the repo root:

```
powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1
```

It is deliberately **weaker than the exits above — never an alternative to
them.** Detection is fail-closed: it removes nothing unless it can positively
establish that no block is enforcing (service not running, no hosts marker, no
live schedule). If a block is still active it **refuses** and routes you back to
the exits (or to `unblock --force` for the during-a-block removal); it never
stops a running service, never calls `unblock --force` itself, and never edits
hosts. When clear, it deletes the idle service registration (`sc delete` on a
stopped service only), the install dir, the machine `PATH` entry, and the
current user's notifier autorun. Your account data (`monkmode_setup.ini`,
`monkmode_stats`, the stale enforcement config, and the `monkmode_doh.snapshot` /
`monkmode_hosts.block` snapshots) is **kept by default** so a reinstall keeps your
setup and history; pass `-PurgeData` for a clean slate. The DoH snapshot is kept
because it holds *your* browser DNS-over-HTTPS setting from before MonkMode — it
is the only thing that can put that setting back, so `-PurgeData` discards it too.
Options: `-InstallDir <folder>` if you installed somewhere other than
`C:\Program Files\MonkMode`; `-IgnoreSchedule` to remove despite a lingering
recurring schedule (it is then orphaned — clear it with `monkmode schedule
--clear` first).

If you ran from a plain `dist\` folder (Section 1b, no `install.ps1`), there is
no service registration the uninstaller can key off in Program Files — just
`sc delete MONKMODE` while idle and delete the `dist\` folder yourself after the
service and guardian processes have exited.

---

## 10. Gotchas (proven)

These are real, live-observed footguns — worth internalising before you script
anything.

- **Never pipe or capture `monkmode block` output.** Historically the notifier
  (`mm_notify`) inherited the CLI's stdout, so redirecting or capturing the
  output of `monkmode block` (`| tee`, `> log.txt`, `$(...)`, backticks, a
  captured subprocess) left the calling shell **wedged until the block
  expires**. Run `block` with its output going straight to the terminal.
  (Live-proven 10/07/2026. **P2 — fixed in source 10/07/2026** by launching the
  notifier detached with no handle inheritance, but **not yet live-verified**, so
  keep avoiding piped/captured arms until a smoke proves the fix.) Note that
  "expiry" of a block leaves the service *Stopped but still installed*, not gone.

- **`--for` has a strict >60-second floor.** A block must end at least a minute
  in the future, so **`--for 1` is refused** ("The block must end at least a
  minute in the future.") because one minute is exactly 60 s from now. Use
  `--for 2` or longer. (Bare numbers are minutes.)

- **Don't arm a block across a binary upgrade.** The enforcement config carries a
  compile-time schema version as its first MAC-covered line. A block armed under
  **older** binaries fails the MAC under newer ones and **freezes fail-closed**
  (it keeps enforcing and won't auto-lift). **Arm blocks *after* upgrading the
  binaries, not across an upgrade.** If you are rebuilding, let any live block
  end first, then rebuild `dist\`, then arm.

- **Always rebuild `dist\` before a live run.** A stale `dist\` (built before the
  guardian existed, or missing an exe) produces a half-broken block. See
  Section 1c.

---

## Exit codes (for scripting)

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Usage error (bad/missing argument, nothing to block, unknown preset). |
| 2 | Access denied (not elevated) or an internal error (e.g. DPAPI unavailable). |
| 3 | A block or schedule already active / a manual-vs-schedule conflict. |
| 4 | Setup has not been run yet (run `monkmode setup` first). |

---

## See also

- `README.md` — the exit model, the honest ceiling, and the engineering notes.
- `ARCHITECTURE.md` (in the project vault, `vault/dev/repos/monk-mode/specs/`) — the
  full bypass surface B1–B11, ranked by effort, with live-verification evidence.
- `monkmode help` — the always-current usage text, including the live preset and
  app-preset category names.
