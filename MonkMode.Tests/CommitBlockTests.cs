// MonkMode.Tests - C4 commit blocks (--commit).
//
// A committed block SURRENDERS the self-serve cooling-off exit (code-only exit): the
// service refuses a cooling-off Start when committed, while the partner code + natural
// expiry still lift it. The flag is a NEW MAC-covered [Commit] Committed field
// ("yes"/"no"), set once at arm by the CLI. The §6.5 asymmetry that makes "commit =
// code-only exit" true: ClassifyCoolOffSignal GATES on committed (refuses), but
// ClassifyPartnerCodeSignal deliberately does NOT (the code is the kept exit).
//
// Fail-closed = STAYS committed: the flag rides the canonical, so flipping it by raw
// edit (un-committing to re-enable cooling-off) breaks the MAC -> macValid=False ->
// the whole block FREEZES (cooling-off Ignored regardless), so an attacker can never
// silently un-commit. Verified here through the pure gate (IsCommitted), the classifier
// asymmetry, the canonical/MAC (raw injected key, no DPAPI), and the real CLI WriteConfig
// path (which stamps a DPAPI MAC, same fence as CanonicalParityTests' CliWriteConfig).

using System.Globalization;

namespace MonkMode.Tests;

public class IsCommittedTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData(" yes ", true)]   // trimmed
    [InlineData("Yes", true)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]     // the empty-value ini round-trip reloads as Nothing
    [InlineData("true", false)]   // ONLY "yes" commits - anything else is not committed
    [InlineData("1", false)]
    public void OnlyYes_IsCommitted_EverythingElseIsNot(string? flag, bool expected)
    {
        Assert.Equal(expected, monkmode.Service1.IsCommitted(flag!));
    }
}

// End-to-end through the REAL gates + BuildCanonical/MAC (raw injected key, no DPAPI).
public class CommitBlockEndToEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly string Ver = MonkMode.ConfigIntegrity.CurrentSchemaVersion;
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private static readonly string HwText = Hw.ToString(EnCa);
    private static readonly string FutureUntil = Hw.AddHours(8).ToString(EnCa);
    private const string SaltB64 = "AAECAwQFBgcICQoLDA0ODw==";  // 16 bytes
    private const string HashB64 = "cGFydG5lci1oYXNoLXBsYWNlaG9sZGVyLTMyLWJ5dGVz";

    // A committed armed config's canonical (Committed="yes", UnlockedAt="") + its MAC.
    private static string CommittedCanonical(string unlockedAt, string committed) =>
        MonkMode.ConfigIntegrity.BuildCanonical(Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, unlockedAt, committed, "", "", "");

    [Fact]
    public void CommittedBlock_RefusesCoolingOff_ButTheCodeStillExits_TheC4Seam()
    {
        // Armed committed, healthy (valid MAC).
        var armed = CommittedCanonical("", "yes");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            armed, MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key), Key);
        Assert.True(macValid);
        var committed = monkmode.Service1.IsCommitted("yes");
        Assert.True(committed);

        // Cooling-off is REFUSED (Ignored) on a committed block...
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            monkmode.Service1.ClassifyCoolOffSignal(requestPresent: true, cancelPresent: false,
                coolOffPending: false, committed: committed, macValid: macValid));

        // ...but the partner code channel is UNAFFECTED (no committed axis)...
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Verify,
            monkmode.Service1.ClassifyPartnerCodeSignal(codePresent: true, candidateNonEmpty: true, alreadyUnlocked: false, macValid: macValid));

        // ...and a verified code lifts the committed block (the kept exit).
        var unlockedAt = Hw.AddMinutes(1).ToString(EnCa);
        var unlocked = CommittedCanonical(unlockedAt, "yes");
        var unlockedMacValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            unlocked, MonkMode.ConfigIntegrity.ComputeConfigMac(unlocked, Key), Key);
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, "", unlockedAt, "", HwText, 5, unlockedMacValid, scheduleArmed: false));
    }

    [Fact]
    public void NotCommittedBlock_AllowsCoolingOff()
    {
        // The contrast: an un-committed healthy block DOES start cooling-off.
        var armed = CommittedCanonical("", "no");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            armed, MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key), Key);
        Assert.Equal(monkmode.Service1.CoolOffAction.Start,
            monkmode.Service1.ClassifyCoolOffSignal(requestPresent: true, cancelPresent: false,
                coolOffPending: false, committed: monkmode.Service1.IsCommitted("no"), macValid: macValid));
    }

    [Fact]
    public void UnCommittingByRawEdit_FreezesTheBlock_CannotReEnableCoolingOff()
    {
        // R6 / "stays committed": the block was armed committed (MAC over Committed="yes").
        // An attacker flips it to "no" by a raw ini edit to re-enable the easy cooling-off
        // exit. The canonical changes, so the stored MAC no longer validates the flipped
        // config => macValid False => the block FREEZES: ClassifyCoolOffSignal Ignores it
        // (macValid required), so the un-commit yields a frozen block, NOT cooling-off.
        var committedArmed = CommittedCanonical("", "yes");
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(committedArmed, Key);

        var flippedToNo = CommittedCanonical("", "no");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(flippedToNo, storedMac, Key);
        Assert.False(macValid);   // the un-commit broke the MAC

        // Under macValid=False the flipped "no" buys nothing - cooling-off is Ignored.
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            monkmode.Service1.ClassifyCoolOffSignal(requestPresent: true, cancelPresent: false,
                coolOffPending: false, committed: monkmode.Service1.IsCommitted("no"), macValid: macValid));
        // And no exit gate lifts it either (frozen, not lifted).
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", "", HwText, 5, macValid, scheduleArmed: false));
    }

    [Fact]
    public void CommittedFlag_IsMacCovered_AnyFlipFailsVerification()
    {
        // Both directions: yes->no and no->yes must fail the MAC (the flag is covered).
        var yes = CommittedCanonical("", "yes");
        var no = CommittedCanonical("", "no");
        var yesMac = MonkMode.ConfigIntegrity.ComputeConfigMac(yes, Key);
        var noMac = MonkMode.ConfigIntegrity.ComputeConfigMac(no, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(no, yesMac, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(yes, noMac, Key));
    }
}

// The real CLI WriteConfig path (stamps a DPAPI [Integrity] Key/Mac - same fence as
// CanonicalParityTests' CliWriteConfig: only the test-bin ini/backup are written).
// Writes the shared test-bin monkmode_settings.ini via Blocker.WriteConfig; the
// "CliIniWriters" collection serialises it with the other ini-writing test classes.
[Collection("CliIniWriters")]
public class CommitWriteConfigTests
{
    [Fact]
    public void WriteConfig_Committed_StoresYes_AndBlockIsCommittedReportsTrue()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        try
        {
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, Array.Empty<string>(),
                new DateTime(2026, 12, 31, 23, 59, 59), committed: true);
            var ini = new MonkMode.IniFile();
            ini.Load(iniPath);
            Assert.Equal("yes", ini.GetKeyValue("Commit", "Committed"));
            // The MAC-gated CLI reader agrees (and the stamped MAC validates over the
            // [Commit] field - if it didn't, BlockIsCommitted would return False).
            Assert.True(MonkMode.Blocker.BlockIsCommitted());
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Fact]
    public void WriteConfig_NotCommitted_StoresNo_AndBlockIsCommittedReportsFalse()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        try
        {
            // Default committed:=false.
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, Array.Empty<string>(),
                new DateTime(2026, 12, 31, 23, 59, 59));
            var ini = new MonkMode.IniFile();
            ini.Load(iniPath);
            Assert.Equal("no", ini.GetKeyValue("Commit", "Committed"));
            Assert.False(MonkMode.Blocker.BlockIsCommitted());
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
