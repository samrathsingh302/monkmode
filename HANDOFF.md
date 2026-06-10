# MonkMode — Handoff / Continuation Doc

This file lets a new chat (or a new session) pick up exactly where we left off.
Read this first, then `ARCHITECTURE.md` (component map + bypass surface) and
`README` (usage/build).

Last updated: 2026-06-10.

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

Local repo: `c:\Users\samra\projects\Cold-Turkey-Serious`
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
- These source fixes are **not yet committed** (working tree on `monkmode`).
- The expiry **toast** and **app-kill / clock-change** paths weren't asserted
  programmatically (mm_notify is confirmed running, but watch for the balloon at
  expiry and test `--apps` / clock-roll manually if you want belt-and-suspenders).
- Residual for Phase 3: between 10s re-asserts an admin user can clear read-only
  and edit hosts; the timer re-asserts the attribute but does NOT yet restore
  deleted entries (this is the B2 "re-assert hosts" hardening item).
- Phase 2 (full threat model) — **explicitly deferred by the user; do not start
  without asking.**
- Phase 3 (hardening) — not started.

---

## 6. Next steps / roadmap

1. **Runtime smoke test** of the CLI end-to-end (see above). Highest priority —
   the migration is only compile-verified.
2. **Phase 2 — `THREATMODEL.md`** (deferred; user must green-light): expand the
   B1–B11 bypass surface into a full threat model with mitigations + residual risk.
3. **Phase 3 — hardening** (closes B1–B11), e.g.:
   - Watchdog: service ⇄ a protected helper restart each other (B1; note the old
     MM_notify2 twin that provided a weak version of this was removed — reinstate
     properly here).
   - Re-assert hosts every few seconds + tamper-detect/restore (B2).
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
