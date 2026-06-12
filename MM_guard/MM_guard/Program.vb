'    MonkMode - guardian entry point (B1 watchdog, layer 2)
'
'    mm_guard.exe is the SYSTEM-session guardian half of the mutual
'    service <-> guardian restart pair (ARCHITECTURE.md B1, decision (A) locked
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
        ' closes the race of two ticks both deciding to spawn.
        Dim createdNew As Boolean = False
        Dim mtx As New Mutex(True, "Global\MonkModeGuardian", createdNew)
        If Not createdNew Then Return

        Try
            Do
                ' Sleep first: the service that just spawned us has already
                ' verified the world this tick - act on the NEXT tick.
                Thread.Sleep(TickIntervalMs)

                Dim blockActive As Boolean = Not Guardian.BlockHasExpired(ReadBlockUntil(), DateTime.Now, ExpiryGraceSeconds)
                If Not blockActive Then
                    ' Genuinely expired (parsed, past end time): stand down for
                    ' good. The service's stopMe() also kills us at expiry;
                    ' this is the fallback if we outlive that.
                    Exit Do
                End If

                TryRestartService(blockActive)
                TryRelaunchNotifier(blockActive)
            Loop
        Finally
            mtx.Dispose()
        End Try
    End Sub

    ' The decrypted [Time] Until value, or "" when the ini/value is missing or
    ' undecryptable. "" is unparseable, and unparseable fails CLOSED (the block
    ' reads as still active) - a deleted or corrupted config must keep the
    ' guardian guarding, never stand it down.
    Private Function ReadBlockUntil() As String
        Try
            Dim ini As New IniFile
            ini.Load(Path.Combine(AppContext.BaseDirectory, IniName))
            Return enc.DecryptData(ini.GetKeyValue("Time", "Until"))
        Catch ex As Exception
            Return ""
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
