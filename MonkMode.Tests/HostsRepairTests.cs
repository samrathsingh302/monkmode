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
// Everything here is in-memory strings - the real hosts file is never touched.

namespace MonkMode.Tests;

public class ServiceRepairHostsBlockTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 www.reddit.com\r\n";

    [Fact]
    public void IntactBlock_WithUserContentAbove_ReturnsNull()
    {
        var hosts = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Block;
        Assert.Null(monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void IntactBlock_WholeFileIsOurs_ReturnsNull()
    {
        Assert.Null(monkmode.Service1.RepairHostsBlock(Block, Block));
    }

    [Fact]
    public void ExtraLinesBelowIntactBlock_ReturnsNull()
    {
        // Content below an intact block doesn't weaken enforcement and is
        // MonkMode's to remove at expiry anyway, so no repair churn.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Block + "127.0.0.1 added-later\r\n";
        Assert.Null(monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void MarkerLineDeletedEntirely_RestoredWithUserContentIntact()
    {
        // The classic tamper: clear read-only, delete the whole MonkMode block.
        var userContent = "# my hosts\r\n127.0.0.1 my-dev-box";
        Assert.Equal(userContent + "\r\n" + Block,
            monkmode.Service1.RepairHostsBlock(userContent, Block));
    }

    [Fact]
    public void EntriesPartiallyRemovedBelowMarker_RepairedToExpected()
    {
        // Marker kept, but one of our entry lines deleted.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Block,
            monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void EntriesEditedBelowMarker_RepairedToExpected()
    {
        // Marker kept, entries rewritten to something harmless-looking.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n# nothing to see here\r\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Block,
            monkmode.Service1.RepairHostsBlock(hosts, Block));
    }

    [Fact]
    public void HostsBlankedEntirely_RepairedEqualsExpectedBlock()
    {
        Assert.Equal(Block, monkmode.Service1.RepairHostsBlock("", Block));
    }

    [Fact]
    public void NullHostsText_RepairedEqualsExpectedBlock()
    {
        // Mirrors a deleted hosts file: the timer reads "nothing" and the
        // repair recreates the file as just our block.
        Assert.Equal(Block, monkmode.Service1.RepairHostsBlock(null!, Block));
    }

    [Fact]
    public void LfEndingUserContent_PreservedExactly_AndLiftRoundTrips()
    {
        // LF-ending user content (block deleted) survives byte-for-byte, and
        // the expiry strip of the repaired text gives back EXACTLY the
        // pre-repair file - the repair must never erode user content.
        var userContent = "# mine\n127.0.0.1 my-dev-box\n";
        var repaired = monkmode.Service1.RepairHostsBlock(userContent, Block);
        Assert.Equal(userContent + "\r\n" + Block, repaired);
        Assert.Equal(userContent, monkmode.Service1.StripMonkModeBlock(repaired));
    }

    [Fact]
    public void CrLfEndingUserContent_PreservedExactly_AndLiftRoundTrips()
    {
        var userContent = "# mine\r\n127.0.0.1 my-dev-box\r\n";
        var repaired = monkmode.Service1.RepairHostsBlock(userContent, Block);
        Assert.Equal(userContent + "\r\n" + Block, repaired);
        Assert.Equal(userContent, monkmode.Service1.StripMonkModeBlock(repaired));
    }

    [Fact]
    public void TamperedBlockWithLfBeforeMarker_RepairKeepsUserText()
    {
        // Strip semantics carry over: only the single line terminator before
        // the (tampered) marker block is treated as ours; the separator is
        // re-normalised to CRLF on repair.
        var hosts = "127.0.0.1 my-dev-box\n" + Marker + "\n127.0.0.1 reddit.com\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n" + Block,
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
        var written = baseText + "\r\n" + block;
        Assert.Null(monkmode.Service1.RepairHostsBlock(written, block));
        Assert.Equal(written, monkmode.Service1.RepairHostsBlock(baseText, block));
    }
}
