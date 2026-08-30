// Copyright (C) 2026 Samrath Singh
//
// This file is part of MonkMode, a fork of Cold Turkey.
// Source: https://github.com/samrathsingh302/monkmode
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

// MonkMode.Tests - ledger 313(a): `status` must report the time a block ACTUALLY has left.
//
// THE BUG THIS PINS. Expiry is decided against the MAC-covered monotonic [Time] HighWater,
// which advances only while the service is alive - so an hour the machine spends shut down or
// asleep is an hour the block does not serve. `status` was reading the WALL clock instead:
//
//   * the slot table printed the stored end stamp with nothing to say that the stamp moves;
//   * the v9 fallback branch asked `Until > DateTime.Now` and, once the wall clock had run
//     past Until while HighWater still lagged behind it, printed
//     "no active block (service installed but idle)" over a block the service was fully
//     enforcing - the worst possible lie for a self-control tool to tell.
//
// Both now measure on the mark (deadline - HighWater), the same subtraction the tray notifier
// shows and the service enforces. These are PURE tests on the formatters and the decision:
// plain values in, literal strings out, no ini in sight. The read path that fills the mark in
// is pinned live in SlotCliTests (SlotCliLiveTests).
//
// Fail-soft is half the contract: an unreadable or MAC-invalid mark arrives as
// DateTime.MinValue and must degrade to a placeholder - never a wrong number, never a throw.

namespace MonkMode.Tests;

public class MonotonicStatusDisplayTests
{
    private static MonkMode.Blocker.SlotView ActiveSlot(DateTime ends, DateTime mark)
        => new()
        {
            Id = "1",
            State = MonkMode.Blocker.SlotStateActive,
            Ends = ends,
            Mark = mark,
            Sites = 1,
        };

    // ---- the headline: the remaining is measured on the mark, not on DateTime.Now ----

    [Fact]
    public void ActiveRow_ShowsTheMonotonicRemaining_NotTheWallClockOne()
    {
        // Both stamps are in the WALL-CLOCK past (2026-08-09), the shape a machine that was
        // shut down overnight produces: the stored end has been and gone, yet the block has
        // 2h 10m of active time still to serve and is still being enforced.
        var v = ActiveSlot(new DateTime(2026, 8, 9, 21, 0, 0), new DateTime(2026, 8, 9, 18, 50, 0));

        Assert.Equal("(~2h 10m of active time left)", MonkMode.Program.FormatSlotRemainingCell(v));
        Assert.EndsWith("  (~2h 10m of active time left)", MonkMode.Program.FormatSlotRow(v));
        // ...and the row up to the Exit token is untouched by it - the fixed-width columns
        // still line up (P32).
        Assert.StartsWith("  1  ACTIVE    2026-08-09 21:00              1    0    0  code+wait",
                          MonkMode.Program.FormatSlotRow(v));
    }

    [Theory]
    [InlineData(0, 130, "(~2h 10m of active time left)")]
    [InlineData(0, 59, "(~59m of active time left)")]
    [InlineData(2, 0, "(~2d of active time left)")]
    [InlineData(0, 0, "(due to lift)")]          // exactly at the mark: the next tick lifts it
    public void Remaining_IsTheDeadlineMinusTheMark(int days, int minutes, string expected)
    {
        var mark = new DateTime(2026, 8, 9, 18, 50, 0);
        var ends = mark.AddDays(days).AddMinutes(minutes);
        Assert.Equal(expected, MonkMode.Program.FormatRemainingParenthetical(ends, mark));
    }

    [Fact]
    public void APassedDeadline_ReadsAsDueToLift_NotAsZeroMinutes()
    {
        // Until <= HighWater IS the expiry condition, so this is a block in its last tick -
        // "0 minutes" would read as a countdown that has stalled.
        var v = ActiveSlot(new DateTime(2026, 8, 9, 21, 0, 0), new DateTime(2026, 8, 9, 22, 0, 0));
        Assert.Equal("(due to lift)", MonkMode.Program.FormatSlotRemainingCell(v));
    }

    // ---- fail-soft: an unreadable / MAC-gated-away mark never invents a number ----

    [Fact]
    public void UnreadableMark_RendersAPlaceholder_RatherThanThrow()
    {
        // MinValue is what ReadSlotViews hands over for an unreadable HighWater AND for a
        // config that failed its integrity check (the mark is MAC-gated like Committed).
        var v = ActiveSlot(new DateTime(2026, 8, 9, 21, 0, 0), DateTime.MinValue);
        Assert.Equal("(active time left unknown)", MonkMode.Program.FormatSlotRemainingCell(v));
        Assert.EndsWith("  (active time left unknown)", MonkMode.Program.FormatSlotRow(v));
    }

    [Fact]
    public void AnUnreadableEnd_CarriesNoRemainingAtAll()
    {
        // The "Ends" cell already renders "?"; a remaining measured from MinValue would be a
        // meaningless 2000-year countdown.
        var v = ActiveSlot(DateTime.MinValue, new DateTime(2026, 8, 9, 18, 50, 0));
        Assert.Equal("", MonkMode.Program.FormatSlotRemainingCell(v));
        Assert.Equal("", MonkMode.Program.FormatSlotRemainingCell(null));
    }

    [Fact]
    public void PendingAndScheduleRows_CarryNoRemaining()
    {
        // A PENDING block has no end yet (the service computes it at activation) and a
        // SCHEDULE row's own cell already says whether a window is open.
        var pending = new MonkMode.Blocker.SlotView
        {
            Id = "5",
            State = MonkMode.Blocker.SlotStatePending,
            StartAt = new DateTime(2026, 8, 10, 7, 0, 0),
            DurationSeconds = 7200,
            Mark = new DateTime(2026, 8, 9, 18, 50, 0),
        };
        var schedule = new MonkMode.Blocker.SlotView
        {
            Id = "6",
            State = MonkMode.Blocker.SlotStateSchedule,
            WindowOpen = true,
            WindowUntil = new DateTime(2026, 8, 10, 4, 0, 0),
            Mark = new DateTime(2026, 8, 9, 18, 50, 0),
        };
        Assert.Equal("", MonkMode.Program.FormatSlotRemainingCell(pending));
        Assert.Equal("", MonkMode.Program.FormatSlotRemainingCell(schedule));
        Assert.DoesNotContain("active time", MonkMode.Program.FormatSlotRow(pending));
        Assert.DoesNotContain("active time", MonkMode.Program.FormatSlotRow(schedule));
    }

    // ---- the note under the table ----

    [Fact]
    public void TheNote_SaysWhatTheEndStampActuallyMeans()
    {
        Assert.Equal("  Note: the end time counts machine-ON time only - sleep or shutdown pushes it later by the same amount.",
                     MonkMode.Program.FormatMonotonicNoteLine());
    }

    [Fact]
    public void TheNote_IsOwedOnlyWhenSomethingIsActive()
    {
        var mark = new DateTime(2026, 8, 9, 18, 50, 0);
        var active = ActiveSlot(new DateTime(2026, 8, 9, 21, 0, 0), mark);
        var pending = new MonkMode.Blocker.SlotView { Id = "5", State = MonkMode.Blocker.SlotStatePending };
        var schedule = new MonkMode.Blocker.SlotView { Id = "6", State = MonkMode.Blocker.SlotStateSchedule };

        Assert.True(MonkMode.Program.AnyActiveSlot(new List<MonkMode.Blocker.SlotView> { pending, active }));
        Assert.False(MonkMode.Program.AnyActiveSlot(new List<MonkMode.Blocker.SlotView> { pending, schedule }));
        Assert.False(MonkMode.Program.AnyActiveSlot(new List<MonkMode.Blocker.SlotView>()));
        Assert.False(MonkMode.Program.AnyActiveSlot(null));
    }

    // ---- the v9 fallback's ACTIVE decision ----

    [Fact]
    public void V9Fallback_IsActive_WhenTheWallClockHasPassedButTheMarkHasNot()
    {
        // THE REGRESSION. Machine off from 22:00 to 08:00: the wall clock is now well past a
        // 02:00 end, but HighWater - which only moved while the service ran - is still at
        // 21:55, so the service is still enforcing. The old `Until > DateTime.Now` test said
        // "idle" here.
        var until = new DateTime(2026, 8, 9, 2, 0, 0);
        var mark = new DateTime(2026, 8, 8, 21, 55, 0);
        Assert.True(until < DateTime.Now, "the fixture only means anything with Until in the wall-clock past");

        Assert.True(MonkMode.Blocker.LegacyBlockIsActive(true, until, mark));
        Assert.Equal("(~4h 5m of active time left)", MonkMode.Program.FormatRemainingParenthetical(until, mark));
    }

    [Fact]
    public void V9Fallback_IsIdle_OnlyWhenGenuinelyExpiredOrUnconfigured()
    {
        var until = new DateTime(2026, 8, 9, 2, 0, 0);

        // Mark past the end = genuinely expired, the same rule BlockGenuinelyExpired applies.
        Assert.False(MonkMode.Blocker.LegacyBlockIsActive(true, until, until));
        Assert.False(MonkMode.Blocker.LegacyBlockIsActive(true, until, until.AddSeconds(1)));
        // No end time at all (absent or unreadable) = nothing configured to report.
        Assert.False(MonkMode.Blocker.LegacyBlockIsActive(true, DateTime.MinValue, until));
        Assert.False(MonkMode.Blocker.LegacyBlockIsActive(false, DateTime.MinValue, DateTime.MinValue));
    }

    [Fact]
    public void V9Fallback_FailsClosed_WhenItCannotMeasure()
    {
        // An invalid MAC or an unreadable mark: the SERVICE holds the block in exactly these
        // cases (EffectiveBlockHasExpired is macValid-gated and needs a parseable mark), so
        // the display must not answer "idle".
        var until = new DateTime(2026, 8, 9, 2, 0, 0);
        Assert.True(MonkMode.Blocker.LegacyBlockIsActive(false, until, DateTime.MinValue));
        Assert.True(MonkMode.Blocker.LegacyBlockIsActive(true, until, DateTime.MinValue));
        Assert.Equal("(active time left unknown)",
                     MonkMode.Program.FormatRemainingParenthetical(until, DateTime.MinValue));
    }
}
