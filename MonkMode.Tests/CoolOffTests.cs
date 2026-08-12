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

// MonkMode.Tests - C2b cooling-off (R1: the service-adjudicated early exit).
//
// The mechanism under test (design: vault\dev\monk-mode\plans\C2a-cooling-off-design.md):
// `monkmode unblock` drops an authority-free, PRESENCE-ONLY trigger file; on its
// next tick the SERVICE (the sole deadline writer) computes a MAC-covered
// deadline CoolOffUntil = HighWater_at_request + max(duration, floor) and counts
// it down against the B4 monotonic HighWater - never DateTime.Now - so a
// clock-forward can't skip the wait, a reboot pauses it, and a raw ini edit of
// the deadline fails the MAC (freeze). When HighWater >= CoolOffUntil (and the
// MAC is valid) the service lifts via the SAME stopMe() as natural expiry.
//
// What is pure and unit-tested here (no DPAPI, no real hosts/registry/SCM):
//   - the pinned consts: the compile-time floor (THE one new security
//     parameter) and the CLI<->service trigger-filename parity;
//   - CoolOffElapsedTime / ComputeCoolOffDeadline / ClassifyCoolOffSignal /
//     EffectiveExit, each fail-closed on every axis;
//   - service<->guardian parity for CoolOffElapsedTime + EffectiveExit (the
//     guardian folding cooling-off in is LOAD-BEARING: without it, it would
//     SCM-resurrect a cooled-off block the moment the service tears down);
//   - end-to-end simulations through the REAL B4 gates (NextHighWater +
//     CapHighWaterAdvance): request->countdown->lift; forward-jump/creep/
//     backward-roll can't skip; reboot resumes off the stored mark; a tampered
//     deadline freezes; a replayed request can't reset the deadline; the C1b
//     backup carries the deadline across a corrupt-then-restore.
//
// The live wiring (ProcessCoolOffSignals' file I/O + ini save + backup refresh,
// the CLI trigger writers, the tick/OnStart/guardian-loop plumbing) is the
// smoke-tested seam (CV C-core smoke), exactly like the B1/B2/B4/C1b live wiring.

using System.Globalization;
using System.IO;

namespace MonkMode.Tests;

public class CoolOffConstTests
{
    [Fact]
    public void Floor_IsOneHour_ThePinnedSecurityParameter()
    {
        // D1 (recommended default, ratifiable): 1 hour. A retune is a single
        // loud edit here, exactly like HighWaterJumpCeilingSeconds.
        Assert.Equal(3600L, monkmode.Service1.MinCoolOffFloorSeconds);
    }

    [Fact]
    public void Floor_IsNeverTrivial()
    {
        // The floor is what makes cooling-off a real wait: a floor of seconds
        // would make `unblock` a trivial escape. Pin a hard lower bound (a few
        // minutes) independent of the exact D1 value.
        Assert.True(monkmode.Service1.MinCoolOffFloorSeconds >= 300L);
    }

    [Fact]
    public void TriggerFileNames_AreStable_AndParityAcrossCliAndService()
    {
        // The CLI drops the files; the service polls for them. A drift would
        // silently break the channel (requests never seen), so both copies are
        // pinned to the exact strings, like SnapshotName/BackupFileName.
        Assert.Equal("monkmode_cooloff.request", MonkMode.Blocker.CoolOffRequestFileName);
        Assert.Equal("monkmode_cooloff.cancel", MonkMode.Blocker.CoolOffCancelFileName);
        Assert.Equal(MonkMode.Blocker.CoolOffRequestFileName, monkmode.Service1.CoolOffRequestFileName);
        Assert.Equal(MonkMode.Blocker.CoolOffCancelFileName, monkmode.Service1.CoolOffCancelFileName);
    }
}

public class CoolOffElapsedTimeTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);

    [Fact]
    public void EmptyDeadline_NoCoolOffPending_IsNotElapsed()
    {
        Assert.False(monkmode.Service1.CoolOffElapsedTime("", Hw.ToString(EnCa)));
        Assert.False(mm_guard.Guardian.CoolOffElapsedTime("", Hw.ToString(EnCa)));
    }

    [Fact]
    public void PastDeadline_IsElapsed_AndEqualCountsAsElapsed()
    {
        Assert.True(monkmode.Service1.CoolOffElapsedTime(Hw.AddMinutes(-1).ToString(EnCa), Hw.ToString(EnCa)));
        // deadline == HighWater: the wait is served (<=, like the design pins).
        Assert.True(monkmode.Service1.CoolOffElapsedTime(Hw.ToString(EnCa), Hw.ToString(EnCa)));
    }

    [Fact]
    public void FutureDeadline_IsNotElapsed()
    {
        Assert.False(monkmode.Service1.CoolOffElapsedTime(Hw.AddHours(1).ToString(EnCa), Hw.ToString(EnCa)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("25.06.2026 17:04:33")] // legacy de-DE format
    public void UnparseableDeadline_IsNotElapsed_FailClosed(string deadline)
    {
        // A corrupted deadline can only ever HOLD the block, never lift it.
        Assert.False(monkmode.Service1.CoolOffElapsedTime(deadline, Hw.ToString(EnCa)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableOrBlankHighWater_IsNotElapsed_FailClosed(string hw)
    {
        // No trustworthy mark to measure against => the wait is never over.
        Assert.False(monkmode.Service1.CoolOffElapsedTime(Hw.ToString(EnCa), hw));
    }

    [Fact]
    public void ServiceAndGuardian_AgreeAcrossTheTable()
    {
        // The pair must never disagree on "cooling-off elapsed", or the guardian
        // could resurrect a block the service just cooled off (or stand down
        // while the service still enforces).
        var inputs = new[]
        {
            Hw.AddMinutes(-1).ToString(EnCa), Hw.ToString(EnCa),
            Hw.AddHours(1).ToString(EnCa), "garbage", "",
        };
        foreach (var deadline in inputs)
            foreach (var hw in new[] { Hw.ToString(EnCa), "garbage", "" })
                Assert.Equal(
                    monkmode.Service1.CoolOffElapsedTime(deadline, hw),
                    mm_guard.Guardian.CoolOffElapsedTime(deadline, hw));
    }
}

public class ComputeCoolOffDeadlineTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private const long Floor = 3600;

    [Fact]
    public void DurationBelowFloor_IsClampedToTheFloor()
    {
        // THE floor property: a (future C6) configured duration is CLI-written
        // and MAC-stampable by an attacker running the CLI, so duration=0 must
        // still wait the full floor. The shortest possible cooling-off is the
        // compile-time floor.
        Assert.Equal(Hw.AddSeconds(Floor).ToString(EnCa),
            monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), 0, Floor));
        Assert.Equal(Hw.AddSeconds(Floor).ToString(EnCa),
            monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), -999, Floor));
        Assert.Equal(Hw.AddSeconds(Floor).ToString(EnCa),
            monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), Floor - 1, Floor));
    }

    [Fact]
    public void DurationAboveFloor_Extends()
    {
        // A configured duration can only ever EXTEND the wait beyond the floor.
        Assert.Equal(Hw.AddSeconds(7200).ToString(EnCa),
            monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), 7200, Floor));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableHighWater_YieldsNoDeadline_FailClosed(string hw)
    {
        // No trustworthy mark => no deadline computable => the service writes
        // nothing (the trigger stays for the next tick). Never a bogus deadline.
        Assert.Equal("", monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor));
    }

    [Fact]
    public void FreshDeadline_IsNotElapsed_AtTheMarkItWasComputedFrom()
    {
        // Round-trip with the elapsed gate: a just-written deadline must not be
        // instantly elapsed (the wait is real).
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), Floor, Floor);
        Assert.False(monkmode.Service1.CoolOffElapsedTime(deadline, Hw.ToString(EnCa)));
    }
}

public class ClassifyCoolOffSignalTests
{
    private static monkmode.Service1.CoolOffAction Classify(
        bool request, bool cancel, bool pending, bool committed, bool macValid) =>
        monkmode.Service1.ClassifyCoolOffSignal(request, cancel, pending, committed, macValid);

    [Fact]
    public void Request_OnAHealthyIdleBlock_Starts()
    {
        Assert.Equal(monkmode.Service1.CoolOffAction.Start,
            Classify(request: true, cancel: false, pending: false, committed: false, macValid: true));
    }

    [Fact]
    public void CancelWins_WhenBothTriggersArePresent()
    {
        // Fail-closed tiebreak: the safe outcome is "stay blocked".
        Assert.Equal(monkmode.Service1.CoolOffAction.Cancel,
            Classify(request: true, cancel: true, pending: true, committed: false, macValid: true));
        Assert.Equal(monkmode.Service1.CoolOffAction.Cancel,
            Classify(request: true, cancel: true, pending: false, committed: false, macValid: true));
    }

    [Fact]
    public void RequestWhilePending_IsIgnored_DeadlineImmutableOnceSet()
    {
        // A replayed/re-dropped request must never reset or extend a running
        // deadline (and this is also what makes consume-after-persist crash-safe:
        // a crash between save and trigger-delete re-classifies here as Ignore).
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            Classify(request: true, cancel: false, pending: true, committed: false, macValid: true));
    }

    [Fact]
    public void RequestOnACommittedBlock_IsIgnored_TheC4Seam()
    {
        // C4 (future): a committed block has no self-serve cooling-off - the
        // partner code is the only exit. C2b callers pass committed:=False.
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            Classify(request: true, cancel: false, pending: false, committed: true, macValid: true));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, true)]
    public void InvalidMac_AlwaysIgnores_NeverTouchesAFrozenConfig(
        bool request, bool cancel, bool pending, bool committed)
    {
        // Mirrors the `add` fail-open fix: never modify/re-stamp an unverified
        // config. A frozen (tampered) block ignores the trigger channel entirely.
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            Classify(request, cancel, pending, committed, macValid: false));
    }

    [Fact]
    public void NoTriggers_IsIgnore()
    {
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            Classify(request: false, cancel: false, pending: false, committed: false, macValid: true));
    }

    [Fact]
    public void FullMatrix_MatchesThePinnedContract()
    {
        // All 32 combinations against the design's processing rules, so any
        // future edit to the classifier is caught whichever row it bends.
        foreach (var request in new[] { true, false })
            foreach (var cancel in new[] { true, false })
                foreach (var pending in new[] { true, false })
                    foreach (var committed in new[] { true, false })
                        foreach (var macValid in new[] { true, false })
                        {
                            var expected =
                                !macValid ? monkmode.Service1.CoolOffAction.Ignore :
                                cancel ? monkmode.Service1.CoolOffAction.Cancel :
                                request && !committed && !pending ? monkmode.Service1.CoolOffAction.Start :
                                monkmode.Service1.CoolOffAction.Ignore;
                            Assert.Equal(expected, Classify(request, cancel, pending, committed, macValid));
                        }
    }
}

public class EffectiveExitTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private static readonly string HwText = Hw.ToString(EnCa);
    private static readonly string PastUntil = Hw.AddHours(-1).ToString(EnCa);
    private static readonly string FutureUntil = Hw.AddHours(5).ToString(EnCa);
    private static readonly string PastCoolOff = Hw.AddMinutes(-5).ToString(EnCa);
    private static readonly string FutureCoolOff = Hw.AddMinutes(30).ToString(EnCa);

    // C3b: a non-empty UnlockedAt = code-unlocked (any non-empty string; here a
    // representative datetime). "" = not unlocked.
    private static readonly string Unlocked = Hw.ToString(EnCa);
    // C5b: a schedule ActiveUntil in the future (an OPEN window - SD1 hard hold) and one
    // already elapsed (a CLOSED window - inert). "" = no window; "garbage" = fail-closed
    // active. Measured against HwText, like the cooling-off deadlines above.
    private static readonly string ScheduleOpen = Hw.AddMinutes(30).ToString(EnCa);
    private static readonly string ScheduleClosed = Hw.AddMinutes(-5).ToString(EnCa);

    [Fact]
    public void CoolOffElapsed_WithValidMac_Exits_EvenThoughUntilIsFuture()
    {
        // The whole point of cooling-off: the block ends BEFORE Until.
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, PastCoolOff, "", "", HwText, 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void CoolOffElapsed_WithInvalidMac_NeverExits()
    {
        // A tampered config can't cool off its way out (freeze).
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, PastCoolOff, "", "", HwText, 5, macValid: false, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, PastCoolOff, "", "", HwText, 5, macValid: false, scheduleArmed: false));
    }

    // C3b: a partner-code UnlockedAt exits under a valid MAC even with a FUTURE
    // Until and NO cooling-off pending - the partner-code arm of EffectiveExit.
    [Fact]
    public void CodeUnlocked_WithValidMac_Exits_EvenThoughUntilIsFutureAndNoCoolOff()
    {
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, "", Unlocked, "", HwText, 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, "", Unlocked, "", HwText, 5, macValid: true, scheduleArmed: false));
    }

    // C3b (R6): a tampered config can't code-unlock its way out (freeze).
    [Fact]
    public void CodeUnlocked_WithInvalidMac_NeverExits()
    {
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", Unlocked, "", HwText, 5, macValid: false, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, "", Unlocked, "", HwText, 5, macValid: false, scheduleArmed: false));
    }

    // C3b: a code-unlock is TIME-FREE - it exits even when HighWater is unparseable
    // (unlike the expiry/cooling-off arms, which fail closed on a bad mark). A
    // non-empty UnlockedAt under a valid MAC is authoritative regardless of the clock.
    [Fact]
    public void CodeUnlocked_ExitsEvenWithUnparseableHighWater()
    {
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, "", Unlocked, "", "garbage", 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, "", Unlocked, "", "", 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void NoCoolOff_ReducesToPlainExpiry()
    {
        // With no deadline pending and no code-unlock, EffectiveExit == expiry.
        Assert.True(monkmode.Service1.EffectiveExit(PastUntil, "", "", "", HwText, 5, macValid: true, scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", "", HwText, 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void UnparseableHighWater_NeverExits_FailClosed()
    {
        // No trustworthy mark and no code-unlock: expiry reads off MinValue (not
        // expired) and the cooling-off gate fails closed too.
        Assert.False(monkmode.Service1.EffectiveExit(PastUntil, PastCoolOff, "", "", "garbage", 5, macValid: true, scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(PastUntil, PastCoolOff, "", "", "", 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void ServiceAndGuardian_AgreeAcrossTheTruthTable()
    {
        // The pair must never disagree on "may end", or one side stands down /
        // lifts while the other still enforces - the resurrection bug. Over the
        // unlockedAt (C3b), scheduleActiveUntil (C5b, SD1) AND scheduleArmed (C5b, c2)
        // dimensions too - the two EffectiveExit BODIES are byte-parity, so passing the
        // SAME scheduleArmed to both must always agree (the exact-vs-over-approx difference
        // is in the CALLERS, pinned separately in ScheduleTests).
        foreach (var until in new[] { PastUntil, FutureUntil, "garbage", "" })
            foreach (var coolOff in new[] { PastCoolOff, FutureCoolOff, "garbage", "" })
                foreach (var unlocked in new[] { "", Unlocked })
                    foreach (var schedule in new[] { "", ScheduleOpen, ScheduleClosed, "garbage" })
                        foreach (var hw in new[] { HwText, "garbage", "" })
                            foreach (var mac in new[] { true, false })
                                foreach (var armed in new[] { true, false })
                                    Assert.Equal(
                                        monkmode.Service1.EffectiveExit(until, coolOff, unlocked, schedule, hw, 5, mac, armed),
                                        mm_guard.Guardian.EffectiveExit(until, coolOff, unlocked, schedule, hw, 5, mac, armed));
    }

    [Fact]
    public void HeartbeatLift_And_EffectiveExit_AreTheSameDecision()
    {
        // The tick heartbeat lifts via ClassifyHeartbeat; OnStart and the
        // guardian go through EffectiveExit. Pin that the two formulations can
        // never drift: Lift <=> EffectiveExit, over the whole input table
        // including the code-unlock (C3b), schedule-hold (C5b, SD1) and armed-
        // between-windows (C5b, c2) arms.
        foreach (var until in new[] { PastUntil, FutureUntil, "garbage", "" })
            foreach (var coolOff in new[] { PastCoolOff, FutureCoolOff, "garbage", "" })
                foreach (var unlocked in new[] { "", Unlocked })
                    foreach (var schedule in new[] { "", ScheduleOpen, ScheduleClosed, "garbage" })
                        foreach (var mac in new[] { true, false })
                            foreach (var armed in new[] { true, false })
                            {
                                var lift = monkmode.Service1.ClassifyHeartbeat(
                                    mac,
                                    monkmode.Service1.BlockHasExpired(until, Hw, 5),
                                    monkmode.Service1.CoolOffElapsedTime(coolOff, HwText),
                                    monkmode.Service1.PartnerUnlocked(unlocked),
                                    monkmode.Service1.ScheduleActive(schedule, HwText),
                                    armed)
                                    == monkmode.Service1.HeartbeatAction.Lift;
                                Assert.Equal(monkmode.Service1.EffectiveExit(until, coolOff, unlocked, schedule, HwText, 5, mac, armed), lift);
                            }
    }
}

// End-to-end simulations through the REAL B4 gates: the cooling-off countdown is
// nothing but HighWater advancing at the honest tick rate toward the deadline,
// so these compose NextHighWater + CapHighWaterAdvance (the live tick's exact
// pair) with the C2b gates.
public class CoolOffEndToEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Ceiling = 120;  // Service1.HighWaterJumpCeilingSeconds (pinned elsewhere)
    private const long Floor = 3600;   // Service1.MinCoolOffFloorSeconds (pinned above)
    private static readonly DateTime T0 = new(2026, 6, 25, 12, 0, 0);
    private static readonly string FutureUntil = T0.AddHours(8).ToString(EnCa);

    // One honest 10s tick: wall now = mark + 10s, real monotonic elapsed = 10s.
    private static string HonestTick(string hw, DateTime wallNow) =>
        monkmode.Service1.CapHighWaterAdvance(
            hw, monkmode.Service1.NextHighWater(hw, wallNow.ToString(EnCa), Ceiling), 10);

    [Fact]
    public void RequestToLift_TheFullCountdown_LiftsExactlyAfterTheFloor()
    {
        // Request at T0: the service computes deadline = HighWater + floor.
        var hw = T0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor);

        // Immediately after the request: enforced, no lift (Restamp keeps the
        // countdown moving - that arm is what advances HighWater).
        Assert.Equal(monkmode.Service1.HeartbeatAction.Restamp,
            monkmode.Service1.ClassifyHeartbeat(true,
                monkmode.Service1.BlockHasExpired(FutureUntil, T0, 5),
                monkmode.Service1.CoolOffElapsedTime(deadline, hw), false, false, scheduleArmed: false));

        // 359 honest ticks (3590s): still waiting, still enforced.
        for (int i = 1; i <= 359; i++)
            hw = HonestTick(hw, T0.AddSeconds(i * 10));
        Assert.False(monkmode.Service1.CoolOffElapsedTime(deadline, hw));

        // The 360th tick reaches T0+3600 = the deadline: cooling-off elapsed,
        // the heartbeat classifies Lift (same stopMe() as natural expiry).
        hw = HonestTick(hw, T0.AddSeconds(3600));
        Assert.True(monkmode.Service1.CoolOffElapsedTime(deadline, hw));
        Assert.Equal(monkmode.Service1.HeartbeatAction.Lift,
            monkmode.Service1.ClassifyHeartbeat(true,
                monkmode.Service1.BlockHasExpired(FutureUntil, DateTime.Parse(hw, EnCa), 5),
                monkmode.Service1.CoolOffElapsedTime(deadline, hw), false, false, scheduleArmed: false));

        // And the guardian agrees (stands down; never resurrects the service).
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, deadline, "", "", hw, 5, macValid: true, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.ShouldRestartService(
            blockActive: !mm_guard.Guardian.EffectiveExit(FutureUntil, deadline, "", "", hw, 5, macValid: true, scheduleArmed: false),
            serviceRunning: false));
    }

    [Fact]
    public void ClockRolledForward_IsRefused_TheWaitIsNeverSkipped()
    {
        // The headline never-skip guarantee: jump the wall clock 2h past the
        // deadline - NextHighWater classifies a ForwardJump and keeps the stored
        // mark, so the deadline is NOT reached.
        var hw = T0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor);

        var jumped = monkmode.Service1.NextHighWater(hw, T0.AddHours(2).ToString(EnCa), Ceiling);
        Assert.Equal(hw, jumped); // the jump was refused
        Assert.False(monkmode.Service1.CoolOffElapsedTime(deadline, jumped));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, deadline, "", "", jumped, 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void ClockCreep_IsCappedToRealElapsed_TheWaitIsNotShortened()
    {
        // The creep attack: before each 10s real tick, set the wall to the
        // current mark +119s (each step within the ceiling = Trusted, so
        // NextHighWater alone would credit ~12x real time). CapHighWaterAdvance
        // credits only the real ~10s per tick, so after 60 real ticks (~10 min)
        // the mark has moved exactly 10 min - far short of the 1h deadline.
        var hw = T0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor);

        for (int i = 1; i <= 60; i++)
        {
            var wall = DateTime.Parse(hw, EnCa).AddSeconds(119); // within-ceiling nudge
            var candidate = monkmode.Service1.NextHighWater(hw, wall.ToString(EnCa), Ceiling);
            hw = monkmode.Service1.CapHighWaterAdvance(hw, candidate, 10);
        }
        Assert.False(monkmode.Service1.CoolOffElapsedTime(deadline, hw));
        // The mark advanced only the real elapsed (60 x 10s = 600s).
        Assert.Equal(T0.AddSeconds(600).ToString(EnCa), hw);
    }

    [Fact]
    public void ClockRolledBackward_HoldsTheMark_OverWaitNeverEarlyLift()
    {
        var hw = T0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor);
        var rolled = monkmode.Service1.NextHighWater(hw, T0.AddHours(-3).ToString(EnCa), Ceiling);
        Assert.Equal(hw, rolled); // backward roll refused, mark frozen
        Assert.False(monkmode.Service1.CoolOffElapsedTime(deadline, rolled));
    }

    [Fact]
    public void RebootMidCoolOff_ResumesOffTheStoredMark_DowntimeNeverCounts()
    {
        // 5 minutes into the wait, the machine reboots and stays off 2 hours.
        var hw = T0.ToString(EnCa);
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor);
        for (int i = 1; i <= 30; i++)                       // 5 min of uptime
            hw = HonestTick(hw, T0.AddSeconds(i * 10));

        // OnStart after the reboot decides off the STORED mark (it never
        // advances it): the deadline is still ~55 min of UPTIME away - no lift,
        // regardless of how long the machine was off (B4: the boot gap is a
        // ForwardJump and is never credited).
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, deadline, "", "", hw, 0, macValid: true, scheduleArmed: false));
        var bootCandidate = monkmode.Service1.NextHighWater(hw, T0.AddHours(2).ToString(EnCa), Ceiling);
        Assert.Equal(hw, bootCandidate); // the 2h off-time gap is refused

        // The countdown then resumes at the honest rate from the stored mark.
        var t = DateTime.Parse(hw, EnCa);
        for (int i = 1; i <= 330; i++)                      // the remaining 55 min
            hw = HonestTick(hw, t.AddSeconds(i * 10));
        Assert.True(monkmode.Service1.CoolOffElapsedTime(deadline, hw));
    }

    [Fact]
    public void TamperedDeadline_FailsTheMac_EverythingFreezes()
    {
        // Forge CoolOffUntil to "already elapsed" by raw ini edit: the canonical
        // changes, the stored MAC no longer validates => macValid False => the
        // heartbeat HOLDS (never lifts, never re-stamps) and the guardian keeps
        // guarding. Cooling-off is exactly as unforgeable as every other
        // MAC-covered field.
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)i;
        var hw = T0.ToString(EnCa);
        var honestDeadline = monkmode.Service1.ComputeCoolOffDeadline(hw, Floor, Floor);

        var honest = OneSlot.Canonical(FutureUntil, "chrome.exe;", "reddit.com;", "N", hw, honestDeadline, "", "", "", "", "", "", "", "");
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(honest, key);

        var forgedDeadline = T0.AddHours(-1).ToString(EnCa); // "the wait is over"
        var forged = OneSlot.Canonical(FutureUntil, "chrome.exe;", "reddit.com;", "N", hw, forgedDeadline, "", "", "", "", "", "", "", "");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(forged, storedMac, key);
        Assert.False(macValid);

        Assert.Equal(monkmode.Service1.HeartbeatAction.Hold,
            monkmode.Service1.ClassifyHeartbeat(macValid,
                monkmode.Service1.BlockHasExpired(FutureUntil, T0, 5),
                monkmode.Service1.CoolOffElapsedTime(forgedDeadline, hw), false, false, scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, forgedDeadline, "", "", hw, 5, macValid, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, forgedDeadline, "", "", hw, 5, macValid, scheduleArmed: false));
    }

    [Fact]
    public void CancelBeforeElapse_WinsTheRace_NoLiftThatTick()
    {
        // The deadline has been reached, but a cancel trigger is present on the
        // same tick. The tick processes signals BEFORE the heartbeat (inside the
        // same tickLock), so the classifier returns Cancel, the deadline is
        // cleared, and the heartbeat - deciding off the POST-signal state -
        // re-stamps instead of lifting. Fail-closed: stay blocked.
        var hw = T0.AddHours(2).ToString(EnCa);
        var elapsedDeadline = T0.AddHours(1).ToString(EnCa);
        Assert.True(monkmode.Service1.CoolOffElapsedTime(elapsedDeadline, hw));

        Assert.Equal(monkmode.Service1.CoolOffAction.Cancel,
            monkmode.Service1.ClassifyCoolOffSignal(
                requestPresent: false, cancelPresent: true,
                coolOffPending: true, committed: false, macValid: true));

        // Post-cancel state: no deadline pending, no code-unlock => no lift, back to
        // the block.
        Assert.Equal(monkmode.Service1.HeartbeatAction.Restamp,
            monkmode.Service1.ClassifyHeartbeat(true,
                monkmode.Service1.BlockHasExpired(FutureUntil, DateTime.Parse(hw, EnCa), 5),
                monkmode.Service1.CoolOffElapsedTime("", hw), false, false, scheduleArmed: false));
    }

    [Fact]
    public void ReplayedRequest_CannotResetARunningDeadline()
    {
        // Mid-wait, the attacker re-drops the request file hoping to push the
        // deadline out (or, with a doctored duration, pull it in): Ignore while
        // pending - the deadline is immutable once set, except by cancel.
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            monkmode.Service1.ClassifyCoolOffSignal(
                requestPresent: true, cancelPresent: false,
                coolOffPending: true, committed: false, macValid: true));
    }
}

// C1b composition: the shadow backup must CARRY the cooling-off deadline, so a
// corrupt-then-restore mid-cooling-off resumes the SAME wait (never an early
// lift, never a lost deadline). The copy layer is byte-exact (CopyIfSourceValid
// + AtomicHosts), so this pins the property end-to-end against temp files with
// the MAC gate boolean-injected, exactly like ConfigBackupTests.
public class CoolOffBackupCarryTests
{
    [Fact]
    public void CorruptPrimaryMidCoolOff_RestoredFromBackup_CarriesTheDeadline()
    {
        var ca = new CultureInfo("en-CA");
        var dir = Directory.CreateTempSubdirectory("mm_cooloff_bak_");
        try
        {
            var primary = Path.Combine(dir.FullName, "monkmode_settings.ini");
            var backup = Path.Combine(dir.FullName, MonkMode.ConfigBackup.BackupFileName);
            var enc = new MonkMode.Simple3Des("mm_textbox");
            var deadline = new DateTime(2026, 6, 25, 13, 0, 0).ToString(ca);

            var ini = new MonkMode.IniFile();
            // v10: the cooling-off deadline is PER-SLOT ([Slot1] CoolOffUntil), still an
            // encrypted datetime. The un-MAC'd housekeeping key [Time] TimeChanging stays.
            OneSlot.WriteSlot1((sec, k, v) => ini.SetKeyValue(sec, k, v),
                enc.EncryptData(new DateTime(2026, 12, 31, 18, 0, 0).ToString(ca)), "", "reddit.com;",
                enc.EncryptData(new DateTime(2026, 6, 25, 12, 0, 0).ToString(ca)),
                enc.EncryptData(new DateTime(2026, 6, 25, 12, 0, 0).ToString(ca)),
                enc.EncryptData(deadline), "", "", "", "no", "", "", "", "");
            ini.SetKeyValue("Time", "TimeChanging", "no");
            ini.Save(primary);

            // The refresh a cooling-off write performs (MAC validity injected, as
            // the service's live gate would report for its just-restamped save).
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(primary, backup, true));

            var before = new MonkMode.IniFile();
            before.Load(primary);
            var canonicalBefore = MonkMode.Blocker.CanonicalFromIni(before);

            // Corrupt the primary mid-cooling-off, then restore from the backup.
            File.WriteAllText(primary, "garbage - not an ini");
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(backup, primary, true));

            // The restored primary carries the SAME deadline and derives the SAME
            // canonical (so the original MAC still validates - no freeze, no
            // reset, and definitely no early lift).
            var restored = new MonkMode.IniFile();
            restored.Load(primary);
            Assert.Equal(deadline, enc.DecryptData(restored.GetKeyValue("Slot1", "CoolOffUntil")));
            Assert.Equal(canonicalBefore, MonkMode.Blocker.CanonicalFromIni(restored));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

// C6b: the CLI-configured cooling-off DURATION ([CoolOff] Duration, MAC-covered seconds).
// ParseConfiguredCoolOffSeconds is the pure interpreter the service uses to turn the stored
// field into a duration; end-to-end (parse + ComputeCoolOffDeadline) a configured value can
// only ever EXTEND the wait, never shorten it below the compile-time floor - the whole point,
// since an attacker running the CLI could set it to 0 under a valid MAC, so the floor clamp is
// load-bearing. The MAC-coverage of the field itself is pinned in ConfigIntegrityTests (a
// forged Duration fails the MAC -> freeze) and CanonicalParityTests (every reader carries it).
public class ConfiguredCoolOffDurationTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private const long Floor = 3600;

    [Theory]
    [InlineData("7200", 7200)]      // a plain positive value passes through (ComputeCoolOffDeadline then clamps)
    [InlineData("5400", 5400)]
    [InlineData("600", 600)]        // below the floor but positive: returned as-is here; the clamp is in ComputeCoolOffDeadline
    [InlineData("  900  ", 900)]    // surrounding whitespace is trimmed
    public void PositiveValue_PassesThrough(string raw, long expected)
    {
        Assert.Equal(expected, monkmode.Service1.ParseConfiguredCoolOffSeconds(raw, Floor));
    }

    [Theory]
    [InlineData("")]         // absent key reads as ""
    [InlineData("   ")]      // blank
    [InlineData(null)]       // a bare/absent key can read back as Nothing
    [InlineData("garbage")]
    [InlineData("2h")]       // NOT a raw number - the CLI converts --cooloff to seconds before storing
    [InlineData("0")]        // non-positive => default floor (an attacker-set 0 can't shorten)
    [InlineData("-5")]
    [InlineData("99999999999999999999999")]  // overflow => TryParse fails => floor
    public void AbsentBlankOrUnusable_YieldsTheFloor(string? raw)
    {
        Assert.Equal(Floor, monkmode.Service1.ParseConfiguredCoolOffSeconds(raw!, Floor));
    }

    [Fact]
    public void ConfiguredAboveFloor_ExtendsTheWait_EndToEnd()
    {
        // A configured 2h (> the 1h floor) produces a deadline exactly 2h out.
        var seconds = monkmode.Service1.ParseConfiguredCoolOffSeconds("7200", Floor);
        var deadline = monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), seconds, Floor);
        Assert.Equal(Hw.AddSeconds(7200).ToString(EnCa), deadline);
    }

    [Fact]
    public void ConfiguredBelowFloorOrTampered_ClampsToTheFloor_EndToEnd()
    {
        // A positive-but-below-floor value, and every unusable/tampered value (blank, 0,
        // negative, garbage), all wait the full floor: the parser + max(configured, floor)
        // clamp guarantee cooling-off is NEVER shorter than the floor. Fail-safe.
        foreach (var raw in new[] { "600", "", "0", "-999", "garbage" })
        {
            var seconds = monkmode.Service1.ParseConfiguredCoolOffSeconds(raw, Floor);
            var deadline = monkmode.Service1.ComputeCoolOffDeadline(Hw.ToString(EnCa), seconds, Floor);
            Assert.Equal(Hw.AddSeconds(Floor).ToString(EnCa), deadline);
        }
    }
}

// C6b: the CLI arm path (`block --cooloff`) writes [CoolOff] Duration MAC-covered from birth.
// Uses the REAL Blocker.WriteConfig (into the test bin dir), then loads the ini back and checks
// the field is stored + carried into every reader's canonical. DPAPI is NOT exercised (we
// compare canonicals, which are DPAPI-free) - the same fence as CliWriteConfig_ProducesAnIni.
// Serialised with the other CLI ini writers so it never races the shared monkmode_settings.ini.
[Collection("CliIniWriters")]
public class CoolOffWriteConfigTests
{
    [Fact]
    public void WriteConfig_WithCoolOff_StoresTheDurationAndEveryReaderCarriesIt()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, new[] { "chrome.exe" }, until, committed: false, coolOffSeconds: 7200);

            var cliIni = new MonkMode.IniFile();
            cliIni.Load(iniPath);
            // Stored plaintext seconds under [CoolOff] Duration...
            Assert.Equal("7200", cliIni.GetKeyValue("CoolOff", "Duration"));
            // ...and every reader still derives the SAME canonical from what was written.
            //
            // S1/S2 SEAM (v1.1): the value is NOT inside the canonical right now. S1 moved
            // the readers to the v10 per-slot canonical but deliberately left the arm path
            // on the v9 sections, so a WriteConfig'd ini has no [Slots] section and every
            // reader derives SlotCount=0.
            // !! NOT fail-closed - do NOT install a tree in this state. The writer stamps
            // with the SAME v10 wrapper the readers verify with, so macValid stays TRUE
            // while every v9-located enforcement field sits OUTSIDE the canonical and is
            // UNCOVERED - tamper-OPEN under a valid MAC, not frozen. S2 restores coverage
            // for fresh arms; S3a moves the enforcement readers. The reader-agreement half
            // of this test still holds. S2 rewrites the arm path to emit [Slot1]
            // CoolOffDuration, at which point the SlotCount=0 assertion fails LOUDLY and
            // "Slot1.CoolOffDuration=7200\n" replaces it.
            var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
            Assert.Contains("SlotCount=0\n", cli);

            var srvIni = new monkmode.IniFile();
            srvIni.Load(iniPath);
            var guardIni = new mm_guard.IniFile();
            guardIni.Load(iniPath);
            var notifyIni = new mm_notify.IniFile();
            notifyIni.Load(iniPath);
            Assert.Equal(cli, new monkmode.Service1().CanonicalFromIni(srvIni));
            Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            var backupPath = MonkMode.Blocker.IniBackupPath();
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Fact]
    public void WriteConfig_WithoutCoolOff_LeavesTheDurationEmpty_TheFloorDefault()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, new[] { "chrome.exe" }, until);

            var cliIni = new MonkMode.IniFile();
            cliIni.Load(iniPath);
            // No --cooloff => no [CoolOff] Duration written (absent => "" => the service's
            // compile-time floor default). Use IsNullOrEmpty (absent reads "", a blanked key
            // reads Nothing - the recurring ini round-trip quirk).
            Assert.True(string.IsNullOrEmpty(cliIni.GetKeyValue("CoolOff", "Duration")));
            // S1/S2 seam (see the sibling test for the full note): the v9 arm path writes no
            // [Slots] section, so the v10 readers derive a zero-slot canonical - macValid
            // TRUE, enforcement fields UNCOVERED, tamper-open, must-never-install. S2
            // restores the per-slot "Slot1.CoolOffDuration=\n" assertion when the arm path
            // emits slots.
            Assert.Contains("SlotCount=0\n", MonkMode.Blocker.CanonicalFromIni(cliIni));
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            var backupPath = MonkMode.Blocker.IniBackupPath();
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}

// C6c: the shared --cooloff argument parser (Program.TryParseCoolOffArg) that drives DoBlock's
// override(>0)/inherit(0) decision and DoSetup's account-default store. Pins the contract both
// verbs rely on: a valid value passes through as seconds (the OVERRIDE), an ABSENT --cooloff yields
// (True, 0) so the caller applies its own default (block inherits the account default; setup stores
// none), and an unparseable/too-long value returns False with seconds reset to 0 (the verb then
// exits 1, no partial state). Pure arg parsing - no file I/O; the reject paths write a friendly
// error to Console.Error, redirected here so the test output stays clean.
public class CoolOffArgParseTests
{
    private static bool Parse(string[] args, out long seconds)
    {
        var prevErr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            long s = -1;
            var ok = MonkMode.Program.TryParseCoolOffArg(args, ref s);
            seconds = s;
            return ok;
        }
        finally { Console.SetError(prevErr); }
    }

    [Theory]
    [InlineData("2h", 7200)]
    [InlineData("90m", 5400)]
    [InlineData("45", 2700)]          // bare number = minutes
    [InlineData("365d", 31536000)]    // exactly MaxCoolOffSeconds is accepted (the cap boundary)
    public void ValidValue_PassesThroughAsSeconds_TheOverride(string arg, long expected)
    {
        Assert.True(Parse(new[] { "--cooloff", arg }, out var s));
        Assert.Equal(expected, s);    // >0 => DoBlock uses THIS, not the account default
    }

    [Fact]
    public void AbsentCoolOff_YieldsTrueAndZero_SoTheCallerInherits()
    {
        // No --cooloff token at all => (True, 0): DoBlock then inherits the account default,
        // DoSetup stores no default. 0 is the unambiguous "absent" signal (a valid value is always >0).
        Assert.True(Parse(new[] { "block", "--sites", "reddit.com", "--for", "2h" }, out var s));
        Assert.Equal(0L, s);
    }

    [Fact]
    public void ValuelessCoolOff_ReadsAsAbsent_TrueAndZero()
    {
        // "--cooloff" as the trailing token (no value) reads as absent (GetOption => "") => (True, 0):
        // treated as unset (the documented C6b behaviour), NOT an error.
        Assert.True(Parse(new[] { "block", "--cooloff" }, out var s));
        Assert.Equal(0L, s);
    }

    [Theory]
    [InlineData("xyz")]     // unparseable
    [InlineData("2x")]
    [InlineData("366d")]    // above the 365d cap
    [InlineData("400d")]
    public void UnparseableOrTooLong_ReturnsFalseAndZero_TheVerbExits1(string arg)
    {
        Assert.False(Parse(new[] { "--cooloff", arg }, out var s));
        Assert.Equal(0L, s);   // seconds reset to 0 on reject - never a partial value leaks through
    }
}
