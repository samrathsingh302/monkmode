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

// MonkMode.Tests - D1c: hosts subdomain mirror coverage (no-bypass web coverage).
//
// BuildHostsEntries expands a bare second-level domain into the bare host plus the
// www./m./web./mobile. mirrors, so blocking snapchat.com also covers Snapchat's web
// app at web.snapchat.com and the mobile-web mirrors (m.facebook.com, mobile.twitter.com).
// A domain typed WITH an explicit subdomain is emitted as-is (no variants). Over-blocking
// an absent mirror is inert; UNDER-blocking a live one is the bypass this slice closes.
// The exact byte layout is pinned here (the canonical hosts-output pin); the CLI<->service
// parity is pinned separately in ScheduleTests.HostsEntriesParityTests. In-memory strings
// only - the real hosts file is never touched.

using Xunit;

namespace MonkMode.Tests;

public class HostsMirrorExpansionTests
{
    [Fact]
    public void BareDomain_ExpandsToBarePlusFourMirrors_InFixedOrder()
    {
        // (a) snapchat.com -> bare first, then www./m./web./mobile. in that stable order,
        // so blocking the site with no explicit subdomain still covers the web app.
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "snapchat.com" });
        Assert.Equal(
            "127.0.0.1 snapchat.com\r\n" +
            "127.0.0.1 www.snapchat.com\r\n" +
            "127.0.0.1 m.snapchat.com\r\n" +
            "127.0.0.1 web.snapchat.com\r\n" +
            "127.0.0.1 mobile.snapchat.com\r\n",
            entries);
    }

    [Fact]
    public void BareDomain_CoversTheWebAndMobileMirrors_TheBypassThisClosed()
    {
        // The point of the slice, stated as membership: the web app + mobile-web hosts are covered.
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "snapchat.com" });
        Assert.Contains("127.0.0.1 web.snapchat.com\r\n", entries);
        Assert.Contains("127.0.0.1 m.snapchat.com\r\n", entries);
        Assert.Contains("127.0.0.1 mobile.snapchat.com\r\n", entries);
        Assert.Contains("127.0.0.1 www.snapchat.com\r\n", entries);
    }

    [Fact]
    public void ExplicitSubdomain_GetsNoVariants_EmittedAsIs()
    {
        // (b) A hand-typed explicit subdomain (two dots) keeps the pre-existing behaviour: emitted
        // verbatim, no mirror expansion (we must not invent siblings for an explicitly narrowed host).
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "web.snapchat.com" });
        Assert.Equal("127.0.0.1 web.snapchat.com\r\n", entries);
    }

    [Fact]
    public void WwwPrefixedInput_Unchanged_JustTheOneLine()
    {
        // (c) A www.-prefixed input is left alone (the StartsWith("www.") guard): one line, no
        // variants - unchanged from before this slice.
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "www.snapchat.com" });
        Assert.Equal("127.0.0.1 www.snapchat.com\r\n", entries);
    }

    [Fact]
    public void BareAndExplicitSubdomain_Deduped_NoRepeatedLine()
    {
        // (d) Given BOTH snapchat.com (which auto-expands web.snapchat.com) AND an explicit
        // web.snapchat.com, the web.snapchat.com line appears exactly once - the bare domain's
        // expansion emits it first, the explicit entry is deduped away.
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "snapchat.com", "web.snapchat.com" });
        Assert.Equal(
            "127.0.0.1 snapchat.com\r\n" +
            "127.0.0.1 www.snapchat.com\r\n" +
            "127.0.0.1 m.snapchat.com\r\n" +
            "127.0.0.1 web.snapchat.com\r\n" +
            "127.0.0.1 mobile.snapchat.com\r\n",
            entries);
        Assert.Equal(1, CountOccurrences(entries, "127.0.0.1 web.snapchat.com\r\n"));
    }

    [Fact]
    public void MultiDotRegistrableDomain_GetsNoVariants()
    {
        // A multi-dot registrable domain (bbc.co.uk) is NOT a bare second-level domain by the
        // single-dot rule, so it gets no mirror variants - the pre-existing multi-dot behaviour,
        // unchanged by this slice.
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "bbc.co.uk" });
        Assert.Equal("127.0.0.1 bbc.co.uk\r\n", entries);
    }

    [Fact]
    public void DuplicateBareDomain_EmittedOnce_AcrossExpansion()
    {
        // The same bare domain listed twice yields one expansion, not two (dedup by host name),
        // so a doubled --sites entry never bloats the block with repeated lines.
        var entries = MonkMode.Blocker.BuildHostsEntries(new[] { "snapchat.com", "snapchat.com" });
        Assert.Equal(
            "127.0.0.1 snapchat.com\r\n" +
            "127.0.0.1 www.snapchat.com\r\n" +
            "127.0.0.1 m.snapchat.com\r\n" +
            "127.0.0.1 web.snapchat.com\r\n" +
            "127.0.0.1 mobile.snapchat.com\r\n",
            entries);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
