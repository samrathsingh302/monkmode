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

'    MonkMode - notifier: the URL watcher (v1.1 F2a pure layer, pins P56-P60;
'    F2b seam + live UIA, pins P54/P61)
'
'    Four side-effect-free functions plus the exact-home predicate. The first half
'    of this file is the whole decision surface of the F2 URL watcher: given a
'    foreground browser URL and the union of the active slots' patterns, decide
'    (a) what the URL reduces to, (b) whether it is blocked, (c) where to send the
'    browser instead, and (d) whether enough time has passed to act again. The
'    second half (S7, from "F2b" below) is the seam and the live UIAutomation
'    wrappers behind it. Same testable-core + live-wrapper shape as
'    Notifications.vb and SingleInstance.vb, so the entire DECISION surface is
'    brute-force unit-tested cross-assembly (mm_notify.UrlWatch.X via
'    InternalsVisibleTo) with no browser and no notifier in the room: the tests
'    assign the hooks, and no UIA type is ever loaded in a test run.
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

Imports System.Runtime.InteropServices
Imports System.Windows.Automation

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

    ' ==================================================================
    ' F2b (v1.1 S7) - the watcher: P54 targeting, the P61 seam, the live UIA
    ' ==================================================================
    '
    '    Everything below is the OUTSIDE of the decision surface: which windows we
    '    look at, how the URL is read, and how the redirect is performed. It obeys
    '    one rule above all others, and every Try in this half exists for it:
    '
    '        A UIA FAILURE MUST NEVER REACH THE NOTIFIER.
    '
    '    The notifier is also the user-session app-kill loop (Form1.appKillTimer),
    '    and mm_notify dying means blocked apps stop being killed - a URL nudge
    '    taking the kill loop down with it would be a bypass manufactured by a
    '    convenience feature (R12). So: every entry point here is TOTAL, every UIA
    '    call sits inside a Try that swallows, and Form1 additionally runs the whole
    '    pass on a POOL THREAD so a UIA call that BLOCKS (a busy browser can hold a
    '    cross-process UIA read for seconds) cannot stall the 2s app-kill beat
    '    either. Nothing here can lift, shorten or weaken a block: the hosts-file
    '    block (B2) is the enforcement, this is a nudge on top of it.

    '    P54: the processes whose foreground window is worth reading. Firefox is out
    '    of scope (it is not a Chromium Views omnibox; documented residual R13).
    Friend ReadOnly BrowserProcessNames As String() = {"chrome", "msedge", "brave"}

    '    Is this foreground process one we watch? Case-insensitive, tolerating a
    '    ".exe" suffix (Process.ProcessName has none; a caller or a test may).
    '
    '    EXACT, not the substring test the BLOCKING predicates use, and that is
    '    deliberate: this is a TARGETING axis, not a blocking one. Widening it does
    '    not block more - it makes the watcher hunt for an address bar inside
    '    unrelated apps, and the redirect actor's SetValue would then be typing a
    '    URL into whatever Edit control that app happens to expose. Narrow is the
    '    safe direction HERE; the cost of a miss is a browser we do not nudge, and
    '    the hosts block still stops the page loading.
    Friend Function IsWatchedBrowser(ByVal processName As String) As Boolean
        If processName Is Nothing Then Return False
        Dim n As String = processName.Trim()
        If n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then n = n.Substring(0, n.Length - 4)
        If n.Length = 0 Then Return False
        For Each b As String In BrowserProcessNames
            If String.Equals(n, b, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    ' ------------------------------------------------------------------
    ' P61 - the seam
    ' ------------------------------------------------------------------

    '    The three hooks are Nothing in production and the live UIA implementations
    '    run; a test assigns them and NO UIAutomation type is ever loaded in a test
    '    run (the same RenameHookForTests/SetAttrHookForTests pattern the service
    '    uses for its hosts rename and read-only attribute).
    Friend Delegate Function ForegroundUrlReader() As String
    Friend Delegate Function ForegroundProcessNameReader() As String
    Friend Delegate Sub RedirectPerformer(ByVal target As String)

    '    One step of the live redirect (focus / type / Enter). Not a hook - the
    '    steps are supplied by LivePerformRedirect - but it is what lets the step
    '    ORDER and the focus-refusal rule be tested with no browser present.
    Friend Delegate Sub RedirectStep()

    Friend UrlReaderHook As ForegroundUrlReader = Nothing
    Friend ProcessNameHook As ForegroundProcessNameReader = Nothing
    Friend RedirectHook As RedirectPerformer = Nothing

    '    The foreground window's process name, or "" for anything unreadable.
    '    Never throws.
    Friend Function ForegroundProcessNameSafe() As String
        Try
            Dim hook As ForegroundProcessNameReader = ProcessNameHook
            If hook IsNot Nothing Then Return If(hook(), "")
            Return LiveForegroundProcessName()
        Catch ex As Exception
            Return ""
        End Try
    End Function

    '    The URL currently shown in the foreground browser's address bar, or "" for
    '    anything unreadable. Never throws.
    Friend Function ReadForegroundUrlSafe() As String
        Try
            Dim hook As ForegroundUrlReader = UrlReaderHook
            If hook IsNot Nothing Then Return If(hook(), "")
            Return LiveReadForegroundUrl()
        Catch ex As Exception
            Return ""
        End Try
    End Function

    '    Steer the foreground browser to `target`. True only when the redirect was
    '    actually performed. "" target => False and NOTHING happens (the caller
    '    gets "" from TickTarget whenever there is no hit, so this is the second
    '    lock on "no hit, no action"). Never throws.
    Friend Function PerformRedirect(ByVal target As String) As Boolean
        If String.IsNullOrEmpty(target) Then Return False
        Try
            Dim hook As RedirectPerformer = RedirectHook
            If hook IsNot Nothing Then
                hook(target)
                Return True
            End If
            Return LivePerformRedirect(target)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' ------------------------------------------------------------------
    ' the tick decision (P54 + P57-P60 + the seam, one total function)
    ' ------------------------------------------------------------------

    '    One watch beat: given the foreground process name, the union of the active
    '    slots' URL patterns and the tick pair, return the URL to redirect to, or
    '    "" for "do nothing". Reads the address bar THROUGH THE SEAM, and only
    '    after three gates have passed:
    '
    '      1. the foreground process is a watched browser (P54) - so a non-browser
    '         foreground never causes a UIA read at all;
    '      2. at least one non-blank pattern exists - so a machine with no armed
    '         block, or armed blocks that carry no --urls, never reads either. This
    '         gate is what makes "the watcher does NOTHING when no slot has
    '         patterns" a property of the code rather than of the pattern matcher's
    '         behaviour on an empty set;
    '      3. the P60 cooldown has elapsed - checked BEFORE the read, because a
    '         read we are forbidden to act on is pure cost.
    '
    '    TOTAL: the whole body is wrapped, so a hook that throws, a collection that
    '    throws while enumerating, or any future addition here yields "" (do
    '    nothing) rather than an exception in the notifier. "Do nothing" is the
    '    correct failure value for a nudge - the hosts block is unaffected by it.
    Friend Function TickTarget(ByVal processName As String,
                               ByVal patterns As IEnumerable(Of String),
                               ByVal lastActionTick As Long,
                               ByVal nowTick As Long) As String
        Try
            If Not IsWatchedBrowser(processName) Then Return ""
            If Not HasAnyPattern(patterns) Then Return ""
            If Not ShouldActOnHit(lastActionTick, nowTick, RedirectCooldownMs) Then Return ""
            Dim url As String = ReadForegroundUrlSafe()
            If url Is Nothing OrElse url.Length = 0 Then Return ""
            Return RedirectTargetFor(url, patterns)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    '    Does this set carry at least one pattern with any content? Nothing entries
    '    and blanks do not count - a slot armed with no --urls stores "", which
    '    splits to a list of empties, and acting on that would mean "read the
    '    address bar forever for nothing".
    Friend Function HasAnyPattern(ByVal patterns As IEnumerable(Of String)) As Boolean
        If patterns Is Nothing Then Return False
        For Each p As String In patterns
            If p IsNot Nothing AndAlso p.Trim().Length > 0 Then Return True
        Next
        Return False
    End Function

    ' ------------------------------------------------------------------
    ' the live UIA layer - NEVER reached from a test (the hooks stand in)
    ' ------------------------------------------------------------------

    '    The Chromium omnibox's UIA AutomationId. NOT a contract and NOT the same
    '    across the three browsers - it is a Views view-class ordinal, so it shifts
    '    with the build. Measured 18/08/2026 by tools\smoke\f2-url-smoke.ps1:
    '    Brave "view_1012" (and Chrome the same, per the research note), but Edge
    '    "view_1021". Hence the ClassName pass below, which is what actually catches
    '    Edge.
    Private Const OmniboxAutomationId As String = "view_1012"

    '    The omnibox's Views class name - the SUFFIX, not the whole string: Chrome
    '    and Edge report "OmniboxViewViews" and Brave reports
    '    "BraveOmniboxViewViews" (both measured 18/08/2026 by the same probe). A
    '    suffix test covers all three and any similarly-prefixed fork; an equality
    '    test would have silently missed Brave.
    Private Const OmniboxClassNameSuffix As String = "OmniboxViewViews"

    '    VK_RETURN. The redirect is SetValue + Enter: SetValue alone only edits the
    '    text in the address bar, it does not navigate.
    Private Const VkReturn As Byte = 13    ' VK_RETURN (0x0D)
    Private Const KeyEventKeyUp As UInteger = 2UI

    <DllImport("user32.dll")>
    Private Function GetForegroundWindow() As IntPtr
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Function GetWindowThreadProcessId(ByVal hWnd As IntPtr, ByRef lpdwProcessId As UInteger) As UInteger
    End Function

    '    keybd_event rather than SendKeys.SendWait: the watch pass runs on a POOL
    '    thread (Form1), and SendKeys' behaviour off the UI thread depends on which
    '    of its two implementations is in play. This one is a plain synthesized key
    '    event to the foreground window, thread-agnostic.
    <DllImport("user32.dll", EntryPoint:="keybd_event")>
    Private Sub SendKeyEvent(ByVal bVk As Byte, ByVal bScan As Byte, ByVal dwFlags As UInteger, ByVal dwExtraInfo As UIntPtr)
    End Sub

    '    The foreground window's process name (no ".exe"), "" when there is no
    '    foreground window or the process has already gone. Callers wrap.
    Private Function LiveForegroundProcessName() As String
        Dim h As IntPtr = GetForegroundWindow()
        If h = IntPtr.Zero Then Return ""
        Dim pid As UInteger = 0UI
        GetWindowThreadProcessId(h, pid)
        If pid = 0UI Then Return ""
        Using p As Process = Process.GetProcessById(CInt(pid))
            Return If(p.ProcessName, "")
        End Using
    End Function

    '    Read the FOREGROUND window's address bar. Foreground only, by design: a
    '    background window's omnibox would be a different browser window the user
    '    is not looking at, and a background TAB has no omnibox of its own at all
    '    (one Views omnibox per window, showing the ACTIVE tab). So a blocked page
    '    sitting in a background tab is a documented F2 residual - it is the hosts
    '    block's job, not the watcher's.
    '
    '    POLL, NEVER SUBSCRIBE. Chrome's native UIA provider floods property-change
    '    events on every keystroke (it froze NVDA for seconds); this is called once
    '    per 2s beat from a pool thread and holds no subscription.
    Private Function LiveReadForegroundUrl() As String
        Dim h As IntPtr = GetForegroundWindow()
        If h = IntPtr.Zero Then Return ""
        Dim root As AutomationElement = AutomationElement.FromHandle(h)
        If root Is Nothing Then Return ""
        Dim box As AutomationElement = FindOmnibox(root)
        If box Is Nothing Then Return ""
        Dim vp As ValuePattern = TryCast(box.GetCurrentPattern(ValuePattern.Pattern), ValuePattern)
        If vp Is Nothing Then Return ""
        Return If(vp.Current.Value, "")
    End Function

    '    SetValue + Enter on the foreground window's address bar. False when there
    '    is nothing to type into. Focus first: SetValue on an unfocused omnibox is
    '    accepted but the Enter would go to whatever DOES have focus (usually the
    '    page), so the navigation would never happen.
    '
    '    The foreground window is re-read here rather than carried over from the
    '    read: the user may have alt-tabbed in between, and typing into the window
    '    they are looking at NOW is the only defensible choice. What stops that
    '    being dangerous is FindOmnibox's strict identification - if the new
    '    foreground window is not a Chromium browser it has no element matching the
    '    omnibox id or class, so this returns False and nothing is typed anywhere.
    '
    '    The three ACTIONS are handed to PerformRedirectSteps rather than run here,
    '    so their ORDER and their refusal rule are unit-testable without a browser;
    '    this function is only the lookup that finds what to hand it.
    Private Function LivePerformRedirect(ByVal target As String) As Boolean
        Dim h As IntPtr = GetForegroundWindow()
        If h = IntPtr.Zero Then Return False
        Dim root As AutomationElement = AutomationElement.FromHandle(h)
        If root Is Nothing Then Return False
        Dim box As AutomationElement = FindOmnibox(root)
        If box Is Nothing Then Return False
        Dim vp As ValuePattern = TryCast(box.GetCurrentPattern(ValuePattern.Pattern), ValuePattern)
        If vp Is Nothing OrElse vp.Current.IsReadOnly Then Return False
        Return PerformRedirectSteps(Sub() box.SetFocus(),
                                    Sub() vp.SetValue(target),
                                    AddressOf PressEnter)
    End Function

    '    The two synthesized VK_RETURN events, down then up.
    Private Sub PressEnter()
        SendKeyEvent(VkReturn, 0, 0UI, UIntPtr.Zero)
        SendKeyEvent(VkReturn, 0, KeyEventKeyUp, UIntPtr.Zero)
    End Sub

    '    The redirect's three steps, in order, with the one rule that matters:
    '
    '        IF FOCUS FAILS, NOTHING IS TYPED AND NO KEY IS SENT.
    '
    '    Focus is a PRECONDITION, not a best effort. The Enter is a SYNTHESIZED
    '    GLOBAL KEY EVENT - it goes wherever focus currently is - so sending it
    '    after a failed SetFocus means pressing Enter on whatever the user is
    '    actually in: a page element, a button, a half-filled form. That is the one
    '    way this whole feature could do something the user did not ask for, and it
    '    would be doing it on THEIR keyboard focus, not on a block. Refusing costs
    '    one nudge; the next beat past the cooldown tries again, and the hosts block
    '    (the actual enforcement) never depended on this at all.
    '
    '    Extracted from LivePerformRedirect purely so this refusal can be pinned by
    '    a test: the steps arrive as delegates, so UrlWatchSeamTests can hand it a
    '    focus step that throws and assert the other two were never invoked. No UIA
    '    type appears in the signature, so a test run still loads no UIAutomation.
    '    Total: a Nothing step or a throw from any step yields False.
    Friend Function PerformRedirectSteps(ByVal takeFocus As RedirectStep,
                                         ByVal typeTarget As RedirectStep,
                                         ByVal pressEnter As RedirectStep) As Boolean
        If takeFocus Is Nothing OrElse typeTarget Is Nothing OrElse pressEnter Is Nothing Then Return False
        Try
            takeFocus()
        Catch ex As Exception
            Return False
        End Try
        Try
            typeTarget()
            pressEnter()
        Catch ex As Exception
            ' A SetValue that throws leaves the omnibox untouched; one that throws
            ' AFTER writing leaves the target text sitting there unnavigated, which
            ' is inert - the user's next keystroke replaces it.
            Return False
        End Try
        Return True
    End Function

    '    The address-bar element inside a browser window, or Nothing.
    '
    '    Two ways in, most specific first, each isolated so a provider that throws
    '    on one still gets the other tried:
    '      1. ControlType=Edit AND AutomationId="view_1012" - Chrome and Brave;
    '      2. ControlType=Edit AND a ClassName ending "OmniboxViewViews" - Edge
    '         (whose id ordinal is view_1021) and Brave's "BraveOmniboxViewViews".
    '    Neither pass alone covers all three browsers - that is the measured fact
    '    behind having both (18/08/2026 probe).
    '
    '    THERE IS DELIBERATELY NO "just take the first Edit control" FALLBACK. If
    '    neither pass identifies the box, we do not know we are looking at an
    '    address bar, and the actor's next move is to TYPE INTO whatever we hand it
    '    - a find bar, a search field, a form on the page. Giving up costs the
    '    nudge; the hosts block still stops the page, and the probe script (which
    '    DOES print the first-Edit fallback) is what would show a future Chromium
    '    renaming this. Name is not matched on either ("Address and search bar" is
    '    localised, so it would miss on a non-English Windows).
    '
    '    Both passes are scoped to the browser WINDOW's subtree, and the browser
    '    CHROME (toolbar/omnibox) is a Views tree separate from the page's renderer
    '    tree, so this does not force renderer accessibility on.
    Private Function FindOmnibox(ByVal root As AutomationElement) As AutomationElement
        Dim isEdit As New PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)
        Dim byId As AutomationElement = TryFindFirst(root, New AndCondition(isEdit,
            New PropertyCondition(AutomationElement.AutomationIdProperty, OmniboxAutomationId)))
        If byId IsNot Nothing Then Return byId

        ' The class test is a suffix comparison, which no PropertyCondition can
        ' express, so this pass enumerates the window's Edit controls instead. One
        ' extra tree walk at most, and only on the browsers the id pass misses.
        Dim edits As AutomationElementCollection = Nothing
        Try
            edits = root.FindAll(TreeScope.Descendants, isEdit)
        Catch ex As Exception
            Return Nothing
        End Try
        If edits Is Nothing Then Return Nothing
        For Each el As AutomationElement In edits
            Try
                Dim cls As String = If(el.Current.ClassName, "")
                If cls.EndsWith(OmniboxClassNameSuffix, StringComparison.OrdinalIgnoreCase) Then Return el
            Catch ex As Exception
                ' This element's provider faulted; the next one may not.
            End Try
        Next
        Return Nothing
    End Function

    '    FindFirst over the window's descendants, Nothing on any provider failure.
    Private Function TryFindFirst(ByVal root As AutomationElement, ByVal cond As Condition) As AutomationElement
        Try
            Return root.FindFirst(TreeScope.Descendants, cond)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

End Module
