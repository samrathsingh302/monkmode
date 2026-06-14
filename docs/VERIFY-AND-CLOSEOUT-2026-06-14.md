# MonkMode — verify & close out the overnight-audit branch (14/06/2026)

> **Hand this to a fresh MonkMode session.** Verify-then-act brief. Samrath drives the merge/push and every §4 call; you do the verification and the safe fixes. **Trust the LIVE repo, not the numbers in this brief — re-derive every count, hash and branch state yourself.** This brief was written from the vault side at ~17:30 on 14/06/2026 and may already be stale.
>
> ⚠️ **HARD FENCE — read before anything else.** Never run the service, the CLI (`monkmode.exe`/`monkmode.dll`), or any smoke/test script casually: they edit the **LIVE Windows hosts file**, add an HKCU `Run` entry, and install a `CanStop=False` LocalSystem service. The only sanctioned runtime verification is the **elevated, supervised smoke test** in §3 — Administrator shell, deliberate, run **by/with Samrath**, never unattended. Until that point this work is **git inspection + read-only static analysis only.**

## 0. Why this exists (and why the docs are partly stale)
The overnight autonomous audit (`docs/handoffs/2026-06-14-overnight-audit.md`, ~05:20) swept the B7+B4+B6+cross-slice wave (`06490f9..322b63c`), found **0 P0 / 0 P1 / 3 P2 / 8 P3**, fixed only doc-drift, and parked 9 code findings with exact patches. Its central verdict: the *code is byte-identical to the branch start* and the **one** thing between "code-complete" and "verified" is the elevated live smoke test.

**Since the report, the branch moved twice and the picture changed — re-orient against the LIVE repo, not the report:**

1. **A post-report commit `d90eb92` (14/06 17:15)** claims the **elevated smoke test ALREADY RAN — `run-smoketest.ps1` 63/63 + `b7-failclosed-test.ps1` 10/0** — and on that basis **dropped the ARCHITECTURE severities (B4 → Low, B6 → Medium, B7 → Medium)** and rewrote the HANDOFF header. The overnight report (written ~05:20) says the smoke is still outstanding; this 17:15 commit says it passed. **These two cannot both be the current truth.** This commit is docs-only and was **never seen by the overnight two-pass**. Treat its smoke-pass claim as the single highest-stakes thing to confirm (§2, §3).
2. **There are UNCOMMITTED working-tree CODE changes** (Samrath's hand, ~17:23–17:27 on 14/06) that **action four of the parked findings in code**: #2 timer re-entrancy (`Monitor.TryEnter` guard in `Service1.vb`), #3 atomic ini write (temp-file + `File.Move` overwrite in all **four** `IniFileVb.vb` copies), #4 heartbeat-restamp TOCTOU (re-validate MAC on the reloaded ini in `Service1.vb`), #10 TRACE leak (`<DefineTrace>false</DefineTrace>` in all **four** `.vbproj`), **plus a new untracked test** `MonkMode.Tests/IniFileSaveTests.cs`. **None of this is committed and none has been built, tested, Codex-reviewed, or verifier-reviewed.** So the claim "the code ends byte-identical to the branch start" is true of the **commits** but **false of the working tree** — there is live, unverified code in flight.

Your job: re-derive the true git state, decide whether `d90eb92`'s smoke-pass claim is real (or back it out), independently verify the uncommitted parked-finding code (gates → two-pass → the elevated smoke is the load-bearing close), and leave the branch ready for Samrath's merge/push call. **Nothing pushed. `_shared-context` untouched.**

## 1. Orient (read in this order)
1. `CLAUDE.md` (fences — hosts-file/live-service danger) → `HANDOFF.md` → `ARCHITECTURE.md` (bypass surface B1–B11; check whether the B4/B6/B7 severities really got dropped) → `docs/handoffs/2026-06-14-overnight-audit.md` (the authoritative overnight report) → the newest 1–2 other dated handoffs → `C:\Users\samra\Atlas\repos\_shared-context\` SAMRATH.md + ORCHESTRATION.md (+ the Codex/Opus two-pass doctrine).
2. **Re-derive the live git state** (do **not** trust the snapshot below — confirm it):
   - `git -C "C:/Users/samra/Atlas/repos/Cold-Turkey-Serious" log --oneline --decorate --graph -15`
   - `git -C "C:/Users/samra/Atlas/repos/Cold-Turkey-Serious" status`
   - base is **`monkmode`** (the default branch), **not** `main` — `git rev-list --left-right --count monkmode...overnight-audit-2026-06-14`
   - `git diff --stat monkmode..overnight-audit-2026-06-14` (committed surface) **and** `git diff --stat` (the **uncommitted** surface — do not miss it) **and** `git status --porcelain` (the untracked test).

**Snapshot as of 14/06 ~17:30 (VERIFY it still holds — every number re-derived):**
- On `overnight-audit-2026-06-14`, **3 commits ahead of `monkmode`** (= `322b63c5c0f759b28835b1476936b17eeb64a8ea`), 0 behind. **There is no `main` branch** — the base for every diff/Codex run is `monkmode` / `322b63c`. (`master` = the untouched original Cold Turkey; never touch it.)
- Tip `d90eb925e2d1fb64cd9fceb730de244ec810d49c`. The 3 committed commits, newest first: `d90eb92` (17:15, smoke-pass + severity-drop claim), `b7608b2` (05:20, audit report + HANDOFF pointer), `36f0572` (05:15, doc-drift reconcile). **All three are docs-only** — `git diff --name-only 322b63c..d90eb92` is `ARCHITECTURE.md`, `HANDOFF.md`, `docs/handoffs/2026-06-14-overnight-audit.md` and nothing else.
- ⚠ **Uncommitted working-tree code (NOT in any commit):** `MonkMode_srv/.../Service1.vb` (#2 + #4), `IniFileVb.vb` ×4 (#3), `*.vbproj` ×4 (#10), **+ untracked** `MonkMode.Tests/IniFileSaveTests.cs` (#3 tests). ~232 insertions / ~79 deletions across 9 tracked files. **Unbuilt, untested, unreviewed.** This is the real review surface now, alongside `d90eb92`.

## 2. The branch (labels are claims — re-verify each)
Base = `322b63c` (`monkmode`). The full review surface is **two parts**: the committed docs `git diff 322b63c..d90eb92` **and** the uncommitted code `git diff` (working tree) + the untracked test.

**Committed (docs only) — confirm docs-only, then weigh the claims:**
- `36f0572` doc-drift reconcile (suite → 273/273; tip → `322b63c`; B6 → "committed `097eaaa`"; ARCH §1 marked historical). Covered by the overnight two-pass.
- `b7608b2` the overnight audit report + HANDOFF pointer. The report itself.
- `d90eb92` **"live-verified 63/63 + b7 10/0 — drop severities (B4 Low, B6/B7 Medium)"** (17:15). **NOT covered by the overnight two-pass; postdates the report by ~12 h.** Its ONLY content is doc edits asserting the elevated smoke passed. **Verify this claim is real before you trust the lowered severities** — find the smoke-test log it implies (the prior runs logged to `C:\Users\samra\monkmode-smoketest\smoketest.log`; check its timestamp/contents and whether it shows a 63/63 + a separate `b7-failclosed-test` 10/0 from **today**, against a `dist\` rebuilt **after** the wave). If you cannot find evidence the run happened, treat the severity drop as **unproven** and surface it to Samrath (do not silently keep or revert it).

**Uncommitted (CODE) — verify this HARDEST; nothing has looked at it:**
- `Service1.vb` — **#2** `Private ReadOnly tickLock`, `Monitor.TryEnter` skip-guard wrapping the whole `timer_Elapsed` body, `Monitor.Exit` in a `Finally`; **#4** the `Restamp` branch now re-runs `ConfigMacIsValidForIni(iniFile)` on the **reloaded** ini and only re-stamps/saves if it still verifies (else falls through to Hold — fail-closed).
- `IniFileVb.vb` ×4 — **#3** `Save` writes a unique `*.<guid>.tmp` in the same dir then `File.Move(tmp, sFileName, True)` (atomic replace on NTFS, same volume), `Catch` deletes the temp + rethrows, target left intact on failure. **These four copies must stay byte-identical** (parity, like `Simple3Des`/`ConfigIntegrity`) — diff them against each other.
- `*.vbproj` ×4 — **#10** `<DefineTrace>false</DefineTrace>` so the inherited `Trace.WriteLine` calls (which echo the DPAPI `[Integrity] Key`/`Mac` + 3DES ciphertext to `OutputDebugString`) compile out in all configs.
- `MonkMode.Tests/IniFileSaveTests.cs` (untracked) — pins #3: Save→Load round-trip, wholesale replace (no torn merge), no leftover `.tmp`, complete parseable bytes, all four per-project copies identical.

## 3. Verify — gates → two-pass → the ELEVATED SMOKE is the load-bearing close
Because the **committed** code is byte-identical to base, the weight here is **not** a code re-review of the commits — it is (a) confirming/backing-out `d90eb92`'s smoke claim and (b) verifying the **uncommitted** parked-finding code, with the **elevated live smoke test as the only thing that truly closes B4/B6/B7**.

1. **Gates — re-derive the numbers (do this with the uncommitted code in place, so it's actually exercised):** `C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release` (SDK is user-scoped, NOT on PATH) then `C:\Users\samra\.dotnet\dotnet.exe test MonkMode.sln`. The report's pre-audit count is **273/273**; the new `IniFileSaveTests.cs` should add tests — report the ACTUAL new total, and confirm 0 errors with `<DefineTrace>false</DefineTrace>` applied. No separate linter in this VB.NET repo (compiler-clean is the bar; `Option Explicit On`, `Option Strict Off` by inherited design).
2. **PRIMARY = Codex** (the outstanding debt — overnight it streamed 11k+ lines without converging to a verdict): run **read-only**, base = the pre-wave base:
   ```
   codex review --base 06490f9
   ```
   (`06490f9` is real — 13/06 03:59, the pre-wave base. Per-commit alternative: `codex review --commit 1794bde|a32a0cd|097eaaa|2da5c5b|13ec2fc|702091a`.) **Never** let Codex write/apply/commit; never feed it vault/transcript content. **Note:** the SECONDARY Opus `verifier` already came back clean overnight (downgraded the TOCTOU #4 to P3; confirmed NO early-lift in the re-entrancy #2; surfaced the freeze-at-expiry P2 #3) — so Codex is the *partial debt* to clear, not a from-scratch pass. If it's still rate-limited, log the exact re-run command and continue.
3. **SECONDARY = fresh-context Opus `verifier` subagent** — but now aimed at the **uncommitted code**, not the (unchanged) committed wave: does the #2 `TryEnter` guard correctly release on every path (including early `Return`s and exceptions inside the `Try`)? Does the #4 reloaded-MAC re-validation actually close the TOCTOU without breaking a legitimate Trusted-tick HighWater advance? Are the four `IniFileVb.Save` copies truly identical and is `File.Move(...,True)` safe on the real ini path (same volume)? Hunt edge cases, not a diff restatement.
4. **ELEVATED RUNTIME SMOKE — the load-bearing close (a green suite is NOT proof it works).** This is what actually closes the repo, and it is **Samrath/operator-gated, run in an elevated (Administrator) PowerShell, deliberately and supervised — never casual, never unattended.** Rebuild `dist\` FIRST (`powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1`) so the binaries include the uncommitted fixes — a stale `dist\` has already caused false-fails (see HANDOFF §8). The three sanctioned runs (all touch the **live hosts file / system clock**):
   - **`run-smoketest.ps1`** — the 61-check lifecycle (B1/B2/B3 + B6 sc-delete refusal/self-heal). Lives in `C:\Users\samra\monkmode-smoketest\`.
   - **`b7-failclosed-test.ps1`** — corrupts the MAC, asserts the service does NOT re-stamp and keeps enforcing, exits via `unblock --force` (the report cites a target of 10/0).
   - **the B4 clock drill** — `run-smoketest.ps1 -IncludeClockTest` (moves the system clock past `Until`, asserts no early lift, restores the clock via the **monotonic Stopwatch** path; +2 checks → 63). After any clock drill, sanity-check the clock (`w32tm /stripchart` / `w32tm /resync /force`) — see HANDOFF §8 for the restore-bug lesson.
   If any run hangs, `cleanup.ps1` (B6-safe) is the rescue. **Only run these with Samrath present and an elevated shell.** This run is what lets the lowered B4/B6/B7 severities stand; until it is demonstrably done **today against the rebuilt `dist\`**, `d90eb92`'s severity drop is unconfirmed.

## 4. Action the parked decisions (from the report's "Reverted / NOT fixed")
The report parked 9 code findings. Four are now **in the working tree, uncommitted** (#2, #3, #4, #10) — your job is to *verify and either commit-or-surface* them, not re-author. The rest are still untouched. Each fix needs a regression test and a green suite after it; surface design calls to Samrath rather than guessing.

- **#2 timer re-entrancy (P2)** — `Monitor.TryEnter` skip-guard: **in working tree.** Verify release-on-all-paths, then it's safe to commit. Robustness, not a security gate (verifier confirmed no early-lift today).
- **#3 atomic ini write (P2)** — temp + `File.Move` across 4 copies + new tests: **in working tree.** Verify parity + same-volume safety; it removes the torn-read window. Commit if clean.
- **#4 heartbeat-restamp TOCTOU (P3)** — reloaded-MAC re-validation: **in working tree.** Verifier said it grants nothing beyond the documented B7 ceiling, but it kills the last instance of the bug class — verify and commit for hygiene.
- **#10 TRACE leak (P3)** — `<DefineTrace>false</DefineTrace>` ×4: **in working tree.** Confirm the build still passes with TRACE off; commit.
- **Still untouched — surface or do-with-care:** **#5** `stopMe()` non-atomic hosts strip (P3, **data-loss class — only with the live smoke**), **#6** `add_to_hosts` double-fire de-dup (P3, cosmetic), **#7** strip-parity whitespace divergence (P3), **#8** `RemoveDenyDeleteAce` strip-all (P3), **#9** `Step_` skip-4-on-failed-3 (P3). All small/low-risk; none urgent. Implement with a regression test if confident, else accept-or-defer with the report's recommendation.
- **The `d90eb92` severity-drop (§2):** this is the §4-class decision — keep the lowered B4/B6/B7 severities ONLY if the elevated smoke is confirmed run today against the rebuilt `dist\`; otherwise surface to Samrath. **Samrath-gated**, because it rests on the **Samrath-gated elevated smoke**.

**Still the headline gate (unchanged):** the elevated smoke (§3) is the only thing that closes B4/B6/B7. It is Samrath-gated.

## 5. Already handled — DO NOT redo
- The overnight `_shared-context/AUDIT_LOG.md` errant-write incident the report flags (a read-only auditor used Bash `>>`): **leave it — it is the morning vault roll-up's job, already reconciled vault-side.** **Do NOT touch `C:\Users\samra\Atlas\repos\_shared-context` at all** — it is OFF-LIMITS for this repo's session (its own state, guard-hardening commits, and Samrath's push gate live there).
- The doc-drift cluster (#1) and the ARCHITECTURE §1 historical line (#11) were fixed in `36f0572` — don't redo.
- The weak 3DES / `mm_textbox` crypto is **B7-owned and documented by design** — do **not** re-flag it (CLAUDE.md fence).

## 6. Fences (MonkMode `CLAUDE.md` / `HANDOFF.md` — no later reasoning overrides these)
- **Never run the service / CLI during dev or audit** — it edits the LIVE hosts file, adds an HKCU `Run` entry, installs a `CanStop=False` LocalSystem service. **Read-only analysis unless Samrath explicitly asks for a live test.** Unit tests must never touch real hosts/registry/SCM. The §3 smoke scripts are the *only* sanctioned runtime path and are **elevated + supervised + Samrath-gated**.
- **No data loss on hosts restore** — only ever touch the MonkMode marker block (`#### MonkMode Entries ####`), never the user's own hosts content.
- **Never force-push or rewrite history on the `monkmode` branch.** Don't disturb git state otherwise — no reset/checkout/rebase unless asked. **Commit per coherent step; push/merge on Samrath's go only — do NOT push or merge yourself.**
- **`master` is the untouched original Cold Turkey — never work on it.**
- Crypto is documented-weak by design (Phase-3-owned, B7) — don't re-flag. Free/local, £0, no telemetry.

## 7. Definition of done
☐ live git state re-derived (base = `monkmode`/`322b63c`, **no `main`**; 3 docs-only commits; **the uncommitted code + untracked test surfaced**) ·
☐ `d90eb92`'s "smoke passed 63/63 + 10/0 / severities dropped" claim **either evidenced (a fresh log against a rebuilt `dist\`) or surfaced to Samrath as unproven** — not silently trusted ·
☐ uncommitted parked-finding code (#2/#3/#4/#10 + `IniFileSaveTests.cs`) verified: build 0 errors with TRACE off, suite green (new count re-derived), Codex `--base 06490f9` run/logged, fresh `verifier` pass over the working-tree code ·
☐ **elevated live smoke run by/with Samrath against the rebuilt `dist\` — `run-smoketest.ps1` (61), `b7-failclosed-test.ps1` (10/0), B4 clock drill (→63) — and PASSING** (this gate cannot be skipped; without it B4/B6/B7 severities are unconfirmed) ·
☐ the four working-tree fixes committed-or-surfaced (each with its regression test, suite green after each); the remaining parked P3s (#5–#9) fixed-or-deferred with a recommendation ·
☐ `HANDOFF.md` + a new dated handoff updated to the TRUE state (incl. whether the smoke actually ran) ·
☐ branch left GREEN and ready for Samrath's merge/push — **nothing pushed, `_shared-context` untouched.**

> **It is NOT "done" until the elevated smoke passes.** A green `dotnet test` and a clean Codex pass prove the code is sound in the abstract; only the supervised elevated smoke proves the tamper-resistant service actually enforces — and only that lets the lowered B4/B6/B7 severities stand.
