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

// MonkMode.Tests - D2c all-session app-kill (the v8->v9 canonical bump + the widened
// service kill scope).
//
// The feature: normally the LocalSystem service kills blocked apps only in session 0 and
// the user-session notifier (MM_notify, SessionId<>0) kills the interactive session. With
// the MAC-covered [Process] AllSession="yes" flag (armed by `block --all-session-kill`),
// the service - which alone has cross-session kill privilege - kills matching apps in EVERY
// session, closing the "fast-user-switch to a second logged-in account" gap.
//
// What is pure and unit-tested here (no DPAPI/hosts/registry/SCM/live processes):
//   - Service1.AllSessionKillArmed: the "yes" flag parse (fail-safe: off on anything but
//     "yes", so a widen-only union can never REMOVE a session-0 kill);
//   - Service1.ProcessInKillScope: the widening decision (off = session-0-only, the exact
//     prior gate; on = every session), pinned as WIDEN-ONLY;
//   - the flag rides the MAC-covered canonical (round-trips valid; flipping it breaks the MAC);
//   - the CLI writer + all four CanonicalFromIni readers agree on a config that carries it.
//
// The live cross-session Process.Kill() is the DPAPI/live seam, smoke-tested at the elevated
// sitting, exactly like the B1/B2/B3/session-0 kill wiring - NOT exercised here.

using System.Globalization;

namespace MonkMode.Tests;

public class AllSessionKillHelperTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]      // case-insensitive, mirroring IsCommitted
    [InlineData("  yes  ", true)]  // trimmed
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("y", false)]       // only the exact token arms it
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("garbage", false)]
    public void AllSessionKillArmed_IsTrue_OnlyForYes(string? raw, bool expected)
    {
        // Fail-safe default: anything but "yes" reads as OFF (session-0-only). The flag is a
        // widen-only union, so "off on anything but yes" can never remove a session-0 kill; a
        // tampered value also freezes the block (MAC-covered), so it can only ever ADD kills.
        Assert.Equal(expected, monkmode.Service1.AllSessionKillArmed(raw!));
    }

    [Theory]
    // OFF: the byte-identical prior gate - session 0 only.
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 7, false)]
    // ON: every session, including session 0.
    [InlineData(true, 0, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    [InlineData(true, 7, true)]
    public void ProcessInKillScope_WidensOnly(bool allSessionKill, int sessionId, bool expected)
    {
        Assert.Equal(expected, monkmode.Service1.ProcessInKillScope(allSessionKill, sessionId));
    }

    [Fact]
    public void ProcessInKillScope_On_IsASupersetOfOff_ForEverySession()
    {
        // The load-bearing property: turning all-session ON can only ADD sessions to the kill
        // scope, never remove one. So for every session id, OFF-in-scope implies ON-in-scope.
        for (var sid = 0; sid < 16; sid++)
        {
            var off = monkmode.Service1.ProcessInKillScope(false, sid);
            var on = monkmode.Service1.ProcessInKillScope(true, sid);
            if (off) Assert.True(on);   // widen-only: never narrows
        }
        // And ON strictly covers a non-session-0 process that OFF would skip.
        Assert.False(monkmode.Service1.ProcessInKillScope(false, 3));
        Assert.True(monkmode.Service1.ProcessInKillScope(true, 3));
    }
}

public class AllSessionKillCanonicalTests
{
    private static readonly byte[] Key =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    };

    [Fact]
    public void AllSessionKillFlag_IsMacCovered_FlippingItBreaksTheMac()
    {
        // An armed all-session block (AllSessionKill="yes") stamps a MAC over that canonical;
        // a raw edit that flips it back to session-0-only (""/"no") no longer validates, so the
        // block FREEZES (fail-closed) rather than silently narrowing its kill scope. R6-shaped.
        var armed = OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "", "", "", "", "no", "", "", "", "yes");
        var mac = MonkMode.ConfigIntegrity.ComputeConfigMac(armed, Key);
        Assert.True(MonkMode.ConfigIntegrity.ConfigMacIsValid(armed, mac, Key));

        var clearedToDefault = OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "", "", "", "", "no", "", "", "", "");
        var clearedToNo = OneSlot.Canonical("U", "a.exe;", "x.com;", "N", "HW", "", "", "", "", "no", "", "", "", "no");
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(clearedToDefault, mac, Key));
        Assert.False(MonkMode.ConfigIntegrity.ConfigMacIsValid(clearedToNo, mac, Key));
    }
}

// Writes the shared test-bin monkmode_settings.ini via Blocker.WriteConfig; the
// "CliIniWriters" collection serialises it with the other ini-writing test classes so
// they never race the shared file.
[Collection("CliIniWriters")]
public class AllSessionKillWriteConfigTests
{
    private static void WithWrittenConfig(bool allSessionKill, Action<string> body)
    {
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            var until = new DateTime(2026, 12, 31, 23, 59, 59);
            MonkMode.Blocker.WriteConfig(
                new[] { "reddit.com" },
                new[] { "chrome.exe" },
                until,
                committed: false,
                coolOffSeconds: 0,
                allSessionKill: allSessionKill);
            body(iniPath);
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
            var backupPath = MonkMode.Blocker.IniBackupPath();
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Fact]
    public void WriteConfig_WithFlag_WritesAllSessionYes_AndTheServiceReadsItArmed()
    {
        WithWrittenConfig(allSessionKill: true, iniPath =>
        {
            var srvIni = new monkmode.IniFile();
            srvIni.Load(iniPath);
            Assert.Equal("yes", srvIni.GetKeyValue("Process", "AllSession"));
            Assert.True(monkmode.Service1.AllSessionKillArmed(srvIni.GetKeyValue("Process", "AllSession")));
        });
    }

    [Fact]
    public void WriteConfig_WithoutFlag_LeavesAllSessionAbsent_AndTheServiceReadsItOff()
    {
        WithWrittenConfig(allSessionKill: false, iniPath =>
        {
            var srvIni = new monkmode.IniFile();
            srvIni.Load(iniPath);
            // Absent => GetKeyValue returns "" => off (the byte-identical default block).
            Assert.Equal("", srvIni.GetKeyValue("Process", "AllSession"));
            Assert.False(monkmode.Service1.AllSessionKillArmed(srvIni.GetKeyValue("Process", "AllSession")));
        });
    }

    [Fact]
    public void WriteConfig_WithFlag_EveryReadersWrapperAgreesOnTheCanonical()
    {
        // The all-session flag is MAC-covered: the CLI writer and all four CanonicalFromIni
        // readers must derive the IDENTICAL canonical from the written ini, or a reader would
        // fail every MAC check and the block would freeze. (Canonicals only - DPAPI-free.)
        WithWrittenConfig(allSessionKill: true, iniPath =>
        {
            var cliIni = new MonkMode.IniFile();
            cliIni.Load(iniPath);
            var srvIni = new monkmode.IniFile();
            srvIni.Load(iniPath);
            var guardIni = new mm_guard.IniFile();
            guardIni.Load(iniPath);
            var notifyIni = new mm_notify.IniFile();
            notifyIni.Load(iniPath);

            var cli = MonkMode.Blocker.CanonicalFromIni(cliIni);
            // S2 (v1.1): the arm path now writes the v10 [Slot1] section, so the all-session
            // flag rides Slot1.AllSession INSIDE the canonical the readers verify with - i.e.
            // the slot DATA is MAC-covered again.
            // !! DO NOT ARM OR DEPLOY THIS TREE UNTIL S3a. S2 did NOT close the S1 hole for
            // ENFORCEMENT: the service still reads the v9 mirror keys ([Time] Until/CoolOffUntil,
            // [User] CustomSites, [Process] List, [Commit] Committed, [Partner] Hash/UnlockedAt),
            // and those keys are OUTSIDE the canonical, so an edit to them leaves macValid TRUE
            // and is not tamper-evident - editing [Time] Until into the past still tears the
            // whole block down. Slot data covered != enforcement covered. S3a moves the
            // enforcement READERS onto the slots; that is what actually closes it.
            // The v9 [Process] AllSession mirror is still written and is still checked above.
            Assert.Contains("Slot1.AllSession=yes\n", cli);
            Assert.Equal(cli, MonkMode.Tests.TestSvc.New().CanonicalFromIni(srvIni));
            Assert.Equal(cli, mm_guard.Program.CanonicalFromIni(guardIni));
            Assert.Equal(cli, new mm_notify.Form1().CanonicalFromIni(notifyIni));
        });
    }
}

// ---- v1.1 S4: the PER-SLOT all-session union ----
//
// v10 arms up to eight blocks at once and each carries its OWN AllSession flag inside the
// MAC-covered canonical, while the v9 [Process] AllSession mirror is a single machine-wide
// key OUTSIDE it. The tick's scope is `iniAllSessionKill OrElse AnySlotAllSessionKill(...)`,
// and what matters is that the second disjunct can only ever ADD sessions to the kill scope:
// blanking the raw-editable mirror must no longer be able to narrow a live block's reach.
public class PerSlotAllSessionUnionTests
{
    private static readonly DateTime AsOf = new(2026, 8, 18, 12, 0, 0);
    private const long Grace = 5;
    private const string Hw = "2026-08-18 12:00:00";
    private const string Future = "2026-08-18 18:00:00";
    private const string Past = "2026-08-18 06:00:00";

    private static monkmode.Service1.SlotState Slot(string id, string until, string allSession, string startAt = "")
        => new()
        {
            Id = id,
            UntilText = until,
            StartAt = startAt,
            ScheduleActiveUntil = "",
            ScheduleSpec = "",
            AllSession = allSession,
            Sites = new List<string>(),
            Apps = new List<string>(),
            UrlPatterns = new List<string>(),
        };

    private static bool Union(bool mirror, List<monkmode.Service1.SlotState> slots, bool macValid = true)
        => mirror || monkmode.Service1.AnySlotAllSessionKill(slots, AsOf, Grace, macValid, Hw);

    [Fact]
    public void OneSlotAsking_ArmsIt_ForTheWholeTick()
    {
        // The union is over slots, not per slot: the service has ONE kill loop and one scope
        // decision per tick, so a single all-session block widens that tick for everything it
        // kills. Over-killing a session is the safe direction; under-killing is the bypass.
        var slots = new List<monkmode.Service1.SlotState>
        {
            Slot("1", Future, ""),
            Slot("2", Future, "yes"),
            Slot("3", Future, ""),
        };
        Assert.True(Union(mirror: false, slots));
        Assert.True(monkmode.Service1.ProcessInKillScope(Union(false, slots), 3));
    }

    [Fact]
    public void NoSlotAsking_LeavesTheScopeExactlyWhereItWas()
    {
        var slots = new List<monkmode.Service1.SlotState> { Slot("1", Future, ""), Slot("2", Future, "no") };
        Assert.False(Union(mirror: false, slots));
        Assert.False(monkmode.Service1.ProcessInKillScope(Union(false, slots), 3));
        Assert.True(monkmode.Service1.ProcessInKillScope(Union(false, slots), 0));   // session 0 unchanged
    }

    [Fact]
    public void ABlankedV9Mirror_CanNoLongerNarrowALiveBlock()
    {
        // THE TAMPER THIS CLOSES. [Process] AllSession sits outside the v10 canonical, so
        // editing it away leaves macValid TRUE. Before the slot union that silently dropped
        // the cross-session kill; now the slot's own MAC-covered flag re-arms it, and blanking
        // the slot's copy instead breaks the MAC (pinned above by
        // AllSessionKillFlag_IsMacCovered_FlippingItBreaksTheMac).
        var slots = new List<monkmode.Service1.SlotState> { Slot("1", Future, "yes") };
        Assert.True(Union(mirror: false, slots));
    }

    [Fact]
    public void WidenOnly_TheUnionNeverRemovesWhatTheMirrorAlreadyArmed()
    {
        // Monotonicity over every shape: with the mirror ON the answer is ON regardless of
        // what the slots say - a slot can never switch the machine-wide flag back off.
        foreach (var slots in new[]
                 {
                     new List<monkmode.Service1.SlotState>(),
                     new List<monkmode.Service1.SlotState> { Slot("1", Future, "") },
                     new List<monkmode.Service1.SlotState> { Slot("1", Past, "yes") },
                     new List<monkmode.Service1.SlotState> { Slot("1", Future, "yes") },
                 })
        {
            Assert.True(Union(mirror: true, slots));
            if (Union(false, slots)) Assert.True(Union(true, slots));
        }
    }

    [Fact]
    public void MembershipMatchesTheAppUnion_SoAppsAndTheirScopeCannotDisagree()
    {
        // AnySlotAllSessionKill filters on SlotContributesLists - the SAME membership the
        // Apps union uses - so a slot's apps and the sessions they are killed in are always
        // decided by one predicate. An EXPIRED slot contributes neither; a PENDING one
        // contributes both.
        var expired = new List<monkmode.Service1.SlotState> { Slot("1", Past, "yes") };
        Assert.False(Union(false, expired));
        Assert.Empty(monkmode.Service1.UnionSlotApps(expired, AsOf, Grace, true, Hw));

        var pending = new List<monkmode.Service1.SlotState> { Slot("2", "", "yes", startAt: Future) };
        Assert.True(Union(false, pending));
    }

    [Fact]
    public void FrozenConfig_NeverLosesAnAllSessionKill()
    {
        // macValid=False reads every slot as enforcing (SlotEnforcesNow), so a tampered config
        // keeps the widened scope rather than quietly dropping to session-0-only. It cannot
        // INVENT one either: with no slot asking, the answer is still off.
        Assert.True(Union(false, new List<monkmode.Service1.SlotState> { Slot("1", Past, "yes") }, macValid: false));
        Assert.False(Union(false, new List<monkmode.Service1.SlotState> { Slot("1", Future, "") }, macValid: false));
    }
}
