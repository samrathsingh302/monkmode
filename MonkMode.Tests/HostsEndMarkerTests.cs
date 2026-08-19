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

// MonkMode.Tests - F35 (v1.1 FX7): the hosts block's END marker.
//
// THE BUG. Every hosts writer rebuilt the file as "user content + CRLF + block"
// from a strip that ran "marker line -> EOF", so MonkMode's block had to be the
// last thing in hosts. A line the user added BELOW it - "10.0.0.5 nas.home" -
// was destroyed at the next arm, self-heal repair, slot retire or expiry, with
// no hosts backup to recover from. S3b's ExactHostsRewrite at EVERY retire made
// the loss recur on a multi-slot machine.
//
// THE FIX. Every hosts write closes the block with "#### MonkMode End ####" on
// its own line; the strip removes start marker -> end marker INCLUSIVE and hands
// back the user's content on BOTH sides, byte-for-byte; the rewriters re-seat the
// block between the two halves, keeping the user's lines BELOW it.
//
// THE RULES pinned here:
//   - a marker (either one) counts only when it OWNS ITS WHOLE LINE, so a mid-line
//     mention in the user's own text is user content (F31's rule, extended);
//   - an end marker ABOVE the start marker is never a close of ours;
//   - LEGACY: a block written before FX7 carries no end marker and still runs to
//     EOF - the only rule that can be right, since nothing distinguishes a line
//     the user appended afterwards from one of ours. The window is self-closing:
//     the first FX7-era write end-markers the block for good;
//   - the block string on disk (monkmode_hosts.block) is UNCHANGED - marker plus
//     entry lines. The end marker is added at the hosts boundary only.
//
// Everything here is in-memory strings or files inside the test bin directory -
// never the real hosts file, the registry or the service.

using System.IO;

namespace MonkMode.Tests;

public class HostsEndMarkerPrimitiveTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string End = "#### MonkMode End ####";

    private static string ServiceStrip(string t) => monkmode.Service1.StripMonkModeBlock(t);
    private static string CliStrip(string t) => MonkMode.Blocker.StripMonkModeBlock(t);

    [Fact]
    public void BothCopiesUseTheSameEndMarkerLiteral()
    {
        Assert.Equal(End, monkmode.Service1.HostsEndMarker);
        Assert.Equal(End, MonkMode.Blocker.EndMarker);
    }

    // ---- the anchoring primitive, both copies ----

    [Theory]
    [InlineData("", 0, -1)]
    [InlineData(End, 0, 0)]                                            // whole file is the end-marker line
    [InlineData("\r\n" + End + "\r\n", 0, 2)]
    [InlineData("\n" + End + "\n", 0, 1)]
    [InlineData("\r" + End + "\r", 0, 1)]
    [InlineData("abc" + End + "\r\n", 0, -1)]                          // glued to the left
    [InlineData("\r\n" + End + " trailing\r\n", 0, -1)]                // trailing text on the line
    [InlineData("   " + End + "\r\n", 0, -1)]                          // indented
    [InlineData("# see " + End + " below\r\n" + End + "\r\n", 0, 36)]  // mention first (6+22+6+2), real one after
    [InlineData(End + "\r\n" + End + "\r\n", 24, 24)]                  // searchFrom skips the first
    public void EndMarkerLineStart_AgreesOnIndex_AndOnBothSides(string text, int from, int expected)
    {
        Assert.Equal(expected, monkmode.Service1.EndMarkerLineStart(text, from));
        Assert.Equal(expected, MonkMode.Blocker.EndMarkerLineStart(text, from));
    }

    [Fact]
    public void EndMarkerLineStart_NullText_IsMinusOne_BothCopies()
    {
        Assert.Equal(-1, monkmode.Service1.EndMarkerLineStart(null!, 0));
        Assert.Equal(-1, MonkMode.Blocker.EndMarkerLineStart(null!, 0));
    }

    // ---- the strip: user content below our block survives ----

    [Fact]
    public void Repro_UserLineBelowTheBlock_SurvivesTheStrip_BothCopies()
    {
        // THE F35 REPRO. Pre-fix both copies returned "# my hosts" and nas.home was gone.
        const string hosts = "# my hosts\r\n" + Marker + "\r\n127.0.0.1 reddit.com\r\n" + End +
                             "\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# my hosts\r\n10.0.0.5 nas.home\r\n", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void BlockAtTopOfFile_EverythingBelowTheEndMarkerIsTheUsers()
    {
        const string hosts = Marker + "\r\n127.0.0.1 reddit.com\r\n" + End + "\r\n# mine\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# mine\r\n10.0.0.5 nas.home\r\n", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void UserBlankLineBelowTheEndMarker_IsPreserved()
    {
        // Only the ONE terminator closing our end-marker line is ours; a blank line the
        // user left under it is theirs.
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n" + End + "\r\n\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# mine\r\n\r\n10.0.0.5 nas.home\r\n", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void EveryLineEnding_JoinsTheTwoHalvesWithExactlyOneTerminator(string eol)
    {
        var hosts = "# mine" + eol + Marker + eol + "127.0.0.1 x.com" + eol + End + eol + "10.0.0.5 nas.home" + eol;
        Assert.Equal("# mine" + eol + "10.0.0.5 nas.home" + eol, ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void EndMarkerIsTheLastLine_StripIsByteIdenticalToThePreF35Result()
    {
        // Nothing below the end marker: the old whole-file semantics, unchanged.
        const string hosts = "# mine\r\n127.0.0.1 box\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n" + End + "\r\n";
        Assert.Equal("# mine\r\n127.0.0.1 box", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void EndMarkerIsTheLastLine_WithNoTrailingNewline()
    {
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n" + End;
        Assert.Equal("# mine", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    // ---- ownership: only a whole-line end marker is ours ----

    [Fact]
    public void MidLineEndMarkerMention_IsUserContent_NotAMarker()
    {
        // A user line that MENTIONS the end marker does not close our block. The block
        // therefore has no end marker at all, so the legacy rule applies and the mention
        // (which is below our marker line) goes with it - the same bias F31 chose:
        // over-removing our own region is acceptable, mistaking user text for ours is not.
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n# the " + End + " line closes it\r\n";
        Assert.Equal("# mine", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
        Assert.Equal(-1, monkmode.Service1.EndMarkerLineStart(hosts, 0));
    }

    [Fact]
    public void MidLineEndMarkerMention_AboveARealOne_DoesNotCloseEarly()
    {
        // The mention must not shield the real close below it, or a user line under our
        // genuine end marker would be swallowed again.
        const string hosts = "# mine\r\n" + Marker + "\r\n# see " + End + " below\r\n127.0.0.1 x.com\r\n" +
                             End + "\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# mine\r\n10.0.0.5 nas.home\r\n", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void IndentedEndMarker_IsUserContent()
    {
        // MonkMode never indents it, so an indented one is not ours.
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n   " + End + "\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# mine", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void EndMarkerAboveOurBlock_IsUserContent_NeverACloseOfOurs()
    {
        // A stray end-marker line ABOVE the start marker is the user's; the search starts
        // at our marker, so it can never be mistaken for our close.
        const string hosts = End + "\r\n# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n" + End + "\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal(End + "\r\n# mine\r\n10.0.0.5 nas.home\r\n", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    [Fact]
    public void FirstEndMarkerWins_ARepeatedOneBelowIsUserContent()
    {
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n" + End + "\r\n" + End + "\r\n";
        Assert.Equal("# mine\r\n" + End + "\r\n", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    // ---- the documented LEGACY rule ----

    [Fact]
    public void LegacyBlockWithNoEndMarker_StillRunsToEof_TheDocumentedRule()
    {
        // The residual, stated so a change to it is visible: with no end marker there is
        // nothing in the file that separates our lines from a line the user appended
        // afterwards (theirs can look exactly like ours), so the strip keeps its pre-F35
        // behaviour and the appended line goes with our block.
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# mine", ServiceStrip(hosts));
        Assert.Equal(ServiceStrip(hosts), CliStrip(hosts));
    }

    // ---- EnsureBlockEndMarker ----

    [Fact]
    public void EnsureBlockEndMarker_AppendsOnce_AndIsIdempotent_BothCopies()
    {
        const string block = Marker + "\r\n127.0.0.1 x.com\r\n";
        var once = monkmode.Service1.EnsureBlockEndMarker(block);
        Assert.Equal(block + End + "\r\n", once);
        Assert.Equal(once, monkmode.Service1.EnsureBlockEndMarker(once));       // idempotent: no stacking
        Assert.Equal(once, MonkMode.Blocker.EnsureBlockEndMarker(block));       // both copies agree
        Assert.Equal(once, MonkMode.Blocker.EnsureBlockEndMarker(once));
    }

    // FX7 verifier P3 #1: the idempotence check must look for a close of THIS block, i.e.
    // from the block's own start marker down. Searching from index 0 let a snapshot tampered
    // to carry an anchored End ABOVE the marker read as "already end-markered": the writer
    // then put a block with NO End below its marker into hosts, and the next strip fell to
    // the LEGACY rule and took the re-seated user tail with it - the F35 loss, reopened by a
    // file anyone can edit.
    [Fact]
    public void EnsureBlockEndMarker_EndMarkerAboveTheStartMarker_IsNotACloseOfThisBlock()
    {
        const string tampered = End + "\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n";
        var fixedUp = monkmode.Service1.EnsureBlockEndMarker(tampered);
        Assert.Equal(tampered + End + "\r\n", fixedUp);                       // a REAL close was appended
        Assert.Equal(fixedUp, MonkMode.Blocker.EnsureBlockEndMarker(tampered));  // both copies agree
        Assert.Equal(fixedUp, monkmode.Service1.EnsureBlockEndMarker(fixedUp));  // still idempotent

        // ...and the whole point: through the writer, the user's tail survives the expiry strip.
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 old.com\r\n" + End +
                             "\r\n10.0.0.5 nas.home\r\n";
        var rewritten = monkmode.Service1.ExactHostsRewrite(hosts, tampered);
        Assert.NotNull(rewritten);
        Assert.EndsWith(End + "\r\n10.0.0.5 nas.home\r\n", rewritten);
        Assert.Contains("10.0.0.5 nas.home", monkmode.Service1.StripMonkModeBlock(rewritten));
        // The tamperer's stray End line rides in above our marker and is left in the user's
        // file as inert junk (over-block/cosmetic, hand-removable); their OWN line is what
        // had to survive, and it does. Pre-fix the strip returned "# mine" and it was gone.
        Assert.Equal("# mine\r\n" + End + "\r\n10.0.0.5 nas.home\r\n",
                     monkmode.Service1.StripMonkModeBlock(rewritten));
        Assert.Equal(monkmode.Service1.StripMonkModeBlock(rewritten), MonkMode.Blocker.StripMonkModeBlock(rewritten));
    }

    // FX7 verifier P3 #2: the documented HONEST LIMIT, pinned WITH a tail present so a future
    // change to this surface trips a test instead of silently changing what gets destroyed.
    // Deleting our End line while the user has content below it makes the block indistinguish-
    // able from a legacy one, so the legacy rule applies and their content goes with the block.
    // It is accepted rather than fixed: nothing in the file separates their lines from ours,
    // and the next self-heal (which repairs the missing End) closes the window within one tick.
    [Fact]
    public void EndMarkerDeletedWithUserContentBelow_TailIsLost_TheAcceptedLimit()
    {
        const string tampered = "# mine\r\n" + Marker + "\r\n127.0.0.1 x.com\r\n10.0.0.5 nas.home\r\n";
        Assert.Equal("# mine", ServiceStrip(tampered));                 // nas.home goes with the block
        Assert.Equal(ServiceStrip(tampered), CliStrip(tampered));
        // The mitigation, same tick: the self-heal sees a block that is not the expected
        // end-markered text and rewrites it - after which a tail added below IS safe.
        const string snapshot = Marker + "\r\n127.0.0.1 x.com\r\n";
        var healed = monkmode.Service1.RepairHostsBlock(tampered, snapshot);
        Assert.Equal("# mine\r\n" + snapshot + End + "\r\n", healed);
        Assert.Equal("# mine\r\n10.0.0.5 nas.home\r\n",
                     monkmode.Service1.StripMonkModeBlock(healed + "10.0.0.5 nas.home\r\n"));
    }

    [Fact]
    public void EnsureBlockEndMarker_AddsTheMissingTerminatorFirst()
    {
        const string block = Marker + "\r\n127.0.0.1 x.com";   // hand-tampered: no trailing EOL
        Assert.Equal(block + "\r\n" + End + "\r\n", monkmode.Service1.EnsureBlockEndMarker(block));
        Assert.Equal(monkmode.Service1.EnsureBlockEndMarker(block), MonkMode.Blocker.EnsureBlockEndMarker(block));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EnsureBlockEndMarker_EmptyStaysEmpty_NeverInventsContent(string? block)
    {
        Assert.Equal(block, monkmode.Service1.EnsureBlockEndMarker(block!));
        Assert.Equal(block, MonkMode.Blocker.EnsureBlockEndMarker(block!));
    }
}

// The writers: what actually lands in hosts, and what a lift hands back.
public class HostsEndMarkerWriterTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string End = "#### MonkMode End ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n";

    [Fact]
    public void ExactHostsRewrite_KeepsBothHalvesOfTheUsersFile()
    {
        const string hosts = "# mine\r\n" + Marker + "\r\n127.0.0.1 a.com\r\n127.0.0.1 b.com\r\n" +
                             End + "\r\n10.0.0.5 nas.home\r\n";
        var desired = monkmode.Service1.ExactHostsRewrite(hosts, Block);
        Assert.Equal("# mine\r\n" + Block + End + "\r\n10.0.0.5 nas.home\r\n", desired);
        // Already exactly that => no churn.
        Assert.Null(monkmode.Service1.ExactHostsRewrite(desired, Block));
    }

    [Fact]
    public void ExactHostsRewrite_ConvergesALegacyBlockInOneRewrite_ThenNoChurn()
    {
        const string legacy = "# mine\r\n" + Block;                     // pre-FX7 hosts
        var converged = monkmode.Service1.ExactHostsRewrite(legacy, Block);
        Assert.Equal("# mine\r\n" + Block + End + "\r\n", converged);
        Assert.Null(monkmode.Service1.ExactHostsRewrite(converged, Block));
        // ...and from then on a user line below it is safe.
        var withUserLine = converged + "10.0.0.5 nas.home\r\n";
        Assert.Null(monkmode.Service1.ExactHostsRewrite(withUserLine, Block));
        Assert.Equal("# mine\r\n10.0.0.5 nas.home\r\n", monkmode.Service1.StripMonkModeBlock(withUserLine));
    }

    [Fact]
    public void ArmWriteThenLift_RoundTrips_BothHalvesOfTheUsersFile()
    {
        // The CLI's real arm writer against a test-owned hosts file (never the real one),
        // then the lift strip: the user's file must come back with both halves intact.
        var dir = Path.Combine(AppContext.BaseDirectory, "fx7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var hosts = Path.Combine(dir, "hosts");
        var snapshot = Path.Combine(dir, "monkmode_hosts.block");
        try
        {
            const string userFile = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Marker +
                                    "\r\n127.0.0.1 old.com\r\n" + End + "\r\n10.0.0.5 nas.home\r\n";
            File.WriteAllText(hosts, userFile);

            MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                  new[] { "reddit.com" }, freshArm: true);

            var written = File.ReadAllText(hosts);
            Assert.Contains("127.0.0.1 reddit.com", written);
            Assert.EndsWith(End + "\r\n10.0.0.5 nas.home\r\n", written);
            // The snapshot keeps the plain marker + entries form (unchanged on-disk contract).
            Assert.DoesNotContain(End, File.ReadAllText(snapshot));
            // A lift hands the user back exactly their own two halves.
            Assert.Equal("# my hosts\r\n127.0.0.1 my-dev-box\r\n10.0.0.5 nas.home\r\n",
                         MonkMode.Blocker.StripMonkModeBlock(written));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    // ---- the `add` verb: more of OUR block, so it must go INSIDE it ----

    [Fact]
    public void InsertIntoHostsBlock_PutsTheEntriesAboveTheEndMarker()
    {
        const string hosts = "# mine\r\n" + Block + End + "\r\n10.0.0.5 nas.home\r\n";
        var after = monkmode.Service1.InsertIntoHostsBlock(hosts, "127.0.0.1 x.com\r\n");
        Assert.Equal("# mine\r\n" + Block + "127.0.0.1 x.com\r\n" + End + "\r\n10.0.0.5 nas.home\r\n", after);
        // ...so a lift still removes them and the user's line still survives.
        Assert.Equal("# mine\r\n10.0.0.5 nas.home\r\n", monkmode.Service1.StripMonkModeBlock(after));
    }

    [Fact]
    public void InsertIntoHostsBlock_NoEndMarker_IsThePlainAppend()
    {
        // Legacy block (runs to EOF) and no block at all: appending IS appending to it.
        Assert.Equal("# mine\r\n" + Block + "127.0.0.1 x.com\r\n",
                     monkmode.Service1.InsertIntoHostsBlock("# mine\r\n" + Block, "127.0.0.1 x.com\r\n"));
        Assert.Equal("# mine\r\n127.0.0.1 x.com\r\n",
                     monkmode.Service1.InsertIntoHostsBlock("# mine\r\n", "127.0.0.1 x.com\r\n"));
    }

    [Fact]
    public void InsertIntoHostsBlock_AddsTheMissingTerminator_SoLinesNeverFuse()
    {
        const string hosts = "# mine\r\n" + Block + End + "\r\n";
        var after = monkmode.Service1.InsertIntoHostsBlock(hosts, "127.0.0.1 x.com");   // no trailing EOL
        Assert.Equal("# mine\r\n" + Block + "127.0.0.1 x.com\r\n" + End + "\r\n", after);
    }

    [Fact]
    public void ProcessAddToHosts_InsertsInsideTheBlock_AndKeepsTheUsersLineBelow()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fx7add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var trigger = Path.Combine(dir, "add_to_hosts");
        var hosts = Path.Combine(dir, "hosts");
        var snap = Path.Combine(dir, "monkmode_hosts.block");
        try
        {
            const string added = "127.0.0.1 x.com\r\n";
            File.WriteAllText(trigger, added);
            File.WriteAllText(hosts, "# mine\r\n" + Block + End + "\r\n10.0.0.5 nas.home\r\n");
            File.WriteAllText(snap, Block);

            monkmode.Service1.ProcessAddToHosts(trigger, hosts, snap);

            Assert.Equal("# mine\r\n" + Block + added + End + "\r\n10.0.0.5 nas.home\r\n", File.ReadAllText(hosts));
            Assert.Equal(Block + added, File.ReadAllText(snap));      // snapshot form unchanged
            Assert.False(File.Exists(trigger));                        // trigger consumed
            Assert.True(File.GetAttributes(hosts).HasFlag(FileAttributes.ReadOnly));   // still fail-closed
            // The self-heal agrees with what is now in hosts: no churn.
            Assert.Null(monkmode.Service1.RepairHostsBlock(File.ReadAllText(hosts), File.ReadAllText(snap)));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }
}
