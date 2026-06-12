'    Copyright (c) 2011, 2012 Felix Belzile
'    Official software website: http://monkmode.local
'    Contact: felixbelzile@rogers.com  Web: http://felixbelzile.com

'    This file is part of MonkMode
'
'    MonkMode is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    MonkMode is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with MonkMode.  If not, see <http://www.gnu.org/licenses/>.

Imports System.ServiceProcess
Imports System.IO
Imports System.Security.Cryptography
Imports Microsoft.Win32
Imports Microsoft.VisualBasic
Imports System.Net.Sockets
Imports System.Net
Imports System.Runtime.InteropServices
Imports monkmode.IniFile
Imports System.Text
Imports System.Threading
Imports System.Globalization

Public Class Service1
    Inherits System.ServiceProcess.ServiceBase

    Dim ctMutex As Threading.Mutex
    Private m_previousExecutionState As UInteger
    Friend WithEvents timer As System.Timers.Timer
    Friend WithEvents adder As System.IO.FileSystemWatcher
    Dim install As String
    Public sWinDir As String = Environ("WinDir")
    Public hostDirS As String = sWinDir + "\system32\drivers\etc\hosts"
    Dim iniDateUntil As DateTime
    Dim iniTimeChanging As String
    Dim encryptionW As New Simple3Des("mm_textbox")
    Dim culture As CultureInfo = New CultureInfo("en-CA")

#Region " Component Designer generated code "

    Public Sub New()
        MyBase.New()
        MyBase.CanHandleSessionChangeEvent = True

        Thread.CurrentThread.CurrentCulture = New CultureInfo("en-CA")
        Thread.CurrentThread.CurrentUICulture = New CultureInfo("en-CA")
        ' This call is required by the Component Designer.
        InitializeComponent()
        ' Add any initialization after the InitializeComponent() call

    End Sub

    'UserService overrides dispose to clean up the component list.
    Protected Overloads Overrides Sub Dispose(ByVal disposing As Boolean)
        If disposing Then
            If Not (components Is Nothing) Then
                components.Dispose()
            End If
        End If
        MyBase.Dispose(disposing)
    End Sub

    Protected Overloads Sub OnStop(ByVal e As System.EventArgs)
        MyBase.OnStop()

        ' Restore previous state
        ' No way to recover; already exiting

    End Sub

    ' The main entry point for the process
    <MTAThread()> _
    Shared Sub Main()
        Dim ServicesToRun() As System.ServiceProcess.ServiceBase

        ' More than one NT Service may run within the same process. To add
        ' another service to this process, change the following line to
        ' create a second service object. For example,
        '
        '   ServicesToRun = New System.ServiceProcess.ServiceBase () {New Service1, New MySecondUserService}
        '
        ServicesToRun = New System.ServiceProcess.ServiceBase() {New Service1}

        System.ServiceProcess.ServiceBase.Run(ServicesToRun)
    End Sub

    'Required by the Component Designer
    Private components As System.ComponentModel.IContainer

    ' NOTE: The following procedure is required by the Component Designer
    ' It can be modified using the Component Designer.  
    ' Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> Private Sub InitializeComponent()
        Me.timer = New System.Timers.Timer()
        Me.adder = New System.IO.FileSystemWatcher()
        CType(Me.timer, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.adder, System.ComponentModel.ISupportInitialize).BeginInit()
        '
        'timer
        '
        Me.timer.Enabled = True
        Me.timer.Interval = 10000.0R
        '
        'adder
        '
        Me.adder.EnableRaisingEvents = True
        Me.adder.Filter = "add_to_hosts"
        '
        'Service1
        '
        Me.CanStop = False
        Me.ServiceName = "MONKMODE"
        CType(Me.timer, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.adder, System.ComponentModel.ISupportInitialize).EndInit()

    End Sub

#End Region

    Protected Overrides Sub OnStart(ByVal args() As String)

        ctMutex = New Threading.Mutex(False, "KeepmealivepleaseMONKMODE")
        adder.Path = sWinDir & "\system32\drivers\etc"
        Try
            Dim iniFile As IniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            If iniFile.Sections.Count < 2 Then
                stopMe()
            ElseIf BlockHasExpired(encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until")), DateTime.Now, 0) Then
                ' Only a successfully parsed, genuinely past end time lifts the
                ' block here; an unparseable Until keeps the block standing.
                stopMe()
            End If

        Catch ex As Exception
            My.Computer.FileSystem.WriteAllText(Application.StartupPath + "\monkmode_settings.ini", "", False)
            Dim iniFile = New IniFile
            iniFile.AddSection("User")
            iniFile.SetKeyValue("User", "CustomChecked", "abcdefghijk")
            iniFile.SetKeyValue("User", "CustomSites", "null")
            iniFile.SetKeyValue("User", "Done", "no")
            iniFile.SetKeyValue("User", "NeedsAlerted", "yes")
            iniFile.AddSection("Time")
            ' Format with the explicit en-CA culture: this runs on an SCM/timer
            ' thread, so the constructor's CurrentCulture does NOT apply here and
            ' an implicit DateTime->String conversion would use the machine locale,
            ' which the en-CA reads above can then fail to parse.
            iniFile.SetKeyValue("Time", "Until", encryptionW.EncryptData(DateAdd("d", 7, DateTime.Now).ToString(culture)))
            iniFile.SetKeyValue("Time", "TimeChanging", "no")
            iniFile.AddSection("CurrentTime")
            iniFile.SetKeyValue("CurrentTime", "Now", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
            iniFile.AddSection("Process")
            iniFile.SetKeyValue("Process", "List", "null")
            iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
        End Try

        If Not My.Computer.FileSystem.FileExists(hostDirS) Then
            System.IO.File.AppendAllText(hostDirS, "")
        End If
        ' Do NOT hold a persistent write handle on the hosts file. A handle opened
        ' FileAccess.Write/FileShare.Read makes the Windows DNS Client (Dnscache)
        ' fail to (re)read hosts during the block, so blocked sites silently
        ' resolve to their real IPs again (e.g. after any ipconfig /flushdns).
        ' We enforce the lock via the read-only attribute, re-asserted by the
        ' timer, and append on demand (adder_Changed) instead.
        Try
            SetAttr(hostDirS, vbReadOnly)
        Catch ex As Exception
            stopMe()
        End Try

    End Sub

    Private Sub timer_Elapsed(ByVal sender As System.Object, ByVal e As System.Timers.ElapsedEventArgs) Handles timer.Elapsed

        Dim processList As System.Diagnostics.Process() = Nothing
        Dim Proc As System.Diagnostics.Process
        Dim notifyFound As Boolean = False
        Dim iniProcessList As String = ""
        Dim iniUntil As String = ""

        Try
            Dim iniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            iniUntil = encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until"))
            iniTimeChanging = iniFile.GetKeyValue("Time", "TimeChanging")
            iniProcessList = iniFile.GetKeyValue("Process", "List")
            If StrComp("null", iniProcessList) <> 0 Then
                iniProcessList = encryptionW.DecryptData(iniProcessList)
            End If
        Catch ex As Exception
            Dim iniFile = New IniFile
            iniFile.AddSection("User")
            iniFile.SetKeyValue("User", "CustomChecked", "abcdefghijk")
            iniFile.SetKeyValue("User", "CustomSites", "null")
            iniFile.SetKeyValue("User", "Done", "no")
            iniFile.SetKeyValue("User", "NeedsAlerted", "yes")
            iniFile.AddSection("Time")
            ' Explicit en-CA, as above (timer threads don't inherit the
            ' constructor's CurrentCulture).
            iniFile.SetKeyValue("Time", "Until", encryptionW.EncryptData(DateAdd("d", 7, DateTime.Now).ToString(culture)))
            iniFile.SetKeyValue("Time", "TimeChanging", "no")
            iniFile.AddSection("CurrentTime")
            iniFile.SetKeyValue("CurrentTime", "Now", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
            iniFile.AddSection("Process")
            iniFile.SetKeyValue("Process", "List", "null")
            iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
        End Try

        ' Re-assert the read-only lock on hosts every tick (cheap tamper-resist;
        ' we no longer hold the file open, so this is how the lock is maintained).
        Try
            If My.Computer.FileSystem.FileExists(hostDirS) Then SetAttr(hostDirS, vbReadOnly)
        Catch ex As Exception
        End Try

        ' B2 self-heal: between ticks an admin can clear the attribute and
        ' edit/blank/delete hosts; while the block is still active (note
        ' BlockHasExpired fails CLOSED: unparseable = active) restore our
        ' entries from the snapshot the CLI persisted next to the exe.
        ' Try/Catch so a transient lock can never crash the service.
        Try
            Dim snapshotPath As String = Application.StartupPath + "\monkmode_hosts.block"
            If Not BlockHasExpired(iniUntil, DateTime.Now, 5) AndAlso My.Computer.FileSystem.FileExists(snapshotPath) Then
                Dim hostsText As String = ""
                If My.Computer.FileSystem.FileExists(hostDirS) Then
                    hostsText = My.Computer.FileSystem.ReadAllText(hostDirS)
                End If
                Dim repaired As String = RepairHostsBlock(hostsText, My.Computer.FileSystem.ReadAllText(snapshotPath))
                If repaired IsNot Nothing Then
                    If My.Computer.FileSystem.FileExists(hostDirS) Then SetAttr(hostDirS, vbNormal)
                    Try
                        Using sw As New StreamWriter(New FileStream(hostDirS, FileMode.Create, FileAccess.Write, FileShare.Read))
                            sw.Write(repaired)
                        End Using
                    Finally
                        ' Even if the write throws mid-way, never leave hosts
                        ' writable or a write handle leaked (a held handle stops
                        ' the DNS client re-reading hosts — the flushdns bug).
                        SetAttr(hostDirS, vbReadOnly)
                    End Try
                End If
            End If
        Catch ex As Exception
        End Try

        processList = System.Diagnostics.Process.GetProcesses()
        For Each Proc In processList
            If Proc.SessionId = 0 Then
                Try
                    If iniProcessList.Contains(Proc.ProcessName + ".exe") Then
                        Proc.Kill()
                    End If
                Catch ex As Exception
                End Try
            End If
        Next

        If StrComp("no", iniTimeChanging) = 0 Then
            ' Fail CLOSED: only a parsed, genuinely past end time lifts the
            ' block; an unparseable Until skips the expiry action this tick.
            If BlockHasExpired(iniUntil, DateTime.Now, 5) Then
                stopMe()
            Else
                Dim iniFile = New IniFile
                iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
                iniFile.SetKeyValue("CurrentTime", "Now", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
                iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
            End If
        End If
    End Sub

    ' Decides whether a persisted block end time has expired. untilText is the
    ' decrypted [Time] Until value (an en-CA datetime string); expired means no
    ' more than graceSeconds remain at asOf. An unparseable value is NOT
    ' expired: treating a failed parse as expiry would fail OPEN — a corrupted
    ' (or legacy machine-locale) value would silently lift the block early.
    ' Shared and file-system-free so it can be unit tested.
    Friend Shared Function BlockHasExpired(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long) As Boolean
        Dim untilDate As DateTime
        If Not DateTime.TryParse(untilText, New CultureInfo("en-CA"), DateTimeStyles.None, untilDate) Then
            Return False
        End If
        Return DateDiff(DateInterval.Second, asOf, untilDate) <= graceSeconds
    End Function

    ' Returns the hosts-file text with the MonkMode marker block (the marker
    ' line and everything below it) removed, leaving the user's own content
    ' untouched. Shared and file-system-free so it can be unit tested.
    Friend Shared Function StripMonkModeBlock(ByVal fileReader As String) As String

        Dim original As String = ""
        Dim startpos As Integer = 0

        ' Ordinal, case-sensitive — the same comparison the stopMe() gate and
        ' the CLI use. The old case-insensitive InStr(..., CompareMethod.Text)
        ' could lock onto a hand-edited case-variant marker line ABOVE the real
        ' one and delete the user's own hosts lines between the two.
        startpos = fileReader.IndexOf("#### MonkMode Entries ####", StringComparison.Ordinal)
        If startpos < 0 Then
            Return fileReader
        End If

        ' Cut at the marker, then drop only the single line terminator the
        ' writer placed before it. The old "startpos - 3" assumed that
        ' terminator was CRLF and silently ate one character of the user's
        ' own content whenever the hosts file used LF endings.
        original = Microsoft.VisualBasic.Left(fileReader, startpos)
        If original.EndsWith(vbCrLf) Then
            original = Microsoft.VisualBasic.Left(original, original.Length - 2)
        ElseIf original.EndsWith(vbLf) OrElse original.EndsWith(vbCr) Then
            original = Microsoft.VisualBasic.Left(original, original.Length - 1)
        End If

        Return original
    End Function

    ' Decides whether hosts needs its MonkMode block restored (B2 self-heal)
    ' and, if so, returns the full repaired hosts text; returns Nothing when no
    ' repair is needed. expectedBlock is the snapshot the CLI persisted when
    ' the block started (the marker line + entry lines, exactly as appended to
    ' hosts). Semantics:
    '   - null/empty/whitespace snapshot -> Nothing (never invent content);
    '   - hosts already contains the snapshot exactly (ordinal) -> Nothing,
    '     so an intact block never causes a rewrite;
    '   - otherwise: the user's own content (StripMonkModeBlock removes any
    '     partial/tampered remnant of our block, preserving the rest
    '     byte-for-byte) + a single CRLF separator + expectedBlock. A blanked
    '     hosts file repairs to the snapshot alone.
    ' Shared and file-system-free so it can be unit tested.
    Friend Shared Function RepairHostsBlock(ByVal hostsText As String, ByVal expectedBlock As String) As String

        If String.IsNullOrWhiteSpace(expectedBlock) Then
            Return Nothing
        End If
        If hostsText Is Nothing Then
            hostsText = ""
        End If
        If hostsText.IndexOf(expectedBlock, StringComparison.Ordinal) >= 0 Then
            Return Nothing
        End If

        Dim userContent As String = StripMonkModeBlock(hostsText)
        If userContent.Length = 0 Then
            Return expectedBlock
        End If
        Return userContent & vbCrLf & expectedBlock
    End Function

    Private Sub stopMe()

        Dim fileReader As String = ""
        Dim original As String = ""
        Dim hostsFileNeedsRemoval As Boolean = False

        If My.Computer.FileSystem.FileExists(hostDirS) Then
            SetAttr(hostDirS, vbNormal)
            fileReader = My.Computer.FileSystem.ReadAllText(hostDirS)
            If fileReader.Contains("#### MonkMode Entries ####") Then
                hostsFileNeedsRemoval = True
            End If
        End If

        If hostsFileNeedsRemoval Then
            original = StripMonkModeBlock(fileReader)

            Dim fs2 As New FileStream(hostDirS, FileMode.Create, FileAccess.Write, FileShare.Read)
            Dim sw2 As New StreamWriter(fs2)
            sw2.Write(original)
            sw2.Close()
            SetAttr(hostDirS, vbReadOnly)

            Dim iniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            iniFile.SetKeyValue("User", "Done", "yes")
            iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
        Else
            SetAttr(hostDirS, vbReadOnly)
        End If

        ' The block is over - drop the repair snapshot (best effort) so an
        ' expired block leaves nothing behind to self-heal back in.
        Try
            System.IO.File.Delete(Application.StartupPath + "\monkmode_hosts.block")
        Catch ex As Exception
        End Try

        Me.Stop()
        End

    End Sub

    Private Sub adder_Changed(ByVal sender As System.Object, ByVal e As System.IO.FileSystemEventArgs) Handles adder.Changed

        If My.Computer.FileSystem.FileExists(sWinDir & "\system32\drivers\etc\add_to_hosts") Then
            Dim toAdd As String
            toAdd = System.IO.File.ReadAllText(sWinDir & "\system32\drivers\etc\add_to_hosts")
            SetAttr(hostDirS, vbNormal)
            System.IO.File.AppendAllText(hostDirS, toAdd)
            SetAttr(hostDirS, vbReadOnly)
            ' Mirror the append into the repair snapshot (best effort) so a
            ' later B2 self-heal restores the added sites too. Only when the
            ' snapshot already exists: creating one here would make a
            ' marker-less "expected block" that a repair would then write and
            ' the expiry strip could never remove.
            Try
                Dim snapshotPath As String = Application.StartupPath + "\monkmode_hosts.block"
                If My.Computer.FileSystem.FileExists(snapshotPath) Then
                    System.IO.File.AppendAllText(snapshotPath, toAdd)
                End If
            Catch ex As Exception
            End Try
            Try
                System.IO.File.Delete(sWinDir & "\system32\drivers\etc\add_to_hosts")
            Catch ex As Exception
            End Try
        End If

    End Sub

    'Private Sub SystemEvents_PowerModeChanged(ByVal sender As Object, ByVal e As PowerModeChangedEventArgs)
    '    If e.Mode = PowerModes.Suspend Then
    '        Dim processList = System.Diagnostics.Process.GetProcessesByName("mm_notify")
    '        For Each Proc In processList
    '            Try
    '                Proc.Kill()
    '            Catch ex As Exception
    '            End Try
    '        Next
    '        Dim processList2 = System.Diagnostics.Process.GetProcessesByName("mm_notify2")
    '        For Each Proc In processList2
    '            Try
    '                Proc.Kill()
    '            Catch ex As Exception
    '            End Try
    '        Next
    '    End If
    '    If e.Mode = PowerModes.Resume Then
    '    End If

    'End Sub

End Class

Public NotInheritable Class Simple3Des
    Private TripleDes As New TripleDESCryptoServiceProvider
    Private Function TruncateHash(
        ByVal key As String,
        ByVal length As Integer) As Byte()

        Dim sha1 As New SHA1CryptoServiceProvider

        ' Hash the key.
        Dim keyBytes() As Byte =
            System.Text.Encoding.Unicode.GetBytes(key)
        Dim hash() As Byte = sha1.ComputeHash(keyBytes)

        ' Truncate or pad the hash.
        ReDim Preserve hash(length - 1)
        Return hash
    End Function
    Sub New(ByVal key As String)
        ' Initialize the crypto provider.
        TripleDes.Key = TruncateHash(key, TripleDes.KeySize \ 8)
        TripleDes.IV = TruncateHash("", TripleDes.BlockSize \ 8)
    End Sub
    Public Function EncryptData(
        ByVal plaintext As String) As String

        ' Convert the plaintext string to a byte array.
        Dim plaintextBytes() As Byte =
            System.Text.Encoding.Unicode.GetBytes(plaintext)

        ' Create the stream.
        Dim ms As New System.IO.MemoryStream
        ' Create the encoder to write to the stream.
        Dim encStream As New CryptoStream(ms,
            TripleDes.CreateEncryptor(),
            System.Security.Cryptography.CryptoStreamMode.Write)

        ' Use the crypto stream to write the byte array to the stream.
        encStream.Write(plaintextBytes, 0, plaintextBytes.Length)
        encStream.FlushFinalBlock()

        ' Convert the encrypted stream to a printable string.
        Return Convert.ToBase64String(ms.ToArray)
    End Function
    Public Function DecryptData(
    ByVal encryptedtext As String) As String
        Dim encryptedBytes() As Byte
        ' Convert the encrypted text string to a byte array.
        Try
            encryptedBytes = Convert.FromBase64String(encryptedtext)
        Catch ef As System.FormatException
            'encryptedBytes = 
            End
        End Try
        ' Create the stream.
        Dim ms As New System.IO.MemoryStream
        ' Create the decoder to write to the stream.
        Dim decStream As New CryptoStream(ms,
            TripleDes.CreateDecryptor(),
            System.Security.Cryptography.CryptoStreamMode.Write)

        ' Use the crypto stream to write the byte array to the stream.
        decStream.Write(encryptedBytes, 0, encryptedBytes.Length)
        decStream.FlushFinalBlock()

        ' Convert the plaintext stream to a string.
        Return System.Text.Encoding.Unicode.GetString(ms.ToArray)
    End Function

End Class