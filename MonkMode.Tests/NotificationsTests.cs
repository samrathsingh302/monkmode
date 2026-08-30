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

// MonkMode.Tests - D4: richer notifier notifications (MM_notify\Notifications.vb).
//
// The D4 notification vocabulary is a set of PURE, fail-soft message builders + derivations in the
// notifier assembly (mm_notify.Notifications, reachable via InternalsVisibleTo). Form1 owns the live
// wiring (when to toast + the latches); this file pins the STRINGS + the derivations they rest on:
//   - HumanizeShort / CountPackedList / RemainingFromMark / ShouldFirePeriodicReminder - the pure
//     helpers, each fail-soft (never throws, "0m"/0/null on garbage);
//   - the four message builders (block active on launch, cooling-off started, active reminder, block
//     ended), asserted by literal so a future wording change is deliberate - including the exact
//     historical block-ended text now that it is centralised.
// No disk / DPAPI / UI is touched (mirrors ScheduleDisplayRenderTests): these are string functions.

using System;
using System.Globalization;
using System.Xml.Linq;

namespace MonkMode.Tests;

public class NotificationsTests
{
    private static readonly CultureInfo CA = new("en-CA");

    // ---- HumanizeShort: the short d/h/m remaining dialect the toasts speak ----

    [Theory]
    [InlineData(9000, "2h 30m")]    // 2h30m
    [InlineData(2700, "45m")]       // 45m
    [InlineData(3600, "1h")]        // exactly 1h -> no minutes part
    [InlineData(90000, "1d 1h")]    // 25h -> days + remainder hours
    [InlineData(86400, "1d")]       // exactly 1d
    [InlineData(90, "1m")]          // 90s -> 1m
    [InlineData(30, "<1m")]         // positive but sub-minute
    [InlineData(0, "0m")]           // zero -> already elapsed
    [InlineData(-300, "0m")]        // negative -> clamped, never a bogus "-5m"
    public void HumanizeShort_RendersShortDurations(int seconds, string expected)
        => Assert.Equal(expected, mm_notify.Notifications.HumanizeShort(TimeSpan.FromSeconds(seconds)));

    // ---- CountPackedList: the "N sites / M apps" count from a PackList/PackApps field ----

    [Theory]
    [InlineData("reddit.com;news.com;", 2)]            // trailing ';' the packers append is not miscounted
    [InlineData("chrome.exe;foo.exe;bar.exe;", 3)]
    [InlineData("x.com;", 1)]
    [InlineData("  a.com ; b.com ;", 2)]               // trims each token
    [InlineData("a; ;b;", 2)]                          // a stray blank token is dropped
    [InlineData("null", 0)]                            // the packers' empty sentinel
    [InlineData("", 0)]
    public void CountPackedList_CountsNonEmptyTokens(string packed, int expected)
        => Assert.Equal(expected, mm_notify.Notifications.CountPackedList(packed));

    [Fact]
    public void CountPackedList_Null_IsZero_NeverThrows()
        => Assert.Equal(0, mm_notify.Notifications.CountPackedList(null!));

    // ---- RemainingFromMark: monotonic deadline - HighWater, fail-soft to null ----

    [Fact]
    public void RemainingFromMark_ValidPair_IsTheDifference()
    {
        var rem = mm_notify.Notifications.RemainingFromMark(
            new DateTime(2026, 7, 8, 17, 0, 0).ToString(CA),
            new DateTime(2026, 7, 8, 15, 0, 0).ToString(CA));
        Assert.Equal(TimeSpan.FromHours(2), rem);
    }

    [Fact]
    public void RemainingFromMark_DeadlineBehindMark_IsNegative()
    {
        // The service can advance HighWater past a cooling-off/expiry deadline; the raw span goes
        // negative and the callers gate on > 0 (they don't print a bogus "0m").
        var rem = mm_notify.Notifications.RemainingFromMark(
            new DateTime(2026, 7, 8, 15, 0, 0).ToString(CA),
            new DateTime(2026, 7, 8, 17, 0, 0).ToString(CA));
        Assert.NotNull(rem);
        Assert.Equal(-2, rem!.Value.TotalHours);
    }

    [Theory]
    [InlineData("garbage", "2026-07-08 15:00")]   // unparseable deadline
    [InlineData("2026-07-08 17:00", "garbage")]   // unparseable mark
    [InlineData("", "2026-07-08 15:00")]          // absent deadline (no cooling-off)
    [InlineData("2026-07-08 17:00", "")]          // absent mark
    public void RemainingFromMark_Unparseable_IsNull(string deadline, string mark)
        => Assert.Null(mm_notify.Notifications.RemainingFromMark(deadline, mark));

    // ---- ShouldFirePeriodicReminder: the pure monotonic interval gate ----

    [Theory]
    [InlineData(10000, 0, 5000, true)]    // 10s elapsed >= 5s interval
    [InlineData(5000, 0, 5000, true)]     // exactly at the boundary fires
    [InlineData(4000, 0, 5000, false)]    // not yet
    [InlineData(1000, 2000, 5000, false)] // negative delta (clock stall) never fires
    public void ShouldFirePeriodicReminder_FiresOnlyAfterInterval(long nowTick, long lastTick, long intervalMs, bool expected)
        => Assert.Equal(expected, mm_notify.Notifications.ShouldFirePeriodicReminder(nowTick, lastTick, intervalMs));

    // ---- BlockActiveMessage: the launch toast, across the subject + committed + remaining branches ----

    private static readonly DateTime Until = new(2026, 7, 8, 17, 0, 0);

    [Fact]
    public void BlockActiveMessage_SitesAndApps_WithRemaining()
        => Assert.Equal(
            "MonkMode is active - 3 sites and 2 apps blocked until 2026-07-08 17:00 (about 2h 30m left).",
            mm_notify.Notifications.BlockActiveMessage(3, 2, Until, false, TimeSpan.FromMinutes(150)));

    [Fact]
    public void BlockActiveMessage_SitesOnly_NoRemaining_OmitsTheLeftClause()
        => Assert.Equal(
            "MonkMode is active - 1 site blocked until 2026-07-08 17:00.",
            mm_notify.Notifications.BlockActiveMessage(1, 0, Until, false, null));

    [Fact]
    public void BlockActiveMessage_AppsOnly_Singular_And_Plural()
        => Assert.Equal(
            "MonkMode is active - 2 apps blocked until 2026-07-08 17:00 (about 45m left).",
            mm_notify.Notifications.BlockActiveMessage(0, 2, Until, false, TimeSpan.FromMinutes(45)));

    [Fact]
    public void BlockActiveMessage_Committed_AppendsCodeOnlyNote_AndPluralisesSingletons()
        => Assert.Equal(
            "MonkMode is active - 1 site and 1 app blocked until 2026-07-08 17:00 (about 1h left). Committed block - the accountability code is the only early exit.",
            mm_notify.Notifications.BlockActiveMessage(1, 1, Until, true, TimeSpan.FromHours(1)));

    [Fact]
    public void BlockActiveMessage_NonPositiveRemaining_OmitsTheLeftClause()
        => Assert.Equal(
            "MonkMode is active - 2 sites blocked until 2026-07-08 17:00.",
            mm_notify.Notifications.BlockActiveMessage(2, 0, Until, false, TimeSpan.Zero));

    [Fact]
    public void BlockActiveMessage_BothCountsZero_FailsSoftToGenericSubject()
        => Assert.Equal(
            "MonkMode is active - Your block blocked until 2026-07-08 17:00.",
            mm_notify.Notifications.BlockActiveMessage(0, 0, Until, false, null));

    // ---- The remaining toasts, pinned by literal ----
    //
    // Ledger 319 deleted CoolOffStartedMessage ("Cooling-off started - the block lifts in
    // about N ... Run 'monkmode unblock --cancel' to stay blocked") along with the exit it
    // announced. Nothing writes a CoolOffUntil, so the toast could never fire again.

    [Fact]
    public void BlockActiveReminderMessage_IsTheGentleNudge()
        => Assert.Equal(
            "Still in the zone - about 45m left on your block. Stay strong.",
            mm_notify.Notifications.BlockActiveReminderMessage(TimeSpan.FromMinutes(45)));

    [Fact]
    public void BlockEndedMessage_PinsTheExactHistoricalText()
        => Assert.Equal("Your block has ended. You're free — stay strong.", mm_notify.Notifications.BlockEndedMessage());

    // ---- v1.1 S4: the AGGREGATE toast for a multi-block machine ----
    //
    // v10 arms up to eight slots at once. The single-block wording above is built from the v9
    // single-block mirror, so with several blocks armed it would state ONE deadline and ONE
    // site/app count for all of them - true of the mirror, false of the machine. The aggregate
    // states only what is certain: how many blocks are armed, and how long the SHORTEST of
    // them has left, so the first thing to end is the number the user sees.

    [Theory]
    [InlineData(3, 45, "3 blocks active · 45m left")]
    [InlineData(2, 150, "2 blocks active · 2h 30m left")]
    [InlineData(1, 60, "1 block active · 1h left")]        // singular, via the shared CountNoun
    [InlineData(8, 1620, "8 blocks active · 1d 3h left")]  // the full cap, d/h vocabulary
    public void AggregateActiveMessage_NamesTheCountAndTheShortestRemaining(int blocks, int minutes, string expected)
        => Assert.Equal(expected, mm_notify.Notifications.AggregateActiveMessage(blocks, TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void AggregateActiveMessage_UnreadableOrElapsedRemaining_DropsTheLeftClause()
    {
        // Same rule BlockActiveMessage follows: never print a bogus or "0m" span. A slot whose
        // end cannot be read against the monotonic mark simply does not contribute one.
        Assert.Equal("2 blocks active", mm_notify.Notifications.AggregateActiveMessage(2, null));
        Assert.Equal("2 blocks active", mm_notify.Notifications.AggregateActiveMessage(2, TimeSpan.Zero));
        Assert.Equal("2 blocks active", mm_notify.Notifications.AggregateActiveMessage(2, TimeSpan.FromMinutes(-5)));
    }

    [Fact]
    public void AggregateActiveMessage_SubMinuteRemaining_UsesTheSharedShortVocabulary()
        => Assert.Equal("3 blocks active · <1m left",
                        mm_notify.Notifications.AggregateActiveMessage(3, TimeSpan.FromSeconds(30)));

    // ---- D4b: the persistent-toast PAYLOAD builder (pure XML; no WinRT is touched here) ----

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a & b", "a &amp; b")]                  // ampersand escaped first
    [InlineData("<x>", "&lt;x&gt;")]                    // angle brackets escaped
    [InlineData("q\"q", "q&quot;q")]                    // double quote
    [InlineData("it's", "it&apos;s")]                   // apostrophe
    [InlineData("", "")]                                // empty
    public void EscapeXml_EscapesTheFivePredefinedEntities(string raw, string expected)
        => Assert.Equal(expected, mm_notify.Notifications.EscapeXml(raw));

    [Fact]
    public void EscapeXml_Null_IsEmpty_NeverThrows()
        => Assert.Equal("", mm_notify.Notifications.EscapeXml(null!));

    [Fact]
    public void BuildToastXml_IsWellFormed_WithTitleAndBodyAsTheTwoTextLines()
    {
        // The real block-ended body carries a non-ASCII em dash + an apostrophe: prove it lands
        // in a well-formed ToastGeneric document with title as the first <text>, body as the second.
        var xml = mm_notify.Notifications.BuildToastXml(
            "MonkMode", mm_notify.Notifications.BlockEndedMessage());
        var doc = XDocument.Parse(xml);   // throws if malformed - the assertion is that it does not
        Assert.Equal("toast", doc.Root!.Name.LocalName);
        var binding = doc.Root.Element("visual")!.Element("binding")!;
        Assert.Equal("ToastGeneric", binding.Attribute("template")!.Value);
        var texts = binding.Elements("text").ToArray();
        Assert.Equal(2, texts.Length);
        Assert.Equal("MonkMode", texts[0].Value);
        Assert.Equal("Your block has ended. You're free — stay strong.", texts[1].Value);
    }

    [Fact]
    public void BuildToastXml_BodyWithMarkupChars_StaysWellFormed_AndRoundTrips()
    {
        // A body that ever gained '&' / '<' / '>' must not produce a document that throws in
        // XmlDocument.LoadXml (which would silently drop delivery to the balloon fallback).
        const string nasty = "block & <tag> \"quote\" 'apos' ends";
        var xml = mm_notify.Notifications.BuildToastXml("MonkMode", nasty);
        var doc = XDocument.Parse(xml);
        var texts = doc.Root!.Element("visual")!.Element("binding")!.Elements("text").ToArray();
        Assert.Equal(nasty, texts[1].Value);   // the parser decodes the escapes back to the original
    }

    // ---- D4b: the fail-safe delivery orchestration (pure; delegates stand in for the live paths) ----

    [Fact]
    public void DeliverWithFallback_PersistentSucceeds_ReportsPersistent_AndSkipsBalloon()
    {
        var balloonCalled = false;
        var got = mm_notify.Notifications.DeliverWithFallback(
            "hi", body => { /* persistent OK */ }, body => balloonCalled = true);
        Assert.Equal(mm_notify.Notifications.ToastDelivery.Persistent, got);
        Assert.False(balloonCalled);   // the transient path is never touched when persistent works
    }

    [Fact]
    public void DeliverWithFallback_PersistentThrows_FallsBackToBalloon_WithTheSameBody()
    {
        // The core fail-safe pin: a throwing persistent path (no WinRT / registration failure) must
        // still deliver the notification via the balloon, and must not let the exception escape.
        string? balloonBody = null;
        var got = mm_notify.Notifications.DeliverWithFallback(
            "the-body",
            body => throw new InvalidOperationException("no WinRT here"),
            body => balloonBody = body);
        Assert.Equal(mm_notify.Notifications.ToastDelivery.Balloon, got);
        Assert.Equal("the-body", balloonBody);
    }

    [Fact]
    public void DeliverWithFallback_BothThrow_SwallowsAndReportsNone_NeverThrows()
    {
        // Even total delivery failure must not crash the notifier or affect enforcement.
        var got = mm_notify.Notifications.DeliverWithFallback(
            "x",
            body => throw new Exception("persistent down"),
            body => throw new Exception("balloon down too"));
        Assert.Equal(mm_notify.Notifications.ToastDelivery.None, got);
    }
}
