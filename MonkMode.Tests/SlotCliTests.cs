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

// MonkMode.Tests - v1.1 S5: THE CLI SURFACE BECOMES SLOT-AWARE.
//
// WHY THIS SLICE EXISTS. Since S2 the machine can run up to 8 blocks, but every verb except
// `unblock` still spoke about "the block": `status` collapsed all eight into one line read
// off the over-blocking v9 mirror, and `add` refused outright because a CLI-side hosts
// append is stripped back out by the next tick's P37 reconciliation. S5 gives each verb an
// id to talk about.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - P32's TABLE, BY LITERAL. The layout is a fixed-width contract, and the exit sentences
//     under it are the only place a user learns how to get out of a particular block. A
//     silent reflow or reworded exit line is invisible in review and fatal in a smoke.
//   - THE THREE cv-d SUBSTRINGS (P31). tools\smoke\cv-d-smoke.ps1 matches "Emergency unlock
//     code" (:113-118, with the code on the NEXT indented line), "committed block" (:141)
//     and "cooling-off pending" (:165) against the live CLI. Drifting any of them turns an
//     elevated sitting into a false FAIL, so all three are asserted here where a builder
//     editing the wording cannot miss them.
//   - P33/P34: a bare `unblock` never guesses which block it means, and the cap refusal
//     LISTS what is in the way rather than just saying no. Exit codes stay 0/1/2/3/4.
//   - P27: every --start refusal, on the ENFORCEMENT window rather than the wall clock.
//   - P42: `add` is service-adjudicated and GROWTH-ONLY - a trigger can add sites to the one
//     slot it names and can do nothing else at all: no lift, no shorten, no other slot, no
//     write over a frozen config.
//
// Fences honoured: pure formatters take plain values; the live tests write only to the
// test-bin ini/backup/snapshot (wiped in finally) or a GUID temp directory under the test
// bin. Never the real hosts file, registry, SCM or a deployed config; nothing is armed for
// real and no binary is executed.

using System.Globalization;

namespace MonkMode.Tests;

// ---- 1. P32: the status table, pinned by literal ----

public class SlotTableFormatTests
{
    // The authoritative sample from the plan (Part 1 §1.4, P32), copied verbatim.
    private const string SampleHeader = " Id  State     Ends / Starts             Sites Apps URLs  Exit";
    private const string SampleActive = "  2  ACTIVE    2026-08-09 21:00              7    1    0  code+wait";
    private const string SampleCommitted = "  3  ACTIVE    2026-08-09 23:59              2    0    3  committed";
    private const string SampleSchedule = "  6  SCHEDULE  window OPEN until 04:00       3    0    0  window";

    private static MonkMode.Blocker.SlotView Active(string id, DateTime ends, int sites, int apps, int urls,
                                                    bool committed = false, TimeSpan? coolOff = null)
        => new()
        {
            Id = id,
            State = MonkMode.Blocker.SlotStateActive,
            Ends = ends,
            Sites = sites,
            Apps = apps,
            Urls = urls,
            Committed = committed,
            CoolOffRemaining = coolOff,
        };

    [Fact]
    public void Header_MatchesTheP32Layout()
    {
        Assert.Equal(SampleHeader, MonkMode.Program.FormatSlotTableHeader());
    }

    [Fact]
    public void ActiveRow_MatchesTheP32Sample()
    {
        Assert.Equal(SampleActive,
            MonkMode.Program.FormatSlotRow(Active("2", new DateTime(2026, 8, 9, 21, 0, 0), 7, 1, 0)));
    }

    [Fact]
    public void CommittedRow_MatchesTheP32Sample()
    {
        Assert.Equal(SampleCommitted,
            MonkMode.Program.FormatSlotRow(Active("3", new DateTime(2026, 8, 9, 23, 59, 0), 2, 0, 3, committed: true)));
    }

    [Fact]
    public void ScheduleRow_MatchesTheP32Sample()
    {
        var v = new MonkMode.Blocker.SlotView
        {
            Id = "6",
            State = MonkMode.Blocker.SlotStateSchedule,
            WindowOpen = true,
            WindowUntil = new DateTime(2026, 8, 10, 4, 0, 0),
            Sites = 3,
        };
        Assert.Equal(SampleSchedule, MonkMode.Program.FormatSlotRow(v));
    }

    [Fact]
    public void PendingRow_KeepsTheWholeStamp_AndPushesTheCountsRight()
    {
        // DELIBERATE, RECORDED DEVIATION from the P32 sample's PENDING line. Its
        // "starts <stamp> (<dur>)" cell is 28 chars against a 24-wide column, and the sample
        // was hand-drawn as if the overflow ate the following padding. Truncating a start
        // time to make the columns line up would be the worse bug (two PENDING blocks
        // starting the same day would render identically), so an over-long cell simply pushes
        // the rest of the row right. Every other row - which is every row a fixed-width
        // column can hold - matches the sample byte for byte.
        var v = new MonkMode.Blocker.SlotView
        {
            Id = "5",
            State = MonkMode.Blocker.SlotStatePending,
            StartAt = new DateTime(2026, 8, 10, 7, 0, 0),
            DurationSeconds = 7200,
            Sites = 4,
        };
        Assert.Equal("  5  PENDING   starts 2026-08-10 07:00 (2h)      4    0    0  cancel",
                     MonkMode.Program.FormatSlotRow(v));
        // The part that is load-bearing rather than cosmetic: the whole start moment and the
        // planned length both survive.
        Assert.Equal("starts 2026-08-10 07:00 (2h)", MonkMode.Program.FormatSlotWhenCell(v));
    }

    [Fact]
    public void CoolingOffRow_ShowsTheCoolingOffToken()
    {
        var v = Active("2", new DateTime(2026, 8, 9, 21, 0, 0), 7, 1, 0, coolOff: TimeSpan.FromMinutes(58));
        Assert.Equal("cooling-off", MonkMode.Program.SlotExitToken(v));
        Assert.Contains("  cooling-off", MonkMode.Program.FormatSlotRow(v));
    }

    [Fact]
    public void UnreadableCells_RenderAPlaceholder_RatherThanThrow()
    {
        // A display path must never throw over a live block: an ACTIVE slot whose end could
        // not be decrypted still renders a row.
        var v = Active("?", DateTime.MinValue, 0, 0, 0);
        Assert.Equal("?", MonkMode.Program.FormatSlotWhenCell(v));
        Assert.Equal("", MonkMode.Program.FormatSlotRow(null));
        Assert.Equal("", MonkMode.Program.FormatSlotExitLine(null));
    }

    [Fact]
    public void ScheduleRow_WithNoOpenWindow_SaysSo()
    {
        var v = new MonkMode.Blocker.SlotView { Id = "6", State = MonkMode.Blocker.SlotStateSchedule };
        Assert.Equal("no window open now", MonkMode.Program.FormatSlotWhenCell(v));
        Assert.Equal("window", MonkMode.Program.SlotExitToken(v));
    }

    [Theory]
    [InlineData(1, "MonkMode: 1 block active")]
    [InlineData(3, "MonkMode: 3 blocks active")]
    [InlineData(8, "MonkMode: 8 blocks active")]
    public void Heading_CountsAndPluralises(int n, string expected)
    {
        Assert.Equal(expected, MonkMode.Program.FormatStatusHeading(n));
    }

    // ---- the Exit sentences, verbatim from P32's sample ----

    [Fact]
    public void ExitLine_Active_NamesTheBlockInTheCommandHint()
    {
        Assert.Equal("Exit:  run 'monkmode unblock --id 2' to start a cooling-off wait, or the accountability code (shown at block start) lifts it now.",
            MonkMode.Program.FormatSlotExitLine(Active("2", new DateTime(2026, 8, 9, 21, 0, 0), 7, 1, 0)));
    }

    [Fact]
    public void ExitLine_Committed_IsTheCodeOnlyExit()
    {
        Assert.Equal("Exit:  committed block - the accountability code (shown at block start) is the only early exit, or wait for the timer.",
            MonkMode.Program.FormatSlotExitLine(Active("3", new DateTime(2026, 8, 9, 23, 59, 0), 2, 0, 3, committed: true)));
    }

    [Fact]
    public void ExitLine_Pending_OffersTheFreeCancel()
    {
        var v = new MonkMode.Blocker.SlotView
        {
            Id = "5",
            State = MonkMode.Blocker.SlotStatePending,
            StartAt = new DateTime(2026, 8, 10, 7, 0, 0),
            DurationSeconds = 7200,
        };
        Assert.Equal("Exit:  not started yet - 'monkmode unblock --id 5 --cancel' cancels it freely until it starts.",
                     MonkMode.Program.FormatSlotExitLine(v));
    }

    [Fact]
    public void ExitLine_Schedule_SaysAnOpenWindowCannotBeEndedEarly()
    {
        var v = new MonkMode.Blocker.SlotView { Id = "6", State = MonkMode.Blocker.SlotStateSchedule };
        Assert.Equal("Exit:  an open window can't be ended early; it closes on its own.",
                     MonkMode.Program.FormatSlotExitLine(v));
    }

    [Fact]
    public void ExitLine_CoolingOff_NamesTheBlockInTheCancelHint()
    {
        var v = Active("2", new DateTime(2026, 8, 9, 21, 0, 0), 7, 1, 0, coolOff: TimeSpan.FromHours(1));
        Assert.Equal("Exit:  cooling-off pending - lifts in about 1h of active time. Run 'monkmode unblock --id 2 --cancel' to stay blocked.",
                     MonkMode.Program.FormatSlotExitLine(v));
    }

    [Fact]
    public void ExitLine_WithNoUsableId_DropsTheIdFlagRatherThanPrintAnUntypableCommand()
    {
        // "?" is the unreadable-id placeholder. A hint of "monkmode unblock --id ?" would be
        // worse than the v1.0 wording, so the flag is simply omitted.
        Assert.Equal("Exit:  run 'monkmode unblock' to start a cooling-off wait, or the accountability code (shown at block start) lifts it now.",
                     MonkMode.Program.FormatCoolOffStatusLine(false, null, "?"));
        Assert.Equal("Exit:  run 'monkmode unblock' to start a cooling-off wait, or the accountability code (shown at block start) lifts it now.",
                     MonkMode.Program.FormatCoolOffStatusLine(false, null, ""));
    }

    [Fact]
    public void ExitToken_TruthTable()
    {
        Assert.Equal("code+wait", MonkMode.Program.SlotExitToken(Active("1", DateTime.Now, 1, 0, 0)));
        Assert.Equal("committed", MonkMode.Program.SlotExitToken(Active("1", DateTime.Now, 1, 0, 0, committed: true)));
        Assert.Equal("cooling-off", MonkMode.Program.SlotExitToken(Active("1", DateTime.Now, 1, 0, 0, coolOff: TimeSpan.FromHours(1))));
        // A committed block has no self-serve cooling-off at all, so a stored deadline on one
        // is stale by construction: "committed" wins, matching FormatCoolOffStatusLine.
        Assert.Equal("committed", MonkMode.Program.SlotExitToken(Active("1", DateTime.Now, 1, 0, 0, committed: true, coolOff: TimeSpan.FromHours(1))));
        Assert.Equal("cancel", MonkMode.Program.SlotExitToken(new MonkMode.Blocker.SlotView { State = MonkMode.Blocker.SlotStatePending }));
        Assert.Equal("window", MonkMode.Program.SlotExitToken(new MonkMode.Blocker.SlotView { State = MonkMode.Blocker.SlotStateSchedule }));
    }

    [Fact]
    public void TheExitSentenceIsIndentedUnderItsRow()
    {
        // P32's sample indents the Exit line to the State column - i.e. past the Id column
        // and its separator, so the ids stay scannable down the left edge.
        Assert.Equal(5, MonkMode.Program.SlotExitIndent.Length);
        Assert.Equal(new string(' ', 5), MonkMode.Program.SlotExitIndent);
    }
}

// ---- 2. R9 / P31: the three literal substrings tools\smoke\cv-d-smoke.ps1 matches ----

public class CvDSmokeWordingTests
{
    [Fact]
    public void EmergencyUnlockCodeHeader_KeepsTheSubstringParseCodeMatches()
    {
        // cv-d-smoke.ps1:113-118 finds the line matching 'Emergency unlock code' and takes
        // the NEXT line, trimmed, as the code. The header may gain text (it names the block
        // now) but that substring, and the code being on the following line, are the contract.
        var header = MonkMode.Program.FormatUnlockCodeHeader(3);
        Assert.Contains("Emergency unlock code", header);
        Assert.Contains("block 3", header);
        Assert.DoesNotContain("\n", header);   // one line: the code is the NEXT one
    }

    [Fact]
    public void StatusCommittedLine_KeepsTheCommittedBlockSubstring()
    {
        // cv-d-smoke.ps1:141 asserts `monkmode status` matches 'committed block' while a
        // committed block is armed. It reaches the user through the table's Exit sentence.
        var line = MonkMode.Program.FormatSlotExitLine(new MonkMode.Blocker.SlotView
        {
            Id = "1",
            State = MonkMode.Blocker.SlotStateActive,
            Ends = DateTime.Now.AddHours(1),
            Committed = true,
        });
        Assert.Contains("committed block", line);
    }

    [Fact]
    public void StatusCoolingOffLine_KeepsTheCoolingOffPendingSubstring()
    {
        // cv-d-smoke.ps1:165 asserts 'cooling-off pending' appears while one is pending, and
        // :170 asserts it is GONE after --cancel - so it must appear only on that branch.
        var pending = MonkMode.Program.FormatSlotExitLine(new MonkMode.Blocker.SlotView
        {
            Id = "1",
            State = MonkMode.Blocker.SlotStateActive,
            Ends = DateTime.Now.AddHours(1),
            CoolOffRemaining = TimeSpan.FromMinutes(59),
        });
        Assert.Contains("cooling-off pending", pending);

        var cancelled = MonkMode.Program.FormatSlotExitLine(new MonkMode.Blocker.SlotView
        {
            Id = "1",
            State = MonkMode.Blocker.SlotStateActive,
            Ends = DateTime.Now.AddHours(1),
        });
        Assert.DoesNotContain("cooling-off pending", cancelled);
    }
}

// ---- 3. P26-P28: every --start refusal ----

public class StartFlagRefusalTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0);

    [Fact]
    public void Unparseable_IsRefused_WithTheAcceptedForms()
    {
        DateTime start = default;
        string err = "";
        Assert.False(MonkMode.Program.TryParseStart("next tuesday-ish", Now, ref start, ref err));
        Assert.Contains("Could not understand --start", err);
        Assert.Contains("+90m", err);
    }

    [Fact]
    public void MoreThanThirtyDaysAhead_IsRefused()
    {
        // P27: an unbounded PENDING slot would squat one of the 8 slots for ever.
        Assert.Equal(30, MonkMode.Program.MaxStartDelayDays);
        Assert.True(MonkMode.Program.StartIsTooFarAhead(Now.AddDays(31), Now));
        Assert.True(MonkMode.Program.StartIsTooFarAhead(Now.AddDays(30).AddSeconds(1), Now));
        Assert.False(MonkMode.Program.StartIsTooFarAhead(Now.AddDays(30), Now));   // exactly 30d is fine
    }

    [Fact]
    public void EndAtOrBeforeADelayedStart_IsACleanRefusal_NotAZeroLengthBlock()
    {
        Assert.Equal(MonkMode.Program.WindowRefusal.EndsBeforeStart,
                     MonkMode.Program.ClassifyBlockWindow(true, Now.AddHours(2), Now.AddHours(1)));
        Assert.Equal(MonkMode.Program.WindowRefusal.EndsBeforeStart,
                     MonkMode.Program.ClassifyBlockWindow(true, Now.AddHours(2), Now.AddHours(2)));
    }

    [Fact]
    public void TheSixtySecondFloorIsMeasuredFromTheStart_NotFromNow()
    {
        // THE PIN THAT MATTERS. `--for` on a delayed block measures from the START, so a
        // window of 30s starting in a week is still a 30s block - and would be accepted if
        // the floor were anchored on the wall clock.
        var start = Now.AddDays(7);
        Assert.Equal(MonkMode.Program.WindowRefusal.TooShort,
                     MonkMode.Program.ClassifyBlockWindow(true, start, start.AddSeconds(30)));
        Assert.Equal(MonkMode.Program.WindowRefusal.TooShort,
                     MonkMode.Program.ClassifyBlockWindow(true, start, start.AddSeconds(60)));
        Assert.Equal(MonkMode.Program.WindowRefusal.None,
                     MonkMode.Program.ClassifyBlockWindow(true, start, start.AddSeconds(61)));
        // ...and an immediate block behaves exactly as it did before v1.1.
        Assert.Equal(MonkMode.Program.WindowRefusal.TooShort,
                     MonkMode.Program.ClassifyBlockWindow(false, Now, Now.AddSeconds(45)));
        Assert.Equal(MonkMode.Program.WindowRefusal.None,
                     MonkMode.Program.ClassifyBlockWindow(false, Now, Now.AddHours(2)));
    }

    [Fact]
    public void AnImmediateBlockWithAPastEnd_IsTooShort_NotEndsBeforeStart()
    {
        // EndsBeforeStart is only reachable for a DELAYED block: without --start there is no
        // start to contradict, and the user should be told about the minute floor.
        Assert.Equal(MonkMode.Program.WindowRefusal.TooShort,
                     MonkMode.Program.ClassifyBlockWindow(false, Now, Now.AddHours(-1)));
    }

    [Fact]
    public void APastStartIsNotAnError_ItMeansNow()
    {
        // P28 (consented taste default): parsing SUCCEEDS on a past start; DoBlock then notes
        // it and arms an ACTIVE slot rather than a PENDING one waiting on a gone moment.
        DateTime start = default;
        string err = "";
        Assert.True(MonkMode.Program.TryParseStart("2020-01-01 07:00", Now, ref start, ref err));
        Assert.True(start < Now);
        Assert.False(MonkMode.Program.StartIsTooFarAhead(start, Now));
    }
}

// ---- 4. exit codes ----

[Collection("CliIniWriters")]
public class ExitCodeTests
{
    [Fact]
    public void ExitCodes_StayZeroOneTwoThreeFour()
    {
        // The whole CLI's contract, so scripts (and the smokes) can tell the cases apart:
        //   0 ok - 1 usage/refusal - 2 could-not-act - 3 cap/already-armed - 4 not set up.
        Assert.Equal(3, MonkMode.Blocker.ExitCapReached);
        Assert.Equal(2, MonkMode.Blocker.ExitArmFailed);

        // The three verb-free paths are safe to drive for real: they touch no SCM, no hosts,
        // no registry and no config (Main's only pre-dispatch call restores a CORRUPT primary
        // from a valid backup, and there is neither here).
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
        Assert.Equal(1, MonkMode.Program.Main(Array.Empty<string>()));      // no verb  -> usage
        Assert.Equal(1, MonkMode.Program.Main(new[] { "nonsense" }));       // unknown  -> usage
        Assert.Equal(0, MonkMode.Program.Main(new[] { "help" }));
        Assert.Equal(0, MonkMode.Program.Main(new[] { "--help" }));
    }
}

// ---- 5. P33/P34 against a real armed config, and P42's add channel end to end ----

[Collection("CliIniWriters")]
public class SlotCliLiveTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    // NEVER construct a Service1 directly (InitializeComponent arms the live 10s tick).
    private static monkmode.Service1 Svc() => TestSvc.New();

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
        foreach (var pattern in new[] { MonkMode.Blocker.CoolOffRequestPrefix + "*",
                                        MonkMode.Blocker.CoolOffCancelPrefix + "*",
                                        MonkMode.Blocker.PartnerCodePrefix + "*",
                                        MonkMode.Blocker.AddRequestPrefix + "*" })
        {
            foreach (var f in Directory.GetFiles(MonkMode.Blocker.AppDir(), pattern)) File.Delete(f);
        }
    }

    private static MonkMode.Blocker.ArmResult Arm(string site, DateTime? startAt = null, bool committed = false)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", startAt, Ends, false, committed: committed);

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    private static List<string> SitesOf(int pos)
        => new(monkmode.Service1.SplitPackedList(Reload().GetKeyValue("Slot" + pos, "Sites"), ';'));

    private static List<string> TriggersIn(string dir)
        => monkmode.Service1.EnumerateTriggerFilesIn(dir);

    // ---- P32 read model ----

    [Fact]
    public void ReadSlotViews_RendersOneRowPerArmedBlock_WithItsDerivedState()
    {
        Wipe();
        try
        {
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "a.com", "x.com" }, new[] { "chrome.exe" },
                                                 "*/watch*", null, Ends, false).Ok);
            Assert.True(Arm("b.com", startAt: DateTime.Now.AddDays(1)).Ok);

            bool macValid = false;
            var views = MonkMode.Blocker.ReadSlotViews(ref macValid);
            Assert.True(macValid);
            Assert.Equal(2, views.Count);

            Assert.Equal("1", views[0].Id);
            Assert.Equal(MonkMode.Blocker.SlotStateActive, views[0].State);
            Assert.Equal(2, views[0].Sites);
            Assert.Equal(1, views[0].Apps);
            Assert.Equal(1, views[0].Urls);
            Assert.Equal(Ends, views[0].Ends);
            Assert.False(views[0].Committed);
            Assert.Null(views[0].CoolOffRemaining);

            // P16: StartAt set with no Until yet = PENDING (the service computes its end at
            // activation), and the planned length is carried so the row can show it.
            Assert.Equal(MonkMode.Blocker.SlotStatePending, views[1].State);
            Assert.True(views[1].DurationSeconds > 0);
            Assert.Equal(DateTime.MinValue, views[1].Ends);

            // ...and the whole table renders without an unreadable cell anywhere.
            Assert.DoesNotContain("?", MonkMode.Program.FormatSlotRow(views[0]));
            Assert.DoesNotContain("?", MonkMode.Program.FormatSlotRow(views[1]));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ReadSlotViews_IsReadOnly_AndLeavesTheConfigByteIdentical()
    {
        // `status` runs against a LIVE block. If it could write, it would be a second writer
        // racing the service's tick - and the one thing a status read must never do is
        // re-stamp a MAC over bytes it did not verify.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            var beforeWrite = File.GetLastWriteTimeUtc(MonkMode.Blocker.IniPath());

            bool macValid = false;
            Assert.Single(MonkMode.Blocker.ReadSlotViews(ref macValid));

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(MonkMode.Blocker.IniPath()));
            Assert.False(File.Exists(MonkMode.Blocker.SnapshotPath()));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ReadSlotViews_OnATamperedConfig_ShowsNoReassuringExit()
    {
        // B7: a forged Committed/CoolOffUntil must never render an exit story. With the MAC
        // invalid both fields are suppressed, so the Exit column falls back to its most
        // conservative value and the caller says the config is frozen.
        Wipe();
        try
        {
            Assert.True(Arm("a.com", committed: true).Ok);
            var ini = Reload();
            ini.SetKeyValue("Slot1", "Sites", "a.com;tampered.com;");   // no re-stamp: MAC now invalid
            ini.Save(MonkMode.Blocker.IniPath());

            bool macValid = true;
            var views = MonkMode.Blocker.ReadSlotViews(ref macValid);
            Assert.False(macValid);
            Assert.Single(views);
            Assert.False(views[0].Committed);
            Assert.Null(views[0].CoolOffRemaining);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void NoSlots_MeansNoTable()
    {
        Wipe();
        bool macValid = true;
        Assert.Empty(MonkMode.Blocker.ReadSlotViews(ref macValid));
        Assert.False(macValid);      // nothing to validate: never reported as a valid config
    }

    // ---- P33: bare `unblock` with two blocks armed ----

    [Fact]
    public void BareUnblock_TwoSlots_Exit1_ListsSlots()
    {
        // The refusal is only useful if the listing lets the user pick: both ids, both
        // states, and enough about each block to tell them apart.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var armed = MonkMode.Blocker.ArmedSlotIds();
            Assert.Equal(new[] { 1, 2 }, armed);

            // Exit code 1 is what the refusal returns, and the rule that reaches it.
            Assert.True(MonkMode.Program.BareUnblockIsAmbiguous(false, armed.Count));
            Assert.False(MonkMode.Program.BareUnblockIsAmbiguous(true, armed.Count));

            var lines = MonkMode.Blocker.ArmedSlotLines();
            Assert.Equal(2, lines.Count);
            foreach (var id in armed) Assert.Contains(lines, l => l.TrimStart().StartsWith(id + "  "));
            Assert.All(lines, l => Assert.Contains("ACTIVE", l));
        }
        finally { Wipe(); }
    }

    // ---- P34: the cap refusal LISTS what is in the way ----

    [Fact]
    public void Cap_Exit3_Listing()
    {
        Wipe();
        try
        {
            for (int i = 1; i <= monkmode.ConfigIntegrity.MaxSlots; i++)
                Assert.True(Arm("site" + i + ".com").Ok);

            var ninth = Arm("overflow.com");
            Assert.Equal(MonkMode.Blocker.ArmOutcome.CapReached, ninth.Outcome);
            Assert.False(ninth.Ok);
            Assert.Equal(3, MonkMode.Blocker.ExitCapReached);

            // A bare "no" would leave the user with nothing to act on: the refusal names every
            // block in the way, with what it covers and when it ends.
            Assert.Equal(monkmode.ConfigIntegrity.MaxSlots, ninth.SlotSummaries.Count);
            for (int i = 1; i <= monkmode.ConfigIntegrity.MaxSlots; i++)
            {
                Assert.Contains(ninth.SlotSummaries, s => s.Contains("#" + i + "  ") && s.Contains("site" + i + ".com"));
            }
            Assert.All(ninth.SlotSummaries, s => Assert.Contains("until ", s));

            // ...and the refusal happened BEFORE any side effect: still exactly 8 slots.
            Assert.Equal(monkmode.ConfigIntegrity.MaxSlots,
                         monkmode.ConfigIntegrity.ParseSlotCount(Reload().GetKeyValue("Slots", "SlotCount")));
        }
        finally { Wipe(); }
    }

    // ---- P42: `add` is a service-adjudicated, growth-only request ----

    [Fact]
    public void AddRequest_DropsATriggerTheSharedBudgetEnumerates()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            MonkMode.Blocker.RequestAdd(1, new[] { "c.com", "d.com" });

            var path = Path.Combine(MonkMode.Blocker.AppDir(), MonkMode.Blocker.AddRequestPrefix + "1");
            Assert.True(File.Exists(path));
            Assert.Equal("c.com" + Environment.NewLine + "d.com", File.ReadAllText(path));

            // P41: it rides the SAME capped, ordinal-sorted per-tick budget as the exit
            // families - a trigger no poller enumerates is a request that never happens.
            Assert.Contains(MonkMode.Blocker.AddRequestPrefix + "1", TriggersIn(MonkMode.Blocker.AppDir()));
            // ...and it addresses a known family, so the legacy-name purge never eats it.
            Assert.True(monkmode.Service1.TriggerAddressesAnyFamily(MonkMode.Blocker.AddRequestPrefix + "1"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AddRequest_SizeCapIsTheSameOnBothSidesOfTheChannel()
    {
        // The CLI refuses an over-long list up front precisely because the service DELETES an
        // oversize trigger without applying it - two different caps would mean the CLI
        // promising an application that gets binned.
        Assert.Equal(monkmode.Service1.TriggerMaxBytes, MonkMode.Blocker.TriggerMaxBytes);
        Assert.True(MonkMode.Blocker.AddRequestFits(new string('a', 4000)));
        Assert.False(MonkMode.Blocker.AddRequestFits(new string('a', 5000)));
        Assert.Equal("c.com" + Environment.NewLine + "d.com",
                     MonkMode.Blocker.BuildAddRequestContent(new[] { " c.com ", "", "d.com" }));
    }

    [Fact]
    public void AddRequest_GrowsTheAddressedSlotOnly()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            MonkMode.Blocker.RequestAdd(2, new[] { "c.com" });

            var svc = Svc();
            var dir = MonkMode.Blocker.AppDir();
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true, TriggersIn(dir));

            Assert.Equal(new[] { "a.com" }, SitesOf(1));                 // untouched
            Assert.Equal(new[] { "b.com", "c.com" }, SitesOf(2));        // grown, in order
            // Consumed after the write landed, so the next tick does not re-apply it.
            Assert.False(File.Exists(Path.Combine(dir, MonkMode.Blocker.AddRequestPrefix + "2")));
            // ...and the config is still MAC-valid, i.e. the grow went through the one
            // per-slot writer rather than around it.
            bool macValid = false;
            MonkMode.Blocker.ReadSlotViews(ref macValid);
            Assert.True(macValid);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AddRequest_UnknownId_DeletesTheTriggerAndChangesNothing()
    {
        // P40: the id ROUTES, it never authorises. A trigger against a retired or invented
        // block must not freeze the config, and must not land on some other block.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            MonkMode.Blocker.RequestAdd(99, new[] { "evil.com" });

            var svc = Svc();
            var dir = MonkMode.Blocker.AppDir();
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true, TriggersIn(dir));

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.False(File.Exists(Path.Combine(dir, MonkMode.Blocker.AddRequestPrefix + "99")));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AddRequest_AgainstAFrozenConfig_NeverWidensAndNeverRestamps()
    {
        // B7: no write path may re-stamp a config it did not just verify. `add` is a write,
        // so a frozen (tampered) config is left exactly as it is - the block stays frozen and
        // unliftable rather than being re-blessed with a fresh valid MAC.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var ini = Reload();
            ini.SetKeyValue("Slot1", "Sites", "a.com;tampered.com;");   // no re-stamp
            ini.Save(MonkMode.Blocker.IniPath());
            var frozen = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            MonkMode.Blocker.RequestAdd(1, new[] { "c.com" });
            var svc = Svc();
            var dir = MonkMode.Blocker.AppDir();
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), false, TriggersIn(dir));

            Assert.Equal(frozen, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            // The trigger is consumed rather than left to occupy the per-tick budget for ever
            // (a frozen config is only left by re-arming, which is a different block).
            Assert.False(File.Exists(Path.Combine(dir, MonkMode.Blocker.AddRequestPrefix + "1")));
        }
        finally { Wipe(); }
    }

    // ---- a second `add` inside one tick must not overwrite the first ----

    [Fact]
    public void SecondAdd_UnionsWithThePendingFirstOne_RatherThanOverwritingIt()
    {
        // THE PIN. The trigger name is fixed per slot, so a plain write would REPLACE a
        // request the CLI had already told the user was accepted ("applies within ~10s"),
        // losing those sites silently. Two adds inside one ~10s tick must behave exactly like
        // one add of both sets.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(MonkMode.Blocker.RequestAdd(1, new[] { "first.com", "shared.com" }));
            Assert.True(MonkMode.Blocker.RequestAdd(1, new[] { "second.com", "SHARED.com" }));

            var written = File.ReadAllText(MonkMode.Blocker.AddRequestPath(1));
            Assert.Equal("first.com" + Environment.NewLine + "shared.com" + Environment.NewLine + "second.com", written);

            // ...and the service then applies BOTH adds from the one trigger.
            var svc = Svc();
            var dir = MonkMode.Blocker.AppDir();
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true, TriggersIn(dir));
            Assert.Equal(new[] { "a.com", "first.com", "shared.com", "second.com" }, SitesOf(1));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AnUnreadablePendingTrigger_StillLetsTheNewSitesThrough()
    {
        // Fail WIDER: whatever parses out of a garbage pending file is kept, and the new
        // sites always land. Refusing an add because a stale trigger is unreadable would
        // strand the user, and the merge can only ever grow the list.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            File.WriteAllText(MonkMode.Blocker.AddRequestPath(1), "not a domain\r\nkept.com\r\n\0\0garbage;bits");
            Assert.True(MonkMode.Blocker.RequestAdd(1, new[] { "new.com" }));

            var written = File.ReadAllText(MonkMode.Blocker.AddRequestPath(1));
            Assert.Contains("new.com", written);          // the new request always survives
            Assert.Contains("kept.com", written);         // ...and so does what parsed
            Assert.DoesNotContain("not a domain", written);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AnOversizePendingTrigger_IsNotEchoedBack_AndTheNewSitesStillLand()
    {
        // The service bins an oversize trigger without applying it, so there is nothing to
        // preserve: the new request replaces it and is applied instead of both being lost.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            File.WriteAllText(MonkMode.Blocker.AddRequestPath(1), new string('x', (int)MonkMode.Blocker.TriggerMaxBytes + 1));
            Assert.True(MonkMode.Blocker.RequestAdd(1, new[] { "new.com" }));
            Assert.Equal("new.com", File.ReadAllText(MonkMode.Blocker.AddRequestPath(1)));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AMergedRequestOverTheCap_IsRefused_AndLeavesThePendingOneIntact()
    {
        // OVER-BUDGET DIRECTION (fail-closed): refuse the NEW add and keep the pending
        // request exactly as it is, so the sites already promised to the user still apply.
        // Writing the merge anyway would push the file over the cap and make the service bin
        // it - losing BOTH requests.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var bulk = new List<string>();
            for (int i = 0; i < 150; i++) bulk.Add("site-" + i.ToString("D4") + ".example.com");
            Assert.True(MonkMode.Blocker.RequestAdd(1, bulk));
            var pending = File.ReadAllText(MonkMode.Blocker.AddRequestPath(1));
            Assert.True(MonkMode.Blocker.AddRequestFits(pending));

            var more = new List<string>();
            for (int i = 0; i < 150; i++) more.Add("later-" + i.ToString("D4") + ".example.com");
            Assert.False(MonkMode.Blocker.RequestAdd(1, more));                       // refused...
            Assert.Equal(pending, File.ReadAllText(MonkMode.Blocker.AddRequestPath(1)));   // ...intact
        }
        finally { Wipe(); }
    }

    [Fact]
    public void MergeAddRequestContent_IsTheOneDefinitionOfTheTriggerBody()
    {
        // Pure, so the cap check measures exactly the bytes that get written.
        Assert.Equal("a.com" + Environment.NewLine + "b.com",
                     MonkMode.Blocker.MergeAddRequestContent("a.com", new[] { "b.com" }));
        Assert.Equal("a.com", MonkMode.Blocker.MergeAddRequestContent("a.com", new[] { "A.COM" }));
        Assert.Equal("a.com", MonkMode.Blocker.MergeAddRequestContent("", new[] { " a.com ", "" }));
        Assert.Equal("", MonkMode.Blocker.MergeAddRequestContent(null, null));
        // A token the SERVICE would drop is not echoed back into the file, or it would ride
        // there for ever, costing budget on every future merge.
        Assert.Equal("b.com", MonkMode.Blocker.MergeAddRequestContent("not a domain", new[] { "b.com" }));
        // BuildAddRequestContent is just the no-pending case.
        Assert.Equal(MonkMode.Blocker.BuildAddRequestContent(new[] { "a.com" }),
                     MonkMode.Blocker.MergeAddRequestContent("", new[] { "a.com" }));
    }

    // ---- the DoAdd wiring itself, driven through Main ----

    [Fact]
    public void Main_Add_WithTwoBlocksAndNoId_Exits1_AndDropsNoTrigger()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            Assert.Equal(1, MonkMode.Program.Main(new[] { "add", "--sites", "c.com" }));
            // The refusal is a refusal: nothing was requested against either block.
            Assert.Empty(Directory.GetFiles(MonkMode.Blocker.AppDir(), MonkMode.Blocker.AddRequestPrefix + "*"));
            // ...and the listing the user needs to choose from exists.
            Assert.Equal(2, MonkMode.Blocker.ArmedSlotLines().Count);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void Main_Add_WithAnUnknownId_Exits1_AndDropsNoTrigger()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.Equal(1, MonkMode.Program.Main(new[] { "add", "--sites", "c.com", "--id", "7" }));
            Assert.Equal(1, MonkMode.Program.Main(new[] { "add", "--sites", "c.com", "--id", "not-a-number" }));
            Assert.Empty(Directory.GetFiles(MonkMode.Blocker.AppDir(), MonkMode.Blocker.AddRequestPrefix + "*"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void Main_Add_AgainstAFrozenConfig_Exits2_AndDropsNoTrigger()
    {
        // Exit 2 = "could not act", matching `block`'s own frozen-config refusal. The service
        // would never widen a frozen config, so accepting would promise an application that
        // gets binned.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var ini = Reload();
            ini.SetKeyValue("Slot1", "Sites", "a.com;tampered.com;");   // no re-stamp
            ini.Save(MonkMode.Blocker.IniPath());
            var frozen = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            Assert.Equal(2, MonkMode.Program.Main(new[] { "add", "--sites", "c.com", "--id", "1" }));
            Assert.Empty(Directory.GetFiles(MonkMode.Blocker.AppDir(), MonkMode.Blocker.AddRequestPrefix + "*"));
            Assert.Equal(frozen, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void Main_Add_WithNoSites_Exits1()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.Equal(1, MonkMode.Program.Main(new[] { "add" }));
            Assert.Empty(Directory.GetFiles(MonkMode.Blocker.AppDir(), MonkMode.Blocker.AddRequestPrefix + "*"));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AddAgainstAFrozenConfig_IsRefusedByTheCliToo()
    {
        // The CLI half of the same rule: the service will not widen a frozen config, so
        // accepting the request and printing "MonkMode applies it within ~10s" would be a
        // straight lie. ConfigIsMacValid is what the refusal reads, and it fails SAFE.
        Wipe();
        Assert.False(MonkMode.Blocker.ConfigIsMacValid());        // no config at all
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
            var ini = Reload();
            ini.SetKeyValue("Slot1", "Sites", "a.com;tampered.com;");   // no re-stamp
            ini.Save(MonkMode.Blocker.IniPath());
            Assert.False(MonkMode.Blocker.ConfigIsMacValid());
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AddRequest_Oversize_IsBinnedWithoutChangingAnything()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            var dir = MonkMode.Blocker.AppDir();
            var path = Path.Combine(dir, MonkMode.Blocker.AddRequestPrefix + "1");
            File.WriteAllText(path, new string('x', (int)monkmode.Service1.TriggerMaxBytes + 1));

            var svc = Svc();
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true, TriggersIn(dir));

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.False(File.Exists(path));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AddRequest_IsIdempotent_SoACrashBetweenWriteAndDeleteCostsNothing()
    {
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var dir = MonkMode.Blocker.AppDir();
            var svc = Svc();
            MonkMode.Blocker.RequestAdd(1, new[] { "c.com" });
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true, TriggersIn(dir));
            Assert.Equal(new[] { "a.com", "c.com" }, SitesOf(1));

            // Re-drop the same request (what a crash between the persist and the delete leaves
            // behind): it adds nothing new and is simply consumed.
            var after = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            MonkMode.Blocker.RequestAdd(1, new[] { "c.com" });
            svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), svc.LoadSlots(Reload()), true, TriggersIn(dir));
            Assert.Equal(after, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.False(File.Exists(Path.Combine(dir, MonkMode.Blocker.AddRequestPrefix + "1")));
        }
        finally { Wipe(); }
    }
}

// ---- 6. the pure growth-only merge behind an add ----

public class AddRequestMergeTests
{
    private static List<string> L(params string[] items) => new(items);

    [Fact]
    public void GrowthOnly_ExistingEntriesSurviveInOrder()
    {
        // THE SAFETY PROPERTY. A request can only ever ADD: a forged or replayed trigger can
        // block more, never less - it cannot drop a site out of a running block.
        Assert.Equal("a.com;b.com;c.com;", monkmode.Service1.MergeSiteList(L("a.com", "b.com"), "c.com"));
        // Whatever the request, the existing entries come first and all of them survive.
        Assert.Equal("a.com;b.com;d.com;c.com;", monkmode.Service1.MergeSiteList(L("a.com", "b.com"), "d.com\r\nc.com\r\na.com"));
    }

    [Fact]
    public void NothingNew_WritesNothing()
    {
        // "" is the caller's signal to leave the config alone entirely - no write, no
        // re-stamp, no backup refresh over a request that would change nothing.
        Assert.Equal("", monkmode.Service1.MergeSiteList(L("a.com"), "a.com"));
        Assert.Equal("", monkmode.Service1.MergeSiteList(L("a.com"), "A.COM"));   // case-insensitive
        Assert.Equal("", monkmode.Service1.MergeSiteList(L("a.com"), "   "));
        Assert.Equal("", monkmode.Service1.MergeSiteList(L("a.com", "b.com"), ""));
        Assert.Equal("", monkmode.Service1.MergeSiteList(L(), ""));
        Assert.Equal("", monkmode.Service1.MergeSiteList(null, null));
    }

    [Fact]
    public void SeparatorsAccepted_OnePerLineOrCommaOrSemicolon()
    {
        Assert.Equal("a.com;b.com;c.com;", monkmode.Service1.MergeSiteList(L("a.com"), "b.com\r\nc.com"));
        Assert.Equal("a.com;b.com;c.com;", monkmode.Service1.MergeSiteList(L("a.com"), "b.com,c.com"));
        Assert.Equal("a.com;b.com;c.com;", monkmode.Service1.MergeSiteList(L("a.com"), "b.com;c.com"));
    }

    [Fact]
    public void TokensThatWouldCorruptTheStoredListAreDropped_NotStored()
    {
        // A token carrying whitespace would survive the pack but break the canonical's line
        // shape; dropping it is the TryExpandPresets stance - emit nothing rather than
        // something that freezes the very block the user was extending.
        Assert.Equal("a.com;b.com;", monkmode.Service1.MergeSiteList(L("a.com"), "b.com\r\nnot a domain"));
        Assert.Equal("", monkmode.Service1.MergeSiteList(L("a.com"), "not a domain\r\nalso bad"));
        // Surrounding whitespace is TRIMMED, not treated as corruption - a trailing space
        // after a domain on its own line is a typo, not an attack.
        Assert.Equal("a.com;b.com;", monkmode.Service1.MergeSiteList(L("a.com"), "  b.com  "));
        Assert.Equal("", monkmode.Service1.MergeSiteList(L("a.com"), "a.com\r\nA.Com"));
    }

    [Fact]
    public void TheResultRoundTripsThroughTheStoredFormat()
    {
        var packed = monkmode.Service1.MergeSiteList(L("a.com"), "b.com");
        Assert.Equal(new[] { "a.com", "b.com" }, monkmode.Service1.SplitPackedList(packed, ';'));
    }
}

// ---- 7. the schedule-only config keeps the guardian up between windows ----

[Collection("CliIniWriters")]
public class ScheduleGuardHoldTests
{
    private const string Spec = "v2 1111100 0900-1700 sites=reddit.com";

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    [Fact]
    public void ArmedCountIsOneWhileASpecIsArmed_ZeroOnceCleared()
    {
        Assert.Equal("1", MonkMode.Blocker.ScheduleGuardArmedCount(Spec));
        Assert.Equal("0", MonkMode.Blocker.ScheduleGuardArmedCount(""));
        Assert.Equal("0", MonkMode.Blocker.ScheduleGuardArmedCount("   "));
        Assert.Equal("0", MonkMode.Blocker.ScheduleGuardArmedCount(null));
    }

    [Fact]
    public void AScheduleOnlyConfigHoldsTheGuardian_BetweenWindowsToo()
    {
        // THE GAP THIS CLOSES. S4 deleted the guardian's legacy floor on the global
        // [Schedule] Spec (P44 replaced it with the raw per-SLOT scan), which left the
        // v9-shaped schedule-only config - the one `monkmode schedule` still writes - with
        // NOTHING holding the guardian between windows: killing the service in a gap left it
        // dead until the next window opened. [Guard] ArmedCount is MAC-covered, so the hold
        // is tamper-evident, and Guardian.GuardArmedCountHolds already honours it.
        Wipe();
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(Spec);
            Assert.True(MonkMode.Blocker.ScheduleIsArmed());              // MAC valid over the new field
            var armed = Reload().GetKeyValue("Guard", "ArmedCount");
            Assert.Equal("1", armed);
            Assert.True(mm_guard.Guardian.GuardArmedCountHolds(armed));
            Assert.True(mm_guard.Guardian.AnyBlockHeld("", armed, false, "2026-08-12 12:00:00", macValid: true));

            // `schedule --clear`: the guardian stands down once the Spec is gone (else it
            // would guard a schedule that no longer exists, for ever).
            MonkMode.Blocker.WriteScheduleConfig("");
            var cleared = Reload().GetKeyValue("Guard", "ArmedCount");
            Assert.Equal("0", cleared);
            Assert.False(mm_guard.Guardian.GuardArmedCountHolds(cleared));
            Assert.False(mm_guard.Guardian.AnyBlockHeld("", cleared, false, "2026-08-12 12:00:00", macValid: true));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void TheGuardHoldIsMacCovered_SoTurningItOffByHandFreezesInstead()
    {
        // The whole point of putting the hold in a canonical field: editing it out is
        // tamper-evident, and an invalid MAC makes the guardian hold unconditionally.
        Wipe();
        try
        {
            MonkMode.Blocker.WriteScheduleConfig(Spec);
            var ini = Reload();
            ini.SetKeyValue("Guard", "ArmedCount", "0");     // no re-stamp
            ini.Save(MonkMode.Blocker.IniPath());
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());            // MAC no longer validates
            Assert.True(mm_guard.Guardian.AnyBlockHeld("", "0", false, "2026-08-12 12:00:00", macValid: false));
        }
        finally { Wipe(); }
    }
}

// ---- F74: the code print names WHO to send it to ----

public class PartnerRelayLineTests
{
    // The label is otherwise read by nothing at runtime: `setup` stored it, echoed the
    // argument straight back, and no later command ever mentioned it again - so a partner
    // who had never actually been given a code looked identical to one who had. This line
    // is the one place the stored label reaches the user, at the one moment the code is on
    // screen.

    [Fact]
    public void ALabel_IsNamedInTheRelayLine()
    {
        var line = MonkMode.Program.FormatPartnerRelayLine("mum (someone@example.com)");
        Assert.Contains("mum (someone@example.com)", line);
        Assert.Contains("NOW", line);
        Assert.DoesNotContain("\n", line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void NoUsableLabel_PrintsNothingAtAll(string label)
    {
        // Never a bare "send it to:" with nothing after it. The header already tells the
        // user to hand the code over, so silence is the right degradation.
        Assert.Equal("", MonkMode.Program.FormatPartnerRelayLine(label));
    }

    [Fact]
    public void ANullLabel_IsTreatedAsAbsent_NotACrash()
    {
        // SetupPartnerLabel never returns null, but this is the arm path: a direct caller
        // must not be able to throw between the arm and the one-and-only code print (F6).
        Assert.Equal("", MonkMode.Program.FormatPartnerRelayLine(null!));
    }

    [Fact]
    public void TheRelayLine_NeverContainsTheParseCodeAnchor()
    {
        // cv-d-smoke.ps1's ParseCode (:113-118) finds the line matching 'Emergency unlock
        // code' and takes the NEXT line as the code. If this line ever carried that anchor
        // it could be matched instead of the real header and the smoke would parse the
        // wrong line as the code. Belt and braces on top of it being printed BELOW the code.
        var line = MonkMode.Program.FormatPartnerRelayLine("mum (someone@example.com)");
        Assert.DoesNotContain("Emergency unlock code", line);
    }
}
