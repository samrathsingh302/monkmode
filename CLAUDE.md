# MonkMode — Claude Code instructions
**Read order, every session:** 1) this file · 2) current state = newest dated handoff in `C:\Users\samra\vault\dev\repos\monk-mode\handoffs\` (always — gotchas; handoffs moved to the vault 26/06/2026, no repo HANDOFF.md) + `C:\Users\samra\vault\dev\repos\monk-mode\specs\ARCHITECTURE.md` (bypass surface B1–B11) + README
· 3) C:\Users\samra\vault\dev\_global\_archive\doctrine\SAMRATH.md + LOOP-GUIDE.md (who you work for; how we prompt + split work across agents + run the autonomous loop — LOOP-GUIDE consolidates the former PROMPTING_GUIDE + ORCHESTRATION).
If _global is unreachable, say so; key defaults: free tiers only · British English, dd/mm/yyyy, £ · evidence over intuition · no data loss · don't ask about things SAMRATH.md §3 lets you decide; always ask about §4.

## What this is
A personal, tamper-resistant website/app self-control blocker for Windows — Samrath's own fork of Cold Turkey (GPLv3), rebranded and being hardened on his own machine. Goal: once a block starts it can't be casually removed before its timer expires. Done = defeat casual→determined bypasses (B1–B9); B10 offline/admin is honestly out of scope.

## Stack & layout
- VB.NET / .NET 8 (net8.0-windows), SDK-style projects. Solution: `MonkMode.sln` (4 projects + `MonkMode.Tests`). No GUI — it's a CLI.
- `MonkMode/` → `monkmode.exe` (CLI: writes hosts + config, installs/starts the service + SCM recovery, registers the notifier).
- `MonkMode_srv/` → `MonkMode_srv.exe` (**LocalSystem service `MONKMODE`**, `CanStop=False`, 10s timer — the enforcement core; inherited logic preserved, hardened via tested fail-closed gates: B2 hosts self-heal, B1 guardian spawn, B3 SafeBoot self-register).
- `MM_notify/` → `mm_notify.exe` (user-session notifier: app-kill, clock-change comp, tray-toast at expiry).
- `MM_guard/` → `mm_guard.exe` (SYSTEM-session watchdog guardian spawned by the service: SCM-restarts a killed service, relaunches the notifier; exits only on genuine expiry).
- Build: `C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release` (SDK is user-scoped, not on PATH).
- Tests: `MonkMode.Tests/` (xunit, C# — VB can't reference both `MonkMode` and `monkmode` namespaces); run `C:\Users\samra\.dotnet\dotnet.exe test MonkMode.sln`. Pure unit tests on strings/temp paths only. Live-path verification is the manual elevated smoke test (last run **63/63, 14/06/2026** — B1 + B2 + B4 + B6 + B7 live-verified, B3 registration live-verified; the B3 in-Safe-Mode run was not reboot-tested, by choice; rebuild `dist\` first via `tools\build-dist.ps1`, see `C:\Users\samra\vault\dev\repos\monk-mode\specs\ARCHITECTURE.md` §4).

## Fences
- **Never run the service / CLI during dev or audit** — it edits the LIVE hosts file, adds an HKCU `Run` entry, and installs a `CanStop=False` LocalSystem service. Read-only analysis unless Samrath explicitly asks for a live test. Unit tests must never touch real hosts/registry/SCM either.
- **No data loss on hosts restore:** only ever touch the MonkMode marker block (`#### MonkMode Entries ####`), never the user's own hosts content.
- **Never force-push or rewrite history on the `monkmode` branch** — everything is committed and pushed as of 12/06/2026 eve; the remote history is the safety net. Don't disturb git state otherwise — no reset/checkout/rebase unless asked.
- `master` is the untouched original Cold Turkey — never work on it.
- Crypto is documented-weak by design (`Simple3Des`/`mm_textbox`, hardcoded symmetric key, no HMAC) and is **Phase-3-owned (B7)** — not a new finding; don't re-flag it.

## Working style
One slice per session · decompose across agents first (LOOP-GUIDE.md §3: how does this split?) ·
Phase 1 done; Phase 2 (threat model) deferred — don't start without asking; Phase 3 (hardening, B1–B11) is the backlog ·
end every session: write a dated handoff to `C:\Users\samra\vault\dev\repos\monk-mode\handoffs\` (newest = current state) + emit the carry-on prompt (LOOP-GUIDE.md §14).

---

## Markdown lives in the vault `dev/` zone (26/06/2026 — supersedes "repo reality wins" for working md)
All working/generated markdown for **monk-mode** now lives in the Obsidian vault, NOT in this repo:
- **Handoffs** -> `C:\Users\samra\vault\dev\repos\monk-mode\handoffs\` — newest dated file = current state (no `HANDOFF.md` in the repo anymore)
- **Tasks** -> `C:\Users\samra\vault\dev\repos\monk-mode\tasks.md`
- **Logs** `dev\repos/monk-mode\logs\` · **Specs** `dev\repos/monk-mode\specs\` · **Plans** `dev\repos/monk-mode\plans\` · **Guides** `dev\repos/monk-mode\guides\` · **Prompts** `dev\repos/monk-mode\prompts\`
End a session by writing a dated handoff `YYYY-MM-DD-HHmm-<slug>.md` to `dev\repos/monk-mode\handoffs\`. Write all of the above there, never in this repo. This repo keeps only code + `README.md` + `CLAUDE.md` + skills/agents + fixtures + product content; a few design docs that code loads by path stay here by necessity. Cheap context: vault `dev\index.md` + `ROUTER.md` route intent -> exact file.

### Every session
1. **Catch up** — read the newest file in `vault\dev\repos\monk-mode\handoffs\` first (where the last session stopped, what's next, gotchas).
2. **Log as you go** — keep a `Now / Next` line in the live handoff; substantial logs → `vault\dev\repos\monk-mode\logs\`.
3. **Hand off at the end** — write a dated `YYYY-MM-DD-HHmm-<slug>.md` to `vault\dev\repos\monk-mode\handoffs\` (status / goal / outcome / gotchas / carry-on), update `vault\dev\repos\monk-mode\tasks.md`, commit. A session without its handoff has failed its exit.

### Too large? Split it
If a task feels too big or token use is running high, **stop and propose splitting it into smaller, independently-verifiable slices** (one slice per session) before continuing — never barrel through one giant attempt.

### New session / steer from your phone
Spawn a fresh session by running `claude` in this repo dir (Samrath can just say "open a new session" → it's spawned seeded). Monitor from a phone via push notifications (`/config` → notify on finish / needs-input), read the handoffs in Obsidian mobile, and steer live cloud sessions at claude.ai/code.
