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
// Sub-slice (b1) folded the schedule HOLD into EffectiveExit/ClassifyHeartbeat (an open
// window out-ranks every lift trigger, SD1) with all call sites reading the STORED
// ActiveUntil - provably inert while nothing writes a non-empty one.
//
// Sub-slice (b2) [THIS slice's additions below] ships the tick DECISION that first writes
// a non-empty ActiveUntil - the window->duration conversion (design §6.1):
//   - LaterScheduleEnd: the extend-never-shorten primitive (overlap => longest end, SD2;
//     a corrupt/"" value never shortens a hold);
//   - NextScheduleActiveUntil: the pure open/extend/clear/steady decision the service
//     persists (ProcessScheduleWindows is the thin file-I/O wrapper around it, exactly as
//     ClassifyCoolOffSignal relates to ProcessCoolOffSignals - the wrapper is smoke-only);
//   - end-to-end compositions through the REAL gates (EvaluateWindows -> the decision ->
//     ScheduleActive/Elapsed + NextHighWater/CapHighWaterAdvance): a window opens ->
//     converts -> counts down -> clears at its monotonic close; a mid-window clock-forward
//     can't end it early; jump-into/over; boot re-hold (inside re-opens, past missed,
//     reboot mid-window survives with downtime never credited); overlap union + longest
//     end; and SD1 proven end-to-end (an open window holds through an expired manual block
//     + elapsed cooling-off + an unlocked code, service<->guardian parity).
//
// Still the (b3)/(b4)/(c) seam (smoke-tested at CV, like the C2b/C3b live wiring): the
// site/app UNION enforcement behind BlockHeld (b3), the guardian ActiveUntil read (b4),
// and the CLI `schedule` front-end (c). No CLI writes a Spec until (c), so production
// stays inert through (b2) - these tests exercise the machinery with an injected Spec.

using System.Globalization;
using System.IO;

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

    // C5b (c2): the schedule-only past-[Time] Until SENTINEL (design §4B). It must be a REAL
    // en-CA datetime that parses, be clearly PAST (so BlockHasExpired(sentinel) is always True
    // -> BlockHeld collapses to its ScheduleActive disjunct, fixing P1's self-heal latch), and
    // be STABLE (a fixed literal, not an arm-time-now that would drift on every re-stamp). The
    // CLI (c3) writes exactly this value; a CLI<->service parity test will pin the copies then.
    [Fact]
    public void ScheduleOnlyExpiredUntil_IsAFixedClearlyPastEnCaDatetime()
    {
        var enCa = new CultureInfo("en-CA");
        Assert.Equal("1970-01-01 00:00:00", monkmode.Service1.ScheduleOnlyExpiredUntil);
        // Parses under en-CA (so BlockHasExpired/EffectiveExit read it, never fail-closed-hold on it).
        Assert.True(DateTime.TryParse(monkmode.Service1.ScheduleOnlyExpiredUntil, enCa, DateTimeStyles.None, out _));
        // Always reads EXPIRED, for any realistic HighWater (hugely negative DateDiff <= grace).
        Assert.True(monkmode.Service1.BlockHasExpired(monkmode.Service1.ScheduleOnlyExpiredUntil, new DateTime(2026, 7, 3, 12, 0, 0), 5));
        Assert.True(monkmode.Service1.BlockHasExpired(monkmode.Service1.ScheduleOnlyExpiredUntil, new DateTime(1970, 1, 2, 0, 0, 0), 5));
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

// ===== C5b sub-slice (b2): the tick decision =====

// LaterScheduleEnd: extend-never-shorten. A newly-opened window can only PUSH the
// schedule deadline out, never pull it in - and a corrupt/"" value never shortens a hold.
public class LaterScheduleEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Base = new(2026, 6, 25, 12, 0, 0);
    private static string At(int mins) => Base.AddMinutes(mins).ToString(EnCa);

    [Fact]
    public void EmptyAccumulator_TakesTheNewEnd()
    {
        // later("", e) = e: a first open sets the deadline.
        Assert.Equal(At(30), monkmode.Service1.LaterScheduleEnd("", At(30)));
    }

    [Fact]
    public void EmptyNewEnd_KeepsTheAccumulator()
    {
        // later(e, "") = e: a window whose ComputeScheduleEnd failed (unparseable HighWater
        // -> "") leaves the current deadline untouched (retry next tick).
        Assert.Equal(At(30), monkmode.Service1.LaterScheduleEnd(At(30), ""));
    }

    [Fact]
    public void ReturnsTheLater_RegardlessOfArgumentOrder_ExtendNeverShorten()
    {
        Assert.Equal(At(60), monkmode.Service1.LaterScheduleEnd(At(30), At(60)));   // extend out
        Assert.Equal(At(60), monkmode.Service1.LaterScheduleEnd(At(60), At(30)));   // never pulled in
    }

    [Fact]
    public void EqualEnds_ReturnTheAccumulatorVerbatim_NoSpuriousChange()
    {
        // Equal -> returns 'a' byte-for-byte, so a steady tick is string-identical to the
        // stored value (ProcessScheduleWindows then writes nothing).
        Assert.Equal(At(30), monkmode.Service1.LaterScheduleEnd(At(30), At(30)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("25.06.2026 11:00:00")]   // legacy de-DE format the en-CA parse rejects; and
                                          // 11:00 is BEFORE At(30)=12:30, so a wrongful parse
                                          // would flip the result to At(30) and FAIL the test
    public void UnparseableAccumulator_IsKept_NeverReplacedByAShorterParseableEnd(string corrupt)
    {
        // A corrupt current ActiveUntil already reads as a permanent HOLD (ScheduleActive
        // true). LaterScheduleEnd must NEVER replace it with a parseable end - that would
        // turn a fail-closed hold into a liftable window.
        Assert.Equal(corrupt, monkmode.Service1.LaterScheduleEnd(corrupt, At(30)));
    }

    [Fact]
    public void UnparseableNewEnd_KeepsTheAccumulator()
    {
        Assert.Equal(At(30), monkmode.Service1.LaterScheduleEnd(At(30), "garbage"));
    }
}

// NextScheduleActiveUntil: the pure open/extend/clear/steady decision the service persists
// each tick (ProcessScheduleWindows is the thin file-I/O wrapper around it).
public class NextScheduleActiveUntilTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private static string HwText => Hw.ToString(EnCa);

    private static System.Collections.Generic.List<monkmode.Service1.ScheduleOpen> Opens(params long[] remainings)
    {
        var list = new System.Collections.Generic.List<monkmode.Service1.ScheduleOpen>();
        foreach (var r in remainings) list.Add(new monkmode.Service1.ScheduleOpen { RemainingSeconds = r });
        return list;
    }

    [Fact]
    public void NoWindowOpen_NoCurrent_StaysEmpty()
    {
        Assert.Equal("", monkmode.Service1.NextScheduleActiveUntil("", Opens(), HwText));
        Assert.Equal("", monkmode.Service1.NextScheduleActiveUntil("", null, HwText));
    }

    [Fact]
    public void WindowOpens_FromNoCurrent_ConvertsToHighWaterPlusRemaining()
    {
        // First open: ActiveUntil = HighWater + remaining (the window->duration conversion).
        var r = monkmode.Service1.NextScheduleActiveUntil("", Opens(1800), HwText);
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(HwText, 1800), r);
        Assert.True(monkmode.Service1.ScheduleActive(r, HwText));
    }

    [Fact]
    public void OverlappingOpens_TakeTheLongestEnd_SD2()
    {
        // Two windows open this tick (2h and 5h remaining): the deadline is the LATER end
        // (union held until the longest end).
        var r = monkmode.Service1.NextScheduleActiveUntil("", Opens(7200, 18000), HwText);
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(HwText, 18000), r);
    }

    [Fact]
    public void ANewShorterWindow_NeverShortensAnExistingLaterDeadline()
    {
        // current = Hw+5h; a shorter 2h window opens -> extend-never-shorten keeps the later
        // end (the shorter overlapping window is subsumed, SD2).
        var current = monkmode.Service1.ComputeScheduleEnd(HwText, 18000);
        Assert.Equal(current, monkmode.Service1.NextScheduleActiveUntil(current, Opens(7200), HwText));
    }

    [Fact]
    public void ALaterWindow_ExtendsTheCurrentDeadline()
    {
        var current = monkmode.Service1.ComputeScheduleEnd(HwText, 7200);        // Hw+2h
        var r = monkmode.Service1.NextScheduleActiveUntil(current, Opens(18000), HwText);   // Hw+5h opens
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(HwText, 18000), r);
    }

    [Fact]
    public void CurrentDeadlineNotYetElapsed_NoWindowOpen_StaysSteady_ClearingSpecNeverShortens()
    {
        // The "clear the schedule while a window is open" case: no window evaluates open this
        // tick (e.g. Spec was cleared) but the current deadline hasn't elapsed -> it is KEPT
        // (runs to its monotonic close). Never shortened by a rule edit.
        var current = monkmode.Service1.ComputeScheduleEnd(HwText, 1800);   // Hw+30m, future
        Assert.Equal(current, monkmode.Service1.NextScheduleActiveUntil(current, Opens(), HwText));
    }

    [Fact]
    public void CurrentDeadlineElapsed_NoWindowOpen_Clears()
    {
        // The window reached its monotonic close and nothing is open now -> clear to "".
        var elapsed = Hw.AddMinutes(-1).ToString(EnCa);   // <= HighWater -> elapsed
        Assert.Equal("", monkmode.Service1.NextScheduleActiveUntil(elapsed, Opens(), HwText));
    }

    [Fact]
    public void CurrentDeadlineElapsed_ButAWindowStillOpen_DoesNotClear_ReOpensInstead()
    {
        // An elapsed deadline BUT a window is open this tick: the extend branch out-ranks
        // clear, so it never blips closed - it re-opens to the new end.
        var elapsed = Hw.AddMinutes(-1).ToString(EnCa);
        var r = monkmode.Service1.NextScheduleActiveUntil(elapsed, Opens(1800), HwText);
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(HwText, 1800), r);
        Assert.True(monkmode.Service1.ScheduleActive(r, HwText));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableHighWater_NoExtendNoClear_HoldsAndRetries(string hw)
    {
        // Fail-closed: no trustworthy mark -> ComputeScheduleEnd yields "" (no extend) and
        // ScheduleElapsed is false (no clear). A current deadline is kept; "" stays "".
        var current = monkmode.Service1.ComputeScheduleEnd(HwText, 1800);
        Assert.Equal(current, monkmode.Service1.NextScheduleActiveUntil(current, Opens(1800), hw));
        Assert.Equal("", monkmode.Service1.NextScheduleActiveUntil("", Opens(1800), hw));
    }
}

// End-to-end: the tick DECISION driven through the REAL gates (EvaluateWindows ->
// NextScheduleActiveUntil -> ScheduleActive/Elapsed, HighWater advanced by the live
// NextHighWater + CapHighWaterAdvance pair). This is exactly what ProcessScheduleWindows
// computes each tick minus the file I/O (smoke-only), mirroring CoolOffEndToEndTests.
public class ScheduleTickDecisionEndToEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Ceiling = 120;   // Service1.HighWaterJumpCeilingSeconds

    // A Wednesday 09:00. All-days windows so the weekday never excludes a tick.
    private static DateTime T0()
    {
        var d = new DateTime(2026, 6, 25);
        while (d.DayOfWeek != DayOfWeek.Wednesday) d = d.AddDays(1);
        return d.AddHours(9);
    }
    private static System.Collections.Generic.List<monkmode.Service1.ScheduleWindow> W(string spec) =>
        monkmode.Service1.ParseSchedule(spec).Windows;
    private static string HonestTick(string hw, DateTime wallNow) =>
        monkmode.Service1.CapHighWaterAdvance(hw, monkmode.Service1.NextHighWater(hw, wallNow.ToString(EnCa), Ceiling), 10);

    [Fact]
    public void WindowOpens_Converts_CountsDown_ThenClearsAtItsMonotonicClose()
    {
        var t0 = T0();
        var win = W("v1;1234567:0900-0930;sites=x.com;apps=");   // 30-minute window
        string activeUntil = "", hw = t0.ToString(EnCa), lastNow = "";

        // Tick 1 at 09:00: inside the window -> open + convert to hw+1800.
        string now = t0.ToString(EnCa);
        activeUntil = monkmode.Service1.NextScheduleActiveUntil(
            activeUntil, monkmode.Service1.EvaluateWindows(win, lastNow, now, 10, false), hw);
        lastNow = now;
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(t0.ToString(EnCa), 1800), activeUntil);
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));

        // 179 honest 10s ticks: still inside the window, the decision re-derives the SAME
        // deadline every tick (extend-never-shorten) -> steady, still held.
        for (int i = 1; i <= 179; i++)
        {
            var wall = t0.AddSeconds(i * 10);
            hw = HonestTick(hw, wall);
            now = wall.ToString(EnCa);
            activeUntil = monkmode.Service1.NextScheduleActiveUntil(
                activeUntil, monkmode.Service1.EvaluateWindows(win, lastNow, now, 10, false), hw);
            lastNow = now;
        }
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(t0.ToString(EnCa), 1800), activeUntil);   // unchanged
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));

        // Tick 180 reaches hw = t0+1800 and wall = 09:30 (window closed): no window open +
        // deadline elapsed -> the decision CLEARS ActiveUntil; no longer active. Guardian agrees.
        var w180 = t0.AddSeconds(1800);
        hw = HonestTick(hw, w180);
        var opens180 = monkmode.Service1.EvaluateWindows(win, lastNow, w180.ToString(EnCa), 10, false);
        Assert.Empty(opens180);
        activeUntil = monkmode.Service1.NextScheduleActiveUntil(activeUntil, opens180, hw);
        Assert.Equal("", activeUntil);
        Assert.False(monkmode.Service1.ScheduleActive(activeUntil, hw));
        Assert.False(mm_guard.Guardian.ScheduleActive(activeUntil, hw));
    }

    [Fact]
    public void MidWindowClockForward_TheDecisionNeverEndsEarly()
    {
        // Open the window, then jump the wall +2h. HighWater refuses the jump, so the
        // decision sees an unchanged hw < ActiveUntil -> not elapsed -> not cleared.
        var t0 = T0();
        var win = W("v1;1234567:0900-0930;sites=x.com;apps=");
        string hw = t0.ToString(EnCa);
        string activeUntil = monkmode.Service1.NextScheduleActiveUntil(
            "", monkmode.Service1.EvaluateWindows(win, "", t0.ToString(EnCa), 10, false), hw);
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));

        var jumpWall = t0.AddHours(2);
        var hwAfter = HonestTick(hw, jumpWall);
        Assert.Equal(hw, hwAfter);   // jump refused: HighWater unchanged
        var opens = monkmode.Service1.EvaluateWindows(win, t0.ToString(EnCa), jumpWall.ToString(EnCa), 10, false);
        var after = monkmode.Service1.NextScheduleActiveUntil(activeUntil, opens, hwAfter);
        Assert.Equal(activeUntil, after);   // unchanged (not cleared, not shortened)
        Assert.True(monkmode.Service1.ScheduleActive(after, hwAfter));   // still held
    }

    [Fact]
    public void DstSpringForwardInsideAWindow_OverRuns_NeverEndsEarly()
    {
        // A +1h DST-like forward shift mid-window is a >ceiling ForwardJump -> HighWater
        // refuses it -> the monotonic countdown doesn't jump -> the window over-runs the
        // wall close rather than ending early (Q4, LOCAL wall-clock).
        var t0 = T0();
        string hw = t0.ToString(EnCa);
        string activeUntil = monkmode.Service1.ComputeScheduleEnd(hw, 1800);   // 30-min window
        var shifted = HonestTick(hw, t0.AddHours(1));
        Assert.Equal(hw, shifted);   // refused
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, shifted));   // over-runs, still held
    }

    [Fact]
    public void DstFallBackInsideAWindow_OverWaits_NeverEndsEarly()
    {
        // A -1h DST-like backward shift: NextHighWater classifies Backward and keeps the
        // stored mark (HighWater freezes until the wall catches up) -> the window over-waits
        // its real duration, never ends early.
        var t0 = T0();
        string hw = t0.ToString(EnCa);
        string activeUntil = monkmode.Service1.ComputeScheduleEnd(hw, 1800);
        var rolledBack = HonestTick(hw, t0.AddHours(-1));
        Assert.Equal(hw, rolledBack);   // frozen (backward roll not credited)
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, rolledBack));   // over-waits, still held
    }

    [Fact]
    public void LiveJumpOverAWholeWindow_OpensItForTheFullDuration_SD4()
    {
        // The wall leaps 08:59 -> 17:01 in one ~10s real tick across a 09:00-17:00 window:
        // EvaluateWindows flags the jump-over -> the decision opens the FULL 8h duration,
        // HighWater-anchored (you can't skip a window by leaping past it).
        var t0d = new DateTime(2026, 6, 25); while (t0d.DayOfWeek != DayOfWeek.Wednesday) t0d = t0d.AddDays(1);
        var win = W("v1;1234567:0900-1700;sites=x.com;apps=");
        var last = t0d.AddHours(8).AddMinutes(59);
        var now = last.AddHours(8).AddMinutes(2);   // 17:01
        string hw = last.ToString(EnCa);
        var opens = monkmode.Service1.EvaluateWindows(win, last.ToString(EnCa), now.ToString(EnCa), 10, false);
        Assert.Single(opens);
        Assert.Equal(8 * 3600, opens[0].RemainingSeconds);
        var activeUntil = monkmode.Service1.NextScheduleActiveUntil("", opens, hw);
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(hw, 8 * 3600), activeUntil);
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));
    }

    [Fact]
    public void TwoOverlappingWindows_HoldUntilTheLongestEnd_SD2()
    {
        // 09:00-17:00 and 10:00-14:00 overlap at 12:00; the decision holds until the LONGER
        // end (17:00's remaining), the shorter subsumed (union over-block).
        var t0d = new DateTime(2026, 6, 25); while (t0d.DayOfWeek != DayOfWeek.Wednesday) t0d = t0d.AddDays(1);
        var win = W("v1;1234567:0900-1700,1234567:1000-1400;sites=x.com;apps=");
        var now = t0d.AddHours(12);   // remaining 5h and 2h
        string hw = now.ToString(EnCa);
        var opens = monkmode.Service1.EvaluateWindows(win, now.AddSeconds(-10).ToString(EnCa), now.ToString(EnCa), 10, false);
        Assert.Equal(2, opens.Count);
        var activeUntil = monkmode.Service1.NextScheduleActiveUntil("", opens, hw);
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(hw, 5 * 3600), activeUntil);   // the 5h (longest) end
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));
    }

    [Fact]
    public void BootInsideAWindow_ReOpensOffStoredHighWater_Crux4a()
    {
        // OnStart (isBoot=True) with the wall clock inside the window and no prior deadline
        // (e.g. the backup didn't carry it): re-open off the STORED HighWater, remaining =
        // close - now.
        var t0 = T0();   // window 09:00-09:30
        var win = W("v1;1234567:0900-0930;sites=x.com;apps=");
        string storedHw = t0.AddSeconds(50).ToString(EnCa);
        var bootWall = t0.AddMinutes(10);   // 09:10 -> 20 min remaining
        var opens = monkmode.Service1.EvaluateWindows(win, "", bootWall.ToString(EnCa), 0, isBoot: true);
        var activeUntil = monkmode.Service1.NextScheduleActiveUntil("", opens, storedHw);
        Assert.Equal(monkmode.Service1.ComputeScheduleEnd(storedHw, 1200), activeUntil);
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, storedHw));
    }

    [Fact]
    public void BootPastANeverOpenedWindow_IsMissed_Crux4b()
    {
        // The one intended coverage gap: off through the whole window, boot past its close
        // with no prior deadline -> missed (no retroactive block).
        var t0 = T0();
        var win = W("v1;1234567:0900-0930;sites=x.com;apps=");
        var opens = monkmode.Service1.EvaluateWindows(win, "", t0.AddHours(2).ToString(EnCa), 0, isBoot: true);
        var activeUntil = monkmode.Service1.NextScheduleActiveUntil("", opens, t0.ToString(EnCa));
        Assert.Equal("", activeUntil);
        Assert.False(monkmode.Service1.ScheduleActive(activeUntil, t0.ToString(EnCa)));
    }

    [Fact]
    public void RebootMidWindow_DeadlineSurvives_DowntimeNeverCredited()
    {
        // A window opened pre-reboot (ActiveUntil = hw+1800). The machine is OFF (HighWater
        // does NOT advance across downtime), then boots with the wall already past the
        // window's wall-close. Boot re-evaluation opens nothing (past close, no jump across a
        // reboot) but must NOT clear the still-unelapsed deadline: the window paused while off
        // and resumes on boot (over-run, never an early lift).
        var t0 = T0();
        var win = W("v1;1234567:0900-0930;sites=x.com;apps=");
        string storedHw = t0.AddSeconds(300).ToString(EnCa);   // 5 min of ticks, then off
        string activeUntil = monkmode.Service1.ComputeScheduleEnd(t0.ToString(EnCa), 1800);  // closes at hw t0+1800
        var opens = monkmode.Service1.EvaluateWindows(win, "", t0.AddHours(2).ToString(EnCa), 0, isBoot: true);
        Assert.Empty(opens);
        var after = monkmode.Service1.NextScheduleActiveUntil(activeUntil, opens, storedHw);
        Assert.Equal(activeUntil, after);   // NOT cleared (t0+1800 > storedHw t0+300)
        Assert.True(monkmode.Service1.ScheduleActive(after, storedHw));
    }

    [Fact]
    public void FirstLiveTickAfterReboot_UsesTheBootSeededAnchor_MissedWindowStaysMissed_Crux4b()
    {
        // The reboot-correctness contract the b2 wiring must satisfy (the bug the in-memory
        // lastTickWallNow anchor fixes): OnStart seeds the wall anchor to BOOT time - NOT the
        // stale pre-reboot [CurrentTime] Now. So the FIRST LIVE tick (isBoot=false) sees
        // lastNow ~= 10s ago with monoElapsed ~= 10s => NO false jump-over => a window that
        // closed while the machine was OFF stays MISSED (crux #4b).
        var t0d = new DateTime(2026, 6, 25); while (t0d.DayOfWeek != DayOfWeek.Wednesday) t0d = t0d.AddDays(1);
        var win = W("v1;1234567:0900-1700;sites=x.com;apps=");
        var bootNow = t0d.AddHours(18);              // booted at 18:00, past the 17:00 close
        var firstTickNow = bootNow.AddSeconds(10);   // first live tick ~10s after boot
        // Correct anchor (boot-seeded): lastNow ~= 10s ago, mono = 10s -> no jump -> missed.
        var correct = monkmode.Service1.EvaluateWindows(
            win, bootNow.ToString(EnCa), firstTickNow.ToString(EnCa), 10, isBoot: false);
        Assert.Empty(correct);
        var afterCorrect = monkmode.Service1.NextScheduleActiveUntil("", correct, bootNow.ToString(EnCa));
        Assert.Equal("", afterCorrect);   // stays missed, no block

        // Documents the failure mode the anchor prevents: feeding the STALE pre-reboot now
        // (08:00) with the reset mono (10s) reads the whole downtime as a live jump and
        // WRONGLY re-opens the window - which is exactly why the tick must pass the in-memory
        // anchor, never the stored [CurrentTime] Now.
        var buggy = monkmode.Service1.EvaluateWindows(
            win, t0d.AddHours(8).ToString(EnCa), firstTickNow.ToString(EnCa), 10, isBoot: false);
        Assert.Single(buggy);
    }

    [Fact]
    public void SD1_OpenWindow_HoldsThroughExpiredManual_ElapsedCoolOff_AndUnlockedCode()
    {
        // SD1 proven end-to-end through the b2 decision: with a window open, EffectiveExit
        // HOLDS even though the manual block expired, cooling-off elapsed, and a code
        // unlocked - the open window out-ranks all three lift triggers (design §6.2). Service
        // and guardian agree (parity); removing the window LIFTS (the control).
        var t0 = T0();
        string hw = t0.ToString(EnCa);
        var win = W("v1;1234567:0900-1700;sites=x.com;apps=");   // long window, open now
        var activeUntil = monkmode.Service1.NextScheduleActiveUntil(
            "", monkmode.Service1.EvaluateWindows(win, "", t0.ToString(EnCa), 10, false), hw);
        Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));

        string pastUntil = t0.AddHours(-1).ToString(EnCa);
        string elapsedCoolOff = t0.AddHours(-1).ToString(EnCa);
        string unlockedCode = t0.AddMinutes(-5).ToString(EnCa);

        Assert.False(monkmode.Service1.EffectiveExit(pastUntil, elapsedCoolOff, unlockedCode, activeUntil, hw, 5, macValid: true, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(pastUntil, elapsedCoolOff, unlockedCode, activeUntil, hw, 5, macValid: true, scheduleArmed: false));
        // Control: no window -> the same expired/elapsed/unlocked state LIFTS.
        Assert.True(monkmode.Service1.EffectiveExit(pastUntil, elapsedCoolOff, unlockedCode, "", hw, 5, macValid: true, scheduleArmed: false));
    }
}

// ===== C5b sub-slice (b3-i): the enforce-while widening + the app-kill union =====
//
// (b3-i) wires the schedule hold into ENFORCEMENT (design §6.3), the non-hosts portion of
// the meaty half: at the 5 self-heal I/O gates (hosts self-heal, guardian-spawn, SafeBoot
// re-assert, DoH-off, deny-DELETE ACE) the bare `Not EffectiveBlockHasExpired` becomes the
// shared `BlockHeld` (so an open window keeps all 5 enforcing past manual expiry, SD1); and
// the per-tick app-kill loop iterates the UNION of the manual [Process] List and (only while
// a window is open) the schedule's apps (SD2). Both are GATED on ScheduleActive, so a
// no-schedule block (ActiveUntil="" - every block until the CLI writes a Spec in slice (c))
// is BYTE-IDENTICAL to today. The hosts self-heal UNION TARGET (snapshot union synthesised
// schedule site entries - the service's first stateful hosts writes for a block it armed
// itself) is the isolated sub-slice (b3-ii), which gets its own focused verifier + CV smoke.
//
// The 5-site BlockHeld swap does live registry/SCM/hosts I/O (not unit-testable here); its
// SAFETY is the pure invariant below - BlockHeld with an empty ActiveUntil is EXACTLY
// `Not EffectiveBlockHasExpired`, so the swap changes nothing for a no-schedule block - plus
// the (b3-ii)/CV smoke. EffectiveKillList (the app-kill union) is fully pure + tested here.

// The (b3-i) enforce-while swap is safe because BlockHeld with no open window degenerates to
// the exact gate it replaced. Pin that equivalence so a future refactor can't drift it.
public class BlockHeldNoScheduleEquivalenceTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime AsOf = new(2026, 6, 25, 12, 0, 0);
    private static string Hw => AsOf.ToString(EnCa);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyActiveUntil_BlockHeld_ExactlyMatches_NotEffectiveBlockHasExpired(bool macValid)
    {
        // At every self-heal site, replacing `Not EffectiveBlockHasExpired(...)` with
        // `BlockHeld(..., "", newHw)` must be a BYTE-IDENTICAL no-op for a block with no open
        // scheduled window (ActiveUntil="" - every block today until slice (c) writes a Spec).
        // Only an OPEN window adds enforcement (BlockHeldTests covers that divergence).
        foreach (var until in new[]
        {
            AsOf.AddHours(-1).ToString(EnCa),   // expired
            AsOf.AddHours(5).ToString(EnCa),    // active
            AsOf.ToString(EnCa),                // exactly at asOf
            "garbage",                          // unparseable -> fail-closed held
            "",                                 // blank -> fail-closed held
        })
        {
            Assert.Equal(
                !monkmode.Service1.EffectiveBlockHasExpired(until, AsOf, 5, macValid),
                monkmode.Service1.BlockHeld(until, AsOf, 5, macValid, "", Hw));
        }
    }
}

// EffectiveKillList: the pure app-kill UNION (design §6.3). Manual [Process] List, plus - ONLY
// while a scheduled window is open - the schedule's apps; returned as the same delimited,
// .Contains-searchable string the kill loop uses, byte-identical to the manual list otherwise.
public class EffectiveKillListTests
{
    private static System.Collections.Generic.List<string> Apps(params string[] a) =>
        new System.Collections.Generic.List<string>(a);

    [Fact]
    public void NotScheduleActive_ReturnsManualListVerbatim_ByteIdentical_EvenWithApps()
    {
        // With no open window the kill set is EXACTLY the manual list - even if schedule apps
        // are (defensively) passed, they are ignored. This is the no-schedule byte-identity.
        Assert.Equal("chrome.exe,firefox.exe",
            monkmode.Service1.EffectiveKillList("chrome.exe,firefox.exe", Apps("brave.exe"), scheduleActive: false));
    }

    [Fact]
    public void ScheduleActive_NoScheduleApps_ReturnsManualListVerbatim()
    {
        // A window is open but the Spec lists no apps: still byte-identical (null and empty
        // are both no-ops), so an apps-less schedule never disturbs the manual kill set.
        Assert.Equal("chrome.exe", monkmode.Service1.EffectiveKillList("chrome.exe", null, scheduleActive: true));
        Assert.Equal("chrome.exe", monkmode.Service1.EffectiveKillList("chrome.exe", Apps(), scheduleActive: true));
    }

    [Fact]
    public void ScheduleActive_UnionsManualAndScheduleApps_AllMatchedUnderContains()
    {
        // While a window is open the kill set is the UNION; every manual AND schedule app must
        // be matched by the loop's `.Contains(name + ".exe")` test.
        var kl = monkmode.Service1.EffectiveKillList("chrome.exe", Apps("brave.exe", "discord.exe"), scheduleActive: true);
        Assert.Contains("chrome.exe", kl);    // manual app still matched
        Assert.Contains("brave.exe", kl);     // schedule apps added
        Assert.Contains("discord.exe", kl);
    }

    [Fact]
    public void ScheduleOnlyBlock_NullManualSentinel_ScheduleAppsAreTheKillSet()
    {
        // A schedule-only block has no manual [Process] List (the "null" sentinel); while its
        // window is open the schedule's apps ARE the kill set.
        var kl = monkmode.Service1.EffectiveKillList("null", Apps("chrome.exe"), scheduleActive: true);
        Assert.Contains("chrome.exe", kl);
    }

    [Fact]
    public void UnionIsManualThenScheduleApps_PipeJoined()
    {
        // Exact serialisation: the manual list, then each schedule app, "|"-joined (distinct,
        // non-substring names so the assertion is purely about the join format). The "|" matches
        // the Spec's own app separator and can't appear in an exe name, so appending never fuses
        // two names into one token (the loop's pre-existing substring .Contains is unchanged).
        Assert.Equal("chrome.exe|discord.exe",
            monkmode.Service1.EffectiveKillList("chrome.exe", Apps("discord.exe"), scheduleActive: true));
    }
}

// ===== C5b sub-slice (b3-ii): the hosts self-heal UNION =====
//
// The meaty stateful half of design §6.3 - the service's FIRST stateful hosts writes for a block
// it armed itself. While a scheduled window is open the per-tick hosts self-heal repairs the
// marker block to the UNION of the manual block's snapshot entries and the schedule's OWN site
// entries, synthesised in the CLI's exact hosts-line format:
//   - the synthesiser (Service1.BuildHostsEntries) is a byte-for-byte PARITY copy of the CLI's
//     Blocker.BuildHostsEntries (separate assemblies, parity-pinned like StripMonkModeBlock);
//   - EffectiveHostsBlock computes the effective marker block: snapshot VERBATIM when inert (the
//     no-schedule byte-identity), the synthesised entries alone for a SCHEDULE-ONLY block (no
//     snapshot), or snapshot UNION schedule entries (line-dedup) for an OVERLAP.
// The live tick (reading the snapshot + calling RepairHostsBlock/AtomicHosts) is the smoke-only
// seam (fence: unit tests never touch the real hosts); its SAFETY is these pure invariants plus
// the RepairHostsBlock/StripMonkModeBlock round-trips below - deterministic target => NO churn
// once written, and stopMe()'s strip removes the WHOLE union cleanly (no data loss).

// Blocker.BuildHostsEntries (CLI) and Service1.BuildHostsEntries (service) must be byte-for-byte
// identical, or a schedule-only block's synthesised hosts would not match a manual block's format
// (mis-block, or a block the expiry strip can't cleanly recognise).
public class HostsEntriesParityTests
{
    [Theory]
    [InlineData("reddit.com")]                // bare 2LD -> also gets the www./m./web./mobile. mirrors
    [InlineData("news.ycombinator.com")]      // subdomain (two dots) -> no mirror variants
    [InlineData("www.reddit.com")]            // already www. -> no extra variants
    [InlineData("Reddit.COM")]                // uppercase -> lowercased
    [InlineData("HTTPS://reddit.com/r/all")]  // pasted URL -> scheme + path stripped
    [InlineData("  spaced.com  ")]            // surrounding whitespace trimmed
    public void ServiceSynthesiser_MatchesCli_ByteForByte_SingleDomain(string domain)
    {
        var domains = new[] { domain };
        Assert.Equal(MonkMode.Blocker.BuildHostsEntries(domains),
                     monkmode.Service1.BuildHostsEntries(domains));
    }

    [Fact]
    public void ServiceSynthesiser_MatchesCli_MultipleDomains_DropsEmpties()
    {
        var domains = new[] { "reddit.com", "", "news.ycombinator.com", "   ", "x.com" };
        Assert.Equal(MonkMode.Blocker.BuildHostsEntries(domains),
                     monkmode.Service1.BuildHostsEntries(domains));
        // ...and a schedule-only synthesised block is byte-identical to the block the CLI would
        // write for the same sites (marker + entries), so the marker parity is pinned too.
        Assert.Equal(MonkMode.Blocker.BuildMonkModeBlock(domains),
                     monkmode.Service1.EffectiveHostsBlock("", new System.Collections.Generic.List<string>(domains), scheduleActive: true));
    }

    [Fact]
    public void EmptyDomainList_ProducesNoEntries_BothCopies()
    {
        var none = System.Array.Empty<string>();
        Assert.Equal("", monkmode.Service1.BuildHostsEntries(none));
        Assert.Equal(MonkMode.Blocker.BuildHostsEntries(none), monkmode.Service1.BuildHostsEntries(none));
    }
}

public class EffectiveHostsBlockTests
{
    private const string Marker = "#### MonkMode Entries ####";
    // A manual block's snapshot (marker + CRLF-terminated entries), exactly as the CLI persists it.
    // Built through BuildHostsEntries so it carries the SAME mirror expansion (reddit.com plus its
    // www./m./web./mobile. lines) the CLI actually writes - a hand-listed subset would leave the
    // union tests appending mirrors the real snapshot already holds.
    private static readonly string ManualSnapshot =
        Marker + "\r\n" + MonkMode.Blocker.BuildHostsEntries(new[] { "reddit.com" });
    private static System.Collections.Generic.List<string> Sites(params string[] s) =>
        new System.Collections.Generic.List<string>(s);

    [Fact]
    public void NotActive_ReturnsSnapshotVerbatim_ByteIdentical_EvenWithSites()
    {
        // The no-schedule byte-identity: with no open window the repair target is the snapshot
        // verbatim (the SAME reference - no allocation), even if schedule sites are (defensively)
        // passed. This is what makes every block today byte-identical to before (b3-ii inert path).
        Assert.Same(ManualSnapshot, monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites("x.com"), scheduleActive: false));
    }

    [Fact]
    public void Active_NoScheduleSites_ReturnsSnapshotVerbatim()
    {
        // A window is open but the Spec lists no sites: still the snapshot verbatim (null and empty
        // are both no-ops), so an apps-only schedule never disturbs the manual hosts block.
        Assert.Same(ManualSnapshot, monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, null, scheduleActive: true));
        Assert.Same(ManualSnapshot, monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites(), scheduleActive: true));
    }

    [Fact]
    public void Active_SitesAllNormaliseAway_ReturnsSnapshotVerbatim()
    {
        // Sites that all normalise to "" add nothing -> snapshot verbatim (no spurious rewrite/churn).
        Assert.Same(ManualSnapshot, monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites("", "   "), scheduleActive: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ScheduleOnly_NoSnapshot_SynthesisesTheBlock_MatchesCliManualLayout(string? snapshot)
    {
        // A schedule-only block (no manual snapshot) synthesises its sites as THE block - byte-
        // identical to a manual block armed with the same sites (Blocker.BuildMonkModeBlock).
        var expected = MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com", "x.com" });
        Assert.Equal(expected, monkmode.Service1.EffectiveHostsBlock(snapshot!, Sites("reddit.com", "x.com"), scheduleActive: true));
    }

    [Fact]
    public void ScheduleOnly_SynthesisedBlock_StripsCleanly_NoDataLoss()
    {
        // The schedule-only block rides RepairHostsBlock into the user's own hosts and stopMe's
        // StripMonkModeBlock removes the WHOLE marker block, handing back exactly the user content.
        var block = monkmode.Service1.EffectiveHostsBlock("", Sites("reddit.com"), scheduleActive: true);
        var userContent = "# my hosts\r\n127.0.0.1 my-dev-box";
        var repaired = monkmode.Service1.RepairHostsBlock(userContent, block);
        Assert.Equal(userContent + "\r\n" + block, repaired);
        Assert.Equal(userContent, monkmode.Service1.StripMonkModeBlock(repaired!));
        // No churn: an intact synthesised block repairs to null next tick.
        Assert.Null(monkmode.Service1.RepairHostsBlock(repaired, block));
    }

    [Fact]
    public void Overlap_UnionsSnapshotAndScheduleEntries_MarkerExactlyOnce()
    {
        // A manual snapshot + schedule sites: the union is the snapshot verbatim then the schedule
        // entries, marker exactly once, every 127.0.0.1 line present.
        var union = monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites("x.com"), scheduleActive: true);
        Assert.StartsWith(ManualSnapshot, union);                                    // snapshot kept verbatim
        Assert.Equal(0, union.IndexOf(Marker, System.StringComparison.Ordinal));     // marker at the top
        Assert.Equal(union.IndexOf(Marker, System.StringComparison.Ordinal),         // ...and only once
                     union.LastIndexOf(Marker, System.StringComparison.Ordinal));
        Assert.Contains("127.0.0.1 x.com\r\n", union);                               // schedule site appended
        Assert.Contains("127.0.0.1 www.x.com\r\n", union);                           // ...with its www. mirror
        Assert.Contains("127.0.0.1 web.x.com\r\n", union);                           // ...and the web-app mirror
    }

    [Fact]
    public void Overlap_SharedSite_IsNotDuplicated()
    {
        // reddit.com is in BOTH the manual snapshot and the schedule: the union dedups it line-wise
        // and adds only the genuinely new x.com (extend, never duplicate).
        var union = monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites("reddit.com", "x.com"), scheduleActive: true);
        Assert.Equal(ManualSnapshot + MonkMode.Blocker.BuildHostsEntries(new[] { "x.com" }), union);
        Assert.Equal(1, CountOccurrences(union, "127.0.0.1 reddit.com\r\n"));   // the shared line, exactly once
    }

    [Fact]
    public void Overlap_Union_StripsCleanly_And_NoChurnOnceWritten()
    {
        // The safety-critical round-trip: the overlap union rides RepairHostsBlock into hosts,
        // no-churns next tick, and stopMe's strip removes the WHOLE union (manual + schedule) at
        // the effective end, restoring the user's content byte-for-byte (no data loss).
        var union = monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites("x.com"), scheduleActive: true);
        var userContent = "# my hosts\r\n127.0.0.1 my-dev-box";
        var written = monkmode.Service1.RepairHostsBlock(userContent, union);
        Assert.Equal(userContent + "\r\n" + union, written);
        Assert.Null(monkmode.Service1.RepairHostsBlock(written, union));          // deterministic target -> no churn
        Assert.Equal(userContent, monkmode.Service1.StripMonkModeBlock(written!)); // clean strip, user content intact
    }

    [Fact]
    public void AppsOnlyManualBlock_MarkerOnlySnapshot_UnionAppendsScheduleEntries()
    {
        // A manual block with apps but no sites snapshots just the marker line (BuildMonkModeBlock
        // over no domains). While a window is open the union appends the schedule's site entries
        // after the bare marker - a valid single-marker block.
        var markerOnly = MonkMode.Blocker.BuildMonkModeBlock(System.Array.Empty<string>());  // "marker\r\n"
        var union = monkmode.Service1.EffectiveHostsBlock(markerOnly, Sites("x.com"), scheduleActive: true);
        Assert.Equal(markerOnly + MonkMode.Blocker.BuildHostsEntries(new[] { "x.com" }), union);
    }

    [Fact]
    public void Overlap_AllScheduleSitesAlreadyInSnapshot_AddsNothing_NoChurn()
    {
        // Every schedule site is already in the manual snapshot: the union adds nothing (value-
        // equal to the snapshot), so a written block re-repairs to null (no per-tick churn).
        var union = monkmode.Service1.EffectiveHostsBlock(ManualSnapshot, Sites("reddit.com"), scheduleActive: true);
        Assert.Equal(ManualSnapshot, union);
        var written = "# mine\r\n127.0.0.1 box\r\n" + ManualSnapshot;
        Assert.Null(monkmode.Service1.RepairHostsBlock(written, union));
    }

    [Fact]
    public void Overlap_SnapshotWithoutTrailingTerminator_AppendsWithoutFusingLines()
    {
        // A hand-tampered snapshot missing its trailing terminator: the guard adds one so the
        // first schedule line can't FUSE onto the snapshot's last line.
        var snapshot = Marker + "\r\n127.0.0.1 reddit.com";   // no trailing CRLF
        var union = monkmode.Service1.EffectiveHostsBlock(snapshot, Sites("x.com"), scheduleActive: true);
        Assert.Equal(Marker + "\r\n127.0.0.1 reddit.com\r\n" + MonkMode.Blocker.BuildHostsEntries(new[] { "x.com" }), union);
    }

    [Fact]
    public void Overlap_LfOnlySnapshot_DedupsAgainstLfLines_AppendsCrLf()
    {
        // An LF-ending snapshot (hand-edited): the line-set split on {CRLF,LF} still dedups a
        // shared site (terminator-agnostic), and genuinely new schedule lines are appended.
        // The snapshot carries the full reddit mirror set (bare + www./m./web./mobile.) in LF form,
        // so a schedule reddit.com dedups against ALL of them, not just the bare + www. lines.
        var snapshot = (Marker + "\r\n" + MonkMode.Blocker.BuildHostsEntries(new[] { "reddit.com" })).Replace("\r\n", "\n");
        var union = monkmode.Service1.EffectiveHostsBlock(snapshot, Sites("reddit.com", "x.com"), scheduleActive: true);
        Assert.Equal(snapshot + MonkMode.Blocker.BuildHostsEntries(new[] { "x.com" }), union);   // reddit.* not re-added
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

// ===== C5b sub-slice (b3-iii): the NOTIFIER user-session app-kill union =====
//
// The notifier (mm_notify) kills a schedule's USER-session apps (browsers/games) while a window is
// open, mirroring the SERVICE session-0 loop (b3-i) - the manual block's app-kill has always been
// split across the two binaries. It reaches the decision through a 3rd byte-for-byte PARITY copy of
// the pure gates (Form1.ScheduleActive/ScheduleElapsed/ParseSchedule/EffectiveKillList) - the same
// duplication+parity pattern as the guardian's ScheduleActive and the 4 ConfigIntegrity copies. The
// notifier only READS the service-written [Schedule] ActiveUntil/Spec + [Time] HighWater (no write
// race). These tests pin the notifier's copies EQUAL to the service's (a drift would silently stop
// killing the right apps in the user session), plus the no-schedule byte-identity and the
// schedule-only union. The live per-tick read + kill loop (appKillTimer_Tick) is the smoke-only seam
// (fence: unit tests never enumerate/kill real processes); its safety is these pure invariants - a
// no-schedule ActiveUntil="" makes EffectiveKillList return the manual list verbatim (unchanged
// kills), and the tick's early-return guards the EFFECTIVE union so a schedule-only "null" manual
// list still kills the schedule's apps.
public class NotifierScheduleGateParityTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);

    private static System.Collections.Generic.List<string> Apps(params string[] a) =>
        new System.Collections.Generic.List<string>(a);

    [Fact]
    public void GrammarVersionTag_MatchesTheService()
    {
        // A Spec grammar retune (v1 -> v2) must move all copies together; pin the notifier's.
        Assert.Equal(monkmode.Service1.ScheduleSpecGrammarVersion, mm_notify.Form1.ScheduleSpecGrammarVersion);
    }

    [Fact]
    public void ScheduleElapsedAndActive_AgreeWithTheService_AcrossTheTable()
    {
        // The notifier reads the service-written [Schedule] ActiveUntil + [Time] HighWater and must
        // agree on "window open" exactly, or it would kill the schedule's user-session apps a window
        // too long/short relative to the service's session-0 loop. Fail-closed on every axis
        // (unparseable deadline/mark => held), exactly like the service<->guardian parity.
        var deadlines = new[]
        {
            Hw.AddMinutes(-1).ToString(EnCa), Hw.ToString(EnCa),
            Hw.AddHours(1).ToString(EnCa), "garbage", "",
        };
        foreach (var d in deadlines)
            foreach (var hw in new[] { Hw.ToString(EnCa), "garbage", "" })
            {
                Assert.Equal(monkmode.Service1.ScheduleElapsed(d, hw), mm_notify.Form1.ScheduleElapsed(d, hw));
                Assert.Equal(monkmode.Service1.ScheduleActive(d, hw), mm_notify.Form1.ScheduleActive(d, hw));
            }
    }

    [Theory]
    [InlineData("v1;12345:0900-1700,67:1000-1400;sites=reddit.com|news.ycombinator.com;apps=chrome.exe|brave.exe")]
    [InlineData("v1;1234567:0000-2359;sites=x.com;apps=")]              // apps= with an empty body
    [InlineData("v1;7:1000-1100;apps=chrome.exe;sites=a.com||b.com")]   // order-tolerant; apps first
    [InlineData("v1;12345:0900-1700,BADWIN,67:2500-2600;sites=x.com;apps=discord.exe")] // one bad window
    [InlineData("")]                                                    // inert -> no apps
    [InlineData("   ")]                                                 // inert -> no apps
    [InlineData("garbage")]                                            // no ';' -> inert
    [InlineData("v2;12345:0900-1700;sites=x;apps=chrome.exe")]          // unknown tag -> inert (no apps)
    public void ParseSchedule_MatchesTheService(string spec)
    {
        // The notifier's ParseSchedule is a FULL byte-for-byte parity copy; only .Apps is consumed
        // today, but pin the WHOLE parsed output (.Apps + .Sites + .Windows count) equal to the
        // service's, so a drift in ANY part of the copied parser (incl. the window/day/time parsing
        // the notifier never reads) is caught - not just the apps branch. A garbage/unknown-tag Spec
        // yields nothing on every axis; the one-bad-window case pins the fail-closed window skip.
        var svc = monkmode.Service1.ParseSchedule(spec);
        var noti = mm_notify.Form1.ParseSchedule(spec);
        Assert.Equal(svc.Apps.ToArray(), noti.Apps.ToArray());
        Assert.Equal(svc.Sites.ToArray(), noti.Sites.ToArray());
        Assert.Equal(svc.Windows.Count, noti.Windows.Count);
    }

    [Fact]
    public void EffectiveKillList_NoOpenWindow_IsManualListVerbatim_ByteIdentical()
    {
        // The no-schedule byte-identity: with no open window the notifier's kill set is EXACTLY the
        // manual list (even if schedule apps are defensively passed) - what it kills today.
        Assert.Equal("chrome.exe,firefox.exe",
            mm_notify.Form1.EffectiveKillList("chrome.exe,firefox.exe", Apps("brave.exe"), scheduleActive: false));
        Assert.Equal(monkmode.Service1.EffectiveKillList("chrome.exe,firefox.exe", Apps("brave.exe"), false),
                     mm_notify.Form1.EffectiveKillList("chrome.exe,firefox.exe", Apps("brave.exe"), false));
    }

    [Fact]
    public void EffectiveKillList_ScheduleActiveButNoApps_IsManualListVerbatim()
    {
        // A window open with an apps-less schedule (null or empty) never disturbs the manual kill set.
        Assert.Equal("chrome.exe", mm_notify.Form1.EffectiveKillList("chrome.exe", null, scheduleActive: true));
        Assert.Equal("chrome.exe", mm_notify.Form1.EffectiveKillList("chrome.exe", Apps(), scheduleActive: true));
    }

    [Fact]
    public void EffectiveKillList_WindowOpen_UnionsManualAndScheduleApps_MatchesTheService()
    {
        // While a window is open the kill set is the UNION, pipe-joined, byte-identical to the service
        // (so both the session-0 and user-session loops kill the SAME set). Every app is matched by
        // the loop's `.Contains(name + ".exe")`.
        var apps = Apps("brave.exe", "discord.exe");
        Assert.Equal(monkmode.Service1.EffectiveKillList("chrome.exe", apps, true),
                     mm_notify.Form1.EffectiveKillList("chrome.exe", apps, true));
        var union = mm_notify.Form1.EffectiveKillList("chrome.exe", apps, true);
        Assert.Contains("chrome.exe", union);   // manual app still matched
        Assert.Contains("brave.exe", union);    // schedule apps added
        Assert.Contains("discord.exe", union);
        // Exact serialisation: manual then schedule apps, "|"-joined.
        Assert.Equal("chrome.exe|discord.exe",
            mm_notify.Form1.EffectiveKillList("chrome.exe", Apps("discord.exe"), true));
    }

    [Fact]
    public void EffectiveKillList_ScheduleOnlyBlock_NullManualSentinel_ScheduleAppsAreTheKillSet()
    {
        // A schedule-only block has no manual [Process] List (the "null" sentinel); while its window
        // is open the schedule's apps ARE the kill set (byte-identical to the service). The tick's
        // early-return keys off THIS effective set, so "null|chrome.exe" is not skipped, and the
        // "null" token never substring-matches a real "<proc>.exe" under the loop's .Contains.
        var scheduleOnly = mm_notify.Form1.EffectiveKillList("null", Apps("chrome.exe"), scheduleActive: true);
        Assert.Equal(monkmode.Service1.EffectiveKillList("null", Apps("chrome.exe"), true), scheduleOnly);
        Assert.Contains("chrome.exe", scheduleOnly);
        Assert.NotEqual("null", scheduleOnly);   // the guard must NOT early-return on a schedule-only union
    }
}

// ===== C5b sub-slice (c1): the schedule-only hosts snapshot LIFECYCLE =====
//
// The gap c1 closes (design C5c §4A, blockers P2#1 + P2#2): a MANUAL block gets a MAC-
// INDEPENDENT on-disk snapshot (monkmode_hosts.block, written by the CLI at arm), which is
// what makes its two tamper backstops work - the timer self-heal reads it VERBATIM (so it re-
// asserts hosts even under macValid=False + a forged [Schedule] ActiveUntil, P2#1) and
// ReassertHostsFailClosed's File.Exists gate lets a crash re-block (P2#2). A SCHEDULE-ONLY
// block (no manual `--for`) had NEITHER. c1 makes the SERVICE the snapshot creator/deleter for
// a schedule-OWNED window, so a schedule-only block rides the same machinery.
//
// The pure decision (ClassifyScheduleSnapshot) is fully unit-tested; ProcessScheduleSnapshot is
// the thin file-I/O wrapper (temp files only - fence: unit tests never touch the real hosts/
// snapshot), exactly like ProcessAddToHosts / ReassertHostsFailClosed. The OWNERSHIP rule is the
// one real edge: manualHold (Not EffectiveBlockHasExpired) => Leave, so a manual block's snapshot
// is never overwritten/deleted, and - because EVERY macValid=False reads as a manual hold - a
// frozen/tampered config's snapshot is preserved (the delete can never fire under tamper).

public class ClassifyScheduleSnapshotTests
{
    // A manual hold OWNS the snapshot: Leave in every combination (this is also the fail-closed
    // catch-all - macValid=False => EffectiveBlockHasExpired=False => manualHold=True => the
    // snapshot is preserved for the self-heal + crash backstop, never deleted under tamper).
    [Theory]
    [InlineData(true, true, true)]     // window open, sites, snapshot present
    [InlineData(true, false, true)]    // window open, no sites
    [InlineData(false, false, true)]   // window closed, snapshot present
    [InlineData(false, false, false)]  // window closed, no snapshot
    public void ManualHold_AlwaysLeaves(bool scheduleActive, bool hasSites, bool snapshotExists)
    {
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.Leave,
            monkmode.Service1.ClassifyScheduleSnapshot(scheduleActive, manualHold: true, hasSites, snapshotExists));
    }

    [Fact]
    public void ScheduleOwnedWindowOpenWithSites_WritesBlock()
    {
        // No manual hold + an open window that contributes sites => create/refresh the snapshot,
        // whether or not one already exists (the write is idempotent on the content).
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.WriteBlock,
            monkmode.Service1.ClassifyScheduleSnapshot(true, manualHold: false, hasScheduleSites: true, snapshotExists: false));
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.WriteBlock,
            monkmode.Service1.ClassifyScheduleSnapshot(true, manualHold: false, hasScheduleSites: true, snapshotExists: true));
    }

    [Fact]
    public void ScheduleOwnedWindowClosed_SnapshotExists_Deletes()
    {
        // Window closed / between windows (no manual hold) with a schedule-owned snapshot on
        // disk => delete it, so it can't self-heal back in and the crash backstop won't re-block.
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.DeleteBlock,
            monkmode.Service1.ClassifyScheduleSnapshot(false, manualHold: false, hasScheduleSites: false, snapshotExists: true));
    }

    [Fact]
    public void ScheduleOwnedWindowOpen_NoSites_ButSnapshotExists_Deletes()
    {
        // An apps-only schedule window (no sites) shouldn't maintain a hosts snapshot; a leftover
        // schedule-owned one is dropped (nothing to enforce in hosts).
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.DeleteBlock,
            monkmode.Service1.ClassifyScheduleSnapshot(true, manualHold: false, hasScheduleSites: false, snapshotExists: true));
    }

    [Fact]
    public void ScheduleOwnedIdle_NoSnapshot_Leaves()
    {
        // Nothing open, no manual hold, nothing on disk => nothing to do (no spurious file).
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.Leave,
            monkmode.Service1.ClassifyScheduleSnapshot(false, manualHold: false, hasScheduleSites: false, snapshotExists: false));
        Assert.Equal(monkmode.Service1.ScheduleSnapshotAction.Leave,
            monkmode.Service1.ClassifyScheduleSnapshot(true, manualHold: false, hasScheduleSites: false, snapshotExists: false));
    }
}

public class ProcessScheduleSnapshotTests
{
    private const string Marker = "#### MonkMode Entries ####";

    private static string TempPath(string tag) =>
        Path.Combine(AppContext.BaseDirectory, $"c1snap_{tag}_{Guid.NewGuid():N}.tmp");

    private static System.Collections.Generic.List<string> Sites(params string[] s) =>
        new System.Collections.Generic.List<string>(s);

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } }
        catch { /* best-effort test cleanup */ }
    }

    // Window OPEN, schedule-only (no manual hold, no pre-existing snapshot): the service CREATES
    // monkmode_hosts.block, and it is byte-identical to EffectiveHostsBlock / a manual block armed
    // with the same sites (Blocker.BuildMonkModeBlock) - so the self-heal finds it intact and the
    // expiry strip removes it cleanly.
    [Fact]
    public void WindowOpen_ScheduleOnly_CreatesSnapshot_EqualsEffectiveHostsBlock()
    {
        var snap = TempPath("open");
        try
        {
            Assert.False(File.Exists(snap));
            monkmode.Service1.ProcessScheduleSnapshot(snap, scheduleActive: true, manualHold: false, Sites("reddit.com", "x.com"));

            Assert.True(File.Exists(snap));
            var written = File.ReadAllText(snap);
            Assert.Equal(monkmode.Service1.EffectiveHostsBlock("", Sites("reddit.com", "x.com"), true), written);
            Assert.Equal(MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com", "x.com" }), written);
        }
        finally { Cleanup(snap); }
    }

    // While the window stays open the snapshot must NOT churn: a second tick reads the on-disk
    // block, re-derives the SAME bytes (EffectiveHostsBlock is idempotent) and skips the write.
    // Proven deterministically by pinning LastWriteTimeUtc to a fixed past time and asserting it
    // is unchanged after the second call (a real write would bump it).
    [Fact]
    public void WindowOpen_SecondTick_IsIdempotent_DoesNotReWrite()
    {
        var snap = TempPath("idem");
        try
        {
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites("reddit.com"));
            var first = File.ReadAllText(snap);
            var pinned = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(snap, pinned);

            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites("reddit.com"));

            Assert.Equal(first, File.ReadAllText(snap));                 // content stable
            Assert.Equal(pinned, File.GetLastWriteTimeUtc(snap));        // ...and NOT re-written
        }
        finally { Cleanup(snap); }
    }

    // Window CLOSE (schedule-owned: no manual hold): the snapshot is DELETED, so nothing self-heals
    // back in and the crash backstop won't re-block an idle schedule. Same for the between-windows
    // state of a recurring schedule (scheduleActive=False, no manual hold).
    [Fact]
    public void WindowClose_ScheduleOwned_DeletesSnapshot()
    {
        var snap = TempPath("close");
        try
        {
            File.WriteAllText(snap, MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com" }));
            Assert.True(File.Exists(snap));

            monkmode.Service1.ProcessScheduleSnapshot(snap, scheduleActive: false, manualHold: false, null);

            Assert.False(File.Exists(snap));
        }
        finally { Cleanup(snap); }
    }

    // A manual HOLD's snapshot is NEVER deleted (the ownership fence) - even with no window open.
    // This is also the macValid=False fail-closed path (a frozen config reads as a manual hold).
    [Fact]
    public void ManualHold_NoWindow_NeverDeletesSnapshot()
    {
        var snap = TempPath("manualkeep");
        try
        {
            var manual = MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com" });
            File.WriteAllText(snap, manual);

            monkmode.Service1.ProcessScheduleSnapshot(snap, scheduleActive: false, manualHold: true, null);

            Assert.True(File.Exists(snap));
            Assert.Equal(manual, File.ReadAllText(snap));   // left byte-for-byte
        }
        finally { Cleanup(snap); }
    }

    // A manual HOLD's snapshot is NEVER overwritten with the schedule union either (the overlap
    // case SD-c1 forbids from the CLI; the b3-ii LIVE-hosts union still over-blocks, but the
    // snapshot FILE stays the manual-only block, so there is nothing to "restore" at close).
    [Fact]
    public void ManualHold_WindowOpen_DoesNotOverwriteManualSnapshotWithUnion()
    {
        var snap = TempPath("overlapkeep");
        try
        {
            var manual = MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com" });
            File.WriteAllText(snap, manual);

            monkmode.Service1.ProcessScheduleSnapshot(snap, scheduleActive: true, manualHold: true, Sites("x.com"));

            Assert.Equal(manual, File.ReadAllText(snap));   // union NOT persisted to the manual snapshot
            Assert.DoesNotContain("x.com", File.ReadAllText(snap));
        }
        finally { Cleanup(snap); }
    }

    // A schedule window with NO sites (apps-only) and no pre-existing snapshot creates nothing.
    [Fact]
    public void WindowOpen_NoScheduleSites_DoesNotCreateSnapshot()
    {
        var snap = TempPath("nosites");
        try
        {
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites());
            Assert.False(File.Exists(snap));
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, null);
            Assert.False(File.Exists(snap));
        }
        finally { Cleanup(snap); }
    }

    // Sites that all normalise away must not create an empty/marker-less snapshot (the
    // IsNullOrWhiteSpace guard): EffectiveHostsBlock returns "" for an empty existing snapshot.
    [Fact]
    public void WindowOpen_SitesAllNormaliseAway_NoSnapshotCreated()
    {
        var snap = TempPath("normaway");
        try
        {
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites("", "   "));
            Assert.False(File.Exists(snap));
        }
        finally { Cleanup(snap); }
    }

    // Best-effort: a DeleteBlock decision with nothing on disk is a silent no-op, never a throw
    // and never a fabricated file.
    [Fact]
    public void NoSnapshot_NothingToDelete_NoThrow_NoFile()
    {
        var snap = TempPath("noop");
        try
        {
            var ex = Record.Exception(() => monkmode.Service1.ProcessScheduleSnapshot(snap, false, false, null));
            Assert.Null(ex);
            Assert.False(File.Exists(snap));
        }
        finally { Cleanup(snap); }
    }
}

// The two P2 backstops, proven end-to-end against the snapshot c1 now creates. These compose
// ProcessScheduleSnapshot with the EXACT pure paths the live tick / crash handler run, so the
// created snapshot demonstrably re-asserts a schedule-only block under tamper and after a crash.
public class ScheduleSnapshotTamperFreezeTests
{
    private const string Marker = "#### MonkMode Entries ####";

    private static string TempPath(string tag) =>
        Path.Combine(AppContext.BaseDirectory, $"c1freeze_{tag}_{Guid.NewGuid():N}.tmp");

    private static System.Collections.Generic.List<string> Sites(params string[] s) =>
        new System.Collections.Generic.List<string>(s);

    private static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } }
        catch { /* best-effort test cleanup */ }
    }

    // P2#1: mid-window the config MAC is invalidated and [Schedule] ActiveUntil forged past/blank,
    // so scheduleActiveNow reads FALSE. Because the block is still HELD (BlockHeld True under
    // macValid=False), the self-heal runs and computes its repair target as
    // EffectiveHostsBlock(onDiskSnapshot, null, scheduleActive:FALSE) = the snapshot VERBATIM.
    // Since c1 wrote that snapshot on window-open, the schedule sites are re-asserted into a
    // blanked hosts EXACTLY as a manual block's would be. (Before c1: no snapshot => "" => no
    // re-assert - the P2#1 hole.)
    [Fact]
    public void P2_1_ForgedActiveUntilMidWindow_SelfHealReassertsScheduleSitesFromSnapshot()
    {
        var snap = TempPath("p1snap");
        var hosts = TempPath("p1hosts");
        try
        {
            // Window open: c1 creates the schedule-only snapshot.
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites("reddit.com"));
            var snapshotBlock = File.ReadAllText(snap);

            // The tamper: scheduleActiveNow=False (forged ActiveUntil). The self-heal's target is
            // the snapshot verbatim (the inert EffectiveHostsBlock path).
            var expectedBlock = monkmode.Service1.EffectiveHostsBlock(snapshotBlock, null, scheduleActive: false);
            Assert.Equal(snapshotBlock, expectedBlock);

            // Attacker blanked hosts; the self-heal repairs it back to the schedule block.
            var repaired = monkmode.Service1.RepairHostsBlock("", expectedBlock);
            Assert.NotNull(repaired);
            Assert.Contains("127.0.0.1 reddit.com", repaired);
            Assert.Contains("127.0.0.1 www.reddit.com", repaired);
        }
        finally { Cleanup(snap); Cleanup(hosts); }
    }

    // P2#2: a crash mid-window. Because c1 left the snapshot on disk, ReassertHostsFailClosed's
    // File.Exists gate is satisfied and it rebuilds the schedule block into a blanked hosts +
    // re-locks it read-only. (Before c1: no snapshot => the crash re-asserts nothing.)
    [Fact]
    public void P2_2_CrashBackstop_FindsTheCreatedSnapshot_AndReasserts()
    {
        var snap = TempPath("p2snap");
        var hosts = TempPath("p2hosts");
        try
        {
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites("reddit.com"));
            var snapshotBlock = File.ReadAllText(snap);
            File.WriteAllText(hosts, "");   // a crash blanked hosts mid-window

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);

            Assert.Equal(snapshotBlock, File.ReadAllText(hosts));   // schedule block re-asserted
            Assert.True(IsReadOnly(hosts));                          // ...and left fail-closed
        }
        finally { Cleanup(snap); Cleanup(hosts); }
    }

    // No data loss: the snapshot c1 creates is a clean marker block that RepairHostsBlock adds to
    // the user's own hosts and StripMonkModeBlock removes WHOLE at the effective end, handing back
    // the user content byte-for-byte (only the "#### MonkMode Entries ####" block is ever touched).
    [Fact]
    public void CreatedSnapshot_StripsCleanly_UserContentPreserved()
    {
        var snap = TempPath("noloss");
        try
        {
            monkmode.Service1.ProcessScheduleSnapshot(snap, true, false, Sites("reddit.com"));
            var block = File.ReadAllText(snap);
            Assert.StartsWith(Marker, block);

            var user = "# my hosts\r\n127.0.0.1 my-dev-box";
            var written = monkmode.Service1.RepairHostsBlock(user, block);
            Assert.Equal(user + "\r\n" + block, written);
            Assert.Equal(user, monkmode.Service1.StripMonkModeBlock(written!));
        }
        finally { Cleanup(snap); }
    }
}

// ===== C5b sub-slice (c2): the schedule-only TEARDOWN + service/guardian LIFECYCLE =====
//
// c2 closes P1 and the §3 trap. A schedule-only block has no manual duration, so the CLI (c3)
// writes a PAST [Time] Until SENTINEL (ScheduleOnlyExpiredUntil, §4B) - which makes BlockHeld
// track ScheduleActive (self-heals idle between windows, not latched forever). But a past Until
// alone is a TRAP: the old binary Restamp/Lift model would Lift->stopMe at the first window's
// close and the recurring schedule would die. c2 adds the DERIVED scheduleArmed signal (§4C)
// folded into ClassifyHeartbeat / Service1.EffectiveExit / Guardian.EffectiveExit, giving the
// third lifecycle state: BETWEEN-windows (armed) = Restamp (stay alive), TORN-DOWN (Spec cleared
// -> not armed) = Lift -> stopMe. The pure gate arms (Restamp-when-armed, Lift-when-cleared,
// open-window out-ranks, macValid-first, Lift<=>EffectiveExit, service<->guardian parity across
// the armed dimension) are pinned in HeartbeatRestampTests + CoolOffTests. These tests pin the
// SCHEDULE-SPECIFIC half: the sentinel, the caller's scheduleArmed DERIVATION (service exact vs
// guardian over-approx), and the between-windows / pre-window / cleared lifecycle end-to-end
// through the real gates. Pure, temp-path-free: hand-built schedule-only ini STATE (Until =
// sentinel, ActiveUntil, Spec) fed to the gates - never arms a block, never touches live paths.
public class ScheduleArmedLifecycleTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const string LiveSpec = "v1;12345:0900-1700;sites=x.com;apps=";   // Mon-Fri 09:00-17:00, >=1 window
    private static string Sentinel => monkmode.Service1.ScheduleOnlyExpiredUntil;

    // The scheduleArmed the SERVICE derives at the tick/OnStart: macValid AndAlso the Spec parses
    // to >=1 window. A real Spec is armed; a cleared ("") or wholly-malformed Spec is not (the
    // terminal-teardown trigger). This is the exact predicate wired at Service1.vb tick + OnStart.
    [Theory]
    [InlineData("v1;12345:0900-1700;sites=x.com;apps=", true)]
    [InlineData("v1;7:1000-1400;sites=x.com", true)]
    [InlineData("", false)]                       // schedule --clear
    [InlineData("garbage", false)]                // unparseable -> inert
    [InlineData("v1;99:9999-0000;sites=x", false)] // every window malformed -> 0 windows
    public void ServiceScheduleArmed_IsExact_ParseScheduleHasAWindow(string spec, bool expectedArmed)
    {
        // Exercises the REAL production helper (wired at the tick + OnStart), not a mirror:
        // macValid AndAlso the Spec parses to >=1 window.
        Assert.Equal(expectedArmed, monkmode.Service1.ScheduleArmed(macValid: true, spec));
        // ...and a tampered/frozen config (macValid=False) is NEVER armed, whatever the Spec.
        Assert.False(monkmode.Service1.ScheduleArmed(macValid: false, spec));
    }

    // The guardian avoids a 4th ParseSchedule copy (design §4C) by OVER-APPROXIMATING armed as
    // "Spec non-empty". For a real Spec and for a cleared Spec, exact and over-approx AGREE. They
    // DIVERGE only on a garbage NON-EMPTY Spec: the service (exact) reads NOT armed while the
    // guardian (over-approx) reads armed - the fail-SAFE direction (the guardian only over-guards;
    // it can never stand down EARLY, only delay its stand-down until --clear blanks the Spec). That
    // divergent input is unreachable from the CLI (c3 validates the Spec before it stamps a MAC),
    // and a MAC-valid garbage Spec needs the attacker-known key (the standing B7/B10 residual).
    [Fact]
    public void GuardianScheduleArmed_OverApprox_AgreesExceptFailSafeOnGarbage()
    {
        // Both derivations exercised through the REAL helpers (service EXACT vs guardian OVER-APPROX).
        // Real Spec: both armed.
        Assert.True(monkmode.Service1.ScheduleArmed(true, LiveSpec));
        Assert.True(mm_guard.Guardian.ScheduleArmed(true, LiveSpec));
        // Cleared Spec: both NOT armed (the terminal-teardown trigger agrees on both sides).
        Assert.False(monkmode.Service1.ScheduleArmed(true, ""));
        Assert.False(mm_guard.Guardian.ScheduleArmed(true, ""));
        // Garbage NON-empty Spec: service NOT armed, guardian armed = the documented fail-SAFE
        // divergence (a parsed window implies a non-empty Spec, so guardian-armed superset
        // service-armed => the guardian only ever OVER-guards, never an early stand-down).
        Assert.False(monkmode.Service1.ScheduleArmed(true, "garbage"));   // exact: 0 windows
        Assert.True(mm_guard.Guardian.ScheduleArmed(true, "garbage"));    // over-approx: non-empty
        // Both null-/whitespace-safe (=> not armed) and macValid-gated (a frozen config => not armed).
        Assert.False(mm_guard.Guardian.ScheduleArmed(true, "   "));
        Assert.False(mm_guard.Guardian.ScheduleArmed(true, null!));
        Assert.False(monkmode.Service1.ScheduleArmed(true, null!));
        Assert.False(mm_guard.Guardian.ScheduleArmed(false, LiveSpec));
        Assert.False(monkmode.Service1.ScheduleArmed(false, LiveSpec));
    }

    // THE c2 KEYSTONE (P1 + §3 trap) end-to-end through the real gates. A schedule-only block:
    // manual Until = the past sentinel (always expired), ActiveUntil="" between windows. While the
    // Spec is armed the heartbeat RESTAMPS (service stays alive) and EffectiveExit HOLDS (OnStart/
    // guardian stay alive) - NOT stopMe. schedule --clear blanks the Spec -> not armed -> the SAME
    // state now Lifts/exits = the clean terminal teardown. Service and guardian agree throughout.
    [Fact]
    public void ScheduleOnlyBlock_HeldBetweenWindows_TornDownOnlyWhenSpecCleared()
    {
        // A Friday 20:00 - after the 17:00 close, before Monday's window: genuinely between windows.
        var now = new DateTime(2026, 7, 3, 20, 0, 0);
        string hw = now.ToString(EnCa);
        bool expiredSentinel = monkmode.Service1.BlockHasExpired(Sentinel, now, 5);
        Assert.True(expiredSentinel);                                    // the manual portion always reads expired
        Assert.False(monkmode.Service1.ScheduleActive("", hw));         // no window open (ActiveUntil="")

        // ARMED (Spec has windows) -> RESTAMP (stay alive), never Lift.
        bool armed = monkmode.Service1.ScheduleArmed(true, LiveSpec);
        Assert.True(armed);
        Assert.Equal(monkmode.Service1.HeartbeatAction.Restamp,
            monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: expiredSentinel,
                coolOffElapsed: false, codeUnlocked: false,
                scheduleActive: monkmode.Service1.ScheduleActive("", hw), scheduleArmed: armed));
        // OnStart / guardian HOLD it alive (EffectiveExit False) - service and guardian agree.
        Assert.False(monkmode.Service1.EffectiveExit(Sentinel, "", "", "", hw, 5, macValid: true, scheduleArmed: armed));
        Assert.False(mm_guard.Guardian.EffectiveExit(Sentinel, "", "", "", hw, 5, macValid: true, scheduleArmed: armed));

        // schedule --clear -> Spec="" -> NOT armed -> the SAME state now tears down (Lift -> stopMe).
        bool clearedArmed = monkmode.Service1.ScheduleArmed(true, "");
        Assert.False(clearedArmed);
        Assert.Equal(monkmode.Service1.HeartbeatAction.Lift,
            monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: expiredSentinel,
                coolOffElapsed: false, codeUnlocked: false,
                scheduleActive: monkmode.Service1.ScheduleActive("", hw), scheduleArmed: clearedArmed));
        Assert.True(monkmode.Service1.EffectiveExit(Sentinel, "", "", "", hw, 5, macValid: true, scheduleArmed: clearedArmed));
        Assert.True(mm_guard.Guardian.EffectiveExit(Sentinel, "", "", "", hw, 5, macValid: true, scheduleArmed: clearedArmed));
    }

    // §4C OnStart trap: a schedule armed at 08:00 (past-Until sentinel, Spec present, the 09:00
    // window NOT yet open) must NOT be stopMe'd the instant the service starts. OnStart's single
    // lift path is EffectiveExit, decided in the STORED HighWater frame; scheduleArmed=True holds
    // it alive to await the window. Without the guard (control: not armed) it would exit at boot.
    [Fact]
    public void OnStart_HoldsAFreshlyArmedPreWindowScheduleOnlyBlock()
    {
        var boot = new DateTime(2026, 7, 3, 8, 0, 0);   // 08:00, before a 09:00-17:00 window
        string storedHw = boot.ToString(EnCa);
        bool armed = monkmode.Service1.ScheduleArmed(true, LiveSpec);
        Assert.True(armed);
        // Held alive (grace 0, the stricter OnStart grace) despite the expired sentinel + no open window.
        Assert.False(monkmode.Service1.EffectiveExit(Sentinel, "", "", "", storedHw, 0, macValid: true, scheduleArmed: armed));
        Assert.False(mm_guard.Guardian.EffectiveExit(Sentinel, "", "", "", storedHw, 0, macValid: true, scheduleArmed: armed));
        // Control: the SAME boot state with NO schedule (not armed) WOULD stopMe (the sentinel is expired).
        Assert.True(monkmode.Service1.EffectiveExit(Sentinel, "", "", "", storedHw, 0, macValid: true, scheduleArmed: false));
    }

    // Regression: a plain MANUAL block has no Spec, so scheduleArmed is always False and behaviour
    // is byte-identical to pre-c2 - a future Until holds, a past Until lifts. c2 adds nothing to a
    // block without a schedule.
    [Fact]
    public void ManualOnlyBlock_Unaffected_WhenScheduleArmedFalse()
    {
        var now = new DateTime(2026, 7, 3, 12, 0, 0);
        string hw = now.ToString(EnCa);
        bool armed = monkmode.Service1.ScheduleArmed(true, "");   // no Spec -> not armed
        Assert.False(armed);
        Assert.False(monkmode.Service1.EffectiveExit(now.AddHours(3).ToString(EnCa), "", "", "", hw, 5, macValid: true, scheduleArmed: armed));  // future Until holds
        Assert.True(monkmode.Service1.EffectiveExit(now.AddHours(-1).ToString(EnCa), "", "", "", hw, 5, macValid: true, scheduleArmed: armed));  // past Until lifts
    }

    // A tampered/frozen config (macValid False) never reads as armed and never lifts regardless of
    // scheduleArmed - the macValid gate is first on every gate (B7 freeze out-ranks the lifecycle).
    [Fact]
    public void InvalidMac_FreezeOutranksScheduleArmed()
    {
        var now = new DateTime(2026, 7, 3, 20, 0, 0);
        string hw = now.ToString(EnCa);
        foreach (var armed in new[] { true, false })
        {
            Assert.False(monkmode.Service1.EffectiveExit(Sentinel, "", "", "", hw, 5, macValid: false, scheduleArmed: armed));
            Assert.False(mm_guard.Guardian.EffectiveExit(Sentinel, "", "", "", hw, 5, macValid: false, scheduleArmed: armed));
            Assert.Equal(monkmode.Service1.HeartbeatAction.Hold,
                monkmode.Service1.ClassifyHeartbeat(macValid: false, blockExpired: true,
                    coolOffElapsed: false, codeUnlocked: false, scheduleActive: false, scheduleArmed: armed));
        }
    }
}
