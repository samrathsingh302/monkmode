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
