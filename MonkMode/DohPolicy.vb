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

'    MonkMode - DohPolicy (B5a: browser-DoH-off policy self-heal) - CLI COPY
'
'    The PURE, unit-testable half of B5a - the curated list of browser "Secure
'    DNS" (DNS-over-HTTPS) policy registry entries we force OFF, plus the probe
'    predicate (already-blocked?), the no-data-loss restore decision, and the
'    snapshot (de)serialiser. NO registry I/O lives here: the live probe / write /
'    restore is the smoke-tested seam on Service1 (AssertDohPolicy /
'    RemoveDohPolicy) and the CLI (Blocker), exactly like the B3 SafeBoot split
'    (SafeBootValueIsCorrect pure; AssertSafeBootRegistration live) and the B6
'    ServiceSecurity split.
'
'    This file is byte-for-byte identical to MonkMode_srv\MonkMode_srv\DohPolicy.vb
'    (the service copy), like the Simple3Des / ConfigIntegrity / ServiceSecurity
'    copies across the projects - only the RootNamespace differs (MonkMode here,
'    monkmode in the service). DohPolicyParityTests pins that 2-copy parity
'    element-for-element: the service WRITES + self-heals these keys and the
'    service RESTORES them at a genuine expiry, so a drift would mean the two
'    disagree on WHICH keys to touch (a stuck policy the hatch can't lift, or the
'    wrong value restored = data loss). The exes never reference each other (house
'    rule), so each side uses its own copy.
'
'    WHY B5a EXISTS: flipping on a browser's "Secure DNS" tunnels DNS over
'    HTTPS:443 and bypasses the hosts file entirely (the #1 real bypass). Setting
'    the enterprise policy value OFF disables that toggle. HONEST CEILING: these
'    vendor policy value NAMES change between browser versions and a portable /
'    policy-ignoring browser build still evades - this list is best-effort and
'    must be re-verified against current Edge/Chrome/Brave/Firefox policy docs at
'    build time. VPN/Tor and a non-browser DoH client are out of scope (B5b/B10).
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict On

Imports System.Globalization
Imports System.Text
Imports Microsoft.Win32

Namespace Global.MonkMode

    Friend Module DohPolicy

        ' One browser "Secure DNS" policy value we force to its blocked setting.
        ' Heterogeneous by vendor: the Chromium family (Edge/Chrome/Brave) take a
        ' REG_SZ "off"; Firefox takes two DWORDs (Enabled=0, Locked=1). SubKey is
        ' RELATIVE to HKLM. Kind + BlockedValue drive both the probe
        ' (ValueIsBlocked) and the live CreateSubKey/SetValue.
        Friend Structure DohPolicyEntry
            Public ReadOnly SubKey As String
            Public ReadOnly ValueName As String
            Public ReadOnly Kind As RegistryValueKind
            Public ReadOnly BlockedValue As Object

            Friend Sub New(ByVal subKey As String, ByVal valueName As String, ByVal kind As RegistryValueKind, ByVal blockedValue As Object)
                Me.SubKey = subKey
                Me.ValueName = valueName
                Me.Kind = kind
                Me.BlockedValue = blockedValue
            End Sub
        End Structure

        ' The curated policy list - the SINGLE SOURCE OF TRUTH (the parity test
        ' pins the CLI copy equal to this element-for-element, and
        ' DohPolicyConstantsTests pins every literal so a vendor-key rename is a
        ' LOUD build failure, not a silent disarm). All under
        ' HKLM\SOFTWARE\Policies\... (machine policy - the elevated CLI /
        ' LocalSystem service can write it). A fresh array each Get so a caller can
        ' never mutate the shared source.
        Friend ReadOnly Property Entries As DohPolicyEntry()
            Get
                Return New DohPolicyEntry() {
                    New DohPolicyEntry("SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", RegistryValueKind.String, "off"),
                    New DohPolicyEntry("SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", RegistryValueKind.String, "off"),
                    New DohPolicyEntry("SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", RegistryValueKind.String, "off"),
                    New DohPolicyEntry("SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", RegistryValueKind.DWord, 0),
                    New DohPolicyEntry("SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", RegistryValueKind.DWord, 1)
                }
            End Get
        End Property

        ' The probe predicate (mirror of B3's SafeBootValueIsCorrect): True => the
        ' value is ALREADY at its blocked setting, so the re-assert can skip the
        ' write (no churn, like the B3 read-only probe and RepairHostsBlock
        ' returning Nothing on an intact block). currentValue is what the registry
        ' read returned: a String for REG_SZ, a boxed Int32 for a DWORD, or Nothing
        ' if the value/key is absent. Absent => False (needs a write). REG_SZ
        ' compare is ORDINAL + case-SENSITIVE (like SafeBootValueIsCorrect, so
        ' "OFF" <> "off" reads as "needs rewrite"); DWORD compare is numeric.
        Friend Function ValueIsBlocked(ByVal entry As DohPolicyEntry, ByVal currentValue As Object) As Boolean
            If currentValue Is Nothing Then Return False
            If entry.Kind = RegistryValueKind.DWord Then
                Dim cur As Long, want As Long
                If Not TryToLong(currentValue, cur) Then Return False
                If Not TryToLong(entry.BlockedValue, want) Then Return False
                Return cur = want
            End If
            Dim curStr As String = TryCast(currentValue, String)
            Dim wantStr As String = TryCast(entry.BlockedValue, String)
            If curStr Is Nothing OrElse wantStr Is Nothing Then Return False
            Return String.Equals(curStr, wantStr, StringComparison.Ordinal)
        End Function

        ' The no-data-loss restore decision (the piece B3 never had to model - it
        ' blind-deleted its own dedicated leaf key; here the keys are SHARED vendor
        ' keys that may hold the user's OWN policies, so we restore at the VALUE
        ' level). Given the value snapshotted BEFORE the block set the policy
        ' (Nothing = the value was ABSENT), decide: restore the user's prior value,
        ' or delete our value. Pure => unit-tested by RestoreActionForTests. `entry`
        ' is passed for API symmetry with ValueIsBlocked (the caller already holds
        ' it) and so a future Kind-specific coercion has it to hand.
        Friend Function RestoreActionFor(ByVal entry As DohPolicyEntry, ByVal snapshotted As Object) As (delete As Boolean, value As Object)
            If snapshotted Is Nothing Then Return (delete:=True, value:=Nothing)
            Return (delete:=False, value:=snapshotted)
        End Function

        ' ---- snapshot (de)serialiser (pure; the file + registry I/O is the live
        ' seam). The CLI writes this at block start with each entry's PRIOR value
        ' (or absent) next to the exes as monkmode_doh.snapshot (mirror
        ' monkmode_hosts.block); RemoveDohPolicy (service stopMe + CLI escape hatch)
        ' reads it to restore. Tab-delimited, one line per entry, keyed by
        ' subkey+value so a reorder can't misalign; a missing snapshot degrades to
        ' "delete our keys" (the same graceful degradation B2 has without its
        ' snapshot). ----

        ' A header token so a stray/foreign file is not mistaken for a snapshot
        ' (its 2 fields are fewer than the 3 a data line needs, so it self-skips).
        Friend Const SnapshotHeader As String = "MonkModeDohSnapshot" & vbTab & "1"

        ' Serialise the prior values (array PARALLEL to Entries; element Nothing =
        ' absent) to the snapshot text. Values are written in their invariant
        ' string form ("off", "0", "1", or the user's prior value).
        Friend Function BuildSnapshot(ByVal priorValues As Object()) As String
            Dim ents As DohPolicyEntry() = Entries
            Dim sb As New StringBuilder()
            sb.Append(SnapshotHeader).Append(vbLf)
            For i As Integer = 0 To ents.Length - 1
                Dim entry As DohPolicyEntry = ents(i)
                Dim prior As Object = Nothing
                If priorValues IsNot Nothing AndAlso i < priorValues.Length Then prior = priorValues(i)
                sb.Append(entry.SubKey).Append(vbTab).Append(entry.ValueName).Append(vbTab)
                If prior Is Nothing Then
                    sb.Append("A"c).Append(vbTab).Append(vbLf)
                Else
                    sb.Append("P"c).Append(vbTab).Append(Convert.ToString(prior, CultureInfo.InvariantCulture)).Append(vbLf)
                End If
            Next
            Return sb.ToString()
        End Function

        ' Parse the snapshot text back to an array PARALLEL to Entries: each element
        ' is the prior value TYPED per the entry's Kind (Int32 for a DWORD, String
        ' for REG_SZ), or Nothing when the entry was absent / its line is missing /
        ' a DWORD line is corrupt (fail-safe: treat as absent => RestoreActionFor
        ' deletes our value rather than writing garbage back). Accepts CRLF or LF.
        Friend Function ParseSnapshot(ByVal text As String) As Object()
            Dim ents As DohPolicyEntry() = Entries
            Dim result(ents.Length - 1) As Object
            If String.IsNullOrEmpty(text) Then Return result

            Dim present As New Dictionary(Of String, String)(StringComparer.Ordinal)
            For Each line As String In text.Replace(vbCrLf, vbLf).Split(CChar(vbLf))
                If line.Length = 0 Then Continue For
                Dim parts() As String = line.Split(CChar(vbTab))
                If parts.Length < 3 Then Continue For                                  ' header / junk line
                If Not String.Equals(parts(2), "P", StringComparison.Ordinal) Then Continue For  ' "A" = absent
                Dim value As String = If(parts.Length >= 4, parts(3), "")
                present(parts(0) & vbTab & parts(1)) = value
            Next

            For i As Integer = 0 To ents.Length - 1
                Dim entry As DohPolicyEntry = ents(i)
                Dim raw As String = Nothing
                If Not present.TryGetValue(entry.SubKey & vbTab & entry.ValueName, raw) Then Continue For
                If entry.Kind = RegistryValueKind.DWord Then
                    Dim n As Integer
                    If Integer.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, n) Then result(i) = n
                Else
                    result(i) = raw
                End If
            Next
            Return result
        End Function

        ' Convert a registry-read Object (a boxed Int32, or a numeric String) to
        ' Long for the DWORD compare. False (never throws) on a non-numeric value.
        Private Function TryToLong(ByVal value As Object, ByRef result As Long) As Boolean
            Try
                result = Convert.ToInt64(value, CultureInfo.InvariantCulture)
                Return True
            Catch
                result = 0
                Return False
            End Try
        End Function

    End Module

End Namespace
