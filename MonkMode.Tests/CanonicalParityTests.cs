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

    // A representative block, in the v10 slot shape: the ENCRYPTED datetimes
    // ([Time] HighWater, [CurrentTime] Now, and the slot's Until/CoolOffUntil/
    // ScheduleActiveUntil) plus PLAINTEXT lists and flags (the slot's Sites/Apps/
    // UrlPatterns, the [Partner] fields, Committed, ScheduleSpec, CoolOffDuration,
    // AllSession). The list values must pass through the canonical VERBATIM; the
    // datetimes must be decrypted. Each constant is distinct so a wrapper reading
    // the wrong field, or getting the decrypt split wrong, diverges visibly.
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
    // D2c: [Process] AllSession is the all-session-app-kill flag, plaintext-as-stored (MAC-covered,
    // NOT encrypted, like Committed/[Schedule] Spec). A distinct value ("yes") so a wrapper reading
    // the wrong field, or one that (wrongly) decrypted it, would diverge.
    private const string AllSessionKillPlain = "yes";

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

    // v10: the same logical block, now as ONE SLOT - the encrypted datetimes stay
    // encrypted in the file (the wrappers decrypt them), and Sites/Apps ride the slot
    // as PLAINTEXT (P8 - a blocklist is not a secret, the MAC is its protection).
    // OneSlot.WriteSlot1 writes the identical 22 keys into whichever project's IniFile
    // the delegate targets, so no copy can drift from the others by a typo.
    private static void Fill(Action<string, string, string> set) =>
        OneSlot.WriteSlot1(set,
            UntilEnc, ProcListPlain, CustomSitesPlain, NowEnc, HighWaterEnc, CoolOffUntilEnc,
            PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain,
            ScheduleSpecPlain, ScheduleActiveUntilEnc, CoolOffDurationPlain, AllSessionKillPlain);

    private static MonkMode.IniFile CliIni()
    {
        var ini = new MonkMode.IniFile();
        Fill((s, k, v) => ini.SetKeyValue(s, k, v));
        return ini;
    }

    private static monkmode.IniFile ServiceIni()
    {
        var ini = new monkmode.IniFile();
        Fill((s, k, v) => ini.SetKeyValue(s, k, v));
        return ini;
    }

    private static mm_guard.IniFile GuardianIni()
    {
        var ini = new mm_guard.IniFile();
        Fill((s, k, v) => ini.SetKeyValue(s, k, v));
        return ini;
    }

    private static mm_notify.IniFile NotifierIni()
    {
        var ini = new mm_notify.IniFile();
        Fill((s, k, v) => ini.SetKeyValue(s, k, v));
        return ini;
    }

    // The four real wrappers, each called on its own project's ini. Service and
    // notifier wrappers are instance methods; constructing those types runs only
    // their field/InitializeComponent setup (no OnStart / no Form.Show), so they
    // touch no hosts/registry/SCM/DPAPI - inside the fence.
    private static string CliCanonical() => MonkMode.Blocker.CanonicalFromIni(CliIni());
    private static string ServiceCanonical() => MonkMode.Tests.TestSvc.New().CanonicalFromIni(ServiceIni());
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
    public void TheCanonical_DecryptsTheDatetimes_ButLeavesTheListsAndFlagsVerbatim()
    {
        // Pin WHAT the wrappers must do, through the REAL reader, end to end. v10's
        // decrypt set is exactly the datetimes: the globals HighWater/Now (and [Guard]
        // HoldUntil) plus each slot's Until/StartAt/CoolOffUntil/ScheduleActiveUntil.
        // Everything else - INCLUDING Sites, Apps and UrlPatterns - passes through
        // verbatim. If a wrapper started decrypting a list, or stopped decrypting one of
        // the datetimes, this exact string would change and the test fails loudly.
        Assert.Equal(
            Ver + "\n" +
            "HighWater=" + HighWaterPlain + "\n" +
            "Now=" + NowPlain + "\n" +
            "NextSlotId=" + OneSlot.NextSlotId + "\n" +
            "SlotCount=1\n" +
            "GuardHoldUntil=" + OneSlot.GuardHoldUntil + "\n" +
            "GuardArmedCount=" + OneSlot.GuardArmedCount + "\n" +
            // FX1 (v11): the GLOBAL [Schedule] pair - empty on this SLOT-shaped ini
            // (WriteSlot1 writes no global [Schedule] section). The populated case is
            // GlobalSchedule_AllFourWrappers_AgreeAndDecryptOnlyTheDeadline below.
            "ScheduleSpec=\n" +
            "ScheduleActiveUntil=\n" +
            // F77 (v12): the TrustedUtc anchor - "" here, because WriteSlot1 seeds no
            // anchor. An absent anchor must pass through as "" (never as a fabricated
            // "now"), which is what makes an un-anchored config earn no downtime credit.
            "TrustedUtc=\n" +
            "Slot1.Id=" + OneSlot.Id + "\n" +
            "Slot1.StartAt=\n" +
            "Slot1.DurationSeconds=\n" +
            "Slot1.Until=" + UntilPlain + "\n" +
            "Slot1.Sites=" + CustomSitesPlain + "\n" +
            "Slot1.Apps=" + ProcListPlain + "\n" +
            "Slot1.UrlPatterns=\n" +
            "Slot1.AllSession=" + AllSessionKillPlain + "\n" +
            "Slot1.ScheduleSpec=" + ScheduleSpecPlain + "\n" +
            "Slot1.ScheduleActiveUntil=" + ScheduleActiveUntilPlain + "\n" +
            "Slot1.CoolOffUntil=" + CoolOffUntilPlain + "\n" +
            "Slot1.CoolOffDuration=" + CoolOffDurationPlain + "\n" +
            "Slot1.PartnerSalt=" + PartnerSaltPlain + "\n" +
            "Slot1.PartnerHash=" + PartnerHashPlain + "\n" +
            "Slot1.PartnerUnlockedAt=" + PartnerUnlockedAtPlain + "\n" +
            "Slot1.Committed=" + CommittedPlain + "\n",
            CliCanonical());
    }

    [Fact]
    public void GlobalSchedule_AllFourWrappers_AgreeAndDecryptOnlyTheDeadline()
    {
        // FX1 (v11): the wrapper-level pin for the two GLOBAL [Schedule] fields - the ones
        // a SCHEDULE-ONLY (slot-less) config enforces from. The split must be: Spec passes
        // through VERBATIM (plaintext-as-stored, like Sites/[Partner]) and ActiveUntil is
        // DECRYPTED (an encrypted datetime, like HighWater). If one wrapper got that split
        // wrong - or forgot the fields entirely - it would build a different canonical from
        // the writer, every MAC check would fail and every block would freeze silently.
        // A slot-less ini, exactly like `monkmode schedule` writes.
        static void FillScheduleOnly(Action<string, string, string> set)
        {
            set("Time", "HighWater", HighWaterEnc);
            set("CurrentTime", "Now", NowEnc);
            set("Guard", "ArmedCount", "1");
            set("Schedule", "Spec", ScheduleSpecPlain);
            set("Schedule", "ActiveUntil", ScheduleActiveUntilEnc);
        }

        var cliIni = new MonkMode.IniFile();
        FillScheduleOnly((s, k, v) => cliIni.SetKeyValue(s, k, v));
        var srvIni = new monkmode.IniFile();
        FillScheduleOnly((s, k, v) => srvIni.SetKeyValue(s, k, v));
        var guardIni = new mm_guard.IniFile();
        FillScheduleOnly((s, k, v) => guardIni.SetKeyValue(s, k, v));
        var notifyIni = new mm_notify.IniFile();
        FillScheduleOnly((s, k, v) => notifyIni.SetKeyValue(s, k, v));

        var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
        Assert.Equal(
            Ver + "\n" +
            "HighWater=" + HighWaterPlain + "\n" +
            "Now=" + NowPlain + "\n" +
            "NextSlotId=\n" +
            "SlotCount=0\n" +
            "GuardHoldUntil=\n" +
            "GuardArmedCount=1\n" +
            "ScheduleSpec=" + ScheduleSpecPlain + "\n" +
            "ScheduleActiveUntil=" + ScheduleActiveUntilPlain + "\n" +
            "TrustedUtc=\n",
            cli);
        Assert.Equal(cli, MonkMode.Tests.TestSvc.New().CanonicalFromIni(srvIni));
        Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
        Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));

        // ...and the MAC over that canonical rejects the F1 edit (Spec blanked in a text
        // editor, everything else untouched) - the freeze that replaces the teardown.
        var stamped = MonkMode.ConfigIntegrity.ComputeConfigMac(cli, Key);
        var tamperedIni = new MonkMode.IniFile();
        FillScheduleOnly((s, k, v) => tamperedIni.SetKeyValue(s, k, v));
        tamperedIni.SetKeyValue("Schedule", "Spec", "");
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            MonkMode.Blocker.CanonicalFromIni(tamperedIni), stamped, Key));
    }

    [Fact]
    public void NegativeContract_NotDecryptingTheGlobalScheduleDeadline_DoesNotMatch()
    {
        // The mirror of the slot-Until contract, for the GLOBAL [Schedule] ActiveUntil: a
        // wrapper that left the ciphertext in the canonical (or that decrypted the Spec,
        // which is stored plaintext) diverges from every other party. Pin both directions.
        var correct = MonkMode.ConfigIntegrity.BuildCanonical(Ver, HighWaterPlain, NowPlain, "", 0, "", "1",
            ScheduleSpecPlain, ScheduleActiveUntilPlain, "", "");
        var buggyDeadline = MonkMode.ConfigIntegrity.BuildCanonical(Ver, HighWaterPlain, NowPlain, "", 0, "", "1",
            ScheduleSpecPlain, ScheduleActiveUntilEnc, "", "");
        var buggySpec = MonkMode.ConfigIntegrity.BuildCanonical(Ver, HighWaterPlain, NowPlain, "", 0, "", "1",
            new MonkMode.Simple3Des(Passphrase).EncryptData(ScheduleSpecPlain), ScheduleActiveUntilPlain, "", "");

        Assert.NotEqual(correct, buggyDeadline);
        Assert.NotEqual(correct, buggySpec);
        var macOverCorrect = MonkMode.ConfigIntegrity.ComputeConfigMac(correct, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggyDeadline, macOverCorrect, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggySpec, macOverCorrect, Key));
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
    public void NegativeContract_DecryptingTheSlotLists_DoesNotMatchTheCorrectCanonical()
    {
        // The most likely future mistake, pinned as a contract: if a wrapper DECRYPTED
        // a slot's Sites or Apps (instead of passing it through), it would build a
        // different canonical, so a MAC over the correct canonical must NOT validate the
        // wrong one. Both values here are deliberately ones that DO decrypt to something
        // else, to make the divergence concrete. (v10 widened this contract: under v9
        // only CustomSites was plaintext-as-stored; now Sites, Apps AND UrlPatterns are.)
        var encryptedSites = new MonkMode.Simple3Des(Passphrase).EncryptData("decrypted-sites;");

        var correct = OneSlot.Canonical(UntilPlain, ProcListEnc, encryptedSites, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain, AllSessionKillPlain);  // lists VERBATIM (correct)
        var buggySites = OneSlot.Canonical(UntilPlain, ProcListEnc, "decrypted-sites;", NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain, AllSessionKillPlain);  // Sites DECRYPTED (the bug)
        var buggyApps = OneSlot.Canonical(UntilPlain, ProcListPlain, encryptedSites, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain, AllSessionKillPlain);  // Apps DECRYPTED (the bug)

        Assert.NotEqual(correct, buggySites);
        Assert.NotEqual(correct, buggyApps);

        // And a MAC over the correct canonical must reject both - i.e. a reader that
        // started decrypting either list would fail every MAC check.
        var macOverCorrect = MonkMode.ConfigIntegrity.ComputeConfigMac(correct, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggySites, macOverCorrect, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggyApps, macOverCorrect, Key));
    }

    [Fact]
    public void NegativeContract_NotDecryptingTheSlotUntil_DoesNotMatchTheCorrectCanonical()
    {
        // The mirror mistake: a wrapper that STOPPED decrypting a slot's Until (the
        // datetime that decides when the block ends) would put the ciphertext into the
        // canonical instead of the plaintext, diverging from every other party. Pin that
        // this breaks MAC agreement.
        var correct = OneSlot.Canonical(UntilPlain, ProcListPlain, CustomSitesPlain, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain, AllSessionKillPlain);  // Until DECRYPTED (correct)
        var buggy = OneSlot.Canonical(UntilEnc, ProcListPlain, CustomSitesPlain, NowPlain, HighWaterPlain, CoolOffUntilPlain, PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain, ScheduleSpecPlain, ScheduleActiveUntilPlain, CoolOffDurationPlain, AllSessionKillPlain);  // Until CIPHERTEXT (the bug)

        Assert.NotEqual(correct, buggy);
        var macOverCorrect = MonkMode.ConfigIntegrity.ComputeConfigMac(correct, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(buggy, macOverCorrect, Key));
    }

    [Fact]
    public void LiteralNullApps_IsJustAnOrdinaryValue_AcrossAllWrappers()
    {
        // v9 stored [Process] List = "null" for "no apps" and every wrapper carried a
        // "decrypt this UNLESS it is the literal null" special case - four places to get
        // wrong. v10 (P9) retired the sentinel: Apps is never decrypted at all, so the
        // string "null" is just an ordinary value that passes straight through, and "no
        // apps" is "". Pin that no wrapper has quietly kept a special case: all four
        // agree, and the value survives verbatim.
        static void SetNullApps(Action<string, string, string> set) =>
            OneSlot.WriteSlot1(set,
                UntilEnc, "null", CustomSitesPlain, NowEnc, HighWaterEnc, CoolOffUntilEnc,
                PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain,
                ScheduleSpecPlain, ScheduleActiveUntilEnc, CoolOffDurationPlain, AllSessionKillPlain);

        var cliIni = new MonkMode.IniFile();
        SetNullApps((s, k, v) => cliIni.SetKeyValue(s, k, v));
        var srvIni = new monkmode.IniFile();
        SetNullApps((s, k, v) => srvIni.SetKeyValue(s, k, v));
        var guardIni = new mm_guard.IniFile();
        SetNullApps((s, k, v) => guardIni.SetKeyValue(s, k, v));
        var notifyIni = new mm_notify.IniFile();
        SetNullApps((s, k, v) => notifyIni.SetKeyValue(s, k, v));

        var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
        Assert.Contains("Slot1.Apps=null\n", cli);
        Assert.Equal(cli, MonkMode.Tests.TestSvc.New().CanonicalFromIni(srvIni));
        Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
        Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));

        // And the empty-apps shape (what v10 actually writes) also agrees everywhere.
        var emptyCli = new MonkMode.IniFile();
        OneSlot.WriteSlot1((s, k, v) => emptyCli.SetKeyValue(s, k, v),
            UntilEnc, "", CustomSitesPlain, NowEnc, HighWaterEnc, CoolOffUntilEnc,
            PartnerSaltPlain, PartnerHashPlain, PartnerUnlockedAtPlain, CommittedPlain,
            ScheduleSpecPlain, ScheduleActiveUntilEnc, CoolOffDurationPlain, AllSessionKillPlain);
        Assert.Contains("Slot1.Apps=\n", MonkMode.Blocker.CanonicalFromIni(emptyCli));
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
        //
        // v1.1 S2: the arm path emits the v10 [Slot1] section, so what this test compares is
        // a genuine two-level canonical - the global header plus one 16-line slot block -
        // and every enforcement field the CLI wrote is INSIDE it, i.e. MAC-covered.
        // (S1 shipped the four readers on v10 while the writer still emitted only the v9
        // single-block sections. In that window macValid stayed TRUE - the writer stamped
        // with the same v10 wrapper - while every v9-located field sat outside the canonical
        // and was therefore uncovered, tamper-OPEN rather than frozen.)
        // !! S2 closed that only for slot DATA, NOT for ENFORCEMENT - DO NOT ARM OR DEPLOY
        // THIS TREE UNTIL S3a. The service still reads the v9 mirror keys, which remain
        // outside the canonical: a raw edit to [Time] Until, [Time] CoolOffUntil, [User]
        // CustomSites, [Process] List, [Commit] Committed or [Partner] Hash/UnlockedAt keeps
        // macValid TRUE and is not tamper-evident. S3a moves the enforcement READERS onto the
        // slots - that is what actually closes it, and only then can the v9 mirror sections go.
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
            Assert.Equal(cli, MonkMode.Tests.TestSvc.New().CanonicalFromIni(srvIni));
            Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));
            Assert.StartsWith(Ver + "\n", cli);
            // Field-level coverage, end to end from the REAL write path: one slot, its id,
            // its packed site/app lists and its end - every one of them inside the canonical
            // the MAC is computed over, and byte-identical across all four readers above.
            Assert.Contains("NextSlotId=2\n", cli);
            Assert.Contains("SlotCount=1\n", cli);
            Assert.Contains("Slot1.Id=1\n", cli);
            Assert.Contains("Slot1.Sites=reddit.com;x.com;\n", cli);
            Assert.Contains("Slot1.Apps=chrome.exe;brave.exe;\n", cli);
            Assert.Contains("Slot1.Until=" + until.ToString(new System.Globalization.CultureInfo("en-CA")) + "\n", cli);
            Assert.Contains("Slot1.Committed=no\n", cli);
            // Exactly ONE slot block: 16 "Slot1." lines and no "Slot2." line at all.
            Assert.Equal(16, cli.Split('\n').Count(l => l.StartsWith("Slot1.")));
            Assert.DoesNotContain("Slot2.", cli);
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
