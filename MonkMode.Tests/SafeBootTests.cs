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

// MonkMode.Tests - B3 Safe Mode resistance: the SafeBoot registration.
//
// Booting into Safe Mode normally leaves the MONKMODE service un-started, so an
// evader could edit hosts / delete files / sc-delete the service unopposed. The
// service registers itself under the SafeBoot Minimal + Network keys so it DOES
// run in Safe Mode, re-asserts those keys every timer tick (self-heal, like the
// hosts read-only lock), and removes them at a genuine expiry.
//
// The live registry I/O (CreateSubKey/SetValue/DeleteSubKeyTree) touches HKLM
// and so is covered by the elevated smoke test, not here. What IS pure and
// unit-tested:
//   - Service1.SafeBootValueIsCorrect: the write-vs-skip gate the re-assert uses
//     so an intact registration is a no-op (ordinal "Service", nothing else).
//   - The path / tag constants: a typo in a SafeBoot path or the "Service" tag
//     would silently disarm B3, so the single-source-of-truth Friend Consts are
//     pinned here and the test fails loudly on drift (mirrors the B1 recovery
//     policy pinning in WatchdogTests).
//
// Everything here is in-memory values - the real registry is never touched.

namespace MonkMode.Tests;

public class SafeBootValueIsCorrectTests
{
    [Fact]
    public void ExactServiceTag_IsCorrect()
    {
        // The only intact value: no rewrite needed this tick.
        Assert.True(monkmode.Service1.SafeBootValueIsCorrect("Service"));
    }

    [Theory]
    [InlineData("service")]   // wrong case - ordinal, so NOT correct
    [InlineData("SERVICE")]
    [InlineData("Driver")]    // the driver tag, not ours
    [InlineData("Service ")]  // trailing space
    [InlineData(" Service")]  // leading space
    [InlineData("")]          // blank default value (key present, tag cleared)
    [InlineData(null)]        // value absent entirely
    public void AnythingElse_NeedsRewrite(string? current)
    {
        // Any deviation reads as "needs a rewrite" so the re-assert restores the
        // canonical tag; null must not throw (a freshly created key reads null).
        Assert.False(monkmode.Service1.SafeBootValueIsCorrect(current!));
    }
}

public class SafeBootRegistrationConstantsTests
{
    // The (Default) tag SafeBoot entries conventionally carry for a service.
    [Fact]
    public void Tag_IsServiceOrdinal()
    {
        Assert.Equal("Service", monkmode.Service1.SafeBootValue);
    }

    // Both keys must live under the real SafeBoot control path - a wrong root
    // would write a harmless junk key and leave the service absent in Safe Mode.
    [Fact]
    public void BothKeys_LiveUnderTheSafeBootControlPath()
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\SafeBoot\";
        Assert.StartsWith(root, monkmode.Service1.SafeBootMinimalKey);
        Assert.StartsWith(root, monkmode.Service1.SafeBootNetworkKey);
    }

    // Minimal => plain Safe Mode; Network => Safe Mode with Networking. Both are
    // registered so neither Safe Mode variant leaves enforcement off.
    [Fact]
    public void MinimalKey_IsTheMinimalSubtree()
    {
        Assert.Equal(
            @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE",
            monkmode.Service1.SafeBootMinimalKey);
    }

    [Fact]
    public void NetworkKey_IsTheNetworkSubtree()
    {
        Assert.Equal(
            @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\MONKMODE",
            monkmode.Service1.SafeBootNetworkKey);
    }

    // The subkey is named after the service (that is what SafeBoot matches on),
    // and the two keys differ ONLY in the Minimal/Network segment.
    [Fact]
    public void BothKeys_AreNamedAfterTheService()
    {
        Assert.EndsWith(@"\MONKMODE", monkmode.Service1.SafeBootMinimalKey);
        Assert.EndsWith(@"\MONKMODE", monkmode.Service1.SafeBootNetworkKey);
        Assert.Equal(
            monkmode.Service1.SafeBootNetworkKey,
            monkmode.Service1.SafeBootMinimalKey.Replace(@"\Minimal\", @"\Network\"));
    }
}
