# MonkMode — Architecture & Bypass Surface (Phase 0)

> Grounding document produced before any hardening. Describes the system **as
> inherited from the open-source Cold Turkey codebase**, then catalogs every
> realistic way the current design can be bypassed. Phase 2 expands this into a
> full threat model; Phase 3 closes the holes.
>
> **Update (post-Phase-1, .NET 8 + CLI):** The front-end is now a console **CLI
> (`monkmode.exe`)** instead of a WinForms GUI, and the notifier shows a tray
> toast (the `MM_notify2` twin and `MM_popup` window were removed). The
> **enforcement model below — and therefore the entire bypass surface B1–B11 —
> is unchanged**; only the configuration front-end changed. Identifiers are now
> the MonkMode names (service `MONKMODE`, `MonkMode_srv.exe`,
> `monkmode_settings.ini`, key `mm_textbox`, marker `#### MonkMode Entries ####`).

## 1. Components

The product is **four cooperating VB.NET (.NET 2.0, x86) programs** built from
five Visual Studio 2010 solutions. There is **no C++** — the "service" is also
VB.NET.

Current (post-migration) components — three cooperating VB.NET (.NET 8,
net8.0-windows) programs:

| Project | Output exe | Runs as | Role |
|---|---|---|---|
| `MonkMode` | `monkmode.exe` | User (elevated, requireAdministrator) | CLI. Parses `block`/`status`/`add`, writes the hosts file, writes the encrypted config, installs & starts the service, registers the notifier. |
| `MonkMode_srv` | `MonkMode_srv.exe` | **LocalSystem service `MONKMODE`** | Enforcer. Holds the hosts file locked, kills blocked session-0 processes, restores hosts & stops itself when the timer expires. |
| `MM_notify` | `mm_notify.exe` | User session (HKCU `Run`) | Notifier. Kills blocked apps in the user session, compensates for clock changes, shows a tray-balloon toast when the block ends. |

The original inherited design (described below) was a **four**-program VB.NET
2.0 set with a WinForms GUI plus `MM_notify2` and an `MM_popup` window; those
two were removed during the CLI migration.

## 2. How a block works (control flow)

1. **GUI (`mainScreen.vb`)** — user checks sites / adds custom domains / adds
   app `.exe`s and a future end time.
   - `writeToHostsFile()` appends `#### Cold Turkey Entries ####` + `0.0.0.0`
     lines to `%WinDir%\system32\drivers\etc\hosts`, then sets the file
     read-only (`SetAttr ... vbReadOnly`).
   - `startService()` writes the end time into `ct_settings.ini` (TripleDES,
     key `"ct_textbox"`, key name `Time/Until`), then
     `ServiceInstaller.InstallAndStart("KCTRP", ... , "...\KCTRP_srv.exe")`.
   - Registers `HKLM\...\CurrentVersion\Run\ColdTurkey_notify -> ct_notify.exe`
     and launches `ct_notify.exe` + `ct_notify2.exe`.
2. **Service (`Service1.vb`)** — installed `LocalSystem`, `StartType=Automatic`,
   `CanStop=False`.
   - On start: opens hosts in append mode and re-marks it read-only.
   - `timer` every **10 s**: re-reads `ct_settings.ini`, kills any session-0
     process whose name is in the encrypted `Process/List`, and compares
     `Time/Until` to `DateTime.Now`. When `timeLeft <= 5`, `stopMe()` strips the
     MonkMode block out of hosts, marks `User/Done=yes`, and `End`s.
   - `adder` is a `FileSystemWatcher` on `...\etc\add_to_hosts`: when the GUI
     drops that file (adding sites mid-block), the service appends it to hosts.
3. **Config (`ct_settings.ini`)** lives in the app folder. Sections:
   `Process/List` (encrypted app list), `User/*` (flags), `Time/Until`
   (encrypted end time), `Time/TimeChanging`, `CurrentTime/Now` (encrypted
   heartbeat). Crypto is **TripleDES with a hardcoded key `"ct_textbox"`** — the
   same key is compiled into both the GUI and the service.

## 3. Trust / enforcement model (inherited)

- The **only** enforcement boundary is: a `LocalSystem` service that (a) keeps
  the hosts file read-only and (b) refuses to stop until its stored end time
  passes. `CanStop=False` blocks the *graceful* SCM stop path only.
  *(Since 12/06/2026 the service also (c) restores its hosts entries from a
  CLI-written snapshot every 10s while the block is active — the B2 self-heal.)*
  *(Since 13/06/2026 the service also (d) registers itself under the SafeBoot
  Minimal+Network keys — self-healed every tick, removed at expiry — so it runs
  in Safe Mode; see B3. Registration live-verified 13/06/2026 (smoke test 52/52:
  written / self-healed / removed); the actual Safe-Mode boot was not
  reboot-tested.)*
- Blocking is purely **hosts-file DNS sinkholing** (`0.0.0.0`).
- The unlock decision trusts **`DateTime.Now`** (system local clock).
- There is **no watchdog**: nothing restarts the service or the notifier if they
  are force-killed.
  *(Since 13/06/2026 this is addressed in software — see B1: the CLI configures
  SCM auto-restart (FailureActions) on the service at install (layer 1), and the
  mutual service ⇄ guardian restart pair is wired (layer 2): the service's timer
  spawns/respawns the SYSTEM-session `mm_guard.exe`, which reciprocally restarts
  the service via the SCM and relaunches the notifier into the user session.
  Both layers **live-verified 13/06/2026** — elevated smoke test 47/47.)*

## 4. Bypass surface (current state — these all work today)

Ranked roughly by how easily a motivated user pulls them off.

> **Status update 12/06/2026:** B2 is now **mitigated in software** (self-healing
> hosts — see its row). Every other row still works as described. Unit-tested
> (81/81); **live-verified 12/06/2026** — the elevated smoke test passed 27/27,
> including both B2 tamper drills (delete-our-block and blank-hosts: restored
> ≤35s, user content preserved, read-only re-asserted, no rewrite churn).
>
> **Status update 13/06/2026:** B1 layer 1 (SCM auto-restart on force-kill) is
> **code-complete but NOT yet live-verified** — `monkmode block` now sets the
> service's FailureActions; this needs an elevated smoke test (kill the service,
> confirm the SCM restarts it; `sc qfailure MONKMODE` shows the policy landed)
> before B1 can be called mitigated.
>
> **Status update 13/06/2026 (later, layer 2):** the mutual watchdog is now
> **wired** — new SYSTEM-session guardian `mm_guard.exe` (decision (A): child
> process spawned by the service, not a second service). Service timer ⇄
> guardian mutually restart each other through pure fail-closed gates; the
> guardian also relaunches `mm_notify` into the interactive session
> (`WTSQueryUserToken` + `CreateProcessAsUser`). Suite 123/123; fresh-context
> verifier: SHIP. Still NOT live-verified — the elevated smoke test must also
> confirm: kill service → SCM restarts it (≤~1 s); kill guardian → service
> respawns it (≤10 s); kill service AND disable recovery → guardian restarts
> it (≤10 s); block expiry → service stops, guardian exits, **no restart
> loop** (stopMe ⇄ recovery/guardian interaction).
>
> **Status update 13/06/2026 (night): B1 LIVE-VERIFIED — supersedes the two
> notes above.** The extended elevated smoke test passed **47/47**: recovery
> policy exact (3× RESTART, 1000 ms, reset INFINITE); guardian spawned 6.9 s as
> SYSTEM; K1 force-kill service → SCM restart in 0.4 s, exactly one guardian;
> K2 kill guardian → service respawned it in 7.4 s; K3 kill notifier → guardian
> relaunched it in 10.5 s **into the user session**; K4 recovery disabled + kill
> → guardian alone restarted the service in 11 s, policy restored; block
> enforced through all drills; clean expiry with **no restart loop**, no stray
> processes. (A first run failed 31/16 purely because `dist\` was stale — built
> before the B1 commits, no `mm_guard.exe` present. **Rebuild `dist\` via
> `tools\build-dist.ps1` before any smoke test.**)
>
> **Status update 13/06/2026 (night): B3 registration LIVE-VERIFIED — supersedes
> the note above; B3 → Low.** The extended elevated smoke test passed **52/52**:
> both SafeBoot keys present at block start (tag `Service`); the section-2d
> self-heal drill (both keys deleted → service re-asserted them in 9.1 s); both
> keys removed after expiry; clean teardown. So the SafeBoot **registration
> lifecycle** (write / self-heal / remove) is proven live. The second half of the
> original gate — a manual Safe Mode reboot to confirm the service actually
> *runs* there — was **deliberately skipped** (Samrath's call). B3's final hop
> therefore rests on the standard, documented Windows SafeBoot mechanism (a
> correctly-registered auto-start service is started in Safe Mode), not an
> empirical reboot; the severity flip to Low carries that caveat explicitly.

| # | Bypass | Why it works now | Severity |
|---|---|---|---|
| B1 | **Force-kill the service** (`taskkill /f`, Process Explorer, `sc` via SYSTEM token, pskill). | `CanStop=False` only blocks graceful stop; a force kill still terminates the process. **MITIGATED 13/06/2026 (both layers live-verified — elevated smoke test 47/47, kill drills K1–K4 + no-restart-loop expiry watch):** **Layer 1** — the CLI sets SCM **FailureActions** on `MONKMODE` at install — restart on every failure forever (3× RESTART, 1 s delay, reset period INFINITE) + restart-on-non-crash flag (`ServiceTools.SetRecoveryOptions`). **Layer 2** — mutual service ⇄ guardian restart pair (the proper version of the removed `MM_notify2` twin): the service's timer spawns/respawns the SYSTEM-session `mm_guard.exe` through the tested fail-closed gate `Service1.ShouldRestartPeer`; the guardian reciprocally SCM-restarts a killed service and relaunches `mm_notify` into the user session, and stands down only on a genuinely parsed, past end time (`stopMe()` also kills it at expiry). **Residuals (honest):** a scripted near-simultaneous double-kill of service+guardian within the ~1 s/10 s restart windows, a SYSTEM-token kill that also disables recovery (`sc failure … reset= 0`), suspend-then-kill of both processes, or an **elevated** user pre-pinning the guardian (start your own `mm_guard.exe` first: it holds the single-instance mutex but lacks SCM rights, neutralising layer 2 — layer 1 still covers) still win; true kill-immunity needs a PPL/kernel driver (out of scope, B10-tier). | ~~Critical~~ → **Medium** (live-verified 13/06/2026; residuals listed) |
| B2 | **Clear the read-only attribute and edit/blank hosts.** | **MITIGATED 12/06/2026 (software side).** The CLI persists the exact marker block to `monkmode_hosts.block` (next to the exes); while the block is unexpired, the service's 10s timer re-asserts read-only and restores tampered/deleted/blanked entries from that snapshot (`Service1.RepairHostsBlock` — fail-closed expiry gate, user content preserved, no rewrite when intact). **Residuals:** an admin can delete the snapshot file itself (repair then degrades to attribute re-assert only); an edit sticks for up to ~10s until the next tick; and if the service is dead (B1) nothing repairs — B2's fate is chained to B1. | ~~Critical~~ → Low while the service runs (residuals listed; B1 unchanged) |
| B3 | **Boot into Safe Mode**, then edit hosts / delete files / `sc delete MONKMODE`. | Originally: the service had no `SafeBoot` registration, so it did **not** run in Safe Mode and everything was editable. **MITIGATED 13/06/2026 (registration live-verified — smoke test 52/52; in-Safe-Mode run not reboot-tested, see ceiling):** the service now registers itself under BOTH `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE` and `…\SafeBoot\Network\MONKMODE` (each `(Default)=Service`) at `OnStart`, re-asserts them every 10s tick while the block is active (fail-CLOSED via `Not BlockHasExpired`; read-only probe first, so an intact registration is a true no-op — no churn), and removes both at a genuine expiry (`stopMe`). So it runs in plain Safe Mode AND Safe Mode with Networking, where it keeps hosts locked + self-healing (B2) and the watchdog pair alive (B1); deleting the keys mid-block is repaired within a tick (live-verified: re-asserted in 9.1 s). Only MonkMode's own two leaf keys are ever touched (no-data-loss fence). **Residuals (honest):** an admin who deletes the keys AND stops the service+guardian from rewriting them wins (the B1 kill problem — B3 chained to B1); `sc delete` during an active block (B6) orphans the keys harmlessly; an offline/WinRE edit (B10) still wins. **Verification ceiling:** the smoke test proves the keys are written/re-asserted/removed live, but that the service actually *runs* in Safe Mode was **not** reboot-tested (deliberately skipped) — it rests on the standard, documented Windows SafeBoot mechanism. | ~~Critical~~ → **Low** (registration live-verified 52/52; in-Safe-Mode run rests on the standard SafeBoot mechanism, not reboot-tested) |
| B4 | **Roll the system clock forward.** | Originally: unlock compared `Time/Until` to `DateTime.Now`, so setting the clock past the end time made the next 10 s tick `stopMe()` and lift the block. **PARTIAL MITIGATION (2026-06-13, `a32a0cd`):** every expiry/self-heal decision uses a MAC-covered `[Time] HighWater` mark as `asOf` instead of `DateTime.Now`; a single clock jump > 120 s past `Until` is refused (`ClassifyTimeAdvance` → `ForwardJump` → mark not advanced), and tampering the mark fails closed post-B7. **Within-ceiling clock-creep (audit 2026-06-13, verifier-confirmed P1) — FIXED IN CODE (`CapHighWaterAdvance`):** the advance was capped per *step* (≤ 120 s) but not bound to real time, so nudging the clock +119 s before each 10 s tick walked `HighWater` ~12× faster than honest time → early lift. **Fix:** each tick's credit is now bounded by REAL monotonic elapsed (`Environment.TickCount64`, clock-immune): `credited = min(wallDelta, monoElapsed)`. A creep step credits only the ~10 s of real time (and in fact freezes the mark once the racing wall gets > ceiling ahead — extra fail-closed); honest ticks credit the full ~10 s and lift normally. `OnStart` no longer credits the boot gap at all (no monotonic anchor across restart). Pinned by a `CapHighWaterAdvanceTests` creep regression. **CLI-seam variant also closed (cross-slice audit):** `Blocker.BlockIsActive()` used to decide "is a block standing?" off raw `DateTime.Now`, so a clock-forward let `monkmode block --for 1m` overwrite the standing block with a fresh short one — now it decides off the persisted HighWater + the B7 MAC (`Blocker.BlockGenuinelyExpired`), fail-closed. **NOT yet live-verified** — the monotonic wiring (`TickCount64`, jitter, the `lastMonoMs` thread) wants the live B4 clock drill. | Critical → **Medium** *(single-jump + tamper + creep closed in code; pending live B4 clock test before Low)* |
| B5 | **Change DNS / use DoH / VPN / proxy / Tor.** | Hosts only intercepts the OS resolver. Browser DoH, a public resolver, or a VPN ignores hosts entirely. | Critical |
| B6 | **`sc delete MONKMODE`** (README literally documents this). | Originally: the service was removable by any admin. **MITIGATION CODE-COMPLETE + UNIT-TESTED (2026-06-13, working tree — NOT yet committed, NOT yet live-verified):** while a block is active the service carries a deny-DELETE ACE `(D;;SD;;;BA)` on its object DACL (asserted at OnStart + re-asserted every tick, fail-CLOSED via `Not EffectiveBlockHasExpired`, read-only probe = no churn), removed at genuine expiry in `stopMe()`. **Brick-safe by construction:** denies DELETE (SD) ONLY — never WRITE_DAC/WRITE_OWNER — so the LocalSystem service (SY) and the elevated admin CLI (BA, still holding WRITE_DAC) can always restore the DACL. The deliberate clean exit is **`monkmode unblock --force`** (disables recovery, kills the watchdog pair, removes the ACE, deletes the service, strips hosts, removes the B2 snapshot + B3 SafeBoot keys + autorun). Pure SDDL surgery (`ServiceSecurity.vb`, CLI+service parity-pinned). **Residuals:** an admin who clears the ACE AND stops the service+guardian re-asserting wins (chained to B1, like B3); offline/WinRE delete (B10) still wins. | High → *(pending live smoke test, then expected Medium)* |
| B7 | **Recover the config key.** `"mm_textbox"` is hardcoded in the binaries; TripleDES end time can be re-encrypted to "now" and written into `monkmode_settings.ini`. | Originally: symmetric key shipped in the client; config not tamper-evident. **MITIGATION CODE-COMPLETE + UNIT-TESTED (2026-06-13, `1794bde`; NOT yet live-verified):** an HMAC-SHA256 over the *decrypted* config canonical (`[Integrity] Mac`), keyed by a random 32-byte key DPAPI-protected at LocalMachine scope (`[Integrity] Key`), is re-stamped by every writer. Expiry is fail-CLOSED — `EffectiveBlockHasExpired = macValid AndAlso BlockHasExpired` — so an absent/forged/foreign-machine MAC reads as "not expired" and the block stands. `ConfigIntegrity` byte-identical across all four projects (parity-pinned). **Residual/ceiling:** an attacker who runs code as admin can still recover the DPAPI key and forge a MAC — full closure needs TPM/PPL (B10-tier); this defeats the blind ini-edit, not a determined admin. | High → *(pending live smoke test, then expected Medium)* |
| B8 | **Delete the app folder / `monkmode_settings.ini`.** | On a missing/short ini the service rewrites a default — but the GUI and removal paths assume the folder exists; deleting binaries while the service is killed removes enforcement. | High |
| B9 | **Just don't run as session 0 / use another user account or portable browser.** | App-kill only targets `SessionId = 0`; blocking is per-machine hosts but DNS escapes (B5) and second browsers dodge app rules. | Medium |
| B10 | **Offline attack:** boot from USB / WinRE, mount the disk, edit hosts or delete the service binary & registry key. | Nothing on the same unencrypted disk can defend against an offline editor. | Medium (needs effort) |
| B11 | **Single hardcoded artifacts** (service name `KCTRP`, file marker, mutex `KeepmealivepleaseKCTRP`, ini path) make scripted teardown trivial and copy-pasteable. | All identifiers are fixed and public. | Low (enabler) |

### Latent bug noted during reading
`writeToHostsFile()` writes the marker `#### Cold Turkey Entries ####` (with a
space) but `erroredOut()` searches for `#### ColdTurkey Entries ####` (no
space). The rebrand standardizes the marker to a single string everywhere, which
incidentally removes this inconsistency.

## 5. Hard truth about the goal ("only a PC reset can remove it")

While the daily user retains **Administrator rights + physical disk access**,
*no* software on that disk can be made truly unremovable — B10 always wins
eventually. Achievable bar (Phase 3): defeat B1–B9 (casual → moderately
determined bypasses), i.e. Cold Turkey Pro / Freedom level. Closing B10 requires
measures outside this codebase: a **non-admin daily account**, **full-disk
encryption (BitLocker)**, and a **BIOS/boot-order lock**. Phase 3 will implement
the software mitigations and document these residual requirements honestly
rather than claiming "unbreakable."
