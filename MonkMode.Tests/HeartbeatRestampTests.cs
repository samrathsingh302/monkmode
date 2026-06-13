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
    [Fact]
    public void ValidMac_Expired_Lifts()
    {
        Assert.Equal(Hb.Lift, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: true));
    }

    [Fact]
    public void ValidMac_NotExpired_Restamps()
    {
        Assert.Equal(Hb.Restamp, monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: false));
    }

    // THE REGRESSION. A tampered/invalid MAC over a (back-dated) past Until must
    // HOLD - the old code re-stamped here, re-blessing the tamper and lifting the
    // block next tick. This is the single assertion that would have caught the P0.
    [Fact]
    public void HeartbeatForTamperedExpiredConfig_IsHold()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: true));
    }

    [Fact]
    public void InvalidMac_NotExpired_Holds()
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: false));
    }

    // Core invariant of the fix: an invalid MAC NEVER re-stamps (and never lifts).
    // Re-stamping is the only thing that re-blesses a tamper, so it must require a
    // valid MAC under every input.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidMac_NeverRestampsNorLifts(bool blockExpired)
    {
        Assert.Equal(Hb.Hold, monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: blockExpired));
    }

    // The lift decision is UNCHANGED by the fix: ClassifyHeartbeat lifts exactly
    // when EffectiveBlockHasExpired would (macValid AndAlso blockExpired). Pin the
    // whole truth table so a future edit can't silently widen "lift".
    [Theory]
    [InlineData(true,  true,  true)]
    [InlineData(true,  false, false)]
    [InlineData(false, true,  false)]
    [InlineData(false, false, false)]
    public void Lifts_IffMacValidAndExpired(bool macValid, bool blockExpired, bool expectLift)
    {
        bool lifts = monkmode.Service1.ClassifyHeartbeat(macValid, blockExpired) == Hb.Lift;
        Assert.Equal(expectLift, lifts);
        Assert.Equal(macValid && blockExpired, lifts);
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
