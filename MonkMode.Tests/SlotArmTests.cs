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

// MonkMode.Tests - v1.1 S2: arming a SLOT (Blocker.ArmSlot).
//
// WHAT THIS PINS (each with the failure it prevents):
//   - a first arm on a fresh config writes [Slot1] with Id=1, SlotCount=1, NextSlotId=2,
//     and the whole slot lands INSIDE the MAC-covered v10 canonical (the S1->S2 coverage
//     gap: S1 moved the readers to v10 while the writer still emitted v9 sections, so
//     every enforcement field sat outside the canonical - valid MAC, tamper-open).
//   - a SECOND arm appends beside the first and NEVER re-seeds the machine frame. This is
//     the most important test in the slice: [Time] HighWater / [CurrentTime] Now are the
//     monotonic clock every running slot's expiry is measured against, so re-seeding them
//     at a second arm would hand slot 1 a fresh, later clock and could lift it EARLY.
//   - the P34 cap: the 9th arm refuses with the slot list and NO side effect.
//   - P17: ids are never reused - not across a retire, not when NextSlotId lags. A reused
//     id would let a replayed monkmode_partner.code.<id> trigger address a DIFFERENT block.
//   - P18: a byte-real v9 config + service ABSENT => full fresh rewrite; and the rule is
//     NOT widened - with the service PRESENT (registered, even if stopped) an unusable
//     config FREEZES instead, because a stopped auto-start service can be resurrected by
//     SCM recovery or a reboot and would then race the rewrite.
//   - the arm-time hosts/snapshot UNION: a second arm merges its entry lines into the
//     block already on disk instead of replacing it, so arming block 2 can never silently
//     unblock block 1's sites.
//
// Fences honoured: the ONLY files touched are the test-bin ini/backup/snapshot (wiped in
// finally) and a GUID temp path under the test bin directory. Never the real hosts file,
// the registry, the SCM (service presence is INJECTED, never queried), Program Files, or
// any deployed config. Nothing is ever armed for real.

using System.Globalization;

namespace MonkMode.Tests;

[Collection("CliIniWriters")]
public class SlotArmTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static MonkMode.IniFile Load()
    {
        var ini = new MonkMode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return ini;
    }

    /// <summary>Arm one ACTIVE slot with the service treated as ABSENT (never queried).</summary>
    private static MonkMode.Blocker.ArmResult Arm(string[] sites, string[] apps, DateTime ends,
                                                  string urls = "", bool committed = false,
                                                  long coolOff = 0, bool allSession = false)
        => MonkMode.Blocker.ArmSlot(sites, apps, urls, null, ends, false, committed, coolOff, allSession);

    // ---- 1. the first arm ----

    [Fact]
    public void Arm1_FreshConfig_WritesSlot1()
    {
        Wipe();
        try
        {
            var r = Arm(new[] { "reddit.com" }, new[] { "chrome.exe" }, Ends, coolOff: 7200);
            Assert.True(r.Ok);
            Assert.Equal(1, r.Id);
            Assert.Equal(1, r.Position);
            Assert.True(r.FreshRewrite);
            Assert.False(string.IsNullOrWhiteSpace(r.PartnerCode));

            var ini = Load();
            Assert.Equal("1", ini.GetKeyValue("Slots", "SlotCount"));
            Assert.Equal("2", ini.GetKeyValue("Slots", "NextSlotId"));
            Assert.Equal("1", ini.GetKeyValue("Slot1", "Id"));
            Assert.Equal("reddit.com;", ini.GetKeyValue("Slot1", "Sites"));
            Assert.Equal("chrome.exe;", ini.GetKeyValue("Slot1", "Apps"));
            Assert.Equal("7200", ini.GetKeyValue("Slot1", "CoolOffDuration"));
            Assert.Equal("no", ini.GetKeyValue("Slot1", "Committed"));

            // THE point of the slice: the slot's fields are inside the MAC-covered canonical
            // (S1 shipped the readers; S2 makes the writer actually fill them in).
            var cli = MonkMode.Blocker.CanonicalFromIni(ini);
            Assert.Contains("SlotCount=1\n", cli);
            Assert.Contains("Slot1.Id=1\n", cli);
            Assert.Contains("Slot1.Sites=reddit.com;\n", cli);
            Assert.Contains("Slot1.Apps=chrome.exe;\n", cli);
            Assert.Contains("Slot1.CoolOffDuration=7200\n", cli);

            // ...and all four readers still derive the identical canonical from what was
            // written, so the single MAC verifies everywhere (canonicals only - DPAPI-free).
            var srv = new monkmode.IniFile(); srv.Load(MonkMode.Blocker.IniPath());
            var guard = new mm_guard.IniFile(); guard.Load(MonkMode.Blocker.IniPath());
            var notify = new mm_notify.IniFile(); notify.Load(MonkMode.Blocker.IniPath());
            Assert.Equal(cli, MonkMode.Tests.TestSvc.New().CanonicalFromIni(srv));
            Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guard));
            Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notify));

            // The v9 mirror the enforcement readers still consume until S3a.
            Assert.Equal("reddit.com;", ini.GetKeyValue("User", "CustomSites"));
            Assert.Equal(Ends, MonkMode.Blocker.ActiveBlockEnd());
        }
        finally { Wipe(); }
    }

    // ---- 2. THE test: a second arm appends and never re-seeds the frame ----

    [Fact]
    public void Arm2_PreservesSlot1_And_DoesNotReseed_HighWater_Or_Now()
    {
        Wipe();
        try
        {
            var first = Arm(new[] { "reddit.com" }, new[] { "chrome.exe" }, Ends);
            Assert.True(first.Ok);

            var before = Load();
            var slot1Before = SlotSnapshot(before, "Slot1");
            var highWaterBefore = before.GetKeyValue("Time", "HighWater");
            var nowBefore = before.GetKeyValue("CurrentTime", "Now");
            Assert.NotEqual("", highWaterBefore);
            Assert.NotEqual("", nowBefore);

            // Sleep past a whole second so a re-seed CANNOT coincidentally reproduce the
            // same en-CA string (which is second-resolution). Without this the assertion
            // could pass for the wrong reason on a fast machine.
            Thread.Sleep(1100);

            var second = MonkMode.Blocker.ArmSlot(new[] { "x.com" }, Array.Empty<string>(), "",
                                                  null, Ends.AddHours(1), false);
            Assert.True(second.Ok);
            Assert.Equal(2, second.Id);
            Assert.Equal(2, second.Position);
            Assert.False(second.FreshRewrite);          // appended, not rewritten

            var after = Load();
            // (a) the frame is untouched - byte-identical stored ciphertext AND the same
            //     decrypted values in the canonical.
            Assert.Equal(highWaterBefore, after.GetKeyValue("Time", "HighWater"));
            Assert.Equal(nowBefore, after.GetKeyValue("CurrentTime", "Now"));
            Assert.Equal(HeaderLine(before, "HighWater="), HeaderLine(after, "HighWater="));
            Assert.Equal(HeaderLine(before, "Now="), HeaderLine(after, "Now="));

            // (b) slot 1 is preserved field for field - the same id, sites, apps, end and
            //     its own partner verifier. Arming block 2 changed nothing about block 1.
            Assert.Equal(slot1Before, SlotSnapshot(after, "Slot1"));

            // (c) slot 2 exists beside it, and the header advanced.
            Assert.Equal("2", after.GetKeyValue("Slots", "SlotCount"));
            Assert.Equal("3", after.GetKeyValue("Slots", "NextSlotId"));
            Assert.Equal("2", after.GetKeyValue("Slot2", "Id"));
            Assert.Equal("x.com;", after.GetKeyValue("Slot2", "Sites"));

            // (d) both slots are MAC-covered, and the MAC still validates (the re-stamp used
            //     the EXISTING key - a fresh key would have been a new block, not an append).
            var cli = MonkMode.Blocker.CanonicalFromIni(after);
            Assert.Contains("SlotCount=2\n", cli);
            Assert.Contains("Slot1.Sites=reddit.com;\n", cli);
            Assert.Contains("Slot2.Sites=x.com;\n", cli);
            Assert.Equal(after.GetKeyValue("Integrity", "Key"), before.GetKeyValue("Integrity", "Key"));
            // Ledger 319 deleted Blocker.BlockIsCommitted, the MAC-gated reader this used to
            // lean on to say "and the MAC still validates". ConfigIsMacValid is the surviving
            // MAC-gated reader and says the same thing more directly.
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());

            // (e) the v9 mirror is the OVER-blocking union/max, never a replacement: both
            //     slots' sites, and the LATER of the two ends.
            Assert.Equal("reddit.com;x.com;", after.GetKeyValue("User", "CustomSites"));
            Assert.Equal(Ends.AddHours(1), MonkMode.Blocker.ActiveBlockEnd());

            // (f) each slot carries its OWN partner verifier - a code minted for one block
            //     can never be the verifier for the other (P17's reason for unique ids).
            Assert.NotEqual(after.GetKeyValue("Slot1", "PartnerHash"), after.GetKeyValue("Slot2", "PartnerHash"));
            Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches(
                second.PartnerCode, after.GetKeyValue("Slot2", "PartnerSalt"), after.GetKeyValue("Slot2", "PartnerHash")));
            Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches(
                second.PartnerCode, after.GetKeyValue("Slot1", "PartnerSalt"), after.GetKeyValue("Slot1", "PartnerHash")));
        }
        finally { Wipe(); }
    }

    /// <summary>Every stored key of one slot section, as a stable comparable string.</summary>
    private static string SlotSnapshot(MonkMode.IniFile ini, string section)
    {
        var keys = new[] { "Id", "StartAt", "DurationSeconds", "Until", "Sites", "Apps", "UrlPatterns",
                           "AllSession", "ScheduleSpec", "ScheduleActiveUntil", "CoolOffUntil",
                           "CoolOffDuration", "PartnerSalt", "PartnerHash", "PartnerUnlockedAt", "Committed" };
        return string.Join("\n", keys.Select(k => k + "=" + ini.GetKeyValue(section, k)));
    }

    /// <summary>The canonical's global header line starting with <paramref name="prefix"/>.</summary>
    private static string HeaderLine(MonkMode.IniFile ini, string prefix)
        => MonkMode.Blocker.CanonicalFromIni(ini).Split('\n').First(l => l.StartsWith(prefix));

    // ---- 3. P34: the cap ----

    [Fact]
    public void Arm_Cap8_Refuses_Exit3()
    {
        Wipe();
        try
        {
            for (int i = 1; i <= MonkMode.ConfigIntegrity.MaxSlots; i++)
            {
                var ok = Arm(new[] { "site" + i + ".com" }, Array.Empty<string>(), Ends.AddMinutes(i));
                Assert.True(ok.Ok);
                Assert.Equal(i, ok.Id);
            }
            var full = Load();
            Assert.Equal("8", full.GetKeyValue("Slots", "SlotCount"));
            var macBefore = full.GetKeyValue("Integrity", "Mac");

            var ninth = Arm(new[] { "overflow.com" }, Array.Empty<string>(), Ends);
            Assert.False(ninth.Ok);
            Assert.Equal(MonkMode.Blocker.ArmOutcome.CapReached, ninth.Outcome);
            // P34: one line per slot in use, so the refusal tells you what to end.
            Assert.Equal(MonkMode.ConfigIntegrity.MaxSlots, ninth.SlotSummaries.Count);
            Assert.Contains(ninth.SlotSummaries, l => l.Contains("site1.com"));
            Assert.Contains(ninth.SlotSummaries, l => l.Contains("site8.com"));
            // The CLI maps this to exit 3 (0/1/2/3/4 across the whole CLI).
            Assert.Equal(3, MonkMode.Blocker.ExitCapReached);

            // ...and it refused BEFORE any side effect: no 9th slot, no id burnt, no re-stamp.
            var after = Load();
            Assert.Equal("8", after.GetKeyValue("Slots", "SlotCount"));
            Assert.Equal("9", after.GetKeyValue("Slots", "NextSlotId"));
            Assert.Equal("", after.GetKeyValue("Slot9", "Id"));
            Assert.Equal(macBefore, after.GetKeyValue("Integrity", "Mac"));
            Assert.DoesNotContain("overflow.com", after.GetKeyValue("User", "CustomSites"));
        }
        finally { Wipe(); }
    }

    // ---- 4. P17: ids are never reused ----

    [Theory]
    // (storedNextSlotId, highestExistingId) => the id this arm takes.
    [InlineData("1", 0, 1)]     // first ever arm
    [InlineData("3", 2, 3)]     // the ordinary case: NextSlotId leads
    [InlineData("3", 1, 3)]     // slot #2 RETIRED (its section is gone, so the highest present
                                //   id fell back to 1) - NextSlotId still leads, so no reuse.
    [InlineData("1", 2, 3)]     // NextSlotId somehow LAGS (rolled back / hand-edited): the
                                //   highest present id wins, so #1 and #2 are never reissued.
    [InlineData("", 2, 3)]      // blank / absent NextSlotId - still never reuses a live id.
    [InlineData("garbage", 0, 1)]
    [InlineData("-5", 3, 4)]    // a negative NextSlotId can never drag an id backwards.
    public void Ids_NeverReused_AcrossRetireAndArm(string storedNextSlotId, int highestExistingId, int expected)
        => Assert.Equal(expected, MonkMode.Blocker.NextIdForArm(storedNextSlotId, highestExistingId));

    [Fact]
    public void Ids_Ascend_AcrossConsecutiveArms_EndToEnd()
    {
        Wipe();
        try
        {
            Assert.Equal(1, Arm(new[] { "a.com" }, Array.Empty<string>(), Ends).Id);
            Assert.Equal(2, Arm(new[] { "b.com" }, Array.Empty<string>(), Ends).Id);
            Assert.Equal(3, Arm(new[] { "c.com" }, Array.Empty<string>(), Ends).Id);
            var ini = Load();
            Assert.Equal("4", ini.GetKeyValue("Slots", "NextSlotId"));
            Assert.Equal("3", ini.GetKeyValue("Slots", "SlotCount"));
            // The ids live in the Id FIELD; the sections are addressed by POSITION.
            Assert.Equal("1", ini.GetKeyValue("Slot1", "Id"));
            Assert.Equal("2", ini.GetKeyValue("Slot2", "Id"));
            Assert.Equal("3", ini.GetKeyValue("Slot3", "Id"));
        }
        finally { Wipe(); }
    }

    // ---- 5. P18: the stale-v9 rule (and the refusal to widen it) ----

    [Fact]
    public void StaleV9Fixture_ServiceAbsent_ArmsFresh()
    {
        Wipe();
        try
        {
            // A BYTE-REAL v9 config (see fixtures/v9-monkmode_settings.ini for the three
            // synthesised values and why). This is the deployed machine's exact state:
            // uninstall leaves the v9 ini in place by design.
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "v9-monkmode_settings.ini");
            Assert.True(File.Exists(fixturePath), "the v9 fixture must be copied to the test output");
            File.Copy(fixturePath, MonkMode.Blocker.IniPath(), overwrite: true);

            // Under v10 code the v9 file is unusable: it carries no [Slots] section, so the
            // canonical every reader builds from it has NO slot block at all and the stored
            // v9 Mac cannot possibly match it.
            var stale = Load();
            Assert.DoesNotContain("Slot1.", MonkMode.Blocker.CanonicalFromIni(stale));
            Assert.Equal("", stale.GetKeyValue("Slots", "SlotCount"));
            // ...and the fields a v9 block DID carry are still sitting there, uncovered.
            Assert.Contains("facebook.com", stale.GetKeyValue("User", "CustomSites"));

            // P18: service ABSENT + unusable config => full FRESH rewrite.
            Assert.True(MonkMode.Blocker.ShouldFreshRewrite(false, false, false, 0));
            var r = Arm(new[] { "reddit.com" }, Array.Empty<string>(), Ends);
            Assert.True(r.Ok);
            Assert.True(r.FreshRewrite);
            Assert.Equal(1, r.Id);

            var armed = Load();
            Assert.Equal("1", armed.GetKeyValue("Slots", "SlotCount"));
            Assert.Equal("1", armed.GetKeyValue("Slot1", "Id"));
            // The v9 leftovers are GONE, not merged: the fresh path rebuilds the file, so
            // the old block's sites can't be resurrected under the new MAC.
            Assert.Equal("reddit.com;", armed.GetKeyValue("User", "CustomSites"));
            Assert.DoesNotContain("facebook.com", armed.GetKeyValue("User", "CustomSites"));
            Assert.Equal("no", armed.GetKeyValue("User", "Done"));
        }
        finally { Wipe(); }
    }

    [Theory]
    // (serviceInstalled, macValid, hasSlotsSection, slotCount) => fresh rewrite?
    [InlineData(false, false, false, 0, true)]   // P18 proper: v9 file, service absent
    [InlineData(false, false, true, 2, true)]    // MAC-invalid v10 file, service absent
    [InlineData(false, true, false, 0, true)]    // valid-MAC file with no [Slots] at all
    [InlineData(false, true, true, 0, false)]    // a legitimately EMPTY v10 config: append to it
    [InlineData(false, true, true, 3, false)]    // the ordinary append
    // THE rule that must NOT be widened: "ABSENT" means NOT REGISTERED. A registered-but-
    // STOPPED MONKMODE is auto-start and can be started by SCM recovery or a reboot at any
    // moment, so treating it as absent would let a fresh rewrite race a service about to
    // read the file. With the service present, an unusable config FREEZES, unconditionally.
    [InlineData(true, false, false, 0, false)]
    [InlineData(true, false, true, 2, false)]
    [InlineData(true, true, true, 1, false)]
    public void ShouldFreshRewrite_ServicePresentAlwaysFreezes(bool installed, bool macValid, bool hasSlots, int count, bool expected)
        => Assert.Equal(expected, MonkMode.Blocker.ShouldFreshRewrite(installed, macValid, hasSlots, count));

    [Fact]
    public void StaleV9Fixture_ServicePresent_RefusesAndChangesNothing()
    {
        Wipe();
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "v9-monkmode_settings.ini");
            File.Copy(fixturePath, MonkMode.Blocker.IniPath(), overwrite: true);
            var before = File.ReadAllText(MonkMode.Blocker.IniPath());

            // serviceInstalled:=True (INJECTED - the SCM is never queried from a test).
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "",
                                             null, Ends, true);
            Assert.False(r.Ok);
            Assert.Equal(MonkMode.Blocker.ArmOutcome.Frozen, r.Outcome);
            // B7: never re-stamp over an unverified config - the file is untouched.
            Assert.Equal(before, File.ReadAllText(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }

    // ---- 6. the arm-time hosts/snapshot union (top risk 3) ----

    [Fact]
    public void ArmTimeSnapshot_PreservesSlot1Lines()
    {
        // PURE - the union is computed on strings, so the real hosts file is never involved.
        var slot1 = MonkMode.Blocker.BuildHostsEntries(new[] { "reddit.com" });
        var slot2 = MonkMode.Blocker.BuildHostsEntries(new[] { "x.com" });
        var existingBlock = MonkMode.Blocker.Marker + "\r\n" + slot1;

        var merged = MonkMode.Blocker.UnionHostsBlock(existingBlock, slot2);

        // Every line block 1 put in hosts survives block 2's arm - the whole point: a second
        // arm that REPLACED the block would silently unblock the first block's sites.
        foreach (var line in slot1.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line + "\r\n", merged);
        foreach (var line in slot2.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line + "\r\n", merged);

        // Exactly one marker line, at the top, and no duplicated entries.
        Assert.StartsWith(MonkMode.Blocker.Marker + "\r\n", merged);
        var lines = merged.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, l => l == MonkMode.Blocker.Marker);
        Assert.Equal(lines.Length, lines.Distinct().Count());

        // Re-arming the SAME sites is idempotent - no line grows a duplicate.
        var again = MonkMode.Blocker.UnionHostsBlock(merged, slot1);
        Assert.Equal(merged, again);
    }

    // ---- 7. P55: the --urls caps ----

    [Fact]
    public void UrlPatterns_ParseCapsAndRefusals()
    {
        string packed = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns("", ref packed, ref err));
        Assert.Equal("", packed);

        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns("reddit.com/r/*, x.com/home", ref packed, ref err));
        Assert.Equal("reddit.com/r/*|x.com/home", packed);      // "|" is the STORED separator

        // Fail-closed refusals, never a silently truncated subset.
        Assert.False(MonkMode.Blocker.TryBuildUrlPatterns("a.com|b.com", ref packed, ref err));
        Assert.Equal("", packed);
        Assert.False(MonkMode.Blocker.TryBuildUrlPatterns("a.com;b.com", ref packed, ref err));
        Assert.False(MonkMode.Blocker.TryBuildUrlPatterns(new string('x', MonkMode.Blocker.MaxUrlPatternChars + 1), ref packed, ref err));
        Assert.False(MonkMode.Blocker.TryBuildUrlPatterns(
            string.Join(",", Enumerable.Range(0, MonkMode.Blocker.MaxUrlPatterns + 1).Select(i => "p" + i + ".com")),
            ref packed, ref err));
        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns(
            string.Join(",", Enumerable.Range(0, MonkMode.Blocker.MaxUrlPatterns).Select(i => "p" + i + ".com")),
            ref packed, ref err));
    }

    [Fact]
    public void UrlPatterns_LandInTheSlotCanonical()
    {
        Wipe();
        try
        {
            // The CLI packs the arg through TryBuildUrlPatterns first (it is what refuses a
            // "|"/";" or an over-cap list), then hands ArmSlot the packed form.
            string packed = "", err = "";
            Assert.True(MonkMode.Blocker.TryBuildUrlPatterns("reddit.com/r/*,x.com/home", ref packed, ref err));
            Assert.True(Arm(new[] { "reddit.com" }, Array.Empty<string>(), Ends, urls: packed).Ok);
            var ini = Load();
            Assert.Equal(packed, ini.GetKeyValue("Slot1", "UrlPatterns"));
            Assert.Contains("Slot1.UrlPatterns=reddit.com/r/*|x.com/home\n", MonkMode.Blocker.CanonicalFromIni(ini));
        }
        finally { Wipe(); }
    }

    // ---- 8. P29: a PENDING slot stores StartAt + a DURATION, never an absolute end ----

    [Fact]
    public void PendingSlot_StoresStartAndDuration_NeverAnAbsoluteUntil()
    {
        Wipe();
        try
        {
            var start = DateTime.Now.AddHours(3);
            var end = start.AddHours(2);
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "",
                                             start, end, false);
            Assert.True(r.Ok);

            var ini = Load();
            // Until is EMPTY: storing an absolute end would under-block after downtime (the
            // wall clock moves on while the machine is off). The SERVICE computes it at
            // activation from StartAt + DurationSeconds.
            Assert.Equal("", ini.GetKeyValue("Slot1", "Until"));
            Assert.Equal("7200", ini.GetKeyValue("Slot1", "DurationSeconds"));
            Assert.NotEqual("", ini.GetKeyValue("Slot1", "StartAt"));

            var cli = MonkMode.Blocker.CanonicalFromIni(ini);
            Assert.Contains("Slot1.Until=\n", cli);
            Assert.Contains("Slot1.DurationSeconds=7200\n", cli);
            Assert.Contains("Slot1.StartAt=" + start.ToString(EnCa) + "\n", cli);

            // P43: a PENDING slot counts toward ArmedCount (it holds the guardian alive with
            // no block open of its own), and the guard horizon reaches its computed END.
            Assert.Equal("1", ini.GetKeyValue("Guard", "ArmedCount"));
            Assert.Contains("GuardHoldUntil=" + end.ToString(EnCa) + "\n", cli);
        }
        finally { Wipe(); }
    }

    // ---- 9. P43: Guard.HoldUntil is EXTEND-ONLY ----

    [Fact]
    public void GuardHoldUntil_IsExtendOnly_AcrossArms()
    {
        Wipe();
        try
        {
            var far = new DateTime(2028, 1, 1, 0, 0, 0);
            Assert.True(Arm(new[] { "a.com" }, Array.Empty<string>(), far).Ok);
            var afterLong = MonkMode.Blocker.CanonicalFromIni(Load());
            Assert.Contains("GuardHoldUntil=" + far.ToString(EnCa) + "\n", afterLong);

            // A SHORTER second block must not pull the guard (or the v9 mirror end) back -
            // a stale scalar may only ever over-guard.
            Assert.True(Arm(new[] { "b.com" }, Array.Empty<string>(), DateTime.Now.AddHours(1)).Ok);
            Assert.Contains("GuardHoldUntil=" + far.ToString(EnCa) + "\n", MonkMode.Blocker.CanonicalFromIni(Load()));
            Assert.Equal(far, MonkMode.Blocker.ActiveBlockEnd());
        }
        finally { Wipe(); }
    }

    // ---- 10. the v9 mirror is the OVER-blocking projection, per dimension ----

    [Fact]
    public void V9Mirror_IsTheOverBlockingProjection_OfEverySlot()
    {
        Wipe();
        try
        {
            Assert.True(Arm(new[] { "a.com" }, new[] { "chrome.exe" }, Ends, coolOff: 3600).Ok);
            Assert.True(Arm(new[] { "b.com" }, new[] { "brave.exe" }, Ends.AddMinutes(5),
                            committed: true, coolOff: 7200, allSession: true).Ok);

            var ini = Load();
            // Sites/apps UNION - a v9 reader enforces both blocks' lists.
            Assert.Equal("a.com;b.com;", ini.GetKeyValue("User", "CustomSites"));
            // "committed" as soon as ANY slot is: the strictest slot sets the exit policy.
            Assert.Equal("yes", ini.GetKeyValue("Commit", "Committed"));
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());   // ...and the union is MAC-covered
            // The LONGEST configured cooling-off wins (the wait may only ever be extended).
            Assert.Equal("7200", ini.GetKeyValue("CoolOff", "Duration"));
            // Widen-only all-session kill.
            Assert.Equal("yes", ini.GetKeyValue("Process", "AllSession"));
            Assert.True(monkmode.Service1.AllSessionKillArmed(ini.GetKeyValue("Process", "AllSession")));
            // The LATEST end.
            Assert.Equal(Ends.AddMinutes(5), MonkMode.Blocker.ActiveBlockEnd());
        }
        finally { Wipe(); }
    }
    // ---- F76: arming after an idle gap must not inherit a stale monotonic frame ----
    //
    // THE BUG (25/08/2026, caught live by the reboot drill). [Time] HighWater advances
    // ONLY while the service is alive, and the service is stopped/absent between blocks,
    // so a stored mark is stale by exactly the machine's idle gap. An IMMEDIATE arm writes
    // Until off the WALL clock, while expiry is Until <= HighWater - so a 45-minute block
    // armed at 14:06 against a mark left at ~12:02 by the previous block was still
    // enforcing at 15:16 and tracking to lift at ~16:55, over-running by the whole ~2h gap.
    // Fail-CLOSED (it over-blocks, never early), but it makes every block after the first
    // one on a machine lie about when it ends.

    [Theory]
    // (slotCount, [Schedule] ActiveUntil) => re-seed the frame?
    [InlineData(0, "", true)]          // the F76 case: nothing armed, nothing anchored
    [InlineData(0, "   ", true)]       // whitespace is not a window
    // NOT WIDENED: with any slot armed the mark is that slot's expiry clock. Re-seeding it
    // forward would lift a RUNNING block early - the exact guarantee Arm2 pins.
    [InlineData(1, "", false)]
    [InlineData(8, "", false)]
    // An OPEN scheduled window closes on ActiveUntil <= HighWater and survives
    // `schedule --clear`, so it reaches this path with zero slots. Moving the mark forward
    // under it would close it EARLY.
    [InlineData(0, "2027-03-01 12:00:00 p.m.", false)]
    [InlineData(0, "not-a-date", false)]   // fail-closed: unreadable still means "maybe open"
    public void ShouldReseedMonotonicFrame_OnlyWhenNothingIsAnchoredToIt(
        int slotCount, string activeUntil, bool expected)
        => Assert.Equal(expected, MonkMode.Blocker.ShouldReseedMonotonicFrame(slotCount, activeUntil));

    [Fact]
    public void ArmAfterTeardown_ReseedsTheMonotonicFrame()
    {
        Wipe();
        try
        {
            Assert.True(Arm(new[] { "reddit.com" }, Array.Empty<string>(), Ends).Ok);

            // Tear down to a legitimately EMPTY, MAC-valid v10 config - the state a machine
            // sits in between blocks, and the state `unblock --force` leaves behind.
            MonkMode.Blocker.PersistZeroSlotConfig(blockIsActive: false);
            var idle = Load();
            Assert.Equal("0", idle.GetKeyValue("Slots", "SlotCount"));
            var staleHighWater = idle.GetKeyValue("Time", "HighWater");
            var staleNow = idle.GetKeyValue("CurrentTime", "Now");
            Assert.NotEqual("", staleHighWater);

            // Past a whole second: the stored value is second-resolution en-CA, so without
            // this a re-seed could reproduce the same ciphertext and the test would pass
            // for the wrong reason (the same trap Arm2 guards against in the other direction).
            Thread.Sleep(1100);

            var second = Arm(new[] { "x.com" }, Array.Empty<string>(), Ends.AddHours(1));
            Assert.True(second.Ok);
            Assert.False(second.FreshRewrite);       // still an APPEND, not a rewrite...
            Assert.Equal(2, second.Id);              // ...so P17 holds: the id does NOT restart

            var after = Load();
            // The frame moved forward with the arm instead of inheriting the idle gap.
            Assert.NotEqual(staleHighWater, after.GetKeyValue("Time", "HighWater"));
            Assert.NotEqual(staleNow, after.GetKeyValue("CurrentTime", "Now"));
        }
        finally { Wipe(); }
    }
}

// P26/P27/P28: the --start grammar. PURE (the reference "now" is injected), so no ini, no
// service, no clock dependence - just the parse and the arm-time refusals.
public class StartGrammarTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 9, 0, 0);

    [Theory]
    // A token that parses as a DURATION is always a DELAY from now - identical grammar to
    // --for, with a leading "+" accepted and stripped.
    [InlineData("+90m", 90)]
    [InlineData("90m", 90)]
    [InlineData("2h", 120)]
    [InlineData("1d12h", 2160)]
    [InlineData("+ 45", 45)]      // a BARE number is minutes, exactly as --for reads it
    [InlineData("45", 45)]
    public void Duration_Forms_AreDelaysFromNow(string token, int minutes)
    {
        DateTime start = default; string err = "";
        Assert.True(MonkMode.Program.TryParseStart(token, Now, ref start, ref err));
        Assert.Equal(Now.AddMinutes(minutes), start);
        Assert.Equal("", err);
    }

    [Fact]
    public void Absolute_Form_IsParsedWhenItIsNotADuration()
    {
        DateTime start = default; string err = "";
        Assert.True(MonkMode.Program.TryParseStart("2026-08-10 07:00", Now, ref start, ref err));
        Assert.Equal(new DateTime(2026, 8, 10, 7, 0, 0), start);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tomorrow")]
    [InlineData("soon-ish")]
    [InlineData("+")]
    public void Unparseable_IsRefused_NeverGuessed(string token)
    {
        // Fail-closed on input: a misread start would arm a block at the wrong time, so the
        // CLI refuses (exit 1, before any side effect) and names what it accepts.
        DateTime start = default; string err = "";
        Assert.False(MonkMode.Program.TryParseStart(token, Now, ref start, ref err));
        Assert.Equal(DateTime.MinValue, start);
        Assert.Contains("Could not understand --start", err);
        Assert.Contains("+90m", err);
    }

    [Fact]
    public void PastStart_ParsesAsThePastMoment_SoDoBlockCanStartNow()
    {
        // P28: the PARSER returns the past moment as given; DoBlock is what turns "<= now"
        // into an immediate ACTIVE arm (never a PENDING slot waiting on a moment that has
        // already gone). Pinned here so a future edit can't quietly make the parser refuse.
        DateTime start = default; string err = "";
        Assert.True(MonkMode.Program.TryParseStart("2020-01-01 07:00", Now, ref start, ref err));
        Assert.True(start < Now);
    }

    [Fact]
    public void MaxStartDelay_IsThirtyDays()
    {
        // P27: an unbounded PENDING slot would squat one of the 8 slots indefinitely.
        Assert.Equal(30, MonkMode.Program.MaxStartDelayDays);
        DateTime start = default; string err = "";
        Assert.True(MonkMode.Program.TryParseStart("31d", Now, ref start, ref err));
        Assert.True(start > Now.AddDays(MonkMode.Program.MaxStartDelayDays));   // DoBlock refuses this
    }

}
