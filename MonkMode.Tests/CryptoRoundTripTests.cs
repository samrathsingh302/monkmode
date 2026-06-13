// MonkMode.Tests - Simple3Des round-trip correctness.
//
// The crypto design (TripleDES, SHA1-derived key from "mm_textbox", zero IV,
// no HMAC) is documented-weak and Phase-3-owned (B7) - these tests do NOT
// bless it, they only pin round-trip correctness and the byte-for-byte
// equivalence of the four per-project copies (CLI, service, notifier,
// guardian), so the Phase-3 refactor has a safety net.
//
// HISTORICAL FENCE (now resolved): the SERVICE copy's DecryptData used to call
// `End` on a FormatException (audit P3) - an availability bypass (write junk
// into [Time] Until -> the LocalSystem service force-terminates) that would
// also have killed the test host. B7 fixed it to `Return ""`, matching the
// other three copies, so the invalid-Base64 test below now covers ALL FOUR
// copies (and pins that the service no longer `End`s).

namespace MonkMode.Tests;

public class CryptoRoundTripTests
{
    private const string Passphrase = "mm_textbox";

    public static readonly TheoryData<string> Plaintexts = new()
    {
        "2026-06-25 5:04:33 p.m.",              // an en-CA datetime, the real payload
        "chrome.exe;brave.exe;steam.exe;",       // a [Process] List payload
        "reddit.com;x.com;",                     // a CustomSites payload
        "café £9.99 — ☬",   // non-ASCII survives the Unicode encoding
        "",                                       // empty value
        new string('x', 5000),                    // multi-block input
    };

    [Theory]
    [MemberData(nameof(Plaintexts))]
    public void CliCopy_RoundTrips(string plaintext)
    {
        var enc = new MonkMode.Simple3Des(Passphrase);
        Assert.Equal(plaintext, enc.DecryptData(enc.EncryptData(plaintext)));
    }

    [Theory]
    [MemberData(nameof(Plaintexts))]
    public void ServiceCopy_RoundTrips(string plaintext)
    {
        var enc = new monkmode.Simple3Des(Passphrase);
        Assert.Equal(plaintext, enc.DecryptData(enc.EncryptData(plaintext)));
    }

    [Theory]
    [MemberData(nameof(Plaintexts))]
    public void NotifierCopy_RoundTrips(string plaintext)
    {
        var enc = new mm_notify.Simple3Des(Passphrase);
        Assert.Equal(plaintext, enc.DecryptData(enc.EncryptData(plaintext)));
    }

    [Theory]
    [MemberData(nameof(Plaintexts))]
    public void GuardianCopy_RoundTrips(string plaintext)
    {
        var enc = new mm_guard.Simple3Des(Passphrase);
        Assert.Equal(plaintext, enc.DecryptData(enc.EncryptData(plaintext)));
    }

    [Theory]
    [MemberData(nameof(Plaintexts))]
    public void AllFourCopies_ProduceIdenticalCiphertext(string plaintext)
    {
        // The contract: the same Simple3Des is compiled into each project, so
        // ciphertext must be interchangeable between them.
        var cli = new MonkMode.Simple3Des(Passphrase).EncryptData(plaintext);
        var srv = new monkmode.Simple3Des(Passphrase).EncryptData(plaintext);
        var notify = new mm_notify.Simple3Des(Passphrase).EncryptData(plaintext);
        var guard = new mm_guard.Simple3Des(Passphrase).EncryptData(plaintext);
        Assert.Equal(cli, srv);
        Assert.Equal(cli, notify);
        Assert.Equal(cli, guard);
    }

    [Fact]
    public void CliEncrypts_GuardianDecrypts()
    {
        // The watchdog data flow: the CLI writes [Time] Until, the guardian
        // reads it each tick to decide whether to keep guarding.
        var plaintext = "2026-06-25 10:00:00 p.m.";
        var stored = new MonkMode.Simple3Des(Passphrase).EncryptData(plaintext);
        Assert.Equal(plaintext, new mm_guard.Simple3Des(Passphrase).DecryptData(stored));
    }

    [Fact]
    public void CliEncrypts_ServiceDecrypts()
    {
        // The live data flow: the CLI writes [Time] Until, the service reads it.
        var plaintext = "2026-06-25 10:00:00 p.m.";
        var stored = new MonkMode.Simple3Des(Passphrase).EncryptData(plaintext);
        Assert.Equal(plaintext, new monkmode.Simple3Des(Passphrase).DecryptData(stored));
    }

    [Fact]
    public void NotifierEncrypts_ServiceDecrypts()
    {
        // The clock-change flow: the notifier rewrites Until, the service reads it.
        var plaintext = "2026-06-26 8:00:00 a.m.";
        var stored = new mm_notify.Simple3Des(Passphrase).EncryptData(plaintext);
        Assert.Equal(plaintext, new monkmode.Simple3Des(Passphrase).DecryptData(stored));
    }

    [Fact]
    public void AllFourCopies_ReturnEmptyOnInvalidBase64()
    {
        // B7 fixed the service copy's `End` to `Return ""`, so all four copies
        // now fail closed on junk: an undecryptable Until becomes an unparseable
        // one, which fails CLOSED everywhere (service keeps enforcing, guardian
        // keeps guarding). The service copy is now safe to exercise directly -
        // if it still `End`ed, this very test would terminate the host.
        Assert.Equal("", new MonkMode.Simple3Des(Passphrase).DecryptData("not base64 at all!"));
        Assert.Equal("", new monkmode.Simple3Des(Passphrase).DecryptData("not base64 at all!"));
        Assert.Equal("", new mm_notify.Simple3Des(Passphrase).DecryptData("not base64 at all!"));
        Assert.Equal("", new mm_guard.Simple3Des(Passphrase).DecryptData("not base64 at all!"));
    }
}
