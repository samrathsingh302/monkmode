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

'    MonkMode - notifier: notification message builders (D4)
'
'    Pure, fail-soft builders + derivations for the richer tray-toast
'    notifications the notifier shows over a MANUAL block's life:
'      - block active on launch  (arm / logon / guardian relaunch)
'      - cooling-off started     (the self-serve `unblock` wait began)
'      - block-active reminder   (a periodic "still blocked" nudge)
'      - block ended             (expiry / lift)
'    Every function is Friend Shared and side-effect-free (no disk, no DPAPI, no
'    UI) so the whole surface is unit-tested cross-assembly (mm_notify.
'    Notifications.X via InternalsVisibleTo), exactly like Form1's
'    ComputeCompensatedUntil / schedule parity gates. Form1 owns the live wiring
'    (WHEN to call these + the ShowBalloonTip + the latches); this file owns ONLY
'    the strings + their derivations, so a wording tweak never touches the
'    notifier's enforcement-adjacent app-kill / clock-comp code.
'
'    Two of the D4 design's listed notifications are DELIBERATELY not built here,
'    checked against the SHIPPED model rather than assumed (fact-discipline):
'      - "schedule opening soon": deriving "minutes until the next window opens"
'        in the notifier would need a 5th byte-for-byte parity copy of the
'        service's window-open math for a purely cosmetic toast. Out of
'        proportion for D4; the armed schedule is surfaced from the CLI instead
'        (status / `schedule --show`, D5).
'      - "code-rotation-needed": the C3b partner code is rotate-on-use PER BLOCK
'        (each arm mints its OWN one-time code; C6a mints no account-level code),
'        and the service stamps [Partner] UnlockedAt only WHEN a code lifts the
'        block - i.e. coincident with teardown. There is no live
'        "blocked-but-code-stale" state for such a toast to reference, so none is
'        invented.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization

Friend Module Notifications

    ' The fixed tray title for every MonkMode toast (unchanged from the historical
    ' ShowBalloonTip title, so nothing about the balloon identity moves).
    Friend Const ToastTitle As String = "MonkMode"

    ' Config datetimes are en-CA (every party parses with en-CA); the monotonic
    ' [Time] HighWater / deadline strings this module reasons over are en-CA too.
    Private ReadOnly CA As New CultureInfo("en-CA")

    ' Human-friendly, SHORT remaining duration: "2h 30m", "45m", "1d 3h", "1h", or
    ' "<1m" for any positive-but-sub-minute span; "0m" for a zero/negative (already
    ' elapsed) span. Never throws. Same d/h/m vocabulary as the CLI's
    ' Program.Humanize so the toasts and `status` speak one dialect; kept as its
    ' own pure copy because the notifier can't reference the CLI assembly.
    Friend Function HumanizeShort(ByVal ts As TimeSpan) As String
        If ts.TotalSeconds <= 0 Then Return "0m"
        Dim parts As New List(Of String)
        If ts.Days > 0 Then parts.Add(ts.Days & "d")
        If ts.Hours > 0 Then parts.Add(ts.Hours & "h")
        If ts.Minutes > 0 Then parts.Add(ts.Minutes & "m")
        If parts.Count = 0 Then Return "<1m"
        Return String.Join(" ", parts)
    End Function

    ' Count the entries in a PackList/PackApps-packed field ("a.com;b.com;" or the
    ' decrypted "chrome.exe;foo.exe;"). "null" / "" / Nothing => 0. Splits on ';',
    ' trims, drops empties - so the trailing ';' the packers append, and any stray
    ' blank, are never miscounted. Pure; never throws.
    Friend Function CountPackedList(ByVal packed As String) As Integer
        If packed Is Nothing Then Return 0
        Dim t As String = packed.Trim()
        If t = "" OrElse t = "null" Then Return 0
        Dim n As Integer = 0
        For Each tok As String In t.Split(";"c)
            If tok.Trim() <> "" Then n += 1
        Next
        Return n
    End Function

    ' Monotonic remaining from a deadline vs the [Time] HighWater mark, both en-CA
    ' PLAINTEXT (the SAME timeline the service enforces expiry/cooling-off on - NOT
    ' the wall clock). Nothing if either string is empty/unparseable, so the caller
    ' SKIPS the toast rather than show a bogus duration. A deadline at/behind the
    ' mark yields a zero/negative span (callers gate on > 0).
    Friend Function RemainingFromMark(ByVal deadlineEnCa As String, ByVal highWaterEnCa As String) As TimeSpan?
        Dim deadline As DateTime, mark As DateTime
        If Not DateTime.TryParse(deadlineEnCa, CA, DateTimeStyles.None, deadline) Then Return Nothing
        If Not DateTime.TryParse(highWaterEnCa, CA, DateTimeStyles.None, mark) Then Return Nothing
        Return deadline - mark
    End Function

    ' Should the periodic "still blocked" reminder fire at nowTick, given the tick
    ' of the last reminder (or of block-start, which Form1 seeds at launch so the
    ' first nudge waits a full interval after arm)? True once >= intervalMs of
    ' MONOTONIC time (Environment.TickCount64) has elapsed. A non-positive delta
    ' (clock stall) reads as "not yet" - it never spams. Pure; the caller resets
    ' the anchor on each fire.
    Friend Function ShouldFirePeriodicReminder(ByVal nowTick As Long, ByVal lastTick As Long, ByVal intervalMs As Long) As Boolean
        Return (nowTick - lastTick) >= intervalMs
    End Function

    ' "N sites and M apps" / "N sites" / "M apps" / "Your block" (the last only if
    ' both counts are 0 - a real block always has >= 1, so it is a fail-soft
    ' fallback, never the normal path). Uses the count + a pluralised noun.
    Private Function DescribeSubject(ByVal siteCount As Integer, ByVal appCount As Integer) As String
        If siteCount > 0 AndAlso appCount > 0 Then Return CountNoun(siteCount, "site") & " and " & CountNoun(appCount, "app")
        If siteCount > 0 Then Return CountNoun(siteCount, "site")
        If appCount > 0 Then Return CountNoun(appCount, "app")
        Return "Your block"
    End Function

    ' "1 site" / "0 sites" / "3 sites" - count + noun with an English plural 's'.
    Private Function CountNoun(ByVal n As Integer, ByVal singular As String) As String
        Return n & " " & singular & If(n = 1, "", "s")
    End Function

    ' The launch toast for an ACTIVE MANUAL block: "MonkMode is active - 3 sites and
    ' 2 apps blocked until 2026-07-08 17:00 (about 2h 30m left)." A committed block
    ' appends the code-only-exit note. remaining is Nothing/<=0 => the "(about X
    ' left)" clause is omitted (an unreadable HighWater must not print a bogus
    ' span). Worded so it is TRUE whether shown at arm, at logon, or on a guardian
    ' relaunch (it states the standing truth "active until X", never a stale
    ' "started just now").
    Friend Function BlockActiveMessage(ByVal siteCount As Integer, ByVal appCount As Integer, ByVal untilWall As DateTime, ByVal committed As Boolean, ByVal remaining As TimeSpan?) As String
        Dim left As String = ""
        If remaining IsNot Nothing AndAlso remaining.Value.TotalSeconds > 0 Then
            left = " (about " & HumanizeShort(remaining.Value) & " left)"
        End If
        Dim tail As String = If(committed, " Committed block - the accountability code is the only early exit.", "")
        Return "MonkMode is active - " & DescribeSubject(siteCount, appCount) & " blocked until " & FormatWall(untilWall) & left & "." & tail
    End Function

    ' v1.1 S4: the AGGREGATE active toast for a multi-block machine - "3 blocks active ·
    ' 45m left". v10 lets up to 8 slots be armed at once, and the single-block wording above
    ' would name ONE deadline and ONE site/app count for all of them (it is built from the v9
    ' single-block mirror), which is true of the mirror and false of the machine. This states
    ' only what is certain: how many blocks are armed, and how long the SHORTEST of them has
    ' left - so the first thing to end is the number the user sees.
    '
    ' shortest is Nothing / <= 0 => the "· X left" clause is dropped entirely rather than
    ' printing a bogus or "0m" span (the same rule BlockActiveMessage follows for its "(about
    ' X left)"). Pure; the count is rendered through the shared CountNoun so "1 block" /
    ' "2 blocks" pluralise exactly like the site/app counts. Never throws.
    Friend Function AggregateActiveMessage(ByVal blockCount As Integer, ByVal shortest As TimeSpan?) As String
        Dim left As String = ""
        If shortest IsNot Nothing AndAlso shortest.Value.TotalSeconds > 0 Then
            left = " · " & HumanizeShort(shortest.Value) & " left"
        End If
        Return CountNoun(blockCount, "block") & " active" & left
    End Function

    ' The cooling-off-started toast: the self-serve `unblock` wait has begun and the
    ' block stays fully enforced until it lifts itself. remaining is the monotonic
    ' active-time left (deadline - HighWater), rendered short.
    Friend Function CoolOffStartedMessage(ByVal remaining As TimeSpan) As String
        Return "Cooling-off started - the block lifts in about " & HumanizeShort(remaining) & " of active machine time. Run 'monkmode unblock --cancel' to stay blocked."
    End Function

    ' The periodic "still blocked" nudge shown during a long manual block.
    Friend Function BlockActiveReminderMessage(ByVal remaining As TimeSpan) As String
        Return "Still in the zone - about " & HumanizeShort(remaining) & " left on your block. Stay strong."
    End Function

    ' The block-ended toast. Pinned to the EXACT historical string (this centralises
    ' the one toast the notifier already showed; a test pins the wording so a future
    ' edit is deliberate).
    Friend Function BlockEndedMessage() As String
        Return "Your block has ended. You're free — stay strong."
    End Function

    ' Deterministic wall-clock render for a toast ("2026-07-08 17:00"): an explicit
    ' invariant pattern (not a culture-dependent short form) so the string is stable
    ' across machines - and unit-testable by literal.
    Private Function FormatWall(ByVal dt As DateTime) As String
        Return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
    End Function

    ' ================= D4b: persistent Action-Centre toast delivery =================
    '
    ' The four builders above produce the toast STRINGS; this section produces the toast
    ' PAYLOAD (a WinRT ToastGeneric XML document) and the fail-safe delivery contract - both
    ' PURE (no WinRT, no disk, no registration) so they are unit-tested cross-assembly exactly
    ' like the strings. Form1 owns the live WinRT call (ToastNotificationManagerCompat) + the
    ' balloon fallback; this module owns only the payload + the orchestration decision.

    ' How long a delivered toast lingers in the Action Centre before Windows evicts it. The whole
    ' point of D4b: on the 10/07/2026 sitting all three balloons fired but DND/full-screen
    ' suppressed them and they were gone forever. A persistent toast with a generous expiry means
    ' a MISSED block-start / cooling-off / expiry toast is still viewable hours later. 24h is long
    ' enough to survive a focus session or a night, without lingering indefinitely (a stale
    ' "block ended" toast a day later is harmless; a week later is clutter). Set on the WinRT
    ' ToastNotification.ExpirationTime by Form1; named here so the value lives in one place.
    Friend Const ToastExpiryHours As Integer = 24

    ' XML-escape the five predefined entities for safe embedding in toast element text. Toast
    ' bodies are our own fixed strings today (no '&' / '<' / '>'), but escaping is the correct,
    ' future-proof contract: a body that ever gains an ampersand or angle bracket must not produce
    ' a malformed document that throws in XmlDocument.LoadXml (which would then drop to the balloon
    ' fallback and lose the persistence D4b exists to add). Order matters: '&' first. Never throws;
    ' Nothing => "".
    Friend Function EscapeXml(ByVal s As String) As String
        If s Is Nothing Then Return ""
        Return s.Replace("&", "&amp;") _
                .Replace("<", "&lt;") _
                .Replace(">", "&gt;") _
                .Replace("""", "&quot;") _
                .Replace("'", "&apos;")
    End Function

    ' Build the WinRT ToastGeneric payload for a title + body. The first <text> is the header line
    ' (title), the second the message (body) - the same two-line shape as the old balloon
    ' (title="MonkMode", body=the message), so nothing about the message identity moves. Default
    ' scenario deliberately: a plain toast already lands in and PERSISTS in the Action Centre;
    ' scenario="reminder"/"alarm" would demand action buttons and nag on-screen, which is wrong for
    ' an informational block notice. Expiry is applied to the ToastNotification object, not the XML.
    ' Pure + deterministic (both texts escaped) so the emitted document is always well-formed and
    ' unit-testable by parse + literal. Never throws.
    Friend Function BuildToastXml(ByVal title As String, ByVal body As String) As String
        Return "<toast><visual><binding template=""ToastGeneric"">" &
               "<text>" & EscapeXml(title) & "</text>" &
               "<text>" & EscapeXml(body) & "</text>" &
               "</binding></visual></toast>"
    End Function

    ' Which delivery path a toast took - reported so a test can PROVE the fail-safe fallback fires
    ' (not merely that no exception escaped). None = both paths threw and were swallowed.
    Friend Enum ToastDelivery
        Persistent
        Balloon
        None
    End Enum

    ' The fail-safe delivery contract (requirement: a notification failure must NEVER crash the
    ' notifier or affect enforcement). Try the persistent (WinRT Action-Centre) path; if it throws
    ' for ANY reason - no WinRT support, shortcut/registration failure, COM error - fall back to the
    ' transient balloon; if that ALSO throws, swallow and report None. A toast is cosmetic: it may
    ' never bubble an exception into the poll/load path, and the notifier holds no enforcement, so
    ' losing a toast entirely is acceptable where crashing is not. Pure orchestration over injected
    ' delegates so the whole try/fallback decision is unit-tested with a throwing 'persistent'.
    Friend Function DeliverWithFallback(ByVal body As String,
                                        ByVal persistent As Action(Of String),
                                        ByVal balloon As Action(Of String)) As ToastDelivery
        Try
            persistent(body)
            Return ToastDelivery.Persistent
        Catch ex As Exception
        End Try
        Try
            balloon(body)
            Return ToastDelivery.Balloon
        Catch ex As Exception
        End Try
        Return ToastDelivery.None
    End Function

End Module
