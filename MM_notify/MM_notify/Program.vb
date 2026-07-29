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

'    MonkMode - notifier entry point
'
'    Explicit Sub Main for the WinForms notifier. We use a custom main (rather
'    than the VB application framework's auto-generated one) because no
'    Application.myapp / MainForm is wired up, which left the auto Sub Main
'    running with MainForm = Nothing -> the message loop never started and the
'    process exited immediately. Driving Application.Run ourselves keeps the
'    hidden tray form (Form1) alive until it exits itself.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports Microsoft.Toolkit.Uwp.Notifications
Imports System.Diagnostics
Imports System.Threading
Imports System.Windows.Forms

Module Program

    ' D4c: the process-lifetime root for the single-instance claim. This field is
    ' load-bearing, not bookkeeping: a Mutex whose last managed reference is dropped
    ' gets its handle closed by the finaliser, which would hand the claim straight to
    ' the next spawn and re-open the double-notifier window. Never disposed - the
    ' kernel object dies with the process, which IS the release we want (a crashed or
    ' killed notifier frees the claim for the guardian's relaunch).
    Private _singleInstanceClaim As Mutex

    <STAThread()>
    Sub Main()
        ' D4b: if THIS process was launched by the toast COM activator (the user clicked a
        ' persistent Action-Centre toast), do nothing and exit. The notifier the CLI spawned is
        ' already running its poll/app-kill loop; a click-relaunch must not start a SECOND notifier
        ' racing it (double Run-entry removal, double app-kill). We register no OnActivated handler
        ' (the toasts are informational, with no buttons), so the click has no work to do. This does
        ' NOT touch how the CLI LAUNCHES the notifier (the P2 no-stdio-inherit spawn, 0607ff9) - it
        ' is the notifier's own front-door guard against the toolkit's activation relaunch. Fail-soft:
        ' if the compat probe ever throws, fall through to the normal launch (never worse than today).
        Try
            If ToastNotificationManagerCompat.WasCurrentProcessToastActivated() Then Return
        Catch ex As Exception
        End Try

        ' D4c: single-instance guard. A reboot DURING a live block spawns the notifier
        ' TWICE, deterministically: the SYSTEM-session guardian's tick sees a notifier
        ' count of 0 straight after boot and relaunches one, and then Explorer processes
        ' the HKCU `MonkMode_notify` Run entry and starts a second. Nothing stopped that
        ' before this guard - Application.myapp's <SingleInstance>true</SingleInstance> is
        ' DEAD in this project (only the VB application framework's generated Sub Main
        ' honours it, and we bypass that with MyType=WindowsFormsWithCustomSubMain to drive
        ' Application.Run ourselves, see the file header), and the D4b check above only
        ' catches the toast-activation relaunch. Two notifiers means double toasts, double
        ' Run-entry removal and double app-kill at expiry, and two concurrent [Time]
        ' TimeChanging ini writers - worst case a torn write, which fails the MAC and so
        ' fails CLOSED into a Hold (never an early lift, but a wedge).
        '
        ' Fail-SOFT, in the same direction as D4b, and deliberately so: this process does
        ' the user-session app-kill and the clock-change compensation, so erroring BOTH
        ' instances into exiting would stop blocked apps being killed - an UNDER-block,
        ' strictly worse than the double-spawn we are fixing. So we stand down ONLY on the
        ' deterministic "the object already existed" signal AND a second real mm_notify
        ' process actually existing - losing the mutex alone is spoofable by a same-user
        ' squatter (see ShouldStandDown's header for the vector and its residuals). If the
        ' claim attempt OR the process count throws for any reason at all we fall through
        ' and launch normally (never worse than today).
        '
        ' The claim is never released by us. It is a kernel handle that dies with the
        ' process, so a crashed or killed notifier auto-frees it for the guardian's next
        ' relaunch, and because we never WaitOne there is no AbandonedMutexException path.
        Try
            If Not SingleInstance.TryClaim(SingleInstance.NotifierMutexName, _singleInstanceClaim) Then
                Dim liveCount As Integer =
                    Process.GetProcessesByName(SingleInstance.NotifierProcessName).Length
                If SingleInstance.ShouldStandDown(True, liveCount) Then Return
            End If
        Catch ex As Exception
        End Try

        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' AppDomain.UnhandledException backstop (fail-closed on crash). Route
        ' WinForms UI-thread exceptions to the AppDomain handler too
        ' (ThrowException mode) so one handler covers the UI thread AND any
        ' background/system-event thread - the default WinForms mode would swallow a
        ' UI-thread throw into an invisible dialog for this hidden tray app. See
        ' OnUnhandledException.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException)
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnUnhandledException

        Application.Run(New Form1())
    End Sub

    ' Best-effort fail-closed cleanup when an unhandled exception is about to kill
    ' the notifier. The notifier holds no hosts/registry enforcement state, so its
    ' ONLY crash residue is [Time] TimeChanging left "yes": SystemEvents_TimeChanged
    ' sets it "yes" for ~2s during a clock change, and if the process dies in that
    ' window the service pauses its expiry evaluation indefinitely - a stuck,
    ' un-liftable block (fail-CLOSED, but a real usability wedge, since only the
    ' notifier ever writes the flag back to "no"). Reset it so a crash can't wedge
    ' the block on. This does NOT weaken enforcement: B4's monotonic HighWater and
    ' the B7 MAC still govern expiry, and TimeChanging is NOT a MAC-covered field
    ' (so this write can neither cause an early lift nor invalidate the MAC). Never
    ' throws (a throw from an UnhandledException handler is undefined behaviour).
    Private Sub OnUnhandledException(ByVal sender As Object, ByVal e As UnhandledExceptionEventArgs)
        Try
            Dim iniPath As String = Application.StartupPath & "\monkmode_settings.ini"
            If Not System.IO.File.Exists(iniPath) Then Return
            Dim ini As New IniFile
            ini.Load(iniPath)
            ini.SetKeyValue("Time", "TimeChanging", "no")
            ini.Save(iniPath)
        Catch ex As Exception
        End Try
    End Sub

End Module
