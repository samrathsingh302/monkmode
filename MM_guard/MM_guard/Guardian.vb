'    MonkMode - Guardian decision gates (B1 watchdog, layer 2)
'
'    The pure half of the SYSTEM-session guardian: every decision the live loop
'    in Program.vb takes goes through one of these functions, so the
'    tamper-critical logic is unit-testable without ever touching the real
'    SCM, process list or file system (the live wiring is verified by the
'    manual elevated smoke test, exactly like the service's B2 repair wiring).
'
'    Fail-safe direction, same spirit as the service's gates:
'      - BlockHasExpired is a copy of Service1.BlockHasExpired: an unparseable
'        Until is NOT expired (fail CLOSED), so a corrupted config keeps the
'        guardian guarding rather than letting it stand down early;
'      - ShouldRestartService / ShouldRelaunchNotifier only act while the block
'        is active, so the guardian can never resurrect anything after the
'        block has genuinely ended;
'      - ShouldRelaunchNotifier mirrors Service1.ShouldRestartPeer: no
'        duplicate-spawn while an instance is running, nothing started when the
'        exe is missing.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization

Friend Module Guardian

    ' Decides whether a persisted block end time has expired. Byte-for-byte the
    ' same semantics as Service1.BlockHasExpired (the service and the guardian
    ' must agree on "expired", or one side could stand down while the other
    ' still enforces): untilText is the decrypted [Time] Until value (an en-CA
    ' datetime string); expired means no more than graceSeconds remain at asOf;
    ' an unparseable value is NOT expired (fail CLOSED).
    Friend Function BlockHasExpired(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long) As Boolean
        Dim untilDate As DateTime
        If Not DateTime.TryParse(untilText, New CultureInfo("en-CA"), DateTimeStyles.None, untilDate) Then
            Return False
        End If
        Return DateDiff(DateInterval.Second, asOf, untilDate) <= graceSeconds
    End Function

    ' B7: MAC-aware expiry, byte-for-byte the same semantics as
    ' Service1.EffectiveBlockHasExpired - the guardian and the service MUST agree
    ' on "expired" or one side could stand down while the other still enforces.
    ' The block is expired ONLY when the time has genuinely passed AND the config
    ' MAC is valid; a tampered/invalid MAC (or an unparseable Until) reads as NOT
    ' expired, so the guardian keeps guarding (fail CLOSED) until a legitimate
    ' stamp exists. The live MAC evaluation that yields macValid lives in
    ' Program.vb (the DPAPI seam); this stays pure and unit-testable.
    Friend Function EffectiveBlockHasExpired(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean) As Boolean
        Return macValid AndAlso BlockHasExpired(untilText, asOf, graceSeconds)
    End Function

    ' Decides whether this tick should ask the SCM to start the MONKMODE
    ' service. Only while the block is active (blockActive is the caller's
    ' Not BlockHasExpired(...), so fail-closed carries through) and only when
    ' the service is not already running/starting - the guardian must never
    ' fight the SCM over an already-starting service, and never resurrect the
    ' service after the block has truly ended (it tore itself down for a
    ' reason: stopMe() strips hosts and stops the service at expiry).
    Friend Function ShouldRestartService(ByVal blockActive As Boolean, ByVal serviceRunning As Boolean) As Boolean
        If Not blockActive Then Return False
        Return Not serviceRunning
    End Function

    ' Decides whether this tick should relaunch the user-session notifier.
    ' Mirrors Service1.ShouldRestartPeer exactly (the same fail-safe shape, on
    ' the guardian's side of the pair): only while the block is active, only
    ' when the exe exists, only when no instance is already running so a slow
    ' start can never spawn a storm of duplicates.
    Friend Function ShouldRelaunchNotifier(ByVal notifierInstanceCount As Integer, ByVal blockActive As Boolean, ByVal notifierExeExists As Boolean) As Boolean
        If Not blockActive Then Return False
        If Not notifierExeExists Then Return False
        Return notifierInstanceCount <= 0
    End Function

End Module
