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

// MonkMode.Tests - bug 1253 (audit N1, 12/10/2026): an OPEN schedule window survives
// `schedule --clear` followed by a fresh `schedule` arm.
//
// THE BUG THIS PINS. `schedule --clear` blanks [Schedule] Spec but leaves an open window's
// [Schedule] ActiveUntil to run to its end, by design. WriteScheduleConfig picked its path on
// ScheduleIsArmed() (MAC-valid AND Spec non-blank), so the next arm took the FRESH scaffold:
// a brand-new ini with no ActiveUntil and a new HighWater - the open window erased early. And
// AnythingArmed() read the same state as "fresh", so DoSchedule deleted the live window's hosts
// snapshot and could re-snapshot our own forced-off DoH policy.
//
// The fixture is the audit's repro (AuditReproOpenWindowWipe.cs, steps 1-4): the service's
// window-open write is staged exactly as PersistScheduleActiveUntilAt does it - ActiveUntil =
// HighWater + 1h, encrypted, MAC recomputed with the stored key.
//
// The AnythingArmed open-window cases live HERE (not in DohSnapshotFreshArmTests.cs, whose
// pins are untouched) because they need this file's window-open staging.
//
// HARD FENCE: only the shared test-bin ini + backup (CliIniWriters collection). No service,
// no SCM (ArmSlot gets serviceInstalled=false injected), no registry, no real hosts file.

using System.Globalization;
using System.IO;

namespace MonkMode.Tests;

public class ShouldEditScheduleInPlaceTests
{
    // The truth table, written out row by row: edit in place ONLY under a valid MAC, and then
    // when EITHER a Spec is armed OR a window is open. Blank = Nothing, "" or whitespace.
    [Theory]
    [InlineData(true, "v1;spec", "", true)]          // armed Spec            -> re-arm/clear in place (today's path)
    [InlineData(true, "", "enc-active-until", true)] // cleared Spec, window open -> THE FIX: carry it forward
    [InlineData(true, "v1;spec", "enc", true)]       // both
    [InlineData(true, "", "", false)]                // nothing armed, no window -> fresh scaffold
    [InlineData(true, "  ", "\t ", false)]           // whitespace reads as blank
    [InlineData(true, null, null, false)]            // Nothing reads as blank
    [InlineData(true, null, "enc", true)]            // Nothing Spec, window open
    [InlineData(true, "v1;spec", null, true)]        // Spec, Nothing window
    [InlineData(false, "v1;spec", "enc", false)]     // MAC invalid with both set -> fresh scaffold (P18 hatch)
    [InlineData(false, "", "enc", false)]            // MAC invalid, window only
    [InlineData(false, "v1;spec", "", false)]        // MAC invalid, Spec only
    [InlineData(false, "", "", false)]
    public void Matrix(bool macValid, string? spec, string? activeUntil, bool expected)
    {
        Assert.Equal(expected, MonkMode.Blocker.ShouldEditScheduleInPlace(macValid, spec, activeUntil));
    }

    // Fail-closed like ShouldReseedMonotonicFrame: a garbage deadline is still "a window may be
    // open", so it carries forward rather than being scaffolded away.
    [Fact]
    public void UnparseableActiveUntil_StillCountsAsOpen()
    {
        Assert.True(MonkMode.Blocker.ShouldEditScheduleInPlace(true, "", "not-a-date"));
    }
}

[Collection("CliIniWriters")]
public class ScheduleOpenWindowTests
{
    private static readonly CultureInfo Ca = new CultureInfo("en-CA");

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath() })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private static MonkMode.IniFile Reload()
    {
        var ini = new MonkMode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static string BuildSpec(string windows, params string[] sites)
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(windows, sites, Array.Empty<string>(), ref spec, ref err), err);
        return spec;
    }

    private static byte[] StoredKey(MonkMode.IniFile ini) =>
        MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));

    private static void RestampAndSave(MonkMode.IniFile ini)
    {
        ini.SetKeyValue("Integrity", "Mac",
            MonkMode.ConfigIntegrity.ComputeConfigMac(MonkMode.Blocker.CanonicalFromIni(ini), StoredKey(ini)));
        ini.Save(MonkMode.Blocker.IniPath());
    }

    private static bool MacValid(MonkMode.IniFile ini)
    {
        var key = StoredKey(ini);
        if (key == null) return false;
        return MonkMode.ConfigIntegrity.ConfigMacIsValid(MonkMode.Blocker.CanonicalFromIni(ini), ini.GetKeyValue("Integrity", "Mac"), key);
    }

    // The service's window-open write (PersistScheduleActiveUntilAt): ActiveUntil = HighWater + 1h.
    private static void StageOpenWindow()
    {
        var enc = new MonkMode.Simple3Des("mm_textbox");
        var ini = Reload();
        var hw = DateTime.Parse(enc.DecryptData(ini.GetKeyValue("Time", "HighWater")), Ca);
        ini.SetKeyValue("Schedule", "ActiveUntil", enc.EncryptData(hw.AddHours(1).ToString(Ca)));
        RestampAndSave(ini);
    }

    // Steps 1-4 of the audit repro: arm, the service opens a window, then `schedule --clear`.
    private static void ArmOpenThenClear()
    {
        MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Sun 00:00-23:59", "repro-a.example"));
        Assert.True(MonkMode.Blocker.ScheduleIsArmed());
        StageOpenWindow();
        Assert.True(MonkMode.Blocker.ScheduleWindowIsOpen());

        MonkMode.Blocker.WriteScheduleConfig("");                // schedule --clear
        Assert.False(MonkMode.Blocker.ScheduleIsArmed());
        Assert.True(MonkMode.Blocker.ScheduleWindowIsOpen());
        Assert.False(MonkMode.Blocker.AnySlotArmed());
    }

    // THE repro, inverted: after --clear, a fresh arm for a window that is not now carries the
    // open window forward - ActiveUntil, HighWater and the key byte-identical - and arms the
    // new Spec under a valid MAC.
    [Fact]
    public void ClearThenFreshArm_CarriesTheOpenWindowForward()
    {
        Wipe();
        try
        {
            ArmOpenThenClear();
            var before = Reload();
            var activeUntil = before.GetKeyValue("Schedule", "ActiveUntil");
            var highWater = before.GetKeyValue("Time", "HighWater");
            var key = before.GetKeyValue("Integrity", "Key");
            Assert.NotEqual("", activeUntil);

            var spec = BuildSpec("Mon-Sun 03:00-03:05", "repro-b.example");
            MonkMode.Blocker.WriteScheduleConfig(spec);

            var after = Reload();
            Assert.Equal(activeUntil, after.GetKeyValue("Schedule", "ActiveUntil"));
            Assert.True(MonkMode.Blocker.ScheduleWindowIsOpen());
            Assert.Equal(highWater, after.GetKeyValue("Time", "HighWater"));
            Assert.Equal(key, after.GetKeyValue("Integrity", "Key"));
            Assert.Equal(spec, after.GetKeyValue("Schedule", "Spec"));
            Assert.Equal("1", after.GetKeyValue("Guard", "ArmedCount"));
            Assert.True(MacValid(after));
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
        }
        finally { Wipe(); }
    }

    // The fresh scaffold is still the path when NO window is open: a clear + arm mints a new
    // config (no ActiveUntil, a NEW [Integrity] Key), exactly as before the fix.
    [Fact]
    public void ClearThenFreshArm_NoOpenWindow_TakesTheFreshScaffold()
    {
        Wipe();
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Sun 00:00-23:59", "repro-a.example"));
            MonkMode.Blocker.WriteScheduleConfig("");
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());
            Assert.False(MonkMode.Blocker.ScheduleWindowIsOpen());
            var oldKey = Reload().GetKeyValue("Integrity", "Key");

            var spec = BuildSpec("Mon-Sun 03:00-03:05", "repro-b.example");
            MonkMode.Blocker.WriteScheduleConfig(spec);

            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Schedule", "ActiveUntil"));
            Assert.NotEqual(oldKey, after.GetKeyValue("Integrity", "Key"));
            Assert.Equal(spec, after.GetKeyValue("Schedule", "Spec"));
            Assert.True(MacValid(after));
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
        }
        finally { Wipe(); }
    }

    // P18 residual, named not fixed: under an INVALID MAC the open window is not trusted by
    // anything, and the writer still takes the fresh scaffold (the recovery hatch).
    [Fact]
    public void MacInvalid_OpenWindow_StillTakesTheFreshScaffold()
    {
        Wipe();
        try
        {
            ArmOpenThenClear();
            var tampered = Reload();
            tampered.SetKeyValue("Integrity", "Mac", "AAAA");
            tampered.Save(MonkMode.Blocker.IniPath());
            Assert.False(MonkMode.Blocker.ScheduleWindowIsOpen());   // MAC-gated

            var spec = BuildSpec("Mon-Sun 03:00-03:05", "repro-b.example");
            MonkMode.Blocker.WriteScheduleConfig(spec);

            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Schedule", "ActiveUntil"));
            Assert.True(MacValid(after));
        }
        finally { Wipe(); }
    }

    // The writer's slot guard holds on the path the open-window term opened: with a slot armed
    // beside an open window and the Spec blank, a direct schedule arm refuses and leaves the
    // config byte-identical (the [Time] Until slot mirror is not overwritten). ArmSlot with the
    // service absent takes the P18 fresh rewrite, which erases ActiveUntil - so the window is
    // staged AGAIN after it, and both preconditions are asserted before the writer runs; only
    // then is it the edit-in-place condition's slot term (not the scaffold backstop) refusing.
    [Fact]
    public void SlotArmedBesideOpenWindow_WriterRefuses()
    {
        Wipe();
        try
        {
            ArmOpenThenClear();
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(),
                                             "", null, DateTime.Now.AddHours(2), false, false, 0, false);
            Assert.True(r.Ok);
            StageOpenWindow();
            Assert.True(MonkMode.Blocker.AnySlotArmed());
            Assert.True(MonkMode.Blocker.ScheduleWindowIsOpen());
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());
            var bytes = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Sun 03:00-03:05", "repro-b.example"));

            Assert.Equal(bytes, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());
        }
        finally { Wipe(); }
    }

    // AnythingArmed counts the open window: Spec blank + window open is NOT a fresh arm, so
    // DoSchedule keeps the live hosts snapshot and never re-snapshots DoH over our own policy.
    [Fact]
    public void AnythingArmed_SpecBlankWindowOpen_IsTrue()
    {
        Wipe();
        try
        {
            ArmOpenThenClear();
            Assert.True(MonkMode.Blocker.AnythingArmed());
            Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), snapshotExists: false));
        }
        finally { Wipe(); }
    }

    // ...and stops counting it once the window has closed on the monotonic mark (HighWater
    // restamped past ActiveUntil, MAC recomputed) - the gate re-opens for a genuinely fresh arm.
    [Fact]
    public void AnythingArmed_WindowElapsed_IsFalse()
    {
        Wipe();
        try
        {
            ArmOpenThenClear();
            var enc = new MonkMode.Simple3Des("mm_textbox");
            var ini = Reload();
            var activeUntil = DateTime.Parse(enc.DecryptData(ini.GetKeyValue("Schedule", "ActiveUntil")), Ca);
            ini.SetKeyValue("Time", "HighWater", enc.EncryptData(activeUntil.AddMinutes(1).ToString(Ca)));
            RestampAndSave(ini);

            Assert.False(MonkMode.Blocker.ScheduleWindowIsOpen());
            Assert.False(MonkMode.Blocker.AnythingArmed());
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AnythingArmed_NoConfig_IsFalse()
    {
        Wipe();
        try
        {
            Assert.False(MonkMode.Blocker.AnythingArmed());
        }
        finally { Wipe(); }
    }
}
