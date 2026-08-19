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

'    MonkMode - notifier (mm_notify)
'
'    A hidden user-session agent (launched by the CLI and registered in
'    HKCU\...\Run). Responsibilities:
'      - kill blocked apps that run in the user session (the service only kills
'        session 0), using the encrypted [Process] List in the config;
'      - compensate for system-clock changes so a block can't be shortened by
'        rolling the clock (cooperates with the service via [Time] TimeChanging);
'      - when the block ends, show a lightweight tray-balloon toast, then remove
'        its own Run entry and exit.
'
'    Replaces the old WinForms popup (mm_popup) and the mm_notify2 twin.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports Microsoft.Toolkit.Uwp.Notifications
Imports Microsoft.Win32
Imports System.Diagnostics
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Security.Cryptography
Imports System.Windows.Forms
Imports mm_notify.IniFile

Public Class Form1
    Inherits System.Windows.Forms.Form

    Private ReadOnly enc As New Simple3Des("mm_textbox")
    ' Config datetimes are en-CA strings (the service parses with en-CA); always
    ' pass this explicitly — SystemEvents handlers run on a system broadcast
    ' thread, not the UI thread whose culture the constructor sets. Shared so
    ' the testable ComputeCompensatedUntil helper can use it.
    Private Shared ReadOnly CA As New CultureInfo("en-CA")
    Private ReadOnly tray As New NotifyIcon()
    ' v1.1 S7b (P53): the tray's context menu - EXACTLY the two items
    ' TrayMenuItemLabels names, built from that one list so the pinned test and the
    ' live menu can never drift. trayBlockedTodayItem is the DISABLED count label,
    ' kept as a field only so the 5s poll can refresh its text in place.
    Private ReadOnly trayMenu As New ContextMenuStrip()
    Private trayBlockedTodayItem As ToolStripMenuItem = Nothing
    Private WithEvents pollTimer As New Timer()
    Private WithEvents appKillTimer As New Timer()
    Private WithEvents closeTimer As New Timer()
    ' v1.1 S7 (F2b, P62): the URL watcher's own 2s beat. Separate from appKillTimer
    ' deliberately - the two do unrelated work and the watcher's pass runs off the UI
    ' thread, so sharing a tick would only entangle their failure modes.
    Private WithEvents urlWatchTimer As New Timer()
    Private iniProcessList As String = ""

    ' v1.1 S7 URL-watch state. urlLastActionTick is the Environment.TickCount64 of the
    ' last redirect ATTEMPT (P60's 5s cooldown is measured off it - attempt, not success,
    ' so a browser that keeps refusing the SetValue is retried once per cooldown rather
    ' than once per beat). urlWatchInFlight is a 0/1 re-entrancy latch: at most one pass
    ' is ever in flight, so a UIA read that blocks for seconds cannot pile up passes.
    ' Both are touched from a pool thread, hence Interlocked throughout.
    Private urlLastActionTick As Long = 0
    Private urlWatchInFlight As Integer = 0

    ' v1.1 S7d (P50/P51): the loopback block page. The server object exists for the
    ' life of the notifier but binds NOTHING until a block is actually held, and its
    ' socket work all happens on its own background thread - the fields here are the
    ' UI side only. blockPageBindAnchorTick is the Environment.TickCount64 of the last
    ' bind ATTEMPT, feeding P50's once-a-minute re-bind gate; it is seeded at Load one
    ' full interval in the past so the FIRST attempt is immediate (an unseeded 0 would
    ' otherwise delay the first bind by up to a minute on a freshly booted machine,
    ' where TickCount64 is itself still under 60000).
    Private ReadOnly pageServer As New BlockPageServer()
    Private blockPageBindAnchorTick As Long = 0

    ' D4 notification state (all in-memory; the notifier persists NOTHING new - no
    ' MAC field, no write that could race the service):
    '   coolOffAnnounced - latches the "cooling-off started" toast so the 5s poll
    '     shows it once per cooling-off, not every tick; reset when CoolOffUntil
    '     clears so a cancel+re-request announces afresh.
    '   reminderAnchorTick - the Environment.TickCount64 of block-start (seeded at
    '     Load) or of the last periodic reminder; the pure ShouldFirePeriodicReminder
    '     gate spaces the "still blocked" nudge off monotonic time.
    Private coolOffAnnounced As Boolean = False
    Private reminderAnchorTick As Long = 0
    ' The periodic block-active reminder cadence. 2h keeps a long block gently in
    ' view without nagging; a short block simply ends before the first one fires.
    ' Tunable in one place (wants eyeballing at the CV/E3 elevated smoke sitting).
    Private Const ReminderIntervalMs As Long = 2L * 60L * 60L * 1000L

    Private Function IniPath() As String
        Return Path.Combine(Application.StartupPath, "monkmode_settings.ini")
    End Function

    Public Sub New()
        System.Threading.Thread.CurrentThread.CurrentCulture = New CultureInfo("en-CA")
        System.Threading.Thread.CurrentThread.CurrentUICulture = New CultureInfo("en-CA")

        Me.FormBorderStyle = FormBorderStyle.FixedToolWindow
        Me.ShowInTaskbar = False
        Me.WindowState = FormWindowState.Minimized
        Me.Opacity = 0
        Me.Size = New Size(0, 0)

        tray.Icon = SystemIcons.Information
        tray.Text = "MonkMode"

        ' v1.1 S7b (P53): the quick-status menu. Item 0 is the only ACTION the tray
        ' offers and it merely shows a toast; item 1 is a disabled label. There is NO
        ' Exit / Close / Quit / Pause item, and there never may be: this process
        ' performs the user-session app-kill AND (S7) the URL watcher, so a one-click
        ' exit in the tray would be a mouse-gesture self-bypass of both. ExitNotifier
        ' stays reachable ONLY from the block-ended path.
        Dim trayLabels As List(Of String) = TrayMenuItemLabels(0)
        Dim statusItem As New ToolStripMenuItem(trayLabels(0))
        AddHandler statusItem.Click, AddressOf TrayStatus_Click
        trayMenu.Items.Add(statusItem)
        trayBlockedTodayItem = New ToolStripMenuItem(trayLabels(1))
        trayBlockedTodayItem.Enabled = False
        trayMenu.Items.Add(trayBlockedTodayItem)
        tray.ContextMenuStrip = trayMenu
        AddHandler tray.DoubleClick, AddressOf TrayStatus_Click

        tray.Visible = True

        pollTimer.Interval = 5000
        appKillTimer.Interval = 2000
        closeTimer.Interval = 6000
        urlWatchTimer.Interval = 2000        ' P62
    End Sub

    Private Sub Form1_Load(ByVal sender As Object, ByVal e As EventArgs) Handles MyBase.Load
        Me.Hide()

        AddHandler SystemEvents.TimeChanged, AddressOf SystemEvents_TimeChanged

        Dim done As String = "", needsAlerted As String = ""
        Dim isScheduleArmed As Boolean = False
        Dim launchMsg As String = ""
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Dim macValid As Boolean = ConfigMacIsValidForIni(ini)
            done = ini.GetKeyValue("User", "Done")
            needsAlerted = ini.GetKeyValue("User", "NeedsAlerted")
            iniProcessList = ini.GetKeyValue("Process", "List")
            If StrComp(iniProcessList, "null") <> 0 Then iniProcessList = enc.DecryptData(iniProcessList)
            ' C5b (c3): don't announce "block ended" while a schedule is armed (design §6.4) - a
            ' schedule-only block's past-Until sentinel + between-windows idle must not toast.
            isScheduleArmed = ScheduleArmed(macValid, ini.GetKeyValue("Schedule", "Spec"))
            ' D4: build the launch toast for an ACTIVE MANUAL block (shown once below, after we
            ' decide to run). MAC-valid + not-a-schedule + not-already-ended only: a tampered/garbage
            ' config must announce nothing, a schedule's per-window toast is deferred, and an ended
            ' block falls through to AnnounceBlockEnded. Fail-soft: an unparseable Until yields "".
            If macValid AndAlso Not isScheduleArmed AndAlso StrComp("yes", done) <> 0 Then
                launchMsg = BuildManualLaunchToast(ini)
            End If
        Catch ex As Exception
            ExitNotifier()
            Return
        End Try

        If StrComp("yes", done) = 0 AndAlso Not isScheduleArmed Then
            If StrComp(needsAlerted, "no") = 0 Then
                ExitNotifier()
            Else
                AnnounceBlockEnded()
            End If
            Return
        End If

        pollTimer.Start()
        appKillTimer.Start()
        urlWatchTimer.Start()   ' v1.1 S7 (P62): same lifecycle as the app-kill beat
        ' D4: seed the periodic-reminder anchor so the first "still blocked" nudge waits a full
        ' interval after this launch, then announce the active manual block once (best-effort).
        reminderAnchorTick = Environment.TickCount64
        If launchMsg <> "" Then ShowToast(launchMsg)
        RefreshTray()   ' v1.1 S7b (P52): seed the tooltip before the first 5s poll
        ' v1.1 S7d (P50): make the first bind attempt due immediately, then try it.
        blockPageBindAnchorTick = Environment.TickCount64 - BlockPage.RebindRetryIntervalMs
        RefreshBlockPage()
    End Sub

    Private Sub pollTimer_Tick(ByVal sender As Object, ByVal e As EventArgs) Handles pollTimer.Tick
        Dim done As String = "", needsAlerted As String = ""
        Dim isScheduleArmed As Boolean = False
        Dim coolOffToast As String = ""
        Dim reminderToast As String = ""
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Dim macValid As Boolean = ConfigMacIsValidForIni(ini)
            done = ini.GetKeyValue("User", "Done")
            needsAlerted = ini.GetKeyValue("User", "NeedsAlerted")
            ' C5b (c3): suppress the manual-expiry toast while a schedule is armed (design §6.4);
            ' announce only once the schedule is genuinely cleared (not armed -> stopMe -> Done=yes).
            isScheduleArmed = ScheduleArmed(macValid, ini.GetKeyValue("Schedule", "Spec"))
            ' D4: manual-block notifications (cooling-off started + the periodic active reminder).
            ' MAC-valid + not-a-schedule + not-already-ended only. Built here inside the single ini
            ' load, but SHOWN below AFTER the Catch so a toast can never disturb the expiry logic.
            If macValid AndAlso Not isScheduleArmed AndAlso StrComp("yes", done) <> 0 Then
                coolOffToast = BuildCoolOffToast(ini)
                reminderToast = BuildReminderToast(ini)
            End If
        Catch ex As Exception
            Return
        End Try

        ' D4: fire the manual-block notifications (best-effort; each self-latches so it can't spam
        ' every 5s poll). Shown before the expiry branch below so an early ExitNotifier /
        ' AnnounceBlockEnded return can never drop a notification (both stay "" on an ended tick
        ' anyway - they are gated off Done<>"yes" above - so this ordering is simply the safe one).
        If coolOffToast <> "" Then ShowToast(coolOffToast)
        If reminderToast <> "" Then ShowToast(reminderToast)

        ' v1.1 S7b (P52): refresh the tray quick-status on the existing 5s beat. Its
        ' OWN ini read, deliberately not folded into the load above: the expiry logic
        ' in this method is the notifier's one load-bearing decision, and a cosmetic
        ' tooltip must not be able to change what it reads or when it returns.
        RefreshTray()

        ' v1.1 S7d (P50): same beat, same stance as the tray - its own ini read, its
        ' own swallow, and no ability to change what the expiry branch below reads.
        RefreshBlockPage()

        If StrComp("yes", done) = 0 AndAlso Not isScheduleArmed Then
            pollTimer.Stop()
            If StrComp(needsAlerted, "no") = 0 Then
                ExitNotifier()
            Else
                AnnounceBlockEnded()
            End If
        End If
    End Sub

    ' D4 (best-effort, fail-soft): build the launch toast for an ACTIVE MANUAL block from an
    ' already-loaded, MAC-valid ini. "" (no toast) if [Time] Until is unreadable/unparseable - a
    ' launch announcement must never print a bogus deadline. remaining is the monotonic Until -
    ' HighWater (Nothing => the "(about X left)" clause is dropped). Reuses iniProcessList (already
    ' decrypted in Form1_Load) for the app count. Never throws.
    Private Function BuildManualLaunchToast(ByVal ini As IniFile) As String
        Try
            ' v1.1 S4: with MORE THAN ONE block armed the single-block wording below would
            ' state one deadline and one site/app count for all of them - true of the v9
            ' mirror, false of the machine. Switch to the aggregate, which names the count and
            ' the SHORTEST remaining span instead. Exactly one block keeps the richer, older
            ' wording (and its pinned tests) unchanged.
            Dim slotCount As Integer = RawSlotBlockCount(ini)
            If slotCount > 1 Then Return Notifications.AggregateActiveMessage(slotCount, ShortestSlotRemaining(ini))
            Dim untilStr As String = enc.DecryptData(ini.GetKeyValue("Time", "Until"))
            Dim untilDt As DateTime
            If Not DateTime.TryParse(untilStr, CA, DateTimeStyles.None, untilDt) Then Return ""
            Dim remaining As TimeSpan? = Notifications.RemainingFromMark(untilStr, enc.DecryptData(ini.GetKeyValue("Time", "HighWater")))
            Dim siteCount As Integer = Notifications.CountPackedList(ini.GetKeyValue("User", "CustomSites"))
            Dim appCount As Integer = Notifications.CountPackedList(iniProcessList)
            Dim committed As Boolean = String.Equals(If(ini.GetKeyValue("Commit", "Committed"), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            Return Notifications.BlockActiveMessage(siteCount, appCount, untilDt, committed, remaining)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ' D4 (best-effort, fail-soft): the "cooling-off started" toast, latched. "" unless a cooling-off
    ' is newly pending: an empty [Time] CoolOffUntil resets the latch (so a cancel + re-request
    ' announces again); a set-and-already-announced deadline is silent; a set-but-unreadable/elapsed
    ' remaining shows nothing. remaining is the monotonic CoolOffUntil - HighWater. Never throws.
    Private Function BuildCoolOffToast(ByVal ini As IniFile) As String
        Try
            Dim coolOffEnc As String = ini.GetKeyValue("Time", "CoolOffUntil")
            If coolOffEnc = "" Then
                coolOffAnnounced = False
                Return ""
            End If
            If coolOffAnnounced Then Return ""
            Dim remaining As TimeSpan? = Notifications.RemainingFromMark(enc.DecryptData(coolOffEnc), enc.DecryptData(ini.GetKeyValue("Time", "HighWater")))
            If remaining Is Nothing OrElse remaining.Value.TotalSeconds <= 0 Then Return ""
            coolOffAnnounced = True
            Return Notifications.CoolOffStartedMessage(remaining.Value)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ' D4 (best-effort, fail-soft): the periodic "still blocked" reminder, gated on the monotonic
    ' ShouldFirePeriodicReminder interval. The anchor is reset whenever the interval elapses (even
    ' if the remaining span is unreadable and no toast is shown) so a bad tick still spaces out the
    ' next attempt. "" when it is not yet time or the remaining span is unreadable/elapsed.
    Private Function BuildReminderToast(ByVal ini As IniFile) As String
        Try
            Dim nowTick As Long = Environment.TickCount64
            If Not Notifications.ShouldFirePeriodicReminder(nowTick, reminderAnchorTick, ReminderIntervalMs) Then Return ""
            reminderAnchorTick = nowTick
            ' v1.1 S4: same switch as the launch toast, taken AFTER the anchor reset so the
            ' 2h cadence is byte-identical whichever wording fires.
            Dim slotCount As Integer = RawSlotBlockCount(ini)
            If slotCount > 1 Then Return Notifications.AggregateActiveMessage(slotCount, ShortestSlotRemaining(ini))
            Dim remaining As TimeSpan? = Notifications.RemainingFromMark(enc.DecryptData(ini.GetKeyValue("Time", "Until")), enc.DecryptData(ini.GetKeyValue("Time", "HighWater")))
            If remaining Is Nothing OrElse remaining.Value.TotalSeconds <= 0 Then Return ""
            Return Notifications.BlockActiveReminderMessage(remaining.Value)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ' v1.1 S4 (display-only): the SHORTEST still-running remaining span across the slots' own
    ' ends, measured against the monotonic [Time] HighWater - the same timeline the service
    ' enforces on, never the wall clock. Slots whose Until is absent (PENDING / schedule),
    ' unreadable or already elapsed are skipped; Nothing when none qualifies, and the caller
    ' then drops the "left" clause rather than print a bogus one. Fail-soft (a toast must never
    ' throw into the poll path) and enforcement-free.
    Private Function ShortestSlotRemaining(ByVal ini As IniFile) As TimeSpan?
        Try
            Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
            Dim highWater As String = If(highWaterEnc = "", "", enc.DecryptData(highWaterEnc))
            Dim best As TimeSpan? = Nothing
            For pos As Integer = 1 To ConfigIntegrity.MaxSlots
                Dim untilEnc As String = ini.GetKeyValue("Slot" & pos.ToString(CultureInfo.InvariantCulture), "Until")
                If untilEnc = "" Then Continue For
                Dim remaining As TimeSpan? = Notifications.RemainingFromMark(enc.DecryptData(untilEnc), highWater)
                If remaining Is Nothing OrElse remaining.Value.TotalSeconds <= 0 Then Continue For
                If best Is Nothing OrElse remaining.Value < best.Value Then best = remaining
            Next
            Return best
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ' ============ v1.1 S7b (F3): the tray quick-status (P52 tooltip, P53 menu) ============
    '
    ' Everything below is DISPLAY. It reads the config (already public to this process)
    ' and the stats sidecar, and it writes nothing at all. The tray offers exactly one
    ' action - show a toast - so there is no gesture here that stops, pauses or exits
    ' the notifier, and therefore none that stops app-kill or the URL watcher.

    ' The historical NotifyIcon.Text ceiling. 63 characters (64 including the null) was
    ' a hard Win32 limit for years; modern frameworks allow more, but truncating is
    ' safe on every one of them, so we truncate rather than probe.
    Friend Const TrayTextMaxLength As Integer = 63

    ' The one action label the menu carries. A const so the "no exit item" test can
    ' assert on the exact set rather than on a literal typed twice.
    Friend Const TrayStatusMenuLabel As String = "Status"

    ' P52's summary, UNTRUNCATED: "MonkMode - 2 blocks · 45m left · 7 blocked today".
    ' The "· X left" clause is dropped when the shortest remaining span is unknown or
    ' already elapsed (the same rule every other MonkMode message follows - never print
    ' a bogus deadline). Pure; never throws.
    Friend Shared Function BuildTraySummary(ByVal blockCount As Integer,
                                            ByVal shortest As TimeSpan?,
                                            ByVal blockedToday As Integer) As String
        Dim sb As New System.Text.StringBuilder("MonkMode - ")
        sb.Append(blockCount.ToString(CultureInfo.InvariantCulture))
        sb.Append(" block")
        If blockCount <> 1 Then sb.Append("s")
        If shortest IsNot Nothing AndAlso shortest.Value.TotalSeconds > 0 Then
            sb.Append(" · ")
            sb.Append(Notifications.HumanizeShort(shortest.Value))
            sb.Append(" left")
        End If
        sb.Append(" · ")
        sb.Append(blockedToday.ToString(CultureInfo.InvariantCulture))
        sb.Append(" blocked today")
        Return sb.ToString()
    End Function

    ' P52: the summary as the TOOLTIP sees it - hard-truncated to TrayTextMaxLength.
    ' Pure; never throws.
    Friend Shared Function TruncateTrayText(ByVal text As String) As String
        Dim s As String = If(text, "")
        If s.Length <= TrayTextMaxLength Then Return s
        Return s.Substring(0, TrayTextMaxLength)
    End Function

    ' P53: the EXACT menu item set, in order - item 0 the Status action, item 1 the
    ' disabled count label. The live menu is built from this list and the pinned test
    ' asserts on it, so the one place to look for "can the tray kill the notifier?" is
    ' this function's return value. There is no third item, and no item whose label or
    ' behaviour exits anything. Pure; never throws.
    Friend Shared Function TrayMenuItemLabels(ByVal blockedToday As Integer) As List(Of String)
        Dim labels As New List(Of String)
        labels.Add(TrayStatusMenuLabel)
        labels.Add("Blocked today: " & blockedToday.ToString(CultureInfo.InvariantCulture))
        Return labels
    End Function

    ' P48 (display): how many attempts MonkMode stopped TODAY - app kills plus browser
    ' redirects, merged across both sidecars. A missing, corrupt or hostile sidecar
    ' reads as 0 (StatsSidecar is total), and 0 is a perfectly honest answer. Never
    ' throws.
    Private Function BlockedTodayCount() As Integer
        Try
            Dim today As StatsSidecar.Counts =
                StatsSidecar.TotalForDay(StatsSidecar.ReadMerged(), StatsSidecar.DayKeyFor(DateTime.Now))
            Return CInt(Math.Min(today.Kills + today.Redirects, CLng(Integer.MaxValue)))
        Catch ex As Exception
            Return 0
        End Try
    End Function

    ' Recompute the tooltip + the disabled count label. Best-effort: on ANY failure the
    ' tray simply keeps whatever it last showed. Called from Load and the 5s poll.
    Private Sub RefreshTray()
        Try
            Dim blockCount As Integer = 0
            Dim shortest As TimeSpan? = Nothing
            Try
                Dim ini As New IniFile
                ini.Load(IniPath())
                blockCount = RawSlotBlockCount(ini)
                shortest = ShortestSlotRemaining(ini)
            Catch ex As Exception
                ' An unreadable config leaves the counts at their zero defaults; the
                ' tooltip then states less, never something false.
            End Try
            Dim blockedToday As Integer = BlockedTodayCount()
            tray.Text = TruncateTrayText(BuildTraySummary(blockCount, shortest, blockedToday))
            If trayBlockedTodayItem IsNot Nothing Then
                trayBlockedTodayItem.Text = TrayMenuItemLabels(blockedToday)(1)
            End If
        Catch ex As Exception
        End Try
    End Sub

    ' P53: the ONLY thing the tray can do - show the same summary as a toast. Shared by
    ' the Status menu item and the double-click. Best-effort; shows nothing on failure.
    Private Sub TrayStatus_Click(ByVal sender As Object, ByVal e As EventArgs)
        Try
            Dim blockCount As Integer = 0
            Dim shortest As TimeSpan? = Nothing
            Try
                Dim ini As New IniFile
                ini.Load(IniPath())
                blockCount = RawSlotBlockCount(ini)
                shortest = ShortestSlotRemaining(ini)
            Catch ex As Exception
            End Try
            ShowToast(BuildTraySummary(blockCount, shortest, BlockedTodayCount()))
        Catch ex As Exception
        End Try
    End Sub

    ' ============ v1.1 S7d (P50/P51): the loopback block page lifecycle ============
    '
    ' DISPLAY, like the tray above it. The page is rendered HERE, on the UI thread's
    ' 5s beat, and handed to the server as finished bytes; the server's request path
    ' reads no file and writes no state (see BlockPage.vb). So this method is the only
    ' place the block page touches the config at all, and it touches it read-only.
    '
    ' The rules, in order:
    '   - an UNREADABLE config changes NOTHING (return): the page keeps saying what it
    '     last said, exactly as the tray keeps its last tooltip. The one path that
    '     genuinely ends the page is the block-ended path, which stops it explicitly.
    '   - no block named in the config => stop the listener. The page must never be up
    '     while nothing is blocked (it would answer for a domain that now resolves
    '     normally), so this is the un-bind trigger.
    '   - a block held => refresh the page, and if the socket is not up, try to bind -
    '     but no more than once per RebindRetryIntervalMs (P50).
    ' Never throws.
    Private Sub RefreshBlockPage()
        Try
            Dim ini As New IniFile
            Try
                ini.Load(IniPath())
            Catch ex As Exception
                Return
            End Try

            ' The same raw, ungated floor RawSlotApps / RawSlotUrlPatterns use: a
            ' position counts as a block the moment the config names it. Over-counting
            ' costs one extra page; under-counting would pull the page down under a
            ' live block, so the widest reading is the right one here too.
            If RawSlotBlockCount(ini) <= 0 Then
                pageServer.StopServing()
                Return
            End If

            pageServer.SetBody(BlockPage.BuildBlockPageHtml(BlockPageSlots(ini), HighWaterMark(ini)))

            If Not pageServer.IsListening Then
                Dim nowTick As Long = Environment.TickCount64
                If BlockPage.ShouldRetryBind(blockPageBindAnchorTick, nowTick, BlockPage.RebindRetryIntervalMs) Then
                    blockPageBindAnchorTick = nowTick
                    ' A False here is the expected outcome on a machine whose port 80
                    ' is taken - the notifier is not elevated and does not own the box.
                    ' Nothing to do about it but try again in a minute.
                    pageServer.TryStart(BlockPage.LoopbackPort)
                End If
            End If
        Catch ex As Exception
        End Try
    End Sub

    ' Stop serving the page. Called from every path that ends the notifier's working
    ' life, so the socket can never outlive the block. Never throws.
    Private Sub StopBlockPage()
        Try
            pageServer.StopServing()
        Catch ex As Exception
        End Try
    End Sub

    ' The monotonic [Time] HighWater mark as PLAINTEXT en-CA, "" if absent or
    ' unreadable (and the page then lists no slot at all rather than a bogus span -
    ' the same fail-soft ShortestSlotRemaining takes). Never throws.
    Private Function HighWaterMark(ByVal ini As IniFile) As String
        Try
            Dim raw As String = ini.GetKeyValue("Time", "HighWater")
            If raw = "" Then Return ""
            Return enc.DecryptData(raw)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ' The slot rows the page may list: Id, site count, and the slot's own end. Sites
    ' are PLAINTEXT-as-stored (P10) so only Until needs decrypting. Slots with no
    ' Until (PENDING, schedule) are handed over with UntilText="" and dropped by
    ' BuildBlockPageHtml - the exclusion lives in the PURE function so it is the
    ' thing the tests pin. Bound by MaxSlots rather than the stored SlotCount, like
    ' every other notifier scan, so a forged SlotCount cannot blank the page.
    ' Never throws.
    Private Function BlockPageSlots(ByVal ini As IniFile) As List(Of BlockPage.SlotLine)
        Dim rows As New List(Of BlockPage.SlotLine)
        Try
            For pos As Integer = 1 To ConfigIntegrity.MaxSlots
                Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
                Dim untilEnc As String = ini.GetKeyValue(sec, "Until")
                If untilEnc = "" Then Continue For
                Dim row As New BlockPage.SlotLine
                row.Id = If(ini.GetKeyValue(sec, "Id"), "")
                row.SiteCount = Notifications.CountPackedList(ini.GetKeyValue(sec, "Sites"))
                Try
                    row.UntilText = enc.DecryptData(untilEnc)
                Catch ex As Exception
                    Continue For
                End Try
                rows.Add(row)
            Next
        Catch ex As Exception
        End Try
        Return rows
    End Function

    ' D4/D4b: show a notification, fail-soft (a toast is cosmetic; it must never bubble an exception
    ' into the poll/load path). All D4 toasts + the block-ended toast route through here. D4b swaps
    ' the DELIVERY: try a PERSISTENT WinRT Action-Centre toast first, fall back to the historical
    ' transient balloon on any failure. The try/fallback decision lives in the pure, tested
    ' Notifications.DeliverWithFallback; this method only wires the two live delegates to it.
    Private Sub ShowToast(ByVal body As String)
        Notifications.DeliverWithFallback(body, AddressOf DeliverPersistentToast, AddressOf DeliverBalloon)
    End Sub

    ' D4b: the persistent path - a WinRT ToastGeneric that lands in AND STAYS in the Action Centre,
    ' so a toast suppressed by DND / full-screen (the 10/07/2026 miss) is still viewable later.
    ' ToastNotificationManagerCompat makes this work for this UNPACKAGED exe: the first
    ' CreateToastNotifier() idempotently registers the AUMID + a per-user Start-menu shortcut + an
    ' HKCU COM activator (HKCU-only, at runtime only - never at build or in tests). ExpirationTime
    ' bounds how long it lingers. Throws on any WinRT/registration failure BY DESIGN -
    ' DeliverWithFallback catches it and drops to the balloon; the notifier never crashes.
    Private Sub DeliverPersistentToast(ByVal body As String)
        Dim doc As New Windows.Data.Xml.Dom.XmlDocument()
        doc.LoadXml(Notifications.BuildToastXml(Notifications.ToastTitle, body))
        Dim toast As New Windows.UI.Notifications.ToastNotification(doc)
        toast.ExpirationTime = DateTimeOffset.Now.AddHours(Notifications.ToastExpiryHours)
        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast)
    End Sub

    ' D4b: the transient fallback - the historical NotifyIcon balloon, byte-unchanged. Reached only
    ' when the persistent path throws (e.g. a Windows build/context without the compat activator).
    Private Sub DeliverBalloon(ByVal body As String)
        tray.ShowBalloonTip(8000, Notifications.ToastTitle, body, ToolTipIcon.Info)
    End Sub

    Private Sub appKillTimer_Tick(ByVal sender As Object, ByVal e As EventArgs) Handles appKillTimer.Tick
        ' C5b (b3-iii): the effective USER-SESSION kill set is the manual [Process] List, PLUS -
        ' ONLY while a scheduled window is OPEN - the schedule's OWN apps (design §6.3 app-kill
        ' UNION, SD2), mirroring the SERVICE session-0 loop (b3-i). The service is the sole writer of
        ' [Schedule] ActiveUntil / [Time] HighWater / [Schedule] Spec and advances them every tick, so
        ' the schedule state is RE-READ here each tick (ActiveUntil always; HighWater + Spec only when
        ' ActiveUntil is set - the manual iniProcessList, fixed for the block, stays the load value).
        ' GATED on ScheduleActive: a no-schedule block (ActiveUntil="" - every block until the CLI
        ' writes a Spec in slice (c)) skips ALL decryption and leaves killList = iniProcessList, so
        ' this loop kills EXACTLY what it does today (BYTE-IDENTICAL, no extra crypto per tick). NOT
        ' macValid-gated (a union only ADDS kills - iniProcessList is always a prefix of killList, so
        ' it can never remove a manual kill; fail-closed, per the b3-i verifier's P3; the notifier
        ' holds no enforcement, and the hosts block is the real one). The schedule read is best-effort:
        ' ANY failure (a transient mid-write ini read, an unreadable ActiveUntil) falls back to the
        ' manual list - never LESS than today, self-heals next tick (the service is the real enforcer;
        ' hosts self-heal (b3-ii) keeps the schedule SITES blocked at manual strength regardless).
        '
        ' v1.1 S4 - THE APP-KILL TAMPER FIX. iniProcessList is the v9 [Process] List mirror,
        ' read ONCE at Load, and it sits OUTSIDE the v10 canonical: blanking it with a text
        ' editor left macValid TRUE and silently stopped every user-session kill, and a slot
        ' armed AFTER this notifier launched was never killed at all. So the union now also
        ' takes EVERY slot's own Apps, RE-READ FROM DISK EACH TICK (RawSlotApps) - MAC-covered
        ' data, so editing it is tamper-evident, and re-read so a new slot is enforced within
        ' one 2s beat instead of never. The mirror stays as the base: this is a UNION, never a
        ' replacement, so no kill this loop used to make can be removed.
        Dim killList As String = iniProcessList
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            killList = EffectiveKillList(killList, RawSlotApps(ini), True)
            Dim scheduleActiveEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")
            If scheduleActiveEnc <> "" Then
                Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
                Dim highWater As String = If(highWaterEnc = "", "", enc.DecryptData(highWaterEnc))
                If ScheduleActive(enc.DecryptData(scheduleActiveEnc), highWater) Then
                    killList = EffectiveKillList(killList, ParseSchedule(ini.GetKeyValue("Schedule", "Spec")).Apps, True)
                End If
            End If
        Catch ex As Exception
            ' Keep whatever was accumulated before the failure (never narrower than the load-
            ' time mirror). The pre-S4 code reset to iniProcessList here; with two independent
            ' widenings in the Try, resetting would DROP the slot union whenever the (later,
            ' decrypting) schedule read threw - and dropping a kill is the fail-open direction.
            ' Self-heals on the next 2s beat either way.
        End Try

        ' Guard on the EFFECTIVE set (not iniProcessList): a SCHEDULE-ONLY block has a "null"/empty
        ' manual list yet a non-empty union while its window is open, so keying the early-return off
        ' the manual list alone would wrongly skip killing the schedule's apps. For a no-schedule
        ' block killList IS iniProcessList, so this is the IDENTICAL early-return as before.
        If killList Is Nothing OrElse killList = "" OrElse killList = "null" Then Return
        For Each proc As Process In Process.GetProcesses()
            Try
                If proc.SessionId <> 0 AndAlso ProcessNameInKillList(killList, proc.ProcessName) Then
                    proc.Kill()
                End If
            Catch ex As Exception
            End Try
        Next
    End Sub

    ' ============ v1.1 S7 (F2b): the URL watcher's beat ============
    '
    ' P62's 2s tick does almost nothing itself: it takes a re-entrancy latch and hands the
    ' pass to a POOL THREAD. That indirection is the whole point. A UI-thread pass would put
    ' a cross-process UIAutomation read - which a busy browser can hold open for seconds -
    ' directly in front of appKillTimer's 2s beat, so a slow browser would stall the loop that
    ' kills blocked apps. Here the UI thread returns immediately and, at worst, the watcher
    ' skips beats while one pass is still running.
    '
    ' The latch is released in the pass's Finally, so a pass that throws (it cannot - every
    ' entry point in UrlWatch is total - but a ThreadPool item that dies would otherwise wedge
    ' the watcher permanently) still re-opens the gate.
    Private Sub urlWatchTimer_Tick(ByVal sender As Object, ByVal e As EventArgs) Handles urlWatchTimer.Tick
        If System.Threading.Interlocked.CompareExchange(urlWatchInFlight, 1, 0) <> 0 Then Return
        Try
            System.Threading.ThreadPool.QueueUserWorkItem(
                Sub()
                    Try
                        RunUrlWatchPass()
                    Catch ex As Exception
                    Finally
                        System.Threading.Interlocked.Exchange(urlWatchInFlight, 0)
                    End Try
                End Sub)
        Catch ex As Exception
            ' The queue itself refused (pool exhaustion). Re-open the latch and wait for
            ' the next beat; never let this reach the timer.
            System.Threading.Interlocked.Exchange(urlWatchInFlight, 0)
        End Try
    End Sub

    ' One watch pass, on a pool thread. Fail-soft end to end: every step's failure value is
    ' "do nothing this beat", and the block is untouched either way (the watcher is a nudge
    ' on top of the hosts block, never enforcement).
    '
    ' Order matters for cost, not for correctness: the foreground process is a cheap
    ' user32 read, so a machine where the user is not in a browser never opens the config at
    ' all, and neither the disk nor UIAutomation is touched.
    Private Sub RunUrlWatchPass()
        ' Read once, then hand the SAME name to TickTarget - which re-applies the P54 gate
        ' itself, so the decision function is complete on its own and this early exit is
        ' purely the cost saving it looks like.
        Dim procName As String = UrlWatch.ForegroundProcessNameSafe()
        If Not UrlWatch.IsWatchedBrowser(procName) Then Return
        Dim patterns As List(Of String) = ActiveUrlPatterns()
        If patterns.Count = 0 Then Return
        Dim nowTick As Long = Environment.TickCount64
        Dim target As String = UrlWatch.TickTarget(procName, patterns,
                                                   System.Threading.Interlocked.Read(urlLastActionTick), nowTick)
        If target Is Nothing OrElse target.Length = 0 Then Return
        ' Stamped BEFORE the attempt: the cooldown bounds ATTEMPTS, so a browser that keeps
        ' refusing the SetValue is retried once per 5s, not on every 2s beat.
        System.Threading.Interlocked.Exchange(urlLastActionTick, nowTick)
        If UrlWatch.PerformRedirect(target) Then RecordRedirect(target)
    End Sub

    ' v1.1 S7b (P45), DISPLAY-ONLY: credit one redirect to the stats sidecar the
    ' notifier alone writes (%ProgramData%\MonkMode\stats-notify.ini). Reached at most
    ' once per P60 cooldown - i.e. once per 5s in the worst case - and ONLY after a
    ' redirect actually happened, so re-opening the config here costs nothing on the
    ' hot path and the watch pass above keeps its single ini read.
    '
    ' Best-effort end to end: the whole body is wrapped, StatsSidecar's own entry
    ' points are total, and the failure value is "no counter this time". A counter may
    ' never disturb the watcher, and the watcher may never disturb the app-kill beat.
    ' grantUsersModify:=False - the notifier is NOT elevated; setting an ACL is the
    ' service's job (P49), and this side only creates the folder if nobody has yet.
    Private Sub RecordRedirect(ByVal target As String)
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            StatsSidecar.Apply(StatsSidecar.NotifyStatsPath(),
                               StatsSidecar.NewDelta(SlotIdOwningTarget(ini, target), 0, 1, 0,
                                                     StatsSidecar.DayKeyFor(DateTime.Now)),
                               False)
        Catch ex As Exception
        End Try
    End Sub

    ' v1.1 S7b (P45), DISPLAY-ONLY: which slot Id owns a redirect to `target`, or "" when
    ' none can be named (the redirect then counts towards the lifetime and day totals with
    ' no slot label - never lost). Same RAW, ungated scan as RawSlotUrlPatterns, in
    ' POSITION order, first owner wins - see UrlWatch.PatternsOwnTarget for why host
    ' equality is the right test and what it deliberately under-attributes. Slot IDs are
    ' stable across the compaction a retire performs; positions are not, which is why the
    ' sidecar keys on the Id. Pure; never throws.
    Friend Shared Function SlotIdOwningTarget(ByVal ini As IniFile, ByVal target As String) As String
        If ini Is Nothing Then Return ""
        For pos As Integer = 1 To ConfigIntegrity.MaxSlots
            Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
            Dim pats As New List(Of String)
            For Each tok As String In If(ini.GetKeyValue(sec, "UrlPatterns"), "").Split("|"c)
                Dim t As String = tok.Trim()
                If t <> "" Then pats.Add(t)
            Next
            If pats.Count > 0 AndAlso UrlWatch.PatternsOwnTarget(target, pats) Then
                Return If(ini.GetKeyValue(sec, "Id"), "").Trim()
            End If
        Next
        Return ""
    End Function

    ' The union of the armed slots' URL patterns, re-read from disk each pass (the CLI is
    ' their only writer, and a slot armed after this notifier launched must start being
    ' watched within one beat - the S4 lesson from the app-kill mirror). Empty on ANY
    ' failure: an unreadable config means no redirect, never a redirect against a stale set.
    Private Function ActiveUrlPatterns() As List(Of String)
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Return RawSlotUrlPatterns(ini)
        Catch ex As Exception
            Return New List(Of String)
        End Try
    End Function

    ' Computes the clock-change-compensated end time from the persisted
    ' [CurrentTime] Now and [Time] Until strings (both already decrypted,
    ' en-CA). Returns Nothing when either value fails to parse: deriving a new
    ' end time from garbage would overwrite the real one with roughly "now"
    ' and end the block instantly (fail-open), so the caller must leave the
    ' stored Until untouched instead. Shared and pure so it can be unit tested.
    Friend Shared Function ComputeCompensatedUntil(ByVal storedNow As String, ByVal storedUntil As String, ByVal currentTime As DateTime) As DateTime?
        Dim oldNow As DateTime, oldUntil As DateTime
        If Not DateTime.TryParse(storedNow, CA, DateTimeStyles.None, oldNow) Then Return Nothing
        If Not DateTime.TryParse(storedUntil, CA, DateTimeStyles.None, oldUntil) Then Return Nothing
        Dim compensated As DateTime = DateAdd(DateInterval.Second, DateDiff(DateInterval.Second, oldNow, oldUntil), currentTime)
        ' NEVER SHORTEN THE BLOCK (fail-closed). Clock-comp exists ONLY to stop a
        ' block being shortened by rolling the clock; it must never itself move
        ' Until earlier. A forward clock change yields compensated > oldUntil => keep
        ' it (extend). A backward change, OR a poisoned [CurrentTime] Now that the
        ' service advanced PAST Until during a forward clock excursion (remaining went
        ' negative), yields compensated < oldUntil and would push Until BELOW the
        ' monotonic HighWater => the service reads "expired" and lifts EARLY (the
        ' 14/06/2026 smoke-test regression: surfaced by the -IncludeClockTest drill
        ' once #3's atomic write made the notifier's past-Until write durable). Clamp
        ' so Until only ever holds or grows; B4's HighWater still ends the block after
        ' the correct real duration.
        If compensated < oldUntil Then Return oldUntil
        Return compensated
    End Function

    ' B7 tamper-evident config: same [Integrity] section the CLI stamps. The
    ' notifier re-stamps the MAC when it rewrites [Time] Until on a clock change.
    Private Const IntegritySection As String = "Integrity"
    Private Const IntegrityKeyName As String = "Key"
    Private Const IntegrityMacName As String = "Mac"

    ' B7/B4: builds the v11 two-level canonical the MAC is computed over, from a
    ' loaded ini - the global header, then one 16-line block per slot for
    ' pos = 1 To the CLAMPED [Slots] SlotCount. Every party (this writer, plus the
    ' service/guardian/notifier readers) must derive a byte-identical string or the
    ' MAC never validates and every block freezes, so BELOW the `crypt` alias line
    ' this body is byte-identical text in all four copies - diff them on any edit.
    ' [Integrity] Key/Mac are excluded (you can't MAC the MAC); missing values pass
    ' through as "". Stale [SlotN] sections beyond SlotCount are never read.
    Friend Function CanonicalFromIni(ByVal ini As IniFile) As String
        Dim crypt As Simple3Des = enc      ' the ONLY line that differs across the four copies
        ' Globals: HighWater/Now/[Guard] HoldUntil are ENCRYPTED datetimes (decrypted
        ' here, like every datetime); [Slots] NextSlotId/SlotCount and [Guard]
        ' ArmedCount are plaintext ints (the MAC is their protection).
        Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
        Dim nowEnc As String = ini.GetKeyValue("CurrentTime", "Now")
        Dim guardHoldEnc As String = ini.GetKeyValue("Guard", "HoldUntil")
        Dim highWaterPlain As String = If(highWaterEnc = "", "", crypt.DecryptData(highWaterEnc))
        Dim nowPlain As String = If(nowEnc = "", "", crypt.DecryptData(nowEnc))
        Dim guardHoldPlain As String = If(guardHoldEnc = "", "", crypt.DecryptData(guardHoldEnc))

        ' FX1 (v11): the GLOBAL [Schedule] pair - the entire enforcement state of a
        ' SCHEDULE-ONLY (v9-shaped, slot-less) config, which `monkmode schedule` still
        ' writes and the service still enforces from. Spec is plaintext-as-stored (a
        ' window rule is not a secret; the MAC is its protection); the service-written
        ' ActiveUntil is an ENCRYPTED datetime, decrypted like the globals above.
        ' Deliberately distinct local names from the PER-SLOT pair read inside the loop
        ' below - they are different fields and must never be confused.
        Dim globalScheduleSpec As String = ini.GetKeyValue("Schedule", "Spec")
        Dim globalScheduleActiveEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")
        Dim globalScheduleActivePlain As String = If(globalScheduleActiveEnc = "", "", crypt.DecryptData(globalScheduleActiveEnc))

        ' The CLAMPED count is BOTH the header value and the loop bound, so a forged
        ' SlotCount can only ever build a canonical nothing can match -> freeze.
        Dim slotCount As Integer = ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"))
        Dim slots As New System.Text.StringBuilder()
        For pos As Integer = 1 To slotCount
            Dim sec As String = "Slot" & pos.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ' Per slot, the ENCRYPTED datetimes are Until/StartAt/CoolOffUntil/
            ' ScheduleActiveUntil; everything else - INCLUDING Sites/Apps/UrlPatterns -
            ' is plaintext-as-stored. No "null" sentinel any more (v10): "no apps" is "".
            Dim untilEnc As String = ini.GetKeyValue(sec, "Until")
            Dim startAtEnc As String = ini.GetKeyValue(sec, "StartAt")
            Dim coolOffEnc As String = ini.GetKeyValue(sec, "CoolOffUntil")
            Dim scheduleActiveEnc As String = ini.GetKeyValue(sec, "ScheduleActiveUntil")
            slots.Append(ConfigIntegrity.BuildSlotCanonical(pos,
                ini.GetKeyValue(sec, "Id"),
                If(startAtEnc = "", "", crypt.DecryptData(startAtEnc)),
                ini.GetKeyValue(sec, "DurationSeconds"),
                If(untilEnc = "", "", crypt.DecryptData(untilEnc)),
                ini.GetKeyValue(sec, "Sites"),
                ini.GetKeyValue(sec, "Apps"),
                ini.GetKeyValue(sec, "UrlPatterns"),
                ini.GetKeyValue(sec, "AllSession"),
                ini.GetKeyValue(sec, "ScheduleSpec"),
                If(scheduleActiveEnc = "", "", crypt.DecryptData(scheduleActiveEnc)),
                If(coolOffEnc = "", "", crypt.DecryptData(coolOffEnc)),
                ini.GetKeyValue(sec, "CoolOffDuration"),
                ini.GetKeyValue(sec, "PartnerSalt"),
                ini.GetKeyValue(sec, "PartnerHash"),
                ini.GetKeyValue(sec, "PartnerUnlockedAt"),
                ini.GetKeyValue(sec, "Committed")))
        Next

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion,
                                             highWaterPlain,
                                             nowPlain,
                                             ini.GetKeyValue("Slots", "NextSlotId"),
                                             slotCount,
                                             guardHoldPlain,
                                             ini.GetKeyValue("Guard", "ArmedCount"),
                                             globalScheduleSpec,
                                             globalScheduleActivePlain,
                                             slots.ToString())
    End Function

    ' B7: recompute [Integrity] Mac over the current canonical with the already
    ' stored [Integrity] Key (DPAPI-unprotected). No-op if no recoverable key;
    ' never throws. Mutates the ini in place; the caller saves.
    Private Sub RestampMacWithExistingKey(ByVal ini As IniFile)
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return
            ini.SetKeyValue(IntegritySection, IntegrityMacName, ConfigIntegrity.ComputeConfigMac(CanonicalFromIni(ini), key))
        Catch ex As Exception
        End Try
    End Sub

    ' B7: is [Integrity] Mac currently valid over the canonical? DPAPI-unprotect
    ' [Integrity] Key and FixedTimeEquals-compare. Returns False (never throws) on
    ' any failure - absent/invalid MAC, unreadable key, foreign-machine blob.
    ' Mirrors the service's ConfigMacIsValidForIni; used to refuse re-stamping a
    ' tampered ini on a clock change (the B7 fail-open fix).
    Private Function ConfigMacIsValidForIni(ByVal ini As IniFile) As Boolean
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return False
            Return ConfigIntegrity.ConfigMacIsValid(CanonicalFromIni(ini), ini.GetKeyValue(IntegritySection, IntegrityMacName), key)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' ============ FX6 (F8): THE ONE NOTIFIER WRITER FOR THE SHARED CONFIG ============
    '
    ' ONE means one: all FOUR of this process's writes to monkmode_settings.ini come through
    ' here - SystemEvents_TimeChanged's two halves, AnnounceBlockEnded's [User] NeedsAlerted,
    ' and Program.ReassertTimeChangingFailSoft (the AppDomain crash backstop). If a fifth ever
    ' appears it belongs here too; a second raw Load/Save copy re-opens exactly what follows.
    '
    ' THE HOLE. The notifier's config writes were Load -> SetKeyValue -> Save, and IniFile.Save
    ' rewrites the WHOLE file from that in-memory model while leaving [Integrity] Mac exactly
    ' as loaded. Anything the service or the CLI wrote in the load->Save window was therefore
    ' silently ROLLED BACK - and because the stale MAC travels with the stale canonical it came
    ' from, the rolled-back file still VERIFIES, so nothing downstream can tell. The losses are
    ' real ones: a [Partner] UnlockedAt the service had just verified (the user's one-time code
    ' is consumed and the unlock vanishes) or an applied `add` (sites lost AFTER the trigger was
    ' eaten - an UNDER-block). Every other writer in the system re-locates and re-validates
    ' before it writes; the notifier was the one that did not.
    '
    ' THE DISCIPLINE:
    '   * re-read HERE, immediately before the write - never save a model loaded earlier (the
    '     clock-change handler's model was 2000ms old);
    '   * NO-OP when the key already reads the wanted value: the commonest call then touches
    '     the file not at all, which is the strongest possible form of "roll nothing back";
    '   * a GENERATION check on [Integrity] Mac taken as late as possible before the Save.
    '     Every legitimate writer re-stamps that MAC over its changed canonical, so a Mac that
    '     moved since our own read means somebody else's write landed - abandon ours and retry
    '     against fresh bytes.
    '
    ' DELIBERATELY NOT MAC-GATED: the two keys this writes ([Time] TimeChanging, [User]
    ' NeedsAlerted) sit OUTSIDE the canonical, and refusing to lower TimeChanging on a frozen
    ' config would be the F7 wedge all over again. Nothing here re-stamps, so no unverified
    ' config is ever blessed by it.
    '
    ' HONEST RESIDUALS: (a) this is not a lock - a write landing between the probe and
    ' IniFile.Save's rename still wins; it shrinks the window from the whole load-modify-save
    ' span to the Save itself. (b) A racing writer that touches ONLY non-MAC-covered keys is
    ' invisible to the token by construction. Both are bounded by the fact that everything
    ' written here is housekeeping: no enforcement field is ever this writer's to change.
    Friend Const SharedConfigWriteAttempts As Integer = 3
    Friend Const SharedConfigWriteRetryMs As Integer = 50

    ' TEST SEAM (FX6/F8) - the service's RetireSaveHookForTests pattern. Fired after this
    ' writer's own read, immediately before the generation probe and the Save: the exact window
    ' a racing service/CLI write has. <ThreadStatic>; PRODUCTION never assigns it, so the field
    ' stays Nothing and the write is behaviourally unchanged.
    <ThreadStatic>
    Friend Shared SharedConfigWriteHookForTests As Action(Of String)

    ' Returns True iff the key ends up holding `value` (including the no-op case). False means
    ' the write was abandoned - fail-SOFT by design, because every caller's key is housekeeping
    ' and a missed write costs at most one duplicate toast or one bounded TimeChanging hold
    ' (Service1.TimeChangeHoldActive), never a lifted or narrowed block.
    Friend Shared Function SaveSharedConfigKey(ByVal iniPath As String, ByVal section As String,
                                               ByVal key As String, ByVal value As String) As Boolean
        For attempt As Integer = 1 To SharedConfigWriteAttempts
            Try
                ' No config, nothing to update. IniFile.Load answers an EMPTY model for a
                ' missing path rather than throwing, so without this the notifier would CREATE
                ' a one-key stub where no config exists - which the service then reads as a
                ' structurally-unusable primary and recovers over. Never write a config into
                ' being; only the CLI arms.
                If Not File.Exists(iniPath) Then Return False
                Dim ini As New IniFile
                ini.Load(iniPath)
                If StrComp(ini.GetKeyValue(section, key), value) = 0 Then Return True   ' already so: write NOTHING
                Dim genAtLoad As String = ini.GetKeyValue(IntegritySection, IntegrityMacName)
                ini.SetKeyValue(section, key, value)
                If SharedConfigWriteHookForTests IsNot Nothing Then SharedConfigWriteHookForTests(iniPath)
                Dim probe As New IniFile
                probe.Load(iniPath)
                If String.Equals(If(genAtLoad, ""), If(probe.GetKeyValue(IntegritySection, IntegrityMacName), ""), StringComparison.Ordinal) Then
                    ini.Save(iniPath)
                    Return True
                End If
            Catch ex As Exception
            End Try
            If attempt < SharedConfigWriteAttempts Then System.Threading.Thread.Sleep(SharedConfigWriteRetryMs)
        Next
        Return False
    End Function

    ' Cooperate with a system-clock change. B4 (the monotonic HighWater mark) owns
    ' clock-rollback now - expiry is decided off real elapsed time, not the wall
    ' clock - so this handler NO LONGER rewrites [Time] Until. It used to, and that
    ' was the cause of two 14/06/2026 smoke-test bugs once B4 was in place:
    '   (a) after the service wrote a jumped [CurrentTime] Now during a forward clock
    '       excursion, a backward correction made the comp push Until into the PAST
    '       => the block lifted EARLY (a real bypass);
    '   (b) a forward jump EXTENDED Until and the never-shorten clamp could not undo
    '       it => the block OVER-RAN.
    ' B4 already ends the block after the correct REAL duration across ANY clock
    ' change (a forward jump > ceiling does not advance HighWater; a backward roll
    ' leaves it untouched), so leaving Until alone is both correct and simpler. We
    ' keep ONLY the TimeChanging cooperation flag (NOT a MAC-covered field): the
    ' service pauses its expiry/re-stamp decisions while the flag is "yes", so it
    ' never acts on a half-updated config mid clock-change. NOTE: ConfigMacIsValidForIni +
    ' CanonicalFromIni are now LIVE again - the C5b (c3) ScheduleArmed toast gate (Form1_Load /
    ' pollTimer_Tick) uses them to suppress the manual-expiry toast while a schedule is armed, so
    ' do NOT delete them. Only RestampMacWithExistingKey + ComputeCompensatedUntil remain unused
    ' here (the notifier no longer rewrites [Time] Until), safe to delete in a later cleanup.
    Private Sub SystemEvents_TimeChanged(ByVal sender As Object, ByVal e As EventArgs)
        Try
            ' FX6 (F8): both halves go through the ONE writer above - re-read, no-op when
            ' already so, generation-checked - instead of whole-file-saving a model of our own.
            ' The "no" half not landing is no longer a permanent wedge either: FX6 (F7) makes
            ' the service treat a raise that outlives its bound as orphaned.
            SaveSharedConfigKey(IniPath(), "Time", "TimeChanging", "yes")

            System.Threading.Thread.Sleep(2000)

            SaveSharedConfigKey(IniPath(), "Time", "TimeChanging", "no")
        Catch ex As Exception
        End Try
    End Sub

    Private Sub AnnounceBlockEnded()
        appKillTimer.Stop()
        urlWatchTimer.Stop()   ' v1.1 S7 (P62): stopped with the app-kill beat it mirrors
        StopBlockPage()        ' v1.1 S7d (P50): the page comes down with the block
        ' FX6 (F8): through the ONE writer - this ran at block-END, i.e. exactly when the
        ' service is rewriting the config to tear the block down, so it was the likeliest of
        ' the three to roll a real write back.
        SaveSharedConfigKey(IniPath(), "User", "NeedsAlerted", "no")

        RemoveRunEntry()

        ' D4: the block-ended toast, now routed through the centralised builder (same
        ' wording, pinned by a test) + the shared fail-soft ShowToast.
        ' v1.1 S7b: it now closes with what the block actually stopped today. A zero or
        ' unreadable count yields the historical string BYTE-UNCHANGED, so a machine
        ' with no sidecar sees exactly the toast it saw before this slice.
        ShowToast(Notifications.BlockEndedMessageWithCount(BlockedTodayCount()))

        ' give the balloon a moment to display before exiting
        closeTimer.Start()
    End Sub

    Private Sub closeTimer_Tick(ByVal sender As Object, ByVal e As EventArgs) Handles closeTimer.Tick
        ExitNotifier()
    End Sub

    Private Sub RemoveRunEntry()
        Try
            Using rk As RegistryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
                If rk IsNot Nothing Then rk.DeleteValue("MonkMode_notify", False)
            End Using
        Catch ex As Exception
        End Try
    End Sub

    Private Sub ExitNotifier()
        Try
            RemoveHandler SystemEvents.TimeChanged, AddressOf SystemEvents_TimeChanged
        Catch
        End Try
        Try
            tray.Visible = False
            tray.Dispose()
        Catch
        End Try
        ' v1.1 S7d: also reached on the paths that exit WITHOUT AnnounceBlockEnded
        ' (NeedsAlerted="no", and the Load catch), so the socket is released on every
        ' exit, not just the announced one.
        StopBlockPage()
        Application.Exit()
    End Sub

    ' ================= C5b (b3-iii): schedule gates — the notifier's parity copy =================
    '
    ' The notifier's USER-SESSION app-kill (appKillTimer_Tick) unions the schedule's own apps while a
    ' window is open (design §6.3, SD2), exactly as the SERVICE session-0 loop does (b3-i). To decide
    ' "is a window open now?" and "what apps does it block?" it needs the same pure gates the service
    ' and guardian use. MonkMode / monkmode (service) / mm_guard / mm_notify are separate assemblies
    ' that can't reference one another, so these are a BYTE-FOR-BYTE parity copy of Service1.Schedule-
    ' Elapsed / ScheduleActive / ParseSchedule / EffectiveKillList (and helpers) - the same
    ' duplication+parity pattern as ConfigIntegrity / Simple3Des / the guardian's ScheduleActive,
    ' pinned equal to the service by CLI-independent parity [Theory]s. The notifier only READS
    ' [Schedule] ActiveUntil/Spec + [Time] HighWater (the service is their sole writer) - no write
    ' race, exactly like the guardian. Friend Shared (like ComputeCompensatedUntil) so the parity
    ' tests reach them as mm_notify.Form1.X.

    ' The Spec grammar-version tag (parity copy of Service1.ScheduleSpecGrammarVersion).
    ' P19 (v1.1 S3a): "v1" -> "v2" WITH the overnight (wrapped) window grammar. The bump is
    ' load-bearing - a v2 wrapped token fed to a v1 parser used to VANISH silently (fail-open),
    ' whereas an unknown tag parses to zero windows.
    Friend Const ScheduleSpecGrammarVersion As String = "v2"

    ' The LEGACY tag, still accepted (never emitted): a v1 Spec means STRICT same-day windows.
    ' Parity copy of Service1.ScheduleSpecGrammarVersionLegacy.
    Friend Const ScheduleSpecGrammarVersionLegacy As String = "v1"

    ' Has the open scheduled window reached its monotonic close? "" (no window) and any unparseable
    ' input read as NOT elapsed (fail-closed: a corrupted deadline/mark keeps the window held).
    ' Byte-for-byte Service1.ScheduleElapsed (parity-pinned).
    Friend Shared Function ScheduleElapsed(ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        If scheduleActiveUntilText = "" Then Return False
        Dim ca As New CultureInfo("en-CA")
        Dim activeUntil As DateTime, highWater As DateTime
        If Not DateTime.TryParse(scheduleActiveUntilText, ca, DateTimeStyles.None, activeUntil) Then Return False
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return False
        Return activeUntil <= highWater
    End Function

    ' Is a scheduled window currently open (set AND not yet elapsed)? SD1 hard hold. Empty => not
    ' active; a non-empty-but-unparseable deadline => active (fail-closed: hold). Byte-for-byte
    ' Service1.ScheduleActive (parity-pinned).
    Friend Shared Function ScheduleActive(ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        Return scheduleActiveUntilText <> "" AndAlso Not ScheduleElapsed(scheduleActiveUntilText, highWaterText)
    End Function

    ' The effective app-kill set this tick: the manual [Process] List, plus - ONLY while a window is
    ' open (scheduleActive) - the schedule's apps, "|"-joined (the separator can't appear in an exe
    ' name). scheduleActive=False / null / no apps => manualProcessList verbatim (byte-identical).
    ' Byte-for-byte Service1.EffectiveKillList (parity-pinned).
    Friend Shared Function EffectiveKillList(ByVal manualProcessList As String, ByVal scheduleApps As List(Of String), ByVal scheduleActive As Boolean) As String
        If Not scheduleActive OrElse scheduleApps Is Nothing OrElse scheduleApps.Count = 0 Then Return manualProcessList
        Dim sb As New System.Text.StringBuilder(manualProcessList)
        For Each app As String In scheduleApps
            sb.Append("|"c)
            sb.Append(app)
        Next
        Return sb.ToString()
    End Function

    ' Does the effective kill list name this live process's image? Case-INSENSITIVE (Ordinal): the
    ' list holds whatever casing the user typed at arm time (PackApps only appends a missing ".exe",
    ' it never lower-cases) while ProcessName reports the casing Windows holds for the running image,
    ' so a case-SENSITIVE compare silently UNDER-killed ("WhatsApp.exe" in the list vs a live
    ' "Whatsapp") - fail-open. Ignoring case only ever ADDS matches (widen-only), so no kill this
    ' code used to make is removed. Still a SUBSTRING search, the old predicate's exact shape: a
    ' token-exact match would NARROW it, and narrowing is the fail-open being fixed. Nothing/empty
    ' => no match. Byte-for-byte Service1.ProcessNameInKillList (parity-pinned).
    Friend Shared Function ProcessNameInKillList(ByVal killList As String, ByVal processName As String) As Boolean
        If killList Is Nothing OrElse killList.Length = 0 Then Return False
        Return killList.IndexOf(If(processName, "") & ".exe", StringComparison.OrdinalIgnoreCase) >= 0
    End Function

    ' ============ v1.1 S4: the notifier's SLOT view (raw scans, no new parser) ============
    '
    ' Same discipline as the guardian's P44 floor: the notifier must NOT grow a fifth copy of
    ' the service's slot readers (SlotState plus the three held predicates), so it reads the
    ' slot sections RAW - no MAC gate, no schedule parse, and no decrypt at all on the
    ' enforcement path (the one slot decrypt lives in ShortestSlotRemaining, which is
    ' display-only) - and every axis over-approximates towards MORE blocked.
    '
    ' Bound by MaxSlots and NOT by the stored [Slots] SlotCount, so forging SlotCount=0 cannot
    ' silence the kill list. A stale section beyond the count can only ADD names, and slots are
    ' REMOVED (never flagged) at retire and teardown, so the union goes quiet exactly when the
    ' blocks do.

    ' Every slot's Apps entries, first-occurrence order, deduped case-insensitively. Apps is
    ' plaintext-as-stored (P8) and already carries the ".exe" suffix PackApps applied at arm,
    ' which is the shape ProcessNameInKillList matches on. Deliberately NOT gated on whether a
    ' slot is enforcing right now: a slot present in the file is armed, a PENDING one already
    ' has its sites in hosts from arm time, and an ended one is removed from the file within a
    ' service tick - so the widest reading costs at most one tick of over-kill and can never
    ' drop a live block's app. Nothing/absent => empty. Pure; never throws.
    Friend Shared Function RawSlotApps(ByVal ini As IniFile) As List(Of String)
        Dim outList As New List(Of String)
        If ini Is Nothing Then Return outList
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For pos As Integer = 1 To ConfigIntegrity.MaxSlots
            Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
            For Each tok As String In If(ini.GetKeyValue(sec, "Apps"), "").Split(";"c)
                Dim t As String = tok.Trim()
                If t <> "" AndAlso seen.Add(t) Then outList.Add(t)
            Next
        Next
        Return outList
    End Function

    ' v1.1 S7 (F2b): every slot's UrlPatterns entries, first-occurrence order, deduped
    ' case-insensitively. UrlPatterns is plaintext-as-stored (P8) and "|"-packed (P55 - both
    ' "|" and ";" are refused inside a pattern at arm time, so the split is unambiguous).
    '
    ' Same raw, ungated scan as RawSlotApps above and for the same reasons: no MAC gate, no
    ' decrypt, bound by MaxSlots rather than the stored SlotCount (so forging SlotCount=0
    ' cannot silence the watcher), and not gated on whether a slot is enforcing this instant.
    ' The over-approximation is CHEAP HERE in a way it is not for app-kill: the widest reading
    ' costs at most one tick of over-nudging, and slots are REMOVED at retire/teardown, so the
    ' union empties exactly when the blocks end. Nothing/absent => empty. Pure; never throws.
    Friend Shared Function RawSlotUrlPatterns(ByVal ini As IniFile) As List(Of String)
        Dim outList As New List(Of String)
        If ini Is Nothing Then Return outList
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For pos As Integer = 1 To ConfigIntegrity.MaxSlots
            Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
            For Each tok As String In If(ini.GetKeyValue(sec, "UrlPatterns"), "").Split("|"c)
                Dim t As String = tok.Trim()
                If t <> "" AndAlso seen.Add(t) Then outList.Add(t)
            Next
        Next
        Return outList
    End Function

    ' How many blocks the config NAMES, by the same raw floor the guardian holds on (P44): a
    ' position counts once its ScheduleSpec / StartAt / Until is non-empty. DISPLAY-ONLY - it
    ' picks the toast wording and nothing else - so an over-count is cosmetic and can never
    ' touch enforcement. Pure; never throws.
    Friend Shared Function RawSlotBlockCount(ByVal ini As IniFile) As Integer
        If ini Is Nothing Then Return 0
        Dim n As Integer = 0
        For pos As Integer = 1 To ConfigIntegrity.MaxSlots
            Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
            If Not String.IsNullOrWhiteSpace(ini.GetKeyValue(sec, "ScheduleSpec")) OrElse
               Not String.IsNullOrWhiteSpace(ini.GetKeyValue(sec, "StartAt")) OrElse
               Not String.IsNullOrWhiteSpace(ini.GetKeyValue(sec, "Until")) Then n += 1
        Next
        Return n
    End Function

    ' C5b (c3): is a schedule armed? macValid AND the Spec parses to >=1 window - the EXACT
    ' derivation Service1.ScheduleArmed uses (parity-pinned). Used ONLY to SUPPRESS the manual-
    ' expiry toast while a schedule is armed (design §6.4): a schedule-only block carries a PAST
    ' [Time] Until sentinel, and between windows the service holds it alive (Done stays "no", c2),
    ' but a defence-in-depth gate here means that even if [User] Done ever read "yes" while a
    ' schedule is still armed, the notifier would NOT falsely announce the block ended - it
    ' announces only once the schedule is genuinely cleared (Spec blanked -> not armed -> the
    ' service's stopMe sets Done=yes). A manual block has an empty Spec => armed=False => the toast
    ' fires byte-identically to today. Byte-for-byte Service1.ScheduleArmed.
    Friend Shared Function ScheduleArmed(ByVal macValid As Boolean, ByVal specText As String) As Boolean
        Return macValid AndAlso ParseSchedule(specText).Windows.Count > 0
    End Function

    ' One recurring window (parity copy of Service1.ScheduleWindow).
    Friend Class ScheduleWindow
        Public DayMask As Integer
        Public OpenMinutes As Integer
        Public CloseMinutes As Integer
    End Class

    ' A parsed [Schedule] Spec: windows + schedule-wide site/app lists (parity copy of
    ' Service1.ParsedSchedule).
    Friend Class ParsedSchedule
        Public Windows As New List(Of ScheduleWindow)
        Public Sites As New List(Of String)
        Public Apps As New List(Of String)
    End Class

    ' Parse a [Schedule] Spec into windows + site/app lists. The notifier only consumes .Apps, but
    ' the WHOLE parser is copied byte-for-byte so .Apps is provably identical to the service's (a
    ' trimmed apps-only parser could drift). FAIL-CLOSED: a malformed window is skipped (keep the
    ' good ones); a wholly unparseable/empty Spec or an unknown grammar tag yields NO windows (inert
    ' - a self-authored garbage rule never invents a phantom block; a TAMPERED Spec fails the MAC
    ' upstream -> the service freezes, B7). Byte-for-byte Service1.ParseSchedule (parity-pinned).
    Friend Shared Function ParseSchedule(ByVal specText As String) As ParsedSchedule
        Dim result As New ParsedSchedule()
        If String.IsNullOrWhiteSpace(specText) Then Return result
        Dim parts() As String = specText.Split(";"c)
        If parts.Length < 2 Then Return result
        ' The TAG selects the grammar: only v2 admits wrapped (overnight) windows.
        Dim tag As String = parts(0).Trim()
        Dim allowWrap As Boolean
        If tag = ScheduleSpecGrammarVersion Then
            allowWrap = True
        ElseIf tag = ScheduleSpecGrammarVersionLegacy Then
            allowWrap = False
        Else
            Return result
        End If
        For Each winTok As String In parts(1).Split(","c)
            Dim w As ScheduleWindow = TryParseWindow(winTok, allowWrap)
            If w IsNot Nothing Then result.Windows.Add(w)
        Next
        For i As Integer = 2 To parts.Length - 1
            Dim p As String = parts(i)
            If p.StartsWith("sites=", StringComparison.Ordinal) Then
                AppendListTokens(result.Sites, p.Substring(6))
            ElseIf p.StartsWith("apps=", StringComparison.Ordinal) Then
                AppendListTokens(result.Apps, p.Substring(5))
            End If
        Next
        Return result
    End Function

    ' Split a "a|b|c" list body on "|", trimming and dropping empties, into dest. Parity copy.
    Private Shared Sub AppendListTokens(ByVal dest As List(Of String), ByVal body As String)
        For Each tok As String In body.Split("|"c)
            Dim t As String = tok.Trim()
            If t <> "" Then dest.Add(t)
        Next
    End Sub

    ' Parse one "dayMask:HHMM-HHMM" window; Nothing if malformed (fail-closed skip). open = close
    ' is never legal; open > close is a WRAPPED overnight window under v2 (allowWrap) and the old
    ' SD3 same-day rejection under the legacy v1 tag. Parity copy.
    Private Shared Function TryParseWindow(ByVal token As String, ByVal allowWrap As Boolean) As ScheduleWindow
        If token Is Nothing Then Return Nothing
        Dim tok As String = token.Trim()
        If tok = "" Then Return Nothing
        Dim halves() As String = tok.Split(":"c)
        If halves.Length <> 2 Then Return Nothing
        Dim mask As Integer = TryParseDayMask(halves(0))
        If mask = 0 Then Return Nothing
        Dim times() As String = halves(1).Split("-"c)
        If times.Length <> 2 Then Return Nothing
        Dim openMin As Integer = TryParseHhmm(times(0))
        Dim closeMin As Integer = TryParseHhmm(times(1))
        If openMin < 0 OrElse closeMin < 0 Then Return Nothing
        If openMin = closeMin Then Return Nothing
        If openMin > closeMin AndAlso Not allowWrap Then Return Nothing
        Dim w As New ScheduleWindow()
        w.DayMask = mask
        w.OpenMinutes = openMin
        w.CloseMinutes = closeMin
        Return w
    End Function

    ' "12345" -> bitmask (bit 0 = Mon .. bit 6 = Sun). 0 if empty or any char is not '1'..'7'
    ' (fail-closed). Parity copy.
    Private Shared Function TryParseDayMask(ByVal s As String) As Integer
        If s Is Nothing OrElse s.Length = 0 Then Return 0
        Dim mask As Integer = 0
        For Each ch As Char In s
            If ch < "1"c OrElse ch > "7"c Then Return 0
            mask = mask Or (1 << (AscW(ch) - AscW("1"c)))
        Next
        Return mask
    End Function

    ' "0900" -> 540 (minute-of-day). -1 if not exactly 4 digits or out of range (HH 0..23, MM 0..59).
    ' Fail-closed. Parity copy.
    Private Shared Function TryParseHhmm(ByVal s As String) As Integer
        If s Is Nothing OrElse s.Length <> 4 Then Return -1
        For Each ch As Char In s
            If ch < "0"c OrElse ch > "9"c Then Return -1
        Next
        Dim hh As Integer = Integer.Parse(s.Substring(0, 2), CultureInfo.InvariantCulture)
        Dim mm As Integer = Integer.Parse(s.Substring(2, 2), CultureInfo.InvariantCulture)
        If hh > 23 OrElse mm > 59 Then Return -1
        Return hh * 60 + mm
    End Function

End Class

Public NotInheritable Class Simple3Des
    Private TripleDes As New TripleDESCryptoServiceProvider
    Private Function TruncateHash(ByVal key As String, ByVal length As Integer) As Byte()
        Dim sha1 As New SHA1CryptoServiceProvider
        Dim keyBytes() As Byte = System.Text.Encoding.Unicode.GetBytes(key)
        Dim hash() As Byte = sha1.ComputeHash(keyBytes)
        ReDim Preserve hash(length - 1)
        Return hash
    End Function
    Sub New(ByVal key As String)
        TripleDes.Key = TruncateHash(key, TripleDes.KeySize \ 8)
        TripleDes.IV = TruncateHash("", TripleDes.BlockSize \ 8)
    End Sub
    Public Function EncryptData(ByVal plaintext As String) As String
        Dim plaintextBytes() As Byte = System.Text.Encoding.Unicode.GetBytes(plaintext)
        Dim ms As New System.IO.MemoryStream
        Dim encStream As New CryptoStream(ms, TripleDes.CreateEncryptor(), CryptoStreamMode.Write)
        encStream.Write(plaintextBytes, 0, plaintextBytes.Length)
        encStream.FlushFinalBlock()
        Return Convert.ToBase64String(ms.ToArray)
    End Function
    Public Function DecryptData(ByVal encryptedtext As String) As String
        Dim encryptedBytes() As Byte
        Try
            encryptedBytes = Convert.FromBase64String(encryptedtext)
        Catch ef As System.FormatException
            Return ""
        End Try
        Dim ms As New System.IO.MemoryStream
        Dim decStream As New CryptoStream(ms, TripleDes.CreateDecryptor(), CryptoStreamMode.Write)
        decStream.Write(encryptedBytes, 0, encryptedBytes.Length)
        decStream.FlushFinalBlock()
        Return System.Text.Encoding.Unicode.GetString(ms.ToArray)
    End Function
End Class
