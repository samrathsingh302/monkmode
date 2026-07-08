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
    ' C5b (c3): the schedule-only past-[Time] Until SENTINEL and the Spec grammar-version
    ' tag - CLI copies of Service1.ScheduleOnlyExpiredUntil / ScheduleSpecGrammarVersion
    ' (separate assemblies can't reference one another - the same duplication+parity pattern
    ' as SnapshotName / CoolOff*FileName / the ConfigIntegrity copies; a CLI<->service parity
    ' test pins each equal). A schedule-only block has NO manual duration, so `schedule` writes
    ' this fixed, clearly-past, MAC-covered value as [Time] Until: BlockHasExpired(sentinel) is
    ' then always True, so BlockHeld tracks ScheduleActive (self-heals idle between windows) and
    ' the c2 scheduleArmed guard keeps the service+guardian alive between windows. The Spec always
    ' leads with the grammar-version tag so the service parser accepts it.
    Public Const ScheduleOnlyExpiredUntil As String = "1970-01-01 00:00:00"
    Public Const ScheduleSpecGrammarVersion As String = "v1"
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

    ' D5 (rich status): the MONOTONIC cooling-off remaining for `status` - CoolOffUntil - HighWater
    ' (the SAME active-time countdown the service enforces via the B4 mark; NOT the wall clock).
    ' Nothing when no cooling-off is pending ([Time] CoolOffUntil empty), the MAC is invalid
    ' (tampered/frozen - never a misleading countdown, like BlockIsCommitted), the deadline has
    ' already passed (about to lift), or anything is unreadable. Display-only: ZERO enforcement
    ' authority. Best-effort; never throws.
    Public Function CoolOffPendingRemaining() As TimeSpan?
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            If Not ConfigMacIsValidForIni(ini) Then Return Nothing
            Dim coolOffEnc As String = ini.GetKeyValue("Time", "CoolOffUntil")
            If coolOffEnc = "" Then Return Nothing
            Return CoolOffRemainingFrom(enc.DecryptData(coolOffEnc), enc.DecryptData(ini.GetKeyValue("Time", "HighWater")))
        Catch
            Return Nothing
        End Try
    End Function

    ' Pure: cooling-off remaining from a deadline vs the HighWater mark, both en-CA plaintext.
    ' Nothing if either is empty/unparseable OR the remaining is non-positive (a passed/at-deadline
    ' cooling-off is "not pending" - the block is about to lift, so `status` shows no countdown).
    ' Friend so the parse + positivity contract is unit-tested without arming a real cooling-off.
    Friend Function CoolOffRemainingFrom(ByVal deadlineText As String, ByVal highWaterText As String) As TimeSpan?
        Dim deadline As DateTime, mark As DateTime
        If Not DateTime.TryParse(deadlineText, CA, DateTimeStyles.None, deadline) Then Return Nothing
        If Not DateTime.TryParse(highWaterText, CA, DateTimeStyles.None, mark) Then Return Nothing
        Dim remaining As TimeSpan = deadline - mark
        If remaining.TotalSeconds <= 0 Then Return Nothing
        Return remaining
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

    ' ---- D1a: site presets (named category -> domains, INPUT sugar only) ----
    '
    ' A preset is a named bundle of well-known domains the CLI expands into the SAME site
    ' list a user could type by hand with --sites. It is PURE INPUT: the expanded domains
    ' flow into WriteHostsBlock + [User] CustomSites exactly like --sites, so the enforcement
    ' canonical (B7) MAC-covers them downstream with NO new canonical surface and NO schema
    ' bump - the preset TABLE is a compile-time constant, not stored config, so there is
    ' nothing extra to protect. The categories are FIXED (the user picks them, can't edit
    ' them); an EDITABLE user default site list is a separate concern (D1b, stored MAC-covered
    ' on the setup ini, mirroring the C6c cooling-off default). Every domain is the bare
    ' registrable single-label-TLD form, so BuildHostsEntries adds the www. variant and the
    ' same NormalizeDomain scheme/path handling applies as for a hand-typed --sites domain.
    Private ReadOnly PresetTable As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase) From {
        {"social", New String() {"facebook.com", "instagram.com", "twitter.com", "x.com", "tiktok.com", "reddit.com", "snapchat.com", "tumblr.com", "pinterest.com", "linkedin.com", "threads.net"}},
        {"video", New String() {"youtube.com", "netflix.com", "twitch.tv", "hulu.com", "disneyplus.com", "primevideo.com"}},
        {"news", New String() {"cnn.com", "nytimes.com", "foxnews.com", "bbc.com", "buzzfeed.com", "theverge.com"}},
        {"shopping", New String() {"amazon.com", "ebay.com", "etsy.com", "aliexpress.com", "walmart.com", "target.com"}},
        {"adult", New String() {"pornhub.com", "xvideos.com", "xnxx.com", "xhamster.com", "redtube.com", "onlyfans.com"}}
    }

    ' The sorted list of known preset category names (for usage/help + the "unknown preset"
    ' error hint). A read-only snapshot; the table is never mutated. Public so the CLI usage
    ' text lists the live categories rather than a hand-maintained copy that could drift.
    Public Function KnownPresetNames() As String()
        Dim names As New List(Of String)(PresetTable.Keys)
        names.Sort(StringComparer.OrdinalIgnoreCase)
        Return names.ToArray()
    End Function

    ' Expand a comma/semicolon-separated preset argument ("social,video") into the union of
    ' those categories' domains, deduped case-insensitively with order preserved (category
    ' order, then domain order within each). FAIL-CLOSED on an unrecognised category: it
    ' collects EVERY unknown token and returns False with a friendly error listing them + the
    ' valid names, and emits NOTHING - rather than silently expanding only the known ones. In
    ' a self-control tool a typo'd preset must never quietly UNDER-block (the same fail-closed
    ' stance as the schedule day-name parser rejecting an unknown day). An empty/whitespace/
    ' Nothing arg returns True with an empty list (nothing requested - the caller's other site
    ' sources still apply). Friend so the expansion contract is unit-tested without arming a block.
    Friend Function TryExpandPresets(ByVal presetArg As String, ByRef domains As List(Of String), ByRef errorMsg As String) As Boolean
        domains = New List(Of String)
        errorMsg = ""
        If presetArg Is Nothing OrElse presetArg.Trim() = "" Then Return True
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim unknown As New List(Of String)
        For Each rawTok As String In presetArg.Split(New Char() {","c, ";"c})
            Dim tok As String = rawTok.Trim()
            If tok = "" Then Continue For
            Dim entries() As String = Nothing
            If Not PresetTable.TryGetValue(tok, entries) Then
                unknown.Add(tok)
                Continue For
            End If
            For Each d As String In entries
                If seen.Add(d) Then domains.Add(d)
            Next
        Next
        If unknown.Count > 0 Then
            domains = New List(Of String)   ' fail-closed: emit NOTHING when any category is unknown
            errorMsg = If(unknown.Count > 1, "Unknown presets: ", "Unknown preset: ") &
                       String.Join(", ", unknown) &
                       ". Available presets: " & String.Join(", ", KnownPresetNames()) & "."
            Return False
        End If
        Return True
    End Function

    ' ---- D2a: APP presets (named category -> executable names, INPUT sugar only) ----
    '
    ' The app analogue of the D1a site PresetTable: a FIXED, compile-time bundle of well-known
    ' distraction executables the user opts into with `block --app-preset games,chat`. The expanded
    ' .exe names flow into the SAME apps list --apps feeds -> [Process] List, enforced + MAC-covered
    ' downstream by the enforcement canonical (B7) exactly like a hand-typed --apps name, with NO new
    ' canonical surface and NO schema bump (a code constant, not stored config - nothing extra to
    ' protect). The categories are FIXED (an EDITABLE user *default* app list is the separate D2b
    ' slice, stored MAC-covered on the setup ini, mirroring D1b). Entries are the bare process-image
    ' names BlockedApps/the notifier compare on (lowercase, .exe); the exact bundle membership is
    ' refinable product content, NOT a correctness surface (an absent process name simply never
    ' matches - over-listing is harmless, it just tries to kill a name that isn't running).
    Private ReadOnly AppPresetTable As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase) From {
        {"games", New String() {"steam.exe", "epicgameslauncher.exe", "battle.net.exe", "riotclientservices.exe", "leagueclient.exe", "valorant.exe", "robloxplayerbeta.exe"}},
        {"chat", New String() {"discord.exe", "telegram.exe", "whatsapp.exe", "signal.exe", "slack.exe"}}
    }

    ' The sorted known app-preset category names (usage/help + the unknown-category error hint). A
    ' read-only snapshot; the table is never mutated. Public so help lists the live categories rather
    ' than a hand-maintained copy that could drift.
    Public Function KnownAppPresetNames() As String()
        Dim names As New List(Of String)(AppPresetTable.Keys)
        names.Sort(StringComparer.OrdinalIgnoreCase)
        Return names.ToArray()
    End Function

    ' Expand a comma/semicolon app-preset argument ("games,chat") into the union of those categories'
    ' executables, deduped case-insensitively with order preserved (category order, then entry order).
    ' FAIL-CLOSED on an unknown category exactly like TryExpandPresets: collect EVERY unknown token,
    ' return False with a friendly error naming them + the valid names, and emit NOTHING - a typo must
    ' never silently UNDER-kill. An empty/whitespace/Nothing arg returns True with an empty list (the
    ' caller's --apps still applies). Friend so the contract is unit-tested without arming a block.
    Friend Function TryExpandAppPresets(ByVal appPresetArg As String, ByRef apps As List(Of String), ByRef errorMsg As String) As Boolean
        apps = New List(Of String)
        errorMsg = ""
        If appPresetArg Is Nothing OrElse appPresetArg.Trim() = "" Then Return True
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim unknown As New List(Of String)
        For Each rawTok As String In appPresetArg.Split(New Char() {","c, ";"c})
            Dim tok As String = rawTok.Trim()
            If tok = "" Then Continue For
            Dim entries() As String = Nothing
            If Not AppPresetTable.TryGetValue(tok, entries) Then
                unknown.Add(tok)
                Continue For
            End If
            For Each a As String In entries
                If seen.Add(a) Then apps.Add(a)
            Next
        Next
        If unknown.Count > 0 Then
            apps = New List(Of String)   ' fail-closed: emit NOTHING when any category is unknown
            errorMsg = If(unknown.Count > 1, "Unknown app presets: ", "Unknown app preset: ") &
                       String.Join(", ", unknown) &
                       ". Available app presets: " & String.Join(", ", KnownAppPresetNames()) & "."
            Return False
        End If
        Return True
    End Function

    ' ---- D1b: build the account-default blocklist string to persist on the setup file ----
    '
    ' Merges `setup --default-sites a.com,b.com` raw domains with any `setup --default-preset
    ' social,video` categories (expanded via the SAME D1a TryExpandPresets), into the comma-joined
    ' packed string SetupDefaultSites reads back. Union, deduped case-insensitively, order preserved
    ' (--default-sites first, then the preset domains). FAIL-CLOSED on an unknown preset: it returns
    ' False + TryExpandPresets' error and packs NOTHING, so the setup verb aborts BEFORE the write
    ' (fail-fast, no partial state) - the preset is validated ONCE here at setup time rather than at
    ' every future block, so a stored default can never make a later `block` fail to arm. An empty/
    ' Nothing arg on either side contributes nothing; both empty => packed = "" (no default). Friend
    ' so the merge is unit-tested directly without running the console setup verb.
    Friend Function TryBuildDefaultSites(ByVal sitesArg As String, ByVal presetArg As String, ByRef packed As String, ByRef errorMsg As String) As Boolean
        packed = ""
        errorMsg = ""
        Dim domains As New List(Of String)
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If sitesArg IsNot Nothing Then
            For Each rawTok As String In sitesArg.Split(New Char() {","c, ";"c})
                Dim tok As String = rawTok.Trim()
                If tok <> "" AndAlso seen.Add(tok) Then domains.Add(tok)
            Next
        End If
        Dim presetDomains As New List(Of String)
        If Not TryExpandPresets(presetArg, presetDomains, errorMsg) Then Return False   ' fail-closed: unknown preset
        For Each d As String In presetDomains
            If seen.Add(d) Then domains.Add(d)
        Next
        packed = String.Join(",", domains)
        Return True
    End Function

    ' ---- D2b: build the account-default app-kill list string to persist on the setup file ----
    '
    ' The app analogue of TryBuildDefaultSites: merges `setup --default-apps a.exe,b.exe` raw names
    ' with any `setup --default-app-preset games,chat` categories (expanded via the SAME D2a
    ' TryExpandAppPresets), into the comma-joined packed string SetupDefaultApps reads back. Union,
    ' deduped case-insensitively, order preserved (--default-apps first, then the preset apps). FAIL-
    ' CLOSED on an unknown app-preset: returns False + TryExpandAppPresets' error and packs NOTHING, so
    ' the setup verb aborts BEFORE the write (fail-fast, no partial state) - the preset is validated
    ' ONCE here at setup time rather than at every future block. An empty/Nothing arg on either side
    ' contributes nothing; both empty => packed = "" (no default). .exe-normalisation happens downstream
    ' in PackApps at arm time (as for a hand-typed --apps name), exactly as TryBuildDefaultSites leaves
    ' domain normalisation to WriteHostsBlock. Friend so the merge is unit-tested directly.
    Friend Function TryBuildDefaultApps(ByVal appsArg As String, ByVal appPresetArg As String, ByRef packed As String, ByRef errorMsg As String) As Boolean
        packed = ""
        errorMsg = ""
        Dim apps As New List(Of String)
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If appsArg IsNot Nothing Then
            For Each rawTok As String In appsArg.Split(New Char() {","c, ";"c})
                Dim tok As String = rawTok.Trim()
                If tok <> "" AndAlso seen.Add(tok) Then apps.Add(tok)
            Next
        End If
        Dim presetApps As New List(Of String)
        If Not TryExpandAppPresets(appPresetArg, presetApps, errorMsg) Then Return False   ' fail-closed: unknown app preset
        For Each a As String In presetApps
            If seen.Add(a) Then apps.Add(a)
        Next
        packed = String.Join(",", apps)
        Return True
    End Function

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
    Public Function WriteConfig(ByVal domains As IEnumerable(Of String), ByVal apps As IEnumerable(Of String), ByVal untilDate As DateTime, Optional ByVal committed As Boolean = False, Optional ByVal coolOffSeconds As Long = 0) As String
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

        ' C6b: the configurable cooling-off duration (seconds), MAC-covered from birth
        ' (set BEFORE StampFreshMac, like the [Partner]/[Commit] fields). Written ONLY
        ' when the user gave --cooloff a positive value; absent = "use the compile-time
        ' floor". The service clamps max(this, floor) so a value below the floor still
        ' waits the floor - the field can only EXTEND cooling-off, never shorten it. Stored
        ' PLAINTEXT (a duration is not a secret); the MAC protects it (a raw edit to shorten
        ' it fails verification -> the readers fail closed -> freeze). A plain integer
        ' ToString() is culture-invariant (no group separators), matching the service parse.
        If coolOffSeconds > 0 Then
            ini.AddSection("CoolOff")
            ini.SetKeyValue("CoolOff", "Duration", coolOffSeconds.ToString())
        End If

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
        ' C6b: the [CoolOff] Duration configured cooling-off wait in seconds, stored
        ' PLAINTEXT (as-stored, like Committed - NOT decrypted); absent => "" (a v7 config
        ' read under v8 code builds a different canonical and freezes, R9). MAC-covered.
        Dim coolOffDuration As String = ini.GetKeyValue("CoolOff", "Duration")

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

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion, untilPlain, procPlain, sites, nowPlain, highWaterPlain, coolOffPlain, partnerSalt, partnerHash, partnerUnlockedAt, committed, scheduleSpec, scheduleActivePlain, coolOffDuration)
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

    ' ---- C5b (c3): the `schedule` front-end (arm/clear a schedule-only block) ----
    '
    ' The CLI half of C5c: validate the human --windows/--sites/--apps args, serialise them to the
    ' compact v1 [Schedule] Spec grammar the service parser (Service1.ParseSchedule) accepts, and
    ' arm a MAC-covered schedule-only block. The arm MIRRORS AppendAddToHosts (edit-in-place +
    ' RestampMacWithExistingKey) / WriteConfig (fresh scaffold), NOT DoBlock's hosts write - the
    ' CLI does NOT write the hosts snapshot (the service creates monkmode_hosts.block on window-
    ' open, c1). A schedule-only block has no manual duration, so [Time] Until = the past sentinel.

    ' Map a human day token to 1..7 (Mon=1 .. Sun=7), matching the compact grammar's dayMask chars
    ' ('1'..'7'). Case-insensitive; the WHOLE trimmed token must be a recognised day name/abbrev.
    ' 0 = unrecognised. Matching the FULL token (not a 3-letter prefix) is deliberately fail-closed:
    ' a garbage word ("monkey") or a space-separated day list ("Mon Tue Wed", which the comma-split
    ' would hand over as one token) is REJECTED with a friendly error, never silently truncated to
    ' its first weekday - under-blocking intent must never pass silently in a self-control tool.
    Private Function DayNameToNumber(ByVal token As String) As Integer
        Select Case token.Trim().ToLowerInvariant()
            Case "mon", "monday" : Return 1
            Case "tue", "tues", "tuesday" : Return 2
            Case "wed", "weds", "wednesday" : Return 3
            Case "thu", "thur", "thurs", "thursday" : Return 4
            Case "fri", "friday" : Return 5
            Case "sat", "saturday" : Return 6
            Case "sun", "sunday" : Return 7
            Case Else : Return 0
        End Select
    End Function

    ' Parse a human day set ("Mon-Fri", "Sat,Sun", "Mon,Wed,Fri", "Tue") into the compact dayMask
    ' chars (sorted, deduped, e.g. "12345"). Ranges A-B are inclusive and reject a reversed range
    ' (start > end); a single unrecognised token fails. Fail-closed: any error returns False with a
    ' friendly message and NO mask (never emit a partial/garbage mask).
    Private Function TryParseDaySet(ByVal daysText As String, ByRef maskChars As String, ByRef errorMsg As String) As Boolean
        maskChars = ""
        Dim present(7) As Boolean   ' index 1..7 (0 unused)
        Dim anyDay As Boolean = False
        For Each rawTok As String In daysText.Split(","c)
            Dim tok As String = rawTok.Trim()
            If tok = "" Then Continue For
            Dim dashParts() As String = tok.Split("-"c)
            If dashParts.Length = 1 Then
                Dim n As Integer = DayNameToNumber(tok)
                If n = 0 Then
                    errorMsg = "Could not understand the day '" & tok & "'. Use Mon, Tue, Wed, Thu, Fri, Sat, Sun (or a range like Mon-Fri)."
                    Return False
                End If
                present(n) = True : anyDay = True
            ElseIf dashParts.Length = 2 Then
                Dim a As Integer = DayNameToNumber(dashParts(0))
                Dim b As Integer = DayNameToNumber(dashParts(1))
                If a = 0 OrElse b = 0 Then
                    errorMsg = "Could not understand the day range '" & tok & "'. Use e.g. Mon-Fri."
                    Return False
                End If
                If a > b Then
                    errorMsg = "The day range '" & tok & "' runs backwards. Use e.g. Mon-Fri, or list the days: Fri,Sat,Sun,Mon."
                    Return False
                End If
                For d As Integer = a To b
                    present(d) = True
                Next
                anyDay = True
            Else
                errorMsg = "Could not understand the days '" & tok & "'. Use e.g. Mon-Fri or Sat,Sun."
                Return False
            End If
        Next
        If Not anyDay Then
            errorMsg = "A window needs at least one day (e.g. Mon-Fri 09:00-17:00)."
            Return False
        End If
        Dim sb As New System.Text.StringBuilder
        For d As Integer = 1 To 7
            If present(d) Then sb.Append(d.ToString(CultureInfo.InvariantCulture))
        Next
        maskChars = sb.ToString()
        Return True
    End Function

    ' Parse a human "H:MM"/"HH:MM" time to minutes-of-day (0..1439). -1 = invalid (HH not 1-2
    ' digits / MM not 2 digits / out of range HH 0-23, MM 0-59).
    Private Function ParseHhmmToken(ByVal s As String) As Integer
        Dim parts() As String = s.Trim().Split(":"c)
        If parts.Length <> 2 Then Return -1
        Dim hStr As String = parts(0).Trim(), mStr As String = parts(1).Trim()
        If hStr.Length < 1 OrElse hStr.Length > 2 OrElse mStr.Length <> 2 Then Return -1
        Dim hh As Integer, mm As Integer
        If Not Integer.TryParse(hStr, NumberStyles.None, CultureInfo.InvariantCulture, hh) Then Return -1
        If Not Integer.TryParse(mStr, NumberStyles.None, CultureInfo.InvariantCulture, mm) Then Return -1
        If hh > 23 OrElse mm > 59 Then Return -1
        Return hh * 60 + mm
    End Function

    ' Minutes-of-day (0..1439) -> compact "HHMM" (4-digit, zero-padded), the grammar's time form.
    Private Function MinutesToHhmm(ByVal minutes As Integer) As String
        Return (minutes \ 60).ToString("00", CultureInfo.InvariantCulture) & (minutes Mod 60).ToString("00", CultureInfo.InvariantCulture)
    End Function

    ' Parse one human window clause ("Mon-Fri 09:00-17:00", "Sat, Sun 10:00-14:00") into the compact
    ' "dayMask:HHMM-HHMM" grammar token. Rejects overnight/zero-length (open >= close, SD3) and bad
    ' days/times. Fail-closed: any error returns False + a friendly message.
    Private Function TryParseWindowClause(ByVal clause As String, ByRef compactWindow As String, ByRef errorMsg As String) As Boolean
        compactWindow = ""
        Dim m As System.Text.RegularExpressions.Match =
            System.Text.RegularExpressions.Regex.Match(clause.Trim(),
                "^(?<days>[A-Za-z][A-Za-z,\s\-]*?)\s+(?<open>\d{1,2}:\d{2})\s*-\s*(?<close>\d{1,2}:\d{2})$")
        If Not m.Success Then
            errorMsg = "Could not understand the window '" & clause.Trim() & "'. Use e.g. ""Mon-Fri 09:00-17:00""."
            Return False
        End If
        Dim maskChars As String = ""
        If Not TryParseDaySet(m.Groups("days").Value, maskChars, errorMsg) Then Return False
        Dim openMin As Integer = ParseHhmmToken(m.Groups("open").Value)
        Dim closeMin As Integer = ParseHhmmToken(m.Groups("close").Value)
        If openMin < 0 OrElse closeMin < 0 Then
            errorMsg = "Could not understand the time in '" & clause.Trim() & "'. Use 24-hour HH:MM, e.g. 09:00-17:00."
            Return False
        End If
        If openMin >= closeMin Then
            errorMsg = "The window '" & clause.Trim() & "' must end after it starts (overnight windows aren't supported in this version - split it into two same-day windows)."
            Return False
        End If
        compactWindow = maskChars & ":" & MinutesToHhmm(openMin) & "-" & MinutesToHhmm(closeMin)
        Return True
    End Function

    ' Validate + serialise the human schedule args to the compact v1 [Schedule] Spec grammar
    ' Service1.ParseSchedule round-trips. windowsArg = ";"-separated human window clauses
    ' ("Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00"); sites/apps = the block lists. Every field is
    ' validated - a malformed window/day/time, an empty window list, or an empty site list fails
    ' with a friendly message and stamps NOTHING (the "never stamp a garbage Spec" fence, verifier
    ' P3#1: a validated result always parses back to >=1 window). Sites are normalised like the
    ' hosts entries (NormalizeDomain); apps get a .exe suffix; "|" separates entries. Friend so the
    ' CLI<->service round-trip test can call it.
    Friend Function TryBuildScheduleSpec(ByVal windowsArg As String, ByVal sites As IEnumerable(Of String), ByVal apps As IEnumerable(Of String), ByRef spec As String, ByRef errorMsg As String) As Boolean
        spec = "" : errorMsg = ""
        If windowsArg Is Nothing OrElse windowsArg.Trim() = "" Then
            errorMsg = "Provide at least one window with --windows ""Mon-Fri 09:00-17:00""."
            Return False
        End If
        Dim compactWindows As New List(Of String)
        For Each clause As String In windowsArg.Split(";"c)
            If clause.Trim() = "" Then Continue For
            Dim cw As String = ""
            If Not TryParseWindowClause(clause, cw, errorMsg) Then Return False
            compactWindows.Add(cw)
        Next
        If compactWindows.Count = 0 Then
            errorMsg = "Provide at least one window with --windows ""Mon-Fri 09:00-17:00""."
            Return False
        End If
        ' Sites (>=1; normalised like the hosts entries). "|"/";" can't appear in a domain.
        Dim siteTokens As New List(Of String)
        For Each raw As String In sites
            Dim d As String = NormalizeDomain(raw)
            If d = "" Then Continue For
            If d.Contains("|") OrElse d.Contains(";") Then
                errorMsg = "The site '" & raw & "' contains an unsupported character."
                Return False
            End If
            siteTokens.Add(d)
        Next
        If siteTokens.Count = 0 Then
            errorMsg = "Provide at least one site to block with --sites a.com,b.com."
            Return False
        End If
        ' Apps (optional; .exe-suffixed like the manual block's [Process] List).
        Dim appTokens As New List(Of String)
        For Each raw As String In apps
            Dim a As String = raw.Trim()
            If a = "" Then Continue For
            If Not a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then a &= ".exe"
            If a.Contains("|") OrElse a.Contains(";") Then
                errorMsg = "The app '" & raw & "' contains an unsupported character."
                Return False
            End If
            appTokens.Add(a)
        Next
        spec = ScheduleSpecGrammarVersion & ";" & String.Join(",", compactWindows) &
               ";sites=" & String.Join("|", siteTokens) &
               ";apps=" & String.Join("|", appTokens)
        Return True
    End Function

    ' C5b (c3): is a schedule currently armed? MAC-valid AND a non-empty [Schedule] Spec (the cheap
    ' over-approximation the guardian uses, NOT a 4th ParseSchedule copy - the CLI only ever writes a
    ' validated Spec or "", so non-empty <=> has windows here). Used for the SD-c1 mutual exclusion
    ' (`block`/`add` refuse while a schedule is armed) and to pick the arm path (edit-in-place vs
    ' fresh scaffold) in WriteScheduleConfig. Fail-safe: any read failure / no config / invalid MAC
    ' reads as NOT armed - a tampered running block is still caught by the manual path's own fail-
    ' closed BlockIsActive (service running + bad MAC => active => refused). Never throws.
    Public Function ScheduleIsArmed() As Boolean
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            If Not ConfigMacIsValidForIni(ini) Then Return False
            Return Not String.IsNullOrWhiteSpace(ini.GetKeyValue("Schedule", "Spec"))
        Catch
            Return False
        End Try
    End Function

    ' C5b (c3): arm / re-arm / clear a schedule-only block. `spec` = a validated compact Spec (>=1
    ' window) to arm, or "" to clear. Mirrors WriteConfig's fresh scaffold when nothing is armed yet,
    ' and AppendAddToHosts's edit-in-place (RestampMacWithExistingKey, preserve every other field)
    ' when re-arming/clearing an armed one - so a re-arm/clear can NEVER reset the monotonic frame
    ' ([Time] HighWater) or shorten a currently-open window ([Schedule] ActiveUntil is left
    ' untouched; C5a §7). The CLI never writes the hosts snapshot (the service creates
    ' monkmode_hosts.block on window-open, c1). Public so the CLI-writer end-to-end test drives it.
    Public Sub WriteScheduleConfig(ByVal spec As String)
        If ScheduleIsArmed() Then
            ' Edit an existing armed schedule-only config in place: change only the Spec, keep the
            ' monotonic frame + any open window. Re-stamp with the EXISTING key, and ONLY if the MAC
            ' was valid before the edit (never re-bless a tampered/frozen config - the B7 `add` fix).
            Try
                Dim ini As New IniFile
                ini.Load(IniPath())
                Dim macValid As Boolean = ConfigMacIsValidForIni(ini)
                ini.SetKeyValue("Schedule", "Spec", spec)
                ' Re-affirm the schedule-only sentinel (idempotent - an armed schedule-only config
                ' already carries it; keeps the invariant explicit).
                ini.SetKeyValue("Time", "Until", enc.EncryptData(ScheduleOnlyExpiredUntil))
                If macValid Then RestampMacWithExistingKey(ini)
                ini.Save(IniPath())
                RefreshBackup(ini)
            Catch
            End Try
        Else
            ' Fresh schedule-only scaffold (WriteConfig's field set, minus the manual duration +
            ' partner code): the past-Until sentinel, a seeded HighWater/Now, empty manual site/app
            ' lists (the schedule's own live in the Spec), and the Spec. StampFreshMac mints the key
            ' + MAC so the block is armed (macValid=True) from birth.
            Dim ini As New IniFile
            ini.AddSection("Process")
            ini.SetKeyValue("Process", "List", "null")            ' no manual apps (schedule apps live in the Spec)

            ini.AddSection("User")
            ini.SetKeyValue("User", "CustomChecked", "")
            ini.SetKeyValue("User", "CustomSites", "null")        ' no manual sites (schedule sites live in the Spec)
            ini.SetKeyValue("User", "Done", "no")
            ini.SetKeyValue("User", "NeedsAlerted", "yes")

            ini.AddSection("Time")
            ini.SetKeyValue("Time", "Until", enc.EncryptData(ScheduleOnlyExpiredUntil))
            ini.SetKeyValue("Time", "TimeChanging", "no")
            ini.SetKeyValue("Time", "HighWater", enc.EncryptData(DateTime.Now.ToString(CA)))

            ini.AddSection("CurrentTime")
            ini.SetKeyValue("CurrentTime", "Now", enc.EncryptData(DateTime.Now.ToString(CA)))

            ' C4: NOT a committed MANUAL block (SD5: no [Schedule] Committed field). "no" is MAC-covered.
            ini.AddSection("Commit")
            ini.SetKeyValue("Commit", "Committed", "no")

            ' C5b: the schedule rule (plaintext, MAC-covered). ActiveUntil is OMITTED (absent = "" =
            ' no window open); the SERVICE is its sole writer (sets it on window-open, c1/c2).
            ini.AddSection("Schedule")
            ini.SetKeyValue("Schedule", "Spec", spec)

            StampFreshMac(ini)
            ini.Save(IniPath())
            RefreshBackup(ini)
        End If
    End Sub

    ' ---- C5b (c4): read-only schedule DISPLAY helpers (schedule --show / status) ----
    '
    ' Render the stored compact v1 [Schedule] Spec back into a human form for DISPLAY only - a cosmetic
    ' reverse of TryBuildScheduleSpec's serialiser, deliberately NOT a 4th Service1.ParseSchedule parity
    ' copy (the live window-open / monotonic-remaining state folds into the richer `status`, D5; c4 shows
    ' only the static rule). Fail-SOFT throughout: a display path must never throw or block a status read,
    ' so an unrecognised token renders VERBATIM rather than erroring. In practice the CLI is the sole Spec
    ' writer and always emits the canonical compact form, and ScheduleIsArmed gates these on a valid MAC
    ' (a hand-tampered Spec reads as not-armed - frozen by the service, never shown) - so the fail-soft
    ' branches only guard against defensive surprises, never normal input.

    ' The raw stored compact [Schedule] Spec (for --show / status). "" on any read failure / no config /
    ' absent Spec. Read-only; never throws. Callers pair it with ScheduleIsArmed (which also checks the
    ' MAC), reading this only once the schedule is known armed.
    Public Function ArmedScheduleSpec() As String
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            Return If(ini.GetKeyValue("Schedule", "Spec"), "")
        Catch
            Return ""
        End Try
    End Function

    ' D5 (rich status): is a scheduled window OPEN right now? Reads the service-maintained
    ' [Schedule] ActiveUntil (encrypted datetime; "" between windows - the service is its sole
    ' writer, set on window-open) vs the monotonic [Time] HighWater, macValid-gated. Display-only:
    ' ZERO enforcement authority (like BlockIsCommitted). Fail-CLOSED to match the service (an
    ' unreadable/tampered config or a set-but-unparseable deadline reads as NOT elapsed = held/open),
    ' so `status` never claims "no window" while the service is in fact still holding one. False on
    ' no config / absent ActiveUntil / any read failure.
    Public Function ScheduleWindowIsOpen() As Boolean
        Try
            Dim ini As New IniFile
            ini.Load(IniPath())
            If Not ConfigMacIsValidForIni(ini) Then Return False
            Dim activeEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")
            If activeEnc = "" Then Return False
            Return Not ScheduleWindowElapsed(enc.DecryptData(activeEnc), enc.DecryptData(ini.GetKeyValue("Time", "HighWater")))
        Catch
            Return False
        End Try
    End Function

    ' Pure: has the open scheduled window reached its monotonic close (ActiveUntil <= HighWater)?
    ' Byte-for-byte Service1.ScheduleElapsed / Form1.ScheduleElapsed (parity-pinned): "" or any
    ' unparseable input reads as NOT elapsed (fail-closed hold), so this CLI copy agrees with the
    ' service that decides the actual enforcement. Friend so the parity is unit-tested.
    Friend Function ScheduleWindowElapsed(ByVal activeUntilText As String, ByVal highWaterText As String) As Boolean
        If activeUntilText = "" Then Return False
        Dim activeUntil As DateTime, highWater As DateTime
        If Not DateTime.TryParse(activeUntilText, CA, DateTimeStyles.None, activeUntil) Then Return False
        If Not DateTime.TryParse(highWaterText, CA, DateTimeStyles.None, highWater) Then Return False
        Return activeUntil <= highWater
    End Function

    ' Compact dayMask chars ('1'..'7' = Mon..Sun) -> a human day phrase, compressing a run of >=3
    ' CONSECUTIVE days into a range (e.g. "12345" -> "Mon-Fri", "67" -> "Sat,Sun", "1235" ->
    ' "Mon-Wed,Fri"). Sorted + deduped; unknown chars are skipped. A wholly unrecognised mask renders
    ' verbatim (fail-soft). Friend so the display round-trip is unit-tested.
    Friend Function DayMaskToHuman(ByVal mask As String) As String
        Dim names() As String = {"", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"}
        Dim present(7) As Boolean
        For Each ch As Char In If(mask, "")
            Dim code As Integer = AscW(ch)
            If code >= AscW("1"c) AndAlso code <= AscW("7"c) Then present(code - AscW("0"c)) = True
        Next
        Dim days As New List(Of Integer)
        For d As Integer = 1 To 7
            If present(d) Then days.Add(d)
        Next
        If days.Count = 0 Then Return If(mask, "")   ' fail-soft: nothing recognised
        Dim parts As New List(Of String)
        Dim i As Integer = 0
        While i < days.Count
            Dim j As Integer = i
            While j + 1 < days.Count AndAlso days(j + 1) = days(j) + 1
                j += 1
            End While
            If j - i + 1 >= 3 Then
                parts.Add(names(days(i)) & "-" & names(days(j)))   ' a run of >=3 -> a range
            Else
                For k As Integer = i To j
                    parts.Add(names(days(k)))                       ' a run of 1-2 -> list each day
                Next
            End If
            i = j + 1
        End While
        Return String.Join(",", parts)
    End Function

    ' Compact "HHMM" (4-digit) -> "HH:MM". Fail-soft: a non-4-digit / non-numeric token renders verbatim.
    Private Function HhmmToHuman(ByVal hhmm As String) As String
        Dim t As String = If(hhmm, "").Trim()
        If t.Length <> 4 Then Return If(hhmm, "")
        For Each ch As Char In t
            If Not Char.IsDigit(ch) Then Return hhmm
        Next
        Return t.Substring(0, 2) & ":" & t.Substring(2, 2)
    End Function

    ' One compact window ("12345:0900-1700") -> "Mon-Fri 09:00-17:00". Fail-soft: an unparseable token
    ' (no colon / no time dash) renders trimmed-verbatim (never throws). Friend for the round-trip test.
    Friend Function CompactWindowToHuman(ByVal compactWindow As String) As String
        Dim w As String = If(compactWindow, "").Trim()
        Dim colon As Integer = w.IndexOf(":"c)
        If colon <= 0 Then Return w
        Dim times As String = w.Substring(colon + 1)
        Dim dash As Integer = times.IndexOf("-"c)
        If dash <= 0 Then Return w
        Return DayMaskToHuman(w.Substring(0, colon)) & " " &
               HhmmToHuman(times.Substring(0, dash)) & "-" & HhmmToHuman(times.Substring(dash + 1))
    End Function

    ' Split a stored compact Spec ("v1;<windows>;sites=a|b;apps=x|y") into its display parts: the human
    ' window phrases, the site list, and the app list. Fail-soft: absent/garbled parts yield empty lists
    ' (the three ByRef outs are ALWAYS initialised, never null; never throws). Reads NOTHING from disk -
    ' the caller passes the Spec it already read, keeping the display pure + trivially testable. Friend
    ' so --show/status render off the same tested split.
    Friend Sub DescribeScheduleSpec(ByVal spec As String, ByRef windows As List(Of String), ByRef sites As List(Of String), ByRef apps As List(Of String))
        windows = New List(Of String)
        sites = New List(Of String)
        apps = New List(Of String)
        If spec Is Nothing Then Return
        Dim parts() As String = spec.Split(";"c)
        For i As Integer = 0 To parts.Length - 1
            Dim p As String = parts(i)
            If i = 0 Then
                Continue For   ' the grammar-version tag (e.g. "v1") - not shown
            ElseIf p.StartsWith("sites=", StringComparison.OrdinalIgnoreCase) Then
                For Each s As String In p.Substring(6).Split("|"c)
                    If s.Trim() <> "" Then sites.Add(s.Trim())
                Next
            ElseIf p.StartsWith("apps=", StringComparison.OrdinalIgnoreCase) Then
                For Each a As String In p.Substring(5).Split("|"c)
                    If a.Trim() <> "" Then apps.Add(a.Trim())
                Next
            ElseIf p.Trim() <> "" Then
                For Each cw As String In p.Split(","c)   ' the windows CSV (the one part between v-tag and sites=)
                    If cw.Trim() <> "" Then windows.Add(CompactWindowToHuman(cw))
                Next
            End If
        Next
    End Sub

    ' ---- C6a: first-run setup / onboarding preferences (CLI-only, MAC-covered) ----
    '
    ' `monkmode setup` records that first-run onboarding has happened and stores the
    ' user's account-level preferences in a SEPARATE file (monkmode_setup.ini) next to
    ' the exes - deliberately NOT the enforcement config (monkmode_settings.ini), which
    ' every `block`/`schedule` arm OVERWRITES and whose canonical is the 4-copy parity-
    ' pinned enforcement MAC. Keeping onboarding state out of that file means C6a adds
    ' ZERO enforcement-canonical surface (no v7->v8 lockstep) and can never perturb a
    ' live block. The setup file is CLI-only (the service never reads it), so its MAC is
    ' a private CLI canonical with no cross-assembly parity requirement.
    '
    ' The gate is a USABILITY guardrail, not a security control: `block`/`schedule`
    ' refuse to arm until setup has run, so a first block always goes through the
    ' accountability-model explanation (each block mints a one-time partner code you must
    ' relay; cooling-off is a mandatory wait) and can't be armed by a user who never saw
    ' how to get out. Forging "setup done" grants nothing an attacker wants (arming a
    ' block is the tool's PURPOSE, not a bypass; removing a block is unaffected). It is
    ' still MAC-covered - for tamper-evidence, house-pattern consistency, and as the
    ' trusted home for the load-bearing preferences C6b (the configurable cooling-off
    ' duration) and D1 (default blocklist/presets) will add.
    '
    ' Fail-closed like the rest of the tool: any read failure / missing file / invalid
    ' MAC / Done<>"yes" reads as NOT set up -> the arm-gate refuses -> the user just
    ' re-runs `setup`. A DPAPI failure that stops the stamp is surfaced by WriteSetupConfig
    ' returning False (the verb warns), never a silent trap; and a machine whose DPAPI is
    ' dead already can't run MonkMode safely (the B7 enforcement MAC is dead too), so
    ' refusing a NEW arm there is correct, not a regression.

    Public Const SetupIniName As String = "monkmode_setup.ini"
    ' The setup file's own canonical version tag (independent of the enforcement
    ' ConfigIntegrity.CurrentSchemaVersion). Bumping it invalidates older setup files so a
    ' schema change forces a one-off `setup` re-run (fail-closed), mirroring the "arm
    ' blocks after upgrading" operational rule. s1 = C6a: Done + Partner. s2 = C6c: adds
    ' the account-default cooling-off duration (CoolOffSeconds). s3 = D1b: adds the account-
    ' default blocklist (DefaultSites). s4 = D2b: adds the account-default app list (DefaultApps)
    ' - an older file's byte-exact MAC can't validate the newer canonical (new version tag + an
    ' appended field), so upgrading forces one `setup` re-run.
    Public Const SetupSchemaVersion As String = "s4"
    Public Const SetupSection As String = "Setup"
    Public Const SetupDoneKey As String = "Done"
    Public Const SetupPartnerKey As String = "Partner"
    ' C6c: the account-default cooling-off wait (seconds) every later `block` inherits when
    ' it gives no --cooloff of its own. Stored PLAINTEXT + MAC-covered on the SETUP file
    ' (like Partner), written only when > 0 (absent = "" = no default = the service floor).
    Public Const SetupCoolOffKey As String = "CoolOffSeconds"
    ' C6c: the shared sanity cap on any cooling-off duration (~365 days). Cooling-off is a short
    ' friction wait before the self-serve exit, not a second timer; a value beyond this is refused
    ' up front by the CLI parse (Program.TryParseCoolOffArg for --cooloff / setup --cooloff) AND
    ' re-clamped fail-safe on READ (SetupDefaultCoolOffSeconds), so an over-cap value can never
    ' reach the service's per-tick HighWater.AddSeconds (which would otherwise overflow).
    Public Const MaxCoolOffSeconds As Long = 365L * 24L * 60L * 60L
    ' D1b: the account-DEFAULT blocklist every later `block` inherits when it names NO explicit site
    ' source (--sites/--preset/--file). Stored PLAINTEXT + MAC-covered on the SETUP file (like Partner/
    ' CoolOffSeconds), written only when non-empty (absent = "" = no default). A comma-joined list of
    ' bare domains (the same raw tokens `--sites` takes, merged + preset-expanded at setup time by
    ' TryBuildDefaultSites); on inherit they flow into the SAME `domains` list -> WriteHostsBlock, so
    ' they are normalised + enforcement-MAC-covered identically to a hand-typed --sites domain. INPUT
    ' sugar only: it can only ever ADD sites to a NEW arm, never lift/shorten a live block, so a
    ' fail-closed (empty) read on any tamper/incomplete setup is safe (see SetupDefaultSites).
    Public Const SetupDefaultSitesKey As String = "DefaultSites"
    ' D2b: the account-DEFAULT app-kill list every later `block` inherits when it names NO explicit
    ' app source (--apps/--app-preset). Stored PLAINTEXT + MAC-covered on the SETUP file (like
    ' DefaultSites), written only when non-empty (absent = "" = no default). A comma-joined list of
    ' bare .exe process-image names (the same raw tokens `--apps` takes, merged + app-preset-expanded
    ' at setup time by TryBuildDefaultApps); on inherit they flow into the SAME `apps` list -> PackApps
    ' -> [Process] List, so they are .exe-normalised + enforcement-MAC-covered identically to a hand-
    ' typed --apps name. INPUT sugar only: it can only ever ADD apps to a NEW arm, never lift/shorten a
    ' live block, so a fail-closed (empty) read on any tamper/incomplete setup is safe (see SetupDefaultApps).
    Public Const SetupDefaultAppsKey As String = "DefaultApps"

    Public Function SetupIniPath() As String
        Return Path.Combine(AppDir(), SetupIniName)
    End Function

    ' The MAC canonical over the setup file's fields (version-tagged, [Integrity]
    ' excluded). A PRIVATE CLI canonical - the service never reads this file, so unlike
    ' CanonicalFromIni it needs no 4-copy parity. Absent fields pass through as "" (so the
    ' empty-partner case, which reloads as a bare-key Nothing, canonicalises identically
    ' at stamp-time and verify-time - the shipped CoolOffUntil="" round-trip pattern).
    ' Friend so a test can re-stamp a hand-edited setup file (e.g. Done="no" under a VALID
    ' MAC) to prove SetupIsComplete gates on the Done flag, not merely on the MAC.
    Friend Function SetupCanonicalFromIni(ByVal ini As IniFile) As String
        Dim done As String = If(ini.GetKeyValue(SetupSection, SetupDoneKey), "")
        Dim partner As String = If(ini.GetKeyValue(SetupSection, SetupPartnerKey), "")
        ' C6c CoolOffSeconds, then D1b DefaultSites, then D2b DefaultApps, are each APPENDED LAST in
        ' turn (the append-at-end rule every schema bump follows), so an older stamp can't validate a
        ' newer canonical (new version tag + an extra field line) -> the upgrade freeze. Each absent
        ' field => "" (written only when set), round-tripping identically at stamp + verify, like the
        ' empty-Partner case.
        Dim coolOff As String = If(ini.GetKeyValue(SetupSection, SetupCoolOffKey), "")
        Dim defaultSites As String = If(ini.GetKeyValue(SetupSection, SetupDefaultSitesKey), "")
        Dim defaultApps As String = If(ini.GetKeyValue(SetupSection, SetupDefaultAppsKey), "")
        Return SetupSchemaVersion & vbLf &
               "Done=" & done & vbLf &
               "Partner=" & partner & vbLf &
               "CoolOffSeconds=" & coolOff & vbLf &
               "DefaultSites=" & defaultSites & vbLf &
               "DefaultApps=" & defaultApps & vbLf
    End Function

    ' Is the setup file's MAC valid over its canonical? DPAPI-unprotect [Integrity] Key +
    ' FixedTimeEquals, exactly like ConfigMacIsValidForIni but over the setup canonical.
    ' False (never throws) on any failure - absent/invalid MAC, unreadable key,
    ' foreign-machine blob.
    Private Function SetupMacIsValidForIni(ByVal ini As IniFile) As Boolean
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return False
            Return ConfigIntegrity.ConfigMacIsValid(SetupCanonicalFromIni(ini), ini.GetKeyValue(IntegritySection, IntegrityMacName), key)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' Has first-run setup completed? True ONLY if the setup file loads, its MAC is valid
    ' AND [Setup] Done = "yes" (case-insensitive). Fail-closed: a missing file, a read
    ' error, a tampered field/MAC, or Done<>"yes" all read as NOT set up (the arm-gate
    ' then refuses and the user re-runs `setup`). Never throws. Independent of any block or
    ' schedule - it reads only the setup file, so an active block does not make setup
    ' "complete" and setup does not touch a live block.
    Public Function SetupIsComplete() As Boolean
        Try
            Dim path As String = SetupIniPath()
            If Not File.Exists(path) Then Return False
            Dim ini As New IniFile
            ini.Load(path)
            If Not SetupMacIsValidForIni(ini) Then Return False
            Return String.Equals(If(ini.GetKeyValue(SetupSection, SetupDoneKey), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase)
        Catch
            Return False
        End Try
    End Function

    ' The stored accountability-partner label (informational only - a name/contact the
    ' user relays their one-time codes to). "" if setup is not complete or none was set.
    ' Read-only; never throws.
    Public Function SetupPartnerLabel() As String
        Try
            Dim path As String = SetupIniPath()
            If Not File.Exists(path) Then Return ""
            Dim ini As New IniFile
            ini.Load(path)
            ' The SAME fail-closed completeness gate as SetupIsComplete (valid MAC AND Done="yes"),
            ' but reusing this single load rather than loading the file twice: an incomplete/tampered
            ' setup file never leaks a label.
            If Not SetupMacIsValidForIni(ini) Then Return ""
            If Not String.Equals(If(ini.GetKeyValue(SetupSection, SetupDoneKey), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase) Then Return ""
            Return If(ini.GetKeyValue(SetupSection, SetupPartnerKey), "").Trim()
        Catch
            Return ""
        End Try
    End Function

    ' C6c: the account-DEFAULT cooling-off wait in seconds every later `block` inherits when it
    ' gives no --cooloff of its own. 0 = none set / setup not complete / unusable. Gated on the
    ' SAME fail-closed completeness as SetupPartnerLabel (valid MAC AND Done="yes"), so a
    ' missing/incomplete/tampered setup file yields 0 -> DoBlock falls back to 0 = the service's
    ' compile-time floor, NEVER a shorter value. THE load-bearing fail-safe: a forged/blanked
    ' default can only lose the extension (fall back to the floor), never shorten cooling-off
    ' below it - only the MAC-covered honest value can EXTEND. A non-positive, unparseable, or
    ' above-cap (> MaxCoolOffSeconds) stored value also yields 0. Mirrors the service's
    ' ParseConfiguredCoolOffSeconds fail-safe (Long.TryParse + > 0), plus the same 365d cap the
    ' CLI parse enforces - re-clamped here so no future caller can smuggle an oversized duration
    ' past it into the service's per-tick HighWater.AddSeconds. Read-only; never throws.
    Public Function SetupDefaultCoolOffSeconds() As Long
        Try
            Dim path As String = SetupIniPath()
            If Not File.Exists(path) Then Return 0
            Dim ini As New IniFile
            ini.Load(path)
            If Not SetupMacIsValidForIni(ini) Then Return 0
            If Not String.Equals(If(ini.GetKeyValue(SetupSection, SetupDoneKey), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase) Then Return 0
            Dim seconds As Long
            If Long.TryParse(If(ini.GetKeyValue(SetupSection, SetupCoolOffKey), "").Trim(), seconds) AndAlso seconds > 0 AndAlso seconds <= MaxCoolOffSeconds Then Return seconds
            Return 0
        Catch
            Return 0
        End Try
    End Function

    ' D1b: the account-DEFAULT blocklist (bare domains) every later `block` inherits when it names NO
    ' explicit site source. Empty array = none set / setup not complete / unusable. Gated on the SAME
    ' fail-closed completeness as SetupPartnerLabel + SetupDefaultCoolOffSeconds (valid MAC AND
    ' Done="yes"), so a missing/incomplete/tampered setup file yields NO default -> DoBlock simply has
    ' no sites to inherit. This is safe by construction: the default only ever feeds a NEW arm (exactly
    ' like --sites), never a live block, so it can neither lift nor shorten an active block. A forged/
    ' added default is over-block-safe (blocks MORE, never less) and the user sees the armed sites
    ' printed; a blanked/tampered one merely loses the default (the user then names sites explicitly or
    ' gets "nothing to block"). Split on , / ; , trimmed, deduped case-insensitively (defensive - the
    ' stored value is already clean). Read-only; never throws.
    Public Function SetupDefaultSites() As String()
        Try
            Dim path As String = SetupIniPath()
            If Not File.Exists(path) Then Return New String() {}
            Dim ini As New IniFile
            ini.Load(path)
            If Not SetupMacIsValidForIni(ini) Then Return New String() {}
            If Not String.Equals(If(ini.GetKeyValue(SetupSection, SetupDoneKey), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase) Then Return New String() {}
            Dim raw As String = If(ini.GetKeyValue(SetupSection, SetupDefaultSitesKey), "")
            Dim domains As New List(Of String)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each rawTok As String In raw.Split(New Char() {","c, ";"c})
                Dim tok As String = rawTok.Trim()
                If tok <> "" AndAlso seen.Add(tok) Then domains.Add(tok)
            Next
            Return domains.ToArray()
        Catch
            Return New String() {}
        End Try
    End Function

    ' D2b: the account-DEFAULT app-kill list (bare .exe names) every later `block` inherits when it
    ' names NO explicit app source. Empty array = none set / setup not complete / unusable. Gated on
    ' the SAME fail-closed completeness as SetupDefaultSites (valid MAC AND Done="yes"), so a missing/
    ' incomplete/tampered setup file yields NO default -> DoBlock simply has no apps to inherit. Safe
    ' by construction: the default only ever feeds a NEW arm (exactly like --apps), never a live block,
    ' so it can neither lift nor shorten an active block. A forged/added default is over-block-safe
    ' (kills MORE, never less) and the user sees the armed apps printed; a blanked/tampered one merely
    ' loses the default. Split on , / ; , trimmed, deduped case-insensitively (.exe-normalisation is left
    ' to PackApps at arm time, as for a hand-typed --apps name). Read-only; never throws.
    Public Function SetupDefaultApps() As String()
        Try
            Dim path As String = SetupIniPath()
            If Not File.Exists(path) Then Return New String() {}
            Dim ini As New IniFile
            ini.Load(path)
            If Not SetupMacIsValidForIni(ini) Then Return New String() {}
            If Not String.Equals(If(ini.GetKeyValue(SetupSection, SetupDoneKey), "").Trim(), "yes", StringComparison.OrdinalIgnoreCase) Then Return New String() {}
            Dim raw As String = If(ini.GetKeyValue(SetupSection, SetupDefaultAppsKey), "")
            Dim apps As New List(Of String)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each rawTok As String In raw.Split(New Char() {","c, ";"c})
                Dim tok As String = rawTok.Trim()
                If tok <> "" AndAlso seen.Add(tok) Then apps.Add(tok)
            Next
            Return apps.ToArray()
        Catch
            Return New String() {}
        End Try
    End Function

    ' Write (or re-write) the first-run setup file: [Setup] Done="yes" + the optional partner
    ' label + the optional account-default cooling-off duration, MAC-stamped from birth (fresh
    ' key + MAC over the setup canonical).
    ' Idempotent - re-running just overwrites (a fresh key/MAC) and is safe while a block
    ' is active (it never touches the enforcement config). Returns True only if the fresh
    ' stamp is genuinely MAC-valid on disk; False if DPAPI stamping failed (the caller
    ' warns - an unstamped setup file reads as NOT complete, so the arm-gate keeps
    ' refusing rather than trust an unprotected marker). A file-write failure throws to the
    ' verb's outer Catch (reported as an error), never a half-written trusted state.
    Public Function WriteSetupConfig(Optional ByVal partnerLabel As String = "", Optional ByVal coolOffSeconds As Long = 0, Optional ByVal defaultSites As String = "", Optional ByVal defaultApps As String = "") As Boolean
        Dim ini As New IniFile
        ini.AddSection(SetupSection)
        ini.SetKeyValue(SetupSection, SetupDoneKey, "yes")
        ini.SetKeyValue(SetupSection, SetupPartnerKey, If(partnerLabel, "").Trim())
        ' C6c: the account-default cooling-off duration, MAC-covered from birth (set BEFORE
        ' StampFreshSetupMac, like Partner). Written ONLY when > 0 (absent = "" = no default;
        ' the absent case round-trips identically at stamp + verify). A plain integer ToString()
        ' is culture-invariant, matching SetupDefaultCoolOffSeconds + the service's parse.
        If coolOffSeconds > 0 Then
            ini.SetKeyValue(SetupSection, SetupCoolOffKey, coolOffSeconds.ToString())
        End If
        ' D1b: the account-default blocklist, MAC-covered from birth (set BEFORE StampFreshSetupMac,
        ' like Partner/CoolOffSeconds). Written ONLY when non-empty (absent = "" = no default; the
        ' absent case round-trips identically at stamp + verify). A comma-joined bare-domain list,
        ' already merged + deduped by TryBuildDefaultSites at the setup verb.
        Dim trimmedDefault As String = If(defaultSites, "").Trim()
        If trimmedDefault <> "" Then
            ini.SetKeyValue(SetupSection, SetupDefaultSitesKey, trimmedDefault)
        End If
        ' D2b: the account-default app list, MAC-covered from birth (set BEFORE StampFreshSetupMac,
        ' like DefaultSites). Written ONLY when non-empty (absent = "" = no default; the absent case
        ' round-trips identically). A comma-joined bare-.exe list, already merged + deduped by
        ' TryBuildDefaultApps at the setup verb.
        Dim trimmedDefaultApps As String = If(defaultApps, "").Trim()
        If trimmedDefaultApps <> "" Then
            ini.SetKeyValue(SetupSection, SetupDefaultAppsKey, trimmedDefaultApps)
        End If
        StampFreshSetupMac(ini)
        ini.Save(SetupIniPath())
        ' Re-read + verify: only report success if the file on disk is genuinely MAC-valid
        ' (a DPAPI failure in StampFreshSetupMac would have left it unstamped).
        Try
            Dim check As New IniFile
            check.Load(SetupIniPath())
            Return SetupMacIsValidForIni(check)
        Catch
            Return False
        End Try
    End Function

    ' Stamp a fresh key + MAC over the setup canonical (mirrors StampFreshMac, but for the
    ' setup file's PRIVATE canonical). Best-effort: a DPAPI failure leaves the file
    ' unstamped (WriteSetupConfig then returns False).
    Private Sub StampFreshSetupMac(ByVal ini As IniFile)
        Try
            Dim key() As Byte = ConfigIntegrity.NewRandomKey()
            Dim protectedKey As String = ConfigIntegrity.ProtectKey(key)
            If protectedKey Is Nothing Then Return
            ini.AddSection(IntegritySection)
            ini.SetKeyValue(IntegritySection, IntegrityKeyName, protectedKey)
            ini.SetKeyValue(IntegritySection, IntegrityMacName, ConfigIntegrity.ComputeConfigMac(SetupCanonicalFromIni(ini), key))
        Catch ex As Exception
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
