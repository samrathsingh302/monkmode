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

// MonkMode.Tests - v1.1 S4: the GUARDIAN's slot-aware hold (P43 + P44).
//
// THE HOLE THIS CLOSES. mm_guard is layer 2 of B1: it SCM-restarts a killed service and
// relaunches a killed notifier, and it stands down only when the block has genuinely ended.
// Until S4 the only things it could see were the v9 SINGLE-BLOCK mirror keys - [Time] Until,
// [Time] CoolOffUntil, [Partner] UnlockedAt, [Schedule] ActiveUntil - plus a legacy floor on
// the global [Schedule] Spec. v10 keeps every armed block in [Slot1]..[Slot8], so a machine
// can hold up to eight blocks the mirror never mentions. A guardian blind to them would
// stand down - stop restarting the killed service - while those blocks were still enforcing.
// That is the fail-OPEN direction, and it is what this slice's fold removes.
//
// What is pinned here (all pure or against test-owned ini files - no SCM, no process list,
// no real hosts/registry, nothing armed):
//
//   - Guardian.AnyBlockHeld: the truth table, INCLUDING `Not macValid => held` before any
//     other input is consulted (with zero readable slots a plain "does anything hold?" fold
//     answers False - the empty-list fail-open Service1.AnyBlockHeld guards the same way);
//   - Guardian.GuardHoldActive / GuardArmedCountHolds: the two MAC'd [Guard] scalars (P43),
//     extend-only, fail-closed on an unparseable value;
//   - Program.RawSlotFloorHeld: the P44 raw per-position floor - non-empty ScheduleSpec /
//     StartAt / Until at ANY pos 1..MaxSlots, with NO parser, NO decrypt and NO MAC gate,
//     and bounded by MaxSlots rather than the stored SlotCount so a forged count cannot
//     silence it;
//   - the SUPERSET property against the service: whenever Service1.AnyBlockHeld says the
//     slots hold, the guardian holds too - proved with the [Guard] scalars deliberately
//     BLANK, so the raw floor alone has to carry it;
//   - P39's other half: after a teardown (and after retiring every slot one at a time) the
//     floor genuinely goes QUIET, which is what lets the guardian ever exit at all. Without
//     this the fold would be a wedge rather than a defence.
//
// The live wiring - the loop's Exit Do, the OnUnhandledException backstop, the SCM restart -
// is the elevated-smoke seam, exactly like the B1/B2/B3 wiring; only the decisions are here.

using System.Globalization;

namespace MonkMode.Tests;

public class GuardianAnyBlockHeldTests
{
    private static readonly CultureInfo CA = new("en-CA");
    private static readonly string Hw = new DateTime(2026, 8, 18, 12, 0, 0).ToString(CA);
    private static readonly string Future = new DateTime(2026, 8, 18, 18, 0, 0).ToString(CA);
    private static readonly string Past = new DateTime(2026, 8, 18, 6, 0, 0).ToString(CA);

    private static bool Held(string hold = "", string armed = "", bool rawFloor = false, bool macValid = true)
        => mm_guard.Guardian.AnyBlockHeld(hold, armed, rawFloor, Hw, macValid);

    [Fact]
    public void MacInvalid_Holds_WhateverElseSays()
    {
        // The fail-closed apex: a tampered or unreadable config must NEVER stand the SYSTEM
        // watchdog down, even when every other signal reads "nothing armed". This is also the
        // empty-slot-list case - the one a plain Any() fold gets wrong by answering False.
        foreach (var hold in new[] { "", Past, Future, "not-a-date" })
        {
            foreach (var armed in new[] { "", "0", "3", "garbage" })
            {
                foreach (var raw in new[] { false, true })
                {
                    Assert.True(mm_guard.Guardian.AnyBlockHeld(hold, armed, raw, Hw, macValid: false));
                }
            }
        }
    }

    [Fact]
    public void NothingRecorded_StandsDown()
    {
        // The ONE combination that may stand the guardian down: a valid MAC, no slot section
        // left in the file, an empty guard horizon and an explicit zero armed count - i.e.
        // exactly what P39's TeardownAll leaves behind.
        Assert.False(Held(hold: "", armed: "0", rawFloor: false, macValid: true));
        Assert.False(Held(hold: "", armed: "", rawFloor: false, macValid: true));
    }

    [Fact]
    public void RawFloor_Holds_EvenWithBothGuardScalarsQuiet()
    {
        // P44's whole point: the floor is INDEPENDENT of the MAC'd scalars, so a config whose
        // [Guard] section was never written still holds the guardian while a slot names a block.
        Assert.True(Held(hold: "", armed: "0", rawFloor: true));
    }

    [Theory]
    // P43: HoldUntil is the EXTEND-ONLY horizon - the latest moment any slot could still hold.
    [InlineData("", false)]                 // nothing recorded
    [InlineData("not-a-date", true)]        // non-empty but unreadable => HOLD (fail-closed)
    [InlineData("   ", true)]               // ditto: non-empty is non-empty
    public void GuardHoldUntil_FailsClosedOnAnythingUnreadable(string hold, bool expected)
        => Assert.Equal(expected, mm_guard.Guardian.GuardHoldActive(hold, Hw));

    [Fact]
    public void GuardHoldUntil_IsMeasuredAgainstTheHighWaterMark_NotTheWallClock()
    {
        // B4: the horizon is compared to the service-written monotonic mark, so rolling the
        // system clock forward cannot bring a still-future horizon into the past.
        Assert.True(mm_guard.Guardian.GuardHoldActive(Future, Hw));
        Assert.False(mm_guard.Guardian.GuardHoldActive(Past, Hw));
        // An unreadable MARK cannot elapse the horizon either (fail-closed).
        Assert.True(mm_guard.Guardian.GuardHoldActive(Past, "not-a-date"));
    }

    [Theory]
    [InlineData("", false)]          // absent/blank: nothing recorded - the raw floor is the backstop
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("0", false)]         // the explicit teardown value
    [InlineData("-1", false)]        // a negative count arms nothing
    [InlineData("1", true)]
    [InlineData("8", true)]
    [InlineData(" 2 ", true)]        // trimmed
    [InlineData("garbage", true)]    // non-empty and unreadable => HOLD (fail-closed)
    [InlineData("2.5", true)]        // ditto - "I cannot read the count" means keep guarding
    public void GuardArmedCount_HoldsUnlessItIsPlainlyZeroOrAbsent(string? armed, bool expected)
        => Assert.Equal(expected, mm_guard.Guardian.GuardArmedCountHolds(armed!));

    [Fact]
    public void EverySignalIsWidenOnly_NoInputCanRemoveAHold()
    {
        // The load-bearing monotonicity: turning ANY one signal on can only ever ADD a hold.
        // A guardian gate that could be narrowed by a config value is a bypass by definition.
        foreach (var hold in new[] { "", Future })
        {
            foreach (var armed in new[] { "0", "2" })
            {
                foreach (var raw in new[] { false, true })
                {
                    var baseline = mm_guard.Guardian.AnyBlockHeld("", "0", false, Hw, true);
                    Assert.False(baseline);                                   // the quiet world
                    var held = mm_guard.Guardian.AnyBlockHeld(hold, armed, raw, Hw, true);
                    var anyOn = hold != "" || armed != "0" || raw;
                    Assert.Equal(anyOn, held);                                // on <=> held
                    if (held) Assert.True(mm_guard.Guardian.AnyBlockHeld(hold, armed, raw, Hw, false));
                }
            }
        }
    }
}

public class GuardianRawFloorTests
{
    // In-memory only: an IniFile that was never loaded from (or saved to) disk, so these
    // touch no file at all.
    private static mm_guard.IniFile Ini(params (string section, string key, string value)[] entries)
    {
        var ini = new mm_guard.IniFile();
        foreach (var (section, key, value) in entries)
        {
            ini.AddSection(section);
            ini.SetKeyValue(section, key, value);
        }
        return ini;
    }

    [Theory]
    // P44's three keys, at the first and the last position - "does the file still NAME a block?"
    [InlineData(1, "Until")]
    [InlineData(1, "StartAt")]
    [InlineData(1, "ScheduleSpec")]
    [InlineData(8, "Until")]
    [InlineData(8, "StartAt")]
    [InlineData(8, "ScheduleSpec")]
    public void RawFloor_AnySlotStartAtOrUntilOrSpec_Holds(int position, string key)
    {
        // Values are deliberately GARBAGE: the floor never parses, decrypts or MAC-checks -
        // a non-empty value means a block is named, and that alone holds. Over-approximating
        // here can only keep the guardian watching longer, never stand it down early.
        Assert.True(mm_guard.Program.RawSlotFloorHeld(Ini(("Slot" + position, key, "!!garbage!!"))));
    }

    [Fact]
    public void RawFloor_IsBoundedByMaxSlots_NotByTheStoredSlotCount()
    {
        // A forged [Slots] SlotCount=0 must not be able to silence the watchdog: the key scan
        // runs 1..MaxSlots and never consults the count. (It cannot silence enforcement either
        // - SlotCount is MAC-covered, so forging it freezes the config - but the floor
        // deliberately does not depend on that being true.)
        Assert.True(mm_guard.Program.RawSlotFloorHeld(
            Ini(("Slots", "SlotCount", "0"), ("Slot1", "Until", "x"))));
        Assert.True(mm_guard.Program.RawSlotFloorHeld(
            Ini(("Slots", "SlotCount", "0"), ("Slot8", "ScheduleSpec", "v2;1234567:0900-1700;sites=;apps="))));
    }

    [Fact]
    public void RawFloor_HoldsWheneverTheConfigDECLARESASlot_EvenWithNoSectionAtAll()
    {
        // The other leg, and the one that makes the floor a provable superset of the service's
        // slot hold rather than a shape-by-shape approximation. Service1.LoadSlots builds a
        // slot for pos = 1 To SlotCount whatever the section contains, and SlotHeld holds a
        // slot with no recorded end ("no recorded end" can never mean "over") - so a declared
        // but empty slot HOLDS THE SERVICE, and the guardian must not stand down under it.
        Assert.True(mm_guard.Program.RawSlotFloorHeld(Ini(("Slots", "SlotCount", "1"))));
        Assert.True(mm_guard.Program.RawSlotFloorHeld(
            Ini(("Slots", "SlotCount", "2"), ("Slot1", "Until", ""), ("Slot1", "StartAt", ""))));
        // Clamped like every other reader: garbage reads as 0 here (and breaks the MAC, which
        // is what actually holds), so this leg can never be widened by a forged count either.
        Assert.False(mm_guard.Program.RawSlotFloorHeld(Ini(("Slots", "SlotCount", "garbage"))));
        Assert.False(mm_guard.Program.RawSlotFloorHeld(Ini(("Slots", "SlotCount", "0"))));
    }

    [Fact]
    public void RawFloor_QuietWhenNoPositionNamesABlock()
    {
        // Empty config, and a config whose slot sections carry only LISTS: neither names a
        // block, so neither holds. This is the direction that lets the guardian ever exit.
        Assert.False(mm_guard.Program.RawSlotFloorHeld(new mm_guard.IniFile()));
        Assert.False(mm_guard.Program.RawSlotFloorHeld(
            Ini(("Slot1", "Sites", "reddit.com;"), ("Slot1", "Apps", "chrome.exe;"),
                ("Slot1", "Until", ""), ("Slot1", "StartAt", ""), ("Slot1", "ScheduleSpec", ""))));
        // Whitespace is not a block either (IsNullOrWhiteSpace, matching the notifier's twin).
        Assert.False(mm_guard.Program.RawSlotFloorHeld(Ini(("Slot3", "Until", "   "))));
    }

    [Fact]
    public void RawFloor_FailsClosedOnAnUnreadableIni()
        => Assert.True(mm_guard.Program.RawSlotFloorHeld(null!));

    [Fact]
    public void RawFloor_IgnoresPositionsBeyondMaxSlots()
    {
        // MaxSlots is the cap, so a hand-written [Slot9] is simply not a position. It cannot
        // hold the guardian - but it also cannot exist under a valid MAC (the canonical is
        // built over the CLAMPED count), so nothing is lost.
        Assert.False(mm_guard.Program.RawSlotFloorHeld(Ini(("Slot9", "Until", "x"))));
        Assert.Equal(8, mm_guard.ConfigIntegrity.MaxSlots);
    }
}

public class GuardianServiceSlotParityTests
{
    private static readonly CultureInfo CA = new("en-CA");
    private static readonly DateTime AsOf = new(2026, 8, 18, 12, 0, 0);
    private static readonly string Hw = AsOf.ToString(CA);
    private static readonly string Future = new DateTime(2026, 8, 18, 18, 0, 0).ToString(CA);
    private static readonly string Past = new DateTime(2026, 8, 18, 6, 0, 0).ToString(CA);
    private const long Grace = 5;

    [Theory]
    [InlineData("", "")]
    [InlineData("", "2026-08-18 12:00:00")]
    [InlineData("2026-08-18 18:00:00", "2026-08-18 12:00:00")]
    [InlineData("2026-08-18 06:00:00", "2026-08-18 12:00:00")]
    [InlineData("not-a-date", "2026-08-18 12:00:00")]
    [InlineData("2026-08-18 18:00:00", "not-a-date")]
    public void GuardHoldActive_IsDefinedAsTheScheduleActiveShape_AndAgreesWithTheService(string deadline, string mark)
    {
        // GuardHoldActive is DEFINED AS ScheduleActive rather than re-derived: the question is
        // identical in shape (a stored deadline vs the monotonic mark, "" = nothing recorded,
        // non-empty-but-unparseable = HELD) and two hand-written copies of one policy drift.
        // Pinned equal to BOTH the guardian's own copy and the service's, so the deadline
        // semantics can never fork across the two halves of the pair.
        var expected = mm_guard.Guardian.ScheduleActive(deadline, mark);
        Assert.Equal(expected, mm_guard.Guardian.GuardHoldActive(deadline, mark));
        Assert.Equal(monkmode.Service1.ScheduleActive(deadline, mark), mm_guard.Guardian.GuardHoldActive(deadline, mark));
    }

    private static monkmode.Service1.SlotState Slot(string until = "", string startAt = "",
                                                    string scheduleActiveUntil = "", string scheduleSpec = "")
        => new()
        {
            Id = "1",
            UntilText = until,
            StartAt = startAt,
            ScheduleActiveUntil = scheduleActiveUntil,
            ScheduleSpec = scheduleSpec,
            Sites = new List<string>(),
            Apps = new List<string>(),
            UrlPatterns = new List<string>(),
        };

    // The same slot shape on both sides: the service reads the parsed SlotState, the guardian
    // reads the RAW ini keys. Values are handed to the guardian side verbatim - it never
    // decrypts, so a plaintext stand-in is exactly as non-empty as a ciphertext would be.
    // `declared` writes the [Slots] SlotCount the service must have read to produce this slot
    // at all; passing false isolates the raw KEY leg of the floor from the count leg.
    private static mm_guard.IniFile RawOf(monkmode.Service1.SlotState s, bool declared)
    {
        var ini = new mm_guard.IniFile();
        if (declared)
        {
            ini.AddSection("Slots");
            ini.SetKeyValue("Slots", "SlotCount", "1");
        }
        ini.AddSection("Slot1");
        ini.SetKeyValue("Slot1", "Until", s.UntilText);
        ini.SetKeyValue("Slot1", "StartAt", s.StartAt);
        ini.SetKeyValue("Slot1", "ScheduleSpec", s.ScheduleSpec);
        return ini;
    }

    public static readonly TheoryData<string, string, string, string> SlotShapes = new()
    {
        { "", "", "", "" },                                                   // nothing armed
        { Future, "", "", "" },                                               // ACTIVE
        { Past, "", "", "" },                                                 // expired, not yet retired
        { "", Future, "", "" },                                               // PENDING
        { monkmode.Service1.ScheduleOnlyExpiredUntil, "", Future, "v2;1234567:0900-1700;sites=x.com;apps=" }, // window OPEN
        { monkmode.Service1.ScheduleOnlyExpiredUntil, "", "", "v2;1234567:0900-1700;sites=x.com;apps=" },     // between windows
    };

    [Theory]
    [MemberData(nameof(SlotShapes))]
    public void GuardianHold_IsASupersetOfTheServiceSlotHold(string until, string startAt, string activeUntil, string spec)
    {
        // THE SAFETY PROPERTY of the whole slice: the guardian must never stand down while
        // the service still considers a slot held, or a killed service would stay killed with
        // blocks still enforcing. Proved with BOTH [Guard] scalars blank, so the raw floor
        // alone has to carry every shape - including the ones the v9 mirror never mentions.
        var slot = Slot(until, startAt, activeUntil, spec);
        foreach (var macValid in new[] { true, false })
        {
            var serviceHeld = monkmode.Service1.AnyBlockHeld(
                new List<monkmode.Service1.SlotState> { slot }, AsOf, Grace, macValid, Hw);
            var guardianHeld = mm_guard.Guardian.AnyBlockHeld(
                "", "", mm_guard.Program.RawSlotFloorHeld(RawOf(slot, declared: true)), Hw, macValid);
            if (serviceHeld) Assert.True(guardianHeld);
        }

        // And the split between the floor's two legs, so neither becomes decoration: the raw
        // KEY scan alone carries every shape that records a date, and the all-empty section -
        // which the service still holds, because "no recorded end" is not "over" - is exactly
        // the case the SlotCount leg exists for.
        var keysOnly = mm_guard.Program.RawSlotFloorHeld(RawOf(slot, declared: false));
        Assert.Equal(until != "" || startAt != "" || spec != "", keysOnly);
    }

    [Fact]
    public void V9ScheduleOnlyConfig_TheOneNarrowingS4Costs_IsAlignedWithTheServicesOwnPeerGate()
    {
        // S4 stops the guardian reading the global [Schedule] Spec (P44 replaces that legacy
        // floor). For a v9-SHAPED schedule-only config - which today's `monkmode schedule`
        // still writes, until S5 makes a schedule a slot - that config has NO slots, so
        // BETWEEN windows nothing holds the guardian any more and it exits at a window's close
        // instead of surviving to the next one. Pinned here so the change is deliberate rather
        // than discovered, together with the bound that makes it acceptable: the SERVICE's own
        // guardian-spawn gate (enforcementHeld = BlockHeld OrElse slotsHeld) is ALREADY False
        // for that config between windows, so it would not respawn a guardian killed in that
        // gap either. S4 aligns the two halves instead of leaving them disagreeing.
        var sentinel = monkmode.Service1.ScheduleOnlyExpiredUntil;
        var noSlots = new List<monkmode.Service1.SlotState>();

        // BETWEEN windows (ActiveUntil = ""): both halves now say "not held".
        Assert.False(monkmode.Service1.BlockHeld(sentinel, AsOf, Grace, true, "", Hw)
                     || monkmode.Service1.AnyBlockHeld(noSlots, AsOf, Grace, true, Hw));
        Assert.True(mm_guard.Guardian.EffectiveExit(sentinel, "", "", "", Hw, Grace, true, false));
        Assert.False(mm_guard.Guardian.AnyBlockHeld("", "", false, Hw, true));

        // INSIDE a window (ActiveUntil in the future): both still HOLD, unchanged by S4 - the
        // open window is a hard hold on the guardian's side and enforcementHeld on the
        // service's, which is the case that actually matters.
        Assert.True(monkmode.Service1.BlockHeld(sentinel, AsOf, Grace, true, Future, Hw));
        Assert.False(mm_guard.Guardian.EffectiveExit(sentinel, "", "", Future, Hw, Grace, true, false));

        // And a SLOT-borne schedule is untouched either way: its rule lives in the slot, so
        // the raw floor holds the guardian between windows exactly as before.
        var slotSchedule = Slot(until: sentinel, scheduleSpec: "v2;1234567:0900-1700;sites=x.com;apps=");
        Assert.True(mm_guard.Program.RawSlotFloorHeld(RawOf(slotSchedule, declared: true)));
        Assert.True(mm_guard.Guardian.AnyBlockHeld(
            "", "", mm_guard.Program.RawSlotFloorHeld(RawOf(slotSchedule, declared: true)), Hw, true));
    }

    [Fact]
    public void TheGuardianCadenceStillMatchesTheService()
    {
        // Restated here because S4 changes WHAT the guardian reads, not WHEN: both halves must
        // still agree on "expired" within one tick of each other.
        Assert.Equal(monkmode.Service1.TimerIntervalMs, mm_guard.Program.TickIntervalMs);
        Assert.Equal(monkmode.Service1.ExpiryGraceSeconds, mm_guard.Program.ExpiryGraceSeconds);
    }
}

// P39's other half, driven through the REAL config writers: after a teardown - and after
// retiring every slot one at a time - the raw floor must genuinely go quiet, or the guardian
// could never exit and the fold would be a wedge instead of a defence. Uses the test-bin
// config (wiped in finally) and GUID temp paths under the test bin; never the real hosts
// file, the registry, the SCM or any deployed config. Nothing is armed on the machine.
[Collection("CliIniWriters")]
public class GuardianTeardownStandDownTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    // NEVER construct a Service1 directly: construction alone starts the live 10s tick.
    private static monkmode.Service1 Svc() => TestSvc.New();

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "s4-" + Guid.NewGuid().ToString("N"));
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
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static bool Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false).Ok;

    private static mm_guard.IniFile GuardView()
    {
        var ini = new mm_guard.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static bool FloorHeld() => mm_guard.Program.RawSlotFloorHeld(GuardView());

    [Fact]
    public void ZeroSlotConfig_StandsDown()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com"));
            Assert.True(Arm("b.com"));
            // Two real armed slots: the floor holds, and so does the whole fold.
            var armedView = GuardView();
            Assert.True(mm_guard.Program.RawSlotFloorHeld(armedView));
            // AND THIS IS WHY P44 EXISTS. [Guard] ArmedCount counts only the slots that keep
            // the machinery up WITHOUT an open block of their own (schedule + PENDING), so two
            // ordinary ACTIVE blocks leave it at "0". The MAC'd scalars alone would therefore
            // NOT hold here - the raw floor is what does, and dropping it would stand the
            // watchdog down over two live blocks.
            Assert.Equal("0", armedView.GetKeyValue("Guard", "ArmedCount"));
            Assert.False(mm_guard.Guardian.GuardArmedCountHolds(armedView.GetKeyValue("Guard", "ArmedCount")));
            Assert.True(mm_guard.Guardian.AnyBlockHeld("", armedView.GetKeyValue("Guard", "ArmedCount"),
                                                       mm_guard.Program.RawSlotFloorHeld(armedView),
                                                       "", macValid: true));

            // P39 step (1): the zero-slot config the whole-machine teardown persists.
            Assert.True(Svc().PersistZeroSlotConfigAt(MonkMode.Blocker.IniPath()));

            var after = GuardView();
            // Every [SlotN] section is GONE, so the raw floor is quiet...
            Assert.False(mm_guard.Program.RawSlotFloorHeld(after));
            // ...and both MAC'd scalars were explicitly cleared, so neither holds either.
            Assert.Equal("", after.GetKeyValue("Guard", "HoldUntil"));
            Assert.Equal("0", after.GetKeyValue("Guard", "ArmedCount"));
            Assert.False(mm_guard.Guardian.GuardHoldActive(after.GetKeyValue("Guard", "HoldUntil"), ""));
            Assert.False(mm_guard.Guardian.GuardArmedCountHolds(after.GetKeyValue("Guard", "ArmedCount")));
            // So the guardian genuinely stands down - the direction that stops the fold
            // becoming a watchdog that can never exit.
            Assert.False(mm_guard.Guardian.AnyBlockHeld(after.GetKeyValue("Guard", "HoldUntil"),
                                                        after.GetKeyValue("Guard", "ArmedCount"),
                                                        mm_guard.Program.RawSlotFloorHeld(after),
                                                        "", macValid: true));
        }
        finally
        {
            Wipe();
        }
    }

    [Fact]
    public void RetiringEverySlotOneAtATime_AlsoLeavesTheFloorQuiet()
    {
        // The other route to an empty machine: slots retiring individually (RetireSlotAt
        // compacts and RemoveSection's the freed TRAILING position). A retired slot must leave
        // nothing behind that keeps the guardian alive - slots are REMOVED, never flagged.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com"));
            Assert.True(Arm("b.com"));
            var svc = Svc();
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");

            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(), snapshot, hosts, "1"));
            Assert.True(FloorHeld());        // the survivor still names a block
            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(), snapshot, hosts, "2"));
            Assert.False(FloorHeld());       // nothing left to name one
        }
        finally
        {
            Drop(dir);
            Wipe();
        }
    }
}
