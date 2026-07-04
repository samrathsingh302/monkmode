// MonkMode.Tests - B7 end-to-end canonical parity across the four projects.
//
// WHY THIS EXISTS (and why ConfigIntegrityTests' 4-copy parity is not enough):
// ConfigIntegrityTests.AllFourCopies_ProduceIdenticalCanonical/Mac call the
// SHARED ConfigIntegrity.BuildCanonical/ComputeConfigMac with identical literal
// arguments. Because ConfigIntegrity.vb is byte-for-byte identical in all four
// projects, those assertions are trivially true - they would still pass even if
// a per-project `CanonicalFromIni` WRAPPER drifted, because they never call the
// wrappers. The real drift risk is exactly those wrappers: the service's
// Service1.CanonicalFromIni, the guardian's Program.CanonicalFromIni, the
// notifier's Form1.CanonicalFromIni and the CLI's Blocker.CanonicalFromIni each
// decide WHICH ini fields to decrypt before handing them to BuildCanonical. If
// someone changed one (e.g. started decrypting [User] CustomSites, or stopped
// decrypting [Process] List), cross-party MAC agreement would silently break:
// every reader would compute a different canonical from the writer, every MAC
// check would fail, and (because the readers fail CLOSED) every block would
// freeze and never auto-lift - with no test catching it. These tests call the
// four real wrappers on the SAME representative ini and pin that they agree.
//
// Fences honoured: no DPAPI (the [Integrity] Key seam) is exercised - the MAC
// is computed with a raw injected key via ComputeConfigMac, exactly like
// ConfigIntegrityTests. Only the test bin directory's monkmode_settings.ini is
// ever written (for the CLI WriteConfig path), never the real deployed config,
// hosts, registry or service. The encrypted fields are encrypted once with the
// CLI's Simple3Des and the IDENTICAL ciphertext is stored in each project's
// IniFile - that is faithful to production (the CLI writes one ini that every
// reader loads), and CryptoRoundTripTests already pins the four Simple3Des
// copies produce identical ciphertext.

using System.Globalization;

namespace MonkMode.Tests;

// CliWriteConfig_ProducesAnIni writes the shared test-bin monkmode_settings.ini via
// Blocker.WriteConfig; the "CliIniWriters" collection serialises it with the other
// ini-writing test classes so they never race the shared file.
[Collection("CliIniWriters")]
public class CanonicalParityTests
{
    private const string Passphrase = "mm_textbox";

    // The schema version the wrappers pass into BuildCanonical (C1 made it a
    // caller-supplied parameter). Shared by the four byte-identical copies.
    private static readonly string Ver = MonkMode.ConfigIntegrity.CurrentSchemaVersion;

    // A representative block, exactly the shape the CLI writes: an encrypted
    // [Time] Until, an encrypted [CurrentTime] Now, an encrypted [Process] List,
    // and a PLAINTEXT [User] CustomSites (the CLI stores CustomSites in the
    // clear - only Until/Now/ProcessList are encrypted). CustomSites is chosen to
    // be valid Base64-looking-ish but the point is it must pass through the
    // canonical VERBATIM (never decrypted); ProcessList must be decrypted.
    private const string UntilPlain = "2026-12-31 11:59:59 p.m.";
    private const string NowPlain = "2026-06-25 12:00:00 p.m.";
    private const string ProcListPlain = "chrome.exe;brave.exe;steam.exe;";
    private const string CustomSitesPlain = "reddit.com;x.com;";
    // B4: HighWater is an encrypted datetime like Until/Now (the wrappers decrypt
    // it). A distinct value so a wrapper that read the wrong field would diverge.
    private const string HighWaterPlain = "2026-06-25 11:30:00 a.m.";
    // C2b: CoolOffUntil is an encrypted datetime like HighWater (the wrappers
    // decrypt it). A distinct value so a wrapper that read the wrong field (or
    // stopped decrypting it) would diverge.
    private const string CoolOffUntilPlain = "2026-06-25 12:45:00 p.m.";
    // C3b: the [Partner] fields are stored PLAINTEXT (as-stored, MAC-covered - NOT
    // encrypted like the datetimes). Distinct values so a wrapper reading the wrong
    // field, or one that (wrongly) started decrypting them, would diverge.
    private const string PartnerSaltPlain = "c2FsdC1iYXNlNjQ=";
    private const string PartnerHashPlain = "aGFzaC1iYXNlNjQ=";
    private const string PartnerUnlockedAtPlain = "2026-06-25 1:15:00 p.m.";
    // C4: the [Commit] Committed flag, plaintext-as-stored (MAC-covered). A distinct
    // value so a wrapper reading the wrong field would diverge.
    private const string CommittedPlain = "yes";
    // C5b: [Schedule] Spec is plaintext-as-stored (MAC-covered, NOT encrypted, like
    // CustomSites/[Partner]); [Schedule] ActiveUntil is an ENCRYPTED datetime like
    // CoolOffUntil (the wrappers decrypt it). Distinct values so a wrapper reading the
    // wrong field, or one that got the plaintext-vs-decrypt split wrong, would diverge.
    private const string ScheduleSpecPlain = "v1;12345:0900-1700;sites=reddit.com;apps=chrome.exe";
    private const string ScheduleActiveUntilPlain = "2026-06-25 1:45:00 p.m.";
    // C6b: [CoolOff] Duration is the configured cooling-off wait in SECONDS, plaintext-as-
    // stored (MAC-covered, NOT encrypted, like Committed/[Schedule] Spec). A distinct value
    // so a wrapper reading the wrong field, or one that (wrongly) decrypted it, would diverge.
    private const string CoolOffDurationPlain = "5400";

    // A fixed raw 32-byte test key (the production key is random + DPAPI-
    // protected; the pure MAC layer takes the raw key, so inject a known one and
    // never touch DPAPI - same approach as ConfigIntegrityTests).
    private static readonly byte[] Key =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    };

    // Ciphertexts produced ONCE with the CLI's Simple3Des; the same bytes are
    // stored into every project's IniFile (production reality: one ini, many
    // readers). CryptoRoundTripTests pins that all four copies encrypt identically.
    private static readonly string UntilEnc = new MonkMode.Simple3Des(Passphrase).EncryptData(UntilPlain);
    private static readonly string NowEnc = new MonkMode.Simple3Des(Passphrase).EncryptData(NowPlain);
    private static readonly string ProcListEnc = new MonkMode.Simple3Des(Passphrase).EncryptData(ProcListPlain);
    private static readonly string HighWaterEnc = new MonkMode.Simple3Des(Passphrase).EncryptData(HighWaterPlain);
    private static readonly string CoolOffUntilEnc = new MonkMode.Simple3Des(Passphrase).EncryptData(CoolOffUntilPlain);
    // C5b: ScheduleActiveUntil is encrypted like CoolOffUntil; ScheduleSpec is stored plaintext.
    private static readonly string ScheduleActiveUntilEnc = new MonkMode.Simple3Des(Passphrase).EncryptData(ScheduleActiveUntilPlain);

    // ---- per-project ini builders (each project has its own IniFile type) ----
    //
    // Same logical content into each project's own IniFile. The encrypted fields
    // get the shared ciphertext; CustomSites is stored plaintext, as the CLI does.

    private static MonkMode.IniFile CliIni()
    {
        var ini = new MonkMode.IniFile();
        ini.SetKeyValue("Time", "Until", UntilEnc);
        ini.SetKeyValue("Time", "HighWater", HighWaterEnc);
        ini.SetKeyValue("Time", "CoolOffUntil", CoolOffUntilEnc);
        ini.SetKeyValue("CurrentTime", "Now", NowEnc);
        ini.SetKeyValue("Process", "List", ProcListEnc);
        ini.SetKeyValue("User", "CustomSites", CustomSitesPlain);
        ini.SetKeyValue("Partner", "Salt", PartnerSaltPlain);
        ini.SetKeyValue("Partner", "Hash", PartnerHashPlain);
        ini.SetKeyValue("Partner", "UnlockedAt", PartnerUnlockedAtPlain);
        ini.SetKeyValue("Commit", "Committed", CommittedPlain);
        ini.SetKeyValue("Schedule", "Spec", ScheduleSpecPlain);
        ini.SetKeyValue("Schedule", "ActiveUntil", ScheduleActiveUntilEnc);
        ini.SetKeyValue("CoolOff", "Duration", CoolOffDurationPlain);
        return ini;
    }

    private static monkmode.IniFile ServiceIni()
    {
        var ini = new monkmode.IniFile();
        ini.SetKeyValue("Time", "Until", UntilEnc);
        ini.SetKeyValue("Time", "HighWater", HighWaterEnc);
        ini.SetKeyValue("Time", "CoolOffUntil", CoolOffUntilEnc);
        ini.SetKeyValue("CurrentTime", "Now", NowEnc);
        ini.SetKeyValue("Process", "List", ProcListEnc);
        ini.SetKeyValue("User", "CustomSites", CustomSitesPlain);
        ini.SetKeyValue("Partner", "Salt", PartnerSaltPlain);
        ini.SetKeyValue("Partner", "Hash", PartnerHashPlain);
        ini.SetKeyValue("Partner", "UnlockedAt", PartnerUnlockedAtPlain);
        ini.SetKeyValue("Commit", "Committed", CommittedPlain);
        ini.SetKeyValue("Schedule", "Spec", ScheduleSpecPlain);
        ini.SetKeyValue("Schedule", "ActiveUntil", ScheduleActiveUntilEnc);
        ini.SetKeyValue("CoolOff", "Duration", CoolOffDurationPlain);
        return ini;
    }

    private static mm_guard.IniFile GuardianIni()
    {
        var ini = new mm_guard.IniFile();
        ini.SetKeyValue("Time", "Until", UntilEnc);
        ini.SetKeyValue("Time", "HighWater", HighWaterEnc);
        ini.SetKeyValue("Time", "CoolOffUntil", CoolOffUntilEnc);
        ini.SetKeyValue("CurrentTime", "Now", NowEnc);
        ini.SetKeyValue("Process", "List", ProcListEnc);
        ini.SetKeyValue("User", "CustomSites", CustomSitesPlain);
        ini.SetKeyValue("Partner", "Salt", PartnerSaltPlain);
        ini.SetKeyValue("Partner", "Hash", PartnerHashPlain);
        ini.SetKeyValue("Partner", "UnlockedAt", PartnerUnlockedAtPlain);
        ini.SetKeyValue("Commit", "Committed", CommittedPlain);
        ini.SetKeyValue("Schedule", "Spec", ScheduleSpecPlain);
        ini.SetKeyValue("Schedule", "ActiveUntil", ScheduleActiveUntilEnc);
        ini.SetKeyValue("CoolOff", "Duration", CoolOffDurationPlain);
        return ini;
    }

    private static mm_notify.IniFile NotifierIni()
    {
        var ini = new mm_notify.IniFile();
        ini.SetKeyValue("Time", "Until", UntilEnc);
        ini.SetKeyValue("Time", "HighWater", HighWaterEnc);
        ini.SetKeyValue("Time", "CoolOffUntil", CoolOffUntilEnc);
        ini.SetKeyValue("CurrentTime", "Now", NowEnc);
        ini.SetKeyValue("Process", "List", ProcListEnc);
        ini.SetKeyValue("User", "CustomSites", CustomSitesPlain);
        ini.SetKeyValue("Partner", "Salt", PartnerSaltPlain);
        ini.SetKeyValue("Partner", "Hash", PartnerHashPlain);
        ini.SetKeyValue("Partner", "UnlockedAt", PartnerUnlockedAtPlain);
        ini.SetKeyValue("Commit", "Committed", CommittedPlain);
        ini.SetKeyValue("Schedule", "Spec", ScheduleSpecPlain);
        ini.SetKeyValue("Schedule", "ActiveUntil", ScheduleActiveUntilEnc);
        ini.SetKeyValue("CoolOff", "Duration", CoolOffDurationPlain);
        return ini;
    }

    // The four real wrappers, each called on its own project's ini. Service and
    // notifier wrappers are instance methods; constructing those types runs only
    // their field/InitializeComponent setup (no OnStart / no Form.Show), so they
    // touch no hosts/registry/SCM/DPAPI - inside the fence.
    private static string CliCanonical() => MonkMode.Blocker.CanonicalFromIni(CliIni());
    private static string ServiceCanonical() => new monkmode.Service1().CanonicalFromIni(ServiceIni());
    private static string GuardianCanonical() => mm_guard.Program.CanonicalFromIni(GuardianIni());
    private static string NotifierCanonical() => new mm_notify.Form1().CanonicalFromIni(NotifierIni());

    [Fact]
    public void AllFourWrappers_ProduceIdenticalCanonical_FromTheSameIni()
    {
        // The end-to-end (not tautological) parity: each project's real
        // CanonicalFromIni wrapper, run on its own IniFile holding the same
        // bytes, must yield the IDENTICAL canonical. This is what the literal
        // BuildCanonical comparison cannot prove - it pins the wrappers' field
        // selection + decrypt decisions agree, not just the shared function.
        var cli = CliCanonical();
        Assert.Equal(cli, ServiceCanonical());
        Assert.Equal(cli, GuardianCanonical());
        Assert.Equal(cli, NotifierCanonical());
    }

    [Fact]
    public void TheCanonical_DecryptsUntilNowProcessList_ButLeavesCustomSitesVerbatim()
    {
        // Pin WHAT the wrappers must do: decrypt Until/HighWater/CoolOffUntil/
        // Now/ProcessList, pass CustomSites through verbatim. If a wrapper
        // started decrypting CustomSites, stopped decrypting ProcessList, or
        // stopped decrypting the B4 HighWater / C2b CoolOffUntil fields, this
        // exact string would change and the test fails loudly. (Mirrors
        // BuildCanonical's pinned format test, but through the real reader, end
        // to end.)
        Assert.Equal(
            Ver + "\n" +
            "Until=" + UntilPlain + "\n" +
            "HighWater=" + HighWaterPlain + "\n" +
            "CoolOffUntil=" + CoolOffUntilPlain + "\n" +
            "ProcessList=" + ProcListPlain + "\n" +
            "CustomSites=" + CustomSitesPlain + "\n" +
            "Now=" + NowPlain + "\n" +
            "PartnerSalt=" + PartnerSaltPlain + "\n" +
            "PartnerHash=" + PartnerHashPlain + "\n" +
            "PartnerUnlockedAt=" + PartnerUnlockedAtPlain + "\n" +
            "Committed=" + CommittedPlain + "\n" +
            "ScheduleSpec=" + ScheduleSpecPlain + "\n" +
            "ScheduleActiveUntil=" + ScheduleActiveUntilPlain + "\n" +
            "CoolOffDuration=" + CoolOffDurationPlain + "\n",
            CliCanonical());
    }

    [Fact]
    public void CliStampedMac_ValidatesUnderEveryReadersCanonical()
    {
        // The data flow B7 depends on: the CLI stamps a MAC over ITS canonical;
        // the service, guardian and notifier each recompute their OWN canonical
        // from the same ini and the stored MAC must validate. Uses a raw injected
        // key (no DPAPI). If any reader's wrapper drifted from the CLI's, its
        // canonical would differ and this MAC check would fail - the silent
        // block-freezing bug, caught here.
        var stampedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(CliCanonical(), Key);

        Assert.True(monkmode.ConfigIntegrity.ConfigMacIsValid(ServiceCanonical(), stampedMac, Key));
        Assert.True(mm_guard.ConfigIntegrity.ConfigMacIsValid(GuardianCanonical(), stampedMac, Key));
        Assert.True(mm_notify.ConfigIntegrity.ConfigMacIsValid(NotifierCanonical(), stampedMac, Key));
        // and trivially under the CLI's own (round-trip sanity).
        Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(CliCanonical(), stampedMac, Key));
    }

    [Fact]
    public void NegativeContract_DecryptingCustomSites_DoesNotMatchTheCorrectCanonical()
    {
        // The most likely future mistake, pinned as a contract: if a wrapper
        // DECRYPTED CustomSites (instead of passing it through), it would build a
        // different canonical, so a MAC over the correct canonical must NOT
        // validate the wrong one. CustomSites here is deliberately a value that
        // DOES decrypt to something else, to make the divergence concrete.
        //
        // Build the "wrong" canonical the way a buggy wrapper would: decrypt
        // CustomSites too. (We encrypt a plaintext, store the CIPHERTEXT as
        // CustomSites, and show that decrypting-vs-not yields different canonicals.)
        var encryptedSites = new MonkMode.Simple3Des(Passphrase).EncryptData("decrypted-sites;");

        var correct = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, UntilPlain, ProcListPlain, encryptedSites, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain);  // CustomSites VERBATIM (correct)
        var buggy = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, UntilPlain, ProcListPlain, "decrypted-sites;", NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain);  // CustomSites DECRYPTED (the bug)

        Assert.NotEqual(correct, buggy);

        // And a MAC over the correct canonical must reject the buggy one - i.e.
        // a reader that started decrypting CustomSites would fail every MAC check.
        var macOverCorrect = MonkMode.ConfigIntegrity.ComputeConfigMac(correct, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggy, macOverCorrect, Key));
    }

    [Fact]
    public void NegativeContract_NotDecryptingProcessList_DoesNotMatchTheCorrectCanonical()
    {
        // The mirror mistake: a wrapper that STOPPED decrypting [Process] List
        // would put the ciphertext into the canonical instead of the plaintext,
        // diverging from every other party. Pin that this breaks MAC agreement.
        var correct = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, UntilPlain, ProcListPlain, CustomSitesPlain, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain);  // ProcessList DECRYPTED (correct)
        var buggy = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, UntilPlain, ProcListEnc, CustomSitesPlain, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain);  // ProcessList CIPHERTEXT (the bug)

        Assert.NotEqual(correct, buggy);
        var macOverCorrect = MonkMode.ConfigIntegrity.ComputeConfigMac(correct, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggy, macOverCorrect, Key));
    }

    [Fact]
    public void NullProcessList_PassesThroughVerbatim_AcrossAllWrappers()
    {
        // An apps-only-absent block stores [Process] List = "null" (verbatim, not
        // encrypted). Every wrapper must pass "null" through without trying to
        // decrypt it (decrypting "null" as Base64 would fail-closed to "" and
        // diverge). Pin the four wrappers still agree in this stored shape.
        static void SetNullProc(Action<string, string, string> set)
        {
            set("Time", "Until", UntilEnc);
            set("Time", "HighWater", HighWaterEnc);
            set("Time", "CoolOffUntil", CoolOffUntilEnc);
            set("CurrentTime", "Now", NowEnc);
            set("Process", "List", "null");
            set("User", "CustomSites", CustomSitesPlain);
            set("Partner", "Salt", PartnerSaltPlain);
            set("Partner", "Hash", PartnerHashPlain);
            set("Partner", "UnlockedAt", PartnerUnlockedAtPlain);
            set("Commit", "Committed", CommittedPlain);
            set("Schedule", "Spec", ScheduleSpecPlain);
            set("Schedule", "ActiveUntil", ScheduleActiveUntilEnc);
            set("CoolOff", "Duration", CoolOffDurationPlain);
        }

        var cliIni = new MonkMode.IniFile();
        SetNullProc((s, k, v) => cliIni.SetKeyValue(s, k, v));
        var srvIni = new monkmode.IniFile();
        SetNullProc((s, k, v) => srvIni.SetKeyValue(s, k, v));
        var guardIni = new mm_guard.IniFile();
        SetNullProc((s, k, v) => guardIni.SetKeyValue(s, k, v));
        var notifyIni = new mm_notify.IniFile();
        SetNullProc((s, k, v) => notifyIni.SetKeyValue(s, k, v));

        var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
        Assert.Contains("ProcessList=null\n", cli);
        Assert.Equal(cli, new monkmode.Service1().CanonicalFromIni(srvIni));
        Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
        Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));
    }

    [Fact]
    public void CliWriteConfig_ProducesAnIni_EveryReadersWrapperAgreesOn()
    {
        // The fullest end-to-end path: let the real CLI WriteConfig build and
        // persist the ini (into the test bin directory), then load it back with
        // each project's own IniFile and assert every wrapper derives the same
        // canonical the CLI does. This exercises the actual write+encrypt path,
        // not hand-built ciphertext.
        //
        // WriteConfig DOES stamp a DPAPI-protected [Integrity] Key/Mac, but we do
        // NOT read or validate that MAC here (that is the DPAPI seam, smoke-
        // tested) - we only compare CANONICALS, which are DPAPI-free. So this
        // stays inside the no-DPAPI fence for what it asserts.
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            MonkMode.Blocker.WriteConfig(
                new[] { "reddit.com", "x.com" },
                new[] { "chrome.exe", "brave.exe" },
                until);

            var cliIni = new MonkMode.IniFile();
            cliIni.Load(iniPath);
            var srvIni = new monkmode.IniFile();
            srvIni.Load(iniPath);
            var guardIni = new mm_guard.IniFile();
            guardIni.Load(iniPath);
            var notifyIni = new mm_notify.IniFile();
            notifyIni.Load(iniPath);

            var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
            Assert.Equal(cli, new monkmode.Service1().CanonicalFromIni(srvIni));
            Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            // C1b: WriteConfig now also refreshes a MAC-covered shadow backup next
            // to the ini (when the DPAPI stamp succeeds). Clean it up too so the
            // test leaves nothing behind in the bin directory.
            var backupPath = MonkMode.Blocker.IniBackupPath();
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
