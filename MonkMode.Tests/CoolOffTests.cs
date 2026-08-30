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

// MonkMode.Tests - LEDGER 319 (30/08/2026): THE COOLING-OFF EXIT IS GONE. THIS FILE PROVES IT.
//
// WHAT THIS FILE USED TO BE. C2b's cooling-off was the self-serve early exit: `monkmode unblock`
// dropped a presence-only trigger, the SERVICE computed a MAC-covered deadline
// CoolOffUntil = HighWater_at_request + max(duration, floor), counted it down against the B4
// monotonic mark, and lifted the block via the same stopMe() as natural expiry. This file pinned
// every part of that - the compile-time floor, ComputeCoolOffDeadline, the ClassifyCoolOffSignal
// request/cancel matrix, and end-to-end countdowns through the real B4 gates.
//
// WHY IT IS GONE. Samrath, 30/08/2026: "i dont like how i can force unblock it regardless ...
// i should only be able to unblock with code." Cooling-off was one of exactly two exits that
// needed no partner code (the other, `unblock --force`, went in the same slice). A block now
// ends on its end time or on a service-verified partner code. Nothing else.
//
// WHAT THIS FILE PINS NOW - that the removal is a REMOVAL, not a disconnection. The follow-up
// slice (same day) went one step further than F79: F79 hard-wired CoolOffElapsedTime to False
// and left it wired into the gates; the follow-up DELETED the function and the coolOffElapsed /
// coolOffUntilText parameters from EffectiveExit / ClassifyHeartbeat / ClassifySlot in BOTH
// assemblies. So the assertion is no longer "the gate ignores the field" but the stronger
// "no exit gate can be handed the field at all":
//   - the exit gates take no cooling-off argument, in either copy. That is compile-enforced,
//     which is why the old CoolOffElapsedTimeTests choke-point class is gone with it.
//   - a slot carrying a forged, already-elapsed CoolOffUntil under a VALID MAC still classifies
//     Hold, not Retire, through the real per-slot gate - after a full honest countdown well
//     past the old deadline. SlotState still carries the field (it mirrors the MAC-covered slot
//     record), so this remains a live, feedable proof rather than a vacuous one.
//   - the writer side is unchanged and still MAC-covers CoolOffDuration (the field stays in the
//     v12 canonical - removing it would mean a schema bump and a four-copy parity edit while
//     v12 is mid-deploy), and `--cooloff` still parses so old invocations do not start failing.
//
// Deleted with the mechanism: CoolOffConstTests' floor pins, ComputeCoolOffDeadlineTests and
// ClassifyCoolOffSignalTests - all three tested functions that no longer exist. The trigger
// DISPOSAL (a stale monkmode_cooloff.* file is now unaddressed junk the tick's
// PurgeUnaddressedTriggers deletes unread) is pinned in TriggerJunkTests and SlotRetireTests.
//
// One thing this file deliberately does NOT pin, because it is not reachable from a unit test:
// that `monkmode block` now writes Committed="yes" and an empty CoolOffDuration on every arm.
// That policy lives in Program.DoBlock, which is Private - the same smoke-tested seam every
// other CLI verb body sits behind.

using System.Globalization;
using System.IO;

namespace MonkMode.Tests;

public class CoolOffConstTests
{
    [Fact]
    public void TriggerFileNames_AreStable_AndParityAcrossCliAndService()
    {
        // Kept even though nothing WRITES these files any more: both sides still need the same
        // names so the tick's sweep finds and deletes a stale one left by an older dist
        // (EnumerateTriggerFilesIn still globs them; TriggerAddressesAnyFamily no longer claims
        // them, so PurgeUnaddressedTriggers bins them). A drift would strand those files on
        // disk, where they would occupy the shared per-tick trigger budget for ever.
        Assert.Equal("monkmode_cooloff.request", MonkMode.Blocker.CoolOffRequestFileName);
        Assert.Equal("monkmode_cooloff.cancel", MonkMode.Blocker.CoolOffCancelFileName);
        Assert.Equal(MonkMode.Blocker.CoolOffRequestFileName, monkmode.Service1.CoolOffRequestFileName);
        Assert.Equal(MonkMode.Blocker.CoolOffCancelFileName, monkmode.Service1.CoolOffCancelFileName);
    }
}

// Ledger 319 follow-up: CoolOffElapsedTimeTests stood here. It was the choke-point class -
// a 28-row table proving Service1.CoolOffElapsedTime and its guardian twin returned False for
// every deadline/mark shape, including the two rows (deadline < mark, deadline == mark) that
// used to return True. Both functions are now DELETED, so there is nothing left to feed: a
// class whose whole subject is an absent function is not a weaker test, it is no test. What it
// was really protecting - "a forged, elapsed CoolOffUntil never ends a block" - is pinned where
// the field can still actually be supplied, on the per-slot gate in CoolOffEndToEndTests.
//
// Ledger 319: ComputeCoolOffDeadlineTests and ClassifyCoolOffSignalTests stood here. They
// pinned Service1.ComputeCoolOffDeadline (the floor-clamped deadline the service wrote on a
// request) and Service1.ClassifyCoolOffSignal (the 32-row request/cancel/pending/committed/
// macValid matrix that decided Start / Cancel / Ignore). Both functions - and the
// CoolOffAction enum, ParseConfiguredCoolOffSeconds and MinCoolOffFloorSeconds with them -
// were DELETED with the cooling-off exit: with no trigger reader there is no signal to
// classify and no deadline to compute. Nothing anywhere can write a slot's CoolOffUntil now.

public class EffectiveExitTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private static readonly string HwText = Hw.ToString(EnCa);
    private static readonly string PastUntil = Hw.AddHours(-1).ToString(EnCa);
    private static readonly string FutureUntil = Hw.AddHours(5).ToString(EnCa);

    // C3b: a non-empty UnlockedAt = code-unlocked (any non-empty string; here a
    // representative datetime). "" = not unlocked.
    private static readonly string Unlocked = Hw.ToString(EnCa);
    // C5b: a schedule ActiveUntil in the future (an OPEN window - SD1 hard hold) and one
    // already elapsed (a CLOSED window - inert). "" = no window; "garbage" = fail-closed
    // active. Measured against HwText, like the cooling-off deadlines above.
    private static readonly string ScheduleOpen = Hw.AddMinutes(30).ToString(EnCa);
    private static readonly string ScheduleClosed = Hw.AddMinutes(-5).ToString(EnCa);

    // FLIPPED BY LEDGER 319, and this is the single most important assertion in the file.
    // It used to read CoolOffElapsed_WithValidMac_Exits_EvenThoughUntilIsFuture - "the whole
    // point of cooling-off: the block ends BEFORE Until". That is exactly the behaviour Samrath
    // asked to be rid of. The follow-up slice removed the parameter, so a deadline can no longer
    // even be handed to this gate; what survives here is the positive half - a MAC-valid block
    // with a future Until and no code does NOT exit, and still ends normally at its own Until.
    // The "a forged elapsed deadline changes nothing" half moved to the per-slot gate, where the
    // field can still be supplied (CoolOffEndToEndTests.APerSlotElapsedDeadline_...).
    [Fact]
    public void AFutureUntil_WithValidMac_DoesNotExit_AndAPastOneStillDoes()
    {
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", HwText, 5, macValid: true, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, "", "", HwText, 5, macValid: true, scheduleArmed: false));

        // ...and the block still ends normally at its own Until, so nothing else was broken.
        Assert.True(monkmode.Service1.EffectiveExit(PastUntil, "", "", HwText, 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.EffectiveExit(PastUntil, "", "", HwText, 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void APastUntil_WithInvalidMac_NeverExits()
    {
        // A tampered config freezes: not even a genuinely elapsed Until lifts it.
        Assert.False(monkmode.Service1.EffectiveExit(PastUntil, "", "", HwText, 5, macValid: false, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(PastUntil, "", "", HwText, 5, macValid: false, scheduleArmed: false));
    }

    // C3b: a partner-code UnlockedAt exits under a valid MAC even with a FUTURE
    // Until - the partner-code arm of EffectiveExit, and now the only early one.
    [Fact]
    public void CodeUnlocked_WithValidMac_Exits_EvenThoughUntilIsFuture()
    {
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, Unlocked, "", HwText, 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, Unlocked, "", HwText, 5, macValid: true, scheduleArmed: false));
    }

    // C3b (R6): a tampered config can't code-unlock its way out (freeze).
    [Fact]
    public void CodeUnlocked_WithInvalidMac_NeverExits()
    {
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, Unlocked, "", HwText, 5, macValid: false, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, Unlocked, "", HwText, 5, macValid: false, scheduleArmed: false));
    }

    // C3b: a code-unlock is TIME-FREE - it exits even when HighWater is unparseable
    // (unlike the expiry arm, which fails closed on a bad mark). A non-empty
    // UnlockedAt under a valid MAC is authoritative regardless of the clock.
    [Fact]
    public void CodeUnlocked_ExitsEvenWithUnparseableHighWater()
    {
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, Unlocked, "", "garbage", 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, Unlocked, "", "", 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void NoCode_ReducesToPlainExpiry()
    {
        // With no code-unlock, EffectiveExit == expiry.
        Assert.True(monkmode.Service1.EffectiveExit(PastUntil, "", "", HwText, 5, macValid: true, scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", HwText, 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void UnparseableHighWater_NeverExits_FailClosed()
    {
        // No trustworthy mark and no code-unlock: expiry reads off MinValue (not expired).
        Assert.False(monkmode.Service1.EffectiveExit(PastUntil, "", "", "garbage", 5, macValid: true, scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(PastUntil, "", "", "", 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void ServiceAndGuardian_AgreeAcrossTheTruthTable()
    {
        // The pair must never disagree on "may end", or one side stands down /
        // lifts while the other still enforces - the resurrection bug. Over the
        // unlockedAt (C3b), scheduleActiveUntil (C5b, SD1) AND scheduleArmed (C5b, c2)
        // dimensions too - the two EffectiveExit BODIES are byte-parity, so passing the
        // SAME scheduleArmed to both must always agree (the exact-vs-over-approx difference
        // is in the CALLERS, pinned separately in ScheduleTests).
        // The coolOff axis is gone with the parameter; every other row is kept.
        foreach (var until in new[] { PastUntil, FutureUntil, "garbage", "" })
            foreach (var unlocked in new[] { "", Unlocked })
                foreach (var schedule in new[] { "", ScheduleOpen, ScheduleClosed, "garbage" })
                    foreach (var hw in new[] { HwText, "garbage", "" })
                        foreach (var mac in new[] { true, false })
                            foreach (var armed in new[] { true, false })
                                Assert.Equal(
                                    monkmode.Service1.EffectiveExit(until, unlocked, schedule, hw, 5, mac, armed),
                                    mm_guard.Guardian.EffectiveExit(until, unlocked, schedule, hw, 5, mac, armed));
    }

    [Fact]
    public void HeartbeatLift_And_EffectiveExit_AreTheSameDecision()
    {
        // The tick heartbeat lifts via ClassifyHeartbeat; OnStart and the
        // guardian go through EffectiveExit. Pin that the two formulations can
        // never drift: Lift <=> EffectiveExit, over the whole input table
        // including the code-unlock (C3b), schedule-hold (C5b, SD1) and armed-
        // between-windows (C5b, c2) arms.
        foreach (var until in new[] { PastUntil, FutureUntil, "garbage", "" })
            foreach (var unlocked in new[] { "", Unlocked })
                foreach (var schedule in new[] { "", ScheduleOpen, ScheduleClosed, "garbage" })
                    foreach (var mac in new[] { true, false })
                        foreach (var armed in new[] { true, false })
                        {
                            var lift = monkmode.Service1.ClassifyHeartbeat(
                                mac,
                                monkmode.Service1.BlockHasExpired(until, Hw, 5),
                                monkmode.Service1.PartnerUnlocked(unlocked),
                                monkmode.Service1.ScheduleActive(schedule, HwText),
                                armed)
                                == monkmode.Service1.HeartbeatAction.Lift;
                            Assert.Equal(monkmode.Service1.EffectiveExit(until, unlocked, schedule, HwText, 5, mac, armed), lift);
                        }
    }
}

// End-to-end through the REAL B4 gates, REWRITTEN BY LEDGER 319. What stood here was the
// cooling-off countdown proved against the live tick's own pair (NextHighWater +
// CapHighWaterAdvance): request -> 360 honest ticks -> Lift, plus the never-skip properties
// (a forward clock jump, creep and a backward roll could none of them reach the deadline
// early, and a reboot resumed off the stored mark).
//
// Every one of those tests ended in a LIFT that needed no partner code, which is the thing
// this slice removed. They are replaced by the inverse, run through the same real gates: the
// full honest countdown, well past the old deadline, and the block is still standing.
public class CoolOffEndToEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private const long Ceiling = 120;  // Service1.HighWaterJumpCeilingSeconds (pinned elsewhere)
    private const long Floor = 3600;   // the cooling-off floor that used to exist
    private static readonly DateTime T0 = new(2026, 6, 25, 12, 0, 0);
    private static readonly string FutureUntil = T0.AddHours(8).ToString(EnCa);

    // One honest 10s tick: wall now = mark + 10s, real monotonic elapsed = 10s.
    private static string HonestTick(string hw, DateTime wallNow) =>
        monkmode.Service1.CapHighWaterAdvance(
            hw, monkmode.Service1.NextHighWater(hw, wallNow.ToString(EnCa), Ceiling), 10);

    // The deadline the service USED to compute on a request, reconstructed here as a literal
    // because ComputeCoolOffDeadline is deleted: HighWater_at_request + the floor. This is the
    // most favourable possible input to the old lift path, and it is what the tests feed it.
    private static string OldStyleDeadline(DateTime at) => at.AddSeconds(Floor).ToString(EnCa);

    // A slot carrying the deadline the service USED to write, and nothing else unusual.
    private static monkmode.Service1.SlotState SlotWithOldDeadline(string untilText, string unlockedAt = "") =>
        new()
        {
            Id = "1",
            UntilText = untilText,
            CoolOffUntil = OldStyleDeadline(T0),
            PartnerUnlockedAt = unlockedAt,
            Committed = "yes",
        };

    [Fact]
    public void TheFullCountdown_PastTheOldDeadline_StillNeverLifts()
    {
        // The exact scenario that used to end a block with no code: a deadline written at T0,
        // then honest ticking until well past it. 720 ticks = 2 hours, double the old floor.
        // The countdown is driven by the live tick's own real pair (NextHighWater +
        // CapHighWaterAdvance), so the mark this ends on is genuine on-machine time, not a
        // literal - and the slot is fed the old deadline through the one gate that still
        // accepts it, SlotExitDue.
        var hw = T0.ToString(EnCa);
        var slot = SlotWithOldDeadline(FutureUntil);

        for (var i = 1; i <= 720; i++)
        {
            hw = HonestTick(hw, T0.AddSeconds(i * 10));
            Assert.Equal(monkmode.Service1.SlotAction.Hold,
                monkmode.Service1.SlotExitDue(slot, DateTime.Parse(hw, EnCa), 5, true, hw));
        }

        // Two hours of genuine on-machine time past the deadline, on a MAC-VALID config, and
        // every gate still says the block is enforced. Restamp, not Lift.
        Assert.Equal(monkmode.Service1.HeartbeatAction.Restamp,
            monkmode.Service1.ClassifyHeartbeat(true,
                monkmode.Service1.BlockHasExpired(FutureUntil, DateTime.Parse(hw, EnCa), 5),
                false, false, scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", hw, 5, macValid: true, scheduleArmed: false));
        Assert.False(monkmode.Service1.SlotEffectiveExit(slot, hw, 5, macValid: true));

        // ...and the guardian keeps guarding rather than standing down.
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, "", "", hw, 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.ShouldRestartService(
            blockActive: !mm_guard.Guardian.EffectiveExit(FutureUntil, "", "", hw, 5, macValid: true, scheduleArmed: false),
            serviceRunning: false));
    }

    [Fact]
    public void TheBlockStillEndsNormally_AtItsOwnUntil()
    {
        // The control, so "nothing lifts" cannot pass for the wrong reason: with the SAME
        // elapsed deadline present on the slot, the block still ends when its own timer runs out.
        var pastUntil = T0.AddHours(-1).ToString(EnCa);
        var hw = T0.ToString(EnCa);

        Assert.Equal(monkmode.Service1.SlotAction.Retire,
            monkmode.Service1.SlotExitDue(SlotWithOldDeadline(pastUntil), T0, 5, true, hw));
        Assert.True(monkmode.Service1.EffectiveExit(pastUntil, "", "", hw, 5, macValid: true, scheduleArmed: false));
        Assert.Equal(monkmode.Service1.HeartbeatAction.Lift,
            monkmode.Service1.ClassifyHeartbeat(true,
                monkmode.Service1.BlockHasExpired(pastUntil, T0, 5),
                false, false, scheduleArmed: false));
    }

    [Fact]
    public void ThePartnerCodeStillLifts_WithTheSameElapsedDeadlinePresent()
    {
        // The other control, and the exit that is now the ONLY early one: a service-verified
        // UnlockedAt lifts a block whose Until is still hours away - unchanged by this slice,
        // and unchanged by the slot carrying the old deadline alongside it.
        var hw = T0.ToString(EnCa);
        var unlockedAt = T0.AddMinutes(1).ToString(EnCa);

        Assert.Equal(monkmode.Service1.SlotAction.Retire,
            monkmode.Service1.SlotExitDue(SlotWithOldDeadline(FutureUntil, unlockedAt), T0, 5, true, hw));
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, unlockedAt, "", hw, 5, macValid: true, scheduleArmed: false));
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, unlockedAt, "", hw, 5, macValid: true, scheduleArmed: false));
    }

    [Fact]
    public void APerSlotElapsedDeadline_DoesNotRetireTheSlot()
    {
        // The per-slot gate, which is what actually retires a block on the live tick, and the
        // one place a CoolOffUntil can still be fed in at all: a slot carrying an elapsed
        // deadline under a VALID MAC must classify Hold, not Retire.
        var hw = T0.AddHours(3).ToString(EnCa);   // long past the old deadline
        Assert.Equal(monkmode.Service1.SlotAction.Hold,
            monkmode.Service1.SlotExitDue(SlotWithOldDeadline(FutureUntil), DateTime.Parse(hw, EnCa), 5, true, hw));
    }
}

// C1b composition: the shadow backup must CARRY the cooling-off deadline, so a
// corrupt-then-restore mid-cooling-off resumes the SAME wait (never an early
// lift, never a lost deadline). The copy layer is byte-exact (CopyIfSourceValid
// + AtomicHosts), so this pins the property end-to-end against temp files with
// the MAC gate boolean-injected, exactly like ConfigBackupTests.
public class CoolOffBackupCarryTests
{
    [Fact]
    public void CorruptPrimaryMidCoolOff_RestoredFromBackup_CarriesTheDeadline()
    {
        var ca = new CultureInfo("en-CA");
        var dir = Directory.CreateTempSubdirectory("mm_cooloff_bak_");
        try
        {
            var primary = Path.Combine(dir.FullName, "monkmode_settings.ini");
            var backup = Path.Combine(dir.FullName, MonkMode.ConfigBackup.BackupFileName);
            var enc = new MonkMode.Simple3Des("mm_textbox");
            var deadline = new DateTime(2026, 6, 25, 13, 0, 0).ToString(ca);

            var ini = new MonkMode.IniFile();
            // v10: the cooling-off deadline is PER-SLOT ([Slot1] CoolOffUntil), still an
            // encrypted datetime. The un-MAC'd housekeeping key [Time] TimeChanging stays.
            OneSlot.WriteSlot1((sec, k, v) => ini.SetKeyValue(sec, k, v),
                enc.EncryptData(new DateTime(2026, 12, 31, 18, 0, 0).ToString(ca)), "", "reddit.com;",
                enc.EncryptData(new DateTime(2026, 6, 25, 12, 0, 0).ToString(ca)),
                enc.EncryptData(new DateTime(2026, 6, 25, 12, 0, 0).ToString(ca)),
                enc.EncryptData(deadline), "", "", "", "no", "", "", "", "");
            ini.SetKeyValue("Time", "TimeChanging", "no");
            ini.Save(primary);

            // The refresh a cooling-off write performs (MAC validity injected, as
            // the service's live gate would report for its just-restamped save).
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(primary, backup, true));

            var before = new MonkMode.IniFile();
            before.Load(primary);
            var canonicalBefore = MonkMode.Blocker.CanonicalFromIni(before);

            // Corrupt the primary mid-cooling-off, then restore from the backup.
            File.WriteAllText(primary, "garbage - not an ini");
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(backup, primary, true));

            // The restored primary carries the SAME deadline and derives the SAME
            // canonical (so the original MAC still validates - no freeze, no
            // reset, and definitely no early lift).
            var restored = new MonkMode.IniFile();
            restored.Load(primary);
            Assert.Equal(deadline, enc.DecryptData(restored.GetKeyValue("Slot1", "CoolOffUntil")));
            Assert.Equal(canonicalBefore, MonkMode.Blocker.CanonicalFromIni(restored));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

// Ledger 319: ConfiguredCoolOffDurationTests stood here, pinning
// Service1.ParseConfiguredCoolOffSeconds - the interpreter that turned the stored [CoolOff]
// Duration into a wait, with the floor clamp that stopped an attacker-set 0 shortening it. The
// function is deleted with the wait it sized. The FIELD is still written and still MAC-covered
// (see CoolOffWriteConfigTests below and CanonicalParityTests) because it stays in the v12
// canonical; it simply has no consumer left anywhere in the four assemblies.

// C6b: the CLI arm path (`block --cooloff`) writes [CoolOff] Duration MAC-covered from birth.
// Uses the REAL Blocker.WriteConfig (into the test bin dir), then loads the ini back and checks
// the field is stored + carried into every reader's canonical. DPAPI is NOT exercised (we
// compare canonicals, which are DPAPI-free) - the same fence as CliWriteConfig_ProducesAnIni.
// Serialised with the other CLI ini writers so it never races the shared monkmode_settings.ini.
[Collection("CliIniWriters")]
public class CoolOffWriteConfigTests
{
    [Fact]
    public void WriteConfig_WithCoolOff_StoresTheDurationAndEveryReaderCarriesIt()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, new[] { "chrome.exe" }, until, committed: false, coolOffSeconds: 7200);

            var cliIni = new MonkMode.IniFile();
            cliIni.Load(iniPath);
            // Stored plaintext seconds under [CoolOff] Duration...
            Assert.Equal("7200", cliIni.GetKeyValue("CoolOff", "Duration"));
            // ...and the value is MAC-COVERED, on the v10 per-slot canonical the arm path has
            // written since S2 (v1.1) - a raw edit to shorten the wait fails the MAC and every
            // reader freezes. The v9 [CoolOff] Duration mirror checked above is still written
            // for the enforcement readers until S3a moves them onto the slots.
            var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
            Assert.Contains("Slot1.CoolOffDuration=7200\n", cli);

            var srvIni = new monkmode.IniFile();
            srvIni.Load(iniPath);
            var guardIni = new mm_guard.IniFile();
            guardIni.Load(iniPath);
            var notifyIni = new mm_notify.IniFile();
            notifyIni.Load(iniPath);
            Assert.Equal(cli, MonkMode.Tests.TestSvc.New().CanonicalFromIni(srvIni));
            Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            var backupPath = MonkMode.Blocker.IniBackupPath();
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Fact]
    public void WriteConfig_WithoutCoolOff_LeavesTheDurationEmpty_TheFloorDefault()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, new[] { "chrome.exe" }, until);

            var cliIni = new MonkMode.IniFile();
            cliIni.Load(iniPath);
            // No --cooloff => no [CoolOff] Duration written (absent => "" => the service's
            // compile-time floor default). Use IsNullOrEmpty (absent reads "", a blanked key
            // reads Nothing - the recurring ini round-trip quirk).
            Assert.True(string.IsNullOrEmpty(cliIni.GetKeyValue("CoolOff", "Duration")));
            // ...and the slot carries an EMPTY CoolOffDuration - MAC-covered as empty, so
            // "no configured wait, use the floor" is itself protected: a raw edit that
            // invents a duration fails the MAC. (16 per-slot keys are always emitted, "" when
            // unset - an absent key could otherwise shorten one config's canonical into
            // another's.)
            Assert.Contains("Slot1.CoolOffDuration=\n", MonkMode.Blocker.CanonicalFromIni(cliIni));
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            var backupPath = MonkMode.Blocker.IniBackupPath();
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}

// C6c: the shared --cooloff argument parser (Program.TryParseCoolOffArg) that drives DoBlock's
// override(>0)/inherit(0) decision and DoSetup's account-default store. Pins the contract both
// verbs rely on: a valid value passes through as seconds (the OVERRIDE), an ABSENT --cooloff yields
// (True, 0) so the caller applies its own default (block inherits the account default; setup stores
// none), and an unparseable/too-long value returns False with seconds reset to 0 (the verb then
// exits 1, no partial state). Pure arg parsing - no file I/O; the reject paths write a friendly
// error to Console.Error, redirected here so the test output stays clean.
public class CoolOffArgParseTests
{
    private static bool Parse(string[] args, out long seconds)
    {
        var prevErr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            long s = -1;
            var ok = MonkMode.Program.TryParseCoolOffArg(args, ref s);
            seconds = s;
            return ok;
        }
        finally { Console.SetError(prevErr); }
    }

    [Theory]
    [InlineData("2h", 7200)]
    [InlineData("90m", 5400)]
    [InlineData("45", 2700)]          // bare number = minutes
    [InlineData("365d", 31536000)]    // exactly MaxCoolOffSeconds is accepted (the cap boundary)
    public void ValidValue_PassesThroughAsSeconds_TheOverride(string arg, long expected)
    {
        Assert.True(Parse(new[] { "--cooloff", arg }, out var s));
        Assert.Equal(expected, s);    // >0 => DoBlock uses THIS, not the account default
    }

    [Fact]
    public void AbsentCoolOff_YieldsTrueAndZero_SoTheCallerInherits()
    {
        // No --cooloff token at all => (True, 0): DoBlock then inherits the account default,
        // DoSetup stores no default. 0 is the unambiguous "absent" signal (a valid value is always >0).
        Assert.True(Parse(new[] { "block", "--sites", "reddit.com", "--for", "2h" }, out var s));
        Assert.Equal(0L, s);
    }

    [Fact]
    public void ValuelessCoolOff_ReadsAsAbsent_TrueAndZero()
    {
        // "--cooloff" as the trailing token (no value) reads as absent (GetOption => "") => (True, 0):
        // treated as unset (the documented C6b behaviour), NOT an error.
        Assert.True(Parse(new[] { "block", "--cooloff" }, out var s));
        Assert.Equal(0L, s);
    }

    [Theory]
    [InlineData("xyz")]     // unparseable
    [InlineData("2x")]
    [InlineData("366d")]    // above the 365d cap
    [InlineData("400d")]
    public void UnparseableOrTooLong_ReturnsFalseAndZero_TheVerbExits1(string arg)
    {
        Assert.False(Parse(new[] { "--cooloff", arg }, out var s));
        Assert.Equal(0L, s);   // seconds reset to 0 on reject - never a partial value leaks through
    }
}
