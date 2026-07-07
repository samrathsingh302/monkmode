---
name: running-loop-sessions
description: "Autonomous loop, slice sessions, LOOP_STATUS, handoff exit, WIP checkpoints, gated smokes for the monk-mode repo — how to operate one slice of the autonomous run and exit cleanly. Use when running or continuing a monk-mode loop iteration, picking the next slice, landing a loop-driver WIP checkpoint, deciding CONTINUE/DONE/BLOCKED, or closing a session with its handoff."
---

# Running loop sessions (monk-mode)

One iteration = one slice, fully gated, handed off. The loop driver owns chaining — sessions
must NOT spawn each other.

## Launch
- [ ] Model/effort: monk-mode sessions launch via the deep-mode profile launcher, per the
  MODELS block in `~/.claude/CLAUDE.md`. Deep mode only — never the default session model,
  never a mid-session model switch.

## Session shape (in order)
1. Read repo `CLAUDE.md`, then the newest dated handoff in
   `C:/Users/samra/vault/dev/monk-mode/handoffs/` (= current state).
2. Do ONE slice (per `vault/dev/monk-mode/plans/fable5-slices.md` run sheet).
3. Gate BEFORE commit:
   - [ ] Build 0-err: `C:/Users/samra/.dotnet/dotnet.exe build MonkMode.sln -c Release`
   - [ ] Full suite green: `C:/Users/samra/.dotnet/dotnet.exe test MonkMode.sln`
   - [ ] Verifier tier: enforcement-core touch = MANDATORY fresh-eyes verifier before commit
     (slice ids at plans/fable5-slices.md:121); inputs-only = light verifier.
4. Local commit only — no push, no `--no-verify`, never rewrite history.
5. Exit: dated handoff `YYYY-MM-DD-HHmm-<slug>.md` to `vault/dev/monk-mode/handoffs/` +
   update `vault/dev/monk-mode/tasks.md`. **A session without its handoff has failed its
   exit** (CLAUDE.md:42).
6. End output with `LOOP_STATUS: CONTINUE` (or DONE/BLOCKED) on the last line.

## WIP checkpoints (loop-driver auto-commits)
The driver auto-commits abandoned work as un-gated "WIP checkpoint" commits (history has
`c3b9ddf`, `58c0cdd`). On finding one at HEAD:
- [ ] Treat it as code to finish, not to trust: review the diff, then gate it (build + full
  suite + appropriate verifier).
- [ ] Land acceptance as an EMPTY MARKER COMMIT documenting the gating (precedent: `dc34f0b`
  over `c3b9ddf`, handoff 2026-07-04-2206-d1a-site-presets.md).
- [ ] NEVER reset/amend the checkpoint — even local unpushed history is history.

## Gated work and BLOCKED
- [ ] The headless loop cannot elevate or spawn. All live smokes (CV + B1c + E3 + H3) batch
  into ONE human-gated elevated sitting (E5 is a gated external review, not a live smoke —
  fable5-slices.md:80) — keep building non-gated unit-testable slices around them.
- [ ] Emit `LOOP_STATUS: BLOCKED` only when solely gated items remain (nothing left a
  headless session can do).
- [ ] `tasks.md`'s 🔴 Do-next is deliberately empty for autonomous sessions — the
  head-of-queue items need Administrator or a Samrath decision (see the ⚙ bucket).

## Stale-count trap
- [ ] Suite/smoke counts printed in older docs (`plans/fable5-slices.md`'s baseline,
  CLAUDE.md:16's smoke line) are STALE snapshots — and any count would go stale here too.
  The rule, not a number: live truth = the newest dated handoff + tasks.md:15's elevated
  smoke line; recompute counts from YOUR OWN build/test run, never quote any doc's number
  (including this skill's) as current.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from repo CLAUDE.md + vault/dev/monk-mode/ handoffs/specs.
Re-verify when repo CLAUDE.md or the newest handoff contradicts it (newest handoff wins).
