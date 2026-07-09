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

'    MonkMode - ServiceSecurity (B6: service-deletion resistance)
'
'    The PURE, unit-testable half of B6 - the SDDL string surgery that adds /
'    removes a single DELETE-deny ACE on the MONKMODE service object. The B6
'    analogue of the B2 strip/repair gates and the B3 SafeBoot predicate: the
'    tamper-critical logic lives here as Friend Shared functions pinned by
'    xunit, while the live SD I/O (QueryServiceObjectSecurity /
'    SetServiceObjectSecurity in ServiceTools.vb) is the smoke-tested seam.
'
'    THE BRICK-SAFE DESIGN (read before changing ANY constant here):
'      We deny `sc delete MONKMODE` by adding ONE deny ACE - Deny, right SD
'      (=DELETE) ONLY, principal BA (Built-in Administrators):  (D;;SD;;;BA)
'      We deliberately do NOT deny WRITE_DAC (WD as a right) or WRITE_OWNER (WO),
'      and never deny anything to SY (LocalSystem) or to Everyone. Rationale:
'        - the LocalSystem service (SY, NOT a member of BA) can always rewrite
'          the DACL to restore it (per-tick re-assert), and
'        - the elevated admin CLI (BA, but it still holds WRITE_DAC because we
'          never deny WRITE_DAC) can ALSO always rewrite the DACL.
'      So both legitimate owners can always lift the deny - the service is never
'      bricked. Denying WRITE_DAC instead would lock even the owner out of the
'      DACL: THAT is the brick. We deny DELETE only; a casual re-ACL by the user
'      is undone by the next per-tick re-assert, and at genuine expiry stopMe()
'      removes the ACE so an expired block leaves a removable service.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict On

Namespace Global.MonkMode

    Friend Module ServiceSecurity

        ' --- THE deny ACE (single source of truth; pinned by a drift test, like
        ' the B1 recovery policy and the B3 SafeBoot consts). Changing any token
        ' here changes what we deny and to whom - a typo could either fail to
        ' resist sc-delete (too weak) or, far worse, deny a right that bricks the
        ' service (too broad), so the suite fails loudly on any drift. ---

        ' Ace type: Deny.
        Friend Const DenyAceType As String = "D"
        ' The ONLY right denied: SD = DELETE. NEVER add WD (WRITE_DAC) or WO
        ' (WRITE_OWNER) here - that would brick the service (see the header).
        Friend Const DeleteRight As String = "SD"
        ' Principal: BA = Built-in Administrators. The service runs as SY (not a
        ' BA member) and so is unaffected; the admin CLI is BA but keeps
        ' WRITE_DAC, so it can still rewrite the DACL to restore.
        Friend Const AdministratorsSid As String = "BA"

        ' The exact ACE string inserted/removed:  (D;;SD;;;BA)
        ' SDDL ACE = (ace_type;ace_flags;rights;object_guid;inherit_object_guid;sid)
        ' - no flags, no object/inherit guids (not object-specific).
        Friend ReadOnly Property DenyDeleteAce As String
            Get
                Return "(" & DenyAceType & ";;" & DeleteRight & ";;;" & AdministratorsSid & ")"
            End Get
        End Property

        ' True if the SDDL's DACL already carries the deny-DELETE-for-BA ACE, so
        ' a re-assert can be a no-op when the registration is intact (no churn -
        ' mirrors RepairHostsBlock returning Nothing on an intact block, and the
        ' B3 read-only probe). Match is on the exact ACE token within the D:
        ' (DACL) portion; case-insensitive because SDDL ACE strings are
        ' conventionally upper-case but we never want a lower-cased copy to read
        ' as "absent" and trigger a needless rewrite. SACL (S:) is irrelevant
        ' here and never inspected.
        Friend Function SddlHasDenyDelete(ByVal sddl As String) As Boolean
            If String.IsNullOrEmpty(sddl) Then Return False
            Dim dacl As String = DaclPortion(sddl)
            If dacl Is Nothing Then Return False
            Return dacl.IndexOf(DenyDeleteAce, StringComparison.OrdinalIgnoreCase) >= 0
        End Function

        ' Insert the deny-DELETE ACE as the FIRST ACE of the D: DACL. Deny ACEs
        ' must precede allow ACEs in a canonical DACL, so it goes immediately
        ' after the "D:" prefix and any DACL flags (e.g. "PAI", "AI", "P") and
        ' before the first existing "(...)". IDEMPOTENT: if the ACE is already
        ' present the SDDL is returned unchanged (adding twice == once). If the
        ' SDDL has no D: section at all (unusual for a service, but possible),
        ' the input is returned unchanged - we never fabricate a DACL from
        ' nothing (that could drop the existing protections). The inverse is
        ' RemoveDenyDeleteAce; RemoveDenyDeleteAce(AddDenyDeleteAce(x)) == x for
        ' any x without the ACE is pinned by a test (the teardown-undo proof).
        Friend Function AddDenyDeleteAce(ByVal sddl As String) As String
            If sddl Is Nothing Then Return Nothing
            If SddlHasDenyDelete(sddl) Then Return sddl

            Dim daclStart As Integer = DaclSectionIndex(sddl)
            If daclStart < 0 Then Return sddl ' no D: section - leave untouched

            ' Position just past "D:" and any flag characters (everything up to
            ' the first '(' or the start of the next section S:/O:/G:). The deny
            ' ACE is inserted there so it leads the ACE list (canonical order).
            Dim insertAt As Integer = AceListStart(sddl, daclStart)
            Return sddl.Substring(0, insertAt) & DenyDeleteAce & sddl.Substring(insertAt)
        End Function

        ' The exact inverse of AddDenyDeleteAce: remove the deny-DELETE-for-BA
        ' ACE(s) wherever they sit in the DACL, leaving everything else
        ' byte-for-byte intact. Removes EVERY matching occurrence, not just the
        ' first: AddDenyDeleteAce never stacks a duplicate, but an attacker can
        ' hand-add a second (D;;SD;;;BA) via `sc sdset` mid-block, and a single
        ' leftover would keep the object DELETE-denied so the `unblock --force`
        ' escape hatch / expiry teardown could no longer delete the service.
        ' Looping to a fixed point (each pass strips one occurrence, so the SDDL
        ' strictly shrinks and the loop always terminates) guarantees teardown
        ' leaves a removable service. Case-insensitive match (a lower-cased
        ' hand-edited copy is still removed); only the D: (DACL) portion is
        ' touched, never a SACL copy of the token. Returns the input unchanged
        ' when no ACE is present, so RemoveDenyDeleteAce(x) == x for any x without
        ' the ACE - and therefore RemoveDenyDeleteAce(AddDenyDeleteAce(x)) == x
        ' (the brick-safety / teardown-undo proof).
        Friend Function RemoveDenyDeleteAce(ByVal sddl As String) As String
            If sddl Is Nothing Then Return Nothing
            Dim idx As Integer = IndexOfDenyAce(sddl)
            Do While idx >= 0
                sddl = sddl.Substring(0, idx) & sddl.Substring(idx + DenyDeleteAce.Length)
                idx = IndexOfDenyAce(sddl)
            Loop
            Return sddl
        End Function

        ' ---- SDDL parsing helpers (private) ----
        ' An SDDL string is a concatenation of O:<owner> G:<group> D:<dacl>
        ' S:<sacl> sections in any order. We only ever touch the D: (DACL)
        ' section; the owner/group/SACL are passed through untouched.

        ' Index in `sddl` of the "D:" that introduces the DACL, or -1. SDDL is a
        ' run of O:/G:/D:/S: sections; the owner and group that may precede the
        ' DACL hold SIDs only (2-letter aliases like BA/SY/WD or "S-1-..."
        ' numeric strings), none of which contains the literal substring "D:".
        ' So the FIRST "D:" is always the DACL introducer - a plain scan is
        ' correct (and an owner/group "WD" SID is "WD", never "D:").
        Private Function DaclSectionIndex(ByVal sddl As String) As Integer
            Return sddl.IndexOf("D:", StringComparison.Ordinal)
        End Function

        ' Given the index of the DACL's "D:", return the index at which the ACE
        ' list begins - i.e. just past "D:" and any DACL flag string (letters
        ' like P, A, I making up PAI/AI/P/NO_ACCESS_CONTROL etc.). The ACE list
        ' starts at the first '(' after the flags; if there is no '(' (an empty
        ' or flags-only DACL) it is wherever the section ends (the next section
        ' letter+':' or end of string), so a leading ACE can still be inserted.
        Private Function AceListStart(ByVal sddl As String, ByVal daclStart As Integer) As Integer
            Dim pos As Integer = daclStart + 2 ' skip "D:"
            ' The ACE list opens at the first '('. Flags (if any) sit between
            ' "D:" and that '(' and contain no '(' themselves, so the first '('
            ' from `pos` is exactly the start of the ACE list.
            Dim paren As Integer = sddl.IndexOf("("c, pos)
            If paren >= 0 Then Return paren
            ' No ACEs at all: insert at the end of the DACL section, which is the
            ' next section letter+':' (S:/O:/G:) or the end of the string.
            Return SectionEnd(sddl, pos)
        End Function

        ' The end of the current section that starts at/after `from`: the index
        ' of the next "S:"/"O:"/"G:" section introducer, or the string length.
        ' Used only for the (rare) flags-only/empty DACL case.
        Private Function SectionEnd(ByVal sddl As String, ByVal from As Integer) As Integer
            Dim ends As Integer = sddl.Length
            For Each letter As String In New String() {"S:", "O:", "G:"}
                Dim i As Integer = sddl.IndexOf(letter, from, StringComparison.Ordinal)
                If i >= 0 AndAlso i < ends Then ends = i
            Next
            Return ends
        End Function

        ' The substring of `sddl` that is the DACL section's body (from just past
        ' "D:" to the next section or end), or Nothing if there is no D: section.
        ' Used by SddlHasDenyDelete so a deny ACE that somehow appeared in the
        ' SACL (S:) is never mistaken for our DACL deny.
        Private Function DaclPortion(ByVal sddl As String) As String
            Dim daclStart As Integer = DaclSectionIndex(sddl)
            If daclStart < 0 Then Return Nothing
            Dim bodyStart As Integer = daclStart + 2
            Dim bodyEnd As Integer = SectionEnd(sddl, bodyStart)
            Return sddl.Substring(bodyStart, bodyEnd - bodyStart)
        End Function

        ' Index in `sddl` of the deny-DELETE ACE token (case-insensitive), but
        ' only when it lies inside the DACL section - so RemoveDenyDeleteAce
        ' never strips an identical token out of a SACL. -1 if absent or no DACL.
        Private Function IndexOfDenyAce(ByVal sddl As String) As Integer
            Dim daclStart As Integer = DaclSectionIndex(sddl)
            If daclStart < 0 Then Return -1
            Dim bodyStart As Integer = daclStart + 2
            Dim bodyEnd As Integer = SectionEnd(sddl, bodyStart)
            Dim rel As Integer = sddl.IndexOf(DenyDeleteAce, bodyStart, bodyEnd - bodyStart, StringComparison.OrdinalIgnoreCase)
            Return rel
        End Function

    End Module

End Namespace
