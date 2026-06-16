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
            If iniFile.Sections.Count < 2 Then
                ' B7 fail-closed: a truncated/blanked ini (fewer than the expected
                ' sections) is treated EXACTLY like a corrupt one - rewrite the
                ' UNSTAMPED default block and keep enforcing, NEVER stopMe(). The
                ' old code called stopMe() here, which let an attacker lift the
                ' block by truncating monkmode_settings.ini to <2 sections and
                ' forcing a restart (no MAC forge needed) - strictly easier than
                ' the recover-the-3DES-key attack B7 exists to stop. The default
                ' is left unstamped, so the readers fail CLOSED (macValid = False)
                ' and it holds until re-armed from the CLI.
                WriteDefaultBlock()
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
                Dim newHw As String = storedHw
                Dim asOfHw As DateTime = DateTime.MinValue
                Dim parsedHw As DateTime
                If DateTime.TryParse(newHw, culture, DateTimeStyles.None, parsedHw) Then asOfHw = parsedHw

                Dim macValidAtStart As Boolean = ConfigMacIsValidForIni(iniFile)
                If EffectiveBlockHasExpired(encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until")), asOfHw, 0, macValidAtStart) Then
                    ' The ONLY OnStart path that may lift the block: a successfully
                    ' parsed, genuinely past end time (measured against the trusted
                    ' high-water mark) AND a valid B7 MAC. An unparseable Until or a
                    ' tampered/invalid MAC keeps the block standing (fail closed).
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
            ' Corrupt/unreadable ini: rewrite the safe default 7-day block (fail
            ' closed, same as the <2 sections branch above).
            WriteDefaultBlock()
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

        ' B3: register under SafeBoot so enforcement survives a Safe Mode reboot.
        ' Only reached on the active path - an expired/invalid block calls stopMe()
        ' above, which Ends the process before here (and removes any stale keys).
        AssertSafeBootRegistration()

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

    ' B4 creep fix: a MONOTONIC anchor (Environment.TickCount64, ms since boot -
    ' immune to wall-clock changes) captured at the last HighWater advance. The
    ' per-tick HighWater credit is capped by the real elapsed since this anchor, so
    ' nudging the wall clock forward each tick can't advance the mark faster than
    ' real time. Seeded at OnStart; 0 = not yet seeded (=> credit 0 that tick).
    Private lastMonoMs As Long = 0

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
            iniUntil = encryptionW.DecryptData(iniFile.GetKeyValue("Time", "Until"))
            iniTimeChanging = iniFile.GetKeyValue("Time", "TimeChanging")
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
            Dim monoElapsedSeconds As Long = If(lastMonoMs <= 0, 0L, (nowMono - lastMonoMs) \ 1000L)
            lastMonoMs = nowMono
            Dim candidateHw As String = NextHighWater(storedHw, DateTime.Now.ToString(culture), HighWaterJumpCeilingSeconds)
            newHw = CapHighWaterAdvance(storedHw, candidateHw, monoElapsedSeconds)
            Dim parsedHw As DateTime
            If DateTime.TryParse(newHw, culture, DateTimeStyles.None, parsedHw) Then
                newHwAsOf = parsedHw
            End If
        Catch ex As Exception
            ' Corrupt/unreadable ini: rewrite the default 7-day block, left
            ' UNSTAMPED on purpose (see the OnStart catch for the rationale) -
            ' macValid stays False this tick so the block holds; re-arm from the
            ' CLI to get a fresh MAC and a liftable block.
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
            ' B4: seed HighWater in the recovery default too (kept consistent with
            ' the CLI-armed shape). This default is UNSTAMPED, so macValid stays
            ' False and the block holds regardless of HighWater - but seeding it
            ' keeps the ini shape uniform and gives the next tick a parseable base.
            iniFile.SetKeyValue("Time", "HighWater", encryptionW.EncryptData(DateTime.Now.ToString(culture)))
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
        ' EffectiveBlockHasExpired fails CLOSED: unparseable Until OR an invalid
        ' B7 MAC = active) restore our entries from the snapshot the CLI
        ' persisted next to the exe.
        ' B4: asOf is newHwAsOf (the trusted high-water mark), NOT DateTime.Now,
        ' so a clock-forward can't flip this to "expired" and stop the repair.
        ' Try/Catch so a transient lock can never crash the service.
        Try
            Dim snapshotPath As String = Application.StartupPath + "\monkmode_hosts.block"
            If Not EffectiveBlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid) AndAlso My.Computer.FileSystem.FileExists(snapshotPath) Then
                Dim hostsText As String = ""
                If My.Computer.FileSystem.FileExists(hostDirS) Then
                    hostsText = My.Computer.FileSystem.ReadAllText(hostDirS)
                End If
                Dim repaired As String = RepairHostsBlock(hostsText, My.Computer.FileSystem.ReadAllText(snapshotPath))
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
            ' the guardian be dropped early.
            If ShouldRestartPeer(System.Diagnostics.Process.GetProcessesByName("mm_guard").Length,
                                 Not EffectiveBlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid),
                                 My.Computer.FileSystem.FileExists(guardianExe)) Then
                System.Diagnostics.Process.Start(guardianExe)
            End If
        Catch ex As Exception
        End Try

        ' B3 SafeBoot self-heal: re-assert the Safe Mode registration every tick
        ' while the block is active (an admin can delete the keys between ticks).
        ' Fail CLOSED via Not EffectiveBlockHasExpired - an unparseable Until OR
        ' an invalid B7 MAC keeps the keys asserted; stopMe() removes them at a
        ' genuine expiry. B4: asOf is newHwAsOf (trusted high-water mark), not
        ' DateTime.Now, so a clock-forward can't drop the keys early.
        Try
            If Not EffectiveBlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid) Then
                AssertSafeBootRegistration()
            End If
        Catch ex As Exception
        End Try

        ' B6 deny-DELETE self-heal: re-assert the service-object deny-DELETE ACE
        ' every tick while the block is active (an admin with WRITE_DAC can clear
        ' it between ticks, as a casual `sc sdset`/Process-Explorer re-ACL). Fail
        ' CLOSED via Not EffectiveBlockHasExpired - an unparseable Until OR an
        ' invalid B7 MAC keeps the deny on; stopMe() removes it at genuine expiry.
        ' B4: asOf is newHwAsOf (trusted high-water mark), not DateTime.Now, so a
        ' clock-forward can't drop the deny early. Read-only probe inside makes an
        ' intact DACL a no-op (no churn). Best-effort - never crash the tick.
        Try
            If Not EffectiveBlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds, macValid) Then
                AssertDenyDeleteAce()
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
            Select Case ClassifyHeartbeat(macValid, BlockHasExpired(iniUntil, newHwAsOf, ExpiryGraceSeconds))
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
        Lift     ' valid MAC + genuinely past end time => stopMe()
        Restamp  ' valid MAC, not yet expired => rewrite Now/HighWater + re-stamp the MAC
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
    '   * Re-stamp ONLY when macValid (and not expired): the service's own
    '     Now/HighWater writes are MAC-covered, so a LEGIT config must be
    '     re-stamped or its MAC would go stale and needlessly freeze it.
    '   * HOLD when the MAC is invalid: NEVER re-stamp over an unverified config
    '     (that is the bug) and never lift. The block stays frozen until re-armed.
    ' A regression test pins ClassifyHeartbeat(macValid:=False, blockExpired:=True)
    ' = Hold (the old code would have re-stamped here). Pure + Shared so the
    ' guardian-parity-style unit tests can pin it.
    Friend Shared Function ClassifyHeartbeat(ByVal macValid As Boolean, ByVal blockExpired As Boolean) As HeartbeatAction
        If Not macValid Then Return HeartbeatAction.Hold
        If blockExpired Then Return HeartbeatAction.Lift
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
        Dim procEnc As String = iniFile.GetKeyValue("Process", "List")
        Dim nowEnc As String = iniFile.GetKeyValue("CurrentTime", "Now")
        Dim sites As String = iniFile.GetKeyValue("User", "CustomSites")

        Dim untilPlain As String = If(untilEnc = "", "", encryptionW.DecryptData(untilEnc))
        Dim highWaterPlain As String = If(highWaterEnc = "", "", encryptionW.DecryptData(highWaterEnc))
        Dim procPlain As String = If(procEnc = "" OrElse procEnc = "null", procEnc, encryptionW.DecryptData(procEnc))
        Dim nowPlain As String = If(nowEnc = "", "", encryptionW.DecryptData(nowEnc))

        Return ConfigIntegrity.BuildCanonical(untilPlain, procPlain, sites, nowPlain, highWaterPlain)
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

        ' B3: drop the SafeBoot registration too - a genuinely expired block must
        ' leave nothing that keeps the (about-to-stop) service starting in Safe
        ' Mode. Best-effort; the OnStart path also removes stale keys on a restart
        ' into an already-expired block.
        RemoveSafeBootRegistration()

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

    Private Sub adder_Changed(ByVal sender As System.Object, ByVal e As System.IO.FileSystemEventArgs) Handles adder.Changed

        ' FAIL-OPEN FIX (audit #2): this is a FileSystemWatcher callback. It
        ' clears hosts' read-only attribute to append, so an unhandled throw
        ' here (a locked/IO-erroring hosts, a transient read) both crashes the
        ' LocalSystem service AND leaves hosts WRITABLE - a fail-OPEN window
        ' against the fail-closed doctrine. Mirror the timer self-heal pattern:
        ' Try/Catch the whole body so the watcher thread/service survives, and a
        ' Finally that ALWAYS re-asserts read-only so hosts is never left
        ' writable on any exit path. Best-effort append; a failed add must never
        ' weaken the block.
        Try
            If My.Computer.FileSystem.FileExists(sWinDir & "\system32\drivers\etc\add_to_hosts") Then
                Dim toAdd As String
                toAdd = System.IO.File.ReadAllText(sWinDir & "\system32\drivers\etc\add_to_hosts")
                SetAttr(hostDirS, vbNormal)
                System.IO.File.AppendAllText(hostDirS, toAdd)
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
        Catch ex As Exception
            ' Swallow so a throw can never escape the watcher callback and crash
            ' the service. The Finally below re-locks hosts (fail-closed).
        Finally
            ' ALWAYS re-assert read-only - even if the append threw after the
            ' attribute was cleared, hosts must not be left writable. Guarded +
            ' best-effort so the re-lock itself can never throw out of Finally.
            Try
                If My.Computer.FileSystem.FileExists(hostDirS) Then SetAttr(hostDirS, vbReadOnly)
            Catch ex As Exception
            End Try
        End Try

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