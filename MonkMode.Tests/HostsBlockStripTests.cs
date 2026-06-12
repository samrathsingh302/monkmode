// MonkMode.Tests - hosts marker-block removal.
//
// The single most important data-loss invariant in MonkMode: lifting a block
// must remove ONLY the "#### MonkMode Entries ####" marker block (the marker
// line and everything below it, which MonkMode owns), and must never eat a
// byte of the user's own hosts content above it.
//
// Two independent implementations exist and both are covered here:
//   - the service's strip (Service1.StripMonkModeBlock, runs at block expiry);
//   - the CLI's strip (Blocker.StripOurBlock, runs when a new block is written).
//
// Everything here is in-memory strings - the real hosts file is never touched.

namespace MonkMode.Tests;

public class ServiceStripMonkModeBlockTests
{
    private const string Marker = "#### MonkMode Entries ####";

    [Fact]
    public void CrLf_BlockAtEndOfFile_RemovesOnlyOurBlock()
    {
        // The layout the CLI actually writes: user content, one CRLF, marker,
        // CRLF, entries (each CRLF-terminated).
        var hosts = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n";
        Assert.Equal("# my hosts\r\n127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void CrLf_UserLineImmediatelyBeforeMarker_IsPreservedWhole()
    {
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal("127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void Lf_UserLineImmediatelyBeforeMarker_IsPreservedWhole()
    {
        // P2 regression (audit: Service1.vb "startpos - 3" assumed CRLF): with
        // LF line endings the old code returned "127.0.0.1 my-dev-bo",
        // silently eating the last character of the user's own line.
        var hosts = "127.0.0.1 my-dev-box\n" + Marker + "\n127.0.0.1 x.com\n";
        Assert.Equal("127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void BlockAtEndOfFile_WithoutTrailingNewline_RemovesOnlyOurBlock()
    {
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker;
        Assert.Equal("127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void MarkerWithNoNewlineBefore_DoesNotEatUserCharacters()
    {
        // Pathological but possible after a hand edit: marker glued straight
        // onto user content. The old code returned "127.0.0.1 my-dev-b".
        var hosts = "127.0.0.1 my-dev-box" + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal("127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void UserBlankLineBeforeMarker_CrLf_IsKept()
    {
        // Only the single line terminator the CLI wrote before the marker is
        // removed; a blank line the user had above it survives.
        var hosts = "127.0.0.1 my-dev-box\r\n\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal("127.0.0.1 my-dev-box\r\n", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void MarkerAtStartOfFile_WholeFileIsOurs_ReturnsEmpty()
    {
        var hosts = Marker + "\r\n127.0.0.1 reddit.com\r\n";
        Assert.Equal("", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void MarkerAfterSingleLeadingNewline_ReturnsEmpty()
    {
        Assert.Equal("", monkmode.Service1.StripMonkModeBlock("\n" + Marker + "\n127.0.0.1 x.com\n"));
        Assert.Equal("", monkmode.Service1.StripMonkModeBlock("\r\n" + Marker + "\r\n"));
    }

    [Fact]
    public void NoMarker_ReturnsInputUnchanged()
    {
        // Without our marker the file is 100% user content - byte-for-byte
        // identical, trailing newline included.
        var hosts = "# my hosts\r\n127.0.0.1 my-dev-box\r\n";
        Assert.Equal(hosts, monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void EmptyFile_ReturnsEmpty()
    {
        Assert.Equal("", monkmode.Service1.StripMonkModeBlock(""));
    }

    [Fact]
    public void ContentBelowMarker_IsOwnedByMonkModeAndRemoved()
    {
        // By design everything below the marker belongs to MonkMode (the `add`
        // verb appends there), so lines added below it do NOT survive a lift.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 added-later\r\n";
        Assert.Equal("127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }
}

public class CliStripOurBlockTests
{
    private static readonly string Marker = MonkMode.Blocker.Marker;

    [Fact]
    public void CrLf_BlockAtEndOfFile_RemovesBlockAndTrailingWhitespace()
    {
        var hosts = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n";
        Assert.Equal("# my hosts\r\n127.0.0.1 my-dev-box", MonkMode.Blocker.StripOurBlock(hosts));
    }

    [Fact]
    public void Lf_UserLineImmediatelyBeforeMarker_IsPreservedWhole()
    {
        var hosts = "127.0.0.1 my-dev-box\n" + Marker + "\n127.0.0.1 x.com\n";
        Assert.Equal("127.0.0.1 my-dev-box", MonkMode.Blocker.StripOurBlock(hosts));
    }

    [Fact]
    public void NoMarker_TrimsTrailingWhitespaceOnly()
    {
        // Documented semantics: the CLI strip normalises the tail (the caller
        // re-appends CRLF + marker), so only trailing CR/LF/space/tab go.
        Assert.Equal("# mine\r\n127.0.0.1 my-dev-box",
            MonkMode.Blocker.StripOurBlock("# mine\r\n127.0.0.1 my-dev-box\r\n"));
    }

    [Fact]
    public void MarkerAtStartOfFile_ReturnsEmpty()
    {
        Assert.Equal("", MonkMode.Blocker.StripOurBlock(Marker + "\r\n127.0.0.1 reddit.com\r\n"));
    }

    [Fact]
    public void WriteHostsBlockLayout_RoundTrips_UserContentSurvivesRewrite()
    {
        // Exactly what WriteHostsBlock assembles (base + CRLF + marker + CRLF
        // + entries): stripping it must give back the user's base text, so a
        // block -> re-block -> lift cycle never erodes user content.
        var baseText = "# my hosts\r\n127.0.0.1 my-dev-box";
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "reddit.com", "x.com" });
        var written = baseText + "\r\n" + Marker + "\r\n" + entries;
        Assert.Equal(baseText, MonkMode.Blocker.StripOurBlock(written));
        Assert.Equal(baseText, monkmode.Service1.StripMonkModeBlock(written));
    }
}
