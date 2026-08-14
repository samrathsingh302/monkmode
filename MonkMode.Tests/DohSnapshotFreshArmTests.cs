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

// MonkMode.Tests - M0 (F6): the DoH snapshot is only ever taken on a FRESH arm.
//
// THE BUG THIS PINS. B5a snapshots the user's browser DNS-over-HTTPS policy at block
// start and restores it at teardown, so a block leaves no trace. The manual arm path took
// that snapshot UNCONDITIONALLY. That was survivable only while v1.0 refused a second
// block outright; v1.1 S2 deleted that refusal, so `monkmode block` now arms a slot beside
// a live one. Arm twice and the SECOND snapshot reads the policy the service has already
// forced OFF and records it as "the user's prior" - teardown then "restores" DoH=off and
// CONSUMES the snapshot, so the real prior is gone permanently.
//
// Not a theory: the 13/08/2026 estate bug-hunt found every browser on this machine
// DoH-force-disabled by machine policy with no snapshot left anywhere, and dist\monkmode_stats
// records two arms 2 s apart (22:26:03 / 22:26:05 on 12/08) - the exact double-arm.
//
// Pinned here:
//   - Blocker.ShouldSnapshotDohPolicy: the full truth table, both conditions independently
//     sufficient to REFUSE. Refusing is always the safe side, because RemoveDohPolicy's
//     no-snapshot path DOES NOTHING rather than delete a value it cannot prove is ours.
//   - the fail-safe directions of the two live readings that feed it (AnySlotArmed answers
//     ARMED on an unreadable config; DohSnapshotExists answers EXISTS on a failed read) -
//     both land on "don't snapshot".
//   - Blocker.AnythingArmed end-to-end through the real config writers: a manual slot OR a
//     schedule closes the gate, and a torn-down config re-opens it. This is the reading the
//     CLI must take BEFORE ArmSlot appends its own slot - afterwards it is always True.
//   - the same widening on the SCHEDULE path, which had only ever asked about schedules.
//
// HARD FENCE: the live registry is never read or written here (WriteDohSnapshot and
// RemoveDohPolicy are never called - they touch HKLM\SOFTWARE\Policies). The only files
// touched are the shared test-bin ini/backup/snapshots, serialised via the CliIniWriters
// collection like every other CLI-writer test, and a GUID-unique temp file. No service, no
// process, no real hosts file.

using System.IO;

namespace MonkMode.Tests;

public class DohSnapshotGateTests
{
    // The whole truth table, written out row by row so the expected column is an
    // independent statement of the policy rather than a second copy of the code.
    // Snapshot ONLY on the one genuinely fresh world: nothing armed AND no snapshot
    // already on disk.
    [Theory]
    [InlineData(false, false, true)]   // fresh arm, no prior record  -> the ONLY snapshot case
    [InlineData(true, false, false)]   // something already armed     -> would capture OUR forced-off state
    [InlineData(false, true, false)]   // a record already exists     -> older-but-genuine beats newer-but-ours
    [InlineData(true, true, false)]    // both                        -> obviously not
    public void ShouldSnapshot_Matrix(bool anythingArmed, bool snapshotExists, bool expected)
    {
        Assert.Equal(expected, MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed, snapshotExists));
    }

    // THE regression pin: the second arm of a double-arm never re-snapshots. This is the
    // exact 12/08 sequence - arm one (fresh: snapshot taken, so a record now exists), then
    // arm two 2 s later while the first is live.
    [Fact]
    public void SecondArmBesideALiveBlock_NeverReSnapshots()
    {
        // Arm 1: nothing armed, no snapshot -> take one.
        Assert.True(MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed: false, snapshotExists: false));

        // Arm 2, seconds later: slot 1 is armed and its snapshot is on disk. Both
        // conditions independently refuse, so the guard holds even if either reading
        // were wrong on its own.
        Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed: true, snapshotExists: true));
        Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed: true, snapshotExists: false));
        Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed: false, snapshotExists: true));
    }

    // The already-poisoned machine must not re-poison itself. With DoH forced off and a
    // stale snapshot left behind by a teardown that failed to consume it, a later FRESH
    // arm (nothing armed) still refuses - snapshotExists alone carries it. Without this
    // second condition the guard would depend entirely on an armed-reading that a frozen
    // or tampered config can get wrong.
    [Fact]
    public void StaleSnapshotSurvivingATeardown_IsNotOverwrittenByTheNextFreshArm()
    {
        Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed: false, snapshotExists: true));
    }

    // Fail-safe composition, axis 1: AnySlotArmed answers ARMED (True) on any read
    // failure - a frozen, tampered or unreadable config. Fed through the gate that lands
    // on "don't snapshot", which is the preserving direction. A gate that had inverted
    // this would treat exactly the corrupt-config case as fresh.
    [Fact]
    public void UnreadableConfigReadsAsArmed_WhichRefusesTheSnapshot()
    {
        const bool anySlotArmedOnReadFailure = true;     // Blocker.AnySlotArmed's Catch
        Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(anySlotArmedOnReadFailure, snapshotExists: false));
    }

    // Fail-safe composition, axis 2: DohSnapshotExists answers EXISTS (True) on a failed
    // read, which likewise refuses. Never overwrite a record you could not prove is absent.
    [Fact]
    public void UnreadableSnapshotPathReadsAsExisting_WhichRefusesTheSnapshot()
    {
        const bool snapshotExistsOnReadFailure = true;   // Blocker.DohSnapshotExists's Catch
        Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(anythingArmed: false, snapshotExistsOnReadFailure));
    }
}

// The two live readings that feed the gate, driven through the REAL CLI config writers.
// Shares the test-bin ini with the other CLI writers, so it takes their collection.
[Collection("CliIniWriters")]
public class DohSnapshotLiveReadingTests
{
    private static readonly DateTime Ends = DateTime.Now.AddHours(2);

    private static void Wipe()
    {
        foreach (var p in new[] { MonkMode.Blocker.IniPath(), MonkMode.Blocker.IniBackupPath(),
                                  MonkMode.Blocker.SnapshotPath(), MonkMode.Blocker.DohSnapshotPath() })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    // DohSnapshotExists is a one-line File.Exists wrapper, but it is the backstop the
    // whole guard leans on when the armed-reading is unreliable, so pin both answers.
    // Restores whatever was there before, so it cannot perturb a sibling test.
    [Fact]
    public void DohSnapshotExists_TracksTheFileOnDisk()
    {
        var path = MonkMode.Blocker.DohSnapshotPath();
        var had = File.Exists(path);
        var saved = had ? File.ReadAllText(path) : null;
        try
        {
            if (had) File.Delete(path);
            Assert.False(MonkMode.Blocker.DohSnapshotExists());

            File.WriteAllText(path, "not-a-real-snapshot");
            Assert.True(MonkMode.Blocker.DohSnapshotExists());

            // ...and that is exactly what closes the gate on an otherwise-fresh arm.
            Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), MonkMode.Blocker.DohSnapshotExists()));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
            if (saved != null) { try { File.WriteAllText(path, saved); } catch { /* best-effort */ } }
        }
    }

    // A torn-down machine (no config at all) is the fresh world: nothing armed.
    [Fact]
    public void AnythingArmed_NoConfigAtAll_IsFalse()
    {
        Wipe();
        try
        {
            Assert.False(MonkMode.Blocker.AnythingArmed());
            Assert.True(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), MonkMode.Blocker.DohSnapshotExists()));
        }
        finally { Wipe(); }
    }

    // THE end-to-end pin. One real slot armed through ArmSlot (the same writer the CLI
    // uses) flips AnythingArmed, which closes the gate - so the second arm of a double-arm
    // cannot snapshot. Also proves WHY the CLI must sample before ArmSlot: after the arm
    // the reading is True for the arming block's own slot too.
    [Fact]
    public void AnythingArmed_OneArmedSlot_ClosesTheGate()
    {
        Wipe();
        try
        {
            Assert.False(MonkMode.Blocker.AnythingArmed());       // pre-arm sample: fresh

            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, System.Array.Empty<string>(),
                                             "", null, Ends, false, false, 0, false);
            Assert.True(r.Ok);

            Assert.True(MonkMode.Blocker.AnythingArmed());        // a live slot
            Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), snapshotExists: false));
        }
        finally { Wipe(); }
    }

    // The same widening on the SCHEDULE side. The schedule path had always guarded its
    // snapshot, but only on `Not ScheduleIsArmed()` - written when a schedule was the only
    // thing that could already be running. AnythingArmed folds slots in, so `schedule`
    // beside a live manual block no longer re-snapshots either.
    [Fact]
    public void AnythingArmed_ArmedSchedule_ClosesTheGate()
    {
        Wipe();
        try
        {
            string spec = "", err = "";
            Assert.True(MonkMode.Blocker.TryBuildScheduleSpec("Mon-Fri 09:00-17:00",
                new List<string> { "reddit.com" }, new List<string>(), ref spec, ref err), err);
            MonkMode.Blocker.WriteScheduleConfig(spec);

            Assert.True(MonkMode.Blocker.ScheduleIsArmed());
            Assert.True(MonkMode.Blocker.AnythingArmed());
            Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), snapshotExists: false));
        }
        finally { Wipe(); }
    }

    // Round trip: arm, then tear the config down, and the gate re-opens. The guard must
    // not be a one-way latch - a genuinely fresh arm after a real teardown still needs its
    // snapshot, or MonkMode would stop being able to restore DoH at all.
    [Fact]
    public void AnythingArmed_AfterTeardown_ReopensTheGate()
    {
        Wipe();
        try
        {
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, System.Array.Empty<string>(),
                                             "", null, Ends, false, false, 0, false);
            Assert.True(r.Ok);
            Assert.True(MonkMode.Blocker.AnythingArmed());

            Wipe();                                              // teardown: config + snapshots gone
            Assert.False(MonkMode.Blocker.AnythingArmed());
            Assert.True(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), MonkMode.Blocker.DohSnapshotExists()));
        }
        finally { Wipe(); }
    }

    // Guards the CLI's ordering requirement in the direction a future edit would break it:
    // if the sample were taken AFTER ArmSlot, the gate would be permanently shut and no arm
    // could ever snapshot again - MonkMode would silently stop restoring DoH.
    [Fact]
    public void SamplingAfterTheArmWouldPermanentlyShutTheGate()
    {
        Wipe();
        try
        {
            var r = MonkMode.Blocker.ArmSlot(new[] { "reddit.com" }, System.Array.Empty<string>(),
                                             "", null, Ends, false, false, 0, false);
            Assert.True(r.Ok);

            // This is the WRONG reading (post-arm) and it is False for the very first arm
            // on a clean machine - which is exactly the arm that must snapshot.
            Assert.False(MonkMode.Blocker.ShouldSnapshotDohPolicy(
                MonkMode.Blocker.AnythingArmed(), snapshotExists: false));
        }
        finally { Wipe(); }
    }
}
