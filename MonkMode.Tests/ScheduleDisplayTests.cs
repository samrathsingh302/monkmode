// MonkMode.Tests - C5b schedules, sub-slice (c4): the READ-ONLY `schedule --show` / `--validate`
// display (+ the `status` schedule line). c4 renders the stored compact v1 [Schedule] Spec back into
// a human form and dry-runs the validator - it touches NO enforcement/arm path (display + dry-run
// only), so the run sheet marks it Light / no verifier. These tests pin the c4 surface:
//   - the compact->human render (DayMaskToHuman run-compression, CompactWindowToHuman, the full
//     DescribeScheduleSpec split) is correct and fail-soft, and ROUND-TRIPS the c3 serialiser (so
//     `schedule --show` shows exactly the windows/sites the user armed);
//   - `--validate` reuses the SAME Blocker.TryBuildScheduleSpec the arm path uses - the exhaustive
//     accept/reject contract is already pinned by TryBuildScheduleSpecTests, so here we prove the
//     serialise->describe round-trip + the design-gotcha-#4 --sites requirement;
//   - the read-only surface WRITES NOTHING: reading + rendering + validating never mutate the ini
//     (byte-identical, not even re-touched), and validating with no config on disk creates none.
//
// Pure render tests need no ini; the "writes nothing" tests share the test-bin ini with the other
// CliIniWriters (fence: only the test-bin ini/backup is ever touched, never a live block/service).

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonkMode.Tests;

// Pure compact->human rendering (no disk): the reverse of the c3 serialiser used by --show/status.
public class ScheduleDisplayRenderTests
{
    [Theory]
    [InlineData("12345", "Mon-Fri")]                 // a run of 5 -> range
    [InlineData("1234567", "Mon-Sun")]               // the full week
    [InlineData("67", "Sat,Sun")]                    // a run of 2 -> listed, NOT ranged
    [InlineData("345", "Wed-Fri")]                   // the minimal range (exactly 3)
    [InlineData("1235", "Mon-Wed,Fri")]              // a range + a trailing single
    [InlineData("1", "Mon")]
    [InlineData("246", "Tue,Thu,Sat")]               // no consecutive run -> each day listed
    [InlineData("57", "Fri,Sun")]
    [InlineData("21", "Mon,Tue")]                    // out-of-order input -> sorted (run of 2 listed)
    [InlineData("11", "Mon")]                        // duplicate char -> deduped
    public void DayMaskToHuman_CompressesRunsOfThreeOrMore(string mask, string expected)
        => Assert.Equal(expected, MonkMode.Blocker.DayMaskToHuman(mask));

    [Fact]
    public void DayMaskToHuman_UnrecognisedMask_RendersVerbatim_NoThrow()
    {
        Assert.Equal("", MonkMode.Blocker.DayMaskToHuman(""));
        Assert.Equal("zzz", MonkMode.Blocker.DayMaskToHuman("zzz"));   // fail-soft: nothing recognised
        Assert.Equal("", MonkMode.Blocker.DayMaskToHuman(null!));      // never throws on null
    }

    [Theory]
    [InlineData("12345:0900-1700", "Mon-Fri 09:00-17:00")]
    [InlineData("67:1000-1400", "Sat,Sun 10:00-14:00")]
    [InlineData("2:0905-0930", "Tue 09:05-09:30")]
    [InlineData("1234567:0000-2359", "Mon-Sun 00:00-23:59")]
    public void CompactWindowToHuman_RendersDaysAndTimes(string compact, string expected)
        => Assert.Equal(expected, MonkMode.Blocker.CompactWindowToHuman(compact));

    [Theory]
    [InlineData("garbage", "garbage")]        // no colon -> verbatim
    [InlineData("12345:0900", "12345:0900")]  // no dash in the time part -> verbatim
    [InlineData("  x  ", "x")]                 // trimmed-verbatim
    [InlineData("", "")]
    public void CompactWindowToHuman_Unparseable_RendersTrimmedVerbatim_NoThrow(string compact, string expected)
        => Assert.Equal(expected, MonkMode.Blocker.CompactWindowToHuman(compact));

    [Fact]
    public void DescribeScheduleSpec_FullExample_SplitsWindowsSitesApps()
    {
        List<string> w = null!, s = null!, a = null!;
        MonkMode.Blocker.DescribeScheduleSpec(
            "v1;12345:0900-1700,67:1000-1400;sites=reddit.com|news.ycombinator.com;apps=chrome.exe",
            ref w, ref s, ref a);
        Assert.Equal(new[] { "Mon-Fri 09:00-17:00", "Sat,Sun 10:00-14:00" }, w.ToArray());
        Assert.Equal(new[] { "reddit.com", "news.ycombinator.com" }, s.ToArray());
        Assert.Equal(new[] { "chrome.exe" }, a.ToArray());
    }

    [Fact]
    public void DescribeScheduleSpec_NoApps_YieldsEmptyAppList()
    {
        List<string> w = null!, s = null!, a = null!;
        MonkMode.Blocker.DescribeScheduleSpec("v1;1:0900-1700;sites=x.com;apps=", ref w, ref s, ref a);
        Assert.Equal(new[] { "Mon 09:00-17:00" }, w.ToArray());
        Assert.Equal(new[] { "x.com" }, s.ToArray());
        Assert.Empty(a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage-not-a-spec")]
    [InlineData("v1;;sites=;apps=")]
    public void DescribeScheduleSpec_EmptyOrGarbage_NeverThrows_AlwaysInitialisesTheLists(string spec)
    {
        List<string> w = null!, s = null!, a = null!;
        MonkMode.Blocker.DescribeScheduleSpec(spec!, ref w, ref s, ref a);
        Assert.NotNull(w); Assert.NotNull(s); Assert.NotNull(a);   // three outs always initialised, never null
    }

    // The load-bearing round-trip: whatever the c3 serialiser (TryBuildScheduleSpec) emits, --show
    // renders the SAME windows/sites back - so `schedule --show` shows exactly what was armed. Inputs
    // are chosen in canonical form (runs >=3 as ranges, runs <3 listed) so they round-trip exactly.
    [Theory]
    [InlineData("Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00", new[] { "Mon-Fri 09:00-17:00", "Sat,Sun 10:00-14:00" })]
    [InlineData("Wed 09:00-09:30", new[] { "Wed 09:00-09:30" })]
    [InlineData("Mon-Wed,Fri 08:00-12:00", new[] { "Mon-Wed,Fri 08:00-12:00" })]
    [InlineData("Sat,Sun 06:00-23:59", new[] { "Sat,Sun 06:00-23:59" })]
    public void Serialise_ThenDescribe_RoundTripsTheHumanWindows(string windowsArg, string[] expectedWindows)
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(
            windowsArg, new[] { "reddit.com", "x.com" }, System.Array.Empty<string>(), ref spec, ref err), err);
        List<string> w = null!, s = null!, a = null!;
        MonkMode.Blocker.DescribeScheduleSpec(spec, ref w, ref s, ref a);
        Assert.Equal(expectedWindows, w.ToArray());
        Assert.Equal(new[] { "reddit.com", "x.com" }, s.ToArray());   // sites survive the round-trip too
    }
}

// The c4 read-only surface must never mutate disk. Shares the test-bin ini with the other
// CliIniWriters so the arm (WriteScheduleConfig, used only to set up a stored Spec to read back) is
// serialised against them - the fence is honoured (only the test-bin ini/backup is touched).
[Collection("CliIniWriters")]
public class ScheduleDisplayReadOnlyTests
{
    private static string BuildSpec(string windows, string[] sites)
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(windows, sites, System.Array.Empty<string>(), ref spec, ref err), err);
        return spec;
    }

    private static void Wipe(string ini, string backup)
    {
        try { if (File.Exists(ini)) File.Delete(ini); } catch { /* best-effort */ }
        try { if (File.Exists(backup)) File.Delete(backup); } catch { /* best-effort */ }
    }

    [Fact]
    public void ArmedScheduleSpec_ReturnsTheStoredSpec_AndDescribeRendersIt()
    {
        var ini = MonkMode.Blocker.IniPath();
        var backup = MonkMode.Blocker.IniBackupPath();
        Wipe(ini, backup);
        try
        {
            var spec = BuildSpec("Mon-Fri 09:00-17:00", new[] { "reddit.com" });
            MonkMode.Blocker.WriteScheduleConfig(spec);

            // --show reads exactly the stored Spec ...
            Assert.Equal(spec, MonkMode.Blocker.ArmedScheduleSpec());
            // ... and renders it correctly (the end-to-end "--show reads a stored config and shows it").
            List<string> w = null!, s = null!, a = null!;
            MonkMode.Blocker.DescribeScheduleSpec(MonkMode.Blocker.ArmedScheduleSpec(), ref w, ref s, ref a);
            Assert.Equal(new[] { "Mon-Fri 09:00-17:00" }, w.ToArray());
            Assert.Equal(new[] { "reddit.com" }, s.ToArray());
        }
        finally { Wipe(ini, backup); }
    }

    [Fact]
    public void ArmedScheduleSpec_NoConfig_ReturnsEmpty_NeverThrows()
    {
        var ini = MonkMode.Blocker.IniPath();
        var backup = MonkMode.Blocker.IniBackupPath();
        Wipe(ini, backup);
        try
        {
            Assert.Equal("", MonkMode.Blocker.ArmedScheduleSpec());   // no config on disk -> "" (read-only, safe)
            Assert.False(File.Exists(ini));                           // and reading created nothing
        }
        finally { Wipe(ini, backup); }
    }

    [Fact]
    public void ReadOnlyPaths_WriteNothing_IniAndBackupByteIdenticalAfterShowAndValidate()
    {
        var ini = MonkMode.Blocker.IniPath();
        var backup = MonkMode.Blocker.IniBackupPath();
        Wipe(ini, backup);
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Fri 09:00-17:00", new[] { "reddit.com" }));
            var iniBefore = File.ReadAllBytes(ini);
            var backupBefore = File.ReadAllBytes(backup);
            var iniStampBefore = File.GetLastWriteTimeUtc(ini);

            // Exercise the whole c4 read-only surface repeatedly: --show reads + renders; --validate
            // builds a spec. None of it may touch disk.
            for (int i = 0; i < 3; i++)
            {
                _ = MonkMode.Blocker.ScheduleIsArmed();
                _ = MonkMode.Blocker.ArmedScheduleSpec();
                List<string> w = null!, s = null!, a = null!;
                MonkMode.Blocker.DescribeScheduleSpec(MonkMode.Blocker.ArmedScheduleSpec(), ref w, ref s, ref a);
                string spec = "", err = "";
                MonkMode.Blocker.TryBuildScheduleSpec("Tue 10:00-11:00", new[] { "other.com" }, System.Array.Empty<string>(), ref spec, ref err);
            }

            Assert.Equal(iniBefore, File.ReadAllBytes(ini));                 // ini byte-identical
            Assert.Equal(backupBefore, File.ReadAllBytes(backup));          // backup byte-identical
            Assert.Equal(iniStampBefore, File.GetLastWriteTimeUtc(ini));    // not even re-touched
        }
        finally { Wipe(ini, backup); }
    }

    [Fact]
    public void Validate_BuildingBlock_WritesNothing_WhetherValidOrRejected()
    {
        var ini = MonkMode.Blocker.IniPath();
        var backup = MonkMode.Blocker.IniBackupPath();
        Wipe(ini, backup);
        try
        {
            // A VALID --validate: the builder succeeds but arms nothing (writes no ini/backup).
            string spec = "", err = "";
            Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(
                "Mon-Fri 09:00-17:00", new[] { "x.com" }, System.Array.Empty<string>(), ref spec, ref err), err);
            Assert.False(File.Exists(ini));
            Assert.False(File.Exists(backup));

            // A REJECTED --validate (design gotcha #4: the builder needs >=1 site, so --validate takes
            // --sites too) likewise writes nothing and surfaces the exact grammar error.
            Assert.False(MonkMode.Blocker.TryBuildScheduleSpec(
                "Mon-Fri 09:00-17:00", System.Array.Empty<string>(), System.Array.Empty<string>(), ref spec, ref err));
            Assert.Contains("site", err, System.StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(ini));
            Assert.False(File.Exists(backup));
        }
        finally { Wipe(ini, backup); }
    }
}
