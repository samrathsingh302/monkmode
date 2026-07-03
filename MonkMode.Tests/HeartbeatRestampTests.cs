// MonkMode.Tests - B7 fail-open regression: the heartbeat MAC re-stamp gate.
//
// THE BUG (found 2026-06-13, verifier-confirmed P0): the service's active-block
// heartbeat used to re-stamp [Integrity] Mac UNCONDITIONALLY in the "not expired"
// branch. So a plain [Time] Until edit (the 3DES key is known by design; only the
// HMAC is meant to stop it) was detected on tick N (macValid=False, block held)
// but RE-BLESSED with a fresh valid MAC the same tick, and lifted the block on
// tick N+1 - exactly the bypass B7 claims to close, in ~2 ticks, no HMAC forge
// and no clock change.
//
// THE FIX: route the heartbeat through the pure Service1.ClassifyHeartbeat gate:
//   - Lift  ONLY when macValid AND the block genuinely expired (== the unchanged
//           EffectiveBlockHasExpired lift condition),
//   - Restamp ONLY when macValid (the service's own Now/HighWater writes are
//           MAC-covered, so a legit config must be re-stamped or it goes stale),
//   - Hold  when the MAC is invalid: NEVER re-stamp over an unverified config
//           (that was the bug) and never lift -> the block stays frozen.
//
// The keystone is HeartbeatForTamperedExpiredConfig_IsHold: under the OLD code
// that case re-stamped (the hole); it must now be Hold. Pure gate, no I/O.

using Hb = monkmode.Service1.HeartbeatAction;

namespace MonkMode.Tests;

public class ClassifyHeartbeatTests
{
    // C2b widened ClassifyHeartbeat with a coolOffElapsed arm; C3b widened it again
    // with a codeUnlocked arm (a partner-verified exit is the THIRD lift trigger,
    // converging on the same stopMe()). The original B7 truth table is preserved
    // below with coolOffElapsed: false, codeUnlocked: false; the cooling-off and
    // partner-code cases are pinned after it.

    [Fact]
    public void ValidMac_Expired_Lifts()
    {
        Assert.Equal(Hb.Lift, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: true, coolOffElapsed: false, codeUnlocked: false, scheduleActive: false));
    }

    [Fact]
    public void ValidMac_NotExpired_Restamps()
    {
        Assert.Equal(Hb.Restamp, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: false, coolOffElapsed: false, codeUnlocked: false, scheduleActive: false));
    }

    // THE REGRESSION. A tampered/invalid MAC over a (back-dated) past Until must
    // HOLD - the old code re-stamped here, re-blessing the tamper and lifting the
    // block next tick. This is the single assertion that would have caught the P0.
    [Fact]
    public void HeartbeatForTamperedExpiredConfig_IsHold()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: true, coolOffElapsed: false, codeUnlocked: false, scheduleActive: false));
    }

    [Fact]
    public void InvalidMac_NotExpired_Holds()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: false, coolOffElapsed: false, codeUnlocked: false, scheduleActive: false));
    }

    // C2b: a completed cooling-off lifts - but ONLY under a valid MAC.
    [Fact]
    public void ValidMac_CoolOffElapsed_Lifts()
    {
        Assert.Equal(Hb.Lift, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: false, coolOffElapsed: true, codeUnlocked: false, scheduleActive: false));
    }

    // C3b: a partner-verified code-unlock lifts - but ONLY under a valid MAC.
    [Fact]
    public void ValidMac_CodeUnlocked_Lifts()
    {
        Assert.Equal(Hb.Lift, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: false, coolOffElapsed: false, codeUnlocked: true, scheduleActive: false));
    }

    // C2b KEYSTONE: a tampered config can never cool off its way out. Even with
    // an "elapsed" cooling-off deadline, an invalid MAC HOLDS - never lifts,
    // never re-stamps. (The cooling-off analogue of the B7 heartbeat P0.)
    [Fact]
    public void InvalidMac_EvenWithCoolOffElapsed_Holds()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: false, coolOffElapsed: true, codeUnlocked: false, scheduleActive: false));
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: true, coolOffElapsed: true, codeUnlocked: false, scheduleActive: false));
    }

    // C3b KEYSTONE (R6): a tampered config can never code-unlock its way out. Even
    // with a (forged) codeUnlocked, an invalid MAC HOLDS - never lifts, never
    // re-stamps. This is what makes "tampered hash = no code valid = freeze".
    [Fact]
    public void InvalidMac_EvenWithCodeUnlocked_Holds()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: false, coolOffElapsed: false, codeUnlocked: true, scheduleActive: false));
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: true, coolOffElapsed: true, codeUnlocked: true, scheduleActive: false));
    }

    // A pending-but-not-elapsed cooling-off with no code-unlock keeps re-stamping:
    // the Restamp arm is what keeps HighWater advancing, which is exactly what makes
    // the cooling-off COUNT DOWN.
    [Fact]
    public void ValidMac_NothingDue_Restamps()
    {
        Assert.Equal(Hb.Restamp, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: false, coolOffElapsed: false, codeUnlocked: false, scheduleActive: false));
    }

    // C5b KEYSTONE (SD1): an OPEN scheduled window OUT-RANKS every lift trigger. Even
    // with a genuinely expired manual block, an elapsed cooling-off AND a code-unlock,
    // a valid MAC + scheduleActive RE-STAMPS (holds, and keeps HighWater advancing so
    // the window counts down to its own close) - it NEVER lifts. This is what makes
    // "a cooling-off or a code can't lift an OPEN scheduled window" true.
    [Fact]
    public void ValidMac_ScheduleActive_Restamps_EvenWhenEveryExitIsDue()
    {
        Assert.Equal(Hb.Restamp, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: true, coolOffElapsed: true, codeUnlocked: true, scheduleActive: true));
        // and with nothing else due, the open window is itself the hold (still Restamp).
        Assert.Equal(Hb.Restamp, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: false, coolOffElapsed: false, codeUnlocked: false, scheduleActive: true));
    }

    // C5b: an invalid MAC HOLDS even with an open window - the macValid gate is first,
    // so a frozen config never even consults the schedule arm (freeze always wins).
    [Fact]
    public void InvalidMac_EvenWithScheduleActive_Holds()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: false, coolOffElapsed: false, codeUnlocked: false, scheduleActive: true));
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: true, coolOffElapsed: true, codeUnlocked: true, scheduleActive: true));
    }

    // Core invariant of the B7 fix, carried over (now over all four hold/exit inputs):
    // an invalid MAC NEVER re-stamps (and never lifts) under ANY input combination -
    // including an open scheduled window (macValid is the first gate).
    [Fact]
    public void InvalidMac_NeverRestampsNorLifts()
    {
        foreach (var blockExpired in new[] { true, false })
            foreach (var coolOffElapsed in new[] { true, false })
                foreach (var codeUnlocked in new[] { true, false })
                    foreach (var scheduleActive in new[] { true, false })
                        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: blockExpired, coolOffElapsed: coolOffElapsed, codeUnlocked: codeUnlocked, scheduleActive: scheduleActive));
    }

    // The full truth table (all 32 combos): lift exactly when macValid AND NOT an open
    // scheduled window (SD1) AND (expired OR cooling-off elapsed OR code-unlocked) -
    // i.e. exactly when EffectiveExit would say so. Pinned so a future edit can't
    // silently widen (or narrow) "lift", and so the schedule HOLD can't be bypassed.
    [Fact]
    public void Lifts_IffMacValidAndNoOpenWindowAndAnExitIsDue()
    {
        foreach (var macValid in new[] { true, false })
            foreach (var blockExpired in new[] { true, false })
                foreach (var coolOffElapsed in new[] { true, false })
                    foreach (var codeUnlocked in new[] { true, false })
                        foreach (var scheduleActive in new[] { true, false })
                        {
                            bool lifts = monkmode.Service1.ClassifyHeartbeat(macValid, blockExpired, coolOffElapsed, codeUnlocked, scheduleActive) == Hb.Lift;
                            Assert.Equal(macValid && !scheduleActive && (blockExpired || coolOffElapsed || codeUnlocked), lifts);
                        }
    }
}

// The OnStart sibling re-stamp site (verifier-found third hole). OnStart re-stamps
// only on a Trusted HighWater advance; that re-stamp must ALSO require a valid MAC,
// or a guardian SCM-restart within the ceiling re-blesses a tampered Until at boot.
public class ShouldRestampOnStartTests
{
    // THE REGRESSION: tampered MAC (macValid=False) must NOT re-stamp, even on a
    // genuine HighWater advance. This is the OnStart analogue of the heartbeat P0.
    [Fact]
    public void InvalidMac_EvenOnAdvance_DoesNotRestamp()
    {
        Assert.False(monkmode.Service1.ShouldRestampOnStart(macValid: false, newHw: "2026-06-13 12:00:00", storedHw: "2026-06-13 11:59:50"));
    }

    [Fact]
    public void ValidMac_OnGenuineAdvance_Restamps()
    {
        Assert.True(monkmode.Service1.ShouldRestampOnStart(macValid: true, newHw: "2026-06-13 12:00:00", storedHw: "2026-06-13 11:59:50"));
    }

    // No advance (newHw == storedHw, the normal boot-gap=jump case) => no re-stamp,
    // even with a valid MAC. Preserves the original "rare advance only" behaviour.
    [Fact]
    public void ValidMac_NoAdvance_DoesNotRestamp()
    {
        Assert.False(monkmode.Service1.ShouldRestampOnStart(macValid: true, newHw: "2026-06-13 12:00:00", storedHw: "2026-06-13 12:00:00"));
    }

    // Empty newHw (a tick that couldn't read HighWater) => never re-stamp (never
    // blank/over-write a good value).
    [Fact]
    public void EmptyNewHw_DoesNotRestamp()
    {
        Assert.False(monkmode.Service1.ShouldRestampOnStart(macValid: true, newHw: "", storedHw: "2026-06-13 11:59:50"));
    }

    // Core invariant across all advance/empty combinations: an invalid MAC NEVER
    // re-stamps at OnStart.
    [Theory]
    [InlineData("2026-06-13 12:00:00", "2026-06-13 11:59:50")]
    [InlineData("2026-06-13 12:00:00", "2026-06-13 12:00:00")]
    [InlineData("", "2026-06-13 11:59:50")]
    public void InvalidMac_NeverRestamps(string newHw, string storedHw)
    {
        Assert.False(monkmode.Service1.ShouldRestampOnStart(macValid: false, newHw: newHw, storedHw: storedHw));
    }
}
