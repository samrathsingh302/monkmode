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

    // ---------------------------------------------------------------
    // F14 + F34 - the REDIRECT-TIME gate (19/08/2026 bug-hunt, both P2)
    // ---------------------------------------------------------------
    //
    // The redirect re-resolves the foreground window, because the pass runs on a pool thread and
    // the user may have alt-tabbed since the read. What used to make that safe was FindOmnibox:
    // "a non-Chromium window has no element matching the omnibox id or class". It is not a safety
    // property - AutomationId and ClassName are values a process publishes about its OWN controls
    // (F34) - and it says nothing at all about whether the window in front of us is the one that
    // was on a blocked URL (F14). MayRedirectToWindow is the gate that actually holds, and it is
    // pure, so the decision is pinned here without a browser in the room. What the WINDOW HANDLES
    // mean live is smoke-owed; what the predicate DECIDES is not.

    private static readonly IntPtr Blocked = new(0x1234);   // the window the hit was read from
    private static readonly IntPtr Other = new(0x5678);     // some other window entirely

    [Fact]
    public void F14_ARedirectIsRefused_WhenTheForegroundIsNoLongerTheWindowTheHitCameFrom()
    {
        // THE F14 REPRO: read a blocked URL in Chrome, user alt-tabs to Edge (a watched browser,
        // so the old process-name reasoning would have waved it through) during the UIA read.
        // Edge is on a half-written form. Acting would navigate it away and lose that state.
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Other, "msedge"));
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Other, "chrome"));
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Other, "brave"));
        // A second window of the SAME browser is still a different window, and the one the user
        // is looking at now was never shown to be on a blocked URL.
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, new IntPtr(0x1235), "chrome"));
        // The window the hit really came from, still in front: this is the case the feature is
        // for, and it must still work - refusing everything would be a silent feature deletion.
        Assert.True(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Blocked, "chrome"));
    }

    [Fact]
    public void F34_ARedirectIsRefused_WhenTheWindowIsNotAWatchedBrowserProcess()
    {
        // THE F34 REPRO: a non-browser window that PASSES the omnibox-shape check. Any app can
        // publish a ControlType.Edit whose AutomationId is "view_1012" or whose ClassName ends
        // "OmniboxViewViews" - they are self-declared metadata, not identity - so FindOmnibox
        // would hand back an element and MonkMode would SetFocus + SetValue + synthesise Enter
        // into it. The process re-check is what refuses, and it is applied to the window we are
        // about to ACT on, not only to the one we read.
        foreach (var impostor in new[] { "notepad", "explorer", "chromedriver", "bravery",
                                         "firefox", "mm_notify", "", " ", ".exe", null })
            Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Blocked, impostor!),
                         $"typed into '{impostor}'");
        // Even the exact window the URL was read from is refused once its process is not one we
        // watch - which is the shape a window handle reused by another process would take.
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Blocked, "notepad"));
        // The three watched browsers, in the casings Process.ProcessName and a caller may use.
        foreach (var browser in new[] { "chrome", "Chrome", "CHROME.EXE", "msedge", "brave.exe" })
            Assert.True(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, Blocked, browser), browser);
    }

    [Fact]
    public void NoRecordedWindowAndNoForegroundWindow_BothMeanDoNothing()
    {
        // Zero on either side is "we do not know", and the answer to not knowing is never to
        // type into somebody's window. A Zero READ handle is what a failed or absent address-bar
        // read leaves behind, which is also what makes the record one-shot: LivePerformRedirect
        // swaps it back to Zero, so one read can authorise at most one redirect.
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(IntPtr.Zero, Blocked, "chrome"));
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(Blocked, IntPtr.Zero, "chrome"));
        Assert.False(mm_notify.UrlWatch.MayRedirectToWindow(IntPtr.Zero, IntPtr.Zero, "chrome"));
    }

    // ---------------------------------------------------------------
    // F33 - the in-flight latch is BOUNDED (19/08/2026 bug-hunt, P2)
    // ---------------------------------------------------------------
    //
    // Every UIA call in a pass is untimed, and the latch was released only in the work item's
    // Finally - which never runs for a pass that never RETURNS. One hanging provider (a stub
    // named chrome.exe, foregrounded once) killed the watcher for the life of the process,
    // silently. The pass cannot be aborted (no safe way to abort a thread inside a cross-process
    // UIA call, and Thread.Abort is gone), so the fix is that a stale pass stops BLOCKING.

    [Fact]
    public void F33_AHungPassStopsBlockingNewPasses_OnceItIsStale()
    {
        const long stale = mm_notify.UrlWatch.WatchPassStaleMs;
        Assert.Equal(60_000L, stale);                      // 30 beats of the 2s watcher
        // Idle: any beat starts a pass. 0 is the idle marker.
        Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(0, 10_000, stale));
        // A pass in flight blocks the beats inside the bound - the property the latch exists for,
        // so a slow-but-live browser cannot pile passes up.
        Assert.False(mm_notify.UrlWatch.ShouldStartWatchPass(10_000, 10_000, stale));
        Assert.False(mm_notify.UrlWatch.ShouldStartWatchPass(10_000, 12_000, stale));
        Assert.False(mm_notify.UrlWatch.ShouldStartWatchPass(10_000, 69_999, stale));
        // THE F33 REPRO: past the bound the watcher is no longer hostage to it.
        Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(10_000, 70_000, stale));
        Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(10_000, 10_000_000, stale));
        // ...and it stays unblocked for ever after, so "dead for the boot" is unreachable.
        for (var t = 70_000L; t < 10_000_000L; t += 97_777L)
            Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(10_000, t, stale));
    }

    [Fact]
    public void F33_NonsenseMarksStartAPass_BecauseAWedgedWatcherIsTheFailureThatMatters()
    {
        // Same R1 call ShouldActOnHit makes, for the same reason: a wrong "start" costs one extra
        // concurrent pass, a wrong "don't" costs the watcher for the whole boot.
        var extremes = new long[] { long.MinValue, -1, 0, 1, 59_999, 60_000, long.MaxValue };
        foreach (var since in extremes)
        foreach (var now in extremes)
        foreach (var bound in extremes)
        {
            var started = mm_notify.UrlWatch.ShouldStartWatchPass(since, now, bound);
            if (since <= 0 || now < since || bound <= 0) Assert.True(started);
        }
        // And it is monotone in now: once a pass is stale, more time cannot un-stale it.
        var rng = new Random(20260819);
        for (var i = 0; i < 20_000; i++)
        {
            long since = rng.Next(1, 1_000_000);
            long bound = rng.Next(1, 200_000);
            long now = since + rng.Next(0, 400_000);
            if (!mm_notify.UrlWatch.ShouldStartWatchPass(since, now, bound)) continue;
            Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(since, now + rng.Next(1, 100_000), bound));
        }
    }

    [Fact]
    public void F33_TheLatchIsClaimedAndReleasedByStartTick_SoATakenOverPassCannotReleaseTheNewOne()
    {
        // The Form1 side of the fix, as the compare-and-swap algebra it actually is: the latch
        // holds the START TICK of the pass in flight, a pass releases it by CAS against its OWN
        // tick, and a pass that was taken over therefore finds a different value and clears
        // NOTHING. Without that, the hung pass returning at minute 40 would open the gate
        // underneath the pass that replaced it and let passes run two-deep for ever.
        long latch = 0;
        const long stale = mm_notify.UrlWatch.WatchPassStaleMs;

        // Beat 1 at t=1000 claims the idle latch.
        Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(latch, 1000, stale));
        Assert.Equal(0L, Interlocked.CompareExchange(ref latch, 1000L, 0L));
        const long hungPassStart = 1000L;

        // Beats inside the bound are refused; the beat past it takes the latch over.
        Assert.False(mm_notify.UrlWatch.ShouldStartWatchPass(latch, 3000, stale));
        Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(latch, 61_000, stale));
        Assert.Equal(hungPassStart, Interlocked.CompareExchange(ref latch, 61_000L, hungPassStart));

        // The hung pass finally returns and tries to release. It must not succeed.
        Interlocked.CompareExchange(ref latch, 0L, hungPassStart);
        Assert.Equal(61_000L, latch);
        Assert.False(mm_notify.UrlWatch.ShouldStartWatchPass(latch, 62_000, stale));

        // The pass that actually owns the latch releases it, and the watcher is idle again.
        Interlocked.CompareExchange(ref latch, 0L, 61_000L);
        Assert.Equal(0L, latch);
        Assert.True(mm_notify.UrlWatch.ShouldStartWatchPass(latch, 62_000, stale));
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
