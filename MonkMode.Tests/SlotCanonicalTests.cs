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

// MonkMode.Tests - the v10 TWO-LEVEL enforcement canonical (v1.1 multi-block, S1).
//
// WHAT THIS PINS (the things a future edit could break silently, each with the
// failure it prevents):
//   - the exact v10 wire format, as a byte literal for a two-slot config: the
//     header (HighWater/Now/NextSlotId/SlotCount/GuardHoldUntil/GuardArmedCount)
//     then 16 "SlotN.Field=" lines per slot, ascending, every line vbLf-terminated
//     INCLUDING the last. A drift in order, naming or separators would silently
//     break cross-party MAC agreement and freeze every block.
//   - ParseSlotCount's fail-closed clamp: blank/garbage/negative => 0 slots, above
//     MaxSlots => MaxSlots. Neither is "enforce fewer blocks" - a clamped count
//     builds a canonical the stored MAC cannot match, so every reader FREEZES
//     (ClassifyHeartbeat Hold). The clamp is what makes a forged "SlotCount=99"
//     useless to an evader.
//   - stray [SlotN] sections beyond SlotCount contribute NOTHING to the canonical:
//     a section an attacker adds by hand can never gain authority, because the loop
//     bound is the MAC-covered SlotCount, not what is present in the file.
//   - ONE MAC over the WHOLE file (never per-slot): a per-slot MAC would be a
//     partial-lift vector - re-stamp one slot, drop it, keep the rest verifying.
//   - IniFile.RemoveSection (the slot-RETIRE primitive): a removed slot is gone from
//     both the lookup model and the saved file, so no stale state can be resurrected.
//
// Fences honoured: pure string/model assertions plus ONE temp ini under the test bin
// directory (GUID-named, deleted in a finally). No DPAPI, no hosts, no registry, no
// SCM, never the real deployed config.

using System.Text;

namespace MonkMode.Tests;

public class SlotCanonicalTests
{
    [Fact]
    public void V10_ByteLiteral_TwoSlots()
    {
        // The pinned v10 skeleton: a two-slot config - Slot1 ACTIVE (Until set, a
        // configured cool-off, a partner verifier), Slot2 PENDING (StartAt +
        // DurationSeconds, no Until, url patterns, all-session kill). Slot STATE is
        // derived from these fields, never stored, so this literal is also the pin
        // for what PENDING vs ACTIVE look like on the wire.
        var slot1 = MonkMode.ConfigIntegrity.BuildSlotCanonical(
            1, "5", "", "", "2026-08-09 9:00:00 p.m.", "reddit.com;", "chrome.exe;", "", "",
            "", "", "", "5400", "PSALT", "PHASH", "", "no");
        var slot2 = MonkMode.ConfigIntegrity.BuildSlotCanonical(
            2, "6", "2026-08-10 7:00:00 a.m.", "7200", "", "instagram.com;", "", "instagram.com/reels|", "yes",
            "", "", "", "", "QSALT", "QHASH", "", "no");

        var canonical = MonkMode.ConfigIntegrity.BuildCanonical(
            "v10", "2026-08-09 3:00:00 p.m.", "2026-08-09 3:00:10 p.m.", "7", 2,
            "2026-08-09 11:59:00 p.m.", "1", slot1 + slot2);

        Assert.Equal(
            "v10\n" +
            "HighWater=2026-08-09 3:00:00 p.m.\n" +
            "Now=2026-08-09 3:00:10 p.m.\n" +
            "NextSlotId=7\n" +
            "SlotCount=2\n" +
            "GuardHoldUntil=2026-08-09 11:59:00 p.m.\n" +
            "GuardArmedCount=1\n" +
            "Slot1.Id=5\n" +
            "Slot1.StartAt=\n" +
            "Slot1.DurationSeconds=\n" +
            "Slot1.Until=2026-08-09 9:00:00 p.m.\n" +
            "Slot1.Sites=reddit.com;\n" +
            "Slot1.Apps=chrome.exe;\n" +
            "Slot1.UrlPatterns=\n" +
            "Slot1.AllSession=\n" +
            "Slot1.ScheduleSpec=\n" +
            "Slot1.ScheduleActiveUntil=\n" +
            "Slot1.CoolOffUntil=\n" +
            "Slot1.CoolOffDuration=5400\n" +
            "Slot1.PartnerSalt=PSALT\n" +
            "Slot1.PartnerHash=PHASH\n" +
            "Slot1.PartnerUnlockedAt=\n" +
            "Slot1.Committed=no\n" +
            "Slot2.Id=6\n" +
            "Slot2.StartAt=2026-08-10 7:00:00 a.m.\n" +
            "Slot2.DurationSeconds=7200\n" +
            "Slot2.Until=\n" +
            "Slot2.Sites=instagram.com;\n" +
            "Slot2.Apps=\n" +
            "Slot2.UrlPatterns=instagram.com/reels|\n" +
            "Slot2.AllSession=yes\n" +
            "Slot2.ScheduleSpec=\n" +
            "Slot2.ScheduleActiveUntil=\n" +
            "Slot2.CoolOffUntil=\n" +
            "Slot2.CoolOffDuration=\n" +
            "Slot2.PartnerSalt=QSALT\n" +
            "Slot2.PartnerHash=QHASH\n" +
            "Slot2.PartnerUnlockedAt=\n" +
            "Slot2.Committed=no\n",
            canonical);

        // Every slot block is exactly 16 lines - the fixed, eyeball-checkable shape.
        Assert.Equal(16, slot1.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(16, slot2.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        // ...and the canonical ends in a newline, like every version before it.
        Assert.EndsWith("\n", canonical);
    }

    [Fact]
    public void EveryCopy_AgreesOnMaxSlots_AndTheSchemaVersion()
    {
        // MaxSlots is the loop ceiling ParseSlotCount clamps to. If one copy raised it,
        // that party would emit slot blocks the others do not - the canonical would
        // diverge and every block would freeze. (The schema-version twin of this pin,
        // AllFourCopies_ShareTheSameSchemaVersion, lives in ConfigIntegrityTests.)
        Assert.Equal(8, MonkMode.ConfigIntegrity.MaxSlots);
        Assert.Equal(MonkMode.ConfigIntegrity.MaxSlots, monkmode.ConfigIntegrity.MaxSlots);
        Assert.Equal(MonkMode.ConfigIntegrity.MaxSlots, mm_guard.ConfigIntegrity.MaxSlots);
        Assert.Equal(MonkMode.ConfigIntegrity.MaxSlots, mm_notify.ConfigIntegrity.MaxSlots);
        Assert.Equal("v10", MonkMode.ConfigIntegrity.CurrentSchemaVersion);
    }

    [Theory]
    [InlineData("", 0)]          // absent/blank => no slots
    [InlineData("   ", 0)]
    [InlineData(null, 0)]
    [InlineData("abc", 0)]       // garbage
    [InlineData("2.5", 0)]       // not an integer
    [InlineData("-1", 0)]        // negative
    [InlineData("9", 8)]         // above the ceiling => clamped DOWN to MaxSlots
    [InlineData("99", 8)]        // the forged-count attack
    [InlineData("8", 8)]         // the ceiling itself
    [InlineData("0", 0)]
    [InlineData(" 3 ", 3)]       // whitespace tolerated, like every other ini value
    public void ParseSlotCount_Garbage_ClampsFailClosed(string? raw, int expected)
    {
        // Fail-closed in the ONLY direction that matters: the clamp never yields
        // "fewer slots to enforce", it yields a canonical that cannot match the stored
        // MAC -> macValid False -> every reader Holds. Pinned across all four copies,
        // because a divergent clamp is a divergent canonical.
        Assert.Equal(expected, MonkMode.ConfigIntegrity.ParseSlotCount(raw!));
        Assert.Equal(expected, monkmode.ConfigIntegrity.ParseSlotCount(raw!));
        Assert.Equal(expected, mm_guard.ConfigIntegrity.ParseSlotCount(raw!));
        Assert.Equal(expected, mm_notify.ConfigIntegrity.ParseSlotCount(raw!));
    }

    [Fact]
    public void ForgedSlotCount_BuildsACanonicalNoStoredMacCanMatch()
    {
        // The point of the clamp, end to end: an evader edits [Slots] SlotCount from 1
        // to 99 hoping a reader loops past the real slots (or trips). Instead the
        // reader clamps to 8, emits 8 blocks (7 of them empty), and the canonical no
        // longer matches the MAC stamped over the 1-slot canonical => FREEZE.
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var honest = MonkMode.Blocker.CanonicalFromIni(SlotIni(slotCount: "1"));
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(honest, key);
        Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(honest, storedMac, key));

        var forged = MonkMode.Blocker.CanonicalFromIni(SlotIni(slotCount: "99"));
        Assert.Contains("SlotCount=8\n", forged);            // clamped, not honoured
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(forged, storedMac, key));
    }

    [Fact]
    public void StraySlotSection_BeyondSlotCount_DoesNotChangeCanonical()
    {
        // A hand-added [Slot3] in a SlotCount=2 config must be invisible: every reader
        // loops 1..SlotCount only. A stray section can never ADD authority (and
        // SlotCount is itself MAC-covered, so it cannot be grown to reach the stray).
        var clean = SlotIni(slotCount: "2");
        var withStray = SlotIni(slotCount: "2");
        withStray.SetKeyValue("Slot3", "Id", "99");
        withStray.SetKeyValue("Slot3", "Sites", "attacker-added.com;");
        withStray.SetKeyValue("Slot3", "Until", "2099-01-01 12:00:00 a.m.");

        var canonical = MonkMode.Blocker.CanonicalFromIni(clean);
        Assert.Equal(canonical, MonkMode.Blocker.CanonicalFromIni(withStray));
        Assert.DoesNotContain("Slot3.", canonical);
        // And every other reader agrees the stray is invisible.
        Assert.Equal(canonical, new monkmode.Service1().CanonicalFromIni(SrvCopy(withStray)));
    }

    [Fact]
    public void OneMacOverTheWholeFile_NeverAPerSlotMac()
    {
        // A per-slot MAC would be a partial-lift vector: re-stamp one slot, delete it,
        // and leave the rest verifying. There is exactly ONE [Integrity] Mac key, and
        // no slot section carries a Mac/Key of its own.
        var ini = SlotIni(slotCount: "2");
        ini.SetKeyValue("Integrity", "Mac", "the-one-and-only");
        Assert.Equal("the-one-and-only", ini.GetKeyValue("Integrity", "Mac"));
        foreach (var sec in new[] { "Slot1", "Slot2" })
        {
            Assert.Equal("", ini.GetKeyValue(sec, "Mac"));
            Assert.Equal("", ini.GetKeyValue(sec, "Key"));
        }
        // The MAC lines are excluded from the canonical (you can't MAC the MAC).
        var canonical = MonkMode.Blocker.CanonicalFromIni(ini);
        Assert.DoesNotContain("Mac=", canonical);
    }

    [Fact]
    public void RemoveSection_RoundTrips()
    {
        // The slot-RETIRE primitive: a removed section disappears from BOTH the lookup
        // model and the SAVED file. Removing from only one would leave Save re-emitting
        // a section no reader can see (or the reverse) - a retired block that comes back.
        var path = Path.Combine(AppContext.BaseDirectory, $"slotcanon_{Guid.NewGuid():N}.ini");
        try
        {
            var ini = new MonkMode.IniFile();
            ini.SetKeyValue("Slots", "SlotCount", "2");
            ini.SetKeyValue("Slot1", "Id", "1");
            ini.SetKeyValue("Slot2", "Id", "2");
            ini.SetKeyValue("Slot2", "Sites", "reddit.com;");

            Assert.True(ini.RemoveSection("slot2"));          // case-insensitive, like every lookup
            Assert.False(ini.RemoveSection("Slot2"));         // gone => no-op, no throw
            Assert.False(ini.RemoveSection("NeverExisted"));
            Assert.Equal("", ini.GetKeyValue("Slot2", "Id"));
            Assert.Equal("1", ini.GetKeyValue("Slot1", "Id"));

            ini.Save(path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("[Slot2]", text);
            Assert.DoesNotContain("reddit.com;", text);
            Assert.Contains("[Slot1]", text);

            // ...and it stays gone across a reload (the file is the truth).
            var reloaded = new MonkMode.IniFile();
            reloaded.Load(path);
            Assert.Equal("", reloaded.GetKeyValue("Slot2", "Id"));
            Assert.Equal("1", reloaded.GetKeyValue("Slot1", "Id"));

            // Re-adding the same name after a removal starts clean (no resurrected keys).
            reloaded.SetKeyValue("Slot2", "Id", "7");
            Assert.Equal("7", reloaded.GetKeyValue("Slot2", "Id"));
            Assert.Equal("", reloaded.GetKeyValue("Slot2", "Sites"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemoveSection_ParityAcrossAllFourCopies()
    {
        // IniFile.vb is byte-identical in all four projects; a new member has to behave
        // identically in every one or the parties would disagree about a retired slot.
        var cli = new MonkMode.IniFile();
        cli.SetKeyValue("Slot1", "Id", "1");
        Assert.True(cli.RemoveSection("Slot1"));
        Assert.False(cli.RemoveSection("Slot1"));

        var srv = new monkmode.IniFile();
        srv.SetKeyValue("Slot1", "Id", "1");
        Assert.True(srv.RemoveSection("Slot1"));
        Assert.False(srv.RemoveSection("Slot1"));

        var guard = new mm_guard.IniFile();
        guard.SetKeyValue("Slot1", "Id", "1");
        Assert.True(guard.RemoveSection("Slot1"));
        Assert.False(guard.RemoveSection("Slot1"));

        var notify = new mm_notify.IniFile();
        notify.SetKeyValue("Slot1", "Id", "1");
        Assert.True(notify.RemoveSection("Slot1"));
        Assert.False(notify.RemoveSection("Slot1"));
    }

    // ---- helpers ----

    // A v10-shaped in-memory config with two slots (plaintext-only fields, so no
    // Simple3Des round-trip is needed for these structural assertions).
    private static MonkMode.IniFile SlotIni(string slotCount)
    {
        var ini = new MonkMode.IniFile();
        ini.SetKeyValue("Slots", "NextSlotId", "3");
        ini.SetKeyValue("Slots", "SlotCount", slotCount);
        ini.SetKeyValue("Guard", "ArmedCount", "2");
        ini.SetKeyValue("Slot1", "Id", "1");
        ini.SetKeyValue("Slot1", "Sites", "reddit.com;");
        ini.SetKeyValue("Slot1", "Apps", "chrome.exe;");
        ini.SetKeyValue("Slot2", "Id", "2");
        ini.SetKeyValue("Slot2", "Sites", "instagram.com;");
        ini.SetKeyValue("Slot2", "UrlPatterns", "instagram.com/reels|");
        return ini;
    }

    // The same logical content in the SERVICE project's own IniFile type (production
    // reality: one ini on disk, four readers each with their own parser copy).
    private static monkmode.IniFile SrvCopy(MonkMode.IniFile source)
    {
        var ini = new monkmode.IniFile();
        ini.SetKeyValue("Slots", "NextSlotId", source.GetKeyValue("Slots", "NextSlotId"));
        ini.SetKeyValue("Slots", "SlotCount", source.GetKeyValue("Slots", "SlotCount"));
        ini.SetKeyValue("Guard", "ArmedCount", source.GetKeyValue("Guard", "ArmedCount"));
        foreach (var sec in new[] { "Slot1", "Slot2", "Slot3" })
        {
            foreach (var k in new[] { "Id", "Sites", "Apps", "UrlPatterns", "Until" })
            {
                var v = source.GetKeyValue(sec, k);
                if (v != "") ini.SetKeyValue(sec, k, v);
            }
        }
        return ini;
    }
}

// The v9 -> v10 migration shim for the pre-existing field-coverage tests.
//
// Those tests (PartnerCodeTests, CommitBlockTests, CoolOffTests, AllSessionKillTests,
// ConfigIntegrityTests' tamper matrix) each pin "field X is MAC-covered: flip it and
// the stamp stops validating". That fact is unchanged by v10 - only the field's PLACE
// changed, from the flat single-block canonical into slot 1 of the two-level one. So
// they call this helper with the SAME argument list they used to pass BuildCanonical
// (minus the version), and it renders them as a one-slot v10 config. Keeping the old
// argument order is deliberate: it made the S1 migration a mechanical substitution,
// which is what kept the intent of ~40 assertions verifiably intact.
//
// New tests should call BuildCanonical/BuildSlotCanonical directly - this is a
// migration aid, not the house style.
internal static class OneSlot
{
    public const string NextSlotId = "2";
    public const string GuardHoldUntil = "";
    public const string GuardArmedCount = "1";
    public const string Id = "1";

    public static string Canonical(
        string until, string apps, string sites, string now, string highWater, string coolOffUntil,
        string partnerSalt, string partnerHash, string partnerUnlockedAt, string committed,
        string scheduleSpec, string scheduleActiveUntil, string coolOffDuration, string allSession) =>
        MonkMode.ConfigIntegrity.BuildCanonical(
            MonkMode.ConfigIntegrity.CurrentSchemaVersion, highWater, now, NextSlotId, 1,
            GuardHoldUntil, GuardArmedCount,
            MonkMode.ConfigIntegrity.BuildSlotCanonical(
                1, Id, "", "", until, sites, apps, "", allSession,
                scheduleSpec, scheduleActiveUntil, coolOffUntil, coolOffDuration,
                partnerSalt, partnerHash, partnerUnlockedAt, committed));

    // Write the same one-slot shape into a real ini (CLI copy), so a test can drive it
    // through the four REAL CanonicalFromIni wrappers. Datetime fields are handed over
    // ALREADY ENCRYPTED by the caller (the wrappers decrypt them); everything else is
    // plaintext-as-stored, including Sites/Apps/UrlPatterns.
    public static void WriteSlot1(Action<string, string, string> set,
        string untilEnc, string apps, string sites, string nowEnc, string highWaterEnc, string coolOffUntilEnc,
        string partnerSalt, string partnerHash, string partnerUnlockedAt, string committed,
        string scheduleSpec, string scheduleActiveUntilEnc, string coolOffDuration, string allSession)
    {
        set("Time", "HighWater", highWaterEnc);
        set("CurrentTime", "Now", nowEnc);
        set("Slots", "NextSlotId", NextSlotId);
        set("Slots", "SlotCount", "1");
        set("Guard", "HoldUntil", GuardHoldUntil);
        set("Guard", "ArmedCount", GuardArmedCount);
        set("Slot1", "Id", Id);
        set("Slot1", "StartAt", "");
        set("Slot1", "DurationSeconds", "");
        set("Slot1", "Until", untilEnc);
        set("Slot1", "Sites", sites);
        set("Slot1", "Apps", apps);
        set("Slot1", "UrlPatterns", "");
        set("Slot1", "AllSession", allSession);
        set("Slot1", "ScheduleSpec", scheduleSpec);
        set("Slot1", "ScheduleActiveUntil", scheduleActiveUntilEnc);
        set("Slot1", "CoolOffUntil", coolOffUntilEnc);
        set("Slot1", "CoolOffDuration", coolOffDuration);
        set("Slot1", "PartnerSalt", partnerSalt);
        set("Slot1", "PartnerHash", partnerHash);
        set("Slot1", "PartnerUnlockedAt", partnerUnlockedAt);
        set("Slot1", "Committed", committed);
    }
}
