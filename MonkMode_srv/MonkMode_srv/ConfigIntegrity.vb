'    MonkMode - ConfigIntegrity (B7: tamper-evident config)
'
'    An HMAC-SHA256 "integrity" MAC over the decrypted config values, on top of
'    the (documented-weak, B7-owned) Simple3Des layer. The threat it closes:
'    an attacker who recovers the hardcoded 3DES key can re-encrypt [Time] Until
'    to "now" and write it into monkmode_settings.ini to end a block early. With
'    the MAC, that edit no longer verifies, and the readers (service/guardian/
'    notifier) treat a MAC-invalid config as STILL ACTIVE (fail CLOSED) - so the
'    block never auto-lifts until the config is legitimately re-stamped, exactly
'    the tamper-resistant direction every other gate takes.
'
'    The HMAC key is a per-block random 32 bytes, DPAPI-protected at machine
'    scope ([Integrity] Key) so it is not a second hardcoded secret an attacker
'    can lift from the binaries; the MAC itself is [Integrity] Mac. Both keys are
'    EXCLUDED from the canonical (you can't MAC the MAC).
'
'    This file is byte-for-byte identical across all four projects (CLI,
'    service, guardian, notifier), like the Simple3Des copies - the unit tests
'    pin that 4-copy parity. Only the RootNamespace differs (set per project).
'
'    Split of concerns:
'      - BuildCanonical / ComputeConfigMac / ConfigMacIsValid are PURE
'        (filesystem/DPAPI-free) and fully unit-tested; ConfigMacIsValid is the
'        fail-closed gate and NEVER throws (False on any bad/blank/non-Base64
'        stored MAC).
'      - NewRandomKey / ProtectKey / UnprotectKey are the DPAPI live seam (not
'        unit-tested - they hit the real DPAPI; smoke-tested later). A DPAPI
'        failure on a reader must read as MAC-INVALID -> fail closed, never as
'        "lift", so UnprotectKey returns Nothing on failure and the caller maps
'        a Nothing key to "MAC invalid".
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Security.Cryptography
Imports System.Text

Friend Module ConfigIntegrity

    ' The current config schema version - the FIRST line of the canonical below,
    ' so it is MAC-covered. Bumping it makes every config stamped under an OLDER
    ' schema fail verification on the new binaries; because the readers fail
    ' CLOSED on an invalid MAC, a pre-upgrade block then FREEZES rather than being
    ' silently accepted or auto-lifted (R9 forward-migration freeze). It is a
    ' COMPILE-TIME constant every party bakes in from its byte-identical copy,
    ' never read from the ini: a config-DECLARED version would let a doctored old
    ' config re-declare its own version and re-validate, so the version an evader
    ' is measured against must be the CODE's, not the file's. Operational rule
    ' this implies: arm blocks AFTER upgrading the binaries, not ACROSS an upgrade
    ' (an in-flight block armed by the previous schema correctly freezes until it
    ' is re-armed). History: v1 (pre-B4) -> v2 (B4: added HighWater) -> v3
    ' (Section C accountability core) -> v4 (C2b: added CoolOffUntil, the
    ' cooling-off deadline) -> v5 (C3b: added the [Partner] Salt/Hash/UnlockedAt
    ' accountability-code fields) -> v6 (C4: added the [Commit] Committed flag) ->
    ' v7 (C5b: added the [Schedule] Spec + ActiveUntil scheduled-window fields) ->
    ' v8 (C6b: added the [CoolOff] Duration configured-cooling-off-duration field) ->
    ' v9 (D2c: added the [Process] AllSession all-session-app-kill flag).
    ' The four copies MUST share this value or the parties would stamp/verify
    ' different tags and every block would freeze;
    ' AllFourCopies_ShareTheSameSchemaVersion pins that.
    Friend Const CurrentSchemaVersion As String = "v9"

    ' The canonical string the MAC is computed over: the schema version tag plus
    ' one Key=Value line per protected field, vbLf-separated, in a FIXED order.
    ' Every party (CLI writer, service/guardian/notifier readers) builds this from
    ' the DECRYPTED plaintext values and supplies CurrentSchemaVersion for the tag,
    ' so the input is byte-identical regardless of the ciphertext or who wrote it.
    ' "null"/"" pass through as-is - the point is a stable, reproducible input, not
    ' interpretation. [Integrity] Key and [Integrity] Mac are deliberately NOT part
    ' of this (you can't MAC the MAC).
    '
    ' B4 (clock-rollback hardening): the canonical includes HighWater - the
    ' monotonic high-water mark (an en-CA LOCAL datetime, same format as Until)
    ' that the service advances at most one tick at a time and never on a clock
    ' jump. It MUST be MAC-covered so an attacker who recovered the 3DES key can't
    ' forge a HighWater past Until to fake an expiry; covering it here is what
    ' couples the value to the MAC. HighWater sits right after Until (the two are
    ' paired: Until is the target, HighWater the trusted clock measured against it).
    '
    ' C2b (cooling-off): the canonical includes CoolOffUntil - the cooling-off
    ' deadline (an en-CA LOCAL datetime like Until/HighWater; "" = no cooling-off
    ' pending). It MUST be MAC-covered or a raw ini edit could forge the deadline
    ' into the past for an instant lift. The SERVICE is its sole writer
    ' (HighWater_at_request + max(duration, floor)); the CLI's request channel is
    ' a presence-only trigger file with zero timing authority (R2). CoolOffUntil
    ' sits directly after HighWater so the [Time] datetimes stay grouped: Until =
    ' block target, HighWater = trusted clock, CoolOffUntil = early-exit target.
    '
    ' C3b (partner code): the canonical also includes the [Partner] accountability-
    ' code fields - PartnerSalt (Base64 salt), PartnerHash (Base64 salted one-way
    ' KDF of the code) and PartnerUnlockedAt (the en-CA datetime the service stamps
    ' on a verified code, "" = not unlocked, the exit flag). All three are stored
    ' PLAINTEXT-in-ini (they are not reversible secrets; the MAC is what protects
    ' them, exactly like plaintext CustomSites) and MUST be MAC-covered: a tampered
    ' or swapped-in Hash then fails the MAC -> macValid=False -> NO code is valid ->
    ' the block FREEZES (R6, automatic once they are in the canonical - never
    ' fail-open), and a raw-edited UnlockedAt likewise fails the MAC, so a non-empty
    ' UnlockedAt is only ever valid UNDER a valid MAC, i.e. only if the SERVICE wrote
    ' it after verifying a correct code. They form a trailing [Partner] group after
    ' the [CurrentTime] Now line (the [Time] datetimes stay grouped as before).
    '
    ' The version is a caller-supplied PARAMETER (not a hardcoded literal) so it
    ' rides the same wrapper-supplied contract as every other field: each
    ' CanonicalFromIni wrapper passes CurrentSchemaVersion exactly as it passes the
    ' decrypted Until/HighWater/ProcessList/Now - the uniform shape every Section-C
    ' field will slot into. A bump is then one edit to CurrentSchemaVersion, pinned
    ' loudly by the 4-copy parity + format tests.
    ' C4 (commit blocks): the canonical also includes Committed - the MAC-covered
    ' policy flag ("yes"=committed, "no"/absent=not) that disables self-serve
    ' cooling-off (the block becomes CODE-ONLY exit) while the partner code + expiry
    ' still lift it. It MUST be MAC-covered or a raw edit could clear it and re-enable
    ' the easy cooling-off exit; instead, flipping it fails the MAC -> macValid=False
    ' -> the whole block FREEZES (cooling-off Ignored anyway), so an attacker can
    ' never un-commit. Set once at arm by the CLI, never mutated during the block.
    '
    ' C3b/C4/C5b/C6b: partnerSalt/partnerHash/partnerUnlockedAt/committed/scheduleSpec/
    ' scheduleActiveUntil/coolOffDuration are appended at the END of the PARAMETER list
    ' (after coolOffUntil) and their LINES trail after Now= (the [Partner]/[Commit]
    ' group, the [Schedule] group, then the [CoolOff] Duration line last) - mind that
    ' the parameter order (until, processList, customSites, now, highWater, coolOffUntil,
    ' partnerSalt, partnerHash, partnerUnlockedAt, committed, scheduleSpec,
    ' scheduleActiveUntil, coolOffDuration, allSessionKill) is NOT the line order; every wrapper threads
    ' them positionally, so the four copies must stay byte-identical (the parity tests
    ' fail loudly on drift).
    '
    ' C5b (schedules): the canonical also includes the two [Schedule] fields -
    ' ScheduleSpec (the plaintext, MAC-covered recurring-window rule + its site/app
    ' lists, stored as-stored like CustomSites/[Partner] - a blocklist is not a secret;
    ' the MAC is its protection) and ScheduleActiveUntil (an en-CA LOCAL datetime,
    ' DECRYPTED like CoolOffUntil; "" = no scheduled window currently open). Both MUST
    ' be MAC-covered: a tampered Spec (adding/removing windows) or a forged
    ' ScheduleActiveUntil (a past value to end an open window early) then fails the MAC
    ' -> macValid=False -> the whole block FREEZES (B7, automatic once they are in the
    ' canonical), and the SERVICE is the sole writer of ScheduleActiveUntil (the
    ' window->duration conversion, HighWater-anchored) exactly as it is of CoolOffUntil.
    ' They form a trailing [Schedule] group after Committed=.
    '
    ' C6b (configurable cooling-off duration): the canonical also includes CoolOffDuration
    ' - the CLI-configured cooling-off wait in SECONDS (plaintext-as-stored like Committed/
    ' [Partner], NOT encrypted - a duration is not a secret; the MAC is its protection).
    ' The CLI writes it at arm time BEFORE the fresh MAC, so it is MAC-covered from birth,
    ' and the SERVICE reads it to compute CoolOffUntil = HighWater_at_request +
    ' max(configured, floor). It MUST be MAC-covered: a raw edit to shorten it fails the
    ' MAC -> freeze (and even absent the freeze the compile-time floor clamps it up, so a
    ' tampered/0 value can only ever EXTEND the wait, never shorten below the floor).
    ' Absent/blank/unparseable -> the floor. It trails after ScheduleActiveUntil (the
    ' append-at-end rule every bump follows, keeping each schema bump a uniform edit).
    '
    ' D2c (all-session app-kill): the canonical also includes AllSessionKill - the [Process]
    ' AllSession policy flag ("yes" = the LocalSystem service kills blocked apps in EVERY session,
    ' not just session 0; absent/"no" = today's session-0-only service kill, the notifier still
    ' covering its own interactive session). Stored PLAINTEXT-as-stored (a boolean policy is not a
    ' secret; the MAC is its protection), like Committed. It MUST be MAC-covered or a raw edit could
    ' flip an armed all-session block back to session-0-only to run a blocked app in a second
    ' logged-in session; instead flipping it fails the MAC -> freeze. The service reads it UN-gated
    ' by macValid (a widen-only union that can never REMOVE a kill, matching the schedule app-kill
    ' union), so a tampered "yes" only ever ADDS kills. It trails LAST, after CoolOffDuration.
    Friend Function BuildCanonical(ByVal schemaVersion As String, ByVal until As String, ByVal processList As String, ByVal customSites As String, ByVal now As String, ByVal highWater As String, ByVal coolOffUntil As String, ByVal partnerSalt As String, ByVal partnerHash As String, ByVal partnerUnlockedAt As String, ByVal committed As String, ByVal scheduleSpec As String, ByVal scheduleActiveUntil As String, ByVal coolOffDuration As String, ByVal allSessionKill As String) As String
        Return schemaVersion & vbLf &
               "Until=" & until & vbLf &
               "HighWater=" & highWater & vbLf &
               "CoolOffUntil=" & coolOffUntil & vbLf &
               "ProcessList=" & processList & vbLf &
               "CustomSites=" & customSites & vbLf &
               "Now=" & now & vbLf &
               "PartnerSalt=" & partnerSalt & vbLf &
               "PartnerHash=" & partnerHash & vbLf &
               "PartnerUnlockedAt=" & partnerUnlockedAt & vbLf &
               "Committed=" & committed & vbLf &
               "ScheduleSpec=" & scheduleSpec & vbLf &
               "ScheduleActiveUntil=" & scheduleActiveUntil & vbLf &
               "CoolOffDuration=" & coolOffDuration & vbLf &
               "AllSessionKill=" & allSessionKill & vbLf
    End Function

    ' HMAC-SHA256 of the canonical (Unicode bytes), Base64-encoded. The key is
    ' the raw 32-byte block key (post-DPAPI-unprotect on a reader).
    Friend Function ComputeConfigMac(ByVal canonical As String, ByVal key As Byte()) As String
        Using h As New HMACSHA256(key)
            Dim mac() As Byte = h.ComputeHash(Encoding.Unicode.GetBytes(canonical))
            Return Convert.ToBase64String(mac)
        End Using
    End Function

    ' The fail-closed verification gate. Recompute the MAC over canonical and
    ' compare it to storedMacB64 in constant time (CryptographicOperations.
    ' FixedTimeEquals over the raw bytes). Returns False - never throws - on a
    ' null/blank/non-Base64 stored MAC, so a tampered or absent [Integrity] Mac
    ' reads as "invalid" and the caller keeps the block standing.
    Friend Function ConfigMacIsValid(ByVal canonical As String, ByVal storedMacB64 As String, ByVal key As Byte()) As Boolean
        If String.IsNullOrWhiteSpace(storedMacB64) Then Return False
        Dim storedBytes() As Byte
        Try
            storedBytes = Convert.FromBase64String(storedMacB64)
        Catch ef As System.FormatException
            Return False
        End Try
        Try
            Using h As New HMACSHA256(key)
                Dim expected() As Byte = h.ComputeHash(Encoding.Unicode.GetBytes(canonical))
                Return CryptographicOperations.FixedTimeEquals(expected, storedBytes)
            End Using
        Catch ex As Exception
            ' A null key (or any other crypto hiccup) reads as MAC-invalid, not
            ' as a lift - fail closed.
            Return False
        End Try
    End Function

    ' ==== C3b: partner accountability code (salted one-way KDF verifier) ====
    '
    ' The partner code is the FAST early exit (cooling-off is the slow one): at arm
    ' the CLI generates a random code, prints it ONCE for the user to relay to their
    ' accountability partner, and stores ONLY a salted one-way hash (never the
    ' plaintext). To get out early the user submits a candidate; the SERVICE alone
    ' (R1) derives KDF(salt, candidate), constant-time-compares it to the stored
    ' hash and, on a match, sets the MAC-covered [Partner] UnlockedAt exit flag.
    '
    ' These functions are PURE (no DPAPI, no filesystem) and fully unit-tested; they
    ' live here - the byte-identical shared crypto module - so the CLI (generate +
    ' hash) and the service (verify) call the SAME code and the 4-copy parity tests
    ' pin them. The security floor is offline brute-force = code entropy (PD2) x KDF
    ' cost (PD3): the hash is readable on the attacker's own disk, so a slow KDF is
    ' load-bearing, not hygiene.

    ' PD2 (code entropy): 10 chars from the 32-symbol Crockford base32 alphabet
    ' (~2^50), grouped XXXXX-XXXXX for relay. The alphabet excludes I, L, O, U so a
    ' phone-relayed code can't be misheard into an ambiguous char; NormalisePartner-
    ' Code additionally folds any I/L->1, O->0 at BOTH generation and verification,
    ' so a mis-transcribed relay still verifies. Pinned by a unit test (a retune is
    ' one loud edit, like MinCoolOffFloorSeconds / HighWaterJumpCeilingSeconds).
    Friend Const PartnerCodeLength As Integer = 10
    Friend Const PartnerCodeAlphabet As String = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    ' PD3 (KDF cost + salt): PBKDF2-HMAC-SHA256 at 600,000 iterations over a 16-byte
    ' per-code random salt, producing a 32-byte hash. Built into .NET 8 (no new
    ' dependency). The per-code salt makes each block's offline search independent
    ' and rainbow-tables useless. Pinned by a unit test.
    Friend Const PartnerKdfIterations As Integer = 600000
    Friend Const PartnerSaltBytes As Integer = 16
    Friend Const PartnerHashBytes As Integer = 32

    ' A fresh per-code random salt (PD3). CSPRNG.
    Friend Function NewPartnerSalt() As Byte()
        Return RandomNumberGenerator.GetBytes(PartnerSaltBytes)
    End Function

    ' Generate a random partner code (PD2): PartnerCodeLength chars drawn uniformly
    ' (RandomNumberGenerator.GetInt32 is unbiased over the 32-char alphabet) and
    ' grouped XXXXX-XXXXX for phone relay. The returned DISPLAY form is what the CLI
    ' shows once and hashes (via ComputePartnerHash, which normalises first), so the
    ' separator never affects the stored hash.
    Friend Function GeneratePartnerCode() As String
        Dim sb As New StringBuilder(PartnerCodeLength + 1)
        For i As Integer = 0 To PartnerCodeLength - 1
            sb.Append(PartnerCodeAlphabet(RandomNumberGenerator.GetInt32(PartnerCodeAlphabet.Length)))
            ' Group into two halves for readability/relay (XXXXX-XXXXX for len 10).
            If i = (PartnerCodeLength \ 2) - 1 AndAlso i < PartnerCodeLength - 1 Then sb.Append("-"c)
        Next
        Return sb.ToString()
    End Function

    ' Deterministic normalisation applied IDENTICALLY at generation-hash and verify,
    ' so a phone-relayed "xxxxx-xxxxx" (lower case, spaces, or a mis-transcribed
    ' I/L/O) verifies against the stored hash: uppercase, strip every non-alphanumeric
    ' char (the group separator, spaces), and apply Crockford's ambiguous-char
    ' canonicalisation (I,L -> 1; O -> 0). Pure. A round-trip test pins that a
    ' generated code, and its lower-case / spaced / O-for-0 relay variants, all
    ' normalise to the same string.
    Friend Function NormalisePartnerCode(ByVal code As String) As String
        If code Is Nothing Then Return ""
        Dim sb As New StringBuilder(code.Length)
        For Each ch As Char In code.ToUpperInvariant()
            Select Case ch
                Case "I"c, "L"c : sb.Append("1"c)          ' Crockford: I/L -> 1
                Case "O"c : sb.Append("0"c)                ' Crockford: O -> 0
                Case "0"c To "9"c, "A"c To "Z"c : sb.Append(ch)   ' keep alphanumerics
                Case Else                                  ' drop separators/whitespace
            End Select
        Next
        Return sb.ToString()
    End Function

    ' The raw KDF: PBKDF2-HMAC-SHA256 over the NORMALISED code (Unicode bytes, the
    ' house encoding) and the raw salt bytes, PartnerKdfIterations rounds, 32-byte
    ' output. Private so both ComputePartnerHash and PartnerCodeMatches derive bytes
    ' the same way (the only place the KDF is invoked).
    Private Function PartnerKdf(ByVal code As String, ByVal saltBytes As Byte()) As Byte()
        Return Rfc2898DeriveBytes.Pbkdf2(Encoding.Unicode.GetBytes(NormalisePartnerCode(code)),
                                         saltBytes, PartnerKdfIterations, HashAlgorithmName.SHA256, PartnerHashBytes)
    End Function

    ' The stored [Partner] Hash for a code + its salt (Base64 of the KDF output).
    ' Used by the CLI at arm time (it holds the raw salt from NewPartnerSalt).
    Friend Function ComputePartnerHash(ByVal saltBytes As Byte(), ByVal code As String) As String
        Return Convert.ToBase64String(PartnerKdf(code, saltBytes))
    End Function

    ' The fail-closed verify gate (pure, no DPAPI - the caller folds macValid). True
    ' ONLY when the candidate, KDF'd under the stored salt, constant-time-equals the
    ' stored hash. Fail-CLOSED on every axis: a blank candidate, a missing/blank
    ' verifier, an un-Base64 salt/hash, a wrong-length hash or any crypto throw ->
    ' NOT a match -> the block holds. A corrupted verifier can only ever WITHHOLD a
    ' lift, never grant one. Constant-time compare via CryptographicOperations.
    ' FixedTimeEquals (the house pattern, ConfigMacIsValid), over the raw hash bytes.
    Friend Function PartnerCodeMatches(ByVal candidate As String, ByVal saltB64 As String, ByVal storedHashB64 As String) As Boolean
        If String.IsNullOrWhiteSpace(candidate) Then Return False          ' blank never matches
        If String.IsNullOrEmpty(saltB64) OrElse String.IsNullOrEmpty(storedHashB64) Then Return False  ' no verifier set -> no code valid
        Try
            Dim saltBytes() As Byte = Convert.FromBase64String(saltB64)
            Dim storedHash() As Byte = Convert.FromBase64String(storedHashB64)
            Dim expected() As Byte = PartnerKdf(candidate, saltBytes)
            ' FixedTimeEquals returns False (never throws) on a length mismatch too,
            ' so a truncated/oversized stored hash reads as "not a match".
            Return CryptographicOperations.FixedTimeEquals(expected, storedHash)
        Catch ex As Exception
            Return False                                                   ' any parse/crypto hiccup -> not a match
        End Try
    End Function

    ' ---- DPAPI key management (live seam - NOT unit-tested) ----

    ' A fresh per-block HMAC key. 32 bytes of CSPRNG output.
    Friend Function NewRandomKey() As Byte()
        Return RandomNumberGenerator.GetBytes(32)
    End Function

    ' DPAPI-protect the raw key at MACHINE scope and return it Base64 (the
    ' [Integrity] Key value). Machine scope (decision locked) so the LocalSystem
    ' service, the SYSTEM guardian and the user-session notifier/CLI can all
    ' unprotect it. Returns Nothing on failure so the caller can degrade
    ' gracefully (a DPAPI failure must NOT abort arming the block).
    Friend Function ProtectKey(ByVal key As Byte()) As String
        Try
            Dim blob() As Byte = ProtectedData.Protect(key, Nothing, DataProtectionScope.LocalMachine)
            Return Convert.ToBase64String(blob)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ' Reverse of ProtectKey: Base64 protected blob -> raw key bytes. Returns
    ' Nothing on any failure (blank/non-Base64 blob, DPAPI denial, blob written
    ' by a different machine). The caller MUST map a Nothing key to "MAC
    ' invalid" -> fail closed (block stands), never to "lift".
    Friend Function UnprotectKey(ByVal protectedB64 As String) As Byte()
        If String.IsNullOrWhiteSpace(protectedB64) Then Return Nothing
        Try
            Dim blob() As Byte = Convert.FromBase64String(protectedB64)
            Return ProtectedData.Unprotect(blob, Nothing, DataProtectionScope.LocalMachine)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

End Module
