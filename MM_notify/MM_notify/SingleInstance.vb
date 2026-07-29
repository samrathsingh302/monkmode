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

'    MonkMode - notifier: single-instance claim (D4c)
'
'    The testable core behind the notifier's single-instance guard. Mechanism
'    only: it creates a named mutex and reports whether THIS process created it
'    (so it is the sole live instance) or found one already standing (so another
'    notifier owns the claim). The POLICY - what to do about that answer, and
'    what to do when the attempt throws - lives in the thin live wrapper at the
'    top of Program.Main, in the same testable-core + live-wrapper shape as
'    Service1.ReassertHostsFailClosed and Notifications' pure builders.
'
'    Why a mutex at all: MM_notify has NO single-instance guard on the reboot
'    path. Application.myapp still carries <SingleInstance>true</SingleInstance>,
'    but that flag is DEAD here - it is only honoured by the VB application
'    framework's generated Sub Main, and this project sets
'    MyType=WindowsFormsWithCustomSubMain + StartupObject=mm_notify.Program to
'    drive Application.Run itself (see the header of Program.vb). Nothing reads
'    the flag.
'
'    Release semantics are deliberately the kernel's, not ours: the claim is a
'    handle held open for the process lifetime and never released by code, so a
'    crashed, killed or guardian-restarted notifier frees it automatically when
'    its last handle closes. We never WaitOne on the mutex, so no caller can ever
'    meet an AbandonedMutexException - "already exists" is read purely from the
'    creation flag.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Threading

Module SingleInstance

    '    The machine-wide name of the notifier's single-instance claim.
    '
    '    "Global\", not "Local\" (nor an unprefixed name, which is per-session),
    '    because the notifier is ONE machine-wide helper with three independent
    '    launchers sitting in different places: the CLI at arm time, the
    '    SYSTEM-session guardian's relaunch, and Explorer's HKCU Run entry. Today
    '    the guardian hands its spawn into the interactive session
    '    (LaunchInActiveUserSession -> CreateProcessAsUser, MM_guard\Program.vb),
    '    so the reboot race happens to land both copies in the SAME session and a
    '    session-local name would also have caught it - but only by accident of
    '    that one launcher's mechanism. A global claim is one per MACHINE, which is
    '    what "one notifier" actually means here, and it cannot be quietly
    '    narrowed by a future launcher (or a second interactive session) putting a
    '    spawn somewhere else.
    '
    '    Creating a named MUTEX in the global namespace from an unelevated
    '    interactive process is allowed: the SeCreateGlobalPrivilege check is
    '    limited to file-MAPPING objects, not to mutexes/events/semaphores.
    Public Const NotifierMutexName As String = "Global\MonkMode_notify_single_instance"

    '    The notifier's own process name, for the stand-down count check below. Each
    '    assembly keeps its own copy of this constant (Blocker.vb and MM_guard's
    '    Program.vb carry the same literal) - the projects do not reference each other.
    Public Const NotifierProcessName As String = "mm_notify"

    '    Try to become the one live notifier.
    '
    '    Returns True and hands back the owning Mutex when THIS process created the
    '    named object (no other instance is running). Returns False - and claims
    '    nothing - when the object already existed, i.e. another notifier is live.
    '    The handle we opened onto that existing object is closed immediately so a
    '    losing instance leaves no trace of itself behind.
    '
    '    The returned Mutex must be rooted (a module-level field) for as long as
    '    this process should hold the claim: dropping the last reference lets the
    '    finaliser close the handle and silently hand the claim to the next spawn.
    '
    '    Throws only what the Mutex constructor throws (e.g. a name collision with a
    '    different object type, or a namespace the token cannot open); the caller
    '    owns the fail-soft decision.
    Friend Function TryClaim(ByVal name As String, ByRef handle As Mutex) As Boolean
        handle = Nothing

        Dim createdNew As Boolean = False
        Dim claim As New Mutex(True, name, createdNew)

        If Not createdNew Then
            ' Someone else is the live notifier. We never took ownership (initiallyOwned
            ' is ignored when the object already exists), so closing our handle here
            ' releases nothing of theirs - it just drops our reference.
            claim.Dispose()
            Return False
        End If

        handle = claim
        Return True
    End Function

    '    The stand-down decision for a process that LOST the claim - pure, so the
    '    truth table is pinned by tests without touching real process enumeration
    '    (the live wrapper in Program.Main feeds it the real mm_notify count).
    '
    '    Losing the mutex alone is NOT enough to exit. The name lives in the global
    '    kernel namespace with a default DACL, and the notifier runs as the
    '    interactive user - so any same-user script could create the mutex first,
    '    hold it forever, and stand down every notifier the guardian ever relaunches:
    '    a three-line permanent kill of user-session app-kill and clock-change
    '    compensation, strictly worse than the pre-guard posture (where killing the
    '    notifier was beaten by the guardian's 10s relaunch). So the loser also
    '    requires a SECOND real mm_notify process to exist (count includes self, so
    '    > 1 means another copy is live). In the genuine reboot race both copies
    '    exist, the loser sees >= 2 and exits; a bare mutex squatter leaves the count
    '    at 1 and the notifier launches anyway - unclaimed, exactly today's posture.
    '
    '    Accepted residuals, both bounded by what an attacker can already do today: a
    '    squatter who ALSO runs a decoy renamed mm_notify.exe collapses to the
    '    pre-existing decoy attack - every real spawn loses the claim, counts the
    '    decoy as a second notifier and stands down, permanent ZERO notifiers - but a
    '    decoy named mm_notify already achieves exactly that against the guardian's
    '    count<=0 relaunch today, with no mutex involved, so no new capability is
    '    added. And a winner dying in the instant between the loser's failed claim
    '    and its count check leaves the survivor running unclaimed, where a later
    '    spawn can double it - the benign pre-guard status quo.
    Friend Function ShouldStandDown(ByVal claimLost As Boolean, ByVal notifierProcessCount As Integer) As Boolean
        Return claimLost AndAlso notifierProcessCount > 1
    End Function

End Module
