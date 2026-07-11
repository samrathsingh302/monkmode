---
name: advancing-monotonic-highwater
description: "HighWater, monotonic clock, clock rollback, clock creep, TickCount64, AdvanceHighWater, ClassifyTimeAdvance, time defence, B4, B1b, early lift — maintains monk-mode's clock-defence core where per-tick HighWater credit never exceeds real monotonic elapsed time. Use when touching HighWater advancement, expiry timing, clock-change handling, tick cadence, or anything in monk-mode that reads or persists [Time] HighWater/Now/Until."
---

# Advancing the monotonic HighWater

The invariant: **cumulative HighWater advance ≤ real monotonic elapsed since arm** ⇒ a block can NEVER lift before its real duration. Every tick credits at most the real time that actually passed (`Environment.TickCount64`, which the wall clock cannot move). Wall-clock direction is irrelevant; only real elapsed is ever credited.

## The one entry point
- [ ] `AdvanceHighWater(storedHwText, wallNowText, monoElapsedSeconds, ceilingSeconds)` — MonkMode_srv/MonkMode_srv/Service1.vb:2229; SINGLE call-site at :839 (the timer tick). Do not add a second call-site or a parallel advancement path.
- [ ] Trusted branch (:2247-2249) is byte-identical to the old `NextHighWater` + `CapHighWaterAdvance` composition — the honest path is provably unchanged.
- [ ] Backward/ForwardJump branch (:2250-2253) credits the CLAMPED monotonic elapsed (budget ∈ [0, ceiling]) instead of freezing — the B1b fix for the P2 over-run residual (ARCHITECTURE.md:174-180; a backward roll used to freeze the mark until the wall caught up).
- [ ] The ceiling clamp on budget (:2242-2244) bounds a resume-after-sleep/hibernate tick to one ceiling — never an unbounded jump, never a lift.

## The helpers (pure, Shared, unit-pinned)
- [ ] `ClassifyTimeAdvance` — Service1.vb:2157: Backward (delta<0) / Trusted (0..ceiling) / ForwardJump (>ceiling). Unparseable stored or now text ⇒ ForwardJump (never credit an advance you can't measure).
- [ ] Ceiling: `HighWaterJumpCeilingSeconds = 120` — Service1.vb:2149 (guardian parity copy MM_guard/MM_guard/Guardian.vb:203).
- [ ] `CapHighWaterAdvance` — Service1.vb:2197: per-tick credit = min(wallDelta, monoElapsed). Closes the B4 creep bug (+119s wall nudge before each 10s tick walked the mark ~12x honest speed).
- [ ] `monoElapsedSeconds` comes from `Environment.TickCount64` deltas captured per tick (:815-818) — the same interval as the wall anchor, so wall and mono deltas span the same tick.

## Fail-safe couplings — preserve verbatim
- [ ] Unparseable/tampered stored mark ⇒ returned UNCHANGED (:2232-2236) — never re-seeded to now. It stays coupled to the already-failing B7 MAC ⇒ `newHwAsOf` → MinValue ⇒ block holds. Never fabricate a fresh, MAC-shaped value.
- [ ] `OnStart` credits the boot gap NOT AT ALL — there is no monotonic anchor across a service restart (ARCHITECTURE.md B4 row, :187). Expected behaviour, not a bug: restarts only ever over-block.
- [ ] Every expiry/self-heal decision takes `asOf = newHwAsOf` (the parsed advanced mark), never raw `DateTime.Now`.

## The CLI seam
- [ ] `Blocker.BlockGenuinelyExpired(macValid, untilText, highWaterText)` — MonkMode/Blocker.vb:169-175: expired ONLY when MAC valid AND `Until <= HighWater`. Fail-closed: invalid MAC or unparseable Until/HighWater ⇒ NOT expired.
- [ ] `BlockIsActive` (Blocker.vb:177-204) decides "is a block standing?" off the persisted HighWater + MAC — never raw `DateTime.Now`. The old wall-clock check let a clock-forward make `block --for 1m` overwrite a standing block. Any new CLI liveness check must reuse this seam.

## Never reintroduce Until-rewriting
- [ ] The notifier's clock-change compensation (`ComputeCompensatedUntil`) was REMOVED 15/06/2026 after causing a REAL early lift — it rewrote `[Time] Until` into the past after a backward clock jiggle (ARCHITECTURE.md:159-172).
- [ ] `SystemEvents_TimeChanged` now only toggles the `TimeChanging` cooperation flag. B4's monotonic HighWater already ends the block after the correct real duration across any clock change — Until-rewriting is redundant AND harmful. Reject any proposal to "compensate" Until on clock change.

## Change checklist
- [ ] Any new credit path: prove per-tick credit ≤ clamped real mono elapsed, in a comment AND a test.
- [ ] Trusted-branch edits: show byte-identical output to the shipped composition, or treat as an enforcement-core slice with a verifier.
- [ ] Never let a helper return a parseable value from an unparseable input (breaks the MAC coupling).
- [ ] Tests live in MonkMode.Tests/ClockRollbackTests.cs (plus the creep/heartbeat pins in sibling test files) — extend them, never weaken assertions.
- [ ] The B1c live backward-clock drill is still PENDING in the gated smoke batch (vault/dev/repos/monk-mode/plans/fable5-slices.md:39, :118) — the backward-roll fix is unit-pinned but not yet live-drilled; do not claim it live-verified.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from Service1.vb, Blocker.vb, Guardian.vb, vault/dev/repos/monk-mode/specs/ARCHITECTURE.md and the vault handoffs (B1a/B1b handoffs of 04/07/2026). Line numbers pinned to baseline commit `dc34f0b` (dirty D1b tree for Blocker.vb — cited as-is); the 06-07/07/2026 fix-branch merges shift Service1.vb lines — re-locate symbols by NAME after those land. Re-verify every citation against ARCHITECTURE.md's bypass table and the newest handoff in vault/dev/repos/monk-mode/handoffs/ when they change — newest dated evidence wins.
