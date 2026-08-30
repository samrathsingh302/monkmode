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

// MonkMode.Tests - v1.1 A2: the slot-addressed TRIGGER channel under junk.
//
// The trigger directory is the one enforcement input any NON-ELEVATED process can write to:
// the files sit beside the exes, the service reads them every tick, and two of the four
// families are exit requests. P40's whole safety argument is that the <id> in the file name is
// a ROUTING HINT WITH ZERO AUTHORITY - it says which slot to look at, never what may happen to
// it - and that everything else about the file (its content, its size, how many there are) can
// only ever cost time, never a lift.
//
// SlotRetireTests and SlotCliTests pin the happy paths and the named junk cases. This file
// attacks the channel with what an adversary would actually drop into that directory:
//
//   - IDS THAT ARE NOT IDS: unicode, 4,000 characters, path fragments, the ids of retired
//     slots, ids that differ from a live one only by case or whitespace. Every one must delete
//     the trigger, write nothing, and NOT freeze (freezing on junk would hand anyone a
//     one-file wedge of the whole machine).
//   - PATH CONTAINMENT, proved by path arithmetic rather than by writing anything: no id, however
//     hostile, can make the Path.Combine the deleters use resolve outside the state directory.
//     The prefix is what anchors it, and that is worth pinning because the deleters are
//     unconditional.
//   - THE CAP UNDER FLOOD: 40 files in one directory must not stall the tick, must be consumed
//     16 at a time in a DETERMINISTIC order, and - the load-bearing half - a deferred EXIT
//     trigger must still be there next tick and must eventually apply. Deferral is fail-closed
//     (the block holds ~10s longer); LOSING the trigger would be an exit the user performed
//     that silently never happened.
//   - OVERSIZE AND EMPTY CONTENT: over TriggerMaxBytes reads as blank, so it is a non-matching
//     attempt, not a memory lever. Blank likewise.
//   - MergeSiteList as a GROWTH-ONLY algebra over generated input: the result can only ever be
//     a superset of what the slot already had, and can never contain a token that would break
//     the packed list (and therefore the canonical, and therefore freeze the block the user
//     was trying to extend).
//
// Fences honoured: every file this touches is either in a GUID temp directory under the test
// bin or the test-bin config itself (wiped in `finally`). No real hosts file, registry key, SCM
// handle, port or deployed state file is read or written, and no Service1 is constructed except
// through TestSvc (whose header explains the live-tick landmine that forced it).

using System.Globalization;
using System.Text;

namespace MonkMode.Tests;

// =====================================================================================
// 1. the pure surface: name parsing, selection, containment, the merge algebra
// =====================================================================================

public class TriggerJunkTests
{
    // Every prefix the enumeration glob still matches. Ledger 319 left the two CoolOff names
    // in the GLOB (so a stale file from an older dist is found and purged) while removing them
    // from TriggerAddressesAnyFamily - so this list and AddressedPrefixes below are no longer
    // the same set, and that difference is the whole disposal mechanism.
    private static readonly string[] Prefixes =
    {
        monkmode.Service1.CoolOffRequestPrefix,
        monkmode.Service1.CoolOffCancelPrefix,
        monkmode.Service1.PartnerCodePrefix,
        monkmode.Service1.AddRequestPrefix,
    };

    // The prefixes that still ADDRESS a family, i.e. that a poller will read.
    private static readonly string[] AddressedPrefixes =
    {
        monkmode.Service1.PartnerCodePrefix,
        monkmode.Service1.AddRequestPrefix,
    };

    [Fact]
    public void TheCoolOffPrefixes_AreEnumeratedButAddressNoFamily()
    {
        // THE ledger 319 disposal contract, in one place. A monkmode_cooloff.request.<id> is
        // still found by the glob - so it cannot sit on disk for ever - and answers "no family"
        // - so no poller reads it and PurgeUnaddressedTriggers deletes it. Putting these
        // prefixes back into TriggerAddressesAnyFamily would resurrect nothing (there is no
        // cooling-off reader) but WOULD strand the files, permanently occupying the P41 budget.
        Assert.False(monkmode.Service1.TriggerAddressesAnyFamily(monkmode.Service1.CoolOffRequestPrefix + "1"));
        Assert.False(monkmode.Service1.TriggerAddressesAnyFamily(monkmode.Service1.CoolOffCancelPrefix + "1"));
        Assert.True(monkmode.Service1.TriggerAddressesAnyFamily(monkmode.Service1.PartnerCodePrefix + "1"));
        Assert.True(monkmode.Service1.TriggerAddressesAnyFamily(monkmode.Service1.AddRequestPrefix + "1"));
    }

    [Fact]
    public void TriggerIdFromName_ReadsOnlyItsOwnFamily_AndTakesTheRemainderVerbatim()
    {
        foreach (var prefix in Prefixes)
        {
            // The remainder is taken whole and only TRIMMED - it is compared to a stored Id,
            // never parsed, so no numeric interpretation can widen it into another slot's.
            Assert.Equal("7", monkmode.Service1.TriggerIdFromName(prefix + "7", prefix));
            Assert.Equal("7", monkmode.Service1.TriggerIdFromName(prefix + "  7  ", prefix));
            Assert.Equal("07", monkmode.Service1.TriggerIdFromName(prefix + "07", prefix));
            Assert.Equal("7.8", monkmode.Service1.TriggerIdFromName(prefix + "7.8", prefix));
            Assert.Equal("\u4e2d", monkmode.Service1.TriggerIdFromName(prefix + "\u4e2d", prefix));
            // Windows file names are case-insensitive, so the prefix match must be too, or an
            // exit request typed by a case-folding tool would be silently ignored for ever.
            Assert.Equal("7", monkmode.Service1.TriggerIdFromName(prefix.ToUpperInvariant() + "7", prefix));
            // A bare prefix carries no id at all - that is what PurgeUnaddressedTriggers is for.
            Assert.Equal("", monkmode.Service1.TriggerIdFromName(prefix, prefix));
            Assert.Equal("", monkmode.Service1.TriggerIdFromName(prefix + "   ", prefix));
            // ...and it never reads another family's file.
            foreach (var other in Prefixes)
            {
                if (other == prefix) continue;
                Assert.Equal("", monkmode.Service1.TriggerIdFromName(other + "7", prefix));
            }
            Assert.Equal("", monkmode.Service1.TriggerIdFromName("monkmode_settings.ini", prefix));
            Assert.Equal("", monkmode.Service1.TriggerIdFromName("x" + prefix + "7", prefix));   // prefix must LEAD
            Assert.Equal("", monkmode.Service1.TriggerIdFromName(null, prefix));
            Assert.Equal("", monkmode.Service1.TriggerIdFromName(prefix + "7", null));
        }
    }

    [Fact]
    public void TriggerIdFromName_IsTotal_OverAGeneratedNameCorpus()
    {
        // It runs inside the tick, so a throw here stops enforcement outright.
        var pool = "monkde_cflw.aiqes0123456789 \t\\/:*?\"<>|\u4e2d\u0000";
        var rng = new Random(20260819);
        for (var i = 0; i < 4000; i++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 45);
            for (var c = 0; c < len; c++) sb.Append(pool[rng.Next(pool.Length)]);
            var name = rng.Next(2) == 0 ? sb.ToString() : Prefixes[rng.Next(4)] + sb;
            foreach (var prefix in Prefixes)
            {
                var id = monkmode.Service1.TriggerIdFromName(name, prefix);
                Assert.NotNull(id);
                Assert.Equal(id.Trim(), id);
            }
            // AddressedPrefixes, not Prefixes: since ledger 319 a cooling-off name carries a
            // parseable id but addresses no family (see TheCoolOffPrefixes_... above).
            Assert.Equal(AddressedPrefixes.Any(p => monkmode.Service1.TriggerIdFromName(name, p) != ""),
                         monkmode.Service1.TriggerAddressesAnyFamily(name));
        }
    }

    [Fact]
    public void NoIdAFileNameCanCarry_MakesADeletePathLeaveTheStateDirectory()
    {
        // The deleters do Path.Combine(stateDir, prefix + id) with no containment check, so
        // the containment has to come from somewhere else. It comes from the SOURCE of the id:
        // every name reaching them is a Path.GetFileName leaf, and a Windows file name cannot
        // contain a path separator. Pinned here as the two halves of that chain - this test
        // creates and deletes NOTHING, it is path arithmetic only.
        //
        // (The chain is worth pinning rather than assuming: with a separator in the id the
        // combine DOES escape - the prefix's trailing dot merges into a leading "..", leaving
        // real parent segments behind it. Recorded as finding F61, unreachable today because
        // the only caller passes enumerated leaf names.)
        var stateDir = Path.Combine(AppContext.BaseDirectory, "trigger-containment");
        var root = Path.GetFullPath(stateDir) + Path.DirectorySeparatorChar;
        var hostileIds = new[]
        {
            "..", "...", "....", "~", "%SystemRoot%", "$HOME", ".", " .. ", "..;..",
            new string('a', 60), "con", "nul", "prn", "aux", "lpt1", "\u4e2d", "*", "?",
        };
        foreach (var prefix in Prefixes)
        foreach (var id in hostileIds)
        {
            var combined = Path.GetFullPath(Path.Combine(stateDir, prefix + id));
            Assert.StartsWith(root, combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoEnumeratedName_CanEverYieldAnIdCarryingAPathSeparator()
    {
        // The other half of the containment chain. Whatever a file is called, the id parsed
        // out of its LEAF name is separator-free, so the combine above can never be handed the
        // shape that escapes.
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' };
        var awkward = new[]
        {
            @"C:\Windows\monkmode_cooloff.request.7", "/tmp/monkmode_partner.code.7",
            @"..\..\monkmode_add.request.7", @"\\server\share\monkmode_cooloff.cancel.7",
            "monkmode_cooloff.request.7",
        };
        foreach (var full in awkward)
        {
            var leaf = Path.GetFileName(full);
            foreach (var prefix in Prefixes)
            {
                var id = monkmode.Service1.TriggerIdFromName(leaf, prefix);
                foreach (var sep in separators) Assert.DoesNotContain(sep, id);
            }
        }
    }

    [Fact]
    public void SelectTriggerFiles_CapsAtTheBudget_Deterministically_WhateverTheDiskOrder()
    {
        // The cap exists so a directory stuffed with files cannot stall the enforcement tick;
        // the ORDINAL sort exists so the same 16 are chosen every tick, which is what lets a
        // starved trigger eventually lead the list instead of being re-shuffled for ever.
        var names = new List<string>();
        for (var i = 0; i < 1000; i++)
            names.Add(monkmode.Service1.CoolOffRequestPrefix + i.ToString("0000", CultureInfo.InvariantCulture));

        var selected = monkmode.Service1.SelectTriggerFiles(names, monkmode.Service1.MaxTriggerFilesPerTick);
        Assert.Equal(monkmode.Service1.MaxTriggerFilesPerTick, selected.Count);

        var shuffled = new List<string>(names);
        var rng = new Random(20260819);
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        Assert.Equal(selected, monkmode.Service1.SelectTriggerFiles(shuffled, monkmode.Service1.MaxTriggerFilesPerTick));

        // The chosen set is the ordinal-least prefix of the whole list, not an arbitrary 16.
        var expected = names.OrderBy(n => n, StringComparer.Ordinal).Take(monkmode.Service1.MaxTriggerFilesPerTick);
        Assert.Equal(expected, selected);
    }

    [Fact]
    public void SelectTriggerFiles_DegenerateBudgets_AreSafe()
    {
        var names = new List<string> { "b", "a", "c" };
        Assert.Empty(monkmode.Service1.SelectTriggerFiles(names, 0));
        Assert.Empty(monkmode.Service1.SelectTriggerFiles(names, -1));
        Assert.Empty(monkmode.Service1.SelectTriggerFiles(names, int.MinValue));
        Assert.Equal(3, monkmode.Service1.SelectTriggerFiles(names, int.MaxValue).Count);
        Assert.Empty(monkmode.Service1.SelectTriggerFiles(null, 16));
        Assert.Empty(monkmode.Service1.SelectTriggerFiles(new List<string>(), 16));
        // The input list is never mutated - it is the caller's, and the sort is on a copy.
        Assert.Equal(new[] { "b", "a", "c" }, names);
    }

    [Fact]
    public void TheBudget_IsTwiceTheSlotCap_SoEverySlotCanHaveARequestAndACancelInFlight()
    {
        Assert.Equal(2 * monkmode.ConfigIntegrity.MaxSlots, monkmode.Service1.MaxTriggerFilesPerTick);
        Assert.Equal(4096, monkmode.Service1.TriggerMaxBytes);
    }

    // ---- MergeSiteList: the growth-only algebra ----

    [Fact]
    public void MergeSiteList_IsGrowthOnly_OverGeneratedRequests()
    {
        // `add` is the one channel that can CHANGE an armed slot, so its whole safety argument
        // is that it can only ever add: a forged, replayed or garbage trigger blocks MORE.
        // Asserted as a set relation over generated input rather than as examples.
        var rng = new Random(20260819);
        // The REQUESTED side is the untrusted one, so it gets the whole junk pool. The
        // EXISTING side is drawn only from tokens the CLI validator would have let through:
        // slot.Sites is read back from the MAC-covered store, so an entry carrying a space or
        // a ';' there is not an input this function is asked to sanitise (it copies existing
        // entries verbatim, by design - dropping one would be an under-block).
        var tokens = new[] { "a.com", "b.com", "A.COM", "", " ", "has space.com", "semi;colon.com",
                             "tab\tsep.com", "\u4e2d.com", new string('x', 300) + ".com", "c.com" };
        var storable = new[] { "a.com", "b.com", "A.COM", "\u4e2d.com", "c.com" };
        for (var i = 0; i < 4000; i++)
        {
            var existing = new List<string>();
            for (var e = rng.Next(0, 4); e > 0; e--) existing.Add(storable[rng.Next(storable.Length)]);
            var requested = new StringBuilder();
            for (var r = rng.Next(0, 5); r > 0; r--)
                requested.Append(tokens[rng.Next(tokens.Length)]).Append(",\n;"[rng.Next(3)]);

            var grown = monkmode.Service1.MergeSiteList(existing, requested.ToString());
            if (grown == "") continue;                      // nothing new: the caller writes nothing

            var after = monkmode.Service1.SplitPackedList(grown, ';');
            // (a) nothing the slot already enforced was dropped.
            foreach (var e in existing.Select(x => x.Trim()).Where(x => x != ""))
                Assert.Contains(e, after, StringComparer.OrdinalIgnoreCase);
            // (b) no entry can break the packed list, and therefore the canonical.
            foreach (var entry in after)
            {
                Assert.DoesNotContain(";", entry, StringComparison.Ordinal);
                Assert.False(entry.Any(char.IsWhiteSpace), $"whitespace survived into '{entry}'");
                Assert.NotEqual("", entry);
            }
            // (c) deduped case-insensitively, and the stored form ends with the pack separator.
            Assert.Equal(after.Count, after.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.EndsWith(";", grown, StringComparison.Ordinal);
            // (d) IDEMPOTENT: re-applying the same request adds nothing, so a crash between the
            //     write and the trigger delete costs exactly nothing.
            Assert.Equal("", monkmode.Service1.MergeSiteList(after, requested.ToString()));
        }
    }

    [Fact]
    public void MergeSiteList_DegenerateInputs_WriteNothing()
    {
        Assert.Equal("", monkmode.Service1.MergeSiteList(null, null));
        Assert.Equal("", monkmode.Service1.MergeSiteList(null, ""));
        Assert.Equal("", monkmode.Service1.MergeSiteList(new List<string> { "a.com" }, null));
        Assert.Equal("", monkmode.Service1.MergeSiteList(new List<string> { "a.com" }, "   \n\n,,,;;;"));
        Assert.Equal("", monkmode.Service1.MergeSiteList(new List<string> { "a.com" }, "A.COM"));
        // A request made ENTIRELY of unstorable tokens is not a partial success - it is nothing.
        Assert.Equal("", monkmode.Service1.MergeSiteList(new List<string> { "a.com" }, "bad site.com\nother\tsite.com"));
    }
}

// =====================================================================================
// 2. the live channel: real files in a temp directory, against the real config writer
// =====================================================================================

[Collection("CliIniWriters")]
public class TriggerJunkLiveTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);
    private const string Hw = "2026-08-12 12:00:00";

    // NEVER construct a Service1 directly (TestServiceFactory.cs explains why: construction
    // alone starts the live 10s enforcement tick on a threadpool thread).
    private static monkmode.Service1 Svc() => TestSvc.New();

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "a2trig-" + Guid.NewGuid().ToString("N"));
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
            if (File.Exists(p)) File.Delete(p);
        foreach (var pattern in new[] { MonkMode.Blocker.CoolOffRequestPrefix + "*",
                                        MonkMode.Blocker.CoolOffCancelPrefix + "*",
                                        MonkMode.Blocker.PartnerCodePrefix + "*" })
            foreach (var f in Directory.GetFiles(MonkMode.Blocker.AppDir(), pattern)) File.Delete(f);
    }

    private static MonkMode.Blocker.ArmResult Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false);

    private static monkmode.IniFile Reload()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    /// <summary>One tick's worth of trigger handling, in the order the service does it:
    /// enumerate ONCE (the budget is shared across the whole channel), purge the unaddressed,
    /// then run the three pollers over that one list.</summary>
    private static List<string> Tick(monkmode.Service1 svc, string dir, bool macValid = true)
    {
        var names = monkmode.Service1.EnumerateTriggerFilesIn(dir);
        // Ledger 319: the purge is now what disposes of a cooling-off trigger too - the two
        // CoolOff prefixes address no family any more, so they are deleted here, unread, and
        // there is no third poller below to run over them.
        monkmode.Service1.PurgeUnaddressedTriggers(dir, names);
        var slots = svc.LoadSlots(Reload());
        svc.ProcessPartnerCodeSignalAt(dir, MonkMode.Blocker.IniPath(), slots, macValid, names);
        svc.ProcessAddRequestsAt(dir, MonkMode.Blocker.IniPath(), slots, macValid, names);
        return names;
    }

    // ---- ids that are not ids ----

    [Fact]
    public void EveryShapeOfJunkId_IsBinnedWithoutTouchingASingleByteOfConfig()
    {
        // P40 in one sweep. None of these may write, and none may FREEZE: a freeze on junk
        // would let anyone wedge the machine by dropping a file into a directory they can
        // write to, which is a far better bypass than any of the ones this product defends.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            var junkIds = new[] { "0", "99", "-1", "1.0", "1x", "x1", "01", "0001", "not-a-number",
                                  "\u4e2d", "%20", "1%2e", new string('9', 50), "true", "null", "NaN" };
            foreach (var id in junkIds)
            {
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + id), "");
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffCancelPrefix + id), "");
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + id), "SOME-CODE1");
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.AddRequestPrefix + id), "evil.com");
            }
            // 64 files against a 16-file budget, so drain it.
            for (var tick = 0; tick < 8 && Directory.GetFiles(dir).Length > 0; tick++) Tick(svc, dir);

            Assert.Empty(Directory.GetFiles(dir));
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            // ...and the armed slot is still exactly as armed: not unlocked, not cooling off,
            // not widened.
            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Slot1", "PartnerUnlockedAt"));
            Assert.Equal("", after.GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.DoesNotContain("evil.com", after.GetKeyValue("Slot1", "Sites"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void ARetiredSlotsId_CarriesNoAuthorityOverTheSurvivor()
    {
        // P17's never-reused ids are what make the routing hint safe. A replayed trigger
        // addressed to a retired block must not become a trigger against whoever now sits at
        // that POSITION - the classic "the id moved under me" bug.
        Wipe();
        var dir = TempDir();
        var work = TempDir();
        try
        {
            var a = Arm("a.com");
            var b = Arm("b.com");
            Assert.True(a.Ok && b.Ok);
            var svc = Svc();
            Assert.True(svc.RetireSlotAt(MonkMode.Blocker.IniPath(),
                                         Path.Combine(work, "monkmode_hosts.block"),
                                         Path.Combine(work, "hosts"),
                                         a.Id.ToString(CultureInfo.InvariantCulture)));
            // b has now COMPACTED DOWN into position 1, keeping its own id.
            var compacted = Reload();
            Assert.Equal(b.Id.ToString(CultureInfo.InvariantCulture), compacted.GetKeyValue("Slot1", "Id"));
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            // Replay every family against the retired id, including a's real partner code.
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + a.Id), "");
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), a.PartnerCode);
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.AddRequestPrefix + a.Id), "evil.com");
            Tick(svc, dir);

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.Empty(Directory.GetFiles(dir));
            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Slot1", "PartnerUnlockedAt"));
            Assert.Equal("", after.GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.DoesNotContain("evil.com", after.GetKeyValue("Slot1", "Sites"));
        }
        finally { Wipe(); Drop(dir); Drop(work); }
    }

    // ---- size and content ----

    [Fact]
    public void AnOversizeTrigger_ReadsAsBlank_SoItIsAnAttemptNotAMemoryLever()
    {
        // TriggerMaxBytes + 1 must not be read at all: an unbounded read is a DoS lever and a
        // 4KB-plus "code" is not a real attempt. Blank => Ignore => delete, no state change,
        // and crucially NO rotation of the real code (spamming misses must not grief-lock the
        // partner's legitimate one).
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            var oversize = new string('A', (int)monkmode.Service1.TriggerMaxBytes + 1);
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), oversize);
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.AddRequestPrefix + a.Id), oversize);
            Tick(svc, dir);

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            Assert.Empty(Directory.GetFiles(dir));

            // The REAL code still works afterwards - the oversize attempt cost nothing.
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), a.PartnerCode);
            Tick(svc, dir);
            Assert.NotEqual("", Reload().GetKeyValue("Slot1", "PartnerUnlockedAt"));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void AnEmptyOrWhitespacePartnerCode_IsIgnored_NotVerified()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            foreach (var body in new[] { "", " ", "\r\n", "\t\t" })
            {
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), body);
                Tick(svc, dir);
                Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
                Assert.Empty(Directory.GetFiles(dir));
            }
        }
        finally { Wipe(); Drop(dir); }
    }

    // FLIPPED BY LEDGER 319. This used to pin that a zero-byte cooling-off request was a valid
    // request (the family was presence-only, and the service computed the deadline itself). A
    // cooling-off request of ANY content is now inert: one full tick reads nothing from it,
    // writes no deadline, leaves the block armed, and deletes the file.
    [Fact]
    public void ACoolOffRequest_IsInert_WhateverItContains()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            File.WriteAllBytes(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + a.Id), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffCancelPrefix + a.Id), "anything at all");
            Tick(svc, dir);

            Assert.Equal("", Reload().GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));   // not one byte written
            Assert.Empty(Directory.GetFiles(dir, "monkmode_cooloff.*"));           // and swept away
        }
        finally { Wipe(); Drop(dir); }
    }

    // ---- the flood ----

    [Fact]
    public void ACapFlood_DefersTheExitTriggerButNeverLosesIt_AndConverges()
    {
        // THE flood pin. 40 junk `add` files sort ORDINALLY BEFORE the exit trigger, so they
        // take the whole 16-file budget and starve it for several ticks. Two things must hold,
        // and they pull in opposite directions:
        //   * the deferral is FAIL-CLOSED - the block simply holds ~10s longer per tick, and
        //     nothing is written early;
        //   * the trigger is NOT consumed while deferred, and once the flood drains it applies
        //     in full. A channel that dropped starved triggers would silently swallow an exit
        //     the user really did request.
        //
        // Ledger 319 changed WHICH trigger this is about. It used to be the cooling-off request
        // (which sorted after "monkmode_add..."); a cooling-off trigger is now purged unread on
        // the first tick that sees it, so the starvable exit is the PARTNER CODE - and that is
        // a stronger version of the same test, because "monkmode_partner..." sorts LAST of all
        // three families, which is exactly the starvation risk P41 was sized against.
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();

            var exitTrigger = Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id);
            File.WriteAllText(exitTrigger, a.PartnerCode);
            for (var i = 0; i < 40; i++)
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.AddRequestPrefix + "z" + i.ToString("000", CultureInfo.InvariantCulture)), "junk.com");

            // Tick 1: the budget is spent entirely on the flood.
            var names = Tick(svc, dir);
            Assert.Equal(monkmode.Service1.MaxTriggerFilesPerTick, names.Count);
            Assert.DoesNotContain(monkmode.Service1.PartnerCodePrefix + a.Id, names);
            Assert.True(File.Exists(exitTrigger), "a starved exit trigger was consumed anyway");
            Assert.Equal("", Reload().GetKeyValue("Slot1", "PartnerUnlockedAt"));   // nothing written early
            Assert.Equal(40 - 16 + 1, Directory.GetFiles(dir).Length);

            // Successive ticks drain it; the code survives untouched until its turn.
            var ticks = 1;
            while (Reload().GetKeyValue("Slot1", "PartnerUnlockedAt") == "" && ticks < 10)
            {
                Tick(svc, dir);
                ticks++;
            }
            Assert.True(ticks <= 4, $"the flood took {ticks} ticks to drain - the budget is not converging");
            Assert.NotEqual("", Reload().GetKeyValue("Slot1", "PartnerUnlockedAt"));
            Assert.False(File.Exists(exitTrigger));                               // consumed only once applied
            Assert.DoesNotContain("junk.com", Reload().GetKeyValue("Slot1", "Sites"));   // the flood widened nothing
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void EnumerateTriggerFilesIn_ReadsOnlyTheFourFamilies_AndDeletesNothing()
    {
        // Enumeration is a READ. A directory that also holds the config, the backup, the
        // snapshot and the user's own files must come back with those untouched and unlisted -
        // the state directory is shared with the deployed exes.
        var dir = TempDir();
        try
        {
            var bystanders = new[] { "monkmode_settings.ini", "monkmode_settings.bak", "monkmode_hosts.block",
                                     "monkmode_stats", "monkmode_add_to_hosts", "readme.txt",
                                     "monkmode_cooloff.requestX", "monkmode_partner.codeX" };
            foreach (var b in bystanders) File.WriteAllText(Path.Combine(dir, b), "keep me");
            for (var i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + i), "");

            var names = monkmode.Service1.EnumerateTriggerFilesIn(dir);
            Assert.Equal(3, names.Count);
            // Ledger 319: these three ARE enumerated (the glob still carries the CoolOff
            // patterns) but address no family, which is what gets them purged below.
            foreach (var n in names) Assert.False(monkmode.Service1.TriggerAddressesAnyFamily(n));

            // Purging the unaddressed must leave every bystander in place too.
            monkmode.Service1.PurgeUnaddressedTriggers(dir, names);
            foreach (var b in bystanders) Assert.True(File.Exists(Path.Combine(dir, b)), b + " was removed");
            Assert.Empty(monkmode.Service1.EnumerateTriggerFilesIn(dir));   // ...and the three are gone
        }
        finally { Drop(dir); }
    }

    [Fact]
    public void EnumerateTriggerFilesIn_AnAbsentDirectory_IsAnEmptyTick_NotAThrow()
    {
        // Best-effort: an unreadable state directory defers every trigger to the next tick,
        // which is the fail-closed direction (a deferred EXIT holds the block ~10s longer).
        var absent = Path.Combine(AppContext.BaseDirectory, "no-such-dir-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(monkmode.Service1.EnumerateTriggerFilesIn(absent));
        Assert.Empty(monkmode.Service1.EnumerateTriggerFilesIn(""));
        Assert.Empty(monkmode.Service1.EnumerateTriggerFilesIn(null));
        // ...and purging against a directory that is not there is likewise a no-op.
        monkmode.Service1.PurgeUnaddressedTriggers(absent, new List<string> { "junk" });
        monkmode.Service1.PurgeUnaddressedTriggers(absent, null);
    }

    [Fact]
    public void TheTwoPollers_SurviveNullAndEmptyArguments_WithoutWritingAnything()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());
            var path = MonkMode.Blocker.IniPath();
            var empty = new List<string>();

            // Ledger 319: the cooling-off poller is gone; the purge takes its place and must be
            // equally total (it runs inside the tick, so a throw here stops enforcement).
            monkmode.Service1.PurgeUnaddressedTriggers(dir, null);
            monkmode.Service1.PurgeUnaddressedTriggers(dir, empty);
            svc.ProcessPartnerCodeSignalAt(dir, path, null, true, empty);
            svc.ProcessPartnerCodeSignalAt(dir, path, svc.LoadSlots(Reload()), true, null);
            svc.ProcessAddRequestsAt(dir, path, null, true, empty);
            svc.ProcessAddRequestsAt(dir, path, svc.LoadSlots(Reload()), true, null);

            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void AFrozenConfig_BinsEveryFamily_WithoutWideningOrReStamping()
    {
        // The B7 rule: a frozen config is never re-stamped and never widened. The triggers are
        // still DELETED rather than held over, because a frozen config is only left by
        // re-arming and holding them would leak the shared budget for ever.
        Wipe();
        var dir = TempDir();
        try
        {
            var a = Arm("a.com");
            Assert.True(a.Ok);
            var svc = Svc();
            var before = File.ReadAllBytes(MonkMode.Blocker.IniPath());

            File.WriteAllText(Path.Combine(dir, monkmode.Service1.CoolOffRequestPrefix + a.Id), "");
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.PartnerCodePrefix + a.Id), a.PartnerCode);
            File.WriteAllText(Path.Combine(dir, monkmode.Service1.AddRequestPrefix + a.Id), "late.com");
            Tick(svc, dir, macValid: false);

            Assert.Equal(before, File.ReadAllBytes(MonkMode.Blocker.IniPath()));
            var after = Reload();
            Assert.Equal("", after.GetKeyValue("Slot1", "PartnerUnlockedAt"));
            Assert.Equal("", after.GetKeyValue("Slot1", "CoolOffUntil"));
            Assert.DoesNotContain("late.com", after.GetKeyValue("Slot1", "Sites"));
        }
        finally { Wipe(); Drop(dir); }
    }
}
