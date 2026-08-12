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

'    MonkMode - IniFile (minimal Windows-INI reader/writer)
'
'    Clean-room implementation (10/07/2026); replaces a CPOL-licensed third-party
'    parser removed for GPL compatibility.
'
'    A small in-memory model of a Windows-style .ini file: an ordered set of
'    "[Section]" blocks, each an ordered set of "key=value" lines. The MonkMode
'    config (monkmode_settings.ini) is the only consumer; every reader (CLI,
'    service, guardian, notifier) parses it into a canonical string that a MAC is
'    computed over (ConfigIntegrity/B7), so ROUND-TRIP FIDELITY is load-bearing:
'    a value written by SetKeyValue and read back by GetKeyValue after Save/Load
'    must be byte-identical, or the canonical shifts and every reader fails closed
'    (the block over-holds). To guarantee that:
'      - a value is stored and re-read VERBATIM: never trimmed, never quoted, and
'        split from its key on the FIRST '=' only (so a value may itself contain
'        '=', e.g. a Base64 payload's padding or a [Schedule] Spec's "sites=...").
'      - section and key lookup is case-INSENSITIVE (the Windows .ini convention);
'        no MonkMode call site writes a name in one casing and reads it in another.
'      - GetKeyValue returns "" (never Nothing) for an absent section/key, so the
'        readers' If(x = "", ...) guards behave and callers never NRE.
'      - Load on a missing file is a no-op (no throw): call sites Load paths that
'        may not exist yet.
'      - Load skips blank lines, ';'/'#' comment lines, and any line without '='
'        rather than throwing, so a hand-edited or partially-written file degrades
'        gracefully instead of aborting a reader mid-tick.
'      - Save is atomic (write a unique temp sibling, then rename over the target)
'        so a crash or a concurrent reader can never see a half-written/blank ini.
'
'    This file is byte-for-byte identical across all four projects (CLI, service,
'    guardian, notifier), like the ConfigIntegrity / Simple3Des copies - the unit
'    tests pin that 4-copy parity. Only the RootNamespace differs (set per
'    project), so no Namespace is declared here.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.IO
Imports System.Text

' One "[Section]" block: an ordered, case-insensitive bag of key=value pairs.
' Insertion order is preserved so Save re-emits keys in the order they were set
' (stable, human-readable output; no bearing on the value-based MAC canonical).
' Friend: an internal helper - AddSection hands it back as Object, so no Public
' member of IniFile ever exposes this type across the assembly boundary.
Friend Class IniSection

    Public ReadOnly Name As String

    Private ReadOnly m_values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly m_keyOrder As New List(Of String)

    Public Sub New(ByVal name As String)
        Me.Name = name
    End Sub

    ' Set (creating or overwriting) a key. Last write wins; the display casing of a
    ' key is fixed at first sight (a later differently-cased write updates the value
    ' but keeps the original key text). The value is stored verbatim.
    Public Sub SetValue(ByVal key As String, ByVal value As String)
        If Not m_values.ContainsKey(key) Then
            m_keyOrder.Add(key)
        End If
        m_values(key) = value
    End Sub

    ' The value for key, or "" (never Nothing) if the key is absent.
    Public Function GetValue(ByVal key As String) As String
        Dim v As String = Nothing
        If m_values.TryGetValue(key, v) Then
            Return If(v, "")
        End If
        Return ""
    End Function

    ' Keys in insertion order (Save iterates this).
    Public ReadOnly Property Keys As IEnumerable(Of String)
        Get
            Return m_keyOrder
        End Get
    End Property

End Class

' A minimal Windows-INI document. See the file header for the fidelity contract.
Public Class IniFile

    Private ReadOnly m_sections As New Dictionary(Of String, IniSection)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly m_order As New List(Of IniSection)

    Public Sub New()
    End Sub

    ' The parsed sections. Exposed as the non-generic ICollection (which the internal
    ' section type satisfies) so no Public member leaks the Friend IniSection type;
    ' the only MonkMode caller reads Sections.Count (the B7/C1b structural-usability
    ' gate: a primary with fewer than the expected [Section] blocks is treated as
    ' corrupt and recovered, never lifted).
    Public ReadOnly Property Sections As ICollection
        Get
            Return m_sections.Values
        End Get
    End Property

    ' Create the named section, or return the existing one if present (idempotent).
    ' Returned as Object so a Public method never exposes an internal type across the
    ' assembly boundary; every MonkMode call site discards the result, and it stays a
    ' valid statement.
    Public Function AddSection(ByVal sSection As String) As Object
        Return GetOrCreateSection(sSection)
    End Function

    ' The value at [sSection] sKey, or "" if the section or key is absent. Never
    ' returns Nothing (the readers' "= " emptiness checks and callers depend on this).
    Public Function GetKeyValue(ByVal sSection As String, ByVal sKey As String) As String
        Dim sec As IniSection = Nothing
        If sSection IsNot Nothing AndAlso sKey IsNot Nothing AndAlso m_sections.TryGetValue(sSection, sec) Then
            Return sec.GetValue(sKey)
        End If
        Return ""
    End Function

    ' Set [sSection] sKey = sValue, creating the section and/or key as needed. The
    ' value is stored verbatim. Returns True (the historical signature; callers
    ' discard it).
    Public Function SetKeyValue(ByVal sSection As String, ByVal sKey As String, ByVal sValue As String) As Boolean
        If sSection Is Nothing OrElse sKey Is Nothing Then
            Return False
        End If
        GetOrCreateSection(sSection).SetValue(sKey, If(sValue, ""))
        Return True
    End Function

    ' Load and parse an ini file from disk. When bMerge is False (the default) the
    ' current contents are cleared first (a fresh load); when True the file's keys
    ' are overlaid on the existing model (last write wins). A missing/Nothing path
    ' is a no-op - it never throws (call sites Load paths that may not exist yet).
    Public Sub Load(ByVal sFileName As String, Optional ByVal bMerge As Boolean = False)
        If Not bMerge Then
            m_sections.Clear()
            m_order.Clear()
        End If
        If sFileName Is Nothing OrElse Not File.Exists(sFileName) Then
            Return
        End If

        Dim current As IniSection = Nothing
        ' Open share-all (ReadWrite + Delete) so a concurrent atomic Save (temp +
        ' rename) by another MonkMode process - e.g. the service advancing HighWater
        ' while the notifier reads the same ini - can replace this file without a
        ' sharing violation on either side. StreamReader defaults to UTF-8 with BOM
        ' detection, matching Save's File.WriteAllText, so unicode round-trips.
        Using fs As New FileStream(sFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
            Using sr As New StreamReader(fs)
                Dim line As String = sr.ReadLine()
                While line IsNot Nothing                       ' ReadLine strips \r\n, \n or \r
                    Dim trimmed As String = line.Trim()
                    If trimmed.Length = 0 Then
                        ' blank line - skip
                    ElseIf trimmed(0) = ";"c OrElse trimmed(0) = "#"c Then
                        ' comment line - skip
                    ElseIf trimmed(0) = "["c AndAlso trimmed(trimmed.Length - 1) = "]"c Then
                        Dim name As String = trimmed.Substring(1, trimmed.Length - 2).Trim()
                        current = GetOrCreateSection(name)      ' section header
                    Else
                        Dim eq As Integer = line.IndexOf("="c)
                        ' A line with no '=' is malformed (tolerate); a key before any
                        ' section header has nowhere to go (skip). Neither throws.
                        If eq > 0 AndAlso current IsNot Nothing Then
                            Dim key As String = line.Substring(0, eq).Trim()
                            If key.Length > 0 Then
                                ' Value = everything after the FIRST '=', verbatim (no
                                ' trim, no unquote): round-trip fidelity for the MAC.
                                current.SetValue(key, line.Substring(eq + 1))
                            End If
                        End If
                    End If
                    line = sr.ReadLine()
                End While
            End Using
        End Using
    End Sub

    ' Serialise the current sections/keys to disk. Atomic: write a unique temp
    ' sibling then rename it over the target (MoveFileEx REPLACE_EXISTING - atomic
    ' on NTFS), so a crash mid-write or a concurrent reader can never observe a
    ' half-written or blank ini. The temp is always cleaned up.
    Public Sub Save(ByVal sFileName As String)
        Dim sb As New StringBuilder()
        Dim nl As String = Environment.NewLine
        For Each sec As IniSection In m_order
            sb.Append("["c).Append(sec.Name).Append("]"c).Append(nl)
            For Each key As String In sec.Keys
                sb.Append(key).Append("="c).Append(sec.GetValue(key)).Append(nl)
            Next
        Next

        Dim tmp As String = sFileName & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(tmp, sb.ToString())
            File.Move(tmp, sFileName, True)
        Finally
            If File.Exists(tmp) Then
                Try
                    File.Delete(tmp)
                Catch
                End Try
            End If
        End Try
    End Sub

    ' Remove the named section and every key in it; True if one was there, False (a
    ' no-op) if not. Case-insensitive like every other lookup. Both m_sections AND
    ' m_order must drop it together - m_order is what Save re-emits from and
    ' m_sections is what GetKeyValue/AddSection look in, so dropping one only would
    ' leave Save writing a section no reader can see (or the reverse). The removal is
    ' by OBJECT reference, since List(Of IniSection).Remove has no case-insensitive
    ' by-name overload. Used to RETIRE a slot: a finished [SlotN] is deleted from the
    ' file outright rather than flagged, so no stale state can be resurrected.
    Public Function RemoveSection(ByVal name As String) As Boolean
        Dim key As String = If(name, "")
        Dim sec As IniSection = Nothing
        If Not m_sections.TryGetValue(key, sec) Then
            Return False
        End If
        m_sections.Remove(key)
        m_order.Remove(sec)
        Return True
    End Function

    Private Function GetOrCreateSection(ByVal name As String) As IniSection
        Dim key As String = If(name, "")
        Dim sec As IniSection = Nothing
        If m_sections.TryGetValue(key, sec) Then
            Return sec
        End If
        sec = New IniSection(key)
        m_sections(key) = sec
        m_order.Add(sec)
        Return sec
    End Function

End Class
