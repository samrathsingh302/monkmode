# MonkMode — Claude Code instructions
**Read order, every session:** 1) this file · 2) HANDOFF.md (always — current state, gotchas) + ARCHITECTURE.md (bypass surface B1–B11) + README
· 3) C:\Users\samra\projects\_shared-context\SAMRATH.md + PROMPTING_GUIDE.md + ORCHESTRATION.md (who you work for, how we prompt, how to split work across agents).
If _shared-context is unreachable, say so; key defaults: free tiers only · British English, dd/mm/yyyy, £ · evidence over intuition · no data loss · don't ask about things SAMRATH.md §3 lets you decide; always ask about §4.

## What this is
A personal, tamper-resistant website/app self-control blocker for Windows — Samrath's own fork of Cold Turkey (GPLv3), rebranded and being hardened on his own machine. Goal: once a block starts it can't be casually removed before its timer expires. Done = defeat casual→determined bypasses (B1–B9); B10 offline/admin is honestly out of scope.

## Stack & layout
- VB.NET / .NET 8 (net8.0-windows), SDK-style projects. Solution: `MonkMode.sln` (3 projects). No GUI — it's a CLI.
- `MonkMode/` → `monkmode.exe` (CLI: writes hosts + config, installs/starts the service, registers the notifier).
- `MonkMode_srv/` → `MonkMode_srv.exe` (**LocalSystem service `MONKMODE`**, `CanStop=False`, 10s timer — the enforcement core; logic UNCHANGED).
- `MM_notify/` → `mm_notify.exe` (user-session notifier: app-kill, clock-change comp, tray-toast at expiry).
- Build: `C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release` (SDK is user-scoped, not on PATH).
- No automated tests; verification is the manual elevated smoke test (last run 15/15, see HANDOFF §5).

## Fences
- **Never run the service / CLI during dev or audit** — it edits the LIVE hosts file, adds an HKCU `Run` entry, and installs a `CanStop=False` LocalSystem service. Read-only analysis unless Samrath explicitly asks for a live test.
- **No data loss on hosts restore:** only ever touch the MonkMode marker block (`#### MonkMode Entries ####`), never the user's own hosts content.
- **Don't lose the unpushed `monkmode`-branch commit** (working tree / source fixes not yet pushed). Don't disturb git state — no commit/push/reset/checkout unless asked.
- `master` is the untouched original Cold Turkey — never work on it.
- Crypto is documented-weak by design (`Simple3Des`/`mm_textbox`, hardcoded symmetric key, no HMAC) and is **Phase-3-owned (B7)** — not a new finding; don't re-flag it.

## Working style
One slice per session · decompose across agents first (ORCHESTRATION.md: how does this split?) ·
Phase 1 done; Phase 2 (threat model) deferred — don't start without asking; Phase 3 (hardening, B1–B11) is the backlog ·
end every session: update HANDOFF + emit the carry-on prompt (PROMPTING_GUIDE §4).
