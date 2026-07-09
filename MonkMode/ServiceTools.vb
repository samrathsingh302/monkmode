'    Copyright (C) 2026 Samrath Singh
'
'    This file is part of MonkMode, a fork of Cold Turkey.
'    Source: https://github.com/samrathsingh302/monkmode
'
'    This program is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    This program is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with this program.  If not, see <https://www.gnu.org/licenses/>.

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

        ' --- Standard rights for the B6 security ops ---
        ' READ_CONTROL | WRITE_DAC is the MINIMAL handle B6 needs to read and
        ' rewrite the service's DACL (QueryServiceObjectSecurity /
        ' SetServiceObjectSecurity). DELETE is needed only by the escape hatch's
        ' DeleteService. Crucially we never need (and never request) anything
        ' that would let a denied-DELETE service resist its OWN restore: WRITE_DAC
        ' is NOT in the deny ACE, so this handle always succeeds.
        Private Const READ_CONTROL As UInteger = &H20000UI
        Private Const WRITE_DAC As UInteger = &H40000UI
        Private Const [DELETE] As UInteger = &H10000UI
        ' SECURITY_INFORMATION: we read/write the DACL only - never the owner,
        ' group or SACL, so the rest of the descriptor is left exactly as-is.
        Private Const DACL_SECURITY_INFORMATION As UInteger = &H4UI

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

        ' --- B6 service-object security (DACL read/write) ---
        ' QueryServiceObjectSecurity copies the requested SECURITY_INFORMATION of
        ' the service's security descriptor into a self-relative buffer;
        ' SetServiceObjectSecurity writes it back. We read the DACL, convert it to
        ' SDDL, surgically add/remove the deny-DELETE ACE (ServiceSecurity, the
        ' pure unit-tested layer), convert back, and write it - mirroring the
        ' best-effort SetRecoveryOptions pattern.
        <DllImport("advapi32.dll", SetLastError:=True)> _
        Private Shared Function QueryServiceObjectSecurity(ByVal hService As IntPtr, ByVal dwSecurityInformation As UInteger, _
            ByVal lpSecurityDescriptor As IntPtr, ByVal cbBufSize As UInteger, ByRef pcbBytesNeeded As UInteger) As Boolean
        End Function

        <DllImport("advapi32.dll", SetLastError:=True)> _
        Private Shared Function SetServiceObjectSecurity(ByVal hService As IntPtr, ByVal dwSecurityInformation As UInteger, _
            ByVal lpSecurityDescriptor As IntPtr) As Boolean
        End Function

        ' SDDL <-> binary security-descriptor conversion (sddlapi, exported from
        ' advapi32). SDDL revision 1. The "...ToString..." call allocates the
        ' string with LocalAlloc; we copy it out and LocalFree it.
        <DllImport("advapi32.dll", EntryPoint:="ConvertSecurityDescriptorToStringSecurityDescriptorW", CharSet:=CharSet.Unicode, SetLastError:=True)> _
        Private Shared Function ConvertSecurityDescriptorToStringSecurityDescriptor(ByVal SecurityDescriptor As IntPtr, _
            ByVal RequestedStringSDRevision As UInteger, ByVal SecurityInformation As UInteger, _
            ByRef StringSecurityDescriptor As IntPtr, ByRef StringSecurityDescriptorLen As UInteger) As Boolean
        End Function

        <DllImport("advapi32.dll", EntryPoint:="ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet:=CharSet.Unicode, SetLastError:=True)> _
        Private Shared Function ConvertStringSecurityDescriptorToSecurityDescriptor(ByVal StringSecurityDescriptor As String, _
            ByVal StringSDRevision As UInteger, ByRef SecurityDescriptor As IntPtr, ByRef SecurityDescriptorSize As UInteger) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)> _
        Private Shared Function LocalFree(ByVal hMem As IntPtr) As IntPtr
        End Function

        ' SDDL revision constant (the only one defined).
        Private Const SDDL_REVISION_1 As UInteger = 1UI

        <DllImport("advapi32.dll", SetLastError:=True)> _
        Private Shared Function DeleteService(ByVal hService As IntPtr) As Boolean
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

        ' ===== B6: deny-DELETE on the service object (sc-delete resistance) =====
        '
        ' BRICK-SAFE by construction (see ServiceSecurity.vb's header): we add a
        ' single deny ACE for the DELETE right (SD) only, targeting Built-in
        ' Administrators (BA). We open the service with READ_CONTROL | WRITE_DAC
        ' to rewrite the DACL; we NEVER deny WRITE_DAC, so this open always
        ' succeeds and BOTH the LocalSystem service (SY) and the elevated admin
        ' CLI (BA, still holding WRITE_DAC) can always restore the DACL. There is
        ' no path here that can make the service un-removable: the per-tick
        ' re-assert undoes a casual re-ACL, stopMe() removes the ACE at genuine
        ' expiry, and the `unblock --force` escape hatch removes it unconditionally.

        ' Read the service's DACL as an SDDL string, or Nothing on any failure.
        ' Two-phase QueryServiceObjectSecurity (size probe, then fetch).
        Private Shared Function ReadServiceDaclSddl(ByVal svc As IntPtr) As String
            Dim needed As UInteger = 0UI
            ' First call sizes the buffer (it fails with ERROR_INSUFFICIENT_BUFFER
            ' and sets `needed`).
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
                ' ConvertString...ToSecurityDescriptor allocates the SD with
                ' LocalAlloc; free it with LocalFree.
                If sd <> IntPtr.Zero Then LocalFree(sd)
            End Try
        End Function

        ''' <summary>
        ''' B6: add the deny-DELETE-for-Administrators ACE to the MONKMODE service
        ''' object so `sc delete MONKMODE` (OpenService DELETE + DeleteService) is
        ''' refused while a block is active. Best-effort and idempotent: if the
        ''' DACL already carries the ACE this is a true no-op (no churn). NEVER
        ''' bricks the service - it denies DELETE only, and opens with WRITE_DAC
        ''' which is never denied, so the owner can always restore. Friend: it
        ''' mutates the live service SD, so only the service-side caller (and the
        ''' unit tests, via InternalsVisibleTo) should reach it. Throws on the SCM
        ''' open failures so the caller's Try/Catch can swallow them.
        ''' </summary>
        Friend Shared Sub AssertDenyDelete(ByVal serviceName As String)
            Dim scm As IntPtr = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then
                Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager (administrator rights required).")
            End If
            Dim svc As IntPtr = IntPtr.Zero
            Try
                svc = OpenService(scm, serviceName, READ_CONTROL Or WRITE_DAC)
                If svc = IntPtr.Zero Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the MonkMode service to set its DACL.")
                End If
                Dim sddl As String = ReadServiceDaclSddl(svc)
                If sddl Is Nothing Then Return
                ' Read-only probe first: an intact DACL (already denying DELETE) is
                ' a no-op, so we never rewrite the SD needlessly (mirrors the B3
                ' SafeBoot probe and RepairHostsBlock returning Nothing).
                If MonkMode.ServiceSecurity.SddlHasDenyDelete(sddl) Then Return
                Dim updated As String = MonkMode.ServiceSecurity.AddDenyDeleteAce(sddl)
                If updated <> sddl Then
                    WriteServiceDaclSddl(svc, updated)
                End If
            Finally
                If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
                CloseServiceHandle(scm)
            End Try
        End Sub

        ''' <summary>
        ''' B6: restore the default service SD by removing the deny-DELETE ACE, so
        ''' an expired block (or the escape hatch) leaves a fully removable
        ''' service. The exact inverse of AssertDenyDelete. Best-effort; a no-op if
        ''' the ACE is absent. THIS is the non-negotiable re-grant: stopMe() calls
        ''' it at genuine expiry (after killing the guardian, before stopping) so
        ''' no live guardian can re-deny in the gap. Throws on SCM open failures
        ''' for the caller to swallow.
        ''' </summary>
        Friend Shared Sub RestoreDefaultServiceSd(ByVal serviceName As String)
            Dim scm As IntPtr = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then
                Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager (administrator rights required).")
            End If
            Dim svc As IntPtr = IntPtr.Zero
            Try
                svc = OpenService(scm, serviceName, READ_CONTROL Or WRITE_DAC)
                If svc = IntPtr.Zero Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the MonkMode service to restore its DACL.")
                End If
                Dim sddl As String = ReadServiceDaclSddl(svc)
                If sddl Is Nothing Then Return
                Dim updated As String = MonkMode.ServiceSecurity.RemoveDenyDeleteAce(sddl)
                If updated <> sddl Then
                    WriteServiceDaclSddl(svc, updated)
                End If
            Finally
                If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
                CloseServiceHandle(scm)
            End Try
        End Sub

        ''' <summary>
        ''' Disable the SCM auto-restart recovery policy (B1 layer 1) on the named
        ''' service - the escape hatch's first step, so nothing resurrects the
        ''' service mid-teardown. Equivalent to `sc failure NAME reset= 0
        ''' actions= ""`. Best-effort; throws on the SCM open failures only.
        ''' </summary>
        Friend Shared Sub DisableRecovery(ByVal serviceName As String)
            Dim scm As IntPtr = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then
                Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager (administrator rights required).")
            End If
            Dim svc As IntPtr = IntPtr.Zero
            Dim faPtr As IntPtr = IntPtr.Zero
            Try
                svc = OpenService(scm, serviceName, SERVICE_ALL_ACCESS)
                If svc = IntPtr.Zero Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the MonkMode service to clear its recovery policy.")
                End If
                ' Zero reset period, zero actions (empty list) = no recovery.
                Dim fa As New SERVICE_FAILURE_ACTIONS
                fa.dwResetPeriod = 0UI
                fa.lpRebootMsg = Nothing
                fa.lpCommand = Nothing
                fa.cActions = 0UI
                fa.lpsaActions = IntPtr.Zero
                faPtr = Marshal.AllocHGlobal(Marshal.SizeOf(GetType(SERVICE_FAILURE_ACTIONS)))
                Marshal.StructureToPtr(fa, faPtr, False)
                ChangeServiceConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS, faPtr)
            Finally
                If faPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(faPtr)
                If svc <> IntPtr.Zero Then CloseServiceHandle(svc)
                CloseServiceHandle(scm)
            End Try
        End Sub

        ''' <summary>
        ''' Delete the named service via the SCM (escape-hatch final step). Opens
        ''' with DELETE and calls DeleteService. The caller MUST already have
        ''' restored the default SD (RestoreDefaultServiceSd) so the deny-DELETE
        ''' ACE no longer blocks this open. Best-effort; throws on SCM failures so
        ''' the escape hatch can report and continue. (Marks the service for
        ''' deletion; it is removed once the last handle closes / it stops.)
        ''' </summary>
        Friend Shared Sub DeleteServiceByName(ByVal serviceName As String)
            Dim scm As IntPtr = OpenSCManager(Nothing, Nothing, SC_MANAGER_ALL_ACCESS)
            If scm = IntPtr.Zero Then
                Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager (administrator rights required).")
            End If
            Dim svc As IntPtr = IntPtr.Zero
            Try
                svc = OpenService(scm, serviceName, [DELETE])
                If svc = IntPtr.Zero Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "Could not open the MonkMode service to delete it.")
                End If
                If Not DeleteService(svc) Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error(), "DeleteService failed for the MonkMode service.")
                End If
            Finally
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
