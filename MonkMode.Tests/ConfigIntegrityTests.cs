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

    private static string Canonical(string until) =>
        MonkMode.ConfigIntegrity.BuildCanonical(until, "chrome.exe;", "reddit.com;", "2026-06-25 12:00:00 p.m.", "2026-06-25 12:00:00 p.m.");

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
        // CustomSites, Now or HighWater and the original MAC must reject the new
        // canonical. (B4: HighWater is now covered too - forging it past Until
        // must fail verification, which is the whole point of MAC-ing it.)
        var original = MonkMode.ConfigIntegrity.BuildCanonical("U", "a.exe;", "x.com;", "N", "HW");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(original, Key);

        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            MonkMode.ConfigIntegrity.BuildCanonical("U", "b.exe;", "x.com;", "N", "HW"), mac, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            MonkMode.ConfigIntegrity.BuildCanonical("U", "a.exe;", "evil.com;", "N", "HW"), mac, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            MonkMode.ConfigIntegrity.BuildCanonical("U", "a.exe;", "x.com;", "N2", "HW"), mac, Key));
        // B4: a forged HighWater (the clock-forward-by-config attack) must not validate.
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(
            MonkMode.ConfigIntegrity.BuildCanonical("U", "a.exe;", "x.com;", "N", "9999-01-01"), mac, Key));
    }

    [Fact]
    public void BuildCanonical_HasAStableFixedFormat()
    {
        // Pin the exact wire format for a known input. Every party derives the
        // canonical this way; a change to the order, the tag, the separators or
        // the field names would silently break cross-party MAC agreement, so the
        // string is pinned literally and the test fails loudly on any drift.
        var c = MonkMode.ConfigIntegrity.BuildCanonical(
            "2026-06-25 5:04:33 p.m.", "chrome.exe;brave.exe;", "reddit.com;x.com;", "2026-06-25 12:00:00 p.m.", "2026-06-25 11:00:00 a.m.");
        Assert.Equal(
            "v2\n" +
            "Until=2026-06-25 5:04:33 p.m.\n" +
            "HighWater=2026-06-25 11:00:00 a.m.\n" +
            "ProcessList=chrome.exe;brave.exe;\n" +
            "CustomSites=reddit.com;x.com;\n" +
            "Now=2026-06-25 12:00:00 p.m.\n",
            c);
    }

    [Fact]
    public void BuildCanonical_PassesNullAndEmptyTokensThroughVerbatim()
    {
        // "null"/"" are stored verbatim (an apps-only or sites-only block), so
        // they must appear in the canonical unchanged - the input just has to be
        // reproducible across parties, not interpreted.
        var c = MonkMode.ConfigIntegrity.BuildCanonical("U", "null", "", "N", "HW");
        Assert.Equal("v2\nUntil=U\nHighWater=HW\nProcessList=null\nCustomSites=\nNow=N\n", c);
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
        var cli = MonkMode.ConfigIntegrity.BuildCanonical(until, "chrome.exe;", "reddit.com;", "N", "HW");
        var srv = monkmode.ConfigIntegrity.BuildCanonical(until, "chrome.exe;", "reddit.com;", "N", "HW");
        var guard = mm_guard.ConfigIntegrity.BuildCanonical(until, "chrome.exe;", "reddit.com;", "N", "HW");
        var notify = mm_notify.ConfigIntegrity.BuildCanonical(until, "chrome.exe;", "reddit.com;", "N", "HW");
        Assert.Equal(cli, srv);
        Assert.Equal(cli, guard);
        Assert.Equal(cli, notify);
    }

    [Theory]
    [MemberData(nameof(Untils))]
    public void AllFourCopies_ProduceIdenticalMac(string until)
    {
        var c = MonkMode.ConfigIntegrity.BuildCanonical(until, "chrome.exe;", "reddit.com;", "N", "HW");
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
