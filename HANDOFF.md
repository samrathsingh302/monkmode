# MonkMode — Handoff / Continuation Doc

This file lets a new chat (or a new session) pick up exactly where we left off.
Read this first, then `ARCHITECTURE.md` (component map + bypass surface) and
`README` (usage/build).

Last updated: 2026-06-14 (**B4 + B6 + B7 NOW LIVE-VERIFIED — elevated smoke test 63/63 + `b7-failclosed-test` 10/0; ARCHITECTURE severities dropped: B4 → Low, B6 → Medium, B7 → Medium.** These severity-flip doc edits are on branch `overnight-audit-2026-06-14`, uncommitted. A first `-IncludeClockTest` run was 53/10 purely from a `run-smoketest.ps1` clock-restore bug that left the system clock +10 min fast — FIXED with a monotonic Stopwatch; the service behaved correctly, refusing to lift on a dishonest clock. The wave below was code-complete + unit-tested (suite 273/273), committed through `322b63c`.) _Older header kept for history:_ (**B6, B7, B4 all code-complete + unit-tested (suite 273/273), committed through `322b63c` (push not verified this read-only session); two fail-open bug CLASSES found this session and FIXED + verifier-SHIP'd, but NOTHING LIVE-VERIFIED yet.** Session arc: finished B6 (`unblock --force` + build fix + tests); found & fixed the **B7 MAC re-stamp fail-open** at all 4 re-stamp sites (heartbeat/OnStart/notifier/`add`) — a tampered `[Time] Until` no longer auto-lifts; adversarial-audited B4+B6, which surfaced & closed the **B4 within-ceiling clock-creep** (monotonic-elapsed bound). All fixes have an independent verifier SHIP + a pinned regression test. The gate to drop any B4/B6/B7 severity is still the elevated smoke test (`run-smoketest.ps1` 61-check + `b7-failclosed-test.ps1`), which could NOT be run — non-elevated agent + Samrath away. ~~Older state below kept for history.~~ Prior partial header: **B7/B4/B6 code-complete + unit-tested (240)** — the elevated smoke test still needs running, and Samrath was away from the laptop so it could NOT be run this session, see the entry below. `dist\` was REBUILT this session (B4+B7+B6 across all four components — verified via the IL-bearing `.dll`s) and the smoke test was EXTENDED 52 → **61 checks** for B4/B6/B7 (authored + parses clean under PS 5.1, NOT run — Samrath must run it elevated when back). All three slices are committed + pushed (`097eaaa` B6, `20a6b75` docs, on top of `1794bde` B7 / `a32a0cd` B4). Prior verified state: **B3 registration LIVE-VERIFIED — smoke test 52/52** (ARCHITECTURE B3 Critical → Low, in-Safe-Mode run not reboot-tested); B1 LIVE-VERIFIED 47/47 → Medium. Always rebuild `dist\` before smoke-testing — see §8.)

> **✅ DOC-DRIFT RESOLVED 14/06/2026:** the live elevated smoke test ran (63/63 + `b7-failclosed-test` 10/0), so the ARCHITECTURE B4/B6/B7 severities are now lowered (B4 → Low, B6/B7 → Medium) per the 14/06 status note at the top of §4. ~~Earlier: the B4 (`a32a0cd`)/B7 (`1794bde`) commits shipped code + tests but touched NO docs, so the rows still showed their original (pre-mitigation) severities, intentionally not lowered until a live smoke test.~~

> **🌙 Overnight audit (14/06/2026):** read-only correctness sweep of the
> B4/B6/B7 + cross-slice wave (`06490f9..322b63c`) — **0 P0 / 0 P1**, repo green
> (273/273). Only doc-drift fixed (`36f0572`); all code findings (3 P2 / 8 P3,
> none an active bypass) parked with exact patches; the elevated live smoke test
> stays the gate. Full report: `docs/handoffs/2026-06-14-overnight-audit.md`.

---

## 1. What MonkMode is

A **personal, tamper-resistant website/app self-control blocker for Windows**,
forked from the open-source **Cold Turkey** blocker (GPLv3) and rebranded.

**Goal:** once a block is started it cannot be casually removed before its timer
expires. Aspirational target is "only a full PC reset can remove it," but the
honest ceiling (documented in ARCHITECTURE.md §5) is: while the user keeps admin
rights + physical disk access, an offline edit always wins. So the realistic bar
is **defeat casual → determined bypasses** (Cold Turkey Pro / Freedom level), and
close the rest with non-code measures (non-admin daily account, BitLocker, BIOS
lock).

The "think like a 24-year-old trying to disable a porn blocker" framing is
**adversarial threat-modeling to harden the user's own tool** — legitimate.

Local repo: `C:\Users\samra\Atlas\repos\Cold-Turkey-Serious`
Private GitHub: https://github.com/samrathsingh302/monkmode  (default branch `monkmode`)
Working branch: **`monkmode`** (all work is here; `master` = original Cold Turkey).

---

## 2. Current architecture (after the CLI migration)

**MonkMode is now a CLI — there is no GUI.** Four VB.NET programs, all
**.NET 8 (net8.0-windows)**, SDK-style projects:

| Project | Output | Runs as | Role |
|---|---|---|---|
| `MonkMode` | `monkmode.exe` | User, elevated (requireAdministrator) | **CLI.** Verbs `block`/`status`/`add`/`help`. Writes hosts, writes config, installs+starts service (+ SCM recovery policy), launches notifier. |
| `MonkMode_srv` | `MonkMode_srv.exe` | **LocalSystem service `MONKMODE`** | **Enforcement core (inherited design preserved; hardened via tested fail-closed gates).** Locks hosts read-only, restores tampered hosts (B2), kills blocked session-0 processes, keeps the guardian alive (B1), lifts block + stops itself when timer expires. `CanStop=False`. 10s timer. |
| `MM_notify` | `mm_notify.exe` | User session (HKCU `Run`) | Notifier. Kills blocked apps in the *user* session, compensates for clock changes, shows a **tray-balloon toast** when the block ends. |
| `MM_guard` | `mm_guard.exe` | **SYSTEM session** (spawned by the service's timer) | **Watchdog guardian (B1 layer 2).** SCM-restarts the service if it's killed, relaunches `mm_notify` into the interactive session (`WTSQueryUserToken`+`CreateProcessAsUser`), exits only on a genuinely parsed, past end time (unparseable = keep guarding). |

Removed during migration: the WinForms GUI, `MM_notify2` (the old weak
user-session watchdog twin — properly reinstated 13/06/2026 as `MM_guard`), and
`MM_popup` (popup window).

Root solution: `MonkMode.sln` (4 projects + tests). The old per-project `.sln`
files were deleted.

---

## 3. CRITICAL: the config contract (do not break this)

The service (`MonkMode_srv/MonkMode_srv/Service1.vb`) is **unchanged**, so the CLI
and notifier MUST match what it reads/writes:

- **Config file:** `monkmode_settings.ini` in the **same directory as the running
  exe** (so all three exes must be deployed together). Path = `AppContext.BaseDirectory`.
- **Crypto:** `Simple3Des("mm_textbox")` — TripleDES, SHA1-derived key, zero IV,
  Unicode plaintext, Base64. Identical implementation copied into each project
  (`MonkMode/Crypto.vb`, inline in `MM_notify/Form1.vb`, and the service's own copy).
- **Hosts marker:** `#### MonkMode Entries ####` (block sits below it; service
  strips from the marker on expiry).
- **Hosts path:** `%SystemRoot%\System32\drivers\etc\hosts`.
- **ini sections:** `[Process] List` (`"null"` or encrypted `"a.exe;b.exe;"`),
  `[User] CustomChecked/CustomSites/Done/NeedsAlerted`, `[Time] Until/TimeChanging`,
  `[CurrentTime] Now`.
- **Datetimes:** stored as **en-CA** culture strings (`dt.ToString(new CultureInfo("en-CA"))`);
  the service parses with en-CA. Keep this or the service silently rewrites a
  default 7-day block on parse failure.
- **Add-sites channel:** writing file `...\etc\add_to_hosts` makes the service
  append its contents to hosts (FileSystemWatcher). The `add` verb uses this.
- **Notifier registration:** HKCU `...\Run\MonkMode_notify` → `mm_notify.exe`.

---

## 4. Build & run

The **.NET 8 SDK is installed user-scoped** at `C:\Users\samra\.dotnet\` (NOT on
PATH). The machine has no .NET Framework targeting packs, so always use this dotnet:

```
C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release
```

Assemble a runnable folder (all three exes + deps together):
```
powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1     # -> dist\
```
Run (from an ELEVATED prompt — needs admin for hosts + SCM):
```
dist\monkmode.exe block --sites reddit.com,youtube.com --for 2h30m
dist\monkmode.exe block --sites example.com --apps chrome.exe --until "2026-06-11 18:00"
dist\monkmode.exe status
dist\monkmode.exe add --sites x.com
```
`--for` accepts `45` (minutes), `90m`, `2h`, `1d12h`. A block can't be shortened
or replaced until it expires; `add` only adds sites.

Tip for testing CLI logic WITHOUT elevation: run the managed dll directly, which
bypasses the manifest's UAC prompt:
`C:\Users\samra\.dotnet\dotnet.exe dist\monkmode.dll status`

---

## 5. Status — done vs. not done

- ✅ **B7 + B4 + B6 — LIVE-VERIFIED 14/06/2026 (elevated smoke test 63/63 + `b7-failclosed-test` 10/0).** Severities dropped in ARCHITECTURE §4: **B4 → Low, B6 → Medium, B7 → Medium** (see the 14/06 status note there). The run also surfaced + FIXED a `run-smoketest.ps1` bug — the `-IncludeClockTest` clock-restore used `(Get-Date)-$t0` on the already-jumped clock, leaving the system clock +10 min fast (a 53/10 false-fail where ONLY the 10 expiry checks failed; the service was correct, refusing to lift on a dishonest clock); fixed with a monotonic `Stopwatch`. ~~Superseded: code-complete + unit-tested, NOT live-verified (2026-06-13 late night).~~
  Three hardening slices landed back-to-back; the suite is green at **240/240**
  but **none has had a live elevated smoke test**, and `dist\` is stale (03:47,
  predates B4/B7). Severities in ARCHITECTURE are therefore unchanged pending
  that run.
  0. **🔴 B7 fail-open FOUND + FIXED (2026-06-13, verifier-confirmed P0 ×2).**
     While authoring the B7 live test I found B7 did NOT actually close its bypass:
     the service heartbeat re-stamped `[Integrity] Mac` **unconditionally** every
     active tick, so a plain `[Time] Until` edit (the Simple3Des key is known by
     design; only the HMAC was meant to stop it) was detected on tick N
     (`macValid=False`, block held) but **re-blessed with a fresh valid MAC the
     same tick** → lifted on tick N+1 (~20s, no HMAC forge, no clock change). An
     independent verifier confirmed P0, then a second verifier pass on the fix
     found **two more** unguarded autonomous re-stamp sites of the same class:
     the **notifier** clock-change handler (`Form1.vb` — launder via a clock
     nudge) and **OnStart** (`Service1.vb` `ElseIf` Trusted-HighWater-advance —
     launder via a guardian SCM-restart within the 120 s ceiling). **All three
     are now guarded on `macValid`** (never re-stamp over an unverified config):
     - Service heartbeat → pure `ClassifyHeartbeat(macValid, blockExpired)` gate
       → {Lift, Restamp, **Hold**}; Hold = tampered → freeze, never re-stamp,
       never lift. Lift semantics unchanged (`macValid AndAlso blockExpired`).
     - OnStart → pure `ShouldRestampOnStart(macValid, newHw, storedHw)` gate.
     - Notifier → gated behind a new `ConfigMacIsValidForIni(ini)` check
       (`TimeChanging` is not MAC-covered, so legit clock-comp still works).
     Guardian only reads the MAC (clean); CLI `WriteConfig`/`add` re-stamp but
     are deliberate user commands (documented lower-risk residual, not fixed).
     **Tests 250 → 257 → all green at 257:** `HeartbeatRestampTests.cs` pins both
     gates incl. the keystones `ClassifyHeartbeat(False,True)=Hold` and
     `ShouldRestampOnStart(False, advance)=False` (the exact bug cases).
     **Effect: a tampered config now never auto-lifts — the only exit is
     `unblock --force`** (the intended escape hatch). Standalone live test
     authored: `b7-failclosed-test.ps1` (corrupts the MAC — safe, never touches
     the 3DES-encrypted Until which would hit DecryptData→End — and asserts the
     service does NOT re-stamp + keeps enforcing; exits via `unblock --force`).
     NOT yet committed/live-run as of this note → see the commit + gate below.
  0b. **🔎 Adversarial audit of B4 + B6 (2026-06-13) — two more findings.** After
     the B7 P0, two independent verifier audits swept B4 and B6 for the same
     class of integration fail-open. Both found real issues:
     - **B6 → P2 FIXED: the `monkmode add` re-stamp was a 4th B7-class
       fail-open.** `Blocker.AppendAddToHosts` re-stamped the MAC unconditionally;
       `BlockIsActive` (used by the `add` verb) only checks service-running +
       parseable Until, NOT the MAC. So: edit Until → past (block freezes,
       macValid=False), run `monkmode add` → fresh valid MAC minted over the
       tampered canonical → block lifts next tick. **Fixed**: `AppendAddToHosts`
       now captures `macValid` (new `Blocker.ConfigMacIsValidForIni`) BEFORE the
       CustomSites edit and only re-stamps if it was valid (mirrors the other 3
       gates). All four autonomous/user re-stamp sites are now macValid-gated.
       Covered live by a new `b7-failclosed-test.ps1` §4b (`add` must not change
       the corrupted MAC). Suite still 257 (CLI file-I/O path, no unit seam).
     - **B4 → P1 within-ceiling clock-creep — FIXED IN CODE (`CapHighWaterAdvance`).**
       HighWater was capped per *step* (≤120 s) but NOT bound to real elapsed time
       (no monotonic clock), so nudging the clock +119 s before each 10 s tick
       walked HighWater ~12× faster than honest time → early lift. **Fix:** each
       tick's credit is now bounded by REAL monotonic elapsed via a new instance
       anchor `lastMonoMs = Environment.TickCount64` (clock-immune): pure
       `Service1.CapHighWaterAdvance(stored, candidate, monoElapsedSeconds)` credits
       `min(wallDelta, monoElapsed)`. A creep step credits only the ~10 s of real
       time (and freezes the mark once the racing wall gets > ceiling ahead — extra
       fail-closed); honest ticks credit the full ~10 s and lift normally. `OnStart`
       no longer credits the boot gap (no monotonic anchor survives a restart),
       which also closes the OnStart compounding vector. `NextHighWater` signature
       unchanged (guardian parity untouched). **Tests 257 → 265** — new
       `CapHighWaterAdvanceTests` incl. the creep regression the audit said was
       missing (compose NextHighWater + cap; attacker +119 s/tick vs 10 min block;
       assert no early lift + advance ≤ real elapsed). **NOT yet live-verified** —
       the monotonic wiring (`TickCount64`, timer jitter, the `lastMonoMs` thread)
       wants the live B4 clock drill; ARCHITECTURE B4 → **Medium** (creep closed in
       code; pending live test before Low). Verifier also
       confirmed: B6 sc-delete refusal + brick-safety + escape-hatch ordering are
       sound (Q1–Q4 accept); the ~10 s inter-tick re-assert window is an accepted
       residual; B4 single-jump/backward/restart/HighWater-tamper all hold.
  0c. **🔎 Audit sweep of B1 / B2 / B3 / cross-slice (2026-06-14) — one more P1.**
     Three more independent verifier audits (the un-audited shipped mitigations +
     the seams between all of them, esp. the new B7/B4 "frozen block" states):
     - **B1 watchdog → SHIP.** No new fail-open; every permanent-disable path
       (double-kill faster than respawn, recovery-disabled + kill, elevated-user
       mutex pre-grab) is an already-documented residual. SCM reset-INFINITE means
       the 4th+ kill IS still auto-restarted (not exhaustion). No same-tick
       spawn-then-stop race; frozen block keeps both watchdogs alive (fail-closed).
     - **B2 hosts self-heal + B3 SafeBoot → SHIP.** The no-data-loss strip/repair
       holds against every nasty case (case-variant/doubled/mid-file marker,
       LF/CRLF/no-newline, blanked/deleted hosts, user line == a snapshot entry);
       B3 `DeleteSubKeyTree` touches only MonkMode's two leaf keys; intact tick =
       true no-op. Three P3 nits, all DEFERRED (not data-loss, not bypass): the
       `stopMe()` hosts rewrite lacks the timer path's Try/Finally (near-impossible
       in-memory-string torn write; and the audit's suggested "re-assert read-only
       in Finally" is WRONG for the expiry path — it'd leave clean hosts locked);
       `adder_Changed` has no debounce (duplicate appends, cosmetic) and appends
       raw bytes (admin-only channel, pre-existing documented residual).
     - **Cross-slice → P1 FIXED + P2/P3 noted.** **P1 (clock-forward bypass via
       the CLI seam, FIXED):** `Blocker.BlockIsActive()` decided liveness off raw
       `DateTime.Now`, so rolling the clock forward past `Until` made it return
       False and `monkmode block --for 1m` overwrote the standing block with a
       fresh valid-MAC short one — bypassing B4/B7 entirely through the CLI even
       though the service refuses to lift. Fixed: new pure
       `Blocker.BlockGenuinelyExpired(macValid, until, highWater)` mirrors the
       service's `EffectiveBlockHasExpired` (MAC-gated, decided off the persisted
       HighWater a clock-forward can't advance, grace 0); `BlockIsActive` now uses
       it and fails CLOSED (running + invalid-MAC/unreadable => active, never
       overwritable). 8 new tests incl. the clock-forward regression. **P2
       (DEFERRED):** the service heartbeat and notifier are concurrent
       unsynchronised ini writers (truncate-rewrite, no lock) — the audit confirmed
       this is fail-CLOSED (a lost write only drops a HighWater advance / clock-comp
       = block runs longer; a torn read fails closed), a robustness nit not a
       security hole; a named mutex + temp-rename is the fix but touches 3 writers,
       so not changed blind. Cross-slice frozen-block invariants, unblock --force
       ordering, and "no raw DateTime.Now in any service/guardian enforcement
       decision" all verified sound. **Suite 265 → 273 green.** All still
       UNVERIFIED LIVE.
  1. **B7 — tamper-evident config (`1794bde`, committed).** HMAC-SHA256 over the
     *decrypted* ini canonical, key = random 32 bytes DPAPI-protected
     (LocalMachine scope so service/guardian/notifier/CLI can all unprotect).
     `ConfigIntegrity` module byte-identical across all four projects
     (parity-pinned). Fail-CLOSED: `EffectiveBlockHasExpired = macValid AndAlso
     BlockHasExpired` — an absent/invalid MAC, DPAPI failure or foreign-machine
     blob reads as "not expired" → block stands. Re-stamped by every writer of a
     covered field. Ceiling: an admin who runs code can still recover the key +
     forge a MAC (machine-scope DPAPI; full closure needs TPM/PPL, B10-tier).
  2. **B4 — clock-rollback hardening (`a32a0cd`, committed).** New MAC-covered
     `[Time] HighWater` (furthest legitimately-observed time). Every expiry /
     self-heal decision (B1 spawn, B2 repair, B3 SafeBoot, expiry/stopMe, OnStart)
     now passes `asOf = HighWater`, not `DateTime.Now`. Pure gates
     `ClassifyTimeAdvance` / `NextHighWater` (parity-pinned; unparseable =
     fail-closed jump; ceiling 120 s ≫ 10 s tick). Rolling the clock forward past
     `Until` refuses to advance HighWater → never reads "expired". Guardian is
     sole reader, service sole writer (no race). Stacks on B7 fail-closed.
  3. **B6 — service-deletion resistance (committed `097eaaa`; NOT yet live-verified).** Denies
     `sc delete MONKMODE` while a block is active via one deny ACE `(D;;SD;;;BA)`
     on the service-object DACL. **Brick-safe by construction:** denies DELETE
     (SD) ONLY — never WRITE_DAC/WRITE_OWNER — so both SY (service) and BA (admin
     CLI) can always rewrite the DACL to restore. Pure SDDL surgery in
     `ServiceSecurity.vb` (CLI + service copies, parity-pinned). Service:
     `AssertDenyDeleteAce` at OnStart + per-tick (fail-closed `Not
     EffectiveBlockHasExpired`, B4 `asOf`, read-only probe = no churn);
     `RestoreDefaultServiceSd` in `stopMe()` AFTER the guardian kill, BEFORE
     `Me.Stop()/End` (ordering load-bearing). CLI: new **`unblock --force`**
     escape hatch (`Program.DoUnblock`) — the deliberate, documented clean-exit /
     brick-insurance: DisableRecovery → KillWatchdogProcesses → RestoreDefaultSd
     → DeleteService → strip hosts → delete snapshot → remove SafeBoot keys →
     clear autorun (all best-effort, ordered; mirrors `cleanup.ps1`). Gated
     behind explicit `--force` so it is never a casual one-word bypass.
     **This session's fix:** the slice did not build — `Program.vb` dispatched
     `unblock → DoUnblock` but `DoUnblock` was never written (`BC30451`). Authored
     it + usage text; build now green. **New tests:** `ServiceSecurityTests.cs` —
     30 tests pinning the deny-ACE constants (incl. the brick-guard that we deny
     SD and never WD/WO), the four pure functions' truth tables, the round-trip
     brick-safety proof `Remove(Add(x))==x`, and CLI↔service parity. 210 → **240
     green**.
  **Gate before any severity drop — the elevated smoke test (NOT yet run).**
  Prepared this session so it is a one-double-click run when Samrath is back at
  an elevated prompt:
  - `dist\` **rebuilt** with the B4+B7+B6 binaries (the stale 03:47 build that
    caused the earlier 31/16 false-fail is gone).
  - Smoke test **extended 52 → 61 checks** (`run-smoketest.ps1`, parses clean
    PS 5.1, authored-not-run): B7/B4 wiring-present checks (`[Integrity] Mac`+
    `Key`, `[Time] HighWater` in the ini); **B6 section 2e** — `sc delete
    MONKMODE` refused mid-block + service survives + deny-DELETE self-heal after
    the ACE is stripped (≤15s); **B6 at expiry** — ACE removed (service
    removable). Plus an OPTIONAL `-IncludeClockTest` B4 drill (moves the system
    clock past `Until`, asserts no lift, restores the clock; OFF by default,
    +2 checks → 63). **Teardown + `cleanup.ps1` made B6-safe** — they now strip
    the deny-DELETE ACE before `sc delete`, else the new ACE would refuse the
    teardown's own delete and orphan an undeletable service.
  - **NOT covered live (by design, documented in the script header):** B7's
    fail-closed-expiry (corrupting the MAC is one-way and would hang the
    auto-lift run — prove it in a dedicated short run + `unblock --force`); the
    in-Safe-Mode B3 reboot. Both rest on the unit suite + a manual one-off.
  - ⚠️ **Could NOT be run this session** — the agent shell is non-elevated and
    Samrath was away; running an unattended elevated test that installs a
    `CanStop=False` + now sc-delete-resistant (B6) service on the live machine
    with nobody present is a §4-class risk. Run it elevated when back; if it
    hangs, `cleanup.ps1` (now B6-safe) is the rescue.

**Done (committed + pushed on `monkmode`):**
- Phase 0 — `ARCHITECTURE.md`: component map + ranked bypass surface **B1–B11**.
- Phase 1 — rebrand Cold Turkey → MonkMode (all identifiers, service name, marker,
  config, crypto key, dirs/projects). Removed build artifacts; added `.gitignore`.
- Toolchain — migrated .NET Framework 2.0 (VB 2010) → **.NET 8**. Authored
  `MonkMode/ServiceTools.vb` (advapi32 P/Invoke) to replace a third-party
  `ServiceTools` helper that the original referenced but never shipped (the public
  source never built without it). Dropped phantom PowerPacks ref + dead
  installutil installer.
- GUI → **CLI** conversion + tray-toast notifier; dropped MM_notify2/MM_popup;
  added `tools/build-dist.ps1`.

**Verified:** `dotnet build MonkMode.sln -c Release` succeeds (0 errors).

- ✅ **Audit FIX session (2026-06-12)** — implemented the approved AUDIT_LOG §2 fixes:
  1. **P1 culture loss fixed.** Every write of a persisted datetime now formats
     with the explicit en-CA culture (the constructor's thread-culture set never
     applied to SCM/timer/SystemEvents threads): `Service1.vb` (the five
     `EncryptData(DateAdd…/DateTime.Now…)` sites) and `MM_notify/Form1.vb`
     (clock-change reads now `TryParse(…, CA, …)`, write now `ToString(CA)`).
     The CLI (`Blocker.vb`) was already explicit.
  2. **P2 hosts-strip boundary bug fixed** (was `Service1.vb` `startpos - 3`,
     CRLF-only assumption): the strip logic is extracted to the testable
     `Service1.StripMonkModeBlock`, which now removes the marker block plus only
     the single line terminator before it. Failing tests proved the old code ate
     one user character under LF endings (and two with no newline before the
     marker). Behaviour on the smoke-tested CRLF path is unchanged.
  3. **P0 zero tests closed: `MonkMode.Tests/`** (xunit, **C#** — deliberate: VB's
     case-insensitive namespaces merge `MonkMode` (CLI) with `monkmode` (service)
     and make the duplicated `Simple3Des`/`IniFile` types ambiguous). 50 tests,
     all green: marker-block strip edge cases (both implementations), en-CA
     round-trip under de-DE/fr-FR/en-US/en-GB locales (incl. a real
     `WriteConfig`→`ActiveBlockEnd` round-trip in the test bin dir), Simple3Des
     round-trips + three-copy ciphertext equivalence. Pure unit tests — they
     never touch the real hosts file, registry or service. **Never feed invalid
     Base64 to the service's `DecryptData` in a test — it calls `End`** (P3).
     Production visibility changes only: `StripOurBlock` Private→Friend and
     `InternalsVisibleTo("MonkMode.Tests")` in MonkMode + MonkMode_srv.

- ✅ **Fail-closed fix session (2026-06-12)** — fixed the three verifier-confirmed
  findings from the `005ea7a` review:
  1. **Fail-open expiry closed.** Both expiry deciders ignored `DateTime.TryParse`'s
     return value, so an unparseable `[Time] Until` (legacy machine-locale ini,
     corrupted-but-decryptable value) became `MinValue` → "expired" → the service
     lifted the block and the notifier rewrote Until to ~now. Now fail CLOSED:
     the service's `OnStart`/timer gates go through `Service1.BlockHasExpired`
     (unparseable = NOT expired, block stands) and the notifier's clock-comp goes
     through `Form1.ComputeCompensatedUntil` (unparseable = `Nothing`, stored
     Until left untouched; `TimeChanging` still reset to "no"). Consequence: a
     corrupted Until now means the block never auto-expires until the value is
     fixed — that's the intended tamper-resistant direction.
  2. **Marker comparison made ordinal.** `StripMonkModeBlock` used case-insensitive
     `InStr(..., CompareMethod.Text)` while the `stopMe()` gate and the CLI match
     ordinally; a hand-edited case-variant marker line above the real one would
     cut early and delete user hosts lines. Now `IndexOf(..., StringComparison.Ordinal)`.
  3. **Marker tests added**: case-variant-above-real-marker and two-exact-markers
     (first wins) for the service strip, plus a CLI parity test; 14 new tests,
     **64/64 green**. `InternalsVisibleTo("MonkMode.Tests")` added to MM_notify.
  Also added `.mcp.json` (house Obsidian MCP wiring) at the repo root.

- ✅ **CV-readiness session (2026-06-12, late eve)** — the project is now
  presentation-grade and the first Phase 3 item is in:
  1. **B2 self-healing hosts implemented** (the §6 roadmap item): the CLI
     persists the exact marker block it writes to a snapshot
     (`monkmode_hosts.block`, next to the exes — `Blocker.WriteHostsBlock`);
     the service's timer restores tampered/deleted/blanked hosts entries from
     it every 10s while the block is unexpired, via the pure, tested
     `Service1.RepairHostsBlock` (fail-closed gate reused; user hosts content
     preserved byte-for-byte; one-rewrite convergence, no flap). `adder_Changed`
     mirrors added sites into the snapshot (only if it exists); `stopMe()`
     deletes it. Verifier-confirmed SHIP; its P2 (apps-only block must delete a
     stale snapshot or old sites resurrect — `Program.vb` DoBlock) and two P3s
     (Using/Finally on the repair write; best-effort CLI snapshot write) were
     fixed same-session. **17 new tests, 81/81 green.** ~~NOT yet live-smoke-
     tested~~ *(superseded 12/06/2026 night: elevated smoke test passed 27/27 —
     see below)*; known residuals: the snapshot itself is deletable by an admin
     (documented, not hidden) and a legacy ANSI hosts file would be
     UTF-8-mangled on first repair (pre-existing class).
  2. **CI added**: `.github/workflows/ci.yml` — build + full suite on every
     push/PR to `monkmode`, windows-latest, free tier.
  3. **README → README.md** (git mv, history kept), rewritten as a CV-grade
     front page (architecture, tamper-resistance, honest threat model,
     engineering story, CI badge). GPL/Cold Turkey attribution intact.
  4. **`docs/CV.md` added** — CV bullets (3 sizes), elevator pitch, 5 STAR
     stories, interview Q&A, numbers table, honesty rules (it's a fork; weak
     crypto is B7-owned; never claim "unbreakable").

- ✅ **B2 close-out session (2026-06-12, night)** — docs truth + live-test
  readiness for B2 (no code changes; suite untouched at 81/81):
  1. **ARCHITECTURE.md updated**: §4 B2 row → **MITIGATED** with residuals
     (snapshot deletable by admin; ~10s tick window; dead service = no repair,
     so B2's fate is chained to B1); §4 status note + §3 trust-model note (c).
  2. **Smoke test extended for B2** (`C:\Users\samra\monkmode-smoketest\`):
     fixed the stale dist path (repo moved to `Atlas\repos\`), block 2→3 min,
     +12 checks → expect **27/27**: snapshot exists + verbatim-in-hosts; T1
     delete-our-block tamper (restored ≤35s, planted user sentinel preserved,
     read-only re-asserted, resolves 127.0.0.1 again, no rewrite churn 12s
     later); T2 blank-hosts tamper (block restored; sentinel intentionally
     lost — snapshot only owns OUR block); post-lift snapshot deleted. Teardown
     AND cleanup.ps1 now delete the snapshot (else a reinstalled service
     self-heals old sites back in). Scripts parse clean (PS 5.1). Fresh-eyes
     verifier could NOT run (session token limit) — parse-check + author
     self-review only; the elevated run is the real verification.
     ~~NOT yet run~~ *(superseded — run and PASSED, see next entry).*

- ✅ **B2 live verification session (2026-06-12 night → 13/06)** — the elevated
  smoke test **PASSED 27/27** (Samrath ran it elevated; log:
  `C:\Users\samra\monkmode-smoketest\smoketest.log`, finished 23:59:53):
  full lifecycle green — block live (incl. survives `ipconfig /flushdns`),
  **T1 delete-our-block tamper**: marker + entries restored ≤35s, planted user
  sentinel preserved, read-only re-asserted, resolves 127.0.0.1 again, repair
  converges (no churn one tick later); **T2 blank-hosts tamper**: block
  restored, resolving again; auto-lift on time, marker stripped, service
  stopped, snapshot deleted, teardown clean. **B2 is now live-verified, not
  just unit-tested** (ARCHITECTURE §4 note updated). Also fixed this session:
  `tools/build-dist.ps1` SDK selection — a runtime-only dotnet appeared at
  `C:\Program Files\dotnet` and shadowed the user-scoped SDK ("No .NET SDKs
  were found"); the script now skips any dotnet whose `--list-sdks` is empty
  and falls back to `%USERPROFILE%\.dotnet`. Suite still 81/81.

- ✅ **B1 watchdog — layer 1 + design (2026-06-13; live-verified later same day — see the 47/47 entry below)** —
  first Phase 3 B1 increment (force-kill resistance), verifier-confirmed SHIP,
  purely additive. **Layer 1 (SCM auto-restart):** `monkmode block` now calls
  `ServiceTools.SetRecoveryOptions`, which sets the `MONKMODE` service's
  FailureActions via `ChangeServiceConfig2W` — 3× RESTART, 1 s delay, reset
  period INFINITE (count never resets), + restart-on-non-crash flag — so a
  force-kill is auto-restarted by the SCM. Best-effort (a recovery-config
  failure never blocks a block from arming). Policy is a set of `Friend Const`s
  (single source of truth, pinned by tests). **Layer 2 (designed, gate tested,
  unwired):** pure `Service1.ShouldRestartPeer(count, blockActive, exeExists)`
  fail-safe gate for the mutual service ⇄ guardian pair — fail-CLOSED via
  `Not BlockHasExpired`, no duplicate-spawn, no start of a missing exe. **14 new
  tests, 95/95 green.** ~~⚠️ NOT live-smoke-tested — the elevated smoke test (kill
  service → SCM restarts it; `sc qfailure MONKMODE` shows the policy) is the real
  gate before B1 is "mitigated".~~ *(superseded — passed 13/06 night, 47/47.)* ~~The layer-2 guardian's form (SYSTEM child vs
  second service) is an open decision~~ *(decision locked + wired same day — see
  next entry)* — see `docs/handoffs/2026-06-13-0000-b1-watchdog-design.md`.

- ✅ **B1 watchdog — layer 2 wired (2026-06-13; live-verified later same day — see the 47/47 entry below)** — the
  mutual service ⇄ guardian pair is complete in software (decision (A) locked:
  SYSTEM child process, NOT a second service). Verifier-confirmed SHIP; its two
  actionable P3s fixed same-session. Commits `395782c` + `879e24b`.
  1. **New project `MM_guard` → `mm_guard.exe`**: SYSTEM-session guardian the
     service spawns. 10s loop (cadence pinned to the service's consts by tests):
     exits ONLY on a genuinely parsed, past `[Time] Until` (unparseable/missing/
     undecryptable fails CLOSED = keeps guarding); SCM-restarts `MONKMODE` if
     not running; relaunches `mm_notify` into the interactive user session via
     `WTSQueryUserToken`+`CreateProcessAsUser` (winsta0\default, user env block;
     nobody logged on = skip, retry next tick). All decisions go through pure
     tested gates (`MM_guard/Guardian.vb`); single-instance `Global\` mutex;
     per-project `Simple3Des`/`IniFile` copies (guardian's decrypt returns ""
     on bad Base64 — never `End`).
  2. **Service wiring** (`Service1.vb`): timer tick calls the already-tested
     `ShouldRestartPeer` gate → spawns `mm_guard.exe` while the block is active;
     `stopMe()` best-effort kills the guardian at expiry (it would also
     self-exit next tick). Cadence promoted to `Friend Const TimerIntervalMs`/
     `ExpiryGraceSeconds` (used at the timer + all timer-path grace sites) so
     the guardian tests pin guardian == service.
  3. **Tests 95 → 123 green**: gate truth tables, fail-closed ties, service ⇄
     guardian expiry-semantics parity + peer-gate mirror pins, cadence pins,
     4-copy crypto equivalence. Wiring: sln, Tests refs, `build-dist.ps1`.
  **Residuals (documented in ARCHITECTURE B1):** near-simultaneous double-kill,
  `sc failure … reset= 0` + kill, suspend-then-kill, and an *elevated* user
  pre-pinning the guardian (own `mm_guard.exe` grabs the mutex but lacks SCM
  rights — layer 1 still covers). ~~⚠️ The elevated smoke test is the gate before
  B1 is "mitigated"; it must also check expiry causes NO restart loop
  (stopMe ⇄ SCM-recovery/guardian interaction was reasoned about + verifier-
  walked, but never run live).~~ *(superseded — passed 13/06 night, 47/47 incl.
  the no-restart-loop expiry watch.)*

- ✅ **B1 smoke-test extension session (2026-06-13, authored; run same night —
  PASSED 47/47, see next entry)** — extended the elevated smoke test
  (`C:\Users\samra\monkmode-smoketest\`) from
  27 → **47 checks** to cover BOTH B1 watchdog layers; **no repo code touched**
  (suite untouched at 123/123).
  1. **Baseline (+5)**: `sc qfailure MONKMODE` shows the exact policy (3×
     RESTART, 1000 ms delay each, reset INFINITE — pins
     `ServiceTools.SetRecoveryOptions` landed); mm_guard spawned by the
     service's first tick (≤25s) and running in session 0 (SYSTEM).
  2. **Kill drills (+12, section 2c)**: **K1** taskkill service → SCM restarts
     ≤5s (layer 1), still exactly ONE mm_guard (no duplicate spawn); **K2**
     taskkill mm_guard → service respawns it (new PID) ≤15s; **K3** taskkill
     mm_notify → guardian relaunches it ≤15s AND it lands in the interactive
     user session (the CreateProcessAsUser assertion); **K4** `sc failure
     reset= 0 actions= ""` (recovery disabled, verified) + kill service →
     guardian ALONE restarts it ≤15s (layer-2-only path), then policy restored
     + re-verified; final check: block still enforced after all drills.
  3. **Expiry (+3)**: 30s tight-poll (500ms) watch after the lift — service
     STAYS stopped (no stopMe ⇄ SCM-recovery/guardian restart loop — the
     never-run-live interaction), no stray mm_guard, mm_notify self-exits
     after the toast.
  4. **Authoring decisions**: block bumped 3 → 5 min (B2 tamper ~100s + B1
     drills ~75s would have collided with expiry); "≤10s" bounds from the
     design are nominal — checks use 15s ceilings (worst case = one full 10s
     tick + process-start slack) and print actual elapsed; K1/K4 wait for the
     service to be observed DOWN before polling for the restart (a stale
     'Running' read in the instant after taskkill would fake-pass); teardown
     AND cleanup.ps1 now disarm B1 first (`sc failure … reset= 0 actions= ""`,
     then kill mm_guard + service together in a retry loop) — the old order
     would fight the watchdogs mid-teardown. Both scripts parse clean (PS 5.1
     parser); not run (fence: authoring only).

- ✅ **B1 live verification session (2026-06-13, night)** — the extended elevated
  smoke test **PASSED 47/47** (Samrath ran it elevated; log:
  `C:\Users\samra\monkmode-smoketest\smoketest.log`). **B1 is now live-verified;
  ARCHITECTURE §3/§4 updated, B1 severity High → Medium.** Observed timings:
  recovery policy exact (3× RESTART / 1000 ms / reset INFINITE); guardian
  spawned 6.9s, session 0; K1 force-kill service → SCM restart **0.4s**, exactly
  one guardian after; K2 kill guardian → service respawned it **7.4s**; K3 kill
  notifier → guardian relaunched it **10.5s** into the interactive user session;
  K4 recovery disabled + kill → guardian alone restarted the service **11s**,
  policy restored; block enforced through all drills; clean on-time expiry with
  **no restart loop** (30s tight poll), no stray mm_guard, notifier self-exited.
  **A first run failed 31 passed / 16 failed — every B1 check red — with a
  one-cause diagnosis: `dist\` was stale** (built 12/06 23:46, BEFORE layer 1
  `db1bb57` 00:24 and layer 2 `395782c` 00:46; no recovery policy in that CLI
  and no `mm_guard.exe` in dist at all). Cascade: K1's kill left the service
  permanently dead → K2–K4 moot → block never auto-lifted (marker stuck until
  teardown's backup restore — machine verified clean after). Fixed by rebuilding
  via `tools\build-dist.ps1`; second run 47/47. Lesson recorded in §8. No repo
  code changed this session (suite untouched at 123/123); docs only.

- ✅ **B3 Safe Mode resistance — registration LIVE-VERIFIED (2026-06-13 night, smoke test 52/52)** —
  the next Phase 3 slice (closes the "boot into Safe Mode and tamper unopposed"
  bypass). Verifier-confirmed SHIP; purely additive; service-owned (no CLI
  change). **What it does:** the service registers `MONKMODE` under BOTH
  `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE` and
  `…\Network\MONKMODE` (each `(Default)=Service`) so it starts in plain Safe Mode
  AND Safe Mode with Networking. Mirrors the hosts-lock pattern exactly:
  written at `OnStart` (active path only — an expired block hits `stopMe()` and
  `End`s first), re-asserted every 10s tick while active (fail-CLOSED via
  `Not BlockHasExpired`; **read-only probe first so an intact registration is a
  true no-op — no churn**), removed at genuine expiry (`stopMe`). Only MonkMode's
  own two leaf keys are ever touched (no-data-loss fence). All in `Service1.vb`:
  new `Friend Const SafeBootMinimalKey/SafeBootNetworkKey/SafeBootValue`, pure
  `SafeBootValueIsCorrect` gate, private `AssertSafeBootRegistration` (per-key
  Try) + `RemoveSafeBootRegistration`, three call sites.
  1. **Tests 123 → 136 green**: 13 new in `MonkMode.Tests/SafeBootTests.cs` —
     pin the exact key paths + ordinal `"Service"` tag (drift = silent disarm,
     like the B1 recovery-policy pinning), and the predicate truth table incl.
     null/blank/case-variant/whitespace (null-safe, no NRE).
  2. **Verifier (fresh-context): SHIP.** Refuted the data-loss, fail-closed,
     same-tick-race, brick-Safe-Mode and null-safety concerns; 3 P3/Low findings,
     2 fixed same-session (per-key Try on the assert; read-only-probe no-op on an
     intact tick), the 3rd = this very "track the manual Safe Mode verification"
     note.
  3. **Smoke test extended 47 → 52 checks** (`run-smoketest.ps1`, authoring only,
     parses clean): +2 SafeBoot keys present at block start; new **section 2d**
     self-heal drill (delete both keys → service re-asserts ≤15s); +1 both keys
     removed after expiry. Teardown AND `cleanup.ps1` now also delete the keys
     (failure-path rescue). `dist\` rebuilt with the B3 binaries.
  ✅ **Live-verified 13/06/2026 night — smoke test 52/52** (Samrath ran it elevated;
  log `C:\Users\samra\monkmode-smoketest\smoketest.log`): both SafeBoot keys
  present at block start (tag `Service`), section-2d self-heal drill (both keys
  deleted → service re-asserted them in 9.1s), both removed after expiry, clean
  teardown — the registration lifecycle (write/self-heal/remove) is proven live.
  **The second half of the original gate — a manual Safe Mode reboot to confirm
  the service actually *runs* in Safe Mode — was deliberately SKIPPED (Samrath's
  call).** So B3's final hop rests on the standard, documented Windows SafeBoot
  mechanism (a correctly-registered auto-start service is started in Safe Mode),
  not an empirical reboot. **ARCHITECTURE B3 → Low** with that caveat stated.
  Optional future belt-and-braces: do the reboot check (start a block → reboot
  into Safe Mode w/ Networking → `sc query MONKMODE` = RUNNING + example.com →
  127.0.0.1 → `bcdedit /deletevalue {current} safeboot` → reboot → cleanup.ps1)
  to close that last gap.

- ✅ **Live elevated smoke test (2026-06-10) — PASSED 15/15.** Built `dist\`, ran an
  elevated 2-minute block on example.com, verified it was live, waited for the
  auto-lift, verified cleanup, and tore everything down. Reusable scripts live in
  `C:\Users\samra\monkmode-smoketest\` (`run-smoketest.ps1`, `cleanup.ps1`, and the
  `dns-diag*.ps1` root-cause probes). The smoke test found and we FIXED three real
  bugs that the compile-only verification had hidden:
  1. **`0.0.0.0` didn't block.** Windows' resolver ignores `0.0.0.0` hosts entries
     and falls through to real DNS. `Blocker.BuildHostsEntries` now writes
     `127.0.0.1` (Windows honors it; it suppresses both A and AAAA for the name).
  2. **The service's persistent hosts file-handle defeated the block.** Opening
     hosts `FileAccess.Write/FileShare.Read` made the Windows DNS Client fail to
     (re)read hosts during a block, so any `ipconfig /flushdns` silently un-blocked
     everything until reboot (it only "worked" via a dnscache cache race).
     `Service1.vb` no longer holds a persistent handle: it locks via the read-only
     attribute, re-asserts it every 10s in the timer, and appends `add_to_hosts`
     on demand. Block now survives `ipconfig /flushdns` (verified in the test).
  3. **The notifier never ran.** `MM_notify` used `MyType=WindowsForms` with no
     `MainForm`, so the auto Sub Main exited instantly. Added an explicit
     `MM_notify/Program.vb` `Sub Main` + `<MyType>WindowsFormsWithCustomSubMain</MyType>`.
     `mm_notify.exe` now stays alive (toast + user-session app-kill + clock comp).

**NOT done / NOT verified:**
- ~~These source fixes are **not yet committed** (working tree on `monkmode`).~~
  *Superseded 12/06/2026 eve: committed AND pushed (a494da7, plus 005ea7a and
  640db62 on top) — the remote `monkmode` tip is current.*
- The expiry **toast** and **app-kill / clock-change** paths weren't asserted
  programmatically (mm_notify is confirmed running, but watch for the balloon at
  expiry and test `--apps` / clock-roll manually if you want belt-and-suspenders).
- ~~Residual for Phase 3: between 10s re-asserts an admin user can clear read-only
  and edit hosts; the timer re-asserts the attribute but does NOT yet restore
  deleted entries (this is the B2 "re-assert hosts" hardening item).~~
  *Superseded 12/06/2026 late eve: B2 restore is implemented (see above) —
  remaining B2 residual: snapshot deletable by admin. The repair path was
  live-smoke-tested 12/06/2026 night (27/27).*
- Phase 2 (full threat model) — **explicitly deferred by the user; do not start
  without asking.**
- Phase 3 (hardening) — not started.

---

## 6. Next steps / roadmap

1. ~~**Runtime smoke test** of the CLI end-to-end (see above). Highest priority —
   the migration is only compile-verified.~~ ✅ Done 10/06/2026 — the live
   elevated smoke test passed 15/15 (see §5).
2. **Phase 2 — `THREATMODEL.md`** (deferred; user must green-light): expand the
   B1–B11 bypass surface into a full threat model with mitigations + residual risk.
3. **Phase 3 — hardening** (closes B1–B11), e.g.:
   - ~~Watchdog (B1): service ⇄ a protected helper restart each other (the old
     MM_notify2 twin was a weak version — reinstate properly).~~
     ✅ Done 13/06/2026 — both layers code-complete and **live-verified the same
     night: elevated smoke test 47/47** (kill drills K1–K4 + no-restart-loop
     expiry watch). ARCHITECTURE B1 → Medium; residuals documented there.
   - ~~Re-assert hosts every few seconds + tamper-detect/restore (B2).~~
     ✅ Done 12/06/2026 (software side; snapshot-deletion residual documented)
     and **live-verified 12/06/2026 night — elevated smoke test 27/27**.
   - ~~`SafeBoot` service registration so it runs in Safe Mode (B3).~~
     ✅ Done 13/06/2026 — service self-registers under the SafeBoot Minimal+Network
     keys (self-healed each tick, removed at expiry; 136/136, verifier-SHIP).
     **Registration live-verified — smoke test 52/52; ARCHITECTURE B3 → Low.**
     Caveat: the in-Safe-Mode run itself was NOT reboot-tested (skipped by
     choice) — it rests on the standard SafeBoot mechanism. Optional: a manual
     Safe Mode reboot would close that last gap.
   - Monotonic/authenticated time instead of trusting `DateTime.Now` (B4; the
     notifier's clock-change compensation is only a partial mitigation).
   - WFP/firewall-layer enforcement so DNS/DoH/VPN can't trivially bypass hosts (B5).
   - Signed/HMAC config so editing the ini to end early is rejected (B7).
   - Gate uninstall/removal while a block is active (B6/B8).
   - Document residual B10 (offline/admin) + the non-code mitigations.

---

## 7. Key files

- `MonkMode/Program.vb` — CLI entry, arg parsing, verbs.
- `MonkMode/Blocker.vb` — hosts/config/service/notifier orchestration (the contract).
- `MonkMode/Crypto.vb` — Simple3Des.
- `MonkMode/ServiceTools.vb` — advapi32 service install/start + SCM recovery policy (authored by us).
- `MonkMode/IniFileVb.vb` — INI reader/writer (inherited).
- `MonkMode_srv/MonkMode_srv/Service1.vb` — the enforcement service (inherited logic + tested fail-closed gates: strip/repair/expiry/peer-spawn/safeboot).
- `MM_notify/MM_notify/Form1.vb` — notifier (toast + app-kill + clock comp).
- `MM_guard/MM_guard/Guardian.vb` — guardian decision gates (pure, tested).
- `MM_guard/MM_guard/Program.vb` — guardian loop + SCM restart + CreateProcessAsUser notifier relaunch.
- `ARCHITECTURE.md` — bypass surface B1–B11 + honest ceiling.
- `tools/build-dist.ps1` — assemble `dist\`.

---

## 8. Gotchas learned (so you don't rediscover them)

- VB WinForms on .NET 8 SDK: delete hand-written `My Project\*.Designer.vb` (the
  SDK regenerates them) and set `GenerateAssemblyInfo=false` to keep `AssemblyInfo.vb`.
- For a non-WinForms VB exe (the CLI), use `<MyType>Empty</MyType>` — `Console`
  pulls in `My.Application`/`My.Computer` types that aren't available and won't build.
- `ServiceBase` (to host a service) IS provided by the
  `System.ServiceProcess.ServiceController` NuGet package on .NET. `ServiceController`
  + `Microsoft.Win32.Registry` packages cover the rest.
- Each project carried a stray duplicate `IniFileVb.vb` in a subfolder that the old
  projects didn't compile but SDK globbing did → removed.
- The repo can't build with stock VS2022 alone (no .NET Framework targeting packs);
  the user-scoped .NET 8 SDK is what works.
- **Rebuild `dist\` (`tools\build-dist.ps1`) before EVERY smoke test.** The smoke
  test installs from `dist\`, which nothing rebuilds automatically — committing
  code does not refresh it. A stale dist caused a 31/16 false-fail B1 run on
  13/06/2026 (binaries predated the B1 commits: no recovery policy, no
  `mm_guard.exe` in the folder), with the telltale signature: every NEW check
  red, every pre-existing check green.
- **The `-IncludeClockTest` smoke drill must restore the clock with a MONOTONIC
  timer, not `(Get-Date) - $t0`.** The original restore subtracted using the
  already-jumped wall clock, so it re-applied the +10 min and left the system
  clock ~10 min fast. That froze HighWater (every tick then looked like a forward
  jump, so the block never lifted) AND tripped the `Get-Date`-based section-3
  deadline → a **53/10 false-fail on 14/06/2026 where ONLY the 10 expiry checks
  failed** (all enforcement/tamper checks passed). Fixed with
  `[System.Diagnostics.Stopwatch]` (clock-immune) in `run-smoketest.ps1`. The
  product was correct — it MUST NOT lift on a dishonest clock (fail-closed).
  Always sanity-check the clock (`w32tm /stripchart /computer:time.windows.com`)
  after a clock-drill run; resync with `w32tm /resync /force` if it drifted.
