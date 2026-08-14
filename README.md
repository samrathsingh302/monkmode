# MonkMode

[![CI](https://github.com/samrathsingh302/monkmode/actions/workflows/ci.yml/badge.svg?branch=monkmode)](https://github.com/samrathsingh302/monkmode/actions/workflows/ci.yml)

**A tamper-resistant website/app self-control blocker for Windows.** Once a block
starts, it cannot be casually removed before its timer expires — the point is to
protect you from *yourself*, so the software is designed and hardened against
its own administrator.

MonkMode is a personal fork of the open-source [Cold Turkey](#upstream--licence)
blocker (GPLv3): a 2011 VB.NET 2.0 WinForms codebase that no longer built,
modernised to .NET 10 LTS, converted to a CLI, threat-modelled, tested and hardened.

```
monkmode setup --partner "Alex (alex@example.com)"   # required once, first run
monkmode block --sites reddit.com,youtube.com --for 2h30m
monkmode block --preset social,video --apps chrome.exe --until "2026-06-11 18:00"
monkmode block --file blocklist.txt --for 8h --commit
monkmode schedule --sites x.com --windows "Mon-Fri 09:00-17:00"
monkmode status
monkmode stats
monkmode add --sites x.com          # an active block can only ever grow
monkmode unblock                    # start the cooling-off exit (delayed)
monkmode unblock --code <CODE>      # partner code: lifts within ~10s
monkmode help
```

`--for` accepts `45` (minutes), `90m`, `2h`, `1d12h`. Once a block starts it
cannot be shortened or replaced until it expires; `add` can only add more sites.

You block **sites** (hosts-level, machine-wide) and **apps** (killed on sight),
named explicitly, from a `--file` list, or via a named **preset** category —
`social`, `video`, `news`, `shopping`, `adult` for sites; `games`, `chat` for
apps. `monkmode setup` can record **account defaults** (a default blocklist,
app list and cooling-off duration) that a bare `monkmode block` inherits. You
can arm recurring **schedules** (wall-clock windows enforced at the same
strength as a manual block), review history with `monkmode stats`, and widen
app-kill to every logged-in session with `--all-session-kill`. `monkmode
status` shows the live block, time left and the current exit path; the notifier
raises tray toasts at block start, when a cooling-off begins, and at expiry.

## Exits — how a block ends (and what that honestly protects against)

A block is deliberately *hard to leave on impulse*, not impossible to leave.
There are three ordinary ways out, in increasing order of friction:

- **Wait for the timer.** A block always lifts itself at its end time. Expiry
  is decided off a monotonic high-water mark (see below), so rolling the clock
  forward can't bring it early.
- **Cooling-off (self-serve, but delayed).** `monkmode unblock` does *not* lift
  the block — it *requests* a lift. The block stays fully enforced while the
  service counts down a mandatory wait (~1 hour of active machine time by
  default; raise it with `--cooloff`, never shorten it), then lifts itself.
  `monkmode unblock --cancel` aborts a pending wait. There is no self-serve
  *instant* exit — that is the point.
- **Partner accountability code (immediate).** Every block mints a one-time
  code, shown once at block start and stored only as a salted, MAC-covered
  hash. Relay it to an accountability partner. `monkmode unblock --code <CODE>`
  is verified by the *service* (not the CLI) and lifts within ~10 s. A fresh
  code is minted per block; a wrong, blank or tampered code leaves the block
  standing.

A **committed** block (`monkmode block --commit`) disables the self-serve
cooling-off, leaving the partner code (or the timer) as the only early way out
— use it when you mean it.

**What this does *not* protect against — the honest ceiling.** You keep
Administrator rights on your own single machine, so MonkMode is *impulse-proof,
not admin-proof*. A deliberate, explicitly-flagged escape hatch —
`monkmode unblock --force` — always tears a block down and removes the service.
It is retained on purpose as brick-insurance: a fail-closed bug or a dead DPAPI
store must never be able to trap the machine permanently, so the guaranteed way
out is kept and documented rather than hidden. And an offline / WinRE /
determined-admin-with-time attack (B10) always wins eventually. MonkMode aims to
defeat casual-to-determined bypasses; it does **not** claim to be unbreakable,
and there is deliberately no BitLocker / BIOS-lock / non-admin-account layer in
this codebase. See the full bypass table (B1–B11) and the honest ceiling in
`ARCHITECTURE.md`.

## How it works

Four cooperating processes, so that no single Ctrl+Alt+Del kill ends the block:

| Component | Output | Runs as | Role |
|---|---|---|---|
| `MonkMode/` | `monkmode.exe` | User (elevated) | CLI. Parses commands, writes the hosts file, writes the encrypted config, installs & starts the service, registers the notifier. |
| `MonkMode_srv/` | `MonkMode_srv.exe` | **LocalSystem service `MONKMODE`** | Enforcement core. Locks the hosts file, restores it if tampered with, kills blocked processes, keeps the guardian alive, lifts the block only when the timer genuinely expires. `CanStop=False`. |
| `MM_notify/` | `mm_notify.exe` | User session (HKCU `Run`) | Notifier. Kills blocked apps in the user session, flags clock changes to the service (it no longer rewrites the end time), and shows tray toasts at block start, when a cooling-off begins, and when the block ends. |
| `MM_guard/` | `mm_guard.exe` | SYSTEM session (spawned by the service) | Watchdog guardian. Restarts the service via the SCM if it is killed, relaunches the notifier into the user session, stands down only when the block genuinely expires. |

A block is hosts-file DNS sinkholing (`127.0.0.1` entries below a
`#### MonkMode Entries ####` marker) plus process-kill rules, enforced by the
service on a 10-second loop.

## Tamper resistance (what's actually enforced)

- **Hosts lock + self-healing restore.** The service re-asserts the read-only
  attribute every 10 s, and if the MonkMode entries are edited or deleted
  (an admin can always clear an attribute) it restores them from a snapshot
  taken at block time. Only the marker block is ever touched — the user's own
  hosts content is preserved byte-for-byte.
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
- **Honest threat model.** `ARCHITECTURE.md` (kept in the project vault at
  `vault/dev/repos/monk-mode/specs/`) catalogues the full bypass surface
  (B1–B11), ranked by effort. While the user keeps admin
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
  exercised live in the same sitting (cooling-off can't be skipped, a wrong code
  doesn't lift, a good code does, a committed block refuses self-serve, a
  scheduled window auto-starts and tears down on `--clear`). The forward /
  backward clock drills are unit-pinned; their live drill is deferred, since
  manipulating the system clock is unsafe to run unattended. The smoke's first
  incarnation exposed three real bugs the compiler couldn't: `0.0.0.0` sinkholes
  that Windows' resolver silently ignores (now `127.0.0.1`), a persistent write
  handle on the hosts file that stopped the DNS client re-reading it (any
  `ipconfig /flushdns` un-blocked everything), and a notifier that exited
  instantly due to a WinForms entry-point subtlety.
- **Tested where it counts:** an xunit suite (**938 tests**) covers the dangerous
  string logic (hosts-file marker stripping and repair, culture-safe datetime
  round-trips under de-DE/fr-FR/en-US/en-GB locales, crypto round-trips and
  cross-project ciphertext equivalence, the config-integrity MAC and its
  four-project canonical parity, the monotonic clock gates, and the browser-DoH
  policy decisions), plus the accountability core added since (the cooling-off
  and partner-code lifecycle and their fail-closed gates, schedule parsing and
  window→duration conversion, preset expansion, and the separate stats file).
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

## Removing a block or the service

The intended way out of an active block is one of the exits above — cooling-off,
the partner code, or waiting for the timer. While a block is active the service
carries a deny-DELETE ACE, so `sc delete MONKMODE` is *refused* (B6); that is by
design, not a bug.

The deliberate escape hatch is `monkmode unblock --force` (run as
Administrator): it disables SCM recovery, stops the watchdog pair, removes the
deny-DELETE ACE, deletes the service, and strips only the MonkMode hosts marker
block (your own hosts content is preserved). It is the honest, documented
removal for a fail-closed corner or a determined admin — see the honest ceiling
above. When no block is active the service is idle and `sc delete MONKMODE`
removes it normally.

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
