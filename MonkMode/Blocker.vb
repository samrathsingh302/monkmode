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

    ' Process names (no .exe) for the watchdog pair + notifier the escape hatch
    ' must kill, and the guardian exe name. Kept here as the CLI's single source
    ' of truth.
    Public Const ServiceProcessName As String = "MonkMode_srv"
    Public Const GuardProcessName As String = "mm_guard"
    Public Const NotifierProcessName As String = "mm_notify"

    ' B3 SafeBoot leaf keys the escape hatch removes (relative to HKLM). Mirror of
    ' the service's Service1.SafeBootMinimalKey/SafeBootNetworkKey - a parity test
    ' pins them equal so this CLI copy can't drift from what the service writes.
    Public Const SafeBootMinimalKey As String = "SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE"
    Public Const SafeBootNetworkKey As String = "SYSTEM\CurrentControlSet\Control\SafeBoot\Network\MONKMODE"

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

    ' Pure: is the block GENUINELY expired? Mirrors the service's
    ' EffectiveBlockHasExpired (grace 0): expired ONLY when the config MAC is valid
    ' (B7) AND the end time is at/before the trusted HIGH-WATER mark (B4) - NOT raw
    ' DateTime.Now. Fail-CLOSED: an invalid MAC or an unparseable Until/HighWater
    ' reads as NOT expired (block still standing). Friend so it is unit-tested.
    Friend Function BlockGenuinelyExpired(ByVal macValid As Boolean, ByVal untilText As String, ByVal highWaterText As String) As Boolean
        If Not macValid Then Return False
        Dim untilDt As DateTime, hwDt As DateTime
        If Not DateTime.TryParse(untilText, CA, DateTimeStyles.None, untilDt) Then Return False
        If Not DateTime.TryParse(highWaterText, CA, DateTimeStyles.None, hwDt) Then Return False
        Return untilDt <= hwDt
    End Function

    Public Function BlockIsActive() As Boolean
        ' A standing block must NOT be overwritable by `block` unless it is
        ' GENUINELY expired by the SAME MAC + high-water rule the service enforces.
        ' The old check (ActiveBlockEnd() > DateTime.Now) decided liveness off the
        ' raw wall clock, so an attacker could roll the clock forward past Until ->
        ' BlockIsActive()=False -> `monkmode block --for 1m` overwrote the standing
        ' block with a fresh short one, bypassing B4/B7 through the CLI seam. Now we
        ' decide off the persisted high-water mark (a clock-forward can't advance
        ' it) and the MAC (a tampered/frozen block stays active). Fail CLOSED: a
        ' running service with an unreadable/tampered config reads as ACTIVE, so the
        ' standing block is never overwritten - the exit remains `unblock --force`.
        If Not ServiceIsRunning() Then Return False
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            ' Invalid MAC (tampered/unstamped/frozen) => active. Also avoids
            ' decrypting possibly-garbage Until/HighWater (the service's DecryptData
            ' End()s on bad Base64; the CLI just shouldn't feed it junk).
            If Not ConfigMacIsValidForIni(ini) Then Return True
            Dim untilStr As String = enc.DecryptData(ini.GetKeyValue("Time", "Until"))
            Dim hwStr As String = enc.DecryptData(ini.GetKeyValue("Time", "HighWater"))
            Return Not BlockGenuinelyExpired(True, untilStr, hwStr)
        Catch
            Return True
        End Try
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
        ' C1: atomic write (temp + rename) so a crash mid-write can never blank or
        ' half-write hosts and lose the user's own entries. Read-only was cleared
        ' above, so the rename can replace the target.
        AtomicHosts.WriteAtomic(path, baseText & vbCrLf & block)
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
        ' B4: seed the monotonic high-water mark at "now" (en-CA LOCAL, encrypted
        ' like Until). From here the service advances it at most one tick at a
        ' time and never on a clock jump, and decides expiry off it instead of
        ' raw DateTime.Now - so rolling the clock forward past Until can't lift
        ' the block early. Stamped under the MAC by StampFreshMac below, so it
        ' can't be forged past Until without failing verification.
        ini.SetKeyValue("Time", "HighWater", enc.EncryptData(DateTime.Now.ToString(CA)))

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

    ' B7/B4: builds the canonical string the MAC is computed over, from a loaded
    ' ini. Uses the DECRYPTED plaintext for the encrypted fields ([Time] Until,
    ' [Time] HighWater, [Process] List, [CurrentTime] Now) and the as-stored
    ' value for the plaintext [User] CustomSites, so every party (this writer,
    ' plus the service/guardian/notifier readers) derives a byte-identical
    ' canonical. [Integrity] Key/Mac are excluded. Missing values pass through
    ' as "". B4: [Time] HighWater is ENCRYPTED like Until/Now (a datetime
    ' belongs with the datetimes), so it is decrypted here the same way - all
    ' four wrappers must agree or the MAC never validates and blocks freeze.
    Friend Function CanonicalFromIni(ByVal ini As IniFile) As String
        Dim untilEnc As String = ini.GetKeyValue("Time", "Until")
        Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
        Dim procEnc As String = ini.GetKeyValue("Process", "List")
        Dim nowEnc As String = ini.GetKeyValue("CurrentTime", "Now")
        Dim sites As String = ini.GetKeyValue("User", "CustomSites")

        Dim untilPlain As String = If(untilEnc = "", "", enc.DecryptData(untilEnc))
        Dim highWaterPlain As String = If(highWaterEnc = "", "", enc.DecryptData(highWaterEnc))
        ' "null" is stored verbatim (not encrypted); only decrypt a real payload.
        Dim procPlain As String = If(procEnc = "" OrElse procEnc = "null", procEnc, enc.DecryptData(procEnc))
        Dim nowPlain As String = If(nowEnc = "", "", enc.DecryptData(nowEnc))

        Return ConfigIntegrity.BuildCanonical(untilPlain, procPlain, sites, nowPlain, highWaterPlain)
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
            ' B7 fail-open FIX: capture the MAC validity BEFORE we touch any
            ' MAC-covered field. Only re-stamp if the config was already valid -
            ' otherwise `add` would re-bless a TAMPERED config. The attack the
            ' other re-stamp gates close also reaches here: an attacker edits
            ' [Time] Until to "now" (the 3DES key is known by design) => macValid
            ' goes False so the block correctly FREEZES; but `add` only requires
            ' BlockIsActive (service running + a parseable Until), not a valid MAC,
            ' so running `monkmode add` would otherwise mint a fresh VALID MAC over
            ' the tampered canonical and un-freeze the block (it lifts next tick).
            ' When the MAC is invalid we still sync CustomSites best-effort but
            ' leave the stale MAC, so the readers keep failing closed. Mirrors the
            ' service heartbeat/OnStart (ClassifyHeartbeat/ShouldRestampOnStart)
            ' and notifier gates.
            Dim macValid As Boolean = ConfigMacIsValidForIni(ini)
            Dim cur As String = ini.GetKeyValue("User", "CustomSites")
            If cur Is Nothing OrElse cur = "null" Then cur = ""
            Dim merged As String = cur & PackList(domains)
            ini.SetKeyValue("User", "CustomSites", If(merged = "", "null", merged))
            If macValid Then
                ' Re-stamp [Integrity] Mac over the new canonical, reusing the SAME
                ' [Integrity] Key (the block is unchanged otherwise - never mint a
                ' new key here). Safe because the MAC was valid before this edit, so
                ' the only change is our own legitimate CustomSites append.
                RestampMacWithExistingKey(ini)
            End If
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

    ' B7: is [Integrity] Mac currently valid over the canonical? DPAPI-unprotect
    ' [Integrity] Key + FixedTimeEquals. Returns False (never throws) on any
    ' failure - absent/invalid MAC, unreadable key, foreign-machine blob. Mirrors
    ' the service/notifier readers; used to refuse re-stamping a tampered config in
    ' AppendAddToHosts (the B7-class `add` fail-open fix).
    Private Function ConfigMacIsValidForIni(ByVal ini As IniFile) As Boolean
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return False
            Return ConfigIntegrity.ConfigMacIsValid(CanonicalFromIni(ini), ini.GetKeyValue(IntegritySection, IntegrityMacName), key)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' ---- B6 escape hatch primitives (the guaranteed-removal path) ----
    ' These are the brick-insurance teardown steps the `unblock --force` verb
    ' sequences (Program.DoUnblock). Each is best-effort and independent so the
    ' verb can run them in the correct order and continue past any one failure.
    ' They mirror the live-verified cleanup.ps1 emergency teardown exactly.

    ' Kill the watchdog pair (guardian + service) in a retry loop until both stay
    ' down, then the notifier. The caller MUST have disabled SCM recovery first
    ' (ServiceTools.DisableRecovery) or the SCM would resurrect the service
    ' between kills. Returns True if both watchdog processes are gone. Best
    ' effort: a kill that races a restart is retried up to `attempts` times.
    Public Function KillWatchdogProcesses(Optional ByVal attempts As Integer = 8) As Boolean
        Dim bothDown As Boolean = False
        For i As Integer = 1 To attempts
            For Each name As String In New String() {GuardProcessName, ServiceProcessName}
                For Each p As Process In Process.GetProcessesByName(name)
                    Try
                        p.Kill()
                    Catch
                    End Try
                Next
            Next
            If Process.GetProcessesByName(GuardProcessName).Length = 0 AndAlso
               Process.GetProcessesByName(ServiceProcessName).Length = 0 Then
                bothDown = True
                Exit For
            End If
            Threading.Thread.Sleep(500)
        Next
        ' The notifier is harmless once the block is gone, but kill it too so the
        ' teardown leaves nothing behind.
        For Each p As Process In Process.GetProcessesByName(NotifierProcessName)
            Try
                p.Kill()
            Catch
            End Try
        Next
        Return bothDown
    End Function

    ' Unlock hosts and strip ONLY the MonkMode marker block, preserving the
    ' user's own content (reuses StripOurBlock - the same data-loss-safe strip
    ' the service's expiry path uses). No-op if hosts has no MonkMode block.
    Public Sub RestoreHostsFromStrip()
        Dim path As String = HostsPath()
        If Not File.Exists(path) Then Return
        ClearReadOnly(path)
        Dim text As String = File.ReadAllText(path)
        If text.IndexOf(Marker, StringComparison.Ordinal) < 0 Then Return
        ' C1: atomic write - this path's whole job is preserving the user's own
        ' hosts content, so a torn rewrite here is the worst case to guard against.
        AtomicHosts.WriteAtomic(path, StripOurBlock(text))
    End Sub

    ' Remove the B2 hosts snapshot so a reinstalled service can't self-heal the
    ' old block back in. Best-effort.
    Public Sub DeleteSnapshot()
        Try
            File.Delete(SnapshotPath())
        Catch
        End Try
    End Sub

    ' Remove the B3 SafeBoot leaf keys so no orphaned Safe Mode registration
    ' lingers for a service that is being deleted. Only MonkMode's own two leaf
    ' keys are touched (no-data-loss fence). Best-effort, per-key.
    Public Sub RemoveSafeBootKeys()
        For Each subKey As String In New String() {SafeBootMinimalKey, SafeBootNetworkKey}
            Try
                Registry.LocalMachine.DeleteSubKeyTree(subKey, False)
            Catch
            End Try
        Next
    End Sub

    ' Clear the HKCU Run autorun for the notifier. Best-effort.
    Public Sub ClearNotifierAutorun()
        Try
            Using rk As RegistryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
                If rk IsNot Nothing Then rk.DeleteValue(RunValueName, False)
            End Using
        Catch
        End Try
    End Sub

End Module
