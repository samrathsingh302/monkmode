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

// MonkMode.Tests - B1 watchdog layer 2: the guardian's decision gates.
//
// mm_guard.exe is the SYSTEM-session half of the mutual service <-> guardian
// restart pair. Every decision its live loop takes goes through the pure
// gates in MM_guard's Guardian module, covered here:
//
//   - Guardian.BlockHasExpired: must agree byte-for-byte with the service's
//     Service1.BlockHasExpired (parity-tested below) - if the two halves ever
//     disagreed on "expired", one side could stand down while the other still
//     enforces. Unparseable = NOT expired (fail CLOSED).
//
//   - Guardian.ShouldRestartService: restart the killed service only while
//     the block is active - never resurrect it after stopMe() tore it down at
//     expiry, never fight the SCM over an already-running/starting service.
//
//   - Guardian.ShouldRelaunchNotifier: the mirror of Service1.ShouldRestartPeer
//     on the guardian's side - active block only, exe must exist, no
//     duplicate-spawn while an instance is running.
//
//   - Guardian.ShouldStandDown (M1/F6): the single-instance stand-down, ported
//     from MM_notify's SingleInstance.ShouldStandDown - a lost mutex claim alone
//     is NOT a reason to exit, or any non-elevated squatter switches the SYSTEM
//     watchdog off permanently.
//
//   - The cadence consts: the guardian must tick at the service's own 10s/5s
//     cadence so the pair agree on expiry within one tick of each other.
//
// Everything here is in-memory values - no SCM, process list or file system.

namespace MonkMode.Tests;

public class GuardianBlockHasExpiredTests
{
    [Fact]
    public void Unparseable_IsNotExpired_FailsClosed()
    {
        // The tamper-resistant direction: a corrupted Until keeps the guardian
        // guarding, it never stands the watchdog down early.
        Assert.False(mm_guard.Guardian.BlockHasExpired("not-a-date", DateTime.Now, 5));
        Assert.False(mm_guard.Guardian.BlockHasExpired("", DateTime.Now, 5));
    }

    [Fact]
    public void ParsedPastEnd_IsExpired()
    {
        var past = DateTime.Now.AddMinutes(-10).ToString(new System.Globalization.CultureInfo("en-CA"));
        Assert.True(mm_guard.Guardian.BlockHasExpired(past, DateTime.Now, 5));
    }

    [Fact]
    public void ParsedFutureEnd_IsNotExpired()
    {
        var future = DateTime.Now.AddHours(2).ToString(new System.Globalization.CultureInfo("en-CA"));
        Assert.False(mm_guard.Guardian.BlockHasExpired(future, DateTime.Now, 5));
    }

    public static readonly TheoryData<string, long> ParityCases = new()
    {
        { "not-a-date", 5 },                  // unparseable
        { "", 0 },                            // empty (a failed decrypt)
        { "2026-06-25 5:04:33 p.m.", 5 },     // en-CA payload, far past asOf below
        { "2099-01-01 9:00:00 a.m.", 5 },     // far future
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void AgreesWithServiceCopy(string untilText, long grace)
    {
        // The pair must share one definition of "expired": guardian copy and
        // service copy give the same answer for the same inputs.
        var asOf = new DateTime(2026, 6, 30, 12, 0, 0);
        Assert.Equal(
            monkmode.Service1.BlockHasExpired(untilText, asOf, grace),
            mm_guard.Guardian.BlockHasExpired(untilText, asOf, grace));
    }
}

public class GuardianShouldRestartServiceTests
{
    [Fact]
    public void Active_ServiceDead_Restarts()
    {
        // The case the guardian exists for: block live, service force-killed.
        Assert.True(mm_guard.Guardian.ShouldRestartService(blockActive: true, serviceRunning: false));
    }

    [Fact]
    public void Active_ServiceRunning_LeavesItAlone()
    {
        Assert.False(mm_guard.Guardian.ShouldRestartService(blockActive: true, serviceRunning: true));
    }

    [Fact]
    public void Expired_NeverResurrectsTheService()
    {
        // stopMe() stops the service at expiry on purpose; the guardian must
        // not undo that.
        Assert.False(mm_guard.Guardian.ShouldRestartService(blockActive: false, serviceRunning: false));
        Assert.False(mm_guard.Guardian.ShouldRestartService(blockActive: false, serviceRunning: true));
    }

    [Fact]
    public void FailClosed_UnparseableUntil_KeepsRestartingTheService()
    {
        // The fail-closed tie-in: blockActive is the caller's
        // !BlockHasExpired(...), so an unparseable Until reads active and a
        // dead service still gets restarted.
        bool active = !mm_guard.Guardian.BlockHasExpired("garbage", DateTime.Now, 5);
        Assert.True(active);
        Assert.True(mm_guard.Guardian.ShouldRestartService(active, serviceRunning: false));
    }
}

public class GuardianShouldRelaunchNotifierTests
{
    [Fact]
    public void Active_ExeExists_NoneRunning_Relaunches()
    {
        Assert.True(mm_guard.Guardian.ShouldRelaunchNotifier(0, blockActive: true, notifierExeExists: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void AlreadyRunning_DoesNotRelaunch(int running)
    {
        // No duplicate-spawn storm while an instance is already up.
        Assert.False(mm_guard.Guardian.ShouldRelaunchNotifier(running, blockActive: true, notifierExeExists: true));
    }

    [Fact]
    public void NegativeCount_TreatedAsNoneRunning_Relaunches()
    {
        Assert.True(mm_guard.Guardian.ShouldRelaunchNotifier(-1, blockActive: true, notifierExeExists: true));
    }

    [Fact]
    public void BlockNotActive_NeverRelaunches()
    {
        Assert.False(mm_guard.Guardian.ShouldRelaunchNotifier(0, blockActive: false, notifierExeExists: true));
    }

    [Fact]
    public void ExeMissing_NeverRelaunches()
    {
        Assert.False(mm_guard.Guardian.ShouldRelaunchNotifier(0, blockActive: true, notifierExeExists: false));
    }

    [Fact]
    public void MirrorsServicePeerGate()
    {
        // Both halves of the pair use the same fail-safe spawn shape - pin the
        // mirror so they can't drift apart silently.
        foreach (var count in new[] { -1, 0, 1, 3 })
            foreach (var active in new[] { true, false })
                foreach (var exists in new[] { true, false })
                    Assert.Equal(
                        monkmode.Service1.ShouldRestartPeer(count, active, exists),
                        mm_guard.Guardian.ShouldRelaunchNotifier(count, active, exists));
    }
}

// M1 (F6, 14/08/2026): the guardian's single-instance stand-down.
//
// THE BUG. mm_guard exited on the bare "the Global\MonkModeGuardian mutex already
// exists" signal, on a comment claiming a Global\ object needs SeCreateGlobalPrivilege
// so only a stray non-elevated launch could ever hit it. That premise is false -
// SeCreateGlobalPrivilege gates SECTION (file-mapping) objects, not mutexes, and the
// name carries a default DACL. So any non-elevated process could create the mutex
// first, hold it, and permanently disable the SYSTEM watchdog in three lines, while
// the service respawned it every 10 s (ShouldRestartPeer counts processes, and a
// guardian that exits in milliseconds always counts zero). Strictly worse than having
// no mutex at all - which is exactly the reasoning MM_notify's
// SingleInstance.ShouldStandDown already carried, and which this half never received.
//
// A stale test-bin mm_guard.exe realising the vector is not hypothetical either: one
// was found running from MonkMode.Tests\bin on 12/08 (S3b handoff).
public class GuardianStandDownTests
{
    // A bare squatter (count 1 = just us) never stands the guardian down. THE
    // regression pin: this is the whole attack.
    [Fact]
    public void ClaimLost_ButNoSecondGuardian_KeepsGuarding()
    {
        Assert.False(mm_guard.Guardian.ShouldStandDown(true, 1));
        Assert.False(mm_guard.Guardian.ShouldStandDown(true, 0));
        Assert.False(mm_guard.Guardian.ShouldStandDown(true, -1));
    }

    // The race the mutex was actually for: two service ticks both spawn, both
    // processes exist, the loser sees >= 2 and exits.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    public void ClaimLost_AndASecondGuardianIsLive_StandsDown(int live)
    {
        Assert.True(mm_guard.Guardian.ShouldStandDown(true, live));
    }

    // Winning the claim is never a reason to exit, whatever the count says.
    [Fact]
    public void ClaimWon_NeverStandsDown()
    {
        foreach (var count in new[] { -1, 0, 1, 2, 5 })
            Assert.False(mm_guard.Guardian.ShouldStandDown(false, count));
    }

    // The port must be exact. Two hand-written copies of the same policy are how
    // two gates drift, so pin the guardian's answer to the notifier's across the
    // whole input space rather than restating the rule.
    [Fact]
    public void MirrorsTheNotifiersStandDownGate()
    {
        foreach (var lost in new[] { true, false })
            foreach (var count in new[] { -1, 0, 1, 2, 3, 7 })
                Assert.Equal(
                    mm_notify.SingleInstance.ShouldStandDown(lost, count),
                    mm_guard.Guardian.ShouldStandDown(lost, count));
    }

    // The names the live wrapper feeds the gate. The process name must be the one
    // the SERVICE counts when it decides to respawn a peer, or the two halves would
    // be reasoning about different processes; the mutex name is pinned literally
    // because renaming it silently forfeits every claim held by an already-running
    // guardian (both copies would then think they are alone).
    [Fact]
    public void NamesArePinned()
    {
        Assert.Equal("mm_guard", mm_guard.Guardian.GuardianProcessName);
        Assert.Equal(@"Global\MonkModeGuardian", mm_guard.Guardian.GuardianMutexName);
        Assert.StartsWith(@"Global\", mm_guard.Guardian.GuardianMutexName);
    }

    // The count includes SELF, so the boundary is > 1, not > 0. Stated separately
    // because an off-by-one here restores the original bug exactly: with > 0 a lone
    // guardian that lost the claim to a squatter would stand down again.
    [Fact]
    public void TheCountIncludesSelf_SoTheBoundaryIsTwo()
    {
        Assert.False(mm_guard.Guardian.ShouldStandDown(true, 1));   // only us
        Assert.True(mm_guard.Guardian.ShouldStandDown(true, 2));    // us + a real peer
    }
}

public class GuardianCadenceTests
{
    // The guardian must tick at the service's own cadence so the two halves
    // agree on "expired" within one tick of each other. The service's values
    // are Friend Consts (Service1.TimerIntervalMs feeds the timer in
    // InitializeComponent; Service1.ExpiryGraceSeconds is the grace at every
    // timer-path BlockHasExpired call) - pin guardian == service so a retune
    // of either half that forgets the other fails loudly, AND pin the
    // absolute policy values so a coordinated accidental drift is loud too.
    [Fact]
    public void TicksAtTheServicesCadence()
    {
        Assert.Equal(monkmode.Service1.TimerIntervalMs, mm_guard.Program.TickIntervalMs);
        Assert.Equal(10000, mm_guard.Program.TickIntervalMs);
    }

    [Fact]
    public void UsesTheServicesExpiryGrace()
    {
        Assert.Equal(monkmode.Service1.ExpiryGraceSeconds, mm_guard.Program.ExpiryGraceSeconds);
        Assert.Equal(5L, mm_guard.Program.ExpiryGraceSeconds);
    }
}
