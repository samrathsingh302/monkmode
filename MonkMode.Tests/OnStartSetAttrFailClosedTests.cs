// MonkMode.Tests - O1 (issue #1): OnStart's SetAttr failure path must fail
// CLOSED, not lift the block.
//
// The service's OnStart re-asserts the read-only attribute on hosts (the
// DNS-client lock - defence-in-depth, not the enforcement itself: the 10s tick
// re-asserts it every beat and the B2 self-heal repairs the hosts CONTENT
// independent of the attribute). The OLD code wrapped that SetAttr in a
// Try/Catch whose Catch called stopMe() - the ONE remaining path in the
// service where an ERROR resulted in the block being LIFTED (full teardown:
// hosts stripped, snapshot/backup deleted, SafeBoot/DoH/deny-ACL undone,
// process Ended) off a transient or attacker-induced FS error at boot.
//
// The fix routes OnStart through Service1.TrySetHostsReadOnly: a short bounded
// retry that NEVER throws and returns False on persistent failure, so OnStart
// degrades (block kept standing, the tick keeps retrying the attribute)
// instead of tearing down. These tests pin that contract:
//   - happy path: the attribute really is set, True returned (idempotent on an
//     already-read-only file);
//   - real failure shape: a missing file returns False without throwing;
//   - deterministic transient/persistent failures via SetAttrHookForTests (the
//     same <ThreadStatic>, Friend, production-Nothing seam pattern as
//     AtomicHosts.RenameHookForTests): a transient failure is retried to
//     success, a persistent one exhausts exactly `attempts` tries and returns
//     False - no exception ever escapes;
//   - the OnStart constants stay inside the SCM start budget.
//
// HARD FENCE: only files inside the test bin directory are touched - never the
// real hosts file, the registry or the service. No block is ever armed, and
// Service1 is never instantiated (its designer code arms a live timer).

using System.IO;

namespace MonkMode.Tests;

public class OnStartSetAttrFailClosedTests
{
    private static string TempPath() =>
        Path.Combine(AppContext.BaseDirectory, $"o1_{Guid.NewGuid():N}.tmp");

    private static bool IsReadOnly(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { /* best-effort test cleanup */ }
    }

    // ---- happy path: the real SetAttr sets the attribute and reports True ----

    [Fact]
    public void SetsReadOnly_AndReturnsTrue()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "x");

            var ok = monkmode.Service1.TrySetHostsReadOnly(path, attempts: 3, retryDelayMs: 0);

            Assert.True(ok);
            Assert.True(IsReadOnly(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AlreadyReadOnly_IsIdempotent_ReturnsTrue()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "x");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            var ok = monkmode.Service1.TrySetHostsReadOnly(path, attempts: 3, retryDelayMs: 0);

            Assert.True(ok);
            Assert.True(IsReadOnly(path));
        }
        finally { Cleanup(path); }
    }

    // ---- real failure shape: missing file must degrade, never throw ----------

    [Fact]
    public void MissingFile_ReturnsFalse_NeverThrows()
    {
        // A path that cannot exist: SetAttr throws FileNotFoundException on
        // every attempt. The helper must swallow it and report False - the
        // OLD OnStart would have called stopMe() here and lifted the block.
        var path = Path.Combine(AppContext.BaseDirectory, $"o1_missing_{Guid.NewGuid():N}", "hosts");

        var ex = Record.Exception(() =>
        {
            var ok = monkmode.Service1.TrySetHostsReadOnly(path, attempts: 3, retryDelayMs: 0);
            Assert.False(ok);
        });

        Assert.Null(ex);
    }

    // ---- deterministic transient/persistent failures via the test seam -------

    [Fact]
    public void TransientFailure_IsRetried_ToSuccess()
    {
        var calls = 0;
        monkmode.Service1.SetAttrHookForTests = _ =>
        {
            calls++;
            if (calls < 3) throw new IOException("transient sharing violation");
            // third attempt: succeed (no throw)
        };
        try
        {
            var ok = monkmode.Service1.TrySetHostsReadOnly("ignored-by-hook", attempts: 3, retryDelayMs: 0);

            Assert.True(ok);
            Assert.Equal(3, calls);
        }
        finally { monkmode.Service1.SetAttrHookForTests = null; }
    }

    [Fact]
    public void PersistentFailure_ExhaustsAttempts_ReturnsFalse_NeverThrows()
    {
        var calls = 0;
        monkmode.Service1.SetAttrHookForTests = _ =>
        {
            calls++;
            throw new UnauthorizedAccessException("induced ACL denial");
        };
        try
        {
            var ex = Record.Exception(() =>
            {
                var ok = monkmode.Service1.TrySetHostsReadOnly("ignored-by-hook", attempts: 3, retryDelayMs: 0);
                Assert.False(ok);
            });

            Assert.Null(ex);
            Assert.Equal(3, calls); // bounded: exactly `attempts`, no runaway loop
        }
        finally { monkmode.Service1.SetAttrHookForTests = null; }
    }

    [Fact]
    public void SuccessOnFirstAttempt_DoesNotRetry()
    {
        var calls = 0;
        monkmode.Service1.SetAttrHookForTests = _ => calls++;
        try
        {
            var ok = monkmode.Service1.TrySetHostsReadOnly("ignored-by-hook", attempts: 3, retryDelayMs: 0);

            Assert.True(ok);
            Assert.Equal(1, calls);
        }
        finally { monkmode.Service1.SetAttrHookForTests = null; }
    }

    // ---- the OnStart constants stay inside the SCM start budget --------------

    [Fact]
    public void OnStartRetryBudget_StaysWellUnderScmStartTimeout()
    {
        Assert.True(monkmode.Service1.OnStartReadOnlyAttempts >= 1);
        Assert.True(monkmode.Service1.OnStartReadOnlyRetryDelayMs >= 0);
        // Worst-case added OnStart delay: (attempts - 1) sleeps. The SCM start
        // budget is ~30s; keep the retry burst under 5s so a persistent failure
        // can never turn the fail-closed degrade into a failed service start.
        var worstCaseMs = (monkmode.Service1.OnStartReadOnlyAttempts - 1)
                          * monkmode.Service1.OnStartReadOnlyRetryDelayMs;
        Assert.True(worstCaseMs < 5000);
    }
}
