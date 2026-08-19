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

// MonkMode.Tests - v1.1 S3b: RETIRE ONE SLOT WITHOUT A GLOBAL TEARDOWN.
//
// WHY THIS SLICE EXISTS. S3a moved WHAT is blocked and WHETHER THE MACHINERY STAYS UP onto
// the MAC-covered slots. What had NOT moved was WHETHER THE BLOCK ENDS: ClassifyHeartbeat
// and EffectiveExit still adjudicated the lift over the v9 mirror keys - [Time] Until,
// [Time] CoolOffUntil, [Partner] UnlockedAt, [Schedule] ActiveUntil/Spec - and not one of
// those is inside the v10 canonical. Back-dating [Time] Until in a text editor therefore
// left macValid TRUE and tore the WHOLE machine down, with no tamper evidence at all. That
// is the hole these tests close, and it is why the tree carried a DO-NOT-ARM warning.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - the CLASSIFIER SPLIT. ClassifySlot decides one slot's fate off that slot's own fields;
//     ClassifyTick decides the machine's. The v9 residual's Lift is demoted to a NECESSARY
//     condition for teardown and is never sufficient - so a raw v9 edit can now only ever
//     OVER-block.
//   - Retire <=> SlotEffectiveExit, derived two INDEPENDENT ways (via ClassifyHeartbeat's
//     Lift arm, and via EffectiveExit's own body) so the pair cannot drift apart the way a
//     hand-copied second implementation would.
//   - the P38 RETIRE ORDER: config -> snapshot recomputed from config truth -> hosts. Every
//     crash point OVER-blocks, and each one converges on the next tick. Snapshot-before-
//     config is what would UNDER-block, so the ordering is asserted, not assumed.
//   - the P39 TEARDOWN ORDER, which deliberately INVERTS the pre-S3b strip-then-mark: a
//     crash between the zero-slot config and the hosts strip leaves hosts BLOCKED, and the
//     next tick re-runs the teardown. The old order's crash window let the B2 self-heal
//     resurrect a torn-down block from the snapshot.
//   - teardown fires ONLY at SlotCount = 0, and PENDING counts - so it can never eat a
//     block that has not started yet.
//   - P29 PENDING -> ACTIVE: the SERVICE computes Until = HighWater + DurationSeconds. An
//     absolute until stored at arm time would UNDER-block after downtime. And activation
//     never removes the slot from the hosts union for even one tick (the S3a P1 trap).
//   - P40: a slot-addressed code unlocks ONLY its own slot; an unknown/retired id deletes
//     the trigger and changes nothing, with no freeze.
//
// Fences honoured: every write goes to the test-bin ini/backup/snapshot (wiped in finally)
// or to a GUID temp directory under the test bin directory. Never the real hosts file, the
// registry, the SCM, Program Files or any deployed config. Nothing is ever armed for real,
// and no service, CLI or notifier binary is executed.

using System.Globalization;

namespace MonkMode.Tests;

// ---- 1. the pure classifier split ----

public class SlotClassifierTests
{
    private const long Grace = 5;
    private const string Hw = "2026-08-12 12:00:00";
    private static readonly DateTime AsOf = new(2026, 8, 12, 12, 0, 0);
    private const string Future = "2026-08-12 18:00:00";
    private const string Past = "2026-08-12 06:00:00";

    private static monkmode.Service1.SlotState Slot(string id = "1", string until = "", string startAt = "",
                                                    string coolOffUntil = "", string unlockedAt = "",
                                                    string scheduleActiveUntil = "", string scheduleSpec = "",
                                                    string committed = "")
        => new()
        {
            Id = id,
            UntilText = until,
            StartAt = startAt,
            CoolOffUntil = coolOffUntil,
            PartnerUnlockedAt = unlockedAt,
            ScheduleActiveUntil = scheduleActiveUntil,
            ScheduleSpec = scheduleSpec,
            Committed = committed,
        };

    private static monkmode.Service1.SlotAction Due(monkmode.Service1.SlotState s, bool macValid = true)
        => monkmode.Service1.SlotExitDue(s, AsOf, Grace, macValid, Hw);

    [Fact]
    public void ExpiredSlot_Retires_AndALiveOneHolds()
    {
        Assert.Equal(monkmode.Service1.SlotAction.Retire, Due(Slot(until: Past)));
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(until: Future)));
    }

    [Fact]
    public void InvalidMac_HoldsEverySlot_NeverRetires()
    {
        // The B7 freeze. A tampered config must never retire a slot - retiring is a write,
        // and re-stamping over unverified bytes is the original fail-open bug.
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(until: Past), macValid: false));
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(coolOffUntil: Past), macValid: false));
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(unlockedAt: "2026-08-12 11:00:00"), macValid: false));
    }

    [Fact]
    public void UnparseableUntil_IsNotExpired_SoTheSlotHolds()
    {
        // Fail-closed by inheritance from BlockHasExpired: "we could not read the end" can
        // never mean "the block is over".
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(until: "not-a-date")));
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(until: "")));
    }

    [Fact]
    public void CoolingOffElapsed_AndCodeUnlock_EachRetireTheSlotOnTheirOwn()
    {
        Assert.Equal(monkmode.Service1.SlotAction.Retire, Due(Slot(until: Future, coolOffUntil: Past)));
        Assert.Equal(monkmode.Service1.SlotAction.Retire, Due(Slot(until: Future, unlockedAt: "2026-08-12 11:00:00")));
        // A cooling-off deadline still ahead of the trusted mark holds.
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(until: Future, coolOffUntil: Future)));
    }

    [Fact]
    public void PendingSlot_NeverRetiresOnTime_ButItsAuthorisedExitsStillWork()
    {
        // A `--start` slot has no Until at all, so "no recorded end" must not read as over.
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(Slot(startAt: Future)));
        // ...but a partner code or a completed cooling-off must still cancel it: a block you
        // scheduled for tomorrow has to be cancellable by the exits you were promised.
        Assert.Equal(monkmode.Service1.SlotAction.Retire, Due(Slot(startAt: Future, unlockedAt: "2026-08-12 11:00:00")));
        Assert.Equal(monkmode.Service1.SlotAction.Retire, Due(Slot(startAt: Future, coolOffUntil: Past)));
    }

    [Fact]
    public void OpenWindow_OutranksEveryExitReason()
    {
        // SD1 inherited verbatim: while a window is open NOTHING retires the slot.
        var s = Slot(until: Past, coolOffUntil: Past, unlockedAt: "2026-08-12 11:00:00", scheduleActiveUntil: Future);
        Assert.Equal(monkmode.Service1.SlotAction.Hold, Due(s));
    }

    [Theory]
    // The whole matrix, derived TWO independent ways: ClassifySlot routes through
    // ClassifyHeartbeat's Lift arm, SlotEffectiveExit through EffectiveExit's own body. If
    // either is ever hand-edited without the other, this fails.
    [InlineData(true, "", "", "", "", "")]
    [InlineData(true, Past, "", "", "", "")]
    [InlineData(true, Future, "", "", "", "")]
    [InlineData(true, Future, Past, "", "", "")]
    [InlineData(true, Future, "", "2026-08-12 11:00:00", "", "")]
    [InlineData(true, Past, "", "", Future, "")]
    [InlineData(true, Past, "", "", "", "v2 1111100 0900-1700 sites=a.com")]
    [InlineData(false, Past, Past, "2026-08-12 11:00:00", "", "")]
    [InlineData(true, "not-a-date", "", "", "", "")]
    public void RetireIsExactlySlotEffectiveExit(bool macValid, string until, string coolOff, string unlockedAt, string windowUntil, string spec)
    {
        var s = Slot(until: until, coolOffUntil: coolOff, unlockedAt: unlockedAt,
                     scheduleActiveUntil: windowUntil, scheduleSpec: spec);
        var retires = monkmode.Service1.SlotExitDue(s, AsOf, Grace, macValid, Hw) == monkmode.Service1.SlotAction.Retire;
        Assert.Equal(monkmode.Service1.SlotEffectiveExit(s, Hw, Grace, macValid), retires);
    }

    [Fact]
    public void SlotEffectiveExit_OfNothing_IsNotAnExit()
    {
        Assert.False(monkmode.Service1.SlotEffectiveExit(null, Hw, Grace, true));
        Assert.Equal(monkmode.Service1.SlotAction.Hold, monkmode.Service1.SlotExitDue(null, AsOf, Grace, true, Hw));
    }
}

// ---- 2. ClassifyTick: teardown fires only when nothing remains ----

public class ClassifyTickTests
{
    private static monkmode.Service1.TickAction Tick(bool macValid, int remaining, monkmode.Service1.HeartbeatAction residual)
        => monkmode.Service1.ClassifyTick(macValid, remaining, residual);

    [Fact]
    public void InvalidMac_Holds_RegardlessOfEverythingElse()
    {
        Assert.Equal(monkmode.Service1.TickAction.Hold, Tick(false, 0, monkmode.Service1.HeartbeatAction.Lift));
        Assert.Equal(monkmode.Service1.TickAction.Hold, Tick(false, 3, monkmode.Service1.HeartbeatAction.Hold));
    }

    [Fact]
    public void AnySlotRemaining_NeverTearsDown_EvenWhenTheV9ResidualSaysLift()
    {
        // THE SLICE IN ONE ASSERTION. Back-dating [Time] Until makes the v9 residual read
        // Lift; with slots armed that is now simply ignored, where before it ran stopMe().
        Assert.Equal(monkmode.Service1.TickAction.Restamp, Tick(true, 1, monkmode.Service1.HeartbeatAction.Lift));
        Assert.Equal(monkmode.Service1.TickAction.Restamp, Tick(true, 8, monkmode.Service1.HeartbeatAction.Lift));
    }

    [Fact]
    public void ZeroSlots_ButAHoldingV9Residual_KeepsTheServiceAlive()
    {
        // This is what preserves the v9 schedule-only shape: SlotCount = 0 with an armed
        // [Schedule] Spec makes the residual Restamp (the c2 between-windows hold), so the
        // machine stays up for tomorrow's window instead of tearing down.
        Assert.Equal(monkmode.Service1.TickAction.Restamp, Tick(true, 0, monkmode.Service1.HeartbeatAction.Restamp));
        Assert.Equal(monkmode.Service1.TickAction.Restamp, Tick(true, 0, monkmode.Service1.HeartbeatAction.Hold));
    }

    [Fact]
    public void ZeroSlots_AndAnExitedResidual_IsTheOnlyPathToTeardown()
    {
        Assert.Equal(monkmode.Service1.TickAction.TeardownAll, Tick(true, 0, monkmode.Service1.HeartbeatAction.Lift));
    }
}

// ---- 2b. FX3 (F3): may the [Schedule] residual be cleared? ----

public class ScheduleResidualSpentTests
{
    private const string Hw = "2026-08-12 12:00:00";
    private const string Spec = "v2;12345:0900-1700;sites=x.com;apps=";
    private const string OpenWindow = "2026-08-12 18:00:00";
    private const string ClosedWindow = "2026-08-12 06:00:00";

    private static bool Spent(bool macValid = true, string spec = "", string activeUntil = "", string hw = Hw)
        => monkmode.Service1.ScheduleResidualIsSpent(macValid, spec, activeUntil, hw);

    [Fact]
    public void NothingScheduled_IsSpent_SoTheClearStillHappens()
    {
        // The ONLY state the last-slot retire and the whole-machine teardown ever meet in
        // practice, and the one the pre-FX3 unconditional clear was written for.
        Assert.True(Spent());
        Assert.True(Spent(activeUntil: ClosedWindow));          // a window that already closed
        Assert.True(Spent(spec: "v2;not-a-window;sites=x.com")); // residue that parses to 0 windows
    }

    [Fact]
    public void AnArmedScheduleOrAnOpenWindow_IsNotSpent()
    {
        // F3: retiring a SLOT must never take a live schedule down with it - blanking Spec
        // between windows kills every future window, and blanking ActiveUntil mid-window
        // collapses enforcementHeld and tears the machine down inside the window.
        Assert.False(Spent(spec: Spec));
        Assert.False(Spent(activeUntil: OpenWindow));
        Assert.False(Spent(spec: Spec, activeUntil: OpenWindow));
    }

    [Fact]
    public void AnUnreadableDeadlineOrHighWater_ReadsAsAnOpenWindow_SoNothingIsCleared()
    {
        // Fail-closed by inheritance from ScheduleActive: "we could not read it" can never
        // mean "it is over". A decrypt failure hands the CIPHERTEXT through, which lands here.
        Assert.False(Spent(activeUntil: "kJ8s+garbage=="));
        Assert.False(Spent(activeUntil: OpenWindow, hw: "not-a-date"));
        Assert.False(Spent(activeUntil: OpenWindow, hw: ""));
    }

    [Fact]
    public void AnInvalidMac_CannotMakeAScheduleArmed_ButAnOpenWindowStillHolds()
    {
        // ScheduleArmed is macValid-gated (a tampered Spec is not a schedule), but an open
        // window is judged on the deadline alone - so a frozen config is never the thing that
        // gets its window cleared either.
        Assert.True(Spent(macValid: false, spec: Spec));
        Assert.False(Spent(macValid: false, spec: Spec, activeUntil: OpenWindow));
    }
}

// ---- 3. P29 activation + the trigger channel: pure parts ----

public class SlotActivationAndTriggerNameTests
{
    [Fact]
    public void SlotStartDue_MeasuresAgainstTheTrustedMark_NeverTheWallClock()
    {
        var pending = new monkmode.Service1.SlotState { Id = "1", StartAt = "2026-08-12 12:00:00", DurationSeconds = "3600" };
        Assert.False(monkmode.Service1.SlotStartDue(pending, "2026-08-12 11:59:59"));
        Assert.True(monkmode.Service1.SlotStartDue(pending, "2026-08-12 12:00:00"));
        Assert.True(monkmode.Service1.SlotStartDue(pending, "2026-08-12 13:00:00"));
    }

    [Fact]
    public void SlotStartDue_FailsClosed_OnEveryUnreadableInput()
    {
        // Fail-closed here means "stays PENDING" - and a pending slot is still in every
        // union, so a slot that cannot be activated over-blocks rather than under-blocks.
        var pending = new monkmode.Service1.SlotState { Id = "1", StartAt = "2026-08-12 12:00:00", DurationSeconds = "3600" };
        Assert.False(monkmode.Service1.SlotStartDue(pending, "not-a-date"));
        Assert.False(monkmode.Service1.SlotStartDue(new monkmode.Service1.SlotState { Id = "1", StartAt = "junk" }, "2026-08-12 13:00:00"));
        // An ACTIVE slot (Until already set) is not pending and is never re-activated - that
        // would extend the block by a fresh duration on every tick.
        var active = new monkmode.Service1.SlotState { Id = "1", StartAt = "2026-08-12 12:00:00", UntilText = "2026-08-12 13:00:00", DurationSeconds = "3600" };
        Assert.False(monkmode.Service1.SlotStartDue(active, "2026-08-12 13:00:00"));
        Assert.False(monkmode.Service1.SlotStartDue(null, "2026-08-12 13:00:00"));
    }

    [Fact]
    public void ComputeSlotActivationUntil_IsHighWaterPlusDuration_NotAnAbsoluteStoredEnd()
    {
        // P29: storing an absolute end at arm time would UNDER-block after downtime, because
        // the wall clock runs on while the machine is off. Anchoring on HighWater is what
        // makes "machine off across the window" yield the FULL duration from the resume.
        // The stored SHAPE is en-CA, byte-identical to ComputeCoolOffDeadline's - the two
        // service-computed deadlines must be written and re-parsed the same way.
        var enCa = new CultureInfo("en-CA");
        Assert.Equal(new DateTime(2026, 8, 12, 13, 0, 0).ToString(enCa),
            monkmode.Service1.ComputeSlotActivationUntil("2026-08-12 12:00:00", "3600"));
        Assert.Equal(new DateTime(2026, 8, 13, 12, 0, 0).ToString(enCa),
            monkmode.Service1.ComputeSlotActivationUntil("2026-08-12 12:00:00", "86400"));
        Assert.Equal(monkmode.Service1.ComputeCoolOffDeadline("2026-08-12 12:00:00", 3600, 0),
            monkmode.Service1.ComputeSlotActivationUntil("2026-08-12 12:00:00", "3600"));
        // ...and it round-trips through the parser every gate reads it with.
        Assert.True(DateTime.TryParse(monkmode.Service1.ComputeSlotActivationUntil("2026-08-12 12:00:00", "3600"),
                                      enCa, DateTimeStyles.None, out _));
    }

    [Theory]
    [InlineData("2026-08-12 12:00:00", "")]
    [InlineData("2026-08-12 12:00:00", "0")]
    [InlineData("2026-08-12 12:00:00", "-60")]
    [InlineData("2026-08-12 12:00:00", "banana")]
    [InlineData("not-a-date", "3600")]
    public void ComputeSlotActivationUntil_FailsClosedToNoWrite(string highWater, string duration)
    {
        Assert.Equal("", monkmode.Service1.ComputeSlotActivationUntil(highWater, duration));
    }

    [Theory]
    [InlineData("monkmode_cooloff.request.7", "monkmode_cooloff.request.", "7")]
    [InlineData("MONKMODE_COOLOFF.REQUEST.7", "monkmode_cooloff.request.", "7")]   // Windows names are case-insensitive
    [InlineData("monkmode_partner.code.11", "monkmode_partner.code.", "11")]
    [InlineData("monkmode_cooloff.cancel.", "monkmode_cooloff.cancel.", "")]        // no id => not addressed
    [InlineData("monkmode_cooloff.request.7", "monkmode_partner.code.", "")]        // wrong family
    [InlineData("monkmode_settings.ini", "monkmode_cooloff.request.", "")]
    public void TriggerIdFromName_ReadsOnlyTheAddressedFamily(string fileName, string prefix, string expected)
    {
        Assert.Equal(expected, monkmode.Service1.TriggerIdFromName(fileName, prefix));
    }

    [Fact]
    public void SelectTriggerFiles_SortsOrdinalAndCapsTheTick()
    {
        // P41: the cap is what stops a directory stuffed with trigger files stalling the
        // enforcement tick. The surplus is LEFT for the next tick - deferring an exit trigger
        // is fail-closed (the block holds ~10s longer), never a lift.
        var many = new List<string>();
        for (var i = 0; i < 100; i++) many.Add("monkmode_cooloff.request." + i.ToString(CultureInfo.InvariantCulture));
        var picked = monkmode.Service1.SelectTriggerFiles(many, monkmode.Service1.MaxTriggerFilesPerTick);
        Assert.Equal(monkmode.Service1.MaxTriggerFilesPerTick, picked.Count);
        var sorted = new List<string>(picked);
        sorted.Sort(StringComparer.Ordinal);
        Assert.Equal(sorted, picked);
        Assert.Empty(monkmode.Service1.SelectTriggerFiles(null, 16));
    }

    [Fact]
    public void MaxTriggerFilesPerTick_IsTwiceTheSlotCap()
    {
        // 2 x MaxSlots, so every armed slot can have both a request and a cancel in flight.
        Assert.Equal(2 * monkmode.ConfigIntegrity.MaxSlots, monkmode.Service1.MaxTriggerFilesPerTick);
        Assert.Equal(4096, monkmode.Service1.TriggerMaxBytes);
    }

    [Fact]
    public void TriggerPrefixes_AreParityPinnedWithTheCliCopies()
    {
        // A drift here silently breaks the whole exit channel: the CLI would drop files the
        // service never reads, and `unblock` would appear to do nothing at all.
        Assert.Equal(MonkMode.Blocker.CoolOffRequestPrefix, monkmode.Service1.CoolOffRequestPrefix);
        Assert.Equal(MonkMode.Blocker.CoolOffCancelPrefix, monkmode.Service1.CoolOffCancelPrefix);
        Assert.Equal(MonkMode.Blocker.PartnerCodePrefix, monkmode.Service1.PartnerCodePrefix);
        Assert.Equal(MonkMode.Blocker.AddRequestPrefix, monkmode.Service1.AddRequestPrefix);
    }

    [Fact]
    public void SlotFieldNames_AreExactlyTheCanonicalsSixteenLines()
    {
        // The compaction a retire performs copies a slot section field-by-field from this
        // list. A field in the canonical but missing here would be silently DROPPED by a
        // compaction, leaving the block MAC-stamped over a value it had just lost.
        var canonical = monkmode.ConfigIntegrity.BuildSlotCanonical(
            1, "id", "start", "dur", "until", "sites", "apps", "urls", "all",
            "spec", "schedUntil", "coolUntil", "coolDur", "salt", "hash", "unlocked", "committed");
        var emitted = new List<string>();
        foreach (var line in canonical.Split('\n'))
        {
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            emitted.Add(line.Substring("Slot1.".Length, eq - "Slot1.".Length));
        }
        Assert.Equal(16, emitted.Count);
        Assert.Equal(emitted, new List<string>(monkmode.Service1.SlotFieldNames));
    }

    [Fact]
    public void ExactHostsRewrite_ShrinksTheBlock_WhereRepairHostsBlockWouldNoOp()
    {
        // The reason retire does not reuse RepairHostsBlock: that returns Nothing whenever
        // hosts already CONTAINS the expected text, which is right for a self-heal but wrong
        // for a SHRINK. Retiring the LAST slot in position order leaves the survivors as a
        // literal prefix of what is in hosts, so the repair would no-op and the retired
        // block's sites would stay blocked until teardown.
        const string marker = "#### MonkMode Entries ####";
        var wide = "127.0.0.1 mine.example\r\n" + marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n";
        var narrow = marker + "\r\n0.0.0.0 a.com\r\n";
        Assert.Null(monkmode.Service1.RepairHostsBlock(wide, narrow));            // would leave b.com blocked
        var rewritten = monkmode.Service1.ExactHostsRewrite(wide, narrow);
        Assert.NotNull(rewritten);
        Assert.Contains("a.com", rewritten);
        Assert.DoesNotContain("b.com", rewritten);
        Assert.StartsWith("127.0.0.1 mine.example", rewritten);                   // the user's own line survives
        // Already exactly right => no churn.
        Assert.Null(monkmode.Service1.ExactHostsRewrite(rewritten, narrow));
        // Never invent content.
        Assert.Null(monkmode.Service1.ExactHostsRewrite(wide, ""));
        Assert.Null(monkmode.Service1.ExactHostsRewrite(wide, null));
    }
}

// ---- 4. the live retire/teardown machinery, end to end through REAL config writes ----

[Collection("CliIniWriters")]
public class SlotRetireLiveTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);
    private const string Marker = "#### MonkMode Entries ####";

    // NEVER construct a Service1 directly in a test. InitializeComponent sets
    // timer.Enabled = True, so construction alone starts the 10s ENFORCEMENT TICK on a
    // threadpool thread - which writes the very test-bin config these tests assert over, and
    // re-asserts the read-only attribute on the REAL hosts file. It was caught doing exactly
    // that: a stray heartbeat rewrote [CurrentTime] Now underneath SlotArmTests' "a second
    // arm never re-seeds the monotonic frame" assertion, from a Service1 this file had
    // constructed. Every construction goes through here.
    private static monkmode.Service1 Svc() => TestSvc.New();

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "s3b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // The hosts writer ALWAYS leaves its target read-only (a writable hosts is the fail-OPEN
    // state the DNS client would read around), so the temp fixture has to clear the attribute
    // before it can be removed. That the cleanup needs this is itself the proof the writer
    // re-locks.
    private static void Drop(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
        foreach (var pattern in new[] { MonkMode.Blocker.CoolOffRequestPrefix + "*",
                                        MonkMode.Blocker.CoolOffCancelPrefix + "*",
                                        MonkMode.Blocker.PartnerCodePrefix + "*" })
        {
            foreach (var f in Directory.GetFiles(MonkMode.Blocker.AppDir(), pattern)) File.Delete(f);
        }
    }

    private static MonkMode.Blocker.ArmResult Arm(string site, string? app = null, DateTime? startAt = null)
        => MonkMode.Blocker.ArmSlot(new[] { site },
                                    app is null ? Array.Empty<string>() : new[] { app },
                                    "", startAt, Ends, false);

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static List<string> SiteOf(monkmode.IniFile ini, int pos)
        => new(monkmode.Service1.SplitPackedList(ini.GetKeyValue("Slot" + pos, "Sites"), ';'));

    private static int SlotCount(monkmode.IniFile ini)
        => monkmode.ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"));

    // ---- the retire matrix ----

    [Theory]
    [InlineData(1, "b.com", "c.com")]   // retire the FIRST of three
    [InlineData(2, "a.com", "c.com")]   // the MIDDLE
    [InlineData(3, "a.com", "b.com")]   // the LAST
    public void RetireOneOfThree_CompactsTheRest_AndLeavesTheMacValid(int retirePosition, string survivor1, string survivor2)
    {
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.True(Arm("c.com").Ok);
            var svc = Svc();
            var retiredId = retirePosition.ToString(CultureInfo.InvariantCulture);   // ids 1..3 == positions here

            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(dir, "monkmode_hosts.block"),
                                         Path.Combine(dir, "hosts"), retiredId));

            var after = Reload();
            Assert.Equal(2, SlotCount(after));
            // The survivors slid down into positions 1..2, keeping their own ids.
            Assert.Equal(new[] { survivor1 }, SiteOf(after, 1));
            Assert.Equal(new[] { survivor2 }, SiteOf(after, 2));
            // The freed TRAILING section is gone outright - a retired slot must leave no
            // state a later reader could resurrect.
            Assert.Equal("", after.GetKeyValue("Slot3", "Id"));
            Assert.Equal(0, monkmode.Service1.FindSlotPositionById(after, retiredId));
            // P17: NextSlotId never goes backwards, so the retired id can never be re-issued
            // and a replayed monkmode_partner.code.<id> can never address a different block.
            Assert.Equal("4", after.GetKeyValue("Slots", "NextSlotId"));
            // ...and the whole thing is still MAC-valid: a second per-slot write proves it,
            // because PersistSlotFieldAt refuses outright on an invalid MAC.
            Assert.True(svc.PersistSlotFieldAt(MonkMode.Blocker.IniPath(), survivor1 == "a.com" ? "1" : "2",
                                               "PartnerUnlockedAt", "2026-08-12 12:00:00", false));
            // The v9 list mirror follows the slot set: the retired block's site is gone from
            // it too, or a finished 1-hour block would keep being enforced for the whole life
            // of an unrelated 30-day one.
            var mirror = Reload().GetKeyValue("User", "CustomSites");
            Assert.Contains(survivor1, mirror);
            Assert.Contains(survivor2, mirror);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_RewritesHostsAndSnapshotToConfigTruth()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            File.WriteAllText(snapshotPath, Marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n");
            File.WriteAllText(hostsPath, "127.0.0.1 mine.example\r\n" + Marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n");

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "1"));

            var snapshot = File.ReadAllText(snapshotPath);
            var hosts = File.ReadAllText(hostsPath);
            Assert.DoesNotContain("a.com", snapshot);
            Assert.Contains("b.com", snapshot);
            Assert.DoesNotContain("a.com", hosts);
            Assert.Contains("b.com", hosts);
            // The paramount fence: only ever the marker block. The user's own line survives.
            Assert.StartsWith("127.0.0.1 mine.example", hosts);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_WithNothingLeftToBlock_LeavesHostsAndSnapshotAlone()
    {
        // R1: with no sites left in config truth, the honest choice is the OVER-blocking one.
        // Blanking a snapshot is what a torn-down block looks like on disk, and the remaining
        // slot (app-only here) is still armed - so removing the entries is the whole-machine
        // teardown's job, not a retire's.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(MonkMode.Blocker.ArmSlot(Array.Empty<string>(), new[] { "chrome.exe" }, "", null, Ends, false).Ok);
            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            var snapshotBefore = Marker + "\r\n0.0.0.0 a.com\r\n";
            var hostsBefore = "127.0.0.1 mine.example\r\n" + snapshotBefore;
            File.WriteAllText(snapshotPath, snapshotBefore);
            File.WriteAllText(hostsPath, hostsBefore);

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "1"));

            Assert.Equal(1, SlotCount(Reload()));                    // the config DID retire
            Assert.Equal(snapshotBefore, File.ReadAllText(snapshotPath));
            Assert.Equal(hostsBefore, File.ReadAllText(hostsPath));  // ...and hosts over-blocks
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_MacInvalidConfig_IsFrozen_NotRetired()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var ini = Reload();
            ini.SetKeyValue("Slot1", "Sites", "harmless.example;");   // a raw edit to a MAC-covered field
            ini.Save(MonkMode.Blocker.IniPath());
            var tampered = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            Assert.False(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                                              Path.Combine(dir, "monkmode_hosts.block"),
                                                              Path.Combine(dir, "hosts"), "1"));
            Assert.Equal(tampered, File.ReadAllBytes(MonkMode.Blocker.IniPath()));   // not one byte
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_UnknownOrAlreadyRetiredId_WritesNothing()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            var svc = Svc();
            Assert.False(svc.RetireSlotAt(MonkMode.Blocker.IniPath(), Path.Combine(dir, "s"), Path.Combine(dir, "h"), "99"));
            Assert.False(svc.RetireSlotAt(MonkMode.Blocker.IniPath(), Path.Combine(dir, "s"), Path.Combine(dir, "h"), ""));
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- P38 crash-point ordering: EVERY intermediate state over-blocks ----

    [Fact]
    public void RetireSlot_CrashBeforeTheConfigWrite_ChangesNothing_AndTheRetryDoesItAll()
    {
        // Crash point 0. Nothing happened, so the slot is still armed and hosts still blocks
        // it - the next tick simply re-classifies it Retire and starts over. Idempotent.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            Assert.Equal(2, SlotCount(Reload()));
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));

            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            File.WriteAllText(snapshotPath, Marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n");
            File.WriteAllText(hostsPath, Marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n");
            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "1"));
            Assert.Equal(1, SlotCount(Reload()));
            Assert.DoesNotContain("a.com", File.ReadAllText(hostsPath));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_CrashAfterTheConfigWrite_LeavesHostsOverBlocking_AndTheTickConverges()
    {
        // Crash point 1->2. The config no longer carries the slot, but the snapshot and hosts
        // still block its sites. That is an OVER-block, and it is the whole reason the order
        // is config-first: the reverse would leave a NARROWED snapshot with the slot still
        // armed, which is an UNDER-block nothing could repair.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            // Build the fixture with the REAL synthesiser, not a hand-written approximation:
            // whether the survivors are a contiguous substring of the wide block is the exact
            // property under test, and BuildHostsEntries emits www./m./web./mobile. variants
            // per domain, so a made-up two-line block would test a geometry that never occurs.
            var wide = Marker + "\r\n" + monkmode.Service1.BuildHostsEntries(new List<string> { "a.com", "b.com" });
            File.WriteAllText(snapshotPath, wide);
            File.WriteAllText(hostsPath, wide);

            // Force the crash: the hosts rename throws, so step (3) never lands. (Step (2)
            // still writes the snapshot - a real crash between (1) and (2) is the STRICTLY
            // more over-blocking version of this state, since the snapshot would stay wide.)
            monkmode.AtomicHosts.RenameHookForTests = (_, _) => throw new IOException("simulated crash");
            try
            {
                Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "1"));
            }
            finally { monkmode.AtomicHosts.RenameHookForTests = null; }

            Assert.Equal(1, SlotCount(Reload()));                        // config: retired
            Assert.Equal(wide, File.ReadAllText(hostsPath));             // hosts: STILL blocking a.com
            Assert.DoesNotContain("a.com", File.ReadAllText(snapshotPath));

            // Convergence is the TICK's, not the retire's: the slot is gone from the config,
            // so RetireSlotAt will never run for it again. IN THIS GEOMETRY the per-tick B2
            // self-heal does repair hosts from the (already reconciled) snapshot, because the
            // survivors are the TAIL of the wide block and so are not a substring of it.
            var repaired = monkmode.Service1.RepairHostsBlock(File.ReadAllText(hostsPath), File.ReadAllText(snapshotPath));
            Assert.NotNull(repaired);
            Assert.DoesNotContain("a.com", repaired);
            Assert.Contains("b.com", repaired);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_CrashAfterSnapshot_HostsStaysWide_WhichIsTheOverBlockingSide()
    {
        // Crash point 2->3, stated as the invariant that matters: at NO intermediate state is
        // a site unblocked that config truth still says is blocked. The set of hosts entries
        // is always a SUPERSET of the surviving slots' sites.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            // Build the fixture with the REAL synthesiser, not a hand-written approximation:
            // whether the survivors are a contiguous substring of the wide block is the exact
            // property under test, and BuildHostsEntries emits www./m./web./mobile. variants
            // per domain, so a made-up two-line block would test a geometry that never occurs.
            var wide = Marker + "\r\n" + monkmode.Service1.BuildHostsEntries(new List<string> { "a.com", "b.com" });
            File.WriteAllText(snapshotPath, wide);
            File.WriteAllText(hostsPath, wide);

            monkmode.AtomicHosts.RenameHookForTests = (_, _) => throw new IOException("simulated crash");
            try { Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "1"); }
            finally { monkmode.AtomicHosts.RenameHookForTests = null; }

            var truth = monkmode.Service1.UnionSlotSites(Svc().LoadSlots(Reload()),
                                                         DateTime.Now, 5, true, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            var blocked = File.ReadAllText(hostsPath);
            foreach (var site in truth) Assert.Contains(site, blocked);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_CrashedRetireOfTheLastSlotInOrder_DoesNotSelfHeal_AndThatIsCarried()
    {
        // The honest limit of the crash story, pinned so the comment cannot drift back to
        // claiming unconditional 10s convergence. Retiring the LAST slot in position order
        // leaves the survivors as a contiguous PREFIX of the wide block, so RepairHostsBlock -
        // which no-ops whenever hosts CONTAINS the expected text - never fires, and the
        // retired slot's sites stay blocked until the next retire or the teardown. A rare
        // double fault (a crash inside the retire AND that geometry) and a pure OVER-block,
        // so it is carried rather than paid for with an exact-target computation on the 10s
        // hot path. The NON-crash path is exact - that is what ExactHostsRewrite is for.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            // Build the fixture with the REAL synthesiser, not a hand-written approximation:
            // whether the survivors are a contiguous substring of the wide block is the exact
            // property under test, and BuildHostsEntries emits www./m./web./mobile. variants
            // per domain, so a made-up two-line block would test a geometry that never occurs.
            var wide = Marker + "\r\n" + monkmode.Service1.BuildHostsEntries(new List<string> { "a.com", "b.com" });
            File.WriteAllText(snapshotPath, wide);
            File.WriteAllText(hostsPath, wide);

            monkmode.AtomicHosts.RenameHookForTests = (_, _) => throw new IOException("simulated crash");
            try { Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "2"); }
            finally { monkmode.AtomicHosts.RenameHookForTests = null; }

            Assert.Equal(1, SlotCount(Reload()));
            // The snapshot IS truth; the self-heal simply declines to shrink hosts to it.
            Assert.DoesNotContain("b.com", File.ReadAllText(snapshotPath));
            Assert.Null(monkmode.Service1.RepairHostsBlock(File.ReadAllText(hostsPath), File.ReadAllText(snapshotPath)));
            Assert.Contains("b.com", File.ReadAllText(hostsPath));      // over-block, carried
            // ...and the exact rewrite the non-crash path uses WOULD have shrunk it.
            Assert.NotNull(monkmode.Service1.ExactHostsRewrite(File.ReadAllText(hostsPath), File.ReadAllText(snapshotPath)));
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- P2: the [Commit] Committed latch must not outlive its slot ----

    [Fact]
    public void RetiringACommittedSlot_ClearsTheV9CommitLatch_SoTheSurvivorKeepsItsExit()
    {
        // The latch is "yes if ANY slot is committed", written at every arm and never cleared.
        // The CLI's `unblock` gate reads it, so a committed 1h block retiring beside an
        // uncommitted 30d one used to refuse the SURVIVOR's cooling-off - "This block is
        // COMMITTED" - for the whole 30 days, until some later arm happened to recompute it.
        // Over-block, no bypass, but it locks the user out of an exit they are entitled to.
        Wipe();
        var dir = TempDir();
        try
        {
            // Slot 1 committed, slot 2 not.
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "a.com" }, Array.Empty<string>(), "", null, Ends, false, committed: true).Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.Equal("yes", Reload().GetKeyValue("Commit", "Committed"));
            Assert.True(MonkMode.Blocker.BlockIsCommitted());

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                                             Path.Combine(dir, "monkmode_hosts.block"),
                                                             Path.Combine(dir, "hosts"), "1"));

            Assert.Equal("no", Reload().GetKeyValue("Commit", "Committed"));
            Assert.False(MonkMode.Blocker.BlockIsCommitted());
            // ...and the addressed gate agrees about the survivor specifically.
            Assert.False(MonkMode.Blocker.SlotIsCommitted(2));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetiringAnUncommittedSlot_LeavesACommittedSurvivorsLatchOn()
    {
        // The recompute is a UNION, not a clear: a surviving committed block must keep the
        // machine-wide latch on, or the CLI would offer a cooling-off the service will Ignore.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "b.com" }, Array.Empty<string>(), "", null, Ends, false, committed: true).Ok);
            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                                             Path.Combine(dir, "monkmode_hosts.block"),
                                                             Path.Combine(dir, "hosts"), "1"));
            Assert.Equal("yes", Reload().GetKeyValue("Commit", "Committed"));
            Assert.True(MonkMode.Blocker.SlotIsCommitted(2));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void SlotIsCommitted_ReadsTheNamedBlock_NotTheMachineWideLatch()
    {
        Wipe();
        try
        {
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "a.com" }, Array.Empty<string>(), "", null, Ends, false, committed: true).Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.True(MonkMode.Blocker.BlockIsCommitted());     // the latch says yes...
            Assert.True(MonkMode.Blocker.SlotIsCommitted(1));
            Assert.False(MonkMode.Blocker.SlotIsCommitted(2));    // ...but block 2 is not
            Assert.False(MonkMode.Blocker.SlotIsCommitted(99));   // unknown id: fail-safe False
        }
        finally { Wipe(); }
    }

    // ---- P33: a bare `unblock` may never aim at a block the user did not name ----

    [Fact]
    public void BareUnblock_WithTwoBlocksArmed_Refuses_AndNeitherCoolOffIsWritten()
    {
        // THE PIN. Habit-typing `unblock` with a 1h focus block and a 30d commitment armed
        // must NOT start the ~1h clock on both - the 30d block would lift within the hour and
        // the success message never said it had addressed every block.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var armed = MonkMode.Blocker.ArmedSlotIds();
            Assert.Equal(2, armed.Count);
            Assert.True(MonkMode.Program.BareUnblockIsAmbiguous(false, armed.Count));

            // The refusal means NO trigger is dropped, and the service is the only writer of
            // CoolOffUntil - so with an empty trigger zone a full poll writes nothing.
            var svc = Svc();
            svc.ProcessCoolOffSignalsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()),
                                        "2026-08-12 12:00:00", true, monkmode.Service1.EnumerateTriggerFilesIn(dir));
            Assert.Equal("", Reload().GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.Equal("", Reload().GetKeyValue("Slot2", "CoolOffUntil"));

            // ...and this is the harm the refusal prevents: had the CLI broadcast instead,
            // BOTH blocks would now be counting down to a lift.
            foreach (var id in armed) File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + id), "");
            svc.ProcessCoolOffSignalsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()),
                                        "2026-08-12 12:00:00", true, monkmode.Service1.EnumerateTriggerFilesIn(dir));
            Assert.NotEqual("", Reload().GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.NotEqual("", Reload().GetKeyValue("Slot2", "CoolOffUntil"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void BareUnblock_WithOneBlockArmed_StillTargetsIt()
    {
        // v1.0's feel is preserved on a single-block machine: no --id needed.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.False(MonkMode.Program.BareUnblockIsAmbiguous(false, MonkMode.Blocker.ArmedSlotIds().Count));
        }
        finally { Wipe(); }
    }

    [Theory]
    [InlineData(true, 0, false)]    // --id given: never ambiguous
    [InlineData(true, 5, false)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, true)]
    [InlineData(false, 8, true)]
    public void BareUnblockIsAmbiguous_OnlyWhenUnnamedAndPlural(bool explicitId, int armedCount, bool expected)
    {
        Assert.Equal(expected, MonkMode.Program.BareUnblockIsAmbiguous(explicitId, armedCount));
    }

    [Fact]
    public void ArmedSlotLines_NameEachBlockWellEnoughToChooseBetweenThem()
    {
        // The refusal is only usable if the listing distinguishes the blocks, so pin the
        // shape: id, derived state, end, and the site/app counts.
        Wipe();
        try
        {
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "a.com", "x.com" }, new[] { "chrome.exe" }, "", null, Ends, false).Ok);
            Assert.True(Arm("b.com", startAt: DateTime.Now.AddDays(1)).Ok);
            var lines = MonkMode.Blocker.ArmedSlotLines();
            Assert.Equal(2, lines.Count);
            Assert.Contains("1", lines[0]);
            Assert.Contains("ACTIVE", lines[0]);
            Assert.Contains("(2 sites, 1 apps)", lines[0]);
            Assert.Contains("2", lines[1]);
            Assert.Contains("PENDING", lines[1]);
            Assert.Contains("(1 sites, 0 apps)", lines[1]);
            Assert.Empty(MonkMode.Blocker.ArmedSlotLines().Where(l => l.Contains("?")));
        }
        finally { Wipe(); }
    }

    // ---- P3: the legacy-name budget leak ----

    [Fact]
    public void UnsuffixedLegacyTriggers_AreEnumeratedByTheGlob_AndPurged()
    {
        // The Win32 trailing-".*" quirk: GetFiles(dir, "prefix.*") also matches "prefix" with
        // nothing after it. TriggerIdFromName then returns "", so NEITHER poller reaches its
        // delete - and the file occupies the 16-per-tick budget for ever, starving the tail
        // (partner codes sort LAST) at a full 8-slot load. It is also why an old dist's
        // `unblock` would appear to do nothing, permanently.
        Wipe();
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "monkmode_cooloff.request"), "");
            File.WriteAllText(Path.Combine(dir, "monkmode_cooloff.cancel"), "");
            File.WriteAllText(Path.Combine(dir, "monkmode_partner.code"), "OLD-CODE123");
            File.WriteAllText(Path.Combine(dir, "monkmode_cooloff.request.4"), "");

            var names = monkmode.Service1.EnumerateTriggerFilesIn(dir);
            Assert.Equal(4, names.Count);                                    // the quirk is real
            Assert.Contains("monkmode_cooloff.request", names);
            Assert.False(monkmode.Service1.TriggerAddressesAnyFamily("monkmode_cooloff.request"));
            Assert.True(monkmode.Service1.TriggerAddressesAnyFamily("monkmode_cooloff.request.4"));

            monkmode.Service1.PurgeUnaddressedTriggers(dir, names);
            Assert.False(File.Exists(Path.Combine(dir, "monkmode_cooloff.request")));
            Assert.False(File.Exists(Path.Combine(dir, "monkmode_cooloff.cancel")));
            Assert.False(File.Exists(Path.Combine(dir, "monkmode_partner.code")));
            // The addressed one is untouched - purging is only ever for names with no id.
            Assert.True(File.Exists(Path.Combine(dir, "monkmode_cooloff.request.4")));
            Assert.Single(monkmode.Service1.EnumerateTriggerFilesIn(dir));
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- the teardown gate ----

    [Fact]
    public void LastSlot_TriggersTeardownAll()
    {
        // Retiring the last slot empties the config; ClassifyTick then - and only then -
        // reaches TeardownAll (the v9 residual having also exited).
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var svc = Svc();
            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(dir, "monkmode_hosts.block"),
                                         Path.Combine(dir, "hosts"), "1"));
            Assert.Equal(0, SlotCount(Reload()));
            Assert.Equal(monkmode.Service1.TickAction.TeardownAll,
                monkmode.Service1.ClassifyTick(true, 0, monkmode.Service1.HeartbeatAction.Lift));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetiringTheLastSlotEarly_DoesNotWedgeOnTheV9GuardHorizon()
    {
        // THE WEDGE. The v9 [Time] Until mirror is the EXTEND-ONLY guard horizon, so a slot
        // armed `--start +30d` carries a mirror 30 days in the future. Retire that slot early
        // (a partner code) and the slot set is empty - but if the mirror still read "held",
        // ClassifyTick would Restamp forever and the machine would sit with hosts blocked and
        // NOTHING armed for a month. Neutralising the residual as the last slot goes is what
        // makes the very next tick reach TeardownAll.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com", startAt: DateTime.Now.AddDays(30)).Ok);
            var enc = new MonkMode.Simple3Des("mm_textbox");
            var horizon = DateTime.Parse(enc.DecryptData(Reload().GetKeyValue("Time", "Until")), new CultureInfo("en-CA"));
            Assert.True(horizon > DateTime.Now.AddDays(29));          // the mirror really is far ahead

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                                             Path.Combine(dir, "monkmode_hosts.block"),
                                                             Path.Combine(dir, "hosts"), "1"));

            var after = Reload();
            Assert.Equal(0, SlotCount(after));
            var until = enc.DecryptData(after.GetKeyValue("Time", "Until"));
            Assert.Equal(MonkMode.Blocker.ScheduleOnlyExpiredUntil, until);
            var residual = monkmode.Service1.ClassifyHeartbeat(
                true, monkmode.Service1.BlockHasExpired(until, DateTime.Now, 5), false, false, false, false);
            Assert.Equal(monkmode.Service1.TickAction.TeardownAll,
                monkmode.Service1.ClassifyTick(true, 0, residual));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireWithSurvivors_LeavesTheV9HorizonAlone_SoItKeepsOverBlocking()
    {
        // The mirror is only ever neutralised when NOTHING is left. While a slot survives its
        // job is to over-block, and shortening it would be the narrowing direction.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var before = Reload().GetKeyValue("Time", "Until");
            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                                             Path.Combine(dir, "monkmode_hosts.block"),
                                                             Path.Combine(dir, "hosts"), "1"));
            Assert.Equal(1, SlotCount(Reload()));
            Assert.Equal(before, Reload().GetKeyValue("Time", "Until"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void PendingSlot_KeepsSlotCount_TeardownDoesNotFire()
    {
        // P39: PENDING counts. A block armed with `--start` for tomorrow has no Until at all,
        // so a teardown decision that only looked at expiry would eat it before it ever ran.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);                                        // active
            Assert.True(Arm("b.com", startAt: DateTime.Now.AddDays(1)).Ok);      // pending
            var svc = Svc();
            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(dir, "monkmode_hosts.block"),
                                         Path.Combine(dir, "hosts"), "1"));

            var after = Reload();
            Assert.Equal(1, SlotCount(after));
            Assert.Equal(monkmode.Service1.TickAction.Restamp,
                monkmode.Service1.ClassifyTick(true, SlotCount(after), monkmode.Service1.HeartbeatAction.Lift));

            // ...and the pending slot itself never retires on time, so nothing can empty the
            // config out from under it.
            var slots = svc.LoadSlots(after);
            Assert.Single(slots);
            Assert.True(monkmode.Service1.SlotIsPending(slots[0]));
            Assert.Equal(monkmode.Service1.SlotAction.Hold,
                monkmode.Service1.SlotExitDue(slots[0], DateTime.Now, 5, true, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void TeardownAll_PersistsAZeroSlotConfig_ThatCannotWedgeIfInterrupted()
    {
        // P39 step (1). It also NEUTRALISES the v9 residual: leaving a future [Time] Until or
        // an armed [Schedule] Spec behind would make the next tick's residual HOLD, and a
        // half-finished teardown could then never complete - hosts blocked forever with
        // nothing armed. [Time] Until/CoolOffUntil sit outside the canonical; the [Schedule]
        // pair is INSIDE it since v11 (FX1), so the re-stamp that follows the clear covers
        // those two as well as the slot/SlotCount changes.
        //
        // FX3 (F3): the [Schedule] half of that clear is now CONDITIONAL on the schedule being
        // SPENT (ScheduleResidualIsSpent). This config has no schedule at all, so it is spent
        // and the pair is still blanked - which is every config that reaches a teardown, since
        // ClassifyTick only Lifts with scheduleArmed AND scheduleActive both False. What
        // changed is the RETIRE path, where a live schedule now survives its last slot
        // (RetiringTheLastSlot_LeavesAnArmedScheduleStanding).
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.True(Svc().PersistZeroSlotConfigAt(MonkMode.Blocker.IniPath()));

            var after = Reload();
            Assert.Equal(0, SlotCount(after));
            Assert.Equal("", after.GetKeyValue("Slot1", "Id"));
            Assert.Equal("", after.GetKeyValue("Slot2", "Id"));
            Assert.Equal("", after.GetKeyValue("Guard", "HoldUntil"));
            Assert.Equal("0", after.GetKeyValue("Guard", "ArmedCount"));
            Assert.Equal("", after.GetKeyValue("Schedule", "Spec"));
            Assert.Equal("", after.GetKeyValue("Time", "CoolOffUntil"));
            // P17: ids never restart, even across a whole teardown.
            Assert.Equal("3", after.GetKeyValue("Slots", "NextSlotId"));

            // The interrupted-teardown state RE-ENTERS TeardownAll rather than wedging: zero
            // slots, and a residual whose [Time] Until is now the past sentinel.
            var svc = Svc();
            var until = new MonkMode.Simple3Des("mm_textbox").DecryptData(after.GetKeyValue("Time", "Until"));
            Assert.Equal(MonkMode.Blocker.ScheduleOnlyExpiredUntil, until);
            var residual = monkmode.Service1.ClassifyHeartbeat(
                true, monkmode.Service1.BlockHasExpired(until, DateTime.Now, 5), false, false, false, false);
            Assert.Equal(monkmode.Service1.HeartbeatAction.Lift, residual);
            Assert.Equal(monkmode.Service1.TickAction.TeardownAll,
                monkmode.Service1.ClassifyTick(true, SlotCount(after), residual));
            // ...and the config is still MAC-valid, so the next tick reads it rather than
            // freezing on it.
            Assert.False(svc.PersistSlotFieldAt(MonkMode.Blocker.IniPath(), "1", "Until", "x", false));  // no such slot
        }
        finally { Wipe(); }
    }

    // ---- P29 activation, and carried finding 4 ----

    [Fact]
    public void PendingToActive_GrowsHostsUnion()
    {
        // The SERVICE stamps Until = HighWater + DurationSeconds at activation. Critically,
        // the slot's sites are in the hosts union BEFORE activation too (SlotContributesLists
        // is SlotEnforcesNow OrElse SlotIsPending) - they never leave the union for even one
        // tick. Narrowing that predicate would strip a pending block's entries out of the
        // snapshot with nothing able to put them back (the S3a P1 trap).
        Wipe();
        try
        {
            var startAt = DateTime.Now.AddHours(-1);      // already due
            Assert.True(Arm("a.com", startAt: startAt).Ok);
            var svc = Svc();
            var ini = Reload();
            var slots = svc.LoadSlots(ini);
            var hw = new MonkMode.Simple3Des("mm_textbox").DecryptData(ini.GetKeyValue("Time", "HighWater"));
            var asOf = DateTime.Parse(hw, new CultureInfo("en-CA"));

            Assert.True(monkmode.Service1.SlotIsPending(slots[0]));
            var beforeUnion = monkmode.Service1.UnionSlotSites(slots, asOf, 5, true, hw);
            Assert.Equal(new[] { "a.com" }, beforeUnion.ToArray());          // pending ALREADY contributes
            Assert.False(monkmode.Service1.SlotEnforcesNow(slots[0], asOf, 5, true, hw));

            svc.ActivateDueSlotsAt(MonkMode.Blocker.IniPath(), slots, hw, true);

            Assert.False(monkmode.Service1.SlotIsPending(slots[0]));
            Assert.True(monkmode.Service1.SlotEnforcesNow(slots[0], asOf, 5, true, hw));
            var afterUnion = monkmode.Service1.UnionSlotSites(slots, asOf, 5, true, hw);
            Assert.Equal(beforeUnion.ToArray(), afterUnion.ToArray());       // never a gap
            // Persisted, encrypted, and derived from HighWater - not from the wall clock.
            var stored = new MonkMode.Simple3Des("mm_textbox").DecryptData(Reload().GetKeyValue("Slot1", "Until"));
            Assert.Equal(monkmode.Service1.ComputeSlotActivationUntil(hw, Reload().GetKeyValue("Slot1", "DurationSeconds")), stored);
            Assert.True(DateTime.Parse(stored, new CultureInfo("en-CA")) > asOf);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ActivationBeforeItsStart_DoesNothing()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com", startAt: DateTime.Now.AddDays(1)).Ok);
            var svc = Svc();
            var ini = Reload();
            var slots = svc.LoadSlots(ini);
            var hw = new MonkMode.Simple3Des("mm_textbox").DecryptData(ini.GetKeyValue("Time", "HighWater"));
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            svc.ActivateDueSlotsAt(MonkMode.Blocker.IniPath(), slots, hw, true);
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.True(monkmode.Service1.SlotIsPending(slots[0]));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ActivationOnAFrozenConfig_DoesNothing()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com", startAt: DateTime.Now.AddHours(-1)).Ok);
            var svc = Svc();
            var ini = Reload();
            var slots = svc.LoadSlots(ini);
            var hw = new MonkMode.Simple3Des("mm_textbox").DecryptData(ini.GetKeyValue("Time", "HighWater"));
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            svc.ActivateDueSlotsAt(MonkMode.Blocker.IniPath(), slots, hw, macValid: false);
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }

    // ---- a CLI arm racing a retire ----

    [Fact]
    public void CliArmDuringRetire_SnapshotRecomputedFromConfigTruth()
    {
        // The retire recomputes the snapshot by RE-READING the config from disk, never from
        // the tick's in-memory slot list. That is precisely what makes an arm landing mid-
        // retire safe: the new slot is on disk, so it is in the recomputed union and its
        // sites survive. Reading the stale in-memory list would silently drop the block the
        // user just armed - and nothing would ever put it back.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var svc = Svc();
            // The tick's view, captured BEFORE the race: two slots, no c.com.
            var staleSlots = svc.LoadSlots(Reload());
            Assert.Equal(2, staleSlots.Count);

            // The CLI arms a third block while the tick still holds that stale list.
            Assert.True(Arm("c.com").Ok);

            var snapshotPath = Path.Combine(dir, "monkmode_hosts.block");
            var hostsPath = Path.Combine(dir, "hosts");
            File.WriteAllText(snapshotPath, Marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n");
            File.WriteAllText(hostsPath, Marker + "\r\n0.0.0.0 a.com\r\n0.0.0.0 b.com\r\n");

            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(), snapshotPath, hostsPath, "1"));

            var snapshot = File.ReadAllText(snapshotPath);
            Assert.DoesNotContain("a.com", snapshot);
            Assert.Contains("b.com", snapshot);
            Assert.Contains("c.com", snapshot);          // the racing arm survived the retire
            Assert.Contains("c.com", File.ReadAllText(hostsPath));
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- P40: slot-addressed triggers ----

    [Fact]
    public void PartnerCode_ForSlotA_DoesNotUnlockSlotB()
    {
        // Before S3b there was ONE machine-wide verifier, so any valid code lifted every
        // block. Now each slot carries its own MAC-covered Salt/Hash and the candidate is
        // checked only against the addressed slot's - so possessing block A's code retires
        // block A and leaves block B fully enforced.
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            var b = Arm("b.com");
            Assert.True(a.Ok && b.Ok);
            var svc = Svc();

            // Slot A's code, submitted (as the CLI broadcasts it) to BOTH slots.
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), a.PartnerCode);
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + b.Id), a.PartnerCode);

            var slots = svc.LoadSlots(Reload());
            svc.ProcessPartnerCodeSignalAt(dir, MonkMode.Blocker.IniPath(), slots, true,
                                           monkmode.Service1.EnumerateTriggerFilesIn(dir));

            var after = Reload();
            Assert.NotEqual("", after.GetKeyValue("Slot1", "PartnerUnlockedAt"));   // A unlocked
            Assert.Equal("", after.GetKeyValue("Slot2", "PartnerUnlockedAt"));      // B untouched
            // Both triggers are consumed either way - a miss must not leave a candidate to
            // be retried forever, and it must not rotate B's code either.
            Assert.Empty(Directory.GetFiles(dir, monkmode.Service1.PartnerCodePrefix + "*"));

            // ...and the classifiers agree: only A is due to retire.
            var reread = svc.LoadSlots(after);
            var hw = new MonkMode.Simple3Des("mm_textbox").DecryptData(after.GetKeyValue("Time", "HighWater"));
            var asOf = DateTime.Parse(hw, new CultureInfo("en-CA"));
            Assert.Equal(monkmode.Service1.SlotAction.Retire, monkmode.Service1.SlotExitDue(reread[0], asOf, 5, true, hw));
            Assert.Equal(monkmode.Service1.SlotAction.Hold, monkmode.Service1.SlotExitDue(reread[1], asOf, 5, true, hw));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void WrongCode_UnlocksNothing_AndDoesNotRotateTheRealOne()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), "WRONG-CODE1");
            svc.ProcessPartnerCodeSignalAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true,
                                           monkmode.Service1.EnumerateTriggerFilesIn(dir));
            Assert.Equal("", Reload().GetKeyValue("Slot1", "PartnerUnlockedAt"));

            // The real code still works afterwards: a miss must not grief-lock the partner's
            // legitimate code (PD6).
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), a.PartnerCode);
            svc.ProcessPartnerCodeSignalAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true,
                                           monkmode.Service1.EnumerateTriggerFilesIn(dir));
            Assert.NotEqual("", Reload().GetKeyValue("Slot1", "PartnerUnlockedAt"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void TriggerWithRetiredId_DeletedNoStateChange()
    {
        // P40: the id is a ROUTING HINT with zero authority. An unknown, retired or garbage
        // id deletes the trigger and changes nothing - and deliberately does NOT freeze,
        // because freezing on junk would hand anyone a one-file wedge of the machine.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            foreach (var id in new[] { "99", "not-a-number", "1x" })
            {
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + id), "");
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + id), "SOME-CODE1");
            }
            var slots = svc.LoadSlots(Reload());
            var names = monkmode.Service1.EnumerateTriggerFilesIn(dir);
            svc.ProcessCoolOffSignalsAt(dir, MonkMode.Blocker.IniPath(), slots, "2026-08-12 12:00:00", true, names);
            svc.ProcessPartnerCodeSignalAt(dir, MonkMode.Blocker.IniPath(), slots, true, names);

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));    // not one byte
            Assert.Empty(Directory.GetFiles(dir, "monkmode_cooloff.*"));
            Assert.Empty(Directory.GetFiles(dir, "monkmode_partner.*"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void CoolingOff_IsPerSlot_SoASecondArmIsNotSubjectedToTheFirstsDeadline()
    {
        // THE S2 UNDER-BLOCK, CLOSED. `arm 30d; unblock; arm 30d` used to subject the second
        // block to the first's machine-wide ~1h deadline, so BOTH lifted at ~1h. There is no
        // machine-wide deadline left to inherit.
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            const string hw = "2026-08-12 12:00:00";

            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + a.Id), "");
            svc.ProcessCoolOffSignalsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), hw, true,
                                        monkmode.Service1.EnumerateTriggerFilesIn(dir));
            var enc = new MonkMode.Simple3Des("mm_textbox");
            Assert.NotEqual("", Reload().GetKeyValue("Slot1", "CoolOffUntil"));
            // The deadline is the FLOOR-clamped HighWater offset, never the wall clock.
            Assert.Equal(monkmode.Service1.ComputeCoolOffDeadline(hw, monkmode.Service1.MinCoolOffFloorSeconds, monkmode.Service1.MinCoolOffFloorSeconds),
                         enc.DecryptData(Reload().GetKeyValue("Slot1", "CoolOffUntil")));

            // Now arm a SECOND block. It starts with no deadline of its own and cannot inherit
            // the first's.
            var b = Arm("b.com");
            Assert.True(b.Ok);
            Assert.Equal("", Reload().GetKeyValue("Slot2", "CoolOffUntil"));
            var slots = svc.LoadSlots(Reload());
            var asOf = DateTime.Parse(hw, new CultureInfo("en-CA"));
            Assert.Equal(monkmode.Service1.SlotAction.Hold, monkmode.Service1.SlotExitDue(slots[1], asOf, 5, true, hw));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void CoolOffCancel_WinsOverASimultaneousRequest_PerSlot()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + a.Id), "");
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffCancelPrefix + a.Id), "");
            svc.ProcessCoolOffSignalsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), "2026-08-12 12:00:00", true,
                                        monkmode.Service1.EnumerateTriggerFilesIn(dir));
            // Cancel wins: the safe outcome is "stay blocked".
            Assert.Equal("", Reload().GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.Empty(Directory.GetFiles(dir, "monkmode_cooloff.*"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void FrozenConfig_IgnoresEveryTrigger_AndWritesNothing()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            var slots = svc.LoadSlots(Reload());
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + a.Id), "");
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), a.PartnerCode);
            var names = monkmode.Service1.EnumerateTriggerFilesIn(dir);
            svc.ProcessCoolOffSignalsAt(dir, MonkMode.Blocker.IniPath(), slots, "2026-08-12 12:00:00", macValid: false, triggerNames: names);
            svc.ProcessPartnerCodeSignalAt(dir, MonkMode.Blocker.IniPath(), slots, macValid: false, triggerNames: names);
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- the DoSchedule bypass ----

    [Fact]
    public void WriteScheduleConfig_RefusesToScaffoldOverArmedSlots()
    {
        // A LIVE BYPASS, closed. The fresh-scaffold branch builds a brand-new IniFile and
        // stamps a fresh VALID MAC over it - with slots armed that would delete every [SlotN]
        // section and silently lift every running block, in one command. DoSchedule's guard
        // was BlockIsActive() alone, which short-circuits False whenever the service is not
        // RUNNING, so a registered-but-STOPPED machine with slots armed was wide open.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            MonkMode.Blocker.WriteScheduleConfig("v2 1111100 0900-1700 sites=x.com");
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.Equal(2, SlotCount(Reload()));
        }
        finally { Wipe(); }
    }

    // ---- FX3 (F3): the DoBlock bypass - the OTHER half of SD-c1 ----

    // The compact Spec the real CLI builder emits, so ParseSchedule reads >= 1 window from it
    // (ScheduleArmed is the parse, not a non-empty check).
    private static string Spec(string windows = "Mon-Fri 09:00-17:00", string site = "x.com")
    {
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(windows, new[] { site },
                                                          Array.Empty<string>(), ref spec, ref err), err);
        return spec;
    }

    // Re-stamp [Integrity] Mac over the current canonical with the config's OWN stored key -
    // the fixture's copy of the writers' private RestampMacWithExistingKey. It exists ONLY to
    // build the slot+schedule MIXED state, which no writer will create any more (that is the
    // fix): the CLI refuses to arm beside a schedule, and the schedule writer refuses to
    // scaffold over slots. A crash between two writes could still leave a config in it, which
    // is exactly what the retire tests below have to be able to construct.
    private static void RestampByHand(monkmode.IniFile ini)
    {
        var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
        Assert.NotNull(key);
        ini.SetKeyValue("Integrity", "Mac",
            MonkMode.ConfigIntegrity.ComputeConfigMac(MonkMode.Tests.TestSvc.New().CanonicalFromIni(ini), key));
    }

    // Arm one slot, then graft a GLOBAL schedule onto the same config (optionally with an OPEN
    // window) and re-stamp, so the result is MAC-valid - the mixed state a crash could leave.
    private static void ArmSlotBesideAScheduleByHand(string spec, DateTime? windowOpenUntil = null)
    {
        Assert.True(Arm("a.com").Ok);
        var ini = Reload();
        ini.SetKeyValue("Schedule", "Spec", spec);
        if (windowOpenUntil.HasValue)
        {
            ini.SetKeyValue("Schedule", "ActiveUntil",
                new MonkMode.Simple3Des("mm_textbox").EncryptData(windowOpenUntil.Value.ToString(new CultureInfo("en-CA"))));
        }
        RestampByHand(ini);
        ini.Save(MonkMode.Blocker.IniPath());
        Assert.True(MonkMode.Blocker.ScheduleIsArmed());        // MAC-valid AND carrying a Spec
    }

    [Fact]
    public void ArmSlot_RefusesBesideAnArmedSchedule_AndWritesNothing()
    {
        // THE F3 P1, at the writer. S2 removed `block`'s schedule refusal reasoning "v1.1
        // blocks coexist" - true slot-vs-slot, false slot-vs-SCHEDULE, because a schedule is
        // NOT a slot (global [Schedule] Spec, no [SlotN] section). With the service ABSENT
        // ShouldFreshRewrite is True for a schedule-only config (MAC-valid, but no [Slots]
        // section at all), so the arm scaffolded a brand-new ini and the Spec was simply gone.
        Wipe();
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(Spec());
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            // (a) service ABSENT - the fresh-scaffold branch, the destructive one.
            var absent = MonkMode.Blocker.ArmSlot(new[] { "a.com" }, Array.Empty<string>(), "", null, Ends, false);
            Assert.False(absent.Ok);
            Assert.Equal(MonkMode.Blocker.ArmOutcome.ScheduleArmed, absent.Outcome);

            // (b) service INSTALLED - the append branch, where the Spec used to survive only
            // until this slot retired. Same refusal: the two shapes never coexist.
            var installed = MonkMode.Blocker.ArmSlot(new[] { "a.com" }, Array.Empty<string>(), "", null, Ends, true);
            Assert.False(installed.Ok);
            Assert.Equal(MonkMode.Blocker.ArmOutcome.ScheduleArmed, installed.Outcome);

            // Refusing is side-effect free, and the schedule is untouched.
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
            Assert.Equal(Spec(), Reload().GetKeyValue("Schedule", "Spec"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ArmSlot_StillArmsWhenNoScheduleIsArmed_AndAfterOneIsCleared()
    {
        // The refusal must not be a latch: `schedule --clear` blanks the Spec, and a manual
        // block has to be armable again immediately afterwards.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);                       // no schedule anywhere: unchanged
            Wipe();

            MonkMode.Blocker.WriteScheduleConfig(Spec());
            MonkMode.Blocker.WriteScheduleConfig("");           // schedule --clear
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());
            var armed = MonkMode.Blocker.ArmSlot(new[] { "a.com" }, Array.Empty<string>(), "", null, Ends, true);
            Assert.True(armed.Ok);
            Assert.Equal(1, SlotCount(Reload()));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void RetiringTheLastSlot_LeavesAnArmedScheduleStanding()
    {
        // BRANCH (b) OF THE SAME BUG, and the reason the retire path needed its own guard.
        // With the service installed the arm appended a slot and the Spec survived - until
        // that slot retired, when the last-slot NeutraliseV9Residual blanked [Schedule]
        // Spec AND ActiveUntil. With a window OPEN that flipped ScheduleActive to False,
        // enforcementHeld collapsed and the next tick tore the machine down MID-WINDOW.
        Wipe();
        var dir = TempDir();
        try
        {
            var openUntil = DateTime.Now.AddHours(3);
            ArmSlotBesideAScheduleByHand(Spec(), windowOpenUntil: openUntil);

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                           Path.Combine(dir, "monkmode_hosts.block"),
                                           Path.Combine(dir, "hosts"), "1"));

            var after = Reload();
            Assert.Equal(0, SlotCount(after));                                  // the slot is gone
            Assert.Equal(Spec(), after.GetKeyValue("Schedule", "Spec"));        // the schedule is NOT
            var enc = new MonkMode.Simple3Des("mm_textbox");
            var activeUntil = enc.DecryptData(after.GetKeyValue("Schedule", "ActiveUntil"));
            Assert.Equal(openUntil.ToString(new CultureInfo("en-CA")), activeUntil);

            // ...and the config is still MAC-valid (the [Schedule] pair is canonical since
            // v11), so the service reads it rather than freezing on it - and reads it as a
            // schedule-only block whose window is still open, i.e. HELD.
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
            var hw = DateTime.Now.ToString(new CultureInfo("en-CA"));
            Assert.True(monkmode.Service1.ScheduleActive(activeUntil, hw));
            Assert.True(monkmode.Service1.ScheduleArmed(true, after.GetKeyValue("Schedule", "Spec")));
            var residual = monkmode.Service1.ClassifyHeartbeat(
                true, monkmode.Service1.BlockHasExpired(MonkMode.Blocker.ScheduleOnlyExpiredUntil, DateTime.Now, 5),
                false, false, monkmode.Service1.ScheduleActive(activeUntil, hw), true);
            Assert.Equal(monkmode.Service1.HeartbeatAction.Restamp, residual);
            Assert.Equal(monkmode.Service1.TickAction.Restamp,
                monkmode.Service1.ClassifyTick(true, SlotCount(after), residual));

            // The guardian hold survives too: ArmedCount is recomputed from the slots that
            // remain PLUS the schedule, so slot arithmetic can never zero a schedule's hold.
            var armedCount = after.GetKeyValue("Guard", "ArmedCount");
            Assert.Equal("1", armedCount);
            Assert.True(mm_guard.Guardian.GuardArmedCountHolds(armedCount));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetiringTheLastSlot_StillNeutralisesASPENTScheduleResidual()
    {
        // The guard is conditional, not a blanket "never clear": a Spec that no longer parses
        // to a window (and no open window) is spent residue, and leaving it would hold the
        // machine up for a schedule that does not exist. The [Time] Until sentinel is written
        // either way - it is what stops a retired `--start +30d` slot wedging the teardown.
        Wipe();
        var dir = TempDir();
        try
        {
            ArmSlotBesideAScheduleByHand("v2;not-a-window;sites=x.com");    // parses to 0 windows
            Assert.False(monkmode.Service1.ScheduleArmed(true, "v2;not-a-window;sites=x.com"));

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(),
                                           Path.Combine(dir, "monkmode_hosts.block"),
                                           Path.Combine(dir, "hosts"), "1"));

            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Schedule", "Spec"));
            Assert.Equal("", after.GetKeyValue("Schedule", "ActiveUntil"));
            Assert.Equal("0", after.GetKeyValue("Guard", "ArmedCount"));
            Assert.Equal(MonkMode.Blocker.ScheduleOnlyExpiredUntil,
                         new MonkMode.Simple3Des("mm_textbox").DecryptData(after.GetKeyValue("Time", "Until")));
            // Cleared UNDER a re-stamp, so the torn-down config is verifiable, not frozen.
            var check = new MonkMode.IniFile(); check.Load(MonkMode.Blocker.IniPath());
            var key = MonkMode.ConfigIntegrity.UnprotectKey(check.GetKeyValue("Integrity", "Key"));
            Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(
                MonkMode.Blocker.CanonicalFromIni(check), check.GetKeyValue("Integrity", "Mac"), key));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void AnySlotArmed_DoesNotConsultTheService_SoAStoppedOneCannotHideArmedSlots()
    {
        // The reason the guard moved off BlockIsActive(): this reads the CONFIG, so it is
        // true in the registered-but-stopped state the machine sits in between blocks.
        Wipe();
        try
        {
            Assert.False(MonkMode.Blocker.AnySlotArmed());
            Assert.True(Arm("a.com").Ok);
            Assert.True(MonkMode.Blocker.AnySlotArmed());
            Assert.Equal(new[] { 1 }, MonkMode.Blocker.ArmedSlotIds().ToArray());
            Assert.True(Arm("b.com").Ok);
            Assert.Equal(new[] { 1, 2 }, MonkMode.Blocker.ArmedSlotIds().ToArray());
        }
        finally { Wipe(); }
    }
}
