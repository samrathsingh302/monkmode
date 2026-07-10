# Changelog

All notable changes to MonkMode are recorded here. The format is adapted from
[Keep a Changelog](https://keepachangelog.com/); this is a personal, single-machine
project, so entries are at release-notes altitude (features, fixes and hardening a
user would care about) rather than one line per commit. Dates are dd/mm/yyyy.

MonkMode is a personal fork of **Cold Turkey** (GPLv3) by Felix Belzile — a 2011
VB.NET 2.0 WinForms blocker that no longer built. The fork rebuilt it as a .NET 8
CLI and hardened enforcement so that, once a block starts, it cannot be casually
removed before its timer expires. See [The fork base](#the-fork-base) below.

## [Unreleased]

Queued, not yet landed (see `vault/dev/monk-mode/tasks.md` for the live queue):

- **H1 — installer.** Self-contained install script to `C:\Program Files\MonkMode\`.
- **H2 — uninstall UX.** A clean, honest teardown path.
- **P2 — notifier stdout-handle fix.** `mm_notify` inherits the CLI's stdout, so
  any piped/captured `block` arm wedges until the block expires; the fix is a
  handle-inheritance change so block-arm can be safely scripted.
- **D4b — persistent notifications.** WinRT Action-Center toasts to replace the
  transient balloons, which fired but were not reliably seen (found in the
  10/07/2026 watched sitting).

## [1.0.0-rc] — 10/07/2026

First release candidate. The enforcement core, config integrity, exit model and
CLI/UX are complete and live-verified (see [Live-verification](#live-verification)).
The candidate label reflects that the packaging slices (installer H1, uninstall H2)
and the GPL-compliance polish (G2/G3) have not yet landed.

### Added

- **CLI front-end.** Replaced the inherited WinForms GUI with `monkmode.exe`, a
  command-line blocker; a lightweight toast notifier (`mm_notify.exe`) took the
  place of the old popup/notify assemblies.
- **B1 guardian watchdog.** A SYSTEM-session guardian (`mm_guard.exe`) spawned by
  the service; the two mutually restart each other, and the SCM auto-restarts a
  force-killed service. The guardian exits only on genuine expiry.
- **B2 self-healing hosts file.** The service continuously restores the MonkMode
  hosts block if it is edited or deleted, touching only the marked block and never
  the user's own hosts content.
- **B3 Safe-Mode resistance.** The service self-registers under `SafeBoot` so it
  starts in Safe Mode (boot-time behaviour not reboot-tested, by choice — see
  [Honest ceiling](#honest-ceiling)).
- **B5a browser DoH self-heal.** Restores a browser's DNS-over-HTTPS-off policy if
  changed, with no loss of the user's own policy data.
- **Cooling-off exit.** Self-serve delayed teardown adjudicated by the service
  (never the co-written ini). The duration is configurable (`[CoolOff] Duration`)
  and can carry an account default; the shipped default is roughly one hour.
- **Partner-code exit.** An immediate exit gated on a valid trusted-partner code
  (salted-hash, single stable code, rotated on use, no email). A tampered config
  disables code-exit too, so honouring a code can never re-bless a tampered block.
- **Committed blocks (`--commit`).** A MAC-covered committed flag whose only exit
  is the partner code — no cooling-off.
- **Schedules.** Daily and weekly recurring blocks with the same enforcement
  strength as a manual block, including a schedule-only hosts-snapshot lifecycle
  and a HOLD gate across clock changes.
- **First-run onboarding (`monkmode setup`).** Guided account setup with a
  required-setup gate before the first arm; account defaults for cooling-off,
  partner, blocklists and app lists.
- **Site and app presets.** `block --preset social,video,news,shopping,adult` and
  `block --app-preset games,chat` expand curated lists on the input side only.
- **Account-default blocklists.** `setup --default-sites/--default-preset` and
  `setup --default-apps/--default-app-preset`; site and app defaults inherit
  independently per dimension.
- **All-session app-kill.** Blocked applications are killed across every user
  session, not just the arming session.
- **Block history / stats.** A separate, non-MAC history file and a
  `monkmode stats` command; a corrupt counter can never freeze a block.
- **Richer notifications and a rich `status` line.** Block-armed, expiry and
  clock-change notices; `status` reports cooling-off / committed-exit state and
  live schedule state, plus an input-typo warning and expanded help.
- **Config shadow backup (R8).** A MAC-covered shadow copy refreshed on legitimate
  writes; a corrupt or short primary is restored from it rather than freezing.

### Changed

- **Migrated the whole codebase to .NET 8** (`net8.0-windows`, SDK-style
  projects). The public source had never built.
- **Rebranded** Cold Turkey → MonkMode across projects, namespaces, assembly
  names, the service name (`MONKMODE`), config (`monkmode_settings.ini`), the
  hosts marker (`#### MonkMode Entries ####`, fixing a latent space/no-space marker
  bug) and the registry Run value.
- **Config integrity schema** advanced from canonical v2 to **v9** and the
  CLI-only setup schema from s1 to **s4** as new MAC-covered fields were added,
  each with a forward-migration freeze so an older binary cannot silently downgrade.
- **Exit model.** The only exits are cooling-off (delayed, self-serve) and partner
  code (immediate); teardown is service-driven. `unblock --force` is **retained as
  unconditional brick-insurance** (the reset hatch used in live smokes) — this is
  the shipped reality and is documented as such.
- **Clean-room INI parser (GPL compliance).** Replaced the inherited third-party
  INI reader/writer (`IniFileVb.vb`, by Ludvik Jerabek, CPOL 1.02 — a non-free,
  GPL-incompatible licence per the FSF) with a clean-room GPLv3 implementation
  (`IniFile.vb`) across all four assemblies, with 22 new parser and round-trip
  tests. The source tree now contains no third-party code; copyright, fork
  acknowledgement and the removal history are recorded in `NOTICE`, with a
  source-availability statement in the README.

### Fixed

- Fail closed on unparseable block end times; ordinal (not culture-sensitive)
  hosts-marker matching, fixing an en-CA culture and strip-boundary defect.
- Atomic hosts writes through one shared helper across all four writers; the hosts
  file-change handler no longer fails open (re-asserts read-only in a `Finally`).
- **Issue #1** — the service `OnStart` set-attribute failure now degrades with
  bounded retry instead of taking the one error-path that lifted the block.
- **Issue #2** — closed the CLI/service `schedule --clear` lost-update race with a
  spec-snapshot persist guard.
- Removed a needless zero-byte hosts window on default-block write.
- Three latent clock-drill script defects found in the 10/07/2026 watched sitting
  (script-only; enforcement was correct throughout).

### Security

- **B7 tamper-evident config.** HMAC-SHA256 over the ini with a DPAPI
  machine-scoped key; four separate MAC re-stamp fail-open sites were closed so a
  re-stamp only ever happens on an already-valid MAC. (The symmetric `Simple3Des`
  payload crypto remains documented-weak by design — a Phase-3-owned B7 residual.)
- **B4 clock-rollback / forward-jump hardening.** Expiry is decided off a
  monotonic high-water mark, and per-tick credit is bounded to real monotonic
  elapsed time so neither a rollback nor a large forward jump can lift a block
  early — a large forward jump HOLDS a schedule (may over-run ~1h) rather than
  lifting.
- **B1b backward-clock fix.** On a backward roll or forward jump the high-water
  mark credits real monotonic elapsed time instead of freezing.
- **B6 service-deletion resistance.** A deny-DELETE ACE on the service, with a
  matching strip-all teardown that removes it on genuine expiry.
- **Fail-closed backstop.** An `AppDomain.UnhandledException` handler re-asserts
  the block across the service, guardian and notifier; no error path may lift a
  block.

### Live-verification

Elevated smoke evidence backing the claims above (dates dd/mm/yyyy):

- **63/63** — full stack, 14/06/2026.
- **71/0** — whole stack B1–B7 + B5a, 01/07/2026.
- **69/0** run-smoketest **+ 10/0** B7 fail-closed, 09/07/2026 (fresh v9 dist).
- **9/0 clock drills** watched, 10/07/2026 — B4 survived a +30m jump past the
  deadline (×3); B1c lifted at ~117s of a 120s block under a −30m roll; clock
  within ~1s of NTP after every drill.
- **960/960** unit tests green; Release build 0 errors.

### Honest ceiling

MonkMode defeats casual-to-determined bypasses (B1–B9). It does **not** defend
against an offline / determined-admin-with-time attack (**B10**) — that is out of
scope by design; a user who keeps admin rights can eventually win. The B3
Safe-Mode registration was verified but not reboot-tested, by choice. See the
bypass table (B1–B11) and full ceiling in the README, `docs/USER-GUIDE.md` and
`docs/RUNBOOK.md`.

## The fork base

- **Upstream:** Cold Turkey by **Felix Belzile**, a 2011 VB.NET 2.0 WinForms
  application, licensed **GPLv3** (see `COPYING`). The `master` branch preserves
  the untouched original.
- **Inherited and kept:** the LocalSystem enforcement service, the hosts-file
  blocking approach, the four-component split, and the config/notifier contracts.
- **Rebuilt:** the .NET 8 migration, the CLI front-end, and every hardening layer
  above (B1–B7, B5a, the config-integrity schema, the exit model, schedules,
  presets, stats and notifications).
