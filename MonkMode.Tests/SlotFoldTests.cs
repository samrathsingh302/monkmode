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

// MonkMode.Tests - v1.1 S3a: the SLOT folds, the three unions, the per-slot writer and the
// P37 hosts-snapshot reconciliation.
//
// WHY THIS SLICE EXISTS: until S3a the tick decided everything off the v9 single-block
// mirror keys - [Time] Until, [User] CustomSites, [Process] List/AllSession - and those keys
// sit OUTSIDE the v10 MAC-covered canonical. A raw edit to any of them therefore left
// macValid TRUE: back-dating [Time] Until tore the whole block down with no tamper evidence.
// The folds and unions pinned here read the SLOTS instead, every field of which is inside
// the canonical.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - macValid=False => HELD, checked BEFORE the loop. With an empty slot list a plain
//     "does any slot hold?" fold returns False, so a frozen config with unreadable slots
//     would stand every self-heal down - the exact fail-OPEN the MAC gate exists to stop.
//   - the union is WIDEN-ONLY and deduped: a domain named by two slots blocks once, and a
//     site is dropped from the union only when its slot genuinely stops enforcing.
//   - SlotHeld vs SlotEnforcesNow: a PENDING slot keeps the MACHINERY up (so the guardian,
//     the SafeBoot keys and the ACE are already asserted when its start arrives) but
//     contributes NO sites yet (blocking before --start would break the feature).
//   - P36's Id re-locate (TOP RISK 2): positions are not stable across a compaction, so a
//     writer that trusted a captured position could write into a DIFFERENT block's section.
//     Every per-slot write re-locates by Id and writes NOTHING when the id is gone.
//   - P37: the snapshot is reconciled to config truth, but NEVER touched under a frozen
//     config and NEVER blanked when nothing is enforcing (blanking it is a torn-down block).
//
// Fences honoured: the pure folds touch nothing at all. The writer/reconciler tests use the
// test-bin ini (wiped in finally) and GUID temp paths under the test bin directory - never
// the real hosts file, the registry, the SCM, Program Files or any deployed config. Nothing
// is ever armed.

using System.Globalization;

namespace MonkMode.Tests;

public class SlotFoldTests
{
    private static readonly DateTime AsOf = new(2026, 8, 12, 12, 0, 0);
    private const long Grace = 5;
    private const string Hw = "2026-08-12 12:00:00";
    private const string Future = "2026-08-12 18:00:00";
    private const string Past = "2026-08-12 06:00:00";

    private static monkmode.Service1.SlotState Slot(string id = "1", string until = "", string startAt = "",
                                                    string scheduleActiveUntil = "", string scheduleSpec = "",
                                                    string allSession = "",
                                                    string[]? sites = null, string[]? apps = null, string[]? urls = null)
        => new()
        {
            Id = id,
            UntilText = until,
            StartAt = startAt,
            ScheduleActiveUntil = scheduleActiveUntil,
            ScheduleSpec = scheduleSpec,
            AllSession = allSession,
            Sites = new List<string>(sites ?? System.Array.Empty<string>()),
            Apps = new List<string>(apps ?? System.Array.Empty<string>()),
            UrlPatterns = new List<string>(urls ?? System.Array.Empty<string>()),
        };

    // The four P16 slot states, as the tick derives them (state is DERIVED, never stored).
    private static monkmode.Service1.SlotState Active(string id = "1", params string[] sites)
        => Slot(id, until: Future, sites: sites);
    private static monkmode.Service1.SlotState Expired(string id = "2", params string[] sites)
        => Slot(id, until: Past, sites: sites);
    private static monkmode.Service1.SlotState Pending(string id = "3", params string[] sites)
        => Slot(id, startAt: Future, sites: sites);
    private static monkmode.Service1.SlotState ScheduleOpen(string id = "4", params string[] sites)
        => Slot(id, until: monkmode.Service1.ScheduleOnlyExpiredUntil, scheduleActiveUntil: Future,
                scheduleSpec: "v2;1234567:0900-1700;sites=x.com;apps=", sites: sites);
    private static monkmode.Service1.SlotState ScheduleIdle(string id = "5", params string[] sites)
        => Slot(id, until: monkmode.Service1.ScheduleOnlyExpiredUntil,
                scheduleSpec: "v2;1234567:0900-1700;sites=x.com;apps=", sites: sites);

    private static List<monkmode.Service1.SlotState> L(params monkmode.Service1.SlotState[] s) => new(s);

    private static bool Held(List<monkmode.Service1.SlotState>? slots, bool macValid = true)
        => monkmode.Service1.AnyBlockHeld(slots, AsOf, Grace, macValid, Hw);
    private static bool One(monkmode.Service1.SlotState? s, bool macValid = true)
        => monkmode.Service1.SlotHeld(s, AsOf, Grace, macValid, Hw);
    private static bool Enforcing(monkmode.Service1.SlotState? s, bool macValid = true)
        => monkmode.Service1.SlotEnforcesNow(s, AsOf, Grace, macValid, Hw);
    private static List<string> Sites(List<monkmode.Service1.SlotState>? slots, bool macValid = true)
        => monkmode.Service1.UnionSlotSites(slots, AsOf, Grace, macValid, Hw);

    // ---- 1. SlotExpired: fail-closed on every axis, exactly like BlockHasExpired ----

    [Fact]
    public void SlotExpired_OnlyAGenuinelyPastRecordedEndCounts()
    {
        Assert.True(monkmode.Service1.SlotExpired(Slot(until: Past), AsOf, Grace));
        Assert.False(monkmode.Service1.SlotExpired(Slot(until: Future), AsOf, Grace));
        // No recorded end (PENDING, or a slot mid-arm) is NOT "over" - reading it as expired
        // would let a slot that never started be treated as finished.
        Assert.False(monkmode.Service1.SlotExpired(Slot(), AsOf, Grace));
        // An unparseable end is NOT expired (the B4/B7 rule: a corrupted value never lifts).
        Assert.False(monkmode.Service1.SlotExpired(Slot(until: "not-a-date"), AsOf, Grace));
        Assert.False(monkmode.Service1.SlotExpired(null, AsOf, Grace));
    }

    // ---- 2. the OR-fold truth table ----

    [Fact]
    public void AnyBlockHeld_MacInvalid_IsAlwaysHeld_EvenWithNoSlotsAtAll()
    {
        // THE pin of this file. macValid=False is answered BEFORE the loop, so the empty and
        // null cases - a frozen config whose slots could not be read - hold rather than
        // standing every self-heal down.
        Assert.True(Held(L(), macValid: false));
        Assert.True(Held(null, macValid: false));
        Assert.True(Held(L(Expired()), macValid: false));
        Assert.True(One(null, macValid: false));
        Assert.True(One(Expired(), macValid: false));
    }

    [Theory]
    [InlineData(false)]   // nothing armed
    public void AnyBlockHeld_MacValid_EmptyOrNull_IsNotHeld(bool expected)
    {
        Assert.Equal(expected, Held(L()));
        Assert.Equal(expected, Held(null));
    }

    [Fact]
    public void AnyBlockHeld_OrFoldsOverTheSlots()
    {
        Assert.False(Held(L(Expired())));                       // the only slot is over
        Assert.True(Held(L(Active())));                         // ACTIVE
        Assert.True(Held(L(Expired(), Active())));              // one live slot holds the machine
        Assert.True(Held(L(Active(), Expired())));              // order-independent
        Assert.True(Held(L(ScheduleOpen())));                   // SD1: an open window holds
        Assert.False(Held(L(ScheduleIdle())));                  // between windows, sentinel Until
        Assert.True(Held(L(Pending())));                        // PENDING keeps the machinery up
        Assert.True(Held(L(Slot(until: "garbled"))));           // unparseable end => held
    }

    [Fact]
    public void SlotHeld_MatchesTheBlockHeldShape_PerSlot()
    {
        Assert.True(One(Active()));
        Assert.False(One(Expired()));
        Assert.True(One(ScheduleOpen()));
        Assert.True(One(Pending()));
        Assert.False(One(null));                                 // a null slot holds nothing
    }

    // ---- 3. SlotEnforcesNow: the STRICTER union-membership question ----

    [Fact]
    public void SlotEnforcesNow_PendingSlotsTimerHasNotStarted()
    {
        // SlotEnforcesNow answers "is this slot's own timer running", and a --start slot's is
        // not. It is NOT the union-membership test - see SlotContributesLists below, which is.
        Assert.True(One(Pending()));
        Assert.False(Enforcing(Pending()));
        // ...and under a FROZEN config every slot reads as running (over-block, never lose).
        Assert.True(Enforcing(Pending(), macValid: false));
        Assert.True(monkmode.Service1.SlotIsPending(Pending()));
        Assert.False(monkmode.Service1.SlotIsPending(Active()));
        Assert.False(monkmode.Service1.SlotIsPending(Expired()));
        Assert.False(monkmode.Service1.SlotIsPending(null));
    }

    [Fact]
    public void SlotContributesLists_IncludesPending_ButNotExpired()
    {
        // WOULD HAVE CAUGHT THE P1. A pending slot's sites are already in hosts and in
        // monkmode_hosts.block (the CLI wrote them at arm), and NOTHING turns PENDING into
        // ACTIVE until S3b's activation stamper. Excluding pending here makes the P37
        // reconciler strip those entries out of the heal source permanently.
        Assert.True(monkmode.Service1.SlotContributesLists(Pending(), AsOf, Grace, true, Hw));
        Assert.True(monkmode.Service1.SlotContributesLists(Active(), AsOf, Grace, true, Hw));
        Assert.True(monkmode.Service1.SlotContributesLists(ScheduleOpen(), AsOf, Grace, true, Hw));
        // An expired slot genuinely stops contributing - that is the SHRINK half of P37 and
        // the whole point of per-slot ends (slot 1 must stop blocking when slot 1 ends).
        Assert.False(monkmode.Service1.SlotContributesLists(Expired(), AsOf, Grace, true, Hw));
        Assert.False(monkmode.Service1.SlotContributesLists(ScheduleIdle(), AsOf, Grace, true, Hw));
        Assert.False(monkmode.Service1.SlotContributesLists(null, AsOf, Grace, true, Hw));
    }

    [Fact]
    public void SlotEnforcesNow_ActiveAndOpenWindowsEnforce_ExpiredAndIdleDoNot()
    {
        Assert.True(Enforcing(Active()));
        Assert.False(Enforcing(Expired()));
        Assert.True(Enforcing(ScheduleOpen()));
        Assert.False(Enforcing(ScheduleIdle()));
        Assert.False(Enforcing(null));
        // Frozen: every slot's lists stay enforced rather than any being dropped.
        Assert.True(Enforcing(Expired(), macValid: false));
    }

    // ---- 4. the three unions ----

    [Fact]
    public void UnionSites_ADomainInTwoSlots_AppearsOnce()
    {
        var union = Sites(L(Active("1", "reddit.com", "x.com"), Active("2", "reddit.com", "y.com")));
        Assert.Equal(new[] { "reddit.com", "x.com", "y.com" }, union.ToArray());
    }

    [Fact]
    public void UnionSites_DedupIsCaseInsensitive_AndFirstOccurrenceOrderIsKept()
    {
        // Hosts names are case-insensitive; a case-only duplicate would just emit a second
        // identical hosts line.
        var union = Sites(L(Active("1", "Reddit.com"), Active("2", "REDDIT.COM", "b.com")));
        Assert.Equal(new[] { "Reddit.com", "b.com" }, union.ToArray());
    }

    [Fact]
    public void UnionSites_TakesOpenAndPendingSlots_AndDropsFinishedOnes()
    {
        var slots = L(Active("1", "live.com"), Expired("2", "over.com"), Pending("3", "later.com"),
                      ScheduleOpen("4", "window.com"), ScheduleIdle("5", "idle.com"));
        // later.com is in because a --start slot's entries are already in hosts and in the
        // snapshot, and nothing would ever put them back if the reconciler removed them.
        Assert.Equal(new[] { "live.com", "later.com", "window.com" }, Sites(slots).ToArray());
        // Frozen => EVERY slot's sites, including the finished ones. A frozen config
        // over-blocks; it never loses entries.
        Assert.Equal(new[] { "live.com", "over.com", "later.com", "window.com", "idle.com" },
                     Sites(slots, macValid: false).ToArray());
    }

    [Fact]
    public void UnionApps_AndUrlPatterns_FoldTheSameWay()
    {
        var slots = L(Slot("1", until: Future, apps: new[] { "chrome.exe" }, urls: new[] { "*/watch*" }),
                      Slot("2", until: Future, apps: new[] { "chrome.exe", "discord.exe" }, urls: new[] { "*reddit*" }),
                      Slot("3", until: Past, apps: new[] { "never.exe" }, urls: new[] { "*never*" }));
        Assert.Equal(new[] { "chrome.exe", "discord.exe" },
                     monkmode.Service1.UnionSlotApps(slots, AsOf, Grace, true, Hw).ToArray());
        Assert.Equal(new[] { "*/watch*", "*reddit*" },
                     monkmode.Service1.UnionSlotUrlPatterns(slots, AsOf, Grace, true, Hw).ToArray());
    }

    [Fact]
    public void UnionsOfNothing_AreEmpty_SoTheTickIsByteIdenticalToPreSlotBehaviour()
    {
        // With no slots the tick's enforcedSites/enforcedApps are empty, which is what makes
        // EffectiveHostsBlock return the snapshot verbatim and EffectiveKillList return the
        // v9 list verbatim - the "this slice can only ever ADD" guarantee.
        Assert.Empty(Sites(null));
        Assert.Empty(Sites(L()));
        Assert.Empty(monkmode.Service1.UnionSlotApps(L(Expired()), AsOf, Grace, true, Hw));
    }

    [Fact]
    public void AnySlotAllSessionKill_WidensOnly_FromEnforcingSlots()
    {
        Assert.True(monkmode.Service1.AnySlotAllSessionKill(L(Slot("1", until: Future, allSession: "yes")), AsOf, Grace, true, Hw));
        Assert.True(monkmode.Service1.AnySlotAllSessionKill(L(Slot("1", until: Future, allSession: "YES")), AsOf, Grace, true, Hw));
        Assert.False(monkmode.Service1.AnySlotAllSessionKill(L(Slot("1", until: Future, allSession: "")), AsOf, Grace, true, Hw));
        // An expired slot no longer widens the kill scope.
        Assert.False(monkmode.Service1.AnySlotAllSessionKill(L(Slot("1", until: Past, allSession: "yes")), AsOf, Grace, true, Hw));
        Assert.False(monkmode.Service1.AnySlotAllSessionKill(null, AsOf, Grace, true, Hw));
    }

    // ---- 5. the stored list encodings ----

    [Theory]
    [InlineData("reddit.com;x.com;", ';', new[] { "reddit.com", "x.com" })]   // PackList's trailing ';'
    [InlineData("chrome.exe;", ';', new[] { "chrome.exe" })]
    [InlineData(" a.com ; b.com ", ';', new[] { "a.com", "b.com" })]          // trimmed
    [InlineData(";;", ';', new string[0])]                                     // empties dropped
    [InlineData("", ';', new string[0])]
    [InlineData("*/watch*|*reddit*", '|', new[] { "*/watch*", "*reddit*" })]  // P55 url packing
    public void SplitPackedList_MatchesWhatTheCliPacked(string packed, char sep, string[] expected)
    {
        Assert.Equal(expected, monkmode.Service1.SplitPackedList(packed, sep).ToArray());
    }
}

// ---- 6. P36: locating a slot by ID, never by position (TOP RISK 2) ----

public class SlotPositionLookupTests
{
    private static monkmode.IniFile Ini(int slotCount, params string[] idsByPosition)
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
    public void FindSlotPositionById_FollowsTheId_NotTheOrdinal()
    {
        // After a retire + compaction the ids no longer match their positions. A writer that
        // trusted the position it read BEFORE reloading would update another block's field -
        // the mis-adjudicated-lift class.
        var ini = Ini(3, "7", "3", "11");
        Assert.Equal(1, monkmode.Service1.FindSlotPositionById(ini, "7"));
        Assert.Equal(2, monkmode.Service1.FindSlotPositionById(ini, "3"));
        Assert.Equal(3, monkmode.Service1.FindSlotPositionById(ini, "11"));
    }

    [Fact]
    public void FindSlotPositionById_MissingOrEmptyOrNull_IsNotFound()
    {
        var ini = Ini(2, "1", "2");
        Assert.Equal(0, monkmode.Service1.FindSlotPositionById(ini, "99"));
        Assert.Equal(0, monkmode.Service1.FindSlotPositionById(ini, ""));
        Assert.Equal(0, monkmode.Service1.FindSlotPositionById(ini, null));
        Assert.Equal(0, monkmode.Service1.FindSlotPositionById(null, "1"));
    }

    [Fact]
    public void FindSlotPositionById_NeverScansBeyondTheClampedSlotCount()
    {
        // A stale [Slot3] section left behind by a compaction is invisible, exactly as it is
        // to the canonical - otherwise a write could land in a section no reader consumes.
        var ini = Ini(2, "1", "2", "3");
        Assert.Equal(0, monkmode.Service1.FindSlotPositionById(ini, "3"));
        // ...and a garbage/forged count clamps to 0 rather than widening the scan.
        var forged = Ini(2, "1", "2");
        forged.SetKeyValue("Slots", "SlotCount", "banana");
        Assert.Equal(0, monkmode.Service1.FindSlotPositionById(forged, "1"));
    }
}

// ---- 7. P36: the per-slot writer, end to end through the REAL config ----

[Collection("CliIniWriters")]
public class PersistSlotFieldTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static void ArmTwoSlots()
    {
        Assert.True(MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, new[] { "chrome.exe" }, "", null, Ends, false).Ok);
        Assert.True(MonkMode.Blocker.ArmSlot(new[] { "x.com" }, System.Array.Empty<string>(), "", null, Ends, false).Ok);
    }

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    [Fact]
    public void PersistSlotField_IdMovedByCompaction_WritesNothing()
    {
        Wipe();
        try
        {
            ArmTwoSlots();
            var path = MonkMode.Blocker.IniPath();
            var before = File.ReadAllBytes(path);

            // The service holds an id that is no longer at ANY position (retired, or moved
            // out from under this tick). The write must be refused outright: writing "at the
            // position we last saw it" would set another slot's field.
            var svc = new monkmode.Service1();
            Assert.False(svc.PersistSlotFieldAt(path, "99", "CoolOffUntil", "2027-01-01 00:00:00", true));
            Assert.Equal(before, File.ReadAllBytes(path));      // not one byte touched
        }
        finally { Wipe(); }
    }

    [Fact]
    public void PersistSlotField_WritesAtTheLocatedPosition_AndLeavesTheMacValid()
    {
        Wipe();
        try
        {
            ArmTwoSlots();
            var path = MonkMode.Blocker.IniPath();
            var svc = new monkmode.Service1();

            // Encrypted field on slot 2 (id 2 lives at position 2).
            Assert.True(svc.PersistSlotFieldAt(path, "2", "CoolOffUntil", "2027-01-01 00:00:00", true));
            var ini = Reload();
            Assert.NotEqual("", ini.GetKeyValue("Slot2", "CoolOffUntil"));
            Assert.NotEqual("2027-01-01 00:00:00", ini.GetKeyValue("Slot2", "CoolOffUntil"));   // stored ENCRYPTED
            Assert.Equal("", ini.GetKeyValue("Slot1", "CoolOffUntil"));                          // the neighbour is untouched

            // A SECOND write proves the first left the config MAC-valid: PersistSlotFieldAt
            // re-validates the MAC before it does anything, so a broken re-stamp would make
            // this return False. (This is also the plaintext path.)
            Assert.True(svc.PersistSlotFieldAt(path, "1", "PartnerUnlockedAt", "2026-08-12 12:00:00", false));
            Assert.Equal("2026-08-12 12:00:00", Reload().GetKeyValue("Slot1", "PartnerUnlockedAt"));

            // ...and all four assemblies still derive the identical canonical afterwards.
            var cliIni = new MonkMode.IniFile(); cliIni.Load(path);
            var srvIni = new monkmode.IniFile(); srvIni.Load(path);
            var guardIni = new mm_guard.IniFile(); guardIni.Load(path);
            var notifyIni = new mm_notify.IniFile(); notifyIni.Load(path);
            var canonical = MonkMode.Blocker.CanonicalFromIni(cliIni);
            Assert.Equal(canonical, new monkmode.Service1().CanonicalFromIni(srvIni));
            Assert.Equal(canonical, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(canonical, new mm_notify.Form1().CanonicalFromIni(notifyIni));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void PersistSlotField_MacInvalidConfig_IsFrozen_NotReStamped()
    {
        Wipe();
        try
        {
            ArmTwoSlots();
            var path = MonkMode.Blocker.IniPath();
            // Raw-edit a MAC-covered slot field: the config is now tampered. Re-stamping over
            // it is the B7 fail-open bug, so the write must be refused and nothing re-blessed.
            var ini = Reload();
            ini.SetKeyValue("Slot1", "Sites", "harmless.example;");
            ini.Save(path);
            var tampered = File.ReadAllBytes(path);

            Assert.False(new monkmode.Service1().PersistSlotFieldAt(path, "1", "PartnerUnlockedAt", "x", false));
            Assert.Equal(tampered, File.ReadAllBytes(path));
        }
        finally { Wipe(); }
    }

    [Fact]
    public void AnySlotArmed_IsTheGateThatStopsAddSilentlyNoOpping()
    {
        // `add` appends to the v9 [User] CustomSites + the hosts snapshot and to NO slot,
        // while P37 reconciles that snapshot to the SLOTS every tick - so an added site would
        // be stripped back out within ~10s. The CLI refuses instead, and the refusal is
        // fail-SAFE (an unreadable config reads as ARMED).
        Wipe();
        try
        {
            Assert.False(MonkMode.Blocker.AnySlotArmed());     // no config at all
            ArmTwoSlots();
            Assert.True(MonkMode.Blocker.AnySlotArmed());
        }
        finally { Wipe(); }
    }

    [Fact]
    public void LoadSlots_ReadsTheArmedSlots_DecryptedAndSplit()
    {
        Wipe();
        try
        {
            ArmTwoSlots();
            var slots = new monkmode.Service1().LoadSlots(Reload());
            Assert.Equal(2, slots.Count);
            Assert.Equal("1", slots[0].Id);
            Assert.Equal(new[] { "reddit.com" }, slots[0].Sites.ToArray());
            Assert.Equal(new[] { "chrome.exe" }, slots[0].Apps.ToArray());
            Assert.Equal("2", slots[1].Id);
            Assert.Equal(new[] { "x.com" }, slots[1].Sites.ToArray());
            Assert.Empty(slots[1].Apps);
            // Until is DECRYPTED (the folds compare it as a datetime, never as ciphertext).
            Assert.Equal(Ends.ToString(new CultureInfo("en-CA")), slots[0].UntilText);
            // ...and both slots are ACTIVE and enforcing at a moment before their end.
            Assert.True(monkmode.Service1.AnyBlockHeld(slots, new DateTime(2026, 8, 12, 12, 0, 0), 5, true, "2026-08-12 12:00:00"));
            Assert.Equal(new[] { "reddit.com", "x.com" },
                         monkmode.Service1.UnionSlotSites(slots, new DateTime(2026, 8, 12, 12, 0, 0), 5, true, "2026-08-12 12:00:00").ToArray());
        }
        finally { Wipe(); }
    }
}

// ---- 8. P37: hosts-snapshot reconciliation ----

public class HostsSnapshotReconcileTests
{
    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "recon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Block(params string[] sites)
        => monkmode.Service1.HostsMarker + "\r\n" + monkmode.Service1.BuildHostsEntries(sites);

    [Fact]
    public void EntrySet_IgnoresTheMarkerAndBlankLines_SoFormattingIsNotADifference()
    {
        var a = monkmode.Service1.HostsBlockEntrySet(Block("a.com"));
        var b = monkmode.Service1.HostsBlockEntrySet(Block("a.com") + "\r\n\r\n");
        Assert.True(a.SetEquals(b));
        Assert.False(monkmode.Service1.SnapshotNeedsReconcile(Block("a.com"), Block("a.com") + "\r\n"));
        Assert.DoesNotContain(monkmode.Service1.HostsMarker, a);
        // A genuinely different set IS a difference, in both directions.
        Assert.True(monkmode.Service1.SnapshotNeedsReconcile(Block("a.com"), Block("a.com", "b.com")));
        Assert.True(monkmode.Service1.SnapshotNeedsReconcile(Block("a.com", "b.com"), Block("a.com")));
        Assert.True(monkmode.Service1.SnapshotNeedsReconcile("", Block("a.com")));
    }

    [Fact]
    public void Reconcile_GrowsAndShrinksToConfigTruth()
    {
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            // GROW: a second slot armed while the snapshot write was lost.
            File.WriteAllText(snap, Block("a.com"));
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string> { "a.com", "b.com" });
            Assert.Equal(Block("a.com", "b.com"), File.ReadAllText(snap));

            // SHRINK: a slot retired, so its entries must leave the repair source too -
            // otherwise the B2 self-heal would keep re-blocking a site nothing enforces.
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string> { "a.com" });
            Assert.Equal(Block("a.com"), File.ReadAllText(snap));

            // CREATE: a config with slots but no snapshot at all (the crash-between-writes
            // case, or a hand-deleted snapshot). NOT "a PENDING slot that just activated" -
            // activation does not exist until S3b, which is exactly the false premise the
            // S3a P1 hid behind; pending slots contribute to truth from ARM, via
            // SlotContributesLists, precisely so nothing can ever strip them out.
            File.Delete(snap);
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string> { "a.com" });
            Assert.Equal(Block("a.com"), File.ReadAllText(snap));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_NoChurnWhenTheEntrySetsAlreadyAgree()
    {
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            // Same entries, different formatting: the reconciler compares SETS, so it must
            // not rewrite the file every 10s tick over a trailing newline.
            var onDisk = Block("a.com", "b.com") + "\r\n";
            File.WriteAllText(snap, onDisk);
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string> { "b.com", "a.com" });
            Assert.Equal(onDisk, File.ReadAllText(snap));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_FrozenConfig_NeverTouchesTheSnapshot()
    {
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            var onDisk = Block("a.com", "b.com");
            File.WriteAllText(snap, onDisk);
            // macValid=False: the snapshot is the MAC-INDEPENDENT backstop the self-heal and
            // the crash re-block read. A frozen config must keep exactly the one it has -
            // reconciling from a config we cannot trust is how a tamper would shrink it.
            monkmode.Service1.ReconcileHostsSnapshot(snap, false, new List<string> { "a.com" });
            Assert.Equal(onDisk, File.ReadAllText(snap));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_EmptyTruth_NeverBlanksTheSnapshot()
    {
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            var onDisk = Block("a.com");
            File.WriteAllText(snap, onDisk);
            // R1: "nothing is enforcing right now" is exactly the state in which rewriting
            // from truth would BLANK the repair source - i.e. tear the block down. Teardown
            // is stopMe's job, never this reconciler's.
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string>());
            Assert.Equal(onDisk, File.ReadAllText(snap));
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, null);
            Assert.Equal(onDisk, File.ReadAllText(snap));
            // ...and a list of sites that all normalise away is the same case.
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string> { "   " });
            Assert.Equal(onDisk, File.ReadAllText(snap));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- the PENDING-slot heal path (the S3a P1) ----
    //
    // `block --sites a.com --for 4h` then `block --sites b.com --start +30m --for 2h`. The CLI
    // writes BOTH sets into hosts and into monkmode_hosts.block at arm time. If the reconciler
    // reasoned over ENFORCING slots only, b.com would be stripped from the snapshot within one
    // tick - and nothing in the tree could put it back, because the P29 activation stamper
    // that turns PENDING into ACTIVE does not exist until S3b. The scheduled block would then
    // never block anything, and any tamper-repair or reboot would rebuild hosts without it.

    private static List<monkmode.Service1.SlotState> ActiveAndPending()
        => new()
        {
            new monkmode.Service1.SlotState { Id = "1", UntilText = "2026-08-12 18:00:00", Sites = new List<string> { "a.com" } },
            new monkmode.Service1.SlotState { Id = "2", StartAt = "2026-08-12 12:30:00", UntilText = "", Sites = new List<string> { "b.com" } },
        };

    private static List<string> TruthFor(List<monkmode.Service1.SlotState> slots)
        => monkmode.Service1.UnionSlotSites(slots, new DateTime(2026, 8, 12, 12, 0, 0), 5, true, "2026-08-12 12:00:00");

    [Fact]
    public void Reconcile_APendingSlotsSitesSurvive_NothingCouldEverReAddThem()
    {
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            var armed = Block("a.com", "b.com");        // what the two arms wrote
            File.WriteAllText(snap, armed);

            monkmode.Service1.ReconcileHostsSnapshot(snap, true, TruthFor(ActiveAndPending()));

            // The heal source is untouched: the scheduled block's sites are still in it.
            Assert.Equal(armed, File.ReadAllText(snap));
            Assert.Contains("127.0.0.1 b.com", File.ReadAllText(snap));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void HealPath_RebuildsHostsIncludingThePendingSlotsSites()
    {
        // Where the user actually loses the block: an evader deletes the marker block and the
        // B2 self-heal rebuilds hosts from snapshot UNION enforcedSites. Both halves must
        // still name b.com, or the scheduled block silently never happens.
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            File.WriteAllText(snap, Block("a.com", "b.com"));
            var truth = TruthFor(ActiveAndPending());
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, truth);

            var snapshotBlock = File.ReadAllText(snap);
            var expected = monkmode.Service1.EffectiveHostsBlock(snapshotBlock, truth, truth.Count > 0);
            var repaired = monkmode.Service1.RepairHostsBlock("# the user's own entry\r\n", expected);

            Assert.NotNull(repaired);
            Assert.Contains("127.0.0.1 a.com", repaired);
            Assert.Contains("127.0.0.1 b.com", repaired);
            Assert.Contains("# the user's own entry", repaired);   // no data loss, ever
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_WritesTheSameBytesTheCliWould_SoTheSelfHealFindsItIntact()
    {
        var dir = TempDir();
        try
        {
            var snap = Path.Combine(dir, "monkmode_hosts.block");
            monkmode.Service1.ReconcileHostsSnapshot(snap, true, new List<string> { "reddit.com" });
            // Byte-identical to what the CLI's own BuildMonkModeBlock writes for the same
            // sites, so the expiry strip removes it exactly as it would a CLI-armed block's
            // and the B2 repair no-churns once it has been written.
            Assert.Equal(MonkMode.Blocker.BuildMonkModeBlock(new[] { "reddit.com" }), File.ReadAllText(snap));
        }
        finally { Directory.Delete(dir, true); }
    }
}
