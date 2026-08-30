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

// MonkMode.Tests - LEDGER 319 (30/08/2026): THE ESCAPE HATCH IS GONE; ITS ONE SURVIVING
// WRITER IS PINNED HERE, PLUS THE PROOF THAT `--force` IS NO LONGER A FLAG.
//
// `monkmode unblock --force` was an unconditional teardown: SCM recovery disabled, the watchdog
// pair killed, the deny-DELETE ACE removed, the service DELETED, hosts stripped, the snapshot,
// the config backup, the SafeBoot keys, the DoH policy and the notifier autorun all removed, and
// the armed config finally zeroed. It ended any block, at any time, with no partner code. On
// 30/08/2026 Samrath asked for it to go ("i dont like how i can force unblock it regardless ...
// i should only be able to unblock with code"), so the verb, the branch and every primitive that
// existed only to serve it were deleted - Blocker.KillWatchdogProcesses, RestoreHostsFromStrip,
// RemoveSafeBootKeys, DeleteBackup, RemoveDohPolicy, ClearNotifierAutorun, ServiceTools'
// DisableRecovery / RestoreDefaultServiceSd / DeleteServiceByName (and the DeleteService P/Invoke
// with them), and Program's Step_ / RunSdRestoreThenDelete. Most of that is proved by the fact
// that this file still compiles: a test naming any of them would not.
//
// TWO THINGS ARE STILL TESTABLE AND STILL MATTER:
//   1. `--force` and `--cancel` are not merely ignored - they are UNKNOWN options, so the CLI
//      says the flag does not exist rather than silently doing nothing (UnblockOptionSurfaceTests
//      at the foot of this file). A silent no-op would be the dangerous shape: a user could
//      believe a block was coming down.
//   2. Blocker.PersistZeroSlotConfig - the writer that produced the "nothing is armed" config -
//      survives as an assembly-internal seam with NO caller in the shipped CLI (its Public
//      entry point went with DoUnblock). It is kept, and pinned, because it is the CLI-side
//      statement of the shape a torn-down config must have, and because its live-block REFUSAL
//      is the property that stops a config-zeroing writer ever becoming a one-call teardown.
//
// The original F70 narrative, kept because it is why the writer exists at all:
//
// WHAT SMOKE B FOUND (20/08/2026, logs\2026-08-20-smoke-b.md §3). `monkmode unblock --force`
// removed the enforcement - watchdog pair killed, service DELETED, hosts stripped, snapshot and
// backup gone, `status` printing "no block has ever been installed on this machine" - and then
// left the ARMED CONFIG sitting on disk. Two guards read nothing but that file:
//
//   - Blocker.AnySlotArmed() reads [Slots] SlotCount straight out of it, and DoSchedule refuses
//     on `AnySlotArmed() OrElse BlockIsActive()`. BlockIsActive short-circuits False on a
//     stopped/absent service, so AnySlotArmed was the sole true: `monkmode schedule ...` exited
//     3 with "A block is armed. Finish or exit it before setting a schedule."
//   - tools\build-dist.ps1's Get-BuildRefusals reads the same SlotCount (and [Schedule] Spec),
//     so the dist could not be rebuilt or installed either - which is what blocked FX9's own
//     install-ACL drill during that sitting.
//
// Both refusals are in the SAFE direction. The bug is that they are UNENDING: the documented
// escape hatch put the machine in a state where a shipped user-facing command and the whole
// build/install path stayed refused, with nothing enforcing anywhere, until the ini was deleted
// by hand.
//
// WHAT THE FIX IS (and what these tests pin). PARITY, not a weaker guard. The SERVICE's own
// genuine-expiry teardown has always persisted a zero-slot config (Service1.PersistZeroSlotConfigAt,
// P39); the CLI escape hatch was missing the equivalent step. Blocker.PersistZeroSlotConfig() adds
// it, and DoUnblock runs it LAST. Not one guard was touched - crucially, none of them learns to
// consult the SCM, because AnySlotArmed exists precisely to answer the armed question WITHOUT the
// SCM (BlockIsActive's short-circuit on a stopped service is the hole it was added to close).
//
// So what is pinned here is the FAILING DIRECTION of F70 end to end - forced teardown, then
// `schedule` arms - plus the three things the fix must not cost: P17's never-restarting ids, B7's
// "never re-bless a tampered config", and the fail-closed truth that a machine which IS enforcing
// still refuses (that last one is unchanged code, asserted here as the control).
//
// Fences honoured: every write goes to the shared TEST-BIN ini/backup (the CliIniWriters
// collection serialises them, and the finally wipes them). Never the real hosts file, the
// registry, the SCM, Program Files or any deployed config. Nothing is ever armed for real, no
// service/CLI/notifier binary is executed, and the real forced-teardown command is never run -
// only the one config-writing step it now calls.

using System.Globalization;

namespace MonkMode.Tests;

[Collection("CliIniWriters")]
public class ForcedTeardownTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath() })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    private static bool Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false).Ok;

    private static MonkMode.IniFile Reload()
    {
        var ini = new MonkMode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static int SlotCount(MonkMode.IniFile ini)
        => MonkMode.ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"));

    // The CLI's own DPAPI MAC check (the private ConfigMacIsValidForIni, through the Friend
    // helpers), same idiom as ScheduleWriteConfigTests.
    private static bool MacValid()
    {
        var ini = Reload();
        var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
        if (key == null) return false;
        return MonkMode.ConfigIntegrity.ConfigMacIsValid(
            MonkMode.Blocker.CanonicalFromIni(ini), ini.GetKeyValue("Integrity", "Mac"), key);
    }

    private static string BuildSpec(string windows, params string[] sites)
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(windows, sites, Array.Empty<string>(), ref spec, ref err), err);
        return spec;
    }

    // ---- THE REGRESSION: the failing direction, end to end ----

    [Fact]
    public void AfterForcedTeardown_NothingReadsAsArmed_AndScheduleArmsAgain()
    {
        // F70 exactly: two slots armed, the teardown's config step runs, and the two things the
        // smoke found refused must both go through. Nothing here consults a service - that is the
        // whole point, since the machine in this state HAS no service.
        Wipe();
        try
        {
            Assert.True(Arm("smokeb-a.example"));
            Assert.True(Arm("smokeb-b.example"));
            Assert.Equal(2, SlotCount(Reload()));
            Assert.True(MonkMode.Blocker.AnySlotArmed());        // the exit-3 true, before

            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: false);

            // 1. The DoSchedule guard's sole true is gone (BlockIsActive is already False with no
            //    service, so this is the whole refusal).
            Assert.False(MonkMode.Blocker.AnySlotArmed());
            Assert.Equal(0, SlotCount(Reload()));

            // 2. The slot SECTIONS are gone outright, not just uncounted - a stale [Slot1] left
            //    behind is state a later reader could resurrect.
            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Slot1", "Id"));
            Assert.Equal("", after.GetKeyValue("Slot2", "Id"));

            // 3. ...and the command that F70 blocked now actually arms. WriteScheduleConfig has its
            //    own AnySlotArmed refusal (the S3b structural backstop), so this proves BOTH the
            //    command guard and the writer guard are satisfied - not just the first one.
            var spec = BuildSpec("Mon-Sun 23:00-04:00", "smokeb-night.example");
            MonkMode.Blocker.WriteScheduleConfig(spec);
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
            Assert.Equal(spec, Reload().GetKeyValue("Schedule", "Spec"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AfterForcedTeardown_TheConfigSaysNothingIsArmedToEveryReaderOfIt()
    {
        // The full "nothing is armed" shape, field by field - the same set the service's own
        // teardown writes. The first two are literally what tools\build-dist.ps1's Get-BuildRefusals
        // parses out of this file, so they are what unblocks a rebuild/install; the rest are the v9
        // mirror and the guard scalars, which must not be left claiming a horizon nothing holds.
        Wipe();
        try
        {
            Assert.True(Arm("smokeb-a.example"));
            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: false);

            var after = Reload();
            Assert.Equal("0", after.GetKeyValue("Slots", "SlotCount"));     // build-dist refusal 1
            Assert.Equal("", after.GetKeyValue("Schedule", "Spec"));        // build-dist refusal 2
            Assert.Equal("", after.GetKeyValue("Schedule", "ActiveUntil"));
            Assert.Equal("", after.GetKeyValue("Guard", "HoldUntil"));
            Assert.Equal("0", after.GetKeyValue("Guard", "ArmedCount"));
            Assert.Equal("null", after.GetKeyValue("Process", "List"));
            Assert.Equal("null", after.GetKeyValue("User", "CustomSites"));
            Assert.Equal("", after.GetKeyValue("Time", "CoolOffUntil"));
            // The v9 mirror end is the expired sentinel, not the block's old horizon.
            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0), MonkMode.Blocker.ActiveBlockEnd());
            // And the whole file still verifies, so a reader treats it as a real torn-down config
            // rather than a frozen one.
            Assert.True(MacValid());
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ForcedTeardown_KeepsNextSlotId_SoIdsNeverRestart()
    {
        // P17: ids never restart, even across a teardown - the service's PersistZeroSlotConfigAt
        // deliberately does NOT reset NextSlotId, and neither may this. If it did, a replayed
        // monkmode_partner.code.<id> minted for an old block could address a future one.
        Wipe();
        try
        {
            Assert.True(Arm("smokeb-a.example"));
            Assert.True(Arm("smokeb-b.example"));
            Assert.True(Arm("smokeb-c.example"));
            Assert.Equal("4", Reload().GetKeyValue("Slots", "NextSlotId"));

            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: false);

            Assert.Equal("4", Reload().GetKeyValue("Slots", "NextSlotId"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ForcedTeardown_OnATamperedConfig_ClearsItButNeverReBlessesTheMac()
    {
        // B7, both halves. The clear must still happen on a tampered config - otherwise a user who
        // poked at the ini could never escape the wedge - but the MAC must NOT be re-stamped, or
        // the escape hatch would become a laundry service that hands a tampered config a fresh
        // valid MAC. Fail-closed side: it stays frozen.
        Wipe();
        try
        {
            Assert.True(Arm("smokeb-a.example"));
            var tampered = Reload();
            tampered.SetKeyValue("Integrity", "Mac", Convert.ToBase64String(new byte[32]));
            tampered.Save(MonkMode.Blocker.IniPath());
            Assert.False(MacValid());

            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: false);

            Assert.False(MonkMode.Blocker.AnySlotArmed());                // the wedge is cleared anyway
            Assert.Equal(0, SlotCount(Reload()));
            Assert.False(MacValid());                                     // ...but nothing was re-blessed
            Assert.Equal(Convert.ToBase64String(new byte[32]), Reload().GetKeyValue("Integrity", "Mac"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ForcedTeardown_WithNoConfigAtAll_IsANoOp()
    {
        // Run against a machine that never had a config: must not throw, and must not CREATE an
        // ini where there was none.
        Wipe();
        try
        {
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));
            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: false);
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));
            Assert.False(MonkMode.Blocker.AnySlotArmed());
        }
        finally { Wipe(); }
    }

    // ---- THE CONTROL: fail-closed is unchanged ----

    [Fact]
    public void AMachineThatIsStillArmed_StillRefuses_TheGuardItselfWasNotWeakened()
    {
        // The route NOT taken. The other way to fix F70 was to teach the guards "the service is
        // gone and hosts are clean, so ignore the config" - which would have re-introduced exactly
        // the SCM dependence AnySlotArmed was added to remove, and handed anyone who could stop
        // the service and clean hosts a `schedule` that wipes live slots. Nothing about the guard
        // changed, and this is the assertion that says so: with slots in the config, it refuses,
        // service or no service.
        Wipe();
        try
        {
            Assert.True(Arm("smokeb-a.example"));
            Assert.True(MonkMode.Blocker.AnySlotArmed());
            // WriteScheduleConfig's own backstop refuses too, so an armed machine cannot be
            // schedule-scaffolded out of its blocks.
            MonkMode.Blocker.WriteScheduleConfig(BuildSpec("Mon-Sun 23:00-04:00", "smokeb-night.example"));
            Assert.Equal(1, SlotCount(Reload()));                     // the slot survived
            Assert.Equal("", Reload().GetKeyValue("Schedule", "Spec"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void TheWriterItselfRefusesWhileABlockIsLive_NotJustTheCommand()
    {
        // S3b's rule, applied to the new writer: "the refusal lives in the command and the
        // writer is Public and test-driven, so the writer must refuse too". If
        // PersistZeroSlotConfig cleared a config a RUNNING service was enforcing from, it would
        // BE a one-call teardown - the next tick reads zero slots, classifies TeardownAll and
        // lifts every block. So the live-block answer is a parameter of the writer, and this is
        // the arm that must change nothing. Ledger 319 made this the LOAD-BEARING test of the
        // pair: with DoUnblock's teardown gone there is no caller left to pass True, and this
        // refusal is the only thing standing between a surviving config-zeroing writer and a
        // code-free lift if one were ever wired up again.
        Wipe();
        try
        {
            Assert.True(Arm("smokeb-a.example"));
            var before = Reload();
            var slotsBefore = SlotCount(before);
            var untilBefore = before.GetKeyValue("Time", "Until");

            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: true);

            var after = Reload();
            Assert.Equal(slotsBefore, SlotCount(after));                    // nothing was zeroed
            Assert.Equal(1, SlotCount(after));
            Assert.NotEqual("", after.GetKeyValue("Slot1", "Id"));          // the section survived
            Assert.Equal(untilBefore, after.GetKeyValue("Time", "Until"));  // the mirror was not neutralised
            Assert.True(MonkMode.Blocker.AnySlotArmed());
            Assert.True(MacValid());                                        // and it was not re-stamped over
        }
        finally { Wipe(); }
    }
}
// ---- LEDGER 319: `unblock`'s option surface, which is now the whole exit surface ----
//
// DoUnblock is Private (like every CLI verb body), so what is drivable from a unit test is the
// list it validates against and the shared typo detector it feeds. That is enough to pin the
// property that matters: `--force` and `--cancel` are ABSENT from the accepted set, so they land
// in UnknownOptions and the CLI reports them as options that do not exist. They were deliberately
// NOT kept as accepted-and-ignored flags - a flag that used to tear a block down and now silently
// succeeds is the worst of both worlds.
public class UnblockOptionSurfaceTests
{
    [Fact]
    public void UnblockAccepts_OnlyIdAndCode()
    {
        Assert.Equal(new[] { "--id", "--code" }, MonkMode.Program.UnblockOptionNames());
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--FORCE")]
    [InlineData("--cancel")]
    [InlineData("--force=yes")]
    public void TheRetiredFlags_AreReportedAsUnknown_NotSilentlyIgnored(string flag)
    {
        var unknown = MonkMode.Program.UnknownOptions(new[] { "unblock", flag },
                                                      MonkMode.Program.UnblockOptionNames());
        Assert.Single(unknown);
        // A "--flag=value" form is reported by its head, so the user sees the flag they typed.
        Assert.Equal(flag.Split('=')[0], unknown[0]);
    }

    [Fact]
    public void TheTwoRealFlags_AreNotReportedAsUnknown()
    {
        Assert.Empty(MonkMode.Program.UnknownOptions(new[] { "unblock", "--id", "2", "--code", "ABCD-EFGH" },
                                                     MonkMode.Program.UnblockOptionNames()));
    }

    [Fact]
    public void BlockStillAccepts_TheInertCommitAndCoolOffFlags()
    {
        // The other half of the compatibility decision. `--commit` and `--cooloff` no longer DO
        // anything (every block is committed; there is no cooling-off), but they are still
        // ACCEPTED, so an old script or a habit-typed invocation keeps working and draws no
        // "unknown flag" warning. Only the flags that used to END a block were made unknown.
        Assert.Contains("--commit", MonkMode.Program.BlockOptionNames());
        Assert.Contains("--cooloff", MonkMode.Program.BlockOptionNames());
        Assert.Empty(MonkMode.Program.UnknownOptions(new[] { "block", "--commit", "--cooloff", "2h" },
                                                     MonkMode.Program.BlockOptionNames()));
        // ...and `--force` is not smuggled in through the `block` list either.
        Assert.DoesNotContain("--force", MonkMode.Program.BlockOptionNames());
    }
}
