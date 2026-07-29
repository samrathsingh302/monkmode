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

// MonkMode.Tests - D4c: the notifier's single-instance claim (MM_notify\SingleInstance.vb).
//
// A reboot DURING a live block spawned the notifier TWICE, deterministically: the
// SYSTEM-session guardian relaunches one as soon as its tick sees a notifier count of 0
// post-boot, then Explorer processes the HKCU `MonkMode_notify` Run entry and starts a
// second. Nothing stopped that - Application.myapp's <SingleInstance>true</SingleInstance>
// is dead in a project with a custom Sub Main, and D4b's check only catches the toast
// activation relaunch. Two notifiers means double toasts, double Run-entry removal, double
// app-kill, and two concurrent [Time] TimeChanging writers.
//
// mm_notify.SingleInstance.TryClaim(name, ref handle) is the testable core (mechanism
// only); the thin live wrapper at the top of Program.Main feeds it the production name,
// roots the returned Mutex in a module-level field for the process lifetime, and owns the
// fail-soft policy (exit ONLY on the deterministic "already exists" answer; a throw falls
// through and launches normally, because two notifiers beat zero notifiers). Invariants
// pinned here:
//   - first claim on a free name succeeds and hands back an owning Mutex;
//   - a second claim while the first is held FAILS - the regression pin for the reboot
//     double-spawn - and hands back nothing;
//   - releasing the first claim frees the name again (the crash-release semantics the live
//     guard relies on: a killed notifier must not lock out the guardian's relaunch);
//   - distinct names are independent (the guard keys on the name, nothing ambient);
//   - the production constant is exactly `Global\MonkMode_notify_single_instance` (drift
//     pin - the "Global\" prefix keeps the claim one-per-MACHINE rather than one-per-session);
//   - ShouldStandDown's truth table: a LOST claim stands the process down only when a
//     second real mm_notify process exists (count > 1, count includes self). Losing the
//     mutex alone is spoofable - any same-user script can squat the global name forever,
//     and exit-on-lost-alone would hand it a permanent notifier kill (no app-kill, no
//     clock-change compensation), strictly worse than the pre-guard 10s-relaunch posture.
//
// HARD FENCE: every kernel object created here uses a Guid-suffixed test-unique name. The
// PRODUCTION name is only ever compared as a STRING - never passed to TryClaim - because a
// real notifier may be live on this machine and claiming its mutex would be indistinguishable
// from a second notifier starting. All claims are disposed in finally blocks so no test run
// leaks a claim into the next. Nothing touches hosts, the registry or the service.

using System.Threading;

namespace MonkMode.Tests;

public class SingleInstanceTests
{
    // Test-unique, and deliberately in the same "Global\" namespace as production so the
    // tests exercise the namespace the live guard actually uses - with a name no live
    // notifier could ever hold.
    private static string TestName(string tag) =>
        $@"Global\MonkMode_test_{tag}_{Guid.NewGuid():N}";

    // A free name: this process becomes the one live instance and gets the owning handle
    // back to root for its lifetime.
    [Fact]
    public void TryClaim_FreeName_Succeeds_AndReturnsHandle()
    {
        var name = TestName("free");
        Mutex? claim = null;
        try
        {
            Assert.True(mm_notify.SingleInstance.TryClaim(name, ref claim));
            Assert.NotNull(claim);
        }
        finally { claim?.Dispose(); }
    }

    // THE regression pin: while one claim is held, a second attempt on the same name must
    // fail. This is the reboot double-spawn - guardian relaunch and the HKCU Run entry both
    // reaching Sub Main - and the losing instance must be handed nothing to root.
    [Fact]
    public void TryClaim_NameAlreadyHeld_Fails_AndReturnsNoHandle()
    {
        var name = TestName("held");
        Mutex? first = null;
        Mutex? second = null;
        try
        {
            Assert.True(mm_notify.SingleInstance.TryClaim(name, ref first));

            Assert.False(mm_notify.SingleInstance.TryClaim(name, ref second));
            Assert.Null(second);
        }
        finally { second?.Dispose(); first?.Dispose(); }
    }

    // Crash-release semantics. The live guard never releases its claim in code and relies on
    // the handle dying with the process; releasing it here stands in for that death, and the
    // name must come free again - otherwise a killed notifier would permanently lock out the
    // guardian's relaunch.
    [Fact]
    public void TryClaim_AfterFirstClaimReleased_NameIsFreeAgain()
    {
        var name = TestName("release");
        Mutex? first = null;
        Mutex? again = null;
        try
        {
            Assert.True(mm_notify.SingleInstance.TryClaim(name, ref first));
            first!.Dispose();
            first = null;

            Assert.True(mm_notify.SingleInstance.TryClaim(name, ref again));
            Assert.NotNull(again);
        }
        finally { again?.Dispose(); first?.Dispose(); }
    }

    // The guard keys on the name and nothing ambient: two different names are independent,
    // so holding one never stands another process down.
    [Fact]
    public void TryClaim_DifferentNames_AreIndependent()
    {
        var nameA = TestName("indep_a");
        var nameB = TestName("indep_b");
        Mutex? a = null;
        Mutex? b = null;
        try
        {
            Assert.True(mm_notify.SingleInstance.TryClaim(nameA, ref a));
            Assert.True(mm_notify.SingleInstance.TryClaim(nameB, ref b));
            Assert.NotNull(a);
            Assert.NotNull(b);
        }
        finally { b?.Dispose(); a?.Dispose(); }
    }

    // Drift pin, string-only: no kernel object is created with this name here. The "Global\"
    // prefix is part of the contract - the notifier is one helper per MACHINE, launched from
    // three independent places (CLI, SYSTEM-session guardian, Explorer's Run entry), so the
    // claim must not be silently scoped to whichever session a spawn happens to land in.
    [Fact]
    public void NotifierMutexName_IsTheProductionLiteral()
    {
        Assert.Equal(@"Global\MonkMode_notify_single_instance", mm_notify.SingleInstance.NotifierMutexName);
    }

    // Drift pin for the count check: the live wrapper enumerates processes by this name,
    // and Blocker.vb / MM_guard's Program.vb carry the same literal in their own copies
    // (the assemblies do not reference each other).
    [Fact]
    public void NotifierProcessName_IsTheProductionLiteral()
    {
        Assert.Equal("mm_notify", mm_notify.SingleInstance.NotifierProcessName);
    }

    // The squatter pin: a lost claim with NO second real notifier process (count 1 = just
    // this process) must NOT stand down - a bare mutex squatter gets today's posture (the
    // notifier launches anyway, unclaimed), not a permanent kill.
    [Fact]
    public void ShouldStandDown_ClaimLost_ButNoSecondNotifier_LaunchesAnyway()
    {
        Assert.False(mm_notify.SingleInstance.ShouldStandDown(true, 1));
        Assert.False(mm_notify.SingleInstance.ShouldStandDown(true, 0));
    }

    // The reboot race pin: claim lost AND a second real notifier live (count >= 2) is the
    // genuine double-spawn - the loser stands down.
    [Fact]
    public void ShouldStandDown_ClaimLost_AndSecondNotifierLive_StandsDown()
    {
        Assert.True(mm_notify.SingleInstance.ShouldStandDown(true, 2));
        Assert.True(mm_notify.SingleInstance.ShouldStandDown(true, 3));
    }

    // A WON claim never stands down, whatever the count says - the winner is the one live
    // notifier by definition, and a squatter-plus-renamed-exe circus around it must not
    // talk it out of running.
    [Fact]
    public void ShouldStandDown_ClaimWon_NeverStandsDown()
    {
        Assert.False(mm_notify.SingleInstance.ShouldStandDown(false, 0));
        Assert.False(mm_notify.SingleInstance.ShouldStandDown(false, 1));
        Assert.False(mm_notify.SingleInstance.ShouldStandDown(false, 2));
    }
}
