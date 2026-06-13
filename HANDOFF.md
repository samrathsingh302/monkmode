# MonkMode — Handoff / Continuation Doc

This file lets a new chat (or a new session) pick up exactly where we left off.
Read this first, then `ARCHITECTURE.md` (component map + bypass surface) and
`README` (usage/build).

Last updated: 2026-06-13 night (**B3 registration LIVE-VERIFIED — smoke test 52/52**; ARCHITECTURE B3 Critical → Low, with the caveat that the in-Safe-Mode run itself was NOT reboot-tested (skipped by choice) so it rests on the standard SafeBoot mechanism. Prior: B1 LIVE-VERIFIED 47/47 → Medium. Always rebuild `dist\` before smoke-testing — see §8).

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
