# MonkMode — Handoff / Continuation Doc

Read this first, then `ARCHITECTURE.md` (component map + bypass surface) and `README` (usage/build).

## ▶ NEXT SESSION — START HERE (22/06/2026)

**Current state:** `monkmode` (the working branch; `master` = untouched upstream Cold Turkey) is reconciled and **pushed** — the 16/06 morning-fix is merged in: **C1** atomic hosts writes (one shared `AtomicHosts.WriteAtomic`), **#2** `adder_Changed` fail-OPEN closed (read-only re-assert), CI → Node-24. Suite **286/286**, clean build, fresh-Opus-verifier SHIP. The Atlas→vault path rename is finished here; the old `HANDOFF.atlas-old-2026-06-16.md` carry-on is folded in below and removed.

> ⚠️ **Everything since the 15/06 audit fixes is BENCH-verified, NOT LIVE-verified.** The standing gate is the elevated smoke — do **#1** before trusting the build on a real machine.

### 1. ▶️ Run the elevated re-smoke — immediate next action (needs Administrator; Claude's `!` is non-elevated)
`dist\` was rebuilt with the 15/06 + 16/06 binaries. From a **genuinely elevated** PowerShell:
```
powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\run-smoketest.ps1 -IncludeClockTest
powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\b7-failclosed-test.ps1
```
**Expect:** `run-smoketest` → **64/0**, auto-lift **~+330s past block start** (not early, not over-run); `b7-failclosed-test` → **10/0**. Log: `C:\Users\samra\monkmode-smoketest\smoketest.log` (verify the LOG, not a verbal "passed"; `cleanup.ps1` is the B6-safe rescue if it hangs). **On green:** flip §5 + the ARCHITECTURE 2026-06-15 note from "bench-verified" to **LIVE-VERIFIED** (no B-row severity changes). NB: the external smoke scripts under `C:\Users\samra\monkmode-smoketest\` are NOT version-controlled.

### 2. ⏳ Decide B5 (DNS/DoH/VPN) — biggest remaining bypass (Critical) — then build B5a
Plan: `docs/B5-network-enforcement-plan.md` §7. The `AskUserQuestion` was interrupted unanswered. Four decisions: (1) **scope** — B5a only (browser DoH-off, recommended MVP) / +B5b (firewall port-53) / +WFP; (2) **collateral** — accept breaking external DNS/DoH tools during a block, or browser-policy-only; (3) **your setup** — stock vs custom firewall/DoH to snapshot+restore; (4) **VPN stance** — confirm VPN/Tor stays out of scope (B10 ceiling). Recommended: **B5a first**.

### 3. Codex debt
`cd C:/Users/samra/repos/Cold-Turkey-Serious ; codex review --base 2a775ff` (read-only) once credits are back — covers the morning-fix hosts commits.

---

**Parked correctness findings (14/06 audit; none an active bypass, all patched/known):** #2 timer re-entrancy, #3 non-atomic `IniFile.Save`, #4 heartbeat TOCTOU, #5 expiry hosts-strip non-atomic, #6–#11 (dup append, strip-parity, DACL/SD edge cases, Release TRACE leak); residual P2 = backward clock change over-extends a block (fail-closed; proper fix = monotonic elapsed, a future B4 change). Table: `docs/handoffs/2026-06-14-overnight-audit.md`. N1/N2 known gaps: the 5th hosts writer (`Service1.vb:1170`, append-mode) + no deterministic `File.Move`-failure test.

**Changelog (newest first):**
- **22/06/2026** — fleet reconciliation: morning-fix merged→monkmode + pushed; vault rename finished; carry-on folded; handoff collapsed.
- **16/06/2026** — morning-fix: C1 atomic hosts writes, #2 hosts fail-OPEN re-assert, CI Node-24 (286/286, verifier SHIP).
- **15/06/2026** — audit fixes #2/#3/#4/#10 + notifier early-lift fix (bench-verified 281/281; re-smoke pending).
- **14/06/2026** — B4/B6/B7 LIVE-VERIFIED (elevated smoke 63/63); overnight audit 0 P0/P1.
- **earlier** — B3 Safe-Mode reg LIVE-VERIFIED 52/52; B1 two-layer watchdog LIVE-VERIFIED 47/47.

**Latest dated handoffs:** `docs/handoffs/2026-06-16-morning-fix.md` · `docs/handoffs/2026-06-14-overnight-audit.md`.
