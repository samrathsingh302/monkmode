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

// MonkMode.Tests - AppDomain.UnhandledException backstop (fail-closed on crash).
//
// If an unhandled exception escapes every local handler and is about to terminate
// an enforcement process, a last-line backstop re-asserts the block BEFORE the
// process dies, so a crash can never leave it fail-OPEN. Scope (which process needs
// what):
//   - SERVICE (Service1): the ONLY process that clears hosts' read-only attribute
//     to write, so the only one with a real fail-OPEN window (writable / stripped /
//     deleted hosts). Its backstop restores the marker block when the block is still
//     active and ALWAYS re-locks hosts read-only. Covered end-to-end here against
//     TEMP files (the live wiring), plus the pure RepairHostsBlock decision it reuses
//     (HostsRepairTests).
//   - GUARDIAN (MM_guard): holds no fail-OPEN state; its crash re-assert reuses the
//     already-tested pure gates (Guardian.EffectiveBlockHasExpired fail-closed +
//     Guardian.ShouldRestartService - see GuardianTests). Nothing new to pin here.
//   - NOTIFIER (MM_notify): holds no hosts state; its crash re-assert clears the
//     non-MAC-covered [Time] TimeChanging flag (a live ini write, smoke-tested).
//
// Service1.ReassertHostsFailClosed(hostsPath, snapshotPath) is the testable core:
// the parameterless live wrapper just feeds it the real hosts + snapshot paths.
// Invariants pinned:
//   - block ACTIVE (snapshot on disk) + hosts blanked/stripped/deleted => our block
//     rebuilt, the user's own content preserved byte-for-byte, hosts left read-only;
//   - block ACTIVE + hosts intact => NO churn, but still left read-only (closes a
//     "crash left read-only cleared" window even when the content was fine);
//   - block ENDED (stopMe() already deleted the snapshot) => NOT re-blocked, but a
//     surviving hosts is still left read-only;
//   - never throws, never fabricates a hosts file when there is nothing to enforce.
//
// HARD FENCE: only files inside the test bin directory are touched - never the real
// hosts file, the registry or the service.

using System.IO;

namespace MonkMode.Tests;

public class AppDomainBackstopServiceTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string EndMarker = "#### MonkMode End ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 www.reddit.com\r\n";

    // F35 (v1.1 FX7): the hosts-side form of the snapshot block - what every writer
    // emits. The snapshot file itself stays the plain marker + entry lines.
    private const string Written = Block + EndMarker + "\r\n";

    private static string TempPath(string tag) =>
        Path.Combine(AppContext.BaseDirectory, $"backstop_{tag}_{Guid.NewGuid():N}.tmp");

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

    // Active block, hosts blanked by a crash mid-write: our block must be rebuilt
    // (the core fail-OPEN closure - a blanked hosts resolves every site) and hosts
    // left read-only.
    [Fact]
    public void ActiveBlock_HostsBlanked_RestoresBlock_AndReadOnly()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            File.WriteAllText(hosts, "");
            File.WriteAllText(snap, Block);

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);

            Assert.Equal(Written, File.ReadAllText(hosts));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }

    // Active block, our block stripped but the user's own hosts lines remain: the
    // block is re-appended AND the user's content is preserved byte-for-byte
    // (no-data-loss fence), hosts left read-only.
    [Fact]
    public void ActiveBlock_BlockStripped_RestoresAndPreservesUserContent()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            var user = "# my hosts\r\n127.0.0.1 my-dev-box";
            File.WriteAllText(hosts, user);
            File.WriteAllText(snap, Block);

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);

            Assert.Equal(user + "\r\n" + Written, File.ReadAllText(hosts));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }

    // Active block, a crash DELETED hosts entirely: rebuilt from the snapshot (a
    // missing hosts is as fail-OPEN as a blanked one), left read-only. Mirrors the
    // timer self-heal recreating a deleted hosts.
    [Fact]
    public void ActiveBlock_HostsDeleted_RecreatesFromSnapshot_AndReadOnly()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            File.WriteAllText(snap, Block);
            Assert.False(File.Exists(hosts));

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);

            Assert.True(File.Exists(hosts));
            Assert.Equal(Written, File.ReadAllText(hosts));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }

    // Active block, hosts intact but a crash left read-only CLEARED (the classic
    // adder_Changed fail-OPEN window): no content churn, but the attribute is
    // re-asserted. This is the single most important guarantee.
    [Fact]
    public void ActiveBlock_HostsIntactButWritable_NoChurn_ButReLocked()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            var full = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Written;
            File.WriteAllText(hosts, full);            // writable (Normal attrs)
            File.WriteAllText(snap, Block);
            Assert.False(IsReadOnly(hosts));

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);

            Assert.Equal(full, File.ReadAllText(hosts));   // unchanged (no churn)
            Assert.True(IsReadOnly(hosts));                // but re-locked
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }

    // Block ENDED: stopMe() already deleted the snapshot, so the backstop must NOT
    // re-block a legitimately-expired block - snapshot absence is the fail-closed
    // "do not re-block" signal. A surviving hosts is still left read-only (harmless;
    // stopMe leaves hosts read-only at expiry by design too).
    [Fact]
    public void EndedBlock_NoSnapshot_DoesNotReblock_ButReLocks()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");   // deliberately never created
        try
        {
            var user = "# my hosts\r\n127.0.0.1 my-dev-box\r\n";
            File.WriteAllText(hosts, user);

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);

            Assert.Equal(user, File.ReadAllText(hosts));       // NOT re-blocked
            Assert.DoesNotContain(Marker, File.ReadAllText(hosts));
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }

    // Nothing to enforce (block ended AND hosts already gone): must not throw and
    // must not fabricate a hosts file out of nothing.
    [Fact]
    public void EndedBlock_NoSnapshot_NoHosts_NoThrow_AndDoesNotCreateHosts()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            Assert.False(File.Exists(hosts));
            Assert.False(File.Exists(snap));

            var ex = Record.Exception(() => monkmode.Service1.ReassertHostsFailClosed(hosts, snap));

            Assert.Null(ex);
            Assert.False(File.Exists(hosts));
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }

    // Idempotent: running the backstop twice (e.g. a crash during recovery) leaves
    // exactly the enforced state, never a doubled or churned block.
    [Fact]
    public void RunTwice_IsIdempotent()
    {
        var hosts = TempPath("hosts");
        var snap = TempPath("snap");
        try
        {
            var user = "# my hosts\r\n127.0.0.1 my-dev-box";
            File.WriteAllText(hosts, user);
            File.WriteAllText(snap, Block);

            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);
            var afterFirst = File.ReadAllText(hosts);
            monkmode.Service1.ReassertHostsFailClosed(hosts, snap);
            var afterSecond = File.ReadAllText(hosts);

            Assert.Equal(user + "\r\n" + Written, afterFirst);
            Assert.Equal(afterFirst, afterSecond);
            Assert.True(IsReadOnly(hosts));
        }
        finally { Cleanup(hosts); Cleanup(snap); }
    }
}
