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

// MonkMode.Tests - B5a DoH-policy CLI<->service parity (the MANDATORY test that
// B3 forgot until 30/06/2026 - see SafeBootParityTests for that backfill).
//
// The browser-DoH policy list is declared TWICE, in two assemblies:
//   - the service WRITES + self-heals it:   monkmode.DohPolicy.Entries
//   - the CLI snapshots it at block start and the `unblock --force` escape hatch
//     RESTORES it:                          MonkMode.DohPolicy.Entries
// DohPolicy.vb is byte-for-byte identical in both projects except its namespace
// (like Simple3Des / ConfigIntegrity / ServiceSecurity), but nothing MECHANICALLY
// stops a one-sided edit. If the two copies drift, the service would force one set
// of keys off while the CLI snapshotted/restored a DIFFERENT set - a stuck policy
// the escape hatch can't lift, or the WRONG prior value written back (a
// no-data-loss violation). This test compares the REAL Entries from both
// assemblies element-for-element so a one-sided edit fails the build (mirrors the
// B7 CanonicalParityTests cross-assembly approach and SafeBootParityTests).
//
// In-memory only - the real registry / snapshot files are never touched.

namespace MonkMode.Tests;

public class DohPolicyParityTests
{
    [Fact]
    public void CliEntries_MatchServiceEntries_ElementForElement()
    {
        var srv = monkmode.DohPolicy.Entries;
        var cli = MonkMode.DohPolicy.Entries;

        Assert.Equal(srv.Length, cli.Length);
        for (int i = 0; i < srv.Length; i++)
        {
            Assert.Equal(srv[i].SubKey, cli[i].SubKey);
            Assert.Equal(srv[i].ValueName, cli[i].ValueName);
            Assert.Equal(srv[i].Kind, cli[i].Kind);        // same framework enum in both
            Assert.Equal(srv[i].BlockedValue, cli[i].BlockedValue); // boxed string/int, value-equal
        }
    }

    [Fact]
    public void Entries_AreNonEmpty_AndEveryFieldIsReal()
    {
        // Guard against the degenerate "both blank" pass (the SafeBootParityTests
        // Keys_AreNonEmpty analogue): refuse a vacuous equality if someone cleared
        // both copies at once. Every entry must carry a real subkey + value name.
        var cli = MonkMode.DohPolicy.Entries;
        Assert.NotEmpty(cli);
        foreach (var e in cli)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.SubKey));
            Assert.False(string.IsNullOrWhiteSpace(e.ValueName));
            Assert.NotNull(e.BlockedValue);
        }
    }

    [Fact]
    public void SnapshotFormat_IsCrossCompatible_CliWrites_ServiceReads()
    {
        // The snapshot the CLI persists at block start is read back by the SERVICE
        // (its own RemoveDohPolicy) at expiry - the two must agree on the format or
        // the restore would misparse and either lose the prior value or leave ours.
        // Build with the CLI copy, parse with the service copy, expect identity.
        var priors = new object?[] { null, "automatic", null, 3, null };
        var text = MonkMode.DohPolicy.BuildSnapshot(priors!);
        var parsed = monkmode.DohPolicy.ParseSnapshot(text);

        Assert.Equal(priors.Length, parsed.Length);
        Assert.Null(parsed[0]);
        Assert.Equal("automatic", parsed[1]);
        Assert.Null(parsed[2]);
        Assert.Equal(3, parsed[3]);
        Assert.Null(parsed[4]);
    }
}
