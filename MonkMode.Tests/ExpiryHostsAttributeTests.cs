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

// MonkMode.Tests - ledger 313(b): a NATURAL expiry must not leave hosts read-only.
//
// The read-only attribute on hosts is enforcement - the DNS-client lock that stops a casual
// edit while a block stands - and the service re-asserts it every 10s tick, at OnStart and
// from the crash backstop. Its old expiry path re-asserted it one last time AFTER stripping
// the marker block, so a block that simply ran out left hosts locked with nothing left to
// enforce, and the next writer (Tailscale, a DNS tool) failed until a manual `attrib -r`. The
// CLI teardown has always ended with the attribute CLEAR; Samrath's decision (30/08/2026) is
// that the service's natural expiry matches it.
//
// WHAT THESE PIN, and the failure each prevents:
//   - genuine expiry => hosts ends NORMAL, our block gone, the user's own content byte-for-byte
//     intact (the no-data-loss fence, which the attribute change must not disturb);
//   - the strip FAILING => hosts ends READ-ONLY with the block still in it. The attribute is
//     cleared before the write, so a naive "clear at the end" would leave a still-blocking
//     hosts WRITABLE on the one path where enforcement did not actually end - fail-OPEN;
//   - nothing of ours in hosts => today's behaviour, unchanged (there is nothing to strip, so
//     that branch is not part of the expiry decision).
//
// HARD FENCE: every test drives Service1.StripHostsBlockAtExpiry against a temp file inside the
// test bin - never the real hosts file. No service is constructed, no block is armed, nothing
// is executed. (Service1.StripHostsBlockAtExpiry is Friend Shared for exactly this reason - the
// ReassertHostsFailClosed / ProcessAddToHosts pattern.)

using System.IO;

namespace MonkMode.Tests;

public class ExpiryHostsAttributeTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string UserContent = "# my hosts\r\n127.0.0.1 my-dev-box\r\n";
    private const string Blocked = UserContent + Marker + "\r\n127.0.0.1 reddit.com\r\n#### MonkMode End ####\r\n";

    private static string TempHosts(string contents, bool readOnly)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"hosts313_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, contents);
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

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

    [Fact]
    public void GenuineExpiry_StripsOurBlock_AndLeavesHostsWritable()
    {
        var path = TempHosts(Blocked, readOnly: true);
        try
        {
            Assert.True(monkmode.Service1.StripHostsBlockAtExpiry(path));

            // The block is gone and the user's own content survived byte-for-byte...
            Assert.Equal("# my hosts\r\n127.0.0.1 my-dev-box", File.ReadAllText(path));
            // ...and hosts is an ordinary file again: no manual `attrib -r` owed.
            Assert.False(IsReadOnly(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AFailedStrip_KeepsHostsReadOnly_WithTheBlockStillStanding()
    {
        // The fail-CLOSED half. The attribute is cleared before the rewrite, so if the write
        // throws, hosts still carries our entries - and must not be left writable.
        var path = TempHosts(Blocked, readOnly: true);
        try
        {
            monkmode.AtomicHosts.RenameHookForTests =
                (_, _) => throw new InvalidOperationException("simulated write failure");

            Assert.Throws<InvalidOperationException>(() => monkmode.Service1.StripHostsBlockAtExpiry(path));

            Assert.Contains(Marker, File.ReadAllText(path));
            Assert.True(IsReadOnly(path));
        }
        finally
        {
            monkmode.AtomicHosts.RenameHookForTests = null;
            Cleanup(path);
        }
    }

    [Fact]
    public void NothingOfOursInHosts_KeepsTodaysBehaviour()
    {
        // Unchanged branch, pinned so the 313(b) edit is provably confined to the strip path:
        // with no marker there is nothing to strip, the caller is told so (False => the config
        // is not marked Done), and the attribute assert stands exactly as it did.
        var path = TempHosts(UserContent, readOnly: false);
        try
        {
            Assert.False(monkmode.Service1.StripHostsBlockAtExpiry(path));
            Assert.Equal(UserContent, File.ReadAllText(path));
            Assert.True(IsReadOnly(path));
        }
        finally { Cleanup(path); }
    }
}
