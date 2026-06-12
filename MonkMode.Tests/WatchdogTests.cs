// MonkMode.Tests - B1 watchdog (force-kill resistance).
//
// Two tamper-critical decisions are covered, both pure and SCM/process-free so
// they never touch the live machine:
//
//   - Service1.ShouldRestartPeer: the layer-2 mutual-restart gate. Decides
//     whether the service's timer should (re)launch the guardian peer. It must
//     fail SAFE - never resurrect the guardian after the block has truly ended,
//     never start a second copy while one is already running, never try to
//     start a missing exe - and, via the caller's Not BlockHasExpired(...),
//     keep the guardian alive when the end time is unparseable (fail CLOSED).
//
//   - ServiceInstaller recovery policy: the layer-1 SCM auto-restart constants.
//     These are the single source of truth the live SetRecoveryOptions marshals
//     into the SCM. Weakening any of them weakens B1 (a finite reset period
//     would let the SCM stop restarting; fewer/!restart actions would stop it
//     sooner; non-crash off would miss some force-kills), so they are pinned
//     here and the test fails loudly if they drift.
//
// Everything here is in-memory values - the real service/SCM is never touched.

namespace MonkMode.Tests;

public class ServiceShouldRestartPeerTests
{
    [Fact]
    public void Active_ExeExists_NoneRunning_Restarts()
    {
        // The case the watchdog exists for: block live, guardian dead, relaunch.
        Assert.True(monkmode.Service1.ShouldRestartPeer(0, blockActive: true, peerExeExists: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Active_ExeExists_AlreadyRunning_DoesNotRestart(int running)
    {
        // No duplicate-spawn storm while an instance is already up.
        Assert.False(monkmode.Service1.ShouldRestartPeer(running, blockActive: true, peerExeExists: true));
    }

    [Fact]
    public void NegativeCount_TreatedAsNoneRunning_Restarts()
    {
        // Defensive: a bogus negative count still means "nothing running".
        Assert.True(monkmode.Service1.ShouldRestartPeer(-1, blockActive: true, peerExeExists: true));
    }

    [Fact]
    public void BlockNotActive_NeverRestarts_EvenWithNoneRunning()
    {
        // After the block ends the guardian must stay dead - the watchdog must
        // not keep resurrecting it once enforcement is over.
        Assert.False(monkmode.Service1.ShouldRestartPeer(0, blockActive: false, peerExeExists: true));
    }

    [Fact]
    public void ExeMissing_NeverRestarts()
    {
        // Nothing to start: never busy-loop Process.Start on a missing exe.
        Assert.False(monkmode.Service1.ShouldRestartPeer(0, blockActive: true, peerExeExists: false));
    }

    [Fact]
    public void FailClosed_UnparseableUntil_KeepsGuardianAlive()
    {
        // The fail-closed tie-in: the caller passes blockActive = !BlockHasExpired.
        // An unparseable Until is NOT expired (fail closed), so the block reads
        // active and the guardian is kept alive rather than dropped.
        bool active = !monkmode.Service1.BlockHasExpired("not-a-date", DateTime.Now, 5);
        Assert.True(active);
        Assert.True(monkmode.Service1.ShouldRestartPeer(0, active, peerExeExists: true));
    }

    [Fact]
    public void GenuinelyExpired_StopsRestarting()
    {
        // A real, parsed, past end time => expired => guardian not restarted.
        var pastUntil = DateTime.Now.AddMinutes(-10).ToString(new System.Globalization.CultureInfo("en-CA"));
        bool active = !monkmode.Service1.BlockHasExpired(pastUntil, DateTime.Now, 5);
        Assert.False(active);
        Assert.False(monkmode.Service1.ShouldRestartPeer(0, active, peerExeExists: true));
    }
}

public class ServiceRecoveryPolicyTests
{
    // INFINITE (0xFFFFFFFF) reset period: the SCM must NEVER reset the failure
    // count, or it would eventually stop restarting a repeatedly-killed service.
    [Fact]
    public void ResetPeriod_IsInfinite()
    {
        Assert.Equal(0xFFFFFFFFu, ServiceTools.ServiceInstaller.RecoveryResetPeriodSeconds);
    }

    [Fact]
    public void RestartsThreeTimes_SoEverySubsequentFailureRestartsToo()
    {
        // The SCM applies the last action to all failures beyond the listed
        // count, so >=3 RESTART actions = "restart on every failure, forever".
        Assert.True(ServiceTools.ServiceInstaller.RecoveryActionCount >= 3);
    }

    [Fact]
    public void ActionType_IsRestart()
    {
        Assert.Equal(1, ServiceTools.ServiceInstaller.RecoveryActionTypeRestart); // SC_ACTION_RESTART
    }

    [Fact]
    public void RestartDelay_IsOneSecond()
    {
        Assert.Equal(1000u, ServiceTools.ServiceInstaller.RecoveryRestartDelayMs);
    }

    [Fact]
    public void RestartsOnNonCrashFailures()
    {
        Assert.True(ServiceTools.ServiceInstaller.RecoveryRestartOnNonCrash);
    }
}
