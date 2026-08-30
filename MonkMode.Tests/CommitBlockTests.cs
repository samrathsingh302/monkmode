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
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private static readonly string HwText = Hw.ToString(EnCa);
    private static readonly string FutureUntil = Hw.AddHours(8).ToString(EnCa);
    private const string SaltB64 = "AAECAwQFBgcICQoLDA0ODw==";  // 16 bytes
    private const string HashB64 = "cGFydG5lci1oYXNoLXBsYWNlaG9sZGVyLTMyLWJ5dGVz";

    // A committed armed config's canonical (Committed="yes", UnlockedAt="") + its MAC.
    private static string CommittedCanonical(string unlockedAt, string committed) =>
        OneSlot.Canonical(FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, unlockedAt, committed, "", "", "", "");

    [Fact]
    public void CommittedBlock_KeepsTheCodeExit()
    {
        // Armed committed, healthy (valid MAC).
        var armed = CommittedCanonical("", "yes");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            armed, MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key), Key);
        Assert.True(macValid);
        Assert.True(monkmode.Service1.IsCommitted("yes"));

        // The partner code channel Verifies (it has no committed axis, and never had one)...
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Verify,
            monkmode.Service1.ClassifyPartnerCodeSignal(codePresent: true, candidateNonEmpty: true, alreadyUnlocked: false, macValid: macValid));

        // ...and a verified code lifts the committed block: since ledger 319 this and the end
        // time are the ONLY two exits, for every block.
        var unlockedAt = Hw.AddMinutes(1).ToString(EnCa);
        var unlocked = CommittedCanonical(unlockedAt, "yes");
        var unlockedMacValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            unlocked, MonkMode.ConfigIntegrity.ComputeConfigMac(unlocked, Key), Key);
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, unlockedAt, "", HwText, 5, unlockedMacValid, scheduleArmed: false));
    }

    // LEDGER 319: the C4 asymmetry is GONE because the weaker half of it is gone. There used to
    // be two grades of block - uncommitted (cooling-off OR code) and committed (code only) - and
    // NotCommittedBlock_AllowsCoolingOff pinned the difference. Every block is committed now, and
    // more to the point there is no cooling-off machinery left to allow: what this test asserts
    // is that a config which still SAYS Committed="no" (an old file, or a forged one under a
    // valid MAC) buys no cooling-off exit, because there is none to buy.
    [Fact]
    public void EvenAnUncommittedConfig_HasNoCoolingOffExit()
    {
        var armed = CommittedCanonical("", "no");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            armed, MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key), Key);
        Assert.True(macValid);
        Assert.False(monkmode.Service1.IsCommitted("no"));

        // A valid MAC, an unexpired block, an uncommitted flag: no exit. The follow-up slice
        // removed the cool-off argument from both gates, so the deadline that used to be fed
        // in here cannot even be offered; it is fed to the per-slot gate instead, which is the
        // only one that still accepts it (CoolOffTests.APerSlotElapsedDeadline_...).
        var elapsedCoolOff = Hw.AddHours(-5).ToString(EnCa);
        var slot = new monkmode.Service1.SlotState
        {
            Id = "1", UntilText = FutureUntil, CoolOffUntil = elapsedCoolOff, Committed = "no",
        };
        Assert.Equal(monkmode.Service1.SlotAction.Hold,
            monkmode.Service1.SlotExitDue(slot, Hw, 5, macValid, HwText));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", HwText, 5, macValid, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, "", "", HwText, 5, macValid, scheduleArmed: false));
    }

    [Fact]
    public void UnCommittingByRawEdit_FreezesTheBlock()
    {
        // R6 / "stays committed": the block was armed committed (MAC over Committed="yes").
        // An attacker flips it to "no" by a raw ini edit. The canonical changes, so the stored
        // MAC no longer validates the flipped config => macValid False => the block FREEZES.
        // Ledger 319: there is no longer even a prize for winning this - the flag gates nothing.
        var committedArmed = CommittedCanonical("", "yes");
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(committedArmed, Key);

        var flippedToNo = CommittedCanonical("", "no");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(flippedToNo, storedMac, Key);
        Assert.False(macValid);   // the un-commit broke the MAC

        // No exit gate lifts it (frozen, not lifted).
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", HwText, 5, macValid, scheduleArmed: false));
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
            Assert.Equal("yes", ini.GetKeyValue("Slot1", "Committed"));
            // Ledger 319 deleted Blocker.BlockIsCommitted (its only caller was the cooling-off
            // gate), so the field is read straight off the ini here. The stamped MAC still
            // covers it - CommittedFlag_IsMacCovered_AnyFlipFailsVerification pins that.
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Fact]
    public void WriteConfig_NotCommitted_StoresNo()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        try
        {
            // Default committed:=false. The WRITER still takes the flag both ways - ledger 319
            // put the "every block is committed" policy in the CLI's DoBlock (which now always
            // passes True), deliberately NOT in the writer, so the canonical shape is unchanged
            // and an old config that says "no" still round-trips.
            MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, Array.Empty<string>(),
                new DateTime(2026, 12, 31, 23, 59, 59));
            var ini = new MonkMode.IniFile();
            ini.Load(iniPath);
            Assert.Equal("no", ini.GetKeyValue("Commit", "Committed"));
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
