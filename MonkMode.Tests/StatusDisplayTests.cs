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

    // ---- FormatCoolOffStatusLine: the active-block exit line, all three branches ----

    [Fact]
    public void FormatCoolOffStatusLine_Committed_ShowsCodeOnlyExit()
        => Assert.Equal(
            "Exit:  committed block - the accountability code (shown at block start) is the only early exit, or wait for the timer.",
            MonkMode.Program.FormatCoolOffStatusLine(true, null));

    [Fact]
    public void FormatCoolOffStatusLine_Committed_IgnoresAnyCoolOffRemaining()
        // A committed block has NO self-serve cooling-off, so even a (spurious) remaining is ignored.
        => Assert.Equal(
            "Exit:  committed block - the accountability code (shown at block start) is the only early exit, or wait for the timer.",
            MonkMode.Program.FormatCoolOffStatusLine(true, TimeSpan.FromHours(1)));

    [Fact]
    public void FormatCoolOffStatusLine_CoolOffPending_ShowsMonotonicRemainingAndCancel()
        => Assert.Equal(
            "Exit:  cooling-off pending - lifts in about 1h of active time. Run 'monkmode unblock --cancel' to stay blocked.",
            MonkMode.Program.FormatCoolOffStatusLine(false, TimeSpan.FromHours(1)));

    [Fact]
    public void FormatCoolOffStatusLine_NothingPending_OffersTheSelfServeWaitAndCode()
        => Assert.Equal(
            "Exit:  run 'monkmode unblock' to start a cooling-off wait, or the accountability code (shown at block start) lifts it now.",
            MonkMode.Program.FormatCoolOffStatusLine(false, null));

    // ---- CoolOffRemainingFrom: deadline - HighWater, non-positive => not pending ----

    [Fact]
    public void CoolOffRemainingFrom_ValidFuture_IsTheDifference()
    {
        var rem = MonkMode.Blocker.CoolOffRemainingFrom(
            new DateTime(2026, 7, 8, 17, 0, 0).ToString(CA),
            new DateTime(2026, 7, 8, 15, 30, 0).ToString(CA));
        Assert.Equal(TimeSpan.FromMinutes(90), rem);
    }

    [Fact]
    public void CoolOffRemainingFrom_AtOrPastDeadline_IsNull()
    {
        // deadline == mark and deadline < mark are both "not pending" (about to lift / lifted).
        var atDeadline = new DateTime(2026, 7, 8, 15, 0, 0).ToString(CA);
        Assert.Null(MonkMode.Blocker.CoolOffRemainingFrom(atDeadline, atDeadline));
        Assert.Null(MonkMode.Blocker.CoolOffRemainingFrom(
            new DateTime(2026, 7, 8, 14, 0, 0).ToString(CA),
            new DateTime(2026, 7, 8, 15, 0, 0).ToString(CA)));
    }

    [Theory]
    [InlineData("garbage", "2026-07-08 15:00")]
    [InlineData("2026-07-08 17:00", "garbage")]
    [InlineData("", "2026-07-08 15:00")]
    [InlineData("2026-07-08 17:00", "")]
    public void CoolOffRemainingFrom_Unparseable_IsNull(string deadline, string mark)
        => Assert.Null(MonkMode.Blocker.CoolOffRemainingFrom(deadline, mark));

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
    public void NoConfig_CoolOffAndScheduleAccessors_AreInertAndCreateNothing()
    {
        var ini = MonkMode.Blocker.IniPath();
        var backup = MonkMode.Blocker.IniBackupPath();
        Wipe(ini, backup);
        try
        {
            Assert.Null(MonkMode.Blocker.CoolOffPendingRemaining());   // no config -> nothing pending
            Assert.False(MonkMode.Blocker.ScheduleWindowIsOpen());     // no config -> no window open
            Assert.False(File.Exists(ini));                            // reading created nothing
        }
        finally { Wipe(ini, backup); }
    }
}
