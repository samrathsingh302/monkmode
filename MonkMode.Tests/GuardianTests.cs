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

public class GuardianCadenceTests
{
    // The guardian must tick at the service's own cadence (10s timer, 5s
    // expiry grace) so the two halves agree on "expired" within one tick of
    // each other. The service's interval lives in InitializeComponent
    // (10000.0R) and its grace is the literal 5 at every BlockHasExpired call
    // site - these pins fail loudly if the guardian drifts from that.
    [Fact]
    public void TicksEveryTenSeconds_LikeTheServiceTimer()
    {
        Assert.Equal(10000, mm_guard.Program.TickIntervalMs);
    }

    [Fact]
    public void UsesTheServicesFiveSecondExpiryGrace()
    {
        Assert.Equal(5L, mm_guard.Program.ExpiryGraceSeconds);
    }
}
