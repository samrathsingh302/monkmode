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

// MonkMode.Tests - B5a browser-DoH-off policy: the PURE decisions.
//
// Flipping on a browser's "Secure DNS" (DNS-over-HTTPS) tunnels DNS over
// HTTPS:443 and bypasses the hosts file entirely - the #1 real bypass. B5a forces
// the enterprise "DoH off" policy value for Edge/Chrome/Brave/Firefox and
// self-heals it every tick (like the B3 SafeBoot registration), restoring the
// user's prior value at expiry (no data loss).
//
// The live registry / snapshot-file I/O (CreateSubKey/SetValue/DeleteValue,
// File.Write/Read) is the elevated smoke-test's job. What IS pure and unit-tested
// here (the SERVICE copy; DohPolicyParityTests pins the CLI copy identical):
//   - DohPolicy.Entries: the curated policy list - a vendor-key rename must be a
//     LOUD build failure, not a silent disarm (mirrors the SafeBoot const pinning).
//   - ValueIsBlocked: the write-vs-skip probe the re-assert uses so an already-off
//     value is a no-op (ordinal, case-sensitive for REG_SZ; numeric for DWORD).
//   - RestoreActionFor: the no-data-loss teardown decision (restore prior / delete).
//   - BuildSnapshot/ParseSnapshot: the snapshot round-trip (typed by Kind).
//
// Everything here is in-memory values - the real registry is never touched. The
// internal DohPolicyEntry struct is reachable via InternalsVisibleTo (aliased
// `Entry` below); no `dynamic` (whose binder would not honour the friend access).

using Microsoft.Win32;
using Entry = monkmode.DohPolicy.DohPolicyEntry;

namespace MonkMode.Tests;

public class DohPolicyConstantsTests
{
    // The exact curated list, pinned literally. A vendor renaming a policy key or
    // value (they change between browser versions) must fail THIS test loudly so
    // the list is re-verified - not silently stop blocking DoH. Matched
    // order-independently: a reorder is fine, a content drift is caught.
    private static readonly (string SubKey, string ValueName, RegistryValueKind Kind, object Blocked)[] Expected =
    {
        (@"SOFTWARE\Policies\Microsoft\Edge",               "DnsOverHttpsMode", RegistryValueKind.String, "off"),
        (@"SOFTWARE\Policies\Google\Chrome",                "DnsOverHttpsMode", RegistryValueKind.String, "off"),
        (@"SOFTWARE\Policies\BraveSoftware\Brave",          "DnsOverHttpsMode", RegistryValueKind.String, "off"),
        (@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled",          RegistryValueKind.DWord,  0),
        (@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked",           RegistryValueKind.DWord,  1),
    };

    [Fact]
    public void Entries_AreExactlyTheCuratedList()
    {
        var entries = monkmode.DohPolicy.Entries;
        Assert.Equal(Expected.Length, entries.Length);
        foreach (var e in Expected)
        {
            Assert.Contains(entries, a =>
                a.SubKey == e.SubKey &&
                a.ValueName == e.ValueName &&
                a.Kind == e.Kind &&
                Equals(a.BlockedValue, e.Blocked));
        }
    }

    [Fact]
    public void EveryEntry_LivesUnderTheMachinePolicyRoot()
    {
        // All under HKLM\SOFTWARE\Policies\... - a wrong root would write a
        // harmless junk key and leave DoH un-blocked (the B3-style silent disarm).
        foreach (var e in monkmode.DohPolicy.Entries)
            Assert.StartsWith(@"SOFTWARE\Policies\", e.SubKey);
    }

    [Fact]
    public void ChromiumEntries_AreRegSzOff_AndFirefox_IsDword()
    {
        // The heterogeneity the module exists to model: Chromium = REG_SZ "off";
        // Firefox = DWORD. A Kind flip would make the live SetValue write the wrong
        // registry type and the policy would not take effect.
        foreach (var e in monkmode.DohPolicy.Entries)
        {
            if (e.SubKey.Contains("Mozilla"))
                Assert.Equal(RegistryValueKind.DWord, e.Kind);
            else
            {
                Assert.Equal(RegistryValueKind.String, e.Kind);
                Assert.Equal("off", e.BlockedValue);
            }
        }
    }
}

public class DohPolicyValueIsBlockedTests
{
    private static readonly Entry Edge =
        System.Linq.Enumerable.First(monkmode.DohPolicy.Entries, e => e.SubKey.Contains("Edge"));
    private static readonly Entry FirefoxEnabled =
        System.Linq.Enumerable.First(monkmode.DohPolicy.Entries, e => e.ValueName == "Enabled"); // blocked = 0
    private static readonly Entry FirefoxLocked =
        System.Linq.Enumerable.First(monkmode.DohPolicy.Entries, e => e.ValueName == "Locked");  // blocked = 1

    [Fact]
    public void Chromium_ExactlyOff_IsBlocked()
    {
        Assert.True(monkmode.DohPolicy.ValueIsBlocked(Edge, "off"));
    }

    [Theory]
    [InlineData("automatic")]  // the DoH-ON values
    [InlineData("secure")]
    [InlineData("")]           // blank = not our "off"
    [InlineData("OFF")]        // ordinal + case-SENSITIVE, like SafeBootValueIsCorrect
    [InlineData("Off")]
    [InlineData(" off")]       // stray whitespace
    public void Chromium_AnythingElse_NeedsRewrite(string current)
    {
        Assert.False(monkmode.DohPolicy.ValueIsBlocked(Edge, current));
    }

    [Fact]
    public void Chromium_AbsentValue_NeedsRewrite()
    {
        // A never-set value reads null and must not throw.
        Assert.False(monkmode.DohPolicy.ValueIsBlocked(Edge, null!));
    }

    [Fact]
    public void FirefoxEnabled_Zero_IsBlocked_NonZeroOrAbsent_NeedsRewrite()
    {
        Assert.True(monkmode.DohPolicy.ValueIsBlocked(FirefoxEnabled, 0));   // Enabled=0 => DoH off
        Assert.False(monkmode.DohPolicy.ValueIsBlocked(FirefoxEnabled, 1));
        Assert.False(monkmode.DohPolicy.ValueIsBlocked(FirefoxEnabled, null!));
    }

    [Fact]
    public void FirefoxLocked_One_IsBlocked_ZeroOrAbsent_NeedsRewrite()
    {
        Assert.True(monkmode.DohPolicy.ValueIsBlocked(FirefoxLocked, 1));    // Locked=1 => can't re-enable
        Assert.False(monkmode.DohPolicy.ValueIsBlocked(FirefoxLocked, 0));
        Assert.False(monkmode.DohPolicy.ValueIsBlocked(FirefoxLocked, null!));
    }
}

public class DohPolicyRestoreActionForTests
{
    private static readonly Entry Edge =
        System.Linq.Enumerable.First(monkmode.DohPolicy.Entries, e => e.SubKey.Contains("Edge"));
    private static readonly Entry FirefoxEnabled =
        System.Linq.Enumerable.First(monkmode.DohPolicy.Entries, e => e.ValueName == "Enabled");

    // The tuple is (delete As Boolean, value As Object) -> .Item1 = delete, .Item2 = value.

    [Fact]
    public void PriorValuePresent_RestoresIt()
    {
        // The user had DnsOverHttpsMode = "automatic" before the block => teardown
        // must restore that exact value, not delete it (no data loss).
        var action = monkmode.DohPolicy.RestoreActionFor(Edge, "automatic");
        Assert.False(action.Item1);               // delete == false
        Assert.Equal("automatic", action.Item2);  // value == the prior
    }

    [Fact]
    public void PriorValueAbsent_DeletesOurs()
    {
        // The user had no policy before the block (null snapshot) => teardown must
        // DELETE our value (leaving the shared vendor key otherwise untouched).
        var action = monkmode.DohPolicy.RestoreActionFor(Edge, null);
        Assert.True(action.Item1);                // delete == true
        Assert.Null(action.Item2);
    }

    [Fact]
    public void PriorDwordPresent_RestoresIt()
    {
        var action = monkmode.DohPolicy.RestoreActionFor(FirefoxEnabled, 2);
        Assert.False(action.Item1);
        Assert.Equal(2, action.Item2);
    }
}

public class DohPolicySnapshotRoundTripTests
{
    [Fact]
    public void RoundTrip_PreservesPresentAndAbsent_TypedByKind()
    {
        // A mixed snapshot: Edge absent, Chrome had "automatic", Brave absent,
        // Firefox Enabled had 2, Locked absent. Parse must return the values TYPED
        // per Kind (Int32 for the DWORD, String for REG_SZ) and null for absent.
        var priors = new object?[] { null, "automatic", null, 2, null };
        var text = monkmode.DohPolicy.BuildSnapshot(priors!);
        var parsed = monkmode.DohPolicy.ParseSnapshot(text);

        Assert.Equal(priors.Length, parsed.Length);
        Assert.Null(parsed[0]);
        Assert.Equal("automatic", parsed[1]);
        Assert.Null(parsed[2]);
        Assert.Equal(2, parsed[3]);      // typed back to Int32, not "2"
        Assert.Null(parsed[4]);
    }

    [Fact]
    public void AllAbsent_RoundTripsToAllNull()
    {
        var priors = new object?[] { null, null, null, null, null };
        var parsed = monkmode.DohPolicy.ParseSnapshot(monkmode.DohPolicy.BuildSnapshot(priors!));
        Assert.All(parsed, v => Assert.Null(v));
    }

    [Fact]
    public void EmptyOrMissingText_ParsesToAllNull()
    {
        // A missing/blank snapshot yields an all-absent array (=> teardown removes
        // only our lingering "off"), never a throw.
        foreach (var text in new[] { "", "   ", null! })
            Assert.All(monkmode.DohPolicy.ParseSnapshot(text), v => Assert.Null(v));
    }

    [Fact]
    public void CorruptDwordLine_TreatedAsAbsent()
    {
        // A snapshot whose Firefox Enabled line is non-numeric must fail SAFE:
        // treat it as absent (=> delete our value) rather than write garbage back.
        var priors = new object?[] { null, null, null, 0, null };
        var text = monkmode.DohPolicy.BuildSnapshot(priors!)
            .Replace("\tP\t0", "\tP\tnot-a-number");
        var parsed = monkmode.DohPolicy.ParseSnapshot(text);
        Assert.Null(parsed[3]);
    }
}
