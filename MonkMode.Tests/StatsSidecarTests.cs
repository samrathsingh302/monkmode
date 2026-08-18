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

// MonkMode.Tests - the STATS SIDECAR (v1.1 S7b, pins P45-P48).
//
// WHAT THIS PINS, and why each pin exists:
//
//  1. MERGE ARITHMETIC ACROSS TWO FILES (P45/P48). The service and the notifier each own
//     ONE file so neither can lose the other's update; the display path is what puts them
//     back together. If the merge dropped a field, a whole category of counter (redirects,
//     say) would silently read zero for ever and nobody would notice - there is no other
//     consumer to disagree with it.
//
//  2. STREAKS (P47). Consecutive days, a gap, today-only, and the LENIENT anchor (a streak
//     is not broken merely because today is young). Streaks are the one number here a user
//     will care about being wrong.
//
//  3. THE 730-KEY PRUNE (P47). Without it the day-log grows for ever, and the file the
//     service rewrites every 10s grows with it.
//
//  4. CORRUPT / ABSENT / GARBAGE => ZEROS, NEVER A THROW. This is the load-bearing one.
//     The ONLY reason a counter is allowed to be incremented from inside the service's 10s
//     tick and the notifier's watch pass is that no input can make StatsSidecar throw. If
//     any assertion in this group ever fails, a hand-edited text file has been handed a
//     route into the enforcement tick.
//
//  5. SURVIVAL (P48). A retire, a teardown and an `unblock --force` all rewrite or delete
//     the CONFIG; none of them may touch the counters, because a streak history is user
//     data and the no-data-loss rule covers it. Simulated here at the level that matters:
//     the sidecar is a separate file that nothing in those paths names.
//
//  6. THREE-COPY PARITY. StatsSidecar.vb is byte-identical in the CLI, the service and the
//     notifier (the guardian has no use for it). The parity theory calls all three real
//     copies on the same inputs, in the spirit of CanonicalParityTests: a copy that drifted
//     would make the service write a shape the CLI cannot read back.
//
// FENCE: no test here touches %ProgramData%\MonkMode. Every file test uses a GUID temp path
// under the test bin directory, which Apply reaches through EnsureDirFor(path, ...) - the
// directory already exists, so nothing is created and no ACL is set anywhere.

using System.Globalization;

namespace MonkMode.Tests;

public class StatsSidecarTests
{
    private static string TempPath(string tag) =>
        Path.Combine(AppContext.BaseDirectory, $"statssidecar_{tag}_{Guid.NewGuid():N}.ini");

    // A delta with everything on it, through the CLI copy (the copies are byte-identical;
    // the parity theory at the bottom proves the other two agree).
    private static MonkMode.StatsSidecar.StatsData Delta(
        string slotId, long kills, long redirects, long armedSeconds, string dayKey) =>
        MonkMode.StatsSidecar.NewDelta(slotId, kills, redirects, armedSeconds, dayKey);

    // ---------------------------------------------------------------
    // 1. merge arithmetic across two files (P45/P48)
    // ---------------------------------------------------------------

    [Fact]
    public void TwoFiles_MergeFieldWise_PerSlot_Lifetime_AndDays()
    {
        string svc = TempPath("svc");
        string ntf = TempPath("ntf");
        try
        {
            // The service's file: kills for slot 3, and 30s of held time today.
            Assert.True(MonkMode.StatsSidecar.Apply(svc, Delta("3", 2, 0, 0, "2026-08-18"), false));
            Assert.True(MonkMode.StatsSidecar.Apply(svc, Delta("", 0, 0, 30, "2026-08-18"), false));
            // The notifier's file: redirects for the SAME slot and a different one.
            Assert.True(MonkMode.StatsSidecar.Apply(ntf, Delta("3", 0, 5, 0, "2026-08-18"), false));
            Assert.True(MonkMode.StatsSidecar.Apply(ntf, Delta("7", 0, 1, 0, "2026-08-18"), false));

            var merged = MonkMode.StatsSidecar.Merge(
                MonkMode.StatsSidecar.ReadFrom(svc), MonkMode.StatsSidecar.ReadFrom(ntf));

            Assert.Equal(2, merged.Lifetime.Kills);
            Assert.Equal(6, merged.Lifetime.Redirects);
            Assert.Equal(30, merged.Lifetime.ArmedSeconds);

            Assert.Equal(2, merged.PerSlot["3"].Kills);
            Assert.Equal(5, merged.PerSlot["3"].Redirects);
            Assert.Equal(1, merged.PerSlot["7"].Redirects);
            Assert.Equal(0, merged.PerSlot["7"].Kills);

            var today = MonkMode.StatsSidecar.TotalForDay(merged, "2026-08-18");
            Assert.Equal(30, today.ArmedSeconds);
            Assert.Equal(2, today.Kills);
            Assert.Equal(6, today.Redirects);
        }
        finally
        {
            File.Delete(svc);
            File.Delete(ntf);
        }
    }

    [Fact]
    public void Merge_WithNothing_IsIdentity_AndNeverThrows()
    {
        var a = Delta("1", 3, 4, 5, "2026-08-18");
        var m = MonkMode.StatsSidecar.Merge(a, null);
        Assert.Equal(3, m.Lifetime.Kills);
        Assert.Equal(4, m.Lifetime.Redirects);
        Assert.Equal(5, m.Lifetime.ArmedSeconds);
        // Both Nothing is a valid zero - the "one sidecar is missing" case is free.
        Assert.True(MonkMode.StatsSidecar.IsEmpty(MonkMode.StatsSidecar.Merge(null, null)));
    }

    [Fact]
    public void Merge_DoesNotMutateItsInputs()
    {
        var a = Delta("1", 1, 0, 0, "2026-08-18");
        var b = Delta("1", 1, 0, 0, "2026-08-18");
        MonkMode.StatsSidecar.Merge(a, b);
        Assert.Equal(1, a.Lifetime.Kills);
        Assert.Equal(1, b.Lifetime.Kills);
    }

    [Fact]
    public void NewDelta_BlankSlotId_StillCountsLifetimeAndDay_ButLabelsNoSlot()
    {
        var d = Delta("", 1, 0, 0, "2026-08-18");
        Assert.Equal(1, d.Lifetime.Kills);
        Assert.Empty(d.PerSlot);
        Assert.Equal(1, MonkMode.StatsSidecar.TotalForDay(d, "2026-08-18").Kills);
    }

    [Fact]
    public void Apply_AccumulatesAcrossCalls_RoundTrippingThroughDisk()
    {
        string p = TempPath("accum");
        try
        {
            for (int i = 0; i < 5; i++)
            {
                Assert.True(MonkMode.StatsSidecar.Apply(p, Delta("2", 1, 0, 10, "2026-08-18"), false));
            }
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.Equal(5, d.Lifetime.Kills);
            Assert.Equal(50, d.Lifetime.ArmedSeconds);
            Assert.Equal(5, d.PerSlot["2"].Kills);
            Assert.Equal(50, MonkMode.StatsSidecar.TotalForDay(d, "2026-08-18").ArmedSeconds);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Apply_EmptyDelta_WritesNothing_SoAnIdleMachineNeverChurnsTheDisk()
    {
        string p = TempPath("idle");
        Assert.False(MonkMode.StatsSidecar.Apply(p, MonkMode.StatsSidecar.NewDelta("", 0, 0, 0, ""), false));
        Assert.False(MonkMode.StatsSidecar.Apply(p, null, false));
        Assert.False(File.Exists(p));
    }

    // ---------------------------------------------------------------
    // 2. streaks (P47)
    // ---------------------------------------------------------------

    private static MonkMode.StatsSidecar.StatsData Days(params string[] focusDays)
    {
        MonkMode.StatsSidecar.StatsData d = new();
        foreach (string k in focusDays)
        {
            d = MonkMode.StatsSidecar.Merge(d, MonkMode.StatsSidecar.NewDelta("", 0, 0, 60, k));
        }
        return d;
    }

    [Fact]
    public void CurrentStreak_ConsecutiveDaysEndingToday()
    {
        var d = Days("2026-08-16", "2026-08-17", "2026-08-18");
        Assert.Equal(3, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
    }

    [Fact]
    public void CurrentStreak_StopsAtAGap()
    {
        // 14th is separated from the 16-18 run by an empty 15th.
        var d = Days("2026-08-14", "2026-08-16", "2026-08-17", "2026-08-18");
        Assert.Equal(3, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
    }

    [Fact]
    public void CurrentStreak_TodayOnly_IsOne()
    {
        Assert.Equal(1, MonkMode.StatsSidecar.CurrentStreak(Days("2026-08-18"), "2026-08-18"));
    }

    [Fact]
    public void CurrentStreak_LenientAnchor_TodayNotYetAFocusDay_CountsFromYesterday()
    {
        // The rule that stops every user seeing a 0 at 00:01: a streak breaks only once a
        // WHOLE day has passed with nothing blocked.
        var d = Days("2026-08-16", "2026-08-17");
        Assert.Equal(2, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
    }

    [Fact]
    public void CurrentStreak_TwoQuietDays_IsZero()
    {
        var d = Days("2026-08-15", "2026-08-16");
        Assert.Equal(0, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
    }

    [Fact]
    public void CurrentStreak_NoDataOrBadTodayKey_IsZero()
    {
        Assert.Equal(0, MonkMode.StatsSidecar.CurrentStreak(new MonkMode.StatsSidecar.StatsData(), "2026-08-18"));
        Assert.Equal(0, MonkMode.StatsSidecar.CurrentStreak(Days("2026-08-18"), "not-a-date"));
        Assert.Equal(0, MonkMode.StatsSidecar.CurrentStreak(null, "2026-08-18"));
    }

    [Fact]
    public void ZeroArmedSecondsDay_IsNotAFocusDay()
    {
        // A day with kills but no held time cannot happen in production (a kill implies a
        // held block), but the definition must be single-valued anyway: armedSeconds > 0.
        var d = MonkMode.StatsSidecar.NewDelta("1", 3, 0, 0, "2026-08-18");
        Assert.False(MonkMode.StatsSidecar.IsFocusDay(d, "2026-08-18"));
        Assert.Equal(0, MonkMode.StatsSidecar.FocusDayCount(d));
        Assert.Equal(0, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
    }

    [Fact]
    public void LongestStreak_FindsTheBestRunAnywhere_NotJustTheCurrentOne()
    {
        // A 4-day run in July, a gap, then a 2-day run ending today.
        var d = Days("2026-07-01", "2026-07-02", "2026-07-03", "2026-07-04",
                     "2026-08-17", "2026-08-18");
        Assert.Equal(4, MonkMode.StatsSidecar.LongestStreak(d));
        Assert.Equal(2, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
        Assert.Equal(6, MonkMode.StatsSidecar.FocusDayCount(d));
    }

    [Fact]
    public void LongestStreak_CrossesAMonthAndAYearBoundary()
    {
        Assert.Equal(3, MonkMode.StatsSidecar.LongestStreak(Days("2026-12-31", "2027-01-01", "2027-01-02")));
    }

    [Fact]
    public void LongestStreak_Empty_IsZero()
    {
        Assert.Equal(0, MonkMode.StatsSidecar.LongestStreak(new MonkMode.StatsSidecar.StatsData()));
        Assert.Equal(0, MonkMode.StatsSidecar.LongestStreak(null));
    }

    // ---------------------------------------------------------------
    // 3. the 730-key prune (P47)
    // ---------------------------------------------------------------

    [Fact]
    public void PruneDays_KeepsTheNewestMaxKeys_AndDropsTheOldest()
    {
        MonkMode.StatsSidecar.StatsData d = new();
        DateTime start = new(2020, 1, 1);
        for (int i = 0; i < MonkMode.StatsSidecar.MaxDayKeys + 40; i++)
        {
            string key = start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            d = MonkMode.StatsSidecar.Merge(d, MonkMode.StatsSidecar.NewDelta("", 0, 0, 10, key));
        }
        MonkMode.StatsSidecar.PruneDays(d, MonkMode.StatsSidecar.MaxDayKeys);

        Assert.Equal(MonkMode.StatsSidecar.MaxDayKeys, d.Days.Count);
        // The 40 oldest are gone; the newest survives.
        Assert.False(MonkMode.StatsSidecar.IsFocusDay(d, "2020-01-01"));
        Assert.True(MonkMode.StatsSidecar.IsFocusDay(
            d, start.AddDays(MonkMode.StatsSidecar.MaxDayKeys + 39).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        // Lifetime is NOT pruned - the total is the total, however old.
        Assert.Equal((MonkMode.StatsSidecar.MaxDayKeys + 40) * 10, d.Lifetime.ArmedSeconds);
    }

    [Fact]
    public void PruneDays_UnderTheCap_OrNonPositiveMax_IsANoOp()
    {
        var d = Days("2026-08-17", "2026-08-18");
        MonkMode.StatsSidecar.PruneDays(d, MonkMode.StatsSidecar.MaxDayKeys);
        Assert.Equal(2, d.Days.Count);
        // "Prune everything" is never what a caller means - obeying it would bin user data.
        MonkMode.StatsSidecar.PruneDays(d, 0);
        MonkMode.StatsSidecar.PruneDays(d, -5);
        Assert.Equal(2, d.Days.Count);
    }

    [Fact]
    public void Apply_PrunesOnWrite_SoTheFileIsBounded()
    {
        string p = TempPath("prune");
        try
        {
            MonkMode.StatsSidecar.StatsData big = new();
            DateTime start = new(2020, 1, 1);
            for (int i = 0; i < MonkMode.StatsSidecar.MaxDayKeys + 5; i++)
            {
                big = MonkMode.StatsSidecar.Merge(big, MonkMode.StatsSidecar.NewDelta(
                    "", 0, 0, 10, start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            Assert.True(MonkMode.StatsSidecar.Apply(p, big, false));
            Assert.Equal(MonkMode.StatsSidecar.MaxDayKeys, MonkMode.StatsSidecar.ReadFrom(p).Days.Count);
        }
        finally { File.Delete(p); }
    }

    // ---------------------------------------------------------------
    // 4. corrupt / absent / garbage => zeros, never a throw
    // ---------------------------------------------------------------

    [Fact]
    public void AbsentFile_ReadsZero_NeverThrows()
    {
        var d = MonkMode.StatsSidecar.ReadFrom(TempPath("never_written"));
        Assert.True(MonkMode.StatsSidecar.IsEmpty(d));
        Assert.Equal(0, d.Lifetime.Kills);
    }

    [Theory]
    // Not an ini at all.
    [InlineData("this is not an ini file at all\nnor is this line\n")]
    // Right shape, WRONG schema version: read as zeros rather than half-understood.
    [InlineData("[Meta]\nVersion=st99\n[Lifetime]\nKills=5\nRedirects=5\nArmedSeconds=5\n")]
    // No [Meta] at all.
    [InlineData("[Lifetime]\nKills=5\n")]
    // Empty file.
    [InlineData("")]
    // Just a BOM-ish pile of junk bytes as text.
    [InlineData("\0\0\0ÿþ???\n")]
    public void CorruptOrGarbageFile_ReadsZero_NeverThrows(string body)
    {
        string p = TempPath("garbage");
        try
        {
            File.WriteAllText(p, body);
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.True(MonkMode.StatsSidecar.IsEmpty(d));
        }
        finally { File.Delete(p); }
    }

    [Theory]
    // Non-numeric, negative, overflowing and blank counters all read as 0 - a counter that
    // can go backwards is a counter nobody can read, so a hand-edited "-999" is refused.
    [InlineData("banana")]
    [InlineData("-999999")]
    [InlineData("99999999999999999999999999")]
    [InlineData("")]
    [InlineData("3.5")]
    public void HostileCounterValues_ReadAsZero(string value)
    {
        string p = TempPath("hostile");
        try
        {
            File.WriteAllText(p,
                $"[Meta]\nVersion={MonkMode.StatsSidecar.SchemaVersion}\n[Lifetime]\nKills={value}\nRedirects={value}\nArmedSeconds={value}\n");
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.Equal(0, d.Lifetime.Kills);
            Assert.Equal(0, d.Lifetime.Redirects);
            Assert.Equal(0, d.Lifetime.ArmedSeconds);
        }
        finally { File.Delete(p); }
    }

    [Theory]
    // A malformed day VALUE is skipped entirely (wrong field count), while a well-formed one
    // with junk fields degrades to zeros. Neither throws, and neither invents a focus day.
    [InlineData("60")]
    [InlineData("60|1")]
    [InlineData("60|1|2|3")]
    [InlineData("")]
    [InlineData("|||")]
    public void MalformedDayValues_AreSkipped_NeverThrow(string value)
    {
        string p = TempPath("badday");
        try
        {
            File.WriteAllText(p,
                $"[Meta]\nVersion={MonkMode.StatsSidecar.SchemaVersion}\n[Days]\n2026-08-18={value}\n");
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.False(MonkMode.StatsSidecar.IsFocusDay(d, "2026-08-18"));
            Assert.Equal(0, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void GarbageDayKeys_AreCarriedButNeverBreakTheStreakWalk()
    {
        string p = TempPath("badkeys");
        try
        {
            File.WriteAllText(p,
                $"[Meta]\nVersion={MonkMode.StatsSidecar.SchemaVersion}\n[Days]\n" +
                "not-a-date=60|0|0\n2026-13-45=60|0|0\n2026-08-18=60|0|0\n");
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            // The real day still counts; the two unplaceable keys neither throw nor extend.
            Assert.Equal(1, MonkMode.StatsSidecar.CurrentStreak(d, "2026-08-18"));
            Assert.Equal(1, MonkMode.StatsSidecar.LongestStreak(d));
        }
        finally { File.Delete(p); }
    }

    // A well-formed sidecar body carrying real counters, padded with ini COMMENT lines
    // (IniFile.Load skips ';' lines) to exactly `totalBytes`. The counters are real so a
    // refusal can only be about SIZE, never about content.
    private static void WritePaddedSidecar(string path, long totalBytes)
    {
        string body =
            $"[Meta]\nVersion={MonkMode.StatsSidecar.SchemaVersion}\n" +
            "[Lifetime]\nKills=5\nRedirects=6\nArmedSeconds=70\n" +
            "[Days]\n2026-08-18=70|5|6\n";
        long padLen = totalBytes - body.Length;
        Assert.True(padLen >= 2, "the padded body must leave room for at least one comment line");
        // One ';' comment line: ';' + filler + '\n'.
        string pad = ";" + new string('x', (int)(padLen - 2)) + "\n";
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(body + pad));
        Assert.Equal(totalBytes, new FileInfo(path).Length);
    }

    [Fact]
    public void OversizeFile_ReadsZero_AndTheNextApplyHealsItSmall()
    {
        // S7b verifier P2. P49 grants BUILTIN\Users : Modify on %ProgramData%\MonkMode so
        // the NON-elevated notifier can record a redirect - which also means a user can
        // plant a giant stats-*.ini there. The notifier's 5s RefreshTray poll parses BOTH
        // files on the WinForms UI THREAD, the same thread the app-kill and URL-watch
        // timers dispatch on, and the service reads its own file every 10s tick. Without a
        // bound, a few hundred megabytes of text would starve exactly the layers the
        // notifier exists to run - a denial of service against enforcement, bought with a
        // text file. So: oversize is treated as corrupt, and it SELF-HEALS.
        string p = TempPath("oversize");
        try
        {
            WritePaddedSidecar(p, MonkMode.StatsSidecar.MaxFileBytes + 1);

            // 1. It reads as zeros - not as its (perfectly valid) counters, and not as a throw.
            var refused = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.True(MonkMode.StatsSidecar.IsEmpty(refused));

            // 2. The next write heals it: a small, well-formed file carrying the new delta.
            Assert.True(MonkMode.StatsSidecar.Apply(p, Delta("3", 1, 0, 10, "2026-08-18"), false));
            Assert.True(new FileInfo(p).Length < MonkMode.StatsSidecar.MaxFileBytes);
            var healed = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.Equal(1, healed.Lifetime.Kills);
            Assert.Equal(10, healed.Lifetime.ArmedSeconds);
            Assert.Equal(1, healed.PerSlot["3"].Kills);
            // The planted file's numbers are gone, which is the honest outcome for a file
            // nothing can verify - and it was the user's own to delete anyway.
            Assert.Equal(0, healed.Lifetime.Redirects);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void FileAtExactlyTheCeiling_IsStillParsed_SoTheBoundIsGreaterThanNotAtLeast()
    {
        string p = TempPath("ceiling");
        try
        {
            WritePaddedSidecar(p, MonkMode.StatsSidecar.MaxFileBytes);
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.Equal(5, d.Lifetime.Kills);
            Assert.Equal(6, d.Lifetime.Redirects);
            Assert.Equal(70, d.Lifetime.ArmedSeconds);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void AllThreeCopies_RefuseTheSameOversizeFile()
    {
        // The bound has to hold in the SERVICE (10s tick) and the NOTIFIER (UI thread)
        // copies, which are the two that matter for the attack; the CLI is only display.
        string p = TempPath("oversize_parity");
        try
        {
            WritePaddedSidecar(p, MonkMode.StatsSidecar.MaxFileBytes + 1);
            Assert.True(MonkMode.StatsSidecar.IsEmpty(MonkMode.StatsSidecar.ReadFrom(p)));
            Assert.True(monkmode.StatsSidecar.IsEmpty(monkmode.StatsSidecar.ReadFrom(p)));
            Assert.True(mm_notify.StatsSidecar.IsEmpty(mm_notify.StatsSidecar.ReadFrom(p)));
            Assert.Equal(MonkMode.StatsSidecar.MaxFileBytes, monkmode.StatsSidecar.MaxFileBytes);
            Assert.Equal(MonkMode.StatsSidecar.MaxFileBytes, mm_notify.StatsSidecar.MaxFileBytes);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ReadFrom_NullOrBlankPath_ReadsZero_NeverThrows()
    {
        Assert.True(MonkMode.StatsSidecar.IsEmpty(MonkMode.StatsSidecar.ReadFrom(null)));
        Assert.True(MonkMode.StatsSidecar.IsEmpty(MonkMode.StatsSidecar.ReadFrom("")));
    }

    [Fact]
    public void Apply_ToAnUnwritablePath_ReturnsFalse_NeverThrows()
    {
        // A path whose directory cannot be created: the service tick and the notifier beat
        // must survive this without noticing.
        string impossible = Path.Combine(AppContext.BaseDirectory, "no\0such", "x.ini");
        Assert.False(MonkMode.StatsSidecar.Apply(impossible, Delta("1", 1, 0, 0, "2026-08-18"), false));
        Assert.False(MonkMode.StatsSidecar.Apply(null, Delta("1", 1, 0, 0, "2026-08-18"), false));
        Assert.False(MonkMode.StatsSidecar.Apply("", Delta("1", 1, 0, 0, "2026-08-18"), false));
    }

    [Fact]
    public void Apply_OverAGarbageFile_RecoversByOverwriting_AndKeepsTheNewDelta()
    {
        // A hostile edit costs the history, which is the honest outcome for an unverifiable
        // file - but it must never cost an exception, and the next write must work.
        string p = TempPath("recover");
        try
        {
            File.WriteAllText(p, "%%% not an ini %%%");
            Assert.True(MonkMode.StatsSidecar.Apply(p, Delta("1", 4, 0, 20, "2026-08-18"), false));
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.Equal(4, d.Lifetime.Kills);
            Assert.Equal(20, d.Lifetime.ArmedSeconds);
        }
        finally { File.Delete(p); }
    }

    // ---------------------------------------------------------------
    // 5. survival across retire / teardown / force (P48)
    // ---------------------------------------------------------------

    [Fact]
    public void Counters_SurviveAConfigRewrite_BecauseTheyAreNotInTheConfig()
    {
        // The point of the pin: the sidecar path is derived from %ProgramData%, and the
        // config path from the app directory. A retire, a TeardownAll and an
        // `unblock --force` all rewrite or delete files in the APP directory (the config,
        // its backup, the hosts snapshot). None of them can name a file under
        // CommonApplicationData, so none of them can take a streak with it.
        string appDirFile = Path.Combine(AppContext.BaseDirectory, "monkmode_settings.ini");
        Assert.NotEqual(
            Path.GetDirectoryName(appDirFile),
            Path.GetDirectoryName(MonkMode.StatsSidecar.ServiceStatsPath()));
        Assert.NotEqual(
            Path.GetDirectoryName(appDirFile),
            Path.GetDirectoryName(MonkMode.StatsSidecar.NotifyStatsPath()));
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            MonkMode.StatsSidecar.ServiceStatsPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Counters_SurviveTheDisappearanceOfEveryOtherSidecar()
    {
        // Simulates a teardown that removed one party's file (or a machine where the
        // notifier never ran): the survivor's numbers are still read in full.
        string svc = TempPath("survive_svc");
        string gone = TempPath("survive_gone");
        try
        {
            Assert.True(MonkMode.StatsSidecar.Apply(svc, Delta("1", 9, 0, 900, "2026-08-18"), false));
            var merged = MonkMode.StatsSidecar.Merge(
                MonkMode.StatsSidecar.ReadFrom(svc), MonkMode.StatsSidecar.ReadFrom(gone));
            Assert.Equal(9, merged.Lifetime.Kills);
            Assert.Equal(900, merged.Lifetime.ArmedSeconds);
            Assert.Equal(1, MonkMode.StatsSidecar.CurrentStreak(merged, "2026-08-18"));
        }
        finally { File.Delete(svc); }
    }

    [Fact]
    public void RetiredSlotIdKeepsItsHistory_AndANewSlotAtTheSamePositionDoesNotInheritIt()
    {
        // Sections are keyed on the slot ID, never on the position: a retire COMPACTS the
        // config's [SlotN] sections, so position 1 is a different block tomorrow.
        string p = TempPath("ids");
        try
        {
            MonkMode.StatsSidecar.Apply(p, Delta("4", 7, 0, 10, "2026-08-18"), false);
            MonkMode.StatsSidecar.Apply(p, Delta("9", 1, 0, 10, "2026-08-18"), false);
            var d = MonkMode.StatsSidecar.ReadFrom(p);
            Assert.Equal(7, d.PerSlot["4"].Kills);
            Assert.Equal(1, d.PerSlot["9"].Kills);
            Assert.Equal(8, d.Lifetime.Kills);
        }
        finally { File.Delete(p); }
    }

    // ---------------------------------------------------------------
    // 6. serialisation shape + three-copy parity
    // ---------------------------------------------------------------

    [Fact]
    public void WrittenFile_CarriesTheP46Schema()
    {
        string p = TempPath("shape");
        try
        {
            MonkMode.StatsSidecar.Apply(p, Delta("5", 1, 2, 30, "2026-08-18"), false);
            string text = File.ReadAllText(p);
            Assert.Contains("[Meta]", text);
            Assert.Contains("Version=" + MonkMode.StatsSidecar.SchemaVersion, text);
            Assert.Contains("[Lifetime]", text);
            Assert.Contains("[Slot.5]", text);
            Assert.Contains("[Days]", text);
            Assert.Contains("2026-08-18=30|1|2", text);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void FromIni_OfToIni_RoundTripsEveryField()
    {
        var src = MonkMode.StatsSidecar.Merge(
            Delta("1", 3, 0, 60, "2026-08-17"), Delta("2", 0, 4, 90, "2026-08-18"));
        var back = MonkMode.StatsSidecar.FromIni(MonkMode.StatsSidecar.ToIni(src));
        Assert.Equal(src.Lifetime.Kills, back.Lifetime.Kills);
        Assert.Equal(src.Lifetime.Redirects, back.Lifetime.Redirects);
        Assert.Equal(src.Lifetime.ArmedSeconds, back.Lifetime.ArmedSeconds);
        Assert.Equal(3, back.PerSlot["1"].Kills);
        Assert.Equal(4, back.PerSlot["2"].Redirects);
        Assert.Equal(60, MonkMode.StatsSidecar.TotalForDay(back, "2026-08-17").ArmedSeconds);
        Assert.Equal(90, MonkMode.StatsSidecar.TotalForDay(back, "2026-08-18").ArmedSeconds);
    }

    [Fact]
    public void FromIni_OfNothing_IsZero()
    {
        Assert.True(MonkMode.StatsSidecar.IsEmpty(MonkMode.StatsSidecar.FromIni(null)));
    }

    [Theory]
    [InlineData("2026-08-16,2026-08-17,2026-08-18", "2026-08-18", 3, 3)]
    [InlineData("2026-08-14,2026-08-16,2026-08-17,2026-08-18", "2026-08-18", 3, 3)]
    [InlineData("2026-08-18", "2026-08-18", 1, 1)]
    [InlineData("2026-08-16,2026-08-17", "2026-08-18", 2, 2)]
    [InlineData("2026-08-15", "2026-08-18", 0, 1)]
    public void AllThreeCopies_AgreeOnStreaks(string focusDays, string today, int expectedCurrent, int expectedLongest)
    {
        string[] keys = focusDays.Split(',');

        MonkMode.StatsSidecar.StatsData cli = new();
        monkmode.StatsSidecar.StatsData srv = new();
        mm_notify.StatsSidecar.StatsData ntf = new();
        foreach (string k in keys)
        {
            cli = MonkMode.StatsSidecar.Merge(cli, MonkMode.StatsSidecar.NewDelta("", 0, 0, 60, k));
            srv = monkmode.StatsSidecar.Merge(srv, monkmode.StatsSidecar.NewDelta("", 0, 0, 60, k));
            ntf = mm_notify.StatsSidecar.Merge(ntf, mm_notify.StatsSidecar.NewDelta("", 0, 0, 60, k));
        }

        Assert.Equal(expectedCurrent, MonkMode.StatsSidecar.CurrentStreak(cli, today));
        Assert.Equal(expectedCurrent, monkmode.StatsSidecar.CurrentStreak(srv, today));
        Assert.Equal(expectedCurrent, mm_notify.StatsSidecar.CurrentStreak(ntf, today));

        Assert.Equal(expectedLongest, MonkMode.StatsSidecar.LongestStreak(cli));
        Assert.Equal(expectedLongest, monkmode.StatsSidecar.LongestStreak(srv));
        Assert.Equal(expectedLongest, mm_notify.StatsSidecar.LongestStreak(ntf));
    }

    [Fact]
    public void AllThreeCopies_ReadEachOthersFiles_AndAgreeOnEveryConstant()
    {
        // The real cross-assembly risk: the service WRITES and the CLI READS. A drifted copy
        // would make one of them see zeros where the other wrote numbers.
        string p = TempPath("parity");
        try
        {
            Assert.True(monkmode.StatsSidecar.Apply(p, monkmode.StatsSidecar.NewDelta("6", 2, 3, 40, "2026-08-18"), false));

            var byCli = MonkMode.StatsSidecar.ReadFrom(p);
            var byNtf = mm_notify.StatsSidecar.ReadFrom(p);
            Assert.Equal(2, byCli.Lifetime.Kills);
            Assert.Equal(3, byCli.Lifetime.Redirects);
            Assert.Equal(40, byCli.Lifetime.ArmedSeconds);
            Assert.Equal(byCli.Lifetime.Kills, byNtf.Lifetime.Kills);
            Assert.Equal(2, byNtf.PerSlot["6"].Kills);

            // And the notifier can append to the same shape without the CLI losing anything.
            Assert.True(mm_notify.StatsSidecar.Apply(p, mm_notify.StatsSidecar.NewDelta("6", 0, 1, 0, "2026-08-18"), false));
            Assert.Equal(4, MonkMode.StatsSidecar.ReadFrom(p).Lifetime.Redirects);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void AllThreeCopies_AgreeOnEveryConstantAndPath()
    {
        Assert.Equal(MonkMode.StatsSidecar.SchemaVersion, monkmode.StatsSidecar.SchemaVersion);
        Assert.Equal(MonkMode.StatsSidecar.SchemaVersion, mm_notify.StatsSidecar.SchemaVersion);
        Assert.Equal(MonkMode.StatsSidecar.MaxDayKeys, monkmode.StatsSidecar.MaxDayKeys);
        Assert.Equal(MonkMode.StatsSidecar.MaxDayKeys, mm_notify.StatsSidecar.MaxDayKeys);
        Assert.Equal(MonkMode.StatsSidecar.ServiceStatsPath(), monkmode.StatsSidecar.ServiceStatsPath());
        Assert.Equal(MonkMode.StatsSidecar.NotifyStatsPath(), mm_notify.StatsSidecar.NotifyStatsPath());
        Assert.Equal(MonkMode.StatsSidecar.ServiceFileName, monkmode.StatsSidecar.ServiceFileName);
        Assert.Equal(MonkMode.StatsSidecar.NotifyFileName, mm_notify.StatsSidecar.NotifyFileName);
    }

    [Fact]
    public void ThreeCopies_AreByteIdenticalOnDisk()
    {
        // The same 4-copy discipline ConfigIntegrity / IniFile / Simple3Des are held to.
        // Walk up from the test bin directory to the repo root.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MonkMode.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        string[] copies =
        {
            Path.Combine(dir!.FullName, "MonkMode", "StatsSidecar.vb"),
            Path.Combine(dir.FullName, "MonkMode_srv", "MonkMode_srv", "StatsSidecar.vb"),
            Path.Combine(dir.FullName, "MM_notify", "MM_notify", "StatsSidecar.vb"),
        };
        byte[] first = File.ReadAllBytes(copies[0]);
        foreach (string c in copies)
        {
            Assert.True(first.SequenceEqual(File.ReadAllBytes(c)), $"StatsSidecar.vb copy drifted: {c}");
        }
    }

    // ---------------------------------------------------------------
    // 7. the enforcement fence itself
    // ---------------------------------------------------------------

    [Fact]
    public void DayKeyFor_IsTheOrdinallySortableCalendarKey()
    {
        Assert.Equal("2026-08-18", MonkMode.StatsSidecar.DayKeyFor(new DateTime(2026, 8, 18, 23, 59, 59)));
        Assert.Equal("2026-01-02", MonkMode.StatsSidecar.DayKeyFor(new DateTime(2026, 1, 2)));
        // Ordinal sort == calendar order is what the pruner and the streak walk rest on.
        Assert.True(string.CompareOrdinal(
            MonkMode.StatsSidecar.DayKeyFor(new DateTime(2026, 1, 2)),
            MonkMode.StatsSidecar.DayKeyFor(new DateTime(2026, 1, 10))) < 0);
    }

    [Fact]
    public void SidecarPaths_AreUnderProgramData_NotBesideTheExecutables()
    {
        // P45/P49: the notifier is NOT elevated, so the counters cannot live in the
        // admin-write-only install folder.
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.Equal(Path.Combine(programData, "MonkMode", "stats-service.ini"),
                     MonkMode.StatsSidecar.ServiceStatsPath());
        Assert.Equal(Path.Combine(programData, "MonkMode", "stats-notify.ini"),
                     MonkMode.StatsSidecar.NotifyStatsPath());
        // Two SEPARATE files: one writer each, so no update can be lost (P45).
        Assert.NotEqual(MonkMode.StatsSidecar.ServiceStatsPath(), MonkMode.StatsSidecar.NotifyStatsPath());
    }
}
