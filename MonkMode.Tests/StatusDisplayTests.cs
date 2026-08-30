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

// MonkMode.Tests - D5: rich `status` + friendly validation (Program.vb + Blocker.vb read-only bits).
//
// D5 enriches `status` (cooling-off / committed exit state, schedule live window state) and warns on
// typo'd block flags. The enforcement surface is untouched: the new Blocker accessors are read-only,
// MAC-gated, best-effort (like BlockIsCommitted). What is PURE and pinned here:
//   - FormatCoolOffStatusLine - the three-way exit line (committed / cooling-off pending / available);
//   - CoolOffRemainingFrom - deadline - HighWater with the "non-positive => not pending" contract;
//   - ScheduleWindowElapsed - byte-for-byte parity with monkmode.Service1.ScheduleElapsed, so the
//     CLI's "a window is open now" agrees with the service that actually enforces it;
//   - UnknownOptions - the typo detector (warn-not-fail).
// Plus the read-only-no-config safety of the two live accessors (return Nothing/False, create nothing),
// on the shared CliIniWriters collection (fence: only the test-bin ini/backup is ever touched).

using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MonkMode.Tests;

public class StatusDisplayPureTests
{
    private static readonly CultureInfo CA = new("en-CA");

    // ---- FormatExitStatusLine: the active-block exit line. ONE branch, since ledger 319 ----
    //
    // This replaced FormatCoolOffStatusLine, whose three branches (committed / cooling-off
    // pending / self-serve wait) described a choice of exits that no longer exists. Every
    // armed manual block now prints the same sentence, in both `status` renderers - the v1.1
    // slot table and the v9 single-block fallback - and CoolOffRemainingFrom went with the
    // countdown it computed.

    [Fact]
    public void FormatExitStatusLine_NamesTheTwoExitsAndDeniesAnyOther()
        => Assert.Equal(
            "Exit:  ends at its end time, or earlier with the partner code (shown once at block start): 'monkmode unblock --code <CODE>'. There is no other way out.",
            MonkMode.Program.FormatExitStatusLine());

    [Fact]
    public void FormatExitStatusLine_WithASlotId_NamesTheBlockInTheCommandHint()
        => Assert.Equal(
            "Exit:  ends at its end time, or earlier with the partner code (shown once at block start): 'monkmode unblock --id 4 --code <CODE>'. There is no other way out.",
            MonkMode.Program.FormatExitStatusLine("4"));

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData(null)]
    public void FormatExitStatusLine_UnnameableId_BuildsNoCommandTheUserCannotType(string? slotId)
        // "?" is ReadSlotViews' unreadable-id placeholder: it must never reach a command hint.
        => Assert.Equal(MonkMode.Program.FormatExitStatusLine(), MonkMode.Program.FormatExitStatusLine(slotId!));

    // ---- ScheduleWindowElapsed: byte-for-byte parity with the service's copy ----

    [Theory]
    [InlineData("2026-07-08 17:00", "2026-07-08 16:00")]   // au > hw -> not elapsed (open)
    [InlineData("2026-07-08 16:00", "2026-07-08 17:00")]   // au < hw -> elapsed
    [InlineData("2026-07-08 17:00", "2026-07-08 17:00")]   // au == hw -> elapsed (<=)
    [InlineData("", "2026-07-08 17:00")]                   // no window -> not elapsed
    [InlineData("garbage", "2026-07-08 17:00")]            // unparseable deadline -> fail-closed (not elapsed)
    [InlineData("2026-07-08 17:00", "garbage")]            // unparseable mark -> fail-closed (not elapsed)
    public void ScheduleWindowElapsed_MatchesServiceScheduleElapsed(string activeUntil, string hw)
    {
        // Normalise via en-CA round-trip so both copies parse the same literal regardless of OS culture.
        string au = Reformat(activeUntil), mark = Reformat(hw);
        Assert.Equal(
            monkmode.Service1.ScheduleElapsed(au, mark),
            MonkMode.Blocker.ScheduleWindowElapsed(au, mark));
    }

    [Fact]
    public void ScheduleWindowElapsed_FailClosed_OpenAndElapsedEndpoints()
    {
        var au = new DateTime(2026, 7, 8, 17, 0, 0).ToString(CA);
        var before = new DateTime(2026, 7, 8, 16, 0, 0).ToString(CA);
        var after = new DateTime(2026, 7, 8, 18, 0, 0).ToString(CA);
        Assert.False(MonkMode.Blocker.ScheduleWindowElapsed(au, before)); // hw before close -> still open
        Assert.True(MonkMode.Blocker.ScheduleWindowElapsed(au, after));   // hw past close -> elapsed
        Assert.False(MonkMode.Blocker.ScheduleWindowElapsed("", after));  // no window -> not elapsed
    }

    // ---- UnknownOptions: the typo detector ----

    [Fact]
    public void UnknownOptions_FlagsOnlyTheUnknownDashDashTokens()
    {
        var unknown = MonkMode.Program.UnknownOptions(
            new[] { "block", "--sites", "a.com", "--site", "b.com", "--commit" },
            MonkMode.Program.BlockOptionNames());
        Assert.Equal(new[] { "--site" }, unknown.ToArray());   // "block"/values skipped; --sites/--commit known
    }

    [Fact]
    public void UnknownOptions_MatchesEqualsFormOnItsHead_CaseInsensitive()
    {
        // "--for=2h" is known (head "--for"); "--SITES" matches case-insensitively; "--foo=x" is not.
        var unknown = MonkMode.Program.UnknownOptions(
            new[] { "--for=2h", "--SITES", "x.com", "--foo=x" },
            MonkMode.Program.BlockOptionNames());
        Assert.Equal(new[] { "--foo" }, unknown.ToArray());
    }

    [Fact]
    public void UnknownOptions_NullArgs_And_AllKnown_YieldEmpty()
    {
        Assert.Empty(MonkMode.Program.UnknownOptions(null!, MonkMode.Program.BlockOptionNames()));
        Assert.Empty(MonkMode.Program.UnknownOptions(
            new[] { "--sites", "a.com", "--for", "2h", "--commit" }, MonkMode.Program.BlockOptionNames()));
    }

    [Fact]
    public void UnknownOptions_AllSessionKill_IsAKnownBlockFlag_NotWarned()
    {
        // D2c: `block --all-session-kill` must NOT be flagged as an unrecognised option (it is a
        // real bare boolean flag), or the typo warning would nag on every use.
        Assert.Contains("--all-session-kill", MonkMode.Program.BlockOptionNames());
        Assert.Empty(MonkMode.Program.UnknownOptions(
            new[] { "--apps", "chrome.exe", "--for", "2h", "--all-session-kill" }, MonkMode.Program.BlockOptionNames()));
    }

    // ---- BooleanFlagsWithValue: the "=value on an on/off flag" detector (D5 follow-up) ----

    [Fact]
    public void BooleanFlagsWithValue_FlagsCommitEqualsValue_WhichHasFlagSilentlyIgnores()
    {
        // The gap this closes: `--commit=yes` is a NO-OP under HasFlag (it only matches the bare
        // "--commit"), and UnknownOptions won't flag it (its head is a known flag). Pin that this
        // detector catches it (case-insensitive, on the "--flag" head), so DoBlock can warn.
        Assert.Equal(new[] { "--commit" },
            MonkMode.Program.BooleanFlagsWithValue(new[] { "--sites", "a.com", "--commit=yes" }).ToArray());
        Assert.Equal(new[] { "--all-session-kill" },
            MonkMode.Program.BooleanFlagsWithValue(new[] { "--COMMIT" /* bare, fine */, "--all-session-kill=1" }).ToArray());
    }

    [Fact]
    public void BooleanFlagsWithValue_IgnoresBareBooleans_ValueFlags_And_Null()
    {
        // A bare boolean (correct usage), a value-flag written with "=" (--for=2h is legitimate),
        // and null/empty args all yield nothing - only a BOOLEAN flag misused with "=value" warns.
        Assert.Empty(MonkMode.Program.BooleanFlagsWithValue(new[] { "--commit", "--for=2h", "--sites", "a.com" }));
        Assert.Empty(MonkMode.Program.BooleanFlagsWithValue(null!));
        Assert.Empty(MonkMode.Program.BooleanFlagsWithValue(new string[] { }));
    }

    private static string Reformat(string s)
    {
        // "" / unparseable pass through verbatim (both copies must see the identical raw string);
        // a parseable datetime is re-emitted in en-CA so the InlineData literal is culture-stable.
        if (DateTime.TryParse(s, CA, DateTimeStyles.None, out var dt)) return dt.ToString(CA);
        return s;
    }
}

// The two live accessors are read-only + MAC-gated: with no config they return Nothing/False and
// create nothing. Shares the CliIniWriters collection so a concurrent arm can't race the wipe.
[Collection("CliIniWriters")]
public class StatusDisplayReadOnlyTests
{
    private static void Wipe(string ini, string backup)
    {
        try { if (File.Exists(ini)) File.Delete(ini); } catch { /* best-effort */ }
        try { if (File.Exists(backup)) File.Delete(backup); } catch { /* best-effort */ }
    }

    [Fact]
    public void NoConfig_ScheduleAccessor_IsInertAndCreatesNothing()
    {
        var ini = MonkMode.Blocker.IniPath();
        var backup = MonkMode.Blocker.IniBackupPath();
        Wipe(ini, backup);
        try
        {
            // Ledger 319 deleted Blocker.CoolOffPendingRemaining, the other accessor this
            // pinned; ScheduleWindowIsOpen carries the read-only-no-config contract alone now.
            Assert.False(MonkMode.Blocker.ScheduleWindowIsOpen());     // no config -> no window open
            Assert.False(File.Exists(ini));                            // reading created nothing
        }
        finally { Wipe(ini, backup); }
    }
}
