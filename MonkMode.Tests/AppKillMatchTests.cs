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

// MonkMode.Tests - the app-kill MATCH predicate (ProcessNameInKillList), the decision that
// turns "this process is in scope this tick" into "kill it".
//
// THE BUG THIS PINS (fail-open, found in the 03/08/2026 audit): the match used to be a
// case-SENSITIVE String.Contains - Service1.vb `killList.Contains(Proc.ProcessName + ".exe")`
// and Form1.vb `killList.Contains(proc.ProcessName & ".exe")`. But the two sides are written
// by different parties and never had to agree on casing:
//   - the list side keeps whatever the user typed at arm time. PackApps (Blocker.vb:630) only
//     APPENDS a missing ".exe"; it never lower-cases. The shipped preset script arms
//     `--apps 'WhatsApp.exe'` (presets/mm-presets.ps1, mm-insta), so "WhatsApp.exe" is what
//     lands in [Process] List. The schedule's own apps (Blocker.vb:1059) preserve case too, and
//     flow into the SAME string via EffectiveKillList.
//   - the process side reports whatever casing Windows holds for the running image.
// When those disagreed the app was simply never killed and the block silently under-enforced.
// Fail-open is the one thing enforcement may never do, so the match now ignores case.
//
// The load-bearing property proved here is WIDEN-ONLY: OrdinalIgnoreCase can only ever ADD
// matches, so no kill the old code made is removed. That is what makes a matcher change safe
// to land on a live enforcement path - the same stance ProcessInKillScope (D2c) is held to.
//
// Pure string tests: no live process enumeration, no hosts/registry/SCM (the actual
// Process.Kill() is the live seam, covered by the elevated smoke, not here).

namespace MonkMode.Tests;

public class AppKillMatchTests
{
    // The exact predicate the kill loops used BEFORE this fix: an ordinal, case-sensitive
    // substring search. Kept here as the baseline the widen-only property is proved against.
    private static bool OldCaseSensitiveMatch(string killList, string processName) =>
        killList.Contains(processName + ".exe", StringComparison.Ordinal);

    // ---- The four required behaviours, on both assemblies' copies ----

    [Theory]
    // Exact-case match: the ordinary case, unchanged from before.
    [InlineData("chrome.exe;", "chrome", true)]
    [InlineData("chrome.exe;firefox.exe;", "firefox", true)]
    // Differing-case match: THE FIX. Every one of these silently missed before.
    [InlineData("WhatsApp.exe;", "whatsapp", true)]   // the shipped mm-insta arm vs a lower-case image
    [InlineData("whatsapp.exe;", "WhatsApp", true)]   // and the reverse
    [InlineData("whatsapp.exe;", "WHATSAPP", true)]
    [InlineData("CHROME.EXE;", "chrome", true)]       // suffix casing differs too
    [InlineData("Discord.exe|Steam.exe", "steam", true)]  // schedule-union "|" separator
    // Non-member miss: a name nobody armed is never killed (no over-broad matching).
    [InlineData("chrome.exe;firefox.exe;", "notepad", false)]
    [InlineData("chrome.exe;firefox.exe;", "chrom", false)]   // a prefix of an entry is not a member
    [InlineData("", "chrome", false)]                 // empty list matches nothing
    [InlineData(null, "chrome", false)]               // Nothing list matches nothing (no throw)
    public void ProcessNameInKillList_MatchesIgnoringCase(string? killList, string processName, bool expected)
    {
        Assert.Equal(expected, monkmode.Service1.ProcessNameInKillList(killList!, processName));
        Assert.Equal(expected, mm_notify.Form1.ProcessNameInKillList(killList!, processName));
    }

    [Theory]
    // The ".exe" suffixing: the predicate takes a BARE ProcessName (which is what
    // Process.ProcessName returns - no extension) and appends ".exe" itself.
    [InlineData("chrome.exe;", "chrome", true)]
    // So a list entry without the extension is NOT matched by the bare name - the suffix is
    // genuinely required on the list side. (PackApps guarantees it: it appends a missing ".exe"
    // before storing, so a real armed config always has it.)
    [InlineData("chrome;", "chrome", false)]
    // And a caller passing an already-suffixed name would look for "chrome.exe.exe" and miss -
    // pinning that the loops must pass ProcessName bare, as both do.
    [InlineData("chrome.exe;", "chrome.exe", false)]
    public void ProcessNameInKillList_AppendsExeToTheProcessName(string killList, string processName, bool expected)
    {
        Assert.Equal(expected, monkmode.Service1.ProcessNameInKillList(killList, processName));
        Assert.Equal(expected, mm_notify.Form1.ProcessNameInKillList(killList, processName));
    }

    [Fact]
    public void ProcessNameInKillList_WidensOnly_NeverRemovesAKillTheOldMatchMade()
    {
        // THE safety property. Case-insensitivity may only ADD kills: for every (list, name)
        // pair, if the old case-sensitive predicate matched, the new one must match too.
        // If this ever fails, the change has become capable of UNDER-killing - fail-open.
        string[] lists =
        {
            "chrome.exe;", "WhatsApp.exe;", "chrome.exe;firefox.exe;WhatsApp.exe;",
            "Discord.exe|Steam.exe", "vscode.exe;", "CHROME.EXE;", "null", "",
        };
        string[] names = { "chrome", "Chrome", "CHROME", "whatsapp", "WhatsApp", "steam",
                           "Steam", "code", "vscode", "notepad", "", "firefox" };

        foreach (var list in lists)
        {
            foreach (var name in names)
            {
                if (OldCaseSensitiveMatch(list, name))
                {
                    Assert.True(monkmode.Service1.ProcessNameInKillList(list, name),
                        $"case-insensitive match LOST a kill the old code made: list='{list}' name='{name}'");
                    Assert.True(mm_notify.Form1.ProcessNameInKillList(list, name),
                        $"case-insensitive match LOST a kill the old code made: list='{list}' name='{name}'");
                }
            }
        }

        // And it strictly ADDS at least the case that motivated the fix.
        Assert.False(OldCaseSensitiveMatch("WhatsApp.exe;", "whatsapp"));
        Assert.True(monkmode.Service1.ProcessNameInKillList("WhatsApp.exe;", "whatsapp"));
    }

    [Fact]
    public void ProcessNameInKillList_KeepsSubstringSemantics_SoTheChangeCannotNarrow()
    {
        // The predicate is still a SUBSTRING search over the delimited list, exactly as before.
        // A token-exact matcher would be tidier but would NARROW the set (this pair would stop
        // matching), and narrowing on an enforcement path is the fail-open being fixed here.
        // Documented as deliberate, not as desirable behaviour.
        Assert.True(OldCaseSensitiveMatch("vscode.exe;", "code"));
        Assert.True(monkmode.Service1.ProcessNameInKillList("vscode.exe;", "code"));
        Assert.True(mm_notify.Form1.ProcessNameInKillList("vscode.exe;", "code"));
    }

    [Fact]
    public void ProcessNameInKillList_ServiceAndNotifierCopiesAgree_ParityPinned()
    {
        // MonkMode_srv and MM_notify are separate assemblies that cannot reference one another,
        // so the matcher is duplicated - the same reason EffectiveKillList/ScheduleActive are.
        // This pins the two copies equal, exactly as EffectiveKillList_* does for the union: the
        // service (session 0) and the notifier (the interactive session) must agree on WHICH
        // processes are blocked, or a block would enforce differently in each session.
        string?[] lists = { null, "", "null", "chrome.exe;", "WhatsApp.exe;",
                            "chrome.exe;firefox.exe;", "Discord.exe|Steam.exe", "CHROME.EXE;" };
        string[] names = { "chrome", "Chrome", "CHROME", "whatsapp", "WhatsApp", "steam",
                           "discord", "notepad", "code", "" };

        foreach (var list in lists)
        {
            foreach (var name in names)
            {
                Assert.Equal(monkmode.Service1.ProcessNameInKillList(list!, name),
                             mm_notify.Form1.ProcessNameInKillList(list!, name));
            }
        }
    }

    [Fact]
    public void ProcessNameInKillList_NullOrEmptyList_MatchesNothing_AndDoesNotThrow()
    {
        // The loops call this inside their per-process Try, but a matcher that threw on a
        // Nothing list would burn the exception handler on every process every tick. Returning
        // False is the same OUTCOME the old NullReferenceException produced (it was swallowed by
        // the same Catch and killed nothing), so this is not a narrowing.
        Assert.False(monkmode.Service1.ProcessNameInKillList(null!, "chrome"));
        Assert.False(mm_notify.Form1.ProcessNameInKillList(null!, "chrome"));
        Assert.False(monkmode.Service1.ProcessNameInKillList("", "chrome"));
        Assert.False(mm_notify.Form1.ProcessNameInKillList("", "chrome"));
        Assert.False(monkmode.Service1.ProcessNameInKillList(null!, null!));
        Assert.False(mm_notify.Form1.ProcessNameInKillList(null!, null!));
    }

    [Fact]
    public void ProcessNameInKillList_DegenerateProcessName_MatchesAnyExeEntry_PreExistingAndDeliberate()
    {
        // Documenting a PRE-EXISTING quirk this fix deliberately does NOT change: an empty (or
        // Nothing) process name makes the search string ".exe", which is a substring of any real
        // list entry - so it reports a match. The old case-sensitive predicate did exactly the
        // same (`killList.Contains("" & ".exe")`).
        //
        // It is left alone on purpose. Suppressing it would mean returning False where the old
        // code returned True - a NARROWING, i.e. removing a kill, which is the fail-open this
        // whole slice exists to close. Over-matching a degenerate name errs toward over-blocking,
        // the safe direction here.
        //
        // Unreachable in practice: Process.ProcessName never returns ""/Nothing - it throws
        // InvalidOperationException for an exited process, and both loops catch that per-process.
        Assert.True(OldCaseSensitiveMatch("chrome.exe;", ""));   // the old behaviour...
        Assert.True(monkmode.Service1.ProcessNameInKillList("chrome.exe;", ""));   // ...preserved
        Assert.True(mm_notify.Form1.ProcessNameInKillList("chrome.exe;", ""));
        Assert.True(monkmode.Service1.ProcessNameInKillList("chrome.exe;", null!));
        Assert.True(mm_notify.Form1.ProcessNameInKillList("chrome.exe;", null!));
    }

    [Fact]
    public void ProcessNameInKillList_MatchesTheAppPresetTableRegardlessOfItsCasing()
    {
        // The D2a AppPresetTable entries are written lowercase as a house convention. That was
        // previously load-bearing (a mixed-case entry would have under-killed); it no longer is,
        // which is what the updated Blocker.vb comment now says. Pin it so a future contributor
        // adding "Discord.exe" to the table cannot silently reintroduce the fail-open.
        Assert.True(monkmode.Service1.ProcessNameInKillList("discord.exe;", "Discord"));
        Assert.True(monkmode.Service1.ProcessNameInKillList("Discord.exe;", "discord"));
        Assert.True(monkmode.Service1.ProcessNameInKillList("RiotClientServices.exe;", "riotclientservices"));
        Assert.True(mm_notify.Form1.ProcessNameInKillList("RiotClientServices.exe;", "riotclientservices"));
    }
}
