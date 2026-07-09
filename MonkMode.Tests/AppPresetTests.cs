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

// MonkMode.Tests - D2a: app presets (named category -> executables, INPUT sugar only).
//
// An app preset (`monkmode block --app-preset games,chat`) is a FIXED, compile-time bundle of
// well-known distraction executables the CLI expands into the SAME app-kill list a user could type
// by hand with --apps. It is PURE INPUT: the expanded .exe names flow into [Process] List exactly
// like --apps and are MAC-covered downstream by the enforcement canonical (B7) with NO new canonical
// surface and NO schema bump - the table is a code constant, not stored config, so there is nothing
// extra to protect. (An EDITABLE *user default* app list is the separate D2b slice, stored MAC-
// covered on the setup ini, mirroring the D1b default site list.)
//
// These tests pin the PURE expander Blocker.TryExpandAppPresets + Blocker.KnownAppPresetNames (no
// arming, no DPAPI, no real hosts/registry/SCM - the hard fence):
//   - each known category expands to its executables; multiple presets UNION (deduped, order-
//     preserved); the category name is case-insensitive; empty/whitespace/null => empty list;
//   - an UNKNOWN category FAILS CLOSED - it emits NOTHING and lists every unknown token + the valid
//     names, so a typo can never silently UNDER-kill (the same stance as the D1a site-preset parser);
//   - the table entries are hygienic process-image names (bare, lowercase, .exe, no separators).
// The DoBlock `--app-preset` wiring does live console/service I/O (the smoke seam, fence: unit tests
// never arm a block); its one pure input step (TryExpandAppPresets) is pinned here, the wiring is
// verifier + the smoke. The exact bundle MEMBERSHIP is refinable product content and is deliberately
// asserted only by loose lower bounds, never pinned to an exact list.

using System.Collections.Generic;
using System.Linq;

namespace MonkMode.Tests;

public class AppPresetTests
{
    // VB ByRef -> C# ref: pre-initialise both out params (the D1a Expand helper pattern).
    private static bool ExpandApps(string arg, out List<string> apps, out string err)
    {
        List<string> a = new();
        string e = "";
        var ok = MonkMode.Blocker.TryExpandAppPresets(arg, ref a, ref e);
        apps = a;
        err = e;
        return ok;
    }

    [Theory]
    [InlineData("games", "steam.exe")]
    [InlineData("games", "valorant.exe")]
    [InlineData("chat", "discord.exe")]
    [InlineData("chat", "slack.exe")]
    public void KnownAppPreset_ExpandsToItsExecutables(string preset, string expectedExe)
    {
        Assert.True(ExpandApps(preset, out var apps, out var err));
        Assert.Equal("", err);
        Assert.Contains(expectedExe, apps);
    }

    [Fact]
    public void Games_ExpandsToTheFullCategory_NoDuplicates()
    {
        // A loose lower bound guards against an accidental table truncation without pinning the exact
        // (product-content) list; the dedupe invariant is exact.
        Assert.True(ExpandApps("games", out var apps, out _));
        Assert.True(apps.Count >= 5, $"games preset unexpectedly small: {apps.Count}");
        Assert.Equal(apps.Count, apps.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MultipleAppPresets_Union_Deduped_OrderPreserved()
    {
        // games,chat = the union of both categories, category order preserved (all games before any
        // chat), no duplicates across the union.
        Assert.True(ExpandApps("games,chat", out var union, out var err));
        Assert.Equal("", err);

        ExpandApps("games", out var games, out _);
        ExpandApps("chat", out var chat, out _);

        // Every category member is present.
        foreach (var g in games) Assert.Contains(g, union);
        foreach (var c in chat) Assert.Contains(c, union);
        // Order preserved: the last game appears before the first chat app.
        Assert.True(union.IndexOf(games.Last()) < union.IndexOf(chat.First()));
        // No duplicates across the union.
        Assert.Equal(union.Count, union.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AppPresetName_IsCaseInsensitive()
    {
        Assert.True(ExpandApps("GAMES", out var upper, out _));
        Assert.True(ExpandApps("games", out var lower, out _));
        Assert.Equal(lower, upper);
        Assert.Contains("steam.exe", upper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ")]     // separators-only => nothing requested
    public void EmptyOrWhitespaceOrNull_YieldsEmptyList_Succeeds(string arg)
    {
        Assert.True(ExpandApps(arg, out var apps, out var err));
        Assert.Empty(apps);
        Assert.Equal("", err);
    }

    [Fact]
    public void UnknownAppPreset_FailsClosed_EmitsNothing_ListsUnknownAndValid()
    {
        // A typo'd category must FAIL CLOSED (not silently under-kill): emit nothing, name the unknown
        // token, and list the valid categories as a hint.
        Assert.False(ExpandApps("gaems", out var apps, out var err));
        Assert.Empty(apps);
        Assert.Contains("Unknown app preset", err);
        Assert.Contains("gaems", err);
        foreach (var name in MonkMode.Blocker.KnownAppPresetNames())
            Assert.Contains(name, err);
    }

    [Fact]
    public void MixedKnownAndUnknown_FailsClosed_EmitsNothing()
    {
        // A valid category alongside an unknown one still emits NOTHING (never a partial expansion of
        // only the known token) - the D1a site-preset fail-closed stance.
        Assert.False(ExpandApps("games,notacategory", out var apps, out var err));
        Assert.Empty(apps);
        Assert.Contains("notacategory", err);
    }

    [Fact]
    public void MultipleUnknown_AreAllListed_Pluralised()
    {
        Assert.False(ExpandApps("foo,bar", out var apps, out var err));
        Assert.Empty(apps);
        Assert.Contains("Unknown app presets:", err);   // plural
        Assert.Contains("foo", err);
        Assert.Contains("bar", err);
    }

    [Fact]
    public void TableEntries_AreHygienic_LowercaseExeNoSeparators()
    {
        // Every entry across every category is a bare process-image name the notifier/BlockedApps can
        // compare on: lowercase, ends .exe, no whitespace or ,/; separators, no scheme/path.
        foreach (var name in MonkMode.Blocker.KnownAppPresetNames())
        {
            Assert.True(ExpandApps(name, out var apps, out _));
            Assert.NotEmpty(apps);
            foreach (var exe in apps)
            {
                Assert.Equal(exe.ToLowerInvariant(), exe);
                Assert.EndsWith(".exe", exe);
                Assert.DoesNotContain(" ", exe);
                Assert.DoesNotContain(",", exe);
                Assert.DoesNotContain(";", exe);
                Assert.DoesNotContain("/", exe);
                Assert.DoesNotContain("\\", exe);
            }
        }
    }
}
