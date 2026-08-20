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
backups) are written to `C:\Users\samra\monkmode-smoketest\` and are NOT
versioned.

| Script | What it proves | Time | Self-cleaning |
|---|---|---|---|
| `run-smoketest.ps1` | Full stack B1–B7 + B5a: install, block, hosts self-heal, watchdog kill drills, SafeBoot, sc-delete resistance, DoH-off policy, auto-lift + clean teardown. `-IncludeClockTest` adds the B4 forward-clock-jump drill (moves the system clock, restored in a `finally`). Baseline **71/0** (with clock test). | ~7 min | yes (+ `cleanup.ps1` fallback) |
| `b7-failclosed-test.ps1` | B7 fail-closed: a tampered `[Integrity] Mac` is NOT re-stamped and the block HOLDS (never lifts). Standalone. Baseline **10/0**. | ~1 min | yes (exits via `unblock --force`) |
| `cv-d-smoke.ps1` | C-core + Section-D usability: partner-code verify/rotate, committed code-only exit, cooling-off flow, `--preset`/`--app-preset` expansion, account-default inherit, `stats`, `status` exit lines, `schedule --clear` teardown, and `--all-session-kill` (arm AllSession=yes + live app kill). Short blocks, `unblock --force` between each, global-finally cleanup. NO clock manipulation. | ~7 min | yes |
| `clock-drill-test.ps1` | B4 forward-jump-past-Until (no lift) + B1c backward-roll (no over-extend). Guaranteed-restore (monotonic-anchor Set-Date in a `finally`, **no `w32tm`**), HTTP-Date offset verify, and a feasibility probe that self-defers if w32time yanks manual jumps. **Run FOREGROUND + WATCHED only — never piped under an external timeout** (a hard-kill mid-drill skips the restore). | ~3 min | yes |
| `cleanup.ps1` | Emergency teardown: disarm SCM recovery, kill guardian+service, strip B6 deny-DELETE ACE, delete the service, restore hosts, remove SafeBoot keys + DoH snapshot. Run if any script hangs or leaves the box blocked. | — | n/a |
| `f2-url-smoke.ps1` | The v1.1 URL watcher (B13) probe — **non-elevated, arms nothing, strictly read-only against the browsers**: confirms Chrome/Edge/Brave still expose the omnibox the way the watcher expects, and what shape `ValuePattern.Value` hands back. Run it first when a browser update is suspected of breaking the nudge. It prints whatever URL the address bar currently shows. | ~20 s | n/a (arms nothing) |
| `dns-diag.ps1` / `dns-diag2.ps1` / `dns-diag3.ps1` | Isolated hosts-reload timing diagnostics (no MonkMode). Kept for DNS-cache debugging. | — | yes |

If a block ever jams, the unconditional escapes (in order): `dist\monkmode.exe
unblock --force`, then `tools\smoke\cleanup.ps1` (elevated).

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
