// MonkMode.Tests - C3b partner accountability code (R1 - the FAST service-
// adjudicated early exit; design: vault\dev\monk-mode\plans\C3a-partner-code-design.md).
//
// The mechanism: at arm the CLI generates a random code, prints it ONCE, and stores
// ONLY a salted one-way KDF hash (plaintext-in-ini, MAC-covered). `unblock --code`
// drops the ONE content-bearing trigger with the candidate; on its next tick the
// SERVICE (the sole verifier + sole stopMe() caller, R1) derives KDF(salt, candidate),
// constant-time-compares it to the MAC-covered [Partner] Hash and, on a match, sets
// the MAC-covered [Partner] UnlockedAt exit flag - the EXISTING EffectiveExit
// machinery (tick/OnStart/guardian) then lifts via the SAME stopMe() natural expiry
// and cooling-off use.
//
// What is pure and unit-tested here (no DPAPI, no real hosts/registry/SCM):
//   - the pinned consts: PartnerCodeFileName CLI<->service parity, the PD2 code
//     length/alphabet and PD3 KDF-iteration/salt parameters (a retune is one loud edit);
//   - GeneratePartnerCode / NormalisePartnerCode / ComputePartnerHash / PartnerCodeMatches,
//     each fail-closed on every axis, with the relay-variant round-trip and 4-copy parity;
//   - PartnerUnlocked (service<->guardian parity) and the full ClassifyPartnerCodeSignal matrix;
//   - end-to-end through the REAL exit gates + BuildCanonical/MAC: correct code sets
//     UnlockedAt -> EffectiveExit true (service AND guardian) -> lift; wrong/blank held;
//     tampered/swapped/deleted hash and forged UnlockedAt freeze (R6); the guardian
//     can't resurrect a code-unlocked block; committed keeps the code exit (the C4 seam);
//     the C1b backup carries the verifier across a corrupt-then-restore; rotate-on-use.
//
// The live wiring (ProcessPartnerCodeSignal's file I/O + ini save + backup refresh, the
// CLI trigger writer, the block-time generate/show-once) is the smoke-tested seam (CV
// C-core smoke), exactly like the C2b cooling-off live wiring. The DPAPI [Integrity] Key
// seam is untouched (raw injected keys, like ConfigIntegrityTests/CoolOffTests).

using System.Globalization;

namespace MonkMode.Tests;

public class PartnerCodeConstTests
{
    [Fact]
    public void TriggerFileName_IsStable_AndParityAcrossCliAndService()
    {
        // The CLI drops the file; the service polls for it. A drift would silently
        // break the channel (attempts never seen), so both copies are pinned to the
        // exact string, like the CoolOff*FileName parity.
        Assert.Equal("monkmode_partner.code", MonkMode.Blocker.PartnerCodeFileName);
        Assert.Equal(MonkMode.Blocker.PartnerCodeFileName, monkmode.Service1.PartnerCodeFileName);
    }

    [Fact]
    public void CodeLengthAndAlphabet_ArePinned_PD2()
    {
        // PD2: 10 Crockford-base32 chars (~2^50). A retune is a single loud edit.
        Assert.Equal(10, MonkMode.ConfigIntegrity.PartnerCodeLength);
        Assert.Equal("0123456789ABCDEFGHJKMNPQRSTVWXYZ", MonkMode.ConfigIntegrity.PartnerCodeAlphabet);
        // Crockford excludes the ambiguous I, L, O, U from the ENCODE alphabet.
        Assert.DoesNotContain('I', MonkMode.ConfigIntegrity.PartnerCodeAlphabet);
        Assert.DoesNotContain('L', MonkMode.ConfigIntegrity.PartnerCodeAlphabet);
        Assert.DoesNotContain('O', MonkMode.ConfigIntegrity.PartnerCodeAlphabet);
        Assert.DoesNotContain('U', MonkMode.ConfigIntegrity.PartnerCodeAlphabet);
        Assert.Equal(32, MonkMode.ConfigIntegrity.PartnerCodeAlphabet.Length);
    }

    [Fact]
    public void KdfParameters_ArePinned_PD3_AndNotTrivial()
    {
        // PD3: PBKDF2-HMAC-SHA256 >= 600,000 iterations over a 16-byte salt, 32-byte
        // hash. The hash is readable on the attacker's own disk, so the KDF cost is
        // the load-bearing offline-brute-force floor - pin a hard lower bound so a
        // future edit can't silently gut it.
        Assert.Equal(600000, MonkMode.ConfigIntegrity.PartnerKdfIterations);
        Assert.True(MonkMode.ConfigIntegrity.PartnerKdfIterations >= 100000);
        Assert.Equal(16, MonkMode.ConfigIntegrity.PartnerSaltBytes);
        Assert.Equal(32, MonkMode.ConfigIntegrity.PartnerHashBytes);
    }

    [Fact]
    public void AllFourCopies_ShareThePartnerConsts()
    {
        // ConfigIntegrity.vb is byte-identical across the four projects; pin that the
        // partner parameters agree, or the CLI could hash under different parameters
        // than the service verifies with and every code would fail.
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerKdfIterations, monkmode.ConfigIntegrity.PartnerKdfIterations);
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerKdfIterations, mm_guard.ConfigIntegrity.PartnerKdfIterations);
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerKdfIterations, mm_notify.ConfigIntegrity.PartnerKdfIterations);
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerCodeAlphabet, monkmode.ConfigIntegrity.PartnerCodeAlphabet);
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerCodeAlphabet, mm_guard.ConfigIntegrity.PartnerCodeAlphabet);
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerCodeAlphabet, mm_notify.ConfigIntegrity.PartnerCodeAlphabet);
    }
}

public class NormalisePartnerCodeTests
{
    private static string N(string s) => MonkMode.ConfigIntegrity.NormalisePartnerCode(s);

    [Fact]
    public void StripsSeparators_AndUppercases()
    {
        Assert.Equal("ABCDE12345", N("abcde-12345"));
        Assert.Equal("ABCDE12345", N("ABCDE 12345"));
        Assert.Equal("ABCDE12345", N("  abcde-1 2 3 4 5  "));
    }

    [Fact]
    public void AppliesCrockfordAmbiguousCharCanonicalisation()
    {
        // I/L -> 1 and O -> 0 at BOTH gen and verify, so a mis-transcribed relay
        // still matches. The generated alphabet never contains I/L/O, so this only
        // ever forgives a human relay error.
        Assert.Equal("11110", N("ILlio"));   // upper "ILLIO": I->1, L->1, L->1, I->1, O->0
        Assert.Equal("10", N("1O"));         // digits + O->0
    }

    [Fact]
    public void BlankAndNull_NormaliseToEmpty()
    {
        Assert.Equal("", N(""));
        Assert.Equal("", N("   -  "));
        Assert.Equal("", N(null!));
    }

    [Fact]
    public void RelayVariantsOfAGeneratedCode_AllNormaliseIdentically()
    {
        // A generated display code and its lower-case / spaced / separator-mangled
        // relay variants must all normalise to the same string (so they all verify).
        var code = MonkMode.ConfigIntegrity.GeneratePartnerCode();  // e.g. "ABCDE-12345"
        var canonical = N(code);
        Assert.Equal(canonical, N(code.ToLowerInvariant()));
        Assert.Equal(canonical, N(code.Replace("-", " ")));
        Assert.Equal(canonical, N(code.Replace("-", "")));
        Assert.Equal(canonical, N("  " + code + "  "));
    }
}

public class GeneratePartnerCodeTests
{
    [Fact]
    public void HasThePinnedShape_LengthAlphabetAndGrouping()
    {
        var code = MonkMode.ConfigIntegrity.GeneratePartnerCode();
        // Grouped XXXXX-XXXXX: 10 code chars + one separator.
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerCodeLength + 1, code.Length);
        Assert.Contains('-', code);
        var normalised = MonkMode.ConfigIntegrity.NormalisePartnerCode(code);
        Assert.Equal(MonkMode.ConfigIntegrity.PartnerCodeLength, normalised.Length);
        // Every normalised char is from the generation alphabet (no separators, no
        // ambiguous chars survive).
        foreach (var ch in normalised)
            Assert.Contains(ch, MonkMode.ConfigIntegrity.PartnerCodeAlphabet);
    }

    [Fact]
    public void SuccessiveCodes_Differ_RotateOnUseIsMeaningful()
    {
        // Rotate-on-use only matters if each arm mints a genuinely different code.
        // 50 draws of ~2^50 must not collide (a collision here means broken entropy).
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
            Assert.True(seen.Add(MonkMode.ConfigIntegrity.GeneratePartnerCode()));
    }
}

public class PartnerCodeMatchesTests
{
    // A fixed salt so the (expensive) KDF is exercised a bounded number of times.
    private static readonly byte[] Salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly string SaltB64 = Convert.ToBase64String(Salt);
    private const string Code = "ABCDE-12345";
    private static readonly string Hash = MonkMode.ConfigIntegrity.ComputePartnerHash(Salt, Code);

    [Fact]
    public void CorrectCode_Matches()
    {
        Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches(Code, SaltB64, Hash));
    }

    [Fact]
    public void RelayVariantsOfTheCode_StillMatch()
    {
        // Lower-case, separator-mangled and Crockford-ambiguous relays all verify
        // (Normalise is applied identically at gen-hash and verify).
        Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches("abcde12345", SaltB64, Hash));
        Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches("ABCDE 12345", SaltB64, Hash));
        // The code has no ambiguous chars, so an O-for-0 mishear of a code containing
        // a real '0' round-trips; here just confirm a spaced/cased relay matches.
        Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches("  abcde-12345  ", SaltB64, Hash));
    }

    [Fact]
    public void WrongCode_DoesNotMatch()
    {
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches("ZZZZZ-99999", SaltB64, Hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankCandidate_NeverMatches_FailClosed(string? candidate)
    {
        // Guarded before the KDF; a blank/whitespace/null candidate is never a match.
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches(candidate!, SaltB64, Hash));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "aGFzaA==")]
    [InlineData("c2FsdA==", "")]
    public void MissingVerifier_NeverMatches_FailClosed(string salt, string hash)
    {
        // No stored verifier (blank salt or hash) => no code is valid (fail-closed),
        // guarded before the KDF.
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches(Code, salt, hash));
    }

    [Theory]
    [InlineData("not-base64!!", "aGFzaA==")]
    [InlineData("c2FsdA==", "not-base64!!")]
    public void UnBase64SaltOrHash_NeverMatches_NeverThrows(string salt, string hash)
    {
        // A corrupted (un-Base64) salt/hash can only ever WITHHOLD a lift, never grant
        // one - and never throws (it runs inside the tick's Try, but defence in depth).
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches(Code, salt, hash));
    }

    [Fact]
    public void WrongLengthStoredHash_DoesNotMatch()
    {
        // FixedTimeEquals returns false (not throw) on a length mismatch: a truncated
        // stored hash reads as "not a match".
        var truncated = Convert.ToBase64String(Convert.FromBase64String(Hash)[..^1]);
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches(Code, SaltB64, truncated));
    }

    [Fact]
    public void GenHashVerify_RoundTrips_WithAFreshSaltAndCode()
    {
        // The full CLI-side path: a fresh salt + generated code -> stored hash ->
        // verifies; a different code against the same verifier does not.
        var salt = MonkMode.ConfigIntegrity.NewPartnerSalt();
        var code = MonkMode.ConfigIntegrity.GeneratePartnerCode();
        var hash = MonkMode.ConfigIntegrity.ComputePartnerHash(salt, code);
        Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches(code, Convert.ToBase64String(salt), hash));
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches(
            MonkMode.ConfigIntegrity.GeneratePartnerCode(), Convert.ToBase64String(salt), hash));
    }

    [Fact]
    public void FreshSalt_IsSixteenBytes_AndRandom()
    {
        var a = MonkMode.ConfigIntegrity.NewPartnerSalt();
        var b = MonkMode.ConfigIntegrity.NewPartnerSalt();
        Assert.Equal(16, a.Length);
        Assert.NotEqual(Convert.ToBase64String(a), Convert.ToBase64String(b));
    }

    [Fact]
    public void AllFourCopies_VerifyTheSameStamp_Parity()
    {
        // The CLI hashes; the service (the verifier) must agree, and all four copies
        // are byte-identical - pin that the CLI's stored hash verifies under every
        // copy's PartnerCodeMatches (and a wrong code fails under every copy).
        Assert.True(monkmode.ConfigIntegrity.PartnerCodeMatches(Code, SaltB64, Hash));
        Assert.True(mm_guard.ConfigIntegrity.PartnerCodeMatches(Code, SaltB64, Hash));
        Assert.True(mm_notify.ConfigIntegrity.PartnerCodeMatches(Code, SaltB64, Hash));
        Assert.False(monkmode.ConfigIntegrity.PartnerCodeMatches("ZZZZZ-99999", SaltB64, Hash));
    }
}

public class PartnerUnlockedTests
{
    [Theory]
    [InlineData("2026-06-25 1:15:00 p.m.", true)]
    [InlineData("anything-non-empty", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void NonEmpty_IsUnlocked_FailClosed(string? unlockedAt, bool expected)
    {
        Assert.Equal(expected, monkmode.Service1.PartnerUnlocked(unlockedAt!));
        // Service<->guardian parity: the pair must never disagree, or one could
        // resurrect a code-unlocked block the other stood down.
        Assert.Equal(monkmode.Service1.PartnerUnlocked(unlockedAt!), mm_guard.Guardian.PartnerUnlocked(unlockedAt!));
    }
}

public class ClassifyPartnerCodeSignalTests
{
    private static monkmode.Service1.PartnerCodeAction Classify(
        bool codePresent, bool candidateNonEmpty, bool alreadyUnlocked, bool macValid) =>
        monkmode.Service1.ClassifyPartnerCodeSignal(codePresent, candidateNonEmpty, alreadyUnlocked, macValid);

    [Fact]
    public void PresentNonBlankCandidate_OnAHealthyNotYetUnlockedBlock_Verifies()
    {
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Verify,
            Classify(codePresent: true, candidateNonEmpty: true, alreadyUnlocked: false, macValid: true));
    }

    [Fact]
    public void BlankCandidate_IsIgnored()
    {
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Ignore,
            Classify(codePresent: true, candidateNonEmpty: false, alreadyUnlocked: false, macValid: true));
    }

    [Fact]
    public void NoTrigger_IsIgnored()
    {
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Ignore,
            Classify(codePresent: false, candidateNonEmpty: false, alreadyUnlocked: false, macValid: true));
    }

    [Fact]
    public void AlreadyUnlocked_IsIgnored_ConsumeAfterPersistIsCrashSafe()
    {
        // UnlockedAt already set => the block is ending; nothing to re-verify. This is
        // also what makes consume-after-persist crash-safe (a crash between the
        // UnlockedAt write and the trigger delete re-classifies here as Ignore).
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Ignore,
            Classify(codePresent: true, candidateNonEmpty: true, alreadyUnlocked: true, macValid: true));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void InvalidMac_AlwaysIgnores_R6_NeverVerifiesAgainstAnUntrustedHash(
        bool codePresent, bool candidateNonEmpty, bool alreadyUnlocked)
    {
        // R6: a frozen/tampered config never verifies against a hash it can't trust -
        // it ignores the channel entirely (mirrors ClassifyCoolOffSignal + the `add`
        // fail-open fix).
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Ignore,
            Classify(codePresent, candidateNonEmpty, alreadyUnlocked, macValid: false));
    }

    [Fact]
    public void FullMatrix_MatchesThePinnedContract()
    {
        // All 16 combinations against the design's processing rules (macValid-required,
        // already-unlocked => Ignore, present + non-blank => Verify, else Ignore).
        // Deliberately NO `committed` axis - a committed block (C4) keeps the code as
        // its ONE intended exit (contrast ClassifyCoolOffSignal, which gates on committed).
        foreach (var present in new[] { true, false })
            foreach (var nonEmpty in new[] { true, false })
                foreach (var unlocked in new[] { true, false })
                    foreach (var mac in new[] { true, false })
                    {
                        var expected =
                            !mac ? monkmode.Service1.PartnerCodeAction.Ignore :
                            unlocked ? monkmode.Service1.PartnerCodeAction.Ignore :
                            (present && nonEmpty) ? monkmode.Service1.PartnerCodeAction.Verify :
                            monkmode.Service1.PartnerCodeAction.Ignore;
                        Assert.Equal(expected, Classify(present, nonEmpty, unlocked, mac));
                    }
    }
}

// End-to-end through the REAL exit gates + BuildCanonical/MAC (raw injected key, no
// DPAPI): a service-verified code sets a MAC-covered UnlockedAt that lifts the block
// on the tick heartbeat, OnStart and the guardian; every wrong/tampered/forged path
// fails closed.
public class PartnerCodeEndToEndTests
{
    private static readonly CultureInfo EnCa = new("en-CA");
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly string Ver = MonkMode.ConfigIntegrity.CurrentSchemaVersion;
    private static readonly DateTime Hw = new(2026, 6, 25, 12, 0, 0);
    private static readonly string HwText = Hw.ToString(EnCa);
    private static readonly string FutureUntil = Hw.AddHours(8).ToString(EnCa);

    private const string Code = "ABCDE-12345";
    private static readonly byte[] Salt = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();
    private static readonly string SaltB64 = Convert.ToBase64String(Salt);
    private static readonly string HashB64 = MonkMode.ConfigIntegrity.ComputePartnerHash(Salt, Code);

    // The armed config's canonical (UnlockedAt="", not committed) and its MAC.
    private static string ArmedCanonical() =>
        MonkMode.ConfigIntegrity.BuildCanonical(Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, "", "no", "", "");

    [Fact]
    public void CorrectCode_SetsUnlockedAt_AndTheBlockLiftsAcrossServiceAndGuardian()
    {
        // 1. Armed: a valid MAC over the [Partner] verifier, UnlockedAt="" => no exit.
        var armedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(ArmedCanonical(), Key);
        Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(ArmedCanonical(), armedMac, Key));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", "", HwText, 5, macValid: true));

        // 2. The service verifies the correct candidate against the stored verifier.
        Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches(Code, SaltB64, HashB64));

        // 3. It sets the MAC-covered UnlockedAt and re-stamps (a NEW canonical + MAC).
        var unlockedAt = Hw.AddMinutes(1).ToString(EnCa);
        var unlockedCanonical = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, unlockedAt, "no", "", "");
        var unlockedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(unlockedCanonical, Key);
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(unlockedCanonical, unlockedMac, Key);
        Assert.True(macValid);

        // 4. The block now lifts on the heartbeat, the shared exit decision AND the
        //    guardian - even though Until is far in the future and no cooling-off is
        //    pending. All three converge on stopMe().
        Assert.Equal(monkmode.Service1.HeartbeatAction.Lift,
            monkmode.Service1.ClassifyHeartbeat(macValid,
                monkmode.Service1.BlockHasExpired(FutureUntil, Hw, 5),
                monkmode.Service1.CoolOffElapsedTime("", HwText),
                monkmode.Service1.PartnerUnlocked(unlockedAt),
                monkmode.Service1.ScheduleActive("", HwText)));
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, "", unlockedAt, "", HwText, 5, macValid));
        Assert.True(mm_guard.Guardian.EffectiveExit(FutureUntil, "", unlockedAt, "", HwText, 5, macValid));
    }

    [Fact]
    public void GuardianCannotResurrectACodeUnlockedBlock()
    {
        // The load-bearing edge: once a code has unlocked (UnlockedAt set, valid MAC),
        // the guardian must STAND DOWN, not SCM-restart the service in the stopMe() gap.
        var unlockedAt = Hw.AddMinutes(1).ToString(EnCa);
        var unlockedCanonical = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, unlockedAt, "no", "", "");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            unlockedCanonical, MonkMode.ConfigIntegrity.ComputeConfigMac(unlockedCanonical, Key), Key);

        var blockActive = !mm_guard.Guardian.EffectiveExit(FutureUntil, "", unlockedAt, "", HwText, 5, macValid);
        Assert.False(blockActive);
        Assert.False(mm_guard.Guardian.ShouldRestartService(blockActive, serviceRunning: false));
    }

    [Fact]
    public void WrongCode_LeavesUnlockedAtEmpty_TheBlockHolds()
    {
        // A miss never sets UnlockedAt, so the config stays armed (UnlockedAt="") and
        // the block holds on every gate - fully enforced.
        Assert.False(MonkMode.ConfigIntegrity.PartnerCodeMatches("ZZZZZ-99999", SaltB64, HashB64));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", "", HwText, 5, macValid: true));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, "", "", "", HwText, 5, macValid: true));
    }

    [Fact]
    public void SwappedInAttackerHash_FreezesTheBlock_R6()
    {
        // R6: an attacker swaps [Partner] Hash for KDF(salt, theirOwnCode). The stored
        // MAC still covers the ORIGINAL hash, so the swapped canonical no longer
        // validates => macValid False => NO code is valid (not even the real one) and
        // the block FREEZES - even though the attacker "knows" a matching code.
        var armedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(ArmedCanonical(), Key);
        var attackerCode = "MYOWN-CODE1";
        var attackerHash = MonkMode.ConfigIntegrity.ComputePartnerHash(Salt, attackerCode);
        var swapped = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, attackerHash, "", "no", "", "");

        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(swapped, armedMac, Key);
        Assert.False(macValid);   // the swap broke the MAC
        // So even though the attacker's code matches the swapped hash, the config is
        // frozen: ClassifyPartnerCodeSignal ignores a macValid=False config, and the
        // exit gate never lifts.
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Ignore,
            monkmode.Service1.ClassifyPartnerCodeSignal(codePresent: true, candidateNonEmpty: true, alreadyUnlocked: false, macValid: macValid));
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", "", "", HwText, 5, macValid));
    }

    [Fact]
    public void ForgedUnlockedAt_ByRawEdit_FreezesTheBlock_R6()
    {
        // R6: forging UnlockedAt=<now> by a raw ini edit changes the canonical, so the
        // stored MAC no longer validates => freeze. A non-empty UnlockedAt is only
        // trusted UNDER a valid MAC (i.e. only if the service wrote it after a code).
        var armedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(ArmedCanonical(), Key);
        var forged = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, Hw.ToString(EnCa), "no", "", "");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(forged, armedMac, Key);
        Assert.False(macValid);
        Assert.False(monkmode.Service1.EffectiveExit(FutureUntil, "", Hw.ToString(EnCa), "", HwText, 5, macValid));
        Assert.False(mm_guard.Guardian.EffectiveExit(FutureUntil, "", Hw.ToString(EnCa), "", HwText, 5, macValid));
    }

    [Fact]
    public void DeletedPartnerFields_FreezeTheBlock_CantDeleteYourWayOut()
    {
        // Deleting the [Partner] fields (reading back "") changes the canonical => the
        // original MAC no longer validates => freeze. Can't delete your way to "no code
        // required".
        var armedMac = MonkMode.ConfigIntegrity.ComputeConfigMac(ArmedCanonical(), Key);
        var stripped = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", "", "", "", "no", "", "");
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(stripped, armedMac, Key));
    }

    [Fact]
    public void CommittedBlock_KeepsTheCodeExit_TheC4Seam()
    {
        // The §6.5 asymmetry that makes "commit = code-only exit" true: a committed
        // block (C4) has ClassifyCoolOffSignal Ignore the cooling-off request, but the
        // partner code still lifts. ClassifyPartnerCodeSignal deliberately has NO
        // committed axis, so a committed-and-healthy block still Verifies a code, and a
        // code-unlock still EffectiveExits.
        Assert.Equal(monkmode.Service1.CoolOffAction.Ignore,
            monkmode.Service1.ClassifyCoolOffSignal(requestPresent: true, cancelPresent: false,
                coolOffPending: false, committed: true, macValid: true));
        // The code path is unaffected by commitment (there is no committed parameter):
        Assert.Equal(monkmode.Service1.PartnerCodeAction.Verify,
            monkmode.Service1.ClassifyPartnerCodeSignal(codePresent: true, candidateNonEmpty: true, alreadyUnlocked: false, macValid: true));
        // A genuinely COMMITTED block (Committed="yes") whose code was verified: the
        // code-unlock still lifts it (the exit a commit deliberately keeps).
        var unlockedAt = Hw.AddMinutes(1).ToString(EnCa);
        var unlockedCanonical = MonkMode.ConfigIntegrity.BuildCanonical(
            Ver, FutureUntil, "chrome.exe;", "reddit.com;", "N", HwText, "", SaltB64, HashB64, unlockedAt, "yes", "", "");
        var macValid = MonkMode.ConfigIntegrity.ConfigMacIsValid(
            unlockedCanonical, MonkMode.ConfigIntegrity.ComputeConfigMac(unlockedCanonical, Key), Key);
        Assert.True(monkmode.Service1.EffectiveExit(FutureUntil, "", unlockedAt, "", HwText, 5, macValid));
    }
}

// C1b composition: the shadow backup must CARRY the [Partner] verifier, so a
// corrupt-then-restore leaves the partner's code still working (never a code-free
// lift, never a lost verifier). Byte-exact copy layer (CopyIfSourceValid +
// AtomicHosts), MAC gate boolean-injected, exactly like CoolOffBackupCarryTests.
public class PartnerCodeBackupCarryTests
{
    [Fact]
    public void CorruptPrimary_RestoredFromBackup_CarriesTheVerifier_CodeStillWorks()
    {
        var ca = new CultureInfo("en-CA");
        var dir = Directory.CreateTempSubdirectory("mm_partner_bak_");
        try
        {
            var primary = Path.Combine(dir.FullName, "monkmode_settings.ini");
            var backup = Path.Combine(dir.FullName, MonkMode.ConfigBackup.BackupFileName);
            var enc = new MonkMode.Simple3Des("mm_textbox");

            var salt = MonkMode.ConfigIntegrity.NewPartnerSalt();
            var code = MonkMode.ConfigIntegrity.GeneratePartnerCode();
            var hash = MonkMode.ConfigIntegrity.ComputePartnerHash(salt, code);

            var ini = new MonkMode.IniFile();
            ini.AddSection("User");
            ini.SetKeyValue("User", "CustomSites", "reddit.com;");
            ini.AddSection("Time");
            ini.SetKeyValue("Time", "Until", enc.EncryptData(new DateTime(2026, 12, 31, 18, 0, 0).ToString(ca)));
            ini.SetKeyValue("Time", "TimeChanging", "no");
            ini.SetKeyValue("Time", "HighWater", enc.EncryptData(new DateTime(2026, 6, 25, 12, 0, 0).ToString(ca)));
            ini.AddSection("CurrentTime");
            ini.SetKeyValue("CurrentTime", "Now", enc.EncryptData(new DateTime(2026, 6, 25, 12, 0, 0).ToString(ca)));
            ini.AddSection("Process");
            ini.SetKeyValue("Process", "List", "null");
            ini.AddSection("Partner");
            ini.SetKeyValue("Partner", "Salt", Convert.ToBase64String(salt));
            ini.SetKeyValue("Partner", "Hash", hash);
            ini.SetKeyValue("Partner", "UnlockedAt", "");
            ini.Save(primary);

            // The refresh a legitimate write performs (MAC validity injected true, as
            // the live gate would report for a just-stamped save).
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(primary, backup, true));

            var before = new MonkMode.IniFile();
            before.Load(primary);
            var canonicalBefore = MonkMode.Blocker.CanonicalFromIni(before);

            // Corrupt the primary, then restore from the backup.
            File.WriteAllText(primary, "garbage - not an ini");
            Assert.True(MonkMode.ConfigBackup.CopyIfSourceValid(backup, primary, true));

            // The restored primary carries the SAME verifier and derives the SAME
            // canonical, and the partner's code STILL verifies against it.
            var restored = new MonkMode.IniFile();
            restored.Load(primary);
            Assert.Equal(canonicalBefore, MonkMode.Blocker.CanonicalFromIni(restored));
            Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches(
                code, restored.GetKeyValue("Partner", "Salt"), restored.GetKeyValue("Partner", "Hash")));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

// The plaintext-never-persists invariant, exercised through the real CLI WriteConfig
// (which DOES stamp a DPAPI [Integrity] Key/Mac - the same path CanonicalParityTests'
// CliWriteConfig uses, inside the same fence: only the test bin ini/backup are written).
// Writes the shared test-bin monkmode_settings.ini via Blocker.WriteConfig; the
// "CliIniWriters" collection serialises it with the other ini-writing test classes.
[Collection("CliIniWriters")]
public class PartnerCodePlaintextNeverPersistsTests
{
    [Fact]
    public void WriteConfig_StoresOnlyTheHash_NeverThePlaintextCode()
    {
        var iniPath = MonkMode.Blocker.IniPath();
        var backupPath = MonkMode.Blocker.IniBackupPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            var code = MonkMode.Blocker.WriteConfig(new[] { "reddit.com" }, Array.Empty<string>(), until);

            // A real grouped code came back, and it verifies against the STORED hash.
            Assert.False(string.IsNullOrWhiteSpace(code));
            var ini = new MonkMode.IniFile();
            ini.Load(iniPath);
            var salt = ini.GetKeyValue("Partner", "Salt");
            var hash = ini.GetKeyValue("Partner", "Hash");
            Assert.False(string.IsNullOrEmpty(salt));
            Assert.False(string.IsNullOrEmpty(hash));
            // UnlockedAt="" round-trips through IniFile as a bare key line, reloading
            // as Nothing/"" - the same proven pattern as CoolOffUntil="". VB treats
            // both as "" in the canonical + PartnerUnlocked, so accept null-or-empty.
            Assert.True(string.IsNullOrEmpty(ini.GetKeyValue("Partner", "UnlockedAt")));
            Assert.True(MonkMode.ConfigIntegrity.PartnerCodeMatches(code, salt, hash));

            // THE invariant: the plaintext code (and its normalised form) must NOT
            // appear anywhere in the persisted ini or the shadow backup - only the
            // one-way hash is stored.
            var normalised = MonkMode.ConfigIntegrity.NormalisePartnerCode(code);
            var iniText = File.ReadAllText(iniPath);
            Assert.DoesNotContain(code, iniText);
            Assert.DoesNotContain(normalised, iniText);
            if (File.Exists(backupPath))
            {
                var backupText = File.ReadAllText(backupPath);
                Assert.DoesNotContain(code, backupText);
                Assert.DoesNotContain(normalised, backupText);
            }
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
