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

// MonkMode.Tests - the F2 URL WATCHER seam (v1.1 S7, pins P54 + P61), i.e. everything
// UrlMatchTests.cs deliberately left out: which windows get looked at, when the address bar is
// read at all, and what happens when the read or the redirect fails.
//
// NO UIAutomation IS LOADED BY THIS FILE. That is the point of the seam: UrlWatch.UrlReaderHook /
// ProcessNameHook / RedirectHook are Nothing in production (the live AutomationElement code runs)
// and assigned here (it does not). So every assertion below is a pure string/int assertion, no
// browser is started, nothing is armed, and a test run never touches a real omnibox.
//
// THE PROPERTY THESE TESTS EXIST FOR. The notifier is ALSO the user-session app-kill loop, so a
// URL nudge that throws would take app-kill down with it - a bypass manufactured by a convenience
// feature (R12). Hence: a hook that throws must yield "do nothing", never an exception; and a
// redirect must be impossible unless the foreground really is a watched browser, a pattern really
// exists, and the P60 cooldown really has elapsed.
//
// THE HOOKS ARE PROCESS-GLOBAL, so every test here restores them in a finally and they all live in
// ONE class (xunit runs the tests of a class one at a time; separate classes may run in parallel).

namespace MonkMode.Tests;

public class UrlWatchSeamTests
{
    // The shipped shortform pattern set (P63), same one UrlMatchTests uses.
    private static readonly string[] Shipped =
    {
        "youtube.com/shorts", "instagram.com/reels", "facebook.com/reel", "youtube.com/",
    };

    // Install hooks, run the body, and always put the seam back the way production leaves it:
    // all three Nothing, so nothing after this test can accidentally run against a stub.
    private static void WithHooks(Func<string>? url, Func<string>? procName, Action<string>? redirect, Action body)
    {
        try
        {
            mm_notify.UrlWatch.UrlReaderHook =
                url is null ? null : new mm_notify.UrlWatch.ForegroundUrlReader(() => url());
            mm_notify.UrlWatch.ProcessNameHook =
                procName is null ? null : new mm_notify.UrlWatch.ForegroundProcessNameReader(() => procName());
            mm_notify.UrlWatch.RedirectHook =
                redirect is null ? null : new mm_notify.UrlWatch.RedirectPerformer(t => redirect(t));
            body();
        }
        finally
        {
            mm_notify.UrlWatch.UrlReaderHook = null;
            mm_notify.UrlWatch.ProcessNameHook = null;
            mm_notify.UrlWatch.RedirectHook = null;
        }
    }

    // ---------------------------------------------------------------
    // P54 - who gets watched
    // ---------------------------------------------------------------

    [Fact]
    public void P54_TheWatchedProcessSet_IsExactlyTheThreeChromiumBrowsers()
    {
        Assert.Equal(new[] { "chrome", "msedge", "brave" }, mm_notify.UrlWatch.BrowserProcessNames);
    }

    [Theory]
    // The three, in every casing and with or without the extension Process.ProcessName omits.
    [InlineData("chrome", true)]
    [InlineData("Chrome", true)]
    [InlineData("CHROME.EXE", true)]
    [InlineData("msedge", true)]
    [InlineData("brave.exe", true)]
    [InlineData("  brave  ", true)]
    // Not watched. firefox is the documented R13 residual; the rest are the reason this axis is
    // an EXACT match and not the substring test the BLOCKING predicates use - "chromedriver" or
    // "bravery" exposing an Edit control must never be typed into.
    [InlineData("firefox", false)]
    [InlineData("chromedriver", false)]
    [InlineData("bravery", false)]
    [InlineData("notepad", false)]
    [InlineData("chrome.exe.exe", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(".exe", false)]
    [InlineData(null, false)]
    public void P54_IsWatchedBrowser(string? processName, bool expected)
    {
        Assert.Equal(expected, mm_notify.UrlWatch.IsWatchedBrowser(processName));
    }

    // ---------------------------------------------------------------
    // The tick decision
    // ---------------------------------------------------------------

    [Fact]
    public void AHitOnAWatchedBrowser_YieldsTheRedirectTarget_AndTheActorIsCalledWithIt()
    {
        var seen = new List<string>();
        WithHooks(url: () => "https://www.instagram.com/reels/abc123/", procName: null, redirect: seen.Add, body: () =>
        {
            var target = mm_notify.UrlWatch.TickTarget("chrome", Shipped, 0, 10_000);
            Assert.Equal("https://instagram.com/", target);
            Assert.True(mm_notify.UrlWatch.PerformRedirect(target));
            Assert.Equal(new[] { "https://instagram.com/" }, seen);
        });
    }

    [Fact]
    public void AYouTubeHit_GoesToTheSubscriptionsFeed_NotTheHomeFeed()
    {
        WithHooks(url: () => "youtube.com/shorts/xyz", procName: null, redirect: null, body: () =>
        {
            Assert.Equal(mm_notify.UrlWatch.YouTubeRedirect,
                         mm_notify.UrlWatch.TickTarget("msedge", Shipped, 0, 10_000));
        });
    }

    [Fact]
    public void AnUnblockedUrl_IsNoAction()
    {
        var seen = new List<string>();
        WithHooks(url: () => "https://news.ycombinator.com/", procName: null, redirect: seen.Add, body: () =>
        {
            var target = mm_notify.UrlWatch.TickTarget("brave", Shipped, 0, 10_000);
            Assert.Equal("", target);
            // ...and the actor refuses "" on its own account, so a caller that forgets to check
            // still cannot type an empty string into somebody's address bar.
            Assert.False(mm_notify.UrlWatch.PerformRedirect(target));
            Assert.Empty(seen);
        });
    }

    // ---------------------------------------------------------------
    // The three gates that must hold BEFORE the address bar is read
    // ---------------------------------------------------------------

    [Fact]
    public void ANonBrowserForeground_IsNeverEvenREAD()
    {
        var reads = 0;
        WithHooks(url: () => { reads++; return "https://www.instagram.com/reels/abc/"; },
                  procName: null, redirect: null, body: () =>
        {
            foreach (var name in new[] { "notepad", "explorer", "firefox", "", null! })
            {
                Assert.Equal("", mm_notify.UrlWatch.TickTarget(name, Shipped, 0, 10_000));
            }
            // Not "no redirect" - no READ. Reading a non-browser's UI would be both pointless
            // and the first half of typing into it.
            Assert.Equal(0, reads);
        });
    }

    [Fact]
    public void WithNoUrlPatternsArmed_TheWatcherDoesNothing_AndReadsNothing()
    {
        var reads = 0;
        WithHooks(url: () => { reads++; return "https://www.instagram.com/reels/abc/"; },
                  procName: null, redirect: null, body: () =>
        {
            // Every shape a "no --urls anywhere" config produces: no slots at all, a slot whose
            // UrlPatterns key is empty (which splits to one empty token), and whitespace junk.
            var empties = new IEnumerable<string>?[]
            {
                null, new string[0], new[] { "" }, new[] { "", "" }, new[] { "   ", "\t" }, new string[] { null! },
            };
            foreach (var patterns in empties)
            {
                Assert.False(mm_notify.UrlWatch.HasAnyPattern(patterns!));
                Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", patterns!, 0, 10_000));
            }
            Assert.Equal(0, reads);
            // One real pattern in the set is enough to open the gate.
            Assert.True(mm_notify.UrlWatch.HasAnyPattern(new[] { "", "youtube.com/shorts" }));
        });
    }

    [Fact]
    public void TheCooldownSuppressesASecondActionInsideFiveSeconds()
    {
        var reads = 0;
        WithHooks(url: () => { reads++; return "https://www.instagram.com/reels/abc/"; },
                  procName: null, redirect: null, body: () =>
        {
            // First hit at t=100_000: acts (nothing has been done yet).
            Assert.Equal("https://instagram.com/", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 0, 100_000));
            Assert.Equal(1, reads);

            // The watcher beats every 2s, so the next two beats fall inside the 5s cooldown -
            // exactly the case P60 exists for (the omnibox still reads the OLD url while the
            // redirect is loading, so without this the user gets a stream of SetValues).
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 100_000, 102_000));
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 100_000, 104_000));
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 100_000, 104_999));
            // Suppressed BEFORE the read, not after: a read we may not act on is pure cost.
            Assert.Equal(1, reads);

            // 5s exactly, and beyond, act again.
            Assert.Equal("https://instagram.com/", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 100_000, 105_000));
            Assert.Equal("https://instagram.com/", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 100_000, 106_000));
            Assert.Equal(3, reads);
            Assert.Equal(5000, mm_notify.UrlWatch.RedirectCooldownMs);
        });
    }

    // ---------------------------------------------------------------
    // Fail-soft (R12): nothing here may throw into the notifier
    // ---------------------------------------------------------------

    [Fact]
    public void AReaderThatThrows_YieldsNoActionAndNoException()
    {
        var seen = new List<string>();
        WithHooks(url: () => throw new InvalidOperationException("UIA went bang"),
                  procName: null, redirect: seen.Add, body: () =>
        {
            // The realistic failures are a browser that died between the foreground read and the
            // UIA read, and a provider that faults mid-call. Both arrive here as an exception.
            Assert.Equal("", mm_notify.UrlWatch.ReadForegroundUrlSafe());
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 0, 10_000));
            Assert.Empty(seen);
        });
    }

    [Fact]
    public void AReaderThatReturnsNothing_IsNoAction()
    {
        WithHooks(url: () => null!, procName: null, redirect: null, body: () =>
        {
            Assert.Equal("", mm_notify.UrlWatch.ReadForegroundUrlSafe());
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 0, 10_000));
        });
        WithHooks(url: () => "", procName: null, redirect: null, body: () =>
        {
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Shipped, 0, 10_000));
        });
    }

    [Fact]
    public void AProcessNameReaderThatThrows_YieldsAnUnwatchedForeground()
    {
        WithHooks(url: null, procName: () => throw new UnauthorizedAccessException(),
                  redirect: null, body: () =>
        {
            // "" is not a watched browser, so the failure degrades to "do not watch this beat".
            Assert.Equal("", mm_notify.UrlWatch.ForegroundProcessNameSafe());
            Assert.False(mm_notify.UrlWatch.IsWatchedBrowser(mm_notify.UrlWatch.ForegroundProcessNameSafe()));
        });
        WithHooks(url: null, procName: () => null!, redirect: null, body: () =>
        {
            Assert.Equal("", mm_notify.UrlWatch.ForegroundProcessNameSafe());
        });
        WithHooks(url: null, procName: () => "chrome", redirect: null, body: () =>
        {
            Assert.Equal("chrome", mm_notify.UrlWatch.ForegroundProcessNameSafe());
        });
    }

    [Fact]
    public void ARedirectThatThrows_IsSwallowed_AndReportsFailure()
    {
        WithHooks(url: null, procName: null,
                  redirect: t => throw new InvalidOperationException("SetValue refused"), body: () =>
        {
            // A browser can refuse the SetValue (a modal open, the window closing). It costs the
            // nudge, nothing else - the hosts block is what actually stops the page.
            Assert.False(mm_notify.UrlWatch.PerformRedirect("https://instagram.com/"));
        });
    }

    // ---------------------------------------------------------------
    // The redirect's step order - and the one place this feature could act WRONGLY
    // ---------------------------------------------------------------

    [Fact]
    public void AFailedSetFocus_SendsNoKeystrokeAtAll()
    {
        // THE FINDING THIS PINS. The Enter is a SYNTHESIZED GLOBAL key event: it lands wherever
        // keyboard focus actually is. Swallowing a SetFocus failure and pressing on anyway means
        // pressing Enter into whatever the user is really in - a page button, a half-filled form.
        // That is the only way the URL watcher could ever do something the user did not ask for,
        // and it would be doing it on their keyboard focus rather than on a block. So focus is a
        // PRECONDITION: it fails, nothing is typed and no key is sent.
        var steps = new List<string>();
        var ok = mm_notify.UrlWatch.PerformRedirectSteps(
            takeFocus: () => throw new InvalidOperationException("the browser refused focus"),
            typeTarget: () => steps.Add("type"),
            pressEnter: () => steps.Add("enter"));

        Assert.False(ok);
        Assert.Empty(steps);   // not "no enter" - NOTHING. The omnibox is not written to either.
    }

    [Fact]
    public void TheHappyPathRunsAllThreeStepsInOrder()
    {
        var steps = new List<string>();
        var ok = mm_notify.UrlWatch.PerformRedirectSteps(
            takeFocus: () => steps.Add("focus"),
            typeTarget: () => steps.Add("type"),
            pressEnter: () => steps.Add("enter"));

        Assert.True(ok);
        // Order is the contract: SetValue on an unfocused omnibox is accepted but the Enter would
        // then navigate whatever DOES have focus, so focus must come first.
        Assert.Equal(new[] { "focus", "type", "enter" }, steps);
    }

    [Fact]
    public void AFailedSetValue_StopsBeforeTheEnter()
    {
        // Same reasoning one step later: if the text never landed in the address bar, an Enter
        // would submit whatever the box still holds - the page the user was already on, or their
        // half-typed search.
        var steps = new List<string>();
        var ok = mm_notify.UrlWatch.PerformRedirectSteps(
            takeFocus: () => steps.Add("focus"),
            typeTarget: () => throw new InvalidOperationException("SetValue refused"),
            pressEnter: () => steps.Add("enter"));

        Assert.False(ok);
        Assert.Equal(new[] { "focus" }, steps);
    }

    [Fact]
    public void AMissingStep_IsRefusedRatherThanPartlyRun()
    {
        var steps = new List<string>();
        mm_notify.UrlWatch.RedirectStep record = () => steps.Add("ran");
        Assert.False(mm_notify.UrlWatch.PerformRedirectSteps(null, record, record));
        Assert.False(mm_notify.UrlWatch.PerformRedirectSteps(record, null, record));
        Assert.False(mm_notify.UrlWatch.PerformRedirectSteps(record, record, null));
        Assert.Empty(steps);
    }

    [Fact]
    public void PerformRedirect_RefusesAnEmptyTarget_WithoutCallingTheActor()
    {
        var seen = new List<string>();
        WithHooks(url: null, procName: null, redirect: seen.Add, body: () =>
        {
            Assert.False(mm_notify.UrlWatch.PerformRedirect(""));
            Assert.False(mm_notify.UrlWatch.PerformRedirect(null));
            Assert.Empty(seen);
        });
    }

    [Fact]
    public void TickTarget_IsTotal_AcrossTheHostileCorpus()
    {
        // The S6 corpus shapes, now fed through the WHOLE tick rather than the matcher alone,
        // against nonsense tick pairs and a hostile pattern set. Nothing may throw; every answer
        // is either "" or a target that carries a host.
        var urls = new[]
        {
            "", " ", "\t\r\n", "/", "//", ":", "@", "?", "#", "about:blank", "data:text/html,x",
            "chrome://settings", "file:///c:/x", "view-source:http://youtube.com/shorts",
            "youtube.com", "youtube.com/", "http://user:pw@youtube.com:8080/shorts/a?b#c",
            "HTTPS://WWW.YouTube.COM/SHORTS/A", "m.youtube.com/shorts/a", new string('x', 5000),
            "youtube.com/shorts/" + new string('/', 500), "\uD800", "xn--80ak6aa92e.com/shorts",
        };
        var patternSets = new IEnumerable<string>[]
        {
            Shipped, new[] { "" }, new[] { "youtube.com" }, new[] { "/" }, new[] { "?" },
            new[] { new string('y', 300) },
        };
        var ticks = new (long last, long now)[] { (0, 0), (0, -1), (-5, -1), (long.MaxValue, long.MinValue), (1, long.MaxValue) };

        foreach (var u in urls)
        {
            foreach (var set in patternSets)
            {
                foreach (var (last, now) in ticks)
                {
                    string target = "";
                    WithHooks(url: () => u, procName: null, redirect: null,
                              body: () => target = mm_notify.UrlWatch.TickTarget("chrome", set, last, now));
                    if (target.Length > 0)
                    {
                        Assert.StartsWith("https://", target, StringComparison.Ordinal);
                        Assert.NotEqual("https:///", target);
                    }
                }
            }
        }
    }

    [Fact]
    public void APatternSetThatThrowsWhileEnumerating_IsNoAction()
    {
        // Not a real config shape - the union comes from RawSlotUrlPatterns, which returns a
        // List - but it pins the totality contract at the one place a caller could break it.
        WithHooks(url: () => "https://www.instagram.com/reels/a/", procName: null, redirect: null, body: () =>
        {
            Assert.Equal("", mm_notify.UrlWatch.TickTarget("chrome", Exploding(), 0, 10_000));
        });

        static IEnumerable<string> Exploding()
        {
            yield return "youtube.com/shorts";
            throw new InvalidOperationException("collection went bang");
        }
    }
}

// The notifier's view of the armed slots' URL patterns (v1.1 S7): the union the watcher matches
// against, read raw from the config each beat by the same discipline RawSlotApps uses for the
// app-kill union - no MAC gate, no decrypt, bounded by MaxSlots rather than by the stored
// SlotCount, so a forged count cannot silence it.
public class NotifierSlotUrlPatternTests
{
    private static mm_notify.IniFile Ini(params (string section, string key, string value)[] entries)
    {
        var ini = new mm_notify.IniFile();
        foreach (var (section, key, value) in entries)
        {
            ini.AddSection(section);
            ini.SetKeyValue(section, key, value);
        }
        return ini;
    }

    [Fact]
    public void EverySlotContributes_DedupedAndInFirstOccurrenceOrder()
    {
        // "|" is the P55 pack separator (both "|" and ";" are refused INSIDE a pattern at arm
        // time, so the split is unambiguous), and the dedupe is case-insensitive because the
        // matcher is.
        var patterns = mm_notify.Form1.RawSlotUrlPatterns(Ini(
            ("Slot1", "UrlPatterns", "youtube.com/shorts|instagram.com/reels"),
            ("Slot2", "UrlPatterns", "YouTube.com/Shorts| facebook.com/reel "),   // case dupe + padding
            ("Slot8", "UrlPatterns", "youtube.com/")));
        Assert.Equal(new[] { "youtube.com/shorts", "instagram.com/reels", "facebook.com/reel", "youtube.com/" },
                     patterns);
    }

    [Fact]
    public void TheScanIsBoundedByMaxSlots_NotByTheStoredSlotCount()
    {
        Assert.Equal(new[] { "youtube.com/shorts" },
                     mm_notify.Form1.RawSlotUrlPatterns(Ini(("Slots", "SlotCount", "0"),
                                                            ("Slot1", "UrlPatterns", "youtube.com/shorts"))));
        Assert.Empty(mm_notify.Form1.RawSlotUrlPatterns(Ini(("Slot9", "UrlPatterns", "youtube.com/shorts"))));
    }

    [Fact]
    public void NoPatternsAnywhere_IsAnEmptyUnion_WhichSwitchesTheWatcherOff()
    {
        // The common case by far: blocks armed with sites and apps but no --urls at all. The
        // union is empty, HasAnyPattern is False, and the watcher never reads an address bar.
        foreach (var ini in new[]
                 {
                     new mm_notify.IniFile(),
                     Ini(("Slot1", "UrlPatterns", "")),
                     Ini(("Slot1", "UrlPatterns", "  | |")),
                     Ini(("Slot1", "Apps", "chrome.exe;")),
                 })
        {
            var patterns = mm_notify.Form1.RawSlotUrlPatterns(ini);
            Assert.Empty(patterns);
            Assert.False(mm_notify.UrlWatch.HasAnyPattern(patterns));
        }
        Assert.Empty(mm_notify.Form1.RawSlotUrlPatterns(null!));
    }
}
