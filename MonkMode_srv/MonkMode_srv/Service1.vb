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
Imports System.Collections.Generic

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

        ' AppDomain.UnhandledException backstop (fail-closed on crash): if an
        ' exception escapes every local handler and is about to terminate the
        ' LocalSystem service, re-assert the hosts enforcement BEFORE the process
        ' dies, so a crash can never leave the block fail-OPEN. The process-wide
        ' generalisation of the adder_Changed / timer self-heal Try/Finally re-lock
        ' (2026-06-16 morning-fix #2). See OnUnhandledException.
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnUnhandledException

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
        Me.timer.Interval = CDbl(TimerIntervalMs)
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
            If Not ConfigBackup.PrimaryIsStructurallyUsable(iniFile.Sections.Count) Then
                ' B7 fail-closed: a truncated/blanked ini (fewer than the expected
                ' sections) is treated EXACTLY like a corrupt one - NEVER stopMe().
                ' The old code called stopMe() here, which let an attacker lift the
                ' block by truncating monkmode_settings.ini to <2 sections and
                ' forcing a restart (no MAC forge needed) - strictly easier than
                ' the recover-the-3DES-key attack B7 exists to stop.
                ' C1b (R8): recover instead of only defaulting - RESTORE the primary
                ' from a MAC-valid backup if one exists (so the block keeps a
                ' liftable config and ends at its real expiry), else write the
                ' UNSTAMPED default (unchanged behaviour: readers fail CLOSED,
                ' macValid=False, holds until re-armed from the CLI). Either way the
                ' block keeps enforcing (fall-through below); the next tick evaluates
                ' expiry off the recovered config.
                RecoverPrimaryConfig()
            Else
                ' B4: decide the OnStart expiry off the HIGH-WATER MARK, not raw
                ' DateTime.Now. NextHighWater advances the stored value to "now"
                ' ONLY for a Trusted (within-ceiling) advance; the boot gap after
                ' real downtime is a big delta => ForwardJump => the stored value
                ' is kept => the downtime is NOT credited toward expiry and the
                ' block stays standing (the intended fail-closed cost of defeating
                ' clock-forward). A clock rolled forward while off is likewise a
                ' jump and never lifts the block here.
                Dim storedHw As String = encryptionW.DecryptData(iniFile.GetKeyValue("Time", "HighWater"))
                ' B4 creep fix: seed the monotonic anchor for the timer ticks, and
                ' do NOT advance HighWater at OnStart. A restart has no trustworthy
                ' monotonic elapsed from the previous run to bound an advance by
                ' (TickCount64 resets across reboots), and crediting a wall gap here
                ' was itself a creep vector (+ceiling per restart). So the boot gap
                ' is never credited and OnStart expiry is decided off the STORED
                ' mark; live ticks advance it, bounded by real elapsed.
                lastMonoMs = Environment.TickCount64
                ' C5b (b2): seed the wall-clock jump-OVER anchor together with the monotonic
                ' one, so the first LIVE tick after this (re)start measures wallDelta from
                ' boot - not from the stale pre-reboot [CurrentTime] Now - and a window that
                ' closed during the downtime stays MISSED (crux #4b), not falsely re-opened.
                lastTickWallNow = DateTime.Now.ToString(culture)
                Dim newHw As String = storedHw

                Dim macValidAtStart As Boolean = ConfigMacIsValidForIni(iniFile)
                ' C2b: read the cooling-off deadline too - OnStart's one lift path
                ' now goes through the shared EffectiveExit (expiry OR cooling-off
                ' elapsed, both MAC-gated, both measured against the STORED
                ' HighWater - OnStart never advances the mark). A reboot mid-
                ' cooling-off therefore resumes the countdown off the persisted
                ' mark: downtime is never credited (B4 semantic) = an over-wait,
                ' never an early lift. An unreadable/absent CoolOffUntil reads
                ' not-elapsed; a tampered one fails the MAC (freeze).
                Dim coolOffEncAtStart As String = iniFile.GetKeyValue("Time", "CoolOffUntil")
                Dim coolOffAtStart As String = If(coolOffEncAtStart = "", "", encryptionW.DecryptData(coolOffEncAtStart))
                ' C3b: read the MAC-covered [Partner] UnlockedAt (plaintext, as-stored)
                ' so OnStart's one lift path also re-lifts a code-unlocked block. This
                ' is the LOAD-BEARING re-lift: if the service was resurrected in the
                ' stopMe() gap after a code-unlock, OnStart sees the persisted flag and
                ' completes the teardown (the same convergence cooling-off relies on).
                Dim unlockedAtStart As String = iniFile.GetKeyValue("Partner", "UnlockedAt")
                ' C5b: read the stored [Schedule] ActiveUntil (encrypted like CoolOffUntil;
                ' "" = no window open) so OnStart's one lift path also HOLDS through an open
                ' scheduled window (SD1 - a window out-ranks expiry/cooling-off/code at boot
                ' too). SUB-SLICE (b1): OnStart decides off the STORED deadline (it never
                ' advances HighWater, so it uses storedHw as the frame); the boot-mode
                ' window RE-EVALUATION that can (re)open a window at boot is (b2). On every
                ' existing block this reads "" (inert).
                Dim scheduleActiveEncAtStart As String = iniFile.GetKeyValue("Schedule", "ActiveUntil")
                Dim scheduleActiveAtStart As String = If(scheduleActiveEncAtStart = "", "", encryptionW.DecryptData(scheduleActiveEncAtStart))
                ' C5b (b2) boot re-hold: re-evaluate the wall-clock windows at boot
                ' (isBoot:=True) so a reboot that lands INSIDE a window RE-OPENS it (writes
                ' ActiveUntil off the STORED HighWater - OnStart never advances the mark) and
                ' the lift path below then HOLDS through it (SD1); a reboot AFTER a window
                ' closed leaves it clear (the one intended miss, crux #4b). isBoot disables
                ' live jump-OVER detection (no trustworthy monoElapsed across a reboot), so
                ' lastNow/monoElapsed are unused - pass ""/0. This may Save a fresh ini (new
                ' ActiveUntil + re-stamp); safe because the only later OnStart save
                ' (ShouldRestampOnStart) is inert here (newHw = storedHw), so it can't clobber
                ' this write. No Spec => inert fast path (unchanged on every existing block).
                Dim scheduleSpecAtStart As String = iniFile.GetKeyValue("Schedule", "Spec")
                scheduleActiveAtStart = ProcessScheduleWindows(scheduleActiveAtStart, scheduleSpecAtStart, "", DateTime.Now.ToString(culture), storedHw, 0, macValidAtStart, True)
                ' C5b (c2): OnStart's scheduleArmed hold. A freshly-armed schedule-only block
                ' at boot (past [Time] Until sentinel, Spec present, no window open yet) MUST
                ' stay alive to await its window - without this the single lift path below would
                ' stopMe() it the instant the service starts (the §4C OnStart trap). EXACT
                ' derivation via ScheduleArmed (EXACT, like the tick); "" Spec on every existing
                ' block => False => inert. ScheduleArmed is frame-independent (Spec + macValid
                ' only, no HighWater input); the EffectiveExit below decides in the STORED frame
                ' (storedHw - OnStart never advances the high-water mark).
                Dim scheduleArmedAtStart As Boolean = ScheduleArmed(macValidAtStart, scheduleSpecAtStart)
                If EffectiveExit(encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until")), coolOffAtStart, unlockedAtStart, scheduleActiveAtStart, storedHw, 0, macValidAtStart, scheduleArmedAtStart) Then
                    ' The ONLY OnStart path that may lift the block: a valid B7 MAC
                    ' AND (a successfully parsed, genuinely past end time OR an
                    ' elapsed cooling-off deadline OR a partner-verified code-unlock),
                    ' measured against the trusted high-water mark. An unparseable
                    ' Until or a tampered/invalid MAC keeps the block standing (fail
                    ' closed).
                    stopMe()
                ElseIf ShouldRestampOnStart(macValidAtStart, newHw, storedHw) Then
                    ' CURRENTLY INERT (retained as a guard): since the B4 creep fix,
                    ' OnStart sets newHw = storedHw (it never advances - no monotonic
                    ' anchor survives a restart to bound an advance), so this branch's
                    ' guard (newHw <> storedHw, inside ShouldRestampOnStart) is never
                    ' true and OnStart never re-stamps. It is kept as the fail-closed
                    ' gate IN CASE a future change re-introduces an OnStart advance:
                    ' the B7 hole it closes is a guardian SCM-restart within the
                    ' HighWater ceiling re-blessing a tampered [Time] Until at boot,
                    ' so any re-added advance MUST stay gated on macValidAtStart here.
                    iniFile.SetKeyValue("Time", "HighWater", encryptionW.EncryptData(newHw))
                    RestampMacWithExistingKey(iniFile)
                    iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
                End If
            End If

        Catch ex As Exception
            ' Corrupt/unreadable ini: recover fail-closed, same as the <2 sections
            ' branch above. C1b (R8): RESTORE from a MAC-valid backup if one exists
            ' (block ends at its real expiry), else rewrite the safe UNSTAMPED
            ' default 7-day block (readers fail CLOSED until re-armed).
            RecoverPrimaryConfig()
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
        ' O1 fail-closed (issue #1): the read-only attribute here is defence-in-
        ' depth, NOT the enforcement itself - the 10s tick re-asserts it every
        ' beat (best-effort, swallowed) and the B2 self-heal repairs the hosts
        ' CONTENT independent of the attribute. The old code called stopMe() on
        ' a SetAttr failure: the ONE remaining path where an ERROR lifted the
        ' block (full teardown - hosts stripped, snapshot/backup deleted,
        ' SafeBoot/DoH/deny-ACL undone, process Ended - off a transient or
        ' attacker-induced FS error at boot). Now: a short bounded retry, then
        ' DEGRADE with the block kept standing - the next successful tick
        ' re-locks the attribute. TrySetHostsReadOnly never throws (an OnStart
        ' throw would fail the service start, which is also fail-open).
        TrySetHostsReadOnly(hostDirS, OnStartReadOnlyAttempts, OnStartReadOnlyRetryDelayMs)

        ' B3: register under SafeBoot so enforcement survives a Safe Mode reboot.
        ' Only reached on the active path - an expired/invalid block calls stopMe()
        ' above, which Ends the process before here (and removes any stale keys).
        AssertSafeBootRegistration()

        ' B5a: force browser DoH off on the active path (same active-path-only
        ' placement as the SafeBoot registration - an expired/invalid block
        ' stopMe()s above, which restores the user's prior DoH policy from the
        ' snapshot). AssertDohPolicy is per-entry Try internally, so no outer Try.
        AssertDohPolicy()

        ' B6: deny DELETE on the service object so `sc delete MONKMODE` is refused
        ' while the block is active. Same active-path-only placement as the
        ' SafeBoot registration (an expired/invalid block stopMe()s above). The
        ' per-tick re-assert below undoes any casual re-ACL; stopMe() removes the
        ' deny at genuine expiry. Best-effort - a failure must not abort OnStart.
        Try
            AssertDenyDeleteAce()
        Catch ex As Exception
        End Try

    End Sub

    ' Rewrite a safe default 7-day block (the inherited panic behaviour), used by
    ' OnStart on both fail-closed paths: a corrupt/unreadable ini (the Catch) AND
    ' a truncated/blanked one (< 2 sections). B7: this default is deliberately
    ' left UNSTAMPED (no [Integrity] Key/Mac) - only the CLI mints a fresh key
    ' when legitimately arming a block. With no MAC the readers fail CLOSED
    ' (macValid = False), so this recovery block stays standing until it is
    ' re-armed from the CLI, rather than silently auto-lifting from a config that
    ' just failed to parse or was truncated to lift the block. That is the
    ' tamper-resistant direction (an evader who corrupts OR truncates the ini does
    ' not get a liftable block); the trade-off is a corrupted-at-expiry ini must
    ' be re-armed by hand instead of timing out.
    Private Sub WriteDefaultBlock()
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
        ' which the en-CA reads can then fail to parse.
        iniFile.SetKeyValue("Time", "Until", encryptionW.EncryptData(DateAdd("d", 7, DateTime.Now).ToString(culture)))
        iniFile.SetKeyValue("Time", "TimeChanging", "no")
        ' B4: seed HighWater (en-CA LOCAL, encrypted) so the recovery default has
        ' the same shape as a CLI-armed block. Unstamped, so macValid stays False
        ' and the block holds regardless; this just keeps the ini uniform.
        iniFile.SetKeyValue("Time", "HighWater", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
        iniFile.AddSection("CurrentTime")
        iniFile.SetKeyValue("CurrentTime", "Now", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
        iniFile.AddSection("Process")
        iniFile.SetKeyValue("Process", "List", "null")
        iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
    End Sub

    ' ===== C1b (R8, LOAD-BEARING): config shadow-backup recovery =====
    '
    ' Recover a corrupt/blanked/short primary ini. RESTORE the primary from a
    ' MAC-valid backup if one exists - so the block keeps a LIFTABLE, MAC-valid
    ' config and lifts at its real expiry - instead of freezing into the UNSTAMPED
    ' WriteDefaultBlock (which macValid=False keeps standing until a manual CLI
    ' re-arm; harmless-but-TRAPPING now that R1 removed the unconditional escape).
    ' Only if there is NO trustworthy backup do we fall back to WriteDefaultBlock
    ' (the prior behaviour). Returns True iff the primary was restored from a good
    ' backup.
    '
    ' This is ONLY reached from the OnStart/tick CORRUPT paths (a parse/decrypt
    ' throw, or < 2 sections). A parseable-but-MAC-invalid (TAMPERED) config takes
    ' the normal path and FREEZES per B7 - it is never "recovered" (that
    ' distinction is the whole point: a tamper must not be silently undone).
    Private Function RecoverPrimaryConfig() As Boolean
        If TryRestorePrimaryFromBackup() Then Return True
        WriteDefaultBlock()
        Return False
    End Function

    ' Restore the primary from the shadow backup iff the backup is MAC-valid. THE
    ' load-bearing gate is ConfigMacIsValidForIni(backupIni): a corrupt, tampered
    ' or unstamped backup reads as invalid and CopyIfSourceValid refuses to copy it
    ' over the primary (no data loss) - so corrupt-primary + corrupt-backup falls
    ' through to WriteDefaultBlock, exactly as today. Best-effort; never throws.
    Private Function TryRestorePrimaryFromBackup() As Boolean
        Try
            Dim backupPath As String = Application.StartupPath + "\" + ConfigBackup.BackupFileName
            If Not My.Computer.FileSystem.FileExists(backupPath) Then Return False
            Dim backupIni As New IniFile
            backupIni.Load(backupPath)
            Return ConfigBackup.CopyIfSourceValid(backupPath,
                                                  Application.StartupPath + "\monkmode_settings.ini",
                                                  ConfigMacIsValidForIni(backupIni))
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' Refresh the shadow backup from the primary after a LEGITIMATE (MAC-valid)
    ' service write (the heartbeat Restamp). iniFile is the in-memory object just
    ' saved to the primary; CopyIfSourceValid copies the file only when that object
    ' is MAC-valid, so a corrupt/tampered primary can never overwrite the good
    ' backup. Best-effort - a failed refresh just keeps the previous good backup.
    Private Sub RefreshBackupFromValid(ByVal iniFile As IniFile)
        Try
            ConfigBackup.CopyIfSourceValid(Application.StartupPath + "\monkmode_settings.ini",
                                           Application.StartupPath + "\" + ConfigBackup.BackupFileName,
                                           ConfigMacIsValidForIni(iniFile))
        Catch ex As Exception
        End Try
    End Sub

    ' C2b live wiring: the per-tick cooling-off request/cancel poll. Runs INSIDE
    ' tickLock and only while TimeChanging="no" (the caller gates both), so a
    ' request/cancel can never race the heartbeat's read-modify-write or
    ' interleave with a clock-change. currentCoolOffUntil/highWaterText/macValid
    ' are this tick's already-loaded state; returns the EFFECTIVE decrypted
    ' CoolOffUntil after any transition so the SAME tick's heartbeat decides off
    ' the post-signal state - that ordering is what makes "cancel wins" hold in
    ' the cancel-vs-elapse race (a cancel processed here clears the deadline
    ' before the heartbeat ever sees it; tickLock serialises the two).
    '
    ' Consume-after-persist (crash-safe): on Start the ini (with the new
    ' deadline) is SAVED before the request trigger is deleted - a crash between
    ' the two leaves the trigger, and the next tick classifies it Ignore
    ' (already pending) and just deletes it: no lost request, no double-set. On
    ' Cancel the cleared ini is saved before BOTH triggers are deleted (a torn
    ' cancel leaves the deadline standing - cooling-off continues, the user
    ' re-cancels; never an early lift). Both write paths re-validate the MAC on
    ' the RELOADED object before touching it (the heartbeat's #4 TOCTOU rule:
    ' never re-stamp bytes you didn't just verify) and re-stamp with the
    ' EXISTING key - this modifies an existing block; only the CLI mints keys.
    ' Every successful write refreshes the C1b shadow backup so a later
    ' corrupt-then-restore carries the cooling-off state. Best-effort throughout;
    ' a throw never crashes the tick (the tick continues off the returned state).
    Private Function ProcessCoolOffSignals(ByVal currentCoolOffUntil As String, ByVal highWaterText As String, ByVal macValid As Boolean, ByVal committed As Boolean) As String
        Try
            Dim requestPath As String = Application.StartupPath + "\" + CoolOffRequestFileName
            Dim cancelPath As String = Application.StartupPath + "\" + CoolOffCancelFileName
            Dim requestPresent As Boolean = System.IO.File.Exists(requestPath)
            Dim cancelPresent As Boolean = System.IO.File.Exists(cancelPath)
            ' Fast path: no triggers this tick (the overwhelmingly common case).
            If Not requestPresent AndAlso Not cancelPresent Then Return currentCoolOffUntil

            ' C4: `committed` is now the real MAC-covered flag (was the False C4 seam).
            ' A committed block refuses a cooling-off Start (code-only exit); the
            ' partner code still lifts it (ProcessPartnerCodeSignal, which does NOT
            ' read committed). Cancel is still honoured (harmless on a committed block,
            ' which never has a pending deadline to clear).
            Select Case ClassifyCoolOffSignal(requestPresent, cancelPresent, currentCoolOffUntil <> "", committed, macValid)
                Case CoolOffAction.Start
                    ' The service is the SOLE deadline writer: trusted HighWater at the
                    ' request + max(configured duration, floor). C6b: the configured
                    ' duration is the MAC-covered [CoolOff] Duration field (CLI-written at
                    ' arm). Read it off the RELOADED + MAC-validated ini below (never the
                    ' pre-validation object) so a tampered value is caught by the freeze,
                    ' and the floor clamp in ComputeCoolOffDeadline means it can only ever
                    ' EXTEND, never shorten below the floor. An uncomputable deadline
                    ' (unparseable HighWater) writes nothing and leaves the trigger for
                    ' the next tick.
                    Dim iniFile = New IniFile
                    iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
                    If Not ConfigMacIsValidForIni(iniFile) Then Return currentCoolOffUntil
                    Dim configuredSeconds As Long = ParseConfiguredCoolOffSeconds(iniFile.GetKeyValue("CoolOff", "Duration"), MinCoolOffFloorSeconds)
                    Dim deadline As String = ComputeCoolOffDeadline(highWaterText, configuredSeconds, MinCoolOffFloorSeconds)
                    If deadline = "" Then Return currentCoolOffUntil
                    iniFile.SetKeyValue("Time", "CoolOffUntil", encryptionW.EncryptData(deadline))
                    RestampMacWithExistingKey(iniFile)
                    iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
                    RefreshBackupFromValid(iniFile)
                    Try
                        System.IO.File.Delete(requestPath)
                    Catch ex As Exception
                    End Try
                    Return deadline
                Case CoolOffAction.Cancel
                    Dim iniFile = New IniFile
                    iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
                    If Not ConfigMacIsValidForIni(iniFile) Then Return currentCoolOffUntil
                    iniFile.SetKeyValue("Time", "CoolOffUntil", "")
                    RestampMacWithExistingKey(iniFile)
                    iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
                    RefreshBackupFromValid(iniFile)
                    Try
                        System.IO.File.Delete(requestPath)
                    Catch ex As Exception
                    End Try
                    Try
                        System.IO.File.Delete(cancelPath)
                    Catch ex As Exception
                    End Try
                    Return ""
                Case Else
                    ' Ignore: no ini write. Delete any stale trigger (a request
                    ' while one is already pending, a consumed request whose
                    ' delete crashed, triggers against a frozen config) so it
                    ' doesn't re-classify forever.
                    Try
                        System.IO.File.Delete(requestPath)
                    Catch ex As Exception
                    End Try
                    Try
                        System.IO.File.Delete(cancelPath)
                    Catch ex As Exception
                    End Try
                    Return currentCoolOffUntil
            End Select
        Catch ex As Exception
            Return currentCoolOffUntil
        End Try
        Return currentCoolOffUntil
    End Function

    ' C3b live wiring: the per-tick partner-code verify poll. Runs INSIDE tickLock
    ' and only while TimeChanging="no" (the caller gates both, exactly like the
    ' cooling-off poll), right after ProcessCoolOffSignals, and returns the
    ' (possibly newly-set) [Partner] UnlockedAt so THIS tick's heartbeat decides off
    ' the post-verify state. currentUnlockedAt is this tick's already-read UnlockedAt;
    ' macValid is this tick's already-computed MAC validity. The candidate is read
    ' from the trigger; the VERIFIER (Salt/Hash) is read from the RELOADED,
    ' MAC-revalidated ini - the same bytes UnlockedAt is written onto (see below),
    ' never a value captured earlier in the tick.
    '
    ' Consume-after-persist (crash-safe, mirroring ProcessCoolOffSignals): on a MATCH
    ' the ini (UnlockedAt set + re-stamped) is SAVED and the C1b backup refreshed
    ' BEFORE the trigger is deleted - a crash between them leaves the trigger, and the
    ' next tick classifies alreadyUnlocked => Ignore and just deletes it (no lost
    ' unlock, no double-set). On a miss/Ignore, just delete the trigger (no ini
    ' write). Best-effort throughout; a throw never crashes the tick (it continues
    ' off the returned UnlockedAt).
    Private Function ProcessPartnerCodeSignal(ByVal currentUnlockedAt As String, ByVal macValid As Boolean) As String
        Try
            Dim codePath As String = Application.StartupPath + "\" + PartnerCodeFileName
            Dim triggerPresent As Boolean = System.IO.File.Exists(codePath)
            ' Fast path: no trigger this tick (the overwhelmingly common case).
            If Not triggerPresent Then Return currentUnlockedAt

            ' Read the candidate, length-capped: an over-large trigger is a memory/DoS
            ' lever, not a real attempt, so it reads as "" (a non-matching attempt) and
            ' is simply deleted. The service NEVER logs the candidate.
            Dim candidate As String = ""
            Try
                Dim fi As New FileInfo(codePath)
                If fi.Length <= PartnerCodeTriggerMaxBytes Then
                    candidate = System.IO.File.ReadAllText(codePath)
                End If
            Catch ex As Exception
                candidate = ""
            End Try

            Select Case ClassifyPartnerCodeSignal(triggerPresent, Not String.IsNullOrWhiteSpace(candidate), currentUnlockedAt <> "", macValid)
                Case PartnerCodeAction.Verify
                    ' TOCTOU re-validate (the heartbeat's #4 rule): macValid was computed
                    ' on the tick's EARLIER read; RELOAD the ini and re-validate its MAC,
                    ' then verify the candidate against - and write UnlockedAt onto - that
                    ' SAME reloaded, just-revalidated object. The service is the sole
                    ' [Partner] writer and this runs inside tickLock, so the verifier
                    ' bytes and the write bytes are provably one consistent MAC-valid
                    ' config (no split-read seam). Never mint a key - re-stamp with the
                    ' EXISTING key (this modifies an existing block).
                    Dim iniFile = New IniFile
                    iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
                    If Not ConfigMacIsValidForIni(iniFile) Then
                        Try
                            System.IO.File.Delete(codePath)
                        Catch ex As Exception
                        End Try
                        Return currentUnlockedAt
                    End If
                    If ConfigIntegrity.PartnerCodeMatches(candidate, iniFile.GetKeyValue("Partner", "Salt"), iniFile.GetKeyValue("Partner", "Hash")) Then
                        ' MATCH: set the MAC-covered UnlockedAt, re-stamp, save, refresh
                        ' the backup - THEN delete the trigger (consume-after-persist).
                        Dim unlockedAt As String = DateTime.Now.ToString(culture)
                        iniFile.SetKeyValue("Partner", "UnlockedAt", unlockedAt)
                        RestampMacWithExistingKey(iniFile)
                        iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
                        RefreshBackupFromValid(iniFile)
                        Try
                            System.IO.File.Delete(codePath)
                        Catch ex As Exception
                        End Try
                        Return unlockedAt
                    Else
                        ' A miss HOLDS the block: delete the trigger, NO ini write, and
                        ' do NOT rotate/invalidate the code (only success rotates - else
                        ' a user could grief-lock the partner's legitimate code by
                        ' spamming misses, the PD6 availability concern).
                        Try
                            System.IO.File.Delete(codePath)
                        Catch ex As Exception
                        End Try
                        Return currentUnlockedAt
                    End If
                Case Else
                    ' Ignore (blank candidate, already unlocked, or a frozen config):
                    ' delete any stale trigger so it doesn't re-classify forever; no
                    ' ini write.
                    Try
                        System.IO.File.Delete(codePath)
                    Catch ex As Exception
                    End Try
                    Return currentUnlockedAt
            End Select
        Catch ex As Exception
            Return currentUnlockedAt
        End Try
        Return currentUnlockedAt
    End Function

    ' C5b (b2) live wiring: the per-tick schedule window step - the sibling of
    ' ProcessCoolOffSignals/ProcessPartnerCodeSignal, polled AFTER them (inside tickLock
    ' while TimeChanging="no", the caller gates both), returning the post-step [Schedule]
    ' ActiveUntil so THIS tick's heartbeat (its SD1 schedule-hold arm) decides off it. This
    ' is the FIRST code that ever WRITES a non-empty ActiveUntil: it runs the window->
    ' duration conversion (design §4.1/§6.1) - evaluate the wall-clock windows, convert each
    ' open one to a HighWater-anchored deadline, extend-never-shorten into ActiveUntil
    ' (NextScheduleActiveUntil, the pure decision), and clear it at the window's monotonic
    ' close. currentScheduleActiveUntil/newHwText/macValid are the tick's already-loaded
    ' state; lastNowText (the in-memory lastTickWallNow anchor from the previous tick - NOT
    ' the stored [CurrentTime] Now, which is stale across a reboot)/nowText/monoElapsedSeconds
    ' drive the live jump-OVER detection (§4.2); isBoot=False for a live tick (OnStart passes
    ' True - and lastNow/monoElapsed unused - to re-hold a window a reboot lands inside).
    '
    ' macValid REQUIRED to act - a frozen config never has its schedule state modified or
    ' re-stamped (fail-closed: an invalid MAC is already enforcing). The service is the SOLE
    ' writer of ActiveUntil (the guardian only reads it), so there is no write race. Persist
    ' ONLY on change, via PersistScheduleActiveUntil (RELOAD + TOCTOU re-validate + re-stamp
    ' with the existing key + Save + refresh the C1b backup) - the ProcessCoolOffSignals
    ' discipline. Best-effort throughout; a throw never crashes the tick (it continues off
    ' the returned value). No CLI writes a Spec until sub-slice (c), so in production this
    ' fast-paths out (spec="" AND ActiveUntil="") on every block - (b2) ships the machinery
    ' + its e2e tests; the site/app UNION enforcement behind BlockHeld is (b3).
    Private Function ProcessScheduleWindows(ByVal currentScheduleActiveUntil As String, ByVal spec As String, ByVal lastNowText As String, ByVal nowText As String, ByVal newHwText As String, ByVal monoElapsedSeconds As Long, ByVal macValid As Boolean, ByVal isBoot As Boolean) As String
        Try
            ' Frozen config: never touch the schedule state (already enforcing, fail-closed).
            If Not macValid Then Return currentScheduleActiveUntil
            ' Fast path: no schedule rule AND no window currently open - the overwhelmingly
            ' common case (every block that never used `monkmode schedule`). Nothing to do.
            If String.IsNullOrEmpty(spec) AndAlso currentScheduleActiveUntil = "" Then Return currentScheduleActiveUntil
            Dim openNow As List(Of ScheduleOpen) = EvaluateWindows(ParseSchedule(spec).Windows, lastNowText, nowText, monoElapsedSeconds, isBoot)
            Dim target As String = NextScheduleActiveUntil(currentScheduleActiveUntil, openNow, newHwText)
            If target <> currentScheduleActiveUntil Then
                ' A window opened/extended, or an elapsed window cleared: persist it. If the
                ' TOCTOU re-validate fails (the config went invalid in the read->reload
                ' window) nothing is written and we keep the pre-read value (fail-closed; the
                ' heartbeat's own reload re-validates and Holds).
                If PersistScheduleActiveUntil(target) Then Return target
                Return currentScheduleActiveUntil
            End If
            Return currentScheduleActiveUntil
        Catch ex As Exception
            Return currentScheduleActiveUntil
        End Try
    End Function

    ' Persist [Schedule] ActiveUntil = newValue ("" clears it), the ProcessCoolOffSignals
    ' write discipline: RELOAD the ini, TOCTOU re-validate its MAC (only re-stamp bytes just
    ' re-verified - never re-bless a swap in the read->reload window), set the field
    ' (encrypted like CoolOffUntil; "" stored verbatim = no window), re-stamp with the
    ' EXISTING key, Save, and refresh the C1b backup (guarded on the in-memory MAC, so a bad
    ' primary can never overwrite a good backup - no data loss). Returns True iff the write
    ' happened (a failed re-validate returns False; the caller keeps the old value). Best-
    ' effort; never throws (the caller is inside a Try too).
    Private Function PersistScheduleActiveUntil(ByVal newValue As String) As Boolean
        Try
            Dim iniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            If Not ConfigMacIsValidForIni(iniFile) Then Return False
            iniFile.SetKeyValue("Schedule", "ActiveUntil", If(newValue = "", "", encryptionW.EncryptData(newValue)))
            RestampMacWithExistingKey(iniFile)
            iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
            RefreshBackupFromValid(iniFile)
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' B4 creep fix: a MONOTONIC anchor (Environment.TickCount64, ms since boot -
    ' immune to wall-clock changes) captured at the last HighWater advance. The
    ' per-tick HighWater credit is capped by the real elapsed since this anchor, so
    ' nudging the wall clock forward each tick can't advance the mark faster than
    ' real time. Seeded at OnStart; 0 = not yet seeded (=> credit 0 that tick).
    Private lastMonoMs As Long = 0

    ' C5b (b2): the WALL-CLOCK sibling of lastMonoMs - the previous tick's DateTime.Now,
    ' held in memory and seeded at OnStart alongside lastMonoMs. The schedule jump-OVER
    ' detector needs wallDelta and monoElapsed measured over the SAME (previous-live-tick ->
    ' this-tick) interval. The STORED [CurrentTime] Now can't serve as lastNow: it is stale
    ' across a reboot (OnStart's active path never rewrites it), so the FIRST live tick would
    ' read the whole downtime gap as a live jump and re-open a window crux #4b says must be
    ' MISSED. Seeding this in memory at OnStart - exactly like lastMonoMs, which also resets
    ' on every (re)start - makes the first live tick measure wallDelta from boot, not from
    ' the pre-reboot Now, so a missed window stays missed. "" = not yet seeded (=> the
    ' evaluator cannot detect a jump that tick; the HighWater still holds any open window).
    ' Rolled only while TimeChanging="no" (see the tick), so it freezes through the notifier's
    ' clock-change flag and a jump-OVER coinciding with it still surfaces (SD4, fail-closed).
    ' Supersedes the C5a design §4.2 "lastNowText = the stored [CurrentTime] Now": in-memory +
    ' boot-seeded closes the reboot-staleness gap the b2 verifier caught, while the "no"-gated
    ' roll keeps the old stored-Now cadence (which was heartbeat-written, also "no"-gated).
    Private lastTickWallNow As String = ""

    ' #2 (audit P2): serialize timer ticks. System.Timers.Timer runs with
    ' AutoReset=True and no SynchronizingObject, so a tick that overruns the 10s
    ' interval (it does Process.GetProcesses() + file/registry/SCM I/O) would
    ' re-enter on another threadpool thread and race lastMonoMs + the [Time]
    ' HighWater read-modify-write. TryEnter SKIPS a tick while one is still
    ' running - benign (the next tick re-asserts every gate), and never blocks a
    ' threadpool thread the way a plain SyncLock could.
    Private ReadOnly tickLock As New Object

    Private Sub timer_Elapsed(ByVal sender As System.Object, ByVal e As System.Timers.ElapsedEventArgs) Handles timer.Elapsed
        ' Re-entrancy guard (#2): if the previous tick is still running, skip this
        ' one rather than racing it. Released in the Finally at the end of the Sub.
        If Not Threading.Monitor.TryEnter(tickLock) Then Return
        Try

        Dim processList As System.Diagnostics.Process() = Nothing
        Dim Proc As System.Diagnostics.Process
        Dim notifyFound As Boolean = False
        Dim iniProcessList As String = ""
        Dim iniUntil As String = ""
        ' C2b: the decrypted [Time] CoolOffUntil for THIS tick ("" = no cooling-
        ' off pending, also the fail-closed default when the read fails - an
        ' unreadable deadline can never lift the block, only hold it).
        Dim iniCoolOffUntil As String = ""
        ' C3b: the [Partner] UnlockedAt exit flag for THIS tick ("" = not code-
        ' unlocked, the fail-closed default - a tick that couldn't read it holds
        ' the block). Plaintext-as-stored (MAC-covered), not decrypted.
        Dim iniPartnerUnlockedAt As String = ""
        ' C5b: the decrypted [Schedule] ActiveUntil (an open window's converted monotonic
        ' close) for THIS tick ("" = no window open, also the fail-closed default on a
        ' read failure). SUB-SLICE (b2): now WRITTEN by ProcessScheduleWindows below (the
        ' window->duration conversion), so an open scheduled window makes the heartbeat's SD1
        ' schedule-hold arm (ScheduleActive) LIVE. Still "" on every block with no [Schedule]
        ' Spec - and no CLI writes a Spec until sub-slice (c) - so production stays inert;
        ' (b2) ships the machinery + e2e tests. The self-heal ENFORCEMENT sites still key off
        ' the manual block only (the site/app union enforcement behind BlockHeld is (b3)).
        Dim iniScheduleActiveUntil As String = ""
        ' C5b (b2): the [Schedule] Spec (recurring-window rule, plaintext-as-stored, MAC-
        ' covered) read in the Try below; the wall-clock jump-OVER pair (prevTickWallNow = the
        ' in-memory anchor from the PREVIOUS tick = lastNow; tickWallNow = this tick's now);
        ' and monoElapsedSeconds (hoisted from the B4 block). All method-scope so they survive
        ' the Try into the schedule poll section. prevTickWallNow uses the in-memory anchor
        ' (seeded at OnStart), NOT the stored [CurrentTime] Now, so a reboot's downtime gap is
        ' never misread as a live jump (crux #4b - see lastTickWallNow above).
        Dim iniScheduleSpec As String = ""
        Dim prevTickWallNow As String = ""
        Dim tickWallNow As String = ""
        Dim monoElapsedSeconds As Long = 0
        ' C4: whether THIS block is committed (self-serve cooling-off disabled = code-
        ' only exit). Only consulted under macValid (a frozen config Ignores cooling-off
        ' regardless), and under macValid the MAC-covered flag is authentic. Default
        ' not-committed on a failed read - harmless, since a failed read => not macValid.
        Dim iniCommitted As Boolean = False
        ' B7: MAC validity for this tick. Default FALSE = fail closed: if the
        ' config can't even be read, or the MAC doesn't verify, the block is
        ' treated as active (never expired) and every self-heal gate keeps
        ' enforcing. Computed once below while the ini is loaded.
        Dim macValid As Boolean = False
        ' B4: the trusted high-water mark for THIS tick (en-CA LOCAL string) and
        ' the DateTime asOf every expiry/self-heal gate below is driven off. The
        ' default asOf = MinValue is fail-closed: against any parseable Until,
        ' MinValue is far in the past so the block reads NOT expired (a tick that
        ' couldn't read/advance HighWater never lifts the block). newHw "" means
        ' "nothing to persist this tick".
        Dim newHw As String = ""
        Dim newHwAsOf As DateTime = DateTime.MinValue

        Try
            Dim iniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            ' C1b (R8): a blanked/truncated primary (< 2 sections, no parse throw)
            ' recovers here - RESTORE from a MAC-valid backup if one exists, else the
            ' unstamped default - then RELOAD so THIS tick enforces off the recovered
            ' config (a restored block can then lift at its real expiry; an unstamped
            ' default holds fail-closed with macValid=False). A genuinely corrupt read
            ' below instead throws into the Catch, which recovers the same way. This is
            ' the short-ini path the tick previously lacked (it only had the Catch), so
            ' a blanked-mid-block primary no longer freezes when a good backup exists.
            If Not ConfigBackup.PrimaryIsStructurallyUsable(iniFile.Sections.Count) Then
                RecoverPrimaryConfig()
                iniFile = New IniFile
                iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            End If
            iniUntil = encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until"))
            iniTimeChanging = iniFile.GetKeyValue("Time", "TimeChanging")
            ' C2b: read the cooling-off deadline alongside Until (encrypted like
            ' the other [Time] datetimes; absent/empty = none pending).
            Dim coolOffEnc As String = iniFile.GetKeyValue("Time", "CoolOffUntil")
            iniCoolOffUntil = If(coolOffEnc = "", "", encryptionW.DecryptData(coolOffEnc))
            ' C3b: read the [Partner] UnlockedAt exit flag (plaintext, as-stored -
            ' MAC-covered, not decrypted). Absent/"" = not code-unlocked.
            iniPartnerUnlockedAt = iniFile.GetKeyValue("Partner", "UnlockedAt")
            ' C5b: read the schedule's converted monotonic deadline [Schedule] ActiveUntil
            ' (encrypted like CoolOffUntil; absent/empty = no window open). Consumed via
            ' ScheduleActive so an open window out-ranks every lift trigger (SD1), and updated
            ' by ProcessScheduleWindows below. Decrypt-before-macValid is LOAD-BEARING: a
            ' garbled ciphertext must THROW here (-> Catch -> RecoverPrimaryConfig, fail-
            ' closed), never silently read "" (which would drop the schedule hold).
            Dim scheduleActiveEnc As String = iniFile.GetKeyValue("Schedule", "ActiveUntil")
            iniScheduleActiveUntil = If(scheduleActiveEnc = "", "", encryptionW.DecryptData(scheduleActiveEnc))
            ' C5b (b2): the recurring-window rule [Schedule] Spec (plaintext-as-stored, MAC-
            ' covered - a tampered Spec fails the MAC -> freeze, B7). The jump-OVER lastNow is
            ' NOT read from the ini (the stored [CurrentTime] Now is stale across a reboot) but
            ' from the in-memory lastTickWallNow anchor captured below.
            iniScheduleSpec = iniFile.GetKeyValue("Schedule", "Spec")
            ' C4: read the [Commit] Committed policy flag ("yes"=committed). MAC-covered.
            iniCommitted = IsCommitted(iniFile.GetKeyValue("Commit", "Committed"))
            iniProcessList = iniFile.GetKeyValue("Process", "List")
            If StrComp("null", iniProcessList) <> 0 Then
                iniProcessList = encryptionW.DecryptData(iniProcessList)
            End If
            ' B7: evaluate the tamper-evident MAC (DPAPI-unprotect [Integrity]
            ' Key, validate [Integrity] Mac over the canonical). Invalid/absent
            ' MAC or a DPAPI failure -> False -> block stays standing.
            macValid = ConfigMacIsValidForIni(iniFile)
            ' B4: advance the monotonic high-water mark. Read the stored value
            ' (decrypted), then NextHighWater advances it to "now" ONLY if the
            ' advance is a Trusted real tick; a clock-forward jump or a backward
            ' roll leaves it unchanged, so a rolled clock can never carry it past
            ' Until. EVERY expiry/self-heal decision below uses newHwAsOf (the
            ' parsed HighWater) as asOf instead of DateTime.Now - that is the
            ' whole B4 fix. The new value is persisted in the heartbeat save
            ' below (one save) so it advances each live tick.
            Dim storedHw As String = encryptionW.DecryptData(iniFile.GetKeyValue("Time", "HighWater"))
            ' B4 creep fix: NextHighWater gives the candidate (wall 'now' on a Trusted
            ' tick, else the stored value unchanged), then CapHighWaterAdvance bounds
            ' the ACTUAL advance to the REAL monotonic elapsed (Environment.TickCount64,
            ' clock-change-immune) since the last tick. So a wall clock nudged +119s
            ' before each 10s tick credits only the real ~10s - defeating the
            ' within-ceiling creep that the per-step 120s ceiling alone allowed.
            ' monoElapsed is 0 on the first tick after OnStart seeded the anchor.
            Dim nowMono As Long = Environment.TickCount64
            ' monoElapsedSeconds is method-scoped (hoisted up top) so the schedule poll
            ' below can read it too; assigned (not re-declared) here.
            monoElapsedSeconds = If(lastMonoMs <= 0, 0L, (nowMono - lastMonoMs) \ 1000L)
            lastMonoMs = nowMono
            ' C5b (b2): capture this tick's wall 'now' for the schedule jump-OVER detection
            ' and remember the PREVIOUS tick's now as lastNow. Captured right after lastMonoMs
            ' so wallDelta (tickWallNow - prevTickWallNow) and monoElapsedSeconds span the SAME
            ' previous-tick->this-tick interval - the whole point of an in-memory anchor over
            ' the stored Now. The anchor is ROLLED only while TimeChanging="no" (the same gate
            ' the schedule poll + heartbeat take below): during the notifier's ~2s clock-change
            ' flag the poll is skipped, so freezing the anchor through the episode keeps the
            ' full wallDelta visible on the resume tick - a live forward jump-OVER a window that
            ' coincides with the flag still surfaces (fail-closed, SD4), instead of being hidden
            ' by an anchor that rolled to the post-jump time mid-episode. A no-op flag (no real
            ' change) leaves wallDelta ~= real elapsed, so it never false-opens. lastMonoMs
            ' stays ungated (B4 needs it every tick); across a >5min episode the two anchors can
            ' diverge by that episode, which only ever OVER-blocks (never lifts).
            prevTickWallNow = lastTickWallNow
            tickWallNow = DateTime.Now.ToString(culture)
            If StrComp("no", iniTimeChanging) = 0 Then lastTickWallNow = tickWallNow
            ' B1: advance on the REAL monotonic elapsed regardless of wall DIRECTION
            ' (a backward roll or forward jump credits mono instead of freezing, so
            ' the block ends at its real duration - the P2 fix). A Trusted tick is
            ' byte-identical to the old NextHighWater+CapHighWaterAdvance composition.
            newHw = AdvanceHighWater(storedHw, DateTime.Now.ToString(culture), monoElapsedSeconds, HighWaterJumpCeilingSeconds)
            Dim parsedHw As DateTime
            If DateTime.TryParse(newHw, culture, DateTimeStyles.None, parsedHw) Then
                newHwAsOf = parsedHw
            End If
        Catch ex As Exception
            ' C1b (R8): corrupt/unreadable primary. RESTORE from a MAC-valid backup
            ' if one exists, else write the UNSTAMPED default (the prior inline
            ' behaviour, now centralised in RecoverPrimaryConfig -> WriteDefaultBlock;
            ' re-arm from the CLI to get a fresh MAC + a liftable block). This tick
            ' then proceeds fail-closed (macValid stays False, newHwAsOf MinValue =
            ' the block holds); the NEXT tick reads the recovered config fresh and,
            ' if it was restored, lifts normally at the REAL expiry instead of
            ' over-running to the 7-day default (the old inline default extended
            ' every corrupt tick to +7d; restoring from the backup avoids that).
            RecoverPrimaryConfig()
        End Try

        ' C2b: poll the cooling-off request/cancel triggers - inside tickLock,
        ' and only while no clock change is in flight (the same TimeChanging
        ' guard the heartbeat takes, so a signal can never interleave with a
        ' clock-change state transition). Returns the POST-signal deadline so
        ' this tick's heartbeat below decides off it - a cancel processed here
        ' wins over an elapse the same tick (fail-closed: stay blocked).
        If StrComp("no", iniTimeChanging) = 0 Then
            iniCoolOffUntil = ProcessCoolOffSignals(iniCoolOffUntil, newHw, macValid, iniCommitted)
            ' C3b: poll the partner-code trigger AFTER cooling-off (still inside
            ' tickLock + the TimeChanging="no" guard). Running it after ProcessCoolOff-
            ' Signals is what makes a valid code beat a same-tick --cancel: UnlockedAt
            ' is never cleared by cancel, so a correct code lifts even if a cooling-off
            ' cancel landed the same tick (a partner-authorised exit is authoritative
            ' over the user's own change-of-mind about the slow path). Returns the
            ' post-verify UnlockedAt so THIS tick's heartbeat decides off it.
            iniPartnerUnlockedAt = ProcessPartnerCodeSignal(iniPartnerUnlockedAt, macValid)
            ' C5b (b2): poll the schedule windows AFTER cooling-off + code (still inside
            ' tickLock + the TimeChanging="no" guard). The FIRST step that can WRITE a
            ' non-empty [Schedule] ActiveUntil - the window->duration conversion (§6.1):
            ' evaluate the wall-clock windows off the in-memory lastNow (prevTickWallNow) and
            ' this tick's now (tickWallNow), convert each open one to a HighWater-anchored
            ' deadline, extend-never-shorten, and clear at the monotonic close. Returns the
            ' post-step ActiveUntil so THIS tick's heartbeat + its SD1 schedule-hold arm decide
            ' off it (an open window out-ranks expiry/cooling-off/code). isBoot:=False (a live
            ' tick; OnStart re-evaluates with isBoot:=True). No Spec => inert fast path.
            iniScheduleActiveUntil = ProcessScheduleWindows(iniScheduleActiveUntil, iniScheduleSpec, prevTickWallNow, tickWallNow, newHw, monoElapsedSeconds, macValid, False)
        End If

        ' C5b (b3-i/b3-ii): the effective schedule state for THIS tick's ENFORCEMENT, computed
        ' ONCE now that iniScheduleActiveUntil is settled (the post-poll value when TimeChanging=
        ' "no", else the read value - the self-heal below runs every tick regardless) and SHARED by
        ' the hosts self-heal UNION (b3-ii, below) and the app-kill UNION (b3-i). ScheduleActive is
        ' deliberately NOT macValid-gated here (see the app-kill loop / the b3-i verifier's P3): a
        ' union only ever ADDS enforcement, so a frozen/forged config can only ever block MORE,
        ' never lift. activeSchedule is parsed ONCE and only while active (the inert path does no
        ' extra work); it is never Nothing when scheduleActiveNow is True (ParseSchedule always
        ' returns a non-null ParsedSchedule). Every block today reads iniScheduleActiveUntil="" ->
        ' scheduleActiveNow=False -> both unions degenerate to today's exact behaviour.
        Dim scheduleActiveNow As Boolean = ScheduleActive(iniScheduleActiveUntil, newHw)
        Dim activeSchedule As ParsedSchedule = If(scheduleActiveNow, ParseSchedule(iniScheduleSpec), Nothing)

        ' Re-assert the read-only lock on hosts every tick (cheap tamper-resist;
        ' we no longer hold the file open, so this is how the lock is maintained).
        Try
            If My.Computer.FileSystem.FileExists(hostDirS) Then SetAttr(hostDirS, vbReadOnly)
        Catch ex As Exception
        End Try

        ' C5b (c1): the schedule-only hosts snapshot LIFECYCLE (design §4A). Give a schedule-
        ' only block the SAME MAC-independent on-disk monkmode_hosts.block a manual block gets
        ' from the CLI, so (P2#1) the self-heal below re-asserts the schedule sites FROM the
        ' snapshot under macValid=False+forged ActiveUntil (its EffectiveHostsBlock inert path
        ' returns the snapshot VERBATIM), and (P2#2) ReassertHostsFailClosed's File.Exists gate
        ' lets a crash re-block. The service creates the snapshot on window-OPEN + deletes it on
        ' CLOSE for a schedule-OWNED block; a manual hold's snapshot is NEVER touched
        ' (manualHold => Leave), which is also the fail-closed catch-all (every macValid=False
        ' reads as a manual hold => the snapshot is preserved, never deleted, under tamper).
        ' Gated on a schedule being in play (a Spec, or an open/stored window) so a NO-schedule
        ' block is byte-identical (the CLI/stopMe stay the sole snapshot actors). manualHold uses
        ' newHwAsOf (the trusted high-water mark), so a clock-forward can't flip it. Placed BEFORE
        ' the self-heal so the snapshot is persisted first each tick - the self-heal synthesises
        ' the same target this tick either way, but persisting first is what arms the MAC-
        ' independent fallback the self-heal reads on a LATER tamper tick + the crash backstop.
        ' Best-effort.
        Try
            If iniScheduleSpec <> "" OrElse iniScheduleActiveUntil <> "" Then
                Dim manualHold As Boolean = Not EffectiveBlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid)
                ProcessScheduleSnapshot(Application.StartupPath + "\monkmode_hosts.block",
                                        scheduleActiveNow, manualHold,
                                        If(scheduleActiveNow, activeSchedule.Sites, Nothing))
            End If
        Catch ex As Exception
        End Try

        ' B2 self-heal: between ticks an admin can clear the attribute and
        ' edit/blank/delete hosts; while the block is HELD (BlockHeld: the manual
        ' block hasn't effectively expired - unparseable Until OR an invalid B7 MAC
        ' fail CLOSED to held - OR a scheduled window is open, SD1) restore our
        ' entries from the snapshot the CLI persisted next to the exe.
        ' B4: asOf is newHwAsOf (the trusted high-water mark), NOT DateTime.Now,
        ' so a clock-forward can't flip this to "expired" and stop the repair.
        ' C5b (b3-ii): while a scheduled window is open the repair TARGET is the UNION of the
        ' manual snapshot's entries and the schedule's OWN synthesised site entries
        ' (EffectiveHostsBlock, design §6.3) - the service's FIRST stateful hosts writes for a
        ' block it armed itself. A SCHEDULE-ONLY block has no snapshot, so the synthesised schedule
        ' entries ARE the block: the gate no longer requires a snapshot FILE - it enters whenever
        ' HELD and the computed target is non-empty. A no-schedule block (scheduleActiveNow=False)
        ' makes EffectiveHostsBlock return the snapshot VERBATIM, so with a snapshot present this
        ' is RepairHostsBlock(hostsText, snapshot) - BYTE-IDENTICAL to before - and with no
        ' snapshot the target is "" and nothing is written (also as before). Try/Catch so a
        ' transient lock never crashes the service.
        Try
            Dim snapshotPath As String = Application.StartupPath + "\monkmode_hosts.block"
            If BlockHeld(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid, iniScheduleActiveUntil, newHw) Then
                ' The manual block's snapshot (the CLI persisted it at arm), or "" for a
                ' schedule-only block that never manually armed (no snapshot file on disk).
                Dim snapshotBlock As String = ""
                If My.Computer.FileSystem.FileExists(snapshotPath) Then
                    snapshotBlock = My.Computer.FileSystem.ReadAllText(snapshotPath)
                End If
                ' The effective marker block for this tick: snapshot UNION schedule sites while a
                ' window is open, else the snapshot verbatim (the no-schedule byte-identity).
                Dim expectedBlock As String = EffectiveHostsBlock(snapshotBlock, If(scheduleActiveNow, activeSchedule.Sites, Nothing), scheduleActiveNow)
                If Not String.IsNullOrWhiteSpace(expectedBlock) Then
                    Dim hostsText As String = ""
                    If My.Computer.FileSystem.FileExists(hostDirS) Then
                        hostsText = My.Computer.FileSystem.ReadAllText(hostDirS)
                    End If
                    Dim repaired As String = RepairHostsBlock(hostsText, expectedBlock)
                    If repaired IsNot Nothing Then
                        If My.Computer.FileSystem.FileExists(hostDirS) Then SetAttr(hostDirS, vbNormal)
                        Try
                            ' C1: atomic write (temp + rename) - this self-heal fires
                            ' every 10s tick, so an in-place truncate-rewrite here was
                            ' opening a blank-hosts/lost-user-entries window constantly.
                            AtomicHosts.WriteAtomic(hostDirS, repaired)
                        Finally
                            ' Even if the write throws mid-way, never leave hosts
                            ' writable (a writable hosts is the fail-OPEN state the
                            ' DNS client would then re-read around).
                            SetAttr(hostDirS, vbReadOnly)
                        End Try
                    End If
                End If
            End If
        Catch ex As Exception
        End Try

        ' B1 watchdog, layer 2: keep the SYSTEM guardian peer (mm_guard.exe,
        ' next to this exe) alive while the block is active. The decision is
        ' the pure, tested ShouldRestartPeer gate - fail CLOSED via
        ' Not BlockHasExpired (unparseable Until = still active = keep the
        ' guardian up), no duplicate spawn while one is running, nothing
        ' started if the exe is missing. The guardian reciprocally restarts
        ' this service via the SCM if it is force-killed. Try/Catch so a spawn
        ' failure can never crash the enforcement tick.
        Try
            Dim guardianExe As String = Application.StartupPath + "\mm_guard.exe"
            ' B4: blockActive uses newHwAsOf (trusted high-water mark), not
            ' DateTime.Now, so a clock-forward can't read as "expired" and let
            ' the guardian be dropped early. C5b (b3-i): via BlockHeld an open scheduled
            ' window (ScheduleActive) also keeps the guardian peer up (SD1), so a
            ' schedule-only block is watched too; a no-schedule block (ActiveUntil="")
            ' is byte-identical (BlockHeld reduces to Not EffectiveBlockHasExpired).
            If ShouldRestartPeer(System.Diagnostics.Process.GetProcessesByName("mm_guard").Length,
                                 BlockHeld(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid, iniScheduleActiveUntil, newHw),
                                 My.Computer.FileSystem.FileExists(guardianExe)) Then
                System.Diagnostics.Process.Start(guardianExe)
            End If
        Catch ex As Exception
        End Try

        ' B3 SafeBoot self-heal: re-assert the Safe Mode registration every tick
        ' while the block is HELD (an admin can delete the keys between ticks).
        ' Fail CLOSED via BlockHeld - an unparseable Until OR an invalid B7 MAC keeps
        ' the keys asserted; C5b (b3-i): an open scheduled window (ScheduleActive) also
        ' holds them (SD1), so a schedule-only block keeps Safe Mode registered while
        ' its window is open. stopMe() removes them at a genuine expiry. B4: asOf is
        ' newHwAsOf (trusted high-water mark), not DateTime.Now, so a clock-forward
        ' can't drop the keys early. No-schedule block (ActiveUntil="") is byte-identical.
        Try
            If BlockHeld(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid, iniScheduleActiveUntil, newHw) Then
                AssertSafeBootRegistration()
            End If
        Catch ex As Exception
        End Try

        ' B5a DoH-off self-heal: re-assert the browser Secure-DNS policy every tick
        ' while the block is HELD (an admin can flip a browser's DoH back on or
        ' delete our policy value between ticks). Same VERBATIM fail-closed gate as
        ' the B3 SafeBoot re-assert above - BlockHeld: an unparseable Until OR an
        ' invalid B7 MAC keeps the policy asserted, and C5b (b3-i) an open scheduled
        ' window (ScheduleActive) also holds it (SD1). stopMe() restores the user's
        ' prior at a genuine expiry. B4: asOf is newHwAsOf (trusted high-water mark),
        ' not DateTime.Now, so a clock-forward can't drop the policy early. Own Try so a
        ' registry hiccup here never disturbs the SafeBoot re-assert or crashes the tick.
        Try
            If BlockHeld(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid, iniScheduleActiveUntil, newHw) Then
                AssertDohPolicy()
            End If
        Catch ex As Exception
        End Try

        ' B6 deny-DELETE self-heal: re-assert the service-object deny-DELETE ACE
        ' every tick while the block is HELD (an admin with WRITE_DAC can clear
        ' it between ticks, as a casual `sc sdset`/Process-Explorer re-ACL). Fail
        ' CLOSED via BlockHeld - an unparseable Until OR an invalid B7 MAC keeps the
        ' deny on, and C5b (b3-i) an open scheduled window (ScheduleActive) also holds it
        ' (SD1); stopMe() removes it at genuine expiry. B4: asOf is newHwAsOf (trusted
        ' high-water mark), not DateTime.Now, so a clock-forward can't drop the deny
        ' early. Read-only probe inside makes an intact DACL a no-op (no churn).
        ' Best-effort - never crash the tick. No-schedule block byte-identical.
        Try
            If BlockHeld(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid, iniScheduleActiveUntil, newHw) Then
                AssertDenyDeleteAce()
            End If
        Catch ex As Exception
        End Try

        ' C5b (b3-i): the effective app-kill set is the manual [Process] List, PLUS - only while a
        ' scheduled window is OPEN - the schedule's own apps (design §6.3 app-kill union, SD2),
        ' from the SHARED scheduleActiveNow/activeSchedule computed once above (the same values the
        ' hosts UNION uses; deliberately NOT macValid-gated - see that hoist's note). A no-schedule
        ' block (scheduleActiveNow=False) makes EffectiveKillList return iniProcessList verbatim,
        ' so the loop below kills exactly what it does today (BYTE-IDENTICAL). SCOPE: this is only
        ' the SERVICE session-0 kill loop; the notifier's user-session loop (MM_notify\Form1.vb
        ' appKillTimer_Tick, SessionId<>0) still keys off iniProcessList alone, so blocking a
        ' schedule's USER-session apps (browsers/games) needs the same union THERE - a follow-up
        ' slice (b3-iii, see handoff), exactly as the manual block's app-kill is split today.
        Dim killList As String = EffectiveKillList(iniProcessList,
                                                   If(scheduleActiveNow, activeSchedule.Apps, Nothing),
                                                   scheduleActiveNow)
        processList = System.Diagnostics.Process.GetProcesses()
        For Each Proc In processList
            If Proc.SessionId = 0 Then
                Try
                    If killList.Contains(Proc.ProcessName + ".exe") Then
                        Proc.Kill()
                    End If
                Catch ex As Exception
                End Try
            End If
        Next

        If StrComp("no", iniTimeChanging) = 0 Then
            ' Fail CLOSED: only a parsed, genuinely past end time AND a valid B7
            ' MAC lifts the block; an unparseable Until or a tampered/invalid MAC
            ' skips the expiry action this tick (block stays standing).
            ' B4: expiry is decided off newHwAsOf (the trusted high-water mark),
            ' NOT raw DateTime.Now - this is the headline B4 change. A clock
            ' rolled forward past Until does not advance HighWater (the jump is
            ' refused), so this stays "not expired" and stopMe() is not called.
            ' B7 fail-open FIX: route the heartbeat through the pure ClassifyHeartbeat
            ' gate. The OLD code took an If/Else on EffectiveBlockHasExpired and
            ' re-stamped the MAC in the Else branch UNCONDITIONALLY - so a tampered
            ' [Time] Until (macValid=False, detected this tick) was re-blessed with a
            ' fresh valid MAC the same tick, and lifted the block next tick. That
            ' defeated B7 with a plain Until edit (no HMAC forge, no clock change).
            ' Now: only LIFT on a valid MAC + genuinely past end time; only RE-STAMP
            ' when the MAC was already valid (the service's own Now/HighWater writes
            ' are MAC-covered, so a legit config must be re-stamped or it'd go stale);
            ' otherwise HOLD - never re-stamp over an invalid MAC. B4 unchanged:
            ' expiry is still decided off newHwAsOf (the trusted high-water mark).
            ' C2b: cooling-off elapsed (against the SAME trusted HighWater) is the
            ' second Lift trigger - a completed cooling-off converges on the same
            ' stopMe() as natural expiry. Folded inside ClassifyHeartbeat's
            ' macValid gate, so a tampered config can never cool off its way out.
            ' C3b: PartnerUnlocked (over the post-verify [Partner] UnlockedAt) is the
            ' THIRD lift trigger, folded inside ClassifyHeartbeat's macValid gate - a
            ' tampered config can never code-unlock its way out (a raw-edited UnlockedAt
            ' fails the MAC => Hold). Natural expiry, cooling-off and partner-code all
            ' converge on the one Lift => stopMe().
            ' C5b (SD1): an OPEN scheduled window OUT-RANKS all three lift triggers -
            ' ClassifyHeartbeat's scheduleActive arm Restamps (keeps HighWater advancing
            ' so the window counts down), never lifting, until the window's own monotonic
            ' close. C5b (c2): scheduleArmedNow (macValid AndAlso the Spec parses to >=1
            ' window) is the BETWEEN-windows hold - a schedule-only block carries a past
            ' [Time] Until sentinel, so between windows blockExpired is True yet we must
            ' Restamp (stay alive for tomorrow's window), not stopMe. iniScheduleSpec is the
            ' SAME value the b2/b3 schedule wiring reads this tick; on every block until the
            ' CLI writes a Spec (c3) it is "" => scheduleArmedNow False and this is byte-
            ' identical to a manual block. Derived via ScheduleArmed (the EXACT form: macValid
            ' AndAlso ParseSchedule(Spec) yields >=1 window); the guardian uses its cheaper
            ' Spec-non-empty over-approximation (Guardian.ScheduleArmed, no 4th parser copy).
            Dim scheduleArmedNow As Boolean = ScheduleArmed(macValid, iniScheduleSpec)
            Select Case ClassifyHeartbeat(macValid, BlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds), CoolOffElapsedTime(iniCoolOffUntil, newHw), PartnerUnlocked(iniPartnerUnlockedAt), ScheduleActive(iniScheduleActiveUntil, newHw), scheduleArmedNow)
                Case HeartbeatAction.Lift
                    stopMe()
                Case HeartbeatAction.Restamp
                    Dim iniFile = New IniFile
                    iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
                    ' #4 (audit P2->P3) TOCTOU FIX: macValid (above) was computed on
                    ' the EARLIER read; this branch RELOADS the ini. A script that
                    ' swaps a past [Time] Until + stale MAC into the read->reload
                    ' window must not get blessed by the re-stamp below. Re-validate
                    ' the MAC on the RELOADED object and only re-stamp if it STILL
                    ' verifies; otherwise treat the tick as Hold - no re-stamp, no lift
                    ' (fail-closed), next tick re-evaluates fresh. The sibling sites
                    ' (OnStart, AppendAddToHosts, notifier) already validate the same
                    ' object they mutate; the heartbeat was the one site that reloaded.
                    If ConfigMacIsValidForIni(iniFile) Then
                        iniFile.SetKeyValue("CurrentTime", "Now", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
                        ' B4: persist the advanced high-water mark in the SAME save as the
                        ' heartbeat (one write). newHw is "now" on a Trusted tick and the
                        ' unchanged stored value on a jump/rollback (monotonic), so this
                        ' only ever moves HighWater forward at the real tick rate. Skip
                        ' when newHw is "" (a tick that couldn't read it - never blank a
                        ' good value).
                        If newHw <> "" Then
                            iniFile.SetKeyValue("Time", "HighWater", encryptionW.EncryptData(newHw))
                        End If
                        ' The heartbeat just rewrote [CurrentTime] Now AND [Time] HighWater,
                        ' both MAC-covered fields, so re-stamp [Integrity] Mac over the new
                        ' canonical with the existing key - safe here because the MAC was
                        ' re-verified just above (the only changes are ours). Reuses the
                        ' stored key; never re-arms.
                        RestampMacWithExistingKey(iniFile)
                        iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
                        ' C1b: the primary is MAC-valid (re-validated just above) and
                        ' freshly saved - refresh the shadow backup so a later corrupt
                        ' primary restores to THIS state (current HighWater/Now), not a
                        ' stale one. Guarded on the in-memory MAC, so this can never
                        ' overwrite the good backup with a bad primary.
                        RefreshBackupFromValid(iniFile)
                    End If
                Case HeartbeatAction.Hold
                    ' macValid=False: a tampered or unstamped (WriteDefaultBlock) config.
                    ' Fail CLOSED - do NOT re-stamp (that would re-bless the tamper and
                    ' let it lift next tick: the B7 bypass) and do NOT lift. The block
                    ' stays frozen until re-armed from the CLI / removed via unblock --force.
            End Select
        End If
        Finally
            ' #2: always release the per-tick lock so the next tick can run.
            Threading.Monitor.Exit(tickLock)
        End Try
    End Sub

    ' The enforcement cadence. Friend Consts so the guardian's unit tests can
    ' pin its own tick/grace to EXACTLY these values - the two halves of the
    ' B1 watchdog pair must agree on "expired" within one tick of each other,
    ' and a retune here that forgot the guardian would otherwise drift apart
    ' silently. TimerIntervalMs feeds InitializeComponent's timer;
    ' ExpiryGraceSeconds is the grace used at every timer-path
    ' BlockHasExpired call (OnStart deliberately uses the stricter 0).
    Friend Const TimerIntervalMs As Integer = 10000
    Friend Const ExpiryGraceSeconds As Long = 5

    ' B3 SafeBoot: the registry subkeys that make the MONKMODE service start in
    ' Safe Mode (Minimal) and Safe Mode with Networking (Network). Without them
    ' the service does NOT run in Safe Mode, so an evader could reboot there and
    ' edit hosts / delete the service unopposed. Each subkey is named after the
    ' service; what SafeBoot keys off is the subkey's PRESENCE, and its (Default)
    ' value is the conventional "Service" tag (drivers use "Driver"). Friend
    ' Consts (single source of truth) so the unit tests pin them - a typo in a
    ' path or the tag would silently disarm B3, so the suite fails loudly on drift.
    Friend Const SafeBootMinimalKey As String = "SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE"
    Friend Const SafeBootNetworkKey As String = "SYSTEM\CurrentControlSet\Control\SafeBoot\Network\MONKMODE"
    Friend Const SafeBootValue As String = "Service"

    ' B7 tamper-evident config: the [Integrity] section (DPAPI-protected HMAC key
    ' + MAC over the canonical of the decrypted config values).
    Friend Const IntegritySection As String = "Integrity"
    Friend Const IntegrityKeyName As String = "Key"
    Friend Const IntegrityMacName As String = "Mac"

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

    ' B7: the MAC-aware expiry decision. The block counts as expired ONLY when
    ' the time has genuinely passed (BlockHasExpired) AND the config MAC is
    ' valid. An invalid/absent MAC (a tampered ini - e.g. the attacker recovered
    ' the 3DES key and re-encrypted Until to "now") therefore reads as NOT
    ' expired, exactly like an unparseable Until: the block stays standing and
    ' never auto-lifts until a legitimate stamp exists. This does NOT gate
    ' stopMe() on the MAC directly - it just forces the "active" path, so the
    ' B2/B1/B3 self-heal gates (all keyed off Not <this>) keep enforcing too.
    ' Pure and Shared so it is unit tested; the live MAC/DPAPI evaluation that
    ' produces macValid (ConfigMacIsValidForIni) is the smoke-tested seam.
    Friend Shared Function EffectiveBlockHasExpired(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean) As Boolean
        Return macValid AndAlso BlockHasExpired(untilText, asOf, graceSeconds)
    End Function

    ' What the active-block heartbeat does on a given tick (TimeChanging="no").
    Friend Enum HeartbeatAction
        Lift     ' valid MAC + (past end time OR cooling-off elapsed OR code-unlocked) => stopMe()
        Restamp  ' valid MAC, no exit due => rewrite Now/HighWater + re-stamp the MAC
        Hold     ' INVALID MAC => fail closed: neither lift nor re-stamp (freeze)
    End Enum

    ' B7 fail-open FIX (the pure, pinned decision behind the heartbeat). The bug
    ' it closes: the old heartbeat re-stamped the MAC in the "not expired" branch
    ' UNCONDITIONALLY, so a tampered [Time] Until (macValid=False) was re-blessed
    ' with a fresh valid MAC the same tick and lifted the block the next tick -
    ' defeating B7 with a plain Until edit (the 3DES key is known by design; only
    ' the HMAC stops it). The rule:
    '   * Lift ONLY when macValid AND the block has genuinely expired. This is
    '     EXACTLY EffectiveBlockHasExpired (macValid AndAlso blockExpired), so the
    '     lift condition is unchanged.
    '   * Re-stamp ONLY when macValid (and no exit due): the service's own
    '     Now/HighWater writes are MAC-covered, so a LEGIT config must be
    '     re-stamped or its MAC would go stale and needlessly freeze it.
    '   * HOLD when the MAC is invalid: NEVER re-stamp over an unverified config
    '     (that is the bug) and never lift. The block stays frozen until re-armed.
    ' A regression test pins ClassifyHeartbeat(macValid:=False, blockExpired:=True)
    ' = Hold (the old code would have re-stamped here). Pure + Shared so the
    ' guardian-parity-style unit tests can pin it.
    '
    ' C2b: coolOffElapsed (CoolOffElapsedTime against the SAME trusted HighWater)
    ' is the SECOND lift trigger - natural expiry and a completed cooling-off
    ' converge on the one Lift => stopMe() teardown. It is folded INSIDE the
    ' macValid gate, so a tampered config can never cool off its way out
    ' (macValid=False => Hold regardless of coolOffElapsed) - the lift condition
    ' is exactly EffectiveExit, pinned by a test.
    '
    ' C3b: codeUnlocked (PartnerUnlocked over the MAC-covered [Partner] UnlockedAt)
    ' is the THIRD lift trigger - a partner-verified early exit converges on the
    ' SAME stopMe(). Also folded INSIDE the macValid gate: a non-empty UnlockedAt
    ' is only trusted UNDER a valid MAC (forging it by raw edit fails the MAC =>
    ' Hold), so a code-unlock can only ever have come from the service verifying a
    ' correct code. Lift <=> EffectiveExit still holds (now three OR-ed reasons).
    '
    ' C5b (SD1): scheduleActive (ScheduleActive over the MAC-covered [Schedule]
    ' ActiveUntil) is a HARD HOLD that OUT-RANKS every lift trigger. While a window
    ' is open, keep RE-STAMPING (that arm is what advances HighWater, so the window
    ' counts down to its OWN monotonic close) and NEVER lift - not on expiry, not on
    ' a completed cooling-off, not on a code. It mirrors EffectiveExit's
    ' `If ScheduleActive Then Return False`, so Lift <=> EffectiveExit still holds
    ' (both now also gate on NOT scheduleActive). Only the window reaching its
    ' monotonic close (ScheduleElapsed => scheduleActive False) releases it.
    '
    ' C5b (c2): scheduleArmed is the BETWEEN-windows lifecycle state the old binary
    ' Restamp/Lift model lacked (design §4C). A schedule-only block carries a PAST [Time]
    ' Until sentinel (ScheduleOnlyExpiredUntil, written by the CLI in c3), so between windows
    ' blockExpired is True - yet the block must NOT tear down, because a recurring schedule
    ' still needs the service alive for tomorrow's window. scheduleArmed (macValid AndAlso
    ' the Spec parses to >=1 window) RE-STAMPS instead of Lifting when an exit is otherwise
    ' due, giving three states: WINDOW-OPEN (scheduleActive -> Restamp, hard hold), BETWEEN-
    ' windows (scheduleArmed -> Restamp, idle: the self-heals stand down via BlockHeld while
    ' the service stays alive), TORN-DOWN (neither -> Lift -> stopMe, reached only once the
    ' Spec is cleared so scheduleArmed goes False). Without this arm a past-Until schedule-
    ' only block would Lift->stopMe at its first window's close and never enforce the next
    ' one (the §3 trap). EffectiveExit gains the identical `If scheduleArmed Then Return
    ' False` guard, so Lift <=> EffectiveExit still holds. INERT on every existing block
    ' until the CLI writes a Spec (c3): scheduleActive=False AND scheduleArmed=False => the
    ' arm is never consulted and behaviour is byte-identical to a manual block.
    Friend Shared Function ClassifyHeartbeat(ByVal macValid As Boolean, ByVal blockExpired As Boolean, ByVal coolOffElapsed As Boolean, ByVal codeUnlocked As Boolean, ByVal scheduleActive As Boolean, ByVal scheduleArmed As Boolean) As HeartbeatAction
        If Not macValid Then Return HeartbeatAction.Hold
        If scheduleActive Then Return HeartbeatAction.Restamp           ' SD1: an open window is a hard hold
        If blockExpired OrElse coolOffElapsed OrElse codeUnlocked Then
            If scheduleArmed Then Return HeartbeatAction.Restamp        ' c2: BETWEEN windows of a live schedule - stay alive, don't tear down
            Return HeartbeatAction.Lift                                 ' torn down: schedule cleared (or none) + a manual exit is due
        End If
        Return HeartbeatAction.Restamp
    End Function

    ' B7 fail-open FIX (the OnStart sibling of ClassifyHeartbeat). OnStart re-stamps
    ' the MAC only on a rare Trusted HighWater advance (a fast restart within the
    ' 120s ceiling). That re-stamp MUST also require a currently-valid MAC: without
    ' it, a guardian SCM-restart within the ceiling would re-bless a tampered
    ' [Time] Until at boot (a Trusted advance => unguarded re-stamp => the block
    ' lifts next heartbeat) - the same P0 as the old timer hole, via OnStart. So
    ' re-stamp ONLY when macValid AND there is a genuine advance to persist.
    ' Pure + Shared so it is pinned by a regression test.
    Friend Shared Function ShouldRestampOnStart(ByVal macValid As Boolean, ByVal newHw As String, ByVal storedHw As String) As Boolean
        Return macValid AndAlso newHw <> "" AndAlso newHw <> storedHw
    End Function

    ' ===== C2b: cooling-off (R1 - the service-adjudicated early exit) =====
    '
    ' `monkmode unblock` (bare) no longer tears anything down: the CLI only drops
    ' an authority-free, PRESENCE-ONLY trigger file next to the exes, and the
    ' SERVICE decides whether and when to lift - it writes a MAC-covered deadline
    ' [Time] CoolOffUntil = HighWater_at_request + max(duration, floor), counts it
    ' down against the B4 monotonic HighWater (never DateTime.Now), and lifts via
    ' the SAME stopMe() natural expiry uses (one teardown actor, one primitive).
    ' The trigger file's CONTENT is ignored (R2): the CLI is a legitimate MAC
    ' stamper, so any CLI-written timing field could be forged to "now" under a
    ' valid MAC - the request channel must carry ZERO timing authority. Triggers
    ' are POLLED at the top of each tick inside tickLock (no second
    ' FileSystemWatcher and its double-fire hazards; <=10s latency to BEGIN a
    ' wait is immaterial). The block stays FULLY ENFORCED during the wait - every
    ' self-heal gate still keys off Until, which is untouched.

    ' The two presence-only trigger files, in Application.StartupPath (MonkMode's
    ' own state zone next to the ini/snapshots - NOT the hosts adder's etc\ zone).
    ' Parity-pinned with the CLI copies (Blocker.CoolOff*FileName), like
    ' SnapshotName/BackupFileName - a drift would silently break the channel.
    Friend Const CoolOffRequestFileName As String = "monkmode_cooloff.request"
    Friend Const CoolOffCancelFileName As String = "monkmode_cooloff.cancel"

    ' C3b: the ONE content-bearing partner-code trigger. The CLI (unblock --code)
    ' drops it carrying the CANDIDATE code; the service reads that content as a
    ' verified ATTEMPT (R2 - it applies the KDF + compares to the MAC-covered
    ' verifier, it never obeys it as a command). Parity-pinned with the CLI copy
    ' (Blocker.PartnerCodeFileName), like CoolOff*FileName - a drift silently breaks
    ' the channel. In Application.StartupPath (MonkMode's own state zone).
    Friend Const PartnerCodeFileName As String = "monkmode_partner.code"

    ' Cap the trigger read: a code is ~11 chars, so an over-large trigger file is a
    ' memory/DoS lever, not a real attempt. A file above this reads as a
    ' non-matching attempt (candidate stays "" => Ignore => the trigger is deleted).
    Friend Const PartnerCodeTriggerMaxBytes As Long = 4096

    ' The compile-time FLOOR: the shortest cooling-off the service will ever
    ' grant, in seconds - THE one new C2b security parameter, pinned by a unit
    ' test exactly like HighWaterJumpCeilingSeconds. Load-bearing because the
    ' C6b configured duration ([CoolOff] Duration) is a CLI-written MAC-covered
    ' field, so an attacker running the CLI could set it to 0 under a valid MAC;
    ' the service clamps to this floor via max(configured, floor), and the floor
    ' is compile-time - not attacker-settable. A configured duration can only ever
    ' EXTEND the wait, never shorten below this floor. D1: 1 hour (recommended
    ' default; 15 min = light, 3 h = strict).
    Friend Const MinCoolOffFloorSeconds As Long = 3600

    ' Has the pending cooling-off deadline been reached? coolOffUntilText is the
    ' decrypted [Time] CoolOffUntil ("" = none pending); highWaterText is the
    ' trusted B4 mark the deadline is measured against - NEVER DateTime.Now, so a
    ' clock-forward can't reach the deadline early (HighWater refuses the jump)
    ' and a reboot pauses the countdown (downtime is never credited). Fail-closed
    ' on every axis: empty (none pending) and any unparseable input read as NOT
    ' elapsed - a corrupted deadline or mark can only ever hold the block, never
    ' lift it. The caller folds macValid, exactly as expiry does (mirrors
    ' EffectiveBlockHasExpired's split). Pure + Shared so it is unit tested;
    ' byte-for-byte the same semantics as the guardian copy (parity-pinned).
    Friend Shared Function CoolOffElapsedTime(ByVal coolOffUntilText As String, ByVal highWaterText As String) As Boolean
        If coolOffUntilText = "" Then Return False
        Dim ca As New CultureInfo("en-CA")
        Dim coolOffUntil As DateTime, highWater As DateTime
        If Not DateTime.TryParse(coolOffUntilText, ca, DateTimeStyles.None, coolOffUntil) Then Return False
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return False
        Return coolOffUntil <= highWater
    End Function

    ' The deadline the service (the SOLE writer) persists on a Start: the trusted
    ' HighWater at the request plus max(configured duration, floor). The deadline
    ' therefore lives in the HighWater frame - reached only after that much
    ' genuine ON-machine elapsed time - and can never be shorter than the
    ' compile-time floor even if the C6b configured duration says 0.
    ' Returns "" when the stored HighWater doesn't parse (fail-closed: no
    ' deadline computable => no write; the trigger stays for the next tick).
    ' Pure + Shared so it is unit tested.
    Friend Shared Function ComputeCoolOffDeadline(ByVal highWaterText As String, ByVal configuredDurationSeconds As Long, ByVal floorSeconds As Long) As String
        Dim ca As New CultureInfo("en-CA")
        Dim highWater As DateTime
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return ""
        Return highWater.AddSeconds(Math.Max(configuredDurationSeconds, floorSeconds)).ToString(ca)
    End Function

    ' C6b: interpret the CLI-configured [CoolOff] Duration field (plaintext seconds,
    ' MAC-covered) into a duration in seconds. A usable positive value is returned as
    ' is (ComputeCoolOffDeadline then clamps it up to the floor if it is below);
    ' absent/blank/unparseable/non-positive => the floor, so an unset or garbage field
    ' simply yields the default floor wait. Under a valid MAC this value is authentic
    ' (the CLI wrote it at arm and it never changes); a raw edit to shorten it fails
    ' the MAC -> the block freezes, and even absent the freeze the floor clamp in
    ' ComputeCoolOffDeadline means it can only ever EXTEND the wait. Pure + Shared so
    ' it is unit tested (the caller reads the raw [CoolOff] Duration off a MAC-validated
    ' ini and passes it here).
    Friend Shared Function ParseConfiguredCoolOffSeconds(ByVal rawDuration As String, ByVal floorSeconds As Long) As Long
        Dim seconds As Long
        If Long.TryParse(If(rawDuration, "").Trim(), seconds) AndAlso seconds > 0 Then Return seconds
        Return floorSeconds
    End Function

    ' What the per-tick trigger poll should do.
    Friend Enum CoolOffAction
        Start    ' write the service-computed CoolOffUntil, consume the request
        Cancel   ' clear CoolOffUntil (back into the block), consume both triggers
        Ignore   ' no ini write; delete any stale trigger
    End Enum

    ' The pure trigger classifier (the R2 processing matrix + the C4 seam):
    '   * macValid REQUIRED to act: never modify/re-stamp an unverified config
    '     (mirrors the `add` fail-open fix) - a frozen config ignores triggers.
    '   * cancel WINS when both files are present: the safe outcome is "stay
    '     blocked".
    '   * Start only when nothing is pending: CoolOffUntil is IMMUTABLE once set
    '     (except by cancel), so a replayed/re-dropped request can never reset or
    '     extend a running deadline.
    '   * committed (C4, future): a committed block ignores cooling-off requests
    '     (code-only exit). C2b callers pass False until C4 wires the flag.
    ' Pure + Shared so the full matrix is unit tested.
    Friend Shared Function ClassifyCoolOffSignal(ByVal requestPresent As Boolean, ByVal cancelPresent As Boolean, ByVal coolOffPending As Boolean, ByVal committed As Boolean, ByVal macValid As Boolean) As CoolOffAction
        If Not macValid Then Return CoolOffAction.Ignore
        If cancelPresent Then Return CoolOffAction.Cancel
        If requestPresent AndAlso Not committed AndAlso Not coolOffPending Then Return CoolOffAction.Start
        Return CoolOffAction.Ignore
    End Function

    ' C4: interpret the [Commit] Committed flag. "yes" (case-insensitive, trimmed) =
    ' committed; "no"/absent/Nothing/anything else = not committed. The flag is
    ' MAC-covered, so under a valid MAC this value is authentic (the CLI wrote it at
    ' arm and it never changes during the block); flipping it by raw edit fails the
    ' MAC -> macValid=False -> the block FREEZES (cooling-off Ignored regardless of
    ' this value), so this default of "not committed on anything but 'yes'" can never
    ' silently un-commit a genuinely committed block. Pure + Shared so it is unit tested.
    Friend Shared Function IsCommitted(ByVal committedText As String) As Boolean
        Return String.Equals(If(committedText, "").Trim(), "yes", StringComparison.OrdinalIgnoreCase)
    End Function

    ' ===== C3b: partner code (R1 - the FAST service-adjudicated early exit) =====
    '
    ' `monkmode unblock --code <CODE>` drops the ONE content-bearing trigger
    ' (PartnerCodeFileName) carrying the candidate; on its next tick the SERVICE
    ' (the sole verifier + sole stopMe() caller, R1) derives KDF(salt, candidate),
    ' constant-time-compares it to the MAC-covered [Partner] Hash and, on a match,
    ' sets the MAC-covered [Partner] UnlockedAt exit flag - the EXISTING EffectiveExit
    ' machinery (tick/OnStart/guardian) then lifts via the SAME stopMe() natural
    ' expiry and cooling-off use. The CLI has ZERO lift authority: it can only
    ' SUBMIT a candidate; it cannot forge a KDF preimage (PD2xPD3), swap the verifier
    ' (MAC-covered -> freeze, R6), or skip the service-side lift. The trigger's
    ' content is a verified ATTEMPT, never an obeyed command (contrast cooling-off,
    ' whose content is ignored entirely - R2). Polled inside tickLock while
    ' TimeChanging="no", exactly like the cooling-off channel.

    ' What the per-tick partner-code poll should do.
    Friend Enum PartnerCodeAction
        Verify   ' run the KDF/compare; a MATCH sets UnlockedAt, a miss holds the block
        Ignore   ' no verify; delete any stale trigger
    End Enum

    ' The pure partner-code trigger classifier (the R2/R6 processing matrix):
    '   * macValid REQUIRED to even attempt a verify (R6): a frozen/untrusted config
    '     never verifies against a hash it can't trust - it ignores the channel
    '     (mirrors ClassifyCoolOffSignal + the `add` fail-open fix).
    '   * alreadyUnlocked (UnlockedAt already set) => Ignore: the block is ending,
    '     nothing to re-verify (this is also what makes consume-after-persist
    '     crash-safe: a crash between the UnlockedAt write and the trigger delete
    '     re-classifies here as Ignore and just deletes the stale trigger).
    '   * a present trigger with a non-blank candidate => Verify; otherwise Ignore
    '     (no/blank candidate = a no-op that just deletes the stale trigger).
    '   * deliberately does NOT read `committed` (contrast ClassifyCoolOffSignal): a
    '     committed block (C4) keeps the partner code as its ONE intended exit.
    '   * Verify != lift - only a MATCH inside the Verify branch sets UnlockedAt.
    ' Pure + Shared so the full matrix is unit tested.
    Friend Shared Function ClassifyPartnerCodeSignal(ByVal codePresent As Boolean, ByVal candidateNonEmpty As Boolean, ByVal alreadyUnlocked As Boolean, ByVal macValid As Boolean) As PartnerCodeAction
        If Not macValid Then Return PartnerCodeAction.Ignore
        If alreadyUnlocked Then Return PartnerCodeAction.Ignore
        If codePresent AndAlso candidateNonEmpty Then Return PartnerCodeAction.Verify
        Return PartnerCodeAction.Ignore
    End Function

    ' C3b: is the block partner-code-unlocked? PURE: a non-empty [Partner]
    ' UnlockedAt (under the caller's macValid gate) = unlocked; empty/whitespace =
    ' not. Fail-closed: only the SERVICE writes UnlockedAt, and only after verifying
    ' a correct code (ProcessPartnerCodeSignal), and the field is MAC-covered - so a
    ' non-empty UnlockedAt under a valid MAC can only mean a service-verified code.
    ' Byte-for-byte the same as the guardian copy (parity-pinned).
    Friend Shared Function PartnerUnlocked(ByVal unlockedAtText As String) As Boolean
        Return Not String.IsNullOrWhiteSpace(unlockedAtText)
    End Function

    ' The ONE exit decision, shared in semantics by the tick heartbeat (via
    ' ClassifyHeartbeat's Lift arm), OnStart and the guardian's stand-down (its
    ' parity copy) so the three can never drift: the block may end ONLY when the
    ' MAC is valid AND (it genuinely expired OR a pending cooling-off deadline
    ' has been reached OR a partner code has unlocked it), the time-based arms
    ' measured against the monotonic HighWater. The guardian folding cooling-off
    ' AND code-unlock in is LOAD-BEARING: without it, a guardian tick in the
    ' stopMe() gap at the end of a cooling-off OR a code-unlock would read "Until
    ' not passed + macValid => still active" and SCM-resurrect the just-exited
    ' block. Pure + Shared, parity-pinned with the guardian copy.
    '
    ' C5b (SD1): an OPEN scheduled window is a HARD HOLD that out-ranks every exit
    ' reason - while it is open NOTHING lifts the effective block (not expiry, not a
    ' completed cooling-off, not a partner code), until the window reaches its own
    ' monotonic close (ScheduleActive False). The check sits right after the macValid
    ' gate, so it only ever ADDS a hold; ClassifyHeartbeat's mirror arm keeps
    ' Lift <=> EffectiveExit. scheduleActiveUntilText is the decrypted [Schedule]
    ' ActiveUntil ("" = no window, the inert default on every block without a live schedule).
    '
    ' C5b (c2): scheduleArmed is the BETWEEN-windows hold (design §4C) - an armed schedule
    ' (macValid AndAlso the Spec parses to >=1 window) keeps the service AND the guardian
    ' ALIVE between windows, so a recurring schedule enforces tomorrow's window too. A
    ' schedule-only block carries a PAST [Time] Until sentinel (ScheduleOnlyExpiredUntil,
    ' written by the CLI in c3), so between windows BlockHasExpired is True; without this
    ' guard the tick/OnStart/guardian would Lift->stopMe at the first window's close and the
    ' schedule would die (the §3 trap). It sits right after the ScheduleActive hold, so it
    ' only ever ADDS a hold; ClassifyHeartbeat's mirror `If scheduleArmed Then Return
    ' Restamp` keeps Lift <=> EffectiveExit. Terminal teardown (stopMe) is reached only when
    ' the Spec is cleared (scheduleArmed False) AND no window is open AND an exit is due.
    ' scheduleArmed is derived by the CALLER: the service exact (ParseSchedule(Spec).Windows.
    ' Count > 0), the guardian a cheap over-approximation (Spec non-empty) - the difference is
    ' in the caller, so this function stays byte-parity with the guardian copy.
    '
    ' C3b/C5b param order (until, coolOffUntil, unlockedAt, scheduleActiveUntil, highWater,
    ' grace, macValid, scheduleArmed): unlockedAt sits after coolOffUntil (the early-exit
    ' reasons together); scheduleActiveUntil (a HOLD input that needs highWater) sits just
    ' before the highWater/grace time frame; scheduleArmed (the c2 HOLD, no time input of its
    ' own) appends last - the frozen-design order, parity-pinned with the guardian copy.
    Friend Shared Function EffectiveExit(ByVal untilText As String, ByVal coolOffUntilText As String, ByVal unlockedAtText As String, ByVal scheduleActiveUntilText As String, ByVal highWaterText As String, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal scheduleArmed As Boolean) As Boolean
        If Not macValid Then Return False
        ' C5b (SD1): an open scheduled window is a HARD HOLD - nothing lifts while it is open.
        If ScheduleActive(scheduleActiveUntilText, highWaterText) Then Return False
        ' C5b (c2): an armed schedule keeps the service+guardian ALIVE between windows.
        If scheduleArmed Then Return False
        Dim asOf As DateTime = DateTime.MinValue
        Dim parsedHw As DateTime
        If DateTime.TryParse(highWaterText, New CultureInfo("en-CA"), DateTimeStyles.None, parsedHw) Then asOf = parsedHw
        Return BlockHasExpired(untilText, asOf, graceSeconds) OrElse CoolOffElapsedTime(coolOffUntilText, highWaterText) OrElse PartnerUnlocked(unlockedAtText)
    End Function

    ' ===== C5b: schedules (recurring wall-clock windows -> monotonic holds) =====
    '
    ' A schedule is a recurring WALL-CLOCK rule (e.g. Mon-Fri 09:00-17:00), stored as
    ' one MAC-covered plaintext [Schedule] Spec. The design rests on one asymmetry:
    ' WALL-CLOCK decides WHEN a window opens; the monotonic B4 HighWater decides HOW
    ' LONG an opened window enforces. At the first tick a window is open the service
    ' converts it ONCE into a HighWater-anchored deadline [Schedule] ActiveUntil =
    ' HighWater_now + (close - now) - so a mid-window clock-forward can't end it early
    ' (HighWater refuses the jump), exactly like CoolOffUntil. These gates are the
    ' PURE, fail-closed, unit-tested core. NOTE (C5b sub-slice a): the fields are in
    ' the canonical and these gates exist + are tested, but NOTHING here is wired into
    ' the enforcement path yet - the per-tick ProcessScheduleWindows step, the lift/
    ' hold fold into EffectiveExit/ClassifyHeartbeat and the union enforcement behind
    ' BlockHeld are the C5b sub-slice (b) enforcement-core seam. This slice changes NO
    ' enforcement behaviour (the fields read as "" on every existing block).

    ' Has the open scheduled window reached its monotonic close? The sibling of
    ' CoolOffElapsedTime: scheduleActiveUntilText is the decrypted [Schedule] ActiveUntil
    ' ("" = no window open); highWaterText is the trusted B4 mark it is measured against
    ' - NEVER DateTime.Now, so a clock-forward can't reach the close early and a reboot
    ' pauses the countdown. Fail-closed on every axis: empty (no window) and any
    ' unparseable input read as NOT elapsed - a corrupted deadline or mark can only ever
    ' HOLD the window, never end it. Pure + Shared; byte-for-byte the same semantics as
    ' the guardian copy (parity-pinned, like CoolOffElapsedTime).
    Friend Shared Function ScheduleElapsed(ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        If scheduleActiveUntilText = "" Then Return False
        Dim ca As New CultureInfo("en-CA")
        Dim activeUntil As DateTime, highWater As DateTime
        If Not DateTime.TryParse(scheduleActiveUntilText, ca, DateTimeStyles.None, activeUntil) Then Return False
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return False
        Return activeUntil <= highWater
    End Function

    ' Is a scheduled window currently open (set AND not yet elapsed)? SD1: an open
    ' window is a HARD HOLD - while this is True nothing lifts the effective block (not
    ' expiry, not cooling-off, not a code) until the window's own monotonic close.
    ' Empty => no window (not active); a non-empty-but-unparseable deadline => active
    ' (fail-closed: hold, never lift on a garbled deadline). The caller folds macValid
    ' exactly as expiry does. Byte-for-byte the same as the guardian copy (parity-pinned).
    Friend Shared Function ScheduleActive(ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        Return scheduleActiveUntilText <> "" AndAlso Not ScheduleElapsed(scheduleActiveUntilText, highWaterText)
    End Function

    ' C5b (c2): the DERIVED scheduleArmed signal for the SERVICE (design §4C) - the EXACT form:
    ' the config MAC is valid AND the Spec parses to at least one window. Folded (+1 arg) into
    ' ClassifyHeartbeat / EffectiveExit at the tick + OnStart so a schedule-only block (past-Until
    ' sentinel) is held ALIVE between windows and torn down (stopMe) ONLY once the Spec is cleared
    ' (scheduleArmed False). macValid AndAlso is first, so a tampered/frozen config never reads as
    ' armed (its freeze holds via the macValid gate regardless). Pure + Shared so the derivation
    ' ITSELF is unit-tested, not just asserted-by-mirror. The guardian uses a cheaper over-
    ' approximation (Guardian.ScheduleArmed - Spec non-empty) to avoid a 4th ParseSchedule copy;
    ' the difference is fail-safe (the guardian only ever OVER-guards). INERT on every existing
    ' block: no CLI writes a Spec until c3, so specText="" => 0 windows => False.
    Friend Shared Function ScheduleArmed(ByVal macValid As Boolean, ByVal specText As String) As Boolean
        Return macValid AndAlso ParseSchedule(specText).Windows.Count > 0
    End Function

    ' The monotonic end the service (the SOLE writer) persists when a window opens: the
    ' trusted HighWater at open plus the remaining seconds to enforce. The deadline
    ' therefore lives in the HighWater frame - reached only after that much genuine
    ' ON-machine elapsed time - so it can never be clock-skipped. Returns "" when the
    ' stored HighWater doesn't parse (fail-closed: no deadline computable => no write;
    ' retry next tick). The schedule sibling of ComputeCoolOffDeadline. Pure + Shared.
    ' remainingSeconds is produced by EvaluateWindows (always > 0 for an open window); a
    ' non-positive value would yield a deadline <= HighWater (immediately elapsed), but
    ' EvaluateWindows never emits one for an open window.
    Friend Shared Function ComputeScheduleEnd(ByVal highWaterText As String, ByVal remainingSeconds As Long) As String
        Dim ca As New CultureInfo("en-CA")
        Dim highWater As DateTime
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return ""
        Return highWater.AddSeconds(remainingSeconds).ToString(ca)
    End Function

    ' Extend-never-shorten primitive (design §4.1): the LATER of two en-CA schedule-end
    ' strings, treating "" as "no bound" (so later("", e) = e). A newly-opened window's
    ' converted end can only ever PUSH the deadline out, never pull it in - so overlapping
    ' windows resolve to the longest end (SD2). Fail-closed: a non-empty but UNPARSEABLE
    ' accumulator 'a' (a corrupt current ActiveUntil, which ScheduleActive already treats
    ' as a permanent hold) is KEPT - never replaced by a shorter parseable end, which would
    ' turn a fail-closed hold into a liftable window. A "" computed end 'b' (ComputeSchedule-
    ' End failed on an unparseable HighWater) likewise leaves 'a' untouched (retry next
    ' tick). Pure + Shared, unit-tested (never-shorten is a safety invariant). Byte-
    ' preserving: returns one input verbatim, never a reformat, so an unchanged decision is
    ' string-identical to the stored value (no spurious "changed" write).
    Friend Shared Function LaterScheduleEnd(ByVal a As String, ByVal b As String) As String
        If a = "" Then Return b
        If b = "" Then Return a
        Dim ca As New CultureInfo("en-CA")
        Dim da As DateTime, db As DateTime
        If Not DateTime.TryParse(a, ca, DateTimeStyles.None, da) Then Return a   ' keep a fail-closed hold
        If Not DateTime.TryParse(b, ca, DateTimeStyles.None, db) Then Return a   ' can't trust b; keep a
        If db > da Then Return b Else Return a
    End Function

    ' The pure per-tick schedule-state DECISION (design §6.1): given the current [Schedule]
    ' ActiveUntil, the windows the evaluator says are open THIS tick, and the trusted
    ' HighWater, return the ActiveUntil the service should now hold. ProcessScheduleWindows
    ' persists it iff the result differs from current.
    '   * OPEN / EXTEND: fold each open window's converted end (ComputeScheduleEnd =
    '     HighWater + remaining) into ActiveUntil via LaterScheduleEnd - extend-never-
    '     shorten, so a window only pushes the end out (overlap => longest end, SD2), and a
    '     first open (current="") sets it.
    '   * CLEAR: only when the current deadline has reached its monotonic close
    '     (ScheduleElapsed) AND no window is open this tick - so a still-open overlapping
    '     window never blips the deadline closed, and clearing Spec never shortens an open
    '     window (its ActiveUntil runs to close).
    '   * STEADY: otherwise return current unchanged (no write).
    ' Pure + Shared (no DPAPI/filesystem) so the whole open/extend/clear/steady decision is
    ' unit- and e2e-testable through the real gates - exactly as ClassifyCoolOffSignal
    ' relates to ProcessCoolOffSignals. Fail-closed throughout: an unparseable HighWater
    ' yields "" ends (no extend) and ScheduleElapsed=False (no clear) => the window holds
    ' and the tick retries; the extend branch out-ranks clear (an open later window wins).
    Friend Shared Function NextScheduleActiveUntil(ByVal currentActiveUntil As String, ByVal openNow As List(Of ScheduleOpen), ByVal highWaterText As String) As String
        Dim newEnd As String = currentActiveUntil
        If openNow IsNot Nothing Then
            For Each o As ScheduleOpen In openNow
                newEnd = LaterScheduleEnd(newEnd, ComputeScheduleEnd(highWaterText, o.RemainingSeconds))
            Next
        End If
        ' A window opened or extended the deadline: hold the later end.
        If newEnd <> currentActiveUntil Then Return newEnd
        ' The open window reached its monotonic close and nothing is open now: clear it.
        If currentActiveUntil <> "" AndAlso (openNow Is Nothing OrElse openNow.Count = 0) _
           AndAlso ScheduleElapsed(currentActiveUntil, highWaterText) Then Return ""
        ' Steady state: no change (no write).
        Return currentActiveUntil
    End Function

    ' The shared "is the effective block held this tick?" helper (design §6.3). The
    ' block is held when the manual block has NOT effectively expired OR a scheduled
    ' window is open. Defined ONCE so the ~5 self-heal sites (hosts, app-kill, DoH,
    ' SafeBoot, heartbeat guard) can't drift (the way they all share
    ' EffectiveBlockHasExpired today). When macValid=False the first disjunct is already
    ' True (freeze enforces), so the schedule arm only ADDS enforcement when the manual
    ' block has genuinely expired but a window is open. NOTE (C5b sub-slice a): defined
    ' + unit-tested here, WIRED into the self-heal sites in sub-slice (b) - this slice
    ' changes no enforcement behaviour. Pure + Shared.
    Friend Shared Function BlockHeld(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        Return (Not EffectiveBlockHasExpired(untilText, asOf, graceSeconds, macValid)) OrElse (macValid AndAlso ScheduleActive(scheduleActiveUntilText, highWaterText))
    End Function

    ' The effective app-kill set for this tick (design §6.3, the app-kill UNION / SD2). It is
    ' the manual [Process] List, plus - ONLY while a scheduled window is open (scheduleActive) -
    ' the schedule's own apps. Returned as the SAME delimited, .Contains-searchable string the
    ' per-tick kill loop already uses, so a no-schedule tick (scheduleActive=False - every block
    ' today until the CLI writes a Spec in slice (c)) OR a schedule with no apps is BYTE-IDENTICAL
    ' to manualProcessList: the union ADDS enforcement only while a window holds, never removes
    ' any. The "|" separator matches the Spec's own app separator and can't appear in an exe
    ' name, so appending never creates a spurious cross-entry substring match under .Contains.
    ' Caller only parses the Spec (and so only passes a non-empty scheduleApps) when
    ' scheduleActive, so a no-schedule tick does no extra work. Pure + Shared (unit-tested).
    Friend Shared Function EffectiveKillList(ByVal manualProcessList As String, ByVal scheduleApps As List(Of String), ByVal scheduleActive As Boolean) As String
        If Not scheduleActive OrElse scheduleApps Is Nothing OrElse scheduleApps.Count = 0 Then Return manualProcessList
        Dim sb As New System.Text.StringBuilder(manualProcessList)
        For Each app As String In scheduleApps
            sb.Append("|"c)
            sb.Append(app)
        Next
        Return sb.ToString()
    End Function

    ' ---- C5b (b3-ii): the hosts self-heal UNION (design §6.3, the meaty stateful half) ----
    '
    ' While a scheduled window is open the per-tick hosts self-heal repairs the marker block to
    ' the UNION of the manual block's snapshot entries and the schedule's OWN site entries -
    ' synthesised HERE in the CLI's exact hosts-line format, so a schedule-only block (no manual
    ' arm -> no monkmode_hosts.block snapshot) is still enforced and an overlap block over-blocks
    ' the union (SD2). The two synthesisers are a BYTE-FOR-BYTE parity copy of the CLI's
    ' Blocker.NormalizeDomain / BuildHostsEntries (MonkMode and monkmode are separate assemblies
    ' that can't reference one another - the same reason StripMonkModeBlock / ConfigIntegrity /
    ' AtomicHosts are duplicated and parity-pinned); a CLI<->service parity test pins them equal,
    ' so the synthesised block matches what the CLI would write for the same sites - and
    ' stopMe()'s StripMonkModeBlock strips the whole marker block cleanly regardless of contents
    ' (only the marker block is ever touched - the paramount no-data-loss fence).

    ' The MonkMode-owned hosts marker line (the same literal StripMonkModeBlock/stopMe/CLI match).
    Friend Const HostsMarker As String = "#### MonkMode Entries ####"

    ' Parity copy of Blocker.NormalizeDomain: trim + lowercase, strip a pasted scheme/path.
    Private Shared Function NormalizeDomain(ByVal d As String) As String
        d = d.Trim().ToLowerInvariant()
        ' strip scheme and any path if a URL was pasted
        If d.Contains("://") Then d = d.Substring(d.IndexOf("://") + 3)
        Dim slash As Integer = d.IndexOf("/"c)
        If slash >= 0 Then d = d.Substring(0, slash)
        Return d.Trim()
    End Function

    ' Parity copy of Blocker.BuildHostsEntries: one "127.0.0.1 <domain>" line per site (plus a
    ' "127.0.0.1 www.<domain>" line for a bare second-level domain), each CRLF-terminated. Uses
    ' 127.0.0.1 (NOT 0.0.0.0 - Windows' resolver ignores 0.0.0.0 hosts entries). Byte-for-byte
    ' identical to the CLI so the synthesised schedule block matches a manual block's format.
    ' Friend Shared so the CLI<->service parity test (and EffectiveHostsBlock) can call it.
    Friend Shared Function BuildHostsEntries(ByVal domains As IEnumerable(Of String)) As String
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

    ' design §6.3: the effective hosts marker block to repair THIS tick. While a scheduled window
    ' is open (scheduleActive) the target is snapshotBlock UNION the schedule's own synthesised
    ' site entries; otherwise (the inert path - every block today until the CLI writes a Spec in
    ' slice (c)) it is snapshotBlock VERBATIM, so a no-schedule block is BYTE-IDENTICAL to before.
    ' Cases:
    '   - not active / no schedule sites / all sites normalise away -> snapshotBlock verbatim;
    '   - schedule-only (no manual snapshot -> "") -> marker + synthesised entries (the block IS
    '     the schedule's sites - byte-identical to Blocker.BuildMonkModeBlock(sites));
    '   - overlap (a manual snapshot exists) -> the snapshot verbatim, then each schedule entry
    '     LINE not already present appended (dedup line-wise, order-preserving, marker once).
    ' Rides the existing RepairHostsBlock/AtomicHosts machinery (no new writer): the target is
    ' DETERMINISTIC, so once written it is found intact next tick and RepairHostsBlock no-churns;
    ' stopMe()'s StripMonkModeBlock removes the whole marker block at the effective end (NO data
    ' loss - only the marker block is ever touched). Pure + Shared (unit-tested).
    Friend Shared Function EffectiveHostsBlock(ByVal snapshotBlock As String, ByVal scheduleSites As List(Of String), ByVal scheduleActive As Boolean) As String
        If Not scheduleActive OrElse scheduleSites Is Nothing OrElse scheduleSites.Count = 0 Then Return snapshotBlock
        Dim scheduleEntries As String = BuildHostsEntries(scheduleSites)
        If scheduleEntries = "" Then Return snapshotBlock   ' all sites normalised away -> nothing to add
        ' Schedule-only block: the synthesised entries ARE the block (matches the CLI's layout for
        ' the same sites, so the expiry strip removes it exactly as it would a manual block's).
        If String.IsNullOrWhiteSpace(snapshotBlock) Then Return HostsMarker & vbCrLf & scheduleEntries
        ' Overlap: snapshot verbatim + each NEW schedule entry line. A line-set (seeded from the
        ' snapshot's lines) gives a precise, non-substring dedup so a shared site is never
        ' duplicated and the union is stable tick-to-tick.
        Dim present As New HashSet(Of String)(StringComparer.Ordinal)
        For Each ln As String In snapshotBlock.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
            present.Add(ln)
        Next
        Dim sb As New System.Text.StringBuilder(snapshotBlock)
        ' The snapshot ends in a line terminator (BuildMonkModeBlock = marker & CRLF & CRLF-
        ' terminated entries); if a hand-tampered snapshot somehow doesn't, add one so an appended
        ' line can't fuse onto the last.
        If sb.Length > 0 AndAlso Not snapshotBlock.EndsWith(vbCrLf, StringComparison.Ordinal) _
           AndAlso Not snapshotBlock.EndsWith(vbLf, StringComparison.Ordinal) Then sb.Append(vbCrLf)
        For Each ln As String In scheduleEntries.Split(New String() {vbCrLf}, StringSplitOptions.RemoveEmptyEntries)
            If present.Add(ln) Then sb.Append(ln).Append(vbCrLf)   ' Add returns True only for a NEW line
        Next
        Return sb.ToString()
    End Function

    ' ---- C5b (c1): the schedule-only hosts snapshot LIFECYCLE (design C5c §4A) ----
    '
    ' The gap this closes: a MANUAL block gets a MAC-INDEPENDENT on-disk snapshot
    ' (monkmode_hosts.block, written by the CLI at arm - Blocker.vb WriteHostsBlock),
    ' which is what makes its two tamper backstops work: the timer self-heal reads that
    ' snapshot VERBATIM (Service1 timer_Elapsed / EffectiveHostsBlock's inert path) so it
    ' re-asserts hosts even when the config MAC is invalid or [Schedule] ActiveUntil is
    ' forged, and ReassertHostsFailClosed's File.Exists(snapshot) gate lets the crash
    ' backstop re-block. A SCHEDULE-ONLY block (no manual `--for` -> the CLI writes no
    ' snapshot) has NEITHER, so under macValid=False+forged ActiveUntil the self-heal
    ' synthesises "" (P2#1) and a crash re-asserts nothing (P2#2). c1 makes the SERVICE
    ' the snapshot creator/deleter for a schedule-OWNED window, so a schedule-only block
    ' rides the exact same MAC-independent-disk machinery a manual block does.
    '
    ' The one real edge is OWNERSHIP: the service must only ever create/delete a snapshot
    ' the SCHEDULE owns, never touch a MANUAL block's snapshot. The discriminator is a
    ' manual HOLD (design §4A / §9.2): manualHold = Not EffectiveBlockHasExpired(Until,...),
    ' so (a) a manual block still holding (Until future, OR macValid=False = frozen, OR an
    ' unparseable Until) => Leave (never overwrite/delete its snapshot); (b) a schedule-only
    ' block carries the c2 past-`Until` sentinel => EffectiveBlockHasExpired=True => no manual
    ' hold => the schedule owns it. This is fail-closed: EVERY macValid=False case reads as a
    ' manual hold, so a frozen/tampered config keeps its snapshot for the self-heal + crash
    ' backstop and the delete can never fire under tamper. (SD-c1 keeps manual and schedule
    ' mutually exclusive at the CLI in C5b, so the overlap "restore-manual-only" case never
    ' arises from the CLI; Leave keeps the manual snapshot intact if one ever did - the b3-ii
    ' live-hosts union still over-blocks the window, unchanged.)
    Friend Enum ScheduleSnapshotAction
        WriteBlock   ' schedule-owned window open: ensure the snapshot = the synthesised block
        DeleteBlock  ' schedule-owned, window closed / between windows: drop the snapshot
        Leave        ' manual-owned (or nothing to do): never touch the snapshot
    End Enum

    ' The pure snapshot-lifecycle decision (design §4A). scheduleActive = is a window open
    ' this tick; manualHold = is a manual block holding (owns the snapshot); hasScheduleSites =
    ' the open schedule contributes >=1 site to synthesise; snapshotExists = the file is on
    ' disk. Fail-closed: manualHold (which is True for every macValid=False case) always
    ' Leaves, so a frozen/tampered config's snapshot is never deleted. Pure + Shared (the full
    ' matrix is unit-tested; ProcessScheduleSnapshot is the thin file-I/O wrapper around it,
    ' exactly as ClassifyScheduleSnapshot relates to ProcessScheduleSnapshot mirrors
    ' ClassifyCoolOffSignal/ProcessCoolOffSignals).
    Friend Shared Function ClassifyScheduleSnapshot(ByVal scheduleActive As Boolean, ByVal manualHold As Boolean, ByVal hasScheduleSites As Boolean, ByVal snapshotExists As Boolean) As ScheduleSnapshotAction
        ' A manual hold OWNS the snapshot (the CLI wrote it): never overwrite it with the
        ' schedule union, never delete it. Also the fail-closed catch-all (macValid=False =>
        ' EffectiveBlockHasExpired=False => manualHold=True), so a frozen/tampered config keeps
        ' its snapshot for the self-heal + crash backstop.
        If manualHold Then Return ScheduleSnapshotAction.Leave
        ' No manual hold => the schedule owns the snapshot domain.
        If scheduleActive AndAlso hasScheduleSites Then Return ScheduleSnapshotAction.WriteBlock
        ' Window closed / between windows / Spec cleared: drop the schedule-owned snapshot so it
        ' can't self-heal back in and the crash backstop won't re-block an idle schedule.
        If snapshotExists Then Return ScheduleSnapshotAction.DeleteBlock
        Return ScheduleSnapshotAction.Leave
    End Function

    ' The testable file-I/O core of the schedule snapshot lifecycle (design §4A). Given the
    ' snapshot path + this tick's settled state, create/refresh the snapshot to the schedule's
    ' synthesised block while a schedule-owned window is open (so P2#1's self-heal + P2#2's
    ' crash backstop both have a MAC-independent on-disk block), delete it when the schedule-
    ' owned window closes, and never touch a manual-owned snapshot. Idempotent: the WRITE only
    ' fires when the on-disk bytes actually differ (absent file or drift), so an open window
    ' does not churn the snapshot every tick - and the block it writes (EffectiveHostsBlock)
    ' equals what the self-heal synthesises, so once written it is found intact. Best-effort;
    ' NEVER throws (a snapshot-I/O hiccup must never disturb the enforcement tick). Friend
    ' Shared with an explicit snapshotPath so unit tests drive it against temp files, exactly
    ' like ProcessAddToHosts / ReassertHostsFailClosed (fence: unit tests never touch the real
    ' hosts/snapshot). The live tick passes Application.StartupPath\monkmode_hosts.block - the
    ' SAME path the CLI writes, the self-heal reads, and stopMe deletes.
    Friend Shared Sub ProcessScheduleSnapshot(ByVal snapshotPath As String, ByVal scheduleActive As Boolean, ByVal manualHold As Boolean, ByVal scheduleSites As List(Of String))
        Try
            Dim snapshotExists As Boolean = System.IO.File.Exists(snapshotPath)
            Dim hasScheduleSites As Boolean = scheduleActive AndAlso scheduleSites IsNot Nothing AndAlso scheduleSites.Count > 0
            Select Case ClassifyScheduleSnapshot(scheduleActive, manualHold, hasScheduleSites, snapshotExists)
                Case ScheduleSnapshotAction.WriteBlock
                    ' Union with any existing snapshot (idempotent once the schedule entries are
                    ' present; "" for a genuine schedule-only block => the synthesised block IS
                    ' the file, byte-identical to a manual arm's BuildMonkModeBlock for the same
                    ' sites). Only write on a real change, so an open window doesn't re-write the
                    ' file every 10s tick. block can be "" only if every site normalises away
                    ' (EffectiveHostsBlock then returns existing) - guarded so we never create an
                    ' empty/marker-less snapshot.
                    Dim existing As String = If(snapshotExists, System.IO.File.ReadAllText(snapshotPath), "")
                    Dim block As String = EffectiveHostsBlock(existing, scheduleSites, True)
                    If Not String.IsNullOrWhiteSpace(block) AndAlso block <> existing Then
                        System.IO.File.WriteAllText(snapshotPath, block)
                    End If
                Case ScheduleSnapshotAction.DeleteBlock
                    If snapshotExists Then System.IO.File.Delete(snapshotPath)
                    ' ScheduleSnapshotAction.Leave => no-op (a manual-owned snapshot, or nothing on disk).
            End Select
        Catch ex As Exception
        End Try
    End Sub

    ' ---- the wall-clock window evaluator (pure; the schedule's WHEN half) ----
    '
    ' Reference types so the C# unit tests (InternalsVisibleTo) can inspect them as
    ' monkmode.Service1.ParsedSchedule / ScheduleWindow / ScheduleOpen.

    ' One recurring window: a day-of-week mask (bit 0 = Mon .. bit 6 = Sun), an open
    ' minute-of-day and a close minute-of-day, open < close (same-day only, SD3).
    Friend Class ScheduleWindow
        Public DayMask As Integer
        Public OpenMinutes As Integer
        Public CloseMinutes As Integer
    End Class

    ' A parsed [Schedule] Spec: the recurring windows plus the schedule-wide site/app
    ' block lists.
    Friend Class ParsedSchedule
        Public Windows As New List(Of ScheduleWindow)
        Public Sites As New List(Of String)
        Public Apps As New List(Of String)
    End Class

    ' One window the evaluator says should be open this tick, carrying the seconds to
    ' enforce (close-now for a normal/jump-into/boot-inside open; the full window
    ' duration for a live jump-over, SD4). The tick converts each to a HighWater
    ' deadline via ComputeScheduleEnd and takes the LATER (extend-never-shorten).
    Friend Class ScheduleOpen
        Public OpenMinutes As Integer
        Public CloseMinutes As Integer
        Public RemainingSeconds As Long
    End Class

    ' The grammar-version tag the Spec always leads with, so C6 can extend the grammar
    ' (v1 -> v2) without a canonical bump. Pinned by a unit test.
    Friend Const ScheduleSpecGrammarVersion As String = "v1"

    ' C5b (c2): the schedule-only PAST [Time] Until SENTINEL (design §4B). A schedule-only
    ' block has no manual duration, so the CLI (c3) writes THIS fixed, clearly-past,
    ' MAC-covered value as [Time] Until. BlockHasExpired(sentinel) is therefore always True,
    ' so BlockHeld collapses to its ScheduleActive disjunct - the four non-hosts self-heals
    ' track the window (idle between windows) instead of latching forever on an empty Until
    ' (fixes P1). It is a REAL en-CA datetime (parses; hugely past => reads expired) and
    ' STABLE across restamps (unlike an arm-time-now, which would drift on every re-stamp).
    ' The scheduleArmed guard (EffectiveExit / ClassifyHeartbeat) is what stops this past
    ' Until from tearing the block down BETWEEN windows. Written by the CLI in c3; a
    ' CLI<->service parity test will pin the copies equal (like SnapshotName /
    ' CoolOff*FileName). The service itself does not special-case it - BlockHasExpired parses
    ' it generically - so this is just the single source of truth, in the schedule-logic home.
    Friend Const ScheduleOnlyExpiredUntil As String = "1970-01-01 00:00:00"

    ' Parse a [Schedule] Spec (C5a design §3 grammar) into windows + site/app lists.
    ' FAIL-CLOSED: a malformed WINDOW is skipped (keep the good ones); a wholly
    ' unparseable/empty Spec or an unknown grammar tag yields NO windows (the schedule
    ' is inert - a self-authored garbage rule must never INVENT a phantom permanent
    ' block, and it never disturbs the manual block or the MAC). A TAMPERED Spec, by
    ' contrast, fails the MAC upstream -> freeze (B7). Pure; no filesystem/DPAPI.
    '   Spec := "v1" ";" windowList ";" "sites=" siteList ";" "apps=" appList
    '   window := dayMask ":" HHMM "-" HHMM   (dayMask = chars '1'..'7' = Mon..Sun)
    Friend Shared Function ParseSchedule(ByVal specText As String) As ParsedSchedule
        Dim result As New ParsedSchedule()
        If String.IsNullOrWhiteSpace(specText) Then Return result
        Dim parts() As String = specText.Split(";"c)
        ' Need at least the version tag + the window list; an unknown tag is inert.
        If parts.Length < 2 Then Return result
        If parts(0).Trim() <> ScheduleSpecGrammarVersion Then Return result
        ' Windows (comma-separated); skip any malformed one, keep the rest.
        For Each winTok As String In parts(1).Split(","c)
            Dim w As ScheduleWindow = TryParseWindow(winTok)
            If w IsNot Nothing Then result.Windows.Add(w)
        Next
        ' Sites / apps: locate by prefix among the remaining parts (order-tolerant,
        ' either may be absent). "|" separates entries (never valid in a domain/exe).
        For i As Integer = 2 To parts.Length - 1
            Dim p As String = parts(i)
            If p.StartsWith("sites=", StringComparison.Ordinal) Then
                AppendListTokens(result.Sites, p.Substring(6))
            ElseIf p.StartsWith("apps=", StringComparison.Ordinal) Then
                AppendListTokens(result.Apps, p.Substring(5))
            End If
        Next
        Return result
    End Function

    ' Split a "a|b|c" list body on "|", trimming and dropping empties, into dest.
    Private Shared Sub AppendListTokens(ByVal dest As List(Of String), ByVal body As String)
        For Each tok As String In body.Split("|"c)
            Dim t As String = tok.Trim()
            If t <> "" Then dest.Add(t)
        Next
    End Sub

    ' Parse one "dayMask:HHMM-HHMM" window; Nothing if malformed (fail-closed skip).
    ' Enforces SD3: same-day only (open < close); rejects any out-of-range time or day.
    Private Shared Function TryParseWindow(ByVal token As String) As ScheduleWindow
        If token Is Nothing Then Return Nothing
        Dim tok As String = token.Trim()
        If tok = "" Then Return Nothing
        Dim halves() As String = tok.Split(":"c)
        If halves.Length <> 2 Then Return Nothing          ' HHMM carries no colon in the compact grammar
        Dim mask As Integer = TryParseDayMask(halves(0))
        If mask = 0 Then Return Nothing                    ' empty/invalid day set
        Dim times() As String = halves(1).Split("-"c)
        If times.Length <> 2 Then Return Nothing
        Dim openMin As Integer = TryParseHhmm(times(0))
        Dim closeMin As Integer = TryParseHhmm(times(1))
        If openMin < 0 OrElse closeMin < 0 Then Return Nothing
        If openMin >= closeMin Then Return Nothing         ' SD3: reject overnight / zero-length
        Dim w As New ScheduleWindow()
        w.DayMask = mask
        w.OpenMinutes = openMin
        w.CloseMinutes = closeMin
        Return w
    End Function

    ' "12345" -> bitmask (bit 0 = Mon .. bit 6 = Sun). 0 if empty or any char is not
    ' '1'..'7' (fail-closed: an invalid day set makes the whole window malformed).
    Private Shared Function TryParseDayMask(ByVal s As String) As Integer
        If s Is Nothing OrElse s.Length = 0 Then Return 0
        Dim mask As Integer = 0
        For Each ch As Char In s
            If ch < "1"c OrElse ch > "7"c Then Return 0
            mask = mask Or (1 << (AscW(ch) - AscW("1"c)))   ' '1'->bit0(Mon) .. '7'->bit6(Sun)
        Next
        Return mask
    End Function

    ' "0900" -> 540 (minute-of-day). -1 if not exactly 4 digits or out of range
    ' (HH 0..23, MM 0..59). Fail-closed: a bad time makes the window malformed.
    Private Shared Function TryParseHhmm(ByVal s As String) As Integer
        If s Is Nothing OrElse s.Length <> 4 Then Return -1
        For Each ch As Char In s
            If ch < "0"c OrElse ch > "9"c Then Return -1
        Next
        Dim hh As Integer = Integer.Parse(s.Substring(0, 2), CultureInfo.InvariantCulture)
        Dim mm As Integer = Integer.Parse(s.Substring(2, 2), CultureInfo.InvariantCulture)
        If hh > 23 OrElse mm > 59 Then Return -1
        Return hh * 60 + mm
    End Function

    ' Does 'dt' fall on a day the window applies to? (bit 0 = Mon .. bit 6 = Sun.)
    Private Shared Function ScheduleDayMatches(ByVal dt As DateTime, ByVal dayMask As Integer) As Boolean
        Dim bit As Integer = ((CInt(dt.DayOfWeek) + 6) Mod 7)   ' .NET Sun=0..Sat=6 -> Mon=0..Sun=6
        Return (dayMask And (1 << bit)) <> 0
    End Function

    ' The wall-clock evaluator (design §4.2). For each window decide OPEN? and the
    ' seconds to enforce, over the §4.2 matrix:
    '   * INSIDE now (normal / forward-jump-INTO / boot-inside): now in [open, close)
    '     on a matching day -> OPEN, remaining = close - now.
    '   * LIVE jump-OVER (running session only): the wall advanced past a whole window
    '     (crossed its open, now >= its close) AND the advance is a JUMP - the wall
    '     delta vastly exceeds the real monotonic elapsed (wallDelta - monoElapsed >
    '     HighWaterJumpCeilingSeconds) -> OPEN for the FULL window duration (SD4).
    '   * BOOT past a closed window (isBoot, now >= close): MISSED (crux #4b) - a boot
    '     never treats a past-and-closed window as a jump (TickCount64 reset means no
    '     trustworthy monoElapsed), so it only opens a window it lands INSIDE (#4a).
    '   * BEFORE the window: not open.
    ' lastNowText = the previous tick's [CurrentTime] Now; nowText = this tick's now;
    ' monoElapsedSeconds = the real B4 creep-anchor elapsed (wall-clock-immune); isBoot
    ' = OnStart. Fail-closed: an unparseable now opens nothing new this tick (the
    ' existing ScheduleActiveUntil, if any, still holds via ScheduleActive). Pure.
    Friend Shared Function EvaluateWindows(ByVal windows As List(Of ScheduleWindow), ByVal lastNowText As String, ByVal nowText As String, ByVal monoElapsedSeconds As Long, ByVal isBoot As Boolean) As List(Of ScheduleOpen)
        Dim opens As New List(Of ScheduleOpen)
        If windows Is Nothing OrElse windows.Count = 0 Then Return opens
        Dim ca As New CultureInfo("en-CA")
        Dim nowDt As DateTime
        If Not DateTime.TryParse(nowText, ca, DateTimeStyles.None, nowDt) Then Return opens   ' no 'now' -> open nothing new
        Dim lastNowDt As DateTime
        Dim haveLastNow As Boolean = DateTime.TryParse(lastNowText, ca, DateTimeStyles.None, lastNowDt)
        Dim nowSec As Double = nowDt.TimeOfDay.TotalSeconds
        ' A live forward JUMP: the wall advanced far more than the real elapsed. Only a
        ' running session (Not isBoot) with a parseable previous 'now' can detect one.
        Dim wallIsJump As Boolean = False
        If (Not isBoot) AndAlso haveLastNow Then
            Dim wallDelta As Long = CLng(DateDiff(DateInterval.Second, lastNowDt, nowDt))
            wallIsJump = (wallDelta - monoElapsedSeconds) > HighWaterJumpCeilingSeconds
        End If
        For Each w As ScheduleWindow In windows
            Dim openSec As Double = w.OpenMinutes * 60.0
            Dim closeSec As Double = w.CloseMinutes * 60.0
            If ScheduleDayMatches(nowDt, w.DayMask) AndAlso nowSec >= openSec AndAlso nowSec < closeSec Then
                ' INSIDE now (covers normal ticks, forward-jump-INTO, boot-inside).
                opens.Add(NewScheduleOpen(w, CLng(Math.Ceiling(closeSec - nowSec))))
            ElseIf wallIsJump AndAlso ScheduleJumpedOver(w, lastNowDt, nowDt) Then
                ' LIVE jump-OVER a whole window: enforce its FULL duration (SD4).
                opens.Add(NewScheduleOpen(w, (w.CloseMinutes - w.OpenMinutes) * 60L))
            End If
        Next
        Return opens
    End Function

    Private Shared Function NewScheduleOpen(ByVal w As ScheduleWindow, ByVal remainingSeconds As Long) As ScheduleOpen
        Dim o As New ScheduleOpen()
        o.OpenMinutes = w.OpenMinutes
        o.CloseMinutes = w.CloseMinutes
        o.RemainingSeconds = remainingSeconds
        Return o
    End Function

    ' Did the wall traversal (lastNow, now] leap over a whole instance of window w -
    ' i.e. is there a matching day whose open-instant is in (lastNow, now] and whose
    ' same-day close is at/before now? Bounded backward scan from now.Date (day-of-week
    ' repeats weekly, so a matching day is always within ~7 days of now; the 366 cap
    ' just bounds a pathological multi-year jump). Only called when wallIsJump is
    ' already established, so this is existence-only; the enforced duration is always
    ' the full window length regardless of which day was skipped.
    Private Shared Function ScheduleJumpedOver(ByVal w As ScheduleWindow, ByVal lastNowDt As DateTime, ByVal nowDt As DateTime) As Boolean
        Dim d As DateTime = nowDt.Date
        Dim guard As Integer = 0
        While d >= lastNowDt.Date AndAlso guard <= 366
            If ScheduleDayMatches(d, w.DayMask) Then
                Dim openInstant As DateTime = d.AddMinutes(w.OpenMinutes)
                Dim closeInstant As DateTime = d.AddMinutes(w.CloseMinutes)
                If openInstant > lastNowDt AndAlso openInstant <= nowDt AndAlso nowDt >= closeInstant Then
                    Return True
                End If
            End If
            d = d.AddDays(-1)
            guard += 1
        End While
        Return False
    End Function

    ' ---- B4: monotonic high-water mark (clock-rollback hardening) ----
    '
    ' Expiry must NOT trust raw DateTime.Now: rolling the clock forward past
    ' [Time] Until would make the next tick call stopMe() and lift the block
    ' early (and stand the guardian down too). B4 decides expiry off a HIGH-WATER
    ' MARK ([Time] HighWater) that only ever advances at the real tick rate and
    ' never by a jump - so a forward clock jump can't carry it past Until. The
    ' service is the SOLE writer of HighWater (no write race); the guardian only
    ' reads it. These gates are the pure, unit-tested core; the live wiring (read
    ' the ini, persist the new value, re-stamp the MAC) is the per-tick seam.
    '
    ' DELIBERATE SEMANTIC: because HighWater only advances while the service is
    ' running, genuine machine-OFF downtime is NOT credited toward expiry - the
    ' block extends by the downtime. That is the fail-closed cost of defeating
    ' clock-forward (the block measures real ON-machine elapsed time). At OnStart
    ' the boot gap is a big delta => ForwardJump => not advanced => not credited.
    ' Intended. A spring-DST forward shift is likewise a >ceiling jump => refused
    ' => the only cost is a rare ~1h block-extension, the safe direction (LOCAL
    ' time on purpose, so a DST/timezone forward shift never lifts a block early).

    ' The classifier results for ClassifyTimeAdvance. Plain Integer (not an Enum)
    ' so the service-copy and guardian-copy results compare directly in the
    ' parity tests across the two assemblies. Untrusted folds into ForwardJump:
    ' both mean "do not credit this advance", so the caller needs only the one
    ' "don't advance" branch.
    Friend Const TimeAdvanceBackward As Integer = -1     ' delta < 0 (clock rolled back)
    Friend Const TimeAdvanceTrusted As Integer = 0       ' 0 <= delta <= ceiling (a real tick)
    Friend Const TimeAdvanceForwardJump As Integer = 1   ' delta > ceiling, OR unparseable stored (fail closed)

    ' The largest forward advance, in seconds, that still counts as a TRUSTED
    ' tick. Must be >> the 10s TimerIntervalMs so an ordinary (possibly slightly
    ' late) tick is always Trusted, while a deliberate clock-forward of minutes/
    ' hours is a ForwardJump. Pinned by a unit test (like the recovery-policy and
    ' SafeBoot consts) - a retune that dropped it near the tick interval would
    ' start refusing legitimate ticks (block never advances), and one that raised
    ' it huge would let a clock-forward of up to that many seconds through.
    Friend Const HighWaterJumpCeilingSeconds As Long = 120

    ' Classify how 'now' compares to the stored high-water mark. storedHwText and
    ' nowText are en-CA LOCAL datetime strings (same format/parse as [Time]
    ' Until, via DateDiff over the en-CA parse). Returns Backward (delta < 0),
    ' Trusted (0 <= delta <= ceilingSeconds) or ForwardJump (delta > ceiling).
    ' Fail-closed: an unparseable storedHw or nowText is ForwardJump (never
    ' credit an advance we can't measure). Pure and Shared so it is unit tested.
    Friend Shared Function ClassifyTimeAdvance(ByVal storedHwText As String, ByVal nowText As String, ByVal ceilingSeconds As Long) As Integer
        Dim storedHw As DateTime, nowDt As DateTime
        If Not DateTime.TryParse(storedHwText, New CultureInfo("en-CA"), DateTimeStyles.None, storedHw) Then
            Return TimeAdvanceForwardJump
        End If
        If Not DateTime.TryParse(nowText, New CultureInfo("en-CA"), DateTimeStyles.None, nowDt) Then
            Return TimeAdvanceForwardJump
        End If
        Dim delta As Long = DateDiff(DateInterval.Second, storedHw, nowDt)
        If delta < 0 Then Return TimeAdvanceBackward
        If delta > ceilingSeconds Then Return TimeAdvanceForwardJump
        Return TimeAdvanceTrusted
    End Function

    ' The next [Time] HighWater string to persist. MONOTONIC: it advances to the
    ' 'now' string ONLY when the advance is Trusted (a real tick); a backward
    ' roll or a forward jump leaves it UNCHANGED (returns storedHwText), so a
    ' clock jump can never move it past Until. On an unparseable storedHw it is
    ' ForwardJump (not Trusted), so storedHwText is returned unchanged - keeping
    ' the value and the MAC coupled (a tampered HighWater already fails the MAC,
    ' so the block stands regardless; we deliberately do NOT re-seed to now here).
    ' Pure and Shared so it is unit tested.
    Friend Shared Function NextHighWater(ByVal storedHwText As String, ByVal nowText As String, ByVal ceilingSeconds As Long) As String
        If ClassifyTimeAdvance(storedHwText, nowText, ceilingSeconds) = TimeAdvanceTrusted Then
            Return nowText
        End If
        Return storedHwText
    End Function

    ' B4 CREEP FIX. NextHighWater alone only caps each STEP at the 120s ceiling, not
    ' the RATE: an attacker who nudges the wall clock +119s right before each 10s
    ' real tick gets a Trusted advance every tick and walks the mark ~12x faster
    ' than honest time, lifting a block early. This bounds the advance from
    ' storedHw -> candidateHw to the REAL elapsed time (monoElapsedSeconds, from
    ' Environment.TickCount64, which the wall clock can't move): credit the SMALLER
    ' of the wall advance and the monotonic elapsed. So a +119s wall step with only
    ' ~10s of real time credits ~10s; an honest +10s/10s step credits the full 10s.
    ' Pure + Shared so the creep regression test can pin it (the test the audit
    ' said was missing). Fail-safe: unparseable stored/candidate, or a non-positive
    ' or already-within-budget advance, returns candidateHwText unchanged.
    Friend Shared Function CapHighWaterAdvance(ByVal storedHwText As String, ByVal candidateHwText As String, ByVal monoElapsedSeconds As Long) As String
        Dim ca As New CultureInfo("en-CA")
        Dim storedHw As DateTime, candidateHw As DateTime
        If Not DateTime.TryParse(storedHwText, ca, DateTimeStyles.None, storedHw) Then Return candidateHwText
        If Not DateTime.TryParse(candidateHwText, ca, DateTimeStyles.None, candidateHw) Then Return candidateHwText
        Dim advance As Long = DateDiff(DateInterval.Second, storedHw, candidateHw)
        Dim budget As Long = If(monoElapsedSeconds < 0, 0L, monoElapsedSeconds)
        ' No forward advance (jump/backward already kept stored), or the advance is
        ' within the real elapsed budget: keep the candidate as-is.
        If advance <= 0 OrElse advance <= budget Then Return candidateHwText
        ' Otherwise the wall ran ahead of real time (creep): credit only the budget.
        Return storedHw.AddSeconds(budget).ToString(ca)
    End Function

    ' B1 backward-clock fix. The next [Time] HighWater to persist, advancing on the
    ' REAL monotonic elapsed (monoElapsedSeconds, from Environment.TickCount64, which
    ' the wall clock cannot move) regardless of wall-clock DIRECTION. A Trusted tick
    ' behaves EXACTLY as before - it reuses NextHighWater + CapHighWaterAdvance
    ' verbatim, so the honest path is provably byte-identical (min(wallDelta, mono),
    ' never past the honest wall 'now'). The change is the Else branch: on a BACKWARD
    ' roll (DST fall-back / NTP / manual) or a FORWARD jump - where the wall gives no
    ' trustworthy delta - the old composition FROZE the mark (credited 0), so an
    ' active block over-ran by the rollback amount until the wall climbed back (fail-
    ' CLOSED - it over-blocks, never lifts early - but a real correctness deviation,
    ' the Codex residual P2). Here we instead credit the real monotonic elapsed, so
    ' the mark keeps climbing at the real ~10s/tick rate and the block ends at its
    ' REAL duration. The safety invariant is preserved exactly: per-tick credit <=
    ' real monotonic elapsed this tick (Trusted: min(wallDelta, budget) <= budget <=
    ' mono; non-Trusted: budget = min(mono, ceiling) <= mono) => cumulative advance <=
    ' real-elapsed-since-arm => the block can NEVER lift before its real duration. We
    ' only raise the non-Trusted credit from 0 up to <= mono, never above it.
    ' Pure + Shared so B1b's regressions pin it (like its three sibling functions).
    Friend Shared Function AdvanceHighWater(ByVal storedHwText As String, ByVal wallNowText As String, ByVal monoElapsedSeconds As Long, ByVal ceilingSeconds As Long) As String
        Dim ca As New CultureInfo("en-CA")
        Dim storedHw As DateTime
        ' Fail-safe: an unparseable/tampered stored mark stays UNCHANGED - coupled to
        ' the already-failing MAC, so newHwAsOf -> MinValue -> the block holds. We
        ' never fabricate a fresh, MAC-shaped value here (mirrors NextHighWater /
        ' CapHighWaterAdvance).
        If Not DateTime.TryParse(storedHwText, ca, DateTimeStyles.None, storedHw) Then Return storedHwText

        ' The real time we credit THIS tick, clamped [0, ceiling]. The ceiling clamp
        ' (D1) makes the fix robust to whatever TickCount64 does across sleep/hibernate:
        ' a single resume tick credits at most one ceiling - never an unbounded jump,
        ' never a lift. A non-positive delta credits nothing.
        Dim budget As Long = monoElapsedSeconds
        If budget < 0 Then budget = 0
        If budget > ceilingSeconds Then budget = ceilingSeconds

        Select Case ClassifyTimeAdvance(storedHwText, wallNowText, ceilingSeconds)
            Case TimeAdvanceTrusted
                ' Honest wall: keep today's rule EXACTLY (reuse the shipped helpers).
                Return CapHighWaterAdvance(storedHwText, NextHighWater(storedHwText, wallNowText, ceilingSeconds), budget)
            Case Else   ' TimeAdvanceBackward or TimeAdvanceForwardJump
                ' B1 FIX: wall untrustworthy -> credit the REAL monotonic elapsed,
                ' don't freeze. budget <= ceiling <= any realistic value, so no overflow.
                Return storedHw.AddSeconds(budget).ToString(ca)
        End Select
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

    ' B1 watchdog gate (mutual-restart pair, layer 2). Decides whether the timer
    ' should (re)launch the guardian peer this tick. Fail-SAFE in the same spirit
    ' as the B2 repair gate:
    '   - only while the block is still active (blockActive is the caller's
    '     Not BlockHasExpired(...), so an unparseable Until fails CLOSED -> active
    '     -> the guardian is kept alive, never dropped, until the block truly
    '     ends and stopMe() tears everything down);
    '   - only when the peer exe actually exists (nothing to start otherwise);
    '   - only when no instance is already running, so a slow guardian start can
    '     never spawn a storm of duplicates (mirrors the old twin's
    '     processList.Length <= 0 guard, made explicit and testable).
    ' Pure, Shared and process/SCM-free so it can be unit tested; the live timer
    ' wiring (GetProcessesByName + Process.Start of the guardian) is verified by
    ' the manual elevated smoke test, exactly like the B2 repair wiring.
    Friend Shared Function ShouldRestartPeer(ByVal peerInstanceCount As Integer, ByVal blockActive As Boolean, ByVal peerExeExists As Boolean) As Boolean
        If Not blockActive Then Return False
        If Not peerExeExists Then Return False
        Return peerInstanceCount <= 0
    End Function

    ' B3 SafeBoot gate. The (Default) value under each SafeBoot subkey must be the
    ' ordinal string "Service" for the registration to read as intact; anything
    ' else (missing, blank, a case-variant, a tampered tag) needs a rewrite. Pure
    ' and Shared so the re-assert's write-vs-skip decision is unit tested; the
    ' live registry I/O (CreateSubKey/SetValue/DeleteSubKeyTree) is smoke-tested,
    ' exactly like the B1/B2 live wiring.
    Friend Shared Function SafeBootValueIsCorrect(ByVal currentValue As String) As Boolean
        Return String.Equals(currentValue, SafeBootValue, StringComparison.Ordinal)
    End Function

    ' B3 SafeBoot self-heal (live wiring). Ensure both SafeBoot subkeys exist with
    ' the "Service" tag so the service starts in Safe Mode / Safe Mode w/ Network.
    ' What SafeBoot keys off is the subkey's PRESENCE; the (Default) value is the
    ' conventional tag. Read-only probe FIRST so an already-correct registration
    ' is a true no-op (no writable handle opened, no churn - mirrors
    ' RepairHostsBlock returning Nothing on an intact block); only a missing key
    ' (probe returns Nothing) or a tampered tag triggers CreateSubKey + SetValue.
    ' Per-key Try (like RemoveSafeBootRegistration) so a hiccup on one key still
    ' attempts the other; best-effort throughout - a registry failure must never
    ' crash the enforcement tick.
    Private Sub AssertSafeBootRegistration()
        For Each subKey As String In New String() {SafeBootMinimalKey, SafeBootNetworkKey}
            Try
                Dim current As String = Nothing
                Using rk As RegistryKey = Registry.LocalMachine.OpenSubKey(subKey)
                    If rk IsNot Nothing Then current = TryCast(rk.GetValue(String.Empty, ""), String)
                End Using
                If Not SafeBootValueIsCorrect(current) Then
                    Using rk As RegistryKey = Registry.LocalMachine.CreateSubKey(subKey)
                        If rk IsNot Nothing Then rk.SetValue(String.Empty, SafeBootValue, RegistryValueKind.String)
                    End Using
                End If
            Catch ex As Exception
            End Try
        Next
    End Sub

    ' Remove both SafeBoot subkeys (B3 teardown at a genuine expiry). Best-effort
    ' and no throw if already absent; the two deletes are independent so a failure
    ' on one still attempts the other. A clean expiry must leave no SafeBoot
    ' registration behind for a service that is about to stop.
    Private Sub RemoveSafeBootRegistration()
        Try
            Registry.LocalMachine.DeleteSubKeyTree(SafeBootMinimalKey, False)
        Catch ex As Exception
        End Try
        Try
            Registry.LocalMachine.DeleteSubKeyTree(SafeBootNetworkKey, False)
        Catch ex As Exception
        End Try
    End Sub

    ' ===== B5a: browser DoH-off policy (self-heal + no-data-loss restore) =====
    ' Mirrors the B3 SafeBoot pair (AssertSafeBootRegistration / RemoveSafeBoot-
    ' Registration) but over the heterogeneous browser Secure-DNS policy values in
    ' DohPolicy.Entries, and with a SNAPSHOT-AWARE restore (B3 blind-deleted its own
    ' dedicated leaf key; these are SHARED vendor keys that may hold the user's own
    ' policies, so teardown must restore the user's prior value at the VALUE level -
    ' no data loss). The pure decisions (ValueIsBlocked / RestoreActionFor / snapshot
    ' parse) live in DohPolicy.vb and are unit-tested; the live registry/file I/O
    ' here is the smoke-tested seam, exactly like the B3 live wiring.

    ' Read one policy value (String for REG_SZ, boxed Int32 for a DWORD, or Nothing
    ' if the value/key is absent). Read-only OpenSubKey.
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

    ' Delete ONLY our value (never the subkey tree - the vendor key may hold the
    ' user's own policies). No-op if the value/key is already absent.
    Private Sub DeleteDohValue(ByVal entry As DohPolicy.DohPolicyEntry)
        Using rk As RegistryKey = Registry.LocalMachine.OpenSubKey(entry.SubKey, True)
            If rk IsNot Nothing Then rk.DeleteValue(entry.ValueName, False)
        End Using
    End Sub

    ' B5a self-heal (live wiring). Force every browser Secure-DNS policy value to
    ' its blocked setting so the block can't be bypassed by tunnelling DNS over
    ' HTTPS. Read-only probe FIRST so an already-blocked value is a true no-op
    ' (mirrors the B3 SafeBoot probe / RepairHostsBlock returning Nothing on an
    ' intact block); only an absent or changed value triggers a write. Per-entry Try
    ' (like AssertSafeBootRegistration) so a hiccup on one hive still attempts the
    ' rest; best-effort - a registry failure must never crash the enforcement tick.
    Private Sub AssertDohPolicy()
        For Each entry As DohPolicy.DohPolicyEntry In DohPolicy.Entries
            Try
                If Not DohPolicy.ValueIsBlocked(entry, ReadDohValue(entry)) Then
                    SetDohValue(entry, entry.BlockedValue)
                End If
            Catch ex As Exception
            End Try
        Next
    End Sub

    ' B5a teardown at a genuine expiry (the CLI escape hatch has its own copy).
    ' Restore each policy value to the user's PRIOR state from the snapshot the CLI
    ' persisted at block start (monkmode_doh.snapshot, next to the exes): restore the
    ' prior value, or delete our value where it was ABSENT before. NO DATA LOSS - the
    ' snapshot is authoritative for the pre-block state (an all-absent snapshot
    ' correctly deletes our values). The snapshot is then CONSUMED (like stopMe
    ' deleting the hosts snapshot) so a later restart into the still-expired block
    ' can't re-restore a now-stale prior. When there is NO snapshot (the write failed
    ' at block start, or a prior teardown already consumed it) we DO NOTHING: with no
    ' authoritative record that WE created the current value, deleting it could clobber
    ' the user's own value (e.g. a security-conscious user who already had DoH off, or
    ' an already-restored prior after a first teardown) - the paramount no-data-loss
    ' fence. The only cost is a rare lingering "off" if the snapshot write failed -
    ' fail-safe (leaves enforcement, never deletes a value we can't prove is ours).
    ' Per-entry Try; best-effort.
    Private Sub RemoveDohPolicy()
        Dim snapshotPath As String = Application.StartupPath + "\monkmode_doh.snapshot"
        Dim haveSnapshot As Boolean = False
        Dim parsed As Object() = Nothing
        Try
            If My.Computer.FileSystem.FileExists(snapshotPath) Then
                parsed = DohPolicy.ParseSnapshot(My.Computer.FileSystem.ReadAllText(snapshotPath))
                haveSnapshot = True
            End If
        Catch ex As Exception
            haveSnapshot = False
        End Try

        ' No authoritative snapshot => do nothing (see the header: never delete a
        ' value we cannot prove we created).
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
            Catch ex As Exception
            End Try
        Next

        ' Consume the snapshot so a later restart into the expired block takes the
        ' safe do-nothing path above instead of re-restoring a now-stale prior.
        Try
            System.IO.File.Delete(snapshotPath)
        Catch ex As Exception
        End Try
    End Sub

    ' B7: build the canonical (decrypted plaintext, fixed field order) the MAC is
    ' computed over, from a loaded ini. Byte-identical construction to the CLI's
    ' Blocker.CanonicalFromIni and the notifier/guardian readers - all parties
    ' must derive the same input or the MAC would never agree. [Integrity]
    ' Key/Mac are excluded (you can't MAC the MAC). Friend (not Private) so the
    ' end-to-end parity tests can prove this reader derives a byte-identical
    ' canonical to the CLI writer and the other readers - the tautological
    ' BuildCanonical literal comparison can't catch a drift in THIS wrapper (e.g.
    ' if someone started decrypting CustomSites or stopped decrypting ProcessList).
    Friend Function CanonicalFromIni(ByVal iniFile As IniFile) As String
        Dim untilEnc As String = iniFile.GetKeyValue("Time", "Until")
        Dim highWaterEnc As String = iniFile.GetKeyValue("Time", "HighWater")
        Dim coolOffEnc As String = iniFile.GetKeyValue("Time", "CoolOffUntil")
        Dim procEnc As String = iniFile.GetKeyValue("Process", "List")
        Dim nowEnc As String = iniFile.GetKeyValue("CurrentTime", "Now")
        Dim sites As String = iniFile.GetKeyValue("User", "CustomSites")
        ' C3b: the [Partner] fields are stored PLAINTEXT (as-stored, like CustomSites -
        ' NOT decrypted like the datetimes); absent => "" (a v4 config read under v5
        ' code therefore builds a different canonical and freezes, R9). MAC-covered.
        Dim partnerSalt As String = iniFile.GetKeyValue("Partner", "Salt")
        Dim partnerHash As String = iniFile.GetKeyValue("Partner", "Hash")
        Dim partnerUnlockedAt As String = iniFile.GetKeyValue("Partner", "UnlockedAt")
        ' C4: the [Commit] Committed flag ("yes"/"no", plaintext-as-stored, MAC-covered).
        Dim committed As String = iniFile.GetKeyValue("Commit", "Committed")
        ' C5b: [Schedule] Spec is the recurring-window rule stored PLAINTEXT (as-stored,
        ' like CustomSites/[Partner] - NOT decrypted); [Schedule] ActiveUntil is an
        ' ENCRYPTED datetime like CoolOffUntil ("" = no window open). Absent => "" (a v6
        ' config read under v7 code builds a different canonical and freezes, R9).
        Dim scheduleSpec As String = iniFile.GetKeyValue("Schedule", "Spec")
        Dim scheduleActiveEnc As String = iniFile.GetKeyValue("Schedule", "ActiveUntil")
        ' C6b: the [CoolOff] Duration configured cooling-off wait in seconds, stored
        ' PLAINTEXT (as-stored, like Committed - NOT decrypted); absent => "" (a v7 config
        ' read under v8 code builds a different canonical and freezes, R9). MAC-covered.
        Dim coolOffDuration As String = iniFile.GetKeyValue("CoolOff", "Duration")

        Dim untilPlain As String = If(untilEnc = "", "", encryptionW.DecryptData(untilEnc))
        Dim highWaterPlain As String = If(highWaterEnc = "", "", encryptionW.DecryptData(highWaterEnc))
        ' C2b: CoolOffUntil is an encrypted datetime like Until/HighWater; absent/
        ' empty ("" - no cooling-off pending) passes through verbatim.
        Dim coolOffPlain As String = If(coolOffEnc = "", "", encryptionW.DecryptData(coolOffEnc))
        Dim procPlain As String = If(procEnc = "" OrElse procEnc = "null", procEnc, encryptionW.DecryptData(procEnc))
        Dim nowPlain As String = If(nowEnc = "", "", encryptionW.DecryptData(nowEnc))
        ' C5b: ScheduleActiveUntil decrypts exactly like CoolOffUntil ("" = no window open).
        Dim scheduleActivePlain As String = If(scheduleActiveEnc = "", "", encryptionW.DecryptData(scheduleActiveEnc))

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion, untilPlain, procPlain, sites, nowPlain, highWaterPlain, coolOffPlain, partnerSalt, partnerHash, partnerUnlockedAt, committed, scheduleSpec, scheduleActivePlain, coolOffDuration)
    End Function

    ' B7 live MAC gate (the DPAPI seam - smoke-tested, not unit-tested). Reads
    ' [Integrity] Key, DPAPI-unprotects it at machine scope, and validates
    ' [Integrity] Mac against the canonical. Returns False (MAC INVALID) on ANY
    ' failure - missing/blank/non-Base64 key or MAC, a DPAPI denial, a blob from
    ' another machine, a crypto error - so a tamper or a DPAPI hiccup reads as
    ' "keep the block standing" via EffectiveBlockHasExpired, NEVER as "lift".
    ' Best-effort and never throws (the caller is inside the per-tick Try too).
    Private Function ConfigMacIsValidForIni(ByVal iniFile As IniFile) As Boolean
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(iniFile.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return False
            Return ConfigIntegrity.ConfigMacIsValid(CanonicalFromIni(iniFile), iniFile.GetKeyValue(IntegritySection, IntegrityMacName), key)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' B7: re-stamp [Integrity] Mac over the current canonical using the already
    ' stored [Integrity] Key (DPAPI-unprotected) - used after the heartbeat
    ' rewrites [CurrentTime] Now (a MAC-covered field) so normal operation does
    ' not trip the MAC. No-op if no recoverable key; never throws. The service
    ' does NOT mint a new key (it never arms a block; the CLI owns that).
    Private Sub RestampMacWithExistingKey(ByVal iniFile As IniFile)
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(iniFile.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return
            iniFile.SetKeyValue(IntegritySection, IntegrityMacName, ConfigIntegrity.ComputeConfigMac(CanonicalFromIni(iniFile), key))
        Catch ex As Exception
        End Try
    End Sub

    ' ===== B6: deny-DELETE on the service object (sc-delete resistance) =====
    '
    ' The service is the SOLE per-tick re-asserter of the deny-DELETE ACE (the
    ' CLI's escape hatch is the only remover besides stopMe()). BRICK-SAFE by
    ' construction (see ServiceSecurity.vb): we deny the DELETE right (SD) only,
    ' to Built-in Administrators (BA). The service runs as LocalSystem (SY, not a
    ' BA member) so the deny does not even apply to it, and it opens the service
    ' with READ_CONTROL | WRITE_DAC (we NEVER deny WRITE_DAC) - so this re-assert
    ' and the stopMe() re-grant can ALWAYS rewrite the DACL. There is no path
    ' here that leaves the service un-removable.

    Private Const SC_MANAGER_ALL_ACCESS As UInteger = &H3FUI
    Private Const READ_CONTROL As UInteger = &H20000UI
    Private Const WRITE_DAC As UInteger = &H40000UI
    Private Const DACL_SECURITY_INFORMATION As UInteger = &H4UI
    Private Const SDDL_REVISION_1 As UInteger = 1UI

    <DllImport("advapi32.dll", EntryPoint:="OpenSCManagerW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function OpenSCManager(ByVal machineName As String, ByVal databaseName As String, ByVal dwAccess As UInteger) As IntPtr
    End Function

    <DllImport("advapi32.dll", EntryPoint:="OpenServiceW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function OpenService(ByVal hSCManager As IntPtr, ByVal serviceName As String, ByVal dwDesiredAccess As UInteger) As IntPtr
    End Function

    <DllImport("advapi32.dll", SetLastError:=True)>
    Private Shared Function CloseServiceHandle(ByVal hSCObject As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True)>
    Private Shared Function QueryServiceObjectSecurity(ByVal hService As IntPtr, ByVal dwSecurityInformation As UInteger,
        ByVal lpSecurityDescriptor As IntPtr, ByVal cbBufSize As UInteger, ByRef pcbBytesNeeded As UInteger) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True)>
    Private Shared Function SetServiceObjectSecurity(ByVal hService As IntPtr, ByVal dwSecurityInformation As UInteger,
        ByVal lpSecurityDescriptor As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="ConvertSecurityDescriptorToStringSecurityDescriptorW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function ConvertSecurityDescriptorToStringSecurityDescriptor(ByVal SecurityDescriptor As IntPtr,
        ByVal RequestedStringSDRevision As UInteger, ByVal SecurityInformation As UInteger,
        ByRef StringSecurityDescriptor As IntPtr, ByRef StringSecurityDescriptorLen As UInteger) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function ConvertStringSecurityDescriptorToSecurityDescriptor(ByVal StringSecurityDescriptor As String,
        ByVal StringSDRevision As UInteger, ByRef SecurityDescriptor As IntPtr, ByRef SecurityDescriptorSize As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function LocalFree(ByVal hMem As IntPtr) As IntPtr
    End Function

    ' Read the service's DACL as an SDDL string, or Nothing on any failure.
    ' Two-phase QueryServiceObjectSecurity (size probe, then fetch).
    Private Shared Function ReadServiceDaclSddl(ByVal svc As IntPtr) As String
        Dim needed As UInteger = 0UI
        QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, IntPtr.Zero, 0UI, needed)
        If needed = 0UI Then Return Nothing
        Dim sdBuf As IntPtr = Marshal.AllocHGlobal(CInt(needed))
        Try
            Dim got As UInteger = 0UI
            If Not QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sdBuf, needed, got) Then
                Return Nothing
            End If
            Dim strSd As IntPtr = IntPtr.Zero
            Dim strLen As UInteger = 0UI
            If Not ConvertSecurityDescriptorToStringSecurityDescriptor(sdBuf, SDDL_REVISION_1, DACL_SECURITY_INFORMATION, strSd, strLen) Then
                Return Nothing
            End If
            Try
                Return Marshal.PtrToStringUni(strSd)
            Finally
                If strSd <> IntPtr.Zero Then LocalFree(strSd)
            End Try
        Finally
            Marshal.FreeHGlobal(sdBuf)
        End Try
    End Function

    ' Convert an SDDL string back to a binary SD and write it as the service's
    ' DACL. Returns True on success.
    Private Shared Function WriteServiceDaclSddl(ByVal svc As IntPtr, ByVal sddl As String) As Boolean
        If String.IsNullOrEmpty(sddl) Then Return False
        Dim sd As IntPtr = IntPtr.Zero
        Dim sdSize As UInteger = 0UI
        If Not ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SDDL_REVISION_1, sd, sdSize) Then
            Return False
        End If
        Try
            Return SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd)
        Finally
            If sd <> IntPtr.Zero Then LocalFree(sd)
        End Try
    End Function

    ' B6 re-assert: ensure the MONKMODE service object carries the deny-DELETE
    ' ACE, so `sc delete MONKMODE` is refused while a block is active. Read-only
    ' probe FIRST so an already-denied DACL is a true no-op (no churn, like the
    ' B3 SafeBoot probe). Best-effort throughout - a registry/SCM hiccup must
    ' never crash the enforcement tick; the caller wraps this in Try too.
    Private Sub AssertDenyDeleteAce()
        Dim scm As IntPtr = IntPtr.Zero
        Dim svc As IntPtr = IntPtr.Zero
        Try
            scm = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then Return
            svc = OpenService(scm, "MONKMODE", READ_CONTROL Or WRITE_DAC)
            If svc = IntPtr.Zero Then Return
            Dim sddl As String = ReadServiceDaclSddl(svc)
            If sddl Is Nothing Then Return
            If ServiceSecurity.SddlHasDenyDelete(sddl) Then Return
            Dim updated As String = ServiceSecurity.AddDenyDeleteAce(sddl)
            If updated <> sddl Then WriteServiceDaclSddl(svc, updated)
        Catch ex As Exception
        Finally
            If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
            If scm <> IntPtr.Zero Then CloseServiceHandle(scm)
        End Try
    End Sub

    ' B6 re-grant (the non-negotiable teardown step): remove the deny-DELETE ACE
    ' so a genuinely expired block leaves a fully REMOVABLE service. The exact
    ' inverse of AssertDenyDeleteAce. stopMe() calls this AFTER killing the
    ' guardian and BEFORE Me.Stop()/End, so no live guardian/tick can re-deny in
    ' the gap. Best-effort; a no-op if the ACE is absent.
    Private Sub RestoreDefaultServiceSd()
        Dim scm As IntPtr = IntPtr.Zero
        Dim svc As IntPtr = IntPtr.Zero
        Try
            scm = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then Return
            svc = OpenService(scm, "MONKMODE", READ_CONTROL Or WRITE_DAC)
            If svc = IntPtr.Zero Then Return
            Dim sddl As String = ReadServiceDaclSddl(svc)
            If sddl Is Nothing Then Return
            Dim updated As String = ServiceSecurity.RemoveDenyDeleteAce(sddl)
            If updated <> sddl Then WriteServiceDaclSddl(svc, updated)
        Catch ex As Exception
        Finally
            If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
            If scm <> IntPtr.Zero Then CloseServiceHandle(scm)
        End Try
    End Sub

    ' ===== AppDomain.UnhandledException backstop (fail-closed on crash) =====
    '
    ' A last-line safety net in the fail-closed doctrine. The hot enforcement paths
    ' already re-lock hosts in their own Try/Finally (adder_Changed, the timer
    ' self-heal), and the two framework paths that could throw - OnStart (ServiceBase
    ' swallows it) and the Timers.Timer Elapsed tick (the timer swallows it) - do NOT
    ' reach here AND can't leave hosts writable anyway (OnStart has not cleared the RO
    ' attribute at the points it can throw; the tick's own Finally re-locks). This
    ' handler catches the REMAINING case the doctrine still wants covered: a genuinely
    ' unhandled exception on some other thread (a worker/callback not wrapped by a
    ' framework try) that DOES terminate the process - so even then hosts is re-locked
    ' before exit and a crash can never leave the block fail-OPEN. Registered in Main;
    ' Shared so it needs no live Service1 instance (which may be in a bad state here).
    '
    ' It only re-asserts the HOSTS state, deliberately not the SafeBoot/DoH/deny-ACE
    ' enforcement: those are persistent registry/SCM state a crash does not remove
    ' (nothing to fail OPEN there), and the forced restart (SCM FailureActions + the
    ' B1 guardian) re-asserts them anyway on the fresh OnStart. Keeping the crash
    ' handler to the one thing a crash can leave OPEN - a writable / stripped hosts -
    ' keeps it minimal and hard to make throw.
    '
    ' The wrapper feeds the testable core the REAL hosts + block-snapshot paths (the
    ' same Environ("WinDir") / Application.StartupPath idioms the rest of the service
    ' uses). Everything - including the path construction - is inside the Try so
    ' nothing can throw back out of the handler (parity with the guardian/notifier
    ' handlers). The core is split out with explicit path params so it can be
    ' unit-tested against temp files (fence: unit tests never touch the real hosts).
    Private Shared Sub OnUnhandledException(ByVal sender As Object, ByVal e As UnhandledExceptionEventArgs)
        Try
            ReassertHostsFailClosed(Environ("WinDir") & "\system32\drivers\etc\hosts",
                                    Application.StartupPath & "\monkmode_hosts.block")
        Catch ex As Exception
        End Try
    End Sub

    ' The testable core of the crash backstop. Re-assert the fail-closed hosts state:
    '   1. Restore our marker block, gated on the CLI's block snapshot still being on
    '      disk. stopMe() deletes that snapshot at a genuine expiry, so its PRESENCE
    '      is the fail-closed "block still active, keep enforcing" signal and its
    '      ABSENCE means "do not re-block" - no separate, throw-prone MAC/expiry read
    '      is needed in a crash handler. The gate is snapshot presence, NOT hosts
    '      presence: a crash that BLANKED or even DELETED hosts while the block is
    '      active must be rebuilt from the snapshot too (a missing hosts reads as ""
    '      and is recreated - exactly the timer self-heal's behaviour). The restore
    '      reuses the pure, unit-tested RepairHostsBlock (intact block => no rewrite/
    '      no churn; blanked/stripped/deleted hosts => our block re-appended with the
    '      user's own content kept byte-for-byte). It only ADDS enforcement, never
    '      lifts, which is always the safe direction: the fresh OnStart after the
    '      forced restart makes the real expiry decision. (Rare edge: a crash DURING
    '      stopMe(), after the strip but before the snapshot delete, briefly re-blocks
    '      a just-expired block for one restart cycle - the fail-CLOSED over-block
    '      direction, self-corrected on the next OnStart.)
    '   2. ALWAYS leave hosts read-only - the single most important line, closing the
    '      fail-OPEN window a crash mid-write (read-only cleared) would otherwise
    '      leave. Mirrors the adder_Changed Finally.
    ' Best-effort throughout; NEVER throws (a throw from an UnhandledException handler
    ' is itself undefined behaviour). Friend Shared so the unit tests drive it against
    ' temp files, exactly like AtomicHosts.
    Friend Shared Sub ReassertHostsFailClosed(ByVal hostsPath As String, ByVal snapshotPath As String)
        Try
            If System.IO.File.Exists(snapshotPath) Then
                Dim hostsText As String = If(System.IO.File.Exists(hostsPath), System.IO.File.ReadAllText(hostsPath), "")
                Dim repaired As String = RepairHostsBlock(hostsText, System.IO.File.ReadAllText(snapshotPath))
                If repaired IsNot Nothing Then
                    If System.IO.File.Exists(hostsPath) Then SetAttr(hostsPath, vbNormal)
                    AtomicHosts.WriteAtomic(hostsPath, repaired)
                End If
            End If
        Catch ex As Exception
            ' The block restore is best-effort - the Finally still re-locks hosts.
        Finally
            ' ALWAYS re-assert read-only (fail-closed), even if the restore above
            ' threw after clearing the attribute. Guarded so the re-lock can never
            ' itself throw out of the handler.
            Try
                If System.IO.File.Exists(hostsPath) Then SetAttr(hostsPath, vbReadOnly)
            Catch ex As Exception
            End Try
        End Try
    End Sub

    ' O1 (issue #1) retry bounds for OnStart's read-only assert: 3 quick attempts
    ' (worst case adds ~400ms to OnStart - the SCM start budget is ~30s), then the
    ' retry is handed off to the 10s tick's own best-effort re-assert.
    Friend Const OnStartReadOnlyAttempts As Integer = 3
    Friend Const OnStartReadOnlyRetryDelayMs As Integer = 200

    ' Test seam (O1 fail-closed branch coverage, same pattern as
    ' AtomicHosts.RenameHookForTests). When set, TrySetHostsReadOnly calls this
    ' instead of the real SetAttr, so a unit test can force transient and
    ' persistent attribute-set failures DETERMINISTICALLY - proving the retry
    ' loop retries, the persistent path returns False without throwing, and no
    ' failure shape can reach a teardown. <ThreadStatic> so the hook is confined
    ' to the single test thread that sets it (parallel test classes unaffected);
    ' PRODUCTION never assigns it - the field stays Nothing and the real SetAttr
    ' path runs, behaviourally unchanged. Friend, so only the in-repo test
    ' assembly (InternalsVisibleTo) can even see it.
    <ThreadStatic>
    Friend Shared SetAttrHookForTests As Action(Of String)

    ' O1 fail-closed (issue #1): assert the read-only attribute on hosts with a
    ' short bounded retry, and NEVER throw - the attribute is defence-in-depth
    ' (the DNS-client lock), not the block itself, so its failure must degrade
    ' (return False, keep the service and the block standing, let the 10s tick
    ' keep re-asserting) rather than tear anything down. This replaced OnStart's
    ' old Catch -> stopMe(), the one error->LIFT path in the service. Friend
    ' Shared so the unit tests drive it against temp files, exactly like
    ' ReassertHostsFailClosed - never the live hosts.
    Friend Shared Function TrySetHostsReadOnly(ByVal hostsPath As String,
                                               ByVal attempts As Integer,
                                               ByVal retryDelayMs As Integer) As Boolean
        For attempt As Integer = 1 To attempts
            Try
                ' SetAttrHookForTests is Nothing in production - this is the
                ' plain SetAttr; the hook only diverts it under a unit test.
                If SetAttrHookForTests IsNot Nothing Then
                    SetAttrHookForTests(hostsPath)
                Else
                    SetAttr(hostsPath, vbReadOnly)
                End If
                Return True
            Catch ex As Exception
                ' Swallow EVERY failure shape (missing file, sharing violation,
                ' ACL denial): pause briefly between attempts, and after the
                ' last one degrade to False - never rethrow, never lift.
                If attempt < attempts AndAlso retryDelayMs > 0 Then
                    Thread.Sleep(retryDelayMs)
                End If
            End Try
        Next
        Return False
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

            ' C1: atomic write (temp + rename) so a crash while stripping our block
            ' at expiry can never blank hosts or lose the user's own entries
            ' (read-only was cleared above). SetAttr below keeps the existing
            ' expiry behaviour - it is NOT a Try/Finally re-assert (which would
            ' wrongly lock a clean hosts on the early-return paths).
            AtomicHosts.WriteAtomic(hostDirS, original)
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

        ' C1b: drop the config shadow backup too - a genuinely expired block must
        ' leave nothing behind to restore an old config from (mirrors the hosts
        ' snapshot delete above + the CLI escape hatch's DeleteBackup). Best-effort.
        Try
            System.IO.File.Delete(Application.StartupPath + "\" + ConfigBackup.BackupFileName)
        Catch ex As Exception
        End Try

        ' C2b: drop any cooling-off trigger files too - an ended block must leave
        ' no stale request behind, or the NEXT block would start a cooling-off the
        ' moment it arms (fail-closed but wrong). Best-effort.
        Try
            System.IO.File.Delete(Application.StartupPath + "\" + CoolOffRequestFileName)
        Catch ex As Exception
        End Try
        Try
            System.IO.File.Delete(Application.StartupPath + "\" + CoolOffCancelFileName)
        Catch ex As Exception
        End Try

        ' C3b: drop any partner-code trigger too - an ended block must leave no stale
        ' candidate behind (a used code dies with the block; the next arm mints a
        ' fresh one - rotate-on-use). Best-effort.
        Try
            System.IO.File.Delete(Application.StartupPath + "\" + PartnerCodeFileName)
        Catch ex As Exception
        End Try

        ' B3: drop the SafeBoot registration too - a genuinely expired block must
        ' leave nothing that keeps the (about-to-stop) service starting in Safe
        ' Mode. Best-effort; the OnStart path also removes stale keys on a restart
        ' into an already-expired block.
        RemoveSafeBootRegistration()

        ' B5a: restore the user's prior browser DoH policy (or remove ours) from the
        ' snapshot - a genuinely expired block must undo the DoH-off enforcement with
        ' no data loss. Snapshot-aware + per-entry Try internally; consumes the
        ' snapshot so a restart into the expired block can't re-restore a stale prior.
        RemoveDohPolicy()

        ' B1 layer 2: tear the guardian down too (best effort). It would also
        ' stand down by itself on its next tick - it reads the same parsed,
        ' past end time and exits - but killing it here makes the teardown
        ' immediate and leaves no stray process behind an expired block.
        Try
            For Each guardProc As System.Diagnostics.Process In System.Diagnostics.Process.GetProcessesByName("mm_guard")
                Try
                    guardProc.Kill()
                Catch ex As Exception
                End Try
            Next
        Catch ex As Exception
        End Try

        ' B6 re-grant - THE non-negotiable teardown step: remove the deny-DELETE
        ' ACE so a genuinely expired block leaves a fully REMOVABLE service.
        ' ORDERING IS LOAD-BEARING: this runs AFTER the guardian kill above and
        ' BEFORE Me.Stop()/End below, so no live guardian (or another tick of
        ' this service) can re-assert the deny in the gap between removing it and
        ' the process exiting. Best-effort; if it somehow fails, the service is
        ' still removable via `monkmode unblock --force` (which restores the SD
        ' itself). Without this, an expired block would leave a service that
        ' resists sc-delete until the next install rewrote the DACL.
        Try
            RestoreDefaultServiceSd()
        Catch ex As Exception
        End Try

        Me.Stop()
        End

    End Sub

    ' Audit #6 de-dup gate: FileSystemWatcher routinely raises two or more
    ' Changed events for ONE logical write to add_to_hosts (data + metadata
    ' updates), delivered on threadpool threads that can run CONCURRENTLY.
    ' Shared so every fire contends on the same monitor.
    Private Shared ReadOnly adderGate As New Object()

    Private Sub adder_Changed(ByVal sender As System.Object, ByVal e As System.IO.FileSystemEventArgs) Handles adder.Changed
        ' Thin wrapper: feed the testable core the real trigger/hosts/snapshot
        ' paths. Guarded so nothing - even the path construction - can throw
        ' out of the watcher callback and crash the service (parity with the
        ' OnUnhandledException wrapper).
        Try
            ProcessAddToHosts(sWinDir & "\system32\drivers\etc\add_to_hosts",
                              hostDirS,
                              Application.StartupPath + "\monkmode_hosts.block")
        Catch ex As Exception
        End Try
    End Sub

    ' The testable core of the add_to_hosts channel. Two audit fixes live here:
    '
    ' FAIL-OPEN FIX (audit #2): this runs from a FileSystemWatcher callback. It
    ' clears hosts' read-only attribute to append, so an unhandled throw here
    ' (a locked/IO-erroring hosts, a transient read) both crashes the
    ' LocalSystem service AND leaves hosts WRITABLE - a fail-OPEN window
    ' against the fail-closed doctrine. Mirror the timer self-heal pattern:
    ' Try/Catch the whole body so the watcher thread/service survives, and a
    ' Finally that ALWAYS re-asserts read-only so hosts is never left writable
    ' on any exit path. Best-effort append; a failed add must never weaken the
    ' block.
    '
    ' DOUBLE-FIRE DE-DUP (audit #6): without serialisation, a duplicate
    ' Changed fire could read the trigger file after another fire read it but
    ' BEFORE that fire deleted it, appending the same entries to hosts + the
    ' snapshot twice (harmless to enforcement, but real churn in the user's
    ' hosts). SyncLock serialises the fires, and the trigger delete happens
    ' INSIDE the critical section, so a duplicate fire re-checks File.Exists
    ' and no-ops: at most one append per trigger-file write. A failed delete
    ' keeps today's behaviour (a later fire may re-append - the trigger still
    ' says "append me", and losing an add would weaken the block).
    '
    ' Friend Shared with explicit path params so the unit tests drive it
    ' against temp files, exactly like ReassertHostsFailClosed (fence: unit
    ' tests never touch the real hosts).
    Friend Shared Sub ProcessAddToHosts(ByVal triggerPath As String, ByVal hostsPath As String, ByVal snapshotPath As String)
        SyncLock adderGate
            Try
                If System.IO.File.Exists(triggerPath) Then
                    Dim toAdd As String = System.IO.File.ReadAllText(triggerPath)
                    SetAttr(hostsPath, vbNormal)
                    System.IO.File.AppendAllText(hostsPath, toAdd)
                    ' Mirror the append into the repair snapshot (best effort) so a
                    ' later B2 self-heal restores the added sites too. Only when the
                    ' snapshot already exists: creating one here would make a
                    ' marker-less "expected block" that a repair would then write and
                    ' the expiry strip could never remove.
                    Try
                        If System.IO.File.Exists(snapshotPath) Then
                            System.IO.File.AppendAllText(snapshotPath, toAdd)
                        End If
                    Catch ex As Exception
                    End Try
                    ' The de-dup pivot: consume the trigger before releasing the
                    ' lock, so the next queued fire finds nothing to process.
                    Try
                        System.IO.File.Delete(triggerPath)
                    Catch ex As Exception
                    End Try
                End If
            Catch ex As Exception
                ' Swallow so a throw can never escape the watcher callback and crash
                ' the service. The Finally below re-locks hosts (fail-closed).
            Finally
                ' ALWAYS re-assert read-only - even if the append threw after the
                ' attribute was cleared, hosts must not be left writable. Guarded +
                ' best-effort so the re-lock itself can never throw out of Finally.
                Try
                    If System.IO.File.Exists(hostsPath) Then SetAttr(hostsPath, vbReadOnly)
                Catch ex As Exception
                End Try
            End Try
        End SyncLock
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
            ' Fail CLOSED, like the CLI/notifier/guardian copies: return "" so a
            ' junk value written into an encrypted field (e.g. [Time] Until) is
            ' treated as an unparseable plaintext (block stays standing), instead
            ' of `End` force-terminating the LocalSystem service - that `End` was
            ' an availability bypass (write garbage -> service dies). The B7 MAC
            ' makes such a junk write fail verification anyway, but the decrypt
            ' must not crash the enforcement core regardless.
            Return ""
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