// MonkMode.Tests - add_to_hosts channel: double-fire de-dup (audit #6) +
// fail-closed re-lock (audit #2).
//
// `monkmode add` signals the service by writing an add_to_hosts trigger file
// next to hosts; the service's FileSystemWatcher handler appends the trigger's
// entries to hosts (+ the B2 repair snapshot) and consumes the trigger. A
// FileSystemWatcher routinely raises two or more Changed events for ONE
// logical write, on threadpool threads that can run concurrently - so an
// unserialised handler could append the same entries twice (audit #6).
// Service1.ProcessAddToHosts(triggerPath, hostsPath, snapshotPath) is the
// testable core (the live adder_Changed wrapper feeds it the real paths).
// Invariants pinned:
//   - one trigger write => exactly ONE hosts + snapshot append, whether the
//     duplicate fires arrive sequentially or concurrently (the trigger is
//     consumed INSIDE the critical section, so the next fire finds nothing);
//   - the snapshot is only appended when it already exists - a missing
//     snapshot is never invented (that would create a marker-less "expected
//     block" the B2 repair would write and the expiry strip could never
//     remove);
//   - hosts is ALWAYS left read-only afterwards, on every path (audit #2
//     fail-closed re-lock), including the duplicate-fire no-op;
//   - a read-only hosts still receives the append (cleared, then re-locked).
//
// HARD FENCE: only files inside the test bin directory are touched - never
// the real hosts file, the registry or the service.

using System.IO;

namespace MonkMode.Tests;

public class AdderDedupTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 www.reddit.com\r\n";
    private const string Added = "127.0.0.1 x.com\r\n127.0.0.1 www.x.com\r\n";
    private const string UserBase = "# my hosts\r\n127.0.0.1 my-dev-box\r\n";

    private static string TempPath(string tag) =>
        Path.Combine(AppContext.BaseDirectory, $"adder_{tag}_{Guid.NewGuid():N}.tmp");

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

    private static int CountOf(string text, string needle)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // The base case: one fire appends the trigger's entries to hosts AND the
    // snapshot, consumes the trigger, and leaves hosts read-only.
    [Fact]
    public void SingleFire_AppendsHostsAndSnapshot_ConsumesTrigger_RelocksHosts()
    {
        var trigger = TempPath("trigger");
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            File.WriteAllText(trigger, Added);
            File.WriteAllText(hosts, UserBase + Block);
            File.WriteAllText(snap, Block);

            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);

            Assert.Equal(UserBase + Block + Added, File.ReadAllText(hosts));
            Assert.Equal(Block + Added, File.ReadAllText(snap));
            Assert.False(File.Exists(trigger));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(trigger); Cleanup(hosts); Cleanup(snap); }
    }

    // Audit #6, the sequential shape: the second fire for the same write finds
    // the trigger already consumed and must NOT append again (but still leaves
    // hosts read-only).
    [Fact]
    public void DoubleFire_Sequential_AppendsExactlyOnce()
    {
        var trigger = TempPath("trigger");
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            File.WriteAllText(trigger, Added);
            File.WriteAllText(hosts, UserBase + Block);
            File.WriteAllText(snap, Block);

            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);
            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);

            Assert.Equal(1, CountOf(File.ReadAllText(hosts), Added));
            Assert.Equal(1, CountOf(File.ReadAllText(snap), Added));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(trigger); Cleanup(hosts); Cleanup(snap); }
    }

    // Audit #6, the racing shape: concurrent fires (the real FileSystemWatcher
    // delivery) must serialise on the handler's gate so exactly one of them
    // processes the trigger. Without the gate, fires could all read the
    // trigger before any deleted it and multi-append. With the gate this
    // passes DETERMINISTICALLY; against unserialised code the pin is
    // probabilistic per round (a lucky schedule could still append once), so
    // it runs many barrier-released rounds - a regression is caught with
    // near-certainty across them.
    [Fact]
    public async Task ConcurrentFires_AppendExactlyOnce()
    {
        const int rounds = 20;
        const int fires = 4;
        for (var round = 0; round < rounds; round++)
        {
            var trigger = TempPath("trigger");
            var hosts = TempPath("hosts");
            var snap = TempPath("snap");
            try
            {
                File.WriteAllText(trigger, Added);
                File.WriteAllText(hosts, UserBase + Block);
                File.WriteAllText(snap, Block);

                using var start = new Barrier(fires);
                var tasks = Enumerable.Range(0, fires).Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);
                })).ToArray();
                await Task.WhenAll(tasks);

                Assert.Equal(1, CountOf(File.ReadAllText(hosts), Added));
                Assert.Equal(1, CountOf(File.ReadAllText(snap), Added));
                Assert.False(File.Exists(trigger));
                Assert.True(IsReadOnly(hosts));
            }
            finally { Cleanup(trigger); Cleanup(hosts); Cleanup(snap); }
        }
    }

    // No snapshot on disk: hosts still gets the append but the snapshot must
    // NOT be invented - its presence is the B2 "block active" signal.
    [Fact]
    public void MissingSnapshot_HostsAppended_SnapshotNotInvented()
    {
        var trigger = TempPath("trigger");
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");   // deliberately never created
        try
        {
            File.WriteAllText(trigger, Added);
            File.WriteAllText(hosts, UserBase + Block);

            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);

            Assert.Equal(UserBase + Block + Added, File.ReadAllText(hosts));
            Assert.False(File.Exists(snap));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(trigger); Cleanup(hosts); Cleanup(snap); }
    }

    // No trigger at all (e.g. a late duplicate fire long after processing):
    // hosts content untouched, but the fail-closed re-lock still runs - the
    // live handler always leaves hosts read-only.
    [Fact]
    public void NoTrigger_NoContentChange_ButHostsRelocked()
    {
        var trigger = TempPath("trigger");   // never created
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            File.WriteAllText(hosts, UserBase + Block);   // writable (Normal)
            File.WriteAllText(snap, Block);
            Assert.False(IsReadOnly(hosts));

            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);

            Assert.Equal(UserBase + Block, File.ReadAllText(hosts));
            Assert.Equal(Block, File.ReadAllText(snap));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(trigger); Cleanup(hosts); Cleanup(snap); }
    }

    // The live state between timer ticks: hosts sits read-only. The handler
    // must clear the attribute, land the append, and re-lock.
    [Fact]
    public void ReadOnlyHosts_AppendStillLands_AndRelocks()
    {
        var trigger = TempPath("trigger");
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            File.WriteAllText(trigger, Added);
            File.WriteAllText(hosts, UserBase + Block);
            File.SetAttributes(hosts, FileAttributes.ReadOnly);
            File.WriteAllText(snap, Block);

            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);

            Assert.Equal(UserBase + Block + Added, File.ReadAllText(hosts));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(trigger); Cleanup(hosts); Cleanup(snap); }
    }
}
