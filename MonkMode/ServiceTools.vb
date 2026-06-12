'    MonkMode - ServiceTools
'
'    Modern replacement for the third-party "ServiceTools" helper that the
'    original Cold Turkey sources referenced (Imports ServiceTools) but never
'    shipped in source form. Provides the two entry points the GUI uses:
'
'        ServiceInstaller.InstallAndStart(serviceName, displayName, binaryPath)
'        ServiceInstaller.StartService(serviceName)
'
'    Implemented directly against the Windows Service Control Manager
'    (advapi32) so MonkMode has no external dependency for service setup.
'    The GUI runs elevated (see app.manifest), which is required for these
'    SCM operations.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.ServiceProcess

Namespace Global.ServiceTools

    Public NotInheritable Class ServiceInstaller

        Private Sub New()
        End Sub

        ' --- SCM access rights ---
        Private Const SC_MANAGER_ALL_ACCESS As UInteger = &H3F
        Private Const SERVICE_ALL_ACCESS As UInteger = &HF01FF
        Private Const SERVICE_QUERY_STATUS As UInteger = &H4

        ' --- Service type / start type / error control ---
        Private Const SERVICE_WIN32_OWN_PROCESS As UInteger = &H10
        Private Const SERVICE_AUTO_START As UInteger = &H2
        Private Const SERVICE_ERROR_NORMAL As UInteger = &H1

        ' --- Service recovery (B1 watchdog, layer 1: SCM auto-restart) ---
        ' ChangeServiceConfig2 info levels.
        Private Const SERVICE_CONFIG_FAILURE_ACTIONS As Integer = 2
        Private Const SERVICE_CONFIG_FAILURE_ACTIONS_FLAG As Integer = 4

        ' THE recovery policy (single source of truth — the live SetRecoveryOptions
        ' marshals exactly these and the unit tests pin them; weakening any one of
        ' them weakens B1, so the test fails loudly if they drift):
        '   - RESTART on every failure, applied THREE times so the SCM keeps
        '     restarting on the 1st, 2nd and every subsequent failure (it reuses
        '     the last action for all further failures);
        '   - 1s after the kill;
        '   - reset period = INFINITE, so the failure count NEVER resets and
        '     recovery never "gives up" no matter how many times it is killed;
        '   - on non-crash failures too, so a force-kill that the SCM treats as a
        '     clean-but-unexpected stop still triggers a restart.
        Friend Const RecoveryActionTypeRestart As Integer = 1          ' SC_ACTION_RESTART
        Friend Const RecoveryActionCount As Integer = 3
        Friend Const RecoveryRestartDelayMs As UInteger = 1000UI
        Friend Const RecoveryResetPeriodSeconds As UInteger = &HFFFFFFFFUI ' INFINITE
        Friend Const RecoveryRestartOnNonCrash As Boolean = True

        <DllImport("advapi32.dll", EntryPoint:="OpenSCManagerW", CharSet:=CharSet.Unicode, SetLastError:=True)> _
        Private Shared Function OpenSCManager(ByVal machineName As String, ByVal databaseName As String, ByVal dwAccess As UInteger) As IntPtr
        End Function

        <DllImport("advapi32.dll", EntryPoint:="OpenServiceW", CharSet:=CharSet.Unicode, SetLastError:=True)> _
        Private Shared Function OpenService(ByVal hSCManager As IntPtr, ByVal serviceName As String, ByVal dwDesiredAccess As UInteger) As IntPtr
        End Function

        <DllImport("advapi32.dll", EntryPoint:="CreateServiceW", CharSet:=CharSet.Unicode, SetLastError:=True)> _
        Private Shared Function CreateService(ByVal hSCManager As IntPtr, ByVal serviceName As String, ByVal displayName As String, _
            ByVal dwDesiredAccess As UInteger, ByVal dwServiceType As UInteger, ByVal dwStartType As UInteger, ByVal dwErrorControl As UInteger, _
            ByVal binaryPathName As String, ByVal loadOrderGroup As String, ByVal lpdwTagId As IntPtr, ByVal dependencies As String, _
            ByVal serviceStartName As String, ByVal password As String) As IntPtr
        End Function

        <DllImport("advapi32.dll", SetLastError:=True)> _
        Private Shared Function CloseServiceHandle(ByVal hSCObject As IntPtr) As Boolean
        End Function

        <DllImport("advapi32.dll", EntryPoint:="ChangeServiceConfig2W", CharSet:=CharSet.Unicode, SetLastError:=True)> _
        Private Shared Function ChangeServiceConfig2(ByVal hService As IntPtr, ByVal dwInfoLevel As Integer, ByVal lpInfo As IntPtr) As Boolean
        End Function

        <StructLayout(LayoutKind.Sequential)> _
        Private Structure SC_ACTION
            Public Type As Integer
            Public Delay As UInteger
        End Structure

        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)> _
        Private Structure SERVICE_FAILURE_ACTIONS
            Public dwResetPeriod As UInteger
            Public lpRebootMsg As String
            Public lpCommand As String
            Public cActions As UInteger
            Public lpsaActions As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)> _
        Private Structure SERVICE_FAILURE_ACTIONS_FLAG
            Public fFailureActionsOnNonCrashFailures As Boolean
        End Structure

        ''' <summary>True if a service with the given name is already installed.</summary>
        Public Shared Function ServiceIsInstalled(ByVal serviceName As String) As Boolean
            For Each sc As ServiceController In ServiceController.GetServices()
                If String.Equals(sc.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            Next
            Return False
        End Function

        ''' <summary>
        ''' Install the service (LocalSystem, automatic start) if it is not already
        ''' present, then start it. Mirrors the original ServiceTools API.
        ''' </summary>
        Public Shared Sub InstallAndStart(ByVal serviceName As String, ByVal displayName As String, ByVal binaryPath As String)
            If Not ServiceIsInstalled(serviceName) Then
                InstallService(serviceName, displayName, binaryPath)
            End If
            ' B1 watchdog, layer 1: ask the SCM to auto-restart the service if it
            ' is force-killed. Best-effort — recovery hardening must never block a
            ' block from actually starting, so a failure here is swallowed.
            Try
                SetRecoveryOptions(serviceName)
            Catch
            End Try
            StartService(serviceName)
        End Sub

        ''' <summary>Create the service via the SCM (LocalSystem, auto start).</summary>
        Public Shared Sub InstallService(ByVal serviceName As String, ByVal displayName As String, ByVal binaryPath As String)
            Dim scm As IntPtr = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then
                Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager (administrator rights required).")
            End If

            Dim svc As IntPtr = IntPtr.Zero
            Try
                ' Quote the binary path so spaces in the install directory are handled.
                Dim quotedPath As String = """" & binaryPath & """"
                svc = CreateService(scm, serviceName, displayName, SERVICE_ALL_ACCESS, _
                    SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, _
                    quotedPath, Nothing, IntPtr.Zero, Nothing, Nothing, Nothing)
                If svc = IntPtr.Zero Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not create the MonkMode service.")
                End If
            Finally
                If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
                CloseServiceHandle(scm)
            End Try
        End Sub

        ''' <summary>
        ''' Configure the SCM to auto-restart the service after an abnormal
        ''' termination (B1 watchdog, layer 1). Restarts on every failure forever,
        ''' 1s after the kill, including non-crash failures. Requires admin (the
        ''' CLI runs elevated). Throws on failure so InstallAndStart can swallow it.
        ''' Friend, not Public: it mutates the live SCM, so only InstallAndStart
        ''' (and the unit-test assembly, via InternalsVisibleTo) should reach it.
        ''' </summary>
        Friend Shared Sub SetRecoveryOptions(ByVal serviceName As String)
            Dim scm As IntPtr = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then
                Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager (administrator rights required).")
            End If

            Dim svc As IntPtr = IntPtr.Zero
            Dim actionsPtr As IntPtr = IntPtr.Zero
            Try
                svc = OpenService(scm, serviceName, SERVICE_ALL_ACCESS)
                If svc = IntPtr.Zero Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the MonkMode service to set recovery options.")
                End If

                ' Build the SC_ACTION array (RecoveryActionCount RESTART actions)
                ' in unmanaged memory.
                Dim actionSize As Integer = Marshal.SizeOf(GetType(SC_ACTION))
                actionsPtr = Marshal.AllocHGlobal(actionSize * RecoveryActionCount)
                For i As Integer = 0 To RecoveryActionCount - 1
                    Dim act As New SC_ACTION
                    act.Type = RecoveryActionTypeRestart
                    act.Delay = RecoveryRestartDelayMs
                    Marshal.StructureToPtr(act, IntPtr.Add(actionsPtr, i * actionSize), False)
                Next

                Dim fa As New SERVICE_FAILURE_ACTIONS
                fa.dwResetPeriod = RecoveryResetPeriodSeconds
                fa.lpRebootMsg = Nothing
                fa.lpCommand = Nothing
                fa.cActions = CUInt(RecoveryActionCount)
                fa.lpsaActions = actionsPtr

                Dim faPtr As IntPtr = Marshal.AllocHGlobal(Marshal.SizeOf(GetType(SERVICE_FAILURE_ACTIONS)))
                Try
                    Marshal.StructureToPtr(fa, faPtr, False)
                    If Not ChangeServiceConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS, faPtr) Then
                        Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not set MonkMode service recovery actions.")
                    End If
                Finally
                    Marshal.FreeHGlobal(faPtr)
                End Try

                ' Also restart on non-crash failures (best-effort — the restart
                ' actions above are what matter; this just widens what counts).
                Dim flag As New SERVICE_FAILURE_ACTIONS_FLAG
                flag.fFailureActionsOnNonCrashFailures = RecoveryRestartOnNonCrash
                Dim flagPtr As IntPtr = Marshal.AllocHGlobal(Marshal.SizeOf(GetType(SERVICE_FAILURE_ACTIONS_FLAG)))
                Try
                    Marshal.StructureToPtr(flag, flagPtr, False)
                    ChangeServiceConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, flagPtr)
                Finally
                    Marshal.FreeHGlobal(flagPtr)
                End Try
            Finally
                If actionsPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(actionsPtr)
                If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
                CloseServiceHandle(scm)
            End Try
        End Sub

        ''' <summary>Start the named service if it is not already running.</summary>
        Public Shared Sub StartService(ByVal serviceName As String)
            Using sc As New ServiceController(serviceName)
                If sc.Status <> ServiceControllerStatus.Running AndAlso sc.Status <> ServiceControllerStatus.StartPending Then
                    sc.Start()
                End If
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))
            End Using
        End Sub

        ''' <summary>Stop the named service (best effort).</summary>
        Public Shared Sub StopService(ByVal serviceName As String)
            Using sc As New ServiceController(serviceName)
                If sc.Status <> ServiceControllerStatus.Stopped AndAlso sc.CanStop Then
                    sc.Stop()
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30))
                End If
            End Using
        End Sub

    End Class

End Namespace
