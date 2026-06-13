// MonkMode.Tests - cross-slice fix: the CLI `block` liveness gate must decide
// off the B4 high-water mark + B7 MAC, not the raw wall clock.
//
// THE BUG (cross-slice audit, 2026-06-13, verifier P1): Blocker.BlockIsActive()
// used `ActiveBlockEnd() > DateTime.Now`. An attacker rolls the system clock
// forward past [Time] Until => BlockIsActive() returns False => `monkmode block
// --for 1m` thinks no block is standing and overwrites it with a fresh, fully
// MAC-valid 1-minute block. B4 ("clock can't shorten a block") and B7 are
// bypassed through the CLI seam, even though the service itself refuses to lift.
//
// THE FIX: Blocker.BlockGenuinelyExpired mirrors the service's
// EffectiveBlockHasExpired - expired ONLY when the MAC is valid AND Until is
// at/before the trusted HighWater mark (which a clock-forward can't advance).
// These pin the pure decision incl. the clock-forward regression. (BlockIsActive
// itself does ini I/O - the smoke test covers the live path; this is the gate.)

using System.Globalization;

namespace MonkMode.Tests;

public class BlockGenuinelyExpiredTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static string T(DateTime dt) => dt.ToString(EnCa);
    private static readonly DateTime Base = new(2026, 6, 13, 12, 0, 0);

    // Invalid MAC (tampered / unstamped / frozen) => NEVER genuinely expired, so
    // the standing block is treated as active and is not overwritable. The exit is
    // `unblock --force`, not re-running `block`.
    [Theory]
    [InlineData(true)]   // even if Until is "past" HighWater
    [InlineData(false)]
    public void InvalidMac_IsNeverGenuinelyExpired(bool untilBeforeHw)
    {
        var until = untilBeforeHw ? Base.AddMinutes(-30) : Base.AddMinutes(30);
        Assert.False(MonkMode.Blocker.BlockGenuinelyExpired(macValid: false, T(until), T(Base)));
    }

    // Valid MAC + Until at/before the high-water mark => genuinely expired
    // (overwritable). This is the legitimate "the block has run its course" case.
    [Fact]
    public void ValidMac_UntilPastHighWater_IsExpired()
    {
        Assert.True(MonkMode.Blocker.BlockGenuinelyExpired(true, T(Base.AddMinutes(-1)), T(Base)));
    }

    [Fact]
    public void ValidMac_UntilEqualsHighWater_IsExpired()
    {
        Assert.True(MonkMode.Blocker.BlockGenuinelyExpired(true, T(Base), T(Base)));
    }

    // THE REGRESSION. Until is still in the future relative to the trusted
    // HighWater (the service refused to advance it when the clock jumped), so the
    // block is NOT expired - regardless of what the wall clock now reads. Under the
    // OLD code (compare Until to DateTime.Now), an attacker who rolled the clock to
    // 13:00 would make BlockIsActive() False and overwrite the block; here the
    // decision uses HighWater (12:00) so the block stays active.
    [Fact]
    public void ClockRolledForward_HighWaterUnmoved_NotExpired()
    {
        var until = Base.AddMinutes(30);   // block ends 12:30
        var highWater = Base;              // trusted mark stuck at real 12:00 (jump refused)
        // wall clock may now read 13:00, but that never enters the decision:
        Assert.False(MonkMode.Blocker.BlockGenuinelyExpired(true, T(until), T(highWater)));
    }

    // Fail-closed on unparseable inputs (a corrupt/legacy field reads as active).
    [Theory]
    [InlineData("garbage", "2026-06-13 12:00:00")]
    [InlineData("2026-06-13 12:30:00", "garbage")]
    [InlineData("", "")]
    public void UnparseableInputs_AreNotExpired(string until, string highWater)
    {
        Assert.False(MonkMode.Blocker.BlockGenuinelyExpired(true, until, highWater));
    }
}
