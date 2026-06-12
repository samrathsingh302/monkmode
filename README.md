# MonkMode

[![CI](https://github.com/samrathsingh302/monkmode/actions/workflows/ci.yml/badge.svg?branch=monkmode)](https://github.com/samrathsingh302/monkmode/actions/workflows/ci.yml)

**A tamper-resistant website/app self-control blocker for Windows.** Once a block
starts, it cannot be casually removed before its timer expires — the point is to
protect you from *yourself*, so the software is designed and hardened against
its own administrator.

MonkMode is a personal fork of the open-source [Cold Turkey](#upstream--licence)
blocker (GPLv3): a 2011 VB.NET 2.0 WinForms codebase that no longer built,
modernised to .NET 8, converted to a CLI, threat-modelled, tested and hardened.

```
monkmode block --sites reddit.com,youtube.com --for 2h30m
monkmode block --sites x.com --apps chrome.exe --until "2026-06-11 18:00"
monkmode block --file blocklist.txt --for 8h
monkmode status
monkmode add --sites x.com        # an active block can only ever grow
monkmode help
```

`--for` accepts `45` (minutes), `90m`, `2h`, `1d12h`. Once a block starts it
cannot be shortened or replaced until it expires; `add` can only add more sites.

## How it works

Three cooperating processes, so that no single Ctrl+Alt+Del kill ends the block:

| Component | Output | Runs as | Role |
|---|---|---|---|
| `MonkMode/` | `monkmode.exe` | User (elevated) | CLI. Parses commands, writes the hosts file, writes the encrypted config, installs & starts the service, registers the notifier. |
| `MonkMode_srv/` | `MonkMode_srv.exe` | **LocalSystem service `MONKMODE`** | Enforcement core. Locks the hosts file, restores it if tampered with, kills blocked processes, lifts the block only when the timer genuinely expires. `CanStop=False`. |
| `MM_notify/` | `mm_notify.exe` | User session (HKCU `Run`) | Notifier. Kills blocked apps in the user session, compensates for clock changes, shows a tray toast when the block ends. |

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
- **Clock-change compensation.** The notifier detects system clock changes and
  rewrites the end time so rolling the clock forward doesn't end the block.
- **Honest threat model.** [ARCHITECTURE.md](ARCHITECTURE.md) catalogues the
  full bypass surface (B1–B11), ranked by effort. While the user keeps admin
  rights and physical disk access, an offline edit always wins eventually
  (B10) — the design goal is to defeat casual-to-determined bypasses, and to
  document the rest honestly rather than claim "unbreakable". Closing the gaps
  (watchdog, Safe Mode, firewall-layer enforcement, signed config) is the
  Phase 3 backlog.

## Engineering notes

This fork is where the actual work is — the inherited codebase was a starting
point, not a product:

- **Legacy modernisation:** VB.NET 2010 / .NET Framework 2.0 → SDK-style
  **.NET 8** (`net8.0-windows`). The public source had *never* built — it
  referenced a third-party `ServiceTools` helper that was never shipped;
  replaced with a hand-written advapi32 P/Invoke layer
  ([`MonkMode/ServiceTools.vb`](MonkMode/ServiceTools.vb)).
- **GUI → CLI:** replaced the WinForms GUI with a console CLI and slimmed four
  cooperating programs to three.
- **Verified live, not just compiled:** an elevated end-to-end smoke test
  (block → enforce → auto-expire → clean teardown) passed 15/15 checks and
  exposed three real bugs the compiler couldn't: `0.0.0.0` sinkholes that
  Windows' resolver silently ignores (now `127.0.0.1`), a persistent write
  handle on the hosts file that stopped the DNS client re-reading it (any
  `ipconfig /flushdns` un-blocked everything), and a notifier that exited
  instantly due to a WinForms entry-point subtlety.
- **Tested where it counts:** an xunit suite covers the dangerous string logic
  (hosts-file marker stripping and repair, culture-safe datetime round-trips
  under de-DE/fr-FR/en-US/en-GB locales, crypto round-trips and cross-project
  ciphertext equivalence). The tests are deliberately written in **C#**: VB's
  case-insensitive namespaces merge the `MonkMode` and `monkmode` namespaces
  and make the duplicated types ambiguous. Pure unit tests — nothing touches
  the real hosts file, registry or SCM, so they run in CI.

## Building & running

- Target: .NET 8 (`net8.0-windows`), VB.NET, SDK-style projects.
- Build everything: open `MonkMode.sln` in Visual Studio 2022, or:

      dotnet build MonkMode.sln -c Release

- Run the tests:

      dotnet test MonkMode.sln -c Release

- Assemble a runnable folder (all three exes must live together, alongside
  `monkmode_settings.ini` created at block time):

      powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1

  Then, from an elevated prompt:

      dist\monkmode.exe block --sites reddit.com --for 2h

- `monkmode.exe` requests Administrator elevation (it edits the hosts file and
  installs/starts the service via the Service Control Manager). Requires the
  .NET 8 desktop runtime.

## Removing the service (development only)

Run as Administrator: `sc delete MONKMODE`

Note: this removal path is exactly the kind of bypass the Phase 3 hardening is
meant to close. Documented only because it's needed in development.

## Upstream & licence

Originally based on **Cold Turkey** by Felix Belzile. Licensed **GPLv3**
(inherited) — see [COPYING](COPYING).
