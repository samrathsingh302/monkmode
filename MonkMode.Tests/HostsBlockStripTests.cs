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

    [Fact]
    public void CaseVariantMarkerAboveRealMarker_DoesNotCutEarly()
    {
        // The strip must match the marker ordinally, like the stopMe() gate
        // and the CLI. The old case-insensitive InStr locked onto the
        // hand-edited variant line at the top and deleted the user's own
        // "127.0.0.1 my-dev-box" between the two.
        var hosts = "#### MONKMODE ENTRIES ####\r\n127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal("#### MONKMODE ENTRIES ####\r\n127.0.0.1 my-dev-box", monkmode.Service1.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void TwoExactMarkerLines_FirstWins_EverythingBelowItRemoved()
    {
        // Two literal marker lines in one file: the cut happens at the FIRST,
        // and everything below it (including the second marker) is MonkMode's
        // to remove.
        var hosts = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n";
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
    public void CaseVariantAndDoubledMarkers_MatchTheServiceStripSemantics()
    {
        // Parity with the service strip: ordinal matching (a case-variant
        // line above the real marker is user content) and first-marker-wins.
        var caseVariant = "#### MONKMODE ENTRIES ####\r\n127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal("#### MONKMODE ENTRIES ####\r\n127.0.0.1 my-dev-box", MonkMode.Blocker.StripOurBlock(caseVariant));

        var doubled = "127.0.0.1 my-dev-box\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n" + Marker + "\r\n";
        Assert.Equal("127.0.0.1 my-dev-box", MonkMode.Blocker.StripOurBlock(doubled));
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

public class CliServiceStripParityTests
{
    private const string Marker = "#### MonkMode Entries ####";

    // #7 (audit P3): the CLI's LIFT strip (Blocker.StripMonkModeBlock, used by
    // RestoreHostsFromStrip / `unblock --force`) must be byte-for-byte identical
    // to the service's expiry strip (Service1.StripMonkModeBlock), so lifting a
    // block via the escape hatch and via a natural expiry leave hosts in the
    // exact same state. Before the fix the lift path called StripOurBlock, which
    // trims trailing whitespace, so the two teardowns diverged on a user's
    // trailing blank line above the marker (whitespace-only, but a real
    // divergence and a false "same strip" code comment).
    [Theory]
    [InlineData("")]
    [InlineData("# my hosts\r\n127.0.0.1 box\r\n")]                                                // no marker, trailing CRLF
    [InlineData("# my hosts\r\n127.0.0.1 box")]                                                    // no marker, no trailing newline
    [InlineData("# my hosts   \t \r\n")]                                                           // no marker, trailing spaces+tab
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n")]               // block at end, CRLF
    [InlineData("127.0.0.1 box\n#### MonkMode Entries ####\n0.0.0.0 x.com\n")]                     // LF endings
    [InlineData("127.0.0.1 box\r\n\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n")]           // user blank line before marker (the divergence)
    [InlineData("127.0.0.1 box\r\n  \t\r\n#### MonkMode Entries ####\r\n")]                        // user whitespace-only line before marker
    [InlineData("127.0.0.1 box#### MonkMode Entries ####\r\n")]                                    // marker glued to content
    [InlineData("#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n")]                                // whole file is ours
    [InlineData("#### MONKMODE ENTRIES ####\r\n127.0.0.1 box\r\n#### MonkMode Entries ####\r\n")]  // case-variant above the real marker
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 a\r\n#### MonkMode Entries ####\r\n")] // doubled marker
    public void CliLiftStrip_MatchesServiceExpiryStrip_ByteForByte(string hosts)
    {
        Assert.Equal(
            monkmode.Service1.StripMonkModeBlock(hosts),
            MonkMode.Blocker.StripMonkModeBlock(hosts));
    }

    // The re-block strip (StripOurBlock, used by WriteHostsBlock) must be
    // behaviour-identical to the ORIGINAL ordinal implementation (IndexOf +
    // Substring + TrimEnd) - the refactor's "zero behaviour change" claim.
    // Asserted against an independent reimplementation (LegacyStripOurBlock), NOT
    // against StripOurBlock's own new definition, so it has real teeth.
    [Theory]
    [InlineData("")]
    [InlineData("# my hosts\r\n127.0.0.1 box\r\n")]
    [InlineData("# my hosts   \t \r\n")]
    [InlineData("127.0.0.1 box\r\n\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n")]
    [InlineData("127.0.0.1 box\r\n  \t\r\n#### MonkMode Entries ####\r\n")]
    [InlineData("#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n")]
    public void ReBlockStrip_MatchesLegacyOrdinalSemantics(string hosts)
    {
        Assert.Equal(LegacyStripOurBlock(hosts), MonkMode.Blocker.StripOurBlock(hosts));
    }

    // A CRLF followed by a Unicode-ignorable char (U+00AD soft hyphen) right
    // before the marker is the one input class where a culture-sensitive
    // EndsWith would diverge from the ordinal original: it would match "\r\n",
    // drop two chars BY INDEX (the LF + the ignorable) and leave a dangling CR.
    // Both StripMonkModeBlock copies use ordinal EndsWith, so the ignorable is
    // preserved and the strip matches the legacy ordinal impl. Built with (char)
    // code points so the source stays pure-ASCII (no invisible bytes on disk).
    [Fact]
    public void SoftHyphenBeforeMarker_OrdinalStrip_KeepsItAndMatchesLegacy()
    {
        string soft = ((char)0x00AD).ToString();
        string hosts = "127.0.0.1 box\r\n" + soft + Marker + "\r\n";
        Assert.Equal(LegacyStripOurBlock(hosts), MonkMode.Blocker.StripOurBlock(hosts));
        Assert.Equal("127.0.0.1 box\r\n" + soft, MonkMode.Blocker.StripMonkModeBlock(hosts)); // ignorable kept, no dangling CR
        Assert.Equal(                                                                          // CLI<->service parity holds here too
            monkmode.Service1.StripMonkModeBlock(hosts),
            MonkMode.Blocker.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void ZeroWidthSpaceBeforeMarker_OrdinalStrip_KeepsItAndMatchesLegacy()
    {
        string zwsp = ((char)0x200B).ToString();
        string hosts = "127.0.0.1 box\r\n" + zwsp + Marker + "\r\n";
        Assert.Equal(LegacyStripOurBlock(hosts), MonkMode.Blocker.StripOurBlock(hosts));
        Assert.Equal("127.0.0.1 box\r\n" + zwsp, MonkMode.Blocker.StripMonkModeBlock(hosts));
        Assert.Equal(
            monkmode.Service1.StripMonkModeBlock(hosts),
            MonkMode.Blocker.StripMonkModeBlock(hosts));
    }

    // The original pre-refactor StripOurBlock, verbatim, as an independent oracle
    // for the tests above (ordinal IndexOf + Substring + TrimEnd).
    private static string LegacyStripOurBlock(string text)
    {
        int idx = text.IndexOf(Marker, System.StringComparison.Ordinal);
        if (idx >= 0) text = text.Substring(0, idx);
        return text.TrimEnd('\r', '\n', ' ', '\t');
    }

    // The concrete case #7 closes: a user's blank line above the marker now
    // survives an `unblock` lift byte-for-byte, exactly as it already survived a
    // natural service expiry (the CLI lift used to trim it away).
    [Fact]
    public void UserBlankLineBeforeMarker_SurvivesCliLift_LikeServiceExpiry()
    {
        const string hosts = "127.0.0.1 box\r\n\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n";
        Assert.Equal("127.0.0.1 box\r\n", MonkMode.Blocker.StripMonkModeBlock(hosts));
        Assert.Equal(
            monkmode.Service1.StripMonkModeBlock(hosts),
            MonkMode.Blocker.StripMonkModeBlock(hosts));
    }
}
