// MonkMode.Tests - AtomicHosts persistent-rename-failure + crash-backstop re-lock
// (audit N2 + N3 deterministic fail-closed branch coverage).
//
// AtomicHosts.WriteAtomic writes a unique sibling temp then atomically renames it
// over the target (File.Move overwrite == MoveFileEx REPLACE_EXISTING), retrying a
// bounded number of times if a concurrent reader holds the target at the rename
// instant, then - on PERSISTENT failure - cleaning up the temp and rethrowing so the
// existing target is left intact (fail-closed: a dropped write, never a blanked or
// torn hosts). AtomicHostsTests pins the happy paths; two branches were previously
// proven only by inspection (the A2/backstop verifier rated them Low value):
//   - N2: the persistent-failure path where every retry is exhausted and WriteAtomic
//         rethrows, leaving the old target intact and no temp behind.
//   - N3: Service1.ReassertHostsFailClosed clears hosts' read-only attribute, then
//         calls WriteAtomic to rebuild the block; if that write throws AFTER the
//         attribute was cleared, the Finally must STILL re-lock hosts read-only.
//
// Both are driven deterministically through AtomicHosts.RenameHookForTests - a
// <ThreadStatic>, Friend, production-Nothing seam that diverts ONLY the rename step
// to a test callback (see AtomicHosts.vb). Because the hook is <ThreadStatic>, it is
// visible solely on the thread that sets it, so nothing here can perturb a WriteAtomic
// call made on any other (parallel) test thread; each test additionally resets the
// hook in a finally. The forced fault is an IOException - the real shape of a
// MoveFileEx sharing/access violation - so it walks the genuine bounded-retry loop
// before the rethrow, exactly the path N2 targets.
//
// HARD FENCE: only files inside the test bin directory are touched - never the real
// hosts file, the registry or the service. No block is ever armed.

using System.IO;

namespace MonkMode.Tests;

public class AtomicHostsFailClosedTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 www.reddit.com\r\n";

    private static string TempPath() =>
        Path.Combine(AppContext.BaseDirectory, $"n2n3_{Guid.NewGuid():N}.tmp");

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

    private static string[] TempSiblings(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp");

    // ---- N2: persistent rename failure fails closed (both project copies) ----------

    // Shared assertion for one AtomicHosts copy: with the rename forced to fail on
    // every attempt, WriteAtomic must retry, then rethrow, leaving the pre-existing
    // target byte-for-byte intact and no temp sibling behind.
    private static void AssertPersistentFailureFailsClosed(
        Action<Action<string, string>> setHook,
        Action clearHook,
        Action<string, string> writeAtomic,
        string label)
    {
        var path = TempPath();
        var original = "# my own hosts line\r\n127.0.0.1 keep-me\r\n" + Block;
        File.WriteAllText(path, original);

        int renameAttempts = 0;
        try
        {
            setHook((tmp, target) =>
            {
                renameAttempts++;
                throw new IOException("forced persistent rename failure (test seam)");
            });

            // The rename can never succeed, so once the bounded retries are exhausted
            // WriteAtomic rethrows (fail-closed) rather than silently dropping the write.
            Assert.Throws<IOException>(() => writeAtomic(path, "BRAND NEW CONTENTS THAT MUST NOT LAND"));
        }
        finally { clearHook(); }

        // Retried, not one-shot: the bounded-retry loop actually engaged before giving
        // up (production retries 8x; assert >1 to stay robust to any future tuning).
        Assert.True(renameAttempts > 1, $"{label}: expected the rename to be retried, saw {renameAttempts} attempt(s)");

        // Fail-CLOSED, no data loss: the pre-existing target is byte-for-byte intact -
        // never blanked, never torn together with the half-written new contents.
        Assert.Equal(original, File.ReadAllText(path));

        // The temp sibling was cleaned up by WriteAtomic's outer Catch (no litter).
        Assert.True(TempSiblings(path).Length == 0, $"{label}: a temp file was left behind");

        Cleanup(path);
    }

    [Fact]
    public void WriteAtomic_PersistentRenameFailure_CliCopy_FailsClosed() =>
        AssertPersistentFailureFailsClosed(
            hook => MonkMode.AtomicHosts.RenameHookForTests = hook,
            () => MonkMode.AtomicHosts.RenameHookForTests = null,
            (p, c) => MonkMode.AtomicHosts.WriteAtomic(p, c),
            "CLI/MonkMode.AtomicHosts");

    [Fact]
    public void WriteAtomic_PersistentRenameFailure_ServiceCopy_FailsClosed() =>
        AssertPersistentFailureFailsClosed(
            hook => monkmode.AtomicHosts.RenameHookForTests = hook,
            () => monkmode.AtomicHosts.RenameHookForTests = null,
            (p, c) => monkmode.AtomicHosts.WriteAtomic(p, c),
            "service/monkmode.AtomicHosts");

    // ---- N3: crash backstop re-locks hosts even when the rebuild write faults -------

    // ReassertHostsFailClosed clears the read-only attribute then rebuilds the marker
    // block. Force that WriteAtomic to throw AFTER the attribute is cleared and prove
    // the handler (a) never lets the throw escape and (b) STILL re-locks hosts
    // read-only in its Finally - the single most important fail-closed guarantee.
    [Fact]
    public void Backstop_WriteFaultsAfterAttributeCleared_StillReLocksHosts_FailClosed()
    {
        var hosts = TempPath();
        var snap = TempPath();
        try
        {
            // Active block (snapshot on disk) + a hosts a crash BLANKED, so the backstop
            // decides to rewrite (RepairHostsBlock returns non-Nothing) and therefore
            // reaches the WriteAtomic we make fail. Start hosts WRITABLE (Normal) so the
            // read-only asserted afterwards can only have come from the Finally re-lock,
            // never a leftover attribute.
            File.WriteAllText(hosts, "");
            File.SetAttributes(hosts, FileAttributes.Normal);
            File.WriteAllText(snap, Block);
            Assert.False(IsReadOnly(hosts));

            monkmode.AtomicHosts.RenameHookForTests =
                (tmp, target) => throw new IOException("forced write fault after attr cleared (test seam)");

            // Never throws (a throw from a crash handler is itself undefined behaviour).
            var ex = Record.Exception(() => monkmode.Service1.ReassertHostsFailClosed(hosts, snap));
            Assert.Null(ex);
        }
        finally { monkmode.AtomicHosts.RenameHookForTests = null; }

        // Despite the failed rebuild write, hosts is left read-only (Finally re-lock).
        Assert.True(IsReadOnly(hosts), "hosts must be re-locked read-only even when the backstop write faulted");

        // The failed write applied nothing: hosts stays blanked (no partial/torn
        // content), and the temp sibling was cleaned up by WriteAtomic's outer Catch.
        Assert.Equal("", File.ReadAllText(hosts));
        Assert.True(TempSiblings(hosts).Length == 0, "backstop left a temp file behind");

        Cleanup(hosts);
        Cleanup(snap);
    }
}
