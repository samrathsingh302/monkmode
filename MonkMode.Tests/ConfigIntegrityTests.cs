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

// MonkMode.Tests - B7 tamper-evident config (the HMAC-SHA256 integrity MAC).
//
// B7 sits on top of the documented-weak Simple3Des layer: an attacker who
// recovers the hardcoded 3DES key can re-encrypt [Time] Until to "now" and
// write it into the ini to end a block early. The MAC closes that: the edited
// config no longer verifies, and the readers (service/guardian/notifier) treat
// a MAC-invalid config as STILL ACTIVE (fail CLOSED via EffectiveBlockHasExpired)
// so the block never auto-lifts until a legitimate re-stamp exists.
//
// What is pure and unit-tested here (no DPAPI, no files, no registry - raw keys
// injected directly):
//   - ConfigIntegrity.BuildCanonical / ComputeConfigMac / ConfigMacIsValid, the
//     three pure functions, including the pinned attack (a future-Until MAC must
//     not validate a now-Until canonical) and the fail-closed gate (False, never
//     throw, on a blank/null/non-Base64 stored MAC);
//   - the 4-copy parity: the same canonical+key yields an identical MAC across
//     the CLI/service/guardian/notifier copies (mirrors
//     CryptoRoundTripTests.AllFourCopies_ProduceIdenticalCiphertext), so the
//     deliberate per-project duplication can never drift;
//   - Service1.EffectiveBlockHasExpired / Guardian.EffectiveBlockHasExpired, the
//     MAC-aware expiry gates, pinned to agree with each other and to fail CLOSED
//     on macValid = false (the tamper-resistant direction).
//
// DPAPI (NewRandomKey/ProtectKey/UnprotectKey) is the live seam and is
// deliberately NOT exercised here - it hits the real machine DPAPI and is
// covered by the elevated smoke test, exactly like the B1/B2/B3 live wiring.

using System.Globalization;
using System.Text;

namespace MonkMode.Tests;

public class ConfigIntegrityTests
{
    // A fixed literal 32-byte test key (the production key is random + DPAPI-
    // protected; the pure functions take the raw key, so we inject a known one).
    private static readonly byte[] Key =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    };

    // A second, different key (one byte flipped) for the wrong-key test.
    private static readonly byte[] OtherKey =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0xFF,
    };

    // The schema version every BuildCanonical call passes as its first arg (C1
    // made the version a caller-supplied parameter). All four copies share it -
    // AllFourCopies_ShareTheSameSchemaVersion pins that - so passing the CLI copy's
    // value into the service/guardian/notifier copies below is faithful.
    private static readonly string Ver = MonkMode.ConfigIntegrity.CurrentSchemaVersion;

    // coolOffUntil "" = no cooling-off pending; the trailing "" are the C3b [Partner]
    // Salt/Hash/UnlockedAt, the C4 Committed flag, the C5b [Schedule] Spec/ActiveUntil,
    // the C6b [CoolOff] Duration and the D2c AllSessionKill flag (no partner code / not
    // committed / no schedule / default cooling-off / session-0-only kill - the common
    // shape here; those fields have dedicated tests in PartnerCodeTests / CommitBlockTests /
    // ScheduleTests / CoolOffTests / AllSessionKillTests).
    // v10: every one of those fields now lives in SLOT 1 of the two-level canonical.
    // OneSlot.Canonical (SlotCanonicalTests.cs) renders exactly that, from the same
    // argument list these MAC-coverage tests always used - the field moved, the fact
    // being pinned ("flip it and the stamp stops validating") did not.
    private static string Canonical(string until) =>
        OneSlot.Canonical(until, "chrome.exe;", "reddit.com;", "2026-06-25 12:00:00 p.m.", "2026-06-25 12:00:00 p.m.", "", "", "", "", "", "", "", "", "");

    [Fact]
    public void RoundTrip_ValidMac_Verifies()
    {
        var c = Canonical("2026-06-25 5:04:33 p.m.");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(c, Key);
        Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(c, mac, Key));
    }

    [Fact]
    public void TheB7Attack_FutureUntilMac_DoesNotValidateANowUntilCanonical()
    {
        // The pinned attack: the legitimate block ends far in the future, so the
        // real MAC is over the future-Until canonical. The evader recovers the
        // 3DES key and rewrites Until to ~now to end the block early - but the
        // stored MAC still covers the future Until, so the now-Until canonical
        // must NOT validate against it. (Without the MAC this edit would win.)
        var futureCanonical = Canonical("2026-12-31 11:59:59 p.m.");
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(futureCanonical, Key);

        var nowCanonical = Canonical("2026-06-25 12:00:01 p.m.");
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(nowCanonical, storedMac, Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("not-base64!!")]      // not valid Base64 at all
    [InlineData("=====")]             // malformed Base64 padding
    public void FailClosed_BadOrMissingStoredMac_IsFalse_NeverThrows(string? storedMac)
    {
        // The fail-closed gate: any unusable stored MAC reads as invalid (block
        // stays standing), and must not throw - it runs inside the enforcement
        // tick's Try, but defence in depth: never let bad input crash it.
        var c = Canonical("2026-06-25 5:04:33 p.m.");
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(c, storedMac!, Key));
    }

    [Fact]
    public void WrongKey_DoesNotValidate()
    {
        // A MAC stamped with one key must not verify under a different key (the
        // whole point of keying the HMAC - a per-block DPAPI-protected secret).
        var c = Canonical("2026-06-25 5:04:33 p.m.");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(c, Key);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(c, mac, OtherKey));
    }

    [Fact]
    public void TamperedCanonical_AnyCoveredField_DoesNotValidate()
    {
        // Each covered field is actually under the MAC: flip ProcessList,
        // CustomSites, Now, HighWater, CoolOffUntil, a [Partner] field, the Committed
        // flag or a [Schedule] field and the original MAC must reject the new
        // canonical. (B4: HighWater is covered - forging it past Until must fail; C2b:
        // CoolOffUntil is covered - forging the cooling-off deadline into the past
        // must fail; C3b: the [Partner] Salt/Hash/UnlockedAt are covered - swapping
        // the verifier or forging the UnlockedAt exit flag must fail verification too,
        // R6; C5b: ScheduleSpec/ScheduleActiveUntil are covered - rewriting the window
        // rule or forging the converted deadline into the past must fail too.)
        // The original carries non-empty [Schedule] tokens ("SS"/"SA") and a non-empty
        // C6b CoolOffDuration ("1800") so the schedule/duration cases below differ from
        // it in ONLY their one field.
        var original = OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "no", "SS", "SA", "1800", "");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(original, Key);

        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "b.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "evil.com;", "N", "HW", "CO", "PS", "PH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N2", "HW", "CO", "PS", "PH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        // B4: a forged HighWater (the clock-forward-by-config attack) must not validate.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "9999-01-01", "CO", "PS", "PH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        // C2b: a forged CoolOffUntil (the skip-the-wait-by-config attack) must not validate.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "1970-01-01", "PS", "PH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        // C3b/R6: a swapped-in partner Hash (the attacker's own verifier) must not
        // validate - that is what makes "tampered hash = no code valid = freeze".
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "ATTACKER-HASH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        // C3b: a forged salt must not validate either.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "ATTACKER-SALT", "PH", "", "no", "SS", "SA", "1800", ""), mac, Key));
        // C3b: a forged UnlockedAt (the raw-edit "I'm unlocked" exit-flag attack)
        // must not validate - so a non-empty UnlockedAt is only trusted under a
        // valid MAC, i.e. only if the service wrote it after verifying a code.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "2020-01-01 12:00:00 p.m.", "no", "SS", "SA", "1800", ""), mac, Key));
        // C4/R6: flipping the Committed flag (un-committing to re-enable cooling-off)
        // must not validate - so a committed block can never be silently un-committed
        // (the raw edit freezes it instead).
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "yes", "SS", "SA", "1800", ""), mac, Key));
        // C5b: a forged ScheduleSpec (rewriting the recurring-window rule) must not validate.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "no", "EVIL-SPEC", "SA", "1800", ""), mac, Key));
        // C5b: a forged ScheduleActiveUntil (a past value to end an open window early)
        // must not validate - the converted deadline is as unforgeable as CoolOffUntil.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "no", "SS", "1970-01-01", "1800", ""), mac, Key));
        // C6b: a forged CoolOffDuration (shortening the configured cooling-off wait by
        // raw edit) must not validate - so the duration is only trusted under a valid MAC.
        // (Defence in depth: even if it validated, the service's max(configured, floor)
        // clamp means a value below the floor can never shorten the wait below the floor.)
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "no", "SS", "SA", "0", ""), mac, Key));
        // D2c: flipping the AllSessionKill flag (original "" = session-0-only; forged "yes" to
        // widen, or a raw edit either way) must not validate - the flag is only trusted under a
        // valid MAC. (Defence in depth: it is a WIDEN-only union the service reads un-gated, so a
        // forged "yes" would only ever ADD kills; this pins it is nonetheless MAC-covered.)
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "CO", "PS", "PH", "", "no", "SS", "SA", "1800", "yes"), mac, Key));
    }

    [Fact]
    public void BuildCanonical_HasAStableFixedFormat()
    {
        // Pin the exact wire format for a known input. Every party derives the
        // canonical this way; a change to the order, the tag, the separators or
        // the field names would silently break cross-party MAC agreement, so the
        // string is pinned literally and the test fails loudly on any drift.
        // v10: this is the ONE-slot shape (the two-slot skeleton, which also pins
        // the ascending slot order, is SlotCanonicalTests.V10_ByteLiteral_TwoSlots).
        var c = OneSlot.Canonical("2026-06-25 5:04:33 p.m.", "chrome.exe;brave.exe;", "reddit.com;x.com;", "2026-06-25 12:00:00 p.m.", "2026-06-25 11:00:00 a.m.", "2026-06-25 12:30:00 p.m.", "c2FsdA==", "aGFzaA==", "2026-06-25 1:00:00 p.m.", "yes", "v1;12345:0900-1700;sites=reddit.com;apps=", "2026-06-25 2:00:00 p.m.", "5400", "yes");
        Assert.Equal(
            "v11\n" +
            "HighWater=2026-06-25 11:00:00 a.m.\n" +
            "Now=2026-06-25 12:00:00 p.m.\n" +
            "NextSlotId=2\n" +
            "SlotCount=1\n" +
            "GuardHoldUntil=\n" +
            "GuardArmedCount=1\n" +
            // FX1 (v11): the GLOBAL [Schedule] pair - "" here, because this shape is a
            // SLOT config; the schedule-only shape that populates them is pinned by
            // GlobalSchedule_IsMacCovered_* below.
            "ScheduleSpec=\n" +
            "ScheduleActiveUntil=\n" +
            "Slot1.Id=1\n" +
            "Slot1.StartAt=\n" +
            "Slot1.DurationSeconds=\n" +
            "Slot1.Until=2026-06-25 5:04:33 p.m.\n" +
            "Slot1.Sites=reddit.com;x.com;\n" +
            "Slot1.Apps=chrome.exe;brave.exe;\n" +
            "Slot1.UrlPatterns=\n" +
            "Slot1.AllSession=yes\n" +
            "Slot1.ScheduleSpec=v1;12345:0900-1700;sites=reddit.com;apps=\n" +
            "Slot1.ScheduleActiveUntil=2026-06-25 2:00:00 p.m.\n" +
            "Slot1.CoolOffUntil=2026-06-25 12:30:00 p.m.\n" +
            "Slot1.CoolOffDuration=5400\n" +
            "Slot1.PartnerSalt=c2FsdA==\n" +
            "Slot1.PartnerHash=aGFzaA==\n" +
            "Slot1.PartnerUnlockedAt=2026-06-25 1:00:00 p.m.\n" +
            "Slot1.Committed=yes\n",
            c);
    }

    [Fact]
    public void BuildCanonical_PassesEmptyTokensThroughVerbatim_AndTheNullSentinelIsJustAValue()
    {
        // "" is stored verbatim (a sites-only block; a "" CoolOffUntil = no cooling-off
        // pending), so an unset field must appear as a bare "Key=" line - the input just
        // has to be reproducible across parties, not interpreted. Every one of the 16
        // slot keys is ALWAYS emitted, so an absent key can never shorten one config's
        // canonical into another's.
        //
        // v10 (P9) RETIRED the "null" no-apps sentinel: v9 stored [Process] List="null"
        // and every wrapper special-cased "don't decrypt this one". v10 stores "" and
        // Apps is never decrypted at all, so "null" is now just an ordinary string that
        // passes straight through - pinned here so nobody reintroduces the special case.
        var c = OneSlot.Canonical("U", "", "", "N", "HW", "", "", "", "", "", "", "", "", "");
        Assert.Equal(
            "v11\nHighWater=HW\nNow=N\nNextSlotId=2\nSlotCount=1\nGuardHoldUntil=\nGuardArmedCount=1\n" +
            "ScheduleSpec=\nScheduleActiveUntil=\n" +
            "Slot1.Id=1\nSlot1.StartAt=\nSlot1.DurationSeconds=\nSlot1.Until=U\nSlot1.Sites=\nSlot1.Apps=\n" +
            "Slot1.UrlPatterns=\nSlot1.AllSession=\nSlot1.ScheduleSpec=\nSlot1.ScheduleActiveUntil=\n" +
            "Slot1.CoolOffUntil=\nSlot1.CoolOffDuration=\nSlot1.PartnerSalt=\nSlot1.PartnerHash=\n" +
            "Slot1.PartnerUnlockedAt=\nSlot1.Committed=\n", c);

        var withNull = OneSlot.Canonical("U", "null", "", "N", "HW", "", "", "", "", "", "", "", "", "");
        Assert.Contains("Slot1.Apps=null\n", withNull);
        Assert.NotEqual(c, withNull);
    }

    [Fact]
    public void ComputeConfigMac_IsBase64OfA32ByteDigest()
    {
        // HMAC-SHA256 is 32 bytes; the stamp is its Base64. Pins the algorithm
        // size (a switch to a shorter MAC would weaken B7).
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(Canonical("U"), Key);
        Assert.Equal(32, Convert.FromBase64String(mac).Length);
    }

    [Fact]
    public void ConstantTimeCompare_RejectsWrongLengthMac_WithoutThrowing()
    {
        // CryptographicOperations.FixedTimeEquals over the raw bytes returns
        // false (not throw) when the lengths differ - the observable contract of
        // the constant-time comparison. A valid MAC truncated by one byte is the
        // wrong length and must read invalid, no exception.
        var c = Canonical("2026-06-25 5:04:33 p.m.");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(c, Key);
        var truncated = Convert.ToBase64String(Convert.FromBase64String(mac)[..^1]);
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(c, truncated, Key));
    }

    [Fact]
    public void ConstantTimeCompare_RejectsAMacDifferingOnlyInTheLastByte()
    {
        // Same length, single-bit/byte difference: still rejected. (A naive
        // early-out compare would also reject it, but this pins that a
        // near-miss MAC never validates.)
        var c = Canonical("2026-06-25 5:04:33 p.m.");
        var bytes = Convert.FromBase64String(MonkMode.ConfigIntegrity.ComputeConfigMac(c, Key));
        bytes[^1] ^= 0x01;
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(c, Convert.ToBase64String(bytes), Key));
    }

    // ---- 4-copy parity (mirrors CryptoRoundTripTests for Simple3Des) ----
    //
    // ConfigIntegrity.vb is byte-for-byte identical in all four projects. If a
    // future edit updates one copy and not the others, the MAC would diverge and
    // the parties would stop agreeing - so the same canonical+key must produce
    // the same MAC across the CLI, service, guardian and notifier copies.

    public static readonly TheoryData<string> Untils = new()
    {
        "2026-06-25 5:04:33 p.m.",
        "2026-12-31 11:59:59 p.m.",
        "",
        "null",
    };

    [Theory]
    [MemberData(nameof(Untils))]
    public void AllFourCopies_ProduceIdenticalCanonical(string until)
    {
        // Called on each copy DIRECTLY (not through the OneSlot shim), because the whole
        // point is that the four byte-identical copies of BOTH v10 functions agree.
        // "GSS"/"GSA" are the v11 GLOBAL [Schedule] Spec/ActiveUntil, distinct from the
        // per-slot "SS"/"SA" so a copy that mixed the two up would diverge visibly.
        var cli = MonkMode.ConfigIntegrity.BuildCanonical(Ver, "HW", "N", "2", 1, "GH", "1", "GSS", "GSA",
            MonkMode.ConfigIntegrity.BuildSlotCanonical(1, "1", "SA1", "DS", until, "reddit.com;", "chrome.exe;", "UP", "AS", "SS", "SA", "CO", "CD", "PS", "PH", "PU", "CM"));
        var srv = monkmode.ConfigIntegrity.BuildCanonical(Ver, "HW", "N", "2", 1, "GH", "1", "GSS", "GSA",
            monkmode.ConfigIntegrity.BuildSlotCanonical(1, "1", "SA1", "DS", until, "reddit.com;", "chrome.exe;", "UP", "AS", "SS", "SA", "CO", "CD", "PS", "PH", "PU", "CM"));
        var guard = mm_guard.ConfigIntegrity.BuildCanonical(Ver, "HW", "N", "2", 1, "GH", "1", "GSS", "GSA",
            mm_guard.ConfigIntegrity.BuildSlotCanonical(1, "1", "SA1", "DS", until, "reddit.com;", "chrome.exe;", "UP", "AS", "SS", "SA", "CO", "CD", "PS", "PH", "PU", "CM"));
        var notify = mm_notify.ConfigIntegrity.BuildCanonical(Ver, "HW", "N", "2", 1, "GH", "1", "GSS", "GSA",
            mm_notify.ConfigIntegrity.BuildSlotCanonical(1, "1", "SA1", "DS", until, "reddit.com;", "chrome.exe;", "UP", "AS", "SS", "SA", "CO", "CD", "PS", "PH", "PU", "CM"));
        Assert.Equal(cli, srv);
        Assert.Equal(cli, guard);
        Assert.Equal(cli, notify);
    }

    [Theory]
    [MemberData(nameof(Untils))]
    public void AllFourCopies_ProduceIdenticalMac(string until)
    {
        var c = OneSlot.Canonical(until, "chrome.exe;", "reddit.com;", "N", "HW", "CO", "PS", "PH", "PU", "CM", "SS", "SA", "CD", "AS");
        var cli = MonkMode.ConfigIntegrity.ComputeConfigMac(c, Key);
        var srv = monkmode.ConfigIntegrity.ComputeConfigMac(c, Key);
        var guard = mm_guard.ConfigIntegrity.ComputeConfigMac(c, Key);
        var notify = mm_notify.ConfigIntegrity.ComputeConfigMac(c, Key);
        Assert.Equal(cli, srv);
        Assert.Equal(cli, guard);
        Assert.Equal(cli, notify);
    }

    [Fact]
    public void CliStamps_ServiceAndGuardianAndNotifierAllVerify()
    {
        // The live data flow: the CLI stamps the MAC; the service, guardian and
        // notifier each independently validate it (with the same recovered key).
        var c = Canonical("2026-06-25 5:04:33 p.m.");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(c, Key);
        Assert.True(monkmode.ConfigIntegrity.ConfigMacIsValid(c, mac, Key));
        Assert.True(mm_guard.ConfigIntegrity.ConfigMacIsValid(c, mac, Key));
        Assert.True(mm_notify.ConfigIntegrity.ConfigMacIsValid(c, mac, Key));
    }

    [Fact]
    public void AllFourCopies_ShareTheSameSchemaVersion()
    {
        // C1 made the version a per-copy constant each wrapper passes into
        // BuildCanonical. If a future bump updated one copy and not the others,
        // the CLI would stamp under one tag while a reader verified under another
        // - every MAC check would fail and every block would FREEZE. Pin that the
        // four byte-identical copies still agree on the value (mirrors the
        // canonical/MAC 4-copy parity above, for the version constant itself).
        Assert.Equal(MonkMode.ConfigIntegrity.CurrentSchemaVersion, monkmode.ConfigIntegrity.CurrentSchemaVersion);
        Assert.Equal(MonkMode.ConfigIntegrity.CurrentSchemaVersion, mm_guard.ConfigIntegrity.CurrentSchemaVersion);
        Assert.Equal(MonkMode.ConfigIntegrity.CurrentSchemaVersion, mm_notify.ConfigIntegrity.CurrentSchemaVersion);
    }

    [Fact]
    public void ForwardMigration_OldSchemaMacUnderCurrentCode_FailsClosed_FreezesBlock()
    {
        // R9 forward-migration freeze: a block armed by a PREVIOUS schema (the v2
        // that shipped before Section C) was stamped over a "v2\n..." canonical.
        // The upgraded binaries build the CURRENT canonical from the SAME decrypted
        // values, so the old stamp cannot validate it - macValid goes False, and
        // because EffectiveBlockHasExpired is (macValid AndAlso expired) the block
        // stays standing (freezes) instead of silently auto-lifting. The version is
        // the FIRST MAC-covered line, which is what makes this hold across a bump.
        // Operational rule this enforces: arm blocks AFTER upgrading, not across one.
        const string until = "2026-12-31 11:59:59 p.m.";
        const string proc = "chrome.exe;";
        const string sites = "reddit.com;";
        const string now = "2026-06-25 12:00:00 p.m.";
        const string hw = "2026-06-25 11:00:00 a.m.";

        // The OLD (v2) canonical a pre-upgrade CLI stamped, and its MAC. Built as a
        // literal so this test is independent of the current BuildCanonical format.
        var oldCanonical =
            "v2\n" +
            "Until=" + until + "\n" +
            "HighWater=" + hw + "\n" +
            "ProcessList=" + proc + "\n" +
            "CustomSites=" + sites + "\n" +
            "Now=" + now + "\n";
        var oldMac = MonkMode.ConfigIntegrity.ComputeConfigMac(oldCanonical, Key);

        // What the upgraded readers now build from the same values (an absent
        // CoolOffUntil, [Partner] fields, Committed, [Schedule] fields, the C6b
        // [CoolOff] Duration and the D2c AllSessionKill flag all read as "").
        var currentCanonical = OneSlot.Canonical(until, proc, sites, now, hw, "", "", "", "", "", "", "", "", "");
        Assert.StartsWith(Ver + "\n", currentCanonical);   // the version tag really changed
        Assert.NotEqual(oldCanonical, currentCanonical);

        // The old stamp does NOT validate the current canonical => macValid False.
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(currentCanonical, oldMac, Key);
        Assert.False(macValid);

        // End-to-end: even with a genuinely PAST Until, macValid False keeps the
        // block standing on both the service and guardian gates (the freeze).
        var pastUntil = new DateTime(2020, 1, 1, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        var asOf = new DateTime(2030, 1, 1, 12, 0, 0);
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(pastUntil, asOf, 5, macValid));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(pastUntil, asOf, 5, macValid));
    }

    [Fact]
    public void ForwardMigration_V8SchemaMacUnderCurrentCode_FailsClosed_FreezesBlock()
    {
        // The D2c instance of the R9 freeze, kept as a REGRESSION pin across every later
        // bump: a block armed under v8 (C6b [CoolOff] Duration present, NO [Process]
        // AllSession flag) runs under upgraded binaries. The current readers build a
        // different canonical from the same stored values, so the byte-exact old v8 stamp
        // cannot validate it - macValid goes False and the block FREEZES (never
        // silent-accept, never auto-lift). Mirrors
        // ForwardMigration_OldSchemaMacUnderCurrentCode for the v8 tag; op rule
        // "arm after upgrading, not across an upgrade" carries over.
        const string until = "2026-12-31 11:59:59 p.m.";
        const string proc = "chrome.exe;";
        const string sites = "reddit.com;";
        const string now = "2026-06-25 12:00:00 p.m.";
        const string hw = "2026-06-25 11:00:00 a.m.";
        const string co = "2026-06-25 12:30:00 p.m.";
        const string psalt = "PSALT";
        const string phash = "PHASH";
        const string sspec = "v1;12345:0900-1700;sites=reddit.com;apps=";
        const string sactive = "2026-06-25 2:00:00 p.m.";
        const string cd = "5400";

        // The OLD (v8) canonical a pre-D2c CLI stamped - built as a literal (version tag
        // first, WITH the [Partner]+Committed+[Schedule]+[CoolOff] Duration lines, NO
        // [Process] AllSession line) so this test is independent of the current BuildCanonical.
        var oldCanonical =
            "v8\n" +
            "Until=" + until + "\n" +
            "HighWater=" + hw + "\n" +
            "CoolOffUntil=" + co + "\n" +
            "ProcessList=" + proc + "\n" +
            "CustomSites=" + sites + "\n" +
            "Now=" + now + "\n" +
            "PartnerSalt=" + psalt + "\n" +
            "PartnerHash=" + phash + "\n" +
            "PartnerUnlockedAt=\n" +
            "Committed=yes\n" +
            "ScheduleSpec=" + sspec + "\n" +
            "ScheduleActiveUntil=" + sactive + "\n" +
            "CoolOffDuration=" + cd + "\n";
        var oldMac = MonkMode.ConfigIntegrity.ComputeConfigMac(oldCanonical, Key);

        // What the upgraded readers build from the same stored values, carried into
        // slot 1 of the v10 two-level canonical.
        var currentCanonical = OneSlot.Canonical(until, proc, sites, now, hw, co, psalt, phash, "", "yes", sspec, sactive, cd, "");
        Assert.Equal("v11", MonkMode.ConfigIntegrity.CurrentSchemaVersion);
        Assert.NotEqual(oldCanonical, currentCanonical);

        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(currentCanonical, oldMac, Key);
        Assert.False(macValid);

        // End-to-end through the REAL exit gates: even with a genuinely past Until,
        // an elapsed cooling-off deadline AND a (forged) code-unlock, macValid False
        // keeps the block standing on the heartbeat, the shared exit decision and
        // the guardian's stand-down - the freeze, never an early lift.
        var pastUntil = new DateTime(2020, 1, 1, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        var pastCoolOff = new DateTime(2020, 1, 2, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        var forgedUnlock = new DateTime(2020, 1, 3, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        var storedHw = new DateTime(2030, 1, 1, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        Assert.Equal(monkmode.Service1.HeartbeatAction.Hold,
            monkmode.Service1.ClassifyHeartbeat(macValid,
                monkmode.Service1.BlockHasExpired(pastUntil, new DateTime(2030, 1, 1, 12, 0, 0), 5),
                monkmode.Service1.CoolOffElapsedTime(pastCoolOff, storedHw),
                monkmode.Service1.PartnerUnlocked(forgedUnlock),
                monkmode.Service1.ScheduleActive("", storedHw), scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveExit(pastUntil, pastCoolOff, forgedUnlock, "", storedHw, 5, macValid, scheduleArmed: false));
        Assert.False(mm_guard.Guardian.EffectiveExit(pastUntil, pastCoolOff, forgedUnlock, "", storedHw, 5, macValid, scheduleArmed: false));
    }

    [Fact]
    public void ForwardMigration_V9SchemaMacUnderCurrentCode_FailsClosed_FreezesBlock()
    {
        // The v1.1 (multi-block) instance of the R9 freeze, and the one that matters
        // OPERATIONALLY right now: there is a real v9 monkmode_settings.ini deployed on
        // this machine. Under the current binaries the readers build a TWO-LEVEL canonical -
        // and, because the v9 file has no [Slots] section at all, ParseSlotCount returns
        // 0, so the v10 canonical carries the header and NO slot lines. Nothing about
        // the v9 stamp can validate that => macValid False => the block FREEZES.
        //
        // This is the CORRECT behaviour and is why S1 deliberately implements NO
        // migration: an old config is never silently re-blessed, and never auto-lifts.
        // (Re-arming after the upgrade rewrites the file; the stale-v9 rule that makes
        // a service-ABSENT rewrite possible is the arm path's business, not the
        // canonical's.) Built as a raw literal, like every ForwardMigration test, so it
        // stays independent of whatever BuildCanonical looks like later.
        const string until = "2026-12-31 11:59:59 p.m.";
        const string proc = "chrome.exe;";
        const string sites = "reddit.com;";
        const string now = "2026-06-25 12:00:00 p.m.";
        const string hw = "2026-06-25 11:00:00 a.m.";

        var oldCanonical =
            "v9\n" +
            "Until=" + until + "\n" +
            "HighWater=" + hw + "\n" +
            "CoolOffUntil=\n" +
            "ProcessList=" + proc + "\n" +
            "CustomSites=" + sites + "\n" +
            "Now=" + now + "\n" +
            "PartnerSalt=\n" +
            "PartnerHash=\n" +
            "PartnerUnlockedAt=\n" +
            "Committed=no\n" +
            "ScheduleSpec=\n" +
            "ScheduleActiveUntil=\n" +
            "CoolOffDuration=\n" +
            "AllSessionKill=\n";
        var oldMac = MonkMode.ConfigIntegrity.ComputeConfigMac(oldCanonical, Key);

        // The current canonical the upgraded readers derive from that same v9 FILE: the
        // encrypted globals still decrypt, but there is no [Slots] section, so
        // ParseSlotCount => 0 => zero slot blocks. (v11/FX1: the v9 file HAD a global
        // [Schedule] pair, and the current readers now cover it - here it is empty.)
        var currentFromAV9File = MonkMode.ConfigIntegrity.BuildCanonical(Ver, hw, now, "", 0, "", "", "", "", "");
        Assert.Equal("v11\nHighWater=" + hw + "\nNow=" + now + "\nNextSlotId=\nSlotCount=0\nGuardHoldUntil=\nGuardArmedCount=\nScheduleSpec=\nScheduleActiveUntil=\n", currentFromAV9File);
        Assert.NotEqual(oldCanonical, currentFromAV9File);

        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(currentFromAV9File, oldMac, Key);
        Assert.False(macValid);

        // ...and the same holds for the fully-populated current reading (a hand-migrated
        // file): the version tag alone already breaks the stamp.
        var currentPopulated = OneSlot.Canonical(until, proc, sites, now, hw, "", "", "", "", "no", "", "", "", "");
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(currentPopulated, oldMac, Key));

        // End-to-end through the REAL gates: even with a genuinely past Until the
        // block stays standing on the heartbeat, the shared exit and the guardian.
        var pastUntil = new DateTime(2020, 1, 1, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        var storedHw = new DateTime(2030, 1, 1, 12, 0, 0).ToString(new CultureInfo("en-CA"));
        Assert.Equal(monkmode.Service1.HeartbeatAction.Hold,
            monkmode.Service1.ClassifyHeartbeat(macValid,
                monkmode.Service1.BlockHasExpired(pastUntil, new DateTime(2030, 1, 1, 12, 0, 0), 5),
                monkmode.Service1.CoolOffElapsedTime("", storedHw),
                monkmode.Service1.PartnerUnlocked(""),
                monkmode.Service1.ScheduleActive("", storedHw), scheduleArmed: false));
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(pastUntil, new DateTime(2030, 1, 1, 12, 0, 0), 5, macValid));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(pastUntil, new DateTime(2030, 1, 1, 12, 0, 0), 5, macValid));
    }

    // ---- FX1 (v11): the GLOBAL [Schedule] pair is MAC-covered (the F1 fail-open) ----

    // A SCHEDULE-ONLY config's canonical: what `monkmode schedule` actually produces -
    // NO [Slots] section (so ParseSlotCount => 0 and there are zero slot blocks), the
    // past [Time] Until sentinel outside the canonical, and the whole armed state living
    // in the two GLOBAL [Schedule] header fields.
    private static string ScheduleOnlyCanonical(string spec, string activeUntil, string hw = "2026-06-25 11:00:00 a.m.") =>
        MonkMode.ConfigIntegrity.BuildCanonical(Ver, hw, "2026-06-25 12:00:00 p.m.", "", 0, "", "1", spec, activeUntil, "");

    [Fact]
    public void GlobalScheduleSpec_IsMacCovered_BlankingItFreezesInsteadOfTearingDown()
    {
        // THE F1 REGRESSION PIN. v10 covered the header + one block per armed SLOT, but
        // `monkmode schedule` writes a SLOT-LESS config whose enforcing value is the
        // GLOBAL [Schedule] Spec. With that key outside the canonical, blanking it in a
        // text editor kept macValid TRUE; the residual heartbeat then saw no armed
        // schedule beside a past-sentinel Until => Lift => TeardownAll, tearing a live
        // window down MID-WINDOW. v11 puts it back inside: the same edit fails the MAC.
        const string spec = "v1;12345:0900-1700;sites=reddit.com;apps=";
        var armed = ScheduleOnlyCanonical(spec, "");
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key);
        Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(armed, storedMac, Key));   // honest file verifies

        // The attack: blank the Spec, change nothing else.
        var blanked = ScheduleOnlyCanonical("", "");
        Assert.NotEqual(armed, blanked);                                                  // the canonical really moved
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(blanked, storedMac, Key);
        Assert.False(macValid);                                                           // ...so the stamp rejects it

        // End-to-end through the REAL gates: macValid False makes the heartbeat Hold and
        // the tick Hold - a FREEZE. (The control below is what v10 did: with macValid
        // still True the same state Lifts and ClassifyTick tears the machine down.)
        var pastSentinel = MonkMode.Blocker.ScheduleOnlyExpiredUntil;
        var asOf = new DateTime(2026, 6, 25, 12, 0, 0);
        var hw = asOf.ToString(new CultureInfo("en-CA"));
        var frozen = monkmode.Service1.ClassifyHeartbeat(macValid,
            monkmode.Service1.BlockHasExpired(pastSentinel, asOf, 5),
            monkmode.Service1.CoolOffElapsedTime("", hw), monkmode.Service1.PartnerUnlocked(""),
            monkmode.Service1.ScheduleActive("", hw), monkmode.Service1.ScheduleArmed(macValid, ""));
        Assert.Equal(monkmode.Service1.HeartbeatAction.Hold, frozen);
        Assert.Equal(monkmode.Service1.TickAction.Hold, monkmode.Service1.ClassifyTick(macValid, 0, frozen));

        // The v10 behaviour this closes, stated as the control: had the edit left the MAC
        // valid, the identical state would have LIFTED and torn everything down.
        var v10Style = monkmode.Service1.ClassifyHeartbeat(true,
            monkmode.Service1.BlockHasExpired(pastSentinel, asOf, 5),
            monkmode.Service1.CoolOffElapsedTime("", hw), monkmode.Service1.PartnerUnlocked(""),
            monkmode.Service1.ScheduleActive("", hw), monkmode.Service1.ScheduleArmed(true, ""));
        Assert.Equal(monkmode.Service1.HeartbeatAction.Lift, v10Style);
        Assert.Equal(monkmode.Service1.TickAction.TeardownAll, monkmode.Service1.ClassifyTick(true, 0, v10Style));
    }

    [Fact]
    public void GlobalScheduleActiveUntil_IsMacCovered_AndIsADistinctFieldFromTheSlotOne()
    {
        // The sibling key: the service-written open-window deadline. Forging it into the
        // past is the "end this window early" edit, so it must be as unforgeable as
        // CoolOffUntil. A canonical differing ONLY in it must fail the stored MAC.
        const string spec = "v1;12345:0900-1700;sites=reddit.com;apps=";
        var armed = ScheduleOnlyCanonical(spec, "2026-06-25 5:00:00 p.m.");
        var storedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key);

        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            ScheduleOnlyCanonical(spec, "1970-01-01 12:00:00 a.m."), storedMac, Key));   // forged into the past
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            ScheduleOnlyCanonical(spec, ""), storedMac, Key));                           // blanked entirely

        // And the GLOBAL pair is not the PER-SLOT pair: the same two values carried on a
        // slot produce a different canonical, so no reader can confuse the two.
        var global = MonkMode.ConfigIntegrity.BuildCanonical(Ver, "HW", "N", "2", 1, "", "1", "SPEC", "ACTIVE",
            MonkMode.ConfigIntegrity.BuildSlotCanonical(1, "1", "", "", "U", "", "", "", "", "", "", "", "", "", "", "", ""));
        var perSlot = MonkMode.ConfigIntegrity.BuildCanonical(Ver, "HW", "N", "2", 1, "", "1", "", "",
            MonkMode.ConfigIntegrity.BuildSlotCanonical(1, "1", "", "", "U", "", "", "", "", "SPEC", "ACTIVE", "", "", "", "", "", ""));
        Assert.NotEqual(global, perSlot);
        Assert.Contains("\nScheduleSpec=SPEC\n", global);
        Assert.Contains("\nSlot1.ScheduleSpec=SPEC\n", perSlot);
    }

    [Fact]
    public void ForwardMigration_V10SchemaMacUnderCurrentCode_FailsClosed_FreezesBlock()
    {
        // The FX1 instance of the R9 freeze: a block armed under v10 (the two-level
        // canonical WITHOUT the global [Schedule] pair) running under v11 binaries. Built
        // as a raw literal - never via today's BuildCanonical - so it stays an honest
        // record of the old wire format whatever the current one becomes.
        const string hw = "2026-06-25 11:00:00 a.m.";
        const string now = "2026-06-25 12:00:00 p.m.";
        const string spec = "v1;12345:0900-1700;sites=reddit.com;apps=";

        var oldCanonical =
            "v10\n" +
            "HighWater=" + hw + "\n" +
            "Now=" + now + "\n" +
            "NextSlotId=\n" +
            "SlotCount=0\n" +
            "GuardHoldUntil=\n" +
            "GuardArmedCount=1\n";
        var oldMac = MonkMode.ConfigIntegrity.ComputeConfigMac(oldCanonical, Key);

        // The v11 reading of the SAME schedule-only file: the version tag moved AND the
        // two global [Schedule] lines appeared, so the old stamp cannot validate it.
        var currentCanonical = ScheduleOnlyCanonical(spec, "");
        Assert.StartsWith("v11\n", currentCanonical);
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(currentCanonical, oldMac, Key);
        Assert.False(macValid);

        // ...and the freeze that follows: past sentinel + macValid False => Hold, never a
        // lift. Arm blocks AFTER upgrading the binaries, not across an upgrade.
        var asOf = new DateTime(2026, 6, 25, 12, 0, 0);
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(MonkMode.Blocker.ScheduleOnlyExpiredUntil, asOf, 5, macValid));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(MonkMode.Blocker.ScheduleOnlyExpiredUntil, asOf, 5, macValid));
    }
}

// The MAC-aware expiry gates: the block is expired ONLY when the time has
// genuinely passed AND the MAC is valid. An invalid MAC forces the "active"
// path (the tamper-resistant direction) without gating stopMe() on the MAC
// directly. The service and guardian copies must agree, exactly like
// BlockHasExpired does (WatchdogTests/GuardianTests pin that pairing).
public class EffectiveBlockHasExpiredTests
{
    private static readonly CultureInfo EnCa = new("en-CA");

    [Fact]
    public void PastUntil_ValidMac_IsExpired()
    {
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var asOf = until.AddMinutes(1);
        Assert.True(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), asOf, 5, macValid: true));
        Assert.True(mm_guard.Guardian.EffectiveBlockHasExpired(until.ToString(EnCa), asOf, 5, macValid: true));
    }

    [Fact]
    public void PastUntil_InvalidMac_IsNotExpired_BlockStaysStanding()
    {
        // The core B7 behaviour: a genuinely past end time would normally lift,
        // but a tampered/invalid MAC forces "active" so the block holds. This is
        // what stops the recover-the-3DES-key-and-rewrite-Until attack.
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var asOf = until.AddMinutes(1);
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), asOf, 5, macValid: false));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(until.ToString(EnCa), asOf, 5, macValid: false));
    }

    [Fact]
    public void FutureUntil_ValidMac_IsNotExpired()
    {
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var asOf = until.AddMinutes(-30);
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(until.ToString(EnCa), asOf, 5, macValid: true));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(until.ToString(EnCa), asOf, 5, macValid: true));
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("25.06.2026 17:04:33")] // legacy de-DE format
    [InlineData("")]
    public void UnparseableUntil_EvenWithValidMac_IsNotExpired(string stored)
    {
        // The two fail-closed axes are independent: an unparseable Until is not
        // expired regardless of the MAC (BlockHasExpired already fails closed).
        var asOf = new DateTime(2030, 1, 1, 12, 0, 0);
        Assert.False(monkmode.Service1.EffectiveBlockHasExpired(stored, asOf, 5, macValid: true));
        Assert.False(mm_guard.Guardian.EffectiveBlockHasExpired(stored, asOf, 5, macValid: true));
    }

    [Fact]
    public void ServiceAndGuardian_AgreeAcrossTheTruthTable()
    {
        // The pair must never disagree on "expired", or one side could stand
        // down while the other still enforces. Exhaustive over the inputs that
        // matter (past/future x valid/invalid MAC x parseable/not).
        var pastUntil = new DateTime(2026, 6, 25, 17, 0, 0).ToString(EnCa);
        var futureUntil = new DateTime(2026, 6, 25, 17, 0, 0).AddHours(2).ToString(EnCa);
        var asOf = new DateTime(2026, 6, 25, 17, 30, 0);

        foreach (var until in new[] { pastUntil, futureUntil, "garbage", "" })
        {
            foreach (var mac in new[] { true, false })
            {
                Assert.Equal(
                    monkmode.Service1.EffectiveBlockHasExpired(until, asOf, 5, mac),
                    mm_guard.Guardian.EffectiveBlockHasExpired(until, asOf, 5, mac));
            }
        }
    }
}
