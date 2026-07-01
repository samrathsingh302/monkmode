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

Imports System.Windows.Forms

Module Program

    <STAThread()>
    Sub Main()
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
