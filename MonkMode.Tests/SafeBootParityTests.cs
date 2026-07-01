// MonkMode.Tests - B3 SafeBoot CLI<->service constant parity.
//
// The SafeBoot registry keys are declared TWICE, in two assemblies:
//   - the service WRITES + self-heals them: monkmode.Service1.SafeBoot{Minimal,Network}Key
//   - the `unblock --force` escape hatch REMOVES them: MonkMode.Blocker.SafeBoot{Minimal,Network}Key
// If the two copies ever drift, the service would keep registering a key the
// escape hatch can no longer find - a stuck SafeBoot registration that survives a
// force-unblock (and a no-data-loss violation if the stray key is left behind).
//
// Blocker.vb's comment asserts "a parity test pins them equal so this CLI copy
// can't drift from what the service writes" - but no such test existed until now
// (added 30/06/2026, found during the B5a design map). This is that test. It
// mirrors the B7 ConfigIntegrity cross-assembly parity approach (CanonicalParityTests):
// compare the REAL symbols from both assemblies so a one-sided edit fails the build.

namespace MonkMode.Tests;

public class SafeBootParityTests
{
    // The CLI remover's Minimal key must be byte-identical to what the service writes.
    [Fact]
    public void MinimalKey_CliMatchesService()
    {
        Assert.Equal(monkmode.Service1.SafeBootMinimalKey, MonkMode.Blocker.SafeBootMinimalKey);
    }

    // ...and likewise the Network key.
    [Fact]
    public void NetworkKey_CliMatchesService()
    {
        Assert.Equal(monkmode.Service1.SafeBootNetworkKey, MonkMode.Blocker.SafeBootNetworkKey);
    }

    // Guard against the degenerate "both empty" pass: the keys are real, non-blank
    // paths (the service-side SafeBootRegistrationConstantsTests pins the literals;
    // here we just refuse a vacuous equality if someone blanked both copies at once).
    [Fact]
    public void Keys_AreNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(MonkMode.Blocker.SafeBootMinimalKey));
        Assert.False(string.IsNullOrWhiteSpace(MonkMode.Blocker.SafeBootNetworkKey));
    }
}
