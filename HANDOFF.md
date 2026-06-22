# MonkMode (Cold-Turkey-Serious) — HANDOFF (thin single-writer pointer)

Read this first, then `ARCHITECTURE.md` (component map + bypass surface) and `README` (usage/build). **Planning/backlog → [`TASKS.md`](TASKS.md).**

**Current state (22/06/2026):** `monkmode` (the working branch; `master` = untouched upstream Cold Turkey), **pushed and in sync with `monkmode/monkmode`** (0 ahead/0 behind), tree clean. **Unit-test gate VERIFIED green this run: `dotnet test` → 286/286** (0 failed/0 skipped, via the user-scoped SDK `C:\Users\samra\.dotnet\dotnet.exe`; CA1416 platform warnings only). Clean build; 16/06 morning-fix merged (C1 atomic hosts writes, #2 hosts fail-OPEN closed, CI Node-24); fresh-Opus-verifier SHIP.

> ⚠️ **Everything *security-behavioural* since the 15/06 audit is BENCH-verified, NOT LIVE-verified.** The standing open gate is the **elevated smoke** (needs Administrator + arms a live LocalSystem service — a Claude session cannot run it; it's the fence). The only smoke *log* on disk is a stale **FAILED 53/11 pre-fix run** — do **not** read it as current; a clean post-fix smoke (`run-smoketest -IncludeClockTest` → 64/0; `b7-failclosed` → 10/0) has not yet been run. See `TASKS.md` ⚙ G1/G2.

**Health: green on code (286/286 unit) + git; the live security smoke is the one open gate (⚙, needs you elevated). Critical open decision: B5 DNS/DoH/VPN.**

**Next → see [`TASKS.md`](TASKS.md).** (★ the elevated re-smoke + the B5 decision are the head of the queue.)

**Parked correctness findings (14/06 audit; none an active bypass, all patched/known):** #2 timer re-entrancy, #3 non-atomic `IniFile.Save`, #4 heartbeat TOCTOU, #5–#11 (expiry strip, dup append, strip-parity, DACL/SD edge cases, Release TRACE leak); residual P2 = backward-clock over-extend (fail-closed; proper fix = monotonic elapsed, future B4). N1/N2 gaps: 5th hosts writer (`Service1.vb:1170`, append-mode) + no deterministic `File.Move`-failure test. Full table: `docs/handoffs/2026-06-14-overnight-audit.md`. (All migrated into `TASKS.md`.)

**Changelog (newest first):**
- **22/06/2026** — fleet finalisation: `dotnet test` re-verified **286/286** (real run, user-scoped SDK); installed `TASKS.md`; collapsed this HANDOFF to the lean shape; reduced `CONTINUE.md` to a pointer; archived spent `VERIFY-AND-CLOSEOUT-2026-06-14.md`; pruned merged `fix/morning-2026-06-16` + `overnight-audit-2026-06-14` (kept `master` = GPL anchor); `_old/*` deletion routed to TASKS ⚙. Pushed to monkmode.
- **22/06/2026** — fleet reconciliation: morning-fix merged→monkmode + pushed; vault rename finished; carry-on folded.
- **16/06/2026** — morning-fix: C1 atomic hosts writes, #2 hosts fail-OPEN re-assert, CI Node-24 (286/286, verifier SHIP).
- **15/06/2026** — audit fixes #2/#3/#4/#10 + notifier early-lift fix (bench-verified 281/281; re-smoke pending).
- **14/06/2026** — B4/B6/B7 LIVE-VERIFIED (elevated smoke 63/63); overnight audit 0 P0/P1.
- **earlier** — B3 Safe-Mode reg LIVE-VERIFIED 52/52; B1 two-layer watchdog LIVE-VERIFIED 47/47.

**Latest dated handoffs:** `docs/handoffs/2026-06-22-1958-fleet-finalisation.md` · `docs/handoffs/2026-06-16-morning-fix.md` · `docs/handoffs/2026-06-14-overnight-audit.md`.
