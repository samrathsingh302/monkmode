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

// MonkMode.Tests - 05/10 audit A11: the per-tick hosts read-only re-assert runs only while
// something is HELD.
//
// The service used to set hosts read-only on every 10 s tick, unconditionally. A
// schedule-only config BETWEEN windows keeps the service alive with nothing held
// (ClassifyHeartbeat -> Restamp), so the user's hosts stayed read-only for every other
// writer (Tailscale, a DNS tool) - the leftover 313(b)/F78 already removed at teardown.
// The re-assert is now gated on Service1.ShouldReassertHostsReadOnly, pinned here. The tick
// itself is not unit-drivable: the wiring at the re-assert site is review-proven.

namespace MonkMode.Tests;

public class HostsReadOnlyGateTests
{
    [Theory]
    [InlineData(false, false, false)]  // between schedule windows / nothing armed: leave hosts alone
    [InlineData(true, false, true)]    // the v9 mirror holds (manual block, open window, or a bad MAC)
    [InlineData(false, true, true)]    // only a slot holds
    [InlineData(true, true, true)]
    public void TheReassertRunsExactlyWhileSomethingIsHeld(bool blockHeld, bool slotsHeld, bool expected)
    {
        Assert.Equal(expected, monkmode.Service1.ShouldReassertHostsReadOnly(blockHeld, slotsHeld));
    }

    [Fact]
    public void TheGatesInputsAreEnforcementHeldsTwoTerms_SoABadMacStillRelocks()
    {
        // Service1.vb's tick computes
        //     mirrorHeld = BlockHeld(iniUntil, newHwAsOf, grace, macValid, iniScheduleActiveUntil, newHw)
        //     enforcementHeld = mirrorHeld OrElse slotsHeld
        // and passes the SAME two terms to ShouldReassertHostsReadOnly, so the re-assert and
        // every other self-heal agree on "held". Pinned as: the gate equals that fold on every
        // input...
        foreach (var blockHeld in new[] { false, true })
            foreach (var slotsHeld in new[] { false, true })
                Assert.Equal(blockHeld || slotsHeld,
                             monkmode.Service1.ShouldReassertHostsReadOnly(blockHeld, slotsHeld));

        // ...and the fail-closed row reaches it: a MAC-invalid config makes BlockHeld True
        // even with no Until and no window, so a tampered config still re-locks hosts.
        var mirrorHeld = monkmode.Service1.BlockHeld("", DateTime.MinValue, 5, false, "", "");
        Assert.True(mirrorHeld);
        Assert.True(monkmode.Service1.ShouldReassertHostsReadOnly(mirrorHeld, false));
    }
}
