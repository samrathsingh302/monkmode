# 2026-06-16 — Morning fix run: Cold-Turkey-Serious atomic hosts + fail-OPEN re-assert

Cross-repo morning-fix run. Branch `fix/morning-2026-06-16` (off `2a775ff`).
All fixes verified and committed locally. **NOT pushed — push is Samrath's gate.**

## What was fixed

### C1 — atomic hosts writes · commit `e796f19`
The four hosts-file writers each did a non-atomic write, so a crash mid-write could
truncate or blank the system `hosts` file (data-loss / fail-open of the block).
Fix: one shared `AtomicHosts.WriteAtomic` helper — write to a temp file, then
`File.Move` over the target, with an **8 × 25 ms retry** and **fail-closed** on
exhaustion. Routed through all four writers: `Blocker.vb:242`, `Blocker.vb:493`,
`Service1.vb:401`, `Service1.vb:1091`. +5 tests.

### #2 — `adder_Changed` fail-OPEN closed · commit `df1b66b`
`adder_Changed` could leave `hosts` writable after an exception. Fix: a
`Try/Catch/Finally` that re-asserts the hosts file read-only in the `Finally`, so the
block is restored even on the error path.

### CI — Node-24 actions · commit `fd3b16d`
Bumped the deprecated Node-20 GitHub actions to Node-24: `checkout@v5` and
`setup-dotnet@v5`.

## Verification record
- **Tests 281 → 286 green** (+5, the C1 atomic-write suite).
- **Fresh-Opus verifier verdict: SHIP** — every data-loss / fail-closed hunt passed
  (atomic-write contract holds across all four writers; the fail-OPEN re-assert path
  restores read-only on exception).

## Codex debt (Codex was OUT this session)
Per Samrath's standing call, Codex review is deferred. Re-run once credits are topped up:

```
codex review --base 2a775ff
```

## Not done / needs Samrath
- **FLAGGED, NOT implemented:** no `AppDomain.UnhandledException` backstop. This is a
  process-wide change (catch-all on the unhandled-exception path to re-assert the block
  before the process dies) — **scope it as its own slice**, not a drive-by here.
- **N1** — a 5th writer at `Service1.vb:1170` uses `AppendAllText` (append-mode, so it
  cannot blank the hosts file). Left as-is; capture as a future ticket.
- **N2** — no deterministic test for the *persistent* `File.Move`-failure path (where all
  8 retries are exhausted). The retry/fail-closed logic exists but the always-fails branch
  is not exercised by a test.

## Gates
- **PUSH** `fix/morning-2026-06-16` — Samrath's gate (LOCAL-ONLY, not pushed).
- Pre-existing uncommitted edits (`CLAUDE.md`, `HANDOFF.md`,
  `docs/VERIFY-AND-CLOSEOUT-2026-06-14.md`, `HANDOFF.atlas-old-2026-06-16.md`) were left
  untouched — they predate this run.
