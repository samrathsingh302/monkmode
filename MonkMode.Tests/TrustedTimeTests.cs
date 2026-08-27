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

// MonkMode.Tests - F77: crediting machine-OFF downtime without giving B4 back.
//
// THE ASK (Samrath, 28/08/2026): "if it's 2AM and I shut down my laptop at 12AM,
// and then I turn on my laptop at 10AM, it should just unlock on boot."
//
// THE TRAP: the obvious implementation - credit the boot gap from DateTime.Now -
// is a one-line B4 bypass. Shut down, wind the clock forward, boot, block lifts.
// So downtime is credited against an EXTERNALLY corroborated clock only, held in
// the MAC-covered [Time] TrustedUtc anchor beside the mark.
//
// What is pure and pinned here (no network, no files, no registry - the HTTPS probe
// itself is the seam, like the DPAPI and SCM seams elsewhere):
//   - the witness quorum, including the DIRECTION of every failure mode;
//   - the credit arithmetic and its fail-closed zeros;
//   - ResolveMarkAndAnchor, the one decision - and specifically the cases that
//     matter: the honest overnight shutdown LIFTS, the rolled-forward clock does
//     NOT, and with no reading at all the output is byte-identical to the shipped
//     B4 behaviour;
//   - that the anchor's format is deliberately NOT the en-CA local one, which is
//     what makes the whole thing survive a timezone change.

using System.Globalization;

namespace MonkMode.Tests;

public class TrustedTimeTests
{
    private static readonly CultureInfo CA = new("en-CA");

    // en-CA local, the format [Time] HighWater and [Time] Until are stored in.
    private static string L(int y, int mo, int d, int h, int mi, int s = 0) =>
        new DateTime(y, mo, d, h, mi, s).ToString(CA);

    // Invariant UTC, the format the [Time] TrustedUtc anchor is stored in.
    private static string U(int y, int mo, int d, int h, int mi, int s = 0) =>
        new DateTime(y, mo, d, h, mi, s).ToString(
            monkmode.ConfigIntegrity.TrustedUtcFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseLocal(string s) => DateTime.Parse(s, CA);

    private const long Cap = monkmode.TrustedTime.MaxCreditSeconds;

    // VB ByRef surfaces as C# ref, so the outputs are collected through this shim
    // rather than out-vars.
    private static (string Hw, string Anchor) Resolve(
        string storedHw, string tickHw, string anchor, string reading)
    {
        string hw = "", outAnchor = "";
        monkmode.TrustedTime.ResolveMarkAndAnchor(
            storedHw, tickHw, anchor, reading, Cap, ref hw, ref outAnchor);
        return (hw, outAnchor);
    }

    // The shape of every "the machine came back" test: the mark froze at midnight,
    // the first tick after boot has credited its ordinary 10 monotonic seconds, and
    // the anchor says midnight-local was 23:00 UTC (BST).
    private static string StoredHw => L(2026, 8, 28, 0, 0);
    private static string TickHw => L(2026, 8, 28, 0, 0, 10);
    private static string Anchor => U(2026, 8, 27, 23, 0);
    private static string Until2Am => L(2026, 8, 28, 2, 0);

    // The test-owned config lives in the test bin directory (Blocker.IniPath()), never
    // the installed one - the project's hard fence. Cleared either side of the one test
    // that writes a real armed config.
    private static void WipeTestConfig()
    {
        foreach (var p in new[]
                 { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ------------------------------- the quorum -------------------------------

    [Fact]
    public void OneWitness_IsNeverEnough()
    {
        // A single source is a single point of forgery. The whole design rests on an
        // attacker having to defeat EVERY witness, so one reading is refused outright.
        var one = new List<DateTime> { new(2026, 8, 28, 9, 0, 0) };
        Assert.Equal("", monkmode.TrustedTime.CorroboratedUtc(one, 2, 300));
    }

    [Fact]
    public void NoWitnesses_IsRefused_NotTreatedAsZeroTime()
    {
        Assert.Equal("", monkmode.TrustedTime.CorroboratedUtc(new List<DateTime>(), 2, 300));
        Assert.Equal("", monkmode.TrustedTime.CorroboratedUtc(null, 2, 300));
    }

    [Fact]
    public void TwoAgreeingWitnesses_YieldTheEarlierOne()
    {
        // Minimum, not mean: the conservative reading is always the one we act on.
        var readings = new List<DateTime>
        {
            new(2026, 8, 28, 9, 0, 30),
            new(2026, 8, 28, 9, 0, 0),
        };
        Assert.Equal(U(2026, 8, 28, 9, 0), monkmode.TrustedTime.CorroboratedUtc(readings, 2, 300));
    }

    [Fact]
    public void AWitnessLyingLATE_CannotPullTheResultForward_AndDoesNotCount()
    {
        // The attack direction: a compromised witness claiming it is hours later, to
        // manufacture downtime credit. It cannot move the minimum, and it falls outside
        // the spread so it does not even count toward the quorum - the two honest ones
        // carry it, at the honest time.
        var readings = new List<DateTime>
        {
            new(2026, 8, 28, 9, 0, 0),
            new(2026, 8, 28, 9, 0, 10),
            new(2026, 8, 28, 23, 0, 0),   // the liar
        };
        Assert.Equal(U(2026, 8, 28, 9, 0), monkmode.TrustedTime.CorroboratedUtc(readings, 2, 300));
    }

    [Fact]
    public void ASingleHonestWitnessBesideALiar_FailsTheQuorum_RatherThanTrustingEither()
    {
        // Only one reading sits within the spread of the minimum, so there is no
        // corroboration at all => "" => no credit => the block over-blocks. Fail-closed.
        var readings = new List<DateTime>
        {
            new(2026, 8, 28, 9, 0, 0),
            new(2026, 8, 28, 23, 0, 0),
        };
        Assert.Equal("", monkmode.TrustedTime.CorroboratedUtc(readings, 2, 300));
    }

    [Fact]
    public void AWitnessLyingEARLY_OnlyEverCostsTime_NeverBuysIt()
    {
        // The harmless direction, pinned so nobody "fixes" the minimum into an average:
        // a witness reporting early drags the result back, which shortens the credit and
        // makes the block last LONGER. That is the safe way to be wrong.
        var honest = new List<DateTime> { new(2026, 8, 28, 9, 0, 0), new(2026, 8, 28, 9, 0, 5) };
        var withEarlyLiar = new List<DateTime>
        {
            new(2026, 8, 28, 9, 0, 0),
            new(2026, 8, 28, 9, 0, 5),
            new(2026, 8, 28, 8, 58, 0),
        };
        var a = monkmode.TrustedTime.CorroboratedUtc(honest, 2, 300);
        var b = monkmode.TrustedTime.CorroboratedUtc(withEarlyLiar, 2, 300);
        Assert.Equal(U(2026, 8, 28, 9, 0), a);
        Assert.Equal(U(2026, 8, 28, 8, 58), b);
        Assert.True(DateTime.Parse(b, CultureInfo.InvariantCulture)
                  < DateTime.Parse(a, CultureInfo.InvariantCulture));
    }

    // ----------------------------- the arithmetic -----------------------------

    [Theory]
    [InlineData("", "2026-08-28 09:00:00")]                     // no anchor
    [InlineData("2026-08-27 23:00:00", "")]                     // no reading
    [InlineData("garbage", "2026-08-28 09:00:00")]              // unparseable anchor
    [InlineData("2026-08-27 23:00:00", "garbage")]              // unparseable reading
    [InlineData("2026-08-27 23:00:00", "2026-08-27 23:00:00")]  // no time passed
    [InlineData("2026-08-27 23:00:00", "2026-08-27 22:00:00")]  // reading BEFORE the anchor
    public void EveryUnusableInput_CreditsExactlyZero(string anchor, string now)
    {
        Assert.Equal(0, monkmode.TrustedTime.ElapsedSinceAnchor(anchor, now, Cap));
    }

    [Fact]
    public void AnHonestTenHourGap_CreditsTenHours()
    {
        Assert.Equal(36000,
            monkmode.TrustedTime.ElapsedSinceAnchor("2026-08-27 23:00:00", "2026-08-28 09:00:00", Cap));
    }

    [Fact]
    public void AnAbsurdGap_IsCapped_NotRefused()
    {
        // Capping rather than refusing on purpose: refusing would punish a genuinely long
        // shutdown by never lifting the block, and it changes no attack outcome (anyone
        // who has fooled the whole quorum already has more credit than any block needs).
        Assert.Equal(Cap,
            monkmode.TrustedTime.ElapsedSinceAnchor("2020-01-01 00:00:00", "2026-08-28 09:00:00", Cap));
    }

    // --------------------- the decision: the cases that matter ---------------------

    [Fact]
    public void WithNoReading_TheMarkIsByteIdenticalToTheShippedB4Behaviour()
    {
        // THE REGRESSION THAT MATTERS MOST. Probes are minutes apart, so the
        // overwhelming majority of ticks carry no reading - and on those ticks this
        // feature must be invisible. The mark comes back as the exact string
        // AdvanceHighWater produced, and only the anchor moves, by exactly the amount
        // the monotonic rule credited. If this ever fails, F77 has started changing
        // enforcement on ordinary ticks, which it must never do.
        var r = Resolve(StoredHw, TickHw, Anchor, "");

        Assert.Equal(TickHw, r.Hw);
        Assert.Equal(U(2026, 8, 27, 23, 0, 10), r.Anchor);
    }

    [Fact]
    public void TheHeadline_ShutDownAtMidnight_BootAtTen_A2amBlockIsAlreadyOver()
    {
        // Samrath's exact scenario (28/08/2026). Armed until 02:00; the machine goes
        // down at 00:00 with mark and anchor in step; it comes back at 10:00 local /
        // 09:00 UTC and the first tick carrying a corroborated reading credits the ten
        // real hours.
        var r = Resolve(StoredHw, TickHw, Anchor, U(2026, 8, 28, 9, 0));

        // The mark has moved to the real time...
        Assert.Equal(new DateTime(2026, 8, 28, 10, 0, 0), ParseLocal(r.Hw));
        Assert.Equal(U(2026, 8, 28, 9, 0), r.Anchor);
        // ...so the 02:00 block reads EXPIRED through the real gate, and lifts on boot.
        Assert.True(monkmode.Service1.BlockHasExpired(Until2Am, ParseLocal(r.Hw), 5));
    }

    [Fact]
    public void TheAttack_ClockWoundForwardWithNoCorroboration_EarnsNothing_AndTheBlockHolds()
    {
        // Shut down at 00:00, set the clock to 10:00, boot with the network pulled (or
        // simply before any probe has succeeded). No reading => no credit => the 02:00
        // block is still standing, exactly as it does today.
        var r = Resolve(StoredHw, TickHw, Anchor, "");

        Assert.False(monkmode.Service1.BlockHasExpired(Until2Am, ParseLocal(r.Hw), 5));
    }

    [Fact]
    public void TheAttack_ClockWoundForwardWhileCorroborated_EarnsOnlyTheRealMinutes()
    {
        // The sharper version: the machine's clock says 10:00, but the witnesses say only
        // ten real minutes have passed. The credit follows the WITNESSES, so the mark
        // moves ten minutes and the 02:00 block is nowhere near expired. This is the test
        // that fails if anyone reintroduces DateTime.Now into the credit path - note that
        // the machine's claimed 10:00 appears nowhere in the inputs OR the output.
        var r = Resolve(StoredHw, TickHw, Anchor, U(2026, 8, 27, 23, 10));

        Assert.Equal(new DateTime(2026, 8, 28, 0, 10, 0), ParseLocal(r.Hw));
        Assert.Equal(U(2026, 8, 27, 23, 10), r.Anchor);
        Assert.False(monkmode.Service1.BlockHasExpired(Until2Am, ParseLocal(r.Hw), 5));
    }

    [Fact]
    public void AReadingBehindTheAnchor_NeverWalksTheMarkBackwards()
    {
        // Clock skew between witnesses, or an early-lying witness, must not be able to
        // REMOVE credit the monotonic rule already granted. Both outputs are the max of
        // the two rules, always.
        var r = Resolve(StoredHw, TickHw, Anchor, U(2026, 8, 27, 22, 0));

        Assert.Equal(TickHw, r.Hw);
        Assert.Equal(U(2026, 8, 27, 23, 0, 10), r.Anchor);
    }

    [Fact]
    public void NoAnchor_SeedsOneAndCreditsNothing()
    {
        // A config with no usable anchor has no instant to measure from, so the first
        // reading can only SEED it. Crediting here would be crediting from nowhere.
        var r = Resolve(StoredHw, TickHw, "", U(2026, 8, 28, 9, 0));

        Assert.Equal(TickHw, r.Hw);
        Assert.Equal(U(2026, 8, 28, 9, 0), r.Anchor);
        Assert.False(monkmode.Service1.BlockHasExpired(Until2Am, ParseLocal(r.Hw), 5));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void AnUnparseableStoredMark_LeavesBothOutputsUntouched(string storedHw)
    {
        // Mirrors AdvanceHighWater's fail-safe: a tampered mark is coupled to an already
        // failing MAC, so the block holds. We never fabricate a fresh MAC-shaped value.
        var r = Resolve(storedHw, "also garbage", Anchor, U(2026, 8, 28, 9, 0));

        Assert.Equal("also garbage", r.Hw);
        Assert.Equal(Anchor, r.Anchor);
    }

    [Fact]
    public void TheCreditIsAPureUtcDuration_SoTheLocalMarksOffsetIsIrrelevant()
    {
        // THE TIMEZONE PIN. The same UTC anchor and the same UTC reading must credit the
        // same number of seconds no matter what local time the mark sits at - that is
        // precisely what stops "change the timezone" from being a bypass. Two marks three
        // hours apart, one identical pair of UTC operands, one identical delta.
        var reading = U(2026, 8, 28, 9, 0);
        var markA = L(2026, 8, 28, 0, 0);
        var markB = L(2026, 8, 28, 3, 0);

        var a = Resolve(markA, markA, Anchor, reading);
        var b = Resolve(markB, markB, Anchor, reading);

        Assert.Equal(TimeSpan.FromHours(10), ParseLocal(a.Hw) - ParseLocal(markA));
        Assert.Equal(TimeSpan.FromHours(10), ParseLocal(b.Hw) - ParseLocal(markB));
    }

    [Fact]
    public void TheArmedConfigCarriesNoAnchor_SoABackDatedArmCannotManufactureCredit()
    {
        // THE BYPASS THIS CLOSES (found in review of F77's own first cut, 28/08/2026).
        // If the CLI seeded the anchor from the ARMING machine's DateTime.UtcNow, then:
        //
        //   wind the clock back 10h -> `block --for 1h` (Until and HighWater are
        //   self-consistent in the wrong frame, so the arm looks completely normal, but
        //   the anchor is now 10h behind reality) -> correct the clock -> the first probe
        //   computes trustedNow - anchor = 10h of "downtime" and the 1h block lifts in
        //   about a minute.
        //
        // The fix is that an armed config carries NO anchor and the service seeds it from
        // a corroborated reading. Pinned two ways: the arm writes an empty anchor, and an
        // empty anchor provably credits nothing even when a reading is in hand.
        // (1) The REAL arm path, through the real writer, into the test-owned config -
        // this is the half with teeth against a CLI regression: if anyone reinstates a
        // DateTime.UtcNow seed in Blocker.vb, this fails.
        try
        {
            WipeTestConfig();
            var armed = MonkMode.Blocker.ArmSlot(
                new[] { "example.com" }, Array.Empty<string>(), "", null,
                new DateTime(2027, 3, 1, 12, 0, 0), false);
            Assert.True(armed.Ok);

            var ini = new monkmode.IniFile();
            ini.Load(MonkMode.Blocker.IniPath());
            Assert.Equal("", ini.GetKeyValue("Time", "TrustedUtc"));
            // ...while the mark beside it IS seeded, so this is "no anchor", not "no F77".
            Assert.NotEqual("", ini.GetKeyValue("Time", "HighWater"));
        }
        finally { WipeTestConfig(); }

        // (2) The back-dated arm, replayed: a 1h block whose anchor is 10h stale-if-seeded.
        // With no anchor, the honest reading seeds and credits nothing, so the block
        // still has its full hour to run.
        var armedHw = L(2026, 8, 28, 0, 0);          // machine said midnight while arming
        var until1h = L(2026, 8, 28, 1, 0);          // --for 1h, in that same wrong frame
        var honestReading = U(2026, 8, 28, 9, 0);    // the real UTC now, 10h later

        var r = Resolve(armedHw, L(2026, 8, 28, 0, 0, 10), "", honestReading);

        Assert.Equal(honestReading, r.Anchor);       // seeded...
        Assert.Equal(L(2026, 8, 28, 0, 0, 10), r.Hw); // ...and NOT credited
        Assert.False(monkmode.Service1.BlockHasExpired(until1h, ParseLocal(r.Hw), 5));
    }

    [Fact]
    public void AProbeIsStillDueOnTheFastCadenceWhileTheAnchorIsMissing()
    {
        // The cost of not seeding at arm time is that downtime before the anchor exists
        // can never be credited - so an unseeded anchor must keep probing on the FAST
        // cadence however many probes have already succeeded, or "arm then immediately
        // close the laptop" would wait out the ten-minute steady interval first.
        // (Pinned through the pure cadence function, never by constructing a real probe -
        // that would fire live HTTPS requests, which this test project must never do.)
        Assert.True(monkmode.TrustedTime.ProbeRetryFastMs < monkmode.TrustedTime.ProbeRetrySteadyMs);

        // The one case that gets the slow cadence: a working probe AND an anchor.
        Assert.Equal(monkmode.TrustedTime.ProbeRetrySteadyMs,
                     monkmode.TrustedTime.ProbeIntervalMs(true, false));
        // Every other case stays fast.
        Assert.Equal(monkmode.TrustedTime.ProbeRetryFastMs,
                     monkmode.TrustedTime.ProbeIntervalMs(true, true));    // seeded probe, no anchor
        Assert.Equal(monkmode.TrustedTime.ProbeRetryFastMs,
                     monkmode.TrustedTime.ProbeIntervalMs(false, false));  // no probe yet
        Assert.Equal(monkmode.TrustedTime.ProbeRetryFastMs,
                     monkmode.TrustedTime.ProbeIntervalMs(false, true));   // neither
    }

    // ------------------------------ format + consts ------------------------------

    [Fact]
    public void TheAnchorFormat_IsInvariantUtc_AndDeliberatelyNotTheEnCaLocalOne()
    {
        // If someone ever "tidies" the anchor into the en-CA format every other datetime
        // in this config uses, the anchor starts moving with the machine's timezone and a
        // timezone change becomes free downtime credit. Pin the difference loudly.
        var t = new DateTime(2026, 8, 28, 9, 0, 0);
        Assert.Equal("2026-08-28 09:00:00", monkmode.TrustedTime.FormatUtc(t));
        Assert.NotEqual(t.ToString(CA), monkmode.TrustedTime.FormatUtc(t));

        DateTime back = DateTime.MinValue;
        Assert.True(monkmode.TrustedTime.TryParseUtc("2026-08-28 09:00:00", ref back));
        Assert.Equal(t, back);

        // ...and the en-CA rendering is NOT accepted as an anchor, so a mixed-format
        // config reads as "no anchor" (no credit) instead of silently misparsing.
        DateTime ignored = DateTime.MinValue;
        Assert.False(monkmode.TrustedTime.TryParseUtc(t.ToString(CA), ref ignored));
    }

    [Fact]
    public void TheSafetyConstants_ArePinned()
    {
        // Each of these is load-bearing, so each moves only deliberately.
        Assert.True(monkmode.TrustedTime.MinWitnesses >= 2);        // never trust one source
        Assert.Equal(300, monkmode.TrustedTime.WitnessSpreadCeilingSeconds);
        Assert.Equal(365L * 24 * 60 * 60, monkmode.TrustedTime.MaxCreditSeconds);
        // There must be enough witnesses to reach the quorum at all...
        Assert.True(monkmode.TrustedTime.WitnessUrls.Length >= monkmode.TrustedTime.MinWitnesses);
        // ...every one must be HTTPS, because the TLS handshake is the entire
        // anti-forgery story (a plain-http witness would be silently forgeable by the
        // very hosts-file redirect MonkMode itself installs)...
        Assert.All(monkmode.TrustedTime.WitnessUrls, u => Assert.StartsWith("https://", u));
        // ...and they must be distinct, or "two witnesses" is really one.
        Assert.Equal(monkmode.TrustedTime.WitnessUrls.Length,
                     monkmode.TrustedTime.WitnessUrls.Distinct().Count());
    }
}
