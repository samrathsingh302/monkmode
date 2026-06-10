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
        Application.Run(New Form1())
    End Sub

End Module
