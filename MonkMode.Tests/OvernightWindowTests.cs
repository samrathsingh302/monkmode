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

// MonkMode.Tests - v1.1 S3a: OVERNIGHT (wrapped) schedule windows.
//
// The mechanism: a window whose end is BEFORE its start wraps past midnight
// (22:30-04:00). P20 encodes that as openMin > closeMin with NO stored flag to forge,
// and P22 makes the wrapped segment belong to the START day's mask - so a Mon 22:30-04:00
// window covers Tuesday 00:00-04:00 even though TUESDAY IS NOT IN THE MASK.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - P19, the grammar bump: a v2 wrapped token under a v1 parser was SKIPPED silently
//     (TryParseWindow's old openMin >= closeMin refusal) - a window that simply VANISHES
//     is a fail-OPEN. With the tag bumped an old parser sees an unknown tag and yields
//     zero windows instead. The legacy v1 tag is still accepted, and under it a wrapped
//     token is still refused: a v1 writer could never emit one, so honouring it would be
//     inventing a window nobody armed.
//   - P22 case (c), the post-midnight tail. THE fail-open case: after midnight the
//     calendar day has rolled, so only YESTERDAY's mask names the window. A boot at 02:00
//     re-evaluates from scratch (no stored ActiveUntil survives a reboot's re-derivation),
//     so dropping case (c) would silently release the block for the rest of the night -
//     BootInsideTheWrappedTail_ReHolds is the test that would catch it.
//   - open = close stays rejected in EVERY grammar (zero-length, and "24 hours" would be a
//     second silent meaning for the same token).
//   - P23, the jump-over close instant: open + DURATION, which for a wrapped window is the
//     NEXT day. Using the same-day close would make a wrapped window unjumpable-over, so a
//     clock-forward across the whole night would enforce nothing (SD4 fail-open).
//
// Fences honoured: pure functions only - no ini, no DPAPI, no hosts, no registry, no SCM.
// Nothing is armed.

namespace MonkMode.Tests;

public class OvernightWindowTests
{
    // 22:30-04:00 = open 1350, close 240 minutes-of-day; 330 minutes long.
    private const string MonNight = "v2;1:2230-0400;sites=x.com;apps=";      // Monday only
    private const string EveryNight = "v2;1234567:2230-0400;sites=x.com;apps=";
    private const string SunNight = "v2;7:2230-0400;sites=x.com;apps=";      // Sunday only

    // Anchor week: 2026-03-23 is a MONDAY (2026-03-29, the last Sunday in March, is the
    // spring-forward date; 2026-10-25 is the last Sunday in October).
    private const string Mon = "2026-03-23";
    private const string Tue = "2026-03-24";
    private const string Wed = "2026-03-25";

    private static List<monkmode.Service1.ScheduleWindow> W(string spec)
        => monkmode.Service1.ParseSchedule(spec).Windows;

    private static List<monkmode.Service1.ScheduleOpen> Eval(string spec, string now, bool isBoot = false,
                                                             string lastNow = "", long mono = 0)
        => monkmode.Service1.EvaluateWindows(W(spec), lastNow, now, mono, isBoot);

    /// <summary>A deadline in the exact en-CA form ComputeScheduleEnd persists (the stored
    /// [Schedule] ActiveUntil is compared byte-wise, so the expectation must be built the
    /// same way rather than typed as an ISO-looking literal).</summary>
    private static string End(int y, int m, int d, int hh, int mm)
        => new DateTime(y, m, d, hh, mm, 0).ToString(new System.Globalization.CultureInfo("en-CA"));

    // ---- 1. the grammar: what parses, and under which tag ----

    [Fact]
    public void WrappedWindow_ParsesUnderV2_WithOpenAfterClose()
    {
        var w = Assert.Single(W(MonNight));
        Assert.Equal(1, w.DayMask);           // Monday = bit 0
        Assert.Equal(22 * 60 + 30, w.OpenMinutes);
        Assert.Equal(4 * 60, w.CloseMinutes);
        Assert.True(monkmode.Service1.WindowIsWrapped(w));
        // (1440 - 1350) + 240 = 330 minutes, NOT the negative (close - open).
        Assert.Equal(330, monkmode.Service1.WindowDurationMinutes(w));
    }

    [Fact]
    public void SameDayWindow_IsNotWrapped_AndKeepsItsPlainDuration()
    {
        var w = Assert.Single(W("v2;1:0900-1700;sites=x.com;apps="));
        Assert.False(monkmode.Service1.WindowIsWrapped(w));
        Assert.Equal(480, monkmode.Service1.WindowDurationMinutes(w));
    }

    [Fact]
    public void LegacyV1Tag_StillRefusesAWrappedToken_ButKeepsItsSameDayWindows()
    {
        // A v1 writer could not emit a wrapped token, so one appearing under a v1 tag is
        // not a window the user armed - skip it (and keep the good ones beside it).
        Assert.Empty(W("v1;1:2230-0400;sites=x.com;apps="));
        var p = monkmode.Service1.ParseSchedule("v1;1:2230-0400,2:0900-1700;sites=x.com;apps=");
        Assert.Single(p.Windows);
        Assert.Equal(540, p.Windows[0].OpenMinutes);
    }

    [Theory]
    [InlineData("v2;1:0900-0900;sites=x;apps=")]   // zero-length under the new grammar
    [InlineData("v1;1:0900-0900;sites=x;apps=")]   // ...and under the legacy one
    public void OpenEqualsClose_IsRejectedInEveryGrammar(string spec)
    {
        Assert.Empty(W(spec));
    }

    [Fact]
    public void NotifierParityCopy_AgreesOnAWrappedWindow()
    {
        // mm_notify parses the Spec independently (separate assembly); a drift here would
        // make the user-session app-kill blind to an overnight window's apps.
        var svc = Assert.Single(W(EveryNight));
        var noti = Assert.Single(mm_notify.Form1.ParseSchedule(EveryNight).Windows);
        Assert.Equal(svc.DayMask, noti.DayMask);
        Assert.Equal(svc.OpenMinutes, noti.OpenMinutes);
        Assert.Equal(svc.CloseMinutes, noti.CloseMinutes);
        // ...including the legacy refusal, so the two never disagree about what is armed.
        Assert.Empty(mm_notify.Form1.ParseSchedule("v1;1:2230-0400;sites=x.com;apps=").Windows);
    }

    [Fact]
    public void CliRoundTrip_AnOvernightClauseSerialisesAndParsesBack()
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec("Mon-Sun 22:30-04:00", new[] { "x.com" },
                                                          System.Array.Empty<string>(), ref spec, ref err), err);
        Assert.Equal("v2;1234567:2230-0400;sites=x.com;apps=", spec);
        var w = Assert.Single(monkmode.Service1.ParseSchedule(spec).Windows);
        Assert.Equal(1350, w.OpenMinutes);
        Assert.Equal(240, w.CloseMinutes);
    }

    [Fact]
    public void CliRefusesZeroLength_WithTheOvernightTeachingMessage()
    {
        string spec = "", err = "";
        Assert.False(MonkMode.Blocker.TryBuildScheduleSpec("Mon 09:00-09:00", new[] { "x.com" },
                                                           System.Array.Empty<string>(), ref spec, ref err));
        Assert.Equal("", spec);                                   // nothing stamped
        Assert.Contains("starts and ends at the same time", err);
        Assert.Contains("22:30-04:00", err);                      // teaches the overnight form
    }

    // ---- 2. the 22:30 -> 04:00 matrix (P22's three cases) ----

    [Theory]
    // BEFORE the window opens on its masked day -> shut.
    [InlineData(Mon + " 22:00:00", -1)]
    // (b) inside TODAY's pre-midnight segment: to midnight (3600s) + the 04:00 close (14400s).
    [InlineData(Mon + " 23:00:00", 18000)]
    [InlineData(Mon + " 22:30:00", 19800)]     // exactly at open = the full 330 minutes
    [InlineData(Mon + " 23:59:59", 14401)]
    // (c) the post-midnight tail of Monday's opening - on a day NOT in the mask.
    [InlineData(Tue + " 00:00:00", 14400)]
    [InlineData(Tue + " 02:00:00", 7200)]
    [InlineData(Tue + " 03:59:59", 1)]
    // AT and AFTER the close -> shut (the close is exclusive, as it always was).
    [InlineData(Tue + " 04:00:00", -1)]
    [InlineData(Tue + " 05:00:00", -1)]
    // A day with NEITHER today nor yesterday in the mask -> shut all night.
    [InlineData(Wed + " 02:00:00", -1)]
    public void TheOvernightMatrix(string now, int expectedRemaining)
    {
        var opens = Eval(MonNight, now);
        if (expectedRemaining < 0) { Assert.Empty(opens); return; }
        Assert.Equal(expectedRemaining, Assert.Single(opens).RemainingSeconds);
    }

    [Fact]
    public void YesterdayMaskOnly_ATuesdayEveningIsShut_EvenThoughTuesdayMorningWasBlocked()
    {
        // The window is Monday-masked. Tuesday 02:00 is INSIDE it (Monday's tail) but
        // Tuesday 23:00 is NOT - Tuesday is not in the mask, so it opens nothing of its own.
        // Getting this wrong in the "generous" direction would block every night of the week.
        Assert.Single(Eval(MonNight, Tue + " 02:00:00"));
        Assert.Empty(Eval(MonNight, Tue + " 23:00:00"));
    }

    [Fact]
    public void EveryNightMask_TheTailAndTheNextOpeningAreOneWindowEach_NeverDoubled()
    {
        // With all seven days masked BOTH case (b) and case (c) can name the same window at
        // different times; neither may emit twice for one window in one tick (a doubled open
        // would double the SD4 jump-over duration downstream).
        Assert.Single(Eval(EveryNight, Tue + " 23:00:00"));
        Assert.Single(Eval(EveryNight, Tue + " 02:00:00"));
        Assert.Empty(Eval(EveryNight, Tue + " 12:00:00"));
    }

    // ---- 3. midnight rollover + boot, through the REAL gates ----

    [Fact]
    public void MidnightRollover_TheHoldRunsFrom2230ThroughMidnightToItsMonotonicClose()
    {
        // Tick 1, 23:00 on the masked Monday: the window opens and converts ONCE into a
        // HighWater-anchored deadline of 04:00.
        var hw1 = Mon + " 23:00:00";
        var end = monkmode.Service1.NextScheduleActiveUntil("", Eval(MonNight, hw1), hw1);
        Assert.Equal(End(2026, 3, 24, 4, 0), end);
        Assert.True(monkmode.Service1.ScheduleActive(end, hw1));

        // Tick 2, PAST MIDNIGHT (02:00 Tuesday): case (c) keeps it open and the deadline is
        // unchanged - extend-never-shorten sees the same 04:00 end, so no spurious write.
        var hw2 = Tue + " 02:00:00";
        var end2 = monkmode.Service1.NextScheduleActiveUntil(end, Eval(MonNight, hw2), hw2);
        Assert.Equal(end, end2);
        Assert.True(monkmode.Service1.ScheduleActive(end2, hw2));

        // Tick 3, at the close: nothing open + the deadline reached => cleared => lifts.
        var hw3 = Tue + " 04:00:00";
        var end3 = monkmode.Service1.NextScheduleActiveUntil(end2, Eval(MonNight, hw3), hw3);
        Assert.Equal("", end3);
        Assert.False(monkmode.Service1.ScheduleActive(end3, hw3));
    }

    [Fact]
    public void BootInsideTheWrappedTail_ReHolds()
    {
        // THE fail-open case P22 case (c) exists for. A reboot at 02:00 re-derives the
        // schedule state from scratch with isBoot=True, on a calendar day the mask does NOT
        // name. Without case (c) this returns nothing, ActiveUntil stays "" and the rest of
        // the night is unblocked. With it, the window re-holds for its remaining 2 hours.
        var boot = Eval(MonNight, Tue + " 02:00:00", isBoot: true);
        Assert.Equal(7200, Assert.Single(boot).RemainingSeconds);

        var end = monkmode.Service1.NextScheduleActiveUntil("", boot, Tue + " 02:00:00");
        Assert.Equal(End(2026, 3, 24, 4, 0), end);
        Assert.True(monkmode.Service1.ScheduleActive(end, Tue + " 02:00:00"));
    }

    [Fact]
    public void BootAfterTheWindowClosed_StaysMissed()
    {
        // Crux #4b is unchanged for a wrapped window: a boot only ever opens a window it
        // lands INSIDE. A boot at 05:00 has no trustworthy monotonic elapsed to call the gap
        // a jump, so the missed night stays missed rather than starting a 5.5h block at dawn.
        Assert.Empty(Eval(MonNight, Tue + " 05:00:00", isBoot: true));
    }

    // ---- 4. SD4: jumping OVER a wrapped window ----

    [Fact]
    public void LiveJumpOverAWholeNight_EnforcesTheFull330Minutes()
    {
        // 20:00 Monday -> 10:00 Tuesday with ~no real elapsed: the whole 22:30-04:00 window
        // was traversed. P23's close instant (open + duration = Tuesday 04:00) is what makes
        // this detectable at all; with the old same-day close (Monday 04:00, BEFORE the open)
        // the condition could never hold and a clock-forward across the night would enforce
        // nothing.
        var opens = Eval(MonNight, Tue + " 10:00:00", lastNow: Mon + " 20:00:00", mono: 10);
        Assert.Equal(330 * 60, Assert.Single(opens).RemainingSeconds);
    }

    [Fact]
    public void JumpLandingInsideTheTail_TakesTheInsideBranch_NotTheFullDuration()
    {
        // Jumping to 03:00 Tuesday lands INSIDE the window: it must enforce the REMAINING
        // hour, not the full 330 minutes (the inside branch out-ranks the jump branch).
        var opens = Eval(MonNight, Tue + " 03:00:00", lastNow: Mon + " 20:00:00", mono: 10);
        Assert.Equal(3600, Assert.Single(opens).RemainingSeconds);
    }

    [Fact]
    public void AnHonestTickAcrossMidnight_IsNeverAJumpOver()
    {
        // 23:59:50 -> 00:00:00 with the matching real elapsed is an ordinary tick: it opens
        // via the INSIDE branch (case (c)), with the remaining-to-close, not a full duration.
        var opens = Eval(MonNight, Tue + " 00:00:00", lastNow: Mon + " 23:59:50", mono: 10);
        Assert.Equal(14400, Assert.Single(opens).RemainingSeconds);
    }

    [Fact]
    public void SameDayWindows_JumpOverIsByteIdenticalToBefore()
    {
        // The P23 rewrite must not disturb the same-day case: d + open + (close - open) is
        // exactly d + close. A 09:00-17:00 window jumped clean over enforces its 8 hours.
        var opens = monkmode.Service1.EvaluateWindows(W("v2;1234567:0900-1700;sites=x.com;apps="),
                                                      Mon + " 08:00:00", Mon + " 18:00:00", 10, false);
        Assert.Equal(8 * 3600, Assert.Single(opens).RemainingSeconds);
    }

    // ---- 5. DST-adjacent dates ----

    [Theory]
    // Spring forward: 2026-03-29 is the last Sunday in March (the local day is 23h long).
    [InlineData(SunNight, "2026-03-29 23:00:00", 18000)]   // Sunday evening, case (b)
    [InlineData(SunNight, "2026-03-30 02:00:00", 7200)]    // Monday morning tail, case (c)
    // Fall back: 2026-10-25 is the last Sunday in October (the local day is 25h long).
    [InlineData(SunNight, "2026-10-25 23:00:00", 18000)]
    [InlineData(SunNight, "2026-10-26 02:00:00", 7200)]
    public void DstAdjacentNights_StillOpenAndStillCarryTheirRemaining(string spec, string now, int remaining)
    {
        // The evaluator works in LOCAL minute-of-day only - it never converts to UTC - so a
        // DST transition cannot make a masked night vanish. The remaining it emits is a
        // wall-clock estimate that a 23h/25h day skews by up to an hour, which is why the
        // hold is RE-EXTENDED on every tick while still inside (extend-never-shorten): the
        // skew self-corrects each tick, and a spring-forward skew errs by over-blocking.
        Assert.Equal(remaining, Assert.Single(Eval(spec, now)).RemainingSeconds);
    }

    [Fact]
    public void SpringForwardNight_ReExtendsToTheRealCloseAsTheNightProceeds()
    {
        // Composed through the real decision: the deadline minted at 23:00 is re-derived at
        // 02:00 from that tick's own HighWater, so the hold tracks the close rather than
        // depending on the first tick's estimate being exactly right across the transition.
        var hwEve = "2026-03-29 23:00:00";
        var end = monkmode.Service1.NextScheduleActiveUntil("", Eval(SunNight, hwEve), hwEve);
        Assert.Equal(End(2026, 3, 30, 4, 0), end);
        var hwMorn = "2026-03-30 02:00:00";
        Assert.True(monkmode.Service1.ScheduleActive(end, hwMorn));
        Assert.Equal(end, monkmode.Service1.NextScheduleActiveUntil(end, Eval(SunNight, hwMorn), hwMorn));
    }
}
