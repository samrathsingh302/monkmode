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

    // C6c: stamp [Integrity] Mac over an ARBITRARY canonical with the existing key - lets a test
    // forge a MAC over the OLD s1 canonical (Done + Partner, no CoolOffSeconds line) to prove the
    // s1->s2 upgrade freeze, exactly like the enforcement ForwardMigration test hand-builds the old
    // canonical literal.
    private static void StampSetupOverCanonical(MonkMode.IniFile ini, string canonical)
    {
        var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
        ini.SetKeyValue("Integrity", "Mac", MonkMode.ConfigIntegrity.ComputeConfigMac(canonical, key));
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

    // ---- C6c: the account-DEFAULT cooling-off duration ([Setup] CoolOffSeconds, s1->s2) ----
    //
    // C6c stores an account-level default cooling-off wait on the CLI-only setup file that every later
    // `block` inherits when it gives no --cooloff of its own. It bumps the setup canonical s1->s2 (a new
    // CoolOffSeconds field appended last), so an old s1 file freezes under s2 code and forces a `setup`
    // re-run - the same fail-closed upgrade rule the enforcement v-bumps use. The load-bearing property:
    // the stored default is MAC-covered, and SetupDefaultCoolOffSeconds fail-closes to 0 (= the service
    // floor, never a shorter value) on any tamper / incomplete setup / non-positive / unparseable value -
    // so a forged default can only ever LOSE the extension, never shorten cooling-off below the floor.
    // (The DoBlock inherit + DoSetup --cooloff verb wiring is smoke-tested; the seam is pinned below.)

    [Fact]
    public void SetupSchemaVersion_IsS4_TheD2bBump()
    {
        // A loud pin on the setup-file schema tag: C6c bumped it s1->s2 (CoolOffSeconds), D1b s2->s3
        // (DefaultSites), D2b s3->s4 (DefaultApps). A future bump is one deliberate edit here, and this
        // stops the freeze tests' "s2"/"s3" literals silently matching an un-bumped constant.
        Assert.Equal("s4", MonkMode.Blocker.SetupSchemaVersion);
    }

    [Fact]
    public void FreshWrite_WithCoolOffDefault_RoundTripsMacCovered()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 7200));
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(7200L, MonkMode.Blocker.SetupDefaultCoolOffSeconds());

            // Stored as plaintext seconds under [Setup] CoolOffSeconds (MAC-covered, like Partner).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.Equal("7200", ini.GetKeyValue("Setup", "CoolOffSeconds"));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void NoCoolOffDefault_Yields0_AndStaysComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));      // no --cooloff default given
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(0L, MonkMode.Blocker.SetupDefaultCoolOffSeconds());

            // Written ONLY when > 0 => the key is absent (IsNullOrEmpty covers absent "" / bare Nothing).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.True(string.IsNullOrEmpty(ini.GetKeyValue("Setup", "CoolOffSeconds")));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void TamperedCoolOffDefault_BreaksMac_FallsBackTo0_NeverTheShortenedValue()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 7200));
            Assert.Equal(7200L, MonkMode.Blocker.SetupDefaultCoolOffSeconds());

            // Raw-edit the stored default DOWN to 60s WITHOUT re-stamping: the MAC no longer covers the
            // canonical => the whole setup file reads as incomplete => the inherited default is 0 (the
            // service floor), NEVER the attacker's shortened 60. The tamper can only lose the extension.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "CoolOffSeconds", "60");
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());               // tamper => fail-closed
            Assert.Equal(0L, MonkMode.Blocker.SetupDefaultCoolOffSeconds()); // 0 = floor, NOT 60
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void CoolOffDefault_IsGatedOnCompleteness_IncompleteSetupYields0()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 7200));

            // Flip Done to "no" and RE-STAMP a VALID MAC over it: the MAC is valid and CoolOffSeconds is
            // intact (7200), but setup is NOT complete (Done<>"yes") => the default reads as 0. Proves
            // SetupDefaultCoolOffSeconds gates on completeness (like SetupPartnerLabel) - an incomplete
            // setup file never leaks an inheritable default.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "Done", "no");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(0L, MonkMode.Blocker.SetupDefaultCoolOffSeconds());
        }
        finally { WipeSetup(); }
    }

    [Theory]
    [InlineData("0")]         // non-positive: an attacker-set 0 can't shorten cooling-off
    [InlineData("-5")]
    [InlineData("garbage")]
    [InlineData("2h")]        // NOT raw seconds - only a plain integer is ever stored/accepted
    [InlineData("99999999999999999999999")]  // overflow => TryParse fails => 0
    [InlineData("31536001")]  // 365d + 1s: above MaxCoolOffSeconds => re-clamped to 0 (fail-safe cap)
    public void UnusableCoolOffDefault_UnderAValidMac_Yields0(string stored)
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 7200));

            // A VALID-MAC, complete setup file whose CoolOffSeconds is non-positive / unparseable /
            // above the 365d cap: none can shorten cooling-off, and the over-cap one can't overflow the
            // service tick either - the reader returns 0 (=> the service floor). Re-stamp so the MAC
            // stays valid + Done stays "yes", isolating the parse/cap fail-safe from the MAC/complete
            // gates (SetupIsComplete True below is the positive control: a good value WOULD be returned).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "CoolOffSeconds", stored);
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.True(MonkMode.Blocker.SetupIsComplete());                // MAC valid + Done=yes...
            Assert.Equal(0L, MonkMode.Blocker.SetupDefaultCoolOffSeconds()); // ...yet the value yields 0
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void SchemaBump_S1MacUnderS2Code_FreezesSetup_ForcesReRun()
    {
        // The C6c instance of the setup-file upgrade freeze: a file stamped under s1 (C6a: Done + Partner,
        // NO CoolOffSeconds) read under s2 code. The s2 SetupCanonicalFromIni tags "s2" and appends a
        // "CoolOffSeconds=" line, so the byte-exact s1 stamp can't validate it - setup reads NOT complete
        // (the arm-gate then makes the user re-run `setup`) and the inherited default is 0 (the floor).
        // Mirrors the enforcement v7->v8 ForwardMigration freeze; "arm/setup after upgrading" carries over.
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));   // a real, DPAPI-stamped setup file
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());

            // Forge the OLD s1 canonical (version "s1", Done + Partner only, no CoolOffSeconds line) and
            // stamp the file's MAC over IT with the existing key - exactly what a pre-C6c CLI stored.
            var s1Canonical = "s1\n" + "Done=yes\n" + "Partner=Alex\n";
            StampSetupOverCanonical(ini, s1Canonical);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            // Sanity: the forged s1 MAC IS valid over the s1 canonical (a genuine "old but honest" file,
            // not a corrupt one)...
            var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
            Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(
                s1Canonical, ini.GetKeyValue("Integrity", "Mac"), key));
            // ...yet under s2 code it does NOT validate the s2 canonical => frozen, forces re-run.
            Assert.False(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(0L, MonkMode.Blocker.SetupDefaultCoolOffSeconds());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void SetupDefault_FlowsIntoBlockCoolOffDuration_TheInheritSeam()
    {
        // The DoBlock inherit path can't arm a live block in a unit test (fence), so pin its SEAM: the
        // stored account default (SetupDefaultCoolOffSeconds) flows into WriteConfig's coolOffSeconds and
        // lands MAC-covered in the enforcement [CoolOff] Duration - exactly what `block` (no --cooloff)
        // does. An explicit `block --cooloff` instead passes its own value (CoolOffWriteConfigTests); here
        // we prove the DEFAULT is what a no-`--cooloff` block would write into the enforcement canonical.
        WipeSetup();
        WipeEnforcement();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 5400));   // account default 90m
            var inherited = MonkMode.Blocker.SetupDefaultCoolOffSeconds();
            Assert.Equal(5400L, inherited);

            // What DoBlock does when --cooloff is absent: coolOffSeconds = the inherited default.
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, System.Array.Empty<string>(),
                new System.DateTime(2027, 1, 1, 0, 0, 0), committed: false, coolOffSeconds: inherited);

            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.IniPath());
            Assert.Equal("5400", ini.GetKeyValue("CoolOff", "Duration"));
            Assert.Contains("CoolOffDuration=5400\n", MonkMode.Blocker.CanonicalFromIni(ini));
        }
        finally { WipeSetup(); WipeEnforcement(); }
    }

    // ---- D1b: the account-DEFAULT blocklist ([Setup] DefaultSites, s2->s3) ----
    //
    // D1b stores an account-level default site list on the CLI-only setup file that every later `block`
    // inherits when it names NO explicit site source (--sites/--preset/--file). It bumps the setup
    // canonical s2->s3 (a new DefaultSites field appended last), so an old s2 file freezes under s3 code
    // and forces a `setup` re-run - the same fail-closed upgrade rule C6c/the enforcement v-bumps use.
    // Security posture (like D1a presets): PURE INPUT sugar. The default only ever feeds a NEW arm - it
    // can neither lift nor shorten a live block - so a forged/added default just over-blocks a new arm
    // (the user sees the armed sites), and a tampered/incomplete one fail-closes to NO default. The
    // active block's sites remain MAC-covered by the v8 enforcement canonical once armed. (The DoBlock
    // inherit + DoSetup --default-sites/--default-preset verb wiring is smoke-tested; the pure steps -
    // TryBuildDefaultSites (SitePresetTests) + the reader/seam below - are pinned here.)

    [Fact]
    public void SetupCanonical_S4_Format_IsExact_DefaultAppsAppendedLast()
    {
        // The setup canonical is a SINGLE, CLI-only function - the setup file is never read by the
        // service, so unlike the 4-copy enforcement canonical there are NO cross-assembly wrappers to
        // hold parity with. The parity that matters here is FORMAT STABILITY: this literal pins the s4
        // version tag, the exact field order, the key names, and that D2b's DefaultApps is APPENDED
        // LAST (after D1b's DefaultSites), so any accidental reorder / rename / missed version bump
        // breaks loudly (the analogue of the enforcement BuildCanonical format + parity tests).
        var full = new MonkMode.IniFile();
        full.AddSection("Setup");
        full.SetKeyValue("Setup", "Done", "yes");
        full.SetKeyValue("Setup", "Partner", "Alex");
        full.SetKeyValue("Setup", "CoolOffSeconds", "7200");
        full.SetKeyValue("Setup", "DefaultSites", "reddit.com,x.com");
        full.SetKeyValue("Setup", "DefaultApps", "discord.exe,steam.exe");
        Assert.Equal(
            "s4\nDone=yes\nPartner=Alex\nCoolOffSeconds=7200\nDefaultSites=reddit.com,x.com\nDefaultApps=discord.exe,steam.exe\n",
            MonkMode.Blocker.SetupCanonicalFromIni(full));

        // The all-absent-optionals shape (only Done set) still emits every field line, each "" - the
        // round-trip pattern that lets a no-preferences setup file stay MAC-valid.
        var bare = new MonkMode.IniFile();
        bare.AddSection("Setup");
        bare.SetKeyValue("Setup", "Done", "yes");
        Assert.Equal(
            "s4\nDone=yes\nPartner=\nCoolOffSeconds=\nDefaultSites=\nDefaultApps=\n",
            MonkMode.Blocker.SetupCanonicalFromIni(bare));
    }

    [Fact]
    public void FreshWrite_WithDefaultSites_RoundTripsMacCovered()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "reddit.com,x.com"));
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(new[] { "reddit.com", "x.com" }, MonkMode.Blocker.SetupDefaultSites());

            // Stored as the raw comma-joined string under [Setup] DefaultSites (MAC-covered, like Partner).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.Equal("reddit.com,x.com", ini.GetKeyValue("Setup", "DefaultSites"));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void NoDefaultSites_YieldsEmpty_AndStaysComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));      // no default blocklist given
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Empty(MonkMode.Blocker.SetupDefaultSites());

            // Written ONLY when non-empty => the key is absent.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.True(string.IsNullOrEmpty(ini.GetKeyValue("Setup", "DefaultSites")));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void DefaultSites_Reader_SplitsTrimsAndDedupes_UnderAValidMac()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));
            // Hand-store a messy value (dupes, blanks, whitespace, ; separators) and RE-STAMP a valid
            // MAC: the reader normalises it (split on , / ; , trim, drop empties, dedupe case-insensitive).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "DefaultSites", " a.com , ; b.com;a.com , A.COM ");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(new[] { "a.com", "b.com" }, MonkMode.Blocker.SetupDefaultSites());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void TamperedDefaultSites_BreaksMac_FallsBackToEmpty()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "reddit.com,x.com"));
            Assert.Equal(new[] { "reddit.com", "x.com" }, MonkMode.Blocker.SetupDefaultSites());

            // Raw-edit the stored default (inject an extra site) WITHOUT re-stamping: the MAC no longer
            // covers the canonical => the whole setup file reads as incomplete => the inherited default
            // is EMPTY, not the tampered list. (Injecting sites would only ever over-block a NEW arm
            // anyway; this proves the tamper-evidence path fails closed to no-default.)
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "DefaultSites", "reddit.com,x.com,evil.com");
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());               // tamper => fail-closed
            Assert.Empty(MonkMode.Blocker.SetupDefaultSites());             // empty = no default, NOT the tampered list
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void DefaultSites_IsGatedOnCompleteness_IncompleteSetupYieldsEmpty()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "reddit.com,x.com"));

            // Flip Done to "no" and RE-STAMP a VALID MAC over it: the MAC is valid and DefaultSites is
            // intact, but setup is NOT complete (Done<>"yes") => the default reads as empty. Proves
            // SetupDefaultSites gates on completeness (like SetupPartnerLabel/SetupDefaultCoolOffSeconds)
            // - an incomplete setup file never leaks an inheritable default.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "Done", "no");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());
            Assert.Empty(MonkMode.Blocker.SetupDefaultSites());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void SchemaBump_S2MacUnderS3Code_FreezesSetup_ForcesReRun()
    {
        // The D1b instance of the setup-file upgrade freeze: a file stamped under s2 (C6c: Done +
        // Partner + CoolOffSeconds, NO DefaultSites) read under s3 code. The s3 SetupCanonicalFromIni
        // tags "s3" and appends a "DefaultSites=" line, so the byte-exact s2 stamp can't validate it -
        // setup reads NOT complete (the arm-gate then makes the user re-run `setup`) and the inherited
        // default is empty. Mirrors the enforcement v-bump ForwardMigration freeze + the s1->s2 test
        // above; "arm/setup after upgrading" carries over.
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 7200));   // a real, DPAPI-stamped s3 file
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());

            // Forge the OLD s2 canonical (version "s2", Done + Partner + CoolOffSeconds, NO DefaultSites
            // line) and stamp the file's MAC over IT with the existing key - exactly what a pre-D1b CLI stored.
            var s2Canonical = "s2\n" + "Done=yes\n" + "Partner=Alex\n" + "CoolOffSeconds=7200\n";
            StampSetupOverCanonical(ini, s2Canonical);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            // Sanity: the forged s2 MAC IS valid over the s2 canonical (a genuine "old but honest" file,
            // not a corrupt one)...
            var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
            Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(
                s2Canonical, ini.GetKeyValue("Integrity", "Mac"), key));
            // ...yet under s3 code it does NOT validate the s3 canonical => frozen, forces re-run.
            Assert.False(MonkMode.Blocker.SetupIsComplete());
            Assert.Empty(MonkMode.Blocker.SetupDefaultSites());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void SetupDefault_FlowsIntoBlock_TheSitesInheritSeam()
    {
        // The DoBlock inherit path can't arm a live block in a unit test (fence), so pin its SEAM: the
        // stored account default (SetupDefaultSites) flows into the SAME `domains` list WriteConfig
        // writes, landing MAC-covered in the enforcement [User] CustomSites - exactly what `block` (no
        // site source) does. An explicit `block --sites` instead passes its own list; here we prove the
        // DEFAULT is what a no-site block would enforce, identical to a hand-typed --sites list.
        WipeSetup();
        WipeEnforcement();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "reddit.com,x.com"));
            var inherited = MonkMode.Blocker.SetupDefaultSites();
            Assert.Equal(new[] { "reddit.com", "x.com" }, inherited);

            // What DoBlock does when no --sites/--preset/--file is given: domains = the inherited default.
            MonkMode.Blocker.WriteConfig(inherited, System.Array.Empty<string>(),
                new System.DateTime(2027, 1, 1, 0, 0, 0));

            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.IniPath());
            // [User] CustomSites is the PackList form (";"-joined + trailing ";"), MAC-covered by the v8
            // enforcement canonical - the default is now enforced identically to a hand-typed --sites list.
            Assert.Equal("reddit.com;x.com;", ini.GetKeyValue("User", "CustomSites"));
            Assert.Contains("reddit.com;x.com;", MonkMode.Blocker.CanonicalFromIni(ini));
        }
        finally { WipeSetup(); WipeEnforcement(); }
    }

    // ---- D2b: the account-DEFAULT app list ([Setup] DefaultApps, s3->s4) ----
    //
    // D2b is the app analogue of the D1b default blocklist: an account-level default app-kill list on
    // the CLI-only setup file that every later `block` inherits when it names NO explicit app source
    // (--apps/--app-preset). It bumps the setup canonical s3->s4 (a new DefaultApps field appended
    // last), so an old s3 file freezes under s4 code and forces a `setup` re-run. Same PURE-INPUT-sugar
    // posture as DefaultSites: the default only ever feeds a NEW arm (never lifts/shortens a live
    // block); a forged/added default over-kills a new arm (visible, safe), a tampered/incomplete one
    // fail-closes to NO default. The reader returns the raw stored tokens; .exe-normalisation happens
    // downstream in PackApps at arm time (proven by the inherit-seam test).

    [Fact]
    public void FreshWrite_WithDefaultApps_RoundTripsMacCovered()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "", "discord.exe,steam.exe"));
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(new[] { "discord.exe", "steam.exe" }, MonkMode.Blocker.SetupDefaultApps());

            // Stored as the raw comma-joined string under [Setup] DefaultApps (MAC-covered, like Partner).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.Equal("discord.exe,steam.exe", ini.GetKeyValue("Setup", "DefaultApps"));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void NoDefaultApps_YieldsEmpty_AndStaysComplete()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));      // no default app list given
            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Empty(MonkMode.Blocker.SetupDefaultApps());

            // Written ONLY when non-empty => the key is absent.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            Assert.True(string.IsNullOrEmpty(ini.GetKeyValue("Setup", "DefaultApps")));
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void DefaultApps_Reader_SplitsTrimsAndDedupes_UnderAValidMac()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex"));
            // Hand-store a messy value (dupes, blanks, whitespace, ; separators) and RE-STAMP a valid
            // MAC: the reader normalises it (split on , / ; , trim, drop empties, dedupe case-insensitive).
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "DefaultApps", " discord.exe , ; steam.exe;discord.exe , DISCORD.EXE ");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.True(MonkMode.Blocker.SetupIsComplete());
            Assert.Equal(new[] { "discord.exe", "steam.exe" }, MonkMode.Blocker.SetupDefaultApps());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void TamperedDefaultApps_BreaksMac_FallsBackToEmpty()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "", "discord.exe,steam.exe"));
            Assert.Equal(new[] { "discord.exe", "steam.exe" }, MonkMode.Blocker.SetupDefaultApps());

            // Raw-edit the stored default (inject an extra app) WITHOUT re-stamping: the MAC no longer
            // covers the canonical => the whole setup file reads as incomplete => the inherited default
            // is EMPTY, not the tampered list. (Injecting apps would only over-kill a NEW arm anyway;
            // this proves the tamper-evidence path fails closed to no-default.)
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "DefaultApps", "discord.exe,steam.exe,evil.exe");
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());               // tamper => fail-closed
            Assert.Empty(MonkMode.Blocker.SetupDefaultApps());              // empty = no default, NOT the tampered list
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void DefaultApps_IsGatedOnCompleteness_IncompleteSetupYieldsEmpty()
    {
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "", "discord.exe,steam.exe"));

            // Flip Done to "no" and RE-STAMP a VALID MAC over it: the MAC is valid and DefaultApps is
            // intact, but setup is NOT complete (Done<>"yes") => the default reads as empty. Proves
            // SetupDefaultApps gates on completeness (like SetupDefaultSites) - an incomplete setup file
            // never leaks an inheritable default.
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());
            ini.SetKeyValue("Setup", "Done", "no");
            ReStampSetup(ini);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            Assert.False(MonkMode.Blocker.SetupIsComplete());
            Assert.Empty(MonkMode.Blocker.SetupDefaultApps());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void SchemaBump_S3MacUnderS4Code_FreezesSetup_ForcesReRun()
    {
        // The D2b instance of the setup-file upgrade freeze: a file stamped under s3 (D1b: Done +
        // Partner + CoolOffSeconds + DefaultSites, NO DefaultApps) read under s4 code. The s4
        // SetupCanonicalFromIni tags "s4" and appends a "DefaultApps=" line, so the byte-exact s3 stamp
        // can't validate it - setup reads NOT complete (the arm-gate then makes the user re-run `setup`)
        // and the inherited default is empty. Mirrors the s2->s3 freeze test above.
        WipeSetup();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 7200, "reddit.com,x.com"));   // a real, DPAPI-stamped s4 file
            var ini = new MonkMode.IniFile(); ini.Load(MonkMode.Blocker.SetupIniPath());

            // Forge the OLD s3 canonical (version "s3", through DefaultSites, NO DefaultApps line) and
            // stamp the file's MAC over IT with the existing key - exactly what a pre-D2b CLI stored.
            var s3Canonical = "s3\n" + "Done=yes\n" + "Partner=Alex\n" + "CoolOffSeconds=7200\n" + "DefaultSites=reddit.com,x.com\n";
            StampSetupOverCanonical(ini, s3Canonical);
            ini.Save(MonkMode.Blocker.SetupIniPath());

            // Sanity: the forged s3 MAC IS valid over the s3 canonical (a genuine "old but honest" file)...
            var key = MonkMode.ConfigIntegrity.UnprotectKey(ini.GetKeyValue("Integrity", "Key"));
            Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(
                s3Canonical, ini.GetKeyValue("Integrity", "Mac"), key));
            // ...yet under s4 code it does NOT validate the s4 canonical => frozen, forces re-run.
            Assert.False(MonkMode.Blocker.SetupIsComplete());
            Assert.Empty(MonkMode.Blocker.SetupDefaultApps());
        }
        finally { WipeSetup(); }
    }

    [Fact]
    public void SetupDefaultApps_FlowsIntoBlock_TheAppsInheritSeam()
    {
        // The DoBlock inherit path can't arm a live block in a unit test (fence), so pin its SEAM: the
        // stored account default (SetupDefaultApps) flows into the SAME `apps` list WriteConfig writes,
        // landing in the enforcement [Process] List and round-tripping through BlockedApps - exactly
        // what `block` (no app source) does. Stored WITHOUT .exe here to prove the documented claim that
        // .exe-normalisation happens downstream in PackApps at arm time (the reader returns raw tokens).
        WipeSetup();
        WipeEnforcement();
        try
        {
            Assert.True(MonkMode.Blocker.WriteSetupConfig("Alex", 0, "", "discord,steam"));
            var inherited = MonkMode.Blocker.SetupDefaultApps();
            Assert.Equal(new[] { "discord", "steam" }, inherited);          // reader returns RAW (no .exe)

            // What DoBlock does when no --apps/--app-preset is given: apps = the inherited default.
            MonkMode.Blocker.WriteConfig(System.Array.Empty<string>(), inherited,
                new System.DateTime(2027, 1, 1, 0, 0, 0));

            // [Process] List is encrypted; BlockedApps decrypts + strips the trailing ';'. PackApps
            // .exe-normalised each name at arm time - the default is now enforced identically to a
            // hand-typed --apps list.
            Assert.Equal("discord.exe;steam.exe", MonkMode.Blocker.BlockedApps());
        }
        finally { WipeSetup(); WipeEnforcement(); }
    }
}
