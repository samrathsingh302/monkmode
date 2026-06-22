# MonkMode (Cold-Turkey-Serious) — TASKS (live planning doc)

**Last updated:** 22/06/2026 · **Maintainer:** Samrath (+ Claude sessions)

How this works: **Read this → plan the session → do the work → update `HANDOFF.md` at session end → when every task is done, archive this file to `docs/archive/TASKS-<date>-done.md`.** Living doc — edit freely as work changes. Buckets: 🔴 Do-next · 🟠 Important · 🟡 Backlog · ⚙ Manual (Samrath-only). Branch = `monkmode`, remote = `monkmode` (NOT origin); `master` = untouched upstream Cold Turkey.

> ⚠️ **Everything since the 15/06 audit is BENCH-verified, NOT LIVE-verified.** The standing gate is the elevated smoke (⚙ below). The only smoke *log* on disk is a stale **FAILED 53/11 pre-fix run** — do **not** read it as the current result; a clean post-fix smoke has not been run.

---

## 🔴 Do-next
_(none a Claude session can do autonomously — the head-of-queue items need Administrator (smoke) or a Samrath decision (B5). See ⚙.)_

## 🟠 Important
- [ ] **On green smoke: flip bench → LIVE-VERIFIED** — once the elevated re-smoke passes (⚙ G1/G2), update `HANDOFF.md` §state + `ARCHITECTURE.md` 2026-06-15 note (:152-154) + the B-rows from "bench-verified"/"re-smoke pending" to LIVE-VERIFIED. **No B-row severity changes** — wording only. Blocker: G1+G2 green. _(source: HANDOFF.md:17)_
- [ ] **Build B5a (after the B5 decision)** — browser-DoH-off policy, B3-style self-heal + a pure testable layer + tests + smoke-extension. Blocker: B5 decision (⚙). `docs/B5-network-enforcement-plan.md:147,68-80`. _(source: harvest)_
- [ ] **AppDomain.UnhandledException backstop** — a catch-all that re-asserts the block before process death; scope as its own slice. Flagged-not-implemented. _(source: docs/handoffs/2026-06-16-morning-fix.md:39-41)_

## 🟡 Backlog
- [ ] **N1 — 5th hosts writer** — `Service1.vb:1170` uses `AppendAllText` (append-mode; can't blank hosts). Left as-is; capture as a ticket. _(source: docs/handoffs/2026-06-16-morning-fix.md:42-44)_
- [ ] **N2 — deterministic `File.Move`-persistent-failure test** — retry/fail-closed logic exists but the always-fails branch isn't exercised. _(source: docs/handoffs/2026-06-16-morning-fix.md:45-46)_
- [ ] **Parked audit P3s #5–#9** (none an active bypass): #5 `stopMe()` non-atomic hosts strip (`Service1.vb:1062-1066`); #6 `add_to_hosts` double-fire dup (`Service1.vb:1122-1146`); #7 strip-parity whitespace (`Blocker.vb:224` vs `Service1.vb:731-735`); #8 `RemoveDenyDeleteAce` strips first match (`ServiceSecurity.vb:106-111`); #9 `Step_` continues after failed SD-restore (`Program.vb:227-235`). Each: fix-or-defer with a regression test. _(source: docs/handoffs/2026-06-14-overnight-audit.md:53-57)_
- [ ] **Residual P2 — backward-clock over-extend** — a backward clock change over-extends a block (fail-closed, known). Proper fix = monotonic elapsed, a future **B4** change. _(source: HANDOFF.md:27)_

## ⚙ Manual (Samrath-only)
- [ ] **★ G1 — Run the elevated re-smoke (immediate next action)** — the only thing that closes bench→live. From a genuinely elevated PowerShell: `powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\run-smoketest.ps1 -IncludeClockTest` → **expect 64/0**, auto-lift ~+330s. **Verify the LOG** at `C:\Users\samra\monkmode-smoketest\smoketest.log`, not a verbal pass. Needs Administrator (Claude's `!` is non-elevated) + a rebuilt `dist\` (run `tools\build-dist.ps1` first). ⚠ The smoke scripts live OUTSIDE the repo (`C:\Users\samra\monkmode-smoketest\`, **un-versioned — not recoverable from git**). _(source: HANDOFF.md:11-17)_
- [ ] **G2 — Run `b7-failclosed-test`** — `powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\b7-failclosed-test.ps1` → **expect 10/0**. Needs Administrator. _(source: HANDOFF.md:15-17)_
- [ ] **B5 — Decide DNS/DoH/VPN (Critical — biggest remaining bypass)** — 5 open questions in `docs/B5-network-enforcement-plan.md` §7 (:158-174): (1) scope B5a-only vs +B5b firewall vs +WFP; (2) collateral tolerance; (3) stock vs custom firewall/DoH to snapshot+restore; (4) confirm VPN/Tor out of scope (B10 ceiling); (5) live-verify B1-B7 first vs B5a in parallel. **Recommend: B5a first.** Blocker: your decision. _(source: HANDOFF.md:19-20)_
- [x] **dotnet test gate — ✅ VERIFIED 22/06 (286/286, 0 failed)** via `C:\Users\samra\.dotnet\dotnet.exe test MonkMode.sln` (user-scoped SDK, not on PATH). Re-run this before any release. _(source: fleet-finalisation 22/06 — real run)_
- [ ] **Codex re-run debt** — `cd C:/Users/samra/repos/Cold-Turkey-Serious ; codex review --base 2a775ff` (read-only) — covers the morning-fix hosts commits. Blocker: ChatGPT-sub credits. _(source: HANDOFF.md:22-23)_
- [ ] **Folder rename `Cold-Turkey-Serious` → `Monk-Mode`** (machine-level) — the GitHub repo is already `monkmode`; only the local folder + any launcher/scheduled-task path refs remain. Plan: `Rename-Item` → if locked, fresh `git clone` to `C:\Users\samra\repos\Monk-Mode`, re-point the remote, update memory + path refs. _(source: archived CONTINUE.md task 1)_
- [ ] **Delete the `_old/*` anchor branches** — verified fully absorbed (0-orphan; all three tips are ancestors of `monkmode`). Kept as insurance — **safe to run:** `git -C C:/Users/samra/repos/Cold-Turkey-Serious branch -D _old/master _old/monkmode _old/overnight-audit-2026-06-14`. _(source: fleet-finalisation 22/06 verify)_
- [ ] **`master` branch — kept deliberately** — it's the untouched upstream Cold Turkey GPL provenance anchor (CLAUDE.md "never work on it"). Git-proven merged into `monkmode` but NOT pruned (not a working branch). Leave as-is. _(source: fleet-finalisation 22/06 verify)_

---
_Codex two-pass note: no code changed in the 22/06 fleet run (docs only), so Codex sits out; the `--base 2a775ff` pass above is the pre-existing debt. The .NET unit-test + Release-build + elevated-smoke gates are recorded as Manual because no .NET SDK is on PATH and the smoke needs Administrator + a live service (the fence forbids arming it autonomously)._
