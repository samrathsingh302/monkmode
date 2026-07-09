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

// MonkMode.Tests - D1a: site presets (named category -> domains, INPUT sugar only).
//
// A preset (`monkmode block --preset social,video`) is a FIXED, compile-time bundle of
// well-known domains the CLI expands into the SAME site list a user could type by hand with
// --sites. It is PURE INPUT: the expanded domains flow into WriteHostsBlock + [User] CustomSites
// exactly like --sites and are MAC-covered downstream by the enforcement canonical (B7) with NO
// new canonical surface and NO schema bump - the preset table is a code constant, not stored
// config, so there is nothing extra to protect. (An EDITABLE *user default* site list is the
// separate D1b slice, stored MAC-covered on the setup ini, mirroring the C6c cooling-off default.)
//
// These tests pin the PURE expander Blocker.TryExpandPresets + Blocker.KnownPresetNames (no
// arming, no DPAPI, no real hosts/registry/SCM - the hard fence):
//   - each known category expands to its domains; multiple presets UNION (deduped, order-
//     preserved); the category name is case-insensitive; empty/whitespace/null => empty list;
//   - an UNKNOWN category FAILS CLOSED - it emits NOTHING and lists every unknown token + the
//     valid names, so a typo can never silently UNDER-block (the schedule day-parser stance);
//   - the table domains are hygienic (bare, lowercase, no scheme/separators);
//   - an expanded preset flows into the MAC-covered hosts block (the enforcement seam).
// The DoBlock `--preset` wiring does live console/service I/O (the smoke seam, fence: unit tests
// never arm a block); its one pure input step (TryExpandPresets) is pinned here, the wiring is
// verifier + the CV smoke.

using System.Collections.Generic;
using System.Linq;

namespace MonkMode.Tests;

public class SitePresetTests
{
    // VB ByRef -> C# ref: pre-initialise both out params (the CoolOff arg-parse test pattern).
    private static bool Expand(string arg, out List<string> domains, out string err)
    {
        List<string> d = new();
        string e = "";
        var ok = MonkMode.Blocker.TryExpandPresets(arg, ref d, ref e);
        domains = d;
        err = e;
        return ok;
    }

    [Theory]
    [InlineData("social", "facebook.com")]
    [InlineData("social", "x.com")]
    [InlineData("video", "youtube.com")]
    [InlineData("news", "cnn.com")]
    [InlineData("shopping", "amazon.com")]
    [InlineData("adult", "pornhub.com")]
    public void KnownPreset_ExpandsToItsDomains(string preset, string expectedDomain)
    {
        Assert.True(Expand(preset, out var domains, out var err));
        Assert.Equal("", err);
        Assert.Contains(expectedDomain, domains);
    }

    [Fact]
    public void Social_ExpandsToTheFullCategory_NoDuplicates()
    {
        // A loose lower bound guards against an accidental table truncation without pinning the
        // exact (product-content) list; the dedupe invariant is exact.
        Assert.True(Expand("social", out var domains, out _));
        Assert.True(domains.Count >= 8, $"'social' should expand to the full category; got {domains.Count}");
        Assert.Equal(domains.Count, domains.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MultiplePresets_UnionTheirDomains_OrderPreserved_Deduped()
    {
        Assert.True(Expand("social", out var social, out _));
        Assert.True(Expand("video", out var video, out _));
        Assert.True(Expand("social,video", out var both, out _));

        // Union preserving order: social's domains, then video's, deduped case-insensitively.
        var expected = social.Concat(video).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(expected, both);
        Assert.Contains("facebook.com", both);
        Assert.Contains("youtube.com", both);
    }

    [Fact]
    public void RepeatedPreset_DedupesToTheSingleExpansion()
    {
        // `--preset social,social` must not double every domain (the `seen` set dedupes).
        Assert.True(Expand("social", out var once, out _));
        Assert.True(Expand("social,social", out var twice, out _));
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("SOCIAL")]
    [InlineData("Social")]
    [InlineData("sOcIaL")]
    public void CategoryName_IsCaseInsensitive(string variant)
    {
        Assert.True(Expand("social", out var canonical, out _));
        Assert.True(Expand(variant, out var got, out var err));
        Assert.Equal("", err);
        Assert.Equal(canonical, got);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespace_YieldsEmptyList_True(string arg)
    {
        // Nothing requested => True + empty (the caller's --sites/--file still apply); never an error.
        Assert.True(Expand(arg, out var domains, out var err));
        Assert.Empty(domains);
        Assert.Equal("", err);
    }

    [Fact]
    public void NullArg_YieldsEmptyList_True()
    {
        // Defensive: production never passes null (GetOption returns ""), but the Is Nothing guard holds.
        Assert.True(Expand(null!, out var domains, out _));
        Assert.Empty(domains);
    }

    [Fact]
    public void UnknownCategory_FailsClosed_EmitsNothing_ListsItAndTheValidNames()
    {
        Assert.False(Expand("banana", out var domains, out var err));
        Assert.Empty(domains);                 // fail-closed: never a partial/silent under-block
        Assert.Contains("banana", err);        // names the offending token
        Assert.Contains("Available presets", err);
        Assert.Contains("social", err);        // offers the valid names
    }

    [Fact]
    public void UnknownMixedWithKnown_EmitsNothing_ListsEveryUnknown()
    {
        // The whole expansion is rejected (NOT partially expanded to social+video), and EVERY
        // unknown token is reported - so the user fixes all typos at once.
        Assert.False(Expand("social,banana,video,kiwi", out var domains, out var err));
        Assert.Empty(domains);
        Assert.Contains("banana", err);
        Assert.Contains("kiwi", err);
        Assert.Contains("Unknown presets", err);   // plural form when >1 unknown
    }

    [Fact]
    public void SemicolonSeparatorAndSurroundingWhitespace_AreTolerated()
    {
        Assert.True(Expand("social,video", out var comma, out _));
        Assert.True(Expand("social;video", out var semi, out _));
        Assert.True(Expand("  social , video  ", out var spaced, out _));
        Assert.Equal(comma, semi);
        Assert.Equal(comma, spaced);
    }

    [Fact]
    public void KnownPresetNames_AreSorted_NonEmpty_AndEachExpandsCleanly()
    {
        var names = MonkMode.Blocker.KnownPresetNames();
        Assert.NotEmpty(names);

        // Sorted ordinal-ignore-case (a stable order for help output + the error hint).
        var sorted = names.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(sorted, names);

        // The documented categories are present, and every category expands to a non-empty,
        // duplicate-free list.
        Assert.Contains("social", names);
        Assert.Contains("adult", names);
        foreach (var name in names)
        {
            Assert.True(Expand(name, out var domains, out var err), $"'{name}' should expand");
            Assert.Equal("", err);
            Assert.NotEmpty(domains);
            Assert.Equal(domains.Count, domains.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void EveryPresetDomain_IsHygienic_BareLowercaseNoSchemeNoSeparators()
    {
        // The table must hold bare registrable domains (like a hand-typed --sites), so downstream
        // NormalizeDomain/BuildHostsEntries + the schedule Spec/list packing all round-trip cleanly.
        foreach (var name in MonkMode.Blocker.KnownPresetNames())
        {
            Assert.True(Expand(name, out var domains, out _));
            foreach (var d in domains)
            {
                Assert.Equal(d.ToLowerInvariant(), d);   // already lowercase
                Assert.Contains(".", d);                 // a real domain
                Assert.DoesNotContain(" ", d);           // no stray whitespace
                Assert.DoesNotContain("://", d);         // bare domain, not a URL
                Assert.DoesNotContain(";", d);           // would break list/Spec packing
                Assert.DoesNotContain("|", d);
            }
        }
    }

    [Fact]
    public void ExpandedPreset_FlowsIntoTheMacCoveredHostsBlock_TheEnforcementSeam()
    {
        // The preset's whole point: its domains are enforced exactly like --sites. Feed an expanded
        // preset through BuildMonkModeBlock (the same string WriteHostsBlock writes to hosts AND the
        // MAC-covered snapshot) and confirm a known domain + its auto www. variant land as real
        // 127.0.0.1 entries under the MonkMode marker - proving the input -> enforcement seam.
        Assert.True(Expand("social", out var domains, out _));
        var block = MonkMode.Blocker.BuildMonkModeBlock(domains);
        Assert.Contains("#### MonkMode Entries ####", block);
        Assert.Contains("127.0.0.1 facebook.com", block);
        Assert.Contains("127.0.0.1 www.facebook.com", block);   // single-dot domain => www. auto-added
    }

    // ---- D1b: TryBuildDefaultSites - the pure setup-time merge of --default-sites + --default-preset
    // into the comma-joined string persisted on the setup file (no DPAPI, no files - the hard fence).
    // It reuses TryExpandPresets (so it inherits the same fail-closed-on-unknown-preset behaviour) and
    // is the one pure step the `setup` verb's console wiring leans on. ----

    private static bool BuildDefaults(string sitesArg, string presetArg, out string packed, out string err)
    {
        string p = "";
        string e = "";
        var ok = MonkMode.Blocker.TryBuildDefaultSites(sitesArg, presetArg, ref p, ref e);
        packed = p;
        err = e;
        return ok;
    }

    [Fact]
    public void BuildDefaults_SitesOnly_PacksTrimmedCommaJoined()
    {
        Assert.True(BuildDefaults("reddit.com, x.com ;news.ycombinator.com", null!, out var packed, out var err));
        Assert.Equal("", err);
        // Split on , / ; , each token trimmed, order preserved.
        Assert.Equal("reddit.com,x.com,news.ycombinator.com", packed);
    }

    [Fact]
    public void BuildDefaults_PresetOnly_PacksExpandedDomains()
    {
        Assert.True(BuildDefaults(null!, "video", out var packed, out var err));
        Assert.Equal("", err);
        // Exactly the expander's domains, comma-joined (the video category, in table order).
        Expand("video", out var vid, out _);
        Assert.Equal(string.Join(",", vid), packed);
    }

    [Fact]
    public void BuildDefaults_SitesAndPreset_Union_SitesFirst_DedupedCaseInsensitive()
    {
        // reddit.com appears in BOTH the explicit sites and the `social` preset: it must appear ONCE,
        // in its FIRST position (the explicit --default-sites slot), with the rest of social appended.
        Assert.True(BuildDefaults("mysite.com,REDDIT.COM", "social", out var packed, out _));
        var parts = packed.Split(',');
        Assert.Equal("mysite.com", parts[0]);
        Assert.Equal("REDDIT.COM", parts[1]);              // the explicit token's own casing is preserved
        // deduped case-insensitively: the preset's lowercase "reddit.com" is NOT re-added.
        Assert.Single(parts, p => string.Equals(p, "reddit.com", System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(parts.Length, parts.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("facebook.com", parts);            // the rest of social still present
    }

    [Fact]
    public void BuildDefaults_UnknownPreset_FailsClosed_PacksNothing()
    {
        // Inherits TryExpandPresets' fail-closed contract: an unknown category returns False + a naming
        // error and packs NOTHING, so the setup verb aborts before writing (no partial/typo'd default).
        Assert.False(BuildDefaults("reddit.com", "socail", out var packed, out var err));
        Assert.Equal("", packed);
        Assert.Contains("Unknown preset", err);
        Assert.Contains("socail", err);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", " ; , ")]     // whitespace / separators-only on both sides => nothing
    public void BuildDefaults_EmptyInputs_SucceedWithEmptyPacked(string sitesArg, string presetArg)
    {
        Assert.True(BuildDefaults(sitesArg, presetArg, out var packed, out var err));
        Assert.Equal("", packed);
        Assert.Equal("", err);
    }

    [Fact]
    public void BuildDefaults_DuplicateAndBlankSiteTokens_AreDedupedAndDropped()
    {
        Assert.True(BuildDefaults("a.com,,a.com, b.com ,A.COM", null!, out var packed, out _));
        Assert.Equal("a.com,b.com", packed);   // blanks dropped, a.com deduped case-insensitively
    }
}
