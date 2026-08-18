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

// MonkMode.Tests - the F2 URL layer (v1.1 S6, pins P56-P60): the whole decision surface of the
// URL watcher, brute-forced with no browser and no notifier in the room.
//
// WHAT THIS LAYER IS. Best-effort NUDGING, not enforcement. The hosts-file block (B2) is what
// actually stops a site loading; a redirect only steers a browser that can still reach the page.
// Two consequences shape every test below:
//   * every function must be TOTAL. It runs inside the notifier's 2s timer, and the notifier is
//     also the user-session app-kill loop - a URL parser that throws would take app-kill down
//     with it. So "never throws, for any input" is pinned by a corpus, not by hope (R12).
//   * where a reading was genuinely open, the one that blocks MORE wins (R1). Pinned here so a
//     later "tidy-up" cannot quietly reverse the direction.
//
// WIDEN-ONLY, the same property AppKillMatchTests.cs holds the app matcher to: the predicate is a
// case-insensitive ordinal SUBSTRING, and a token-exact matcher is forbidden because narrowing a
// blocking predicate is fail-open. Proved below as a superset relation over a brute-force grid.
//
// THE ONE DELIBERATE NARROWING is P58's exact-home token, and it is narrow by construction and by
// opt-in: only a pattern the author wrote WITH a trailing slash ("youtube.com/") behaves that way,
// and it exists precisely because substring matching cannot say "the home page only". Its blast
// radius is pinned pair-by-pair rather than asserted in prose.
//
// Pure string tests: no UIAutomation, no browser, no hosts/registry/SCM, no clock. The live UIA
// seam is S7's and is covered there.

namespace MonkMode.Tests;

public class UrlMatchTests
{
    // The pattern set the shipped shortform preset carries (P63): three path patterns plus the
    // one exact-home token. Used wherever a test wants "the real thing" rather than a fixture.
    private static readonly string[] Shipped =
    {
        "youtube.com/shorts", "instagram.com/reels", "facebook.com/reel", "youtube.com/",
    };

    // Everything a hostile omnibox, a malformed page, or a fuzzer could plausibly hand the
    // watcher. Reused by the totality, idempotence and no-nonsense-redirect properties.
    private static readonly string?[] Corpus =
    {
        null, "", " ", "\t\r\n", "/", "//", "///", ":", "::", ":/", ":80", "@", "@@", "?", "#",
        "?#", "#?", ".", "..", "...", "-", "%", "%%%", "%zz", "\\", "\\\\server\\share",
        "youtube.com", "youtube.com/", "youtube.com//", "youtube.com/watch", "www.", "www.//",
        "http://", "https://", "http:", "https:", "http://@", "http://:@", "http://user@",
        "http://:80", "http://youtube.com:/", "http://youtube.com:abc/x", "https://[::1]:8080/x",
        "about:blank", "about:", "data:", "data:text/html,<h1>x</h1>", "chrome://settings",
        "edge://settings/privacy", "brave://settings", "file:///c:/users/x.html", "file://",
        "view-source:https://youtube.com/", "mailto:x@y.com", "javascript:alert(1)",
        "c:\\users\\samrath", "1:2:3", "9999999999999:80", "localhost", "localhost:3000/app",
        "192.168.0.1:8080/x", "\u00fcnicode.de/caf\u00e9", "xn--nicode-4ya.de/x",
        "YOUTUBE.COM/SHORTS/ABC", "  https://WWW.YouTube.com/Watch?v=A#t=1  ",
        "https://user:p@ss@youtube.com/watch", new string('a', 4000) + ".com/x",
        "youtube.com/" + new string('b', 4000), "\0", "a\0b.com/x", "\u202eevil.com/x",
        // Whitespace that is INTERIOR on the way in and becomes an EDGE once the userinfo, the
        // port, the "www." or the query has been stripped. These broke idempotence and drove a
        // "https:// b.com/" redirect target before the authority and path were re-trimmed.
        "user@ b.com/x", "b.com/x ?q", "www. b.com/x", " b.com /x ", "user@ b.com:80 /x",
        "b.com /x#f", "b .com/x", "http://user@\tb.com/x",
    };

    private static string N(string? raw) => mm_notify.UrlWatch.NormalizeUrlForMatch(raw!);
    private static bool M(string? url, params string?[] patterns)
        => mm_notify.UrlWatch.UrlMatchesPatterns(url!, patterns!);
    private static string R(string? url, params string?[] patterns)
        => mm_notify.UrlWatch.RedirectTargetFor(url!, patterns!);

    // =================================================================================
    // P56 - NormalizeUrlForMatch: "null/blank => ''; trim; lower-case; a non-http(s) scheme
    // => ''; strip scheme, userinfo@, :port, a leading www.; drop ?query/#fragment; append
    // '/' when no path remains. IDN left alone."
    // =================================================================================

    [Theory]
    // -- the ordinary shapes, including the scheme-less one the unfocused Chromium omnibox
    //    actually reports (the 18/08/2026 omnibox-facts guide). Scheme-less is FIRST-CLASS.
    [InlineData("youtube.com", "youtube.com/")]                       // no path => "/" appended
    [InlineData("youtube.com/", "youtube.com/")]
    [InlineData("youtube.com/watch", "youtube.com/watch")]            // path preserved verbatim
    [InlineData("youtube.com/shorts/abc123", "youtube.com/shorts/abc123")]
    [InlineData("https://www.youtube.com/watch", "youtube.com/watch")]
    [InlineData("http://youtube.com/watch", "youtube.com/watch")]
    // -- case: lower-cased invariantly, so a pattern and a URL that differ only in case agree.
    [InlineData("HTTPS://WWW.YouTube.COM/Watch", "youtube.com/watch")]
    [InlineData("YOUTUBE.COM", "youtube.com/")]
    // -- whitespace: trimmed before anything else.
    [InlineData("   https://www.youtube.com/watch   ", "youtube.com/watch")]
    // -- www. stripped; every OTHER subdomain kept (m. is load-bearing for P59's redirect).
    [InlineData("www.youtube.com", "youtube.com/")]
    [InlineData("m.youtube.com/shorts/x", "m.youtube.com/shorts/x")]
    [InlineData("music.youtube.com/", "music.youtube.com/")]
    [InlineData("www.www.youtube.com/", "www.youtube.com/")]          // a LEADING www. only
    // -- ports, including the empty one; the digits test is what keeps "youtube.com:8080" from
    //    being read as the scheme "youtube.com:".
    [InlineData("http://youtube.com:8080/watch", "youtube.com/watch")]
    [InlineData("youtube.com:443/", "youtube.com/")]
    [InlineData("youtube.com:/x", "youtube.com/x")]
    // A malformed port is dropped with the colon rather than kept: keeping it would leave a
    // token that a second normalisation pass reads as the scheme "youtube.com:" and discards,
    // and an unmatchable host is the fail-open direction.
    [InlineData("http://youtube.com:abc/watch", "youtube.com/watch")]
    [InlineData("localhost:3000/app", "localhost/app")]
    [InlineData("192.168.0.1:8080/x", "192.168.0.1/x")]
    // -- userinfo: up to and including the LAST '@' in the authority.
    [InlineData("https://user@youtube.com/watch", "youtube.com/watch")]
    [InlineData("https://user:pass@youtube.com/watch", "youtube.com/watch")]
    [InlineData("https://user:p@ss@youtube.com/watch", "youtube.com/watch")]
    [InlineData("https://user:pass@youtube.com:8443/watch", "youtube.com/watch")]
    // -- query and fragment, and everything after them, dropped.
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch#t=30", "youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch?v=a#t=30", "youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch#t=30?v=a", "youtube.com/watch")]  // '#' first
    [InlineData("youtube.com?u=a@b:1", "youtube.com/")]   // '@'/':' in a QUERY never eat the host
    [InlineData("youtube.com#a@b", "youtube.com/")]
    // -- whitespace that only becomes an EDGE once a strip has run. The opening Trim cannot see
    //    it, so the authority and the path are re-trimmed; without that the token kept a leading
    //    space (breaking idempotence) and RedirectTargetFor emitted "https:// b.com/".
    [InlineData("user@ b.com/x", "b.com/x")]
    [InlineData("b.com/x ?q", "b.com/x")]
    [InlineData("www. b.com/x", "b.com/x")]
    [InlineData(" b.com /x ", "b.com/x")]
    [InlineData("user@ b.com:80 /x", "b.com/x")]
    [InlineData("http://user@\tb.com/x", "b.com/x")]
    // ...but INTERIOR whitespace is left alone: it is not recoverable, and a host that matches
    // nothing is the fail-soft outcome.
    [InlineData("b .com/x", "b .com/x")]
    // -- non-http(s) schemes: never a hit, whatever they carry.
    [InlineData("about:blank", "")]
    [InlineData("about:", "")]
    [InlineData("data:text/html,<h1>hi</h1>", "")]
    [InlineData("chrome://settings", "")]
    [InlineData("edge://settings/privacy", "")]
    [InlineData("brave://settings", "")]
    [InlineData("file:///c:/users/samrath/x.html", "")]
    [InlineData("view-source:https://www.youtube.com/watch", "")]
    [InlineData("mailto:samrath@example.com", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("c:\\users\\samrath", "")]                // a bare drive letter parses as scheme "c"
    // -- http(s) WITHOUT the authority marker, and protocol-relative: still http, still parsed.
    [InlineData("http:youtube.com/watch", "youtube.com/watch")]
    [InlineData("//youtube.com/watch", "youtube.com/watch")]
    // -- degenerate: no host left after stripping => "" (never a hit). See the hostless note on
    //    RedirectTargetFor - a hostless match would emit "https:///".
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("https://", "")]
    [InlineData("http://www./", "")]
    [InlineData("/watch?v=a", "")]
    [InlineData("//", "")]
    [InlineData("?v=a", "")]
    // -- IDN: left EXACTLY as it arrives, no IdnMapping (it throws on malformed labels, and this
    //    function may not throw). A unicode host therefore never matches an ASCII pattern -
    //    fail-soft, with the hosts-level block doing the real work.
    [InlineData("https://\u00FCnicode.de/Caf\u00C9", "\u00fcnicode.de/caf\u00e9")]
    [InlineData("XN--NICODE-4YA.DE/X", "xn--nicode-4ya.de/x")]   // punycode is plain ASCII: kept
    public void NormalizeUrlForMatch_ReducesToTheComparisonToken(string raw, string expected)
        => Assert.Equal(expected, N(raw));

    [Fact]
    public void NormalizeUrlForMatch_NothingAndBlank_AreEmpty_NotAThrow()
    {
        // The hook can hand back Nothing (no ValuePattern, no focus, a torn-down window). That is
        // an ordinary answer, not an error path - it is what the watcher sees most ticks.
        Assert.Equal("", N(null));
        Assert.Equal("", N(""));
        Assert.Equal("", N("\t\r\n "));
    }

    [Fact]
    public void NormalizeUrlForMatch_IsTotal_NoInputCanMakeItThrow()
    {
        // THE property the verifier attacks first. A throw here lands inside the notifier's 2s
        // timer, and the notifier also runs user-session app-kill: a URL parser must never be
        // able to stop apps being killed.
        foreach (var raw in Corpus)
        {
            var ex = Record.Exception(() => mm_notify.UrlWatch.NormalizeUrlForMatch(raw!));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void NormalizeUrlForMatch_EveryNonEmptyResult_HasAHostAndAPath()
    {
        // The output shape is a contract, not an accident: RedirectTargetFor splits on the first
        // '/' to build "https://<host>/", so a result with no host or no '/' would produce a
        // nonsense navigation. Either "" (never a hit) or "<host>/<path>" - nothing in between.
        foreach (var raw in Corpus)
        {
            var n = N(raw);
            if (n.Length == 0) continue;
            Assert.Contains("/", n, StringComparison.Ordinal);
            Assert.NotEqual(0, n.IndexOf('/'));                       // host is non-empty
            Assert.Equal(n, n.ToLowerInvariant());                    // lower-cased
            Assert.DoesNotContain("?", n, StringComparison.Ordinal);  // query gone
            Assert.DoesNotContain("#", n, StringComparison.Ordinal);  // fragment gone
            Assert.Equal(n.Trim(), n);                                // trimmed
        }
    }

    [Fact]
    public void NormalizeUrlForMatch_IsIdempotent_SoCallersNeedNotTrackWhoNormalisedWhat()
    {
        // UrlMatchesPatterns and RedirectTargetFor both normalise defensively; patterns go through
        // the SAME function as URLs (P56). Idempotence is what makes that safe to do twice.
        foreach (var raw in Corpus)
        {
            var once = N(raw);
            Assert.Equal(once, N(once));
        }
    }

    [Fact]
    public void NormalizeUrlForMatch_PatternsAndUrlsGoThroughTheSameFunction()
    {
        // P56's closing clause: "https://YouTube.com/Shorts" and "youtube.com/shorts" are the
        // SAME token, which is why a pattern file may be written in any style.
        Assert.Equal(N("youtube.com/shorts"), N("https://YouTube.com/Shorts"));
        Assert.Equal(N("youtube.com/shorts"), N("HTTP://WWW.youtube.com:80/shorts?a=1#b"));
    }

    [Theory]
    // A trailing-dot ("fully qualified") host is left alone and therefore never matches an
    // ordinary pattern. DOCUMENTED RESIDUAL, not an oversight: the hosts file misses the same
    // trick, so closing it here would buy nothing, and stripping a trailing dot is a normalisation
    // decision nobody has pinned. Recorded so a future reader sees it was considered.
    [InlineData("youtube.com./", "youtube.com./")]
    [InlineData("www.youtube.com./shorts/x", "youtube.com./shorts/x")]
    public void NormalizeUrlForMatch_TrailingDotHost_IsLeftAlone_KnownResidual(string raw, string expected)
    {
        Assert.Equal(expected, N(raw));
        Assert.False(M(raw, "youtube.com/shorts"));   // ...and so it misses. Deliberate.
    }

    // =================================================================================
    // P58 - the exact-home token
    // =================================================================================

    [Theory]
    // A trailing slash and no further path: home only.
    [InlineData("youtube.com/", true)]
    [InlineData("https://www.youtube.com/", true)]
    [InlineData("  YouTube.com/  ", true)]
    [InlineData("m.youtube.com/", true)]
    // No trailing slash => the WHOLE HOST, as a substring pattern. This is the R1 call: P56
    // appends "/" when no path remains, so "youtube.com" and "youtube.com/" normalise alike and
    // only the RAW trailing slash still carries the author's intent. Reading it after
    // normalisation would demote a bare-host pattern to home-only - a narrowing, fail-open.
    [InlineData("youtube.com", false)]
    [InlineData("https://www.youtube.com", false)]
    // A path present => an ordinary substring pattern, trailing slash or not.
    [InlineData("youtube.com/shorts", false)]
    [InlineData("youtube.com/shorts/", false)]
    [InlineData("youtube.com/feed/subscriptions", false)]
    // A query or fragment disqualifies it, however the pattern is spelled: "youtube.com/?x/" is
    // only "<host>/" AFTER normalisation throws the query away, and admitting it would demote a
    // junk pattern to home-only - a narrowing, i.e. fail-open. It stays a substring pattern and
    // blocks the whole host instead.
    [InlineData("youtube.com/?x/", false)]
    [InlineData("youtube.com/#/", false)]
    [InlineData("youtube.com/?", false)]
    [InlineData("https://www.youtube.com/#fragment/", false)]
    // Nothing that normalises to "" can be a home token.
    [InlineData("chrome://settings/", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExactHomeToken_IsDecidedByTheRawTrailingSlash(string? rawPattern, bool expected)
        => Assert.Equal(expected, mm_notify.UrlWatch.IsExactHomeToken(rawPattern!));

    [Fact]
    public void ExactHomeToken_NeverMatchesASubPath_NotOnceInTheWholePathCorpus()
    {
        // THE question the verifier asks of P58. "youtube.com/" is a substring of every URL on
        // the host, so a plain substring match would blanket the site; equality is what keeps it
        // to the home page. Brute-forced rather than sampled.
        string[] subPaths =
        {
            "youtube.com/watch", "youtube.com/watch?v=abc", "youtube.com/results?search_query=x",
            "youtube.com/@channel", "youtube.com/feed/subscriptions", "youtube.com/shorts/abc",
            "youtube.com/c/somebody", "youtube.com/playlist?list=1", "https://www.youtube.com/tv",
            "m.youtube.com/watch", "youtube.com//", "youtube.com/x",
        };
        foreach (var url in subPaths)
            Assert.False(M(url, "youtube.com/"), $"the home token matched a sub-path: {url}");

        // ...and it DOES match the home page itself, however the browser spells it.
        foreach (var home in new[] { "youtube.com", "youtube.com/", "https://youtube.com/",
                                     "https://www.youtube.com/", "www.youtube.com",
                                     "HTTPS://WWW.YOUTUBE.COM/", "youtube.com/?gl=GB",
                                     "youtube.com:443/", "youtube.com/#feed" })
            Assert.True(M(home, "youtube.com/"), $"the home token missed the home page: {home}");
    }

    [Fact]
    public void BareHostPattern_BlocksTheWholeHost_TheFailDirectionForAnUnslashedPattern()
    {
        // The other half of the same decision: a user who writes "youtube.com" means the site.
        foreach (var url in new[] { "youtube.com/", "youtube.com/watch", "youtube.com/shorts/x",
                                    "m.youtube.com/watch", "https://www.youtube.com/@channel" })
            Assert.True(M(url, "youtube.com"), $"a bare-host pattern missed: {url}");
    }

    [Fact]
    public void AQueryBearingPattern_WidensToTheWholeHost_RatherThanNarrowingToHome()
    {
        // The other side of the P58 refusal above, as behaviour. "youtube.com/?x/" normalises to
        // "youtube.com/" either way; what changes is WHICH branch it goes down. As a substring
        // pattern it covers the host; as a home token it would have covered one page - and
        // silently discarding a user's pattern down to one page is the fail-open direction.
        foreach (var junk in new[] { "youtube.com/?x/", "youtube.com/#/", "https://www.youtube.com/#f/" })
        {
            Assert.False(mm_notify.UrlWatch.IsExactHomeToken(junk));
            Assert.True(M("youtube.com/watch?v=abc", junk), $"pattern narrowed to home: {junk}");
            Assert.True(M("m.youtube.com/shorts/x", junk));
            Assert.True(M("youtube.com/", junk));
        }
        // ...while the literal token still means home only.
        Assert.False(M("youtube.com/watch?v=abc", "youtube.com/"));
    }

    // =================================================================================
    // P57 - UrlMatchesPatterns: case-insensitive ordinal SUBSTRING, widen-only
    // =================================================================================

    [Theory]
    // -- against the shipped shortform set (P63): the four patterns a real block will carry.
    [InlineData("https://www.youtube.com/shorts/abc123", true)]
    [InlineData("m.youtube.com/shorts/abc123", true)]
    [InlineData("YOUTUBE.COM/SHORTS/ABC", true)]                 // case-insensitive
    [InlineData("https://www.youtube.com/shorts", true)]
    [InlineData("youtube.com/", true)]                           // the home token
    [InlineData("https://www.youtube.com/", true)]
    [InlineData("youtube.com/watch?v=abc", false)]               // ...but not a video
    [InlineData("youtube.com/results?search_query=cats", false)] // ...nor a search
    [InlineData("youtube.com/@channel", false)]                  // ...nor a channel
    [InlineData("youtube.com/feed/subscriptions", false)]        // ...nor the redirect target
    [InlineData("https://www.instagram.com/reels/xyz/", true)]
    [InlineData("instagram.com/", false)]                        // no home token for instagram
    [InlineData("instagram.com/p/abc", false)]
    [InlineData("facebook.com/reel/123", true)]
    [InlineData("m.facebook.com/reel/123", true)]                // any subdomain, via substring
    [InlineData("tiktok.com/", false)]                           // not in this set at all
    [InlineData("about:blank", false)]                           // non-http schemes never hit
    [InlineData("chrome://settings", false)]
    [InlineData("data:text/html,youtube.com/shorts", false)]     // ...even carrying the token
    [InlineData("file:///c:/youtube.com/shorts/x.html", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UrlMatchesPatterns_AgainstTheShippedShortformSet(string? url, bool expected)
        => Assert.Equal(expected, mm_notify.UrlWatch.UrlMatchesPatterns(url!, Shipped));

    [Theory]
    // A pattern that is itself a full URL - the shape a user gets by copying the address bar.
    // It normalises like any other, so its query and scheme simply fall away.
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube.com/watch?v=other", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://m.youtube.com/watch", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube.com/shorts/x", false)]
    // A pattern that is a full URL with a fragment and a port, matched against the plain form.
    [InlineData("HTTP://WWW.Reddit.com:80/r/all#top", "reddit.com/r/all/comments/1", true)]
    // A pattern carrying a trailing slash AND a path stays an ordinary substring pattern.
    [InlineData("youtube.com/shorts/", "youtube.com/shorts/abc", true)]
    [InlineData("youtube.com/shorts/", "youtube.com/shorts", false)]  // the slash is real text
    public void UrlMatchesPatterns_PatternMayItselfBeAFullUrl(string pattern, string url, bool expected)
        => Assert.Equal(expected, M(url, pattern));

    [Fact]
    public void UrlMatchesPatterns_EmptyOrAbsentInputs_AreFalse_AndNeverThrow()
    {
        // P57: "empty url or empty pattern set => False". The watcher spends most of its life
        // here - no block armed, no patterns, or a browser on a blank tab.
        Assert.False(M("youtube.com/shorts/x"));                     // empty set
        Assert.False(mm_notify.UrlWatch.UrlMatchesPatterns("youtube.com/shorts/x", null!));
        Assert.False(M("youtube.com/shorts/x", new string?[] { null, "", "  ", "about:blank" }));
        Assert.False(M(null, Shipped));
        Assert.False(M("", Shipped));
        Assert.False(M("   ", Shipped));
        Assert.False(mm_notify.UrlWatch.UrlMatchesPatterns(null!, null!));
        // A Nothing element mid-list is skipped, not fatal - the real ones still fire.
        Assert.True(M("youtube.com/shorts/x", null, "youtube.com/shorts", null));
    }

    [Fact]
    public void UrlMatchesPatterns_IsTotal_NoUrlPatternPairCanMakeItThrow()
    {
        foreach (var url in Corpus)
        {
            foreach (var pattern in Corpus)
            {
                var ex = Record.Exception(() =>
                    mm_notify.UrlWatch.UrlMatchesPatterns(url!, new[] { pattern! }));
                Assert.Null(ex);
            }
        }
    }

    // =================================================================================
    // WIDEN-ONLY: the substring predicate is a superset of any token-exact variant
    // =================================================================================

    // The token-exact matcher this layer is FORBIDDEN to become: the normalised URL must equal
    // the normalised pattern. It is the tidiest thing to write and it is the fail-open thing to
    // write - it misses every deeper path, every subdomain and every query variant.
    private static bool TokenExactMatch(string? url, string? pattern)
    {
        var u = N(url);
        var p = N(pattern);
        return u.Length > 0 && p.Length > 0 && string.Equals(u, p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubstringPredicate_MatchesASupersetOfTheTokenExactVariant()
    {
        // THE safety property, and it holds for BOTH branches: the substring branch contains
        // equality trivially, and the P58 home branch IS equality. So no pattern set can be
        // written for which the shipped predicate matches less than a token-exact one would.
        // If this ever fails, the layer has become capable of under-blocking.
        string[] urls =
        {
            "youtube.com", "youtube.com/", "youtube.com/watch", "youtube.com/watch?v=abc",
            "https://www.youtube.com/shorts/abc", "m.youtube.com/shorts/abc",
            "instagram.com/reels/xyz", "www.instagram.com/", "facebook.com/reel/1",
            "reddit.com/r/all", "tiktok.com/@user/video/1", "about:blank", "", "localhost:3000/x",
        };
        string[] patterns =
        {
            "youtube.com", "youtube.com/", "youtube.com/watch", "youtube.com/shorts",
            "https://www.youtube.com/shorts/abc", "instagram.com/reels", "instagram.com/",
            "facebook.com/reel", "reddit.com", "tiktok.com/", "", "youtube.com/watch?v=abc",
        };

        var strictlyWider = 0;
        foreach (var url in urls)
        {
            foreach (var pattern in patterns)
            {
                var exact = TokenExactMatch(url, pattern);
                var shipped = M(url, pattern);
                if (exact)
                    Assert.True(shipped,
                        $"the shipped predicate LOST a match token-exact made: url='{url}' pattern='{pattern}'");
                if (shipped && !exact) strictlyWider++;
            }
        }
        // ...and it is strictly wider, not merely equal - the widening is the point.
        Assert.True(strictlyWider > 0);
    }

    [Fact]
    public void SubstringPredicate_StrictlyAddsTheMatchesTokenExactWouldMiss()
    {
        // Named, so the regression reads as behaviour rather than as a counter. Each of these is
        // a real navigation a token-exact matcher would wave through.
        foreach (var (url, pattern) in new[]
        {
            ("youtube.com/shorts/abc123", "youtube.com/shorts"),          // a deeper path
            ("m.youtube.com/shorts/abc", "youtube.com/shorts"),           // a mobile subdomain
            ("youtube.com/shorts/abc?feature=share", "youtube.com/shorts"),// a tracking query
            ("YouTube.com/Shorts/ABC", "youtube.com/shorts"),             // different casing
            ("instagram.com/reels/audio/9", "instagram.com/reels"),       // a nested path
        })
        {
            Assert.False(TokenExactMatch(url, pattern));
            Assert.True(M(url, pattern), $"substring failed to widen: url='{url}' pattern='{pattern}'");
        }
    }

    [Fact]
    public void TheOnlyPlaceTheShippedPredicateIsNarrowerThanRawSubstring_IsTheHomeToken()
    {
        // Honesty pin. P58 narrows relative to a RAW substring search, on purpose and only for a
        // pattern the author opted into by writing the trailing slash. This asserts the blast
        // radius exactly: for the home token the two disagree on sub-paths, and for every other
        // pattern shape they agree everywhere.
        Assert.True(N("youtube.com/watch").Contains(N("youtube.com/"), StringComparison.Ordinal));
        Assert.False(M("youtube.com/watch", "youtube.com/"));   // the deliberate difference

        string[] urls = { "youtube.com/", "youtube.com/watch", "youtube.com/shorts/x",
                          "m.youtube.com/watch", "instagram.com/reels/1", "tiktok.com/" };
        string[] nonHomePatterns = { "youtube.com", "youtube.com/watch", "youtube.com/shorts",
                                     "instagram.com/reels", "tiktok.com" };
        foreach (var url in urls)
        {
            foreach (var pattern in nonHomePatterns)
            {
                var rawSubstring = N(url).Length > 0 && N(pattern).Length > 0
                                   && N(url).Contains(N(pattern), StringComparison.Ordinal);
                Assert.Equal(rawSubstring, M(url, pattern));
            }
        }
    }

    // =================================================================================
    // P59 - RedirectTargetFor
    // =================================================================================

    [Theory]
    // youtube.com and m.youtube.com go to SUBSCRIPTIONS (decision 12), not to the YT home feed -
    // which is also what keeps the shipped set from looping, since the home feed is blocked.
    [InlineData("https://www.youtube.com/shorts/abc", "https://www.youtube.com/feed/subscriptions")]
    [InlineData("m.youtube.com/shorts/abc", "https://www.youtube.com/feed/subscriptions")]
    [InlineData("youtube.com/", "https://www.youtube.com/feed/subscriptions")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/", "https://www.youtube.com/feed/subscriptions")]
    // every other host: its own home page, rebuilt from the URL's host (www. already stripped).
    [InlineData("https://www.instagram.com/reels/xyz", "https://instagram.com/")]
    [InlineData("m.facebook.com/reel/1", "https://m.facebook.com/")]
    // no match => "" => the caller does NOTHING. Never a default page.
    [InlineData("youtube.com/watch?v=abc", "")]
    [InlineData("tiktok.com/", "")]
    [InlineData("about:blank", "")]
    [InlineData("chrome://settings", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void RedirectTargetFor_SendsTheBrowserToTheHostsHome_ExceptYouTube(string? url, string expected)
        => Assert.Equal(expected, mm_notify.UrlWatch.RedirectTargetFor(url!, Shipped));

    [Fact]
    public void RedirectTargetFor_NoPatterns_IsAlwaysTheDoNothingAnswer()
    {
        Assert.Equal("", R("youtube.com/shorts/x"));
        Assert.Equal("", mm_notify.UrlWatch.RedirectTargetFor("youtube.com/shorts/x", null!));
        Assert.Equal("", R("youtube.com/shorts/x", "", null, "   "));
    }

    [Fact]
    public void RedirectTargetFor_TheShippedSetNeverRedirectsIntoABlockedPage_NoLoop()
    {
        // THE loop question. For every URL the shipped shortform set blocks, the place we send
        // the browser must not itself be blocked - otherwise the watcher re-fires every cooldown
        // and the address bar is unusable.
        string[] blockedUrls =
        {
            "youtube.com/", "https://www.youtube.com/", "youtube.com/shorts/abc",
            "m.youtube.com/shorts/abc", "www.instagram.com/reels/xyz", "instagram.com/reels",
            "facebook.com/reel/123", "m.facebook.com/reel/1",
        };
        foreach (var url in blockedUrls)
        {
            var target = mm_notify.UrlWatch.RedirectTargetFor(url, Shipped);
            Assert.NotEqual("", target);
            Assert.False(mm_notify.UrlWatch.UrlMatchesPatterns(target, Shipped),
                $"redirect loop: '{url}' -> '{target}', which is itself blocked");
        }
    }

    [Fact]
    public void RedirectTargetFor_AHomeTokenOnANonYouTubeHost_DoesLoop_DocumentedAndDeliberate()
    {
        // The general case P59 does not repair: pair a home token with a path pattern on the SAME
        // non-YouTube host and the target is itself a hit. Left alone on purpose - "redirect
        // somewhere unblocked" has no defined answer, and inventing one would be a way to fail
        // open; a site that becomes unusable is the block getting STRONGER, and the 5s cooldown
        // bounds the churn to one action per cooldown. Pinned so it is a known shape, not a
        // surprise, and so a future preset author sees the cost of shipping a bare home token.
        var patterns = new[] { "instagram.com/", "instagram.com/reels" };
        var target = R("instagram.com/reels/xyz", patterns);
        Assert.Equal("https://instagram.com/", target);
        Assert.True(M(target, patterns));    // ...and round it goes. The YT exception exists
        Assert.Equal("https://www.youtube.com/feed/subscriptions",  // precisely to avoid this for
            R("youtube.com/", "youtube.com/", "youtube.com/shorts"));  // the one shipped token.
    }

    [Fact]
    public void RedirectTargetFor_AWholeHostYouTubePattern_MakesTheTargetAFixedPoint_DocumentedResidual()
    {
        // The YT exception's own loop sub-case, on the same accepted-residual terms as the
        // general one above: "youtube.com" without a trailing slash blocks every YT path, the
        // subscriptions feed included, so the target is a FIXED POINT - it re-fires once per
        // cooldown and YouTube becomes unusable. Which is precisely what blocking the whole of
        // YouTube means, so it is pinned rather than repaired; the "youtube.com/" home token is
        // the spelling that leaves the feed reachable.
        var whole = new[] { "youtube.com" };
        var target = R("youtube.com/shorts/x", whole);
        Assert.Equal(mm_notify.UrlWatch.YouTubeRedirect, target);
        Assert.True(M(target, whole));                       // the fixed point
        Assert.Equal(target, R(target, whole));              // ...and it maps to itself
        // The shipped spelling does not do this: the home token leaves the feed alone.
        Assert.False(M(mm_notify.UrlWatch.YouTubeRedirect, "youtube.com/"));
        Assert.Equal("", R(mm_notify.UrlWatch.YouTubeRedirect, Shipped));
    }

    [Fact]
    public void RedirectTargetFor_NeverEmitsAHostlessOrNonHttpsTarget()
    {
        // A hostless URL normalises to "" rather than to "/", exactly so no input can drive the
        // target to "https:///". Brute-forced over the corpus against a deliberately broad
        // pattern set, so a hit is easy to provoke.
        var greedy = new[] { "/", ".", "com", "youtube.com", "a", "localhost", "1" };
        foreach (var url in Corpus)
        {
            var target = mm_notify.UrlWatch.RedirectTargetFor(url!, greedy);
            if (target.Length == 0) continue;
            Assert.StartsWith("https://", target, StringComparison.Ordinal);
            Assert.NotEqual("https:///", target);
            Assert.True(target.Length > "https://".Length + 1);
            // No whitespace where the host is spliced in: "https:// b.com/" was reachable
            // before the authority was re-trimmed, and it is not a navigable URL.
            Assert.False(target.StartsWith("https:// ", StringComparison.Ordinal));
            Assert.False(target.EndsWith(" /", StringComparison.Ordinal));
            // Either a rebuilt "https://<host>/" or the one fixed YouTube destination - never a
            // third shape, and never a bare scheme with the host missing.
            if (target != mm_notify.UrlWatch.YouTubeRedirect)
                Assert.EndsWith("/", target, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RedirectTargetFor_IsTotal_AndAgreesWithUrlMatchesPatterns()
    {
        // The two must never disagree: a non-empty target means "act", and acting without a hit
        // would be the watcher typing into the omnibox of a site nobody blocked.
        foreach (var url in Corpus)
        {
            foreach (var pattern in new[] { "youtube.com/", "youtube.com/shorts", "", "/", null })
            {
                var patterns = new[] { pattern! };
                var ex = Record.Exception(() =>
                {
                    var target = mm_notify.UrlWatch.RedirectTargetFor(url!, patterns);
                    Assert.Equal(mm_notify.UrlWatch.UrlMatchesPatterns(url!, patterns), target.Length > 0);
                });
                Assert.Null(ex);
            }
        }
    }

    [Fact]
    public void HostOfNormalized_IsTheTokenUpToTheFirstSlash()
    {
        Assert.Equal("youtube.com", mm_notify.UrlWatch.HostOfNormalized("youtube.com/shorts/x"));
        Assert.Equal("m.youtube.com", mm_notify.UrlWatch.HostOfNormalized("m.youtube.com/"));
        Assert.Equal("", mm_notify.UrlWatch.HostOfNormalized(""));
        Assert.Equal("", mm_notify.UrlWatch.HostOfNormalized(null!));
        Assert.Equal("youtube.com", mm_notify.UrlWatch.HostOfNormalized("youtube.com"));
    }

    // =================================================================================
    // P60 - ShouldActOnHit
    // =================================================================================

    [Theory]
    // never acted: the first hit always acts, whatever the cooldown.
    [InlineData(0L, 1000L, 5000L, true)]
    [InlineData(0L, 0L, 5000L, true)]
    [InlineData(-5L, 1000L, 5000L, true)]
    [InlineData(long.MinValue, 0L, 5000L, true)]
    // inside the cooldown: suppressed. This is the case that stops a stream of SetValue+Enter
    // while the page loads and the omnibox still reads the old URL.
    [InlineData(1000L, 1000L, 5000L, false)]
    [InlineData(1000L, 3000L, 5000L, false)]
    [InlineData(1000L, 5999L, 5000L, false)]
    // exactly at, and beyond: acts. ">=", so a cooldown of exactly 5000ms elapses at 5000ms.
    [InlineData(1000L, 6000L, 5000L, true)]
    [InlineData(1000L, 6001L, 5000L, true)]
    [InlineData(1000L, 999999L, 5000L, true)]
    // backwards ticks: act. Nonsense in, blocking direction out (R1) - and no subtraction
    // happens on this path, so it cannot overflow either.
    [InlineData(1000L, 500L, 5000L, true)]
    [InlineData(long.MaxValue, long.MinValue, 5000L, true)]
    // no cooldown asked for: every hit acts.
    [InlineData(1000L, 1000L, 0L, true)]
    [InlineData(1000L, 1000L, -1L, true)]
    // the extremes, for overflow: the only subtraction runs with 0 < last <= now.
    [InlineData(1L, long.MaxValue, 5000L, true)]
    [InlineData(long.MaxValue, long.MaxValue, 5000L, false)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, false)]
    [InlineData(1L, long.MaxValue, long.MaxValue, false)]
    public void ShouldActOnHit_GatesOnTheElapsedTicks(long last, long now, long cooldown, bool expected)
        => Assert.Equal(expected, mm_notify.UrlWatch.ShouldActOnHit(last, now, cooldown));

    [Fact]
    public void ShouldActOnHit_IsTotal_AcrossTheTickExtremes()
    {
        long[] ticks = { long.MinValue, -1, 0, 1, 1000, int.MaxValue, long.MaxValue - 1, long.MaxValue };
        long[] cooldowns = { long.MinValue, -1, 0, 1, 5000, long.MaxValue };
        foreach (var last in ticks)
            foreach (var now in ticks)
                foreach (var cooldown in cooldowns)
                    Assert.Null(Record.Exception(() => mm_notify.UrlWatch.ShouldActOnHit(last, now, cooldown)));
    }

    [Fact]
    public void ShouldActOnHit_OneHitProducesOneActionAcrossTheWatchBeat()
    {
        // The behaviour the constant is FOR, walked at the notifier's real 2s cadence: a hit at
        // t=0 acts once, and the next two beats are suppressed because the omnibox still reads
        // the blocked URL while the redirect loads.
        const long cooldown = mm_notify.UrlWatch.RedirectCooldownMs;
        long last = 0;
        var actions = 0;
        for (long t = 0; t <= 6000; t += 2000)
        {
            if (mm_notify.UrlWatch.ShouldActOnHit(last, t, cooldown)) { actions++; last = t == 0 ? 1 : t; }
        }
        Assert.Equal(2, actions);   // t=0 and t=6000; t=2000 and t=4000 suppressed
    }

    [Fact]
    public void TheConstants_AreWhatThePinsSay()
    {
        Assert.Equal(5000, mm_notify.UrlWatch.RedirectCooldownMs);
        Assert.Equal("https://www.youtube.com/feed/subscriptions", mm_notify.UrlWatch.YouTubeRedirect);
        // The YT target must not be blocked by the token that sends traffic to it - the whole
        // reason decision 12 chose subscriptions over the home feed.
        Assert.False(M(mm_notify.UrlWatch.YouTubeRedirect, "youtube.com/"));
        Assert.False(M(mm_notify.UrlWatch.YouTubeRedirect, Shipped));
    }
}
