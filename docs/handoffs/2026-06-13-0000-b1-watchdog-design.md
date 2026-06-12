---
status: needs-decision
agent: b1-watchdog-design
goal: Phase 3 B1 (watchdog / force-kill resistance) — design the proper service ⇄ guardian restart pair, ship the fence-safe first increment with tests, surface the one architecture decision for layer 2.
outcome: Layer 1 (SCM auto-restart on force-kill) code-complete + tested (NOT live-verified); layer 2 fail-safe gate (ShouldRestartPeer) tested but unwired; suite 81→95 green; verifier SHIP. Layer-2 guardian form is the open decision (see carry-on).
gotchas: SCM recovery + the live guardian wiring are NOT live-verified — fence forbids running the service; the elevated smoke test is the real gate (kill service → SCM restarts it; sc qfailure MONKMODE shows policy). ShouldRestartPeer has NO caller yet (additive; timer wiring is the next increment). Recovery policy lives as Friend Consts in ServiceTools — they ARE the policy the SCM gets AND what the tests pin; don't weaken (finite reset / <3 actions / non-crash off all weaken B1). InstallAndStart swallows SetRecoveryOptions failures by design (recovery must never block a block from arming).
carry-on: "Phase 3 B1, layer 2 (the mutual watchdog pair) needs Samrath's architecture pick before wiring — see the DECISION section below. Once picked: wire the chosen guardian + the service-side timer call to ShouldRestartPeer, then it ALL needs the elevated smoke test (incl. layer 1's SCM restart). Phase 2 (THREATMODEL.md) stays DEFERRED."
---

# 13/06/2026 — B1 watchdog: design + layer-1 increment

## Decomposition (ORCHESTRATION §0 — how it split)
B1 is a coupled, single-file-set change on the **sacred enforcement surface**
(Service1.vb + ServiceTools.vb). Parallel writers would collide (C1) on those
two files, so the correct split is **serialise, don't fan out**: one writer
(orchestrator, full context from reading the 8 source/contract files) + **one
fresh-context verifier** on the diff (the genuine second agent — independence is
the value, M10). No fleet — right-sizing per §2.7 (don't spawn 10 agents for a
1-writer job). Research was already complete (no read fan-out needed). Verifier
verdict: **SHIP** (P/Invoke marshalling, memory/handle hygiene, SCM semantics,
fail-closed gate all confirmed against the Win32 contract; 95/95).

## The B1 design (layered defense — honest ceiling)
Force-killing the service ends enforcement; `CanStop=False` only blocks the
graceful stop. The old `MM_notify2` twin was a weak watchdog: a **user-session**
WinForms app, one-directional (it restarted the notifier, nothing restarted it),
and it never guarded the **service** at all. "Reinstate properly" = guard the
enforcement core with a **protected** peer and make restart **mutual**.

- **Layer 1 — SCM FailureActions (shipped this increment).** The cheapest,
  most robust half and needs no extra process: at install the CLI tells the
  Service Control Manager to auto-restart `MONKMODE` after abnormal termination.
  Force-kill → SCM restarts it. Policy: 3× SC_ACTION_RESTART, 1 s delay, reset
  period INFINITE (count never resets → never gives up), + restart-on-non-crash
  flag. `ServiceTools.SetRecoveryOptions`, called best-effort from
  `InstallAndStart`.
- **Layer 2 — mutual service ⇄ protected guardian (designed, gate tested,
  NOT wired).** Covers what SCM doesn't: restart the guardian, and re-launch the
  notifier (the old twin's proper job). The service's timer will, each tick,
  call `Service1.ShouldRestartPeer(count, blockActive, exeExists)` and
  `Process.Start` the guardian if it returns true; the guardian reciprocally
  restarts the service (it has SYSTEM rights via the service, or its own). The
  gate fails SAFE: only while the block is active (fail-CLOSED via
  `Not BlockHasExpired`), only if the exe exists, only if none already running.

**Honest residual (do not oversell — matches CLAUDE.md's casual→determined
bar):** a scripted near-simultaneous double-kill within the ~1 s restart window,
a SYSTEM-token kill that also runs `sc failure MONKMODE reset= 0` to disable
recovery, or suspend-then-kill of both processes still wins. True kill-immunity
needs a Protected Process Light cert (anti-malware only) or a signed kernel
driver — out of scope (B10-level). B1's realistic prize is **survives a single
force-kill and survives reboot re-arm**, raising it Critical → High/Medium.

## Delivered this increment (fence-safe, additive, no behaviour change)
1. `MonkMode/ServiceTools.vb` — `SetRecoveryOptions` (ChangeServiceConfig2W
   P/Invoke: SERVICE_FAILURE_ACTIONS + FLAG), recovery policy as `Friend Const`s,
   wired best-effort into `InstallAndStart`.
2. `MonkMode_srv/.../Service1.vb` — pure `Friend Shared ShouldRestartPeer` gate
   (no caller yet — wiring is layer 2).
3. `MonkMode.Tests/WatchdogTests.cs` — 14 tests (9 gate fail-safe incl. the
   fail-closed BlockHasExpired tie; 5 recovery-policy drift guards). **95/95.**

## DECISION NEEDED — the layer-2 guardian's form (Samrath picks)
What is the "protected helper"? Trade-off (simplicity ↔ capability, §4.5/4.6):
- **(A) SYSTEM child process spawned by the service** *(recommended)* — lightest;
  no new SCM entry / install-uninstall change. With layer 1's SCM recovery on the
  service, single-kills of either side self-heal (service killed → SCM restarts →
  re-spawns guardian; guardian killed → service re-spawns). Weakest only on a
  near-simultaneous double-kill (already the residual).
- **(B) Dedicated second service `MONKMODE_WD`** — strongest: auto-starts on
  reboot independently, both SYSTEM. Cost: new project/exe, new SCM
  registration + uninstall handling, enlarges the B6 "sc delete" surface (now two
  services). The "max hardening" option; its main edge (independent reboot
  auto-start) is largely already delivered by layer-1 SCM recovery on the primary.
- **(C) Reuse mm_notify as the guardian** — rejected: user-session = trivially
  killable, dies at logoff; exactly why the old twin was weak.

Recommendation: **A** (lightest, and layer 1 already carries the heavy lifting).
B if Samrath wants the reboot-independent second service for CV "defense in depth".

## Evidence
- Verifier (fresh context) confirmed marshalling/offsets/free-paths/handle-close
  on every path, SCM INFINITE-reset semantics, and fail-closed gate; ran 95/95.
- Tests grounded in code: gate `Service1.vb` ShouldRestartPeer + BlockHasExpired
  (~288–294, 373–377); policy consts `ServiceTools.vb` (~48–58).
- Not live-run (fence). Layer 1 + any layer-2 wiring need the elevated smoke test.
