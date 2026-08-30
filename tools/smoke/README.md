# tools\smoke — elevated live smoke tests

Live-path verification for MonkMode's enforcement core. These exercise the REAL
hosts file, registry, SCM service and system clock, so they must be run from an
**elevated** PowerShell, one at a time, on a machine with **no active block**.

Always rebuild `dist\` first (a block armed on stale binaries can freeze after a
canonical bump):

```
powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1
```

⚠️ `build-dist.ps1` wipes `dist\` **including `monkmode_setup.ini`**, and without
it every arm refuses with exit 4 (fail-closed by design). All three arming
scripts now self-setup in their preconditions (`run-smoketest`, `cv-d-smoke`,
`b7-failclosed-test`), so a full-script run is fine on a fresh dist. Only if you
paste the steps **line-by-line** must you run `dist\monkmode.exe setup` yourself
first — skipping it cost a 30-min re-run on 14/07/2026.

Each script defaults `-Dist` to `repo\dist` (derived from its own location); pass
`-Dist <path>` to point elsewhere. Ephemeral run outputs (transcripts, hosts
backups, and `b7-failclosed-test.ps1`'s pre-corruption `monkmode_settings.pre-b7.ini`)
are written to `C:\Users\samra\monkmode-smoketest\` and are NOT versioned. Do not
delete that folder while a drill is running: the b7 snapshot in it is the only way
back out of the frozen state that drill creates.

| Script | What it proves | Time | Teardown |
|---|---|---|---|
| `_lib.ps1` | Not a drill — the **shared partner-code teardown** every drill below dot-sources. See "Teardown after F79". | — | n/a |
| `run-smoketest.ps1` | Full stack B1–B7 + B5a: install, block, hosts self-heal, watchdog kill drills, SafeBoot, sc-delete resistance, DoH-off policy, auto-lift + clean teardown. `-IncludeClockTest` adds the B4 forward-clock-jump drill (moves the system clock, restored in a `finally`). Expected **83/0** (85 with the clock test) — bench-counted, see the tally below. | ~10 min | natural expiry for the main block; **code lift** for the 4b second block |
| `b7-failclosed-test.ps1` | B7 fail-closed: a tampered `[Integrity] Mac` is NOT re-stamped and the block HOLDS (never lifts). Then ledger 320's addition: the drill **restores the ini bytes it snapshotted**, proves the service resumes, and lifts by code — because a frozen config is opened by nothing at all, not even the code. Expected **21/0**. | ~3 min | ini restore → **code lift** |
| `cv-d-smoke.ps1` | C-core + Section-D usability: partner-code verify/rotate, code-only exit, `--preset`/`--app-preset` expansion, account-default inherit, `stats`, `status` exit lines, `schedule --clear` teardown, and `--all-session-kill` (arm AllSession=yes + live app kill). Short blocks. NO clock manipulation. (CV3, the cooling-off flow, was deleted by ledger 319.) Expected **65/0**. | ~8 min | **code lift** per section |
| `clock-drill-test.ps1` | B4 forward-jump-past-Until (no lift) + B1c backward-roll (no over-extend). Guaranteed-restore (monotonic-anchor Set-Date in a `finally`, **no `w32tm`**), HTTP-Date offset verify, and a feasibility probe that self-defers if w32time yanks manual jumps. **Run FOREGROUND + WATCHED only — never piped under an external timeout** (a hard-kill mid-drill skips the restore). Expected **22/0** when both drills run. | ~4 min | B4: **code lift**. B1c: natural expiry (that IS the assertion), code as the wedge backstop |
| `fx6-drill.ps1` | FX6 clock-change / orphaned-raise drill: arm-vs-retire race, orphan recovery past the 300 s bound, gate-site wiring. `-SkipClock` runs only the race. Expected **24/0** with the clock sub-drills. | ~15 min | race: **code lift** (two blocks, two codes). orphan: natural expiry, code as the wedge backstop |
| `f2-url-smoke.ps1` | The v1.1 URL watcher (B13) probe — **non-elevated, arms nothing, strictly read-only against the browsers**: confirms Chrome/Edge/Brave still expose the omnibox the way the watcher expects, and what shape `ValuePattern.Value` hands back. Run it first when a browser update is suspected of breaking the nudge. It prints whatever URL the address bar currently shows. | ~20 s | n/a (arms nothing) |
| `dns-diag.ps1` / `dns-diag2.ps1` / `dns-diag3.ps1` | Isolated hosts-reload timing diagnostics (no MonkMode). Kept for DNS-cache debugging. | — | yes |

**Every expected count above is BENCH-COUNTED, not observed.** No elevated run has
scored these since the rework — record the real numbers at the first sitting and
replace them here.

## Teardown after F79

Ledger 319 (30/08/2026) removed `monkmode unblock --force`, the cooling-off exit
and `cleanup.ps1`. **There is no unconditional escape, by design**: a running block
ends at its own end time, or with the one-time partner code its arm printed. Nothing
else, at any privilege level the CLI has. Ledger 320 (30/08/2026) converted the five
drills that used `--force` as their inter-section teardown; the recipe lives once, in
**`_lib.ps1`**, which each drill dot-sources:

```powershell
$mmLib = if ($PSScriptRoot) { $PSScriptRoot } else { 'C:\Users\samra\repos\monk-mode\tools\smoke' }
. (Join-Path $mmLib '_lib.ps1')
MMInit -Monk $monk -Hosts $hosts
```

Dot-sourced functions run in the calling script's session state, so `MMCheck` scores
into the drill's own `$pass` / `$fail`. The drills keep their own
`Check`/`SvcState`/`HostsBlocked` (each has slightly different semantics); every name
in `_lib.ps1` is `MM`-prefixed except `ParseCode`, whose output shape
`MonkMode\Program.vb:1228-1235` pins by unit test.

| Helper | What it does |
|---|---|
| `MMInit -Monk <exe> [-Hosts <path>]` | Once, after dot-sourcing. |
| `MMArm "<block args>"` | Arms and **captures stdout**; returns `@{ Out; Code; Id }` and registers the code. Scores a loud FAIL if no code was printed — an arm whose output was thrown away cannot be torn down at all. |
| `ParseCode <lines>` / `ParseCodeId <lines>` | The `XXXXX-XXXXX` code (matched by shape, after the `Emergency unlock code` header) and the block id from `for block N`. |
| `MMCheckRefusedExits <id>` | (a) `unblock --force` → unknown option + exit 1 + still enforcing; (b) `unblock --id N` with no code → exit 1 + still enforcing. 4 checks, ~3 s. |
| `MMCheckWrongCode <code>` | (c) the real code with its first char flipped — shape-valid, so the refusal proves the KDF verify said no. 1 check, ~16 s. |
| `MMLiftWithCode -Code <c>` | (d) submits the code, polls **Stopped-or-gone** (RUNBOOK E9), scores the lift at `<=30 s` + the hosts marker gone. 2 checks. No-op success when the block is already down. |
| `MMResetInstall` | Only while Stopped-or-gone: `sc.exe delete` until `gone`, drop the HKCU `MonkMode_notify` value, then **assert** hosts is marker-free and not read-only. 3 checks. |
| `MMTearDown -Code <c>` | `MMLiftWithCode` + `MMResetInstall`. The ordinary inter-section teardown — what `ForceDown` used to be. 5 checks (3 when the block already expired). |
| `MMSubmitCode <c>` | Fire-and-forget submit, asserts nothing. For the multi-block case: a code opens only the slot that minted it, so with two armed the first submission may match an already-retired slot and the service rightly stays Running. |
| `MMEmergencyLift` | Outer-`finally` backstop: submits every code **this run** minted, then resets. Not a rescue hatch. |

Three rules the helpers exist to enforce, all of them previously got wrong by hand:

1. **Poll Stopped-or-gone, never `gone`** (RUNBOOK E9). A lift — natural or by code —
   leaves the service REGISTERED and stopped. Only `sc.exe delete` reaches `gone`.
2. **Never arm over a stopped registration** (RUNBOOK §3.1) — it freezes instead of
   fresh-rewriting. That is why every teardown ends in `sc.exe delete`.
3. **Assert, never fix.** A hosts marker still present after a lift, or a hosts file
   left read-only, is a DEFECT to report — not something a smoke test tidies away.
   Tidying it is exactly how a real regression goes unnoticed (F78, 313(b)).

**An aborted run leaves the armed block standing until its timer runs out.** Keep
every `--for` short: `--for 1` is always refused (the CLI's >60 s-in-the-future
window check), so 2 minutes is the floor. `b7-failclosed-test.ps1` is the sharp edge —
it deliberately freezes the config, and a frozen config is lifted by **nothing**, not
even the code (`ClassifyPartnerCodeSignal` requires a valid MAC). It snapshots the ini
bytes to `C:\Users\samra\monkmode-smoketest\monkmode_settings.pre-b7.ini` before
corrupting, and restores them in its `finally`. If that restore ever fails, copying
that file back by hand is the only way out. Run it watched.

### Check tally after ledger 320

| Script | Before | After | What changed |
|---|---|---|---|
| `run-smoketest.ps1` | 70 (72 w/ clock) — its header wrongly said 69/71 | **83 (85)** | **+13, none removed or renumbered.** New section `2a` (F79 exit surface, 4) is inserted *before* `2b`, so `2b`–`2g` keep their letters. Section `4b` gained 9: the no-snapshot DoH teardown had to be rebuilt around a real second block, since the only code path that still calls `RemoveDohPolicy` is the service's own `stopMe()`. |
| `cv-d-smoke.ps1` | 28 | **65** (27 direct + 38 from the helpers) | Net −1 direct: `CV1 correct code lifted` + `CV1 hosts block removed` → `MMTearDown`; `CV1 wrong code did NOT lift` → `MMCheckWrongCode`. Added `CV1 the code names its block`, `CV1 rotate: block A's spent code does NOT open block B`, and the F79 refusals. |
| `b7-failclosed-test.ps1` | 10 | **21** | −2: `the teardown removed the service` / `the teardown restored hosts` (now `MMResetInstall`'s). +4 direct: ini snapshot written, ini restored, service re-stamped the MAC once valid again, block still enforcing through the resume. |
| `clock-drill-test.ps1` | 9 | **22** | No direct check changed. B4 gained the refusals + wrong-code + code lift; B1c keeps natural expiry as its assertion. |
| `fx6-drill.ps1` | 13 | **24** | −1: `race: torn down cleanly (service gone, hosts clean)` — `MMResetInstall` scores exactly that now. |

Removed everywhere, with the features they tested: every `--force` assertion, the
cooling-off flow (CV3), `--cancel`, and the `committed` vs `cooling-off` exit tokens
(`status` now prints one exit sentence for every block, always naming `--code`).

---

## Smoke B — the v1.1 owed-drill addendum

Everything below is **owed live proof** for v1.1, to be claimed in one elevated
sitting before the `v1.1` tag. Each fix slice deliberately shipped unit-pinned
and recorded what only a live machine can show; this is the consolidated list, so
the sitting is planned once rather than reconstructed from nine commit messages.

**Pre-flight (in addition to the rules above)**

- `sc.exe delete MONKMODE` first. This machine's normal between-blocks state is
  the service **REGISTERED / AUTO_START / STOPPED**, which is *not* "absent" —
  arming over it freezes rather than fresh-rewrites (`docs\RUNBOOK.md` §3.1).
- Build the dist **`-SelfContained`**. The machine-wide runtimes here are 6.0 and
  8.0 only, so a framework-dependent `dist\` for the .NET 10 build is
  unlaunchable and every assertion fails for the wrong reason.
- Expect **multi-slot** output: assertions written against the v1.0 single-block
  `status` text need re-reading before they are trusted.

**Carried from earlier gates** (folded in here deliberately — a reboot cannot
live in an unattended run): ledger **91** (D4c reboot double-notifier drill),
ledger **104** (mixed-case app-kill), the two S4-gated tamper drills, and F6/M1's
mutex-squat drill (a squatted `Global\MonkModeGuardian` must no longer stand the
SYSTEM watchdog down).

**FX6 — config-writer / race family**

- Orphan-recovery end to end: raise `[Time] TimeChanging`, kill the notifier so
  nothing lowers it, and confirm the service treats it as orphaned past the 300 s
  bound and resumes — the block ends late by at most that bound, never early.
- Gate-site wiring: confirm all three gates read the bounded hold, not the raw
  flag (an armed block, a self-heal and a heartbeat all observed across one
  raised-then-orphaned flag).
- Live arm-vs-retire race: arm a new slot at the instant another retires, and
  confirm the confirmed arm is not clobbered and no duplicate slot appears.

**FX7 — hosts end marker**

- End-marker fixtures: after an arm, hosts carries `#### MonkMode Entries ####`
  … `#### MonkMode End ####`, each on its own line, and a lift removes exactly
  that region.
- **F35 user-tail drill:** hand-add a line *below* the end marker, run a
  retire and a lift, and confirm the line survives byte-for-byte. Then repeat
  from a legacy (no-end-marker) block and confirm the documented one-rewrite
  convergence — the tail is lost exactly once, and never again.

**FX8 — URL watcher**

- Window-gate alt-tab drill: with a blocked URL in the foreground, alt-tab
  during the pass and confirm **no** redirect lands in the new window.
- Non-browser stub: an executable named `chrome.exe` that is not a browser must
  never receive a `SetValue`/Enter.
- Hung-provider recovery: a stub with a hanging UIA provider must stop blocking
  new passes once the 60 s staleness bound elapses (the watcher recovers rather
  than dying for the boot).
- **F12 end to end:** browse to a trailing-dot FQDN of a blocked pattern
  (`youtube.com./shorts/…`) and confirm the redirect now fires.

**FX9 — block page and install dir**

- `install.ps1 -InstallDir` on a data drive: `icacls` shows inheritance broken
  with SYSTEM:F / Administrators:F / Users:RX on the folder **and** the copied
  files; and the default Program Files path is a no-op (no ACL calls, no output,
  byte-identical behaviour).
- Bind/release: `netstat -ano | findstr :80` across a **pending → active**
  boundary (unbound while pending, bound once running) and across a schedule
  window opening and closing.
- Fast re-arm page gap: retire and immediately re-arm, and confirm the page's
  bind gap is the expected short one rather than a permanent loss of the port.
