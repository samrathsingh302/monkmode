# MonkMode

[![CI](https://github.com/samrathsingh302/monkmode/actions/workflows/ci.yml/badge.svg?branch=monkmode)](https://github.com/samrathsingh302/monkmode/actions/workflows/ci.yml)

**A tamper-resistant website/app self-control blocker for Windows.** Once a block
starts, it cannot be casually removed before its timer expires — the point is to
protect you from *yourself*, so the software is designed and hardened against
its own administrator.

MonkMode is a personal fork of the open-source [Cold Turkey](#upstream--licence)
blocker (GPLv3): a 2011 VB.NET 2.0 WinForms codebase that no longer built,
modernised to .NET 10 LTS, converted to a CLI, threat-modelled, tested and hardened.

## Quickstart

**Windows 10/11 only.** Every command runs from an **elevated (Administrator)**
prompt — from a normal prompt every command silently appears to do nothing (see
the warning under [Building & running](#building--running)). To build you need
the free [.NET 10 SDK](https://dotnet.microsoft.com/download); the installed
copy is self-contained, so the machine needs nothing else.

From an elevated PowerShell prompt in the repo root:

    powershell -ExecutionPolicy Bypass -File tools\install.ps1

That publishes a self-contained `win-x64` build (`tools\build-dist.ps1
-SelfContained` under the hood, .NET runtime bundled), copies it to
`C:\Program Files\MonkMode` and puts `monkmode` on the machine PATH. Open a
**fresh** elevated prompt, then:

    monkmode setup --partner "Alex (alex@example.com)"
    monkmode block --sites reddit.com --for 2h

Uninstall (refuses while a block is enforcing, keeps your data unless you pass
`-PurgeData`):

    powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1

**New users: read [`docs/USER-GUIDE.md`](docs/USER-GUIDE.md)** — the complete
zero-assumed-knowledge guide to every command and behaviour. This program is
GPLv3 free software with no warranty — see [COPYING](COPYING) and the
[honest ceiling](#exits--how-a-block-ends-and-what-that-honestly-protects-against)
before you arm anything long.

```
monkmode setup --partner "Alex (alex@example.com)"   # required once, first run
monkmode block --sites reddit.com,youtube.com --for 2h30m
monkmode block --preset social,video --apps chrome.exe --until "2026-06-11 18:00"
monkmode block --file blocklist.txt --for 8h
monkmode block --urls "youtube.com/shorts" --for 4h # pages, not whole sites
monkmode block --sites x.com --start +90m --for 2h  # begins in 90 minutes
monkmode schedule --sites x.com --windows "Mon-Fri 09:00-17:00"
monkmode status                     # one row per running block, with its id
monkmode stats
monkmode add --sites x.com --id 2   # an active block can only ever grow
monkmode unblock --code <CODE>      # partner code: the ONLY early exit
monkmode help
```

`--for` accepts `45` (minutes), `90m`, `2h`, `1d12h`. Once a block starts it
cannot be shortened or replaced until it expires; `add` can only add more sites.

**Up to eight blocks run side by side.** `monkmode block` starts a *new* one
beside the ones already running rather than refusing, each with its own timer,
lists and partner code; `monkmode status` gives every block an
**id**, and `--id N` names one for `add` or `unblock`. A block retires
on its own timer without touching the others — only the last one leaving tears
the machine down. `--start` delays a block (up to 30 days; `--for` then measures
from the start, and the service computes the end time when it actually begins).
`--urls` attaches URL patterns for *pages* rather than whole sites — the notifier
watches the foreground Chrome/Edge/Brave address bar and nudges a matching page
back to the site's home. Patterns are **plain case-insensitive substrings**
(`youtube.com/shorts`), with one special form: `host/` with nothing after it
matches only that site's front page. There are **no wildcards** — a `*` is a
literal character and a pattern containing one silently never matches. The nudge
is best-effort; the hosts block is what actually stops a site.

You block **sites** (hosts-level, machine-wide) and **apps** (killed on sight),
named explicitly, from a `--file` list, or via a named **preset** category —
`social`, `video`, `news`, `shopping`, `adult` for sites; `games`, `chat` for
apps. `monkmode setup` can record **account defaults** (a default blocklist and
app list) that a bare `monkmode block` inherits. You
can arm recurring **schedules** (wall-clock windows enforced at the same
strength as a manual block, including overnight windows such as
`Mon-Fri 22:30-04:00`), review history with `monkmode stats`, and widen
app-kill to every logged-in session with `--all-session-kill`. A schedule and a
manual block deliberately refuse to run together, in either direction — clear
the schedule first. `monkmode status` shows every running block, the time it
actually has left and the current exit path; the notifier keeps a tray icon and
raises toasts at block start, periodically during a long block, and at expiry.

That "time left" is **machine-ON time**. A block's end time advances only while
the service is running, so hours spent shut down or asleep are not served and
push the end later by the same amount — `status` prints the real remaining
(`~2h 10m of active time left`) beside the end stamp, and says so under the
table.

Two smaller comforts: while a block is running the notifier serves a small
"locked in — Xh left" page on `127.0.0.1:80`, so a plain-HTTP visit to a blocked
site explains itself instead of showing a browser error (HTTPS keeps the browser
error — MonkMode holds no certificate for a site it is blocking); and the
`presets\` folder ships one-word PowerShell wrappers plus a plain-English
grammar, `mm-lock social games for 3h committed`, that composes the CLI flags for
you and refuses on any word it does not know.

Any site, app or URL value carrying a **control character** (anything below
`0x20`, tab included, plus `0x7F`) is refused up front with the offending value
named, and nothing is armed: such a character would split the stored config line
when it was read back and freeze this and every other running block permanently.

## Exits — how a block ends (and what that honestly protects against)

A block is deliberately *hard to leave on impulse*, not impossible to leave.
There are exactly **two** ways out, and there is deliberately no third:

- **Wait for the timer.** A block always lifts itself at its end time. Expiry
  is decided off a monotonic high-water mark (see below), so rolling the clock
  forward can't bring it early.
- **Partner accountability code (immediate).** Every block mints a one-time
  code, shown once at block start and stored only as a salted, MAC-covered
  hash. Relay it to an accountability partner. `monkmode unblock --code <CODE>`
  is verified by the *service* (not the CLI) and lifts within ~10 s. A fresh
  code is minted per block; a wrong, blank or tampered code leaves the block
  standing.

**Lose the code and you wait.** There is no recovery, no override, no admin
bypass and no support channel — the block runs to its end time. Bare
`monkmode unblock` refuses; it does not start anything. Every block is
committed: there is no lesser mode. Choose durations you mean.

Until 30/08/2026 there were two more exits, and both were removed on purpose:
a self-serve **cooling-off** wait (`monkmode unblock` counted down ~1 hour of
active machine time and then lifted the block itself) and an escape hatch
(`monkmode unblock --force`, an unconditional teardown). Neither exists in the
code any more — not as a hidden flag, not as an environment variable, not as a
debug path. `--force` and `--cancel` are now reported as options that do not
exist. `--commit` and `--cooloff` are still *accepted* so old scripts keep
working, but they do nothing.

**What this does *not* protect against — the honest ceiling.** MonkMode is
*impulse-proof, not admin-proof*, and there is **no built-in escape**. You keep
Administrator rights on your own single machine, so an offline / WinRE /
determined-admin-with-time attack (B10) always wins eventually — booting
elsewhere and editing the disk is outside anything this program can defend, and
it is the only route left. The trade the removal makes is explicit: with no
escape hatch, a fail-closed corner is now genuinely unrecoverable in-band. A
config that fails its integrity check (B7 freeze) cannot be lifted even by the
partner code, because the code is checked against a config the service will not
trust — such a block holds past its end time, indefinitely, and only B10 gets
out. That is accepted, not overlooked. MonkMode aims to defeat
casual-to-determined bypasses; it does **not** claim to be unbreakable, and
there is deliberately no BitLocker / BIOS-lock / non-admin-account layer in this
codebase. The full bypass table (B1–B14) and the honest ceiling live in
`ARCHITECTURE.md`, the author's working threat-model notes (kept outside this
repo); the summary above is the accurate short form.

## How it works

Four cooperating processes, so that no single Ctrl+Alt+Del kill ends the block:

| Component | Output | Runs as | Role |
|---|---|---|---|
| `MonkMode/` | `monkmode.exe` | User (elevated) | CLI. Parses commands, writes the hosts file, writes the encrypted config, installs & starts the service, registers the notifier. |
| `MonkMode_srv/` | `MonkMode_srv.exe` | **LocalSystem service `MONKMODE`** | Enforcement core. Locks the hosts file, restores it if tampered with, kills blocked processes, keeps the guardian alive, lifts the block only when the timer genuinely expires. `CanStop=False`. |
| `MM_notify/` | `mm_notify.exe` | User session (HKCU `Run`) | Notifier. Kills blocked apps in the user session, flags clock changes to the service (it no longer rewrites the end time), shows the tray icon and toasts at block start, periodically during a long block, and when the block ends, watches the browser address bar for `--urls` patterns, and serves the loopback block page. Nudge and comfort layers only — it holds no enforcement authority. |
| `MM_guard/` | `mm_guard.exe` | SYSTEM session (spawned by the service) | Watchdog guardian. Restarts the service via the SCM if it is killed, relaunches the notifier into the user session, stands down only when the block genuinely expires. |

A block is hosts-file DNS sinkholing (`127.0.0.1` entries between the
`#### MonkMode Entries ####` and `#### MonkMode End ####` marker lines) plus
process-kill rules, enforced by the service on a 10-second loop. Only that
region is ever touched — your own hosts content, above or below it, is
preserved byte-for-byte.

## Tamper resistance (what's actually enforced)

- **Hosts lock + self-healing restore.** The service re-asserts the read-only
  attribute every 10 s, and if the MonkMode entries are edited or deleted
  (an admin can always clear an attribute) it restores them from a snapshot
  taken at block time. Only the marker block is ever touched — the user's own
  hosts content is preserved byte-for-byte. When the block ends the attribute
  is **cleared** and hosts is left as an ordinary writable file (the same state
  `monkmode unblock` leaves it in), so nothing else that edits hosts —
  Tailscale, a DNS tool — is left needing a manual `attrib -r`.
- **Fail-closed expiry.** A corrupted or unparseable end time keeps the block
  standing rather than lifting it. Failure modes were deliberately audited to
  fail in the *tamper-resistant* direction.
- **No graceful stop.** `CanStop=False` blocks the polite SCM stop path; the
  config that says *when* the block ends is encrypted.
- **Force-kill resistance (two-layer watchdog).** The Service Control Manager
  is configured to restart the service after any abnormal termination — on
  every failure, forever (reset period `INFINITE`) — and a SYSTEM-session
  guardian process and the service mutually restart each other on a 10 s
  loop, with the guardian also relaunching the notifier if it's killed. Every
  restart decision goes through a pure, unit-tested, fail-closed gate: nothing
  is ever resurrected after a block genuinely expires.
- **Safe Mode resistance.** The service registers itself under the Windows
  SafeBoot keys (Minimal *and* Network) — the standard mechanism by which a
  service runs in Safe Mode — so rebooting into Safe Mode no longer leaves
  enforcement off. It re-asserts those keys every 10 s if they're deleted and
  removes them when the block ends; only its own keys are ever touched.
- **Clock-tamper resistance (monotonic).** Expiry is decided against a
  MAC-covered high-water mark that only ever advances at the real tick rate
  (bounded by a monotonic OS timer), never by a wall-clock jump — so rolling the
  clock forward past the end time cannot lift the block. A backward roll only
  makes the block run *longer* (fail-closed). The notifier just flags a clock
  change to the service; it no longer rewrites the end time.
- **Browser Secure-DNS (DoH) resistance.** While a block is active the service
  forces the enterprise "DNS-over-HTTPS off" policy for Edge/Chrome/Brave/Firefox
  and re-asserts it every 10 s, so the #1 casual bypass — flipping on a browser's
  Secure DNS to tunnel around the hosts file — is closed. The user's prior policy
  is snapshotted and restored at expiry, with no data loss.
- **Fail-closed on crash.** An unhandled exception in any long-running enforcement
  process re-asserts its enforcement (re-locks hosts, restores the block) before
  the process dies, so a crash can never leave the block open.
- **Honest threat model.** `ARCHITECTURE.md` (the author's working threat-model
  notes, kept outside this repo) catalogues the full bypass surface
  (B1–B14), ranked by effort. While the user keeps admin
  rights and physical disk access, an offline edit always wins eventually
  (B10) — the design goal is to defeat casual-to-determined bypasses, and to
  document the rest honestly rather than claim "unbreakable". The remaining
  network-layer gap (portable / hard-coded-IP / non-browser DoH, VPN/proxy/Tor)
  is documented as a residual rather than claimed closed.

## Engineering notes

This fork is where the actual work is — the inherited codebase was a starting
point, not a product:

- **Legacy modernisation:** VB.NET 2010 / .NET Framework 2.0 → SDK-style
  **.NET 10 LTS** (`net10.0-windows`, retargeted from .NET 8 by v1.1 slice S0b, 12/08/2026). The public source had *never* built — it
  referenced a third-party `ServiceTools` helper that was never shipped;
  replaced with a hand-written advapi32 P/Invoke layer
  ([`MonkMode/ServiceTools.vb`](MonkMode/ServiceTools.vb)).
- **GUI → CLI:** replaced the WinForms GUI with a console CLI, dropped the
  inherited popup window and the weak user-session watchdog twin (later
  reinstated properly as the SYSTEM-session guardian).
- **Verified live, not just compiled:** an elevated end-to-end smoke test
  (block → enforce → tamper-repair → watchdog kill drills → auto-expire →
  clean teardown), grown as each hardening layer landed, passes **69/0** on the
  current build (09/07/2026), and a dedicated config-integrity fail-closed drill
  passes **10/0** — together covering force-killing the service, the guardian
  and the notifier in turn, disabling SCM recovery to prove the guardian alone
  restores the service, the browser-DoH self-heal, and a corrupted MAC that
  keeps the block standing rather than lifting it. The accountability core was
  exercised live in the same sitting (a wrong code doesn't lift, a good code
  does, a bare `unblock` is refused, a scheduled window auto-starts and tears
  down on `--clear`). The forward /
  backward clock drills are unit-pinned; their live drill is deferred, since
  manipulating the system clock is unsafe to run unattended. The smoke's first
  incarnation exposed three real bugs the compiler couldn't: `0.0.0.0` sinkholes
  that Windows' resolver silently ignores (now `127.0.0.1`), a persistent write
  handle on the hosts file that stopped the DNS client re-reading it (any
  `ipconfig /flushdns` un-blocked everything), and a notifier that exited
  instantly due to a WinForms entry-point subtlety.
- **Tested where it counts:** an xunit suite (**2225 tests**) covers the dangerous
  string logic (hosts-file marker stripping and repair, culture-safe datetime
  round-trips under de-DE/fr-FR/en-US/en-GB locales, crypto round-trips and
  cross-project ciphertext equivalence, the config-integrity MAC and its
  four-project canonical parity, the monotonic clock gates, and the browser-DoH
  policy decisions), plus the accountability core added since (the partner-code
  lifecycle and its fail-closed gates, schedule parsing and
  window→duration conversion, preset expansion, and the separate stats file),
  and the v1.1 surface on top of that (the two-level slot canonical and its
  four-project parity, the retire/teardown state machine, overnight window
  evaluation minute by minute across both DST nights, the pure URL layer under a
  200k-input fuzz, arm-input refusal, and the hosts marker/end-marker anchoring).
  The tests are deliberately written in **C#**: VB's
  case-insensitive namespaces merge the `MonkMode` and `monkmode` namespaces
  and make the duplicated types ambiguous. Pure unit tests — nothing touches
  the real hosts file, registry or SCM, so they run in CI.

## Building & running

- Target: .NET 10 LTS (`net10.0-windows`), VB.NET, SDK-style projects.
- Build everything: open `MonkMode.sln` in Visual Studio 2022, or:

      dotnet build MonkMode.sln -c Release

- Run the tests:

      dotnet test MonkMode.sln -c Release

- Assemble a runnable folder (all four exes must live together, alongside
  `monkmode_settings.ini` created at block time):

      powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1

  Then, from an elevated prompt:

      dist\monkmode.exe block --sites reddit.com --for 2h

- `monkmode.exe` requests Administrator elevation (it edits the hosts file and
  installs/starts the service via the Service Control Manager). Requires the
  .NET 10 desktop runtime.

- **Run every `monkmode` command from a prompt that is already elevated.** The exe
  is manifested `requireAdministrator`, so from a *non*-elevated prompt Windows
  raises UAC and then runs it in a **new console window that closes the instant the
  command returns**. Your original prompt gets **no output and exit code 0** — even
  `monkmode status` looks like it silently did nothing. It is not broken; the output
  went to a window you never saw. (Redirecting or capturing does not rescue it
  either: the child console is not your stdout.) See `docs/RUNBOOK.md` §4.5.

## Removing a block or the service

The way out of an active block is one of the two exits above — the partner code,
or waiting for the timer. While a block is active the service carries a
deny-DELETE ACE, so `sc delete MONKMODE` is *refused* (B6); that is by design,
not a bug.

**There is no escape hatch.** `monkmode unblock --force` was removed on
30/08/2026 along with the cooling-off wait, and `monkmode.exe` no longer contains
any code path that can stop or delete the service, kill the watchdog pair, strip
the hosts block or clear the SafeBoot registration. Nothing you can type ends a
block early except its own partner code.

Once the block has ended, the service stands itself down and re-grants DELETE on
its own, so `sc delete MONKMODE` removes it normally while idle — which is what
`tools\uninstall.ps1` does. That uninstaller is fail-closed and **refuses** while
anything is enforcing; it is not a way out either.

## Upstream & licence

Originally based on **Cold Turkey** by Felix Belzile. Licensed **GPLv3**
(inherited) — see [COPYING](COPYING). Copyright, fork acknowledgement and
third-party history (including the removed CPOL INI parser) are recorded in
[NOTICE](NOTICE).

### Source availability

MonkMode is distributed as source; there is no separate binary distribution. The
complete corresponding source for any build is this repository, on the
`monkmode` branch:

    https://github.com/samrathsingh302/monkmode

Build it with the .NET 10 SDK (`dotnet build MonkMode.sln -c Release`) and
assemble a runnable folder with `tools\build-dist.ps1` — see
[Building & running](#building--running). All use is governed by the GPLv3 terms
in [COPYING](COPYING).
