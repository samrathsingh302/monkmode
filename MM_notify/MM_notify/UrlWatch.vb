'    Copyright (C) 2026 Samrath Singh
'
'    This file is part of MonkMode, a fork of Cold Turkey.
'    Source: https://github.com/samrathsingh302/monkmode
'
'    This program is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    This program is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with this program.  If not, see <https://www.gnu.org/licenses/>.

'    MonkMode - notifier: the URL-watch PURE layer (v1.1 F2a, pins P56-P60)
'
'    Four side-effect-free functions plus the exact-home predicate. This file is
'    the whole decision surface of the F2 URL watcher: given a foreground browser
'    URL and the union of the active slots' patterns, decide (a) what the URL
'    reduces to, (b) whether it is blocked, (c) where to send the browser
'    instead, and (d) whether enough time has passed to act again.
'
'    NOTHING here touches UIAutomation, the disk, the registry or the clock -
'    that is S7's job and it lives behind the P61 seam. Same testable-core +
'    live-wrapper shape as Notifications.vb and SingleInstance.vb, so the entire
'    surface is brute-force unit-tested cross-assembly (mm_notify.UrlWatch.X via
'    InternalsVisibleTo) with no browser and no notifier in the room.
'
'    THE CONTRACT THAT SHAPES EVERY LINE: this layer is BEST-EFFORT NUDGING, not
'    enforcement. The hosts-file block (B2, self-healed by the service every 10s)
'    is what actually stops a site loading; a redirect only steers a browser that
'    is already able to reach the page. So every function here is TOTAL - it
'    returns a safe value for every input, including adversarial ones, and never
'    throws. A throw would surface inside the notifier's 2s timer, and a notifier
'    that dies stops doing user-session app-kill too: a URL parser must never be
'    able to take the app-kill loop down with it (R12, fail-soft).
'
'    Fail DIRECTION, where a choice was genuinely open (R1): prefer the reading
'    that blocks MORE. The two places this bit:
'      - a bare-host pattern ("youtube.com", no trailing slash) is a WHOLE-HOST
'        substring pattern, not the P58 home token - see IsExactHomeToken;
'      - a nonsense tick pair makes ShouldActOnHit say "act".
'    The one place it does not: a URL with no host at all normalises to "" rather
'    than to a hostless "/" token, because a hostless match would make
'    RedirectTargetFor emit "https:///" - acting on garbage, not blocking more.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Friend Module UrlWatch

    '    P60: the minimum gap between two redirect actions, in milliseconds.
    '    5s, against the notifier's 2s watch beat, so a single hit produces ONE
    '    SetValue+Enter and not a stream of them while the page is loading and
    '    the omnibox still reads the old URL. Notifier-only: no service/guardian
    '    copy of this constant exists, so no parity test is owed.
    Friend Const RedirectCooldownMs As Integer = 5000

    '    P59: where a youtube.com / m.youtube.com hit is sent (decision 12).
    '    Subscriptions rather than the YT home feed, precisely so the redirect
    '    cannot land back on a blocked page when the shipped shortform preset
    '    (P63) carries the "youtube.com/" home token.
    Friend Const YouTubeRedirect As String = "https://www.youtube.com/feed/subscriptions"

    ' ------------------------------------------------------------------
    ' P56 - NormalizeUrlForMatch
    ' ------------------------------------------------------------------

    '    Reduce a raw URL to the comparison token "<host><path>", lower-case,
    '    path-preserving. Pure, total, never throws. Returns "" for anything that
    '    can never be a hit.
    '
    '    In pin order: null/blank => ""; trim; lower-case invariant; a scheme
    '    other than http/https => "" (about:, data:, chrome:, edge:, brave:,
    '    file:, view-source:, mailto:, a bare drive letter, ...); strip the
    '    scheme; strip "userinfo@"; strip ":port"; strip a leading "www."; drop
    '    "?query" / "#fragment" and everything after; append "/" when no path
    '    remains.
    '
    '    Scheme-less input is FIRST-CLASS, not an error path: the Chromium
    '    omnibox strips "https://" and "www." from the displayed Value when it is
    '    unfocused (see the 18/08/2026 omnibox-facts guide), so "youtube.com/watch"
    '    is the SHAPE this function will usually be handed in the field.
    '
    '    IDN/punycode is left exactly as it arrives - no IdnMapping conversion.
    '    IdnMapping throws on malformed labels, and this function may not throw;
    '    the cost is that a unicode host simply never matches an ASCII pattern
    '    (fail-soft, and the hosts-level block is the real enforcement).
    '
    '    IDEMPOTENT: Normalize(Normalize(x)) = Normalize(x), which is what lets
    '    UrlMatchesPatterns / RedirectTargetFor normalise their inputs defensively
    '    without caring whether the caller already did. Idempotence rests on the
    '    result carrying NO edge whitespace, so the authority and the path are
    '    re-trimmed after the strips that can expose some - see the comments at
    '    those two lines. (Before that they could not: "user@ b.com/x" reduced to
    '    " b.com/x", which a second pass then trimmed to something different, and
    '    which RedirectTargetFor would have sent out as "https:// b.com/".)
    Friend Function NormalizeUrlForMatch(ByVal raw As String) As String
        If raw Is Nothing Then Return ""
        Dim s As String = raw.Trim()
        If s.Length = 0 Then Return ""
        s = s.ToLowerInvariant()

        ' Scheme. Anything that is not http/https is never a hit. "http:host" is
        ' accepted alongside "http://host": the slashes are optional in the
        ' grammar and dropping the authority marker must not turn an http URL
        ' into a miss.
        Dim scheme As String = SchemeOf(s)
        If scheme.Length > 0 Then
            If scheme <> "http" AndAlso scheme <> "https" Then Return ""
            s = s.Substring(scheme.Length + 1)
        End If
        ' Protocol-relative ("//host/path") and the authority marker left by the
        ' scheme strip are the same two characters.
        If s.StartsWith("//", StringComparison.Ordinal) Then s = s.Substring(2)

        ' The authority ends at the first '/', '?' or '#'. Confining the
        ' userinfo and port strips to it is what stops a '@' or ':' inside a
        ' query string ("youtube.com?u=a@b") from eating the host.
        Dim aEnd As Integer = AuthorityEnd(s)
        Dim authority As String = s.Substring(0, aEnd)
        Dim rest As String = s.Substring(aEnd)

        ' userinfo: up to and including the LAST '@' in the authority (RFC 3986 -
        ' a password may itself contain '@').
        Dim at As Integer = authority.LastIndexOf("@"c)
        If at >= 0 Then authority = authority.Substring(at + 1)

        ' :port - everything from the first ':' of the authority to its end, port
        ' digits or not. A host never legitimately carries a colon otherwise, and
        ' dropping a malformed one ("youtube.com:abc") keeps the host matchable,
        ' which is the blocking direction. It is also what makes this function
        ' IDEMPOTENT: leaving "youtube.com:abc" in place would produce a token
        ' that a second pass reads as the scheme "youtube.com:" and discards.
        ' An IPv6 literal is collateral - "[::1]:8080" degrades to "[", a token
        ' that matches nothing. Fail-soft, and IPv6 in the omnibox is out of
        ' scope for a personal blocker.
        Dim colon As Integer = authority.IndexOf(":"c)
        If colon >= 0 Then authority = authority.Substring(0, colon)

        ' Re-trim the authority. The opening Trim only cleans the ENDS of the raw
        ' string; stripping userinfo or a port can expose whitespace that was
        ' interior a moment ago ("user@ b.com/x" -> " b.com"), and a host with a
        ' leading space matches nothing AND would ride into RedirectTargetFor as
        ' "https:// b.com/". Trimmed again after the www. strip for the same
        ' reason ("www. b.com"), which is also what keeps this function
        ' idempotent on those shapes. Interior whitespace is left alone: it is
        ' not recoverable, and a host that matches nothing is the fail-soft
        ' outcome anyway.
        authority = authority.Trim()

        ' A leading "www." only. "m." and every other subdomain is KEPT, because
        ' substring matching already lets "youtube.com/shorts" catch
        ' "m.youtube.com/shorts/x", and because P59's redirect needs to know it
        ' was the mobile host.
        If authority.StartsWith("www.", StringComparison.Ordinal) Then authority = authority.Substring(4).Trim()

        ' No host left => nothing real was navigated to. Returning "" (rather
        ' than a hostless "/") keeps RedirectTargetFor from ever emitting
        ' "https:///".
        If authority.Length = 0 Then Return ""

        ' Path: what is left before the first '?' or '#'. Trimmed for the same
        ' reason the authority is - the cut can expose a trailing space that was
        ' interior before it ("b.com/x ?q").
        Dim path As String = rest
        Dim cut As Integer = FirstIndexOfAny(path, "?"c, "#"c)
        If cut >= 0 Then path = path.Substring(0, cut)
        path = path.Trim()
        If Not path.StartsWith("/", StringComparison.Ordinal) Then path = "/"

        Return authority & path
    End Function

    '    The URL's host - everything before the first '/' of a NORMALISED token.
    '    "" for "" or for a token that somehow has no host. Pure, never throws.
    Friend Function HostOfNormalized(ByVal normalized As String) As String
        If String.IsNullOrEmpty(normalized) Then Return ""
        Dim slash As Integer = normalized.IndexOf("/"c)
        If slash < 0 Then Return normalized
        Return normalized.Substring(0, slash)
    End Function

    ' ------------------------------------------------------------------
    ' P58 - the exact-home token
    ' ------------------------------------------------------------------

    '    True when the RAW pattern is exactly "<host>/": a trailing slash and no
    '    further path. Such a pattern matches ONLY the bare home page - it is the
    '    one thing substring matching cannot express, since "youtube.com/" is a
    '    substring of every URL on the host.
    '
    '    Decided on the RAW text, not on the normalised form, and that is
    '    load-bearing. P56 appends "/" when no path remains, so "youtube.com" and
    '    "youtube.com/" NORMALISE to the same token; the trailing slash in the
    '    pattern file is the only surviving signal of the author's intent. Reading
    '    it post-normalisation would silently demote a bare-host pattern
    '    ("youtube.com", plainly meaning the whole site) to home-only - a
    '    narrowing, i.e. the fail-open direction. So:
    '        "youtube.com/"  -> home only          (the P63 shortform token)
    '        "youtube.com"   -> the whole host     (substring, blocks more)
    '    Scheme and case are irrelevant: "https://YouTube.com/" is the same token.
    Friend Function IsExactHomeToken(ByVal rawPattern As String) As Boolean
        If rawPattern Is Nothing Then Return False
        Dim t As String = rawPattern.Trim()
        If Not t.EndsWith("/", StringComparison.Ordinal) Then Return False
        ' A query or fragment anywhere in the pattern disqualifies it. P58 says
        ' EXACTLY "<host>/", and "youtube.com/?x/" only looks like one because
        ' normalisation throws the "?x" away; admitting it would demote a junk
        ' pattern to home-only, which is the narrowing (fail-open) direction.
        ' Refused here, so it stays an ordinary substring pattern and blocks the
        ' whole host instead - wrong in the safe direction.
        If t.IndexOf("?"c) >= 0 OrElse t.IndexOf("#"c) >= 0 Then Return False
        Dim n As String = NormalizeUrlForMatch(t)
        If n.Length = 0 Then Return False
        ' Exactly one '/', in last position: host + bare root, no further path.
        Return n.IndexOf("/"c) = n.Length - 1
    End Function

    ' ------------------------------------------------------------------
    ' P57 - UrlMatchesPatterns
    ' ------------------------------------------------------------------

    '    Is this URL covered by any of these patterns? Case-insensitive ORDINAL
    '    SUBSTRING, except for the P58 home token, which is checked first and
    '    compares for equality.
    '
    '    WIDEN-ONLY, for exactly the reason the app-kill matcher is
    '    (MonkMode.Tests\AppKillMatchTests.cs header): a token-exact URL matcher
    '    would be tidier but would NARROW the matched set, and narrowing on a
    '    blocking path is fail-open. Substring means a pattern catches every
    '    deeper path, every subdomain that ends in the pattern's host, and every
    '    query-stripped variant, with no rule per case.
    '
    '    Empty/blank URL, empty/Nothing pattern set, and patterns that normalise
    '    to "" all yield False. Pure, total, never throws; a Nothing element
    '    inside the collection is skipped, not fatal.
    Friend Function UrlMatchesPatterns(ByVal url As String, ByVal patterns As IEnumerable(Of String)) As Boolean
        Return MatchedPatternFor(url, patterns).Length > 0
    End Function

    '    The first pattern that covers this URL, in its NORMALISED form; "" when
    '    none does. UrlMatchesPatterns is this, reduced to a Boolean; kept public
    '    to the test assembly because "which pattern fired" is what makes a brute-
    '    force table readable when it disagrees with the expectation.
    '
    '    The home tokens are swept BEFORE the substring patterns, as P58 pins.
    '    For a Boolean the order is immaterial (it is an OR either way); it is
    '    pinned so that the reported pattern is stable.
    Friend Function MatchedPatternFor(ByVal url As String, ByVal patterns As IEnumerable(Of String)) As String
        If patterns Is Nothing Then Return ""
        Dim u As String = NormalizeUrlForMatch(url)
        If u.Length = 0 Then Return ""

        ' Pass 1: the exact-home tokens.
        For Each p As String In patterns
            If IsExactHomeToken(p) Then
                Dim n As String = NormalizeUrlForMatch(p)
                If n.Length > 0 AndAlso String.Equals(u, n, StringComparison.OrdinalIgnoreCase) Then Return n
            End If
        Next

        ' Pass 2: substring.
        For Each p As String In patterns
            If Not IsExactHomeToken(p) Then
                Dim n As String = NormalizeUrlForMatch(p)
                If n.Length > 0 AndAlso u.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 Then Return n
            End If
        Next

        Return ""
    End Function

    ' ------------------------------------------------------------------
    ' P59 - RedirectTargetFor
    ' ------------------------------------------------------------------

    '    Where to send a browser sitting on a blocked URL: "https://<host>/" for
    '    the host it is already on, except youtube.com / m.youtube.com, which go
    '    to the subscriptions feed (decision 12) rather than the YT home feed.
    '    "" when nothing matched - and "" means the caller does NOTHING, which is
    '    why every no-match path returns it rather than some default page.
    '
    '    The target is built from the URL's OWN host, never from the pattern's:
    '    a pattern may be a bare path fragment, and steering the user to some
    '    other site's home page would be a surprise, not a block.
    '
    '    Loop note (deliberate, and the reason the YT exception exists): if the
    '    pattern set carries the home token for the same host - "instagram.com/"
    '    plus "instagram.com/reels" - then the target IS itself a hit, and the
    '    watcher will re-fire once per cooldown. That is the block getting
    '    STRONGER (the site becomes unusable), not weaker, and the 5s cooldown
    '    bounds it; it is not silently repaired here, because "redirect somewhere
    '    unblocked" has no defined answer and inventing one would be a way to
    '    fail open. The shipped preset (P63) pairs the only home token it ships,
    '    "youtube.com/", with the YT exception, so no shipped set loops.
    '
    '    The YT exception has its own sub-case, on the same accepted-residual
    '    terms: a WHOLE-HOST "youtube.com" pattern (no trailing slash) blocks
    '    every YT path, the subscriptions feed included, so the target is a fixed
    '    point - the watcher re-fires once per cooldown and YouTube becomes
    '    unusable. That is exactly what a user who blocked the whole of YouTube
    '    asked for, so it is documented rather than repaired; a user who wants
    '    the feed to survive writes the "youtube.com/" home token instead.
    Friend Function RedirectTargetFor(ByVal url As String, ByVal patterns As IEnumerable(Of String)) As String
        If MatchedPatternFor(url, patterns).Length = 0 Then Return ""
        Dim host As String = HostOfNormalized(NormalizeUrlForMatch(url))
        If host.Length = 0 Then Return ""
        If host = "youtube.com" OrElse host = "m.youtube.com" Then Return YouTubeRedirect
        Return "https://" & host & "/"
    End Function

    ' ------------------------------------------------------------------
    ' P60 - ShouldActOnHit
    ' ------------------------------------------------------------------

    '    May the watcher act now, given when it last acted? Ticks are
    '    Environment.TickCount64 milliseconds; 0 (or any non-positive value)
    '    means "never acted", so the first hit always acts.
    '
    '    Total by construction, including against a caller that hands it
    '    nonsense: a backwards tick pair or a non-positive cooldown returns True
    '    (acting is the blocking direction, R1), and the only subtraction happens
    '    when 0 < last <= now, so no input can overflow it.
    Friend Function ShouldActOnHit(ByVal lastActionTick As Long, ByVal nowTick As Long, ByVal cooldownMs As Long) As Boolean
        If lastActionTick <= 0 Then Return True        ' never acted
        If nowTick < lastActionTick Then Return True   ' time went backwards
        If cooldownMs <= 0 Then Return True            ' no cooldown asked for
        Return (nowTick - lastActionTick) >= cooldownMs
    End Function

    ' ------------------------------------------------------------------
    ' helpers (private, pure)
    ' ------------------------------------------------------------------

    '    The URL's scheme, lower-case and without its colon; "" when there is
    '    none. Scheme-less input is the common case (the unfocused omnibox), so
    '    "no scheme" must be an ordinary answer, never an error.
    '
    '    Two ambiguities the grammar cannot settle on its own:
    '      - "youtube.com:8080/watch" - "youtube.com" is a legal scheme token, so
    '        the colon is disambiguated by what FOLLOWS it: all-digits up to the
    '        next '/' means port, not scheme. ("localhost:3000" needs this too.)
    '      - a colon later in the path ("youtube.com/a:b") is not a scheme at all,
    '        so a '/' before the colon settles it immediately.
    Private Function SchemeOf(ByVal s As String) As String
        Dim colon As Integer = s.IndexOf(":"c)
        If colon <= 0 Then Return ""                      ' no colon, or a leading one
        Dim slash As Integer = s.IndexOf("/"c)
        If slash >= 0 AndAlso slash < colon Then Return ""  ' the colon is in the path
        Dim token As String = s.Substring(0, colon)
        If Not IsSchemeToken(token) Then Return ""
        Dim tailEnd As Integer = If(slash >= 0, slash, s.Length)
        Dim tail As String = s.Substring(colon + 1, tailEnd - colon - 1)
        If tail.Length > 0 Then
            If AllDigits(tail) Then Return ""            ' "host:8080/..." - a port, not a scheme
        Else
            ' Nothing between the colon and the path. "scheme://host" and a bare
            ' "about:" are schemes; "host:/path" (an empty port) is not, and
            ' reading it as one would drop a host that is still matchable.
            Dim afterColon As String = s.Substring(colon + 1)
            If afterColon.Length > 0 AndAlso Not afterColon.StartsWith("//", StringComparison.Ordinal) Then Return ""
        End If
        Return token
    End Function

    '    RFC 3986 scheme grammar: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    '    Input is already lower-cased.
    Private Function IsSchemeToken(ByVal token As String) As Boolean
        If token.Length = 0 Then Return False
        If token(0) < "a"c OrElse token(0) > "z"c Then Return False
        For i As Integer = 1 To token.Length - 1
            Dim c As Char = token(i)
            If (c >= "a"c AndAlso c <= "z"c) OrElse (c >= "0"c AndAlso c <= "9"c) _
               OrElse c = "+"c OrElse c = "-"c OrElse c = "."c Then Continue For
            Return False
        Next
        Return True
    End Function

    '    True for a run of ASCII digits. ASCII deliberately, not Char.IsDigit,
    '    which also accepts unicode digit forms - a port is ASCII, and a host
    '    label carrying eastern-arabic numerals must not be read as one.
    '    Never called with "" (SchemeOf handles the empty tail on its own branch).
    Private Function AllDigits(ByVal s As String) As Boolean
        For i As Integer = 0 To s.Length - 1
            If s(i) < "0"c OrElse s(i) > "9"c Then Return False
        Next
        Return True
    End Function

    '    Index of the first '/', '?' or '#'; the string's length when there is
    '    none (i.e. the whole string is the authority).
    Private Function AuthorityEnd(ByVal s As String) As Integer
        For i As Integer = 0 To s.Length - 1
            Dim c As Char = s(i)
            If c = "/"c OrElse c = "?"c OrElse c = "#"c Then Return i
        Next
        Return s.Length
    End Function

    '    Index of whichever of the two characters appears first; -1 for neither.
    Private Function FirstIndexOfAny(ByVal s As String, ByVal a As Char, ByVal b As Char) As Integer
        For i As Integer = 0 To s.Length - 1
            If s(i) = a OrElse s(i) = b Then Return i
        Next
        Return -1
    End Function

End Module
