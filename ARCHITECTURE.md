# MonkMode — Architecture & Bypass Surface (Phase 0)

> Grounding document produced before any hardening. Describes the system **as
> inherited from the open-source Cold Turkey codebase**, then catalogs every
> realistic way the current design can be bypassed. Phase 2 expands this into a
> full threat model; Phase 3 closes the holes.

## 1. Components

The product is **four cooperating VB.NET (.NET 2.0, x86) programs** built from
five Visual Studio 2010 solutions. There is **no C++** — the "service" is also
VB.NET.

| Project (orig) | Output exe | Runs as | Role |
|---|---|---|---|
| `ColdTurkey` | `ColdTurkey.exe` | User (elevated to write hosts) | GUI. Picks sites/apps + end time, writes the hosts file, installs & starts the service, registers the notifier. |
| `kasrp_srv` | `kctrp_srv.exe` | **LocalSystem service `KCTRP`** | Enforcer. Holds the hosts file locked, kills blocked processes, restores hosts & stops itself when the timer expires. |
| `CT_notify` / `CT_notify2` | `ct_notify.exe` / `CT_notify2.exe` | User session (HKCU `Run`) | Background watcher; triggers the popup when time is up. |
| `CT_popup` | `CT_popup.exe` | User session | The "time's up" popup. |

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
- Blocking is purely **hosts-file DNS sinkholing** (`0.0.0.0`).
- The unlock decision trusts **`DateTime.Now`** (system local clock).
- There is **no watchdog**: nothing restarts the service or the notifier if they
  are force-killed.

## 4. Bypass surface (current state — these all work today)

Ranked roughly by how easily a motivated user pulls them off.

| # | Bypass | Why it works now | Severity |
|---|---|---|---|
| B1 | **Force-kill the service** (`taskkill /f`, Process Explorer, `sc` via SYSTEM token, pskill). | `CanStop=False` only blocks graceful stop; a force kill still terminates the process. No watchdog restarts it. Once dead, hosts is just a read-only file. | Critical |
| B2 | **Clear the read-only attribute and edit/blank hosts.** | Block is only an `attrib +r` flag; any admin clears it and rewrites hosts. If the service is also dead (B1) nothing re-asserts it. | Critical |
| B3 | **Boot into Safe Mode**, then edit hosts / delete files / `sc delete KCTRP`. | Service has no `SafeBoot` registration, so it does **not** run in Safe Mode. Everything is editable. | Critical |
| B4 | **Roll the system clock forward.** | Unlock compares `Time/Until` to `DateTime.Now`. Set the clock past the end time and the next 10 s tick calls `stopMe()` and lifts the block. `CurrentTime/Now` heartbeat is written but never enforced against rollback. | Critical |
| B5 | **Change DNS / use DoH / VPN / proxy / Tor.** | Hosts only intercepts the OS resolver. Browser DoH, a public resolver, or a VPN ignores hosts entirely. | Critical |
| B6 | **`sc delete KCTRP`** (README literally documents this). | Service is removable by any admin. | High |
| B7 | **Recover the config key.** `"ct_textbox"` is hardcoded in the binaries; TripleDES end time can be re-encrypted to "now" and written into `ct_settings.ini`. | Symmetric key shipped in the client; config is not tamper-evident (no HMAC/signature). | High |
| B8 | **Delete the app folder / `ct_settings.ini`.** | On a missing/short ini the service rewrites a default — but the GUI and removal paths assume the folder exists; deleting binaries while the service is killed removes enforcement. | High |
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
