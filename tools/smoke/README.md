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
| `dns-diag.ps1` / `dns-diag2.ps1` / `dns-diag3.ps1` | Isolated hosts-reload timing diagnostics (no MonkMode). Kept for DNS-cache debugging. | — | yes |

If a block ever jams, the unconditional escapes (in order): `dist\monkmode.exe
unblock --force`, then `tools\smoke\cleanup.ps1` (elevated).
