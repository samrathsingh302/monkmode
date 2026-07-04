// MonkMode.Tests - C5b schedules, sub-slice (c3): the CLI `schedule` front-end.
//
// c3 is the FIRST path that creates a schedule-only block (design C5c §6). The CLI validates the
// human --windows/--sites/--apps args, serialises them to the compact v1 [Schedule] Spec grammar
// the service parser (Service1.ParseSchedule) already accepts, and arms a MAC-covered schedule-
// only config: [Schedule] Spec + the past-[Time] Until SENTINEL (ScheduleOnlyExpiredUntil) +
// ActiveUntil absent, then installs/starts the service so windows are evaluated. The arm MIRRORS
// AppendAddToHosts (edit-in-place) / WriteConfig (fresh scaffold), NOT DoBlock's hosts write - the
// CLI does NOT write monkmode_hosts.block (the service creates it on window-open, c1).
//
// These tests pin the CLI half against the FROZEN c1/c2 contract:
//   - the sentinel + grammar-version consts are CLI<->service parity copies (a drift would make
//     BlockHasExpired misread the sentinel or the service reject the Spec);
//   - TryBuildScheduleSpec validates + serialises so the result ALWAYS round-trips through the real
//     Service1.ParseSchedule to >=1 window (the "never stamp a garbage Spec" fence, verifier P3#1),
//     and a malformed/empty window/day/time or an empty site list is rejected with NO Spec;
//   - WriteScheduleConfig writes a MAC-valid schedule-only config the REAL service gates read as
//     armed-idle (OnStart HOLDS it, the c2 §4C trap fix), a re-arm preserves the monotonic frame
//     (never shortens an open window), and --clear re-stamps a VALID cleared config (so the service
//     can Lift it, not freeze);
//   - the notifier's ScheduleArmed gate suppresses the manual-expiry toast while a schedule is armed
//     (design §6.4), and a manual block (empty Spec) toasts byte-identically to pre-c3.
//
// The DoSchedule verb + SD-c1 refusals do live service I/O (install/start) - the smoke-only seam
// (fence: unit tests never arm a live block); their load-bearing predicates (ScheduleIsArmed,
// TryBuildScheduleSpec, BlockIsActive) are unit-tested here, the wiring is verifier + CV smoke.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MonkMode.Tests;

// CLI<->service parity: the two consts the CLI writes/uses must equal the service's.
public class ScheduleCliParityTests
{
    [Fact]
    public void ScheduleOnlyExpiredUntil_CliMatchesService()
    {
        // The CLI writes this verbatim as [Time] Until for a schedule-only block; the service parses
        // it generically via BlockHasExpired. A drift would make the sentinel read NOT expired ->
        // BlockHeld would latch forever (the P1 hole c2 closed) or the block never tear down.
        Assert.Equal(monkmode.Service1.ScheduleOnlyExpiredUntil, MonkMode.Blocker.ScheduleOnlyExpiredUntil);
    }

    [Fact]
    public void ScheduleSpecGrammarVersion_CliMatchesService()
    {
        // The Spec the CLI emits leads with this tag; the service parser rejects any other tag
        // (inert). A drift would make every CLI-armed schedule parse to zero windows (never enforce).
        Assert.Equal(monkmode.Service1.ScheduleSpecGrammarVersion, MonkMode.Blocker.ScheduleSpecGrammarVersion);
    }
}

// TryBuildScheduleSpec: human args -> compact v1 Spec, validated + round-tripping through the REAL
// service parser. A validated result ALWAYS parses back to >=1 window; anything malformed is
// rejected with NO Spec (never stamp a garbage Spec).
public class TryBuildScheduleSpecTests
{
    private static bool Build(string windows, string[] sites, string[] apps, out string spec, out string err)
    {
        spec = ""; err = "";
        return MonkMode.Blocker.TryBuildScheduleSpec(windows, sites, apps, ref spec, ref err);
    }

    [Fact]
    public void FullExample_SerialisesToTheCanonicalCompactSpec_AndRoundTrips()
    {
        var ok = Build("Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00",
            new[] { "reddit.com", "news.ycombinator.com" }, new[] { "chrome.exe" }, out var spec, out var err);
        Assert.True(ok, err);
        Assert.Equal("v1;12345:0900-1700,67:1000-1400;sites=reddit.com|news.ycombinator.com;apps=chrome.exe", spec);
        // Round-trip through the service parser: 2 windows with the intended masks/times + lists.
        var p = monkmode.Service1.ParseSchedule(spec);
        Assert.Equal(2, p.Windows.Count);
        Assert.Equal(31, p.Windows[0].DayMask); Assert.Equal(540, p.Windows[0].OpenMinutes); Assert.Equal(1020, p.Windows[0].CloseMinutes);
        Assert.Equal(96, p.Windows[1].DayMask); Assert.Equal(600, p.Windows[1].OpenMinutes); Assert.Equal(840, p.Windows[1].CloseMinutes);
        Assert.Equal(new[] { "reddit.com", "news.ycombinator.com" }, p.Sites.ToArray());
        Assert.Equal(new[] { "chrome.exe" }, p.Apps.ToArray());
    }

    [Theory]
    [InlineData("Wed 09:00-09:30", "v1;3:0900-0930;sites=x.com;apps=")]
    [InlineData("Mon-Wed,Fri 08:00-12:00", "v1;1235:0800-1200;sites=x.com;apps=")]         // range + list
    [InlineData("mon,MON,Monday 08:00-12:00", "v1;1:0800-1200;sites=x.com;apps=")]         // case + dedupe + long name
    [InlineData("Mon-Sun 00:00-23:59", "v1;1234567:0000-2359;sites=x.com;apps=")]          // full week, min/max times
    [InlineData("Tue 9:05-9:30", "v1;2:0905-0930;sites=x.com;apps=")]                      // 1-digit hour -> zero-padded
    [InlineData("Sat, Sun 10:00-14:00", "v1;67:1000-1400;sites=x.com;apps=")]              // space after the comma
    public void DayAndTimeForms_SerialiseAndRoundTripToAtLeastOneWindow(string windows, string expectedSpec)
    {
        var ok = Build(windows, new[] { "x.com" }, System.Array.Empty<string>(), out var spec, out var err);
        Assert.True(ok, err);
        Assert.Equal(expectedSpec, spec);
        Assert.True(monkmode.Service1.ParseSchedule(spec).Windows.Count >= 1);   // never a garbage Spec
    }

    [Fact]
    public void SiteNormalisation_MatchesTheHostsForm_LowercasesStripsSchemeAndPath()
    {
        var ok = Build("Mon 09:00-17:00", new[] { "HTTPS://Reddit.com/r/all", "  spaced.com  " }, System.Array.Empty<string>(), out var spec, out var err);
        Assert.True(ok, err);
        Assert.Equal("v1;1:0900-1700;sites=reddit.com|spaced.com;apps=", spec);
    }

    [Fact]
    public void AppsGetExeSuffix_AndAreOptional()
    {
        var ok = Build("Mon 09:00-17:00", new[] { "x.com" }, new[] { "chrome", "discord.exe" }, out var spec, out var err);
        Assert.True(ok, err);
        Assert.Equal("v1;1:0900-1700;sites=x.com;apps=chrome.exe|discord.exe", spec);
    }

    [Fact]
    public void TrailingEmptyWindowClause_IsTolerated()
    {
        // A trailing ';' (empty clause) is skipped, not an error.
        var ok = Build("Mon 09:00-17:00;", new[] { "x.com" }, System.Array.Empty<string>(), out var spec, out var err);
        Assert.True(ok, err);
        Assert.Equal("v1;1:0900-1700;sites=x.com;apps=", spec);
    }

    [Theory]
    [InlineData("", "window")]                              // no windows
    [InlineData("   ", "window")]
    [InlineData("Mon 22:00-06:00", "must end after")]       // SD3 overnight (open >= close)
    [InlineData("Mon 09:00-09:00", "must end after")]       // zero-length
    [InlineData("Fri-Mon 09:00-17:00", "runs backwards")]   // reversed day range
    [InlineData("Xyz 09:00-17:00", "day")]                  // bad day name
    [InlineData("Mon Tue Wed 09:00-17:00", "day")]          // space-separated list -> rejected, NOT silently narrowed to Mon
    [InlineData("monkey 09:00-17:00", "day")]               // garbage word with a day-ish prefix -> rejected (not "Mon")
    [InlineData("Mon 25:00-26:00", "time")]                 // hour out of range
    [InlineData("Mon 09:60-10:00", "time")]                 // minute out of range
    [InlineData("09:00-17:00", "window")]                   // no days
    [InlineData("Mon 0900-1700", "window")]                 // no colon (not the human HH:MM form)
    public void MalformedWindows_AreRejected_NoSpecStamped(string windows, string errFragment)
    {
        var ok = Build(windows, new[] { "x.com" }, System.Array.Empty<string>(), out var spec, out var err);
        Assert.False(ok);
        Assert.Equal("", spec);                              // NOTHING serialised
        Assert.Contains(errFragment, err, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptySiteList_IsRejected_NoSpec()
    {
        var ok = Build("Mon 09:00-17:00", System.Array.Empty<string>(), new[] { "chrome.exe" }, out var spec, out var err);
        Assert.False(ok);
        Assert.Equal("", spec);
        Assert.Contains("site", err, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SiteOrAppWithGrammarSeparator_IsRejected_NoSpec()
    {
        // A "|" or ";" in a site/app token would corrupt the ";"-split / "|"-split Spec grammar;
        // reject it (defence-in-depth - SplitList already eats ";" from --sites, and a stray "|"
        // only ever over-blocks, but a garbage Spec must never be stamped).
        Assert.False(Build("Mon 09:00-17:00", new[] { "a|b.com" }, System.Array.Empty<string>(), out var s1, out var e1));
        Assert.Equal("", s1);
        Assert.Contains("unsupported", e1, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(Build("Mon 09:00-17:00", new[] { "a;b.com" }, System.Array.Empty<string>(), out var s2, out _));
        Assert.Equal("", s2);
        Assert.False(Build("Mon 09:00-17:00", new[] { "x.com" }, new[] { "ev|il.exe" }, out var s3, out _));
        Assert.Equal("", s3);
    }

    [Fact]
    public void EveryGeneratedWindowHasOpenBeforeClose_TheFailClosedGuarantee()
    {
        // A spread of valid forms all parse back to windows with open < close (SD3), so a validated
        // CLI Spec can never carry the garbage-window the guardian's over-approx armed-signal fears
        // (that divergence is unreachable from the CLI - the c2 verifier P3#1 the CLI closes).
        foreach (var w in new[] { "Mon 00:00-00:01", "Tue-Thu 12:30-13:00", "Sat,Sun 06:00-23:59" })
        {
            Assert.True(Build(w, new[] { "x.com" }, System.Array.Empty<string>(), out var spec, out var err), err);
            var parsed = monkmode.Service1.ParseSchedule(spec);
            Assert.NotEmpty(parsed.Windows);
            foreach (var win in parsed.Windows) Assert.True(win.OpenMinutes < win.CloseMinutes);
        }
    }
}

// WriteScheduleConfig + ScheduleIsArmed end-to-end through the CLI arm path. Writes the shared
// test-bin ini + backup (serialised with the other CliIniWriters - fence: only the test-bin ini is
// touched, never a live block/service). Proves the fresh arm is a MAC-valid schedule-only config
// the real service gates read as armed-idle, a re-arm preserves the frame, and --clear disarms +
// re-stamps valid.
[Collection("CliIniWriters")]
public class ScheduleWriteConfigTests
{
    private static readonly CultureInfo EnCa = new("en-CA");

    private static string BuildSpec(string windows, string[] sites)
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(windows, sites, System.Array.Empty<string>(), ref spec, ref err), err);
        return spec;
    }

    // The CLI's own DPAPI MAC check (mirrors the private ConfigMacIsValidForIni via the Friend
    // helpers): a cleared config whose MAC wasn't re-stamped would read INVALID here => the service
    // would FREEZE it, never tear down. Blocker.CanonicalFromIni + ConfigIntegrity are Friend.
    private static bool CliMacValid(string iniPath)
    {
        var ini = new MonkMode.IniFile(); ini.Load(iniPath);
        var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
        if (key == null) return false;
        return MonkMode.ConfigIntegrity.ConfigMacIsValid(MonkMode.Blocker.CanonicalFromIni(ini), ini.GetKeyValue("Integrity", "Mac"), key);
    }

    private static void Wipe(string ini, string backup)
    {
        try { if (File.Exists(ini)) File.Delete(ini); } catch { /* best-effort */ }
        try { if (File.Exists(backup)) File.Delete(backup); } catch { /* best-effort */ }
    }

    [Fact]
    public void FreshArm_WritesScheduleOnlyConfig_ArmedIdleThroughTheRealGates()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        Wipe(iniPath, backupPath);
        try
        {
            var spec = BuildSpec("Mon-Fri 09:00-17:00", new[] { "reddit.com" });
            MonkMode.Blocker.WriteScheduleConfig(spec);

            var ini = new MonkMode.IniFile(); ini.Load(iniPath);
            Assert.Equal(spec, ini.GetKeyValue("Schedule", "Spec"));
            Assert.Equal("", ini.GetKeyValue("Schedule", "ActiveUntil"));   // no window open (the service is its sole writer)
            Assert.Equal("null", ini.GetKeyValue("Process", "List"));       // no manual apps
            Assert.Equal("null", ini.GetKeyValue("User", "CustomSites"));   // no manual sites
            Assert.Equal("no", ini.GetKeyValue("User", "Done"));
            Assert.Equal("no", ini.GetKeyValue("Commit", "Committed"));

            // [Time] Until is the past sentinel (schedule-only has no manual duration).
            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0), MonkMode.Blocker.ActiveBlockEnd());

            // MAC-valid from birth => the CLI reads it as ARMED (ScheduleIsArmed = macValid AND Spec).
            Assert.True(CliMacValid(iniPath));
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());

            // Through the REAL service gates: armed (exact), the sentinel reads expired, no window
            // open => OnStart HOLDS it alive (the c2 §4C trap fix) instead of stopMe.
            Assert.True(monkmode.Service1.ScheduleArmed(true, spec));
            Assert.True(monkmode.Service1.BlockHasExpired(MonkMode.Blocker.ScheduleOnlyExpiredUntil, DateTime.Now, 0));
            var hw = DateTime.Now.ToString(EnCa);
            Assert.False(monkmode.Service1.EffectiveExit(
                MonkMode.Blocker.ScheduleOnlyExpiredUntil, "", "", "", hw, 0, macValid: true, scheduleArmed: true));
            // Control: the SAME state with NO schedule (not armed) WOULD stopMe (sentinel is expired).
            Assert.True(monkmode.Service1.EffectiveExit(
                MonkMode.Blocker.ScheduleOnlyExpiredUntil, "", "", "", hw, 0, macValid: true, scheduleArmed: false));
        }
        finally { Wipe(iniPath, backupPath); }
    }

    [Fact]
    public void ReArm_ChangesSpec_PreservesTheMonotonicFrame_NeverFabricatesAWindow()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        Wipe(iniPath, backupPath);
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Fri 09:00-17:00", new[] { "reddit.com" }));
            var before = new MonkMode.IniFile(); before.Load(iniPath);
            var hwBefore = before.GetKeyValue("Time", "HighWater");   // encrypted; must be byte-identical after
            Assert.NotEqual("", hwBefore);

            var spec2 = BuildSpec("Sat,Sun 10:00-14:00", new[] { "x.com" });
            MonkMode.Blocker.WriteScheduleConfig(spec2);   // armed -> EDIT-in-place path

            var after = new MonkMode.IniFile(); after.Load(iniPath);
            Assert.Equal(spec2, after.GetKeyValue("Schedule", "Spec"));       // the new rule
            Assert.Equal(hwBefore, after.GetKeyValue("Time", "HighWater"));   // the frame is NOT reset (can't shorten a window)
            Assert.Equal("", after.GetKeyValue("Schedule", "ActiveUntil"));   // the edit never fabricates/touches a window
            Assert.True(CliMacValid(iniPath));                                // re-stamped valid
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
        }
        finally { Wipe(iniPath, backupPath); }
    }

    [Fact]
    public void Clear_BlanksSpec_ReStampsValid_Disarms_FramePreserved()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        Wipe(iniPath, backupPath);
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Fri 09:00-17:00", new[] { "reddit.com" }));
            var before = new MonkMode.IniFile(); before.Load(iniPath);
            var hw = before.GetKeyValue("Time", "HighWater");

            MonkMode.Blocker.WriteScheduleConfig("");   // schedule --clear

            var after = new MonkMode.IniFile(); after.Load(iniPath);
            // Blanked: a written-then-empty ini value reloads as null (the IniFile round-trip quirk),
            // which CanonicalFromIni + ScheduleIsArmed both treat as "no spec" (null == "" here).
            Assert.True(string.IsNullOrEmpty(after.GetKeyValue("Schedule", "Spec")));
            Assert.Equal(hw, after.GetKeyValue("Time", "HighWater"));     // frame preserved (a clear can't shorten an open window)
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());            // disarmed (Spec empty)
            // CRUCIAL: the cleared config is MAC-VALID (re-stamped over Spec="") - so the service can
            // Lift/stopMe it. A stale MAC would FREEZE it (macValid=False), trapping the machine until
            // --force. With the past sentinel + not-armed + no window, ClassifyHeartbeat now says Lift.
            Assert.True(CliMacValid(iniPath));
            Assert.Equal(monkmode.Service1.HeartbeatAction.Lift,
                monkmode.Service1.ClassifyHeartbeat(macValid: true, blockExpired: true,
                    coolOffElapsed: false, codeUnlocked: false, scheduleActive: false,
                    scheduleArmed: monkmode.Service1.ScheduleArmed(true, "")));
        }
        finally { Wipe(iniPath, backupPath); }
    }

    [Fact]
    public void ManualBlock_IsNeverScheduleArmed_SoBlockAndAddGuardsStayInert()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        Wipe(iniPath, backupPath);
        try
        {
            // A manual block writes no [Schedule] Spec => ScheduleIsArmed False => the SD-c1 guards in
            // DoBlock/DoAdd never fire for a manual block (byte-identical CLI behaviour to pre-c3).
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, System.Array.Empty<string>(), new DateTime(2027, 1, 1, 0, 0, 0));
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());
        }
        finally { Wipe(iniPath, backupPath); }
    }
}

// The notifier's ScheduleArmed gate (design §6.4): parity with the service, and the toast-
// suppression semantics. A schedule (Spec with windows) suppresses the "block ended" toast; a
// manual block (empty Spec) does not, so it toasts byte-identically to pre-c3.
public class NotifierScheduleArmedGateTests
{
    [Theory]
    [InlineData("v1;12345:0900-1700;sites=x.com;apps=", true)]
    [InlineData("v1;7:1000-1400;sites=x.com", true)]
    [InlineData("", false)]                        // manual block / cleared schedule
    [InlineData("garbage", false)]                 // unparseable -> inert
    [InlineData("v1;99:9999-0000;sites=x", false)] // every window malformed -> 0 windows
    public void ScheduleArmed_MatchesTheService_ExactDerivation(string spec, bool armed)
    {
        // The notifier derives armed EXACTLY as the service (macValid AND ParseSchedule has a window)
        // so it suppresses the toast for precisely the blocks the service holds alive.
        Assert.Equal(armed, mm_notify.Form1.ScheduleArmed(true, spec));
        Assert.Equal(monkmode.Service1.ScheduleArmed(true, spec), mm_notify.Form1.ScheduleArmed(true, spec));
        // A frozen config (macValid False) is never armed => the toast is NOT suppressed - matches the service.
        Assert.False(mm_notify.Form1.ScheduleArmed(false, spec));
    }

    [Fact]
    public void ManualBlock_EmptyOrNullSpec_NotArmed_ToastFiresAsBefore()
    {
        // The load-bearing regression: a manual block has no Spec, so the notifier's Done="yes" ->
        // AnnounceBlockEnded path is UNGATED (armed=False), byte-identical to pre-c3.
        Assert.False(mm_notify.Form1.ScheduleArmed(true, ""));
        Assert.False(mm_notify.Form1.ScheduleArmed(true, null!));
        Assert.False(mm_notify.Form1.ScheduleArmed(true, "   "));
    }
}
