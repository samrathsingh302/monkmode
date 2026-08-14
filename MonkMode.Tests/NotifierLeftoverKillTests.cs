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

// MonkMode.Tests - D4d rider: the arm-time leftover-notifier kill (MonkMode\Blocker.vb).
//
// D4c gave the notifier a machine-wide single-instance mutex, which fixed the reboot
// double-spawn but created a new failure: an ORPHANED notifier (one that outlived its
// block - a killed CLI, a torn-down block, a stale Run-entry launch) still holds the
// claim, so the notifier a FRESH manual block spawns loses it and stands down. The new
// block then runs behind a notifier pointed at the previous block's state. So a manual
// arm now clears leftovers first; a schedule arm does not, because `monkmode schedule`
// can be re-run during an OPEN window whose notifier is healthy and enforcing.
//
// Blocker.ShouldKillLeftoverNotifier(timeChangingText, instanceCount, killLeftovers) is
// the testable core (decision only); the thin live wrapper KillLeftoverNotifiers feeds it
// the real mm_notify process count and the real [Time] TimeChanging, and owns the
// bounded retry (<= 3 passes, 250 ms apart, both facts re-read every pass). Invariants
// pinned here:
//   - the full truth table: kill only when killLeftovers AND instanceCount > 0 AND the
//     clock-change flag is not "yes";
//   - THE regression pin - a raised TimeChanging flag ("yes", any case, any surrounding
//     whitespace) never kills. A notifier killed inside its ~2s clock-change cooperation
//     leaves the flag stuck at "yes", which freezes the service's HighWater roll and
//     schedule poll (Service1.vb gates all of them on TimeChanging="no") for the rest of
//     the block. Failing to kill only ever OVER-enforces (a duplicate notifier), so the
//     gate resolves every ambiguity to "don't kill";
//   - a count of 0 (nothing to kill, or an enumeration that found nothing) never fires;
//   - the two call sites' argument, via the named constants MonkMode\Program.vb passes:
//     ManualArmKillsLeftovers = True at the manual arm, ScheduleArmKillsLeftovers =
//     False at the schedule arm - asserted both as values and composed through the gate;
//   - RegisterAndLaunchNotifier's optional parameter still DEFAULTS to False, so a
//     caller added later leaves a running notifier alone until it opts in.
//
// HARD FENCE: pure decision tests only. Nothing here enumerates or kills a real process,
// reads or writes an ini, touches hosts, the registry or the service, and
// RegisterAndLaunchNotifier is only ever REFLECTED over - never invoked (invoking it
// would write the HKCU Run entry and launch the live notifier). No filesystem use at
// all, so there is no temp path to make GUID-unique.

using System.Reflection;

namespace MonkMode.Tests;

public class NotifierLeftoverKillTests
{
    // The full matrix: {killLeftovers T/F} x {TimeChanging "yes"/"no"/""/"YES "} x
    // {count 0/1/2}. Written out row by row rather than computed, so the expected column
    // is an independent statement of the policy and not a second copy of the code.
    [Theory]
    // --- manual arm (killLeftovers = true) ---
    [InlineData(true, "yes", 0, false)]     // clock change in flight - never kill
    [InlineData(true, "yes", 1, false)]
    [InlineData(true, "yes", 2, false)]
    [InlineData(true, "no", 0, false)]      // nothing to kill
    [InlineData(true, "no", 1, true)]       // the intended case: one orphan, clock quiet
    [InlineData(true, "no", 2, true)]
    [InlineData(true, "", 0, false)]        // no flag (no ini / no block) - still nothing to kill
    [InlineData(true, "", 1, true)]
    [InlineData(true, "", 2, true)]
    [InlineData(true, "YES ", 0, false)]    // case + whitespace still read as in-window
    [InlineData(true, "YES ", 1, false)]
    [InlineData(true, "YES ", 2, false)]
    // --- schedule arm (killLeftovers = false): never kills, whatever the world looks like ---
    [InlineData(false, "yes", 0, false)]
    [InlineData(false, "yes", 1, false)]
    [InlineData(false, "yes", 2, false)]
    [InlineData(false, "no", 0, false)]
    [InlineData(false, "no", 1, false)]
    [InlineData(false, "no", 2, false)]
    [InlineData(false, "", 0, false)]
    [InlineData(false, "", 1, false)]
    [InlineData(false, "", 2, false)]
    [InlineData(false, "YES ", 0, false)]
    [InlineData(false, "YES ", 1, false)]
    [InlineData(false, "YES ", 2, false)]
    public void ShouldKill_Matrix(bool killLeftovers, string timeChanging, int count, bool expected)
    {
        Assert.Equal(expected, MonkMode.Blocker.ShouldKillLeftoverNotifier(timeChanging, count, killLeftovers));
    }

    // THE regression pin, spelled out beyond the matrix rows: every spelling of a raised
    // flag blocks the kill. The comparison is trimmed and case-insensitive because the
    // flag is a plaintext, non-MAC-covered cooperation field written by more than one
    // process - a stricter match would silently fail OPEN into the ~2s window.
    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("Yes")]
    [InlineData("yes ")]
    [InlineData(" yes")]
    [InlineData("\tyes\r\n")]
    public void ShouldKill_ClockChangeInFlight_NeverKills(string flag)
    {
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier(flag, 1, true));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier(flag, 5, true));
    }

    // Only that one word is the window: anything else - including a garbled value - reads
    // as "no clock change" and permits the kill. Deliberately NOT the service's stance on
    // the same field (Service1.vb advances only on an exact "no" and holds on anything
    // else), because the two are guarding opposite risks: the service is deciding whether
    // to ADVANCE state, where a wrong guess corrupts timing, while this gate is deciding
    // whether to kill a process that carries ZERO enforcement authority. Only a positive,
    // present "yes" is evidence of an in-flight clock change - the only thing worth
    // aborting the kill for - and all four writers of the flag write "yes"/"no" verbatim.
    [Theory]
    [InlineData("no")]
    [InlineData("NO")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yess")]
    [InlineData("y")]
    public void ShouldKill_FlagNotRaised_KillsWhenThereIsSomethingToKill(string flag)
    {
        Assert.True(MonkMode.Blocker.ShouldKillLeftoverNotifier(flag, 1, true));
    }

    // A null flag is what a caller would hand over if it defaulted a missing read to
    // Nothing; it must not throw, and it reads as "not raised" exactly like "".
    [Fact]
    public void ShouldKill_NullFlag_IsTreatedAsNotRaised()
    {
        Assert.True(MonkMode.Blocker.ShouldKillLeftoverNotifier(null!, 1, true));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier(null!, 0, true));
    }

    // No leftovers means no kill - including the nonsense negative count a broken
    // enumeration could produce. The wrapper re-counts every retry pass, so this is also
    // what ends the retry loop once the last orphan is gone.
    [Fact]
    public void ShouldKill_NothingRunning_NeverKills()
    {
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("no", 0, true));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("", 0, true));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("no", -1, true));
    }

    // Call-site pin, manual arm (MonkMode\Program.vb, DoBlock): on a FRESH arm the argument
    // it passes is this constant, asserted as a value AND composed through the gate - a
    // fresh manual arm with one orphan and a quiet clock kills. (Since M0/F6 the call site
    // routes through ManualArmKillPolicy; the fresh branch returns exactly this constant.)
    [Fact]
    public void ManualCaller_PassesTrue()
    {
        Assert.True(MonkMode.Blocker.ManualArmKillsLeftovers);
        Assert.True(MonkMode.Blocker.ShouldKillLeftoverNotifier("no", 1, MonkMode.Blocker.ManualArmKillsLeftovers));
    }

    // M0 rider (F6): the manual arm's kill policy is only right for a FRESH arm.
    //
    // D4d's whole argument above is that the notifier a manual arm finds is an ORPHAN - a
    // leftover from a block that no longer exists. v1.1 S2 removed the one-block-at-a-time
    // refusal, so `monkmode block` can now land beside a LIVE block whose notifier is
    // healthy and doing user-session app-kill and clock-change compensation. Killing that
    // one opens exactly the enforcement hole the schedule path already refuses to open, and
    // it does so at the worst moment - the arm that is adding MORE to enforce.
    //
    // So a non-fresh manual arm takes the schedule path's answer. Nothing is lost: the
    // notifier re-reads the ini every app-kill and poll tick, so the survivor picks up the
    // newly armed slot within one tick, and a duplicate notifier only ever OVER-enforces.
    [Theory]
    [InlineData(false, true)]   // fresh manual arm: clear the orphan, as D4d intends
    [InlineData(true, false)]   // something already armed: that notifier is not an orphan
    public void ManualArmKillPolicy_OnlyAFreshArmKills(bool alreadyArmed, bool expected)
    {
        Assert.Equal(expected, MonkMode.Blocker.ManualArmKillPolicy(alreadyArmed));
    }

    // The policy resolves to the two existing named constants rather than bare literals, so
    // there is still exactly ONE statement of each answer.
    [Fact]
    public void ManualArmKillPolicy_ReusesTheNamedConstants()
    {
        Assert.Equal(MonkMode.Blocker.ManualArmKillsLeftovers, MonkMode.Blocker.ManualArmKillPolicy(false));
        Assert.Equal(MonkMode.Blocker.ScheduleArmKillsLeftovers, MonkMode.Blocker.ManualArmKillPolicy(true));
    }

    // Composed through the real gate: the SAME world (one live notifier, quiet clock) that
    // makes a fresh manual arm kill must leave the live block's notifier alone on a second
    // arm. This is the behaviour change, stated end-to-end.
    [Fact]
    public void SecondArmBesideALiveBlock_LeavesItsNotifierRunning()
    {
        Assert.True(MonkMode.Blocker.ShouldKillLeftoverNotifier("no", 1, MonkMode.Blocker.ManualArmKillPolicy(false)));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("no", 1, MonkMode.Blocker.ManualArmKillPolicy(true)));
        // ...and the clock-change refusal still dominates on both branches.
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("yes", 1, MonkMode.Blocker.ManualArmKillPolicy(false)));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("yes", 1, MonkMode.Blocker.ManualArmKillPolicy(true)));
    }

    // Call-site pin, schedule arm (MonkMode\Program.vb, DoSchedule): the same world that
    // makes the manual arm kill must leave a schedule arm's notifier alone, because the
    // re-run can land inside an open window with that notifier enforcing.
    [Fact]
    public void ScheduleCaller_PassesFalse()
    {
        Assert.False(MonkMode.Blocker.ScheduleArmKillsLeftovers);
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("no", 1, MonkMode.Blocker.ScheduleArmKillsLeftovers));
        Assert.False(MonkMode.Blocker.ShouldKillLeftoverNotifier("yes", 1, MonkMode.Blocker.ScheduleArmKillsLeftovers));
    }

    // The conservative default survives: a future caller written as
    // RegisterAndLaunchNotifier() opts OUT of killing, not in. Reflection only - calling
    // it would write the HKCU Run entry and launch the live notifier.
    [Fact]
    public void RegisterAndLaunchNotifier_DefaultsToNotKilling()
    {
        MethodInfo? m = typeof(MonkMode.Blocker).GetMethod(
            "RegisterAndLaunchNotifier", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m);

        ParameterInfo p = Assert.Single(m!.GetParameters());
        Assert.Equal(typeof(bool), p.ParameterType);
        Assert.True(p.IsOptional);
        Assert.True(p.HasDefaultValue);
        Assert.Equal(false, p.DefaultValue);
    }
}
