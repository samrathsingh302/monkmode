# Overnight audit — Cold-Turkey-Serious (MonkMode) — 14/06/2026

Autonomous, read-only correctness sweep of the last hardening wave. Orchestrated:
5 fresh-context `auditor` subagents (one concern each) + 1 refuting `verifier`
subagent + Codex (primary) + the orchestrator's own full read of the core files.

## TL;DR (60-second read)

**Audit window:** `06490f9..322b63c` — the **B7 + B4 + B6 + cross-slice** wave (11
commits). No commit is literally dated 14/06 (HEAD `322b63c` is 13/06 14:08); the
14/06-early HANDOFF describes this wave as "last session", so I scoped to it and
**stated** the window (the prompt's "empty/ambiguous → widen + state" rule).
**Pre-audit commit:** `322b63c` (HEAD at start; working tree clean).
**Branch:** `overnight-audit-2026-06-14`. **Doc-fix commit:** `36f0572`.

**Health:** build ✅ (Release, 0 errors, all 4 projects + tests) · tests **273/273
→ 273/273** (0 skipped; only docs changed) · lint/typecheck ✅ (no separate linter
in this VB.NET repo — compiler clean; `Option Explicit On`, `Option Strict Off` by
inherited design) · **smoke ❌ NOT run** (fence: cannot run the service/CLI; the
elevated `run-smoketest.ps1` 61-check + `b7-failclosed-test.ps1` + B4 clock drill
remain Samrath-gated).

**Found:** **0 P0 · 0 P1 · 3 P2 · 8 P3.** **Fixed:** 2 (the doc-drift cluster,
1 commit). **Reverted:** 0. **Left for you (morning):** 9 (all code findings —
parked with exact patches; see §"Reverted / NOT fixed").

**Verdict:** **GREEN + safe to keep.** The session's B4/B6/B7 + cross-slice
hardening is genuinely **fail-closed**: no early clock-lift, no CLI-seam bypass,
B6 is brick-safe by construction, and B7 is gated at all 4 re-stamp sites. The
repo's *code* ends byte-identical to how it started (only two `.md` files changed).
The single thing standing between "code-complete" and "verified" is unchanged: the
**elevated live smoke test**, which you must run.

> **⚠️ Process incident (read this):** one of my read-only auditor subagents
> (cross-slice concern) used **Bash to append to
> `C:\Users\samra\Atlas\repos\_shared-context\AUDIT_LOG.md`** despite the fence. I
> did **not** revert it — `_shared-context` already holds tonight's concurrent
> appends from the sibling overnight sessions (HA-hevy, mission-control), the table
> got mangled by the concurrent writes, and a revert now would itself race those
> still-active sessions (the exact clash the fence exists to stop). **Leave it for
> the morning AUDIT_LOG roll-up.** Root cause + the fix are in §"Lessons".

---

## Findings (every defect, deduped, post-verification severities)

| # | Sev | Finding | Evidence (file:line + verbatim) | What today's change did | Fix status | Verified by | Confidence |
|---|-----|---------|----------------------------------|-------------------------|------------|-------------|------------|
| 1 | **P2** | **Doc-drift cluster** — HANDOFF header claimed the suite is `265/265` and the tip is `13ec2fc`; B6 was called "UNCOMMITTED" in two committed docs though it landed in `097eaaa`. Actively misleads the next session. | `HANDOFF.md:7` "`(suite 265/265), committed + pushed through 13ec2fc`" (real: 273/273, tip `322b63c`); `HANDOFF.md:238` "`B6 — service-deletion resistance (working tree, UNCOMMITTED)`"; `ARCHITECTURE.md:144` "`(2026-06-13, working tree — NOT yet committed, NOT yet live-verified)`". | The CLI-seam fix `702091a` (+8 tests → 273) landed *after* the header was written at `94e0466` and never updated it; B6 `097eaaa` shipped code without touching the B6 doc rows. | **FIXED** `36f0572` | orchestrator (counts verified by `dotnet test`); auditor-E flagged | High |
| 2 | **P2** | **Timer re-entrancy.** `System.Timers.Timer` is `Enabled=True` with **no `AutoReset=False`, no `SynchronizingObject`, no lock anywhere in `MonkMode_srv`**. A tick that exceeds 10 s (it does `Process.GetProcesses()` + file/registry/SCM I/O) re-enters on another threadpool thread, racing `lastMonoMs` and the `[Time]HighWater` read-modify-write. | `Service1.vb:110-111` "`Me.timer.Enabled = True`" (no `AutoReset`); `:325-327` raced `lastMonoMs`; `:486-503` concurrent same-file `Load`→`Save`. Project grep for `SyncLock\|Interlocked\|Monitor\|SynchronizingObject\|AutoReset` → **no matches**. | B4 added the `lastMonoMs` shared field + the per-tick HighWater RMW to an already-unsynchronized timer. | **PARKED** (enforcement-core; can't live-verify) — fix: `AutoReset=False` + re-`Start()` at end, **or** `Monitor.TryEnter` skip-guard. | auditor-B + refuting verifier (**both: NO early-lift** — genuinely tried to construct one and failed; last-writer-wins on a value both ticks read at the same stored HighWater, mono-cap bounds each credit to real elapsed, torn reads fail closed) | High (real race) / High (no early-lift) |
| 3 | **P2** | **Non-atomic `IniFile.Save` + a 2nd cross-process writer → freeze-at-expiry.** `Save` truncate-rewrites with no atomicity; the notifier (`SystemEvents_TimeChanged`, with a 2 s inter-save gap) and the service heartbeat both write the ini with no cross-process lock. If corruption lands on the *expiry* tick, `ClassifyHeartbeat` returns `Hold` not `Lift` → block freezes ON (must re-arm / `unblock --force`). | `IniFileVb.vb:64` "`New StreamWriter(sFileName, False)`"; `:27` Load `FileShare.ReadWrite`; service `Service1.vb:503` save; notifier `Form1.vb:204,234` saves with `Thread.Sleep(2000)` between (`:206`). | B7's per-tick MAC re-stamp made the heartbeat a frequent writer; the notifier's MAC-gated clock-comp is the 2nd writer. | **PARKED** (touches 3 writers; HANDOFF already logs it deferred — "not changed blind") — fix: temp-file + `File.Replace` (atomic) + one shared named mutex. | auditor-D + auditor-E + refuting verifier (all: **fail-CLOSED** — freezes ON, no bypass) | High mechanism / Medium frequency |
| 4 | **P3** | **Heartbeat Restamp TOCTOU** (initially flagged P2; **DOWNGRADED to P3** by the refuting verifier). The Restamp branch *reloads* the ini and re-stamps a fresh valid MAC over it, but `macValid` was computed against the *earlier* read — so a script that swaps a past `[Time]Until` (+ stale MAC) into the read→reload window gets the service to bless it, and the block lifts next tick. | `Service1.vb:308` macValid over the `:298` read; `:486-487` `Dim iniFile = New IniFile : iniFile.Load(...)` (2nd read); `:502` `RestampMacWithExistingKey` → `:503` Save; `CanonicalFromIni:853-866` pulls `Until` from the **reloaded** ini. | The B7 fail-open fix (`2da5c5b`) routed the heartbeat through `ClassifyHeartbeat`, but the Restamp branch reloads (the verified `iniFile` is scoped to the `Try`). The sibling fixes (`AppendAddToHosts`, OnStart, notifier) correctly validate the **same** in-memory object they mutate — only the heartbeat reloads. | **PARKED** — fix: re-`ConfigMacIsValidForIni(iniFile)` after the reload, skip restamp if invalid (fail-closed). | auditor-A (found it independently) + refuting verifier: **no attacker stopped by B7 can exploit it** — winning the race needs a script, and any script-runner can instead `UnprotectKey` the **LocalMachine** DPAPI blob (`ConfigIntegrity.vb:116,131`) and forge directly (100% reliable). Also self-defeats on misfire (freeze, not lift). | High mechanism / High "grants nothing beyond the documented B7 ceiling" |
| 5 | **P3** | **`stopMe()` expiry hosts-strip is non-atomic** — truncate-then-write with **no `Try/Finally`**, unlike the timer-repair path. A write fault between truncate and write loses the user's preserved hosts content + leaves hosts writable. (Largely **pre-existing**; B6/B3 only added teardown steps around it.) | `Service1.vb:1062-1066` "`New FileStream(hostDirS, FileMode.Create, ...)`" → `sw2.Write(original)` → `Close()` (no guard) vs the guarded repair path `:387-396`. | Pre-existing inherited strip; not modified this session. | **PARKED** (data-loss class — touch only with the live smoke test) — fix: temp+replace, mirror the repair `Finally` (but NOT a naive read-only re-assert — that would leave clean hosts locked at expiry). | auditor-D | Medium (crash-window only) |
| 6 | **P3** | **`add_to_hosts` double-fire dup** — a default-filter `FileSystemWatcher` can fire `Changed` twice for one CLI write; both passing the existence check before the delete append duplicate `127.0.0.1` lines to hosts + the snapshot, no de-dup. Pre-existing/admin-only channel. | `Service1.vb:1122-1146` (`adder_Changed`: append `:1128`, snapshot append `:1138`, delete `:1143`). | Pre-existing. | **PARKED** (cosmetic; documented residual) — fix: debounce + de-dup vs current hosts. | auditor-D + auditor-E | Medium |
| 7 | **P3** | **Strip-parity whitespace divergence** — CLI `StripOurBlock` trims *all* trailing CR/LF/space/tab; service `StripMonkModeBlock` drops only *one* terminator. They disagree on a user trailing blank line before the marker (whitespace-only; **no content loss**), contradicting the "same data-loss-safe strip" claim. Pre-existing. | `Blocker.vb:224` "`.TrimEnd(CChar(vbCr), CChar(vbLf), \" \"c, CChar(vbTab))`" vs `Service1.vb:731-735`. | Pre-existing. | **PARKED** — fix: align `StripOurBlock` to drop-one-terminator, or drop the "identical" wording; add a CLI parity test. | auditor-D | High (it's a real divergence) / Low impact |
| 8 | **P3** | **`RemoveDenyDeleteAce` strips only the first match.** An attacker who hand-adds a *second* `(D;;SD;;;BA)` via `sc sdset` leaves a duplicate after teardown → `unblock --force` step-4 delete still DELETE-denied. Self-inflicted (the service never stacks duplicates), fully recoverable (re-run / reinstall rewrites the DACL). | `ServiceSecurity.vb:106-111` `RemoveDenyDeleteAce` returns after the first `IndexOfDenyAce`. | B6 new code. | **PARKED** (P3 self-sabotage) — fix: loop until `Not SddlHasDenyDelete`, or write a known-good DACL. | auditor-C | High |
| 9 | **P3** | **`Step_` continues after a failed SD-restore.** If `RestoreDefaultServiceSd` throws (SCM open fails) the escape hatch prints "skipped" and proceeds to `DeleteServiceByName`, which then fails (deny still present). Requires SCM unreachable while *elevated* (near-impossible); fully recoverable by re-running. | `Program.vb:227-235` `Step_` swallows + continues; `ServiceTools.vb` `RestoreDefaultServiceSd` throws on SCM-open failure; sequence `Program.vb:202`→`:206`. | B6 new code. | **PARKED** (P3) — fix: if step 3 hard-fails, skip/retry before step 4. | auditor-C | Medium |
| 10 | **P3** | **Release `TRACE` leak.** `DefineTrace` is on in Release, so `IniFile.Save/Load`'s `Trace.WriteLine("...{0}={1}")` echoes the DPAPI-protected `[Integrity]Key` blob + `Mac` + all 3DES ciphertext to the process-global `OutputDebugString` (DebugView-capturable) on every config read/write by the LocalSystem service. **Grants nothing** (same values already in the on-disk ini the same admin can read). | `IniFileVb.vb:70` "`Trace.WriteLine(String.Format(\"Writing Key: {0}={1}\", k.Name, k.Value))`", `:47` similar; reached via `Blocker.StampFreshMac`→`ini.Save` and every `ConfigMacIsValidForIni`→`ini.Load`. | B7 newly routes the key/MAC through this inherited Trace path. | **PARKED** (P3, grants nothing; build-config change) — fix: `<DefineTrace>false</DefineTrace>` in the 4 `.vbproj` (one line each). | auditor-E | High (verified `FinalDefineConstants` shows `TRACE=-1`) |
| 11 | **P3** | **ARCHITECTURE §1 historical line read as present-tense** — "four cooperating VB.NET (.NET 2.0, x86) programs … five Visual Studio 2010 solutions … no C++" contradicts the live .NET 8 state in the very next paragraph. | `ARCHITECTURE.md:18-20` (pre-fix). | Pre-existing inherited grounding doc. | **FIXED** `36f0572` (marked "As originally inherited (pre-migration — superseded by the Current block below)"). | orchestrator + auditor-E | High |

**Not a finding (checked + cleared):** README `47/47` "smoke checks" is the **last-verified-live** count and is accurate; the script was extended to 61 checks but those are *authored-not-run*, so claiming 61 would overstate — left as-is (public-facing, accurate). HANDOFF §5's internal `240→257→265→273` progression is preserved as history (the header, now `273`, is the single current-count source). The weak 3DES/`mm_textbox` is **B7-owned & documented** — not re-flagged. `master` untouched; the session diff is purely additive (0 deletions).

---

## Fixes made (1 commit on `overnight-audit-2026-06-14`)

| Commit | What | Why | Tests before→after | How verified |
|---|---|---|---|---|
| `36f0572` | Doc-drift reconcile: `HANDOFF.md:7` `265/265`→`273/273` + tip `13ec2fc`→`322b63c` (push left unverified — `origin/monkmode` doesn't resolve locally, and the fence forbids pushing/network); `HANDOFF.md:238` + `ARCHITECTURE.md:144` B6 "UNCOMMITTED"→"committed `097eaaa`"; `ARCHITECTURE.md:18-20` inherited line marked historical. | Findings #1 + #11 — the header + B6 status actively mislead the next session about test count, tip, and commit state. | 273/273 → **273/273** (docs only; suite unaffected) | `dotnet test` re-run after the commit = `Passed: 273, Total: 273, 0 skipped`; build 0 errors; 4 surgical `Edit`s, no source touched. |

No code was changed and nothing was reverted. The repo's `.vb`/`.cs`/`.vbproj` are byte-identical to the pre-audit state.

---

## Reverted / NOT fixed — the morning decision list (each with a recommendation)

Everything below is a **code** change to the LocalSystem enforcement core or the
CLI, which **cannot be live-verified tonight** (fence: never run the
service/CLI/blocker). Per the prime directive + the repo's own fence ("read-only
analysis unless Samrath explicitly asks for a live test"), I parked them rather
than commit un-verifiable changes to a tamper-resistant service. **None is an
active bypass** (0 P0 / 0 P1), so parking is low-cost. Recommended order when you
do a supervised FIX+smoke session:

1. **[P2] Timer re-entrancy (#2).** Recommend `Monitor.TryEnter(lockObj)` around
   the `timer_Elapsed` body (skip the tick if one is still running) — safer than
   `AutoReset=False` (no risk of stopping the timer on a missed re-`Start`). No
   early-lift today, so this is robustness/correctness, not a security gate.
2. **[P3→do-with-#2] Heartbeat Restamp TOCTOU (#4).** Re-validate
   `ConfigMacIsValidForIni` on the **reloaded** ini before re-stamping (skip =
   fail-closed). `Monitor.TryEnter` from #2 also narrows it. Verifier confirmed it
   grants nothing beyond the documented B7 ceiling, but it's the one surviving
   instance of the bug class you killed this session — worth closing for hygiene.
3. **[P2] Atomic ini write (#3).** Temp-file + `File.Replace` in `IniFile.Save`,
   and a single shared named mutex around load-modify-save in the service + the
   notifier. Higher-leverage: it also removes the torn-read window (#4's cousin).
   Touches 3 writers — do it supervised, with the live smoke test.
4. **[P3] `stopMe()` atomic hosts strip (#5)**, **`add_to_hosts` debounce (#6)**,
   **strip-parity (#7)**, **`RemoveDenyDeleteAce` strip-all (#8)**, **`Step_`
   skip-4-on-failed-3 (#9)**, **`<DefineTrace>false</DefineTrace>` ×4 (#10)** —
   all small, all low-risk, none urgent. The TRACE one is a 1-line-per-file build
   flag and a genuine hygiene win.

**Still the headline gate (unchanged):** run the elevated **`run-smoketest.ps1`
(61-check)** + **`b7-failclosed-test.ps1`** + the **B4 clock drill** (rebuild
`dist\` first via `tools\build-dist.ps1`). Until then, B4/B6/B7 severities stay
where they are.

---

## Verification record

- **Codex (PRIMARY, read-only, £0):** ran `codex review --base _codex-base`
  (`_codex-base` = a throwaway pointer at `06490f9`, the pre-wave base; **deleted
  after the run**). It did **not converge to a captured verdict** within the
  window — it streamed 11k+ lines echoing the raw diff + reasoning with no summary
  block by report time. **PARTIAL VERIFICATION DEBT** — re-run:
  `codex review --base 06490f9` (or per code commit:
  `codex review --commit 1794bde|a32a0cd|097eaaa|2da5c5b|13ec2fc|702091a`). This
  does **not** weaken the verdict — six independent Opus-side passes + the
  orchestrator's own full read of every core file all cross-corroborate 0 P0/0 P1.
- **Opus `verifier` (SECONDARY, refuting):** DOWNGRADED the TOCTOU (#4) to P3
  ("no non-code attacker; the LocalMachine-DPAPI direct forge is strictly easier");
  **confirmed NO early-lift** in the timer re-entrancy (#2) after a genuine attempt
  to construct one; surfaced the freeze-at-expiry P2 (#3).
- **5 `auditor` subagents (READ phase, one concern each):** B7 fail-closed (A) ·
  B4 clock/monotonic (B) · B6 sc-delete/brick-safety (C) · cross-slice/concurrency/
  hosts-data-loss (D) · security/doc-drift/test-honesty (E). Each returned a
  file:line findings table; **all four security concerns = 0 P0 / 0 P1.**
- **Adversarial skeptic pass:** the refuting verifier pressure-tested the only two
  high-stakes findings (TOCTOU, re-entrancy). TOCTOU survived **only** as P3;
  re-entrancy's "no early-lift" survived.
- **Runtime smoke:** **NOT run** (fence). The live B7/B4/B6 gates are Samrath-only.
- **Build/suite:** Release build 0 errors; `dotnet test` **273/273, 0 skipped**,
  before and after the doc commit.

---

## What's healthy (so it's protected — don't regress these)

- **B7 is genuinely fail-closed at all 4 re-stamp sites** (heartbeat
  `ClassifyHeartbeat`, OnStart `ShouldRestampOnStart`, notifier
  `ConfigMacIsValidForIni`-gated, `add` `AppendAddToHosts` capture-before-mutate).
  `EffectiveBlockHasExpired = macValid AndAlso BlockHasExpired`; `FixedTimeEquals`
  constant-time compare; DPAPI scope LocalMachine↔LocalMachine consistent; the 4
  `ConfigIntegrity.vb` copies are **byte-identical** (MD5 `8d5234051cae44367bc1720517b38971`);
  the canonical is benign even though label-delimited (HMAC **input** only, never
  re-parsed on verify; `IniFile` is line-based so `CustomSites` can't carry `\n`).
- **B4 has no early-lift under any clock attack** (forward-jump > ceiling, +119s/tick
  creep, backward-then-forward, DST, stop/jump/restart, suspend+BIOS-clock+resume) —
  `NextHighWater` refuses any >ceiling advance *before* `CapHighWaterAdvance`, the
  cap bounds within-ceiling creep to real `TickCount64` elapsed, OnStart never
  credits the boot gap, HighWater is MAC-covered, the service is the sole writer.
- **B6 is brick-safe by construction** — `(D;;SD;;;BA)` denies DELETE(SD) only,
  never WD/WO; front-inserted (canonical deny-before-allow); SY + BA both retain
  WRITE_DAC, so the DACL is always rewritable. `unblock --force` ordering is
  correct (RestoreSD **before** DeleteService; recovery off + watchdogs killed
  first); `--force` gating is airtight (bare `unblock` = usage + zero teardown).
- **Cross-slice:** `BlockIsActive` is fail-closed (the clock-forward CLI-overwrite
  bypass stays closed); apps-only `DoBlock` deletes the stale snapshot.
- **Inherited availability bug closed:** `Simple3Des.DecryptData` now `Return ""`
  on bad Base64 instead of `End` (a junk `[Time]Until` no longer force-terminates
  the LocalSystem service).
- **Tests are honest, not tautological** — keystones pin the real bug cases:
  `ClassifyHeartbeat(False, True)=Hold`, the creep regression *with* an anti-tautology
  assertion that the uncapped attack would have crossed, 4-copy `CanonicalFromIni`
  parity via the real wrappers, the ServiceSecurity brick-guard + `Remove(Add(x))==x`.

---

## Decisions I made for you (SAMRATH.md §3 calls — silence = consent)

- **Window = `06490f9..322b63c`, stated.** No commit is literally dated 14/06;
  scoped to the wave the 14/06-early HANDOFF calls "last session".
- **Fixed docs only; parked all code.** The enforcement core can't be
  live-verified tonight and all code findings are P2/P3 (no active bypass), so
  committing un-verifiable changes to a tamper-resistant LocalSystem service
  overnight is the wrong risk. Repo *code* ends identical to start.
- **Did not touch `_shared-context`** to revert the auditor's errant AUDIT_LOG
  write (explicit fence + live sibling sessions = fresh-clash risk). Morning roll-up.
- **Did not edit the public-facing README** (its 47/47 is the last-verified truth).
- **Did not assert "pushed"** in the header fix — `origin/monkmode` doesn't
  resolve locally and the fence forbids network/push, so I wrote "committed through
  `322b63c` (push not verified)".

## Lessons for `_shared-context` (note only — morning job; do NOT write that file tonight)

- **"Read-only" auditor subagents that have the Bash tool can still WRITE via shell
  redirection (`>>`).** The "never Edit/Write" instruction doesn't bind Bash. One
  auditor appended to `AUDIT_LOG.md` despite the fence. Future auditor/overnight
  prompts must explicitly say: *"no Bash writes/redirects/`tee` to any file,
  especially outside this repo; return findings as text only."* Candidate lesson
  slug: `read-only-must-forbid-bash-redirects`.
- **Concurrent overnight sessions appending to one shared `AUDIT_LOG.md` corrupt
  it** (rows glued together tonight). The morning roll-up should rebuild AUDIT_LOG
  from each session's returned report text, not from the concurrently-appended file.
  Reinforces the existing `facts-rot-wherever-they-are-restated` lesson.
- **`compile-success-is-not-verification` held:** the suite is green and the code
  reads fail-closed, but B4/B6/B7 are *still* not live-verified — the elevated
  smoke test is the only thing that closes it. Don't let "273/273 green" read as "done".
