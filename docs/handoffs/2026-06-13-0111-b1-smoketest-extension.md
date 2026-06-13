---
status: done
agent: b1-smoketest-extension
goal: Author (NOT run) the B1 extension of the elevated smoke test — both watchdog layers (SCM recovery + mm_guard guardian) plus the expiry no-restart-loop assertion; make cleanup.ps1 guardian-aware. Fence: no live service/CLI runs.
outcome: Shipped. run-smoketest.ps1 extended 27 → 47 checks (5 baseline policy/guardian + 12 kill drills K1–K4 + 3 expiry-finality), block bumped 3 → 5 min so the B2 tamper drills (~100s) + B1 drills (~75s) can't collide with expiry; teardown AND cleanup.ps1 now disarm B1 before killing (sc failure reset= 0 actions= "" → kill mm_guard+service in a retry loop). Both scripts parse clean under the PS 5.1 parser; no repo code touched (suite stays 123/123). NOT yet run — that's Samrath's elevated step.
gotchas: The carry-on's "≤10s" bounds are nominal — worst case is one full 10s tick AFTER the kill plus process start, so K2/K3/K4 use 15s ceilings and print actual elapsed (K1 keeps 5s: SCM delay is only 1s). K1/K4 must observe the service DOWN before polling for Running — a stale status read in the instant after taskkill would fake-pass the restart check. Disabling actions needs cmd /c quoting (sc failure MONKMODE reset= 0 actions= "") — PowerShell mangles the empty actions= argument natively; restore uses reset= INFINITE actions= restart/1000/restart/1000/restart/1000 (the non-crash failure flag is a separate setting sc failure never touches, so restoring actions restores the full policy). The expiry watch is a tight 500ms poll for 30s — a fast stopMe⇄recovery kill/respawn cycle (~1.5s+) could slip between lazy samples. mm_notify legitimately lives ~11s past Done=yes (toast then self-exit) — the stray check polls with a 15s grace, don't tighten it. Teardown/cleanup order matters: guardian first-ish with recovery disarmed, else the watchdogs fight the teardown.
carry-on: "Next step is SAMRATH'S, not a build slice: run the extended smoke test from an ELEVATED PowerShell — powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\run-smoketest.ps1 — expect 47 passed / 0 failed, ~8 min (watch for the tray toast at expiry; cleanup.ps1 elevated is the rescue if anything sticks). THEN, next session, ONE slice: if 47/47 → flip ARCHITECTURE §3/§4 B1 row to live-verified (severity High → Medium), update HANDOFF §5/§6 + README if it claims B1 unverified, commit docs; if any check fails → paste smoketest.log, diagnose the failing layer (K1=SCM policy, K2=peer spawn, K3=CreateProcessAsUser, K4=guardian SCM rights, expiry=stopMe⇄recovery loop) — the loop case especially is the never-run-live interaction. Phase 2 (THREATMODEL.md) stays DEFERRED."
---

# 13/06/2026 — B1 smoke-test extension: 27 → 47 checks, authored only

## Decomposition (ORCHESTRATION §0)
Single-context authoring slice: one cohesive PowerShell deliverable whose
every timing bound derives from source constants (Service1.TimerIntervalMs,
ServiceTools recovery consts, guardian cadence) — splitting across agents
would cost coherence for no parallel gain. One writer; verification =
PS 5.1 parse + self-review (the elevated run is the real verifier).

## What changed (all outside the repo, in C:\Users\samra\monkmode-smoketest\)
1. **run-smoketest.ps1** — new `Wait-Condition` poll helper (returns elapsed
   seconds or -1, so the log shows how fast each layer reacted); block
   3 → 5 min with `$blockStart`-anchored lift deadline (+420s).
   - **§2 baseline (+5)**: qfailure = 3× RESTART / 1000 ms each / reset
     INFINITE (matches ServiceTools consts exactly); mm_guard appears ≤25s
     (first 10s tick + slack); mm_guard SessionId = 0.
   - **§2c kill drills (+12)**: K1 kill service → SCM restart ≤5s + still
     exactly one mm_guard; K2 kill mm_guard → respawn (new PID) ≤15s; K3 kill
     mm_notify → relaunch ≤15s AND SessionId == the script's own session
     (proves CreateProcessAsUser, not a session-0 ghost); K4 recovery
     disabled→verified→kill→guardian-only restart ≤15s→policy
     restored→re-verified; block still enforced after all drills.
   - **§4 expiry (+3)**: 30s/500ms watch — service never Running/StartPending
     and no MonkMode_srv process (no restart loop), no mm_guard sighting,
     mm_notify gone ≤15s grace.
   - **Teardown**: B1-aware (disarm recovery, kill guardian+service in a
     retry loop, then the rest).
2. **cleanup.ps1** — same B1-aware kill order + header doc.

## Why the deviations from the carry-on spec
- 3-min block → 5-min: drill arithmetic (above) made mid-drill expiry likely.
- "≤10s" → 15s ceilings on tick-driven drills: kill landing just after a tick
  makes >10s physically legitimate; a 10s bound would flake on a correct
  build. Elapsed time is printed so a pathological 14s would still be seen.
