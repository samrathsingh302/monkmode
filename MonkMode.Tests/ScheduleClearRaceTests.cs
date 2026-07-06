// MonkMode.Tests - C5b (c3 residual, issue #2): the CLI<->service `schedule --clear`
// lost-update guard.
//
// The race (fail-closed, but confusing - GitHub issue #2): the tick SNAPSHOTS [Schedule]
// Spec at load, then derives the new ActiveUntil from that snapshot seconds later. The CLI
// (the SOLE Spec writer) can clear or re-arm the Spec inside that window. The persist's
// existing TOCTOU MAC re-validate reloads the ini - so the CLI's new Spec survives - but
// pre-fix it still wrote an ActiveUntil DERIVED FROM THE STALE SNAPSHOT: a window opened
// from a schedule the user had just cleared (the service "reinstating" a clear the CLI
// already reported). Never an under-block, but a lie on a security-posture surface.
//
// The fix (the issue's "write-versioned snapshot", with the Spec TEXT itself as the
// version - no new config field, no canonical v8 bump, no MAC-coverage change):
// PersistScheduleActiveUntil now takes the tick's snapshot Spec and, after the MAC
// re-validate, refuses to write unless the RELOADED Spec is byte-identical to it. The
// pure decision is ScheduleSpecUnchangedSinceSnapshot, pinned here; the file-I/O wrapper
// stays smoke-only (the ClassifyCoolOffSignal/ProcessCoolOffSignals discipline - the
// live-race interleaving itself is the deferred human-gated smoke's to prove).
//
// Both abort outcomes are FAIL-CLOSED:
//   - an aborted OPEN/EXTEND leaves ActiveUntil as loaded (usually "") - no hold is minted
//     from a dead rule; a re-arm's own windows open on the next tick (<=10s) off the fresh
//     Spec;
//   - an aborted CLOSE keeps the stored hold for <=1 tick (over-block, never under; the
//     next tick clears it).

using System.Globalization;

namespace MonkMode.Tests;

public class ScheduleSpecUnchangedSinceSnapshotTests
{
    // The canonical armed Spec (the exact compact form the CLI serialises).
    private const string ArmedSpec = "v1;12345:0900-1700;sites=reddit.com|x.com;apps=chrome.exe";

    [Fact]
    public void UnchangedSpec_AllowsThePersist()
    {
        // Steady state: nothing wrote between load and persist -> the write proceeds.
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(ArmedSpec, ArmedSpec));
    }

    [Fact]
    public void BothEmpty_AllowsThePersist()
    {
        // A schedule-less config persisting a window CLOSE (spec already cleared earlier,
        // ActiveUntil running to its end): still no writer landed -> allowed.
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot("", ""));
    }

    [Fact]
    public void TheIssueRace_ClearLandedMidTick_RefusesThePersist()
    {
        // THE issue #2 scenario: tick snapshots the armed Spec, `schedule --clear` saves
        // Spec="" before the persist reloads -> the derived ActiveUntil is stale -> refuse.
        // The clear stands; no window opens from the cleared schedule.
        Assert.False(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(ArmedSpec, ""));
    }

    [Fact]
    public void ReArmLandedMidTick_RefusesThePersist()
    {
        // A re-arm with DIFFERENT windows/sites landed mid-tick: the ActiveUntil derived
        // from the old rule must not be written; the next tick opens the NEW rule's windows.
        const string reArmed = "v1;67:1000-1400;sites=youtube.com;apps=";
        Assert.False(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(ArmedSpec, reArmed));
    }

    [Fact]
    public void FreshArmLandedMidTick_RefusesThePersist()
    {
        // The reverse direction: the tick loaded Spec="" (e.g. cleared earlier, a stored
        // window running to its close) and computed a CLOSE; a fresh arm lands before the
        // persist. Refuse - keeping the hold <=1 tick is fail-closed, and the next tick
        // re-evaluates close/open off the freshly-armed Spec.
        Assert.False(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot("", ArmedSpec));
    }

    [Fact]
    public void NothingNormalisesToEmpty_BothWays()
    {
        // IniFile.GetKeyValue returns String.Empty for an absent key, but be defensive:
        // Nothing and "" are the same "no Spec" state on both sides of the comparison.
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(null, ""));
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot("", null));
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(null, null));
    }

    [Theory]
    [InlineData("V1;12345:0900-1700;sites=reddit.com|x.com;apps=chrome.exe")]   // case flip
    [InlineData("v1;12345:0900-1700;sites=reddit.com|x.com;apps=chrome.exe ")]  // trailing space
    [InlineData("v1;12345:0900-1701;sites=reddit.com|x.com;apps=chrome.exe")]   // one-minute edit
    public void AnyByteDifference_RefusesThePersist_OrdinalNoTrimming(string reloaded)
    {
        // The CLI is the sole Spec writer and always emits canonical text, so ANY byte
        // difference means a write landed. Deliberately ordinal + no trimming: a spurious
        // refusal costs one 10s tick (fail-closed); a lenient match could bless staleness.
        Assert.False(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(ArmedSpec, reloaded));
    }

    [Fact]
    public void ABA_ClearThenReArmIdenticalSpecWithinOneTick_AllowsThePersist()
    {
        // clear + re-arm of the BYTE-IDENTICAL Spec inside one tick passes the gate. Safe:
        // the derived ActiveUntil depends only on the Spec CONTENT, which is once again the
        // armed content - the window the persist opens is exactly what the re-armed rule
        // demands. (A version COUNTER would refuse here for no behavioural gain; comparing
        // the text is why no new MAC-covered field was needed.)
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(ArmedSpec, ArmedSpec));
    }
}

// The gate composed with the REAL tick pipeline it protects: a genuine CLI-built Spec is
// parsed/evaluated/converted by the real gates into a non-empty ActiveUntil target (the
// value the persist would write), and the guard is judged against the exact snapshot text
// that pipeline consumed - proving the refusal fires for precisely the value the tick
// threads through ProcessScheduleWindows -> PersistScheduleActiveUntil.
public class ScheduleClearRaceCompositionTests
{
    private static readonly CultureInfo EnCa = new("en-CA");

    [Fact]
    public void RealCliSpec_WindowOpens_ButAClearLandingMidTick_RefusesTheStaleActiveUntil()
    {
        // 1) The CLI serialises a real schedule (the c3 writer path's own validator).
        string spec = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildScheduleSpec(
            "Mon-Sun 09:00-17:00", new[] { "reddit.com" }, System.Array.Empty<string>(), ref spec, ref err), err);

        // 2) The tick snapshots that Spec and the real pipeline opens the window: now is
        //    inside 09:00-17:00 on every day, so EvaluateWindows opens it and
        //    NextScheduleActiveUntil converts it to a non-empty HighWater-anchored target.
        var now = new System.DateTime(2026, 7, 6, 10, 0, 0);   // 10:00, inside the window
        var hw = now.ToString(EnCa);
        var opens = monkmode.Service1.EvaluateWindows(
            monkmode.Service1.ParseSchedule(spec).Windows,
            now.AddSeconds(-10).ToString(EnCa), now.ToString(EnCa), 10, isBoot: false);
        Assert.Single(opens);
        var target = monkmode.Service1.NextScheduleActiveUntil("", opens, hw);
        Assert.NotEqual("", target);   // the persist HAS something stale-derivable to write

        // 3a) No CLI write landed: the reloaded Spec is the snapshot -> persist allowed.
        Assert.True(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(spec, spec));

        // 3b) `schedule --clear` landed between the snapshot and the persist's reload
        //     (Blocker.WriteScheduleConfig("") keeps every other field but blanks the
        //     Spec): the guard refuses the stale target - the clear the CLI reported
        //     stands, and no window is reinstated from the dead rule.
        Assert.False(monkmode.Service1.ScheduleSpecUnchangedSinceSnapshot(spec, ""));
    }
}
