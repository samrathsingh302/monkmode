'    MonkMode - CLI entry point
'
'    Usage:
'      monkmode block  --sites a.com,b.com [--apps chrome.exe,foo.exe]
'                      (--for 2h30m | --until "2026-06-11 18:00") [--file list.txt]
'      monkmode status
'      monkmode add    --sites c.com[,d.com]
'      monkmode unblock --force            (escape hatch — tears down an active block)
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
            ' C1b (R8, CLI side of the restore-on-corrupt path): if the primary
            ' config is corrupt/blanked/short and a MAC-valid backup exists, restore
            ' it before dispatching - so status/add see the real (self-healed) block
            ' instead of a fail-closed blank. Never writes a default and never
            ' overwrites a usable primary (a tampered-but-parseable config is left to
            ' freeze per B7); best-effort, never throws.
            Blocker.RestorePrimaryFromBackupIfCorrupt()
            Select Case verb
                Case "block" : Return DoBlock(args)
                Case "status" : Return DoStatus()
                Case "add" : Return DoAdd(args)
                Case "unblock" : Return DoUnblock(args)
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

        If domains.Count > 0 Then
            Blocker.WriteHostsBlock(domains)
        Else
            ' Apps-only block: remove any stale snapshot from an earlier block,
            ' otherwise the service's B2 repair would resurrect the OLD sites
            ' into hosts for the lifetime of this block.
            Try
                File.Delete(Blocker.SnapshotPath())
            Catch
            End Try
        End If
        Blocker.WriteConfig(domains, apps, untilDate)
        ' B5a: snapshot the user's current browser DoH policy BEFORE the service
        ' starts and forces it off, so teardown restores the pre-block state (no
        ' data loss). Must precede InstallAndStart - the service sets the policy in
        ' its OnStart. Never aborts arming the block; if it fails, teardown will
        ' leave the DoH-off policy in place rather than risk deleting a user value.
        If Not Blocker.WriteDohSnapshot() Then
            Console.Error.WriteLine("Warning: could not snapshot current browser DoH settings; MonkMode will leave 'Secure DNS off' in place at expiry rather than restore/remove it.")
        End If
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

    ' B6 escape hatch — the guaranteed-removal / clean-exit path. Once B1/B2/B3/
    ' B4/B7 are all fail-closed, a tampered or corrupted block never auto-lifts,
    ' and the service now resists `sc delete`. This verb is the deliberate,
    ' documented way out (brick-insurance — see vault\dev\monk-mode\specs\ARCHITECTURE.md B6 / the honest
    ' ceiling). It is UNCONDITIONAL by design but gated behind an explicit
    ' --force, so it can never be a casual one-word bypass: you must consciously
    ' ask to tear an active block down. Every step is best-effort and ordered so
    ' nothing resurrects the service mid-teardown; failures are reported, not
    ' fatal. Mirrors the live-verified cleanup.ps1 emergency teardown.
    Private Function DoUnblock(ByVal args As String()) As Integer
        Dim forced As Boolean = HasFlag(args, "--force")
        If Not forced Then
            Console.Error.WriteLine("'unblock' tears down an active block and removes the MonkMode service.")
            Console.Error.WriteLine("This is the deliberate escape hatch — it is NOT undone automatically.")
            Console.Error.WriteLine("If you really mean it, run:  monkmode unblock --force")
            Return 1
        End If

        Console.WriteLine("Forcing MonkMode down (escape hatch). This removes the active block.")

        ' 1. Stop the SCM from auto-restarting the service the moment we kill it
        '    (B1 layer 1), so the kills in step 2 actually stick.
        Step_("Disabling service recovery policy", Sub() ServiceTools.ServiceInstaller.DisableRecovery(Blocker.ServiceName))

        ' 2. Kill the watchdog pair (guardian first, then service) so neither
        '    re-asserts the deny-DELETE ACE nor re-enforces hosts, plus the
        '    notifier. Retries until both stay down (recovery is already off).
        Step_("Stopping the watchdog pair and notifier", Sub() Blocker.KillWatchdogProcesses())

        ' 3+4. With nothing alive to re-deny, remove the deny-DELETE ACE so the
        '    service object can be opened for DELETE (the CLI runs as BA), then
        '    delete the service registration itself (the `sc delete` we
        '    normally refuse during a block). Audit #9: while the deny ACE is
        '    still on the SD the SCM is GUARANTEED to refuse the delete, so a
        '    hard-failed SD restore is retried once and a still-failed restore
        '    SKIPS the delete with an actionable message, instead of burying
        '    the real cause under a misleading AccessDenied "skipped" from
        '    step 4. Steps 5+ run either way (best-effort teardown continues).
        RunSdRestoreThenDelete(
            Function(attempt As Integer) Step_(
                If(attempt = 1, "Removing the service deny-DELETE protection", "Retrying the deny-DELETE removal"),
                Sub() ServiceTools.ServiceInstaller.RestoreDefaultServiceSd(Blocker.ServiceName)),
            Function() Step_("Deleting the MonkMode service", Sub() ServiceTools.ServiceInstaller.DeleteServiceByName(Blocker.ServiceName)),
            Sub(msg) Console.WriteLine(msg))

        ' 5. Unlock hosts and strip ONLY the MonkMode marker block (user content
        '    preserved byte-for-byte — the same data-loss-safe strip the service
        '    uses at expiry).
        Step_("Restoring the hosts file", Sub() Blocker.RestoreHostsFromStrip())

        ' 6-8. Remove the B2 snapshot, the B3 SafeBoot leaf keys, and the HKCU
        '    autorun, so a future install can't self-heal the old block back.
        Step_("Removing the hosts snapshot", Sub() Blocker.DeleteSnapshot())
        ' C1b: remove the config shadow backup so a future install can't restore the
        ' old config from it (mirrors the hosts-snapshot removal + stopMe's delete).
        Step_("Removing the config backup", Sub() Blocker.DeleteBackup())
        Step_("Removing the Safe Mode registration", Sub() Blocker.RemoveSafeBootKeys())
        ' B5a: restore the user's prior browser DoH policy (or remove our lingering
        ' "off") from the snapshot, then consume it - no data loss, so a reinstall
        ' can't re-restore a stale prior.
        Step_("Restoring browser DoH policy", Sub() Blocker.RemoveDohPolicy())
        Step_("Clearing the notifier autorun", Sub() Blocker.ClearNotifierAutorun())

        Console.WriteLine("Done. MonkMode has been removed. If your browser still shows a block, flush DNS / reopen it.")
        Return 0
    End Function

    ' ---------- helpers ----------

    ' Run one best-effort teardown step: print what it does, swallow + report any
    ' failure so the escape hatch always continues to the next step. Returns
    ' whether the step succeeded so a dependent step can be gated on it (audit
    ' #9); callers stay free to ignore the result.
    Private Function Step_(ByVal label As String, ByVal action As Action) As Boolean
        Console.Write("  " & label & " ... ")
        Try
            action()
            Console.WriteLine("ok")
            Return True
        Catch ex As Exception
            Console.WriteLine("skipped (" & ex.Message & ")")
            Return False
        End Try
    End Function

    ' Audit #9 teardown policy: the service delete (step 4) is refused by the
    ' SCM for as long as the deny-DELETE ACE is still on the service SD, so a
    ' hard-failed SD restore (step 3) makes the delete attempt pure noise - a
    ' misleading AccessDenied "skipped". Policy: retry the restore once (covers
    ' a transient SCM hiccup), attempt the delete ONLY after a restore attempt
    ' succeeded, otherwise report an actionable skip. Friend + delegate params
    ' so the unit tests drive the policy without touching the real SCM (hard
    ' fence); production wires the delegates through Step_, so they never
    ' throw. Returns whether the delete ran and succeeded.
    Friend Function RunSdRestoreThenDelete(ByVal tryRestoreSd As Func(Of Integer, Boolean),
                                           ByVal tryDeleteService As Func(Of Boolean),
                                           ByVal reportSkip As Action(Of String)) As Boolean
        Dim sdRestored As Boolean = tryRestoreSd(1)
        If Not sdRestored Then sdRestored = tryRestoreSd(2)
        If Not sdRestored Then
            reportSkip("  Deleting the MonkMode service ... skipped (the deny-DELETE removal failed twice, so the SCM would refuse the delete; re-run 'monkmode unblock --force' to retry)")
            Return False
        End If
        Return tryDeleteService()
    End Function

    ' True if a bare flag (e.g. --force) is present anywhere in args.
    Private Function HasFlag(ByVal args As String(), ByVal name As String) As Boolean
        For Each a As String In args
            If String.Equals(a, name, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

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
        Console.WriteLine("  monkmode unblock --force   (escape hatch: tears down an active block + removes the service)")
        Console.WriteLine("  monkmode help")
        Console.WriteLine("")
        Console.WriteLine("Notes:")
        Console.WriteLine("  - Run as Administrator (needed to edit the hosts file and install the service).")
        Console.WriteLine("  - Once a block starts it cannot be shortened or removed until it expires.")
        Console.WriteLine("  - --for accepts forms like 45 (minutes), 90m, 2h, 1d12h.")
    End Sub

End Module
