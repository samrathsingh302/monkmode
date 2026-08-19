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

// MonkMode.Tests - v1.1 A2: BRUTE-FORCE / PROPERTY fuzz over the F2 pure URL layer.
//
// UrlMatchTests.cs already pins the layer CASE BY CASE - a named input, a named expectation.
// This file pins the same four functions by PROPERTY instead, over generated corpora, because
// the failure mode the case-by-case file cannot catch is the input nobody thought to type. The
// contract at UrlWatch.vb:34-51 is that every function here is TOTAL (a throw would surface
// inside the notifier's 2s timer, and a dead notifier stops doing user-session app-kill - a URL
// parser must never be able to take the kill loop down with it), so "no input can make it
// throw" is itself an enforcement property, not hygiene.
//
// WHAT THIS PINS (each with the failure it prevents):
//   - TOTALITY over ~7,000 adversarial strings: no throw, no null, and every non-empty result
//     has a non-empty host, a path, no edge whitespace and no ASCII upper case. A hostless
//     token would make RedirectTargetFor emit "https:///"; an untrimmed one made it emit
//     "https:// b.com/" before the re-trims landed.
//   - IDEMPOTENCE over the same corpus. Callers normalise defensively without tracking who
//     normalised what; a non-idempotent shape means the pattern and the URL can disagree.
//   - the STRUCTURED product (scheme x userinfo x host x port x path x query = 2,880 URLs)
//     against an independently computed expectation, so the strip order is pinned as an
//     algebraic identity rather than as 60 hand-typed rows.
//   - MATCH == SUBSTRING for non-home patterns, over a full url x pattern cross product. This
//     is the widen-only axis: a matcher that is ever NARROWER than raw substring is fail-open
//     (AppKillMatchTests.cs's header argues the identical case for the process matcher).
//   - MONOTONICITY: adding a pattern can never turn a hit into a miss, and the boolean is
//     order-independent. Both would be silent under-blocks.
//   - the two KNOWN RESIDUALS from the 19/08/2026 bug-hunt are pinned as CURRENT behaviour,
//     named as residuals, with the finding id in the comment - F12 (trailing-dot FQDN) and
//     F13 (whitespace host in a redirect target). Pinning the desired behaviour would leave
//     the suite red and pinning them silently would hide them; naming them means the fix slice
//     has a test to invert.
//
// Fences honoured: strings only. Nothing here touches the filesystem, the registry, the SCM,
// a browser, a socket or any UIA type - the whole file runs against mm_notify.UrlWatch's pure
// half, which is exactly why that half was split out.

using System.Text;

namespace MonkMode.Tests;

public class UrlFuzzTests
{
    private static string N(string? raw) => mm_notify.UrlWatch.NormalizeUrlForMatch(raw!);
    private static bool M(string? url, params string?[] patterns)
        => mm_notify.UrlWatch.UrlMatchesPatterns(url!, patterns!);
    private static string Matched(string? url, params string?[] patterns)
        => mm_notify.UrlWatch.MatchedPatternFor(url!, patterns!);
    private static string R(string? url, params string?[] patterns)
        => mm_notify.UrlWatch.RedirectTargetFor(url!, patterns!);

    // ---------------------------------------------------------------------------------
    // the corpora
    // ---------------------------------------------------------------------------------

    /// <summary>~6,000 adversarial strings from a FIXED seed, so a failure is reproducible by
    /// re-running rather than "flaky". The pool is deliberately loaded with the characters the
    /// parser gives meaning to (scheme colon, authority terminators, the userinfo '@', the two
    /// stored separators) plus the ones that break naive string code: NUL, a right-to-left
    /// override, tabs, a wide CJK char, a combining accent.</summary>
    private static List<string> RandomCorpus()
    {
        const string pool = "abcXYZ019.:/@?#-+_ \t%[]\\|;,&=~^*!'\"<>{}\u00e9\u4e2d\u202e\u0000\u00a0";
        var rng = new Random(20260819);
        var corpus = new List<string>(6200);
        for (var i = 0; i < 6000; i++)
        {
            var len = rng.Next(0, 40);
            var sb = new StringBuilder(len);
            for (var c = 0; c < len; c++) sb.Append(pool[rng.Next(pool.Length)]);
            corpus.Add(sb.ToString());
        }
        // Hand-picked shapes a uniform sampler will practically never emit.
        corpus.AddRange(new[]
        {
            "", " ", "\t", "\u00a0", "/", "//", "///", ":", "::", "@", "?", "#",
            "http://", "https://", "http:", "https:", "http:/", "https:///",
            "http://@", "http://:@", "http://@@@youtube.com/x", "http://user@@@/x",
            "youtube.com:", "youtube.com::80/x", "youtube.com:80:90/x",
            "https://youtube.com:99999999999999999999/x",
            "https://" + new string('a', 3000) + ".com/" + new string('b', 3000),
            "https://" + new string('.', 500) + "/x",
            "https://youtube.com/" + new string('/', 500),
            "\u202ehttps://moc.ebutuoy/x", "https://youtube\u0000.com/x",
            "https://\u00fcnicode.de/caf\u00e9", "https://xn--nicode-4ya.de/x",
            "HTTPS://WWW.YOUTUBE.COM/SHORTS/ABC", "  \t https://www.youtube.com/watch \t ",
            "data:text/html;base64," + new string('Q', 2000),
            "about:blank", "chrome-extension://abcdefg/popup.html", "blob:https://youtube.com/uuid",
            "ws://youtube.com/x", "wss://youtube.com/x", "ftp://youtube.com/x",
            "intent://youtube.com/x#Intent;scheme=https;end",
            "view-source:view-source:https://youtube.com/x",
            "http://http://youtube.com/x", "https://https://x",
            "\\\\server\\share\\file", "C:\\Users\\samrath\\x.html",
        });
        return corpus;
    }

    /// <summary>Every combination of the six axes the normaliser strips, with the expected
    /// token computed INDEPENDENTLY from the pin's own words ("lower-case; strip the scheme,
    /// userinfo@, :port and a leading www.; drop ?query/#fragment; append '/' when no path
    /// remains"). 2,880 URLs.</summary>
    private static IEnumerable<(string Url, string Expected)> StructuredProduct()
    {
        var schemes = new[] { "", "http://", "https://", "HTTPS://", "http:", "//" };
        var hosts = new[] { "youtube.com", "M.YouTube.com", "www.instagram.com", "WWW.Reddit.COM",
                            "xn--nicode-4ya.de", "192.168.0.1", "localhost", "a-b.c.d.example" };
        var ports = new[] { "", ":80", ":8080", ":0", ":65535" };
        var paths = new[] { "", "/", "/watch", "/Shorts/ABC", "/a/b/c/", "/x.y-z_1" };
        var queries = new[] { "", "?v=1", "#frag", "?a=b#c" };

        foreach (var scheme in schemes)
        {
            // A userinfo containing a COLON is only unambiguous once a scheme has been
            // consumed: scheme-less "user:pass@host/x" reads "user" as the scheme by the RFC
            // grammar, which is a documented miss, not a bug (SchemeOf's header argues it).
            // So the colon-bearing userinfo rides only the scheme-bearing arm.
            var userinfos = scheme.Length > 0 && scheme != "//"
                ? new[] { "", "user@", "user:pass@", "u:p@ss@" }
                : new[] { "", "user@" };
            foreach (var userinfo in userinfos)
            foreach (var host in hosts)
            foreach (var port in ports)
            foreach (var path in paths)
            foreach (var query in queries)
            {
                // FINDING F60 (A2, P3): scheme-less + a port + NO path + a query/fragment
                // ("youtube.com:8080?v=1") normalises to "" instead of "youtube.com/", because
                // SchemeOf terminates the scheme tail at '/' only while AuthorityEnd also
                // terminates at '?' and '#'. Excluded here and pinned as current behaviour by
                // KnownResidual_F60 below - so the shape is recorded, not hidden, and the fix
                // slice deletes both the exclusion and the residual test.
                if (scheme.Length == 0 && userinfo.Length == 0 && port.Length > 0
                    && path.Length == 0 && query.Length > 0) continue;
                var expectedHost = host.ToLowerInvariant();
                if (expectedHost.StartsWith("www.", StringComparison.Ordinal)) expectedHost = expectedHost.Substring(4);
                var expectedPath = path.ToLowerInvariant();
                if (!expectedPath.StartsWith("/", StringComparison.Ordinal)) expectedPath = "/";
                yield return (scheme + userinfo + host + port + path + query, expectedHost + expectedPath);
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // 1. NormalizeUrlForMatch - the total-function properties
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Normalize_IsTotal_AndEveryResultIsAWellFormedToken()
    {
        // The four shape rules, asserted together so one pass over the corpus proves the lot:
        //   never null; "" or (host non-empty AND a '/' present); no edge whitespace (an
        //   untrimmed token rides into RedirectTargetFor as "https:// b.com/"); no ASCII upper
        //   case (comparison is ordinal, so a stray capital is a silent miss).
        foreach (var raw in RandomCorpus())
        {
            var token = N(raw);
            Assert.NotNull(token);
            if (token.Length == 0) continue;
            var slash = token.IndexOf('/');
            Assert.True(slash > 0, $"hostless token {Quote(token)} from {Quote(raw)}");
            Assert.Equal(token.Trim(), token);
            foreach (var c in token) Assert.False(c >= 'A' && c <= 'Z', $"upper case in {Quote(token)}");
        }
    }

    [Fact]
    public void Normalize_IsIdempotent_OverTheWholeFuzzCorpus()
    {
        // Patterns and URLs both go through this function, and callers re-normalise
        // defensively; a shape where Normalize(Normalize(x)) != Normalize(x) is a shape where
        // the two sides can disagree about the same string.
        foreach (var raw in RandomCorpus())
        {
            var once = N(raw);
            Assert.Equal(once, N(once));
        }
    }

    [Fact]
    public void Normalize_MatchesTheIndependentlyComputedToken_AcrossTheWholeStripProduct()
    {
        var checkedCount = 0;
        var mismatches = new List<string>();
        foreach (var (url, expected) in StructuredProduct())
        {
            var actual = N(url);
            if (actual != expected) mismatches.Add($"{Quote(url)} -> {Quote(actual)} (expected {Quote(expected)})");
            checkedCount++;
        }
        Assert.True(checkedCount >= 2000, $"the product collapsed to {checkedCount} cases");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(12)) + $"\n({mismatches.Count} total)");
    }

    [Fact]
    public void Normalize_APortNeverChangesTheToken_AcrossTheWholePortRange()
    {
        // A port is authority noise: "youtube.com:8080/shorts" is the same blocked page as
        // "youtube.com/shorts", and a pattern file never carries a port.
        for (var port = 0; port <= 65535; port += 7)
        {
            Assert.Equal("youtube.com/shorts", N("https://www.youtube.com:" + port + "/shorts"));
            Assert.Equal("youtube.com/shorts", N("youtube.com:" + port + "/shorts"));
        }
    }

    [Fact]
    public void Normalize_ArbitraryUserinfo_NeverSurvivesIntoTheToken()
    {
        // RFC 3986 says the userinfo runs to the LAST '@' of the authority, so a password
        // containing '@' must not leave a fragment of itself behind as the host.
        var userinfos = new[] { "a@", "a:b@", "a:b@c@", "@@@", ":@", "a%40b@", "\u00e9@", "a b@" };
        foreach (var u in userinfos)
        {
            Assert.Equal("youtube.com/shorts", N("https://" + u + "youtube.com/shorts"));
            Assert.Equal("youtube.com/shorts", N("https://" + u + "youtube.com:443/shorts"));
        }
    }

    [Fact]
    public void Normalize_EveryNonHttpScheme_IsNeverAHit()
    {
        // The whole point of the scheme gate: a token that merely CONTAINS a blocked URL
        // ("data:text/html,youtube.com/shorts") is not a navigation to it.
        var schemes = new[] { "about", "data", "chrome", "edge", "brave", "file", "view-source",
                              "mailto", "javascript", "ftp", "ws", "wss", "blob", "intent",
                              "chrome-extension", "moz-extension", "res", "vbscript", "tel",
                              "sms", "magnet", "steam", "ms-settings", "x" };
        foreach (var s in schemes)
        {
            Assert.Equal("", N(s + ":youtube.com/shorts"));
            Assert.Equal("", N(s + "://youtube.com/shorts"));
            Assert.Equal("", N(s.ToUpperInvariant() + "://www.youtube.com/shorts"));
            Assert.False(M(s + "://www.youtube.com/shorts", "youtube.com/shorts"));
        }
    }

    [Fact]
    public void Normalize_AsciiCaseIsIrrelevant_ForEveryCasePermutationOfAHost()
    {
        // 2^11 permutations of "youtube.com" (plus the path), because ordinal comparison means
        // a single missed ToLower is a silent miss on exactly one site.
        const string host = "youtube.com";
        for (var mask = 0; mask < (1 << host.Length); mask++)
        {
            var sb = new StringBuilder(host.Length);
            for (var i = 0; i < host.Length; i++)
                sb.Append((mask & (1 << i)) != 0 ? char.ToUpperInvariant(host[i]) : host[i]);
            var permuted = sb.ToString();
            Assert.Equal("youtube.com/shorts", N("https://www." + permuted + "/SHORTS"));
            Assert.True(M(permuted + "/Shorts/x", "youtube.com/shorts"));
        }
    }

    [Fact]
    public void Normalize_A200CharPattern_IsCarriedWhole_AtTheP55Ceiling()
    {
        // P55 caps a pattern at MaxUrlPatternChars; a normaliser that truncated or choked at
        // the ceiling would make the longest legal pattern silently inert.
        var longPath = "/" + new string('p', MonkMode.Blocker.MaxUrlPatternChars - "youtube.com/".Length);
        var pattern = "youtube.com" + longPath;
        Assert.Equal(MonkMode.Blocker.MaxUrlPatternChars, pattern.Length);
        Assert.Equal(pattern, N(pattern));
        Assert.True(M("https://www.youtube.com" + longPath + "/deeper?x=1", pattern));
        Assert.False(M("https://www.youtube.com" + longPath.Substring(0, longPath.Length - 1), pattern));
        // ...and the CLI accepts exactly that length, so the pair is coherent end to end.
        string packed = "", err = "";
        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns(pattern, ref packed, ref err), err);
    }

    // ---------------------------------------------------------------------------------
    // 2. UrlMatchesPatterns - the widen-only algebra
    // ---------------------------------------------------------------------------------

    private static readonly string[] FuzzUrls =
    {
        "https://www.youtube.com/shorts/abc", "youtube.com/watch?v=1", "m.youtube.com/",
        "https://instagram.com/reels/xyz/", "instagram.com/p/1", "facebook.com/reel/9",
        "reddit.com/r/all", "tiktok.com/@user/video/1", "x.com/home", "example.org/",
        "about:blank", "data:text/html,youtube.com/shorts", "", "   ",
        "https://user:pass@www.YouTube.com:443/Shorts/ABC?x=1#y",
    };

    private static readonly string[] FuzzPatterns =
    {
        "youtube.com/shorts", "youtube.com/", "youtube.com", "instagram.com/reels",
        "facebook.com/reel", "reddit.com", "tiktok.com", "/shorts", "shorts", "",
        "   ", "about:blank", "https://www.youtube.com/shorts", "EXAMPLE.ORG",
    };

    [Fact]
    public void Match_IsExactlyTheSubstringRelation_ForEveryNonHomePattern()
    {
        // THE widen-only pin, as an identity rather than as examples: for a pattern that is
        // not the P58 home token, a hit is precisely "the normalised pattern occurs inside the
        // normalised url". Anything narrower is fail-open; anything wider would match tokens
        // the user never named.
        foreach (var url in FuzzUrls)
        foreach (var pattern in FuzzPatterns)
        {
            if (mm_notify.UrlWatch.IsExactHomeToken(pattern)) continue;
            var u = N(url);
            var p = N(pattern);
            var expected = u.Length > 0 && p.Length > 0 && u.Contains(p, StringComparison.Ordinal);
            Assert.Equal(expected, M(url, pattern));
        }
    }

    [Fact]
    public void Match_IsMonotoneInThePatternSet_SoAddingAPatternNeverUnblocks()
    {
        // Every subset of a 6-pattern pool against every url: a hit on a subset must remain a
        // hit on any superset. A matcher that lost a hit when another pattern was added would
        // under-block for a reason no user could ever diagnose.
        var pool = new[] { "youtube.com/shorts", "youtube.com/", "instagram.com/reels",
                           "facebook.com/reel", "", "tiktok.com" };
        foreach (var url in FuzzUrls)
        {
            for (var subset = 0; subset < (1 << pool.Length); subset++)
            {
                var chosen = Pick(pool, subset);
                if (!M(url, chosen)) continue;
                for (var extra = 0; extra < pool.Length; extra++)
                {
                    if ((subset & (1 << extra)) != 0) continue;
                    Assert.True(M(url, Pick(pool, subset | (1 << extra))),
                                $"adding '{pool[extra]}' unblocked {Quote(url)}");
                }
            }
        }
    }

    [Fact]
    public void Match_IsOrderIndependent_EvenThoughMatchedPatternForIsNot()
    {
        // MatchedPatternFor deliberately sweeps home tokens first so the REPORTED pattern is
        // stable; the boolean is an OR and must not depend on order at all.
        var pool = new[] { "youtube.com/", "youtube.com/shorts", "instagram.com/reels", "" };
        foreach (var url in FuzzUrls)
        {
            var forward = M(url, pool);
            var reversed = pool.Reverse().ToArray();
            Assert.Equal(forward, M(url, reversed));
        }
    }

    [Fact]
    public void Match_NullAndBlankElementsInTheSet_AreSkippedNotFatal()
    {
        // The union of the armed slots' patterns is built by concatenation; one slot with an
        // empty list must not be able to poison the whole set.
        Assert.True(M("youtube.com/shorts/x", null, "", "   ", "youtube.com/shorts"));
        Assert.False(M("example.org/", null, "", "   "));
        Assert.Equal("", Matched("example.org/", null, "", "   "));
        Assert.False(M(null, "youtube.com"));
        Assert.Equal("", R(null, "youtube.com"));
    }

    [Fact]
    public void Match_IsTotal_OverTheWholeFuzzCorpusCrossedWithItself()
    {
        // Both arguments adversarial at once. Sampled (every 37th) so the pass stays fast
        // while still covering thousands of pairs.
        var corpus = RandomCorpus();
        for (var i = 0; i < corpus.Count; i += 37)
        for (var j = 0; j < corpus.Count; j += 541)
        {
            var hit = M(corpus[i], corpus[j]);
            var target = R(corpus[i], corpus[j]);
            Assert.NotNull(target);
            if (!hit) Assert.Equal("", target);
        }
    }

    // ---------------------------------------------------------------------------------
    // 3. RedirectTargetFor - what may ever leave this layer
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Redirect_IsEitherNothing_OrAnHttpsUrlWithARealHost()
    {
        // The target is typed into a browser's omnibox and Enter is synthesised, so a
        // malformed one navigates the user somewhere nobody asked for. "https:///" is the
        // shape the hostless-token guard exists to prevent.
        var patterns = new[] { "youtube.com/shorts", "youtube.com/", "instagram.com/reels", "reddit.com" };
        foreach (var raw in RandomCorpus())
        {
            var target = R(raw, patterns);
            if (target.Length == 0) continue;
            Assert.StartsWith("https://", target);
            Assert.NotEqual("https:///", target);
            Assert.True(target == mm_notify.UrlWatch.YouTubeRedirect || target.EndsWith("/", StringComparison.Ordinal),
                        $"odd target {Quote(target)} from {Quote(raw)}");
            Assert.True(target.Length > "https://".Length + 1, $"hostless target {Quote(target)}");
        }
    }

    [Fact]
    public void Redirect_YouTubeGoesToSubscriptions_ForEveryYouTubeShapedHit()
    {
        // P59 decision 12: the YT exception exists so the shipped preset's home token cannot
        // send the browser straight back onto a blocked page.
        foreach (var url in new[] { "youtube.com/shorts/a", "https://www.youtube.com/shorts/a",
                                    "m.youtube.com/shorts/a", "HTTPS://WWW.YOUTUBE.COM:443/SHORTS/A?x=1",
                                    "https://user@m.youtube.com/shorts/a" })
            Assert.Equal(mm_notify.UrlWatch.YouTubeRedirect, R(url, "youtube.com/shorts"));
        // ...and every other host is sent to its own home, never to some third site's.
        Assert.Equal("https://instagram.com/", R("https://www.instagram.com/reels/x", "instagram.com/reels"));
        Assert.Equal("https://m.facebook.com/", R("m.facebook.com/reel/1", "facebook.com/reel"));
    }

    // ---------------------------------------------------------------------------------
    // 4. ShouldActOnHit - the cooldown, by property
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Cooldown_IsMonotoneInNow_SoAPermittedActionNeverBecomesForbidden()
    {
        // Once enough time has passed, more time passing cannot un-pass it. The watcher beats
        // every 2s against a 5s cooldown, so a non-monotone gate would drop redirects at
        // random intervals.
        var rng = new Random(20260819);
        for (var i = 0; i < 20000; i++)
        {
            long last = rng.Next(1, 1_000_000);
            long cooldown = rng.Next(1, 20_000);
            long now = last + rng.Next(0, 40_000);
            if (!mm_notify.UrlWatch.ShouldActOnHit(last, now, cooldown)) continue;
            Assert.True(mm_notify.UrlWatch.ShouldActOnHit(last, now + rng.Next(1, 100_000), cooldown));
        }
    }

    [Fact]
    public void Cooldown_NonsenseInputsAllowTheAction_WhichIsTheBlockingDirection()
    {
        // R1: where the choice is open, prefer the reading that nudges. A never-acted mark, a
        // backwards clock and an absent cooldown all mean "act".
        var extremes = new long[] { long.MinValue, -1, 0, 1, 4999, 5000, long.MaxValue };
        foreach (var last in extremes)
        foreach (var now in extremes)
        foreach (var cooldown in extremes)
        {
            var acted = mm_notify.UrlWatch.ShouldActOnHit(last, now, cooldown);
            if (last <= 0 || now < last || cooldown <= 0) Assert.True(acted);
        }
    }

    // ---------------------------------------------------------------------------------
    // 5. the two KNOWN RESIDUALS - current behaviour, named, with their finding ids
    // ---------------------------------------------------------------------------------

    [Fact]
    public void KnownResidual_F12_TrailingDotFqdnIsNotMatched()
    {
        // FINDING F12 (19/08/2026 bug-hunt, P2): "http://youtube.com./shorts/abc" loads (hosts
        // matching is exact-name) but the authority keeps its trailing dot, so the pattern
        // "youtube.com" -> "youtube.com/" never occurs in "youtube.com./shorts/abc" and the
        // watcher is silent on the one case F2b exists to catch. Pinned as CURRENT behaviour,
        // not as desired behaviour: the fix slice inverts these three assertions.
        Assert.Equal("youtube.com./shorts/abc", N("http://youtube.com./shorts/abc"));
        Assert.False(M("http://youtube.com./shorts/abc", "youtube.com/shorts"));
        Assert.False(M("http://youtube.com./", "youtube.com/"));
        // The ordinary FQDN is unaffected, which is why the residual is P2 and not P1.
        Assert.True(M("http://youtube.com/shorts/abc", "youtube.com/shorts"));
    }

    [Fact]
    public void KnownResidual_F13_InteriorWhitespaceSurvivesIntoTheRedirectHost()
    {
        // FINDING F13 (19/08/2026 bug-hunt, P2): omnibox free text is read verbatim, so typing
        // "why is youtube.com/shorts down" matches, and the target is built from an authority
        // that still carries interior spaces. Normalize deliberately only re-trims the EDGES
        // (UrlWatch.vb:151-159 argues interior whitespace is not recoverable), so the guard at
        // :171 - authority non-empty - never fires. Pinned as CURRENT behaviour.
        const string typed = "why is youtube.com/shorts down";
        Assert.Equal("why is youtube.com/shorts down", N(typed));
        Assert.True(M(typed, "youtube.com/shorts"));
        Assert.Equal("https://why is youtube.com/", R(typed, "youtube.com/shorts"));
        // The trimmed sibling behaves properly, so the residual is confined to free text.
        Assert.Equal(mm_notify.UrlWatch.YouTubeRedirect, R("youtube.com/shorts", "youtube.com/shorts"));
    }

    [Fact]
    public void KnownResidual_F60_ASchemelessPortedUrlWithAQueryButNoPath_NormalisesToNothing()
    {
        // FINDING F60 (A2 blitz, P3): SchemeOf (UrlWatch.vb:363) takes the scheme's tail up to
        // the first '/' ONLY - `tailEnd = If(slash >= 0, slash, s.Length)` - while
        // AuthorityEnd (:404) correctly ends the authority at '/', '?' OR '#'. So in
        // "youtube.com:8080?v=1" the tail is read as "8080?v=1", AllDigits says "not a port",
        // and the host is misread as the SCHEME "youtube.com" - which is not http/https, so
        // the whole token is discarded. Fail-SOFT (a miss in the best-effort nudge layer; the
        // hosts block is untouched) and no shipped preset host uses a port, hence P3.
        Assert.Equal("", N("youtube.com:8080?v=1"));
        Assert.Equal("", N("youtube.com:80#frag"));
        Assert.False(M("youtube.com:8080?v=1", "youtube.com"));
        // Every neighbouring shape is fine, which is what localises the defect to that line:
        Assert.Equal("youtube.com/", N("youtube.com:8080/?v=1"));      // a path is present
        Assert.Equal("youtube.com/", N("https://youtube.com:8080?v=1"));// the scheme was consumed
        Assert.Equal("youtube.com/", N("youtube.com?v=1"));             // no port
        Assert.Equal("youtube.com/", N("user@youtube.com:8080?v=1"));   // userinfo blocks the misread
    }

    // ---------------------------------------------------------------------------------

    private static string[] Pick(string[] pool, int mask)
    {
        var chosen = new List<string>();
        for (var i = 0; i < pool.Length; i++)
            if ((mask & (1 << i)) != 0) chosen.Add(pool[i]);
        return chosen.ToArray();
    }

    /// <summary>Renders a failing input readably - the corpus is full of NUL, tabs and
    /// bidi overrides, which an unescaped assertion message turns into gibberish.</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c < 32 || c == 127 || c > 126 ? "\\u" + ((int)c).ToString("x4") : c.ToString());
        return sb.Append('"').ToString();
    }
}
