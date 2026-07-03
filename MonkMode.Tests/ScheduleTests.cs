// MonkMode.Tests - C5b schedules, sub-slice (a): the PURE gates.
//
// The mechanism under test (design: vault\dev\monk-mode\plans\C5a-schedules-design.md):
// a schedule is a recurring WALL-CLOCK rule (Mon-Fri 09:00-17:00) stored as one
// MAC-covered plaintext [Schedule] Spec. WALL-CLOCK decides WHEN a window opens;
// the monotonic B4 HighWater decides HOW LONG it enforces. At the first tick a
// window is open the service converts it ONCE into a HighWater-anchored deadline
// [Schedule] ActiveUntil = HighWater_now + (close - now); from then it counts down
// against HighWater (never DateTime.Now), so a mid-window clock-forward can't end
// it early - exactly like C2b's CoolOffUntil.
//
// Sub-slice (a) is the PURE, fail-closed core (no DPAPI, no hosts/registry/SCM, no
// enforcement wiring - the fields are in the canonical + these gates exist and are
// tested, but nothing here is read by the tick/lift/hold path yet; that is (b)):
//   - the grammar-version const;
//   - ParseSchedule: the §3 grammar + fail-closed (garbage/unknown-tag -> inert; a
//     malformed window is skipped, the good ones kept; SD3 overnight rejected);
//   - EvaluateWindows: the §4.2 matrix (inside-now / forward-jump-INTO / live
//     jump-OVER / boot-inside / missed-on-boot / before-window / day-mismatch);
//   - ComputeScheduleEnd (the ComputeCoolOffDeadline sibling);
//   - ScheduleElapsed / ScheduleActive, each fail-closed on every axis, with
//     service<->guardian parity (the guardian folding ScheduleActive in is
//     LOAD-BEARING in (b): without it, it would resurrect a scheduled block);
//   - BlockHeld (the shared enforce-while helper);
//   - a pure end-to-end composition through the REAL B4 gates (NextHighWater +
//     CapHighWaterAdvance): a window opens -> converts -> counts down -> ends at its
//     monotonic close; a mid-window clock-forward is refused (the wait is never
//     skipped).
//
// The live wiring (ProcessScheduleWindows' file I/O + ini save + backup refresh, the
// lift/hold fold into EffectiveExit/ClassifyHeartbeat, the union enforcement, the
// guardian fold, the CLI `schedule` front-end) is the C5b sub-slice (b)/(c) seam,
// smoke-tested at the CV checkpoint - exactly like the C2b/C3b live wiring.

using System.Globalization;

namespace MonkMode.Tests;

public class ScheduleConstTests
{
    [Fact]
    public void GrammarVersionTag_IsV1_AndParityAcrossServiceAndGuardianEvaluator()
    {
        // The Spec always leads with this tag so C6 can grow the grammar (v1 -> v2)
        // WITHOUT a canonical bump. A retune is a single loud edit here.
        Assert.Equal("v1", monkmode.Service1.ScheduleSpecGrammarVersion);
    }
}

public class ParseScheduleTests
{
    [Fact]
    public void FullSpec_ParsesWindowsSitesAndApps()
    {
        var p = monkmode.Service1.ParseSchedule(
            "v1;12345:0900-1700,67:1000-1400;sites=reddit.com|news.ycombinator.com;apps=chrome.exe|brave.exe");
        Assert.Equal(2, p.Windows.Count);
        // Mon-Fri 09:00-17:00 -> bits 0..4 = 0b0011111 = 31; 540..1020 minutes.
        Assert.Equal(31, p.Windows[0].DayMask);
        Assert.Equal(540, p.Windows[0].OpenMinutes);
        Assert.Equal(1020, p.Windows[0].CloseMinutes);
        // Sat,Sun 10:00-14:00 -> bits 5,6 = 0b1100000 = 96; 600..840 minutes.
        Assert.Equal(96, p.Windows[1].DayMask);
        Assert.Equal(600, p.Windows[1].OpenMinutes);
        Assert.Equal(840, p.Windows[1].CloseMinutes);
        Assert.Equal(new[] { "reddit.com", "news.ycombinator.com" }, p.Sites.ToArray());
        Assert.Equal(new[] { "chrome.exe", "brave.exe" }, p.Apps.ToArray());
    }

    [Fact]
    public void AllSevenDays_AndEmptyApps_Parse()
    {
        var p = monkmode.Service1.ParseSchedule("v1;1234567:0000-2359;sites=x.com;apps=");
        Assert.Single(p.Windows);
        Assert.Equal(127, p.Windows[0].DayMask);   // all seven bits
        Assert.Equal(0, p.Windows[0].OpenMinutes);
        Assert.Equal(23 * 60 + 59, p.Windows[0].CloseMinutes);
        Assert.Equal(new[] { "x.com" }, p.Sites.ToArray());
        Assert.Empty(p.Apps);                       // apps= with an empty body
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("garbage")]                          // no ';' -> < 2 parts -> inert
    [InlineData("v2;12345:0900-1700;sites=x;apps=")] // unknown grammar tag -> inert
    [InlineData("nope;12345:0900-1700;sites=x;apps=")]
    public void UnparseableOrUnknownTag_YieldsNoWindows_Inert(string? spec)
    {
        // A wholly unparseable/empty Spec or an unknown grammar version must NEVER
        // invent a phantom permanent block - it is inert (no windows). (A TAMPERED
        // Spec fails the MAC upstream -> freeze; that is B7, not ParseSchedule.)
        var p = monkmode.Service1.ParseSchedule(spec!);
        Assert.Empty(p.Windows);
    }

    [Theory]
    [InlineData("v1;99:0900-1700;sites=x;apps=")]        // day '9' out of range
    [InlineData("v1;:0900-1700;sites=x;apps=")]          // empty day mask
    [InlineData("v1;12345:2500-1700;sites=x;apps=")]     // hour 25 out of range
    [InlineData("v1;12345:0960-1700;sites=x;apps=")]     // minute 60 out of range
    [InlineData("v1;12345:900-1700;sites=x;apps=")]      // HHMM not 4 digits
    [InlineData("v1;12345:1700-0900;sites=x;apps=")]     // SD3: overnight (close <= open)
    [InlineData("v1;12345:0900-0900;sites=x;apps=")]     // SD3: zero-length window
    [InlineData("v1;12345:09:00-17:00;sites=x;apps=")]   // human form (extra colon) not the compact grammar
    public void MalformedWindow_IsSkipped_FailClosed(string spec)
    {
        // A malformed window is dropped (fail-closed: never enforce a window you can't
        // trust the bounds of), leaving the schedule inert here (its only window bad).
        var p = monkmode.Service1.ParseSchedule(spec);
        Assert.Empty(p.Windows);
    }

    [Fact]
    public void OneGoodOneBadWindow_KeepsTheGood_DropsTheBad()
    {
        // "skip the bad window, keep the good ones" - a partly-garbage rule still
        // enforces its valid windows (and never the malformed one).
        var p = monkmode.Service1.ParseSchedule("v1;12345:0900-1700,BADWIN,67:2500-2600;sites=x.com;apps=");
        Assert.Single(p.Windows);
        Assert.Equal(31, p.Windows[0].DayMask);
        Assert.Equal(540, p.Windows[0].OpenMinutes);
        Assert.Equal(1020, p.Windows[0].CloseMinutes);
    }

    [Fact]
    public void SitesAndApps_AreOrderTolerant_AndTrimEmpties()
    {
        // The lists are located by their "sites="/"apps=" prefix, so their order is
        // tolerated and empty entries between separators are dropped.
        var p = monkmode.Service1.ParseSchedule("v1;7:1000-1100;apps=chrome.exe;sites=a.com||b.com");
        Assert.Single(p.Windows);
        Assert.Equal(new[] { "a.com", "b.com" }, p.Sites.ToArray());   // the empty middle token dropped
        Assert.Equal(new[] { "chrome.exe" }, p.Apps.ToArray());
    }
}

public class ComputeScheduleEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);

    [Fact]
    public void AddsRemainingSecondsToTheHighWaterMark()
    {
        // The window->duration conversion: end = HighWater_at_open + remaining. It
        // lives in the HighWater frame, so it is reached only after that much real
        // on-machine elapsed time (never clock-skippable).
        Assert.Equal(Hw.AddSeconds(28800).ToString(EnCa),
            monkmode.Service1.ComputeScheduleEnd(Hw.ToString(EnCa), 28800));   // an 8h window
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableHighWater_YieldsNoDeadline_FailClosed(string hw)
    {
        // No trustworthy mark => no deadline computable => the service writes nothing
        // (retry next tick). Never a bogus deadline.
        Assert.Equal("", monkmode.Service1.ComputeScheduleEnd(hw, 28800));
    }

    [Fact]
    public void FreshDeadline_IsNotElapsed_AtTheMarkItWasComputedFrom()
    {
        // Round-trip with the elapsed gate: a just-converted window is not instantly
        // elapsed (the hold is real).
        var deadline = monkmode.Service1.ComputeScheduleEnd(Hw.ToString(EnCa), 28800);
        Assert.False(monkmode.Service1.ScheduleElapsed(deadline, Hw.ToString(EnCa)));
        Assert.True(monkmode.Service1.ScheduleActive(deadline, Hw.ToString(EnCa)));
    }
}

public class ScheduleElapsedActiveTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);

    [Fact]
    public void EmptyDeadline_NoWindowOpen_NotElapsed_NotActive()
    {
        Assert.False(monkmode.Service1.ScheduleElapsed("", Hw.ToString(EnCa)));
        Assert.False(monkmode.Service1.ScheduleActive("", Hw.ToString(EnCa)));
        Assert.False(mm_guard.Guardian.ScheduleElapsed("", Hw.ToString(EnCa)));
        Assert.False(mm_guard.Guardian.ScheduleActive("", Hw.ToString(EnCa)));
    }

    [Fact]
    public void PastDeadline_IsElapsed_NotActive_AndEqualCountsAsElapsed()
    {
        Assert.True(monkmode.Service1.ScheduleElapsed(Hw.AddMinutes(-1).ToString(EnCa), Hw.ToString(EnCa)));
        Assert.False(monkmode.Service1.ScheduleActive(Hw.AddMinutes(-1).ToString(EnCa), Hw.ToString(EnCa)));
        // deadline == HighWater: the window is served (<=, like the design pins).
        Assert.True(monkmode.Service1.ScheduleElapsed(Hw.ToString(EnCa), Hw.ToString(EnCa)));
        Assert.False(monkmode.Service1.ScheduleActive(Hw.ToString(EnCa), Hw.ToString(EnCa)));
    }

    [Fact]
    public void FutureDeadline_IsNotElapsed_AndIsActive()
    {
        Assert.False(monkmode.Service1.ScheduleElapsed(Hw.AddHours(1).ToString(EnCa), Hw.ToString(EnCa)));
        Assert.True(monkmode.Service1.ScheduleActive(Hw.AddHours(1).ToString(EnCa), Hw.ToString(EnCa)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("25.06.2026 17:04:33")] // legacy de-DE format
    public void UnparseableDeadline_IsNotElapsed_ButStaysActive_FailClosed(string deadline)
    {
        // A corrupted deadline can only ever HOLD the window (active), never end it.
        Assert.False(monkmode.Service1.ScheduleElapsed(deadline, Hw.ToString(EnCa)));
        Assert.True(monkmode.Service1.ScheduleActive(deadline, Hw.ToString(EnCa)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableOrBlankHighWater_IsNotElapsed_HoldsTheWindow_FailClosed(string hw)
    {
        // No trustworthy mark to measure against => the window never ends (stays held).
        Assert.False(monkmode.Service1.ScheduleElapsed(Hw.AddHours(1).ToString(EnCa), hw));
        Assert.True(monkmode.Service1.ScheduleActive(Hw.AddHours(1).ToString(EnCa), hw));
    }

    [Fact]
    public void ServiceAndGuardian_AgreeAcrossTheTable()
    {
        // The pair must never disagree on "window open"/"window elapsed", or the
        // guardian could resurrect a scheduled block the service just released (or
        // stand down mid-window). Parity is LOAD-BEARING once (b) folds ScheduleActive
        // into the guardian's stand-down.
        var deadlines = new[]
        {
            Hw.AddMinutes(-1).ToString(EnCa), Hw.ToString(EnCa),
            Hw.AddHours(1).ToString(EnCa), "garbage", "",
        };
        foreach (var d in deadlines)
            foreach (var hw in new[] { Hw.ToString(EnCa), "garbage", "" })
            {
                Assert.Equal(
                    monkmode.Service1.ScheduleElapsed(d, hw),
                    mm_guard.Guardian.ScheduleElapsed(d, hw));
                Assert.Equal(
                    monkmode.Service1.ScheduleActive(d, hw),
                    mm_guard.Guardian.ScheduleActive(d, hw));
            }
    }
}

public class BlockHeldTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime AsOf = new(2026, 6, 25, 12, 0, 0);
    private static readonly string Hw = AsOf.ToString(EnCa);
    private static readonly string PastUntil = AsOf.AddHours(-1).ToString(EnCa);
    private static readonly string FutureUntil = AsOf.AddHours(5).ToString(EnCa);
    private static readonly string OpenWindow = AsOf.AddHours(1).ToString(EnCa);   // ActiveUntil in the future
    private static readonly string ClosedWindow = AsOf.AddHours(-1).ToString(EnCa); // ActiveUntil already elapsed

    [Fact]
    public void InvalidMac_AlwaysHeld_Freeze()
    {
        // macValid=False => EffectiveBlockHasExpired is False => Not False => held.
        // A frozen config always enforces, schedule or no schedule.
        Assert.True(monkmode.Service1.BlockHeld(PastUntil, AsOf, 5, macValid: false, "", Hw));
        Assert.True(monkmode.Service1.BlockHeld(PastUntil, AsOf, 5, macValid: false, ClosedWindow, Hw));
    }

    [Fact]
    public void ManualBlockNotYetExpired_Held_RegardlessOfSchedule()
    {
        Assert.True(monkmode.Service1.BlockHeld(FutureUntil, AsOf, 5, macValid: true, "", Hw));
    }

    [Fact]
    public void ManualExpired_NoWindow_NotHeld()
    {
        // The manual block genuinely ended and no scheduled window is open: not held.
        Assert.False(monkmode.Service1.BlockHeld(PastUntil, AsOf, 5, macValid: true, "", Hw));
        Assert.False(monkmode.Service1.BlockHeld(PastUntil, AsOf, 5, macValid: true, ClosedWindow, Hw));
    }

    [Fact]
    public void ManualExpired_ButWindowOpen_Held_TheScheduleArmAddsEnforcement()
    {
        // The whole point of the schedule arm: an expired manual block still enforces
        // while a window is open (§6.3 - BlockHeld ORs the open window in).
        Assert.True(monkmode.Service1.BlockHeld(PastUntil, AsOf, 5, macValid: true, OpenWindow, Hw));
    }
}

// EvaluateWindows: the §4.2 wall-clock matrix. Windows are parsed from a Spec (so the
// parser + evaluator are exercised together, as the tick composes them). Days are
// derived from the concrete date under test to avoid weekday-hardcoding mistakes.
public class EvaluateWindowsTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Mono = 10;   // a normal ~10s tick's real monotonic elapsed

    // A Mon-Fri 09:00-17:00 schedule (the canonical example).
    private static System.Collections.Generic.List<monkmode.Service1.ScheduleWindow> MonFri9to5() =>
        monkmode.Service1.ParseSchedule("v1;12345:0900-1700;sites=x.com;apps=").Windows;

    // The next date on/after 2026-06-25 with the given weekday (avoids hardcoding).
    private static DateTime OnA(DayOfWeek dow, int hour, int minute)
    {
        var d = new DateTime(2026, 6, 25);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void InsideNow_NormalTick_Opens_RemainingIsCloseMinusNow()
    {
        // now = Wed 10:00, inside 09:00-17:00 -> open, remaining = 7h = 25200s.
        var now = OnA(DayOfWeek.Wednesday, 10, 0);
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), now.AddSeconds(-Mono).ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Single(opens);
        Assert.Equal(7 * 3600, opens[0].RemainingSeconds);
    }

    [Fact]
    public void ForwardJumpINTO_AWindow_Opens_RemainingIsCloseMinusNow()
    {
        // A live jump lands INSIDE the window (last=08:00, now=10:00): the inside test
        // fires, so it opens with remaining = close-now (NOT the full duration). More
        // blocking (crux #2, jump-into).
        var now = OnA(DayOfWeek.Wednesday, 10, 0);
        var last = now.AddHours(-2);   // 08:00, before the window
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), last.ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Single(opens);
        Assert.Equal(7 * 3600, opens[0].RemainingSeconds);   // close(17:00) - now(10:00)
    }

    [Fact]
    public void LiveJumpOVER_AWholeWindow_Opens_ForTheFullDuration_SD4()
    {
        // The attacker rolls the wall 08:59 -> 17:01 in one ~10s tick: the traversal
        // crossed the window's open, now >= its close, and wallDelta(~8h) - mono(10s)
        // >> the ceiling => a jump. Opens for the FULL window duration (8h), anchored
        // in HighWater - you can't skip a window by leaping past it.
        var last = OnA(DayOfWeek.Wednesday, 8, 59);
        var now = last.AddHours(8).AddMinutes(2);   // 17:01 same day
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), last.ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Single(opens);
        Assert.Equal(8 * 3600, opens[0].RemainingSeconds);   // full 09:00-17:00
    }

    [Fact]
    public void ForwardTraversalOverAWindow_ThatIsRealElapsed_NotAJump_DoesNotOpen()
    {
        // The SAME 08:59->17:01 traversal but with monoElapsed also ~8h (genuine time
        // passing across a running service, e.g. the tick was delayed): wallDelta -
        // mono is small => NOT a jump => the past-and-closed window is not re-opened.
        var last = OnA(DayOfWeek.Wednesday, 8, 59);
        var now = last.AddHours(8).AddMinutes(2);
        long realElapsed = (long)(now - last).TotalSeconds;   // wall == mono: honest time
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), last.ToString(EnCa), now.ToString(EnCa), realElapsed, isBoot: false);
        Assert.Empty(opens);
    }

    [Fact]
    public void LiveJumpThatLandsBEFOREAWindow_DoesNotOpenIt()
    {
        // A jump 06:00 -> 08:00 (still before the 09:00 open): not inside, and the
        // traversal did not cross the window's open -> not open.
        var last = OnA(DayOfWeek.Wednesday, 6, 0);
        var now = last.AddHours(2);   // 08:00
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), last.ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Empty(opens);
    }

    [Fact]
    public void BootInsideAWindow_Opens_RemainingIsCloseMinusNow_Crux4a()
    {
        // OnStart (isBoot=True) while the wall clock is inside the window: opens off
        // the remainder. isBoot never computes a jump (no trustworthy monoElapsed
        // across a reboot), so it relies purely on the inside test.
        var now = OnA(DayOfWeek.Wednesday, 12, 0);
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), "", now.ToString(EnCa), 0, isBoot: true);
        Assert.Single(opens);
        Assert.Equal(5 * 3600, opens[0].RemainingSeconds);   // close(17:00) - now(12:00)
    }

    [Fact]
    public void BootAfterAClosedWindow_IsMissed_NotOpened_Crux4b()
    {
        // The one intended coverage gap: the machine was OFF across the window and
        // boots at 18:00 (past the 17:00 close). No monotonic continuity survives a
        // reboot to prove a jump, so retroactively starting a full block would be
        // punitive and useless - the window is simply MISSED.
        var now = OnA(DayOfWeek.Wednesday, 18, 0);
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), "", now.ToString(EnCa), 0, isBoot: true);
        Assert.Empty(opens);
    }

    [Fact]
    public void BeforeTheWindow_NormalTick_DoesNotOpen()
    {
        var now = OnA(DayOfWeek.Wednesday, 8, 0);   // 08:00, before 09:00
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), now.AddSeconds(-Mono).ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Empty(opens);
    }

    [Fact]
    public void AfterTheWindow_NormalTick_DoesNotOpen()
    {
        var now = OnA(DayOfWeek.Wednesday, 18, 0);   // 18:00, after 17:00, no jump
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), now.AddSeconds(-Mono).ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Empty(opens);
    }

    [Fact]
    public void DayMismatch_DoesNotOpen_EvenInsideTheClockWindow()
    {
        // Saturday 10:00 is inside 09:00-17:00 by time, but the schedule is Mon-Fri,
        // so the day mask excludes it -> not open.
        var now = OnA(DayOfWeek.Saturday, 10, 0);
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), now.AddSeconds(-Mono).ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Empty(opens);
    }

    [Fact]
    public void OverlappingWindows_BothOpen_EachWithItsOwnRemaining_Crux5()
    {
        // Two windows overlap at now=12:00: 09:00-17:00 (remaining 5h) and 10:00-14:00
        // (remaining 2h). BOTH open; the tick takes the LATER end (extend-never-shorten)
        // and enforces the union - here the evaluator just reports both.
        var windows = monkmode.Service1.ParseSchedule(
            "v1;1234567:0900-1700,1234567:1000-1400;sites=x.com;apps=").Windows;
        var now = OnA(DayOfWeek.Wednesday, 12, 0);
        var opens = monkmode.Service1.EvaluateWindows(
            windows, now.AddSeconds(-Mono).ToString(EnCa), now.ToString(EnCa), Mono, isBoot: false);
        Assert.Equal(2, opens.Count);
        var remainings = opens.Select(o => o.RemainingSeconds).OrderBy(x => x).ToArray();
        Assert.Equal(new long[] { 2 * 3600, 5 * 3600 }, remainings);
    }

    [Fact]
    public void UnparseableNow_OpensNothingNew_FailClosed()
    {
        // Without a parseable 'now' the evaluator can't convert a window; it opens
        // nothing this tick (any existing ActiveUntil still holds via ScheduleActive).
        var opens = monkmode.Service1.EvaluateWindows(
            MonFri9to5(), "", "garbage", Mono, isBoot: false);
        Assert.Empty(opens);
    }

    [Fact]
    public void EmptyWindowList_OpensNothing()
    {
        var opens = monkmode.Service1.EvaluateWindows(
            monkmode.Service1.ParseSchedule("").Windows, "", OnA(DayOfWeek.Wednesday, 12, 0).ToString(EnCa), Mono, isBoot: false);
        Assert.Empty(opens);
    }
}

// End-to-end through the REAL B4 gates: an open scheduled window's countdown is
// nothing but HighWater advancing at the honest tick rate toward the converted
// deadline. Composes NextHighWater + CapHighWaterAdvance (the live tick's exact pair)
// with the C5b gates, exactly like CoolOffEndToEndTests.
public class ScheduleEndToEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Ceiling = 120;  // Service1.HighWaterJumpCeilingSeconds
    // A Wednesday 09:00, inside a 09:00-09:30 window (short so the tick loop is small).
    private static DateTime T0()
    {
        var d = new DateTime(2026, 6, 25);
        while (d.DayOfWeek != DayOfWeek.Wednesday) d = d.AddDays(1);
        return d.AddHours(9);
    }

    private static string HonestTick(string hw, DateTime wallNow) =>
        monkmode.Service1.CapHighWaterAdvance(
            hw, monkmode.Service1.NextHighWater(hw, wallNow.ToString(EnCa), Ceiling), 10);

    [Fact]
    public void WindowOpens_Converts_CountsDown_EndsExactlyAtItsMonotonicClose()
    {
        var t0 = T0();
        var windows = monkmode.Service1.ParseSchedule("v1;1234567:0900-0930;sites=x.com;apps=").Windows;

        // First tick inside the window: convert to a HighWater-anchored deadline.
        var opens = monkmode.Service1.EvaluateWindows(windows, "", t0.ToString(EnCa), 10, isBoot: false);
        Assert.Single(opens);
        Assert.Equal(1800, opens[0].RemainingSeconds);   // a 30-minute window
        var hw = t0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeScheduleEnd(hw, opens[0].RemainingSeconds);
        Assert.True(monkmode.Service1.ScheduleActive(deadline, hw));   // held immediately

        // 179 honest 10s ticks (1790s): still held, not elapsed.
        for (int i = 1; i <= 179; i++) hw = HonestTick(hw, t0.AddSeconds(i * 10));
        Assert.True(monkmode.Service1.ScheduleActive(deadline, hw));
        Assert.False(monkmode.Service1.ScheduleElapsed(deadline, hw));

        // The 180th tick reaches T0+1800 = the window's monotonic close: elapsed, no
        // longer active. The guardian agrees (parity), so it won't resurrect it.
        hw = HonestTick(hw, t0.AddSeconds(1800));
        Assert.True(monkmode.Service1.ScheduleElapsed(deadline, hw));
        Assert.False(monkmode.Service1.ScheduleActive(deadline, hw));
        Assert.False(mm_guard.Guardian.ScheduleActive(deadline, hw));
    }

    [Fact]
    public void MidWindowClockForward_IsRefused_TheWindowDoesNotEndEarly()
    {
        // The headline never-skip guarantee for schedules: jump the wall clock 2h past
        // the window close - NextHighWater classifies a ForwardJump and keeps the
        // stored mark, so the converted deadline is NOT reached and the window holds.
        var t0 = T0();
        var hw = t0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeScheduleEnd(hw, 1800);   // closes at 09:30

        var jumped = monkmode.Service1.NextHighWater(hw, t0.AddHours(2).ToString(EnCa), Ceiling);
        Assert.Equal(hw, jumped);   // the jump was refused
        Assert.False(monkmode.Service1.ScheduleElapsed(deadline, jumped));
        Assert.True(monkmode.Service1.ScheduleActive(deadline, jumped));   // still held
    }

    [Fact]
    public void ClockCreep_IsCappedToRealElapsed_TheWindowIsNotShortened()
    {
        // The creep attack against a window: nudge the wall +119s (within-ceiling =
        // Trusted) before each 10s real tick. CapHighWaterAdvance credits only the real
        // ~10s per tick, so after 60 real ticks the mark moved 10 min - far short of
        // the 30-min window.
        var t0 = T0();
        var hw = t0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeScheduleEnd(hw, 1800);
        for (int i = 1; i <= 60; i++)
        {
            var wall = DateTime.Parse(hw, EnCa).AddSeconds(119);
            var candidate = monkmode.Service1.NextHighWater(hw, wall.ToString(EnCa), Ceiling);
            hw = monkmode.Service1.CapHighWaterAdvance(hw, candidate, 10);
        }
        Assert.False(monkmode.Service1.ScheduleElapsed(deadline, hw));
        Assert.Equal(t0.AddSeconds(600).ToString(EnCa), hw);   // only the real 600s
    }
}
