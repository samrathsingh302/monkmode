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

'    MonkMode - the block page (v1.1 S7d, pins P50 + P51)
'
'    WHAT THIS IS. While at least one block is armed the notifier listens on
'    127.0.0.1:80 and answers every plain-HTTP request with one small page that
'    says "Locked in." and lists what is still running. A blocked domain resolves
'    to 127.0.0.1 through the hosts file (B2), so an http:// visit lands here
'    instead of on a connection-refused error page.
'
'    WHAT THIS IS NOT. It is NOT enforcement. Nothing here blocks, kills,
'    redirects, stamps, writes or decides anything: the enforcement surface is the
'    hosts file + the service, and this page is the courtesy note taped over the
'    hole they already made. Every failure mode below therefore fails SOFT - to
'    "no page" - because the alternative (a listener failure taking down the
'    notifier) would take the user-session app-kill AND the URL watcher down with
'    it, manufacturing a bypass out of a cosmetic feature (the same R12 hazard the
'    URL watcher's seam is built around).
'
'    HONEST LIMIT (carried into the S8 docs). HTTP ONLY. An https:// visit to a
'    blocked domain is answered by this process with a certificate for the wrong
'    name - or, more usually, is not answered at all - so the browser shows its
'    own TLS / connection error, exactly as it did before this feature existed.
'    Serving HTTPS would mean minting and installing a trusted root CA on the
'    machine, which is a far larger and far more dangerous thing than a nicer
'    error page is worth. Most blocked traffic in practice is https, so this page
'    is a bonus on the http path, never the mechanism.
'
'    WHY RAW TcpListener AND NOT HttpListener (P50). HttpListener is http.sys, and
'    http.sys needs a URL ACL (`netsh http add urlacl`) that a NON-ELEVATED process
'    does not have. mm_notify runs in the user session, unelevated: HttpListener
'    would throw Access Denied on EVERY start, the fail-soft would swallow it, and
'    the feature would be silently dead forever on every machine while the unit
'    tests stayed green. A raw loopback socket needs no ACL. Port 80 itself is not
'    privileged on Windows.
'
'    STRUCTURE. Everything above the class is PURE (strings and bytes; no socket,
'    no clock, no file) and is what the tests assert on. BlockPageServer is the
'    thin listener seam: it takes the port as an argument so tests bind an
'    EPHEMERAL port (Port 0) and never port 80.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

Friend Module BlockPage

    ' The live loopback port. Only Form1 passes it; every test passes 0 (ephemeral).
    Friend Const LoopbackPort As Integer = 80

    ' P50: after a failed bind, try again at most once a minute while a block is
    ' held. A port-80 conflict is usually another local web server, which may well
    ' go away; retrying forever at the 5s poll rate would be a pointless bind storm
    ' in the meantime.
    Friend Const RebindRetryIntervalMs As Long = 60000

    ' P50: per connection - read once, at most this many bytes, with this timeout,
    ' then answer and close. The request is IGNORED (see ServeOne), so these bounds
    ' exist only to stop a client holding the accept thread: a peer that connects
    ' and says nothing costs 2 seconds and no more.
    Friend Const ConnectionTimeoutMs As Integer = 2000
    Friend Const MaxRequestBytes As Integer = 8192

    ' The page's fixed text. Consts so the pinned tests assert on the same literals
    ' the page emits rather than on a string typed twice.
    Friend Const PageTitle As String = "MonkMode - locked in"
    Friend Const PageHeading As String = "Locked in."
    Friend Const HttpsLimitationText As String =
        "MonkMode only serves this page on plain HTTP; HTTPS sites just fail to load."

    Private Const Crlf As String = vbCr & vbLf

    ' One listable block. The caller (Form1) has already read and decrypted the ini;
    ' this type is deliberately plain data so BuildBlockPageHtml stays pure and the
    ' tests can build one from literals with no config in the room.
    '   Id        - the slot's [SlotN] Id, i.e. the SAME id `monkmode status` prints.
    '   SiteCount - how many domains that slot blocks.
    '   UntilText - the slot's end, en-CA PLAINTEXT (already decrypted). "" for a
    '               slot that has no end yet - PENDING and schedule slots - which is
    '               exactly how P50 excludes them from the page.
    Friend Class SlotLine
        Friend Id As String = ""
        Friend SiteCount As Integer = 0
        Friend UntilText As String = ""
    End Class

    ' P50's page. PURE: given the slots and the as-of mark it returns the same bytes
    ' every time, and it never throws.
    '
    ' asOfMark is the monotonic [Time] HighWater string - the SAME timeline the
    ' service enforces expiry on, never the wall clock, so the page cannot be made
    ' to say "0m left" by rolling the system clock forward.
    '
    ' A slot is LISTED only if its Until parses AND is still ahead of the mark. That
    ' one rule drops PENDING slots (no Until), schedule slots (no Until), elapsed
    ' slots, and any slot whose stamps are unreadable - in every case the page
    ' states LESS rather than something false, which is the same rule every other
    ' MonkMode message follows. Zero listable slots is a legitimate page: the
    ' heading still says Locked in., because the hosts entries that sent the browser
    ' here are still in place.
    Friend Function BuildBlockPageHtml(ByVal slots As IEnumerable(Of SlotLine), ByVal asOfMark As String) As String
        Dim sb As New StringBuilder()
        sb.Append("<!DOCTYPE html>" & vbLf)
        sb.Append("<html lang=""en"">" & vbLf)
        sb.Append("<head><meta charset=""utf-8""><title>" & HtmlEscape(PageTitle) & "</title></head>" & vbLf)
        sb.Append("<body>" & vbLf)
        sb.Append("<h1>" & HtmlEscape(PageHeading) & "</h1>" & vbLf)
        If slots IsNot Nothing Then
            For Each s As SlotLine In slots
                If s Is Nothing Then Continue For
                Dim remaining As TimeSpan? = Notifications.RemainingFromMark(s.UntilText, asOfMark)
                If remaining Is Nothing OrElse remaining.Value.TotalSeconds <= 0 Then Continue For
                ' HumanizeShort's sub-minute answer is the literal "<1m", which would be
                ' malformed markup if pasted in raw - so the span is escaped too, not just
                ' the Id. The site count is an Integer and cannot carry markup.
                sb.Append("<p>Block " & HtmlEscape(s.Id) & ": " &
                          s.SiteCount.ToString(CultureInfo.InvariantCulture) & " sites blocked - " &
                          HtmlEscape(Notifications.HumanizeShort(remaining.Value)) & "</p>" & vbLf)
            Next
        End If
        sb.Append("<p>" & HtmlEscape(HttpsLimitationText) & "</p>" & vbLf)
        sb.Append("</body>" & vbLf)
        sb.Append("</html>" & vbLf)
        Return sb.ToString()
    End Function

    ' Minimal HTML escaping for the one field that comes out of a file: the slot Id.
    ' The config is MAC-protected, but this page is rendered by a BROWSER and the
    ' notifier reads the ini WITHOUT a MAC gate (the raw, widest reading every other
    ' notifier scan uses), so a forged Id must not be able to put markup on the page.
    ' '&' first, or the later replacements would be double-escaped. Nothing => "".
    Friend Function HtmlEscape(ByVal s As String) As String
        If s Is Nothing Then Return ""
        Return s.Replace("&", "&amp;").
                 Replace("<", "&lt;").
                 Replace(">", "&gt;").
                 Replace("""", "&quot;")
    End Function

    ' P50's response, verbatim: the status line, the four headers, the blank line,
    ' then the UTF-8 body. Content-Length is the BYTE count of the encoded body (not
    ' its character count) - the page is ASCII today, but a slot Id is user data and
    ' a wrong length would truncate or hang the browser. Pure; never throws.
    Friend Function BuildResponseBytes(ByVal html As String) As Byte()
        Dim body As Byte() = Encoding.UTF8.GetBytes(If(html, ""))
        Dim head As String =
            "HTTP/1.1 200 OK" & Crlf &
            "Content-Type: text/html; charset=utf-8" & Crlf &
            "Content-Length: " & body.Length.ToString(CultureInfo.InvariantCulture) & Crlf &
            "Cache-Control: no-store" & Crlf &
            "Connection: close" & Crlf &
            Crlf
        Dim headBytes As Byte() = Encoding.UTF8.GetBytes(head)
        Dim outBytes(headBytes.Length + body.Length - 1) As Byte
        Array.Copy(headBytes, 0, outBytes, 0, headBytes.Length)
        Array.Copy(body, 0, outBytes, headBytes.Length, body.Length)
        Return outBytes
    End Function

    ' P50's re-bind gate: has intervalMs of MONOTONIC time (Environment.TickCount64)
    ' passed since the last bind ATTEMPT? Attempt, not success - a machine whose port
    ' 80 is permanently taken then costs one failed bind a minute, not one every 5s
    ' poll. A non-positive delta reads as "not yet", so it can never storm. Pure -
    ' same shape as Notifications.ShouldFirePeriodicReminder.
    Friend Function ShouldRetryBind(ByVal lastAttemptTick As Long, ByVal nowTick As Long, ByVal intervalMs As Long) As Boolean
        Return (nowTick - lastAttemptTick) >= intervalMs
    End Function

    ' ================= FX9 (F11): WHEN MAY THE PAGE HOLD PORT 80? =================
    '
    ' THE HARM THIS ENDS. The gate used to be "does the config NAME a block?" - the
    ' RawSlotBlockCount floor, which counts a position the moment its ScheduleSpec OR
    ' StartAt OR Until is non-empty. `monkmode block --start +30d --for 1h` therefore
    ' took 127.0.0.1:80 with ExclusiveAddressUse - so nothing else on the machine could
    ' have it - for THIRTY DAYS, and a schedule sat on it between windows. A cosmetic
    ' page evicting every other local web server for a month is the harm; the page is
    ' now up only while a block's own TIMER is running.
    '
    ' THE TRADE, STATED HONESTLY - THIS IS NOT A FREE LUNCH. A PENDING slot's sites are
    ' ALREADY BLOCKED from arm time: the CLI writes them into hosts and into
    ' monkmode_hosts.block at arm, and Service1.SlotContributesLists is
    ' `SlotEnforcesNow OrElse SlotIsPending`, so they are in the hosts union, the kill
    ' union and the P37 snapshot truth throughout the wait (Service1.vb:1189-1196,
    ' :3143-3156 - a documented, accepted v1.1 over-block). So "not started" does NOT
    ' mean "not enforcing", and this gate DOES cost something real: for the whole pending
    ' window a blocked http:// visit gets a browser connection-refused page instead of
    ' MonkMode's. That is the DECISION, not an oversight - a month-long exclusive hold on
    ' the machine's only port 80 is the worse harm of the two, and the thing that is
    ' protected either way is the block itself: hosts keeps refusing those domains for
    ' every second of the wait, with or without this socket. Same trade, smaller, for a
    ' schedule between windows.
    '
    ' WHY THE WIDE READING IS RIGHT EVERYWHERE ELSE AND WRONG HERE. RawSlotApps /
    ' RawSlotUrlPatterns / RawSlotBlockCount over-approximate deliberately: their cost
    ' is one extra kill or one extra toast word, and under-counting would drop a live
    ' block. This gate's cost is an exclusive hold on a well-known port belonging to the
    ' whole machine, and its under-count costs only the PRETTINESS of the refusal. That
    ' is why this is the one notifier scan that narrows.
    '
    ' THE BIAS, WHERE IT IS STILL UNCERTAIN. Every ambiguity binds:
    '   * a deadline present but unparseable (or undecryptable - Form1 hands those over
    '     as UnreadableStamp) => BIND;
    '   * an unreadable [Time] HighWater mark, so nothing can be measured => any slot
    '     carrying a deadline BINDS;
    '   * BindGraceSeconds past a deadline => still BIND, because the service removes a
    '     retired slot's hosts entries a tick AFTER its end, and the page should outlive
    '     the entries that send a browser here rather than the other way round.
    ' What never binds is a STRUCTURAL not-yet-started: PENDING (StartAt set, no Until
    ' yet) and a schedule with no open window have no timer running as a matter of shape,
    ' not of timing, so no mark and no grace can turn them into a bind. Their sites stay
    ' hosts-blocked regardless - see THE TRADE above.
    '
    ' NO MAC GATE, like every other notifier read. A forged config can only take the
    ' PAGE down, which is cosmetic; it cannot touch hosts, the kill list or the service.
    '
    ' CONVERGENCE, NOT FLAP. The verdict is a pure function of stored fields and the
    ' MONOTONIC mark, and both only ever move forward, so for a fixed config the answer
    ' flips at most once (bind -> release) and re-binds only when the config itself
    ' changes. There is no oscillation for the 5s poll to chase.

    ' Grace past a deadline before the page is released. The service retires a slot on
    ' its own 10s tick, so its hosts entries can outlive its end by a tick; 60s covers
    ' that with room and is the sanctioned direction of error (a page held slightly too
    ' long, never a port held for thirty days).
    Friend Const BindGraceSeconds As Integer = 60

    ' What Form1 substitutes for a slot stamp that is present in the file but cannot be
    ' decrypted or is empty after decryption. Deliberately never a parseable datetime, so
    ' it reads as "present, unmeasurable" => BIND. Public so the tests pin the bias on the
    ' same literal the notifier uses.
    Friend Const UnreadableStamp As String = "?"

    ' One slot as the bind decision sees it. All three stamps are en-CA PLAINTEXT
    ' (Form1 has already decrypted them); ScheduleSpec is plaintext-as-stored.
    '   UntilText       - the slot's own end. "" for PENDING and for a schedule slot.
    '   StartAtText     - the delayed start. Set + no UntilText = PENDING.
    '   ScheduleSpec    - the slot's window rule, if it is a schedule slot.
    '   WindowUntilText - ScheduleActiveUntil: the SERVICE stamps it when a window opens
    '                     and clears it at the close, so it is the one honest "is a
    '                     window open?" signal the notifier can read without growing a
    '                     window evaluator of its own.
    Friend Class SlotBindState
        Friend UntilText As String = ""
        Friend StartAtText As String = ""
        Friend ScheduleSpec As String = ""
        Friend WindowUntilText As String = ""
    End Class

    ' The gate. True = at least one slot's own TIMER is running right now (or is inside
    ' the boundary grace, or cannot be measured), so serve the page; False = release the
    ' port, which for a PENDING or between-windows slot means giving up the page while
    ' its sites stay hosts-blocked. asOfMark is the monotonic [Time] HighWater string,
    ' never the wall clock - so rolling the system clock cannot make MonkMode drop the
    ' page. PURE; never throws.
    Friend Function ShouldBindNow(ByVal slots As IEnumerable(Of SlotBindState), ByVal asOfMark As String) As Boolean
        If slots Is Nothing Then Return False
        Dim mark As DateTime
        Dim markOk As Boolean = DateTime.TryParse(If(asOfMark, ""), BindCulture, DateTimeStyles.None, mark)
        For Each s As SlotBindState In slots
            If s Is Nothing Then Continue For
            If SlotEnforcesNow(s, mark, markOk) Then Return True
        Next
        Return False
    End Function

    ' Is THIS slot's own TIMER running? An open (or just-closed) schedule window, or a
    ' running (or just-ended) end of its own. Everything else - PENDING, a schedule
    ' between windows, a slot with no stamps at all - is No.
    '
    ' "Timer running", NOT "enforcing": a PENDING slot's sites are in the hosts union
    ' from arm time and so ARE enforced here (Service1.SlotContributesLists). This asks
    ' the narrower question the PAGE is entitled to ask - see THE TRADE in the essay.
    Private Function SlotEnforcesNow(ByVal s As SlotBindState, ByVal mark As DateTime, ByVal markOk As Boolean) As Boolean
        If StampStillAhead(s.WindowUntilText, mark, markOk) Then Return True
        Return StampStillAhead(s.UntilText, mark, markOk)
    End Function

    ' Is this deadline still ahead of the mark, allowing BindGraceSeconds of overhang?
    ' "" (no deadline at all) => False - the ONLY answer that releases the port, and the
    ' one PENDING and between-windows both land on (deliberately: they are still
    ' hosts-blocked, they just get the browser's own refusal page). Unparseable, or an
    ' unmeasurable mark => True (bind: never release on an uncertainty).
    Private Function StampStillAhead(ByVal text As String, ByVal mark As DateTime, ByVal markOk As Boolean) As Boolean
        If If(text, "") = "" Then Return False
        If Not markOk Then Return True
        Dim deadline As DateTime
        If Not DateTime.TryParse(text, BindCulture, DateTimeStyles.None, deadline) Then Return True
        Return deadline.AddSeconds(BindGraceSeconds) > mark
    End Function

    ' Config datetimes are en-CA everywhere in MonkMode, the mark and the deadlines here
    ' included (same constant Notifications keeps for the same reason).
    Private ReadOnly BindCulture As New CultureInfo("en-CA")

End Module

' The listener seam. ONE background accept thread, one pre-rendered response, no
' state written from the request path at all.
'
' THE REQUEST PATH IS READ-ONLY BY CONSTRUCTION. The page is rendered by the UI
' thread's 5s poll and handed here as a finished byte array (SetBody); serving a
' connection copies that array to a socket and closes it. It reads no file, takes
' no lock the poll holds, allocates no page, increments no counter and touches no
' config - so a connection storm cannot slow the poll, cannot race the service's
' writes, and cannot produce a stats number an attacker chose. A storm's only cost
' is the accept backlog, on a background thread the UI never waits on.
Friend NotInheritable Class BlockPageServer

    ' Guards listener / boundPortValue / the generation identity. Never held while
    ' doing socket I/O, and never held across a thread join (there is no join), so
    ' the UI thread's calls into here are always brief.
    Private ReadOnly syncRoot As New Object()
    Private listener As TcpListener = Nothing
    Private boundPortValue As Integer = 0

    ' The finished response. Assigned wholesale (an atomic reference write, read
    ' with Volatile.Read on the accept thread), never mutated in place - so a serve
    ' in flight always writes ONE self-consistent page, old or new, never a mix.
    Private responseBytes As Byte() = Nothing

    Friend Sub New()
        ' Seeded with the no-listable-slots page so that a connection accepted before
        ' the first SetBody still gets a complete, valid HTTP response rather than an
        ' empty one. Form1 sets the real page before it ever calls TryStart.
        SetBody(BlockPage.BuildBlockPageHtml(New List(Of BlockPage.SlotLine)(), ""))
    End Sub

    ' Is the socket bound right now? False after a failed bind, after StopServing,
    ' and after the accept loop has stood itself down - which is precisely when the
    ' caller's 60s retry should try again.
    Friend ReadOnly Property IsListening As Boolean
        Get
            SyncLock syncRoot
                Return listener IsNot Nothing
            End SyncLock
        End Get
    End Property

    ' The port actually bound - the ephemeral port the OS chose when TryStart was
    ' passed 0, which is how a test reaches the listener without ever naming 80.
    ' 0 when not listening.
    Friend ReadOnly Property BoundPort As Integer
        Get
            SyncLock syncRoot
                Return boundPortValue
            End SyncLock
        End Get
    End Property

    ' Replace the page served from now on. Pure + cheap (a string encode); safe to
    ' call whether or not the listener is up. Never throws.
    Friend Sub SetBody(ByVal html As String)
        Try
            Volatile.Write(responseBytes, BlockPage.BuildResponseBytes(html))
        Catch ex As Exception
        End Try
    End Sub

    ' Bind 127.0.0.1:port and start accepting. Returns True if the socket is up
    ' (including "was already up"), False if the bind failed - and a False is a
    ' clean, quiet return, never an exception: on the live machine the usual cause
    ' is another local web server owning port 80, which is not MonkMode's business
    ' and is certainly not a reason to take the notifier down.
    '
    ' ExclusiveAddressUse is deliberate: MonkMode's page must not share its port with
    ' whatever else asked for it. It also makes the failure DETERMINISTIC, which is
    ' what the bind-failure test pins.
    Friend Function TryStart(ByVal port As Integer) As Boolean
        SyncLock syncRoot
            If listener IsNot Nothing Then Return True
            Dim l As TcpListener = Nothing
            Try
                l = New TcpListener(IPAddress.Loopback, port)
                l.ExclusiveAddressUse = True
                l.Start()
                boundPortValue = CType(l.LocalEndpoint, IPEndPoint).Port
                listener = l
                Dim t As New Thread(AddressOf AcceptLoop)
                t.IsBackground = True     ' P50: never holds the process open
                t.Name = "MonkMode block page"
                t.Start(l)
                Return True
            Catch ex As Exception
                Try
                    If l IsNot Nothing Then l.Stop()
                Catch ex2 As Exception
                End Try
                listener = Nothing
                boundPortValue = 0
                Return False
            End Try
        End SyncLock
    End Function

    ' Close the socket. The accept thread's blocking AcceptTcpClient throws as a
    ' result and the loop ends on its own - not joined, because this is called from
    ' the UI thread and a join is exactly how a cosmetic feature freezes a UI.
    ' Idempotent; never throws.
    Friend Sub StopServing()
        Dim victim As TcpListener = Nothing
        SyncLock syncRoot
            victim = listener
            listener = Nothing
            boundPortValue = 0
        End SyncLock
        Try
            If victim IsNot Nothing Then victim.Stop()
        Catch ex As Exception
        End Try
    End Sub

    ' Stand down a SPECIFIC generation. Called by an accept loop that has ended, so
    ' that a listener which died on its own is reported as not-listening and the
    ' caller's 60s retry revives it. The identity check is what stops a dying old
    ' loop from clearing a newer listener that has since been started on the same
    ' field.
    Private Sub MarkStopped(ByVal l As TcpListener)
        SyncLock syncRoot
            If listener Is l Then
                listener = Nothing
                boundPortValue = 0
            End If
        End SyncLock
    End Sub

    ' The accept loop. Runs on its own background thread for the life of one bind.
    ' Every connection is handled inside its own Try: a throw closes THAT socket and
    ' the loop continues, exactly as P50 requires.
    Private Sub AcceptLoop(ByVal state As Object)
        Dim l As TcpListener = TryCast(state, TcpListener)
        If l Is Nothing Then Return
        Try
            While True
                Dim client As TcpClient = Nothing
                Try
                    client = l.AcceptTcpClient()
                Catch ex As Exception
                    ' The socket was closed (StopServing) or faulted. Either way this
                    ' generation is over; standing down lets the caller re-bind later.
                    Exit While
                End Try
                Try
                    ServeOne(client)
                Catch ex As Exception
                Finally
                    Try
                        client.Close()
                    Catch ex2 As Exception
                    End Try
                End Try
            End While
        Catch ex As Exception
        Finally
            MarkStopped(l)
        End Try
    End Sub

    ' Answer ONE connection: read once (and throw the bytes away), write the page,
    ' return - the caller closes. The request is ignored ENTIRELY, which is both
    ' P50's rule and the reason no malformed, oversized, empty or hostile request can
    ' change what this process does: there is no parse to confuse and no branch for
    ' it to take. The read happens at all only so a browser's request does not sit
    ' unread in the receive buffer when the socket closes (which some clients report
    ' as a connection reset instead of showing the page).
    Private Sub ServeOne(ByVal client As TcpClient)
        If client Is Nothing Then Return
        Dim payload As Byte() = Volatile.Read(responseBytes)
        client.ReceiveTimeout = BlockPage.ConnectionTimeoutMs
        client.SendTimeout = BlockPage.ConnectionTimeoutMs
        Dim scratch(BlockPage.MaxRequestBytes - 1) As Byte
        Using ns As NetworkStream = client.GetStream()
            ns.ReadTimeout = BlockPage.ConnectionTimeoutMs
            ns.WriteTimeout = BlockPage.ConnectionTimeoutMs
            Try
                ns.Read(scratch, 0, scratch.Length)
            Catch ex As Exception
                ' A timed-out or broken read changes nothing: the response does not
                ' depend on the request, so we still answer.
            End Try
            If payload IsNot Nothing AndAlso payload.Length > 0 Then
                Try
                    ns.Write(payload, 0, payload.Length)
                    ns.Flush()
                Catch ex As Exception
                    ' The peer went away mid-write. Nothing to do and nothing to log.
                End Try
            End If

            ' P50 says "write the response, close". Closing LITERALLY there turns out to
            ' break the very case this feature exists for: a socket closed while unread
            ' bytes are still sitting in its receive buffer makes Windows send an RST, and
            ' a browser that gets an RST can show a connection-reset error INSTEAD of the
            ' page it has already received. Any request larger than the one 8 KB read hits
            ' that every time - fat cookies alone will do it, and the 40 KB case is pinned
            ' by test. So: send our FIN first, then DISCARD whatever the peer still had in
            ' flight. Still a pure discard (nothing is parsed, no branch depends on it),
            ' and bounded - but be honest about the bound: the 2s timeout is PER READ, not
            ' per connection, so a trickler feeding one byte every ~1.9s can hold this
            ' connection for ~2s + DrainReads x ~2s = ~18s and ~72 KB total before the
            ' hard cap closes it. Page-only, loopback-only degradation; the UI thread and
            ' every enforcement beat are on other threads and never wait on this one.
            Try
                client.Client.Shutdown(SocketShutdown.Send)
            Catch ex As Exception
            End Try
            Try
                Dim reads As Integer = 0
                While reads < DrainReads
                    If ns.Read(scratch, 0, scratch.Length) <= 0 Then Exit While
                    reads += 1
                End While
            Catch ex As Exception
            End Try
        End Using
    End Sub

    ' At most 8 x 8 KB of leftover request is drained before the socket is closed anyway.
    ' A browser never sends 64 KB of headers; something that does gets the RST it earned.
    Private Const DrainReads As Integer = 8

End Class
