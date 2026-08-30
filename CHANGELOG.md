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

Defects found on and just after tag day, all outside enforcement, plus one usability fix — F77,
which changes what a block's end time *means* — and a **removal** that changes what MonkMode
*is*: a block can now only be ended by its partner code or its own end time. Rounded off by a
sweep of small honesty fixes: the binary no longer teaches a `--urls` pattern form that cannot
match, no longer claims sites are blocked when they are not, and now names which install and
which build is answering.

### Removed

- **The `unblock --force` escape hatch is gone.** It tore any block down unconditionally —
  SCM recovery disabled, the watchdog pair killed, the deny-DELETE ACE removed, the service
  deleted, the hosts block stripped, the snapshot, the config backup, the SafeBoot keys, the
  browser DoH policy and the notifier autorun all removed — with no code and no waiting. It
  had been retained for four releases as brick-insurance. Samrath, 30/08/2026: *"i dont like
  how i can force unblock it regardless ... i should only be able to unblock with code."*

  The removal is a removal, not a hiding. The flag, the teardown branch and every primitive
  that existed only to serve it were deleted from the source: `KillWatchdogProcesses`,
  `RestoreHostsFromStrip`, `RemoveSafeBootKeys`, `DeleteBackup`, the CLI's `RemoveDohPolicy`
  and `ClearNotifierAutorun`, `ServiceTools`' `DisableRecovery` / `RestoreDefaultServiceSd` /
  `DeleteServiceByName` — and the `DeleteService` P/Invoke with them, so `monkmode.exe` now
  has no code path at all that can delete a Windows service. `--force` is reported as an
  option that does not exist.

- **The self-serve cooling-off wait is gone.** A bare `monkmode unblock` used to drop a
  request trigger; the service computed a MAC-covered deadline of about an hour of machine-on
  time and then lifted the block itself. That was the second exit needing no partner code, so
  it went with the first. The whole channel is deleted: the request/cancel writers, the
  service's per-tick poll, the deadline computation, the request/cancel classifier and the
  compile-time floor. A stale `monkmode_cooloff.request.<id>` left by an older build is now
  unaddressed junk that the tick deletes unread — it can never start anything.

  `unblock --cancel` went with it (it only ever cleared a pending cooling-off deadline).
  That also closes **F24** by fixing the wording rather than the behaviour: `status` and
  `help` claimed `--cancel` cancelled a delayed block "freely until it starts", which was
  never true — a PENDING block has no cooling-off deadline, so the cancel did nothing and the
  block armed anyway. Both surfaces now say plainly that a waiting block cannot be cancelled.

- **Every block is committed; there is no lesser mode.** `--commit` and `--cooloff` are still
  *accepted* so existing scripts and muscle memory keep working, but they do nothing: the
  arm always records a committed block with no cooling-off duration. They are gone from
  `help`. The MAC-covered `Committed` / `CoolOffUntil` / `CoolOffDuration` fields stay in the
  v12 canonical, written `yes` / empty / empty, so the schema is untouched.

  **What this costs, stated rather than buried.** With no escape hatch, a fail-closed corner
  is now genuinely unrecoverable in-band. A config that fails its integrity check (the B7
  freeze — caused by hand-editing MonkMode's files, or by arming a block and then upgrading
  the binaries underneath it) can be lifted by *nothing*: not its end time, and not the
  partner code either, because the code is verified against a config the service will not
  trust. Such a block holds indefinitely and only the offline route (B10 — boot elsewhere,
  edit the disk) gets out. The honest ceiling therefore reads: **impulse-proof, not
  admin-proof, and there is no built-in escape.**

### Added

- **`status` now shows the time a block actually has left.** Expiry is decided against the
  monotonic high-water mark, which only advances while the service runs, so the stored end
  stamp is not a wall-clock promise: an hour spent shut down or asleep pushes the real end an
  hour later. The table said nothing about that. Every ACTIVE row now carries the real
  remaining — `(~2h 10m of active time left)`, the same `deadline − high-water` subtraction the
  tray already showed — and one note under the table says the end time counts machine-ON time
  only. The remaining rides on the end of the row, after the Exit column, so the fixed-width
  columns still line up; an unreadable or MAC-frozen mark reads `(active time left unknown)`
  rather than inventing a number.
- **F77 — a block now ends when it said it would, even if the laptop was off.** Until now the
  expiry clock only ran while the service did, so a block armed `--until 02:00` that spanned an
  overnight shutdown did not end at 02:00 — it ended once the machine had been *switched on*
  that long. Shut down at midnight, boot at ten, and a 02:00 block still had two hours to run.
  That was deliberate (it is what stops a rolled-forward clock from lifting a block), but it
  broke the promise the `--until` flag makes, so downtime is now credited.

  The obvious way to do that would hand the bypass straight back: if the boot gap were measured
  with the machine's own clock, "shut down, wind the clock forward, boot" would lift any block —
  *easier* than the attack the monotonic mark exists to stop. So downtime is measured against a
  clock you cannot edit. The config gained one MAC-covered field, `[Time] TrustedUtc` — the UTC
  instant at which the high-water mark was last known correct — and the credit is the gap
  between that anchor and an **externally corroborated** UTC now: the `Date` header of several
  independent HTTPS hosts, where at least two must agree and the **earliest** reading wins.
  Certificate validation is what makes it trustworthy, so a redirect through the hosts file
  MonkMode itself writes produces a certificate error rather than a forged time.

  Three things follow, and each is pinned by tests. Changing the machine's **timezone** earns
  nothing, because the credit is a duration between two UTC instants added to the local mark.
  Rolling the **clock** forward earns nothing, because neither `DateTime.Now` nor `UtcNow`
  appears anywhere in the credit path. And with **no network** — or too few witnesses, or
  witnesses that disagree — nothing is credited at all, which is exactly the behaviour that
  shipped before: the feature can only ever shorten an over-run, never end a block early.

  The time probe runs in the background and is read by a later tick, so the 10-second
  enforcement beat never waits on a network; a boot that lands after a block's real end
  therefore lifts a minute or two after the network comes up rather than instantly — the
  probe fired at start-up races the boot's own network stack, and if it loses it retries
  a minute later. Honest residual: someone who
  installs a trusted root certificate and intercepts *every* witness could manufacture credit —
  an administrative attack on the machine's own trust store, in the same family as the offline
  disk edit that has always been out of scope.

  **Upgrading:** this is config schema v11 → v12, so a block armed under the old binaries
  correctly freezes (it holds and cannot lift) until it is re-armed. Install the new build when
  no block is live.

- **F75 — the binary can now talk you out of a corner on its own.** Every recovery path lived
  only in `docs/RUNBOOK.md`: a developer file, in a source repo, which someone running the
  installed binary has no reason to have on disk. `monkmode help` gained an **"If something
  looks wrong"** section naming the four things that look like breakage and are not (a
  non-elevated run printing nothing; `service installed but idle`; the service not running;
  the FROZEN integrity message), and stating plainly what the way out is. (That section named
  `unblock --force` when it shipped; the removal above rewrote it to say the opposite — that a
  lost code means you wait, and that a frozen config holds indefinitely.) It also gained the one exit the help had
  never actually named: **a block ends by itself when the timer runs out.** New `monkmode
  version` reports the release, the install directory and the build's own timestamp, because
  the assembly's `FileVersion` is still the inherited Cold Turkey `0.7.0.0` and Windows'
  properties dialog therefore answers "which build is this?" wrongly.
- **F74 — the one-time code now names who to send it to.** The `--partner` label was stored,
  echoed back once by `setup`, and then read by no runtime code at all, so a partner who had
  never actually been given a code looked identical to one who had. `block` now prints
  `Send it to your accountability partner NOW: <label>` directly under the code. The label is
  read **before** the arm and an absent or unusable one prints nothing, so nothing new can throw
  in the window between arming and the code print (the F6 rule). The line sits **below** the
  code, never between the header and it, because `cv-d-smoke.ps1`'s `ParseCode` takes the line
  immediately after the header as the code. Still no message is sent anywhere — MonkMode has no
  outbound capability; relaying the code remains a physical act.

### Changed

- **`monkmode version` is one line, and it names the build and the install.** Two installs of
  the same release — `dist\` and `C:\Program Files\MonkMode\` — keep separate config and setup
  state and were indistinguishable from each other, which is how both sat on a stale build for
  two days after a fix shipped with nothing flagging the drift. `version` now prints
  `MonkMode 1.1.0 (850f1ef, built 30/08/2026 16:31) at C:\Program Files\MonkMode`, and
  `monkmode status` prints the same line first — including on the "no block has ever been
  installed" path, which is precisely the message that used to get attributed to the wrong
  install. The commit and build instant are stamped in at build time by `tools\build-dist.ps1`
  (properties and their fallbacks in the new `Directory.Build.props`); a developer build reads
  `dev` and falls back to the exe's own timestamp. `tools\build-dist.ps1` also closes with a
  **read-only** comparison against the deployed exe and says when the deployed build is behind
  — it installs nothing, because deploying replaces the binaries of a registered service and
  may only happen in a gap with no block live.
- **The monotonic note under `status` no longer says downtime is simply lost.** With F77
  deployed, "sleep or shutdown pushes the end later by the same amount" is only true
  *offline*: once the service can corroborate the real time from at least two agreeing HTTPS
  witnesses, the downtime is credited back on the next tick. The note now states both halves,
  as do `README.md` and the user guide.

### Fixed

- **A block that simply ran out left the hosts file read-only.** The read-only attribute is
  enforcement — the DNS-client lock the service re-asserts every 10 s — but the expiry path
  re-asserted it one last time *after* stripping the MonkMode block, so a block that ended on
  its own timer left hosts locked with nothing left to enforce, and the next writer (Tailscale,
  a DNS tool) failed until a manual `attrib -r`. A genuine expiry now leaves hosts as an
  ordinary writable file. Only that one path changed: the per-tick self-heal, `OnStart` and
  the crash backstop still lock hosts while a block stands, and a strip that *fails* still ends
  read-only — the block is still in the file.

  Follow-up (30/08/2026): the fix originally covered only the branch that actually *found*
  something to strip. The other branch — expiry running against a hosts file with no MonkMode
  marker in it, because a previous teardown had already cleaned it — still re-asserted
  read-only on its way out, leaving a machine with no block holding a locked hosts file that
  nothing would ever unlock again. Both branches of the expiry strip now end writable.
- **`status` could report "no active block" over a block it was fully enforcing.** On a machine
  with no slots armed (a pre-v10 config), the fallback line asked whether the stored end was in
  the wall-clock past. After an overnight shutdown it always was, while the mark the service
  actually decides on still lagged behind it — so the one command a user runs to check said
  everything was clear while the sites stayed blocked. It decides on the mark now, and the idle
  line is printed only when the block is genuinely over or nothing is configured.
- **F71 — a full arm/teardown cycle dropped the hosts file's trailing newline.** The
  CLI's writer normalised the user's tail (`StripOurBlock` trims every trailing
  CR/LF/space/tab) and then re-appended one CRLF as its block separator — so on a
  *fresh* arm, with no marker present, the newline it swallowed and re-issued was the
  user's own. The teardown strip then removed one terminator, correctly undoing what it
  believed was the separator, and the user's newline never came back: the 20/08 smoke
  measured hosts going 1051 → 1049 bytes with `diff` reporting *"\ No newline at end of
  file"*. No hosts LINE was ever lost, but "byte-identical after teardown" was not true.
  The writer uses `HostsAboveBlock` now — which drops a terminator only when a marker is
  actually there — so the separator is unambiguous and the cycle is byte-exact in both
  directions: a file that ended with a newline keeps it, one that did not does not gain
  one. This also ends a CLI↔service layout divergence (the service's writers always
  assembled it this way). Pinned by three round-trip tests that drive the real writer and
  both real teardown strips against a temp fixture and assert on bytes.
- **F72 — `install.ps1` clobbered the install's own data files.** The payload copy was a
  flat `Copy-Item '*' -Recurse -Force`, and the default payload dir is `dist\`, which is
  not a build artefact but a live install — `build-dist.ps1` deliberately preserves the
  six runtime data files there. So an install copied whatever setup, config, history and
  snapshots that folder had accumulated over the install dir's own. Measured on the 22/08
  v1.1 deploy: Program Files' `monkmode_setup.ini` was replaced by `dist\`'s 20/08
  smoke-session copy, destroying the real account setup; `monkmode_settings.ini` survived
  only because `dist\` had none, and restoring a *stale enforcement config* over a newer
  one is the far worse version of the same bug. The installer now copies **binaries only**
  and prints a `SKIPPED data files` line naming anything it left behind.
- **F73 — a non-elevated `monkmode` run looks like it did nothing.** `monkmode.exe` is
  manifested `requireAdministrator`, so a non-elevated invocation is elevated by Windows
  into a **new console window that closes the moment it returns**: the calling prompt gets
  zero lines and exit code 0, and even `monkmode status` reads as a broken install.
  Documented, not shimmed — the elevation is load-bearing (the CLI writes hosts, the SCM
  and HKLM) and a relauncher would put a second process on the arm path for a
  documentation problem. `README.md`, `docs/USER-GUIDE.md` §1a, `docs/RUNBOOK.md` §4.5 and
  the `install.ps1` closing banner now all name the failure mode.
- **The `--urls` glob footgun, in the exe's own help.** `--urls` patterns are matched as
  ordinal **substrings** — there are no wildcards, so a `*` is compared literally and, since
  real addresses never contain one, a pattern carrying `*` matches nothing at all while the
  block arms and looks perfectly healthy. The docs were fixed on 26/08/2026; the binary's own
  usage text still shipped `--urls "*/watch*"` as its example and so taught the broken form to
  anyone without the docs. Every shipped example is now the substring form
  (`--urls "youtube.com/shorts"`), the help explains the matching rule outright, and an arm
  carrying an asterisked pattern now prints a **warning** naming the pattern and its rewrite.
  It is a nudge, not a refusal: the block still arms with the patterns exactly as typed, so a
  script that has been passing globs keeps working rather than suddenly failing.
- **The failed-service-install warning overstated what was still blocked.** When the block was
  armed but the service could not be installed or started, the warning promised that "the
  blocked sites stay in your hosts file meanwhile" — true in the ordinary case, but a lie in
  the double failure where the hosts write had *also* failed and nothing whatever was
  blocking. The two cases now print different sentences. Overstating coverage is the one thing
  a self-control tool must not do.

## [1.1.0] — 22/08/2026 (multi-block + URL-level blocking)

The v1.1 line turns the single machine-wide block into up to **eight independent
blocks** that start, run and end on their own timers, adds **URL-pattern**
nudging and **delayed starts**, and closes the nine defect families the 19/08/2026
adversarial bug-hunt found. Gated on an elevated smoke batch, which ran on
20/08/2026 and is summarised under **Live-verification** below.

### Added

- **Multi-slot blocks.** `monkmode block` starts a *new* block beside the ones
  already running (up to `MaxSlots = 8`) instead of refusing. Each slot carries
  its own end time, sites, apps, URL patterns, cooling-off deadline, partner code
  and committed flag; `monkmode status` lists one row per block with its **id**,
  and every verb that addresses one block takes `--id N` (`add`, `unblock`).
  `unblock --cancel` and `unblock --code` deliberately take none: a cancel
  broadcasts to every armed block (it puts them *back* into full enforcement) and
  a code already belongs to exactly one block. A slot **retires on its own timer**
  without disturbing the others; only the last one leaving tears the machine down.
- **`--start` (delayed blocks).** `block --start +90m --for 2h` arms a **PENDING**
  slot that begins in 90 minutes and then runs for two hours (`--for` measures from
  the start), at most 30 days ahead. The service — not the CLI — computes the end
  time at the PENDING→ACTIVE transition from the monotonic mark, so a clock roll
  cannot shorten it. A pending slot's sites are hosts-blocked from *arm* time
  (deliberate over-block).
- **`--urls` URL patterns + the browser URL watcher.** `block --urls
  "*/watch*,*reddit.com/r/*"` attaches per-block URL patterns; the user-session
  notifier reads the foreground Chromium omnibox via UI Automation every 2 s and
  redirects a matching page to the site's home (one global 5 s cooldown, not one
  per window). Chrome, Edge and Brave are watched; Firefox is not. This is a
  **nudge, never enforcement** — the hosts block is what actually stops a site.
- **Block page.** While a block is running the notifier serves a small
  "locked in — Xh left" page on `127.0.0.1:80`, so a plain-HTTP visit to a blocked
  site shows why rather than a browser error. HTTP only (an `https://` visit keeps
  the browser's own error page), loopback only, and the request is never parsed.
- **Stats, streaks and a tray icon.** Per-block app-kill and redirect counters in a
  deliberately separate, non-MAC sidecar under `%ProgramData%\MonkMode\`
  (`stats-service.ini`, `stats-notify.ini` — one writer each), surfaced by
  `monkmode stats` (streaks, lifetime hours), `monkmode status` and the tray
  tooltip. Zero enforcement authority, and the counters survive a retire, a
  teardown and `unblock --force` (streak history is user data).
- **Overnight schedule windows.** `--windows "Mon-Fri 22:30-04:00"` — an end
  before the start now means *overnight*, accepted by the CLI validator and both
  parsers; the after-midnight tail belongs to the start day's mask.
- **Preset bundles and the `mm-lock` grammar** (preset layer, `presets\`): a
  shortform-URL preset, a doom-scroll site preset, a games/launcher app preset, the
  one-word wrappers `mm-shorts` / `mm-games`, and
  `mm-lock [doomscroll|shorts|social|games|everything] [for 2h | until 22:00 |
  tonight] [committed]` — an unknown word refuses and prints the vocabulary.
- **Hosts end marker.** MonkMode's hosts region is now closed by
  `#### MonkMode End ####` on its own line, so your own content *below* the block
  survives every write.

### Changed

- **Retargeted the whole stack to .NET 10 LTS** (`net10.0-windows`, supported to
  11/2028), with runtime packages, test tooling and the CI pins brought current
  (12/08/2026).
- **Enforcement canonical v9 → v11.** A two-level canonical — a global header plus
  one MAC-covered group per slot — replaced the single machine-wide block (v10),
  and v11 brought the global `[Schedule]` pair back under the MAC (FX1). One MAC
  covers the whole file: a failure freezes *every* slot, never some of them. The
  forward-migration freeze is unchanged — **arm blocks after upgrading the
  binaries, not across an upgrade**.
- **`monkmode add` is service-adjudicated**, via a slot-addressed request trigger,
  so it can only ever grow the block it names (~10 s to take effect).
- **A schedule and a manual block still refuse to coexist**, in *both* directions
  (see FX3): a schedule is global state, not a slot. `schedule --id` /
  schedule-as-a-slot was considered and deferred out of v1.1.

### Fixed

Nine fix slices closing the 19/08/2026 bug-hunt's P0/P1/P2 ladder
(`logs\2026-08-19-v11-bughunt.md`):

- **FX1 (F1, P0) — the one fail-open in the report.** The v10 canonical stopped
  covering the global `[Schedule] Spec`/`ActiveUntil` the service still enforces
  from, so blanking `Spec` in a text editor left the MAC valid and tore an open
  scheduled window down mid-window. Both keys are back inside the canonical (v11).
- **FX2 (F31, P1) — hosts data loss.** `StripMonkModeBlock` matched the marker as
  *text* and cut at that character index, so a user's own hosts line that merely
  *mentioned* the marker was truncated and every line below it deleted, on the
  first tick of the first block. The marker must now own its whole line.
- **FX3 (F3, P1) — block-over-schedule.** `monkmode block` had lost its refusal
  beside an armed schedule (a schedule is not a slot), and arming over one
  destroyed the schedule two different ways. The mutual exclusion is restored in
  both directions, in the command and independently in the writer.
- **FX4 (F4 + F30, P1) — arm-input hardening.** The shipped URL-only wrappers
  either could not arm at all or inherited the *whole* default blocklist; and one
  control character in any `--sites`/`--apps`/`--urls` value permanently bricked
  the config and froze every other running block. URL-only arms now inherit
  nothing, and any value carrying a character below `0x20` (tab included) or
  `0x7F` is **refused**, naming the offending value, before anything is armed.
- **FX5 (F2 + F5 + F6, P1) — no unverified source may narrow what is blocked.**
  Arming a second block used to unblock the first one's sites if the hosts
  snapshot was missing or locked (the arm now unions the snapshot, MAC-verified
  config truth and its own entries); a locked hosts file or a wedged SCM could
  swallow the one-time partner code of an already-committed block (both are now
  reported and non-fatal); and a slot retire trusted an unverified re-read of the
  config.
- **FX6 (F7–F10, P2) — config-writer and race family.** An orphaned
  `[Time] TimeChanging` flag can no longer wedge a block forever (the hold is
  bounded to 300 s of monotonic time); all four notifier writes go through one
  guarded shared-config writer; a confirmed arm can no longer be clobbered by a
  concurrent service write; and the arm confirm finds its slot by id at any
  position.
- **FX7 (F35, P2) — user hosts content below the block.** The three hosts writers
  now emit `#### MonkMode End ####`, and the strip preserves everything below it.
  A pre-v1.1 block with no end marker still strips to end-of-file (nothing in the
  file distinguishes your line from ours) and converges in exactly one rewrite.
- **FX8 (F12–F14 + F33–F34, P2) — URL-watcher family.** Trailing-dot hosts now
  match; a derived redirect host that is not plausible aborts the redirect (never
  the match); a redirect is only ever synthesised into the *same* window the read
  came from, re-checked as a watched browser, once per read; and a hung UI
  Automation read no longer disables the watcher for the life of the process.
- **FX9 (F11 + F32, P2) — block-page bind gate and install-dir ACL.** The block
  page now binds `127.0.0.1:80` only while a **deadline** is genuinely ahead of the
  monotonic mark, so `--start +30d` no longer holds the machine's only port 80 for
  a month; and `install.ps1 -InstallDir` pointed outside Program Files now stamps
  an explicit admin-only DACL instead of silently inheriting `Users: Modify`.
- **F70 — `unblock --force` no longer leaves an armed config behind.** The escape
  hatch removed the enforcement — watchdog pair killed, service deleted, hosts
  stripped, snapshot and backup gone — but left the config still carrying a
  non-zero `[Slots] SlotCount`. Two surfaces read only that file, so on a machine
  with nothing running, `monkmode schedule` refused with *"A block is armed"*
  (exit 3) and `build-dist.ps1` refused to rebuild or install. Both refusals were
  in the safe direction, but neither ever ended: after using the documented escape
  hatch you could not set a schedule or reinstall until the config was deleted by
  hand. The forced teardown now persists a zero-slot config, exactly as the
  service's own genuine-expiry teardown always has. No guard was weakened — the
  fix is parity between the two teardown paths, not a second opinion for the
  readers. Block ids still never restart across a teardown, and a tampered config
  is cleared but never re-stamped with a fresh MAC.

### Live-verification

The elevated smoke batch ran on 20/08/2026 (four sittings; enforcement core
45/1, with every failure triaged). Proven live on the maintainer's machine, not
merely unit-tested: three blocks armed inside one hosts marker block; one
expiring with the other two undisturbed; cooling-off request and cancel (the
request does **not** lift); a per-slot partner code unlocking only its own block;
teardown on the last block leaving; the two-marker hosts format and a user's own
trailing content surviving a full lifecycle; **guardian kill → respawn**;
**app-kill tampering freezing rather than lifting**; a squatted guardian mutex no
longer standing the watchdog down; an overnight schedule window; the block page
answering on loopback; and the block page's bind gate across pending → active →
expiry. The URL watcher was confirmed on Brave and Edge; Chrome was not installed
for the batch, so it is claimed on two of three browsers rather than three.

On 22/08/2026 the installer's admin-only DACL (FX9/F32) was drilled directly: an
install to a path outside Program Files was verified — independently of the
installer's own output — to break inheritance and grant `BUILTIN\Users` read and
execute only, on the folder and on the copied executables; a genuine
non-elevated attempt to overwrite `MonkMode_srv.exe` was **denied**, against a
control proving the probe detects writable files.

Not proven and carried honestly: a reboot-during-block notifier-count drill, the
trailing-dot URL case end to end (the watcher is foreground-only, which the
harness could not drive honestly), and three clock/timezone drills.

### Known limitations

The accepted, documented residuals for this line — including everything the
bug-hunt ranked P3 — are listed in `docs/RUNBOOK.md` §5.

## [1.0.0] — 16/07/2026

First release. Everything the release candidate was waiting on has since been
live-proven: the install/uninstall scripts passed their elevated drills, the
real install landed (H3), and a real 24-hour block ran end-to-end on the
maintainer's machine — armed, enforced, and exited via the sanctioned
cooling-off path with a textbook lift (service stopped-but-present, hosts
restored to the user's own content, guardian and notifier down).

### Added

- **D4b — persistent notifications.** WinRT Action-Centre toasts (a
  notifier-only change) replace the transient balloons for block-armed, expiry
  and clock-change notices; the block-start toast was live-confirmed persistent
  in the notification centre (15/07/2026).
- **D1c — subdomain mirror coverage.** Bare blocked domains now also expand to
  their `www.` / `m.` / `web.` / `mobile.` mirrors in the hosts block;
  live-verified under a social-preset block (14/07/2026).

### Changed

- **Smoke tooling only** (no product-code changes): the CV/D lift assertions now
  follow the documented lift contract (a genuine expiry leaves the service
  stopped-but-present; only `--force` deletes it), and `run-smoketest.ps1` /
  `b7-failclosed-test.ps1` self-run `monkmode setup` on a fresh dist, closing
  the arm-refusal trap after `build-dist.ps1` wipes the setup ini.

### Live-verification

- **14/07/2026 elevated batch:** B1–B7 full smoke **69/0** · CV/D **28/0** ·
  P2 piped-arm proof — a deliberately piped `block` arm returned in **0.8 s**
  (the pre-fix shape hung until expiry) · D1c subdomain mirrors live ·
  H1/H2 live drills — install verified (4 exes + machine `PATH`), uninstall
  clean on an idle machine, uninstall **refused** (exit 1, nothing touched)
  against a live block, then reinstalled.
- **First real block, end-to-end:** armed 14/07/2026 (snapchat + instagram,
  24 h), enforced continuously, exited early via the sanctioned cooling-off
  (the block held through the full cooling-off floor), clean teardown per the
  lift contract on 15/07/2026.
- **979/979** unit tests green; Release build 0 errors.

## [1.0.0-rc] — 10/07/2026

First release candidate. The enforcement core, config integrity, exit model and
CLI/UX are complete and live-verified (see [Live-verification](#live-verification)).
The GPL-compliance work (G2/G3, including the clean-room INI parser
replacement) and the packaging slices (installer H1, uninstaller H2) have all
landed; the candidate label reflects that the install/uninstall scripts are not
yet live-smoke-tested and the Samrath-gated release steps (H3/H4) remain.

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
- **File installer (H1).** A self-contained `win-x64` publish pipeline
  (`build-dist.ps1 -SelfContained`, bundling the .NET 8 runtime) and an elevated
  `install.ps1` that copies the four executables to `C:\Program Files\MonkMode\` (admin-ACL'd,
  raising the tamper bar) and adds them to the machine `PATH` idempotently. It installs
  files only — the first `block` still registers the service — and refuses to overwrite while
  a `MONKMODE` service exists (never upgrade across a block).
- **File uninstaller (H2).** An elevated `tools\uninstall.ps1` that reverses the H1 install:
  it `sc delete`s the idle service registration (stopped-only), removes the install dir, the
  machine `PATH` entry and the current user's notifier autorun, keeping account data unless
  `-PurgeData` is passed. It is deliberately **weaker than the R1 exits, never an alternative
  to them**: detection is fail-closed (it refuses unless it can positively establish that no
  block is enforcing — service state, hosts marker, schedule spec), it never stops a running
  service, never calls `unblock --force`, and never edits hosts. A lingering recurring
  schedule refuses unless `-IgnoreSchedule`; it self-protects against being pointed at a
  source/working tree.

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
- **P2 — notifier stdout-handle wedge.** The CLI launched `mm_notify` so it
  inherited the CLI's stdout, so any piped or captured `block` arm (`| tee`,
  `$x = ...`, CI) blocked until the block expired. The notifier is now launched
  detached (no handle inheritance), so a `block` arm can be safely scripted;
  `cv-d-smoke.ps1` was also made pipe-free as belt-and-braces. Fixed 10/07/2026,
  not yet live-verified (the fix's live proof rides the next elevated smoke).

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
  application, licensed **GPLv3** (see `COPYING`). The untouched original
  survives as the root of this repository's history (commit `c0838c4`,
  "0.6 Serious").
- **Inherited and kept:** the LocalSystem enforcement service, the hosts-file
  blocking approach, the four-component split, and the config/notifier contracts.
- **Rebuilt:** the .NET 8 migration, the CLI front-end, and every hardening layer
  above (B1–B7, B5a, the config-integrity schema, the exit model, schedules,
  presets, stats and notifications).
