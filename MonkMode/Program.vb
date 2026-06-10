'    MonkMode - CLI entry point
'
'    Usage:
'      monkmode block  --sites a.com,b.com [--apps chrome.exe,foo.exe]
'                      (--for 2h30m | --until "2026-06-11 18:00") [--file list.txt]
'      monkmode status
'      monkmode add    --sites c.com[,d.com]
'      monkmode help
'
'    A block, once started, cannot be shortened or started anew until the
'    current one expires (the service enforces this). 'add' only adds sites.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization
Imports System.IO

Module Program

    Function Main(ByVal args As String()) As Integer
        If args Is Nothing OrElse args.Length = 0 Then
            PrintUsage()
            Return 1
        End If

        Dim verb As String = args(0).ToLowerInvariant()
        Try
            Select Case verb
                Case "block" : Return DoBlock(args)
                Case "status" : Return DoStatus()
                Case "add" : Return DoAdd(args)
                Case "help", "-h", "--help", "/?" : PrintUsage() : Return 0
                Case Else
                    Console.Error.WriteLine("Unknown command: " & verb)
                    PrintUsage()
                    Return 1
            End Select
        Catch ex As UnauthorizedAccessException
            Console.Error.WriteLine("Access denied. Run MonkMode as Administrator.")
            Return 2
        Catch ex As Exception
            Console.Error.WriteLine("Error: " & ex.Message)
            Return 2
        End Try
    End Function

    ' ---------- verbs ----------

    Private Function DoBlock(ByVal args As String()) As Integer
        Dim domains As New List(Of String)
        domains.AddRange(SplitList(GetOption(args, "--sites")))

        Dim fileArg As String = GetOption(args, "--file")
        If fileArg <> "" AndAlso File.Exists(fileArg) Then
            For Each line As String In File.ReadAllLines(fileArg)
                Dim t As String = line.Trim()
                If t <> "" AndAlso Not t.StartsWith("#") Then domains.Add(t)
            Next
        End If

        Dim apps As New List(Of String)
        apps.AddRange(SplitList(GetOption(args, "--apps")))

        If domains.Count = 0 AndAlso apps.Count = 0 Then
            Console.Error.WriteLine("Nothing to block. Provide --sites and/or --apps.")
            Return 1
        End If

        Dim untilDate As DateTime
        Dim untilArg As String = GetOption(args, "--until")
        Dim forArg As String = GetOption(args, "--for")
        If untilArg <> "" Then
            If Not DateTime.TryParse(untilArg, CultureInfo.CurrentCulture, DateTimeStyles.None, untilDate) _
               AndAlso Not DateTime.TryParse(untilArg, CultureInfo.InvariantCulture, DateTimeStyles.None, untilDate) Then
                Console.Error.WriteLine("Could not understand --until '" & untilArg & "'. Try ""2026-06-11 18:00"".")
                Return 1
            End If
        ElseIf forArg <> "" Then
            Dim span As TimeSpan
            If Not TryParseDuration(forArg, span) Then
                Console.Error.WriteLine("Could not understand --for '" & forArg & "'. Try 2h, 90m, 1d12h.")
                Return 1
            End If
            untilDate = DateTime.Now.Add(span)
        Else
            Console.Error.WriteLine("Specify a duration with --for or --until.")
            Return 1
        End If

        If untilDate <= DateTime.Now.AddSeconds(60) Then
            Console.Error.WriteLine("The block must end at least a minute in the future.")
            Return 1
        End If

        Dim serviceExe As String = Path.Combine(Blocker.AppDir(), Blocker.ServiceExeName)
        If Not File.Exists(serviceExe) Then
            Console.Error.WriteLine("Cannot find " & Blocker.ServiceExeName & " next to monkmode.exe (" & Blocker.AppDir() & ").")
            Console.Error.WriteLine("Deploy the service and notifier into the same folder as the CLI.")
            Return 2
        End If

        If Blocker.BlockIsActive() Then
            Dim ends As DateTime = Blocker.ActiveBlockEnd()
            Console.WriteLine("A block is already active (" & Humanize(ends.Subtract(DateTime.Now)) & " left, until " & ends.ToString() & ").")
            Console.WriteLine("You can't start a new block or shorten this one. Use 'monkmode add --sites ...' to add sites.")
            Return 3
        End If

        If domains.Count > 0 Then Blocker.WriteHostsBlock(domains)
        Blocker.WriteConfig(domains, apps, untilDate)
        ServiceTools.ServiceInstaller.InstallAndStart(Blocker.ServiceName, Blocker.ServiceDisplay, serviceExe)
        Blocker.RegisterAndLaunchNotifier()

        Console.WriteLine("MonkMode is now active until " & untilDate.ToString() & " (" & Humanize(untilDate.Subtract(DateTime.Now)) & ").")
        If domains.Count > 0 Then Console.WriteLine("  Sites: " & String.Join(", ", domains))
        If apps.Count > 0 Then Console.WriteLine("  Apps:  " & String.Join(", ", apps))
        Console.WriteLine("Close and reopen your browser to see the block. It cannot be removed until the timer ends.")
        Return 0
    End Function

    Private Function DoStatus() As Integer
        If Not Blocker.ServiceIsInstalled() Then
            Console.WriteLine("MonkMode: no block has ever been installed on this machine.")
            Return 0
        End If
        Dim ends As DateTime = Blocker.ActiveBlockEnd()
        If Blocker.ServiceIsRunning() AndAlso ends > DateTime.Now Then
            Console.WriteLine("MonkMode: ACTIVE")
            Console.WriteLine("  Ends:  " & ends.ToString() & " (" & Humanize(ends.Subtract(DateTime.Now)) & " left)")
            Dim sites As String = Blocker.BlockedSites()
            Dim apps As String = Blocker.BlockedApps()
            If sites <> "" Then Console.WriteLine("  Sites: " & sites.Replace(";", " "))
            If apps <> "" Then Console.WriteLine("  Apps:  " & apps.Replace(";", " "))
        Else
            Console.WriteLine("MonkMode: no active block (service installed but idle).")
        End If
        Return 0
    End Function

    Private Function DoAdd(ByVal args As String()) As Integer
        Dim domains As New List(Of String)
        domains.AddRange(SplitList(GetOption(args, "--sites")))
        If domains.Count = 0 Then
            Console.Error.WriteLine("Provide sites to add with --sites a.com,b.com")
            Return 1
        End If
        If Not Blocker.BlockIsActive() Then
            Console.Error.WriteLine("No active block to add to. Start one with 'monkmode block'.")
            Return 1
        End If
        Blocker.AppendAddToHosts(domains)
        Console.WriteLine("Added to the active block: " & String.Join(", ", domains))
        Return 0
    End Function

    ' ---------- helpers ----------

    Private Function GetOption(ByVal args As String(), ByVal name As String) As String
        For i As Integer = 0 To args.Length - 1
            If String.Equals(args(i), name, StringComparison.OrdinalIgnoreCase) Then
                If i + 1 < args.Length Then Return args(i + 1)
                Return ""
            End If
            If args(i).StartsWith(name & "=", StringComparison.OrdinalIgnoreCase) Then
                Return args(i).Substring(name.Length + 1)
            End If
        Next
        Return ""
    End Function

    Private Function SplitList(ByVal value As String) As String()
        If value Is Nothing OrElse value.Trim() = "" Then Return New String() {}
        Return value.Split(New Char() {","c, ";"c}, StringSplitOptions.RemoveEmptyEntries)
    End Function

    ' Parse "1d2h30m" / "90m" / "2h" / "45" (minutes) into a TimeSpan.
    Private Function TryParseDuration(ByVal s As String, ByRef span As TimeSpan) As Boolean
        s = s.Trim().ToLowerInvariant()
        If s = "" Then Return False
        Dim days As Integer = 0, hours As Integer = 0, mins As Integer = 0
        Dim matched As Boolean = False
        Dim m As System.Text.RegularExpressions.Match =
            System.Text.RegularExpressions.Regex.Match(s, "^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?$")
        If m.Success AndAlso (m.Groups(1).Success OrElse m.Groups(2).Success OrElse m.Groups(3).Success) Then
            If m.Groups(1).Success Then days = Integer.Parse(m.Groups(1).Value)
            If m.Groups(2).Success Then hours = Integer.Parse(m.Groups(2).Value)
            If m.Groups(3).Success Then mins = Integer.Parse(m.Groups(3).Value)
            matched = True
        ElseIf Integer.TryParse(s, mins) Then
            matched = True   ' bare number = minutes
        End If
        If Not matched Then Return False
        span = New TimeSpan(days, hours, mins, 0)
        Return span.TotalSeconds > 0
    End Function

    Private Function Humanize(ByVal ts As TimeSpan) As String
        If ts.TotalSeconds <= 0 Then Return "0 minutes"
        Dim parts As New List(Of String)
        If ts.Days > 0 Then parts.Add(ts.Days & "d")
        If ts.Hours > 0 Then parts.Add(ts.Hours & "h")
        If ts.Minutes > 0 Then parts.Add(ts.Minutes & "m")
        If parts.Count = 0 Then parts.Add("<1m")
        Return String.Join(" ", parts)
    End Function

    Private Sub PrintUsage()
        Console.WriteLine("MonkMode - tamper-resistant self-control blocker")
        Console.WriteLine("")
        Console.WriteLine("Usage:")
        Console.WriteLine("  monkmode block --sites a.com,b.com [--apps chrome.exe,foo.exe] (--for 2h30m | --until ""2026-06-11 18:00"") [--file list.txt]")
        Console.WriteLine("  monkmode status")
        Console.WriteLine("  monkmode add --sites c.com")
        Console.WriteLine("  monkmode help")
        Console.WriteLine("")
        Console.WriteLine("Notes:")
        Console.WriteLine("  - Run as Administrator (needed to edit the hosts file and install the service).")
        Console.WriteLine("  - Once a block starts it cannot be shortened or removed until it expires.")
        Console.WriteLine("  - --for accepts forms like 45 (minutes), 90m, 2h, 1d12h.")
    End Sub

End Module
