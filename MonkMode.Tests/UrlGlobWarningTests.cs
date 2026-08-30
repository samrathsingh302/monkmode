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

// MonkMode.Tests - the --urls GLOB FOOTGUN warning (backlog item, found 26/08/2026).
//
// The P57 matcher is ORDINAL SUBSTRING by design (MM_notify\UrlWatch.vb MatchedPatternFor):
// there are no wildcards, so a "*" is compared literally, and since real web addresses never
// contain one, a pattern carrying "*" matches NOTHING - silently. The block arms, `status`
// lists the patterns, and the nudge just never fires. That is what cost a live FX8 drill
// attempt, and the exe's own help shipped the broken form (`--urls "*/watch*"`) as its
// example while the docs said the opposite.
//
// WHAT THIS PINS:
//   - a pattern carrying "*" produces a warning naming that pattern and its rewrite;
//   - a clean pattern produces NOTHING (an unconditional nag would train the eye past it);
//   - the warning is a NUDGE and only a nudge - TryBuildUrlPatterns still accepts the
//     asterisked pattern unchanged, because refusing would turn a cosmetic mistake into a
//     failed arm and silently rewriting would arm something the user did not type.
//
// Fences honoured: pure string functions only. Nothing here arms, reads the hosts file, the
// registry or the SCM.

namespace MonkMode.Tests;

public class UrlGlobWarningTests
{
    [Fact]
    public void AGlobPattern_IsWarnedAbout_WithItsRewrite()
    {
        var w = MonkMode.Blocker.UrlGlobWarningLine("*/watch*");
        Assert.NotEqual("", w);
        Assert.Contains("*/watch*", w);
        Assert.Contains("/watch", w);
        // Say WHY, not just that: "plain text, not wildcards" is the fact the user is missing.
        Assert.Contains("PLAIN TEXT", w);
    }

    [Fact]
    public void ACleanPattern_SaysNothingAtAll()
    {
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine("youtube.com/shorts"));
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine("youtube.com/shorts,instagram.com/reels"));
        // The deliberate front-page form (P58 exact-home token) is not a glob either.
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine("youtube.com/"));
    }

    [Fact]
    public void NoArgumentAtAll_SaysNothing()
    {
        // GetOption returns "" for an absent --urls, and Nothing is reachable from a direct
        // caller: neither may produce a warning about patterns that do not exist.
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine(""));
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine("   "));
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine(null));
        Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine(",,,"));
    }

    [Fact]
    public void OnlyTheOffendingPatternsAreNamed()
    {
        // A mixed list must not tar the good patterns: the user has to be able to see which
        // one to fix.
        var w = MonkMode.Blocker.UrlGlobWarningLine("youtube.com/shorts,*reddit.com/r/*");
        Assert.NotEqual("", w);
        Assert.Contains("*reddit.com/r/*", w);
        Assert.Contains("reddit.com/r/", w);
        Assert.DoesNotContain("\"youtube.com/shorts\"", w);
    }

    [Fact]
    public void ARepeatedGlob_IsNamedOnce()
    {
        var w = MonkMode.Blocker.UrlGlobWarningLine("*/watch*, */watch*");
        Assert.Equal(1, w.Split("\"*/watch*\"").Length - 1);
    }

    [Fact]
    public void APatternThatIsNothingButStars_SuggestsAShapeInsteadOfAnEmptyString()
    {
        // Stripping "*" out of "**" leaves nothing, and printing `"**" -> ""` would be worse
        // than useless. Point at the form that works instead.
        var w = MonkMode.Blocker.UrlGlobWarningLine("**");
        Assert.Contains("**", w);
        Assert.DoesNotContain("-> \"\"", w);
        Assert.Contains("youtube.com/shorts", w);
    }

    [Fact]
    public void TheWarningIsANudge_TheGlobStillArmsExactlyAsTyped()
    {
        // The load-bearing half. This is a nudge layer with NO enforcement authority: the
        // parse still succeeds and the pattern is stored verbatim, so an existing script
        // that has been passing globs for weeks keeps arming its block (uselessly, but
        // exactly as it did yesterday) instead of suddenly failing.
        string packed = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns("*/watch*", ref packed, ref err));
        Assert.Equal("*/watch*", packed);
        Assert.Equal("", err);
        Assert.NotEqual("", MonkMode.Blocker.UrlGlobWarningLine("*/watch*"));
    }

    [Fact]
    public void TheShippedHelpNoLongerCarriesTheBrokenExample()
    {
        // The regression that made this worth a slice: the exe's own usage text taught the
        // glob form. Whatever example the help prints, it must not be one this function
        // would have to warn about.
        foreach (var example in new[] { "youtube.com/shorts", "youtube.com/shorts,reddit.com/r/all" })
            Assert.Equal("", MonkMode.Blocker.UrlGlobWarningLine(example));
    }
}

// ---- FX5 leftover (19/08/2026): the failed-service-install warning must not overstate ----

public class ServiceInstallFailureMessageTests
{
    [Fact]
    public void WithTheHostsWriteDone_ItSaysTheSitesAreAlreadyBlocked()
    {
        var lines = MonkMode.Program.FormatServiceInstallFailureLines("access denied", true);
        Assert.Contains(lines, l => l.Contains("the block IS armed"));
        Assert.Contains(lines, l => l.Contains("access denied"));
        Assert.Contains(lines, l => l.Contains("stay in your hosts file"));
    }

    [Fact]
    public void WithTheHostsWriteFailedToo_ItDoesNotClaimTheSitesAreBlocked()
    {
        // The P3 the FX5 verifier raised: the single old literal promised "the blocked sites
        // stay in your hosts file meanwhile" even when the hosts write above had ALSO failed,
        // i.e. when nothing whatever was blocking. Overstating coverage is the one thing a
        // self-control tool must not do - the user walks away believing they are blocked.
        var lines = MonkMode.Program.FormatServiceInstallFailureLines("access denied", false);
        Assert.Contains(lines, l => l.Contains("the block IS armed"));
        Assert.Contains(lines, l => l.Contains("nothing is being blocked at the moment"));
        Assert.DoesNotContain(lines, l => l.Contains("stay in your hosts file"));
    }

    [Fact]
    public void EitherWay_ItNeverThrowsAndNeverPrintsABlankLine()
    {
        // This runs inside a Catch, past the point where the slot is COMMITTED (FX5/F6):
        // nothing from here to the partner-code print may throw, including on a null message.
        foreach (var hostsWritten in new[] { true, false })
        {
            var lines = MonkMode.Program.FormatServiceInstallFailureLines(null, hostsWritten);
            Assert.Equal(2, lines.Count);
            Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        }
    }
}
