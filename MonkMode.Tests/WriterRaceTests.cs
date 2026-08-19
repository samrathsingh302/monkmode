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

// MonkMode.Tests - FX6: THE CONFIG-WRITER / RACE FAMILY (v1.1 bughunt F7-F10).
//
// Four separate ways the shared monkmode_settings.ini could be written wrongly by writers
// that never agreed with each other. Nothing here is a style question: each one ends in a
// block that is wedged shut, silently narrowed, or duplicated with a burnt exit code.
//
//   F7  A killed notifier inside its own ~2s clock-change window leaves [Time] TimeChanging
//       raised FOR EVER. Every service exit path AND the only [Time] HighWater write sit
//       behind that flag, it is outside the MAC-covered canonical (so nothing detects it),
//       it survives a reboot, and it is reachable NON-elevated (a time-zone change
//       broadcasts WM_TIMECHANGE). The block could then never reach its own end.
//       -> the flag now SELF-EXPIRES against monotonic time, and the heartbeat lowers an
//          orphan so the cooperation protocol recovers.
//   F8  The notifier whole-file-saved the shared config from its own in-memory model, so
//       anything the service or CLI wrote in the window was rolled back - and since the
//       stale [Integrity] Mac travelled with the stale canonical, the rolled-back file
//       still VERIFIED. -> one writer, re-read + no-op + generation check.
//   F9  RetireSlotAt and the heartbeat's Restamp are whole-file writes with no generation
//       check, so they could delete a slot a CLI arm had already CONFIRMED - i.e. a block
//       the user was told was armed, whose ONE-TIME partner code was printed and is now
//       unrecoverable. -> both abandon the save if the config moved under them.
//   F10 The arm confirmed its own write by POSITION, so a retire compacting a slot out
//       between the Save and the confirm made it misread its landed write as a lost race
//       and append a SECOND identical slot with a fresh code. -> confirm the id ANYWHERE.
//
// EVERY assertion below fails if the fix is reverted - the three live classes stage the
// exact race through the <ThreadStatic> production-inert hooks and assert on the resulting
// FILE, not on an intention.
//
// Fences honoured: the only files touched are the shared test-bin ini/backup/snapshot
// (serialised with the other writers via the CliIniWriters collection, wiped in finally)
// and GUID temp directories under the test bin. Never the real hosts file, the registry,
// the SCM or any deployed config. Nothing is armed for real; no binary is executed.

using System.Globalization;

namespace MonkMode.Tests;

// ================= F7 (pure): the TimeChanging flag self-expires =================

public class TimeChangeGateTests
{
    private const long Max = monkmode.Service1.TimeChangeHoldMaxSeconds;

    [Fact]
    public void LoweredFlag_NeverHolds_ExactlyAsTheOldStrCompDid()
    {
        Assert.False(monkmode.Service1.TimeChangeHoldActive("no", 0, Max));
        // ...and it stays not-holding however long it has read "no".
        Assert.False(monkmode.Service1.TimeChangeHoldActive("no", 99999, Max));
    }

    [Fact]
    public void AGenuineClockChangeEpisode_IsStillFullyHeld()
    {
        // THE PROPERTY THE FLAG EXISTS FOR. The notifier raises it, sleeps 2000ms and lowers
        // it, so a real episode is ~2 seconds. If this ever goes False the fix has broken the
        // thing it was protecting: the service would act on a half-updated config mid-change.
        Assert.True(monkmode.Service1.TimeChangeHoldActive("yes", 0, Max));
        Assert.True(monkmode.Service1.TimeChangeHoldActive("yes", 2, Max));
        Assert.True(monkmode.Service1.TimeChangeHoldActive("yes", Max, Max));
    }

    [Fact]
    public void ARaiseThatOutlivesItsBound_StopsGating_TheWholeF7Fix()
    {
        // The regression pin. The old gate was a raw StrComp("no", flag) = 0, under which
        // this is True for ever and the block can never reach its own end.
        Assert.False(monkmode.Service1.TimeChangeHoldActive("yes", Max + 1, Max));
        Assert.False(monkmode.Service1.TimeChangeHoldActive("yes", 86400, Max));
    }

    [Fact]
    public void AnAbsentOrGarbledFlag_GatesBriefly_ThenAlsoExpires()
    {
        // GetKeyValue answers "" for a missing [Time] TimeChanging, and the old strict "no"
        // test therefore wedged on a config that simply lacked the key. Fail-closed while it
        // is fresh (we do not know what is happening), bounded like every other raise.
        Assert.True(monkmode.Service1.TimeChangeHoldActive("", 0, Max));
        Assert.True(monkmode.Service1.TimeChangeHoldActive("YES ", 0, Max));
        Assert.False(monkmode.Service1.TimeChangeHoldActive("", Max + 1, Max));
        Assert.False(monkmode.Service1.TimeChangeHoldActive("YES ", Max + 1, Max));
    }

    [Fact]
    public void TheBoundIsGenerousAgainstTheNotifiersOwn2sWindow()
    {
        // 300s is two orders of magnitude of headroom over the notifier's Sleep(2000). Pinned
        // as a value because shrinking it towards the episode length is what would start
        // breaking real clock changes, and growing it is what would make the wedge bite.
        Assert.Equal(300, monkmode.Service1.TimeChangeHoldMaxSeconds);
    }
}

// ================= F9 (pure): the lost-update generation token =================

public class ConfigGenerationTokenTests
{
    [Fact]
    public void SameToken_IsUnchanged_DifferentTokenIsNot()
    {
        Assert.True(monkmode.Service1.ConfigGenerationUnchanged("abc==", "abc=="));
        Assert.False(monkmode.Service1.ConfigGenerationUnchanged("abc==", "abd=="));
    }

    [Fact]
    public void AnUnreadableProbe_CountsAsChanged_SoNothingIsOverwritten()
    {
        // ConfigGenerationToken answers Nothing when it cannot read the file at all. We never
        // rewrite a whole config we could not just read: fail-closed on the write side.
        Assert.False(monkmode.Service1.ConfigGenerationUnchanged("abc==", null!));
        Assert.False(monkmode.Service1.ConfigGenerationUnchanged(null!, "abc=="));
        Assert.False(monkmode.Service1.ConfigGenerationUnchanged(null!, null!));
    }

    [Fact]
    public void TheTokenIsTheStoredMac_AndAMissingFileReadsAsUnreadable()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fx6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p = Path.Combine(dir, "cfg.ini");
            // An ABSENT config reads as unreadable, NOT as "no MAC": IniFile.Load answers an
            // empty model for a missing path rather than throwing, and an empty model's "" Mac
            // would compare equal to another "" and wave a whole-file write through.
            Assert.Null(monkmode.Service1.ConfigGenerationToken(Path.Combine(dir, "absent.ini")));
            File.WriteAllText(p, "[Integrity]\r\nMac=AAAA\r\n");
            Assert.Equal("AAAA", monkmode.Service1.ConfigGenerationToken(p));
            Assert.False(monkmode.Service1.ConfigGenerationUnchanged(
                monkmode.Service1.ConfigGenerationToken(Path.Combine(dir, "absent.ini")),
                monkmode.Service1.ConfigGenerationToken(Path.Combine(dir, "absent.ini"))));
        }
        finally { Directory.Delete(dir, true); }
    }
}

// ================= the live races =================

[Collection("CliIniWriters")]
public class WriterRaceLiveTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    // NEVER construct a Service1 directly - InitializeComponent starts the live 10s
    // enforcement tick. TestSvc stops it the instant the instance exists.
    private static monkmode.Service1 Svc() => TestSvc.New();

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "fx6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Drop(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static void Wipe()
    {
        monkmode.Service1.RetireSaveHookForTests = null;
        monkmode.Service1.RestampSaveHookForTests = null;
        MonkMode.Blocker.ArmConfirmHookForTests = null;
        mm_notify.Form1.SharedConfigWriteHookForTests = null;
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static MonkMode.Blocker.ArmResult Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false);

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static int SlotCount(monkmode.IniFile ini)
        => monkmode.ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"));

    private static List<string> Sites(monkmode.IniFile ini)
    {
        var all = new List<string>();
        for (var pos = 1; pos <= SlotCount(ini); pos++)
            all.AddRange(monkmode.Service1.SplitPackedList(ini.GetKeyValue("Slot" + pos, "Sites"), ';'));
        return all;
    }

    // A one-shot latch: the hooks fire on EVERY attempt, and a race that repeats is not the
    // race being staged (the retry must be allowed to succeed).
    private static Action<string> Once(Action body)
    {
        var fired = false;
        return _ =>
        {
            if (fired) return;
            fired = true;
            body();
        };
    }

    // ---- F10: the arm confirms its id at ANY position ----

    [Fact]
    public void ArmConfirm_SurvivesARetireCompactingASlotOutUnderIt_AndNeverDuplicates()
    {
        // THE EXACT REPORTED SHAPE. Three slots armed; the fourth arm saves at position 4 and,
        // before it can confirm, the service retires slot 1 - which compacts every later slot
        // down one, putting the new slot at position 3. The old position-keyed confirm read
        // Slot4.Id (now "", the freed trailing section), called its own landed write a lost
        // race and retried, APPENDING A SECOND IDENTICAL SLOT with a fresh id and a fresh
        // partner code - while the first duplicate's code was a local that the retry had just
        // overwritten, so a real, MAC-stamped block existed with no exit code at all.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.True(Arm("c.com").Ok);

            var svc = Svc();
            MonkMode.Blocker.ArmConfirmHookForTests = Once(() =>
                Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                             Path.Combine(dir, "monkmode_hosts.block"),
                                             Path.Combine(dir, "hosts"), "1")));

            var r = Arm("d.com");
            MonkMode.Blocker.ArmConfirmHookForTests = null;

            // The arm SUCCEEDED on its first attempt: the write did land, it was just sitting
            // one position lower than where it was written.
            Assert.True(r.Ok);
            Assert.Equal(4, r.Id);
            Assert.Equal(3, r.Position);        // reported from where the slot IS, post-compaction
            Assert.NotEqual("", r.PartnerCode);

            var after = Reload();
            Assert.Equal(3, SlotCount(after));                       // b, c, d - never four
            Assert.Equal(new[] { "b.com", "c.com", "d.com" }, Sites(after));
            Assert.Equal(new[] { 2, 3, 4 }, MonkMode.Blocker.ArmedSlotIds());
            // ONE slot carries the new site. A duplicate here is the burnt-code bug.
            Assert.Single(Sites(after).Where(s => s == "d.com"));
            var cliView = new MonkMode.IniFile();
            cliView.Load(MonkMode.Blocker.IniPath());
            Assert.Equal(3, MonkMode.Blocker.FindSlotPositionById(cliView, 4));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void ArmConfirm_StillRefusesWhenItsWriteGenuinelyDidNotLand()
    {
        // The other direction: confirming the id ANYWHERE must not become "confirm anything".
        // A hook that wipes the config outright leaves no id to find, so the arm still calls
        // it a lost race and, after its bounded retries, refuses - "could not arm" is the safe
        // failure, and the retries here all fail because the hook fires on every attempt.
        Wipe();
        try
        {
            MonkMode.Blocker.ArmConfirmHookForTests = p => File.WriteAllText(p, "");
            var r = Arm("a.com");
            MonkMode.Blocker.ArmConfirmHookForTests = null;
            Assert.False(r.Ok);
            Assert.Equal(MonkMode.Blocker.ArmOutcome.WriteRace, r.Outcome);
        }
        finally { Wipe(); }
    }

    // ---- F9: the retire abandons a save that would clobber a confirmed arm ----

    [Fact]
    public void Retire_AbandonsItsWrite_WhenAnArmLandsUnderIt_AndConvergesNextTick()
    {
        // A retire loads the config, compacts a slot out of its in-memory copy, and saves the
        // WHOLE file. An arm that landed in that window used to be deleted by that save -
        // after the user had been shown its one-time partner code. Now the generation check
        // (the stored [Integrity] Mac, which every legitimate writer moves) makes the retire
        // stand down instead. Fail-closed: the retired slot simply stays armed one more tick.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var svc = Svc();

            monkmode.Service1.RetireSaveHookForTests = Once(() => Assert.True(Arm("c.com").Ok));
            var wrote = svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(dir, "monkmode_hosts.block"),
                                         Path.Combine(dir, "hosts"), "1");
            monkmode.Service1.RetireSaveHookForTests = null;

            Assert.False(wrote);                       // the config was NOT rewritten
            var after = Reload();
            Assert.Equal(3, SlotCount(after));         // the racing arm survived intact
            Assert.Equal(new[] { "a.com", "b.com", "c.com" }, Sites(after));

            // ...and the retire is deferred, not lost: the next tick's attempt lands.
            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(dir, "monkmode_hosts.block"),
                                         Path.Combine(dir, "hosts"), "1"));
            var settled = Reload();
            Assert.Equal(2, SlotCount(settled));
            Assert.Equal(new[] { "b.com", "c.com" }, Sites(settled));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void Retire_WithNothingRacingIt_IsUnchanged()
    {
        // The guard must be inert on the normal path - a retire with no contender still
        // rewrites the config exactly as before.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                           Path.Combine(dir, "monkmode_hosts.block"),
                                           Path.Combine(dir, "hosts"), "1"));
            Assert.Equal(new[] { "b.com" }, Sites(Reload()));
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- F9 + F7: the heartbeat's Restamp write ----

    [Fact]
    public void HeartbeatRestamp_AbandonsItsWrite_WhenAnArmLandsUnderIt()
    {
        // The same clobber from the other whole-file service writer. The heartbeat runs every
        // 10 seconds for the whole life of every block, so this is the widest window of the
        // two - and losing the save costs nothing but one tick of HighWater advance, which
        // over-blocks by 10s and converges on the next tick.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = Reload().GetKeyValue("Time", "HighWater");
            var svc = Svc();

            monkmode.Service1.RestampSaveHookForTests = Once(() => Assert.True(Arm("b.com").Ok));
            var wrote = svc.RestampHeartbeatAt(MonkMode.Blocker.IniPath(), "2026-08-19 12:00:00", false);
            monkmode.Service1.RestampSaveHookForTests = null;

            Assert.False(wrote);
            var after = Reload();
            Assert.Equal(2, SlotCount(after));                                  // the arm survived
            Assert.Equal(new[] { "a.com", "b.com" }, Sites(after));
            Assert.Equal(before, after.GetKeyValue("Time", "HighWater"));       // and nothing was written
        }
        finally { Wipe(); }
    }

    [Fact]
    public void HeartbeatRestamp_PersistsHighWater_AndKeepsTheConfigMacValid()
    {
        // The unchanged behaviour the extraction must preserve: Now + HighWater written and
        // the MAC re-stamped over them. PersistSlotFieldAt refuses outright on an invalid MAC,
        // so a successful per-slot write afterwards is the proof the stamp is good.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var svc = Svc();
            const string hw = "2026-08-19 12:00:00";
            Assert.True(svc.RestampHeartbeatAt(MonkMode.Blocker.IniPath(), hw, false));

            var after = Reload();
            Assert.Equal(hw, new monkmode.Simple3Des("mm_textbox").DecryptData(after.GetKeyValue("Time", "HighWater")));
            Assert.True(svc.PersistSlotFieldAt(MonkMode.Blocker.IniPath(), "1", "PartnerUnlockedAt", "", false));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void HeartbeatRestamp_NeverBlanksAGoodHighWater_WhenTheTickCouldNotReadOne()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = Reload().GetKeyValue("Time", "HighWater");
            Assert.True(Svc().RestampHeartbeatAt(MonkMode.Blocker.IniPath(), "", false));
            Assert.Equal(before, Reload().GetKeyValue("Time", "HighWater"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void HeartbeatRestamp_LowersAnOrphanedTimeChangingFlag_OnlyWhenAsked()
    {
        // The F7 recovery half. Once a raise has outlived its bound the service stops obeying
        // it - but if nothing ever lowered it, the flag would stay raised for ever, be ignored
        // for ever, and the NEXT genuine clock change would go unhonoured. The heartbeat
        // lowers it in a write whose bytes it has just re-verified, and only for an orphan.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var svc = Svc();

            Assert.True(mm_notify.Form1.SaveSharedConfigKey(MonkMode.Blocker.IniPath(), "Time", "TimeChanging", "yes"));
            // A raise the service still considers LIVE is left strictly alone.
            Assert.True(svc.RestampHeartbeatAt(MonkMode.Blocker.IniPath(), "2026-08-19 12:00:00", false));
            Assert.Equal("yes", Reload().GetKeyValue("Time", "TimeChanging"));

            // An ORPHAN is lowered, and the config stays MAC-valid across the write (the flag
            // is outside the canonical, so lowering it moves no MAC-covered byte).
            Assert.True(svc.RestampHeartbeatAt(MonkMode.Blocker.IniPath(), "2026-08-19 12:00:10", true));
            Assert.Equal("no", Reload().GetKeyValue("Time", "TimeChanging"));
            Assert.True(svc.PersistSlotFieldAt(MonkMode.Blocker.IniPath(), "1", "PartnerUnlockedAt", "", false));
        }
        finally { Wipe(); }
    }

    // ---- F8: the notifier's writes ----

    [Fact]
    public void NotifierWrite_NeverRollsBackASlotArmedUnderIt()
    {
        // THE REPORTED SHAPE. The notifier loaded the config, set one housekeeping key and
        // whole-file-saved its own model - so an arm (or an applied `add`, or a just-verified
        // [Partner] UnlockedAt) that landed in that window was silently DELETED, and the stale
        // MAC that travelled with the stale canonical left the result verifying cleanly.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);

            mm_notify.Form1.SharedConfigWriteHookForTests = Once(() => Assert.True(Arm("c.com").Ok));
            var ok = mm_notify.Form1.SaveSharedConfigKey(MonkMode.Blocker.IniPath(), "Time", "TimeChanging", "yes");
            mm_notify.Form1.SharedConfigWriteHookForTests = null;

            // The retry (against fresh bytes) lands, so the notifier still gets its write...
            Assert.True(ok);
            var after = Reload();
            Assert.Equal("yes", after.GetKeyValue("Time", "TimeChanging"));
            // ...and the racing arm is intact. Two slots here is the F8 bug.
            Assert.Equal(3, SlotCount(after));
            Assert.Equal(new[] { "a.com", "b.com", "c.com" }, Sites(after));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void NotifierWrite_IsAPureNoOp_WhenTheKeyAlreadyReadsTheWantedValue()
    {
        // The commonest call by far (a fresh config already carries TimeChanging=no). Not
        // touching the file at all is the strongest possible form of "roll nothing back".
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            Assert.True(mm_notify.Form1.SaveSharedConfigKey(MonkMode.Blocker.IniPath(), "Time", "TimeChanging", "no"));
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void NotifierWrite_LandsOnAnUnstampedConfig_SoLoweringTheFlagIsNeverWedgedShut()
    {
        // The writer is deliberately NOT MAC-gated: refusing to lower [Time] TimeChanging on a
        // frozen or unstamped config would recreate the F7 wedge. Nothing here re-stamps, so
        // no unverified config is ever blessed by it.
        var dir = TempDir();
        try
        {
            var p = Path.Combine(dir, "cfg.ini");
            File.WriteAllText(p, "[Time]\r\nTimeChanging=yes\r\n[User]\r\nDone=no\r\n");
            Assert.True(mm_notify.Form1.SaveSharedConfigKey(p, "Time", "TimeChanging", "no"));
            var ini = new monkmode.IniFile();
            ini.Load(p);
            Assert.Equal("no", ini.GetKeyValue("Time", "TimeChanging"));
            Assert.Equal("no", ini.GetKeyValue("User", "Done"));   // nothing else disturbed
        }
        finally { Drop(dir); }
    }

    [Fact]
    public void NotifierCrashBackstop_LowersTheFlag_ThroughTheOneWriter()
    {
        // FX6 (F8) fold-in. The AppDomain UnhandledException backstop was the fourth notifier
        // write and the last raw Load -> SetKeyValue -> Save copy: a whole-file rewrite from
        // the crash handler's own model, at the moment the rest of the machine is LEAST likely
        // to be idle. It now routes through SaveSharedConfigKey like the other three.
        // ReassertTimeChangingFailSoft is the testable core (the Service1
        // .ReassertHostsFailClosed pattern); the live handler only feeds it the real path.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(mm_notify.Form1.SaveSharedConfigKey(MonkMode.Blocker.IniPath(), "Time", "TimeChanging", "yes"));

            // The hook lives INSIDE SaveSharedConfigKey, so `hookFired` is the routing pin: a
            // handler that went back to its own raw Load -> Save would never trip it. The hook
            // body is the race the raw copy lost - an arm landing mid-write.
            var hookFired = false;
            var arm = Once(() => Assert.True(Arm("b.com").Ok));
            mm_notify.Form1.SharedConfigWriteHookForTests = p => { hookFired = true; arm(p); };
            mm_notify.Program.ReassertTimeChangingFailSoft(MonkMode.Blocker.IniPath());
            mm_notify.Form1.SharedConfigWriteHookForTests = null;

            Assert.True(hookFired);
            var after = Reload();
            Assert.Equal("no", after.GetKeyValue("Time", "TimeChanging"));
            // ...and the arm that raced the crash handler survived it. One slot here is the
            // rollback the raw copy performed.
            Assert.Equal(2, SlotCount(after));
            Assert.Equal(new[] { "a.com", "b.com" }, Sites(after));
            Assert.True(Svc().PersistSlotFieldAt(MonkMode.Blocker.IniPath(), "1", "PartnerUnlockedAt", "", false));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void NotifierCrashBackstop_NeverThrows_AndNeverFabricatesAConfig()
    {
        // A throw out of an UnhandledException handler is undefined behaviour, and a crash
        // must never leave a stub config behind for the service to recover over.
        var dir = TempDir();
        try
        {
            var absent = Path.Combine(dir, "absent.ini");
            Assert.Null(Record.Exception(() => mm_notify.Program.ReassertTimeChangingFailSoft(absent)));
            Assert.False(File.Exists(absent));
            Assert.Null(Record.Exception(() => mm_notify.Program.ReassertTimeChangingFailSoft(null!)));
        }
        finally { Drop(dir); }
    }

    [Fact]
    public void NotifierWrite_FailsSoft_AndNeverCreatesAConfigOfItsOwn()
    {
        // Every notifier caller is best-effort: a missed housekeeping write costs at most one
        // duplicate toast or one bounded TimeChanging hold, never a lifted or narrowed block.
        // And it never writes a config into being - IniFile.Load answers an empty model for a
        // missing path, so a one-key stub would otherwise appear where no config exists (which
        // the service reads as a structurally-unusable primary and recovers over).
        var dir = TempDir();
        try
        {
            var absent = Path.Combine(dir, "absent.ini");
            Assert.False(mm_notify.Form1.SaveSharedConfigKey(absent, "Time", "TimeChanging", "no"));
            Assert.False(File.Exists(absent));
            Assert.False(mm_notify.Form1.SaveSharedConfigKey(Path.Combine(dir, "sub", "absent.ini"),
                                                             "Time", "TimeChanging", "no"));
        }
        finally { Drop(dir); }
    }
}
