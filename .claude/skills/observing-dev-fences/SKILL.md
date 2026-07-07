---
name: observing-dev-fences
description: "Fences, safety rails, read-only discipline, git hygiene for the monk-mode repo — keeps a dev or audit session from arming or damaging the LIVE blocker. Use in EVERY monk-mode session before running, testing, committing, or touching git state; also when tempted to run the built app, clean the tree, or 'fix' something that looks wrong by design."
---

# Observing dev fences (monk-mode)

MonkMode is a tamper-resistant blocker running against the REAL machine: hosts file, HKCU Run,
a `CanStop=False` LocalSystem service. A careless dev command arms or damages the live system.

## Never-run fence
- [ ] **Never run the service or CLI during dev/audit** — it edits the LIVE hosts file, adds an
  HKCU `Run` entry, and installs a `CanStop=False` LocalSystem service (CLAUDE.md:19).
  Read-only analysis unless Samrath explicitly asks for a live test.
- [ ] Unit tests must never touch real hosts/registry/SCM — pure tests on strings/temp paths only.
- [ ] Any `--for` arming happens ONLY inside a smoke (fable5-slices.md:121), and smokes are
  human-gated because they need Administrator (vault/dev/monk-mode/tasks.md ⚙ bucket) — never
  from a dev/loop session.

## Hosts safety
- [ ] Hosts restore only ever touches the MonkMode marker block `#### MonkMode Entries ####`
  (`MonkMode/Blocker.vb:63`), NEVER the user's own hosts content (CLAUDE.md:20).

## Git fences
- [ ] Never force-push or rewrite history on `monkmode`; no reset/checkout/rebase unless asked
  (CLAUDE.md:21).
- [ ] Remote is named **`monkmode`**, NOT `origin`. **No push at all until slice H4** (the
  human-gated push slice in plans/fable5-slices.md).
- [ ] `master` = the untouched original Cold Turkey fork base — never work on it (CLAUDE.md:22).
- [ ] Dirty working-tree files = LIVE slice work in progress — never clean, stash, or checkout
  over them.

## Coordination
- [ ] Write sessions claim `coordination/ACTIVE.md` FIRST (check for an existing claim, write your
  own, clear it at session end). Read-only sessions don't claim.
- [ ] Directories containing **`-wt-`** are git worktrees belonging to parallel fix sessions —
  never enter them.

## Do-not-"fix" (documented-weak / deliberate)
- [ ] Crypto is documented-weak by design (`Simple3Des`/`mm_textbox`, hardcoded symmetric key,
  no HMAC) — Phase-3-owned (B7), not a new finding; don't re-flag it (CLAUDE.md:23).
- [ ] Tests are deliberately **C# xunit against VB code** — VB can't reference both `MonkMode`
  and `monkmode` namespaces (CLAUDE.md:16). Don't "fix" the language split.

## Environment traps
- [ ] TWO dotnets exist. Doctrine build/test uses the user-scoped
  `C:/Users/samra/.dotnet/dotnet.exe` (CLAUDE.md:15) — NOT the `C:/Program Files/dotnet/`
  one that PATH resolves to.
- [ ] Working markdown (handoffs, tasks, plans, specs, logs) lives in the vault at
  `C:/Users/samra/vault/dev/monk-mode/`, never in the repo (CLAUDE.md:32-37). The repo keeps
  only code + README + CLAUDE.md + skills/fixtures.
- [ ] Current project state = the newest dated handoff in `vault/dev/monk-mode/handoffs/` —
  read it before believing any count or "next" claim elsewhere.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from repo CLAUDE.md + vault/dev/monk-mode/ handoffs/specs.
Re-verify when repo CLAUDE.md or the newest handoff contradicts it (newest handoff wins).
