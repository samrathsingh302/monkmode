'    MonkMode - AtomicHosts (C1: crash-safe hosts writes)
'
'    A single shared helper for every writer of the LIVE hosts file. The four
'    writers - the CLI's WriteHostsBlock (arm) and RestoreHostsFromStrip
'    (unblock), and the service's stopMe (expiry strip) and timer self-heal
'    (B2 repair) - used to truncate-then-rewrite hosts in place
'    (File.WriteAllText / a FileMode.Create StreamWriter). A crash or power loss
'    mid-write left hosts blanked or half-written, destroying the user's OWN
'    custom entries (a no-data-loss-fence violation) - and the B2 self-heal write
'    fires every 10s heartbeat, so that window was being opened constantly.
'
'    WriteAtomic serializes to a unique temp file in the SAME directory as hosts
'    (the ...\etc dir, so File.Move is a same-volume rename, atomic on NTFS) then
'    renames it over hosts with File.Move(overwrite:=True) (MoveFileEx
'    REPLACE_EXISTING). A reader (the DNS client, or another writer) therefore
'    always sees either the whole old hosts or the whole new one - never an empty
'    or torn file. This mirrors IniFile.Save's atomic write (audit P2 #3) exactly,
'    including its bounded retry: File.Move throws a sharing/access violation if a
'    concurrent reader holds hosts at the rename instant, so retry briefly; on
'    persistent failure the existing hosts is left intact and the temp is cleaned
'    up before rethrowing (a service heartbeat tick swallows it - fail-closed, a
'    dropped repair, never a torn or blanked hosts).
'
'    The caller still owns the read-only attribute (clear before, re-assert
'    after) - this helper only swaps the file contents, exactly like IniFile.Save.
'    NOTE: the target must be writable (read-only cleared) at the rename instant,
'    or File.Move throws UnauthorizedAccessException; every caller already clears
'    it first.
'
'    This file is byte-for-byte identical to the MonkMode_srv copy (only the
'    RootNamespace differs), like the Simple3Des / ConfigIntegrity / IniFile
'    copies - the unit tests pin that parity.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict On

Imports System.IO

Module AtomicHosts

    ' Test seam (audit N2/N3 fail-closed branch coverage). When set, the atomic
    ' rename below calls this instead of File.Move, so a unit test can force the
    ' persistent-failure path DETERMINISTICALLY - proving (N2) that an unrenameable
    ' target still fails closed (retries exhausted -> rethrow, temp cleaned up, the
    ' existing target left intact, never blanked or torn) and (N3) that the service's
    ' ReassertHostsFailClosed backstop still re-locks hosts read-only even when this
    ' write throws AFTER the read-only attribute was already cleared. <ThreadStatic>
    ' so the hook is confined to the single test thread that sets it: a parallel test
    ' class calling WriteAtomic on another thread is unaffected (no shared-state race),
    ' and it needs no initializer (a reference-type ThreadStatic defaults to Nothing on
    ' every thread). PRODUCTION never assigns it - the field stays Nothing, so
    ' WriteAtomic takes the real File.Move path, behaviourally unchanged. Friend, so
    ' only the in-repo test assembly (InternalsVisibleTo) can even see it.
    <ThreadStatic>
    Friend RenameHookForTests As Action(Of String, String)

    ' Crash-safe replacement of a file's entire contents: write a unique sibling
    ' temp, then atomically rename it over the target. Used for every hosts write.
    ' (targetPath, not "path" - VB is case-insensitive, so a "path" parameter would
    ' shadow System.IO.Path, exactly the name clash IniFile.Save avoids.)
    Public Sub WriteAtomic(ByVal targetPath As String, ByVal contents As String)
        Dim dir As String = Path.GetDirectoryName(targetPath)
        If String.IsNullOrEmpty(dir) Then dir = "."
        Dim tmp As String = Path.Combine(dir, Path.GetFileName(targetPath) & "." & System.Guid.NewGuid().ToString("N") & ".tmp")
        Try
            File.WriteAllText(tmp, contents)
            ' Atomic replace with a short bounded retry (mirrors IniFile.Save).
            ' File.Move(overwrite:=True) maps to MoveFileEx REPLACE_EXISTING, which
            ' throws a sharing/access violation if a concurrent reader holds the
            ' target at the rename instant. Retry briefly so a colliding read does
            ' not drop the write; if it still cannot replace, the outer Catch
            ' cleans up the temp and rethrows. Works whether or not hosts exists.
            Dim attempt As Integer = 0
            Do
                Try
                    ' RenameHookForTests is Nothing in production - this is the plain
                    ' File.Move rename; the hook only diverts it under a unit test.
                    If RenameHookForTests IsNot Nothing Then
                        RenameHookForTests(tmp, targetPath)
                    Else
                        File.Move(tmp, targetPath, True)
                    End If
                    Exit Do
                Catch ex As Exception When (TypeOf ex Is System.IO.IOException OrElse TypeOf ex Is System.UnauthorizedAccessException) AndAlso attempt < 8
                    attempt += 1
                    System.Threading.Thread.Sleep(25)
                End Try
            Loop
        Catch
            ' Leave the existing target intact; clean up the temp so we don't litter.
            Try
                If File.Exists(tmp) Then File.Delete(tmp)
            Catch
            End Try
            Throw
        End Try
    End Sub

End Module
