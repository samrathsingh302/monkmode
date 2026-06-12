# MonkMode — Handoff / Continuation Doc

This file lets a new chat (or a new session) pick up exactly where we left off.
Read this first, then `ARCHITECTURE.md` (component map + bypass surface) and
`README` (usage/build).

Last updated: 2026-06-12.

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

**MonkMode is now a CLI — there is no GUI.** Three VB.NET programs, all
**.NET 8 (net8.0-windows)**, SDK-style projects:

| Project | Output | Runs as | Role |
|---|---|---|---|
| `MonkMode` | `monkmode.exe` | User, elevated (requireAdministrator) | **CLI.** Verbs `block`/`status`/`add`/`help`. Writes hosts, writes config, installs+starts service, launches notifier. |
| `MonkMode_srv` | `MonkMode_srv.exe` | **LocalSystem service `MONKMODE`** | **Enforcement core (UNCHANGED from inherited design).** Locks hosts read-only, kills blocked session-0 processes, lifts block + stops itself when timer expires. `CanStop=False`. 10s timer. |
| `MM_notify` | `mm_notify.exe` | User session (HKCU `Run`) | Notifier. Kills blocked apps in the *user* session, compensates for clock changes, shows a **tray-balloon toast** when the block ends. |

Removed during migration: the WinForms GUI, `MM_notify2` (watchdog twin), and
`MM_popup` (popup window).

Root solution: `MonkMode.sln` (3 projects). The old per-project `.sln` files were
deleted.

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
     fixed same-session. **17 new tests, 81/81 green.** NOT yet live-smoke-
     tested (no elevation this session) — re-run the elevated smoke test before
     trusting the repair path live; known residuals: the snapshot itself is
     deletable by an admin (documented, not hidden) and a legacy ANSI hosts
     file would be UTF-8-mangled on first repair (pre-existing class).
  2. **CI added**: `.github/workflows/ci.yml` — build + full suite on every
     push/PR to `monkmode`, windows-latest, free tier.
  3. **README → README.md** (git mv, history kept), rewritten as a CV-grade
     front page (architecture, tamper-resistance, honest threat model,
     engineering story, CI badge). GPL/Cold Turkey attribution intact.
  4. **`docs/CV.md` added** — CV bullets (3 sizes), elevator pitch, 5 STAR
     stories, interview Q&A, numbers table, honesty rules (it's a fork; weak
     crypto is B7-owned; never claim "unbreakable").

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
  remaining B2 residuals: snapshot deletable by admin; repair path not yet
  live-smoke-tested.*
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
   - Watchdog: service ⇄ a protected helper restart each other (B1; note the old
     MM_notify2 twin that provided a weak version of this was removed — reinstate
     properly here).
   - ~~Re-assert hosts every few seconds + tamper-detect/restore (B2).~~
     ✅ Done 12/06/2026 (software side; snapshot-deletion residual documented;
     live verification pending the next elevated smoke test).
   - `SafeBoot` service registration so it runs in Safe Mode (B3).
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
- `MonkMode/ServiceTools.vb` — advapi32 service install/start (authored by us).
- `MonkMode/IniFileVb.vb` — INI reader/writer (inherited).
- `MonkMode_srv/MonkMode_srv/Service1.vb` — the enforcement service (UNCHANGED logic).
- `MM_notify/MM_notify/Form1.vb` — notifier (toast + app-kill + clock comp).
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
