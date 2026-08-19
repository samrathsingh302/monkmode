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
    public void MarkerGluedToUserLine_IsUserContent_NothingIsRemoved()
    {
        // F31 (FX2): a marker that does NOT start its own line is not ours -
        // MonkMode only ever writes the marker at column 0 - so the text is
        // user content and survives verbatim. This test previously pinned the
        // cut-at-the-text behaviour ("127.0.0.1 my-dev-box"), which is exactly
        // the data-loss defect F31 closes: everything below the glued marker
        // was discarded too.
        var hosts = "127.0.0.1 my-dev-box" + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal(hosts, monkmode.Service1.StripMonkModeBlock(hosts));
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
    // F35 (FX7): the end-marker shapes. Both copies must answer identically on every one of
    // them - the strip is the lift path, so a divergence means `unblock` and a natural expiry
    // leave the user's hosts in different states.
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n#### MonkMode End ####\r\n")]                       // end marker, block last
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n#### MonkMode End ####\r\n10.0.0.5 nas\r\n")]      // THE F35 shape
    [InlineData("#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n#### MonkMode End ####\r\n10.0.0.5 nas\r\n")]                        // block at the top
    [InlineData("127.0.0.1 box\n#### MonkMode Entries ####\n0.0.0.0 x.com\n#### MonkMode End ####\n10.0.0.5 nas\n")]                 // LF endings
    [InlineData("127.0.0.1 box\r#### MonkMode Entries ####\r0.0.0.0 x.com\r#### MonkMode End ####\r10.0.0.5 nas\r")]                 // bare CR
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n#### MonkMode End ####\r\n\r\n10.0.0.5 nas\r\n")]  // user blank line below
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x.com\r\n  #### MonkMode End ####\r\n10.0.0.5 nas\r\n")]    // indented end marker: not ours
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n# the #### MonkMode End #### line\r\n0.0.0.0 x\r\n")]                // mid-line mention only
    [InlineData("#### MonkMode End ####\r\n127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x\r\n#### MonkMode End ####\r\n")] // stray end marker above
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n#### MonkMode End ####\r\n10.0.0.5 nas\r\n")]                        // empty block
    [InlineData("127.0.0.1 box\r\n#### MonkMode Entries ####\r\n0.0.0.0 x\r\n#### MonkMode End ####")]                               // end marker, no trailing EOL
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

    // A Unicode-ignorable char (U+00AD soft hyphen, U+200B ZWSP) sitting
    // between the line terminator and the marker. Pre-F31 this was the one
    // input class where a culture-sensitive EndsWith would have diverged from
    // the ordinal original (matching "\r\n" through the ignorable, dropping two
    // chars BY INDEX and leaving a dangling CR); both copies use ordinal
    // EndsWith, so no dangling CR was ever produced.
    // Since F31 the input never reaches that code at all: the marker is not at
    // column 0, so it is not our block, and the ENTIRE text is preserved -
    // strictly safer still. This is a deliberate divergence from
    // LegacyStripOurBlock (the pre-F31 oracle), which is precisely the defect
    // FX2 closes, so the legacy comparison is asserted NOT to hold here.
    // Built with (char) code points so the source stays pure-ASCII.
    [Fact]
    public void SoftHyphenBeforeMarker_MarkerIsNotLineAnchored_TextPreservedWhole()
    {
        string soft = ((char)0x00AD).ToString();
        string hosts = "127.0.0.1 box\r\n" + soft + Marker + "\r\n";
        Assert.Equal(hosts, MonkMode.Blocker.StripMonkModeBlock(hosts));
        Assert.NotEqual(LegacyStripOurBlock(hosts), MonkMode.Blocker.StripOurBlock(hosts)); // F31 divergence, by design
        Assert.Equal(                                                                        // CLI<->service parity holds here too
            monkmode.Service1.StripMonkModeBlock(hosts),
            MonkMode.Blocker.StripMonkModeBlock(hosts));
    }

    [Fact]
    public void ZeroWidthSpaceBeforeMarker_MarkerIsNotLineAnchored_TextPreservedWhole()
    {
        string zwsp = ((char)0x200B).ToString();
        string hosts = "127.0.0.1 box\r\n" + zwsp + Marker + "\r\n";
        Assert.Equal(hosts, MonkMode.Blocker.StripMonkModeBlock(hosts));
        Assert.NotEqual(LegacyStripOurBlock(hosts), MonkMode.Blocker.StripOurBlock(hosts));
        Assert.Equal(
            monkmode.Service1.StripMonkModeBlock(hosts),
            MonkMode.Blocker.StripMonkModeBlock(hosts));
    }

    // The ordinal-EndsWith guard itself, still reachable via a line-anchored
    // marker: an ignorable char at the END of the user's own last line, with a
    // proper CRLF before the marker. The single terminator goes; the ignorable
    // stays, byte-exact.
    [Fact]
    public void IgnorableAtEndOfUserLine_AnchoredMarker_TerminatorOnlyIsDropped()
    {
        string soft = ((char)0x00AD).ToString();
        string hosts = "127.0.0.1 box" + soft + "\r\n" + Marker + "\r\n0.0.0.0 x.com\r\n";
        Assert.Equal("127.0.0.1 box" + soft, MonkMode.Blocker.StripMonkModeBlock(hosts));
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

// F31 (v1.1 FX2, P1 - irreversible user data loss).
//
// Both strips used to locate the marker with a bare ordinal IndexOf and cut the
// file at that CHARACTER INDEX. A user's own hosts line that merely MENTIONED
// the marker was therefore truncated mid-line and every user line below it was
// discarded - on the FIRST tick of the FIRST block, since the per-tick B2
// self-heal routes through the same strip. MonkMode keeps no hosts backup.
//
// The fix: the marker only counts when it OWNS ITS WHOLE LINE - column 0 (start
// of text, or right after CR/LF) and immediately followed by CR/LF or EOF. That
// is exactly the shape MonkMode itself writes (Marker & vbCrLf & entries), so
// behaviour for a correctly-placed block is unchanged; anything else is user
// content and survives verbatim.
//
// Decision recorded: an indented / glued / annotated marker is USER CONTENT, not
// ours. Consequence if MonkMode's own block were ever mangled into that shape:
// its entries linger in hosts (over-block - acceptable). The opposite bias would
// delete the user's data (never acceptable).
public class MarkerLineAnchoringTests
{
    private const string Marker = "#### MonkMode Entries ####";

    private static string ServiceStrip(string t) => monkmode.Service1.StripMonkModeBlock(t);
    private static string CliStrip(string t) => MonkMode.Blocker.StripMonkModeBlock(t);

    // The exact repro from the bug-hunt report.
    private const string MentionRepro =
        "# my hosts\r\n" +
        "# the #### MonkMode Entries #### block is MonkMode's\r\n" +
        "10.0.0.5 nas.home\r\n" +
        "192.168.1.9 printer.home\r\n";

    [Fact]
    public void Repro_UserLineMentioningMarker_NothingIsRemoved_Service()
    {
        // Pre-fix this returned "# my hosts\r\n# the " - nas.home and
        // printer.home gone for good, a dangling fragment committed to hosts.
        Assert.Equal(MentionRepro, ServiceStrip(MentionRepro));
    }

    [Fact]
    public void Repro_UserLineMentioningMarker_NothingIsRemoved_Cli()
    {
        Assert.Equal(MentionRepro, CliStrip(MentionRepro));
        // The re-block strip only normalises the tail, so the user's lines
        // survive an arm too.
        Assert.Equal(MentionRepro.TrimEnd('\r', '\n'), MonkMode.Blocker.StripOurBlock(MentionRepro));
    }

    [Fact]
    public void Repro_ArmThenLift_RoundTrip_UserLinesSurvive()
    {
        // What WriteHostsFile assembles on arm: StripOurBlock(existing) + CRLF
        // + our block. Lifting it (service expiry or CLI unblock) must give the
        // user's file back, mention line and all.
        var baseText = MonkMode.Blocker.StripOurBlock(MentionRepro);
        var written = baseText + "\r\n" + Marker + "\r\n" +
                      MonkMode.Blocker.BuildHostsEntries(new[] { "reddit.com" });
        Assert.Equal(baseText, ServiceStrip(written));
        Assert.Equal(baseText, CliStrip(written));
        Assert.Contains("10.0.0.5 nas.home", ServiceStrip(written));
        Assert.Contains("192.168.1.9 printer.home", ServiceStrip(written));
    }

    [Fact]
    public void MidLineMentionAboveRealBlock_RealBlockIsStillStripped()
    {
        // The mention must not shield a genuine block below it from removal -
        // that would be a lift failure, not a data-loss failure.
        var hosts = "# see the #### MonkMode Entries #### marker below\r\n" +
                    "10.0.0.5 nas.home\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n";
        var expected = "# see the #### MonkMode Entries #### marker below\r\n10.0.0.5 nas.home";
        Assert.Equal(expected, ServiceStrip(hosts));
        Assert.Equal(expected, CliStrip(hosts));
    }

    [Fact]
    public void MarkerAtPositionZeroButNotAloneOnItsLine_IsUserContent()
    {
        // Column 0 is not enough: trailing text on the line means it is not our
        // marker line. Pre-fix this deleted every line below.
        var hosts = Marker + " is where MonkMode writes\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal(hosts, ServiceStrip(hosts));
        Assert.Equal(hosts, CliStrip(hosts));
    }

    [Fact]
    public void IndentedMarker_IsUserContent_DocumentedChoice()
    {
        // MonkMode never indents the marker, so an indented one is not ours.
        var hosts = "10.0.0.5 nas.home\r\n   " + Marker + "\r\n127.0.0.1 x.com\r\n";
        Assert.Equal(hosts, ServiceStrip(hosts));
        Assert.Equal(hosts, CliStrip(hosts));
    }

    // ---- correct placement: unchanged, byte-for-byte ----

    [Theory]
    [InlineData("# mine\r\n127.0.0.1 box\r\n" + Marker + "\r\n0.0.0.0 x.com\r\n", "# mine\r\n127.0.0.1 box")] // CRLF
    [InlineData("# mine\n127.0.0.1 box\n" + Marker + "\n0.0.0.0 x.com\n", "# mine\n127.0.0.1 box")]           // LF
    [InlineData("# mine\r127.0.0.1 box\r" + Marker + "\r0.0.0.0 x.com\r", "# mine\r127.0.0.1 box")]           // bare CR
    [InlineData(Marker + "\r\n0.0.0.0 x.com\r\n", "")]                                                        // marker at position 0
    [InlineData("127.0.0.1 box\r\n" + Marker, "127.0.0.1 box")]                                               // marker is the last line, no EOL
    [InlineData("127.0.0.1 box\r\n" + Marker + "\r\n", "127.0.0.1 box")]                                      // marker last line, empty block
    [InlineData("127.0.0.1 box\r\n" + Marker + "\r\n0.0.0.0 a\r\n" + Marker + "\r\n", "127.0.0.1 box")]        // first anchored marker wins
    [InlineData("# mine\r\n127.0.0.1 box\r\n", "# mine\r\n127.0.0.1 box\r\n")]                                // no marker at all
    [InlineData("", "")]
    public void CorrectlyPlacedBlock_StripsExactlyAsBefore(string hosts, string expected)
    {
        Assert.Equal(expected, ServiceStrip(hosts));
        Assert.Equal(expected, CliStrip(hosts));
    }

    // ---- the anchoring primitive itself, both copies ----

    [Theory]
    [InlineData("", -1)]
    [InlineData("#### MonkMode", -1)]                                     // shorter than the marker
    [InlineData(Marker, 0)]                                               // whole file is the marker line
    [InlineData("\n" + Marker + "\n", 1)]
    [InlineData("\r\n" + Marker + "\r\n", 2)]
    [InlineData("\r" + Marker + "\r", 1)]
    [InlineData("abc" + Marker + "\r\n", -1)]                             // glued to the left
    [InlineData("\r\n" + Marker + " trailing\r\n", -1)]                   // trailing text on the line
    [InlineData("  " + Marker + "\r\n", -1)]                              // indented
    [InlineData("x " + Marker + " y\r\n" + Marker + "\r\n", 32)]          // mention first (2+26+2+2), real marker after
    public void MarkerLineStart_AgreesOnIndex_AndOnBothSides(string text, int expected)
    {
        Assert.Equal(expected, monkmode.Service1.MarkerLineStart(text));
        Assert.Equal(expected, MonkMode.Blocker.MarkerLineStart(text));
    }

    [Fact]
    public void MarkerLineStart_NullText_IsMinusOne_BothCopies()
    {
        Assert.Equal(-1, monkmode.Service1.MarkerLineStart(null!));
        Assert.Equal(-1, MonkMode.Blocker.MarkerLineStart(null!));
    }

    // ---- the self-heal path: over-block, never data loss ----

    [Fact]
    public void RepairHostsBlock_WithMentionOnly_KeepsUserContent_AndAppendsOurBlock()
    {
        // B2 self-heal on a hosts file that only MENTIONS the marker: the
        // user's lines are kept whole and our block is appended below them.
        var expectedBlock = Marker + "\r\n127.0.0.1 reddit.com\r\n";
        var repaired = monkmode.Service1.RepairHostsBlock(MentionRepro, expectedBlock);
        Assert.NotNull(repaired);
        Assert.StartsWith(MentionRepro, repaired);
        // F35: hosts gets the block plus its end-marker line.
        Assert.EndsWith(expectedBlock + "#### MonkMode End ####\r\n", repaired);
        Assert.Contains("10.0.0.5 nas.home", repaired);
        Assert.Contains("192.168.1.9 printer.home", repaired);
    }
}
