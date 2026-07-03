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
    ' Config datetimes are en-CA strings (the service parses with en-CA); always
    ' pass this explicitly — SystemEvents handlers run on a system broadcast
    ' thread, not the UI thread whose culture the constructor sets. Shared so
    ' the testable ComputeCompensatedUntil helper can use it.
    Private Shared ReadOnly CA As New CultureInfo("en-CA")
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

    ' B7: build the canonical (decrypted plaintext, fixed order) the MAC is over,
    ' from a loaded ini. Byte-identical construction to the CLI's CanonicalFromIni
    ' and the service/guardian readers - every party must agree on the input.
    ' Friend (not Private) so the end-to-end parity tests can prove this reader
    ' agrees with the CLI writer and the other readers - a tautological
    ' BuildCanonical literal comparison would miss a drift in THIS wrapper.
    Friend Function CanonicalFromIni(ByVal ini As IniFile) As String
        Dim untilEnc As String = ini.GetKeyValue("Time", "Until")
        Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
        Dim coolOffEnc As String = ini.GetKeyValue("Time", "CoolOffUntil")
        Dim procEnc As String = ini.GetKeyValue("Process", "List")
        Dim nowEnc As String = ini.GetKeyValue("CurrentTime", "Now")
        Dim sites As String = ini.GetKeyValue("User", "CustomSites")
        ' C3b: the [Partner] fields are stored PLAINTEXT (as-stored, like CustomSites -
        ' NOT decrypted like the datetimes); absent => "" (a v4 config read under v5
        ' code therefore builds a different canonical and freezes, R9). MAC-covered.
        Dim partnerSalt As String = ini.GetKeyValue("Partner", "Salt")
        Dim partnerHash As String = ini.GetKeyValue("Partner", "Hash")
        Dim partnerUnlockedAt As String = ini.GetKeyValue("Partner", "UnlockedAt")
        ' C4: the [Commit] Committed flag ("yes"/"no", plaintext-as-stored, MAC-covered).
        Dim committed As String = ini.GetKeyValue("Commit", "Committed")
        ' C5b: [Schedule] Spec is the recurring-window rule stored PLAINTEXT (as-stored,
        ' like CustomSites/[Partner] - NOT decrypted); [Schedule] ActiveUntil is an
        ' ENCRYPTED datetime like CoolOffUntil ("" = no window open). Absent => "" (a v6
        ' config read under v7 code builds a different canonical and freezes, R9).
        Dim scheduleSpec As String = ini.GetKeyValue("Schedule", "Spec")
        Dim scheduleActiveEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")

        Dim untilPlain As String = If(untilEnc = "", "", enc.DecryptData(untilEnc))
        Dim highWaterPlain As String = If(highWaterEnc = "", "", enc.DecryptData(highWaterEnc))
        ' C2b: CoolOffUntil is an encrypted datetime like Until/HighWater; absent/
        ' empty ("" - no cooling-off pending) passes through verbatim.
        Dim coolOffPlain As String = If(coolOffEnc = "", "", enc.DecryptData(coolOffEnc))
        Dim procPlain As String = If(procEnc = "" OrElse procEnc = "null", procEnc, enc.DecryptData(procEnc))
        Dim nowPlain As String = If(nowEnc = "", "", enc.DecryptData(nowEnc))
        ' C5b: ScheduleActiveUntil decrypts exactly like CoolOffUntil ("" = no window open).
        Dim scheduleActivePlain As String = If(scheduleActiveEnc = "", "", enc.DecryptData(scheduleActiveEnc))

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion, untilPlain, procPlain, sites, nowPlain, highWaterPlain, coolOffPlain, partnerSalt, partnerHash, partnerUnlockedAt, committed, scheduleSpec, scheduleActivePlain)
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
    ' never acts on a half-updated config mid clock-change. NOTE: this leaves the
    ' notifier's B7 comp helpers (ConfigMacIsValidForIni / RestampMacWithExistingKey
    ' / CanonicalFromIni / ComputeCompensatedUntil) unused - retained for now, safe
    ' to delete in a later cleanup.
    Private Sub SystemEvents_TimeChanged(ByVal sender As Object, ByVal e As EventArgs)
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            ini.SetKeyValue("Time", "TimeChanging", "yes")
            ini.Save(IniPath())

            System.Threading.Thread.Sleep(2000)

            ini.Load(IniPath())
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
