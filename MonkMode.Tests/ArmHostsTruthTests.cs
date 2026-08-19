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

// MonkMode.Tests - v1.1 FX5: THE HOSTS BLOCK MUST NEVER SHRINK BEHIND AN ARMED SLOT.
//
// Three P1s from the 19/08 bug-hunt, all of them the same sin in different places - an
// UNAUTHENTICATED or UNVERIFIED source being allowed to narrow what is blocked:
//
//   F5. `WriteArmHostsBlock` unioned the new arm's entries onto the SNAPSHOT FILE only,
//       read inside a swallowing Try. Delete or lock monkmode_hosts.block and the union
//       collapsed to the new slot's entries, which the writer then REPLACED the marker
//       block with - arming a second block silently unblocked the first one's sites, and
//       with the service registered-but-stopped (this machine's normal between-blocks
//       state) that unblock was permanent. The arm now unions THREE sources - snapshot,
//       MAC-verified CONFIG TRUTH, and this arm's own entries - so any one of them
//       failing can only lose what the other two still supply.
//
//   F6. `DoBlock` armed and MAC-stamped the slot BEFORE the hosts write, whose outer path
//       could throw. A `block --for 30d --commit` against a momentarily locked hosts file
//       exited at Main's catch BEFORE the one-time partner-code print: the code is minted
//       once and stored only as a salted hash, so a committed 30-day block lost its only
//       early exit. The write is now guarded, and the guard's contract is the pair of
//       invariants pinned below - the code always reaches the screen, and the B2 repair
//       source is always already wide when the hosts write fails.
//
//   F2. `RetireSlotAt` re-read the config it had just saved and passed macValid:=True to
//       the truth computation without re-validating. A forged config landing in that
//       window narrowed the SNAPSHOT - the MAC-independent source every self-heal reads -
//       so a still-armed slot's sites stayed unblocked for the whole life of the frozen
//       block. The re-read is now re-validated (the PersistSlotFieldAt discipline).
//
// Plus the FX4 verifier's fold-in: ArmSlot judged `urlPatterns` Trim()ed and stored it
// RAW, so a direct caller passing an edge newline passed the control-character backstop
// and still bricked the config (the F30 IniFile round-trip asymmetry).
//
// Fences honoured: every write lands in the test-bin ini/backup or a GUID temp directory
// under the test bin. Never the real hosts file, the registry, the SCM or a deployed
// config; nothing is armed for real and no binary is executed.

namespace MonkMode.Tests;

// ---- 1. F5: the arm-time union reads CONFIG TRUTH, not just the snapshot file ----

[Collection("CliIniWriters")]
public class ArmHostsConfigTruthTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);
    private const string Marker = "#### MonkMode Entries ####";

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "fx5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Drop(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static MonkMode.Blocker.ArmResult Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false);

    // The expected block, built with the REAL synthesiser rather than a hand-written
    // approximation: BuildHostsEntries emits the www./m./web./mobile. mirror variants per
    // domain, so a made-up two-line block would pin a geometry that never occurs.
    private static string Expected(params string[] domains)
        => MonkMode.Blocker.UnionHostsBlock("", MonkMode.Blocker.BuildHostsEntries(domains));

    // What the arm assembles into hosts: the user's own content with its tail normalised,
    // then CRLF, then our marker block. Every fixture seeds one user line, both because it
    // is the realistic shape and because it is the no-data-loss fence under test.
    private const string UserLine = "127.0.0.1 mine.example";
    private static string ExpectedHosts(params string[] domains) => UserLine + "\r\n" + Expected(domains);

    [Fact]
    public void SecondArm_WithTheSnapshotFileMissing_StillCoversTheFirstSlotsSites()
    {
        // The exact F5 repro. Slot a is armed; the snapshot has been deleted; arming slot b
        // used to rewrite the marker block with ONLY b's entries.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            File.WriteAllText(hosts, UserLine + "\r\n");
            Assert.False(File.Exists(snapshot));

            MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                  new[] { "b.com" }, freshArm: false);

            // Exact value, both files: config truth is the union over BOTH slots, in slot order.
            Assert.Equal(Expected("a.com", "b.com"), File.ReadAllText(snapshot));
            // ...and the user's own line survives verbatim above it (the paramount fence).
            Assert.Equal(ExpectedHosts("a.com", "b.com"), File.ReadAllText(hosts));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void SecondArm_WithTheSnapshotLockedAgainstReading_StillCoversTheFirstSlotsSites()
    {
        // The other half of F5: not a missing snapshot but an unreadable one (a sharing
        // violation). The read throws into the swallowing Try exactly as before - what has
        // changed is that "" from that source no longer decides the union on its own.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            File.WriteAllText(hosts, UserLine + "\r\n");
            File.WriteAllText(snapshot, Expected("a.com"));

            using (File.Open(snapshot, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                      new[] { "b.com" }, freshArm: false);
            }

            // The snapshot write failed too (still locked), so the repair source is the older
            // a.com-only copy - but HOSTS, the thing that actually blocks, covers both.
            Assert.Equal(ExpectedHosts("a.com", "b.com"), File.ReadAllText(hosts));
            Assert.Equal(Expected("a.com"), File.ReadAllText(snapshot));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void SecondArm_WithAHealthySnapshot_IsUnchangedByFx5_AndDoesNotDuplicateEntries()
    {
        // The no-regression pin: with the snapshot intact, config truth adds nothing new and
        // the result is byte-identical to the pre-FX5 union (dedup is first-occurrence).
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            File.WriteAllText(hosts, UserLine + "\r\n");
            File.WriteAllText(snapshot, Expected("a.com"));

            MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                  new[] { "b.com" }, freshArm: false);

            Assert.Equal(Expected("a.com", "b.com"), File.ReadAllText(snapshot));
            Assert.Equal(ExpectedHosts("a.com", "b.com"), File.ReadAllText(hosts));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void FreshArm_StillDiscardsTheStaleSnapshot_AndConfigTruthDoesNotResurrectIt()
    {
        // A fresh rewrite discarded the previous config, so its snapshot is stale and must
        // NOT be merged in. Config truth is that same fresh config - this arm's slot and
        // nothing else - so the FX5 source cannot smuggle the old block back either.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("new.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            File.WriteAllText(hosts, UserLine + "\r\n");
            File.WriteAllText(snapshot, Expected("stale.com"));

            MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                  new[] { "new.com" }, freshArm: true);

            Assert.Equal(Expected("new.com"), File.ReadAllText(snapshot));
            Assert.Equal(ExpectedHosts("new.com"), File.ReadAllText(hosts));
            Assert.DoesNotContain("stale.com", File.ReadAllText(hosts), StringComparison.Ordinal);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void AnAppsOnlySecondArm_NowRepairsTheHostsBlockFromConfigTruth()
    {
        // Second-order F5 case: an apps-only arm has no entries of its own, so pre-FX5 a
        // missing snapshot made it write a marker block with NOTHING under it - unblocking
        // the armed site slot. Config truth now supplies the entries.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(MonkMode.Blocker.ArmSlot(Array.Empty<string>(), new[] { "chrome.exe" }, "", null, Ends, false).Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            File.WriteAllText(hosts, UserLine + "\r\n");

            MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                  Array.Empty<string>(), freshArm: false);

            Assert.Equal(ExpectedHosts("a.com"), File.ReadAllText(hosts));
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void ConfigTruthEntries_AreEmptyForAnUnverifiableConfig_SoTheyCanOnlyEverWiden()
    {
        // The MAC gate on the new source, asserted directly: a tampered config contributes
        // NOTHING, which leaves the snapshot-plus-new-entries union exactly as it was. The
        // new source can only ever add, never decide.
        Wipe();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.Equal(MonkMode.Blocker.BuildHostsEntries(new[] { "a.com" }),
                         MonkMode.Blocker.ConfigTruthHostsEntriesAt(MonkMode.Blocker.IniPath()));

            var ini = new monkmode.IniFile();
            ini.Load(MonkMode.Blocker.IniPath());
            ini.SetKeyValue("Slot1", "Sites", "harmless.example;");   // raw edit, no re-stamp
            ini.Save(MonkMode.Blocker.IniPath());
            Assert.Equal("", MonkMode.Blocker.ConfigTruthHostsEntriesAt(MonkMode.Blocker.IniPath()));

            // An absent config is the same answer, and neither case throws.
            File.Delete(MonkMode.Blocker.IniPath());
            Assert.Equal("", MonkMode.Blocker.ConfigTruthHostsEntriesAt(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }
}

// ---- 2. F6: a failed hosts write must not swallow the partner code ----

[Collection("CliIniWriters")]
public class ArmHostsWriteGuardTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "fx5g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Drop(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static MonkMode.Blocker.ArmResult Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false);

    private const string UserLine = "127.0.0.1 mine.example";

    private static string Expected(params string[] domains)
        => MonkMode.Blocker.UnionHostsBlock("", MonkMode.Blocker.BuildHostsEntries(domains));

    [Fact]
    public void TheUnguardedWriter_StillThrowsOnAHostsFailure_WhichIsWhyTheGuardExists()
    {
        // Half one of the F6 story, stated so it cannot drift: the writer does NOT swallow
        // (a writer that lied about landing would be worse than the bug). DoBlock used to
        // call this one directly, which is exactly how the throw reached Main's catch.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");

            MonkMode.AtomicHosts.RenameHookForTests = (_, _) => throw new IOException("simulated AV lock");
            try
            {
                Assert.Throws<IOException>(() =>
                    MonkMode.Blocker.WriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                          new[] { "a.com" }, freshArm: false));
            }
            finally { MonkMode.AtomicHosts.RenameHookForTests = null; }
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void AFailedHostsWrite_ReportsAndDoesNotThrow_SoThePartnerCodePrintIsAlwaysReached()
    {
        // Invariant one: nothing between the arm and the one-time partner-code print may
        // throw. The guard turns the AV-lock failure into a warning the caller prints and
        // then keeps going.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            Assert.True(Arm("b.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            var warning = "";

            MonkMode.AtomicHosts.RenameHookForTests = (_, _) => throw new IOException("simulated AV lock");
            bool ok;
            try
            {
                ok = MonkMode.Blocker.TryWriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                             new[] { "b.com" }, false, ref warning);
            }
            finally { MonkMode.AtomicHosts.RenameHookForTests = null; }

            Assert.False(ok);
            // The message must say the block IS armed - the pre-FX5 failure printed "Error"
            // and exit 2, which read as "nothing happened" while a 30-day block was running.
            Assert.Contains("the block IS armed", warning, StringComparison.Ordinal);
            Assert.Contains("simulated AV lock", warning, StringComparison.Ordinal);

            // Invariant two: the B2 repair source is ALREADY wide when the hosts write fails
            // - the snapshot is written first, and it covers BOTH armed slots - so the
            // service repairs hosts from it rather than from a narrowed copy. Hosts itself
            // was never touched (the atomic write left the target intact).
            Assert.Equal(Expected("a.com", "b.com"), File.ReadAllText(snapshot));
            Assert.False(File.Exists(hosts));
            // ...and nothing was retried away: both slots are still armed and MAC-valid.
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
            Assert.Equal(2, MonkMode.Blocker.ArmedSlotIds().Count);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void AHostsWriteThatLands_ReportsSuccessWithNoWarning()
    {
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com").Ok);
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            var warning = "not cleared";
            File.WriteAllText(hosts, UserLine + "\r\n");

            Assert.True(MonkMode.Blocker.TryWriteArmHostsBlockAt(MonkMode.Blocker.IniPath(), snapshot, hosts,
                                                                 new[] { "a.com" }, false, ref warning));
            Assert.Equal("", warning);
            Assert.Equal(UserLine + "\r\n" + Expected("a.com"), File.ReadAllText(hosts));
        }
        finally { Wipe(); Drop(dir); }
    }
}

// ---- 3. F2: the retire's re-read is re-validated before it may narrow anything ----

[Collection("CliIniWriters")]
public class RetireReReadValidationTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

    // NEVER construct a Service1 directly - construction alone starts the live 10s tick.
    private static monkmode.Service1 Svc() => TestSvc.New();

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(), MonkMode.Blocker.SnapshotPath() })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "fx5r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Drop(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private static bool Arm(string site)
        => MonkMode.Blocker.ArmSlot(new[] { site }, Array.Empty<string>(), "", null, Ends, false).Ok;

    private static string Wide(params string[] domains)
        => "#### MonkMode Entries ####\r\n" + monkmode.Service1.BuildHostsEntries(new List<string>(domains));

    private static int SlotCount()
    {
        var ini = new monkmode.IniFile();
        ini.Load(MonkMode.Blocker.IniPath());
        return monkmode.ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"));
    }

    // Blank the Sites of the slot now at `position` WITHOUT re-stamping: a raw forged write,
    // the shape an attacker loops against the config, and MAC-invalid from that moment.
    private static void Forge(string iniPath, int position)
    {
        var ini = new monkmode.IniFile();
        ini.Load(iniPath);
        ini.SetKeyValue("Slot" + position, "Sites", "");
        ini.Save(iniPath);
    }

    [Fact]
    public void RetireSlot_ForgedConfigInTheReReadWindow_LeavesTheSnapshotAndHostsWIDE()
    {
        // The exact F2 repro. Three slots; slot 1 retires; a forged, MAC-INVALID config
        // lands between the retire's own Save and its re-read, blanking a SURVIVING slot's
        // Sites. Pre-fix, macValid:=True was hardcoded: truthSites came back narrowed to
        // b.com and the SNAPSHOT - the MAC-independent source every self-heal reads - was
        // rewritten without c.com, so c.com stayed unblocked for the whole life of the
        // frozen block. Post-fix the re-read must verify before it may narrow anything.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com"));
            Assert.True(Arm("b.com"));
            Assert.True(Arm("c.com"));
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            var wide = Wide("a.com", "b.com", "c.com");
            File.WriteAllText(snapshot, wide);
            File.WriteAllText(hosts, wide);

            // After the compaction the survivors are b (Slot1) and c (Slot2); the forgery
            // blanks c's sites, so a TRUSTED re-read would drop c.com from truth.
            monkmode.Service1.RetireReloadHookForTests = path => Forge(path, 2);
            bool retired;
            try { retired = Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshot, hosts, "1"); }
            finally { monkmode.Service1.RetireReloadHookForTests = null; }

            // The CONFIG retire genuinely landed - that half was verified and honest.
            Assert.True(retired);
            Assert.Equal(2, SlotCount());
            // ...and NOTHING downstream was narrowed on the forgery's authority. Both files
            // are byte-identical to the wide block: over-block, the documented "crash
            // between (1) and (2)" state, which the next valid tick converges.
            Assert.Equal(wide, File.ReadAllText(snapshot));
            Assert.Equal(wide, File.ReadAllText(hosts));
            Assert.Contains("c.com", File.ReadAllText(snapshot), StringComparison.Ordinal);
        }
        finally { Wipe(); Drop(dir); }
    }

    [Fact]
    public void RetireSlot_WithAValidReRead_StillNarrowsToConfigTruth()
    {
        // The control for the pin above, so the gate cannot be "return early always": with
        // nothing forged, the same retire recomputes both files to config truth exactly.
        Wipe();
        var dir = TempDir();
        try
        {
            Assert.True(Arm("a.com"));
            Assert.True(Arm("b.com"));
            Assert.True(Arm("c.com"));
            var snapshot = Path.Combine(dir, "monkmode_hosts.block");
            var hosts = Path.Combine(dir, "hosts");
            File.WriteAllText(snapshot, Wide("a.com", "b.com", "c.com"));
            File.WriteAllText(hosts, Wide("a.com", "b.com", "c.com"));

            Assert.True(Svc().RetireSlotAt(MonkMode.Blocker.IniPath(), snapshot, hosts, "1"));

            Assert.Equal(Wide("b.com", "c.com"), File.ReadAllText(snapshot));
            var rewritten = File.ReadAllText(hosts);
            Assert.DoesNotContain("a.com", rewritten, StringComparison.Ordinal);
            Assert.Contains("127.0.0.1 b.com", rewritten, StringComparison.Ordinal);
            Assert.Contains("127.0.0.1 c.com", rewritten, StringComparison.Ordinal);
        }
        finally { Wipe(); Drop(dir); }
    }
}

// ---- 4. the FX4 verifier's fold-in: what is JUDGED trimmed must be STORED trimmed ----

[Collection("CliIniWriters")]
public class ArmSlotStoresTrimmedValuesTests
{
    private static readonly DateTime Ends = new(2027, 3, 1, 12, 0, 0);

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

    [Theory]
    [InlineData("*/watch*\n")]
    [InlineData("\n*/watch*")]
    [InlineData("*/watch*\r\n")]
    [InlineData("\t*/watch*\t")]
    [InlineData("  */watch*  ")]
    public void ArmSlot_WithEdgeWhitespaceOnTheUrlPatterns_StoresThemTrimmed_AndTheConfigStaysUsable(string urls)
    {
        // TryRejectControlChars judges each value TRIMMED (an edge newline off a shell paste
        // is not a brick, an interior one is). The store had to agree: writing the raw value
        // put an LF inside an ini line, which Save writes as one line and Load reads back as
        // two - macValid False FOREVER, freezing every other armed block. End to end through
        // the REAL ArmSlot, because the pure checker cannot see the asymmetry.
        Wipe();
        try
        {
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com\n" }, new[] { "chrome.exe\t" }, urls, null, Ends, false);
            Assert.True(r.Ok);
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());

            var ini = Reload();
            Assert.Equal("*/watch*", ini.GetKeyValue("Slot1", "UrlPatterns"));
            Assert.Equal("reddit.com;", ini.GetKeyValue("Slot1", "Sites"));
            Assert.Equal("chrome.exe;", ini.GetKeyValue("Slot1", "Apps"));

            // The real cost of the brick was the NEXT arm, so pin that it still works: a
            // second slot appends beside this one and the config is still MAC-valid.
            Assert.True(MonkMode.Blocker.ArmSlot(new[] { "x.com" }, Array.Empty<string>(), "", null, Ends, false).Ok);
            Assert.True(MonkMode.Blocker.ConfigIsMacValid());
            Assert.Equal(2, MonkMode.Blocker.ArmedSlotIds().Count);
        }
        finally { Wipe(); }
    }

    [Fact]
    public void ArmSlot_WithAnInteriorControlCharInTheUrlPatterns_IsStillRefused()
    {
        // The trim must not become a laundering step: an INTERIOR control character is the
        // one that actually splits the stored line, and it is still refused outright.
        Wipe();
        try
        {
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, Array.Empty<string>(), "*/wat\nch*", null, Ends, false);
            Assert.Equal(MonkMode.Blocker.ArmOutcome.BadInput, r.Outcome);
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));
        }
        finally { Wipe(); }
    }
}
