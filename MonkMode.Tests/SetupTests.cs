// MonkMode.Tests - C6a: `monkmode setup` first-run onboarding preferences.
//
// C6a is the first sub-slice of C6 (setup onboarding). It records that first-run setup has happened
// in a SEPARATE, MAC-covered, CLI-only file (monkmode_setup.ini) and gates the arm paths on it:
// `block`/`schedule` refuse to arm until SetupIsComplete(), so a first block always goes through the
// accountability-model explanation. Deliberately NOT in the enforcement config (monkmode_settings.ini)
// - so C6a adds ZERO enforcement-canonical surface (no v7->v8 lockstep) and can never perturb a live
// block. C6a does NOT mint an account-level code (the lighter reconciliation of design gotcha #3: each
// `block` already mints its own per-block code, C3b); the configurable cooling-off duration + default
// blocklist/presets are deferred to C6b / D1.
//
// These tests pin the load-bearing predicate SetupIsComplete() and the WriteSetupConfig round-trip:
//   - a fresh write is MAC-valid + Done="yes" => complete; a missing file / tampered field / tampered
//     MAC / missing key / Done<>"yes" all read as NOT complete (fail-closed, the arm-gate then refuses);
//   - the partner label round-trips (incl. the empty case, the CoolOffUntil="" bare-key quirk);
//   - setup is idempotent (re-run updates the label, stays complete);
//   - setup writes ONLY the setup file - it never creates/touches the enforcement config or a block.
// The DoSetup verb + the DoBlock/DoSchedule arm-gate wiring do live console/service I/O (the smoke-only
// seam, fence: unit tests never arm a live block); their one load-bearing predicate (SetupIsComplete)
// is unit-tested here, the wiring is verifier + the CV smoke.

using System.IO;

namespace MonkMode.Tests;

// Writes the shared test-bin monkmode_setup.ini (and, in the isolation tests, checks the enforcement
// ini/backup) - serialised with the other CLI ini writers so they can't clobber each other's files.
// Fence: only the test-bin files are ever touched, never a live block/service/registry.
[Collection("CliIniWriters")]
public class SetupConfigTests
{
    private static void WipeSetup()
    {
        try { if (File.Exists(MonkMode.Blocker.SetupIniPath())) File.Delete(MonkMode.Blocker.SetupIniPath()); }
        catch { /* best-effort */ }
    }

    private static void WipeEnforcement()
    {
        try { if (File.Exists(MonkMode.Blocker.IniPath())) File.Delete(MonkMode.Blocker.IniPath()); } catch { }
        try { if (File.Exists(MonkMode.Blocker.IniBackupPath())) File.Delete(MonkMode.Blocker.IniBackupPath()); } catch { }
    }

    // Re-stamp the setup file's MAC over its (possibly hand-edited) canonical with the EXISTING key -
    // mirrors the production StampFreshSetupMac's restamp so a test can produce a VALID MAC over a
    // deliberately-altered field (e.g. Done="no"), proving SetupIsComplete gates on Done, not just the MAC.
    private static void ReStampSetup(MonkMode.IniFile ini)
    {
        var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
        ini.SetKeyValue("Integrity", "Mac",
            MonkMode.ConfigIntegrity.ComputeConfigMac(MonkMode.Blocker.SetupCanonicalFromIni(ini), key));
    }

    [Fact]
    public void FreshWrite_IsMacValidAndComplete_DoneStoredYes()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig());       // healthy DPAPI => stamps + returns true
            Assert.True(MonkMode.Blocker.SetupIsComplete());        // MAC valid AND Done="yes"

            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.Equal("yes", ini.GetKeyValue("Setup", "Done"));
            Assert.False(string.IsNullOrEmpty(ini.GetKeyValue("Integrity", "Key")));
            Assert.False(string.IsNullOrEmpty(ini.GetKeyValue("Integrity", "Mac")));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void NoSetupFile_NotComplete_NoPartner()
    {
        WipeSetup();
        try
        {
            Assert.False(MonkMode.Blocker.SetupIsComplete());       // missing file => fail-closed
            Assert.Equal("", MonkMode.Blocker.SetupPartnerLabel());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void PartnerLabel_RoundTrips_AndEmpty()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex (alex@example.com)"));
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal("Alex (alex@example.com)", MonkMode.Blocker.SetupPartnerLabel());

            // No partner: stored empty => a bare-key reload (Nothing) canonicalises identically at
            // stamp + verify (the CoolOffUntil="" pattern), so it stays MAC-valid + complete, label "".
            Assert.True(MonkMode.Blocker.WriteSetupConfig(""));
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal("", MonkMode.Blocker.SetupPartnerLabel());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void PartnerLabel_IsTrimmed()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("   Bob   "));
            Assert.Equal("Bob", MonkMode.Blocker.SetupPartnerLabel());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void ReRun_IsIdempotent_UpdatesLabel_StaysComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal("Alex", MonkMode.Blocker.SetupPartnerLabel());

            Assert.True(MonkMode.Blocker.WriteSetupConfig("Bob"));   // re-run overwrites (fresh key/MAC)
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal("Bob", MonkMode.Blocker.SetupPartnerLabel());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void TamperedField_BreaksMac_NotComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));
            Assert.True(MonkMode.Blocker.SetupIsComplete());

            // Raw-edit the partner WITHOUT re-stamping => the stored MAC no longer covers the canonical.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "Partner", "Mallory");
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());       // tamper => fail-closed
            Assert.Equal("", MonkMode.Blocker.SetupPartnerLabel()); // not complete => no label leaked
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void TamperedMac_NotComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig());
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Integrity", "Mac", "not-a-real-mac");
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void MissingIntegrityKey_NotComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig());
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Integrity", "Key", "");   // no unprotectable key => UnprotectKey null => fail-closed
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());
        }
        finally { WipeSetup(); }
    }

    [Theory]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("maybe")]
    public void DoneNotYes_UnderAValidMac_NotComplete(string done)
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig());
            // Set Done to a non-"yes" value and RE-STAMP a VALID MAC over it: proves SetupIsComplete
            // gates on the Done flag, not merely on MAC validity (a valid-MAC config with Done<>"yes"
            // is still NOT complete).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "Done", done);
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            // Sanity: the re-stamp itself is valid (so a "yes" would have been complete)...
            var check = new MonkMode.IniFile(); check.Load(MonkMode.Blocker.SetupIniPath());
            Assert.Equal(done, check.GetKeyValue("Setup", "Done") ?? "");
            // ...yet Done<>"yes" => NOT complete.
            Assert.False(MonkMode.Blocker.SetupIsComplete());

            // Positive control (makes this test self-contained): the SAME re-stamp path with
            // Done="yes" IS complete - so the False above is the Done gate, NOT a broken MAC.
            ini.SetKeyValue("Setup", "Done", "yes");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());
            Assert.True(MonkMode.Blocker.SetupIsComplete());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void DoneYes_IsCaseInsensitive()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig());
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "Done", "YES");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.True(MonkMode.Blocker.SetupIsComplete());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void WriteSetupConfig_TouchesOnlyTheSetupFile_NeverTheEnforcementConfigOrABlock()
    {
        // THE isolation guarantee: setup never creates/mutates the enforcement config, its backup, or a
        // block - so running `setup` any time (incl. re-running) can't perturb a live block.
        WipeSetup();
        WipeEnforcement();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));

            Assert.True(File.Exists(MonkMode.Blocker.SetupIniPath()));           // setup file written
            Assert.False(File.Exists(MonkMode.Blocker.IniPath()));              // enforcement config NOT created
            Assert.False(File.Exists(MonkMode.Blocker.IniBackupPath()));        // its backup NOT created
            Assert.False(MonkMode.Blocker.ScheduleIsArmed());                   // no schedule armed by setup
        }
        finally { WipeSetup(); WipeEnforcement(); }
    }

    [Fact]
    public void SetupAndBlockAreIndependent_AnArmedBlockDoesNotImplySetup()
    {
        // The two files are independent: an armed enforcement block does NOT make setup "complete"
        // (setup reads ONLY the setup file), and vice versa.
        WipeSetup();
        WipeEnforcement();
        try
        {
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, System.Array.Empty<string>(),
                new System.DateTime(2027, 1, 1, 0, 0, 0));
            Assert.False(MonkMode.Blocker.SetupIsComplete());   // a block exists, but setup has NOT run

            Assert.True(MonkMode.Blocker.WriteSetupConfig());
            Assert.True(MonkMode.Blocker.SetupIsComplete());    // now setup is complete, block untouched
        }
        finally { WipeSetup(); WipeEnforcement(); }
    }
}
