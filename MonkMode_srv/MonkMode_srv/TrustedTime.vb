' Copyright (C) 2026 Samrath Singh
'
' This file is part of MonkMode, a fork of Cold Turkey.
' Source: https://github.com/samrathsingh302/monkmode
'
' This program is free software: you can redistribute it and/or modify
' it under the terms of the GNU General Public License as published by
' the Free Software Foundation, either version 3 of the License, or
' (at your option) any later version.
'
' This program is distributed in the hope that it will be useful,
' but WITHOUT ANY WARRANTY; without even the implied warranty of
' MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
' GNU General Public License for more details.
'
' You should have received a copy of the GNU General Public License
' along with this program.  If not, see <https://www.gnu.org/licenses/>.

' ============================ F77: CREDITING DOWNTIME ============================
'
' THE PROBLEM. B4 decides expiry off the monotonic [Time] HighWater, which only
' advances while the service is running. That is what stops a clock-forward from
' lifting a block - and it also means a machine that is OFF or ASLEEP earns nothing
' toward its own expiry, so a block armed `--until 02:00` that spans an overnight
' shutdown does not end at 02:00; it ends whenever the machine has been ON long
' enough. Samrath asked for the wall-clock promise to be kept (28/08/2026): shut
' down at 00:00, boot at 10:00, a 02:00 block should already be over.
'
' WHY THE OBVIOUS FIX IS A BYPASS. "Credit the boot gap from DateTime.Now" hands B4
' straight back, and in its EASIEST form: shut down, set the clock (or the BIOS
' clock) forward, boot, and the block lifts. That is strictly less work than the
' attack B4 exists to stop, and it would flip B4's severity from Low to Critical.
' Downtime can only be credited against a clock the person being blocked cannot
' edit.
'
' THE MECHANISM. The config carries a second, MAC-covered global beside the mark:
' [Time] TrustedUtc, the UTC instant at which HighWater was last known correct.
' The pair moves together, always:
'
'   - every tick, whatever the monotonic rule credited to HighWater is added to the
'     anchor too, so the two stay in step with no network at all;
'   - when a probe returns an EXTERNALLY CORROBORATED UTC 'now', the real elapsed
'     since the anchor is trustedNow - anchor. The tick already credited part of
'     that; the remainder is the downtime, and it is added to the mark.
'
' Three properties fall out, and all three are load-bearing:
'
'   1. TIMEZONE-PROOF. Credit is a DURATION between two UTC instants, added to the
'      local mark. Changing the machine's timezone moves neither operand. (This is
'      why the anchor is stored in invariant UTC and not the en-CA local format
'      every other datetime here uses - see ConfigIntegrity.TrustedUtcFormat.)
'   2. CLOCK-PROOF. DateTime.Now and DateTime.UtcNow appear NOWHERE in the credit
'      path. Rolling the local clock forward by a year earns exactly zero.
'   3. FAIL-CLOSED. No network, too few witnesses, witnesses that disagree, an
'      unparseable anchor, a negative delta - every one of them yields NO credit,
'      which is precisely today's shipped behaviour. The feature can only ever
'      shorten an over-run, never lift a block early.
'
' WHY HTTPS AND NOT NTP. The Date response header is covered by the TLS handshake,
' so sinkholing the host through the hosts file MonkMode itself writes (or through
' DNS) yields a certificate failure, not a forged time. Plain NTP is unauthenticated
' - a local NTP server would forge it outright. Certificate validation is therefore
' left at the .NET default and must NEVER be relaxed here: it is the entire defence.
'
' THE RESIDUAL, STATED HONESTLY. Someone who installs a trusted root CA and MITMs
' EVERY witness can manufacture credit. That is an administrative attack on the
' machine's trust store, the same family as B10, and it is more work than the
' offline disk edit B10 already concedes. Compromising a SUBSET of witnesses buys
' nothing: the quorum takes the MINIMUM reading, so a lying witness can only push
' the credit DOWN (over-block), never up. Gaining time requires all of them.

Imports System.Collections.Generic
Imports System.Globalization
Imports System.Net.Http

Namespace Global.monkmode

    Friend Module TrustedTime

        ' The witness hosts, HEADed over HTTPS for their Date response header.
        ' Chosen for three reasons: run by mutually independent organisations (so one
        ' compromised operator cannot move the quorum), effectively always up, and
        ' nothing anyone would put on a self-control blocklist - if one IS blocked it
        ' simply drops out of the quorum and the others carry it, and if they all are
        ' the feature falls back to today's no-credit behaviour.
        Friend ReadOnly WitnessUrls As String() = {
            "https://www.cloudflare.com/",
            "https://www.microsoft.com/",
            "https://www.apple.com/"
        }

        ' How many witnesses must agree before a reading is trusted at all. Two, not
        ' one: a single source is a single point of forgery, and the whole design rests
        ' on an attacker having to defeat every witness rather than any witness.
        Friend Const MinWitnesses As Integer = 2

        ' How far above the MINIMUM reading a witness may sit and still count toward the
        ' quorum. Generous (5 min) because these are independent servers whose clocks
        ' and network latencies genuinely differ; tight enough that a witness lying by
        ' the hours an attacker would need is excluded from the count rather than
        ' averaged in.
        Friend Const WitnessSpreadCeilingSeconds As Long = 300

        ' A ceiling on a single credit, purely to bound the arithmetic - a year is far
        ' beyond any block this tool can arm (7 days), so it never binds in practice.
        ' It is a cap and not a refusal on purpose: refusing an over-large credit would
        ' punish a genuinely long shutdown by never lifting the block, while capping at
        ' a year lifts every real block just the same. It changes no attack outcome,
        ' because an attacker who has fooled the whole quorum already has more than
        ' enough credit at any cap.
        Friend Const MaxCreditSeconds As Long = 365L * 24L * 60L * 60L

        ' Per-witness HTTP timeout. Three witnesses are probed sequentially, so the
        ' worst case is ~3x this - which is why the probe NEVER runs on the tick thread
        ' (see TrustedTimeProbe): the 10s enforcement beat must not wait on a network.
        Friend Const ProbeTimeoutMs As Integer = 3000

        ' Probe cadence. Fast while no reading has ever succeeded (a boot usually beats
        ' the network stack up, and the whole point is to lift promptly once it
        ' arrives); slow once the anchor is live, where the only thing left to catch is
        ' a sleep gap.
        Friend Const ProbeRetryFastMs As Long = 60000
        Friend Const ProbeRetrySteadyMs As Long = 600000

        ' ---------------------------- PURE (unit-tested) ----------------------------

        ' How long to wait before the next probe. Fast until a reading has EVER succeeded
        ' (a boot generally beats the network stack up, and a block owed a downtime credit
        ' wants it promptly), and fast again whenever the config has no anchor - because
        ' until one exists no downtime is creditable at all, and every minute spent
        ' unseeded is downtime that can never be recovered. Steady only once there is both
        ' a working probe and an anchor for it to measure from.
        Friend Function ProbeIntervalMs(ByVal everSucceeded As Boolean, ByVal anchorMissing As Boolean) As Long
            If everSucceeded AndAlso Not anchorMissing Then Return ProbeRetrySteadyMs
            Return ProbeRetryFastMs
        End Function

        ' The anchor as stored: invariant UTC, ConfigIntegrity.TrustedUtcFormat.
        Friend Function FormatUtc(ByVal value As DateTime) As String
            Return value.ToString(ConfigIntegrity.TrustedUtcFormat, CultureInfo.InvariantCulture)
        End Function

        ' Parse an anchor. False (and DateTime.MinValue) on anything unparseable, which
        ' every caller treats as "no anchor" => no credit.
        Friend Function TryParseUtc(ByVal text As String, ByRef result As DateTime) As Boolean
            result = DateTime.MinValue
            If text Is Nothing OrElse text = "" Then Return False
            Return DateTime.TryParseExact(text, ConfigIntegrity.TrustedUtcFormat,
                                          CultureInfo.InvariantCulture, DateTimeStyles.None, result)
        End Function

        ' The quorum rule. Takes the MINIMUM reading and requires at least minWitnesses
        ' readings within spreadCeilingSeconds of it; returns "" when that is not met.
        '
        ' Minimum, not mean or median, deliberately: it is the conservative direction in
        ' every failure mode. A witness that reports EARLY (broken clock, or an attacker
        ' trying to stall a lift) lowers the minimum and so lowers the credit - the
        ' block over-blocks, which is safe. A witness that reports LATE - the direction
        ' that would lift a block early - cannot pull the minimum up at all; it just
        ' falls outside the spread and stops counting toward the quorum. So buying time
        ' requires forging EVERY witness, while breaking any one of them only ever costs
        ' the honest user a later lift.
        Friend Function CorroboratedUtc(ByVal readings As List(Of DateTime),
                                        ByVal minWitnesses As Integer,
                                        ByVal spreadCeilingSeconds As Long) As String
            If readings Is Nothing OrElse readings.Count < minWitnesses Then Return ""
            Dim ordered As New List(Of DateTime)(readings)
            ordered.Sort()
            Dim lowest As DateTime = ordered(0)
            Dim agreeing As Integer = 0
            For Each r As DateTime In ordered
                If CLng((r - lowest).TotalSeconds) <= spreadCeilingSeconds Then agreeing += 1
            Next
            If agreeing < minWitnesses Then Return ""
            Return FormatUtc(lowest)
        End Function

        ' The real seconds elapsed since the anchor, per the corroborated reading.
        ' 0 whenever it cannot be established or would be negative - an anchor that
        ' does not parse, a reading that does not parse, a reading at or before the
        ' anchor (clock skew between witnesses, or a re-probe inside the same second).
        Friend Function ElapsedSinceAnchor(ByVal anchorUtcText As String,
                                           ByVal trustedNowUtcText As String,
                                           ByVal maxCreditSeconds As Long) As Long
            Dim anchor As DateTime, trustedNow As DateTime
            If Not TryParseUtc(anchorUtcText, anchor) Then Return 0
            If Not TryParseUtc(trustedNowUtcText, trustedNow) Then Return 0
            Dim delta As Long = CLng((trustedNow - anchor).TotalSeconds)
            If delta <= 0 Then Return 0
            If delta > maxCreditSeconds Then Return maxCreditSeconds
            Return delta
        End Function

        ' THE WHOLE DECISION, in one pure place so the regressions can pin it.
        '
        ' Inputs: the mark as stored at the top of this tick (storedHwText), the mark
        ' the existing B4 monotonic rule already produced for this tick (tickHwText -
        ' AdvanceHighWater's output, untouched), the stored anchor, and the corroborated
        ' reading for this tick ("" when there is none, which is the overwhelmingly
        ' common case - probes are minutes apart).
        '
        ' Outputs: the mark and the anchor to persist. BOTH are monotonic - the max of
        ' what the two independent rules would give - so neither can ever be walked
        ' backwards, by a lying witness or by anything else.
        '
        ' With no reading this reduces to "keep the tick's mark, and move the anchor by
        ' exactly the same amount", i.e. the shipped B4 behaviour plus bookkeeping.
        Friend Sub ResolveMarkAndAnchor(ByVal storedHwText As String,
                                        ByVal tickHwText As String,
                                        ByVal storedAnchorUtcText As String,
                                        ByVal trustedNowUtcText As String,
                                        ByVal maxCreditSeconds As Long,
                                        ByRef outHwText As String,
                                        ByRef outAnchorUtcText As String)
            Dim ca As New CultureInfo("en-CA")
            outHwText = tickHwText
            outAnchorUtcText = storedAnchorUtcText

            Dim storedHw As DateTime, tickHw As DateTime
            ' Fail-safe, mirroring AdvanceHighWater: an unparseable/tampered mark is left
            ' exactly as the tick left it. It is coupled to an already-failing MAC, so the
            ' block holds; we never fabricate a fresh, MAC-shaped value here.
            If Not DateTime.TryParse(storedHwText, ca, DateTimeStyles.None, storedHw) Then Return
            If Not DateTime.TryParse(tickHwText, ca, DateTimeStyles.None, tickHw) Then Return

            ' What the monotonic rule already credited this tick, and therefore how far
            ' the anchor must move to stay the UTC coordinate of the mark.
            Dim tickAdvance As Long = CLng((tickHw - storedHw).TotalSeconds)
            If tickAdvance < 0 Then tickAdvance = 0

            Dim anchor As DateTime
            Dim haveAnchor As Boolean = TryParseUtc(storedAnchorUtcText, anchor)
            If haveAnchor Then outAnchorUtcText = FormatUtc(anchor.AddSeconds(tickAdvance))

            If trustedNowUtcText = "" Then Return

            ' A reading with no usable anchor cannot credit anything - there is no
            ' instant to measure from - but it CAN seed the anchor so the next probe
            ' works. Seeding earns zero credit, which is the point.
            If Not haveAnchor Then
                outAnchorUtcText = trustedNowUtcText
                Return
            End If

            Dim realElapsed As Long = ElapsedSinceAnchor(storedAnchorUtcText, trustedNowUtcText, maxCreditSeconds)

            ' The mark: storedHw + the REAL elapsed, but never behind what the tick
            ' already gave. Taking the max is what makes a witness that under-reports
            ' harmless - it can only fail to add time, never remove it.
            Dim credited As DateTime = storedHw.AddSeconds(realElapsed)
            If credited > tickHw Then outHwText = credited.ToString(ca)

            ' The anchor: likewise the later of the corroborated reading and the anchor
            ' the tick alone would have produced, so the pair stays consistent even if
            ' the mark ran ahead of the witnesses.
            Dim trustedNow As DateTime
            If TryParseUtc(trustedNowUtcText, trustedNow) Then
                Dim tickAnchor As DateTime = anchor.AddSeconds(tickAdvance)
                outAnchorUtcText = FormatUtc(If(trustedNow > tickAnchor, trustedNow, tickAnchor))
            End If
        End Sub

        ' ------------------------- IMPURE (the network seam) -------------------------

        ' HEAD each witness and collect its Date header. Never throws. Returns the
        ' corroborated UTC 'now' or "" - one dead witness is not a failure, the quorum
        ' decides. Not unit-tested (it is live network I/O, like the DPAPI and SCM
        ' seams); everything it feeds is.
        Friend Function ProbeWitnesses(ByVal timeoutMs As Integer) As String
            Dim readings As New List(Of DateTime)
            Try
                Using handler As New HttpClientHandler()
                    ' No redirects: a 301/302 still carries a Date header, and following
                    ' one would let a redirect chain choose which host actually answers.
                    handler.AllowAutoRedirect = False
                    ' NOTE: ServerCertificateCustomValidationCallback is deliberately NOT
                    ' set. TLS validation is the only thing standing between this probe
                    ' and a forged Date served from 127.0.0.1 by way of the hosts file
                    ' MonkMode itself writes. Never relax it.
                    Using client As New HttpClient(handler)
                        client.Timeout = TimeSpan.FromMilliseconds(timeoutMs)
                        For Each url As String In WitnessUrls
                            Try
                                Using req As New HttpRequestMessage(HttpMethod.Head, url)
                                    Using resp As HttpResponseMessage = client.Send(req)
                                        If resp.Headers.Date.HasValue Then
                                            readings.Add(resp.Headers.Date.Value.UtcDateTime)
                                        End If
                                    End Using
                                End Using
                            Catch ex As Exception
                                ' This witness is unreachable, blocked, or slow. Fine.
                            End Try
                        Next
                    End Using
                End Using
            Catch ex As Exception
            End Try
            Return CorroboratedUtc(readings, MinWitnesses, WitnessSpreadCeilingSeconds)
        End Function

    End Module

    ' The probe's scheduler. Exists for one reason: the 10s enforcement tick may NEVER
    ' block on a network call. RequestIfDue starts at most one background probe and
    ' returns immediately; TryTakeReading collects whatever a previous probe finished,
    ' so a reading is consumed by a LATER tick than the one that asked for it. That is
    ' why nothing here is on the critical path - a probe that hangs for its full timeout
    ' costs the block nothing at all.
    '
    ' Cadence is measured on Environment.TickCount64, not the wall clock, so the same
    ' clock manipulation this whole feature defends against cannot be used to force the
    ' probe to hammer the network either.
    Friend Class TrustedTimeProbe

        Private ReadOnly gate As New Object()
        Private inFlight As Boolean = False
        Private lastAttemptMono As Long = 0
        Private attempted As Boolean = False
        Private everSucceeded As Boolean = False
        Private pending As String = ""

        ' Start a probe if none is running and one is due. Never blocks, never throws.
        '
        ' anchorMissing is the config's state, not ours: an UNSEEDED [Time] TrustedUtc
        ' means no downtime can be credited at all until a reading arrives, so those
        ' probes stay on the fast cadence however many have already succeeded. Without
        ' this, arming a block and immediately closing the laptop would wait out the full
        ' steady interval before the anchor existed - and the downtime before the anchor
        ' is established is downtime that can never be credited.
        Friend Sub RequestIfDue(ByVal nowMono As Long, ByVal anchorMissing As Boolean)
            SyncLock gate
                If inFlight Then Return
                If attempted Then
                    Dim interval As Long = TrustedTime.ProbeIntervalMs(everSucceeded, anchorMissing)
                    ' Guard the subtraction against a TickCount64 that went backwards
                    ' (it should not, but a 0 elapsed simply defers - fail-safe either way).
                    Dim elapsed As Long = nowMono - lastAttemptMono
                    If elapsed >= 0 AndAlso elapsed < interval Then Return
                End If
                inFlight = True
                attempted = True
                lastAttemptMono = nowMono
            End SyncLock

            Try
                Threading.Tasks.Task.Run(
                    Sub()
                        Dim reading As String = ""
                        Try
                            reading = TrustedTime.ProbeWitnesses(TrustedTime.ProbeTimeoutMs)
                        Catch ex As Exception
                        End Try
                        SyncLock gate
                            If reading <> "" Then
                                pending = reading
                                everSucceeded = True
                            End If
                            inFlight = False
                        End SyncLock
                    End Sub)
            Catch ex As Exception
                ' Could not even queue the work (shutdown, thread-pool starvation).
                ' Release the flag so a later tick can try again.
                SyncLock gate
                    inFlight = False
                End SyncLock
            End Try
        End Sub

        ' Take and clear the last completed reading; "" when there is none. Consumed
        ' once, so a stale reading can never be credited twice.
        Friend Function TryTakeReading() As String
            SyncLock gate
                Dim r As String = pending
                pending = ""
                Return r
            End SyncLock
        End Function

    End Class

End Namespace
