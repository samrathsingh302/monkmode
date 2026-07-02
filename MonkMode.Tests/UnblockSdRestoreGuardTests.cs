// MonkMode.Tests - escape-hatch teardown: the step-4 service delete is gated
// on the step-3 deny-DELETE removal (audit #9).
//
// `monkmode unblock --force` tears down in strict order; step 3 removes the
// B6 deny-DELETE ACE from the service SD and step 4 deletes the service
// registration. While the ACE is still on the SD the SCM is GUARANTEED to
// refuse the delete, so a hard-failed step 3 previously let step 4 run into a
// misleading AccessDenied "skipped" that buried the real cause.
// Program.RunSdRestoreThenDelete(tryRestoreSd, tryDeleteService, reportSkip)
// is the testable policy core - production wires the delegates through Step_
// (print + swallow), so they never throw. Invariants pinned:
//   - restore succeeds first try => delete attempted, exactly one restore
//     attempt, no skip report;
//   - restore hard-fails once, the single retry succeeds => delete still
//     attempted (a transient SCM hiccup must not strand a removable service);
//   - restore hard-fails twice => the delete is NEVER attempted, exactly two
//     restore attempts are made (no retry storm), and the skip report is
//     actionable (tells the user to re-run the escape hatch);
//   - a failed delete after a successful restore reports through its own
//     step, not the skip path.
//
// HARD FENCE: pure delegates only - the real SCM/service is never touched.

namespace MonkMode.Tests;

public class UnblockSdRestoreGuardTests
{
    [Fact]
    public void RestoreSucceedsFirstTry_DeleteAttempted_NoRetry_NoSkip()
    {
        var restoreAttempts = new List<int>();
        var deleteCalls = 0;
        var skips = new List<string>();

        var result = MonkMode.Program.RunSdRestoreThenDelete(
            attempt => { restoreAttempts.Add(attempt); return true; },
            () => { deleteCalls++; return true; },
            skips.Add);

        Assert.True(result);
        Assert.Equal(new[] { 1 }, restoreAttempts);
        Assert.Equal(1, deleteCalls);
        Assert.Empty(skips);
    }

    [Fact]
    public void RestoreFailsOnce_RetrySucceeds_DeleteAttempted()
    {
        var restoreAttempts = new List<int>();
        var deleteCalls = 0;
        var skips = new List<string>();

        var result = MonkMode.Program.RunSdRestoreThenDelete(
            attempt => { restoreAttempts.Add(attempt); return attempt == 2; },
            () => { deleteCalls++; return true; },
            skips.Add);

        Assert.True(result);
        Assert.Equal(new[] { 1, 2 }, restoreAttempts);
        Assert.Equal(1, deleteCalls);
        Assert.Empty(skips);
    }

    // The audit #9 regression: with the deny ACE still on the SD, the delete
    // must be skipped - not attempted into a guaranteed AccessDenied - and the
    // user must be told how to retry.
    [Fact]
    public void RestoreFailsTwice_DeleteNeverAttempted_SkipIsActionable()
    {
        var restoreAttempts = new List<int>();
        var deleteCalls = 0;
        var skips = new List<string>();

        var result = MonkMode.Program.RunSdRestoreThenDelete(
            attempt => { restoreAttempts.Add(attempt); return false; },
            () => { deleteCalls++; return true; },
            skips.Add);

        Assert.False(result);
        Assert.Equal(new[] { 1, 2 }, restoreAttempts);   // exactly one retry, no storm
        Assert.Equal(0, deleteCalls);
        var skip = Assert.Single(skips);
        Assert.Contains("skipped", skip);
        Assert.Contains("unblock --force", skip);        // actionable: how to retry
    }

    [Fact]
    public void DeleteFails_AfterSuccessfulRestore_ResultFalse_NoSkipReport()
    {
        var skips = new List<string>();

        var result = MonkMode.Program.RunSdRestoreThenDelete(
            _ => true,
            () => false,
            skips.Add);

        Assert.False(result);
        Assert.Empty(skips);   // the delete step reports its own failure
    }
}
