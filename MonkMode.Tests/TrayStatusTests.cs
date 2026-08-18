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

// MonkMode.Tests - the S7b DISPLAY surface: the tray quick-status (P52/P53), the counter
// lines `stats` and `status` grew (P48), and the slot ATTRIBUTION that decides whose
// counter goes up.
//
// THE ONE THAT MATTERS MOST: TrayMenu_ContainsNoWayToStopTheNotifier. The notifier
// performs the user-session app-kill AND, since S7, the URL watcher. A tray with an Exit
// (or Close, or Quit, or Pause) item would therefore be a complete self-bypass of both,
// available by mouse gesture, from a process the user already owns - the single most
// obvious way a "nice quality-of-life feature" could hand back everything B1/B8 defend.
// P53 says the menu is EXACTLY two items and neither of them exits anything; Form1 builds
// its live ContextMenuStrip from TrayMenuItemLabels, so pinning that function's return
// value pins the real menu.
//
// The rest is ordinary display pinning: a tooltip longer than the historical 63-character
// NotifyIcon.Text ceiling must be truncated (not thrown on, not silently dropped), a
// missing/zero counter must print nothing rather than a row of zeros, and no display path
// may be able to throw.
//
// NOTHING HERE IS ENFORCEMENT. Every function under test returns a string, a list of
// strings, or a slot label; none of them is read by the service, the guardian, the MAC,
// the hosts self-heal or the watcher's decision path. No form is constructed, no toast is
// delivered, no tray icon is created, and no file under %ProgramData% is touched.

using System.Globalization;

namespace MonkMode.Tests;

public class TrayStatusTests
{
    // ---------------------------------------------------------------
    // P53 - the menu may not offer a way out
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9999)]
    public void TrayMenu_ContainsNoWayToStopTheNotifier(int blockedToday)
    {
        List<string> labels = mm_notify.Form1.TrayMenuItemLabels(blockedToday);

        // Exactly two items, in order: the Status action and the disabled count label.
        Assert.Equal(2, labels.Count);
        Assert.Equal(mm_notify.Form1.TrayStatusMenuLabel, labels[0]);
        Assert.Equal("Blocked today: " + blockedToday.ToString(CultureInfo.InvariantCulture), labels[1]);

        // And not one of them names an escape hatch. Substring, case-insensitive: an item
        // called "Exit MonkMode" or "quit" must fail this just as loudly as "Exit".
        string[] forbidden = { "exit", "close", "quit", "pause", "stop", "disable", "unblock", "remove", "end" };
        foreach (string label in labels)
        {
            foreach (string word in forbidden)
            {
                Assert.DoesNotContain(word, label, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ---------------------------------------------------------------
    // P52 - the tooltip
    // ---------------------------------------------------------------

    [Fact]
    public void Tooltip_StatesBlockCount_ShortestRemaining_AndTodaysTally()
    {
        Assert.Equal("MonkMode - 2 blocks · 45m left · 7 blocked today",
                     mm_notify.Form1.BuildTraySummary(2, TimeSpan.FromMinutes(45), 7));
    }

    [Fact]
    public void Tooltip_PluralisesOneBlock()
    {
        Assert.Equal("MonkMode - 1 block · 2h left · 0 blocked today",
                     mm_notify.Form1.BuildTraySummary(1, TimeSpan.FromHours(2), 0));
    }

    [Theory]
    // An unknown or already-elapsed remaining span drops the clause entirely rather than
    // print "0m" or a bogus deadline - the rule every other MonkMode message follows.
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-600)]
    public void Tooltip_DropsTheLeftClause_WhenTheSpanIsUnknownOrElapsed(int? seconds)
    {
        TimeSpan? span = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
        Assert.Equal("MonkMode - 3 blocks · 4 blocked today",
                     mm_notify.Form1.BuildTraySummary(3, span, 4));
    }

    [Fact]
    public void Tooltip_TruncatesToTheSixtyThreeCharacterCeiling()
    {
        // The worst realistic case: 8 slots, a long span and a big tally.
        string full = mm_notify.Form1.BuildTraySummary(8, TimeSpan.FromDays(13).Add(TimeSpan.FromMinutes(59)), 123456);
        string shown = mm_notify.Form1.TruncateTrayText(full);
        Assert.True(shown.Length <= mm_notify.Form1.TrayTextMaxLength);
        Assert.StartsWith(shown, full, StringComparison.Ordinal);
        Assert.Equal(63, mm_notify.Form1.TrayTextMaxLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void Tooltip_UnderTheCeiling_IsLeftAlone(string text)
    {
        Assert.Equal(text, mm_notify.Form1.TruncateTrayText(text));
    }

    [Fact]
    public void Tooltip_TruncateHandlesNothing()
    {
        Assert.Equal("", mm_notify.Form1.TruncateTrayText(null));
    }

    [Fact]
    public void Tooltip_ExactlyAtTheCeiling_IsNotCut()
    {
        string exact = new('x', mm_notify.Form1.TrayTextMaxLength);
        Assert.Equal(exact, mm_notify.Form1.TruncateTrayText(exact));
        Assert.Equal(mm_notify.Form1.TrayTextMaxLength,
                     mm_notify.Form1.TruncateTrayText(exact + "y").Length);
    }

    // ---------------------------------------------------------------
    // the expiry toast gains the count
    // ---------------------------------------------------------------

    [Fact]
    public void BlockEndedToast_WithNoCount_IsTheHistoricalStringByteUnchanged()
    {
        // A machine with no sidecar (or a genuinely quiet day) must see exactly the toast
        // it saw before this slice - which is also why the existing pinned wording test
        // does not have to move.
        Assert.Equal(mm_notify.Notifications.BlockEndedMessage(),
                     mm_notify.Notifications.BlockEndedMessageWithCount(0));
        Assert.Equal(mm_notify.Notifications.BlockEndedMessage(),
                     mm_notify.Notifications.BlockEndedMessageWithCount(-3));
    }

    [Theory]
    [InlineData(1, "MonkMode blocked 1 attempt today.")]
    [InlineData(12, "MonkMode blocked 12 attempts today.")]
    public void BlockEndedToast_AppendsTheDaysTally(int count, string tail)
    {
        string msg = mm_notify.Notifications.BlockEndedMessageWithCount(count);
        Assert.StartsWith(mm_notify.Notifications.BlockEndedMessage(), msg, StringComparison.Ordinal);
        Assert.EndsWith(tail, msg, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // P45 attribution - which slot's counter goes up
    // ---------------------------------------------------------------

    [Theory]
    // The target is where the browser was SENT ("https://<matched host>/"), so host
    // equality is the test. The P59 YouTube special case still resolves, because both
    // sides go through NormalizeUrlForMatch first.
    [InlineData("https://youtube.com/", "youtube.com/shorts", true)]
    [InlineData("https://www.youtube.com/feed/subscriptions", "youtube.com/shorts", true)]
    [InlineData("https://www.youtube.com/feed/subscriptions", "youtube.com/", true)]
    [InlineData("https://instagram.com/", "instagram.com/reels", true)]
    [InlineData("https://instagram.com/", "youtube.com/shorts", false)]
    // The documented under-attribution: a subdomain-only pattern does not own the bare
    // host, so its redirect lands in the lifetime total with no slot label.
    [InlineData("https://youtube.com/", "m.youtube.com/shorts", false)]
    public void PatternsOwnTarget_IsHostEquality(string target, string pattern, bool owns)
    {
        Assert.Equal(owns, mm_notify.UrlWatch.PatternsOwnTarget(target, new[] { pattern }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("chrome://settings")]
    [InlineData("about:blank")]
    [InlineData("not a url")]
    public void PatternsOwnTarget_GarbageTarget_OwnsNothing_NeverThrows(string? target)
    {
        Assert.False(mm_notify.UrlWatch.PatternsOwnTarget(target!, new[] { "youtube.com/shorts" }));
    }

    [Fact]
    public void PatternsOwnTarget_NothingPatternSet_OwnsNothing()
    {
        Assert.False(mm_notify.UrlWatch.PatternsOwnTarget("https://youtube.com/", null));
        Assert.False(mm_notify.UrlWatch.PatternsOwnTarget("https://youtube.com/", Array.Empty<string>()));
        Assert.False(mm_notify.UrlWatch.PatternsOwnTarget("https://youtube.com/", new string?[] { null }!));
    }

    private static mm_notify.IniFile SlotIni(params (int pos, string id, string patterns)[] slots)
    {
        mm_notify.IniFile ini = new();
        foreach ((int pos, string id, string patterns) in slots)
        {
            string sec = "Slot" + pos.ToString(CultureInfo.InvariantCulture);
            ini.SetKeyValue(sec, "Id", id);
            ini.SetKeyValue(sec, "UrlPatterns", patterns);
        }
        return ini;
    }

    [Fact]
    public void SlotIdOwningTarget_NamesTheSlotThatAskedForTheBlock()
    {
        var ini = SlotIni((1, "4", "instagram.com/reels"), (2, "7", "youtube.com/shorts|youtube.com/"));
        Assert.Equal("7", mm_notify.Form1.SlotIdOwningTarget(ini, "https://youtube.com/"));
        Assert.Equal("4", mm_notify.Form1.SlotIdOwningTarget(ini, "https://instagram.com/"));
    }

    [Fact]
    public void SlotIdOwningTarget_TwoSlotsOnTheSameHost_CreditsTheFirstPosition()
    {
        // Under-attribution by design: crediting both would make the per-slot counts sum to
        // more than the lifetime total.
        var ini = SlotIni((1, "2", "youtube.com/shorts"), (2, "5", "youtube.com/"));
        Assert.Equal("2", mm_notify.Form1.SlotIdOwningTarget(ini, "https://youtube.com/"));
    }

    [Fact]
    public void SlotIdOwningTarget_NoOwner_IsBlank_SoTheRedirectStillCountsAtLifetime()
    {
        var ini = SlotIni((1, "3", "instagram.com/reels"));
        Assert.Equal("", mm_notify.Form1.SlotIdOwningTarget(ini, "https://youtube.com/"));
        Assert.Equal("", mm_notify.Form1.SlotIdOwningTarget(new mm_notify.IniFile(), "https://youtube.com/"));
        Assert.Equal("", mm_notify.Form1.SlotIdOwningTarget(null, "https://youtube.com/"));
    }

    private static monkmode.Service1.SlotState AppSlot(string id, params string[] apps)
    {
        monkmode.Service1.SlotState s = new() { Id = id };
        s.Apps.AddRange(apps);
        return s;
    }

    [Fact]
    public void SlotIdOwningApp_NamesTheSlotWhoseAppsListTheImage()
    {
        List<monkmode.Service1.SlotState> slots = new()
        {
            AppSlot("1", "steam.exe"),
            AppSlot("6", "chrome.exe", "brave.exe"),
        };
        Assert.Equal("6", monkmode.Service1.SlotIdOwningApp(slots, "chrome"));
        Assert.Equal("1", monkmode.Service1.SlotIdOwningApp(slots, "steam"));
        // Case-insensitive, exactly like the kill matcher it reuses.
        Assert.Equal("6", monkmode.Service1.SlotIdOwningApp(slots, "BRAVE"));
        // Unattributable: still "" (the kill counts at lifetime, never lost).
        Assert.Equal("", monkmode.Service1.SlotIdOwningApp(slots, "notepad"));
        Assert.Equal("", monkmode.Service1.SlotIdOwningApp(null, "chrome"));
        Assert.Equal("", monkmode.Service1.SlotIdOwningApp(slots, null));
    }

    [Fact]
    public void SlotIdOwningApp_TwoSlotsNamingTheSameApp_CreditsTheFirstPosition()
    {
        List<monkmode.Service1.SlotState> slots = new() { AppSlot("2", "chrome.exe"), AppSlot("8", "chrome.exe") };
        Assert.Equal("2", monkmode.Service1.SlotIdOwningApp(slots, "chrome"));
    }

    // ---------------------------------------------------------------
    // P48 - the CLI's counter lines
    // ---------------------------------------------------------------

    private static MonkMode.StatsSidecar.StatsData Sample()
    {
        var d = MonkMode.StatsSidecar.NewDelta("1", 3, 0, 3600, "2026-08-17");
        d = MonkMode.StatsSidecar.Merge(d, MonkMode.StatsSidecar.NewDelta("1", 2, 4, 1800, "2026-08-18"));
        return d;
    }

    [Fact]
    public void StatsActuals_ReportTimeKillsRedirectsStreaksAndToday()
    {
        List<string> lines = MonkMode.Program.FormatStatsActuals(Sample(), "2026-08-18");
        Assert.Equal(5, lines.Count);
        Assert.Contains("Time blocked:", lines[0]);
        Assert.Contains("1h 30m", lines[0]);          // 3600 + 1800 seconds
        Assert.Contains("(actual)", lines[0]);
        Assert.Contains("Apps closed:      5", lines[1]);
        Assert.Contains("Browser nudges:   4", lines[2]);
        Assert.Contains("Focus days:       2", lines[3]);
        Assert.Contains("streak 2", lines[3]);
        Assert.Contains("longest 2", lines[3]);
        Assert.Contains("Today:", lines[4]);
        Assert.Contains("30m", lines[4]);
        Assert.Contains("6 attempt(s) stopped", lines[4]);   // 2 kills + 4 redirects
    }

    [Fact]
    public void StatsActuals_NothingRecorded_IsAnEmptyBlock_SoStatsPrintsNoZeroRows()
    {
        Assert.Empty(MonkMode.Program.FormatStatsActuals(new MonkMode.StatsSidecar.StatsData(), "2026-08-18"));
        Assert.Empty(MonkMode.Program.FormatStatsActuals(null, "2026-08-18"));
        // Kills with no held time is a corrupt-ish shape: claim nothing rather than guess.
        Assert.Empty(MonkMode.Program.FormatStatsActuals(
            MonkMode.StatsSidecar.NewDelta("1", 9, 9, 0, "2026-08-18"), "2026-08-18"));
    }

    [Fact]
    public void StatsActuals_BadTodayKey_StillReportsLifetime_AndClaimsNothingForToday()
    {
        List<string> lines = MonkMode.Program.FormatStatsActuals(Sample(), "not-a-date");
        Assert.Equal(5, lines.Count);
        Assert.Contains("1h 30m", lines[0]);
        Assert.Contains("streak 0", lines[3]);
        Assert.Contains("0 attempt(s) stopped", lines[4]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(0, -4)]
    public void StatusTodayLine_IsBlankOnAQuietDay(long kills, long redirects)
    {
        Assert.Equal("", MonkMode.Program.FormatBlockedTodayLine(kills, redirects));
    }

    [Fact]
    public void StatusTodayLine_NamesBothHalvesOfTheTally()
    {
        Assert.Equal("  Today: 5 stopped (3 app close(s), 2 browser nudge(s))",
                     MonkMode.Program.FormatBlockedTodayLine(3, 2));
        // A negative half is floored at zero rather than subtracting from the total.
        Assert.Equal("  Today: 2 stopped (0 app close(s), 2 browser nudge(s))",
                     MonkMode.Program.FormatBlockedTodayLine(-9, 2));
    }
}
