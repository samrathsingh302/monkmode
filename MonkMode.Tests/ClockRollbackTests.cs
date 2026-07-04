// MonkMode.Tests - B4 clock-rollback hardening (the monotonic high-water mark).
//
// The bypass (Critical): expiry trusted raw DateTime.Now, so rolling the system
// clock forward past [Time] Until made the next 10s tick call stopMe() and lift
// the block early (and the guardian stood down too). B4 decides expiry off a
// HIGH-WATER MARK ([Time] HighWater) that only advances at the real tick rate
// and never by a jump - so a forward clock jump can't carry it past Until.
//
// What is pure and unit-tested here (no files, registry, DPAPI or SCM - the
// classifier/next-value functions and the existing EffectiveBlockHasExpired gate
// are all filesystem-free; the live per-tick persist + MAC re-stamp is the seam,
// smoke-tested like the B1/B2/B3/B7 live wiring):
//   - Service1.ClassifyTimeAdvance / NextHighWater (the monotonic core), with the
//     fail-closed (unparseable) and boundary (exactly the ceiling) cases pinned;
//   - the headline regression: a HighWater BEHIND Until while raw DateTime.Now is
//     PAST Until must read NOT expired (the block stands - a clock-forward can't
//     lift it), and a HighWater past Until reads expired (a genuine lift still
//     works);
//   - service-copy == guardian-copy parity across the classifier (the two halves
//     of the watchdog pair must agree, mirroring GuardianTests.AgreesWithServiceCopy);
//   - the jump-ceiling const, pinned absolutely AND relative to the tick interval.

using System.Globalization;

namespace MonkMode.Tests;

public class ClassifyTimeAdvanceTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Ceiling = 120; // mirrors Service1.HighWaterJumpCeilingSeconds

    private static string T(DateTime dt) => dt.ToString(EnCa);

    [Fact]
    public void NormalTick_About10s_IsTrusted()
    {
        // The ordinary case: ~one 10s tick of forward progress is a real tick.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var now = hw.AddSeconds(10);
        Assert.Equal(monkmode.Service1.TimeAdvanceTrusted,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), T(now), Ceiling));
    }

    [Fact]
    public void ForwardByOneHour_IsForwardJump()
    {
        // The attack: the clock is shoved an hour forward. Way over the ceiling,
        // so it is a jump and must NOT be credited as elapsed time.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var now = hw.AddHours(1);
        Assert.Equal(monkmode.Service1.TimeAdvanceForwardJump,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), T(now), Ceiling));
    }

    [Fact]
    public void BackwardByThirtyMinutes_IsBackward()
    {
        // A clock rolled BACKWARD: delta < 0. Never advance on this either (the
        // mark is monotonic; a back-roll must not move it).
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var now = hw.AddMinutes(-30);
        Assert.Equal(monkmode.Service1.TimeAdvanceBackward,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), T(now), Ceiling));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("25.06.2026 17:04:33")] // legacy de-DE format - unparseable as en-CA
    [InlineData("")]
    public void UnparseableStoredHighWater_IsForwardJump_FailClosed(string storedHw)
    {
        // Fail closed: if we can't even read the stored mark we must NOT credit an
        // advance (treating it as a jump => NextHighWater keeps the old value =>
        // the mark, coupled to the MAC, never silently jumps forward).
        var now = new DateTime(2026, 6, 25, 12, 0, 0);
        Assert.Equal(monkmode.Service1.TimeAdvanceForwardJump,
            monkmode.Service1.ClassifyTimeAdvance(storedHw, T(now), Ceiling));
    }

    [Fact]
    public void UnparseableNow_IsForwardJump_FailClosed()
    {
        // The other axis: an unreadable 'now' is also a non-credit (jump).
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        Assert.Equal(monkmode.Service1.TimeAdvanceForwardJump,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), "not-a-date", Ceiling));
    }

    [Fact]
    public void ExactlyAtTheCeiling_IsTrusted_OneSecondOver_IsJump()
    {
        // Boundary: delta == ceiling is still Trusted (<=); ceiling + 1 is a jump.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        Assert.Equal(monkmode.Service1.TimeAdvanceTrusted,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), T(hw.AddSeconds(Ceiling)), Ceiling));
        Assert.Equal(monkmode.Service1.TimeAdvanceForwardJump,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), T(hw.AddSeconds(Ceiling + 1)), Ceiling));
    }

    [Fact]
    public void ZeroDelta_IsTrusted()
    {
        // Two ticks landing on the same second (delta 0) is a real, in-bounds tick.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        Assert.Equal(monkmode.Service1.TimeAdvanceTrusted,
            monkmode.Service1.ClassifyTimeAdvance(T(hw), T(hw), Ceiling));
    }
}

public class NextHighWaterTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Ceiling = 120;

    private static string T(DateTime dt) => dt.ToString(EnCa);

    [Fact]
    public void NormalTick_AdvancesToNow()
    {
        // A Trusted advance moves the mark forward to 'now'.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var now = hw.AddSeconds(10);
        Assert.Equal(T(now), monkmode.Service1.NextHighWater(T(hw), T(now), Ceiling));
    }

    [Fact]
    public void ForwardJump_DoesNotAdvance_KeepsStoredValue()
    {
        // The core monotonic property under attack: a clock-forward of an hour is
        // a jump, so the mark stays where it was - it never leaps toward Until.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var now = hw.AddHours(1);
        Assert.Equal(T(hw), monkmode.Service1.NextHighWater(T(hw), T(now), Ceiling));
    }

    [Fact]
    public void BackwardRoll_DoesNotAdvance_KeepsStoredValue()
    {
        // A back-roll never moves the mark backward (monotonic).
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var now = hw.AddMinutes(-30);
        Assert.Equal(T(hw), monkmode.Service1.NextHighWater(T(hw), T(now), Ceiling));
    }

    [Fact]
    public void UnparseableStored_KeepsStoredValueUnchanged()
    {
        // On an unparseable stored mark we return it UNCHANGED (not re-seeded to
        // now), so the value stays coupled to the MAC - a tampered HighWater
        // already fails verification and the block stands; we never quietly heal
        // a tampered mark into a fresh, MAC-passing one here.
        var now = new DateTime(2026, 6, 25, 12, 0, 0);
        Assert.Equal("garbage", monkmode.Service1.NextHighWater("garbage", T(now), Ceiling));
    }

    [Fact]
    public void IdempotentOnReplay_AStoredMarkEqualToNow_StaysPut()
    {
        // Replaying the same tick (stored == now, delta 0 = Trusted) returns now,
        // which equals the stored value: stable, no drift on a repeated tick.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var once = monkmode.Service1.NextHighWater(T(hw), T(hw), Ceiling);
        var twice = monkmode.Service1.NextHighWater(once, T(hw), Ceiling);
        Assert.Equal(T(hw), once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void RepeatedJumps_NeverMoveTheMark()
    {
        // Hammering the clock forward tick after tick never advances the mark:
        // each jump is refused, so the stored value is invariant across attempts.
        var hw = new DateTime(2026, 6, 25, 12, 0, 0);
        var stored = T(hw);
        for (int i = 1; i <= 5; i++)
        {
            var jumped = T(hw.AddHours(i)); // further and further forward
            stored = monkmode.Service1.NextHighWater(stored, jumped, Ceiling);
            Assert.Equal(T(hw), stored);
        }
    }
}

// B4 creep fix: CapHighWaterAdvance bounds the per-tick advance to REAL elapsed.
// NextHighWater alone caps each step at the 120s ceiling but not the RATE, so a
// clock nudged +119s before each 10s tick walked the mark ~12x faster than real
// time (audit-found P1). These pin the cap, incl. the regression the audit said
// the old tests structurally couldn't see (they advanced the synthetic clock at
// the honest 10s/tick cadence, never the adversarial +119s/10s one).
public class CapHighWaterAdvanceTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static string T(DateTime dt) => dt.ToString(EnCa);
    private static readonly DateTime Base = new(2026, 6, 25, 12, 0, 0);

    [Fact]
    public void HonestStep_WithinBudget_KeepsFullAdvance()
    {
        // +10s wall with 10s real elapsed: the full advance is within budget.
        var cand = Base.AddSeconds(10);
        Assert.Equal(T(cand), monkmode.Service1.CapHighWaterAdvance(T(Base), T(cand), 10));
    }

    [Fact]
    public void OverBudgetStep_IsCappedToRealElapsed()
    {
        // THE CREEP STEP: +119s wall but only 10s real elapsed => credit only 10s.
        var cand = Base.AddSeconds(119);
        Assert.Equal(T(Base.AddSeconds(10)), monkmode.Service1.CapHighWaterAdvance(T(Base), T(cand), 10));
    }

    [Fact]
    public void NoAdvance_CandidateEqualsStored_Unchanged()
    {
        // Jump/backward already kept stored => candidate == stored => no change.
        Assert.Equal(T(Base), monkmode.Service1.CapHighWaterAdvance(T(Base), T(Base), 10));
    }

    [Fact]
    public void SlowerWall_TracksWall_NotBudget()
    {
        // Wall ran slower than real (+5s wall, 10s real): credit the smaller (5s),
        // i.e. keep the candidate - never advance the mark past the wall clock.
        var cand = Base.AddSeconds(5);
        Assert.Equal(T(cand), monkmode.Service1.CapHighWaterAdvance(T(Base), T(cand), 10));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableStored_ReturnsCandidate(string stored)
    {
        var cand = Base.AddSeconds(10);
        Assert.Equal(T(cand), monkmode.Service1.CapHighWaterAdvance(stored, T(cand), 10));
    }

    [Fact]
    public void NegativeBudget_ClampsToZero_NoAdvance()
    {
        // A defensive guard: a non-positive monotonic delta credits nothing.
        var cand = Base.AddSeconds(119);
        Assert.Equal(T(Base), monkmode.Service1.CapHighWaterAdvance(T(Base), T(cand), -5));
    }

    // THE REGRESSION the audit asked for. Compose NextHighWater (Trusted => wall
    // 'now') with CapHighWaterAdvance (RealTickSeconds=10 of real elapsed per tick),
    // attacker nudging the wall +119s before each tick against a 10-MINUTE block.
    // OLD behaviour (NextHighWater alone, no cap) advanced the mark ~+119s/tick and
    // would cross the 600s Until in ~6 ticks => early lift. With the cap the mark
    // advances at most the real elapsed (and in fact freezes once the racing wall
    // gets >ceiling ahead), so it never approaches Until.
    [Fact]
    public void Creep_CannotOutrunRealTime_BlockDoesNotLiftEarly()
    {
        const int Ticks = 10;
        const int CreepStep = 119;   // attacker's per-tick wall nudge (<= ceiling, so each is "Trusted")
        const int RealTick = 10;     // real seconds of monotonic elapsed per tick
        var until = Base.AddMinutes(10);   // 600s block
        var mark = T(Base);                // HighWater seeded at arm
        var wall = Base;
        for (int i = 0; i < Ticks; i++)
        {
            wall = wall.AddSeconds(CreepStep);                                   // attacker nudges the clock
            var candidate = monkmode.Service1.NextHighWater(mark, T(wall), 120); // Trusted => wall 'now'
            mark = monkmode.Service1.CapHighWaterAdvance(mark, candidate, RealTick); // capped to real elapsed
        }
        var marked = DateTime.Parse(mark, EnCa);
        // Security property: the mark never reaches Until, so the block does NOT lift early.
        Assert.True(marked < until, $"mark {marked:O} must stay below Until {until:O}");
        // Rate bound: the total advance can never exceed the summed real elapsed
        // (Ticks * RealTick), no matter how far the wall was nudged.
        Assert.True((marked - Base).TotalSeconds <= Ticks * RealTick,
            $"mark advanced {(marked - Base).TotalSeconds}s; must be <= {Ticks * RealTick}s of real elapsed");
        // Sanity that the attack WOULD work without the cap: an uncapped run
        // (NextHighWater alone) advances ~CreepStep/tick and crosses Until here.
        Assert.True(Base.AddSeconds(Ticks * CreepStep) > until,
            "test params must be such that the uncapped creep would cross Until");
    }
}

// B1 backward-clock fix: AdvanceHighWater credits the REAL monotonic elapsed on a
// non-Trusted tick (backward roll / forward jump) instead of FREEZING the mark, so
// an active block ends at its real duration rather than over-running by the rollback
// (the Codex residual P2). The Trusted path is byte-identical to the old NextHighWater
// -> CapHighWaterAdvance composition. The safety invariant is preserved exactly:
// per-tick credit <= real monotonic elapsed, so the block can never lift early.
public class AdvanceHighWaterTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static string T(DateTime dt) => dt.ToString(EnCa);
    private static readonly DateTime Base = new(2026, 6, 25, 12, 0, 0);
    private const long Ceiling = 120; // mirrors Service1.HighWaterJumpCeilingSeconds

    [Fact]
    public void BackwardRoll_CreditsRealElapsed_NotFrozen()
    {
        // THE headline. A wall rolled back 30 min used to FREEZE the mark (credit 0),
        // over-running the block by the rollback. Now it credits the real ~10s elapsed.
        var wall = T(Base.AddMinutes(-30)); // backward roll
        Assert.Equal(T(Base.AddSeconds(10)),
            monkmode.Service1.AdvanceHighWater(T(Base), wall, 10, Ceiling));
    }

    [Fact]
    public void ForwardJump_CreditsRealElapsed_NotTheJump()
    {
        // A +1h forward jump credits the real ~10s elapsed - NOT the hour (which would
        // lift the block early) and NOT 0 (which would freeze/over-run). Just mono.
        var wall = T(Base.AddHours(1)); // forward jump (> ceiling)
        Assert.Equal(T(Base.AddSeconds(10)),
            monkmode.Service1.AdvanceHighWater(T(Base), wall, 10, Ceiling));
    }

    [Fact]
    public void Trusted_IsByteIdenticalToOldComposition()
    {
        // The honest path must be UNCHANGED: AdvanceHighWater must equal the exact old
        // two-call composition NextHighWater -> CapHighWaterAdvance. Covers a normal
        // +10s tick, a +119s within-ceiling creep step, AND the subtle case the design
        // flagged - a Trusted tick with mono > ceiling, where AdvanceHighWater passes the
        // CLAMPED budget (120) but the old code passed the raw mono: they must STILL
        // agree, because a Trusted wall delta is <= ceiling so the mono term is always
        // dominated by wallDelta (min(wallDelta, X) == min(wallDelta, 120) when X >= 120).
        foreach (var (wallDelta, mono) in new[] { (10, 10L), (119, 10L), (119, 200L), (10, 500L) })
        {
            var now = T(Base.AddSeconds(wallDelta));
            var expected = monkmode.Service1.CapHighWaterAdvance(
                T(Base), monkmode.Service1.NextHighWater(T(Base), now, Ceiling), mono);
            Assert.Equal(expected, monkmode.Service1.AdvanceHighWater(T(Base), now, mono, Ceiling));
        }
    }

    [Fact]
    public void MonoClampedToCeiling_SleepCannotOverCredit()
    {
        // D1: a pathological monotonic delta (e.g. TickCount64 counting a long sleep)
        // credits AT MOST one ceiling per tick on a non-Trusted tick - never more, so a
        // resume-from-sleep can neither lift the block nor jump the mark unbounded.
        var wall = T(Base.AddMinutes(-30)); // non-Trusted (backward)
        Assert.Equal(T(Base.AddSeconds(Ceiling)),
            monkmode.Service1.AdvanceHighWater(T(Base), wall, 100000, Ceiling));
    }

    [Fact]
    public void NegativeOrZeroMono_CreditsNothing()
    {
        // No real elapsed (mono <= 0) credits nothing even on a non-Trusted tick: the
        // mark holds where it was. Covers a degenerate/defensive anchor read.
        var wall = T(Base.AddMinutes(-30)); // backward
        Assert.Equal(T(Base), monkmode.Service1.AdvanceHighWater(T(Base), wall, 0, Ceiling));
        Assert.Equal(T(Base), monkmode.Service1.AdvanceHighWater(T(Base), wall, -5, Ceiling));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableStored_ReturnsUnchanged_FailClosed(string stored)
    {
        // Fail-closed: an unreadable stored mark is returned UNCHANGED (never re-seeded
        // to a fresh MAC-shaped value), so it stays coupled to the failing MAC and the
        // block holds. Mirrors NextHighWater / CapHighWaterAdvance.
        Assert.Equal(stored, monkmode.Service1.AdvanceHighWater(stored, T(Base), 10, Ceiling));
    }
}

public class ClockRollbackRegressionTests
{
    // THE headline B4 test: the clock-forward bypass, pinned shut. Drives the
    // existing MAC-aware expiry gate with the HIGH-WATER MARK as asOf (B4's only
    // change to that gate's caller) and proves a rolled-forward clock can't lift
    // the block, while a genuine expiry still does.
    private static readonly CultureInfo EnCa = new("en-CA");

    [Fact]
    public void HighWaterBehindUntil_EvenWhenRawNowIsPastUntil_BlockStands()
    {
        // The attack made concrete: Until is 30 min out; the user rolls the clock
        // an hour forward, so raw DateTime.Now would be PAST Until. But the high-
        // water mark only advanced to the real, pre-roll time (still BEHIND Until,
        // because the jump was refused), so expiry measured against it is FALSE:
        // the block stands. Driving the SAME gate with raw 'now' instead would lift
        // it - that contrast is the whole bug, so we assert both directions.
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var highWater = until.AddMinutes(-30);   // mark legitimately reached 16:30
        var rolledNow = until.AddHours(1);        // attacker's clock now reads 18:00

        // B4 (the fix): asOf = highWater => not expired => block stands.
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), highWater, 5, macValid: true));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(until.ToString(EnCa), highWater, 5, macValid: true));

        // The pre-B4 behaviour the bug relied on (asOf = raw rolled clock) WOULD
        // have lifted it - pinned here so a regression that reverts to DateTime.Now
        // is caught as the contrast it is.
        Assert.True(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), rolledNow, 5, macValid: true));
    }

    [Fact]
    public void HighWaterPastUntil_IsExpired_GenuineLiftStillWorks()
    {
        // The other half: once the mark genuinely advances past Until (the block
        // really ran to completion on-machine), expiry is TRUE and the block lifts
        // as designed. B4 hardens against early lift; it must not break real expiry.
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var highWater = until.AddMinutes(1); // the mark legitimately passed the end
        Assert.True(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), highWater, 5, macValid: true));
        Assert.True(mm_guard.Guardian.EffectiveBlockHasExpired(until.ToString(EnCa), highWater, 5, macValid: true));
    }

    [Fact]
    public void EndToEnd_AdvanceThenClassifyExpiry_AcrossTrustedTicksOnly()
    {
        // Walk the actual flow: start the mark at "now", advance it over several
        // Trusted 10s ticks, then on each step decide expiry against Until using
        // the advanced mark. It stays NOT expired until the mark itself reaches
        // within the 5s grace of Until - i.e. only real elapsed ticks expire it.
        var until = new DateTime(2026, 6, 25, 12, 1, 0); // 60s out
        var clock = new DateTime(2026, 6, 25, 12, 0, 0);
        var mark = clock.ToString(EnCa);

        for (int tick = 1; tick <= 6; tick++)
        {
            clock = clock.AddSeconds(10);
            mark = monkmode.Service1.NextHighWater(mark, clock.ToString(EnCa), 120);
            bool expired = monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), DateTime.Parse(mark, EnCa), 5, macValid: true);
            // Ticks land at :10 .. :60. Only tick 6 (mark 12:01:00, 0s left) is
            // within the 5s grace of the 12:01:00 end; ticks 1-5 (>=10s left)
            // are not. The mark advances exactly one 10s tick each time.
            Assert.Equal(tick >= 6, expired);
        }
    }

    [Fact]
    public void InvalidMac_StillForcesActive_EvenWithHighWaterPastUntil()
    {
        // B4 layers on B7: even a mark past Until must NOT lift the block if the
        // config MAC is invalid (a tampered ini). The two fail-closed axes stack.
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var highWater = until.AddMinutes(1);
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), highWater, 5, macValid: false));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(until.ToString(EnCa), highWater, 5, macValid: false));
    }

    [Fact]
    public void BackwardRoll_MarkClimbsAtRealRate_EndsOnTime_NeverEarly()
    {
        // B1 (the P2 fix), driven end to end. A 10-min block; the wall is held 30 min
        // BEHIND real time every tick (so every tick classifies Backward), with 10s of
        // real monotonic elapsed per tick. The OLD code froze the mark on every backward
        // tick, so the block over-ran by ~30 min; B1 credits the real elapsed, so the
        // mark climbs ~10s/tick and the block ends at its REAL 600s duration - and NEVER
        // before it (cumulative advance <= real elapsed so far: the safety invariant).
        var arm = new DateTime(2026, 6, 25, 12, 0, 0);
        var until = arm.AddMinutes(10);          // 600s block
        var mark = arm.ToString(EnCa);           // HighWater seeded at arm
        const int RealTick = 10;                 // real seconds elapsed per tick
        int ticks = 600 / RealTick;              // 60 ticks == the real duration

        var trueClock = arm;
        for (int i = 1; i <= ticks; i++)
        {
            trueClock = trueClock.AddSeconds(RealTick);
            var wall = trueClock.AddMinutes(-30).ToString(EnCa); // held 30 min behind => Backward
            var before = DateTime.Parse(mark, EnCa);
            mark = monkmode.Service1.AdvanceHighWater(mark, wall, RealTick, 120);
            var after = DateTime.Parse(mark, EnCa);

            // (a) not frozen: the mark advances every backward tick (old code stalled it).
            Assert.True(after > before, $"tick {i}: mark must advance, was frozen at {before:O}");
            // (c) never early: cumulative advance <= real elapsed so far (the invariant).
            Assert.True((after - arm).TotalSeconds <= i * RealTick,
                $"tick {i}: advance {(after - arm).TotalSeconds}s must be <= {i * RealTick}s real elapsed");
        }

        // (b) ends on real time: after exactly duration/tick ticks the mark reaches Until
        // (no over-run), and expiry against the mark now reads TRUE (lifts on time),
        // whereas the pre-B1 freeze would have left it ~30 min short here.
        var final = DateTime.Parse(mark, EnCa);
        Assert.Equal(until, final);
        Assert.True(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), final, 5, macValid: true));
    }

    [Fact]
    public void MixedWallDirections_CreditNeverExceedsRealElapsed()
    {
        // Adversarial interleave of backward, forward-jump and trusted ticks, 10s of
        // real elapsed each. The safety invariant must hold at EVERY step: cumulative
        // advance <= sum of real elapsed (so early-lift is impossible) - AND the mark
        // must still advance on the non-Trusted ticks (B1: no freeze/over-block), so the
        // total beats what the OLD freeze-on-non-Trusted code could reach.
        var arm = new DateTime(2026, 6, 25, 12, 0, 0);
        var mark = arm.ToString(EnCa);
        const long Mono = 10;

        // (kind label, wall delta from the CURRENT mark). Trusted deltas are within [0,120].
        var steps = new (string kind, int delta)[]
        {
            ("trusted", 10),        // credit 10 (full mono)
            ("backward", -1800),    // credit 10 (B1 fix; old froze)
            ("forwardjump", 3600),  // credit 10 (B1 fix; old froze)
            ("trusted", 5),         // credit 5  (honest wall ran slower than real)
            ("backward", -1800),    // credit 10 (B1 fix)
            ("trusted", 10),        // credit 10
        };

        long sumMono = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            sumMono += Mono;
            var cur = DateTime.Parse(mark, EnCa);
            var wall = cur.AddSeconds(steps[i].delta).ToString(EnCa);
            mark = monkmode.Service1.AdvanceHighWater(mark, wall, Mono, 120);
            var adv = (DateTime.Parse(mark, EnCa) - arm).TotalSeconds;
            // Invariant: cumulative advance never exceeds the real elapsed so far.
            Assert.True(adv <= sumMono, $"step {i} ({steps[i].kind}): advance {adv}s must be <= {sumMono}s real elapsed");
        }

        var total = (DateTime.Parse(mark, EnCa) - arm).TotalSeconds;
        // Exact expected credit: 10+10+10+5+10+10 = 55 (the +5 trusted tick credits only
        // the 5s the wall actually moved; every other tick credits the full 10s mono).
        Assert.Equal(arm.AddSeconds(55), DateTime.Parse(mark, EnCa));
        // Ceiling: never exceeds total real elapsed (60s). Floor: strictly beats the OLD
        // freeze-on-non-Trusted behaviour (which credited only the 3 trusted ticks =
        // 10+5+10 = 25s), proving the backward/forward ticks are now credited, not frozen.
        Assert.True(total <= sumMono, $"total {total}s must be <= {sumMono}s real elapsed");
        Assert.True(total > 25, $"total {total}s must beat the old freeze floor of 25s");
    }
}

public class HighWaterParityTests
{
    // The two halves of the watchdog pair carry byte-identical B4 gates; pin that
    // the service copy and the guardian copy agree on the classification for the
    // same inputs (mirrors GuardianTests.AgreesWithServiceCopy for BlockHasExpired).
    // If they ever drifted, the service could advance/refuse a mark differently
    // from how the guardian reasons about it.
    private static readonly CultureInfo EnCa = new("en-CA");
    private static string T(DateTime dt) => dt.ToString(EnCa);

    public static readonly TheoryData<string, string> Cases = new()
    {
        // storedHw, now (en-CA), exercising trusted / jump / backward / unparseable
        { T(new DateTime(2026, 6, 25, 12, 0, 0)), T(new DateTime(2026, 6, 25, 12, 0, 10)) }, // +10s trusted
        { T(new DateTime(2026, 6, 25, 12, 0, 0)), T(new DateTime(2026, 6, 25, 13, 0, 0)) },  // +1h jump
        { T(new DateTime(2026, 6, 25, 12, 0, 0)), T(new DateTime(2026, 6, 25, 11, 30, 0)) }, // -30m backward
        { T(new DateTime(2026, 6, 25, 12, 0, 0)), T(new DateTime(2026, 6, 25, 12, 2, 0)) },  // +120s exactly the ceiling
        { T(new DateTime(2026, 6, 25, 12, 0, 0)), T(new DateTime(2026, 6, 25, 12, 2, 1)) },  // +121s just over
        { "garbage", T(new DateTime(2026, 6, 25, 12, 0, 0)) },                                // unparseable stored
        { T(new DateTime(2026, 6, 25, 12, 0, 0)), "garbage" },                                // unparseable now
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClassifyTimeAdvance_ServiceAndGuardian_Agree(string storedHw, string now)
    {
        Assert.Equal(
            monkmode.Service1.ClassifyTimeAdvance(storedHw, now, 120),
            mm_guard.Guardian.ClassifyTimeAdvance(storedHw, now, 120));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NextHighWater_ServiceAndGuardian_Agree(string storedHw, string now)
    {
        Assert.Equal(
            monkmode.Service1.NextHighWater(storedHw, now, 120),
            mm_guard.Guardian.NextHighWater(storedHw, now, 120));
    }

    [Fact]
    public void TheClassifierResultConstants_Agree()
    {
        // The named result codes must match across the two copies, or a "Trusted"
        // on one side could be read as something else on the other.
        Assert.Equal(monkmode.Service1.TimeAdvanceBackward, mm_guard.Guardian.TimeAdvanceBackward);
        Assert.Equal(monkmode.Service1.TimeAdvanceTrusted, mm_guard.Guardian.TimeAdvanceTrusted);
        Assert.Equal(monkmode.Service1.TimeAdvanceForwardJump, mm_guard.Guardian.TimeAdvanceForwardJump);
    }
}

public class HighWaterCeilingTests
{
    // The jump ceiling is the single knob separating "a real (possibly late) tick"
    // from "a deliberate clock-forward". Pin it like the recovery-policy and
    // SafeBoot consts: a drift that lowered it near the tick interval would start
    // refusing legitimate ticks (the block would never advance / never expire); a
    // huge value would let a clock-forward of that many seconds through.
    [Fact]
    public void Ceiling_IsTheExpectedAbsoluteValue()
    {
        Assert.Equal(120L, monkmode.Service1.HighWaterJumpCeilingSeconds);
    }

    [Fact]
    public void Ceiling_AgreesAcrossServiceAndGuardian()
    {
        Assert.Equal(monkmode.Service1.HighWaterJumpCeilingSeconds, mm_guard.Guardian.HighWaterJumpCeilingSeconds);
    }

    [Fact]
    public void Ceiling_IsComfortablyAboveTheTickInterval()
    {
        // Must be >> the 10s tick so an ordinary slightly-late tick is always
        // Trusted. 120s = 12 ticks of slack; assert it is at least several ticks.
        Assert.True(monkmode.Service1.HighWaterJumpCeilingSeconds >= 3L * (monkmode.Service1.TimerIntervalMs / 1000));
    }
}
