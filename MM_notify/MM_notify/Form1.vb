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
    Private ReadOnly tray As New NotifyIcon()
    Private WithEvents pollTimer As New Timer()
    Private WithEvents appKillTimer As New Timer()
    Private WithEvents closeTimer As New Timer()
    Private iniProcessList As String = ""

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
        tray.Visible = True

        pollTimer.Interval = 5000
        appKillTimer.Interval = 2000
        closeTimer.Interval = 6000
    End Sub

    Private Sub Form1_Load(ByVal sender As Object, ByVal e As EventArgs) Handles MyBase.Load
        Me.Hide()

        AddHandler SystemEvents.TimeChanged, AddressOf SystemEvents_TimeChanged

        Dim done As String = "", needsAlerted As String = ""
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            done = ini.GetKeyValue("User", "Done")
            needsAlerted = ini.GetKeyValue("User", "NeedsAlerted")
            iniProcessList = ini.GetKeyValue("Process", "List")
            If StrComp(iniProcessList, "null") <> 0 Then iniProcessList = enc.DecryptData(iniProcessList)
        Catch ex As Exception
            ExitNotifier()
            Return
        End Try

        If StrComp("yes", done) = 0 Then
            If StrComp(needsAlerted, "no") = 0 Then
                ExitNotifier()
            Else
                AnnounceBlockEnded()
            End If
            Return
        End If

        pollTimer.Start()
        appKillTimer.Start()
    End Sub

    Private Sub pollTimer_Tick(ByVal sender As Object, ByVal e As EventArgs) Handles pollTimer.Tick
        Dim done As String = "", needsAlerted As String = ""
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            done = ini.GetKeyValue("User", "Done")
            needsAlerted = ini.GetKeyValue("User", "NeedsAlerted")
        Catch ex As Exception
            Return
        End Try

        If StrComp("yes", done) = 0 Then
            pollTimer.Stop()
            If StrComp(needsAlerted, "no") = 0 Then
                ExitNotifier()
            Else
                AnnounceBlockEnded()
            End If
        End If
    End Sub

    Private Sub appKillTimer_Tick(ByVal sender As Object, ByVal e As EventArgs) Handles appKillTimer.Tick
        If iniProcessList Is Nothing OrElse iniProcessList = "" OrElse iniProcessList = "null" Then Return
        For Each proc As Process In Process.GetProcesses()
            Try
                If proc.SessionId <> 0 AndAlso iniProcessList.Contains(proc.ProcessName & ".exe") Then
                    proc.Kill()
                End If
            Catch ex As Exception
            End Try
        Next
    End Sub

    ' Keep the remaining time stable across a system-clock change so a block
    ' cannot be shortened by moving the clock forward.
    Private Sub SystemEvents_TimeChanged(ByVal sender As Object, ByVal e As EventArgs)
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            ini.SetKeyValue("Time", "TimeChanging", "yes")
            ini.Save(IniPath())

            System.Threading.Thread.Sleep(2000)
            ini.Load(IniPath())

            Dim oldNow As DateTime, until As DateTime
            DateTime.TryParse(enc.DecryptData(ini.GetKeyValue("CurrentTime", "Now")), oldNow)
            DateTime.TryParse(enc.DecryptData(ini.GetKeyValue("Time", "Until")), until)

            Dim secondsLeft As Long = DateDiff(DateInterval.Second, oldNow, until)
            Dim newUntil As DateTime = DateAdd(DateInterval.Second, secondsLeft, DateTime.Now)

            ini.SetKeyValue("Time", "Until", enc.EncryptData(newUntil.ToString()))
            ini.SetKeyValue("Time", "TimeChanging", "no")
            ini.Save(IniPath())
        Catch ex As Exception
        End Try
    End Sub

    Private Sub AnnounceBlockEnded()
        appKillTimer.Stop()
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            ini.SetKeyValue("User", "NeedsAlerted", "no")
            ini.Save(IniPath())
        Catch ex As Exception
        End Try

        RemoveRunEntry()

        Try
            tray.ShowBalloonTip(8000, "MonkMode", "Your block has ended. You're free — stay strong.", ToolTipIcon.Info)
        Catch ex As Exception
        End Try

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
        Application.Exit()
    End Sub

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
