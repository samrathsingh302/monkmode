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

// MonkMode.Tests - T2 (audit A2, 05/10/2026): an immediate arm's end lives in the HighWater frame.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - RealSpanUntil is a UTC difference: a `--until` armed 24/10/2026 22:00 BST for 25/10 09:00
//     GMT is 12h of real time. A wall subtraction gives 11h, and HighWater (which credits real
//     time through the fall-back) reached that Until an hour early.
//   - a `--for` span is the TimeSpan itself, never recomputed from a wall end.
//   - FrameEndFor = mark + span, Nothing on an unparseable mark (the caller keeps the wall end).
//   - ArmSlot writes Until = HighWater + span on the APPEND path: a mark 1h AHEAD of the wall (a
//     fall-back or westward zone change with a slot running) no longer lifts a new 45m block on
//     its first tick, and a mark 1h BEHIND no longer over-runs it by the lag.
//   - (T2, 12/10/2026) a PENDING arm's DurationSeconds is the real span: 24/10 22:00 -> 25/10
//     09:00 stores 43200, not the 39600 the wall difference gave (the service activates at
//     HighWater + DurationSeconds, so the old value lifted an hour early); a typed start inside
//     a spring-forward gap reads as the earlier instant, so a guess only lengthens the block.
//
// Fences honoured: zones by id, never the machine's current zone in an assertion; the only
// files touched are the test-bin ini/backup/snapshot (wiped in finally). Nothing is ever armed
// for real - the service is treated as ABSENT (injected, never queried).

using System.Globalization;
using Xunit.Abstractions;

namespace MonkMode.Tests;

[Collection("CliIniWriters")]
public class ArmFrameTests
{
    private readonly ITestOutputHelper _output;
    public ArmFrameTests(ITestOutputHelper output) => _output = output;

    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly TimeZoneInfo Uk = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    private static readonly MonkMode.Simple3Des Enc = new("mm_textbox");

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static MonkMode.IniFile Load()
    {
        var ini = new MonkMode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static DateTime Stamp(MonkMode.IniFile ini, string section, string key)
        => DateTime.Parse(Enc.DecryptData(ini.GetKeyValue(section, key)), EnCa);

    /// <summary>Move the stored HighWater to `mark` (and, with `retireAll`, empty the slot list)
    /// and re-stamp the MAC with the existing key, as a running service's tick would have.</summary>
    private static void RestampHighWater(DateTime mark, bool retireAll = false)
    {
        var ini = Load();
        ini.SetKeyValue("Time", "HighWater", Enc.EncryptData(mark.ToString(EnCa)));
        if (retireAll)
        {
            ini.RemoveSection("Slot1");
            ini.SetKeyValue("Slots", "SlotCount", "0");
        }
        var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
        Assert.NotNull(key);
        ini.SetKeyValue("Integrity", "Mac",
                        MonkMode.ConfigIntegrity.ComputeConfigMac(MonkMode.Blocker.CanonicalFromIni(ini), key));
        ini.Save(MonkMode.Blocker.IniPath());
        Assert.True(MonkMode.Blocker.ConfigIsMacValid());
    }

    // ---- 1. RealSpanUntil: real time, not wall arithmetic ----

    [Fact]
    public void RealSpanUntil_FallBackInsideSpan_IsTwelveHours()
    {
        var armed = new DateTime(2026, 10, 24, 22, 0, 0);   // BST
        var until = new DateTime(2026, 10, 25, 9, 0, 0);    // GMT (clocks fell back at 02:00)
        Assert.Equal(TimeSpan.FromHours(12), MonkMode.Blocker.RealSpanUntil(armed, until, Uk));
    }

    [Fact]
    public void RealSpanUntil_NoTransition_IsThePlainDifference()
    {
        var armed = new DateTime(2026, 11, 10, 10, 0, 0);
        var until = new DateTime(2026, 11, 10, 18, 30, 0);
        Assert.Equal(until - armed, MonkMode.Blocker.RealSpanUntil(armed, until, Uk));
    }

    [Fact]
    public void RealSpanUntil_AmbiguousEnd_ReadsTheLaterInstant()
    {
        // 01:30 on 25/10/2026 happens twice; a guess may only lengthen the block.
        var armed = new DateTime(2026, 10, 25, 0, 0, 0);    // BST
        var until = new DateTime(2026, 10, 25, 1, 30, 0);
        Assert.Equal(TimeSpan.FromHours(2.5), MonkMode.Blocker.RealSpanUntil(armed, until, Uk));
    }

    [Fact]
    public void ForSpan_IsTheTimeSpanItself_NeverAWallRecompute()
    {
        // `--for 11h` at 22:00 BST on 24/10: the display end is the wall 09:00, whose real span is
        // 12h - the arm must carry 11h, so the stored Until is mark + 11h exactly.
        var armed = new DateTime(2026, 10, 24, 22, 0, 0);
        var forSpan = TimeSpan.FromHours(11);
        Assert.Equal(TimeSpan.FromHours(12), MonkMode.Blocker.RealSpanUntil(armed, armed.Add(forSpan), Uk));

        Wipe();
        try
        {
            var now = DateTime.Now;
            // endsAt deliberately disagrees with the span: the span must win.
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "", null,
                                             now.AddHours(12), false, realSpan: forSpan);
            Assert.True(r.Ok);
            var ini = Load();
            Assert.Equal(forSpan, Stamp(ini, "Slot1", "Until") - Stamp(ini, "Time", "HighWater"));
        }
        finally { Wipe(); }
    }

    // ---- 2. FrameEndFor ----

    [Fact]
    public void FrameEndFor_UnparseableMark_IsNothing()
    {
        Assert.Null(MonkMode.Blocker.FrameEndFor("not a date", TimeSpan.FromMinutes(45)));
        Assert.Null(MonkMode.Blocker.FrameEndFor("", TimeSpan.FromMinutes(45)));
    }

    [Fact]
    public void FrameEndFor_IsMarkPlusSpan()
    {
        var mark = new DateTime(2026, 10, 25, 11, 0, 0);
        Assert.Equal(mark.AddMinutes(45), MonkMode.Blocker.FrameEndFor(mark.ToString(EnCa), TimeSpan.FromMinutes(45)));
    }

    // ---- 3. ArmSlot: the append path writes in the HighWater frame ----

    [Theory]
    [InlineData(1)]     // mark AHEAD of the wall: the fall-back / westward-zone under-block
    [InlineData(-1)]    // mark BEHIND the wall: the lagging-frame over-block mirror
    public void AppendArm_UntilIsHighWaterPlusSpan_WhateverTheMarkLead(int leadHours)
    {
        Wipe();
        try
        {
            // Slot 1 keeps a slot running, so the append path never re-seeds the frame.
            var first = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "", null,
                                                 DateTime.Now.AddDays(2), false);
            Assert.True(first.Ok);
            var mark = DateTime.Now.AddHours(leadHours);
            mark = mark.AddTicks(-(mark.Ticks % TimeSpan.TicksPerSecond));
            RestampHighWater(mark);

            var forSpan = TimeSpan.FromMinutes(45);
            var second = MonkMode.Blocker.ArmSlot(new[] { "x.com" }, Array.Empty<string>(), "", null,
                                                  DateTime.Now.Add(forSpan), false, realSpan: forSpan);
            Assert.True(second.Ok);
            Assert.False(second.FreshRewrite);

            var ini = Load();
            var highWater = Stamp(ini, "Time", "HighWater");
            Assert.Equal(mark, highWater);                          // the frame was not re-seeded
            var until = Stamp(ini, "Slot2", "Until");
            Assert.Equal(forSpan, until - highWater);
            Assert.False(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), highWater, 5));
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
        }
        finally { Wipe(); }
    }

    [Fact]
    public void FreshArm_UntilIsTheSeededMarkPlusSpan()
    {
        Wipe();
        try
        {
            var forSpan = TimeSpan.FromMinutes(45);
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "", null,
                                             DateTime.Now.Add(forSpan), false, realSpan: forSpan);
            Assert.True(r.Ok);
            Assert.True(r.FreshRewrite);
            var ini = Load();
            Assert.Equal(forSpan, Stamp(ini, "Slot1", "Until") - Stamp(ini, "Time", "HighWater"));
            Assert.Equal(Stamp(ini, "Time", "HighWater"), Stamp(ini, "CurrentTime", "Now"));   // one instant
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ReseedArm_UntilIsTheReseededMarkPlusSpan()
    {
        // Zero slots + a stale mark: the append path re-seeds (F76), and Until rides the NEW mark.
        Wipe();
        try
        {
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "", null,
                                                 DateTime.Now.AddDays(2), false).Ok);
            var stale = DateTime.Now.AddHours(-2);
            stale = stale.AddTicks(-(stale.Ticks % TimeSpan.TicksPerSecond));
            RestampHighWater(stale, retireAll: true);

            var forSpan = TimeSpan.FromMinutes(45);
            var r = MonkMode.Blocker.ArmSlot(new[] { "x.com" }, Array.Empty<string>(), "", null,
                                             DateTime.Now.Add(forSpan), false, realSpan: forSpan);
            Assert.True(r.Ok);
            Assert.False(r.FreshRewrite);
            var ini = Load();
            var highWater = Stamp(ini, "Time", "HighWater");
            Assert.True(highWater > stale.AddHours(1));                 // re-seeded to now
            Assert.Equal(forSpan, Stamp(ini, "Slot1", "Until") - highWater);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AppendArm_WithNoSpan_KeepsTheAbsoluteEnd()
    {
        // The WriteConfig shim and direct test arms pass only an absolute end and store it as
        // given (no production caller arms without a span - DoBlock always passes one).
        Wipe();
        try
        {
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "", null,
                                                 DateTime.Now.AddDays(2), false).Ok);
            var mark = DateTime.Now.AddHours(1);
            mark = mark.AddTicks(-(mark.Ticks % TimeSpan.TicksPerSecond));
            RestampHighWater(mark);

            var ends = new DateTime(2027, 3, 1, 12, 0, 0);
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "x.com" }, Array.Empty<string>(), "", null,
                                                 ends, false).Ok);
            Assert.Equal(ends, Stamp(Load(), "Slot2", "Until"));
        }
        finally { Wipe(); }
    }

    // ---- 4. PENDING arms store a REAL duration (T2, 12/10/2026) ----
    //
    // The service activates a pending slot at HighWater + DurationSeconds, and HighWater credits
    // real time, so the stored duration is the real span DoBlock passes - never the wall
    // difference, which across the 25/10/2026 fall-back stored 11h for a 12h window.

    private static MonkMode.IniFile ArmPending(DateTime start, DateTime end, TimeSpan? realSpan)
    {
        var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "", start,
                                         end, false, realSpan: realSpan);
        Assert.True(r.Ok);
        Assert.True(MonkMode.Blocker.ConfigIsMacValid());
        return Load();
    }

    [Fact]
    public void PendingArm_FallBackInsideWindow_StoresTwelveHours()
    {
        var start = new DateTime(2026, 10, 24, 22, 0, 0);   // BST
        var end = new DateTime(2026, 10, 25, 9, 0, 0);      // GMT
        Wipe();
        try
        {
            var ini = ArmPending(start, end, MonkMode.Blocker.RealSpanUntil(start, end, Uk));
            Assert.Equal("43200", ini.GetKeyValue("Slot1", "DurationSeconds"));
            Assert.Equal("", ini.GetKeyValue("Slot1", "Until"));             // P29: still no Until
            Assert.Equal(start, Stamp(ini, "Slot1", "StartAt"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void PendingArm_WithNoSpan_KeepsTheWallDifference()
    {
        // The back-compat line: the WriteConfig shim and span-less test arms store end - start.
        var start = new DateTime(2026, 10, 24, 22, 0, 0);
        var end = new DateTime(2026, 10, 25, 9, 0, 0);
        Wipe();
        try
        {
            var ini = ArmPending(start, end, null);
            Assert.Equal("39600", ini.GetKeyValue("Slot1", "DurationSeconds"));
            Assert.Equal("", ini.GetKeyValue("Slot1", "Until"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void PendingArm_ForSpan_IsTheTimeSpanItself()
    {
        // `--start "2026-10-25 00:30" --for 2h`: the window holds 01:00-02:00 twice, so the real
        // span to the wall end would be 3h - the arm must carry the 2h that was asked for.
        var start = new DateTime(2026, 10, 25, 0, 30, 0);
        var forSpan = TimeSpan.FromHours(2);
        Assert.Equal(TimeSpan.FromHours(3), MonkMode.Blocker.RealSpanUntil(start, start.Add(forSpan), Uk));
        Wipe();
        try
        {
            var ini = ArmPending(start, start.Add(forSpan), forSpan);
            Assert.Equal("7200", ini.GetKeyValue("Slot1", "DurationSeconds"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void PendingArm_SpringForwardInsideWindow_StoresTwoHours()
    {
        var start = new DateTime(2027, 3, 28, 0, 30, 0);    // GMT
        var end = new DateTime(2027, 3, 28, 3, 30, 0);      // BST (clocks went forward at 01:00)
        Wipe();
        try
        {
            var ini = ArmPending(start, end, MonkMode.Blocker.RealSpanUntil(start, end, Uk));
            Assert.Equal("7200", ini.GetKeyValue("Slot1", "DurationSeconds"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void RealSpanUntil_StartInsideTheSpringForwardGap_ReadsTheEarlierInstant()
    {
        // 01:30 on 28/03/2027 never shows on a UK wall. A typed pending start there reads as
        // 00:30 UTC (the earlier reading), so the guess lengthens the block; an end there keeps
        // reading as the later instant, 01:30 UTC.
        var gap = new DateTime(2027, 3, 28, 1, 30, 0);
        Assert.Equal(TimeSpan.FromHours(2), MonkMode.Blocker.RealSpanUntil(gap, new DateTime(2027, 3, 28, 3, 30, 0), Uk));
        Assert.Equal(TimeSpan.FromHours(1.5), MonkMode.Blocker.RealSpanUntil(new DateTime(2027, 3, 28, 0, 0, 0), gap, Uk));
    }

    [Fact]
    public void RealSpanUntil_UnspecifiedKindStart_AgreesWithTheLocalKindReading()
    {
        // TryParseStart yields an Unspecified-kind start for a typed date, a Local-kind one for
        // `+90m` (DateTime.Now + span). DoBlock reads both in TimeZoneInfo.Local; the two must
        // agree. No machine-zone value is asserted - only that the two readings match.
        var start = new DateTime(2026, 10, 24, 22, 0, 0);
        var end = new DateTime(2026, 10, 25, 9, 0, 0);
        foreach (var tz in new[] { Uk, TimeZoneInfo.Local })
        {
            Assert.Equal(MonkMode.Blocker.RealSpanUntil(DateTime.SpecifyKind(start, DateTimeKind.Local), end, tz),
                         MonkMode.Blocker.RealSpanUntil(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), end, tz));
        }
    }

    [Fact]
    public void RealSpanUntil_LocalKindStartInsideTheMachineZonesGap_ReadsTheEarlierInstant()
    {
        // WallToUtc short-circuits a Local-kind wall through ToUniversalTime() only OUTSIDE the
        // machine zone's spring-forward gap: inside it ToUniversalTime() applies the standard
        // offset (the LATER reading), so a `--start +Nm` landing in the gap would disagree with
        // the typed-date path. The gap is found from TimeZoneInfo.Local's own rules (no zone or
        // offset is asserted): the first invalid wall time within three years.
        var tz = TimeZoneInfo.Local;
        var probe = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        var limit = probe.AddYears(3);
        while (probe < limit && !tz.IsInvalidTime(probe)) probe = probe.AddMinutes(30);
        if (probe >= limit)
        {
            // No gap in this zone: the branch is unreachable here. Pin the ordinary agreement and
            // say so in the output - this is not a proof of the gap clause.
            var ordinary = new DateTime(2026, 10, 24, 22, 0, 0);
            Assert.Equal(MonkMode.Blocker.RealSpanUntil(DateTime.SpecifyKind(ordinary, DateTimeKind.Local), ordinary.AddHours(2), tz),
                         MonkMode.Blocker.RealSpanUntil(DateTime.SpecifyKind(ordinary, DateTimeKind.Unspecified), ordinary.AddHours(2), tz));
            _output.WriteLine($"GAP BRANCH NOT REACHABLE: {tz.Id} has no spring-forward gap within three years; only the ordinary Local/Unspecified agreement was checked.");
            return;
        }
        var gapStart = probe;
        while (tz.IsInvalidTime(gapStart.AddMinutes(-1))) gapStart = gapStart.AddMinutes(-1);
        var gapEnd = probe;
        while (tz.IsInvalidTime(gapEnd)) gapEnd = gapEnd.AddMinutes(1);
        var inside = gapStart.AddTicks((gapEnd - gapStart).Ticks / 2);
        var end = gapEnd.AddHours(2);

        // The two candidate offsets either side of the gap; the larger is the earlier instant.
        var before = tz.GetUtcOffset(gapStart.AddMinutes(-1));
        var after = tz.GetUtcOffset(gapEnd);
        var earliestUtc = inside - (before > after ? before : after);
        var expected = TimeZoneInfo.ConvertTimeToUtc(end, tz) - earliestUtc;

        var local = MonkMode.Blocker.RealSpanUntil(DateTime.SpecifyKind(inside, DateTimeKind.Local), end, tz);
        var unspecified = MonkMode.Blocker.RealSpanUntil(DateTime.SpecifyKind(inside, DateTimeKind.Unspecified), end, tz);
        Assert.Equal(expected, unspecified);
        Assert.Equal(expected, local);
    }

    // ---- 5. the existing refusals are unchanged ----

    [Fact]
    public void UntilInThePast_IsStillRefused()
    {
        var now = new DateTime(2026, 10, 24, 22, 0, 0);
        Assert.Equal(MonkMode.Program.WindowRefusal.TooShort,
                     MonkMode.Program.ClassifyBlockWindow(false, now, now.AddHours(-1)));
    }

    // ---- 6. WallEndFor: the wall clock a `--for` block really ends at (T4, 13/10/2026) ----
    //
    // DoBlock printed `start + span` as a `--for` block's end, a wall sum, while the span runs in
    // real time: 2h armed 00:30 on 25/10/2026 printed "until 02:30" for a block that ends at 01:30
    // GMT. Display only - the stored Until/DurationSeconds are pinned by sections 3 and 4.

    [Fact]
    public void WallEndFor_FallBackInside_EndsAnHourEarlierOnTheWall()
    {
        var start = new DateTime(2026, 10, 25, 0, 30, 0);   // BST; clocks go back at 02:00
        Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0), MonkMode.Blocker.WallEndFor(start, TimeSpan.FromHours(2), Uk));
    }

    [Fact]
    public void WallEndFor_SpringForwardInside_EndsAnHourLaterOnTheWall()
    {
        var start = new DateTime(2027, 3, 28, 0, 30, 0);    // GMT; clocks go forward at 01:00
        Assert.Equal(new DateTime(2027, 3, 28, 3, 30, 0), MonkMode.Blocker.WallEndFor(start, TimeSpan.FromHours(2), Uk));
    }

    [Fact]
    public void WallEndFor_OrdinaryDay_IsTheWallSum()
    {
        var start = new DateTime(2026, 7, 14, 21, 15, 0);
        var span = TimeSpan.FromMinutes(150);
        Assert.Equal(start + span, MonkMode.Blocker.WallEndFor(start, span, Uk));
    }

    [Fact]
    public void WallEndFor_UnspecifiedKindStart_AgreesWithTheLocalKindReading()
    {
        // The immediate print's start is DateTime.Now (Local kind), a typed pending start is
        // Unspecified; read in the same zone they must give the same end. No machine-zone value
        // is asserted - only that the two readings match.
        var start = new DateTime(2026, 10, 25, 0, 30, 0);
        foreach (var tz in new[] { Uk, TimeZoneInfo.Local })
        {
            Assert.Equal(MonkMode.Blocker.WallEndFor(DateTime.SpecifyKind(start, DateTimeKind.Local), TimeSpan.FromHours(2), tz),
                         MonkMode.Blocker.WallEndFor(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), TimeSpan.FromHours(2), tz));
        }
    }

    [Fact]
    public void WallEndFor_PastTheLastConvertibleInstant_FallsBackWithoutThrowing()
    {
        // UTC-5: 20:00 on the last day has no UTC instant (01:00 on 01/01/10000), so the
        // conversion throws and the wall sum is printed; past the wall's own range, MaxValue.
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var start = new DateTime(9999, 12, 31, 20, 0, 0);
        Assert.Equal(start + TimeSpan.FromHours(1), MonkMode.Blocker.WallEndFor(start, TimeSpan.FromHours(1), eastern));
        Assert.Equal(DateTime.MaxValue, MonkMode.Blocker.WallEndFor(start, TimeSpan.FromDays(2), eastern));
    }
}
