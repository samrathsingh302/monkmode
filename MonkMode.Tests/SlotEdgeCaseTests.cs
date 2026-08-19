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

// MonkMode.Tests - v1.1 A2: EXHAUSTIVE / PROPERTY pins on the slot decision cores.
//
// SlotFoldTests, SlotRetireTests, SlotCanonicalTests, OvernightWindowTests and SlotCliTests
// each pin their slice's chosen cases. This file pins the same functions by EXHAUSTION, on the
// argument that a lift decision is not a place to sample: ClassifySlot has six boolean inputs
// (64 states) and ClassifyTick three axes, so "all of them" is cheap and "the six we thought
// of" is not. Where exhaustion is impossible the pin is an ALGEBRAIC property - monotonicity,
// subset inclusion, agreement between two independently-derived answers.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - ClassifySlot over all 64 states, against an independently written predicate. A drift
//     between the classifier and its stated rule is a wrong retire, i.e. a lift nobody asked
//     for. The same for ClassifyTick over its whole grid, INCLUDING a negative slot count
//     (unreachable today, but "> 0" is the guard, so 0 and -1 must both be shown safe).
//   - SlotExitDue == SlotEffectiveExit over a 1,440-cell field matrix. The two are derived
//     independently in the product (one through ClassifyHeartbeat, one through EffectiveExit)
//     and S3b's whole safety argument is that they cannot disagree.
//   - the OR-fold and the three unions over EVERY SUBSET of a slot pool: held is monotone and
//     the unions are monotone under subset inclusion. A union that ever SHRANK when a slot was
//     added would unblock a site the user had named twice.
//   - ParseSlotCount over ~4,000 inputs incl. culture forms, fullwidth digits, huge integers
//     and control characters: always inside [0, MaxSlots], never a throw. It runs inside the
//     tick, and its clamp is what makes a forged SlotCount freeze rather than under-enforce.
//   - the compaction-race Id guard over ids that LOOK alike ("1" / "01" / " 1 " / "10"): the
//     locate is ordinal on the trimmed text, so no near-miss id may ever resolve to a
//     neighbour's position (the mis-adjudicated-lift class, TOP RISK 2).
//   - overnight windows minute by minute across a whole 1,440-minute day, every day mask, and
//     both DST-adjacent nights: the open set is exactly the wrapped interval, with no gap at
//     midnight and no double-count.
//   - every --start boundary: the 30-day ceiling to the tick, the 60-second floor measured
//     from the START, and the whole duration/absolute grammar.
//
// Fences honoured: SlotEdgeCaseTests is PURE - strings, DateTimes and in-memory IniFile objects
// only. The one class that writes (SlotCompactionRaceTests, at the foot) drives the real
// per-slot writer against the TEST-BIN config and GUID temp paths, wiped in `finally`, and
// constructs its Service1 only through TestSvc. Nothing anywhere in this file touches the real
// hosts file, the registry, the SCM, a port or a deployed config, and nothing is ever armed on
// the machine.

using System.Globalization;
using System.Text;

namespace MonkMode.Tests;

public class SlotEdgeCaseTests
{
    private static readonly CultureInfo Ca = new("en-CA");
    private const long Grace = 5;

    // ---------------------------------------------------------------------------------
    // 1. ParseSlotCount - the clamp, brute-forced
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ParseSlotCount_EveryPlainIntegerFromMinusTwoThousand_LandsInsideTheClamp()
    {
        // The clamp is the whole reason a forged "[Slots] SlotCount=99" freezes instead of
        // enforcing 99 (or 0) blocks: the count is BOTH the loop bound and the printed header
        // line, so a value the reader cannot reproduce yields a canonical no stored MAC
        // matches. That only holds if the function is total over the integers.
        for (var n = -2000; n <= 2000; n++)
        {
            var parsed = monkmode.ConfigIntegrity.ParseSlotCount(n.ToString(CultureInfo.InvariantCulture));
            Assert.InRange(parsed, 0, monkmode.ConfigIntegrity.MaxSlots);
            var expected = n < 0 ? 0 : Math.Min(n, monkmode.ConfigIntegrity.MaxSlots);
            Assert.Equal(expected, parsed);
        }
    }

    [Fact]
    public void ParseSlotCount_JunkAndCultureForms_ClampFailClosed_AndNeverThrow()
    {
        // Every shape a hand-edited or machine-mangled ini can carry. Each must land in range;
        // the ones that are NOT plain integers must read as 0, because "I could not read the
        // count" and "there are no slots" have to be the same answer - both build a canonical
        // that cannot match a real stamp.
        var zeroes = new string?[]
        {
            null, "", " ", "\t", "\r\n", "abc", "banana", "3banana", "banana3", "+", "-",
            "--3", "3.0", "3,0", "1e2", "0x3", "&H3", "1/2", "٣", "３",
            "99999999999999999999", "-99999999999999999999", "2147483648", "-2147483649",
            "3 3", "3;", "NaN", "Infinity", "true", "one", "()", "[3]", "'3'", "\"3\"",
        };
        var surprises = new List<string>();
        foreach (var raw in zeroes)
        {
            var parsed = monkmode.ConfigIntegrity.ParseSlotCount(raw!);
            Assert.InRange(parsed, 0, monkmode.ConfigIntegrity.MaxSlots);
            if (parsed != 0) surprises.Add($"[{raw}] -> {parsed}");
        }
        Assert.True(surprises.Count == 0, string.Join(" ; ", surprises));
        // ...and the forms that ARE readable integers with decoration the Trim/TryParse pair
        // genuinely accepts, so the reader is not needlessly brittle about whitespace.
        Assert.Equal(3, monkmode.ConfigIntegrity.ParseSlotCount(" 3 "));
        Assert.Equal(3, monkmode.ConfigIntegrity.ParseSlotCount("\t3\r\n"));
        Assert.Equal(3, monkmode.ConfigIntegrity.ParseSlotCount("+3"));
        Assert.Equal(0, monkmode.ConfigIntegrity.ParseSlotCount("-3"));
        Assert.Equal(monkmode.ConfigIntegrity.MaxSlots, monkmode.ConfigIntegrity.ParseSlotCount("2147483647"));
        // A TRAILING NUL is swallowed by the BCL's integer parser (a long-standing
        // compatibility behaviour, not something this code asks for), so "3\0" reads as 3
        // rather than as garbage. Recorded rather than "fixed": the direction is the safe one
        // - it enforces the three slots the file describes instead of clamping to 0 - and the
        // canonical prints the CLAMPED integer, so "3\0" and "3" build byte-identical
        // canonicals and no MAC outcome can differ between them.
        Assert.Equal(3, monkmode.ConfigIntegrity.ParseSlotCount("3\0"));
        Assert.Equal(0, monkmode.ConfigIntegrity.ParseSlotCount("\0" + "3"));   // ...leading is not
    }

    [Fact]
    public void ParseSlotCount_IsTotal_OverAGeneratedJunkCorpus()
    {
        var pool = "0123456789+-., \t\r\nabcXYZ٣３\0eE";
        var rng = new Random(20260819);
        for (var i = 0; i < 4000; i++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 12);
            for (var c = 0; c < len; c++) sb.Append(pool[rng.Next(pool.Length)]);
            var parsed = monkmode.ConfigIntegrity.ParseSlotCount(sb.ToString());
            Assert.InRange(parsed, 0, monkmode.ConfigIntegrity.MaxSlots);
        }
    }

    // ---------------------------------------------------------------------------------
    // 2. ClassifySlot / ClassifyTick - exhaustive
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ClassifySlot_AllSixtyFourStates_MatchTheStatedRule()
    {
        // The rule, written out independently of the implementation (which routes through
        // ClassifyHeartbeat): retire only when the MAC is valid, no window is open, an exit is
        // genuinely due, and no schedule is armed to keep the slot alive for tomorrow.
        for (var bits = 0; bits < 64; bits++)
        {
            bool macValid = (bits & 1) != 0, expired = (bits & 2) != 0, coolOff = (bits & 4) != 0,
                 code = (bits & 8) != 0, windowOpen = (bits & 16) != 0, armed = (bits & 32) != 0;

            var expected = macValid && !windowOpen && (expired || coolOff || code) && !armed
                ? monkmode.Service1.SlotAction.Retire
                : monkmode.Service1.SlotAction.Hold;

            Assert.Equal(expected, monkmode.Service1.ClassifySlot(macValid, expired, coolOff, code, windowOpen, armed));
        }
    }

    [Fact]
    public void ClassifySlot_TheThreeHardHolds_AreUnconditional()
    {
        // Restated as three standalone sweeps because each is a separate fail-closed promise
        // and a truth table can go green while one of them is only accidentally satisfied.
        for (var bits = 0; bits < 64; bits++)
        {
            bool expired = (bits & 2) != 0, coolOff = (bits & 4) != 0, code = (bits & 8) != 0,
                 windowOpen = (bits & 16) != 0, armed = (bits & 32) != 0;
            // (a) an invalid MAC freezes, whatever else is true.
            Assert.Equal(monkmode.Service1.SlotAction.Hold,
                         monkmode.Service1.ClassifySlot(false, expired, coolOff, code, windowOpen, armed));
            // (b) an OPEN window outranks every exit reason (SD1).
            Assert.Equal(monkmode.Service1.SlotAction.Hold,
                         monkmode.Service1.ClassifySlot(true, expired, coolOff, code, true, armed));
            // (c) BETWEEN windows of an armed schedule, an otherwise-due exit still holds (c2).
            Assert.Equal(monkmode.Service1.SlotAction.Hold,
                         monkmode.Service1.ClassifySlot(true, expired, coolOff, code, false, true));
        }
    }

    [Fact]
    public void ClassifyTick_TheWholeGrid_IncludingCountsBelowZero()
    {
        // Teardown is the single most destructive act in the product (hosts stripped, DoH
        // restored, SafeBoot keys removed, the service stopped), so its gate is pinned over
        // every reachable AND unreachable input: a negative count must behave as zero, not as
        // "nothing armed, but skip the residual check".
        foreach (var macValid in new[] { true, false })
        for (var count = -5; count <= 10; count++)
        foreach (var residual in new[] { monkmode.Service1.HeartbeatAction.Lift,
                                         monkmode.Service1.HeartbeatAction.Restamp,
                                         monkmode.Service1.HeartbeatAction.Hold })
        {
            var expected = !macValid ? monkmode.Service1.TickAction.Hold
                         : count > 0 ? monkmode.Service1.TickAction.Restamp
                         : residual != monkmode.Service1.HeartbeatAction.Lift ? monkmode.Service1.TickAction.Restamp
                         : monkmode.Service1.TickAction.TeardownAll;
            Assert.Equal(expected, monkmode.Service1.ClassifyTick(macValid, count, residual));
        }
    }

    [Fact]
    public void ClassifyTick_OneArmedSlot_ForbidsTeardown_ForEveryResidualAndEveryCount()
    {
        // The load-bearing demotion of the v9 residual: with anything armed it cannot cause a
        // teardown, only hold one back. Back-dating [Time] Until beside a live slot is then a
        // no-op rather than the machine-wide lift it used to be.
        for (var count = 1; count <= monkmode.ConfigIntegrity.MaxSlots; count++)
        foreach (var residual in new[] { monkmode.Service1.HeartbeatAction.Lift,
                                         monkmode.Service1.HeartbeatAction.Restamp,
                                         monkmode.Service1.HeartbeatAction.Hold })
            Assert.NotEqual(monkmode.Service1.TickAction.TeardownAll,
                            monkmode.Service1.ClassifyTick(true, count, residual));
    }

    // ---------------------------------------------------------------------------------
    // 3. SlotExitDue == SlotEffectiveExit, over a full field matrix
    // ---------------------------------------------------------------------------------

    private const string Hw = "2026-08-12 12:00:00";
    private static readonly DateTime AsOf = new(2026, 8, 12, 12, 0, 0);
    private const string Past = "2026-08-12 06:00:00";
    private const string Future = "2026-08-12 18:00:00";
    private const string ValidSpec = "v2;1234567:0900-1700;sites=x.com;apps=";

    private static monkmode.Service1.SlotState S(string until, string coolOff, string unlocked,
                                                 string windowUntil, string spec)
        => new()
        {
            Id = "1",
            UntilText = until,
            CoolOffUntil = coolOff,
            PartnerUnlockedAt = unlocked,
            ScheduleActiveUntil = windowUntil,
            ScheduleSpec = spec,
        };

    [Fact]
    public void SlotExitDue_AgreesWithSlotEffectiveExit_AcrossTheWholeFieldMatrix()
    {
        // S3b's safety argument in one assertion: Retire <=> SlotEffectiveExit. The two are
        // computed down DIFFERENT paths (ClassifyHeartbeat vs EffectiveExit), so a divergence
        // is exactly the "two gates drift apart" failure the split was designed to avoid, and
        // it would show up as a slot that reports it may not exit and is retired anyway.
        var untils = new[] { "", Past, Future, "not-a-date", monkmode.Service1.ScheduleOnlyExpiredUntil };
        var coolOffs = new[] { "", Past, Future, "garbage" };
        var unlockeds = new[] { "", "2026-08-12 11:00:00", "   " };
        var windows = new[] { "", Past, Future, "junk" };
        var specs = new[] { "", ValidSpec, "v1;1:2230-0400;sites=x;apps=", "not-a-spec" };

        var cells = 0;
        foreach (var macValid in new[] { true, false })
        foreach (var until in untils)
        foreach (var coolOff in coolOffs)
        foreach (var unlocked in unlockeds)
        foreach (var window in windows)
        foreach (var spec in specs)
        {
            var slot = S(until, coolOff, unlocked, window, spec);
            var retire = monkmode.Service1.SlotExitDue(slot, AsOf, Grace, macValid, Hw) == monkmode.Service1.SlotAction.Retire;
            var mayExit = monkmode.Service1.SlotEffectiveExit(slot, Hw, Grace, macValid);
            Assert.Equal(mayExit, retire);
            if (!macValid) Assert.False(retire);          // frozen configs never retire anything
            cells++;
        }
        Assert.Equal(2 * 5 * 4 * 3 * 4 * 4, cells);
    }

    [Fact]
    public void SlotExitDue_ANullSlot_Holds_RatherThanRetiringNothingness()
    {
        foreach (var macValid in new[] { true, false })
        {
            Assert.Equal(monkmode.Service1.SlotAction.Hold,
                         monkmode.Service1.SlotExitDue(null, AsOf, Grace, macValid, Hw));
            Assert.False(monkmode.Service1.SlotEffectiveExit(null, Hw, Grace, macValid));
        }
    }

    // ---------------------------------------------------------------------------------
    // 4. the folds and the unions, over EVERY subset
    // ---------------------------------------------------------------------------------

    private static monkmode.Service1.SlotState Sited(string id, string until, params string[] sites)
        => new()
        {
            Id = id,
            UntilText = until,
            Sites = new List<string>(sites),
            Apps = new List<string>(sites.Select(s => s.Replace(".com", ".exe"))),
            UrlPatterns = new List<string>(sites.Select(s => s + "/x")),
        };

    private static readonly monkmode.Service1.SlotState[] Pool =
    {
        Sited("1", Future, "a.com", "shared.com"),
        Sited("2", Past, "b.com"),
        Sited("3", "", "c.com"),
        Sited("4", Future, "shared.com", "d.com"),
    };

    [Fact]
    public void AnyBlockHeld_IsTheOrFold_OverEverySubsetOfThePool()
    {
        for (var mask = 0; mask < (1 << Pool.Length); mask++)
        {
            var slots = Subset(mask);
            var expected = slots.Any(s => monkmode.Service1.SlotHeld(s, AsOf, Grace, true, Hw));
            Assert.Equal(expected, monkmode.Service1.AnyBlockHeld(slots, AsOf, Grace, true, Hw));
            // ...and an invalid MAC holds for EVERY subset, the empty one included.
            Assert.True(monkmode.Service1.AnyBlockHeld(slots, AsOf, Grace, false, Hw));
        }
    }

    [Fact]
    public void TheThreeUnions_AreMonotoneUnderSubsetInclusion()
    {
        // Widen-only, as an order relation rather than as examples: for every pair of subsets
        // S subset-of T, union(S) is contained in union(T). A union that lost an entry when a
        // slot joined it would silently unblock a site - and with dedup and enforcement
        // filtering both in the path, that is not obvious by inspection.
        for (var sub = 0; sub < (1 << Pool.Length); sub++)
        for (var sup = 0; sup < (1 << Pool.Length); sup++)
        {
            if ((sub & sup) != sub) continue;                 // only genuine subsets
            AssertContains(monkmode.Service1.UnionSlotSites(Subset(sub), AsOf, Grace, true, Hw),
                           monkmode.Service1.UnionSlotSites(Subset(sup), AsOf, Grace, true, Hw));
            AssertContains(monkmode.Service1.UnionSlotApps(Subset(sub), AsOf, Grace, true, Hw),
                           monkmode.Service1.UnionSlotApps(Subset(sup), AsOf, Grace, true, Hw));
            AssertContains(monkmode.Service1.UnionSlotUrlPatterns(Subset(sub), AsOf, Grace, true, Hw),
                           monkmode.Service1.UnionSlotUrlPatterns(Subset(sup), AsOf, Grace, true, Hw));
        }
    }

    [Fact]
    public void TheUnions_DedupeWithoutLosing_AndNeverOutgrowTheirInputs()
    {
        // A domain named by two slots appears once (hosts entries must not duplicate), and the
        // union never invents an entry no slot carries.
        var all = Subset((1 << Pool.Length) - 1);
        var sites = monkmode.Service1.UnionSlotSites(all, AsOf, Grace, true, Hw);
        Assert.Equal(sites.Count, sites.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var named = new HashSet<string>(all.SelectMany(s => s.Sites), StringComparer.OrdinalIgnoreCase);
        foreach (var s in sites) Assert.Contains(s, named);
        Assert.Contains("shared.com", sites);     // in slots 1 and 4, both enforcing
    }

    private static List<monkmode.Service1.SlotState> Subset(int mask)
    {
        var slots = new List<monkmode.Service1.SlotState>();
        for (var i = 0; i < Pool.Length; i++)
            if ((mask & (1 << i)) != 0) slots.Add(Pool[i]);
        return slots;
    }

    private static void AssertContains(List<string> smaller, List<string> larger)
    {
        foreach (var entry in smaller)
            Assert.Contains(entry, larger, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------
    // 5. the compaction-race Id guard, against ids that look alike
    // ---------------------------------------------------------------------------------

    private static monkmode.IniFile IniWithIds(int slotCount, params string[] idsByPosition)
    {
        var ini = new monkmode.IniFile();
        ini.AddSection("Slots");
        ini.SetKeyValue("Slots", "SlotCount", slotCount.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < idsByPosition.Length; i++)
        {
            var sec = "Slot" + (i + 1).ToString(CultureInfo.InvariantCulture);
            ini.AddSection(sec);
            ini.SetKeyValue(sec, "Id", idsByPosition[i]);
        }
        return ini;
    }

    [Fact]
    public void FindSlotPositionById_NearMissIds_NeverResolveToANeighbour()
    {
        // The write path re-locates by Id and writes NOTHING when it is gone; that is only
        // safe if the comparison cannot be widened. "1" must not find "10", "01" or "1x" -
        // each of those would land a CoolOffUntil or a PartnerUnlockedAt on a DIFFERENT
        // block, which is the mis-adjudicated-lift class in its purest form.
        var ini = IniWithIds(4, "1", "10", "01", "1x");
        Assert.Equal(1, monkmode.Service1.FindSlotPositionById(ini, "1"));
        Assert.Equal(2, monkmode.Service1.FindSlotPositionById(ini, "10"));
        Assert.Equal(3, monkmode.Service1.FindSlotPositionById(ini, "01"));
        Assert.Equal(4, monkmode.Service1.FindSlotPositionById(ini, "1x"));
        // Nothing else resolves at all.
        foreach (var junk in new[] { "1 x", "11", "100", "0", "-1", "1.0", "1;", "X", "1\n2" })
            Assert.Equal(0, monkmode.Service1.FindSlotPositionById(ini, junk));
        // Surrounding whitespace is trimmed on BOTH sides of the comparison, so a hand-edited
        // "Id = 1 " still routes - over-locating a slot the user really named is not the
        // dangerous direction; mis-locating a different one is.
        Assert.Equal(1, monkmode.Service1.FindSlotPositionById(ini, " 1 "));
        Assert.Equal(1, monkmode.Service1.FindSlotPositionById(IniWithIds(1, " 1 "), "1"));
    }

    [Fact]
    public void FindSlotPositionById_AgreesWithFindSlotById_OnEveryIdInThePool()
    {
        // The trigger channel routes with FindSlotById and the writer locates with
        // FindSlotPositionById. If the two ever disagreed, a trigger would be accepted and
        // then silently dropped by the write - an exit the user performed that never happened.
        var ids = new[] { "1", "10", "01", "1x", " 7 ", "" };
        var ini = IniWithIds(4, "1", "10", "01", "1x");
        var slots = new List<monkmode.Service1.SlotState>
        {
            new() { Id = "1" }, new() { Id = "10" }, new() { Id = "01" }, new() { Id = "1x" },
        };
        foreach (var id in ids)
        {
            var located = monkmode.Service1.FindSlotPositionById(ini, id) != 0;
            var routed = monkmode.Service1.FindSlotById(slots, id) is not null;
            Assert.Equal(routed, located);
        }
    }

    [Fact]
    public void FindSlotPositionById_IsBoundedByTheClampedCount_ForEveryForgedValue()
    {
        // A forged count must never widen the scan into a stale [SlotN] a compaction left
        // behind - the canonical ignores those sections, so a write into one would be
        // invisible to every reader while still costing a re-stamp.
        foreach (var forged in new[] { "99", "-1", "banana", "", "8" })
        {
            var ini = IniWithIds(3, "1", "2", "3");
            ini.SetKeyValue("Slots", "SlotCount", forged);
            var clamped = monkmode.ConfigIntegrity.ParseSlotCount(forged);
            for (var pos = 1; pos <= 3; pos++)
            {
                var expected = pos <= clamped ? pos : 0;
                Assert.Equal(expected, monkmode.Service1.FindSlotPositionById(ini, pos.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // 6. PENDING -> ACTIVE: the fail-closed edges
    // ---------------------------------------------------------------------------------

    [Fact]
    public void SlotStartDue_EveryUnreadableInput_LeavesTheSlotPending()
    {
        // Not-due is the over-blocking answer: a PENDING slot already contributes its sites to
        // every union, so a slot that never activates keeps blocking and never lifts.
        // (Slash-separated dates are deliberately NOT in this list: DateTime.TryParse under
        // en-CA reads "2026/08/12" and even "12/2026/08" perfectly well, so they are good
        // input, not junk. Only genuinely unreadable text belongs here.)
        var junk = new[] { "", " ", "not-a-date", "2026-13-45 99:99:99", "0", "\0", "yesterday", "--", "99999" };
        var surprises = new List<string>();
        foreach (var startAt in junk)
        foreach (var hw in junk)
            if (monkmode.Service1.SlotStartDue(new monkmode.Service1.SlotState { StartAt = startAt }, hw))
                surprises.Add($"start=[{startAt}] hw=[{hw}]");
        foreach (var hw in junk)
            if (monkmode.Service1.SlotStartDue(new monkmode.Service1.SlotState { StartAt = Past }, hw))
                surprises.Add($"start=past hw=[{hw}]");
        Assert.True(surprises.Count == 0, string.Join(" ; ", surprises));
        Assert.False(monkmode.Service1.SlotStartDue(null, Hw));
        // ...and a slot that is not PENDING at all (it already has an Until) is never "due".
        Assert.False(monkmode.Service1.SlotStartDue(
            new monkmode.Service1.SlotState { StartAt = Past, UntilText = Future }, Hw));
        // The one shape that IS due: a pending slot whose start has been reached by the
        // trusted mark.
        Assert.True(monkmode.Service1.SlotStartDue(new monkmode.Service1.SlotState { StartAt = Past }, Hw));
        Assert.True(monkmode.Service1.SlotStartDue(new monkmode.Service1.SlotState { StartAt = Hw }, Hw));
        Assert.False(monkmode.Service1.SlotStartDue(new monkmode.Service1.SlotState { StartAt = Future }, Hw));
    }

    [Fact]
    public void ComputeSlotActivationUntil_UnreadableDurationsAndMarks_WriteNothing()
    {
        // "" means the activation writes NOTHING and the slot stays PENDING for the next tick,
        // which over-blocks. (An absurdly large DurationSeconds is a separate, already-recorded
        // robustness item - it is not re-raised here, and no value near the ceiling is fed in.)
        var badDurations = new[] { "", " ", "abc", "0", "-1", "-3600", "1.5", "3,600", "1e3", "\0", null };
        foreach (var d in badDurations)
            Assert.Equal("", monkmode.Service1.ComputeSlotActivationUntil(Hw, d!));
        foreach (var hw in new[] { "", " ", "not-a-date", "\0", null })
            Assert.Equal("", monkmode.Service1.ComputeSlotActivationUntil(hw!, "3600"));
        // The good case is HighWater + duration, in the stored en-CA shape - never the wall clock.
        Assert.Equal(AsOf.AddSeconds(3600).ToString(Ca), monkmode.Service1.ComputeSlotActivationUntil(Hw, "3600"));
        Assert.Equal(AsOf.AddSeconds(1).ToString(Ca), monkmode.Service1.ComputeSlotActivationUntil(Hw, " 1 "));
    }

    // ---------------------------------------------------------------------------------
    // 7. overnight windows, minute by minute
    // ---------------------------------------------------------------------------------

    // 2026-03-23 is a Monday. 2026-03-29 (spring forward) and 2026-10-25 (fall back) are the
    // two DST Sundays of that year in the UK.
    private static readonly string[] Week =
        { "2026-03-23", "2026-03-24", "2026-03-25", "2026-03-26", "2026-03-27", "2026-03-28", "2026-03-29" };

    private static List<monkmode.Service1.ScheduleOpen> Eval(string spec, string now)
        => monkmode.Service1.EvaluateWindows(monkmode.Service1.ParseSchedule(spec).Windows, "", now, 0, false);

    [Fact]
    public void WrappedWindow_EveryMinuteOfTheMaskedDayAndItsTail_IsExactlyTheWrappedInterval()
    {
        // 2,880 evaluations: every minute of the masked Monday and of the unmasked Tuesday.
        // The window is 22:30 -> 04:00, so the open set is exactly [Mon 22:30, Tue 04:00) with
        // NO gap at midnight - the missing-gap case (P22 case (c), the post-midnight tail
        // belonging to YESTERDAY's mask) is the fail-open a reboot at 00:30 would expose.
        const string monNight = "v2;1:2230-0400;sites=x.com;apps=";
        for (var minute = 0; minute < 1440; minute++)
        {
            var mon = Eval(monNight, Stamp("2026-03-23", minute));
            Assert.Equal(minute >= 22 * 60 + 30, mon.Count == 1);
            var tue = Eval(monNight, Stamp("2026-03-24", minute));
            Assert.Equal(minute < 4 * 60, tue.Count == 1);
            // Never more than one open window from a one-window spec, at any minute.
            Assert.True(mon.Count <= 1 && tue.Count <= 1);
        }
    }

    [Fact]
    public void WrappedWindow_TheRemainingSecondsFallMonotonically_RightThroughMidnight()
    {
        // The hold must count DOWN across the date change, not restart. A remaining that ever
        // rose would extend the window past its own close; one that jumped would mean the two
        // branches (pre- and post-midnight) disagree about the same window.
        const string monNight = "v2;1:2230-0400;sites=x.com;apps=";
        var previous = long.MaxValue;
        for (var minute = 22 * 60 + 30; minute < 1440 + 4 * 60; minute++)
        {
            var day = minute < 1440 ? "2026-03-23" : "2026-03-24";
            var open = Assert.Single(Eval(monNight, Stamp(day, minute % 1440)));
            Assert.True(open.RemainingSeconds < previous,
                        $"remaining did not fall at minute {minute}: {open.RemainingSeconds} vs {previous}");
            Assert.True(open.RemainingSeconds > 0);
            previous = open.RemainingSeconds;
        }
        Assert.Equal(60, previous);   // the last minute before 04:00 has a minute left
    }

    [Fact]
    public void WrappedWindow_EveryDayMask_OpensOnItsOwnDayAndSpillsOntoTheNext()
    {
        // Seven single-day masks x seven days x the two probe minutes. The Sunday mask is the
        // one that must spill onto MONDAY (the mask wraps the week, not just the day), which a
        // naive "yesterday = today - 1" would get wrong at the week boundary.
        for (var maskDay = 1; maskDay <= 7; maskDay++)
        {
            var spec = "v2;" + maskDay + ":2230-0400;sites=x.com;apps=";
            for (var dayIndex = 0; dayIndex < 7; dayIndex++)
            {
                var evening = Eval(spec, Stamp(Week[dayIndex], 23 * 60));
                var smallHours = Eval(spec, Stamp(Week[dayIndex], 2 * 60));
                Assert.Equal(dayIndex + 1 == maskDay, evening.Count == 1);
                Assert.Equal(dayIndex == maskDay % 7, smallHours.Count == 1);
            }
        }
    }

    [Fact]
    public void WrappedWindow_TheEveryNightMask_NeverDoubleCountsAtAnyMinute()
    {
        // With every day masked, the pre-midnight opening and the post-midnight tail overlap
        // conceptually at every minute of the night; the evaluator must still report ONE open
        // window, or the extend-never-shorten fold would take the wrong (shorter) one.
        const string everyNight = "v2;1234567:2230-0400;sites=x.com;apps=";
        for (var minute = 0; minute < 1440; minute++)
        {
            var open = Eval(everyNight, Stamp("2026-03-25", minute));
            var shouldBeOpen = minute >= 22 * 60 + 30 || minute < 4 * 60;
            Assert.Equal(shouldBeOpen ? 1 : 0, open.Count);
        }
    }

    [Fact]
    public void WrappedWindow_BothDstNights_HoldWithoutAGap()
    {
        // The clocks move at 01:00/02:00 local on these two dates. EvaluateWindows reasons on
        // wall-clock text, so the pin is that neither night develops a hole - a gap would mean
        // the block lifts for part of the night once a year, which is precisely the sort of
        // thing nobody would ever reproduce on purpose.
        const string everyNight = "v2;1234567:2230-0400;sites=x.com;apps=";
        foreach (var (night, morning) in new[] { ("2026-03-28", "2026-03-29"), ("2026-10-24", "2026-10-25") })
        {
            for (var minute = 22 * 60 + 30; minute < 1440; minute++)
                Assert.Single(Eval(everyNight, Stamp(night, minute)));
            for (var minute = 0; minute < 4 * 60; minute++)
                Assert.Single(Eval(everyNight, Stamp(morning, minute)));
            Assert.Empty(Eval(everyNight, Stamp(morning, 4 * 60)));
        }
    }

    [Fact]
    public void SameDayWindows_AreUnaffectedByTheWrapSupport_AtEveryMinute()
    {
        // The v1.0 shape, re-pinned minute by minute: adding overnight support must not have
        // moved a single boundary of an ordinary 09:00-17:00 window.
        const string office = "v2;1234567:0900-1700;sites=x.com;apps=";
        for (var minute = 0; minute < 1440; minute++)
        {
            var open = Eval(office, Stamp("2026-03-25", minute));
            var shouldBeOpen = minute >= 9 * 60 && minute < 17 * 60;
            Assert.Equal(shouldBeOpen ? 1 : 0, open.Count);
            if (shouldBeOpen) Assert.Equal((17 * 60 - minute) * 60, open[0].RemainingSeconds);
        }
    }

    private static string Stamp(string day, int minuteOfDay)
        => $"{day} {minuteOfDay / 60:00}:{minuteOfDay % 60:00}:00";

    // ---------------------------------------------------------------------------------
    // 8. --start boundaries
    // ---------------------------------------------------------------------------------

    private static readonly DateTime ArmNow = new(2026, 8, 9, 12, 0, 0);

    [Fact]
    public void StartIsTooFarAhead_IsExactAtTheTick()
    {
        // Refusing at arm time is never fail-open (nothing is armed), but refusing a legal
        // 30-day delay would be a bug the user cannot work around, so the boundary is pinned
        // at 1-tick resolution on both sides.
        var ceiling = ArmNow.AddDays(MonkMode.Program.MaxStartDelayDays);
        Assert.False(MonkMode.Program.StartIsTooFarAhead(ceiling.AddTicks(-1), ArmNow));
        Assert.False(MonkMode.Program.StartIsTooFarAhead(ceiling, ArmNow));
        Assert.True(MonkMode.Program.StartIsTooFarAhead(ceiling.AddTicks(1), ArmNow));
        // A past start is never "too far ahead" - P28 turns it into "start now".
        Assert.False(MonkMode.Program.StartIsTooFarAhead(DateTime.MinValue, ArmNow));
    }

    [Fact]
    public void ClassifyBlockWindow_TheSixtySecondFloor_IsExactOnBothSidesForBothShapes()
    {
        // The floor is measured from the WINDOW START, so it must behave identically whether
        // the start is now or a week away. Second-by-second across the boundary, both shapes.
        foreach (var delayed in new[] { true, false })
        {
            var start = delayed ? ArmNow.AddDays(7) : ArmNow;
            for (var seconds = 55; seconds <= 65; seconds++)
            {
                var expected = seconds <= 60 ? MonkMode.Program.WindowRefusal.TooShort
                                             : MonkMode.Program.WindowRefusal.None;
                Assert.Equal(expected, MonkMode.Program.ClassifyBlockWindow(delayed, start, start.AddSeconds(seconds)));
            }
            // Exactly-equal ends and past ends: a DELAYED block is told which flag is wrong,
            // an immediate one is told about the floor.
            Assert.Equal(delayed ? MonkMode.Program.WindowRefusal.EndsBeforeStart : MonkMode.Program.WindowRefusal.TooShort,
                         MonkMode.Program.ClassifyBlockWindow(delayed, start, start));
            Assert.Equal(delayed ? MonkMode.Program.WindowRefusal.EndsBeforeStart : MonkMode.Program.WindowRefusal.TooShort,
                         MonkMode.Program.ClassifyBlockWindow(delayed, start, start.AddDays(-1)));
        }
    }

    [Theory]
    // durations - the --for grammar, with the optional leading '+'
    [InlineData("+90m", 90 * 60)]
    [InlineData("90m", 90 * 60)]
    [InlineData("2h", 2 * 3600)]
    [InlineData("1d12h", 36 * 3600)]
    [InlineData("1d", 86400)]
    [InlineData("45", 45 * 60)]              // a bare number is minutes
    [InlineData(" +2h ", 2 * 3600)]
    [InlineData("+ 2h", 2 * 3600)]           // the '+' strip re-trims, so a space after it is fine
    [InlineData("2H", 2 * 3600)]             // case-insensitive
    [InlineData("0m", -1)]                   // a zero delay is not a delay...
    [InlineData("0", -1)]
    [InlineData("-30m", -1)]                 // ...and a negative one is refused outright
    [InlineData("2h30", -1)]                 // trailing bare number is not the grammar
    [InlineData("", -1)]
    [InlineData("   ", -1)]
    [InlineData("tomorrow", -1)]
    [InlineData("next tuesday-ish", -1)]
    public void TryParseStart_TheDurationGrammar_IsExactlyTheForGrammar(string raw, int expectedOffsetSeconds)
    {
        DateTime start = default;
        string err = "";
        var ok = MonkMode.Program.TryParseStart(raw, ArmNow, ref start, ref err);
        if (expectedOffsetSeconds < 0)
        {
            Assert.False(ok);
            Assert.Contains("Could not understand --start", err);
            Assert.Contains("+90m", err);          // the message teaches the accepted forms
            return;
        }
        Assert.True(ok, err);
        Assert.Equal("", err);
        Assert.Equal(ArmNow.AddSeconds(expectedOffsetSeconds), start);
    }

    [Theory]
    [InlineData("2026-08-10 07:00")]
    [InlineData("2026-08-10T07:00")]
    [InlineData("2026-08-10 07:00:00")]
    [InlineData("10 August 2026 07:00")]
    public void TryParseStart_AbsoluteFormsAreAccepted_AndNeverGuessed(string raw)
    {
        DateTime start = default;
        string err = "";
        Assert.True(MonkMode.Program.TryParseStart(raw, ArmNow, ref start, ref err), err);
        Assert.Equal(new DateTime(2026, 8, 10, 7, 0, 0), start);
    }

    [Fact]
    public void TryParseStart_IsTotal_OverAJunkCorpus()
    {
        // The parse runs before ANY side effect, so a throw here is only an ugly exit - but it
        // is an exit the user cannot distinguish from a real failure, and TryParse* functions
        // that throw are how a refusal turns into a crash report.
        var pool = "0123456789dhm+-: /\\.,'\"abcXYZ\té";
        var rng = new Random(20260819);
        for (var i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 14);
            for (var c = 0; c < len; c++) sb.Append(pool[rng.Next(pool.Length)]);
            DateTime start = default;
            string err = "";
            var ok = MonkMode.Program.TryParseStart(sb.ToString(), ArmNow, ref start, ref err);
            Assert.Equal(ok, err.Length == 0);          // exactly one of a value or a message
        }
    }
}

// =====================================================================================
// 9. TOP RISK 2, live: every compaction, every surviving id, through the real writer
// =====================================================================================

[Collection("CliIniWriters")]
public class SlotCompactionRaceTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
            if (File.Exists(p)) File.Delete(p);
    }

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "a2race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Drop(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AfterEveryPossibleCompaction_EveryWriteStillLandsOnItsOwnBlock(int retirePosition)
    {
        // TOP RISK 2, exhaustively: four blocks, retire each position in turn, then prove the
        // per-slot writer still routes by ID and not by the position it last saw.
        //
        // The failure this forbids is the mis-adjudicated lift: a compaction moves slot 4 down
        // to position 3, a writer holding "position 3" stamps a CoolOffUntil or a
        // PartnerUnlockedAt there, and a block the user never asked to end acquires an exit.
        // Asserted positively (each survivor's write lands on its OWN section, checked by a
        // value unique to it) and negatively (the retired id writes nowhere at all).
        Wipe();
        var dir = TempDir();
        try
        {
            var armed = new List<MonkMode.Blocker.ArmResult>();
            foreach (var site in new[] { "a.com", "b.com", "c.com", "d.com" })
            {
                var r = MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false);
                Assert.True(r.Ok);
                armed.Add(r);
            }
            var svc = TestSvc.New();
            var retired = armed[retirePosition - 1];
            var survivors = armed.Where(a => a.Id != retired.Id).ToList();

            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(dir, "monkmode_hosts.block"),
                                         Path.Combine(dir, "hosts"),
                                         retired.Id.ToString(CultureInfo.InvariantCulture)));

            var after = Reload();
            Assert.Equal(3, monkmode.ConfigIntegrity.ParseSlotCount(after.GetKeyValue("Slots", "SlotCount")));

            // The retired id is gone from every position, and a write against it does nothing.
            var retiredId = retired.Id.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(0, monkmode.Service1.FindSlotPositionById(after, retiredId));
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            Assert.False(svc.PersistSlotFieldAt(MonkMode.Blocker.IniPath(), retiredId, "PartnerUnlockedAt", "2026-08-12 12:00:00", false));
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));

            // Each survivor slid to its new position keeping its own id, and a write addressed
            // to that id lands on that id's section and on no other.
            for (var i = 0; i < survivors.Count; i++)
            {
                var id = survivors[i].Id.ToString(CultureInfo.InvariantCulture);
                Assert.Equal(i + 1, monkmode.Service1.FindSlotPositionById(Reload(), id));
                var stamp = "2026-08-1" + (i + 1) + " 0" + (i + 1) + ":00:00";
                Assert.True(svc.PersistSlotFieldAt(MonkMode.Blocker.IniPath(), id, "PartnerUnlockedAt", stamp, false));
                Assert.Equal(stamp, Reload().GetKeyValue("Slot" + (i + 1), "PartnerUnlockedAt"));
            }
            // ...and every stamp is still where it was put - no write overwrote a neighbour's.
            var final = Reload();
            for (var i = 0; i < survivors.Count; i++)
                Assert.Equal("2026-08-1" + (i + 1) + " 0" + (i + 1) + ":00:00",
                             final.GetKeyValue("Slot" + (i + 1), "PartnerUnlockedAt"));
        }
        finally { Wipe(); Drop(dir); }
    }
}
