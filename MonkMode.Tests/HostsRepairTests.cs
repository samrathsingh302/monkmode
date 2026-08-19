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

// MonkMode.Tests - B2 self-heal: hosts block repair.
//
// While a block is active the service re-asserts hosts' read-only attribute
// every 10s, but an admin can clear the attribute and edit/blank hosts between
// ticks. Service1.RepairHostsBlock decides, from the snapshot the CLI persisted
// when the block started (the marker line + entry lines, exactly as appended to
// hosts), whether hosts needs its MonkMode block restored and what the repaired
// file must be. Invariants:
//   - an intact block means NO write (null) - the timer must not churn hosts;
//   - a repair preserves the user's own content byte-for-byte (LF or CRLF) and
//     re-appends the snapshot after a single CRLF, so the expiry strip later
//     hands back exactly the user's pre-repair content;
//   - no/empty snapshot means no repair (null) - never invent hosts content.
//
// F35 (v1.1 FX7): what a writer puts into hosts is the snapshot block PLUS the
// end-marker line, so the block has a known end and user content below it
// survives. The snapshot on disk is unchanged (marker + entry lines); every
// expectation below therefore compares against Written(Block), not Block.
//
// Everything here is in-memory strings - the real hosts file is never touched.

namespace MonkMode.Tests;

public class ServiceRepairHostsBlockTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string EndMarker = "#### MonkMode End ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 www.reddit.com\r\n";

    // The hosts-side form of a snapshot block: what every writer emits (F35).
    private const string Written = Block + EndMarker + "\r\n";

    [Fact]
    public void IntactBlock_WithUserContentAbove_ReturnsNull()
    {
        var hosts = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Written;
        Assert.Null(monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void IntactBlock_WholeFileIsOurs_ReturnsNull()
    {
        Assert.Null(monkmode.Service1.RepairHostsBlock(Written, Block));
    }

    [Fact]
    public void LegacyBlockWithNoEndMarker_ConvergesInOneRewrite()
    {
        // F35 legacy rule: a pre-FX7 block is intact by the OLD test (hosts
        // contains the snapshot verbatim) but carries no end marker, so the
        // repair rewrites it once - and from then on it is stable (null).
        var legacy = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Block;
        var converged = monkmode.Service1.RepairHostsBlock(legacy, Block);
        Assert.Equal("# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Written, converged);
        Assert.Null(monkmode.Service1.RepairHostsBlock(converged, Block));
    }

    [Fact]
    public void EndMarkerDeleted_IsTamperingInsideTheBlock_AndIsRepaired()
    {
        // Deleting the end marker is an edit INSIDE our block: the self-heal
        // must put it back, exactly as it does for a deleted entry line.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Block;
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Written,
            monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void UserLineBelowTheEndMarker_IsPreservedInPlaceByARepair()
    {
        // THE F35 REPRO on the self-heal path: the user's own line sits below
        // our end marker; a repair (here: an entry line deleted) must keep it,
        // and keep it BELOW our block so it can never out-rank our entries.
        var tampered = "# my hosts\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n" +
                       EndMarker + "\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# my hosts\r\n" + Written + "10.0.0.5 nas.home\r\n",
            monkmode.Service1.RepairHostsBlock(tampered, Block));
    }

    [Fact]
    public void ExtraLinesBelowIntactBlock_ReturnsNull()
    {
        // Content below an intact block doesn't weaken enforcement, so no
        // repair churn - and since F35 it is the USER's content, kept for good.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Written + "127.0.0.1 added-later\r\n";
        Assert.Null(monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void MarkerLineDeletedEntirely_RestoredWithUserContentIntact()
    {
        // The classic tamper: clear read-only, delete the whole MonkMode block.
        var userContent = "# my hosts\r\n127.0.0.1 my-dev-box";
        Assert.Equal(userContent + "\r\n" + Written,
            monkmode.Service1.RepairHostsBlock(userContent, Block));
    }

    [Fact]
    public void EntriesPartiallyRemovedBelowMarker_RepairedToExpected()
    {
        // Marker kept, but one of our entry lines deleted.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n" + EndMarker + "\r\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Written,
            monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void EntriesEditedBelowMarker_RepairedToExpected()
    {
        // Marker kept, entries rewritten to something harmless-looking.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n# nothing to see here\r\n" + EndMarker + "\r\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Written,
            monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void HostsBlankedEntirely_RepairedEqualsExpectedBlock()
    {
        Assert.Equal(Written, monkmode.Service1.RepairHostsBlock("", Block));
    }

    [Fact]
    public void NullHostsText_RepairedEqualsExpectedBlock()
    {
        // Mirrors a deleted hosts file: the timer reads "nothing" and the
        // repair recreates the file as just our block.
        Assert.Equal(Written, monkmode.Service1.RepairHostsBlock(null!, Block));
    }

    [Fact]
    public void LfEndingUserContent_PreservedExactly_AndLiftRoundTrips()
    {
        // LF-ending user content (block deleted) survives byte-for-byte, and
        // the expiry strip of the repaired text gives back EXACTLY the
        // pre-repair file - the repair must never erode user content.
        var userContent = "# mine\n127.0.0.1 my-dev-box\n";
        var repaired = monkmode.Service1.RepairHostsBlock(userContent, Block);
        Assert.Equal(userContent + "\r\n" + Written, repaired);
        Assert.Equal(userContent, monkmode.Service1.StripMonkModeBlock(repaired));
    }

    [Fact]
    public void CrLfEndingUserContent_PreservedExactly_AndLiftRoundTrips()
    {
        var userContent = "# mine\r\n127.0.0.1 my-dev-box\r\n";
        var repaired = monkmode.Service1.RepairHostsBlock(userContent, Block);
        Assert.Equal(userContent + "\r\n" + Written, repaired);
        Assert.Equal(userContent, monkmode.Service1.StripMonkModeBlock(repaired));
    }

    [Fact]
    public void TamperedBlockWithLfBeforeMarker_RepairKeepsUserText()
    {
        // Strip semantics carry over: only the single line terminator before
        // the (tampered) marker block is treated as ours; the separator is
        // re-normalised to CRLF on repair.
        var hosts = "127.0.0.1 my-dev-box\n" + Marker + "\n127.0.0.1 reddit.com\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Written,
            monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void NullEmptyOrWhitespaceSnapshot_ReturnsNull_NeverInventsContent(string? snapshot)
    {
        var hosts = "# my hosts\r\n127.0.0.1 my-dev-box\r\n";
        Assert.Null(monkmode.Service1.RepairHostsBlock(hosts, snapshot!));
    }
}

public class CliSnapshotParityTests
{
    [Fact]
    public void BuildMonkModeBlock_IsMarkerLinePlusHostsEntries()
    {
        // The snapshot the CLI persists is built by the same function that
        // produces the text appended to hosts, so the two can never drift.
        var domains = new[] { "reddit.com", "x.com" };
        Assert.Equal(MonkMode.Blocker.Marker + "\r\n" + MonkMode.Blocker.BuildHostsEntries(domains),
            MonkMode.Blocker.BuildMonkModeBlock(domains));
    }

    [Fact]
    public void CliLayout_TamperedBackToBase_ServiceRepairReproducesExactWrittenLayout()
    {
        // Cross-project round-trip: WriteHostsBlock writes base + CRLF + block
        // and snapshots block. If an admin reverts hosts to just their own
        // base text, the service repair must reproduce the CLI's exact hosts
        // layout - and an intact layout must repair to null (no churn).
        var baseText = "# my hosts\r\n127.0.0.1 my-dev-box";
        var block = MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com" });
        // F35: hosts carries the block plus its end marker; the snapshot does not.
        var written = baseText + "\r\n" + block + MonkMode.Blocker.EndMarker + "\r\n";
        Assert.Null(monkmode.Service1.RepairHostsBlock(written, block));
        Assert.Equal(written, monkmode.Service1.RepairHostsBlock(baseText, block));
    }
}
