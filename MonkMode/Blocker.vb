'    MonkMode - Blocker
'
'    Core logic shared by the CLI verbs. Writes the hosts file, the encrypted
'    config consumed by the MonkMode service, installs/starts the service, and
'    registers the user-session notifier.
'
'    IMPORTANT: the service (MonkMode_srv) is unchanged, so everything here must
'    honor the contract it expects:
'      - config file:  <appdir>\monkmode_settings.ini
'      - sections:     [Process] List, [User] *, [Time] Until/TimeChanging,
'                      [CurrentTime] Now
'      - crypto:       Simple3Des("mm_textbox")
'      - hosts marker: "#### MonkMode Entries ####"
'      - datetimes:    stored as en-CA strings (the service parses with en-CA)
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization
Imports System.IO
Imports System.ServiceProcess
Imports Microsoft.Win32
Imports MonkMode.IniFile
Imports ServiceTools

Module Blocker

    Public Const ServiceName As String = "MONKMODE"
    Public Const ServiceDisplay As String = "MonkMode"
    Public Const IniName As String = "monkmode_settings.ini"
    Public Const SnapshotName As String = "monkmode_hosts.block"
    Public Const Marker As String = "#### MonkMode Entries ####"
    Public Const ServiceExeName As String = "MonkMode_srv.exe"
    Public Const NotifierExeName As String = "mm_notify.exe"
    Public Const RunValueName As String = "MonkMode_notify"

    ' B7 tamper-evident config: the [Integrity] section holds the DPAPI-protected
    ' HMAC key and the MAC over the canonical of the decrypted config values.
    ' Both are EXCLUDED from the canonical (you can't MAC the MAC).
    Public Const IntegritySection As String = "Integrity"
    Public Const IntegrityKeyName As String = "Key"
    Public Const IntegrityMacName As String = "Mac"

    Public ReadOnly CA As CultureInfo = New CultureInfo("en-CA")
    Private ReadOnly enc As New Simple3Des("mm_textbox")

    Public Function AppDir() As String
        Return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
    End Function

    Public Function IniPath() As String
        Return Path.Combine(AppDir(), IniName)
    End Function

    ' Snapshot of the exact MonkMode hosts block written for the current block,
    ' kept next to the exes/ini. The service reads it every timer tick to
    ' restore the entries if an admin clears the read-only attribute and edits
    ' or blanks hosts between ticks (B2 self-heal).
    Public Function SnapshotPath() As String
        Return Path.Combine(AppDir(), SnapshotName)
    End Function

    Public Function HostsPath() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
    End Function

    Public Function EtcDir() As String
        Return Path.GetDirectoryName(HostsPath())
    End Function

    ' ---- service state ----

    Public Function ServiceIsInstalled() As Boolean
        For Each sc As ServiceController In ServiceController.GetServices()
            If String.Equals(sc.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Public Function ServiceIsRunning() As Boolean
        If Not ServiceIsInstalled() Then Return False
        Try
            Using sc As New ServiceController(ServiceName)
                Return sc.Status = ServiceControllerStatus.Running OrElse sc.Status = ServiceControllerStatus.StartPending
            End Using
        Catch
            Return False
        End Try
    End Function

    ' Returns the active block end time, or DateTime.MinValue if none/unreadable.
    Public Function ActiveBlockEnd() As DateTime
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Dim s As String = enc.DecryptData(ini.GetKeyValue("Time", "Until"))
            Dim dt As DateTime
            If DateTime.TryParse(s, CA, DateTimeStyles.None, dt) Then Return dt
        Catch
        End Try
        Return DateTime.MinValue
    End Function

    Public Function BlockIsActive() As Boolean
        Return ServiceIsRunning() AndAlso ActiveBlockEnd() > DateTime.Now
    End Function

    Public Function BlockedSites() As String
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Dim s As String = ini.GetKeyValue("User", "CustomSites")
            If s Is Nothing OrElse s = "null" Then Return ""
            Return s.TrimEnd(";"c)
        Catch
            Return ""
        End Try
    End Function

    Public Function BlockedApps() As String
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Dim s As String = ini.GetKeyValue("Process", "List")
            If s Is Nothing OrElse s = "null" Then Return ""
            Return enc.DecryptData(s).TrimEnd(";"c)
        Catch
            Return ""
        End Try
    End Function

    ' ---- hosts helpers ----

    Private Function NormalizeDomain(ByVal d As String) As String
        d = d.Trim().ToLowerInvariant()
        ' strip scheme and any path if a URL was pasted
        If d.Contains("://") Then d = d.Substring(d.IndexOf("://") + 3)
        Dim slash As Integer = d.IndexOf("/"c)
        If slash >= 0 Then d = d.Substring(0, slash)
        Return d.Trim()
    End Function

    ' Use 127.0.0.1, NOT 0.0.0.0: Windows' DNS resolver does not honor 0.0.0.0
    ' (INADDR_ANY) hosts entries and falls through to real DNS, so 0.0.0.0 does
    ' not block. A loopback hosts entry IS honored and suppresses the real A AND
    ' AAAA lookups for that name, so a single 127.0.0.1 line fully blocks it.
    Public Function BuildHostsEntries(ByVal domains As IEnumerable(Of String)) As String
        Dim sb As New System.Text.StringBuilder
        For Each raw As String In domains
            Dim d As String = NormalizeDomain(raw)
            If d = "" Then Continue For
            sb.Append("127.0.0.1 ").Append(d).Append(vbCrLf)
            If Not d.StartsWith("www.") AndAlso d.IndexOf("."c) = d.LastIndexOf("."c) Then
                ' bare second-level domain -> also block www.
                sb.Append("127.0.0.1 www.").Append(d).Append(vbCrLf)
            End If
        Next
        Return sb.ToString()
    End Function

    Private Sub ClearReadOnly(ByVal path As String)
        If File.Exists(path) Then
            Dim a As FileAttributes = File.GetAttributes(path)
            If (a And FileAttributes.ReadOnly) = FileAttributes.ReadOnly Then
                File.SetAttributes(path, a And (Not FileAttributes.ReadOnly))
            End If
        End If
    End Sub

    Friend Function StripOurBlock(ByVal text As String) As String
        Dim idx As Integer = text.IndexOf(Marker, StringComparison.Ordinal)
        If idx >= 0 Then text = text.Substring(0, idx)
        Return text.TrimEnd(CChar(vbCr), CChar(vbLf), " "c, CChar(vbTab))
    End Function

    ' The exact MonkMode-owned text appended to hosts: the marker line plus the
    ' entry lines. WriteHostsBlock writes this same string to both hosts and the
    ' snapshot file, so the two can never drift. Friend so tests can assert
    ' that parity without touching the real hosts file.
    Friend Function BuildMonkModeBlock(ByVal domains As IEnumerable(Of String)) As String
        Return Marker & vbCrLf & BuildHostsEntries(domains)
    End Function

    Public Sub WriteHostsBlock(ByVal domains As IEnumerable(Of String))
        Dim path As String = HostsPath()
        ClearReadOnly(path)
        Dim existing As String = ""
        If File.Exists(path) Then existing = File.ReadAllText(path)
        Dim baseText As String = StripOurBlock(existing)
        Dim block As String = BuildMonkModeBlock(domains)
        File.WriteAllText(path, baseText & vbCrLf & block)
        ' Persist the exact block just written so the service can restore it if
        ' hosts is tampered with mid-block (B2 self-heal). Best-effort: a failed
        ' snapshot write must not abort DoBlock between the hosts write and the
        ' service install — without a snapshot, enforcement simply degrades to
        ' the pre-snapshot behaviour (attribute re-assert only).
        Try
            File.WriteAllText(SnapshotPath(), block)
        Catch
        End Try
    End Sub

    ' ---- config (ini) ----

    Private Function PackList(ByVal items As IEnumerable(Of String)) As String
        Dim parts As New List(Of String)
        For Each it As String In items
            Dim s As String = it.Trim()
            If s <> "" Then parts.Add(s)
        Next
        If parts.Count = 0 Then Return ""
        Return String.Join(";", parts) & ";"
    End Function

    Private Function PackApps(ByVal apps As IEnumerable(Of String)) As String
        Dim parts As New List(Of String)
        For Each a As String In apps
            Dim s As String = a.Trim()
            If s = "" Then Continue For
            If Not s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then s &= ".exe"
            parts.Add(s)
        Next
        If parts.Count = 0 Then Return ""
        Return String.Join(";", parts) & ";"
    End Function

    Public Sub WriteConfig(ByVal domains As IEnumerable(Of String), ByVal apps As IEnumerable(Of String), ByVal untilDate As DateTime)
        Dim ini As New IniFile
        Dim appList As String = PackApps(apps)
        Dim siteList As String = PackList(domains)

        ini.AddSection("Process")
        ini.SetKeyValue("Process", "List", If(appList = "", "null", enc.EncryptData(appList)))

        ini.AddSection("User")
        ini.SetKeyValue("User", "CustomChecked", "")
        ini.SetKeyValue("User", "CustomSites", If(siteList = "", "null", siteList))
        ini.SetKeyValue("User", "Done", "no")
        ini.SetKeyValue("User", "NeedsAlerted", "yes")

        ini.AddSection("Time")
        ini.SetKeyValue("Time", "Until", enc.EncryptData(untilDate.ToString(CA)))
        ini.SetKeyValue("Time", "TimeChanging", "no")

        ini.AddSection("CurrentTime")
        ini.SetKeyValue("CurrentTime", "Now", enc.EncryptData(DateTime.Now.ToString(CA)))

        ' B7: stamp a fresh tamper-evident MAC. Generate a per-block HMAC key,
        ' DPAPI-protect it at machine scope into [Integrity] Key, and MAC the
        ' canonical of the plaintext values just written into [Integrity] Mac.
        ' Best-effort: a DPAPI failure must NOT abort arming the block (the
        ' readers then see no/invalid MAC and fail CLOSED = keep enforcing,
        ' which is safe - they just can't auto-lift until a good stamp exists).
        StampFreshMac(ini)

        ini.Save(IniPath())
    End Sub

    ' B7: builds the canonical string the MAC is computed over, from a loaded
    ' ini. Uses the DECRYPTED plaintext for the encrypted fields ([Time] Until,
    ' [Process] List, [CurrentTime] Now) and the as-stored value for the
    ' plaintext [User] CustomSites, so every party (this writer, plus the
    ' service/guardian/notifier readers) derives a byte-identical canonical.
    ' [Integrity] Key/Mac are excluded. Missing values pass through as "".
    Friend Function CanonicalFromIni(ByVal ini As IniFile) As String
        Dim untilEnc As String = ini.GetKeyValue("Time", "Until")
        Dim procEnc As String = ini.GetKeyValue("Process", "List")
        Dim nowEnc As String = ini.GetKeyValue("CurrentTime", "Now")
        Dim sites As String = ini.GetKeyValue("User", "CustomSites")

        Dim untilPlain As String = If(untilEnc = "", "", enc.DecryptData(untilEnc))
        ' "null" is stored verbatim (not encrypted); only decrypt a real payload.
        Dim procPlain As String = If(procEnc = "" OrElse procEnc = "null", procEnc, enc.DecryptData(procEnc))
        Dim nowPlain As String = If(nowEnc = "", "", enc.DecryptData(nowEnc))

        Return ConfigIntegrity.BuildCanonical(untilPlain, procPlain, sites, nowPlain)
    End Function

    ' B7: generate a new HMAC key, protect it into [Integrity] Key, and stamp
    ' [Integrity] Mac over the current canonical. Best-effort throughout - on a
    ' DPAPI/crypto failure the block still arms (readers fail closed). Mutates
    ' the ini in place; the caller saves.
    Private Sub StampFreshMac(ByVal ini As IniFile)
        Try
            Dim key() As Byte = ConfigIntegrity.NewRandomKey()
            Dim protectedKey As String = ConfigIntegrity.ProtectKey(key)
            If protectedKey Is Nothing Then Return
            ini.AddSection(IntegritySection)
            ini.SetKeyValue(IntegritySection, IntegrityKeyName, protectedKey)
            ini.SetKeyValue(IntegritySection, IntegrityMacName, ConfigIntegrity.ComputeConfigMac(CanonicalFromIni(ini), key))
        Catch ex As Exception
        End Try
    End Sub

    ' ---- notifier ----

    Public Sub RegisterAndLaunchNotifier()
        Dim notifier As String = Path.Combine(AppDir(), NotifierExeName)
        If Not File.Exists(notifier) Then Return
        Try
            Using rk As RegistryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
                If rk IsNot Nothing Then rk.SetValue(RunValueName, notifier)
            End Using
        Catch
        End Try
        Try
            Process.Start(notifier)
        Catch
        End Try
    End Sub

    ' ---- add sites to an active block ----

    Public Sub AppendAddToHosts(ByVal domains As IEnumerable(Of String))
        Dim entries As String = BuildHostsEntries(domains)
        Dim addFile As String = Path.Combine(EtcDir(), "add_to_hosts")
        File.WriteAllText(addFile, entries)

        ' keep CustomSites in the config in sync (best effort)
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Dim cur As String = ini.GetKeyValue("User", "CustomSites")
            If cur Is Nothing OrElse cur = "null" Then cur = ""
            Dim merged As String = cur & PackList(domains)
            ini.SetKeyValue("User", "CustomSites", If(merged = "", "null", merged))
            ' B7: this rewrote a MAC-covered field ([User] CustomSites), so the
            ' existing [Integrity] Mac no longer matches. Re-stamp it over the new
            ' canonical, reusing the SAME [Integrity] Key (the block is unchanged
            ' otherwise - never mint a new key here). If the key can't be
            ' unprotected, leave the (now stale) MAC be: the readers will fail
            ' closed (keep enforcing) rather than auto-lift, which is the safe
            ' direction. Best-effort, like the CustomSites sync itself.
            RestampMacWithExistingKey(ini)
            ini.Save(IniPath())
        Catch
        End Try
    End Sub

    ' B7: recompute [Integrity] Mac over the current canonical using the already
    ' stored [Integrity] Key (DPAPI-unprotected). Used when a writer changes a
    ' MAC-covered field of an EXISTING block without re-arming it. No-op if there
    ' is no recoverable key; never throws.
    Private Sub RestampMacWithExistingKey(ByVal ini As IniFile)
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return
            ini.SetKeyValue(IntegritySection, IntegrityMacName, ConfigIntegrity.ComputeConfigMac(CanonicalFromIni(ini), key))
        Catch ex As Exception
        End Try
    End Sub

End Module
