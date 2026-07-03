'    MonkMode - guardian entry point (B1 watchdog, layer 2)
'
'    mm_guard.exe is the SYSTEM-session guardian half of the mutual
'    service <-> guardian restart pair (vault\dev\monk-mode\specs\ARCHITECTURE.md B1, decision (A) locked
'    13/06/2026). The service's timer spawns it (gate: Service1.ShouldRestartPeer)
'    and re-spawns it if it is killed; reciprocally, every tick this loop:
'
'      1. reads [Time] Until from monkmode_settings.ini next to the exes and
'         EXITS once the block has genuinely expired (parsed, past end time -
'         the only way the guardian ever stands down; an unparseable value
'         fails CLOSED and it keeps guarding);
'      2. restarts the MONKMODE service via the SCM if it is not running
'         (it has SCM rights: spawned by the LocalSystem service, it IS SYSTEM);
'      3. relaunches the user-session notifier (mm_notify.exe) if it has been
'         killed - the proper version of the old MM_notify2 twin's job. A
'         SYSTEM process can't just Process.Start into the interactive session,
'         so this uses WTSQueryUserToken + CreateProcessAsUser (no user logged
'         on -> skipped, retried next tick).
'
'    All decisions go through the pure gates in Guardian.vb (unit-tested); all
'    actions are best-effort Try/Catch and retried on the next tick, so a
'    transient failure can never kill the guardian loop itself.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.IO
Imports System.Runtime.InteropServices
Imports System.ServiceProcess
Imports System.Text
Imports System.Threading

Module Program

    Friend Const ServiceName As String = "MONKMODE"
    Friend Const NotifierExeName As String = "mm_notify.exe"
    Friend Const NotifierProcessName As String = "mm_notify"
    Friend Const IniName As String = "monkmode_settings.ini"

    ' B7 tamper-evident config: same [Integrity] section the CLI stamps. The
    ' guardian reads (never writes) it - a MAC failure keeps it guarding.
    Friend Const IntegritySection As String = "Integrity"
    Friend Const IntegrityKeyName As String = "Key"
    Friend Const IntegrityMacName As String = "Mac"

    ' Same cadence and expiry grace as the service's timer (10s tick, 5s grace)
    ' so both halves of the pair agree on "expired" within one tick of each
    ' other. Pinned by the unit tests - keep in sync with Service1.
    Friend Const TickIntervalMs As Integer = 10000
    Friend Const ExpiryGraceSeconds As Long = 5

    Private ReadOnly enc As New Simple3Des("mm_textbox")

    Sub Main()
        ' Single instance, machine-wide ("Global\" so the SYSTEM-session copy
        ' also excludes any copy started in a user session, and vice versa).
        ' The service's spawn gate already counts processes, but the mutex
        ' closes the race of two ticks both deciding to spawn. Creating a
        ' Global\ object needs SeCreateGlobalPrivilege - a stray non-elevated
        ' launch (the guardian is only meant to be SYSTEM-spawned) throws
        ' here, so exit quietly instead of crashing unhandled.
        Dim createdNew As Boolean = False
        Dim mtx As Mutex
        Try
            mtx = New Mutex(True, "Global\MonkModeGuardian", createdNew)
        Catch ex As Exception
            Return
        End Try
        If Not createdNew Then
            mtx.Dispose()
            Return
        End If

        ' AppDomain.UnhandledException backstop (fail-closed on crash) - see
        ' OnUnhandledException. Registered only once we are the single real
        ' guardian instance (a throwaway second instance returned above).
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnUnhandledException

        Try
            Do
                ' Sleep first: the service that just spawned us has already
                ' verified the world this tick - act on the NEXT tick.
                Thread.Sleep(TickIntervalMs)

                ' Read [Time] Until, [Time] HighWater, [Time] CoolOffUntil, the C3b
                ' [Partner] UnlockedAt and the B7 MAC validity in one ini load.
                Dim until As String = ""
                Dim highWater As String = ""
                Dim coolOffUntil As String = ""
                Dim unlockedAt As String = ""
                Dim scheduleActiveUntil As String = ""
                Dim spec As String = ""
                Dim macValid As Boolean = False
                ReadBlockState(until, highWater, coolOffUntil, unlockedAt, scheduleActiveUntil, spec, macValid)

                ' Fail CLOSED on every axis: an unparseable Until OR an invalid/
                ' absent B7 MAC (a tampered config) reads as NOT ended, so the
                ' guardian keeps guarding. Only a valid MAC AND (a parsed, past
                ' end time OR an elapsed cooling-off deadline) stands it down -
                ' exactly Service1's exit semantics (EffectiveExit parity), so the
                ' pair never disagree. B4: both are measured against the service-
                ' written HighWater mark (parsed inside EffectiveExit; unparseable
                ' => MinValue => NOT ended), never raw DateTime.Now, so a rolled
                ' clock can't stand the guardian down. C2b: folding cooling-off in
                ' here is LOAD-BEARING - without it the guardian would SCM-restart
                ' the service the moment a completed cooling-off tears it down,
                ' resurrecting the cooled-off block. C3b: folding the partner-code
                ' UnlockedAt in is LOAD-BEARING for the identical reason - the
                ' guardian must not resurrect a just-code-unlocked block. C5b (c2): folding
                ' scheduleArmed in keeps the guardian alive BETWEEN windows of a recurring
                ' schedule (so it can restart a killed service for tomorrow's window) and stands
                ' it down (terminal) only once the Spec is cleared. Cheap over-approximation -
                ' the Spec non-empty, NOT a 4th ParseSchedule copy (design §4C): the fail-SAFE
                ' direction (a garbage non-empty Spec only over-guards, never an early stand-down).
                ' macValid AndAlso so a tampered/frozen config never reads as armed (EffectiveExit
                ' holds on its macValid gate anyway). Null-safe (IsNullOrWhiteSpace) inside the helper.
                Dim scheduleArmed As Boolean = Guardian.ScheduleArmed(macValid, spec)
                Dim blockActive As Boolean = Not Guardian.EffectiveExit(until, coolOffUntil, unlockedAt, scheduleActiveUntil, highWater, ExpiryGraceSeconds, macValid, scheduleArmed)
                If Not blockActive Then
                    ' Genuinely expired (parsed, past end time, valid MAC): stand
                    ' down for good. The service's stopMe() also kills us at
                    ' expiry; this is the fallback if we outlive that.
                    Exit Do
                End If

                TryRestartService(blockActive)
                TryRelaunchNotifier(blockActive)
            Loop
        Finally
            mtx.Dispose()
        End Try
    End Sub

    ' AppDomain.UnhandledException backstop (fail-closed on crash). The guardian
    ' holds no hosts/registry/config state of its own - it only READS the ini and
    ' drives the SCM/notifier - so its own crash leaves nothing fail-OPEN. But its
    ' whole purpose is to keep the enforcement core alive, so if an exception is
    ' about to kill it, make ONE best-effort pass to (re)start the MONKMODE service
    ' while the block is still active before dying - so the guardian's own death
    ' never leaves the service down. Reuses the EXACT fail-closed gates the loop
    ' uses (ReadBlockState fails CLOSED on any read/MAC error; the HighWater asOf;
    ' Guardian.EffectiveBlockHasExpired; Guardian.ShouldRestartService inside
    ' TryRestartService). The service reciprocally re-spawns the guardian
    ' (ShouldRestartPeer), so this is defence-in-depth. Never throws.
    Private Sub OnUnhandledException(ByVal sender As Object, ByVal e As UnhandledExceptionEventArgs)
        Try
            Dim until As String = ""
            Dim highWater As String = ""
            Dim coolOffUntil As String = ""
            Dim unlockedAt As String = ""
            Dim scheduleActiveUntil As String = ""
            Dim spec As String = ""
            Dim macValid As Boolean = False
            ReadBlockState(until, highWater, coolOffUntil, unlockedAt, scheduleActiveUntil, spec, macValid)
            ' C2b/C3b/C5b: same EffectiveExit gate as the loop - the dying guardian must
            ' not restart the service into a block that just cooled off, was code-unlocked
            ' OR whose scheduled window has closed (nor stand down mid-window: an open
            ' window holds via ScheduleActive). C5b (c2): scheduleArmed (same cheap Spec-
            ' non-empty over-approximation as the loop) also keeps the dying guardian from
            ' standing down BETWEEN windows of a live schedule.
            Dim scheduleArmed As Boolean = Guardian.ScheduleArmed(macValid, spec)
            Dim blockActive As Boolean = Not Guardian.EffectiveExit(until, coolOffUntil, unlockedAt, scheduleActiveUntil, highWater, ExpiryGraceSeconds, macValid, scheduleArmed)
            TryRestartService(blockActive)
        Catch ex As Exception
        End Try
    End Sub

    ' Reads the block state from a single ini load: the decrypted [Time] Until
    ' (untilOut), the decrypted [Time] HighWater (highWaterOut, B4), the
    ' decrypted [Time] CoolOffUntil (coolOffUntilOut, C2b) and the B7 MAC
    ' validity (macValidOut). All fail CLOSED on any error - untilOut "" is
    ' unparseable (block reads active), highWaterOut "" parses to MinValue
    ' (reads active), coolOffUntilOut "" means no cooling-off pending (never an
    ' early stand-down), macValidOut False means a tampered/unreadable config
    ' also reads active - so a deleted or corrupted config keeps the guardian
    ' guarding, never stands it down. One load (not four) so Until, HighWater,
    ' CoolOffUntil, ScheduleActiveUntil, the [Schedule] Spec and the MAC are all evaluated
    ' against the same bytes. C5b: scheduleActiveOut is the decrypted [Schedule] ActiveUntil
    ' ("" = no window open, the fail-closed default); the guardian only READS it (the
    ' service is its sole writer, like HighWater/CoolOffUntil), and folding
    ' ScheduleActive into the stand-down (via EffectiveExit) is LOAD-BEARING - without
    ' it the guardian could stand down at a window's start (not restart a killed
    ' service mid-window) or resurrect the block at its close. C5b (c2): specOut is the
    ' [Schedule] Spec (plaintext, as-stored - like UnlockedAt), from which the caller derives
    ' scheduleArmed as the cheap Spec-non-empty over-approximation (no ParseSchedule copy in
    ' the guardian); "" => not armed. Folding scheduleArmed into the stand-down keeps the
    ' guardian alive BETWEEN windows so it can restart a killed service for the next one.
    Private Sub ReadBlockState(ByRef untilOut As String, ByRef highWaterOut As String, ByRef coolOffUntilOut As String, ByRef unlockedOut As String, ByRef scheduleActiveOut As String, ByRef specOut As String, ByRef macValidOut As Boolean)
        untilOut = ""
        highWaterOut = ""
        coolOffUntilOut = ""
        unlockedOut = ""
        scheduleActiveOut = ""
        specOut = ""
        macValidOut = False
        Try
            Dim ini As New IniFile
            ini.Load(Path.Combine(AppContext.BaseDirectory, IniName))
            untilOut = enc.DecryptData(ini.GetKeyValue("Time", "Until"))
            highWaterOut = enc.DecryptData(ini.GetKeyValue("Time", "HighWater"))
            Dim coolOffEnc As String = ini.GetKeyValue("Time", "CoolOffUntil")
            coolOffUntilOut = If(coolOffEnc = "", "", enc.DecryptData(coolOffEnc))
            ' C3b: [Partner] UnlockedAt is plaintext-as-stored (MAC-covered), not
            ' decrypted; absent/"" = not code-unlocked (never an early stand-down).
            unlockedOut = ini.GetKeyValue("Partner", "UnlockedAt")
            ' C5b: [Schedule] ActiveUntil is an encrypted datetime like CoolOffUntil
            ' ("" = no window open). Absent/unreadable => "" (fail-closed: no phantom
            ' window; a genuine window's deadline is covered by the same MAC).
            Dim scheduleEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")
            scheduleActiveOut = If(scheduleEnc = "", "", enc.DecryptData(scheduleEnc))
            ' C5b (c2): [Schedule] Spec is plaintext (as-stored, MAC-covered), not decrypted.
            specOut = ini.GetKeyValue("Schedule", "Spec")
            macValidOut = ConfigMacIsValidForIni(ini)
        Catch ex As Exception
        End Try
    End Sub

    ' B7: build the canonical (decrypted plaintext, fixed order) the MAC is over.
    ' Byte-identical construction to the CLI/service/notifier readers. Friend (not
    ' Private) so the end-to-end parity tests can prove this reader agrees with the
    ' CLI writer and the other readers - a tautological BuildCanonical literal
    ' comparison would miss a drift in THIS wrapper.
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

    ' B7 live MAC gate (DPAPI seam - smoke-tested). DPAPI-unprotect [Integrity]
    ' Key and validate [Integrity] Mac over the canonical. False on ANY failure
    ' (missing/blank/non-Base64 key or MAC, DPAPI denial, foreign-machine blob,
    ' crypto error) so a tamper reads as "keep guarding", never "stand down".
    Private Function ConfigMacIsValidForIni(ByVal ini As IniFile) As Boolean
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return False
            Return ConfigIntegrity.ConfigMacIsValid(CanonicalFromIni(ini), ini.GetKeyValue(IntegritySection, IntegrityMacName), key)
        Catch ex As Exception
            Return False
        End Try
    End Function

    Private Sub TryRestartService(ByVal blockActive As Boolean)
        Try
            Using sc As New ServiceController(ServiceName)
                Dim running As Boolean =
                    (sc.Status = ServiceControllerStatus.Running OrElse
                     sc.Status = ServiceControllerStatus.StartPending)
                If Guardian.ShouldRestartService(blockActive, running) Then
                    sc.Start()
                End If
            End Using
        Catch ex As Exception
            ' Service deleted/SCM denied/start race - retry next tick.
        End Try
    End Sub

    Private Sub TryRelaunchNotifier(ByVal blockActive As Boolean)
        Try
            Dim notifierExe As String = Path.Combine(AppContext.BaseDirectory, NotifierExeName)
            Dim count As Integer = Process.GetProcessesByName(NotifierProcessName).Length
            If Guardian.ShouldRelaunchNotifier(count, blockActive, File.Exists(notifierExe)) Then
                LaunchInActiveUserSession(notifierExe)
            End If
        Catch ex As Exception
            ' Best effort - retry next tick.
        End Try
    End Sub

#Region "Launch into the interactive user session (SYSTEM -> user)"

    ' A SYSTEM-session process can't Process.Start a program into the
    ' interactive desktop - it would land invisibly in session 0. The supported
    ' route is: active console session id -> that user's primary token
    ' (WTSQueryUserToken, needs SE_TCB which SYSTEM has) -> CreateProcessAsUser
    ' on winsta0\default with the user's environment block. Every failure path
    ' just returns; the caller retries on a later tick (e.g. once someone is
    ' actually logged on).

    Private Const CREATE_UNICODE_ENVIRONMENT As Integer = &H400
    Private Const INVALID_SESSION_ID As UInteger = &HFFFFFFFFUI

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure STARTUPINFO
        Public cb As Integer
        Public lpReserved As String
        Public lpDesktop As String
        Public lpTitle As String
        Public dwX As Integer
        Public dwY As Integer
        Public dwXSize As Integer
        Public dwYSize As Integer
        Public dwXCountChars As Integer
        Public dwYCountChars As Integer
        Public dwFillAttribute As Integer
        Public dwFlags As Integer
        Public wShowWindow As Short
        Public cbReserved2 As Short
        Public lpReserved2 As IntPtr
        Public hStdInput As IntPtr
        Public hStdOutput As IntPtr
        Public hStdError As IntPtr
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure PROCESS_INFORMATION
        Public hProcess As IntPtr
        Public hThread As IntPtr
        Public dwProcessId As Integer
        Public dwThreadId As Integer
    End Structure

    <DllImport("kernel32.dll")>
    Private Function WTSGetActiveConsoleSessionId() As UInteger
    End Function

    <DllImport("wtsapi32.dll", SetLastError:=True)>
    Private Function WTSQueryUserToken(ByVal sessionId As UInteger, ByRef phToken As IntPtr) As Boolean
    End Function

    <DllImport("userenv.dll", SetLastError:=True)>
    Private Function CreateEnvironmentBlock(ByRef lpEnvironment As IntPtr, ByVal hToken As IntPtr, ByVal bInherit As Boolean) As Boolean
    End Function

    <DllImport("userenv.dll", SetLastError:=True)>
    Private Function DestroyEnvironmentBlock(ByVal lpEnvironment As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="CreateProcessAsUserW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CreateProcessAsUser(ByVal hToken As IntPtr, ByVal lpApplicationName As String,
        ByVal lpCommandLine As StringBuilder, ByVal lpProcessAttributes As IntPtr,
        ByVal lpThreadAttributes As IntPtr, ByVal bInheritHandles As Boolean,
        ByVal dwCreationFlags As Integer, ByVal lpEnvironment As IntPtr,
        ByVal lpCurrentDirectory As String, ByRef lpStartupInfo As STARTUPINFO,
        ByRef lpProcessInformation As PROCESS_INFORMATION) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function CloseHandle(ByVal hObject As IntPtr) As Boolean
    End Function

    Private Sub LaunchInActiveUserSession(ByVal exePath As String)
        Dim sessionId As UInteger = WTSGetActiveConsoleSessionId()
        If sessionId = INVALID_SESSION_ID Then Return ' no console session attached

        Dim userToken As IntPtr = IntPtr.Zero
        If Not WTSQueryUserToken(sessionId, userToken) Then Return ' nobody logged on (or not SYSTEM)

        Dim envBlock As IntPtr = IntPtr.Zero
        Try
            If Not CreateEnvironmentBlock(envBlock, userToken, False) Then
                envBlock = IntPtr.Zero ' fall back to inheriting ours
            End If

            Dim si As New STARTUPINFO
            si.cb = Marshal.SizeOf(GetType(STARTUPINFO))
            si.lpDesktop = "winsta0\default"
            Dim pi As New PROCESS_INFORMATION

            ' CreateProcessW may scribble on the command-line buffer, so it must
            ' be mutable (StringBuilder). Quote the path against spaces.
            Dim cmd As New StringBuilder("""" & exePath & """")
            If CreateProcessAsUser(userToken, exePath, cmd, IntPtr.Zero, IntPtr.Zero, False,
                                   CREATE_UNICODE_ENVIRONMENT, envBlock,
                                   Path.GetDirectoryName(exePath), si, pi) Then
                If pi.hProcess <> IntPtr.Zero Then CloseHandle(pi.hProcess)
                If pi.hThread <> IntPtr.Zero Then CloseHandle(pi.hThread)
            End If
        Finally
            If envBlock <> IntPtr.Zero Then DestroyEnvironmentBlock(envBlock)
            CloseHandle(userToken)
        End Try
    End Sub

#End Region

End Module
