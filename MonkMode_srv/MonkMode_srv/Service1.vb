'    Copyright (c) 2011, 2012 Felix Belzile
'    Source: https://github.com/samrathsingh302/monkmode
'
'    Modified by Samrath Singh, 2026 — hardened enforcement core of the MonkMode
'    fork: fail-closed gates (hosts self-heal, guardian spawn, SafeBoot/DoH
'    self-register, monotonic clock, cooling-off / partner-code / commit /
'    schedules) (fork: MonkMode).

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
    ' FX6 (F7): the MONOTONIC moment (Environment.TickCount64) this service instance first
    ' observed the CURRENT unbroken run of a raised [Time] TimeChanging flag; 0 = the flag
    ' reads "no". In-memory on purpose - the flag itself sits OUTSIDE the MAC-covered
    ' canonical and survives a reboot, so it cannot carry its own age, and a persisted age
    ' would be raw-editable anyway. TickCount64 (not the wall clock) because the whole
    ' episode this measures IS a clock change. See TimeChangeHoldActive.
    Private timeChangeRaisedAtMono As Long = 0
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

    ' TEST SEAM - the SetAttrHookForTests / AtomicHosts.RenameHookForTests pattern, and the
    ' one thing standing between the unit suite and a LIVE enforcement tick.
    '
    ' InitializeComponent sets timer.Enabled = True, so merely CONSTRUCTING a Service1 starts
    ' the 10s tick on a threadpool thread. In production that is harmless - a Service1 is only
    ' ever constructed in order to be Run. In a unit test it is not: the tick writes
    ' Application.StartupPath\monkmode_settings.ini (which under `dotnet test` IS the test-bin
    ' config every CLI-writer test asserts over), re-asserts the read-only attribute on the
    ' REAL hosts file, and would run the self-heals if it ever read a held block. It was
    ' observed corrupting a sibling test: an unrelated heartbeat rewrote [CurrentTime] Now
    ' underneath SlotArmTests' "a second arm never re-seeds the frame" assertion.
    '
    ' Tests call this the moment they construct one. PRODUCTION NEVER CALLS IT - Main() runs
    ' ServiceBase.Run(New Service1()) and the timer must stay live there. It is deliberately
    ' the narrowest possible seam (it cannot re-enable anything) rather than a change to when
    ' production arms the timer; that larger fix is flagged in the handback, not taken here.
    Friend Sub StopTimerForTests()
        Try
            timer.Enabled = False
        Catch ex As Exception
        End Try
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
                ' F77: kick the trusted-time probe the moment we start, because a boot is
                ' exactly when a downtime credit is owed. It runs in the BACKGROUND and is
                ' not read here: OnStart deliberately keeps deciding off the STORED mark,
                ' unchanged, and the first live tick that finds a reading folds the credit
                ' in and lifts. Two reasons for that split. (1) SCM: OnStart has a start
                ' timeout, and three sequential HTTPS HEADs could eat it - a service that
                ' fails to start is a far worse outcome than lifting ~10s later. (2) Risk:
                ' the OnStart exit decision is the most delicate code in the service, and
                ' this change does not need to touch it. Cost of the split is one tick.
                ' anchorMissing:=True unconditionally: this is the first probe of the
                ' instance, so the cadence flag cannot make it any sooner, and OnStart has
                ' no reason to decrypt the anchor just to answer it.
                trustedProbe.RequestIfDue(lastMonoMs, True)
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

                ' ---- v1.1 S3b: the BOOT-TIME slot pass (carried finding 3) ----
                ' Before S3b the v9 twin ran its window re-evaluation at OnStart (isBoot:=True)
                ' but the SLOTS were polled only on the 10s tick - so a reboot landing inside a
                ' slot's window got its re-hold from the first tick, and the exit decision
                ' below adjudicated BEFORE that. The slots therefore get the identical boot
                ' treatment here: re-open any window the boot lands inside, activate any
                ' PENDING slot whose start has arrived, and only then decide.
                '
                ' Everything is measured against the STORED HighWater - OnStart never advances
                ' the mark (there is no monotonic anchor across a restart), so downtime is
                ' never credited and a reboot only ever OVER-blocks. Both passes may Save via
                ' PersistSlotField; that is safe because the one later OnStart save
                ' (ShouldRestampOnStart) is inert here (newHw = storedHw), exactly as the v9
                ' schedule re-hold above already relies on.
                Dim slotsAtStart As List(Of SlotState) = LoadSlots(iniFile)
                ProcessSlotScheduleWindows(slotsAtStart, "", DateTime.Now.ToString(culture), storedHw, 0, macValidAtStart, True)
                ActivateDueSlots(slotsAtStart, storedHw, macValidAtStart)
                Dim storedHwAsOf As DateTime = DateTime.MinValue
                Dim parsedStoredHw As DateTime
                If DateTime.TryParse(storedHw, culture, DateTimeStyles.None, parsedStoredHw) Then storedHwAsOf = parsedStoredHw
                ' AnyBlockHeld answers Not macValid BEFORE the loop, so a frozen config holds
                ' even with zero readable slots - and a PENDING slot counts as held, so a boot
                ' can never tear down a block that has not started yet (P39). Grace 0 here,
                ' matching OnStart's deliberately stricter expiry.
                Dim slotsHoldAtStart As Boolean = AnyBlockHeld(slotsAtStart, storedHwAsOf, 0, macValidAtStart, storedHw)

                If Not slotsHoldAtStart AndAlso EffectiveExit(encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until")), coolOffAtStart, unlockedAtStart, scheduleActiveAtStart, storedHw, 0, macValidAtStart, scheduleArmedAtStart) Then
                    ' The ONLY OnStart path that may tear the machine down, and it is now the
                    ' boot twin of ClassifyTick: NO slot may be holding (which folds in every
                    ' slot's own MAC-covered end, cooling-off, code and window) AND the v9
                    ' residual must ALSO have exited. The residual keeps exactly the power it
                    ' has in the tick - it can hold a teardown back, never cause one - so
                    ' back-dating [Time] Until at boot no longer tears down a live slot.
                    ' Fail-closed throughout: an invalid MAC makes slotsHoldAtStart True AND
                    ' EffectiveExit False, so a tampered config can never teardown at boot.
                    TeardownAll()
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
        ' O2: no pre-blanking of the target here. The IniFile below is built
        ' purely in memory and Save() is an atomic temp-file + rename replace
        ' (creates the target if missing), so a blank-first WriteAllText would
        ' only open a needless 0-byte window between the blank and the rename.
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
        ' F77: the mark's UTC anchor, EMPTY - same shape as a CLI-armed block, and empty
        ' for the same reason: an anchor derived from this machine's own clock is exactly
        ' what F77 refuses to trust (see Blocker.vb's fresh-arm seed). The service seeds
        ' it from a corroborated reading. Unstamped like everything else here, so macValid
        ' stays False and the block holds regardless.
        iniFile.SetKeyValue("Time", "TrustedUtc", "")
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

    ' ---- LEDGER 319 (30/08/2026): THE COOLING-OFF CHANNEL IS DELETED ----
    '
    ' What stood here was C2b's per-tick cooling-off poll - ProcessCoolOffSignals and its
    ' testable core ProcessCoolOffSignalsAt - which read a monkmode_cooloff.request.<id>
    ' trigger, computed a floor-clamped deadline off the trusted HighWater and wrote it to
    ' that slot's MAC-covered CoolOffUntil. When the deadline elapsed the block LIFTED, with
    ' no code and nobody's permission. That was the second of the two no-code exits Samrath
    ' asked to be rid of on 30/08/2026 ("i should only be able to unblock with code"); the
    ' first, `unblock --force`, is gone from the CLI in the same slice.
    '
    ' Deleted with it: ClassifyCoolOffSignal (the request/cancel matrix), the CoolOffAction
    ' enum, ComputeCoolOffDeadline, ParseConfiguredCoolOffSeconds and MinCoolOffFloorSeconds.
    ' There is now NO writer for any slot's CoolOffUntil anywhere in the four assemblies.
    '
    ' What deliberately REMAINS, and why:
    '   * the CoolOffUntil / CoolOffDuration fields in the v12 canonical, and the four
    '     CanonicalFromIni wrappers that feed them. Removing a MAC-covered field means a
    '     schema bump and a four-copy parity edit, and v12 (F77) is about to deploy - so the
    '     fields stay, written empty by the CLI, and mean nothing.
    '   * CoolOffElapsedTime, hard-wired to False in both its copies (see its own comment).
    '     It is still called from EffectiveExit, so even a config carrying a forged, elapsed
    '     CoolOffUntil cannot lift: the reader ignores the value rather than trusting it.
    '   * CoolOffRequestPrefix / CoolOffCancelPrefix, so the enumeration glob still finds a
    '     stale trigger from an older dist and PurgeUnaddressedTriggers can delete it.

    ' The slot carrying this id, or Nothing. Ordinal Id comparison, matching
    ' FindSlotPositionById's - the two must agree or a trigger could route to a slot the
    ' writer then fails to locate. Pure + Shared.
    Friend Shared Function FindSlotById(ByVal slots As List(Of SlotState), ByVal slotId As String) As SlotState
        If slots Is Nothing Then Return Nothing
        Dim wanted As String = If(slotId, "").Trim()
        If wanted = "" Then Return Nothing
        For Each s As SlotState In slots
            If String.Equals(If(s.Id, "").Trim(), wanted, StringComparison.Ordinal) Then Return s
        Next
        Return Nothing
    End Function

    ' Delete one trigger file, best-effort. A failed delete is harmless: the next tick
    ' re-classifies it (Ignore, since the state it asked for is already applied) and tries
    ' again. NEVER throws.
    Private Shared Sub DeleteTriggerFile(ByVal path As String)
        Try
            System.IO.File.Delete(path)
        Catch ex As Exception
        End Try
    End Sub

    ' P41: the trigger file NAMES on disk this tick, capped and ordinal-sorted. Enumerated
    ' once per tick and shared by both pollers, so the cap is a single budget across the
    ' whole channel rather than one per family. Best-effort: an unreadable directory yields
    ' an empty list, which just defers every trigger to the next tick (fail-closed - a
    ' deferred EXIT trigger holds the block ~10s longer). NEVER throws.
    Private Function EnumerateTriggerFiles() As List(Of String)
        Return EnumerateTriggerFilesIn(Application.StartupPath)
    End Function

    ' The testable core, with the state directory made explicit (the PersistSlotFieldAt /
    ' ProcessAddToHosts pattern) so unit tests drive the real enumeration against a temp
    ' directory and never the deployed state zone.
    Friend Shared Function EnumerateTriggerFilesIn(ByVal stateDir As String) As List(Of String)
        Dim names As New List(Of String)
        Try
            ' v1.1 S5 (P42): the `add` family joins the SAME capped, ordinal-sorted budget -
            ' P41 sized it at 2 x MaxSlots for exactly this. Deferring an add delays a WIDEN
            ' by <=10s, which is the harmless direction.
            For Each pattern As String In New String() {CoolOffRequestPrefix & "*", CoolOffCancelPrefix & "*", PartnerCodePrefix & "*", AddRequestPrefix & "*"}
                For Each full As String In System.IO.Directory.GetFiles(stateDir, pattern)
                    names.Add(System.IO.Path.GetFileName(full))
                Next
            Next
        Catch ex As Exception
            Return New List(Of String)
        End Try
        Return SelectTriggerFiles(names, MaxTriggerFilesPerTick)
    End Function

    ' Does this enumerated name address ANY trigger family? Pure + Shared.
    '
    ' Ledger 319: the two COOLING-OFF prefixes were REMOVED from this list. That is the whole
    ' mechanism by which a stray monkmode_cooloff.request.<id> / .cancel.<id> is now disposed
    ' of: it still matches the enumeration glob (EnumerateTriggerFilesIn keeps both patterns on
    ' purpose), it now addresses no family, and PurgeUnaddressedTriggers therefore deletes it
    ' before either poller runs. Nothing reads it, so it cannot start a wait; and it is not left
    ' on disk to squat the per-tick budget for ever, which is the failure PurgeUnaddressedTriggers
    ' was written for. Adding these prefixes back would resurrect nothing (there is no cooling-off
    ' reader any more) but WOULD strand the files - do not.
    Friend Shared Function TriggerAddressesAnyFamily(ByVal fileName As String) As Boolean
        Return TriggerIdFromName(fileName, PartnerCodePrefix) <> "" OrElse
               TriggerIdFromName(fileName, AddRequestPrefix) <> ""
    End Function

    ' Delete every enumerated name that resolves to NO family id. Two shapes reach here, and
    ' both leak the P41 budget permanently if left alone:
    '   * the UNSUFFIXED legacy names an older CLI dropped (monkmode_cooloff.request with no
    '     id). The Win32 trailing-".*" glob quirk matches them - "prefix.*" also matches
    '     "prefix" with nothing after it - so they are enumerated every tick forever, occupy
    '     up to 3 of the 16 slots, and neither poller ever reaches its delete because
    '     TriggerIdFromName returns "". At a full 8-slot load that can starve the tail, and
    '     partner codes sort LAST.
    '   * a bare "<prefix>" with a blank id, from a truncated write.
    ' Deleting them is the over-blocking direction either way (they carry no authority and
    ' nothing reads them), and it removes the "an old dist's `unblock` appears to do nothing,
    ' forever" confusion - which would otherwise read as a real failure during a smoke.
    ' Best-effort; never throws.
    Friend Shared Sub PurgeUnaddressedTriggers(ByVal stateDir As String, ByVal triggerNames As List(Of String))
        If triggerNames Is Nothing Then Return
        For Each name As String In triggerNames
            If TriggerAddressesAnyFamily(name) Then Continue For
            DeleteTriggerFile(System.IO.Path.Combine(stateDir, name))
        Next
    End Sub

    ' v1.1 S3b: SLOT-ADDRESSED (P40). A candidate submitted as monkmode_partner.code.<id> is
    ' verified ONLY against slot <id>'s own MAC-covered PartnerSalt/PartnerHash, so holding
    ' one block's code retires that block and NO other - the pre-S3b model verified every
    ' candidate against the single machine-wide [Partner] verifier and lifted everything. The
    ' CLI broadcasts the same candidate to every armed slot, which is safe precisely because
    ' each slot verifies independently: only the owning slot can match.
    '
    ' The VERIFIER is read from the RELOADED, MAC-revalidated ini at the RE-LOCATED position -
    ' never from the SlotState captured earlier in the tick - so the bytes the code is checked
    ' against and the bytes UnlockedAt is written onto are provably one consistent MAC-valid
    ' config (the heartbeat's #4 TOCTOU rule). Consume-after-persist is preserved: the trigger
    ' is deleted only after the write lands, so a crash between them re-classifies next tick
    ' as alreadyUnlocked => Ignore (no lost unlock, no double-set). A miss deletes the trigger,
    ' writes nothing and does NOT rotate the code - only success rotates, or spamming misses
    ' would grief-lock the partner's legitimate code (the PD6 availability concern). Runs
    ' inside tickLock while TimeChanging="no" (the caller gates both). Never throws.
    Private Sub ProcessPartnerCodeSignal(ByVal slots As List(Of SlotState), ByVal macValid As Boolean, ByVal triggerNames As List(Of String))
        ProcessPartnerCodeSignalAt(Application.StartupPath, Application.StartupPath + "\monkmode_settings.ini",
                                   slots, macValid, triggerNames)
    End Sub

    ' The testable core with the state directory and config path made explicit.
    Friend Sub ProcessPartnerCodeSignalAt(ByVal stateDir As String, ByVal iniPath As String, ByVal slots As List(Of SlotState), ByVal macValid As Boolean, ByVal triggerNames As List(Of String))
        Try
            If slots Is Nothing OrElse triggerNames Is Nothing OrElse triggerNames.Count = 0 Then Return
            For Each name As String In triggerNames
                Dim id As String = TriggerIdFromName(name, PartnerCodePrefix)
                If id = "" Then Continue For
                Dim codePath As String = System.IO.Path.Combine(stateDir, PartnerCodePrefix + id)
                Dim slot As SlotState = FindSlotById(slots, id)
                If slot Is Nothing Then
                    ' P40: unknown / retired / garbage id - delete, no state change, no freeze.
                    DeleteTriggerFile(codePath)
                    Continue For
                End If

                ' Read the candidate, length-capped: an over-large trigger is a memory/DoS
                ' lever, not a real attempt, so it reads as "" (a non-matching attempt) and
                ' is simply deleted. The service NEVER logs the candidate.
                Dim candidate As String = ""
                Try
                    Dim fi As New FileInfo(codePath)
                    If fi.Length <= TriggerMaxBytes Then
                        candidate = System.IO.File.ReadAllText(codePath)
                    End If
                Catch ex As Exception
                    candidate = ""
                End Try

                If ClassifyPartnerCodeSignal(True, Not String.IsNullOrWhiteSpace(candidate),
                                             slot.PartnerUnlockedAt <> "", macValid) <> PartnerCodeAction.Verify Then
                    ' Ignore: blank candidate, this slot already unlocked, or a frozen config.
                    DeleteTriggerFile(codePath)
                    Continue For
                End If

                Dim iniFile = New IniFile
                iniFile.Load(iniPath)
                If Not ConfigMacIsValidForIni(iniFile) Then
                    DeleteTriggerFile(codePath)
                    Continue For
                End If
                Dim pos As Integer = FindSlotPositionById(iniFile, id)
                If pos = 0 Then
                    ' Retired between this tick's read and now - nothing to unlock.
                    DeleteTriggerFile(codePath)
                    Continue For
                End If
                Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
                If ConfigIntegrity.PartnerCodeMatches(candidate, iniFile.GetKeyValue(sec, "PartnerSalt"), iniFile.GetKeyValue(sec, "PartnerHash")) Then
                    ' MATCH: set THIS slot's MAC-covered PartnerUnlockedAt through the one
                    ' per-slot writer (P36), then consume the trigger.
                    Dim unlockedAt As String = DateTime.Now.ToString(culture)
                    If PersistSlotFieldAt(iniPath, id, "PartnerUnlockedAt", unlockedAt, False) Then
                        slot.PartnerUnlockedAt = unlockedAt
                        DeleteTriggerFile(codePath)
                    End If
                Else
                    DeleteTriggerFile(codePath)
                End If
            Next
        Catch ex As Exception
        End Try
    End Sub

    ' ---- P42 (v1.1 S5): `monkmode add` becomes SERVICE-ADJUDICATED ----
    '
    ' The CLI validates the requested sites and drops `monkmode_add.request.<id>`; this step
    ' grows THAT slot's MAC-covered `Sites` through the one per-slot writer (P36), and the
    ' existing P37 reconciliation then rewrites the snapshot from config truth and the B2
    ' self-heal propagates it into hosts. Nothing here touches hosts directly.
    '
    ' GROWTH-ONLY, and that is the whole safety argument: a request can only ever ADD entries
    ' to one slot's list, so a forged, replayed or garbage trigger can block MORE, never less.
    ' It has no timing authority, cannot address another slot's fields, and cannot shorten,
    ' lift or retire anything. An unknown/retired id (P40) deletes the trigger and changes
    ' nothing - the id routes, it never authorises.
    '
    ' Consume-after-persist, like the two exit families: the trigger is deleted only once the
    ' write lands, so a crash between them simply re-applies next tick (the merge is
    ' idempotent - a re-applied request adds nothing new and is then deleted).
    Private Sub ProcessAddRequests(ByVal slots As List(Of SlotState), ByVal macValid As Boolean, ByVal triggerNames As List(Of String))
        ProcessAddRequestsAt(Application.StartupPath, Application.StartupPath + "\monkmode_settings.ini",
                             slots, macValid, triggerNames)
    End Sub

    ' The testable core with the state directory and config path made explicit.
    Friend Sub ProcessAddRequestsAt(ByVal stateDir As String, ByVal iniPath As String, ByVal slots As List(Of SlotState), ByVal macValid As Boolean, ByVal triggerNames As List(Of String))
        Try
            If slots Is Nothing OrElse triggerNames Is Nothing OrElse triggerNames.Count = 0 Then Return
            For Each name As String In triggerNames
                Dim id As String = TriggerIdFromName(name, AddRequestPrefix)
                If id = "" Then Continue For
                Dim addPath As String = System.IO.Path.Combine(stateDir, AddRequestPrefix + id)
                Dim slot As SlotState = FindSlotById(slots, id)
                If slot Is Nothing Then
                    ' P40: unknown / retired / garbage id - delete, no state change, no freeze.
                    DeleteTriggerFile(addPath)
                    Continue For
                End If
                If Not macValid Then
                    ' A frozen config is never widened and never re-stamped (the B7 rule). The
                    ' request cannot be applied while frozen and a frozen config is only left by
                    ' re-arming, so hold nothing over: delete it, exactly as the cooling-off and
                    ' partner-code families do, rather than leaking the P41 budget for ever.
                    DeleteTriggerFile(addPath)
                    Continue For
                End If
                ' Length-capped read, same rule as the partner-code candidate: an over-large
                ' trigger is a DoS lever, not a real request, so it reads as "" and is binned.
                Dim requested As String = ""
                Try
                    Dim fi As New FileInfo(addPath)
                    If fi.Length <= TriggerMaxBytes Then requested = System.IO.File.ReadAllText(addPath)
                Catch ex As Exception
                    requested = ""
                End Try
                Dim grown As String = MergeSiteList(slot.Sites, requested)
                If grown = "" Then
                    ' Oversize, unreadable, empty, all-invalid or entirely redundant - there is
                    ' nothing to apply, so consume the trigger and change nothing.
                    DeleteTriggerFile(addPath)
                    Continue For
                End If
                If PersistSlotFieldAt(iniPath, id, "Sites", grown, False) Then
                    ' In-memory too, so THIS tick's hosts/snapshot union already carries the new
                    ' sites (the widening direction; a failed persist just retries next tick).
                    slot.Sites = SplitPackedList(grown, ";"c)
                    DeleteTriggerFile(addPath)
                End If
            Next
        Catch ex As Exception
        End Try
    End Sub

    ' P42 (pure): the growth-only merge - the slot's existing entries in order, then every
    ' requested entry not already present (case-insensitive). Requested entries are split on
    ' newlines, commas and semicolons (the CLI writes one per line; the others are accepted so
    ' a hand-dropped trigger behaves).
    '
    ' An entry is accepted ONLY if it can survive storage: non-empty, no ";" (the pack
    ' separator) and no whitespace. Anything else is DROPPED, not stored - a token that breaks
    ' the packed list breaks the canonical, which would freeze the block the user was trying
    ' to extend. Dropping is also the exact fail-closed stance TryExpandPresets takes.
    '
    ' Returns the new packed value, or "" when nothing would change (the caller writes
    ' nothing). Pure + Shared.
    Friend Shared Function MergeSiteList(ByVal existing As List(Of String), ByVal requestedRaw As String) As String
        Dim merged As New List(Of String)
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If existing IsNot Nothing Then
            For Each e As String In existing
                Dim s As String = If(e, "").Trim()
                If s <> "" AndAlso seen.Add(s) Then merged.Add(s)
            Next
        End If
        Dim before As Integer = merged.Count
        If requestedRaw IsNot Nothing Then
            For Each tok As String In requestedRaw.Split(New Char() {ControlChars.Cr, ControlChars.Lf, ","c, ";"c})
                Dim s As String = tok.Trim()
                If s = "" Then Continue For
                If s.IndexOf(";"c) >= 0 Then Continue For
                If s.IndexOfAny(New Char() {" "c, ControlChars.Tab}) >= 0 Then Continue For
                If seen.Add(s) Then merged.Add(s)
            Next
        End If
        If merged.Count = before Then Return ""      ' nothing new: write nothing
        Return String.Join(";", merged) & ";"
    End Function

    ' C5b (b2) live wiring: the per-tick schedule window step - the sibling of
    ' ProcessPartnerCodeSignal, polled AFTER it (inside tickLock
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
    ' writer of ActiveUntil (the guardian only reads it), so ActiveUntil itself has no write
    ' race - but the CLI owns the SPEC, and a `schedule --clear`/re-arm can land between this
    ' tick's load and the persist below (issue #2). Persist ONLY on change, via
    ' PersistScheduleActiveUntil (RELOAD + TOCTOU re-validate + Spec-unchanged re-check
    ' against THIS tick's snapshot spec + re-stamp with the existing key + Save + refresh the
    ' C1b backup) - the ProcessPartnerCodeSignal discipline; a changed Spec aborts the write and
    ' the next tick re-evaluates off the fresh Spec (ScheduleSpecUnchangedSinceSnapshot).
    ' Best-effort throughout; a throw never crashes the tick (it continues off
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
                ' window) or the Spec changed under this tick (a CLI clear/re-arm landed -
                ' issue #2), nothing is written and we keep the pre-read value (fail-closed;
                ' the heartbeat's own reload re-validates and Holds, and the next tick
                ' re-evaluates off the fresh Spec).
                If PersistScheduleActiveUntil(target, spec) Then Return target
                Return currentScheduleActiveUntil
            End If
            Return currentScheduleActiveUntil
        Catch ex As Exception
            Return currentScheduleActiveUntil
        End Try
    End Function

    ' Persist [Schedule] ActiveUntil = newValue ("" clears it), the ProcessPartnerCodeSignal
    ' write discipline: RELOAD the ini, TOCTOU re-validate its MAC (only re-stamp bytes just
    ' re-verified - never re-bless a swap in the read->reload window), re-check the [Schedule]
    ' Spec is still the one newValue was DERIVED from (issue #2: the CLI clearing/re-arming the
    ' Spec mid-tick must not have a stale-spec ActiveUntil written over it - see
    ' ScheduleSpecUnchangedSinceSnapshot), set the field (encrypted like CoolOffUntil; ""
    ' stored verbatim = no window), re-stamp with the EXISTING key, Save, and refresh the C1b
    ' backup (guarded on the in-memory MAC, so a bad primary can never overwrite a good backup
    ' - no data loss). Returns True iff the write happened (a failed re-validate or a changed
    ' Spec returns False; the caller keeps the old value and the next tick re-evaluates off
    ' the fresh Spec). Best-effort; never throws (the caller is inside a Try too).
    Private Function PersistScheduleActiveUntil(ByVal newValue As String, ByVal snapshotSpec As String) As Boolean
        Try
            Dim iniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            If Not ConfigMacIsValidForIni(iniFile) Then Return False
            If Not ScheduleSpecUnchangedSinceSnapshot(snapshotSpec, iniFile.GetKeyValue("Schedule", "Spec")) Then Return False
            iniFile.SetKeyValue("Schedule", "ActiveUntil", If(newValue = "", "", encryptionW.EncryptData(newValue)))
            RestampMacWithExistingKey(iniFile)
            iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
            RefreshBackupFromValid(iniFile)
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' v1.1 S3a: read the SLOTS out of a loaded ini into the per-tick SlotState list - the
    ' enforcement truth the folds/unions above reason over. Positions 1..CLAMPED SlotCount
    ' only (ConfigIntegrity.ParseSlotCount, never the raw stored value: a forged count can
    ' only ever build a canonical no MAC matches -> freeze), so stale [SlotN] sections beyond
    ' the count are invisible here exactly as they are to CanonicalFromIni.
    '
    ' Decrypt-BEFORE-macValid is deliberate and load-bearing, the same discipline the
    ' [Schedule] ActiveUntil read follows: a garbled ciphertext must THROW (-> the tick's
    ' Catch -> RecoverPrimaryConfig, fail-closed) and must never be silently read as "",
    ' which would drop a slot's end and, with it, its hold. In practice a slot that fails to
    ' decrypt has already failed the MAC (CanonicalFromIni decrypts the same four fields), so
    ' macValid is False by the time this runs and every fold holds regardless.
    Friend Function LoadSlots(ByVal ini As IniFile) As List(Of SlotState)
        Dim slots As New List(Of SlotState)
        If ini Is Nothing Then Return slots
        Dim count As Integer = ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"))
        For pos As Integer = 1 To count
            Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
            Dim s As New SlotState()
            s.Position = pos
            s.Id = ini.GetKeyValue(sec, "Id")
            Dim startEnc As String = ini.GetKeyValue(sec, "StartAt")
            Dim untilEnc As String = ini.GetKeyValue(sec, "Until")
            Dim schedEnc As String = ini.GetKeyValue(sec, "ScheduleActiveUntil")
            ' v1.1 S3b: the per-slot cooling-off deadline, decrypted with the same
            ' decrypt-BEFORE-macValid discipline as the other three datetimes - a garbled
            ' ciphertext must THROW into the tick's Catch (fail-closed), never read as ""
            ' (which would silently drop a pending cooling-off and, with it, nothing at all -
            ' but the same read is what the RETIRE decision now consumes, so a silent "" is
            ' the wrong kind of quiet).
            Dim coolOffEnc As String = ini.GetKeyValue(sec, "CoolOffUntil")
            s.StartAt = If(startEnc = "", "", encryptionW.DecryptData(startEnc))
            s.UntilText = If(untilEnc = "", "", encryptionW.DecryptData(untilEnc))
            s.ScheduleActiveUntil = If(schedEnc = "", "", encryptionW.DecryptData(schedEnc))
            s.CoolOffUntil = If(coolOffEnc = "", "", encryptionW.DecryptData(coolOffEnc))
            s.CoolOffDuration = ini.GetKeyValue(sec, "CoolOffDuration")
            s.PartnerSalt = ini.GetKeyValue(sec, "PartnerSalt")
            s.PartnerHash = ini.GetKeyValue(sec, "PartnerHash")
            s.PartnerUnlockedAt = ini.GetKeyValue(sec, "PartnerUnlockedAt")
            s.Committed = ini.GetKeyValue(sec, "Committed")
            s.DurationSeconds = ini.GetKeyValue(sec, "DurationSeconds")
            s.Sites = SplitPackedList(ini.GetKeyValue(sec, "Sites"), ";"c)
            s.Apps = SplitPackedList(ini.GetKeyValue(sec, "Apps"), ";"c)
            s.UrlPatterns = SplitPackedList(ini.GetKeyValue(sec, "UrlPatterns"), "|"c)
            s.AllSession = ini.GetKeyValue(sec, "AllSession")
            s.ScheduleSpec = ini.GetKeyValue(sec, "ScheduleSpec")
            slots.Add(s)
        Next
        Return slots
    End Function

    ' P36: the ONE per-slot writer. Order is the PersistScheduleActiveUntil discipline plus
    ' the Id re-locate that top risk 2 demands:
    '   reload -> TOCTOU re-validate the MAC (only ever re-stamp bytes just re-verified;
    '   re-stamping over an unverified config IS the B7 fail-open bug) -> RE-LOCATE the
    '   position whose Id = slotId (never trust a position captured before the reload: a
    '   compaction between read and write would otherwise write into ANOTHER slot's section,
    '   which is the mis-adjudicated-lift class) -> not found means write NOTHING -> set ->
    '   re-stamp with the EXISTING key -> Save -> refresh the C1b shadow backup.
    ' Returns True iff the write happened. NEVER throws (it runs inside the tick).
    Friend Function PersistSlotField(ByVal slotId As String, ByVal key As String, ByVal plainValue As String, ByVal encrypt As Boolean) As Boolean
        Return PersistSlotFieldAt(Application.StartupPath + "\monkmode_settings.ini", slotId, key, plainValue, encrypt)
    End Function

    ' The testable core of PersistSlotField with the config path made explicit - the
    ' ProcessScheduleSnapshot/ProcessAddToHosts pattern, so unit tests drive the real write
    ' path against a test-owned file and never the deployed config.
    Friend Function PersistSlotFieldAt(ByVal iniPath As String, ByVal slotId As String, ByVal key As String, ByVal plainValue As String, ByVal encrypt As Boolean) As Boolean
        Try
            Dim iniFile = New IniFile
            iniFile.Load(iniPath)
            If Not ConfigMacIsValidForIni(iniFile) Then Return False
            Dim pos As Integer = FindSlotPositionById(iniFile, slotId)
            If pos = 0 Then Return False        ' the slot moved or was retired: write NOTHING
            Dim stored As String = If(plainValue = "", "", If(encrypt, encryptionW.EncryptData(plainValue), plainValue))
            iniFile.SetKeyValue("Slot" & pos.ToString(CultureInfo.InvariantCulture), key, stored)
            RestampMacWithExistingKey(iniFile)
            iniFile.Save(iniPath)
            Try
                ConfigBackup.CopyIfSourceValid(iniPath,
                                               System.IO.Path.Combine(System.IO.Path.GetDirectoryName(iniPath), ConfigBackup.BackupFileName),
                                               ConfigMacIsValidForIni(iniFile))
            Catch ex As Exception
            End Try
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' ============ FX6 (F9): THE LOST-UPDATE GUARD FOR THE WHOLE-FILE SERVICE WRITERS ============
    '
    ' THE HOLE. RetireSlotAt and the heartbeat's Restamp are load -> verify -> modify -> Save,
    ' and IniFile.Save rewrites the WHOLE file from the in-memory model. A CLI arm that lands
    ' between the load and the Save is therefore silently rolled back - and ArmSlot's confirm
    ' loop protects the arm from LOSING the race, not from winning it and being overwritten
    ' afterwards. The user is then told a block is armed and is shown its ONE-TIME partner code
    ' (the plaintext exists only in that console line) for a slot the service has just deleted:
    ' an UNDER-block whose exit code is gone with it. The re-read at retire step (2) does not
    ' cover this - it makes the SNAPSHOT safe against a racing arm, not the config write.
    '
    ' THE TOKEN IS THE MAC WE ALREADY HAVE. Every legitimate writer re-stamps [Integrity] Mac
    ' over the changed canonical, and an arm always moves MAC-covered fields ([Slots] SlotCount
    ' plus the new slot's own canonical block), so a Mac byte-identical to the one we loaded is
    ' proof that no MAC-covered write landed in between. No new field, no canonical bump, and
    ' nothing to keep in four-copy parity.
    '
    ' NOT A LOCK, deliberately: the house rule (Blocker.ArmAttempts) is that a wedged CLI must
    ' never be able to stall the enforcement tick, so the loser here is always the SERVICE
    ' write, which is idempotent and simply happens on the next 10s tick.
    '
    ' FAIL-CLOSED. Refusing to write leaves a slot armed one tick longer or HighWater one tick
    ' behind - both over-block. It can never lift, shorten or narrow anything, and it never
    ' re-stamps (a refusal writes nothing at all).
    '
    ' HONEST RESIDUAL: this shrinks the exposure from the whole load-modify-Save span to the
    ' Save itself; it does not eliminate it. A write landing between the probe below and
    ' IniFile.Save's rename still wins, and a racing write that touches ONLY non-MAC-covered
    ' keys (the notifier's housekeeping flags) is invisible to this token by construction.
    Friend Shared Function ConfigGenerationToken(ByVal iniPath As String) As String
        Try
            ' An ABSENT file is "changed", not "no MAC": IniFile.Load answers an EMPTY model for
            ' a missing path rather than throwing, and an empty model's Mac ("") would otherwise
            ' compare equal to another "" and wave a write through. Both callers already require
            ' macValid (impossible on an empty ini) before they reach the guard, so this is
            ' belt-and-braces - but the token must not lie about what it can see.
            If Not System.IO.File.Exists(iniPath) Then Return Nothing
            Dim probe As New IniFile
            probe.Load(iniPath)
            Return If(probe.GetKeyValue(IntegritySection, IntegrityMacName), "")
        Catch ex As Exception
            Return Nothing      ' unreadable right now: the caller must treat that as "changed"
        End Try
    End Function

    ' PURE (unit-pinned): is the config still the generation the caller loaded? Nothing on
    ' either side (an unreadable probe) answers False - we never overwrite a file we could not
    ' just read. Ordinal, because this is a Base64 MAC and not text.
    Friend Shared Function ConfigGenerationUnchanged(ByVal loadedToken As String, ByVal currentToken As String) As Boolean
        If loadedToken Is Nothing OrElse currentToken Is Nothing Then Return False
        Return String.Equals(loadedToken, currentToken, StringComparison.Ordinal)
    End Function

    ' TEST SEAM (FX6/F9) - the RetireSaveHookForTests twin, fired inside RestampHeartbeatAt
    ' after its model is built and immediately before the generation probe + Save.
    ' <ThreadStatic>, never assigned in production.
    <ThreadStatic>
    Friend Shared RestampSaveHookForTests As Action(Of String)

    ' The heartbeat's Restamp WRITE, lifted out of timer_Elapsed unchanged except for the two
    ' FX6 additions below, and with the config path made explicit so unit tests drive the REAL
    ' write path against a test-owned file and never the deployed config.
    '
    ' #4 (audit P2->P3) TOCTOU FIX (unchanged): macValid was computed on the tick's EARLIER
    ' read; this RELOADS. A script that swaps a past [Time] Until + stale MAC into the
    ' read->reload window must not get blessed by the re-stamp. Re-validate on the RELOADED
    ' object and only re-stamp if it STILL verifies; otherwise behave as Hold - no re-stamp,
    ' no lift (fail-closed), next tick re-evaluates fresh.
    '
    ' FX6 (F7): `clearOrphanedTimeChanging` lowers an ORPHANED [Time] TimeChanging here, in a
    ' write whose bytes were just re-verified. That restores the cooperation protocol after a
    ' notifier was killed inside its own window - without it the flag stays raised for ever,
    ' is ignored for ever (TimeChangeHoldActive), and the next GENUINE clock change would go
    ' unhonoured. The caller passes True only for a raise that outlived its bound, so this can
    ' never stamp "no" over an episode that is actually in progress. The key is outside the
    ' canonical, so writing it changes no MAC-covered byte.
    '
    ' FX6 (F9): the lost-update guard, exactly as in RetireSlotAt - a MAC-covered write that
    ' landed since our reload (a CLI arm) abandons this save. Skipping a heartbeat costs one
    ' tick of HighWater advance, which OVER-blocks by 10s and converges on the next tick.
    ' Returns True iff the config was rewritten. NEVER throws (it runs inside the tick).
    Friend Function RestampHeartbeatAt(ByVal iniPath As String, ByVal newHw As String, ByVal newTrustedUtc As String, ByVal clearOrphanedTimeChanging As Boolean) As Boolean
        Try
            Dim iniFile = New IniFile
            iniFile.Load(iniPath)
            If Not ConfigMacIsValidForIni(iniFile) Then Return False
            Dim genAtLoad As String = iniFile.GetKeyValue(IntegritySection, IntegrityMacName)
            If clearOrphanedTimeChanging Then iniFile.SetKeyValue("Time", "TimeChanging", "no")
            iniFile.SetKeyValue("CurrentTime", "Now", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
            ' B4: persist the advanced high-water mark in the SAME save as the heartbeat (one
            ' write). newHw is "now" on a Trusted tick and the unchanged stored value on a
            ' jump/rollback (monotonic), so this only ever moves HighWater forward at the real
            ' tick rate. Skip when newHw is "" (a tick that couldn't read it - never blank a
            ' good value).
            If newHw <> "" Then
                iniFile.SetKeyValue("Time", "HighWater", encryptionW.EncryptData(newHw))
            End If
            ' F77: the mark's UTC anchor rides the SAME save as the mark. It has to: the
            ' two are one value in two coordinate systems, and persisting either without
            ' the other leaves the pair inconsistent - an anchor that lagged its mark
            ' would make the next probe re-credit time the ticks already credited.
            ' Skipped on "" by the same rule as newHw (never blank a good value).
            If newTrustedUtc <> "" Then
                iniFile.SetKeyValue("Time", "TrustedUtc", encryptionW.EncryptData(newTrustedUtc))
            End If
            ' The heartbeat just rewrote [CurrentTime] Now AND [Time] HighWater, both
            ' MAC-covered fields, so re-stamp [Integrity] Mac over the new canonical with the
            ' existing key - safe here because the MAC was re-verified just above (the only
            ' changes are ours). Reuses the stored key; never re-arms.
            RestampMacWithExistingKey(iniFile)
            If RestampSaveHookForTests IsNot Nothing Then RestampSaveHookForTests(iniPath)
            If Not ConfigGenerationUnchanged(genAtLoad, ConfigGenerationToken(iniPath)) Then Return False
            iniFile.Save(iniPath)
            ' C1b: the primary is MAC-valid (re-validated above) and freshly saved - refresh the
            ' shadow backup so a later corrupt primary restores to THIS state (current
            ' HighWater/Now), not a stale one. Guarded on the in-memory MAC, so this can never
            ' overwrite the good backup with a bad primary.
            Try
                ConfigBackup.CopyIfSourceValid(iniPath,
                                               System.IO.Path.Combine(System.IO.Path.GetDirectoryName(iniPath), ConfigBackup.BackupFileName),
                                               ConfigMacIsValidForIni(iniFile))
            Catch ex As Exception
            End Try
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' v1.1 S3a: the PER-SLOT window poll - the slot twin of ProcessScheduleWindows, running
    ' the same pure decision (EvaluateWindows -> NextScheduleActiveUntil) once per slot that
    ' carries a rule, and persisting each result through PersistSlotField. Updates the
    ' in-memory SlotState on a successful write so THIS tick's folds/unions see it.
    '
    ' macValid REQUIRED to act (a frozen config never has its schedule state modified or
    ' re-stamped - it is already enforcing). Best-effort per slot: a failed persist leaves
    ' that slot's stored value alone and the next tick re-evaluates.
    '
    ' STILL DEFERRED (past S3b): the issue-#2 lost-update guard the v9 path gets from
    ' ScheduleSpecUnchangedSinceSnapshot has no per-slot equivalent. It stays unreachable -
    ' no CLI writes a slot ScheduleSpec (WriteSlotSection always writes ""), so no writer can
    ' change one under this tick - and PersistSlotField's Id re-locate already stops the write
    ' landing in the wrong section. It becomes reachable only when `schedule` becomes a slot.
    '
    ' S3b (carried finding 6): a FAILED persist no longer narrows this tick's unions. The
    ' in-memory value now follows the OVER-BLOCKING side regardless of whether the write
    ' landed: an OPEN/EXTEND (target <> "") is adopted even if the persist failed, so the
    ' slot's sites stay in the hosts/kill unions for this tick rather than being briefly
    ' stripped by a self-heal that then has nothing to put them back; a CLEAR (target = "")
    ' is adopted ONLY if it persisted, so a lost write leaves the window open one more tick.
    ' Both directions cost at most one 10s tick and both err towards MORE blocked.
    Private Sub ProcessSlotScheduleWindows(ByVal slots As List(Of SlotState), ByVal lastNowText As String, ByVal nowText As String, ByVal newHwText As String, ByVal monoElapsedSeconds As Long, ByVal macValid As Boolean, ByVal isBoot As Boolean)
        Try
            If Not macValid OrElse slots Is Nothing Then Return
            For Each s As SlotState In slots
                ' Fast path: no rule and no open window - nothing to evaluate or clear.
                If s.ScheduleSpec = "" AndAlso s.ScheduleActiveUntil = "" Then Continue For
                Dim openNow As List(Of ScheduleOpen) = EvaluateWindows(ParseSchedule(s.ScheduleSpec).Windows, lastNowText, nowText, monoElapsedSeconds, isBoot)
                Dim target As String = NextScheduleActiveUntil(s.ScheduleActiveUntil, openNow, newHwText)
                If target <> s.ScheduleActiveUntil Then
                    Dim persisted As Boolean = PersistSlotField(s.Id, "ScheduleActiveUntil", target, True)
                    If persisted OrElse target <> "" Then s.ScheduleActiveUntil = target
                End If
            Next
        Catch ex As Exception
        End Try
    End Sub

    ' P29 live wiring: turn every PENDING slot whose start moment has arrived into an ACTIVE
    ' one. The SERVICE computes Until = HighWater + DurationSeconds (ComputeSlotActivationUntil)
    ' and persists it through the one per-slot writer; the CLI never stores an absolute end for
    ' a `--start` block, because the wall clock runs on while the machine is off and an absolute
    ' end would therefore UNDER-block after downtime.
    '
    ' Carried finding 4 (the activation/union interaction): activation does NOT change union
    ' membership, by design. SlotContributesLists is SlotEnforcesNow OrElse SlotIsPending, so a
    ' pending slot's sites are ALREADY in the hosts union, the kill union and the P37 snapshot
    ' truth - the CLI wrote them into hosts and into the snapshot at arm time. Activation just
    ' moves the slot from the PENDING arm of that OR to the ACTIVE arm; the entries never leave
    ' the unions for even one tick, so the "stripped with nothing able to re-add them" bug S3a
    ' caught cannot reappear here. Narrowing SlotContributesLists to SlotEnforcesNow would only
    ' be safe if the arm-time hosts write moved to activation time too - it has NOT, so it must
    ' NOT be narrowed. A pending slot therefore over-blocks from arm until its start; that is a
    ' known, accepted over-block, and it is the fail-closed side.
    '
    ' In-memory state follows the PERSIST here (unlike the window step above): an un-persisted
    ' activation must not read as started, or the next tick would re-activate off a LATER
    ' HighWater and hand the block a longer run each time. A failed persist simply retries next
    ' tick - the slot stays PENDING, stays in every union, and its start is delayed by <= 10s,
    ' which lengthens the block rather than shortening it. Never throws.
    Private Sub ActivateDueSlots(ByVal slots As List(Of SlotState), ByVal highWaterText As String, ByVal macValid As Boolean)
        ActivateDueSlotsAt(Application.StartupPath + "\monkmode_settings.ini", slots, highWaterText, macValid)
    End Sub

    ' The testable core with the config path made explicit (the PersistSlotFieldAt pattern).
    Friend Sub ActivateDueSlotsAt(ByVal iniPath As String, ByVal slots As List(Of SlotState), ByVal highWaterText As String, ByVal macValid As Boolean)
        Try
            If Not macValid OrElse slots Is Nothing Then Return
            For Each s As SlotState In slots
                If Not SlotStartDue(s, highWaterText) Then Continue For
                Dim untilText As String = ComputeSlotActivationUntil(highWaterText, s.DurationSeconds)
                If untilText = "" Then Continue For      ' fail-closed: stays PENDING, retried next tick
                If PersistSlotFieldAt(iniPath, s.Id, "Until", untilText, True) Then s.UntilText = untilText
            Next
        Catch ex As Exception
        End Try
    End Sub

    ' Retire every slot whose own exit is due this tick, and report how many went. Each
    ' RetireSlot re-reads the config for itself and addresses the slot by ID, so consecutive
    ' retires compose correctly even though every one of them renumbers the positions. The
    ' iteration is over a COPY because RetireSlot mutates the file the list came from.
    ' macValid REQUIRED: a frozen config retires nothing (it is already enforcing). Returns 0
    ' and changes nothing on any failure. Never throws.
    Private Function RetireDueSlots(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal macValid As Boolean, ByVal highWaterText As String) As Integer
        Dim retired As Integer = 0
        Try
            If Not macValid OrElse slots Is Nothing Then Return 0
            For Each s As SlotState In New List(Of SlotState)(slots)
                If SlotExitDue(s, asOf, ExpiryGraceSeconds, macValid, highWaterText) <> SlotAction.Retire Then Continue For
                If RetireSlot(s.Id) Then retired += 1
            Next
        Catch ex As Exception
        End Try
        Return retired
    End Function

    ' ================= P38: RETIRE ONE SLOT, WITHOUT DISTURBING THE OTHERS =================
    '
    ' THE ORDER IS THE SAFETY PROPERTY, and it is:
    '     (1) CONFIG   - compact the slot out, RemoveSection the freed trailing position,
    '                    restamp SlotCount/Guard.ArmedCount, re-stamp the MAC, Save, refresh
    '                    the C1b backup;
    '     (2) SNAPSHOT - recompute monkmode_hosts.block from CONFIG TRUTH (re-read from disk,
    '                    never from the tick's in-memory list);
    '     (3) HOSTS    - rewrite the marker block to that same truth.
    ' Snapshot-before-config is FORBIDDEN: the snapshot is the MAC-INDEPENDENT repair source
    ' every self-heal reads, so narrowing it before the config agrees would let one tick strip
    ' a still-armed slot's sites out of the only place that could put them back.
    '
    ' EVERY CRASH POINT OVER-BLOCKS:
    '   * before (1): nothing happened at all. The slot is still armed, hosts still blocks it,
    '     and the next tick re-classifies it Retire and starts over. Idempotent.
    '   * between (1) and (2): the config no longer carries the slot, but the snapshot and
    '     hosts still block its sites. Over-block. The next tick's P37 reconcile recomputes
    '     the snapshot from the (already correct) config - and note the reverse order would
    '     leave a NARROWED snapshot with the slot still armed, which is an UNDER-block.
    '   * between (2) and (3): the snapshot is truth, hosts is still wide. Over-block.
    '   * after (3): done.
    ' A crash can therefore never end a block early, only end it late.
    '
    ' HOW LATE, HONESTLY. Hosts is NOT guaranteed to converge on the next tick. The per-tick
    ' B2 self-heal goes through RepairHostsBlock, which returns Nothing whenever hosts already
    ' CONTAINS the expected block - the exact property ExactHostsRewrite exists to escape. So
    ' when the survivors' entry lines happen to be a contiguous substring of the wide block
    ' (retiring the LAST slot in position order), a crashed retire leaves the retired slot's
    ' sites blocked until the next retire or the whole-machine teardown. That is a rare double
    ' fault (a crash inside the retire, AND that geometry) and it is a pure OVER-block, so it
    ' is carried rather than fixed: making the hot-path self-heal compute an exact target every
    ' 10s would trade a permanent per-tick cost against a fault that only over-blocks. The
    ' non-crash path is exact, because step (3) uses ExactHostsRewrite.
    '
    ' WHAT IS DELIBERATELY NOT SHORTENED. [Slots] NextSlotId is never lowered (P17 - ids are
    ' monotone and never reused, so a replayed monkmode_partner.code.<id> can never come to
    ' address a different block). [Guard] HoldUntil is EXTEND-ONLY (P43) - only TeardownAll
    ' clears it, so the guardian over-guards rather than standing down mid-block. The v9
    ' [Time] Until mirror is likewise left at the guard horizon: it can no longer LIFT
    ' anything (ClassifyTick demoted it to a hold), so leaving it long only over-blocks.
    Friend Function RetireSlot(ByVal slotId As String) As Boolean
        Return RetireSlotAt(Application.StartupPath + "\monkmode_settings.ini",
                            Application.StartupPath + "\monkmode_hosts.block",
                            hostDirS, slotId)
    End Function

    ' TEST SEAM (FX5/F2) - the SetAttrHookForTests / AtomicHosts.RenameHookForTests pattern.
    ' When set, RetireSlotAt calls this with the config path at the ONE instant that matters:
    ' after its own Save, immediately before the re-read that computes hosts truth. That is
    ' the window a raw forged write has to land in, and it cannot be staged from outside a
    ' single-threaded test any other way. <ThreadStatic> so it is confined to the test thread
    ' that sets it; PRODUCTION never assigns it, so the field stays Nothing and the retire is
    ' behaviourally unchanged. Friend, so only the in-repo test assembly can see it.
    <ThreadStatic>
    Friend Shared RetireReloadHookForTests As Action(Of String)

    ' TEST SEAM (FX6/F9) - the sibling of the hook above, at the OTHER instant that matters:
    ' after the retire has built its whole-file model, immediately before the generation probe
    ' and the Save. That is the window a racing CLI arm has, and staging it from outside a
    ' single-threaded test is not otherwise possible. <ThreadStatic>, never assigned in
    ' PRODUCTION (the field stays Nothing and the retire is behaviourally unchanged), Friend so
    ' only the in-repo test assembly can see it.
    <ThreadStatic>
    Friend Shared RetireSaveHookForTests As Action(Of String)

    ' The testable core with every path made explicit (the PersistSlotFieldAt pattern), so the
    ' retire matrix and the crash-point ordering tests drive the REAL code against test-owned
    ' files and never the deployed config or the live hosts file. Returns True iff the config
    ' was rewritten. NEVER throws (it runs inside the tick).
    Friend Function RetireSlotAt(ByVal iniPath As String, ByVal snapshotPath As String, ByVal hostsPath As String, ByVal slotId As String) As Boolean
        Try
            ' ---------------- (1) CONFIG ----------------
            Dim iniFile = New IniFile
            iniFile.Load(iniPath)
            ' A frozen config is never retired FROM: re-stamping over unverified bytes is the
            ' B7 fail-open bug, and a tampered config is already enforcing (Hold).
            If Not ConfigMacIsValidForIni(iniFile) Then Return False
            ' FX6 (F9): the generation this whole-file rewrite is derived from, captured BEFORE
            ' RestampMacWithExistingKey changes the in-memory Mac. Re-checked against the file
            ' immediately before the Save below.
            Dim genAtLoad As String = iniFile.GetKeyValue(IntegritySection, IntegrityMacName)
            Dim count As Integer = ConfigIntegrity.ParseSlotCount(iniFile.GetKeyValue("Slots", "SlotCount"))
            If count <= 0 Then Return False
            Dim pos As Integer = FindSlotPositionById(iniFile, slotId)
            If pos = 0 Then Return False        ' already retired, or never here: write NOTHING

            ' Compact: every later slot slides down one position, then the freed TRAILING
            ' section is removed outright. Removed, never flagged - a retired slot must leave
            ' no state behind that a later reader could resurrect.
            For p As Integer = pos To count - 1
                CopySlotSection(iniFile, p + 1, p)
            Next
            iniFile.RemoveSection("Slot" & count.ToString(CultureInfo.InvariantCulture))

            Dim remaining As Integer = count - 1
            iniFile.SetKeyValue("Slots", "SlotCount", remaining.ToString(CultureInfo.InvariantCulture))
            ' THE WEDGE THIS AVOIDS: the v9 [Time] Until mirror is the EXTEND-ONLY guard
            ' horizon, i.e. the latest moment any slot COULD have held - so a slot armed
            ' `--start +30d --for 1h` and then retired early by a partner code would leave the
            ' mirror 30 days in the future. ClassifyTick consults the residual once the slot
            ' set empties, so that future Until would HOLD the teardown back and the machine
            ' would sit with hosts blocked and nothing armed for a month. The moment the last
            ' slot goes, every block has genuinely exited and the mirror has no authority left
            ' to represent, so it is neutralised here exactly as TeardownAll neutralises it.
            ' FX3 (F3): macValid:=True is not an assumption - this retire returned False above
            ' unless the config verified, and nothing since has changed the bytes' provenance.
            ' The residual clear is now conditional: a GENUINELY ARMED schedule (or an open
            ' window) keeps its [Schedule] pair, so the last slot leaving can never take a
            ' schedule down with it. ClassifyTick then reads zero slots + a Restamp residual
            ' (the c2 between-windows hold) and keeps the machine up, exactly as it does for a
            ' schedule-only config - which is precisely the shape left behind here.
            If remaining = 0 Then NeutraliseV9Residual(iniFile, True)
            ' The guard hold is recomputed from the slots that remain PLUS the surviving global
            ' schedule (FX3), so a schedule that outlives every slot still holds the guardian.
            iniFile.SetKeyValue("Guard", "ArmedCount", CountGuardedSlots(iniFile, remaining).ToString(CultureInfo.InvariantCulture))
            ' The v9 list mirror is a PROJECTION of the slot set (the CLI maintains it at every
            ' arm), and retire is the inverse of arm: without this, a finished 1-hour block's
            ' apps would keep being killed for the whole life of an unrelated 30-day one - the
            ' service's own kill loop AND the notifier's both take [Process] List as their base.
            ' Narrowing it here is narrowing to CONFIG TRUTH, and the slot union still adds
            ' every remaining slot's apps independently, so no remaining block loses a kill.
            RefreshV9ListMirror(iniFile, remaining)
            RestampMacWithExistingKey(iniFile)
            ' RetireSaveHookForTests is Nothing in production - this is a plain fall-through;
            ' the hook only lets a test stage a racing arm into this exact window.
            If RetireSaveHookForTests IsNot Nothing Then RetireSaveHookForTests(iniPath)
            ' FX6 (F9): LOST-UPDATE GUARD. Abandon the whole-file rewrite if another writer's
            ' MAC-covered write (a CLI arm appending a slot) landed since our load - saving here
            ' would delete a slot the user has already been told is armed, and burn its one-time
            ' partner code with it. Returning False is the documented "the config was not
            ' rewritten": nothing is stamped, the slot stays armed and the next tick retires it.
            If Not ConfigGenerationUnchanged(genAtLoad, ConfigGenerationToken(iniPath)) Then Return False
            iniFile.Save(iniPath)
            Try
                ConfigBackup.CopyIfSourceValid(iniPath,
                                               System.IO.Path.Combine(System.IO.Path.GetDirectoryName(iniPath), ConfigBackup.BackupFileName),
                                               ConfigMacIsValidForIni(iniFile))
            Catch ex As Exception
            End Try

            ' ------------- (2) SNAPSHOT, from CONFIG TRUTH -------------
            ' Re-read from disk deliberately: the caller's in-memory slot list is what a racing
            ' CLI arm would NOT be in, so recomputing from the file is what makes an arm landing
            ' during a retire safe - the arm's slot is on disk and is therefore in this union.
            Dim after As New IniFile
            ' RetireReloadHookForTests is Nothing in production - this is a plain re-read;
            ' the hook only lets a test stage a forged write into this exact window.
            If RetireReloadHookForTests IsNot Nothing Then RetireReloadHookForTests(iniPath)
            after.Load(iniPath)
            ' FX5 (F2): TOCTOU RE-VALIDATION of the re-read, the PersistSlotFieldAt discipline
            ' (:996) this path was missing. These are FRESH bytes off disk, not the ones
            ' verified at the top of the function, so `macValid:=True` below was an assumption,
            ' not a fact: a forged config landing between the Save above and this Load was
            ' folded into truthSites as if it verified, and a slot-Sites-blanked forgery
            ' therefore NARROWED the truth. That matters here more than anywhere else, because
            ' step (2) rewrites the SNAPSHOT - the MAC-INDEPENDENT repair source every B2
            ' self-heal reads - so the narrowing would survive the freeze and keep a
            ' still-armed slot's sites unblocked for the whole life of the frozen block.
            ' An unverifiable re-read therefore reconciles NOTHING: the config retire above
            ' genuinely landed (True), and the snapshot and hosts simply stay WIDE - exactly
            ' the documented "crash between (1) and (2)" state, which over-blocks and
            ' converges on the next tick that sees a valid config.
            If Not ConfigMacIsValidForIni(after) Then Return True
            Dim highWater As String = ""
            Try
                Dim hwEnc As String = after.GetKeyValue("Time", "HighWater")
                highWater = If(hwEnc = "", "", encryptionW.DecryptData(hwEnc))
            Catch ex As Exception
                highWater = ""
            End Try
            Dim asOf As DateTime = DateTime.MinValue
            Dim parsedHw As DateTime
            If DateTime.TryParse(highWater, culture, DateTimeStyles.None, parsedHw) Then asOf = parsedHw
            Dim truthSites As List(Of String) = UnionSlotSites(LoadSlots(after), asOf, ExpiryGraceSeconds, True, highWater)
            ' R1: with nothing left to block, LEAVE the snapshot and hosts exactly as they are.
            ' Blanking a snapshot is what a torn-down block looks like, and the remaining slots
            ' (an app-only or pending one) are still armed - so the honest choice is the
            ' over-blocking one, and the whole-machine teardown is what removes the entries.
            If truthSites.Count > 0 Then
                Dim entries As String = BuildHostsEntries(truthSites)
                If entries <> "" Then
                    Dim expected As String = HostsMarker & vbCrLf & entries
                    ReconcileHostsSnapshot(snapshotPath, True, truthSites)
                    ' ---------------- (3) HOSTS ----------------
                    WriteHostsMarkerBlock(hostsPath, expected)
                End If
            End If
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' Copy all 16 of a slot's keys from one position to another (the compaction step). Every
    ' field is moved - SlotFieldNames is pinned equal to the canonical's line set, so a field
    ' can never be silently dropped and leave the block MAC-stamped over a value it lost.
    Private Shared Sub CopySlotSection(ByVal ini As IniFile, ByVal fromPos As Integer, ByVal toPos As Integer)
        Dim src As String = "Slot" & fromPos.ToString(CultureInfo.InvariantCulture)
        Dim dst As String = "Slot" & toPos.ToString(CultureInfo.InvariantCulture)
        ini.AddSection(dst)
        For Each key As String In SlotFieldNames
            ini.SetKeyValue(dst, key, ini.GetKeyValue(src, key))
        Next
    End Sub

    ' [Guard] ArmedCount: the slots that keep the guardian alive WITHOUT an open block of their
    ' own - SCHEDULE slots (a rule waiting for its next window) and PENDING slots (StartAt set,
    ' Until not yet computed). Byte-parity in SEMANTICS with the CLI's GuardedSlotCount, which
    ' recomputes the same value at every arm; both read the RAW stored strings, so neither
    ' needs to decrypt to count.
    Private Shared Function CountGuardedSlots(ByVal ini As IniFile, ByVal slotCount As Integer) As Integer
        Dim n As Integer = 0
        For pos As Integer = 1 To slotCount
            Dim sec As String = "Slot" & pos.ToString(CultureInfo.InvariantCulture)
            Dim spec As String = If(ini.GetKeyValue(sec, "ScheduleSpec"), "")
            Dim startAt As String = If(ini.GetKeyValue(sec, "StartAt"), "")
            Dim untilText As String = If(ini.GetKeyValue(sec, "Until"), "")
            If spec <> "" OrElse (startAt <> "" AndAlso untilText = "") Then n += 1
        Next
        ' FX3 (F3): the GLOBAL [Schedule] Spec counts too - the CLI's GuardedSlotCount gained
        ' the identical term, so retiring a slot can never zero out the guardian hold that a
        ' surviving schedule owns. Same non-empty over-approximation the guardian itself uses:
        ' it can only ever ADD a hold, never drop one.
        If Not String.IsNullOrWhiteSpace(ini.GetKeyValue("Schedule", "Spec")) Then n += 1
        Return n
    End Function

    ' Recompute the v9 single-block list mirror as the union over the slots that remain, in the
    ' CLI's exact encoding (apps encrypted with the "null" no-apps sentinel; sites plaintext).
    ' Both keys sit OUTSIDE the v10 canonical, so this does not itself move the MAC - the
    ' re-stamp that follows is for the SlotCount/ArmedCount/section changes.
    Private Sub RefreshV9ListMirror(ByVal ini As IniFile, ByVal slotCount As Integer)
        Dim sites As String = PackedSlotUnion(ini, slotCount, "Sites")
        Dim apps As String = PackedSlotUnion(ini, slotCount, "Apps")
        ini.AddSection("Process")
        ini.SetKeyValue("Process", "List", If(apps = "", "null", encryptionW.EncryptData(apps)))
        ini.AddSection("User")
        ini.SetKeyValue("User", "CustomSites", If(sites = "", "null", sites))
        ' [Commit] Committed is the OR-LATCH the CLI arm maintains ("yes if ANY slot is"), and
        ' a latch that only ever goes on is wrong once slots start leaving. The CLI's exit gate
        ' reads it, so a committed 1h block retiring beside an uncommitted 30d one would keep
        ' refusing the survivor's cooling-off - "This block is COMMITTED" - for the whole 30
        ' days, locking the user out of an exit they are entitled to until the next arm
        ' happened to recompute it. Same union pattern as the two lists above: recompute from
        ' the slots that remain. (The SERVICE is already correct - it reads each slot's own
        ' Committed - so this is purely repairing what the CLI gate consumes.)
        Dim anyCommitted As Boolean = False
        For pos As Integer = 1 To slotCount
            If IsCommitted(ini.GetKeyValue("Slot" & pos.ToString(CultureInfo.InvariantCulture), "Committed")) Then
                anyCommitted = True
                Exit For
            End If
        Next
        ini.AddSection("Commit")
        ini.SetKeyValue("Commit", "Committed", If(anyCommitted, "yes", "no"))
    End Sub

    ' The ";"-packed union of one plaintext list key across the remaining slots, first-
    ' occurrence order, deduped case-insensitively - the same shape (and trailing ";") the
    ' CLI's UnionSlotList produces, so a mirror written here is indistinguishable from one
    ' written at arm.
    Private Shared Function PackedSlotUnion(ByVal ini As IniFile, ByVal slotCount As Integer, ByVal key As String) As String
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim parts As New List(Of String)
        For pos As Integer = 1 To slotCount
            For Each tok As String In If(ini.GetKeyValue("Slot" & pos.ToString(CultureInfo.InvariantCulture), key), "").Split(";"c)
                Dim s As String = tok.Trim()
                If s <> "" AndAlso seen.Add(s) Then parts.Add(s)
            Next
        Next
        If parts.Count = 0 Then Return ""
        Return String.Join(";", parts) & ";"
    End Function

    ' The EXACT hosts target for a retire, as opposed to RepairHostsBlock's restore-only
    ' semantics. RepairHostsBlock returns Nothing whenever hosts already CONTAINS the expected
    ' text, which is right for a self-heal (never churn) but wrong for a SHRINK: retiring the
    ' last slot in position order leaves the remaining entries as a literal prefix of what is
    ' already in hosts, so the repair would no-op and the retired block's sites would stay
    ' blocked until teardown. This computes the exact desired file instead, and returns Nothing
    ' only when hosts is ALREADY exactly that. Data-loss safe by the same rule as every other
    ' writer: StripMonkModeBlock touches only our marker block. Pure + Shared.
    Friend Shared Function ExactHostsRewrite(ByVal hostsText As String, ByVal expectedBlock As String) As String
        If String.IsNullOrWhiteSpace(expectedBlock) Then Return Nothing      ' never invent content
        If hostsText Is Nothing Then hostsText = ""
        ' F35: above and below are BOTH the user's. The block is re-seated between them,
        ' end-markered, so the next strip knows where it stops.
        Dim block As String = EnsureBlockEndMarker(expectedBlock)
        Dim userContent As String = HostsAboveBlock(hostsText)
        Dim below As String = HostsBelowBlock(hostsText)
        Dim desired As String = AppendUserTail(If(userContent.Length = 0, block, userContent & vbCrLf & block), below)
        If String.Equals(desired, hostsText, StringComparison.Ordinal) Then Return Nothing
        Return desired
    End Function

    ' The thin file wrapper: clear read-only, write atomically, and ALWAYS re-assert read-only
    ' in a Finally - a writable hosts is the fail-OPEN state the DNS client would read around.
    ' Best-effort; never throws.
    Friend Shared Sub WriteHostsMarkerBlock(ByVal hostsPath As String, ByVal expectedBlock As String)
        Try
            Dim hostsText As String = ""
            If System.IO.File.Exists(hostsPath) Then hostsText = System.IO.File.ReadAllText(hostsPath)
            Dim desired As String = ExactHostsRewrite(hostsText, expectedBlock)
            If desired Is Nothing Then Return
            If System.IO.File.Exists(hostsPath) Then SetAttr(hostsPath, vbNormal)
            Try
                AtomicHosts.WriteAtomic(hostsPath, desired)
            Finally
                Try
                    If System.IO.File.Exists(hostsPath) Then SetAttr(hostsPath, vbReadOnly)
                Catch ex As Exception
                End Try
            End Try
        Catch ex As Exception
        End Try
    End Sub

    ' ================= P39: THE WHOLE-MACHINE TEARDOWN =================
    '
    ' Fires ONLY from ClassifyTick's TeardownAll arm, i.e. only at SlotCount = 0 with the v9
    ' residual also exited. The ORDER DELIBERATELY INVERTS the pre-S3b one (which stripped
    ' hosts first and marked the config afterwards):
    '     (1) persist the ZERO-SLOT config;
    '     (2) delete the hosts snapshot;
    '     (3) the existing stopMe() body, from the hosts strip onward.
    ' Crash points:
    '   * before (1): nothing changed; the next tick re-decides TeardownAll and starts over.
    '   * between (1) and (2): the config says nothing is armed, but hosts is still blocked
    '     and the snapshot still exists. OVER-block. The next tick reads a zero-slot config
    '     with an exited residual, classifies TeardownAll again and runs to completion.
    '   * between (2) and (3): hosts still blocked, snapshot gone - still an over-block, and
    '     still convergent for the same reason.
    ' The OLD order had the fatal window the other way round: hosts stripped, config still
    ' armed, so the next tick's B2 self-heal RESURRECTED a torn-down block from the snapshot.
    ' Nothing here can under-block, and nothing can wedge, because every intermediate state
    ' re-enters TeardownAll.
    '
    ' (1) also clears the v9 residual's holding fields. Since v11 (FX1) two of them - the
    ' [Schedule] pair - ARE canonical fields, so the re-stamp below is load-bearing for them
    ' as well as for the slot/SlotCount changes; the point of clearing them is unchanged.
    ' Leaving a future [Time] Until or an armed
    ' [Schedule] Spec behind would make the next tick's residual HOLD and a half-finished
    ' teardown could then never complete (hosts blocked forever with nothing armed). This is
    ' also what S4 needs: the guardian's raw floor must see zero slot keys AND no v9 hold, or
    ' it never stands down.
    Friend Function PersistZeroSlotConfigAt(ByVal iniPath As String) As Boolean
        Try
            Dim iniFile = New IniFile
            iniFile.Load(iniPath)
            Dim macValid As Boolean = ConfigMacIsValidForIni(iniFile)
            For p As Integer = 1 To ConfigIntegrity.MaxSlots
                iniFile.RemoveSection("Slot" & p.ToString(CultureInfo.InvariantCulture))
            Next
            iniFile.AddSection("Slots")
            iniFile.SetKeyValue("Slots", "SlotCount", "0")
            ' NextSlotId is NOT reset (P17: ids never restart, even across a teardown).
            iniFile.AddSection("Guard")
            iniFile.SetKeyValue("Guard", "HoldUntil", "")
            ' The v9 residual, neutralised so an interrupted teardown always converges.
            ' FX3 (F3): the [Schedule] half of that clear is now conditional on the schedule
            ' being SPENT. Unchanged in practice on this path - TeardownAll is only ever
            ' reached through ClassifyTick's Lift arm, which requires scheduleArmed=False AND
            ' scheduleActive=False, i.e. exactly "spent" - so the pair is still cleared and
            ' the teardown still converges. What it removes is the possibility of this sub
            ' being the thing that ends a live schedule if it is ever called from anywhere
            ' else. ArmedCount is therefore derived from what SURVIVED rather than hardcoded
            ' to 0, so the two can never disagree about whether a schedule still holds.
            NeutraliseV9Residual(iniFile, macValid)
            iniFile.SetKeyValue("Guard", "ArmedCount", CountGuardedSlots(iniFile, 0).ToString(CultureInfo.InvariantCulture))
            iniFile.SetKeyValue("Process", "List", "null")
            iniFile.SetKeyValue("User", "CustomSites", "null")
            ' B7: only ever re-stamp bytes just verified. Unreachable with an invalid MAC
            ' (ClassifyTick Holds), and if it were reached the stale MAC would freeze the
            ' config rather than bless it - the fail-closed side.
            If macValid Then RestampMacWithExistingKey(iniFile)
            iniFile.Save(iniPath)
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' FX3 (F3) - PURE: may the [Schedule] pair be cleared, i.e. is the schedule SPENT? Only
    ' when it is neither ARMED (a Spec that still parses to >=1 window, under a valid MAC -
    ' tomorrow's windows are still owed) nor ACTIVE (a window open right now, which SD1 says
    ' outranks every exit). Anything else - a live schedule, an open window, an unreadable
    ' ActiveUntil - is NOT spent and its two fields are left exactly as they are. The two
    ' halves are judged as ONE unit because they are one schedule: clearing either alone
    ' would be a partial teardown of something still holding. Fail-closed by inheritance:
    ' ScheduleActive treats a non-empty-but-unparseable deadline as OPEN, so "we could not
    ' read it" can never mean "it is over".
    Friend Shared Function ScheduleResidualIsSpent(ByVal macValid As Boolean, ByVal specText As String,
                                                   ByVal activeUntilText As String, ByVal highWaterText As String) As Boolean
        Return (Not ScheduleArmed(macValid, specText)) AndAlso (Not ScheduleActive(activeUntilText, highWaterText))
    End Function

    ' Clear every v9 mirror field that can HOLD (the residual's four inputs). [Time] Until
    ' and [Time] CoolOffUntil sit OUTSIDE the canonical; the [Schedule] pair is INSIDE it as
    ' of v11 (FX1), so this DOES move the MAC and the caller's re-stamp - which runs AFTER
    ' this, and only when the config was already MAC-valid - is what keeps the torn-down
    ' config verifiable rather than frozen. Called at exactly the two moments the mirror stops
    ' representing anything: the last slot retiring, and the whole-machine teardown. Never
    ' called while a slot survives, because until then the mirror's job is to over-block.
    Private Sub NeutraliseV9Residual(ByVal ini As IniFile, ByVal macValid As Boolean)
        ini.SetKeyValue("Time", "Until", encryptionW.EncryptData(ScheduleOnlyExpiredUntil))
        ini.SetKeyValue("Time", "CoolOffUntil", "")
        ' FX3 (F3): the [Schedule] pair is cleared ONLY when the schedule is spent. It used to
        ' be blanked unconditionally, which was right for the whole-machine teardown (reached
        ' only with the schedule already down) but WRONG for the last slot retiring: a
        ' `monkmode block` armed beside a schedule (S2 dropped that refusal - restored in
        ' FX3) took its Spec down with it when it ended, and if a scheduled window was open
        ' at that moment ScheduleActive went False, enforcementHeld collapsed and the next
        ' tick tore the machine down MID-WINDOW. The CLI now refuses to create that state at
        ' all; this is the belt to that braces, for a config that reached it by crash timing.
        Dim spec As String = If(ini.GetKeyValue("Schedule", "Spec"), "")
        Dim activeUntil As String = ""
        Dim highWater As String = ""
        ' Both stored encrypted. A decrypt failure keeps the CIPHERTEXT as the value: non-empty
        ' and unparseable, which ScheduleActive reads as an OPEN window => not spent => keep.
        Try
            Dim rawActive As String = ini.GetKeyValue("Schedule", "ActiveUntil")
            activeUntil = If(rawActive = "", "", encryptionW.DecryptData(rawActive))
        Catch ex As Exception
            activeUntil = If(ini.GetKeyValue("Schedule", "ActiveUntil"), "")
        End Try
        Try
            Dim rawHw As String = ini.GetKeyValue("Time", "HighWater")
            highWater = If(rawHw = "", "", encryptionW.DecryptData(rawHw))
        Catch ex As Exception
            highWater = ""
        End Try
        If ScheduleResidualIsSpent(macValid, spec, activeUntil, highWater) Then
            ini.SetKeyValue("Schedule", "Spec", "")
            ini.SetKeyValue("Schedule", "ActiveUntil", "")
        End If
    End Sub

    Private Sub TeardownAll()
        PersistZeroSlotConfigAt(Application.StartupPath + "\monkmode_settings.ini")
        Try
            System.IO.File.Delete(Application.StartupPath + "\monkmode_hosts.block")
        Catch ex As Exception
        End Try
        stopMe()
    End Sub

    ' B4 creep fix: a MONOTONIC anchor (Environment.TickCount64, ms since boot -
    ' immune to wall-clock changes) captured at the last HighWater advance. The
    ' per-tick HighWater credit is capped by the real elapsed since this anchor, so
    ' nudging the wall clock forward each tick can't advance the mark faster than
    ' real time. Seeded at OnStart; 0 = not yet seeded (=> credit 0 that tick).
    Private lastMonoMs As Long = 0

    ' F77: the background trusted-time probe. One per service instance, deliberately
    ' NOT static - it holds nothing worth surviving a restart, and a fresh instance at
    ' every OnStart means a reboot always probes immediately (which is exactly the
    ' moment a downtime credit is owed).
    Private ReadOnly trustedProbe As New TrustedTimeProbe()

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
        ' v1.1 S3a: the SLOTS as this tick sees them - the enforcement truth the folds and
        ' unions below read INSTEAD of the v9 mirror keys (which sit outside the MAC-covered
        ' canonical and were therefore raw-editable under a valid MAC). Empty is the
        ' fail-closed default: with no readable slots every gate falls back on the v9
        ' disjunct it always had, so this can never REMOVE enforcement, only add.
        Dim slots As New List(Of SlotState)
        Dim prevTickWallNow As String = ""
        Dim tickWallNow As String = ""
        Dim monoElapsedSeconds As Long = 0
        ' v1.1 S3b: the v9 machine-wide [Commit] Committed read is GONE from the tick. The
        ' cooling-off poll is slot-addressed now and reads each slot's OWN MAC-covered
        ' Committed flag, so a machine-wide one had no consumer left - and keeping a dead read
        ' of an enforcement field would misrepresent what the tick actually adjudicates on.
        ' The CLI still maintains the v9 key ("yes if ANY slot is") for its `unblock` warning.
        ' D2c: whether THIS block kills blocked apps in EVERY session (not just session 0).
        ' Default FALSE = the current session-0-only kill = fail-safe: a tick that couldn't read
        ' the flag never widens (the block still holds - hosts stay locked, the deadline never
        ' lifts - and the notifier still covers the interactive session). Read raw (NOT macValid-
        ' gated): a widen-only union that can never REMOVE a kill, so a tampered "yes" (which also
        ' freezes the block) only ever ADDS kills - matching the schedule app-kill union's stance.
        Dim iniAllSessionKill As Boolean = False
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
        ' F77: the mark's UTC anchor to persist alongside newHw this tick. "" means
        ' "nothing to persist" by the same rule as newHw - a tick that couldn't read the
        ' config never blanks a good anchor.
        Dim newTrustedUtc As String = ""

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
            iniProcessList = iniFile.GetKeyValue("Process", "List")
            If StrComp("null", iniProcessList) <> 0 Then
                iniProcessList = encryptionW.DecryptData(iniProcessList)
            End If
            ' D2c: read the [Process] AllSession all-session-app-kill flag ("yes" = widen the kill
            ' loop below from session 0 to EVERY session). Plaintext-as-stored (MAC-covered, not
            ' decrypted), read raw like the schedule union (widen-only, never removes a kill).
            iniAllSessionKill = AllSessionKillArmed(iniFile.GetKeyValue("Process", "AllSession"))
            ' B7: evaluate the tamper-evident MAC (DPAPI-unprotect [Integrity]
            ' Key, validate [Integrity] Mac over the canonical). Invalid/absent
            ' MAC or a DPAPI failure -> False -> block stays standing.
            macValid = ConfigMacIsValidForIni(iniFile)
            ' v1.1 S3a: read the slots (decrypting their four datetimes) now the ini is
            ' loaded. A garbled slot ciphertext throws into the Catch below - fail-closed,
            ' the same discipline the [Schedule] ActiveUntil read follows.
            slots = LoadSlots(iniFile)
            ' B4: advance the monotonic high-water mark. Read the stored value
            ' (decrypted), then NextHighWater advances it to "now" ONLY if the
            ' advance is a Trusted real tick; a clock-forward jump or a backward
            ' roll leaves it unchanged, so a rolled clock can never carry it past
            ' Until. EVERY expiry/self-heal decision below uses newHwAsOf (the
            ' parsed HighWater) as asOf instead of DateTime.Now - that is the
            ' whole B4 fix. The new value is persisted in the heartbeat save
            ' below (one save) so it advances each live tick.
            Dim storedHw As String = encryptionW.DecryptData(iniFile.GetKeyValue("Time", "HighWater"))
            ' F77: the mark's stored UTC anchor. "" (absent, or a decrypt that returns ""
            ' on bad Base64) means no anchor => no downtime credit is computable this
            ' tick, only a re-seed if a reading happens to be in hand - fail-closed.
            Dim storedTrustedUtcEnc As String = iniFile.GetKeyValue("Time", "TrustedUtc")
            Dim storedTrustedUtc As String = If(storedTrustedUtcEnc = "", "", encryptionW.DecryptData(storedTrustedUtcEnc))
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
            ' FX6 (F7): the gate is now TimeChangeHoldsNow (a raise that outlives its bound is
            ' an orphan and stops gating), not the raw StrComp - see TimeChangeHoldActive.
            If Not TimeChangeHoldsNow() Then lastTickWallNow = tickWallNow
            ' B1: advance on the REAL monotonic elapsed regardless of wall DIRECTION
            ' (a backward roll or forward jump credits mono instead of freezing, so
            ' the block ends at its real duration - the P2 fix). A Trusted tick is
            ' byte-identical to the old NextHighWater+CapHighWaterAdvance composition.
            newHw = AdvanceHighWater(storedHw, DateTime.Now.ToString(culture), monoElapsedSeconds, HighWaterJumpCeilingSeconds)
            ' F77: fold in machine-OFF/asleep downtime, measured against an EXTERNALLY
            ' corroborated clock (never DateTime.Now - see TrustedTime.vb's header for why
            ' the obvious version of this is a B4 bypass). The probe is asked here and
            ' ANSWERED on some later tick: RequestIfDue queues a background HTTPS HEAD and
            ' returns at once, TryTakeReading collects whatever a previous one finished, so
            ' the 10s enforcement beat never waits on a network. With no reading in hand -
            ' the common case, since probes are minutes apart - ResolveMarkAndAnchor just
            ' carries the anchor forward by exactly what the B4 rule above credited, and
            ' newHw comes back byte-identical to AdvanceHighWater's output.
            '
            ' This sits BEFORE the parse into newHwAsOf on purpose: newHwAsOf is the asOf
            ' every expiry, cooling-off, schedule and slot gate below reads, so the credit
            ' has to be in the mark by the time it is taken. A boot that lands after a
            ' block's real end therefore lifts on the FIRST tick that carries a reading.
            trustedProbe.RequestIfDue(nowMono, storedTrustedUtc = "")
            TrustedTime.ResolveMarkAndAnchor(storedHw, newHw, storedTrustedUtc,
                                             trustedProbe.TryTakeReading(),
                                             TrustedTime.MaxCreditSeconds,
                                             newHw, newTrustedUtc)
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
        ' FX6 (F7): TimeChangeHoldsNow, not the raw StrComp - an orphaned raise (the notifier
        ' killed inside its own ~2s window) must not gate the exit machinery for ever.
        If Not TimeChangeHoldsNow() Then
            ' P41: one capped, ordinal-sorted enumeration of the trigger zone per tick, shared
            ' by both pollers - so a directory stuffed with trigger files cannot stall the tick,
            ' and the surplus is simply deferred (fail-closed: a deferred exit trigger holds).
            Dim triggerNames As List(Of String) = EnumerateTriggerFiles()
            ' Clear anything the glob picked up that addresses no slot at all (legacy
            ' unsuffixed names), or it occupies the per-tick budget for ever.
            ' Ledger 319: PurgeUnaddressedTriggers is now what handles a cooling-off trigger.
            ' ProcessCoolOffSignals is DELETED, and CoolOffRequestPrefix/CoolOffCancelPrefix
            ' were dropped from TriggerAddressesAnyFamily, so a monkmode_cooloff.request.<id>
            ' left by an older dist resolves to NO family and is deleted here, unread. It can
            ' never start a wait, and it cannot squat the P41 budget either.
            PurgeUnaddressedTriggers(Application.StartupPath, triggerNames)
            ' C3b: poll the partner-code trigger - since ledger 319 the ONLY exit channel
            ' there is (still inside tickLock + the TimeChanging="no" guard). Returns the
            ' post-verify UnlockedAt so THIS tick's heartbeat decides off it.
            ProcessPartnerCodeSignal(slots, macValid, triggerNames)
            ' P42 (v1.1 S5): apply any `add` requests AFTER both exit families, so a tick that
            ' carries an exit AND an add resolves the exit first (a slot being retired is never
            ' widened on its way out). It runs BEFORE the unions below so an accepted add is in
            ' this tick's hosts/snapshot truth rather than the next one's. Growth-only: it can
            ' never remove a site, drop a hold or shorten a block.
            ProcessAddRequests(slots, macValid, triggerNames)
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
            ' v1.1 S3a: the same window->duration conversion, once per SLOT that carries a
            ' rule, persisted through PersistSlotField. Still inert on every slot the CLI can
            ' arm today (WriteSlotSection writes ScheduleSpec=""), so this remains machinery +
            ' tests until `schedule` becomes a slot.
            ProcessSlotScheduleWindows(slots, prevTickWallNow, tickWallNow, newHw, monoElapsedSeconds, macValid, False)
            ' P29: PENDING -> ACTIVE, before the retire pass so a slot that starts and a slot
            ' that ends in the same tick are both handled. A just-activated slot has
            ' Until = HighWater + duration, which is strictly in the future, so it can never
            ' be activated and retired in one tick.
            ActivateDueSlots(slots, newHw, macValid)
            ' P38: retire every slot whose OWN exit is due. Each retire rewrites the config,
            ' the snapshot and hosts to the post-retire truth, so the slots the tick reasons
            ' over below must be RE-READ from disk afterwards - the in-memory list still holds
            ' the retired slot and its stale positions.
            If RetireDueSlots(slots, newHwAsOf, macValid, newHw) > 0 Then
                Try
                    Dim reloaded = New IniFile
                    reloaded.Load(Application.StartupPath + "\monkmode_settings.ini")
                    ' Re-derive macValid too: the retire re-stamped, so a failure to re-validate
                    ' here means something else moved the file, and the fold below must freeze.
                    macValid = ConfigMacIsValidForIni(reloaded)
                    slots = LoadSlots(reloaded)
                Catch ex As Exception
                    ' Fail-closed: keep enforcing off the pre-retire list (which is a SUPERSET
                    ' of the truth) and let the next tick read a clean config.
                End Try
            End If
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

        ' ---- v1.1 S3a: this tick's SLOT-derived enforcement, computed ONCE ----
        '
        ' Every value below is a WIDENING of what the v9 mirror already produced (R1): the
        ' held gate is "mirror held OR any slot held", the two lists are "the mirror's list
        ' PLUS the enforcing slots' entries", and the all-session flag is an OR. Nothing here
        ' can narrow a matcher or drop a hold - narrowing is the fail-open sin - so a config
        ' whose slots are unreadable (or absent) enforces exactly what it did before S3a,
        ' while a config with slots is now enforced from MAC-COVERED fields: raw-editing
        ' [Time] Until or blanking [User] CustomSites no longer changes what is blocked.
        Dim slotsHeld As Boolean = AnyBlockHeld(slots, newHwAsOf, ExpiryGraceSeconds, macValid, newHw)
        ' The union the hosts machinery enforces AND reconciles the snapshot to: the open
        ' schedule's own sites (unchanged) plus the sites of every slot whose lists count now -
        ' open OR PENDING (SlotContributesLists; a pending slot's entries are already in hosts
        ' and in the snapshot from arm time, and nothing can re-add them if this drops them).
        ' Duplicates are harmless - BuildHostsEntries dedups by host name and EffectiveHostsBlock
        ' dedups line-wise.
        Dim enforcedSites As New List(Of String)
        If scheduleActiveNow AndAlso activeSchedule IsNot Nothing Then enforcedSites.AddRange(activeSchedule.Sites)
        enforcedSites.AddRange(UnionSlotSites(slots, newHwAsOf, ExpiryGraceSeconds, macValid, newHw))
        ' The apps appended to the v9 kill list: the open schedule's apps (unchanged) plus the
        ' apps of the same slots the hosts union takes.
        Dim enforcedApps As New List(Of String)
        If scheduleActiveNow AndAlso activeSchedule IsNot Nothing Then enforcedApps.AddRange(activeSchedule.Apps)
        enforcedApps.AddRange(UnionSlotApps(slots, newHwAsOf, ExpiryGraceSeconds, macValid, newHw))
        ' D2c widen: the v9 [Process] AllSession flag OR that of any slot contributing lists.
        Dim allSessionKillNow As Boolean = iniAllSessionKill OrElse AnySlotAllSessionKill(slots, newHwAsOf, ExpiryGraceSeconds, macValid, newHw)
        ' The one gate the five per-tick self-heals take: the v9 BlockHeld (kept as the
        ' widening disjunct - the mirror is still written, and S3b/S8 own its removal) OR the
        ' slot OR-fold. macValid=False makes BOTH arms True, so a frozen config still freezes.
        Dim enforcementHeld As Boolean = BlockHeld(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid, iniScheduleActiveUntil, newHw) OrElse slotsHeld

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
        ' P37 (v1.1 S3a): reconcile the snapshot with config truth BEFORE the self-heal reads
        ' it, so a snapshot that drifted (a crash between the config write and the snapshot
        ' write, a CLI arm racing a service retire, a hand-edited snapshot) is repaired from
        ' the MAC-covered slots in the same tick it is used. macValid-gated, never-blanking, and
        ' driven by enforcedSites - which INCLUDES pending slots, so this can never strip out a
        ' scheduled block's entries that nothing would put back. See ReconcileHostsSnapshot.
        ReconcileHostsSnapshot(Application.StartupPath + "\monkmode_hosts.block", macValid, enforcedSites)

        Try
            Dim snapshotPath As String = Application.StartupPath + "\monkmode_hosts.block"
            If enforcementHeld Then
                ' The manual block's snapshot (the CLI persisted it at arm), or "" for a
                ' schedule-only block that never manually armed (no snapshot file on disk).
                Dim snapshotBlock As String = ""
                If My.Computer.FileSystem.FileExists(snapshotPath) Then
                    snapshotBlock = My.Computer.FileSystem.ReadAllText(snapshotPath)
                End If
                ' The effective marker block for this tick: snapshot UNION schedule sites while a
                ' window is open, else the snapshot verbatim (the no-schedule byte-identity).
                ' v1.1 S3a: the union is now the schedule's sites AND every enforcing slot's
                ' (enforcedSites). With neither in play the list is empty and this returns the
                ' snapshot VERBATIM - the byte-identical no-schedule path, unchanged.
                Dim expectedBlock As String = EffectiveHostsBlock(snapshotBlock, enforcedSites, enforcedSites.Count > 0)
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
                                 enforcementHeld,
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
            If enforcementHeld Then
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
            If enforcementHeld Then
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
            If enforcementHeld Then
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
        ' appKillTimer_Tick, SessionId<>0) runs the same union on its own side - S4 replaced its
        ' old "iniProcessList alone" reading with a per-beat union over every slot's Apps, re-read
        ' from disk each tick (Form1.RawSlotApps), plus the open schedule's apps. So the follow-up
        ' this comment used to promise (b3-iii) is DONE, and neither loop keys off the
        ' raw-editable v9 mirror by itself any more.
        ' v1.1 S3a: enforcedApps carries the open schedule's apps AND every ENFORCING slot's
        ' apps, so the kill set is now driven by the MAC-COVERED [SlotN] Apps rather than by
        ' the raw-editable v9 [Process] List. iniProcessList stays as the BASE (widen-only:
        ' deleting a name from the mirror can no longer stop a kill, and the mirror can never
        ' subtract from the slots either). Empty list => iniProcessList verbatim, byte-identical.
        Dim killList As String = EffectiveKillList(iniProcessList, enforcedApps, enforcedApps.Count > 0)
        ' D2c: normally the SERVICE kills only session-0 processes and the user-session notifier
        ' (MM_notify appKillTimer_Tick, SessionId<>0) kills the interactive session. With the armed
        ' [Process] AllSession flag, the LocalSystem service (which alone has cross-session kill
        ' privilege) kills matching apps in EVERY session - closing the "fast-user-switch to a second
        ' account" gap the notifier's single-session reach leaves. ProcessInKillScope WIDENS only:
        ' allSessionKill=False is the byte-identical session-0-only gate as before (Proc.SessionId is
        ' read exactly as the old If did), so a default block is unchanged; True makes it true for any
        ' session. No widen ever removes a kill (fail-closed, matching the schedule app-kill union
        ' that is likewise un-gated by macValid).
        ' v1.1 S7b: the names of the processes this tick actually killed, for the
        ' stats sidecar below. Collected rather than counted so each one can be
        ' attributed to the slot that asked for it. Filled INSIDE the existing Try,
        ' after Kill() returned without throwing, so the list holds real kills only.
        ' The name is read into a local BEFORE Kill(): Process.ProcessName throws once
        ' the process has exited, so reading it afterwards would drop the very kills we
        ' are trying to count (and it is the same string the matcher just used).
        Dim killedThisTick As New List(Of String)
        processList = System.Diagnostics.Process.GetProcesses()
        For Each Proc In processList
            If ProcessInKillScope(allSessionKillNow, Proc.SessionId) Then
                Try
                    Dim procName As String = Proc.ProcessName
                    If ProcessNameInKillList(killList, procName) Then
                        Proc.Kill()
                        killedThisTick.Add(procName)
                    End If
                Catch ex As Exception
                End Try
            End If
        Next

        ' ---- v1.1 S7b (P45/P47): the stats sidecar. DISPLAY-ONLY, ONE WRITE PER TICK ----
        '
        ' Records what this tick DID - apps killed (per slot) and, while a block is
        ' held, another TimerIntervalMs/1000 seconds on today's day-log - into
        ' %ProgramData%\MonkMode\stats-service.ini, of which the service is the sole
        ' writer (P45). NOTHING in this service, the guardian, the notifier or the CLI's
        ' arming path ever READS that file: it is numbers on a screen, so a deleted,
        ' forged or hostile sidecar cannot lift, shorten or perturb a block.
        '
        ' Everything is inside ONE Try and every StatsSidecar entry point is itself
        ' total - a counter may never throw into the tick. The write is skipped
        ' entirely when the delta is empty (IsEmpty), so an idle machine with no block
        ' held never touches the disk here.
        '
        ' The armed-seconds gate is enforcementHeld - the SAME value the five
        ' self-heals above take - so the day-log measures exactly the time MonkMode
        ' considered itself to be enforcing, including the fail-closed freeze a bad
        ' MAC produces (during which the block genuinely IS held). The day key is the
        ' WALL clock: a streak is a calendar idea, and this timeline has no
        ' enforcement authority.
        Try
            Dim statsDelta As StatsSidecar.StatsData = Nothing
            Dim statsDayKey As String = StatsSidecar.DayKeyFor(DateTime.Now)
            For Each killedName As String In killedThisTick
                statsDelta = StatsSidecar.Merge(statsDelta,
                                                StatsSidecar.NewDelta(SlotIdOwningApp(slots, killedName), 1, 0, 0, statsDayKey))
            Next
            If enforcementHeld Then
                statsDelta = StatsSidecar.Merge(statsDelta,
                                                StatsSidecar.NewDelta("", 0, 0, CLng(TimerIntervalMs \ 1000), statsDayKey))
            End If
            ' True = create the directory with the P49 BUILTIN\Users:Modify ACE if it
            ' is absent, so the NON-elevated notifier can write its own sidecar beside
            ' this one. LocalSystem is the only party here that can set that ACE.
            StatsSidecar.Apply(StatsSidecar.ServiceStatsPath(), statsDelta, True)
        Catch ex As Exception
        End Try

        ' FX6 (F7): TimeChangeHoldsNow, not the raw StrComp. This gate is the one that also
        ' fences [Time] HighWater's only persistence, which is exactly why an orphaned raise
        ' froze the block for ever - and why releasing it can only ever end a block LATE.
        If Not TimeChangeHoldsNow() Then
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
            ' v1.1 S3b: this is now the V9 RESIDUAL, not the machine's exit decision. Its
            ' [Time] Until, [Time] CoolOffUntil and [Partner] UnlockedAt inputs sit OUTSIDE
            ' the canonical and are therefore raw-editable under a valid MAC; its
            ' [Schedule] Spec/ActiveUntil inputs are INSIDE it as of v11 (FX1), because a
            ' schedule-only config has no slots and those two keys ARE the armed block - so
            ' blanking the Spec now fails the MAC and FREEZES (Hold), where before v11 it
            ' kept macValid True and tore a live window down mid-block (the F1 fail-open).
            ' ClassifyTick consumes its Lift as a NECESSARY
            ' condition for teardown and never as a sufficient one, so back-dating [Time]
            ' Until can no longer tear anything down: with slots armed it is ignored outright,
            ' and with none armed it only withdraws a hold the empty slot set had already
            ' withdrawn. Its Restamp/Hold arms still HOLD - which is what keeps the v9
            ' schedule-only shape (SlotCount = 0, an armed [Schedule] Spec) working unchanged.
            Dim residual As HeartbeatAction = ClassifyHeartbeat(macValid, BlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds), CoolOffElapsedTime(iniCoolOffUntil, newHw), PartnerUnlocked(iniPartnerUnlockedAt), ScheduleActive(iniScheduleActiveUntil, newHw), scheduleArmedNow)
            Select Case ClassifyTick(macValid, slots.Count, residual)
                Case TickAction.TeardownAll
                    ' P39: nothing is armed any more - config first, then the snapshot, then
                    ' the existing stopMe() teardown from the hosts strip onward.
                    TeardownAll()
                Case TickAction.Restamp
                    ' FX6: the write itself lives in RestampHeartbeatAt (the PersistSlotFieldAt/
                    ' RetireSlotAt "testable core with the path made explicit" pattern), so the
                    ' unit suite can drive the REAL heartbeat write against a test-owned file.
                    ' The orphaned-flag argument is the F7 half: the gate above is open, so a
                    ' still-raised flag here is by definition one that outlived its bound.
                    RestampHeartbeatAt(Application.StartupPath + "\monkmode_settings.ini", newHw, newTrustedUtc, TimeChangeFlagIsOrphaned())
                Case TickAction.Hold
                    ' macValid=False: a tampered or unstamped (WriteDefaultBlock) config.
                    ' Fail CLOSED - do NOT re-stamp (that would re-bless the tamper and
                    ' let it lift next tick: the B7 bypass), do NOT retire any slot and do
                    ' NOT tear down. Frozen until re-armed from the CLI. Ledger 319 removed
                    ' `unblock --force`, so there is no in-band recovery from this state at
                    ' all any more - not even the partner code, which needs a valid MAC.
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

    ' ================= FX6 (F7): THE TimeChanging FLAG SELF-EXPIRES =================
    '
    ' THE WEDGE THIS CLOSES. The notifier raises [Time] TimeChanging = "yes", sleeps ~2s and
    ' lowers it (MM_notify Form1.SystemEvents_TimeChanged), and this service pauses BOTH its
    ' exit machinery and its HighWater persistence while it is raised - the trigger polls,
    ' ProcessScheduleWindows, ActivateDueSlots, RetireDueSlots and the whole heartbeat/
    ' ClassifyTick block all sit behind that one gate. Kill the notifier INSIDE those 2s
    ' (`taskkill /f` is the documented B1 move, and it skips the AppDomain backstop that
    ' would have lowered the flag) and the raise is ORPHANED: the flag is outside the
    ' canonical so nothing detects it, it survives a reboot, and every intended exit -
    ' natural expiry and partner code - is gated off FOR EVER, and since ledger 319 there is
    ' no escape hatch left behind it: nothing gets a genuinely finished block out. Reachable
    ' NON-elevated: changing the time zone is a default user right and broadcasts
    ' WM_TIMECHANGE. The same wedge follows from a config that simply has no [Time]
    ' TimeChanging key at all, since the gate tests for the literal "no".
    '
    ' THE FIX: the flag holds for a BOUNDED span of monotonic time and then stops being
    ' obeyed. A genuine episode is ~2s, so 5 minutes is two orders of magnitude of headroom -
    ' an in-progress clock change is still fully held, which is the property the flag exists
    ' for.
    '
    ' WHY LETTING IT GO IS FAIL-CLOSED. While the flag holds, [Time] HighWater is never
    ' persisted (its one write lives inside the gate), so the stored high-water mark FREEZES
    ' at the moment of the wedge and every later tick re-advances from that frozen value at
    ' the real monotonic rate. A block therefore OVER-runs by exactly the wedged span and
    ' cannot be one second short when the gate re-opens: releasing the gate can never lift
    ' early, it can only let a block that has already served its full time end. Nor does the
    ' release touch a self-heal, a matcher or a MAC - hosts, the kill list and the freeze
    ' semantics are untouched either way.
    Friend Const TimeChangeHoldMaxSeconds As Long = 300

    ' PURE (unit-pinned): does a TimeChanging flag still gate this tick? "no" never gates -
    ' that is the byte-identical StrComp the tick has always used. Anything else (a raised
    ' "yes", an absent key, garbage) gates until it has been continuously observed for MORE
    ' than maxSeconds of monotonic time; past that it is treated as orphaned and ignored.
    ' Note the fail-closed direction on each axis: a fresh raise gates (a real clock change
    ' is honoured), an unreadable/garbled value gates (we do not know what is happening), and
    ' only the passage of real time - which the wedge itself cannot fake, because
    ' Environment.TickCount64 is immune to the clock - ever releases it.
    Friend Shared Function TimeChangeHoldActive(ByVal flagText As String, ByVal raisedForSeconds As Long, ByVal maxSeconds As Long) As Boolean
        If StrComp("no", flagText) = 0 Then Return False
        Return raisedForSeconds <= maxSeconds
    End Function

    ' The live side of the gate: maintain the monotonic raise anchor and answer the pure
    ' classifier above. Idempotent, so the tick may consult it at each of its gate sites
    ' without the answer drifting (the bound is minutes; the sites are microseconds apart).
    ' The anchor starts at FIRST OBSERVATION, so a flag left raised across a reboot buys
    ' itself one more bounded hold on the next service start - bounded is the whole point.
    Private Function TimeChangeHoldsNow() As Boolean
        If StrComp("no", iniTimeChanging) = 0 Then
            timeChangeRaisedAtMono = 0
            Return False
        End If
        Dim nowMono As Long = Environment.TickCount64
        If timeChangeRaisedAtMono = 0 Then timeChangeRaisedAtMono = nowMono
        Return TimeChangeHoldActive(iniTimeChanging, (nowMono - timeChangeRaisedAtMono) \ 1000L, TimeChangeHoldMaxSeconds)
    End Function

    ' True when the flag is raised AND has outlived its bound - i.e. it is an orphan, not a
    ' clock change in progress. The heartbeat's own (MAC-re-validated) write lowers it when
    ' this is True, which restores the protocol: without that, a permanently raised flag
    ' would be permanently ignored and the NEXT genuine clock change would go unhonoured.
    ' Deliberately never True for a fresh raise, so the service can never stamp "no" over a
    ' notifier episode that is actually running.
    Private Function TimeChangeFlagIsOrphaned() As Boolean
        Return StrComp("no", iniTimeChanging) <> 0 AndAlso Not TimeChangeHoldsNow()
    End Function

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

    ' v1.1 S3b: the read cap moved to the SHARED TriggerMaxBytes (see the P40/P41 block) so
    ' the content-bearing `partner.code.<id>` and `add.request.<id>` channels are capped by
    ' one constant instead of two that could drift.

    ' Ledger 319: MinCoolOffFloorSeconds, ComputeCoolOffDeadline and
    ' ParseConfiguredCoolOffSeconds are DELETED. Between them they turned a request trigger
    ' into a deadline; with no request channel and no writer there is no deadline to compute,
    ' and a compile-time "shortest wait we will grant" describes a wait that no longer exists.

    ' THE COOLING-OFF LIFT ARM, PERMANENTLY DISARMED (ledger 319, 30/08/2026).
    '
    ' This used to answer "has the pending cooling-off deadline been reached?" from the stored
    ' CoolOffUntil against the trusted B4 mark, and a True here LIFTED the block through
    ' EffectiveExit / ClassifyHeartbeat. Cooling-off was removed as an exit, so the honest
    ' answer is now always NO - and it is returned WITHOUT LOOKING at either argument.
    '
    ' Why this shape rather than deleting the function and its parameter:
    '   * it is the single choke point. Both callers (EffectiveExit here, and the guardian's
    '     byte-identical copy) get their cool-off term from this one function, so hard-wiring
    '     it False is what makes a forged, already-elapsed CoolOffUntil in a MAC-valid config
    '     unable to lift anything. The value is not merely unwritten - it is unread.
    '   * removing the parameter would mean re-shaping EffectiveExit / ClassifyHeartbeat /
    '     ClassifySlot across two assemblies and ~130 positional call sites in the test suite.
    '     Positional booleans shifted by hand is exactly how a lift gate gets silently
    '     inverted, and the safety gained over "always False" is zero.
    ' Parity: the guardian's copy carries the same body and the same comment. Pure + Shared.
    ' Pinned by CoolOffTests (an elapsed deadline never lifts, through the real gates).
    Friend Shared Function CoolOffElapsedTime(ByVal coolOffUntilText As String, ByVal highWaterText As String) As Boolean
        Return False
    End Function

    ' Ledger 319: the CoolOffAction enum and ClassifyCoolOffSignal (the request/cancel/commit/
    ' macValid matrix) are DELETED with the poll that consumed them. There is no trigger left
    ' to classify - a stale cooling-off file is unaddressed junk that PurgeUnaddressedTriggers
    ' deletes unread.

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

    ' D2c: interpret the [Process] AllSession flag. "yes" (case/space-insensitive, mirroring
    ' IsCommitted) = kill blocked apps in EVERY session, not just session 0; "no"/absent/Nothing/
    ' anything else = the current session-0-only service kill (the notifier still covers the
    ' interactive session). The flag is MAC-covered, so under a valid MAC this value is authentic
    ' (the CLI wrote it at arm and it never changes during the block). It is read raw (NOT macValid-
    ' gated): a widen-only union that can only ADD kills, so this default of "off on anything but
    ' 'yes'" can never REMOVE a session-0 kill, and a tampered "yes" (which also freezes the block)
    ' only ever widens. Pure + Shared so it is unit tested.
    Friend Shared Function AllSessionKillArmed(ByVal allSessionText As String) As Boolean
        Return String.Equals(If(allSessionText, "").Trim(), "yes", StringComparison.OrdinalIgnoreCase)
    End Function

    ' D2c: is THIS process in the service's kill scope this tick? All-session ON => every session;
    ' OFF => session 0 only (the byte-identical prior gate). WIDENS only - OFF can never kill fewer
    ' than session 0, ON only ADDS the other sessions - so no error path here ever removes a kill.
    ' Pure + Shared so the widening decision is unit-tested without enumerating live processes.
    Friend Shared Function ProcessInKillScope(ByVal allSessionKill As Boolean, ByVal sessionId As Integer) As Boolean
        Return allSessionKill OrElse sessionId = 0
    End Function

    ' The per-tick app-kill MATCH decision: does the effective kill list name this live process's
    ' image? Case-INSENSITIVE (Ordinal), because the two sides are written by different parties and
    ' need not agree on casing: the list holds whatever the user typed at arm time (PackApps,
    ' Blocker.vb:630, only APPENDS a missing ".exe" - it never lower-cases, so `--apps WhatsApp.exe`
    ' is stored verbatim; the schedule's own apps, Blocker.vb:1059, likewise), while ProcessName
    ' reports the casing Windows holds for the running image. The old case-SENSITIVE String.Contains
    ' therefore silently UNDER-killed - a list entry "WhatsApp.exe" against a live ProcessName
    ' "Whatsapp" simply never matched, and the app stayed open. That is FAIL-OPEN, the one thing
    ' enforcement may never do, so the match ignores case.
    ' WIDEN-ONLY, the property that makes this safe: ignoring case can only ever ADD matches -
    ' every (list, name) pair that matched case-sensitively still matches - so no kill this code
    ' used to make can be removed by the change.
    ' Deliberately still a SUBSTRING search over the delimited list, the old predicate's exact shape.
    ' A token-exact match would be tidier but would NARROW the set (it would stop "code.exe" matching
    ' a list holding "vscode.exe"), and narrowing here is precisely the fail-open this fixes.
    ' Nothing/empty list => no match: the old loop's NullReferenceException on a Nothing list was
    ' swallowed by its own Catch and killed nothing either, so this is the same outcome, not a narrow.
    ' Pure + Shared so the matcher is unit-tested without enumerating live processes.
    Friend Shared Function ProcessNameInKillList(ByVal killList As String, ByVal processName As String) As Boolean
        If killList Is Nothing OrElse killList.Length = 0 Then Return False
        Return killList.IndexOf(If(processName, "") & ".exe", StringComparison.OrdinalIgnoreCase) >= 0
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
    '     (mirrors the `add` fail-open fix; ledger 319 deleted the cooling-off sibling).
    '   * alreadyUnlocked (UnlockedAt already set) => Ignore: the block is ending,
    '     nothing to re-verify (this is also what makes consume-after-persist
    '     crash-safe: a crash between the UnlockedAt write and the trigger delete
    '     re-classifies here as Ignore and just deletes the stale trigger).
    '   * a present trigger with a non-blank candidate => Verify; otherwise Ignore
    '     (no/blank candidate = a no-op that just deletes the stale trigger).
    '   * deliberately does NOT read `committed`: since ledger 319 every block is committed
    '     and the partner code is its ONE intended exit, so the flag decides nothing here.
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
    ' retry next tick). Same frame + fail-closed shape as ActivationUntil. Pure + Shared.
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
    ' unit- and e2e-testable through the real gates - exactly as ClassifyPartnerCodeSignal
    ' relates to ProcessPartnerCodeSignal. Fail-closed throughout: an unparseable HighWater
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

    ' C5b (c3 residual, issue #2): the `schedule --clear` lost-update guard - the pure decision
    ' behind PersistScheduleActiveUntil's write-back gate. The tick SNAPSHOTS [Schedule] Spec at
    ' load and derives the new ActiveUntil from it seconds later; the CLI (the sole Spec writer)
    ' can clear/re-arm the Spec inside that window. Without this gate the persist's reload keeps
    ' the CLI's new Spec but still writes an ActiveUntil derived from the STALE snapshot - a
    ' window opens from a schedule the user just cleared (the service "reinstating" a clear the
    ' CLI already reported). The persist may write ONLY when the reloaded Spec is BYTE-IDENTICAL
    ' to the snapshot (ordinal, no trimming - the CLI emits canonical text, so any difference
    ' means a write landed); otherwise it aborts and the next tick (<=10s) re-evaluates off the
    ' fresh Spec. In effect the Spec text IS the write-version. Both abort outcomes are fail-
    ' closed: an aborted OPEN leaves ActiveUntil="" (no hold minted from a dead rule - a fresh
    ' arm's own windows open next tick), an aborted CLOSE keeps the stored hold <=1 tick (over-
    ' block, never under). ABA (clear + re-arm the identical Spec within one tick) passes the
    ' gate - safe, because the derived ActiveUntil depends only on the Spec CONTENT, which is
    ' once again the armed content. Nothing/absent normalise to "" (IniFile.GetKeyValue returns
    ' String.Empty for a missing key). Pure + Shared so the decision is unit-tested; the file-I/O
    ' wrapper stays smoke-only (the ClassifyPartnerCodeSignal/ProcessPartnerCodeSignal discipline).
    Friend Shared Function ScheduleSpecUnchangedSinceSnapshot(ByVal snapshotSpec As String, ByVal reloadedSpec As String) As Boolean
        Return String.Equals(If(snapshotSpec, ""), If(reloadedSpec, ""), StringComparison.Ordinal)
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

    ' ============ v1.1 S3a: the SLOT enforcement layer (folds + unions) ============
    '
    ' Until S3a the tick enforced off the v9 SINGLE-BLOCK mirror keys ([Time] Until,
    ' [User] CustomSites, [Process] List/AllSession). Those keys sit OUTSIDE the v10
    ' canonical, so a raw edit to any of them left macValid TRUE: back-dating [Time] Until
    ' tore the whole block down with no tamper evidence at all. The folds and unions below
    ' read the SLOTS instead ([Slot1]..[Slot8] - every field MAC-covered), which is what
    ' actually closes that hole.
    '
    ' The v9 mirror is NOT removed here (S3b/S8 own its removal): S2 still writes it as an
    ' over-blocking PROJECTION of the slot set, and every call site below keeps it as a
    ' WIDENING disjunct - held if the mirror says held OR any slot says held; kill/hosts =
    ' the mirror's list PLUS the slots'. Widen-only is R1 (narrowing a matcher is fail-open),
    ' and it also guarantees this slice cannot REMOVE enforcement a config had before it.
    '
    ' P16: a slot's STATE is DERIVED, never stored -
    '   PENDING  <=> StartAt <> "" AndAlso Until = ""   (armed for later; not enforcing yet)
    '   SCHEDULE <=> ScheduleSpec <> ""                 (its Until is the past sentinel)
    '   ACTIVE   <=> Until <> "" and not expired against the trusted HighWater.

    ' One slot as the tick sees it: the four encrypted datetimes already DECRYPTED, the
    ' three lists already split. Reference type so the C# unit tests can build one directly
    ' and drive the pure folds without any ini/DPAPI at all.
    Friend Class SlotState
        Public Position As Integer
        Public Id As String = ""
        Public StartAt As String = ""              ' decrypted; "" unless PENDING
        Public DurationSeconds As String = ""
        Public UntilText As String = ""            ' decrypted; "" = no end computed yet
        Public Sites As New List(Of String)
        Public Apps As New List(Of String)
        Public UrlPatterns As New List(Of String)
        Public AllSession As String = ""
        Public ScheduleSpec As String = ""
        Public ScheduleActiveUntil As String = ""  ' decrypted
        ' v1.1 S3b: the four EXIT fields, all MAC-covered, all per slot. Until S3b the
        ' exit was adjudicated off the v9 machine-wide mirror ([Time] CoolOffUntil,
        ' [Partner] UnlockedAt/Salt/Hash, [Commit] Committed), which sits OUTSIDE the v10
        ' canonical - so one block's cooling-off deadline lifted every block, and one
        ' block's code addressed all of them. Read per slot, they are tamper-evident AND
        ' independent.
        Public CoolOffUntil As String = ""         ' decrypted; "" = no cooling-off pending
        Public CoolOffDuration As String = ""      ' plaintext seconds, as stored
        Public PartnerSalt As String = ""
        Public PartnerHash As String = ""
        Public PartnerUnlockedAt As String = ""    ' plaintext, as stored; "" = not code-unlocked
        Public Committed As String = ""
    End Class

    ' The 16 per-slot key names, in BuildSlotCanonical's LINE order. The slot compaction a
    ' retire performs (P38) copies a whole section field-by-field, so it needs the field set
    ' spelled out exactly once; a key missing here would be silently DROPPED by a compaction
    ' and the canonical would then be built over "" for it - a MAC that no longer matches the
    ' block the user armed. Pinned equal to BuildSlotCanonical's emitted lines by a unit test,
    ' so adding a 17th field to the canonical without adding it here fails loudly.
    Friend Shared ReadOnly SlotFieldNames As String() = {
        "Id", "StartAt", "DurationSeconds", "Until", "Sites", "Apps", "UrlPatterns",
        "AllSession", "ScheduleSpec", "ScheduleActiveUntil", "CoolOffUntil", "CoolOffDuration",
        "PartnerSalt", "PartnerHash", "PartnerUnlockedAt", "Committed"}

    ' Split one stored packed list into its entries (trimmed, empties dropped). Sites/Apps
    ' are ";"-packed with a trailing ";" (Blocker.PackList/PackApps); UrlPatterns is
    ' "|"-packed (P55). Neither separator can appear inside an entry - both are rejected at
    ' arm - so a split can never fuse or split an entry wrongly. Pure + Shared.
    Friend Shared Function SplitPackedList(ByVal packed As String, ByVal separator As Char) As List(Of String)
        Dim outList As New List(Of String)
        If packed Is Nothing OrElse packed = "" Then Return outList
        For Each tok As String In packed.Split(separator)
            Dim t As String = tok.Trim()
            If t <> "" Then outList.Add(t)
        Next
        Return outList
    End Function

    ' Has THIS slot's own end genuinely passed? Fail-closed on every axis, exactly like
    ' BlockHasExpired (which it delegates to): an unparseable Until is NOT expired, and a
    ' slot with no Until at all (PENDING, or a SCHEDULE slot before the CLI writes the
    ' sentinel) is NOT expired either - "no recorded end" can never mean "over". Pure.
    Friend Shared Function SlotExpired(ByVal slot As SlotState, ByVal asOf As DateTime, ByVal graceSeconds As Long) As Boolean
        If slot Is Nothing Then Return False
        If slot.UntilText = "" Then Return False
        Return BlockHasExpired(slot.UntilText, asOf, graceSeconds)
    End Function

    ' Does this slot keep the machine's ENFORCEMENT MACHINERY up (the guardian peer, the
    ' SafeBoot keys, the DoH policy, the deny-DELETE ACE, the hosts repair)? The per-slot
    ' twin of BlockHeld, and fail-closed the same way:
    '   * macValid = False   => HELD (freeze: a tampered config never stands anything down);
    '   * not expired        => HELD (which includes a PENDING slot: its machinery must be
    '                           up and re-asserted BEFORE its start moment arrives);
    '   * an open schedule window => HELD (SD1).
    ' Note the deliberate difference from SlotEnforcesNow below: "keep the machinery up" is
    ' NOT the same question as "are this slot's sites blocked right now". Pure + Shared.
    Friend Shared Function SlotHeld(ByVal slot As SlotState, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As Boolean
        If Not macValid Then Return True
        If slot Is Nothing Then Return False
        Return (Not SlotExpired(slot, asOf, graceSeconds)) OrElse ScheduleActive(slot.ScheduleActiveUntil, highWaterText)
    End Function

    ' The OR-fold over every slot - the gate all five per-tick self-heals take. macValid =
    ' False returns True BEFORE the loop, so a frozen config holds even with zero readable
    ' slots (the empty-list case is exactly where a plain Any() fold would fail OPEN).
    ' Pure + Shared.
    Friend Shared Function AnyBlockHeld(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As Boolean
        If Not macValid Then Return True
        If slots Is Nothing Then Return False
        For Each s As SlotState In slots
            If SlotHeld(s, asOf, graceSeconds, macValid, highWaterText) Then Return True
        Next
        Return False
    End Function

    ' Is THIS slot's own enforcement window OPEN at this instant - i.e. is its timer running?
    ' Deliberately STRICTER than SlotHeld, and NOT the union-membership test (that is
    ' SlotContributesLists below): a PENDING slot's timer has not started, so anything that
    ' reasons about "is this block running" must say No. Fail-closed where it counts:
    ' macValid = False reads every slot as running (a frozen config over-blocks rather than
    ' losing anything). Pure + Shared.
    Friend Shared Function SlotEnforcesNow(ByVal slot As SlotState, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As Boolean
        If slot Is Nothing Then Return False
        If Not macValid Then Return True
        If slot.UntilText <> "" AndAlso Not SlotExpired(slot, asOf, graceSeconds) Then Return True   ' ACTIVE
        Return ScheduleActive(slot.ScheduleActiveUntil, highWaterText)                               ' open window
    End Function

    ' P16: is this slot PENDING - armed with `--start` for later, its Until not yet computed?
    ' Derived, never stored: StartAt set AND no Until. Pure + Shared.
    Friend Shared Function SlotIsPending(ByVal slot As SlotState) As Boolean
        If slot Is Nothing Then Return False
        Return slot.StartAt <> "" AndAlso slot.UntilText = ""
    End Function

    ' Do this slot's LISTS belong in the enforced union and in the hosts heal source? Yes when
    ' its window is open (SlotEnforcesNow) - and ALSO when it is PENDING.
    '
    ' The pending arm is load-bearing, not generosity. The CLI writes a pending slot's sites
    ' into hosts AND into monkmode_hosts.block at arm (Program.vb DoBlock -> Blocker.Write-
    ' HostsBlock), and S2's v9 guard-horizon mirror already enforces them from that moment, so
    ' a `--start` slot over-blocks from arm time in this tree - accepted, and over-block only.
    ' Leaving PENDING out here would make the P37 reconciler STRIP those already-written
    ' entries out of the heal source within one tick, and NOTHING could ever put them back:
    ' the P29 activation stamper that turns PENDING into ACTIVE does not exist until S3b, so
    ' the slot never becomes enforcing. Every later hosts repair, crash re-block or reboot
    ' would then rebuild hosts WITHOUT the scheduled block's sites - a block the CLI promised
    ' and that would never once take effect. That is a strict-subset regression against v1.0,
    ' the one place the unions would not be a superset, so PENDING is in.
    ' Fail-closed: macValid = False already returns True via SlotEnforcesNow. Pure + Shared.
    Friend Shared Function SlotContributesLists(ByVal slot As SlotState, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As Boolean
        Return SlotEnforcesNow(slot, asOf, graceSeconds, macValid, highWaterText) OrElse SlotIsPending(slot)
    End Function

    ' The shared body of the three unions: first-occurrence order, deduped case-insensitively
    ' (domains and exe names are both case-insensitive on Windows, and a case-only duplicate
    ' would just emit a second identical hosts line). WIDEN-ONLY by construction - it can only
    ' ever ADD entries to whatever the caller already had. Pure + Shared.
    Private Shared Function UnionContributedSlotLists(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String, ByVal pick As Func(Of SlotState, List(Of String))) As List(Of String)
        Dim outList As New List(Of String)
        If slots Is Nothing Then Return outList
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each s As SlotState In slots
            If Not SlotContributesLists(s, asOf, graceSeconds, macValid, highWaterText) Then Continue For
            Dim items As List(Of String) = pick(s)
            If items Is Nothing Then Continue For
            For Each item As String In items
                Dim t As String = If(item, "").Trim()
                If t <> "" AndAlso seen.Add(t) Then outList.Add(t)
            Next
        Next
        Return outList
    End Function

    ' The hosts UNION: the blocked sites of every slot whose lists count right now (open, or
    ' PENDING - see SlotContributesLists). A domain named by two slots appears ONCE (dedup is
    ' per host name, exactly like BuildHostsEntries' own dedup).
    Friend Shared Function UnionSlotSites(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As List(Of String)
        Return UnionContributedSlotLists(slots, asOf, graceSeconds, macValid, highWaterText, Function(s) s.Sites)
    End Function

    ' The app-kill UNION, over the same slots as the hosts union. Appended to the v9 kill list
    ' by the tick (never replacing it), so the matcher can only ever gain names.
    Friend Shared Function UnionSlotApps(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As List(Of String)
        Return UnionContributedSlotLists(slots, asOf, graceSeconds, macValid, highWaterText, Function(s) s.Apps)
    End Function

    ' The URL-pattern UNION (P55 lists). NOTE: nothing enforces URL patterns yet - the F2
    ' in-browser UIA watcher that consumes them is a later slice. The fold lives here so the
    ' three unions are defined and tested together and so F2 wires to a pinned reader instead
    ' of inventing a fourth traversal of the slots.
    Friend Shared Function UnionSlotUrlPatterns(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As List(Of String)
        Return UnionContributedSlotLists(slots, asOf, graceSeconds, macValid, highWaterText, Function(s) s.UrlPatterns)
    End Function

    ' Does any slot contributing lists ask for the cross-session app kill? Same membership as
    ' the unions (so a slot's apps and the scope they are killed in can never disagree), and
    ' widen-only exactly like the v9 [Process] AllSession read it is OR'd with: a "yes" can
    ' only ever ADD kills, never remove one.
    Friend Shared Function AnySlotAllSessionKill(ByVal slots As List(Of SlotState), ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As Boolean
        If slots Is Nothing Then Return False
        For Each s As SlotState In slots
            If SlotContributesLists(s, asOf, graceSeconds, macValid, highWaterText) AndAlso
               String.Equals(If(s.AllSession, ""), "yes", StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    ' v1.1 S7b (P45), DISPLAY-ONLY: which slot Id asked for this killed process, or "" if
    ' none can be named. Walks the slots in POSITION order and takes the first whose Apps
    ' name the image, through the SAME ProcessNameInKillList predicate the kill decision
    ' itself used - so attribution can never disagree with what was killed.
    '
    ' Two blocks may legitimately name the same app; the FIRST is credited and the others
    ' are not, so a per-slot count can under-attribute. That is a deliberate choice over
    ' crediting all of them, which would make the per-slot figures sum to MORE than the
    ' lifetime total and turn the display into nonsense. The unattributed case ("") still
    ' counts towards lifetime and the day-log, so no kill is ever lost.
    '
    ' Pure; never throws; NOT gated on macValid or on whether a slot is enforcing - this
    ' decides a LABEL on a counter, and it has no enforcement authority whatsoever.
    Friend Shared Function SlotIdOwningApp(ByVal slots As List(Of SlotState), ByVal processName As String) As String
        If slots Is Nothing OrElse processName Is Nothing Then Return ""
        For Each s As SlotState In slots
            If s Is Nothing OrElse s.Apps Is Nothing Then Continue For
            For Each app As String In s.Apps
                If ProcessNameInKillList(app, processName) Then Return If(s.Id, "")
            Next
        Next
        Return ""
    End Function

    ' P36: the POSITION (1-based) holding the slot whose Id is slotId, or 0 if no position
    ' does. TOP RISK 2 lives here: positions are not stable (a retire/compaction renumbers
    ' them), so a writer that trusted a position captured before a reload could update a
    ' DIFFERENT block's field - the mis-adjudicated-lift class. Every per-slot write
    ' re-locates by Id instead. Uses the CLAMPED SlotCount, never the raw stored value, so a
    ' forged count can't widen the scan. Pure + Shared (unit-pinned).
    Friend Shared Function FindSlotPositionById(ByVal ini As IniFile, ByVal slotId As String) As Integer
        If ini Is Nothing Then Return 0
        Dim wanted As String = If(slotId, "").Trim()
        If wanted = "" Then Return 0
        Dim count As Integer = ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"))
        For pos As Integer = 1 To count
            Dim stored As String = If(ini.GetKeyValue("Slot" & pos.ToString(CultureInfo.InvariantCulture), "Id"), "").Trim()
            If String.Equals(stored, wanted, StringComparison.Ordinal) Then Return pos
        Next
        Return 0
    End Function

    ' ======== v1.1 S3b: the EXIT moves onto the slots (the classifier split) ========
    '
    ' THE HOLE THIS CLOSES. S3a moved WHAT is blocked and WHETHER THE MACHINERY STAYS UP onto
    ' the slots, but the LIFT still ran through ClassifyHeartbeat/EffectiveExit over the v9
    ' mirror keys - [Time] Until, [Time] CoolOffUntil, [Partner] UnlockedAt, [Schedule]
    ' ActiveUntil/Spec - and NOT ONE of those is inside the v10 canonical. Back-dating
    ' [Time] Until with a text editor left macValid TRUE and tore the whole machine down.
    '
    ' THE SPLIT. Two decisions where there was one:
    '   * ClassifySlot - per SLOT, off that slot's own MAC-covered fields. Retire removes
    '     THAT slot (P38) and disturbs no other.
    '   * ClassifyTick - per MACHINE. Teardown fires ONLY at SlotCount = 0 (P39).
    ' The v9 residual keeps exactly ONE power in ClassifyTick: it can HOLD a teardown back,
    ' never cause one. A raw edit to any v9 key is therefore now an OVER-block at worst -
    ' which is the whole point of the slice.

    Friend Enum SlotAction
        Retire   ' this slot's own exit is due: compact it out of the config (P38)
        Hold     ' keep it: not due, frozen config, open window, or an armed schedule
    End Enum

    ' The per-slot exit gate. DEFINED as ClassifyHeartbeat's Lift arm rather than re-derived,
    ' deliberately: the heartbeat trichotomy is the attacked, pinned core (macValid freeze,
    ' the SD1 open-window hard hold, the c2 between-windows hold, and the Lift <=>
    ' EffectiveExit equivalence), and a second hand-written copy of that logic is exactly how
    ' two gates drift apart. Restamp and Hold both mean "keep this slot"; only Lift retires
    ' it. Pure + Shared; the per-slot Retire <=> SlotEffectiveExit equivalence is pinned by a
    ' test that derives the two INDEPENDENTLY.
    Friend Shared Function ClassifySlot(ByVal macValid As Boolean, ByVal slotExpired As Boolean, ByVal coolOffElapsed As Boolean, ByVal codeUnlocked As Boolean, ByVal scheduleActive As Boolean, ByVal scheduleArmed As Boolean) As SlotAction
        If ClassifyHeartbeat(macValid, slotExpired, coolOffElapsed, codeUnlocked, scheduleActive, scheduleArmed) = HeartbeatAction.Lift Then
            Return SlotAction.Retire
        End If
        Return SlotAction.Hold
    End Function

    ' The per-slot twin of EffectiveExit: may THIS slot end? Threads the slot's own four
    ' MAC-covered exit fields through the SHARED EffectiveExit body (so the service, the
    ' guardian's parity copy and OnStart still cannot drift), with the slot's own Spec
    ' driving the between-windows hold. Fail-closed on every axis by inheritance: an invalid
    ' MAC, an unparseable Until, an unparseable cooling-off deadline and an open window all
    ' read as "does not exit". A PENDING slot (Until = "") likewise never exits on time - but
    ' it CAN exit on a verified partner code or a completed cooling-off, which is correct: a
    ' block you scheduled for tomorrow must still be cancellable by the authorised exits.
    ' Pure + Shared.
    Friend Shared Function SlotEffectiveExit(ByVal slot As SlotState, ByVal highWaterText As String, ByVal graceSeconds As Long, ByVal macValid As Boolean) As Boolean
        If slot Is Nothing Then Return False
        Return EffectiveExit(slot.UntilText, slot.CoolOffUntil, slot.PartnerUnlockedAt,
                             slot.ScheduleActiveUntil, highWaterText, graceSeconds, macValid,
                             ScheduleArmed(macValid, slot.ScheduleSpec))
    End Function

    ' The live per-slot derivation the tick and OnStart take: read the slot's own state into
    ' ClassifySlot. asOf is the trusted high-water mark (never DateTime.Now). Pure + Shared.
    Friend Shared Function SlotExitDue(ByVal slot As SlotState, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal highWaterText As String) As SlotAction
        If slot Is Nothing Then Return SlotAction.Hold
        Return ClassifySlot(macValid,
                            SlotExpired(slot, asOf, graceSeconds),
                            CoolOffElapsedTime(slot.CoolOffUntil, highWaterText),
                            PartnerUnlocked(slot.PartnerUnlockedAt),
                            ScheduleActive(slot.ScheduleActiveUntil, highWaterText),
                            ScheduleArmed(macValid, slot.ScheduleSpec))
    End Function

    ' What the WHOLE MACHINE does this tick, once every due slot has been retired.
    Friend Enum TickAction
        TeardownAll   ' nothing is armed any more: run the P39 whole-machine teardown
        Restamp       ' something is still armed: advance Now/HighWater and re-stamp
        Hold          ' INVALID MAC: freeze - neither re-stamp nor tear anything down
    End Enum

    ' P39: the teardown gate. THREE things must all be true before a single byte of
    ' enforcement is undone:
    '   * the MAC is valid (an invalid one freezes, exactly as ClassifyHeartbeat does);
    '   * remainingSlotCount = 0 - and PENDING slots are counted, so a teardown can never
    '     eat a block that has not started yet;
    '   * the v9 residual has ALSO exited. This is the load-bearing demotion: `residual` is
    '     ClassifyHeartbeat over the v9 mirror keys, and its Lift is now merely NECESSARY,
    '     never sufficient. Back-dating [Time] Until while any slot is armed changes
    '     nothing at all; with no slots armed it still only removes a HOLD that the empty
    '     slot set had already removed. And the v9 schedule-only shape (SlotCount = 0, a
    '     [Schedule] Spec armed) keeps working unchanged, because its residual Restamps.
    ' Pure + Shared.
    Friend Shared Function ClassifyTick(ByVal macValid As Boolean, ByVal remainingSlotCount As Integer, ByVal residual As HeartbeatAction) As TickAction
        If Not macValid Then Return TickAction.Hold
        If remainingSlotCount > 0 Then Return TickAction.Restamp
        If residual <> HeartbeatAction.Lift Then Return TickAction.Restamp
        Return TickAction.TeardownAll
    End Function

    ' ---- P29: PENDING -> ACTIVE ----
    '
    ' A `--start` slot stores StartAt (absolute wall-clock, encrypted) + DurationSeconds
    ' (plaintext) and NO Until; the SERVICE computes the end at activation. Storing an
    ' absolute Until at arm time would UNDER-BLOCK after downtime - the wall clock runs on
    ' while the machine is off, so a 1h block armed for tonight and booted tomorrow would
    ' already be over. Refused.

    ' Has this PENDING slot's start moment arrived? Measured against the trusted HighWater,
    ' never DateTime.Now, so: a clock rolled FORWARD cannot start (and therefore cannot
    ' finish) a block early - HighWater refuses the jump; a clock rolled BACK before the
    ' start merely delays it (permitted-cancel-equivalent); and machine-off across the start
    ' moment yields the FULL duration measured from the moment HighWater catches up. Fail-
    ' closed: an unparseable StartAt or HighWater is NOT due, so the slot stays PENDING - and
    ' a PENDING slot still contributes its sites to every union, so "not due" over-blocks.
    ' Pure + Shared.
    Friend Shared Function SlotStartDue(ByVal slot As SlotState, ByVal highWaterText As String) As Boolean
        If Not SlotIsPending(slot) Then Return False
        Dim ca As New CultureInfo("en-CA")
        Dim startAt As DateTime, highWater As DateTime
        If Not DateTime.TryParse(slot.StartAt, ca, DateTimeStyles.None, startAt) Then Return False
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return False
        Return startAt <= highWater
    End Function

    ' The Until an activation persists: HighWater_now + DurationSeconds, in the SHAPE of
    ' ScheduleActiveUntil (same frame, same fail-closed "" on an unparseable mark) so the
    ' service-computed deadlines are derived identically. "" means "no deadline
    ' computable" => write NOTHING => the slot stays PENDING and is retried next tick, which
    ' over-blocks (a pending slot's sites are already enforced) and never lifts. Pure +
    ' Shared.
    Friend Shared Function ComputeSlotActivationUntil(ByVal highWaterText As String, ByVal durationSecondsText As String) As String
        Dim seconds As Long
        If Not Long.TryParse(If(durationSecondsText, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, seconds) Then Return ""
        If seconds <= 0 Then Return ""
        Dim ca As New CultureInfo("en-CA")
        Dim highWater As DateTime
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return ""
        Return highWater.AddSeconds(seconds).ToString(ca)
    End Function

    ' ---- P40/P41: the SLOT-ADDRESSED trigger channel ----
    '
    ' Every trigger now carries the id of the slot it addresses. The id is a ROUTING HINT
    ' with ZERO authority: an unknown, retired or garbage id deletes the trigger and changes
    ' nothing (no freeze - a freeze would let anyone wedge the machine by dropping junk), and
    ' a code is verified ONLY against the addressed slot's own MAC-covered Salt/Hash, so
    ' possessing slot A's code lifts slot A and nothing else. P17's never-reused ids are what
    ' make that safe: a replayed monkmode_partner.code.<id> can never come to address a
    ' different block.

    Friend Const CoolOffRequestPrefix As String = "monkmode_cooloff.request."
    Friend Const CoolOffCancelPrefix As String = "monkmode_cooloff.cancel."
    Friend Const PartnerCodePrefix As String = "monkmode_partner.code."
    ' P40: declared here so the four names live in one place. NOT consumed yet - `add` stays
    ' CLI-side until P42/S5 makes it service-adjudicated; declaring the name early is what
    ' stops S5 inventing a fifth spelling.
    Friend Const AddRequestPrefix As String = "monkmode_add.request."

    ' The shared cap on a content-bearing trigger read (was PartnerCodeTriggerMaxBytes; the
    ' `add` channel needs the same cap, so it is now one constant). A code is ~11 chars and a
    ' site list is short: anything above this is a memory/DoS lever, not a real request, so it
    ' reads as blank (=> Ignore => the trigger is deleted, no state change).
    Friend Const TriggerMaxBytes As Long = 4096

    ' P41: how many trigger files one tick will consume. It was sized 2 x MaxSlots for the two
    ' trigger families that existed then (a request and a cancel per armed slot); S3b/S5 brought
    ' the count to FOUR (the partner-code and `add` channels), so the true worst case is
    ' 4 x MaxSlots = 32. The constant is deliberately left at 16 (F40, accepted): the surplus is
    ' LEFT ON DISK for the next tick rather than deleted, so a full flood only DEFERS - deferring
    ' an EXIT trigger is fail-closed (the block simply holds ~10s longer) and deferring an `add`
    ' delays a widen by <= 10s. Note SelectTriggerFiles sorts ORDINAL, so under a flood
    ' `monkmode_add.request.*` is always served first and `monkmode_partner.code.*` last. The cap
    ' exists so a directory stuffed with 100k trigger files cannot stall the enforcement tick.
    Friend Const MaxTriggerFilesPerTick As Integer = 16

    ' The id a trigger file name addresses, or "" if it is not one of ours. Ordinal-
    ' case-insensitive prefix match (Windows file names are case-insensitive), and the
    ' remainder is taken VERBATIM apart from trimming - it is only ever compared to a stored
    ' Id, never parsed, so no numeric interpretation can widen it. Pure + Shared.
    Friend Shared Function TriggerIdFromName(ByVal fileName As String, ByVal prefix As String) As String
        If fileName Is Nothing OrElse prefix Is Nothing Then Return ""
        If Not fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then Return ""
        Return fileName.Substring(prefix.Length).Trim()
    End Function

    ' P41: the names this tick will consume - sorted ORDINAL (so the selection is
    ' deterministic and a starved trigger eventually leads the list) and capped. Pure +
    ' Shared so the cap is unit-pinned without a directory full of files.
    Friend Shared Function SelectTriggerFiles(ByVal names As List(Of String), ByVal maxPerTick As Integer) As List(Of String)
        Dim selected As New List(Of String)
        If names Is Nothing Then Return selected
        Dim sorted As New List(Of String)(names)
        sorted.Sort(StringComparer.Ordinal)
        For Each n As String In sorted
            If selected.Count >= maxPerTick Then Exit For
            selected.Add(n)
        Next
        Return selected
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

    ' F35 (v1.1 FX7): the CLOSING marker line. Every hosts write emits it directly below the
    ' entry lines, so the block has a known END and the strip stops there instead of running to
    ' EOF. Without it MonkMode's block had to be the last thing in the file, and a user line
    ' hand-added below it was destroyed at the next arm / self-heal rewrite / retire / expiry -
    ' with no hosts backup to recover from. Same ownership rule as the start marker: it only
    ' counts when it OWNS ITS WHOLE LINE (MonkMode never indents it and never writes anything
    ' after it), so a mid-line mention in a user's own line is user content.
    ' Line-for-line identical to MonkMode.Blocker.EndMarker and pinned by the parity tests.
    Friend Const HostsEndMarker As String = "#### MonkMode End ####"

    ' Parity copy of Blocker.NormalizeDomain: trim + lowercase, strip a pasted scheme/path.
    Private Shared Function NormalizeDomain(ByVal d As String) As String
        d = d.Trim().ToLowerInvariant()
        ' strip scheme and any path if a URL was pasted
        If d.Contains("://") Then d = d.Substring(d.IndexOf("://") + 3)
        Dim slash As Integer = d.IndexOf("/"c)
        If slash >= 0 Then d = d.Substring(0, slash)
        Return d.Trim()
    End Function

    ' Parity copy of Blocker.BuildHostsEntries: one "127.0.0.1 <domain>" line per site (plus
    ' "127.0.0.1 www./m./web./mobile.<domain>" mirror lines for a bare second-level domain),
    ' each CRLF-terminated. Uses 127.0.0.1 (NOT 0.0.0.0 - Windows' resolver ignores 0.0.0.0
    ' hosts entries). Byte-for-byte identical to the CLI so the synthesised schedule block
    ' matches a manual block's format.
    ' Friend Shared so the CLI<->service parity test (and EffectiveHostsBlock) can call it.
    Friend Shared Function BuildHostsEntries(ByVal domains As IEnumerable(Of String)) As String
        Dim sb As New System.Text.StringBuilder
        ' Dedup by host name so an explicit web.snapchat.com given ALONGSIDE the bare
        ' snapchat.com (which auto-expands the same mirror) never emits a repeated line;
        ' first occurrence wins, so order is stable.
        Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
        For Each raw As String In domains
            Dim d As String = NormalizeDomain(raw)
            If d = "" Then Continue For
            If seen.Add(d) Then sb.Append("127.0.0.1 ").Append(d).Append(vbCrLf)
            If Not d.StartsWith("www.") AndAlso d.IndexOf("."c) = d.LastIndexOf("."c) Then
                ' Bare second-level domain -> also block the common no-bypass web mirrors.
                ' web.snapchat.com is Snapchat's web app and m./mobile. are the usual
                ' mobile-web hosts (m.facebook.com, mobile.twitter.com): leaving them out
                ' lets the site load unblocked. Hosts lines for absent mirrors are inert,
                ' and in a self-control blocker over-blocking is acceptable while
                ' under-blocking is the sin, so we emit them all. Order fixed: bare first,
                ' then www./m./web./mobile.
                For Each prefix As String In HostsVariantPrefixes
                    Dim v As String = prefix & d
                    If seen.Add(v) Then sb.Append("127.0.0.1 ").Append(v).Append(vbCrLf)
                Next
            End If
        Next
        Return sb.ToString()
    End Function

    ' The subdomain prefixes a bare second-level domain expands into (in emit order) - the
    ' parity twin of Blocker.HostsVariantPrefixes. www. is the classic mirror; m./web./mobile.
    ' cover the mobile-web + web-app hosts that would otherwise be a casual bypass.
    Private Shared ReadOnly HostsVariantPrefixes As String() = {"www.", "m.", "web.", "mobile."}

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
    ' ClassifyPartnerCodeSignal/ProcessPartnerCodeSignal).
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

    ' ---- P37 (v1.1 S3a): hosts-snapshot RECONCILIATION, the single anti-race invariant ----
    '
    ' monkmode_hosts.block is the MAC-INDEPENDENT on-disk copy of what should be in hosts:
    ' the B2 self-heal repairs FROM it, and ReassertHostsFailClosed's crash backstop re-blocks
    ' FROM it. With N slots it can drift from config truth at a dozen crash points (an arm that
    ' wrote the config and died before the snapshot; a CLI arm racing a service retire; a
    ' snapshot hand-edited between ticks). Rather than patch each of those incrementally, EVERY
    ' tick compares the snapshot's entry SET with the union of the slots whose lists count now
    ' (SlotContributesLists - open OR pending) and, on ANY difference, rewrites the snapshot
    ' from config truth. That single step subsumes every incremental patch and self-heals every
    ' crash point, grow or shrink.
    '
    ' The truth set is deliberately the CONTRIBUTING slots, not the ENFORCING ones. A PENDING
    ' slot's sites are already in hosts and in this snapshot (the CLI wrote them at arm), and
    ' nothing in the tree turns PENDING into ACTIVE until S3b ships the P29 activation stamper
    ' - so reconciling against "enforcing" alone would strip a scheduled block's sites out of
    ' the heal source permanently, and every later repair/crash-reblock/reboot would rebuild
    ' hosts without them. See SlotContributesLists.
    '
    ' Two hard gates:
    '  * macValid = False => the snapshot is NEVER touched. A frozen config keeps the snapshot
    '    it has, for the self-heal and the crash backstop - the same fail-closed rule
    '    ClassifyScheduleSnapshot takes (every macValid=False case reads as a manual hold).
    '  * an EMPTY truth set => leave it alone (R1). "Nothing is enforcing" is exactly the state
    '    in which a rewrite would BLANK the snapshot, and a blanked snapshot is a torn-down
    '    block. Retiring a slot and clearing the snapshot at the end is the teardown path's
    '    job (stopMe deletes it), never this reconciler's.

    ' The entry lines of a hosts marker block as a SET: the marker line and blank lines are
    ' excluded, so a difference means a genuinely different set of blocked hosts rather than
    ' formatting noise. Pure + Shared.
    Friend Shared Function HostsBlockEntrySet(ByVal blockText As String) As HashSet(Of String)
        Dim entries As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If blockText Is Nothing OrElse blockText = "" Then Return entries
        For Each raw As String In blockText.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
            Dim ln As String = raw.Trim()
            If ln = "" OrElse String.Equals(ln, HostsMarker, StringComparison.Ordinal) _
               OrElse String.Equals(ln, HostsEndMarker, StringComparison.Ordinal) Then Continue For
            entries.Add(ln)
        Next
        Return entries
    End Function

    ' F35 (v1.1 FX7): the `add` verb's entry lines, placed INSIDE our marker block instead of
    ' appended at EOF. With an end marker the file no longer ends with our block, so a plain
    ' append would drop the added hosts BELOW the end marker, where they read as the user's own
    ' content: they would survive the expiry strip and stay in the user's hosts for ever (and be
    ' duplicated by the next self-heal, which restores them from the snapshot INSIDE the block).
    ' Inserting them immediately above the end marker keeps `add` exactly what it was - more of
    ' MonkMode's block - so the whole block still lifts cleanly.
    ' Both no-end-marker cases fall back to today's plain append, which is already correct there:
    ' no block of ours in the file (nothing to be inside), or a legacy block that runs to EOF
    ' (appending IS appending to it). Pure + Shared.
    Friend Shared Function InsertIntoHostsBlock(ByVal hostsText As String, ByVal toAdd As String) As String
        If hostsText Is Nothing Then hostsText = ""
        If String.IsNullOrEmpty(toAdd) Then Return hostsText
        Dim startpos As Integer = MarkerLineStart(hostsText)
        If startpos < 0 Then Return hostsText & toAdd
        Dim endpos As Integer = EndMarkerLineStart(hostsText, startpos)
        If endpos < 0 Then Return hostsText & toAdd
        Dim add As String = toAdd
        If Not (add.EndsWith(vbCrLf, StringComparison.Ordinal) OrElse add.EndsWith(vbLf, StringComparison.Ordinal) _
                OrElse add.EndsWith(vbCr, StringComparison.Ordinal)) Then add &= vbCrLf
        Return Microsoft.VisualBasic.Left(hostsText, endpos) & add & hostsText.Substring(endpos)
    End Function

    ' Do the snapshot and config truth disagree about WHICH hosts are blocked? Pure + Shared.
    Friend Shared Function SnapshotNeedsReconcile(ByVal snapshotText As String, ByVal expectedBlock As String) As Boolean
        Return Not HostsBlockEntrySet(snapshotText).SetEquals(HostsBlockEntrySet(expectedBlock))
    End Function

    ' The thin file-I/O wrapper (the ProcessScheduleSnapshot pattern - explicit path so unit
    ' tests drive it against a temp file, never the real snapshot). truthSites = the union of
    ' the ENFORCING slots' sites plus any open schedule's own sites. Best-effort; NEVER throws.
    Friend Shared Sub ReconcileHostsSnapshot(ByVal snapshotPath As String, ByVal macValid As Boolean, ByVal truthSites As List(Of String))
        Try
            If Not macValid Then Return                                         ' frozen: never touch it
            If truthSites Is Nothing OrElse truthSites.Count = 0 Then Return    ' R1: never blank it
            Dim entries As String = BuildHostsEntries(truthSites)
            If entries = "" Then Return                                         ' every site normalised away
            Dim expected As String = HostsMarker & vbCrLf & entries
            Dim existing As String = ""
            If System.IO.File.Exists(snapshotPath) Then existing = System.IO.File.ReadAllText(snapshotPath)
            If Not SnapshotNeedsReconcile(existing, expected) Then Return        ' already truth: no churn
            System.IO.File.WriteAllText(snapshotPath, expected)
        Catch ex As Exception
        End Try
    End Sub

    ' ---- the wall-clock window evaluator (pure; the schedule's WHEN half) ----
    '
    ' Reference types so the C# unit tests (InternalsVisibleTo) can inspect them as
    ' monkmode.Service1.ParsedSchedule / ScheduleWindow / ScheduleOpen.

    ' One recurring window: a day-of-week mask (bit 0 = Mon .. bit 6 = Sun), an open
    ' minute-of-day and a close minute-of-day. open < close is a same-day window; open >
    ' close is an OVERNIGHT (wrapped) window whose tail lands on the day AFTER the masked
    ' day (P20); open = close is never legal.
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

    ' The grammar-version tag the Spec always leads with, so the grammar can be extended
    ' without a canonical bump. Pinned by a unit test.
    '
    ' P19 (v1.1 S3a) - bumped "v1" -> "v2" WITH the overnight (wrapped) window grammar, and
    ' the bump is LOAD-BEARING, not cosmetic: under v1 code TryParseWindow skipped any token
    ' with openMin >= closeMin, so a v2 wrapped token ("2230-0400") fed to a v1 binary would
    ' VANISH silently - a fail-OPEN. With the tag bumped, a v2 Spec under a v1 binary fails
    ' the tag check and parses to ZERO windows instead (inert, and the v10 canonical has
    ' already frozen that config anyway, since a v1-era binary carries an older schema).
    Friend Const ScheduleSpecGrammarVersion As String = "v2"

    ' The LEGACY grammar tag, still accepted by the parser (never emitted). A v1 Spec means
    ' STRICT same-day windows (SD3): a v1 writer could not emit a wrapped token, so treating
    ' one as a wrap would be inventing a window nobody armed. Accepting v1 keeps an existing
    ' v1-armed schedule ENFORCED under new binaries (dropping it would be the under-block);
    ' it is inert in production anyway, because a config armed by a v1-era binary carries a
    ' pre-v10 schema and therefore fails the MAC -> freeze. The WRITER only ever emits
    ' ScheduleSpecGrammarVersion.
    Friend Const ScheduleSpecGrammarVersionLegacy As String = "v1"

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
    '   Spec := ("v1"|"v2") ";" windowList ";" "sites=" siteList ";" "apps=" appList
    '   window := dayMask ":" HHMM "-" HHMM   (dayMask = chars '1'..'7' = Mon..Sun)
    ' P20: under "v2" an END BEFORE the start means the window WRAPS past midnight
    ' (2230-0400); under the legacy "v1" tag a wrapped token is still skipped (SD3).
    Friend Shared Function ParseSchedule(ByVal specText As String) As ParsedSchedule
        Dim result As New ParsedSchedule()
        If String.IsNullOrWhiteSpace(specText) Then Return result
        Dim parts() As String = specText.Split(";"c)
        ' Need at least the version tag + the window list; an unknown tag is inert.
        If parts.Length < 2 Then Return result
        ' The TAG selects the grammar: only v2 admits wrapped (overnight) windows.
        Dim tag As String = parts(0).Trim()
        Dim allowWrap As Boolean
        If tag = ScheduleSpecGrammarVersion Then
            allowWrap = True
        ElseIf tag = ScheduleSpecGrammarVersionLegacy Then
            allowWrap = False
        Else
            Return result
        End If
        ' Windows (comma-separated); skip any malformed one, keep the rest.
        For Each winTok As String In parts(1).Split(","c)
            Dim w As ScheduleWindow = TryParseWindow(winTok, allowWrap)
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
    ' Rejects any out-of-range time or day. P20: openMin = closeMin is rejected in EVERY
    ' grammar (zero-length, and "24 hours" would be an ambiguous second meaning for the same
    ' token); openMin > closeMin is a WRAPPED overnight window under v2 (allowWrap) and the
    ' old SD3 same-day rejection under the legacy v1 tag.
    Private Shared Function TryParseWindow(ByVal token As String, ByVal allowWrap As Boolean) As ScheduleWindow
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
        If openMin = closeMin Then Return Nothing          ' zero-length / ambiguous 24h - never legal
        If openMin > closeMin AndAlso Not allowWrap Then Return Nothing   ' SD3 under the v1 grammar
        Dim w As New ScheduleWindow()
        w.DayMask = mask
        w.OpenMinutes = openMin
        w.CloseMinutes = closeMin
        Return w
    End Function

    ' P20: does this window WRAP past midnight (its end is BEFORE its start)? The wrap is
    ' derived from the times themselves - there is no stored flag to forge or to drift.
    Friend Shared Function WindowIsWrapped(ByVal w As ScheduleWindow) As Boolean
        Return w.OpenMinutes > w.CloseMinutes
    End Function

    ' P20: a window's length in minutes - (1440 - open + close) when it wraps past midnight,
    ' (close - open) otherwise. Used for the SD4 jump-OVER duration and for the jump-over
    ' close instant, so both stay correct for a wrapped window without a second formula.
    Friend Shared Function WindowDurationMinutes(ByVal w As ScheduleWindow) As Integer
        If WindowIsWrapped(w) Then Return 1440 - w.OpenMinutes + w.CloseMinutes
        Return w.CloseMinutes - w.OpenMinutes
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
    '     on a matching day -> OPEN, remaining = close - now. For a WRAPPED window (P22)
    '     "inside" is either today's pre-midnight segment (remaining = to midnight + close)
    '     or the post-midnight tail of YESTERDAY's masked opening (remaining = close - now).
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
            ' P22: the wrapped segment belongs to the START day's mask, so "inside" is three
            ' cases, not one. (c) is the one that matters most: after midnight the calendar day
            ' has ROLLED, so today's mask no longer names this window - only YESTERDAY's does.
            ' Omitting (c) would drop the hold across a midnight reboot (isBoot re-evaluates
            ' from scratch with no stored ActiveUntil to fall back on) = fail-OPEN.
            Dim insideRemaining As Long = -1
            If Not WindowIsWrapped(w) Then
                ' (a) same-day window: today's mask AND open <= now < close.
                If ScheduleDayMatches(nowDt, w.DayMask) AndAlso nowSec >= openSec AndAlso nowSec < closeSec Then
                    insideRemaining = CLng(Math.Ceiling(closeSec - nowSec))
                End If
            ElseIf ScheduleDayMatches(nowDt, w.DayMask) AndAlso nowSec >= openSec Then
                ' (b) wrapped, and we are in TODAY's pre-midnight segment: run to midnight,
                ' then on to the close on the far side.
                insideRemaining = CLng(Math.Ceiling((86400.0 - nowSec) + closeSec))
            ElseIf ScheduleDayMatches(nowDt.Date.AddDays(-1), w.DayMask) AndAlso nowSec < closeSec Then
                ' (c) wrapped, and we are in the post-midnight tail of YESTERDAY's opening.
                insideRemaining = CLng(Math.Ceiling(closeSec - nowSec))
            End If
            If insideRemaining >= 0 Then
                ' INSIDE now (covers normal ticks, forward-jump-INTO, boot-inside).
                opens.Add(NewScheduleOpen(w, insideRemaining))
            ElseIf wallIsJump AndAlso ScheduleJumpedOver(w, lastNowDt, nowDt) Then
                ' LIVE jump-OVER a whole window: enforce its FULL duration (SD4).
                opens.Add(NewScheduleOpen(w, WindowDurationMinutes(w) * 60L))
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
    ' close (open + duration; the NEXT day for a wrapped window, P23) is at/before
    ' now? Bounded backward scan from now.Date (day-of-week
    ' repeats weekly, so a matching day is always within ~7 days of now; the 366 cap
    ' just bounds a pathological multi-year jump). Only called when wallIsJump is
    ' already established, so this is existence-only; the enforced duration is always
    ' the full window length regardless of which day was skipped.
    Private Shared Function ScheduleJumpedOver(ByVal w As ScheduleWindow, ByVal lastNowDt As DateTime, ByVal nowDt As DateTime) As Boolean
        Dim d As DateTime = nowDt.Date
        Dim guard As Integer = 0
        While d >= lastNowDt.Date AndAlso guard <= 366
            If ScheduleDayMatches(d, w.DayMask) Then
                ' P23: the close instant is the open instant PLUS the window's length, which
                ' for a wrapped window lands on the NEXT day. Deriving it this way keeps the
                ' same-day case byte-identical (d + open + (close - open) = d + close).
                Dim openInstant As DateTime = d.AddMinutes(w.OpenMinutes)
                Dim closeInstant As DateTime = openInstant.AddMinutes(WindowDurationMinutes(w))
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

    ' F31 (v1.1 FX2): the index of the first marker occurrence that OWNS ITS
    ' WHOLE LINE - it starts at position 0 or immediately after a line
    ' terminator (LF, which also covers CRLF, or a bare CR), AND is followed
    ' immediately by a line terminator or end-of-file. -1 when there is no such
    ' occurrence.
    '
    ' Why anchoring is load-bearing: the strip cuts the file at this index and
    ' discards everything below it. Matching the marker ANYWHERE in the text
    ' meant a user's own hosts line that merely MENTIONED the marker (e.g.
    ' "# the #### MonkMode Entries #### block is MonkMode's") was truncated
    ' mid-line and every user line below it silently deleted - a breach of the
    ' paramount no-data-loss fence, with no hosts backup to recover from.
    '
    ' Whole-line, column 0, deliberately: MonkMode itself ALWAYS writes the
    ' marker as a line of its own with no indent and nothing after it
    ' (BuildMonkModeBlock / UnionHostsBlock / the schedule synthesiser all emit
    ' Marker & vbCrLf & entries), so an indented, glued or commented-on marker
    ' can never be ours - it is user content and is preserved verbatim. The
    ' bias is deliberate: failing to recognise our own block leaves stale
    ' entries in hosts (over-block, acceptable); mistaking the user's text for
    ' ours destroys their data (never acceptable).
    ' Line-for-line identical to MonkMode.Blocker.MarkerLineStart and pinned by
    ' the CLI<->service parity tests.
    Friend Shared Function MarkerLineStart(ByVal text As String) As Integer
        Return MarkerLineStartFrom(text, HostsMarker, 0)
    End Function

    ' F35 (v1.1 FX7): the index of the first line-anchored END marker at or below
    ' searchFrom, -1 when there is none. Searching FROM the start marker is
    ' deliberate: an end marker sitting ABOVE our block is a user line, never a
    ' close of ours.
    Friend Shared Function EndMarkerLineStart(ByVal text As String, ByVal searchFrom As Integer) As Integer
        Return MarkerLineStartFrom(text, HostsEndMarker, searchFrom)
    End Function

    ' The shared anchored search both markers use (F31's rule, generalised by F35).
    Private Shared Function MarkerLineStartFrom(ByVal text As String, ByVal marker As String, ByVal searchFrom As Integer) As Integer
        If text Is Nothing Then Return -1
        If searchFrom < 0 Then searchFrom = 0
        Do While searchFrom <= text.Length - marker.Length
            Dim idx As Integer = text.IndexOf(marker, searchFrom, StringComparison.Ordinal)
            If idx < 0 Then Return -1
            If IsWholeLine(text, idx, marker.Length) Then Return idx
            ' Mid-line hit: user content. Keep looking BELOW it - a real,
            ' line-anchored block further down must still be stripped.
            searchFrom = idx + 1
        Loop
        Return -1
    End Function

    ' True when text[start, start+length) is bounded by line terminators or the
    ' ends of the text - i.e. it is a complete line. Helper for MarkerLineStart;
    ' identical to MonkMode.Blocker.IsWholeLine.
    Private Shared Function IsWholeLine(ByVal text As String, ByVal start As Integer, ByVal length As Integer) As Boolean
        If start > 0 Then
            Dim prev As Char = text.Chars(start - 1)
            If prev <> CChar(vbLf) AndAlso prev <> CChar(vbCr) Then Return False
        End If
        Dim after As Integer = start + length
        If after < text.Length Then
            Dim nxt As Char = text.Chars(after)
            If nxt <> CChar(vbLf) AndAlso nxt <> CChar(vbCr) Then Return False
        End If
        Return True
    End Function

    ' The user's own content ABOVE our block: the text cut at the marker line,
    ' with the single line terminator the writer placed before it dropped.
    ' (This is what StripMonkModeBlock returned in full before F35 added the end
    ' marker; it is now the "head" half, and the writers re-append the block
    ' after it exactly as before.) Shared and file-system-free.
    Friend Shared Function HostsAboveBlock(ByVal fileReader As String) As String

        Dim original As String = ""
        Dim startpos As Integer = 0

        ' Ordinal, case-sensitive — the same comparison the stopMe() gate and
        ' the CLI use. The old case-insensitive InStr(..., CompareMethod.Text)
        ' could lock onto a hand-edited case-variant marker line ABOVE the real
        ' one and delete the user's own hosts lines between the two.
        ' F31: and line-anchored, so a marker MENTIONED inside a user line is
        ' never mistaken for the start of our block.
        startpos = MarkerLineStart(fileReader)
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

    ' F35 (v1.1 FX7): the user's own content BELOW our block - everything after the
    ' END marker line and the single line terminator that closes it, byte-for-byte.
    ' "" when there is no block, or when the block carries no end marker (a LEGACY
    ' block written before FX7: it owns everything down to EOF, so there is nothing
    ' below it by definition - see StripMonkModeBlock's legacy rule).
    Friend Shared Function HostsBelowBlock(ByVal text As String) As String
        If text Is Nothing Then Return ""
        Dim startpos As Integer = MarkerLineStart(text)
        If startpos < 0 Then Return ""
        Dim endpos As Integer = EndMarkerLineStart(text, startpos)
        If endpos < 0 Then Return ""
        Dim after As Integer = endpos + HostsEndMarker.Length
        ' Drop exactly ONE line terminator - the one closing our end-marker line.
        ' A blank line the user left below our block is theirs and survives.
        If after < text.Length Then
            If String.CompareOrdinal(text, after, vbCrLf, 0, 2) = 0 Then
                after += 2
            ElseIf text.Chars(after) = CChar(vbLf) OrElse text.Chars(after) = CChar(vbCr) Then
                after += 1
            End If
        End If
        If after >= text.Length Then Return ""
        Return text.Substring(after)
    End Function

    ' Returns the hosts-file text with the MonkMode marker block removed, leaving
    ' the user's own content - above AND below it - untouched. Shared and
    ' file-system-free so it can be unit tested.
    '
    ' F35 (v1.1 FX7): "the block" is now marker line -> END marker line INCLUSIVE.
    ' Everything below the end marker is the user's and is preserved byte-for-byte.
    '
    ' LEGACY RULE (a block written before FX7 has no end marker): the block runs to
    ' EOF, exactly as it always did. That is the only rule that can be right for it -
    ' every line of a legacy block was written by MonkMode as of that write, and
    ' nothing in the file distinguishes a line the user appended afterwards from one
    ' of ours (their line can look exactly like ours: "127.0.0.1 my-dev-box"). Of the
    ' two possible errors, keeping the strip whole over-removes MonkMode's own lines
    ' (never a lift) while a guess at where our lines stop would leave ours behind in
    ' the user's file for ever. The window is transitional and self-closing: the very
    ' first write by FX7 code (arm, self-heal repair, retire, or the crash backstop)
    ' end-markers the block, and from then on content below it is safe for good.
    Friend Shared Function StripMonkModeBlock(ByVal fileReader As String) As String
        Dim startpos As Integer = MarkerLineStart(fileReader)
        If startpos < 0 Then Return fileReader
        Dim below As String = HostsBelowBlock(fileReader)
        ' No end marker, or nothing below it: byte-identical to the pre-F35 strip.
        If below.Length = 0 Then Return HostsAboveBlock(fileReader)
        ' Keep the terminator that separated the user's content from our marker
        ' line: it now joins the two halves of their file back together.
        Return Microsoft.VisualBasic.Left(fileReader, startpos) & below
    End Function

    ' F35 (v1.1 FX7): the block text a WRITER is about to put into hosts, guaranteed
    ' to carry a closing end-marker line. Idempotent - a block that already has one
    ' anywhere is returned untouched, so repeated writes never stack markers and the
    ' self-heal never churns. Callers have already refused an empty block (never
    ' invent content), so an empty input is passed straight back.
    Friend Shared Function EnsureBlockEndMarker(ByVal block As String) As String
        If String.IsNullOrEmpty(block) Then Return block
        ' Search from the block's OWN start marker, never from index 0: an anchored end
        ' marker ABOVE the start marker (a hand-tampered snapshot) is not a close of this
        ' block, and treating it as one would emit a block with no End below its marker -
        ' which the strip then reads by the LEGACY rule and takes the re-seated user tail
        ' with it. A block with no start marker at all searches from 0, unchanged.
        If EndMarkerLineStart(block, Math.Max(0, MarkerLineStart(block))) >= 0 Then Return block
        Dim s As String = block
        If Not (s.EndsWith(vbCrLf, StringComparison.Ordinal) OrElse s.EndsWith(vbLf, StringComparison.Ordinal) _
                OrElse s.EndsWith(vbCr, StringComparison.Ordinal)) Then s &= vbCrLf
        Return s & HostsEndMarker & vbCrLf
    End Function

    ' F35 (v1.1 FX7): re-attach the user's below-the-block content to a rewritten
    ' hosts text, keeping it BELOW our block. Position is load-bearing, not cosmetic:
    ' hoisting a user line above our entries would let their "1.2.3.4 x.com" win the
    ' resolver's first-match over our "127.0.0.1 x.com" - a rewrite that narrows the
    ' block. A terminator is inserted only if the block did not end with one.
    Friend Shared Function AppendUserTail(ByVal textEndingWithOurBlock As String, ByVal below As String) As String
        If String.IsNullOrEmpty(below) Then Return textEndingWithOurBlock
        Dim s As String = textEndingWithOurBlock
        If s.Length > 0 AndAlso Not (s.EndsWith(vbCrLf, StringComparison.Ordinal) OrElse s.EndsWith(vbLf, StringComparison.Ordinal) _
                                     OrElse s.EndsWith(vbCr, StringComparison.Ordinal)) Then s &= vbCrLf
        Return s & below
    End Function

    ' Decides whether hosts needs its MonkMode block restored (B2 self-heal)
    ' and, if so, returns the full repaired hosts text; returns Nothing when no
    ' repair is needed. expectedBlock is the snapshot the CLI persisted when
    ' the block started (the marker line + entry lines, exactly as appended to
    ' hosts). Semantics:
    '   - null/empty/whitespace snapshot -> Nothing (never invent content);
    '   - hosts already contains the snapshot exactly, end marker included
    '     (ordinal) -> Nothing, so an intact block never causes a rewrite;
    '   - otherwise: the user's own content above (HostsAboveBlock removes any
    '     partial/tampered remnant of our block, preserving the rest
    '     byte-for-byte) + a single CRLF separator + expectedBlock + its end
    '     marker + the user's own content BELOW the old end marker, in place
    '     (F35). A blanked hosts file repairs to the snapshot alone.
    ' The snapshot on disk is stored WITHOUT the end marker (it is the block the
    ' CLI/reconciler build: marker + entry lines); the end marker is added here,
    ' at the hosts boundary, so snapshot-format compatibility is untouched.
    ' Shared and file-system-free so it can be unit tested.
    Friend Shared Function RepairHostsBlock(ByVal hostsText As String, ByVal expectedBlock As String) As String

        If String.IsNullOrWhiteSpace(expectedBlock) Then
            Return Nothing
        End If
        If hostsText Is Nothing Then
            hostsText = ""
        End If
        ' F35: the block we expect to find - and to write - carries its end marker. Testing
        ' for the END-MARKERED form is what makes a deleted end marker read as tampering
        ' INSIDE the block (repair it, like any other edit to our lines) and what converges
        ' a legacy pre-FX7 block onto the end-markered form in ONE rewrite.
        Dim block As String = EnsureBlockEndMarker(expectedBlock)
        If hostsText.IndexOf(block, StringComparison.Ordinal) >= 0 Then
            Return Nothing
        End If

        Dim userContent As String = HostsAboveBlock(hostsText)
        Dim below As String = HostsBelowBlock(hostsText)
        If userContent.Length = 0 Then
            Return AppendUserTail(block, below)
        End If
        Return AppendUserTail(userContent & vbCrLf & block, below)
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

    ' B7/B4: builds the v11 two-level canonical the MAC is computed over, from a
    ' loaded ini - the global header, then one 16-line block per slot for
    ' pos = 1 To the CLAMPED [Slots] SlotCount. Every party (this writer, plus the
    ' service/guardian/notifier readers) must derive a byte-identical string or the
    ' MAC never validates and every block freezes, so BELOW the `crypt` alias line
    ' this body is byte-identical text in all four copies - diff them on any edit.
    ' [Integrity] Key/Mac are excluded (you can't MAC the MAC); missing values pass
    ' through as "". Stale [SlotN] sections beyond SlotCount are never read.
    Friend Function CanonicalFromIni(ByVal ini As IniFile) As String
        Dim crypt As Simple3Des = encryptionW      ' the ONLY line that differs across the four copies
        ' Globals: HighWater/Now/[Guard] HoldUntil are ENCRYPTED datetimes (decrypted
        ' here, like every datetime); [Slots] NextSlotId/SlotCount and [Guard]
        ' ArmedCount are plaintext ints (the MAC is their protection).
        Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
        Dim nowEnc As String = ini.GetKeyValue("CurrentTime", "Now")
        Dim guardHoldEnc As String = ini.GetKeyValue("Guard", "HoldUntil")
        Dim highWaterPlain As String = If(highWaterEnc = "", "", crypt.DecryptData(highWaterEnc))
        Dim nowPlain As String = If(nowEnc = "", "", crypt.DecryptData(nowEnc))
        Dim guardHoldPlain As String = If(guardHoldEnc = "", "", crypt.DecryptData(guardHoldEnc))

        ' FX1 (v11): the GLOBAL [Schedule] pair - the entire enforcement state of a
        ' SCHEDULE-ONLY (v9-shaped, slot-less) config, which `monkmode schedule` still
        ' writes and the service still enforces from. Spec is plaintext-as-stored (a
        ' window rule is not a secret; the MAC is its protection); the service-written
        ' ActiveUntil is an ENCRYPTED datetime, decrypted like the globals above.
        ' Deliberately distinct local names from the PER-SLOT pair read inside the loop
        ' below - they are different fields and must never be confused.
        Dim globalScheduleSpec As String = ini.GetKeyValue("Schedule", "Spec")
        Dim globalScheduleActiveEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")
        Dim globalScheduleActivePlain As String = If(globalScheduleActiveEnc = "", "", crypt.DecryptData(globalScheduleActiveEnc))

        ' F77 (v12): the GLOBAL [Time] TrustedUtc anchor - the UTC instant at which
        ' [Time] HighWater was last known correct. ENCRYPTED like the datetimes above, but
        ' stored in INVARIANT UTC (ConfigIntegrity.TrustedUtcFormat) rather than en-CA LOCAL,
        ' so a timezone change moves neither it nor the credit derived from it. MAC-covered
        ' because back-dating it is an early-lift primitive (the next probe would credit the
        ' difference). Absent reads "" and passes as "" - an unseeded anchor simply earns no
        ' downtime credit, which is the fail-closed direction.
        Dim trustedUtcEnc As String = ini.GetKeyValue("Time", "TrustedUtc")
        Dim trustedUtcPlain As String = If(trustedUtcEnc = "", "", crypt.DecryptData(trustedUtcEnc))

        ' The CLAMPED count is BOTH the header value and the loop bound, so a forged
        ' SlotCount can only ever build a canonical nothing can match -> freeze.
        Dim slotCount As Integer = ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"))
        Dim slots As New System.Text.StringBuilder()
        For pos As Integer = 1 To slotCount
            Dim sec As String = "Slot" & pos.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ' Per slot, the ENCRYPTED datetimes are Until/StartAt/CoolOffUntil/
            ' ScheduleActiveUntil; everything else - INCLUDING Sites/Apps/UrlPatterns -
            ' is plaintext-as-stored. No "null" sentinel any more (v10): "no apps" is "".
            Dim untilEnc As String = ini.GetKeyValue(sec, "Until")
            Dim startAtEnc As String = ini.GetKeyValue(sec, "StartAt")
            Dim coolOffEnc As String = ini.GetKeyValue(sec, "CoolOffUntil")
            Dim scheduleActiveEnc As String = ini.GetKeyValue(sec, "ScheduleActiveUntil")
            slots.Append(ConfigIntegrity.BuildSlotCanonical(pos,
                ini.GetKeyValue(sec, "Id"),
                If(startAtEnc = "", "", crypt.DecryptData(startAtEnc)),
                ini.GetKeyValue(sec, "DurationSeconds"),
                If(untilEnc = "", "", crypt.DecryptData(untilEnc)),
                ini.GetKeyValue(sec, "Sites"),
                ini.GetKeyValue(sec, "Apps"),
                ini.GetKeyValue(sec, "UrlPatterns"),
                ini.GetKeyValue(sec, "AllSession"),
                ini.GetKeyValue(sec, "ScheduleSpec"),
                If(scheduleActiveEnc = "", "", crypt.DecryptData(scheduleActiveEnc)),
                If(coolOffEnc = "", "", crypt.DecryptData(coolOffEnc)),
                ini.GetKeyValue(sec, "CoolOffDuration"),
                ini.GetKeyValue(sec, "PartnerSalt"),
                ini.GetKeyValue(sec, "PartnerHash"),
                ini.GetKeyValue(sec, "PartnerUnlockedAt"),
                ini.GetKeyValue(sec, "Committed")))
        Next

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion,
                                             highWaterPlain,
                                             nowPlain,
                                             ini.GetKeyValue("Slots", "NextSlotId"),
                                             slotCount,
                                             guardHoldPlain,
                                             ini.GetKeyValue("Guard", "ArmedCount"),
                                             globalScheduleSpec,
                                             globalScheduleActivePlain,
                                             trustedUtcPlain,
                                             slots.ToString())
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

    ' 313(b): the hosts half of the genuine-expiry teardown, split out of stopMe() with an
    ' explicit path so the unit tests can drive it against a temp file (the
    ' ReassertHostsFailClosed / ProcessAddToHosts pattern; fence: unit tests never touch the
    ' real hosts). Reached ONLY from stopMe(), i.e. only from ClassifyTick's Lift arm - a
    ' MAC-valid config whose end time has genuinely passed.
    '
    ' THE ONE BEHAVIOUR CHANGE (Samrath, 30/08/2026): after a genuine expiry hosts is left with
    ' NORMAL attributes, not read-only. The read-only bit is enforcement - the DNS-client lock -
    ' and once our marker block is gone there is nothing left to enforce; leaving it set made
    ' every later hosts writer (Tailscale, a DNS tool) fail until a manual `attrib -r`. The CLI
    ' teardown has always ended this way (Blocker.ClearReadOnly), so this is the service's
    ' natural expiry matching it. ONLY this path changes: the per-tick B2 self-heal, OnStart and
    ' the crash backstop still re-assert read-only, because those run while a block STANDS.
    '
    ' FAIL-CLOSED ON FAILURE: the attribute is cleared only AFTER the strip has been written.
    ' If the write throws, hosts still carries the block, so it is re-locked (best-effort) and
    ' the exception is rethrown exactly as before - a still-blocked, WRITABLE hosts is the one
    ' state this must never leave behind. Ledger 319 finished the job on the NO-MARKER branch:
    ' it used to re-assert read-only, which left an unblocked machine with a permanently
    ' read-only hosts file. Both exit branches now end vbNormal.
    '
    ' Returns True when the marker block was found and stripped - the caller then marks the
    ' config Done, exactly as before.
    Friend Shared Function StripHostsBlockAtExpiry(ByVal hostsPath As String) As Boolean

        Dim fileReader As String = ""
        Dim original As String = ""
        Dim hostsFileNeedsRemoval As Boolean = False

        If My.Computer.FileSystem.FileExists(hostsPath) Then
            SetAttr(hostsPath, vbNormal)
            fileReader = My.Computer.FileSystem.ReadAllText(hostsPath)
            If fileReader.Contains("#### MonkMode Entries ####") Then
                hostsFileNeedsRemoval = True
            End If
        End If

        If hostsFileNeedsRemoval Then
            original = StripMonkModeBlock(fileReader)

            ' C1: atomic write (temp + rename) so a crash while stripping our block
            ' at expiry can never blank hosts or lose the user's own entries
            ' (read-only was cleared above).
            Try
                AtomicHosts.WriteAtomic(hostsPath, original)
            Catch ex As Exception
                ' The block is still in hosts and the attribute is currently CLEAR: re-lock
                ' before letting the failure out, so a failed teardown never degrades the
                ' enforcement it failed to end.
                Try
                    SetAttr(hostsPath, vbReadOnly)
                Catch ex2 As Exception
                End Try
                Throw
            End Try
            ' The block is gone: leave hosts as an ordinary file again.
            SetAttr(hostsPath, vbNormal)
            Return True
        End If

        ' F78 residual (ledger 319 rider): vbNormal, NOT vbReadOnly. This is the no-marker
        ' branch of the EXPIRY strip - the block is ending and there is nothing of ours in
        ' hosts to remove. Re-asserting read-only here left a machine with no block holding a
        ' read-only hosts file for ever (nothing ever clears it again), which is the same
        ' leftover 313(b) removed from the stripped branch. Leaving hosts an ordinary file is
        ' the correct end state on BOTH exit branches. The failed-strip branch above still
        ' re-locks and rethrows: there, the block is still enforced and must stay that way.
        SetAttr(hostsPath, vbNormal)
        Return False
    End Function

    Private Sub stopMe()

        If StripHostsBlockAtExpiry(hostDirS) Then
            Dim iniFile = New IniFile
            iniFile.Load(Application.StartupPath + "\monkmode_settings.ini")
            iniFile.SetKeyValue("User", "Done", "yes")
            iniFile.Save(Application.StartupPath + "\monkmode_settings.ini")
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

        ' v1.1 S3b: and every SLOT-ADDRESSED trigger (P40). The unsuffixed deletes above are
        ' kept only to clear a legacy file an old CLI may have left; the live channel is
        ' <prefix><id>, and leaving one behind would have the NEXT block's arm inherit a
        ' cooling-off request or a stale candidate the moment it takes that id's successor.
        Try
            For Each pattern As String In New String() {CoolOffRequestPrefix & "*", CoolOffCancelPrefix & "*", PartnerCodePrefix & "*", AddRequestPrefix & "*"}
                For Each stale As String In System.IO.Directory.GetFiles(Application.StartupPath, pattern)
                    Try
                        System.IO.File.Delete(stale)
                    Catch ex As Exception
                    End Try
                Next
            Next
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
        ' left DELETE-denied until the next install rewrites the DACL - ledger 319 removed
        ' the CLI's `RestoreDefaultServiceSd`, so this call is now the ONLY thing that
        ' re-grants DELETE. Without it, an expired block leaves a service that resists
        ' sc-delete indefinitely.
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
                    ' F35: insert INSIDE our block (above the end marker) rather than at EOF,
                    ' where the lines would read as the user's own and never lift. Read +
                    ' atomic rewrite, like every other hosts writer; a throw here is caught
                    ' below with the trigger still on disk, so the add is retried, never lost.
                    Dim hostsNow As String = ""
                    If System.IO.File.Exists(hostsPath) Then hostsNow = System.IO.File.ReadAllText(hostsPath)
                    AtomicHosts.WriteAtomic(hostsPath, InsertIntoHostsBlock(hostsNow, toAdd))
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