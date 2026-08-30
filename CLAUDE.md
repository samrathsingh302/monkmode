# MonkMode — Claude Code instructions
**Read order, every session:** 1) this file · 2) current state = newest dated handoff in `C:\Users\samra\OneDrive\dev\repos\monk-mode\handoffs\` (always — gotchas; handoffs moved to the vault 26/06/2026, no repo HANDOFF.md) + `C:\Users\samra\OneDrive\dev\repos\monk-mode\specs\ARCHITECTURE.md` (bypass surface B1–B11) + README
House doctrine, defaults and the session ritual load from the global `~/.claude/CLAUDE.md` — not restated here (trimmed 05/08/2026).

## What this is
A personal, tamper-resistant website/app self-control blocker for Windows — Samrath's own fork of Cold Turkey (GPLv3), rebranded and being hardened on his own machine. Goal: once a block starts it can't be casually removed before its timer expires. Done = defeat casual→determined bypasses (B1–B9); B10 offline/admin is honestly out of scope.

## Stack & layout
- VB.NET / **.NET 10 LTS** (`net10.0-windows`; MM_notify + Tests carry the `10.0.17763.0` platform suffix — retargeted from .NET 8 by v1.1 slice S0b, 12/08/2026; SDK 10.0.400 installed user-scoped beside 8.0.422; support ends 11/2028), SDK-style projects. Solution: `MonkMode.sln` (4 projects + `MonkMode.Tests`). No GUI — it's a CLI.
- `MonkMode/` → `monkmode.exe` (CLI: writes hosts + config, installs/starts the service + SCM recovery, registers the notifier).
- `MonkMode_srv/` → `MonkMode_srv.exe` (**LocalSystem service `MONKMODE`**, `CanStop=False`, 10s timer — the enforcement core; inherited logic preserved, hardened via tested fail-closed gates: B2 hosts self-heal, B1 guardian spawn, B3 SafeBoot self-register).
- `MM_notify/` → `mm_notify.exe` (user-session notifier: app-kill, clock-change comp, tray-toast at expiry).
- `MM_guard/` → `mm_guard.exe` (SYSTEM-session watchdog guardian spawned by the service: SCM-restarts a killed service, relaunches the notifier; exits only on genuine expiry).
- Build: `C:\Users\samra\.dotnet\dotnet.exe build MonkMode.sln -c Release` (SDK is user-scoped, not on PATH).
- Tests: `MonkMode.Tests/` (xunit, C# — VB can't reference both `MonkMode` and `monkmode` namespaces); run `C:\Users\samra\.dotnet\dotnet.exe test MonkMode.sln`. Pure unit tests on strings/temp paths only. Live-path verification is the manual elevated smoke test (last run **30/08/2026 on build 4e9983e: run-smoketest 85/0 (incl. clock), cv-d 68/0, b7 24/0, fx6 29/0, clock 25/0 — all via partner-code lifts, `tools\smoke\_lib.ps1`** (previous baseline 63/63, 14/06/2026) — B1 + B2 + B4 + B6 + B7 live-verified, B3 registration live-verified; the B3 in-Safe-Mode run was not reboot-tested, by choice; rebuild `dist\` first via `tools\build-dist.ps1`, see `C:\Users\samra\OneDrive\dev\repos\monk-mode\specs\ARCHITECTURE.md` §4).

## Fences
- **Never run the service / CLI during dev or audit** — it edits the LIVE hosts file, adds an HKCU `Run` entry, and installs a `CanStop=False` LocalSystem service. Read-only analysis unless Samrath explicitly asks for a live test. Unit tests must never touch real hosts/registry/SCM either.
- **No data loss on hosts restore:** only ever touch the MonkMode marker block (`#### MonkMode Entries ####`), never the user's own hosts content.
- **Never force-push or rewrite history on the `monkmode` branch** — everything is committed and pushed as of 12/06/2026 eve; the remote history is the safety net. Don't disturb git state otherwise — no reset/checkout/rebase unless asked.
- The untouched original Cold Turkey survives as the root commit of `monkmode` history (`c0838c4` "0.6 Serious") — there is no `master` branch any more; never rewrite history beneath it.
- Crypto is documented-weak by design (`Simple3Des`/`mm_textbox`, hardcoded symmetric key, no HMAC) and is **Phase-3-owned (B7)** — not a new finding; don't re-flag it.

## Working style
One slice per session · Phase 1 done; Phase 2 (threat model) deferred — don't start without asking; Phase 3 (hardening, B1–B11) is the backlog.

---

## Markdown lives in the dev zone, never this repo
All working md → `C:\Users\samra\OneDrive\dev\repos\monk-mode\` (`handoffs\` newest dated file = current state · `tasks.md` · `logs\` `specs\` `plans\` `guides\` `prompts\`). This repo keeps only code + `README.md` + `CLAUDE.md` + skills/agents + fixtures + product content. Session ritual (catch-up, handoff at close, split-when-large, seeded spawns) = the global contract in `~/.claude/CLAUDE.md` (trimmed 05/08/2026; ROUTER retired).
