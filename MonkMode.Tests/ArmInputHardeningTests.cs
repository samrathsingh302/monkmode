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

// MonkMode.Tests - FX4 (v1.1): the ARM-INPUT surface, two P1s.
//
// F4 - the shipped URL-ONLY wrappers could not arm, or armed everything.
//   `mm-shorts` / `mm-lock shorts` compose `monkmode block --urls <patterns> --for X` with
//   NO --sites and NO --apps (presets/mm-presets.ps1:447 -> MM-ArmSlot '' 'shortform-urls.txt',
//   and MM-BuildBlockArgs omits an empty --sites). DoBlock's emptiness gate and its two
//   account-default inherits all predate --urls and read only the site/app counts, so:
//     * with no defaults configured the gate refused the command outright (exit 1 - the
//       shipped wrapper was simply dead);
//     * WITH defaults configured, both inherits fired and a "Shorts/Reels ONLY" command
//       hosts-blocked the entire default site list and killed every default app, for the
//       full duration, uncancellable.
//   Pinned here on the pure decision layer (ShouldInheritDefaults / HasNothingToBlock /
//   NothingToBlockMessage) because DoBlock itself does console + service I/O and cannot be
//   unit-run without arming a real block.
//
// F30 - ONE control character permanently bricked the config AND froze every other block.
//   IniFile.Save writes `key & "=" & value` verbatim (IniFile.vb:216) while Load splits on
//   \r\n / \n / \r (:177-200), so a stored value carrying LF/CR is written as one line and
//   read back as two: the reloaded canonical no longer matches the stamped one, macValid
//   goes False and STAYS False - no cooling-off, no partner-code exit, no retire, only
//   `unblock --force`. Nothing validated: PackList/PackApps only Trim(), TryBuildUrlPatterns
//   rejected only |/;/over-length, SplitList split only on , and ;.
//   The fix REFUSES (never strips, never truncates) at every input funnel, and the tests
//   below walk each funnel plus the exact reported repro.
//
// Fences honoured: pure functions and the test-bin ini only (wiped in finally). Never the
// real hosts file, the registry, the SCM (service presence is INJECTED), or a deployed
// config. Nothing is ever armed for real.

namespace MonkMode.Tests;

public class ArmInputDecisionTests
{
    // ---- F4: ShouldInheritDefaults - the exact URL-only rule ----

    [Fact]
    public void UrlOnlyArm_InheritsNoDefaults()
    {
        // The whole point: --urls given, no site source, no app source => defaults stay out,
        // so `mm-shorts` arms URL patterns and NOTHING else even on a machine with defaults.
        Assert.False(MonkMode.Blocker.ShouldInheritDefaults(0, 0, true));
    }

    [Fact]
    public void BareBlock_StillInheritsDefaults_UnchangedByFx4()
    {
        // `monkmode block --for 2h` with defaults configured: pre-FX4 behaviour, untouched.
        Assert.True(MonkMode.Blocker.ShouldInheritDefaults(0, 0, false));
    }

    [Theory]
    // An arm that named ANY site or app source is not URL-only, so the pre-FX4 per-dimension
    // inheritance stands - including alongside --urls.
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, false)]
    public void ExplicitSiteOrAppSource_KeepsPreFx4Inheritance(int domains, int apps, bool urls)
        => Assert.True(MonkMode.Blocker.ShouldInheritDefaults(domains, apps, urls));

    // ---- F4: the emptiness gate now counts --urls ----

    [Fact]
    public void UrlOnlyArm_PassesTheEmptinessGate()
        => Assert.False(MonkMode.Blocker.HasNothingToBlock(0, 0, true));

    [Fact]
    public void AllThreeEmpty_IsStillRefused()
        => Assert.True(MonkMode.Blocker.HasNothingToBlock(0, 0, false));

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    public void AnythingToBlock_IsNotRefused(int domains, int apps, bool urls)
        => Assert.False(MonkMode.Blocker.HasNothingToBlock(domains, apps, urls));

    [Fact]
    public void NothingToBlockMessage_NamesUrls()
    {
        // A user told only about --sites/--preset/--apps/--app-preset would never learn that
        // a URL-only block is legal - which is exactly how F4 hid.
        var m = MonkMode.Blocker.NothingToBlockMessage;
        Assert.StartsWith("Nothing to block.", m, StringComparison.Ordinal);
        Assert.Contains("--urls", m, StringComparison.Ordinal);
        Assert.Contains("--sites", m, StringComparison.Ordinal);
        Assert.Contains("--apps", m, StringComparison.Ordinal);
    }
}

public class ControlCharRefusalTests
{
    // The five characters the report and the brief name.
    public static TheoryData<string> BadChars => new() { "\n", "\r", "\t", "\u001F", "\u007F" };

    [Theory]
    [MemberData(nameof(BadChars))]
    public void InteriorControlChar_RefusesWholeList_AndNamesTheValue(string bad)
    {
        var err = "";
        var ok = MonkMode.Blocker.TryRejectControlChars(
            "site", new[] { "reddit.com", "news" + bad + "bbc.co.uk", "x.com" }, ref err);
        Assert.False(ok);
        // Names the OFFENDING value (escaped so the message stays on one line), not just "a site".
        Assert.Contains("news", err, StringComparison.Ordinal);
        Assert.Contains("bbc.co.uk", err, StringComparison.Ordinal);
        Assert.Contains("site", err, StringComparison.Ordinal);
        // The raw character never reaches the console - it is rendered, so the error is legible.
        Assert.DoesNotContain(bad, err, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalMessage_RendersTheCharacter()
    {
        var err = "";
        Assert.False(MonkMode.Blocker.TryRejectControlChars("site", new[] { "a\nb" }, ref err));
        Assert.Contains(@"\n", err, StringComparison.Ordinal);
        err = "";
        Assert.False(MonkMode.Blocker.TryRejectControlChars("app", new[] { "a\tb.exe" }, ref err));
        Assert.Contains(@"\t", err, StringComparison.Ordinal);
        err = "";
        Assert.False(MonkMode.Blocker.TryRejectControlChars("site", new[] { "a\u001Fb" }, ref err));
        Assert.Contains(@"\x1F", err, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSpace_IsStillAccepted()
    {
        // The control test: refusing is fail-closed, but over-refusing a legitimate value would
        // be a usability regression, so only CONTROL characters are refused.
        var err = "";
        Assert.True(MonkMode.Blocker.TryRejectControlChars(
            "site", new[] { "reddit.com", "some site.com", "a-b_c.example.co.uk" }, ref err));
        Assert.Equal("", err);
    }

    [Fact]
    public void LeadingOrTrailingWhitespace_IsAccepted_BecauseItIsTrimmedBeforeStorage()
    {
        // PackList/PackApps Trim() before writing, so a stray pasted newline never reaches the
        // ini and must not cost the user the whole arm. Only what would be STORED is judged.
        var err = "";
        Assert.True(MonkMode.Blocker.TryRejectControlChars("site", new[] { "\nreddit.com\t", "  x.com  " }, ref err));
    }

    [Fact]
    public void EmptyAndNullInputs_AreClean()
    {
        var err = "";
        Assert.True(MonkMode.Blocker.TryRejectControlChars("site", Array.Empty<string>(), ref err));
        Assert.True(MonkMode.Blocker.TryRejectControlChars("site", null!, ref err));
        Assert.True(MonkMode.Blocker.TryRejectControlChars("site", new[] { null!, "ok.com" }, ref err));
    }

    [Theory]
    [InlineData('\u0000', true)]
    [InlineData('\t', true)]        // TAB counts - never legitimate in a hostname or an exe name
    [InlineData('\u001F', true)]
    [InlineData(' ', false)]        // 0x20 - the first accepted character
    [InlineData('~', false)]        // 0x7E
    [InlineData('\u007F', true)]    // DEL
    [InlineData('\u0080', false)]   // 0x80 - outside the refused range
    [InlineData('e', false)]
    public void IsControlChar_BoundaryTable(char c, bool expected)
        => Assert.Equal(expected, MonkMode.Blocker.IsControlChar(c));

    [Fact]
    public void RenderValueForMessage_EscapesOnlyControlChars()
        => Assert.Equal(@"a\nb\tc\rd\x00e f",
                        MonkMode.Blocker.RenderValueForMessage("a\nb\tc\rd\u0000e f"));
}

public class ControlCharFunnelTests
{
    // Each test below is ONE input route into a MAC-covered stored value. Together they are
    // the route-coverage argument: every route reaches the ini through one of these.

    // ---- funnel 1: block --urls ----

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u001F")]
    [InlineData("\u007F")]
    public void UrlPatterns_RefuseControlChars(string bad)
    {
        string packed = "x", err = "";
        Assert.False(MonkMode.Blocker.TryBuildUrlPatterns("*/watch*,*/sho" + bad + "rts*", ref packed, ref err));
        Assert.Equal("", packed);                                   // nothing packed - refuse, not subset
        Assert.Contains("URL pattern", err, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlPatterns_CleanListStillBuilds()
    {
        string packed = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns("*/watch*, */shorts*", ref packed, ref err));
        Assert.Equal("*/watch*|*/shorts*", packed);
    }

    // ---- funnel 2: setup --default-sites / --default-apps (the stored defaults) ----

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u001F")]
    [InlineData("\u007F")]
    public void DefaultSites_RefuseControlChars(string bad)
    {
        string packed = "x", err = "";
        Assert.False(MonkMode.Blocker.TryBuildDefaultSites("reddit.com,b" + bad + "bc.co.uk", "", ref packed, ref err));
        Assert.Equal("", packed);                                   // nothing stored - the setup write aborts
        Assert.Contains("default site", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u007F")]
    public void DefaultApps_RefuseControlChars(string bad)
    {
        string packed = "x", err = "";
        Assert.False(MonkMode.Blocker.TryBuildDefaultApps("chrome.exe,ste" + bad + "am.exe", "", ref packed, ref err));
        Assert.Equal("", packed);
        Assert.Contains("default app", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_CleanListsStillBuild()
    {
        string packed = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildDefaultSites("reddit.com, x.com", "", ref packed, ref err));
        Assert.Equal("reddit.com,x.com", packed);
        packed = ""; err = "";
        Assert.True(MonkMode.Blocker.TryBuildDefaultApps("chrome.exe, steam.exe", "", ref packed, ref err));
        Assert.Equal("chrome.exe,steam.exe", packed);
    }

    // ---- funnel 3: monkmode schedule --sites / --apps (the global [Schedule] Spec) ----
    //
    // The schedule does NOT go through PackList/PackApps - it has its own builder writing ONE
    // MAC-covered Spec value - so it needed the same refusal in its own right.

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u001F")]
    public void ScheduleSpec_RefusesControlCharInASite(string bad)
    {
        string spec = "x", err = "";
        Assert.False(MonkMode.Blocker.TryBuildScheduleSpec(
            "Mon-Fri 09:00-17:00", new[] { "red" + bad + "dit.com" }, Array.Empty<string>(), ref spec, ref err));
        Assert.Equal("", spec);
        Assert.Contains("control character", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u007F")]
    public void ScheduleSpec_RefusesControlCharInAnApp(string bad)
    {
        string spec = "x", err = "";
        Assert.False(MonkMode.Blocker.TryBuildScheduleSpec(
            "Mon-Fri 09:00-17:00", new[] { "reddit.com" }, new[] { "chr" + bad + "ome.exe" }, ref spec, ref err));
        Assert.Equal("", spec);
        Assert.Contains("control character", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleSpec_CleanInputStillBuilds()
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(
            "Mon-Fri 09:00-17:00", new[] { "reddit.com" }, new[] { "chrome.exe" }, ref spec, ref err));
        Assert.Contains("sites=reddit.com", spec, StringComparison.Ordinal);
    }
}

// The writer backstop and the exact reported repro both drive the REAL ArmSlot against the
// test-bin ini, so they share the ini-writing collection.
[Collection("CliIniWriters")]
public class ControlCharArmRefusalTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static MonkMode.Blocker.ArmResult Arm(string[] sites, string[] apps, string urls = "")
        => MonkMode.Blocker.ArmSlot(sites, apps, urls, null, Ends, false);

    // ---- funnel 4: the writer chokepoint - EVERY arm reaches the ini through ArmSlot ----

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u001F")]
    [InlineData("\u007F")]
    public void ArmSlot_RefusesAControlCharInASite_AndWritesNothing(string bad)
    {
        Wipe();
        try
        {
            var r = Arm(new[] { "red" + bad + "dit.com" }, Array.Empty<string>());
            Assert.Equal(MonkMode.Blocker.ArmOutcome.BadInput, r.Outcome);
            Assert.False(r.Ok);
            Assert.Contains("control character", r.Message, StringComparison.Ordinal);
            // Refusing is completely side-effect free: the check runs before the ini is loaded.
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));
            Assert.Equal("", r.PartnerCode);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ArmSlot_RefusesAControlCharInAnApp_OrInTheUrlPatterns()
    {
        Wipe();
        try
        {
            var a = Arm(new[] { "reddit.com" }, new[] { "chr\nome.exe" });
            Assert.Equal(MonkMode.Blocker.ArmOutcome.BadInput, a.Outcome);
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));

            var u = Arm(new[] { "reddit.com" }, Array.Empty<string>(), "*/wat\nch*");
            Assert.Equal(MonkMode.Blocker.ArmOutcome.BadInput, u.Outcome);
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }

    // ---- the exact reported repro ----

    [Fact]
    public void SecondArmWithALineFeed_IsRefused_AndLeavesTheFirstBlockArmedAndMacValid()
    {
        Wipe();
        try
        {
            // Arm 1: good.
            var first = Arm(new[] { "reddit.com" }, new[] { "chrome.exe" });
            Assert.True(first.Ok);
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            // Arm 2: one LF in a site value. BEFORE FX4 this was written verbatim, split the
            // stored line on reload and left macValid False FOREVER - freezing arm 1 too.
            var second = Arm(new[] { "x.com\nevil.com" }, Array.Empty<string>());
            Assert.Equal(MonkMode.Blocker.ArmOutcome.BadInput, second.Outcome);
            Assert.Contains("control character", second.Message, StringComparison.Ordinal);

            // The config is byte-identical and still MAC-valid: arm 1 is untouched and can
            // still cool off, take its partner code and retire normally.
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
            Assert.Single(MonkMode.Blocker.ArmedSlotIds());
            Assert.Equal(first.Id, MonkMode.Blocker.ArmedSlotIds()[0]);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void SecondArmWithAPlainSpace_StillArms()
    {
        // Control for the repro above: only CONTROL characters are refused, so a second arm
        // carrying an ordinary (if odd) space still appends beside the first.
        Wipe();
        try
        {
            Assert.True(Arm(new[] { "reddit.com" }, Array.Empty<string>()).Ok);
            var second = Arm(new[] { "some site.com" }, Array.Empty<string>());
            Assert.True(second.Ok);
            Assert.Equal(2, MonkMode.Blocker.ArmedSlotIds().Count);
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
        }
        finally { Wipe(); }
    }
}
