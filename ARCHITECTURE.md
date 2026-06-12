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
- Blocking is purely **hosts-file DNS sinkholing** (`0.0.0.0`).
- The unlock decision trusts **`DateTime.Now`** (system local clock).
- There is **no watchdog**: nothing restarts the service or the notifier if they
  are force-killed.

## 4. Bypass surface (current state — these all work today)

Ranked roughly by how easily a motivated user pulls them off.

> **Status update 12/06/2026:** B2 is now **mitigated in software** (self-healing
> hosts — see its row). Every other row still works as described. Unit-tested
> (81/81); live elevated smoke test of the repair path still pending.

| # | Bypass | Why it works now | Severity |
|---|---|---|---|
| B1 | **Force-kill the service** (`taskkill /f`, Process Explorer, `sc` via SYSTEM token, pskill). | `CanStop=False` only blocks graceful stop; a force kill still terminates the process. No watchdog restarts it. Once dead, hosts is just a read-only file. | Critical |
| B2 | **Clear the read-only attribute and edit/blank hosts.** | **MITIGATED 12/06/2026 (software side).** The CLI persists the exact marker block to `monkmode_hosts.block` (next to the exes); while the block is unexpired, the service's 10s timer re-asserts read-only and restores tampered/deleted/blanked entries from that snapshot (`Service1.RepairHostsBlock` — fail-closed expiry gate, user content preserved, no rewrite when intact). **Residuals:** an admin can delete the snapshot file itself (repair then degrades to attribute re-assert only); an edit sticks for up to ~10s until the next tick; and if the service is dead (B1) nothing repairs — B2's fate is chained to B1. | ~~Critical~~ → Low while the service runs (residuals listed; B1 unchanged) |
| B3 | **Boot into Safe Mode**, then edit hosts / delete files / `sc delete KCTRP`. | Service has no `SafeBoot` registration, so it does **not** run in Safe Mode. Everything is editable. | Critical |
| B4 | **Roll the system clock forward.** | Unlock compares `Time/Until` to `DateTime.Now`. Set the clock past the end time and the next 10 s tick calls `stopMe()` and lifts the block. `CurrentTime/Now` heartbeat is written but never enforced against rollback. | Critical |
| B5 | **Change DNS / use DoH / VPN / proxy / Tor.** | Hosts only intercepts the OS resolver. Browser DoH, a public resolver, or a VPN ignores hosts entirely. | Critical |
| B6 | **`sc delete KCTRP`** (README literally documents this). | Service is removable by any admin. | High |
| B7 | **Recover the config key.** `"mm_textbox"` is hardcoded in the binaries; TripleDES end time can be re-encrypted to "now" and written into `monkmode_settings.ini`. | Symmetric key shipped in the client; config is not tamper-evident (no HMAC/signature). | High |
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
