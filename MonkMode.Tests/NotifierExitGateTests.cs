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

// MonkMode.Tests - A3: the notifier's [User] Done=yes exit gate.
//
// Done sits outside the MAC canonical, so a single text edit used to end the notifier -
// and with it the user-session app-kill, the URL watcher and the block page - for the
// rest of a block, and the guardian's 10s relaunch hit the same exit. ShouldExitOnDone
// now honours Done=yes only when the config itself agrees nothing is armed: MAC valid,
// no raw slot, no schedule held (Spec armed OR a cleared schedule's window still open
// by ActiveUntil vs HighWater, ScheduleWindowOpen).
//
// Pure gates plus a temp ini only. No form is constructed, no toast or tray icon is
// shown, mm_notify.exe is never launched, and nothing under %ProgramData% is touched.

using System.Globalization;

namespace MonkMode.Tests;

public class NotifierExitGateTests
{
    private static readonly CultureInfo CA = new("en-CA");

    private static string Stamp(DateTime dt) => dt.ToString(CA);

    // ---------------------------------------------------------------
    // ShouldExitOnDone - the truth table
    // ---------------------------------------------------------------

    [Fact]
    public void Done_ValidMac_NoSlots_NoSchedule_Exits()
    {
        Assert.True(mm_notify.Form1.ShouldExitOnDone("yes", true, 0, false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public void Done_WithAnyRawSlot_DoesNotExit(int rawSlotCount)
    {
        Assert.False(mm_notify.Form1.ShouldExitOnDone("yes", true, rawSlotCount, false));
    }

    [Fact]
    public void Done_WithInvalidMac_DoesNotExit()
    {
        Assert.False(mm_notify.Form1.ShouldExitOnDone("yes", false, 0, false));
    }

    [Fact]
    public void Done_WithScheduleArmed_DoesNotExit()
    {
        Assert.False(mm_notify.Form1.ShouldExitOnDone("yes", true, 0, true));
    }

    [Fact]
    public void Done_WithScheduleSpecArmed_DoesNotExit()
    {
        // The Spec term exactly as the call sites build it: ScheduleArmed(macValid, Spec).
        bool armed = mm_notify.Form1.ScheduleArmed(true, "v2;12345:0900-1700;sites=x.com;apps=");
        Assert.True(armed);
        Assert.False(mm_notify.Form1.ShouldExitOnDone("yes", true, 0, armed));
    }

    [Fact]
    public void Done_ClearedSchedule_WindowStillOpen_DoesNotExit()
    {
        // schedule --clear: Spec emptied, ActiveUntil kept, the window is ahead of HighWater.
        var hw = new DateTime(2026, 10, 5, 10, 0, 0);
        bool held = mm_notify.Form1.ScheduleArmed(true, "")
                    || mm_notify.Form1.ScheduleWindowOpen(Stamp(hw.AddHours(1)), Stamp(hw));
        Assert.True(held);
        Assert.False(mm_notify.Form1.ShouldExitOnDone("yes", true, 0, held));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("YES")]
    [InlineData("Yes")]
    [InlineData(" yes")]
    [InlineData("yes ")]
    public void Done_NotExactlyYes_DoesNotExit(string? done)
    {
        // StrComp under Option Compare Binary: case- and whitespace-exact, as before A3.
        Assert.False(mm_notify.Form1.ShouldExitOnDone(done!, true, 0, false));
    }

    // ---------------------------------------------------------------
    // ScheduleWindowOpen - the cleared-schedule term
    // ---------------------------------------------------------------

    [Fact]
    public void WindowOpen_ActiveUntilAheadOfHighWater_IsOpen()
    {
        var hw = new DateTime(2026, 10, 5, 10, 0, 0);
        Assert.True(mm_notify.Form1.ScheduleWindowOpen(Stamp(hw.AddMinutes(1)), Stamp(hw)));
    }

    [Fact]
    public void WindowOpen_ActiveUntilBehindOrAtHighWater_IsNotOpen()
    {
        var hw = new DateTime(2026, 10, 5, 10, 0, 0);
        Assert.False(mm_notify.Form1.ScheduleWindowOpen(Stamp(hw.AddMinutes(-1)), Stamp(hw)));
        Assert.False(mm_notify.Form1.ScheduleWindowOpen(Stamp(hw), Stamp(hw)));
    }

    [Theory]
    [InlineData("", "2026-10-05 10:00:00")]
    [InlineData("garbage", "2026-10-05 10:00:00")]
    [InlineData("2026-10-05 11:00:00", "")]
    [InlineData("2026-10-05 11:00:00", "garbage")]
    [InlineData(null, null)]
    public void WindowOpen_UnparseableOrEmpty_IsNotOpen(string? activeUntil, string? highWater)
    {
        Assert.False(mm_notify.Form1.ScheduleWindowOpen(activeUntil!, highWater!));
    }

    // ---------------------------------------------------------------
    // Through a real ini on disk: the raw slot count the call sites pass
    // ---------------------------------------------------------------

    private static int RawSlotCountViaDisk(Action<mm_notify.IniFile> build)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-a3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "monkmode_settings.ini");
            var w = new mm_notify.IniFile();
            build(w);
            w.Save(path);
            var r = new mm_notify.IniFile();
            r.Load(path);
            return mm_notify.Form1.RawSlotBlockCount(r);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GenuineTeardown_ZeroSlotsWithDoneYes_Exits()
    {
        // stopMe's shape: every slot section removed, SlotCount=0, then Done=yes.
        int raw = RawSlotCountViaDisk(ini =>
        {
            ini.SetKeyValue("User", "Done", "yes");
            ini.SetKeyValue("Slots", "SlotCount", "0");
        });
        Assert.Equal(0, raw);
        Assert.True(mm_notify.Form1.ShouldExitOnDone("yes", true, raw, false));
    }

    [Fact]
    public void ForgedDone_WithAnArmedSlotOnDisk_DoesNotExit()
    {
        // The A3 attack: a live slot, Done hand-edited to yes, SlotCount forged to 0.
        int raw = RawSlotCountViaDisk(ini =>
        {
            ini.SetKeyValue("User", "Done", "yes");
            ini.SetKeyValue("Slots", "SlotCount", "0");
            ini.SetKeyValue("Slot1", "Id", "1");
            ini.SetKeyValue("Slot1", "Until", "ZW5jcnlwdGVk");
        });
        Assert.Equal(1, raw);
        Assert.False(mm_notify.Form1.ShouldExitOnDone("yes", true, raw, false));
    }
}
