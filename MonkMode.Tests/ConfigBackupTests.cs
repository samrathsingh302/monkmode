// MonkMode.Tests - C1b (R8): the MAC-covered config shadow backup.
//
// WHAT THIS CLOSES: the service's OnStart + tick corrupt/short-ini paths used to
// rewrite an UNSTAMPED default block (WriteDefaultBlock), which the readers treat
// as MAC-invalid and therefore FREEZE - it holds until a manual CLI re-arm. Once
// R1 removed the unconditional escape, a corrupt config with no escape = trapped.
// C1b adds a byte-copy backup of the last-known-good config, refreshed on every
// legitimate (MAC-valid) write, so a corrupt/blanked/short primary is RESTORED to
// a real, MAC-valid config instead of frozen.
//
// WHAT IS PURE AND UNIT-TESTED HERE (no DPAPI, no registry, temp files only):
//   - ConfigBackup.CopyIfSourceValid - the no-data-loss copy gate. sourceIsValid
//     is boolean-injected (in production it is ConfigMacIsValidForIni, the live
//     DPAPI seam), so the gate is tested exhaustively with no DPAPI, exactly like
//     ConfigIntegrityTests injects a raw HMAC key into the pure MAC functions.
//   - the 2-copy parity (CLI + service) + the filename drift guard, mirroring
//     AtomicHostsTests' both-copies pin.
//   - the end-to-end MAC round-trip: a corrupt primary restored from a valid
//     backup yields a config whose canonical still validates under the same key
//     (the "block stays MAC-valid" guarantee), and the backup round-trips the
//     canonical byte-for-byte across every reader's wrapper.
//
// The live DPAPI decision that PRODUCES sourceIsValid (ConfigMacIsValidForIni) and
// the OnStart/tick/CLI wiring that calls these helpers are the smoke-tested seam,
// exactly like the B1/B2/B3/B7 live wiring - deliberately not exercised here.
//
// HARD FENCE: only temp files inside the test bin directory are touched - never
// the real hosts file, monkmode_settings.ini, the registry or the service.

using System.Globalization;
using System.IO;

namespace MonkMode.Tests;

public class ConfigBackupTests
{
    private const string Passphrase = "mm_textbox";

    // A fixed raw 32-byte test key (production is random + DPAPI-protected; the
    // pure MAC layer takes the raw key, so inject a known one and never touch
    // DPAPI - the same approach as ConfigIntegrityTests / CanonicalParityTests).
    private static readonly byte[] Key =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    };

    // A representative armed block, exactly the shape the CLI writes (encrypted
    // Until/HighWater/Now/ProcessList, plaintext CustomSites), same values as
    // CanonicalParityTests so the wrappers decrypt them identically.
    private const string UntilPlain = "2026-12-31 11:59:59 p.m.";
    private const string NowPlain = "2026-06-25 12:00:00 p.m.";
    private const string ProcListPlain = "chrome.exe;brave.exe;";
    private const string CustomSitesPlain = "reddit.com;x.com;";
    private const string HighWaterPlain = "2026-06-25 11:30:00 a.m.";

    private static string TempPath(string tag) =>
        Path.Combine(AppContext.BaseDirectory, $"cfgbackup_{tag}_{Guid.NewGuid():N}.ini");

    // Build a valid config IniFile (CLI copy) carrying a raw-key [Integrity] Mac
    // over its own canonical. No [Integrity] Key: these tests validate the MAC via
    // the pure ConfigIntegrity.ConfigMacIsValid with the raw key, never via the
    // DPAPI-unprotect path, so a DPAPI-protected key is neither needed nor faked.
    private static MonkMode.IniFile ValidConfigIni()
    {
        var s = new MonkMode.Simple3Des(Passphrase);
        var ini = new MonkMode.IniFile();
        ini.SetKeyValue("Time", "Until", s.EncryptData(UntilPlain));
        ini.SetKeyValue("Time", "HighWater", s.EncryptData(HighWaterPlain));
        ini.SetKeyValue("Time", "TimeChanging", "no");
        ini.SetKeyValue("CurrentTime", "Now", s.EncryptData(NowPlain));
        ini.SetKeyValue("Process", "List", s.EncryptData(ProcListPlain));
        ini.SetKeyValue("User", "CustomSites", CustomSitesPlain);
        ini.SetKeyValue("User", "Done", "no");
        var canonical = MonkMode.Blocker.CanonicalFromIni(ini);
        ini.SetKeyValue("Integrity", "Mac", MonkMode.ConfigIntegrity.ComputeConfigMac(canonical, Key));
        return ini;
    }

    // The two byte-identical copies of the CopyIfSourceValid gate (CLI + service),
    // as delegates - so every gate assertion below runs against both and pins them
    // in parity (mirrors AtomicHostsTests' both-copies check).
    private static readonly Func<string, string, bool, bool>[] BothCopies =
    {
        MonkMode.ConfigBackup.CopyIfSourceValid,
        monkmode.ConfigBackup.CopyIfSourceValid,
    };

    // ---- the no-data-loss copy gate (the load-bearing property) ----

    [Fact]
    public void CopyIfSourceValid_SourceInvalid_LeavesDestUntouched_ReturnsFalse()
    {
        // THE no-data-loss contract in both directions:
        //   restore (backup -> primary): a corrupt/tampered/unstamped BACKUP
        //     (sourceIsValid=false) must never overwrite the primary;
        //   refresh (primary -> backup): a corrupt PRIMARY must never overwrite the
        //     good backup.
        // Either way: sourceIsValid=false => no copy, dest byte-for-byte unchanged.
        foreach (var copy in BothCopies)
        {
            var source = TempPath("badsrc");
            var dest = TempPath("gooddest");
            try
            {
                File.WriteAllText(source, "garbage-not-a-config");
                var destContent = "# the user's own good config\r\n[Time]\r\nUntil=keep-me\r\n";
                File.WriteAllText(dest, destContent);

                var copied = copy(source, dest, /*sourceIsValid:*/ false);

                Assert.False(copied);
                Assert.Equal(destContent, File.ReadAllText(dest));   // untouched, byte-for-byte
            }
            finally
            {
                if (File.Exists(source)) File.Delete(source);
                if (File.Exists(dest)) File.Delete(dest);
            }
        }
    }

    [Fact]
    public void CopyIfSourceValid_SourceValid_ReplacesDestWithExactSourceBytes()
    {
        // The restore/refresh happy path: a known-good source (sourceIsValid=true)
        // is copied over the dest wholesale (exact bytes, no torn merge - AtomicHosts
        // owns the crash-safe temp+rename).
        foreach (var copy in BothCopies)
        {
            var source = TempPath("goodsrc");
            var dest = TempPath("corruptdest");
            try
            {
                var sourceContent = "[Time]\r\nUntil=the-real-block\r\n[Integrity]\r\nMac=abc\r\n";
                File.WriteAllText(source, sourceContent);
                File.WriteAllText(dest, "");   // a blanked/corrupt primary

                var copied = copy(source, dest, /*sourceIsValid:*/ true);

                Assert.True(copied);
                Assert.Equal(sourceContent, File.ReadAllText(dest));
                // no leftover temp sibling (the rename consumed it)
                var leftovers = Directory.GetFiles(
                    Path.GetDirectoryName(dest)!, Path.GetFileName(dest) + ".*.tmp");
                Assert.Empty(leftovers);
            }
            finally
            {
                if (File.Exists(source)) File.Delete(source);
                if (File.Exists(dest)) File.Delete(dest);
            }
        }
    }

    [Fact]
    public void CopyIfSourceValid_MissingSource_ReturnsFalse_NoThrow_DestUntouched()
    {
        // A restore attempt when the backup file does not exist: no throw, no copy,
        // the (corrupt) primary is left exactly as-is for WriteDefaultBlock to handle.
        foreach (var copy in BothCopies)
        {
            var source = TempPath("missing");   // never created
            var dest = TempPath("dest");
            try
            {
                File.WriteAllText(dest, "still-here");
                Assert.False(copy(source, dest, /*sourceIsValid:*/ true));
                Assert.Equal("still-here", File.ReadAllText(dest));
            }
            finally
            {
                if (File.Exists(dest)) File.Delete(dest);
            }
        }
    }

    // ---- filename: stable, parity across copies, matches the ini name ----

    [Fact]
    public void BackupFileName_IsStable_ParityAcrossCopies_AndMatchesIniName()
    {
        // A drift in the filename would point the CLI writer and the service restorer
        // at different files and silently defeat the restore. Pin the literal, pin
        // the two byte-identical copies equal, and pin it is <ini name> + ".bak" so a
        // future ini rename is caught.
        Assert.Equal("monkmode_settings.ini.bak", MonkMode.ConfigBackup.BackupFileName);
        Assert.Equal(MonkMode.ConfigBackup.BackupFileName, monkmode.ConfigBackup.BackupFileName);
        Assert.Equal(MonkMode.Blocker.IniName + ".bak", MonkMode.ConfigBackup.BackupFileName);
        // And the CLI's derived path lands next to the ini (same directory).
        Assert.Equal(
            Path.GetDirectoryName(MonkMode.Blocker.IniPath()),
            Path.GetDirectoryName(MonkMode.Blocker.IniBackupPath()));
    }

    // ---- end-to-end: corrupt primary restored from a valid backup stays MAC-valid ----

    [Fact]
    public void CorruptPrimary_RestoredFromValidBackup_StaysMacValid()
    {
        // The headline C1b guarantee. A backup holding a real armed config restores
        // over a corrupt primary, and the restored primary's canonical STILL
        // validates under the same key - so the block ends at its real expiry
        // instead of freezing into an unstamped default. Byte-copy is what preserves
        // the [Integrity] Mac, so no re-stamp is needed.
        var backupPath = TempPath("backup");
        var primaryPath = TempPath("primary");
        try
        {
            // A valid backup on disk, and its canonical + raw-key MAC.
            var cfg = ValidConfigIni();
            cfg.Save(backupPath);
            var canonical = MonkMode.Blocker.CanonicalFromIni(cfg);
            var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(canonical, Key);

            // A corrupt primary that could never validate (proves the restore mattered).
            File.WriteAllText(primaryPath, "totally corrupt \x00 not an ini");

            // Restore (sourceIsValid=true models "the backup's MAC verified").
            var restored = MonkMode.ConfigBackup.CopyIfSourceValid(backupPath, primaryPath, sourceIsValid: true);
            Assert.True(restored);

            // The restored primary is byte-identical to the backup...
            Assert.Equal(File.ReadAllText(backupPath), File.ReadAllText(primaryPath));

            // ...and, loaded by the SERVICE reader, its canonical validates under the
            // key => macValid would read TRUE => the block is liftable, not frozen.
            var srvIni = new monkmode.IniFile();
            srvIni.Load(primaryPath);
            var restoredCanonical = new monkmode.Service1().CanonicalFromIni(srvIni);
            Assert.Equal(canonical, restoredCanonical);
            Assert.True(monkmode.ConfigIntegrity.ConfigMacIsValid(restoredCanonical, mac, Key));
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(primaryPath)) File.Delete(primaryPath);
        }
    }

    [Fact]
    public void CorruptBackup_NeverOverwritesAGoodPrimary_WhichStaysMacValid()
    {
        // The mirror guarantee (no data loss): a corrupt backup (sourceIsValid=false,
        // as ConfigMacIsValidForIni would report) must leave a good primary exactly
        // as it was - the primary keeps validating under the key, nothing lost.
        var backupPath = TempPath("corruptbackup");
        var primaryPath = TempPath("goodprimary");
        try
        {
            var cfg = ValidConfigIni();
            cfg.Save(primaryPath);
            var canonical = MonkMode.Blocker.CanonicalFromIni(cfg);
            var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(canonical, Key);
            var primaryBefore = File.ReadAllText(primaryPath);

            File.WriteAllText(backupPath, "corrupt backup blob");

            // sourceIsValid=false => the corrupt backup is refused.
            var copied = MonkMode.ConfigBackup.CopyIfSourceValid(backupPath, primaryPath, sourceIsValid: false);
            Assert.False(copied);

            // Primary byte-for-byte unchanged, and still MAC-valid.
            Assert.Equal(primaryBefore, File.ReadAllText(primaryPath));
            var stillIni = new monkmode.IniFile();
            stillIni.Load(primaryPath);
            Assert.True(monkmode.ConfigIntegrity.ConfigMacIsValid(
                new monkmode.Service1().CanonicalFromIni(stillIni), mac, Key));
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(primaryPath)) File.Delete(primaryPath);
        }
    }

    // ---- routing guard: the corrupt-vs-usable boundary (invariant B) ----

    [Fact]
    public void PrimaryIsStructurallyUsable_GatesRecoveryOnStructureNotMac_BothCopies()
    {
        // The load-bearing B7 property: recovery (restore/default) fires ONLY on
        // STRUCTURAL corruption (< 2 sections), never on MAC validity. So a full
        // config (>= 2 sections) is "usable" - it takes the normal path and, if its
        // MAC is invalid (a TAMPER), FREEZES per B7; it is never routed to restore.
        // Pin the threshold AND that a full (e.g. 5-section, possibly tampered)
        // config is NOT treated as needs-recovery. Both byte-identical copies agree.
        foreach (var usable in new Func<int, bool>[]
                 {
                     MonkMode.ConfigBackup.PrimaryIsStructurallyUsable,
                     monkmode.ConfigBackup.PrimaryIsStructurallyUsable,
                 })
        {
            Assert.False(usable(0));   // blanked  -> recover
            Assert.False(usable(1));   // truncated -> recover
            Assert.True(usable(2));    // minimal structure -> normal path
            // A full armed config (User/Time/CurrentTime/Process/Integrity = 5) is
            // usable EVEN IF its MAC is invalid (tampered): it is NOT recovered, it
            // freezes per B7. This is the invariant-B regression guard.
            Assert.True(usable(5));
        }
    }

    // ---- the backup round-trips under the canonical, across every reader ----

    [Fact]
    public void Backup_RoundTripsUnderTheCanonical_AcrossAllReaders()
    {
        // A byte-copy backup, reloaded by each project's real CanonicalFromIni
        // wrapper, must derive the IDENTICAL canonical the CLI writer did (and the
        // raw-key MAC must validate over it). If a copy corrupted the encrypted
        // fields, a reader's decrypt would diverge and this would fail.
        var primaryPath = TempPath("primary");
        var backupPath = TempPath("backup");
        try
        {
            var cfg = ValidConfigIni();
            cfg.Save(primaryPath);
            var cliCanonical = MonkMode.Blocker.CanonicalFromIni(cfg);
            var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(cliCanonical, Key);

            // Simulate the refresh: copy the good primary to the backup.
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(primaryPath, backupPath, sourceIsValid: true));

            // Load the backup with every reader and compare canonicals + MAC.
            var cliIni = new MonkMode.IniFile();
            cliIni.Load(backupPath);
            var srvIni = new monkmode.IniFile();
            srvIni.Load(backupPath);
            var guardIni = new mm_guard.IniFile();
            guardIni.Load(backupPath);
            var notifyIni = new mm_notify.IniFile();
            notifyIni.Load(backupPath);

            var fromBackup = MonkMode.Blocker.CanonicalFromIni(cliIni);
            Assert.Equal(cliCanonical, fromBackup);
            Assert.Equal(cliCanonical, new monkmode.Service1().CanonicalFromIni(srvIni));
            Assert.Equal(cliCanonical, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(cliCanonical, new mm_notify.Form1().CanonicalFromIni(notifyIni));

            Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(fromBackup, mac, Key));
        }
        finally
        {
            if (File.Exists(primaryPath)) File.Delete(primaryPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
