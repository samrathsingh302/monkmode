# MonkMode — Claude Code instructions
**Read order, every session:** 1) this file · 2) HANDOFF.md (always — current state, gotchas) + ARCHITECTURE.md (bypass surface B1–B11) + README
· 3) C:\Users\samra\vault\_shared-context\SAMRATH.md + PROMPTING_GUIDE.md + ORCHESTRATION.md (who you work for, how we prompt, how to split work across agents).
If _shared-context is unreachable, say so; key defaults: free tiers only · British English, dd/mm/yyyy, £ · evidence over intuition · no data loss · don't ask about things SAMRATH.md §3 lets you decide; always ask about §4.

## What this is
A personal, tamper-resistant website/app self-control blocker for Windows — Samrath's own fork of Cold Turkey (GPLv3), rebranded and being hardened on his own machine. Goal: once a block starts it can't be casually removed before its timer expires. Done = defeat casual→determined bypasses (B1–B9); B10 offline/admin is honestly out of scope.

## Stack & layout
- VB.NET / .NET 8 (net8.0-windows), SDK-style projects. Solution: `MonkMode.sln` (4 projects + `MonkMode.Tests`). No GUI — it's a CLI.
- `MonkMode/` → `monkmode.exe` (CLI: writes hosts + config, installs/starts the service + SCM recovery, registers the notifier).
- `MonkMode_srv/` → `MonkMode_srv.exe` (**LocalSystem service `MONKMODE`**, `CanStop=False`, 10s timer — the enforcement core; inherited logic preserved, hardened via tested fail-closed gates: B2 hosts self-heal, B1 guardian spawn, B3 SafeBoot self-register).
- `MM_notify/` → `mm_notify.exe` (user-session notifier: app-kill, clock-change comp, tray-toast at expiry).
- `MM_guard/` → `mm_guard.exe` (SYSTEM-session watchdog guardian spawned by the service: SCM-restarts a killed service, relaunches the notifier; exits only on genuine expiry).
- Build: `C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release` (SDK is user-scoped, not on PATH).
- Tests: `MonkMode.Tests/` (xunit, C# — VB can't reference both `MonkMode` and `monkmode` namespaces); run `C:\Users\samra\.dotnet\dotnet.exe test MonkMode.sln`. Pure unit tests on strings/temp paths only. Live-path verification is the manual elevated smoke test (last run **52/52, 13/06/2026** — B1 + B2 fully live-verified, B3 registration live-verified; the B3 in-Safe-Mode run was not reboot-tested, by choice; rebuild `dist\` first, see HANDOFF §8).

## Fences
- **Never run the service / CLI during dev or audit** — it edits the LIVE hosts file, adds an HKCU `Run` entry, and installs a `CanStop=False` LocalSystem service. Read-only analysis unless Samrath explicitly asks for a live test. Unit tests must never touch real hosts/registry/SCM either.
- **No data loss on hosts restore:** only ever touch the MonkMode marker block (`#### MonkMode Entries ####`), never the user's own hosts content.
- **Never force-push or rewrite history on the `monkmode` branch** — everything is committed and pushed as of 12/06/2026 eve; the remote history is the safety net. Don't disturb git state otherwise — no reset/checkout/rebase unless asked.
- `master` is the untouched original Cold Turkey — never work on it.
- Crypto is documented-weak by design (`Simple3Des`/`mm_textbox`, hardcoded symmetric key, no HMAC) and is **Phase-3-owned (B7)** — not a new finding; don't re-flag it.

## Working style
One slice per session · decompose across agents first (ORCHESTRATION.md: how does this split?) ·
Phase 1 done; Phase 2 (threat model) deferred — don't start without asking; Phase 3 (hardening, B1–B11) is the backlog ·
end every session: update HANDOFF + emit the carry-on prompt (PROMPTING_GUIDE §4).
