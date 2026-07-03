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
    ' B5a: the browser-DoH-policy snapshot (the user's prior policy values captured
    ' at block start) so teardown can restore them with no data loss.
    Public Const DohSnapshotName As String = "monkmode_doh.snapshot"
    ' C2b: the two presence-only cooling-off trigger files the CLI drops in
    ' AppDir(). Their CONTENT IS IGNORED by the service (R2): the CLI holds the
    ' MAC-stamping pattern, so any CLI-written timing would be forgeable under a
    ' valid MAC - the request channel carries ZERO timing authority; the service
    ' alone computes and writes the MAC-covered deadline. Parity-pinned with the
    ' service copies (Service1.CoolOff*FileName), like SnapshotName/BackupFileName.
    Public Const CoolOffRequestFileName As String = "monkmode_cooloff.request"
    Public Const CoolOffCancelFileName As String = "monkmode_cooloff.cancel"
    ' C3b: the ONE content-bearing partner-code trigger the CLI drops in AppDir()
    ' (unblock --code). Unlike the cooling-off triggers, its CONTENT is read - but
    ' as a verified ATTEMPT the service KDF-checks against a MAC-covered verifier
    ' (R2), never as a command it obeys. The CLI has ZERO lift authority: it can
    ' only submit a candidate; the service alone verifies and lifts. Parity-pinned
    ' with the service copy (Service1.PartnerCodeFileName).
    Public Const PartnerCodeFileName As String = "monkmode_partner.code"
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

    ' C1b (R8): the MAC-covered shadow copy of the ini, next to the exes/ini.
    ' Written by RefreshBackup after every legitimate (MAC-valid) CLI write; the
    ' service restores the primary from it if the primary is found corrupt/blanked/
    ' short (instead of freezing into the unstamped panic default). ConfigBackup is
    ' the parity-pinned single source of truth for the filename.
    Public Function IniBackupPath() As String
        Return Path.Combine(AppDir(), ConfigBackup.BackupFileName)
    End Function

    ' Snapshot of the exact MonkMode hosts block written for the current block,
    ' kept next to the exes/ini. The service reads it every timer tick to
    ' restore the entries if an admin clears the read-only attribute and edits
    ' or blanks hosts between ticks (B2 self-heal).
    Public Function SnapshotPath() As String
        Return Path.Combine(AppDir(), SnapshotName)
    End Function

    ' B5a: the browser-DoH-policy snapshot path, next to the exes like the hosts
    ' snapshot. Written by WriteDohSnapshot at block start; read by RemoveDohPolicy
    ' (the escape hatch) and the service's own RemoveDohPolicy at expiry.
    Public Function DohSnapshotPath() As String
        Return Path.Combine(AppDir(), DohSnapshotName)
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
            ' decrypting possibly-garbage Until/HighWater: DecryptData returns ""
            ' on bad Base64 (all four copies now, incl. the service's inline one -
            ' the old service End()-on-junk availability bypass is fixed), but a
            ' valid-Base64/invalid-ciphertext value still throws, so the CLI just
            ' shouldn't feed it junk.
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

    ' C4: is the active block committed (self-serve cooling-off disabled = code-only
    ' exit)? Best-effort, for the CLI's `unblock` WARNING ONLY - it has ZERO
    ' enforcement authority (the service alone adjudicates cooling-off). Gated on a
    ' valid MAC so a tampered/frozen config never yields a misleading "committed"
    ' message; returns False on any read/parse failure.
    Public Function BlockIsCommitted() As Boolean
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            If Not ConfigMacIsValidForIni(ini) Then Return False
            Return String.Equals(If(ini.GetKeyValue("Commit", "Committed"), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase)
        Catch
            Return False
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

    ' The service's stopMe() marker-block strip, ported into the CLI so the
    ' `unblock` LIFT path (RestoreHostsFromStrip) removes our block EXACTLY as the
    ' service does at a genuine expiry: cut at the first ordinal marker, then drop
    ' only the single line terminator the writer placed before it, so the user's
    ' own content - including any trailing blank line - is preserved byte-for-byte.
    ' Kept behaviourally identical to monkmode.Service1.StripMonkModeBlock and
    ' pinned by the CLI<->service parity tests; a CLI-side copy is needed because
    ' MonkMode (CLI) and monkmode (service) are separate assemblies that cannot
    ' reference one another - the same reason ServiceSecurity / ConfigIntegrity /
    ' DohPolicy are duplicated and parity-pinned.
    Friend Function StripMonkModeBlock(ByVal text As String) As String
        Dim startpos As Integer = text.IndexOf(Marker, StringComparison.Ordinal)
        If startpos < 0 Then
            Return text
        End If
        Dim original As String = Microsoft.VisualBasic.Left(text, startpos)
        ' Ordinal EndsWith (matching the ordinal marker IndexOf above): the drop
        ' is by index, so a culture-sensitive match on a CRLF followed by a
        ' Unicode-ignorable char (e.g. U+00AD) would chop by count and leave a
        ' dangling CR. Ordinal keeps the strip byte-exact.
        If original.EndsWith(vbCrLf, StringComparison.Ordinal) Then
            original = Microsoft.VisualBasic.Left(original, original.Length - 2)
        ElseIf original.EndsWith(vbLf, StringComparison.Ordinal) OrElse original.EndsWith(vbCr, StringComparison.Ordinal) Then
            original = Microsoft.VisualBasic.Left(original, original.Length - 1)
        End If
        Return original
    End Function

    ' The tail-normalising strip used by the RE-BLOCK path (WriteHostsBlock):
    ' remove our marker block via the byte-for-byte StripMonkModeBlock, then trim
    ' any trailing CR/LF/space/tab, because the caller immediately re-appends
    ' vbCrLf + a fresh block - normalising the tail stops a blank line
    ' accumulating before the marker across repeated blocks. Deliberately NOT
    ' byte-for-byte (that is StripMonkModeBlock's job, used by the lift path):
    ' StripOurBlock(x) == StripMonkModeBlock(x) with the trailing whitespace
    ' trimmed, pinned by the strip-parity tests.
    Friend Function StripOurBlock(ByVal text As String) As String
        Return StripMonkModeBlock(text).TrimEnd(CChar(vbCr), CChar(vbLf), " "c, CChar(vbTab))
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

    ' C3b: WriteConfig now also mints the partner accountability code. It generates
    ' a random code, stores ONLY its salted one-way hash (plaintext-in-ini,
    ' MAC-covered) plus an empty UnlockedAt exit flag, and RETURNS the plaintext
    ' ONCE for the caller to show. The plaintext is NEVER persisted (not the ini,
    ' the C1b backup, the snapshot, or any log). Rotate-on-use: each arm mints a
    ' FRESH code (a used code dies with its block at stopMe()), so a code the user
    ' saw themselves entering can't be banked for the next block.
    Public Function WriteConfig(ByVal domains As IEnumerable(Of String), ByVal apps As IEnumerable(Of String), ByVal untilDate As DateTime, Optional ByVal committed As Boolean = False) As String
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

        ' C3b: mint the partner code. Generate a random code, store ONLY its salted
        ' one-way hash (Base64) + its salt (Base64) + an empty UnlockedAt, all as
        ' PLAINTEXT in [Partner] (they are not reversible secrets - the MAC is what
        ' protects them, exactly like plaintext CustomSites). Set BEFORE StampFreshMac
        ' so the fresh MAC covers them (and the C1b backup below captures them) - the
        ' shown code is then MAC-valid from birth. The plaintext is returned once and
        ' never persisted.
        Dim partnerSalt() As Byte = ConfigIntegrity.NewPartnerSalt()
        Dim partnerCodePlain As String = ConfigIntegrity.GeneratePartnerCode()
        ini.AddSection("Partner")
        ini.SetKeyValue("Partner", "Salt", Convert.ToBase64String(partnerSalt))
        ini.SetKeyValue("Partner", "Hash", ConfigIntegrity.ComputePartnerHash(partnerSalt, partnerCodePlain))
        ini.SetKeyValue("Partner", "UnlockedAt", "")

        ' C4: the commit policy flag, MAC-covered from birth (set BEFORE StampFreshMac,
        ' like the [Partner] fields). A committed block disables self-serve cooling-off
        ' (code-only exit); the partner code + expiry still lift it. Stored NON-empty in
        ' both states ("yes"/"no") so it round-trips cleanly through IniFile (no bare-key
        ' Nothing ambiguity). Clearing/flipping it by raw edit fails the MAC -> freeze.
        ini.AddSection("Commit")
        ini.SetKeyValue("Commit", "Committed", If(committed, "yes", "no"))

        ' B7: stamp a fresh tamper-evident MAC. Generate a per-block HMAC key,
        ' DPAPI-protect it at machine scope into [Integrity] Key, and MAC the
        ' canonical of the plaintext values just written into [Integrity] Mac.
        ' Best-effort: a DPAPI failure must NOT abort arming the block (the
        ' readers then see no/invalid MAC and fail CLOSED = keep enforcing,
        ' which is safe - they just can't auto-lift until a good stamp exists).
        StampFreshMac(ini)

        ini.Save(IniPath())
        ' C1b: capture a MAC-covered shadow copy of the just-armed config, so a
        ' later corrupt/blanked/short primary restores from it instead of freezing
        ' into the unstamped panic default. Only copies if the fresh stamp is
        ' MAC-valid (a DPAPI failure above left it unstamped -> no backup), so the
        ' backup is always a genuinely liftable config.
        RefreshBackup(ini)
        Return partnerCodePlain
    End Function

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
        ' "null" is stored verbatim (not encrypted); only decrypt a real payload.
        Dim procPlain As String = If(procEnc = "" OrElse procEnc = "null", procEnc, enc.DecryptData(procEnc))
        Dim nowPlain As String = If(nowEnc = "", "", enc.DecryptData(nowEnc))
        ' C5b: ScheduleActiveUntil decrypts exactly like CoolOffUntil ("" = no window open).
        Dim scheduleActivePlain As String = If(scheduleActiveEnc = "", "", enc.DecryptData(scheduleActiveEnc))

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion, untilPlain, procPlain, sites, nowPlain, highWaterPlain, coolOffPlain, partnerSalt, partnerHash, partnerUnlockedAt, committed, scheduleSpec, scheduleActivePlain)
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
            ' C1b: keep the shadow backup current with a legitimate `add` (captures
            ' the new CustomSites). RefreshBackup only copies when the ini is
            ' MAC-valid, so a tampered config (macValid=False above, NOT re-stamped)
            ' never overwrites the good backup - the tamper stays frozen and the
            ' backup keeps the last legitimate state.
            RefreshBackup(ini)
        Catch
        End Try
    End Sub

    ' ---- C2b: cooling-off request channel (authority-free trigger files) ----

    ' Drop the cooling-off REQUEST trigger. Presence-only: the service polls for
    ' the file on its next tick (<=10s), computes its OWN floor-clamped deadline
    ' off the monotonic HighWater and consumes the file - nothing the CLI writes
    ' here (content, timestamps) carries any timing authority (R2). Same
    ' file-drop channel shape as add_to_hosts, but in MonkMode's own AppDir zone.
    Public Sub RequestCoolOff()
        File.WriteAllText(Path.Combine(AppDir(), CoolOffRequestFileName), "")
    End Sub

    ' Drop the cooling-off CANCEL trigger: the service clears any pending
    ' CoolOffUntil (back into the block) and consumes both triggers. Cancel WINS
    ' over a simultaneous request (fail-closed: stay blocked).
    Public Sub CancelCoolOff()
        File.WriteAllText(Path.Combine(AppDir(), CoolOffCancelFileName), "")
    End Sub

    ' C3b: drop the partner-code ATTEMPT trigger carrying the candidate code. Unlike
    ' the cooling-off triggers (presence-only, content ignored), this trigger's
    ' CONTENT is read - but the service treats it as an authentication ATTEMPT it
    ' KDF-verifies against the MAC-covered verifier, never as a command it obeys
    ' (R2). The CLI has ZERO lift authority: it can only submit; the service alone
    ' verifies and lifts. The candidate is written PLAINTEXT because the service must
    ' apply the one-way KDF itself (if the CLI pre-hashed with the stored function,
    ' an attacker who can read [Partner] Hash would just drop it in and match) - safe
    ' because the submitter already knows the code they typed, rotate-on-use burns a
    ' used one, and the service never logs it and deletes the trigger after
    ' adjudication (success or failure).
    Public Sub RequestPartnerCode(ByVal code As String)
        File.WriteAllText(Path.Combine(AppDir(), PartnerCodeFileName), code)
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

    ' ---- C1b (R8): config shadow backup ----

    ' Refresh the shadow backup from the primary after a LEGITIMATE write. `ini` is
    ' the in-memory object just saved to IniPath(); CopyIfSourceValid copies the
    ' file ONLY when that object is MAC-valid (the CLI just stamped/re-stamped it),
    ' so a config with no/failed MAC never becomes the backup AND a corrupt primary
    ' can never overwrite a good backup (no data loss). Best-effort: a failed
    ' refresh just leaves the previous good backup in place - the block still arms.
    Public Sub RefreshBackup(ByVal ini As IniFile)
        Try
            ConfigBackup.CopyIfSourceValid(IniPath(), IniBackupPath(), ConfigMacIsValidForIni(ini))
        Catch
        End Try
    End Sub

    ' Remove the shadow backup (escape-hatch teardown), mirroring DeleteSnapshot -
    ' a torn-down block must leave nothing behind to restore an old config from.
    ' Best-effort.
    Public Sub DeleteBackup()
        Try
            File.Delete(IniBackupPath())
        Catch
        End Try
    End Sub

    ' The CLI side of the restore-on-corrupt path: if the primary ini is
    ' corrupt/blanked/short AND a MAC-valid backup exists, restore the primary from
    ' it, so a `status`/`add` sees the real block (self-healed) instead of a
    ' fail-closed blank. Unlike the service's recovery, the CLI NEVER writes a
    ' default block (a status/add on a fresh or idle machine must not arm
    ' anything) - with no trustworthy backup it does nothing and the read paths
    ' keep failing closed. A parseable-but-MAC-invalid (tampered) primary reads as
    ' "usable" here (>= 2 sections) and is left UNTOUCHED, so this never overwrites
    ' a tamper - B7's freeze holds; only a genuinely unreadable primary is
    ' restored, and only from a MAC-valid backup (no data loss). Best-effort; never
    ' throws.
    Public Sub RestorePrimaryFromBackupIfCorrupt()
        Try
            Dim primaryUsable As Boolean = False
            Try
                Dim p As New IniFile
                p.Load(IniPath())
                primaryUsable = ConfigBackup.PrimaryIsStructurallyUsable(p.Sections.Count)
            Catch
                primaryUsable = False
            End Try
            If primaryUsable Then Return   ' nothing to recover; never clobber a usable primary
            Dim backupPath As String = IniBackupPath()
            If Not File.Exists(backupPath) Then Return
            Dim b As New IniFile
            b.Load(backupPath)
            ' THE load-bearing gate: only a MAC-valid backup is ever trusted/copied.
            ConfigBackup.CopyIfSourceValid(backupPath, IniPath(), ConfigMacIsValidForIni(b))
        Catch
        End Try
    End Sub

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
    ' user's own content byte-for-byte via StripMonkModeBlock - the SAME strip
    ' the service's expiry path (stopMe -> StripMonkModeBlock) uses, so lifting a
    ' block through `unblock --force` and lifting it through a natural expiry
    ' leave hosts in identical states. (This path previously called StripOurBlock,
    ' which trims the tail, so the two teardowns diverged on a user's trailing
    ' blank line - whitespace-only, but a real divergence; audit P3 #7.) No-op if
    ' hosts has no MonkMode block.
    Public Sub RestoreHostsFromStrip()
        Dim path As String = HostsPath()
        If Not File.Exists(path) Then Return
        ClearReadOnly(path)
        Dim text As String = File.ReadAllText(path)
        If text.IndexOf(Marker, StringComparison.Ordinal) < 0 Then Return
        ' C1: atomic write - this path's whole job is preserving the user's own
        ' hosts content, so a torn rewrite here is the worst case to guard against.
        AtomicHosts.WriteAtomic(path, StripMonkModeBlock(text))
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

    ' ---- B5a: browser DoH-off policy (snapshot at block start + escape-hatch
    ' restore). The CLI writes the pre-block snapshot and owns the escape-hatch
    ' teardown; the service (its own RemoveDohPolicy) re-asserts + restores at
    ' expiry. The pure decisions live in DohPolicy.vb; the live registry/file I/O
    ' here is the smoke-tested seam. This RemoveDohPolicy is the CLI copy of the
    ' service's (same shared pure helpers), for the `unblock --force` path. ----

    ' Read one policy value (String for REG_SZ, boxed Int32 for a DWORD, or Nothing
    ' if absent). Read-only OpenSubKey.
    Private Function ReadDohValue(ByVal entry As DohPolicy.DohPolicyEntry) As Object
        Using rk As RegistryKey = Registry.LocalMachine.OpenSubKey(entry.SubKey)
            If rk Is Nothing Then Return Nothing
            Return rk.GetValue(entry.ValueName, Nothing)
        End Using
    End Function

    ' Write one policy value at its Kind (creating the key path if needed).
    Private Sub SetDohValue(ByVal entry As DohPolicy.DohPolicyEntry, ByVal value As Object)
        Using rk As RegistryKey = Registry.LocalMachine.CreateSubKey(entry.SubKey)
            If rk IsNot Nothing Then rk.SetValue(entry.ValueName, value, entry.Kind)
        End Using
    End Sub

    ' Delete ONLY our value (never the shared vendor subkey tree). No-op if absent.
    Private Sub DeleteDohValue(ByVal entry As DohPolicy.DohPolicyEntry)
        Using rk As RegistryKey = Registry.LocalMachine.OpenSubKey(entry.SubKey, True)
            If rk IsNot Nothing Then rk.DeleteValue(entry.ValueName, False)
        End Using
    End Sub

    ' B5a: snapshot the user's CURRENT browser DoH policy values BEFORE the service
    ' forces them off, so teardown can restore the pre-block state with no data
    ' loss. Called at block start, BEFORE InstallAndStart (the service sets the
    ' policy in its OnStart, after this). Best-effort: a failed snapshot write must
    ' not abort arming the block - teardown then degrades to "remove only our
    ' lingering off" (RemoveDohPolicy's no-snapshot path), like B2 without its snapshot.
    ' Returns True on success. False => teardown can't restore the user's prior DoH
    ' policy (it will DO NOTHING at expiry - fail-safe, our "off" may linger), so the
    ' caller warns the user. Never throws / never aborts arming the block.
    Public Function WriteDohSnapshot() As Boolean
        Try
            Dim ents As DohPolicy.DohPolicyEntry() = DohPolicy.Entries
            Dim priors(ents.Length - 1) As Object
            For i As Integer = 0 To ents.Length - 1
                priors(i) = ReadDohValue(ents(i))
            Next
            File.WriteAllText(DohSnapshotPath(), DohPolicy.BuildSnapshot(priors))
            Return True
        Catch
            Return False
        End Try
    End Function

    ' B5a escape-hatch teardown (the CLI copy of the service's RemoveDohPolicy).
    ' Restore each browser DoH policy value to the user's prior state from the
    ' snapshot (no data loss - restore the prior, or delete our value where it was
    ' ABSENT before), then consume the snapshot. When there is NO snapshot (write
    ' failed at block start, or a prior teardown consumed it) DO NOTHING: with no
    ' authoritative record that WE created the current value, deleting it could
    ' clobber the user's own value (e.g. a user who already had DoH off) - the
    ' paramount no-data-loss fence. Cost = a rare lingering "off"; fail-safe.
    ' Best-effort, per-entry.
    Public Sub RemoveDohPolicy()
        Dim path As String = DohSnapshotPath()
        Dim haveSnapshot As Boolean = False
        Dim parsed As Object() = Nothing
        Try
            If File.Exists(path) Then
                parsed = DohPolicy.ParseSnapshot(File.ReadAllText(path))
                haveSnapshot = True
            End If
        Catch
            haveSnapshot = False
        End Try

        ' No authoritative snapshot => do nothing (never delete a value we cannot
        ' prove we created).
        If Not haveSnapshot Then Return

        Dim ents As DohPolicy.DohPolicyEntry() = DohPolicy.Entries
        For i As Integer = 0 To ents.Length - 1
            Dim entry As DohPolicy.DohPolicyEntry = ents(i)
            Try
                Dim action = DohPolicy.RestoreActionFor(entry, parsed(i))
                If action.delete Then
                    DeleteDohValue(entry)
                Else
                    SetDohValue(entry, action.value)
                End If
            Catch
            End Try
        Next

        Try
            File.Delete(path)
        Catch
        End Try
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
